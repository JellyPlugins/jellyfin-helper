using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Comprehensive tests for the internal ReasonResolver reason-inference logic. Exercises every branch of DetermineReason, the private resolvers, and the response stripping helper.
/// </summary>
public class ReasonResolverTests
{
    private const double AboveThreshold = 0.20;

    private static Movie CreateMovie(Guid? id = null, string[]? genres = null, string[]? studios = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            Name = "Movie",
            Genres = genres ?? [],
            Studios = studios ?? []
        };

    private static ScoreExplanation Explain(
        string dominant = "Genre",
        double genre = 0.0,
        double collab = 0.0,
        double rating = 0.0,
        double recency = 0.0,
        double userRating = 0.0,
        double yearProx = 0.0,
        double interaction = 0.0,
        double people = 0.0,
        double studio = 0.0)
        => new()
        {
            DominantSignal = dominant,
            GenreContribution = genre,
            CollaborativeContribution = collab,
            RatingContribution = rating,
            RecencyContribution = recency,
            UserRatingContribution = userRating,
            YearProximityContribution = yearProx,
            InteractionContribution = interaction,
            PeopleContribution = people,
            StudioContribution = studio,
            FinalScore = 0.5,
            StrategyName = "Test"
        };

    [Fact]
    public void GenrePlusPeople_WithMatchedPerson_ReturnsNamedCombo()
    {
        var id = Guid.NewGuid();
        var candidate = CreateMovie(id, genres: ["Action", "Drama"]);
        var explanation = Explain(genre: AboveThreshold, people: AboveThreshold);
        var genrePrefs = new Dictionary<string, double> { { "Action", 1.0 }, { "Drama", 0.3 } };
        var preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice" };
        var peopleLookup = new Dictionary<Guid, HashSet<string>>
        {
            [id] = new(StringComparer.OrdinalIgnoreCase) { "Alice", "Bob" }
        };

        var (reason, key, related) = ReasonResolver.DetermineReason(
            candidate, explanation, genrePrefs, preferredPeople, peopleLookup: peopleLookup);

        Assert.Equal("reasonGenreAndPerson", key);
        Assert.Contains("Alice", reason);
        Assert.Contains("Action", reason);
        Assert.Equal("Alice | Action", related);
    }

    [Fact]
    public void GenrePlusPeople_NoLookupMatch_ReturnsGenericCombo()
    {
        var candidate = CreateMovie(genres: ["Action"]);
        var explanation = Explain(genre: AboveThreshold, people: AboveThreshold);
        var genrePrefs = new Dictionary<string, double> { { "Action", 1.0 } };

        var (_, key, related) = ReasonResolver.DetermineReason(candidate, explanation, genrePrefs);

        Assert.Equal("reasonGenreAndPeople", key);
        Assert.Equal("Action", related);
    }

    [Fact]
    public void GenrePlusCollaborative_ReturnsCombo()
    {
        var candidate = CreateMovie(genres: ["Sci-Fi"]);
        var explanation = Explain(genre: AboveThreshold, collab: AboveThreshold);
        var genrePrefs = new Dictionary<string, double> { { "Sci-Fi", 1.0 } };

        var (_, key, related) = ReasonResolver.DetermineReason(candidate, explanation, genrePrefs);

        Assert.Equal("reasonGenreAndCollab", key);
        Assert.Equal("Sci-Fi", related);
    }

    [Fact]
    public void RecencyPlusRating_ReturnsTrending()
    {
        var candidate = CreateMovie();
        var explanation = Explain(dominant: "Recency", recency: AboveThreshold, rating: AboveThreshold);

        var (reason, key, related) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>());

