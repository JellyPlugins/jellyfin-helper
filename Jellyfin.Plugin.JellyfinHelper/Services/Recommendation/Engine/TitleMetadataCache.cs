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
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Bounded, configuration-scoped title cache. Reads never fetch metadata or write to disk.</summary>
public sealed class TitleMetadataCache
{
    private const int MaximumEntries = 4096;
    private const int MaximumFileBytes = 16 * 1024 * 1024;
    private const string MovieMediaType = "movie";
    private readonly Lock _gate = new();
    private readonly Lock _persistenceGate = new();
    private readonly Lock _writerGate = new();
    private readonly Dictionary<string, TitleMetadataSnapshot> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _attempts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _entryAliases = new(StringComparer.Ordinal);
    private readonly IServerConfigurationManager? _serverConfiguration;
    private readonly IFileSystem? _fileSystem;
    private readonly string? _cachePath;
    private readonly TimeProvider _clock;
    private Task<bool>? _pendingSave;
    private long _generation;
    private long _savedGeneration;
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

    /// <summary>Gets the caller-wait clock; production keeps the same two-second persistence deadline.</summary>
    internal TimeProvider WaitTimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Captures bounded metadata validity before library alias lookup; later checks perform no inventory scan.</summary>
    /// <returns>The immutable revision and the next time-dependent projection boundary.</returns>
    internal (long Revision, DateTime CapturedAtUtc, DateTime ValidUntilUtc) CaptureExclusionStamp()
    {
        lock (_gate)
        {
            var now = Now;
            var until = DateTime.MaxValue;
            foreach (var snapshot in _entries.Values)
            {
                until = NextValidityBoundary(snapshot.FetchedAtUtc, now, until);
                foreach (var time in snapshot.FieldFetchedAtUtc?.Values ?? [])
                {
                    until = NextValidityBoundary(time, now, until);
                }
            }

            return (_generation, now, until);
        }
    }

    /// <summary>Validates the captured alias evidence without rereading cache entries or performing I/O.</summary>
    /// <param name="stamp">The revision and validity window captured before alias lookup.</param>
    /// <returns>Whether no metadata or clock boundary invalidated that evidence.</returns>
    internal bool IsExclusionStampCurrent((long Revision, DateTime CapturedAtUtc, DateTime ValidUntilUtc) stamp)
    {
        var now = Now;
        return Volatile.Read(ref _generation) == stamp.Revision && now >= stamp.CapturedAtUtc && now < stamp.ValidUntilUtc;
    }

    private static DateTime NextValidityBoundary(DateTime fetched, DateTime now, DateTime current)
    {
        var expiry = fetched > DateTime.MaxValue.AddDays(-30) ? DateTime.MaxValue : fetched.AddDays(30);
        var boundary = fetched > now ? fetched : expiry;
        return boundary > now && boundary < current ? boundary : current;
    }

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
            var keys = LookupIdentityKeys(config, mediaType, tmdbId, tvdbId, imdbId, language);

            // An ambiguous current scope is not a miss that another provider may silently
            // resolve. Exact TMDb anchors remain usable; expired alias claims do not compete.
            var identity = new TitleMetadataSnapshot { TmdbId = tmdbId, TvdbId = tvdbId, ImdbId = imdbId, MediaType = mediaType };
            if (tmdbId <= 0 && keys.Any(key => AliasIsAmbiguous(key, identity, now)))
            {
                return null;
            }

            var snapshots = keys.GroupBy(key => key.Split(':')[0]).Select(group =>
            {
                var observations = group.Select(key => ReadEntry(key, now, identity)).OfType<TitleMetadataSnapshot>().Distinct()
                    .Where(snapshot => TitleProviderIdentity.Compatible(identity, snapshot) && TitleProviderIdentity.SharesIdentity(identity, snapshot)).ToArray();
                var exact = observations.FirstOrDefault(snapshot => tmdbId > 0 && snapshot.TmdbId == tmdbId);
                if (exact is not null)
                {
                    observations = observations.Where(snapshot => TitleProviderIdentity.Compatible(exact, snapshot)).ToArray();
                }

                if (ConflictingIdentities(observations))
                {
                    return null;
                }

                return ReconcileSourceObservations(observations.Select(snapshot => LinkSuppliedIdentity(snapshot, identity)).ToArray(), now);
            }).OfType<TitleMetadataSnapshot>().ToArray();
            if (tmdbId <= 0 && ConflictingIdentities(snapshots))
            {
                return null;
            }

