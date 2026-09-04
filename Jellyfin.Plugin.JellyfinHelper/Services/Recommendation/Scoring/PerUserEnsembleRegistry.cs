using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    public PerUserEnsembleRegistry(
        EnsembleScoringStrategy globalEnsemble,
        NeuralScoringStrategy? sharedNeural,
        string? dataPath,
        EnsembleBlendBounds blendBounds,
        IPluginLogService pluginLog,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(globalEnsemble);
        ArgumentNullException.ThrowIfNull(pluginLog);

        _globalEnsemble = globalEnsemble;
        _sharedNeural = sharedNeural;
        _dataPath = dataPath;
        _blendBounds = blendBounds;
        _pluginLog = pluginLog;
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
            return existing.Value;
        }

        // Cold-start: only adopt a per-user model when its weights file is actually present on disk. A user
        // below the training threshold has no file and must score with the global model (byte-identical to
        // the previous global-only behaviour), so the read path never creates an empty per-user model.
        if (_dataPath is null || !File.Exists(GetLearnedWeightsPath(userId)))
        {
            return _globalEnsemble;
        }

        return GetOrAddPerUser(userId);
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
        lock (_blendBoundsLock)
        {
            _blendBounds = blendBounds;
        }

        foreach (var entry in _perUser.Values)
        {
            // Only reconfigure ensembles that have actually been built. Touching entry.Value on an
            // unmaterialized Lazy would construct it here for no reason.
            if (entry.IsValueCreated)
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
        // Resolve once so the snapshot and the per-user flag describe the same ensemble. The flag is derived
        // from the resolved instance itself: GetEnsembleForUser returns the shared global ensemble for a
        // cold-start user and a distinct instance otherwise, so a reference check is authoritative here (no
        // second file probe that could disagree with the resolution).
        var ensemble = GetEnsembleForUser(userId);
        var isPerUser = userId != Guid.Empty && !ReferenceEquals(ensemble, _globalEnsemble);
        return (ensemble.GetDiagnosticsSnapshot(), isPerUser);
    }

    /// <inheritdoc />
    public bool HasPerUserModel(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return false;
        }

        return _perUser.ContainsKey(userId)
            || (_dataPath is not null && File.Exists(GetLearnedWeightsPath(userId)));
    }

    /// <inheritdoc />
    public void PruneOrphans(IReadOnlyCollection<Guid> liveUserIds)
    {
        ArgumentNullException.ThrowIfNull(liveUserIds);

        if (_dataPath is null || !Directory.Exists(_dataPath))
        {
            return;
        }

        var live = new HashSet<Guid>(liveUserIds);

        try
        {
            foreach (var file in Directory.EnumerateFiles(_dataPath, "ml_weights_*.json"))
            {
                PruneIfOrphan(file, live);
            }

            foreach (var file in Directory.EnumerateFiles(_dataPath, "ensemble_state_*.json"))
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
    public void Dispose()
    {
        // Dispose only the per-user ensembles that were actually built. Each was constructed with
        // ownsNeural:false, so this leaves the shared neural (owned by the global ensemble) intact.
        // Touching a never-evaluated Lazy would construct an ensemble just to dispose it.
        foreach (var lazy in _perUser.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
            }
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
        if (learnedPath is null || !File.Exists(learnedPath))
        {
            // No persisted per-user weights: start from the global fit rather than cold defaults.
            learned.SeedFrom(_globalEnsemble.LearnedStrategy);
        }

        // The heuristic sub-strategy MUST have its genre penalty disabled (floor = 1.0); the ensemble applies
        // the penalty centrally. It is stateless, so the single shared instance is reused here. The shared
        // neural is passed by reference with ownsNeural:false.
        EnsembleBlendBounds bounds;
        lock (_blendBoundsLock)
        {
            bounds = _blendBounds;
        }

        return new EnsembleScoringStrategy(
            learned,
            _sharedHeuristic,
            _sharedNeural,
            statePath,
            bounds.AlphaMin,
            bounds.AlphaMax,
            bounds.GenrePenaltyFloor,
            _logger,
            ownsNeural: false);
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
            File.Delete(file);
            if (_perUser.TryRemove(id, out var evicted) && evicted.IsValueCreated)
            {
                evicted.Value.Dispose();
            }

            _pluginLog.LogInfo(LogSource, $"Pruned per-user model file for removed user {id:N}.", _logger);
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            _pluginLog.LogWarning(LogSource, $"Could not prune per-user file '{file}': {ex.Message}", ex, _logger);
        }
    }

    /// <summary>Builds the per-user learned-weights path, or null when no data path is configured.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The path, or null.</returns>
    private string? GetLearnedWeightsPath(Guid userId) =>
        _dataPath is null
            ? null
            : Path.Combine(_dataPath, string.Create(CultureInfo.InvariantCulture, $"ml_weights_{userId:N}.json"));

    /// <summary>Builds the per-user ensemble-state path, or null when no data path is configured.</summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The path, or null.</returns>
    private string? GetEnsembleStatePath(Guid userId) =>
        _dataPath is null
            ? null
            : Path.Combine(_dataPath, string.Create(CultureInfo.InvariantCulture, $"ensemble_state_{userId:N}.json"));

    /// <summary>
    ///     Matches the 32-hex user id in <c>ml_weights_{id:N}.json</c> / <c>ensemble_state_{id:N}.json</c>.
    ///     Anchored so the unsuffixed global files (<c>ml_weights.json</c>, <c>ensemble_state.json</c>) never match.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"^(?:ml_weights|ensemble_state)_(?<id>[0-9a-fA-F]{32})\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex PerUserFileIdPattern();
}
