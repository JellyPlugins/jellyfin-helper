// --- Recommendations Tab (Smart Suggestions) ---
const MAX_ACTIVITY_ROWS = 15;

let _profileReqId = 0;
let _activityReqId = 0;
let _recsListReqId = 0;

function initRecommendationsTab() {
    // If browser-cache already has results (e.g. from a previous tab visit), render directly
    // without triggering another API call. This avoids expensive on-demand generation on every tab switch.
    // Also caches empty results (length === 0) so empty-state responses don't re-trigger the API.
    // TTL: invalidate after 5 minutes so the UI picks up fresh results after the scheduled task runs.
    var ttlMs = 5 * 60 * 1000; // 5 minutes
    if (window._recsResults !== undefined && window._recsTimestamp && (Date.now() - window._recsTimestamp) < ttlMs) {
        var container = document.getElementById('recsContent');
        if (container) { renderRecommendations(container, window._recsResults); }
        return;
    }
    loadRecommendations();
}

function loadRecommendations() {
    var container = document.getElementById('recsContent');
    if (!container) return;
    container.innerHTML = '<div class="loading-overlay" style="padding:2em;"><div class="spinner"></div><p>' + T('loadingRecommendations', 'Loading recommendations…') + '</p></div>';
    var reqId = ++_recsListReqId;
    apiGet('JellyfinHelper/Recommendations', function (data) {
        if (reqId !== _recsListReqId) return;
        window._recsTimestamp = Date.now();
        renderRecommendations(container, data);
    }, function (err) {
        if (reqId !== _recsListReqId) return;
        container.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + T('recsError', 'Failed to load recommendations. Make sure the recommendation task has run at least once.') + '</div>';
        console.error('Jellyfin Helper: Error loading recommendations', err);
    });
}

function renderRecommendations(container, results) {
    // Cache results (including empty) so tab re-visits don't re-trigger API calls.
    // Timestamp tracks when the cache was populated for TTL-based invalidation.
    window._recsResults = results || [];
    window._recsTimestamp = window._recsTimestamp || Date.now();
    if (!results || results.length === 0) {
        container.innerHTML = '<div class="recs-empty"><div class="recs-empty-icon">' + mi('smart_toy') + '</div><p>' + T('recsEmpty', 'No recommendations available yet. Run the "Helper Cleanup" scheduled task first.') + '</p></div>';
        return;
    }
    var html = '';
    var totalRecs = 0, totalUsers = results.length;
    for (var i = 0; i < results.length; i++) { totalRecs += results[i].Recommendations ? results[i].Recommendations.length : 0; }
    html += '<div class="recs-info-line"><span class="icon-label-inline">' + mi('group') + totalUsers + ' ' + T('recsUsers', 'Users') + '</span><span class="recs-info-sep">\u2022</span><span class="icon-label-inline">' + mi('track_changes') + totalRecs + ' ' + T('recsTotal', 'Recommendations') + '</span></div>';
    html += '<div class="recs-user-selector"><label for="recsUserSelect">' + T('recsSelectUser', 'Select User') + ': </label><select id="recsUserSelect" class="recs-select">';
    for (var u = 0; u < results.length; u++) {
        html += '<option value="' + u + '">' + escHtml(results[u].UserName) + ' (' + (results[u].Recommendations ? results[u].Recommendations.length : 0) + ' ' + T('recsItems', 'items') + ')</option>';
    }
    html += '</select></div>';

    // Collapsible Recommendations section
    html += '<div class="recs-collapsible"><button class="recs-collapsible-toggle" id="recsGridToggle" aria-expanded="false" aria-controls="recsGridBody"><span class="recs-collapsible-arrow">\u25B6</span> ' + mi('track_changes') + ' ' + T('recsSubtabRecommendations', 'Recommendations') + ' <span>(<span id="recsGridCount">0</span> ' + T('recsItems', 'items') + ')</span></button>';
    html += '<div class="recs-collapsible-body" id="recsGridBody">';
    html += '<div id="recsUserGrid"></div>';
    html += '</div></div>';

    // Collapsible Watch Activity section
    html += '<div class="recs-collapsible"><button class="recs-collapsible-toggle" id="recsActivityToggle" aria-expanded="false" aria-controls="recsActivityBody"><span class="recs-collapsible-arrow">\u25B6</span> ' + mi('bar_chart') + ' ' + T('recsActivityToggle', 'Watch Activity') + '</button>';
    html += '<div class="recs-collapsible-body" id="recsActivityBody">';
    html += '<div id="recsUserProfile"><div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div></div>';
    html += '<div id="recsUserActivity"><div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div></div>';
    html += '</div></div>';
    html += '<div id="discoverySection"></div>';
    container.innerHTML = html;
    var recsSelect = document.getElementById('recsUserSelect');
    if (recsSelect) {
        recsSelect.addEventListener('change', function () {
            var idx = parseInt(recsSelect.value, 10);
            // Persist selected user in browser storage so it survives page refresh
            try { var uid = results[idx] && results[idx].UserId; if (uid) localStorage.setItem('jh_recsSelectedUser', uid); } catch (e) { /* localStorage unavailable */ }
            onUserChanged(idx);
        });
    }

    // Toggle for Recommendations collapsible
    var gridToggleBtn = document.getElementById('recsGridToggle');
    if (gridToggleBtn) { gridToggleBtn.addEventListener('click', function () { toggleCollapsible('recsGridBody', 'recsGridToggle'); }); }

    // Toggle for Watch Activity collapsible
    var toggleBtn = document.getElementById('recsActivityToggle');
    if (toggleBtn) { toggleBtn.addEventListener('click', function () { toggleCollapsible('recsActivityBody', 'recsActivityToggle'); }); }

    var discoveryContainer = document.getElementById('discoverySection');
    if (discoveryContainer) { renderDiscoverySection(discoveryContainer, results); }

    // Restore previously selected user from browser storage (fallback: first user)
    var initialIdx = 0;
    try {
        var savedUserId = (localStorage.getItem('jh_recsSelectedUser') || '').toLowerCase();
        if (savedUserId) {
            for (var s = 0; s < results.length; s++) {
                if ((results[s].UserId || '').toLowerCase() === savedUserId) { initialIdx = s; break; }
            }
        }
    } catch (e) { /* localStorage unavailable - use default */ }
    if (recsSelect && initialIdx > 0) { recsSelect.value = '' + initialIdx; }
    onUserChanged(initialIdx);
}

