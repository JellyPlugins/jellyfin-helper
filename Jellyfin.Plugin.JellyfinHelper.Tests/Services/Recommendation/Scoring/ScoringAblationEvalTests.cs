using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Offline ablation evaluation of the recommendation scoring pipeline. This is a measurement
///     harness (not a single-method unit test): it builds a deterministic synthetic population whose
///     hidden per-user taste drives BOTH the ground-truth labels AND the engagement features, then
///     measures whether the newer genre-engagement + SeriesAffinity signals actually improve ranking
///     quality (NDCG@10) versus an ablated feature set where those signals are reset to neutral.
/// </summary>
public class ScoringAblationEvalTests(ITestOutputHelper output)
{
    /// <summary>Number of synthetic users in the small (heuristic / learned) population.</summary>
    private const int SmallUserCount = 40;

    /// <summary>Candidate examples per user in the small population (mix of liked + disliked).</summary>
    private const int SmallExamplesPerUser = 40;

    /// <summary>Fixed generation timestamp so temporal-decay weighting is identical across arms.</summary>
    private static readonly DateTime FixedGeneratedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     Tier 1 - the fixed-weight heuristic ranker. The new engagement/affinity signals must
    ///     STRICTLY improve NDCG@10 on data where taste drives both the label and the features.
    ///     A failure here is a real finding, not a flaky assertion.
    /// </summary>
    [Fact]
    public void Tier1_Heuristic_RealFeatures_StrictlyBeatsAblated()
    {
        var examples = BuildPopulation(SmallUserCount, SmallExamplesPerUser);
        var ablated = Ablate(examples);

        var real = RankingMetrics.ComputeAll(examples, new HeuristicScoringStrategy());
        var neutral = RankingMetrics.ComputeAll(ablated, new HeuristicScoringStrategy());

        WriteRow("Heuristic", real.NdcgAtK, neutral.NdcgAtK);

        Assert.True(
            real.NdcgAtK > neutral.NdcgAtK,
            FormattableString.Invariant(
                $"Tier1 (Heuristic): expected NDCG@10 with REAL features ({real.NdcgAtK:F4}) to STRICTLY exceed ablated ({neutral.NdcgAtK:F4}). The genre-engagement + SeriesAffinity signals are not helping the fixed-weight cold-start ranker."));
    }

    /// <summary>
    ///     Tier 2 - the Heuristic + Learned (SGD) blend. Each arm trains on its own feature set so
    ///     the comparison is fair. A learned model may wash out a redundant signal, so we require
    ///     "not meaningfully worse" (>= ablated - epsilon) rather than strict improvement.
    /// </summary>
    [Fact]
    public void Tier2_HeuristicLearned_RealFeatures_NotMeaningfullyWorseThanAblated()
    {
        const double epsilon = 0.02;
        var examples = BuildPopulation(SmallUserCount, SmallExamplesPerUser);
        var ablated = Ablate(examples);

        using var realEnsemble = new EnsembleScoringStrategy(weightsPath: null);
        realEnsemble.Train(examples);

        using var ablatedEnsemble = new EnsembleScoringStrategy(weightsPath: null);
        ablatedEnsemble.Train(ablated);

        var real = RankingMetrics.ComputeAll(examples, realEnsemble);
        var neutral = RankingMetrics.ComputeAll(ablated, ablatedEnsemble);

        WriteRow("Heuristic+Learned", real.NdcgAtK, neutral.NdcgAtK);

        Assert.True(
            real.NdcgAtK >= neutral.NdcgAtK - epsilon,
            FormattableString.Invariant(
                $"Tier2 (Heuristic+Learned): NDCG@10 real ({real.NdcgAtK:F4}) is more than {epsilon:F2} below ablated ({neutral.NdcgAtK:F4}); the new signals meaningfully hurt the learned blend."));
    }

