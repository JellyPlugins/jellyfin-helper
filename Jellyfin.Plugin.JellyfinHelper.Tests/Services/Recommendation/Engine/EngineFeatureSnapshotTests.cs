using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Playlist;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

public sealed class EngineFeatureSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObservedAccessWithoutAnEligibleStreamSampleRemainsUnknown(bool seriesCandidate)
    {
        var harness = EngineTestFactory.Create();
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var series = new Mock<Series> { CallBase = true };
        series.Object.Id = Guid.NewGuid();
        series.Object.Path = "/media/show";
        series.Object.Genres = ["Drama"];
        series.Setup(item => item.GetMediaStreams()).Returns([]);
        var movie = new Mock<Movie> { CallBase = true };
        movie.Object.Id = Guid.NewGuid();
        movie.Object.Path = "/media/movie.mkv";
        movie.Object.Genres = ["Drama"];
        movie.Setup(item => item.GetMediaStreams()).Returns((IReadOnlyList<MediaStream>)null!);
        BaseItem candidate = seriesCandidate ? series.Object : movie.Object;
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Object.Id, Path = "/media/show/denied.mkv" };
        var allowed = new HashSet<Guid> { candidate.Id };
        var profile = new UserWatchProfile
        {
            UserId = user, AccessibleItemIds = allowed,
            WatchedItems = [new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Drama"] }],
            LanguageProfile = new() { ["en"] = new() { ChosenCount = 3 } }
        };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.WatchHistory.Setup(history => history.GetUserParentalPolicy(user)).Returns((true, (int?)null));
        harness.WatchHistory.Setup(history => history.GetCurrentAccessibleItemIds(user)).Returns(allowed);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.IncludeItemTypes.Contains(BaseItemKind.Movie)) return seriesCandidate ? [] : [candidate];
            if (query.IncludeItemTypes.Contains(BaseItemKind.Series)) return seriesCandidate ? [candidate] : [];
            if (query.IncludeItemTypes.Contains(BaseItemKind.Episode)) return [episode];
            return [];
        });
        var features = Assert.Single(Assert.IsType<RecommendationResult>(harness.Engine.GetRecommendations(user, 1, TestContext.Current.CancellationToken)).Recommendations).ScoringFeatures!;
        Assert.False(features.HasAudioLanguageEvidence);
        Assert.Null(features.MediaLanguageSampleItemId);
        Assert.Equal(.37, features.ToModelVector(Enumerable.Repeat(.37, 41).ToArray())[(int)FeatureIndex.LanguageAffinity]);
        Assert.Equal(41, features.ToVector().Length);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("virtual")]
    [InlineData("empty-path")]
    [InlineData("wrong-series")]
    [InlineData("parental")]
    [InlineData("excluded-root")]
    [InlineData("empty-streams")]
    public void SeriesLanguageSamplingUsesOnlyThePermittedPhysicalEpisodeAndCapturesItsIdentity(string boundary)
    {
        var harness = EngineTestFactory.Create();
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var series = new Mock<Series> { CallBase = true };
        series.Object.Id = Guid.NewGuid();
        series.Object.Name = "Synthetic permitted series";
        series.Object.Path = "/media/series";
        series.Object.Genres = ["Drama"];
        series.Object.OfficialRating = "FSK-0";
        series.Object.InheritedParentalRatingValue = 0;
        series.Setup(item => item.GetMediaStreams()).Returns([]);
        var rejected = new Mock<Episode> { CallBase = true };
        rejected.Object.Id = Guid.NewGuid();
        rejected.Object.SeriesId = boundary == "wrong-series" ? Guid.NewGuid() : series.Object.Id;
        rejected.Object.ParentIndexNumber = 1;
        rejected.Object.IndexNumber = 1;
        rejected.Object.Path = boundary switch
        {
            "empty-path" => string.Empty,
            "excluded-root" => "/restricted/1.mkv",
            _ => "/media/series/1.mkv"
        };
        rejected.Object.IsVirtualItem = boundary == "virtual";
        rejected.Object.OfficialRating = boundary == "parental" ? "FSK-16" : "FSK-0";
        rejected.Object.InheritedParentalRatingValue = boundary == "parental" ? 16 : 0;
        IReadOnlyList<MediaStream> rejectedStreams =
        [
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Language = "eng" },
            new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Language = "eng" }
        ];
        rejected.Setup(item => item.GetMediaStreams()).Returns(boundary == "empty-streams" ? [] : rejectedStreams);
        var permitted = new Mock<Episode> { CallBase = true };
        permitted.Object.Id = Guid.NewGuid();
        permitted.Object.SeriesId = series.Object.Id;
        permitted.Object.ParentIndexNumber = 1;
        permitted.Object.IndexNumber = 2;
        permitted.Object.Path = "/media/series/2.mkv";
        permitted.Object.OfficialRating = "FSK-0";
        permitted.Object.InheritedParentalRatingValue = 0;
        permitted.Setup(item => item.GetMediaStreams()).Returns(
        [
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Language = "deu" },
            new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Language = "deu" }
        ]);
        var allowed = new HashSet<Guid> { series.Object.Id, permitted.Object.Id };
        if (boundary != "denied") allowed.Add(rejected.Object.Id);
        var profile = new UserWatchProfile
        {
            UserId = user, MaxParentalRating = 6, AccessibleItemIds = allowed,
            WatchedItems = [new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Drama"] }],
            LanguageProfile = new() { ["en"] = new() { ChosenCount = 10 } },
            SubtitleLanguageProfile = new() { ["en"] = new() { ChosenCount = 10 } }
        };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.WatchHistory.Setup(history => history.GetUserParentalPolicy(user)).Returns((true, (int?)6));
        harness.WatchHistory.Setup(history => history.GetCurrentAccessibleItemIds(user)).Returns(allowed);
        harness.ConfigService.Setup(configuration => configuration.GetConfiguration()).Returns(new Jellyfin.Plugin.JellyfinHelper.Configuration.PluginConfiguration { ExcludedLibraries = "Restricted" });
        harness.LibraryManager.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Allowed", Locations = ["/media"] },
            new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Restricted", Locations = ["/restricted"] }
        ]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) =>
        {
            if (query.IncludeItemTypes.Contains(BaseItemKind.Series)) return [series.Object];
            if (query.IncludeItemTypes.Contains(BaseItemKind.Episode)) return [rejected.Object, permitted.Object];
            return [];
        });

        var result = Assert.IsType<RecommendationResult>(harness.Engine.GetRecommendations(user, 1, TestContext.Current.CancellationToken));
        var features = Assert.Single(result.Recommendations).ScoringFeatures!;
        Assert.Equal(permitted.Object.Id, features.MediaLanguageSampleItemId);
        Assert.True(features.MediaLanguageIsEpisodeSample);
        Assert.Equal(DateTimeKind.Utc, features.MediaLanguageSampledAtUtc!.Value.Kind);
        Assert.True(features.HasAudioLanguageEvidence);
        Assert.True(features.HasSubtitleLanguageEvidence);
        Assert.Equal(.1, features.LanguageAffinity);
        Assert.Equal(.1, features.SubtitleLanguageAffinity);
        Assert.Equal(41, features.ToVector().Length);
        harness.LibraryManager.Verify(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId == series.Object.Id && query.ItemIds.Length > 0 && query.Limit == 128 && !query.GroupByPresentationUniqueKey)), Times.AtLeastOnce);
        rejected.Verify(item => item.GetMediaStreams(), boundary == "empty-streams" ? Times.AtLeastOnce() : Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableCurrentSeriesAccessCannotProduceObservedLanguageEvidence(bool failedRead)
    {
        var harness = EngineTestFactory.Create();
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var series = new Mock<Series> { CallBase = true };
        series.Object.Id = Guid.NewGuid();
        series.Object.Path = "/media/series";
        series.Object.Genres = ["Drama"];
        series.Setup(item => item.GetMediaStreams()).Returns([]);
        var profile = new UserWatchProfile
        {
            UserId = user, AccessibleItemIds = new HashSet<Guid> { series.Object.Id },
            WatchedItems = [new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Drama"] }],
            LanguageProfile = new() { ["en"] = new() { ChosenCount = 1 } }
        };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        harness.WatchHistory.Setup(history => history.GetUserParentalPolicy(user)).Returns((true, (int?)null));
        if (failedRead) harness.WatchHistory.Setup(history => history.GetCurrentAccessibleItemIds(user)).Throws(new IOException("current access unavailable"));
        else harness.WatchHistory.Setup(history => history.GetCurrentAccessibleItemIds(user)).Returns((IReadOnlySet<Guid>?)null);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Series) ? [series.Object] : []);
        // Supply only the episode-count inventory; it never grants permission to read tracks.
        var counted = new Episode { Id = Guid.NewGuid(), SeriesId = series.Object.Id, Path = "/media/series/1.mkv" };
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.IncludeItemTypes.Contains(BaseItemKind.Episode) && query.ParentId == Guid.Empty))).Returns([counted]);
        var features = Assert.Single(Assert.IsType<RecommendationResult>(harness.Engine.GetRecommendations(user, 1, TestContext.Current.CancellationToken)).Recommendations).ScoringFeatures!;
        Assert.False(features.HasAudioLanguageEvidence);
        Assert.Null(features.MediaLanguageSampleItemId);
        Assert.Null(features.MediaLanguageSampledAtUtc);
        Assert.False(features.MediaLanguageIsEpisodeSample);
        Assert.Equal(.37, features.ToModelVector(Enumerable.Repeat(.37, 41).ToArray())[(int)FeatureIndex.LanguageAffinity]);
        harness.LibraryManager.Verify(manager => manager.GetItemList(It.Is<InternalItemsQuery>(query => query.ParentId == series.Object.Id)), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LanguageAvailabilityFlags_AreCapturedWithoutBorrowingSubtitleEvidenceOrInventingUnknownAudio(bool partialAudio)
    {
        var strategy = new Mock<IScoringStrategy>();
        strategy.SetupGet(value => value.Name).Returns("capture");
        strategy.SetupGet(value => value.NameKey).Returns("capture");
        strategy.Setup(value => value.Score(It.IsAny<CandidateFeatures>())).Returns(.9);
        strategy.Setup(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>()))
            .Returns(new ScoreExplanation { FinalScore = .9, StrategyName = "capture", DominantSignal = "rating" });
        var harness = EngineTestFactory.Create(strategy.Object);
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Action"] }] };
        profile.LanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 5 };
        profile.SubtitleLanguageProfile["de"] = new LanguageProfileEntry { ChosenCount = 5 };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        var movie = new Mock<Movie> { CallBase = true };
        movie.Object.Id = Guid.NewGuid();
        movie.Object.Name = "Synthetic language availability";
        movie.Object.Path = "/media/language-availability.mkv";
        movie.Object.Genres = ["Action"];
        movie.Object.CommunityRating = 8;
        movie.Setup(item => item.GetMediaStreams()).Returns(
        [
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Language = "eng" },
            new MediaStream { Index = 2, Type = MediaStreamType.Audio, Language = partialAudio ? null : "eng" },
            new MediaStream { Index = 3, Type = MediaStreamType.Subtitle, Language = "deu" }
        ]);
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? [movie.Object] : []);
        var result = Assert.IsType<RecommendationResult>(harness.Engine.GetRecommendations(user, 1, TestContext.Current.CancellationToken));
        var features = Assert.Single(result.Recommendations).ScoringFeatures!;
        Assert.Equal(!partialAudio, features.HasAudioLanguageEvidence);
        Assert.True(features.HasSubtitleLanguageEvidence);
        Assert.Equal(1, features.SubtitleLanguageAffinity);
        var means = Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray();
        Assert.Equal(partialAudio ? .8 : .1, features.ToModelVector(means)[28]);
        Assert.Equal(1, features.ToModelVector(means)[30]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedSnapshots_ExactlyMatchFinalScoringInputsWithoutRetainingTheCandidatePopulation(bool refreshMetadata)
    {
        var rankingInputs = new List<double[]>();
        var contrastInputs = new Dictionary<double, double[]>();
        var explanationInputs = new Dictionary<double, double[]>();
        BaseItem[] movies = [];
        var scored = 0;
        var strategy = new Mock<IScoringStrategy>();
        strategy.SetupGet(value => value.Name).Returns("capture");
        strategy.SetupGet(value => value.NameKey).Returns("capture");
        strategy.Setup(value => value.Score(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) =>
        {
            if (++scored <= 40)
            {
                rankingInputs.Add(features.ToVector());
            }
            else
            {
                contrastInputs.Add(features.CombinedCriticScore, features.ToVector());
            }
            if (scored == 40 && refreshMetadata)
            {
                movies[^1].CommunityRating = 9.9f;
            }
            return features.CombinedCriticScore;
        });
        strategy.Setup(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>())).Returns((CandidateFeatures features) =>
        {
            explanationInputs.Add(features.CombinedCriticScore, features.ToVector());
            return new ScoreExplanation { FinalScore = features.CombinedCriticScore, StrategyName = "capture", DominantSignal = "rating" };
        });
        var harness = EngineTestFactory.Create(strategy.Object);
        using var lifetime = harness.Engine;
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-1), Genres = ["Drama"] }] };
        harness.WatchHistory.Setup(history => history.GetUserWatchProfile(user)).Returns(profile);
        harness.WatchHistory.Setup(history => history.GetAllUserWatchProfiles()).Returns([profile]);
        movies = Enumerable.Range(0, 40).Select(index => (BaseItem)new Movie { Id = Guid.NewGuid(), Name = $"Movie {index}", Path = $"/media/movie-{index}.mkv", Genres = ["Drama"], CommunityRating = 4.0f + index * 0.05f, DateCreated = DateTime.UtcNow.AddDays(-index - 1), ProductionYear = 2020 }).ToArray();
        harness.LibraryManager.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? movies : []);

        var result = Assert.IsType<RecommendationResult>(harness.Engine.GetRecommendations(user, 3, TestContext.Current.CancellationToken));

        strategy.Verify(value => value.Score(It.IsAny<CandidateFeatures>()), Times.Exactly(movies.Length + 5));
        strategy.Verify(value => value.ScoreWithExplanation(It.IsAny<CandidateFeatures>()), Times.Exactly(3));
        Assert.Equal(movies.Length, rankingInputs.Count);
        void AssertSnapshot(RecommendedItem recommendation, Dictionary<double, double[]> inputs)
        {
            var features = Assert.IsType<CandidateFeatures>(recommendation.ScoringFeatures);
            Assert.Equal(inputs[features.CombinedCriticScore], features.ToVector());
            Assert.Equal(Math.Round(features.CombinedCriticScore, 4), recommendation.Score);
            var dateCreated = Assert.IsType<DateTime>(recommendation.DateCreated);
            Assert.Equal(ContentScoring.ComputeRecencyScore(dateCreated, result.GeneratedAt), features.LibraryAddedRecency);
        }

        Assert.Equal(3, result.Recommendations.Count);
        Assert.Equal(result.Recommendations.OrderByDescending(item => item.Score).Select(item => item.ItemId), result.Recommendations.Select(item => item.ItemId));
        Assert.All(result.Recommendations, item => AssertSnapshot(item, explanationInputs));
        var lastMovie = Assert.Single(result.Recommendations, item => item.ItemId == movies[^1].Id);
        Assert.Equal(ContentScoring.ComputeCombinedCriticScore(refreshMetadata ? 9.9f : 5.95f, null), lastMovie.ScoringFeatures!.CombinedCriticScore);
        Assert.Equal(!refreshMetadata, rankingInputs.Any(vector => vector.SequenceEqual(lastMovie.ScoringFeatures.ToVector())));
        Assert.NotNull(result.TrainingContrasts);
        Assert.Equal(5, result.TrainingContrasts.Count);
        Assert.All(result.TrainingContrasts, item =>
        {
            AssertSnapshot(item, contrastInputs);
            Assert.DoesNotContain(item.ItemId, result.Recommendations.Select(selected => selected.ItemId));
            Assert.DoesNotContain(item.ScoringFeatures!.CombinedCriticScore, explanationInputs.Keys);
        });

        var playlists = new RecommendationPlaylistService(Mock.Of<IPlaylistManager>(), Mock.Of<IUserManager>(), harness.LibraryManager.Object,
            Mock.Of<IPluginLogService>(), Mock.Of<ILogger<RecommendationPlaylistService>>());
        var playlistIds = playlists.ResolvePlaylistItemIds(result.Recommendations, result.Recommendations.Count);
        Assert.Equal(result.Recommendations.Select(item => item.ItemId), playlistIds);
    }
}
