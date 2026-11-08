using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

public sealed class TrainingInvariantsTests
{
    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(true, 0.0)]
    [InlineData(false, 0.005)]
    [InlineData(true, 0.005)]
    public void IgnoredBatch_DoesNotChangeAnAlreadyTrainedModel(bool neural, double weight)
    {
        IScoringStrategy strategy = neural ? new NeuralScoringStrategy() : new LearnedScoringStrategy();
        try
        {
            var trainable = (ITrainableStrategy)strategy;
            Assert.True(trainable.Train(Batch(30, 0.1, 0.9)));
            var candidate = new CandidateFeatures { GenreSimilarity = 0.8 };
            var score = strategy.Score(candidate);
            var snapshot = Snapshot(strategy);
            Assert.False(trainable.Train(Batch(250, 0.8, 0.9, weight)));
            Assert.Equal(snapshot, Snapshot(strategy));
            Assert.Equal(score, strategy.Score(candidate), 12);
        }
        finally
        {
            (strategy as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredBatch_DoesNotCountAsTraining(bool neural)
    {
        var examples = Batch(250, 0.1, 0.9);
        foreach (var example in examples) example.GeneratedAtUtc = DateTime.UtcNow.AddYears(-3);
        IScoringStrategy strategy = neural ? new NeuralScoringStrategy() : new LearnedScoringStrategy();
        try
        {
            var snapshot = Snapshot(strategy);
            Assert.False(((ITrainableStrategy)strategy).Train(examples));
            Assert.Equal(snapshot, Snapshot(strategy));
        }
        finally
        {
            (strategy as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Ensemble_CountsOnlyEffectiveExamplesAndDoesNotActivateOnIgnoredData()
    {
        using var neural = new NeuralScoringStrategy();
        using var ensemble = new EnsembleScoringStrategy(
            new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), neural);
        var examples = Batch(40, 0.1, 0.9);
        examples.AddRange(Batch(250, 0.8, 0.9, weight: 0));
        Assert.True(ensemble.Train(examples, Batch(20, 0.1, 0.9)));
        Assert.Equal(40, ensemble.TrainingExampleCount);
        Assert.Equal(0, ensemble.CurrentNeuralBeta);
        Assert.False(ensemble.Train(Batch(250, 0.8, 0.9, weight: 0)));
        Assert.Equal(40, ensemble.TrainingExampleCount);
        Assert.Equal(0, ensemble.CurrentNeuralBeta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppliedHoldoutLoss_MatchesTheFittedModelRatherThanItsTrainingFit(bool neural)
    {
        IScoringStrategy strategy = neural ? new NeuralScoringStrategy() : new LearnedScoringStrategy();
        try
        {
            var holdout = Batch(30, 0.2, 0.8, label: 0);
            Assert.True(((ITrainableStrategy)strategy).Train(Batch(160, 0.2, 0.8, label: 1), holdout));
            var expected = holdout.Average(example => Math.Pow(strategy.Score(example.Features) - example.Label, 2));
            var actual = neural
                ? ((NeuralScoringStrategy)strategy).LastValidationLoss
                : ((LearnedScoringStrategy)strategy).LastValidationLoss;
            Assert.Equal(expected, actual, 12);
            Assert.True(actual > EnsembleScoringStrategy.ValidationLossThreshold);
        }
        finally
        {
            (strategy as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Ensemble_DoesNotActivateNeuralWhenIndependentHoldoutQualityIsPoor()
    {
        using var neural = new NeuralScoringStrategy();
        using var ensemble = new EnsembleScoringStrategy(
            new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), neural);
        Assert.True(ensemble.Train(Batch(250, 0.2, 0.8, label: 1), Batch(30, 0.2, 0.8, label: 0)));
        Assert.True(neural.LastValidationLoss > EnsembleScoringStrategy.ValidationLossThreshold);
        Assert.Equal(0, ensemble.CurrentNeuralBeta);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IneffectiveHoldout_IsUnknownAndDoesNotFallBackToTrainingLoss(bool neural)
    {
        IScoringStrategy strategy = neural ? new NeuralScoringStrategy() : new LearnedScoringStrategy();
        try
        {
            Assert.True(((ITrainableStrategy)strategy).Train(Batch(50, 0.1, 0.9), Batch(20, 0.1, 0.9, weight: 0)));
            var loss = neural ? ((NeuralScoringStrategy)strategy).LastValidationLoss : ((LearnedScoringStrategy)strategy).LastValidationLoss;
            Assert.True(double.IsNaN(loss));
        }
        finally
        {
            (strategy as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Neural_WarmTrainingWithoutIndependentHoldout_DoesNotReuseValidationMembersAsQualityEvidence()
    {
        using var neural = new NeuralScoringStrategy();
        Assert.True(neural.Train(Batch(50, 0.1, 0.9)));
        Assert.True(double.IsFinite(neural.LastValidationLoss));
        Assert.True(neural.Train(Batch(50, 0.1, 0.9)));
        Assert.True(double.IsNaN(neural.LastValidationLoss));
    }

    [Fact]
    public void Neural_ChangedInputCoordinates_ResetAdamButPreserveItsTrainingGeneration()
    {
        using var neural = new NeuralScoringStrategy();
        Assert.True(neural.Train(Batch(30, 0.1, 0.9)));
        var firstSteps = neural.AdamTimestep;
        Assert.True(firstSteps > 0);
        Assert.True(neural.Train(Batch(30, 0.8, 0.9)));
        Assert.Equal(firstSteps, neural.AdamTimestep);
        Assert.Equal(2, neural.TrainingGeneration);
        Assert.InRange(neural.Score(new CandidateFeatures { GenreSimilarity = 0.85 }), 0.0, 1.0);
    }

    [Fact]
    public void Neural_IdenticalTrainingMembership_DoesNotResetAdamThroughShufflingNoise()
    {
        using var neural = new NeuralScoringStrategy();
        var examples = Batch(80, 0.1, 0.9);
        var holdout = Batch(20, 0.12, 0.88);
        Assert.True(neural.Train(examples, holdout));
        var firstSteps = neural.AdamTimestep;
        Assert.True(firstSteps > 0);
        Assert.True(neural.Train(examples, holdout));
        Assert.True(neural.AdamTimestep > firstSteps);
        Assert.True(double.IsFinite(neural.LastValidationLoss));
    }

    [Fact]
    public void Neural_LoadedWeights_RequireIndependentValidationEvenWhenTheFileGenerationIsZero()
    {
        var path = Path.Combine(Path.GetTempPath(), "jfh-normalization-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var source = new NeuralScoringStrategy(path)) Assert.True(source.Train(Batch(50, 0.1, 0.9)));
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            document["TrainingGeneration"] = 0;
            File.WriteAllText(path, document.ToJsonString());
            using var loaded = new NeuralScoringStrategy(path);
            Assert.True(loaded.Train(Batch(50, 0.1, 0.9)));
            Assert.True(double.IsNaN(loaded.LastValidationLoss));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(3, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    public void Rebase_PreservesEveryDensePreactivationAcrossCoordinateAndConstantFeatureChanges(int oldMode, int newMode)
    {
        var oldStats = Statistics(oldMode);
        var newStats = Statistics(newMode);
        var weights = Enumerable.Range(0, CandidateFeatures.FeatureCount * 2).Select(i => (i % 7 - 3) * 0.1).ToArray();
        var biases = new[] { 0.2, -0.3 };
        var raw = Enumerable.Range(0, CandidateFeatures.FeatureCount).Select(i => (i % 9 + 1) * 0.1).ToArray();
        var before = Preactivations(weights, biases, raw, oldStats.Means, oldStats.StdDevs);
        var changed = TrainingMath.RebaseInputLayer(weights, biases, oldStats.Means, oldStats.StdDevs, newStats.Means, newStats.StdDevs);
        var after = Preactivations(weights, biases, raw, newStats.Means, newStats.StdDevs);
        Assert.Equal(oldMode != newMode, changed);
        for (var h = 0; h < before.Length; h++) Assert.Equal(before[h], after[h], 10);
    }

    [Fact]
    public void WeightedHoldoutLoss_UsesEffectiveWeightsAndExcludesIgnoredExamples()
    {
        var strategy = new HeuristicScoringStrategy();
        var examples = Batch(3, 0.5, 0.5);
        examples[0].Label = 0;
        examples[1].Label = 1;
        examples[1].SampleWeight = 0.5;
        examples[2].SampleWeight = 0;
        var score = strategy.Score(examples[0].Features);
        var expected = (score * score + 0.5 * Math.Pow(score - 1, 2)) / 1.5;
        Assert.Equal(expected, TrainingMath.ValidationLoss(examples, strategy, DateTime.UtcNow, 0.01, 0.1), 12);
        Assert.Equal(0.1, TrainingMath.ValidationLoss(null, strategy, DateTime.UtcNow, 0.01, 0.1));
        Assert.True(double.IsNaN(TrainingMath.ValidationLoss([], strategy, DateTime.UtcNow, 0.01, 0.1)));
        Assert.True(double.IsNaN(TrainingMath.ValidationLoss(examples.Take(1).ToArray(), strategy, DateTime.UtcNow, 0.01, 0.1)));
    }

    // Future fixture timestamps keep temporal weights exact while validation compares model outputs.
    private static List<TrainingExample> Batch(int count, double low, double high, double weight = 1, double? label = null) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var genre = low + (high - low) * i / (count - 1);
            return new TrainingExample { Features = new CandidateFeatures { GenreSimilarity = genre },
                Label = label ?? (genre > 0.5 ? 1 : 0), SampleWeight = weight, GeneratedAtUtc = DateTime.UtcNow.AddHours(1) };
        }).ToList();

    private static string Snapshot(IScoringStrategy strategy)
    {
        var fields = strategy.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.Name.StartsWith("_weights", StringComparison.Ordinal) || field.Name.StartsWith("_bias", StringComparison.Ordinal)
                || field.Name.StartsWith("_feature", StringComparison.Ordinal) || field.Name.StartsWith("_m", StringComparison.Ordinal)
                || field.Name.StartsWith("_v", StringComparison.Ordinal) || field.Name is "_trainingGeneration" or "_adamTimestep" or "_hasTrainedWeights")
            .ToDictionary(field => field.Name, field => field.GetValue(strategy));
        return JsonSerializer.Serialize(fields, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
    }

    private static (double[]? Means, double[]? StdDevs) Statistics(int mode)
    {
        if (mode == 0) return (null, null);
        var means = Enumerable.Range(0, CandidateFeatures.FeatureCount).Select(i => 0.2 + i * 0.01 * mode).ToArray();
        var stdDevs = Enumerable.Repeat(mode == 2 ? 0.3 : 0.2, CandidateFeatures.FeatureCount).ToArray();
        if (mode == 3) stdDevs[0] = 0;
        return (means, stdDevs);
    }

    private static double[] Preactivations(double[] weights, double[] biases, double[] raw, double[]? means, double[]? stdDevs)
    {
        var vector = (double[])raw.Clone();
        if (means is not null && stdDevs is not null) LearnedScoringStrategy.StandardizeSingleVector(vector, means, stdDevs);
        return biases.Select((bias, h) => bias + vector.Select((value, f) => value * weights[h * CandidateFeatures.FeatureCount + f]).Sum()).ToArray();
    }
}
