using System.Collections.Generic;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

// SA1402 is suppressed in jellyfin.tests.ruleset — multiple test classes per file is intentional here.

public class ConnectionTestResponseTests
{
    [Fact]
    public void Defaults_AreEmptyStringAndFalse()
    {
        var dto = new ConnectionTestResponse();
        Assert.False(dto.Success);
        Assert.Equal(string.Empty, dto.Message);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var dto = new ConnectionTestResponse { Success = true, Message = "OK" };
        Assert.True(dto.Success);
        Assert.Equal("OK", dto.Message);
    }
}

public class ConfigurationSaveResponseTests
{
    [Fact]
    public void Defaults_AreEmptyStringAndEmptyList()
    {
        var dto = new ConfigurationSaveResponse();
        Assert.Equal(string.Empty, dto.Message);
        Assert.NotNull(dto.Warnings);
        Assert.Empty(dto.Warnings);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var warnings = new List<string> { "warn1", "warn2" };
        var dto = new ConfigurationSaveResponse { Message = "Saved.", Warnings = warnings };
        Assert.Equal("Saved.", dto.Message);
        Assert.Equal(2, dto.Warnings.Count);
        Assert.Same(warnings, dto.Warnings);
    }

    [Fact]
    public void Warnings_AcceptsReadOnlyList()
    {
        var dto = new ConfigurationSaveResponse { Warnings = new[] { "x" } };
        Assert.Single(dto.Warnings);
    }
}

public class LogLevelResponseTests
{
    [Fact]
    public void Defaults_AreEmptyStrings()
    {
        var dto = new LogLevelResponse();
        Assert.Equal(string.Empty, dto.Message);
        Assert.Equal(string.Empty, dto.PluginLogLevel);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var dto = new LogLevelResponse { Message = "Updated.", PluginLogLevel = "DEBUG" };
        Assert.Equal("Updated.", dto.Message);
        Assert.Equal("DEBUG", dto.PluginLogLevel);
    }
}

public class LibraryEntryTests
{
    [Fact]
    public void Defaults_AreEmptyStrings()
    {
        var entry = new LibraryEntry();
        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.CollectionType);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var entry = new LibraryEntry { Name = "Movies", CollectionType = "movies" };
        Assert.Equal("Movies", entry.Name);
        Assert.Equal("movies", entry.CollectionType);
    }

    [Fact]
    public void TwoInstances_AreNotEqualByValue()
    {
        var a = new LibraryEntry { Name = "Movies", CollectionType = "movies" };
        var b = new LibraryEntry { Name = "Movies", CollectionType = "movies" };
        Assert.False(a.Equals(b), "LibraryEntry must use reference equality");
        Assert.True(a.Equals(a));
    }
}

public class LibraryListResponseTests
{
    [Fact]
    public void Default_LibrariesIsEmpty()
    {
        var dto = new LibraryListResponse();
        Assert.NotNull(dto.Libraries);
        Assert.Empty(dto.Libraries);
    }

    [Fact]
    public void Libraries_RoundTrip()
    {
        var entries = new List<LibraryEntry> { new() { Name = "Movies" }, new() { Name = "TV" } };
        var dto = new LibraryListResponse { Libraries = entries };
        Assert.Equal(2, dto.Libraries.Count);
        Assert.Same(entries, dto.Libraries);
    }
}

public class LibraryPathEntryTests
{
    [Fact]
    public void Defaults_AreEmptyStrings()
    {
        var entry = new LibraryPathEntry();
        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.Path);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var entry = new LibraryPathEntry { Name = "Movies", Path = "/mnt/movies" };
        Assert.Equal("Movies", entry.Name);
        Assert.Equal("/mnt/movies", entry.Path);
    }
}

public class FolderBrowserResponseTests
{
    [Fact]
    public void Default_LibraryPathsIsEmpty()
    {
        var dto = new FolderBrowserResponse();
        Assert.NotNull(dto.LibraryPaths);
        Assert.Empty(dto.LibraryPaths);
    }

