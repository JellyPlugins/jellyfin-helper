using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Response model for Seerr /api/v1/movie/{id} and /api/v1/tv/{id} endpoints.
///     Contains credits (cast/crew) data needed for people-based scoring.
/// </summary>
internal sealed class SeerrMediaDetailResponse
{
    /// <summary>
    ///     Gets or sets the TMDb ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    ///     Gets or sets the credits information containing cast and crew.
    /// </summary>
    [JsonPropertyName("credits")]
    public SeerrCredits? Credits { get; set; }
}