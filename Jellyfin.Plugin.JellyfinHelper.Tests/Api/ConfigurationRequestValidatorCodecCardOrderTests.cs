using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Api;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Api;

/// <summary>
///     Tests for <see cref="ConfigurationRequestValidator.ValidateCodecCardOrder" />. The endpoint is
///     strict: it expects a true permutation of known ids, so unknown ids and duplicates are errors
///     rather than silently dropped (that lenient drop happens only in the backup path).
/// </summary>
public class ConfigurationRequestValidatorCodecCardOrderTests
{
    [Fact]
    public void ValidateCodecCardOrder_Null_IsValid()
    {
        Assert.Null(ConfigurationRequestValidator.ValidateCodecCardOrder(null));
    }

    [Fact]
    public void ValidateCodecCardOrder_EmptyList_IsValid()
    {
        Assert.Null(ConfigurationRequestValidator.ValidateCodecCardOrder([]));
    }

    [Fact]
    public void ValidateCodecCardOrder_ValidPermutation_IsValid()
    {
        var permutation = CodecCardCatalog.Canonical.Reverse().ToList();
        Assert.Null(ConfigurationRequestValidator.ValidateCodecCardOrder(permutation));
    }

    [Fact]
    public void ValidateCodecCardOrder_UnknownId_ReturnsError()
    {
        var error = ConfigurationRequestValidator.ValidateCodecCardOrder(["containers", "notACard"]);

        Assert.NotNull(error);
        Assert.Contains("notACard", error);
    }

    [Fact]
    public void ValidateCodecCardOrder_DuplicateId_ReturnsError()
    {
        var error = ConfigurationRequestValidator.ValidateCodecCardOrder(["containers", "containers"]);

        Assert.NotNull(error);
        Assert.Contains("Duplicate", error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCodecCardOrder_TooManyEntries_ReturnsError()
    {
        // 33 entries exceeds the cap of 32 regardless of content.
        var order = Enumerable.Range(0, 33).Select(i => $"card{i}").ToList();

        var error = ConfigurationRequestValidator.ValidateCodecCardOrder(order);

        Assert.NotNull(error);
        Assert.Contains("32", error);
    }

    [Fact]
    public void ValidateCodecCardOrder_OverlongEntry_ReturnsError()
    {
        var order = new List<string> { new('x', 65) };

        var error = ConfigurationRequestValidator.ValidateCodecCardOrder(order);

        Assert.NotNull(error);
        Assert.Contains("64", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCodecCardOrder_EmptyOrWhitespaceEntry_ReturnsError(string entry)
    {
        var error = ConfigurationRequestValidator.ValidateCodecCardOrder([entry]);

        Assert.NotNull(error);
        Assert.Contains("empty", error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCodecCardOrder_UnknownIdWithControlChars_StripsThemFromMessage()
    {
        // A crafted id must not forge log lines: the error echoes the id into the plugin log,
        // so embedded newlines/carriage returns are stripped before interpolation.
        var error = ConfigurationRequestValidator.ValidateCodecCardOrder(["bogus\r\nINJECTED"]);

        Assert.NotNull(error);
        Assert.DoesNotContain('\n', error!);
        Assert.DoesNotContain('\r', error!);
        // The visible characters survive so the message still names the offending id.
        Assert.Contains("bogusINJECTED", error, System.StringComparison.Ordinal);
    }
}
