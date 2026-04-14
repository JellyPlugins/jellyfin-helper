using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

[Collection("ConfigOverride")]
public class ConfigurationControllerTests : IDisposable
{
    private readonly ConfigurationController _controller;
    private readonly Mock<ArrIntegrationService> _arrServiceMock;

    public ConfigurationControllerTests()
    {
        ControllerTestFactory.InitializePluginInstance();
        var loggerMock = new Mock<ILogger<ConfigurationController>>();

        // Mock ArrIntegrationService (TestConnectionAsync is virtual → mockable)
        var httpClientFactoryMock = new Mock<IHttpClientFactory>();
        var arrLoggerMock = new Mock<ILogger<ArrIntegrationService>>();
        _arrServiceMock = new Mock<ArrIntegrationService>(httpClientFactoryMock.Object, arrLoggerMock.Object) { CallBase = false };

        // Default: connection tests succeed
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "OK"));

        _controller = new ConfigurationController(_arrServiceMock.Object, loggerMock.Object);

        // Use the same config instance that Plugin.Instance.Configuration returns.
        // This ensures UpdateConfigurationAsync (writes to Plugin.Instance.Configuration)
        // and GetConfiguration (reads via CleanupConfigHelper.GetConfig()) share the same object.
        CleanupConfigHelper.ConfigOverride = Plugin.Instance!.Configuration;
    }

    public void Dispose()
    {
        CleanupConfigHelper.ConfigOverride = null;
    }

    [Fact]
    public void GetConfiguration_ReturnsCurrentConfig()
    {
        var result = _controller.GetConfiguration();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<PluginConfiguration>(okResult.Value);
    }

    [Fact]
    public async Task UpdateConfiguration_ValidConfig_ReturnsOk()
    {
        var request = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 5,
            TrashRetentionDays = 10
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(5, Plugin.Instance!.Configuration.OrphanMinAgeDays);
        Assert.Equal(10, Plugin.Instance.Configuration.TrashRetentionDays);
    }

    [Fact]
    public async Task UpdateConfiguration_InvalidOrphanAge_ReturnsBadRequest()
    {
        var request = new ConfigurationUpdateRequest { OrphanMinAgeDays = -1 };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_InvalidTrashRetention_ReturnsBadRequest()
    {
        var request = new ConfigurationUpdateRequest { TrashRetentionDays = -1 };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_MultipleRadarrInstances_AllPersisted()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = new[]
            {
                new ArrInstanceConfig { Name = "Radarr-1", Url = "http://r1:7878", ApiKey = "key1" },
                new ArrInstanceConfig { Name = "Radarr-2", Url = "http://r2:7878", ApiKey = "key2" },
                new ArrInstanceConfig { Name = "Radarr-3", Url = "http://r3:7878", ApiKey = "key3" },
            },
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var config = Plugin.Instance!.Configuration;
        Assert.Equal(3, config.RadarrInstances.Count);
        Assert.Equal("Radarr-2", config.RadarrInstances[1].Name);
        Assert.Equal("http://r3:7878", config.RadarrInstances[2].Url);
    }

    [Fact]
    public async Task UpdateConfiguration_MultipleInstances_SurviveGetAfterSave()
    {
        // Simulate POST: save 3 Radarr + 2 Sonarr instances
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = new[]
            {
                new ArrInstanceConfig { Name = "R1", Url = "http://r1:7878", ApiKey = "rk1" },
                new ArrInstanceConfig { Name = "R2", Url = "http://r2:7878", ApiKey = "rk2" },
                new ArrInstanceConfig { Name = "R3", Url = "http://r3:7878", ApiKey = "rk3" },
            },
            SonarrInstances = new[]
            {
                new ArrInstanceConfig { Name = "S1", Url = "http://s1:8989", ApiKey = "sk1" },
                new ArrInstanceConfig { Name = "S2", Url = "http://s2:8989", ApiKey = "sk2" },
            },
        };

        await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        // Simulate GET: retrieve the config and serialize to JSON (like Jellyfin API)
        var getResult = _controller.GetConfiguration();
        var okResult = Assert.IsType<OkObjectResult>(getResult.Result);
        var config = Assert.IsType<PluginConfiguration>(okResult.Value);

        // Jellyfin uses PropertyNamingPolicy = null (PascalCase) and PropertyNameCaseInsensitive = true
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
        };

        var json = JsonSerializer.Serialize(config, jsonOptions);

        // Verify the JSON contains all instances with PascalCase keys
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("RadarrInstances", out var radarrArr), "JSON must contain RadarrInstances (PascalCase)");
        Assert.Equal(3, radarrArr.GetArrayLength());
        Assert.Equal("R2", radarrArr[1].GetProperty("Name").GetString());
        Assert.Equal("http://r3:7878", radarrArr[2].GetProperty("Url").GetString());

        Assert.True(root.TryGetProperty("SonarrInstances", out var sonarrArr), "JSON must contain SonarrInstances (PascalCase)");
        Assert.Equal(2, sonarrArr.GetArrayLength());
        Assert.Equal("S1", sonarrArr[0].GetProperty("Name").GetString());

        // Also verify it deserializes back correctly (simulating JS → server round-trip)
        var restored = JsonSerializer.Deserialize<PluginConfiguration>(json, jsonOptions);
        Assert.NotNull(restored);
        Assert.Equal(3, restored!.RadarrInstances.Count);
        Assert.Equal(2, restored.SonarrInstances.Count);
    }

    [Fact]
    public async Task UpdateConfiguration_UnreachableArr_SavesButReturnsWarnings()
    {
        // Simulate one reachable and one unreachable Radarr instance
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync("http://r1:7878", "key1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Radarr v5.0"));
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync("http://r2:7878", "key2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Connection refused"));

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = new[]
            {
                new ArrInstanceConfig { Name = "OK-Radarr", Url = "http://r1:7878", ApiKey = "key1" },
                new ArrInstanceConfig { Name = "Bad-Radarr", Url = "http://r2:7878", ApiKey = "key2" },
            },
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        // Config should still be saved
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, Plugin.Instance!.Configuration.RadarrInstances.Count);

        // Response should contain warnings
        var json = JsonSerializer.Serialize(okResult.Value);
        Assert.Contains("Bad-Radarr", json);
        Assert.Contains("not reachable", json);
    }

    [Fact]
    public void JsonRoundTrip_ConfigurationUpdateRequest_DeserializesMultipleInstances()
    {
        // Simulate the exact JSON the frontend sends (PascalCase)
        var frontendJson = """
        {
            "IncludedLibraries": "",
            "ExcludedLibraries": "",
            "OrphanMinAgeDays": 0,
            "TrickplayTaskMode": "DryRun",
            "EmptyMediaFolderTaskMode": "DryRun",
            "OrphanedSubtitleTaskMode": "DryRun",
            "StrmRepairTaskMode": "DryRun",
            "UseTrash": false,
            "TrashFolderPath": ".jellyfin-trash",
            "TrashRetentionDays": 30,
            "Language": "en",
            "RadarrUrl": "http://r1:7878",
            "RadarrApiKey": "key1",
            "SonarrUrl": "",
            "SonarrApiKey": "",
            "RadarrInstances": [
                { "Name": "Radarr-1", "Url": "http://r1:7878", "ApiKey": "key1" },
                { "Name": "Radarr-2", "Url": "http://r2:7878", "ApiKey": "key2" },
                { "Name": "Radarr-3", "Url": "http://r3:7878", "ApiKey": "key3" }
            ],
            "SonarrInstances": []
        }
        """;

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = true,
        };

        var request = JsonSerializer.Deserialize<ConfigurationUpdateRequest>(frontendJson, jsonOptions);

        Assert.NotNull(request);
        Assert.Equal(3, request!.RadarrInstances.Count);
        Assert.Equal("Radarr-1", request.RadarrInstances[0].Name);
        Assert.Equal("Radarr-2", request.RadarrInstances[1].Name);
        Assert.Equal("Radarr-3", request.RadarrInstances[2].Name);
        Assert.Equal("http://r2:7878", request.RadarrInstances[1].Url);
        Assert.Equal("key3", request.RadarrInstances[2].ApiKey);
    }
}