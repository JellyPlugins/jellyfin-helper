using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

public sealed class CreditRootBeforeAfterTests
{
    [Fact]
    public void BillingCannotExceedMatchedCandidateCoverage()
    {
        var score = SimilarityComputer.ComputeBillingWeightedPeople(new Dictionary<string, double> { ["A"] = .04, ["Other"] = 1 }, new Dictionary<string, double> { ["A"] = 26 });
        Assert.Equal(.04 / 1.04, score, 12);
    }

    [Fact]
    public void NonfinitePreferenceDoesNotDestroyValidBilling()
    {
        var score = SimilarityComputer.ComputeBillingWeightedPeople(new Dictionary<string, double> { ["A"] = 1, ["Other"] = 1 }, new Dictionary<string, double> { ["A"] = 1, ["Invalid"] = double.PositiveInfinity });
        Assert.Equal(.5, score, 12);
        Assert.True(double.IsFinite(SimilarityComputer.ComputeAveragePreferredWeight(new Dictionary<string, double> { ["A"] = double.MaxValue, ["B"] = double.MaxValue })));
    }

    [Fact]
    public void UndatedCreditAcquisitionDoesNotInventHistoricalPreference()
    {
        var source = new UserWatchProfile { WatchedItems = [new() { ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-3), PeopleNames = ["A"], PeopleWeights = [1] }] };
        Assert.Empty(TrainingHistory.Before(source, DateTime.UtcNow).PeopleProfile);
    }

    [Fact]
    public void ParentCreditsAreCapturedOnceWithoutEpisodeQueries()
    {
        var parent = new Series { Id = Guid.NewGuid(), Genres = ["Drama"], ProductionLocations = ["Japan"] };
        var episodes = Enumerable.Range(0, 3).Select(_ => new Episode { Id = Guid.NewGuid(), SeriesId = parent.Id, Path = "/synthetic/episode.mkv" }).ToArray();
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        library.Setup(l => l.GetPeople(parent)).Returns([new PersonInfo { Name = "A", Type = PersonKind.Actor, SortOrder = 0 }, new PersonInfo { Name = "W", Type = PersonKind.Writer }]);
        var config = new Mock<IPluginConfigurationService>();
        config.Setup(c => c.GetConfiguration()).Returns(new PluginConfiguration());
        var data = new Mock<IUserDataManager>();
        data.Setup(d => d.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((User _, BaseItem item) => new UserItemData { Key = item.Id.ToString("N"), Played = item is Episode, LastPlayedDate = item is Episode ? DateTime.UtcNow.AddDays(-1) : null });
        using var service = new WatchHistoryService(library.Object, config.Object, new Mock<IUserManager>().Object, data.Object, new Mock<IPluginLogService>().Object, NullLogger<WatchHistoryService>.Instance);
        var profile = service.BuildProfile(new User("synthetic", "synthetic", "synthetic") { Id = Guid.NewGuid() }, episodes, [parent]);
        Assert.All(profile.WatchedItems, row => { Assert.Contains("A", row.PeopleNames); Assert.Equal(row.PeopleNames.Count, row.PeopleWeights.Count); Assert.Contains("W", row.WriterNames); Assert.Contains("Japan", row.ProductionCountries); });
        library.Verify(l => l.GetPeople(parent), Times.Once);
        library.Verify(l => l.GetPeople(It.Is<BaseItem>(item => item is Episode)), Times.Never);
    }

    [Fact]
    public void MissingCreditSubfieldsAreNotVerifiedEmpty()
    {
        var missing = JsonSerializer.Deserialize<SeerrCredits>("{}")!;
        Assert.Null(Extract(missing));
        Assert.Empty(Extract(JsonSerializer.Deserialize<SeerrCredits>("{\"cast\":[],\"crew\":[]}")!)!);
    }

    [Fact]
    public void NullMembersDoNotAbortValidCreditExtraction()
    {
        var credits = JsonSerializer.Deserialize<SeerrCredits>("{\"cast\":[null,{\"id\":1,\"name\":\"A\",\"order\":0}],\"crew\":[]}")!;
        Assert.Equal("A", Assert.Single(Extract(credits)!));
    }

    [Fact]
    public void ConfirmedHomonymsDoNotMatchByName()
    {
        using var harness = Serving("Alex", "Alex", "1", "2");
        var result = harness.Engine.GetRecommendations(harness.Profile.UserId, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.Equal(0, Assert.Single(result.Recommendations).ScoringFeatures!.PeopleSimilarity);
    }

    [Fact]
    public void TranslatedDisplayNamesMatchTheSamePersonIdentity()
    {
        using var harness = Serving("Alex", "Übersetzter Alex", "1", "1");
        Assert.True(Assert.Single(harness.Engine.GetRecommendations(harness.Profile.UserId, cancellationToken: TestContext.Current.CancellationToken)!.Recommendations).ScoringFeatures!.PeopleSimilarity > 0);
    }

    [Fact]
    public void CapturedLegacyVectorsRemainExactAndFinite()
    {
        var original = new CandidateFeatures { GenreSimilarity = .3, PeopleSimilarity = .6, BillingWeightedPeople = .8, WriterAffinity = .7, ContentFitAffinity = .1, ContentFitSupport = .2, AudienceMismatch = .3 };
        Assert.Equal(original.ToVector(), original.Copy().ToVector());
        Assert.Equal(original.ToVector(), original.ToModelVector(Enumerable.Repeat(.99, 41).ToArray()));
        Assert.Equal(41, original.ToVector().Length);
        Assert.All(original.ToVector(), value => Assert.True(double.IsFinite(value)));
    }

    private static List<string>? Extract(SeerrCredits credits)
    {
        var snapshot = CreditMetadataCapture.External(credits, DateTime.UtcNow);
        var people = snapshot.People().OrderBy(person => person.Kind == PersonKind.Director ? 0 : 1)
            .ThenByDescending(person => person.BillingWeight).Select(person => person.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return people.Count > 0 || snapshot.PeopleComplete ? people : null;
    }

    private static ServingHarness Serving(string watchedName, string candidateName, string watchedId, string candidateId)
    {
        var watched = new Movie { Id = Guid.NewGuid(), Path = "/synthetic/a.mkv", Genres = ["Drama"] };
        var candidate = new Movie { Id = Guid.NewGuid(), Path = "/synthetic/b.mkv", Genres = ["Drama"] };
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [new() { ItemId = watched.Id, ItemType = "Movie", Played = true, LastPlayedDate = DateTime.UtcNow.AddDays(-1), Genres = ["Drama"] }] };
        var harness = EngineTestFactory.Create();
        harness.WatchHistory.Setup(w => w.GetUserWatchProfile(profile.UserId)).Returns(profile);
        harness.WatchHistory.Setup(w => w.GetAllUserWatchProfiles()).Returns([profile]);
        harness.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery q) => q.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true ? [watched, candidate] : []);
        harness.LibraryManager.Setup(l => l.GetPeople(watched)).Returns([new() { Name = watchedName, Type = PersonKind.Actor, SortOrder = 0, ProviderIds = new() { ["Tmdb"] = watchedId } }]);
        harness.LibraryManager.Setup(l => l.GetPeople(candidate)).Returns([new() { Name = candidateName, Type = PersonKind.Actor, SortOrder = 0, ProviderIds = new() { ["Tmdb"] = candidateId } }]);
        return new(harness.Engine, profile);
    }

    private sealed record ServingHarness(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine Engine, UserWatchProfile Profile) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }
}
