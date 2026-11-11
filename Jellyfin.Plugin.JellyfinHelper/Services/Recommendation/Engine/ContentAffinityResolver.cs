using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyfinHelper.Services.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Shared, library-call-free resolvers for the candidate-invariant content-affinity source data (TMDb collection, production countries, inherited tags, series lifecycle, writers).
/// </summary>
internal static class ContentAffinityResolver
{
    /// <summary>Creates one language-independent collection key, retaining names only for legacy metadata.</summary>
    /// <param name="id">The corroborated provider collection identity, or zero when unknown.</param>
    /// <param name="name">An optional legacy display label.</param>
    /// <returns>A stable key, a legacy name, or null when no evidence exists.</returns>
    internal static string? CollectionKey(int id, string? name)
        => id > 0 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tmdb:collection:{id}") : name;

    /// <summary>Detects self-valued collection metadata that needs evidence; provider ID spaces may legitimately overlap.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>Whether a nameless collection ID is indistinguishable from a copied title ID.</returns>
    internal static bool NeedsCollectionCorroboration(BaseItem item)
        => item is Movie movie && string.IsNullOrWhiteSpace(movie.TmdbCollectionName)
            && int.TryParse(item.ProviderIds.GetValueOrDefault("TmdbCollection"), out var collection) && collection > 0
            && int.TryParse(item.ProviderIds.GetValueOrDefault("Tmdb"), out var title) && collection == title;

    /// <summary>Resolves a collection ID without turning suspicious migration metadata into a verified franchise.</summary>
    /// <param name="item">The library movie; BoxSet libraries are optional.</param>
    /// <param name="collections">Optional corroboration for ambiguous self-valued IDs.</param>
    /// <param name="memberships">An optional pre-resolved authoritative item-to-BoxSet lookup.</param>
    /// <returns>The typed collection ID, or zero while evidence is insufficient.</returns>
    internal static int ResolveTmdbCollectionId(BaseItem item, IEnumerable<BoxSet>? collections = null, IReadOnlyDictionary<Guid, List<Guid>>? memberships = null)
    {
        if (item is not Movie || !int.TryParse(item.ProviderIds.GetValueOrDefault("TmdbCollection"), out var id) || id <= 0)
        {
            return 0;
        }

        if (!NeedsCollectionCorroboration(item))
        {
            return id;
        }

        var supported = collections?.Any(boxset => int.TryParse(boxset.ProviderIds.GetValueOrDefault("Tmdb"), out var boxsetId) && boxsetId == id
            && (memberships is not null
                ? memberships.TryGetValue(item.Id, out var parents) && parents.Contains(boxset.Id)
                : boxset.LinkedChildren.Any(child => child.Type == LinkedChildType.Manual && child.ItemId == item.Id))) == true;
        return supported ? id : 0;
    }

    /// <summary>
    ///     Resolves the TMDb collection (franchise) name of a movie, if any. Non-movies and items
    ///     without a collection return null.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The TMDb collection name, or null.</returns>
    internal static string? ResolveTmdbCollectionName(BaseItem item)
    {
        try
        {
            if (item is Movie movie)
            {
                var name = movie.TmdbCollectionName;
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }

            return null;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return null;
        }
    }

    /// <summary>
    ///     Resolves the production countries/locations of an item. Returns an empty list when unavailable.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The production locations, or an empty list.</returns>
    internal static List<string> ResolveProductionCountries(BaseItem item)
    {
        try
        {
            var locations = item.ProductionLocations;
            return locations is { Length: > 0 } ? [.. locations] : [];
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return [];
        }
    }

    /// <summary>
    ///     Resolves the inherited tags of an item (own tags unioned with parent/collection/library-folder
    ///     tags). Returns an empty list when unavailable.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The inherited tags, or an empty list.</returns>
    internal static List<string> ResolveInheritedTags(BaseItem item)
    {
        try
        {
            var tags = item.GetInheritedTags();
            return tags is { Count: > 0 } ? [.. tags] : [];
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return [];
        }
    }

    /// <summary>
    ///     Resolves the series lifecycle status string (e.g. "Continuing", "Ended", "Unreleased").
    ///     Non-series or items without a status return null.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The series status name, or null.</returns>
    internal static string? ResolveSeriesStatus(BaseItem item)
    {
        try
        {
            if (item is Series series && series.Status.HasValue)
            {
                return series.Status.Value.ToString();
            }

            return null;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return null;
        }
    }

    /// <summary>
    ///     Resolves the series end date, if any. Non-series or ongoing series return null.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The end date, or null.</returns>
    internal static DateTime? ResolveSeriesEndDate(BaseItem item)
    {
        try
        {
            return item is Series series ? series.EndDate : null;
        }
        catch (Exception ex) when (!ex.IsFatal())
        {
            return null;
        }
    }

    /// <summary>
    ///     Extracts distinct writer (screenplay/creator) names from an already-fetched people list (no library call).
    /// </summary>
    /// <param name="people">The item's people, or null.</param>
    /// <returns>Distinct writer names (case-insensitive), or an empty list.</returns>
    internal static List<string> ExtractWriterNames(IReadOnlyList<PersonInfo>? people)
    {
        if (people is null || people.Count == 0)
        {
            return [];
        }

        return people
            .Where(person => person.Type == PersonKind.Writer && !string.IsNullOrWhiteSpace(person.Name))
            .Select(person => person.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
