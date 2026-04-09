using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests;

/// <summary>
/// Structural tests for the configPage.html embedded resource.
/// Validates that the HTML configuration page contains the expected elements,
/// IDs, and TaskMode options that match the PluginConfiguration properties.
/// </summary>
public class ConfigPageHtmlTests
{
    private static readonly string HtmlContent = LoadConfigPageHtml();

    private static string LoadConfigPageHtml()
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("configPage.html", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(resourceName);

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── renderTaskModeSelect function ──────────────────────────────────

    [Fact]
    public void Html_ContainsRenderTaskModeSelectFunction()
    {
        Assert.Contains("function renderTaskModeSelect", HtmlContent);
    }

    [Fact]
    public void Html_RenderTaskModeSelect_ContainsAllThreeOptions()
    {
        // The function should generate options for Deactivate, DryRun, Activate
        Assert.Contains("Deactivate", HtmlContent);
        Assert.Contains("DryRun", HtmlContent);
        Assert.Contains("Activate", HtmlContent);
    }

    // ── TaskMode select elements for each sub-task ─────────────────────

    [Theory]
    [InlineData("cfgTrickplayMode", "TrickplayTaskMode")]
    [InlineData("cfgEmptyFolderMode", "EmptyMediaFolderTaskMode")]
    [InlineData("cfgSubtitleMode", "OrphanedSubtitleTaskMode")]
    [InlineData("cfgStrmMode", "StrmRepairTaskMode")]
    public void Html_ContainsSelectElementForTaskMode(string elementId, string configProperty)
    {
        // Verify the select element is rendered with the correct ID
        Assert.Contains($"'{elementId}'", HtmlContent);

        // Verify the config property is read when loading
        Assert.Contains($"cfg.{configProperty}", HtmlContent);
    }

    [Theory]
    [InlineData("cfgTrickplayMode", "TrickplayTaskMode")]
    [InlineData("cfgEmptyFolderMode", "EmptyMediaFolderTaskMode")]
    [InlineData("cfgSubtitleMode", "OrphanedSubtitleTaskMode")]
    [InlineData("cfgStrmMode", "StrmRepairTaskMode")]
    public void Html_SavesTaskModeFromSelectElement(string elementId, string configProperty)
    {
        // Verify the config property is written when saving
        var savePattern = $"{configProperty}: document.getElementById('{elementId}').value";
        Assert.Contains(savePattern, HtmlContent);
    }

    [Theory]
    [InlineData("cfgTrickplayMode")]
    [InlineData("cfgEmptyFolderMode")]
    [InlineData("cfgSubtitleMode")]
    [InlineData("cfgStrmMode")]
    public void Html_DefaultsToDryRunWhenConfigPropertyMissing(string elementId)
    {
        // Each renderTaskModeSelect call should have a fallback: || 'DryRun'
        var pattern = new Regex($@"renderTaskModeSelect\s*\(\s*'{Regex.Escape(elementId)}'.*\|\|\s*'DryRun'\s*\)");
        Assert.Matches(pattern, HtmlContent);
    }

    // ── No legacy checkbox elements for dry-run ────────────────────────

    [Fact]
    public void Html_DoesNotContainLegacyDryRunCheckboxes()
    {
        // Old checkbox IDs that should no longer exist
        Assert.DoesNotContain("cfgDryRunTrickplay", HtmlContent);
        Assert.DoesNotContain("cfgDryRunEmptyFolders", HtmlContent);
        Assert.DoesNotContain("cfgDryRunSubtitles", HtmlContent);
        Assert.DoesNotContain("cfgDryRunStrm", HtmlContent);
    }

    // ── Task labels are present ────────────────────────────────────────

    [Theory]
    [InlineData("Trickplay Folder Cleaner")]
    [InlineData("Empty Media Folder Cleaner")]
    [InlineData("Orphaned Subtitle Cleaner")]
    [InlineData(".strm File Repair")]
    public void Html_ContainsTaskLabel(string label)
    {
        Assert.Contains(label, HtmlContent);
    }

    // ── General page structure ─────────────────────────────────────────

    [Fact]
    public void Html_ContainsPluginConfigPageClass()
    {
        Assert.Contains("pluginConfigurationPage", HtmlContent);
    }

    [Fact]
    public void Html_ContainsJellyfinHelperPageId()
    {
        Assert.Contains("JellyfinHelperConfigPage", HtmlContent);
    }

    [Fact]
    public void Html_ContainsSaveButton()
    {
        // There should be a save/submit mechanism
        Assert.Matches(new Regex(@"(save|submit|btnSave)", RegexOptions.IgnoreCase), HtmlContent);
    }

    [Fact]
    public void Html_TaskModeEnumValues_MatchSelectOptions()
    {
        // Verify all TaskMode enum values are represented in the HTML
        foreach (var value in Enum.GetNames<TaskMode>())
        {
            Assert.Contains(value, HtmlContent);
        }
    }

    [Fact]
    public void Html_AllPluginConfigTaskModeProperties_HaveCorrespondingSelect()
    {
        // Use reflection to find all TaskMode properties on PluginConfiguration
        var taskModeProperties = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(TaskMode))
            .Select(p => p.Name)
            .ToList();

        Assert.NotEmpty(taskModeProperties);

        foreach (var prop in taskModeProperties)
        {
            // Each TaskMode property should be referenced in the HTML (cfg.PropertyName)
            Assert.Contains($"cfg.{prop}", HtmlContent);

            // Each TaskMode property should appear in the save logic (PropertyName:)
            Assert.Contains($"{prop}:", HtmlContent);
        }
    }
}