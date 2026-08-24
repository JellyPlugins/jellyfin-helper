using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Arr;

/// <summary>
/// Result of comparing an Arr application with Jellyfin.
/// </summary>
public class ArrComparisonResult
{
    /// <summary>Gets items that exist in both systems.</summary>
    public Collection<string> InBoth { get; } = new();

    /// <summary>Gets items that exist in the Arr app (with file) but not in Jellyfin.</summary>
    public Collection<string> InArrOnly { get; } = new();

    /// <summary>Gets items that exist in the Arr app (without file) but not in Jellyfin.</summary>
    public Collection<string> InArrOnlyMissing { get; } = new();

    /// <summary>Gets items that exist in Jellyfin but not in the Arr app.</summary>
    public Collection<string> InJellyfinOnly { get; } = new();
}
