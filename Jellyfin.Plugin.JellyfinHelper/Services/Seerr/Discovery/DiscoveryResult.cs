using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Discovery results for a single user.
/// </summary>
public sealed class DiscoveryResult
{
    /// <summary>
    ///     Gets or sets the Jellyfin user ID.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    ///     Gets or sets the user's display name. Excluded from JSON serialization to avoid persisting PII in cache payloads.
    /// </summary>
    [JsonIgnore]
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the list of discovery recommendations for this user.
    /// </summary>
    [SuppressMessage("Usage", "CA1002:DoNotExposeGenericLists", Justification = "System.Text.Json round-trip")]
    public List<DiscoveryRecommendation> Recommendations { get; set; } = [];

    /// <summary>
    ///     Gets or sets the UTC timestamp when these results were generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Returns a deep copy of this result with a new, independent Recommendations list.
    /// </summary>
    /// <returns>A fully detached deep copy of this <see cref="DiscoveryResult"/>.</returns>
    public DiscoveryResult Clone()
    {
        var cloned = new DiscoveryResult
        {
            UserId = UserId,
            UserName = UserName,
            GeneratedAt = GeneratedAt,
        };
        foreach (var rec in Recommendations)
        {
            cloned.Recommendations.Add(rec.Clone());
        }

        return cloned;
    }
}