using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Calibrates one candidate and independently screens its calibration and per-user ranking utility.</summary>
internal static class EnsembleAdmissionEvaluator
{
    /// <summary>Minimum effective independent titles in each coefficient-selection and acceptance slice.</summary>
    internal const int MinimumEffectiveTitles = 20;

    /// <summary>Deterministic paired resamples; predictions are never recomputed inside this loop.</summary>
    internal const int BootstrapReplicates = 2048;

    /// <summary>Bounds one evaluation/archived evidence block, not the lifetime number of spent titles.</summary>
    internal const int MaximumEvaluationTitles = 4096;

    private const double StandardErrorMultiplier = 2.5;
    private const int BootstrapTailDivisor = 60;
    private const int FullListRankingK = 20;
    private const double MetricRoundingTolerance = 1e-12;

    /// <summary>Evaluates one preselected mixture; rejection never reselects coefficients using acceptance labels.</summary>
    /// <param name="calibration">Independent coefficient-selection observations.</param>
    /// <param name="acceptance">Separate final observations, not used for fitting or calibration.</param>
    /// <param name="models">Detached/coherent prediction sources.</param>
    /// <param name="referenceTime">The fixed time for evidence eligibility and decay.</param>
    /// <param name="alphaCap">The unchanged sigmoid maturity ceiling.</param>
    /// <param name="neuralCap">The absolute Neural influence ceiling after its existing maturity ramp.</param>
    /// <returns>An immutable decision; applying or persisting it is a separate lifecycle responsibility.</returns>
    internal static AdmissionDecision Evaluate(
        IReadOnlyList<TrainingExample> calibration,
        IReadOnlyList<TrainingExample> acceptance,
        AdmissionModels models,
        DateTime referenceTime,
        double alphaCap,
        double neuralCap)
    {
        ValidateAdmissionInputs(calibration, acceptance, models, alphaCap, neuralCap);
        var cal = CaptureEvidence(calibration, referenceTime);
        var test = CaptureEvidence(acceptance, referenceTime);
        if (cal.Select(row => (row.UserId, row.ObservationId)).Intersect(test.Select(row => (row.UserId, row.ObservationId))).Any())
        {
            return AdmissionDecision.Unknown("OverlappingEvidence", cal.Length, test.Length);
        }

        var plan = PrepareCalibration(cal, test.Length, models, alphaCap, neuralCap);
        if (plan.Limitation is { } limitation)
        {
            return limitation;
        }

        var testUsers = MixedUsers(test).ToHashSet();
        test = test.Where(row => plan.Users.Contains(row.UserId) && testUsers.Contains(row.UserId)).ToArray();
        if (!HasSupport(test))
        {
            var reason = testUsers.Count == 0 ? "HomogeneousObservedOutcomes" : "InsufficientIndependentRankingEvidence";
            return AdmissionDecision.Unknown(reason, plan.Rows.Length, test.Length) with { Candidate = plan.Selected };
        }

        test = ScoreEvidence(test, models, neuralCap > 0);
        if (!ValidScores(test))
        {
            return AdmissionDecision.Unknown("InvalidPrediction", plan.Rows.Length, test.Length) with { Candidate = plan.Selected };
        }

        return AssessSelectedMixture(plan, test, models.Incumbent is not null);
    }

