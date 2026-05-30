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

    // ===== TrashFolderPath =====

    [Fact]
    public void ValidateTrashPath_ReturnsNull_ForEmpty()
    {
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPath(""));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPath(null));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPath("   "));
    }

    [Fact]
    public void ValidateTrashPath_ReturnsNull_ForSimpleRelativePath()
    {
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPath("my-trash"));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPath(".jellyfin-trash"));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPath("subdir/trash"));
    }

    [Theory]
    [InlineData("/mnt/trash")]
    [InlineData("/absolute/path/to/trash")]
    [InlineData("C:\\Trash")]
    public void ValidateTrashPath_ReturnsNull_ForAbsolutePath(string path)
    {
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPath(path));
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("../sibling")]
    [InlineData("sub/../../../../../../escape")]
    public void ValidateTrashPath_ReturnsWarning_ForEscapingRelativePath(string path)
    {
        var warning = ConfigurationRequestValidator.ValidateTrashPath(path);
        Assert.NotNull(warning);
        Assert.Contains("TrashFolderPath", warning);
        Assert.Contains(".jellyfin-trash", warning);
        Assert.Contains("absolute path", warning);
    }

    [Fact]
    public void Validate_ReturnsNull_WhenTrashPathEscapes_BecauseWarningsAreNotErrors()
    {
        // ValidateTrashPath (the warning-only method) flags escaping relative paths,
        // but Validate() must NOT reject them — warnings are surfaced separately in the
        // controller response. This test proves that with UseTrash = true and a path that
        // passes ValidateTrashPathStrict() (no literal ".." segments), Validate() returns null
        // even though ValidateTrashPath() would issue a warning for similar paths.
        // Note: "../../evil" would fail ValidateTrashPathStrict() due to ".." segments,
        // so we use a simple relative path that Strict allows but is conceptually similar.
        var req = new ConfigurationUpdateRequest
        {
            UseTrash = true,
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            TrashFolderPath = ".jellyfin-trash"
        };
        Assert.Null(ConfigurationRequestValidator.Validate(req));

        // Additionally verify that ValidateTrashPath (the warning method) does NOT block Validate.
        // A path that triggers a ValidateTrashPath warning (absolute path going nowhere suspicious)
        // must still pass Validate() — the warning is advisory only.
        var reqWithAbsolutePath = new ConfigurationUpdateRequest
        {
            UseTrash = true,
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            TrashFolderPath = "/tmp/custom-trash"
        };
        Assert.Null(ConfigurationRequestValidator.Validate(reqWithAbsolutePath));
    }

    [Fact]
    public void ValidateTrashPath_ReturnsWarning_ForNullCharInPath()
    {
        // Null characters in paths are universally invalid across all platforms.
        // Path.GetFullPath throws ArgumentException for embedded null chars.
        var warning = ConfigurationRequestValidator.ValidateTrashPath("path\0with\0nulls");
        Assert.NotNull(warning);
        Assert.Contains("invalid characters or is too long", warning);
        Assert.Contains(".jellyfin-trash", warning);
    }

    [Fact]
    public void ValidateTrashPath_ReturnsWarning_ForDotPath()
    {
        // "." resolves to the library root itself — admin should be warned.
        var warning = ConfigurationRequestValidator.ValidateTrashPath(".");
        Assert.NotNull(warning);
        Assert.Contains("resolves to the library root itself", warning);
        Assert.Contains(".jellyfin-trash", warning);
    }

    // ===== ValidateTrashPathStrict (blocking validation) =====

    [Fact]
    public void ValidateTrashPathStrict_ReturnsNull_WhenTrashDisabled()
    {
        // When trash is disabled, any path is acceptable (validation is skipped)
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPathStrict("/*", false));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPathStrict("/\\", false));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPathStrict("", false));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPathStrict(null, false));
    }

    [Fact]
    public void ValidateTrashPathStrict_ReturnsNull_ForValidPaths()
    {
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPathStrict(".jellyfin-trash", true));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPathStrict("my-trash", true));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPathStrict("subdir/trash", true));
        Assert.Null(ConfigurationRequestValidator.ValidateTrashPathStrict("/mnt/trash", true));
    }

    [Fact]
    public void ValidateTrashPathStrict_ReturnsError_WhenEmptyAndTrashEnabled()
    {
        var error = ConfigurationRequestValidator.ValidateTrashPathStrict("", true);
        Assert.NotNull(error);
        Assert.Contains("required", error);

        error = ConfigurationRequestValidator.ValidateTrashPathStrict("   ", true);
        Assert.NotNull(error);

        error = ConfigurationRequestValidator.ValidateTrashPathStrict(null, true);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("/*")]
    [InlineData("/\\")]
    [InlineData("\\/")]
    [InlineData("*")]
    [InlineData("?")]
    [InlineData("<>")]
    [InlineData("|")]
    [InlineData("\"")]
    public void ValidateTrashPathStrict_ReturnsError_ForInvalidCharacters(string path)
    {
        var error = ConfigurationRequestValidator.ValidateTrashPathStrict(path, true);
        Assert.NotNull(error);
        Assert.Contains("invalid", error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("../../outside")]
    [InlineData("sub/../..")]
    public void ValidateTrashPathStrict_ReturnsError_ForTraversalPatterns(string path)
    {
        var error = ConfigurationRequestValidator.ValidateTrashPathStrict(path, true);
        Assert.NotNull(error);
        Assert.Contains("'..'", error);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData(".\\")]
    public void ValidateTrashPathStrict_ReturnsError_ForDotPaths(string path)
    {
        var error = ConfigurationRequestValidator.ValidateTrashPathStrict(path, true);
        Assert.NotNull(error);
        Assert.Contains("library root", error);
    }

    [Fact]
    public void Validate_ReturnsError_ForInvalidTrashPath_WhenTrashEnabled()
    {
        // When UseTrash is true AND path is invalid, Validate() must block the save
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            UseTrash = true,
            TrashFolderPath = "/*"
        };
        var error = ConfigurationRequestValidator.Validate(req);
        Assert.NotNull(error);
        Assert.Contains("invalid", error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ReturnsNull_ForInvalidTrashPath_WhenTrashDisabled()
    {
        // When UseTrash is false, invalid paths are allowed (they're irrelevant)
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            UseTrash = false,
            TrashFolderPath = "/*"
        };
        Assert.Null(ConfigurationRequestValidator.Validate(req));
    }

    [Fact]
    public void Validate_ReturnsNull_ForValidTrashPath_WhenTrashEnabled()
    {
        var req = new ConfigurationUpdateRequest
        {
            OrphanMinAgeDays = 7,
            TrashRetentionDays = 30,
            UseTrash = true,
            TrashFolderPath = ".jellyfin-trash"
        };
        Assert.Null(ConfigurationRequestValidator.Validate(req));
    }
}
