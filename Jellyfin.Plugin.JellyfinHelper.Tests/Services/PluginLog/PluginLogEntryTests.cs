using Jellyfin.Plugin.JellyfinHelper.Services.PluginLog;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.PluginLog;

/// <summary>
/// Unit tests for <see cref="PluginLogEntry"/>.
/// </summary>
public class PluginLogEntryTests
{
    // ===== Default Values =====

    /// <summary>
    /// Verifies that a default-constructed entry has empty strings for Level, Source, and Message.
    /// </summary>
    [Fact]
    public void DefaultEntry_HasEmptyStringsForRequiredFields()
    {
        var entry = new PluginLogEntry();

        Assert.Equal(string.Empty, entry.Level);
        Assert.Equal(string.Empty, entry.Source);
        Assert.Equal(string.Empty, entry.Message);
    }

    /// <summary>
    /// Verifies that a default-constructed entry has a default DateTime for Timestamp.
    /// </summary>
    [Fact]
    public void DefaultEntry_HasDefaultTimestamp()
    {
        var entry = new PluginLogEntry();

        Assert.Equal(default(DateTime), entry.Timestamp);
    }

    /// <summary>
    /// Verifies that Exception defaults to null.
    /// </summary>
    [Fact]
    public void DefaultEntry_ExceptionIsNull()
    {
        var entry = new PluginLogEntry();

        Assert.Null(entry.Exception);
    }

    // ===== Property Init =====

    /// <summary>
    /// Verifies that Timestamp can be set via init.
    /// </summary>
    [Fact]
    public void Timestamp_CanBeSetViaInit()
    {
        var now = new DateTime(2025, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var entry = new PluginLogEntry { Timestamp = now };

        Assert.Equal(now, entry.Timestamp);
    }

    /// <summary>
    /// Verifies that Level can be set via init.
    /// </summary>
    [Fact]
    public void Level_CanBeSetViaInit()
    {
        var entry = new PluginLogEntry { Level = "ERROR" };

        Assert.Equal("ERROR", entry.Level);
    }

    /// <summary>
    /// Verifies that Source can be set via init.
    /// </summary>
    [Fact]
    public void Source_CanBeSetViaInit()
    {
        var entry = new PluginLogEntry { Source = "Trickplay" };

        Assert.Equal("Trickplay", entry.Source);
    }

    /// <summary>
    /// Verifies that Message can be set via init.
    /// </summary>
    [Fact]
    public void Message_CanBeSetViaInit()
    {
        var entry = new PluginLogEntry { Message = "Cleanup completed" };

        Assert.Equal("Cleanup completed", entry.Message);
    }

    /// <summary>
    /// Verifies that Exception can be set via init with a string value.
    /// </summary>
    [Fact]
    public void Exception_CanBeSetViaInit()
    {
        var entry = new PluginLogEntry { Exception = "System.IO.IOException: Disk full" };

        Assert.Equal("System.IO.IOException: Disk full", entry.Exception);
    }

    /// <summary>
    /// Verifies that Exception can be explicitly set to null.
    /// </summary>
    [Fact]
    public void Exception_CanBeSetToNull()
    {
        var entry = new PluginLogEntry { Exception = null };

        Assert.Null(entry.Exception);
    }

    // ===== Full Object Init =====

    /// <summary>
    /// Verifies that all properties can be set together via object initializer.
    /// </summary>
    [Fact]
    public void AllProperties_CanBeSetTogether()
    {
        var timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var entry = new PluginLogEntry
        {
            Timestamp = timestamp,
            Level = "WARN",
            Source = "API",
            Message = "Rate limit exceeded",
            Exception = "System.Exception: Too many requests",
        };

        Assert.Equal(timestamp, entry.Timestamp);
        Assert.Equal("WARN", entry.Level);
        Assert.Equal("API", entry.Source);
        Assert.Equal("Rate limit exceeded", entry.Message);
        Assert.Equal("System.Exception: Too many requests", entry.Exception);
    }

    /// <summary>
    /// Verifies that all properties can be set together without an exception.
    /// </summary>
    [Fact]
    public void AllProperties_WithoutException_HasNullException()
    {
        var entry = new PluginLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = "INFO",
            Source = "Backup",
            Message = "Backup exported successfully",
        };

        Assert.Equal("INFO", entry.Level);
        Assert.Equal("Backup", entry.Source);
        Assert.Equal("Backup exported successfully", entry.Message);
        Assert.Null(entry.Exception);
    }

