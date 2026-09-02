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
/// <param name="Studios">Item id -> non-empty studio names (mirrors <c>BaseItem.Studios</c>).</param>
/// <param name="Tags">Item id -> non-empty tag names (mirrors <c>BaseItem.Tags</c>).</param>
/// <param name="BoxSetIds">Item id -> BoxSet ids, resolved exactly as the serve-time candidate BoxSet lookup.</param>
internal readonly record struct LibraryItemMetadata(
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Studios,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Tags,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> BoxSetIds);
