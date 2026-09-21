using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Common;

/// <summary>
///     Single home for media language identity: which language a track tag names,
///     regardless of whether the container wrote a code ("ger"), an English name
///     ("German"), an endonym ("deutsch", "svenska") or an exonym ("allemand").
///     Statistics facets and recommendation profiles both resolve through here so
///     one spelling variant can never split into two identities.
/// </summary>
internal static class LanguageIdentity
{
    // Shared word for Hindi across locales, kept in one place.
    private const string HindiExonym = "hindi";

    // Bounded fold cache, keyed by exact input. Declared before every table: all
    // builders fold through it during initialization. Concurrent scans share it safely.
    private static readonly ConcurrentDictionary<string, string> FoldCache = new(StringComparer.Ordinal);

    // Canonical codes with their aliases. Every row starts with the ISO 639-1 code
    // and continues with the three letter and retired variants from real containers.
    private static readonly string[][] _languageCodeAliases =
    [
        ["de", "deu", "ger", "de"],
        ["en", "eng", "en"],
        ["fr", "fra", "fre", "fr"],
        ["es", "spa", "es"],
        ["it", "ita", "it"],
        ["ja", "jpn", "ja"],
        ["ko", "kor", "ko"],
        ["ru", "rus", "ru"],
        ["zh", "zho", "chi", "zh"],
        ["pt", "por", "pt"],
        ["nl", "nld", "dut", "nl"],
        ["pl", "pol", "pl"],
        ["tr", "tur", "tr"],
        ["ar", "ara", "ar"],
        ["hi", "hin", "hi"],
        ["sv", "swe", "sv"],
        ["no", "nor", "nob", "nno", "no"],
        ["da", "dan", "da"],
        ["fi", "fin", "fi"],
        ["el", "ell", "gre", "el"],
        ["cs", "ces", "cze", "cs"],
        ["hu", "hun", "hu"],
        ["th", "tha", "th"],
        ["vi", "vie", "vi"],
        ["uk", "ukr", "uk"],
        ["he", "heb", "he"],
        ["ro", "ron", "rum", "ro"],
        ["id", "ind", "id"],
        ["ms", "msa", "may", "ms"],
        ["hr", "hrv", "hr"],
        ["sr", "srp", "sr"],
        ["sk", "slk", "slo", "sk"],
        ["sl", "slv", "sl"],
        ["bg", "bul", "bg"],
        ["ca", "cat", "ca"],
        ["et", "est", "et"],
        ["lv", "lav", "lv"],
        ["lt", "lit", "lt"],
        ["fa", "fas", "per", "fa"],
        ["ur", "urd", "ur"],
    ];

    // Stable English display per canonical code for the curated languages above.
    private static readonly string[][] _languageCodeDisplays =
    [
        ["de", "German"],
        ["en", "English"],
        ["fr", "French"],
        ["es", "Spanish"],
        ["it", "Italian"],
        ["ja", "Japanese"],
        ["ko", "Korean"],
        ["ru", "Russian"],
        ["zh", "Chinese"],
        ["pt", "Portuguese"],
        ["nl", "Dutch"],
        ["pl", "Polish"],
        ["tr", "Turkish"],
        ["ar", "Arabic"],
        ["hi", "Hindi"],
        ["sv", "Swedish"],
        ["no", "Norwegian"],
        ["da", "Danish"],
        ["fi", "Finnish"],
        ["el", "Greek"],
        ["cs", "Czech"],
        ["hu", "Hungarian"],
        ["th", "Thai"],
        ["vi", "Vietnamese"],
        ["uk", "Ukrainian"],
        ["he", "Hebrew"],
        ["ro", "Romanian"],
    ];

