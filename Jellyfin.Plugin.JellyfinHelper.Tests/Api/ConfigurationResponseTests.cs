using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Unit tests for <see cref="ConfigurationResponse" /> and <see cref="MaskedArrInstanceConfig" />.
///     Verifies the masking contract: non-empty API keys must never leave the server in plain text;
///     the sentinel value signals "key is already set" to the UI without exposing the real value.
/// </summary>
public class ConfigurationResponseTests
{
    // ── ApiKeyMask constant ──────────────────────────────────────────────────

    [Fact]
    public void ApiKeyMask_IsTripleAsterisk()
    {
        Assert.Equal("***", ConfigurationResponse.ApiKeyMask);
    }

    // ── SeerrApiKey masking ──────────────────────────────────────────────────

    [Fact]
    public void FromConfig_SeerrApiKey_NonEmpty_ReturnsMask()
    {
        var config = new PluginConfiguration { SeerrApiKey = "real-secret-key" };
        var response = ConfigurationResponse.FromConfig(config);
        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.SeerrApiKey);
    }

    [Fact]
    public void FromConfig_SeerrApiKey_Empty_ReturnsEmpty()
    {
        var config = new PluginConfiguration { SeerrApiKey = string.Empty };
        var response = ConfigurationResponse.FromConfig(config);
        Assert.Equal(string.Empty, response.SeerrApiKey);
    }

    [Fact]
    public void FromConfig_SeerrApiKey_Whitespace_ReturnsEmpty()
    {
        // Whitespace-only key is treated as "not configured" (IsNullOrWhiteSpace) — same
        // behaviour as the save-path in ApplyRequestToConfig. Masking it as "***" would
        // mislead operators into thinking the key is valid when it will fail all API calls.
        var config = new PluginConfiguration { SeerrApiKey = "   " };
        var response = ConfigurationResponse.FromConfig(config);
        Assert.Equal(string.Empty, response.SeerrApiKey);
    }

    // ── Radarr instance masking ──────────────────────────────────────────────

    [Fact]
    public void FromConfig_RadarrInstance_NonEmptyKey_ReturnsMask()
    {
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Name = "R1", Url = "http://radarr", ApiKey = "abc123" });

        var response = ConfigurationResponse.FromConfig(config);

        Assert.Single(response.RadarrInstances);
        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public void FromConfig_RadarrInstance_EmptyKey_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Name = "R1", Url = "http://radarr", ApiKey = string.Empty });

        var response = ConfigurationResponse.FromConfig(config);

        Assert.Equal(string.Empty, response.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public void FromConfig_RadarrInstance_PreservesNameAndUrl()
    {
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { Name = "Main", Url = "http://radarr:7878", ApiKey = "key" });

        var response = ConfigurationResponse.FromConfig(config);

        Assert.Equal("Main", response.RadarrInstances[0].Name);
        Assert.Equal("http://radarr:7878", response.RadarrInstances[0].Url);
    }

    // ── Sonarr instance masking ──────────────────────────────────────────────

    [Fact]
    public void FromConfig_SonarrInstance_NonEmptyKey_ReturnsMask()
    {
        var config = new PluginConfiguration();
        config.SonarrInstances.Add(new ArrInstanceConfig { Name = "S1", Url = "http://sonarr", ApiKey = "xyz789" });

        var response = ConfigurationResponse.FromConfig(config);

        Assert.Single(response.SonarrInstances);
        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.SonarrInstances[0].ApiKey);
    }

    [Fact]
    public void FromConfig_SonarrInstance_EmptyKey_ReturnsEmpty()
    {
        var config = new PluginConfiguration();
        config.SonarrInstances.Add(new ArrInstanceConfig { Name = "S1", Url = "http://sonarr", ApiKey = string.Empty });

        var response = ConfigurationResponse.FromConfig(config);

        Assert.Equal(string.Empty, response.SonarrInstances[0].ApiKey);
    }

    // ── Multiple instances ───────────────────────────────────────────────────

    [Fact]
    public void FromConfig_MultipleInstances_EachMaskedIndependently()
    {
        var config = new PluginConfiguration();
        config.RadarrInstances.Add(new ArrInstanceConfig { ApiKey = "key1" });
        config.RadarrInstances.Add(new ArrInstanceConfig { ApiKey = string.Empty });
        config.RadarrInstances.Add(new ArrInstanceConfig { ApiKey = "key3" });

        var response = ConfigurationResponse.FromConfig(config);

        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.RadarrInstances[0].ApiKey);
        Assert.Equal(string.Empty, response.RadarrInstances[1].ApiKey);
        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.RadarrInstances[2].ApiKey);
    }

    // ── Non-key fields pass through unchanged ────────────────────────────────

    [Fact]
    public void FromConfig_NonKeyFields_PassThroughUnchanged()
    {
        var config = new PluginConfiguration
        {
            OrphanMinAgeDays = 14,
            TrashRetentionDays = 30,
            Language = "de",
            SeerrUrl = "http://seerr",
            UseTrash = true
        };

        var response = ConfigurationResponse.FromConfig(config);

        Assert.Equal(14, response.OrphanMinAgeDays);
        Assert.Equal(30, response.TrashRetentionDays);
        Assert.Equal("de", response.Language);
        Assert.Equal("http://seerr", response.SeerrUrl);
        Assert.True(response.UseTrash);
    }

    // ── MaskedArrInstanceConfig defaults ────────────────────────────────────

    [Fact]
    public void MaskedArrInstanceConfig_Defaults_AreEmpty()
    {
        var masked = new MaskedArrInstanceConfig();
        Assert.Equal(string.Empty, masked.Name);
        Assert.Equal(string.Empty, masked.Url);
        Assert.Equal(string.Empty, masked.ApiKey);
    }

    // ── Real key is never present in response ────────────────────────────────

    [Fact]
    public void FromConfig_RealKeyNeverAppearsInResponse()
    {
        const string realKey = "super-secret-token-12345";
        var config = new PluginConfiguration { SeerrApiKey = realKey };
        config.RadarrInstances.Add(new ArrInstanceConfig { ApiKey = realKey });
        config.SonarrInstances.Add(new ArrInstanceConfig { ApiKey = realKey });

        var response = ConfigurationResponse.FromConfig(config);

        Assert.NotEqual(realKey, response.SeerrApiKey);
        Assert.NotEqual(realKey, response.RadarrInstances[0].ApiKey);
        Assert.NotEqual(realKey, response.SonarrInstances[0].ApiKey);
    }
}
