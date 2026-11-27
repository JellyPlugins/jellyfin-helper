using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine.Training;

/// <summary>Organic language inputs share inference units and use only dated, earlier, self-excluded personal evidence.</summary>
public sealed class HistoricalLanguageFeaturesTests
{
    private static readonly DateTime Prediction = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void HistoricalLanguageProfiles_IgnoreFutureTargetUndatedAndCurrentAggregateEdits()
    {
        var earlier = Watch(Prediction.AddDays(-1), "de");
        var target = Watch(Prediction, "ja");
        var future = Watch(Prediction.AddDays(1), "ja");
        var undated = Watch(null, "ja");
        var excluded = Watch(Prediction.AddHours(-1), "ja");
        var stale = Watch(Prediction.AddDays(-2), "ja");
        stale.LanguagePlaybackDate = Prediction.AddDays(1);
        var source = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [earlier, target, future, undated, excluded, stale] };
        source.LanguageProfile["ja"] = new LanguageProfileEntry { ChosenCount = 1000 };
        source.SubtitleLanguageProfile["ja"] = new LanguageProfileEntry { ChosenCount = 1000 };
        var exclusions = new HashSet<Guid> { target.ItemId, excluded.ItemId };
        var direct = TrainingHistory.Before(source, Prediction, exclusions);
        var cached = new TrainingHistoryCache([source], CancellationToken.None).Get(source.UserId, Prediction, exclusions).Profile;
        Assert.Equal("de", direct.PrimaryLanguage);
        Assert.Equal("de", direct.PrimarySubtitleLanguage);
        Assert.Equal(1, direct.LanguageProfile["de"].ChosenCount);
        Assert.Equal(1, direct.SubtitleLanguageProfile["de"].ChosenCount);
        Assert.DoesNotContain("ja", direct.LanguageProfile.Keys);
        Assert.Equal(direct.LanguageProfile.Keys, cached.LanguageProfile.Keys);
        Assert.Equal(direct.SubtitleLanguageProfile.Keys, cached.SubtitleLanguageProfile.Keys);
        Assert.Equal(1000, source.LanguageProfile["ja"].ChosenCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrganicMovieAndAggregatedSeries_UseTheSharedLanguageCoordinatesWithoutTheirOwnOutcome(bool series)
    {
        var earlier = Watch(Prediction.AddDays(-1), "de");
        var target = Watch(Prediction, "ja");
        target.AudioLanguages = ["en", "de"];
        target.SubtitleLanguages = ["en"];
        var title = target.ItemId;
        if (series)
        {
            title = Guid.NewGuid();
            target.SeriesId = title;
            target.ItemType = "Episode";
        }

        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [earlier, target] };
        profile.LanguageProfile["ja"] = new LanguageProfileEntry { ChosenCount = 1000 };
        var built = TrainingDataBuilder.BuildExamples([], [profile], CancellationToken.None).Examples;
        var example = Assert.Single(built, row => row.ObservationId == TrainingExample.CreateTitleObservationId(profile.UserId, title, 0, series));
        var history = TrainingHistory.Before(profile, Prediction, new HashSet<Guid> { target.ItemId, title });
        Assert.Equal(1, example.Features.LanguageAffinity);
        Assert.Equal(TrainingFeatureComputer.ComputeLanguageAffinityFromCache(target.AudioLanguages, history), example.Features.LanguageAffinity);
        Assert.Equal(TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(target.SubtitleLanguages, history), example.Features.SubtitleLanguageAffinity);
        Assert.True(example.Features.HasAudioLanguageEvidence);
        Assert.True(example.Features.HasSubtitleLanguageEvidence);
        Assert.Equal(CandidateFeatures.FeatureCount, example.Features.ToVector().Length);
    }

    [Fact]
    public void LegacyAndFirstWatch_AreExplicitlyUnknownInsteadOfGenuineHalfAffinity()
    {
        var row = Watch(Prediction, "de");
        row.AudioLanguages = null;
        row.SubtitleLanguages = null;
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [row] };
        var example = Assert.Single(TrainingDataBuilder.BuildExamples([], [profile], CancellationToken.None).Examples);
        Assert.False(example.Features.HasAudioLanguageEvidence);
        Assert.False(example.Features.HasSubtitleLanguageEvidence);
        var means = Enumerable.Repeat(.75, CandidateFeatures.FeatureCount).ToArray();
        Assert.Equal(.75, example.Features.ToModelVector(means)[(int)FeatureIndex.LanguageAffinity]);
        Assert.Equal(.5, example.Features.ToVector()[(int)FeatureIndex.LanguageAffinity]);
        Assert.Null(row.AudioLanguages);
    }

    [Fact]
    public void DirectAggregatedHelper_CannotBorrowItsOwnSeriesOrCurrentAggregateLanguageEdits()
    {
        var earlier = Watch(Prediction.AddDays(-1), "de");
        var target = Watch(Prediction, "ja");
        var series = Guid.NewGuid();
        target.SeriesId = series;
        target.ItemType = "Episode";
        target.AudioLanguages = ["de"];
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [earlier, target] };
        profile.LanguageProfile["ja"] = new LanguageProfileEntry { ChosenCount = 1000 };
        var examples = new List<TrainingExample>();
        TrainingFeatureComputer.AddAggregatedSeriesExample(examples, [target], series, profile,
            new Dictionary<string, double> { ["Action"] = 1 }, [], 1, 2026, PreferenceBuilder.BuildGenreExposureAnalysis([], profile),
            [], new Dictionary<string, double>(), [], [], [], [], new Dictionary<string, double>(), new Dictionary<string, double>(), [],
            new Dictionary<string, double>(), null, Prediction);
        Assert.Equal(1, Assert.Single(examples).Features.LanguageAffinity);
        Assert.True(examples[0].Features.HasAudioLanguageEvidence);
        Assert.Equal(1000, profile.LanguageProfile["ja"].ChosenCount);
    }

    [Fact]
    public void PartlyUnlabelledCandidateAudio_RemainsUnknownDespiteItsKnownSubset()
    {
        var earlier = Watch(Prediction.AddDays(-1), "de");
        var target = Watch(Prediction, "ja");
        target.AudioLanguages = ["en"];
        target.HasAudioLanguageMetadata = false;
        var profile = new UserWatchProfile { UserId = Guid.NewGuid(), WatchedItems = [earlier, target] };
        var examples = TrainingDataBuilder.BuildExamples([], [profile], CancellationToken.None).Examples;
        var features = Assert.Single(examples, example => example.GeneratedAtUtc == Prediction).Features;
        Assert.False(features.HasAudioLanguageEvidence);
        var means = Enumerable.Repeat(.8, CandidateFeatures.FeatureCount).ToArray();
        Assert.Equal(.8, features.ToModelVector(means)[28]);
        var choices = new Dictionary<string, LanguageProfileEntry> { ["de"] = new() { ChosenCount = 1 } };
        Assert.False(TrainingFeatureComputer.HasLanguageEvidence(["und"], choices));
        Assert.True(TrainingFeatureComputer.HasLanguageEvidence(["de"], choices, true));
    }

    private static WatchedItemInfo Watch(DateTime? date, string language) => new()
    {
        ItemId = Guid.NewGuid(), ItemType = "Movie", Played = true, Genres = ["Action"], LastPlayedDate = date,
        AudioLanguages = [language, "en"], SubtitleLanguages = [language, "en"], PlaybackAudioLanguage = language,
        PlaybackSubtitleLanguage = language, LanguagePlaybackDate = date
    };
}
