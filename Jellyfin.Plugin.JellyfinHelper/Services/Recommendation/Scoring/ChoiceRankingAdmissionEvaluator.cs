using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Independently evaluates revealed choices without relabeling available alternatives as observed dislikes.</summary>
internal static class ChoiceRankingAdmissionEvaluator
{
    private const double Tolerance = 1e-12;
    private const double ConfidenceMultiplier = 2.5;

    internal static bool HasSupport(IReadOnlyList<TrainingExample> examples, DateTime referenceTime)
        => Supported(Capture(examples, referenceTime));

    internal static AdmissionDecision? Evaluate(IReadOnlyList<TrainingExample> calibration, IReadOnlyList<TrainingExample> acceptance, AdmissionModels models, DateTime referenceTime, (double Alpha, double Neural) caps, Func<IReadOnlyCollection<Guid>, string?>? reserve)
    {
        var cal = Capture(calibration, referenceTime);
        var test = Capture(acceptance, referenceTime);
        var users = cal.Select(row => row.UserId).ToHashSet();
        test = test.Where(row => users.Contains(row.UserId)).ToArray();
        if (!Supported(cal) || !Supported(test))
        {
            return null;
        }

        cal = Score(cal, models);
        test = Score(test, models);
        if (!ValidScores(cal) || !ValidScores(test))
        {
            return AdmissionDecision.Unknown("InvalidChoicePrediction", cal.Length, test.Length);
        }

        var noNeural = Calibrate(cal, caps.Alpha, 0);
        var selected = Calibrate(cal, caps.Alpha, models.Neural is null ? 0 : caps.Neural);
        if (selected.Neural > 0 && !HasNeuralIncrement(cal, selected, noNeural, LowerImprovement(cal, noNeural.Score, selected.Score)))
        {
            selected = noNeural;
        }

        var failure = reserve?.Invoke(test.Select(row => row.ObservationId).ToArray());
        if (failure is not null)
        {
            return AdmissionDecision.Unknown(failure, cal.Length, test.Length) with { Candidate = selected };
        }

        return Assess(cal, test, selected, noNeural, models.Incumbent is not null);
    }

    private static AdmissionDecision Assess(ChoiceRow[] calibration, ChoiceRow[] test, AdmissionMixture selected, AdmissionMixture noNeural, bool incumbent)
    {
        var lowerH = LowerImprovement(test, scores => scores.Heuristic, selected.Score);
        var lowerN = selected.Neural > 0 ? LowerImprovement(test, noNeural.Score, selected.Score) : (double?)null;
        var lowerI = incumbent ? LowerImprovement(test, scores => scores.Incumbent, selected.Score) : (double?)null;
        var accepted = lowerH > 0 && HasNeuralIncrement(test, selected, noNeural, lowerN) && (!incumbent || lowerI > 0)
            && RankingSafe(test, selected.Score, scores => scores.Heuristic)
            && CalibrationSafe(test, selected.Score, scores => scores.Heuristic);
        if (incumbent)
        {
            accepted &= RankingSafe(test, selected.Score, scores => scores.Incumbent)
                && CalibrationSafe(test, selected.Score, scores => scores.Incumbent);
        }

        return new AdmissionDecision(
            accepted,
            accepted ? "AcceptedChoiceRanking" : "RejectedChoiceRanking",
            selected,
            new AdmissionEvidence(calibration.Length, test.Length, EffectiveTitles(calibration), EffectiveTitles(test)),
            new AdmissionUtility(Mean(test, row => CalibrationLoss(row, selected.Score)), Mean(test, row => Ndcg(row, selected.Score, 20)), lowerH, lowerN, lowerI)
            {
                IsChoiceRanking = true,
                ChoiceRankingLoss = Mean(test, row => ChoiceLoss(row, selected.Score)),
                ChoiceRecallAtK = Mean(test, row => Rank(row, selected.Score) <= 10 ? 1 : 0)
            });
    }

