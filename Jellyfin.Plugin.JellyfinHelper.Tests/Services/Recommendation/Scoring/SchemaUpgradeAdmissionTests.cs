using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Schema-only refits never load old predictors or discard independent evidence and recovery receipts.</summary>
public sealed class SchemaUpgradeAdmissionTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "jfh-schema-upgrade-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime ReferenceTime = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid User = new(123, 4, 5, new byte[8]);

    public SchemaUpgradeAdmissionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IntactProvisionalSchemaChangeRequiresFreshV6FittingButNotCorruptionRecovery()
    {
        var statePath = Path.Join(_directory, "ensemble.json");
        var fit = Rows(520, 1);
        var holdout = Holdout();
        var old = PreviousBundle(fit);
        WritePreviousSnapshot(statePath, old);
        var bundlePath = AcceptedModelStore.GetBundlePath(statePath)!;
        var ledgerPath = AcceptedModelStore.GetLedgerPath(statePath)!;
        var before = File.ReadAllBytes(bundlePath);
        var ledger = File.ReadAllBytes(ledgerPath);
        var assessmentBytes = File.ReadAllBytes(statePath + ".assessment.json");
        using (var loaded = Create(statePath))
        {
            Assert.Equal(0, loaded.GetDiagnosticsSnapshot().EffectiveAlpha);
            Assert.Equal(520, loaded.TrainingExampleCount);
            Assert.False(loaded.LearnedStrategy.HasOwnWeights);
            Assert.Equal(before, File.ReadAllBytes(bundlePath));
            Assert.Equal(ledger, File.ReadAllBytes(ledgerPath));
            Assert.Equal(assessmentBytes, File.ReadAllBytes(statePath + ".assessment.json"));
            Assert.Null(loaded.GetAcceptedNeuralPrediction());
            Assert.Equal(new HeuristicScoringStrategy(1).Score(fit[0].Features), loaded.Score(fit[0].Features));
            Assert.True(loaded.TrainValidated(fit, holdout, false, TestContext.Current.CancellationToken));
            Assert.Equal("Provisional", loaded.GetDiagnosticsSnapshot().ValidationLevel);
            Assert.False(loaded.LastAdmission!.Accepted);
            var fresh = Assert.IsType<AcceptedModelBundle>(new AcceptedModelStore(statePath).Read());
            Assert.Equal(6, fresh.Learned.Version);
            Assert.Equal(41, fresh.Learned.Weights.Length);
            Assert.Equal(9, fresh.Revision);
            Assert.Null(fresh.Neural); // An incompatible old shared network cannot be borrowed.
            Assert.Equal(old.AcceptanceObservationIds.Order(), loaded.GetAcceptedObservationIds()!.Order());
        }

        using var restarted = Create(statePath);
        Assert.Equal("Provisional", restarted.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.Equal(9, restarted.GetDiagnosticsSnapshot().ModelRevision);
        Assert.Equal(old.AcceptanceObservationIds.Order(), restarted.GetAcceptedObservationIds()!.Order());
    }

    [Fact]
    public void ProvisionalServingDoesNotEraseAvailableIndependentAcceptanceTitles()
    {
        var statePath = Path.Join(_directory, "diagnostics.json");
        using var ensemble = Create(statePath);
        Assert.True(ensemble.TrainValidated(Rows(520, 1), Holdout(), false, TestContext.Current.CancellationToken));
        Assert.Equal("Provisional", ensemble.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.Equal(24, ensemble.LastAdmission!.Evidence.CalibrationTitles);
        Assert.Equal(33, ensemble.LastAdmission.Evidence.AcceptanceTitles);
        Assert.Equal(33, ensemble.GetDiagnosticsSnapshot().AcceptanceTitleCount);
        var trace = Assert.IsType<ProvisionalServingAssessment>(ensemble.LastAdmission.ProvisionalServing);
        Assert.Equal("HomogeneousObservedOutcomes", trace.IndependentReason);
        Assert.Equal(ensemble.LastAdmission.Candidate, trace.AttemptedMixture);
        Assert.Equal(trace.AttemptedCalibration, trace.SelectedCalibration);
        Assert.True(trace.SelectedCalibration.HeuristicLoss > 0);
        Assert.True(trace.SelectedCalibration.RankingSafe);
        Assert.Null(trace.SelectedCalibration.NeuralLoss);
        Assert.Null(trace.SelectedCalibration.IncumbentLoss);
        Assert.True(trace.Valid());
        var response = EnsembleDiagnosticsResponse.FromDiagnostics(ensemble.GetDiagnosticsSnapshot());
        Assert.Equal(trace.IndependentReason, response.IndependentAdmissionReason);
        Assert.Equal(trace.SelectedCalibration.MixtureLoss, response.ProvisionalCalibrationLoss);
        Assert.Equal(trace.SelectedCalibration.LearnedLoss, response.ProvisionalLearnedCalibrationLoss);
        Assert.Equal(trace.SelectedCalibration.HeuristicLoss, response.ProvisionalHeuristicCalibrationLoss);
        Assert.Null(response.ProvisionalNeuralCalibrationLoss);
        Assert.Null(response.ProvisionalIncumbentCalibrationLoss);
        Assert.Null(response.CandidateValidationLoss);
        Assert.Null(ensemble.LastAdmission.Utility.CandidateLoss);
        Assert.Empty(ensemble.GetAcceptedObservationIds()!); // Positive-only labels are not independently spent.
        using var restarted = Create(statePath);
        Assert.Equal(ensemble.LastAdmission, restarted.LastAdmission);
        Assert.Equal(33, restarted.GetDiagnosticsSnapshot().AcceptanceTitleCount);
    }

    [Theory]
    [InlineData("missing-model")]
    [InlineData("malformed-model")]
    [InlineData("oversized-model")]
    [InlineData("unknown-version")]
    [InlineData("learned-shape")]
    [InlineData("learned-bias")]
    [InlineData("learned-normalization")]
    [InlineData("learned-imputation")]
    [InlineData("learned-limits")]
    [InlineData("learned-unfitted")]
    [InlineData("neural-shape")]
    [InlineData("neural-bias")]
    [InlineData("neural-normalization")]
    [InlineData("neural-imputation")]
    [InlineData("neural-unfitted")]
    [InlineData("mixed-versions")]
    [InlineData("optimizer-shape")]
    [InlineData("missing-bias-field")]
    [InlineData("missing-retired-field")]
    [InlineData("duplicate-version")]
    [InlineData("duplicate-neural-field")]
    [InlineData("invalid-receipt")]
    [InlineData("invalid-maturity")]
    [InlineData("invalid-digest")]
    [InlineData("retired")]
    [InlineData("missing-ledger")]
    [InlineData("corrupt-ledger")]
    [InlineData("missing-assessment")]
    [InlineData("malformed-assessment")]
    [InlineData("oversized-assessment")]
    [InlineData("missing-recovery-field")]
    [InlineData("missing-reservations")]
    [InlineData("duplicate-recovery-field")]
    [InlineData("older-assessment")]
    [InlineData("newer-assessment")]
    [InlineData("zero-assessment-revision")]
    [InlineData("empty-training-receipt")]
    [InlineData("duplicate-training-id")]
    [InlineData("empty-training-id")]
    [InlineData("mismatching-training-count")]
    [InlineData("forgotten-checkpoint-id")]
    [InlineData("recovery-required")]
    [InlineData("withdrawn")]
    public void SchemaReplacementCannotBypassDamagedOrSuppressedTrustReceipts(string fault)
    {
        var statePath = Path.Join(_directory, "damaged.json");
        var fit = Rows(520, 1);
        WritePreviousSnapshot(statePath, PreviousBundle(fit));
        DamageSnapshot(statePath, fault);
        var bundlePath = AcceptedModelStore.GetBundlePath(statePath)!;
        var bundle = File.Exists(bundlePath) ? File.ReadAllBytes(bundlePath) : null;
        var ledgerPath = AcceptedModelStore.GetLedgerPath(statePath)!;
        var ledger = File.Exists(ledgerPath) ? File.ReadAllBytes(ledgerPath) : null;
        using (var loaded = Create(statePath))
        {
            Assert.Equal(0, loaded.GetDiagnosticsSnapshot().EffectiveAlpha);
            loaded.TrainValidated(fit, Holdout(), false, TestContext.Current.CancellationToken);
            Assert.Equal(0, loaded.GetDiagnosticsSnapshot().EffectiveAlpha);
            Assert.NotEqual("Provisional", loaded.GetDiagnosticsSnapshot().ValidationLevel);
            Assert.False(loaded.LastAdmission?.Accepted);
            Assert.Equal(bundle, File.Exists(bundlePath) ? File.ReadAllBytes(bundlePath) : null);
            Assert.Equal(ledger, File.Exists(ledgerPath) ? File.ReadAllBytes(ledgerPath) : null);
        }

        using var restarted = Create(statePath);
        restarted.TrainValidated(Rows(520, 4), Holdout(), false, TestContext.Current.CancellationToken);
        Assert.Equal(0, restarted.GetDiagnosticsSnapshot().EffectiveAlpha);
        Assert.NotEqual("Provisional", restarted.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.Equal(bundle, File.Exists(bundlePath) ? File.ReadAllBytes(bundlePath) : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOnlySchemaRecognitionPreservesPreLedgerReceiptsAndAcceptsAbsentOptionalNormalization(bool neuralPresent)
    {
        var statePath = Path.Join(_directory, "pre-ledger.json");
        var old = PreviousBundle(Rows(520, 1));
        old.AcceptanceLedgerRequired = false;
        old.Learned.FeatureMeans = old.Learned.FeatureStdDevs = old.Learned.ImputationMeans = null;
        if (neuralPresent) { old.Neural!.FeatureMeans = old.Neural.FeatureStdDevs = old.Neural.ImputationMeans = null; }
        else { old.Neural = null; old.Mixture = new AdmissionMixture(.75, 0); old.State.NeuralBeta = 0; }
        WritePreviousSnapshot(statePath, old);
        File.Delete(AcceptedModelStore.GetLedgerPath(statePath)!);
        var assessment = JsonNode.Parse(File.ReadAllText(statePath + ".assessment.json"))!;
        assessment["AcceptanceLedgerRequired"] = false;
        File.WriteAllText(statePath + ".assessment.json", assessment.ToJsonString());
        var store = new AcceptedModelStore(statePath);
        Assert.Null(store.Read(out var superseded));
        Assert.Equal(8, Assert.IsType<AcceptedModelStore.SupersededCheckpoint>(superseded).Revision);
        Assert.False(File.Exists(AcceptedModelStore.GetLedgerPath(statePath)));
        using var loaded = Create(statePath);
        Assert.Equal(old.AcceptanceObservationIds.Order(), loaded.GetAcceptedObservationIds()!.Order());
        Assert.True(loaded.TrainValidated(Rows(520, 1), Holdout(), false, TestContext.Current.CancellationToken));
        Assert.Equal("Provisional", loaded.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.Equal(old.AcceptanceObservationIds.Order(), loaded.GetAcceptedObservationIds()!.Order());
        Assert.Empty(store.ReadHistory());
    }

    [Fact]
    public void AnInsufficientFirstRefitPersistsTheDiscardedRevisionAcrossRestartWithoutLoadingOldWeights()
    {
        var statePath = Path.Join(_directory, "interrupted-upgrade.json");
        WritePreviousSnapshot(statePath, PreviousBundle(Rows(520, 1)));
        var before = File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!);
        using (var first = Create(statePath))
        {
            Assert.False(first.TrainValidated(Rows(2, 4), Holdout(), false, TestContext.Current.CancellationToken));
            Assert.Equal(0, first.GetDiagnosticsSnapshot().ModelRevision);
            Assert.Equal(before, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
            Assert.Equal(8, EnsembleScoringStrategy.ReadAssessment(statePath + ".assessment.json", true)!.AcceptedModelRevision);
        }

        using var restarted = Create(statePath);
        Assert.True(restarted.TrainValidated(Rows(520, 1), Holdout(), false, TestContext.Current.CancellationToken));
        Assert.Equal("Provisional", restarted.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.Equal(9, restarted.GetDiagnosticsSnapshot().ModelRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaRefitRetainsRejectionReservationsAndOutcomeIndependentCursor(bool wildcard)
    {
        var statePath = Path.Join(_directory, "reserved-upgrade.json");
        WritePreviousSnapshot(statePath, PreviousBundle(Rows(520, 1)));
        var assessment = JsonNode.Parse(File.ReadAllText(statePath + ".assessment.json"))!;
        var digest = wildcard ? "*" : new string('B', 64);
        assessment["ReservedOperatingInputDigests"] = new JsonArray(JsonValue.Create(digest));
        assessment["AcceptanceBlockCursor"] = 123;
        File.WriteAllText(statePath + ".assessment.json", assessment.ToJsonString());
        using var loaded = Create(statePath);
        loaded.TrainValidated(Rows(520, 1), Holdout(), false, TestContext.Current.CancellationToken);
        var journal = EnsembleScoringStrategy.ReadAssessment(statePath + ".assessment.json", true)!;
        Assert.Contains(digest, journal.ReservedOperatingInputDigests);
        Assert.Equal(123, journal.AcceptanceBlockCursor);
        Assert.Equal(wildcard ? "Heuristic" : "Provisional", loaded.GetDiagnosticsSnapshot().ValidationLevel);
    }

    [Fact]
    public void CalibrationFailureTraceSurvivesRetainedParametersAndReplayWithoutBecomingAcceptanceUtility()
    {
        var statePath = Path.Join(_directory, "retained-trace.json");
        using var ensemble = Create(statePath);
        var fit = Rows(520, 1);
        Assert.True(ensemble.TrainValidated(fit, Holdout(), false, TestContext.Current.CancellationToken));
        var before = new AcceptedModelStore(statePath).Read()!;
        var worse = Rows(520, 4);
        foreach (var row in worse) row.Label = 0;
        Assert.True(ensemble.TrainValidated(worse, Holdout(), false, TestContext.Current.CancellationToken));
        Assert.Equal("ProvisionalParametersRetained", ensemble.LastAdmission!.Reason);
        var trace = Assert.IsType<ProvisionalServingAssessment>(ensemble.LastAdmission.ProvisionalServing);
        Assert.True(trace.AttemptedCalibration.MixtureLoss > trace.AttemptedCalibration.IncumbentLoss);
        Assert.True(trace.SelectedCalibration.LearnedLoss < trace.AttemptedCalibration.LearnedLoss);
        Assert.Equal("HomogeneousObservedOutcomes", trace.IndependentReason);
        Assert.Equal(33, ensemble.LastAdmission.Evidence.AcceptanceTitles);
        var retained = new AcceptedModelStore(statePath).Read()!;
        Assert.Equal(before.Learned.Weights, retained.Learned.Weights);
        Assert.Equal(before.TrainedAtUtc, retained.TrainedAtUtc);
        Assert.Null(retained.Utility);
        using var restarted = Create(statePath);
        Assert.Equal(ensemble.LastAdmission, restarted.LastAdmission);
        restarted.SetPinnedNeuralTrainingPrior(null);
        Assert.False(restarted.TrainValidated(worse, Holdout(), false, TestContext.Current.CancellationToken));
        Assert.Equal("UnchangedOperatingInputs", restarted.LastAdmission!.Reason);
        Assert.Equal(trace, restarted.LastAdmission.ProvisionalServing);
        Assert.Null(restarted.LastAdmission.Utility.CandidateLoss);
        Assert.Empty(restarted.GetAcceptedObservationIds()!);
    }

    [Theory]
    [InlineData("missing-selected")]
    [InlineData("null-selected")]
    [InlineData("missing-heuristic")]
    [InlineData("invalid-loss")]
    [InlineData("invalid-title-count")]
    [InlineData("invalid-independent-reason")]
    [InlineData("duplicate-loss")]
    [InlineData("null-independent-utility")]
    public void MalformedServingDiagnosticsCannotInventMetricsOrEraseRecoveryRestrictions(string fault)
    {
        var statePath = Path.Join(_directory, "trace-corruption.json");
        var holdout = Holdout();
        double[] scores;
        using (var first = Create(statePath))
        {
            Assert.True(first.TrainValidated(Rows(520, 1), holdout, false, TestContext.Current.CancellationToken));
            scores = holdout.Select(row => first.Score(row.Features)).ToArray();
        }

        var path = statePath + ".assessment.json";
        var journal = JsonNode.Parse(File.ReadAllText(path))!;
        var trace = journal["Decision"]!["ProvisionalServing"]!;
        switch (fault)
        {
            case "missing-selected": trace.AsObject().Remove("SelectedCalibration"); break;
            case "null-selected": trace["SelectedCalibration"] = null; break;
            case "missing-heuristic": trace["SelectedCalibration"]!.AsObject().Remove("HeuristicLoss"); break;
            case "invalid-loss": trace["SelectedCalibration"]!["MixtureLoss"] = -1; break;
            case "invalid-title-count": trace["SelectedCalibration"]!["Titles"] = 0; break;
            case "invalid-independent-reason": trace["IndependentReason"] = "Accepted"; break;
            case "null-independent-utility": journal["Decision"]!["Utility"] = null; break;
            case "duplicate-loss":
                File.WriteAllText(path, journal.ToJsonString().Replace("\"LearnedLoss\":", "\"LearnedLoss\":0,\"LearnedLoss\":", StringComparison.Ordinal));
                break;
            default: throw new ArgumentException("Unknown trace fault.", nameof(fault));
        }

        if (fault != "duplicate-loss") File.WriteAllText(path, journal.ToJsonString());
        var bytes = File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!);
        using var loaded = Create(statePath);
        Assert.Equal("UnreadableAdmissionAssessment", loaded.LastAdmission!.Reason);
        Assert.Null(loaded.GetDiagnosticsSnapshot().ProvisionalCalibrationLoss);
        Assert.Equal(scores, holdout.Select(row => loaded.Score(row.Features)).ToArray());
        loaded.TrainValidated(Rows(520, 4), holdout, false, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, File.ReadAllBytes(AcceptedModelStore.GetBundlePath(statePath)!));
        Assert.True(EnsembleScoringStrategy.ReadAssessment(path, true)!.IndependentRecoveryRequired);
    }

    [Fact]
    public void IntactDiscardedOptimizerIsValidatedButNeverLoadedOrBorrowed()
    {
        var statePath = Path.Join(_directory, "old-optimizer.json");
        var old = PreviousBundle(Rows(520, 1));
        int[] lengths = [38 * 76, 76 * 96, 96 * 48, 48 * 24, 24, 76, 96, 48, 24];
        old.Neural!.TrainingState = new NeuralTrainingState
        {
            Timestep = 1, FirstMoments = lengths.Select(length => new double[length]).ToArray(), SecondMoments = lengths.Select(length => new double[length]).ToArray(),
            InputWeightLimits = Enumerable.Repeat(2d, 38).ToArray(), InputBiasLimits = Enumerable.Repeat(2d, 76).ToArray()
        };
        WritePreviousSnapshot(statePath, old);
        Assert.Null(new AcceptedModelStore(statePath).Read(out var superseded));
        Assert.NotNull(superseded);
        Assert.Null(NeuralScoringStrategy.FromParameters(old.Neural));
        using var loaded = Create(statePath);
        Assert.True(loaded.TrainValidated(Rows(520, 1), Holdout(), false, TestContext.Current.CancellationToken));
        Assert.Equal("Provisional", loaded.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.Null(new AcceptedModelStore(statePath).Read()!.Neural);
    }

    [Fact]
    public void DamagedParametersCanRecoverOnFreshIndependentEvidenceWithoutRecyclingSpentTitles()
    {
        var statePath = Path.Join(_directory, "real-recovery.json");
        var old = PreviousBundle(Rows(520, 1));
        WritePreviousSnapshot(statePath, old);
        DamageSnapshot(statePath, "neural-bias");
        using (var recovering = Create(statePath))
        {
            recovering.TrainValidated(Rows(520, 1), Holdout(), false, TestContext.Current.CancellationToken);
            Assert.Equal(0, recovering.GetDiagnosticsSnapshot().EffectiveAlpha);
            Assert.True(EnsembleScoringStrategy.ReadAssessment(statePath + ".assessment.json", true)!.IndependentRecoveryRequired);
            var fit = MixedRows(520, 5);
            var independent = MixedRows(500, 6, permanentHoldout: true);
            Assert.True(recovering.TrainValidated(fit, independent, false, TestContext.Current.CancellationToken));
            Assert.True(recovering.LastAdmission!.Accepted);
            Assert.Equal("Verified", recovering.GetDiagnosticsSnapshot().ValidationLevel);
            Assert.True(recovering.GetDiagnosticsSnapshot().EffectiveAlpha > 0);
            Assert.All(old.AcceptanceObservationIds, id => Assert.Contains(id, recovering.GetAcceptedObservationIds()!));
            Assert.False(EnsembleScoringStrategy.ReadAssessment(statePath + ".assessment.json", true)!.IndependentRecoveryRequired);
            var fresh = Assert.IsType<AcceptedModelBundle>(new AcceptedModelStore(statePath).Read());
            Assert.Equal(6, fresh.Learned.Version);
            Assert.NotNull(fresh.Utility);
            Assert.Null(fresh.Neural);
            Assert.Equal(9, fresh.Revision);
        }

        using var restarted = Create(statePath);
        Assert.Equal("Verified", restarted.GetDiagnosticsSnapshot().ValidationLevel);
        Assert.All(old.AcceptanceObservationIds, id => Assert.Contains(id, restarted.GetAcceptedObservationIds()!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedAcceptanceStillReportsAvailableObservedTitlesWithoutScoringOrSpendingThem(bool unrelatedUser)
    {
        var calibration = MixedRows(24, 7, permanentHoldout: true);
        var acceptance = unrelatedUser ? MixedRows(33, 8, permanentHoldout: true) : Rows(33, 8, permanentHoldout: true);
        if (unrelatedUser) foreach (var row in acceptance) row.UserId = new Guid(456, 0, 0, new byte[8]);
        var reserved = false;
        var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, acceptance, new AdmissionModels(new HeuristicScoringStrategy(1), new LearnedScoringStrategy()), ReferenceTime, .75, 0, _ => { reserved = true; return null; });
        Assert.False(decision.Accepted);
        Assert.Equal(unrelatedUser ? "InsufficientIndependentRankingEvidence" : "HomogeneousObservedOutcomes", decision.Reason);
        Assert.Equal(24, decision.Evidence.CalibrationTitles);
        Assert.Equal(33, decision.Evidence.AcceptanceTitles);
        Assert.Equal(0, decision.Evidence.AcceptanceEffectiveTitles);
        Assert.False(reserved);
        Assert.Null(decision.Utility.CandidateLoss);
    }

    [Fact]
    public void SchemaRefitMayUseOnlyAFreshCompatibleSharedNeuralPredictorWithinUnchangedCaps()
    {
        var statePath = Path.Join(_directory, "compatible-prior.json");
        var old = PreviousBundle(Rows(520, 1));
        WritePreviousSnapshot(statePath, old);
        var prior = new NeuralScoringStrategy.NeuralWeightsData
        {
            Version = 6, TrainingGeneration = 1, BiasOutput = Math.Log(.85 / .15),
            WeightsIH = new double[41 * 82], BiasH1 = new double[82], WeightsH1H2 = new double[82 * 96], BiasH2 = new double[96],
            WeightsH2H3 = new double[96 * 48], BiasH3 = new double[48], WeightsH3H4 = new double[48 * 24], BiasH4 = new double[24], WeightsH4O = new double[24]
        };
        using var loaded = Create(statePath);
        loaded.SetPinnedNeuralTrainingPrior(new NeuralScoringStrategy.PredictionSnapshot(prior));
        Assert.True(loaded.TrainValidated(Rows(520, 1), Holdout(), false, TestContext.Current.CancellationToken));
        var diagnostics = loaded.GetDiagnosticsSnapshot();
        Assert.Equal("Provisional", diagnostics.ValidationLevel);
        Assert.InRange(diagnostics.NeuralShare, double.Epsilon, .3);
        Assert.InRange(diagnostics.EffectiveAlpha, 0, .75);
        Assert.Equal(6, diagnostics.NeuralModelVersion);
        Assert.Equal(41, diagnostics.NeuralInputFeatureCount);
        Assert.False(loaded.LastAdmission!.Accepted);
        Assert.Null(loaded.LastAdmission.Utility.LowerNeuralImprovement);
        Assert.Equal(33, loaded.LastAdmission.Evidence.AcceptanceTitles);
        Assert.Equal(prior.BiasOutput, new AcceptedModelStore(statePath).Read()!.Neural!.BiasOutput);
    }

    [Fact]
    public void GlobalSchemaRefitTrainsFreshNetworkAndOptimizerCoordinatesInsteadOfRevivingTheDiscardedPredictor()
    {
        var statePath = Path.Join(_directory, "global-upgrade.json");
        WritePreviousSnapshot(statePath, PreviousBundle(Rows(520, 1)));
        using var ensemble = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), new NeuralScoringStrategy(), statePath: statePath, genrePenaltyFloor: 1);
        Assert.Null(ensemble.GetAcceptedNeuralPrediction());
        Assert.True(ensemble.TrainValidated(Rows(520, 1), Holdout(), true, TestContext.Current.CancellationToken));
        var fresh = Assert.IsType<AcceptedModelBundle>(new AcceptedModelStore(statePath).Read());
        Assert.True(fresh.IsProvisional);
        Assert.Equal(9, fresh.Revision);
        Assert.Equal(6, fresh.Learned.Version);
        var neural = Assert.IsType<NeuralScoringStrategy.NeuralWeightsData>(fresh.Neural);
        Assert.Equal(6, neural.Version);
        Assert.Equal(41 * 82, neural.WeightsIH.Length);
        Assert.Equal(82, neural.BiasH1.Length);
        Assert.Equal(1, neural.TrainingGeneration);
        Assert.True(Assert.IsType<NeuralTrainingState>(neural.TrainingState).Valid(41));
        Assert.False(ensemble.LastAdmission!.Accepted);
        Assert.InRange(ensemble.GetDiagnosticsSnapshot().NeuralShare, double.Epsilon, .3);
        Assert.Equal(33, ensemble.LastAdmission.Evidence.AcceptanceTitles);
    }

    [Fact]
    public void DuplicatePreLedgerReceiptFieldsCannotReleaseHiddenSpentIdentitiesOrBeOverwritten()
    {
        var statePath = Path.Join(_directory, "ambiguous-spent.json");
        var old = PreviousBundle(Rows(520, 1));
        old.AcceptanceLedgerRequired = false;
        WritePreviousSnapshot(statePath, old);
        File.Delete(AcceptedModelStore.GetLedgerPath(statePath)!);
        var modelPath = AcceptedModelStore.GetBundlePath(statePath)!;
        var model = File.ReadAllText(modelPath).Replace("\"AcceptanceObservationIds\":[", "\"AcceptanceObservationIds\":[],\"AcceptanceObservationIds\":[", StringComparison.Ordinal);
        File.WriteAllText(modelPath, model);
        var before = File.ReadAllBytes(modelPath);
        var store = new AcceptedModelStore(statePath);
        Assert.Null(store.Read(out var superseded));
        Assert.Null(superseded);
        Assert.Throws<InvalidDataException>(() => store.ReadSpentAcceptanceIds(null));
        using var loaded = Create(statePath);
        Assert.False(loaded.TrainValidated(Rows(520, 1), Holdout(), false, TestContext.Current.CancellationToken));
        Assert.Equal(before, File.ReadAllBytes(modelPath));
        Assert.False(File.Exists(AcceptedModelStore.GetLedgerPath(statePath)));
    }

    private static TrainingExample[] MixedRows(int count, int seed, bool permanentHoldout = false)
    {
        var rows = Rows(count, seed, permanentHoldout);
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index].Label = index % 2 == 0 ? .85 : 0;
            rows[index].Features.CombinedCriticScore = rows[index].Label;
        }

        return rows;
    }

    private static void DamageSnapshot(string statePath, string fault)
    {
        var modelPath = AcceptedModelStore.GetBundlePath(statePath)!;
        var assessmentPath = statePath + ".assessment.json";
        var model = JsonNode.Parse(File.ReadAllText(modelPath))!;
        var assessment = JsonNode.Parse(File.ReadAllText(assessmentPath))!;
        switch (fault)
        {
            case "missing-model": File.Delete(modelPath); return;
            case "malformed-model": File.WriteAllText(modelPath, "{broken"); return;
            case "oversized-model": using (var stream = File.OpenWrite(modelPath)) stream.SetLength(11 * 1024 * 1024); return;
            case "unknown-version": model["Learned"]!["Version"] = 99; break;
            case "learned-shape": model["Learned"]!["Weights"] = new JsonArray(1); break;
            case "learned-bias": model["Learned"]!["Bias"] = "NaN"; break;
            case "learned-normalization": model["Learned"]!["FeatureStdDevs"] = null; break;
            case "learned-imputation": model["Learned"]!["ImputationMeans"] = new JsonArray(1); break;
            case "learned-limits": model["Learned"]!["WeightLimits"] = new JsonArray(1); break;
            case "learned-unfitted": model["Learned"]!["TrainingGeneration"] = 0; break;
            case "neural-shape": model["Neural"]!["WeightsH1H2"] = new JsonArray(1); break;
            case "neural-bias": model["Neural"]!["BiasOutput"] = "Infinity"; break;
            case "neural-normalization": model["Neural"]!["FeatureStdDevs"]![0] = -1; break;
            case "neural-imputation": model["Neural"]!["ImputationMeans"]![0] = "NaN"; break;
            case "neural-unfitted": model["Neural"]!["TrainingGeneration"] = 0; break;
            case "mixed-versions": model["Neural"]!["Version"] = 6; break;
            case "optimizer-shape": model["Neural"]!["TrainingState"] = JsonSerializer.SerializeToNode(new NeuralTrainingState()); break;
            case "missing-bias-field": model["Neural"]!.AsObject().Remove("BiasOutput"); break;
            case "missing-retired-field": model.AsObject().Remove("Retired"); break;
            case "duplicate-version": File.WriteAllText(modelPath, model.ToJsonString().Replace("\"Version\":5", "\"Version\":99,\"Version\":5", StringComparison.Ordinal)); return;
            case "duplicate-neural-field": File.WriteAllText(modelPath, model.ToJsonString().Replace("\"BiasOutput\":0", "\"BiasOutput\":1,\"BiasOutput\":0", StringComparison.Ordinal)); return;
            case "invalid-receipt": model["AcceptanceObservationIds"]![0] = Guid.Empty; break;
            case "invalid-maturity": model["State"]!["TrainingExampleCount"] = 519; break;
            case "invalid-digest": model["OperatingInputDigest"] = "bad"; break;
            case "retired": model["Retired"] = true; model["State"]!["QualityGateFrozen"] = true; model["RetirementUtility"] = JsonSerializer.SerializeToNode(new AdmissionUtility(.1, 1, .01, null, .01)); break;
            case "missing-ledger": File.Delete(AcceptedModelStore.GetLedgerPath(statePath)!); return;
            case "corrupt-ledger": File.AppendAllText(AcceptedModelStore.GetLedgerPath(statePath)!, "corrupted\n"); return;
            case "missing-assessment": File.Delete(assessmentPath); return;
            case "malformed-assessment": File.WriteAllText(assessmentPath, "{broken"); return;
            case "oversized-assessment": using (var stream = File.OpenWrite(assessmentPath)) stream.SetLength(2 * 1024 * 1024); return;
            case "missing-recovery-field": assessment.AsObject().Remove("IndependentRecoveryRequired"); break;
            case "missing-reservations": assessment.AsObject().Remove("ReservedOperatingInputDigests"); break;
            case "duplicate-recovery-field": File.WriteAllText(assessmentPath, assessment.ToJsonString().Replace("\"IndependentRecoveryRequired\":false", "\"IndependentRecoveryRequired\":true,\"IndependentRecoveryRequired\":false", StringComparison.Ordinal)); return;
            case "older-assessment": assessment["AcceptedModelRevision"] = 7; break;
            case "newer-assessment": assessment["AcceptedModelRevision"] = 9; break;
            case "zero-assessment-revision": assessment["AcceptedModelRevision"] = 0; break;
            case "empty-training-receipt": assessment["ObservedTrainingIds"] = new JsonArray(); assessment["TrainingExampleCount"] = 0; break;
            case "duplicate-training-id": assessment["ObservedTrainingIds"]![0] = assessment["ObservedTrainingIds"]![1]!.DeepClone(); break;
            case "empty-training-id": assessment["ObservedTrainingIds"]![0] = Guid.Empty; break;
            case "mismatching-training-count": assessment["TrainingExampleCount"] = 519; break;
            case "forgotten-checkpoint-id": assessment["ObservedTrainingIds"]![0] = new Guid(999999, 0, 0, new byte[8]); break;
            case "recovery-required": assessment["IndependentRecoveryRequired"] = true; break;
            case "withdrawn": assessment["LegacyModelSuppressed"] = true; break;
            default: throw new ArgumentException("Unknown injected fault.", nameof(fault));
        }

        File.WriteAllText(modelPath, model.ToJsonString());
        File.WriteAllText(assessmentPath, assessment.ToJsonString());
    }

    private static EnsembleScoringStrategy Create(string statePath)
    {
        var ensemble = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), statePath: statePath, genrePenaltyFloor: 1, ownsNeural: false);
        ensemble.SetSharedPredictionProvider(() => null);
        return ensemble;
    }

    private static TrainingExample[] Rows(int count, int seed, bool permanentHoldout = false)
        => Enumerable.Range(1, checked(count * 10)).Where(index => (index % 10 == 0) == permanentHoldout).Take(count).Select(index => new TrainingExample
        {
            Features = new CandidateFeatures { GenreSimilarity = .1, CombinedCriticScore = .1 },
            Label = .85, UserId = User, GeneratedAtUtc = ReferenceTime,
            ObservationId = new Guid(index, (short)seed, 0, new byte[8]), HasStableObservationId = true
        }).ToArray();

    private static TrainingExample[] Holdout()
    {
        var candidates = Rows(1000, 2, permanentHoldout: true);
        return candidates.Where(row => (SHA256.HashData(row.ObservationId.ToByteArray())[0] & 1) == 0).Take(24)
            .Concat(candidates.Where(row => (SHA256.HashData(row.ObservationId.ToByteArray())[0] & 1) != 0).Take(33)).ToArray();
    }

    private static AcceptedModelBundle PreviousBundle(TrainingExample[] fit) => new()
    {
        Version = 2, IsProvisional = true, Revision = 8, TrainedAtUtc = new DateTime(2026, 11, 14, 2, 32, 40, DateTimeKind.Utc),
        AlphaCap = .75, NeuralCap = .3, Mixture = new AdmissionMixture(.66, .09),
        CalibrationLoss = .0329, CalibrationTitleCount = 24, OperatingInputDigest = new string('A', 64),
        AcceptanceLedgerRequired = true, AcceptanceObservationIds = Rows(20, 3, permanentHoldout: true).Where(row => (SHA256.HashData(row.ObservationId.ToByteArray())[0] & 1) != 0).Take(2).Select(row => row.ObservationId).ToArray(),
        Learned = new LearnedScoringStrategy.WeightsData
        {
            Version = 5, TrainingGeneration = 6, Weights = new double[38], Bias = .85,
            FeatureMeans = new double[38], FeatureStdDevs = Enumerable.Repeat(1d, 38).ToArray(), ImputationMeans = new double[38]
        },
        Neural = new NeuralScoringStrategy.NeuralWeightsData
        {
            Version = 5, TrainingGeneration = 6, WeightsIH = new double[38 * 76], BiasH1 = new double[76],
            WeightsH1H2 = new double[76 * 96], BiasH2 = new double[96], WeightsH2H3 = new double[96 * 48], BiasH3 = new double[48],
            WeightsH3H4 = new double[48 * 24], BiasH4 = new double[24], WeightsH4O = new double[24],
            FeatureMeans = new double[38], FeatureStdDevs = Enumerable.Repeat(1d, 38).ToArray(), ImputationMeans = new double[38]
        },
        State = new EnsembleScoringStrategy.EnsembleStateData
        {
            Alpha = .75, NeuralBeta = .12, ObservedTrainingIds = fit.Select(row => row.ObservationId).ToArray(), TrainingExampleCount = fit.Length
        }
    };

    private static void WritePreviousSnapshot(string statePath, AcceptedModelBundle bundle)
    {
        File.WriteAllText(AcceptedModelStore.GetBundlePath(statePath)!, JsonSerializer.Serialize(bundle));
        new AcceptanceObservationLedger(AcceptedModelStore.GetLedgerPath(statePath), File.WriteAllText).Spend(bundle.AcceptanceObservationIds, [], false);
        File.WriteAllText(statePath + ".assessment.json", JsonSerializer.Serialize(new ModelTrainingAssessment
        {
            AcceptedModelRevision = bundle.Revision, IndependentAdmissionEnabled = true, AcceptanceLedgerRequired = true,
            ObservedTrainingIds = bundle.State.ObservedTrainingIds!, TrainingExampleCount = bundle.State.TrainingExampleCount,
            Decision = AdmissionDecision.Unknown("ProvisionalParametersRetained", 24, 0)
        }));
    }
}
