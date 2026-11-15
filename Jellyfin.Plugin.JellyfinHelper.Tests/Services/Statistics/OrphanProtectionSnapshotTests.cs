using Jellyfin.Plugin.JellyfinHelper.Services.Statistics;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Statistics;

public class OrphanProtectionSnapshotTests
{
    private static readonly string Root = Path.GetFullPath(Path.Join("media", "movies"));

    [Fact]
    public void ManagedDirectory_ProtectsItselfDescendantsAndAncestorsButNotSiblings()
    {
        var managed = Path.Join(Root, "Group", "Wanted");
        var snapshot = new OrphanProtectionSnapshot([managed, managed + Path.DirectorySeparatorChar], []);

        Assert.True(snapshot.ProtectsDirectory(managed));
        Assert.True(snapshot.ProtectsDirectory(Path.Join(managed, "Season 01")));
        Assert.True(snapshot.ProtectsDirectory(Path.Join(Root, "Group")));
        Assert.True(snapshot.ProtectsDirectory(Root));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(Root, "Group", "Wanted Other")));
        Assert.False(snapshot.ProtectsDirectory(Path.Join(Root, "Unmanaged")));
    }

    [Fact]
    public void Uncertainty_IsScopedAndDoesNotBecomePositiveOwnership()
    {
        var snapshot = new OrphanProtectionSnapshot([], [Root, Root + Path.DirectorySeparatorChar]);

        Assert.True(snapshot.IsUncertain(Root));
        Assert.True(snapshot.IsUncertain(Path.Join(Root, "Unknown")));
        Assert.False(snapshot.IsUncertain(Root + "-other"));
        Assert.False(snapshot.IsUncertain(Path.GetDirectoryName(Root)!));
        Assert.False(snapshot.ProtectsDirectory(Root));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative/path")]
    [InlineData(null)]
    public void InvalidPaths_DoNotCreateProtectionOrUncertainty(string? path)
    {
        var snapshot = new OrphanProtectionSnapshot([path!], [path!]);

        Assert.False(snapshot.ProtectsDirectory(path!));
        Assert.False(snapshot.IsUncertain(path!));
        Assert.False(snapshot.ProtectsDirectory(Root));
    }

    [Fact]
    public void InvalidAbsolutePath_IsIgnored()
    {
        var invalid = Root + "\0bad";
        var snapshot = new OrphanProtectionSnapshot([invalid], [invalid]);

        Assert.False(snapshot.ProtectsDirectory(invalid));
        Assert.False(snapshot.IsUncertain(invalid));
    }

    [Fact]
    public void RootDirectory_ProtectsChildrenWithoutLosingItsSeparator()
    {
        var root = Path.GetPathRoot(Root)!;
        var snapshot = new OrphanProtectionSnapshot([root], [root]);

        Assert.True(snapshot.ProtectsDirectory(Root));
        Assert.True(snapshot.IsUncertain(Root));
    }

    [Fact]
    public void CaseComparison_FollowsTheFilesystem()
    {
        var snapshot = new OrphanProtectionSnapshot([Root], [Root]);
        var insensitive = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        Assert.Equal(insensitive, snapshot.ProtectsDirectory(Root.ToUpperInvariant()));
        Assert.Equal(insensitive, snapshot.IsUncertain(Root.ToUpperInvariant()));
    }

    [Fact]
    public void EmptySnapshot_HasNoEffect()
    {
        Assert.False(OrphanProtectionSnapshot.Empty.ProtectsDirectory(Root));
        Assert.False(OrphanProtectionSnapshot.Empty.IsUncertain(Root));
    }

    [Theory]
    [InlineData("movie.trickplay")]
    [InlineData("Wanted [boxset]")]
    [InlineData("Wanted [collection]")]
    [InlineData(".custom-trash")]
    public void DeepExcludedSegments_AreRecognizedWithoutASplitCountLimit(string excludedSegment)
    {
        var deep = Path.Join(Root, Path.Join(Enumerable.Repeat("d", 60).ToArray()));

        Assert.True(OrphanProtectionSnapshot.IsExcludedMediaPath(Path.Join(deep, excludedSegment, "movie.mkv"), Root, null, ".custom-trash"));
        Assert.False(OrphanProtectionSnapshot.IsExcludedMediaPath(Path.Join(deep, "Wanted", "movie.mkv"), Root, null, ".custom-trash"));
    }

    [Fact]
    public void NullInputs_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new OrphanProtectionSnapshot(null!, []));
        Assert.Throws<ArgumentNullException>(() => new OrphanProtectionSnapshot([], null!));
    }
}
