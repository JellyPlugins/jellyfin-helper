using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Atomic complete checkpoint round trips, invalid evidence/coordinates and non-destructive failure.</summary>
public sealed class AcceptedModelStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-accepted-model-" + Guid.NewGuid().ToString("N"));

    public AcceptedModelStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* best-effort temporary artifact cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temporary artifact cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AtomicBundleRoundTripPreservesV5ParametersAndDoesNotRewriteLegacyFiles()
    {
        var statePath = Path.Join(_directory, "ensemble_state.json");
        var weightsPath = Path.Join(_directory, "ml_weights.json");
        File.WriteAllText(statePath, "legacy-state-canary");
        File.WriteAllText(weightsPath, "legacy-weights-canary");
        var store = new AcceptedModelStore(statePath);
        var bundle = Bundle();
        Assert.True(store.Commit(bundle));
        var restored = Assert.IsType<AcceptedModelBundle>(store.Read());
        Assert.Equal(bundle.Mixture, restored.Mixture);
        Assert.Equal(5, restored.Learned.Version);
        Assert.Equal(5, restored.Neural!.Version);
        Assert.Equal(bundle.Revision, restored.Revision);
        Assert.Equal(bundle.AcceptanceObservationIds, restored.AcceptanceObservationIds);
        Assert.Equal("legacy-state-canary", File.ReadAllText(statePath));
        Assert.Equal("legacy-weights-canary", File.ReadAllText(weightsPath));
        var learned = Assert.IsType<LearnedScoringStrategy>(LearnedScoringStrategy.FromParameters(restored.Learned));
        using var neural = Assert.IsType<NeuralScoringStrategy>(NeuralScoringStrategy.FromParameters(restored.Neural));
        var features = new CandidateFeatures { GenreSimilarity = .7, CombinedCriticScore = .6 };
        var expectedL = learned.Score(features);
        var expectedN = neural.Score(features);
        Array.Fill(restored.Learned.Weights, 99);
        Array.Fill(restored.Neural.WeightsIH, 99);
        Assert.Equal(expectedL, learned.Score(features));
        Assert.Equal(expectedN, neural.Score(features));
    }

    [Fact]
    public void StorageFailureLeavesTheCompleteIncumbentFileBitIdentical()
    {
        var statePath = Path.Join(_directory, "ensemble_state.json");
        var store = new AcceptedModelStore(statePath);
        Assert.True(store.Commit(Bundle()));
        var before = File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!);
        var failing = new AcceptedModelStore(statePath, write: (_, _) => throw new IOException("injected atomic write failure"));
        var candidate = Bundle();
        candidate.Revision = 2;
        candidate.Mixture = new AdmissionMixture(.5, .1);
        Assert.False(failing.Commit(candidate));
        Assert.Equal(before, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
        Assert.Equal(1, store.Read()!.Revision);
    }

    [Fact]
    public void FailureAfterLedgerWriteKeepsTheIncumbentAndConservativelySpendsTheUnpublishedBlock()
    {
        var statePath = Path.Join(_directory, "ledger-first.json");
        var store = new AcceptedModelStore(statePath);
        var first = Bundle();
        Assert.True(store.Commit(first));
        var modelPath = AcceptedModelStore.GetBundlePath(statePath)!;
        var before = File.ReadAllBytes(modelPath);
        var next = Bundle();
        next.Revision = 2;
        next.AcceptanceObservationIds = Enumerable.Range(21, 20).Select(value => new Guid(value, 0, 0, new byte[8])).ToArray();
        var failing = new AcceptedModelStore(statePath, write: (path, contents) =>
        {
            if (path == modelPath) throw new IOException("failure after the evidence ledger commit");
            File.WriteAllText(path, contents);
        });
        Assert.False(failing.Commit(next));
        Assert.Equal(before, File.ReadAllBytes(modelPath));
        Assert.Equal(40, store.ReadSpentAcceptanceIds(store.Read()).Count);
        Assert.True(next.AcceptanceObservationIds.All(store.ReadSpentAcceptanceIds(store.Read()).Contains));
    }

    [Fact]
    public void LegacyCumulativeEvidenceMigratesWithoutBeingCopiedIntoEveryNewArchive()
    {
        var statePath = Path.Join(_directory, "migration.json");
        var legacy = Bundle();
        legacy.AcceptanceObservationIds = Enumerable.Range(1, 1200).Select(value => new Guid(value, 0, 0, new byte[8])).ToArray();
        File.WriteAllText(AcceptedModelStore.GetBundlePath(statePath)!, JsonSerializer.Serialize(legacy));
        var store = new AcceptedModelStore(statePath);
        Assert.Equal(1200, store.ReadSpentAcceptanceIds(store.Read()).Count);
        for (var revision = 2; revision <= 5; revision++)
        {
            var next = Bundle();
            next.Revision = revision;
            next.TrainedAtUtc = DateTime.UtcNow.AddMinutes(revision);
            next.AcceptanceObservationIds = Enumerable.Range(1200 + revision * 20, 20).Select(value => new Guid(value, 0, 0, new byte[8])).ToArray();
            Assert.True(store.Commit(next));
        }

        Assert.Equal(1280, store.ReadSpentAcceptanceIds(store.Read()).Count);
        Assert.True(legacy.AcceptanceObservationIds.All(store.ReadSpentAcceptanceIds(store.Read()).Contains));
        Assert.All(store.ReadHistory(), bundle => Assert.Equal(20, bundle.AcceptanceObservationIds.Length));
        Assert.Equal(2, store.ReadHistory().Length);
        Assert.Single(Directory.GetFiles(_directory, "*.acceptance-ledger.txt"));
    }

    [Theory]
    [InlineData("dimension")]
    [InlineData("nan")]
    [InlineData("version")]
    [InlineData("cap")]
    [InlineData("duplicate-evidence")]
    [InlineData("missing-utility")]
    [InlineData("frozen")]
    public void InvalidCandidateCannotOverwriteOrLoadAsAnAcceptedModel(string corruption)
    {
        var statePath = Path.Join(_directory, "ensemble_state.json");
        var store = new AcceptedModelStore(statePath);
        Assert.True(store.Commit(Bundle()));
        var before = File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!);
        var candidate = Bundle();
        switch (corruption)
        {
            case "dimension": candidate.Learned.Weights = [1]; break;
            case "nan": candidate.Neural!.WeightsIH[0] = double.NaN; break;
            case "version": candidate.Learned.Version = 4; break;
            case "cap": candidate.NeuralCap = 1; break;
            case "duplicate-evidence": candidate.AcceptanceObservationIds = Enumerable.Repeat(Guid.NewGuid(), 20).ToArray(); break;
            case "missing-utility": candidate.Utility = null; break;
            case "frozen": candidate.State.QualityGateFrozen = true; break;
        }

        Assert.False(store.Commit(candidate));
        Assert.Equal(before, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
        if (corruption == "nan")
        {
            Assert.Null(NeuralScoringStrategy.FromParameters(candidate.Neural!));
        }
        else
        {
            File.WriteAllText(AcceptedModelStore.GetBundlePath(statePath)!, JsonSerializer.Serialize(candidate));
            Assert.Null(store.Read());
        }
    }

    [Fact]
    public void MissingMalformedAndMemoryOnlyStoresDegradeWithoutProducingPerfectMetrics()
    {
        var statePath = Path.Join(_directory, "ensemble_state.json");
        var store = new AcceptedModelStore(statePath);
        Assert.Null(store.Read());
        File.WriteAllText(AcceptedModelStore.GetBundlePath(statePath)!, "{broken");
        Assert.Null(store.Read());
        Assert.True(new AcceptedModelStore(null).Commit(Bundle()));
        Assert.Null(new AcceptedModelStore(null).Read());
        Assert.Null(LearnedScoringStrategy.FromParameters(new LearnedScoringStrategy.WeightsData()));
        Assert.Null(NeuralScoringStrategy.FromParameters(new NeuralScoringStrategy.NeuralWeightsData()));
    }

    [Fact]
    public void ArchivesAreBoundedToTwoPreviousAcceptedVersionsAndExpireWithoutDeletingTheCurrentModel()
    {
        var statePath = Path.Join(_directory, "ensemble_state.json");
        var store = new AcceptedModelStore(statePath);
        for (var version = 1; version <= 5; version++)
        {
            var bundle = Bundle();
            bundle.Revision = version;
            bundle.TrainedAtUtc = DateTime.UtcNow.AddMinutes(version);
            Assert.True(store.Commit(bundle));
        }

        Assert.Equal(5, store.Read()!.Revision);
        Assert.Equal(new long[] { 4, 3 }, store.ReadHistory().Select(bundle => bundle.Revision).ToArray());
        Assert.Equal(2, Directory.GetFiles(_directory, "*.history.*.json").Length);
        var expired = Bundle();
        expired.TrainedAtUtc = DateTime.UtcNow.AddDays(-91);
        File.WriteAllText(AcceptedModelStore.GetBundlePath(statePath) + ".history.0.json", JsonSerializer.Serialize(expired));
        Assert.Single(store.ReadHistory());
        Assert.False(File.Exists(AcceptedModelStore.GetBundlePath(statePath) + ".history.0.json"));
        Assert.Equal(5, store.Read()!.Revision);
    }

    [Fact]
    public void OversizedReadsAndWritesFailClosedBeforeChangingTheIncumbentOrLedger()
    {
        var statePath = Path.Join(_directory, "size-bound.json");
        var store = new AcceptedModelStore(statePath);
        Assert.True(store.Commit(Bundle()));
        var path = AcceptedModelStore.GetBundlePath(statePath)!;
        var before = File.ReadAllBytes(path);
        var ledgerPath = AcceptedModelStore.GetLedgerPath(statePath)!;
        var ledger = File.ReadAllBytes(ledgerPath);
        var oversized = Bundle();
        oversized.State.UpdatedAt = new string('x', 11 * 1024 * 1024);
        Assert.False(store.Commit(oversized));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(ledger, File.ReadAllBytes(ledgerPath));
        using (var file = File.OpenWrite(path)) file.SetLength(11 * 1024 * 1024);
        Assert.Null(store.Read());
        Assert.Equal(ledger, File.ReadAllBytes(ledgerPath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("retired")]
    public void UnusableArchiveSlotsNeverReplaceOrDeleteTheActiveModel(string kind)
    {
        var statePath = Path.Join(_directory, "bad-history.json");
        var store = new AcceptedModelStore(statePath);
        Assert.True(store.Commit(Bundle()));
        var activePath = AcceptedModelStore.GetBundlePath(statePath)!;
        var before = File.ReadAllBytes(activePath);
        var historyPath = activePath + ".history.0.json";
        var historical = Bundle();
        if (kind == "invalid") historical.Version = 99;
        if (kind == "retired")
        {
            historical.Retired = true;
            historical.State.QualityGateFrozen = true;
            historical.RetirementUtility = new AdmissionUtility(.1, .8, null, null, .01);
        }

        File.WriteAllText(historyPath, kind == "null" ? "null" : JsonSerializer.Serialize(historical));
        if (kind == "malformed") File.WriteAllText(historyPath, "{invalid-json}");
        if (kind == "oversized")
        {
            using var file = File.OpenWrite(historyPath);
            file.SetLength(11 * 1024 * 1024);
        }

        Assert.Empty(store.ReadHistory());
        Assert.Equal(before, File.ReadAllBytes(activePath));
        Assert.Equal(1, store.Read()!.Revision);
    }

    [Fact]
    public void ExpiredActiveVersionIsRetainedButNotArchivedDuringAUsefulReplacement()
    {
        var statePath = Path.Join(_directory, "expired-active.json");
        var store = new AcceptedModelStore(statePath);
        var old = Bundle();
        old.TrainedAtUtc = DateTime.UtcNow.AddDays(-91);
        Assert.True(store.Commit(old));
        Assert.Equal(old.TrainedAtUtc, store.Read()!.TrainedAtUtc);
        var next = Bundle();
        next.Revision = 2;
        Assert.True(store.Commit(next));
        Assert.Empty(store.ReadHistory());
        Assert.Equal(2, store.Read()!.Revision);
    }

    [Fact]
    public void MemoryOnlyHistoryAndJsonNullDoNotCreateAClaimedModel()
    {
        Assert.Empty(new AcceptedModelStore(null).ReadHistory());
        var statePath = Path.Join(_directory, "json-null.json");
        File.WriteAllText(AcceptedModelStore.GetBundlePath(statePath)!, "null");
        Assert.Null(new AcceptedModelStore(statePath).Read());
        Assert.Throws<ArgumentNullException>(() => new AcceptedModelStore(null).Commit(null!));
    }

    private static AcceptedModelBundle Bundle()
    {
        var learned = new LearnedScoringStrategy();
        using var neural = new NeuralScoringStrategy();
        return new AcceptedModelBundle
        {
            Learned = learned.CapturePredictionSnapshot().ExportParameters(),
            Neural = neural.CapturePredictionSnapshot()!.ExportParameters(),
            Mixture = new AdmissionMixture(.4, .1),
            AlphaCap = .75,
            NeuralCap = .3,
            Revision = 1,
            TrainedAtUtc = DateTime.UtcNow,
            AcceptanceObservationIds = Enumerable.Range(1, 20).Select(value => new Guid(value, 0, 0, new byte[8])).ToArray(),
            Utility = new AdmissionUtility(.02, .9, .03, .01, null),
            State = new EnsembleScoringStrategy.EnsembleStateData
            {
                SchemaVersion = EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion,
                Alpha = .75,
                NeuralBeta = .2,
                QualityGateFrozen = false
            }
        };
    }
}
