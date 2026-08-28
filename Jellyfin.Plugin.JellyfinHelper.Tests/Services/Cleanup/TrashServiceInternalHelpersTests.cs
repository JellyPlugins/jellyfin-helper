using System;
using System.Text;
using Jellyfin.Plugin.JellyfinHelper.Services.Cleanup;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Cleanup;

/// <summary>
///     Tests for the TrashService internal static helpers (TruncateToSize, MeasureString, ExtractOriginalName, TryParseTrashTimestamp, PathComparison) that the higher-level path-length tests exercise only indirectly.
/// </summary>
public sealed class TrashServiceInternalHelpersTests
{
    // MeasureString
    //   Windows: char length (UTF-16 code units)
    //   Non-Windows: UTF-8 byte length

    [Fact]
    public void MeasureString_Null_ReturnsZero()
    {
        // Null must never crash MeasureString - it is called with directory names
        // that Path.GetDirectoryName can legitimately return as null (e.g. bare filenames).
        Assert.Equal(0, TrashService.MeasureString(null!));
    }

    [Fact]
    public void MeasureString_Empty_ReturnsZero()
    {
        Assert.Equal(0, TrashService.MeasureString(string.Empty));
    }

    [Fact]
    public void MeasureString_AsciiOnly_MatchesLengthAndUtf8ByteCount()
    {
        const string value = "hello-world";
        var measured = TrashService.MeasureString(value);
        // For pure ASCII, char length and UTF-8 byte count coincide - the platform
        // divergence doesn't manifest, so we can assert both invariants at once.
        Assert.Equal(value.Length, measured);
        Assert.Equal(Encoding.UTF8.GetByteCount(value), measured);
    }

    [Fact]
    public void MeasureString_MultibyteString_MatchesPlatformExpectation()
    {
        // 5 CJK code units. Char length = 5. UTF-8 byte length = 15 (3 bytes each).
        const string value = "日本語テス";
        var measured = TrashService.MeasureString(value);

        if (OperatingSystem.IsWindows())
        {
            // Windows enforcement is char-based. Surrogate-free string -> char count.
            Assert.Equal(value.Length, measured);
        }
        else
        {
            Assert.Equal(Encoding.UTF8.GetByteCount(value), measured);
        }
    }

