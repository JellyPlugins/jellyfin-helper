using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>Reads/writes one authoritative v6 model bundle; unsupported files are not rewritten while loading.</summary>
internal sealed class AcceptedModelStore
{
    private const long MaximumBundleBytes = 10 * 1024 * 1024;
    private const int MaximumHistoricalVersions = 2;
    private static readonly TimeSpan HistoricalRetention = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly string? _path;
    private readonly string? _ledgerPath;
    private readonly ILogger? _logger;
    private readonly Action<string, string> _write;

    /// <summary>Initializes a new instance of the <see cref="AcceptedModelStore"/> class for a scope-bound atomic checkpoint.</summary>
    /// <param name="statePath">The scope's existing ensemble state path; null keeps the scope memory-only.</param>
    /// <param name="logger">Optional diagnostic logger.</param>
    /// <param name="write">Optional atomic write dependency for deterministic storage-failure tests.</param>
    internal AcceptedModelStore(string? statePath, ILogger? logger = null, Action<string, string>? write = null)
    {
        _path = GetBundlePath(statePath);
        _ledgerPath = GetLedgerPath(statePath);
        _logger = logger;
        _write = write ?? ((path, json) => AtomicFile.WriteAllText(path, json));
    }

    /// <summary>Derives an additive path rather than changing the meaning of legacy weights or blend files.</summary>
    /// <param name="statePath">The existing scope-bound state path.</param>
    /// <returns>The accepted bundle path, or null for memory-only scopes.</returns>
    internal static string? GetBundlePath(string? statePath) => string.IsNullOrEmpty(statePath) ? null : statePath + ".accepted-model.json";

    /// <summary>Derives the single exact reuse ledger, which is not duplicated into model archives.</summary>
    /// <param name="statePath">The scope-bound state path.</param>
    /// <returns>The additive ledger path, or null for a memory-only scope.</returns>
    internal static string? GetLedgerPath(string? statePath) => string.IsNullOrEmpty(statePath) ? null : statePath + ".acceptance-ledger.txt";

    /// <summary>Reads all spent identities, preserving cumulative pre-ledger bundle compatibility.</summary>
    /// <param name="bundle">The current complete checkpoint, when present.</param>
    /// <param name="required">A persisted assessment may require the ledger even if the bundle is missing.</param>
    /// <returns>Exact spent identities; storage/integrity failures propagate to the training guard.</returns>
    internal HashSet<Guid> ReadSpentAcceptanceIds(AcceptedModelBundle? bundle, bool required = false)
    {
        // Prediction compatibility is independent of the already-spent acceptance receipt.
        // A new store is used on each lookup, so recover the receipt even when Read() rejected the parameters.
        var receipt = bundle ?? ReadStoredBundle();
        ValidateStoredReceipt(receipt);
        return new AcceptanceObservationLedger(_ledgerPath, _write).Read(receipt?.AcceptanceObservationIds ?? [], required || receipt?.AcceptanceLedgerRequired == true);
    }

    /// <summary>Reads only complete, finite, compatible versions. Invalid bundles never rewrite legacy checkpoints.</summary>
    /// <returns>The accepted version, or null when no usable compound checkpoint exists.</returns>
    internal AcceptedModelBundle? Read() => Read(out _);

    /// <summary>Distinguishes an intact schema replacement from damaged/missing checkpoints without loading old parameters.</summary>
    /// <param name="superseded">Only a validated schema and reuse receipt, never an old predictor.</param>
    /// <returns>The current compatible bundle, or null when fresh parameters are required.</returns>
    internal AcceptedModelBundle? Read(out SupersededCheckpoint? superseded)
    {
        superseded = null;
        try
        {
            var bundle = ReadStoredBundle(out var intactPreviousSchema);
            if (bundle is null || Valid(bundle))
            {
                return bundle;
            }

            if (intactPreviousSchema && ValidReceipt(bundle))
            {
                // Lost/corrupt required ledgers remain recovery failures, even for perfectly intact V5 weights.
                ReadSpentAcceptanceIds(bundle);
                superseded = new SupersededCheckpoint(bundle.Revision, bundle.State.ObservedTrainingIds!.ToFrozenSet());
            }

            return null;
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            _logger?.LogWarning(exception, "Accepted model checkpoint could not be read; retaining the legacy fallback");
            return null;
        }
    }

    private AcceptedModelBundle? ReadStoredBundle() => ReadStoredBundle(out _);

