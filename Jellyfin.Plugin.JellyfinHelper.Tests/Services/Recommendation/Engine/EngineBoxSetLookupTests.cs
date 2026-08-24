using System;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the two <c>private static</c> BoxSet-resolution helpers on
///     <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>:
///     <c>BuildCandidateBoxSetLookupFresh</c> and <c>ResolveBoxSetIds</c>.
///     <para>
///         Both helpers sit on the recommendation hot path - <c>ResolveBoxSetIds</c> is
///         invoked once per candidate on every batch run (typically 5k-50k candidates in
///         a real deployment) and <c>BuildCandidateBoxSetLookupFresh</c> is the O(N)
///         driver that materialises the per-candidate lookup consumed by
///         <c>BuildWatchedBoxSetCounts</c>. A regression that either throws on a
///         legitimate BaseItem or silently drops entries would either crash the entire
///         batch or make collection-progression rewards disappear from the ensemble
///         score.
///     </para>
///     <para>
///         BUG SURFACE: both methods are wrapped in <c>catch (Exception ex) when (ex is
///         not OutOfMemoryException and not StackOverflowException)</c> so that a badly-
///         constructed <c>BaseItem</c> (e.g. one whose <c>GetParent()</c> throws because
///         the LibraryManager static hook is not initialised in a test host) NEVER blows
///         up the pipeline - the empty list is the fail-soft default. These tests pin
///         that contract by feeding raw <c>Movie()</c> instances that HAVE no parent and
///         verifying the helpers still produce sensible output rather than throwing.
///     </para>
/// </summary>
public sealed class EngineBoxSetLookupTests
{
    // ================================================================================
    // BuildCandidateBoxSetLookupFresh
    // ================================================================================

    [Fact]
    public void BuildCandidateBoxSetLookupFresh_EmptyCandidateList_ReturnsEmpty()
    {
        // The most trivial contract: the helper must not throw when given nothing, and
        // must not fabricate any keys. A regression that pre-allocated a "default" entry
        // (e.g. via `new Dictionary<..> { [Guid.Empty] = [] }`) would surface as a phantom
        // collection ID inside every subsequent BuildWatchedBoxSetCounts call.
        var result = InvokeBuildCandidateBoxSetLookupFresh([]);
        Assert.Empty(result);
    }

    [Fact]
    public void BuildCandidateBoxSetLookupFresh_MovieWithoutParent_IsSkipped_NotInLookup()
    {
        // BUG GUARD: the helper's contract is SPARSE - only candidates that resolve to
        // at least one BoxSet are stored. A raw Movie() without any parent hierarchy
        // must produce a 0-item lookup, NOT a `{ movie.Id -> [] }` entry.
        //
        // Motivation for the sparsity: downstream code does a `TryGetValue(itemId, out var boxSets)`
        // and treats "key missing" as "no signal". Storing empty lists as values would
        // still satisfy the TryGetValue call but push the enumeration into a wasted
        // `foreach` over zero items - multiplied by tens of thousands of candidates in a
        // real library that becomes measurable overhead on every batch run.
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

    // ================================================================================
    // ResolveBoxSetIds
    // ================================================================================

    [Fact]
    public void ResolveBoxSetIds_MovieWithoutParent_ReturnsEmpty()
    {
        // BUG GUARD: the helper walks the parent hierarchy (`candidate.GetParent()` in
        // a loop). A movie without a parent must produce an empty list, not throw.
        // Note: in the test host the LibraryManager static hook is not fully wired, so
        // this exercises the graceful fallback path via the `catch (Exception ...)` in
        // ResolveBoxSetIds - which is exactly the branch that protects production from
        // corrupted parent references in third-party metadata plugins.
        var movie = new Movie { Id = Guid.NewGuid() };
        var result = InvokeResolveBoxSetIds(movie);
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveBoxSetIds_ReturnedList_IsMutable()
    {
        // BUG GUARD: even though the helper produces an empty list in the fail-soft
        // branch, callers currently mutate the return value (BuildCandidateBoxSetLookupFresh
        // stores it directly in the lookup, then downstream code enumerates it). A regression
        // that switched to `Array.Empty<Guid>()` or `ImmutableList<Guid>.Empty` for the
        // "no boxes" path would silently break future callers that assume `.Add()` is legal.
        // Locking the mutability contract keeps that door open.
        var movie = new Movie { Id = Guid.NewGuid() };
        var result = InvokeResolveBoxSetIds(movie);
        // .Add on an ImmutableList/Array.Empty would throw NotSupportedException.
        result.Add(Guid.NewGuid());
        Assert.Single(result);
    }

    // ================================================================================
    // Reflection glue - both methods are `private static`.
    // ================================================================================

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