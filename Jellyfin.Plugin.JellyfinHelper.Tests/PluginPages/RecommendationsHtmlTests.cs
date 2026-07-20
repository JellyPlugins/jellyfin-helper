using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.JellyfinHelper.Tests.PluginPages;

/// <summary>
/// Deep tests for Recommendations.js behaviour (Discover tab logic).
/// Complements DiscoverHtmlTests.cs which covers surface-level structure;
/// this class covers caching, TTL, request-ID guards, error handling,
/// XSS-safety, and the quality-profile popup.
/// </summary>
public class RecommendationsHtmlTests : ConfigPageTestBase
{
    // === Cache TTL constants ===

    [Fact]
    public void Html_RecsCache_HasFiveMinuteTtl()
    {
        Assert.Contains("5 * 60 * 1000", HtmlContent);
    }

    [Fact]
    public void Html_DiscoveryCache_HasFiveMinuteTtl()
    {
        Assert.Contains("_discoveryCacheTtlMs = 5 * 60 * 1000", HtmlContent);
    }

    [Fact]
    public void Html_SeerrServicesCache_HasFiveMinuteTtl()
    {
        Assert.Contains("_seerrServicesCacheTtlMs = 5 * 60 * 1000", HtmlContent);
    }

    // === Cache invalidation & guards ===

    [Fact]
    public void Html_LoadDiscovery_InvalidatesPerUserCachesAfterGlobalExpiry()
    {
        // When the global discovery cache expires, ALL per-user caches must be cleared
        // otherwise a stale result would keep being rendered.
        Assert.Matches(
            new Regex(@"cacheAge\s*>=\s*_discoveryCacheTtlMs[\s\S]*?_cachedDiscovery\s*=\s*undefined"),
            HtmlContent);
    }

    [Fact]
    public void Html_LoadRecommendations_UsesRequestIdGuard()
    {
        // reqId prevents late responses from overwriting fresh ones after a tab switch.
        Assert.Matches(
            new Regex(@"function\s+loadRecommendations[\s\S]*?reqId\s*!==\s*_recsListReqId"),
            HtmlContent);
    }

    [Fact]
    public void Html_LoadUserWatchProfile_UsesRequestIdGuard()
    {
        Assert.Matches(
            new Regex(@"function\s+loadUserWatchProfile[\s\S]*?reqId\s*!==\s*_profileReqId"),
            HtmlContent);
    }

    [Fact]
    public void Html_LoadUserActivity_UsesRequestIdGuard()
    {
        Assert.Matches(
            new Regex(@"function\s+loadUserActivity[\s\S]*?reqId\s*!==\s*_activityReqId"),
            HtmlContent);
    }

    [Fact]
    public void Html_LoadDiscoveryForUser_UsesRequestIdGuard()
    {
        Assert.Matches(
            new Regex(@"function\s+loadDiscoveryForUser[\s\S]*?reqId\s*!==\s*_discoveryReqId"),
            HtmlContent);
    }

    [Fact]
    public void Html_ProfileFetchFailure_DoesNotCacheError()
    {
        // Only assign result._cachedProfile inside the success callback so subsequent
        // user-switches retry rather than showing "no profile" forever.
        Assert.Matches(
            new Regex(@"function\s+loadUserWatchProfile[\s\S]*?apiGet\([^,]+,\s*function\s*\([^)]+\)\s*\{[\s\S]*?result\._cachedProfile\s*=\s*profile"),
            HtmlContent);
    }

    [Fact]
    public void Html_ActivityFetchFailure_DoesNotCacheError()
    {
        Assert.Matches(
            new Regex(@"function\s+loadUserActivity[\s\S]*?apiGet\([^,]+,\s*function\s*\([^)]+\)\s*\{[\s\S]*?result\._cachedActivity\s*=\s*items"),
            HtmlContent);
    }

    [Fact]
    public void Html_ShowSeerrUserPopup_DoesNotCacheFailures()
    {
        // Failed profile lookups must NOT be cached (would fail-open on wrong server otherwise)
        Assert.Matches(
            new Regex(@"function\s+showSeerrUserPopup[\s\S]*?delete\s+window\[cacheKey\]"),
            HtmlContent);
    }

    // === XSS safety in the recommendations grid ===

