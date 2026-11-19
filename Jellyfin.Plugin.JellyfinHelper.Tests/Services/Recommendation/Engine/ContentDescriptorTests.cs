using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Content families require real evidence, not guessed country, audience or adaptation labels.</summary>
public sealed class ContentDescriptorTests
{
    [Theory]
    [InlineData("Animation", "anime", "anime")]
    [InlineData("Animation", "japanese animation", "anime")]
    [InlineData("Action", "live action", "live action")]
    [InlineData("Animation", "western animation", "animation")]
    [InlineData("Animation", "american animation", "animation")]
    [InlineData("Reality", "competition", "reality")]
    [InlineData("Documentary", "true crime", "true crime")]
    [InlineData("Documentary", "nature", "documentary")]
    [InlineData("News", "politics", "news")]
    [InlineData("Talk", "interview", "talk show")]
    [InlineData("Crime", "true crime", null)]
    [InlineData("Animation", "preschool", null)]
    [InlineData("Animation", "based on manga", null)]
    [InlineData("Animation", "live action and animation", null)]
    [InlineData("Animation", "live action", null)]
    [InlineData("Action", "Sonarr: anime", null)]
    public void ClassificationRespectsIndependentRepresentationFormatAndTopicEvidence(string genre, string tag, string? expected)
        => Assert.Equal(expected, ContentDescriptor.Create([genre], [tag]).Family);

    [Theory]
    [InlineData("Reality", "reality")]
    [InlineData("Documentary", "documentary")]
    [InlineData("News", "news")]
    [InlineData("Talk Show", "talk show")]
    [InlineData("Scripted", null)]
    [InlineData("Miniseries", null)]
    public void StructuredSeriesFormatWorksWithoutGenreLabels(string format, string? expected)
        => Assert.Equal(expected, ContentDescriptor.Create([], null, new TitleMetadataSnapshot { SeriesType = format }).Family);

    [Fact]
    public void JapaneseOriginAndLanguageAreNotProofOfAnime()
        => Assert.Null(ContentDescriptor.Create(["Animation"], ["based on manga"], new TitleMetadataSnapshot { OriginalLanguage = "ja", OriginCountries = ["JP"] }).Family);

    [Fact]
    public void StableGenresAndProviderKeywordsWorkWhenLocalMetadataIsEmpty()
        => Assert.Equal("true crime", ContentDescriptor.Create([], [], new TitleMetadataSnapshot { GenreIds = [99], Keywords = ["true crime"] }).Family);

    [Fact]
    public void ContradictoryAnimeAndLiveActionEvidenceRemainsUnknown()
        => Assert.Null(ContentDescriptor.Create(["Action"], ["anime", "live action"]).Family);

    [Theory]
    [InlineData("anime", "western animation")]
    [InlineData("japanese animation", "american animation")]
    [InlineData("western animation", "anime")]
    public void ConflictingLocalAndProviderStylesCannotClassifyOrGuessTheRepresentation(string local, string provider)
    {
        var descriptor = ContentDescriptor.Create(["Animation"], [local], new TitleMetadataSnapshot { Keywords = [provider], OriginalLanguage = "ja", OriginCountries = ["JP"] });
        Assert.Null(descriptor.Representation);
        Assert.Null(descriptor.ProbableRepresentation);
        Assert.Null(descriptor.Family);
    }

    [Fact]
    public void AnimeDocumentaryRetainsBothIndependentContentDimensions()
    {
        var descriptor = ContentDescriptor.Create(["Animation", "Documentary"], ["anime"]);
        Assert.Equal("anime", descriptor.Representation);
        Assert.Equal("documentary", descriptor.Narrative);
        Assert.Equal("documentary", descriptor.Family);
    }

    [Fact]
    public void ScriptedDoesNotInventLiveActionRepresentation()
    {
        var descriptor = ContentDescriptor.Create([], [], new TitleMetadataSnapshot { SeriesType = "Scripted" });
        Assert.Null(descriptor.Representation);
        Assert.Equal("scripted", descriptor.Narrative);
    }

    [Theory]
    [InlineData("true crime", "true crime")]
    [InlineData("nature documentary", "nature")]
    [InlineData("wildlife documentary", "nature")]
    [InlineData("historical documentary", "history")]
    [InlineData("sports documentary", "sports")]
    public void ExplicitDocumentaryTopicsStaySeparateFromGenericGenres(string tag, string topic)
        => Assert.Contains(topic, ContentDescriptor.Create(["Documentary"], [tag]).Topics!);

    [Fact]
    public void BroadCrimeAndNatureLabelsDoNotInventVerifiedDocumentaryTopics()
    {
        Assert.Empty(ContentDescriptor.Create(["Crime"], ["true crime"]).Topics!);
        Assert.Empty(ContentDescriptor.Create(["Documentary"], ["nature"]).Topics!);
    }

    [Theory]
    [InlineData("ja", "JP", "anime")]
    [InlineData("en", "US", "animation")]
    [InlineData("ja", "US", null)]
    [InlineData("en", "JP", null)]
    [InlineData(null, "JP", null)]
    public void OriginLanguageAndAnimationOnlyCreateALikelyRepresentation(string? language, string country, string? expected)
    {
        var descriptor = ContentDescriptor.Create(["Animation"], [], new TitleMetadataSnapshot { OriginalLanguage = language, ProductionCountries = [country] });
        Assert.Null(descriptor.Representation);
        Assert.Null(descriptor.Family);
        Assert.Equal(expected, descriptor.ProbableRepresentation);
    }

    [Fact]
    public void MissingAnimationAndContradictoryRepresentationCannotBecomeAnOriginBasedGuess()
    {
        var metadata = new TitleMetadataSnapshot { OriginalLanguage = "ja", OriginCountries = ["JP"] };
        Assert.Null(ContentDescriptor.Create(["Action"], [], metadata).ProbableRepresentation);
        Assert.Null(ContentDescriptor.Create(["Animation"], ["live action", "anime"], metadata).ProbableRepresentation);
    }
}
