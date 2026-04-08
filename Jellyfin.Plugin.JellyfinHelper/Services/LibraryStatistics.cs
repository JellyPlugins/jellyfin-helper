namespace Jellyfin.Plugin.JellyfinHelper.Services;

/// <summary>
/// Statistics for a single library.
/// </summary>
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
}