using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr;

/// <summary>
///     Tests the null-coalescing setter on Results. SeerrRequestPage is the DTO deserialised from the Seerr /api/v1/request endpoint.
/// </summary>
public sealed class SeerrRequestPageTests
{
    [Fact]
    public void Results_NullAssignment_CoalescedToEmptyList()
    {
        // BUG GUARD: a `null` Results list would cause `SeerrIntegrationService.FetchAllRequestsAsync` to throw NRE when iterating with `foreach (var r in page.Results)`.
        var sut = new SeerrRequestPage { Results = null! };
        Assert.NotNull(sut.Results);
        Assert.Empty(sut.Results);
    }

    [Fact]
    public void Results_NonNullAssignment_PreservesReference()
    {
        // Non-null input must be stored as-is - no defensive copy, no re-wrap. A regression that wrapped the input in a fresh list would break `Assert.Same` and would double the memory allocation on every page.
        var input = new List<SeerrRequest>
        {
            new() { Id = 1 },
            new() { Id = 2 }
        };
        var sut = new SeerrRequestPage { Results = input };
        Assert.Same(input, sut.Results);
    }

    [Fact]
    public void Defaults_ResultsIsEmptyList_PageInfoIsFreshInstance()
    {
        // Freshly constructed page must expose safe defaults so that consumers can
        // treat every field as non-null unconditionally.
        var sut = new SeerrRequestPage();
        Assert.NotNull(sut.Results);
        Assert.Empty(sut.Results);
        Assert.NotNull(sut.PageInfo);
    }

    [Fact]
    public void Results_Reassignment_FromNonNullToNull_ReplacesWithEmpty()
    {
        // Contract: every setter call re-evaluates the null-coalescing operator. A regression that used init-only or one-shot assignment would fail this test because the second (null) assignment would silently no-op, leaving the stale non-null value visible to callers.
        var sut = new SeerrRequestPage
        {
            Results = [new SeerrRequest { Id = 42 }]
        };
        Assert.Single(sut.Results);

        sut.Results = null!;

        Assert.NotNull(sut.Results);
        Assert.Empty(sut.Results);
    }
}