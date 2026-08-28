using Jellyfin.Plugin.JellyfinHelper.Services;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services;

/// <summary>
///     Tests for DateTimeNormalization - every branch of the UTC coercion logic. Coverage here is critical because this helper is called from every result DTO's timestamp setter, so a subtle bug (e.g.
/// </summary>
public class DateTimeNormalizationTests
{
    [Fact]
    public void ToUtc_UtcKind_ReturnsSameInstance()
    {
        var utc = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var result = DateTimeNormalization.ToUtc(utc);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(utc, result);
        Assert.Equal(utc.Ticks, result.Ticks);
    }

    [Fact]
    public void ToUtc_LocalKind_ConvertsToUniversalTime()
    {
        // BUG GUARD: Local values MUST be CONVERTED to UTC (subtract the local offset), not just relabeled. If a future refactor uses SpecifyKind on Local values, this test surfaces it - the resulting Utc would differ from the expected universal representation.
        var local = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Local);
        var expected = local.ToUniversalTime();

        var result = DateTimeNormalization.ToUtc(local);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ToUtc_UnspecifiedKind_LabelsAsUtcWithoutConversion()
    {
        // BUG GUARD: Unspecified DateTimes are treated as ALREADY BEING in UTC and just relabeled - NOT converted.
        var unspecified = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Unspecified);

        var result = DateTimeNormalization.ToUtc(unspecified);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        // Ticks must be unchanged - the value is relabeled, not shifted.
        Assert.Equal(unspecified.Ticks, result.Ticks);
    }

    [Fact]
    public void ToUtc_NullableWithValue_PassesThroughToNonNullableOverload()
    {
        DateTime? value = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Unspecified);

        var result = DateTimeNormalization.ToUtc(value);

        var normalized = Assert.IsType<DateTime>(result);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(value.Value.Ticks, normalized.Ticks);
    }

    [Fact]
    public void ToUtc_NullableNull_ReturnsNull()
    {
        DateTime? value = null;

        var result = DateTimeNormalization.ToUtc(value);

        Assert.Null(result);
    }

    [Fact]
    public void ToUtc_NullableLocalKind_ConvertsToUtc()
    {
        DateTime? local = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Local);
        var expected = local.Value.ToUniversalTime();

        var result = DateTimeNormalization.ToUtc(local);

        var normalized = Assert.IsType<DateTime>(result);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ToUtc_MinValueUtc_ReturnsMinValue()
    {
        // Edge case: DateTime.MinValue with Utc kind should be idempotent.
        var minUtc = new DateTime(DateTime.MinValue.Ticks, DateTimeKind.Utc);
        var result = DateTimeNormalization.ToUtc(minUtc);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(minUtc.Ticks, result.Ticks);
    }

    [Fact]
    public void ToUtc_MaxValueUtc_ReturnsMaxValue()
    {
        // Edge case: DateTime.MaxValue with Utc kind should be idempotent.
        var maxUtc = new DateTime(DateTime.MaxValue.Ticks, DateTimeKind.Utc);
        var result = DateTimeNormalization.ToUtc(maxUtc);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(maxUtc.Ticks, result.Ticks);
    }
}