    private AcceptedModelBundle? ReadStoredBundle(out bool intactPreviousSchema)
    {
        intactPreviousSchema = false;
        if (_path is null || !File.Exists(_path))
        {
            return null;
        }

        if (new FileInfo(_path).Length > MaximumBundleBytes)
        {
            throw new InvalidDataException("Accepted checkpoint exceeds the receipt read limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (!SupersededModelSchema.HasUniqueFields(document.RootElement))
            {
                throw new InvalidDataException("Accepted checkpoint has ambiguous duplicate receipt fields.");
            }

            var bundle = document.RootElement.Deserialize<AcceptedModelBundle>(JsonOptions)
                ?? throw new InvalidDataException("Accepted checkpoint contains no receipt.");
            intactPreviousSchema = bundle.Learned?.Version == 5 && ValidReceipt(bundle) && SupersededModelSchema.IsIntact(bundle, document.RootElement);
            return bundle;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Accepted checkpoint receipt is malformed.", exception);
        }
    }

    private static void ValidateStoredReceipt(AcceptedModelBundle? receipt)
    {
        if (receipt is not null && !ValidReceipt(receipt))
        {
            throw new InvalidDataException("Accepted checkpoint has invalid acceptance receipts; spent evidence cannot be recovered safely.");
        }
    }

    /// <summary>Validates and atomically stores a complete version before its caller publishes it in memory.</summary>
    /// <param name="bundle">The complete version to commit.</param>
    /// <returns>False on validation/storage failure, leaving the old authoritative version untouched.</returns>
    internal bool Commit(AcceptedModelBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!Valid(bundle))
        {
            return false;
        }

        if (_path is null)
        {
            return true;
        }

        try
        {
            var previous = ReadStoredBundle();
            ValidateStoredReceipt(previous);
            bundle.AcceptanceLedgerRequired = true;
            var json = JsonSerializer.Serialize(bundle, JsonOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBundleBytes)
            {
                return false;
            }

            // Ledger first: a crash/write failure may spend an uncommitted block, but cannot admit it twice.
            new AcceptanceObservationLedger(_ledgerPath, _write).Spend(bundle.AcceptanceObservationIds, previous?.AcceptanceObservationIds ?? [], previous?.AcceptanceLedgerRequired == true);
            ArchiveBeforeReplacement(bundle, previous);
            _write(_path, json);
            return true;
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            _logger?.LogWarning(exception, "Accepted model checkpoint commit failed; the incumbent remains active");
            return false;
        }
    }