            return TitleMetadataMerger.Combine(snapshots);
        }
    }

    private List<string> LookupIdentityKeys(PluginConfiguration config, string mediaType, int tmdbId, int tvdbId, string? imdbId, string? language)
    {
        var keys = IdentityKeys(Key(config, 0, mediaType, language).Split(':')[0], mediaType, tmdbId, tvdbId, imdbId).ToList();
        foreach (var source in ActiveSupplementalSources(config))
        {
            keys.AddRange(IdentityKeys(SourceDigest(source, source.Source == "trakt" ? null : language), mediaType, tmdbId, tvdbId, imdbId));
        }

        return keys;
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

    private TitleMetadataSnapshot? ReadEntry(string key, DateTime now, TitleMetadataSnapshot identity)
    {
        if (ReadUsableEntry(key, now) is { } exact && key.Split(':').Length == 3)
        {
            return exact;
        }

        if (!_aliases.TryGetValue(key, out var matches))
        {
            return null;
        }

        // Expired observations and expired identity fields are not competing claims.
        // Never select whichever of two current, conflicting links published last.
        var current = matches.Select(primary => ReadUsableEntry(primary, now)).OfType<TitleMetadataSnapshot>()
            .Where(snapshot => TitleProviderIdentity.Compatible(identity, snapshot)
                && IdentityKeys(key.Split(':')[0], snapshot.MediaType, snapshot.TmdbId, snapshot.TvdbId, snapshot.ImdbId).Contains(key, StringComparer.Ordinal))
            .ToArray();
        if (CompetingClaims(current.Where(snapshot => snapshot.TmdbId > 0).Select(snapshot => snapshot.TmdbId))
            || CompetingClaims(current.Where(snapshot => snapshot.TvdbId > 0).Select(snapshot => snapshot.TvdbId))
            || CompetingClaims(current.Select(snapshot => TitleProviderIdentity.NormalizeImdb(snapshot.ImdbId)).OfType<string>()))
        {
            return null;
        }

        return ReconcileSourceObservations(current, now);
    }

    private static bool CompetingClaims<T>(IEnumerable<T> claims) => claims.Distinct().Skip(1).Any();

    private bool AliasIsAmbiguous(string key, TitleMetadataSnapshot identity, DateTime now)
        => _aliases.TryGetValue(key, out var matches) && ConflictingIdentities(matches.Select(primary => ReadUsableEntry(primary, now))
            .OfType<TitleMetadataSnapshot>().Where(snapshot => TitleProviderIdentity.Compatible(identity, snapshot)
                && IdentityKeys(key.Split(':')[0], snapshot.MediaType, snapshot.TmdbId, snapshot.TvdbId, snapshot.ImdbId).Contains(key, StringComparer.Ordinal)));

    private static bool ConflictingIdentities(IEnumerable<TitleMetadataSnapshot> observations)
    {
        var snapshots = observations.ToArray();
        return CompetingClaims(snapshots.Where(snapshot => snapshot.TmdbId > 0).Select(snapshot => snapshot.TmdbId))
            || CompetingClaims(snapshots.Where(snapshot => snapshot.TvdbId > 0).Select(snapshot => snapshot.TvdbId))
            || CompetingClaims(snapshots.Select(snapshot => TitleProviderIdentity.NormalizeImdb(snapshot.ImdbId)).OfType<string>());
    }

    internal static TitleMetadataSnapshot? ReconcileSourceObservations(TitleMetadataSnapshot[] snapshots, DateTime now)
    {
        if (snapshots.Length == 1)
        {
            return FreshProjection(snapshots[0], now);
        }

        // These entries share the alias's configuration/language digest. A newer
        // same-provider observation supersedes old scalar values, including false hints,
        // before Get merges independent providers. Retained fields keep their own ages.
        TitleMetadataSnapshot? combined = null;
        foreach (var current in snapshots.OrderBy(snapshot => snapshot.FetchedAtUtc).GroupBy(snapshot => snapshot.FetchedAtUtc)
            .Select(group => TitleMetadataMerger.Combine(group.ToArray())!))
        {
            // Only the primary anchor is inherited here. MergeFields already retains
            // missing TVDB/IMDb links with their original field acquisition times;
            // copying them into the incoming observation would falsely renew them.
            var anchored = current.TmdbId > 0 || combined is null || !TitleProviderIdentity.Compatible(current, combined)
                ? current
                : current with { TmdbId = combined.TmdbId };
            if (combined is not null && !TitleProviderIdentity.Compatible(anchored, combined))
            {
                combined = null;
            }

            combined = MergeFields(anchored, combined, now, preferNewerFields: true);
        }

        return combined is null ? null : combined with { SourceObservations = null, ObservationScope = snapshots[0].ObservationScope };
    }

    private TitleMetadataSnapshot? ReadUsableEntry(string key, DateTime now)
    {
        if (!_entries.TryGetValue(key, out var snapshot) || !IsUsable(snapshot.FetchedAtUtc, now))
        {
            return null;
        }

        var usable = FreshProjection(snapshot, now)!;
        return IdentityKeys(key.Split(':')[0], usable.MediaType, usable.TmdbId, usable.TvdbId, usable.ImdbId).Contains(key, StringComparer.Ordinal)
            ? usable
            : null;
    }

    internal static TitleMetadataSnapshot? FreshProjection(TitleMetadataSnapshot snapshot, DateTime now)
    {
        if (!IsUsable(snapshot.FetchedAtUtc, now))
        {
            return null;
        }

        return snapshot.FieldFetchedAtUtc is null || snapshot.FieldFetchedAtUtc.Values.All(time => IsUsable(time, now))
            ? snapshot
            : MergeFields(snapshot, null, now);
    }

    internal static string SeerrScope(PluginConfiguration config, string? language)
        => Key(config, 0, MovieMediaType, language).Split(':')[0];

    /// <summary>Captures currently configured producer sources without exposing their credentials.</summary>
    /// <param name="config">Current provider configuration.</param>
    /// <returns>Only hashed source identities, not credentials.</returns>
    internal string ProducerScope(PluginConfiguration config)
        => string.Join(':', ActiveSupplementalSources(config).Select(source => SourceDigest(source, source.Source == "trakt" ? null : MetadataLanguage))
            .Append(SeerrScope(config, MetadataLanguage)).Order(StringComparer.Ordinal));

    /// <summary>Reserves a refresh slot, preventing duplicate title requests and rapid retries after failures.</summary>
    /// <param name="config">The captured provider configuration.</param>
    /// <param name="tmdbId">The title ID.</param>
    /// <param name="mediaType">The title namespace.</param>
    /// <returns>Whether this scheduled pass should refresh the title.</returns>
    public bool TryBeginRefresh(PluginConfiguration config, int tmdbId, string mediaType)
        => TryBeginRefresh(config, tmdbId, mediaType, MetadataLanguage);

    private bool TryBeginRefresh(PluginConfiguration config, int tmdbId, string mediaType, string? language)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (tmdbId <= 0 || mediaType is not (MovieMediaType or "tv"))
        {
            return false;
        }

        var key = Key(config, tmdbId, mediaType, language);
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

    private TitleMetadataSnapshot? StoreEntry(string key, TitleMetadataSnapshot snapshot)
    {
        snapshot = snapshot with { ObservationScope = key.Split(':')[0], SourceObservations = null };
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
                return ReadUsableEntry(key, Now);
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
            _generation++;
            RegisterAliases(key, _entries[key]);
            return ReadUsableEntry(key, Now);
        }
    }

    private void RemoveEntry(string key)
    {
        _entries.Remove(key);
        RemoveAliases(key);
    }

    private void RemoveAliases(string key)
    {
        if (!_entryAliases.Remove(key, out var aliases))
        {
            return;
        }

        foreach (var alias in aliases)
        {
            if (!_aliases.TryGetValue(alias, out var entries))
            {
                continue;
            }

            entries.Remove(key);
            if (entries.Count == 0)
            {
                _aliases.Remove(alias);
            }
        }
    }

    private void RegisterAliases(string key, TitleMetadataSnapshot snapshot)
    {
        RemoveAliases(key);
        var scope = key.Split(':')[0];
        var aliases = IdentityKeys(scope, snapshot.MediaType, snapshot.TmdbId, snapshot.TvdbId, snapshot.ImdbId).ToArray();
        _entryAliases[key] = aliases;
        foreach (var alias in aliases)
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

    internal static string SourceDigest(MetadataSourceScope source, string? language)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', source.Source, source.Url, source.ApiKey, language))));

    internal (int TmdbId, string MediaType)[] ReserveRefreshes(PluginConfiguration config, IEnumerable<(int TmdbId, string MediaType)> titles, int maximum)
        => ReserveRefreshes(config, titles, maximum, MetadataLanguage);

    internal (int TmdbId, string MediaType)[] ReserveRefreshes(PluginConfiguration config, IEnumerable<(int TmdbId, string MediaType)> titles, int maximum, string? language)
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
                .OrderBy(candidate => RefreshPriority(Key(config, candidate.Title.TmdbId, candidate.Title.MediaType, language)))
                .ThenBy(candidate => (candidate.Index - start + candidates.Length) % candidates.Length)
                .Where(candidate => TryReserveRefresh(config, candidate.Title.TmdbId, candidate.Title.MediaType, language)).Take(maximum).ToArray();
            if (reserved.Length > 0)
            {
                // Rotation also protects large libraries when bounded cache/attempt entries are evicted.
                _nextRefreshIndex = (reserved[^1].Index + 1) % candidates.Length;
            }

            return reserved.Select(candidate => candidate.Title).ToArray();
        }
    }

    private bool TryReserveRefresh(PluginConfiguration config, int id, string mediaType, string? language)
    {
        var key = Key(config, id, mediaType, language);
        var now = Now;
        return id > 0 && mediaType is MovieMediaType or "tv" && _reservations.Count < MaximumEntries
            && !(_entries.TryGetValue(key, out var snapshot) && snapshot.FetchedAtUtc > now.AddDays(-7) && snapshot.FetchedAtUtc <= now)
            && !(_attempts.TryGetValue(key, out var attempted) && attempted > now.AddMinutes(-15))
            && _reservations.Add(key);
    }

    internal bool DispatchRefresh(PluginConfiguration config, int id, string mediaType, string? language)
    {
        lock (_gate)
        {
            var key = Key(config, id, mediaType, language);
            return _reservations.Remove(key) && TryBeginRefresh(config, id, mediaType, language);
        }
    }

    internal void ReleaseRefreshes(PluginConfiguration config, IEnumerable<(int TmdbId, string MediaType)> titles, string? language)
    {
        lock (_gate)
        {
            foreach (var title in titles)
            {
                _reservations.Remove(Key(config, title.TmdbId, title.MediaType, language));
            }
        }
    }

    private DateTime RefreshPriority(string key)
    {
        var fetched = _entries.TryGetValue(key, out var snapshot) ? snapshot.FetchedAtUtc : DateTime.MinValue;
        return _attempts.TryGetValue(key, out var attempted) && attempted > fetched ? attempted : fetched;
    }

    private static bool IsUsable(DateTime acquiredAt, DateTime now)
        => acquiredAt <= now && acquiredAt > now.AddDays(-30);

    private static TitleMetadataSnapshot MergeFields(TitleMetadataSnapshot snapshot, TitleMetadataSnapshot? previous, DateTime now, bool preferNewerFields = false)
    {
        var times = ImmutableDictionary.CreateBuilder<string, DateTime>(StringComparer.Ordinal);
        T Field<T>(T incoming, T retained, string name)
        {
            var keepRetained = preferNewerFields && incoming is not null && retained is not null && previous is not null
                && (previous.FieldFetchedAtUtc?.GetValueOrDefault(name, previous.FetchedAtUtc) ?? previous.FetchedAtUtc)
                    > (snapshot.FieldFetchedAtUtc?.GetValueOrDefault(name, snapshot.FetchedAtUtc) ?? snapshot.FetchedAtUtc);
            var source = incoming is not null && !keepRetained ? snapshot : previous;
            var value = incoming is not null && !keepRetained ? incoming : retained;
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
            ProductionCompanies = Field(snapshot.ProductionCompanies, previous?.ProductionCompanies, nameof(TitleMetadataSnapshot.ProductionCompanies)),
            Networks = Field(snapshot.Networks, previous?.Networks, nameof(TitleMetadataSnapshot.Networks)),
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
                long generation;
                lock (_gate)
                {
                    var acquired = Now;
                    snapshot = _entries.Keys.Select(key => (Key: key, Metadata: ReadUsableEntry(key, acquired)))
                        .Where(entry => entry.Metadata is not null).ToDictionary(entry => entry.Key, entry => entry.Metadata!, StringComparer.Ordinal);
                    generation = _generation;
                }

                var json = JsonSerializer.Serialize(snapshot, JsonDefaults.Options);
                if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
                {
                    // Keep the previous readable file instead of replacing it with a
                    // successful-looking save that startup will discard in its entirety.
                    return false;
                }

                _fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                _fileSystem.File.WriteAllText(_cachePath + ".tmp", json);
                _fileSystem.File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
                lock (_gate)
                {
                    _savedGeneration = generation;
                }

                return true;
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                return false;
            }
        }
    }

    /// <summary>Waits a bounded time for one managed atomic writer; cancellation never waits on stalled filesystem I/O.</summary>
    /// <param name="cancellationToken">Cancellation of this caller's wait, not ownership of the shared writer.</param>
    /// <returns>Whether persistence completed successfully within the wait budget.</returns>
    public async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cachePath is null || _fileSystem is null)
        {
            return false;
        }

        Task<bool> writer;
        long requestedGeneration;
        lock (_gate)
        {
            requestedGeneration = _generation;
        }

        lock (_writerGate)
        {
            // Safe abandonment is bounded to one writer per cache. Repeated scheduled
            // passes cannot accumulate blocked writes or race the atomic replacement.
            if (_pendingSave is { IsCompleted: false } active)
            {
                writer = active;
            }
            else
            {
                writer = Task.Run(Save, CancellationToken.None);
                _pendingSave = writer;
            }
        }

        try
        {
            var saved = await writer.WaitAsync(TimeSpan.FromSeconds(2), WaitTimeProvider, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                return saved && _savedGeneration >= requestedGeneration;
            }
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

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
            if (!_fileSystem.File.Exists(_cachePath) || _fileSystem.FileInfo.New(_cachePath).Length > MaximumFileBytes)
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<Dictionary<string, TitleMetadataSnapshot>>(_fileSystem.File.ReadAllText(_cachePath), JsonDefaults.Options);
            foreach (var pair in (saved ?? []).Where(pair => ValidSavedEntry(pair)).OrderByDescending(pair => pair.Value.FetchedAtUtc).Take(MaximumEntries))
            {
                _entries[pair.Key] = pair.Value with { ObservationScope = pair.Key.Split(':')[0], SourceObservations = null };
                RegisterAliases(pair.Key, _entries[pair.Key]);
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
