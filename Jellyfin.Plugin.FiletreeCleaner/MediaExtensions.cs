using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.FiletreeCleaner;

/// <summary>
/// Provides a shared set of known video/media file extensions.
/// </summary>
internal static class MediaExtensions
{
    /// <summary>
    /// Gets the set of known video/media file extensions (with leading dot, case-insensitive).
    /// </summary>
    internal static HashSet<string> VideoExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3g2",
        ".3gp",
        ".asf",
        ".avi",
        ".divx",
        ".dvr-ms",
        ".f4v",
        ".flv",
        ".hevc",
        ".img",
        ".iso",
        ".m2ts",
        ".m2v",
        ".m4v",
        ".mk3d",
        ".mkv",
        ".mov",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".mts",
        ".ogg",
        ".ogm",
        ".ogv",
        ".rec",
        ".rm",
        ".rmvb",
        ".ts",
        ".vob",
        ".webm",
        ".wmv",
        ".wtv"
    };
}