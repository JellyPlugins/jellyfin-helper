using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

public class SettingsHtmlTests : ConfigPageTestBase
{
    [Theory]
    [InlineData("cfgTrickplayMode", "TrickplayTaskMode")]
    [InlineData("cfgEmptyFolderMode", "EmptyMediaFolderTaskMode")]
    [InlineData("cfgSubtitleMode", "OrphanedSubtitleTaskMode")]
    [InlineData("cfgStrmMode", "StrmRepairTaskMode")]
    public void Html_SavesTaskModeFromSelectElement(string elementId, string configProperty)
    {
        var savePattern = configProperty + ": document.getElementById('" + elementId + "').value";
        Assert.Contains(savePattern, HtmlContent);
    }

    [Theory]
    [InlineData("cfgTrickplayMode")]
    [InlineData("cfgEmptyFolderMode")]
    [InlineData("cfgSubtitleMode")]
    [InlineData("cfgStrmMode")]
    public void Html_DefaultsToDryRunWhenConfigPropertyMissing(string elementId)
    {
        var pattern = new Regex("renderTaskModeSelect\\s*\\(\\s*'" + Regex.Escape(elementId) + "'.*\\|\\|\\s*'DryRun'\\s*\\)");
        Assert.Matches(pattern, HtmlContent);
    }

    [Fact]
    public void Html_DoesNotContainLegacyDryRunCheckboxes()
    {
        Assert.DoesNotContain("cfgDryRunTrickplay", HtmlContent);
        Assert.DoesNotContain("cfgDryRunEmptyFolders", HtmlContent);
        Assert.DoesNotContain("cfgDryRunSubtitles", HtmlContent);
        Assert.DoesNotContain("cfgDryRunStrm", HtmlContent);
    }

    [Theory]
    [InlineData("Trickplay Folder Cleaner")]
    [InlineData("Empty Media Folder Cleaner")]
    [InlineData("Orphaned Subtitle Cleaner")]
    [InlineData(".strm File Repair")]
    public void Html_ContainsTaskLabel(string label)
    {
        Assert.Contains(label, HtmlContent);
    }

    [Fact]
    public void Html_TaskModeEnumValues_MatchSelectOptions()
    {
        foreach (var value in Enum.GetNames(typeof(TaskMode)))
        {
            Assert.Contains(value, HtmlContent);
        }
    }

    [Fact]
    public void Html_AllPluginConfigTaskModeProperties_HaveCorrespondingSelect()
    {
        var taskModeProperties = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(TaskMode))
            .Select(p => p.Name)
            .ToList();

        Assert.NotEmpty(taskModeProperties);