function onUserChanged(index) {
    renderUserRecommendations(index);
    // Keep collapsible states as-is on user change - content updates in place
    loadUserWatchProfile(index);
    loadUserActivity(index);
    loadDiscoveryForUser(index);
}

function toggleCollapsible(bodyId, toggleId) {
    var body = document.getElementById(bodyId);
    var toggle = document.getElementById(toggleId);
    var arrow = document.querySelector('#' + toggleId + ' .recs-collapsible-arrow');
    if (!body) return;
    if (body.classList.contains('open')) {
        body.classList.remove('open');
        if (arrow) arrow.textContent = '\u25B6';
        if (toggle) toggle.setAttribute('aria-expanded', 'false');
    } else {
        body.classList.add('open');
        if (arrow) arrow.textContent = '\u25BC';
        if (toggle) toggle.setAttribute('aria-expanded', 'true');
    }
}

function renderUserRecommendations(index) {
    var grid = document.getElementById('recsUserGrid');
    var countSpan = document.getElementById('recsGridCount');
    if (!grid || !window._recsResults) return;
    var result = window._recsResults[index];
    if (!result) return;
    var recs = result.Recommendations || [];

    // Update the count in the collapsible header
    if (countSpan) countSpan.textContent = '' + recs.length;

    if (recs.length === 0) { grid.innerHTML = '<div class="recs-empty"><p>' + T('recsNoItems', 'No recommendations for this user yet. More watch history is needed.') + '</p></div>'; return; }
    // Sort by score descending so the UI ranking matches the match percentage.
    // The backend uses MMR diversity-reranking which intentionally interleaves genres,
    // but the display order should be intuitive (highest match first).
    var sorted = recs.slice().sort(function (a, b) { return (b.Score || 0) - (a.Score || 0); });
    var html = '<div class="recs-grid">';
    for (var i = 0; i < sorted.length; i++) { html += renderRecommendationCard(sorted[i], i + 1); }
    html += '</div>';
    grid.innerHTML = html;
}