    // ===== Level Values =====

    /// <summary>
    /// Verifies that all standard log levels can be assigned.
    /// </summary>
    [Theory]
    [InlineData("DEBUG")]
    [InlineData("INFO")]
    [InlineData("WARN")]
    [InlineData("ERROR")]
    public void Level_AcceptsAllStandardLevels(string level)
    {
        var entry = new PluginLogEntry { Level = level };

        Assert.Equal(level, entry.Level);
    }

    /// <summary>
    /// Verifies that arbitrary level strings are accepted (no validation in the model).
    /// </summary>
    [Fact]
    public void Level_AcceptsArbitraryStrings()
    {
        var entry = new PluginLogEntry { Level = "CUSTOM_LEVEL" };

        Assert.Equal("CUSTOM_LEVEL", entry.Level);
    }

    // ===== Edge Cases =====

    /// <summary>
    /// Verifies that empty string values are valid for all string properties.
    /// </summary>
    [Fact]
    public void EmptyStrings_AreValidForAllStringProperties()
    {
        var entry = new PluginLogEntry
        {
            Level = string.Empty,
            Source = string.Empty,
            Message = string.Empty,
            Exception = string.Empty,
        };

        Assert.Equal(string.Empty, entry.Level);
        Assert.Equal(string.Empty, entry.Source);
        Assert.Equal(string.Empty, entry.Message);
        Assert.Equal(string.Empty, entry.Exception);
    }

    /// <summary>
    /// Verifies that very long strings are accepted without issue.
    /// </summary>
    [Fact]
    public void LongStrings_AreAccepted()
    {
        var longMessage = new string('A', 10_000);
        var entry = new PluginLogEntry { Message = longMessage };

        Assert.Equal(10_000, entry.Message.Length);
        Assert.Equal(longMessage, entry.Message);
    }

    /// <summary>
    /// Verifies that Unicode characters are preserved in all string properties.
    /// </summary>
    [Fact]
    public void UnicodeCharacters_ArePreserved()
    {
        var entry = new PluginLogEntry
        {
            Source = "日本語テスト",
            Message = "Ünîcödé: 你好世界 🎬",
            Exception = "Ошибка: файл не найден",
        };

        Assert.Equal("日本語テスト", entry.Source);
        Assert.Equal("Ünîcödé: 你好世界 🎬", entry.Message);
        Assert.Equal("Ошибка: файл не найден", entry.Exception);
    }

    /// <summary>
    /// Verifies that Timestamp preserves DateTimeKind.Utc.
    /// </summary>
    [Fact]
    public void Timestamp_PreservesUtcKind()
    {
        var utcTime = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var entry = new PluginLogEntry { Timestamp = utcTime };

        Assert.Equal(DateTimeKind.Utc, entry.Timestamp.Kind);
    }

    /// <summary>
    /// Verifies that Timestamp preserves DateTimeKind.Local.
    /// </summary>
    [Fact]
    public void Timestamp_PreservesLocalKind()
    {
        var localTime = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Local);
        var entry = new PluginLogEntry { Timestamp = localTime };

        Assert.Equal(DateTimeKind.Local, entry.Timestamp.Kind);
    }

    /// <summary>
    /// Verifies that MinValue DateTime is accepted.
    /// </summary>
    [Fact]
    public void Timestamp_AcceptsMinValue()
    {
        var entry = new PluginLogEntry { Timestamp = DateTime.MinValue };

        Assert.Equal(DateTime.MinValue, entry.Timestamp);
    }

    /// <summary>
    /// Verifies that MaxValue DateTime is accepted.
    /// </summary>
    [Fact]
    public void Timestamp_AcceptsMaxValue()
    {
        var entry = new PluginLogEntry { Timestamp = DateTime.MaxValue };

        Assert.Equal(DateTime.MaxValue, entry.Timestamp);
    }
}