    private static bool HasNeuralIncrement(ChoiceRow[] rows, AdmissionMixture candidate, AdmissionMixture noNeural, double? lower)
    {
        if (candidate.Neural <= 0)
        {
            return true;
        }

        return lower > 0 && RankingSafe(rows, candidate.Score, noNeural.Score)
            && Mean(rows, row => Ndcg(row, candidate.Score, 20)) > Mean(rows, row => Ndcg(row, noNeural.Score, 20)) + Tolerance;
    }

    private static ChoiceRow[] Capture(IReadOnlyList<TrainingExample> examples, DateTime referenceTime)
        => examples.Where(example => example.IsObserved && example.HasStableObservationId)
            .GroupBy(example => (example.UserId, example.ObservationId))
            .Select(group => EnsembleAdmissionEvaluator.SelectObservationView(group, referenceTime))
            .Where(example => example.Label > 0.5
                && example.ComputeEffectiveWeight(referenceTime) >= LearnedScoringStrategy.MinSampleWeight
                && example.Choice?.IsValid(example.ObservationId, referenceTime) == true)
            .Select(example => new ChoiceRow(
                example.UserId,
                example.ObservationId,
                example.Choice!.ChosenFeatures.ContentTasteKey,
                example.ComputeEffectiveWeight(referenceTime),
                example.Label,
                default,
                []) { Evidence = example.Choice.Copy() }).ToArray();

    private static bool Supported(ChoiceRow[] rows)
        => rows.Length >= EnsembleAdmissionEvaluator.MinimumEffectiveTitles
            && EffectiveTitles(rows) + Tolerance >= EnsembleAdmissionEvaluator.MinimumEffectiveTitles;

    private static double EffectiveTitles(ChoiceRow[] rows)
    {
        var weights = MacroUserWeights(rows);
        var sum = weights.Sum(row => row.Weight);
        var squares = weights.Sum(row => row.Weight * row.Weight);
        return squares > 0 ? sum * sum / squares : 0;
    }

    private static ChoiceRow[] Score(ChoiceRow[] rows, AdmissionModels models)
        => rows.Select(row => row with
        {
            Scores = Predict(row.Evidence!.ChosenFeatures, models),
            Alternatives = row.Evidence.Alternatives.Select(alternative => Predict(alternative.Features, models)).ToArray()
        }).ToArray();

    private static AdmissionScores Predict(CandidateFeatures features, AdmissionModels models)
        => new(models.Heuristic.Score(features), models.Learned.Score(features), models.Neural?.Score(features) ?? 0, models.Incumbent?.Score(features) ?? 0);

    private static bool ValidScores(ChoiceRow[] rows)
        => rows.All(row => Valid(row.Scores) && row.Alternatives.All(Valid));

    private static bool Valid(AdmissionScores scores)
        => Valid(scores.Heuristic) && Valid(scores.Learned) && Valid(scores.Neural) && Valid(scores.Incumbent);

