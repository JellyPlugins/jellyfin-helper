using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Seerr.Discovery;

/// <summary>
///     Tests for <see cref="NullableDateTimeConverter"/>.
///     TMDb/Seerr responses frequently contain empty strings (<c>""</c>) or malformed
///     date values for optional fields like <c>release_date</c> or <c>first_air_date</c>.
///     A stock <c>JsonSerializer</c> would throw <see cref="JsonException"/> in that case
///     and drop the entire response - this converter must degrade gracefully.
/// </summary>
public class NullableDateTimeConverterTests
{
    /// <summary>Wraps the target property in a container so the converter is actually exercised.</summary>
    private sealed class Container
    {
        public DateTime? Value { get; set; }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new NullableDateTimeConverter());
        return opts;
    }

    // -----------------------------------------------------------------------
    // Read: valid inputs
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_NullToken_ReturnsNull()
    {
        var json = "{\"Value\":null}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Read_MissingProperty_ReturnsNull()
    {
        // Property absent → default(DateTime?) = null.
        var json = "{}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Read_ValidIso8601String_ReturnsParsedDateTime()
    {
        var json = "{\"Value\":\"2024-06-15T10:30:00Z\"}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.NotNull(result!.Value);
        // Compare against the canonical UTC value regardless of local kind conversion.
        Assert.Equal(new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc), result.Value!.Value.ToUniversalTime());
    }

    [Fact]
    public void Read_DateOnlyFormat_ReturnsParsedDateTime()
    {
        // TMDb release_date is normally "YYYY-MM-DD" (no time).
        var json = "{\"Value\":\"2024-06-15\"}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.NotNull(result!.Value);
        Assert.Equal(2024, result.Value!.Value.Year);
        Assert.Equal(6, result.Value!.Value.Month);
        Assert.Equal(15, result.Value!.Value.Day);
    }

    // -----------------------------------------------------------------------
    // Read: graceful degradation - the core motivation for this converter
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_EmptyString_ReturnsNull_DoesNotThrow()
    {
        // TMDb returns "" for unknown release dates. Must NOT throw.
        var json = "{\"Value\":\"\"}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Theory]
    [InlineData(" ", " ")]
    [InlineData("   ", "   ")]
    [InlineData("\t", "\\t")]
    [InlineData("\n", "\\n")]
    [InlineData("\r\n", "\\r\\n")]
    public void Read_WhitespaceOnlyString_ReturnsNull(string _, string jsonEscapedWhitespace)
    {
        // The JSON spec forbids unescaped control chars inside string literals, so tab / newline
        // must be represented as their JSON escape sequences (\t, \n, \r) in the wire payload.
        var json = $"{{\"Value\":\"{jsonEscapedWhitespace}\"}}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("hello world")]
    [InlineData("13/13/2024")]
    [InlineData("2024-13-45")]
    [InlineData("0000-00-00")]
    [InlineData("abcd-ef-gh")]
    public void Read_UnparseableString_ReturnsNull_InsteadOfThrowing(string bogus)
    {
        // Without the converter, System.Text.Json would blow up here
        // and discard the whole containing object. The contract is: return null,
        // consume the token, keep the caller alive.
        var json = $"{{\"Value\":\"{bogus}\"}}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Read_NumericToken_ReturnsNull_AndDoesNotThrow()
    {
        // TMDb should never return a numeric date, but if a proxy mangles the payload
        // we must survive rather than crashing the entire discovery pipeline.
        var json = "{\"Value\":12345}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Read_BooleanToken_ReturnsNull_AndDoesNotThrow()
    {
        var json = "{\"Value\":true}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Read_ObjectToken_ReturnsNull_AndDoesNotThrow()
    {
        // Defensive: an unexpected nested object where a date-string was expected
        // must be skipped, not fatal. Reader.Skip() is the contract.
        var json = "{\"Value\":{\"nested\":\"stuff\",\"count\":1}}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Read_ArrayToken_ReturnsNull_AndDoesNotThrow()
    {
        var json = "{\"Value\":[1,2,3]}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.Null(result!.Value);
    }

    [Fact]
    public void Read_UnexpectedToken_DoesNotBreakSiblingPropertyDeserialization()
    {
        // If the converter fails to Skip() an unexpected object token,
        // the JsonReader position is corrupt and the next property parse crashes.
        var payload = new { Value = new { garbage = "x" }, After = 42 };
        var json = JsonSerializer.Serialize(payload);

        // A container capturing both the date field and a following int field.
        var opts = CreateOptions();
        var doc = JsonSerializer.Deserialize<AfterContainer>(json, opts);
        Assert.NotNull(doc);
        Assert.Null(doc!.Value);
        Assert.Equal(42, doc.After);
    }

    private sealed class AfterContainer
    {
        public DateTime? Value { get; set; }
        public int After { get; set; }
    }

    // -----------------------------------------------------------------------
    // Write: round-trip
    // -----------------------------------------------------------------------

    [Fact]
    public void Write_NullValue_ProducesJsonNull()
    {
        var container = new Container { Value = null };
        var json = JsonSerializer.Serialize(container, CreateOptions());
        Assert.Contains("\"Value\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_NonNullValue_ProducesRoundTrippableIsoString()
    {
        var container = new Container
        {
            Value = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc)
        };
        var json = JsonSerializer.Serialize(container, CreateOptions());
        // Must be a JSON string, not a number, not null.
        Assert.Contains("\"Value\":\"", json, StringComparison.Ordinal);
        // The wire format itself carries the UTC marker ("Z") - proof that Write emitted UTC.
        Assert.Contains("Z\"", json, StringComparison.Ordinal);

        // Round-trip: the absolute point in time and (with DateTimeStyles.RoundtripKind
        // in the converter) also the Kind must survive.
        var roundTripped = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(roundTripped);
        Assert.NotNull(roundTripped!.Value);
        Assert.Equal(container.Value, roundTripped.Value);
    }

    [Fact]
    public void Write_UsesInvariantCulture_NotAffectedByAmbientCulture()
    {
        // If the converter used CurrentCulture, a German locale
        // ("15.06.2024 10:30:00") would break every downstream parser expecting ISO 8601.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var container = new Container
            {
                Value = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc)
            };
            var json = JsonSerializer.Serialize(container, CreateOptions());
            // The emitted string must be invariant ISO-8601 regardless of ambient culture.
            Assert.Contains("2024-06-15T10:30:00", json, StringComparison.Ordinal);
            Assert.DoesNotContain("15.06.2024", json, StringComparison.Ordinal);

            // Round-trip through en-US just to be extra pedantic.
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            var back = JsonSerializer.Deserialize<Container>(json, CreateOptions());
            Assert.NotNull(back);
            Assert.NotNull(back!.Value);
            Assert.Equal(container.Value.Value.ToUniversalTime(), back.Value!.Value.ToUniversalTime());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // -----------------------------------------------------------------------
    // Kind-preservation contract (regression tests for the RoundtripKind fix)
    // -----------------------------------------------------------------------

    [Fact]
    public void ReadWrite_UtcKind_IsPreservedThroughRoundTrip()
    {
        // Prior to using DateTimeStyles.RoundtripKind, the converter's Read()
        // silently downgraded UTC ("...Z") input to Kind=Local, shifting .Ticks by the
        // host's UTC offset. This test locks in the fixed behaviour so nobody accidentally
        // reverts to DateTimeStyles.None.
        var container = new Container
        {
            Value = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc)
        };
        var json = JsonSerializer.Serialize(container, CreateOptions());
        var back = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(back);
        Assert.NotNull(back!.Value);

        // Kind must survive round-trip.
        Assert.Equal(DateTimeKind.Utc, back.Value!.Value.Kind);
        // Ticks must be identical - no timezone shift happens.
        Assert.Equal(container.Value.Value.Ticks, back.Value.Value.Ticks);
        // Full equality (including Kind) holds now.
        Assert.Equal(container.Value, back.Value);
    }

    [Fact]
    public void Read_IsoStringWithZeroOffset_RepresentsSameInstantAsZ()
    {
        // NOTE on .NET semantics: with DateTimeStyles.RoundtripKind, "+00:00" is treated as
        // "local timezone with offset 0" and becomes Kind=Local, whereas the trailing "Z" is
        // treated as UTC and becomes Kind=Utc. This is intentional in .NET - offsets carry
        // wall-clock information, "Z" carries the UTC marker.
        // The instant is identical either way, which is what matters for callers doing
        // absolute comparisons via ToUniversalTime().
        var json = "{\"Value\":\"2024-06-15T10:30:00+00:00\"}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.NotNull(result!.Value);
        Assert.NotEqual(DateTimeKind.Unspecified, result.Value!.Value.Kind);
        Assert.Equal(
            new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
            result.Value.Value.ToUniversalTime());
    }

    [Fact]
    public void Read_IsoStringWithNonZeroOffset_DeserializesAsLocalOrUtc_ButRepresentsSameInstant()
    {
        // "+02:00" preserves Kind=Local but the underlying instant must match UTC 10:30.
        var json = "{\"Value\":\"2024-06-15T12:30:00+02:00\"}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.NotNull(result!.Value);
        // Kind is Local (or Utc if the machine happens to be UTC), never Unspecified for
        // offset-carrying strings.
        Assert.NotEqual(DateTimeKind.Unspecified, result.Value!.Value.Kind);
        // Instant must match 2024-06-15T10:30:00Z regardless of Kind.
        Assert.Equal(
            new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc),
            result.Value.Value.ToUniversalTime());
    }

    [Fact]
    public void Read_DateOnlyIsoString_YieldsUnspecifiedKind()
    {
        // "YYYY-MM-DD" carries no timezone info - must not be assumed to be UTC or Local.
        var json = "{\"Value\":\"2024-06-15\"}";
        var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(result);
        Assert.NotNull(result!.Value);
        Assert.Equal(DateTimeKind.Unspecified, result.Value!.Value.Kind);
    }

    [Fact]
    public void ReadInvariantCulture_ParsesEnglishFormattedDate_UnderGermanCulture()
    {
        // The Read() path MUST use InvariantCulture. Otherwise a
        // "6/15/2024" style string under de-DE culture would parse as 6 May 2024
        // (dd/MM/yyyy) or fail entirely, silently corrupting dates parsed from
        // Seerr responses.
        //
        // The previous version of this test used an ISO-8601 payload ("2024-06-15T…Z")
        // which parses identically in every culture - so it could not detect a
        // regression that removed the InvariantCulture argument. We now use the
        // US-format "6/15/2024" which is:
        //   * unambiguous under InvariantCulture (June 15, 2024)
        //   * INVALID under de-DE (day 15 of month 15 → FormatException)
        // If the InvariantCulture argument were dropped from DateTime.TryParse, the
        // Read() method would return null under de-DE and this test would fail.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var json = "{\"Value\":\"6/15/2024\"}";

            var result = JsonSerializer.Deserialize<Container>(json, CreateOptions());

            Assert.NotNull(result);
            Assert.NotNull(result!.Value);
            // Positive fields assertion: it MUST parse to June 15th, 2024.
            Assert.Equal(2024, result.Value!.Value.Year);
            Assert.Equal(6, result.Value.Value.Month);
            Assert.Equal(15, result.Value.Value.Day);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // -----------------------------------------------------------------------
    // Direct converter API (bypassing JsonSerializer) - exercises Write() edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void Write_LocalKindDateTime_RoundTripsThroughIsoString()
    {
        // Ensures the "O" format specifier is used, which preserves DateTimeKind.
        var container = new Container
        {
            Value = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Local)
        };
        var json = JsonSerializer.Serialize(container, CreateOptions());
        var back = JsonSerializer.Deserialize<Container>(json, CreateOptions());
        Assert.NotNull(back);
        Assert.NotNull(back!.Value);
        // Round-trip must preserve the point in time.
        Assert.Equal(container.Value.Value.ToUniversalTime(), back.Value!.Value.ToUniversalTime());
    }

    [Fact]
    public void Read_ValidDate_InsideLargerObject_DoesNotAffectOtherFields()
    {
        // The converter must consume exactly one token so surrounding
        // properties remain parseable.
        var opts = CreateOptions();
        var json = "{\"Before\":7,\"Value\":\"2024-06-15\",\"After\":42}";
        var doc = JsonSerializer.Deserialize<BeforeAfterContainer>(json, opts);
        Assert.NotNull(doc);
        Assert.Equal(7, doc!.Before);
        Assert.NotNull(doc.Value);
        Assert.Equal(42, doc.After);
    }

    private sealed class BeforeAfterContainer
    {
        public int Before { get; set; }
        public DateTime? Value { get; set; }
        public int After { get; set; }
    }
}
