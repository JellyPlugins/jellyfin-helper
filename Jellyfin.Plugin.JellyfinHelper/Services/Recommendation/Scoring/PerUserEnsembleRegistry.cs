using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;

/// <summary>
///     Default <see cref="IPerUserEnsembleRegistry"/> implementation. Lazily builds one
///     <see cref="EnsembleScoringStrategy"/> per user (backed by per-user weight/state files) and falls back
///     to the shared global ensemble for cold-start users.
/// </summary>
public sealed partial class PerUserEnsembleRegistry : IPerUserEnsembleRegistry
{
    private const string LogSource = "Recommendations";

    /// <summary>
    ///     Minimum training examples before a user gets a dedicated learned model. Mirrors the learned
    ///     strategy's own minimum so a per-user model is only created when the learned SGD can actually train.
    /// </summary>
    internal const int PerUserModelThreshold = LearnedScoringStrategy.MinTrainingExamples;

    private readonly EnsembleScoringStrategy _globalEnsemble;
    private readonly NeuralScoringStrategy? _sharedNeural;
    private readonly string? _dataPath;
    private readonly Lock _blendBoundsLock = new();
    private readonly IPluginLogService _pluginLog;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger? _logger;

    // The heuristic is stateless (fixed weights, no training state, no mutation), so one instance is shared
    // by reference across every per-user ensemble. Its floor is 1.0 because the ensemble applies the genre
    // penalty centrally; a per-instance heuristic would only duplicate an immutable object.
    private readonly HeuristicScoringStrategy _sharedHeuristic = new(genrePenaltyFloor: 1.0);

    // Values are lazy so a GetOrAdd race for the same user builds the ensemble exactly once. Concurrent
    // callers may each allocate a Lazy wrapper, but only the dictionary winner's Value is ever evaluated;
    // the losing wrappers are discarded without constructing (and leaking) a second ensemble.
    private readonly ConcurrentDictionary<Guid, Lazy<EnsembleScoringStrategy>> _perUser = new();

    // Mutable so a configuration change can retune per-user ensembles without a restart; guarded by
    // _blendBoundsLock. See Reconfigure.
    private EnsembleBlendBounds _blendBounds;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PerUserEnsembleRegistry"/> class.
    /// </summary>
    /// <param name="globalEnsemble">The shared global ensemble (cold-start fallback + warm-start source).</param>
    /// <param name="sharedNeural">
    ///     The single global neural strategy, shared by reference across every per-user ensemble. May be null
    ///     when neural scoring is disabled. Its lifetime is owned by <paramref name="globalEnsemble"/>, so
    ///     per-user ensembles are constructed with <c>ownsNeural: false</c>.
    /// </param>
    /// <param name="dataPath">The plugin data folder for per-user files. Null keeps per-user models in memory only.</param>
    /// <param name="blendBounds">The alpha bounds and genre-penalty floor for per-user ensembles.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">Optional logger forwarded to each per-user ensemble.</param>
    /// <param name="fileSystem">Abstracts file access so persistence failures can be exercised in tests. Defaults to the real file system.</param>
    public PerUserEnsembleRegistry(
        EnsembleScoringStrategy globalEnsemble,
        NeuralScoringStrategy? sharedNeural,
        string? dataPath,
        EnsembleBlendBounds blendBounds,
        IPluginLogService pluginLog,
        ILogger? logger = null,
        IFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(globalEnsemble);
        ArgumentNullException.ThrowIfNull(pluginLog);

        _globalEnsemble = globalEnsemble;
        _sharedNeural = sharedNeural;
        _dataPath = string.IsNullOrEmpty(dataPath) ? null : dataPath;
        _blendBounds = blendBounds;
        _pluginLog = pluginLog;
        _fileSystem = fileSystem ?? new FileSystem();
        _logger = logger;
    }

    /// <inheritdoc />
    public EnsembleScoringStrategy GlobalEnsemble => _globalEnsemble;

    /// <inheritdoc />
    public IScoringStrategy GetScoringStrategyForUser(Guid userId) => GetEnsembleForUser(userId);

