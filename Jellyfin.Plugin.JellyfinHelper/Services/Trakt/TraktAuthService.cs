using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt;

/// <summary>
///     Default <see cref="ITraktAuthService"/>: drives the Trakt OAuth device flow against api.trakt.tv using
///     the admin's shared client id/secret, and keeps per-user tokens fresh via the refresh grant.
/// </summary>
public sealed class TraktAuthService : ITraktAuthService
{
    private const string LogSource = "Trakt";

    // Refresh a little before the real expiry so a token does not die mid-request.
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITraktUserStore _store;
    private readonly ISecretProtector _secretProtector;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<TraktAuthService> _logger;
    private readonly Func<DateTime> _utcNow;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TraktAuthService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory (uses the hardened "Trakt" client).</param>
    /// <param name="store">The per-user token store.</param>
    /// <param name="secretProtector">Decrypts the configured client secret.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger. Never receives token values.</param>
    /// <param name="utcNow">Clock seam for deterministic expiry handling in tests; defaults to UTC now.</param>
    public TraktAuthService(
        IHttpClientFactory httpClientFactory,
        ITraktUserStore store,
        ISecretProtector secretProtector,
        IPluginLogService pluginLog,
        ILogger<TraktAuthService> logger,
        Func<DateTime>? utcNow = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _secretProtector = secretProtector ?? throw new ArgumentNullException(nameof(secretProtector));
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public async Task<TraktDeviceCodeResponse?> StartDeviceAuthAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        if (config is null || string.IsNullOrWhiteSpace(config.TraktClientId))
        {
            return null;
        }

        var body = JsonSerializer.Serialize(new { client_id = config.TraktClientId });
        using var request = BuildJsonRequest(HttpMethod.Post, "/oauth/device/code", body);
        try
        {
            using var response = await Send(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _pluginLog.LogWarning(LogSource, $"Device code request failed: {(int)response.StatusCode}.", logger: _logger);
                return null;
            }

            var json = await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TraktDeviceCodeResponse>(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ResponseTooLargeException)
        {
            _pluginLog.LogWarning(LogSource, "Device code request errored.", ex, _logger);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<TraktDevicePollStatus> PollDeviceAuthAsync(Guid userId, string deviceCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            return TraktDevicePollStatus.Error;
        }

        var config = GetConfig();
        var clientSecret = _secretProtector.Unprotect(config?.TraktClientSecret);
        if (config is null || string.IsNullOrWhiteSpace(config.TraktClientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return TraktDevicePollStatus.Error;
        }

        var body = JsonSerializer.Serialize(new
        {
            code = deviceCode,
            client_id = config.TraktClientId,
            client_secret = clientSecret,
        });

        using var request = BuildJsonRequest(HttpMethod.Post, "/oauth/device/token", body);
        try
        {
            using var response = await Send(request, cancellationToken).ConfigureAwait(false);

            // Trakt's documented device-token status codes map directly onto the poll outcome.
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var json = await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
                    var token = JsonSerializer.Deserialize<TraktTokenResponse>(json);
                    if (token is null || string.IsNullOrEmpty(token.AccessToken))
                    {
                        return TraktDevicePollStatus.Error;
                    }

                    await StoreTokenAsync(userId, token, cancellationToken).ConfigureAwait(false);
                    return TraktDevicePollStatus.Linked;
                case HttpStatusCode.BadRequest:
                    return TraktDevicePollStatus.Pending;
                case HttpStatusCode.Gone:
                    return TraktDevicePollStatus.Expired;
                case (HttpStatusCode)418:
                    return TraktDevicePollStatus.Denied;
                default:
                    return TraktDevicePollStatus.Error;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ResponseTooLargeException)
        {
            _pluginLog.LogWarning(LogSource, "Device token poll errored.", ex, _logger);
            return TraktDevicePollStatus.Error;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetValidAccessTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var token = _store.GetToken(userId);
        if (token is null || !token.IsLinked)
        {
            return null;
        }

        // Still valid (with skew): use it as is.
        if (token.ExpiresAtUtc - ExpirySkew > _utcNow())
        {
            return token.AccessToken;
        }

        // Expired or about to: refresh exactly once, then surface a re-link on failure.
        var refreshed = await RefreshAsync(userId, token.RefreshToken, cancellationToken).ConfigureAwait(false);
        return refreshed?.AccessToken;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(Guid userId, CancellationToken cancellationToken)
        => _store.RemoveAsync(userId, cancellationToken);

    // Exchanges a refresh token for a new token pair and persists it. Returns null (and clears nothing) when
    // the refresh fails, so the caller treats the user as needing a re-link without destroying the stored token.
    private async Task<TraktUserToken?> RefreshAsync(Guid userId, string refreshToken, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var clientSecret = _secretProtector.Unprotect(config?.TraktClientSecret);
        if (config is null || string.IsNullOrWhiteSpace(config.TraktClientId) || string.IsNullOrWhiteSpace(clientSecret)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        var body = JsonSerializer.Serialize(new
        {
            refresh_token = refreshToken,
            client_id = config.TraktClientId,
            client_secret = clientSecret,
            grant_type = "refresh_token",
        });

        using var request = BuildJsonRequest(HttpMethod.Post, "/oauth/token", body);
        try
        {
            using var response = await Send(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _pluginLog.LogWarning(LogSource, $"Token refresh failed: {(int)response.StatusCode}.", logger: _logger);
                return null;
            }

            var json = await HttpResponseReader.ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var token = JsonSerializer.Deserialize<TraktTokenResponse>(json);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
            {
                return null;
            }

            return await StoreTokenAsync(userId, token, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ResponseTooLargeException)
        {
            _pluginLog.LogWarning(LogSource, "Token refresh errored.", ex, _logger);
            return null;
        }
    }

    private async Task<TraktUserToken> StoreTokenAsync(Guid userId, TraktTokenResponse token, CancellationToken cancellationToken)
    {
        var stored = new TraktUserToken
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAtUtc = _utcNow().AddSeconds(token.ExpiresIn),
        };
        await _store.SaveAsync(userId, stored, cancellationToken).ConfigureAwait(false);
        return stored;
    }

    private static PluginConfiguration? GetConfig() => Plugin.Instance?.Configuration;

    private HttpClient GetClient() => _httpClientFactory.CreateClient("Trakt");

    private static HttpRequestMessage BuildJsonRequest(HttpMethod method, string relPath, string jsonBody)
    {
        var request = new HttpRequestMessage(method, new Uri(TraktApi.BaseUrl + relPath, UriKind.Absolute))
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        return request;
    }

    private Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => GetClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
}
