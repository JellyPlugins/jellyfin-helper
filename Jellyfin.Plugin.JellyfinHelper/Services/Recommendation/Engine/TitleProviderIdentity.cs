using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Typed optional cross-provider links; names and equal movie/TV numbers are never identity evidence.</summary>
internal static class TitleProviderIdentity
{
    /// <summary>Resolves identity-only boundaries through direct IDs or a compatible configured cache alias, never title names.</summary>
    /// <param name="items">Already partitioned movie and series identities, not recommendation metadata seeds.</param>
    /// <param name="cache">Optional network-free provider cache.</param>
    /// <param name="configuration">The provider configuration whose aliases are eligible.</param>
    /// <param name="cancellationToken">Cancellation checked for each local identity.</param>
    /// <returns>Distinct typed TMDb keys; unresolved aliases remain unknown.</returns>
    internal static HashSet<(int TmdbId, string MediaType)> ResolveTmdbKeys(IEnumerable<BaseItem> items, TitleMetadataCache? cache, PluginConfiguration configuration, CancellationToken cancellationToken = default)
        => ResolveTmdbKeysByItem(items, cache, configuration, cancellationToken).Values.ToHashSet();

    /// <summary>Retains physical-copy provenance for the same configured, typed provider identities.</summary>
    /// <param name="items">Already partitioned local movie/series copies.</param>
    /// <param name="cache">Optional network-free identity cache.</param>
    /// <param name="configuration">The current source scope.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Known typed provider identities keyed by concrete library item.</returns>
    internal static Dictionary<Guid, (int TmdbId, string MediaType)> ResolveTmdbKeysByItem(IEnumerable<BaseItem> items, TitleMetadataCache? cache, PluginConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var keys = new Dictionary<Guid, (int TmdbId, string MediaType)>();
        foreach (var item in items.Where(item => item is Movie or Series))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mediaType = item is Series ? TmdbLibraryMapper.TvMediaType : TmdbLibraryMapper.MovieMediaType;
            var tmdbId = TmdbLibraryMapper.TryGetTmdbId(item, out var direct)
                ? direct
                : cache?.Get(configuration, 0, mediaType, Tvdb(item), Imdb(item))?.TmdbId ?? 0;
            if (tmdbId > 0)
            {
                keys[item.Id] = (tmdbId, mediaType);
            }
        }

        return keys;
    }

    internal static int Tvdb(BaseItem item)
        => item.ProviderIds is not null && item.ProviderIds.TryGetValue("Tvdb", out var id)
            && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : 0;

    internal static string? Imdb(BaseItem item)
        => item.ProviderIds is not null && item.ProviderIds.TryGetValue("Imdb", out var id) ? NormalizeImdb(id) : null;

    internal static string? NormalizeImdb(string? id)
    {
        var trimmed = id?.Trim();
        return trimmed is not null && trimmed.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
            && trimmed.Length is > 2 and < 16 && trimmed[2..].All(char.IsAsciiDigit)
                ? trimmed.ToLowerInvariant()
                : null;
    }

    internal static bool Compatible(TitleMetadataSnapshot left, TitleMetadataSnapshot right)
        => left.MediaType == right.MediaType
            && (left.TmdbId <= 0 || right.TmdbId <= 0 || left.TmdbId == right.TmdbId)
            && (left.TvdbId <= 0 || right.TvdbId <= 0 || left.TvdbId == right.TvdbId)
            && (left.ImdbId is null || right.ImdbId is null || string.Equals(left.ImdbId, right.ImdbId, StringComparison.OrdinalIgnoreCase));

    internal static bool SharesIdentity(TitleMetadataSnapshot left, TitleMetadataSnapshot right)
        => left.MediaType == right.MediaType
            && ((left.TmdbId > 0 && left.TmdbId == right.TmdbId)
                || (left.MediaType == "tv" && left.TvdbId > 0 && left.TvdbId == right.TvdbId)
                || (left.ImdbId is not null && string.Equals(left.ImdbId, right.ImdbId, StringComparison.OrdinalIgnoreCase)));
}