    [Fact]
    public void MeasureString_EmojiSurrogatePair_MatchesPlatformExpectation()
    {
        // One emoji: 4 bytes in UTF-8, 2 chars in UTF-16 (surrogate pair). BUG GUARD: an early revision counted char length as UTF-8 length, which under-reported sizes on Unix and let mojibake through the truncation logic.
        const string emoji = "\U0001F3AC";
        var measured = TrashService.MeasureString(emoji);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(2, measured); // 2 UTF-16 code units
        }
        else
        {
            Assert.Equal(4, measured); // 4 UTF-8 bytes
        }
    }

    // TruncateToSize Contract: Empty / null / non-positive maxSize -> empty string Value that already fits -> returned as-is Overshoot -> truncated on encoding boundary (no split surrogate, no split UTF-8 sequence).

    [Fact]
    public void TruncateToSize_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TrashService.TruncateToSize(null!, 100));
    }

    [Fact]
    public void TruncateToSize_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TrashService.TruncateToSize(string.Empty, 100));
    }

    [Fact]
    public void TruncateToSize_ZeroMaxSize_ReturnsEmpty()
    {
        // A zero budget must not produce a single-char result - otherwise callers
        // would try to Directory.Move to an over-budget path and hit IOException.
        Assert.Equal(string.Empty, TrashService.TruncateToSize("anything", 0));
    }

    [Fact]
    public void TruncateToSize_NegativeMaxSize_ReturnsEmpty()
    {
        // Negative budgets originate from GetMaxComponentSize when the directory itself
        // already exhausts the platform limit. The helper must degrade cleanly, not throw.
        Assert.Equal(string.Empty, TrashService.TruncateToSize("anything", -5));
    }

    [Fact]
    public void TruncateToSize_ValueAlreadyFits_ReturnsUnchanged()
    {
        // No-op path: the value fits comfortably within the budget.
        const string value = "short-name";
        Assert.Equal(value, TrashService.TruncateToSize(value, 100));
    }

    [Fact]
    public void TruncateToSize_ExactBudget_ReturnsUnchanged()
    {
        // Boundary condition: the value exactly hits the budget. Off-by-one bugs
        // (using < instead of <=) would truncate one char here.
        const string value = "abcde"; // 5 ASCII chars
        Assert.Equal(value, TrashService.TruncateToSize(value, 5));
    }

    [Fact]
    public void TruncateToSize_Ascii_TruncatesToBudget()
    {
        const string value = "abcdefghij";
        var truncated = TrashService.TruncateToSize(value, 4);
        Assert.Equal("abcd", truncated);
    }

    [Fact]
    public void TruncateToSize_WindowsSurrogatePair_DoesNotSplit()
    {
        // BUG GUARD: naive char-slicing at an odd budget would split a surrogate pair
        // producing an invalid UTF-16 string. The helper must step back one code unit.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Two emojis = 4 UTF-16 code units. Budget = 3 lands mid-surrogate; must drop back to 2.
        const string value = "\U0001F3AC\U0001F3AC";
        var truncated = TrashService.TruncateToSize(value, 3);

        // Result must be exactly one emoji (2 code units), not a truncated surrogate half.
        Assert.Equal(2, truncated.Length);
        // Re-encoding must round-trip cleanly.
        var reEncoded = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(truncated));
        Assert.Equal(truncated, reEncoded);
    }

    [Fact]
    public void TruncateToSize_UnixMultibyteCharacter_DoesNotSplitByteSequence()
    {
        // Rune-based enumeration must stop BEFORE a 3-byte CJK sequence that would overshoot.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // 3 CJK chars = 9 UTF-8 bytes. Budget = 8 must land after 2 chars (6 bytes), not
        // split the third one.
        const string value = "日本語";
        var truncated = TrashService.TruncateToSize(value, 8);
        Assert.Equal("日本", truncated);

        // Round-trip through UTF-8 must succeed - verifies we didn't emit a broken sequence.
        var reEncoded = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(truncated));
        Assert.Equal(truncated, reEncoded);
    }

    [Fact]
    public void TruncateToSize_UnixEmoji_DoesNotSplitFourByteSequence()
    {
        // Emoji = 4 UTF-8 bytes each. Budget of 3 must produce empty (no partial emoji).
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string value = "\U0001F3AC"; // 4 bytes
        var truncated = TrashService.TruncateToSize(value, 3);
        Assert.Equal(string.Empty, truncated);
    }

    [Fact]
    public void TruncateToSize_UnixBudgetExactlyOneEmoji_KeepsIt()
    {
        // A budget that exactly fits one emoji byte-sequence must not drop it.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const string value = "\U0001F3AC\U0001F3AC"; // Two emoji = 8 bytes
        var truncated = TrashService.TruncateToSize(value, 4);
        Assert.Equal("\U0001F3AC", truncated);
    }

    // ExtractOriginalName Reconstruct the human-readable name from a trashed path prefix. Format: "yyyyMMdd-HHmmss_<original>" -> "<original>" Bare / malformed input passes through untouched.

    [Fact]
    public void ExtractOriginalName_ValidTimestampPrefix_ReturnsSuffixOnly()
    {
        var result = TrashService.ExtractOriginalName("20260601-120000_The Movie.mkv");
        Assert.Equal("The Movie.mkv", result);
    }

    [Fact]
    public void ExtractOriginalName_Empty_ReturnsInputUnchanged()
    {
        // Documented tolerance: empty and null pass through untouched.
        Assert.Equal(string.Empty, TrashService.ExtractOriginalName(string.Empty));
    }

    [Fact]
    public void ExtractOriginalName_Null_ReturnsNull()
    {
        // Null input flows through null-checks and returns null (documented behaviour).
        Assert.Null(TrashService.ExtractOriginalName(null!));
    }

    [Fact]
    public void ExtractOriginalName_TooShort_ReturnsUnchanged()
    {
        // Anything shorter than "yyyyMMdd-HHmmss_" (16 chars) cannot possibly carry a
        // timestamp prefix - it must pass through unchanged.
        const string tiny = "short";
        Assert.Equal(tiny, TrashService.ExtractOriginalName(tiny));
    }

    [Fact]
    public void ExtractOriginalName_ExactlyLengthOfTimestampFormat_ReturnsUnchanged()
    {
        // 15 chars = exactly the format length - but with no trailing underscore + original name
        // the guard "trashItemName.Length <= TimestampFormat.Length + 1" fires and we pass through.
        const string boundary = "20260601-120000"; // exactly 15 chars
        Assert.Equal(boundary, TrashService.ExtractOriginalName(boundary));
    }

    [Fact]
    public void ExtractOriginalName_NoUnderscoreAfterTimestamp_ReturnsUnchanged()
    {
        // 16 chars but the 16th is not "_" - pattern doesn't match, pass through unchanged.
        // BUG GUARD: a naive substring approach would produce garbage here.
        const string bogus = "20260601-1200000"; // 16 chars, no underscore
        Assert.Equal(bogus, TrashService.ExtractOriginalName(bogus));
    }

    [Fact]
    public void ExtractOriginalName_UnderscoreAtPositionButBadTimestamp_ReturnsUnchanged()
    {
        // Underscore at the right position but the preceding chars are not a valid
        // yyyyMMdd-HHmmss timestamp. TryParseTrashTimestamp must reject it.
        const string bogus = "abcdefgh-ijklmn_original";
        Assert.Equal(bogus, TrashService.ExtractOriginalName(bogus));
    }

    [Fact]
    public void ExtractOriginalName_TimestampPlusUnderscoreOnly_ReturnsInputUnchanged_BoundaryGuard()
    {
        // 16 chars input: valid timestamp + underscore + NOTHING. The guard "trashItemName.Length <= TimestampFormat.Length + 1" (i.e.
        const string exact16 = "20260601-120000_";
        Assert.Equal(16, exact16.Length);
        var result = TrashService.ExtractOriginalName(exact16);
        Assert.Equal(exact16, result);
    }

    [Fact]
    public void ExtractOriginalName_TimestampPlusUnderscorePlusOneChar_ExtractsThatChar()
    {
        // 17 chars: valid timestamp + underscore + single char suffix. The guard "<= 16" does NOT fire -> we get the "x" suffix back.
        var result = TrashService.ExtractOriginalName("20260601-120000_x");
        Assert.Equal("x", result);
    }

    // TryParseTrashTimestamp
    //   Verifies the timestamp-prefix parser used by purge & GetTrashContents.

    [Fact]
    public void TryParseTrashTimestamp_ValidFormat_ReturnsUtcTimestamp()
    {
        var ok = TrashService.TryParseTrashTimestamp("20260601-120000_whatever", out var ts);
        Assert.True(ok);
        // AssumeUniversal + AdjustToUniversal means the result is UTC.
        Assert.Equal(DateTimeKind.Utc, ts.Kind);
        Assert.Equal(2026, ts.Year);
        Assert.Equal(6, ts.Month);
        Assert.Equal(1, ts.Day);
        Assert.Equal(12, ts.Hour);
        Assert.Equal(0, ts.Minute);
        Assert.Equal(0, ts.Second);
    }

    [Fact]
    public void TryParseTrashTimestamp_Null_ReturnsFalse()
    {
        var ok = TrashService.TryParseTrashTimestamp(null!, out var ts);
        Assert.False(ok);
        Assert.Equal(DateTime.MinValue, ts);
    }

    [Fact]
    public void TryParseTrashTimestamp_Empty_ReturnsFalse()
    {
        var ok = TrashService.TryParseTrashTimestamp(string.Empty, out var ts);
        Assert.False(ok);
        Assert.Equal(DateTime.MinValue, ts);
    }

    [Fact]
    public void TryParseTrashTimestamp_TooShort_ReturnsFalse()
    {
        // 15 chars is exactly the format length but the guard demands >= length + 1,
        // so this must fail.
        var ok = TrashService.TryParseTrashTimestamp("20260601-120000", out var ts);
        Assert.False(ok);
        Assert.Equal(DateTime.MinValue, ts);
    }

    [Fact]
    public void TryParseTrashTimestamp_MalformedDate_ReturnsFalse()
    {
        // The prefix "20261301" is February-off month 13 -> invalid.
        var ok = TrashService.TryParseTrashTimestamp("20261301-000000_bad", out var ts);
        Assert.False(ok);
        Assert.Equal(DateTime.MinValue, ts);
    }

    [Fact]
    public void TryParseTrashTimestamp_MissingSeparator_ReturnsFalse()
    {
        // Missing dash between date and time - pattern won't match.
        var ok = TrashService.TryParseTrashTimestamp("20260601 120000_bad", out var ts);
        Assert.False(ok);
        Assert.Equal(DateTime.MinValue, ts);
    }

    // PathComparison - platform-aware string comparison used for path prefix checks.

    [Fact]
    public void PathComparison_MatchesPlatformCasePolicy()
    {
        // Windows + macOS use OrdinalIgnoreCase; Linux uses Ordinal.
        // Confirm the exported constant honours the same policy used by all path guards.
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Assert.Equal(expected, TrashService.PathComparison);
    }

    [Fact]
    public void PathComparison_IsCaseInsensitiveOnCaseInsensitiveFilesystems()
    {
        // On macOS/Windows a lower-case reference to a Trash path would fail to match the canonical mixed-case one if this ever regressed to Ordinal.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.True("Trash".Equals("trash", TrashService.PathComparison));
        }
        else
        {
            Assert.False("Trash".Equals("trash", TrashService.PathComparison));
        }
    }
}
