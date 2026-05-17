using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Response envelope for Seerr /api/v1/user endpoint.
/// </summary>
internal sealed class SeerrUserPage
{
    /// <summary>
    ///     Gets or sets the page info.
    /// </summary>
    [JsonPropertyName("pageInfo")]
    public SeerrUserPageInfo? PageInfo { get; set; }

    /// <summary>
    ///     Gets or sets the list of users.
    /// </summary>
    [JsonPropertyName("results")]
    public List<SeerrUser> Results { get; set; } = [];
}