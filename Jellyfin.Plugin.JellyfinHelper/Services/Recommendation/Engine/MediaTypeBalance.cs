using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Soft personal movie/series balance from completed material, separate from prediction coordinates and dislikes.</summary>
internal static class MediaTypeBalance
{
    private const double MaximumAdjustment = 0.06;
    private const double MinimumShare = 0.2;
    private const double PriorTitleCount = 4;
    private const string MovieType = "Movie";
    private const string EpisodeType = "Episode";
    private const string SeriesType = "Series";

    /// <summary>Builds a smoothed target. Missing runtimes use distinct titles rather than treating an episode as a movie.</summary>
    /// <param name="profile">Only this user's currently eligible history.</param>
    /// <param name="referenceTime">The common scoring boundary, or current UTC time.</param>
    /// <returns>A bounded series share, or null without completed movie/series evidence.</returns>
    internal static double? TargetSeriesShare(UserWatchProfile? profile, DateTime? referenceTime = null)
    {
        var now = referenceTime ?? DateTime.UtcNow;
        var units = (profile?.WatchedItems ?? []).Where(row => row.Played && row.ItemId != Guid.Empty
                && (!row.LastPlayedDate.HasValue || row.LastPlayedDate.Value <= now)
                && !(row.UserRating is > 0 and <= 5)
                && (IsType(row, MovieType) || IsType(row, SeriesType) || (IsType(row, EpisodeType) && (row.SeriesId.GetValueOrDefault() != Guid.Empty || row.SeriesTmdbId > 0))))
            .GroupBy(UnitKey).Select(group => group.OrderByDescending(row => row.LastPlayedDate).First()).ToArray();
        var titles = units.GroupBy(TitleKey).Select(group =>
        {
            // A played container is not extra material on top of its completed episodes.
            var material = group.Any(row => IsType(row, EpisodeType)) ? group.Where(row => IsType(row, EpisodeType)).ToArray() : group.ToArray();
            return (Series: !IsType(material[0], MovieType), Material: material);
        }).ToArray();
        if (titles.Length == 0)
        {
            return null;
        }

        // Compare actual durations only when both sides have complete usable runtime
        // metadata. Never impute an invented movie/episode duration for the missing side.
        var timed = titles.All(title => title.Material.All(row => !IsType(row, SeriesType) && row.RuntimeTicks > 0 && row.RuntimeTicks <= TimeSpan.TicksPerDay));
        var movies = 0.0;
        var series = 0.0;
        var evidence = 0.0;
        foreach (var title in titles)
        {
            var titleWeight = title.Material.Max(row => Weight(row.LastPlayedDate, now));
            evidence += titleWeight;
            var amount = timed ? title.Material.Sum(row => row.RuntimeTicks / (double)TimeSpan.TicksPerHour * Weight(row.LastPlayedDate, now)) : titleWeight;
            if (title.Series)
            {
                series += amount;
            }
            else
            {
                movies += amount;
            }
        }

        var total = movies + series;
        return total > 0 ? Math.Clamp(0.5 + (((series / total) - 0.5) * evidence / (evidence + PriorTitleCount)), MinimumShare, 1 - MinimumShare) : null;
    }

    /// <summary>Adds only bounded selection pressure toward the user's mix; it never changes saved scores or eligibility.</summary>
    /// <param name="isSeries">The candidate's media type.</param>
    /// <param name="selectedCount">Already selected titles.</param>
    /// <param name="selectedSeries">Already selected series.</param>
    /// <param name="targetSeriesShare">Captured personal target, or null for legacy/source-ranked selection.</param>
    /// <returns>A small signed selection adjustment; strong relevance differences can outweigh it.</returns>
    internal static double Adjustment(bool isSeries, int selectedCount, int selectedSeries, double? targetSeriesShare)
    {
        if (!targetSeriesShare.HasValue || !double.IsFinite(targetSeriesShare.Value))
        {
            return 0;
        }

        var target = Math.Clamp(targetSeriesShare.Value, MinimumShare, 1 - MinimumShare);
        var deficit = isSeries ? (target * (selectedCount + 1)) - selectedSeries : ((1 - target) * (selectedCount + 1)) - (selectedCount - selectedSeries);
        return MaximumAdjustment * Math.Clamp(deficit, -1, 1);
    }

    private static double Weight(DateTime? playedAt, DateTime now)
        => playedAt.HasValue ? Math.Exp(-Math.Log(2) * Math.Max(0, (now - playedAt.Value).TotalDays) / EngineConstants.GenreDecayHalfLifeDays) : 0.5;

    private static bool IsType(WatchedItemInfo row, string type) => string.Equals(row.ItemType, type, StringComparison.OrdinalIgnoreCase);

    private static Guid TitleKey(WatchedItemInfo row)
        => IsType(row, EpisodeType)
            ? TrainingExample.CreateTitleObservationId(Guid.Empty, row.SeriesId.GetValueOrDefault(row.ItemId), row.SeriesTmdbId, true)
            : TrainingExample.CreateTitleObservationId(Guid.Empty, row.ItemId, row.TmdbId, IsType(row, SeriesType));

    private static Guid UnitKey(WatchedItemInfo row)
    {
        if (!IsType(row, EpisodeType))
        {
            return TitleKey(row);
        }

        return row.TmdbId > 0
            ? TrainingExample.CreateObservationId(Guid.Empty, string.Create(CultureInfo.InvariantCulture, $"episode:tmdb:{row.TmdbId}"))
            : TrainingExample.CreateObservationId(Guid.Empty, $"episode:library:{row.ItemId:N}");
    }
}
