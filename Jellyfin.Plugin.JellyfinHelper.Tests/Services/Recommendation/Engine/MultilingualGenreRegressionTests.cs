using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Anonymous regressions for multilingual, mixed-provider, inactive, repetitive and empty profiles.</summary>
public sealed class MultilingualGenreRegressionTests
{
    [Theory]
    [InlineData("Abenteuer")]
    [InlineData("Adventure")]
    [InlineData("Aventura")]
    [InlineData("Aventure")]
    [InlineData("Äventyr")]
    [InlineData("Macera")]
    [InlineData("冒险")]
    public void Adventure_HasIdenticalFeaturesInEverySupportedLanguage(string label)
    {
        var profile = Profile(label, 1);
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var candidate = TmdbGenreMap.ToJellyfinGenres([12]);
        Assert.Equal(1, SimilarityComputer.ComputeGenreSimilarity(candidate, preferences));
        Assert.Equal(1, ContentScoring.ComputeGenreEngagement(candidate, profile).Familiarity);
        Assert.Equal(label, profile.WatchedItems[0].Genres[0]);
    }

    [Theory]
    [InlineData(10759, 28, 12)]
    [InlineData(10765, 878, 14)]
    public void FilmAndTvGenreIds_UseTheSameSemanticCoordinates(int tvId, int movieId1, int movieId2)
    {
        var movies = TmdbGenreMap.ToJellyfinGenres([movieId1, movieId2]);
        var television = TmdbGenreMap.ToJellyfinGenres([tvId]);
        var profile = Profile(television[0], 1);
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        Assert.Equal(1, SimilarityComputer.ComputeGenreSimilarity(movies, preferences), 12);
        Assert.Equal(1, SimilarityComputer.ComputeGenreSimilarity(television, preferences), 12);
        Assert.Equal(GenreNormalizer.Set(movies), GenreNormalizer.Set(television));
    }

    [Theory]
    [InlineData("Krimi", "Crime")]
    [InlineData("Komödie", "Comedy")]
    [InlineData("Familie", "Family")]
    public void CachedAndDirectEngagement_KeepLocalizedAffinity(string local, string canonical)
    {
        var profile = Profile(local, 3);
        var context = ContentScoring.BuildGenreEngagementContext(profile);
        var direct = ContentScoring.ComputeGenreEngagement([canonical], profile);
        var cached = ContentScoring.ComputeGenreEngagement([canonical], context);
        Assert.Equal(direct, cached);
        Assert.Equal(1, direct.Familiarity);
        Assert.Equal(1, SimilarityComputer.ComputeGenreSimilarity([canonical], new Dictionary<string, double> { [local] = 1 }, 1));
    }

