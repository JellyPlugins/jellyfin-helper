using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Tests that the composed configPage.html contains all expected Logs tab elements,
/// API calls, functions, and i18n keys.
/// </summary>
public class LogsHtmlTests : ConfigPageTestBase
{
    // === Tab registration ===

    [Fact]
    public void Html_ContainsLogsTabButton()
    {
        Assert.Contains("data-tab=\"logs\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsRenderLogsTabFunction()
    {
        Assert.Contains("function renderLogsTab()", HtmlContent);
    }

    [Fact]
    public void Html_ContainsInitLogsTabFunction()
    {
        Assert.Contains("function initLogsTab()", HtmlContent);
    }

    [Fact]
    public void Html_ContainsDestroyLogsTabFunction()
    {
        Assert.Contains("function destroyLogsTab()", HtmlContent);
    }

    // === UI elements ===

    [Fact]
    public void Html_ContainsLogLevelFilterSelect()
    {
        Assert.Contains("id=\"logsLevelFilter\"", HtmlContent);
    }

    [Theory]
    [InlineData("DEBUG")]
    [InlineData("INFO")]
    [InlineData("WARN")]
    [InlineData("ERROR")]
    public void Html_ContainsLogLevelOption(string level)
    {
        Assert.Contains("<option value=\"" + level + "\">" + level + "</option>", HtmlContent);
    }

    [Fact]
    public void Html_ContainsSourceFilterInput()
    {
        Assert.Contains("id=\"logsSourceFilter\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsLogsCountElement()
    {
        Assert.Contains("id=\"logsCount\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsAutoRefreshIndicator()
    {
        Assert.Contains("id=\"logsAutoRefreshIndicator\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsDownloadButton()
    {
        Assert.Contains("id=\"btnLogsDownload\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsClearButton()
    {
        Assert.Contains("id=\"btnLogsClear\"", HtmlContent);
    }

    [Fact]
    public void Html_ContainsLogsTableWrapper()
    {
        Assert.Contains("id=\"logsTableWrapper\"", HtmlContent);
    }

    // === Table structure ===

    [Fact]
    public void Html_ContainsLogsTableClass()
    {
        Assert.Contains("logs-table", HtmlContent);
    }

    [Theory]
    [InlineData("col-time")]
    [InlineData("col-level")]
    [InlineData("col-source")]
    [InlineData("col-message")]
    public void Html_ContainsTableColumnClass(string cssClass)
    {
        Assert.Contains(cssClass, HtmlContent);
    }

    // === Core functions ===

    [Fact]
    public void Html_ContainsLoadLogsFunction()
    {
        Assert.Contains("function loadLogs()", HtmlContent);
    }

    [Fact]
    public void Html_ContainsDownloadLogsFunction()
    {
        Assert.Contains("function downloadLogs()", HtmlContent);
    }

    [Fact]
    public void Html_ContainsClearLogsFunction()
    {
        Assert.Contains("function clearLogs()", HtmlContent);
    }

    [Fact]
    public void Html_ContainsFormatLogTimestampFunction()
    {
        Assert.Contains("function formatLogTimestamp(", HtmlContent);
    }

    // === Log level persistence ===

    [Fact]
    public void Html_ContainsLoadLogLevelFromConfigFunction()
    {
        Assert.Contains("function loadLogLevelFromConfig(", HtmlContent);
    }

    [Fact]
    public void Html_ContainsSaveLogLevelToConfigFunction()
    {
        Assert.Contains("function saveLogLevelToConfig(", HtmlContent);
    }

    [Fact]
    public void Html_LogLevelPersistence_ReadsPluginLogLevel()
    {
        Assert.Contains("cfg.PluginLogLevel", HtmlContent);
    }

    [Fact]
    public void Html_LogLevelPersistence_DefaultsToInfo()
    {
        Assert.Matches(new Regex(@"cfg\.PluginLogLevel\s*\|\|\s*'INFO'"), HtmlContent);
    }

    [Fact]
    public void Html_LogLevelPersistence_SavesViaConfiguration()
    {
        // Logs.js now uses a dedicated PUT endpoint instead of GET+POST of the entire config
        Assert.Contains("JellyfinHelper/Configuration/LogLevel", HtmlContent);
        Assert.Contains("PluginLogLevel", HtmlContent);
    }

    // === API endpoints ===

    [Fact]
    public void Html_CallsLogsEndpoint()
    {
        Assert.Contains("JellyfinHelper/Logs", HtmlContent);
    }

    [Fact]
    public void Html_CallsLogsDownloadEndpoint()
    {
        Assert.Contains("JellyfinHelper/Logs/Download", HtmlContent);
    }

    [Fact]
    public void Html_CallsDeleteLogsEndpoint()
    {
        Assert.Matches(
            new Regex(@"type\s*:\s*['""]DELETE['""].*JellyfinHelper/Logs", RegexOptions.Singleline),
            HtmlContent);
    }

    // === Auto-refresh ===

    [Fact]
    public void Html_ContainsAutoRefreshTimer()
    {
        Assert.Contains("_logsAutoRefreshTimer", HtmlContent);
    }

    [Fact]
    public void Html_ContainsStartAutoRefreshFunction()
    {
        Assert.Contains("function startLogsAutoRefresh()", HtmlContent);
    }

    [Fact]
    public void Html_ContainsStopAutoRefreshFunction()
    {
        Assert.Contains("function stopLogsAutoRefresh()", HtmlContent);
    }

    [Fact]
    public void Html_AutoRefreshInterval_Is10Seconds()
    {
        Assert.Contains("10000", HtmlContent);
    }

    // === Download mechanism ===

    [Fact]
    public void Html_DownloadUsesFetchApi()
    {
        // Download now delegates to the shared apiFetchBlob helper (which uses fetch internally)
        Assert.Contains("apiFetchBlob(", HtmlContent);
    }

    [Fact]
    public void Html_DownloadUsesAuthorizationHeader()
    {
        // Auth header is handled internally by apiFetchBlob in shared.js;
        // verify the shared helper carries the token via Authorization header
        Assert.Contains("Authorization", HtmlContent);
        Assert.Contains("accessToken()", HtmlContent);
    }

    [Fact]
    public void Html_DownloadCreatesTemporaryLink()
    {
        Assert.Contains("URL.createObjectURL", HtmlContent);
    }

    [Fact]
    public void Html_DownloadCleansUpObjectUrl()
    {
        Assert.Contains("URL.revokeObjectURL", HtmlContent);
    }

    [Fact]
    public void Html_DownloadFilename()
    {
        Assert.Contains("jellyfin-helper-logs.txt", HtmlContent);
    }

    // === Clear confirmation ===

    [Fact]
    public void Html_ClearLogs_RequiresConfirmation()
    {
        // Finding 12: native confirm() replaced with custom dialog
        Assert.Contains("createDialogOverlay(", HtmlContent);
        Assert.Contains("logsClearConfirm", HtmlContent);
    }

    // === CSS classes ===

    [Theory]
    [InlineData("logs-container")]
    [InlineData("logs-toolbar")]
    [InlineData("logs-table-wrapper")]
    [InlineData("logs-empty")]
    [InlineData("logs-btn-group")]
    [InlineData("logs-auto-refresh")]
    public void Html_ContainsLogsCssClass(string cssClass)
    {
        Assert.Contains(cssClass, HtmlContent);
    }

    [Theory]
    [InlineData("log-level-")]
    [InlineData("log-exception")]
    public void Html_ContainsLogEntryLevelStyling(string cssClass)
    {
        Assert.Contains(cssClass, HtmlContent);
    }

    // === i18n keys ===

    [Theory]
    [InlineData("logsLevel")]
    [InlineData("logsSource")]
    [InlineData("logsSourcePlaceholder")]
    [InlineData("logsAutoRefresh")]
    [InlineData("logsDownload")]
    [InlineData("logsClear")]
    [InlineData("logsLoading")]
    [InlineData("logsEmpty")]
    [InlineData("logsLoadError")]
    [InlineData("logsDownloadError")]
    [InlineData("logsClearConfirm")]
    [InlineData("logsClearError")]
    [InlineData("logsCountLabel")]
    [InlineData("logsTime")]
    [InlineData("logsLevelCol")]
    [InlineData("logsSourceCol")]
    [InlineData("logsMessage")]
    public void Html_ContainsI18nKey(string key)
    {
        Assert.Contains("'" + key + "'", HtmlContent);
    }

    // === Source filter debounce ===

    [Fact]
    public void Html_SourceFilter_HasDebounce()
    {
        Assert.Contains("debounceTimer", HtmlContent);
    }
}
