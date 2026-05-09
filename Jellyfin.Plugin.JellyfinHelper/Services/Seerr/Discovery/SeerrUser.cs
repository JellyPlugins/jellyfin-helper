using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Represents a user in the Seerr system.
/// </summary>
public sealed class SeerrUser
{
    /// <summary>
    ///     Gets or sets the Seerr user ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the email address.
    /// </summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>
    ///     Gets or sets the avatar URL.
    /// </summary>
    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }
}