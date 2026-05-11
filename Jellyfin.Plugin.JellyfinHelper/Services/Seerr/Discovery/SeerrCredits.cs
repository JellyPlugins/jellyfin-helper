using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Credits (cast and crew) from a Seerr media detail response.
/// </summary>
internal sealed class SeerrCredits
{
    /// <summary>
    ///     Gets or sets the cast list (actors).
    /// </summary>
    [JsonPropertyName("cast")]
    public List<SeerrCastMember> Cast { get; set; } = [];

    /// <summary>
    ///     Gets or sets the crew list (directors, writers, etc.).
    /// </summary>
    [JsonPropertyName("crew")]
    public List<SeerrCrewMember> Crew { get; set; } = [];
}