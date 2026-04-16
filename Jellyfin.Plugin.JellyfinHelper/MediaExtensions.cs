using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper;

/// <summary>
///     Provides media file extension constants and utilities.
/// </summary>
public static class MediaExtensions
{
    /// <summary>
    ///     Stream file extension used by Jellyfin for .strm link files.
    /// </summary>
    public const string StrmExtension = ".strm";

    /// <summary>
    ///     Gets the set of known video/media file extensions (with leading dot, case-insensitive).
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
    ///     Gets the set of known subtitle file extensions (with leading dot, case-insensitive).
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
    ///     Gets the set of known image file extensions (with leading dot, case-insensitive).
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
    ///     Gets the set of known metadata/NFO file extensions (with leading dot, case-insensitive).
    /// </summary>
    internal static HashSet<string> NfoExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo",
        ".xml"
    };

    /// <summary>
    ///     Gets the set of known audio/theme file extensions (with leading dot, case-insensitive).
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
        ".opus",
        ".ape",
        ".wv",
        ".mka",
        ".dsf",
        ".dff"
    };

    /// <summary>
    ///     Gets a mapping from audio file extension (with leading dot) to a human-readable codec name.
    ///     Used as a fallback when no codec tag is found in the filename.
    /// </summary>
    internal static Dictionary<string, string> AudioExtensionToCodec { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".mp3", "MP3" },
        { ".flac", "FLAC" },
        { ".wav", "WAV" },
        { ".aac", "AAC" },
        { ".ogg", "Vorbis" },
        { ".wma", "WMA" },
        { ".m4a", "AAC" },
        { ".opus", "Opus" },
        { ".ape", "APE" },
        { ".wv", "WavPack" },
        { ".mka", "Unknown" },
        { ".dsf", "DSD" },
        { ".dff", "DSD" }
    };

    /// <summary>
    ///     Gets the set of common subtitle flags that are appended to subtitle filenames
    ///     (e.g., "forced", "sdh", "hi").
    /// </summary>
    internal static HashSet<string> SubtitleFlags { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "forced", "sdh", "hi", "cc", "default", "foreign", "commentary", "full"
    };

    /// <summary>
    ///     Gets the set of well-known ISO 639-1 (2-letter) and ISO 639-2/B (3-letter) language codes
    ///     used in subtitle filenames. This is an explicit allowlist to prevent false positives
    ///     (e.g., "DTS", "HDR", "S01" would incorrectly match a naive "2-3 letter" heuristic).
    /// </summary>
    internal static HashSet<string> KnownLanguageCodes { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // ISO 639-1 (2-letter) — most common languages
        "aa", "ab", "af", "ak", "am", "an", "ar", "as", "av", "ay", "az",
        "ba", "be", "bg", "bh", "bi", "bm", "bn", "bo", "br", "bs",
        "ca", "ce", "ch", "co", "cr", "cs", "cu", "cv", "cy",
        "da", "de", "dv", "dz",
        "ee", "el", "en", "eo", "es", "et", "eu",
        "fa", "ff", "fi", "fj", "fo", "fr", "fy",
        "ga", "gd", "gl", "gn", "gu", "gv",
        "ha", "he", "hi", "ho", "hr", "ht", "hu", "hy", "hz",
        "ia", "id", "ie", "ig", "ii", "ik", "io", "is", "it", "iu",
        "ja", "jv",
        "ka", "kg", "ki", "kj", "kk", "kl", "km", "kn", "ko", "kr", "ks", "ku", "kv", "kw", "ky",
        "la", "lb", "lg", "li", "ln", "lo", "lt", "lu", "lv",
        "mg", "mh", "mi", "mk", "ml", "mn", "mr", "ms", "mt", "my",
        "na", "nb", "nd", "ne", "ng", "nl", "nn", "no", "nr", "nv", "ny",
        "oc", "oj", "om", "or", "os",
        "pa", "pi", "pl", "ps", "pt",
        "qu",
        "rm", "rn", "ro", "ru", "rw",
        "sa", "sc", "sd", "se", "sg", "si", "sk", "sl", "sm", "sn", "so", "sq", "sr", "ss", "st", "su", "sv", "sw",
        "ta", "te", "tg", "th", "ti", "tk", "tl", "tn", "to", "tr", "ts", "tt", "tw", "ty",
        "ug", "uk", "ur", "uz",
        "ve", "vi", "vo",
        "wa", "wo",
        "xh",
        "yi", "yo",
        "za", "zh", "zu",

        // ISO 639-2/B (3-letter) — most commonly used with subtitles
        "ara", "bul", "cat", "ces", "chi", "cze", "dan", "deu", "dut", "ell",
        "eng", "est", "fas", "fin", "fra", "fre", "ger", "gre", "heb", "hin",
        "hrv", "hun", "ice", "isl", "ind", "ita", "jpn", "kor", "lav", "lit",
        "mac", "mkd", "msa", "may", "nld", "nor", "per", "pol", "por", "rum",
        "ron", "rus", "slk", "slo", "slv", "spa", "srp", "swe", "tha", "tur",
        "ukr", "urd", "vie", "zho",

        // Common regional/extended tags
        "lat", "gsw", "nob", "nno", "yue", "cmn"
    };
}