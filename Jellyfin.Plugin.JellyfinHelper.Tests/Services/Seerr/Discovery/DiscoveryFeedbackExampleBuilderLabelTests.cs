using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for BuildDiscoveryExamples covering the interaction-status -> label mapping, the most-recent-interaction timestamp selection used for temporal holdout placement, and the people-similarity feature.
/// </summary>
public sealed class DiscoveryFeedbackExampleBuilderLabelTests
{
    private const string ActionGenre = "Action";

    [Theory]
    [InlineData("dismissed")]
    [InlineData("requested")]
    [InlineData("watched")]
    [InlineData("legacy-watched")]
    public void LaterVisibilityCannotRenewTheAgeOrStrengthOfAnOlderObservedOutcome(string kind)
    {
        var outcome = DateTime.UtcNow.AddDays(-365);
        var entry = ShownEntry(outcome.AddDays(-1));
        entry.ImpressionAtUtc = DateTime.UtcNow.AddDays(-1);
        entry.ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7, IsExternalCandidate = true };
        if (kind == "dismissed") entry.DismissedAtUtc = outcome;
        else entry.RequestedAtUtc = outcome;
        if (kind is "watched" or "legacy-watched") entry.WasWatched = true;
        if (kind == "watched") entry.WatchedAtUtc = outcome;
        var vector = entry.ScoringFeatures.ToVector();
        var example = Assert.Single(BuildFor(entry));
        Assert.True(example.IsObserved);
        Assert.False(example.IsPassiveSample);
        Assert.Equal(outcome, example.GeneratedAtUtc);
        Assert.InRange(example.ComputeEffectiveWeight(), 0, .04);
        Assert.Equal(vector, example.Features.ToVector());
        Assert.Equal(vector, entry.ScoringFeatures.ToVector());
        Assert.Equal(outcome.AddDays(-1), entry.ShownAtUtc);
        Assert.True(entry.ImpressionAtUtc > example.GeneratedAtUtc);
    }

    [Theory]
    [InlineData(false, 90)]
    [InlineData(false, 3650)]
    [InlineData(true, 1)]
    public void UnseenGenerationAndRecentVisibilityCannotEnterAnyOptimizerOrIndependentAcceptance(bool visible, int days)
    {
        var entry = ShownEntry();
        entry.ShownAtUtc = DateTime.UtcNow.AddDays(-days);
        entry.ImpressionAtUtc = visible ? entry.ShownAtUtc : null;
        entry.ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7, IsExternalCandidate = true };
        var vector = entry.ScoringFeatures.ToVector();
        Assert.Empty(BuildFor(entry));
        Assert.Equal(vector, entry.ScoringFeatures.ToVector());
        Assert.Null(entry.DismissedAtUtc);
    }

    [Fact]
    public void ManyIgnoredCardsHaveABoundedDistinctBudgetAndRealActionsRemainStrongerAndIndependent()
    {
        var user = Guid.NewGuid();
        var first = new DiscoveryFeedbackResult { UserId = user };
        foreach (var id in Enumerable.Range(1, 50))
        {
            var entry = ShownEntry();
            entry.TmdbId = id;
            entry.ScoringFeatures = new CandidateFeatures { GenreSimilarity = .7, IsExternalCandidate = true };
            first.Entries.Add(entry);
        }

        var action = ShownEntry();
        action.TmdbId = 51;
        action.DismissedAtUtc = DateTime.UtcNow;
        first.Entries.Add(action);
        var requested = ShownEntry();
        requested.TmdbId = 52;
        requested.RequestedAtUtc = DateTime.UtcNow;
        first.Entries.Add(requested);
        var duplicate = new DiscoveryFeedbackResult { UserId = user, Entries = first.Entries.Take(20).ToList() };
        var built = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([first, duplicate], new Dictionary<Guid, UserWatchProfile>(), null, null, TestContext.Current.CancellationToken);
        var passive = built.Examples.Where(example => example.IsPassiveSample).ToArray();
        Assert.Equal(8, passive.Length);
        Assert.Equal(8, passive.Select(example => example.ObservationId).Distinct().Count());
        Assert.InRange(passive.Sum(example => example.SampleWeight), .19, .21);
        Assert.All(passive, example => { Assert.False(example.IsObserved); Assert.False(example.IsContrastSample); Assert.Equal(.45, example.Label); });
        Assert.Equal(2, built.Examples.Count(example => example.IsObserved));
        Assert.Equal(.6, Assert.Single(built.Examples, example => example.IsObserved && example.Label == 0).SampleWeight);
        Assert.Equal(EngineConstants.DiscoveryRequestedLabel, Assert.Single(built.Examples, example => example.IsObserved && example.Label > .5).Label);
        Assert.False(EnsembleAdmissionEvaluator.HasIndependentAcceptanceSupport(passive, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ActualPlaybackOrExplicitOpinionWithdrawsTheSameTypedTitlesPassiveComparison(bool complete, bool rating)
    {
        var user = Guid.NewGuid();
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo
        {
            ItemId = Guid.NewGuid(), ItemType = "Movie", TmdbId = 700, Played = complete,
            PlaybackPositionTicks = complete || rating ? 0 : 30, RuntimeTicks = 100, UserRating = rating ? 8 : null
        }] };
        var feedback = WithEntry(user, ShownEntry());
        var built = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([feedback], new Dictionary<Guid, UserWatchProfile> { [user] = profile }, null, null, TestContext.Current.CancellationToken);
        Assert.Empty(built.Examples);
        feedback.Entries[0].MediaType = "tv";
        Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([feedback], new Dictionary<Guid, UserWatchProfile> { [user] = profile }, null, null, TestContext.Current.CancellationToken).Examples);
        Assert.Null(feedback.Entries[0].DismissedAtUtc);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReconstructedFeedback_UsesActualGenreEvidenceWithoutChangingConfirmedSnapshots(bool candidateGenres, bool profileGenres)
    {
        var user = Guid.NewGuid();
        var shown = DateTime.UtcNow.AddDays(-3);
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), Played = true, LastPlayedDate = shown.AddDays(-1), Genres = profileGenres ? ["Action"] : [] }] };
        var entry = new DiscoveryFeedbackEntry { TmdbId = 101, ShownAtUtc = shown, RequestedAtUtc = shown.AddHours(1), Genres = candidateGenres ? ["Action"] : [], TmdbRating = 9, Popularity = 50 };
        var feedback = new DiscoveryFeedbackResult { UserId = user, Entries = [entry] };
        var profiles = new Dictionary<Guid, UserWatchProfile> { [user] = profile };
        var reconstructed = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([feedback], profiles, null, null, TestContext.Current.CancellationToken).Examples).Features;
        Assert.Equal(candidateGenres && profileGenres, reconstructed.HasGenreEvidence);
        entry.ScoringFeatures = new CandidateFeatures { HasGenreEvidence = false, GenreSimilarity = 0.5 };
        entry.PredictionEvidenceAtUtc = shown;
        var confirmed = Assert.Single(DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([feedback], profiles, null, null, TestContext.Current.CancellationToken).Examples).Features;
        Assert.False(confirmed.HasGenreEvidence);
        Assert.Equal(0.5, confirmed.GenreSimilarity);
    }

    [Fact]
    public void BuildDiscoveryExamples_NoFeedback_ReturnsEmptyWithZeroCount()
    {
        // Empty input must short-circuit before any per-user work so training callers
        // don't emit phantom examples.
        var (examples, count) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
            Array.Empty<DiscoveryFeedbackResult>(),
            new Dictionary<Guid, UserWatchProfile>(),
            seriesEpisodeCounts: null,
            featureMeans: null,
            CancellationToken.None);

        Assert.Empty(examples);
        Assert.Equal(0, count);
    }

    [Fact]
    public void BuildDiscoveryExamples_RequestedAndWatchedEntry_UsesStrongestLabel()
    {
        // Request + watch is the strongest positive signal and must map to the top label,
        // outranking a bare request.
        var entry = ShownEntry();
        entry.RequestedAtUtc = DateTime.UtcNow;
        entry.WasWatched = true;

        var examples = BuildFor(entry);

        Assert.Equal(EngineConstants.DiscoveryRequestedAndWatchedLabel, examples[0].Label, 6);
    }

    [Fact]
    public void BuildDiscoveryExamples_RequestedButNotWatchedEntry_UsesRequestedLabel()
    {
        // A request without a confirmed watch is a weaker positive than requested-and-watched.
        var entry = ShownEntry();
        entry.RequestedAtUtc = DateTime.UtcNow;
        entry.WasWatched = false;

        var examples = BuildFor(entry);

        Assert.Equal(EngineConstants.DiscoveryRequestedLabel, examples[0].Label, 6);
        Assert.NotEqual(EngineConstants.DiscoveryRequestedAndWatchedLabel, examples[0].Label);
    }

    [Fact]
    public void BuildDiscoveryExamples_DismissedEntry_UsesDismissedLabel()
    {
        // An explicit dismissal is the negative target.
        var entry = ShownEntry();
        entry.DismissedAtUtc = DateTime.UtcNow;

        var examples = BuildFor(entry);

        Assert.Equal(EngineConstants.DiscoveryDismissedLabel, examples[0].Label, 6);
    }

    [Fact]
    public void BuildDiscoveryExamples_DismissedAfterShown_TimestampIsDismissal()
    {
        // GeneratedAtUtc must be the latest interaction so the example lands in the right
        // temporal cutoff/holdout window; the dismissal happened after the item was shown.
        var shownAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var dismissedAt = shownAt.AddDays(3);
        var entry = ShownEntry(shownAt);
        entry.DismissedAtUtc = dismissedAt;

        var examples = BuildFor(entry);

        Assert.Equal(dismissedAt, examples[0].GeneratedAtUtc);
    }

    [Fact]
    public void BuildDiscoveryExamples_RequestedAfterShown_TimestampIsRequest()
    {
        var shownAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var requestedAt = shownAt.AddDays(2);
        var entry = ShownEntry(shownAt);
        entry.RequestedAtUtc = requestedAt;
        entry.WasWatched = false;

        var examples = BuildFor(entry);

        Assert.Equal(requestedAt, examples[0].GeneratedAtUtc);
    }

    [Fact]
    public void BuildDiscoveryExamples_WatchedIsLatest_TimestampIsWatchTime()
    {
        // Watched last of all four timestamps => it wins the max selection.
        var shownAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var requestedAt = shownAt.AddDays(2);
        var watchedAt = shownAt.AddDays(5);
        var entry = ShownEntry(shownAt);
        entry.RequestedAtUtc = requestedAt;
        entry.WasWatched = true;
        entry.WatchedAtUtc = watchedAt;

        var examples = BuildFor(entry);

        Assert.Equal(watchedAt, examples[0].GeneratedAtUtc);
    }

    [Fact]
    public void BuildDiscoveryExamples_KnownPeopleOverlapPreferredPeople_ProducesPositivePeopleSimilarity()
    {
        // A person the user has watched in >=2 items becomes a "top person"; a candidate whose KnownPeople include that person must produce a positive PeopleSimilarity feature that matches the shared inference helper exactly.
        const string sharedPerson = "Keanu Reeves";
        var userId = Guid.NewGuid();
        var profile = BuildActionHeavyProfile(userId);
        profile.PeopleProfile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [sharedPerson] = 3
        };
        foreach (var watch in profile.WatchedItems.Take(3)) watch.PeopleNames = [sharedPerson];

        var entry = ShownEntry();
        entry.KnownPeople = [sharedPerson, "Someone Else"];
        entry.RequestedAtUtc = DateTime.UtcNow;

        var profileById = new Dictionary<Guid, UserWatchProfile> { [userId] = profile };
        var (examples, _) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
            [WithEntry(userId, entry)],
            profileById,
            seriesEpisodeCounts: null,
            featureMeans: null,
            CancellationToken.None);

        var preferredPeople = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in profile.TopPeople)
        {
            preferredPeople.Add(person);
        }

        var expected = ExternalCandidateFeatureBuilder.ComputePeopleSimilarityFromNames(
            entry.KnownPeople, preferredPeople);

        Assert.True(examples[0].Features.PeopleSimilarity > 0.0);
        Assert.Equal(expected, examples[0].Features.PeopleSimilarity, 6);
    }

    [Fact]
    public void BuildDiscoveryExamples_ShownOnlyEntryHasBoundedPassiveInfluenceNotAnObservedDislike()
    {
        // Confirmed visibility can weakly influence fitting after its window, but
        // cannot supply observed negative labels or independent model admission.
        var entry = ShownEntry();

        var examples = BuildFor(entry);

        var example = Assert.Single(examples);
        Assert.Equal(EngineConstants.DiscoveryShownLabel, example.Label, 6);
        Assert.InRange(example.Label, .4, .5);
        Assert.Equal(EngineConstants.DiscoveryPassiveSampleWeight, example.SampleWeight);
        Assert.True(example.IsPassiveSample);
        Assert.False(example.IsObserved);
        Assert.False(example.IsContrastSample);
        Assert.True(example.Copy().IsPassiveSample);
        Assert.False(EnsembleAdmissionEvaluator.HasIndependentAcceptanceSupport(examples, DateTime.UtcNow));
    }

    [Fact]
    public void BuildDiscoveryExamples_UserWithNoEntries_IsSkipped()
    {
        // A feedback result present in the list but carrying zero entries must be skipped without
        // emitting an example (guards the per-user empty-Entries continue).
        var userId = Guid.NewGuid();
        var profileById = new Dictionary<Guid, UserWatchProfile>
        {
            [userId] = BuildActionHeavyProfile(userId)
        };
        var emptyFeedback = new DiscoveryFeedbackResult { UserId = userId };

        var (examples, count) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
            [emptyFeedback],
            profileById,
            seriesEpisodeCounts: null,
            featureMeans: null,
            CancellationToken.None);

        Assert.Empty(examples);
        Assert.Equal(0, count);
    }

    private static DiscoveryFeedbackEntry ShownEntry(DateTime? shownAt = null) => new()
    {
        TmdbId = 700,
        MediaType = "movie",
        Title = "Fixture",
        Year = 2015,
        Genres = [ActionGenre],
        TmdbRating = 7.0,
        Popularity = 120.0,
        ShownAtUtc = shownAt ?? DateTime.UtcNow.AddDays(-EngineConstants.RecommendationInfluenceWindowDays - 2),
        ImpressionAtUtc = shownAt?.AddDays(1) ?? DateTime.UtcNow.AddDays(-EngineConstants.RecommendationInfluenceWindowDays - 1)
    };

    private static DiscoveryFeedbackResult WithEntry(Guid userId, DiscoveryFeedbackEntry entry)
    {
        var feedback = new DiscoveryFeedbackResult { UserId = userId };
        feedback.Entries.Add(entry);
        return feedback;
    }

    private static List<TrainingExample> BuildFor(DiscoveryFeedbackEntry entry)
    {
        var userId = Guid.NewGuid();
        var profileById = new Dictionary<Guid, UserWatchProfile>
        {
            [userId] = BuildActionHeavyProfile(userId)
        };

        var (examples, _) = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples(
            [WithEntry(userId, entry)],
            profileById,
            seriesEpisodeCounts: null,
            featureMeans: null,
            CancellationToken.None);

        return examples;
    }

    private static UserWatchProfile BuildActionHeavyProfile(Guid? userId = null)
    {
        var profile = new UserWatchProfile { UserId = userId ?? Guid.NewGuid() };

        for (var i = 0; i < 35; i++)
        {
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = DateTime.UtcNow.AddDays(-EngineConstants.RecommendationInfluenceWindowDays - i - 3),
                Genres = [ActionGenre],
                Year = 2015
            });
        }

        return profile;
    }
}
