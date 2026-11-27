using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Training;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for ComputeLanguageAffinity and ComputeSubtitleLanguageAffinity - the two internal static language-scoring entry points on the recommendation engine.
/// </summary>
public sealed class EngineLanguageAffinityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForcedDominantUsageCannotOutrankActiveAudioOrSubtitleChoices(bool subtitles)
    {
        var profile = new UserWatchProfile();
        var languages = new Dictionary<string, LanguageProfileEntry>
        {
            ["en"] = new() { ForcedCount = 100 },
            ["de"] = new() { ChosenCount = 1 }
        };
        if (subtitles) profile.SubtitleLanguageProfile = languages;
        else profile.LanguageProfile = languages;
        double Score(string language) => subtitles
            ? TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache([language], profile)
            : TrainingFeatureComputer.ComputeLanguageAffinityFromCache([language], profile);

        Assert.Equal("en", subtitles ? profile.PrimarySubtitleLanguage : profile.PrimaryLanguage);
        Assert.Equal(.5, Score("en"));
        Assert.Equal(1, Score("de"));
        if (!subtitles)
        {
            Assert.Null(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine.FirstPreferredLanguage(["en"], profile));
            Assert.Equal("de", Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine.FirstPreferredLanguage(["de"], profile));
        }

        var mixed = new Dictionary<string, LanguageProfileEntry>
        {
            ["en"] = new() { ChosenCount = 1, ForcedCount = 100 },
            ["de"] = new() { ChosenCount = 2 }
        };
        if (subtitles) profile.SubtitleLanguageProfile = mixed;
        else profile.LanguageProfile = mixed;
        Assert.Equal("en", subtitles ? profile.PrimarySubtitleLanguage : profile.PrimaryLanguage);
        Assert.Equal(.85, Score("en"));
        Assert.Equal(1, Score("de"));
    }

    [Fact]
    public void OnlyForcedUsageStaysToleranceAndCannotCreateAReasonAnchor()
    {
        var profile = new UserWatchProfile
        {
            LanguageProfile = new() { ["en"] = new() { ForcedCount = 100 } },
            SubtitleLanguageProfile = new() { ["en"] = new() { ForcedCount = 100 } }
        };
        Assert.Equal("en", profile.PrimaryLanguage);
        Assert.Equal(.5, TrainingFeatureComputer.ComputeLanguageAffinityFromCache(["en"], profile));
        Assert.Equal(.5, TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache(["en"], profile));
        Assert.Null(Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine.FirstPreferredLanguage(["en"], profile));
        foreach (var language in new[] { "en", "de" })
        {
            Assert.Equal(.5, TrainingFeatureComputer.ComputeLanguageAffinityFromCache([language], profile));
            Assert.Equal(.5, TrainingFeatureComputer.ComputeSubtitleLanguageAffinityFromCache([language], profile));
            Assert.False(TrainingFeatureComputer.HasLanguageEvidence([language], profile.LanguageProfile, true));
            Assert.False(TrainingFeatureComputer.HasLanguageEvidence([language], profile.SubtitleLanguageProfile, true));
        }

        var unknown = new CandidateFeatures
        {
            LanguageAffinity = .5, SubtitleLanguageAffinity = .5,
            HasAudioLanguageEvidence = TrainingFeatureComputer.HasLanguageEvidence(["de"], profile.LanguageProfile, true),
            HasSubtitleLanguageEvidence = TrainingFeatureComputer.HasLanguageEvidence(["de"], profile.SubtitleLanguageProfile, true)
        };
        var means = Enumerable.Repeat(.7, CandidateFeatures.FeatureCount).ToArray();
        Assert.Equal(.7, unknown.ToModelVector(means)[(int)FeatureIndex.LanguageAffinity]);
        Assert.Equal(.7, unknown.ToModelVector(means)[(int)FeatureIndex.SubtitleLanguageAffinity]);
        Assert.Equal(0, profile.LanguageProfile["en"].ChosenCount);
        Assert.Equal(100, profile.LanguageProfile["en"].ForcedCount);
    }

    [Fact]
    public void EqualActiveChoicesKeepThePrimaryTieBreakButStalePreferenceSetsCannotInventChoices()
    {
        var languages = new Dictionary<string, LanguageProfileEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = new() { ChosenCount = 3 },
            ["en"] = new() { ChosenCount = 3 },
            ["fr"] = new() { ForcedCount = 2 }
        };
        var preferred = new HashSet<string>(["de", "en", "fr"], StringComparer.OrdinalIgnoreCase);
        var tolerated = new HashSet<string>(["fr"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, TrainingFeatureComputer.ComputeBestLanguageAffinity(["en"], "en", preferred, tolerated, languages));
        Assert.Equal(.85, TrainingFeatureComputer.ComputeBestLanguageAffinity(["de"], "en", preferred, tolerated, languages));
        Assert.Equal(1, TrainingFeatureComputer.ComputeBestLanguageAffinity(["de"], "unknown", preferred, tolerated, languages));
        Assert.Equal(.5, TrainingFeatureComputer.ComputeBestLanguageAffinity(["fr"], "fr", preferred, tolerated, languages));
        Assert.Equal(.1, TrainingFeatureComputer.ComputeBestLanguageAffinity(["es"], "es", preferred, tolerated, languages));
    }

    // ComputeLanguageAffinity - AUDIO language affinity

    [Fact]
    public void ComputeLanguageAffinity_EmptyLanguageProfile_ReturnsNeutral()
    {
        // BUG GUARD: the empty-profile short-circuit must fire BEFORE any BaseItem access - otherwise scoring cold-start users (who have no profile yet) would hit the GetMediaStreams path on every single candidate and produce a huge per-candidate cost during warmup.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>()
        };
        // A brand-new Movie has no streams - but we should never reach that code path because the profile is empty.
        var item = new Movie();
        Assert.Equal(0.5, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeLanguageAffinity(profile, item));
    }

    [Fact]
    public void ComputeLanguageAffinity_ProfileWithData_ItemHasNoStreams_ReturnsNeutral()
    {
        // BUG GUARD: this is the "graceful fallback" branch of ResolveMediaLanguages - GetMediaStreams on a raw Movie() with no stream metadata either throws or returns null, and either path must produce the neutral 0.5 rather than penalising the candidate to zero.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 5, ForcedCount = 0 } }
            }
        };
        var item = new Movie();
        Assert.Equal(0.5, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeLanguageAffinity(profile, item));
    }

    [Fact]
    public void ComputeLanguageAffinity_ProfileWithMultipleLanguages_ItemHasNoStreams_ReturnsNeutral()
    {
        // Even with a rich, multi-language profile the outcome must be the neutral
        // 0.5 when the item exposes nothing - the profile-shape must not accidentally
        // influence the fallback path.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 20, ForcedCount = 0 } },
                { "de", new LanguageProfileEntry { ChosenCount = 5, ForcedCount = 3 } },
                { "ja", new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 8 } }
            }
        };
        var item = new Movie();
        Assert.Equal(0.5, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeLanguageAffinity(profile, item));
    }

    // ComputeSubtitleLanguageAffinity - SUBTITLE language affinity

    [Fact]
    public void ComputeSubtitleLanguageAffinity_EmptySubtitleProfile_ReturnsNeutral()
    {
        // Mirror of the audio short-circuit. Users who have never picked a subtitle track (e.g.
        var profile = new UserWatchProfile
        {
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>()
        };
        var item = new Movie();
        Assert.Equal(0.5, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeSubtitleLanguageAffinity(profile, item));
    }

    [Fact]
    public void ComputeSubtitleLanguageAffinity_ProfileWithData_ItemHasNoStreams_ReturnsNeutral()
    {
        // BUG GUARD: same graceful-fallback contract as ComputeLanguageAffinity - an item without subtitle stream metadata must never be pushed to the bottom of the ranking just because its metadata is missing.
        var profile = new UserWatchProfile
        {
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 3, ForcedCount = 0 } }
            }
        };
        var item = new Movie();
        Assert.Equal(0.5, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeSubtitleLanguageAffinity(profile, item));
    }

    // Cross-feature isolation guard

    [Fact]
    public void ComputeLanguageAffinity_SubtitleProfilePresent_AudioProfileEmpty_StillNeutral()
    {
        // BUG GUARD: the two profiles are INDEPENDENT.
        var profile = new UserWatchProfile
        {
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 10, ForcedCount = 0 } }
            }
            // LanguageProfile intentionally left as the default (empty) dictionary.
        };
        var item = new Movie();
        Assert.Equal(0.5, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeLanguageAffinity(profile, item));
    }

    [Fact]
    public void ComputeSubtitleLanguageAffinity_AudioProfilePresent_SubtitleProfileEmpty_StillNeutral()
    {
        // Mirror of the above - a user with a rich audio profile but no subtitle
        // profile must still get 0.5 from the SUBTITLE scorer.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 10, ForcedCount = 0 } }
            }
            // SubtitleLanguageProfile intentionally left as the default (empty) dictionary.
        };
        var item = new Movie();
        Assert.Equal(0.5, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeSubtitleLanguageAffinity(profile, item));
    }

    // Positive-signal path: past both short-circuits, the real ComputeBestLanguageAffinity delegation runs.

    [Fact]
    public void ComputeLanguageAffinity_ProfileAndCandidateStreamsMatchPrimary_ReturnsHighAffinity()
    {
        // The user's dominant (primary) language is 'en'; the candidate carries an English audio stream ('eng' -> 'en').
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 10, ForcedCount = 0 } }
            }
        };

        var candidate = new Mock<Movie> { CallBase = true };
        candidate.Setup(m => m.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Audio, Language = "eng" }
        ]);

        var affinity = Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeLanguageAffinity(profile, candidate.Object);

        Assert.True(
            affinity > 0.5,
            $"A primary-language audio match must score above the 0.5 neutral value; got {affinity}.");
    }

    [Fact]
    public void ComputeSubtitleLanguageAffinity_ProfileAndCandidateSubtitleStreamsMatch_ReturnsHighAffinity()
    {
        // Subtitle mirror of the audio positive path: primary subtitle language 'en', candidate exposes an English subtitle stream.
        var profile = new UserWatchProfile
        {
            SubtitleLanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 10, ForcedCount = 0 } }
            }
        };

        var candidate = new Mock<Movie> { CallBase = true };
        candidate.Setup(m => m.GetMediaStreams()).Returns(
        [
            new MediaStream { Type = MediaStreamType.Subtitle, Language = "eng" }
        ]);

        var affinity = Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeSubtitleLanguageAffinity(profile, candidate.Object);

        Assert.True(
            affinity > 0.5,
            $"A primary-language subtitle match must score above the 0.5 neutral value; got {affinity}.");
    }

    // FirstPreferredLanguage - the language anchor must only name a >0.5-tier language (primary 1.0 or preferred 0.85).

    [Fact]
    public void FirstPreferredLanguage_PreferredTierLanguage_IsNamed()
    {
        // "en" has ChosenCount > 0, placing it in the preferred (0.85) tier, so the anchor is allowed to name it.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 5, ForcedCount = 0 } }
            }
        };

        var result = Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .FirstPreferredLanguage(["en"], profile);

        Assert.Equal("en", result);
    }

    [Fact]
    public void FirstPreferredLanguage_PrimaryTierLanguage_IsNamed()
    {
        // "ja" has the highest WeightedScore, making it the primary (1.0) tier, so it is a valid anchor language.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "ja", new LanguageProfileEntry { ChosenCount = 20, ForcedCount = 0 } },
                { "en", new LanguageProfileEntry { ChosenCount = 2, ForcedCount = 0 } }
            }
        };

        var result = Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .FirstPreferredLanguage(["ja"], profile);

        Assert.Equal("ja", result);
    }

    [Fact]
    public void FirstPreferredLanguage_OnlyLowTierInProfile_ReturnsNull()
    {
        // BUG GUARD (H): "de" is tolerated only (ChosenCount 0, ForcedCount 1 -> WeightedScore 0.25), so it is
        // neither preferred nor primary - "en" outranks it on WeightedScore. The pre-fix code named any language
        // merely present in the profile (0.3 tier), advertising a language that never drove LanguageAffinity above
        // the 0.5 neutral floor; the anchor must now stay silent and return null.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>
            {
                { "en", new LanguageProfileEntry { ChosenCount = 10, ForcedCount = 0 } },
                { "de", new LanguageProfileEntry { ChosenCount = 0, ForcedCount = 1 } }
            }
        };

        var result = Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .FirstPreferredLanguage(["de"], profile);

        Assert.Null(result);
    }

    [Fact]
    public void FirstPreferredLanguage_EmptyProfile_ReturnsNull()
    {
        // Cold-start users have no language profile, so no language can clear the primary/preferred bar.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>()
        };

        var result = Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .FirstPreferredLanguage(["en"], profile);

        Assert.Null(result);
    }
}