    /// <inheritdoc />
    public EnsembleScoringStrategy GetEnsembleForUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return _globalEnsemble;
        }

        // Already materialized this session.
        if (_perUser.TryGetValue(userId, out var existing))
        {
            var ensemble = existing.Value;
            return ResolveOperatingEnsemble(ensemble);
        }

        // A trained global prior supplies immutable coordinates, never the user's permanent mixture.
        // Without a usable operating prior/model, preserve the original heuristic cold-start fallback.
        var weightsPath = GetLearnedWeightsPath(userId);
        if (_dataPath is null || weightsPath is null || !HasModelFile(userId))
        {
            if (_globalEnsemble.GetOperatingPrior() is { } prior)
            {
                var contextual = GetOrAddPerUser(userId);
                contextual.InitializeOperatingPrior(prior);
                return contextual;
            }

            return _globalEnsemble;
        }

        var perUser = GetOrAddPerUser(userId);

        // A concurrent eviction (training deleting a below-threshold user's files) can delete the weights file
        // between the check above and the build here, leaving a freshly warm-started ensemble cached for a user
        // who should be back on the global model. If the file has since gone, discard that entry so eviction
        // wins and the user falls back to the global model rather than lingering on a stray per-user instance.
        if (!HasModelFile(userId) && _perUser.TryRemove(userId, out var stray))
        {
            if (stray.IsValueCreated)
            {
                stray.Value.Dispose();
            }

            return _globalEnsemble;
        }

        return ResolveOperatingEnsemble(perUser);
    }

    private EnsembleScoringStrategy ResolveOperatingEnsemble(EnsembleScoringStrategy personal)
    {
        if (personal.HasOwnOperatingModel)
        {
            return personal;
        }

        var prior = _globalEnsemble.GetOperatingPrior();
        personal.InitializeOperatingPrior(prior);
        if (prior is not null || personal.GetDiagnosticsSnapshot().ValidationLevel is "GlobalPrior" or "Heuristic")
        {
            return personal;
        }

        return personal.LearnedStrategy.HasOwnWeights ? personal : _globalEnsemble;
    }

    /// <inheritdoc />
    public EnsembleScoringStrategy GetOrCreateTrainableEnsembleForUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return _globalEnsemble;
        }

        return GetOrAddPerUser(userId);
    }

    // Returns the cached per-user ensemble, building it exactly once even under concurrent callers. The
    // Lazy value factory is what actually constructs the ensemble, so a losing GetOrAdd wrapper never runs it.
    private EnsembleScoringStrategy GetOrAddPerUser(Guid userId) =>
        _perUser.GetOrAdd(
            userId,
            id => new Lazy<EnsembleScoringStrategy>(() => BuildPerUserEnsemble(id))).Value;

    /// <inheritdoc />
    public void Reconfigure(EnsembleBlendBounds blendBounds)
    {
        // Store the new bounds so any per-user ensemble built later starts from them, and push them onto every
        // ensemble already materialized this session. Without this a config change would reconfigure only the
        // global ensemble and leave existing per-user models on their construction-time bounds until restart.
        // The whole update runs under the bounds lock, which BuildPerUserEnsemble also holds across
        // construction, so an ensemble whose build is in flight cannot finish on the old bounds and escape this
        // pass: it either becomes visible (and is reconfigured below) or is still blocked on the lock and will
        // read the new bounds when it proceeds. Only reconfigure ensembles that have actually been built, since
        // touching Value on an unmaterialized Lazy would construct it here for no reason and deadlock on the
        // non-reentrant lock.
        lock (_blendBoundsLock)
        {
            _blendBounds = blendBounds;

            foreach (var entry in _perUser.Values.Where(entry => entry.IsValueCreated))
            {
                entry.Value.Reconfigure(blendBounds.AlphaMin, blendBounds.AlphaMax, blendBounds.GenrePenaltyFloor);
            }
        }
    }

    /// <inheritdoc />
    public EnsembleDiagnostics GetDiagnostics(Guid userId) => GetEnsembleForUser(userId).GetDiagnosticsSnapshot();

    /// <inheritdoc />
    public (EnsembleDiagnostics Diagnostics, bool IsPerUser) GetUserModelDiagnostics(Guid userId)
    {
        // Resolve once and derive scope from the same locked snapshot as the displayed coefficients.
        var ensemble = GetEnsembleForUser(userId);
        var diagnostics = ensemble.GetDiagnosticsSnapshot();
        var isPerUser = userId != Guid.Empty && !ReferenceEquals(ensemble, _globalEnsemble) && diagnostics.HasOwnOperatingModel;
        return (diagnostics, isPerUser);
    }

    /// <inheritdoc />
    public bool HasPerUserModel(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        var ensemble = GetEnsembleForUser(userId);
        return !ReferenceEquals(ensemble, _globalEnsemble) && ensemble.HasOwnOperatingModel;
    }

    /// <inheritdoc />
    public void PruneOrphans(IReadOnlyCollection<Guid> liveUserIds)
    {
        ArgumentNullException.ThrowIfNull(liveUserIds);

        var live = new HashSet<Guid>(liveUserIds);
        // Cold priors are fileless, including memory-only scopes. Account removal/disablement still evicts them.
        foreach (var id in _perUser.Keys.Where(id => !live.Contains(id)))
        {
            RemoveCachedEnsemble(id);
        }

        if (_dataPath is null || !_fileSystem.Directory.Exists(_dataPath))
        {
            return;
        }

        try
        {
            foreach (var file in _fileSystem.Directory.EnumerateFiles(_dataPath, "ml_weights_*.json"))
            {
                PruneIfOrphan(file, live);
            }

            foreach (var file in _fileSystem.Directory.EnumerateFiles(_dataPath, "ensemble_state_*.json"))
            {
                PruneIfOrphan(file, live);
            }

            foreach (var file in _fileSystem.Directory.EnumerateFiles(_dataPath, "ensemble_state_*.json.acceptance-ledger.txt"))
            {
                PruneIfOrphan(file, live);
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, $"Per-user model pruning failed: {ex.Message}", ex, _logger);
        }
    }

    /// <inheritdoc />
    public bool EvictPerUserModel(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        var hadCached = _perUser.ContainsKey(userId);
        var statePath = GetEnsembleStatePath(userId);
        var hadFiles = HasModelFile(userId)
                       || (statePath is not null && _fileSystem.File.Exists(statePath));

        if (!hadCached && !hadFiles)
        {
            return false;
        }

        // This path reports eviction as attempted regardless of the delete outcome; the confirmed-removal
        // signal only matters to the stale sweep, which counts it.
        _ = EvictUser(userId);
        _pluginLog.LogInfo(LogSource, $"Evicted per-user model for user {userId:N} (below the per-user threshold).", _logger);
        return true;
    }

    /// <inheritdoc />
    public int EvictStaleModels(DateTime cutoffUtc)
        => EvictInactiveModelsCore(cutoffUtc, null);

    /// <inheritdoc />
    public int EvictInactiveModels(DateTime cutoffUtc, IReadOnlyDictionary<Guid, DateTime?> lastActivityUtc)
    {
        ArgumentNullException.ThrowIfNull(lastActivityUtc);
        return EvictInactiveModelsCore(cutoffUtc, lastActivityUtc);
    }

    private int EvictInactiveModelsCore(DateTime cutoffUtc, IReadOnlyDictionary<Guid, DateTime?>? lastActivityUtc)
    {
        if (_dataPath is null || !_fileSystem.Directory.Exists(_dataPath))
        {
            return 0;
        }

        var evicted = 0;

        try
        {
            // Walk the weights files rather than the cached instances: a stale user is precisely one the
            // training pass never revisits, so they are usually not materialized this session and only exist
            // on disk. The state file's last-trained time drives the decision, so a user retrained within the
            // window (which rewrites that time) is never seen as stale here.
            var processed = new HashSet<Guid>();
            var modelFiles = _fileSystem.Directory.EnumerateFiles(_dataPath, "ml_weights_*.json")
                .Concat(_fileSystem.Directory.EnumerateFiles(_dataPath, "ensemble_state_*.json.accepted-model.json")).ToArray();
            foreach (var file in modelFiles)
            {
                var match = PerUserFileIdPattern().Match(Path.GetFileName(file));
                if (match.Success && Guid.TryParseExact(match.Groups["id"].Value, "N", out var id) && processed.Add(id))
                {
                    evicted += EvictStaleWeightsFile(id, file, cutoffUtc, lastActivityUtc) ? 1 : 0;
                }
            }

            // A state file with no companion weights file is not a model the read path can ever rebuild, so it
            // is leftover metadata rather than something to age. This survives the weights walk above (which
            // only sees ids that still have weights) and PruneOrphans (which keeps files for live users), so
            // clean it up here regardless of age.
            foreach (var file in _fileSystem.Directory.EnumerateFiles(_dataPath, "ensemble_state_*.json"))
            {
                RemoveOrphanedEnsembleState(file);
            }
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, $"Stale per-user model eviction failed: {ex.Message}", ex, _logger);
        }

        return evicted;
    }

    /// <summary>Evicts one stale weights file, counting it only when deletion is confirmed.</summary>
    /// <param name="id">The user identity already validated by the filename scan.</param>
    /// <param name="file">The per-user weights file.</param>
    /// <param name="cutoffUtc">The oldest acceptable last-trained time.</param>
    /// <param name="lastActivityUtc">Authoritative observed activity, or null for legacy compatibility only.</param>
    /// <returns>True when the stale model was removed.</returns>
    private bool EvictStaleWeightsFile(Guid id, string file, DateTime cutoffUtc, IReadOnlyDictionary<Guid, DateTime?>? lastActivityUtc)
    {
        var lastTrained = ResolveRetirementActivity(id, file, lastActivityUtc);
        if (lastTrained is null)
        {
            // Unknown age must not delete a possibly healthy model after a transient metadata failure.
            _pluginLog.LogWarning(LogSource, $"Skipping stale check for user {id:N}: activity/legacy age is unknown.", logger: _logger);
            return false;
        }

        if (lastTrained >= cutoffUtc || !EvictUser(id))
        {
            return false;
        }

        _pluginLog.LogInfo(LogSource, $"Evicted inactive per-user model for user {id:N} (activity before {cutoffUtc:O}).", _logger);
        return true;
    }

    private DateTime? ResolveRetirementActivity(Guid userId, string weightsFile, IReadOnlyDictionary<Guid, DateTime?>? activity)
    {
        if (activity is null)
        {
            return ResolveLastTrainedUtc(userId, weightsFile);
        }

        return activity.TryGetValue(userId, out var lastActivity) ? lastActivity : null;
    }

    /// <summary>Removes state-only metadata that cannot rebuild a per-user model.</summary>
    /// <param name="file">The per-user ensemble state file.</param>
    private void RemoveOrphanedEnsembleState(string file)
    {
        if (file.EndsWith(".assessment.json", StringComparison.Ordinal))
        {
            return;
        }

        var match = PerUserFileIdPattern().Match(Path.GetFileName(file));
        if (!match.Success || !Guid.TryParseExact(match.Groups["id"].Value, "N", out var id))
        {
            return;
        }

        if (HasModelFile(id))
        {
            return;
        }

        try
        {
            _fileSystem.File.Delete(file);
            _pluginLog.LogInfo(LogSource, $"Removed orphaned ensemble state for user {id:N} with no weights file.", _logger);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, $"Could not remove orphaned ensemble state '{file}': {ex.Message}", ex, _logger);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Dispose only the per-user ensembles that were actually built. Each was constructed with
        // ownsNeural:false, so this leaves the shared neural (owned by the global ensemble) intact.
        // Touching a never-evaluated Lazy would construct an ensemble just to dispose it.
        foreach (var lazy in _perUser.Values.Where(lazy => lazy.IsValueCreated))
        {
            lazy.Value.Dispose();
        }

        _perUser.Clear();
    }

    /// <summary>
    ///     Builds a fresh per-user ensemble. When no per-user weights file exists yet, the learned model is
    ///     warm-started from the global learned model so a new user begins from the global fit; when a file
    ///     exists, the learned ctor loads it and no seeding happens.
    /// </summary>
    /// <param name="userId">The user to build an ensemble for.</param>
    /// <returns>The constructed per-user ensemble.</returns>
    private EnsembleScoringStrategy BuildPerUserEnsemble(Guid userId)
    {
        var learnedPath = GetLearnedWeightsPath(userId);
        var statePath = GetEnsembleStatePath(userId);

        var learned = new LearnedScoringStrategy(learnedPath, _logger);
        if (!learned.HasOwnWeights)
        {
            // No persisted per-user weights: start from the global fit rather than cold defaults.
            learned.SeedFrom(_globalEnsemble.LearnedStrategy);
        }

        // The heuristic sub-strategy MUST have its genre penalty disabled (floor = 1.0); the ensemble applies
        // the penalty centrally. It is stateless, so the single shared instance is reused here. The shared
        // neural is passed by reference with ownsNeural:false.
        // Hold the bounds lock across construction so a concurrent Reconfigure cannot slip in after the bounds
        // are read but before the ensemble exists. Reconfigure takes the same lock and only reaches a built
        // ensemble through IsValueCreated, so serializing here guarantees this instance is either constructed
        // with the latest bounds or reconfigured by the writer once it becomes visible, never left stale.
        lock (_blendBoundsLock)
        {
            var bounds = _blendBounds;
            var ensemble = new EnsembleScoringStrategy(
                learned,
                _sharedHeuristic,
                _sharedNeural,
                statePath,
                bounds.AlphaMin,
                bounds.AlphaMax,
                bounds.GenrePenaltyFloor,
                _logger,
                ownsNeural: false);
            ensemble.SetSharedPredictionProvider(_globalEnsemble.GetAcceptedNeuralPrediction);
            return ensemble;
        }
    }

    /// <summary>
    ///     Deletes the file when its embedded user id is not in the live set, evicting any cached instance.
    /// </summary>
    /// <param name="file">The per-user file path.</param>
    /// <param name="live">The set of live user ids.</param>
    private void PruneIfOrphan(string file, HashSet<Guid> live)
    {
        var match = PerUserFileIdPattern().Match(Path.GetFileName(file));
        if (!match.Success || !Guid.TryParseExact(match.Groups["id"].Value, "N", out var id))
        {
            return;
        }

        if (live.Contains(id))
        {
            return;
        }

        try
        {
            _fileSystem.File.Delete(file);
            RemoveCachedEnsemble(id);

            _pluginLog.LogInfo(LogSource, $"Pruned per-user model file for removed user {id:N}.", _logger);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, $"Could not prune per-user file '{file}': {ex.Message}", ex, _logger);
        }
    }

    /// <summary>
    ///     Removes the cached instance for a user and deletes both persisted files. Best-effort: individual
    ///     file failures are logged and swallowed so a locked or already-gone file does not leave the cache
    ///     inconsistent. Returns whether the weights file is confirmed gone, because that file is what the read
    ///     path keys on when rebuilding a per-user model; a failed weights delete means the model is not really
    ///     retired.
    /// </summary>
    /// <param name="userId">The user to evict.</param>
    /// <returns>True when the weights file no longer exists after the attempt.</returns>
    private bool EvictUser(Guid userId)
    {
        // Delete the persisted files before dropping the cached instance. The read path resolves a per-user
        // model only when the weights file exists and re-checks that file after building, so deleting first
        // means a score racing this eviction either sees the file already gone (and stays on the global model)
        // or discards its freshly built instance on the re-check. Removing the cache entry first would leave
        // that ordering open to a stray rebuild that outlives the eviction.
        var weightsPath = GetLearnedWeightsPath(userId);
        var statePath = GetEnsembleStatePath(userId);
        var bundlePath = AcceptedModelStore.GetBundlePath(statePath);
        foreach (var path in new[] { weightsPath, statePath, bundlePath, statePath is null ? null : statePath + ".assessment.json", bundlePath is null ? null : bundlePath + ".history.0.json", bundlePath is null ? null : bundlePath + ".history.1.json" })
        {
            if (path is null)
            {
                continue;
            }

            try
            {
                if (_fileSystem.File.Exists(path))
                {
                    _fileSystem.File.Delete(path);
                }
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _pluginLog.LogWarning(LogSource, $"Could not delete per-user file '{path}': {ex.Message}", ex, _logger);
            }
        }

        RemoveCachedEnsemble(userId);

        // The weights file is the source of truth for a per-user model. If a delete failure left it on disk the
        // model is not actually retired and the read path can rebuild it, so report that back to the caller.
        return !HasModelFile(userId);
    }

    private void RemoveCachedEnsemble(Guid userId)
    {
        if (_perUser.TryRemove(userId, out var cached) && cached.IsValueCreated)
        {
            cached.Value.Dispose();
        }
    }

    /// <summary>
    ///     Resolves when a user's per-user model was last trained. Reads the <c>UpdatedAt</c> stamp from the
    ///     ensemble-state file, which is written only by a training save, so it is a true last-trained time. A
    ///     minimal projection is used so a state-schema change does not break this read. Falls back to the
    ///     weights file's last write time when the state file is missing or unreadable.
    /// </summary>
    /// <param name="userId">The user whose model is being aged.</param>
    /// <param name="weightsFile">The user's weights file, used for the write-time fallback.</param>
    /// <returns>The last-trained time in UTC, or <see langword="null"/> when nothing readable can determine it.</returns>
    private DateTime? ResolveLastTrainedUtc(Guid userId, string weightsFile)
    {
        var statePath = GetEnsembleStatePath(userId);
        var acceptedPath = AcceptedModelStore.GetBundlePath(statePath);
        if (acceptedPath is not null && _fileSystem.File.Exists(acceptedPath))
        {
            // An accepted training date is not evidence of user inactivity. Production supplies activity
            // separately; compatibility callers without it must retain an independently accepted model.
            return null;
        }

        if (statePath is not null && _fileSystem.File.Exists(statePath))
        {
            try
            {
                var json = _fileSystem.File.ReadAllText(statePath);
                var stamp = JsonSerializer.Deserialize<LastTrainedProjection>(json)?.UpdatedAt;
                if (!string.IsNullOrEmpty(stamp)
                    && DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    return parsed.ToUniversalTime();
                }
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                _pluginLog.LogWarning(LogSource, $"Could not read last-trained time for user {userId:N}: {ex.Message}", ex, _logger);
            }
        }

        try
        {
            return _fileSystem.File.GetLastWriteTimeUtc(weightsFile);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, $"Could not read weights file time for user {userId:N}: {ex.Message}", ex, _logger);
            return null;
        }
    }

    /// <summary>Builds the per-user learned-weights path, or null when no data path is configured.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The path, or null.</returns>
    private string? GetLearnedWeightsPath(Guid userId) =>
        _dataPath is null
            ? null
            : Path.Join(_dataPath, string.Create(CultureInfo.InvariantCulture, $"ml_weights_{userId:N}.json"));

    private bool HasModelFile(Guid userId)
    {
        var weights = GetLearnedWeightsPath(userId);
        var bundle = AcceptedModelStore.GetBundlePath(GetEnsembleStatePath(userId));
        return (weights is not null && _fileSystem.File.Exists(weights)) || (bundle is not null && _fileSystem.File.Exists(bundle));
    }

    /// <summary>Builds the per-user ensemble-state path, or null when no data path is configured.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The path, or null.</returns>
    private string? GetEnsembleStatePath(Guid userId) =>
        _dataPath is null
            ? null
            : Path.Join(_dataPath, string.Create(CultureInfo.InvariantCulture, $"ensemble_state_{userId:N}.json"));

    /// <summary>
    ///     Matches the 32-hex user id in <c>ml_weights_{id:N}.json</c> / <c>ensemble_state_{id:N}.json</c>.
    ///     Anchored so the unsuffixed global files (<c>ml_weights.json</c>, <c>ensemble_state.json</c>) never match.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"^(?:ml_weights|ensemble_state)_(?<id>[0-9a-fA-F]{32})\.json(?:\.(?:accepted-model\.json(?:\.history\.[01]\.json)?|assessment\.json|acceptance-ledger\.txt))?$", RegexOptions.CultureInvariant)]
    private static partial Regex PerUserFileIdPattern();

    /// <summary>
    ///     Minimal projection of the ensemble-state file used only to read the last-trained stamp for staleness
    ///     checks, kept independent of the full state schema so a schema change cannot break the age sweep.
    /// </summary>
    private sealed class LastTrainedProjection
    {
        [JsonPropertyName("UpdatedAt")]
        public string? UpdatedAt { get; set; }
    }
}
