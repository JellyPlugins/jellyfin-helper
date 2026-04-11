using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Statistics for a single library.
/// </summary>
[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class LibraryStatistics
{
    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collection type.
    /// </summary>
    public string CollectionType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the total video file size in bytes.
    /// </summary>
    public long VideoSize { get; set; }

    /// <summary>
    /// Gets or sets the number of video files.
    /// </summary>
    public int VideoFileCount { get; set; }

    /// <summary>
    /// Gets or sets the total subtitle file size in bytes.
    /// </summary>
    public long SubtitleSize { get; set; }

    /// <summary>
    /// Gets or sets the number of subtitle files.
    /// </summary>
    public int SubtitleFileCount { get; set; }

    /// <summary>
    /// Gets or sets the total image file size in bytes.
    /// </summary>
    public long ImageSize { get; set; }

    /// <summary>
    /// Gets or sets the number of image files.
    /// </summary>
    public int ImageFileCount { get; set; }

    /// <summary>
    /// Gets or sets the total NFO/metadata file size in bytes.
    /// </summary>
    public long NfoSize { get; set; }

    /// <summary>
    /// Gets or sets the number of NFO/metadata files.
    /// </summary>
    public int NfoFileCount { get; set; }

    /// <summary>
    /// Gets or sets the total audio file size in bytes.
    /// </summary>
    public long AudioSize { get; set; }

    /// <summary>
    /// Gets or sets the number of audio files.
    /// </summary>
    public int AudioFileCount { get; set; }

    /// <summary>
    /// Gets or sets the total trickplay data size in bytes.
    /// </summary>
    public long TrickplaySize { get; set; }

    /// <summary>
    /// Gets or sets the number of trickplay folders.
    /// </summary>
    public int TrickplayFolderCount { get; set; }

    /// <summary>
    /// Gets or sets the total size of other/unrecognized files in bytes.
    /// </summary>
    public long OtherSize { get; set; }

    /// <summary>
    /// Gets or sets the number of other/unrecognized files.
    /// </summary>
    public int OtherFileCount { get; set; }

    /// <summary>
    /// Gets the total size of all files in this library in bytes.
    /// </summary>
    public long TotalSize => VideoSize + SubtitleSize + ImageSize + NfoSize + AudioSize + TrickplaySize + OtherSize;

    // === Codec/Quality Breakdown ===

    /// <summary>
    /// Gets the container format breakdown (extension → count), e.g. "MKV" → 150.
    /// </summary>
    public Dictionary<string, int> ContainerFormats { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the resolution breakdown (tier → count), e.g. "4K" → 20, "1080p" → 100.
    /// </summary>
    public Dictionary<string, int> Resolutions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video codec breakdown (codec → count), e.g. "HEVC" → 80, "H.264" → 50.
    /// </summary>
    public Dictionary<string, int> VideoCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the audio codec breakdown for video files (codec → count), e.g. "DTS" → 40, "AAC" → 30.
    /// Parsed from video filenames.
    /// </summary>
    public Dictionary<string, int> VideoAudioCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the audio codec breakdown for music files (codec → count), e.g. "FLAC" → 100, "MP3" → 50.
    /// Parsed from filename tags with extension-based fallback.
    /// </summary>
    public Dictionary<string, int> MusicAudioCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the container format size breakdown (extension → total bytes).
    /// </summary>
    public Dictionary<string, long> ContainerSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the resolution size breakdown (tier → total bytes).
    /// </summary>
    public Dictionary<string, long> ResolutionSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video codec size breakdown (codec → total bytes).
    /// </summary>
    public Dictionary<string, long> VideoCodecSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the audio codec size breakdown for video files (codec → total bytes).
    /// </summary>
    public Dictionary<string, long> VideoAudioCodecSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the audio codec size breakdown for music files (codec → total bytes).
    /// </summary>
    public Dictionary<string, long> MusicAudioCodecSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    // === Codec File Path Tracking ===

    /// <summary>
    /// Gets the video codec file paths (codec → list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> VideoCodecPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video audio codec file paths (codec → list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> VideoAudioCodecPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the music audio codec file paths (codec → list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> MusicAudioCodecPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the container format file paths (format → list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> ContainerFormatPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the resolution file paths (resolution → list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> ResolutionPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    // === Health Check Counters ===

    /// <summary>
    /// Gets or sets the number of video files without any subtitle file in the same directory.
    /// </summary>
    public int VideosWithoutSubtitles { get; set; }

    /// <summary>
    /// Gets or sets the number of video files without any image/poster in the same directory.
    /// </summary>
    public int VideosWithoutImages { get; set; }

    /// <summary>
    /// Gets or sets the number of video files without any NFO metadata in the same directory.
    /// </summary>
    public int VideosWithoutNfo { get; set; }

    /// <summary>
    /// Gets or sets the number of directories with only metadata but no video (orphaned metadata).
    /// </summary>
    public int OrphanedMetadataDirectories { get; set; }

    // === Health Check Detail Paths ===

    /// <summary>
    /// Gets the list of video file paths that have no subtitle file in the same directory.
    /// </summary>
    public Collection<string> VideosWithoutSubtitlesPaths { get; } = new();

    /// <summary>
    /// Gets the list of video file paths that have no image/poster in the same directory.
    /// </summary>
    public Collection<string> VideosWithoutImagesPaths { get; } = new();

    /// <summary>
    /// Gets the list of video file paths that have no NFO metadata in the same directory.
    /// </summary>
    public Collection<string> VideosWithoutNfoPaths { get; } = new();

    /// <summary>
    /// Gets the list of directory paths that contain only metadata but no video (orphaned metadata).
    /// </summary>
    public Collection<string> OrphanedMetadataDirectoriesPaths { get; } = new();
}