function renderRecommendationCard(rec, rank) {
    var scorePercent = Math.max(0, Math.min(100, Math.round((Number(rec.Score) || 0) * 100)));
    var scoreClass = scorePercent >= 80 ? 'recs-score-high' : scorePercent >= 50 ? 'recs-score-mid' : 'recs-score-low';
    var html = '<div class="recs-item"><div class="recs-item-rank">#' + rank + '</div><div class="recs-item-body">';
    html += '<div class="recs-item-title">' + escHtml(rec.Name || T('recsUnknownTitle', 'Unknown')) + '</div><div class="recs-item-meta">';
    if (rec.ItemType) { html += '<span class="recs-tag recs-tag-type">' + escHtml(rec.ItemType) + '</span>'; }
    if (rec.Genres && rec.Genres.length > 0) { for (var g = 0; g < Math.min(rec.Genres.length, 3); g++) { html += '<span class="recs-tag">' + escHtml(rec.Genres[g]) + '</span>'; } }
    if (typeof rec.Year === 'number' && rec.Year > 0) { html += '<span class="recs-tag recs-tag-year">' + rec.Year + '</span>'; }
    html += '</div>';
    html += '<div class="recs-item-reason"><span class="recs-reason-label">' + T('recsReason', 'Why') + ':</span> ';
    var reasonText = rec.ReasonKey ? T(rec.ReasonKey, rec.Reason || '') : (rec.Reason || T('recsReasonGeneric', 'Based on your viewing history'));
    // Replace {0}, {1}, ... placeholders with parts from RelatedItemName (split on " | ")
    // Uses a single-pass regex to prevent cascading replacements when a part contains "{1}" etc.
    if (rec.RelatedItemName) {
        var parts = rec.RelatedItemName.split(' | ');
        reasonText = reasonText.replace(/\{(\d+)\}/g, function (m, idx) {
            var i = parseInt(idx, 10);
            return (i >= 0 && i < parts.length) ? parts[i] : m;
        });
    }
    html += escHtml(reasonText) + '</div>';
    html += '<div class="recs-item-score ' + scoreClass + '"><div class="recs-score-bar" style="width:' + scorePercent + '%"></div>';
    html += '<span class="recs-score-text">' + scorePercent + '% ' + T('recsMatch', 'match') + '</span></div>';
    html += '</div></div>';
    return html;
}

function loadUserWatchProfile(index) {
    var container = document.getElementById('recsUserProfile');
    if (!container || !window._recsResults) return;
    var result = window._recsResults[index];
    if (!result || !result.UserId) { container.innerHTML = ''; return; }
    // Return cached profile if already fetched (avoids redundant API calls on user switch)
    if (result._cachedProfile !== undefined) {
        renderCompactWatchProfile(container, result._cachedProfile);
        return;
    }
    container.innerHTML = '<div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div>';
    var reqId = ++_profileReqId;
    apiGet('JellyfinHelper/Recommendations/WatchProfile/' + result.UserId, function (profile) {
        if (reqId !== _profileReqId) return;
        result._cachedProfile = profile;
        renderCompactWatchProfile(container, profile);
    }, function () {
        if (reqId !== _profileReqId) return;
        // Do not cache failures so a later user-switch can retry.
        container.innerHTML = '<div class="recs-profile-compact-empty">' + T('recsNoProfiles', 'No watch profile available.') + '</div>';
    });
}

function renderCompactWatchProfile(container, profile) {
    if (!profile) { container.innerHTML = '<div class="recs-profile-compact-empty">' + T('recsNoProfiles', 'No watch profile available.') + '</div>'; return; }
    var totalWatched = (profile.WatchedMovieCount || 0) + (profile.WatchedEpisodeCount || 0);
    var topGenres = getTopGenresFromDistribution(profile.GenreDistribution, 5);
    var html = '<div class="recs-profile-compact"><div class="recs-profile-compact-stats">';
    html += '<span class="recs-profile-compact-stat">' + mi('movie') + totalWatched + ' ' + T('recsWatched', 'Watched') + '</span>';
    html += '<span class="recs-profile-compact-stat">' + mi('tv') + (profile.WatchedSeriesCount || 0) + ' ' + T('recsSeries', 'Series') + '</span>';
    html += '<span class="recs-profile-compact-stat">' + mi('star') + (profile.FavoriteCount || 0) + ' ' + T('recsFavorites', 'Favorites') + '</span></div>';
    if (topGenres.length > 0) {
        html += '<div class="recs-profile-compact-genres"><span class="recs-profile-compact-genres-label">' + T('recsTopGenres', 'Top Genres') + ':</span> ';
        var gl = [];
        for (var g = 0; g < topGenres.length; g++) { gl.push(escHtml(topGenres[g])); }
        html += gl.join(', ') + '</div>';
    }
    html += '</div>';
    container.innerHTML = html;
}

