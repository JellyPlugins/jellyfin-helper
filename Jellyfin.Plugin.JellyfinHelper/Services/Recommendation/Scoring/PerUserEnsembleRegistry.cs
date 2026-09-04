using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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
    private readonly double _alphaMin;
    private readonly double _alphaMax;
    private readonly double _genrePenaltyFloor;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<Guid, EnsembleScoringStrategy> _perUser = new();

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
    /// <param name="alphaMin">Minimum blending factor for per-user ensembles.</param>
    /// <param name="alphaMax">Maximum blending factor for per-user ensembles.</param>
    /// <param name="genrePenaltyFloor">Genre penalty floor for per-user ensembles.</param>
    /// <param name="pluginLog">The plugin log service.</param>
    /// <param name="logger">Optional logger forwarded to each per-user ensemble.</param>
    public PerUserEnsembleRegistry(
        EnsembleScoringStrategy globalEnsemble,
        NeuralScoringStrategy? sharedNeural,
        string? dataPath,
        double alphaMin,
        double alphaMax,
        double genrePenaltyFloor,
        IPluginLogService pluginLog,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(globalEnsemble);
        ArgumentNullException.ThrowIfNull(pluginLog);

        _globalEnsemble = globalEnsemble;
        _sharedNeural = sharedNeural;
        _dataPath = dataPath;
        _alphaMin = alphaMin;
        _alphaMax = alphaMax;
        _genrePenaltyFloor = genrePenaltyFloor;
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
            return existing;
        }

        // Cold-start: only adopt a per-user model when its weights file is actually present on disk. A user
        // below the training threshold has no file and must score with the global model (byte-identical to
        // the previous global-only behaviour), so the read path never creates an empty per-user model.
        if (_dataPath is null || !File.Exists(GetLearnedWeightsPath(userId)))
        {
            return _globalEnsemble;
        }

        return _perUser.GetOrAdd(userId, BuildPerUserEnsemble);
    }

    /// <inheritdoc />
    public EnsembleScoringStrategy GetOrCreateTrainableEnsembleForUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return _globalEnsemble;
        }

        return _perUser.GetOrAdd(userId, BuildPerUserEnsemble);
    }

    /// <inheritdoc />
    public EnsembleDiagnostics GetDiagnostics(Guid userId) => GetEnsembleForUser(userId).GetDiagnosticsSnapshot();

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
        // Dispose only the per-user ensembles. Each was constructed with ownsNeural:false, so this leaves the
        // shared neural (owned by the global ensemble) intact.
        foreach (var ensemble in _perUser.Values)
        {
            ensemble.Dispose();
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

        // The heuristic sub-strategy MUST disable its own genre penalty (floor = 1.0); the ensemble applies
        // the penalty centrally. The shared neural is passed by reference with ownsNeural:false.
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1.0);

        return new EnsembleScoringStrategy(
            learned,
            heuristic,
            _sharedNeural,
            statePath,
            _alphaMin,
            _alphaMax,
            _genrePenaltyFloor,
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
            if (_perUser.TryRemove(id, out var evicted))
            {
                evicted.Dispose();
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
