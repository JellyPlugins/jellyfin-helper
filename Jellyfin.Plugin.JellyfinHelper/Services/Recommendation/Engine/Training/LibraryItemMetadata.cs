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
internal readonly record struct LibraryItemMetadata(
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Studios,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Tags,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> BoxSetIds,
    IReadOnlyDictionary<Guid, int>? TmdbIds = null,
    IReadOnlyDictionary<Guid, int>? TmdbCollectionIds = null);
