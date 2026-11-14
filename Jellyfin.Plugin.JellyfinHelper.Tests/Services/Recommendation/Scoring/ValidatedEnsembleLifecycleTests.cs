using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Real detached fits prove atomic promotion, restart, retained incumbents and immutable Neural borrowing.</summary>
public sealed class ValidatedEnsembleLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-validated-lifecycle-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public ValidatedEnsembleLifecycleTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* temporary artifact cleanup */ }
        catch (UnauthorizedAccessException) { /* temporary artifact cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NonlinearCandidatePromotesCompleteVersionAndRestartsWithTheSameScores()
    {
        using var ensemble = Create("global");
        Assert.True(ensemble.TrainValidated(Examples(600, 11), Examples(400, 12), true, TestContext.Current.CancellationToken));
        Assert.True(ensemble.LastAdmission!.Accepted);
        var diagnostics = ensemble.GetDiagnosticsSnapshot();
        Assert.True(diagnostics.NeuralShare > 0);
        Assert.InRange(diagnostics.EffectiveAlpha, 0, .75);
        Assert.InRange(diagnostics.NeuralShare, 0, .3);
        Assert.False(diagnostics.AllowsExploration);
        var features = Examples(24, 13).Select(row => row.Features).ToArray();
        var before = features.Select(ensemble.Score).ToArray();
        using var restarted = Create("global");
        Assert.Equal(before, features.Select(restarted.Score).ToArray());
        Assert.Equal(diagnostics.EffectiveAlpha, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.Equal(diagnostics.NeuralShare, restarted.GetDiagnosticsSnapshot().NeuralShare);
        Assert.False(File.Exists(Path.Join(_directory, "global-ml.json")));
        Assert.False(File.Exists(Path.Join(_directory, "global-state.json")));
    }

    [Fact]
    public void RejectedOrUninformativeRefreshLeavesIncumbentPredictionsAndCheckpointBitIdentical()
    {
        using var ensemble = Create("retention");
        Assert.True(ensemble.TrainValidated(Examples(600, 21), Examples(400, 22), true, TestContext.Current.CancellationToken));
        Assert.True(ensemble.LastAdmission!.Accepted);
        var path = AcceptedModelStore.GetBundlePath(Path.Join(_directory, "retention-state.json"))!;
        var beforeFile = File.ReadAllBytes(path);
        var probes = Examples(24, 23).Select(row => row.Features).ToArray();
        var before = probes.Select(ensemble.Score).ToArray();
        var badFit = Examples(600, 24);
        foreach (var row in badFit) row.Label = .85 - row.Label;
        Assert.True(ensemble.TrainValidated(badFit, Examples(400, 25), true, TestContext.Current.CancellationToken));
        Assert.False(ensemble.LastAdmission!.Accepted);
        Assert.Equal(before, probes.Select(ensemble.Score).ToArray());
        Assert.Equal(beforeFile, File.ReadAllBytes(path));

        var homogeneous = Examples(400, 26);
        foreach (var row in homogeneous) row.Label = .85;
        Assert.True(ensemble.TrainValidated(Examples(600, 27), homogeneous, true, TestContext.Current.CancellationToken));
        Assert.False(ensemble.LastAdmission!.Accepted);
        Assert.Equal(before, probes.Select(ensemble.Score).ToArray());
        Assert.Equal(beforeFile, File.ReadAllBytes(path));
    }

    [Fact]
    public void UnchangedAcceptanceTitlesCannotPromoteAnotherTrainingGeneration()
    {
        using var ensemble = Create("replay");
        var fit = Examples(600, 31);
        var holdout = Examples(400, 32);
        Assert.True(ensemble.TrainValidated(fit, holdout, true, TestContext.Current.CancellationToken));
        Assert.True(ensemble.LastAdmission!.Accepted);
        var path = AcceptedModelStore.GetBundlePath(Path.Join(_directory, "replay-state.json"))!;
        var before = File.ReadAllBytes(path);
        Assert.False(ensemble.TrainValidated(fit, holdout, true, TestContext.Current.CancellationToken));
        Assert.False(ensemble.LastAdmission!.Accepted);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void PositiveOnlyInitialFitMayServeProvisionallyWithoutClaimingVerifiedRankingAndRestartsCoherently()
    {
        using var ensemble = Create("rejected-restart");
        var holdout = Examples(80, 141);
        foreach (var row in holdout) row.Label = .85;
        Assert.True(ensemble.TrainValidated(Examples(200, 142), holdout, false, TestContext.Current.CancellationToken));
        Assert.False(ensemble.LastAdmission!.Accepted);
        var probes = Examples(24, 143).Select(row => row.Features).ToArray();
        var before = probes.Select(ensemble.Score).ToArray();
        Assert.InRange(ensemble.GetDiagnosticsSnapshot().EffectiveAlpha, .3, .75);
        var bundle = new AcceptedModelStore(Path.Join(_directory, "rejected-restart-state.json")).Read()!;
        Assert.True(bundle.IsProvisional);
        Assert.Null(bundle.Utility);
        using var restarted = Create("rejected-restart");
        Assert.Equal(before, probes.Select(restarted.Score).ToArray());
        Assert.Equal(ensemble.GetDiagnosticsSnapshot().EffectiveAlpha, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.False(restarted.GetDiagnosticsSnapshot().AllowsExploration);
        Assert.Equal(ensemble.LastAdmission, restarted.LastAdmission);
    }

    [Fact]
    public void InsufficientFittingStillPersistsValidatedModeWithoutInventingRankingMetrics()
    {
        using var ensemble = Create("insufficient-fit");
        Assert.False(ensemble.TrainValidated(Examples(2, 201), [], false, TestContext.Current.CancellationToken));
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        using var restarted = Create("insufficient-fit");
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.Equal(EnsembleScoringStrategy.MetricsTrend.InsufficientData, restarted.LastTrend);
        Assert.Equal(0, restarted.MetricsHistoryCount);
        Assert.False(restarted.GetDiagnosticsSnapshot().AllowsExploration);
    }

    [Fact]
    public void HomogeneousAssessmentsRemainUnknownAcrossRestartInsteadOfFabricatingAWarmedTrend()
    {
        using var ensemble = Create("unknown-trend");
        var holdout = Examples(100, 211);
        foreach (var row in holdout) row.Label = .85;
        for (var iteration = 0; iteration < EnsembleScoringStrategy.TrendMinSnapshots; iteration++)
        {
            Assert.True(ensemble.TrainValidated(Examples(40, 212 + iteration), holdout, false, TestContext.Current.CancellationToken));
        }

        using var restarted = Create("unknown-trend");
        Assert.Equal(ensemble.MetricsHistoryCount, restarted.MetricsHistoryCount);
        Assert.Equal(EnsembleScoringStrategy.MetricsTrend.InsufficientData, restarted.LastTrend);
        Assert.Null(restarted.LastAdmission!.Utility.CandidateNdcgAtK);
        Assert.InRange(restarted.GetDiagnosticsSnapshot().EffectiveAlpha, .3, .75);
    }

    [Fact]
    public void LargeInitialAcceptanceBlockCanPromoteAndStillRejectReplayAfterRestart()
    {
        using var ensemble = Create("large-evidence");
        var fit = Examples(600, 151);
        var holdout = Examples(2600, 152);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.True(ensemble.LastAdmission!.Accepted);
        Assert.True(ensemble.GetDiagnosticsSnapshot().ModelRevision > 0);
        using var restarted = Create("large-evidence");
        Assert.False(restarted.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.False(restarted.LastAdmission!.Accepted);
        Assert.Equal(1, restarted.GetDiagnosticsSnapshot().ModelRevision);
    }

    [Fact]
    public void ValidatedTrainingTrendsUseTheEvaluatedMixtureInsteadOfUntouchedLegacyMetrics()
    {
        using var ensemble = Create("mixture-trend");
        for (var index = 0; index < EnsembleScoringStrategy.TrendMinSnapshots; index++)
        {
            var fit = Examples(200, 160 + index * 2);
            var holdout = Examples(100, 161 + index * 2);
            foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
            Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
            Assert.NotNull(ensemble.LastAdmission!.Utility.CandidateNdcg);
        }

        Assert.NotEqual(EnsembleScoringStrategy.MetricsTrend.InsufficientData, ensemble.LastTrend);
        using var restarted = Create("mixture-trend");
        Assert.Equal(ensemble.LastTrend, restarted.LastTrend);
        Assert.Equal(ensemble.MetricsHistoryCount, restarted.MetricsHistoryCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public void MissingOrCorruptRequiredLedgerBlocksAdmissionWithoutChangingAcceptedScores(string damage)
    {
        using var ensemble = Create("ledger-guard");
        var fit = Examples(600, 171);
        var holdout = Examples(100, 172);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.True(ensemble.LastAdmission!.Accepted);
        var probes = Examples(24, 173).Select(row => row.Features).ToArray();
        var scores = probes.Select(ensemble.Score).ToArray();
        var ledgerPath = AcceptedModelStore.GetLedgerPath(Path.Join(_directory, "ledger-guard-state.json"))!;
        if (damage == "missing") File.Delete(ledgerPath);
        else File.WriteAllText(ledgerPath, "broken-ledger");
        using var restarted = Create("ledger-guard");
        Assert.Equal(scores, probes.Select(restarted.Score).ToArray());
        Assert.False(restarted.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("AcceptanceLedgerUnavailable", restarted.LastAdmission!.Reason);
        Assert.Equal(scores, probes.Select(restarted.Score).ToArray());
        Assert.Equal(1, restarted.GetDiagnosticsSnapshot().ModelRevision);
    }

    [Fact]
    public void FreshPromotionPastTheOldLifetimeLimitPreservesAllPreviouslySpentTitles()
    {
        var statePath = Path.Join(_directory, "past-limit-state.json");
        var previous = LinearBundle(inverted: true, revision: 1, DateTime.UtcNow.AddHours(-1));
        previous.AcceptanceObservationIds = Enumerable.Range(1, 1000).Select(value => new Guid(value, 12, 0, new byte[8])).ToArray();
        var store = new AcceptedModelStore(statePath);
        Assert.True(store.Commit(previous));
        using var ensemble = Create("past-limit");
        var fit = Examples(600, 181);
        var holdout = Examples(200, 182);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.True(ensemble.LastAdmission!.Accepted);
        Assert.Equal(2, store.Read()!.Revision);
        Assert.True(store.ReadSpentAcceptanceIds(store.Read()).Count > 1000);
        Assert.True(previous.AcceptanceObservationIds.All(store.ReadSpentAcceptanceIds(store.Read()).Contains));
        Assert.True(store.Read()!.AcceptanceObservationIds.Length < 1000);
    }

    [Fact]
    public void FreshWithdrawalPastTheOldLifetimeLimitPersistsAndRestartsHeuristicOnly()
    {
        var statePath = Path.Join(_directory, "withdraw-limit-state.json");
        var previous = LinearBundle(inverted: true, revision: 1, DateTime.UtcNow.AddHours(-1));
        previous.AcceptanceObservationIds = Enumerable.Range(1, 1000).Select(value => new Guid(value, 12, 0, new byte[8])).ToArray();
        var store = new AcceptedModelStore(statePath);
        Assert.True(store.Commit(previous));
        using var ensemble = Create("withdraw-limit");
        var fit = Examples(600, 191);
        var holdout = Examples(200, 192);
        foreach (var row in fit)
        {
            row.Features.CombinedCriticScore = row.Label;
            row.Label = .85 - row.Label;
        }

        foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("IncumbentWithdrawnOnNewEvidence", ensemble.LastAdmission!.Reason);
        Assert.True(store.Read()!.Retired);
        Assert.True(store.ReadSpentAcceptanceIds(store.Read()).Count > 1000);
        using var restarted = Create("withdraw-limit");
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.Equal(ensemble.LastAdmission, restarted.LastAdmission);
    }

    [Fact]
    public void HistoricalNeuralCannotRegainInfluenceUsingItsOldIncrementalEvidence()
    {
        var statePath = Path.Join(_directory, "historical-neural-state.json");
        var store = new AcceptedModelStore(statePath);
        var older = LinearBundle(inverted: false, revision: 1, DateTime.UtcNow.AddHours(-2));
        using var neural = new NeuralScoringStrategy();
        older.Neural = neural.CapturePredictionSnapshot()!.ExportParameters();
        older.Mixture = new AdmissionMixture(.5, .1);
        older.NeuralCap = .3;
        older.State.NeuralBeta = older.Mixture.Beta;
        older.Utility = older.Utility! with { LowerNeuralImprovement = .01 };
        Assert.True(store.Commit(older));
        Assert.True(store.Commit(LinearBundle(inverted: true, revision: 2, DateTime.UtcNow.AddHours(-1))));
        using var ensemble = Create("historical-neural");
        var fit = Examples(600, 221);
        var holdout = Examples(200, 222);
        foreach (var row in fit)
        {
            row.Features.CombinedCriticScore = row.Label;
            row.Label = .85 - row.Label;
        }

        foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.NotEqual("HistoricalModelRestoredOnNewEvidence", ensemble.LastAdmission!.Reason);
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().NeuralShare);
    }

    [Fact]
    public void UsefulHistoricalNeuralRestoresItsExactParametersOnlyWithFreshIncrementalEvidence()
    {
        var statePath = Path.Join(_directory, "useful-neural-history-state.json");
        var store = new AcceptedModelStore(statePath);
        using (var trained = Create("useful-neural-history"))
        {
            Assert.True(trained.TrainValidated(Examples(600, 241), Examples(400, 242), true, TestContext.Current.CancellationToken));
            Assert.True(trained.GetDiagnosticsSnapshot().NeuralShare > 0);
        }

        var older = store.Read()!;
        Assert.True(store.Commit(LinearBundle(inverted: true, revision: 2, DateTime.UtcNow.AddSeconds(1))));
        using var ensemble = Create("useful-neural-history");
        Assert.True(ensemble.TrainValidated(Examples(600, 243), Examples(400, 244), false, TestContext.Current.CancellationToken));
        Assert.Equal("HistoricalModelRestoredOnNewEvidence", ensemble.LastAdmission!.Reason);
        var restored = store.Read()!;
        Assert.True(restored.Utility!.LowerNeuralImprovement > 0);
        Assert.Equal(older.Neural!.WeightsIH, restored.Neural!.WeightsIH);
        Assert.Equal(older.TrainedAtUtc, restored.TrainedAtUtc);
        Assert.True(ensemble.GetDiagnosticsSnapshot().NeuralShare > 0);
    }

    [Fact]
    public void RejectedHistoricalCandidateCannotPreventWithdrawalOfAHarmfulIncumbent()
    {
        var statePath = Path.Join(_directory, "historical-rejected-state.json");
        var store = new AcceptedModelStore(statePath);
        var older = LinearBundle(inverted: false, revision: 1, DateTime.UtcNow.AddHours(-2));
        older.Learned.Weights[(int)FeatureIndex.CombinedCriticScore] = 0;
        older.Learned.Weights[(int)FeatureIndex.RecencyScore] = 1;
        Assert.True(store.Commit(older));
        Assert.True(store.Commit(LinearBundle(inverted: true, revision: 2, DateTime.UtcNow.AddHours(-1))));
        using var ensemble = Create("historical-rejected");
        var fit = Examples(600, 231);
        var holdout = Examples(200, 232);
        foreach (var row in fit)
        {
            row.Features.CombinedCriticScore = row.Label;
            row.Label = .85 - row.Label;
        }

        foreach (var row in holdout)
        {
            row.Features.CombinedCriticScore = row.Label;
            var calibration = (SHA256.HashData(row.ObservationId.ToByteArray())[0] & 1) == 0;
            row.Features.RecencyScore = calibration ? row.Label : .85 - row.Label;
        }

        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("IncumbentWithdrawnOnNewEvidence", ensemble.LastAdmission!.Reason);
        Assert.True(store.Read()!.Retired);
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
    }

    [Fact]
    public void ExplanationCapturePinsOnePredictionAndBoundsAcrossAConcurrentPublication()
    {
        var statePath = Path.Join(_directory, "explanation-version-state.json");
        var first = LinearBundle(inverted: false, revision: 1, DateTime.UtcNow.AddHours(-1));
        Assert.True(new AcceptedModelStore(statePath).Commit(first));
        using var ensemble = Create("explanation-version");
        var features = new CandidateFeatures { GenreSimilarity = .2, CombinedCriticScore = .85, RecencyScore = .8 };
        var captured = Assert.IsType<Func<CandidateFeatures, double>>(ensemble.CaptureAcceptedExplanationScore());
        var before = ScoringHelper.BuildMarginalExplanation(features, captured, ensemble.Name);
        ensemble.Reconfigure(0, .2, .7);
        var next = LinearBundle(inverted: true, revision: 2, DateTime.UtcNow);
        var prediction = new AcceptedPrediction(ensemble.HeuristicStrategy, new LearnedScoringStrategy.PredictionSnapshot(next.Learned), null, next.Mixture, next.NeuralCap);
        typeof(EnsembleScoringStrategy).GetField("_acceptedPrediction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(ensemble, prediction);
        var after = ScoringHelper.BuildMarginalExplanation(features, captured, ensemble.Name);
        Assert.Equal(before.FinalScore, after.FinalScore);
        Assert.Equal(before.GenreContribution, after.GenreContribution);
        Assert.Equal(before.RatingContribution, after.RatingContribution);
        Assert.Equal(before.RecencyContribution, after.RecencyContribution);
        Assert.Equal(before.DominantSignal, after.DominantSignal);
        Assert.NotEqual(before.FinalScore, ensemble.ScoreWithExplanation(features).FinalScore);
        Assert.Equal(ensemble.ScoreWithExplanation(features).FinalScore, ensemble.ScoreWithExplanationAndOffset(features, .1).FinalScore);
    }

    [Fact]
    public void HarmfulLegacyV5ModelIsPersistentlySuppressedWithoutFabricatingAnAcceptedBundleOrDeletingWeights()
    {
        var statePath = Path.Join(_directory, "legacy-withdraw-state.json");
        var weightsPath = Path.Join(_directory, "legacy-withdraw-ml.json");
        var legacy = LinearBundle(inverted: true, revision: 1, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(weightsPath, System.Text.Json.JsonSerializer.Serialize(legacy.Learned));
        File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(legacy.State));
        var beforeWeights = File.ReadAllBytes(weightsPath);
        var beforeState = File.ReadAllBytes(statePath);
        using var ensemble = Create("legacy-withdraw");
        var fit = Examples(600, 251);
        var holdout = Examples(200, 252);
        foreach (var row in fit)
        {
            row.Features.CombinedCriticScore = row.Label;
            row.Label = .85 - row.Label;
        }

        foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("LegacyModelWithdrawnOnNewEvidence", ensemble.LastAdmission!.Reason);
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.False(File.Exists(AcceptedModelStore.GetBundlePath(statePath)));
        Assert.Equal(beforeWeights, File.ReadAllBytes(weightsPath));
        Assert.Equal(beforeState, File.ReadAllBytes(statePath));
        using var restarted = Create("legacy-withdraw");
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.Equal(ensemble.LastAdmission, restarted.LastAdmission);
        var goodFit = Examples(600, 253);
        var fresh = Examples(200, 254);
        foreach (var row in goodFit.Concat(fresh)) row.Features.CombinedCriticScore = row.Label;
        Assert.True(restarted.TrainValidated(goodFit, fresh, false, TestContext.Current.CancellationToken));
        Assert.True(restarted.LastAdmission!.Accepted);
        Assert.True(restarted.GetDiagnosticsSnapshot().EffectiveAlpha > 0);
        using var promotedRestart = Create("legacy-withdraw");
        Assert.Equal(restarted.GetDiagnosticsSnapshot().EffectiveAlpha, promotedRestart.GetDiagnosticsSnapshot().EffectiveAlpha);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("malformed", false)]
    [InlineData("invalid-parameters", true)]
    public void LostAcceptedBundleAfterLegacyReplacementCannotReactivateWithdrawnV5Weights(string damage, bool removeLegacyState)
    {
        var scope = "lost-promoted-" + damage + "-" + removeLegacyState;
        var statePath = Path.Join(_directory, scope + "-state.json");
        var weightsPath = Path.Join(_directory, scope + "-ml.json");
        var legacy = LinearBundle(inverted: true, revision: 1, DateTime.UtcNow);
        File.WriteAllText(weightsPath, System.Text.Json.JsonSerializer.Serialize(legacy.Learned));
        File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(legacy.State));
        var originalWeights = File.ReadAllBytes(weightsPath);
        using (var ensemble = Create(scope))
        {
            var badFit = Examples(600, 301);
            var holdout = Examples(200, 302);
            foreach (var row in badFit)
            {
                row.Features.CombinedCriticScore = row.Label;
                row.Label = .85 - row.Label;
            }

            foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
            Assert.True(ensemble.TrainValidated(badFit, holdout, false, TestContext.Current.CancellationToken));
            Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
            var goodFit = Examples(600, 303);
            var fresh = Examples(200, 304);
            foreach (var row in goodFit.Concat(fresh)) row.Features.CombinedCriticScore = row.Label;
            Assert.True(ensemble.TrainValidated(goodFit, fresh, false, TestContext.Current.CancellationToken));
            Assert.True(ensemble.LastAdmission!.Accepted);
        }

        var bundlePath = AcceptedModelStore.GetBundlePath(statePath)!;
        if (damage == "missing")
        {
            File.Delete(bundlePath);
        }
        else if (damage == "malformed")
        {
            File.WriteAllText(bundlePath, "{broken}");
        }
        else
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(bundlePath))!;
            node["Learned"]!["Weights"] = new System.Text.Json.Nodes.JsonArray(1);
            File.WriteAllText(bundlePath, node.ToJsonString());
        }

        if (removeLegacyState) File.Delete(statePath);
        using var restarted = Create(scope);
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1);
        var probes = Examples(24, 305).Select(row => row.Features).ToArray();
        Assert.Equal(probes.Select(heuristic.Score), probes.Select(restarted.Score));
        Assert.False(restarted.GetDiagnosticsSnapshot().AllowsExploration);
        Assert.Equal(originalWeights, File.ReadAllBytes(weightsPath));
    }

    [Theory]
    [InlineData("ledger")]
    [InlineData("assessment")]
    public void FailedLegacySuppressionCommitCannotPublishSuppressionOrModifyV5Weights(string blocked)
    {
        var statePath = Path.Join(_directory, "legacy-write-failure-state.json");
        var weightsPath = Path.Join(_directory, "legacy-write-failure-ml.json");
        var legacy = LinearBundle(inverted: true, revision: 1, DateTime.UtcNow);
        File.WriteAllText(weightsPath, System.Text.Json.JsonSerializer.Serialize(legacy.Learned));
        File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(legacy.State));
        using var ensemble = Create("legacy-write-failure");
        var probes = Examples(24, 261).Select(row => row.Features).ToArray();
        var before = probes.Select(ensemble.Score).ToArray();
        var weights = File.ReadAllBytes(weightsPath);
        Directory.CreateDirectory(blocked == "ledger" ? AcceptedModelStore.GetLedgerPath(statePath)! : statePath + ".assessment.json");
        var fit = Examples(600, 262);
        var holdout = Examples(200, 263);
        foreach (var row in fit)
        {
            row.Features.CombinedCriticScore = row.Label;
            row.Label = .85 - row.Label;
        }

        foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("CheckpointCommitFailed", ensemble.LastAdmission!.Reason);
        Assert.Equal(before, probes.Select(ensemble.Score));
        Assert.Equal(weights, File.ReadAllBytes(weightsPath));
        Assert.False(File.Exists(AcceptedModelStore.GetBundlePath(statePath)));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("invalid-proof")]
    public void DamagedLegacySuppressionMetadataCannotReactivateTheOldModel(string damage)
    {
        var statePath = Path.Join(_directory, "legacy-damaged-state.json");
        var weightsPath = Path.Join(_directory, "legacy-damaged-ml.json");
        var legacy = LinearBundle(inverted: true, revision: 1, DateTime.UtcNow);
        File.WriteAllText(weightsPath, System.Text.Json.JsonSerializer.Serialize(legacy.Learned));
        File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(legacy.State));
        using (var ensemble = Create("legacy-damaged"))
        {
            var fit = Examples(600, 271);
            var holdout = Examples(200, 272);
            foreach (var row in fit)
            {
                row.Features.CombinedCriticScore = row.Label;
                row.Label = .85 - row.Label;
            }

            foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
            Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
            Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        }

        var path = statePath + ".assessment.json";
        if (damage == "missing")
        {
            File.Delete(path);
        }
        else if (damage == "corrupt")
        {
            File.WriteAllText(path, "{broken}");
        }
        else
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            node["LegacyWithdrawalUtility"] = null;
            File.WriteAllText(path, node.ToJsonString());
        }

        using var restarted = Create("legacy-damaged");
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.True(File.Exists(weightsPath));
        Assert.False(restarted.GetDiagnosticsSnapshot().AllowsExploration);
    }

    [Theory]
    [InlineData(false, "minimum")]
    [InlineData(false, "maximum")]
    [InlineData(false, "floor")]
    [InlineData(true, "minimum")]
    [InlineData(true, "maximum")]
    [InlineData(true, "floor")]
    public void ConfigurationChangeCannotCommitWithdrawalUnderStaleEvidence(bool legacyModel, string changed)
    {
        var scope = "configuration-" + legacyModel + "-" + changed;
        var statePath = Path.Join(_directory, scope + "-state.json");
        var original = LinearBundle(inverted: true, revision: 1, DateTime.UtcNow);
        if (legacyModel)
        {
            File.WriteAllText(Path.Join(_directory, scope + "-ml.json"), System.Text.Json.JsonSerializer.Serialize(original.Learned));
            File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(original.State));
        }
        else
        {
            Assert.True(new AcceptedModelStore(statePath).Commit(original));
        }

        using var ensemble = Create(scope);
        Assert.False(ensemble.TrainValidated(Examples(2, 281), [], false, TestContext.Current.CancellationToken));
        var configuration = ensemble.CaptureEvaluationConfiguration();
        var rows = Examples(200, 282);
        foreach (var row in rows) row.Features.CombinedCriticScore = row.Label;
        var evidence = EnsembleAdmissionEvaluator.EvaluateWithdrawal(rows, ensemble.HeuristicStrategy, ensemble, DateTime.UtcNow)!;
        Assert.NotNull(evidence);
        typeof(EnsembleScoringStrategy).GetField("_lastAdmission", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(ensemble, AdmissionDecision.Unknown("MeasuredBeforeConfigurationChange", 100, 100));
        if (changed == "minimum") ensemble.Reconfigure(.1, .75, 1);
        else if (changed == "maximum") ensemble.Reconfigure(0, .2, 1);
        else ensemble.Reconfigure(.3, .75, .2);
        var before = rows.Select(row => ensemble.Score(row.Features)).ToArray();
        var commit = typeof(EnsembleScoringStrategy).GetMethod("CommitWithdrawal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var incumbent = typeof(EnsembleScoringStrategy).GetField("_acceptedPrediction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(ensemble)!;
        commit.Invoke(ensemble, [incumbent, evidence, rows.Select(row => row.ObservationId).ToArray(), configuration, TestContext.Current.CancellationToken]);
        Assert.Equal(before, rows.Select(row => ensemble.Score(row.Features)));
        Assert.Equal("EvaluationConfigurationChanged", ensemble.LastAdmission!.Reason);
        if (!legacyModel) Assert.Equal(1, new AcceptedModelStore(statePath).Read()!.Revision);
    }

    [Fact]
    public void ChangedConfigurationCannotPromoteAChallengerOrSpendItsAcceptanceBlock()
    {
        using var ensemble = Create("stale-promotion");
        var fit = Examples(600, 311);
        var holdout = Examples(200, 312);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        ensemble.SetSharedPredictionProvider(() => { ensemble.Reconfigure(.1, .75, 1); return null; });
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("EvaluationConfigurationChanged", ensemble.LastAdmission!.Reason);
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().ModelRevision);
        Assert.Empty(ensemble.GetAcceptedObservationIds()!);
        ensemble.SetSharedPredictionProvider(() => null);
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.True(ensemble.LastAdmission!.Accepted);
    }

    [Fact]
    public void InformativeRejectedRefreshSpendsItsAcceptanceAndCannotTryAnotherGenerationAgainstIt()
    {
        var statePath = Path.Join(_directory, "rejected-evidence-state.json");
        var store = new AcceptedModelStore(statePath);
        Assert.True(store.Commit(LinearBundle(false, 1, DateTime.UtcNow)));
        using var ensemble = Create("rejected-evidence");
        var fit = Examples(600, 411);
        var holdout = Examples(200, 412);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        var before = File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!);
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("RejectedIndependentUtility", ensemble.LastAdmission!.Reason);
        Assert.NotNull(ensemble.LastAdmission.Utility.CandidateLoss);
        var used = ensemble.GetAcceptedObservationIds()!;
        Assert.True(used.Intersect(holdout.Select(row => row.ObservationId)).Any());
        Assert.Equal(before, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
        using var restarted = Create("rejected-evidence");
        Assert.False(restarted.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Null(restarted.LastAdmission!.Utility.CandidateLoss);
        Assert.Equal(before, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
    }

    [Fact]
    public void HomogeneousUnknownAssessmentDoesNotSpendUnusedAcceptance()
    {
        using var ensemble = Create("unknown-evidence-reservation");
        var fit = Examples(200, 421);
        var holdout = Examples(100, 422);
        foreach (var row in holdout) row.Label = .85;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("ProvisionalMaturityMix", ensemble.LastAdmission!.Reason);
        Assert.Empty(ensemble.GetAcceptedObservationIds()!);
    }

    [Fact]
    public void IndependentlyRejectedInitialCandidateCannotEnterProvisionallyByReplayingItsSpentAcceptance()
    {
        using var ensemble = Create("rejected-provisional-replay");
        var fit = Examples(200, 471);
        var holdout = Examples(400, 472);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        foreach (var row in holdout.Where(row => (SHA256.HashData(row.ObservationId.ToByteArray())[0] & 1) != 0)) row.Label = .85 - row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("RejectedIndependentUtility", ensemble.LastAdmission!.Reason);
        Assert.NotNull(ensemble.LastAdmission.Utility.CandidateLoss);
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.False(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        var assessmentPath = Path.Join(_directory, "rejected-provisional-replay-state.json.assessment.json");
        var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(assessmentPath))!.AsObject();
        Assert.NotEmpty(journal["ReservedOperatingInputDigests"]!.AsArray());
        // Simulate interruption before a decision was recorded: the durable reservation still prevents entry.
        journal["Decision"] = null;
        File.WriteAllText(assessmentPath, journal.ToJsonString());
        using var restarted = Create("rejected-provisional-replay");
        Assert.False(restarted.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.False(File.Exists(AcceptedModelStore.GetBundlePath(Path.Join(_directory, "rejected-provisional-replay-state.json"))));
    }

    [Fact]
    public void ReservationJournalFailureDoesNotInspectUtilityOrPermitProvisionalEntryAfterRestart()
    {
        using var ensemble = Create("reservation-journal-failure");
        var fit = Examples(200, 491);
        var holdout = Examples(400, 492);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        var assessmentPath = Path.Join(_directory, "reservation-journal-failure-state.json.assessment.json");
        ensemble.SetSharedPredictionProvider(() =>
        {
            File.Delete(assessmentPath);
            Directory.CreateDirectory(assessmentPath);
            return null;
        });
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("CheckpointCommitFailed", ensemble.LastAdmission!.Reason);
        Assert.Null(ensemble.LastAdmission.Utility.CandidateLoss);
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.NotEmpty(ensemble.GetAcceptedObservationIds()!);
        using var restarted = Create("reservation-journal-failure");
        Assert.True(restarted.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.False(File.Exists(AcceptedModelStore.GetBundlePath(Path.Join(_directory, "reservation-journal-failure-state.json"))));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("missing-receipts")]
    [InlineData("missing-recovery-flag")]
    public void LostProvisionalJournalRetainsTheIncumbentButCannotForgetRejectedReplacementReceipts(string damage)
    {
        var scope = "provisional-journal-" + damage;
        var statePath = Path.Join(_directory, scope + "-state.json");
        var fitA = Examples(200, 531);
        var holdoutA = Examples(100, 532);
        foreach (var row in fitA.Concat(holdoutA)) row.Label = .85;
        var fitB = Examples(200, 533);
        var holdoutB = Examples(400, 534);
        foreach (var row in fitB.Concat(holdoutB)) row.Features.CombinedCriticScore = row.Label;
        foreach (var row in holdoutB.Where(row => (SHA256.HashData(row.ObservationId.ToByteArray())[0] & 1) != 0))
        {
            row.Label = row.Features.CombinedCriticScore > 0 && row.ObservationId.ToByteArray()[1] % 5 == 0 ? 0 : .85;
        }

        byte[] incumbentBytes;
        double[] incumbentScores;
        using (var initial = Create(scope))
        {
            Assert.True(initial.TrainValidated(fitA, holdoutA, false, TestContext.Current.CancellationToken));
            Assert.Equal("Provisional", initial.GetDiagnosticsSnapshot().ValidationLevel);
            incumbentBytes = File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!);
            incumbentScores = holdoutB.Select(row => initial.Score(row.Features)).ToArray();
            Assert.True(initial.TrainValidated(fitB, holdoutB, false, TestContext.Current.CancellationToken));
            Assert.Equal("RejectedIndependentUtility", initial.LastAdmission!.Reason);
            Assert.NotNull(initial.LastAdmission.Utility.CandidateLoss);
            Assert.Equal(incumbentBytes, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
            Assert.False(new AcceptedModelStore(statePath).Read()!.Retired);
            Assert.NotEmpty(initial.GetAcceptedObservationIds()!);
        }

        var assessmentPath = statePath + ".assessment.json";
        if (damage == "missing")
        {
            File.Delete(assessmentPath);
        }
        else if (damage == "corrupt")
        {
            File.WriteAllText(assessmentPath, "{ damaged journal");
        }
        else
        {
            var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(assessmentPath))!.AsObject();
            journal.Remove(damage == "missing-receipts" ? "ReservedOperatingInputDigests" : "IndependentRecoveryRequired");
            File.WriteAllText(assessmentPath, journal.ToJsonString());
        }
        using (var restarted = Create(scope))
        {
            Assert.Equal(incumbentScores, holdoutB.Select(row => restarted.Score(row.Features)).ToArray());
            Assert.True(restarted.TrainValidated(fitB, holdoutB, false, TestContext.Current.CancellationToken));
            Assert.Equal(incumbentBytes, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
            Assert.Equal(incumbentScores, holdoutB.Select(row => restarted.Score(row.Features)).ToArray());
            Assert.False(restarted.IsQualityGateFrozen);
        }

        using var restartedAgain = Create(scope);
        Assert.True(restartedAgain.TrainValidated(fitB, holdoutB, false, TestContext.Current.CancellationToken));
        Assert.Equal(incumbentBytes, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
        var recoveryFit = Examples(200, 535);
        var recoveryHoldout = Examples(400, 536);
        foreach (var row in recoveryFit.Concat(recoveryHoldout)) row.Features.CombinedCriticScore = row.Label;
        Assert.True(restartedAgain.TrainValidated(recoveryFit, recoveryHoldout, false, TestContext.Current.CancellationToken));
        Assert.True(restartedAgain.LastAdmission!.Accepted);
        Assert.Equal("Verified", restartedAgain.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.NotNull(new AcceptedModelStore(statePath).Read()!.Utility);
    }

    [Fact]
    public void ProvisionalMixtureRebalancesOnGrowingPersonalEvidenceWithoutClaimingVerifiedUtility()
    {
        using var ensemble = Create("provisional-growth");
        var holdout = Examples(100, 431);
        var firstFit = Examples(40, 432);
        foreach (var row in firstFit.Concat(holdout)) row.Label = .85;
        Assert.True(ensemble.TrainValidated(firstFit, holdout, false, TestContext.Current.CancellationToken));
        var first = ensemble.GetDiagnosticsSnapshot();
        var laterFit = firstFit.Concat(Examples(160, 433)).ToArray();
        foreach (var row in laterFit) row.Label = .85;
        Assert.True(ensemble.TrainValidated(laterFit, holdout, false, TestContext.Current.CancellationToken));
        var later = ensemble.GetDiagnosticsSnapshot();
        Assert.True(later.ModelRevision > first.ModelRevision);
        Assert.True(later.EffectiveAlpha > first.EffectiveAlpha);
        Assert.Equal("Provisional", later.ValidationLevel);
        Assert.Null(ensemble.LastAdmission!.Utility.CandidateLoss);
        Assert.Empty(ensemble.GetAcceptedObservationIds()!);
        Assert.Empty(new AcceptedModelStore(Path.Join(_directory, "provisional-growth-state.json")).ReadHistory());
    }

    [Fact]
    public void ProvisionalReplayIsBitIdenticalIncludingRestartAndInputOrderChanges()
    {
        using var ensemble = Create("provisional-replay");
        var fit = Examples(100, 441);
        var holdout = Examples(80, 442);
        foreach (var row in fit.Concat(holdout)) row.Label = .85;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        var path = AcceptedModelStore.GetBundlePath(Path.Join(_directory, "provisional-replay-state.json"))!;
        var before = File.ReadAllBytes(path);
        Assert.NotEmpty(new AcceptedModelStore(Path.Join(_directory, "provisional-replay-state.json")).Read()!.OperatingInputDigest!);
        var scores = holdout.Select(row => ensemble.Score(row.Features)).ToArray();
        var generation = ensemble.LearnedStrategy.CapturePredictionSnapshot().ExportParameters().TrainingGeneration;
        var metrics = ensemble.MetricsHistoryCount;
        Assert.False(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("UnchangedOperatingInputs", ensemble.LastAdmission!.Reason);
        Assert.Equal(generation, ensemble.LearnedStrategy.CapturePredictionSnapshot().ExportParameters().TrainingGeneration);
        Assert.Equal(metrics, ensemble.MetricsHistoryCount);
        Assert.Equal(before, File.ReadAllBytes(path));
        using var restarted = Create("provisional-replay");
        Assert.False(restarted.TrainValidated(fit.Reverse().ToArray(), holdout.Reverse().ToArray(), false, TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(scores, holdout.Select(row => restarted.Score(row.Features)).ToArray());
    }

    [Fact]
    public void FreshMixedAcceptancePreventsReplaySkippingEvenWhenFittingAndCalibrationAreUnchanged()
    {
        using var ensemble = Create("fresh-acceptance-replay");
        var fit = Examples(300, 571);
        var holdout = Examples(100, 572);
        foreach (var row in fit.Concat(holdout)) row.Label = .85;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.False(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        var acceptance = holdout.Where(row => (SHA256.HashData(row.ObservationId.ToByteArray())[0] & 1) != 0).ToArray();
        foreach (var row in acceptance) row.Label = row.ObservationId.ToByteArray()[1] % 2 == 0 ? 0 : .85;
        Assert.True(EnsembleAdmissionEvaluator.HasIndependentAcceptanceSupport(acceptance, DateTime.UtcNow));
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.All(acceptance, row => Assert.Contains(row.ObservationId, ensemble.GetAcceptedObservationIds()!));
    }

    [Fact]
    public void PersonalPinnedNeuralReplaySkipsFittingWithoutChangingItsOwnCoefficients()
    {
        using var global = Create("personal-replay-global");
        Assert.True(global.TrainValidated(Examples(600, 581), Examples(400, 582), true, TestContext.Current.CancellationToken));
        using var personal = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), global.NeuralStrategy, ownsNeural: false);
        personal.SetPinnedNeuralTrainingPrior(global.GetAcceptedNeuralPrediction());
        var fit = Examples(300, 583);
        var holdout = Examples(100, 584);
        foreach (var row in fit.Concat(holdout)) row.Label = .85;
        Assert.True(personal.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        var before = personal.GetDiagnosticsSnapshot();
        var scores = holdout.Select(row => personal.Score(row.Features)).ToArray();
        Assert.False(personal.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal(before.EffectiveAlpha, personal.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.Equal(before.EffectiveNeuralBeta, personal.GetDiagnosticsSnapshot().EffectiveNeuralBeta);
        Assert.Equal(scores, holdout.Select(row => personal.Score(row.Features)).ToArray());
    }

    [Fact]
    [Trait("Category", "Profiling")]
    public void ProfileUnchangedNeuralReplaysAgainstTheOrdinaryFittingPath()
    {
        var fit = Examples(300, 591);
        var holdout = Examples(100, 592);
        foreach (var row in fit.Concat(holdout)) row.Label = .85;
        foreach (var ordinaryPath in new[] { true, false })
        {
            using var ensemble = Create(ordinaryPath ? "profile-ordinary" : "profile-skip");
            Assert.True(ensemble.TrainValidated(fit, holdout, true, TestContext.Current.CancellationToken));
            if (ordinaryPath) ensemble.SetSharedPredictionProvider(() => null); // Dynamic preparation uses the ordinary path.
            var before = holdout.Select(row => ensemble.Score(row.Features)).ToArray();
            var allocation = GC.GetAllocatedBytesForCurrentThread();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (var replay = 0; replay < 3; replay++)
            {
                Assert.Equal(ordinaryPath, ensemble.TrainValidated(fit, holdout, true, TestContext.Current.CancellationToken));
            }

            timer.Stop();
            _output.WriteLine($"Replay profile ({(ordinaryPath ? "ordinary fit" : "early skip")}): {timer.Elapsed.TotalMilliseconds:F2} ms, {GC.GetAllocatedBytesForCurrentThread() - allocation:N0} allocated bytes, three replays.");
            Assert.Equal(before, holdout.Select(row => ensemble.Score(row.Features)).ToArray());
        }
    }

    [Fact]
    public void WorseProvisionalFitRetainsGoodCoordinatesAndHonestTrainingTime()
    {
        using var ensemble = Create("provisional-retention");
        var fit = Examples(100, 451);
        var holdout = Examples(80, 452);
        foreach (var row in fit.Concat(holdout)) row.Label = .85;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        var store = new AcceptedModelStore(Path.Join(_directory, "provisional-retention-state.json"));
        var first = store.Read()!;
        var badFit = Examples(100, 453);
        foreach (var row in badFit) row.Label = .05;
        Assert.True(ensemble.TrainValidated(badFit, holdout, false, TestContext.Current.CancellationToken));
        var retained = store.Read()!;
        Assert.True(retained.Revision > first.Revision);
        Assert.NotEqual(first.OperatingInputDigest, retained.OperatingInputDigest);
        Assert.Equal(first.Learned.Weights, retained.Learned.Weights);
        Assert.Equal(first.Learned.Bias, retained.Learned.Bias);
        Assert.Equal(first.Learned.FeatureMeans, retained.Learned.FeatureMeans);
        Assert.Equal(first.Learned.ImputationMeans, retained.Learned.ImputationMeans);
        Assert.Equal(first.Learned.TrainingGeneration, retained.Learned.TrainingGeneration);
        Assert.Equal(first.TrainedAtUtc, retained.TrainedAtUtc);
        Assert.True(retained.Mixture.Alpha <= first.Mixture.Alpha);
        Assert.False(retained.Retired);
        Assert.Null(retained.Utility);
    }

    [Fact]
    public void InsufficientNeuralRefreshRetainsThePreviouslyTrainedPredictorInsteadOfPublishingRandomWeights()
    {
        using var ensemble = Create("provisional-neural-failure");
        var fit = Examples(300, 461);
        var holdout = Examples(100, 462);
        foreach (var row in fit.Concat(holdout)) row.Label = .85;
        Assert.True(ensemble.TrainValidated(fit, holdout, true, TestContext.Current.CancellationToken));
        Assert.True(ensemble.GetDiagnosticsSnapshot().NeuralShare > 0);
        var before = ensemble.GetAcceptedNeuralPrediction()!.ExportParameters();
        var sparseFit = Examples(12, 463);
        foreach (var row in sparseFit) row.Label = .85;
        Assert.True(ensemble.TrainValidated(sparseFit, holdout, true, TestContext.Current.CancellationToken));
        var after = ensemble.GetAcceptedNeuralPrediction()!.ExportParameters();
        Assert.Equal(before.WeightsIH, after.WeightsIH);
        Assert.Equal(before.WeightsH4O, after.WeightsH4O);
        Assert.Equal(before.TrainingGeneration, after.TrainingGeneration);
        Assert.True(ensemble.GetDiagnosticsSnapshot().NeuralShare > 0);
    }

    [Fact]
    public void ProvisionalReconfigurationRetainsGoodParametersWithinTheNewMaturityAndConfigurationCeilings()
    {
        using var ensemble = Create("provisional-reconfigure");
        var fit = Examples(100, 481);
        var holdout = Examples(80, 482);
        foreach (var row in fit.Concat(holdout)) row.Label = .85;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        ensemble.Reconfigure(.2, .4, 1);
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.NotEqual("CheckpointCommitFailed", ensemble.LastAdmission!.Reason);
        var stored = new AcceptedModelStore(Path.Join(_directory, "provisional-reconfigure-state.json")).Read()!;
        Assert.InRange(stored.Mixture.Alpha, .2, stored.AlphaCap);
        Assert.InRange(stored.AlphaCap, .2, .4);
        using var restarted = Create("provisional-reconfigure");
        restarted.Reconfigure(.2, .4, 1);
        Assert.Equal(ensemble.GetDiagnosticsSnapshot().EffectiveAlpha, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
    }

    [Fact]
    public void ChangedConfigurationCannotRestoreHistoryAndDoesNotSpendFreshEvidence()
    {
        var statePath = Path.Join(_directory, "stale-history-state.json");
        var store = new AcceptedModelStore(statePath);
        Assert.True(store.Commit(LinearBundle(false, 1, DateTime.UtcNow.AddHours(-2))));
        Assert.True(store.Commit(LinearBundle(true, 2, DateTime.UtcNow.AddHours(-1))));
        var before = File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!);
        using var ensemble = Create("stale-history");
        var fit = Examples(600, 321);
        var holdout = Examples(200, 322);
        foreach (var row in fit)
        {
            row.Features.CombinedCriticScore = row.Label;
            row.Label = .85 - row.Label;
        }

        foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
        ensemble.SetSharedPredictionProvider(() => { ensemble.Reconfigure(.1, .75, 1); return null; });
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("EvaluationConfigurationChanged", ensemble.LastAdmission!.Reason);
        Assert.Equal(before, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
        ensemble.SetSharedPredictionProvider(() => null);
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("HistoricalModelRestoredOnNewEvidence", ensemble.LastAdmission!.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedWithdrawalAndHistoryStorageFailuresKeepTheCurrentPrediction(bool historical)
    {
        var scope = historical ? "history-write-failure" : "withdraw-write-failure";
        var statePath = Path.Join(_directory, scope + "-state.json");
        var store = new AcceptedModelStore(statePath);
        if (historical) Assert.True(store.Commit(LinearBundle(false, 1, DateTime.UtcNow.AddHours(-2))));
        var revision = historical ? 2 : 1;
        Assert.True(store.Commit(LinearBundle(true, revision, DateTime.UtcNow.AddHours(-1))));
        using var ensemble = Create(scope);
        var fit = Examples(600, 351);
        var holdout = Examples(200, 352);
        foreach (var row in fit)
        {
            row.Features.CombinedCriticScore = row.Label;
            row.Label = .85 - row.Label;
        }

        foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
        var before = holdout.Select(row => ensemble.Score(row.Features)).ToArray();
        ensemble.SetSharedPredictionProvider(() =>
        {
            var path = AcceptedModelStore.GetBundlePath(statePath)!;
            File.Delete(path);
            Directory.CreateDirectory(path);
            return null;
        });
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("CheckpointCommitFailed", ensemble.LastAdmission!.Reason);
        Assert.Equal(before, holdout.Select(row => ensemble.Score(row.Features)));
        Assert.Equal(revision, ensemble.GetDiagnosticsSnapshot().ModelRevision);
        Assert.False(ensemble.IsQualityGateFrozen);
    }

    [Theory]
    [InlineData("promotion")]
    [InlineData("withdrawal")]
    [InlineData("history")]
    public void DisposalDuringEvaluationNeverCommitsAPromotionWithdrawalOrRollback(string operation)
    {
        var scope = "disposed-" + operation;
        var statePath = Path.Join(_directory, scope + "-state.json");
        var store = new AcceptedModelStore(statePath);
        if (operation == "history") Assert.True(store.Commit(LinearBundle(false, 1, DateTime.UtcNow.AddHours(-2))));
        if (operation != "promotion") Assert.True(store.Commit(LinearBundle(true, 2, DateTime.UtcNow.AddHours(-1))));
        using var ensemble = Create(scope);
        var fit = Examples(600, 361);
        var holdout = Examples(200, 362);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        if (operation != "promotion") foreach (var row in fit) row.Label = .85 - row.Label;
        var before = store.Read()?.Revision ?? 0;
        ensemble.SetSharedPredictionProvider(() => { ensemble.Dispose(); return null; });
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal(before, store.Read()?.Revision ?? 0);
        Assert.False(store.Read()?.Retired ?? false);
    }

    [Fact]
    public void PersonalNeuralBorrowingPinsTheAcceptedGlobalVersionWithoutOwningItsResources()
    {
        using var global = Create("borrow-global");
        Assert.True(global.TrainValidated(Examples(600, 371), Examples(400, 372), true, TestContext.Current.CancellationToken));
        Assert.True(global.GetDiagnosticsSnapshot().NeuralShare > 0);
        var statePath = Path.Join(_directory, "borrow-personal-state.json");
        using var personal = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), global.NeuralStrategy, statePath, genrePenaltyFloor: 1, ownsNeural: false);
        personal.SetSharedPredictionProvider(global.GetAcceptedNeuralPrediction);
        Assert.True(personal.TrainValidated(Examples(600, 373), Examples(400, 374), false, TestContext.Current.CancellationToken));
        Assert.True(personal.GetDiagnosticsSnapshot().NeuralShare > 0);
        var probe = Examples(1, 375)[0].Features;
        var score = personal.Score(probe);
        using var restarted = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), global.NeuralStrategy, statePath, genrePenaltyFloor: 1, ownsNeural: false);
        Assert.Equal(score, restarted.Score(probe));
        personal.Dispose();
        Assert.NotNull(global.GetAcceptedNeuralPrediction());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedNullNeuralPriorCannotBorrowTheRefreshedInheritedIncumbent(bool replaceProviderAfterPreparation)
    {
        using var global = Create("null-prior-global");
        Assert.True(global.TrainValidated(Examples(600, 501), Examples(400, 502), true, TestContext.Current.CancellationToken));
        Assert.True(global.GetDiagnosticsSnapshot().NeuralShare > 0);
        using var personal = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), global.NeuralStrategy, ownsNeural: false);
        var pinnedCalls = 0;
        personal.SetSharedPredictionProvider(() => { pinnedCalls++; return null; });
        // A cold read happens after this cycle published its globally selected predictor.
        personal.InitializeOperatingPrior(global.GetOperatingPrior()!);
        var fit = Examples(200, 503);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var context = typeof(EnsembleScoringStrategy).GetMethod("PrepareChallengers", flags)!.Invoke(personal, [fit, null, DateTime.UtcNow])!;
        if (replaceProviderAfterPreparation) personal.SetSharedPredictionProvider(global.GetAcceptedNeuralPrediction);
        var candidates = typeof(EnsembleScoringStrategy).GetMethod("CaptureFittedCandidates", flags)!.Invoke(personal, [context, fit, Array.Empty<TrainingExample>(), TestContext.Current.CancellationToken])!;
        Assert.Null(candidates.GetType().GetProperty("Neural")!.GetValue(candidates));
        Assert.Equal(1, pinnedCalls);
    }

    [Fact]
    public void ColdInheritedMixtureStopsNeuralWhenTheGlobalCoefficientStopsEvenIfCoordinatesRemain()
    {
        using var global = Create("dormant-prior-global");
        Assert.True(global.TrainValidated(Examples(600, 511), Examples(400, 512), true, TestContext.Current.CancellationToken));
        var active = global.GetOperatingPrior()!;
        Assert.NotNull(active.Neural);
        using var cold = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), global.NeuralStrategy, ownsNeural: false);
        cold.InitializeOperatingPrior(active);
        var before = cold.GetDiagnosticsSnapshot();
        Assert.True(before.NeuralShare > 0);
        var dormant = new AcceptedPrediction(global.HeuristicStrategy, active.Learned, active.Neural, new AdmissionMixture(active.Mixture.Alpha, 0), active.NeuralCap);
        Assert.NotNull(dormant.Neural);
        cold.InitializeOperatingPrior(dormant);
        var after = cold.GetDiagnosticsSnapshot();
        Assert.Equal(0, after.NeuralShare);
        Assert.Equal(0, after.EffectiveNeuralBeta);
        Assert.Equal(before.EffectiveAlpha, after.EffectiveAlpha);
        Assert.Equal(after.EffectiveAlpha, after.LearnedShare);
        Assert.Equal("GlobalPrior", after.ValidationLevel);
        Assert.Null(cold.GetAcceptedNeuralPrediction());
        cold.InitializeOperatingPrior(active);
        Assert.Equal(before.NeuralShare, cold.GetDiagnosticsSnapshot().NeuralShare);
        Assert.Equal(before.EffectiveAlpha, cold.GetDiagnosticsSnapshot().EffectiveAlpha);
    }

    [Fact]
    public void ColdInheritedMixtureDoesNotUseDormantLearnedCoordinatesFromANeuralOnlyGlobalPrior()
    {
        using var global = Create("neural-only-prior-global");
        Assert.True(global.TrainValidated(Examples(600, 521), Examples(400, 522), true, TestContext.Current.CancellationToken));
        var active = global.GetOperatingPrior()!;
        Assert.True(active.Mixture.Neural > 0);
        using var cold = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), global.NeuralStrategy, ownsNeural: false);
        cold.InitializeOperatingPrior(active);
        var neuralOnly = new AcceptedPrediction(global.HeuristicStrategy, active.Learned, active.Neural, new AdmissionMixture(0, active.Mixture.Neural), active.NeuralCap);
        cold.InitializeOperatingPrior(neuralOnly);
        var diagnostics = cold.GetDiagnosticsSnapshot();
        Assert.Equal(0, diagnostics.LearnedShare);
        Assert.True(diagnostics.NeuralShare > 0);
        Assert.InRange(diagnostics.NeuralShare, 0, .3);
        Assert.Equal(1, diagnostics.HeuristicShare + diagnostics.NeuralShare, 12);
    }

    [Fact]
    public void UsefulProvisionalRefreshWithPinnedAbsentNeuralKeepsCoefficientsAndPredictorsPaired()
    {
        using var ensemble = Create("paired-provisional-refresh");
        var firstFit = Examples(300, 541);
        var firstHoldout = Examples(100, 542);
        foreach (var row in firstFit.Concat(firstHoldout))
        {
            row.Features.GenreSimilarity = .1;
            row.Features.CombinedCriticScore = .1;
            row.Features.RecencyScore = .1;
            row.Features.YearProximityScore = .1;
            row.Label = .2;
        }

        Assert.True(ensemble.TrainValidated(firstFit, firstHoldout, true, TestContext.Current.CancellationToken));
        var store = new AcceptedModelStore(Path.Join(_directory, "paired-provisional-refresh-state.json"));
        var before = store.Read()!;
        Assert.True(ensemble.GetDiagnosticsSnapshot().NeuralShare > 0);
        var fit = Examples(300, 543);
        var holdout = Examples(100, 544);
        foreach (var row in fit.Concat(holdout))
        {
            row.Features.GenreSimilarity = .1;
            row.Features.CombinedCriticScore = .1;
            row.Features.RecencyScore = .1;
            row.Features.YearProximityScore = .1;
            row.Label = .85;
        }

        ensemble.SetSharedPredictionProvider(() => null);
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.NotEqual("CheckpointCommitFailed", ensemble.LastAdmission!.Reason);
        var after = store.Read()!;
        Assert.True(after.Revision > before.Revision);
        Assert.True(after.Learned.Bias > before.Learned.Bias);
        Assert.Equal(0, after.Mixture.Neural);
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().NeuralShare);
        Assert.Equal(after.Mixture.Alpha, ensemble.GetDiagnosticsSnapshot().LearnedShare);
        Assert.Null(after.Utility);
    }

    [Theory]
    [InlineData("promotion")]
    [InlineData("withdrawal")]
    [InlineData("legacy-withdrawal")]
    [InlineData("history")]
    public void PublicationSpendsOnlyTheAcceptanceIdentitiesActuallyExamined(string operation)
    {
        var scope = "examined-identities-" + operation;
        var statePath = Path.Join(_directory, scope + "-state.json");
        var store = new AcceptedModelStore(statePath);
        if (operation == "history") Assert.True(store.Commit(LinearBundle(false, 1, DateTime.UtcNow.AddHours(-2))));
        if (operation is "withdrawal" or "history") Assert.True(store.Commit(LinearBundle(true, 2, DateTime.UtcNow.AddHours(-1))));
        if (operation == "legacy-withdrawal")
        {
            var legacy = LinearBundle(true, 1, DateTime.UtcNow);
            File.WriteAllText(Path.Join(_directory, scope + "-ml.json"), System.Text.Json.JsonSerializer.Serialize(legacy.Learned));
            File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(legacy.State));
        }

        var mixedUser = new Guid(1, 0, 0, new byte[8]);
        var excludedUser = new Guid(2, 0, 0, new byte[8]);
        var fit = Examples(600, 551);
        var holdout = Examples(400, 552);
        foreach (var row in fit.Concat(holdout))
        {
            row.UserId = mixedUser;
            row.Features.CombinedCriticScore = row.Label;
        }

        if (operation != "promotion") foreach (var row in fit) row.Label = .85 - row.Label;
        var excluded = Examples(100, 553);
        foreach (var row in excluded)
        {
            row.UserId = excludedUser;
            row.Label = .85;
        }

        using var ensemble = Create(scope);
        Assert.True(ensemble.TrainValidated(fit, holdout.Concat(excluded).ToArray(), false, TestContext.Current.CancellationToken));
        var expectedReason = operation switch
        {
            "promotion" => "Accepted",
            "history" => "HistoricalModelRestoredOnNewEvidence",
            "legacy-withdrawal" => "LegacyModelWithdrawnOnNewEvidence",
            _ => "IncumbentWithdrawnOnNewEvidence"
        };
        Assert.Equal(expectedReason, ensemble.LastAdmission!.Reason);
        var examined = holdout.Where(row => (SHA256.HashData(row.ObservationId.ToByteArray())[0] & 1) != 0).Select(row => row.ObservationId).ToArray();
        if (operation == "legacy-withdrawal")
        {
            var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(statePath + ".assessment.json"))!.AsObject();
            var ids = journal["LegacyWithdrawalObservationIds"]!.AsArray().Select(node => Guid.Parse(node!.GetValue<string>())).ToArray();
            Assert.Equal(examined.Order(), ids.Order());
        }
        else
        {
            Assert.Equal(examined.Order(), store.Read()!.AcceptanceObservationIds.Order());
        }

        var spent = ensemble.GetAcceptedObservationIds()!;
        Assert.All(examined, id => Assert.Contains(id, spent));
        Assert.Empty(excluded.Select(row => row.ObservationId).Intersect(spent));
    }

    [Fact]
    public async Task SlowLedgerReadDoesNotBlockInference()
    {
        using var ensemble = Create("slow-ledger-read");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reading = Task.Run(() => ensemble.GetAcceptedObservationIds((_, _) =>
        {
            entered.Set();
            release.Wait(TestContext.Current.CancellationToken);
            return [];
        }), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var scoring = Task.Run(() => ensemble.Score(new CandidateFeatures { GenreSimilarity = .8 }), TestContext.Current.CancellationToken);
            Assert.InRange(await scoring.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), 0, 1);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
            await reading;
        }
    }

    [Fact]
    public void LedgerReadRetriesRatherThanApplyingResultsFromBeforeAnInterveningPromotion()
    {
        using var ensemble = Create("ledger-read-revision");
        var fit = Examples(200, 561);
        var holdout = Examples(400, 562);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        var staleId = new Guid(999, 0, 0, new byte[8]);
        var calls = 0;
        var ids = ensemble.GetAcceptedObservationIds((bundle, required) =>
        {
            if (++calls == 1)
            {
                Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
                Assert.True(ensemble.LastAdmission!.Accepted);
                return [staleId];
            }

            return new AcceptedModelStore(Path.Join(_directory, "ledger-read-revision-state.json")).ReadSpentAcceptanceIds(bundle, required);
        });
        Assert.Equal(2, calls);
        Assert.NotNull(ids);
        Assert.DoesNotContain(staleId, ids);
        Assert.NotEmpty(ids);
    }

    [Fact]
    public void RestoredNeuralSnapshotRemainsVisibleInDiagnosticsWithoutALiveNeuralTrainer()
    {
        var statePath = Path.Join(_directory, "snapshot-neural-state.json");
        using (var trained = Create("snapshot-neural"))
        {
            Assert.True(trained.TrainValidated(Examples(600, 401), Examples(400, 402), true, TestContext.Current.CancellationToken));
            Assert.True(trained.GetDiagnosticsSnapshot().NeuralShare > 0);
        }

        using var restored = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(genrePenaltyFloor: 1), neural: null, statePath: statePath, genrePenaltyFloor: 1);
        var diagnostics = restored.GetDiagnosticsSnapshot();
        Assert.True(diagnostics.NeuralEnabled);
        Assert.True(diagnostics.NeuralShare > 0);
        Assert.Equal(1, diagnostics.HeuristicShare + diagnostics.LearnedShare + diagnostics.NeuralShare, 12);
    }

    [Theory]
    [InlineData("empty-object")]
    [InlineData("Version")]
    [InlineData("ObservedTrainingIds")]
    [InlineData("IndependentAdmissionEnabled")]
    [InlineData("HeuristicFallbackRequired")]
    [InlineData("AcceptanceLedgerRequired")]
    [InlineData("AcceptedModelRevision")]
    [InlineData("LegacyModelSuppressed")]
    public void OmittedSafetyMetadataCannotRestoreLegacyInfluence(string omitted)
    {
        var scope = "missing-safety-" + omitted;
        var statePath = Path.Join(_directory, scope + "-state.json");
        var legacy = LinearBundle(true, 1, DateTime.UtcNow);
        File.WriteAllText(Path.Join(_directory, scope + "-ml.json"), System.Text.Json.JsonSerializer.Serialize(legacy.Learned));
        File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(legacy.State));
        var metadata = new ModelTrainingAssessment { IndependentAdmissionEnabled = true, HeuristicFallbackRequired = true };
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(metadata))!.AsObject();
        if (omitted == "empty-object") node.Clear();
        else node.Remove(omitted);
        File.WriteAllText(statePath + ".assessment.json", node.ToJsonString());
        using var restarted = Create(scope);
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.Equal("UnreadableAdmissionAssessment", restarted.LastAdmission!.Reason);
    }

    [Fact]
    public void DisposedAndConcurrentTrainingCannotStartASecondFitOrPublishAnotherModel()
    {
        using var ensemble = Create("concurrent-fit");
        var fit = Examples(200, 331);
        var holdout = Examples(100, 332);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        var refused = false;
        ensemble.SetSharedPredictionProvider(() =>
        {
            refused = !ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken);
            return null;
        });
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.True(refused);
        Assert.Equal(1, ensemble.GetDiagnosticsSnapshot().ModelRevision);
        ensemble.Dispose();
        Assert.False(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void IndependentModeIgnoresLegacyOffsetsAndCohortFeedbackAndRedirectsPublicTraining()
    {
        using var ensemble = Create("compatibility-entry");
        var fit = Examples(200, 341);
        var holdout = Examples(100, 342);
        foreach (var row in fit.Concat(holdout)) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        var probe = fit[0].Features;
        var score = ensemble.Score(probe);
        Assert.Equal(score, ensemble.ScoreWithOffset(probe, .4));
        Assert.Equal(score, ensemble.ScoreWithOffset(probe, -.4));
        var midpoint = ensemble.SigmoidMidpointOffset;
        ensemble.ApplyCohortFeedback([], []);
        Assert.Equal(midpoint, ensemble.SigmoidMidpointOffset);
        Assert.False(ensemble.Train(fit, holdout, trainNeural: false));
        Assert.False(ensemble.LastAdmission!.Accepted);
        Assert.Equal(1, ensemble.GetDiagnosticsSnapshot().ModelRevision);
        Assert.Equal(score, ensemble.Score(probe));
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("identities")]
    public void UnreadableAssessmentCannotGrantLegacyInfluence(string damage)
    {
        var scope = "bad-assessment-" + damage;
        var statePath = Path.Join(_directory, scope + "-state.json");
        var parameters = LinearBundle(true, 1, DateTime.UtcNow).Learned;
        File.WriteAllText(Path.Join(_directory, scope + "-ml.json"), System.Text.Json.JsonSerializer.Serialize(parameters));
        var path = statePath + ".assessment.json";
        var assessment = new ModelTrainingAssessment { Version = damage == "version" ? 99 : 1 };
        if (damage == "identities") assessment.ObservedTrainingIds = null!;
        File.WriteAllText(path, damage == "null" ? "null" : System.Text.Json.JsonSerializer.Serialize(assessment));
        if (damage == "oversized")
        {
            using var file = File.OpenWrite(path);
            file.SetLength(1024 * 1024 + 1);
        }

        using var ensemble = Create(scope);
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.Equal("UnreadableAdmissionAssessment", ensemble.LastAdmission!.Reason);
    }

    [Fact]
    public void OlderAssessmentCannotSuppressANewerAcceptedCheckpoint()
    {
        var statePath = Path.Join(_directory, "older-journal-state.json");
        Assert.True(new AcceptedModelStore(statePath).Commit(LinearBundle(false, 2, DateTime.UtcNow)));
        var assessment = new ModelTrainingAssessment { AcceptedModelRevision = 1, HeuristicFallbackRequired = true, LegacyModelSuppressed = true };
        File.WriteAllText(statePath + ".assessment.json", System.Text.Json.JsonSerializer.Serialize(assessment));
        using var ensemble = Create("older-journal");
        Assert.Equal(2, ensemble.GetDiagnosticsSnapshot().ModelRevision);
        Assert.Equal(.75, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.False(ensemble.IsQualityGateFrozen);
    }

    [Fact]
    public void ConfigurationIdentityUsesExactCopiedValuesRatherThanAnEpsilon()
    {
        using var ensemble = Create("exact-configuration");
        var original = ensemble.CaptureEvaluationConfiguration();
        ensemble.Reconfigure(.3, .75, 1);
        Assert.Equal(original.Revision, ensemble.CaptureEvaluationConfiguration().Revision);
        ensemble.Reconfigure(.3, Math.BitIncrement(.75), 1);
        Assert.True(ensemble.CaptureEvaluationConfiguration().Revision > original.Revision);
        Assert.False(ScoringHelper.SameConfigurationValue(.75, Math.BitIncrement(.75)));
    }

    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, true)]
    [InlineData(double.NegativeInfinity, false)]
    public void HistoricalCalibrationRejectsNaNReferencesButAllowsTheUnknownChallengerSentinel(double referenceLoss, bool selected)
    {
        using var ensemble = Create("calibration-reference");
        var original = LinearBundle(false, 1, DateTime.UtcNow);
        var bad = LinearBundle(true, 2, DateTime.UtcNow);
        var heuristic = ensemble.HeuristicStrategy;
        var learned = new LearnedScoringStrategy.PredictionSnapshot(bad.Learned);
        var incumbent = new AcceptedPrediction(heuristic, learned, null, bad.Mixture, bad.NeuralCap);
        var calibration = Examples(200, 381);
        foreach (var row in calibration) row.Features.CombinedCriticScore = row.Label;
        var flags = System.Reflection.BindingFlags.NonPublic;
        var contextType = typeof(EnsembleScoringStrategy).GetNestedType("RestorationContext", flags)!;
        var context = Activator.CreateInstance(contextType, [calibration, Array.Empty<TrainingExample>(), incumbent, 1.0, DateTime.UtcNow, referenceLoss, learned, ensemble.CaptureEvaluationConfiguration()])!;
        var heuristicType = typeof(EnsembleScoringStrategy).GetNestedType("PenalizedPredictor", flags)!;
        var penalized = Activator.CreateInstance(heuristicType, [heuristic, 1.0])!;
        var incumbentType = typeof(EnsembleScoringStrategy).GetNestedType("PinnedMixturePredictor", flags)!;
        var current = Activator.CreateInstance(incumbentType, [incumbent, .75, 1.0])!;
        var method = typeof(EnsembleScoringStrategy).GetMethod("CalibrateHistoricalCandidate", flags | System.Reflection.BindingFlags.Instance)!;
        var candidate = method.Invoke(ensemble, [original, context, penalized, current]);
        Assert.Equal(selected, candidate is not null);
    }

    [Fact]
    public void ValidationAdaptersExposeConsistentMetadataAndPinnedMarginalExplanations()
    {
        var source = new LearnedScoringStrategy().CapturePredictionSnapshot();
        var feature = new CandidateFeatures { GenreSimilarity = .1, CombinedCriticScore = .8, RecencyScore = .7 };
        var type = typeof(EnsembleScoringStrategy).GetNestedType("PenalizedPredictor", System.Reflection.BindingFlags.NonPublic)!;
        var adapter = (IScoringStrategy)Activator.CreateInstance(type, [source, .2])!;
        Assert.Equal(source.Name, adapter.Name);
        Assert.Equal(source.NameKey, adapter.NameKey);
        Assert.Equal(adapter.Score(feature), adapter.ScoreWithExplanation(feature).FinalScore);
        var prediction = new AcceptedPrediction(new HeuristicScoringStrategy(genrePenaltyFloor: 1), source, null, new AdmissionMixture(.5, 0), 0);
        type = typeof(EnsembleScoringStrategy).GetNestedType("PinnedMixturePredictor", System.Reflection.BindingFlags.NonPublic)!;
        adapter = (IScoringStrategy)Activator.CreateInstance(type, [prediction, .75, .2])!;
        Assert.Equal("Pinned incumbent", adapter.Name);
        Assert.Equal("strategyEnsemble", adapter.NameKey);
        Assert.Equal(prediction.Score(feature, .75, .2), adapter.Score(feature));
        Assert.Equal(adapter.Score(feature), adapter.ScoreWithExplanation(feature).FinalScore);
    }

    [Fact]
    public void CheckpointFailureDoesNotPublishTheApprovedCandidate()
    {
        using var ensemble = Create("write-failure");
        var probes = Examples(24, 43).Select(row => row.Features).ToArray();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1);
        Directory.CreateDirectory(AcceptedModelStore.GetBundlePath(Path.Join(_directory, "write-failure-state.json"))!);
        Assert.True(ensemble.TrainValidated(Examples(600, 41), Examples(400, 42), true, TestContext.Current.CancellationToken));
        Assert.Equal("CheckpointCommitFailed", ensemble.LastAdmission!.Reason);
        Assert.Equal(probes.Select(heuristic.Score).ToArray(), probes.Select(ensemble.Score).ToArray());
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
    }

    [Fact]
    public void CallerCancellationAndOverlappingTitlesCannotChangeTheActiveVersion()
    {
        using var ensemble = Create("cancel");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var rows = Examples(80, 51);
        Assert.Throws<OperationCanceledException>(() => ensemble.TrainValidated(rows, [], true, cancellation.Token));
        Assert.Throws<ArgumentException>(() => ensemble.TrainValidated(rows, rows, true, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(AcceptedModelStore.GetBundlePath(Path.Join(_directory, "cancel-state.json"))!));
    }

    [Fact]
    public void AConfigurationCeilingReductionNeverRaisesATinyOrNeuralOnlyMix()
    {
        var learned = new LearnedScoringStrategy();
        using var neural = new NeuralScoringStrategy();
        var h = new HeuristicScoringStrategy(genrePenaltyFloor: 1);
        var tiny = new AcceptedPrediction(h, learned.CapturePredictionSnapshot(), neural.CapturePredictionSnapshot(), new AdmissionMixture(1e-9, 1e-9), .3);
        Assert.Equal(2e-9, tiny.Resolve(.75).Alpha);
        var neuralOnly = new AcceptedPrediction(h, learned.CapturePredictionSnapshot(), neural.CapturePredictionSnapshot(), new AdmissionMixture(0, .3), .3);
        Assert.Equal(.08, neuralOnly.Resolve(.2).Neural, 12);
        Assert.Equal(0, neuralOnly.Resolve(0).Alpha);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(1000)]
    public void HistoricalModelIsRestoredOnlyAfterFreshIndependentEvidenceAndKeepsOldTrainingDate(int spentTitles)
    {
        var statePath = Path.Join(_directory, "history-state.json");
        var store = new AcceptedModelStore(statePath);
        var older = LinearBundle(inverted: false, revision: 1, DateTime.UtcNow.AddHours(-2));
        var newer = LinearBundle(inverted: true, revision: 2, DateTime.UtcNow.AddHours(-1));
        newer.AcceptanceObservationIds = Enumerable.Range(1, spentTitles).Select(value => new Guid(value, 12, 0, new byte[8])).ToArray();
        Assert.True(store.Commit(older));
        Assert.True(store.Commit(newer));
        using var ensemble = Create("history");
        var fit = Examples(600, 71);
        var holdout = Examples(400, 72);
        foreach (var row in fit)
        {
            row.Features.CombinedCriticScore = row.Label;
            row.Label = .85 - row.Label;
        }
        foreach (var row in holdout) row.Features.CombinedCriticScore = row.Label;
        Assert.True(ensemble.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
        Assert.Equal("HistoricalModelRestoredOnNewEvidence", ensemble.LastAdmission!.Reason);
        var restored = store.Read()!;
        Assert.Equal(3, restored.Revision);
        Assert.Equal(older.TrainedAtUtc, restored.TrainedAtUtc);
        Assert.Equal(older.Learned.Weights, restored.Learned.Weights);
        Assert.True(restored.Utility!.LowerIncumbentImprovement > 0);
        Assert.True(store.ReadSpentAcceptanceIds(restored).Count > spentTitles);
    }

    private static AcceptedModelBundle LinearBundle(bool inverted, long revision, DateTime trainedAt)
    {
        var weights = new double[CandidateFeatures.FeatureCount];
        weights[(int)FeatureIndex.CombinedCriticScore] = inverted ? -1 : 1;
        return new AcceptedModelBundle
        {
            Learned = new LearnedScoringStrategy.WeightsData { Version = 5, Weights = weights, Bias = inverted ? .85 : 0, TrainingGeneration = 1 },
            Mixture = new AdmissionMixture(.75, 0),
            AlphaCap = .75,
            NeuralCap = .3,
            Revision = revision,
            TrainedAtUtc = trainedAt,
            AcceptanceObservationIds = Enumerable.Range(1, 20).Select(value => new Guid(value, 12, 0, new byte[8])).ToArray(),
            Utility = new AdmissionUtility(.02, 1, .02, null, null),
            State = new EnsembleScoringStrategy.EnsembleStateData { Alpha = .75, NeuralBeta = 0, QualityGateFrozen = false }
        };
    }

    private EnsembleScoringStrategy Create(string scope)
        => new(new LearnedScoringStrategy(Path.Join(_directory, scope + "-ml.json")),
            new HeuristicScoringStrategy(genrePenaltyFloor: 1), new NeuralScoringStrategy(),
            statePath: Path.Join(_directory, scope + "-state.json"), genrePenaltyFloor: 1);

    private static TrainingExample[] Examples(int count, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(index =>
        {
            var a = random.Next(2) == 0;
            var b = random.Next(2) == 0;
            return new TrainingExample
            {
                Features = new CandidateFeatures
                {
                    GenreSimilarity = .8,
                    CombinedCriticScore = .8,
                    RecencyScore = a ? .9 : .1,
                    YearProximityScore = b ? .9 : .1,
                    GenreCount = 2
                },
                Label = a != b ? .85 : 0,
                ObservationId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(seed + ":" + index)).AsSpan(0, 16)),
                HasStableObservationId = true,
                GeneratedAtUtc = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
        }).ToArray();
    }
}
