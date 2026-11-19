using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    private const string MovieMediaType = "movie";
    private readonly Lock _gate = new();
    private readonly Lock _persistenceGate = new();
    private readonly Dictionary<string, TitleMetadataSnapshot> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _aliases = new(StringComparer.Ordinal);
    private readonly IServerConfigurationManager? _serverConfiguration;
    private readonly IFileSystem? _fileSystem;
    private readonly string? _cachePath;
    private readonly TimeProvider _clock;
    private int _nextRefreshIndex;

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

    /// <summary>Gets current read-side localization context; acquisition captures it before requests start.</summary>
    internal string? MetadataLanguage => _serverConfiguration?.Configuration?.PreferredMetadataLanguage;

    /// <summary>Gets the live optional integration check; persisted Trakt data is not proof the plugin is still active.</summary>
    internal Func<PluginConfiguration, bool> TraktMetadataAvailable { get; init; } = config => config.TraktSourcingEnabled;

    /// <summary>Returns last-known-good metadata, including bounded stale fallback during provider outages.</summary>
    /// <param name="config">The current provider configuration.</param>
    /// <param name="tmdbId">The title ID.</param>
    /// <param name="mediaType">The title namespace.</param>
    /// <param name="tvdbId">Optional independently supplied television link.</param>
    /// <param name="imdbId">Optional independently supplied IMDb link.</param>
    /// <returns>Immutable metadata, or null when unavailable or from another configuration.</returns>
    public TitleMetadataSnapshot? Get(PluginConfiguration config, int tmdbId, string mediaType, int tvdbId = 0, string? imdbId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return GetForLanguage(config, tmdbId, mediaType, tvdbId, imdbId, MetadataLanguage);
    }

    internal TitleMetadataSnapshot? GetForLanguage(PluginConfiguration config, int tmdbId, string mediaType, int tvdbId, string? imdbId, string? language)
    {
        ArgumentNullException.ThrowIfNull(config);
        imdbId = TitleProviderIdentity.NormalizeImdb(imdbId);
        lock (_gate)
        {
            var now = Now;
            var keys = IdentityKeys(Key(config, 0, mediaType, language).Split(':')[0], mediaType, tmdbId, tvdbId, imdbId).ToList();
            foreach (var source in ActiveSupplementalSources(config))
            {
                keys.AddRange(IdentityKeys(SourceDigest(source, source.Source == "trakt" ? null : language), mediaType, tmdbId, tvdbId, imdbId));
            }

            var identity = new TitleMetadataSnapshot { TmdbId = tmdbId, TvdbId = tvdbId, ImdbId = imdbId, MediaType = mediaType };
            var snapshots = keys.Select(key => ReadEntry(key, now)).OfType<TitleMetadataSnapshot>().Distinct()
                .Where(snapshot => TitleProviderIdentity.Compatible(identity, snapshot) && TitleProviderIdentity.SharesIdentity(identity, snapshot))
                .Select(snapshot => LinkSuppliedIdentity(snapshot, identity)).ToArray();
            return TitleMetadataMerger.Combine(snapshots);
        }
    }

    private static TitleMetadataSnapshot LinkSuppliedIdentity(TitleMetadataSnapshot snapshot, TitleMetadataSnapshot identity)
    {
        if ((snapshot.TmdbId <= 0 && identity.TmdbId > 0) || (snapshot.TvdbId <= 0 && identity.TvdbId > 0)
            || (snapshot.ImdbId is null && identity.ImdbId is not null))
        {
            return snapshot with
            {
                TmdbId = snapshot.TmdbId > 0 ? snapshot.TmdbId : identity.TmdbId,
                TvdbId = snapshot.TvdbId > 0 ? snapshot.TvdbId : identity.TvdbId,
                ImdbId = snapshot.ImdbId ?? identity.ImdbId
            };
        }

        return snapshot;
    }

    private TitleMetadataSnapshot? ReadEntry(string key, DateTime now)
    {
        var primary = key;
        if (!_entries.ContainsKey(key) && _aliases.TryGetValue(key, out var matches))
        {
            // An ambiguous alias cannot select whichever title happened to publish last.
            if (matches.Count != 1)
            {
                return null;
            }

            primary = matches.Single();
        }

        if (!_entries.TryGetValue(primary, out var snapshot) || !IsUsable(snapshot.FetchedAtUtc, now))
        {
            return null;
        }

        return snapshot.FieldFetchedAtUtc is null || snapshot.FieldFetchedAtUtc.Values.All(time => IsUsable(time, now))
            ? snapshot
            : MergeFields(snapshot, null, now);
    }

    /// <summary>Reserves a refresh slot, preventing duplicate title requests and rapid retries after failures.</summary>
    /// <param name="config">The captured provider configuration.</param>
    /// <param name="tmdbId">The title ID.</param>
    /// <param name="mediaType">The title namespace.</param>
    /// <returns>Whether this scheduled pass should refresh the title.</returns>
    public bool TryBeginRefresh(PluginConfiguration config, int tmdbId, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (tmdbId <= 0 || mediaType is not (MovieMediaType or "tv"))
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
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(snapshot);
        Store(config, snapshot, _serverConfiguration?.Configuration?.PreferredMetadataLanguage);
    }

    internal TitleMetadataSnapshot? Store(PluginConfiguration config, TitleMetadataSnapshot snapshot, string? requestedLanguage)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TmdbId <= 0 || snapshot.MediaType is not (MovieMediaType or "tv") || snapshot.FetchedAtUtc > Now)
        {
            return null;
        }

        var key = Key(config, snapshot.TmdbId, snapshot.MediaType, requestedLanguage);
        return StoreEntry(key, snapshot);
    }

    internal TitleMetadataSnapshot? StoreSupplemental(PluginConfiguration config, TitleMetadataSnapshot snapshot, MetadataSourceScope source, string? language)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(snapshot);
        var key = IdentityKeys(SourceDigest(source, language), snapshot.MediaType, snapshot.TmdbId, snapshot.TvdbId, snapshot.ImdbId).FirstOrDefault();
        if (key is null || snapshot.FetchedAtUtc > Now || snapshot.MediaType is not (MovieMediaType or "tv")
            || !ActiveSupplementalSources(config).Contains(source))
        {
            return null;
        }

        return StoreEntry(key, snapshot with { Source = source.Source });
    }

    private TitleMetadataSnapshot StoreEntry(string key, TitleMetadataSnapshot snapshot)
    {
        var imdbId = TitleProviderIdentity.NormalizeImdb(snapshot.ImdbId);
        if (snapshot.ImdbId != imdbId)
        {
            snapshot = snapshot with { ImdbId = imdbId };
        }

        lock (_gate)
        {
            _entries.TryGetValue(key, out var previous);
            if (previous is not null && previous.FetchedAtUtc > snapshot.FetchedAtUtc)
            {
                return previous;
            }

            if (previous is not null && !TitleProviderIdentity.Compatible(previous, snapshot))
            {
                // Corrected provider links invalidate old fields as well as their aliases.
                previous = null;
            }

            if (_entries.Count >= MaximumEntries && !_entries.ContainsKey(key))
            {
                RemoveEntry(_entries.MinBy(pair => pair.Value.FetchedAtUtc).Key);
            }

            _entries[key] = previous is null && snapshot.FieldFetchedAtUtc is null ? snapshot : MergeFields(snapshot, previous, Now);
            RegisterAliases(key, _entries[key]);
            return _entries[key];
        }
    }

    private void RemoveEntry(string key)
    {
        _entries.Remove(key);
        RemoveAliases(key);
    }

    private void RemoveAliases(string key)
    {
        foreach (var pair in _aliases.Where(pair => pair.Value.Contains(key)).ToArray())
        {
            pair.Value.Remove(key);
            if (pair.Value.Count == 0)
            {
                _aliases.Remove(pair.Key);
            }
        }
    }

    private void RegisterAliases(string key, TitleMetadataSnapshot snapshot)
    {
        RemoveAliases(key);
        var scope = key.Split(':')[0];
        foreach (var alias in IdentityKeys(scope, snapshot.MediaType, snapshot.TmdbId, snapshot.TvdbId, snapshot.ImdbId))
        {
            if (!_aliases.TryGetValue(alias, out var matches))
            {
                matches = new HashSet<string>(StringComparer.Ordinal);
                _aliases[alias] = matches;
            }

            matches.Add(key);
        }
    }

    private static IEnumerable<string> IdentityKeys(string scope, string mediaType, int tmdbId, int tvdbId, string? imdbId)
    {
        if (tmdbId > 0)
        {
            yield return scope + ":" + mediaType + ":" + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (mediaType == "tv" && tvdbId > 0)
        {
            yield return scope + ":tv:tvdb:" + tvdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (TitleProviderIdentity.NormalizeImdb(imdbId) is { } normalizedImdb)
        {
            yield return scope + ":" + mediaType + ":imdb:" + normalizedImdb;
        }
    }

    private IEnumerable<MetadataSourceScope> ActiveSupplementalSources(PluginConfiguration config)
    {
        foreach (var instance in config.GetEffectiveRadarrInstances())
        {
            yield return new("radarr", instance.Url, instance.ApiKey);
        }

        foreach (var instance in config.GetEffectiveSonarrInstances())
        {
            yield return new("sonarr", instance.Url, instance.ApiKey);
        }

        if (config.TraktSourcingEnabled && TraktMetadataAvailable(config))
        {
            yield return new("trakt", "https://api.trakt.tv", "public-metadata");
        }
    }

    private static string SourceDigest(MetadataSourceScope source, string? language)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', source.Source, source.Url, source.ApiKey, language))));

    internal (int TmdbId, string MediaType)[] ReserveRefreshes(PluginConfiguration config, IEnumerable<(int TmdbId, string MediaType)> titles, int maximum)
    {
        lock (_gate)
        {
            // Never-fetched and least-recently attempted titles precede recently refreshed titles.
            // Include failed attempts in the age ordering so a permanently failing prefix cannot starve the tail.
            var candidates = titles.Distinct().ToArray();
            if (candidates.Length == 0 || maximum <= 0)
            {
                return [];
            }

            var start = _nextRefreshIndex % candidates.Length;
            var reserved = candidates.Select((title, index) => (Title: title, Index: index))
                .OrderBy(candidate => RefreshPriority(Key(config, candidate.Title.TmdbId, candidate.Title.MediaType)))
                .ThenBy(candidate => (candidate.Index - start + candidates.Length) % candidates.Length)
                .Where(candidate => TryBeginRefresh(config, candidate.Title.TmdbId, candidate.Title.MediaType)).Take(maximum).ToArray();
            if (reserved.Length > 0)
            {
                // Rotation also protects large libraries when bounded cache/attempt entries are evicted.
                _nextRefreshIndex = (reserved[^1].Index + 1) % candidates.Length;
            }

            return reserved.Select(candidate => candidate.Title).ToArray();
        }
    }

    private DateTime RefreshPriority(string key)
    {
        var fetched = _entries.TryGetValue(key, out var snapshot) ? snapshot.FetchedAtUtc : DateTime.MinValue;
        return _attempts.TryGetValue(key, out var attempted) && attempted > fetched ? attempted : fetched;
    }

    private static bool IsUsable(DateTime acquiredAt, DateTime now)
        => acquiredAt <= now && acquiredAt > now.AddDays(-30);

    private static TitleMetadataSnapshot MergeFields(TitleMetadataSnapshot snapshot, TitleMetadataSnapshot? previous, DateTime now)
    {
        var times = ImmutableDictionary.CreateBuilder<string, DateTime>(StringComparer.Ordinal);
        T Field<T>(T incoming, T retained, string name)
        {
            var source = incoming is not null ? snapshot : previous;
            var value = incoming is not null ? incoming : retained;
            if (value is null || source is null)
            {
                return default!;
            }

            var time = source.FieldFetchedAtUtc?.GetValueOrDefault(name, source.FetchedAtUtc) ?? source.FetchedAtUtc;
            if (!IsUsable(time, now))
            {
                return default!;
            }

            times[name] = time;
            return value;
        }

        var merged = snapshot with
        {
            TvdbId = Field(snapshot.TvdbId > 0 ? snapshot.TvdbId : (int?)null, previous?.TvdbId > 0 ? previous.TvdbId : (int?)null, nameof(TitleMetadataSnapshot.TvdbId)) ?? 0,
            ImdbId = Field(snapshot.ImdbId, previous?.ImdbId, nameof(TitleMetadataSnapshot.ImdbId)),
            GenreIds = Field(snapshot.GenreIds, previous?.GenreIds, nameof(TitleMetadataSnapshot.GenreIds)),
            SourceGenres = Field(snapshot.SourceGenres, previous?.SourceGenres, nameof(TitleMetadataSnapshot.SourceGenres)),
            KeywordIds = Field(snapshot.KeywordIds, previous?.KeywordIds, nameof(TitleMetadataSnapshot.KeywordIds)),
            Keywords = Field(snapshot.Keywords, previous?.Keywords, nameof(TitleMetadataSnapshot.Keywords)),
            KeywordReferences = Field(snapshot.KeywordReferences, previous?.KeywordReferences, nameof(TitleMetadataSnapshot.KeywordReferences)),
            OriginalLanguage = Field(snapshot.OriginalLanguage, previous?.OriginalLanguage, nameof(TitleMetadataSnapshot.OriginalLanguage)),
            SeriesType = Field(snapshot.SeriesType, previous?.SeriesType, nameof(TitleMetadataSnapshot.SeriesType)),
            AnimeHint = Field(snapshot.AnimeHint, previous?.AnimeHint, nameof(TitleMetadataSnapshot.AnimeHint)),
            OriginCountries = Field(snapshot.OriginCountries, previous?.OriginCountries, nameof(TitleMetadataSnapshot.OriginCountries)),
            ProductionCountries = Field(snapshot.ProductionCountries, previous?.ProductionCountries, nameof(TitleMetadataSnapshot.ProductionCountries)),
            Overview = Field(snapshot.Overview, previous?.Overview, nameof(TitleMetadataSnapshot.Overview))
        };
        return merged with { FieldFetchedAtUtc = times.ToImmutable() };
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
        => Key(config, tmdbId, mediaType, _serverConfiguration?.Configuration?.PreferredMetadataLanguage);

    private static string Key(PluginConfiguration config, int tmdbId, string mediaType, string? metadataLanguage)
    {
        var context = string.Join('\n', config.SeerrUrl, config.SeerrApiKey, metadataLanguage);
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
            foreach (var pair in (saved ?? []).Where(pair => ValidSavedEntry(pair)).OrderByDescending(pair => pair.Value.FetchedAtUtc).Take(MaximumEntries))
            {
                _entries[pair.Key] = pair.Value;
                RegisterAliases(pair.Key, pair.Value);
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Corrupt optional metadata must never prevent the plugin from starting.
        }
    }

    private bool ValidSavedEntry(KeyValuePair<string, TitleMetadataSnapshot> pair)
        => pair.Value is not null && pair.Key.Length > 64 && pair.Key[64] == ':' && pair.Key[..64].All(char.IsAsciiHexDigit)
            && pair.Value.MediaType is MovieMediaType or "tv"
            && IdentityKeys(pair.Key[..64], pair.Value.MediaType, pair.Value.TmdbId, pair.Value.TvdbId, pair.Value.ImdbId).Contains(pair.Key, StringComparer.Ordinal)
            && IsUsable(pair.Value.FetchedAtUtc, Now);
}