    [Fact]
    public void Html_RenderRecommendationCard_EscapesUserInputName()
    {
        // rec.Name is user data (media title from TMDb) - must be escaped
        Assert.Matches(
            new Regex(@"function\s+renderRecommendationCard[\s\S]*?escHtml\(rec\.Name"),
            HtmlContent);
    }

    [Fact]
    public void Html_RenderRecommendationCard_EscapesGenres()
    {
        Assert.Matches(
            new Regex(@"function\s+renderRecommendationCard[\s\S]*?escHtml\(rec\.Genres"),
            HtmlContent);
    }

    [Fact]
    public void Html_RenderRecommendationCard_EscapesReasonText()
    {
        // reasonText may include externally supplied related-item names
        Assert.Matches(
            new Regex(@"function\s+renderRecommendationCard[\s\S]*?escHtml\(reasonText"),
            HtmlContent);
    }

    [Fact]
    public void Html_ReasonPlaceholder_UsesSinglePassRegex()
    {
        // Uses replace with function form to prevent cascading replacements
        // (see comment in Recommendations.js).
        Assert.Matches(
            new Regex(@"function\s+renderRecommendationCard[\s\S]*?reasonText\.replace\s*\(\s*/\\\{\(\\d\+\)\\\}/g\s*,\s*function"),
            HtmlContent);
    }

    [Fact]
    public void Html_RenderDiscoveryCard_EscapesTmdbFields()
    {
        // TMDb data is external - all string fields must be escaped
        Assert.Matches(
            new Regex(@"function\s+renderDiscoveryCard[\s\S]*?escHtml\(rec\.Title"),
            HtmlContent);
        Assert.Matches(
            new Regex(@"function\s+renderDiscoveryCard[\s\S]*?escHtml\(rec\.Genres"),
            HtmlContent);
    }

    [Fact]
    public void Html_RenderDiscoveryCard_EscapesPosterPath()
    {
        // rec.PosterPath is concatenated into an <img src>. escHtml prevents URL injection.
        Assert.Matches(
            new Regex(@"function\s+renderDiscoveryCard[\s\S]*?escHtml\(rec\.PosterPath"),
            HtmlContent);
    }

    [Fact]
    public void Html_DiscoveryReasonPlaceholder_UsesFunctionFormForRelatedInfo()
    {
        // Prevents $& / $' / $` from being interpreted as replacement directives.
        Assert.Matches(
            new Regex(@"function\s+renderDiscoveryCard[\s\S]*?replace\s*\(\s*/\\\{0\\\}/g\s*,\s*function\s*\(\s*\)\s*\{[\s\S]*?rec\.RelatedInfo"),
            HtmlContent);
    }

    // === Sorting / scoring ===

    [Fact]
    public void Html_RenderUserRecommendations_SortsByScoreDescending()
    {
        // Backend uses MMR which interleaves genres, but UI must sort by score for intuitive display
        Assert.Matches(
            new Regex(@"function\s+renderUserRecommendations[\s\S]*?slice\(\)\.sort[\s\S]*?b\.Score[\s\S]*?a\.Score"),
            HtmlContent);
    }

    [Fact]
    public void Html_Score_ClampedBetween0And100()
    {
        // Renders as percent, must be clamped for the width style
        Assert.Matches(new Regex(@"Math\.max\(0,\s*Math\.min\(100,\s*Math\.round"), HtmlContent);
    }

    // === LocalStorage for selected user ===

    [Fact]
    public void Html_UserSelection_PersistedToLocalStorage()
    {
        Assert.Contains("jh_recsSelectedUser", HtmlContent);
        Assert.Matches(new Regex(@"localStorage\.setItem\(\s*['""]jh_recsSelectedUser['""]"), HtmlContent);
    }

    [Fact]
    public void Html_UserSelection_RestoredFromLocalStorage()
    {
        Assert.Matches(new Regex(@"localStorage\.getItem\(\s*['""]jh_recsSelectedUser['""]"), HtmlContent);
    }

    [Fact]
    public void Html_UserSelection_GuardsAgainstStorageUnavailable()
    {
        // localStorage may be unavailable in incognito / disabled cookies
        Assert.Matches(
            new Regex(@"try\s*\{[\s\S]*?localStorage\.getItem[\s\S]*?\}\s*catch"),
            HtmlContent);
    }

