using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.ConfigAccess;

/// <summary>
///     Tests for <see cref="PluginConfigurationService"/>. The service depends on the
///     <c>Plugin.Instance</c> singleton - a piece of process-wide state that other tests
///     may leave in either the initialised or the uninitialised state. To make the
///     branches deterministic we go through the internal <c>IPluginAccessor</c> seam
///     (exposed via <c>InternalsVisibleTo</c>) with a per-test fake so both the
///     "plugin present" and "plugin absent" paths are exercised on every run.
/// </summary>
public class PluginConfigurationServiceTests
{
    /// <summary>
    ///     Deterministic accessor stub - replaces the real Plugin.Instance lookup with
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
        var sut = new PluginConfigurationService(new FakePluginAccessor { IsInitialized = true, Configuration = owned });

        var cfg = sut.GetConfiguration();

        // Same reference - the service returns the live shared object so callers have the
        // authoritative view. Mutation must go through ReadAndMutate, not via this reference.
        Assert.Same(owned, cfg);
        Assert.Equal("de", cfg.Language);
        Assert.Equal(99, cfg.OrphanMinAgeDays);
    }

    [Fact]
    public void GetConfiguration_ThrowsWhenNotInitialized()
    {
        // Finding #9: when the plugin singleton has not yet been created, GetConfiguration must
        // throw rather than return a silent default that could mask a startup-ordering bug.
        var sut = new PluginConfigurationService(new FakePluginAccessor { IsInitialized = false, Configuration = null });

        Assert.Throws<InvalidOperationException>(() => sut.GetConfiguration());
    }

    [Fact]
    public void GetConfiguration_ThrowsWhenAccessorIsUninitialised_DefaultFakeState()
    {
        // Confirm the default FakePluginAccessor (IsInitialized=false) also triggers the guard.
        var sut = new PluginConfigurationService(new FakePluginAccessor());
        Assert.Throws<InvalidOperationException>(() => sut.GetConfiguration());
    }

    // ===== SaveConfiguration =====

    [Fact]
    public void SaveConfiguration_ForwardsToAccessorWhenInitialized()
    {
        var accessor = new FakePluginAccessor { IsInitialized = true };
        var sut = new PluginConfigurationService(accessor);

        sut.SaveConfiguration();
        sut.SaveConfiguration();

        // Every call must be forwarded - the service must not deduplicate or throttle.
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
        // Smoke test for the production wiring path. Construction must not throw
        // regardless of whether Plugin.Instance has been set by a parallel test.
        // We verify PluginVersion but do NOT call GetConfiguration() because
        // GetConfiguration() throws InvalidOperationException when not initialized.
        //
        // NOTE: We intentionally do NOT invoke SaveConfiguration() here - that would
        // touch the real Plugin.Instance persistence layer (ambient disk I/O) and could
        // race with other tests running in parallel. Save-path behaviour is covered by
        // the accessor-mock tests below.
        var sut = new PluginConfigurationService();

        // Construction must succeed regardless of Plugin.Instance state.
        // IsInitialized reflects whether Plugin.Instance is non-null at construction time.
        Assert.Equal(Plugin.Instance != null, sut.IsInitialized);
        Assert.False(string.IsNullOrWhiteSpace(sut.PluginVersion));
    }

    // ===== ReadAndMutate =====

    [Fact]
    public void ReadAndMutate_ThrowsArgumentNullException_WhenMutateIsNull()
    {
        // The null-guard must fire before the lock and before any save, so a caller
        // passing a null delegate can never accidentally persist an unchanged config.
        var accessor = new FakePluginAccessor { Configuration = new PluginConfiguration() };
        var sut = new PluginConfigurationService(accessor);

        Assert.Throws<ArgumentNullException>(() => sut.ReadAndMutate(null!));
        Assert.Equal(0, accessor.SaveCallCount);
    }

    [Fact]
    public void ReadAndMutate_NoOps_WhenConfigurationIsNull()
    {
        // Plugin not initialised: there is nothing to mutate, so the delegate must not
        // run and no save may happen - an early return, not a silent write of a null.
        var accessor = new FakePluginAccessor { Configuration = null };
        var sut = new PluginConfigurationService(accessor);

        var mutateInvoked = false;
        sut.ReadAndMutate(_ => mutateInvoked = true);

        Assert.False(mutateInvoked);
        Assert.Equal(0, accessor.SaveCallCount);
    }

    [Fact]
    public void ReadAndMutate_InvokesMutateThenSaves_WhenConfigurationPresent()
    {
        // The delegate must receive the live shared config object (so edits stick), and
        // the save must follow the mutation exactly once to persist it atomically.
        var config = new PluginConfiguration { Language = "en" };
        var accessor = new FakePluginAccessor { Configuration = config };
        var sut = new PluginConfigurationService(accessor);

        PluginConfiguration? received = null;
        sut.ReadAndMutate(c =>
        {
            received = c;
            c.Language = "fr";
        });

        Assert.Same(config, received);
        Assert.Equal("fr", config.Language);
        Assert.Equal(1, accessor.SaveCallCount);
    }

    [Fact]
    public void ReadAndMutate_DoesNotSave_WhenMutateThrows()
    {
        // A failed mutation must propagate and must not persist a partially-applied config.
        var accessor = new FakePluginAccessor { Configuration = new PluginConfiguration() };
        var sut = new PluginConfigurationService(accessor);

        Assert.Throws<InvalidOperationException>(
            () => sut.ReadAndMutate(_ => throw new InvalidOperationException("boom")));
        Assert.Equal(0, accessor.SaveCallCount);
    }
}