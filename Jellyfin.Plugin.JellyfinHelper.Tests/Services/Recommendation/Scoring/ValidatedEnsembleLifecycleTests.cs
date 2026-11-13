using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Real detached fits prove atomic promotion, restart, retained incumbents and immutable Neural borrowing.</summary>
public sealed class ValidatedEnsembleLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-validated-lifecycle-" + Guid.NewGuid().ToString("N"));

    public ValidatedEnsembleLifecycleTests() => Directory.CreateDirectory(_directory);

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
        Assert.True(ensemble.TrainValidated(fit, holdout, true, TestContext.Current.CancellationToken));
        Assert.False(ensemble.LastAdmission!.Accepted);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RejectedInitialFitRemainsHeuristicOnlyAfterRestart()
    {
        using var ensemble = Create("rejected-restart");
        var holdout = Examples(80, 141);
        foreach (var row in holdout) row.Label = .85;
        Assert.True(ensemble.TrainValidated(Examples(200, 142), holdout, false, TestContext.Current.CancellationToken));
        Assert.False(ensemble.LastAdmission!.Accepted);
        var probes = Examples(24, 143).Select(row => row.Features).ToArray();
        var before = probes.Select(ensemble.Score).ToArray();
        Assert.Equal(0, ensemble.GetDiagnosticsSnapshot().EffectiveAlpha);
        using var restarted = Create("rejected-restart");
        Assert.Equal(before, probes.Select(restarted.Score).ToArray());
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
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
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
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
        Assert.True(restarted.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
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
        commit.Invoke(ensemble, [incumbent, evidence, rows, configuration, TestContext.Current.CancellationToken]);
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
        Assert.True(ensemble.Train(fit, holdout, trainNeural: false));
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