    // Exonyms per display name in the plugin UI locales (de, es, fr, pt, sv, tr, zh),
    // for track tags from tagging tools in those locales ("allemand", "tyska").
    // English names and endonyms resolve through culture data below; a null cell
    // means that locale needs no extra word for the language.
    internal static readonly string?[][] LanguageExonyms =
    [
        ["German", "deutsch", "alemán", "allemand", "alemão", "tyska", "Almanca", "德语"],
        ["English", "englisch", "inglés", "anglais", "inglês", "engelska", "İngilizce", "英语"],
        ["French", "französisch", "francés", null, "francês", "franska", "Fransızca", "法语"],
        ["Spanish", "spanisch", null, "espagnol", "espanhol", "spanska", "İspanyolca", "西班牙语"],
        ["Italian", "italienisch", null, "italien", null, "italienska", "İtalyanca", "意大利语"],
        ["Japanese", "japanisch", "japonés", "japonais", "japonês", "japanska", "Japonca", "日语"],
        ["Korean", "koreanisch", "coreano", "coréen", "coreano", "koreanska", "Korece", "韩语"],
        ["Russian", "russisch", "ruso", "russe", "russo", "ryska", "Rusça", "俄语"],
        ["Chinese", "chinesisch", "chino", "chinois", "chinês", "kinesiska", "Çince", null],
        ["Portuguese", "portugiesisch", "portugués", "portugais", null, "portugisiska", "Portekizce", "葡萄牙语"],
        ["Dutch", "niederländisch", "neerlandés", "néerlandais", "holandês", "nederländska", "Hollandaca", "荷兰语"],
        ["Polish", "polnisch", "polaco", "polonais", "polonês", "polska", "Lehçe", "波兰语"],
        ["Turkish", "türkisch", "turco", "turc", "turco", "turkiska", null, "土耳其语"],
        ["Arabic", "arabisch", "árabe", "arabe", "árabe", "arabiska", "Arapça", "阿拉伯语"],
        ["Hindi", HindiExonym, HindiExonym, HindiExonym, HindiExonym, HindiExonym, "Hintçe", "印地语"],
        ["Swedish", "schwedisch", "sueco", "suédois", "sueco", null, "İsveççe", "瑞典语"],
        ["Norwegian", "norwegisch", "noruego", "norvégien", "norueguês", "norska", "Norveççe", "挪威语"],
        ["Danish", "dänisch", "danés", "danois", "dinamarquês", "danska", "Danca", "丹麦语"],
        ["Finnish", "finnisch", "finés", "finnois", "finlandês", "finska", "Fince", "芬兰语"],
        ["Greek", "griechisch", "griego", "grec", "grego", "grekiska", "Yunanca", "希腊语"],
        ["Czech", "tschechisch", "checo", "tchèque", "tcheco", "tjeckiska", "Çekçe", "捷克语"],
        ["Hungarian", "ungarisch", "húngaro", "hongrois", "húngaro", "ungerska", "Macarca", "匈牙利语"],
        ["Thai", "thailändisch", "tailandés", "thaï", "tailandês", "thailändska", "Tayca", "泰语"],
        ["Vietnamese", "vietnamesisch", "vietnamita", "vietnamien", "vietnamita", "vietnamesiska", "Vietnamca", "越南语"],
        ["Ukrainian", "ukrainisch", "ucraniano", "ukrainien", "ucraniano", "ukrainska", "Ukraynaca", "乌克兰语"],
        ["Hebrew", "hebräisch", "hebreo", "hébreu", "hebraico", "hebreiska", "İbranice", "希伯来语"],
        ["Romanian", "rumänisch", "rumano", "roumain", "romeno", "rumänska", "Rumence", "罗马尼亚语"],
    ];

    // Alias to canonical code, built from the tables above plus neutral cultures.
    private static readonly Dictionary<string, string> _codeAliases = BuildCodeAliases();

    // Canonical code to stable English display, curated first, cultures after.
    private static readonly Dictionary<string, string> _codeDisplays = BuildCodeDisplays();

    // Flag words per locale for flag-only detection, stored folded like the lookups.
    // Resolution always runs first, so no entry here can ever shadow a real language.
    // Exposed for tests so every word stays pinned. Frozen: safe for concurrent scans.
    internal static readonly FrozenSet<string> TrackFlagWords = BuildTrackFlagWords();

    /// <summary>
    ///     Cleans a raw track tag for lookup: trims, cuts parenthetical qualifiers
    ///     ("German (Forced)") and compact region subtags ("en-US"). Spaced compounds
    ///     keep their dashes ("United states - English"), the segment engine splits
    ///     those. Returns null when empty.
    /// </summary>
    /// <param name="tag">The raw language tag, or <c>null</c>.</param>
    /// <returns>The cleaned basis, or <c>null</c> when nothing remains.</returns>
    internal static string? CleanLanguageTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var trimmed = tag.Trim();
        var qualifier = trimmed.IndexOfAny(['(', '[']);
        var basis = (qualifier >= 0 ? trimmed.Substring(0, qualifier) : trimmed).Trim();
        if (basis.IndexOfAny([' ', '\t']) < 0)
        {
            var separator = basis.IndexOfAny(['-', '_']);
            basis = separator > 0 ? basis.Substring(0, separator) : basis;
        }

