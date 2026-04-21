using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
///     Tests for the <see cref="TaskMode" /> enum and its JSON serialization behavior.
/// </summary>
public class TaskModeTests
{
    [Fact]
    public void TaskMode_HasThreeValues()
    {
        var values = Enum.GetValues<TaskMode>();
        Assert.Equal(3, values.Length);
    }

    [Theory]
    [InlineData(TaskMode.Deactivate, 0)]
    [InlineData(TaskMode.DryRun, 1)]
    [InlineData(TaskMode.Activate, 2)]
    public void TaskMode_HasExpectedIntValues(TaskMode mode, int expected)
    {
        Assert.Equal(expected, (int)mode);
    }

    [Fact]
    public void TaskMode_DefaultValue_IsDeactivate()
    {
        // default(TaskMode) should be Deactivate (value 0)
        Assert.Equal(TaskMode.Deactivate, default);
    }

    [Theory]
    [InlineData(TaskMode.Deactivate, "\"Deactivate\"")]
    [InlineData(TaskMode.DryRun, "\"DryRun\"")]
    [InlineData(TaskMode.Activate, "\"Activate\"")]
    public void TaskMode_SerializesToString(TaskMode mode, string expectedJson)
    {
        var json = JsonSerializer.Serialize(mode);
        Assert.Equal(expectedJson, json);
    }

    [Theory]
    [InlineData("\"Deactivate\"", TaskMode.Deactivate)]
    [InlineData("\"DryRun\"", TaskMode.DryRun)]
    [InlineData("\"Activate\"", TaskMode.Activate)]
    public void TaskMode_DeserializesFromString(string json, TaskMode expected)
    {
        var result = JsonSerializer.Deserialize<TaskMode>(json);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void PluginConfiguration_DefaultTaskModes_AreExpectedDefaults()
    {
        var config = new PluginConfiguration();
        Assert.Equal(TaskMode.DryRun, config.TrickplayTaskMode);
        Assert.Equal(TaskMode.DryRun, config.EmptyMediaFolderTaskMode);
        Assert.Equal(TaskMode.DryRun, config.OrphanedSubtitleTaskMode);
        Assert.Equal(TaskMode.DryRun, config.LinkRepairTaskMode);
        Assert.Equal(TaskMode.DryRun, config.RecommendationsTaskMode);
        Assert.Equal(TaskMode.Deactivate, config.SeerrCleanupTaskMode);
    }

    [Fact]
    public void PluginConfiguration_TaskModes_RoundtripThroughJson()
    {
        var config = new PluginConfiguration
        {
            TrickplayTaskMode = TaskMode.Activate,
            EmptyMediaFolderTaskMode = TaskMode.Deactivate,
            OrphanedSubtitleTaskMode = TaskMode.DryRun,
            LinkRepairTaskMode = TaskMode.Activate,
            RecommendationsTaskMode = TaskMode.Activate,
            SeerrCleanupTaskMode = TaskMode.Deactivate
        };

        var json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<PluginConfiguration>(json)!;

        Assert.Equal(TaskMode.Activate, deserialized.TrickplayTaskMode);
        Assert.Equal(TaskMode.Deactivate, deserialized.EmptyMediaFolderTaskMode);
        Assert.Equal(TaskMode.DryRun, deserialized.OrphanedSubtitleTaskMode);
        Assert.Equal(TaskMode.Activate, deserialized.LinkRepairTaskMode);
        Assert.Equal(TaskMode.Activate, deserialized.RecommendationsTaskMode);
        Assert.Equal(TaskMode.Deactivate, deserialized.SeerrCleanupTaskMode);
    }
}