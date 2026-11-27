using System.Collections.ObjectModel;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Production identity propagates through reasons, diversity, native serving and historical reconstruction.</summary>
public sealed class ProducerConsumerParityTests
{
    [Fact]
    public void CachedNativeSnapshotCannotKeepProducerAffinityAfterConfiguredSourceWithdrawal()
    {
        var cache = new TitleMetadataCache();
        var harness = Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures.EngineTestFactory.Create(titleMetadataCache: cache);
        using var engine = harness.Engine;
        var config = harness.ConfigService.Object.GetConfiguration();
        config.SeerrUrl = "https://seerr.example.test";
        config.SeerrApiKey = "synthetic";
        var watched = new Movie { Id = Guid.NewGuid(), Name = "Watched", Path = "/private/watched.mkv", Genres = ["Drama"], ProviderIds = new() { ["Tmdb"] = "123" } };
        var candidate = new Movie { Id = Guid.NewGuid(), Name = "Unseen", Path = "/private/unseen.mkv", Genres = ["Drama"], ProviderIds = new() { ["Tmdb"] = "456" } };
        cache.Store(config, Metadata(1, "Producer") with { TmdbId = 123, MediaType = "movie" });
        cache.Store(config, Metadata(1, "Translated producer") with { TmdbId = 456, MediaType = "movie" });
        var oldWatchSnapshot = cache.Get(config, 123, "movie");
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [new WatchedItemInfo { ItemId = watched.Id, ItemType = "Movie", TmdbId = 123, Played = true, Genres = ["Drama"], TitleMetadata = oldWatchSnapshot }] };
        harness.WatchHistory.Setup(service => service.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(service => service.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? new List<BaseItem> { watched, candidate } : []);
        var before = Assert.Single(engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!.Recommendations);
        Assert.True(before.ScoringFeatures!.StudioMatch);

        config.SeerrUrl = string.Empty;
        config.SeerrApiKey = string.Empty;
        var after = Assert.Single(engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!.Recommendations);
        Assert.False(after.ScoringFeatures!.StudioMatch);
        Assert.True(before.ScoringFeatures.StudioMatch); // Original prediction evidence stays frozen.
        Assert.Same(oldWatchSnapshot, profile.WatchedItems[0].TitleMetadata);
    }

    [Fact]
    public void QualifiedIdControlsDiversityAndSameLegacyLabelDoesNotAddStudioSimilarity()
    {
        var a = new Movie { Id = Guid.NewGuid(), Genres = ["Drama"], ProductionYear = 2020, Studios = ["Same label"] };
        var b = new Movie { Id = Guid.NewGuid(), Genres = ["Comedy"], ProductionYear = 2020, Studios = ["Same label"] };
        var first = Metadata(1, "Original producer");
        var second = Metadata(1, "Translated producer");
        var unknown = Similarity(a, b, _ => null);
        var matched = Similarity(a, b, item => item.Id == a.Id ? first : second);
        var unmatched = Similarity(a, b, item => item.Id == a.Id ? first : Metadata(2, "Original producer"));

        Assert.Equal(0.2, unmatched, 12);
        Assert.True(unknown > unmatched); // Missing producer dimension is not fabricated negative evidence.
        Assert.True(matched > unknown);
        Assert.Equal("Same label", a.Studios[0]);
        Assert.Equal("Same label", b.Studios[0]);
    }

