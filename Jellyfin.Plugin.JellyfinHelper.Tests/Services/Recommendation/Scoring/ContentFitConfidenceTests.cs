using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Content confidence acts once in final score space, without changing v5 predictors or manufacturing outcomes.</summary>
public sealed class ContentFitConfidenceTests
{
    private static readonly Dictionary<string, double> Preferences = new() { ["Animation"] = 1, ["Adventure"] = 1, ["Family"] = .1 };
    private static readonly string[] Story = ["anime", "shounen", "pirate", "friendship", "sea", "ship"];

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(0.8)]
    [InlineData(1.0)]
    public void ContextIsBoundedAndNeutralLegacyInputsPreserveTheExactScore(double raw)
    {
        var f = new CandidateFeatures { GenreSimilarity = .7 };
        Assert.Equal(raw, ScoringHelper.ComputeContextualScore(raw, f, .1));
        foreach (var support in new[] { 0.0, .4, 1.0 })
        foreach (var mismatch in new[] { 0.0, .25, .5 })
        {
            f.ContentFitSupport = support;
            f.AudienceMismatch = mismatch;
            var score = ScoringHelper.ComputeContextualScore(raw, f, .1);
            Assert.InRange(score, 0, 1);
            Assert.Equal(raw, ScoringHelper.RecoverPreContentScore(score, f)!.Value, 12);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedConfidenceDoesNotChangeAnyOfThe38RawOrImputedModelCoordinates(bool external)
    {
        var f = new CandidateFeatures { IsExternalCandidate = external, GenreSimilarity = .7, TagSimilarity = .004, LanguageAffinity = 1, IsSeries = true };
        var means = Enumerable.Repeat(.3, 38).ToArray();
        var raw = f.ToVector();
        var imputed = f.ToModelVector(means);
        var predictor = Predictor();
        using var neural = new NeuralScoringStrategy();
        var snapshot = neural.CapturePredictionSnapshot()!;
        var learnedScore = predictor.Score(f);
        var neuralScore = snapshot.Score(f);
        f.ContentFitSupport = .8;
        f.AudienceMismatch = .25;
        f.CollectionContinuationSupport = .75;
        Assert.Equal(raw, f.ToVector());
        Assert.Equal(imputed, f.ToModelVector(means));
        Assert.Equal(38, f.ToVector().Length);
        Assert.Equal(learnedScore, predictor.Score(f));
        Assert.Equal(neuralScore, snapshot.Score(f));
        Assert.Equal(raw, f.WithoutContentFit().ToVector());
    }

    [Theory]
    [InlineData(.3, .12)]
    [InlineData(.75, .12)]
    [InlineData(.75, .4)]
    public void ContextCommutesWithEveryAllowedMixtureForConsistentIndependentAdmission(double alpha, double beta)
    {
        var f = new CandidateFeatures { GenreSimilarity = .2, ContentFitSupport = .8, AudienceMismatch = .25 };
        var mix = new AdmissionMixture(alpha * (1 - beta), alpha * beta);
        var scores = new AdmissionScores(.4, .8, .2, .5);
        var contextual = new AdmissionScores(ScoringHelper.ComputeContextualScore(scores.Heuristic, f, .1),
            ScoringHelper.ComputeContextualScore(scores.Learned, f, .1), ScoringHelper.ComputeContextualScore(scores.Neural, f, .1), 0);
        Assert.Equal(ScoringHelper.ComputeContextualScore(mix.Score(scores), f, .1), mix.Score(contextual), 12);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedAndLegacyEnsemblePathsApplyContextOnlyOnceAndExplainTheActualScore(bool pinned)
    {
        var learned = new LearnedScoringStrategy();
        var heuristic = new HeuristicScoringStrategy(genrePenaltyFloor: 1);
        using var ensemble = new EnsembleScoringStrategy(learned, heuristic);
        var f = new CandidateFeatures { GenreSimilarity = .7, CombinedCriticScore = .8, ContentFitSupport = .8, AudienceMismatch = .25 };
        if (pinned)
        {
            typeof(EnsembleScoringStrategy).GetField("_acceptedPrediction", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(ensemble, new AcceptedPrediction(heuristic, Predictor(), null, new AdmissionMixture(.66, 0), 0));
        }

        var raw = ensemble.Score(f.WithoutContentFit());
        var expected = (raw + ((1 - raw) * .8 * .5)) * .75;
        Assert.Equal(expected, ensemble.Score(f), 12);
        var explanation = ensemble.ScoreWithExplanation(f);
        Assert.Equal(ensemble.Score(f), explanation.FinalScore, 12);
        Assert.Equal(expected - raw, explanation.ContentFitContribution!.Value, 12);
        Assert.Equal(ensemble.ScoreWithOffset(f, .1), ensemble.ScoreWithExplanationAndOffset(f, .1).FinalScore, 12);
        var copy = JsonSerializer.Deserialize<CandidateFeatures>(JsonSerializer.Serialize(f))!;
        Assert.Equal(expected, ensemble.Score(copy), 12);
        Assert.Equal(f.ToVector(), copy.ToVector());
    }

    [Fact]
    public void BroadFormatLanguageOrScatteredKeywordsCannotSupplyScoreSpaceSupport()
    {
        var profile = new UserWatchProfile
        {
            WatchedItems = [Watch(["anime", "shounen", "based on manga", "super power"]), Watch(["pirate"]), Watch(["friendship"])]
        };
        var f = Features();
        ContentFitProfile.Create(profile).Apply(f, ["Animation", "Adventure"], ["anime", "shounen", "based on manga", "super power", "pirate", "friendship", "German"], null, Preferences);
        Assert.Null(f.ContentFitSupport);
        Assert.Null(f.AudienceMismatch);
        Assert.Equal(.4, ScoringHelper.ComputeContextualScore(.4, f, .1));
    }

    [Fact]
    public void RicherCandidateKeywordsRetainCoherentPriorThemesWithoutDilutionOrTechnicalLabels()
    {
        var profile = new UserWatchProfile { WatchedItems = [Watch(Story)] };
        var candidate = Story.Concat(Enumerable.Range(0, 20).Select(i => $"new setting {i}")).ToArray();
        var f = Features();
        var content = ContentFitProfile.Create(profile);
        content.Apply(f, ["Animation", "Adventure"], candidate, "FSK-12", Preferences);
        Assert.True(content.ScoreTags(candidate) < .5);
        Assert.Equal(1, f.ContentFitSupport);
        Assert.Null(f.AudienceMismatch);
        Assert.Equal(.575, ScoringHelper.ComputeContextualScore(.15, f, .1), 12);
        Assert.Equal(.003, f.TagSimilarity);
    }

    [Fact]
    public void PositiveStoryEvidenceCanOvercomeAWeakIncumbentWithoutBlacklistingAllAgesTitles()
    {
        var content = ContentFitProfile.Create(new UserWatchProfile { WatchedItems = [Watch(Story)] });
        var story = Features();
        var generic = Features();
        content.Apply(story, ["Animation", "Adventure"], Story, "FSK-12", Preferences);
        content.Apply(generic, ["Animation", "Adventure"], ["JE Arr Tag: german"], "FSK-0", Preferences);
        Assert.Equal(.25, generic.AudienceMismatch);
        Assert.Null(generic.ContentFitSupport);
        var storyScore = ScoringHelper.ComputeContextualScore(.15, story, .1);
        var genericScore = ScoringHelper.ComputeContextualScore(.65, generic, .1);
        Assert.True(storyScore > genericScore);
        Assert.InRange(genericScore, .1, .65);
        Assert.True(generic.GenreSimilarity > .3); // The old genre threshold alone would not moderate the whole ensemble.
    }

    [Fact]
    public void DislikedPartialFutureOrSelfExcludedTitlesCannotBecomePositiveContentReferences()
    {
        var disliked = Watch(Story);
        disliked.UserRating = 2;
        var partial = Watch(Story);
        partial.Played = false;
        var profile = new UserWatchProfile { WatchedItems = [disliked, partial] };
        var f = Features();
        ContentFitProfile.Create(profile).Apply(f, ["Animation", "Adventure"], Story, "FSK-12", Preferences);
        Assert.Null(f.ContentFitSupport);
        Assert.Equal(0, ContentFitProfile.Create(profile).ScoreTags(Story));
        var watched = Watch(Story);
        profile.WatchedItems.Add(watched);
        ContentFitProfile.Create(profile, excluded: new HashSet<Guid> { watched.ItemId }).Apply(f, ["Animation", "Adventure"], Story, "FSK-12", Preferences);
        Assert.Null(f.ContentFitSupport);
        var historical = new UserWatchProfile { UserId = profile.UserId, WatchedItems = [disliked, partial] };
        ContentFitProfile.Create(historical).Apply(f, ["Animation", "Adventure"], Story, "FSK-12", Preferences);
        Assert.Null(f.ContentFitSupport);
    }

    [Fact]
    public void UnknownGenresAndMalformedContextAreNeutralAndInvalidPredictionsStayInvalid()
    {
        var f = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = false, ContentFitSupport = 1, AudienceMismatch = 1 };
        Assert.Equal(.4, ScoringHelper.ComputeContextualScore(.4, f, .1));
        f.HasGenreEvidence = true;
        f.ContentFitSupport = double.NaN;
        f.AudienceMismatch = double.PositiveInfinity;
        Assert.Equal(.4, ScoringHelper.ComputeContextualScore(.4, f, .1));
        Assert.True(double.IsNaN(ScoringHelper.ComputeContextualScore(double.NaN, f, .1)));
    }

    [Fact]
    public void ConfirmedContinuationWorksWithUnknownGenresAndOverridesOnlyTheAmbiguousAudienceHint()
    {
        var f = new CandidateFeatures { GenreSimilarity = .7, HasGenreEvidence = false, CollectionContinuationSupport = .75 };
        Assert.Equal(.625, ScoringHelper.ComputeContextualScore(.4, f, .1), 12);
        f.HasGenreEvidence = true;
        f.AudienceMismatch = .25;
        Assert.Equal(.625, ScoringHelper.ComputeContextualScore(.4, f, .1), 12);
        f.AudienceMismatch = .5;
        Assert.Equal(.3125, ScoringHelper.ComputeContextualScore(.4, f, .1), 12);
        var vector = f.ToVector();
        Assert.Null(f.WithoutSignal(FeatureIndex.CollectionProgressionBoost).CollectionContinuationSupport);
        Assert.Equal(vector, f.ToVector());
    }

    private static CandidateFeatures Features() => new() { GenreSimilarity = .8, TagSimilarity = .003 };

    private static WatchedItemInfo Watch(string[] tags) => new() { ItemId = Guid.NewGuid(), Played = true, PlayCount = 1, Genres = ["Animation", "Adventure"], Tags = tags };

    private static LearnedScoringStrategy.PredictionSnapshot Predictor()
    {
        var weights = new double[38];
        weights[(int)FeatureIndex.GenreSimilarity] = .03;
        weights[(int)FeatureIndex.TagSimilarity] = .2;
        return new LearnedScoringStrategy.PredictionSnapshot(new LearnedScoringStrategy.WeightsData { Version = 5, TrainingGeneration = 1, Weights = weights, Bias = .6 });
    }
}
