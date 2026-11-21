using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>Captures chronological revealed preferences without turning publication or newly added media into rejection.</summary>
internal static class RecommendationChoiceBuilder
{
    internal const double ContrastWeightBudget = 0.25;
    private const int MaximumFittingAlternatives = 8;

    internal static Plan Build(Context context, Func<WatchedItemInfo, RecommendedItem?, TrainingExample> capture)
    {
        var result = context.Result;
        var recommendedAt = DateTimeNormalization.ToUtc(result.GeneratedAt);
        if (context.Profile.UserId != result.UserId || recommendedAt <= DateTime.UnixEpoch || recommendedAt > context.ReferenceTime)
        {
            return Plan.Empty;
        }

        var keys = new TitleKeys(result.UserId, context.TitleIds);
        var recorded = result.Recommendations.Where(item => item.ScoringFeatures is not null)
            .GroupBy(item => keys.Get(item.ItemId, string.Equals(item.ItemType, "Series", StringComparison.OrdinalIgnoreCase))).ToDictionary(group => group.Key, group => group.First());
        var engaged = EngagedTitles(context.Profile, keys);
        var prepared = new Prepared(context, recorded, engaged);
        var choices = new Dictionary<Guid, RecommendationChoiceEvidence>();
        var fitting = new HashSet<Guid>();
        var watched = context.Profile.WatchedItems.Where(item => IsCompletedChoice(item, recommendedAt, context.ReferenceTime))
            .GroupBy(keys.Get);
        foreach (var group in watched)
        {
            var captured = CaptureObservedChoice(prepared, group, capture, recommendedAt);
            if (captured is null)
            {
                continue;
            }

            choices[group.Key] = captured.Evidence;
            if (captured.ForFitting)
            {
                fitting.UnionWith(captured.Evidence.Alternatives.Select(alternative => alternative.ObservationId));
            }
        }

        return new Plan(choices, fitting.Where(id => !engaged.Contains(id)).Order()
            .Take(FittingAlternativeLimit(recommendedAt, context.ReferenceTime)).ToHashSet());
    }

    internal static HashSet<Guid> EngagedTitles(UserWatchProfile profile, IReadOnlyDictionary<Guid, int> titleIds)
    {
        var engaged = EngagedTitles(profile, new TitleKeys(profile.UserId, titleIds));
        // Persisted alternatives may predate provider ID acquisition. Retain both
        // namespaced provider and original library aliases only when they differ.
        var titles = profile.WatchedItems.Where(item => item.HasMeaningfulInteraction())
            .Select(item => item.SeriesId.GetValueOrDefault() != Guid.Empty ? item.SeriesId!.Value : item.ItemId)
            .Concat(profile.FavoriteSeriesIds).Distinct();
        foreach (var title in titles.Where(title => title != Guid.Empty && titleIds.GetValueOrDefault(title) > 0))
        {
            engaged.Add(TrainingExample.CreateLibraryObservationId(profile.UserId, title));
        }

        return engaged;
    }

    private static HashSet<Guid> EngagedTitles(UserWatchProfile profile, TitleKeys keys)
        => profile.WatchedItems.Where(item => item.HasMeaningfulInteraction()).Select(keys.Get)
            .Concat(profile.FavoriteSeriesIds.Where(series => series != Guid.Empty).Select(series => keys.Get(series, true))).ToHashSet();

    internal static int FittingAlternativeLimit(DateTime predictedAt, DateTime referenceTime)
    {
        // Thin comparisons, never inflate their shared budget, renew their timestamps,
        // or lower the optimizer floor. Older evidence supports fewer eligible comparisons.
        var ageDays = Math.Max(0, (referenceTime - predictedAt).TotalDays);
        var effectiveBudget = ContrastWeightBudget * Math.Exp(-TrainingExample.TemporalDecayConstant * ageDays);
        return Math.Min(MaximumFittingAlternatives, (int)Math.Floor(effectiveBudget / LearnedScoringStrategy.MinSampleWeight));
    }

