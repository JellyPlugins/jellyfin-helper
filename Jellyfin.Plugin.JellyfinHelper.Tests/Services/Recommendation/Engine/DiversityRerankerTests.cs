using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="DiversityReranker"/>: contract of <c>ApplyDiversityReranking</c>
///     on edge cases (empty input, zero count) that don't require live Jellyfin
///     <see cref="BaseItem"/> instances.
///     <para>
///         Full MMR behaviour with the multi-dimensional similarity blend
///         (genre 50% + studio 30% + era 20%) is exercised through integration tests
///         that construct real <see cref="BaseItem"/> derivatives; here we only lock down
///         the public contract for degenerate inputs.
///     </para>
/// </summary>
public class DiversityRerankerTests
{
    [Fact]
    public void ApplyDiversityReranking_EmptyList_ReturnsEmpty()
    {
        var result = DiversityReranker.ApplyDiversityReranking(
            new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(),
            5);
        Assert.Empty(result);
    }

    [Fact]
    public void ApplyDiversityReranking_ZeroCount_ReturnsEmpty()
    {
        var result = DiversityReranker.ApplyDiversityReranking(
            new List<(BaseItem Item, double Score, string Reason, string ReasonKey, string? RelatedItem)>(),
            0);
        Assert.Empty(result);
    }
}