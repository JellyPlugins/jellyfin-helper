using System.Collections.Immutable;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Typed metadata, provider field names, immutability and unknown-versus-empty semantics.</summary>
public sealed class TitleMetadataSnapshotTests
{
    [Fact]
    public void SeerrDetailsPreserveStructuredMetadataAndStableKeywordIds()
    {
        var detail = JsonSerializer.Deserialize<SeerrMediaDetailResponse>("""
            {"id":123,"genres":[{"id":16,"name":"Animation"}],"keywords":[{"id":42,"name":"anime"}],
             "originalLanguage":"ja","originCountry":["JP"],"type":"Scripted",
             "productionCountries":[{"iso_3166_1":"JP","name":"Japan"}],"overview":"Synopsis"}
            """)!;
        var now = DateTime.UtcNow;
        var snapshot = TitleMetadataSnapshot.FromDetail(detail, "tv", now);
        Assert.Equal(123, snapshot.TmdbId);
        Assert.Equal("tv", snapshot.MediaType);
        Assert.Equal([16], snapshot.GenreIds!.Value);
        Assert.Equal([42], snapshot.KeywordIds!.Value);
        Assert.Equal(["anime"], snapshot.Keywords!.Value);
        Assert.Equal(new ProviderKeyword(42, "anime"), Assert.Single(snapshot.KeywordReferences!.Value));
        Assert.Equal("ja", snapshot.OriginalLanguage);
        Assert.Equal("Scripted", snapshot.SeriesType);
        Assert.Equal(["JP"], snapshot.OriginCountries!.Value);
        Assert.Equal(["JP"], snapshot.ProductionCountries!.Value);
        Assert.Equal("Synopsis", snapshot.Overview);
        Assert.Equal(now, snapshot.FetchedAtUtc);
        Assert.Equal("seerr", snapshot.Source);
        detail.Keywords![0].Name = "changed";
        Assert.Equal("anime", Assert.Single(snapshot.Keywords!.Value));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"genres\":[],\"keywords\":[]}", true)]
    public void MissingMetadataIsNotConfusedWithKnownEmptyMetadata(string json, bool known)
    {
        var snapshot = TitleMetadataSnapshot.FromDetail(JsonSerializer.Deserialize<SeerrMediaDetailResponse>(json)!, "movie", DateTime.UtcNow);
        Assert.Equal(known, snapshot.GenreIds.HasValue);
        Assert.Equal(known, snapshot.Keywords.HasValue);
        Assert.Equal(known, snapshot.KeywordIds.HasValue);
        Assert.Null(snapshot.OriginCountries);
        Assert.Null(snapshot.ProductionCountries);
    }

    [Fact]
    public void SnapshotRoundTripsWithoutLosingNullableImmutableMetadata()
    {
        var original = new TitleMetadataSnapshot { TmdbId = 123, MediaType = "tv", KeywordIds = [42], Keywords = ImmutableArray<string>.Empty, FetchedAtUtc = DateTime.UtcNow };
        var restored = JsonSerializer.Deserialize<TitleMetadataSnapshot>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original.TmdbId, restored.TmdbId);
        Assert.Equal(original.KeywordIds!.Value, restored.KeywordIds!.Value);
        Assert.Empty(restored.Keywords!.Value);
        Assert.Null(restored.GenreIds);
    }
}
