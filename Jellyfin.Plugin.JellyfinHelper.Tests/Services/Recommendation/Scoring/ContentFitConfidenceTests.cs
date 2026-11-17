using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Shared v6 thematic inputs and score-space confidence remain distinct, immutable and outcome-independent.</summary>
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

    [Fact]
    public void TinyPositiveContextIsNotMistakenForTheExactNeutralSentinel()
    {
        var f = new CandidateFeatures { GenreSimilarity = .7, ContentFitSupport = 1e-12 };
        Assert.Equal(5e-13, ScoringHelper.ComputeContextualScore(0, f, .1));
        f.ContentFitSupport = null;
        f.AudienceMismatch = 1e-12;
        Assert.Equal(1 - 1e-12, ScoringHelper.ComputeContextualScore(1, f, .1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void V6ThemeCoordinatesAreExplicitAndCollectionContinuationDoesNotBecomeAnExtraModelInput(bool external)
    {
        var f = new CandidateFeatures { IsExternalCandidate = external, GenreSimilarity = .7, TagSimilarity = .004, LanguageAffinity = 1, IsSeries = true };
        var means = Enumerable.Repeat(.3, CandidateFeatures.FeatureCount).ToArray();
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
        Assert.Equal(raw.Take((int)FeatureIndex.ContentFitAffinity), f.ToVector().Take((int)FeatureIndex.ContentFitAffinity));
        Assert.Equal(imputed.Take((int)FeatureIndex.ContentFitAffinity), f.ToModelVector(means).Take((int)FeatureIndex.ContentFitAffinity));
        Assert.Equal(.8, f.ToVector()[(int)FeatureIndex.ContentFitSupport]);
        Assert.Equal(.25, f.ToVector()[(int)FeatureIndex.AudienceMismatch]);
        Assert.Equal(41, f.ToVector().Length);
        Assert.Equal(learnedScore, predictor.Score(f));
        Assert.NotEqual(neuralScore, snapshot.Score(f));
        var withoutContinuation = f.Copy();
        withoutContinuation.CollectionContinuationSupport = null;
        Assert.Equal(snapshot.Score(f), snapshot.Score(withoutContinuation));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContextualMarginalsKeepBothPredictorsPinnedWhenLiveCoordinatesChangeAfterTheFullScore(bool external)
    {
        var learned = LearnedScoringStrategy.FromParameters(Predictor().ExportParameters())!;
        using var initializer = new NeuralScoringStrategy();
        var parameters = initializer.CapturePredictionSnapshot()!.ExportParameters();
        parameters.BiasOutput = -.5;
        using var neural = NeuralScoringStrategy.FromParameters(parameters)!;
        using var ensemble = new EnsembleScoringStrategy(learned, new HeuristicScoringStrategy(1), neural, ownsNeural: false);
        typeof(EnsembleScoringStrategy).GetField("_alpha", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ensemble, .75);
        typeof(EnsembleScoringStrategy).GetField("_neuralBeta", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ensemble, .12);
        var f = new CandidateFeatures { IsExternalCandidate = external, GenreSimilarity = .7, CombinedCriticScore = .8, ContentFitSupport = .8, AudienceMismatch = .25 };
        var capture = typeof(EnsembleScoringStrategy).GetMethod("CaptureContextualScore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pinned = (Func<CandidateFeatures, double>)capture.Invoke(ensemble, [.75, .12, .1])!;
        var expected = ScoringHelper.BuildMarginalExplanation(f, pinned, ensemble.Name);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(ensemble.ScoreWithExplanation(f)));
        var calls = 0;
        var actual = ScoringHelper.BuildMarginalExplanation(f, input =>
        {
            var value = pinned(input);
            if (++calls == 1)
            {
                // Deterministically reproduce an update between the full and counterfactual reads.
                // These are isolated in-memory test models, never a production fit or checkpoint.
                typeof(LearnedScoringStrategy).GetField("_bias", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(learned, -.8);
                typeof(NeuralScoringStrategy).GetField("_biasOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(neural, 8.0);
            }

            return value;
        }, ensemble.Name);
        Assert.True(calls > 10);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(expected.FinalScore, pinned(f));
        var refreshed = (Func<CandidateFeatures, double>)capture.Invoke(ensemble, [.75, .12, .1])!;
        Assert.NotEqual(expected.FinalScore, refreshed(f));
        Assert.Equal(refreshed(f), ScoringHelper.BuildMarginalExplanation(f, refreshed, ensemble.Name).FinalScore);
        Assert.Equal(refreshed(f), ensemble.ScoreWithExplanation(f).FinalScore);
    }

    [Fact]
    public void CapturedContextSurvivesNeuralDisposalAndFreshCapturePreservesTheExistingNeutralFallback()
    {
        var learned = LearnedScoringStrategy.FromParameters(Predictor().ExportParameters())!;
        using var initializer = new NeuralScoringStrategy();
        var parameters = initializer.CapturePredictionSnapshot()!.ExportParameters();
        parameters.BiasOutput = -2;
        using var neural = NeuralScoringStrategy.FromParameters(parameters)!;
        using var ensemble = new EnsembleScoringStrategy(learned, new HeuristicScoringStrategy(1), neural, ownsNeural: false);
        var f = new CandidateFeatures { GenreSimilarity = .7, CollectionContinuationSupport = .75 };
        var capture = typeof(EnsembleScoringStrategy).GetMethod("CaptureContextualScore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pinned = (Func<CandidateFeatures, double>)capture.Invoke(ensemble, [.75, .12, .1])!;
        var expected = pinned(f);
        neural.Dispose();
        Assert.Equal(expected, pinned(f));
        var fresh = (Func<CandidateFeatures, double>)capture.Invoke(ensemble, [.75, .12, .1])!;
        var raw = (.25 * HeuristicScoringStrategy.ScoreRaw(f)) + (.75 * ((.88 * learned.Score(f)) + (.12 * .5)));
        Assert.Equal(ScoringHelper.ComputeContextualScore(raw, f, .1), fresh(f));
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
        var weights = new double[CandidateFeatures.FeatureCount];
        weights[(int)FeatureIndex.GenreSimilarity] = .03;
        weights[(int)FeatureIndex.TagSimilarity] = .2;
        return new LearnedScoringStrategy.PredictionSnapshot(new LearnedScoringStrategy.WeightsData { Version = 6, TrainingGeneration = 1, Weights = weights, Bias = .6 });
    }
}
