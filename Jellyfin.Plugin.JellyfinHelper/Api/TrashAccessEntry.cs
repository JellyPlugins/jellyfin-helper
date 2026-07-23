namespace Jellyfin.Plugin.JellyfinHelper.Api;

/// <summary>Access check result for a single path in the trash access response.</summary>
public sealed class TrashAccessEntry
{
    /// <summary>Gets or sets the resolved trash path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the library root (only set for relative-path queries).</summary>
    public string? LibraryRoot { get; set; }

    /// <summary>Gets or sets a value indicating whether the path exists on disk.</summary>
    public bool Exists { get; set; }

    /// <summary>Gets or sets a value indicating whether the path is readable.</summary>
    public bool CanRead { get; set; }

    /// <summary>Gets or sets a value indicating whether the path is writable.</summary>
    public bool CanWrite { get; set; }

    /// <summary>Gets or sets a value indicating whether the path has full read+write access.</summary>
    public bool HasFullAccess { get; set; }

    /// <summary>Gets or sets the error message when access is denied; null when access is granted.</summary>
    public string? ErrorMessage { get; set; }
}