    private static CapturedChoice? CaptureObservedChoice(Prepared prepared, IGrouping<Guid, WatchedItemInfo> group, Func<WatchedItemInfo, RecommendedItem?, TrainingExample> capture, DateTime recommendedAt)
    {
        var context = prepared.Context;
        var recorded = prepared.Recorded;
        recorded.TryGetValue(group.Key, out var prediction);
        var selected = group.OrderBy(item => item.LastPlayedDate).FirstOrDefault(item => AvailableAt(item, recommendedAt, prediction is not null).HasValue);
        if (selected is null)
        {
            return null;
        }

        // LastPlayedDate can be a replay, resume or later episode, not the first
        // engagement. Without earliest-engagement evidence, any engaged title is unknown
        // as an unobserved alternative, regardless of how late its last play was.
        var alternatives = recorded.Where(pair => pair.Key != group.Key && !prepared.Engaged.Contains(pair.Key))
            .Take(RecommendationChoiceEvidence.MaximumAlternatives)
            .Select(pair => new RecommendationChoiceAlternative(pair.Key, pair.Value.ScoringFeatures!.Copy())).ToImmutableArray();
        if (alternatives.IsEmpty)
        {
            return null;
        }

        var example = capture(selected, prediction);
        if (!example.IsObserved || example.Label <= 0.5 || example.ObservationId != group.Key)
        {
            return null;
        }

        var evidence = CaptureChoice(selected, example, recorded, recommendedAt, alternatives);
        return evidence.IsValid(group.Key, context.ReferenceTime) ? new CapturedChoice(evidence, !example.IsPermanentHoldout) : null;
    }

    private static RecommendationChoiceEvidence CaptureChoice(WatchedItemInfo selected, TrainingExample example, Dictionary<Guid, RecommendedItem> recorded, DateTime recommendedAt, ImmutableArray<RecommendationChoiceAlternative> alternatives)
    {
        var chosenAt = DateTimeNormalization.ToUtc(selected.LastPlayedDate!.Value);
        return new RecommendationChoiceEvidence
        {
            ChosenObservationId = example.ObservationId,
            RecommendedAtUtc = recommendedAt,
            AvailableAtUtc = AvailableAt(selected, recommendedAt, recorded.ContainsKey(example.ObservationId))!.Value,
            ChosenAtUtc = chosenAt,
            ChosenFeatures = example.Features.Copy(),
            Alternatives = alternatives
        };
    }

    private static bool IsCompletedChoice(WatchedItemInfo item, DateTime recommendationAt, DateTime referenceTime)
    {
        if (!item.Played || !item.LastPlayedDate.HasValue || (string.Equals(item.ItemType, "Episode", StringComparison.OrdinalIgnoreCase) && item.SeriesId.GetValueOrDefault() == Guid.Empty))
        {
            return false;
        }

        var playedAt = DateTimeNormalization.ToUtc(item.LastPlayedDate.Value);
        return playedAt > recommendationAt && playedAt <= referenceTime
            && (playedAt - recommendationAt).TotalDays <= EngineConstants.RecommendationInfluenceWindowDays;
    }

    private static DateTime? AvailableAt(WatchedItemInfo item, DateTime recommendationAt, bool wasRecommended)
    {
        if (item.DateCreated.HasValue && DateTimeNormalization.ToUtc(item.DateCreated.Value) > DateTime.UnixEpoch)
        {
            var added = DateTimeNormalization.ToUtc(item.DateCreated.Value);
            return added <= recommendationAt ? added : null;
        }

        // The recorded movie/title itself proves availability. An undated episode cannot prove
        // that the material actually watched was not newly added to an already listed series.
        return wasRecommended && item.SeriesId.GetValueOrDefault() == Guid.Empty ? recommendationAt : null;
    }

    internal sealed record Context(RecommendationResult Result, UserWatchProfile Profile, IReadOnlyDictionary<Guid, int> TitleIds, DateTime ReferenceTime);

    internal sealed record Plan(Dictionary<Guid, RecommendationChoiceEvidence> Choices, HashSet<Guid> FittingAlternatives)
    {
        internal static Plan Empty => new([], []);
    }

    private sealed record CapturedChoice(RecommendationChoiceEvidence Evidence, bool ForFitting);

    private sealed record Prepared(Context Context, Dictionary<Guid, RecommendedItem> Recorded, HashSet<Guid> Engaged);

    private sealed class TitleKeys(Guid user, IReadOnlyDictionary<Guid, int> ids)
    {
        private readonly Dictionary<(Guid Item, bool Series), Guid> _cache = [];

        internal Guid Get(WatchedItemInfo item)
        {
            var parent = item.SeriesId.GetValueOrDefault();
            if (parent == Guid.Empty && string.Equals(item.ItemType, "Episode", StringComparison.OrdinalIgnoreCase))
            {
                // An episode's own TMDb number is neither a movie nor a series title ID.
                return TrainingExample.CreateLibraryObservationId(user, item.ItemId);
            }

            return Get(parent != Guid.Empty ? parent : item.ItemId, parent != Guid.Empty || string.Equals(item.ItemType, "Series", StringComparison.OrdinalIgnoreCase));
        }

        internal Guid Get(Guid item, bool series)
        {
            if (!_cache.TryGetValue((item, series), out var key))
            {
                key = TrainingExample.CreateTitleObservationId(user, item, ids.GetValueOrDefault(item), series);
                _cache[(item, series)] = key;
            }

            return key;
        }
    }
}
