using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Statistics;

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
    /// Gets the list of root directory paths for this library.
    /// </summary>
    public Collection<string> RootPaths { get; } = new();

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
    /// Gets or sets the total size of eBook files in bytes.
    /// </summary>
    public long BookSize { get; set; }

    /// <summary>
    /// Gets or sets the number of eBook files (PDF, EPUB, CBZ, …).
    /// </summary>
    public int BookFileCount { get; set; }

    /// <summary>
    ///     Gets the eBook format breakdown (format label -> count), e.g. "EPUB" -> 42.
    /// </summary>
    public Dictionary<string, int> BookFormats { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the eBook format size breakdown (format label -> total bytes).
    /// </summary>
    public Dictionary<string, long> BookFormatSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the total size of all files in this library in bytes.
    /// </summary>
    public long TotalSize => VideoSize + SubtitleSize + ImageSize + NfoSize + AudioSize + TrickplaySize + BookSize + OtherSize;

    /// <summary>
    ///     Gets a reverse lookup of file path -> file size in bytes, populated for every classified
    ///     video, music-audio, and eBook file. Used by the Statistics tab's "largest files" curated
    ///     default view, which needs per-file size rather than the per-dimension aggregates above.
    /// </summary>
    public Dictionary<string, long> FileSizes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the container format breakdown (extension -> count), e.g. "MKV" -> 150.
    /// </summary>
    public Dictionary<string, int> ContainerFormats { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the resolution breakdown (tier -> count), e.g. "4K" -> 20, "1080p" -> 100.
    /// </summary>
    public Dictionary<string, int> Resolutions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video codec breakdown (codec -> count), e.g. "HEVC" -> 80, "H.264" -> 50.
    /// </summary>
    public Dictionary<string, int> VideoCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the audio codec breakdown for video files (codec -> count), e.g. "DTS-HD MA" -> 40, "AAC" -> 30.
    /// </summary>
    public Dictionary<string, int> VideoAudioCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Gets the audio codec breakdown for music files (codec -> count), e.g. "FLAC" -> 100, "MP3" -> 50.
    /// </summary>
    public Dictionary<string, int> MusicAudioCodecs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the dynamic range breakdown (range type -> count), e.g. "HDR10" -> 20, "SDR" -> 100.
    /// Extracted from Jellyfin MediaStream metadata.
    /// </summary>
    public Dictionary<string, int> DynamicRanges { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the container format size breakdown (extension -> total bytes).
    /// </summary>
    public Dictionary<string, long> ContainerSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the resolution size breakdown (tier -> total bytes).
    /// </summary>
    public Dictionary<string, long> ResolutionSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video codec size breakdown (codec -> total bytes).
    /// </summary>
    public Dictionary<string, long> VideoCodecSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the audio codec size breakdown for video files (codec -> total bytes).
    /// </summary>
    public Dictionary<string, long> VideoAudioCodecSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the audio codec size breakdown for music files (codec -> total bytes).
    /// </summary>
    public Dictionary<string, long> MusicAudioCodecSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the dynamic range size breakdown (range type -> total bytes).
    /// </summary>
    public Dictionary<string, long> DynamicRangeSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video codec file paths (codec -> list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> VideoCodecPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video audio codec file paths (codec -> list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> VideoAudioCodecPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the music audio codec file paths (codec -> list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> MusicAudioCodecPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the container format file paths (format -> list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> ContainerFormatPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the book format file paths (format -> list of file paths). Populated for eBook
    /// libraries so the codec-tab drill-down can list the files behind each book format.
    /// </summary>
    public Dictionary<string, Collection<string>> BookFormatPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the resolution file paths (resolution -> list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> ResolutionPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the real pixel dimensions per file (file path -> "widthxheight", e.g. "1920x800").
    /// Lets the resolution drill-down show the exact source dimensions behind a tier label,
    /// so a cinemascope 1920x800 file listed under 1080p reveals why it was classified there.
    /// </summary>
    public Dictionary<string, string> ResolutionDimensions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the dynamic range file paths (range type -> list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> DynamicRangePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video bitrate tier breakdown (tier -> count), e.g. "10-20 Mbps" -> 40.
    /// </summary>
    public Dictionary<string, int> VideoBitrateTiers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video bitrate tier size breakdown (tier -> total bytes).
    /// </summary>
    public Dictionary<string, long> VideoBitrateTierSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the video bitrate tier file paths (tier -> list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> VideoBitrateTierPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Gets the audio language breakdown (language -> count) for video files.
    /// One file counts once per distinct language across its audio tracks.
    /// </summary>
    public Dictionary<string, int> AudioLanguages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the audio language size breakdown (language -> total bytes) for video files.
    /// </summary>
    public Dictionary<string, long> AudioLanguageSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the audio language file paths (language -> list of file paths).
    /// </summary>
    public Dictionary<string, Collection<string>> AudioLanguagePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the subtitle language breakdown (language -> count) for embedded subtitle tracks.
    /// One file counts once per distinct embedded language; external sidecar files are excluded.
    /// </summary>
    public Dictionary<string, int> SubtitleLanguages { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the subtitle language size breakdown (language -> total bytes) for embedded subtitle tracks.
    /// </summary>
    public Dictionary<string, long> SubtitleLanguageSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the subtitle language file paths (language -> list of file paths) for embedded subtitle tracks.
    /// </summary>
    public Dictionary<string, Collection<string>> SubtitleLanguagePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the watched status breakdown (Watched vs Never watched).
    /// Kept for the simple donut; per-user filtering uses <see cref="WatchedByUserPaths"/> instead of buckets.
    /// </summary>
    public Dictionary<string, int> WatchedTiers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the watched status file paths (Watched / Never watched).
    /// </summary>
    public Dictionary<string, Collection<string>> WatchedTierPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the watched status size breakdown (Watched / Never watched).
    /// </summary>
    public Dictionary<string, long> WatchedTierSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the per-file watched-by mapping (file path -> list of usernames that have played the file).
    /// Only files watched by at least one user have an entry; never-watched files are absent.
    /// Kept for backward compatibility; new code also populates <see cref="WatchedDetails"/>.
    /// </summary>
    public Dictionary<string, Collection<string>> WatchedByUsers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the per-user watched paths (username -> list of file paths watched by that user).
    /// Enables the "Watched by" filter without forcing every file into a bucket donut.
    /// </summary>
    public Dictionary<string, Collection<string>> WatchedByUserPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the per-user watched size breakdown (username -> total bytes of files watched by that user).
    /// </summary>
    public Dictionary<string, long> WatchedByUserSizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the per-file per-user watch details (file path -> list of user details).
    /// Each entry records play count and last played date so the file drawer can show "Alice — 3 Plays, zuletzt 2024-03-01".
    /// </summary>
    public Dictionary<string, Collection<WatchedUserDetail>> WatchedDetails { get; } = new(StringComparer.Ordinal);
}
