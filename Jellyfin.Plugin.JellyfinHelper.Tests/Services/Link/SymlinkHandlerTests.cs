using Jellyfin.Plugin.JellyfinHelper.Services.Link;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Link;

/// <summary>
///     Unit tests for <see cref="SymlinkHandler" />.
///     Tests the symlink-specific logic in isolation using a mocked <see cref="ISymlinkHelper" />.
/// </summary>
public class SymlinkHandlerTests
{
    private static readonly string[] Expected = ["delete", "create"];
    private readonly SymlinkHandler _handler;
    private readonly Mock<ISymlinkHelper> _symlinkHelper;

    public SymlinkHandlerTests()
    {
        _symlinkHelper = new Mock<ISymlinkHelper>();
        _handler = new SymlinkHandler(_symlinkHelper.Object);
    }

    // ===== CanHandle =====

    [Fact]
    public void CanHandle_SymlinkFile_ReturnsTrue()
    {
        _symlinkHelper.Setup(h => h.IsSymlink("/media/movie.mkv")).Returns(true);

        Assert.True(_handler.CanHandle("/media/movie.mkv"));
    }

    [Fact]
    public void CanHandle_RegularFile_ReturnsFalse()
    {
        _symlinkHelper.Setup(h => h.IsSymlink("/media/movie.mkv")).Returns(false);

        Assert.False(_handler.CanHandle("/media/movie.mkv"));
    }

    [Fact]
    public void CanHandle_DelegatesToSymlinkHelper()
    {
        _handler.CanHandle("/some/path");

        _symlinkHelper.Verify(h => h.IsSymlink("/some/path"), Times.Once);
    }

    // ===== ReadTarget =====

    [Fact]
    public void ReadTarget_ReturnsSymlinkTarget()
    {
        _symlinkHelper.Setup(h => h.GetSymlinkTarget("/series/episode.mkv"))
            .Returns("/movies/Movie1/movie.mkv");

        var result = _handler.ReadTarget("/series/episode.mkv");

        Assert.Equal("/movies/Movie1/movie.mkv", result);
    }

    [Fact]
    public void ReadTarget_BrokenSymlink_ReturnsNull()
    {
        _symlinkHelper.Setup(h => h.GetSymlinkTarget("/series/episode.mkv"))
            .Returns((string?)null);

        Assert.Null(_handler.ReadTarget("/series/episode.mkv"));
    }

    [Fact]
    public void ReadTarget_DelegatesToSymlinkHelper()
    {
        _handler.ReadTarget("/some/path");

        _symlinkHelper.Verify(h => h.GetSymlinkTarget("/some/path"), Times.Once);
    }

    // ===== WriteTarget =====

    [Fact]
    public void WriteTarget_DeletesOldAndCreatesNewSymlink()
    {
        var callOrder = new List<string>();
        _symlinkHelper.Setup(h => h.DeleteSymlink("/series/episode.mkv"))
            .Callback(() => callOrder.Add("delete"));
        _symlinkHelper.Setup(h => h.CreateSymlink("/series/episode.mkv", "/movies/new-movie.mkv"))
            .Callback(() => callOrder.Add("create"));

        _handler.WriteTarget("/series/episode.mkv", "/movies/new-movie.mkv");

        Assert.Equal(Expected, callOrder);
    }

    [Fact]
    public void WriteTarget_CallsDeleteBeforeCreate()
    {
        _handler.WriteTarget("/link", "/target");

        _symlinkHelper.Verify(h => h.DeleteSymlink("/link"), Times.Once);
        _symlinkHelper.Verify(h => h.CreateSymlink("/link", "/target"), Times.Once);
    }
}