using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.ConfigAccess;

/// <summary>
///     Tests for <see cref="PluginConfigurationService"/>. The service depends on the
///     <c>Plugin.Instance</c> singleton — a piece of process-wide state that other tests
///     may leave in either the initialised or the uninitialised state. To make the
///     branches deterministic we go through the internal <c>IPluginAccessor</c> seam
///     (exposed via <c>InternalsVisibleTo</c>) with a per-test fake so both the
///     "plugin present" and "plugin absent" paths are exercised on every run.
/// </summary>
public class PluginConfigurationServiceTests
{
    /// <summary>
    ///     Deterministic accessor stub — replaces the real Plugin.Instance lookup with
    ///     fields we control, so the tests are independent of process-wide singleton
    ///     state and can be run in parallel without racing against any other suite.
    /// </summary>
    private sealed class FakePluginAccessor : PluginConfigurationService.IPluginAccessor
    {
        public bool IsInitialized { get; set; }
        public string? Version { get; set; }
        public PluginConfiguration? Configuration { get; set; }
        public int SaveCallCount { get; private set; }

        public void SaveConfiguration() => SaveCallCount++;
    }

    // ===== IsInitialized =====

    [Fact]
    public void IsInitialized_TrueWhenAccessorReportsInitialized()
    {
        var sut = new PluginConfigurationService(new FakePluginAccessor { IsInitialized = true });
        Assert.True(sut.IsInitialized);
    }

    [Fact]
    public void IsInitialized_FalseWhenAccessorReportsUninitialized()
    {
        var sut = new PluginConfigurationService(new FakePluginAccessor { IsInitialized = false });
        Assert.False(sut.IsInitialized);
    }

    // ===== PluginVersion =====

    [Fact]
    public void PluginVersion_ReturnsAccessorVersionWhenPresent()
    {
        var sut = new PluginConfigurationService(new FakePluginAccessor { Version = "1.2.3-test" });
        Assert.Equal("1.2.3-test", sut.PluginVersion);
    }

    [Fact]
    public void PluginVersion_FallsBackToUnknownWhenAccessorHasNoVersion()
    {
        var sut = new PluginConfigurationService(new FakePluginAccessor { Version = null });
        Assert.Equal("unknown", sut.PluginVersion);
    }

    // ===== GetConfiguration =====

    [Fact]
    public void GetConfiguration_ReturnsAccessorConfigurationWhenAvailable()
    {
        var owned = new PluginConfiguration { Language = "de", OrphanMinAgeDays = 99 };
        var sut = new PluginConfigurationService(new FakePluginAccessor { Configuration = owned });

        var cfg = sut.GetConfiguration();

        // Same reference — the service must not copy so callers can mutate through it.
        Assert.Same(owned, cfg);
        Assert.Equal("de", cfg.Language);
        Assert.Equal(99, cfg.OrphanMinAgeDays);
    }

    [Fact]
    public void GetConfiguration_FallsBackToFreshDefaultsWhenAccessorHasNone()
    {
        var sut = new PluginConfigurationService(new FakePluginAccessor { Configuration = null });

        var cfg = sut.GetConfiguration();

        Assert.NotNull(cfg);
        // A fresh PluginConfiguration is returned — must have default values, not aliased.
        Assert.IsType<PluginConfiguration>(cfg);
        // Each call under the fallback path returns a NEW instance (so callers can't
        // accidentally share a mutable default across the codebase).
        var cfg2 = sut.GetConfiguration();
        Assert.NotSame(cfg, cfg2);
    }

    [Fact]
    public void GetConfiguration_NeverReturnsNull_WhenAccessorIsNull()
    {
        // Redundant with the fallback test above but locks the contract at the interface
        // boundary: no code path may leak a null reference to a caller.
        var sut = new PluginConfigurationService(new FakePluginAccessor());
        Assert.NotNull(sut.GetConfiguration());
    }

    // ===== SaveConfiguration =====

    [Fact]
    public void SaveConfiguration_ForwardsToAccessorWhenInitialized()
    {
        var accessor = new FakePluginAccessor { IsInitialized = true };
        var sut = new PluginConfigurationService(accessor);

        sut.SaveConfiguration();
        sut.SaveConfiguration();

        // Every call must be forwarded — the service must not deduplicate or throttle.
        Assert.Equal(2, accessor.SaveCallCount);
    }

    [Fact]
    public void SaveConfiguration_ForwardsToAccessorEvenWhenUninitialized()
    {
        // Contract: the service forwards unconditionally; the ACCESSOR decides whether
        // an uninitialised plugin means "no-op" or "throw". This keeps the service
        // itself free of environmental branching.
        var accessor = new FakePluginAccessor { IsInitialized = false };
        var sut = new PluginConfigurationService(accessor);

        var ex = Record.Exception(() => sut.SaveConfiguration());

        Assert.Null(ex);
        Assert.Equal(1, accessor.SaveCallCount);
    }

    // ===== Constructor guards =====

    [Fact]
    public void Constructor_RejectsNullAccessor()
    {
        Assert.Throws<ArgumentNullException>(
            () => new PluginConfigurationService(accessor: null!));
    }

    [Fact]
    public void ParameterlessConstructor_UsesRealPluginAccessor_WithoutThrowing()
    {
        // Smoke test for the production wiring path. Whichever state Plugin.Instance
        // happens to be in at the moment this runs, construction must not throw and
        // every read must return a valid value (either the singleton's or the
        // documented fallback).
        var sut = new PluginConfigurationService();

        Assert.NotNull(sut.GetConfiguration());
        Assert.False(string.IsNullOrWhiteSpace(sut.PluginVersion));
        var ex = Record.Exception(() => sut.SaveConfiguration());
        Assert.Null(ex);
    }
}