    private static bool Valid(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static AdmissionMixture Calibrate(ChoiceRow[] rows, double alphaCap, double neuralCap)
    {
        AdmissionMixture best = default;
        var loss = Objective(rows, best);
        for (var learned = 0; learned <= 20; learned++)
        {
            for (var neural = 0; neural <= 10; neural++)
            {
                var candidate = new AdmissionMixture(alphaCap * learned / 20, neuralCap * neural / 10);
                if (candidate.Alpha > alphaCap + Tolerance)
                {
                    continue;
                }

                var candidateLoss = Objective(rows, candidate);
                if (candidateLoss < loss - Tolerance)
                {
                    best = candidate;
                    loss = candidateLoss;
                }
            }
        }

        return best;
    }

    private static double Objective(ChoiceRow[] rows, AdmissionMixture mixture)
        => Mean(rows, row => ChoiceLoss(row, mixture.Score) + CalibrationLoss(row, mixture.Score));

    private static double ChoiceLoss(ChoiceRow row, Func<AdmissionScores, double> score)
    {
        var chosen = score(row.Scores);
        // The query target is a relative choice, not a zero-valued outcome for every alternative.
        return row.Alternatives.Average(alternative => Math.Log(1 + Math.Exp(score(alternative) - chosen)));
    }

    private static double CalibrationLoss(ChoiceRow row, Func<AdmissionScores, double> score)
    {
        var error = score(row.Scores) - row.Label;
        return error * error;
    }

    private static int Rank(ChoiceRow row, Func<AdmissionScores, double> score)
    {
        var chosen = score(row.Scores);
        // A tie must not become an apparent successful discrimination of the chosen title.
        return 1 + row.Alternatives.Count(alternative => score(alternative) >= chosen - Tolerance);
    }

    private static double Ndcg(ChoiceRow row, Func<AdmissionScores, double> score, int k)
    {
        var rank = Rank(row, score);
        return rank <= k ? 1 / Math.Log2(rank + 1) : 0;
    }

    private static bool RankingSafe(ChoiceRow[] rows, Func<AdmissionScores, double> candidate, Func<AdmissionScores, double> baseline)
        => SafetyGroups(rows).All((ChoiceRow[] group) => new[] { RankingMetrics.DefaultK, 20 }.All((int k) =>
            Mean(group, row => Ndcg(row, candidate, k)) + Tolerance >= Mean(group, row => Ndcg(row, baseline, k)))
            && Mean(group, row => ChoiceLoss(row, candidate)) <= Mean(group, row => ChoiceLoss(row, baseline)) + Tolerance);

    private static bool CalibrationSafe(ChoiceRow[] rows, Func<AdmissionScores, double> candidate, Func<AdmissionScores, double> baseline)
        => rows.GroupBy(row => row.UserId).All(group => Mean(group.ToArray(), row => CalibrationLoss(row, candidate))
            <= Mean(group.ToArray(), row => CalibrationLoss(row, baseline)) + Tolerance);

    private static IEnumerable<ChoiceRow[]> SafetyGroups(ChoiceRow[] rows)
        => rows.GroupBy(row => row.UserId).Select(group => group.ToArray())
            .Concat(rows.Where(row => row.TasteKey.HasValue).GroupBy(row => (row.UserId, row.TasteKey)).Select(group => group.ToArray()));

    private static double Mean(ChoiceRow[] rows, Func<ChoiceRow, double> metric)
        => rows.GroupBy((ChoiceRow row) => row.UserId).Average((IGrouping<Guid, ChoiceRow> group) =>
        {
            var weighted = 0.0;
            var total = 0.0;
            foreach (var row in group)
            {
                weighted += row.Weight * metric(row);
                total += row.Weight;
            }

            return weighted / total;
        });

    private static double LowerImprovement(ChoiceRow[] rows, Func<AdmissionScores, double> baseline, Func<AdmissionScores, double> candidate)
    {
        var gains = MacroUserWeights(rows).Select(row =>
            (Gain: ChoiceLoss(row.Row, baseline) - ChoiceLoss(row.Row, candidate), row.Weight)).ToArray();
        var mean = gains.Sum(value => value.Gain * value.Weight);
        var variance = gains.Sum(value => value.Weight * (value.Gain - mean) * (value.Gain - mean));
        var effective = 1.0 / gains.Sum(value => value.Weight * value.Weight);
        return mean - (ConfidenceMultiplier * Math.Sqrt(variance / effective));
    }

    private static (ChoiceRow Row, double Weight)[] MacroUserWeights(ChoiceRow[] rows)
    {
        var users = rows.GroupBy(row => row.UserId).ToArray();
        return users.SelectMany((IGrouping<Guid, ChoiceRow> group) =>
        {
            var total = group.Sum((ChoiceRow row) => row.Weight) * users.Length;
            return group.Select((ChoiceRow row) => (Row: row, Weight: row.Weight / total));
        }).ToArray();
    }

    private sealed record ChoiceRow(Guid UserId, Guid ObservationId, Guid? TasteKey, double Weight, double Label, AdmissionScores Scores, AdmissionScores[] Alternatives)
    {
        public RecommendationChoiceEvidence? Evidence { get; init; }
    }
}
