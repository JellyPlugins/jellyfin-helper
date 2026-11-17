using System;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

internal sealed class YearlyRewindMonth
{
    public DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset EndUtc { get; init; }

    public double ActiveSeconds { get; set; }

    public double EstimatedSeconds { get; set; }

    public Collection<YearlyRewindGenre>? Genres { get; init; }
}