        foreach (var prop in taskModeProperties)
        {
            Assert.Contains("cfg." + prop, HtmlContent);
            Assert.Contains(prop + ":", HtmlContent);
        }
    }

    [Fact]
    public void Html_ContainsSaveButton()
    {
        Assert.Matches(new Regex(@"(save|submit|btnSave)", RegexOptions.IgnoreCase), HtmlContent);
    }

    // Trash settings fields
    [Theory]
    [InlineData("cfgTrash", "UseTrash")]
    [InlineData("cfgTrashPath", "TrashFolderPath")]
    [InlineData("cfgTrashDays", "TrashRetentionDays")]
    public void Html_ContainsTrashSettingsElement(string elementId, string configProperty)
    {
        Assert.Contains(elementId, HtmlContent);
        Assert.Contains("cfg." + configProperty, HtmlContent);
    }

    [Theory]
    [InlineData("UseTrash")]
    [InlineData("TrashFolderPath")]
    [InlineData("TrashRetentionDays")]
    public void Html_SavesTrashSettingsInPayload(string configProperty)
    {
        Assert.Contains(configProperty + ":", HtmlContent);
    }

    // Trash disable dialog
    [Fact]
    public void Html_ContainsShowTrashDisableDialogFunction()
    {
        Assert.Contains("function showTrashDisableDialog", HtmlContent);
    }

    [Fact]
    public void Html_ContainsShowTrashDeleteConfirmationFunction()
    {
        Assert.Contains("function showTrashDeleteConfirmation", HtmlContent);
    }

    [Fact]
    public void Html_ContainsRemoveTrashDialogFunction()
    {
        Assert.Contains("function removeTrashDialog", HtmlContent);
    }

    [Fact]
    public void Html_ContainsWasTrashEnabledVariable()
    {
        Assert.Contains("_wasTrashEnabled", HtmlContent);
    }

    [Fact]
    public void Html_TrashDisableDialog_CallsGetTrashFoldersEndpoint()
    {
        Assert.Matches(
            new Regex(@"type\s*:\s*['""]GET['""].*JellyfinHelper/Trash/Folders", RegexOptions.Singleline),
            HtmlContent);
    }

    [Fact]
    public void Html_TrashDisableDialog_CallsDeleteTrashFoldersEndpoint()
    {
        Assert.Contains("type: 'DELETE', url: apiClient.getUrl('JellyfinHelper/Trash/Folders')", HtmlContent);
    }

    [Fact]
    public void Html_SaveSettings_ChecksTrashDisableCondition()
    {
        Assert.Contains("_wasTrashEnabled && !payload.UseTrash", HtmlContent);
    }

    [Fact]
    public void Html_TrashDisableDialog_HasKeepAndDeleteButtons()
    {
        Assert.Contains("trashKeep", HtmlContent);
        Assert.Contains("trashDelete", HtmlContent);
    }

    [Fact]
    public void Html_TrashDeleteConfirmation_HasConfirmAndCancelButtons()
    {
        Assert.Contains("trashDeleteConfirmOk", HtmlContent);
        Assert.Contains("cancel", HtmlContent);
    }

    [Fact]
    public void Html_TrashDisableDialog_SetsWasTrashEnabledOnLoad()
    {
        Assert.Matches(new Regex(@"_wasTrashEnabled\s*=\s*!!cfg\.UseTrash"), HtmlContent);
    }

    [Fact]
    public void Html_TrashDisableDialog_UpdatesWasTrashEnabledOnSave()
    {
        Assert.Matches(new Regex(@"_wasTrashEnabled\s*=\s*payload\.UseTrash"), HtmlContent);
    }

    [Fact]
    public void Html_TrashDisableDialog_CancelReChecksTrashCheckbox()
    {
        Assert.Contains("cfgTrash", HtmlContent);
    }

    [Fact]
    public void Html_DoSaveSettings_DetectsTrashChanged()
    {
        Assert.Contains("var trashChanged = (!!payload.UseTrash) !== _wasTrashEnabled", HtmlContent);
    }

    [Fact]
    public void Html_DoSaveSettings_RebuildUIOnTrashChange()
    {
        Assert.Contains("langChanged || trashChanged", HtmlContent);
    }

    [Fact]
    public void Html_DoSaveSettings_TrashChangedBeforeUpdate()
    {
        var trashChangedPos = HtmlContent.IndexOf("var trashChanged = (!!payload.UseTrash) !== _wasTrashEnabled", StringComparison.Ordinal);
        var wasTrashUpdatePos = HtmlContent.IndexOf("_wasTrashEnabled = payload.UseTrash", trashChangedPos + 1, StringComparison.Ordinal);
        Assert.True(trashChangedPos >= 0, "trashChanged detection not found");
        Assert.True(wasTrashUpdatePos > trashChangedPos, "_wasTrashEnabled must be updated AFTER trashChanged is computed");
    }

    [Fact]
    public void Html_DoSaveSettings_LangChangedVariable()
    {
        Assert.Contains("var langChanged = newLang !== _currentLang", HtmlContent);
    }

    [Fact]
    public void Html_BackupImportClientLimit_IsTenMegabytes()
    {
        Assert.Contains("10 * 1024 * 1024", HtmlContent);
        Assert.Contains("Maximum size is 10 MB", HtmlContent);
    }

    [Fact]
    public void Html_ContainsBackupSection()
    {
        Assert.Contains("settingsBackupTitle", HtmlContent);
        Assert.Contains("btnBackupExport", HtmlContent);
        Assert.Contains("btnBackupImportFile", HtmlContent);
    }

    [Fact]
    public void Html_BackupImportFailure_DoesNotExposeRawErrorsInUi()
    {
        Assert.DoesNotContain("Failed to execute 'json' on 'Response'", HtmlContent);
        Assert.DoesNotContain("err.message", HtmlContent);
        // Uses FileReader + apiClient.ajax instead of fetch; errors go through the ajax error handler
        Assert.Contains("reader.readAsText(file)", HtmlContent);
        Assert.Contains("backupImportError", HtmlContent);
    }

    [Fact]
    public void Html_TrashDisableDialog_HasAllI18nKeys()
    {
        var expectedKeys = new[]
        {
            "trashDisableTitle", "trashDisablePrompt", "trashDisableQuestion",
            "trashKeep", "trashDelete", "trashDeleteConfirmTitle",
            "trashDeleteConfirmMsg", "trashDeleteConfirmWarn", "trashDeleteConfirmOk",
            "trashDeleting", "trashDeletedCount", "trashFailedCount", "trashDeleteError"
        };

        foreach (var key in expectedKeys)
        {
            Assert.Contains(key, HtmlContent);
        }
    }
}
