using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.Services.Recommendation.Engine;

/// <summary>Localized metadata, compound TMDb categories, custom genres and display/training boundaries.</summary>
public sealed class GenreNormalizerTests
{
    [Theory]
    [InlineData("Abenteuer", "Adventure")]
    [InlineData("Aventura", "Adventure")]
    [InlineData("Aventure", "Adventure")]
    [InlineData("Äventyr", "Adventure")]
    [InlineData("Macera", "Adventure")]
    [InlineData("冒险", "Adventure")]
    [InlineData("Krimi", "Crime")]
    [InlineData("Komödie", "Comedy")]
    [InlineData("Comédia", "Comedy")]
    [InlineData("Familie", "Family")]
    [InlineData("Dram", "Drama")]
    [InlineData("Science-Fiction", "Science Fiction")]
    [InlineData("  Sci-Fi  ", "Science Fiction")]
    [InlineData("NEWS", "News")]
    [InlineData("Anime", "Anime")]
    [InlineData("Sport", "Sport")]
    [InlineData(null, "")]
    [InlineData(" ", "")]
    public void Name_UsesGenreMeaningNotLanguage(string? input, string expected)
        => Assert.Equal(expected, GenreNormalizer.Name(input));

    [Fact]
    public void Set_ExpandsTvGenresAndDoesNotDoubleCountTranslatedDuplicates()
    {
        var genres = GenreNormalizer.Set(["Action & Abenteuer", "Adventure", "Sci-Fi & Fantasy", "Fantasy", "War & Politics", "Krimi", "Crime", ""]);
        Assert.Equal(7, genres.Count);
        Assert.Contains("Action", genres);
        Assert.Contains("Adventure", genres);
        Assert.Contains("Science Fiction", genres);
        Assert.Contains("Fantasy", genres);
        Assert.Contains("War", genres);
        Assert.Contains("Politics", genres);
        Assert.Contains("Crime", genres);
        Assert.Empty(GenreNormalizer.Set(null));
    }

    [Theory]
    [InlineData("Action & Adventure", "Action", true)]
    [InlineData("Sci-Fi & Fantasy", "Fantasy", true)]
    [InlineData("War & Politics", "Politics", true)]
    [InlineData("Familie", "Family", true)]
    [InlineData("Anime", "Kids", false)]
    [InlineData("Action", "Adventure", false)]
    [InlineData("", "Drama", false)]
    public void Overlaps_PreservesDistinctMeanings(string original, string candidate, bool expected)
        => Assert.Equal(expected, GenreNormalizer.Overlaps(GenreNormalizer.Set([candidate]), [original]));

    [Fact]
    public void Preferences_MergeAliasesWithoutMutatingInput()
    {
        var original = new Dictionary<string, double> { ["Krimi"] = 2, ["Crime"] = 3, ["Action & Adventure"] = 4, ["  "] = 100 };
        var normalized = GenreNormalizer.Preferences(original);
        Assert.Equal(5, normalized["Crime"]);
        Assert.Equal(4, normalized["Action"]);
        Assert.Equal(4, normalized["Adventure"]);
        Assert.Equal(3, normalized.Count);
        Assert.Equal(4, original.Count);
        Assert.Same(normalized, GenreNormalizer.Preferences(normalized));
        Assert.Throws<ArgumentNullException>(() => GenreNormalizer.Preferences(null!));
    }

    [Fact]
    public void DisplayName_KeepsOriginalMetadataAndUnknownGenres()
    {
        Assert.Equal("Krimi", GenreNormalizer.DisplayName("Crime", ["Familie", "Krimi"]));
        Assert.Equal("Action & Abenteuer", GenreNormalizer.DisplayName("Adventure", ["Action & Abenteuer"]));
        Assert.Equal("Anime", GenreNormalizer.DisplayName("Anime", ["Animation"]));
        Assert.False(GenreNormalizer.Overlaps(GenreNormalizer.Set(["Crime"]), null));
        Assert.Throws<ArgumentNullException>(() => GenreNormalizer.Overlaps(null!, []));
    }

    [Fact]
    public void Name_HandlesDecomposedAndCompatibilityUnicode()
    {
        Assert.Equal("Comedy", GenreNormalizer.Name("Komo\u0308die"));
        Assert.Equal("Action", GenreNormalizer.Name("Ａｃｔｉｏｎ"));
    }