    [Fact]
    public void LibraryPaths_RoundTrip()
    {
        var paths = new List<LibraryPathEntry>
        {
            new() { Name = "Movies", Path = "/mnt/movies" },
            new() { Name = "TV", Path = "/mnt/tv" },
        };
        var dto = new FolderBrowserResponse { LibraryPaths = paths };
        Assert.Equal(2, dto.LibraryPaths.Count);
        Assert.Same(paths, dto.LibraryPaths);
    }
}

public class PingResponseTests
{
    [Fact]
    public void Defaults_AreEmptyStringsAndFalse()
    {
        var dto = new PingResponse();
        Assert.False(dto.Ok);
        Assert.Equal(string.Empty, dto.Plugin);
        Assert.Equal(string.Empty, dto.Version);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var dto = new PingResponse { Ok = true, Plugin = "JellyfinHelper", Version = "3.0.0.0" };
        Assert.True(dto.Ok);
        Assert.Equal("JellyfinHelper", dto.Plugin);
        Assert.Equal("3.0.0.0", dto.Version);
    }
}

public class SeerrUrlResponseTests
{
    [Fact]
    public void Default_SeerrUrlIsEmpty()
    {
        var dto = new SeerrUrlResponse();
        Assert.Equal(string.Empty, dto.SeerrUrl);
    }

    [Fact]
    public void SeerrUrl_RoundTrip()
    {
        var dto = new SeerrUrlResponse { SeerrUrl = "http://seerr.local" };
        Assert.Equal("http://seerr.local", dto.SeerrUrl);
    }
}

public class TrashSizeResponseTests
{
    [Fact]
    public void Defaults_AreZero()
    {
        var dto = new TrashSizeResponse();
        Assert.Equal(0L, dto.TotalSize);
        Assert.Equal(0, dto.TotalItems);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var dto = new TrashSizeResponse { TotalSize = 1_048_576L, TotalItems = 42 };
        Assert.Equal(1_048_576L, dto.TotalSize);
        Assert.Equal(42, dto.TotalItems);
    }
}

public class TrashFoldersResponseTests
{
    [Fact]
    public void Defaults_AreEmptyListAndFalse()
    {
        var dto = new TrashFoldersResponse();
        Assert.NotNull(dto.Paths);
        Assert.Empty(dto.Paths);
        Assert.False(dto.IsAbsolute);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var paths = new List<string> { "/trash/a", "/trash/b" };
        var dto = new TrashFoldersResponse { Paths = paths, IsAbsolute = true };
        Assert.Equal(2, dto.Paths.Count);
        Assert.True(dto.IsAbsolute);
        Assert.Same(paths, dto.Paths);
    }
}

public class TrashDeleteResponseTests
{
    [Fact]
    public void Defaults_AreZero()
    {
        var dto = new TrashDeleteResponse();
        Assert.Equal(0, dto.Deleted);
        Assert.Equal(0, dto.Failed);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var dto = new TrashDeleteResponse { Deleted = 5, Failed = 1 };
        Assert.Equal(5, dto.Deleted);
        Assert.Equal(1, dto.Failed);
    }
}

public class TrashRelocateResponseTests
{
    [Fact]
    public void Defaults_AreZero()
    {
        var dto = new TrashRelocateResponse();
        Assert.Equal(0, dto.Moved);
        Assert.Equal(0, dto.Failed);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var dto = new TrashRelocateResponse { Moved = 10, Failed = 2 };
        Assert.Equal(10, dto.Moved);
        Assert.Equal(2, dto.Failed);
    }
}

