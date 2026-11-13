using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Detached fits preserve active predictions, full optimizer coordinates and v5 parameter isolation.</summary>
public sealed class ModelSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-model-snapshots-" + Guid.NewGuid().ToString("N"));

    public ModelSnapshotTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary test artifacts may still be indexed on Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort teardown only; test assertions never suppress write failures.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Learned_ChallengerPreservesContinuingGenerationAndNeverWritesTheIncumbentFile()
    {
        var path = Path.Join(_directory, "ml_weights.json");
        var source = new LearnedScoringStrategy(path);
        var fit = Examples(80, seed: 11);
        var holdout = Examples(40, seed: 12);
        Assert.True(source.Train(fit, holdout));
        var before = File.ReadAllBytes(path);
        var snapshot = source.CapturePredictionSnapshot();
        var probes = Examples(20, seed: 13).Select(example => example.Features).ToArray();
        var expected = probes.Select(source.Score).ToArray();
        var challenger = source.CreateChallenger();
        Assert.Equal(expected, probes.Select(challenger.Score).ToArray());
        Assert.Equal(Read<int>(source, "_trainingGeneration"), Read<int>(challenger, "_trainingGeneration"));
        Assert.Null(Read<string?>(challenger, "_weightsPath"));
        Assert.True(challenger.Train(Examples(80, seed: 14), holdout));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(expected, probes.Select(source.Score).ToArray());
        Assert.Equal(expected, probes.Select(snapshot.Score).ToArray());

        // A continuing clone follows the same next generation, not SeedFrom's intentional reset.
        Assert.True(source.Train(Examples(80, seed: 14), holdout));
        Assert.Equal(probes.Select(source.Score).ToArray(), probes.Select(challenger.Score).ToArray());
    }

    [Fact]
    public void Neural_ChallengerPreservesAdamAndTheSourceRemainsBitIdentical()
    {
        var path = Path.Join(_directory, "neural_weights.json");
        using var source = new NeuralScoringStrategy(path);
        var holdout = Examples(40, seed: 22);
        Assert.True(source.Train(Examples(80, seed: 21), holdout));
        var before = File.ReadAllBytes(path);
        var probes = Examples(20, seed: 23).Select(example => example.Features).ToArray();
        var expected = probes.Select(source.Score).ToArray();
        var snapshot = Assert.IsType<NeuralScoringStrategy.PredictionSnapshot>(source.CapturePredictionSnapshot());
        using var challenger = Assert.IsType<NeuralScoringStrategy>(source.CreateChallenger());
        Assert.Equal(expected, probes.Select(challenger.Score).ToArray());
        Assert.Equal(Read<int>(source, "_trainingGeneration"), Read<int>(challenger, "_trainingGeneration"));
        Assert.Equal(Read<int>(source, "_adamTimestep"), Read<int>(challenger, "_adamTimestep"));
        Assert.Null(Read<string?>(challenger, "_weightsPath"));
        foreach (var field in typeof(NeuralScoringStrategy).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                     .Where(field => field.FieldType == typeof(double[]) && (field.Name.StartsWith("_m", StringComparison.Ordinal) || field.Name.StartsWith("_v", StringComparison.Ordinal))))
        {
            var sourceArray = Assert.IsType<double[]>(field.GetValue(source));
            var targetArray = Assert.IsType<double[]>(field.GetValue(challenger));
            Assert.NotSame(sourceArray, targetArray);
            Assert.Equal(sourceArray, targetArray);
        }

        var nextFit = Examples(80, seed: 24);
        Assert.True(challenger.Train(nextFit, holdout));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(expected, probes.Select(source.Score).ToArray());
        Assert.Equal(expected, probes.Select(snapshot.Score).ToArray());
        Assert.True(source.Train(nextFit, holdout));
        Assert.Equal(probes.Select(source.Score).ToArray(), probes.Select(challenger.Score).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(40)]
    public void SnapshotsMatchRawAndStandardizedScores_AndSurviveTrainingDisposalAndForeignExports(int trainingCount)
    {
        var learned = new LearnedScoringStrategy();
        using var neural = new NeuralScoringStrategy();
        if (trainingCount > 0)
        {
            Assert.Equal(trainingCount >= 12, learned.Train(Examples(trainingCount, 31)));
            Assert.Equal(trainingCount >= 30, neural.Train(Examples(trainingCount, 31)));
        }

        var learnedSnapshot = learned.CapturePredictionSnapshot();
        var neuralSnapshot = Assert.IsType<NeuralScoringStrategy.PredictionSnapshot>(neural.CapturePredictionSnapshot());
        var features = Examples(24, seed: 32).Select(example => example.Features).Append(new CandidateFeatures
        {
            HasGenreEvidence = false,
            IsExternalCandidate = true,
            KnownExternalFeatures = 0
        }).ToArray();
        var learnedExpected = features.Select(learned.Score).ToArray();
        var neuralExpected = features.Select(neural.Score).ToArray();
        Assert.Equal(learnedExpected, features.Select(learnedSnapshot.Score).ToArray());
        Assert.Equal(neuralExpected, features.Select(neuralSnapshot.Score).ToArray());
        var exportedLearned = learnedSnapshot.ExportParameters();
        var exportedNeural = neuralSnapshot.ExportParameters();
        Assert.Equal(5, exportedLearned.Version);
        Assert.Equal(5, exportedNeural.Version);
        Array.Fill(exportedLearned.Weights, 99);
        Array.Fill(exportedNeural.WeightsIH, 99);
        if (exportedLearned.FeatureMeans is not null) Array.Fill(exportedLearned.FeatureMeans, 99);
        if (exportedNeural.ImputationMeans is not null) Array.Fill(exportedNeural.ImputationMeans, 99);
        Assert.True(learned.Train(Examples(80, seed: 33)));
        Assert.True(neural.Train(Examples(80, seed: 33)));
        neural.Dispose();
        Assert.Null(neural.CapturePredictionSnapshot());
        Assert.Null(neural.CreateChallenger());
        Assert.Equal(learnedExpected, features.Select(learnedSnapshot.Score).ToArray());
        Assert.Equal(neuralExpected, features.Select(neuralSnapshot.Score).ToArray());
        for (var i = 0; i < features.Length; i++)
        {
            Assert.Equal(learnedExpected[i], learnedSnapshot.ScoreWithExplanation(features[i]).FinalScore);
            Assert.Equal(neuralExpected[i], neuralSnapshot.ScoreWithExplanation(features[i]).FinalScore);
        }
    }

    [Fact]
    public void SnapshotConstructorsDoNotAdoptCallerOwnedParameterArrays()
    {
        var learned = new LearnedScoringStrategy();
        using var neural = new NeuralScoringStrategy();
        var learnedParameters = learned.CapturePredictionSnapshot().ExportParameters();
        var neuralParameters = neural.CapturePredictionSnapshot()!.ExportParameters();
        var learnedSnapshot = new LearnedScoringStrategy.PredictionSnapshot(learnedParameters);
        var neuralSnapshot = new NeuralScoringStrategy.PredictionSnapshot(neuralParameters);
        var feature = Examples(1, 41)[0].Features;
        var expectedL = learnedSnapshot.Score(feature);
        var expectedN = neuralSnapshot.Score(feature);
        Array.Fill(learnedParameters.Weights, 99);
        Array.Fill(neuralParameters.WeightsIH, 99);
        Assert.Equal(expectedL, learnedSnapshot.Score(feature));
        Assert.Equal(expectedN, neuralSnapshot.Score(feature));
        Assert.Throws<ArgumentNullException>(() => learnedSnapshot.Score(null!));
        Assert.Throws<ArgumentNullException>(() => neuralSnapshot.Score(null!));
        Assert.Throws<ArgumentNullException>(() => new LearnedScoringStrategy.PredictionSnapshot(null!));
        Assert.Throws<ArgumentNullException>(() => new NeuralScoringStrategy.PredictionSnapshot(null!));
    }

    private static T Read<T>(object instance, string field)
        => (T)instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

    private static List<TrainingExample> Examples(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(index =>
        {
            var positive = random.Next(2) == 0;
            return new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = positive ? .8 : .1,
                    CombinedCriticScore = positive ? .9 : .2,
                    RecencyScore = random.NextDouble(),
                    GenreCount = positive ? 4 : 1
                },
                Label = positive ? .85 : .05,
                HasStableObservationId = true,
                ObservationId = new Guid(seed * 1000 + index, 0, 0, new byte[8]),
                GeneratedAtUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
        }).ToList();
    }
}
