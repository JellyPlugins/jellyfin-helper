using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Timeline;

/// <summary>
///     Contains the aggregated library insights: largest entries and recently changed/added entries.
/// </summary>
public sealed class LibraryInsightsResult
{
    private IReadOnlyList<LibraryInsightEntry> _largest = Array.Empty<LibraryInsightEntry>();
    private IReadOnlyList<LibraryInsightEntry> _recent = Array.Empty<LibraryInsightEntry>();
    private IReadOnlyDictionary<string, long> _librarySizes = new Dictionary<string, long>();

    /// <summary>
    ///     Gets or sets the top largest media directories sorted by size descending. Contains up to 10 movies and 10 TV shows.
    /// </summary>
    public IReadOnlyList<LibraryInsightEntry> Largest
    {
        get => _largest;
        set => _largest = value ?? Array.Empty<LibraryInsightEntry>();
    }

    /// <summary>
    ///     Gets or sets the combined total size of all entries in <see cref="Largest"/>.
    /// </summary>
    public long LargestTotalSize { get; set; }

    /// <summary>
    ///     Gets or sets media directories added or changed within the last 30 days, sorted by the relevant date descending.
    /// </summary>
    public IReadOnlyList<LibraryInsightEntry> Recent
    {
        get => _recent;
        set => _recent = value ?? Array.Empty<LibraryInsightEntry>();
    }

    /// <summary>
    ///     Gets or sets the total count of entries added or changed within the last 30 days.
    /// </summary>
    public int RecentTotalCount { get; set; }

    /// <summary>
    ///     Gets or sets per-library size totals (library name -> total bytes).
    ///     Setter coalesces null to empty to prevent NRE from deserialized data.
    /// </summary>
    public IReadOnlyDictionary<string, long> LibrarySizes
    {
        get => _librarySizes;
        set => _librarySizes = value ?? new Dictionary<string, long>();
    }

    /// <summary>
    ///     Gets or sets when this result was computed (UTC).
    /// </summary>
    public DateTime ComputedAtUtc { get; set; }
}
