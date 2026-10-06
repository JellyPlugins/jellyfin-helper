using System;
using System.Collections.Concurrent;
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

    // One refresh gate per user that ever hits an expired token (bounded by the user count). Concurrent
    // requests for the same user share a single refresh grant instead of racing it.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshGates = new();

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

        // Serialize refreshes per user so concurrent requests share one grant. Re-read inside
        // the gate; a concurrent refresh may already have stored a fresh token.
        var gate = _refreshGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _store.GetToken(userId);
            if (current is null || !current.IsLinked)
            {
                return null;
            }

            if (current.ExpiresAtUtc - ExpirySkew > _utcNow())
            {
                return current.AccessToken;
            }

            // Expired or about to: refresh exactly once, then surface a re-link on failure.
            var refreshed = await RefreshAsync(userId, current.RefreshToken, cancellationToken).ConfigureAwait(false);
            return refreshed?.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> RefreshAccessTokenAsync(Guid userId, string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        // Same per-user gate as the lazy path: concurrent 401s share one forced grant.
        var gate = _refreshGates.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = _store.GetToken(userId);
            if (current is null || !current.IsLinked)
            {
                return null;
            }

            // A concurrent refresh rotated the token while this call waited: the stored credential
            // is already newer than the rejected one, so use it without another grant. A revoked
            // token is usually far from expiry, so the old expiry shortcut here would have returned
            // the dead token and the caller could never re-link.
            if (rejectedAccessToken is not null
                && !string.Equals(current.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
            {
                return current.AccessToken;
            }

            var refreshed = await RefreshAsync(userId, current.RefreshToken, cancellationToken).ConfigureAwait(false);
            return refreshed?.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task DisconnectAsync(Guid userId, CancellationToken cancellationToken)
        => _store.RemoveAsync(userId, cancellationToken);

    /// <inheritdoc />
    public async Task<(bool Success, string Message)> TestClientIdAsync(string clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return (false, "A Trakt Client ID is required.");
        }

        // Trending is the only endpoint reachable with just a client id, so it is the honest admin-level check.
        var uri = new Uri($"{TraktApi.BaseUrl}/movies/trending?limit=1", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
        request.Headers.TryAddWithoutValidation("trakt-api-key", clientId);

        try
        {
            using var response = await Send(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Trakt Client ID is valid.");
            }

            // Log the raw status server-side only; the client id itself is never logged.
            _pluginLog.LogWarning(LogSource, $"Trakt client id test failed: {(int)response.StatusCode}.", logger: _logger);
            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? (false, "Trakt rejected this Client ID. Verify it against your Trakt application.")
                : (false, "Could not reach Trakt. Please try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or ResponseTooLargeException)
        {
            _pluginLog.LogWarning(LogSource, "Trakt client id test errored.", ex, _logger);
            return (false, "Could not reach Trakt. Please try again.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _pluginLog.LogWarning(LogSource, "Trakt client id test timed out.", logger: _logger);
            return (false, "Trakt connection timed out.");
        }
    }

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

            return await StoreTokenAsync(userId, token, cancellationToken, refreshToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ResponseTooLargeException)
        {
            _pluginLog.LogWarning(LogSource, "Token refresh errored.", ex, _logger);
            return null;
        }
    }

    private async Task<TraktUserToken> StoreTokenAsync(Guid userId, TraktTokenResponse token, CancellationToken cancellationToken, string? previousRefreshToken = null)
    {
        // A refresh response may omit refresh_token (rotation is optional): keep the previous one
        // so a valid access token never orphans the link by storing an empty refresh token.
        var stored = new TraktUserToken
        {
            AccessToken = token.AccessToken,
            RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? previousRefreshToken ?? string.Empty : token.RefreshToken,
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
