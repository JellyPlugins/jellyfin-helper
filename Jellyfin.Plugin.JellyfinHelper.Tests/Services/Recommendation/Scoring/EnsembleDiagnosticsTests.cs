using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>
///     Tests for <see cref="EnsembleScoringStrategy.GetDiagnosticsSnapshot"/>: after training, the snapshot exposes
///     coherent values (alpha within bounds, training count matches, neural-enabled flag reflects construction) and it
///     never mutates state. Also covers the no-neural construction and a frozen quality-gate case restored from state.
/// </summary>
public sealed class EnsembleDiagnosticsTests : IDisposable
{
    private const double AlphaMin = EnsembleScoringStrategy.DefaultAlphaMin;
    private const double AlphaMax = EnsembleScoringStrategy.DefaultAlphaMax;

    // Cached NaN-tolerant options matching EnsembleScoringStrategy's own reader (avoid per-call allocation).
    private static readonly JsonSerializerOptions StateSerializerOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string _tempDir;

    public EnsembleDiagnosticsTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "jf-helper-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (DirectoryNotFoundException)
        {
            // best-effort cleanup - directory may already be gone
        }
        catch (IOException)
        {
            // best-effort cleanup - file may be locked on CI
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup - permission edge cases on CI
        }

        GC.SuppressFinalize(this);
    }

    private string WeightsPath => Path.Join(_tempDir, "ml_weights.json");

    private string StatePath => Path.Join(_tempDir, "ensemble_state.json");

    // Builds an ensemble whose state persists into the test's temp dir (never the CWD).
    private EnsembleScoringStrategy BuildSut(NeuralScoringStrategy? neural = null)
    {
        var learned = new LearnedScoringStrategy(WeightsPath);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        return new EnsembleScoringStrategy(learned, heuristic, neural, statePath: StatePath);
    }

    // Cleanly separable data -> low validation loss (passes the quality gate), so alpha progresses.
    private static List<TrainingExample> CleanExamples(int count, int seed = 42)
    {
        var examples = new List<TrainingExample>(count);
        var rng = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            var positive = i % 2 == 0;
            examples.Add(new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = positive ? 0.9 : 0.05,
                    CombinedCriticScore = positive ? 0.85 : 0.1,
                    RecencyScore = positive ? 0.7 : 0.2,
                    CollaborativeScore = rng.NextDouble() * 0.1,
                    GenreCount = positive ? 4 : 1
                },
                Label = positive ? 1.0 : 0.0
            });
        }

        return examples;
    }

    // Serializes a persisted-state object with NaN-tolerant options matching EnsembleScoringStrategy's own reader.
    private static string SerializeState(object state)
    {
        return JsonSerializer.Serialize(state, StateSerializerOptions);
    }

    [Fact]
    public void GetDiagnosticsSnapshot_AfterTraining_ReturnsCoherentValues()
    {
        using var ensemble = BuildSut();
        Assert.True(ensemble.Train(CleanExamples(80)));

        var diag = ensemble.GetDiagnosticsSnapshot();

        // Bounds carry the defaults and alpha stays inside them.
        Assert.Equal(AlphaMin, diag.AlphaMin, 6);
        Assert.Equal(AlphaMax, diag.AlphaMax, 6);
        Assert.InRange(diag.Alpha, diag.AlphaMin, diag.AlphaMax);

        // Counts mirror the per-field getters and the training just performed.
        Assert.Equal(ensemble.TrainingExampleCount, diag.TrainingExampleCount);
        Assert.Equal(80, diag.TrainingExampleCount);
        Assert.Equal(ensemble.MetricsHistoryCount, diag.MetricsHistoryCount);
        Assert.Equal(ensemble.CurrentAlpha, diag.Alpha, 9);
        Assert.Equal(ensemble.CurrentNeuralBeta, diag.NeuralBeta, 9);
        Assert.Equal(ensemble.IsQualityGateFrozen, diag.QualityGateFrozen);
        Assert.Equal(ensemble.SigmoidMidpointOffset, diag.SigmoidMidpointOffset, 9);
        Assert.Equal(ensemble.EffectiveSigmoidMidpoint, diag.EffectiveSigmoidMidpoint, 9);
        Assert.Equal(ensemble.LastTrend, diag.Trend);

        // The convenience ctor wires no neural strategy.
        Assert.False(diag.NeuralEnabled);
    }

    [Fact]
    public void GetDiagnosticsSnapshot_WithNeural_ReportsNeuralEnabled()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        using var neural = new NeuralScoringStrategy();
        using var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural);

        Assert.True(ensemble.GetDiagnosticsSnapshot().NeuralEnabled);
    }

    [Fact]
    public void GetDiagnosticsSnapshot_WithoutNeural_ReportsNeuralDisabled()
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);
        using var ensemble = new EnsembleScoringStrategy(learned, heuristic, neural: null);

        Assert.False(ensemble.GetDiagnosticsSnapshot().NeuralEnabled);
    }

    [Fact]
    public void GetDiagnosticsSnapshot_FrozenQualityGate_ReflectsFreeze()
    {
        // A quality-gate freeze is set only when validation loss reaches the ceiling, which noisy synthetic data
        // rarely hits deterministically. Restore a persisted state with the freeze flag set so the snapshot's
        // reflection of it is verified without depending on training randomness.
        File.WriteAllText(StatePath, SerializeState(new
        {
            SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
            TrainingExampleCount = 200,
            Alpha = AlphaMin,
            NeuralBeta = 0.0,
            QualityGateFrozen = true,
            SigmoidMidpointOffset = 0.0,
            UpdatedAt = "2026-01-01T00:00:00.0000000Z",
            MetricsHistory = Array.Empty<object>()
        }));

        using var sut = BuildSut();
        var diag = sut.GetDiagnosticsSnapshot();

        Assert.True(diag.QualityGateFrozen);
        Assert.Equal(200, diag.TrainingExampleCount);
        // The persisted frozen alpha stays at the heuristic-dominant minimum.
        Assert.Equal(diag.AlphaMin, diag.Alpha, 6);
    }

    [Fact]
    public void GetDiagnosticsSnapshot_IsPureRead_DoesNotMutateState()
    {
        using var ensemble = BuildSut();
        Assert.True(ensemble.Train(CleanExamples(80)));

        var first = ensemble.GetDiagnosticsSnapshot();
        var second = ensemble.GetDiagnosticsSnapshot();

        Assert.Equal(first, second);
        Assert.Equal(first.TrainingExampleCount, ensemble.TrainingExampleCount);
    }
}
