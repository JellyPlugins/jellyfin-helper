using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>Personal seeds add recall without title rules or episode vote inflation; enrichment retains a bounded diverse frontier.</summary>
public sealed class PersonalDiscoveryRetrievalTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Seeds_AreProviderTypedTitleBalancedAndPersonal_NotAnEpisodePopularityContest()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        var series = Guid.NewGuid();
        for (var i = 0; i < 100; i++)
        {
            var episode = Watch(70, "Episode", Now.AddDays(-1));
            episode.SeriesId = series;
            episode.SeriesTmdbId = 70;
            profile.WatchedItems.Add(episode);
        }

        profile.WatchedItems.Add(Watch(80, "Series", Now));
        profile.WatchedItems.Add(Watch(80, "Movie", Now));
        var seeds = PersonalDiscoveryRetrieval.Seeds(profile);
        Assert.Equal(80, seeds["movie"]);
        Assert.Equal(80, seeds["tv"]);
        Assert.Equal(2, seeds.Count);
        var other = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(90, "Movie", Now), Watch(91, "Movie", Now.AddDays(-1))] };
        Assert.Equal(90, PersonalDiscoveryRetrieval.Seeds(other)["movie"]);
        Assert.Equal(80, PersonalDiscoveryRetrieval.Seeds(profile)["movie"]);
    }

    [Fact]
    public void MissingIdentityTechnicalLabelsAndExplicitDislikes_CannotSeedRetrieval()
    {
        var missing = Watch(0, "Movie", Now);
        var technical = Watch(2, "Movie", Now);
        technical.Tags = ["4K", "GERMAN", "DDP", "1080p"];
        var dislike = Watch(3, "Movie", Now);
        dislike.UserRating = 2;
        var orphan = Watch(4, "Episode", Now);
        var noOutcome = Watch(5, "Movie", Now);
        noOutcome.Played = false;
        noOutcome.PlayCount = 1;
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [missing, technical, dislike, orphan, noOutcome] };
        Assert.Empty(PersonalDiscoveryRetrieval.Seeds(profile));
    }

    [Fact]
    public void AStrongAnimeLaneSurvivesANewerNonAnimeThemeSeedWithoutEpisodeVoteInflation()
    {
        var newest = Watch(10, "Series", Now);
        newest.Tags = ["courtroom", "trial", "justice"];
        var supporting = Watch(11, "Series", Now.AddDays(-1));
        supporting.Tags = newest.Tags;
        var firstAnime = Watch(20, "Series", Now.AddDays(-2));
        firstAnime.Tags = ["anime", "pirate", "friendship"];
        var secondAnime = Watch(21, "Series", Now.AddDays(-3));
        secondAnime.Tags = ["anime", "magic", "wizard"];
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [newest, supporting, firstAnime, secondAnime] };
        var seeds = PersonalDiscoveryRetrieval.Seeds(profile);
        Assert.Equal(10, seeds["tv"]);
        Assert.Equal(20, seeds["tv:anime"]);
        Assert.InRange(seeds.Count, 2, 4);
        for (var index = 0; index < 100; index++)
        {
            var episode = Watch(21, "Episode", Now.AddDays(-3));
            episode.SeriesId = secondAnime.ItemId;
            episode.SeriesTmdbId = 21;
            episode.SeriesTags = secondAnime.Tags;
            profile.WatchedItems.Add(episode);
        }

        Assert.Equal(20, PersonalDiscoveryRetrieval.Seeds(profile)["tv:anime"]);
    }

    [Fact]
    public void ARepeatedSingleAnimeTitleCannotInventIndependentFamilySupport()
    {
        var parent = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var index = 0; index < 100; index++)
        {
            var row = Watch(20, "Episode", Now);
            row.SeriesId = parent;
            row.SeriesTmdbId = 20;
            row.SeriesTags = ["anime"];
            profile.WatchedItems.Add(row);
        }

        Assert.DoesNotContain("tv:anime", PersonalDiscoveryRetrieval.Seeds(profile).Keys);
    }

    [Fact]
    public void FamilySeedsKeepMovieAndTvIdSpacesSeparate()
    {
        var movie = Watch(20, "Movie", Now);
        var otherMovie = Watch(21, "Movie", Now.AddDays(-1));
        var newestTv = Watch(99, "Series", Now);
        newestTv.Tags = ["courtroom", "trial"];
        var otherTv = Watch(100, "Series", Now.AddDays(-1));
        otherTv.Tags = newestTv.Tags;
        var anime = Watch(20, "Series", Now.AddDays(-2));
        anime.Tags = ["anime", "magic", "wizard"];
        var otherAnime = Watch(22, "Series", Now.AddDays(-3));
        otherAnime.Tags = anime.Tags;
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [movie, otherMovie, newestTv, otherTv, anime, otherAnime] };
        var seeds = PersonalDiscoveryRetrieval.Seeds(profile);
        Assert.Equal(20, seeds["movie"]);
        Assert.Equal(99, seeds["tv"]);
        Assert.Equal(20, seeds["tv:anime"]);
        Assert.InRange(seeds.Count, 3, 4);
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("Series")]
    public void APositiveTitleCannotCorroborateItsOwnSpecificKeywords(string type)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(90, type, Now)] };
        Assert.Empty(PersonalDiscoveryRetrieval.Seeds(profile));
        profile.WatchedItems.Add(Watch(90, type, Now.AddDays(-1))); // Another library copy is still the same provider title.
        Assert.Empty(PersonalDiscoveryRetrieval.Seeds(profile));
        var other = Watch(91, type, Now.AddDays(-2));
        other.Tags = ["courtroom", "trial"];
        profile.WatchedItems.Add(other);
        Assert.Empty(PersonalDiscoveryRetrieval.Seeds(profile));
        other.Tags = ["pirate", "friendship"];
        Assert.Equal(90, PersonalDiscoveryRetrieval.Seeds(profile)[type == "Movie" ? "movie" : "tv"]);
    }

    [Fact]
    public void AllEpisodesAndTheStandaloneSeriesAreExcludedFromTheirOwnSeedEvidence()
    {
        var parent = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var index = 0; index < 40; index++)
        {
            var episode = Watch(70, "Episode", Now);
            episode.SeriesId = parent;
            episode.SeriesTmdbId = 70;
            episode.SeriesTags = ["pirate", "friendship"];
            profile.WatchedItems.Add(episode);
        }
        var series = Watch(70, "Series", Now);
        series.ItemId = parent;
        profile.WatchedItems.Add(series);
        Assert.Empty(PersonalDiscoveryRetrieval.Seeds(profile));
        profile.WatchedItems.Add(Watch(80, "Series", Now.AddDays(-1)));
        Assert.Equal(70, PersonalDiscoveryRetrieval.Seeds(profile)["tv"]);
    }

    [Fact]
    public void OptionalFrontier_ReservesTopScoresAndReachesPersonalAndMinorityLanesWithinTwentyRequests()
    {
        var broad = Enumerable.Range(1, 45).Select(id => Candidate(id, "movie", 16)).ToList();
        var minority = Candidate(100, "tv", 80);
        var thematic = Candidate(101, "tv", 16);
        thematic.PersonalSeedKey = "tv:90";
        var ordered = broad.Append(minority).Append(thematic).ToList();
        var frontier = PersonalDiscoveryRetrieval.DetailFrontier(ordered, 20);
        Assert.Equal(20, frontier.Count);
        Assert.Equal(Enumerable.Range(1, 10), frontier.Where(candidate => candidate.Id <= 10).Select(candidate => candidate.Id));
        Assert.Same(thematic, frontier[1]);
        Assert.Contains(minority, frontier);
        Assert.Contains(thematic, frontier);
        Assert.Equal(frontier.Count, frontier.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 45), ordered.Take(45).Select(candidate => candidate.Id));
        Assert.Null(thematic.KnownKeywords);
        Assert.All(frontier, candidate => Assert.False(candidate.MetadataFetchAttempted));
        Assert.Equal(frontier, PersonalDiscoveryRetrieval.DetailFrontier(ordered, 20));
    }

    [Fact]
    public void EmptyParentIdentityUsesTheMovieProviderAndAnEmptyOwnTagSeedIsSkipped()
    {
        var newest = Watch(90, "Movie", Now);
        newest.SeriesId = Guid.Empty;
        newest.OwnTags = ["pirate", "friendship"];
        newest.SeriesTags = ["courtroom", "trial"];
        var supporting = Watch(91, "Movie", Now.AddDays(-1));
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [newest, supporting] };
        Assert.Equal(90, PersonalDiscoveryRetrieval.Seeds(profile)["movie"]);
        newest.SeriesId = null;
        newest.OwnTags = [];
        Assert.DoesNotContain(90, PersonalDiscoveryRetrieval.Seeds(profile).Values);
    }

    [Fact]
    public void CachedOrFailedDetails_DoNotOccupyFreshOptionalSlots_AndSourceProvenanceIsNotPersisted()
    {
        var ordered = Enumerable.Range(1, 60).Select(id => Candidate(id, "movie", 28)).ToList();
        foreach (var candidate in ordered.Take(20)) candidate.MetadataFetchAttempted = true;
        var frontier = PersonalDiscoveryRetrieval.DetailFrontier(ordered, 20);
        Assert.Equal(Enumerable.Range(21, 20), frontier.Select(candidate => candidate.Id));
        Assert.Empty(PersonalDiscoveryRetrieval.DetailFrontier([], 20));
        ordered[0].PersonalSeedKey = "movie:90";
        Assert.DoesNotContain("PersonalSeedKey", JsonSerializer.Serialize(ordered[0]));
    }

    private static TmdbDiscoverItem Candidate(int id, string type, int genre) => new() { Id = id, MediaType = type, GenreIds = [genre] };

    [Theory]
    [InlineData("Movie", "movie")]
    [InlineData("Series", "tv")]
    public void FamilyLaneSelectsItsUnusedRepresentativeWhenTheFirstTitleIsAlreadyAThemeSeed(string type, string mediaType)
    {
        var newest = Watch(20, type, Now);
        newest.Tags = ["anime", "pirate", "friendship"];
        var second = Watch(21, type, Now.AddDays(-1));
        second.Tags = newest.Tags;
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [newest, second] };
        var seeds = PersonalDiscoveryRetrieval.Seeds(profile);
        Assert.Equal(20, seeds[mediaType]);
        Assert.Equal(21, seeds[mediaType + ":anime"]);
        Assert.InRange(seeds.Count, 2, 4);
        Assert.Equal(seeds.Count, seeds.Values.Distinct().Count());
        profile.WatchedItems.Remove(second);
        Assert.DoesNotContain(mediaType + ":anime", PersonalDiscoveryRetrieval.Seeds(profile).Keys);
    }
    private static WatchedItemInfo Watch(int id, string type, DateTime date) => new()
    {
        ItemId = Guid.NewGuid(), TmdbId = id, ItemType = type, Played = true, LastPlayedDate = date,
        Genres = ["Animation", "Action"], Tags = ["pirate", "treasure", "friendship"]
    };
}
