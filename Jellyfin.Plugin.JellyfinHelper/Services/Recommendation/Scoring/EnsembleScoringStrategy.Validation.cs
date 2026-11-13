using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <content>Detached challenger fitting and atomic, scope-bound independent model promotion.</content>
public sealed partial class EnsembleScoringStrategy
{
    private static readonly JsonSerializerOptions AssessmentJsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly HashSet<Guid> _spentAcceptanceObservations = [];
    private int _validationTraining;
    private AcceptedPrediction? _acceptedPrediction;
    private LearnedScoringStrategy? _acceptedLearnedTrainer;
    private NeuralScoringStrategy? _acceptedNeuralTrainer;
    private AcceptedModelBundle? _acceptedBundle;
    private Func<NeuralScoringStrategy.PredictionSnapshot?>? _sharedPredictionProvider;
    private HashSet<Guid> _seedAcceptanceObservations = [];
    private bool _acceptanceLedgerRequired;
    private long _configurationRevision;
    private bool _legacyModelSuppressed;
    private AdmissionUtility? _legacyWithdrawalUtility;
    private Guid[] _legacyWithdrawalObservations = [];
    private volatile bool _independentAdmissionEnabled;
    private volatile bool _independentAdmissionDisposed;
    private AdmissionDecision? _lastAdmission;

    private enum HistoricalRestoration
    {
        NotSelected,
        SelectedRejected,
        Restored
    }

