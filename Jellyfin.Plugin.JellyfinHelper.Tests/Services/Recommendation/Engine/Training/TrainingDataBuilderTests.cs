using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>
///     Tests for <see cref="TrainingDataBuilder.BuildExamples"/>.
///     Focus is on the F-01 regression: Phase 3 (cross-user random negatives) must be
///     reproducible across runs with identical input, otherwise training weights drift
///     between runs and Regressions-Tests against the ensemble output become flaky.
/// </summary>
public sealed class TrainingDataBuilderTests
{
    [Fact]
    public void BuildExamples_Phase3RandomNegatives_AreDeterministicAcrossRuns()
    {
        // Two users, each with one recommendation of a unique item. Neither user has
        // watched the OTHER's recommended item → both are eligible cross-user negatives
        // in Phase 3. We repeat BuildExamples() with an identical fresh copy of the input
        // (same GUIDs, same order) — the deterministic seed introduced in F-01 must yield
        // bit-identical randomNegativeCount, generatedAtUtc, and label sequences across
        // both runs. A regression to Random.Shared would keep the counts stable (sample
        // size is capped) but the picked items would change on nearly every retry.
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var generatedAt = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        (Collection<UserWatchProfile> Profiles, List<RecommendationResult> Results) BuildInput()
        {
            var profiles = new Collection<UserWatchProfile>
            {
                new()
                {
                    UserId = userA,
                    UserName = "A",
                    WatchedItems = [new WatchedItemInfo { ItemId = itemA, Played = true, LastPlayedDate = generatedAt }]
                },
                new()
                {
                    UserId = userB,
                    UserName = "B",
                    WatchedItems = [new WatchedItemInfo { ItemId = itemB, Played = true, LastPlayedDate = generatedAt }]
                }
            };

            var results = new List<RecommendationResult>
            {
                new()
                {
                    UserId = userA,
                    UserName = "A",
                    GeneratedAt = generatedAt,
                    Recommendations =
                    {
                        new RecommendedItem { ItemId = itemA, Name = "A-rec", ItemType = "Movie", Genres = ["Action"] }
                    }
                },
                new()
                {
                    UserId = userB,
                    UserName = "B",
                    GeneratedAt = generatedAt,
                    Recommendations =
                    {
                        new RecommendedItem { ItemId = itemB, Name = "B-rec", ItemType = "Movie", Genres = ["Drama"] }
                    }
                }
            };

            return (profiles, results);
        }

        var (profiles1, results1) = BuildInput();
        var (profiles2, results2) = BuildInput();

        var run1 = TrainingDataBuilder.BuildExamples(results1, profiles1, CancellationToken.None);
        var run2 = TrainingDataBuilder.BuildExamples(results2, profiles2, CancellationToken.None);

        // Sanity: Phase 3 actually fired (would be 0 if the setup didn't produce eligible negatives).
        Assert.True(run1.RandomNegativeCount > 0,
            "Phase 3 must produce at least one negative for this test to guard determinism.");

        // Same phase counts across runs.
        Assert.Equal(run1.OrganicCount, run2.OrganicCount);
        Assert.Equal(run1.RandomNegativeCount, run2.RandomNegativeCount);
        Assert.Equal(run1.DiscoveryCount, run2.DiscoveryCount);
        Assert.Equal(run1.Examples.Count, run2.Examples.Count);

        // Deterministic contract: for every example emitted, label + feature vector must
        // match position-by-position. Random.Shared would flip the picked negatives (and
        // therefore the CollaborativeScore / GenreSimilarity / GenreCount / PopularityScore
        // of the emitted negative-label examples) on virtually every re-run.
        for (var i = 0; i < run1.Examples.Count; i++)
        {
            var e1 = run1.Examples[i];
            var e2 = run2.Examples[i];
            Assert.Equal(e1.Label, e2.Label, 9);
            Assert.Equal(e1.SampleWeight, e2.SampleWeight, 9);
            Assert.Equal(e1.Features.GenreCount, e2.Features.GenreCount);
            Assert.Equal(e1.Features.IsSeries, e2.Features.IsSeries);
            Assert.Equal(e1.Features.CollaborativeScore, e2.Features.CollaborativeScore, 9);
            Assert.Equal(e1.Features.GenreSimilarity, e2.Features.GenreSimilarity, 9);
        }
    }
}