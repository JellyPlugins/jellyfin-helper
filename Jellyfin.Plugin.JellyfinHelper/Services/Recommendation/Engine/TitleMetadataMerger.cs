using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Combines verified same-title observations without counting duplicate terms or laundering acquisition times.</summary>
internal static class TitleMetadataMerger
{
    internal static TitleMetadataSnapshot? Combine(IReadOnlyList<TitleMetadataSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return null;
        }

        var observations = snapshots.SelectMany(Observations).Distinct().ToArray();
        if (observations.Length == 1)
        {
            return observations[0];
        }

        var ordered = ConnectedObservations(observations);
        var times = ImmutableDictionary.CreateBuilder<string, DateTime>(StringComparer.Ordinal);
        var conflicts = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        T? Select<T>(Func<TitleMetadataSnapshot, T?> selector, string name)
            where T : class
        {
            var source = ordered.FirstOrDefault(snapshot => selector(snapshot) is { } value && (value is not string text || !string.IsNullOrWhiteSpace(text)));
            if (source is null)
            {
                return null;
            }

            times[name] = AcquiredAt(source, name);
            return selector(source);
        }

        string? Consistent(Func<TitleMetadataSnapshot, string?> selector, string name, Func<string?, string?> normalize)
        {
            var values = ordered.Select(selector).OfType<string>()
                .Concat(ordered.SelectMany(snapshot => snapshot.ConflictingValues?.GetValueOrDefault(name) ?? []))
                .Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            var identities = values.Select(normalize).Distinct(StringComparer.Ordinal).ToArray();
            if (identities.Length > 1 || identities.Contains(null))
            {
                conflicts[name] = values;
                return null;
            }

            var selected = Select(selector, name);
            return normalize(selected);
        }

        ImmutableArray<T>? Union<T>(Func<TitleMetadataSnapshot, ImmutableArray<T>?> selector, string name, IEqualityComparer<T>? comparer = null)
        {
            var available = ordered.Where(snapshot => selector(snapshot).HasValue).ToArray();
            if (available.Length == 0)
            {
                return null;
            }

            // One field may retain mixed-age values. A conservative earliest acquisition prevents
            // a fresh source from perpetually renewing older retained values in the merged projection.
            times[name] = available.Min(snapshot => AcquiredAt(snapshot, name));
            return available.SelectMany(snapshot => selector(snapshot)!.Value).Distinct(comparer).ToImmutableArray();
        }

        var genres = ordered.FirstOrDefault(snapshot => snapshot.GenreIds.HasValue);
        if (genres is not null)
        {
            times[nameof(TitleMetadataSnapshot.GenreIds)] = AcquiredAt(genres, nameof(TitleMetadataSnapshot.GenreIds));
        }

        var animeHint = ordered.FirstOrDefault(snapshot => snapshot.AnimeHint == true)
            ?? ordered.FirstOrDefault(snapshot => snapshot.AnimeHint.HasValue);
        if (animeHint is not null)
        {
            times[nameof(TitleMetadataSnapshot.AnimeHint)] = AcquiredAt(animeHint, nameof(TitleMetadataSnapshot.AnimeHint));
        }

        var television = ordered.FirstOrDefault(snapshot => snapshot.TvdbId > 0);
        if (television is not null)
        {
            times[nameof(TitleMetadataSnapshot.TvdbId)] = AcquiredAt(television, nameof(TitleMetadataSnapshot.TvdbId));
        }

