using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Complete best-epoch/restart state, malformed statistics, coordinate-preserving clamps and shared v6 thematic inputs.</summary>
public sealed class NeuralIntegrityTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTime Future = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BestEpochCheckpoint_RestoresEveryMomentClockAndClampBeforeAnIdenticalNextUpdate()
    {
        using var model = new NeuralScoringStrategy();
        Invoke(model, "EnsureAdamState", CandidateFeatures.FeatureCount);
        var checkpoint = model.CaptureTrainingCheckpoint();
        var state = checkpoint.TrainingState!;
        foreach (var values in state.FirstMoments) Array.Fill(values, .01);
        foreach (var values in state.SecondMoments) Array.Fill(values, .02);
        state.OutputFirstMoment = .03;
        state.OutputSecondMoment = .04;
        state.Timestep = 17;
        Invoke(model, "RestoreOptimizerState", state);
        var best = Nested("WeightSnapshot", checkpoint.WeightsIH.ToArray(), checkpoint.BiasH1.ToArray(), checkpoint.WeightsH1H2.ToArray(), checkpoint.BiasH2.ToArray(),
            checkpoint.WeightsH2H3.ToArray(), checkpoint.BiasH3.ToArray(), checkpoint.WeightsH3H4.ToArray(), checkpoint.BiasH4.ToArray(), checkpoint.WeightsH4O.ToArray(), state.Copy());
        Invoke(model, "SnapshotBestWeights", best);
        var expected = model.CaptureTrainingCheckpoint();
        var wrong = state.Copy();
        foreach (var values in wrong.FirstMoments) Array.Fill(values, .1);
        foreach (var values in wrong.SecondMoments) Array.Fill(values, .2);
        wrong.Timestep = 99;
        Invoke(model, "RestoreOptimizerState", wrong);
        Field<double[]>(model, "_weightsIH")[0] += .5;
        Invoke(model, "RestoreBestWeights", best);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(model.CaptureTrainingCheckpoint()));
        using var reference = NeuralScoringStrategy.FromParameters(expected)!;
        var examples = new[] { Example(.8, .7) };
        var vectors = examples.Select(example => example.Features.ToVector()).ToArray();
        var config = Nested("EpochLoopConfig", 1, CandidateFeatures.FeatureCount, false, 1.0, 1.0);
        Invoke(model, "TrainOnExample", 0, config, examples, vectors, new[] { 1.0 }, Buffers(), new Random(1337));
        Invoke(reference, "TrainOnExample", 0, config, examples, vectors, new[] { 1.0 }, Buffers(), new Random(1337));
        Assert.Equal(JsonSerializer.Serialize(reference.CaptureTrainingCheckpoint()), JsonSerializer.Serialize(model.CaptureTrainingCheckpoint()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeDeviations_AreRejectedButConstantAndSignedZeroCoordinatesStayValid(bool neural)
    {
        var negative = new double[CandidateFeatures.FeatureCount];
        negative[0] = -.1;
        if (neural)
        {
            using var model = new NeuralScoringStrategy();
            var parameters = model.CapturePredictionSnapshot()!.ExportParameters();
            parameters.FeatureMeans = new double[CandidateFeatures.FeatureCount];
            parameters.FeatureStdDevs = negative;
            Assert.False(NeuralScoringStrategy.IsValidParameters(parameters));
            Assert.Null(NeuralScoringStrategy.FromParameters(parameters));
            parameters.FeatureStdDevs[0] = BitConverter.Int64BitsToDouble(long.MinValue);
            Assert.True(NeuralScoringStrategy.IsValidParameters(parameters));
            using var valid = NeuralScoringStrategy.FromParameters(parameters);
            Assert.NotNull(valid);
        }
        else
        {
            var parameters = new LearnedScoringStrategy().CapturePredictionSnapshot().ExportParameters();
            parameters.FeatureMeans = new double[CandidateFeatures.FeatureCount];
            parameters.FeatureStdDevs = negative;
            Assert.False(LearnedScoringStrategy.IsValidParameters(parameters));
            Assert.Null(LearnedScoringStrategy.FromParameters(parameters));
            parameters.FeatureStdDevs[0] = BitConverter.Int64BitsToDouble(long.MinValue);
            Assert.True(LearnedScoringStrategy.IsValidParameters(parameters));
        }
    }

    [Fact]
    public void RestartedCheckpoint_ContinuesBitIdenticallyWithoutAdoptingCallerStateOrChangingPinnedInference()
    {
        using var source = new NeuralScoringStrategy();
        var fit = Batch(80);
        Assert.True(source.Train(fit, Batch(20)));
        var checkpoint = source.CaptureTrainingCheckpoint();
        Assert.Equal(6, checkpoint.Version);
        Assert.NotNull(checkpoint.TrainingState);
        var json = JsonSerializer.Serialize(checkpoint);
        using var restored = NeuralScoringStrategy.FromParameters(JsonSerializer.Deserialize<NeuralScoringStrategy.NeuralWeightsData>(json)!)!;
        Assert.Equal(JsonSerializer.Serialize(source.CaptureTrainingCheckpoint()), JsonSerializer.Serialize(restored.CaptureTrainingCheckpoint()));
        var pinned = source.CapturePredictionSnapshot()!;
        var expected = fit.Select(example => pinned.Score(example.Features)).ToArray();
        checkpoint.TrainingState!.FirstMoments[0][0] += 100;
        checkpoint.TrainingState.InputWeightLimits[0] += 100;
        Assert.Equal(expected, fit.Select(example => restored.Score(example.Features)));
        Assert.Null(pinned.ExportParameters().TrainingState);
        Assert.True(source.Train(fit, Batch(20)));
        Assert.True(restored.Train(fit, Batch(20)));
        Assert.Equal(JsonSerializer.Serialize(source.CaptureTrainingCheckpoint()), JsonSerializer.Serialize(restored.CaptureTrainingCheckpoint()));
        Assert.Equal(expected, fit.Select(example => pinned.Score(example.Features)));
    }

    [Fact]
    public void V6UsesOneSharedFeatureSchemaAndRejectsUnsupportedParameterVersionsWithoutChangingPinnedInference()
    {
        using var source = new NeuralScoringStrategy();
        var parameters = source.CapturePredictionSnapshot()!.ExportParameters();
        parameters.TrainingGeneration = 6;
        parameters.FeatureMeans = Enumerable.Repeat(.2, CandidateFeatures.FeatureCount).ToArray();
        parameters.FeatureStdDevs = Enumerable.Repeat(.3, CandidateFeatures.FeatureCount).ToArray();
        using var model = NeuralScoringStrategy.FromParameters(parameters)!;
        var pinned = model.CapturePredictionSnapshot()!;
        var probes = Batch(20).Select(example => example.Features).ToArray();
        var expected = probes.Select(model.Score).ToArray();
        Assert.Equal(6, pinned.Version);
        Assert.Equal(41, NeuralScoringStrategy.PredictionSnapshot.InputCount);
        Assert.Equal(82 * 41, pinned.ExportParameters().WeightsIH.Length);
        parameters.Version = 0;
        Assert.Null(NeuralScoringStrategy.FromParameters(parameters));
        Assert.Equal(expected, probes.Select(pinned.Score).ToArray());
    }

    [Fact]
    public void RebasedInputClamps_MoveWithTheCoordinatesInsteadOfProjectingWeightTwoHundredToThree()
    {
        using var seed = new NeuralScoringStrategy();
        var parameters = seed.CapturePredictionSnapshot()!.ExportParameters();
        parameters.TrainingGeneration = 1;
        parameters.FeatureMeans = new double[CandidateFeatures.FeatureCount];
        parameters.FeatureStdDevs = Enumerable.Repeat(1.0, CandidateFeatures.FeatureCount).ToArray();
        parameters.FeatureStdDevs[0] = .001;
        Array.Clear(parameters.WeightsIH);
        parameters.WeightsIH[0] = 2;
        using var model = NeuralScoringStrategy.FromParameters(parameters)!;
        Invoke(model, "EnsureAdamState", CandidateFeatures.FeatureCount);
        var next = parameters.FeatureStdDevs.ToArray();
        next[0] = .1;
        Invoke(model, "RebaseWarmInputNormalization", parameters.FeatureMeans, next, CandidateFeatures.FeatureCount);
        typeof(NeuralScoringStrategy).GetField("_featureStdDevs", Hidden)!.SetValue(model, next);
        Assert.Equal(200, model.GetCurrentWeightsHidden()[0]);
        Assert.Equal(300, model.CaptureTrainingCheckpoint().TrainingState!.InputWeightLimits[0]);
        var example = Example(.5, .5);
        var vector = example.Features.ToVector();
        LearnedScoringStrategy.StandardizeSingleVector(vector, parameters.FeatureMeans, next);
        Invoke(model, "TrainOnExample", 0, Nested("EpochLoopConfig", 1, CandidateFeatures.FeatureCount, false, 1.0, 1.0), new[] { example }, new[] { vector }, new[] { 1.0 }, Buffers(), new Random(1337));
        Assert.InRange(model.GetCurrentWeightsHidden()[0], 199, 201);
        var checkpoint = model.CaptureTrainingCheckpoint();
        using var restored = NeuralScoringStrategy.FromParameters(checkpoint)!;
        Assert.Equal(300, restored.CaptureTrainingCheckpoint().TrainingState!.InputWeightLimits[0]);
    }

    [Fact]
    public void FineThemeInputs_AreLearnableKnownNonmatchesAreZeroAndUnknownsUseFittingOnlyMeans()
    {
        using var model = new NeuralScoringStrategy();
        var fit = Batch(100);
        var raw = fit[0].Features.ToVector();
        Assert.All(fit, example => Assert.Equal(raw.Take((int)FeatureIndex.ContentFitAffinity), example.Features.ToVector().Take((int)FeatureIndex.ContentFitAffinity)));
        var holdout = Batch(20);
        Assert.True(model.Train(fit, holdout));
        var predictor = model.CapturePredictionSnapshot()!;
        Assert.Equal(41, NeuralScoringStrategy.PredictionSnapshot.InputCount);
        Assert.Equal(new[] { "ContentFitAffinity", "ContentFitSupport", "AudienceMismatch" }, ModelInputFeatures.Names(41).Skip((int)FeatureIndex.ContentFitAffinity));
        Assert.True(model.Score(fit[1].Features) > model.Score(fit[0].Features) + .05);
        Assert.NotEqual(0, predictor.ScoreWithExplanation(fit[1].Features).ContentFitContribution);
        var missing = fit[1].Features.Copy();
        missing.ContentFitAffinity = null;
        missing.ContentFitSupport = null;
        missing.AudienceMismatch = null;
        missing.HasContentMetadata = null;
        var vector = ModelInputFeatures.Vector(missing, predictor.ImputationMeans, 41);
        Assert.Equal(predictor.ImputationMeans!.Skip(38), vector.Skip(38));
        var knownZero = ModelInputFeatures.Vector(fit[0].Features, predictor.ImputationMeans, 41);
        Assert.Equal(0, knownZero[38]);
        Assert.Equal(0, knownZero[39]);
        Assert.Equal(raw.Take((int)FeatureIndex.ContentFitAffinity), missing.ToVector().Take((int)FeatureIndex.ContentFitAffinity));
        var means = ModelInputFeatures.FitImputation(fit, [0, 2, 4], null);
        Assert.Equal(0, means[38]);
        fit[1].Features.ContentFitAffinity = .99;
        Assert.Equal(means, ModelInputFeatures.FitImputation(fit, [4, 2, 0], null));
    }

    [Fact]
    public void LearnedUsesTheSame41ThematicInputsInsteadOfOnlyTheBroadGenrePackage()
    {
        var model = new LearnedScoringStrategy();
        var examples = Batch(100);
        Assert.True(model.Train(examples, Batch(20)));
        Assert.True(model.Score(examples[1].Features) > model.Score(examples[0].Features) + .05);
        Assert.Equal(41, model.CapturePredictionSnapshot().ExportParameters().Weights.Length);
        Assert.NotEqual(0, model.ScoreWithExplanation(examples[1].Features).ContentFitContribution);
    }

    [Fact]
    public void DiagnosticsAndDocumentationUseTheSameCompleteV6FeatureCatalogue()
    {
        using var neural = new NeuralScoringStrategy();
        using var ensemble = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), neural);
        var diagnostics = ensemble.GetDiagnosticsSnapshot();
        Assert.Equal(6, diagnostics.NeuralModelVersion);
        Assert.Equal(41, diagnostics.NeuralInputFeatureCount);
        Assert.Equal(ModelInputFeatures.Names(41), diagnostics.NeuralFeatures);
        var response = Jellyfin.Plugin.JellyfinHelper.Api.EnsembleDiagnosticsResponse.FromDiagnostics(diagnostics);
        response.NeuralFeatures[0] = "changed caller value";
        Assert.Equal("GenreSimilarity", diagnostics.NeuralFeatures[0]);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "CONTRIBUTING.md"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var documentation = File.ReadAllText(Path.Join(directory.FullName, "docs", "RECOMMENDATION_FEATURES.md"));
        for (var index = 0; index < CandidateFeatures.FeatureCount; index++)
            Assert.Contains($"| {index} | `{ModelInputFeatures.Name(index)}` |", documentation);
    }

    [Fact]
    public void HeuristicPositiveBudgetAndFinalConfidenceStayBoundedForMaximumConflictingAndNonfiniteInputs()
    {
        var weights = DefaultWeights.CreateWeightArray();
        Assert.Equal(41, weights.Length);
        Assert.Equal(1, weights.Where(value => value > 0).Sum(), 12);
        var maximum = new CandidateFeatures { GenreCount = int.MaxValue };
        foreach (var property in typeof(CandidateFeatures).GetProperties().Where(property => property.CanWrite))
        {
            if (property.PropertyType == typeof(double) || property.PropertyType == typeof(double?)) property.SetValue(maximum, 1.0);
            if (property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?)) property.SetValue(maximum, true);
        }

        maximum.IsAbandoned = 0;
        maximum.GenreUnderexposure = 0;
        maximum.GenreAffinityGap = 0;
        maximum.AudienceMismatch = 0;
        Assert.Equal(1, HeuristicScoringStrategy.ScoreRaw(maximum), 12);
        var heuristic = new HeuristicScoringStrategy();
        Assert.Equal(1, heuristic.Score(maximum), 12);
        maximum.IsAbandoned = 1;
        maximum.GenreUnderexposure = 1;
        maximum.GenreAffinityGap = 1;
        maximum.AudienceMismatch = 1;
        Assert.InRange(heuristic.Score(maximum), 0, 1);
        Assert.Equal(heuristic.Score(maximum), heuristic.ScoreWithExplanation(maximum).FinalScore);
        foreach (var property in typeof(CandidateFeatures).GetProperties().Where(property => property.CanWrite && (property.PropertyType == typeof(double) || property.PropertyType == typeof(double?))))
            property.SetValue(maximum, double.PositiveInfinity);
        Assert.InRange(heuristic.Score(maximum), 0, 1);
    }

    [Fact]
    public void MalformedOptimizerCannotPoisonValidInferenceAndVersionedStatisticsCannotBeTruncated()
    {
        using var model = new NeuralScoringStrategy();
        Assert.True(model.Train(Batch(50)));
        var checkpoint = model.CaptureTrainingCheckpoint();
        var expected = model.Score(Batch(2)[1].Features);
        checkpoint.TrainingState!.SecondMoments[0][0] = -1;
        using var restored = NeuralScoringStrategy.FromParameters(checkpoint)!;
        Assert.Equal(expected, restored.Score(Batch(2)[1].Features));
        Assert.Null(restored.CaptureTrainingCheckpoint().TrainingState);
        checkpoint.FeatureStdDevs = new double[CandidateFeatures.FeatureCount - 1];
        Assert.False(NeuralScoringStrategy.IsValidParameters(checkpoint));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("negative-time")]
    [InlineData("missing-first")]
    [InlineData("missing-second")]
    [InlineData("wrong-length")]
    [InlineData("nonfinite-first")]
    [InlineData("negative-second")]
    [InlineData("nonfinite-output")]
    [InlineData("negative-output-second")]
    [InlineData("zero-weight-limit")]
    [InlineData("nonfinite-bias-limit")]
    public void OptimizerValidationRejectsMalformedShapesMomentsAndCoordinateLimits(string corruption)
    {
        using var model = new NeuralScoringStrategy();
        Invoke(model, "EnsureAdamState", CandidateFeatures.FeatureCount);
        var state = model.CaptureTrainingCheckpoint().TrainingState!;
        Assert.True(state.Valid(CandidateFeatures.FeatureCount));
        switch (corruption)
        {
            case "version": state.Version = 2; break;
            case "negative-time": state.Timestep = -1; break;
            case "missing-first": state.FirstMoments = null!; break;
            case "missing-second": state.SecondMoments[0] = null!; break;
            case "wrong-length": state.FirstMoments[0] = [0]; break;
            case "nonfinite-first": state.FirstMoments[0][0] = double.NaN; break;
            case "negative-second": state.SecondMoments[0][0] = -1; break;
            case "nonfinite-output": state.OutputFirstMoment = double.PositiveInfinity; break;
            case "negative-output-second": state.OutputSecondMoment = -1; break;
            case "zero-weight-limit": state.InputWeightLimits[0] = 0; break;
            case "nonfinite-bias-limit": state.InputBiasLimits[0] = double.PositiveInfinity; break;
        }
        Assert.False(state.Valid(CandidateFeatures.FeatureCount));
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    [InlineData("output-first")]
    [InlineData("output-second")]
    public void ZeroAdamTimestepRequiresExactlyZeroMomentsEvenForTheSmallestFiniteValue(string coordinate)
    {
        using var model = new NeuralScoringStrategy();
        Invoke(model, "EnsureAdamState", CandidateFeatures.FeatureCount);
        var state = model.CaptureTrainingCheckpoint().TrainingState!;
        Assert.True(state.Valid(CandidateFeatures.FeatureCount));
        switch (coordinate)
        {
            case "first": state.FirstMoments[0][0] = double.Epsilon; break;
            case "second": state.SecondMoments[0][0] = double.Epsilon; break;
            case "output-first": state.OutputFirstMoment = double.Epsilon; break;
            case "output-second": state.OutputSecondMoment = double.Epsilon; break;
        }
        Assert.False(state.Valid(CandidateFeatures.FeatureCount));
        var roundTrip = JsonSerializer.Deserialize<NeuralTrainingState>(JsonSerializer.Serialize(state))!;
        Assert.False(roundTrip.Valid(CandidateFeatures.FeatureCount));
        roundTrip.Timestep = 1;
        Assert.True(roundTrip.Valid(CandidateFeatures.FeatureCount));
    }

    private static List<TrainingExample> Batch(int count) => Enumerable.Range(0, count).Select(index => Example(index % 2 == 0 ? .1 : .9, index % 2 == 0 ? 0 : 1)).ToList();
    private static TrainingExample Example(double label, double theme) => new()
    {
        Features = new CandidateFeatures { GenreSimilarity = .7, GenreCount = 3, CombinedCriticScore = .7, HasContentMetadata = true,
            ContentFitAffinity = theme, ContentFitSupport = theme > 0 ? theme : null, AudienceMismatch = theme > 0 ? null : .5 },
        Label = label, GeneratedAtUtc = Future
    };
    private static object? Invoke(object model, string method, params object?[] args) => typeof(NeuralScoringStrategy).GetMethod(method, Hidden)!.Invoke(model, args);
    private static T Field<T>(object model, string name) => (T)typeof(NeuralScoringStrategy).GetField(name, Hidden)!.GetValue(model)!;
    private static object Nested(string name, params object[] args) => typeof(NeuralScoringStrategy).GetNestedType(name, BindingFlags.NonPublic)!
        .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single(constructor => constructor.GetParameters().Length == args.Length).Invoke(args);
    private static object Buffers() => Nested("TrainingBuffers", new double[82], new double[82], new double[96], new double[96], new double[48], new double[48], new double[24], new double[24],
        new double[82], new double[96], new double[48], new double[24], new double[82], new double[96], new double[48], new double[24]);
}
