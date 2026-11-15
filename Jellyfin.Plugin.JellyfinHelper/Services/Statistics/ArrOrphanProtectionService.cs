using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

/// <summary>
///     Adds optional Arr ownership to orphan diagnostics. All instances share an eight-second
///     budget; no lookup is performed per directory and failed lookups cannot prove an orphan.
/// </summary>
public sealed class ArrOrphanProtectionService : IArrOrphanProtectionService
{
    private readonly IArrIntegrationService _arrService;
    private readonly ISecretProtector _secretProtector;
    private readonly IPluginLogService _pluginLog;
    private readonly ICleanupConfigHelper _configHelper;
    private readonly ILogger<ArrOrphanProtectionService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ArrOrphanProtectionService"/> class.</summary>
    /// <param name="arrService">Existing secured Arr HTTP integration.</param>
    /// <param name="secretProtector">Stored API-key decryption.</param>
    /// <param name="pluginLog">Plugin diagnostics.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="configHelper">Existing trash-path resolution and its safety fallbacks.</param>
    public ArrOrphanProtectionService(
        IArrIntegrationService arrService,
        ISecretProtector secretProtector,
        IPluginLogService pluginLog,
        ILogger<ArrOrphanProtectionService> logger,
        ICleanupConfigHelper configHelper)
    {
        _arrService = arrService;
        _secretProtector = secretProtector;
        _pluginLog = pluginLog;
        _logger = logger;
        _configHelper = configHelper;
    }

