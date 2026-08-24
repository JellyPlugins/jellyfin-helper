using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="Engine.ComputeLanguageAffinity"/> and
///     <see cref="Engine.ComputeSubtitleLanguageAffinity"/> - the two
///     <c>internal static</c> language-scoring entry points on the recommendation
///     engine.
///     <para>
///         Both methods enforce the exact same fail-safe contract:
///         <list type="number">
///             <item>
///                 If the user has NO language profile at all, return the neutral value
///                 <c>0.5</c> immediately without touching the candidate - this avoids
///                 crashing recommendations for monolingual libraries and fresh users.
///             </item>
///             <item>
///                 If the user HAS a profile but the candidate exposes no streams (or
///                 <see cref="MediaBrowser.Controller.Entities.BaseItem.GetMediaStreams"/>
///                 throws), return <c>0.5</c> after the graceful <c>[], []</c> fallback
///                 from <c>ResolveMediaLanguages</c>. This is the "no signal available"
///                 branch and must never blow up the scoring loop for a single
///                 misbehaving item.
///             </item>
///         </list>
///     </para>
///     <para>
///         BUG SURFACE: a regression that swaps the two returns (e.g. throwing when the
///         profile is empty, or returning <c>0.0</c> instead of <c>0.5</c> when the
///         candidate has no streams) would either crash the entire scoring loop or
///         penalise every un-tagged item in the library - both are silent-failure
///         patterns that never surface in the coarse-grained integration tests but
///         wreck the recommendation UX in the wild.
///     </para>
/// </summary>
public sealed class EngineLanguageAffinityTests
{
    // ============================================================================
    // ComputeLanguageAffinity - AUDIO language affinity
    // ============================================================================

    [Fact]
    public void ComputeLanguageAffinity_EmptyLanguageProfile_ReturnsNeutral()
    {
        // BUG GUARD: the empty-profile short-circuit must fire BEFORE any BaseItem
        // access - otherwise scoring cold-start users (who have no profile yet) would
        // hit the GetMediaStreams path on every single candidate and produce a huge
        // per-candidate cost during warmup.
        var profile = new UserWatchProfile
        {
            LanguageProfile = new Dictionary<string, LanguageProfileEntry>()
        };
        // A brand-new Movie has no streams - but we should never reach that code
        // path because the profile is empty. If the short-circuit ever regressed,
        // this test would still pass by accident because the fallback also returns
        // 0.5. That is why the NEXT test explicitly targets the fallback branch.
        var item = new Movie();
        Assert.Equal(0.5, Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine.Engine
            .ComputeLanguageAffinity(profile, item));
    }

    [Fact]
    public void ComputeLanguageAffinity_ProfileWithData_ItemHasNoStreams_ReturnsNeutral()
    {
        // BUG GUARD: this is the "graceful fallback" branch of ResolveMediaLanguages -
        // GetMediaStreams on a raw Movie() with no stream metadata either throws or
        // returns null, and either path must produce the neutral 0.5 rather than
        // penalising the candidate to zero. Zero would push perfectly good items to
        // the bottom of the recommendation list just because their metadata is thin.
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

    // ============================================================================
    // ComputeSubtitleLanguageAffinity - SUBTITLE language affinity
    // ============================================================================

    [Fact]
    public void ComputeSubtitleLanguageAffinity_EmptySubtitleProfile_ReturnsNeutral()
    {
        // Mirror of the audio short-circuit. Users who have never picked a subtitle
        // track (e.g. hearing users of a fully-dubbed library) must not have their
        // recommendations degraded - every candidate must score neutral on the
        // subtitle feature.
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
        // BUG GUARD: same graceful-fallback contract as ComputeLanguageAffinity -
        // an item without subtitle stream metadata must never be pushed to the
        // bottom of the ranking just because its metadata is missing.
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

    // ============================================================================
    // Cross-feature isolation guard
    // ============================================================================

    [Fact]
    public void ComputeLanguageAffinity_SubtitleProfilePresent_AudioProfileEmpty_StillNeutral()
    {
        // BUG GUARD: the two profiles are INDEPENDENT. A user with a rich subtitle
        // profile but no audio profile must still get 0.5 from the AUDIO scorer -
        // the wrong-profile short-circuit would be a silent bug that entangles the
        // two features and skews the ensemble weighting.
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

    // ============================================================================
    // Positive-signal path: past both short-circuits, the real ComputeBestLanguageAffinity
    // delegation runs. GetMediaStreams is virtual on BaseItem, so a mocked candidate can
    // surface a matching-language stream and drive the aggregate above the neutral 0.5.
    // ============================================================================

    [Fact]
    public void ComputeLanguageAffinity_ProfileAndCandidateStreamsMatchPrimary_ReturnsHighAffinity()
    {
        // The user's dominant (primary) language is 'en'; the candidate carries an English
        // audio stream ('eng' -> 'en'). This exercises the branch beyond both neutral returns
        // where the real ComputeBestLanguageAffinity delegation actually runs and awards the
        // 1.0 primary-match tier - so the result must be strictly above the 0.5 neutral value.
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
        // Subtitle mirror of the audio positive path: primary subtitle language 'en', candidate
        // exposes an English subtitle stream. Reaches the subtitle ComputeBestLanguageAffinity
        // delegation, which must return an above-neutral score for the primary-match.
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