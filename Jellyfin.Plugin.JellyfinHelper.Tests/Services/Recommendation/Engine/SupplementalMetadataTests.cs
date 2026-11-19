using System.Collections.Immutable;
using System.IO.Abstractions.TestingHelpers;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Complementary configured sources, typed links, conflict safety and source isolation.</summary>
public sealed class SupplementalMetadataTests
{
    [Fact]
    public void ConfiguredSourcesComplementEachOtherWithoutDuplicatingGenreOrKeywordEvidence()
    {
        var config = Configuration();
        var cache = new TitleMetadataCache();
        var now = DateTime.UtcNow;
        cache.Store(config, new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", GenreIds = [16], Keywords = ["anime", "pirate"], FetchedAtUtc = now });
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", SourceGenres = ["Animation", "Adventure"], Overview = "TVDB overview", FetchedAtUtc = now }, new("sonarr", "https://sonarr.example.test", "sonarr-test"), null);
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", SourceGenres = ["Animation"], OriginalLanguage = "ja", ProductionCountries = ["JP"], FetchedAtUtc = now }, new("trakt", "https://api.trakt.tv", "public-metadata"), null);
        var metadata = cache.Get(config, 123, "tv", 456)!;
        Assert.Equal(456, metadata.TvdbId);
        Assert.Equal(16, Assert.Single(metadata.GenreIds!.Value));
        Assert.Equal(2, metadata.SourceGenres!.Value.Length);
        Assert.Equal("ja", metadata.OriginalLanguage);
        Assert.Equal("TVDB overview", metadata.Overview);
        Assert.Contains("seerr", metadata.Source);
        Assert.Contains("sonarr", metadata.Source);
        Assert.Contains("trakt", metadata.Source);
    }

    [Fact]
    public void TvdbOnlyAndImdbOnlyMetadataCanBeLinkedWithoutInventingTmdbIds()
    {
        var config = Configuration();
        var cache = new TitleMetadataCache();
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TvdbId = 456, MediaType = "tv", SourceGenres = ["Drama"], FetchedAtUtc = DateTime.UtcNow }, new("sonarr", "https://sonarr.example.test", "sonarr-test"), null);
        var show = cache.Get(config, 0, "tv", 456)!;
        Assert.Equal(0, show.TmdbId);
        Assert.Equal(456, show.TvdbId);
        Assert.Null(cache.Get(config, 456, "movie"));
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { ImdbId = "tt123456", MediaType = "movie", SourceGenres = ["Drama"], FetchedAtUtc = DateTime.UtcNow }, new("radarr", "https://radarr.example.test", "radarr-test"), null);
        Assert.NotNull(cache.Get(config, 0, "movie", imdbId: "tt123456"));
        Assert.Null(cache.Get(config, 0, "tv", imdbId: "tt123456"));
    }

    [Fact]
    public void RemovingOrChangingAConfiguredProviderCannotReuseItsMetadata()
    {
        var config = Configuration();
        var cache = new TitleMetadataCache();
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, SourceGenres = ["Drama"], FetchedAtUtc = DateTime.UtcNow }, new("radarr", "https://radarr.example.test", "radarr-test"), null);
        Assert.NotNull(cache.Get(config, 123, "movie"));
        config.RadarrInstances[0].ApiKey = "different-test";
        Assert.Null(cache.Get(config, 123, "movie"));
        config.RadarrInstances.Clear();
        Assert.Null(cache.Get(config, 123, "movie"));
    }

    [Fact]
    public void ContradictoryLanguageOrFormatRemainsUnknownAndRetainsTheProvidedValues()
    {
        var now = DateTime.UtcNow;
        var metadata = TitleMetadataMerger.Combine([
            new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = "ja", SeriesType = "Scripted", Source = "seerr", FetchedAtUtc = now },
            new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = "en", SeriesType = "Documentary", Source = "trakt", FetchedAtUtc = now }
        ])!;
        Assert.Null(metadata.OriginalLanguage);
        Assert.Null(metadata.SeriesType);
        Assert.Equal(2, metadata.ConflictingValues![nameof(TitleMetadataSnapshot.OriginalLanguage)].Length);
        var repeated = TitleMetadataMerger.Combine([metadata, new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = "ja", Source = "seerr", FetchedAtUtc = now }])!;
        Assert.Null(repeated.OriginalLanguage);
        var descriptor = ContentDescriptor.Create(["Documentary"], [], metadata);
        Assert.Null(descriptor.Narrative);
    }

    [Fact]
    public void AConflictingCrossProviderIdentityCannotMixUnrelatedTitles()
    {
        var metadata = TitleMetadataMerger.Combine([
            new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", Keywords = ["pirate"], Source = "seerr", FetchedAtUtc = DateTime.UtcNow },
            new TitleMetadataSnapshot { TmdbId = 999, TvdbId = 456, MediaType = "tv", Keywords = ["unrelated"], Source = "sonarr", FetchedAtUtc = DateTime.UtcNow }
        ])!;
        Assert.Equal(123, metadata.TmdbId);
        Assert.Equal("pirate", Assert.Single(metadata.Keywords!.Value));
    }

    [Fact]
    public void SourceCombinationPreservesTheAcquisitionOfRetainedFields()
    {
        var old = DateTime.UtcNow.AddDays(-20);
        var current = DateTime.UtcNow;
        var metadata = TitleMetadataMerger.Combine([
            new TitleMetadataSnapshot { TmdbId = 123, Keywords = ["pirate"], FetchedAtUtc = current, Source = "seerr", FieldFetchedAtUtc = ImmutableDictionary<string, DateTime>.Empty.Add(nameof(TitleMetadataSnapshot.Keywords), old) },
            new TitleMetadataSnapshot { TmdbId = 123, SourceGenres = ["Adventure"], FetchedAtUtc = current, Source = "trakt" }
        ])!;
        Assert.Equal(old, metadata.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.Keywords)]);
        Assert.Equal(current, metadata.FieldFetchedAtUtc[nameof(TitleMetadataSnapshot.SourceGenres)]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void BlankPreferredProviderValuesCannotHideConsistentFieldsOrRenewTheirAge(string blank)
    {
        var now = DateTime.UtcNow;
        var acquired = now.AddDays(-5);
        var metadata = TitleMetadataMerger.Combine([
            new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = blank, SeriesType = blank, Overview = blank, FetchedAtUtc = now, Source = "seerr" },
            new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = "ja", SeriesType = "Scripted", Overview = "known", FetchedAtUtc = acquired, Source = "trakt" }
        ])!;
        Assert.Equal("ja", metadata.OriginalLanguage);
        Assert.Equal("scripted", metadata.SeriesType);
        Assert.Equal("known", metadata.Overview);
        Assert.Equal(acquired, metadata.FieldFetchedAtUtc![nameof(TitleMetadataSnapshot.OriginalLanguage)]);
        Assert.Equal(acquired, metadata.FieldFetchedAtUtc[nameof(TitleMetadataSnapshot.SeriesType)]);
        Assert.Equal(acquired, metadata.FieldFetchedAtUtc[nameof(TitleMetadataSnapshot.Overview)]);
        Assert.Null(metadata.ConflictingValues);
        var conflict = TitleMetadataMerger.Combine([metadata, new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = "en", FetchedAtUtc = now }])!;
        Assert.Null(conflict.OriginalLanguage);
        Assert.Equal(2, conflict.ConflictingValues![nameof(TitleMetadataSnapshot.OriginalLanguage)].Length);
    }

    [Fact]
    public void SonarrAnimeHintComplementsTmdbNarrativeWithoutReplacingItAndAChangedModeClearsTheHint()
    {
        var config = Configuration();
        var cache = new TitleMetadataCache();
        var source = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "sonarr-test");
        var now = DateTime.UtcNow;
        cache.Store(config, new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", SeriesType = "Scripted", GenreIds = [16], FetchedAtUtc = now });
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", AnimeHint = true, FetchedAtUtc = now }, source, null);
        var merged = cache.Get(config, 123, "tv")!;
        Assert.True(merged.AnimeHint);
        Assert.Equal("scripted", merged.SeriesType);
        Assert.Equal("anime", ContentDescriptor.Create([], [], merged).ProbableRepresentation);
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", AnimeHint = false, FetchedAtUtc = DateTime.UtcNow }, source, null);
        Assert.False(cache.Get(config, 123, "tv")!.AnimeHint);
        Assert.Null(ContentDescriptor.Create([], [], cache.Get(config, 123, "tv")).ProbableRepresentation);
    }

    [Theory]
    [InlineData("ja", "jpn", "ja")]
    [InlineData("Japanese", "japanisch", "ja")]
    [InlineData("de-DE", "German", "de")]
    [InlineData("deu", "ger", "de")]
    [InlineData("en", "unknown-language", null)]
    [InlineData("en", "English story text", null)]
    [InlineData("\ud800", "ja", null)]
    public void LanguageAliasesReuseTheSharedMapperAndUnknownValuesNeverBecomeCertainty(string first, string second, string? expected)
    {
        var now = DateTime.UtcNow;
        var metadata = TitleMetadataMerger.Combine([
            new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = first, FetchedAtUtc = now, Source = "seerr" },
            new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = second, FetchedAtUtc = now, Source = "trakt" }
        ])!;
        Assert.Equal(expected, metadata.OriginalLanguage);
        if (expected is null)
        {
            Assert.Contains(second, metadata.ConflictingValues![nameof(TitleMetadataSnapshot.OriginalLanguage)]);
        }
        else
        {
            Assert.Null(metadata.ConflictingValues);
        }
    }

    [Fact]
    public void SourceGenreLabelsAndFormatAliasesReuseTheExistingLocalizedCatalog()
    {
        var now = DateTime.UtcNow;
        var metadata = TitleMetadataMerger.Combine([
            new TitleMetadataSnapshot { TmdbId = 123, SeriesType = "Dokumentarfilm", SourceGenres = ["Dokumentarfilm"], FetchedAtUtc = now, Source = "seerr" },
            new TitleMetadataSnapshot { TmdbId = 123, SeriesType = "Documentary", SourceGenres = ["documentary"], FetchedAtUtc = now, Source = "trakt" }
        ])!;
        Assert.Equal("documentary", ContentDescriptor.Create([], [], metadata).Narrative);
        Assert.Null(metadata.ConflictingValues);
        Assert.Single(GenreNormalizer.Set(metadata.SourceGenres));
        var animation = ContentDescriptor.Create([], [], new TitleMetadataSnapshot
        {
            SourceGenres = ["Animation"], OriginalLanguage = "Japanese", ProductionCountries = ["JP"]
        });
        Assert.Null(animation.Representation);
        Assert.Equal("anime", animation.ProbableRepresentation);
    }

    [Fact]
    public void AbsentIdentityIsNotAProofThatUnrelatedSourcesDescribeTheSameTitle()
    {
        var now = DateTime.UtcNow;
        var metadata = TitleMetadataMerger.Combine([
            new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", Keywords = ["pirate"], Source = "seerr", FetchedAtUtc = now },
            new TitleMetadataSnapshot { TvdbId = 456, MediaType = "tv", Keywords = ["unrelated"], Source = "trakt", FetchedAtUtc = now }
        ])!;
        Assert.Equal("pirate", Assert.Single(metadata.Keywords!.Value));
        Assert.Equal(0, metadata.TvdbId);
    }

    [Fact]
    public void AVerifiedBridgeConnectsComplementaryIdsButCannotLaunderAThirdConflictingLink()
    {
        var now = DateTime.UtcNow;
        var metadata = TitleMetadataMerger.Combine([
            new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", Keywords = ["pirate"], Source = "seerr", FetchedAtUtc = now },
            new TitleMetadataSnapshot { TvdbId = 456, MediaType = "tv", OriginalLanguage = "ja", Source = "trakt", FetchedAtUtc = now },
            new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", SourceGenres = ["Animation"], Source = "sonarr", FetchedAtUtc = now },
            new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 999, MediaType = "tv", Keywords = ["wrong title"], Source = "radarr", FetchedAtUtc = now.AddSeconds(-1) }
        ])!;
        Assert.Equal("ja", metadata.OriginalLanguage);
        Assert.Equal(456, metadata.TvdbId);
        Assert.Equal("pirate", Assert.Single(metadata.Keywords!.Value));
    }

    [Fact]
    public void JellyfinSuppliedCrossProviderLinksCanJoinSeparateTmdbAndTvdbOnlyObservations()
    {
        var config = Configuration();
        var cache = new TitleMetadataCache();
        cache.Store(config, new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", Keywords = ["pirate"], FetchedAtUtc = DateTime.UtcNow });
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TvdbId = 456, MediaType = "tv", Overview = "Sonarr overview", FetchedAtUtc = DateTime.UtcNow }, new("sonarr", "https://sonarr.example.test", "sonarr-test"), null);
        var metadata = cache.Get(config, 123, "tv", 456)!;
        Assert.Equal("pirate", Assert.Single(metadata.Keywords!.Value));
        Assert.Equal("Sonarr overview", metadata.Overview);
        Assert.Equal(123, metadata.TmdbId);
        Assert.Equal(456, metadata.TvdbId);
    }

    [Fact]
    public void AmbiguousAliasesCannotHijackPrimaryIdsAndPersistAsAmbiguousAfterRestart()
    {
        var config = Configuration();
        var fs = new MockFileSystem();
        var path = Path.GetFullPath("supplemental-ambiguous.json");
        var cache = new TitleMetadataCache(fileSystem: fs, cachePath: path);
        var source = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "sonarr-test");
        foreach (var id in new[] { 123, 999 })
        {
            cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = id, TvdbId = 456, MediaType = "tv", Overview = id.ToString(System.Globalization.CultureInfo.InvariantCulture), FetchedAtUtc = DateTime.UtcNow }, source, null);
        }

        Assert.Null(cache.Get(config, 0, "tv", 456));
        Assert.Equal("123", cache.Get(config, 123, "tv")!.Overview);
        Assert.Equal("999", cache.Get(config, 999, "tv")!.Overview);
        Assert.Null(cache.Get(config, 123, "tv", 111));
        Assert.True(cache.Save());
        Assert.DoesNotContain("sonarr-test", fs.File.ReadAllText(path));
        var restored = new TitleMetadataCache(fileSystem: fs, cachePath: path);
        Assert.Null(restored.Get(config, 0, "tv", 456));
        Assert.Equal("123", restored.Get(config, 123, "tv", 456)!.Overview);
    }

    [Fact]
    public void CorrectedLinksRemoveOldAliasesAndDoNotRetainTheOldTitlesFields()
    {
        var config = Configuration();
        var cache = new TitleMetadataCache();
        var source = new MetadataSourceScope("sonarr", "https://sonarr.example.test", "sonarr-test");
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 456, MediaType = "tv", Overview = "old", FetchedAtUtc = DateTime.UtcNow.AddSeconds(-2) }, source, null);
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, TvdbId = 789, MediaType = "tv", SourceGenres = ["Drama"], FetchedAtUtc = DateTime.UtcNow }, source, null);
        Assert.Null(cache.Get(config, 0, "tv", 456));
        Assert.Null(cache.Get(config, 0, "tv", 789)!.Overview);
        Assert.Equal("Drama", Assert.Single(cache.Get(config, 123, "tv")!.SourceGenres!.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong-id")]
    public void AbsentOrMalformedImdbIdsDoNotConflictWithVerifiedLinks(string missing)
    {
        var config = Configuration();
        var cache = new TitleMetadataCache();
        cache.Store(config, new TitleMetadataSnapshot { TmdbId = 123, ImdbId = "tt123456", Keywords = ["pirate"], FetchedAtUtc = DateTime.UtcNow });
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, ImdbId = missing, Overview = "available", FetchedAtUtc = DateTime.UtcNow }, new("radarr", "https://radarr.example.test", "radarr-test"), null);
        var metadata = cache.Get(config, 123, "movie")!;
        Assert.Equal("available", metadata.Overview);
        Assert.Equal("tt123456", metadata.ImdbId);
        Assert.Equal("pirate", Assert.Single(metadata.Keywords!.Value));
    }

    [Fact]
    public void LiveTraktAvailabilityWithdrawsPersistedMetadataWithoutAffectingOtherSources()
    {
        var config = Configuration();
        var available = true;
        var cache = new TitleMetadataCache { TraktMetadataAvailable = _ => available };
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, OriginalLanguage = "ja", FetchedAtUtc = DateTime.UtcNow }, new("trakt", "https://api.trakt.tv", "public-metadata"), null);
        cache.StoreSupplemental(config, new TitleMetadataSnapshot { TmdbId = 123, Overview = "ARR metadata", FetchedAtUtc = DateTime.UtcNow }, new("radarr", "https://radarr.example.test", "radarr-test"), null);
        Assert.Equal("ja", cache.Get(config, 123, "movie")!.OriginalLanguage);
        available = false;
        Assert.Null(cache.Get(config, 123, "movie")!.OriginalLanguage);
        Assert.Equal("ARR metadata", cache.Get(config, 123, "movie")!.Overview);
    }

    [Fact]
    public void SupplementalValidationRejectsInvalidIdsNamespacesFutureTimesAndInactiveProviders()
    {
        var config = Configuration();
        var cache = new TitleMetadataCache();
        var source = new MetadataSourceScope("radarr", "https://radarr.example.test", "radarr-test");
        var valid = new TitleMetadataSnapshot { TmdbId = 123, FetchedAtUtc = DateTime.UtcNow };
        Assert.Null(cache.StoreSupplemental(config, valid with { TmdbId = 0, ImdbId = "invalid" }, source, null));
        Assert.Null(cache.StoreSupplemental(config, valid with { MediaType = "unknown" }, source, null));
        Assert.Null(cache.StoreSupplemental(config, valid with { FetchedAtUtc = DateTime.UtcNow.AddDays(1) }, source, null));
        Assert.Null(cache.StoreSupplemental(config, valid, source with { ApiKey = "other-test" }, null));
        Assert.Throws<ArgumentNullException>(() => cache.StoreSupplemental(null!, valid, source, null));
        Assert.Throws<ArgumentNullException>(() => cache.StoreSupplemental(config, null!, source, null));
        Assert.Null(TitleMetadataMerger.Combine([]));
    }

    private static PluginConfiguration Configuration()
        => new()
        {
            SeerrUrl = "https://seerr.example.test", SeerrApiKey = "seerr-test", TraktSourcingEnabled = true,
            RadarrInstances = [new ArrInstanceConfig { Url = "https://radarr.example.test", ApiKey = "radarr-test" }],
            SonarrInstances = [new ArrInstanceConfig { Url = "https://sonarr.example.test", ApiKey = "sonarr-test" }]
        };
}
