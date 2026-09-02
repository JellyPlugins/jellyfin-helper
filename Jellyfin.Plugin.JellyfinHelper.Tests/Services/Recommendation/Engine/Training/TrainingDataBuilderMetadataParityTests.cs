using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>
///     Tests the train/serve feature-parity fix: <see cref="TrainingDataBuilder.BuildExamples"/> resolves
///     watched-item studios/tags from a live-library metadata map merged over the previous-recommendations
///     cache (library-first), identical to the serve-time candidateLookup used by
///     <see cref="PreferenceBuilder.BuildStudioPreferenceSet"/> / <see cref="PreferenceBuilder.BuildTagPreferenceSet"/>.
/// </summary>
public sealed class TrainingDataBuilderMetadataParityTests
{
    private static readonly DateTime Anchor = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     A watched item absent from previous recommendations but present in the library metadata map must
    ///     contribute its library studios/tags to the preference sets, so a recommendation sharing that
    ///     studio/tag scores a positive StudioMatch / TagSimilarity - matching serve behaviour, which reads
    ///     the same metadata from the candidateLookup. Without the map (cache-only) the item contributes
    ///     nothing, so the match is absent.
    /// </summary>
    [Fact]
    public void BuildExamples_WatchedItemOnlyInLibraryMap_ContributesStudiosAndTagsToPreferences()
    {
        var user = Guid.NewGuid();
        var watchedItemId = Guid.NewGuid();
        var recItemId = Guid.NewGuid();

        // The watched item is organic (never recommended) so the previous-recommendations cache carries no
        // studios/tags for it. Its studios/tags exist ONLY in the library metadata map.
        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                WatchedItems = [new WatchedItemInfo { ItemId = watchedItemId, ItemType = "Movie", Played = true, LastPlayedDate = Anchor }]
            }
        };

        // The single recommendation shares the watched item's library studio and tag.
        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem
                    {
                        ItemId = recItemId,
                        ItemType = "Movie",
                        Genres = ["Action"],
                        Studios = ["A24"],
                        Tags = ["heist"]
                    }
                }
            }
        };

        var libraryMetadata = new LibraryItemMetadata(
            new Dictionary<Guid, IReadOnlyList<string>> { [watchedItemId] = ["A24"] },
            new Dictionary<Guid, IReadOnlyList<string>> { [watchedItemId] = ["heist"] },
            new Dictionary<Guid, IReadOnlyList<Guid>>());

        var withLibrary = BuildRecExample(results, profiles, libraryMetadata);
        Assert.True(withLibrary.Features.StudioMatch);
        Assert.True(withLibrary.Features.TagSimilarity > 0.0);

        // Cache-only (null map): the organic watched item never entered the cache, so no preference is built.
        var (profiles2, results2) = (CloneProfiles(profiles, user, watchedItemId), CloneResults(user, recItemId));
        var cacheOnly = BuildRecExample(results2, profiles2, libraryItemMetadata: null);
        Assert.False(cacheOnly.Features.StudioMatch);
        Assert.Equal(0.0, cacheOnly.Features.TagSimilarity, 9);
    }

    /// <summary>
    ///     The merged training studio-preference set for a library-only watched item equals what the serve
    ///     path produces from an equivalent candidateLookup, proving byte-level parity of the metadata source.
    /// </summary>
    [Fact]
    public void MergedTrainingPreferenceSet_MatchesServePreferenceSet()
    {
        var user = Guid.NewGuid();
        var watchedItemId = Guid.NewGuid();

        var profile = new UserWatchProfile
        {
            UserId = user,
            UserName = "u",
            WatchedItems = [new WatchedItemInfo { ItemId = watchedItemId, ItemType = "Movie", Played = true, LastPlayedDate = Anchor }]
        };

        // Serve path: candidateLookup holds the real library item.
        var candidateLookup = new Dictionary<Guid, BaseItem>
        {
            { watchedItemId, new Movie { Id = watchedItemId, Studios = ["A24", "Neon"], Tags = ["heist", "noir"] } }
        };
        var serveStudios = PreferenceBuilder.BuildStudioPreferenceSet(profile, candidateLookup);
        var serveTags = PreferenceBuilder.BuildTagPreferenceSet(profile, candidateLookup);

        // Train path: an empty cache merged with the library map (library wins) mirrors production's merge.
        var studioLookup = new Dictionary<Guid, IReadOnlyList<string>> { [watchedItemId] = ["A24", "Neon"] };
        var tagLookup = new Dictionary<Guid, IReadOnlyList<string>> { [watchedItemId] = ["heist", "noir"] };
        var trainStudios = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, studioLookup);
        var trainTags = TrainingFeatureComputer.BuildTagPreferenceSetFromCache(profile, tagLookup);

        Assert.True(serveStudios.SetEquals(trainStudios));
        Assert.True(serveTags.SetEquals(trainTags));
    }

    /// <summary>
    ///     When both the cache (from a previous recommendation) and the library map hold studios for the
    ///     same item id, the library value wins.
    /// </summary>
    [Fact]
    public void BuildExamples_LibraryValueOverridesCacheForSameItem()
    {
        var user = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var recItemId = Guid.NewGuid();

        // The user watched itemId AND it was previously recommended, so the cache carries a stale studio.
        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                WatchedItems = [new WatchedItemInfo { ItemId = itemId, ItemType = "Movie", Played = true, LastPlayedDate = Anchor }]
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    // Cache entry for itemId: stale studio "OldStudio".
                    new RecommendedItem { ItemId = itemId, ItemType = "Movie", Genres = ["Action"], Studios = ["OldStudio"] },
                    // Probe recommendation (distinct GenreCount==2) matches the LIBRARY studio, not the cached one.
                    new RecommendedItem { ItemId = recItemId, ItemType = "Movie", Genres = ["Comedy", "Indie"], Studios = ["NewStudio"] }
                }
            }
        };

        var libraryMetadata = new LibraryItemMetadata(
            new Dictionary<Guid, IReadOnlyList<string>> { [itemId] = ["NewStudio"] },
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>());

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(
            results, profiles, discoveryFeedback: null, seriesEpisodeCounts: null, genreStudioIdf: null, libraryMetadata, featureMeans: null, CancellationToken.None);

        // The probe rec (2 genres) matches only if the library studio "NewStudio" won over cache "OldStudio".
        var probe = examples.Single(e => e.Features.GenreCount == 2);
        Assert.True(probe.Features.StudioMatch);
    }

    /// <summary>
    ///     Series self-exclusion is unchanged by the metadata-source fix: a recommended series whose only
    ///     watched records are its own episodes still draws a 0.65 favorite-style label (it does not fabricate
    ///     engagement from its own history). Guards that the leak-exclusion axis is untouched.
    /// </summary>
    [Fact]
    public void BuildExamples_SeriesSelfExclusion_HoldsWithLibraryMap()
    {
        var user = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var profiles = new Collection<UserWatchProfile>
        {
            new()
            {
                UserId = user,
                UserName = "u",
                FavoriteSeriesIds = { seriesId }
            }
        };

        var results = new List<RecommendationResult>
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = seriesId, ItemType = "Series", Genres = ["Drama"] }
                }
            }
        };

        var libraryMetadata = new LibraryItemMetadata(
            new Dictionary<Guid, IReadOnlyList<string>> { [seriesId] = ["HBO"] },
            new Dictionary<Guid, IReadOnlyList<string>>(),
            new Dictionary<Guid, IReadOnlyList<Guid>>());

        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(
            results, profiles, discoveryFeedback: null, seriesEpisodeCounts: null, genreStudioIdf: null, libraryMetadata, featureMeans: null, CancellationToken.None);

        var seriesExample = Assert.Single(examples);
        Assert.Equal(0.65, seriesExample.Label, 9);
    }

    private static TrainingExample BuildRecExample(
        List<RecommendationResult> results,
        Collection<UserWatchProfile> profiles,
        LibraryItemMetadata? libraryItemMetadata)
    {
        var (examples, _, _, _) = TrainingDataBuilder.BuildExamples(
            results, profiles, discoveryFeedback: null, seriesEpisodeCounts: null, genreStudioIdf: null, libraryItemMetadata, featureMeans: null, CancellationToken.None);

        // The Phase 1 recommendation-feedback example is the one whose genre is Action (the probe rec).
        return examples.Single(e => e.Features.GenreCount == 1 && !e.Features.IsSeries && e.SampleWeight == 1.0);
    }

    private static Collection<UserWatchProfile> CloneProfiles(Collection<UserWatchProfile> _, Guid user, Guid watchedItemId)
        => new()
        {
            new()
            {
                UserId = user,
                UserName = "u",
                WatchedItems = [new WatchedItemInfo { ItemId = watchedItemId, ItemType = "Movie", Played = true, LastPlayedDate = Anchor }]
            }
        };

    private static List<RecommendationResult> CloneResults(Guid user, Guid recItemId)
        => new()
        {
            new()
            {
                UserId = user,
                GeneratedAt = Anchor,
                Recommendations =
                {
                    new RecommendedItem { ItemId = recItemId, ItemType = "Movie", Genres = ["Action"], Studios = ["A24"], Tags = ["heist"] }
                }
            }
        };
}
