using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     End-to-end pipeline tests that drive the FULL recommendation
///     <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>
///     through cold-start and warm code paths.
///     <para>
///         Prior rounds pinned outer contracts (null user, cancellation, clamps) with the
///         empty defaults from <see cref="EngineTestFactory"/>. Those never crossed the
///         <c>userProfile.WatchedItems.Count == 0</c> branch, so
///         <c>GenerateColdStartRecommendations</c>, <c>GenerateForUser</c>,
///         <c>ScoreCandidate</c>, <c>ResolveMediaLanguages</c> and the batch parallel
///         loop stayed at 0% coverage even though the outer control flow was fully green.
///         These tests construct real <see cref="Movie"/> instances and feed them through
///         the library manager mock so the engine actually scores something.
///     </para>
/// </summary>
public sealed class EngineFullPipelineTests
{
    private static Movie MakeMovie(
        string name,
        int? productionYear = 2020,
        string[]? genres = null,
        float? communityRating = 7.5f)
    {
        return new Movie
        {
            Id = Guid.NewGuid(),
            Name = name,
            Path = $"/media/movies/{Guid.NewGuid():N}.mkv",
            ProductionYear = productionYear,
            Genres = genres ?? ["Action", "Drama"],
            CommunityRating = communityRating,
            PremiereDate = productionYear.HasValue
                ? new DateTime(productionYear.Value, 6, 1, 0, 0, 0, DateTimeKind.Utc)
                : null,
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };
    }

