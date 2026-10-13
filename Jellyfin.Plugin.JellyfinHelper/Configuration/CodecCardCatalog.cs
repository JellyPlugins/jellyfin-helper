using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Jellyfin.Plugin.JellyfinHelper.Configuration;

/// <summary>
///     Single source of truth for the Codecs-tab donut cards: their canonical identifiers
///     and the one normalization rule shared by the API endpoint and the backup pipeline.
/// </summary>
public static class CodecCardCatalog
{
    private static readonly string[] CanonicalOrder =
    [
        "containers",
        "resolutions",
        "dynamicRanges",
        "videoCodecs",
        "videoAudioCodecs",
        "videoBitrate",
        "watched",
        "audioLanguages",
        "subtitleLanguages",
        "musicAudioCodecs",
        "bookFormats",
    ];

    private static readonly HashSet<string> KnownIds = new(CanonicalOrder, StringComparer.Ordinal);

    // Read-only wrapper so Canonical can never hand a caller the backing array to mutate.
    private static readonly IReadOnlyList<string> CanonicalView = Array.AsReadOnly(CanonicalOrder);

    /// <summary>
    ///     Gets the canonical card identifiers in the historical render order of the Codecs tab.
    ///     Order here defines the default layout and the fallback for unknown/missing ids.
    /// </summary>
    public static IReadOnlyList<string> Canonical => CanonicalView;

    /// <summary>
    ///     Returns true when <paramref name="id"/> is a known canonical card identifier.
    /// </summary>
    /// <param name="id">The candidate card identifier.</param>
    /// <returns>True if the id is canonical.</returns>
    public static bool IsKnown(string? id) => id is not null && KnownIds.Contains(id);

    /// <summary>
    ///     Normalizes a card order: trims, drops blanks, de-duplicates (first wins), drops unknown
    ///     ids, then appends any missing canonical ids so no card can ever be lost.
    /// </summary>
    /// <param name="input">The raw, possibly partial or dirty order.</param>
    /// <returns>A complete, de-duplicated order covering exactly the canonical set.</returns>
    [SuppressMessage(
        "Design",
        "CA1002:DoNotExposeGenericLists",
        Justification = "Consumers mutate/serialize the result as a List; Collection<T> breaks JSON round-trip.")]
    public static List<string> Normalize(IEnumerable<string>? input)
    {
        var result = new List<string>(CanonicalOrder.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (input is not null)
        {
            foreach (var raw in input)
            {
                var id = raw?.Trim();
                if (string.IsNullOrEmpty(id) || !KnownIds.Contains(id) || !seen.Add(id))
                {
                    continue;
                }

                result.Add(id);
            }
        }

        // Any canonical id the input omitted is appended in canonical order so the layout stays complete.
        result.AddRange(CanonicalOrder.Where(id => !seen.Contains(id)));
        return result;
    }
}
