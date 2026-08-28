using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Construction-time contract for DiscoveryFeedbackStore: the public two-arg constructor resolves its file path from Plugin.Instance.DataFolderPath, so it must fail fast when the singleton is not initialised rather than silently binding to a bogus path.
/// </summary>
[Collection("ConfigOverride")]
public sealed class DiscoveryFeedbackStoreConstructionTests
{
    public DiscoveryFeedbackStoreConstructionTests()
    {
        ControllerTestFactory.InitializePluginInstance();
    }

    [Fact]
    public void Ctor_PluginInstanceNotInitialized_ThrowsInvalidOperationException()
    {
        ControllerTestFactory.TeardownPluginInstance();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new DiscoveryFeedbackStore(
                new Mock<IPluginLogService>().Object,
                new Mock<ILogger<DiscoveryFeedbackStore>>().Object));

            Assert.Contains("Plugin.Instance", ex.Message, StringComparison.Ordinal);
            Assert.Contains("data folder path", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            ControllerTestFactory.InitializePluginInstance();
        }
    }

    [Fact]
    public void Ctor_PluginInstanceInitialized_UsesDataFolderPathAndRecordsFeedback()
    {
        // Exercising the production two-arg ctor proves _filePath is derived from Plugin.Instance.DataFolderPath and that the store is functional through the real DI entry point, not just the test-only internal ctor.
        var store = new DiscoveryFeedbackStore(
            new Mock<IPluginLogService>().Object,
            new Mock<ILogger<DiscoveryFeedbackStore>>().Object);

        var userId = Guid.NewGuid();
        store.RecordShown(userId, "TestUser", new List<DiscoveryRecommendation>
        {
            new() { TmdbId = 1, MediaType = "movie", Title = "Resolved" }
        });

        var result = store.LoadForUser(userId);
        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.Equal("Resolved", result.Entries[0].Title);
    }
}