    private static UserWatchProfile MakeWarmProfile(Guid userId, string userName, Guid watchedItemId)
    {
        return new UserWatchProfile
        {
            UserId = userId,
            UserName = userName,
            WatchedMovieCount = 1,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = watchedItemId,
                    Name = "Already Watched",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 2,
                    Genres = new List<string> { "Action", "Drama" }
                }
            }
        };
    }

    private static void WireLibrary(EngineTestFactory.EngineHarness harness, List<BaseItem> movies)
    {
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(movies);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns([]);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns([]);
    }

    [Fact]
    public void GetRecommendations_ColdStartUser_EmptyLibrary_ReturnsResultWithEmptyRecommendations()
    {
        // BUG GUARD: cold-start on an empty library must not throw and must produce a
        // valid - if empty - RecommendationResult. Entry test for GenerateColdStartRecommendations.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "newbie", WatchedItems = [] });

        var result = harness.Engine.GetRecommendations(userId, 5, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.Equal("newbie", result.UserName);
        Assert.Empty(result.Recommendations);
        Assert.Equal("strategyColdStart", result.ScoringStrategyKey);
    }

    [Fact]
    public void GetRecommendations_ColdStart_UnratedItem_TiesKnownMediocre_ButOutranksTrash()
    {
        // REGRESSION GUARD (audit finding coldstart-05-rating): the cold-start scalar formula must
        // NOT let a fully-unrated candidate outrank a candidate the community explicitly rated poorly.
        // Previously ComputeCombinedCriticScore returned the neutral 0.5 for unrated items, so an
        // unrated title scored 0.6*0.5=0.30 (rating term) while a real 3/10 scored 0.6*0.30=0.18 -
        // a 0.12 quality inversion. The fix substitutes the 0.30 unrated prior LOCALLY in cold-start.
        //
        // We pin BOTH boundaries of the calibration so a future mis-set prior is caught:
        //   * unrated must TIE a 3/10 exactly (0.30 prior == 3.0/10) - a one-sided "<=" would let an
        //     over-penalizing regression (e.g. prior=0.0) ship undetected, so we assert equality.
        //   * unrated must STRICTLY OUTRANK genuinely-bad content (2/10) - an unknown title is not
        //     worse than trash, and this lower-bound assertion fails if the prior is pushed too low.
        // All three movies share genre + year, so recency and diversity-reranking are equal and the
        // rating term is the sole differentiator. We assert on Score (not list position) so an
        // exact-score tie is not flipped by the reranker's deterministic ordering. This is the
        // classic single-user branch (no community prior), which the test harness exercises.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "newbie", WatchedItems = [] });

        var mediocre = MakeMovie("Known Mediocre", 2020, ["Action"], communityRating: 3.0f);
        var trash = MakeMovie("Known Trash", 2020, ["Action"], communityRating: 2.0f);
        var unrated = MakeMovie("Unknown Quality", 2020, ["Action"], communityRating: null);
        // Ensure the "unrated" item truly has NEITHER rating; rated items carry only their community score.
        unrated.CriticRating = null;
        mediocre.CriticRating = null;
        trash.CriticRating = null;

        WireLibrary(harness, [mediocre, trash, unrated]);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("strategyColdStart", result!.ScoringStrategyKey);
        Assert.NotEmpty(result.Recommendations);

        var mediocreRec = result.Recommendations.FirstOrDefault(r => r.ItemId == mediocre.Id);
        var trashRec = result.Recommendations.FirstOrDefault(r => r.ItemId == trash.Id);
        var unratedRec = result.Recommendations.FirstOrDefault(r => r.ItemId == unrated.Id);
        Assert.NotNull(mediocreRec);
        Assert.NotNull(trashRec);
        Assert.NotNull(unratedRec);

        // Upper boundary: unrated ties a 3/10 exactly (0.30 prior maps to the same rating term as
        // community 3.0/10). Asserting equality - not just "<=" - catches an over-penalizing regression.
        Assert.Equal(mediocreRec!.Score, unratedRec!.Score, 4);

        // Lower boundary: unrated strictly outranks genuinely-bad (2/10) content. Fails if the prior
        // is pushed below the trash band (e.g. a future 0.0), which the equality assertion alone
        // would not catch.
        Assert.True(
            unratedRec.Score > trashRec!.Score,
            $"Unrated item (score={unratedRec.Score}) must strictly outrank the community-rated 2/10 item (score={trashRec.Score}).");
    }

    [Fact]
    public void GetRecommendations_ColdStartUser_WithMovies_ExecutesPipelineAndProducesValidResult()
    {
        // Drives cold-start scoring end-to-end: rating filter, combined-critic + recency,
        // diversity reranking, RecommendedItem projection, and the graceful fallback in
        // ResolveMediaLanguages / ResolveBoxSetIds (no streams on bare Movie() instances).
        //
        // NAMING NOTE: this test used to be called "ReturnsPopulatedRecommendations" but
        // an earlier CodeRabbit review correctly pointed out that Assert.All is vacuously
        // true on an empty collection, so a green test did NOT guarantee the recs list
        // was non-empty. Rather than lock in a "must-be-non-empty" invariant that the
        // Jellyfin BaseItem plumbing cannot reliably satisfy from a unit-test host
        // (LoadCandidateItems filters out path-less items and the test-host Movie
        // instances are missing enough BaseItem state to survive that filter), we
        // renamed the test to reflect what it ACTUALLY locks in:
        //   1. The full cold-start pipeline executes without throwing.
        //   2. The result carries the "strategyColdStart" key.
        //   3. IF any recommendations survive, they carry stable per-item invariants
        //      (non-empty ItemId, "reasonPopular" reason, non-empty display Name).
        //
        // If the LoadCandidateItems filter drops every candidate, we still get to
        // exercise cold-start scoring on the pre-filter batch, which is exactly the
        // 800+ lines of previously-uncovered code this test was written to reach.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(new UserWatchProfile { UserId = userId, UserName = "newbie", WatchedItems = [] });

        var movies = new List<BaseItem>
        {
            MakeMovie("Popular A", 2023, ["Action"], 8.5f),
            MakeMovie("Popular B", 2022, ["Drama"], 7.8f),
            MakeMovie("Popular C", 2021, ["Comedy"], 6.9f)
        };
        WireLibrary(harness, movies);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("strategyColdStart", result!.ScoringStrategyKey);
        // Guard against vacuous Assert.All on an empty collection: if all candidates are dropped
        // by LoadCandidateItems the cold-start scoring pipeline (rating filter, popularity sort,
        // RecommendedItem projection) is never exercised and the test provides false coverage.
        Assert.NotEmpty(result.Recommendations);
        Assert.All(result.Recommendations, r =>
        {
            Assert.NotEqual(Guid.Empty, r.ItemId);
            Assert.Equal("reasonPopular", r.ReasonKey);
            Assert.False(string.IsNullOrEmpty(r.Name));
        });
    }

    [Fact]
    public void GetRecommendations_WarmUser_WithCandidates_ReturnsScoredRecommendations()
    {
        // Drives the FULL warm path: GenerateForUser → preference vectors → ScoreCandidate
        // → DiversityReranker → RecommendedItem projection. Largest previously-uncovered
        // block (~800 lines) gets executed here for the first time.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();

        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId))
            .Returns(MakeWarmProfile(userId, "warm", watchedId));
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { MakeWarmProfile(userId, "warm", watchedId) });

        var candidates = new List<BaseItem>
        {
            MakeMovie("Cand 1", 2022, ["Action", "Thriller"], 8.0f),
            MakeMovie("Cand 2", 2020, ["Drama"], 7.0f),
            MakeMovie("Cand 3", 2019, ["Comedy"], 6.5f)
        };
        WireLibrary(harness, candidates);

        var result = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.Equal("warm", result.UserName);
        // Cohort is populated from the mock strategy selector (default "control").
        Assert.Equal("control", result.Cohort);
        // Warm path uses the strategy passed via DI (HeuristicScoringStrategy from the
        // factory default). Its NameKey is stable across strategy-formula refactors.
        Assert.False(string.IsNullOrEmpty(result.ScoringStrategy));
        // At least one recommendation must be produced: if result.Recommendations is empty
        // the warm scoring pipeline (GenerateForUser → ScoreCandidate → DiversityReranker)
        // was never actually exercised and the test provides no coverage of those ~800 lines.
        Assert.NotEmpty(result.Recommendations);
    }

    [Fact]
    public void GetAllRecommendations_MultipleUsers_ProducesOneResultPerUser()
    {
        // Drives the parallel batch loop: LoadCandidateItems, PrecomputeUserWatchSets,
        // PrecomputeCollaborativeContext, BuildCommunityPopularityMap, the two-user
        // gate (activated here because we pass two users), and the Parallel.ForEach
        // scoring branch. Prior to this test the batch path was only exercised with
        // ZERO users so most of its body sat at 0% coverage.
        var harness = EngineTestFactory.Create();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();
        var watched1 = Guid.NewGuid();
        var watched2 = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            MakeWarmProfile(user1, "alice", watched1),
            MakeWarmProfile(user2, "bob", watched2)
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);

        var candidates = new List<BaseItem>
        {
            MakeMovie("Batch Cand 1", 2022, ["Action"], 8.2f),
            MakeMovie("Batch Cand 2", 2021, ["Drama"], 7.4f)
        };
        WireLibrary(harness, candidates);

        var results = harness.Engine.GetAllRecommendations(5, CancellationToken.None);

        Assert.NotNull(results);
        Assert.Equal(2, results.Count);
    }
}