function loadUserActivity(index) {
    var container = document.getElementById('recsUserActivity');
    if (!container || !window._recsResults) return;
    var result = window._recsResults[index];
    if (!result || !result.UserId) { container.innerHTML = ''; return; }
    // Return cached activity if already fetched (avoids redundant API calls on user switch)
    if (result._cachedActivity !== undefined) {
        renderCompactActivityTable(container, result._cachedActivity);
        return;
    }
    container.innerHTML = '<div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div>';
    var reqId = ++_activityReqId;
    apiGet('JellyfinHelper/UserActivity/User/' + result.UserId, function (items) {
        if (reqId !== _activityReqId) return;
        result._cachedActivity = items;
        renderCompactActivityTable(container, items);
    }, function () {
        if (reqId !== _activityReqId) return;
        // Do not cache failures so a later user-switch can retry.
        container.innerHTML = '<div class="recs-profile-compact-empty">' + T('activityNoData', 'No watch activity data available.') + '</div>';
    });
}

function renderCompactActivityTable(container, items) {
    if (!items || items.length === 0) { container.innerHTML = '<div class="recs-profile-compact-empty">' + T('activityNoData', 'No watch activity data available.') + '</div>'; return; }
    var maxRows = Math.min(items.length, MAX_ACTIVITY_ROWS);
    var html = '<div class="recs-activity-section-title">' + T('recsRecentActivity', 'Recent Activity') + '</div>';
    html += '<table class="activity-table"><thead><tr>';
    html += '<th>' + T('activityItemName', 'Title') + '</th>';
    html += '<th>' + T('activityItemType', 'Type') + '</th>';
    html += '<th class="activity-cell-num">' + T('activityPlays', 'Plays') + '</th>';
    html += '<th>' + T('activityLastWatched', 'Last Watched') + '</th>';
    html += '<th>' + T('activityCompletion', 'Completion') + '</th>';
    html += '</tr></thead><tbody>';
    for (var r = 0; r < maxRows; r++) {
        var it = items[r];
        var pct = Math.max(0, Math.min(100, Math.round(Number(it.AverageCompletionPercent) || 0)));
        var sc = pct >= 90 ? 'activity-status-done' : pct > 0 ? 'activity-status-progress' : 'activity-status-new';
        var dn = it.ItemName || '\u2014';
        if (it.SeriesName) {
            dn = it.SeriesName;
            var episodePart = it.EpisodeLabel || it.ItemName;
            if (episodePart) { dn += ' \u2013 ' + episodePart; }
        }
        html += '<tr><td class="activity-cell-title">' + escHtml(dn) + '</td>';
        html += '<td><span class="recs-tag recs-tag-type">' + escHtml(it.ItemType || '') + '</span></td>';
        html += '<td class="activity-cell-num">' + (it.TotalPlayCount || 0) + '</td>';
        html += '<td>' + (it.MostRecentWatch ? new Date(it.MostRecentWatch).toLocaleDateString() : '\u2014') + '</td>';
        html += '<td><div class="activity-completion-bar"><div class="activity-completion-fill ' + sc + '" style="width:' + pct + '%"></div>';
        html += '<span class="activity-completion-text">' + pct + '%</span></div></td></tr>';
    }
    html += '</tbody></table>';
    if (items.length > maxRows) { html += '<div class="activity-more">' + escHtml(T('recsAndMore', 'and {0} more\u2026').replace(/\{0\}/g, items.length - maxRows)) + '</div>'; }
    container.innerHTML = html;
}

function getTopGenresFromDistribution(genreDistribution, maxGenres) {
    if (!genreDistribution || typeof genreDistribution !== 'object') return [];
    var entries = [];
    for (var genre in genreDistribution) { if (Object.prototype.hasOwnProperty.call(genreDistribution, genre)) { entries.push({ name: genre, count: genreDistribution[genre] || 0 }); } }
    entries.sort(function (a, b) { return b.count - a.count; });
    var result = [];
    for (var i = 0; i < Math.min(entries.length, maxGenres); i++) { result.push(entries[i].name); }
    return result;
}
// === Discovery New Content Section ===
var _discoveryReqId = 0;

function renderDiscoverySection(container, results) {
    var html = '<div class="recs-collapsible">';
    html += '<button class="recs-collapsible-toggle" id="discoveryToggle" ';
    html += 'aria-expanded="false" aria-controls="discoveryBody">';
    html += '<span class="recs-collapsible-arrow">\u25B6</span> ';
    html += mi('explore') + ' ';
    html += T('discoveryTitle', 'Discover New Content');
    html += ' <span>(<span id="discoveryCount">0</span> ';
    html += T('recsItems', 'items') + ')</span>';
    html += '</button>';
    html += '<div class="recs-collapsible-body" id="discoveryBody">';
    html += '<div id="discoveryGrid"></div>';
    html += '</div></div>';
    container.innerHTML = html;

    var toggleBtn = document.getElementById('discoveryToggle');
    if (toggleBtn) {
        toggleBtn.addEventListener('click', function () {
            toggleCollapsible('discoveryBody', 'discoveryToggle');
        });
    }
}

