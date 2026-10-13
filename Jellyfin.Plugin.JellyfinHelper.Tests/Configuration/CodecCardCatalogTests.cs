using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Configuration;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Configuration;

/// <summary>
///     Tests for <see cref="CodecCardCatalog" />: the single normalization rule shared by the
///     settings endpoint and the backup pipeline, plus the <see cref="CodecCardCatalog.IsKnown" /> guard.
/// </summary>
public class CodecCardCatalogTests
{
    [Fact]
    public void Normalize_NullInput_ReturnsFullCanonicalOrder()
    {
        var result = CodecCardCatalog.Normalize(null);
        Assert.Equal(CodecCardCatalog.Canonical, result);
    }

    [Fact]
    public void Normalize_EmptyInput_ReturnsFullCanonicalOrder()
    {
        var result = CodecCardCatalog.Normalize([]);
        Assert.Equal(CodecCardCatalog.Canonical, result);
    }

    [Fact]
    public void Normalize_Permutation_RoundTripsExactly()
    {
        // A full permutation of the canonical set must come back unchanged - nothing dropped,
        // nothing reordered, nothing appended.
        var permutation = CodecCardCatalog.Canonical.Reverse().ToList();

        var result = CodecCardCatalog.Normalize(permutation);

        Assert.Equal(permutation, result);
    }

    [Fact]
    public void Normalize_TrimsWhitespaceAroundIds()
    {
        var result = CodecCardCatalog.Normalize(["  containers  ", " resolutions"]);

        Assert.Equal("containers", result[0]);
        Assert.Equal("resolutions", result[1]);
    }

    [Fact]
    public void Normalize_Duplicates_FirstWins()
    {
        // The first occurrence sets the position; later duplicates are dropped.
        var result = CodecCardCatalog.Normalize(["containers", "resolutions", "containers"]);

        Assert.Equal("containers", result[0]);
        Assert.Equal("resolutions", result[1]);
        Assert.Single(result, id => id == "containers");
        // Still a complete set covering every canonical id exactly once.
        Assert.Equal(CodecCardCatalog.Canonical.Count, result.Count);
        Assert.Equal(CodecCardCatalog.Canonical.OrderBy(x => x), result.OrderBy(x => x));
    }

    [Fact]
    public void Normalize_UnknownIds_AreDropped()
    {
        var result = CodecCardCatalog.Normalize(["containers", "notARealCard", "resolutions"]);

        Assert.DoesNotContain("notARealCard", result);
        Assert.Equal("containers", result[0]);
        Assert.Equal("resolutions", result[1]);
    }

    [Fact]
    public void Normalize_MissingIds_AppendedInCanonicalOrder()
    {
        // A partial order keeps its leading explicit ids, then the omitted canonical ids are
        // appended in their canonical sequence so no card is ever lost.
        var partial = new List<string> { "watched", "containers" };

        var result = CodecCardCatalog.Normalize(partial);

        Assert.Equal("watched", result[0]);
        Assert.Equal("containers", result[1]);
        Assert.Equal(CodecCardCatalog.Canonical.Count, result.Count);

        var appended = result.Skip(2).ToList();
        var expectedAppended = CodecCardCatalog.Canonical
            .Where(id => id != "watched" && id != "containers")
            .ToList();
        Assert.Equal(expectedAppended, appended);
    }

    [Theory]
    [InlineData("containers")]
    [InlineData("resolutions")]
    [InlineData("bookFormats")]
    public void IsKnown_CanonicalId_ReturnsTrue(string id)
    {
        Assert.True(CodecCardCatalog.IsKnown(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Containers")]
    [InlineData("unknownCard")]
    public void IsKnown_UnknownOrNullOrWrongCase_ReturnsFalse(string? id)
    {
        Assert.False(CodecCardCatalog.IsKnown(id));
    }

    [Fact]
    public void Canonical_ContainsExpectedElevenIds()
    {
        Assert.Equal(11, CodecCardCatalog.Canonical.Count);
    }

    [Fact]
    public void Canonical_IsReadOnly_CannotBeMutatedViaCast()
    {
        // The property hands out a read-only view, never the backing array, so a caller that casts
        // the IReadOnlyList back to a mutable collection cannot corrupt the canonical order.
        Assert.False(CodecCardCatalog.Canonical is string[]);
        Assert.IsNotType<List<string>>(CodecCardCatalog.Canonical);

        var mutable = CodecCardCatalog.Canonical as IList<string>;
        if (mutable is not null)
        {
            Assert.Throws<System.NotSupportedException>(() => mutable[0] = "mutated");
        }
    }
}
