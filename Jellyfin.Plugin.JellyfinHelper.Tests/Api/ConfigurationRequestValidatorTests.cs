using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

public class ConfigurationRequestValidatorTests
{
    // ===== OrphanMinAgeDays =====

    [Fact]
    public void Validate_ReturnsNull_ForValidRequest()
    {
        var req = new ConfigurationUpdateRequest { OrphanMinAgeDays = 7, TrashRetentionDays = 30 };
        Assert.Null(ConfigurationRequestValidator.Validate(req));
    }

    [Fact]
    public void Validate_ReturnsError_WhenOrphanMinAgeDaysNegative()
    {
        var req = new ConfigurationUpdateRequest { OrphanMinAgeDays = -1, TrashRetentionDays = 30 };
        Assert.Contains("OrphanMinAgeDays", ConfigurationRequestValidator.Validate(req)!);
    }

    [Fact]
    public void Validate_ReturnsError_WhenOrphanMinAgeDaysTooLarge()
    {
        var req = new ConfigurationUpdateRequest { OrphanMinAgeDays = 3651, TrashRetentionDays = 30 };
        Assert.NotNull(ConfigurationRequestValidator.Validate(req));
    }

    // ===== TrashRetentionDays =====

    [Fact]
    public void Validate_ReturnsError_WhenTrashRetentionDaysNegative()
    {
        var req = new ConfigurationUpdateRequest { OrphanMinAgeDays = 7, TrashRetentionDays = -1 };
        Assert.Contains("TrashRetentionDays", ConfigurationRequestValidator.Validate(req)!);
    }

    [Fact]
    public void Validate_ReturnsError_WhenTrashRetentionDaysTooLarge()
    {
        var req = new ConfigurationUpdateRequest { OrphanMinAgeDays = 7, TrashRetentionDays = 5000 };
        Assert.NotNull(ConfigurationRequestValidator.Validate(req));
    }

    // ===== Arr Instance Limits =====

    [Fact]
    public void Validate_ReturnsError_WhenTooManyRadarrInstances()
    {
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            RadarrInstances = new List<ArrInstanceConfig>
            {
                new() { Url = "http://a", ApiKey = "k" },
                new() { Url = "http://b", ApiKey = "k" },
                new() { Url = "http://c", ApiKey = "k" },
                new() { Url = "http://d", ApiKey = "k" },
            }
        };
        Assert.Contains("Radarr", ConfigurationRequestValidator.Validate(req)!);
    }

    [Fact]
    public void Validate_ReturnsError_WhenTooManySonarrInstances()
    {
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            SonarrInstances = new List<ArrInstanceConfig>
            {
                new() { Url = "http://a", ApiKey = "k" },
                new() { Url = "http://b", ApiKey = "k" },
                new() { Url = "http://c", ApiKey = "k" },
                new() { Url = "http://d", ApiKey = "k" },
            }
        };
        Assert.Contains("Sonarr", ConfigurationRequestValidator.Validate(req)!);
    }

    // ===== Seerr Validation =====

    [Fact]
    public void Validate_ReturnsError_WhenSeerrCleanupAgeDaysTooLow()
    {
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            SeerrUrl = "http://seerr.local",
            SeerrApiKey = "key",
            SeerrCleanupAgeDays = 0
        };
        Assert.Contains("SeerrCleanupAgeDays", ConfigurationRequestValidator.Validate(req)!);
    }

    [Fact]
    public void Validate_ReturnsError_WhenSeerrCleanupAgeDaysTooHigh()
    {
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            SeerrUrl = "http://seerr.local",
            SeerrApiKey = "key",
            SeerrCleanupAgeDays = 5000
        };
        Assert.NotNull(ConfigurationRequestValidator.Validate(req));
    }

    [Fact]
    public void Validate_ReturnsError_WhenSeerrUrlInvalid()
    {
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            SeerrUrl = "ftp://invalid",
            SeerrApiKey = "key"
        };
        Assert.Contains("Seerr URL", ConfigurationRequestValidator.Validate(req)!);
    }

    [Fact]
    public void Validate_ReturnsError_WhenSeerrUrlSetButNoApiKey()
    {
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            SeerrUrl = "http://seerr.local",
            SeerrApiKey = ""
        };
        Assert.Contains("API key", ConfigurationRequestValidator.Validate(req)!);
    }

    [Fact]
    public void Validate_NoSeerrError_WhenSeerrUrlBlank()
    {
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            SeerrUrl = "",
            SeerrCleanupAgeDays = 0
        };
        Assert.Null(ConfigurationRequestValidator.Validate(req));
    }

    // ===== Arr Instance Validation =====

    [Fact]
    public void ValidateArrInstances_ReturnsNull_WhenNull()
    {
        Assert.Null(ConfigurationRequestValidator.ValidateArrInstances(null, "Radarr"));
    }

    [Fact]
    public void ValidateArrInstances_SkipsEmptyInstances()
    {
        var instances = new List<ArrInstanceConfig> { new() { Url = "", ApiKey = "" } };
        Assert.Null(ConfigurationRequestValidator.ValidateArrInstances(instances, "Radarr"));
    }

    [Fact]
    public void ValidateArrInstances_ReturnsError_ForInvalidUrl()
    {
        var instances = new List<ArrInstanceConfig> { new() { Url = "not-a-url", ApiKey = "key", Name = "Test" } };
        var error = ConfigurationRequestValidator.ValidateArrInstances(instances, "Radarr");
        Assert.Contains("Test", error!);
        Assert.Contains("invalid URL", error!);
    }

    [Fact]
    public void ValidateArrInstances_ReturnsError_ForInvalidUrl_WithoutName()
    {
        var instances = new List<ArrInstanceConfig> { new() { Url = "ftp://bad", ApiKey = "key" } };
        var error = ConfigurationRequestValidator.ValidateArrInstances(instances, "Sonarr");
        Assert.Contains("#1", error!);
    }

    [Fact]
    public void ValidateArrInstances_ReturnsError_WhenUrlSetButNoApiKey()
    {
        var instances = new List<ArrInstanceConfig> { new() { Url = "http://valid.local", ApiKey = "", Name = "MyArr" } };
        var error = ConfigurationRequestValidator.ValidateArrInstances(instances, "Radarr");
        Assert.Contains("MyArr", error!);
        Assert.Contains("no API key", error!);
    }

    [Fact]
    public void ValidateArrInstances_ReturnsError_WhenUrlSetButNoApiKey_WithoutName()
    {
        var instances = new List<ArrInstanceConfig> { new() { Url = "http://valid.local", ApiKey = "" } };
        var error = ConfigurationRequestValidator.ValidateArrInstances(instances, "Radarr");
        Assert.Contains("#1", error!);
    }

    [Fact]
    public void ValidateArrInstances_ReturnsNull_WhenAllValid()
    {
        var instances = new List<ArrInstanceConfig> { new() { Url = "http://radarr.local", ApiKey = "key123", Name = "Main" } };
        Assert.Null(ConfigurationRequestValidator.ValidateArrInstances(instances, "Radarr"));
    }
}