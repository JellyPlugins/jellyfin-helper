using System;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>
///     Tests for <see cref="TemporalFeatures.ResolveIsWeekend"/>, the shared helper that
///     eliminates the five previously divergent IsWeekend semantics. Verifies user-anchored precedence, per-item override, and
///     deterministic no-signal fallback across all training + inference call sites.
/// </summary>
public class TemporalFeaturesTests
{
    // Fixed anchor timestamps for deterministic assertions.
    // Friday 2026-01-02 12:00 UTC and Saturday 2026-01-03 12:00 UTC.
    private static readonly DateTime FridayNoonUtc = new(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SaturdayNoonUtc = new(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ResolveIsWeekend_UserProfileNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TemporalFeatures.ResolveIsWeekend(null!));
    }

    [Fact]
    public void ResolveIsWeekend_LastActivityFriday_ReturnsFalse()
    {
        var profile = new UserWatchProfile { LastActivityDate = FridayNoonUtc };

        Assert.False(TemporalFeatures.ResolveIsWeekend(profile));
    }

    [Fact]
    public void ResolveIsWeekend_LastActivitySaturday_ReturnsTrue()
    {
        var profile = new UserWatchProfile { LastActivityDate = SaturdayNoonUtc };

        Assert.True(TemporalFeatures.ResolveIsWeekend(profile));
    }

    [Fact]
    public void ResolveIsWeekend_LastActivitySunday_ReturnsTrue()
    {
        var profile = new UserWatchProfile
        {
            LastActivityDate = new DateTime(2026, 1, 4, 12, 0, 0, DateTimeKind.Utc)
        };

        Assert.True(TemporalFeatures.ResolveIsWeekend(profile));
    }

    [Fact]
    public void ResolveIsWeekend_LastActivityWinsOverOverride()
    {
        // User anchor is Friday, but the per-item override says Saturday.
        // The helper must prefer the user anchor so that every feature row for
        // this user within one train/serve cycle carries the same IsWeekend value.
        var profile = new UserWatchProfile { LastActivityDate = FridayNoonUtc };

        var result = TemporalFeatures.ResolveIsWeekend(profile, SaturdayNoonUtc);

        Assert.False(result);
    }

    [Fact]
    public void ResolveIsWeekend_NoAnchor_FallsBackToOverride()
    {
        var profile = new UserWatchProfile { LastActivityDate = null };

        Assert.True(TemporalFeatures.ResolveIsWeekend(profile, SaturdayNoonUtc));
        Assert.False(TemporalFeatures.ResolveIsWeekend(profile, FridayNoonUtc));
    }

    [Fact]
    public void ResolveIsWeekend_NoAnchorAndNoOverride_ReturnsFalseDeterministically()
    {
        // No anchor + no override = no signal. The helper must return a fixed value so training
        // rows and inference rows for the same user never diverge based on what day the task ran.
        var profile = new UserWatchProfile { LastActivityDate = null };

        Assert.False(TemporalFeatures.ResolveIsWeekend(profile));
    }

    [Fact]
    public void ResolveIsWeekend_ConsistencyAcrossAllCallSites_UserAnchoredFriday()
    {
        // Simulates the parity contract enforced by FIX-1: with the same user profile,
        // all five call sites (live, Phase 1, Phase 2, Phase 3, aggregated series)
        // must produce the same IsWeekend value regardless of the per-item override.
        var profile = new UserWatchProfile { LastActivityDate = FridayNoonUtc };

        var live = TemporalFeatures.ResolveIsWeekend(profile);
        var phase1 = TemporalFeatures.ResolveIsWeekend(profile, SaturdayNoonUtc); // watched item on Sat
        var phase2 = TemporalFeatures.ResolveIsWeekend(profile, SaturdayNoonUtc); // organic watch on Sat
        var phase3 = TemporalFeatures.ResolveIsWeekend(profile); // random negative, no override
        var series = TemporalFeatures.ResolveIsWeekend(profile, SaturdayNoonUtc); // aggregated series

        Assert.False(live);
        Assert.False(phase1);
        Assert.False(phase2);
        Assert.False(phase3);
        Assert.False(series);
    }

    [Fact]
    public void ResolveIsWeekend_ConsistencyAcrossAllCallSites_UserAnchoredSaturday()
    {
        var profile = new UserWatchProfile { LastActivityDate = SaturdayNoonUtc };

        var live = TemporalFeatures.ResolveIsWeekend(profile);
        var phase1 = TemporalFeatures.ResolveIsWeekend(profile, FridayNoonUtc);
        var phase2 = TemporalFeatures.ResolveIsWeekend(profile, FridayNoonUtc);
        var phase3 = TemporalFeatures.ResolveIsWeekend(profile);
        var series = TemporalFeatures.ResolveIsWeekend(profile, FridayNoonUtc);

        Assert.True(live);
        Assert.True(phase1);
        Assert.True(phase2);
        Assert.True(phase3);
        Assert.True(series);
    }
}