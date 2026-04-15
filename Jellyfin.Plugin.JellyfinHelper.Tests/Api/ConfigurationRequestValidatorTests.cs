using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
/// Unit tests for <see cref="ConfigurationRequestValidator"/>.
/// </summary>
public class ConfigurationRequestValidatorTests
{
    [Fact]
    public void Validate_ValidRequest_ReturnsNull()
    {
        var request = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 30,
            TrashRetentionDays = 60,
        };

        Assert.Null(ConfigurationRequestValidator.Validate(request));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    public void Validate_InvalidOrphanMinAgeDays_ReturnsError(int days)
    {
        var request = new ConfigurationUpdateRequest { OrphanMinAgeDays = days };

        var error = ConfigurationRequestValidator.Validate(request);

        Assert.NotNull(error);
        Assert.Contains("OrphanMinAgeDays", error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    public void Validate_InvalidTrashRetentionDays_ReturnsError(int days)
    {
        var request = new ConfigurationUpdateRequest { TrashRetentionDays = days };

        var error = ConfigurationRequestValidator.Validate(request);

        Assert.NotNull(error);
        Assert.Contains("TrashRetentionDays", error);
    }

    [Fact]
    public void Validate_TooManyRadarrInstances_ReturnsError()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = new List<ArrInstanceConfig>
            {
                new() { Name = "R1", Url = "http://r1", ApiKey = "k1" },
                new() { Name = "R2", Url = "http://r2", ApiKey = "k2" },
                new() { Name = "R3", Url = "http://r3", ApiKey = "k3" },
                new() { Name = "R4", Url = "http://r4", ApiKey = "k4" },
            },
        };

        var error = ConfigurationRequestValidator.Validate(request);

        Assert.NotNull(error);
        Assert.Contains("Radarr", error);
    }

    [Fact]
    public void Validate_TooManySonarrInstances_ReturnsError()
    {
        var request = new ConfigurationUpdateRequest
        {
            SonarrInstances = new List<ArrInstanceConfig>
            {
                new() { Name = "S1", Url = "http://s1", ApiKey = "k1" },
                new() { Name = "S2", Url = "http://s2", ApiKey = "k2" },
                new() { Name = "S3", Url = "http://s3", ApiKey = "k3" },
                new() { Name = "S4", Url = "http://s4", ApiKey = "k4" },
            },
        };

        var error = ConfigurationRequestValidator.Validate(request);

        Assert.NotNull(error);
        Assert.Contains("Sonarr", error);
    }

    [Fact]
    public void Validate_InvalidArrUrl_ReturnsError()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = new List<ArrInstanceConfig>
            {
                new() { Name = "Bad", Url = "ftp://invalid", ApiKey = "key123" },
            },
        };

        var error = ConfigurationRequestValidator.Validate(request);

        Assert.NotNull(error);
        Assert.Contains("invalid URL", error);
    }

    [Fact]
    public void Validate_ArrUrlWithoutApiKey_ReturnsError()
    {
        var request = new ConfigurationUpdateRequest
        {
            SonarrInstances = new List<ArrInstanceConfig>
            {
                new() { Name = "NoKey", Url = "http://sonarr:8989", ApiKey = "" },
            },
        };

        var error = ConfigurationRequestValidator.Validate(request);

        Assert.NotNull(error);
        Assert.Contains("no API key", error);
    }

    [Fact]
    public void Validate_EmptyArrInstance_IsSkipped()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = new List<ArrInstanceConfig>
            {
                new() { Name = "", Url = "", ApiKey = "" },
            },
        };

        Assert.Null(ConfigurationRequestValidator.Validate(request));
    }

    [Fact]
    public void Validate_LegacyRadarrUrl_InvalidFormat_ReturnsError()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = null!,
            RadarrUrl = "not-a-url",
            RadarrApiKey = "key123",
        };

        var error = ConfigurationRequestValidator.Validate(request);

        Assert.NotNull(error);
        Assert.Contains("invalid URL", error);
    }

    [Fact]
    public void Validate_LegacySonarrUrl_MissingApiKey_ReturnsError()
    {
        var request = new ConfigurationUpdateRequest
        {
            SonarrInstances = null!,
            SonarrUrl = "http://sonarr:8989",
            SonarrApiKey = "",
        };

        var error = ConfigurationRequestValidator.Validate(request);

        Assert.NotNull(error);
        Assert.Contains("no API key", error);
    }

    [Fact]
    public void Validate_LegacyFieldsIgnored_WhenInstanceListProvided()
    {
        var request = new ConfigurationUpdateRequest
        {
            RadarrInstances = new List<ArrInstanceConfig>
            {
                new() { Name = "R1", Url = "http://radarr:7878", ApiKey = "key" },
            },
            RadarrUrl = "not-a-url",
            RadarrApiKey = "",
        };

        // Legacy fields should be ignored because RadarrInstances is provided
        Assert.Null(ConfigurationRequestValidator.Validate(request));
    }

    [Fact]
    public void Validate_BoundaryValues_AreAccepted()
    {
        var request = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 0,
            TrashRetentionDays = 3650,
        };

        Assert.Null(ConfigurationRequestValidator.Validate(request));
    }

    [Fact]
    public void ValidateArrInstances_ValidInstances_ReturnsNull()
    {
        var instances = new List<ArrInstanceConfig>
        {
            new() { Name = "Test", Url = "http://localhost:7878", ApiKey = "abc123" },
            new() { Name = "Test2", Url = "https://radarr.local", ApiKey = "def456" },
        };

        Assert.Null(ConfigurationRequestValidator.ValidateArrInstances(instances, "Radarr"));
    }

    [Fact]
    public void ValidateArrInstances_ThreeInstances_ReturnsNull()
    {
        var instances = new List<ArrInstanceConfig>
        {
            new() { Name = "R1", Url = "http://r1:7878", ApiKey = "k1" },
            new() { Name = "R2", Url = "http://r2:7878", ApiKey = "k2" },
            new() { Name = "R3", Url = "http://r3:7878", ApiKey = "k3" },
        };

        Assert.Null(ConfigurationRequestValidator.ValidateArrInstances(instances, "Radarr"));
    }
}