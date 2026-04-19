using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr;

/// <summary>
///     Represents the response from the Seerr movie or TV detail endpoint.
///     Movies use the "title" field, TV shows use the "name" field.
/// </summary>
internal sealed class SeerrMediaDetails
{
    /// <summary>
    ///     Gets or sets the movie title (from /api/v1/movie/{tmdbId}).
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    ///     Gets or sets the TV show name (from /api/v1/tv/{tmdbId}).
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    ///     Gets the resolved display title, preferring "title" (movie) over "name" (TV).
    ///     Returns <c>null</c> if neither title nor name is available, so the caller can choose a localized fallback.
    /// </summary>
    [JsonIgnore]
    public string? DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title
        : !string.IsNullOrWhiteSpace(Name) ? Name
        : null;
}