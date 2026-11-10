using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     Persists and retrieves per-user discovery feedback for training consumption. Tracks which discovery items were shown, dismissed, or requested by each user.
/// </summary>
public interface IDiscoveryFeedbackStore
{
    /// <summary>
    ///     Legacy alias for recording generated candidates; this does not confirm a viewport impression.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="userName">The user's display name.</param>
    /// <param name="items">The discovery recommendations that were shown.</param>
    void RecordShown(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items);

    /// <summary>Records generated candidates without claiming that the user saw them.</summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="userName">The display name.</param>
    /// <param name="items">The generated candidates and their prediction-time features.</param>
    /// <param name="generatedAtUtc">The generation timestamp, or null for the current time.</param>
    void RecordGenerated(Guid userId, string userName, IReadOnlyList<DiscoveryRecommendation> items, DateTime? generatedAtUtc = null);

    /// <summary>Records confirmed impressions of candidates validated against the server's own delivered response.</summary>
    /// <param name="userId">The authenticated owning user.</param>
    /// <param name="items">Trusted delivered candidates, never client-supplied feature values.</param>
    /// <param name="generatedAtUtc">The timestamp of the delivered candidate generation.</param>
    void RecordImpressions(Guid userId, IReadOnlyList<DiscoveryRecommendation> items, DateTime generatedAtUtc);

    /// <summary>
    ///     Records that a user explicitly dismissed a discovery item.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="tmdbId">The TMDb ID of the dismissed item.</param>
    /// <param name="mediaType">The media type ("movie" or "tv").</param>
    void RecordDismissed(Guid userId, int tmdbId, string mediaType);

    /// <summary>Records dismissal with a detached, server-validated prediction, without claiming viewport visibility.</summary>
    /// <param name="userId">The authenticated owning user.</param>
    /// <param name="item">The exact delivered candidate acted upon.</param>
    /// <param name="generatedAtUtc">Its original prediction timestamp.</param>
    void RecordDismissed(Guid userId, DiscoveryRecommendation item, DateTime generatedAtUtc);

    /// <summary>
    ///     Records that a user requested a discovery item via Seerr.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="tmdbId">The TMDb ID of the requested item.</param>
    /// <param name="mediaType">The media type ("movie" or "tv").</param>
    void RecordRequested(Guid userId, int tmdbId, string mediaType);

    /// <summary>Records a request with the exact server-validated card prediction, independently of impressions.</summary>
    /// <param name="userId">The authenticated owning user.</param>
    /// <param name="item">The exact delivered candidate acted upon.</param>
    /// <param name="generatedAtUtc">Its original prediction timestamp.</param>
    void RecordRequested(Guid userId, DiscoveryRecommendation item, DateTime generatedAtUtc);

    /// <summary>
    ///     Marks requested items as watched when they appear in the user's watch history. Called during training data preparation to detect "requested AND watched" items.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="watchedItems">Composite keys of items the user has watched.</param>
    void MarkWatched(Guid userId, IReadOnlySet<(int TmdbId, string MediaType)> watchedItems);

    /// <summary>
    ///     Loads all discovery feedback for all users.
    ///     Used by the training data builder.
    /// </summary>
    /// <returns>All feedback results, one per user who has any feedback.</returns>
    IReadOnlyList<DiscoveryFeedbackResult> LoadAll();

    /// <summary>
    ///     Loads discovery feedback for a specific user.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>The feedback result for the user, or null if no feedback exists.</returns>
    DiscoveryFeedbackResult? LoadForUser(Guid userId);

    /// <summary>
    ///     Returns the set of (TmdbId, MediaType) pairs that the user has dismissed. Used by the discovery generation task to exclude previously-dismissed items from future recommendations.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>A set of dismissed composite keys, or an empty set if none.</returns>
    IReadOnlySet<(int TmdbId, string MediaType)> GetDismissedItems(Guid userId);

    /// <summary>
    ///     Returns the set of (TmdbId, MediaType) pairs that the user has requested.
    ///     Used by the API layer to filter consumed items from the visible pool.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <returns>A set of requested composite keys, or an empty set if none.</returns>
    IReadOnlySet<(int TmdbId, string MediaType)> GetRequestedItems(Guid userId);
}
