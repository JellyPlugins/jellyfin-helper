using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr;

/// <summary>
///     Represents a single media request from the Seerr API.
/// </summary>
internal sealed class SeerrRequest
{
    /// <summary>
    ///     Gets or sets the unique request ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the creation timestamp of the request (ISO 8601 UTC).
    ///     Nullable: an absent <c>createdAt</c> key deserializes to <c>null</c> rather than
    ///     <see cref="DateTimeOffset.MinValue"/>, so the cleanup age gate can fail CLOSED
    ///     (preserve the request) instead of treating an unknown date as ancient and deleting it.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    ///     Gets or sets the request status.
    ///     1 = pending, 2 = approved, 3 = declined, 4 = available, 5 = partially available.
    ///     Only statuses 1 (pending) and 3 (declined) are candidates for cleanup deletion;
    ///     statuses 2, 4, and 5 are protected because they track downloaded content.
    /// </summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>
    ///     Gets or sets the associated media information.
    /// </summary>
    [JsonPropertyName("media")]
    public SeerrMedia? Media { get; set; }
}