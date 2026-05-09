using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     A single crew member from Seerr credits data.
/// </summary>
internal sealed class SeerrCrewMember
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
    ///     Gets or sets the job title (e.g. "Director", "Writer").
    /// </summary>
    [JsonPropertyName("job")]
    public string? Job { get; set; }

    /// <summary>
    ///     Gets or sets the department (e.g. "Directing", "Writing").
    /// </summary>
    [JsonPropertyName("department")]
    public string? Department { get; set; }
}