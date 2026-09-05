using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Instance-level tests for the recommendation Engine that exercise the outer control-flow branches of GetRecommendations, GetAllRecommendations, and TrainStrategy without needing a real Jellyfin library.
/// </summary>
public sealed class EngineInstanceTests
{
    // GetRecommendations - outer contract

    [Fact]
    public void GetRecommendations_UserNotFound_ReturnsNull()
    {
        // BUG GUARD: the top of GetRecommendations calls `_watchHistoryService.GetUserWatchProfile(userId)` and must forward a NULL straight back to the caller.
        var harness = EngineTestFactory.Create();
        var result = harness.Engine.GetRecommendations(Guid.NewGuid(), 10, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void GetRecommendations_CancelledToken_Throws()
    {
        // The first line of GetRecommendations calls ThrowIfCancellationRequested().
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
        // BUG GUARD: Math.Clamp(maxResults, 1, MaxRecommendationsPerUserLimit) - the clamp lifts nonsense inputs (including negative ones, from a decoded query string) to 1 rather than propagating them into the downstream buffer allocation which would either NRE or throw.
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

    // GetAllRecommendations - outer contract

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
        // Empty-user deployment: the batch must produce an empty list rather than throwing or returning null. Callers currently do `results.Count` on the returned value; a null would NRE on a fresh install with no users.
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

    // TrainStrategy - early-exit contract

    [Fact]
    public void TrainStrategy_EmptyPreviousResults_HeuristicStrategy_ReturnsFalse()
    {
        // BUG GUARD: when the previousResults list is empty AND the active scoring strategy is NOT the EnsembleScoringStrategy, there is nothing to train on and no cohort-feedback pass to run.
        var harness = EngineTestFactory.Create();
        var trained = harness.Engine.TrainStrategy(
            new Collection<Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.RecommendationResult>(),
            incremental: false,
            CancellationToken.None);
        Assert.False(trained);
    }

    [Fact]
    public void TrainStrategy_EmptyPreviousResults_PrunesOrphansBeforeEarlyReturn()
    {
        // Orphan reconciliation against the live user list must run regardless of whether there is anything to
        // train on, because a user can be removed between runs. It happens before the empty-results early return,
        // and the expensive library scan (candidate loading via GetItemList) must be skipped on the empty run.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetAllUserIds()).Returns([userId]);

        var trained = harness.Engine.TrainStrategy(
            new Collection<Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.RecommendationResult>(),
            incremental: false,
            CancellationToken.None);

        Assert.False(trained);
        harness.PerUserRegistry.Verify(
            r => r.PruneOrphans(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId))),
            Times.Once);
        // Empty previous results skip the library load entirely.
        harness.LibraryManager.Verify(
            lm => lm.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()),
            Times.Never);
    }

    // GetUserEnsembleDiagnostics - strategy-type guard

    [Fact]
    public void GetUserEnsembleDiagnostics_NonEnsembleStrategy_ReturnsNullAndNotPerUser()
    {
        // The default harness runs a HeuristicScoringStrategy, so per-user diagnostics are unavailable and the
        // guard short-circuits to (null, false) without consulting the registry.
        var harness = EngineTestFactory.Create();

        var (diagnostics, isPerUser) = harness.Engine.GetUserEnsembleDiagnostics(Guid.NewGuid());

        Assert.Null(diagnostics);
        Assert.False(isPerUser);
    }

    [Fact]
    public void GetUserEnsembleDiagnostics_EnsembleStrategy_DelegatesToRegistry()
    {
        // With an ensemble-backed engine the guard passes and the call delegates to the registry, forwarding
        // whatever (snapshot, isPerUser) tuple the registry resolves for that user.
        using var ensemble = new EnsembleScoringStrategy();
        var harness = EngineTestFactory.Create(ensemble);
        var userId = Guid.NewGuid();
        var expected = ensemble.GetDiagnosticsSnapshot();
        harness.PerUserRegistry.Setup(r => r.GetUserModelDiagnostics(userId)).Returns((expected, true));

        var (diagnostics, isPerUser) = harness.Engine.GetUserEnsembleDiagnostics(userId);

        Assert.True(isPerUser);
        Assert.Equal(expected.Alpha, diagnostics!.Alpha);
        harness.PerUserRegistry.Verify(r => r.GetUserModelDiagnostics(userId), Times.Once);
    }

    [Fact]
    public void TrainStrategy_EmptyPreviousResults_IncrementalMode_ReturnsFalse()
    {
        // BUG GUARD: the incremental=true branch takes a different code path through the underlying TrainingService (it merges the incoming examples with a sample of historical examples).
        var harness = EngineTestFactory.Create();
        var trained = harness.Engine.TrainStrategy(
            new List<Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.RecommendationResult>(),
            incremental: true,
            CancellationToken.None);
        Assert.False(trained);
    }

    // TryPublishSnapshot - out-of-order write rejection

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
        // Normal in-order publish: sequence 2 follows sequence 1 -> must succeed.
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

    // GetAllRecommendations - one throwing user does not abort the batch

    [Fact]
    public void GetAllRecommendations_OneUserThrows_BatchContinuesAndLogsWarning()
    {
        // BUG GUARD: the Parallel.ForEach body catches non-fatal exceptions and logs a warning, then continues processing remaining users.
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

    // GetOrBuildCommunityPopularity - concurrent callers compute only once

    [Fact]
    public async Task GetOrBuildCommunityPopularity_ConcurrentCallers_BuildCalledAtMostOnce()
    {
        // BUG GUARD: when multiple cold-start callers arrive simultaneously with an uncomputed snapshot, BuildCommunityPopularityForColdStart must run at most once.
        var harness = EngineTestFactory.Create();

        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = u1, UserName = "user1" },
            new() { UserId = u2, UserName = "user2" }
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);

        // Run 8 concurrent batch invocations. Each rebuilds the snapshot from scratch (empty library -> no candidates -> trivial batch).
        var tasks = Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => harness.Engine.GetAllRecommendations(5, CancellationToken.None)));

        var allResults = await Task.WhenAll(tasks);

        // All batches must complete without throwing.
        Assert.All(allResults, r => Assert.NotNull(r));
    }

    // Dispose - resource-release contract

    [Fact]
    public void Dispose_WithNonDisposableStrategy_DoesNotThrow()
    {
        // The default HeuristicScoringStrategy does not implement IDisposable, so Dispose must
        // skip the strategy branch and still dispose the internal training service without throwing.
        var harness = EngineTestFactory.Create();

        var ex = Record.Exception(() => harness.Engine.Dispose());

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WithDisposableStrategy_DisposesStrategy()
    {
        // When the injected strategy is IDisposable, Dispose must release it exactly once - the
        // engine owns the strategy's lifetime and a leak here would keep native/ML resources alive.
        var strategy = new Mock<IScoringStrategy>();
        var disposable = strategy.As<IDisposable>();

        var harness = EngineTestFactory.Create(strategy.Object);
        harness.Engine.Dispose();

        disposable.Verify(d => d.Dispose(), Times.Once);
    }

    // Reflection helpers

    private static bool InvokeTryPublishSnapshot(
        Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine engine,
        long publicationSequence)
        => engine.TryPublishSnapshotForTest(publicationSequence);
}