    /// <summary>Returns at most two older accepted checkpoints, pruning only this store's expired archive slots.</summary>
    /// <returns>Usable older versions, never an unbounded training-attempt history.</returns>
    internal AcceptedModelBundle[] ReadHistory()
    {
        if (_path is null)
        {
            return [];
        }

        var results = new System.Collections.Generic.List<AcceptedModelBundle>();
        for (var slot = 0; slot < MaximumHistoricalVersions; slot++)
        {
            var path = HistoryPath(slot);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > MaximumBundleBytes)
                {
                    continue;
                }

                var bundle = JsonSerializer.Deserialize<AcceptedModelBundle>(File.ReadAllText(path), JsonOptions);
                if (bundle is null || !Valid(bundle))
                {
                    continue;
                }

                if (DateTime.UtcNow - bundle.TrainedAtUtc > HistoricalRetention)
                {
                    File.Delete(path);
                    continue;
                }

                if (!bundle.Retired && !bundle.IsProvisional)
                {
                    results.Add(bundle);
                }
            }
            catch (Exception exception) when (!exception.IsFatal())
            {
                _logger?.LogWarning(exception, "Older accepted-model archive could not be read; keeping the current version");
            }
        }

        return results.ToArray();
    }

    private string HistoryPath(int slot) => _path + ".history." + slot + ".json";

    private void ArchiveBeforeReplacement(AcceptedModelBundle replacement, AcceptedModelBundle? previous)
    {
        if (previous is null || !Valid(previous) || previous.Retired || previous.IsProvisional || replacement.Retired || previous.TrainedAtUtc == replacement.TrainedAtUtc)
        {
            return;
        }

        var older = ReadHistory().FirstOrDefault();
        if (older is not null && older.Revision != previous.Revision)
        {
            _write(HistoryPath(1), JsonSerializer.Serialize(older, JsonOptions));
        }

        if (DateTime.UtcNow - previous.TrainedAtUtc <= HistoricalRetention)
        {
            _write(HistoryPath(0), JsonSerializer.Serialize(previous, JsonOptions));
        }
    }

    private static bool Valid(AcceptedModelBundle bundle)
        => ValidReceipt(bundle) && LearnedScoringStrategy.IsValidParameters(bundle.Learned)
            && (bundle.Neural is null || NeuralScoringStrategy.IsValidParameters(bundle.Neural));

    private static bool ValidReceipt(AcceptedModelBundle bundle)
        => bundle.Version is 1 or 2 && bundle.Revision is > 0 and < long.MaxValue && bundle.Learned is not null
            && ValidMixture(bundle) && ValidState(bundle) && ValidCheckpointEvidence(bundle)
            && (!bundle.Retired || (bundle.RetirementUtility is not null && FinitePositive(bundle.RetirementUtility.LowerIncumbentImprovement)))
            && bundle.TrainedAtUtc.Kind == DateTimeKind.Utc;

    private static bool ValidCheckpointEvidence(AcceptedModelBundle bundle)
    {
        if (!bundle.IsProvisional)
        {
            return ValidEvidence(bundle);
        }

        return bundle.Version == 2 && bundle.Utility is null
            && FiniteNonnegative(bundle.CalibrationLoss)
            && bundle.CalibrationLoss <= EnsembleScoringStrategy.ValidationLossCeiling
            && bundle.CalibrationTitleCount >= LearnedScoringStrategy.MinExamplesPerFold
            && bundle.State.TrainingExampleCount >= LearnedScoringStrategy.MinTrainingExamples
            && bundle.Learned.TrainingGeneration > 0
            && (bundle.Mixture.Neural <= 0 || bundle.Neural?.TrainingGeneration > 0)
            && bundle.AcceptanceObservationIds is not null
            && bundle.AcceptanceObservationIds.All(id => id != Guid.Empty)
            && bundle.AcceptanceObservationIds.Distinct().Count() == bundle.AcceptanceObservationIds.Length;
    }

    private static bool ValidMixture(AcceptedModelBundle bundle)
        => double.IsFinite(bundle.AlphaCap) && bundle.AlphaCap is >= 0 and <= 1
            && double.IsFinite(bundle.NeuralCap) && bundle.NeuralCap >= 0
            && bundle.NeuralCap <= bundle.AlphaCap * EnsembleScoringStrategy.NeuralMaxBetaFraction
            && double.IsFinite(bundle.Mixture.Learned) && bundle.Mixture.Learned >= 0
            && double.IsFinite(bundle.Mixture.Neural) && bundle.Mixture.Neural >= 0
            && bundle.Mixture.Alpha > 0 && bundle.Mixture.Alpha <= bundle.AlphaCap
            && bundle.Mixture.Neural <= bundle.NeuralCap && (bundle.Mixture.Neural <= 0 || bundle.Neural is not null);

    private static bool ValidEvidence(AcceptedModelBundle bundle)
        => bundle.Utility is not null && FiniteNonnegative(bundle.Utility.CandidateLoss) && FiniteNonnegative(bundle.Utility.CandidateNdcg)
            && bundle.Utility.CandidateNdcg <= 1 && FinitePositive(bundle.Utility.LowerHeuristicImprovement)
            && (bundle.Mixture.Neural <= 0 || FinitePositive(bundle.Utility.LowerNeuralImprovement))
            && (bundle.Utility.LowerIncumbentImprovement is null || FinitePositive(bundle.Utility.LowerIncumbentImprovement))
            && bundle.AcceptanceObservationIds is { Length: >= EnsembleAdmissionEvaluator.MinimumEffectiveTitles }
            && bundle.AcceptanceObservationIds.All(id => id != Guid.Empty)
            && bundle.AcceptanceObservationIds.Distinct().Count() == bundle.AcceptanceObservationIds.Length;

    private static bool ValidState(AcceptedModelBundle bundle)
        => bundle.State is not null && bundle.State.SchemaVersion == EnsembleScoringStrategy.EnsembleStateData.CurrentSchemaVersion
            && bundle.State.QualityGateFrozen == bundle.Retired && double.IsFinite(bundle.State.Alpha) && bundle.State.Alpha is >= 0 and <= 1
            && double.IsFinite(bundle.State.NeuralBeta) && bundle.State.NeuralBeta is >= 0 and <= 1
            && bundle.State.TrainingExampleCount >= 0
            && bundle.State.MetricsHistory is not null && bundle.State.MetricsHistory.All(snapshot => snapshot is not null);

    private static bool FiniteNonnegative(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0;

    private static bool FinitePositive(double? value) => value.HasValue && double.IsFinite(value.Value) && value.Value > 0;

    /// <summary>Validated discarded-checkpoint identity; no prediction or optimizer parameters may escape through it.</summary>
    internal sealed record SupersededCheckpoint(long Revision, FrozenSet<Guid> ObservedTrainingIds);
}
