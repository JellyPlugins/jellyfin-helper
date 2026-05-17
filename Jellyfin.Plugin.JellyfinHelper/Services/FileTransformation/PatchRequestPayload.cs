using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.FileTransformation;

/// <summary>
///     Payload model used by the File Transformation plugin callback.
///     The plugin passes the current file contents to the transformation function.
/// </summary>
public sealed class PatchRequestPayload
{
    /// <summary>
    ///     Gets or sets the current file contents to be transformed.
    /// </summary>
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}