    [Fact]
    public void ProducerReasonUsesTheMatchingIdLabelNotTheNativeNetworkName()
    {
        var metadata = Metadata(1, "Localized producer");
        var candidate = new Movie { Id = Guid.NewGuid(), Studios = ["Wrong broadcaster"] };
        var preferred = ProducerEvidence.Keys(metadata)!.ToHashSet(StringComparer.Ordinal);
        var explanation = new ScoreExplanation { DominantSignal = "Studio", StudioContribution = 0.5 };

        var reason = ReasonResolver.DetermineReason(candidate, explanation, [], default(ReasonAnchorContext), new ReasonResolver.ReasonPeopleContext(PreferredStudios: preferred, ProducerMetadata: metadata));
        Assert.Equal("Localized producer", reason.RelatedItem);
        Assert.DoesNotContain("Wrong broadcaster", reason.Reason, StringComparison.Ordinal);
        var unknown = ReasonResolver.DetermineReason(candidate, explanation, [], default(ReasonAnchorContext), new ReasonResolver.ReasonPeopleContext(PreferredStudios: preferred));
        Assert.NotEqual("Wrong broadcaster", unknown.RelatedItem);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void NativeInferenceAndLegacyHistoricalReconstructionShareProducerInputs(int candidateCompany, bool matches)
    {
        var at = DateTime.UtcNow;
        var watched = new Movie { Id = Guid.NewGuid(), Name = "Watched", Path = "/private/watched.mkv", Genres = ["Drama"], Studios = ["Same broadcaster"], ProviderIds = new() { ["Tmdb"] = "123" }, CommunityRating = 7 };
        var candidate = new Movie { Id = Guid.NewGuid(), Name = "Unseen", Path = "/private/unseen.mkv", Genres = ["Drama"], Studios = ["Same broadcaster"], ProviderIds = new() { ["Tmdb"] = "456" }, CommunityRating = 7 };
        var config = new PluginConfiguration { SeerrUrl = "https://seerr.example.test", SeerrApiKey = "synthetic" };
        var cache = new TitleMetadataCache();
        cache.Store(config, Metadata(1, "Original producer") with { TmdbId = 123, MediaType = "movie", FetchedAtUtc = at.AddDays(-5) });
        cache.Store(config, Metadata(candidateCompany, "Localized producer") with { TmdbId = 456, MediaType = "movie", FetchedAtUtc = at.AddDays(-5) });
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), UserName = "Private user", WatchedItems = [new WatchedItemInfo { ItemId = watched.Id, ItemType = "Movie", TmdbId = 123, Played = true, Genres = ["Drama"], Studios = ["Same broadcaster"], LastPlayedDate = at.AddDays(-2), RuntimeTicks = 100, TitleMetadata = cache.Get(config, 123, "movie") }] };
        var history = new Mock<IWatchHistoryService>();
        history.Setup(service => service.GetUserWatchProfile(profile.UserId)).Returns(profile);
        history.Setup(service => service.GetAllUserWatchProfiles()).Returns([profile]);
        var library = new Mock<ILibraryManager>();
        library.Setup(manager => manager.GetVirtualFolders()).Returns([]);
        library.Setup(manager => manager.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => query.IncludeItemTypes.Contains(BaseItemKind.Movie) ? new List<BaseItem> { watched, candidate } : []);
        library.Setup(manager => manager.GetPeople(It.IsAny<BaseItem>())).Returns([]);
        var configuration = new Mock<IPluginConfigurationService>();
        configuration.Setup(service => service.GetConfiguration()).Returns(config);
        var heuristic = new HeuristicScoringStrategy();
        var registry = new Mock<IPerUserEnsembleRegistry>();
        registry.Setup(service => service.GetScoringStrategyForUser(profile.UserId)).Returns(heuristic);
        using var engine = new Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine(history.Object, library.Object, configuration.Object, new Mock<IPluginLogService>().Object, NullLogger<Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine>.Instance, heuristic, new Mock<IStrategySelector>().Object, registry.Object, new Mock<Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery.IDiscoveryFeedbackStore>().Object, titleMetadataCache: cache);
        var serving = Assert.Single(engine.GetRecommendations(profile.UserId, 1, TestContext.Current.CancellationToken)!.Recommendations);
        Assert.Equal(matches, serving.ScoringFeatures!.StudioMatch);
        var liveMetadata = engine.BuildTrainingLibraryMetadata([watched, candidate], []);
        var reconstructed = new RecommendedItem { ItemId = candidate.Id, ItemType = "Movie", TmdbId = 456, Name = candidate.Name, Genres = serving.Genres, Studios = ["Same broadcaster"], CommunityRating = 7, TitleMetadata = serving.TitleMetadata };
        var prior = new RecommendationResult { UserId = profile.UserId, UserName = profile.UserName, GeneratedAt = at.AddDays(-1), Recommendations = [reconstructed] };
        var examples = TrainingDataBuilder.BuildExamples([prior], new Collection<UserWatchProfile> { profile }, null, null, engine.BuildGenreStudioIdfTable([watched, candidate]), liveMetadata, null, TestContext.Current.CancellationToken).Examples;
        var captured = Assert.Single(examples, example => example.GeneratedAtUtc == prior.GeneratedAt);
        Assert.Equal(matches, captured.Features.StudioMatch);
        Assert.Equal(serving.ScoringFeatures.ContentNearestNeighborScore, captured.Features.ContentNearestNeighborScore);
        Assert.Equal(serving.ScoringFeatures.GenreStudioIdfPrior, captured.Features.GenreStudioIdfPrior);
        Assert.Equal(41, captured.Features.ToVector().Length);
        Assert.Equal("Same broadcaster", reconstructed.Studios[0]);
    }

    private static TitleMetadataSnapshot Metadata(int id, string name)
        => new() { TmdbId = 123, MediaType = "movie", ProductionCompanies = [new("tmdb", ProducerEvidence.CompanyKind, id, name)], FetchedAtUtc = DateTime.UtcNow.AddDays(-1) };

    private static double Similarity(BaseItem a, BaseItem b, Func<BaseItem, TitleMetadataSnapshot?> metadata)
    {
        var type = typeof(DiversityReranker).GetNestedType("SimilarityCache", BindingFlags.NonPublic)!;
        var instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [metadata], null)!;
        return (double)type.GetMethod("Compute")!.Invoke(instance, [a, b])!;
    }
}
