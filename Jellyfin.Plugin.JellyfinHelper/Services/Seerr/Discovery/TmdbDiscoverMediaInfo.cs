using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     The <c>mediaInfo</c> object Seerr attaches to a discover item for titles it already tracks.
/// </summary>
internal sealed class TmdbDiscoverMediaInfo
{
    /// <summary>
    ///     Gets or sets the Seerr media availability status (see <see cref="SeerrMediaStatus"/>).
    /// </summary>
    [JsonPropertyName("status")]
    public int Status { get; set; }
}