// Global cache for the full /Discovery API response (all users in one call)
var _discoveryAllUsersCache = undefined;
var _discoveryAllUsersCacheTimestamp = 0;
// Discovery cache TTL: 5 minutes (same as recommendations results cache)
var _discoveryCacheTtlMs = 5 * 60 * 1000;

function loadDiscoveryForUser(index) {
    var grid = document.getElementById('discoveryGrid');
    var countSpan = document.getElementById('discoveryCount');
    if (!grid) return;

    var results = window._recsResults;
    if (!results || !results[index]) return;
    var result = results[index];

    var cacheAge = Date.now() - _discoveryAllUsersCacheTimestamp;

    // If we already have a per-user cache entry AND the global cache is still fresh, render immediately
    if (result._cachedDiscovery !== undefined && cacheAge < _discoveryCacheTtlMs) {
        renderDiscoveryCards(grid, countSpan, result._cachedDiscovery);
        return;
    }

    // If we have the global response cached and it's still fresh, extract user data without another API call
    if (_discoveryAllUsersCache !== undefined && cacheAge < _discoveryCacheTtlMs) {
        result._cachedDiscovery = findUserDiscovery(_discoveryAllUsersCache, result.UserId);
        renderDiscoveryCards(grid, countSpan, result._cachedDiscovery);
        return;
    }

    // Cache expired — invalidate ALL per-user caches so fresh data is fetched
    if (_discoveryAllUsersCache !== undefined && cacheAge >= _discoveryCacheTtlMs) {
        _discoveryAllUsersCache = undefined;
        for (var k = 0; k < results.length; k++) {
            if (results[k]) { results[k]._cachedDiscovery = undefined; }
        }
    }

    grid.innerHTML = '<div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div>';
    var reqId = ++_discoveryReqId;

    apiGet('JellyfinHelper/Discovery', function (data) {
        if (reqId !== _discoveryReqId) return;
        // Cache the full API response globally so subsequent user switches don't re-fetch
        _discoveryAllUsersCache = data || [];
        _discoveryAllUsersCacheTimestamp = Date.now();
        result._cachedDiscovery = findUserDiscovery(_discoveryAllUsersCache, result.UserId);
        renderDiscoveryCards(grid, countSpan, result._cachedDiscovery);
    }, function () {
        if (reqId !== _discoveryReqId) return;
        grid.innerHTML = '<div class="recs-profile-compact-empty">' +
            T('discoveryLoadError', 'Could not load discovery suggestions.') + '</div>';
    });
}

function findUserDiscovery(allData, userId) {
    if (!allData || allData.length === 0 || !userId) return null;
    var targetId = userId.toLowerCase();
    for (var d = 0; d < allData.length; d++) {
        if ((allData[d].UserId || allData[d].userId || '').toLowerCase() === targetId) {
            return allData[d];
        }
    }
    return null;
}

function renderDiscoveryCards(grid, countSpan, userDiscovery) {
    if (!userDiscovery || !userDiscovery.Recommendations || userDiscovery.Recommendations.length === 0) {
        if (countSpan) countSpan.textContent = '0';
        // Show configuration hint if discovery data is completely absent (Seerr likely not configured)
        var config = window._pluginConfig || {};
        var seerrConfigured = config.SeerrUrl && config.SeerrApiKey;
        var message = seerrConfigured
            ? T('discoveryNoResults', 'No suggestions available yet. Results will appear after the next scheduled task run.')
            : T('discoveryConfigureSeerr', 'Configure Seerr in the Settings tab to see personalized download suggestions.');
        grid.innerHTML = '<div class="recs-profile-compact-empty">' + message + '</div>';
        return;
    }

    var recs = userDiscovery.Recommendations.filter(function(r) { return !r.AlreadyRequested; });
    if (countSpan) countSpan.textContent = '' + recs.length;

    if (recs.length === 0) {
        grid.innerHTML = '<div class="recs-profile-compact-empty">' +
            T('discoveryNoResults', 'No suggestions available yet. Results will appear after the next scheduled task run.') +
            '</div>';
        return;
    }

    var html = '<div class="discovery-grid">';
    for (var i = 0; i < recs.length; i++) {
        html += renderDiscoveryCard(recs[i], i);
    }
    html += '</div>';
    if (userDiscovery.GeneratedAt) {
        var genDate = new Date(userDiscovery.GeneratedAt);
        html += '<div class="discovery-footer">' +
            T('discoveryGeneratedAt', 'Last updated') + ': ' +
            genDate.toLocaleDateString() + ' ' + genDate.toLocaleTimeString() +
            '</div>';
    }
    grid.innerHTML = html;

    var buttons = grid.querySelectorAll('.discovery-request-btn');
    for (var b = 0; b < buttons.length; b++) {
        buttons[b].addEventListener('click', handleDiscoveryRequest);
    }
}