    /// <summary>
    ///     Tier 3 - the full ensemble including the neural (MLP) sub-strategy. We use the multi-arg
    ///     constructor with an explicit <see cref="NeuralScoringStrategy"/> and train with enough
    ///     examples (well above NeuralActivationThreshold = 150) over a couple of rounds so the neural
    ///     blending factor beta ramps up and the neural path is genuinely exercised. As with Tier 2,
    ///     we require "not meaningfully worse".
    /// </summary>
    [Fact]
    public void Tier3_FullEnsembleWithNeural_RealFeatures_NotMeaningfullyWorseThanAblated()
    {
        const double epsilon = 0.02;
        var examples = BuildPopulation(SmallUserCount, SmallExamplesPerUser);
        var ablated = Ablate(examples);

        using var realEnsemble = BuildNeuralEnsemble();
        using var ablatedEnsemble = BuildNeuralEnsemble();

        // Train each arm over a couple of rounds so the cumulative example count crosses the neural
        // activation threshold (150) and beta ramps above zero, exercising the neural blend. Two rounds
        // already push the cumulative count well past the threshold; more rounds only multiply the MLP
        // training cost (76-96-48-24 over 1600 examples, Adam, 50 epochs) without changing what the test
        // proves, and made the run hang long enough to trip test timeouts.
        for (var round = 0; round < 2; round++)
        {
            realEnsemble.Train(examples);
            ablatedEnsemble.Train(ablated);
        }

        var real = RankingMetrics.ComputeAll(examples, realEnsemble);
        var neutral = RankingMetrics.ComputeAll(ablated, ablatedEnsemble);

        WriteRow("Full Ensemble (+Neural)", real.NdcgAtK, neutral.NdcgAtK);
        output.WriteLine(FormattableString.Invariant(
            $"    neuralBeta real={realEnsemble.CurrentNeuralBeta:F4} ablated={ablatedEnsemble.CurrentNeuralBeta:F4}, trainingExamples={realEnsemble.TrainingExampleCount}"));

        Assert.True(
            real.NdcgAtK >= neutral.NdcgAtK - epsilon,
            FormattableString.Invariant(
                $"Tier3 (Full Ensemble +Neural): NDCG@10 real ({real.NdcgAtK:F4}) is more than {epsilon:F2} below ablated ({neutral.NdcgAtK:F4}); the new signals meaningfully hurt the full ensemble."));
    }

    /// <summary>
    ///     Builds a full 3-way ensemble (Heuristic + Learned + Neural) via the multi-arg constructor,
    ///     all sub-strategies disk-less. The heuristic sub-strategy must disable its own genre penalty
    ///     (floor 1.0) because the ensemble applies the penalty centrally.
    /// </summary>
    private static EnsembleScoringStrategy BuildNeuralEnsemble()
    {
        return new EnsembleScoringStrategy(
            new LearnedScoringStrategy(weightsPath: null),
            new HeuristicScoringStrategy(genrePenaltyFloor: 1.0),
            new NeuralScoringStrategy(weightsPath: null, logger: null),
            statePath: null);
    }

    /// <summary>
    ///     Builds a deterministic synthetic population. Each user has a hidden taste: one genre they
    ///     COMPLETE (liked) and one they ABANDON (disliked). For every candidate we first pick the
    ///     taste category (liked / disliked), then derive the label from that category and derive the
    ///     engagement FEATURES from the same category through realistic quantities.
    /// </summary>
    /// <remarks>
    ///     VALIDITY GUARDRAIL: the label is NOT a copy of any engagement feature. Both the label and
    ///     the features are computed independently from the hidden taste category. They correlate only
    ///     because they share that common cause - which is exactly the real-world structure we want to
    ///     measure - rather than via a trivial label := feature identity that would make the ablation
    ///     meaningless.
    /// </remarks>
    /// <param name="userCount">Number of synthetic users.</param>
    /// <param name="examplesPerUser">Candidate examples per user (split roughly evenly liked/disliked).</param>
    /// <returns>The labelled training examples for the whole population.</returns>
    private static List<TrainingExample> BuildPopulation(int userCount, int examplesPerUser)
    {
        var rng = new Random(12345);
        var examples = new List<TrainingExample>(userCount * examplesPerUser);

        for (var u = 0; u < userCount; u++)
        {
            // Deterministic per-user id so RankingMetrics groups examples per user.
            var idBytes = new byte[16];
            idBytes[0] = (byte)(u + 1);
            idBytes[1] = (byte)((u + 1) >> 8);
            var userId = new Guid(idBytes);

            for (var e = 0; e < examplesPerUser; e++)
            {
                // Hidden taste category: alternate liked/disliked so each user gets a balanced mix.
                var isLiked = e % 2 == 0;

                // --- Label: derived from taste category only (independent of the feature math) ---
                var labelNoise = (rng.NextDouble() * 0.2) - 0.1; // +/-0.1 seeded jitter
                var label = isLiked
                    ? Math.Clamp(0.9 + labelNoise, 0.0, 1.0) // liked -> ~0.8-1.0
                    : Math.Clamp(0.1 + labelNoise, 0.0, 1.0); // disliked -> ~0.0-0.2

                // --- Features: derived from the SAME taste category via realistic quantities ---
                // Independent seeded noise per feature so features are not a copy of the label.
                var completion = isLiked
                    ? 0.85 + ((rng.NextDouble() - 0.5) * 0.2) // high completion for liked genre
                    : 0.10 + ((rng.NextDouble() - 0.5) * 0.15); // low completion (abandoned) for disliked
                completion = Math.Clamp(completion, 0.0, 1.0);

                var abandonRate = isLiked
                    ? 0.05 + (rng.NextDouble() * 0.10) // rarely abandons liked genre
                    : 0.80 + (rng.NextDouble() * 0.15); // frequently abandons disliked genre
                abandonRate = Math.Clamp(abandonRate, 0.0, 1.0);

                // GenreSimilarity is deliberately AMBIGUOUS: liked and disliked overlap heavily
                // around 0.45-0.60, so this shared (non-ablated) signal cannot separate the classes
                // on its own. That leaves genuine ranking headroom for the new engagement + affinity
                // signals to disambiguate - which is exactly what the ablation measures. It still
                // correlates weakly with taste (a slight lift for liked) because both share the cause.
                var genreSimilarity = isLiked
                    ? 0.55 + (rng.NextDouble() * 0.10)
                    : 0.45 + (rng.NextDouble() * 0.10);
                genreSimilarity = Math.Clamp(genreSimilarity, 0.0, 1.0);

                var seriesAffinity = isLiked
                    ? 0.70 + (rng.NextDouble() * 0.25)
                    : 0.05 + (rng.NextDouble() * 0.15);
                seriesAffinity = Math.Clamp(seriesAffinity, 0.0, 1.0);

                var features = new CandidateFeatures
                {
                    // New engagement + affinity signals under test.
                    HasUserInteraction = true,
                    CompletionRatio = completion,
                    IsAbandoned = abandonRate,
                    SeriesAffinity = seriesAffinity,

                    // Genre similarity correlates with taste but is NOT one of the ablated signals -
                    // it stays identical between arms so the ablation isolates the new features.
                    GenreSimilarity = genreSimilarity,

                    // A few neutral / shared background signals so the vectors are not degenerate.
                    CombinedCriticScore = 0.5 + ((rng.NextDouble() - 0.5) * 0.2),
                    CollaborativeScore = 0.5,
                    PopularityScore = 0.5,
                    GenreCount = 3,
                    IsSeries = isLiked
                };

                examples.Add(new TrainingExample
                {
                    Features = features,
                    Label = label,
                    UserId = userId,
                    SampleWeight = 1.0,
                    GeneratedAtUtc = FixedGeneratedAt
                });
            }
        }

        return examples;
    }

