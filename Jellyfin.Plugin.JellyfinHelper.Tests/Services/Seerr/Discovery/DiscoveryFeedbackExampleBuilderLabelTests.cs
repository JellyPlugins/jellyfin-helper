using System;
using System.Collections.Generic;
using System.Threading;
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

        var entry = ShownEntry();
        entry.KnownPeople = [sharedPerson, "Someone Else"];

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
    public void BuildDiscoveryExamples_ShownOnlyEntry_UsesShownLabel()
    {
        // An entry that was only shown (never requested/dismissed/watched) maps to the baseline shown
        // label - the weakest signal.
        var entry = ShownEntry();

        var examples = BuildFor(entry);

        Assert.Single(examples);
        Assert.Equal(EngineConstants.DiscoveryShownLabel, examples[0].Label, 6);
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
        ShownAtUtc = shownAt ?? DateTime.UtcNow
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
                LastPlayedDate = DateTime.UtcNow.AddDays(-i),
                Genres = [ActionGenre],
                Year = 2015
            });
        }

        return profile;
    }
}
