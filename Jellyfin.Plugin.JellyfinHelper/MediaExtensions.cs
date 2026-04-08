using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper;

/// <summary>
/// Provides a shared set of known media file extensions by category.
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

    /// <summary>
    /// Gets the set of known subtitle file extensions (with leading dot, case-insensitive).
    /// </summary>
    internal static HashSet<string> SubtitleExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt",
        ".ass",
        ".ssa",
        ".sub",
        ".idx",
        ".vtt",
        ".smi",
        ".pgs",
        ".sup"
    };

    /// <summary>
    /// Gets the set of known image file extensions (with leading dot, case-insensitive).
    /// </summary>
    internal static HashSet<string> ImageExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".bmp",
        ".svg",
        ".webp",
        ".ico",
        ".tbn"
    };

    /// <summary>
    /// Gets the set of known metadata/NFO file extensions (with leading dot, case-insensitive).
    /// </summary>
    internal static HashSet<string> NfoExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo",
        ".xml"
    };

    /// <summary>
    /// Gets the set of known audio/theme file extensions (with leading dot, case-insensitive).
    /// </summary>
    internal static HashSet<string> AudioExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".wav",
        ".aac",
        ".ogg",
        ".wma",
        ".m4a",
        ".opus"
    };
}
