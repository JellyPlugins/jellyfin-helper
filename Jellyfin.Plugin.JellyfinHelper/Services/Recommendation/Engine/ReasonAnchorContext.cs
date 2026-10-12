namespace Jellyfin.Plugin.JellyfinHelper.Services.Recommendation.Engine;

/// <summary>
///     Candidate-invariant anchors used to turn an abstract signal into a concrete recommendation reason
///     (collection name, watched-sibling count, matched country/writer/tag/language, release era). All fields are
///     optional; an empty context degrades gracefully to the signal-only reasons.
/// </summary>
/// <param name="CollectionName">The candidate's TMDb collection (franchise) name, or null.</param>
/// <param name="WatchedSiblingCount">How many entries of the candidate's box-set collection the user has already watched.</param>
/// <param name="MatchedCountry">A preferred production country the candidate shares, or null.</param>
/// <param name="MatchedWriter">A preferred writer the candidate shares, or null.</param>
/// <param name="MatchedTag">A preferred inherited tag the candidate shares, or null.</param>
/// <param name="MatchedLanguage">A preferred audio language the candidate offers, or null.</param>
/// <param name="ReleaseEra">The candidate's release era label (e.g. "2010s"), or null.</param>
internal readonly record struct ReasonAnchorContext(
    string? CollectionName = null,
    int WatchedSiblingCount = 0,
    string? MatchedCountry = null,
    string? MatchedWriter = null,
    string? MatchedTag = null,
    string? MatchedLanguage = null,
    string? ReleaseEra = null);
