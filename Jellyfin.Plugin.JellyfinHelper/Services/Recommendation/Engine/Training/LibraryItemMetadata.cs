using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;

/// <summary>
///     Item -> metadata maps built from the live library and threaded into training so watched-item
///     studios/tags/BoxSet membership resolve from the same source the serve path reads, eliminating
///     train/serve skew for the StudioMatch, TagSimilarity, ContentNearestNeighbor and
///     CollectionProgression features. When null (existing callers/tests) training behaves exactly as
///     before, resolving metadata only from the previous-recommendations cache.
/// </summary>
/// <param name="Studios">Item id -> known studio names; an empty list clears cached metadata, an absent item remains unknown.</param>
/// <param name="Tags">Item id -> known own tags; an empty list clears cached metadata, an absent item remains unknown.</param>
/// <param name="BoxSetIds">Item id -> known memberships, including explicit empty lists only after a successful authoritative query.</param>
/// <param name="TmdbIds">Optional item id -> canonical provider title id map, also available for legacy cached predictions.</param>
/// <param name="TmdbCollectionIds">Optional corroborated collection identities; zero explicitly invalidates untrusted cached IDs.</param>
/// <param name="BoxSetMembershipsComplete">False preserves cached memberships while adding resolved positive evidence; true also permits explicit empty values to clear stale data.</param>
internal readonly record struct LibraryItemMetadata(
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Studios,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Tags,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> BoxSetIds,
    IReadOnlyDictionary<Guid, int>? TmdbIds = null,
    IReadOnlyDictionary<Guid, int>? TmdbCollectionIds = null,
    bool BoxSetMembershipsComplete = true)
{
    /// <summary>Gets already observed own-title credits; absent/partial fields retain their availability and acquisition.</summary>
    public IReadOnlyDictionary<Guid, CreditMetadataSnapshot>? Credits { get; init; }

    /// <summary>Gets explicitly permitted library IDs when an excluded-library scope is active; null preserves legacy unscoped history.</summary>
    public IReadOnlySet<Guid>? AllowedItemIds { get; init; }

    /// <summary>Gets excluded library aliases that must not survive as historical choice alternatives.</summary>
    public IReadOnlySet<Guid>? ExcludedItemIds { get; init; }

    /// <summary>Gets provider movie/TV keys present only in excluded libraries, never keys with an allowed copy.</summary>
    public IReadOnlySet<(int TmdbId, string MediaType)>? ExcludedTitleKeys { get; init; }

    /// <summary>Gets current permitted provider identities when the complete library eligibility boundary was captured.</summary>
    public IReadOnlySet<(int TmdbId, string MediaType)>? AllowedTitleKeys { get; init; }

    /// <summary>Gets typed identities with physical-copy provenance for per-user library intersections.</summary>
    public IReadOnlyDictionary<Guid, (int TmdbId, string MediaType)>? TitleKeysByItemId { get; init; }

    /// <summary>Gets the same authoritative movie release sequences used by inference; only metadata is reused across historical profiles.</summary>
    public CollectionContinuationCatalog? Continuation { get; init; }

    /// <summary>Gets current provider-scoped producer snapshots; a present null withdraws stale cached evidence.</summary>
    public IReadOnlyDictionary<Guid, TitleMetadataSnapshot?>? ProducerMetadata { get; init; }

    /// <summary>Gets detached provider identities for actual BoxSets, so aliases use the same distinct-title union as external progression.</summary>
    public IReadOnlyDictionary<Guid, int>? BoxSetProviderIds { get; init; }
}