function renderDiscoveryCard(rec, index) {
    var scorePercent = Math.max(0, Math.min(100, Math.round((Number(rec.Score) || 0) * 100)));
    var scoreClass = scorePercent >= 80 ? 'recs-score-high' : scorePercent >= 50 ? 'recs-score-mid' : 'recs-score-low';
    var posterUrl = rec.PosterPath
        ? 'https://image.tmdb.org/t/p/w185' + escHtml(rec.PosterPath)
        : '';

    var html = '<div class="discovery-card" data-index="' + index + '">';

    if (posterUrl) {
        html += '<div class="discovery-card-poster"><img src="' + posterUrl + '" alt="" loading="lazy"></div>';
    } else {
        html += '<div class="discovery-card-poster discovery-card-poster-empty">' + mi('image') + '</div>';
    }

    html += '<div class="discovery-card-body">';
    html += '<div class="discovery-card-title">' + escHtml(rec.Title) + '</div>';
    html += '<div class="discovery-card-meta">';
    if (rec.MediaType) {
        html += '<span class="recs-tag recs-tag-type">' + escHtml(rec.MediaType === 'tv' ? T('tvShows', 'Series') : T('movies', 'Movie')) + '</span>';
    }
    if (rec.Genres && rec.Genres.length > 0) {
        for (var g = 0; g < Math.min(rec.Genres.length, 3); g++) {
            html += '<span class="recs-tag">' + escHtml(rec.Genres[g]) + '</span>';
        }
    }
    if (rec.Year) { html += '<span class="recs-tag recs-tag-year">' + rec.Year + '</span>'; }
    var ratingNum = Number(rec.TmdbRating);
    if (!isNaN(ratingNum) && ratingNum > 0) { html += '<span class="recs-tag recs-tag-rating">' + ratingNum.toFixed(1) + '</span>'; }
    html += '</div>';

    html += '<div class="recs-item-score ' + scoreClass + '">';
    html += '<div class="recs-score-bar" style="width:' + scorePercent + '%"></div>';
    html += '<span class="recs-score-text">' + scorePercent + '% ' + T('recsMatch', 'match') + '</span>';
    html += '</div>';

    var reasonText = rec.ReasonKey ? T(rec.ReasonKey, rec.Reason || '') : (rec.Reason || '');
    if (rec.RelatedInfo) {
        reasonText = reasonText.replace(/\{0\}/g, rec.RelatedInfo);
    }
    if (reasonText) {
        html += '<div class="discovery-card-reason">' + escHtml(reasonText) + '</div>';
    }

    if (rec.AlreadyRequested) {
        html += '<button class="discovery-request-btn discovery-request-done" disabled>';
        html += mi('check_circle') + ' ' + T('discoveryRequested', 'Requested');
        html += '</button>';
    } else {
        html += '<button class="discovery-request-btn" ';
        html += 'data-tmdb-id="' + rec.TmdbId + '" data-media-type="' + escHtml(rec.MediaType) + '">';
        html += mi('cloud_download') + ' ' + T('discoveryRequest', 'Request');
        html += '</button>';
    }

    html += '</div></div>';
    return html;
}

function handleDiscoveryRequest(e) {
    var btn = e.currentTarget;
    if (btn.disabled) return;

    var tmdbId = parseInt(btn.getAttribute('data-tmdb-id'), 10);
    var mediaType = btn.getAttribute('data-media-type');
    if (!tmdbId || !mediaType) return;

    // Show profile selection popup before submitting
    showSeerrUserPopup(tmdbId, mediaType, btn);
}

