using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Verifies <see cref="LearnedScoringStrategy.SeedFrom"/>: the warm-start deep-copies weights, bias, and -
///     critically - the standardization statistics, so a per-user model started from the global fit scores
///     identically and does not silently misapply standardized-space weights to raw features.
/// </summary>
public sealed class LearnedScoringStrategySeedTests
{
    [Fact]
    public void SeedFrom_CopiesWeightsBiasAndStandardizationStats()
    {
        // Train the source on >= MinExamplesForStandardization examples so it fits UNDER standardization
        // (feature means/std-devs become non-null). That is the state the warm-start must carry over.
        var source = new LearnedScoringStrategy();
        Assert.True(source.Train(GenerateExamples(60)));
        Assert.NotNull(source.GetFeatureMeans());
        Assert.NotNull(source.GetFeatureStdDevs());

        var target = new LearnedScoringStrategy();
        target.SeedFrom(source);

        Assert.Equal(source.GetCurrentWeights(), target.GetCurrentWeights());
        Assert.Equal(source.CurrentBias, target.CurrentBias);
        Assert.Equal(source.GetFeatureMeans(), target.GetFeatureMeans());
        Assert.Equal(source.GetFeatureStdDevs(), target.GetFeatureStdDevs());
    }

    [Fact]
    public void SeedFrom_ProducesIdenticalScores()
    {
        // The whole point of warm-start: a freshly seeded model must score exactly like its source before it
        // trains on any per-user data. If the standardization stats were dropped, these would diverge.
        var source = new LearnedScoringStrategy();
        Assert.True(source.Train(GenerateExamples(60)));

        var target = new LearnedScoringStrategy();
        target.SeedFrom(source);

        foreach (var genre in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var features = new CandidateFeatures { GenreSimilarity = genre, CombinedCriticScore = 0.6 };
            Assert.Equal(source.Score(features), target.Score(features), 12);
        }
    }

    [Fact]
    public void SeedFrom_CarriesStandardizationWhenTargetTrainsUnstandardized()
    {
        // The subtle foot-gun: source fit standardized (>= 20 examples); target then trains on 12-19 examples
        // (unstandardized). Because SeedFrom carried the stats, the mode-change rescale runs and scoring stays
        // finite and in range - never a raw-space application of standardized-space weights.
        var source = new LearnedScoringStrategy();
        Assert.True(source.Train(GenerateExamples(60)));

        var target = new LearnedScoringStrategy();
        target.SeedFrom(source);
        Assert.True(target.Train(GenerateExamples(15)));

        var score = target.Score(new CandidateFeatures { GenreSimilarity = 0.7 });
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void SeedFrom_TargetIsIndependentOfLaterSourceChanges()
    {
        var source = new LearnedScoringStrategy();
        Assert.True(source.Train(GenerateExamples(60)));

        var target = new LearnedScoringStrategy();
        target.SeedFrom(source);
        var seededWeights = target.GetCurrentWeights();

        // Retrain the source; the already-seeded target must not move (deep copy, not shared references).
        Assert.True(source.Train(GenerateExamples(80)));

        Assert.Equal(seededWeights, target.GetCurrentWeights());
    }

    [Fact]
    public void SeedFrom_NullSource_Throws()
    {
        var target = new LearnedScoringStrategy();
        Assert.Throws<ArgumentNullException>(() => target.SeedFrom(null!));
    }

    [Fact]
    public void SeedFrom_UntrainedSource_CopiesNullStandardizationStats()
    {
        // An untrained source has null means/std-devs; the target must accept that (no standardization yet)
        // rather than throwing or fabricating stats.
        var source = new LearnedScoringStrategy();
        Assert.Null(source.GetFeatureMeans());

        var target = new LearnedScoringStrategy();
        target.SeedFrom(source);

        Assert.Null(target.GetFeatureMeans());
        Assert.Null(target.GetFeatureStdDevs());
        Assert.Equal(source.GetCurrentWeights(), target.GetCurrentWeights());
    }

    private static List<TrainingExample> GenerateExamples(int count)
    {
        var rng = new Random(42);
        var examples = new List<TrainingExample>(count);
        for (var i = 0; i < count; i++)
        {
            var genreSim = rng.NextDouble();
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = genreSim,
                    CollaborativeScore = rng.NextDouble(),
                    CombinedCriticScore = rng.NextDouble(),
                    RecencyScore = rng.NextDouble(),
                    YearProximityScore = rng.NextDouble(),
                    GenreCount = rng.Next(0, 6),
                    IsSeries = rng.NextDouble() > 0.5
                },
                Label = genreSim > 0.5 ? 1.0 : 0.0
            });
        }

        return examples;
    }
}
