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
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.Contains(".i18n.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var document = JsonDocument.Parse(stream);
            foreach (var entry in document.RootElement.EnumerateObject().Where(entry => entry.Name.StartsWith("genre.", StringComparison.Ordinal)))
                foreach (var alias in entry.Value.GetString()!.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    AssertGenreOnlyTagsAreNeutral([alias, "thriller"]);
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
