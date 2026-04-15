using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Jellyfin.Plugin.JellyfinHelper.Tests.TestFixtures;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.PluginLog;

/// <summary>
/// Unit tests for <see cref="PluginLogService"/>.
/// All tests use unique source markers (e.g. "__PLT_TestName__") and filter by source
/// to avoid interference from parallel test classes that also write to the shared static buffer.
/// </summary>
[Collection("ConfigOverride")]
public class PluginLogServiceTests : IDisposable
{
    private readonly PluginLogService _sut = TestMockFactory.CreatePluginLogService();

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginLogServiceTests"/> class.
    /// Sets minimum level to DEBUG before each test.
    /// </summary>
    public PluginLogServiceTests()
    {
        _sut.TestMinLevelOverride = "DEBUG";
        _sut.Clear();
    }

    /// <summary>
    /// Cleans up after each test by clearing the log buffer and resetting the override.
    /// </summary>
    public void Dispose()
    {
        _sut.Clear();
        _sut.TestMinLevelOverride = null;
    }

    // ===== LogDebug =====

    /// <summary>
    /// Verifies that LogDebug adds an entry with level DEBUG and correct fields.
    /// </summary>
    [Fact]
    public void LogDebug_AddsDebugEntry_WithCorrectFields()
    {
        const string src = "__PLT_DebugFields__";
        _sut.LogDebug(src, "Debug message");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("DEBUG", entries[0].Level);
        Assert.Equal(src, entries[0].Source);
        Assert.Equal("Debug message", entries[0].Message);
        Assert.Null(entries[0].Exception);
    }

