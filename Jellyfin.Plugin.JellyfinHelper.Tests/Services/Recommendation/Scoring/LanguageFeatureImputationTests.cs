using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Missing-language flags, model-specific v6 means and independent fitting splits remain distinct from observed affinity.</summary>
public sealed class LanguageFeatureImputationTests
{
    [Fact]
    public void NullLanguageFlagsPreserveLanguageCoordinatesWhileUnknownThemesUseV6Means()
    {
        var legacy = JsonSerializer.Deserialize<CandidateFeatures>("""{"LanguageAffinity":0.3,"SubtitleLanguageAffinity":0.85,"GenreSimilarity":0.7,"TagSimilarity":0.001}""")!;
        var raw = legacy.ToVector();
        var means = Enumerable.Repeat(.9, CandidateFeatures.FeatureCount).ToArray();
        Assert.Equal(raw.Take((int)FeatureIndex.ContentFitAffinity), legacy.ToModelVector(means).Take((int)FeatureIndex.ContentFitAffinity));
        Assert.Equal(means.Skip((int)FeatureIndex.ContentFitAffinity), legacy.ToModelVector(means).Skip((int)FeatureIndex.ContentFitAffinity));
        Assert.Null(legacy.HasAudioLanguageEvidence);
        Assert.Null(legacy.HasSubtitleLanguageEvidence);
        Assert.Equal(legacy.ToModelVector(means), JsonSerializer.Deserialize<CandidateFeatures>(JsonSerializer.Serialize(legacy))!.ToModelVector(means));
        Assert.Null(LanguageFeatureImputation.Fit([new TrainingExample { Features = legacy }], [0], null));
    }

    [Fact]
    public void UnknownAndKnownLanguages_UseDifferentModelMeansWithoutChangingCapturedFeatures()
    {
        var features = new CandidateFeatures { LanguageAffinity = .5, SubtitleLanguageAffinity = .85, HasAudioLanguageEvidence = false, HasSubtitleLanguageEvidence = true };
        var first = Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray();
        var second = Enumerable.Repeat(.2, CandidateFeatures.FeatureCount).ToArray();
        Assert.Equal(.8, features.ToModelVector(first)[28]);
        Assert.Equal(.2, features.ToModelVector(second)[28]);
        Assert.Equal(.85, features.ToModelVector(first)[30]);
        Assert.Equal(.85, features.ToModelVector(second)[30]);
        Assert.Equal(.5, features.ToModelVector(null)[28]);
        Assert.Equal(.5, features.LanguageAffinity);
        Assert.Equal(features.ToVector(), JsonSerializer.Deserialize<CandidateFeatures>(JsonSerializer.Serialize(features))!.ToVector());
    }

    [Fact]
    public void FittingMeans_ExcludeValidationUnknownLegacyAndUnevidencedExternalPlaceholders()
    {
        TrainingExample[] examples =
        [
            Example(.8, true), Example(.4, true), Example(.99, false),
            Example(.99, null), Example(.1, true), Example(.99, true)
        ];
        examples[5].Features.IsExternalCandidate = true;
        var means = LanguageFeatureImputation.Fit(examples, [0, 1, 2, 3, 5], null)!;
        Assert.Equal(.6, means[28], 12);
        Assert.Equal(.5, means[30]);
        examples[4].Features.LanguageAffinity = 1;
        examples[4].Label = 0;
        Assert.Equal(means, LanguageFeatureImputation.Fit(examples, [5, 3, 2, 1, 0], null));
        var vectors = examples.Select(row => row.Features.ToModelVector(null)).ToArray();
        LanguageFeatureImputation.Apply(examples, vectors, means);
        Assert.Equal(.6, vectors[2][28], 12);
        Assert.Equal(.6, vectors[5][28], 12);
        Assert.Equal(.99, vectors[3][28]);
        Assert.Equal(.99, examples[2].Features.LanguageAffinity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothFittedModels_PersistMissingInputMeansAndPinTheirPredictionsAcrossContinuingFits(bool neural)
    {
        IScoringStrategy strategy = neural ? new NeuralScoringStrategy() : new LearnedScoringStrategy();
        try
        {
            var fit = Enumerable.Range(0, 60).Select(index => Example(index % 3 == 0 ? .5 : 1, index % 3 != 0)).ToArray();
            var holdout = Enumerable.Range(0, 20).Select(_ => Example(.1, true)).ToArray();
            Assert.True(((ITrainableStrategy)strategy).Train(fit, holdout));
            var unknown = Example(.5, false).Features;
            var observed = unknown.Copy();
            observed.HasAudioLanguageEvidence = true;
            observed.LanguageAffinity = 1;
            Assert.Equal(strategy.Score(observed), strategy.Score(unknown));
            if (strategy is NeuralScoringStrategy n)
            {
                var snapshot = n.CapturePredictionSnapshot()!;
                Assert.Equal(1, snapshot.ExportParameters().ImputationMeans![28]);
                var expected = snapshot.Score(unknown);
                Assert.True(n.Train(Enumerable.Range(0, 60).Select(_ => Example(.2, true)).ToArray(), holdout));
                Assert.Equal(expected, snapshot.Score(unknown));
                Assert.Equal(snapshot.Score(unknown), snapshot.ScoreWithExplanation(unknown).FinalScore);
            }
            else
            {
                var learned = (LearnedScoringStrategy)strategy;
                var snapshot = learned.CapturePredictionSnapshot();
                Assert.Equal(1, snapshot.ExportParameters().ImputationMeans![28]);
                var expected = snapshot.Score(unknown);
                Assert.True(learned.Train(Enumerable.Range(0, 60).Select(_ => Example(.2, true)).ToArray(), holdout));
                Assert.Equal(expected, snapshot.Score(unknown));
                Assert.Equal(snapshot.Score(unknown), snapshot.ScoreWithExplanation(unknown).FinalScore);
            }
        }
        finally
        {
            (strategy as IDisposable)?.Dispose();
        }
    }

    private static TrainingExample Example(double affinity, bool? known) => new()
    {
        Features = new CandidateFeatures { GenreSimilarity = .7, LanguageAffinity = affinity, HasAudioLanguageEvidence = known, HasSubtitleLanguageEvidence = false },
        Label = .8, GeneratedAtUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };
}
