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
        var engaged = context.Profile.WatchedItems.Where(item => item.HasMeaningfulInteraction())
            .GroupBy(keys.Get).ToDictionary(group => group.Key, group => group.Min(item => ConservativeEngagementAt(item, context.ReferenceTime)));
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

        return new Plan(choices, fitting);
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

        var chosenAt = DateTimeNormalization.ToUtc(selected.LastPlayedDate!.Value);
        var alternatives = recorded.Where(pair => pair.Key != group.Key && (!prepared.Engaged.TryGetValue(pair.Key, out var engagedAt) || engagedAt > chosenAt))
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

    private static DateTime ConservativeEngagementAt(WatchedItemInfo item, DateTime referenceTime)
    {
        var time = DateTimeNormalization.ToUtc(item.LastPlayedDate ?? DateTime.UnixEpoch);
        // Missing/future engagement times cannot prove that an alternative was still
        // unobserved when the user chose another title. Exclude it from comparisons.
        return time <= referenceTime ? time : DateTime.UnixEpoch;
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

    private sealed record Prepared(Context Context, Dictionary<Guid, RecommendedItem> Recorded, Dictionary<Guid, DateTime> Engaged);

    private sealed class TitleKeys(Guid user, IReadOnlyDictionary<Guid, int> ids)
    {
        private readonly Dictionary<(Guid Item, bool Series), Guid> _cache = [];

        internal Guid Get(WatchedItemInfo item)
        {
            var parent = item.SeriesId.GetValueOrDefault();
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
