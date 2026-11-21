using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Scheduled-only supplemental acquisition from configured ARR providers; outages never become a dependency.</summary>
public sealed class SupplementaryTitleMetadataService
{
    private readonly IArrIntegrationService _arr;
    private readonly TitleMetadataCache _cache;
    private readonly ISecretProtector _secrets;
    private readonly IPluginLogService _pluginLog;
    private readonly ILogger<SupplementaryTitleMetadataService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SupplementaryTitleMetadataService"/> class.</summary>
    /// <param name="arr">Existing bounded ARR API integration.</param>
    /// <param name="cache">Provider-scoped immutable cache.</param>
    /// <param name="secrets">Existing credential protector.</param>
    /// <param name="pluginLog">Plugin diagnostics.</param>
    /// <param name="logger">Structured logger.</param>
    public SupplementaryTitleMetadataService(IArrIntegrationService arr, TitleMetadataCache cache, ISecretProtector secrets, IPluginLogService pluginLog, ILogger<SupplementaryTitleMetadataService> logger)
    {
        _arr = arr ?? throw new ArgumentNullException(nameof(arr));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _pluginLog = pluginLog ?? throw new ArgumentNullException(nameof(pluginLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets optional metadata-language context captured by the registration factory.</summary>
    internal Func<string?> MetadataLanguage { get; init; } = () => null;

    /// <summary>Fetches only configured bulk catalogs, bounded by the caller's scheduled refresh budget.</summary>
    /// <param name="configuration">Captured provider configuration.</param>
    /// <param name="cancellationToken">The bounded background deadline.</param>
    /// <returns>A completed optional acquisition pass.</returns>
    public async Task WarmAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var language = MetadataLanguage();
        var sources = configuration.GetEffectiveRadarrInstances().Select(instance => (Instance: Capture(instance), Movie: true))
            .Concat(configuration.GetEffectiveSonarrInstances().Select(instance => (Instance: Capture(instance), Movie: false))).ToArray();
        await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (source, token) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await FetchAsync(
                () => source.Movie
                    ? FetchRadarrAsync(configuration, source.Instance, language, deadline.Token)
                    : FetchSonarrAsync(configuration, source.Instance, language, deadline.Token),
                token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static ArrInstanceConfig Capture(ArrInstanceConfig instance)
        => new() { Url = instance.Url, ApiKey = instance.ApiKey, SkipCertificateValidation = instance.SkipCertificateValidation };

    private async Task FetchRadarrAsync(PluginConfiguration configuration, ArrInstanceConfig instance, string? language, CancellationToken token)
    {
        var items = await _arr.GetRadarrMoviesAsync(instance.Url, _secrets.Unprotect(instance.ApiKey), instance.SkipCertificateValidation, token).ConfigureAwait(false);
        foreach (var item in items ?? [])
        {
            token.ThrowIfCancellationRequested();
            var snapshot = new TitleMetadataSnapshot
            {
                TmdbId = item.TmdbId,
                ImdbId = item.ImdbId,
                MediaType = "movie",
                SourceGenres = item.Genres?.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
                Overview = item.Overview,
                FetchedAtUtc = DateTime.UtcNow
            };
            _cache.StoreSupplemental(configuration, snapshot, new("radarr", instance.Url, instance.ApiKey), language);
        }
    }

    private async Task FetchSonarrAsync(PluginConfiguration configuration, ArrInstanceConfig instance, string? language, CancellationToken token)
    {
        var items = await _arr.GetSonarrSeriesAsync(instance.Url, _secrets.Unprotect(instance.ApiKey), instance.SkipCertificateValidation, token).ConfigureAwait(false);
        foreach (var item in items ?? [])
        {
            token.ThrowIfCancellationRequested();
            var snapshot = new TitleMetadataSnapshot
            {
                TmdbId = item.TmdbId,
                TvdbId = item.TvdbId,
                ImdbId = item.ImdbId,
                MediaType = "tv",
                AnimeHint = item.SeriesType is null ? null : string.Equals(item.SeriesType, "anime", StringComparison.OrdinalIgnoreCase),
                SourceGenres = item.Genres?.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
                Overview = item.Overview,
                FetchedAtUtc = DateTime.UtcNow
            };
            _cache.StoreSupplemental(configuration, snapshot, new("sonarr", instance.Url, instance.ApiKey), language);
        }
    }

    private async Task FetchAsync(Func<Task> fetch, CancellationToken token)
    {
        try
        {
            await fetch().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!exception.IsFatal())
        {
            _pluginLog.LogDebug("Recommendations", "Optional ARR title metadata unavailable; retaining Jellyfin and last-known-good provider metadata.", _logger);
        }
    }
}
