using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.Arr;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Jellyfin.Plugin.JellyfinHelper.Services.ConfigAccess;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Scoring;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="ConfigurationController" />.
///     All tests use mocked <see cref="IPluginConfigurationService" /> - no
///     <c>Plugin.Instance</c> singleton is required, which eliminates flaky
///     behaviour caused by shared static state during parallel test execution.
/// </summary>
public class ConfigurationControllerTests
{
    private readonly Mock<IArrIntegrationService> _arrServiceMock;
    private readonly PluginConfiguration _config;
    private readonly Mock<IPluginConfigurationService> _configServiceMock;
    private readonly ConfigurationController _controller;
    private readonly Mock<IPluginLogService> _pluginLogMock;
    private readonly Mock<ISeerrIntegrationService> _seerrServiceMock;

    public ConfigurationControllerTests()
    {
        _config = new PluginConfiguration();
        _configServiceMock = new Mock<IPluginConfigurationService>();
        _configServiceMock.Setup(s => s.IsInitialized).Returns(true);
        _configServiceMock.Setup(s => s.GetConfiguration()).Returns(_config);
        TestMockFactory.SetupReadAndMutate(_configServiceMock, _config);
        _pluginLogMock = new Mock<IPluginLogService>();
        _arrServiceMock = new Mock<IArrIntegrationService>();
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "OK"));
        _seerrServiceMock = new Mock<ISeerrIntegrationService>();
        _seerrServiceMock
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "OK"));
        var configHelperMock = new Mock<ICleanupConfigHelper>();
        configHelperMock.Setup(h => h.GetConfig()).Returns(_config);
        var loggerMock = new Mock<ILogger<ConfigurationController>>();
        var libraryManagerMock = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        _controller = new ConfigurationController(
            _arrServiceMock.Object,
            _pluginLogMock.Object,
            loggerMock.Object,
            configHelperMock.Object,
            _configServiceMock.Object,
            _seerrServiceMock.Object,
            libraryManagerMock.Object,
            new EnsembleScoringStrategy());
    }

    [Fact]
    public void GetConfiguration_ReturnsCurrentConfig()
    {
        var result = _controller.GetConfiguration();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<ConfigurationResponse>(okResult.Value);
    }

    [Fact]
    public async Task UpdateConfiguration_ValidConfig_ReturnsOk()
    {
        var request = new ConfigurationUpdateRequest { OrphanMinAgeDays = 5, TrashRetentionDays = 10 };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(5, _config.OrphanMinAgeDays);
        Assert.Equal(10, _config.TrashRetentionDays);
        _configServiceMock.Verify(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.AtLeastOnce);
    }

    /// <summary>
    ///     Locks the deliberate design decision documented on
    ///     <c>ConfigurationController.ApplyRequestToConfig</c>: the Settings POST payload
    ///     MUST NOT be able to overwrite <see cref="PluginConfiguration.PluginLogLevel" />.
    ///     The field is owned exclusively by the Logs tab (PUT /Configuration/LogLevel) to
    ///     close a TOCTOU race where the Settings page had captured a stale value at page
    ///     load and would clobber a concurrent change from the Logs tab or another admin
    ///     session on save.
    /// </summary>
    [Fact]
    public async Task UpdateConfiguration_PluginLogLevel_IsIgnoredByDesignAndSurfacesWarning()
    {
        _config.PluginLogLevel = "WARN";
        var request = new ConfigurationUpdateRequest { PluginLogLevel = "DEBUG" };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("WARN", _config.PluginLogLevel);

        // Silent drop is the worst option — the response must call out the ignored change so
        // the client can surface it to the admin instead of pretending the save worked.
        var payload = Assert.IsType<ConfigurationSaveResponse>(ok.Value);
        Assert.Contains(payload.Warnings, w => w.Contains("PluginLogLevel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateConfiguration_PluginLogLevel_MatchingCurrent_NoWarning()
    {
        _config.PluginLogLevel = "WARN";
        var request = new ConfigurationUpdateRequest { PluginLogLevel = "warn" };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("WARN", _config.PluginLogLevel);

        var payload = Assert.IsType<ConfigurationSaveResponse>(ok.Value);
        Assert.DoesNotContain(payload.Warnings, w => w.Contains("PluginLogLevel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateConfiguration_NullPluginLogLevel_NoWarning()
    {
        // Old clients that don't include the field at all must not trigger the warning.
        _config.PluginLogLevel = "INFO";
        var request = new ConfigurationUpdateRequest { PluginLogLevel = null };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("INFO", _config.PluginLogLevel);

        var payload = Assert.IsType<ConfigurationSaveResponse>(ok.Value);
        Assert.DoesNotContain(payload.Warnings, w => w.Contains("PluginLogLevel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateConfiguration_EmptyPluginLogLevel_DefaultsToInfo()
    {
        _config.PluginLogLevel = "";
        var request = new ConfigurationUpdateRequest { PluginLogLevel = "" };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("INFO", _config.PluginLogLevel);
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
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "Radarr-1", Url = "http://r1:7878", ApiKey = "key1" },
                new ArrInstanceConfig { Name = "Radarr-2", Url = "http://r2:7878", ApiKey = "key2" },
                new ArrInstanceConfig { Name = "Radarr-3", Url = "http://r3:7878", ApiKey = "key3" }
            ]
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(3, _config.RadarrInstances.Count);
        Assert.Equal("Radarr-2", _config.RadarrInstances[1].Name);
        Assert.Equal("http://r3:7878", _config.RadarrInstances[2].Url);
    }

    [Fact]
    public async Task UpdateConfiguration_MultipleInstances_SurviveGetAfterSave()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "R1", Url = "http://r1:7878", ApiKey = "rk1" },
                new ArrInstanceConfig { Name = "R2", Url = "http://r2:7878", ApiKey = "rk2" },
                new ArrInstanceConfig { Name = "R3", Url = "http://r3:7878", ApiKey = "rk3" }
            ],
            SonarrInstances =
            [
                new ArrInstanceConfig { Name = "S1", Url = "http://s1:8989", ApiKey = "sk1" },
                new ArrInstanceConfig { Name = "S2", Url = "http://s2:8989", ApiKey = "sk2" }
            ]
        };
        await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var getResult = _controller.GetConfiguration();
        var okResult = Assert.IsType<OkObjectResult>(getResult.Result);
        var configResponse = Assert.IsType<ConfigurationResponse>(okResult.Value);
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true };
        var json = JsonSerializer.Serialize(configResponse, jsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("RadarrInstances", out var radarrArr), "JSON must contain RadarrInstances (PascalCase)");
        Assert.Equal(3, radarrArr.GetArrayLength());
        Assert.Equal("R2", radarrArr[1].GetProperty("Name").GetString());
        Assert.Equal("http://r3:7878", radarrArr[2].GetProperty("Url").GetString());
        Assert.True(root.TryGetProperty("SonarrInstances", out var sonarrArr), "JSON must contain SonarrInstances (PascalCase)");
        Assert.Equal(2, sonarrArr.GetArrayLength());
        Assert.Equal("S1", sonarrArr[0].GetProperty("Name").GetString());

        var restored = JsonSerializer.Deserialize<PluginConfiguration>(json, jsonOptions);
        Assert.NotNull(restored);
        // ConfigurationResponse uses IReadOnlyList<MaskedArrInstanceConfig>, not List<ArrInstanceConfig>,
        // so direct deserialization into PluginConfiguration will not populate instance lists.
        // Verify counts via the response object directly.
        Assert.Equal(3, configResponse.RadarrInstances.Count);
        Assert.Equal(2, configResponse.SonarrInstances.Count);
    }

    [Fact]
    public async Task UpdateConfiguration_UnreachableArr_SavesButReturnsWarnings()
    {
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync("http://r1:7878", "key1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Radarr v5.0"));
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync("http://r2:7878", "key2", It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "Connection refused"));

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "OK-Radarr", Url = "http://r1:7878", ApiKey = "key1" },
                new ArrInstanceConfig { Name = "Bad-Radarr", Url = "http://r2:7878", ApiKey = "key2" }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, _config.RadarrInstances.Count);

        var json = JsonSerializer.Serialize(okResult.Value);
        Assert.Contains("Bad-Radarr", json);
        Assert.Contains("not reachable", json);
    }

    // ===== UpdateLogLevel Tests =====

    [Fact]
    public void UpdateLogLevel_ValidLevel_PersistsAndReturnsOk()
    {
        var request = new LogLevelUpdateRequest { PluginLogLevel = "DEBUG" };
        var result = _controller.UpdateLogLevel(request);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("DEBUG", _config.PluginLogLevel);
        _configServiceMock.Verify(s => s.ReadAndMutate(It.IsAny<Action<PluginConfiguration>>()), Times.Once);
    }

    [Fact]
    public void UpdateLogLevel_EmptyLevel_DefaultsToInfo()
    {
        var request = new LogLevelUpdateRequest { PluginLogLevel = "" };
        var result = _controller.UpdateLogLevel(request);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("INFO", _config.PluginLogLevel);
    }

    [Fact]
    public void UpdateLogLevel_InvalidLevel_ReturnsBadRequest()
    {
        var request = new LogLevelUpdateRequest { PluginLogLevel = "TRACE" };
        var result = _controller.UpdateLogLevel(request);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void UpdateLogLevel_CaseInsensitive_NormalizesToUpperCase()
    {
        var request = new LogLevelUpdateRequest { PluginLogLevel = "warn" };
        var result = _controller.UpdateLogLevel(request);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("WARN", _config.PluginLogLevel);
    }

    [Fact]
    public async Task UpdateLogLevel_DoesNotAffectOtherSettings()
    {
        var configRequest = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 42,
            TrashRetentionDays = 15,
            PluginLogLevel = "INFO"
        };
        await _controller.UpdateConfigurationAsync(configRequest, CancellationToken.None);

        var logRequest = new LogLevelUpdateRequest { PluginLogLevel = "ERROR" };
        _controller.UpdateLogLevel(logRequest);

        Assert.Equal("ERROR", _config.PluginLogLevel);
        Assert.Equal(42, _config.OrphanMinAgeDays);
        Assert.Equal(15, _config.TrashRetentionDays);
    }

    [Fact]
    public void JsonRoundTrip_ConfigurationUpdateRequest_DeserializesMultipleInstances()
    {
        const string frontendJson = """
                                    {
                                        "IncludedLibraries": "",
                                        "ExcludedLibraries": "",
                                        "OrphanMinAgeDays": 0,
                                        "TrickplayTaskMode": "DryRun",
                                        "EmptyMediaFolderTaskMode": "DryRun",
                                        "OrphanedSubtitleTaskMode": "DryRun",
                                        "LinkRepairTaskMode": "DryRun",
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

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true };
        var request = JsonSerializer.Deserialize<ConfigurationUpdateRequest>(frontendJson, jsonOptions);

        Assert.NotNull(request);
        Assert.Equal(3, request.RadarrInstances.Count);
        Assert.Equal("Radarr-1", request.RadarrInstances[0].Name);
        Assert.Equal("Radarr-2", request.RadarrInstances[1].Name);
        Assert.Equal("Radarr-3", request.RadarrInstances[2].Name);
        Assert.Equal("http://r2:7878", request.RadarrInstances[1].Url);
        Assert.Equal("key3", request.RadarrInstances[2].ApiKey);
        Assert.Equal(TaskMode.DryRun, request.LinkRepairTaskMode);
    }

    [Fact]
    public async Task UpdateConfiguration_PluginNotInitialized_ReturnsBadRequest()
    {
        _configServiceMock.Setup(s => s.IsInitialized).Returns(false);
        var request = new ConfigurationUpdateRequest();
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void UpdateLogLevel_PluginNotInitialized_ReturnsBadRequest()
    {
        _configServiceMock.Setup(s => s.IsInitialized).Returns(false);
        var request = new LogLevelUpdateRequest { PluginLogLevel = "DEBUG" };
        var result = _controller.UpdateLogLevel(request);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetConfiguration_ApiKeysAreMasked()
    {
        // BUG GUARD: the GET endpoint must NEVER return plain-text API keys.
        // Any non-empty key must be replaced with the mask constant; only truly
        // empty keys (not yet configured) should come back as empty string.
        _config.SeerrApiKey = "real-seerr-secret";
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "R1", Url = "http://r:7878", ApiKey = "real-radarr-key" });
        _config.SonarrInstances.Add(new ArrInstanceConfig { Name = "S1", Url = "http://s:8989", ApiKey = string.Empty });

        var result = _controller.GetConfiguration();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ConfigurationResponse>(okResult.Value);

        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.SeerrApiKey);
        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.RadarrInstances[0].ApiKey);
        // Empty key stays empty — mask is only for keys that are set
        Assert.Equal(string.Empty, response.SonarrInstances[0].ApiKey);

        // Also assert the live config was NOT mutated
        Assert.Equal("real-seerr-secret", _config.SeerrApiKey);
    }

    [Fact]
    public void GetConfiguration_EmptySeerrKey_RemainsEmpty()
    {
        _config.SeerrApiKey = string.Empty;
        var result = _controller.GetConfiguration();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ConfigurationResponse>(okResult.Value);
        Assert.Equal(string.Empty, response.SeerrApiKey);
    }

    // ===== GetAvailableLibraries Tests =====

    [Fact]
    public void GetAvailableLibraries_FiltersOutMusicBoxsetsAndCollections()
    {
        // BUG GUARD: cleanup never processes music/boxsets, so the picker must not offer them.
        // Additionally, name-based fallback catches manually-created "Collection" folders that
        // slipped through the enum classification (e.g. legacy libraries migrated from older Jellyfin).
        var libraryManagerMock = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        libraryManagerMock.Setup(lm => lm.GetVirtualFolders()).Returns(
        [
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = "Movies",
                CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.movies
            },
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = "Music",
                CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.music
            },
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = "Boxsets",
                CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.boxsets
            },
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = "My Collection", // Name-based fallback
                CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.movies
            },
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = "TV Shows",
                CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.tvshows
            },
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = " ", // Whitespace name is filtered out
                CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.movies
            }
        ]);
        var localConfigHelper = new Mock<ICleanupConfigHelper>();
        localConfigHelper.Setup(h => h.GetConfig()).Returns(_config);
        var controller = new ConfigurationController(
            _arrServiceMock.Object,
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<ConfigurationController>>().Object,
            localConfigHelper.Object,
            _configServiceMock.Object,
            _seerrServiceMock.Object,
            libraryManagerMock.Object,
            new EnsembleScoringStrategy());

        var result = controller.GetAvailableLibraries();
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        // Reflect into the anonymous type
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Movies", json, StringComparison.Ordinal);
        Assert.Contains("TV Shows", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Music", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Boxsets", json, StringComparison.Ordinal);
        Assert.DoesNotContain("My Collection", json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetAvailableLibraries_ReturnsAlphabeticallySorted()
    {
        // UI ordering is deterministic so admins don't see libraries shuffled between page loads.
        var libraryManagerMock = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        libraryManagerMock.Setup(lm => lm.GetVirtualFolders()).Returns(
        [
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = "Zeta", CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.movies
            },
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = "Alpha", CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.movies
            },
            new MediaBrowser.Model.Entities.VirtualFolderInfo
            {
                Name = "beta", CollectionType = MediaBrowser.Model.Entities.CollectionTypeOptions.movies
            }
        ]);
        var localConfigHelper = new Mock<ICleanupConfigHelper>();
        localConfigHelper.Setup(h => h.GetConfig()).Returns(_config);
        var controller = new ConfigurationController(
            _arrServiceMock.Object,
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<ConfigurationController>>().Object,
            localConfigHelper.Object,
            _configServiceMock.Object,
            _seerrServiceMock.Object,
            libraryManagerMock.Object,
            new EnsembleScoringStrategy());

        var result = controller.GetAvailableLibraries();
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        // Alphabetical (case-insensitive): Alpha < beta < Zeta
        var alphaIdx = json.IndexOf("Alpha", StringComparison.Ordinal);
        var betaIdx = json.IndexOf("beta", StringComparison.Ordinal);
        var zetaIdx = json.IndexOf("Zeta", StringComparison.Ordinal);
        Assert.True(alphaIdx < betaIdx && betaIdx < zetaIdx,
            $"Expected Alpha < beta < Zeta but got positions {alphaIdx}, {betaIdx}, {zetaIdx}");
    }

    // ===== TestArrInstanceGroupAsync: Sonarr coverage =====

    [Fact]
    public async Task UpdateConfiguration_UnreachableSonarr_ReturnsWarning()
    {
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync("http://s1:8989", "sk1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "timeout"));

        var request = new ConfigurationUpdateRequest
        {
            SonarrInstances = [ new ArrInstanceConfig { Name = "BadSonarr", Url = "http://s1:8989", ApiKey = "sk1" } ]
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("BadSonarr", json);
        Assert.Contains("not reachable", json);
    }

    [Fact]
    public async Task UpdateConfiguration_ArrInstanceWithoutName_UsesGenericLabel()
    {
        // BUG GUARD: instances added without a name must still surface a meaningful label
        // ("Radarr #1", "Radarr #2" etc), not a blank string in the warning.
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync("http://r1:7878", "k1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "refused"));

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = string.Empty, Url = "http://r1:7878", ApiKey = "k1" }
            ]
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Radarr #1", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateConfiguration_ArrInstanceWithEmptyCredentials_IsSkipped()
    {
        // Instances without url or apiKey must not trigger a connection test at all —
        // otherwise every save produces a spurious "instance unreachable" warning for
        // partially-filled rows the admin hasn't finished configuring yet.
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "Empty", Url = string.Empty, ApiKey = string.Empty }
            ]
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        _arrServiceMock.Verify(
            s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateConfiguration_ArrTestThrows_ExceptionSurfacesAsWarning()
    {
        // Contract: HttpRequestException / TimeoutException must be caught and reported as
        // a warning — the config save must NOT fail because of unreachable Arr instances.
        _arrServiceMock
            .Setup(s => s.TestConnectionAsync("http://r1:7878", "k1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "Flaky", Url = "http://r1:7878", ApiKey = "k1" }
            ]
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Flaky", json, StringComparison.Ordinal);
        Assert.Contains("network down", json, StringComparison.Ordinal);
    }

    // ===== TestSeerrConnectionAsync coverage =====

    [Fact]
    public async Task UpdateConfiguration_SeerrConfigured_HappyPath_NoWarnings()
    {
        _seerrServiceMock
            .Setup(s => s.TestConnectionAsync("https://seerr.example.com", "seerr-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "Seerr v1.33"));

        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "  https://seerr.example.com  ",
            SeerrApiKey = "  seerr-key  ",
            SeerrCleanupAgeDays = 30
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        // No Seerr warnings should appear
        Assert.DoesNotContain("Seerr instance", json, StringComparison.Ordinal);
        // Values were trimmed before persistence
        Assert.Equal("https://seerr.example.com", _config.SeerrUrl);
        Assert.Equal("seerr-key", _config.SeerrApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_SeerrUnreachable_ReturnsWarning()
    {
        _seerrServiceMock
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, "invalid api key"));

        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = "wrong-key",
            SeerrCleanupAgeDays = 30
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Seerr", json, StringComparison.Ordinal);
        Assert.Contains("not reachable", json, StringComparison.Ordinal);
        Assert.Contains("invalid api key", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateConfiguration_SeerrTestThrows_ExceptionSurfacesAsWarning()
    {
        _seerrServiceMock
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("dns timeout"));

        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = "some-key",
            SeerrCleanupAgeDays = 30
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Seerr", json, StringComparison.Ordinal);
        Assert.Contains("dns timeout", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateConfiguration_NoSeerrCredentials_SkipsSeerrConnectionTest()
    {
        // Save with empty Seerr fields must not test Seerr, matching the Arr "skip empty" behaviour.
        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = string.Empty,
            SeerrApiKey = string.Empty
        };
        await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        _seerrServiceMock.Verify(
            s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ===== SeerrCleanupAgeDays clamp / disable =====

    [Fact]
    public async Task UpdateConfiguration_SeerrCleanupAgeDays_TooLarge_ReturnsBadRequest()
    {
        // BUG GUARD: The validator MUST reject SeerrCleanupAgeDays > 3650 with 400 (hard-fail),
        // NOT silently clamp. Silent clamping would let a malicious/buggy client persist
        // absurd retention values without any feedback that the input was clipped.
        _seerrServiceMock
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "OK"));

        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = "k",
            SeerrCleanupAgeDays = 99999
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("SeerrCleanupAgeDays", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateConfiguration_SeerrCleanupAgeDays_TooSmall_ReturnsBadRequest()
    {
        // Symmetric guard for the lower bound: 0 must be rejected when Seerr is configured
        // (the "disable" state is signalled by clearing SeerrUrl, not by setting cleanupAge=0).
        _seerrServiceMock
            .Setup(s => s.TestConnectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, "OK"));

        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = "k",
            SeerrCleanupAgeDays = 0
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("SeerrCleanupAgeDays", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateConfiguration_SeerrCleanupAgeDays_ClampedWhenSeerrDisabled_DoesNotValidate()
    {
        // When Seerr is disabled (no URL), the age validator MUST be skipped so that clients
        // sending a legacy non-zero age don't get a BadRequest — the code silently forces the
        // stored value to 0. This test locks the "validator skips when Seerr disabled" contract.
        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = string.Empty,
            SeerrApiKey = string.Empty,
            SeerrCleanupAgeDays = 99999
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, _config.SeerrCleanupAgeDays);
    }

    [Fact]
    public async Task UpdateConfiguration_SeerrCleanupAgeDays_ZeroedWhenSeerrDisabled()
    {
        // BUG GUARD: without a Seerr URL, the age setting is meaningless and MUST be forced
        // to 0 so the scheduled cleanup task can trivially detect the "disabled" state.
        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = string.Empty,
            SeerrApiKey = string.Empty,
            SeerrCleanupAgeDays = 30
        };
        await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.Equal(0, _config.SeerrCleanupAgeDays);
    }

    // ===== ApplyRequestToConfig edge cases =====

    [Fact]
    public async Task UpdateConfiguration_OrphanMinAgeDays_TooLarge_ReturnsBadRequest()
    {
        // BUG GUARD: validator rejects OrphanMinAgeDays > 3650 with 400 (hard-fail), not
        // silent clamp. Prevents absurd retention windows from being persisted without feedback.
        var request = new ConfigurationUpdateRequest { OrphanMinAgeDays = 999999 };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("OrphanMinAgeDays", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateConfiguration_OrphanMinAgeDays_AtBoundary3650_Accepted()
    {
        // Boundary test: exactly 3650 must be accepted (inclusive upper bound).
        var request = new ConfigurationUpdateRequest { OrphanMinAgeDays = 3650 };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(3650, _config.OrphanMinAgeDays);
    }

    [Fact]
    public async Task UpdateConfiguration_TrashRetentionDays_TooLarge_ReturnsBadRequest()
    {
        // Symmetric guard: TrashRetentionDays follows the same 0-3650 range as OrphanMinAgeDays.
        var request = new ConfigurationUpdateRequest { TrashRetentionDays = 4000 };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("TrashRetentionDays", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateConfiguration_NullTrashFolderPath_DefaultsToJellyfinTrash()
    {
        // Verifies the null-guard in ApplyRequestToConfig: `string.IsNullOrWhiteSpace(...)` handles
        // an explicit null gracefully by falling back to ".jellyfin-trash". `null!` suppresses the
        // nullable-analysis warning — the DTO property is non-nullable in the C# type system, but
        // production JSON payloads may legitimately deserialize as null.
        //
        // We seed a NON-default value first so a regression that bypasses ApplyRequestToConfig
        // (early return, wrong branch, etc.) cannot silently pass just because ".jellyfin-trash"
        // is also the constructor default.
        _config.TrashFolderPath = "custom-trash";
        var request = new ConfigurationUpdateRequest { TrashFolderPath = null! };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(".jellyfin-trash", _config.TrashFolderPath);
    }

    [Fact]
    public async Task UpdateConfiguration_WhitespaceLanguage_DefaultsToEnglish()
    {
        // Same rationale as the null-TrashFolderPath test above: seed a non-default value
        // ("de") so we can prove the whitespace payload actually reached the defaulting
        // branch instead of leaving the field untouched.
        _config.Language = "de";
        var request = new ConfigurationUpdateRequest { Language = "   " };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("en", _config.Language);
    }

    [Fact]
    public async Task UpdateConfiguration_InvalidPersistedLogLevel_SelfHealsToInfo()
    {
        // Legacy configs may have an unknown level (e.g. "TRACE" from an older schema).
        // ApplyRequestToConfig must normalise these to INFO instead of leaving garbage.
        _config.PluginLogLevel = "GARBAGE";
        var request = new ConfigurationUpdateRequest();
        await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.Equal("INFO", _config.PluginLogLevel);
    }

    [Fact]
    public async Task UpdateConfiguration_MixedCasePersistedLogLevel_NormalizedToUpperCase()
    {
        _config.PluginLogLevel = "warn";
        var request = new ConfigurationUpdateRequest();
        await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.Equal("WARN", _config.PluginLogLevel);
    }

    [Fact]
    public async Task UpdateConfiguration_UseTrashWithTraversalPath_ReturnsBadRequest()
    {
        // BUG GUARD: The validator MUST hard-reject a TrashFolderPath containing "." or ".."
        // segments — traversal is a real attack surface (fs escape) and must not save.
        var request = new ConfigurationUpdateRequest
        {
            UseTrash = true,
            TrashFolderPath = "../../etc"
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("..", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateConfiguration_UseTrashWithInvalidCharacters_ReturnsBadRequest()
    {
        // Invalid folder-name chars must be rejected with 400, not silently persisted.
        var request = new ConfigurationUpdateRequest
        {
            UseTrash = true,
            TrashFolderPath = "trash*folder"
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_UseTrashWithControlChars_ReturnsBadRequest()
    {
        var request = new ConfigurationUpdateRequest
        {
            UseTrash = true,
            TrashFolderPath = "trash\u0007folder"
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_UseTrashDisabledWithTraversalPath_ReturnsBadRequest()
    {
        // SEC: traversal sequences in TrashFolderPath are rejected even when UseTrash=false,
        // so a malicious path cannot be stored and activated later when trash is re-enabled.
        var request = new ConfigurationUpdateRequest
        {
            UseTrash = false,
            TrashFolderPath = "../../etc"
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_UseTrashDisabledWithSafePath_StillAccepts()
    {
        // Non-traversal paths are still accepted when UseTrash=false (path stored for future use).
        var request = new ConfigurationUpdateRequest
        {
            UseTrash = false,
            TrashFolderPath = ".jellyfin-trash"
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_UseTrashWithEmptyPath_ReturnsBadRequest()
    {
        // When trash is enabled, an empty path must be rejected — the feature has no default fallback.
        var request = new ConfigurationUpdateRequest
        {
            UseTrash = true,
            TrashFolderPath = "   " // whitespace-only
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_MaxRadarrInstancesExceeded_ReturnsBadRequest()
    {
        // BUG GUARD: the validator caps Radarr instances at 3 — beyond that the config UI
        // would be unwieldy and per-request overhead would grow linearly.
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "R1", Url = "http://r1:7878", ApiKey = "k1" },
                new ArrInstanceConfig { Name = "R2", Url = "http://r2:7878", ApiKey = "k2" },
                new ArrInstanceConfig { Name = "R3", Url = "http://r3:7878", ApiKey = "k3" },
                new ArrInstanceConfig { Name = "R4", Url = "http://r4:7878", ApiKey = "k4" }
            ]
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_ArrInstanceWithNonHttpUrl_ReturnsBadRequest()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "Bad", Url = "ftp://r1:7878", ApiKey = "k1" }
            ]
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_ArrInstanceUrlWithoutApiKey_ReturnsBadRequest()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "Naked", Url = "http://r1:7878", ApiKey = string.Empty }
            ]
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_SeerrUrlWithoutApiKey_ReturnsBadRequest()
    {
        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = string.Empty,
            SeerrCleanupAgeDays = 30
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateConfiguration_InvalidSeerrUrl_ReturnsBadRequest()
    {
        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "not-a-url",
            SeerrApiKey = "k",
            SeerrCleanupAgeDays = 30
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetConfiguration_NonEmptyKeys_ReturnsSentinelMask()
    {
        // BUG GUARD: GET must never return plain-text API keys.
        // Non-empty keys must be replaced with the sentinel mask "***".
        _config.SeerrApiKey = "real-seerr-secret";
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "R1", Url = "http://r:7878", ApiKey = "real-radarr-key" });
        _config.SonarrInstances.Add(new ArrInstanceConfig { Name = "S1", Url = "http://s:8989", ApiKey = "real-sonarr-key" });

        var result = _controller.GetConfiguration();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ConfigurationResponse>(okResult.Value);

        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.SeerrApiKey);
        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.RadarrInstances[0].ApiKey);
        Assert.Equal(ConfigurationResponse.ApiKeyMask, response.SonarrInstances[0].ApiKey);

        // Live config must not have been mutated by the masking
        Assert.Equal("real-seerr-secret", _config.SeerrApiKey);
        Assert.Equal("real-radarr-key", _config.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public void GetConfiguration_EmptyKeys_RemainEmpty()
    {
        // Empty (unconfigured) keys must come back as "" — not the sentinel — so the UI
        // can distinguish "key not yet set" from "key set but hidden".
        _config.SeerrApiKey = string.Empty;
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "R1", Url = "http://r:7878", ApiKey = string.Empty });

        var result = _controller.GetConfiguration();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ConfigurationResponse>(okResult.Value);

        Assert.Equal(string.Empty, response.SeerrApiKey);
        Assert.Equal(string.Empty, response.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_SentinelSeerrApiKey_PreservesStoredKey()
    {
        // Contract: when the client echoes "***" for SeerrApiKey the POST must leave
        // the real stored key untouched. This is the round-trip case: GET → UI shows "***"
        // → user saves without changing the key → POST receives "***" → key must not change.
        _config.SeerrApiKey = "original-secret";
        _config.SeerrUrl = "https://seerr.example.com";

        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = ConfigurationResponse.ApiKeyMask, // echoed sentinel
            SeerrCleanupAgeDays = 30
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        // The real key must be preserved
        Assert.Equal("original-secret", _config.SeerrApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_RealSeerrApiKey_OverwritesStoredKey()
    {
        // Complementary case: a genuine new key value (not the sentinel) must overwrite
        // the stored key so the user can update credentials.
        _config.SeerrApiKey = "old-secret";
        _config.SeerrUrl = "https://seerr.example.com";

        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = "brand-new-key",
            SeerrCleanupAgeDays = 30
        };
        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Equal("brand-new-key", _config.SeerrApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_SentinelRadarrApiKey_PreservesStoredKey()
    {
        // Contract: GET masks Radarr keys as "***". When the user saves Settings without
        // touching the key field the browser echoes "***" back. The POST must leave the
        // real stored key untouched — identical to the Seerr sentinel contract.
        _config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "R1",
            Url = "http://radarr:7878",
            ApiKey = "original-radarr-secret"
        });

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig
                {
                    Name = "R1",
                    Url = "http://radarr:7878",
                    ApiKey = ConfigurationResponse.ApiKeyMask // echoed sentinel
                }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Single(_config.RadarrInstances);
        Assert.Equal("original-radarr-secret", _config.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_SentinelSonarrApiKey_PreservesStoredKey()
    {
        // Same round-trip contract as Radarr, but for Sonarr.
        _config.SonarrInstances.Add(new ArrInstanceConfig
        {
            Name = "S1",
            Url = "http://sonarr:8989",
            ApiKey = "original-sonarr-secret"
        });

        var request = new ConfigurationUpdateRequest
        {
            SonarrInstances =
            [
                new ArrInstanceConfig
                {
                    Name = "S1",
                    Url = "http://sonarr:8989",
                    ApiKey = ConfigurationResponse.ApiKeyMask // echoed sentinel
                }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Single(_config.SonarrInstances);
        Assert.Equal("original-sonarr-secret", _config.SonarrInstances[0].ApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_RealRadarrApiKey_OverwritesStoredKey()
    {
        // Complementary case: a genuine new key (not the sentinel) must overwrite the stored key.
        _config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "R1",
            Url = "http://radarr:7878",
            ApiKey = "old-radarr-key"
        });

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig
                {
                    Name = "R1",
                    Url = "http://radarr:7878",
                    ApiKey = "brand-new-radarr-key"
                }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Equal("brand-new-radarr-key", _config.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_SentinelArrApiKey_MultipleInstances_PreservesAllStoredKeys()
    {
        // Edge case: multiple instances of the same type — each sentinel must resolve
        // to its own stored key, not bleed across instances or collapse to empty.
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "R1", Url = "http://r1:7878", ApiKey = "key-r1" });
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "R2", Url = "http://r2:7878", ApiKey = "key-r2" });

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "R1", Url = "http://r1:7878", ApiKey = ConfigurationResponse.ApiKeyMask },
                new ArrInstanceConfig { Name = "R2", Url = "http://r2:7878", ApiKey = ConfigurationResponse.ApiKeyMask }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Equal(2, _config.RadarrInstances.Count);
        Assert.Equal("key-r1", _config.RadarrInstances[0].ApiKey);
        Assert.Equal("key-r2", _config.RadarrInstances[1].ApiKey);
    }

    // ===== Diagnostic logging for rejected saves =====

    // Model-binding diagnostics (invalid ModelState / null request body) are exercised in
    // ModelBindingLogFilterTests. Those failures are handled by ModelBindingLogFilter which runs
    // *before* the action method — driving them through UpdateConfigurationAsync() directly (as
    // this test file did originally) bypasses the MVC pipeline and gives a false-positive green
    // even when the production code path is broken. See ModelBindingLogFilterTests for the real
    // contract.

    /// <summary>
    ///     Validator-level errors (as opposed to model-binding
    ///     errors) must also produce a plugin-log entry. Previously the response
    ///     carried a helpful message but the log stayed silent, which meant a user
    ///     running with debug logging still couldn't see the rejection reason.
    /// </summary>
    [Fact]
    public async Task UpdateConfiguration_ValidatorRejects_LogsWarning()
    {
        var request = new ConfigurationUpdateRequest { OrphanMinAgeDays = -5 };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);

        _pluginLogMock.Verify(
            l => l.LogWarning(
                "API",
                It.Is<string>(msg => msg.Contains("validation rejected", System.StringComparison.OrdinalIgnoreCase)),
                It.IsAny<System.Exception?>(),
                It.IsAny<ILogger?>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateConfiguration_SeerrApiKey_Sentinel_DoesNotCallTestConnection()
    {
        _config.SeerrApiKey = "real-stored-key";
        _config.SeerrUrl = "https://seerr.example.com";

        var request = new ConfigurationUpdateRequest
        {
            SeerrUrl = "https://seerr.example.com",
            SeerrApiKey = "***",
            SeerrCleanupAgeDays = 30
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _seerrServiceMock.Verify(
            s => s.TestConnectionAsync(It.IsAny<string>(), "***", It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal("real-stored-key", _config.SeerrApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_ArrApiKey_Sentinel_DoesNotCallTestConnection()
    {
        _config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Radarr", Url = "http://radarr.local", ApiKey = "real-radarr-key"
        });

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = [new ArrInstanceConfig
            {
                Name = "Radarr", Url = "http://radarr.local", ApiKey = "***"
            }]
        };

        await _controller.UpdateConfigurationAsync(request, CancellationToken.None);

        _arrServiceMock.Verify(
            s => s.TestConnectionAsync(It.IsAny<string>(), "***", It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal("real-radarr-key", _config.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_SentinelRadarrApiKey_ReorderedInstances_ResolvesKeyByNameUrl()
    {
        // Sentinel restoration must match by Name+Url, not position.
        // When the admin reorders instances while leaving keys masked, each instance must
        // get back its OWN stored key — not the key at its previous positional index.
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "A", Url = "http://a:7878", ApiKey = "key-A" });
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "B", Url = "http://b:7878", ApiKey = "key-B" });

        // Admin reorders: B comes first, A comes second
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "B", Url = "http://b:7878", ApiKey = ConfigurationResponse.ApiKeyMask },
                new ArrInstanceConfig { Name = "A", Url = "http://a:7878", ApiKey = ConfigurationResponse.ApiKeyMask }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Equal(2, _config.RadarrInstances.Count);
        Assert.Equal("key-B", _config.RadarrInstances[0].ApiKey);
        Assert.Equal("key-A", _config.RadarrInstances[1].ApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_SentinelRadarrApiKey_RemovedInstance_SurvivorKeepsOwnKey()
    {
        // When the admin removes instance[0] and leaves instance[1] masked,
        // the positional approach would restore the key of the removed instance into the surviving one.
        // The Name+Url approach correctly gives the surviving instance its own key.
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "Removed", Url = "http://removed:7878", ApiKey = "key-removed" });
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "Kept", Url = "http://kept:7878", ApiKey = "key-kept" });

        // Admin removes "Removed", saves with "Kept" still masked
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "Kept", Url = "http://kept:7878", ApiKey = ConfigurationResponse.ApiKeyMask }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Single(_config.RadarrInstances);
        Assert.Equal("key-kept", _config.RadarrInstances[0].ApiKey);
    }

    [Fact]
    public async Task UpdateConfiguration_SentinelSonarrApiKey_ReorderedInstances_ResolvesKeyByNameUrl()
    {
        // Same Name+Url matching contract for Sonarr.
        _config.SonarrInstances.Add(new ArrInstanceConfig { Name = "X", Url = "http://x:8989", ApiKey = "key-X" });
        _config.SonarrInstances.Add(new ArrInstanceConfig { Name = "Y", Url = "http://y:8989", ApiKey = "key-Y" });

        var request = new ConfigurationUpdateRequest
        {
            SonarrInstances =
            [
                new ArrInstanceConfig { Name = "Y", Url = "http://y:8989", ApiKey = ConfigurationResponse.ApiKeyMask },
                new ArrInstanceConfig { Name = "X", Url = "http://x:8989", ApiKey = ConfigurationResponse.ApiKeyMask }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Equal(2, _config.SonarrInstances.Count);
        Assert.Equal("key-Y", _config.SonarrInstances[0].ApiKey);
        Assert.Equal("key-X", _config.SonarrInstances[1].ApiKey);
    }

    /// <summary>
    ///     When the admin renames a Radarr instance (Name changes)
    ///     but keeps the same URL, and the client echoes the sentinel "***" for the key, the
    ///     stored key must be preserved. The lookup matches by URL, so a rename alone must not
    ///     clear the API key.
    /// </summary>
    [Fact]
    public async Task ApplyRequestToConfig_RenameRadarrInstance_WithSentinel_PreservesApiKey()
    {
        _config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Main Radarr",
            Url = "http://radarr:7878",
            ApiKey = "real-secret-key"
        });

        // Name changed but URL unchanged — URL-only fallback must restore the key so the
        // admin does not have to re-enter it after a rename.
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig
                {
                    Name = "Primary Radarr",
                    Url = "http://radarr:7878",
                    ApiKey = ConfigurationResponse.ApiKeyMask
                }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Single(_config.RadarrInstances);
        Assert.Equal("real-secret-key", _config.RadarrInstances[0].ApiKey);
        Assert.Equal("Primary Radarr", _config.RadarrInstances[0].Name);
    }

    /// <summary>
    ///     Same rename-with-sentinel contract for Sonarr.
    /// </summary>
    [Fact]
    public async Task ApplyRequestToConfig_RenameSonarrInstance_WithSentinel_PreservesApiKey()
    {
        _config.SonarrInstances.Add(new ArrInstanceConfig
        {
            Name = "Main Sonarr",
            Url = "http://sonarr:8989",
            ApiKey = "sonarr-secret-key"
        });

        // Same contract as Radarr: URL-only fallback preserves the key after a rename.
        var request = new ConfigurationUpdateRequest
        {
            SonarrInstances =
            [
                new ArrInstanceConfig
                {
                    Name = "Primary Sonarr",
                    Url = "http://sonarr:8989",
                    ApiKey = ConfigurationResponse.ApiKeyMask
                }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Single(_config.SonarrInstances);
        Assert.Equal("sonarr-secret-key", _config.SonarrInstances[0].ApiKey);
        Assert.Equal("Primary Sonarr", _config.SonarrInstances[0].Name);
    }

    /// <summary>
    ///     When the URL changes, the sentinel
    ///     cannot find a prior match and must NOT restore a stale key — the result must be an
    ///     empty API key, signalling that the new instance needs its real key supplied.
    /// </summary>
    [Fact]
    public async Task ApplyRequestToConfig_ChangeRadarrUrl_WithSentinel_ClearsApiKey()
    {
        _config.RadarrInstances.Add(new ArrInstanceConfig
        {
            Name = "R1",
            Url = "http://old:7878",
            ApiKey = "real-key"
        });

        // URL changed: no prior entry at the new URL, so sentinel cannot restore a key.
        // The validator rejects a URL-present + empty-key combo, so supply a real key here
        // to let the request through validation — then assert the stored key was not the sentinel.
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig
                {
                    Name = "R1",
                    Url = "http://new:7878", // URL changed — no prior entry at this URL
                    ApiKey = ConfigurationResponse.ApiKeyMask
                }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Single(_config.RadarrInstances);
        // No prior instance at http://new:7878, so the sentinel resolves to empty string.
        Assert.Equal(string.Empty, _config.RadarrInstances[0].ApiKey);
    }

    // TEST-6: Two Radarr instances share the same URL but have different names.
    // The sentinel must restore each instance's own key — not always the first match.
    [Fact]
    public async Task ApplyRequestToConfig_TwoRadarrInstancesSameUrl_SentinelRestoresCorrectKey()
    {
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "Primary",   Url = "http://radarr:7878", ApiKey = "key-primary" });
        _config.RadarrInstances.Add(new ArrInstanceConfig { Name = "Secondary", Url = "http://radarr:7878", ApiKey = "key-secondary" });

        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances =
            [
                new ArrInstanceConfig { Name = "Primary",   Url = "http://radarr:7878", ApiKey = ConfigurationResponse.ApiKeyMask },
                new ArrInstanceConfig { Name = "Secondary", Url = "http://radarr:7878", ApiKey = ConfigurationResponse.ApiKeyMask }
            ]
        };

        var result = await _controller.UpdateConfigurationAsync(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);

        Assert.Equal(2, _config.RadarrInstances.Count);
        var primary   = _config.RadarrInstances.First(i => i.Name == "Primary");
        var secondary = _config.RadarrInstances.First(i => i.Name == "Secondary");
        Assert.Equal("key-primary",   primary.ApiKey);
        Assert.Equal("key-secondary", secondary.ApiKey);
    }
}