    // === Discovery cache lookup semantics ===

    [Fact]
    public void Html_FindUserDiscovery_MatchesUserIdCaseInsensitively()
    {
        Assert.Matches(
            new Regex(@"function\s+findUserDiscovery[\s\S]*?toLowerCase\(\)"),
            HtmlContent);
    }

    [Fact]
    public void Html_FindUserDiscovery_AcceptsPascalAndCamelCaseUserId()
    {
        // Guards against the API layer returning UserId or userId
        Assert.Matches(
            new Regex(@"function\s+findUserDiscovery[\s\S]*?UserId\s*\|\|[\s\S]*?userId"),
            HtmlContent);
    }

    // === Quality-profile popup accessibility ===

    [Fact]
    public void Html_QualityProfilePopup_HasDialogRole()
    {
        Assert.Matches(
            new Regex(@"function\s+renderQualityProfilePopup[\s\S]*?setAttribute\(\s*['""]role['""]\s*,\s*['""]dialog['""]"),
            HtmlContent);
    }

    [Fact]
    public void Html_QualityProfilePopup_HasAriaModal()
    {
        Assert.Matches(
            new Regex(@"function\s+renderQualityProfilePopup[\s\S]*?setAttribute\(\s*['""]aria-modal['""]\s*,\s*['""]true['""]"),
            HtmlContent);
    }

    [Fact]
    public void Html_QualityProfilePopup_HasLabelledByAndDescribedBy()
    {
        Assert.Matches(
            new Regex(@"aria-labelledby['""]\s*,\s*['""]seerrPopupTitle"),
            HtmlContent);
        Assert.Matches(
            new Regex(@"aria-describedby['""]\s*,\s*['""]seerrPopupSubtitle"),
            HtmlContent);
    }

    [Fact]
    public void Html_QualityProfilePopup_FocusesFirstItem()
    {
        Assert.Matches(
            new Regex(@"function\s+renderQualityProfilePopup[\s\S]*?firstItem\.focus\(\)"),
            HtmlContent);
    }

    [Fact]
    public void Html_QualityProfilePopup_ClosesOnEscape()
    {
        Assert.Matches(
            new Regex(@"function\s+onEscape[\s\S]*?['""]Escape['""][\s\S]*?closePopup"),
            HtmlContent);
    }

    [Fact]
    public void Html_QualityProfilePopup_CleansUpOldEscapeHandler()
    {
        // Previous popup's Escape handler must be removed to prevent leaks
        Assert.Matches(
            new Regex(@"function\s+renderQualityProfilePopup[\s\S]*?_onEscape[\s\S]*?removeEventListener"),
            HtmlContent);
    }

    [Fact]
    public void Html_QualityProfilePopup_RestoresFocusToTriggerButton()
    {
        Assert.Matches(
            new Regex(@"function\s+closePopup[\s\S]*?btn\.focus\(\)"),
            HtmlContent);
    }

    // === Discovery request flow ===

    [Fact]
    public void Html_DiscoveryRequest_UsesPostEndpoint()
    {
        Assert.Contains("JellyfinHelper/Discovery/Request", HtmlContent);
    }

    [Fact]
    public void Html_DiscoveryRequest_SendsTmdbIdAndMediaType()
    {
        Assert.Matches(
            new Regex(@"function\s+submitDiscoveryRequest[\s\S]*?TmdbId\s*:\s*tmdbId[\s\S]*?MediaType\s*:\s*mediaType"),
            HtmlContent);
    }

    [Fact]
    public void Html_DiscoveryRequest_WithProfileSendsServerAndProfileId()
    {
        Assert.Matches(
            new Regex(@"function\s+submitDiscoveryRequestWithProfile[\s\S]*?ServerId\s*:\s*serverId[\s\S]*?ProfileId\s*:\s*profileId"),
            HtmlContent);
    }

    [Fact]
    public void Html_MarkDiscoveryItemRequested_MatchesByTmdbIdAndMediaType()
    {
        // Guards against false positives: TMDb movie & TV namespaces are separate.
        Assert.Matches(
            new Regex(@"function\s+markDiscoveryItemRequested[\s\S]*?recTmdbId\s*===\s*tmdbId[\s\S]*?recMediaType\s*===\s*mediaType"),
            HtmlContent);
    }

