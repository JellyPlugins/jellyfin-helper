using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Bounded, configuration-scoped title cache. Reads never fetch metadata or write to disk.</summary>
public sealed class TitleMetadataCache
{
    private const int MaximumEntries = 4096;
    private readonly Lock _gate = new();
    private readonly Lock _persistenceGate = new();
    private readonly Dictionary<string, TitleMetadataSnapshot> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _attempts = new(StringComparer.Ordinal);
    private readonly IServerConfigurationManager? _serverConfiguration;
    private readonly IFileSystem? _fileSystem;
    private readonly string? _cachePath;
    private readonly TimeProvider _clock;

    /// <summary>Initializes a new instance of the <see cref="TitleMetadataCache"/> class.</summary>
    /// <param name="serverConfiguration">Optional metadata-language context.</param>
    /// <param name="fileSystem">Optional persistence filesystem, used only on initialization and scheduled saves.</param>
    /// <param name="cachePath">An optional absolute plugin-data path.</param>
    /// <param name="clock">Optional deterministic clock.</param>
    public TitleMetadataCache(IServerConfigurationManager? serverConfiguration = null, IFileSystem? fileSystem = null, string? cachePath = null, TimeProvider? clock = null)
    {
        _serverConfiguration = serverConfiguration;
        _fileSystem = fileSystem;
        _cachePath = cachePath is not null && Path.IsPathFullyQualified(cachePath) ? cachePath : null;
        _clock = clock ?? TimeProvider.System;
        Load();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Returns last-known-good metadata, including bounded stale fallback during provider outages.</summary>
    /// <param name="config">The current provider configuration.</param>
    /// <param name="tmdbId">The title ID.</param>
    /// <param name="mediaType">The title namespace.</param>
    /// <returns>Immutable metadata, or null when unavailable or from another configuration.</returns>
    public TitleMetadataSnapshot? Get(PluginConfiguration config, int tmdbId, string mediaType)
    {
        var key = Key(config, tmdbId, mediaType);
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var snapshot) && snapshot.FetchedAtUtc <= Now && snapshot.FetchedAtUtc > Now.AddDays(-30) ? snapshot : null;
        }
    }

    /// <summary>Reserves a refresh slot, preventing duplicate title requests and rapid retries after failures.</summary>
    /// <param name="config">The captured provider configuration.</param>
    /// <param name="tmdbId">The title ID.</param>
    /// <param name="mediaType">The title namespace.</param>
    /// <returns>Whether this scheduled pass should refresh the title.</returns>
    public bool TryBeginRefresh(PluginConfiguration config, int tmdbId, string mediaType)
    {
        if (tmdbId <= 0 || mediaType is not ("movie" or "tv"))
        {
            return false;
        }

        var key = Key(config, tmdbId, mediaType);
        lock (_gate)
        {
            if ((_entries.TryGetValue(key, out var snapshot) && snapshot.FetchedAtUtc > Now.AddDays(-7) && snapshot.FetchedAtUtc <= Now)
                || (_attempts.TryGetValue(key, out var attempted) && attempted > Now.AddMinutes(-15)))
            {
                return false;
            }

            if (_attempts.Count >= MaximumEntries)
            {
                _attempts.Remove(_attempts.MinBy(pair => pair.Value).Key);
            }

            _attempts[key] = Now;
            return true;
        }
    }

    /// <summary>Publishes validated metadata without replacing a newer snapshot or persisting a prediction.</summary>
    /// <param name="config">The configuration under which the request was made.</param>
    /// <param name="snapshot">The immutable provider result.</param>
    public void Store(PluginConfiguration config, TitleMetadataSnapshot snapshot)
    {
        if (snapshot.TmdbId <= 0 || snapshot.MediaType is not ("movie" or "tv") || snapshot.FetchedAtUtc > Now)
        {
            return;
        }

        var key = Key(config, snapshot.TmdbId, snapshot.MediaType);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var previous) && previous.FetchedAtUtc > snapshot.FetchedAtUtc)
            {
                return;
            }

            if (_entries.Count >= MaximumEntries && !_entries.ContainsKey(key))
            {
                _entries.Remove(_entries.MinBy(pair => pair.Value.FetchedAtUtc).Key);
            }

            _entries[key] = previous is null
                ? snapshot
                : snapshot with
                {
                    GenreIds = snapshot.GenreIds ?? previous.GenreIds,
                    KeywordIds = snapshot.KeywordIds ?? previous.KeywordIds,
                    Keywords = snapshot.Keywords ?? previous.Keywords,
                    KeywordReferences = snapshot.KeywordReferences ?? previous.KeywordReferences,
                    OriginalLanguage = snapshot.OriginalLanguage ?? previous.OriginalLanguage,
                    SeriesType = snapshot.SeriesType ?? previous.SeriesType,
                    OriginCountries = snapshot.OriginCountries ?? previous.OriginCountries,
                    ProductionCountries = snapshot.ProductionCountries ?? previous.ProductionCountries,
                    Overview = snapshot.Overview ?? previous.Overview
                };
        }
    }

    /// <summary>Saves only metadata during an active scheduled pass, using atomic replacement.</summary>
    /// <returns>Whether persistence succeeded; failure never clears the in-memory fallback.</returns>
    public bool Save()
    {
        if (_cachePath is null || _fileSystem is null)
        {
            return false;
        }

        lock (_persistenceGate)
        {
            try
            {
                Dictionary<string, TitleMetadataSnapshot> snapshot;
                lock (_gate)
                {
                    snapshot = new Dictionary<string, TitleMetadataSnapshot>(_entries, StringComparer.Ordinal);
                }

                _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                _fileSystem.File.WriteAllText(_cachePath + ".tmp", JsonSerializer.Serialize(snapshot, JsonDefaults.Options));
                _fileSystem.File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                return false;
            }
        }
    }

    private string Key(PluginConfiguration config, int tmdbId, string mediaType)
    {
        var context = string.Join('\n', config.SeerrUrl, config.SeerrApiKey, _serverConfiguration?.Configuration?.PreferredMetadataLanguage);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context))) + ":" + mediaType + ":" + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Load()
    {
        if (_cachePath is null || _fileSystem is null)
        {
            return;
        }

        try
        {
            if (!_fileSystem.File.Exists(_cachePath) || _fileSystem.FileInfo.New(_cachePath).Length > 16 * 1024 * 1024)
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<Dictionary<string, TitleMetadataSnapshot>>(_fileSystem.File.ReadAllText(_cachePath), JsonDefaults.Options);
            foreach (var pair in (saved ?? []).Where(pair => pair.Value is not null && pair.Value.TmdbId > 0 && pair.Value.FetchedAtUtc <= Now && pair.Value.FetchedAtUtc > Now.AddDays(-30)).OrderByDescending(pair => pair.Value.FetchedAtUtc).Take(MaximumEntries))
            {
                _entries[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Corrupt optional metadata must never prevent the plugin from starting.
        }
    }
}
