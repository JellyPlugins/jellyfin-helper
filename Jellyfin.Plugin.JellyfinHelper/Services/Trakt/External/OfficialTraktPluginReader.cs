using System;
using System.Globalization;
using System.IO;
using System.Xml;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Trakt.External;

/// <summary>
///     Reads a per-user OAuth token out of the OFFICIAL Jellyfin Trakt plugin's persisted configuration so the
///     Helper can source Trakt recommendations through that plugin's single registered app, instead of
///     registering a second Trakt app of its own. On a Trakt free account only one connected app is allowed per
///     account, so a second app would collide with the official plugin; sourcing through it keeps Trakt seeing
///     exactly one app.
///     <para>
///     This is deliberately the ONLY place that knows the foreign plugin's on-disk layout (file name, XML element
///     names, its hardcoded client id). Everything the Helper does with the official plugin flows through here, so
///     a future change to that plugin is a single-file edit. The reader is strictly READ-ONLY on the token: it
///     never refreshes or writes anything back. Trakt refresh tokens are single-use and rotate on refresh, and the
///     official plugin owns that lifecycle; refreshing here would silently break the official plugin, so an expired
///     token is skipped and left for the official plugin to rotate on its own cycle.
///     </para>
/// </summary>
public sealed class OfficialTraktPluginReader : IOfficialTraktPluginReader
{
    /// <summary>
    ///     The official Trakt plugin's hardcoded client id (from its public <c>TraktUris.ClientId</c>). Outbound
    ///     recommendation calls made with a token sourced here MUST use this id so Trakt attributes the request to
    ///     the same single app the token was issued to.
    /// </summary>
    public const string OfficialTraktClientId = "bfdd2e032c30c35b368f97ef4ec81587b899bcb028b91a1d4ba5589a4b6a7267";

    private const string LogSource = "Trakt";

    // A plugin configuration XML is tiny (a handful of users). Cap the read so a corrupt or hostile file on the
    // shared config path cannot stream an unbounded body into memory while we parse it.
    private const long MaxConfigBytes = 4L * 1024 * 1024;

    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<OfficialTraktPluginReader> _logger;

    // Whether the official plugin is installed and active. A delegate (not a hard IPluginManager dependency) so
    // the reader stays host-free and unit-testable, and so a Jellyfin without the plugin manager cannot break DI.
    private readonly Func<bool> _isPresent;

