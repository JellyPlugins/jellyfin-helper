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
    private const string CheckpointCommitFailure = "CheckpointCommitFailed";
    private const string AssessmentSuffix = ".assessment.json";
    private const int MaximumReservedOperatingInputs = 256;
    private static readonly JsonSerializerOptions AssessmentJsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly HashSet<Guid> _spentAcceptanceObservations = [];
    private readonly HashSet<string> _reservedOperatingInputs = new(StringComparer.Ordinal);
    private int _validationTraining;
    private AcceptedPrediction? _acceptedPrediction;
    private LearnedScoringStrategy? _acceptedLearnedTrainer;
    private NeuralScoringStrategy? _acceptedNeuralTrainer;
    private AcceptedModelBundle? _acceptedBundle;
    private Func<NeuralScoringStrategy.PredictionSnapshot?>? _sharedPredictionProvider;
    private NeuralTrainingPrior? _pinnedSharedNeuralPrior;
    private HashSet<Guid> _seedAcceptanceObservations = [];
    private bool _acceptanceLedgerRequired;
    private bool _usesInheritedPrior;
    private bool _requiresIndependentRecovery;
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

    /// <summary>Gets a value indicating whether this scope has its own fitted operating parameters rather than a global starting prior.</summary>
    internal bool HasOwnOperatingModel
    {
        get
        {
            lock (_syncRoot)
            {
                return !_usesInheritedPrior && (_acceptedBundle is not null || LearnedStrategy.HasOwnWeights);
            }
        }
    }

    /// <summary>Supplies the current globally accepted immutable predictor to future personal challengers.</summary>
    /// <param name="provider">The global predictor provider, never a per-user fitting callback.</param>
    internal void SetSharedPredictionProvider(Func<NeuralScoringStrategy.PredictionSnapshot?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_syncRoot)
        {
            _sharedPredictionProvider = provider;
            _pinnedSharedNeuralPrior = null;
        }
    }

    /// <summary>Pins a coordinator-owned Neural value, including explicit absence, before global promotion.</summary>
    /// <param name="prediction">The pre-cycle immutable predictor, or null for pinned absence.</param>
    internal void SetPinnedNeuralTrainingPrior(NeuralScoringStrategy.PredictionSnapshot? prediction)
    {
        lock (_syncRoot)
        {
            _pinnedSharedNeuralPrior = new NeuralTrainingPrior(prediction);
            _sharedPredictionProvider = null;
        }
    }

    /// <summary>Returns labels previously used to choose the global seed, never exposing the mutable ledger.</summary>
    /// <returns>A defensive acceptance-identity copy.</returns>
    internal Guid[]? GetAcceptedObservationIds()
        => GetAcceptedObservationIds((bundle, required) => new AcceptedModelStore(_statePath, _logger).ReadSpentAcceptanceIds(bundle, required));

    /// <summary>Reads the exact ledger with an injectable persistence boundary for deterministic concurrency tests.</summary>
    /// <param name="read">The ledger reader, never a scoring or fitting callback.</param>
    /// <returns>A defensive identity copy, or null on an integrity failure.</returns>
    internal Guid[]? GetAcceptedObservationIds(Func<AcceptedModelBundle?, bool, HashSet<Guid>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        while (true)
        {
            AcceptedModelBundle? bundle;
            bool required;
            int spentCount;
            lock (_syncRoot)
            {
                bundle = _acceptedBundle;
                required = _acceptanceLedgerRequired;
                spentCount = _spentAcceptanceObservations.Count;
            }

            HashSet<Guid>? observations = null;
            Exception? failure = null;
            try
            {
                observations = read(bundle, required);
            }
            catch (Exception exception) when (!exception.IsFatal())
            {
                failure = exception;
            }

            lock (_syncRoot)
            {
                // Bundle publication and the exact spent set are monotonic under this lock.
                if (!ReferenceEquals(bundle, _acceptedBundle) || required != _acceptanceLedgerRequired || spentCount != _spentAcceptanceObservations.Count)
                {
                    continue;
                }

                if (observations is not null)
                {
                    _spentAcceptanceObservations.UnionWith(observations);
                    return _spentAcceptanceObservations.ToArray();
                }

                _logger?.LogWarning(failure, "Acceptance reuse ledger unavailable; retaining incumbent and skipping admission");
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
            if (_independentAdmissionEnabled)
            {
                return !_qualityGateFrozen && _acceptedPrediction?.Resolve(_alphaMax).Neural > 0 ? _acceptedPrediction.Neural : null;
            }

            return !_qualityGateFrozen && _neuralBeta > 0 && _neural?.HasTrainedWeights == true ? _neural.CapturePredictionSnapshot() : null;
        }
    }

    /// <summary>Returns only a currently participating global predictor, not an inactive/rejected Neural refresh.</summary>
    /// <returns>An immutable operating prior or null when no fitted global model participates.</returns>
    internal AcceptedPrediction? GetOperatingPrior()
    {
        lock (_syncRoot)
        {
            if (!_independentAdmissionEnabled || _qualityGateFrozen || _acceptedPrediction is not { } prediction)
            {
                return null;
            }

            var mixture = prediction.Resolve(_alphaMax);
            if (mixture.Alpha <= 0)
            {
                return null;
            }

            var neural = mixture.Neural > 0 ? prediction.Neural : null;
            return mixture == prediction.Mixture && ReferenceEquals(neural, prediction.Neural)
                ? prediction
                : new AcceptedPrediction(_heuristic, prediction.Learned, neural, mixture, prediction.NeuralCap);
        }
    }

    /// <summary>Initializes a user's own bounded mixture from global coordinates without fitting or writing files.</summary>
    /// <param name="prior">The immutable currently participating global predictor.</param>
    internal void InitializeOperatingPrior(AcceptedPrediction? prior)
    {
        lock (_syncRoot)
        {
            if (_independentAdmissionDisposed || _acceptedBundle is not null || _legacyModelSuppressed || _requiresIndependentRecovery)
            {
                return;
            }

            if (prior is null)
            {
                if (_usesInheritedPrior && _acceptedPrediction is { } previous)
                {
                    _acceptedPrediction = new AcceptedPrediction(_heuristic, previous.Learned, null, default, 0);
                    _qualityGateFrozen = true;
                    _neuralBeta = 0;
                }

                return;
            }

            var maturity = _trainingExampleCount == 0 ? _alphaMin : OperatingMixturePolicy.Maturity(_trainingExampleCount, DefaultSigmoidMidpoint + _sigmoidMidpointOffset, _alphaMin, _alphaMax);
            var alpha = Math.Min(prior.Mixture.Alpha, maturity);
            var current = _usesInheritedPrior && _acceptedPrediction?.Mixture.Alpha > 0 ? _acceptedPrediction.Mixture : prior.Resolve(alpha);
            var neural = prior.Mixture.Neural > 0 ? prior.Neural : null;
            if (neural is null)
            {
                current = new AdmissionMixture(current.Alpha, 0);
            }
            else if (current.Neural <= 0)
            {
                current = prior.Resolve(Math.Min(alpha, current.Alpha));
            }

            if (prior.Mixture.Learned <= 0)
            {
                current = new AdmissionMixture(0, current.Neural);
            }

            var limited = new AcceptedPrediction(_heuristic, prior.Learned, neural, current, prior.NeuralCap);
            var mixture = limited.Resolve(alpha);
            _acceptedPrediction = new AcceptedPrediction(_heuristic, prior.Learned, neural, mixture, _alphaMax * NeuralMaxBetaFraction);
            _usesInheritedPrior = true;
            _independentAdmissionEnabled = true;
            _qualityGateFrozen = mixture.Alpha <= 0;
            _alpha = mixture.Alpha;
            _neuralBeta = mixture.Beta;
        }
    }

    /// <summary>Pins a new personal training seed before the global cycle; serving-prior refreshes never mutate it.</summary>
    /// <param name="prior">The training coordinator's pre-cycle Learned coordinates.</param>
    internal void InitializeTrainingPrior(LearnedScoringStrategy prior)
    {
        lock (_syncRoot)
        {
            if (!_independentAdmissionDisposed && _acceptedBundle is null && !_learned.HasOwnWeights)
            {
                _learned.SeedFrom(prior);
            }
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
        var used = spent.Concat(_seedAcceptanceObservations).ToHashSet();
        var freshAcceptance = EnsembleAdmissionEvaluator.SelectEvaluationBlock(acceptance.Where(row => !used.Contains(row.ObservationId)));
        if (TrySkipUnchangedOperatingInputs(fit, calibration, freshAcceptance, now))
        {
            return false;
        }

        using (var neuralChallenger = CreateNeuralChallenger(trainNeural))
        {
            var context = PrepareChallengers(fit, neuralChallenger, now);
            if (!context.Learned.Train(fit, calibration))
            {
                return false;
            }

            var candidates = CaptureFittedCandidates(context, fit, calibration, cancellationToken);
            var models = BuildAdmissionModels(context, candidates);
            var inputDigest = ComputeOperatingInputDigest(fit, calibration, context.Bounds, _ownsNeural ? null : candidates.Neural);
            var reservedThisCycle = new HashSet<Guid>();
            string? Reserve(IReadOnlyCollection<Guid> ids) => ReserveAcceptanceEvidence(ids, context.Bounds.Configuration, reservedThisCycle, inputDigest);
            var examined = new ExaminedAcceptance(Reserve);
            var decision = EnsembleAdmissionEvaluator.Evaluate(calibration, freshAcceptance, models, now, context.Bounds.AlphaCap, context.Bounds.NeuralCap, examined.Reserve);
            RecordAdmissionAttempt(decision, fit.Count, now);

            var batch = new ValidationBatch(calibration, freshAcceptance, models, now) { ReserveEvidence = Reserve };
            var restoration = TryRestoreAgainstChallenger(context, candidates, decision, batch, cancellationToken);
            if (restoration == HistoricalRestoration.Restored)
            {
                return true;
            }

            if (!decision.Accepted || restoration == HistoricalRestoration.SelectedRejected)
            {
                TryWithdrawOnFreshEvidence(context, batch, cancellationToken);
                if (CanRebalanceProvisionalServing(decision) && TryRebalanceProvisionalServing(context, candidates, batch, fit, cancellationToken))
                {
                    return true;
                }

                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A useful linear refresh is not permission to publish an unvalidated Neural refresh.
            var accepted = candidates;
            if (decision.Candidate.Neural <= 0)
            {
                accepted = candidates with { NeuralTrainer = null, Neural = context.Incumbent.Neural };
            }

            return CommitAcceptedChallenger(accepted, decision, examined.Observations, context.Bounds with { InputDigest = inputDigest }, cancellationToken);
        }
    }

    private bool TrySkipUnchangedOperatingInputs(IReadOnlyList<TrainingExample> fit, TrainingExample[] calibration, TrainingExample[] acceptance, DateTime referenceTime)
    {
        if (EnsembleAdmissionEvaluator.HasIndependentAcceptanceSupport(acceptance, referenceTime))
        {
            return false;
        }

        PromotionBounds bounds;
        AcceptedModelBundle? bundle;
        NeuralTrainingPrior? pinned;
        lock (_syncRoot)
        {
            // Dynamic expert/test providers must run at the ordinary preparation boundary exactly once.
            if (_sharedPredictionProvider is not null || (!_ownsNeural && _pinnedSharedNeuralPrior is null)
                || (_acceptedBundle?.OperatingInputDigest is null && _reservedOperatingInputs.Count == 0))
            {
                return false;
            }

            UpdateObservedMaturity(fit.Where(row => row.IsObserved && row.HasStableObservationId).Select(row => row.ObservationId).Distinct().ToArray());
            var alpha = OperatingMixturePolicy.Maturity(_trainingExampleCount, DefaultSigmoidMidpoint + _sigmoidMidpointOffset, _alphaMin, _alphaMax);
            var configuration = CaptureEvaluationConfiguration();
            bounds = new PromotionBounds(alpha, alpha * OperatingMixturePolicy.NeuralTarget(_trainingExampleCount, _ownsNeural, available: true), configuration.Floor, configuration, _alphaMin, _trainingExampleCount);
            bundle = _acceptedBundle;
            pinned = _pinnedSharedNeuralPrior;
        }

        var digest = ComputeOperatingInputDigest(fit, calibration, bounds, _ownsNeural ? null : pinned?.Prediction);
        lock (_syncRoot)
        {
            if (!ConfigurationIsCurrent(bounds.Configuration) || !ReferenceEquals(bundle, _acceptedBundle)
                || !ReferenceEquals(pinned, _pinnedSharedNeuralPrior)
                || (bundle?.OperatingInputDigest != digest && !_reservedOperatingInputs.Contains(digest)))
            {
                return false;
            }

            _lastAdmission = AdmissionDecision.Unknown("UnchangedOperatingInputs", calibration.Length, acceptance.Length);
            return true;
        }
    }

    private string GetOperatingValidationLevel()
    {
        if (_independentAdmissionEnabled && _qualityGateFrozen)
        {
            return "Heuristic";
        }

        if (_usesInheritedPrior)
        {
            return "GlobalPrior";
        }

        if (_acceptedBundle is null)
        {
            return "Legacy";
        }

        return _acceptedBundle.IsProvisional ? "Provisional" : "Verified";
    }

    private bool CanRebalanceProvisionalServing(AdmissionDecision decision)
    {
        lock (_syncRoot)
        {
            return !_legacyModelSuppressed && !_requiresIndependentRecovery && (_acceptedBundle is null || _acceptedBundle is { IsProvisional: true, Retired: false })
                && decision.Reason is "HomogeneousObservedOutcomes" or "InsufficientIndependentRankingEvidence";
        }
    }

    private bool TryRebalanceProvisionalServing(ChallengerContext context, TrainedCandidates candidates, ValidationBatch batch, IReadOnlyList<TrainingExample> fit, CancellationToken cancellationToken)
    {
        var digest = ComputeOperatingInputDigest(fit, batch.Calibration, context.Bounds, _ownsNeural ? null : candidates.Neural);
        lock (_syncRoot)
        {
            if (_acceptedBundle?.OperatingInputDigest == digest || _reservedOperatingInputs.Contains(digest)
                || _reservedOperatingInputs.Contains("*") || _reservedOperatingInputs.Count >= MaximumReservedOperatingInputs)
            {
                return false;
            }
        }

        var beta = OperatingMixturePolicy.NeuralTarget(context.Bounds.MaturityCount, _ownsNeural, candidates.Neural?.ExportParameters().TrainingGeneration > 0);
        var alpha = context.Bounds.AlphaCap;
        var target = new AdmissionMixture(alpha * (1 - beta), alpha * beta);
        var measured = EnsembleAdmissionEvaluator.EvaluateServingCalibration(batch.Calibration, batch.Models, target, batch.EvaluatedAt);
        if (measured is null)
        {
            return false;
        }

        target = OperatingMixturePolicy.Target(alpha, context.Bounds.Minimum, beta, measured);
        var current = context.Incumbent.Resolve(alpha);
        if (candidates.Neural is null)
        {
            current = new AdmissionMixture(current.Alpha, 0);
        }

        var mixture = current.Alpha > 0 ? OperatingMixturePolicy.Approach(current, target) : target;
        measured = EnsembleAdmissionEvaluator.EvaluateServingCalibration(batch.Calibration, batch.Models, mixture, batch.EvaluatedAt);
        if (measured is null)
        {
            return false;
        }

        var retained = false;
        if (!UsableProvisionalCandidate(measured))
        {
            var retention = TryCalibrateRetainedProvisional(context, batch);
            if (retention is null)
            {
                return false;
            }

            (mixture, measured) = retention.Value;
            candidates = new TrainedCandidates(LearnedStrategy, null, context.Incumbent.Learned, context.Incumbent.Neural);
            retained = true;
        }

        var decision = new AdmissionDecision(false, retained ? "ProvisionalParametersRetained" : "ProvisionalMaturityMix", mixture, new AdmissionEvidence(measured.Titles, 0, 0, 0), new AdmissionUtility(null, null, null, null, null));
        var bounds = context.Bounds with { NeuralCap = context.Bounds.AlphaCap * NeuralMaxBetaFraction };
        var serving = candidates with { Neural = mixture.Neural > 0 ? candidates.Neural : context.Incumbent.Neural, NeuralTrainer = mixture.Neural > 0 ? candidates.NeuralTrainer : null };
        RecordAdmissionAttempt(decision, fit.Count, batch.EvaluatedAt);
        return CommitAcceptedChallenger(serving, decision, [], bounds, cancellationToken, new ProvisionalCommit(measured, digest, retained));
    }

    private static bool UsableProvisionalCandidate(EnsembleAdmissionEvaluator.ServingCalibration measured)
        => measured.LearnedLoss <= ValidationLossCeiling && measured.MixtureLoss <= ValidationLossCeiling
            && measured.MixtureLoss <= measured.HeuristicLoss && measured.RankingSafe
            && (measured.IncumbentLoss is not double incumbent || measured.MixtureLoss <= incumbent);

    private (AdmissionMixture Mixture, EnsembleAdmissionEvaluator.ServingCalibration Calibration)? TryCalibrateRetainedProvisional(ChallengerContext context, ValidationBatch batch)
    {
        if (_acceptedBundle is not { IsProvisional: true, Retired: false })
        {
            return null;
        }

        var current = context.Incumbent.Resolve(context.Bounds.AlphaCap);
        var alpha = context.Bounds.Minimum + ((current.Alpha - context.Bounds.Minimum) * TrendDegradationDamping);
        alpha = Math.Min(current.Alpha, alpha);
        var beta = current.Beta * .5;
        if (beta < NeuralBetaMinFloor)
        {
            beta = 0;
        }

        var reduced = new AdmissionMixture(alpha * (1 - beta), alpha * beta);
        var models = batch.Models with
        {
            Learned = new PenalizedPredictor(context.Incumbent.Learned, context.Bounds.Floor),
            Neural = context.Incumbent.Neural is null ? null : new PenalizedPredictor(context.Incumbent.Neural, context.Bounds.Floor)
        };
        var calibration = EnsembleAdmissionEvaluator.EvaluateServingCalibration(batch.Calibration, models, reduced, batch.EvaluatedAt);
        if (calibration is not null && UsableProvisionalCandidate(calibration))
        {
            return (reduced, calibration);
        }

        calibration = EnsembleAdmissionEvaluator.EvaluateServingCalibration(batch.Calibration, models, current, batch.EvaluatedAt);
        return calibration?.MixtureLoss <= ValidationLossCeiling ? (current, calibration) : null;
    }

    private static string ComputeOperatingInputDigest(IReadOnlyList<TrainingExample> fit, IReadOnlyList<TrainingExample> calibration, PromotionBounds bounds, NeuralScoringStrategy.PredictionSnapshot? neuralPrior)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { bounds.AlphaCap, bounds.Minimum, bounds.Floor, bounds.MaturityCount }, AssessmentJsonOptions));
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(neuralPrior?.ExportParameters(), AssessmentJsonOptions));
        foreach (var partition in new[] { fit, calibration })
        {
            hash.AppendData(BitConverter.GetBytes(partition.Count));
            foreach (var row in partition.OrderBy(row => row.UserId).ThenBy(row => row.ObservationId))
            {
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row, AssessmentJsonOptions));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private string? ReserveAcceptanceEvidence(IReadOnlyCollection<Guid> ids, EvaluationConfiguration configuration, HashSet<Guid> reservedThisCycle, string inputDigest)
    {
        lock (_syncRoot)
        {
            if (!ConfigurationIsCurrent(configuration))
            {
                RejectChangedEvaluationConfiguration();
                return "EvaluationConfigurationChanged";
            }

            if (_independentAdmissionDisposed)
            {
                return "EnsembleDisposed";
            }

            var fresh = ids.Where(id => !reservedThisCycle.Contains(id)).ToArray();
            if (fresh.Length == 0)
            {
                return null;
            }

            try
            {
                new AcceptanceObservationLedger(AcceptedModelStore.GetLedgerPath(_statePath), (path, text) => AtomicFile.WriteAllText(path, text))
                    .Spend(fresh, _spentAcceptanceObservations, _acceptanceLedgerRequired);
                _spentAcceptanceObservations.UnionWith(fresh);
                reservedThisCycle.UnionWith(fresh);
                _acceptanceLedgerRequired = _statePath is not null;
                if (_reservedOperatingInputs.Count < MaximumReservedOperatingInputs)
                {
                    _reservedOperatingInputs.Add(inputDigest);
                }

                if (!WriteAssessment(CreateAssessment(DateTime.UtcNow)))
                {
                    return CheckpointCommitFailure;
                }

                return null;
            }
            catch (Exception exception) when (!exception.IsFatal())
            {
                _logger?.LogWarning(exception, "Acceptance reservation failed; retaining the current model without using these outcomes");
                return CheckpointCommitFailure;
            }
        }
    }

    /// <summary>Returns ownership to the caller's using scope before any further preparation can fail.</summary>
    /// <param name="trainNeural">Whether this scope should fit its own Neural challenger.</param>
    /// <returns>A detached owned challenger, or null for shared/non-training scopes.</returns>
    private NeuralScoringStrategy? CreateNeuralChallenger(bool trainNeural)
    {
        lock (_syncRoot)
        {
            return trainNeural && _ownsNeural ? (_acceptedNeuralTrainer ?? _neural)?.CreateChallenger() : null;
        }
    }

    private ChallengerContext PrepareChallengers(IReadOnlyList<TrainingExample> fit, NeuralScoringStrategy? neural, DateTime evaluatedAt)
    {
        lock (_syncRoot)
        {
            EnableIndependentAdmission();
            var learned = LearnedStrategy.CreateChallenger();
            UpdateObservedMaturity(fit.Where(row => row.IsObserved && row.HasStableObservationId).Select(row => row.ObservationId).Distinct().ToArray());
            var alphaCap = OperatingMixturePolicy.Maturity(_trainingExampleCount, DefaultSigmoidMidpoint + _sigmoidMidpointOffset, _alphaMin, _alphaMax);
            var neuralCap = alphaCap * OperatingMixturePolicy.NeuralTarget(_trainingExampleCount, _ownsNeural, available: true);
            var configuration = CaptureEvaluationConfiguration();
            var context = new ChallengerContext(learned, neural, _acceptedPrediction!, new PromotionBounds(alphaCap, neuralCap, configuration.Floor, configuration, _alphaMin, _trainingExampleCount));
            // Absence is a pinned result too. Resolve before fitting, not from a later inherited serving refresh.
            var sharedPrior = _pinnedSharedNeuralPrior ?? (_sharedPredictionProvider is { } provider ? new NeuralTrainingPrior(provider()) : null);
            SaveAssessment(evaluatedAt);
            return context with { SharedNeuralPrior = sharedPrior };
        }
    }

    private TrainedCandidates CaptureFittedCandidates(ChallengerContext context, IReadOnlyList<TrainingExample> fit, IReadOnlyList<TrainingExample> calibration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NeuralScoringStrategy? fittedNeural = null;
        NeuralScoringStrategy.PredictionSnapshot? neural = null;
        if (context.Neural is { } challenger && challenger.Train(fit, calibration))
        {
            fittedNeural = challenger;
            neural = challenger.CapturePredictionSnapshot();
        }

        cancellationToken.ThrowIfCancellationRequested();
        neural ??= ResolveNeuralTrainingPrior(context);
        return new TrainedCandidates(context.Learned, fittedNeural, context.Learned.CapturePredictionSnapshot(), neural);
    }

    private static NeuralScoringStrategy.PredictionSnapshot? ResolveNeuralTrainingPrior(ChallengerContext context)
    {
        if (context.SharedNeuralPrior is { } supplied)
        {
            return supplied.Prediction;
        }

        return context.Incumbent.Mixture.Neural > 0 ? context.Incumbent.Neural : null;
    }

    private AdmissionModels BuildAdmissionModels(ChallengerContext context, TrainedCandidates candidates)
    {
        var floor = context.Bounds.Floor;
        return new AdmissionModels(
            new PenalizedPredictor(_heuristic, floor),
            new PenalizedPredictor(candidates.Learned, floor),
            candidates.Neural is null ? null : new PenalizedPredictor(candidates.Neural, floor),
            context.Incumbent.Mixture.Alpha > 0 ? new PinnedMixturePredictor(context.Incumbent, context.Bounds.Configuration.Maximum, floor) : null);
    }

    private void RecordAdmissionAttempt(AdmissionDecision decision, int fittingCount, DateTime evaluatedAt)
    {
        lock (_syncRoot)
        {
            _lastAdmission = decision;
            RecordMetricsSnapshot(decision.Utility.CandidateLoss ?? double.NaN, fittingCount, decision.Utility);
            SaveAssessment(evaluatedAt);
        }
    }

    private HistoricalRestoration TryRestoreAgainstChallenger(ChallengerContext context, TrainedCandidates candidates, AdmissionDecision decision, ValidationBatch batch, CancellationToken cancellationToken)
    {
        var store = new AcceptedModelStore(_statePath, _logger);
        var historical = store.ReadHistory();
        if (historical.Length == 0)
        {
            return HistoricalRestoration.NotSelected;
        }

        var configuration = context.Bounds.Configuration;
        var attempted = new AcceptedPrediction(_heuristic, candidates.Learned, candidates.Neural, decision.Candidate, context.Bounds.NeuralCap);
        var calibrated = EnsembleAdmissionEvaluator.EvaluateFixedVersion(batch.Calibration, batch.Models.Heuristic, new PinnedMixturePredictor(attempted, configuration.Maximum, configuration.Floor), batch.Models.Incumbent ?? batch.Models.Heuristic, batch.EvaluatedAt);
        var restoration = new RestorationContext(batch.Calibration, batch.Acceptance, context.Incumbent, configuration.Floor, batch.EvaluatedAt, calibrated?.CandidateLoss ?? double.PositiveInfinity, batch.Models.Learned, configuration) { ReserveEvidence = batch.ReserveEvidence };
        return TryRestoreHistoricalModel(restoration, historical, store, cancellationToken);
    }

    private void TryWithdrawOnFreshEvidence(ChallengerContext context, ValidationBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Models.Incumbent is null)
        {
            return;
        }

        var examined = new ExaminedAcceptance(batch.ReserveEvidence);
        var evidence = EnsembleAdmissionEvaluator.EvaluateWithdrawal(batch.Acceptance, batch.Models.Heuristic, batch.Models.Incumbent, batch.EvaluatedAt, examined.Reserve);
        if (evidence is not null)
        {
            CommitWithdrawal(context.Incumbent, evidence, examined.Observations, context.Bounds.Configuration, cancellationToken);
        }
    }

    private bool CommitAcceptedChallenger(TrainedCandidates candidates, AdmissionDecision decision, IReadOnlyCollection<Guid> examinedObservations, PromotionBounds bounds, CancellationToken cancellationToken, ProvisionalCommit? provisional = null)
    {
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfigurationIsCurrent(bounds.Configuration))
            {
                RejectChangedEvaluationConfiguration();
                return true;
            }

            if (_independentAdmissionDisposed || bounds.AlphaCap > _alphaMax)
            {
                return true;
            }

            var ids = examinedObservations.ToArray();

            var now = DateTime.UtcNow;
            var bundle = new AcceptedModelBundle
            {
                Version = provisional is null ? 1 : 2,
                IsProvisional = provisional is not null,
                CalibrationLoss = provisional?.Calibration.MixtureLoss,
                CalibrationTitleCount = provisional?.Calibration.Titles ?? 0,
                OperatingInputDigest = provisional?.Digest ?? bounds.InputDigest,
                Learned = candidates.Learned.ExportParameters(),
                Neural = candidates.Neural?.ExportParameters(),
                Mixture = decision.Candidate,
                AlphaCap = bounds.AlphaCap,
                NeuralCap = bounds.NeuralCap,
                Revision = (_acceptedBundle?.Revision ?? 0) + 1,
                TrainedAtUtc = provisional?.RetainedParameters == true ? _acceptedBundle!.TrainedAtUtc : now,
                AcceptanceObservationIds = ids,
                Utility = provisional is null ? decision.Utility : null,
                State = new EnsembleStateData
                {
                    ObservedTrainingIds = _observedTrainingIds.ToArray(),
                    TrainingExampleCount = _trainingExampleCount,
                    Alpha = bounds.AlphaCap,
                    NeuralBeta = decision.Candidate.Beta,
                    QualityGateFrozen = false,
                    SigmoidMidpointOffset = _sigmoidMidpointOffset,
                    MetricsHistory = _metricsHistory.ToList(),
                    UpdatedAt = provisional?.RetainedParameters == true ? _acceptedBundle!.State.UpdatedAt : now.ToString("O", CultureInfo.InvariantCulture)
                }
            };
            if (!new AcceptedModelStore(_statePath, _logger).Commit(bundle))
            {
                _lastAdmission = decision with { Accepted = false, Reason = CheckpointCommitFailure };
                SaveAssessment(DateTime.UtcNow);
                return true;
            }

            // After the atomic disk commit, finish publication even if cancellation arrived meanwhile.
            _acceptedPrediction = new AcceptedPrediction(_heuristic, candidates.Learned, candidates.Neural, decision.Candidate, bounds.NeuralCap);
            _acceptedBundle = bundle;
            _usesInheritedPrior = false;
            _legacyModelSuppressed = false;
            _requiresIndependentRecovery = false;
            if (provisional is null)
            {
                _reservedOperatingInputs.Clear();
            }

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

    private void CommitWithdrawal(AcceptedPrediction incumbent, AdmissionUtility evidence, IReadOnlyCollection<Guid> examinedObservations, EvaluationConfiguration configuration, CancellationToken cancellationToken)
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
                CommitLegacyWithdrawal(incumbent, evidence, examinedObservations);
                return;
            }

            var ids = examinedObservations.ToArray();

            var retired = new AcceptedModelBundle
            {
                Version = previous.Version,
                IsProvisional = previous.IsProvisional,
                CalibrationLoss = previous.CalibrationLoss,
                CalibrationTitleCount = previous.CalibrationTitleCount,
                OperatingInputDigest = previous.OperatingInputDigest,
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
                _lastAdmission = _lastAdmission! with { Accepted = false, Reason = CheckpointCommitFailure };
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

    private void CommitLegacyWithdrawal(AcceptedPrediction incumbent, AdmissionUtility evidence, IReadOnlyCollection<Guid> examinedObservations)
    {
        var ids = examinedObservations.ToArray();
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
                _lastAdmission = decision with { Reason = CheckpointCommitFailure };
                return;
            }
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            _logger?.LogWarning(exception, "Legacy suppression could not be committed; legacy weights remain untouched");
            _lastAdmission = decision with { Reason = CheckpointCommitFailure };
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
            && ValidLegacyWithdrawalUtility(assessment.LegacyWithdrawalUtility);

    private static bool ValidLegacyWithdrawalUtility(AdmissionUtility? utility)
        => utility is { LowerIncumbentImprovement: double improvement, CandidateLoss: double loss, CandidateNdcg: double ndcg }
            && double.IsFinite(improvement) && improvement > 0 && double.IsFinite(loss) && loss >= 0 && double.IsFinite(ndcg) && ndcg is >= 0 and <= 1;

    private HistoricalRestoration TryRestoreHistoricalModel(RestorationContext context, IReadOnlyList<AcceptedModelBundle> historical, AcceptedModelStore store, CancellationToken cancellationToken)
    {
        var heuristic = new PenalizedPredictor(_heuristic, context.Floor);
        var current = new PinnedMixturePredictor(context.Incumbent, context.Configuration.Maximum, context.Floor);
        var selected = SelectHistoricalCandidate(historical, context, heuristic, current);
        if (selected is null)
        {
            return HistoricalRestoration.NotSelected;
        }

        var evidence = EvaluateHistoricalAcceptance(selected, context, heuristic, current);
        if (evidence is null)
        {
            return HistoricalRestoration.SelectedRejected;
        }

        return CommitHistoricalCandidate(selected, context, evidence, store, cancellationToken);
    }

    private HistoricalCandidate? SelectHistoricalCandidate(IReadOnlyList<AcceptedModelBundle> historical, RestorationContext context, PenalizedPredictor heuristic, PinnedMixturePredictor current)
    {
        HistoricalCandidate? selected = null;
        foreach (var bundle in historical)
        {
            var candidate = CalibrateHistoricalCandidate(bundle, context, heuristic, current);
            if (candidate is not null && (selected is null || candidate.CalibrationLoss < selected.CalibrationLoss))
            {
                selected = candidate;
            }
        }

        return selected;
    }

    private HistoricalCandidate? CalibrateHistoricalCandidate(AcceptedModelBundle bundle, RestorationContext context, PenalizedPredictor heuristic, PinnedMixturePredictor current)
    {
        var learned = new LearnedScoringStrategy.PredictionSnapshot(bundle.Learned);
        var neural = bundle.Neural is null ? null : new NeuralScoringStrategy.PredictionSnapshot(bundle.Neural);
        var prediction = new AcceptedPrediction(_heuristic, learned, neural, bundle.Mixture, bundle.NeuralCap);
        var scorer = new PinnedMixturePredictor(prediction, context.Configuration.Maximum, context.Floor);
        var metrics = EnsembleAdmissionEvaluator.EvaluateFixedVersion(context.Calibration, heuristic, scorer, current, context.ReferenceTime);
        if (metrics?.CandidateLoss is not double loss || !double.IsFinite(loss)
            || double.IsNaN(context.CandidateCalibrationLoss) || loss >= context.CandidateCalibrationLoss)
        {
            return null;
        }

        IScoringStrategy? noNeural = null;
        if (bundle.Mixture.Neural > 0)
        {
            var alternatives = new IScoringStrategy[] { context.LearnedChallenger, new PenalizedPredictor(context.Incumbent.Learned, context.Floor), new PenalizedPredictor(learned, context.Floor) };
            noNeural = EnsembleAdmissionEvaluator.CalibrateNoNeuralReference(context.Calibration, heuristic, alternatives, context.ReferenceTime, Math.Min(bundle.AlphaCap, context.Configuration.Maximum));
            if (noNeural is null || EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(context.Calibration, heuristic, scorer, noNeural, context.ReferenceTime) is null)
            {
                return null;
            }
        }

        return new HistoricalCandidate(bundle, prediction, loss, noNeural);
    }

    private static VerifiedAcceptance? EvaluateHistoricalAcceptance(HistoricalCandidate selected, RestorationContext context, PenalizedPredictor heuristic, PinnedMixturePredictor current)
    {
        var examined = new ExaminedAcceptance(context.ReserveEvidence);
        var prediction = new PinnedMixturePredictor(selected.Prediction, context.Configuration.Maximum, context.Floor);
        var evidence = EnsembleAdmissionEvaluator.EvaluateFixedVersion(context.Acceptance, heuristic, prediction, current, context.ReferenceTime, examined.Reserve);
        var neuralEvidence = selected.NoNeuralReference is null ? null : EnsembleAdmissionEvaluator.EvaluateFixedNeuralIncrement(context.Acceptance, heuristic, prediction, selected.NoNeuralReference, context.ReferenceTime, examined.Reserve);
        if (evidence?.LowerHeuristicImprovement is not > 0 || evidence.LowerIncumbentImprovement is not > 0
            || (selected.Prediction.Mixture.Neural > 0 && neuralEvidence is null))
        {
            return null;
        }

        return new VerifiedAcceptance(evidence with { LowerNeuralImprovement = neuralEvidence }, examined.Observations);
    }

    private HistoricalRestoration CommitHistoricalCandidate(HistoricalCandidate selected, RestorationContext context, VerifiedAcceptance evidence, AcceptedModelStore store, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfigurationIsCurrent(context.Configuration))
            {
                RejectChangedEvaluationConfiguration();
                return HistoricalRestoration.SelectedRejected;
            }

            if (_independentAdmissionDisposed)
            {
                return HistoricalRestoration.SelectedRejected;
            }

            var original = selected.Bundle;
            var ids = evidence.Observations.ToArray();

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
                Utility = evidence.Utility,
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
                _lastAdmission = _lastAdmission! with { Accepted = false, Reason = CheckpointCommitFailure };
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
            _lastAdmission = new AdmissionDecision(true, "HistoricalModelRestoredOnNewEvidence", original.Mixture, _lastAdmission!.Evidence, evidence.Utility);
            SaveAssessment(DateTime.UtcNow);
            return HistoricalRestoration.Restored;
        }
    }

    private bool ConfigurationIsCurrent(EvaluationConfiguration configuration)
        => configuration.Revision == _configurationRevision
            && ScoringHelper.SameConfigurationValue(configuration.Maximum, _alphaMax)
            && ScoringHelper.SameConfigurationValue(configuration.Floor, _genrePenaltyFloor);

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

        var learned = LearnedScoringStrategy.FromAcceptedParameters(bundle.Learned);
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
            ReservedOperatingInputDigests = _reservedOperatingInputs.Order(StringComparer.Ordinal).ToArray(),
            IndependentAdmissionEnabled = _independentAdmissionEnabled,
            HeuristicFallbackRequired = _acceptedBundle is null && _acceptedPrediction?.Mixture.Alpha <= 0,
            IndependentRecoveryRequired = _requiresIndependentRecovery,
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
            AtomicFile.WriteAllText(_statePath + AssessmentSuffix, JsonSerializer.Serialize(assessment, AssessmentJsonOptions));
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
        if (_statePath is null || !File.Exists(_statePath + AssessmentSuffix))
        {
            if (_statePath is not null && (_acceptedBundle?.IsProvisional == true || File.Exists(AcceptedModelStore.GetLedgerPath(_statePath)) || Directory.Exists(_statePath + AssessmentSuffix)))
            {
                RetainUntrustedFallback("MissingAdmissionAssessment");
            }

            return;
        }

        try
        {
            var path = _statePath + AssessmentSuffix;
            var assessment = ReadAssessment(path, _acceptedBundle?.IsProvisional == true);
            if (assessment is null)
            {
                RetainUntrustedFallback("UnreadableAdmissionAssessment");
                return;
            }

            ApplyAssessment(assessment);
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            _logger?.LogWarning(exception, "Model attempt diagnostics could not be read; using model checkpoint state only");
            RetainUntrustedFallback("UnreadableAdmissionAssessment");
        }
    }

    private static ModelTrainingAssessment? ReadAssessment(string path, bool requireOperatingReceipts)
    {
        if (new FileInfo(path).Length > 1024 * 1024)
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (requireOperatingReceipts && (!document.RootElement.TryGetProperty(nameof(ModelTrainingAssessment.ReservedOperatingInputDigests), out _)
            || !document.RootElement.TryGetProperty(nameof(ModelTrainingAssessment.IndependentRecoveryRequired), out _)))
        {
            return null;
        }

        var assessment = document.RootElement.Deserialize<ModelTrainingAssessment>(AssessmentJsonOptions);
        if (assessment is not { Version: 1, ObservedTrainingIds: not null })
        {
            return null;
        }

        if (assessment.MetricsHistory?.Any(snapshot => snapshot is null) == true
            || assessment.ReservedOperatingInputDigests is null || assessment.ReservedOperatingInputDigests.Length > MaximumReservedOperatingInputs
            || assessment.ReservedOperatingInputDigests.Any(digest => digest is null || (digest != "*" && (digest.Length != 64 || !digest.All(Uri.IsHexDigit)))))
        {
            return null;
        }

        return assessment;
    }

    private void ApplyAssessment(ModelTrainingAssessment assessment)
    {
        UpdateObservedMaturity(assessment.ObservedTrainingIds.Where(id => id != Guid.Empty).Take(MaxTrackedObservations).ToArray());
        if (_acceptedBundle is not null && assessment.AcceptedModelRevision < _acceptedBundle.Revision)
        {
            return;
        }

        _lastAdmission = assessment.Decision;
        _reservedOperatingInputs.UnionWith(assessment.ReservedOperatingInputDigests);
        if (_reservedOperatingInputs.Count == 0 && assessment.Decision?.Reason == "RejectedIndependentUtility")
        {
            // A pre-operational-policy journal has no exact digest: it cannot grant the old rejected input a new entry.
            _reservedOperatingInputs.Add("*");
        }

        _acceptanceLedgerRequired |= assessment.AcceptanceLedgerRequired;
        if (!ApplyAssessmentFallback(assessment))
        {
            return;
        }

        ApplyLegacySuppression(assessment);
        if (assessment.IndependentAdmissionEnabled || assessment.Decision is not null || assessment.AcceptedModelRevision > 0)
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

    private bool ApplyAssessmentFallback(ModelTrainingAssessment assessment)
    {
        // Loss of receipts blocks new provisional replacement, not the surviving incumbent's serving mix.
        _requiresIndependentRecovery |= assessment.IndependentRecoveryRequired;
        if (_acceptedBundle is null && (assessment.HeuristicFallbackRequired || assessment.AcceptedModelRevision > 0))
        {
            _qualityGateFrozen = true;
            _requiresIndependentRecovery = assessment.IndependentRecoveryRequired || assessment.AcceptedModelRevision > 0;
        }

        if (assessment.LegacyModelSuppressed && !ValidLegacySuppression(assessment))
        {
            RetainUntrustedFallback("InvalidLegacySuppressionAssessment");
            return false;
        }

        return true;
    }

    private void ApplyLegacySuppression(ModelTrainingAssessment assessment)
    {
        if (_acceptedBundle is not null || !ValidLegacySuppression(assessment))
        {
            return;
        }

        _legacyModelSuppressed = true;
        _legacyWithdrawalUtility = assessment.LegacyWithdrawalUtility;
        _legacyWithdrawalObservations = assessment.LegacyWithdrawalObservationIds;
    }

    private void RetainUntrustedFallback(string reason)
    {
        if (_acceptedBundle is not null)
        {
            if (_acceptedBundle.IsProvisional)
            {
                _requiresIndependentRecovery = true;
                _lastAdmission = AdmissionDecision.Unknown(reason, 0, 0);
            }

            return;
        }

        // An unreadable/missing suppression journal cannot grant old v5 weights influence again.
        // Keep the parameter checkpoint intact; a genuinely useful fresh candidate can still be admitted.
        _qualityGateFrozen = true;
        _requiresIndependentRecovery = true;
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

    private readonly record struct PromotionBounds(double AlphaCap, double NeuralCap, double Floor, EvaluationConfiguration Configuration, double Minimum, int MaturityCount)
    {
        public string? InputDigest { get; init; }
    }

    private readonly record struct ChallengerContext(LearnedScoringStrategy Learned, NeuralScoringStrategy? Neural, AcceptedPrediction Incumbent, PromotionBounds Bounds)
    {
        public NeuralTrainingPrior? SharedNeuralPrior { get; init; }
    }

    private readonly record struct ValidationBatch(IReadOnlyList<TrainingExample> Calibration, IReadOnlyList<TrainingExample> Acceptance, AdmissionModels Models, DateTime EvaluatedAt)
    {
        public Func<IReadOnlyCollection<Guid>, string?>? ReserveEvidence { get; init; }
    }

    private readonly record struct RestorationContext(IReadOnlyList<TrainingExample> Calibration, IReadOnlyList<TrainingExample> Acceptance, AcceptedPrediction Incumbent, double Floor, DateTime ReferenceTime, double CandidateCalibrationLoss, IScoringStrategy LearnedChallenger, EvaluationConfiguration Configuration)
    {
        public Func<IReadOnlyCollection<Guid>, string?>? ReserveEvidence { get; init; }
    }

    private readonly record struct TrainedCandidates(LearnedScoringStrategy LearnedTrainer, NeuralScoringStrategy? NeuralTrainer, LearnedScoringStrategy.PredictionSnapshot Learned, NeuralScoringStrategy.PredictionSnapshot? Neural);

    private sealed record NeuralTrainingPrior(NeuralScoringStrategy.PredictionSnapshot? Prediction);

    private sealed record ProvisionalCommit(EnsembleAdmissionEvaluator.ServingCalibration Calibration, string Digest, bool RetainedParameters);

    private sealed record HistoricalCandidate(AcceptedModelBundle Bundle, AcceptedPrediction Prediction, double CalibrationLoss, IScoringStrategy? NoNeuralReference);

    private sealed record VerifiedAcceptance(AdmissionUtility Utility, IReadOnlyCollection<Guid> Observations);

    private sealed class ExaminedAcceptance(Func<IReadOnlyCollection<Guid>, string?>? reserve)
    {
        private readonly HashSet<Guid> _observations = [];

        public IReadOnlyCollection<Guid> Observations => _observations;

        public string? Reserve(IReadOnlyCollection<Guid> observations)
        {
            var failure = reserve?.Invoke(observations);
            if (failure is null)
            {
                _observations.UnionWith(observations);
            }

            return failure;
        }
    }

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
