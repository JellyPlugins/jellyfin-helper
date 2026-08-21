using System;
using System.Collections.Generic;
using System.Reflection;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the pure <c>private static</c> parsing/filtering helpers on
///     <see cref="Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine"/>.
///     These sit on the recommendation hot path and encode real filtering rules
///     (skip pathless/orphan episodes, dedupe languages, drop non-billed people), so their
///     edge cases are worth pinning even though the enclosing methods are private. They take
///     plain model objects and touch no library host, so they run deterministically via reflection.
/// </summary>
public sealed class EngineStaticHelpersTests
{
    private static readonly Type EngineType =
        typeof(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine);

    // ================================================================================
    // CountPlayableEpisodesPerSeries
    // ================================================================================

    [Fact]
    public void CountPlayableEpisodesPerSeries_SkipsEpisodesWithoutPathOrSeriesId()
    {
        var seriesId = Guid.NewGuid();
        var episodes = new List<BaseItem>
        {
            new Episode { Id = Guid.NewGuid(), Path = "/media/s1e1.mkv", SeriesId = seriesId },   // counted
            new Episode { Id = Guid.NewGuid(), Path = "/media/s1e2.mkv", SeriesId = seriesId },   // counted
            new Episode { Id = Guid.NewGuid(), Path = null,               SeriesId = seriesId },  // skipped: no path
            new Episode { Id = Guid.NewGuid(), Path = "/media/x.mkv",     SeriesId = Guid.Empty }, // skipped: no series
        };

        var result = InvokeCountPlayableEpisodesPerSeries(episodes);

        Assert.Single(result);
        Assert.Equal(2, result[seriesId]);
    }

    [Fact]
    public void CountPlayableEpisodesPerSeries_NonEpisodeItems_AreIgnored()
    {
        // Only Episode instances count; a stray non-episode BaseItem must not throw or be counted.
        var episodes = new List<BaseItem> { new Movie { Id = Guid.NewGuid(), Path = "/m.mkv" } };

        var result = InvokeCountPlayableEpisodesPerSeries(episodes);

        Assert.Empty(result);
    }

    // ================================================================================
    // ParseLanguagesFromStreams
    // ================================================================================

    [Fact]
    public void ParseLanguagesFromStreams_DedupesAndSkipsBlankLanguages()
    {
        var streams = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Audio, Language = "eng" },
            new() { Type = MediaStreamType.Audio, Language = "eng" },   // duplicate → deduped
            new() { Type = MediaStreamType.Audio, Language = "  " },    // blank → skipped
            new() { Type = MediaStreamType.Subtitle, Language = "ger" },
            new() { Type = MediaStreamType.Subtitle, Language = null }, // null → skipped
        };

        var (audio, subtitles) = InvokeParseLanguagesFromStreams(streams);

        // NormalizeLanguage maps ISO 639-2 codes to short forms: eng→en, ger→de.
        Assert.Single(audio);
        Assert.Contains("en", audio);
        Assert.Single(subtitles);
        Assert.Contains("de", subtitles);
    }

    // ================================================================================
    // ResolveStreamsLanguages: null-streams early return
    // ================================================================================

    [Fact]
    public void ResolveStreamsLanguages_CandidateWithoutStreams_ReturnsEmptyLists()
    {
        // A bare Movie has no attached media streams, so GetMediaStreams() yields null/empty and the
        // helper must return two empty lists rather than throwing.
        var (audio, subtitles) = InvokeResolveStreamsLanguages(new Movie { Id = Guid.NewGuid() });

        Assert.Empty(audio);
        Assert.Empty(subtitles);
    }

    // ================================================================================
    // ExtractBillingWeightMap
    // ================================================================================

    [Fact]
    public void ExtractBillingWeightMap_KeepsActorsAndDirectors_DropsOthersAndBlanks()
    {
        var people = new List<PersonInfo>
        {
            new() { Name = "Lead Actor", Type = PersonKind.Actor, SortOrder = 0 },
            new() { Name = "The Director", Type = PersonKind.Director, SortOrder = 1 },
            new() { Name = "A Writer", Type = PersonKind.Writer },     // dropped: not actor/director
            new() { Name = "   ", Type = PersonKind.Actor },           // dropped: blank name
        };

        var map = InvokeExtractBillingWeightMap(people);

        Assert.Equal(2, map.Count);
        Assert.True(map.ContainsKey("Lead Actor"));
        Assert.True(map.ContainsKey("The Director"));
        // Top-billed (SortOrder 0) must weigh at least as much as the next-billed entry.
        Assert.True(map["Lead Actor"] >= map["The Director"]);
    }

    [Fact]
    public void ExtractBillingWeightMap_NullOrEmpty_ReturnsEmptyMap()
    {
        Assert.Empty(InvokeExtractBillingWeightMap(null));
        Assert.Empty(InvokeExtractBillingWeightMap(new List<PersonInfo>()));
    }

    // ================================================================================
    // Reflection glue - all helpers are `private static`.
    // ================================================================================

    private static Dictionary<Guid, int> InvokeCountPlayableEpisodesPerSeries(IReadOnlyList<BaseItem> episodes)
    {
        var method = EngineType.GetMethod("CountPlayableEpisodesPerSeries", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Dictionary<Guid, int>)method!.Invoke(null, [episodes])!;
    }

    private static (List<string> Audio, List<string> Subtitles) InvokeParseLanguagesFromStreams(IReadOnlyList<MediaStream> streams)
    {
        var method = EngineType.GetMethod("ParseLanguagesFromStreams", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return ((List<string>, List<string>))method!.Invoke(null, [streams])!;
    }

    private static (List<string> Audio, List<string> Subtitles) InvokeResolveStreamsLanguages(BaseItem candidate)
    {
        var method = EngineType.GetMethod("ResolveStreamsLanguages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return ((List<string>, List<string>))method!.Invoke(null, [candidate])!;
    }

    private static Dictionary<string, double> InvokeExtractBillingWeightMap(IReadOnlyList<PersonInfo>? people)
    {
        var method = EngineType.GetMethod("ExtractBillingWeightMap", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Dictionary<string, double>)method!.Invoke(null, [people])!;
    }
}
