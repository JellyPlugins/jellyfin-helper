using System;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Typed optional cross-provider links; names and equal movie/TV numbers are never identity evidence.</summary>
internal static class TitleProviderIdentity
{
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
