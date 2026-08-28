using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Behavioral tests for the shared ContentAffinityResolver resolvers that feed the content-affinity features (series status and end date, TMDb collection, production countries, inherited tags, writers).
/// </summary>
public sealed class ContentAffinityResolverTests
{
    [Theory]
    [InlineData(SeriesStatus.Continuing, "Continuing")]
    [InlineData(SeriesStatus.Ended, "Ended")]
    [InlineData(SeriesStatus.Unreleased, "Unreleased")]
    public void ResolveSeriesStatus_SeriesWithStatus_ReturnsStatusName(SeriesStatus status, string expected)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "S", Status = status };

        // Exact enum-name string, since the feature keys off this token verbatim across call sites.
        Assert.Equal(expected, ContentAffinityResolver.ResolveSeriesStatus(series));
    }

    [Fact]
    public void ResolveSeriesStatus_SeriesWithoutStatus_ReturnsNull()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "S", Status = null };

        // Missing metadata must neutralize to null, not an empty string that would emit a bogus token.
        Assert.Null(ContentAffinityResolver.ResolveSeriesStatus(series));
    }

    [Fact]
    public void ResolveTmdbCollectionName_MovieWithName_ReturnsCollectionName()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "M", TmdbCollectionName = "The Matrix Collection" };

        // Franchise token feeds the collection-affinity feature verbatim across call sites.
        Assert.Equal("The Matrix Collection", ContentAffinityResolver.ResolveTmdbCollectionName(movie));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveTmdbCollectionName_MovieWithBlankName_ReturnsNull(string? name)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "M", TmdbCollectionName = name };

        // Blank must neutralize to null so no spurious franchise token is emitted.
        Assert.Null(ContentAffinityResolver.ResolveTmdbCollectionName(movie));
    }

    [Fact]
    public void ResolveTmdbCollectionName_NonMovie_ReturnsNull()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "S" };

        // Only movies carry a TMDb collection; non-movies are always null.
        Assert.Null(ContentAffinityResolver.ResolveTmdbCollectionName(series));
    }

    [Fact]
    public void ResolveProductionCountries_WithLocations_ReturnsList()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "M", ProductionLocations = ["US", "GB"] };

        // Order-preserving copy so training and scoring see the identical country set.
        Assert.Equal(new List<string> { "US", "GB" }, ContentAffinityResolver.ResolveProductionCountries(movie));
    }

    [Fact]
    public void ResolveProductionCountries_NoLocations_ReturnsEmptyList()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "M" };

        // Missing metadata must be an empty (non-null) list so callers can iterate unconditionally.
        Assert.Empty(ContentAffinityResolver.ResolveProductionCountries(movie));
    }

    // The positive case (tags actually flow through) is not unit-testable here: GetInheritedTags() walks the LibraryManager and parent chain, so a bare Movie with Tags set still resolves to empty.
    [Fact]
    public void ResolveInheritedTags_NoTags_ReturnsEmptyList()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "M" };

        // Missing tags must be an empty, non-null list, never a throw or null.
        Assert.Empty(ContentAffinityResolver.ResolveInheritedTags(movie));
    }

    [Fact]
    public void ResolveSeriesEndDate_Series_ReturnsEndDate()
    {
        var endDate = new DateTime(2021, 5, 23, 0, 0, 0, DateTimeKind.Utc);
        var series = new Series { Id = Guid.NewGuid(), Name = "S", EndDate = endDate };

        // Lifecycle date feeds the completability feature; the exact value is a parity contract.
        Assert.Equal(endDate, ContentAffinityResolver.ResolveSeriesEndDate(series));
    }

    [Fact]
    public void ResolveSeriesEndDate_NonSeries_ReturnsNull()
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "M" };

        // Only series carry an end date; non-series must neutralize to null.
        Assert.Null(ContentAffinityResolver.ResolveSeriesEndDate(movie));
    }

    [Fact]
    public void ExtractWriterNames_MixedPeople_ReturnsDistinctWritersFirstSeenCasing()
    {
        var people = new List<PersonInfo>
        {
            new() { Name = "Jane Writer", Type = PersonKind.Writer },
            new() { Name = "Some Actor", Type = PersonKind.Actor },
            new() { Name = "A Director", Type = PersonKind.Director },
            new() { Name = "jane writer", Type = PersonKind.Writer },
            new() { Name = "   ", Type = PersonKind.Writer },
            new() { Name = null, Type = PersonKind.Writer },
            new() { Name = "John Scribe", Type = PersonKind.Writer },
        };

        // Case-insensitive dedup keeping first-seen casing, writers only. This is the writer-affinity parity set.
        Assert.Equal(new List<string> { "Jane Writer", "John Scribe" }, ContentAffinityResolver.ExtractWriterNames(people));
    }

    [Fact]
    public void ExtractWriterNames_NullInput_ReturnsEmptyList()
    {
        // Null people must be an empty (non-null) list, never a throw.
        Assert.Empty(ContentAffinityResolver.ExtractWriterNames(null));
    }

    [Fact]
    public void ExtractWriterNames_EmptyInput_ReturnsEmptyList()
    {
        // Empty people must be an empty (non-null) list.
        Assert.Empty(ContentAffinityResolver.ExtractWriterNames(new List<PersonInfo>()));
    }
}