    [Fact]
    public void CanonicalSet_HandlesRawSetsWithoutMutatingAndReusesNormalizedSets()
    {
        var raw = new HashSet<string> { "Action & Abenteuer", "Krimi" };
        var normalized = GenreNormalizer.CanonicalSet(raw);
        Assert.Equal(3, normalized.Count);
        Assert.Equal(2, raw.Count);
        Assert.Same(normalized, GenreNormalizer.CanonicalSet(normalized));
        Assert.Throws<ArgumentNullException>(() => GenreNormalizer.CanonicalSet(null!));
        Assert.Empty(GenreNormalizer.CanonicalSet(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" }));
        Assert.Empty(GenreNormalizer.Preferences(new Dictionary<string, double> { [""] = 1 }));
    }

    [Fact]
    public void Name_MalformedUnicodeRemainsCustomMetadataInsteadOfCrashing()
        => Assert.Equal("\ud800", GenreNormalizer.Name("\ud800"));

    [Fact]
    public void ProviderCatalog_SupportsAdditionalLanguagesWithoutChangingExistingGenreMeanings()
    {
        var label = "localized adventure " + Guid.NewGuid().ToString("N");
        Assert.True(GenreNormalizer.RegisterProviderGenre(12, label));
        Assert.Equal("Adventure", GenreNormalizer.Name(label));
        Assert.False(GenreNormalizer.RegisterProviderGenre(28, label));
        Assert.False(GenreNormalizer.RegisterProviderGenre(28, "Adventure"));
        Assert.Equal("Adventure", GenreNormalizer.Name("Adventure"));
        Assert.False(GenreNormalizer.RegisterProviderGenre(999999, "Unknown provider identity"));
        Assert.False(GenreNormalizer.RegisterProviderGenre(12, null));
        Assert.False(GenreNormalizer.RegisterProviderGenre(12, new string('a', 201)));
    }

    [Fact]
    public void BestMatch_UsesOnlyRealPositiveCandidateMatchesAndKeepsDisplayLabels()
    {
        Assert.Equal("Krimi", GenreNormalizer.BestMatch(["Krimi", "Thriller"], new Dictionary<string, double> { ["Action"] = 100, ["Crime"] = 1, ["Thriller"] = 0.1 }));
        Assert.Equal("Action & Abenteuer", GenreNormalizer.BestMatch(["Action & Abenteuer"], new Dictionary<string, double> { ["Adventure"] = 1 }));
        Assert.Null(GenreNormalizer.BestMatch(["Crime"], new Dictionary<string, double> { ["Action"] = 1 }));
        Assert.Null(GenreNormalizer.BestMatch(["Crime"], new Dictionary<string, double> { ["Crime"] = 0 }));
        Assert.Null(GenreNormalizer.BestMatch([], new Dictionary<string, double>()));
    }

    [Fact]
    public void LanguageCatalog_NewTranslationsAndAliasesNeedNoCSharpLanguageList()
    {
        using var language = JsonDocument.Parse("""{"genre.12":"new localized adventure","genre.12.aliases":"second spelling | new localized adventure","genre.12.description":"not an alias","genre.bad":"ignored","genre.999999":"custom unknown","genre.35":null,"ordinaryUiKey":"text"}""");
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        GenreNormalizer.AddLanguageCatalog(aliases, language.RootElement);
        Assert.Equal("Adventure", aliases["new localized adventure"]);
        Assert.Equal("Adventure", aliases["second spelling"]);
        Assert.Equal(2, aliases.Count);
        Assert.Throws<ArgumentNullException>(() => GenreNormalizer.AddLanguageCatalog(null!, language.RootElement));
    }

    [Fact]
    public void LanguageCatalog_ConflictingIdentitiesAreNotSilentlyMerged()
    {
        using var language = JsonDocument.Parse("""{"genre.12":"same label","genre.28":"same label"}""");
        Assert.Throws<InvalidDataException>(() => GenreNormalizer.AddLanguageCatalog(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), language.RootElement));
    }

    [Fact]
    public void EveryEmbeddedLanguageGenreAndAlias_UsesItsDeclaredProviderIdentity()
    {
        var assembly = typeof(GenreNormalizer).Assembly;
        var count = 0;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.Contains(".i18n.", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var document = JsonDocument.Parse(stream);
            var entries = document.RootElement.EnumerateObject().Where(entry => entry.Name.StartsWith("genre.", StringComparison.Ordinal))
                .Select(entry => (Id: int.Parse(entry.Name.Split('.')[1], System.Globalization.CultureInfo.InvariantCulture), Labels: entry.Value.GetString()!.Split('|')));
            foreach (var entry in entries)
            {
                var expected = Assert.Single(Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery.TmdbGenreMap.ToJellyfinGenres([entry.Id]));
                foreach (var label in entry.Labels)
                {
                    Assert.Equal(expected, GenreNormalizer.Name(label));
                    count++;
                }
            }
        }

        Assert.True(count >= 8 * 27 * 2);
    }
}