function showSeerrUserPopup(tmdbId, mediaType, btn) {
    // Determine which service to query based on media type
    var serviceType = (mediaType === 'tv') ? 'sonarr' : 'radarr';
    var cacheKey = '_seerrServices_' + serviceType;

    // Check if we have cached service info
    if (window[cacheKey] !== undefined) {
        renderQualityProfilePopup(tmdbId, mediaType, btn, window[cacheKey]);
        return;
    }

    // Fetch service info from Seerr
    btn.disabled = true;
    btn.innerHTML = '<div class="spinner" style="width:1em;height:1em;"></div>';

    apiGet('JellyfinHelper/Discovery/Services/' + serviceType, function (services) {
        window[cacheKey] = services || [];
        btn.disabled = false;
        btn.innerHTML = mi('cloud_download') + ' ' + T('discoveryRequest', 'Request');
        renderQualityProfilePopup(tmdbId, mediaType, btn, window[cacheKey]);
    }, function () {
        // If service fetch fails, submit without profile selection.
        // Do NOT cache the failure — allow retry on next click.
        delete window[cacheKey];
        btn.disabled = false;
        btn.innerHTML = mi('cloud_download') + ' ' + T('discoveryRequest', 'Request');
        submitDiscoveryRequest(tmdbId, mediaType, null, btn);
    });
}

function renderQualityProfilePopup(tmdbId, mediaType, btn, services) {
    // Remove any existing popup and clean up its Escape key handler.
    // The previous popup's onEscape listener is stored on the element as _onEscape.
    var existing = document.getElementById('seerrUserPopup');
    if (existing) {
        if (existing._onEscape) {
            document.removeEventListener('keydown', existing._onEscape);
        }
        existing.remove();
    }

    // If no services or no profiles available, submit directly with defaults
    if (!services || services.length === 0) {
        submitDiscoveryRequest(tmdbId, mediaType, null, btn);
        return;
    }

    // Collect all profiles across all servers
    var allProfiles = [];
    for (var s = 0; s < services.length; s++) {
        var svc = services[s];
        var profiles = svc.Profiles || svc.profiles || [];
        for (var p = 0; p < profiles.length; p++) {
            allProfiles.push({
                serverId: svc.Id || svc.id,
                serverName: svc.Name || svc.name || ('Server #' + (svc.Id || svc.id)),
                profileId: profiles[p].Id || profiles[p].id,
                profileName: profiles[p].Name || profiles[p].name,
                isDefault: (profiles[p].Id || profiles[p].id) === (svc.ActiveProfileId || svc.activeProfileId),
                rootFolder: svc.ActiveDirectory || svc.activeDirectory || ''
            });
        }
    }

    // If no profiles found, submit with defaults
    if (allProfiles.length === 0) {
        submitDiscoveryRequest(tmdbId, mediaType, null, btn);
        return;
    }

    // Create popup overlay
    var overlay = document.createElement('div');
    overlay.id = 'seerrUserPopup';
    overlay.className = 'discovery-popup-overlay';

    var popup = document.createElement('div');
    popup.className = 'discovery-popup';

    var title = document.createElement('div');
    title.className = 'discovery-popup-title';
    title.textContent = T('discoverySelectQualityProfile', 'Select Quality Profile');
    popup.appendChild(title);

    var subtitle = document.createElement('div');
    subtitle.className = 'discovery-popup-subtitle';
    subtitle.textContent = T('discoverySelectQualityProfileDesc', 'Choose which quality profile to use for the download:');
    popup.appendChild(subtitle);

    var list = document.createElement('div');
    list.className = 'discovery-popup-list';

    for (var i = 0; i < allProfiles.length; i++) {
        var prof = allProfiles[i];
        var item = document.createElement('button');
        item.className = 'discovery-popup-user' + (prof.isDefault ? ' discovery-popup-user-default' : '');
        var label = escHtml(prof.profileName);
        if (services.length > 1) { label += ' <span style="opacity:0.6">(' + escHtml(prof.serverName) + ')</span>'; }
        if (prof.isDefault) { label += ' <span style="opacity:0.5; font-size:0.8em">\u2605 ' + escHtml(T('discoveryProfileDefault', 'default')) + '</span>'; }
        item.innerHTML = mi('high_quality') + ' ' + label;
        item.addEventListener('click', (function (serverId, profileId, rootFolder) {
            return function () {
                closePopup();
                submitDiscoveryRequestWithProfile(tmdbId, mediaType, serverId, profileId, rootFolder, btn);
            };
        })(prof.serverId, prof.profileId, prof.rootFolder));
        list.appendChild(item);
    }
    popup.appendChild(list);

    // Cancel button
    var cancelBtn = document.createElement('button');
    cancelBtn.className = 'discovery-popup-cancel';
    cancelBtn.textContent = T('discoveryCancel', 'Cancel');
    cancelBtn.addEventListener('click', closePopup);
    popup.appendChild(cancelBtn);

    overlay.appendChild(popup);
    document.body.appendChild(overlay);

    // Close on overlay click (outside popup)
    overlay.addEventListener('click', function (ev) {
        if (ev.target === overlay) closePopup();
    });

    // Close on Escape key
    function onEscape(ev) {
        if (ev.key === 'Escape') closePopup();
    }
    document.addEventListener('keydown', onEscape);
    // Store reference on the element so a subsequent popup can clean up this handler
    overlay._onEscape = onEscape;

    function closePopup() {
        document.removeEventListener('keydown', onEscape);
        var el = document.getElementById('seerrUserPopup');
        if (el) el.remove();
    }
}