        Assert.Equal("reasonTrending", key);
        Assert.Contains("Trending", reason);
        Assert.Null(related);
    }

    [Fact]
    public void DominantCollaborative_ReturnsCollaborative()
    {
        var (_, key, _) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "Collaborative", collab: AboveThreshold),
            new Dictionary<string, double>());
        Assert.Equal("reasonCollaborative", key);
    }

    [Fact]
    public void DominantGenre_WithMatch_ReturnsGenre()
    {
        var candidate = CreateMovie(genres: ["Horror", "Thriller"]);
        var explanation = Explain(dominant: "Genre", genre: AboveThreshold);
        var prefs = new Dictionary<string, double> { { "Horror", 0.2 }, { "Thriller", 0.9 } };

        var (_, key, related) = ReasonResolver.DetermineReason(candidate, explanation, prefs);

        Assert.Equal("reasonGenre", key);
        Assert.Equal("Thriller", related);
    }

    [Fact]
    public void DominantGenre_NoMatch_FallsThroughToDefault()
    {
        var candidate = CreateMovie(genres: ["Horror"]);
        var explanation = Explain(dominant: "Genre", genre: AboveThreshold);
        var prefs = new Dictionary<string, double> { { "Action", 1.0 } };

        var (_, key, _) = ReasonResolver.DetermineReason(candidate, explanation, prefs);

        Assert.Equal("reasonDefault", key);
    }

    [Fact]
    public void DominantGenre_CandidateHasNoGenres_ReturnsDefault()
    {
        var candidate = CreateMovie(genres: []);
        var explanation = Explain(dominant: "Genre", genre: AboveThreshold);
        var prefs = new Dictionary<string, double> { { "Action", 1.0 } };

        var (_, key, _) = ReasonResolver.DetermineReason(candidate, explanation, prefs);

        Assert.Equal("reasonDefault", key);
    }

    [Fact]
    public void DominantRating_HighEnough_ReturnsHighlyRated()
    {
        var (_, key, _) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "Rating", rating: 0.08),
            new Dictionary<string, double>());
        Assert.Equal("reasonHighlyRated", key);
    }

    [Fact]
    public void DominantRating_Weak_ReturnsDefault()
    {
        var (_, key, _) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "Rating", rating: 0.02),
            new Dictionary<string, double>());
        Assert.Equal("reasonDefault", key);
    }

    [Fact]
    public void DominantUserRating_ReturnsUserRating()
    {
        var (_, key, _) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "UserRating", userRating: AboveThreshold),
            new Dictionary<string, double>());
        Assert.Equal("reasonUserRating", key);
    }

    [Fact]
    public void DominantRecency_ReturnsRecent()
    {
        var (_, key, _) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "Recency", recency: AboveThreshold),
            new Dictionary<string, double>());
        Assert.Equal("reasonRecent", key);
    }

    [Fact]
    public void DominantYearProximity_ReturnsEra()
    {
        var (_, key, _) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "YearProximity", yearProx: AboveThreshold),
            new Dictionary<string, double>());
        Assert.Equal("reasonYearProximity", key);
    }

    [Fact]
    public void DominantInteraction_ReturnsInteraction()
    {
        var (_, key, _) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "Interaction", interaction: AboveThreshold),
            new Dictionary<string, double>());
        Assert.Equal("reasonInteraction", key);
    }

    [Fact]
    public void DominantPeople_WithMatchedPerson_ReturnsNamed()
    {
        var id = Guid.NewGuid();
        var candidate = CreateMovie(id);
        var explanation = Explain(dominant: "People", people: AboveThreshold);
        var preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tom Hanks" };
        var peopleLookup = new Dictionary<Guid, HashSet<string>>
        {
            [id] = new(StringComparer.OrdinalIgnoreCase) { "Tom Hanks", "Meg Ryan" }
        };

        var (reason, key, related) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(), preferredPeople,
            peopleLookup: peopleLookup);

        Assert.Equal("reasonPersonNamed", key);
        Assert.Contains("Tom Hanks", reason);
        Assert.Equal("Tom Hanks", related);
    }

    [Fact]
    public void DominantPeople_WithWeights_PicksHeaviest()
    {
        var id = Guid.NewGuid();
        var candidate = CreateMovie(id);
        var explanation = Explain(dominant: "People", people: AboveThreshold);
        var preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Cameo", "Heavy" };
        var peopleLookup = new Dictionary<Guid, HashSet<string>>
        {
            [id] = new(StringComparer.OrdinalIgnoreCase) { "Cameo", "Heavy" }
        };
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Cameo", 0.1 },
            { "Heavy", 5.0 }
        };

        var (_, key, related) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(), preferredPeople,
            peopleLookup: peopleLookup, preferredPeopleWeights: weights);

        Assert.Equal("reasonPersonNamed", key);
        Assert.Equal("Heavy", related);
    }

    [Fact]
    public void DominantPeople_WithWeights_NoMatchingCandidatePeople_FallsBackNull()
    {
        // Weights supplied but no candidate person is in the preferred set -> best-weight branch
        // must yield null, then final line returns null (no "any-match" hit either).
        var id = Guid.NewGuid();
        var candidate = CreateMovie(id);
        var explanation = Explain(dominant: "People", people: AboveThreshold);
        var preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice" };
        var peopleLookup = new Dictionary<Guid, HashSet<string>>
        {
            [id] = new(StringComparer.OrdinalIgnoreCase) { "Bob", "Carol" }
        };
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            { "Alice", 3.0 }
        };

        var (_, key, related) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(), preferredPeople,
            peopleLookup: peopleLookup, preferredPeopleWeights: weights);

        // No match at all -> falls back to generic reasonPeople branch (with related=null)
        Assert.Equal("reasonPeople", key);
        Assert.Null(related);
    }

    [Fact]
    public void DominantPeople_NoLookup_ReturnsGenericPeople()
    {
        var candidate = CreateMovie();
        var explanation = Explain(dominant: "People", people: AboveThreshold);

        var (_, key, related) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>());

        Assert.Equal("reasonPeople", key);
        Assert.Null(related);
    }

    [Fact]
    public void DominantPeople_EmptyPreferredSet_ReturnsGenericPeople()
    {
        var id = Guid.NewGuid();
        var candidate = CreateMovie(id);
        var explanation = Explain(dominant: "People", people: AboveThreshold);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var peopleLookup = new Dictionary<Guid, HashSet<string>>
        {
            [id] = new(StringComparer.OrdinalIgnoreCase) { "Someone" }
        };

        var (_, key, _) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(),
            preferredPeople: empty, peopleLookup: peopleLookup);

        Assert.Equal("reasonPeople", key);
    }

    [Fact]
    public void DominantPeople_LookupMissingCandidate_ReturnsGeneric()
    {
        var candidate = CreateMovie();
        var explanation = Explain(dominant: "People", people: AboveThreshold);
        var preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice" };
        var peopleLookup = new Dictionary<Guid, HashSet<string>>(); // empty lookup

        var (_, key, _) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(),
            preferredPeople: preferredPeople, peopleLookup: peopleLookup);

        Assert.Equal("reasonPeople", key);
    }

    [Fact]
    public void DominantStudio_WithMatchedStudio_ReturnsNamed()
    {
        var candidate = CreateMovie(studios: ["A24", "MGM"]);
        var explanation = Explain(dominant: "Studio", studio: AboveThreshold);
        var preferredStudios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MGM" };

        var (reason, key, related) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(),
            preferredStudios: preferredStudios);

        Assert.Equal("reasonStudioNamed", key);
        Assert.Contains("MGM", reason);
        Assert.Equal("MGM", related);
    }

    [Fact]
    public void DominantStudio_NoMatch_ReturnsGeneric()
    {
        var candidate = CreateMovie(studios: ["A24"]);
        var explanation = Explain(dominant: "Studio", studio: AboveThreshold);
        var preferredStudios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MGM" };

        var (_, key, related) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(),
            preferredStudios: preferredStudios);

        Assert.Equal("reasonStudio", key);
        Assert.Null(related);
    }

    [Fact]
    public void DominantStudio_CandidateHasEmptyStudios_ReturnsGeneric()
    {
        var candidate = CreateMovie(studios: []);
        var explanation = Explain(dominant: "Studio", studio: AboveThreshold);
        var preferredStudios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MGM" };

        var (_, key, _) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(),
            preferredStudios: preferredStudios);

        Assert.Equal("reasonStudio", key);
    }

    [Fact]
    public void DominantStudio_NullPreferredStudios_ReturnsGeneric()
    {
        var candidate = CreateMovie(studios: ["A24"]);
        var explanation = Explain(dominant: "Studio", studio: AboveThreshold);

        var (_, key, _) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>());

        Assert.Equal("reasonStudio", key);
    }

    [Fact]
    public void DominantStudio_BelowThreshold_ReturnsDefault()
    {
        // Studio dominant but contribution <= ReasonScoreThreshold -> default path
        var candidate = CreateMovie(studios: ["A24"]);
        var explanation = Explain(dominant: "Studio", studio: 0.01);
        var preferredStudios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A24" };

        var (_, key, _) = ReasonResolver.DetermineReason(
            candidate, explanation, new Dictionary<string, double>(),
            preferredStudios: preferredStudios);

        Assert.Equal("reasonDefault", key);
    }

    [Fact]
    public void UnknownDominantSignal_ReturnsDefault()
    {
        var (reason, key, related) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "Mystery"),
            new Dictionary<string, double>());

        Assert.Equal("reasonDefault", key);
        Assert.Contains("Recommended", reason);
        Assert.Null(related);
    }

    [Fact]
    public void DominantSignal_Uppercase_MatchesIgnoreCase()
    {
        var (_, key, _) = ReasonResolver.DetermineReason(
            CreateMovie(),
            Explain(dominant: "RECENCY", recency: AboveThreshold),
            new Dictionary<string, double>());

        Assert.Equal("reasonRecent", key);
    }

    [Fact]
    public void StripWatchedItemsForResponse_ClearsList_KeepsAggregates()
    {
        var favSeries = Guid.NewGuid();
        var profile = new UserWatchProfile
        {
            UserId = Guid.NewGuid(),
            UserName = "Alice",
            WatchedMovieCount = 10,
            WatchedEpisodeCount = 20,
            WatchedSeriesCount = 3,
            TotalWatchTimeTicks = 12345,
            LastActivityDate = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            GenreDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "Action", 5 },
                { "Drama", 3 }
            },
            FavoriteCount = 2,
            FavoriteSeriesIds = new HashSet<Guid> { favSeries },
            AverageCommunityRating = 7.5,
            MaxParentalRating = 18,
            WatchedItems = new System.Collections.ObjectModel.Collection<WatchedItemInfo>
            {
                new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true }
            }
        };

        var stripped = ReasonResolver.StripWatchedItemsForResponse(profile);

        Assert.Equal(profile.UserId, stripped.UserId);
        Assert.Equal("Alice", stripped.UserName);
        Assert.Equal(10, stripped.WatchedMovieCount);
        Assert.Equal(20, stripped.WatchedEpisodeCount);
        Assert.Equal(3, stripped.WatchedSeriesCount);
        Assert.Equal(12345L, stripped.TotalWatchTimeTicks);
        Assert.Equal(profile.LastActivityDate, stripped.LastActivityDate);
        Assert.Equal(2, stripped.FavoriteCount);
        Assert.Equal(7.5, stripped.AverageCommunityRating);
        Assert.Equal(18, stripped.MaxParentalRating);
        Assert.Contains(favSeries, stripped.FavoriteSeriesIds);

        // Watched items must be cleared
        Assert.Empty(stripped.WatchedItems);

        // The genre distribution comparer must be preserved (OrdinalIgnoreCase)
        // so consumers can look up genres case-insensitively.
        Assert.True(stripped.GenreDistribution.ContainsKey("action"));
        Assert.Equal(2, stripped.GenreDistribution.Count);

        // Independence: mutating the copy must NOT affect the original
        stripped.GenreDistribution["NewGenre"] = 42;
        stripped.FavoriteSeriesIds.Add(Guid.NewGuid());
        Assert.False(profile.GenreDistribution.ContainsKey("NewGenre"));
        Assert.Single(profile.FavoriteSeriesIds);
    }

    [Fact]
    public void StripWatchedItemsForResponse_EmptyProfile_ProducesEmptyStripped()
    {
        var profile = new UserWatchProfile
        {
            UserId = Guid.Empty,
            UserName = string.Empty,
            GenreDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            FavoriteSeriesIds = new HashSet<Guid>(),
            WatchedItems = new System.Collections.ObjectModel.Collection<WatchedItemInfo>()
        };

        var stripped = ReasonResolver.StripWatchedItemsForResponse(profile);

        Assert.Empty(stripped.WatchedItems);
        Assert.Empty(stripped.GenreDistribution);
        Assert.Empty(stripped.FavoriteSeriesIds);
        Assert.NotSame(profile.GenreDistribution, stripped.GenreDistribution);
        Assert.NotSame(profile.FavoriteSeriesIds, stripped.FavoriteSeriesIds);
    }
}