    /// <summary>
    /// Verifies that LogDebug entries are filtered out by AddEntry when MinLevel is INFO.
    /// This is the critical test: AddEntry should NOT store DEBUG when config level is INFO.
    /// </summary>
    [Fact]
    public void LogDebug_NotStored_WhenMinLevelIsInfo()
    {
        const string src = "__PLT_DebugNotInfo__";
        _sut.TestMinLevelOverride = "INFO";

        _sut.LogDebug(src, "Should not be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Empty(entries);
    }

    /// <summary>
    /// Verifies that LogDebug entries are filtered out by AddEntry when MinLevel is WARN.
    /// </summary>
    [Fact]
    public void LogDebug_NotStored_WhenMinLevelIsWarn()
    {
        const string src = "__PLT_DebugNotWarn__";
        _sut.TestMinLevelOverride = "WARN";

        _sut.LogDebug(src, "Should not be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Empty(entries);
    }

    /// <summary>
    /// Verifies that LogDebug entries are filtered out by AddEntry when MinLevel is ERROR.
    /// </summary>
    [Fact]
    public void LogDebug_NotStored_WhenMinLevelIsError()
    {
        const string src = "__PLT_DebugNotErr__";
        _sut.TestMinLevelOverride = "ERROR";

        _sut.LogDebug(src, "Should not be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Empty(entries);
    }

    /// <summary>
    /// Verifies that LogDebug IS stored when MinLevel is DEBUG.
    /// </summary>
    [Fact]
    public void LogDebug_Stored_WhenMinLevelIsDebug()
    {
        const string src = "__PLT_DebugStored__";
        _sut.TestMinLevelOverride = "DEBUG";

        _sut.LogDebug(src, "Should be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("DEBUG", entries[0].Level);
    }

    /// <summary>
    /// Verifies that LogDebug forwards to ILogger.LogDebug when logger is provided.
    /// </summary>
    [Fact]
    public void LogDebug_ForwardsToILogger()
    {
        var mockLogger = new Mock<ILogger>();

        _sut.LogDebug("__PLT_Fwd__", "Msg", mockLogger.Object);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Msg")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that LogDebug with null logger does not throw.
    /// </summary>
    [Fact]
    public void LogDebug_NullLogger_DoesNotThrow()
    {
        var ex = Record.Exception(() => _sut.LogDebug("__PLT__", "No crash", null));
        Assert.Null(ex);
    }

    // ===== LogInfo =====

    /// <summary>
    /// Verifies that LogInfo adds an entry with level INFO and correct fields.
    /// </summary>
    [Fact]
    public void LogInfo_AddsInfoEntry_WithCorrectFields()
    {
        const string src = "__PLT_InfoFields__";
        _sut.LogInfo(src, "Info message");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("INFO", entries[0].Level);
        Assert.Equal(src, entries[0].Source);
        Assert.Equal("Info message", entries[0].Message);
        Assert.Null(entries[0].Exception);
    }

    /// <summary>
    /// Verifies that LogInfo entries are filtered out by AddEntry when MinLevel is WARN.
    /// </summary>
    [Fact]
    public void LogInfo_NotStored_WhenMinLevelIsWarn()
    {
        const string src = "__PLT_InfoNotWarn__";
        _sut.TestMinLevelOverride = "WARN";

        _sut.LogInfo(src, "Should not be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Empty(entries);
    }

    /// <summary>
    /// Verifies that LogInfo entries are filtered out by AddEntry when MinLevel is ERROR.
    /// </summary>
    [Fact]
    public void LogInfo_NotStored_WhenMinLevelIsError()
    {
        const string src = "__PLT_InfoNotErr__";
        _sut.TestMinLevelOverride = "ERROR";

        _sut.LogInfo(src, "Should not be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Empty(entries);
    }

    /// <summary>
    /// Verifies that LogInfo IS stored when MinLevel is INFO.
    /// </summary>
    [Fact]
    public void LogInfo_Stored_WhenMinLevelIsInfo()
    {
        const string src = "__PLT_InfoStoredINFO__";
        _sut.TestMinLevelOverride = "INFO";

        _sut.LogInfo(src, "Should be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("INFO", entries[0].Level);
    }

    /// <summary>
    /// Verifies that LogInfo IS stored when MinLevel is DEBUG.
    /// </summary>
    [Fact]
    public void LogInfo_Stored_WhenMinLevelIsDebug()
    {
        const string src = "__PLT_InfoStoredDBG__";
        _sut.TestMinLevelOverride = "DEBUG";

        _sut.LogInfo(src, "Should be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("INFO", entries[0].Level);
    }

    /// <summary>
    /// Verifies that LogInfo forwards to ILogger.LogInformation when logger is provided.
    /// </summary>
    [Fact]
    public void LogInfo_ForwardsToILogger()
    {
        var mockLogger = new Mock<ILogger>();

        _sut.LogInfo("__PLT_Fwd__", "InfoMsg", mockLogger.Object);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("InfoMsg")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that LogInfo with null logger does not throw.
    /// </summary>
    [Fact]
    public void LogInfo_NullLogger_DoesNotThrow()
    {
        var ex = Record.Exception(() => _sut.LogInfo("__PLT__", "No crash", null));
        Assert.Null(ex);
    }

    // ===== LogWarning =====

    /// <summary>
    /// Verifies that LogWarning adds an entry with level WARN.
    /// </summary>
    [Fact]
    public void LogWarning_AddsWarnEntry()
    {
        const string src = "__PLT_WarnFields__";
        _sut.LogWarning(src, "Warning message");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("WARN", entries[0].Level);
        Assert.Equal(src, entries[0].Source);
        Assert.Equal("Warning message", entries[0].Message);
        Assert.Null(entries[0].Exception);
    }

    /// <summary>
    /// Verifies that LogWarning with exception includes exception details.
    /// </summary>
    [Fact]
    public void LogWarning_WithException_IncludesExceptionString()
    {
        const string src = "__PLT_WarnEx__";
        var ex = new InvalidOperationException("Access denied");
        _sut.LogWarning(src, "Warning occurred", ex);

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("WARN", entries[0].Level);
        Assert.NotNull(entries[0].Exception);
        Assert.Contains("Access denied", entries[0].Exception, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that LogWarning entries are filtered out when MinLevel is ERROR.
    /// </summary>
    [Fact]
    public void LogWarning_NotStored_WhenMinLevelIsError()
    {
        const string src = "__PLT_WarnNotErr__";
        _sut.TestMinLevelOverride = "ERROR";

        _sut.LogWarning(src, "Should not be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Empty(entries);
    }

    /// <summary>
    /// Verifies that LogWarning IS stored when MinLevel is WARN.
    /// </summary>
    [Fact]
    public void LogWarning_Stored_WhenMinLevelIsWarn()
    {
        const string src = "__PLT_WarnStoredW__";
        _sut.TestMinLevelOverride = "WARN";

        _sut.LogWarning(src, "Should be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("WARN", entries[0].Level);
    }

    /// <summary>
    /// Verifies that LogWarning forwards to ILogger.LogWarning (without exception).
    /// </summary>
    [Fact]
    public void LogWarning_ForwardsToILogger_WithoutException()
    {
        var mockLogger = new Mock<ILogger>();

        _sut.LogWarning("__PLT__", "WarnMsg", logger: mockLogger.Object);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("WarnMsg")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that LogWarning forwards to ILogger.LogWarning (with exception).
    /// </summary>
    [Fact]
    public void LogWarning_ForwardsToILogger_WithException()
    {
        var mockLogger = new Mock<ILogger>();
        var exception = new InvalidOperationException("test");

        _sut.LogWarning("__PLT__", "WarnMsg", exception, mockLogger.Object);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("WarnMsg")),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ===== LogError =====

    /// <summary>
    /// Verifies that LogError adds an entry with level ERROR.
    /// </summary>
    [Fact]
    public void LogError_AddsEntry()
    {
        const string src = "__PLT_ErrFields__";
        _sut.LogError(src, "Test error message");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("ERROR", entries[0].Level);
        Assert.Equal(src, entries[0].Source);
        Assert.Equal("Test error message", entries[0].Message);
        Assert.Null(entries[0].Exception);
    }

    /// <summary>
    /// Verifies that LogError with exception includes exception details.
    /// </summary>
    [Fact]
    public void LogError_WithException_IncludesExceptionString()
    {
        const string src = "__PLT_ErrEx__";
        var ex = new InvalidOperationException("Something failed");
        _sut.LogError(src, "Error occurred", ex);

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.NotNull(entries[0].Exception);
        Assert.Contains("Something failed", entries[0].Exception, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that LogError IS stored even when MinLevel is ERROR (equal level).
    /// </summary>
    [Fact]
    public void LogError_Stored_WhenMinLevelIsError()
    {
        const string src = "__PLT_ErrStoredE__";
        _sut.TestMinLevelOverride = "ERROR";

        _sut.LogError(src, "Should be stored");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("ERROR", entries[0].Level);
    }

    /// <summary>
    /// Verifies that LogError forwards to ILogger.LogError (without exception).
    /// </summary>
    [Fact]
    public void LogError_ForwardsToILogger_WithoutException()
    {
        var mockLogger = new Mock<ILogger>();

        _sut.LogError("__PLT__", "ErrMsg", logger: mockLogger.Object);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("ErrMsg")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that LogError forwards to ILogger.LogError (with exception).
    /// </summary>
    [Fact]
    public void LogError_ForwardsToILogger_WithException()
    {
        var mockLogger = new Mock<ILogger>();
        var exception = new InvalidOperationException("fatal");

        _sut.LogError("__PLT__", "ErrMsg", exception, mockLogger.Object);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("ErrMsg")),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that LogError with null logger does not throw.
    /// </summary>
    [Fact]
    public void LogError_NullLogger_DoesNotThrow()
    {
        var ex = Record.Exception(() => _sut.LogError("__PLT__", "No crash", null, null));
        Assert.Null(ex);
    }

    // ===== AddEntry MinLevel Filtering (cross-level matrix) =====

    /// <summary>
    /// Verifies that only ERROR is stored when configured MinLevel is ERROR.
    /// </summary>
    [Fact]
    public void AddEntry_MinLevelError_OnlyStoresErrors()
    {
        const string src = "__PLT_MatrixE__";
        _sut.TestMinLevelOverride = "ERROR";

        _sut.LogDebug(src, "d");
        _sut.LogInfo(src, "i");
        _sut.LogWarning(src, "w");
        _sut.LogError(src, "e");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.All(entries, e => Assert.Equal("ERROR", e.Level));
    }

    /// <summary>
    /// Verifies that WARN and ERROR are stored when configured MinLevel is WARN.
    /// </summary>
    [Fact]
    public void AddEntry_MinLevelWarn_StoresWarnAndError()
    {
        const string src = "__PLT_MatrixW__";
        _sut.TestMinLevelOverride = "WARN";

        _sut.LogDebug(src, "d");
        _sut.LogInfo(src, "i");
        _sut.LogWarning(src, "w");
        _sut.LogError(src, "e");

        var entries = _sut.GetEntries(source: src);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Level == "WARN");
        Assert.Contains(entries, e => e.Level == "ERROR");
        Assert.DoesNotContain(entries, e => e.Level == "DEBUG");
        Assert.DoesNotContain(entries, e => e.Level == "INFO");
    }

    /// <summary>
    /// Verifies that INFO, WARN and ERROR are stored when configured MinLevel is INFO.
    /// </summary>
    [Fact]
    public void AddEntry_MinLevelInfo_StoresInfoWarnError()
    {
        const string src = "__PLT_MatrixI__";
        _sut.TestMinLevelOverride = "INFO";

        _sut.LogDebug(src, "d");
        _sut.LogInfo(src, "i");
        _sut.LogWarning(src, "w");
        _sut.LogError(src, "e");

        var entries = _sut.GetEntries(source: src);
        Assert.Equal(3, entries.Count);
        Assert.DoesNotContain(entries, e => e.Level == "DEBUG");
        Assert.Contains(entries, e => e.Level == "INFO");
        Assert.Contains(entries, e => e.Level == "WARN");
        Assert.Contains(entries, e => e.Level == "ERROR");
    }

    /// <summary>
    /// Verifies that all levels are stored when configured MinLevel is DEBUG.
    /// </summary>
    [Fact]
    public void AddEntry_MinLevelDebug_StoresAllLevels()
    {
        const string src = "__PLT_MatrixD__";
        _sut.TestMinLevelOverride = "DEBUG";

        _sut.LogDebug(src, "d");
        _sut.LogInfo(src, "i");
        _sut.LogWarning(src, "w");
        _sut.LogError(src, "e");

        var entries = _sut.GetEntries(source: src);
        Assert.Equal(4, entries.Count);
    }

    /// <summary>
    /// Verifies that ILogger dual-logging still happens even when AddEntry filters out the entry.
    /// The ILogger should always receive the message regardless of plugin buffer level.
    /// </summary>
    [Fact]
    public void LogDebug_StillForwardsToILogger_EvenWhenFilteredFromBuffer()
    {
        const string src = "__PLT_FwdFiltered__";
        _sut.TestMinLevelOverride = "ERROR"; // DEBUG won't be stored in buffer
        var mockLogger = new Mock<ILogger>();

        _sut.LogDebug(src, "Filtered debug msg", mockLogger.Object);

        // Not stored in buffer
        var entries = _sut.GetEntries(source: src);
        Assert.Empty(entries);

        // But still forwarded to ILogger
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Filtered debug msg")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that ILogger dual-logging still happens for INFO even when filtered from buffer.
    /// </summary>
    [Fact]
    public void LogInfo_StillForwardsToILogger_EvenWhenFilteredFromBuffer()
    {
        const string src = "__PLT_FwdInfoFilt__";
        _sut.TestMinLevelOverride = "ERROR";
        var mockLogger = new Mock<ILogger>();

        _sut.LogInfo(src, "Filtered info", mockLogger.Object);

        var entries = _sut.GetEntries(source: src);
        Assert.Empty(entries);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Filtered info")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ===== GetConfiguredMinLevel =====

    /// <summary>
    /// Verifies that GetConfiguredMinLevel returns the override when set.
    /// </summary>
    [Fact]
    public void GetConfiguredMinLevel_ReturnsOverride_WhenSet()
    {
        _sut.TestMinLevelOverride = "WARN";
        Assert.Equal("WARN", _sut.GetConfiguredMinLevel());
    }

    /// <summary>
    /// Verifies that GetConfiguredMinLevel returns INFO as default when no plugin and no override.
    /// </summary>
    [Fact]
    public void GetConfiguredMinLevel_ReturnsInfo_WhenNoOverrideAndNoPlugin()
    {
        _sut.TestMinLevelOverride = null;

        // Without plugin instance, should fall back to INFO
        var level = _sut.GetConfiguredMinLevel();
        Assert.Equal("INFO", level);
    }

    // ===== GetEntries Filtering =====

    /// <summary>
    /// Verifies that entries are returned newest-first.
    /// </summary>
    [Fact]
    public void GetEntries_ReturnsNewestFirst()
    {
        const string src = "__PLT_Order__";
        _sut.LogError(src, "First");
        _sut.LogError(src, "Second");
        _sut.LogError(src, "Third");

        var entries = _sut.GetEntries(source: src);
        Assert.Equal(3, entries.Count);
        Assert.Equal("Third", entries[0].Message);
        Assert.Equal("Second", entries[1].Message);
        Assert.Equal("First", entries[2].Message);
    }

    /// <summary>
    /// Verifies that the limit parameter caps the number of returned entries.
    /// </summary>
    [Fact]
    public void GetEntries_RespectsLimit()
    {
        const string src = "__PLT_Limit__";
        for (int i = 0; i < 10; i++)
        {
            _sut.LogError(src, $"Message {i}");
        }

        var entries = _sut.GetEntries(source: src, limit: 3);
        Assert.Equal(3, entries.Count);
    }

    /// <summary>
    /// Verifies that the limit returns the NEWEST entries (not oldest).
    /// </summary>
    [Fact]
    public void GetEntries_LimitReturnsNewestEntries()
    {
        const string src = "__PLT_LimitNew__";
        for (int i = 0; i < 5; i++)
        {
            _sut.LogError(src, $"Message {i}");
        }

        var entries = _sut.GetEntries(source: src, limit: 2);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Message 4", entries[0].Message); // newest
        Assert.Equal("Message 3", entries[1].Message);
    }

    /// <summary>
    /// Verifies that the source filter works (partial, case-insensitive).
    /// </summary>
    [Fact]
    public void GetEntries_FiltersBySource()
    {
        _sut.LogError("__PLT_Trickplay__", "Trickplay message");
        _sut.LogError("__PLT_Subtitle__", "Subtitle message");
        _sut.LogError("__PLT_Trickplay__", "Another trickplay");

        var entries = _sut.GetEntries(source: "plt_trickplay");
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Contains("Trickplay", e.Source, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies that source filter with no match returns empty.
    /// </summary>
    [Fact]
    public void GetEntries_SourceFilterNoMatch_ReturnsEmpty()
    {
        _sut.LogError("__PLT_API__", "Some error");

        var entries = _sut.GetEntries(source: "NonExistentXYZ987");
        Assert.Empty(entries);
    }

    /// <summary>
    /// Verifies that the minLevel query filter works on GetEntries.
    /// </summary>
    [Fact]
    public void GetEntries_FiltersByMinLevel()
    {
        const string src = "__PLT_MinLvlQ__";
        _sut.LogError(src, "Error");
        _sut.LogWarning(src, "Warning");

        var warnAndAbove = _sut.GetEntries(minLevel: "WARN", source: src);
        Assert.Equal(2, warnAndAbove.Count);

        var errorOnly = _sut.GetEntries(minLevel: "ERROR", source: src);
        Assert.Single(errorOnly);
        Assert.Equal("ERROR", errorOnly[0].Level);
    }

    /// <summary>
    /// Verifies that DEBUG entries are excluded from GetEntries when minLevel query is INFO.
    /// </summary>
    [Fact]
    public void GetEntries_MinLevelInfo_ExcludesDebug()
    {
        const string src = "__PLT_ExclDbg__";
        _sut.LogDebug(src, "Debug msg");
        _sut.LogInfo(src, "Info msg");
        _sut.LogWarning(src, "Warn msg");
        _sut.LogError(src, "Error msg");

        var entries = _sut.GetEntries(minLevel: "INFO", source: src);
        Assert.Equal(3, entries.Count);
        Assert.DoesNotContain(entries, e => e.Level == "DEBUG");
    }

    /// <summary>
    /// Verifies that combined source and minLevel filters work together.
    /// </summary>
    [Fact]
    public void GetEntries_CombinedFilter_SourceAndLevel()
    {
        _sut.LogDebug("__PLT_Cfg__", "Config debug");
        _sut.LogWarning("__PLT_Cfg__", "Config warning");
        _sut.LogError("__PLT_Trsh__", "Trash error");

        var entries = _sut.GetEntries(minLevel: "WARN", source: "__PLT_Cfg__");
        Assert.Single(entries);
        Assert.Equal("Config warning", entries[0].Message);
    }

    /// <summary>
    /// Verifies that null source returns entries without source filtering.
    /// </summary>
    [Fact]
    public void GetEntries_NullSource_DoesNotFilterBySource()
    {
        const string src = "__PLT_NullSrc__";
        _sut.LogError(src, "api error");

        // Null source should not filter - our entry should be present among results
        var entries = _sut.GetEntries(source: null);
        Assert.Contains(entries, e => e.Source == src);
    }

    /// <summary>
    /// Verifies that empty string source returns entries without source filtering.
    /// </summary>
    [Fact]
    public void GetEntries_EmptySource_DoesNotFilterBySource()
    {
        const string src = "__PLT_EmptySrc__";
        _sut.LogError(src, "api error");

        var entries = _sut.GetEntries(source: string.Empty);
        Assert.Contains(entries, e => e.Source == src);
    }

    // ===== Clear =====

    /// <summary>
    /// Verifies that Clear removes all entries.
    /// </summary>
    [Fact]
    public void Clear_RemovesAllEntries()
    {
        const string src = "__PLT_Clear__";
        _sut.LogError(src, "Message 1");
        _sut.LogError(src, "Message 2");

        var before = _sut.GetEntries(source: src);
        Assert.Equal(2, before.Count);

        _sut.Clear();
        Assert.Equal(0, _sut.GetCount());
        Assert.Empty(_sut.GetEntries());
    }

    /// <summary>
    /// Verifies that Clear on an empty buffer does not throw.
    /// </summary>
    [Fact]
    public void Clear_EmptyBuffer_DoesNotThrow()
    {
        var ex = Record.Exception(() => _sut.Clear());
        Assert.Null(ex);
    }

    // ===== Ring Buffer =====

    /// <summary>
    /// Verifies that the ring buffer respects MaxEntries by not exceeding the maximum count.
    /// </summary>
    [Fact]
    public void RingBuffer_EvictsOldEntries_WhenMaxExceeded()
    {
        for (int i = 0; i < PluginLogService.MaxEntries + 100; i++)
        {
            _sut.LogError("__PLT_Ring__", $"Message {i}");
        }

        Assert.True(_sut.GetCount() <= PluginLogService.MaxEntries);
    }

    /// <summary>
    /// Verifies that oldest entries are evicted and newest are preserved.
    /// </summary>
    [Fact]
    public void RingBuffer_PreservesNewestEntries()
    {
        const string src = "__PLT_RingNew__";
        for (int i = 0; i < PluginLogService.MaxEntries + 50; i++)
        {
            _sut.LogError(src, $"Message {i}");
        }

        var entries = _sut.GetEntries(source: src, limit: 1);
        Assert.Single(entries);

        // The newest entry should be the last one logged
        Assert.Equal($"Message {PluginLogService.MaxEntries + 49}", entries[0].Message);
    }

    /// <summary>
    /// Verifies that the oldest entry was evicted by the ring buffer.
    /// </summary>
    [Fact]
    public void RingBuffer_EvictsOldestEntry()
    {
        const string src = "__PLT_RingOld__";
        for (int i = 0; i < PluginLogService.MaxEntries + 50; i++)
        {
            _sut.LogError(src, $"Message {i}");
        }

        var allEntries = _sut.GetEntries(source: src, limit: PluginLogService.MaxEntries);
        Assert.DoesNotContain(allEntries, e => e.Message == "Message 0");
        Assert.DoesNotContain(allEntries, e => e.Message == "Message 49");
    }

    // ===== Timestamp =====

    /// <summary>
    /// Verifies that the entry timestamp is set to a recent UTC value.
    /// </summary>
    [Fact]
    public void LogEntry_HasUtcTimestamp()
    {
        const string src = "__PLT_Ts__";
        var before = DateTime.UtcNow;
        _sut.LogError(src, "Timestamp test");
        var after = DateTime.UtcNow;

        var entry = _sut.GetEntries(source: src).First();
        Assert.InRange(entry.Timestamp, before, after);
    }

    // ===== ExportAsText =====

    /// <summary>
    /// Verifies that ExportAsText produces valid output with correct header and content.
    /// </summary>
    [Fact]
    public void ExportAsText_ProducesFormattedOutput()
    {
        const string src1 = "__PLT_Export1__";
        const string src2 = "__PLT_Export2__";
        _sut.LogError(src1, "Test error");
        _sut.LogWarning(src2, "Test warning");

        var text = _sut.ExportAsText();
        Assert.Contains("Jellyfin Helper Plugin Logs", text, StringComparison.Ordinal);
        Assert.Contains("[ERROR]", text, StringComparison.Ordinal);
        Assert.Contains("[WARN ]", text, StringComparison.Ordinal);
        Assert.Contains(src1, text, StringComparison.Ordinal);
        Assert.Contains(src2, text, StringComparison.Ordinal);
        Assert.Contains("Entries:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that ExportAsText with minLevel filter excludes lower levels.
    /// </summary>
    [Fact]
    public void ExportAsText_WithMinLevel_FiltersEntries()
    {
        const string src = "__PLT_ExFilt__";
        _sut.LogError(src, "Error");
        _sut.LogWarning(src, "Warning");

        var text = _sut.ExportAsText("ERROR");
        Assert.Contains("[ERROR]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[WARN ]", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that ExportAsText on empty buffer produces header with zero entries.
    /// </summary>
    [Fact]
    public void ExportAsText_EmptyBuffer_ProducesHeaderOnly()
    {
        _sut.Clear();
        var text = _sut.ExportAsText();
        Assert.Contains("Jellyfin Helper Plugin Logs", text, StringComparison.Ordinal);
        Assert.Contains("Entries: 0", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that ExportAsText includes exception details.
    /// </summary>
    [Fact]
    public void ExportAsText_WithException_IncludesExceptionText()
    {
        const string src = "__PLT_ExExc__";
        var ex = new InvalidOperationException("Export test exception");
        _sut.LogError(src, "Error with exception", ex);

        var text = _sut.ExportAsText();
        Assert.Contains("Exception:", text, StringComparison.Ordinal);
        Assert.Contains("Export test exception", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that ExportAsText exports entries oldest-first (chronological).
    /// </summary>
    [Fact]
    public void ExportAsText_ExportsOldestFirst()
    {
        const string src = "__PLT_ExOrd__";
        _sut.LogError(src, "FirstMsg");
        _sut.LogError(src, "SecondMsg");

        var text = _sut.ExportAsText();
        int firstPos = text.IndexOf("FirstMsg", StringComparison.Ordinal);
        int secondPos = text.IndexOf("SecondMsg", StringComparison.Ordinal);

        Assert.True(firstPos < secondPos, "ExportAsText should output entries oldest-first (chronological)");
    }

    /// <summary>
    /// Verifies that ExportAsText contains the "Exported" timestamp line.
    /// </summary>
    [Fact]
    public void ExportAsText_ContainsExportedTimestamp()
    {
        var text = _sut.ExportAsText();
        Assert.Contains("Exported:", text, StringComparison.Ordinal);
        Assert.Contains("UTC", text, StringComparison.Ordinal);
    }

    // ===== GetLevelIndex =====

    /// <summary>
    /// Verifies GetLevelIndex for known and unknown levels.
    /// </summary>
    [Theory]
    [InlineData("DEBUG", 0)]
    [InlineData("INFO", 1)]
    [InlineData("WARN", 2)]
    [InlineData("ERROR", 3)]
    [InlineData("debug", 0)]
    [InlineData("info", 1)]
    [InlineData("warn", 2)]
    [InlineData("error", 3)]
    [InlineData("Debug", 0)]
    [InlineData("Info", 1)]
    [InlineData("Warn", 2)]
    [InlineData("Error", 3)]
    [InlineData("UNKNOWN", 1)]
    [InlineData("", 1)]
    [InlineData("TRACE", 1)]
    public void GetLevelIndex_ReturnsCorrectIndex(string level, int expectedIndex)
    {
        Assert.Equal(expectedIndex, PluginLogService.GetLevelIndex(level));
    }

    // ===== Edge Cases =====

    /// <summary>
    /// Verifies that logging with empty source does not throw.
    /// </summary>
    [Fact]
    public void LogError_EmptySource_DoesNotThrow()
    {
        var ex = Record.Exception(() => _sut.LogError(string.Empty, "msg"));
        Assert.Null(ex);
    }

    /// <summary>
    /// Verifies that logging with empty message does not throw.
    /// </summary>
    [Fact]
    public void LogError_EmptyMessage_DoesNotThrow()
    {
        var ex = Record.Exception(() => _sut.LogError("__PLT__", string.Empty));
        Assert.Null(ex);
    }

    /// <summary>
    /// Verifies that multiple rapid log calls don't lose entries.
    /// </summary>
    [Fact]
    public void MultipleRapidLogCalls_AllEntriesStored()
    {
        const string src = "__PLT_Rapid__";
        for (int i = 0; i < 100; i++)
        {
            _sut.LogError(src, $"Msg {i}");
        }

        var entries = _sut.GetEntries(source: src, limit: 500);
        Assert.Equal(100, entries.Count);
    }

    /// <summary>
    /// Verifies that GetEntries returns a read-only collection (snapshot).
    /// </summary>
    [Fact]
    public void GetEntries_ReturnsReadOnlySnapshot()
    {
        const string src = "__PLT_Snap__";
        _sut.LogError(src, "Before");
        var snapshot = _sut.GetEntries(source: src);

        // Add more after taking snapshot
        _sut.LogError(src, "After");

        // Original snapshot should still have only 1 entry
        Assert.Single(snapshot);
        Assert.Equal("Before", snapshot[0].Message);

        // New query should have 2
        Assert.Equal(2, _sut.GetEntries(source: src).Count);
    }

    // ===== Dynamic Log Level Change (mid-flight) =====

    /// <summary>
    /// Verifies that changing the log level at runtime takes effect immediately
    /// without restart. This simulates a user changing PluginLogLevel in Settings
    /// and saving — the very next log call should respect the new level.
    /// GetConfiguredMinLevel() reads plugin.Configuration.PluginLogLevel live
    /// on every AddEntry call (no caching), so changes are instant.
    /// </summary>
    [Fact]
    public void LogLevel_ChangeMidFlight_TakesEffectImmediately()
    {
        const string src = "__PLT_MidFlight__";

        // Phase 1: Start with ERROR level — only errors stored
        _sut.TestMinLevelOverride = "ERROR";
        _sut.LogDebug(src, "debug-phase1");
        _sut.LogInfo(src, "info-phase1");
        _sut.LogWarning(src, "warn-phase1");
        _sut.LogError(src, "error-phase1");

        var phase1 = _sut.GetEntries(source: src);
        Assert.Single(phase1);
        Assert.Equal("error-phase1", phase1[0].Message);

        // Phase 2: User changes level to DEBUG — all levels now stored
        _sut.TestMinLevelOverride = "DEBUG";
        _sut.LogDebug(src, "debug-phase2");
        _sut.LogInfo(src, "info-phase2");

        var phase2 = _sut.GetEntries(source: src);
        Assert.Equal(3, phase2.Count); // error-phase1 + debug-phase2 + info-phase2
        Assert.Contains(phase2, e => e.Message == "debug-phase2");
        Assert.Contains(phase2, e => e.Message == "info-phase2");

        // Phase 3: User changes level to WARN — debug/info no longer stored
        _sut.TestMinLevelOverride = "WARN";
        _sut.LogDebug(src, "debug-phase3");
        _sut.LogInfo(src, "info-phase3");
        _sut.LogWarning(src, "warn-phase3");

        var phase3 = _sut.GetEntries(source: src);
        Assert.Equal(4, phase3.Count); // 3 from before + warn-phase3
        Assert.DoesNotContain(phase3, e => e.Message == "debug-phase3");
        Assert.DoesNotContain(phase3, e => e.Message == "info-phase3");
        Assert.Contains(phase3, e => e.Message == "warn-phase3");
    }

    /// <summary>
    /// Verifies that lowering the log level from ERROR to DEBUG immediately
    /// allows debug entries to be stored.
    /// </summary>
    [Fact]
    public void LogLevel_LowerFromErrorToDebug_DebugNowStored()
    {
        const string src = "__PLT_Lower__";

        // Start restrictive
        _sut.TestMinLevelOverride = "ERROR";
        _sut.LogDebug(src, "invisible");
        Assert.Empty(_sut.GetEntries(source: src));

        // Lower level — same call now succeeds
        _sut.TestMinLevelOverride = "DEBUG";
        _sut.LogDebug(src, "visible");
        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("visible", entries[0].Message);
    }

    /// <summary>
    /// Verifies that raising the log level from DEBUG to ERROR immediately
    /// prevents debug/info/warn from being stored.
    /// </summary>
    [Fact]
    public void LogLevel_RaiseFromDebugToError_LowerLevelsBlocked()
    {
        const string src = "__PLT_Raise__";

        // Start permissive
        _sut.TestMinLevelOverride = "DEBUG";
        _sut.LogDebug(src, "stored");
        Assert.Single(_sut.GetEntries(source: src));

        // Raise level — lower levels now blocked
        _sut.TestMinLevelOverride = "ERROR";
        _sut.LogDebug(src, "blocked-debug");
        _sut.LogInfo(src, "blocked-info");
        _sut.LogWarning(src, "blocked-warn");

        var entries = _sut.GetEntries(source: src);
        Assert.Single(entries); // only the original "stored"
        Assert.Equal("stored", entries[0].Message);
    }

    // ===== Constructor Guard & DI Integration =====

    /// <summary>
    /// Verifies that the constructor throws <see cref="ArgumentNullException"/>
    /// when <c>null</c> is passed for <see cref="IPluginConfigurationService"/>.
    /// This guards against misconfigured DI containers.
    /// </summary>
    [Fact]
    public void Constructor_NullConfigService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginLogService(null!));
    }

    /// <summary>
    /// Verifies that <see cref="PluginLogService"/> reads the configured log level
    /// from <see cref="IPluginConfigurationService"/> when no test override is set.
    /// This ensures the DI wiring is actually used at runtime.
    /// </summary>
    [Fact]
    public void GetConfiguredMinLevel_ReadsFromConfigService_WhenNoOverride()
    {
        var config = new PluginConfiguration { PluginLogLevel = "WARN" };
        var sut = TestMockFactory.CreatePluginLogService(config);
        sut.TestMinLevelOverride = null; // no override — use config service

        Assert.Equal("WARN", sut.GetConfiguredMinLevel());
    }

    /// <summary>
    /// Verifies that <see cref="TestMockFactory.CreatePluginLogService"/> with a custom
    /// configuration correctly wires the log level through <see cref="IPluginConfigurationService"/>.
    /// DEBUG entries should be filtered when config says ERROR.
    /// </summary>
    [Fact]
    public void CreatePluginLogService_WithCustomConfig_RespectsLogLevel()
    {
        const string src = "__PLT_CustomCfg__";
        var config = new PluginConfiguration { PluginLogLevel = "ERROR" };
        var sut = TestMockFactory.CreatePluginLogService(config);
        sut.TestMinLevelOverride = null; // rely on config service

        sut.LogDebug(src, "should-be-filtered");
        sut.LogInfo(src, "should-be-filtered");
        sut.LogWarning(src, "should-be-filtered");
        sut.LogError(src, "should-be-stored");

        var entries = sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("ERROR", entries[0].Level);
    }

    /// <summary>
    /// Verifies that <see cref="TestMockFactory.CreatePluginLogService"/> with default config
    /// uses "INFO" as the default log level (matching <see cref="Configuration.PluginConfiguration"/> default).
    /// </summary>
    [Fact]
    public void CreatePluginLogService_DefaultConfig_UsesInfoLevel()
    {
        const string src = "__PLT_DefCfg__";
        var sut = TestMockFactory.CreatePluginLogService();
        sut.TestMinLevelOverride = null; // rely on config service

        sut.LogDebug(src, "should-be-filtered");
        sut.LogInfo(src, "should-be-stored");

        var entries = sut.GetEntries(source: src);
        Assert.Single(entries);
        Assert.Equal("INFO", entries[0].Level);
    }
}
