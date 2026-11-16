using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Detached, authoritative movie memberships and release-order metadata; never caches user outcomes.</summary>
internal sealed class CollectionContinuationCatalog
{
    private readonly Sequence[] _sequences;
    private readonly Dictionary<Guid, Guid> _titleKeys;

    private CollectionContinuationCatalog(Sequence[] sequences, Dictionary<Guid, Guid> titleKeys)
    {
        _sequences = sequences;
        _titleKeys = titleKeys;
    }

    /// <summary>Gets a neutral catalog when membership or ordering evidence is unavailable.</summary>
    internal static CollectionContinuationCatalog Empty { get; } = new([], []);

    /// <summary>Captures only provider-backed BoxSets and actual allowed movie members, including known unplayable gaps.</summary>
    /// <param name="items">Already loaded allowed titles; TV provider identities never enter the movie namespace.</param>
    /// <param name="memberships">Authoritatively captured item-to-BoxSet membership.</param>
    /// <param name="sequencedCollections">Provider-backed BoxSets with real memberships, not name-based franchise guesses.</param>
    /// <param name="complete">False means unknown relationships, not a confirmed empty or ordered collection.</param>
    /// <returns>Metadata-only release sequences, reusable across independent historical user views.</returns>
    internal static CollectionContinuationCatalog Create(IEnumerable<BaseItem> items, IReadOnlyDictionary<Guid, List<Guid>> memberships, IReadOnlySet<Guid> sequencedCollections, bool complete)
    {
        if (!complete)
        {
            return Empty;
        }

        var titles = items.OfType<Movie>().Where(item => item.Id != Guid.Empty).DistinctBy(item => item.Id).Select(CaptureTitle).ToArray();
        var byCollection = new Dictionary<Guid, List<Title>>();
        foreach (var title in titles)
        {
            foreach (var collection in (memberships.GetValueOrDefault(title.ItemId) ?? []).Where(sequencedCollections.Contains))
            {
                if (!byCollection.TryGetValue(collection, out var group))
                {
                    group = [];
                    byCollection.Add(collection, group);
                }

                group.Add(title);
            }
        }

        var sequences = byCollection.Values.Where(group => group.Count > 1).Select(PrepareSequence).OfType<Sequence>().ToArray();
        return new CollectionContinuationCatalog(sequences, titles.ToDictionary(title => title.ItemId, title => title.Key));
    }

    /// <summary>Resolves the earliest unfinished title from this user's completed progress, never a later sequel over an unfinished gap.</summary>
    /// <param name="profile">Only this user's current or self-excluded historical profile.</param>
    /// <returns>Next playable title IDs with bounded completion support; unknown/ambiguous order remains neutral.</returns>
    internal Dictionary<Guid, double> BuildSupport(UserWatchProfile profile)
    {
        if (_sequences.Length == 0)
        {
            return [];
        }

        var completed = new HashSet<Guid>();
        var positive = new HashSet<Guid>();
        foreach (var row in profile.WatchedItems.Where(row => row.Played && string.Equals(row.ItemType, "Movie", StringComparison.OrdinalIgnoreCase)))
        {
            var key = _titleKeys.TryGetValue(row.ItemId, out var known) ? known : TrainingExample.CreateTitleObservationId(Guid.Empty, row.ItemId, row.TmdbId, false);
            completed.Add(key);
            if (row.UserRating is null or <= 0 || (double.IsFinite(row.UserRating.Value) && row.UserRating > 5))
            {
                positive.Add(key);
            }
        }

        var support = new Dictionary<Guid, double>();
        foreach (var sequence in _sequences)
        {
            AddNextSupport(sequence, completed, positive, support);
        }

        return support;
    }

    private static Sequence? PrepareSequence(List<Title> group)
    {
        var copies = group.GroupBy(title => title.Key).ToArray();
        var merged = copies.Select(MergeCopies).ToArray();
        if (merged.Any(title => !title.HasValue))
        {
            return null;
        }

        var canonical = merged.Select(title => title.GetValueOrDefault()).OrderBy(title => title.Start).ToArray();
        if (canonical.Any(title => !title.Start.HasValue)
            || canonical.Zip(canonical.Skip(1)).Any(pair => pair.First.End >= pair.Second.Start))
        {
            return null;
        }

        var playableCopies = copies.ToDictionary(
            copiesOfTitle => copiesOfTitle.Key,
            copiesOfTitle => copiesOfTitle.Where(title => title.Playable).Select(title => title.ItemId).ToArray());
        return new Sequence(canonical, canonical.Select(title => playableCopies[title.Key]).ToArray());
    }

    private static void AddNextSupport(Sequence sequence, HashSet<Guid> completed, HashSet<Guid> positive, Dictionary<Guid, double> support)
    {
        var canonical = sequence.Ordered;
        var next = Array.FindIndex(canonical, title => !completed.Contains(title.Key));
        if (next < 0 || !canonical[next].Playable)
        {
            return;
        }

        var positiveProgress = canonical.Count(title => positive.Contains(title.Key));
        if (positiveProgress == 0)
        {
            return;
        }

        var confidence = positiveProgress / (positiveProgress + 1.0);
        foreach (var itemId in sequence.PlayableCopies[next])
        {
            support[itemId] = Math.Max(support.GetValueOrDefault(itemId), confidence);
        }
    }

    private static Title? MergeCopies(IGrouping<Guid, Title> copies)
    {
        var known = copies.Where(title => title.Start.HasValue).ToArray();
        var representative = copies.OrderByDescending(title => title.Playable).ThenBy(title => title.ItemId).First();
        if (known.Length == 0)
        {
            return representative;
        }

        var start = known.Max(title => title.Start);
        var end = known.Min(title => title.End);
        return start <= end ? representative with { Start = start, End = end } : null;
    }

    private static Title CaptureTitle(Movie item)
    {
        var tmdbId = int.TryParse(item.ProviderIds.GetValueOrDefault("Tmdb"), out var provider) && provider > 0 ? provider : 0;
        var key = TrainingExample.CreateTitleObservationId(Guid.Empty, item.Id, tmdbId, false);
        DateTime? start = item.PremiereDate is { } release && release != default ? release.Date : null;
        DateTime? end = start;
        if (!start.HasValue && item.ProductionYear is >= 1 and <= 9999)
        {
            start = new DateTime(item.ProductionYear.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            end = new DateTime(item.ProductionYear.Value, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        }

        return new Title(item.Id, key, start, end, !string.IsNullOrEmpty(item.Path));
    }

    private readonly record struct Title(Guid ItemId, Guid Key, DateTime? Start, DateTime? End, bool Playable);

    private sealed record Sequence(Title[] Ordered, Guid[][] PlayableCopies);
}