    /// <summary>
    ///     Returns a copy of the population where ONLY the new signals are reset to their pre-branch
    ///     neutral values (HasUserInteraction=false, CompletionRatio=0.5, IsAbandoned=0.0,
    ///     SeriesAffinity=0.0). Everything else - GenreSimilarity, critic/collab/popularity, label,
    ///     user id, weight, timestamp - is preserved so the delta isolates the new features' causal
    ///     contribution.
    /// </summary>
    /// <param name="source">The real-feature population.</param>
    /// <returns>The ablated population.</returns>
    private static List<TrainingExample> Ablate(List<TrainingExample> source)
    {
        var ablated = new List<TrainingExample>(source.Count);
        foreach (var ex in source)
        {
            var f = ex.Features;
            var copy = new CandidateFeatures
            {
                // Reset new signals to neutral.
                HasUserInteraction = false,
                CompletionRatio = 0.5,
                IsAbandoned = 0.0,
                SeriesAffinity = 0.0,

                // Preserve everything else exactly.
                GenreSimilarity = f.GenreSimilarity,
                CombinedCriticScore = f.CombinedCriticScore,
                CollaborativeScore = f.CollaborativeScore,
                PopularityScore = f.PopularityScore,
                GenreCount = f.GenreCount,
                IsSeries = f.IsSeries
            };

            ablated.Add(new TrainingExample
            {
                Features = copy,
                Label = ex.Label,
                UserId = ex.UserId,
                SampleWeight = ex.SampleWeight,
                GeneratedAtUtc = ex.GeneratedAtUtc
            });
        }

        return ablated;
    }

    /// <summary>
    ///     Writes one row of the ablation table (tier, NDCG real, NDCG ablated, delta) to test output.
    /// </summary>
    /// <param name="tier">The evaluation tier name.</param>
    /// <param name="ndcgReal">NDCG@10 with real features.</param>
    /// <param name="ndcgAblated">NDCG@10 with ablated features.</param>
    private void WriteRow(string tier, double ndcgReal, double ndcgAblated)
    {
        output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0,-26} NDCG_real={1:F4}  NDCG_ablated={2:F4}  delta={3:+0.0000;-0.0000}",
            tier,
            ndcgReal,
            ndcgAblated,
            ndcgReal - ndcgAblated));
    }
}