    [Fact]
    public void OldRewatches_DecayWithoutErasingCurrentExplicitFavorites()
    {
        var profile = Profile("Animation", 1);
        profile.WatchedItems[0].LastPlayedDate = DateTime.UtcNow.AddYears(-10);
        profile.WatchedItems[0].PlayCount = 100;
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow, Genres = ["Krimi"] });
        var preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        Assert.True(preferences["Animation"] < 0.01);
        Assert.Equal(1, preferences["Crime"]);
        profile.WatchedItems[0].IsFavorite = true;
        preferences = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        Assert.Equal(1, preferences["Animation"]);
    }

    [Fact]
    public void EmptyAndCustomProfiles_AreNotInventedOrDiscarded()
    {
        Assert.Empty(PreferenceBuilder.BuildGenrePreferenceVector(new UserWatchProfile()));
        var profile = Profile("My Custom Genre", 1);
        Assert.Equal(1, SimilarityComputer.ComputeGenreSimilarity(["My Custom Genre"], PreferenceBuilder.BuildGenrePreferenceVector(profile)));
        Assert.Equal(0, SimilarityComputer.ComputeGenreSimilarity(["Kids"], PreferenceBuilder.BuildGenrePreferenceVector(Profile("Anime", 1))));
    }

    [Fact]
    public void SeriesAffinity_EntityAndTrainingOverloadsAgreeForRawCompoundGenres()
    {
        var watchedSeries = Guid.NewGuid();
        var profile = Profile("Action & Abenteuer", 3);
        foreach (var row in profile.WatchedItems)
        {
            row.SeriesId = watchedSeries;
        }

        var context = ContentScoring.BuildSeriesAffinityContext(profile, new Dictionary<Guid, int> { [watchedSeries] = 6 });
        var candidate = new Series { Id = Guid.NewGuid(), Genres = ["Acción & Aventura"] };
        var people = new Dictionary<Guid, HashSet<string>>();
        var live = ContentScoring.ComputeSeriesAffinity(candidate, context, people);
        var training = ContentScoring.ComputeSeriesAffinity(true, candidate.Id, candidate.Genres, context, people);
        Assert.Equal(0.6, live, 12);
        Assert.Equal(live, training);
        Assert.Equal(0, ContentScoring.ComputeSeriesAffinity(true, candidate.Id, candidate.Genres, context, people, watchedSeries));
    }

    [Fact]
    public void NearestNeighbor_RawHistoryAndCandidateSetsAgreeWithCachedTraining()
    {
        var candidate = new HashSet<string> { "Action & Abenteuer", "Krimi" };
        var history = new List<HashSet<string>> { new() { "Acción & Aventura", "Crime" } };
        var people = new List<HashSet<string>> { new() };
        var studios = new List<HashSet<string>> { new() };
        var direct = ContentScoring.ComputeContentNearestNeighborScore(candidate, null, null, history, people, studios);
        var training = TrainingFeatureComputer.ComputeContentNearestNeighborFromCache(["Action", "Adventure", "Crime"], [], [], history, people, studios);
        Assert.Equal(0.5, direct, 12);
        Assert.Equal(direct, training);
        Assert.Contains("Action & Abenteuer", candidate);
        Assert.Contains("Acción & Aventura", history[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TemporalTraining_ListAndRawSetOverloadsAgree(bool isDay)
    {
        var profile = Profile("Krimi", 12);
        var target = profile.WatchedItems[0];
        var list = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(target, (IReadOnlyList<string>)["Crime"], profile, isDay);
        var raw = TrainingFeatureComputer.ComputeTrainingTemporalAffinity(target, new HashSet<string> { "Krimi" }, profile, isDay);
        Assert.Equal(1, list);
        Assert.Equal(list, raw);
    }

    [Fact]
    public void KidsAudience_IsNotInferredFromAnimeButFamilyMovieViewersRemainSupported()
    {
        var anime = new Dictionary<string, double> { ["Animation"] = 1, ["Action"] = 1, ["Adventure"] = 1 };
        var family = new Dictionary<string, double> { ["Animation"] = 1, ["Family"] = 1 };
        var child = new Dictionary<string, double> { ["Animation"] = 1, ["Kids"] = 1 };
        var kidsGenres = TmdbGenreMap.ToJellyfinGenres([16, 10762]);
        Assert.True(SimilarityComputer.ComputeGenreSimilarity(kidsGenres, anime) < 0.2);
        Assert.Equal(0.375, SimilarityComputer.ComputeGenreSimilarity(kidsGenres, family), 12);
        Assert.True(SimilarityComputer.ComputeGenreSimilarity(kidsGenres, family) > SimilarityComputer.ComputeGenreSimilarity(kidsGenres, anime));
        Assert.Equal(1, SimilarityComputer.ComputeGenreSimilarity(kidsGenres, child), 12);
        Assert.True(SimilarityComputer.ComputeGenreSimilarity(["Animation"], anime) > SimilarityComputer.ComputeGenreSimilarity(kidsGenres, anime));
    }

    [Fact]
    public void LegacyAliasCounts_AreSummedBeforeNormalizationAndIndependentOfOrder()
    {
        var first = new UserWatchProfile { GenreDistribution = new Dictionary<string, int> { ["Krimi"] = 6, ["Crime"] = 5, ["Drama"] = 10 } };
        var reversed = new UserWatchProfile { GenreDistribution = new Dictionary<string, int> { ["Drama"] = 10, ["Crime"] = 5, ["Krimi"] = 6 } };
        var one = PreferenceBuilder.BuildGenrePreferenceVector(first);
        var two = PreferenceBuilder.BuildGenrePreferenceVector(reversed);
        Assert.Equal(1, one["Crime"]);
        Assert.Equal(10.0 / 11, one["Drama"], 12);
        Assert.Equal(one["Crime"], two["Crime"]);
        Assert.Equal(one["Drama"], two["Drama"]);
    }

    [Theory]
    [InlineData("Fantasy", 10765)]
    [InlineData("War", 10768)]
    [InlineData("Kriegsfilm", 10768)]
    [InlineData("Politics", 10768)]
    public void NormalizedComponents_CanStillRetrieveTheirTvGenre(string genre, int expected)
        => Assert.Equal(expected, TmdbGenreMap.ToTvTmdbId(genre));

    private static UserWatchProfile Profile(string genre, int count)
    {
        var profile = new UserWatchProfile();
        for (var i = 0; i < count; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, PlayCount = 1, LastPlayedDate = DateTime.UtcNow, RuntimeTicks = TimeSpan.FromMinutes(45).Ticks, Genres = [genre] });
        }

        return profile;
    }
}
