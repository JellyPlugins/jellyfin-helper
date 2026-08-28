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

        var writers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in people)
        {
            if (person.Type == PersonKind.Writer
                && !string.IsNullOrWhiteSpace(person.Name)
                && seen.Add(person.Name))
            {
                writers.Add(person.Name);
            }
        }

        return writers;
    }
}