    private static void ValidateAdmissionInputs(IReadOnlyList<TrainingExample> calibration, IReadOnlyList<TrainingExample> acceptance, AdmissionModels models, double alphaCap, double neuralCap)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(acceptance);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(models.Heuristic);
        ArgumentNullException.ThrowIfNull(models.Learned);
        if (!double.IsFinite(alphaCap) || alphaCap is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alphaCap));
        }

        if (!double.IsFinite(neuralCap) || neuralCap < 0 || neuralCap > alphaCap)
        {
            throw new ArgumentOutOfRangeException(nameof(neuralCap));
        }
    }

    private static CalibrationPlan PrepareCalibration(EvidenceRow[] cal, int acceptanceTitles, AdmissionModels models, double alphaCap, double neuralCap)
    {
        // A cross-user positive/negative difference is not evidence that either user's own ranking is useful.
        var eligibleUsers = MixedUsers(cal).ToHashSet();
        if (eligibleUsers.Count == 0)
        {
            return new CalibrationPlan(cal, eligibleUsers, default, default, AdmissionDecision.Unknown("HomogeneousObservedOutcomes", cal.Length, acceptanceTitles));
        }

        cal = cal.Where(row => eligibleUsers.Contains(row.UserId)).ToArray();
        if (!HasSupport(cal))
        {
            return new CalibrationPlan(cal, eligibleUsers, default, default, AdmissionDecision.Unknown("InsufficientIndependentRankingEvidence", cal.Length, acceptanceTitles));
        }

        // Selection depends exclusively on calibration, including its user eligibility and prediction checks.
        cal = ScoreEvidence(cal, models, neuralCap > 0);
        if (!ValidScores(cal))
        {
            return new CalibrationPlan(cal, eligibleUsers, default, default, AdmissionDecision.Unknown("InvalidPrediction", cal.Length, acceptanceTitles));
        }

        var noNeural = Calibrate(cal, alphaCap, 0);
        var selected = Calibrate(cal, alphaCap, models.Neural is null ? 0 : neuralCap);
        if (selected.Neural > 0 && !HasNeuralIncrement(cal, selected, noNeural))
        {
            selected = noNeural;
        }

        return new CalibrationPlan(cal, eligibleUsers, selected, noNeural, null);
    }

    private static bool ValidScores(EvidenceRow[] rows)
        => rows.All(row => ValidPrediction(row.Scores.Heuristic) && ValidPrediction(row.Scores.Learned)
            && ValidPrediction(row.Scores.Neural) && ValidPrediction(row.Scores.Incumbent));

    private static AdmissionDecision AssessSelectedMixture(CalibrationPlan plan, EvidenceRow[] test, bool hasIncumbent)
    {
        var selected = plan.Selected;
        AdmissionMixture heuristic = default;
        var lowerH = ImprovementLowerBound(test, row => row.Scores.Heuristic, row => selected.Score(row.Scores));
        var lowerN = selected.Neural > 0
            ? ImprovementLowerBound(test, row => plan.NoNeural.Score(row.Scores), row => selected.Score(row.Scores))
            : (double?)null;
        var lowerIncumbent = hasIncumbent
            ? ImprovementLowerBound(test, row => row.Scores.Incumbent, row => selected.Score(row.Scores))
            : (double?)null;
        var accepted = lowerH > 0 && RankSafe(test, selected, heuristic)
            && (selected.Neural <= 0 || (lowerN > 0 && HasNeuralIncrement(test, selected, plan.NoNeural)))
            && (!hasIncumbent || (lowerIncumbent > 0 && RankSafeAgainstIncumbent(test, selected)));

        return new AdmissionDecision(
            accepted,
            accepted ? "Accepted" : "RejectedIndependentUtility",
            selected,
            new AdmissionEvidence(plan.Rows.Length, test.Length, EffectiveTitles(plan.Rows), EffectiveTitles(test)),
            new AdmissionUtility(
                Loss(test, row => selected.Score(row.Scores)),
                MeanNdcg(test, row => selected.Score(row.Scores), FullListRankingK),
                lowerH,
                lowerN,
                lowerIncumbent)
            {
                CandidatePrecisionAtK = MeanRankingMetric(test, row => selected.Score(row.Scores), RankingMetrics.ComputePrecisionAtK),
                CandidateRecallAtK = MeanRankingMetric(test, row => selected.Score(row.Scores), RankingMetrics.ComputeRecallAtK),
                CandidateNdcgAtK = MeanNdcg(test, row => selected.Score(row.Scores), RankingMetrics.DefaultK)
            });
    }

    private static bool ValidPrediction(double score) => double.IsFinite(score) && score is >= 0 and <= 1;

    /// <summary>Selects a deterministic, user-balanced block without inspecting outcomes or prediction quality.</summary>
    /// <param name="examples">One partition, already excluding spent acceptance identities where applicable.</param>
    /// <returns>A bounded block; unused new identities remain available for later independent rounds.</returns>
    internal static TrainingExample[] SelectEvaluationBlock(IEnumerable<TrainingExample> examples)
    {
        var users = examples.GroupBy(row => (row.UserId, row.ObservationId))
            .Select(group => group.OrderByDescending(row => row.GeneratedAtUtc).First())
            .GroupBy(row => row.UserId).OrderBy(group => group.Key)
            .Select(group => group.OrderBy(row => row.ObservationId).ToArray()).ToArray();
        var selected = new List<TrainingExample>();
        var depth = users.Length == 0 ? 0 : Math.Min(users.Max(rows => rows.Length), MaximumEvaluationTitles);
        for (var index = 0; index < depth; index++)
        {
            foreach (var user in users.Where(rows => index < rows.Length))
            {
                selected.Add(user[index]);
                if (selected.Count == MaximumEvaluationTitles)
                {
                    return selected.ToArray();
                }
            }
        }

        return selected.ToArray();
    }

    /// <summary>Tests new observed counterevidence against a pinned incumbent, independently of challenger success.</summary>
    /// <param name="acceptance">Fresh acceptance outcomes.</param>
    /// <param name="heuristic">The actual heuristic reference.</param>
    /// <param name="incumbent">Pinned current model predictions.</param>
    /// <param name="referenceTime">Fixed eligibility/decay time.</param>
    /// <returns>Withdrawal evidence, or null when harm is unknown or not established.</returns>
    internal static AdmissionUtility? EvaluateWithdrawal(IReadOnlyList<TrainingExample> acceptance, IScoringStrategy heuristic, IScoringStrategy incumbent, DateTime referenceTime)
    {
        var rows = CaptureEvidence(acceptance, referenceTime);
        var mixed = MixedUsers(rows).ToHashSet();
        rows = rows.Where(row => mixed.Contains(row.UserId)).ToArray();
        if (!HasSupport(rows))
        {
            return null;
        }

        rows = ScoreEvidence(rows, new AdmissionModels(heuristic, heuristic, Incumbent: incumbent), false);
        if (rows.Any(row => !ValidPrediction(row.Scores.Heuristic) || !ValidPrediction(row.Scores.Incumbent)))
        {
            return null;
        }

        var lower = ImprovementLowerBound(rows, row => row.Scores.Incumbent, row => row.Scores.Heuristic);
        if (lower <= 0 || !RankSafe(rows, row => row.Scores.Heuristic, row => row.Scores.Incumbent))
        {
            return null;
        }

        return new AdmissionUtility(Loss(rows, row => row.Scores.Heuristic), MeanNdcg(rows, row => row.Scores.Heuristic, FullListRankingK), null, null, lower);
    }

    /// <summary>Measures a fixed historical predictor; coefficients are never retuned on restoration acceptance labels.</summary>
    /// <param name="examples">Independent observed examples.</param>
    /// <param name="heuristic">Actual heuristic reference.</param>
    /// <param name="candidate">A fixed historical version.</param>
    /// <param name="incumbent">The current fixed version.</param>
    /// <param name="referenceTime">Fixed effective-weight reference.</param>
    /// <returns>Calibration loss or independent restoration utility; unknown support is represented by null.</returns>
    internal static AdmissionUtility? EvaluateFixedVersion(IReadOnlyList<TrainingExample> examples, IScoringStrategy heuristic, IScoringStrategy candidate, IScoringStrategy incumbent, DateTime referenceTime)
    {
        var rows = CaptureEvidence(examples, referenceTime);
        var users = MixedUsers(rows).ToHashSet();
        rows = rows.Where(row => users.Contains(row.UserId)).ToArray();
        if (!HasSupport(rows))
        {
            return null;
        }

        rows = ScoreEvidence(rows, new AdmissionModels(heuristic, candidate, Incumbent: incumbent), false);
        if (rows.Any(row => !ValidPrediction(row.Scores.Heuristic) || !ValidPrediction(row.Scores.Learned) || !ValidPrediction(row.Scores.Incumbent)))
        {
            return null;
        }

        var lowerH = ImprovementLowerBound(rows, row => row.Scores.Heuristic, row => row.Scores.Learned);
        var lowerCurrent = ImprovementLowerBound(rows, row => row.Scores.Incumbent, row => row.Scores.Learned);
        if (!RankSafe(rows, row => row.Scores.Learned, row => row.Scores.Heuristic)
            || !RankSafe(rows, row => row.Scores.Learned, row => row.Scores.Incumbent))
        {
            return null;
        }

        return new AdmissionUtility(Loss(rows, row => row.Scores.Learned), MeanNdcg(rows, row => row.Scores.Learned, FullListRankingK), lowerH, null, lowerCurrent);
    }

    /// <summary>Selects the strongest available no-Neural reference on calibration alone, never on restoration acceptance.</summary>
    /// <param name="calibration">Coefficient/reference selection evidence.</param>
    /// <param name="heuristic">Actual penalized heuristic.</param>
    /// <param name="learnedVersions">Pinned challenger, incumbent and historical linear predictors.</param>
    /// <param name="referenceTime">Fixed evidence eligibility/decay time.</param>
    /// <param name="alphaCap">The unchanged permitted ML ceiling.</param>
    /// <returns>A fixed no-Neural alternative, or null for insufficient/invalid calibration.</returns>
    internal static IScoringStrategy? CalibrateNoNeuralReference(IReadOnlyList<TrainingExample> calibration, IScoringStrategy heuristic, IReadOnlyList<IScoringStrategy> learnedVersions, DateTime referenceTime, double alphaCap)
    {
        var rows = CaptureEvidence(calibration, referenceTime);
        var users = MixedUsers(rows).ToHashSet();
        rows = rows.Where(row => users.Contains(row.UserId)).ToArray();
        if (!HasSupport(rows))
        {
            return null;
        }

        CalibratedNoNeuralPredictor? selected = null;
        var selectedLoss = double.PositiveInfinity;
        foreach (var learned in learnedVersions)
        {
            var scored = ScoreEvidence(rows, new AdmissionModels(heuristic, learned), false);
            if (scored.Any(row => !ValidPrediction(row.Scores.Heuristic) || !ValidPrediction(row.Scores.Learned)))
            {
                continue;
            }

            var mixture = Calibrate(scored, alphaCap, 0);
            var loss = Loss(scored, row => mixture.Score(row.Scores));
            if (loss < selectedLoss)
            {
                selected = new CalibratedNoNeuralPredictor(heuristic, learned, mixture);
                selectedLoss = loss;
            }
        }

        return selected;
    }

    /// <summary>Screens a fixed historical Neural mixture against its calibration-selected no-Neural alternative on new evidence.</summary>
    /// <param name="examples">Calibration or separate fresh acceptance evidence.</param>
    /// <param name="heuristic">Actual heuristic reference.</param>
    /// <param name="candidate">Fixed archived complete mixture.</param>
    /// <param name="noNeural">The fixed reference chosen on calibration.</param>
    /// <param name="referenceTime">Fixed eligibility/decay time.</param>
    /// <returns>Fresh positive incremental loss evidence with ranking gain, or null when unknown/harmful/redundant.</returns>
    internal static double? EvaluateFixedNeuralIncrement(IReadOnlyList<TrainingExample> examples, IScoringStrategy heuristic, IScoringStrategy candidate, IScoringStrategy noNeural, DateTime referenceTime)
    {
        var rows = CaptureEvidence(examples, referenceTime);
        var users = MixedUsers(rows).ToHashSet();
        rows = rows.Where(row => users.Contains(row.UserId)).ToArray();
        if (!HasSupport(rows))
        {
            return null;
        }

        rows = ScoreEvidence(rows, new AdmissionModels(heuristic, candidate, Incumbent: noNeural), false);
        if (rows.Any(row => !ValidPrediction(row.Scores.Learned) || !ValidPrediction(row.Scores.Incumbent)))
        {
            return null;
        }

        var lower = ImprovementLowerBound(rows, row => row.Scores.Incumbent, row => row.Scores.Learned);
        if (lower > 0 && RankSafe(rows, row => row.Scores.Learned, row => row.Scores.Incumbent)
            && MeanNdcg(rows, row => row.Scores.Learned, FullListRankingK) > MeanNdcg(rows, row => row.Scores.Incumbent, FullListRankingK) + MetricRoundingTolerance)
        {
            return lower;
        }

        return null;
    }

    private static EvidenceRow[] CaptureEvidence(IReadOnlyList<TrainingExample> examples, DateTime referenceTime)
        => examples.Where(example => example.IsObserved && example.HasStableObservationId && example.ObservationId != Guid.Empty
                && example.ComputeEffectiveWeight(referenceTime) >= LearnedScoringStrategy.MinSampleWeight)
            .GroupBy(example => (example.UserId, example.ObservationId))
            .Select(group => group.OrderByDescending(example => example.GeneratedAtUtc).First())
            .OrderBy(example => example.UserId).ThenBy(example => example.ObservationId)
            .Select(example => new EvidenceRow(
                example.UserId,
                example.ObservationId,
                example.Label,
                example.ComputeEffectiveWeight(referenceTime),
                example.Features,
                default))
            .ToArray();

    private static EvidenceRow[] ScoreEvidence(EvidenceRow[] rows, AdmissionModels models, bool scoreNeural)
        => rows.Select(row => row with
        {
            Scores = new AdmissionScores(
                models.Heuristic.Score(row.Features),
                models.Learned.Score(row.Features),
                scoreNeural && models.Neural is not null ? models.Neural.Score(row.Features) : 0,
                models.Incumbent?.Score(row.Features) ?? 0)
        }).ToArray();

    private static IEnumerable<Guid> MixedUsers(EvidenceRow[] rows)
        => rows.GroupBy(row => row.UserId).Where(group => group.Any(row => row.Label > RankingMetrics.DefaultRelevanceThreshold)
            && group.Any(row => row.Label <= RankingMetrics.DefaultRelevanceThreshold)).Select(group => group.Key);

    private static bool HasSupport(EvidenceRow[] rows)
        => rows.Length >= MinimumEffectiveTitles && EffectiveTitles(rows) + MetricRoundingTolerance >= MinimumEffectiveTitles;

    private static double[] MacroWeights(EvidenceRow[] rows)
    {
        var totals = rows.GroupBy(row => row.UserId).ToDictionary(group => group.Key, group => group.Sum(row => row.Weight));
        return rows.Select(row => row.Weight / totals[row.UserId] / totals.Count).ToArray();
    }

    private static double EffectiveTitles(EvidenceRow[] rows)
    {
        var weights = MacroWeights(rows);
        return weights.Length == 0 ? 0 : Math.Pow(weights.Sum(), 2) / weights.Sum(weight => weight * weight);
    }

    private static double Loss(EvidenceRow[] rows, Func<EvidenceRow, double> score)
    {
        var weights = MacroWeights(rows);
        return rows.Select((row, index) => weights[index] * Math.Pow(score(row) - row.Label, 2)).Sum();
    }

    private static AdmissionMixture Calibrate(EvidenceRow[] rows, double alphaCap, double neuralCap)
    {
        var form = QuadraticForm.Create(rows, MacroWeights(rows));
        var vertices = new[]
        {
            default(AdmissionMixture),
            new AdmissionMixture(alphaCap, 0),
            new AdmissionMixture(alphaCap - neuralCap, neuralCap),
            new AdmissionMixture(0, neuralCap)
        };
        var candidates = new List<AdmissionMixture>(vertices);
        if (form.InteriorSolution(alphaCap, neuralCap) is { } interior)
        {
            candidates.Add(interior);
        }

        for (var i = 0; i < vertices.Length; i++)
        {
            candidates.Add(form.ProjectEdge(vertices[i], vertices[(i + 1) % vertices.Length]));
        }

        var best = candidates.OrderBy(mixture => Loss(rows, row => mixture.Score(row.Scores))).First();
        var baseline = Loss(rows, row => row.Scores.Heuristic);
        var bestLoss = Loss(rows, row => best.Score(row.Scores));
        var trust = baseline > 0 ? Math.Clamp((baseline - bestLoss) / baseline, 0, 1) : 0;
        return new AdmissionMixture(best.Learned * trust, best.Neural * trust);
    }

    private static bool HasNeuralIncrement(EvidenceRow[] rows, AdmissionMixture candidate, AdmissionMixture reference)
        => ImprovementLowerBound(rows, row => reference.Score(row.Scores), row => candidate.Score(row.Scores)) > 0
            && RankSafe(rows, candidate, reference)
            && MeanNdcg(rows, row => candidate.Score(row.Scores), FullListRankingK)
                > MeanNdcg(rows, row => reference.Score(row.Scores), FullListRankingK) + MetricRoundingTolerance;

    private static bool RankSafe(EvidenceRow[] rows, AdmissionMixture candidate, AdmissionMixture reference)
        => RankSafe(rows, row => candidate.Score(row.Scores), row => reference.Score(row.Scores));

    private static bool RankSafe(EvidenceRow[] rows, Func<EvidenceRow, double> candidate, Func<EvidenceRow, double> reference)
        => rows.GroupBy(row => row.UserId).All(group => new[] { RankingMetrics.DefaultK, FullListRankingK }
            .All(k => Ndcg(group.ToArray(), candidate, k) + MetricRoundingTolerance >= Ndcg(group.ToArray(), reference, k)));

    private static bool RankSafeAgainstIncumbent(EvidenceRow[] rows, AdmissionMixture candidate)
        => RankSafe(rows, row => candidate.Score(row.Scores), row => row.Scores.Incumbent);

    private static double Ndcg(EvidenceRow[] rows, Func<EvidenceRow, double> score, int k)
        => RankingMetrics.ComputeNdcgAtK(rows.Select(score).ToArray(), rows.Select(row => row.Label).ToArray(), k);

    private static double MeanNdcg(EvidenceRow[] rows, Func<EvidenceRow, double> score, int k)
        => rows.GroupBy(row => row.UserId).Average(group => Ndcg(group.ToArray(), score, k));

    private static double MeanRankingMetric(EvidenceRow[] rows, Func<EvidenceRow, double> score, Func<double[], double[], int, double, double> metric)
        => rows.GroupBy(row => row.UserId).Average(group => metric(group.Select(score).ToArray(), group.Select(row => row.Label).ToArray(), RankingMetrics.DefaultK, RankingMetrics.DefaultRelevanceThreshold));

    private static double ImprovementLowerBound(EvidenceRow[] rows, Func<EvidenceRow, double> reference, Func<EvidenceRow, double> candidate)
    {
        var weights = MacroWeights(rows);
        var differences = rows.Select(row => Math.Pow(reference(row) - row.Label, 2) - Math.Pow(candidate(row) - row.Label, 2)).ToArray();
        var mean = differences.Select((difference, index) => weights[index] * difference).Sum();
        var squaredWeights = weights.Sum(weight => weight * weight);
        var variance = differences.Select((difference, index) => weights[index] * Math.Pow(difference - mean, 2)).Sum() / (1 - squaredWeights);
        var standardErrorBound = mean - (StandardErrorMultiplier * Math.Sqrt(variance * squaredWeights));
        var groups = rows.Select((row, index) => (row.Weight, Index: index)).GroupBy(item => rows[item.Index].UserId)
            .Select(group => group.ToArray()).ToArray();
        // A deterministic statistical resampler, not a security or session-identity generator.
        var random = new Random(9181);
        var samples = new double[BootstrapReplicates];
        for (var trial = 0; trial < samples.Length; trial++)
        {
            var total = 0.0;
            foreach (var group in groups)
            {
                var numerator = 0.0;
                var denominator = 0.0;
                for (var draw = 0; draw < group.Length; draw++)
                {
                    var item = group[random.Next(group.Length)];
                    numerator += item.Weight * differences[item.Index];
                    denominator += item.Weight;
                }

                total += numerator / denominator;
            }

            samples[trial] = total / groups.Length;
        }

        Array.Sort(samples);
        return Math.Min(standardErrorBound, samples[samples.Length / BootstrapTailDivisor]);
    }

    private readonly record struct EvidenceRow(Guid UserId, Guid ObservationId, double Label, double Weight, CandidateFeatures Features, AdmissionScores Scores);

    private readonly record struct QuadraticForm(double AA, double AB, double BB, double AY, double BY)
    {
        internal static QuadraticForm Create(EvidenceRow[] rows, double[] weights)
        {
            double aa = 0, ab = 0, bb = 0, ay = 0, by = 0;
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i];
                var a = row.Scores.Learned - row.Scores.Heuristic;
                var b = row.Scores.Neural - row.Scores.Heuristic;
                var target = row.Label - row.Scores.Heuristic;
                var w = weights[i];
                aa += w * a * a;
                ab += w * a * b;
                bb += w * b * b;
                ay += w * a * target;
                by += w * b * target;
            }

            return new QuadraticForm(aa, ab, bb, ay, by);
        }

        internal AdmissionMixture? InteriorSolution(double alphaCap, double neuralCap)
        {
            var determinant = (AA * BB) - (AB * AB);
            if (determinant <= 0)
            {
                return null;
            }

            var x = ((AY * BB) - (BY * AB)) / determinant;
            var y = ((BY * AA) - (AY * AB)) / determinant;
            return x >= 0 && y >= 0 && x + y <= alphaCap && y <= neuralCap ? new AdmissionMixture(x, y) : null;
        }

        internal AdmissionMixture ProjectEdge(AdmissionMixture start, AdmissionMixture end)
        {
            var dx = end.Learned - start.Learned;
            var dy = end.Neural - start.Neural;
            var denominator = (AA * dx * dx) + (2 * AB * dx * dy) + (BB * dy * dy);
            var numerator = (dx * (AY - (AA * start.Learned) - (AB * start.Neural)))
                + (dy * (BY - (AB * start.Learned) - (BB * start.Neural)));
            var t = denominator > 0 ? Math.Clamp(numerator / denominator, 0, 1) : 0;
            return new AdmissionMixture(start.Learned + (t * dx), start.Neural + (t * dy));
        }
    }

    private sealed record CalibrationPlan(EvidenceRow[] Rows, HashSet<Guid> Users, AdmissionMixture Selected, AdmissionMixture NoNeural, AdmissionDecision? Limitation);

    private sealed class CalibratedNoNeuralPredictor(IScoringStrategy heuristic, IScoringStrategy learned, AdmissionMixture mixture) : IScoringStrategy
    {
        public string Name => "Calibrated no-Neural reference";

        public string NameKey => "strategyEnsemble";

        public double Score(CandidateFeatures features) => mixture.Score(new AdmissionScores(heuristic.Score(features), learned.Score(features), 0, 0));

        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => ScoringHelper.BuildMarginalExplanation(features, Score, Name);
    }
}
