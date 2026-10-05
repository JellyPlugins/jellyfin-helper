using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyfinHelper.Services.Seerr.Discovery;

/// <summary>
///     A candidate supplied by an external discovery source (e.g. Trakt) to be scored through the shared
///     per-user ensemble pipeline. This is the public projection of the internal TMDb candidate shape so an
///     outside source can feed the scorer without taking a dependency on internal types.
/// </summary>
public sealed class ExternalDiscoveryCandidate
{
    /// <summary>Gets or sets the TMDb id. Required; candidates without one are dropped by the source.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the media type ("movie" or "tv").</summary>
    public string MediaType { get; set; } = "movie";

    /// <summary>Gets or sets the display title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the release/first-aired year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the overview/synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the community rating (0-10).</summary>
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the popularity signal.</summary>
    public double Popularity { get; set; }

    /// <summary>Gets or sets the TMDb genre ids (used by the parental filter and genre features).</summary>
    public IReadOnlyList<int> GenreIds { get; set; } = [];

    /// <summary>Gets or sets the poster path (relative to the TMDb CDN).</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is adult-flagged.</summary>
    public bool Adult { get; set; }
}
