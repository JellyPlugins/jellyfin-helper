using Jellyfin.Plugin.JellyfinHelper.Services.FolderBrowser;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.FolderBrowser;

/// <summary>
///     Tests for the folder-browser DTOs FolderBrowseResult and FolderEntry. Covers default values, mutability, and reference-equality semantics so behavioural regressions (e.g.
/// </summary>
public class FolderBrowserDtoTests
{
    [Fact]
    public void FolderEntry_Defaults_AreEmptyStringAndFalse()
    {
        var entry = new FolderEntry();

        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.Path);
        Assert.False(entry.HasChildren);
    }

    [Fact]
    public void FolderEntry_PropertiesAreMutable()
    {
        var entry = new FolderEntry
        {
            Name = "documents",
            Path = "/home/user/documents",
            HasChildren = true
        };

        Assert.Equal("documents", entry.Name);
        Assert.Equal("/home/user/documents", entry.Path);
        Assert.True(entry.HasChildren);

        entry.Name = "renamed";
        entry.Path = "/other";
        entry.HasChildren = false;

        Assert.Equal("renamed", entry.Name);
        Assert.Equal("/other", entry.Path);
        Assert.False(entry.HasChildren);
    }

    [Fact]
    public void FolderEntry_TwoInstancesWithSameValues_AreNotEqualByValue()
    {
        // The DTO is intentionally NOT a value/record type - two payloads with identical field values must not compare equal, so callers can safely use identity in caches without collisions.
        var a = new FolderEntry { Name = "x", Path = "/x", HasChildren = true };
        var b = new FolderEntry { Name = "x", Path = "/x", HasChildren = true };

        Assert.False(ReferenceEquals(a, b));
        // The key invariant: if someone converts FolderEntry to `record`, this line flips
        // to true and the test fails - surfacing the semantic change before it ships.
        Assert.False(a.Equals(b), "FolderEntry must use reference equality (not value/record semantics)");
        // Same instance still equals itself - guards against accidental override of
        // Equals to a constant false.
        Assert.True(a.Equals(a));
    }

    [Fact]
    public void FolderBrowseResult_Defaults_AreNullEmptyAndFalse()
    {
        var result = new FolderBrowseResult();

        Assert.Null(result.CurrentPath);
        Assert.Null(result.ParentPath);
        Assert.False(result.CanGoUp);
        Assert.NotNull(result.Directories);
        Assert.Empty(result.Directories);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FolderBrowseResult_AllPropertiesRoundTrip()
    {
        var dirs = new List<FolderEntry>
        {
            new() { Name = "a", Path = "/a", HasChildren = true },
            new() { Name = "b", Path = "/b" }
        };

        var result = new FolderBrowseResult
        {
            CurrentPath = "/here",
            ParentPath = "/up",
            CanGoUp = true,
            Directories = dirs,
            Error = null
        };

        Assert.Equal("/here", result.CurrentPath);
        Assert.Equal("/up", result.ParentPath);
        Assert.True(result.CanGoUp);
        Assert.Equal(2, result.Directories.Count);
        Assert.Same(dirs, result.Directories);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FolderBrowseResult_ErrorResult_HasSensibleDefaults()
    {
        // Reflect the exact shape the service uses for error results.
        var result = new FolderBrowseResult
        {
            Error = "Cannot access this directory."
        };

        Assert.Equal("Cannot access this directory.", result.Error);
        Assert.Null(result.CurrentPath);
        Assert.Null(result.ParentPath);
        Assert.False(result.CanGoUp);
        Assert.NotNull(result.Directories);
        Assert.Empty(result.Directories);
    }

    [Fact]
    public void FolderBrowseResult_DirectoriesCanBeReassigned()
    {
        var result = new FolderBrowseResult();
        Assert.Empty(result.Directories);

        var replacement = new List<FolderEntry> { new() { Name = "x", Path = "/x" } };
        result.Directories = replacement;

        Assert.Single(result.Directories);
        Assert.Same(replacement, result.Directories);
    }

    [Fact]
    public void FolderBrowseResult_DirectoriesRespectsReadOnlyInterface()
    {
        // IReadOnlyList<T> is exposed - arrays and lists should both fit.
        var result = new FolderBrowseResult { Directories = new FolderEntry[] { new() { Name = "arr" } } };
        Assert.Single(result.Directories);
        Assert.Equal("arr", result.Directories[0].Name);
    }
}