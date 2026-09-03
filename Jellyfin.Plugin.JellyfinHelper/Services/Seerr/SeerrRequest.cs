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
    ///     Gets or sets the creation timestamp of the request (ISO 8601 UTC). Nullable: an absent createdAt key deserializes to null rather than MinValue, so the cleanup age gate can fail CLOSED (preserve the request) instead of treating an unknown date as ancient and deleting it.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    ///     Gets or sets the request status: 1 = pending, 2 = approved, 3 = declined. This is the
    ///     request's own lifecycle status and is separate from the media availability status on
    ///     <see cref="SeerrMedia.Status"/> (see <see cref="SeerrMediaStatus"/>).
    /// </summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }

    /// <summary>
    ///     Gets or sets the associated media information.
    /// </summary>
    [JsonPropertyName("media")]
    public SeerrMedia? Media { get; set; }
}