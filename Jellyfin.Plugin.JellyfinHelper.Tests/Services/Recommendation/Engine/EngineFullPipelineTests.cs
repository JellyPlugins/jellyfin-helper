using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
        // Drives the FULL warm path: GenerateForUser -> preference vectors -> ScoreCandidate
        // -> DiversityReranker -> RecommendedItem projection. Largest previously-uncovered
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
        // the warm scoring pipeline (GenerateForUser -> ScoreCandidate -> DiversityReranker)
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

    [Fact]
    public void GetAllRecommendations_PlaceholderMovieAndEmptySeries_AreFilteredAndLogged()
    {
        // LoadCandidateItems must drop Arr placeholders: a Movie with no Path (file not yet
        // downloaded) and a Series with zero indexed episodes cannot be played, so both are
        // filtered out of the candidate pool and a single summary line is logged. A regression
        // that stopped filtering would recommend un-playable items and waste recommendation slots.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { MakeWarmProfile(userId, "user", watchedId) });

        var goodMovie = MakeMovie("Playable", 2022, ["Action"], 8.0f);
        var placeholder = MakeMovie("Not Downloaded Yet", 2022, ["Action"], 8.0f);
        placeholder.Path = null; // Arr placeholder - no media file on disk

        var emptySeries = new Series
        {
            Id = Guid.NewGuid(),
            Name = "No Episodes Yet",
            Path = "/media/series/empty",
            Genres = ["Drama"],
            ProductionYear = 2021,
            CommunityRating = 7.0f
        };

        // The Episode query returns an episode for an UNRELATED series id, so seriesEpisodeCounts
        // never contains emptySeries.Id and the series is skipped.
        var strayEpisode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = Guid.NewGuid(),
            Name = "Stray",
            Path = "/media/series/other/S01E01.mkv"
        };

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(new List<BaseItem> { goodMovie, placeholder });
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem> { emptySeries });
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem> { strayEpisode });

        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        Assert.NotNull(results);
        // The filter summary is logged exactly once with the correct skipped counts.
        harness.PluginLog.Verify(
            p => p.LogInfo(
                It.IsAny<string>(),
                It.Is<string>(msg => msg.Contains("Filtered 1 empty movies and 1 empty series")),
                It.IsAny<Microsoft.Extensions.Logging.ILogger>()),
            Times.Once);

        // Neither the path-less movie nor the episode-less series may appear in any result.
        foreach (var r in results)
        {
            Assert.DoesNotContain(r.Recommendations, i => i.ItemId == placeholder.Id);
            Assert.DoesNotContain(r.Recommendations, i => i.ItemId == emptySeries.Id);
        }
    }

    [Fact]
    public void GetAllRecommendations_ColdStartUserWithCommunityHistory_UsesCommunityBlendedFormula()
    {
        // A cold-start user (no watch history) is scored with the enhanced 40/30/30 community
        // formula when at least two OTHER users share watch history: the community-popularity
        // map is non-empty, so a candidate watched by the crowd must outrank an identical
        // candidate the crowd has never touched. This proves the 30% community term influenced
        // the cold-start score rather than the classic 60/40 rating+recency branch.
        var harness = EngineTestFactory.Create();

        var coldUser = Guid.NewGuid();
        var warmA = Guid.NewGuid();
        var warmB = Guid.NewGuid();

        // Two candidates with identical rating/genre/year so rating+recency terms tie; the only
        // differentiator is community popularity.
        var crowdFavorite = MakeMovie("Crowd Favorite", 2020, ["Action"], 7.0f);
        var unseenByCrowd = MakeMovie("Unseen", 2020, ["Action"], 7.0f);
        WireLibrary(harness, [crowdFavorite, unseenByCrowd]);

        UserWatchProfile MakeCrowdMember(Guid id, string name) => new()
        {
            UserId = id,
            UserName = name,
            WatchedItems = new Collection<WatchedItemInfo>
            {
                new()
                {
                    ItemId = crowdFavorite.Id,
                    Name = "Crowd Favorite",
                    ItemType = "Movie",
                    Played = true,
                    PlayCount = 1
                }
            }
        };

        var profiles = new Collection<UserWatchProfile>
        {
            new() { UserId = coldUser, UserName = "cold", WatchedItems = [] },
            MakeCrowdMember(warmA, "warmA"),
            MakeCrowdMember(warmB, "warmB")
        };
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles()).Returns(profiles);

        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        var coldResult = results.FirstOrDefault(r => r.UserId == coldUser);
        Assert.NotNull(coldResult);
        Assert.Equal("strategyColdStart", coldResult!.ScoringStrategyKey);

        var favoriteRec = coldResult.Recommendations.FirstOrDefault(i => i.ItemId == crowdFavorite.Id);
        var unseenRec = coldResult.Recommendations.FirstOrDefault(i => i.ItemId == unseenByCrowd.Id);
        Assert.NotNull(favoriteRec);
        Assert.NotNull(unseenRec);

        // The community-watched candidate must score strictly higher - the only signal that can
        // separate two otherwise-identical items is the 30% community-popularity term.
        Assert.True(
            favoriteRec!.Score > unseenRec!.Score,
            $"Community-watched candidate (score={favoriteRec.Score}) must outrank the crowd-unseen " +
            $"candidate (score={unseenRec.Score}) under the community-blended cold-start formula.");
    }

    [Fact]
    public void GetRecommendations_WarmUser_FavoriteSeriesAndManyCandidates_ProducesWellFormedResult()
    {
        // Warm path with a non-empty FavoriteSeriesIds set and a large candidate pool. This drives
        // the favorite-series exclusion (a favorited series is never recommended) and forces the
        // periodic cancellation check inside the scoring loop to execute at least once by supplying
        // more than CancellationCheckBatchSize candidates under a live (non-cancelled) token.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        var favoriteSeriesId = Guid.NewGuid();

        var profile = MakeWarmProfile(userId, "warm", watchedId);
        profile.FavoriteSeriesIds.Add(favoriteSeriesId);

        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(userId)).Returns(profile);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { profile });

        // A favorited series that IS a candidate must be excluded from the output.
        var favoriteSeries = new Series
        {
            Id = favoriteSeriesId,
            Name = "Favorited Show",
            Path = "/media/series/fav",
            Genres = ["Action", "Drama"],
            ProductionYear = 2020,
            CommunityRating = 8.0f
        };

        // > CancellationCheckBatchSize (200) movie candidates so the periodic ct check runs.
        var movies = new List<BaseItem>();
        for (var i = 0; i < 250; i++)
        {
            movies.Add(MakeMovie($"Cand {i}", 2015 + (i % 8), ["Action", "Drama"], 6.0f + (i % 4)));
        }

        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)))
            .Returns(movies);
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Series)))
            .Returns(new List<BaseItem> { favoriteSeries });
        // The favorite series survives LoadCandidateItems only if an episode matches its id.
        harness.LibraryManager
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Episode)))
            .Returns(new List<BaseItem>
            {
                new Episode
                {
                    Id = Guid.NewGuid(),
                    SeriesId = favoriteSeriesId,
                    Name = "Ep1",
                    Path = "/media/series/fav/S01E01.mkv"
                }
            });

        var result = harness.Engine.GetRecommendations(userId, 20, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Recommendations);
        // A favorited series must never be recommended - the user already committed to it.
        Assert.DoesNotContain(result.Recommendations, i => i.ItemId == favoriteSeriesId);
    }

    [Fact]
    public void GetRecommendations_WarmUser_CalledTwice_ReusesCachedSnapshot_NoSecondLibraryScan()
    {
        // The first live request publishes a candidate snapshot; a second request within the
        // CandidateSnapshotMaxAge TTL must take the still-valid fast-path in GetOrRefreshLiveSnapshot
        // and reuse those candidates rather than re-scanning the library. If the TTL fast-path
        // regressed, LoadCandidateItems would run again and fire the Movie query a second time.
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
            MakeMovie("Cand 2", 2020, ["Drama"], 7.0f)
        };
        WireLibrary(harness, candidates);

        var first = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);
        var second = harness.Engine.GetRecommendations(userId, 10, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(userId, first!.UserId);
        Assert.Equal(userId, second!.UserId);

        // The Movie candidate scan must have happened exactly once across BOTH calls: the second
        // call served from the still-valid TTL snapshot instead of re-scanning the library.
        harness.LibraryManager.Verify(
            lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Movie)),
            Times.Once);
    }

    [Fact]
    public void GetAllRecommendations_GetPeopleThrows_StillCachesMetadataAndCompletes()
    {
        // BuildCandidateContentAffinityLookup must swallow a non-fatal GetPeople failure (people=null)
        // and still cache the five candidate metadata fields, so scoring proceeds with empty
        // writer/billing signals. A regression that dropped the guard would crash the batch on the
        // first candidate whose people index is unreadable.
        var harness = EngineTestFactory.Create();
        var userId = Guid.NewGuid();
        var watchedId = Guid.NewGuid();
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles())
            .Returns(new Collection<UserWatchProfile> { MakeWarmProfile(userId, "user", watchedId) });

        var candidates = new List<BaseItem>
        {
            MakeMovie("Cand 1", 2022, ["Action"], 8.0f),
            MakeMovie("Cand 2", 2021, ["Drama"], 7.0f)
        };
        WireLibrary(harness, candidates);

        // People index unreadable for every candidate: the catch-and-continue contract must hold.
        harness.LibraryManager
            .Setup(lm => lm.GetPeople(It.IsAny<BaseItem>()))
            .Throws(new InvalidOperationException("people index offline"));

        var results = harness.Engine.GetAllRecommendations(10, CancellationToken.None);

        // Batch completes with one result for the single user - the guard absorbed the failure.
        Assert.NotNull(results);
        Assert.Single(results);
        Assert.Equal(userId, results[0].UserId);
    }
}