/**
 * Shared handler for discovery request API responses.
 * Manages button state, card removal animation, and counter updates.
 */
function handleDiscoveryRequestResponse(res, btn, tmdbId) {
    if (res && res.Success) {
        btn.classList.add('discovery-request-done');
        btn.innerHTML = mi('check_circle') + ' ' + T('discoveryRequested', 'Requested');
        markDiscoveryItemRequested(tmdbId);

        // Fade out and remove the card after brief success display
        var card = btn.closest('.discovery-card');
        if (card) {
            setTimeout(function () {
                card.classList.add('discovery-card-removing');
                setTimeout(function () {
                    card.remove();
                    var countSpan = document.getElementById('discoveryCount');
                    if (countSpan) {
                        var current = parseInt(countSpan.textContent, 10) || 0;
                        countSpan.textContent = '' + Math.max(0, current - 1);
                    }
                }, 300);
            }, 800);
        }
    } else {
        handleDiscoveryRequestError(btn);
    }
}

/**
 * Shared error handler for discovery request failures.
 * Resets button state with a brief error display.
 */
function handleDiscoveryRequestError(btn) {
    btn.disabled = false;
    btn.innerHTML = mi('error') + ' ' + T('discoveryRequestFailed', 'Failed');
    setTimeout(function () {
        btn.innerHTML = mi('cloud_download') + ' ' + T('discoveryRequest', 'Request');
    }, 3000);
}

function submitDiscoveryRequestWithProfile(tmdbId, mediaType, serverId, profileId, rootFolder, btn) {
    btn.disabled = true;
    btn.innerHTML = '<div class="spinner" style="width:1em;height:1em;"></div> ' + T('discoveryRequesting', 'Requesting\u2026');

    var payload = { TmdbId: tmdbId, MediaType: mediaType, ServerId: serverId, ProfileId: profileId, RootFolder: rootFolder };

    apiPost('JellyfinHelper/Discovery/Request', payload, function (res) {
        handleDiscoveryRequestResponse(res, btn, tmdbId);
    }, function () {
        handleDiscoveryRequestError(btn);
    });
}

function submitDiscoveryRequest(tmdbId, mediaType, seerrUserId, btn) {
    btn.disabled = true;
    btn.innerHTML = '<div class="spinner" style="width:1em;height:1em;"></div> ' + T('discoveryRequesting', 'Requesting\u2026');

    var payload = { TmdbId: tmdbId, MediaType: mediaType };
    if (seerrUserId) payload.SeerrUserId = seerrUserId;

    apiPost('JellyfinHelper/Discovery/Request', payload, function (res) {
        handleDiscoveryRequestResponse(res, btn, tmdbId);
    }, function () {
        handleDiscoveryRequestError(btn);
    });
}

function markDiscoveryItemRequested(tmdbId) {
    // Update the cached discovery data so the item is marked as already requested
    // and won't reappear when switching between users and back.
    function markInDiscovery(userDiscovery) {
        if (!userDiscovery || !userDiscovery.Recommendations) return;
        for (var r = 0; r < userDiscovery.Recommendations.length; r++) {
            if (userDiscovery.Recommendations[r].TmdbId === tmdbId) {
                userDiscovery.Recommendations[r].AlreadyRequested = true;
            }
        }
    }

    // Mark in per-user cached discovery (from _recsResults)
    var results = window._recsResults;
    if (results) {
        for (var i = 0; i < results.length; i++) {
            markInDiscovery(results[i] && results[i]._cachedDiscovery);
        }
    }

    // Also mark in the global all-users cache so switching users doesn't resurface the item
    if (Array.isArray(_discoveryAllUsersCache)) {
        for (var u = 0; u < _discoveryAllUsersCache.length; u++) {
            markInDiscovery(_discoveryAllUsersCache[u]);
        }
    }
}