    [Fact]
    public void Html_HandleDiscoveryRequestError_ClearsPreviousErrorTimer()
    {
        // Prevents stale timers from overwriting a successful retry
        Assert.Matches(
            new Regex(@"function\s+handleDiscoveryRequestError[\s\S]*?_discoveryErrorTimer[\s\S]*?clearTimeout"),
            HtmlContent);
    }

    [Fact]
    public void Html_HandleDiscoveryRequestResponse_GuardsAgainstStaleCard()
    {
        // Card may be removed by user switching profiles - guard with document.contains
        Assert.Matches(
            new Regex(@"function\s+handleDiscoveryRequestResponse[\s\S]*?document\.contains\(card\)"),
            HtmlContent);
    }

    [Fact]
    public void Html_HandleDiscoveryRequestResponse_UpdatesCounter()
    {
        Assert.Matches(
            new Regex(@"function\s+handleDiscoveryRequestResponse[\s\S]*?getElementById\(\s*['""]discoveryCount['""]"),
            HtmlContent);
    }

    // === Discovery card filtering ===

    [Fact]
    public void Html_RenderDiscoveryCards_FiltersAlreadyRequestedItems()
    {
        Assert.Matches(
            new Regex(@"function\s+renderDiscoveryCards[\s\S]*?filter\(function[\s\S]*?!r\.AlreadyRequested"),
            HtmlContent);
    }

    // === Genre distribution helper ===

    [Fact]
    public void Html_GetTopGenres_UsesHasOwnPropertyGuard()
    {
        Assert.Matches(
            new Regex(@"function\s+getTopGenresFromDistribution[\s\S]*?hasOwnProperty\.call"),
            HtmlContent);
    }

    [Fact]
    public void Html_GetTopGenres_ReturnsEmptyArrayForNullInput()
    {
        Assert.Matches(
            new Regex(@"function\s+getTopGenresFromDistribution[\s\S]*?!genreDistribution[\s\S]*?return\s*\[\]"),
            HtmlContent);
    }

    // === Activity table ===

    [Fact]
    public void Html_ActivityTable_LimitedTo15Rows()
    {
        Assert.Contains("MAX_ACTIVITY_ROWS = 15", HtmlContent);
    }

    [Fact]
    public void Html_ActivityTable_EscapesTitleAndType()
    {
        Assert.Matches(
            new Regex(@"function\s+renderCompactActivityTable[\s\S]*?escHtml\(dn"),
            HtmlContent);
        Assert.Matches(
            new Regex(@"function\s+renderCompactActivityTable[\s\S]*?escHtml\(it\.ItemType"),
            HtmlContent);
    }

    [Fact]
    public void Html_ActivityTable_HandlesInvalidCompletionPercent()
    {
        // pct is clamped 0..100 even for NaN input
        Assert.Matches(
            new Regex(@"function\s+renderCompactActivityTable[\s\S]*?Math\.max\(0,\s*Math\.min\(100"),
            HtmlContent);
    }

    // === Collapsible sections ===

    [Fact]
    public void Html_ToggleCollapsible_UpdatesAriaExpanded()
    {
        Assert.Matches(
            new Regex(@"function\s+toggleCollapsible[\s\S]*?setAttribute\(\s*['""]aria-expanded['""]"),
            HtmlContent);
    }

    [Fact]
    public void Html_ToggleCollapsible_UpdatesArrowUnicode()
    {
        // Uses ► (U+25B6) closed / ▼ (U+25BC) open
        Assert.Matches(
            new Regex(@"function\s+toggleCollapsible[\s\S]*?u25B[6C]", RegexOptions.IgnoreCase),
            HtmlContent);
    }

    // === Empty-state rendering ===

    [Fact]
    public void Html_RenderRecommendations_CachesEmptyResults()
    {
        // Prevents repeated API calls on tab switches when the user genuinely has 0 recommendations
        Assert.Matches(
            new Regex(@"function\s+renderRecommendations[\s\S]*?window\._recsResults\s*=\s*results\s*\|\|\s*\[\]"),
            HtmlContent);
    }

    [Fact]
    public void Html_LoadDiscovery_ShowsUserFacingErrorOnFailure()
    {
        Assert.Contains("discoveryLoadError", HtmlContent);
    }
}
