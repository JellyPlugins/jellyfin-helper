using System;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the two private static BoxSet-resolution helpers on Engine: BuildCandidateBoxSetLookupFresh and ResolveBoxSetIds.
/// </summary>
public sealed class EngineBoxSetLookupTests
{
    // BuildCandidateBoxSetLookupFresh

    [Fact]
    public void BuildCandidateBoxSetLookupFresh_EmptyCandidateList_ReturnsEmpty()
    {
        // The most trivial contract: the helper must not throw when given nothing, and must not fabricate any keys.
        var result = InvokeBuildCandidateBoxSetLookupFresh([]);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildCandidateBoxSetLookupFresh_MovieWithoutParent_IsSkipped_NotInLookup()
    {
        // BUG GUARD: the helper's contract is SPARSE - only candidates that resolve to at least one BoxSet are stored.
        var movie = new Movie { Id = Guid.NewGuid() };
        var result = InvokeBuildCandidateBoxSetLookupFresh([movie]);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildCandidateBoxSetLookupFresh_MultipleMoviesWithoutParents_ReturnsEmpty()
    {
        // Ensures the sparsity guarantee holds at scale - a list of 100 orphan movies
        // must still yield a completely empty lookup rather than 100 empty-list entries.
        var movies = new List<BaseItem>();
        for (var i = 0; i < 100; i++)
        {
            movies.Add(new Movie { Id = Guid.NewGuid() });
        }

        var result = InvokeBuildCandidateBoxSetLookupFresh(movies);
        Assert.Empty(result);
    }

    // ResolveBoxSetIds

    [Fact]
    public void ResolveBoxSetIds_MovieWithoutParent_ReturnsEmpty()
    {
        // BUG GUARD: the helper walks the parent hierarchy (`candidate.GetParent()` in a loop). A movie without a parent must produce an empty list, not throw.
        var movie = new Movie { Id = Guid.NewGuid() };
        var result = InvokeResolveBoxSetIds(movie);
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveBoxSetIds_ReturnedList_IsMutable()
    {
        // BUG GUARD: even though the helper produces an empty list in the fail-soft branch, callers currently mutate the return value (BuildCandidateBoxSetLookupFresh stores it directly in the lookup, then downstream code enumerates it).
        var movie = new Movie { Id = Guid.NewGuid() };
        var result = InvokeResolveBoxSetIds(movie);
        // .Add on an ImmutableList/Array.Empty would throw NotSupportedException.
        result.Add(Guid.NewGuid());
        Assert.Single(result);
    }

    // Reflection glue - both methods are `private static`.

    private static Dictionary<Guid, List<Guid>> InvokeBuildCandidateBoxSetLookupFresh(List<BaseItem> candidates)
    {
        var method = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine)
            .GetMethod(
                "BuildCandidateBoxSetLookupFresh",
                BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Dictionary<Guid, List<Guid>>)method!.Invoke(null, [candidates])!;
    }

    private static List<Guid> InvokeResolveBoxSetIds(BaseItem candidate)
    {
        var method = typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine)
            .GetMethod(
                "ResolveBoxSetIds",
                BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (List<Guid>)method!.Invoke(null, [candidate])!;
    }
}