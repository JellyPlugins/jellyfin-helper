using System;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>One observed credit field; omitted/default arrays and partial lists cannot establish a non-match.</summary>
public sealed record CreditField
{
    /// <summary>Gets observed credits; null is unavailable and an explicit empty array is known empty.</summary>
    public ImmutableArray<PersonCredit>? People { get; init; }

    /// <summary>Gets or initializes a value indicating whether every relevant observed entry was readable.</summary>
    public bool Complete { get; init; }

    /// <summary>Gets the actual acquisition time; an unknown acquisition is never replaced by a watch time.</summary>
    public DateTime? AcquiredAtUtc { get; init; }

    /// <summary>Gets a value indicating whether an explicit observed list can establish a genuine zero.</summary>
    internal bool IsComplete => Complete && People is { IsDefault: false } entries
        && entries.All(person => person is not null && !string.IsNullOrWhiteSpace(person.Name));

    /// <summary>Returns only metadata acquired by the prediction boundary.</summary>
    /// <param name="cutoff">The historical prediction time.</param>
    /// <param name="oldest">Optional expiry boundary for a current provider projection.</param>
    /// <returns>The original immutable field or unavailable.</returns>
    internal CreditField? At(DateTime cutoff, DateTime? oldest = null)
        => AcquiredAtUtc is { } acquired && acquired != default && DateTimeNormalization.ToUtc(acquired) <= cutoff
            && (!oldest.HasValue || DateTimeNormalization.ToUtc(acquired) > oldest.Value) ? this : null;
}
