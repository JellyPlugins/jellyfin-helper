using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Branch-coverage for ExternalCandidateFeatureBuilder covering paths like
///     ArgumentNullException.ThrowIfNull guards for all four required inputs.
/// </summary>
public sealed class ExternalCandidateFeatureBuilderExtendedTests
{
    private const int ActionTmdbGenreId = 28;
    private const string ActionGenre = "Action";

    // Argument guards

    [Fact]
    public void Build_NullCandidate_Throws()
    {
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);
        Assert.Throws<ArgumentNullException>(() =>
            ExternalCandidateFeatureBuilder.Build(
                candidate: null!,
                prefs,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                2015.0,
                exposure));
    }

    [Fact]
    public void Build_NullGenrePreferences_Throws()
    {
        var candidate = MinimalCandidate();
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);
        Assert.Throws<ArgumentNullException>(() =>
            ExternalCandidateFeatureBuilder.Build(
                candidate,
                genrePreferences: null!,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                2015.0,
                exposure));
    }

    [Fact]
    public void Build_NullPreferredPeople_Throws()
    {
        var candidate = MinimalCandidate();
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);
        Assert.Throws<ArgumentNullException>(() =>
            ExternalCandidateFeatureBuilder.Build(
                candidate,
                prefs,
                preferredPeople: null!,
                2015.0,
                exposure));
    }

    [Fact]
    public void Build_NullGenreExposure_Throws()
    {
        var candidate = MinimalCandidate();
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        Assert.Throws<ArgumentNullException>(() =>
            ExternalCandidateFeatureBuilder.Build(
                candidate,
                prefs,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                2015.0,
                genreExposure: null!));
    }

    // Defensive comparer rebuild

    [Fact]
    public void Build_PreferredPeopleWithCaseSensitiveComparer_RebuildsAsIgnoreCase()
    {
        // BUG GUARD: TMDb returns names like "leonardo dicaprio" while the profile stores "Leonardo DiCaprio".
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);
        var caseSensitivePeople = new HashSet<string>(StringComparer.Ordinal)
        {
            "Leonardo DiCaprio"
        };

        var candidate = MinimalCandidate();
        candidate.KnownPeople = ["leonardo dicaprio"]; // lower-case from TMDb

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            prefs,
            caseSensitivePeople,
            2015.0,
            exposure);

        // With the rebuild the case-insensitive match yields 1 overlap out of Min(5,1)=1
        // -> PeopleSimilarity = 1.0. Without it, the value would collapse to 0.0.
        Assert.Equal(1.0, features.PeopleSimilarity, 6);
    }

    // EffectiveReleaseDate handling

    [Fact]
    public void Build_CandidateWithoutReleaseDate_UsesNeutralRecencyAndNullYearProximity()
    {
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 700,
            MediaType = "movie",
            Title = "Missing Date",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.0,
            Popularity = 50.0
            // ReleaseDate NOT set -> EffectiveReleaseDate = null
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            prefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            2015.0,
            exposure);

        // RecencyScore falls back to neutral 0.5 when the candidate has no release date.
        Assert.Equal(0.5, features.RecencyScore, 6);
        // YearProximityScore is invoked with null -> should NOT throw and must produce a
        // finite score (the underlying ContentScoring.ComputeYearProximity contract).
        Assert.True(double.IsFinite(features.YearProximityScore));
    }

    [Fact]
    public void Build_TvCandidateUsesFirstAirDateForRecency()
    {
        // BUG GUARD: TV items typically have FirstAirDate populated while ReleaseDate is empty. The TmdbDiscoverItem.EffectiveReleaseDate property falls back to FirstAirDate - this test locks the contract that a TV item DOES receive a non-neutral recency signal (i.e.
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        // Two candidates with different FirstAirDate values so we can prove the score varies with the date - that alone forces the non-null branch through and rules out "always returns 0.5" regressions.
        var recent = new TmdbDiscoverItem
        {
            Id = 701,
            MediaType = "tv",
            Name = "Recent TV",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.0,
            Popularity = 100.0,
            FirstAirDate = DateTime.UtcNow.AddMonths(-1)
        };

        var old = new TmdbDiscoverItem
        {
            Id = 702,
            MediaType = "tv",
            Name = "Old TV",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.0,
            Popularity = 100.0,
            FirstAirDate = DateTime.UtcNow.AddYears(-15)
        };

        var recentFeatures = ExternalCandidateFeatureBuilder.Build(
            recent, prefs, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 2015.0, exposure);
        var oldFeatures = ExternalCandidateFeatureBuilder.Build(
            old, prefs, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 2015.0, exposure);

        Assert.True(recentFeatures.IsSeries);
        Assert.True(oldFeatures.IsSeries);
        // The recent TV MUST score strictly higher than the ancient one - that proves
        // FirstAirDate was consulted (rather than both falling through to a constant).
        Assert.True(
            recentFeatures.RecencyScore > oldFeatures.RecencyScore,
            $"recent ({recentFeatures.RecencyScore}) should score higher than old ({oldFeatures.RecencyScore})");
    }

    [Fact]
    public void Build_MediaTypeCaseInsensitive_TreatsUpperCaseTvAsSeries()
    {
        // The `IsSeries` computation uses OrdinalIgnoreCase - verify the branch by feeding
        // a payload with an unusual casing that a strict-equality regression would miss.
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        var candidate = new TmdbDiscoverItem
        {
            Id = 702,
            MediaType = "TV", // uppercase - spec allows either case
            Name = "Uppercase TV",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 7.0,
            Popularity = 50.0,
            FirstAirDate = DateTime.UtcNow.AddYears(-2)
        };

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            prefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            2015.0,
            exposure);

        Assert.True(features.IsSeries);
    }

    // ComputePeopleSimilarity guard branches

    [Fact]
    public void Build_EmptyPreferredPeople_YieldsZeroPeopleSimilarity()
    {
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        var candidate = MinimalCandidate();
        candidate.KnownPeople = ["Some Actor", "Some Director"];

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            prefs,
            preferredPeople: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            2015.0,
            exposure);

        Assert.Equal(0.0, features.PeopleSimilarity, 6);
    }

    [Fact]
    public void Build_NullKnownPeople_YieldsZeroPeopleSimilarity()
    {
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        var candidate = MinimalCandidate();
        candidate.KnownPeople = null; // unenriched candidate

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            prefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice", "Bob" },
            2015.0,
            exposure);

        Assert.Equal(0.0, features.PeopleSimilarity, 6);
    }

    [Fact]
    public void Build_EmptyKnownPeople_YieldsZeroPeopleSimilarity()
    {
        var profile = BuildProfile();
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        var candidate = MinimalCandidate();
        candidate.KnownPeople = []; // enriched but no people returned

        var features = ExternalCandidateFeatureBuilder.Build(
            candidate,
            prefs,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice" },
            2015.0,
            exposure);

        Assert.Equal(0.0, features.PeopleSimilarity, 6);
    }

    // ComputePeopleSimilarityFromNames branch coverage

    [Fact]
    public void ComputePeopleSimilarityFromNames_NullNames_ReturnsZero()
    {
        var result = ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames(
            knownPeople: null,
            preferredPeople: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice" });
        Assert.Equal(0.0, result, 6);
    }

    [Fact]
    public void ComputePeopleSimilarityFromNames_EmptyPreferred_ReturnsZero()
    {
        var result = ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames(
            knownPeople: ["Alice"],
            preferredPeople: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(0.0, result, 6);
    }

    [Fact]
    public void ComputePeopleSimilarityFromNames_FiltersWhitespaceAndDedupes()
    {
        // "Alice" appears three times (director credit, writer credit, executive-producer credit) - a naive Count() would double-boost the score.
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice", "Bob", "Carol", "Dan", "Eve" };
        string[] known = ["Alice", "alice", "ALICE", "   ", "", "Bob"];

        var result = ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames(known, preferred);

        // 2 distinct matches (Alice, Bob) out of Min(5, 5) = 5 -> 2/5 = 0.4
        Assert.Equal(0.4, result, 6);
    }

    [Fact]
    public void ComputePeopleSimilarityFromNames_SmallPreferredSet_UsesMinCap()
    {
        // The formula divides by Min(preferred.Count, MinPeopleForFullScore=5). A user
        // with only 2 preferred people gets full score (1.0) after 2 matches.
        var preferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alice", "Bob" };
        string[] known = ["Alice", "Bob"];

        var result = ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames(known, preferred);

        Assert.Equal(1.0, result, 6);
    }

    // Test helpers

    private static TmdbDiscoverItem MinimalCandidate() =>
        new()
        {
            Id = 1,
            MediaType = "movie",
            Title = "Test",
            GenreIds = [ActionTmdbGenreId],
            VoteAverage = 5.0,
            Popularity = 10.0
        };

    private static UserWatchProfile BuildProfile()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 5; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = DateTime.UtcNow.AddDays(-i),
                Genres = [ActionGenre],
                Year = 2015
            });
        }
        return profile;
    }
}
