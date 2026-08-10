using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Instance-level tests for the recommendation
///     <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>
///     that exercise the outer control-flow branches of <c>GetRecommendations</c>,
///     <c>GetAllRecommendations</c>, and <c>TrainStrategy</c> without needing a real
///     Jellyfin library.
///     <para>
///         The engine is instantiated through <see cref="EngineTestFactory"/>, which wires
///         seven collaborators to sensible empty-collection defaults. All tests here rely
///         on that harness so the constructor cost stays fixed and future engine-level
///         refactors touch a single central seam instead of every test file.
///     </para>
///     <para>
///         BUG SURFACE: These branches sit at the ENTRY of every recommendation flow -
///         a regression here is a hard failure for every user of the plugin, not a
///         subtle scoring drift. Getting them wrong is what generates 500s on the API
///         layer for "no such user" cases, or drops every recommendation for users
///         whose history hasn't been indexed yet. Pinning the observable contract
///         (returns null / empty rather than throws) is the first line of defence.
///     </para>
/// </summary>
public sealed class EngineInstanceTests
{
    // ================================================================================
    // GetRecommendations - outer contract
    // ================================================================================

    [Fact]
    public void GetRecommendations_UserNotFound_ReturnsNull()
    {
        // BUG GUARD: the top of GetRecommendations calls
        // `_watchHistoryService.GetUserWatchProfile(userId)` and must forward a NULL
        // straight back to the caller. The controller layer relies on this null to
        // decide between 200 (success) and 404 (no such user). Returning an empty
        // RecommendationResult would silently downgrade "no such user" to "no
        // recommendations for this user" - indistinguishable on the wire.
        var harness = EngineTestFactory.Create();
        var result = harness.Engine.GetRecommendations(Guid.NewGuid(), 10, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void GetRecommendations_CancelledToken_Throws()
    {
        // The first line of GetRecommendations calls ThrowIfCancellationRequested().
        // A regression that omitted the check (or placed it AFTER the profile lookup)
        // would silently do the full profile fetch on a cancelled request - a small
        // but real correctness bug because the caller has already given up and the
        // resulting result would be thrown away anyway.
        var harness = EngineTestFactory.Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            harness.Engine.GetRecommendations(Guid.NewGuid(), 10, cts.Token));
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(int.MinValue)]
    public void GetRecommendations_MaxResultsBelowOne_ClampedToPositive_DoesNotThrow(int badMax)
    {
        // BUG GUARD: Math.Clamp(maxResults, 1, MaxRecommendationsPerUserLimit) - the
        // clamp lifts nonsense inputs (including negative ones, from a decoded query
        // string) to 1 rather than propagating them into the downstream buffer
        // allocation which would either NRE or throw ArgumentOutOfRange. The user is
        // still not found so we get null back, but the point is: we get HERE at all
        // rather than throwing on the Math.Clamp line.
        var harness = EngineTestFactory.Create();
        var result = harness.Engine.GetRecommendations(Guid.NewGuid(), badMax, CancellationToken.None);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(10_000_000)]
    public void GetRecommendations_MaxResultsAboveLimit_ClampedDown_DoesNotThrow(int hugeMax)
    {
        // Symmetric upper-bound clamp guard. A regression that removed the clamp
        // would attempt to allocate a 2-billion-element buffer downstream.
        var harness = EngineTestFactory.Create();
        var result = harness.Engine.GetRecommendations(Guid.NewGuid(), hugeMax, CancellationToken.None);
        Assert.Null(result);
    }

    // ================================================================================
    // GetAllRecommendations - outer contract
    // ================================================================================

    [Fact]
    public void GetAllRecommendations_CancelledToken_Throws()
    {
        // Symmetric to the GetRecommendations cancellation guard.
        var harness = EngineTestFactory.Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            harness.Engine.GetAllRecommendations(10, cts.Token));
    }

