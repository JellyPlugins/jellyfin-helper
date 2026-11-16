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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BothModelsLegacyRefitsPreserveSavedImputationWithoutCopyingNormalization(bool savedPrior, bool neural)
    {
        var prior = savedPrior ? Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray() : null;
        var examples = LegacyExternalExamples();
        var capturedFeatures = examples.Select(row => JsonSerializer.Serialize(row.Features)).ToArray();
        var expected = ModelInputFeatures.FitImputation(examples, Enumerable.Range(0, examples.Length).ToArray(), prior);
        ITrainableStrategy model = neural ? NeuralWithSeparateMeans(.1, prior) : LearnedWithSeparateMeans(.1, prior);

        try
        {
            for (var fit = 0; fit < 2; fit++)
            {
                Assert.True(model.Train(examples));
                Assert.Equal(capturedFeatures, examples.Select(row => JsonSerializer.Serialize(row.Features)));
                if (model is NeuralScoringStrategy n)
                {
                    var checkpoint = n.CaptureTrainingCheckpoint();
                    Assert.Equal(expected, checkpoint.ImputationMeans);
                    model = NeuralScoringStrategy.FromParameters(JsonSerializer.Deserialize<NeuralScoringStrategy.NeuralWeightsData>(JsonSerializer.Serialize(checkpoint))!)!;
                    n.Dispose();
                }
                else
                {
                    var parameters = ((LearnedScoringStrategy)model).CapturePredictionSnapshot().ExportParameters();
                    Assert.Equal(expected, parameters.ImputationMeans);
                    model = LearnedScoringStrategy.FromParameters(JsonSerializer.Deserialize<LearnedScoringStrategy.WeightsData>(JsonSerializer.Serialize(parameters))!)!;
                }
            }
        }
        finally
        {
            (model as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void NeuralLegacyRefitsPreserveSavedImputationWithoutNormalizationStatistics()
    {
        var prior = Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray();
        using var model = NeuralWithSeparateMeans(null, prior);
        var snapshot = model.CapturePredictionSnapshot()!;
        var probe = LegacyExternalExamples()[0].Features;
        var pinnedScore = snapshot.Score(probe);

        Assert.True(model.Train(LegacyExternalExamples()));
        Assert.Equal(prior, model.CaptureTrainingCheckpoint().ImputationMeans);
        Assert.Equal(prior, snapshot.ExportParameters().ImputationMeans);
        Assert.Equal(pinnedScore, snapshot.Score(probe));
        Assert.Equal(snapshot.Score(probe), snapshot.ScoreWithExplanation(probe).FinalScore);
    }

    [Fact]
    public void NeuralExternalRefitNormalizationUsesSavedMissingCoordinates()
    {
        var prior = Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray();
        using var model = NeuralWithSeparateMeans(.1, prior);

        Assert.True(model.Train(LegacyExternalExamples()));
        var parameters = model.CaptureTrainingCheckpoint();
        foreach (var coordinate in new[]
        {
            FeatureIndex.CollaborativeScore, FeatureIndex.PeopleSimilarity, FeatureIndex.DayOfWeekAffinity,
            FeatureIndex.LanguageAffinity, FeatureIndex.ContentFitAffinity, FeatureIndex.ContentFitSupport, FeatureIndex.AudienceMismatch
        })
        {
            Assert.Equal(prior[(int)coordinate], parameters.FeatureMeans![(int)coordinate], 12);
            Assert.Equal(0, parameters.FeatureStdDevs![(int)coordinate], 12);
        }
    }

    [Fact]
    public void LearnedCrossValidationUsesSavedImputationIndependentOfPreviousNormalization()
    {
        var prior = Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray();
        var examples = LegacyExternalExamples();
        var first = LearnedWithSeparateMeans(.1, prior);
        var second = LearnedWithSeparateMeans(.9, prior);

        Assert.True(first.Train(examples));
        Assert.True(second.Train(examples));
        Assert.True(double.IsFinite(first.LastValidationLoss));
        Assert.Equal(first.LastValidationLoss, second.LastValidationLoss);
        Assert.Equal(first.LastBaselineValidationLoss, second.LastBaselineValidationLoss);
    }

    [Fact]
    public void LearnedRawExternalVectorsUseSavedImputationBeforeFittingNormalization()
    {
        var prior = Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray();
        var examples = LegacyExternalExamples();
        var expectedVectors = examples.Select(row => row.Features.ToModelVector(prior)).ToArray();
        var (expectedMeans, expectedDeviations) = LearnedScoringStrategy.ComputeFeatureStatistics(expectedVectors);
        var model = LearnedWithSeparateMeans(.1, prior);

        Assert.True(model.Train(examples));
        var parameters = model.CapturePredictionSnapshot().ExportParameters();
        Assert.Equal(expectedMeans, parameters.FeatureMeans);
        Assert.Equal(expectedDeviations, parameters.FeatureStdDevs);
    }

    private static LearnedScoringStrategy LearnedWithSeparateMeans(double normalizationMean, double[]? imputation) =>
        LearnedScoringStrategy.FromParameters(new LearnedScoringStrategy.WeightsData
        {
            Version = LearnedScoringStrategy.CurrentWeightsVersion,
            TrainingGeneration = 1,
            Weights = new double[CandidateFeatures.FeatureCount],
            Bias = .5,
            FeatureMeans = Enumerable.Repeat(normalizationMean, CandidateFeatures.FeatureCount).ToArray(),
            FeatureStdDevs = Enumerable.Repeat(1.0, CandidateFeatures.FeatureCount).ToArray(),
            ImputationMeans = imputation
        })!;

    private static NeuralScoringStrategy NeuralWithSeparateMeans(double? normalizationMean, double[]? imputation)
    {
        using var initializer = new NeuralScoringStrategy();
        var parameters = initializer.CapturePredictionSnapshot()!.ExportParameters();
        parameters.TrainingGeneration = 1;
        parameters.FeatureMeans = normalizationMean.HasValue ? Enumerable.Repeat(normalizationMean.Value, CandidateFeatures.FeatureCount).ToArray() : null;
        parameters.FeatureStdDevs = normalizationMean.HasValue ? Enumerable.Repeat(1.0, CandidateFeatures.FeatureCount).ToArray() : null;
        parameters.ImputationMeans = imputation;
        return NeuralScoringStrategy.FromParameters(parameters)!;
    }

    private static TrainingExample[] LegacyExternalExamples() => Enumerable.Range(0, 60).Select(index => new TrainingExample
    {
        Features = new CandidateFeatures
        {
            GenreSimilarity = index % 2 == 0 ? .3 : .7,
            LanguageAffinity = .15,
            SubtitleLanguageAffinity = .85,
            IsExternalCandidate = true,
            HasPeopleMetadata = false
        },
        Label = index % 2 == 0 ? .2 : .8,
        IsObserved = true,
        GeneratedAtUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    }).ToArray();

    private static TrainingExample Example(double affinity, bool? known) => new()
    {
        Features = new CandidateFeatures { GenreSimilarity = .7, LanguageAffinity = affinity, HasAudioLanguageEvidence = known, HasSubtitleLanguageEvidence = false },
        Label = .8, GeneratedAtUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };
}
