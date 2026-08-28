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

    /// <summary>
    ///     Gets or sets the Jellyfin user ID associated with this Seerr user.
    ///     This is the Jellyfin GUID stored by Seerr (may or may not contain hyphens).
    /// </summary>
    [JsonPropertyName("jellyfinUserId")]
    public string? JellyfinUserId { get; set; }

    /// <summary>
    ///     Gets or sets the permission bitmask for this user. Encodes capabilities such as REQUEST, MANAGE_REQUESTS, ADMIN, etc.
    /// </summary>
    [JsonPropertyName("permissions")]
    public long Permissions { get; set; }
}