        var merged = new TitleMetadataSnapshot
        {
            TmdbId = ordered.Select(snapshot => snapshot.TmdbId).FirstOrDefault(id => id > 0),
            TvdbId = television?.TvdbId ?? 0,
            ImdbId = Select(snapshot => snapshot.ImdbId, nameof(TitleMetadataSnapshot.ImdbId)),
            MediaType = ordered[0].MediaType,
            GenreIds = genres?.GenreIds,
            SourceGenres = Union(snapshot => snapshot.SourceGenres, nameof(TitleMetadataSnapshot.SourceGenres), StringComparer.OrdinalIgnoreCase),
            KeywordIds = Union(snapshot => snapshot.KeywordIds, nameof(TitleMetadataSnapshot.KeywordIds)),
            Keywords = Union(snapshot => snapshot.Keywords, nameof(TitleMetadataSnapshot.Keywords), StringComparer.OrdinalIgnoreCase),
            KeywordReferences = Union(snapshot => snapshot.KeywordReferences, nameof(TitleMetadataSnapshot.KeywordReferences)),
            OriginalLanguage = Consistent(snapshot => snapshot.OriginalLanguage, nameof(TitleMetadataSnapshot.OriginalLanguage), ContentDescriptor.NormalizeOriginalLanguage),
            SeriesType = Consistent(snapshot => snapshot.SeriesType, nameof(TitleMetadataSnapshot.SeriesType), ContentDescriptor.NormalizeNarrativeType),
            AnimeHint = animeHint?.AnimeHint,
            OriginCountries = Union(snapshot => snapshot.OriginCountries, nameof(TitleMetadataSnapshot.OriginCountries), StringComparer.OrdinalIgnoreCase),
            ProductionCountries = Union(snapshot => snapshot.ProductionCountries, nameof(TitleMetadataSnapshot.ProductionCountries), StringComparer.OrdinalIgnoreCase),
            Overview = Select(snapshot => snapshot.Overview, nameof(TitleMetadataSnapshot.Overview)),
            FetchedAtUtc = ordered.Max(snapshot => snapshot.FetchedAtUtc),
            Source = string.Join('+', ordered.Select(snapshot => snapshot.Source).Distinct(StringComparer.Ordinal)),
            SourceObservations = ordered.Select(snapshot => snapshot with { SourceObservations = null }).ToImmutableArray()
        };
        return merged with { FieldFetchedAtUtc = times.ToImmutable(), ConflictingValues = NonEmptyConflicts(conflicts.ToImmutable()) };
    }

    internal static IEnumerable<TitleMetadataSnapshot> Observations(TitleMetadataSnapshot snapshot)
        => snapshot.SourceObservations is { IsDefaultOrEmpty: false } sources ? sources : [snapshot];

    internal static TitleMetadataSnapshot? CombineCurrentObservations(IReadOnlyList<TitleMetadataSnapshot> snapshots, DateTime now)
    {
        var current = snapshots.SelectMany(Observations).Select(snapshot => TitleMetadataCache.FreshProjection(snapshot, now))
            .OfType<TitleMetadataSnapshot>().Distinct().ToArray();
        var scoped = current.Where(snapshot => snapshot.ObservationScope is not null).GroupBy(snapshot => snapshot.ObservationScope)
            .Select(group => TitleMetadataCache.ReconcileSourceObservations(group.ToArray(), now)).OfType<TitleMetadataSnapshot>();
        return Combine(scoped.Concat(current.Where(snapshot => snapshot.ObservationScope is null)).ToArray());
    }

    internal static TitleMetadataSnapshot? WithSeerrObservation(TitleMetadataSnapshot current, TitleMetadataSnapshot? previous, DateTime now)
    {
        var prior = previous is null ? [] : Observations(previous).ToArray();
        var sameSource = prior.Where(snapshot => snapshot.Source == "seerr" && snapshot.ObservationScope == current.ObservationScope
            && TitleProviderIdentity.Compatible(snapshot, current) && TitleProviderIdentity.SharesIdentity(snapshot, current));
        var seerr = TitleMetadataCache.ReconcileSourceObservations(sameSource.Append(current).ToArray(), now);
        // A legacy mixed projection cannot prove which source supplied a retained term.
        // Do not launder superseded Seerr fields through it as independent evidence.
        var independent = prior.Where(snapshot => !snapshot.Source.Split('+').Contains("seerr", StringComparer.Ordinal))
            .Select(snapshot => TitleMetadataCache.FreshProjection(snapshot, now)).OfType<TitleMetadataSnapshot>();
        return Combine(independent.Concat(seerr is null ? [] : new[] { seerr }).ToArray());
    }

    private static ImmutableDictionary<string, ImmutableArray<string>>? NonEmptyConflicts(ImmutableDictionary<string, ImmutableArray<string>> conflicts)
        => conflicts.IsEmpty ? null : conflicts;

    private static DateTime AcquiredAt(TitleMetadataSnapshot snapshot, string field)
        => snapshot.FieldFetchedAtUtc?.GetValueOrDefault(field, snapshot.FetchedAtUtc) ?? snapshot.FetchedAtUtc;

    private static int Priority(string source)
        => source switch { "seerr" => 0, "trakt" => 1, "sonarr" or "radarr" => 2, _ => 3 };

    private static TitleMetadataSnapshot[] ConnectedObservations(IReadOnlyList<TitleMetadataSnapshot> snapshots)
    {
        var pending = snapshots.OrderBy(snapshot => Priority(snapshot.Source)).ThenByDescending(snapshot => snapshot.FetchedAtUtc).ToList();
        var connected = new List<TitleMetadataSnapshot> { pending[0] };
        pending.RemoveAt(0);
        while (pending.FirstOrDefault(candidate => connected.All(item => TitleProviderIdentity.Compatible(item, candidate))
            && connected.Any(item => TitleProviderIdentity.SharesIdentity(item, candidate))) is { } next)
        {
            connected.Add(next);
            pending.Remove(next);
        }

        return connected.OrderBy(snapshot => Priority(snapshot.Source)).ThenByDescending(snapshot => snapshot.FetchedAtUtc).ToArray();
    }
}
