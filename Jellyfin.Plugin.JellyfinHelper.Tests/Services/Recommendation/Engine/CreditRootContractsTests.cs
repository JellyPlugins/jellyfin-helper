using System.Collections.Immutable;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

public sealed class CreditRootContractsTests
{
    private static readonly DateTime Clock = new(2026, 11, 20, 20, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AScopedImdbBindingCanSupplyCreditsWithoutRewritingNativeProviderIdsOrAcceptingAConflict(bool conflict)
    {
        var item = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Imdb"] = "tt123" } };
        var native = CreditMetadataCapture.Native(item, null, Clock);
        var metadata = new TitleMetadataSnapshot
        {
            TmdbId = 77, ImdbId = conflict ? "tt456" : "tt123", Credits = Snapshot(Guid.Empty) with { TmdbId = 77, MediaType = "movie" }, FetchedAtUtc = Clock
        };
        var resolved = CreditMetadataCapture.WithAvailableTitleMetadata(item, native, metadata);
        Assert.Equal(item.Id, resolved.SourceItemId);
        Assert.Equal(conflict ? 0 : 77, resolved.TmdbId);
        if (conflict) Assert.Null(resolved.Cast);
        else Assert.NotEmpty(resolved.People());
        Assert.Equal(0, native.TmdbId);
        Assert.Null(native.Cast);
        Assert.False(item.ProviderIds.ContainsKey("Tmdb"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CachedProviderCreditsComplementOnlyUnavailableSameTitleNativeFields(bool knownEmpty, bool wrongTitle)
    {
        var item = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "77" } };
        var native = CreditMetadataCapture.Native(item, knownEmpty ? [] : null, Clock);
        var detail = new SeerrMediaDetailResponse
        {
            Id = wrongTitle ? 78 : 77,
            Credits = new SeerrCredits
            {
                Cast = [new SeerrCastMember { Id = 1, Name = "Actor", Order = 0 }],
                Crew = [new SeerrCrewMember { Id = 2, Name = "Writer", Job = "Writer" }]
            }
        };
        var metadata = TitleMetadataSnapshot.FromDetail(detail, "movie", Clock.AddHours(-1));
        var resolved = CreditMetadataCapture.WithAvailableTitleMetadata(native, metadata);
        Assert.Equal(item.Id, resolved.SourceItemId);
        Assert.Equal(77, resolved.TmdbId);
        if (wrongTitle)
        {
            Assert.Null(resolved.Cast);
        }
        else if (knownEmpty)
        {
            Assert.Empty(resolved.People());
        }
        else
        {
            Assert.Equal("Actor", Assert.Single(resolved.People()).Name);
            Assert.Equal("Writer", Assert.Single(resolved.People(writers: true)).Name);
            Assert.Equal(Clock.AddHours(-1), resolved.Cast!.AcquiredAtUtc);
            Assert.Equal(Clock.AddHours(-1), resolved.Crew!.AcquiredAtUtc);
        }

        Assert.Equal(knownEmpty ? Clock : (DateTime?)null, native.Cast?.AcquiredAtUtc);
        var roundtrip = JsonSerializer.Deserialize<TitleMetadataSnapshot>(JsonSerializer.Serialize(metadata))!;
        Assert.Equal(metadata.Credits!.Cast!.AcquiredAtUtc, roundtrip.Credits!.Cast!.AcquiredAtUtc);
        Assert.Equal(detail.Id, roundtrip.Credits.TmdbId);
    }

    [Fact]
    public void ProviderCountryFallbackKeepsTheFieldTimeAndDoesNotOverrideAnObservedNativeEmpty()
    {
        var item = new Movie { Id = Guid.NewGuid(), ProviderIds = new() { ["Tmdb"] = "77" }, ProductionLocations = null! };
        var provider = new TitleMetadataSnapshot { TmdbId = 77, ProductionCountries = ["JP"], FetchedAtUtc = Clock.AddDays(-1) };
        var unknown = CreditMetadataCapture.Native(item, null, Clock);
        var filled = CreditMetadataCapture.WithAvailableTitleMetadata(unknown, provider);
        Assert.Equal(new[] { "JP" }, filled.ProductionCountries!.Value);
        Assert.Equal(provider.FetchedAtUtc, filled.CountriesAcquiredAtUtc);
        Assert.Null(filled.At(provider.FetchedAtUtc.AddTicks(-1)).ProductionCountries);
        item.ProductionLocations = [];
        var empty = CreditMetadataCapture.WithAvailableTitleMetadata(CreditMetadataCapture.Native(item, null, Clock), provider);
        Assert.Empty(empty.ProductionCountries!.Value);
        Assert.Equal(Clock, empty.CountriesAcquiredAtUtc);
    }

    [Theory]
    [InlineData(false, false, .8)]
    [InlineData(true, false, .8)]
    [InlineData(true, true, .5)]
    public void LegacyNeighbourAndDirectPeopleUseOneResolvedPredictionTimeCreditSource(bool dtoCredits, bool future, double expectedNeighbour)
    {
        var user = Guid.NewGuid();
        var target = Guid.NewGuid();
        var prior = Row(Guid.NewGuid(), Clock.AddDays(-3));
        var targetCredits = Snapshot(target);
        if (future) targetCredits = targetCredits with { Cast = targetCredits.Cast! with { AcquiredAtUtc = Clock.AddDays(1) } };
        var rec = new RecommendedItem { ItemId = target, ItemType = "Movie", Genres = ["Drama"], DateCreated = Clock.AddDays(-5), Credits = dtoCredits ? targetCredits : null };
        var previous = new RecommendationResult { UserId = user, GeneratedAt = Clock, Recommendations = [rec] };
        var profile = new UserWatchProfile { UserId = user, WatchedItems = [prior] };
        var metadata = new LibraryItemMetadata(new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<string>>(), new Dictionary<Guid, IReadOnlyList<Guid>>())
        {
            Credits = new Dictionary<Guid, CreditMetadataSnapshot> { [prior.ItemId] = prior.Credits!, [target] = Snapshot(target) }
        };
        var example = TrainingDataBuilder.BuildExamples([previous], [profile], null, null, null, metadata, null, TestContext.Current.CancellationToken).Examples
            .Single(row => row.ObservationId == TrainingExample.CreateLibraryObservationId(user, target) && row.GeneratedAtUtc == Clock);
        Assert.Equal(expectedNeighbour, example.Features.ContentNearestNeighborScore, 12);
        Assert.Equal(!future, example.Features.HasPeopleMetadata);
        Assert.Equal(future ? 0 : .2, example.Features.PeopleSimilarity, 12);
        Assert.Equal(Clock, example.GeneratedAtUtc);
        Assert.Equal(dtoCredits ? targetCredits : null, rec.Credits);
        Assert.Null(rec.ScoringFeatures);
    }

    [Fact]
    public void OrganicCapturedCreditsDoNotDependOnRecommendationPublication()
    {
        var rows = Enumerable.Range(1, 3).Select(i => Row(Guid.NewGuid(), Clock.AddDays(i - 4))).ToArray();
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [.. rows] };
        var examples = TrainingDataBuilder.BuildExamples([], [profile], Clock, TestContext.Current.CancellationToken).Examples;
        var target = examples.Single(e => e.ObservationId == TrainingExample.CreateTitleObservationId(profile.UserId, rows[2].ItemId, 0, false));
        Assert.Equal(.4, target.Features.PeopleSimilarity, 12);
        Assert.Equal(.8, target.Features.ContentNearestNeighborScore, 12);
        Assert.Equal(1, target.Features.BillingWeightedPeople);
        Assert.True(target.Features.HasPeopleMetadata);
        Assert.Equal(41, target.Features.ToVector().Length);
    }

    [Fact]
    public void ParentSeriesAggregationUsesDatedParentCreditsNotEpisodeDefaults()
    {
        var prior = Row(Guid.NewGuid(), Clock.AddDays(-3));
        var history = TrainingHistory.Before(new UserWatchProfile { WatchedItems = [prior] }, Clock);
        var parent = Snapshot(Guid.NewGuid());
        var episodes = new List<WatchedItemInfo> { new() { ItemId = Guid.NewGuid(), ItemType = "Episode", SeriesId = parent.SourceItemId, SeriesCredits = parent, LastPlayedDate = Clock, Played = true, Genres = ["Drama"] } };
        var examples = new List<TrainingExample>();
        var genres = new Dictionary<string, double> { ["Drama"] = 1 };
        TrainingFeatureComputer.AddAggregatedSeriesExample(examples, episodes, parent.SourceItemId, history, genres, [], 0, 2000,
            PreferenceBuilder.BuildGenreExposureAnalysis(genres, history), [], new Dictionary<string, double>(), [], [], [], [],
            new Dictionary<string, double>(), new Dictionary<string, double>(), [], new Dictionary<string, double>(), null, Clock,
            seriesMetadata: new TrainingFeatureComputer.SeriesMetadata(Credits: CreditPreferenceProfile.Create(history), CandidateCredits: parent));
        Assert.True(examples.Single().Features.PeopleSimilarity > 0);
        Assert.True(examples.Single().Features.WriterAffinity > 0);
        Assert.Equal(1, examples.Single().Features.BillingWeightedPeople);
        Assert.Equal(.8, examples.Single().Features.ContentNearestNeighborScore, 12);
    }

    [Fact]
    public void KnownEmptyPartialAndUnavailableStayDistinct()
    {
        var preference = CreditPreferenceProfile.Create(new UserWatchProfile { WatchedItems = [Row(Guid.NewGuid(), Clock)] });
        var known = Snapshot(Guid.NewGuid()) with { Cast = new CreditField { People = [], Complete = true, AcquiredAtUtc = Clock }, Crew = new CreditField { People = [], Complete = true, AcquiredAtUtc = Clock } };
        var empty = new CandidateFeatures();
        preference.Apply(empty, known);
        Assert.True(empty.HasPeopleMetadata);
        Assert.Equal(0, empty.PeopleSimilarity);
        var unknown = new CandidateFeatures();
        preference.Apply(unknown, null);
        var means = Enumerable.Repeat(.37, 41).ToArray();
        Assert.False(unknown.HasPeopleMetadata);
        Assert.Equal(.37, unknown.ToModelVector(means)[13]);
        Assert.Equal(0, unknown.ToVector()[13]);
        var partial = Snapshot(Guid.NewGuid()) with { Crew = null };
        Assert.True(preference.PeopleAffinity(partial).Score > 0);
        Assert.False((partial with { Cast = null }).PeopleComplete);
        Assert.False(new CreditField { People = default(ImmutableArray<PersonCredit>), Complete = true }.IsComplete);
        Assert.False(new CreditField { People = [null!], Complete = true }.IsComplete);
    }

    [Fact]
    public void MetadataCutoffDoesNotRetimestampMissingFutureOrRetainedFields()
    {
        var old = Snapshot(Guid.NewGuid());
        var current = old with { Cast = null, Crew = old.Crew! with { AcquiredAtUtc = Clock.AddDays(1) } };
        var combined = current.WithFallback(old);
        Assert.Equal(old.Cast, combined.Cast);
        Assert.Null(combined.At(Clock).Crew);
        Assert.NotNull(combined.At(Clock).Cast);
        Assert.Null((old.Cast! with { AcquiredAtUtc = null }).At(Clock));
        Assert.Null((old.Cast with { AcquiredAtUtc = default(DateTime) }).At(Clock));
        Assert.Equal(old, (old with { Cast = old.Cast with { AcquiredAtUtc = Clock.AddDays(-20) } }).WithFallback(old));
        var roundtrip = JsonSerializer.Deserialize<CreditMetadataSnapshot>(JsonSerializer.Serialize(combined))!;
        Assert.Equal(combined.Cast!.AcquiredAtUtc, roundtrip.Cast!.AcquiredAtUtc);
    }

    [Fact]
    public void IdentityAliasesRejectHomonymsAndConflictingProviderLinks()
    {
        var a = Person("Alex", "1");
        var translated = Person("Übersetzt", "1");
        var other = Person("Alex", "2");
        var legacy = new PersonCredit { Name = "Alex", Kind = PersonKind.Actor };
        var index = new CreditIdentityIndex([a, translated, other, legacy]);
        Assert.Equal(index.Key(a), index.Key(translated));
        Assert.NotEqual(index.Key(a), index.Key(other));
        Assert.Null(index.Key(legacy));
        Assert.NotNull(new CreditIdentityIndex([a, legacy]).Key(legacy));
        var imdb = a with { Identities = [new("tmdb", "1"), new("imdb", "nm1")] };
        var conflict = a with { Identities = [new("tmdb", "1"), new("imdb", "nm2")] };
        Assert.Null(new CreditIdentityIndex([imdb, conflict]).Key(imdb));
        Assert.Null(new CreditIdentityIndex([a with { Identities = [new("tmdb", "1"), new("tmdb", "2")] }]).Key(a with { Identities = [new("tmdb", "1"), new("tmdb", "2")] }));
        Assert.Null(index.Key(new PersonCredit()));
        Assert.NotNull(new CreditIdentityIndex([legacy]).Key(legacy));
    }

    [Fact]
    public void CaptureRetainsRoleIdentityUnknownOrderAndNullMemberAvailability()
    {
        var dto = JsonSerializer.Deserialize<SeerrCredits>("{\"cast\":[null,{\"id\":1,\"name\":\"A\"}],\"crew\":[{\"id\":2,\"name\":\"W\",\"job\":\"Screenplay\"},{\"id\":3,\"name\":\"D\",\"job\":\"Director\"},{\"id\":4,\"name\":\"P\",\"job\":\"Producer\"}]}")!;
        var external = CreditMetadataCapture.External(dto, Clock, 1, "tv");
        Assert.False(external.PeopleComplete);
        Assert.Equal(2, external.People().Count());
        Assert.Equal("W", Assert.Single(external.People(writers: true)).Name);
        var item = new Movie { Id = Guid.NewGuid(), ProductionLocations = ["Japan"] };
        var native = CreditMetadataCapture.Native(item, [new() { Name = "A", Type = PersonKind.Actor, ProviderIds = new() { ["Tmdb"] = "1" } }, new() { Name = "D", Type = PersonKind.Director }, new() { Name = "W", Type = PersonKind.Writer }, null!, new() { Name = "", Type = PersonKind.Actor }, new() { Name = "Producer", Type = PersonKind.Producer }], Clock);
        Assert.False(native.PeopleComplete);
        Assert.Contains("Japan", native.ProductionCountries!.Value);
        Assert.False(CreditMetadataCapture.Native(item, null, Clock).PeopleComplete);
        Assert.Null(CreditMetadataCapture.Normalize("tmdb", "0"));
        Assert.Null(CreditMetadataCapture.Normalize("imdb", "tt1"));
        Assert.Equal("nm1", CreditMetadataCapture.Normalize("IMDb", "NM1")!.Value.Id);
        Assert.Equal("2", CreditMetadataCapture.Normalize("Tvdb", "2")!.Value.Id);
    }

    [Fact]
    public void ImputationUsesOnlyObservedFittingMembersAndNeverChangesRawVectors()
    {
        var known = new CandidateFeatures { PeopleSimilarity = .4, WriterAffinity = .2, BillingWeightedPeople = .8, HasPeopleMetadata = true, HasWriterMetadata = true, HasBillingMetadata = true };
        var unknown = new CandidateFeatures { HasPeopleMetadata = false, HasWriterMetadata = false, HasBillingMetadata = false, GenreSimilarity = .5 };
        var heldout = known.Copy(); heldout.PeopleSimilarity = 1;
        var rows = new List<TrainingExample> { new() { Features = known }, new() { Features = unknown }, new() { Features = heldout } };
        var means = CreditFeatureImputation.Fit(rows, [0, 1], null)!;
        Assert.Equal(.4, means[13]);
        var raw = unknown.ToVector();
        var vectors = rows.Select(row => row.Features.ToModelVector(null)).ToArray();
        CreditFeatureImputation.Apply(rows, vectors, means);
        Assert.Equal(.4, vectors[1][13]); Assert.Equal(.2, vectors[1][21]);
        Assert.Equal(raw, unknown.ToVector());
        Assert.Null(CreditFeatureImputation.Fit([new() { Features = new CandidateFeatures() }], [0], null));
        Assert.Equal(41, ModelInputFeatures.FitImputation(rows, [0, 1], means).Length);
        ModelInputFeatures.ApplyMissing(rows, vectors, means);
        Assert.Equal(41, vectors[0].Length);
    }

    private static PersonCredit Person(string name, string id) => new() { Name = name, Kind = PersonKind.Actor, Identities = [new("tmdb", id)], BillingWeight = 1 };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnGuestAndParentCreditsRetainTheirIndependentCompletenessAndAcquisition(bool datedParent)
    {
        var episode = Guid.NewGuid();
        var series = Guid.NewGuid();
        var own = Snapshot(episode) with { Cast = new() { People = [Person("Guest", "3")], Complete = true, AcquiredAtUtc = Clock.AddMinutes(-1) } };
        var parent = Snapshot(series) with { Cast = new() { People = [Person("Regular", "4")], Complete = true, AcquiredAtUtc = datedParent ? Clock.AddMinutes(-2) : null } };
        var row = new WatchedItemInfo { ItemId = episode, SeriesId = series, ItemType = "Episode", Played = true, Credits = own, SeriesCredits = parent };
        var combined = Assert.IsType<CreditMetadataSnapshot>(CreditPreferenceProfile.Resolve(row));
        Assert.Equal(new[] { "Guest", "Regular" }, combined.Cast!.People!.Value.Select(person => person.Name));
        Assert.True(combined.Cast.Complete);
        Assert.Equal(datedParent ? Clock.AddMinutes(-1) : (DateTime?)null, combined.Cast.AcquiredAtUtc);
        var ownPeople = Assert.IsType<ImmutableArray<PersonCredit>>(Assert.IsType<CreditField>(own.Cast).People);
        var parentPeople = Assert.IsType<ImmutableArray<PersonCredit>>(Assert.IsType<CreditField>(parent.Cast).People);
        Assert.Equal("Guest", Assert.Single(ownPeople).Name);
        Assert.Equal("Regular", Assert.Single(parentPeople).Name);

        row.SeriesCredits = parent with { Cast = null };
        Assert.Same(own.Cast, CreditPreferenceProfile.Resolve(row)!.Cast);
        var wrongOwner = Snapshot(Guid.NewGuid());
        var lookup = new Dictionary<Guid, CreditMetadataSnapshot> { [episode] = wrongOwner };
        Assert.Equal("Guest", CreditPreferenceProfile.Resolve(row, lookup)!.Cast!.People!.Value[0].Name);
    }

    [Fact]
    public void CapturedPeoplePreferencesDoNotRequireAPriorRecommendationLookupAndEmptyBillingIsNeutral()
    {
        var row = Row(Guid.NewGuid(), Clock.AddDays(-1));
        var profile = new UserWatchProfile { WatchedItems = [row] };
        Assert.Contains("A", PreferenceBuilder.BuildPeoplePreferenceSet(profile, []));
        Assert.Equal(0, SimilarityComputer.ComputeBillingWeightedPeople(new Dictionary<string, double>(), new Dictionary<string, double> { ["A"] = 3 }));
        Assert.Equal(0, SimilarityComputer.ComputeBillingWeightedPeople(new Dictionary<string, double> { ["A"] = double.NaN }, new Dictionary<string, double> { ["A"] = 3 }));
        Assert.Equal(0, SimilarityComputer.ComputeBillingWeightedPeople(new Dictionary<string, double> { ["A"] = 1 }, new Dictionary<string, double> { ["A"] = -1 }));
    }

    [Fact]
    public void ReconstructedDiscoveryRetainsTypedCreditsButCannotInventPersonalPeopleEvidence()
    {
        var credits = Snapshot(Guid.Empty);
        var entry = new DiscoveryFeedbackEntry
        {
            TmdbId = 700, MediaType = "movie", ShownAtUtc = Clock, RequestedAtUtc = Clock.AddDays(1),
            Genres = ["Drama"], KnownPeople = ["A"], ScoringFeatures = new CandidateFeatures { Credits = credits }
        };
        var user = Guid.NewGuid();
        var examples = DiscoveryFeedbackExampleBuilder.BuildDiscoveryExamples([new() { UserId = user, Entries = [entry] }],
            new Dictionary<Guid, UserWatchProfile>(), null, Enumerable.Repeat(.37, 41).ToArray(), TestContext.Current.CancellationToken).Examples;
        var features = Assert.Single(examples).Features;
        Assert.NotNull(features.Credits);
        Assert.False(features.HasPeopleMetadata);
        Assert.Equal(.37, features.ToModelVector(Enumerable.Repeat(.37, 41).ToArray())[(int)FeatureIndex.PeopleSimilarity]);
        Assert.Same(credits, entry.ScoringFeatures.Credits);
    }

    [Fact]
    public void ExternalPartialPositivesAndUnknownNonmatchesKeepIdentityAndImputation()
    {
        var profile = new UserWatchProfile { WatchedItems = [Row(Guid.NewGuid(), Clock)] };
        var means = Enumerable.Repeat(.37, 41).ToArray();
        var genres = new Dictionary<string, double> { ["Drama"] = 1 };
        var partial = Snapshot(Guid.Empty) with { Crew = null };
        var candidate = new TmdbDiscoverItem { Id = 1, GenreIds = [18], Credits = partial, KnownPeople = ["A"] };
        var positive = ExternalCandidateFeatureBuilder.Build(candidate, genres, new HashSet<string> { "A" }, 2000,
            PreferenceBuilder.BuildGenreExposureAnalysis(genres, profile), profile, means);
        Assert.True(positive.HasPeopleMetadata);
        Assert.True(positive.IsExternalFeatureKnown(FeatureIndex.PeopleSimilarity));
        Assert.NotEqual(.37, positive.PeopleSimilarity);
        candidate.Credits = partial with { Cast = partial.Cast! with { People = [Person("A", "999")] } };
        var unknown = ExternalCandidateFeatureBuilder.Build(candidate, genres, new HashSet<string> { "A" }, 2000,
            PreferenceBuilder.BuildGenreExposureAnalysis(genres, profile), profile, means);
        Assert.False(unknown.HasPeopleMetadata);
        Assert.False(unknown.IsExternalFeatureKnown(FeatureIndex.PeopleSimilarity));
        Assert.Equal(.37, unknown.ToModelVector(means)[13]);
        var existing = new CandidateFeatures { IsExternalCandidate = true };
        existing.MarkExternalFeatureKnown(FeatureIndex.WriterAffinity);
        existing.MarkExternalFeatureKnown(FeatureIndex.BillingWeightedPeople);
        CreditPreferenceProfile.Create(new UserWatchProfile()).Apply(existing, null);
        Assert.False(existing.IsExternalFeatureKnown(FeatureIndex.WriterAffinity));
        Assert.False(existing.IsExternalFeatureKnown(FeatureIndex.BillingWeightedPeople));
    }

    private static CreditMetadataSnapshot Snapshot(Guid id) => new() { SourceItemId = id, Cast = new() { People = [Person("A", "1")], Complete = true, AcquiredAtUtc = Clock.AddDays(-10) }, Crew = new() { People = [new() { Name = "W", Kind = PersonKind.Writer, Identities = [new("tmdb", "2")] }], Complete = true, AcquiredAtUtc = Clock.AddDays(-10) }, ProductionCountries = ["Japan"], CountriesAcquiredAtUtc = Clock.AddDays(-10) };
    private static WatchedItemInfo Row(Guid id, DateTime played) => new() { ItemId = id, ItemType = "Movie", Played = true, LastPlayedDate = played, Genres = ["Drama"], Credits = Snapshot(id), PeopleNames = ["A"], PeopleWeights = [1], WriterNames = ["W"] };
}
