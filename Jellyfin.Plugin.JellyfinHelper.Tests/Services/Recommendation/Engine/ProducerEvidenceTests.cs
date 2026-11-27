using System.Collections.Immutable;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Provider/kind/ID provenance, missing/empty freshness, legacy isolation and shared producer consumers.</summary>
public sealed class ProducerEvidenceTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("tmdb", "production-company", 10, false)]
    [InlineData("another-provider", "production-company", 1, false)]
    [InlineData("tmdb", "network", 1, false)]
    [InlineData("TMDB", "production-company", 1, true)]
    public void SameLabelIsNotIdentityButQualifiedSameIdSurvivesTranslation(string provider, string kind, int id, bool matches)
    {
        var watched = Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Original name"));
        var candidate = Snapshot(new(provider, kind, id, "Translated name"));
        var preferred = ProducerEvidence.Preferences(Profile(watched), atUtc: Now);

        Assert.Equal(matches, (ProducerEvidence.Keys(candidate, Now) ?? []).Any(preferred.Contains));
        Assert.Equal(matches ? "Translated name" : null, ProducerEvidence.MatchedName(candidate, preferred, Now));
    }

    [Fact]
    public void BroadcastAndProductionFieldsAreCapturedSeparatelyAndLegacyStudioAcronymsRemainUnqualified()
    {
        var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>("""
            {"id":123,"productionCompanies":[{"id":1,"name":"A producer"}],"networks":[{"id":1,"name":"Same number, broadcaster"}]}
            """)!;
        var captured = TitleMetadataSnapshot.FromDetail(detail, "tv", Now);

        Assert.Equal(ProducerEvidence.CompanyKind, Assert.Single(captured.ProductionCompanies!.Value).Kind);
        Assert.Equal(ProducerEvidence.NetworkKind, Assert.Single(captured.Networks!.Value).Kind);
        Assert.Equal("producer:tmdb:production-company:1", Assert.Single(ProducerEvidence.Keys(captured, Now)!));
        var profile = Profile(null);
        profile.WatchedItems[0].Studios = ["A producer", "producer:tmdb:production-company:1"];
        profile.WatchedItems[0].OwnStudios = profile.WatchedItems[0].Studios;
        profile.WatchedItems[0].SeriesStudios = profile.WatchedItems[0].Studios;
        Assert.Empty(ProducerEvidence.Preferences(profile, atUtc: Now));
        Assert.Equal(2, profile.WatchedItems[0].Studios.Count);
    }

    [Theory]
    [InlineData("{}", false, false)]
    [InlineData("{\"productionCompanies\":null}", false, false)]
    [InlineData("{\"productionCompanies\":[]}", true, true)]
    [InlineData("{\"productionCompanies\":[{\"id\":0,\"name\":\"unknown\"}]}", false, false)]
    [InlineData("{\"productionCompanies\":[{\"id\":9,\"name\":\"known\"}]}", true, false)]
    public void MissingMalformedAndVerifiedEmptyProducerMetadataAreNotInterchangeable(string json, bool available, bool empty)
    {
        var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>(json)!;
        var snapshot = TitleMetadataSnapshot.FromDetail(detail, "tv", Now);
        var keys = ProducerEvidence.Keys(snapshot, Now);

        Assert.Equal(available, keys is not null);
        if (available)
        {
            Assert.Equal(empty, keys!.Count == 0);
        }
    }

    [Fact]
    public void CurrentUnknownOrEmptyCaptureWithdrawsOldProducerSnapshotWithoutMutatingTheWatchRow()
    {
        var snapshot = Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Producer"));
        var profile = Profile(snapshot);
        var id = profile.WatchedItems[0].ItemId;
        var unknown = new Dictionary<Guid, TitleMetadataSnapshot?> { [id] = null };
        var empty = new Dictionary<Guid, TitleMetadataSnapshot?> { [id] = snapshot with { ProductionCompanies = [] } };

        Assert.Empty(ProducerEvidence.Preferences(profile, unknown, Now));
        Assert.Empty(ProducerEvidence.Preferences(profile, empty, Now));
        Assert.Same(snapshot, profile.WatchedItems[0].TitleMetadata);
        Assert.Single(ProducerEvidence.Preferences(profile, atUtc: Now));
    }

    [Fact]
    public void ACurrentCaptureForAnotherPhysicalTitleOrNamespaceCannotSupplyProducerPreference()
    {
        var snapshot = Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Producer"));
        var profile = Profile(snapshot);
        var id = profile.WatchedItems[0].ItemId;

        Assert.Empty(ProducerEvidence.Preferences(profile, new Dictionary<Guid, TitleMetadataSnapshot?> { [id] = snapshot with { TmdbId = 999 } }, Now));
        Assert.Empty(ProducerEvidence.Preferences(profile, new Dictionary<Guid, TitleMetadataSnapshot?> { [id] = snapshot with { MediaType = "movie" } }, Now));
        Assert.Empty(ProducerEvidence.Preferences(profile, new Dictionary<Guid, TitleMetadataSnapshot?>(), Now, completeCurrentScope: true));
        Assert.Single(ProducerEvidence.Preferences(profile, new Dictionary<Guid, TitleMetadataSnapshot?>(), Now)); // Historical incomplete lookups retain qualified snapshots, never legacy labels.
    }

    [Fact]
    public void FieldAcquisitionTimeControlsProducerExpiryAndHistoricalAvailability()
    {
        var snapshot = Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Producer")) with
        {
            FieldFetchedAtUtc = ImmutableDictionary<string, DateTime>.Empty.Add(nameof(TitleMetadataSnapshot.ProductionCompanies), Now.AddDays(-29))
        };

        Assert.Single(ProducerEvidence.Keys(snapshot, Now)!);
        Assert.Null(ProducerEvidence.Keys(snapshot, Now.AddDays(2)));
        Assert.Null(ProducerEvidence.Keys(snapshot with { FieldFetchedAtUtc = null }, Now.AddMinutes(-1)));
    }

    [Fact]
    public void PartialRefreshDoesNotRenewRetainedProducerAgeAndExplicitEmptyClearsIt()
    {
        var clock = new Clock { CurrentUtc = Now };
        var config = Configuration();
        var cache = new TitleMetadataCache(clock: clock);
        var original = Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Producer")) with { FetchedAtUtc = Now.AddDays(-29) };
        cache.Store(config, original);
        cache.Store(config, original with { ProductionCompanies = null, Networks = [new("tmdb", ProducerEvidence.NetworkKind, 5, "Network")], FetchedAtUtc = Now });
        var retained = cache.Get(config, 123, "tv")!;
        Assert.Equal(Now.AddDays(-29), retained.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.ProductionCompanies)]);
        Assert.Single(ProducerEvidence.Keys(retained, Now)!);
        clock.CurrentUtc = Now.AddDays(2);
        Assert.Null(cache.Get(config, 123, "tv")!.ProductionCompanies);
        cache.Store(config, original with { ProductionCompanies = [], FetchedAtUtc = clock.CurrentUtc });
        Assert.Empty(ProducerEvidence.Keys(cache.Get(config, 123, "tv"), clock.CurrentUtc)!);
    }

    [Fact]
    public void DisablingOrChangingAConfiguredSourceWithdrawsItsProducerEvidence()
    {
        var clock = new Clock { CurrentUtc = Now };
        var config = Configuration();
        config.SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "synthetic" }];
        var cache = new TitleMetadataCache(clock: clock);
        cache.StoreSupplemental(config, Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Producer")), new MetadataSourceScope("sonarr", "https://sonarr.example.test", "synthetic"), null);

        Assert.Single(ProducerEvidence.Keys(cache.Get(config, 123, "tv"), Now)!);
        var scope = cache.ProducerScope(config);
        config.SonarrInstances = [];
        Assert.Null(cache.Get(config, 123, "tv"));
        Assert.NotEqual(scope, cache.ProducerScope(config));
    }

    [Fact]
    public void ACacheRoundTripPreservesEntityKindsAndObservedEmptyWithoutTouchingLegacyLabels()
    {
        var snapshot = Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Producer")) with { Networks = [new("tmdb", ProducerEvidence.NetworkKind, 1, "Producer")] };
        var copied = Assert.IsType<TitleMetadataSnapshot>(JsonSerializer.Deserialize<TitleMetadataSnapshot>(JsonSerializer.Serialize(snapshot)));
        var originalCompanies = Assert.IsType<ImmutableArray<ProviderStudioReference>>(snapshot.ProductionCompanies);
        var copiedCompanies = Assert.IsType<ImmutableArray<ProviderStudioReference>>(copied.ProductionCompanies);
        var originalNetworks = Assert.IsType<ImmutableArray<ProviderStudioReference>>(snapshot.Networks);
        var copiedNetworks = Assert.IsType<ImmutableArray<ProviderStudioReference>>(copied.Networks);
        Assert.Equal<ProviderStudioReference>(originalCompanies, copiedCompanies);
        Assert.Equal<ProviderStudioReference>(originalNetworks, copiedNetworks);
    }

    [Fact]
    public void LibraryExternalAndHistoricalPreferencePathsUseTheSameQualifiedProducerKeys()
    {
        var snapshot = Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Producer"));
        var profile = Profile(snapshot);
        var metadata = new Dictionary<Guid, TitleMetadataSnapshot?> { [profile.WatchedItems[0].ItemId] = snapshot };
        var current = PreferenceBuilder.BuildStudioPreferenceSet(profile, new Dictionary<Guid, BaseItem>(), metadata);
        var historical = TrainingFeatureComputer.BuildStudioPreferenceSetFromCache(profile, new Dictionary<Guid, IReadOnlyList<string>> { [profile.WatchedItems[0].ItemId] = ["legacy label"] }, metadata, Now);

        Assert.Equal(current, historical);
        Assert.Equal(ProducerEvidence.Preferences(profile, atUtc: Now), historical);
    }

    [Fact]
    public void ExternalProducerMatchUsesIdsAndLeavesGenreAndPeopleFeaturesUnchanged()
    {
        var profile = Profile(Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Original producer")));
        var genres = new Dictionary<string, double> { ["Drama"] = 1 };
        var exposure = PreferenceBuilder.BuildGenreExposureAnalysis(genres, profile);
        var candidate = new TmdbDiscoverItem { Id = 999, MediaType = "tv", GenreIds = [18], KnownPeople = ["Actor"], KnownStudios = ["unqualified"], TitleMetadata = Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Translated producer")) with { TmdbId = 999 } };
        var matched = ExternalCandidateFeatureBuilder.Build(candidate, genres, new HashSet<string> { "Actor" }, 2020, exposure, profile);
        candidate.TitleMetadata = candidate.TitleMetadata with { ProductionCompanies = [new("tmdb", ProducerEvidence.CompanyKind, 2, "Original producer")] };
        var unmatched = ExternalCandidateFeatureBuilder.Build(candidate, genres, new HashSet<string> { "Actor" }, 2020, exposure, profile);

        Assert.True(matched.StudioMatch);
        Assert.False(unmatched.StudioMatch);
        Assert.Equal(matched.GenreSimilarity, unmatched.GenreSimilarity);
        Assert.Equal(matched.PeopleSimilarity, unmatched.PeopleSimilarity);
        Assert.Equal(41, matched.ToVector().Length);
    }

    [Fact]
    public void ArchivedPredictionFeaturesRemainByteIdenticalEvenWhenProducerMetadataChanges()
    {
        var original = new CandidateFeatures { StudioMatch = true, ContentNearestNeighborScore = 0.8, GenreStudioIdfPrior = 0.7 };
        var bytes = JsonSerializer.Serialize(original);
        var profile = Profile(Snapshot(new("tmdb", ProducerEvidence.CompanyKind, 1, "Producer")));
        ProducerEvidence.Preferences(profile, new Dictionary<Guid, TitleMetadataSnapshot?> { [profile.WatchedItems[0].ItemId] = null }, Now);

        Assert.Equal(bytes, JsonSerializer.Serialize(original));
        Assert.Equal(41, original.ToVector().Length);
    }

    private static TitleMetadataSnapshot Snapshot(ProviderStudioReference company)
        => new() { TmdbId = 123, MediaType = "tv", ProductionCompanies = [company], FetchedAtUtc = Now, Source = "seerr" };

    private static UserWatchProfile Profile(TitleMetadataSnapshot? metadata)
        => new() { UserId = Guid.NewGuid(), WatchedItems = [new WatchedItemInfo { ItemId = Guid.NewGuid(), TmdbId = 123, ItemType = "Series", Played = true, Genres = ["Drama"], TitleMetadata = metadata }] };

    private static PluginConfiguration Configuration()
        => new() { SeerrUrl = "https://seerr.example.test", SeerrApiKey = "synthetic" };

    private sealed class Clock : TimeProvider
    {
        public DateTime CurrentUtc { get; set; }

        public override DateTimeOffset GetUtcNow() => new(CurrentUtc);
    }
}