    // Absolute path to the official plugin's config file (<PluginConfigurationsPath>/Trakt.xml), or null when the
    // Jellyfin paths service was unavailable at construction.
    private readonly string? _configPath;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OfficialTraktPluginReader"/> class.
    /// </summary>
    /// <param name="isPresent">Returns whether the official Trakt plugin is installed and active.</param>
    /// <param name="configPath">Absolute path to the official plugin's <c>Trakt.xml</c>, or null when unknown.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">The logger. Never receives a token value.</param>
    public OfficialTraktPluginReader(
        Func<bool> isPresent,
        string? configPath,
        IPluginLogService pluginLog,
        ILogger<OfficialTraktPluginReader> logger)
    {
        _isPresent = isPresent ?? throw new ArgumentNullException(nameof(isPresent));
        _configPath = configPath;
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsPresent()
    {
        try
        {
            return _isPresent();
        }
        catch (Exception ex)
        {
            // A presence probe must never throw into discovery or the UI. Treat any failure as "absent" so the
            // Helper falls back to its own-client-id flow rather than crashing.
            _pluginLog.LogWarning(LogSource, "Official Trakt plugin presence probe failed; treating as absent.", ex, _logger);
            return false;
        }
    }

    /// <inheritdoc />
    public OfficialTraktToken? TryGetToken(Guid jellyfinUserId, DateTimeOffset now)
    {
        if (jellyfinUserId == Guid.Empty || string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
        {
            return null;
        }

        OfficialTraktToken? parsed;
        try
        {
            parsed = ParseTokenForUser(_configPath, jellyfinUserId);
        }
        catch (Exception ex) when (ex is IOException
                                        or UnauthorizedAccessException
                                        or XmlException
                                        or FormatException
                                        or OverflowException)
        {
            // Malformed/locked/foreign-schema-changed config must degrade to "no token" (own-flow fallback),
            // never throw. The message carries no token material.
            _pluginLog.LogWarning(LogSource, "Could not read a token from the official Trakt plugin configuration.", ex, _logger);
            return null;
        }

        if (parsed is null)
        {
            return null;
        }

        // Expired tokens are intentionally skipped, not refreshed: the official plugin is the sole owner of the
        // single-use refresh token and rotates it on its own cycle. Refreshing here would invalidate its token.
        // The comparison is between absolute instants (DateTimeOffset), so it is correct no matter which clock
        // the foreign expiry was written against.
        if (parsed.AccessTokenExpiration <= now)
        {
            return null;
        }

        return parsed;
    }

    private static OfficialTraktToken? ParseTokenForUser(string configPath, Guid jellyfinUserId)
    {
        using var fileStream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Hardened reader: no DTD, no external entity/DOCTYPE resolution, bounded characters. The config lives on
        // the shared Jellyfin config path, so it is treated as untrusted input for parsing purposes.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaxConfigBytes,
            CloseInput = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        using var reader = XmlReader.Create(fileStream, settings);

        // Element names mirror the foreign plugin's TraktUser properties (standard XmlSerializer output).
        string? accessToken = null;
        string? refreshToken = null;
        string? linkedUserId = null;
        string? expiration = null;
        var insideUser = false;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "TraktUser":
                        insideUser = true;
                        accessToken = null;
                        refreshToken = null;
                        linkedUserId = null;
                        expiration = null;
                        break;
                    case "AccessToken" when insideUser:
                        accessToken = reader.ReadElementContentAsString();
                        break;
                    case "RefreshToken" when insideUser:
                        refreshToken = reader.ReadElementContentAsString();
                        break;
                    case "LinkedMbUserId" when insideUser:
                        linkedUserId = reader.ReadElementContentAsString();
                        break;
                    case "AccessTokenExpiration" when insideUser:
                        expiration = reader.ReadElementContentAsString();
                        break;
                    default:
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "TraktUser")
            {
                insideUser = false;
                var match = TryBuildToken(accessToken, refreshToken, linkedUserId, expiration, jellyfinUserId);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static OfficialTraktToken? TryBuildToken(
        string? accessToken,
        string? refreshToken,
        string? linkedUserId,
        string? expiration,
        Guid jellyfinUserId)
    {
        if (!Guid.TryParse(linkedUserId, out var parsedUserId) || parsedUserId != jellyfinUserId)
        {
            return null;
        }

        // A linked user with no access token is not usable; let the caller fall back to the own-client-id flow.
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        // The foreign plugin writes AccessTokenExpiration via XmlSerializer from DateTime.Now (LOCAL time, not
        // UTC - see TraktApi.RefreshUserAccessToken/PollForAccessToken). So:
        //   - a value carrying an offset (Kind=Local) round-trips to the correct absolute instant;
        //   - a value with no offset (Kind=Unspecified) must be read as the server's LOCAL time, which is the
        //     clock the official plugin used. Treating it as UTC here would skew validity by the UTC offset and
        //     wrongly accept or reject tokens depending on the server timezone.
        if (!string.IsNullOrEmpty(expiration)
            && DateTime.TryParse(
                expiration,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedDateTime))
        {
            var localized = parsedDateTime.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(parsedDateTime, DateTimeKind.Local)
                : parsedDateTime;
            return new OfficialTraktToken(accessToken, refreshToken ?? string.Empty, new DateTimeOffset(localized));
        }

        // No parseable expiry: treat as already expired so we skip rather than use a token of unknown validity.
        return new OfficialTraktToken(accessToken, refreshToken ?? string.Empty, DateTimeOffset.MinValue);
    }
}
