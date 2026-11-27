using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Generic provenance/cohort and synthetic model lifecycle checks; no live requests or personal outcome labels.</summary>
public class FeatureDerivationContractTests
{
    private static readonly DateTime Boundary = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions NamedNumberOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PersonalCurrentFitCannotRelabelABorrowedOlderNeuralComponent(int priorVersion)
    {
        var neural = ConstantNeural(priorVersion);
        var original = JsonSerializer.Serialize(neural.ExportParameters(), NamedNumberOptions);
        var learned = LearnedScoringStrategy.FromAcceptedParameters(new() { Version = 6, Weights = new double[41], Bias = .85, TrainingGeneration = 1 });
        var prior = new AcceptedPrediction(new HeuristicScoringStrategy(1), learned.CapturePredictionSnapshot(), neural, new AdmissionMixture(.3, .15), .3, priorVersion);
        using var personal = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), null, null, .3, .75, 1, ownsNeural: false);
        personal.InitializeOperatingPrior(prior);
        personal.SetPinnedNeuralTrainingPrior(neural);
        Assert.True(personal.TrainValidated(CurrentFittingExamples(300, 10), CurrentFittingExamples(100, 11), false, TestContext.Current.CancellationToken));
        var operating = Assert.IsType<AcceptedPrediction>(personal.GetOperatingPrior());
        Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, operating.FeatureDerivationVersion);
        if (priorVersion == CandidateFeatures.CurrentFeatureDerivationVersion)
        {
            Assert.True(operating.Resolve(.75).Neural > 0);
            Assert.Equal(priorVersion, operating.Neural!.FeatureDerivationVersion);
            Assert.Equal(original, JsonSerializer.Serialize(operating.Neural.ExportParameters(), NamedNumberOptions));
        }
        else
        {
            Assert.Equal(0, operating.Resolve(.75).Neural);
            Assert.Null(personal.GetAcceptedNeuralPrediction());
            Assert.Equal(0, personal.GetDiagnosticsSnapshot(CandidateFeatures.CurrentFeatureDerivationVersion).NeuralShare);
        }

        Assert.Equal(original, JsonSerializer.Serialize(neural.ExportParameters(), NamedNumberOptions));
        Assert.Equal(priorVersion, prior.FeatureDerivationVersion);
        Assert.Equal(priorVersion, neural.FeatureDerivationVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeuralProvenanceSurvivesRestartAndMissingProvenanceNeverInheritsTheCompoundMarker(bool removeMarker)
    {
        var directory = Path.Join(Path.GetTempPath(), "opencode", "component-derivation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var state = Path.Join(directory, "ensemble_state.json");
        var neural = ConstantNeural(CandidateFeatures.CurrentFeatureDerivationVersion);
        try
        {
            using (var personal = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), null, state, .3, .75, 1, ownsNeural: false))
            {
                personal.SetPinnedNeuralTrainingPrior(neural);
                Assert.True(personal.TrainValidated(CurrentFittingExamples(300, 12), CurrentFittingExamples(100, 13), false, TestContext.Current.CancellationToken));
            }

            var path = AcceptedModelStore.GetBundlePath(state)!;
            var saved = new AcceptedModelStore(state, null).Read()!;
            Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, saved.NeuralFeatureDerivationVersion);
            if (removeMarker)
            {
                var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                document.Remove(nameof(AcceptedModelBundle.NeuralFeatureDerivationVersion));
                File.WriteAllText(path, document.ToJsonString(NamedNumberOptions));
            }

            var before = File.ReadAllBytes(path);
            using var restarted = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), null, state, .3, .75, 1, ownsNeural: false);
            var diagnostics = restarted.GetDiagnosticsSnapshot(CandidateFeatures.CurrentFeatureDerivationVersion);
            Assert.Equal(removeMarker ? 0 : saved.Mixture.Neural, diagnostics.NeuralShare, 12);
            var restored = new AcceptedModelStore(state, null).Read()!;
            Assert.Equal(removeMarker ? 0 : CandidateFeatures.CurrentFeatureDerivationVersion, restored.NeuralFeatureDerivationVersion);
            Assert.Equal(JsonSerializer.Serialize(saved.Neural, NamedNumberOptions), JsonSerializer.Serialize(restored.Neural, NamedNumberOptions));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(2, 2, true)]
    [InlineData(1, 2, false)]
    [InlineData(2, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(-1, -1, false)]
    [InlineData(99, 99, false)]
    public void OnlyMatchingSupportedInputContractsMayUseTheSameWeights(int input, int model, bool compatible)
        => Assert.Equal(compatible, FeatureDerivationContract.IsCompatible(input, model));

    [Fact]
    public void MissingFeatureObjectCannotQualifyAUniformAdmissionCohort()
    {
        Assert.False(FeatureDerivationContract.TryResolveUniformVersion([new TrainingExample { Features = null! }], out var version));
        Assert.Equal(0, version);
    }

    [Fact]
    public void AServingPassKeepsItsOwnModelMixtureAndMetadataPriorsAcrossPublicationAndDisposal()
    {
        AcceptedPrediction Prior(double bias, double mean)
        {
            var learned = LearnedScoringStrategy.FromAcceptedParameters(new LearnedScoringStrategy.WeightsData
            {
                Version = 6, Weights = new double[41], Bias = bias, TrainingGeneration = 1,
                FeatureMeans = Enumerable.Repeat(mean, 41).ToArray(), FeatureStdDevs = Enumerable.Repeat(1d, 41).ToArray()
            });
            return new AcceptedPrediction(new HeuristicScoringStrategy(1), learned.CapturePredictionSnapshot(), null, new AdmissionMixture(.6, 0), 0, 2);
        }

        using var ensemble = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), null, null, .3, .75, 1, ownsNeural: false);
        ensemble.InitializeOperatingPrior(Prior(.9, .2));
        var features = new CandidateFeatures { FeatureDerivationVersion = 2, GenreSimilarity = .4, CombinedCriticScore = .5 };
        var original = features.ToVector();
        var snapshot = ensemble.CaptureServingSnapshot(.2);
        var before = snapshot.Strategy.Score(features);
        Assert.Equal(ensemble.Score(features), before, 12);
        ensemble.InitializeOperatingPrior(Prior(-.9, .8));
        Assert.True(Math.Abs(ensemble.Score(features) - before) > 1e-6);
        Assert.Equal(before, snapshot.Strategy.Score(features), 12);
        Assert.All(snapshot.FeatureMeans!, mean => Assert.Equal(.2, mean));
        ensemble.Dispose();
        Assert.Equal(before, snapshot.Strategy.ScoreWithExplanation(features).FinalScore, 12);
        Assert.Equal(original, features.ToVector());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AnUnpublishedServingSnapshotPinsItsOffsetConfigurationAndStrategyIdentity(int inputVersion)
    {
        var learned = LearnedScoringStrategy.FromAcceptedParameters(new LearnedScoringStrategy.WeightsData
        {
            Version = 6, Weights = new double[41], Bias = .9, TrainingGeneration = 1
        });
        using var ensemble = new EnsembleScoringStrategy(learned, new HeuristicScoringStrategy(1), genrePenaltyFloor: 1);
        var features = new CandidateFeatures { FeatureDerivationVersion = inputVersion, GenreSimilarity = .1, CombinedCriticScore = .5 };
        var original = features.ToVector();
        var snapshot = ensemble.CaptureServingSnapshot(.2);
        var score = snapshot.Strategy.Score(features);
        Assert.Equal(ensemble.ScoreWithOffset(features, .2), score, 12);
        Assert.Equal(ensemble.Name, snapshot.Strategy.Name);
        Assert.Equal(ensemble.NameKey, snapshot.Strategy.NameKey);
        Assert.Null(snapshot.FeatureMeans);
        if (inputVersion == 2) Assert.Equal(HeuristicScoringStrategy.ScoreRaw(features), score, 12);
        ensemble.Reconfigure(.1, .1, .1);
        Assert.NotEqual(score, ensemble.Score(features));
        ensemble.Dispose();
        Assert.Equal(score, snapshot.Strategy.ScoreWithExplanation(features).FinalScore, 12);
        Assert.Equal("strategyEnsemble", snapshot.Strategy.NameKey);
        Assert.Equal(original, features.ToVector());
        Assert.Equal(inputVersion, features.FeatureDerivationVersion);
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(1, 2, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 99, false)]
    public void NestedChoicesCannotBypassTheEnclosingInputCohortOrRewriteOriginalObservations(int chosenVersion, int alternativeVersion, bool valid)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var row = new TrainingExample
        {
            Features = new CandidateFeatures { FeatureDerivationVersion = 2, GenreSimilarity = .4 }, ObservationId = id, HasStableObservationId = true,
            IsObserved = true, Label = .8, SampleWeight = .7, GeneratedAtUtc = now.AddDays(-2), UserId = Guid.NewGuid(),
            Choice = new RecommendationChoiceEvidence
            {
                ChosenObservationId = id, AvailableAtUtc = now.AddDays(-3), RecommendedAtUtc = now.AddDays(-2), ChosenAtUtc = now.AddDays(-1),
                ChosenFeatures = new CandidateFeatures { FeatureDerivationVersion = chosenVersion },
                Alternatives = [new(Guid.NewGuid(), new CandidateFeatures { FeatureDerivationVersion = alternativeVersion })]
            }
        };
        var original = row.Features.ToVector();
        var choice = row.Choice;
        Assert.Equal(valid, FeatureDerivationContract.TryResolveUniformVersion([row], out _));
        var selected = Assert.Single(FeatureDerivationContract.SelectCohort([row], 2));
        Assert.Equal(original, selected.Features.ToVector());
        Assert.Equal(row.GeneratedAtUtc, selected.GeneratedAtUtc);
        Assert.Equal(row.ObservationId, selected.ObservationId);
        Assert.Equal(row.Label, selected.Label);
        Assert.Equal(row.SampleWeight, selected.SampleWeight);
        Assert.Same(choice, row.Choice);
        Assert.Equal(chosenVersion, row.Choice.ChosenFeatures.FeatureDerivationVersion);
        Assert.Equal(alternativeVersion, Assert.Single(row.Choice.Alternatives).Features.FeatureDerivationVersion);
        Assert.Equal(original, row.Features.ToVector());
        if (valid) Assert.Same(row, selected);
        else Assert.Null(selected.Choice);
        Assert.False(ChoiceRankingAdmissionEvaluator.HasSupport([row], now));
    }

    [Fact]
    public void IncompatibleChoiceWithoutFeaturesCannotBeCopiedOrAdmittedIntoTheCohort()
    {
        var row = new TrainingExample
        {
            Features = new CandidateFeatures { FeatureDerivationVersion = 2, GenreSimilarity = .4 }, ObservationId = Guid.NewGuid(), HasStableObservationId = true,
            IsObserved = true, Label = .8, SampleWeight = .7, GeneratedAtUtc = DateTime.UtcNow.AddDays(-2), UserId = Guid.NewGuid(),
            Choice = new RecommendationChoiceEvidence
            {
                ChosenObservationId = Guid.NewGuid(), AvailableAtUtc = DateTime.UtcNow.AddDays(-3), RecommendedAtUtc = DateTime.UtcNow.AddDays(-2), ChosenAtUtc = DateTime.UtcNow.AddDays(-1),
                ChosenFeatures = null!, Alternatives = [new(Guid.NewGuid(), new CandidateFeatures { FeatureDerivationVersion = 2 })]
            }
        };
        var selected = Assert.Single(FeatureDerivationContract.SelectCohort([row], 2));
        Assert.Null(selected.Choice);
        Assert.Equal(2, selected.Features.FeatureDerivationVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RetainedPriorCannotApplyDifferentDerivationWeightsOrOffsets(int modelVersion)
    {
        var learned = LearnedScoringStrategy.FromAcceptedParameters(new LearnedScoringStrategy.WeightsData
        {
            Version = 6, Weights = new double[41], Bias = .9, TrainingGeneration = 1
        });
        var prior = new AcceptedPrediction(new HeuristicScoringStrategy(1), learned.CapturePredictionSnapshot(), null, new AdmissionMixture(.6, 0), 0, modelVersion);
        using var ensemble = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), null, null, .3, .75, 1, ownsNeural: false);
        ensemble.InitializeOperatingPrior(prior);
        var current = new CandidateFeatures { FeatureDerivationVersion = CandidateFeatures.CurrentFeatureDerivationVersion, GenreSimilarity = .4, CombinedCriticScore = .5 };
        var original = current.ToVector();
        var heuristic = ScoringHelper.ComputeContextualScore(HeuristicScoringStrategy.ScoreRaw(current), current, 1);
        var score = ensemble.Score(current);
        if (modelVersion != CandidateFeatures.CurrentFeatureDerivationVersion)
        {
            Assert.Equal(heuristic, score, 12);
            Assert.Equal(0, ensemble.GetDiagnosticsSnapshot(CandidateFeatures.CurrentFeatureDerivationVersion).EffectiveAlpha);
            Assert.Equal(heuristic, prior.Score(current, .75, 1), 12);
        }
        else
        {
            Assert.True(score > heuristic);
            Assert.Equal(.3, ensemble.GetDiagnosticsSnapshot(CandidateFeatures.CurrentFeatureDerivationVersion).EffectiveAlpha);
        }

        Assert.Equal(score, ensemble.ScoreWithOffset(current, .2), 12);
        Assert.Equal(score, ensemble.ScoreWithExplanation(current).FinalScore, 12);
        Assert.Equal(score, ensemble.ScoreWithExplanationAndOffset(current, .2).FinalScore, 12);
        Assert.Equal(original, current.ToVector());
        Assert.Equal(modelVersion, ensemble.GetOperatingPrior()!.FeatureDerivationVersion);
        Assert.Equal(new AdmissionMixture(.6, 0), prior.Resolve(.75));
        var legacy = current.Copy();
        legacy.FeatureDerivationVersion = 0;
        if (modelVersion == 0) Assert.True(prior.Score(legacy, .75, 1) > heuristic);
        else Assert.Equal(heuristic, prior.Score(legacy, .75, 1), 12);
    }

    [Fact]
    public void MissingFeatureMarker_RemainsUnknownAndDoesNotChangeVector()
    {
        var old = JsonSerializer.Deserialize<CandidateFeatures>("{\"GenreSimilarity\":0.4,\"CollaborativeScore\":0.2,\"CompletionRatio\":0.7}")!;
        var current = old.Copy();
        current.FeatureDerivationVersion = CandidateFeatures.CurrentFeatureDerivationVersion;

        Assert.Equal(0, old.FeatureDerivationVersion);
        Assert.Equal(41, old.ToVector().Length);
        Assert.Equal(old.ToVector(), current.ToVector());
        Assert.DoesNotContain(nameof(CandidateFeatures.FeatureDerivationVersion), JsonSerializer.Serialize(old));
        Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, current.FeatureDerivationVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CapturedCopies_PreserveMarkerValuesLabelsAndPredictionTime(int version)
    {
        var original = Example(1, version, 1);
        original.Features.CollaborativeScore = .023;
        var copy = original.Copy();

        Assert.NotSame(original.Features, copy.Features);
        Assert.Equal(version, copy.Features.FeatureDerivationVersion);
        Assert.Equal(original.Features.ToVector(), copy.Features.ToVector());
        Assert.Equal(original.GeneratedAtUtc, copy.GeneratedAtUtc);
        Assert.Equal(original.ObservationId, copy.ObservationId);
        Assert.Equal(original.Label, copy.Label);
        copy.Features.FeatureDerivationVersion = 9;
        copy.Features.CollaborativeScore = .9;
        Assert.Equal(version, original.Features.FeatureDerivationVersion);
        Assert.Equal(.023, original.Features.CollaborativeScore);
    }

    [Fact]
    public void CurrentCohort_DoesNotRelabelOrUseLegacyPadding()
    {
        var old = Example(1, 0, 1);
        var current = Example(2, CandidateFeatures.CurrentFeatureDerivationVersion, 0);
        var fitting = new[] { old, current };
        var holdout = new[] { Example(3, 0, 0), Example(4, CandidateFeatures.CurrentFeatureDerivationVersion, 1) };
        var version = FeatureDerivationContract.SelectTrainingVersion(fitting, holdout);

        Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, version);
        Assert.Same(current, Assert.Single(FeatureDerivationContract.SelectCohort(fitting, version)));
        Assert.Same(holdout[1], Assert.Single(FeatureDerivationContract.SelectCohort(holdout, version)));
        Assert.Equal(0, old.Features.FeatureDerivationVersion);
        Assert.Equal(Boundary, old.GeneratedAtUtc);
        Assert.Equal(0, FeatureDerivationContract.SelectTrainingVersion([old], [holdout[0]]));
    }

    [Fact]
    public void SparseCurrentCohort_IsNotPaddedByManyOlderTitles()
    {
        var old = Enumerable.Range(1, 100).Select(id => Example(id, 0, id % 2)).ToArray();
        var current = Example(200, CandidateFeatures.CurrentFeatureDerivationVersion, 1);
        var version = FeatureDerivationContract.SelectTrainingVersion(old.Append(current).ToArray(), []);

        Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, version);
        Assert.Same(current, Assert.Single(FeatureDerivationContract.SelectCohort(old.Append(current), version)));
        Assert.All(old, row => Assert.Equal(0, row.Features.FeatureDerivationVersion));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    public void MixedOrUnsupportedDerivations_CannotClaimIndependentAdmission(int calibrationVersion, int acceptanceVersion)
    {
        var calibration = Samples(1, calibrationVersion);
        var acceptance = Samples(101, acceptanceVersion);
        var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, acceptance, Models(), Boundary, .75, 0);

        Assert.False(decision.Accepted);
        Assert.Equal("MixedOrUnsupportedFeatureDerivationEvidence", decision.Reason);
        Assert.Equal(0, decision.FeatureDerivationVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UniformNaturalCohort_PreservesOrdinaryIndependentAdmission(int version)
    {
        var calibration = Samples(1, version);
        var acceptance = Samples(101, version);
        var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, acceptance, Models(), Boundary, .75, 0);

        Assert.True(decision.Accepted);
        Assert.Equal(version, decision.FeatureDerivationVersion);
        Assert.All(calibration.Concat(acceptance), row => Assert.Equal(version, row.Features.FeatureDerivationVersion));
    }

    [Fact]
    public void CurrentHomogeneousCohort_DoesNotInventRankingApproval()
    {
        var calibration = Enumerable.Range(1, 30).Select(id => Example(id, CandidateFeatures.CurrentFeatureDerivationVersion, 1)).ToArray();
        var acceptance = Enumerable.Range(101, 30).Select(id => Example(id, CandidateFeatures.CurrentFeatureDerivationVersion, 1)).ToArray();
        var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, acceptance, Models(), Boundary, .75, 0);

        Assert.False(decision.Accepted);
        Assert.Equal("HomogeneousObservedOutcomes", decision.Reason);
        Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, decision.FeatureDerivationVersion);
    }

    [Fact]
    public void MixedCalibration_CannotSupplyOneProvisionalDerivation()
    {
        var mixed = Samples(1, 0).Concat(Samples(101, 1)).ToArray();
        Assert.Null(EnsembleAdmissionEvaluator.EvaluateServingCalibration(mixed, Models(), new AdmissionMixture(.5, 0), Boundary));
    }

    [Theory]
    [InlineData(0, "Verified", false, "LegacyOrUnknownPrior")]
    [InlineData(1, "Verified", false, "DifferentDerivationPrior")]
    [InlineData(2, "Provisional", false, "Current")]
    [InlineData(2, "GlobalPrior", false, "Current")]
    [InlineData(2, "Verified", true, "Current")]
    [InlineData(2, "ChoiceRanking", true, "Current")]
    [InlineData(3, "Verified", false, "DifferentDerivationPrior")]
    public void Diagnostics_SeparateHistoricalLevelFromCurrentDerivationApproval(int version, string level, bool verified, string status)
    {
        var diagnostics = new EnsembleDiagnostics { ModelFeatureDerivationVersion = version, ValidationLevel = level };
        var response = EnsembleDiagnosticsResponse.FromDiagnostics(diagnostics);

        Assert.Equal(version, response.ModelFeatureDerivationVersion);
        Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, response.CurrentFeatureDerivationVersion);
        Assert.Equal(status, response.FeatureDerivationStatus);
        Assert.Equal(verified, response.HasCurrentFeatureDerivationRankingValidation);
    }

    [Fact]
    public void CalibrationOnlyRetention_DoesNotUpgradeOlderParameterProvenance()
    {
        Assert.Equal(0, FeatureDerivationContract.PublicationVersion(1, retainedParameters: true, priorVersion: 0));
        Assert.Equal(1, FeatureDerivationContract.PublicationVersion(1, retainedParameters: false, priorVersion: 0));
        Assert.Equal(1, FeatureDerivationContract.PublicationVersion(1, retainedParameters: true, priorVersion: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void GlobalPrior_PropagatesMarkerWithoutChangingItsMixtureOrClaimingPersonalValidation(int version)
    {
        var learned = LearnedScoringStrategy.FromAcceptedParameters(new LearnedScoringStrategy.WeightsData
        {
            Version = 6, Weights = new double[41], Bias = .8, TrainingGeneration = 1
        });
        var prior = new AcceptedPrediction(new HeuristicScoringStrategy(1), learned.CapturePredictionSnapshot(), null, new AdmissionMixture(.6, 0), 0, version);
        using var personal = new EnsembleScoringStrategy(new LearnedScoringStrategy(), new HeuristicScoringStrategy(1), null, null, .3, .75, .1, ownsNeural: false);
        personal.InitializeOperatingPrior(prior);
        var diagnostics = personal.GetDiagnosticsSnapshot(CandidateFeatures.CurrentFeatureDerivationVersion);

        Assert.Equal("GlobalPrior", diagnostics.ValidationLevel);
        Assert.Equal(version, diagnostics.ModelFeatureDerivationVersion);
        Assert.False(diagnostics.HasCurrentFeatureDerivationRankingValidation);
        Assert.False(personal.HasOwnOperatingModel);
        Assert.Equal(version == CandidateFeatures.CurrentFeatureDerivationVersion ? .3 : 0, diagnostics.EffectiveAlpha);
        Assert.Equal(version, personal.GetOperatingPrior()!.FeatureDerivationVersion);
    }

    [Fact]
    public void LegacyV6Bundle_LoadsAsUnknownWithoutRewritingItsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "feature-derivation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var state = Path.Combine(directory, "ensemble_state.json");
        var path = AcceptedModelStore.GetBundlePath(state)!;
        var legacy = new AcceptedModelBundle
        {
            Version = 2, IsProvisional = true, CalibrationLoss = .1, CalibrationTitleCount = 5,
            Revision = 1, TrainedAtUtc = Boundary, Mixture = new AdmissionMixture(.6, 0), AlphaCap = .75, NeuralCap = 0,
            Learned = new LearnedScoringStrategy.WeightsData { Version = 6, Weights = new double[41], Bias = .8, TrainingGeneration = 1 },
            State = new EnsembleScoringStrategy.EnsembleStateData { TrainingExampleCount = 80, Alpha = .75, MetricsHistory = [] }
        };
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(legacy, NamedNumberOptions));
            var before = File.ReadAllBytes(path);
            var bundle = new AcceptedModelStore(state, logger: null).Read();

            Assert.NotNull(bundle);
            Assert.Equal(0, bundle.FeatureDerivationVersion);
            Assert.Equal(6, bundle.Learned.Version);
            Assert.Equal(41, bundle.Learned.Weights.Length);
            Assert.True(bundle.IsProvisional);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void SerializedOldBundleAndFeatures_DoNotAcquireCurrentMarkers()
    {
        var bundle = JsonSerializer.Deserialize<AcceptedModelBundle>("{\"Version\":2,\"IsProvisional\":true}")!;
        Assert.Equal(0, bundle.FeatureDerivationVersion);
        Assert.DoesNotContain(nameof(AcceptedModelBundle.FeatureDerivationVersion), JsonSerializer.Serialize(bundle, NamedNumberOptions));
    }

    [Fact]
    public void NewlyBuiltExternalInference_ExplicitlyMarksCurrentDerivation()
    {
        var profile = new UserWatchProfile();
        var preferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(preferences, profile);
        var features = ExternalCandidateFeatureBuilder.Build(new TmdbDiscoverItem { Id = 1, MediaType = "movie", GenreIds = [18] },
            preferences, new HashSet<string>(), 0, exposure, profile);

        Assert.Equal(CandidateFeatures.CurrentFeatureDerivationVersion, features.FeatureDerivationVersion);
        Assert.Equal(41, features.ToVector().Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void FeedbackFeatures_NewReconstructionIsCurrentButCapturedVersionIsPreserved(int capturedVersion)
    {
        var user = new Guid(9000, 0, 0, new byte[8]);
        var profile = new UserWatchProfile { UserId = user };
        var captured = capturedVersion < 0 ? null : new CandidateFeatures { FeatureDerivationVersion = capturedVersion, GenreSimilarity = .4, CollaborativeScore = .023 };
        var result = new RecommendationResult
        {
            UserId = user, GeneratedAt = Boundary,
            Recommendations = new Collection<RecommendedItem>
            {
                new() { ItemId = new Guid(501, 0, 0, new byte[8]), TmdbId = 501, ItemType = "Movie", Genres = ["Drama"], ScoringFeatures = captured }
            }
        };
        var built = TrainingDataBuilder.BuildExamples([result], new Collection<UserWatchProfile> { profile }, Boundary.AddDays(8), CancellationToken.None);
        var example = Assert.Single(built.Examples);

        Assert.Equal(capturedVersion < 0 ? CandidateFeatures.CurrentFeatureDerivationVersion : capturedVersion, example.Features.FeatureDerivationVersion);
        Assert.Equal(Boundary, example.GeneratedAtUtc);
        Assert.Equal(Boundary, result.GeneratedAt);
        if (captured is not null)
        {
            Assert.NotSame(captured, example.Features);
            Assert.Equal(captured.ToVector(), example.Features.ToVector());
            Assert.Equal(capturedVersion, captured.FeatureDerivationVersion);
        }
    }

    private static TrainingExample[] Samples(int start, int version)
        => Enumerable.Range(start, 30).Select(id => Example(id, version, id % 2)).ToArray();

    private static TrainingExample[] CurrentFittingExamples(int count, short partition)
        => Enumerable.Range(1, count).Select(index => new TrainingExample
        {
            Features = new CandidateFeatures { FeatureDerivationVersion = CandidateFeatures.CurrentFeatureDerivationVersion, GenreSimilarity = .1, CombinedCriticScore = .1, RecencyScore = .1, YearProximityScore = .1 },
            UserId = new Guid(9000, 0, 0, new byte[8]), ObservationId = new Guid(index, partition, 0, new byte[8]),
            HasStableObservationId = true, IsObserved = true, Label = .85, SampleWeight = 1, GeneratedAtUtc = DateTime.UtcNow.AddDays(-1)
        }).ToArray();

    private static NeuralScoringStrategy.PredictionSnapshot ConstantNeural(int version) => new(
        new NeuralScoringStrategy.NeuralWeightsData
        {
            Version = 6, TrainingGeneration = 1, BiasOutput = Math.Log(.85 / .15),
            WeightsIH = new double[NeuralScoringStrategy.Hidden1Size * CandidateFeatures.FeatureCount], BiasH1 = new double[NeuralScoringStrategy.Hidden1Size],
            WeightsH1H2 = new double[NeuralScoringStrategy.Hidden2Size * NeuralScoringStrategy.Hidden1Size], BiasH2 = new double[NeuralScoringStrategy.Hidden2Size],
            WeightsH2H3 = new double[NeuralScoringStrategy.Hidden3Size * NeuralScoringStrategy.Hidden2Size], BiasH3 = new double[NeuralScoringStrategy.Hidden3Size],
            WeightsH3H4 = new double[NeuralScoringStrategy.Hidden4Size * NeuralScoringStrategy.Hidden3Size], BiasH4 = new double[NeuralScoringStrategy.Hidden4Size], WeightsH4O = new double[NeuralScoringStrategy.Hidden4Size]
        },
        version);

    private static TrainingExample Example(int id, int version, double label)
        => new()
        {
            Features = new CandidateFeatures { FeatureDerivationVersion = version, GenreSimilarity = label, HasGenreEvidence = true },
            Label = label, GeneratedAtUtc = Boundary, UserId = new Guid(9000, 0, 0, new byte[8]),
            ObservationId = new Guid(id, 0, 0, new byte[8]), HasStableObservationId = true, IsObserved = true, SampleWeight = 1
        };

    private static AdmissionModels Models() => new(new ConstantStrategy(.5), new FeatureStrategy());

    private sealed class ConstantStrategy(double value) : IScoringStrategy
    {
        public string Name => "Generic diagnostic constant";
        public string NameKey => "genericDiagnostic";
        public double Score(CandidateFeatures features) => value;
        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => new() { FinalScore = value };
    }

    private sealed class FeatureStrategy : IScoringStrategy
    {
        public string Name => "Generic diagnostic feature";
        public string NameKey => "genericDiagnostic";
        public double Score(CandidateFeatures features) => features.GenreSimilarity;
        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => new() { FinalScore = Score(features) };
    }
}
