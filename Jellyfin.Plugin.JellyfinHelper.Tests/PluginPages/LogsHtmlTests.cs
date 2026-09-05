using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Tests that the composed configPage.html contains all expected Logs tab elements,
/// API calls, functions, and i18n keys.
/// </summary>
public class LogsHtmlTests : ConfigPageTestBase
{
    /// <summary>
    ///     Verifies the Logs tab DOM element ids and classes are present in the composed HTML.
    /// </summary>
    /// <param name="marker">The element id or class marker expected in the HTML.</param>
    [Theory]
    [InlineData("data-tab=\"logs\"")]
    [InlineData("id=\"logsLevelFilter\"")]
    [InlineData("id=\"logsSourceFilter\"")]
    [InlineData("id=\"logsCount\"")]
    [InlineData("id=\"logsAutoRefreshIndicator\"")]
    [InlineData("id=\"btnLogsDownload\"")]
    [InlineData("id=\"btnLogsClear\"")]
    [InlineData("id=\"logsTableWrapper\"")]
    [InlineData("logs-table")]
    public void Html_ContainsLogsElement(string marker)
    {
        Assert.Contains(marker, HtmlContent);
    }

    /// <summary>
    ///     Verifies the Logs tab function declarations are present in the composed HTML.
    /// </summary>
    /// <param name="signature">The function signature marker expected in the HTML.</param>
    [Theory]
    [InlineData("function renderLogsTab()")]
    [InlineData("function initLogsTab()")]
    [InlineData("function destroyLogsTab()")]
    [InlineData("function loadLogs()")]
    [InlineData("function downloadLogs()")]
    [InlineData("function clearLogs()")]
    [InlineData("function formatLogTimestamp(")]
    [InlineData("function loadLogLevelFromConfig(")]
    [InlineData("function saveLogLevelToConfig(")]
    public void Html_ContainsLogsFunction(string signature)
    {
        Assert.Contains(signature, HtmlContent);
    }

    /// <summary>
    ///     Verifies the Logs tab endpoints, timers, and constants are present in the composed HTML.
    /// </summary>
    /// <param name="marker">The endpoint, timer, or constant marker expected in the HTML.</param>
    [Theory]
    [InlineData("JellyfinHelper/Logs")]
    [InlineData("JellyfinHelper/Logs/Download")]
    [InlineData("_logsAutoRefreshTimer")]
    [InlineData("function startLogsAutoRefresh()")]
    [InlineData("function stopLogsAutoRefresh()")]
    [InlineData("10000")]
    [InlineData("URL.createObjectURL")]
    [InlineData("URL.revokeObjectURL")]
    [InlineData("jellyfin-helper-logs.txt")]
    [InlineData("_logsSourceDebounceTimer")]
    public void Html_ContainsLogsEndpointOrConstant(string marker)
    {
        Assert.Contains(marker, HtmlContent);
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

    [Theory]
    [InlineData("col-time")]
    [InlineData("col-level")]
    [InlineData("col-source")]
    [InlineData("col-message")]
    public void Html_ContainsTableColumnClass(string cssClass)
    {
        Assert.Contains(cssClass, HtmlContent);
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

    [Fact]
    public void Html_CallsDeleteLogsEndpoint()
    {
        Assert.Matches(
            new Regex(@"type\s*:\s*['""]DELETE['""].*JellyfinHelper/Logs", RegexOptions.Singleline),
            HtmlContent);
    }

    [Fact]
    public void Html_DownloadUsesFetchApi()
    {
        // Download now delegates to the shared apiFetchBlob helper (which uses fetch internally)
        // Scoped to downloadLogs() to avoid false positives from other callers
        Assert.Matches(
            new Regex(
                @"function\s+downloadLogs\s*\([^)]*\)\s*\{[\s\S]*?apiFetchBlob\s*\(",
                RegexOptions.Multiline),
            HtmlContent);
    }

    [Fact]
    public void Html_DownloadUsesAuthorizationHeader()
    {
        // Auth header is handled internally by apiFetchBlob in Shared.js; verify the shared helper carries the token via Authorization header Scoped to apiFetchBlob function body to avoid false positives from other helpers.
        Assert.Matches(
            new Regex(
                @"function\s+apiFetchBlob\s*\([^)]*\)\s*\{[\s\S]*?Authorization[\s\S]*?accessToken\(\)",
                RegexOptions.Multiline),
            HtmlContent);
    }

    [Fact]
    public void Html_ClearLogs_RequiresConfirmation()
    {
        // Native confirm() replaced with custom dialog
        // Scoped to clearLogs() to avoid false positives from other dialog usage
        Assert.Matches(
            new Regex(
                @"function\s+clearLogs\s*\([^)]*\)\s*\{[\s\S]*?createDialogOverlay\s*\(",
                RegexOptions.Multiline),
            HtmlContent);
        Assert.Matches(
            new Regex(
                @"function\s+clearLogs\s*\([^)]*\)\s*\{[\s\S]*?logsClearConfirm",
                RegexOptions.Multiline),
            HtmlContent);
    }

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
}
