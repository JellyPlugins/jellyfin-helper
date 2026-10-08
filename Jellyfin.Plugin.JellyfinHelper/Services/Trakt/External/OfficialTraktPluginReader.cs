using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
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
            // A presence probe must never throw into discovery or the UI. The broad catch is intentional (the
            // injected delegate calls the host IPluginManager, whose failure modes we do not control), so treat
            // ANY non-fatal failure as "absent" (no Trakt source usable). SonarCloud S2221 flags
            // this generic catch - it is accepted by design here, not suppressed. Fatal process-ending exceptions
            // still propagate.
            if (ex.IsFatal())
            {
                throw;
            }

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

        List<(Guid UserId, OfficialTraktToken Token)> users;
        try
        {
            users = ParseUsers(_configPath);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Malformed/locked/foreign-schema-changed config must degrade to "no token" (no usable source),
            // never throw. FileStream/XmlReader.Create can throw beyond IO/Xml (ArgumentException,
            // NotSupportedException, SecurityException, ...), so catch all non-fatal failures. No token material
            // is logged.
            _pluginLog.LogWarning(LogSource, "Could not read a token from the official Trakt plugin configuration.", ex, _logger);
            return null;
        }

        foreach (var (userId, token) in users)
        {
            // Expired tokens are intentionally skipped, not refreshed: the official plugin is the sole owner of
            // the single-use refresh token and rotates it on its own cycle. Refreshing here would invalidate its
            // token. The comparison is between absolute instants, correct no matter which clock the foreign
            // expiry was written against.
            if (userId == jellyfinUserId && token.AccessTokenExpiration > now)
            {
                return token;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<Guid> GetLinkedUserIds(DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(_configPath) || !File.Exists(_configPath))
        {
            return [];
        }

        List<(Guid UserId, OfficialTraktToken Token)> users;
        try
        {
            users = ParseUsers(_configPath);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, "Could not enumerate linked users from the official Trakt plugin configuration.", ex, _logger);
            return [];
        }

        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var (userId, token) in users)
        {
            // Only users whose token is still usable: a user we would skip in TryGetToken is not worth warming.
            // Dedupe via a set so a duplicate LinkedMbUserId in the foreign config yields one entry.
            if (token.AccessTokenExpiration > now && seen.Add(userId))
            {
                ids.Add(userId);
            }
        }

        return ids;
    }

    // Single hardened parse of the foreign config into our own (userId, token) pairs. Both TryGetToken and
    // GetLinkedUserIds build on this so there is exactly one place that knows the on-disk shape.
    private static List<(Guid UserId, OfficialTraktToken Token)> ParseUsers(string configPath)
    {
        var result = new List<(Guid, OfficialTraktToken)>();
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

        // Element names mirror the foreign plugin's TraktUser properties (standard XmlSerializer output). We read
        // only the fields we need - the refresh token is intentionally not captured (the Helper never refreshes).
        string? accessToken = null;
        string? linkedUserId = null;
        string? expiration = null;
        var insideUser = false;

        // Flush the fields collected for the current <TraktUser> into the result, then clear them for the next.
        void Flush()
        {
            if (insideUser)
            {
                var built = TryBuildEntry(accessToken, linkedUserId, expiration);
                if (built is not null)
                {
                    result.Add(built.Value);
                }
            }

            accessToken = null;
            linkedUserId = null;
            expiration = null;
        }

        // NOTE: ReadElementContentAsString() advances the reader PAST the element's end tag, so we must not call
        // Read() again for that iteration (doing so skips the following sibling). We therefore drive the cursor
        // manually: read a child's content when we recognize it, otherwise advance by one node. A new <TraktUser>
        // start flushes the previous user; a final flush after the loop captures the last one. This is robust to
        // whitespace packing (a brittle end-tag-only scan silently dropped users in minified XML).
        if (!reader.Read())
        {
            return result;
        }

        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "TraktUser":
                    Flush();          // close out any previous user
                    insideUser = true;
                    reader.Read();
                    break;
                case "AccessToken" when insideUser:
                    accessToken = reader.ReadElementContentAsString(); // advances past end tag
                    break;
                case "LinkedMbUserId" when insideUser:
                    linkedUserId = reader.ReadElementContentAsString();
                    break;
                case "AccessTokenExpiration" when insideUser:
                    expiration = reader.ReadElementContentAsString();
                    break;
                default:
                    reader.Read();
                    break;
            }
        }

        Flush(); // capture the final <TraktUser>
        return result;
    }

    private static (Guid UserId, OfficialTraktToken Token)? TryBuildEntry(
        string? accessToken,
        string? linkedUserId,
        string? expiration)
    {
        if (!Guid.TryParse(linkedUserId, out var parsedUserId) || parsedUserId == Guid.Empty)
        {
            return null;
        }

        // A linked user with no usable access token is dropped (no Trakt source usable for them).
        // Whitespace-only is treated as absent: it would produce a malformed Authorization header, not a token.
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        // The foreign plugin writes AccessTokenExpiration via XmlSerializer from DateTime.Now (LOCAL time, not
        // UTC - see TraktApi.RefreshUserAccessToken/PollForAccessToken). So:
        //   - a value carrying an offset (Kind=Local) round-trips to the correct absolute instant;
        //   - a value with no offset (Kind=Unspecified) must be read as the server's LOCAL time, which is the
        //     clock the official plugin used. Treating it as UTC here would skew validity by the UTC offset and
        //     wrongly accept or reject tokens depending on the server timezone.
        DateTimeOffset expiry;
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
            expiry = new DateTimeOffset(localized);
        }
        else
        {
            // No parseable expiry: treat as already expired so callers skip rather than use a token of unknown validity.
            expiry = DateTimeOffset.MinValue;
        }

        return (parsedUserId, new OfficialTraktToken(accessToken, expiry));
    }
}