    /// <inheritdoc />
    public async Task<OrphanProtectionSnapshot> GetSnapshotAsync(
        PluginConfiguration config,
        IReadOnlyList<VirtualFolderInfo> libraries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(libraries);
        cancellationToken.ThrowIfCancellationRequested();
        var instances = config.GetEffectiveRadarrInstances().Select(instance => (Instance: instance, IsSonarr: false))
            .Concat(config.GetEffectiveSonarrInstances().Select(instance => (Instance: instance, IsSonarr: true)))
            .ToList();
        if (instances.Count == 0)
        {
            return OrphanProtectionSnapshot.Empty;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(8));
        var results = await Task.WhenAll(instances.Select(instance => LoadInstanceAsync(
            instance.Instance, instance.IsSonarr, config, libraries, budget.Token, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new OrphanProtectionSnapshot(results.SelectMany(result => result.Managed), results.SelectMany(result => result.Uncertain));
    }

    private async Task<InstanceProtection> LoadInstanceAsync(
        ArrInstanceConfig instance,
        bool isSonarr,
        PluginConfiguration config,
        IReadOnlyList<VirtualFolderInfo> libraries,
        CancellationToken budgetToken,
        CancellationToken userToken)
    {
        var excluded = CleanupConfigHelper.ParseCommaSeparated(config.ExcludedLibraries);
        var assigned = CleanupConfigHelper.ParseCommaSeparated(instance.Libraries);
        var localRoots = libraries
            .Where(library => CleanupConfigHelper.IsCleanupEligibleLibrary(library, excluded))
            .Where(library => assigned.Count > 0
                ? assigned.Contains(library.Name ?? string.Empty)
                : library.CollectionType == (isSonarr ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies))
            .SelectMany(library => library.Locations ?? [])
            .Where(path => !CleanupConfigHelper.IsCollectionsPath(path))
            .Select(path => OrphanProtectionSnapshot.TryNormalizePath(path, out var normalized) ? normalized : null)
            .OfType<string>()
            .Distinct(PathComparison.Comparer)
            .ToArray();
        if (localRoots.Length == 0)
        {
            return new InstanceProtection([], []);
        }

        try
        {
            var apiKey = _secretProtector.Unprotect(instance.ApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Unavailable(localRoots);
            }

            List<string>? paths;
            if (isSonarr)
            {
                var series = await _arrService.GetSonarrSeriesAsync(instance.Url, apiKey, instance.SkipCertificateValidation, budgetToken).ConfigureAwait(false);
                paths = series?.Select(item => item.Path).ToList();
            }
            else
            {
                var movies = await _arrService.GetRadarrMoviesAsync(instance.Url, apiKey, instance.SkipCertificateValidation, budgetToken).ConfigureAwait(false);
                paths = movies?.Select(item => item.Path).ToList();
            }

            if (paths == null)
            {
                return Unavailable(localRoots);
            }

            if (paths.Count == 0)
            {
                return new InstanceProtection([], []);
            }

            var roots = await _arrService.GetRootFoldersAsync(instance.Url, apiKey, instance.SkipCertificateValidation, budgetToken).ConfigureAwait(false);
            if (roots == null)
            {
                return Unavailable(localRoots);
            }

            return MapDirectories(paths, roots, localRoots, config, assigned.Count > 0);
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            // Do not log keys, endpoint query strings or exception bodies containing credentials.
            return Unavailable(localRoots);
        }
    }

    private InstanceProtection MapDirectories(List<string> paths, List<string> roots, string[] localRoots, PluginConfiguration config, bool explicitlyAssigned)
    {
        var remoteRoots = roots.Select(NormalizeRemotePath).OfType<string>().Distinct(PathComparison.Comparer).ToArray();
        var mappings = new List<(string Remote, string Local)>();
        foreach (var remote in remoteRoots)
        {
            var exact = localRoots.Where(local => string.Equals(NormalizeRemotePath(local), remote, PathComparison.Comparison)).ToArray();
            if (exact.Length == 1)
            {
                mappings.Add((remote, exact[0]));
                continue;
            }

            var name = remote.Split('/').Last();
            // Mount aliases can differ in case; uniqueness is checked case-insensitively,
            // while the resulting local paths retain the filesystem's own comparison rules.
            var candidates = localRoots.Where(local => string.Equals(Path.GetFileName(local), name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1 && remoteRoots.Count(root => string.Equals(root.Split('/').Last(), name, StringComparison.OrdinalIgnoreCase)) == 1)
            {
                mappings.Add((remote, candidates[0]));
            }
        }

        if (explicitlyAssigned && localRoots.Length == 1 && mappings.Count == 0)
        {
            var activeRoots = remoteRoots.Where(root => paths.Select(NormalizeRemotePath).OfType<string>()
                .Any(path => path.StartsWith(root.TrimEnd('/') + "/", PathComparison.Comparison))).ToArray();
            if (activeRoots.Length == 1)
            {
                mappings.Add((activeRoots[0], localRoots[0]));
            }
        }

        var uncertain = localRoots.Except(mappings.Select(mapping => mapping.Local), PathComparison.Comparer).ToList();
        var managed = new List<string>();
        foreach (var path in paths)
        {
            var remotePath = NormalizeRemotePath(path);
            if (remotePath == null)
            {
                uncertain.AddRange(localRoots);
                continue;
            }

            // The most specific root wins; a root folder itself is not a movie/series entry.
            var matches = mappings.Where(mapping => remotePath.StartsWith(
                    mapping.Remote.TrimEnd('/') + "/", PathComparison.Comparison))
                .OrderByDescending(mapping => mapping.Remote.Length).ToList();
            if (matches.Count == 0)
            {
                continue;
            }

            var mapping = matches[0];
            var relative = remotePath[(mapping.Remote.TrimEnd('/').Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
            var localPath = Path.GetFullPath(Path.Join(mapping.Local, relative));
            var trashName = (config.TrashFolderPath ?? string.Empty).Trim();
            var trashPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_configHelper.GetTrashPath(mapping.Local)));
            if (OrphanProtectionSnapshot.IsWithin(localPath, mapping.Local)
                && !OrphanProtectionSnapshot.IsExcludedMediaPath(localPath, mapping.Local, trashPath, trashName))
            {
                managed.Add(localPath);
            }
        }

        if (uncertain.Count > 0)
        {
            _ = Unavailable(uncertain);
        }

        return new InstanceProtection(managed, uncertain);
    }

    private InstanceProtection Unavailable(IReadOnlyList<string> roots)
    {
        _pluginLog.LogWarning("MediaStatistics", "Optional Arr orphan protection unavailable or ambiguous; orphan verdicts are withheld in the affected library roots. File statistics remain available.", logger: _logger);
        return new InstanceProtection([], roots);
    }

    private static string? NormalizeRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var portable = path.Replace('\\', '/');
        var normalized = portable.TrimEnd('/');
        if (normalized.Length == 0)
        {
            return path[0] == '/' ? "/" : null;
        }

        if (normalized.Length == 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && portable.Length > 2 && portable[2] == '/')
        {
            normalized += '/';
        }

        if (!normalized.StartsWith('/') && !(normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/'))
        {
            return null;
        }

        return normalized.Split('/').Any(segment => segment is "." or "..") ? null : normalized;
    }

    private sealed record InstanceProtection(IReadOnlyList<string> Managed, IReadOnlyList<string> Uncertain);
}
