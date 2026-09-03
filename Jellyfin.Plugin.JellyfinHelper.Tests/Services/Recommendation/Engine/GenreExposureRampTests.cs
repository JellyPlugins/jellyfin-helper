using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for the genre-exposure cold-start confidence ramp in <see cref="PreferenceBuilder" />.
///     Below <see cref="Services.Recommendation.EngineConstants.MinWatchCountForGenreExposure" /> watches the
///     three exposure features scale linearly with watch count; at or above the threshold they are unchanged.
/// </summary>
public class GenreExposureRampTests
{
    private const string Action = "Action";
    private const string Comedy = "Comedy";
    private const string Horror = "Horror";

    // A candidate carrying an underexposed genre so all three features are exercised (not just dominance).
    private static readonly string[] Candidate = [Horror];

    // Fixed playback time for every fixture row. BuildGenrePreferenceVector applies temporal decay, so per-row
    // DateTime.UtcNow would give the 30-item and 60-item profiles slightly different genre weights and break the
    // 12-decimal equality assertions even when the confidence ramp is correct.
    private static readonly DateTime PlayedAt = new(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     Builds a profile of <paramref name="count" /> played items following a fixed genre ratio
    ///     (60% Action, 30% Comedy, 10% Horror) so the normalized genre-preference shares are identical
    ///     across counts and only the confidence ramp differs.
    /// </summary>
    private static UserWatchProfile BuildProfile(int count)
    {
        var profile = new UserWatchProfile { UserId = Guid.NewGuid() };
        for (var i = 0; i < count; i++)
        {
            var genre = (i % 10) < 6 ? Action : (i % 10) < 9 ? Comedy : Horror;
            profile.WatchedItems.Add(new WatchedItemInfo
            {
                ItemId = Guid.NewGuid(),
                Played = true,
                LastPlayedDate = PlayedAt,
                Genres = [genre]
            });
        }

        return profile;
    }

    private static (double Underexposure, double DominanceRatio, double AffinityGap) FeaturesAt(int count)
    {
        var profile = BuildProfile(count);
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);
        return PreferenceBuilder.ComputeGenreExposureFeatures(Candidate, analysis);
    }

    [Fact]
    public void Confidence_IsAPureLinearMultiplierOnFeatures()
    {
        // Isolate the ramp from BuildGenreExposureAnalysis' distribution logic: hold the analysis structure
        // fixed and vary ONLY Confidence. The three features must scale by exactly that factor, proving the
        // ramp is a clean linear multiplier (a full-confidence baseline scaled by 0.5 equals the half run).
        static PreferenceBuilder.GenreExposureAnalysis Analysis(double confidence) =>
            new()
            {
                UnderexposedGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Horror },
                DominantGenres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Action },
                AveragePreferenceWeight = 0.8,
                GenrePreferences = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    { Action, 1.0 }, { Horror, 0.1 }
                },
                Confidence = confidence,
                IsValid = true
            };

        var full = PreferenceBuilder.ComputeGenreExposureFeatures(Candidate, Analysis(1.0));
        var half = PreferenceBuilder.ComputeGenreExposureFeatures(Candidate, Analysis(0.5));

        // Sanity: the full-confidence run must produce a non-zero signal so the scaling assertion has teeth.
        Assert.True(full.Underexposure > 0.0 || full.DominanceRatio > 0.0 || full.AffinityGap > 0.0);
        Assert.Equal(full.Underexposure * 0.5, half.Underexposure, 9);
        Assert.Equal(full.DominanceRatio * 0.5, half.DominanceRatio, 9);
        Assert.Equal(full.AffinityGap * 0.5, half.AffinityGap, 9);
    }

    [Fact]
    public void AtThreshold_ConfidenceIsExactlyOne()
    {
        var profile = BuildProfile(30);
        var prefs = PreferenceBuilder.BuildGenrePreferenceVector(profile);
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(prefs, profile);

        Assert.True(analysis.IsValid);
        Assert.Equal(1.0, analysis.Confidence, 12);
    }

    [Fact]
    public void AboveThreshold_ConfidenceStaysOne_FeaturesUnchanged()
    {
        // Confidence saturates at 1.0, so a heavy user sees identical features regardless of extra watches.
        var atThreshold = FeaturesAt(30);
        var aboveThreshold = FeaturesAt(60);

        Assert.Equal(atThreshold.Underexposure, aboveThreshold.Underexposure, 12);
        Assert.Equal(atThreshold.DominanceRatio, aboveThreshold.DominanceRatio, 12);
        Assert.Equal(atThreshold.AffinityGap, aboveThreshold.AffinityGap, 12);
    }

    [Fact]
    public void EmptyGenreVector_IsInvalidWithZeroConfidence()
    {
        var profile = new UserWatchProfile { WatchedItems = [] };
        var analysis = PreferenceBuilder.BuildGenreExposureAnalysis(new Dictionary<string, double>(), profile);

        Assert.False(analysis.IsValid);
        Assert.Equal(0.0, analysis.Confidence);

        var (underexposure, dominance, gap) = PreferenceBuilder.ComputeGenreExposureFeatures(Candidate, analysis);
        Assert.Equal(0.0, underexposure);
        Assert.Equal(0.0, dominance);
        Assert.Equal(0.0, gap);
    }
}