public class TrashLibraryInfoTests
{
    [Fact]
    public void Defaults_AreEmptyStringsAndEmptyList()
    {
        var info = new TrashLibraryInfo();
        Assert.Equal(string.Empty, info.LibraryPath);
        Assert.Equal(string.Empty, info.LibraryName);
        Assert.NotNull(info.Items);
        Assert.Empty(info.Items);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var items = new List<TrashItemInfo>();
        var info = new TrashLibraryInfo
        {
            LibraryPath = "/mnt/movies",
            LibraryName = "Movies",
            Items = items,
        };
        Assert.Equal("/mnt/movies", info.LibraryPath);
        Assert.Equal("Movies", info.LibraryName);
        Assert.Same(items, info.Items);
    }
}

public class TrashConfigResponseTests
{
    [Fact]
    public void Defaults_AreFalseZeroAndEmptyList()
    {
        var dto = new TrashConfigResponse();
        Assert.False(dto.UseTrash);
        Assert.Equal(0, dto.RetentionDays);
        Assert.NotNull(dto.Libraries);
        Assert.Empty(dto.Libraries);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var libs = new List<TrashLibraryInfo> { new() { LibraryName = "Movies" } };
        var dto = new TrashConfigResponse { UseTrash = true, RetentionDays = 30, Libraries = libs };
        Assert.True(dto.UseTrash);
        Assert.Equal(30, dto.RetentionDays);
        Assert.Single(dto.Libraries);
        Assert.Same(libs, dto.Libraries);
    }
}

public class TrashAccessEntryTests
{
    [Fact]
    public void Defaults_AreEmptyStringNullAndFalse()
    {
        var entry = new TrashAccessEntry();
        Assert.Equal(string.Empty, entry.Path);
        Assert.Null(entry.LibraryRoot);
        Assert.False(entry.Exists);
        Assert.False(entry.CanRead);
        Assert.False(entry.CanWrite);
        Assert.False(entry.HasFullAccess);
        Assert.Null(entry.ErrorMessage);
    }

    [Fact]
    public void Properties_RoundTrip_AbsolutePath()
    {
        var entry = new TrashAccessEntry
        {
            Path = "/trash",
            Exists = true,
            CanRead = true,
            CanWrite = true,
            HasFullAccess = true,
        };
        Assert.Equal("/trash", entry.Path);
        Assert.Null(entry.LibraryRoot);
        Assert.True(entry.Exists);
        Assert.True(entry.CanRead);
        Assert.True(entry.CanWrite);
        Assert.True(entry.HasFullAccess);
        Assert.Null(entry.ErrorMessage);
    }

    [Fact]
    public void Properties_RoundTrip_RelativePath()
    {
        var entry = new TrashAccessEntry
        {
            Path = "/mnt/movies/.jellyfin-trash",
            LibraryRoot = "/mnt/movies",
            Exists = false,
            HasFullAccess = false,
            ErrorMessage = "Permission denied",
        };
        Assert.Equal("/mnt/movies", entry.LibraryRoot);
        Assert.False(entry.Exists);
        Assert.False(entry.HasFullAccess);
        Assert.Equal("Permission denied", entry.ErrorMessage);
    }
}

public class TrashAccessResponseTests
{
    [Fact]
    public void Defaults_AreFalseAndEmptyList()
    {
        var dto = new TrashAccessResponse();
        Assert.False(dto.AllAccessible);
        Assert.NotNull(dto.Results);
        Assert.Empty(dto.Results);
    }

    [Fact]
    public void Properties_RoundTrip()
    {
        var results = new List<TrashAccessEntry>
        {
            new() { Path = "/a", HasFullAccess = true },
            new() { Path = "/b", HasFullAccess = false, ErrorMessage = "denied" },
        };
        var dto = new TrashAccessResponse { AllAccessible = false, Results = results };
        Assert.False(dto.AllAccessible);
        Assert.Equal(2, dto.Results.Count);
        Assert.Same(results, dto.Results);
    }

    [Fact]
    public void Results_AcceptsReadOnlyList()
    {
        var dto = new TrashAccessResponse
        {
            Results = new TrashAccessEntry[] { new() { Path = "/x" } },
        };
        Assert.Single(dto.Results);
        Assert.Equal("/x", dto.Results[0].Path);
    }
}