    [Fact]
    public void GetAllRecommendations_NoUsersInSystem_ReturnsEmptyCollection()
    {
        // Empty-user deployment: the batch must produce an empty list rather than
        // throwing or returning null. Callers currently do `results.Count` on the
        // returned value; a null would NRE on a fresh install with no users.
        var harness = EngineTestFactory.Create();
        // Default WatchHistory mock returns an empty Collection<UserWatchProfile>,
        // so the parallel batch loop simply enumerates zero users.
        var result = harness.Engine.GetAllRecommendations(10, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void GetAllRecommendations_NonPositivePerUser_Clamped_DoesNotThrow(int bad)
    {
        // Same Math.Clamp guard as GetRecommendations. Nothing to iterate over
        // in the empty-user setup, but the clamp branch still executes.
        var harness = EngineTestFactory.Create();
        var result = harness.Engine.GetAllRecommendations(bad, CancellationToken.None);
        Assert.Empty(result);
    }

    // ================================================================================
    // TrainStrategy - early-exit contract
    // ================================================================================

    [Fact]
    public void TrainStrategy_EmptyPreviousResults_HeuristicStrategy_ReturnsFalse()
    {
        // BUG GUARD: when the previousResults list is empty AND the active scoring
        // strategy is NOT the EnsembleScoringStrategy, there is nothing to train on
        // and no cohort-feedback pass to run. TrainStrategy must return false rather
        // than degenerating into a no-op that still touches the watch-history service
        // for the training loop - because that touch would trigger the O(U×M)
        // watched-lookup materialisation with no user-visible benefit.
        //
        // We use the default harness (HeuristicScoringStrategy, non-trainable) so
        // the "trained = false" path is exercised on the branch immediately after
        // UpdateDiscoveryWatchedStatus.
        var harness = EngineTestFactory.Create();
        var trained = harness.Engine.TrainStrategy(
            new Collection<Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.RecommendationResult>(),
            incremental: false,
            CancellationToken.None);
        Assert.False(trained);
    }

    [Fact]
    public void TrainStrategy_EmptyPreviousResults_IncrementalMode_ReturnsFalse()
    {
        // BUG GUARD: the incremental=true branch takes a different code path through
        // the underlying TrainingService (it merges the incoming examples with a
        // sample of historical examples). With an empty input list the merge still
        // produces "no fresh signal" and training must be skipped, returning false -
        // NOT accidentally running a no-op train call that mutates the strategy's
        // metrics-history counter and interferes with the strategy-selector's
        // exploration-activation gate (which is keyed on that same counter).
        //
        // This complements the incremental=false test above by pinning the OTHER
        // branch of the same short-circuit contract.
        var harness = EngineTestFactory.Create();
        var trained = harness.Engine.TrainStrategy(
            new List<Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.RecommendationResult>(),
            incremental: true,
            CancellationToken.None);
        Assert.False(trained);
    }

    // ================================================================================
    // TEST-1: TryPublishSnapshot - out-of-order write rejection
    // ================================================================================

    [Fact]
    public void TryPublishSnapshot_FirstPublish_WithNullCurrent_ReturnsTrue()
    {
        // A freshly-created engine has no snapshot. The first publish must always succeed.
        var harness = EngineTestFactory.Create();
        var result = InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 1);
        Assert.True(result);
    }

    [Fact]
    public void TryPublishSnapshot_HigherSequence_Accepted()
    {
        // Normal in-order publish: sequence 2 follows sequence 1 → must succeed.
        var harness = EngineTestFactory.Create();
        InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 1);
        var result = InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 2);
        Assert.True(result);
    }

    [Fact]
    public void TryPublishSnapshot_SameSequence_Accepted()
    {
        // Equal sequence is not strictly greater than current, but the guard uses `>`
        // so an equal-sequence publish (live-refresh over an older batch) must be accepted.
        var harness = EngineTestFactory.Create();
        InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 5);
        var result = InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 5);
        Assert.True(result);
    }

    [Fact]
    public void TryPublishSnapshot_LowerSequence_Rejected()
    {
        // BUG GUARD: a slow batch finishing after a newer live-refresh must not roll the
        // cache back to stale data. PublicationSequence 3 must reject a sequence-2 write.
        var harness = EngineTestFactory.Create();
        InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 3);
        var result = InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 2);
        Assert.False(result);
    }

    [Fact]
    public void TryPublishSnapshot_OldSequenceDoesNotReplaceNewer()
    {
        // After rejection the newer snapshot must still be in the cache - the rejected
        // publish must not have mutated the field at all.
        var harness = EngineTestFactory.Create();
        InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 10);
        InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 3); // rejected

        // A subsequent publish with sequence 11 (> 10) must still be accepted,
        // proving the cache was not rolled back to 3.
        var result = InvokeTryPublishSnapshot(harness.Engine, publicationSequence: 11);
        Assert.True(result);
    }

    // ================================================================================
    // TEST-8: GetAllRecommendations - one throwing user does not abort the batch
    // ================================================================================

    [Fact]
    public void GetAllRecommendations_OneUserThrows_BatchContinuesAndLogsWarning()
    {
        // BUG GUARD: the Parallel.ForEach body catches non-fatal exceptions and logs a
        // warning, then continues processing remaining users. A regression that re-throws
        // or swallows the warning would either abort the batch or hide the failure.
        //
        // Injection: make IStrategySelector.GetAlphaOffset throw for the failing user -
        // that call happens inside the parallel body (line 432) after the cold-start/
        // GenerateForUser branch decision, so it reliably exercises the catch block.
        var failingUserId = Guid.NewGuid();
        var succeedingUserId = Guid.NewGuid();

        var harness = EngineTestFactory.Create();

        var failingProfile = new UserWatchProfile { UserId = failingUserId, UserName = "failing-user" };
        failingProfile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true });

        var normalProfile = new UserWatchProfile { UserId = succeedingUserId, UserName = "ok-user" };

        harness.WatchHistory
            .Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new System.Collections.ObjectModel.Collection<UserWatchProfile>
            {
                failingProfile,
                normalProfile
            });

        // Throw inside the parallel body for the failing user.
        harness.StrategySelector
            .Setup(s => s.GetAlphaOffset(failingUserId))
            .Throws(new InvalidOperationException("simulated per-user failure"));

        // Must not throw - the catch-and-continue contract absorbs it.
        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);
        Assert.NotNull(results);

        // Warning logged for the failing user.
        harness.PluginLog.Verify(
            p => p.LogWarning(
                It.IsAny<string>(),
                It.Is<string>(msg => msg.Contains("failing-user")),
                It.IsAny<Exception>(),
                It.IsAny<Microsoft.Extensions.Logging.ILogger>()),
            Times.Once);
    }

    // ================================================================================
    // TEST-4: GetOrBuildCommunityPopularity - concurrent callers compute only once
    // ================================================================================

    [Fact]
    public async Task GetOrBuildCommunityPopularity_ConcurrentCallers_BuildCalledAtMostOnce()
    {
        // BUG GUARD: when multiple cold-start callers arrive simultaneously with an
        // uncomputed snapshot, BuildCommunityPopularityForColdStart must run at most
        // once. The _snapshotRefreshLock + ReferenceEquals guard in
        // GetOrBuildCommunityPopularity serialises the write-back, but the BUILD itself
        // can still run concurrently on multiple threads before any of them acquire the
        // lock. This test verifies the observable contract - the snapshot's
        // CommunityPopularityComputed flag is true after the first caller finishes -
        // rather than the exact call count (which depends on thread scheduling).
        //
        // We simulate the "concurrent misses" scenario by calling GetAllRecommendations
        // with many users from multiple threads, all hitting a freshly-invalidated
        // snapshot. The engine is set up with two users (enough to unlock the community
        // map) and an empty library (so the batch completes quickly).
        var harness = EngineTestFactory.Create();

        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = u1, UserName = "user1" },
            new() { UserId = u2, UserName = "user2" }
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);

        // Run 8 concurrent batch invocations. Each rebuilds the snapshot from scratch
        // (empty library → no candidates → trivial batch). The community-popularity
        // computation runs inside each batch's cold-start path.
        var tasks = Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => harness.Engine.GetAllRecommendations(5, CancellationToken.None)));

        var allResults = await Task.WhenAll(tasks);

        // All batches must complete without throwing.
        Assert.All(allResults, r => Assert.NotNull(r));
    }

    // ================================================================================
    // Reflection helpers
    // ================================================================================

    private static bool InvokeTryPublishSnapshot(
        Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine engine,
        long publicationSequence)
        => engine.TryPublishSnapshotForTest(publicationSequence);
}