        // Leading separators are never meaningful ("-en" still names English).
        basis = basis.Trim('-', '_', ' ', '\t');
        return basis.Length == 0 ? null : basis;
    }

    /// <summary>
    ///     Resolves a raw track tag to its canonical ISO 639-1 code, or null when the
    ///     tag names no language. Compound tags are tried as a whole, then per segment
    ///     ("Simplified, Mandarin Chinese" to Chinese). Within a segment the first token
    ///     wins; the last token only counts when structure was stripped or split and it
    ///     is longer than two chars, so "Forced by" stays silent instead of turning
    ///     Belarusian, while "Modern Greek (1453-)" still reaches Greek.
    /// </summary>
    /// <param name="tag">The raw language tag, or <c>null</c>.</param>
    /// <returns>The 2-letter code, or <c>null</c>.</returns>
    internal static string? GetIso6391Code(string? tag)
    {
        var cleaned = CleanLanguageTag(tag);
        var trimmed = tag?.Trim();
        if (cleaned == null || trimmed == null)
        {
            return null;
        }

        var derived = !string.Equals(cleaned, trimmed, StringComparison.Ordinal);
        if (!IsUndetermined(cleaned) && _codeAliases.TryGetValue(FoldDiacritics(cleaned.ToLowerInvariant()), out var code))
        {
            return code;
        }

        // Leading flag words ("Forced German", "HI English") describe the track, not
        // the language: strip them, then resolve the remainder. Bare flags resolve
        // nothing by themselves, and resolution-first keeps real codes like "hi" intact.
        if (StripLeadingFlags(cleaned) is string unflagged
            && ResolveTagPart(unflagged, true) is string unflaggedCode)
        {
            return unflaggedCode;
        }

        foreach (var part in SplitTagSegments(cleaned))
        {
            if (IsUndetermined(part))
            {
                continue;
            }

            if (ResolveTagPart(part, derived || !part.Equals(cleaned, StringComparison.Ordinal)) is string partCode)
            {
                return partCode;
            }
        }

        return null;
    }

    // Resolves one segment: whole value, then first token, then (when allowed) the
    // last token. Undetermined markers contribute nothing at every level.
    private static string? ResolveTagPart(string part, bool allowLastToken)
    {
        if (_codeAliases.TryGetValue(FoldDiacritics(part.ToLowerInvariant()), out var partCode))
        {
            return partCode;
        }

        var first = FirstToken(part);
        if (!first.Equals(part, StringComparison.Ordinal)
            && !IsUndetermined(first)
            && _codeAliases.TryGetValue(FoldDiacritics(first.ToLowerInvariant()), out var firstCode))
        {
            return firstCode;
        }

        if (allowLastToken
            && LastToken(part) is string last
            && !IsUndetermined(last)
            && _codeAliases.TryGetValue(FoldDiacritics(last.ToLowerInvariant()), out var lastCode))
        {
            return lastCode;
        }

        return null;
    }

    // Undetermined content markers carry no language and contribute nothing, so
    // compounds like "zxx - commentary" resolve through their real parts instead
    // of leaking an uppercase facet.
    private static bool IsUndetermined(string value) =>
        value.Equals("und", StringComparison.OrdinalIgnoreCase)
        || value.Equals("mis", StringComparison.OrdinalIgnoreCase)
        || value.Equals("mul", StringComparison.OrdinalIgnoreCase)
        || value.Equals("zxx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Reports whether a cleaned tag is ignorable: every token is undetermined
    ///     content or a track flag word. Such tags yield no facet instead of noise.
    /// </summary>
    private static bool IsFlagWord(string token)
    {
        var folded = FoldDiacritics(token.ToLowerInvariant());
        return TrackFlagWords.Contains(folded);
    }

    /// <summary>
    ///     Reports whether a cleaned tag carries only track flags ("Full SDH") instead of
    ///     a language. Flag words come from the shared subtitle flag set plus German
    ///     equivalents. Resolution runs first: "hi" stays Hindi, only unresolvable
    ///     flag noise lands here.
    /// </summary>
    /// <param name="basis">The cleaned tag basis.</param>
    /// <returns>True when every token is a known flag word.</returns>
    internal static bool IsFlagOnlyTag(string basis)
    {
        var tokens = basis.Split([' ', '\t', '/', '|', ',', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length != 0 && tokens.All(static token => IsFlagWord(token));
    }

    /// <summary>
    ///     Reports whether a cleaned tag is ignorable: every token is undetermined
    ///     content or a track flag word. Such tags yield no facet instead of noise.
    /// </summary>
    /// <param name="basis">The cleaned tag basis.</param>
    /// <returns>True when the tag carries no language.</returns>
    internal static bool IsIgnorableTag(string basis)
    {
        var tokens = basis.Split([' ', '\t', '/', '|', ',', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 || tokens.All(static token => IsUndetermined(token) || IsFlagWord(token));
    }

    private static string? StripLeadingFlags(string basis)
    {
        var rest = basis;
        while (true)
        {
            var end = rest.IndexOfAny([' ', '\t']);
            if (end <= 0)
            {
                return rest.Equals(basis, StringComparison.Ordinal) ? null : rest;
            }

            if (!IsFlagWord(rest.Substring(0, end)))
            {
                return rest.Equals(basis, StringComparison.Ordinal) ? null : rest;
            }

            rest = rest.Substring(end + 1).TrimStart(' ', '\t');
            if (rest.Length == 0)
            {
                return null;
            }
        }
    }

    private static IEnumerable<string> SplitTagSegments(string basis)
    {
        // Long dashes first so splits never leave stray hyphens behind.
        foreach (var chunk in basis.Split([" - ", " – ", " — "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Bare dashes split too ("English–German"); compact region tags never reach
            // this point because cleaning already stripped them.
            foreach (var dash in chunk.Split(['-', '–', '—'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var part in dash.Split([',', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return part;
                }
            }
        }
    }

    private static string FirstToken(string part)
    {
        var end = part.IndexOfAny([' ', '\t']);
        return end > 0 ? part.Substring(0, end) : part;
    }

    private static string? LastToken(string part)
    {
        var start = part.LastIndexOfAny([' ', '\t']);
        if (start < 0 || start >= part.Length - 1)
        {
            return null;
        }

        var last = part.Substring(start + 1);
        return last.Length > 2 ? last : null;
    }

    /// <summary>
    ///     Returns the stable English display name for a canonical code.
    /// </summary>
    /// <param name="code">The canonical 2-letter code.</param>
    /// <returns>The display name, or the uppercased code when unknown.</returns>
    internal static string DisplayNameForCode(string code)
    {
        if (_codeDisplays.TryGetValue(code, out var display))
        {
            return display;
        }

        return code.ToUpperInvariant();
    }

    private static Dictionary<string, string> BuildCodeAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _languageCodeAliases)
        {
            foreach (var alias in row.Skip(1))
            {
                aliases.TryAdd(alias, row[0]);
            }
        }

        foreach (var culture in NeutralLanguageCultures())
        {
            aliases.TryAdd(culture.TwoLetterISOLanguageName, culture.TwoLetterISOLanguageName);
            aliases.TryAdd(culture.ThreeLetterISOLanguageName, culture.TwoLetterISOLanguageName);
            aliases.TryAdd(FoldDiacritics(culture.EnglishName.ToLowerInvariant()), culture.TwoLetterISOLanguageName);
            aliases.TryAdd(FoldDiacritics(culture.NativeName.ToLowerInvariant()), culture.TwoLetterISOLanguageName);
        }

        // Curated exceptions no culture carries: colloquial short forms and retired
        // codes still seen in old containers. Retired "in"/"ji"/"mo" stay out on
        // purpose: as first tokens they collide with ordinary English words, and
        // their modern codes ("id"/"yi"/"ro") cover the languages. Visible junk
        // beats a silently wrong language.
        aliases[FoldDiacritics("holländisch")] = "nl";
        aliases["norsk"] = "no";
        aliases["vietnam"] = "vi";
        aliases["nb"] = "no";
        aliases["nn"] = "no";
        aliases["iw"] = "he";
        // Cantonese and Mandarin have no ISO 639-1 code; some ICU builds expose a
        // neutral yue culture while others do not. Both collapse into Chinese so the
        // facet is identical on every host.
        aliases["yue"] = "zh";
        aliases["cmn"] = "zh";
        foreach (var row in LanguageExonyms)
        {
            if (row == null)
            {
                continue;
            }

            var display = row[0];
            if (string.IsNullOrEmpty(display) || !TryGetCodeForDisplay(display, out var code))
            {
                continue;
            }

            foreach (var exonym in row.Skip(1))
            {
                if (!string.IsNullOrEmpty(exonym))
                {
                    aliases[FoldDiacritics(exonym.ToLowerInvariant())] = code;
                }
            }
        }

        return aliases;
    }

    private static bool TryGetCodeForDisplay(string display, out string code)
    {
        foreach (var row in _languageCodeDisplays)
        {
            if (string.Equals(row[1], display, StringComparison.Ordinal))
            {
                code = row[0];
                return true;
            }
        }

        code = string.Empty;
        return false;
    }

    private static Dictionary<string, string> BuildCodeDisplays()
    {
        var displays = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _languageCodeDisplays)
        {
            displays.TryAdd(row[0], row[1]);
        }

        foreach (var culture in NeutralLanguageCultures())
        {
            displays.TryAdd(culture.TwoLetterISOLanguageName, CultureDisplayName(culture));
        }

        return displays;
    }

    // Neutral cultures, shortest name first so "sr" wins over "sr-Latn" when both
    // describe one language. Region free by construction; built once per process.
    // A failing enumeration degrades to the curated tables instead of killing the
    // static initializer and every caller with it.
    private static List<CultureInfo> NeutralLanguageCultures()
    {
        try
        {
            return CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .Where(static c => HasLanguageIdentity(c))
                .OrderBy(static c => c.Name.Length)
                .ThenBy(static c => c.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return new List<CultureInfo>();
        }
    }

    private static bool HasLanguageIdentity(CultureInfo culture)
    {
        try
        {
            return culture.Name.Length > 0
                && !string.IsNullOrEmpty(culture.EnglishName)
                && !string.IsNullOrEmpty(culture.NativeName)
                && !string.IsNullOrEmpty(culture.TwoLetterISOLanguageName)
                && !string.IsNullOrEmpty(culture.ThreeLetterISOLanguageName);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    // Display name of a culture without script or region qualifiers, so "nb" and
    // "nn" collapse into Norwegian instead of opening stray facets.
    private static string CultureDisplayName(CultureInfo culture)
    {
        var name = culture.EnglishName;
        var cut = name.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? name.Substring(0, cut) : name;
    }

    private static FrozenSet<string> BuildTrackFlagWords()
    {
        var words = new HashSet<string>(MediaExtensions.SubtitleFlags, StringComparer.OrdinalIgnoreCase);
        foreach (var word in new[]
        {
            "hearing", "impaired", "closed", "captions", "caption",
            "subtitle", "subtitles", "sub", "subs", "text",
            "erzwungen", "hörgeschädigt", "untertitel", "kommentar", "kommentare",
            "forcé", "malentendant", "sourd", "sourds", "sous", "titres", "commentaire", "commentaires",
            "forzado", "forzada", "sordos", "sordomudo", "subtitulos", "comentarios",
            "forçado", "surdos", "legendas", "comentarios",
            "framtvingad", "framtvingade", "hörselskadade", "undertexter", "kommentarer",
            "zorunlu", "işitme", "altyazı", "yorum",
            "强制", "听障", "字幕", "评论",
        })
        {
            words.Add(FoldDiacritics(word.ToLowerInvariant()));
        }

        return words.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Folds diacritics for language name matching, so "français" and "francais"
    ///     resolve alike. Characters without decomposition stay as is.
    /// </summary>
    /// <param name="value">The lowercase lookup input.</param>
    /// <returns>The input without combining marks.</returns>
    private static string FoldDiacritics(string value)
    {
        // Tags repeat heavily per scan (same languages on every file); the small
        // bounded cache avoids re-normalizing them. Only short tags are cached so
        // pathological probe strings cannot grow it without bound.
        if (value.Length <= 32 && FoldCache.TryGetValue(value, out var cached))
        {
            return cached;
        }

        var folded = new string(value.Normalize(NormalizationForm.FormD)
            .Where(static c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        if (value.Length <= 32)
        {
            FoldCache.TryAdd(value, folded);
        }

        return folded;
    }
}
