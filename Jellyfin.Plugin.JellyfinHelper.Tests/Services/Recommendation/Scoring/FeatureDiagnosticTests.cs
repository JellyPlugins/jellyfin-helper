using System.Globalization;
using System.Reflection;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Scoring;

/// <summary>Locks every diagnostic name to its actual feature slot, including the legacy series alias.</summary>
public sealed class FeatureDiagnosticTests
{
    private static readonly string[] ExpectedNames =
    [
        "GenreSimilarity", "CollaborativeScore", "CombinedCriticScore", "RecencyScore", "YearProximityScore",
        "GenreCountNormalized", "IsSeries", "GenreCriticInteraction", "GenreCollabInteraction", "UserRatingScore",
        "CompletionRatio", "IsAbandoned", "HasInteraction", "PeopleSimilarity", "StudioMatch", "SeriesAffinity",
        "PopularityScore", "DayOfWeekAffinity", "HourOfDayAffinity", "IsWeekend", "TagSimilarity", "PeopleGenreInteraction",
        "RecencyCriticInteraction", "GenreUnderexposure", "GenreDominanceRatio", "GenreAffinityGap", "LibraryAddedRecency",
        "ContentNearestNeighborScore", "LanguageAffinity", "CollectionProgressionBoost", "SubtitleLanguageAffinity",
        "FranchiseAffinity", "ProductionLocationAffinity", "InheritedTagSimilarity", "SeriesCompletability", "WriterAffinity",
        "BillingWeightedPeople", "GenreStudioIdfPrior"
    ];

    [Fact]
    public void CanonicalNames_MatchAllFeatureSlotsAndLegacyAlias()
    {
        Assert.Equal(CandidateFeatures.FeatureCount, ExpectedNames.Length);
        for (var i = 0; i < ExpectedNames.Length; i++)
        {
            Assert.Equal(ExpectedNames[i], ScoringHelper.GetFeatureName((FeatureIndex)i));
        }

        Assert.Equal("SeriesAffinity", ScoringHelper.GetFeatureName(FeatureIndex.SeriesProgressionBoost));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FeatureLogs_PairEveryCanonicalNameWithItsActualWeight(bool neural)
    {
        var logger = TestMockFactory.CreateLogger();
        using var neuralStrategy = neural ? new NeuralScoringStrategy(logger: logger.Object) : null;
        object strategy = neuralStrategy is not null ? neuralStrategy : new LearnedScoringStrategy(logger: logger.Object);
        var type = strategy.GetType();
        var field = type.GetField(neural ? "_weightsIH" : "_weights", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var weights = (double[])field.GetValue(strategy)!;
        for (var i = 0; i < weights.Length; i++) weights[i] = 0.001 * (1 + i % CandidateFeatures.FeatureCount);
        type.GetMethod("LogFeatureImportance", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(strategy, neural ? [CandidateFeatures.FeatureCount] : null);

        var invocation = logger.Invocations.Single(call => call.Method.Name == nameof(ILogger.Log) && call.Arguments[0] is LogLevel.Debug);
        var state = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(invocation.Arguments[2]);
        var text = Assert.IsType<string>(state.Single(pair => pair.Key == (neural ? "FeatureImportance" : "FeatureWeights")).Value);
        for (var i = 0; i < ExpectedNames.Length; i++)
        {
            var expected = (i + 1) * 0.001 * (neural ? Math.Sqrt(NeuralScoringStrategy.Hidden1Size) : 1.0);
            Assert.Contains(ExpectedNames[i] + "=" + expected.ToString("F4", CultureInfo.InvariantCulture), text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("SeriesProgressionBoost=", text, StringComparison.Ordinal);
    }
}
