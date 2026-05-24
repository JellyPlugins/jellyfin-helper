using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Regression tests for bugs fixed in v2.1.0.3:
///     - ServerId=0 causing 400 Bad Request (Seerr uses 0-based server IDs)
///     - Duplicate quality profiles in popup when multiple root folders exist
///     - MissingMethodException crashing the scheduled task
/// </summary>
public sealed class DiscoveryRegressionTests
{
    // ===========================================================================================
    // Issue 3: ServerId=0 must be valid (Seerr uses 0-based IDs)
    // ===========================================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    public void DiscoveryRequestDto_ServerId_AcceptsZeroAndPositiveValues(int serverId)
    {
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 123,
            MediaType = "movie",
            ServerId = serverId,
            ProfileId = 1
        };

        var results = ValidateModel(dto);

        Assert.DoesNotContain(results, r =>
            r.MemberNames.Contains("ServerId"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void DiscoveryRequestDto_ServerId_RejectsNegativeValues(int serverId)
    {
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 123,
            MediaType = "movie",
            ServerId = serverId,
            ProfileId = 1
        };

        var results = ValidateModel(dto);

        Assert.Contains(results, r =>
            r.MemberNames.Contains("ServerId"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(11)]
    public void DiscoveryRequestDto_ProfileId_AcceptsZeroAndPositiveValues(int profileId)
    {
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 123,
            MediaType = "movie",
            ServerId = 0,
            ProfileId = profileId
        };

        var results = ValidateModel(dto);

        Assert.DoesNotContain(results, r =>
            r.MemberNames.Contains("ProfileId"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-50)]
    public void DiscoveryRequestDto_ProfileId_RejectsNegativeValues(int profileId)
    {
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 123,
            MediaType = "movie",
            ServerId = 0,
            ProfileId = profileId
        };

        var results = ValidateModel(dto);

        Assert.Contains(results, r =>
            r.MemberNames.Contains("ProfileId"));
    }

    [Fact]
    public void DiscoveryRequestDto_ServerIdNull_PassesValidation()
    {
        var dto = new DiscoveryRequestDto
        {
            TmdbId = 123,
            MediaType = "movie",
            ServerId = null,
            ProfileId = null
        };

        var results = ValidateModel(dto);

        Assert.DoesNotContain(results, r =>
            r.MemberNames.Contains("ServerId"));
        Assert.DoesNotContain(results, r =>
            r.MemberNames.Contains("ProfileId"));
    }

    // ===========================================================================================
    // Issue 2: Duplicate quality profiles when multiple root folders exist
    // ===========================================================================================

    [Fact]
    public void BuildServiceInfoFromProfiles_DeduplicatesProfilesByProfileId()
    {
        // Simulate: Server with 2 root folders and 3 profiles
        // BuildAllowedProfileList would emit 6 entries (3 profiles × 2 root folders)
        var allowedProfiles = new List<AllowedQualityProfile>
        {
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 6, ProfileName = "HD-1080p", IsDefault = true, RootFolder = "/movies/hd" },
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 6, ProfileName = "HD-1080p", IsDefault = false, RootFolder = "/movies/uhd" },
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 7, ProfileName = "Ultra-HD", IsDefault = false, RootFolder = "/movies/hd" },
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 7, ProfileName = "Ultra-HD", IsDefault = false, RootFolder = "/movies/uhd" },
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 4, ProfileName = "Any", IsDefault = false, RootFolder = "/movies/hd" },
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 4, ProfileName = "Any", IsDefault = false, RootFolder = "/movies/uhd" },
        };

        // Use reflection to test the private static method
        var method = typeof(UserDiscoveryController).GetMethod(
            "BuildServiceInfoFromProfiles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (List<SeerrServiceInfo>)method!.Invoke(null, new object[] { (IReadOnlyList<AllowedQualityProfile>)allowedProfiles })!;

        Assert.Single(result); // One server
        var server = result[0];

        // MUST have exactly 3 unique profiles (not 6 duplicates!)
        Assert.Equal(3, server.Profiles.Count);
        Assert.Contains(server.Profiles, p => p.Id == 6 && p.Name == "HD-1080p");
        Assert.Contains(server.Profiles, p => p.Id == 7 && p.Name == "Ultra-HD");
        Assert.Contains(server.Profiles, p => p.Id == 4 && p.Name == "Any");

        // Root folders should be correctly deduplicated too
        Assert.Equal(2, server.RootFolders.Count);
        Assert.Contains(server.RootFolders, rf => rf.Path == "/movies/hd");
        Assert.Contains(server.RootFolders, rf => rf.Path == "/movies/uhd");
    }

    [Fact]
    public void BuildServiceInfoFromProfiles_SingleRootFolder_NoProfileDuplicates()
    {
        // When only one root folder exists, profiles should never be duplicated
        var allowedProfiles = new List<AllowedQualityProfile>
        {
            new() { ServerId = 1, ServerName = "Sonarr", ProfileId = 1, ProfileName = "SD", IsDefault = false, RootFolder = "/tv" },
            new() { ServerId = 1, ServerName = "Sonarr", ProfileId = 2, ProfileName = "HD", IsDefault = true, RootFolder = "/tv" },
            new() { ServerId = 1, ServerName = "Sonarr", ProfileId = 3, ProfileName = "4K", IsDefault = false, RootFolder = "/tv" },
        };

        var method = typeof(UserDiscoveryController).GetMethod(
            "BuildServiceInfoFromProfiles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (List<SeerrServiceInfo>)method!.Invoke(null, new object[] { (IReadOnlyList<AllowedQualityProfile>)allowedProfiles })!;

        Assert.Single(result);
        Assert.Equal(3, result[0].Profiles.Count);
    }

    [Fact]
    public void BuildServiceInfoFromProfiles_MultipleServers_EachDeduplicatedIndependently()
    {
        // Two servers, each with 2 root folders and 2 profiles
        var allowedProfiles = new List<AllowedQualityProfile>
        {
            // Server 0 (Radarr)
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 1, ProfileName = "HD", IsDefault = true, RootFolder = "/movies/a" },
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 1, ProfileName = "HD", IsDefault = false, RootFolder = "/movies/b" },
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 2, ProfileName = "4K", IsDefault = false, RootFolder = "/movies/a" },
            new() { ServerId = 0, ServerName = "Radarr", ProfileId = 2, ProfileName = "4K", IsDefault = false, RootFolder = "/movies/b" },
            // Server 1 (Radarr Anime)
            new() { ServerId = 1, ServerName = "Radarr Anime", ProfileId = 5, ProfileName = "Anime", IsDefault = true, RootFolder = "/anime/a" },
            new() { ServerId = 1, ServerName = "Radarr Anime", ProfileId = 5, ProfileName = "Anime", IsDefault = false, RootFolder = "/anime/b" },
            new() { ServerId = 1, ServerName = "Radarr Anime", ProfileId = 6, ProfileName = "Anime 4K", IsDefault = false, RootFolder = "/anime/a" },
            new() { ServerId = 1, ServerName = "Radarr Anime", ProfileId = 6, ProfileName = "Anime 4K", IsDefault = false, RootFolder = "/anime/b" },
        };

        var method = typeof(UserDiscoveryController).GetMethod(
            "BuildServiceInfoFromProfiles",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var result = (List<SeerrServiceInfo>)method!.Invoke(null, new object[] { (IReadOnlyList<AllowedQualityProfile>)allowedProfiles })!;

        Assert.Equal(2, result.Count);
        Assert.All(result, server => Assert.Equal(2, server.Profiles.Count));
        Assert.All(result, server => Assert.Equal(2, server.RootFolders.Count));
    }

    // ===========================================================================================
    // Issue 1: MissingMethodException in GetAllUserWatchProfiles should be handled gracefully
    // ===========================================================================================

    [Fact]
    public void GetAllUserWatchProfiles_MissingMethodException_ReturnsEmptyAndDoesNotThrow()
    {
        var mockLibraryManager = new Mock<ILibraryManager>();
        var mockUserManager = new Mock<IUserManager>();
        var mockUserDataManager = new Mock<IUserDataManager>();
        var mockPluginLog = new Mock<IPluginLogService>();
        var mockLogger = new Mock<ILogger<WatchHistoryService>>();

        // Simulate the MissingMethodException that occurs with incompatible Jellyfin versions
        mockUserManager
            .Setup(m => m.Users)
            .Throws(new MissingMethodException(
                "Method not found: 'IEnumerable`1<Jellyfin.Database.Implementations.Entities.User> " +
                "MediaBrowser.Controller.Library.IUserManager.get_Users()'."));

        var service = new WatchHistoryService(
            mockLibraryManager.Object,
            mockUserManager.Object,
            mockUserDataManager.Object,
            mockPluginLog.Object,
            mockLogger.Object);

        // Should NOT throw — must return empty collection gracefully
        var result = service.GetAllUserWatchProfiles();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetAllUserWatchProfiles_MissingMethodException_LogsWarning()
    {
        var mockLibraryManager = new Mock<ILibraryManager>();
        var mockUserManager = new Mock<IUserManager>();
        var mockUserDataManager = new Mock<IUserDataManager>();
        var mockPluginLog = new Mock<IPluginLogService>();
        var mockLogger = new Mock<ILogger<WatchHistoryService>>();

        mockUserManager
            .Setup(m => m.Users)
            .Throws(new MissingMethodException("Simulated incompatibility"));

        var service = new WatchHistoryService(
            mockLibraryManager.Object,
            mockUserManager.Object,
            mockUserDataManager.Object,
            mockPluginLog.Object,
            mockLogger.Object);

        service.GetAllUserWatchProfiles();

        // Verify that a warning was logged with the incompatibility message including ex.Message
        mockPluginLog.Verify(
            l => l.LogWarning(
                "WatchHistory",
                It.Is<string>(msg => msg.Contains("IUserManager API incompatible") && msg.Contains("Discovery skipped")),
                It.IsAny<Exception>(),
                It.IsAny<ILogger>()),
            Times.Once);
    }

    // ===========================================================================================
    // Helpers
    // ===========================================================================================

    private static List<ValidationResult> ValidateModel(object model)
    {
        var context = new ValidationContext(model);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results;
    }
}