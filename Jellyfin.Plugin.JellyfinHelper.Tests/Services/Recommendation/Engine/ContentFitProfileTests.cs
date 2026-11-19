using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Coherent semantic tags, weak audience hints, source provenance and independent user snapshots.</summary>
public sealed class ContentFitProfileTests
{
    private static readonly string[] BattleKeywords = ["anime", "shounen", "pirate", "friendship", "super power"];
    private static readonly Dictionary<string, double> BattleGenres = new() { ["Animation"] = 1, ["Action"] = 1, ["Adventure"] = 1, ["Family"] = .1 };

    [Theory]
    [InlineData(true, null)]
    [InlineData(true, 9.0)]
    [InlineData(true, 2.0)]
    [InlineData(false, null)]
    public void MinorPositiveChildrenInterestSurvivesAnOlderAnimationMajority(bool played, double? rating)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var id = 0; id < 3; id++) profile.WatchedItems.Add(Watch(["Animation", "Action"], BattleKeywords));
        var children = Watch(["Animation", "Kids"], ["preschool", "dog", "rescue mission"]);
        children.Played = played;
        children.UserRating = rating;
        profile.WatchedItems.Add(children);
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .7 };
        ContentFitProfile.Create(profile).Apply(features, ["Animation", "Kids"], ["preschool", "dog", "rescue mission"], "FSK-0", BattleGenres);
        var positive = played && rating is not <= 5;
        Assert.Equal(positive, ScoringHelper.IsContentEligible(features));
        if (positive)
        {
            Assert.Equal(1, features.ContentFitSupport);
            Assert.NotNull(features.ContentTasteKey);
            Assert.Null(features.AudienceMismatch);
        }
        else
        {
            Assert.Equal(.5, features.ContentFamilyMismatch);
        }
    }

    [Fact]
    public void PositiveKidsGenreAlonePreventsAnAudienceBanEvenWhenGenreWeightsAreSmall()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var id = 0; id < 3; id++) profile.WatchedItems.Add(Watch(["Animation"], BattleKeywords));
        profile.WatchedItems.Add(Watch(["Kids"], []));
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .7 };
        ContentFitProfile.Create(profile).Apply(features, ["Animation", "Kids"], ["preschool"], null, BattleGenres);
        Assert.True(ScoringHelper.IsContentEligible(features));
    }

    [Theory]
    [InlineData("western animation")]
    [InlineData(null)]
    public void AHighlyClassifiedAnimeMajorityCannotInventTheStyleOfAPositiveChildrenInterest(string? style)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var id = 0; id < 10; id++) profile.WatchedItems.Add(Watch(["Animation"], BattleKeywords));
        profile.WatchedItems.Add(Watch(["Animation", "Kids"], ["preschool", "dog", "rescue mission"]));
        var tags = new List<string> { "preschool", "dog", "rescue mission" };
        if (style is not null) tags.Add(style);
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .7 };
        ContentFitProfile.Create(profile).Apply(features, ["Animation", "Kids"], tags, null, BattleGenres,
            new TitleMetadataSnapshot { OriginalLanguage = "en", ProductionCountries = ["US"] });
        Assert.True(ScoringHelper.IsContentEligible(features));
        Assert.Null(features.ContentFamilyMismatch);
        Assert.Equal(1, features.ContentFitSupport);
    }

    [Fact]
    public void ChildrenRepresentationUncertaintyDoesNotEraseKnownNarrativeFormatEvidence()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var id = 0; id < 10; id++)
        {
            var title = Watch(["Animation"], BattleKeywords);
            title.TitleMetadata = new TitleMetadataSnapshot { SeriesType = "Scripted" };
            profile.WatchedItems.Add(title);
        }

        var child = Watch(["Kids"], ["preschool", "dog", "rescue mission"]);
        child.TitleMetadata = new TitleMetadataSnapshot { SeriesType = "Scripted" };
        profile.WatchedItems.Add(child);
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .7 };
        ContentFitProfile.Create(profile).Apply(features, ["Kids"], ["preschool", "dog", "rescue mission"], null, BattleGenres,
            new TitleMetadataSnapshot { SeriesType = "Reality" });
        Assert.Equal(.5, features.ContentFamilyMismatch);
    }

    [Fact]
    public void IdentityOnlyMetadataStillProvidesTranslatedCoherentEvidenceWhenKeywordLabelsAreUnavailable()
    {
        var row = Watch(["Adventure"], []);
        row.TitleMetadata = new TitleMetadataSnapshot { KeywordReferences = [new(42, "pirate"), new(43, "friendship")] };
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [row] };
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = 1 };
        ContentFitProfile.Create(profile).Apply(features, ["Adventure"], null, null, new() { ["Adventure"] = 1 },
            new TitleMetadataSnapshot { KeywordReferences = [new(42, "pirata"), new(43, "amistad")] });
        Assert.True(features.HasContentMetadata);
        Assert.Equal(1, features.ContentFitSupport);
        Assert.NotNull(features.ContentTasteKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MissingOrEmptyCandidateIdentitiesCannotInventSupportFromAnIdentityOnlyHistory(int metadataKind)
    {
        var row = Watch(["Adventure"], []);
        row.TitleMetadata = new TitleMetadataSnapshot { KeywordReferences = [new(42, "pirate"), new(43, "friendship")] };
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [row] };
        TitleMetadataSnapshot? candidate = metadataKind switch
        {
            0 => null,
            1 => new TitleMetadataSnapshot(),
            _ => new TitleMetadataSnapshot { KeywordReferences = [] }
        };
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = 1 };
        ContentFitProfile.Create(profile).Apply(features, ["Adventure"], null, null, new() { ["Adventure"] = 1 }, candidate);
        Assert.Equal(metadataKind == 2, features.HasContentMetadata);
        Assert.Null(features.ContentFitSupport);
        Assert.Null(features.ContentTasteKey);
        Assert.True(ScoringHelper.IsContentEligible(features));
    }

    [Theory]
    [InlineData("Family", 1)]
    [InlineData("Kids", 1)]
    [InlineData("Animation", 0)]
    public void ExplicitChildrenMetadataDoesNotBanFamilyPreferencesOrMissingGenreEvidence(string preference, double weight)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var id = 0; id < 3; id++) profile.WatchedItems.Add(Watch(["Animation"], BattleKeywords));
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .7 };
        ContentFitProfile.Create(profile).Apply(features, ["Animation", "Kids"], ["preschool"], null, new() { [preference] = weight });
        Assert.True(ScoringHelper.IsContentEligible(features));
    }

    [Fact]
    public void ProviderKeywordIdentityFilteringRejectsMissingIdsLabelsAndTechnicalTerms()
    {
        var metadata = new TitleMetadataSnapshot { KeywordReferences = [new(0, "pirate"), new(42, null), new(43, "1080p"), new(44, "anime"), new(45, "Action"), new(46, "based on manga")] };
        var row = Watch(["Animation"], []);
        row.TitleMetadata = metadata;
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [row] };
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = 1 };
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], [], null, BattleGenres, metadata);
        Assert.Null(features.ContentFitSupport);
        Assert.Null(features.ContentTasteKey);
    }

    [Fact]
    public void OlderOrEqualEpisodeMetadataCannotReplaceTheNewestTitleSnapshot()
    {
        var parent = Guid.NewGuid();
        var acquired = DateTime.UtcNow;
        var newest = Watch(["Adventure"], []);
        newest.SeriesId = parent;
        newest.TitleMetadata = new TitleMetadataSnapshot { Keywords = ["pirate", "friendship"], FetchedAtUtc = acquired };
        var older = Watch(["Adventure"], []);
        older.SeriesId = parent;
        older.TitleMetadata = new TitleMetadataSnapshot { Keywords = [], FetchedAtUtc = acquired.AddDays(-1) };
        var equal = Watch(["Adventure"], []);
        equal.SeriesId = parent;
        equal.TitleMetadata = new TitleMetadataSnapshot { Keywords = [], FetchedAtUtc = acquired };
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [newest, older, equal] };
        Assert.Equal(1, ContentFitProfile.Create(profile).ScoreTags(["pirate", "friendship"]));
    }

    [Theory]
    [InlineData("anime")]
    [InlineData("western animation")]
    public void ConflictingStyleMetadataDoesNotExcludeEitherPositiveRepresentation(string taste)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var id = 0; id < 3; id++) profile.WatchedItems.Add(Watch(["Animation"], [taste]));
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = 1 };
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], ["western animation"], null, BattleGenres,
            new TitleMetadataSnapshot { Keywords = ["anime"], OriginalLanguage = "ja", OriginCountries = ["JP"] });
        Assert.True(ScoringHelper.IsContentEligible(features));
        Assert.Null(features.ContentFamilyMismatch);
    }

    [Theory]
    [InlineData("anime", "Animation")]
    [InlineData("live action", "Action")]
    [InlineData("competition", "Reality")]
    [InlineData("true crime", "Documentary")]
    public void NarrowTasteModeratesOtherKnownFamiliesWithoutExcludingUnknowns(string tag, string genre)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++) profile.WatchedItems.Add(Watch([genre], [tag]));
        var content = ContentFitProfile.Create(profile);
        var supported = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true };
        content.Apply(supported, [genre], [tag], null, new() { [genre] = 1 });
        Assert.Equal(0, supported.ContentFamilyMismatch);
        var mismatch = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true };
        var otherTag = tag == "anime" ? "live action" : "anime";
        content.Apply(mismatch, otherTag == "anime" ? ["Animation"] : ["Action"], [otherTag], null, new() { [genre] = 1 }, new TitleMetadataSnapshot { SeriesType = "Scripted" });
        Assert.Equal(.5, mismatch.ContentFamilyMismatch);
        var unknown = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true };
        content.Apply(unknown, ["Action"], null, null, new() { [genre] = 1 });
        Assert.Null(unknown.ContentFamilyMismatch);
    }

    [Fact]
    public void RareSecondaryInterestSurvivesHundredsOfEpisodesFromThePrimaryInterest()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++)
        {
            var series = Guid.NewGuid();
            for (var episode = 0; episode < 100; episode++)
            {
                var row = Watch(["Animation"], ["anime"]);
                row.SeriesId = series;
                row.SeriesTmdbId = i + 1;
                profile.WatchedItems.Add(row);
            }
        }

        profile.WatchedItems.Add(Watch(["Action"], ["live action"]));
        var features = new CandidateFeatures { GenreSimilarity = .5, HasGenreEvidence = true };
        ContentFitProfile.Create(profile).Apply(features, ["Action"], ["live action"], null, new() { ["Animation"] = 1, ["Action"] = .1 });
        Assert.Equal(0, features.ContentFamilyMismatch);
    }

    [Fact]
    public void SparseOrMostlyUnknownHistoryCannotInventANarrowTaste()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(["Animation"], ["anime"]), Watch(["Animation"], ["anime"])] };
        var features = new CandidateFeatures();
        ContentFitProfile.Create(profile).Apply(features, ["Action"], ["live action"], null, []);
        Assert.Null(features.ContentFamilyMismatch);
        for (var i = 0; i < 10; i++) profile.WatchedItems.Add(Watch(["Drama"], []));
        ContentFitProfile.Create(profile).Apply(features, ["Action"], ["live action"], null, []);
        Assert.Null(features.ContentFamilyMismatch);
    }

    [Fact]
    public void CachedHistoryAndCandidateDetailsUseTheSameContentEvidenceWithoutChangingRawTags()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++)
        {
            var row = Watch(["Animation"], []);
            row.TitleMetadata = new TitleMetadataSnapshot { Keywords = ["anime", "pirate", "friendship"], FetchedAtUtc = DateTime.UtcNow };
            profile.WatchedItems.Add(row);
        }

        var features = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true, TagSimilarity = .123 };
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], [], null, new() { ["Animation"] = 1 }, new TitleMetadataSnapshot { Keywords = ["anime", "pirate", "friendship"] });
        Assert.Equal(1, features.ContentFitAffinity);
        Assert.Equal(1, features.ContentFitSupport);
        Assert.Equal(0, features.ContentFamilyMismatch);
        Assert.Equal(.123, features.TagSimilarity);
        Assert.Equal(41, features.ToVector().Length);
    }

    [Fact]
    public void RepresentationAndNarrativeAreIndependentForAnimatedDocumentaries()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++) profile.WatchedItems.Add(Watch(["Animation", "Documentary"], ["anime"]));
        var content = ContentFitProfile.Create(profile);
        var animated = new CandidateFeatures();
        content.Apply(animated, ["Documentary", "Animation"], ["anime"], null, []);
        Assert.Equal(0, animated.ContentFamilyMismatch);
        var liveAction = new CandidateFeatures();
        content.Apply(liveAction, ["Documentary"], ["live action"], null, []);
        Assert.Equal(.5, liveAction.ContentFamilyMismatch);
        var unknownRepresentation = new CandidateFeatures();
        content.Apply(unknownRepresentation, ["Documentary"], [], null, []);
        Assert.Equal(0, unknownRepresentation.ContentFamilyMismatch);
    }

    [Fact]
    public void NarrativeConfidenceDoesNotGuessTheRepresentationOfRealityOrDocumentary()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++) profile.WatchedItems.Add(Watch(["Reality"], []));
        var features = new CandidateFeatures();
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], ["anime"], null, []);
        Assert.Null(features.ContentFamilyMismatch);
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], ["anime"], null, [], new TitleMetadataSnapshot { SeriesType = "Scripted" });
        Assert.Equal(.5, features.ContentFamilyMismatch);
    }

    [Fact]
    public void StableKeywordIdsMatchTranslatedStoriesWithoutWeakeningLocalTextMatches()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        var row = Watch(["Adventure"], ["pirate", "friendship"]);
        row.TitleMetadata = new TitleMetadataSnapshot { Keywords = ["pirate", "friendship"], KeywordReferences = [new(42, "pirate"), new(43, "friendship")] };
        profile.WatchedItems.Add(row);
        var content = ContentFitProfile.Create(profile);
        var translated = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true };
        content.Apply(translated, ["Adventure"], ["pirata", "amistad"], null, new() { ["Adventure"] = 1 }, new TitleMetadataSnapshot
        {
            Keywords = ["pirata", "amistad"], KeywordReferences = [new(42, "pirata"), new(43, "amistad")]
        });
        Assert.Equal(1, translated.ContentFitAffinity);
        Assert.Equal(1, translated.ContentFitSupport);
        var local = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true };
        content.Apply(local, ["Adventure"], ["pirate", "friendship"], null, new() { ["Adventure"] = 1 }, new TitleMetadataSnapshot { KeywordReferences = [new(100, "unrelated")] });
        Assert.Equal(1, local.ContentFitAffinity);
        Assert.Equal(1, local.ContentFitSupport);
    }

    [Fact]
    public void GenericGenreFormatAndOperationalKeywordIdsCannotBecomeStoryEvidence()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        var row = Watch(["Animation"], ["anime", "based on manga", "Action", "Japanese", "JE Arr Tag: pirate"]);
        row.TitleMetadata = new TitleMetadataSnapshot { KeywordReferences = [new(1, "anime"), new(2, "based on manga"), new(3, "Action"), new(4, "Japanese"), new(5, "JE Arr Tag: pirate")] };
        profile.WatchedItems.Add(row);
        var features = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true };
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], [], null, new() { ["Animation"] = 1 }, row.TitleMetadata);
        Assert.Equal(0, features.ContentFitAffinity);
        Assert.Null(features.ContentFitSupport);
    }

    [Fact]
    public void NewestProviderSnapshotClearsObsoleteProviderKeywordsAcrossDuplicateHistoryRows()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        var first = Watch(["Animation"], []);
        first.TmdbId = 123;
        first.TitleMetadata = new TitleMetadataSnapshot { Keywords = ["pirate", "friendship"], FetchedAtUtc = DateTime.UtcNow.AddDays(-1) };
        var newer = Watch(["Animation"], []);
        newer.TmdbId = 123;
        newer.TitleMetadata = new TitleMetadataSnapshot { Keywords = [], FetchedAtUtc = DateTime.UtcNow };
        profile.WatchedItems.Add(first);
        profile.WatchedItems.Add(newer);
        Assert.Equal(0, ContentFitProfile.Create(profile).ScoreTags(["pirate", "friendship"]));
    }

    [Fact]
    public void TrueCrimeOnlyTasteDoesNotBecomeAGenericDocumentaryPreference()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++) profile.WatchedItems.Add(Watch(["Documentary"], ["true crime"]));
        var content = ContentFitProfile.Create(profile);
        var knownOtherTopic = new CandidateFeatures();
        content.Apply(knownOtherTopic, ["Documentary"], ["nature documentary"], null, []);
        Assert.Equal(.5, knownOtherTopic.ContentFamilyMismatch);
        var sameTopic = new CandidateFeatures();
        content.Apply(sameTopic, ["Documentary"], ["true crime"], null, []);
        Assert.Equal(0, sameTopic.ContentFamilyMismatch);
        var unknown = new CandidateFeatures();
        content.Apply(unknown, ["Documentary"], [], null, []);
        Assert.Equal(0, unknown.ContentFamilyMismatch);
    }

    [Fact]
    public void SecondaryDocumentaryTopicsAndHybridTopicsStaySupported()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++) profile.WatchedItems.Add(Watch(["Documentary"], ["true crime"]));
        profile.WatchedItems.Add(Watch(["Documentary"], ["nature documentary"]));
        var features = new CandidateFeatures();
        ContentFitProfile.Create(profile).Apply(features, ["Documentary"], ["nature documentary"], null, []);
        Assert.Equal(0, features.ContentFamilyMismatch);
        ContentFitProfile.Create(profile).Apply(features, ["Documentary"], ["nature documentary", "true crime"], null, []);
        Assert.Equal(0, features.ContentFamilyMismatch);
    }

    [Fact]
    public void MultipleTopicsOnOneTitleCannotInflateTopicCoverageAcrossUnknownHistory()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        profile.WatchedItems.Add(Watch(["Documentary"], ["true crime", "nature documentary", "historical documentary"]));
        for (var i = 0; i < 2; i++) profile.WatchedItems.Add(Watch(["Documentary"], []));
        var features = new CandidateFeatures();
        ContentFitProfile.Create(profile).Apply(features, ["Documentary"], ["sports documentary"], null, []);
        Assert.Equal(0, features.ContentFamilyMismatch);
    }

    [Fact]
    public void LikelyAnimationOriginHintsCanModerateButNeverBanARecommendation()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++)
        {
            var row = Watch(["Animation"], []);
            row.TitleMetadata = new TitleMetadataSnapshot { OriginalLanguage = "ja", OriginCountries = ["JP"] };
            profile.WatchedItems.Add(row);
        }

        var features = new CandidateFeatures { GenreSimilarity = 1, HasGenreEvidence = true };
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], ["western animation"], null, new() { ["Animation"] = 1 });
        Assert.Equal(.25, features.ContentFamilyMismatch);
        Assert.True(ScoringHelper.IsContentEligible(features));
        Assert.Equal(.6, ScoringHelper.ComputeContextualScore(.8, features, .1), 12);
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], [], null, new() { ["Animation"] = 1 });
        Assert.Null(features.ContentFamilyMismatch);
    }

    [Fact]
    public void ConfirmedAnimeHistoryDoesNotTurnProbableNonAnimeOriginIntoASelectionBan()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < 3; i++) profile.WatchedItems.Add(Watch(["Animation"], ["anime"]));
        var features = new CandidateFeatures();
        ContentFitProfile.Create(profile).Apply(features, ["Animation"], [], null, [], new TitleMetadataSnapshot { OriginalLanguage = "en", ProductionCountries = ["US"] });
        Assert.Equal(.25, features.ContentFamilyMismatch);
        Assert.True(ScoringHelper.IsContentEligible(features));
    }

    [Fact]
    public void SpecificTitleMatchIsNotDilutedByThousandsOfUnrelatedHistoryKeywords()
    {
        var profile = BattleProfile();
        for (var i = 0; i < 100; i++) profile.WatchedItems.Add(Watch(["Drama"], Enumerable.Range(0, 10).Select(j => $"unrelated {i} {j}").ToArray()));
        var content = ContentFitProfile.Create(profile);
        Assert.Equal(1, content.ScoreTags(BattleKeywords), 12);
        var union = PreferenceBuilder.BuildTagPreferenceSet(profile, []);
        Assert.True(SimilarityComputer.ComputeJaccardFromSets(new HashSet<string>(BattleKeywords), union) < .01);
    }

    [Fact]
    public void ScatteredKeywordsAcrossUnrelatedTitlesCannotBecomeOnePerfectMatch()
    {
        var profile = new UserWatchProfile();
        profile.WatchedItems.Add(Watch(["Action"], ["pirate", "revenge"]));
        profile.WatchedItems.Add(Watch(["Drama"], ["friendship", "courtroom"]));
        profile.WatchedItems.Add(Watch(["Fantasy"], ["magic", "school"]));
        Assert.InRange(ContentFitProfile.Create(profile).ScoreTags(["pirate", "friendship", "magic"]), 0, .5);
    }

    [Fact]
    public void RepeatedEpisodesAndDuplicateProviderTitlesCannotAmplifySemanticEvidence()
    {
        var profile = BattleProfile();
        var episode = profile.WatchedItems[0];
        episode.SeriesId = Guid.NewGuid();
        episode.SeriesTmdbId = 123;
        var first = ContentFitProfile.Create(profile).ScoreTags(["anime", "shounen", "friendship"]);
        for (var i = 0; i < 500; i++)
        {
            var copy = Watch(["Animation", "Action"], BattleKeywords);
            copy.SeriesId = episode.SeriesId;
            copy.SeriesTmdbId = episode.SeriesTmdbId;
            profile.WatchedItems.Add(copy);
        }

        Assert.Equal(first, ContentFitProfile.Create(profile).ScoreTags(["anime", "shounen", "friendship"]), 12);
    }

    [Fact]
    public void GenericAnimeActionAndOperationalTagsDoNotManufactureThematicSimilarity()
    {
        var content = ContentFitProfile.Create(BattleProfile());
        Assert.Equal(0, content.ScoreTags(["anime", "animation", "action"]));
        Assert.Equal(0, content.ScoreTags(["JE Arr Tag: german", "German", "1080p", "Sonarr: shounen", "Radarr: pirate"]));
        Assert.Equal(0, content.ScoreTags(null));
        Assert.Equal(0, ContentFitProfile.Empty.ScoreTags(BattleKeywords));
    }

    [Theory]
    [InlineData("horror", "thriller")]
    [InlineData("crime", "mystery")]
    [InlineData("documentary", "history")]
    [InlineData("music", "western")]
    [InlineData("Krimi", "Mystery")]
    [InlineData("Sci-Fi", "Thriller")]
    [InlineData("ＮＥＷＳ", "Reality")]
    [InlineData("Action & Abenteuer", "War & Politics")]
    [InlineData("Kids", "Talk")]
    public void PureGenreTagPairsCannotCreateThematicAffinityOrNarrativeConfidence(string first, string second)
        => AssertGenreOnlyTagsAreNeutral([first, second]);

    [Fact]
    public void EveryEmbeddedGenreAliasRemainsBroadEvidenceInThematicScoring()
    {
        var assembly = typeof(GenreNormalizer).Assembly;
        foreach (var stream in assembly.GetManifestResourceNames().Where(name => name.Contains(".i18n.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)).Select(assembly.GetManifestResourceStream))
        {
            using (stream)
            {
                Assert.NotNull(stream);
                using var document = JsonDocument.Parse(stream);
                foreach (var aliases in document.RootElement.EnumerateObject().Where(entry => entry.Name.StartsWith("genre.", StringComparison.Ordinal)).Select(entry => entry.Value.GetString()))
                {
                    Assert.NotNull(aliases);
                    foreach (var alias in aliases.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        AssertGenreOnlyTagsAreNeutral([alias, "thriller"]);
                }
            }
        }
    }

    [Fact]
    public void RegisteredProviderGenreAliasesRemainBroadInsteadOfBecomingStoryKeywords()
    {
        var label = "localized horror " + Guid.NewGuid().ToString("N");
        _ = ContentFitProfile.Create(BattleProfile()); // Initialize before a new provider catalogue alias arrives.
        Assert.True(GenreNormalizer.RegisterProviderGenre(27, label));
        AssertGenreOnlyTagsAreNeutral([label, "thriller"]);
    }

    [Fact]
    public void RealStoryKeywordsStillSupportGenreTagsWithoutReplacingRawJaccard()
    {
        string[] tags = ["horror", "thriller", "haunted house", "ghost"];
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(["Horror", "Thriller"], tags)] };
        var features = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true, TagSimilarity = 1 };
        ContentFitProfile.Create(profile).Apply(features, ["Horror", "Thriller"], tags, null, new() { ["Horror"] = 1, ["Thriller"] = 1 });
        Assert.Equal(1, features.ContentFitAffinity);
        Assert.Equal(1, features.ContentFitSupport);
        Assert.Equal(1, features.TagSimilarity);
    }

    [Theory]
    [InlineData("Spanish", "French")]
    [InlineData("spa", "fra")]
    [InlineData("französisch", "español")]
    [InlineData("Fransızca", "İspanyolca")]
    [InlineData("fr-FR", "es-ES")]
    [InlineData("French (Forced)", "Spanish (SDH)")]
    [InlineData("zh_Hans_CN", "sr_Latn_RS")]
    [InlineData("es_419", "en_Latn_US")]
    public void LanguageOnlyTagsCannotCreateNarrativeConfidence(string first, string second)
        => AssertLanguageOnlyTagsAreNeutral([first, second]);

    [Fact]
    public void EveryCuratedLanguageNameAndExonymRemainsTechnicalEvidence()
    {
        foreach (var names in LanguageIdentity.LanguageExonyms)
            foreach (var name in names.OfType<string>())
                AssertLanguageOnlyTagsAreNeutral([name, "French"]);
    }

    [Fact]
    public void LanguageWordsInsideActualStorySettingsAreNotRemoved()
    {
        string[] tags = ["French revolution", "German occupation", "Spanish civil war"];
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(["Drama"], tags)] };
        var features = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true, TagSimilarity = 1 };
        ContentFitProfile.Create(profile).Apply(features, ["Drama"], tags, null, new() { ["Drama"] = 1 });
        Assert.Equal(1, features.ContentFitAffinity);
        Assert.Equal(1, features.ContentFitSupport);
        Assert.Equal(tags.Select(tag => tag.ToLowerInvariant()).Order(), ContentFitProfile.Keywords(tags).Order());
    }

    [Fact]
    public void HyphenatedAndSpacedStoryKeywordsRemainTheSameNarrativeEvidence()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(["Thriller"], ["cat-and-mouse", "pursuit"])] };
        var content = ContentFitProfile.Create(profile);
        Assert.Equal(new[] { "cat and mouse" }, ContentFitProfile.Keywords(["cat-and-mouse"]));
        Assert.Equal(ContentFitProfile.Keywords(["cat and mouse"]), ContentFitProfile.Keywords(["cat-and-mouse"]));
        Assert.Equal(1, content.ScoreTags(["cat and mouse", "pursuit"]));
        var features = new CandidateFeatures { HasGenreEvidence = true, GenreSimilarity = .7 };
        content.Apply(features, ["Thriller"], ["cat-and-mouse", "pursuit"], null, new() { ["Thriller"] = 1 });
        Assert.Equal(1, features.ContentFitSupport);
    }

    private static void AssertLanguageOnlyTagsAreNeutral(string[] tags)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(["Drama"], tags)] };
        var features = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true, TagSimilarity = 1 };
        var content = ContentFitProfile.Create(profile);
        content.Apply(features, ["Drama"], tags, null, new() { ["Drama"] = 1 });
        Assert.Equal(0, content.ScoreTags(tags));
        Assert.Null(features.ContentFitSupport);
        Assert.Equal(.7, features.GenreSimilarity);
        Assert.Equal(1, features.TagSimilarity);
        Assert.Equal(.4, ScoringHelper.ComputeContextualScore(.4, features, .1));
    }

    private static void AssertGenreOnlyTagsAreNeutral(string[] tags)
    {
        var genres = GenreNormalizer.Set(tags).ToArray();
        var preferences = genres.ToDictionary(genre => genre, _ => 1.0);
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(genres, tags)] };
        var content = ContentFitProfile.Create(profile);
        var features = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = true, TagSimilarity = 1 };
        content.Apply(features, genres, tags, null, preferences);
        Assert.Equal(0, content.ScoreTags(tags));
        Assert.Equal(0, features.ContentFitAffinity);
        Assert.Null(features.ContentFitSupport);
        Assert.Equal(.7, features.GenreSimilarity);
        Assert.Equal(1, features.TagSimilarity);
        Assert.Equal(.4, ScoringHelper.ComputeContextualScore(.4, features, .1));
    }

    [Fact]
    public void KeywordNormalizationRetainsStorySettingsAndNormalizesAudienceVariants()
    {
        Assert.Equal(new[] { "adult animation", "german occupation", "preschool", "shounen" },
            ContentFitProfile.Keywords([" Shōnen ", "shonen", "adult cartoon", "pre-school", "german occupation", "german"]).Order());
    }

    [Fact]
    public void KnownEmptyParentMetadataCannotResurrectCombinedEpisodeTags()
    {
        var profile = BattleProfile();
        var row = profile.WatchedItems[0];
        row.SeriesId = Guid.NewGuid();
        row.OwnTags = [];
        row.SeriesTags = BattleKeywords;
        var content = ContentFitProfile.Create(profile, id => id == row.SeriesId ? [] : null);
        Assert.Equal(0, content.ScoreTags(BattleKeywords));
        Assert.Equal(.7, content.AdjustGenreSimilarity(.7, ["Animation", "Action"], [], "FSK-0", BattleGenres));
    }

    [Theory]
    [InlineData("FSK-0")]
    [InlineData("FSK 6")]
    [InlineData("G")]
    [InlineData("TV-Y")]
    public void AllAgesAnimationGetsOnlyAWeakHintForAnOlderAnimeViewer(string rating)
    {
        var content = ContentFitProfile.Create(BattleProfile());
        var family = content.AdjustGenreSimilarity(.7, ["Animation", "Action & Adventure"], ["JE Arr Tag: german"], rating, BattleGenres);
        var battle = content.AdjustGenreSimilarity(.7, ["Animation", "Action & Adventure"], ["anime", "shounen"], rating, BattleGenres);
        Assert.Equal(.35, family, 12);
        Assert.Equal(.7 + (.3 * .5 * content.ScoreTags(["anime", "shounen"])), battle, 12);
        Assert.True(content.ScoreTags(BattleKeywords) > content.ScoreTags(["JE Arr Tag: german"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("FSK-12")]
    [InlineData("FSK-99")]
    public void MissingOrNonAllAgesRatingsCannotInferPreschoolContent(string? rating)
    {
        var content = ContentFitProfile.Create(BattleProfile());
        Assert.Equal(.7, content.AdjustGenreSimilarity(.7, ["Animation", "Action"], [], rating, BattleGenres));
    }

    [Fact]
    public void FamilyChildrenAndUnknownUsersAreNotSubjectToAnAgeBan()
    {
        var content = ContentFitProfile.Create(BattleProfile());
        Assert.Equal(.7, content.AdjustGenreSimilarity(.7, ["Animation"], [], "FSK-0", new() { ["Animation"] = 1, ["Family"] = 1 }));
        Assert.Equal(.7, content.AdjustGenreSimilarity(.7, ["Animation"], [], "FSK-0", new() { ["Animation"] = 1, ["Kids"] = 1 }));
        Assert.Equal(.7, ContentFitProfile.Empty.AdjustGenreSimilarity(.7, ["Animation"], [], "FSK-0", BattleGenres));
        var generic = new UserWatchProfile();
        generic.WatchedItems.Add(Watch(["Animation", "Action"], ["anime"]));
        Assert.Equal(.7, ContentFitProfile.Create(generic).AdjustGenreSimilarity(.7, ["Animation"], [], "FSK-0", BattleGenres));
    }

    [Fact]
    public void SpecificallyMatchingAllAgesThemesOverrideTheAmbiguousRatingHint()
    {
        var content = ContentFitProfile.Create(BattleProfile());
        var matching = BattleKeywords.Where(tag => tag is not "anime" and not "shounen").ToArray();
        Assert.Equal(content.AdjustGenreSimilarity(.7, ["Animation", "Family"], matching, null, BattleGenres), content.AdjustGenreSimilarity(.7, ["Animation", "Family"], matching, "FSK-0", BattleGenres));
        Assert.Equal(.32 * .5, content.AdjustGenreSimilarity(.32, ["Family", "Fantasy", "Comedy"], [], "FSK-6", BattleGenres), 12);
        Assert.Equal(.32, content.AdjustGenreSimilarity(.32, ["Fantasy", "Comedy"], [], "FSK-6", BattleGenres));
    }

    [Fact]
    public void ExplicitKidsGenreKeepsItsExistingPriorRatherThanBeingPenalizedTwice()
    {
        var content = ContentFitProfile.Create(BattleProfile());
        var prior = SimilarityComputer.ComputeGenreSimilarity(["Animation", "Kids"], BattleGenres);
        Assert.Equal(prior, content.AdjustGenreSimilarity(prior, ["Animation", "Kids"], ["preschool"], "FSK-0", BattleGenres));
    }

    [Fact]
    public void ContextIsDetachedAndUnplayedSuggestionsCannotBecomePreferenceEvidence()
    {
        var profile = BattleProfile();
        var original = profile.WatchedItems[0].Tags;
        var content = ContentFitProfile.Create(profile);
        profile.WatchedItems.Clear();
        Assert.Equal(1, content.ScoreTags(BattleKeywords), 12);
        Assert.Equal(BattleKeywords, original);
        profile.WatchedItems.Add(new WatchedItemInfo { ItemId = Guid.NewGuid(), Genres = ["Animation"], Tags = BattleKeywords });
        Assert.Equal(0, ContentFitProfile.Create(profile).ScoreTags(BattleKeywords));
    }

    [Fact]
    public void TargetSeriesExclusionCoversEpisodesAndCannotCreateItsOwnTagPreference()
    {
        var profile = BattleProfile();
        var row = profile.WatchedItems[0];
        row.SeriesId = Guid.NewGuid();
        var excluded = new HashSet<Guid> { row.SeriesId.Value };
        Assert.Equal(0, ContentFitProfile.Create(profile, excluded: excluded).ScoreTags(BattleKeywords));
        Assert.Equal(.7, ContentFitProfile.Create(profile, excluded: excluded).AdjustGenreSimilarity(.7, ["Animation"], [], "FSK-0", BattleGenres));
    }

    [Fact]
    public void MalformedProviderUnicodeCannotAbortOtherUsersOrManufactureTags()
    {
        var keywords = ContentFitProfile.Keywords(["\ud800", "friendship", "pirate"]);
        Assert.Equal(new[] { "friendship", "pirate" }, keywords.Order());
    }

    [Fact]
    public void FamilyAliasesRemainNeutralAndTheCallerPreferenceVectorIsNotRewritten()
    {
        var preferences = new Dictionary<string, double> { ["Animation"] = 1, ["Familie"] = 1 };
        Assert.Equal(.7, ContentFitProfile.Create(BattleProfile()).AdjustGenreSimilarity(.7, ["Animation"], [], "FSK-0", preferences));
        Assert.Contains("Familie", preferences.Keys);
        Assert.DoesNotContain("Family", preferences.Keys);
    }

    [Fact]
    public void CharactersWhoAreChildrenDoNotTurnAdultStoriesIntoChildrensProgramming()
    {
        var content = ContentFitProfile.Create(BattleProfile());
        Assert.Equal(.7, content.AdjustGenreSimilarity(.7, ["Animation", "Horror"], ["kids", "children", "kinder"], "FSK-16", BattleGenres));
        Assert.DoesNotContain("preschool", ContentFitProfile.Keywords(["children", "kids"]));
    }

    [Fact]
    public void SemanticAffinityIsDeterministicAcrossProviderAndHistoryOrdering()
    {
        var profile = BattleProfile();
        profile.WatchedItems.Add(Watch(["Animation"], ["anime", "wizard", "friendship", "magic"]));
        var first = ContentFitProfile.Create(profile).ScoreTags(["pirate", "anime", "friendship", "magic"]);
        foreach (var row in profile.WatchedItems) row.Tags = row.Tags.Reverse().ToArray();
        profile.WatchedItems = [.. profile.WatchedItems.Reverse()];
        var second = ContentFitProfile.Create(profile).ScoreTags(["magic", "friendship", "anime", "pirate"]);
        Assert.Equal(first, second);
    }

    [Fact]
    public void WeightedFullMatchKeepsExactUnitAffinityAndSupportWithoutChangingIterationOrder()
    {
        string[] terms = ["pirate", "friendship", "sea", "ship", "ocean", "super power", "shounen", "anime", "treasure"];
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [Watch(["Animation", "Action"], terms)] };
        for (var i = 0; i < 75; i++) profile.WatchedItems.Add(Watch(["Drama"], ["friendship", "sea", "unrelated " + i]));
        var content = ContentFitProfile.Create(profile);
        var first = new CandidateFeatures { GenreSimilarity = .7, TagSimilarity = .003 };
        content.Apply(first, ["Animation", "Action"], terms.Reverse(), "FSK-12", BattleGenres);
        Assert.Equal(1, content.ScoreTags(terms.Reverse()));
        Assert.Equal(1, first.ContentFitSupport);
        foreach (var row in profile.WatchedItems) row.Tags = row.Tags.Reverse().ToArray();
        profile.WatchedItems = [.. profile.WatchedItems.Reverse()];
        var reordered = ContentFitProfile.Create(profile);
        var second = new CandidateFeatures { GenreSimilarity = .7, TagSimilarity = .003 };
        reordered.Apply(second, ["Animation", "Action"], terms, "FSK-12", BattleGenres);
        Assert.Equal(first.GenreSimilarity, second.GenreSimilarity);
        Assert.Equal(first.ContentFitSupport, second.ContentFitSupport);
        Assert.Equal(first.ToVector(), second.ToVector());
        Assert.Equal(content.ScoreTags(["pirate", "sea", "friendship", "new theme"]), reordered.ScoreTags(["new theme", "friendship", "sea", "pirate"]));
    }

    [Fact]
    public void V6ThematicInputsDoNotRescaleTheStandardizedRawTagCoordinate()
    {
        var profile = BattleProfile();
        for (var i = 0; i < 100; i++) profile.WatchedItems.Add(Watch(["Drama"], Enumerable.Range(0, 10).Select(j => $"unrelated {i} {j}").ToArray()));
        var content = ContentFitProfile.Create(profile);
        var old = SimilarityComputer.ComputeJaccardFromSets(new HashSet<string>(BattleKeywords), PreferenceBuilder.BuildTagPreferenceSet(profile, []));
        var features = new CandidateFeatures { GenreSimilarity = .7, TagSimilarity = old };
        content.Apply(features, ["Animation", "Action"], BattleKeywords, "FSK-12", BattleGenres);
        Assert.Equal(old, features.TagSimilarity);
        Assert.Equal(1, content.ScoreTags(BattleKeywords), 12);
        Assert.True(features.GenreSimilarity > .7);
        var means = new double[CandidateFeatures.FeatureCount];
        var deviations = Enumerable.Repeat(1.0, CandidateFeatures.FeatureCount).ToArray();
        var weights = new double[CandidateFeatures.FeatureCount];
        means[(int)FeatureIndex.TagSimilarity] = old;
        deviations[(int)FeatureIndex.TagSimilarity] = .01;
        weights[(int)FeatureIndex.TagSimilarity] = .2;
        var predictor = new LearnedScoringStrategy.PredictionSnapshot(new LearnedScoringStrategy.WeightsData
        {
            Version = 6, TrainingGeneration = 1, Weights = weights, Bias = .5, FeatureMeans = means, FeatureStdDevs = deviations
        });
        Assert.True(LearnedScoringStrategy.IsValidParameters(predictor.ExportParameters()));
        Assert.Equal(.5, predictor.Score(features), 12);
    }

    [Fact]
    public void MissingTitleIdentitiesCannotMergeUnrelatedRowsIntoACoherentMatch()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems = [new WatchedItemInfo { Played = true, Tags = ["pirate"] }, new WatchedItemInfo { Played = true, Tags = ["friendship"] }]
        };
        Assert.Equal(0, ContentFitProfile.Create(profile).ScoreTags(["pirate", "friendship"]));
    }

    [Fact]
    public void MetadataMemoCannotRecycleFutureOrExcludedPreferenceAggregates()
    {
        var profile = BattleProfile();
        var source = new ContentFitProfile.SourceCache();
        Assert.Equal(1, ContentFitProfile.Create(profile, sourceCache: source).ScoreTags(BattleKeywords), 12);
        Assert.Equal(0, ContentFitProfile.Create(profile, excluded: new HashSet<Guid> { profile.WatchedItems[0].ItemId }, sourceCache: source).ScoreTags(BattleKeywords));
        Assert.Equal(0, ContentFitProfile.Create(new UserWatchProfile { UserId = profile.UserId }, sourceCache: source).ScoreTags(BattleKeywords));
        profile.WatchedItems[0].Tags = ["wizard", "magic"];
        Assert.Equal(0, ContentFitProfile.Create(profile, sourceCache: source).ScoreTags(BattleKeywords));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EpisodeProviderIdsCannotMergeWithAnUnrelatedMovie(bool validParent)
    {
        var movie = Watch(["Adventure"], ["pirate"]);
        movie.ItemType = "Movie";
        movie.TmdbId = 123;
        var episode = Watch(["Animation"], ["friendship"]);
        episode.ItemType = "Episode";
        episode.TmdbId = 123;
        if (validParent)
        {
            episode.SeriesId = Guid.NewGuid();
            episode.SeriesTmdbId = 123;
        }

        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [movie, episode] };
        Assert.Equal(2.0 / 3.0, ContentFitProfile.Create(profile).ScoreTags(["pirate", "friendship"]), 12);
    }

    [Theory]
    [InlineData("German", "1080p")]
    [InlineData("Shōnen", "friendship")]
    public void SemanticEnrichmentKeepsTheExactRawTagCoordinateAndFittedStatistics(string first, string second)
    {
        string[] raw = [first, second];
        var profile = BattleProfile();
        profile.WatchedItems.Add(Watch(["Animation"], raw));
        var preferences = PreferenceBuilder.BuildTagPreferenceSet(profile, []);
        var original = SimilarityComputer.ComputeJaccardFromSets(new HashSet<string>(raw, StringComparer.OrdinalIgnoreCase), preferences);
        var features = new CandidateFeatures { GenreSimilarity = .7, TagSimilarity = original };
        var means = new double[CandidateFeatures.FeatureCount];
        var deviations = Enumerable.Repeat(1.0, CandidateFeatures.FeatureCount).ToArray();
        var weights = new double[CandidateFeatures.FeatureCount];
        means[(int)FeatureIndex.TagSimilarity] = original;
        deviations[(int)FeatureIndex.TagSimilarity] = .01;
        weights[(int)FeatureIndex.TagSimilarity] = .2;
        var predictor = new LearnedScoringStrategy.PredictionSnapshot(new LearnedScoringStrategy.WeightsData
        {
            Version = 6, TrainingGeneration = 1, Weights = weights, Bias = .5, FeatureMeans = means, FeatureStdDevs = deviations
        });
        Assert.True(LearnedScoringStrategy.IsValidParameters(predictor.ExportParameters()));
        var before = predictor.Score(features);
        ContentFitProfile.Create(profile).Apply(features, ["Animation", "Action"], raw, "FSK-12", BattleGenres);
        Assert.Equal(original, features.TagSimilarity);
        Assert.Equal(before, predictor.Score(features));
        Assert.Equal(.5, before, 12);
    }

    private static UserWatchProfile BattleProfile()
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        profile.WatchedItems.Add(Watch(["Animation", "Action"], BattleKeywords));
        return profile;
    }

    private static WatchedItemInfo Watch(string[] genres, string[] tags)
        => new() { ItemId = Guid.NewGuid(), Played = true, PlayCount = 1, Genres = genres, Tags = tags };
}
