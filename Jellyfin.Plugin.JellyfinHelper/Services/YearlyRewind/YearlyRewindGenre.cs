namespace Jellyfin.Plugin.JellyfinHelper.Services.YearlyRewind;

/// <summary>Genre allocation within a viewing month; a null name represents unknown metadata.</summary>
internal sealed record YearlyRewindGenre
{
    public string? Name { get; init; }

    public double ActiveSeconds { get; init; }
}
