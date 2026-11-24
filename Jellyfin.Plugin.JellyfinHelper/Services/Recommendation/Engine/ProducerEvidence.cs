using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.WatchHistory;
using Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>Shared train/serve producer identity. Untyped native studio/network labels never establish production affinity.</summary>
internal static class ProducerEvidence
{
    internal const string CompanyKind = "production-company";
    internal const string NetworkKind = "network";

    /// <summary>Captures a stated provider list; omitted or malformed lists remain unknown, while an explicit empty stays empty.</summary>
    /// <param name="values">The explicitly supplied entity list.</param>
    /// <param name="kind">The known API field's entity kind.</param>
    /// <returns>Immutable qualified entities or unknown.</returns>
    internal static ImmutableArray<ProviderStudioReference>? Capture(IReadOnlyList<SeerrMediaDetailResponse.GenreRef>? values, string kind)
    {
        if (values is null || values.Any(value => value is null || value.Id <= 0))
        {
            return null;
        }

        return values.Select(value => new ProviderStudioReference("tmdb", kind, value.Id, value.Name)).Distinct().ToImmutableArray();
    }

    /// <summary>Returns current qualified company keys, null for unavailable evidence and an empty list for observed absence.</summary>
    /// <param name="metadata">The captured producer observation.</param>
    /// <param name="atUtc">Optional historical prediction boundary.</param>
    /// <returns>Qualified production-company identities or unknown.</returns>
    internal static IReadOnlyList<string>? Keys(TitleMetadataSnapshot? metadata, DateTime? atUtc = null)
    {
        if (metadata?.ProductionCompanies is not { IsDefault: false } companies)
        {
            return null;
        }

        var at = atUtc ?? DateTime.UtcNow;
        var acquired = metadata.FieldFetchedAtUtc?.GetValueOrDefault(nameof(TitleMetadataSnapshot.ProductionCompanies), metadata.FetchedAtUtc) ?? metadata.FetchedAtUtc;
        if (acquired > at || acquired <= at.AddDays(-30) || companies.Any(value => !IsCompany(value)))
        {
            return null;
        }

        return companies.Select(Key).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Uses the exact captured title/parent identity; a known current unknown never falls back to stale row metadata.</summary>
    /// <param name="row">One actual watched item or parent-series row.</param>
    /// <param name="captured">Current identity-keyed metadata, including explicit unknown.</param>
    /// <param name="atUtc">Optional historical acquisition boundary.</param>
    /// <param name="completeCurrentScope">True when missing IDs are outside the current physical/title intersection.</param>
    /// <returns>Known title-level producer identities or unknown.</returns>
    internal static IReadOnlyList<string>? ForWatch(WatchedItemInfo row, IReadOnlyDictionary<Guid, TitleMetadataSnapshot?>? captured = null, DateTime? atUtc = null, bool completeCurrentScope = false)
    {
        var title = row.SeriesId.GetValueOrDefault() is var parent && parent != Guid.Empty ? parent : row.ItemId;
        TitleMetadataSnapshot? metadata;
        if (captured is not null && captured.TryGetValue(title, out var current))
        {
            metadata = current;
        }
        else
        {
            metadata = completeCurrentScope ? null : row.TitleMetadata;
        }

        var tmdb = row.SeriesId.GetValueOrDefault() != Guid.Empty ? row.SeriesTmdbId : row.TmdbId;
        var type = row.SeriesId.GetValueOrDefault() != Guid.Empty || row.ItemType == "Series" ? "tv" : "movie";
        if (metadata is not null && (metadata.MediaType != type || (tmdb > 0 && metadata.TmdbId > 0 && metadata.TmdbId != tmdb)))
        {
            return null;
        }

        return Keys(metadata, atUtc);
    }

    /// <summary>Builds positive, title-qualified producer preferences without reading legacy studio name arrays.</summary>
    /// <param name="profile">The current or self-excluded historical user.</param>
    /// <param name="captured">Current identity-keyed producer observations.</param>
    /// <param name="atUtc">Optional historical acquisition boundary.</param>
    /// <param name="completeCurrentScope">True when the captured map defines the allowed physical/title population.</param>
    /// <returns>Qualified producer preferences.</returns>
    internal static HashSet<string> Preferences(UserWatchProfile profile, IReadOnlyDictionary<Guid, TitleMetadataSnapshot?>? captured = null, DateTime? atUtc = null, bool completeCurrentScope = false)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in profile.WatchedItems.Where(PreferenceBuilder.IsEligibleForPreferenceWeighting))
        {
            keys.UnionWith(ForWatch(row, captured, atUtc, completeCurrentScope) ?? []);
        }

        return keys;
    }

    /// <summary>Resolves a localized producer reason only after matching its qualified identity.</summary>
    /// <param name="metadata">Captured candidate production metadata.</param>
    /// <param name="preferred">The user's qualified producer identities.</param>
    /// <param name="atUtc">Optional historical acquisition boundary.</param>
    /// <returns>A supplied matching producer label or null.</returns>
    internal static string? MatchedName(TitleMetadataSnapshot? metadata, IReadOnlySet<string>? preferred, DateTime? atUtc = null)
    {
        var current = Keys(metadata, atUtc);
        if (current is null || preferred is null || metadata?.ProductionCompanies is not { } companies)
        {
            return null;
        }

        return companies.Where(IsCompany).FirstOrDefault(value => preferred.Contains(Key(value)) && !string.IsNullOrWhiteSpace(value.Name))?.Name;
    }

    private static bool IsCompany(ProviderStudioReference? value)
        => value is not null && value.Id > 0 && !string.IsNullOrWhiteSpace(value.Provider)
            && value.Provider.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') && value.Kind == CompanyKind;

    private static string Key(ProviderStudioReference value)
        => string.Create(CultureInfo.InvariantCulture, $"producer:{value.Provider.ToLowerInvariant()}:{value.Kind}:{value.Id}");
}
