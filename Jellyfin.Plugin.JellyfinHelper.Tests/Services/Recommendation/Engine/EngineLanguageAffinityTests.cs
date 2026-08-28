using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
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
}