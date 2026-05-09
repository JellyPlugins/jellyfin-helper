using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     A single cast member from Seerr credits data.
/// </summary>
internal sealed class SeerrCastMember
{
    /// <summary>
    ///     Gets or sets the TMDb person ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the person's name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the character name played.
    /// </summary>
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>
    ///     Gets or sets the cast order (0 = top-billed).
    /// </summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }
}