    /// <summary>Gets the last independent assessment, without confusing a failed challenger with an active model.</summary>
    internal AdmissionDecision? LastAdmission
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastAdmission;
            }
        }
    }

    /// <summary>Supplies the current globally accepted immutable predictor to future personal challengers.</summary>
    /// <param name="provider">The global predictor provider, never a per-user fitting callback.</param>
    internal void SetSharedPredictionProvider(Func<NeuralScoringStrategy.PredictionSnapshot?> provider) => _sharedPredictionProvider = provider;

    /// <summary>Returns labels previously used to choose the global seed, never exposing the mutable ledger.</summary>
    /// <returns>A defensive acceptance-identity copy.</returns>
    internal Guid[]? GetAcceptedObservationIds()
    {
        lock (_syncRoot)
        {
            try
            {
                _spentAcceptanceObservations.UnionWith(new AcceptedModelStore(_statePath, _logger).ReadSpentAcceptanceIds(_acceptedBundle, _acceptanceLedgerRequired));
                return _spentAcceptanceObservations.ToArray();
            }
            catch (Exception exception) when (!exception.IsFatal())
            {
                _logger?.LogWarning(exception, "Acceptance reuse ledger unavailable; retaining incumbent and skipping admission");
                _lastAdmission = AdmissionDecision.Unknown("AcceptanceLedgerUnavailable", 0, 0);
                SaveAssessment(DateTime.UtcNow);
                return null;
            }
        }
    }

    /// <summary>Excludes a global seed's already-used acceptance labels from a personal promotion.</summary>
    /// <param name="observations">The seed selection's observation identities.</param>
    internal void SetSeedAcceptanceObservations(IReadOnlyCollection<Guid> observations) => _seedAcceptanceObservations = observations.ToHashSet();

    /// <summary>Returns the globally accepted prediction coordinates, not a rejected mutable training refresh.</summary>
    /// <returns>The current immutable Neural predictor.</returns>
    internal NeuralScoringStrategy.PredictionSnapshot? GetAcceptedNeuralPrediction()
    {
        lock (_syncRoot)
        {
            return _acceptedPrediction?.Neural ?? (_neural?.HasTrainedWeights == true ? _neural.CapturePredictionSnapshot() : null);
        }
    }

    /// <summary>Captures immutable evaluation bounds and their internal revision, without exposing a tuning control.</summary>
    /// <returns>The coherent configuration used by every prediction in an evaluation.</returns>
    internal EvaluationConfiguration CaptureEvaluationConfiguration()
    {
        lock (_syncRoot)
        {
            return new EvaluationConfiguration(_configurationRevision, _alphaMax, _genrePenaltyFloor);
        }
    }

    /// <summary>Fits detached candidates, selects coefficients on calibration and commits only independently useful versions.</summary>
    /// <param name="examples">Permanent fitting partition, excluding every calibration/acceptance title.</param>
    /// <param name="holdout">Authoritative permanent holdout, including empty/unknown evidence.</param>
    /// <param name="trainNeural">Whether this scope owns global Neural fitting.</param>
    /// <param name="cancellationToken">Cancellation checked before the atomic commit point.</param>
    /// <returns>Whether fitting was performed; approval and retention are reported separately.</returns>
    internal bool TrainValidated(IReadOnlyList<TrainingExample> examples, IReadOnlyList<TrainingExample> holdout, bool trainNeural, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(examples);
        ArgumentNullException.ThrowIfNull(holdout);
        cancellationToken.ThrowIfCancellationRequested();
        if (_independentAdmissionDisposed || Interlocked.CompareExchange(ref _validationTraining, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            return TrainValidatedCore(examples, holdout, trainNeural, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _validationTraining, 0);
        }
    }

    private bool TrainValidatedCore(IReadOnlyList<TrainingExample> examples, IReadOnlyList<TrainingExample> holdout, bool trainNeural, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var fit = TrainingMath.FilterEffectiveExamples(examples, now, LearnedScoringStrategy.MinSampleWeight);
        if (fit.Select(row => row.ObservationId).Intersect(holdout.Select(row => row.ObservationId)).Any())
        {
            throw new ArgumentException("Fitting and permanent holdout must not share observation identities.", nameof(holdout));
        }

        var (calibration, acceptance) = SplitIndependentHoldout(holdout);
        var spent = GetAcceptedObservationIds();
        if (spent is null)
        {
            return false;
        }

        calibration = EnsembleAdmissionEvaluator.SelectEvaluationBlock(calibration);
        LearnedScoringStrategy challenger;
        NeuralScoringStrategy? neuralChallenger;
        double alphaCap;
        double neuralCap;
        double floor;
        EvaluationConfiguration configuration;
        AcceptedPrediction incumbent;
        lock (_syncRoot)
        {
            EnableIndependentAdmission();
            incumbent = _acceptedPrediction!;
            challenger = LearnedStrategy.CreateChallenger();
            neuralChallenger = trainNeural && _ownsNeural ? (_acceptedNeuralTrainer ?? _neural)?.CreateChallenger() : null;
            UpdateObservedMaturity(fit.Where(row => row.IsObserved && row.HasStableObservationId).Select(row => row.ObservationId).Distinct().ToArray());
            alphaCap = ComputeSigmoidAlpha(_trainingExampleCount, DefaultSigmoidMidpoint + _sigmoidMidpointOffset, _alphaMin, _alphaMax);
            neuralCap = alphaCap * NeuralMaxBetaFraction * Math.Clamp((_trainingExampleCount - NeuralActivationThreshold) / 100.0, 0, 1);
            floor = _genrePenaltyFloor;
            configuration = CaptureEvaluationConfiguration();
            SaveAssessment(now);
        }

        using (neuralChallenger)
        {
            if (!challenger.Train(fit, calibration))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var neuralFitted = neuralChallenger?.Train(fit, calibration) == true;
            cancellationToken.ThrowIfCancellationRequested();
            var neural = (neuralFitted ? neuralChallenger?.CapturePredictionSnapshot() : null) ?? _sharedPredictionProvider?.Invoke() ?? incumbent.Neural;
            var learned = challenger.CapturePredictionSnapshot();
            var used = spent.Concat(_seedAcceptanceObservations).ToHashSet();
            var freshAcceptance = EnsembleAdmissionEvaluator.SelectEvaluationBlock(acceptance.Where(row => !used.Contains(row.ObservationId)));
            var models = new AdmissionModels(
                new PenalizedPredictor(_heuristic, floor),
                new PenalizedPredictor(learned, floor),
                neural is null ? null : new PenalizedPredictor(neural, floor),
                incumbent.Mixture.Alpha > 0 ? new PinnedMixturePredictor(incumbent, configuration.Maximum, floor) : null);
            var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, freshAcceptance, models, now, alphaCap, neuralCap);
            lock (_syncRoot)
            {
                _lastAdmission = decision;
                RecordMetricsSnapshot(decision.Utility.CandidateLoss ?? double.NaN, fit.Count, decision.Utility);
                SaveAssessment(now);
            }

            var attempted = new AcceptedPrediction(_heuristic, learned, neural, decision.Candidate, neuralCap);
            var candidateCalibration = EnsembleAdmissionEvaluator.EvaluateFixedVersion(calibration, models.Heuristic, new PinnedMixturePredictor(attempted, configuration.Maximum, floor), models.Incumbent ?? models.Heuristic, now);
            var restoration = TryRestoreHistoricalModel(new RestorationContext(calibration, freshAcceptance, incumbent, floor, now, candidateCalibration?.CandidateLoss ?? double.PositiveInfinity, models.Learned, configuration), cancellationToken);
            if (restoration == HistoricalRestoration.Restored)
            {
                return true;
            }

            if (!decision.Accepted || restoration == HistoricalRestoration.SelectedRejected)
            {
                var withdrawal = models.Incumbent is null ? null : EnsembleAdmissionEvaluator.EvaluateWithdrawal(freshAcceptance, models.Heuristic, models.Incumbent, now);
                if (withdrawal is not null)
                {
                    CommitWithdrawal(incumbent, withdrawal, freshAcceptance, configuration, cancellationToken);
                }

                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A useful linear refresh is not permission to publish an unvalidated Neural refresh.
            var acceptedNeural = decision.Candidate.Neural > 0 ? neural : incumbent.Neural;
            var acceptedNeuralTrainer = decision.Candidate.Neural > 0 && neuralFitted ? neuralChallenger : null;
            return CommitAcceptedChallenger(new TrainedCandidates(challenger, acceptedNeuralTrainer, learned, acceptedNeural), decision, freshAcceptance, new PromotionBounds(alphaCap, neuralCap, floor, configuration), cancellationToken);
        }
    }

    private bool CommitAcceptedChallenger(TrainedCandidates candidates, AdmissionDecision decision, IReadOnlyList<TrainingExample> acceptance, PromotionBounds bounds, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfigurationIsCurrent(bounds.Configuration))
            {
                RejectChangedEvaluationConfiguration();
                return true;
            }

            if (_independentAdmissionDisposed || bounds.Floor != _genrePenaltyFloor || bounds.AlphaCap > _alphaMax)
            {
                return true;
            }

            var ids = acceptance.Where(row => row.IsObserved && row.HasStableObservationId
                && row.ObservationId != Guid.Empty).Select(row => row.ObservationId).Distinct().ToArray();

            var now = DateTime.UtcNow;
            var bundle = new AcceptedModelBundle
            {
                Learned = candidates.Learned.ExportParameters(),
                Neural = candidates.Neural?.ExportParameters(),
                Mixture = decision.Candidate,
                AlphaCap = bounds.AlphaCap,
                NeuralCap = bounds.NeuralCap,
                Revision = (_acceptedBundle?.Revision ?? 0) + 1,
                TrainedAtUtc = now,
                AcceptanceObservationIds = ids,
                Utility = decision.Utility,
                State = new EnsembleStateData
                {
                    ObservedTrainingIds = _observedTrainingIds.ToArray(),
                    TrainingExampleCount = _trainingExampleCount,
                    Alpha = bounds.AlphaCap,
                    NeuralBeta = decision.Candidate.Beta,
                    QualityGateFrozen = false,
                    SigmoidMidpointOffset = _sigmoidMidpointOffset,
                    MetricsHistory = _metricsHistory.ToList(),
                    UpdatedAt = now.ToString("O", CultureInfo.InvariantCulture)
                }
            };
            if (!new AcceptedModelStore(_statePath, _logger).Commit(bundle))
            {
                _lastAdmission = decision with { Accepted = false, Reason = "CheckpointCommitFailed" };
                SaveAssessment(DateTime.UtcNow);
                return true;
            }

            // After the atomic disk commit, finish publication even if cancellation arrived meanwhile.
            _acceptedPrediction = new AcceptedPrediction(_heuristic, candidates.Learned, candidates.Neural, decision.Candidate, bounds.NeuralCap);
            _acceptedBundle = bundle;
            _legacyModelSuppressed = false;
            _legacyWithdrawalUtility = null;
            _legacyWithdrawalObservations = [];
            _acceptanceLedgerRequired = bundle.AcceptanceLedgerRequired;
            _spentAcceptanceObservations.UnionWith(ids);
            _acceptedLearnedTrainer = candidates.LearnedTrainer;
            _qualityGateFrozen = false;
            _alpha = bounds.AlphaCap;
            _neuralBeta = decision.Candidate.Beta;
            // Capture a continuing optimizer independently: the caller disposes its temporary challenger.
            if (_ownsNeural && candidates.NeuralTrainer is not null)
            {
                var previous = _acceptedNeuralTrainer;
                _acceptedNeuralTrainer = candidates.NeuralTrainer.CreateChallenger();
                previous?.Dispose();
            }

            SaveAssessment(DateTime.UtcNow);
            return true;
        }
    }

    private void EnableIndependentAdmission()
    {
        if (_independentAdmissionEnabled)
        {
            return;
        }

        var alpha = _legacyModelSuppressed || _qualityGateFrozen || !LearnedStrategy.HasOwnWeights ? 0 : _alpha;
        var beta = _neural?.HasTrainedWeights == true ? _neuralBeta : 0;
        _acceptedPrediction = new AcceptedPrediction(_heuristic, LearnedStrategy.CapturePredictionSnapshot(), _neural?.HasTrainedWeights == true ? _neural.CapturePredictionSnapshot() : null, new AdmissionMixture(alpha * (1 - beta), alpha * beta), _alphaMax * NeuralMaxBetaFraction);
        _independentAdmissionEnabled = true;
        _qualityGateFrozen = alpha <= 0;
    }

    private void CommitWithdrawal(AcceptedPrediction incumbent, AdmissionUtility evidence, IReadOnlyList<TrainingExample> acceptance, EvaluationConfiguration configuration, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfigurationIsCurrent(configuration))
            {
                RejectChangedEvaluationConfiguration();
                return;
            }

            var previous = _acceptedBundle;
            if (_independentAdmissionDisposed || previous?.Retired == true)
            {
                return;
            }

            if (previous is null)
            {
                CommitLegacyWithdrawal(incumbent, evidence, acceptance);
                return;
            }

            var ids = acceptance.Where(row => row.IsObserved && row.HasStableObservationId).Select(row => row.ObservationId).Distinct().ToArray();

            var retired = new AcceptedModelBundle
            {
                Learned = previous.Learned,
                Neural = previous.Neural,
                Mixture = previous.Mixture,
                AlphaCap = previous.AlphaCap,
                NeuralCap = previous.NeuralCap,
                Revision = previous.Revision + 1,
                TrainedAtUtc = previous.TrainedAtUtc,
                AcceptanceObservationIds = ids,
                Utility = previous.Utility,
                Retired = true,
                RetirementUtility = evidence,
                State = new EnsembleStateData
                {
                    Alpha = previous.State.Alpha,
                    NeuralBeta = previous.State.NeuralBeta,
                    QualityGateFrozen = true,
                    TrainingExampleCount = _trainingExampleCount,
                    ObservedTrainingIds = _observedTrainingIds.ToArray(),
                    SigmoidMidpointOffset = _sigmoidMidpointOffset,
                    UpdatedAt = previous.State.UpdatedAt,
                    MetricsHistory = _metricsHistory.ToList()
                }
            };
            if (!new AcceptedModelStore(_statePath, _logger).Commit(retired))
            {
                _lastAdmission = _lastAdmission! with { Accepted = false, Reason = "CheckpointCommitFailed" };
                SaveAssessment(DateTime.UtcNow);
                return;
            }

            _acceptedBundle = retired;
            _acceptanceLedgerRequired = retired.AcceptanceLedgerRequired;
            _spentAcceptanceObservations.UnionWith(ids);
            _acceptedPrediction = new AcceptedPrediction(_heuristic, incumbent.Learned, incumbent.Neural, default, incumbent.NeuralCap);
            _qualityGateFrozen = true;
            _neuralBeta = 0;
            _lastAdmission = new AdmissionDecision(false, "IncumbentWithdrawnOnNewEvidence", default, _lastAdmission!.Evidence, evidence);
            SaveAssessment(DateTime.UtcNow);
        }
    }

    private void CommitLegacyWithdrawal(AcceptedPrediction incumbent, AdmissionUtility evidence, IReadOnlyList<TrainingExample> acceptance)
    {
        var ids = acceptance.Where(row => row.IsObserved && row.HasStableObservationId && row.ObservationId != Guid.Empty)
            .Select(row => row.ObservationId).Distinct().ToArray();
        var decision = new AdmissionDecision(false, "LegacyModelWithdrawnOnNewEvidence", default, _lastAdmission!.Evidence, evidence);
        var assessment = CreateAssessment(DateTime.UtcNow);
        assessment.LegacyModelSuppressed = true;
        assessment.HeuristicFallbackRequired = true;
        assessment.LegacyWithdrawalUtility = evidence;
        assessment.LegacyWithdrawalObservationIds = ids;
        assessment.Decision = decision;
        assessment.AcceptanceLedgerRequired = _statePath is not null;
        if (!ValidLegacySuppression(assessment))
        {
            return;
        }

        try
        {
            new AcceptanceObservationLedger(AcceptedModelStore.GetLedgerPath(_statePath), (path, text) => AtomicFile.WriteAllText(path, text))
                .Spend(ids, _spentAcceptanceObservations, _acceptanceLedgerRequired);
            if (!WriteAssessment(assessment))
            {
                _lastAdmission = decision with { Reason = "CheckpointCommitFailed" };
                return;
            }
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            _logger?.LogWarning(exception, "Legacy suppression could not be committed; legacy weights remain untouched");
            _lastAdmission = decision with { Reason = "CheckpointCommitFailed" };
            return;
        }

        _legacyModelSuppressed = true;
        _legacyWithdrawalUtility = evidence;
        _legacyWithdrawalObservations = ids;
        _acceptanceLedgerRequired = assessment.AcceptanceLedgerRequired;
        _spentAcceptanceObservations.UnionWith(ids);
        _acceptedPrediction = new AcceptedPrediction(_heuristic, incumbent.Learned, incumbent.Neural, default, incumbent.NeuralCap);
        _qualityGateFrozen = true;
        _neuralBeta = 0;
        _lastAdmission = decision;
    }

    private static bool ValidLegacySuppression(ModelTrainingAssessment assessment)
        => assessment.LegacyModelSuppressed && assessment.LegacyWithdrawalObservationIds is { Length: >= EnsembleAdmissionEvaluator.MinimumEffectiveTitles }
            && assessment.LegacyWithdrawalObservationIds.All(id => id != Guid.Empty)
            && assessment.LegacyWithdrawalObservationIds.Distinct().Count() == assessment.LegacyWithdrawalObservationIds.Length
            && assessment.LegacyWithdrawalUtility is { LowerIncumbentImprovement: > 0, CandidateLoss: >= 0, CandidateNdcg: >= 0 and <= 1 } utility
            && double.IsFinite(utility.LowerIncumbentImprovement.Value) && double.IsFinite(utility.CandidateLoss.Value) && double.IsFinite(utility.CandidateNdcg.Value);

    private HistoricalRestoration TryRestoreHistoricalModel(RestorationContext context, CancellationToken cancellationToken)
    {
        var store = new AcceptedModelStore(_statePath, _logger);
        var historical = store.ReadHistory();
        var heuristic = new PenalizedPredictor(_heuristic, context.Floor);
        var current = new PinnedMixturePredictor(context.Incumbent, context.Configuration.Maximum, context.Floor);
        HistoricalCandidate? selected = null;
        foreach (var bundle in historical)
        {
            var learned = new LearnedScoringStrategy.PredictionSnapshot(bundle.Learned);
            var neural = bundle.Neural is null ? null : new NeuralScoringStrategy.PredictionSnapshot(bundle.Neural);
            var prediction = new AcceptedPrediction(_heuristic, learned, neural, bundle.Mixture, bundle.NeuralCap);
            var metrics = EnsembleAdmissionEvaluator.EvaluateFixedVersion(context.Calibration, heuristic, new PinnedMixturePredictor(prediction, context.Configuration.Maximum, context.Floor), current, context.ReferenceTime);
            IScoringStrategy? noNeural = null;
            if (bundle.Mixture.Neural > 0)
            {
                noNeural = EnsembleAdmissionEvaluator.CalibrateNoNeuralReference(context.Calibration, heuristic, [context.LearnedChallenger, new PenalizedPredictor(context.Incumbent.Learned, context.Floor), new PenalizedPredictor(learned, context.Floor)], context.ReferenceTime, Math.Min(bundle.AlphaCap, context.Configuration.Maximum));
                if (noNeural is null || EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(context.Calibration, heuristic, new PinnedMixturePredictor(prediction, context.Configuration.Maximum, context.Floor), noNeural, context.ReferenceTime) is null)
                {
                    continue;
                }
            }

            if (metrics is not null && metrics.CandidateLoss < context.CandidateCalibrationLoss && (selected is null || metrics.CandidateLoss < selected.CalibrationLoss))
            {
                selected = new HistoricalCandidate(bundle, prediction, metrics.CandidateLoss!.Value, noNeural);
            }
        }

        if (selected is null)
        {
            return HistoricalRestoration.NotSelected;
        }

        var evidence = EnsembleAdmissionEvaluator.EvaluateFixedVersion(context.Acceptance, heuristic, new PinnedMixturePredictor(selected.Prediction, context.Configuration.Maximum, context.Floor), current, context.ReferenceTime);
        var neuralEvidence = selected.NoNeuralReference is null ? null : EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(context.Acceptance, heuristic, new PinnedMixturePredictor(selected.Prediction, context.Configuration.Maximum, context.Floor), selected.NoNeuralReference, context.ReferenceTime);
        if (evidence?.LowerHeuristicImprovement is not > 0 || evidence.LowerIncumbentImprovement is not > 0
            || (selected.Prediction.Mixture.Neural > 0 && neuralEvidence is null))
        {
            return HistoricalRestoration.SelectedRejected;
        }

        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfigurationIsCurrent(context.Configuration))
            {
                RejectChangedEvaluationConfiguration();
                return HistoricalRestoration.SelectedRejected;
            }

            if (_independentAdmissionDisposed || _genrePenaltyFloor != context.Floor)
            {
                return HistoricalRestoration.SelectedRejected;
            }

            var original = selected.Bundle;
            var ids = context.Acceptance.Where(row => row.IsObserved && row.HasStableObservationId).Select(row => row.ObservationId).Distinct().ToArray();

            var restored = new AcceptedModelBundle
            {
                Learned = original.Learned,
                Neural = original.Neural,
                Mixture = original.Mixture,
                AlphaCap = original.AlphaCap,
                NeuralCap = original.NeuralCap,
                Revision = (_acceptedBundle?.Revision ?? 0) + 1,
                TrainedAtUtc = original.TrainedAtUtc,
                AcceptanceObservationIds = ids,
                Utility = evidence with { LowerNeuralImprovement = neuralEvidence },
                State = new EnsembleStateData
                {
                    Alpha = original.AlphaCap,
                    NeuralBeta = original.Mixture.Beta,
                    QualityGateFrozen = false,
                    ObservedTrainingIds = _observedTrainingIds.ToArray(),
                    TrainingExampleCount = _trainingExampleCount,
                    UpdatedAt = original.State.UpdatedAt,
                    SigmoidMidpointOffset = _sigmoidMidpointOffset,
                    MetricsHistory = _metricsHistory.ToList()
                }
            };
            if (!store.Commit(restored))
            {
                _lastAdmission = _lastAdmission! with { Accepted = false, Reason = "CheckpointCommitFailed" };
                SaveAssessment(DateTime.UtcNow);
                return HistoricalRestoration.SelectedRejected;
            }

            _acceptedBundle = restored;
            _acceptanceLedgerRequired = restored.AcceptanceLedgerRequired;
            _spentAcceptanceObservations.UnionWith(ids);
            _acceptedPrediction = selected.Prediction;
            _acceptedLearnedTrainer = LearnedScoringStrategy.FromParameters(restored.Learned);
            if (_ownsNeural)
            {
                var previous = _acceptedNeuralTrainer;
                _acceptedNeuralTrainer = restored.Neural is null ? null : NeuralScoringStrategy.FromParameters(restored.Neural);
                previous?.Dispose();
            }

            _alpha = restored.AlphaCap;
            _neuralBeta = restored.Mixture.Beta;
            _qualityGateFrozen = false;
            _lastAdmission = new AdmissionDecision(true, "HistoricalModelRestoredOnNewEvidence", original.Mixture, _lastAdmission!.Evidence, evidence);
            SaveAssessment(DateTime.UtcNow);
            return HistoricalRestoration.Restored;
        }
    }

    private bool ConfigurationIsCurrent(EvaluationConfiguration configuration)
        => configuration.Revision == _configurationRevision && configuration.Maximum == _alphaMax && configuration.Floor == _genrePenaltyFloor;

    private void RejectChangedEvaluationConfiguration()
    {
        _lastAdmission = (_lastAdmission ?? AdmissionDecision.Unknown("EvaluationConfigurationChanged", 0, 0)) with { Accepted = false, Reason = "EvaluationConfigurationChanged" };
        SaveAssessment(DateTime.UtcNow);
    }

    private void TryLoadAcceptedModel()
    {
        var bundle = new AcceptedModelStore(_statePath, _logger).Read();
        if (bundle is null)
        {
            TryLoadAssessment();
            return;
        }

        var learned = LearnedScoringStrategy.FromParameters(bundle.Learned);
        if (learned is null)
        {
            TryLoadAssessment();
            return;
        }

        _acceptedLearnedTrainer = learned;
        _acceptedNeuralTrainer = _ownsNeural && bundle.Neural is not null ? NeuralScoringStrategy.FromParameters(bundle.Neural) : null;
        var neural = bundle.Neural is null ? null : new NeuralScoringStrategy.PredictionSnapshot(bundle.Neural);
        _acceptedPrediction = new AcceptedPrediction(_heuristic, learned.CapturePredictionSnapshot(), neural, bundle.Retired ? default : bundle.Mixture, bundle.NeuralCap);
        _acceptedBundle = bundle;
        _acceptanceLedgerRequired = bundle.AcceptanceLedgerRequired;
        _independentAdmissionEnabled = true;
        ApplyLoadedState(bundle.State);
        _alpha = bundle.AlphaCap;
        _neuralBeta = bundle.Retired ? 0 : bundle.Mixture.Beta;
        _qualityGateFrozen = bundle.Retired;
        TryLoadAssessment();
    }

    private ModelTrainingAssessment CreateAssessment(DateTime evaluatedAt)
        => new()
        {
            EvaluatedAtUtc = evaluatedAt,
            ObservedTrainingIds = _observedTrainingIds.ToArray(),
            TrainingExampleCount = _trainingExampleCount,
            Decision = _lastAdmission,
            IndependentAdmissionEnabled = _independentAdmissionEnabled,
            HeuristicFallbackRequired = _acceptedBundle is null && _acceptedPrediction?.Mixture.Alpha <= 0,
            AcceptanceLedgerRequired = _acceptanceLedgerRequired,
            AcceptedModelRevision = _acceptedBundle?.Revision ?? 0,
            LegacyModelSuppressed = _legacyModelSuppressed,
            LegacyWithdrawalUtility = _legacyWithdrawalUtility,
            LegacyWithdrawalObservationIds = _legacyWithdrawalObservations,
            MetricsHistory = _metricsHistory.ToList()
        };

    private void SaveAssessment(DateTime evaluatedAt) => WriteAssessment(CreateAssessment(evaluatedAt));

    private bool WriteAssessment(ModelTrainingAssessment assessment)
    {
        if (_statePath is null)
        {
            return true;
        }

        try
        {
            AtomicFile.WriteAllText(_statePath + ".assessment.json", JsonSerializer.Serialize(assessment, AssessmentJsonOptions));
            return true;
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            _logger?.LogWarning(exception, "Model attempt diagnostics could not be saved; accepted weights remain unchanged");
            return false;
        }
    }

    private void TryLoadAssessment()
    {
        if (_statePath is null || !File.Exists(_statePath + ".assessment.json"))
        {
            if (_statePath is not null && (File.Exists(AcceptedModelStore.GetLedgerPath(_statePath)) || Directory.Exists(_statePath + ".assessment.json")))
            {
                RetainUntrustedLegacyFallback("MissingAdmissionAssessment");
            }

            return;
        }

        try
        {
            var path = _statePath + ".assessment.json";
            if (new FileInfo(path).Length > 1024 * 1024)
            {
                RetainUntrustedLegacyFallback("UnreadableAdmissionAssessment");
                return;
            }

            var assessment = JsonSerializer.Deserialize<ModelTrainingAssessment>(File.ReadAllText(path), AssessmentJsonOptions);
            if (assessment?.Version != 1 || assessment.ObservedTrainingIds is null)
            {
                RetainUntrustedLegacyFallback("UnreadableAdmissionAssessment");
                return;
            }

            UpdateObservedMaturity(assessment.ObservedTrainingIds.Where(id => id != Guid.Empty).Take(MaxTrackedObservations).ToArray());
            if (_acceptedBundle is not null && assessment.AcceptedModelRevision < _acceptedBundle.Revision)
            {
                return;
            }

            _lastAdmission = assessment.Decision;
            _acceptanceLedgerRequired |= assessment.AcceptanceLedgerRequired;
            if (_acceptedBundle is null && assessment.HeuristicFallbackRequired)
            {
                _qualityGateFrozen = true;
            }

            if (assessment.LegacyModelSuppressed && !ValidLegacySuppression(assessment))
            {
                RetainUntrustedLegacyFallback("InvalidLegacySuppressionAssessment");
                return;
            }

            if (_acceptedBundle is null && ValidLegacySuppression(assessment))
            {
                _legacyModelSuppressed = true;
                _legacyWithdrawalUtility = assessment.LegacyWithdrawalUtility;
                _legacyWithdrawalObservations = assessment.LegacyWithdrawalObservationIds;
            }

            if (assessment.IndependentAdmissionEnabled || assessment.Decision is not null)
            {
                EnableIndependentAdmission();
            }

            if (assessment.MetricsHistory is { Count: > 0 })
            {
                _metricsHistory.Clear();
                _metricsHistory.AddRange(assessment.MetricsHistory.TakeLast(10));
            }

            // Assessment records deliberately cannot grant model trust or restore any coefficients.
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            _logger?.LogWarning(exception, "Model attempt diagnostics could not be read; using model checkpoint state only");
            RetainUntrustedLegacyFallback("UnreadableAdmissionAssessment");
        }
    }

    private void RetainUntrustedLegacyFallback(string reason)
    {
        if (_acceptedBundle is not null)
        {
            return;
        }

        // An unreadable/missing suppression journal cannot grant old v5 weights influence again.
        // Keep the parameter checkpoint intact; a genuinely useful fresh candidate can still be admitted.
        _qualityGateFrozen = true;
        EnableIndependentAdmission();
        var previous = _acceptedPrediction!;
        _acceptedPrediction = new AcceptedPrediction(_heuristic, previous.Learned, previous.Neural, default, previous.NeuralCap);
        _neuralBeta = 0;
        _acceptanceLedgerRequired = _statePath is not null;
        _lastAdmission = AdmissionDecision.Unknown(reason, 0, 0);
    }

    private bool TryScoreAccepted(CandidateFeatures features, out double score)
    {
        AcceptedPrediction? prediction;
        double maximum;
        double floor;
        lock (_syncRoot)
        {
            prediction = _acceptedPrediction;
            maximum = _alphaMax;
            floor = _genrePenaltyFloor;
        }

        score = prediction?.Score(features, maximum, floor) ?? 0;
        return prediction is not null;
    }

    /// <summary>Supplies the accepted explanation scorer; callers use it for the full score and every removal.</summary>
    /// <returns>The explanation scorer, or null for the legacy explanation path.</returns>
    internal Func<CandidateFeatures, double>? CaptureAcceptedExplanationScore()
    {
        lock (_syncRoot)
        {
            var prediction = _acceptedPrediction;
            var maximum = _alphaMax;
            var floor = _genrePenaltyFloor;
            return prediction is null ? null : features => prediction.Score(features, maximum, floor);
        }
    }

    private static (TrainingExample[] Calibration, TrainingExample[] Acceptance) SplitIndependentHoldout(IReadOnlyList<TrainingExample> holdout)
    {
        var calibration = new List<TrainingExample>();
        var acceptance = new List<TrainingExample>();
        foreach (var row in holdout)
        {
            var hash = SHA256.HashData(row.ObservationId.ToByteArray());
            ((hash[0] & 1) == 0 ? calibration : acceptance).Add(row);
        }

        return (calibration.ToArray(), acceptance.ToArray());
    }

    /// <summary>One revision of effective configuration; a commit must not reuse evidence from another revision.</summary>
    /// <param name="Revision">Internal configuration generation.</param>
    /// <param name="Maximum">Captured absolute ML ceiling.</param>
    /// <param name="Floor">Captured final genre-penalty floor.</param>
    internal readonly record struct EvaluationConfiguration(long Revision, double Maximum, double Floor);

    private readonly record struct PromotionBounds(double AlphaCap, double NeuralCap, double Floor, EvaluationConfiguration Configuration);

    private readonly record struct RestorationContext(IReadOnlyList<TrainingExample> Calibration, IReadOnlyList<TrainingExample> Acceptance, AcceptedPrediction Incumbent, double Floor, DateTime ReferenceTime, double CandidateCalibrationLoss, IScoringStrategy LearnedChallenger, EvaluationConfiguration Configuration);

    private readonly record struct TrainedCandidates(LearnedScoringStrategy LearnedTrainer, NeuralScoringStrategy? NeuralTrainer, LearnedScoringStrategy.PredictionSnapshot Learned, NeuralScoringStrategy.PredictionSnapshot? Neural);

    private sealed record HistoricalCandidate(AcceptedModelBundle Bundle, AcceptedPrediction Prediction, double CalibrationLoss, IScoringStrategy? NoNeuralReference);

    private sealed class PenalizedPredictor(IScoringStrategy source, double floor) : IScoringStrategy
    {
        public string Name => source.Name;

        public string NameKey => source.NameKey;

        public double Score(CandidateFeatures features) => source.Score(features) * ScoringHelper.ComputeGenrePenalty(features, floor);

        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => ScoringHelper.BuildMarginalExplanation(features, Score, Name);
    }

    private sealed class PinnedMixturePredictor(AcceptedPrediction source, double maximum, double floor) : IScoringStrategy
    {
        public string Name => "Pinned incumbent";

        public string NameKey => "strategyEnsemble";

        public double Score(CandidateFeatures features) => source.Score(features, maximum, floor);

        public ScoreExplanation ScoreWithExplanation(CandidateFeatures features) => ScoringHelper.BuildMarginalExplanation(features, Score, Name);
    }
}
