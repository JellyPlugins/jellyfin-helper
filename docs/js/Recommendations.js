// --- Recommendations Tab (Smart Suggestions) ---

var _profileReqId = 0;
var _activityReqId = 0;

function initRecommendationsTab() {
    loadRecommendations();
}

function loadRecommendations() {
    var container = document.getElementById('recsContent');
    if (!container) return;

    container.innerHTML = '<div class="loading-overlay" style="padding:2em;">'
        + '<div class="spinner"></div>'
        + '<p>' + T('loadingRecommendations', 'Loading recommendations…') + '</p></div>';

    apiGet('JellyfinHelper/Recommendations', function (data) {
        renderRecommendations(container, data);
    }, function (err) {
        container.innerHTML = '<div class="error-msg">❌ '
            + T('recsError', 'Failed to load recommendations. Make sure the recommendation task has run at least once.')
            + '</div>';
        console.error('Jellyfin Helper: Error loading recommendations', err);
    });
}

function renderRecommendations(container, results) {
    if (!results || results.length === 0) {
        container.innerHTML = '<div class="recs-empty">'
            + '<div class="recs-empty-icon">🤖</div>'
            + '<p>' + T('recsEmpty', 'No recommendations available yet. Run the "Update Smart Recommendations" scheduled task first.') + '</p>'
            + '</div>';
        return;
    }

    var html = '';

    // Compact info line instead of big summary cards
    var totalRecs = 0;
    var totalUsers = results.length;
    for (var i = 0; i < results.length; i++) {
        totalRecs += results[i].Recommendations ? results[i].Recommendations.length : 0;
    }
    html += '<div class="recs-info-line">';
    html += '<span>👥 ' + totalUsers + ' ' + T('recsUsers', 'Users') + '</span>';
    html += '<span class="recs-info-sep">·</span>';
    html += '<span>🎯 ' + totalRecs + ' ' + T('recsTotal', 'Recommendations') + '</span>';
    html += '</div>';

    // User selector
    html += '<div class="recs-user-selector">';
    html += '<label for="recsUserSelect">' + T('recsSelectUser', 'Select User') + ': </label>';
    html += '<select id="recsUserSelect" class="recs-select">';
    for (var u = 0; u < results.length; u++) {
        html += '<option value="' + u + '">' + escHtml(results[u].UserName)
            + ' (' + (results[u].Recommendations ? results[u].Recommendations.length : 0) + ' ' + T('recsItems', 'items') + ')'
            + '</option>';
    }
    html += '</select>';
    html += '</div>';

    // Recommendations grid placeholder
    html += '<div id="recsUserGrid"></div>';

    // Collapsible: Watch Activity
    html += '<div class="recs-collapsible">';
    html += '<button class="recs-collapsible-toggle" id="recsActivityToggle">';
    html += '<span class="recs-collapsible-arrow">▶</span> ';
    html += '📊 ' + T('recsActivityToggle', 'Watch Activity');
    html += '</button>';
    html += '<div class="recs-collapsible-body" id="recsActivityBody">';
    // Watch profile stats (inline)
    html += '<div id="recsUserProfile"><div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div></div>';
    // Activity table
    html += '<div id="recsUserActivity"><div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div></div>';
    html += '</div>'; // end collapsible body
    html += '</div>'; // end collapsible

    container.innerHTML = html;

    // Store results globally for user switching
    window._recsResults = results;

    // Attach change handler for user dropdown
    var recsSelect = document.getElementById('recsUserSelect');
    if (recsSelect) {
        recsSelect.addEventListener('change', function () {
            onUserChanged(parseInt(recsSelect.value, 10));
        });
    }

    // Attach collapsible toggle
    var toggleBtn = document.getElementById('recsActivityToggle');
    if (toggleBtn) {
        toggleBtn.addEventListener('click', function () {
            toggleActivityCollapsible();
        });
    }

    // Render first user
    if (results.length > 0) {
        onUserChanged(0);
    }
}

/**
 * Called when the user dropdown changes. Renders recommendations,
 * loads watch profile and activity for the selected user.
 */
function onUserChanged(index) {
    renderUserRecommendations(index);

    // Collapse the activity section on user change
    var body = document.getElementById('recsActivityBody');
    var arrow = document.querySelector('.recs-collapsible-arrow');
    if (body) body.classList.remove('open');
    if (arrow) arrow.textContent = '▶';

    // Load watch profile and activity for this user
    loadUserWatchProfile(index);
    loadUserActivity(index);
}

function toggleActivityCollapsible() {
    var body = document.getElementById('recsActivityBody');
    var arrow = document.querySelector('.recs-collapsible-arrow');
    if (!body) return;

    var isOpen = body.classList.contains('open');
    if (isOpen) {
        body.classList.remove('open');
        if (arrow) arrow.textContent = '▶';
    } else {
        body.classList.add('open');
        if (arrow) arrow.textContent = '▼';
    }
}

function renderUserRecommendations(index) {
    var grid = document.getElementById('recsUserGrid');
    if (!grid || !window._recsResults) return;

    var result = window._recsResults[index];
    if (!result) return;

    var recs = result.Recommendations || [];
    if (recs.length === 0) {
        grid.innerHTML = '<div class="recs-empty"><p>'
            + T('recsNoItems', 'No recommendations for this user yet. More watch history is needed.')
            + '</p></div>';
        return;
    }

    var html = '<div class="recs-grid">';
    for (var i = 0; i < recs.length; i++) {
        var rec = recs[i];
        html += renderRecommendationCard(rec, i + 1);
    }
    html += '</div>';

    grid.innerHTML = html;
}

function renderRecommendationCard(rec, rank) {
    var scorePercent = Math.max(0, Math.min(100, Math.round((Number(rec.Score) || 0) * 100)));
    var scoreClass = scorePercent >= 80 ? 'recs-score-high'
        : scorePercent >= 50 ? 'recs-score-mid' : 'recs-score-low';

    var html = '<div class="recs-item">';
    html += '<div class="recs-item-rank">#' + rank + '</div>';
    html += '<div class="recs-item-body">';
    html += '<div class="recs-item-title">' + escHtml(rec.Name || 'Unknown') + '</div>';
    html += '<div class="recs-item-meta">';

    if (rec.ItemType) {
        html += '<span class="recs-tag recs-tag-type">' + escHtml(rec.ItemType) + '</span>';
    }

    if (rec.Genres && rec.Genres.length > 0) {
        for (var g = 0; g < Math.min(rec.Genres.length, 3); g++) {
            html += '<span class="recs-tag">' + escHtml(rec.Genres[g]) + '</span>';
        }
    }

    if (typeof rec.Year === 'number' && rec.Year > 0) {
        html += '<span class="recs-tag recs-tag-year">' + rec.Year + '</span>';
    }

    html += '</div>'; // meta

    html += '<div class="recs-item-reason">';
    html += '<span class="recs-reason-label">' + T('recsReason', 'Why') + ':</span> ';
    var reasonText = rec.ReasonKey ? T(rec.ReasonKey, rec.Reason || '') : (rec.Reason || T('recsReasonGeneric', 'Based on your viewing history'));
    if (rec.RelatedItemName && reasonText.indexOf('{0}') !== -1) {
        reasonText = reasonText.replace('{0}', rec.RelatedItemName);
    }
    html += escHtml(reasonText);
    html += '</div>';

    html += '<div class="recs-item-score ' + scoreClass + '">';
    html += '<div class="recs-score-bar" style="width:' + scorePercent + '%"></div>';
    html += '<span class="recs-score-text">' + scorePercent + '% ' + T('recsMatch', 'match') + '</span>';
    html += '</div>';

    html += '</div>'; // body
    html += '</div>'; // item

    return html;
}

// --- Watch Profile (compact stats for selected user) ---

function loadUserWatchProfile(index) {
    var container = document.getElementById('recsUserProfile');
    if (!container || !window._recsResults) return;

    var result = window._recsResults[index];
    if (!result || !result.UserId) {
        container.innerHTML = '';
        return;
    }

    container.innerHTML = '<div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div>';

    var reqId = ++_profileReqId;
    apiGet('JellyfinHelper/Recommendations/WatchProfile/' + result.UserId, function (profile) {
        if (reqId !== _profileReqId) return;
        renderCompactWatchProfile(container, profile);
    }, function () {
        if (reqId !== _profileReqId) return;
        container.innerHTML = '<div class="recs-profile-compact-empty">'
            + T('recsNoProfiles', 'No watch profile available.') + '</div>';
    });
}

function renderCompactWatchProfile(container, profile) {
    if (!profile) {
        container.innerHTML = '<div class="recs-profile-compact-empty">'
            + T('recsNoProfiles', 'No watch profile available.') + '</div>';
        return;
    }

    var totalWatched = (profile.WatchedMovieCount || 0) + (profile.WatchedEpisodeCount || 0);
    var topGenres = getTopGenresFromDistribution(profile.GenreDistribution, 5);

    var html = '<div class="recs-profile-compact">';
    html += '<div class="recs-profile-compact-stats">';
    html += '<span class="recs-profile-compact-stat">🎬 ' + totalWatched + ' ' + T('recsWatched', 'Watched') + '</span>';
    html += '<span class="recs-profile-compact-stat">📺 ' + (profile.WatchedSeriesCount || 0) + ' ' + T('recsSeries', 'Series') + '</span>';
    html += '<span class="recs-profile-compact-stat">⭐ ' + (profile.FavoriteCount || 0) + ' ' + T('recsFavorites', 'Favorites') + '</span>';
    html += '</div>';

    if (topGenres.length > 0) {
        html += '<div class="recs-profile-compact-genres">';
        html += '<span class="recs-profile-compact-genres-label">' + T('recsTopGenres', 'Top Genres') + ':</span> ';
        var genreLabels = [];
        for (var g = 0; g < topGenres.length; g++) {
            genreLabels.push(escHtml(topGenres[g]));
        }
        html += genreLabels.join(', ');
        html += '</div>';
    }

    html += '</div>';
    container.innerHTML = html;
}

// --- Activity (compact table for selected user) ---

function loadUserActivity(index) {
    var container = document.getElementById('recsUserActivity');
    if (!container || !window._recsResults) return;

    var result = window._recsResults[index];
    if (!result || !result.UserId) {
        container.innerHTML = '';
        return;
    }

    container.innerHTML = '<div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div>';

    var reqId = ++_activityReqId;
    apiGet('JellyfinHelper/UserActivity/User/' + result.UserId, function (items) {
        if (reqId !== _activityReqId) return;
        renderCompactActivityTable(container, items);
    }, function () {
        if (reqId !== _activityReqId) return;
        container.innerHTML = '<div class="recs-profile-compact-empty">'
            + T('activityNoData', 'No watch activity data available.') + '</div>';
    });
}

function renderCompactActivityTable(container, items) {
    if (!items || items.length === 0) {
        container.innerHTML = '<div class="recs-profile-compact-empty">'
            + T('activityNoData', 'No watch activity data available.') + '</div>';
        return;
    }

    var maxRows = Math.min(items.length, 15);

    var html = '<div class="recs-activity-section-title">'
        + T('recsRecentActivity', 'Recent Activity') + '</div>';
    html += '<table class="activity-table">';
    html += '<thead><tr>';
    html += '<th>' + T('activityItemName', 'Title') + '</th>';
    html += '<th>' + T('activityItemType', 'Type') + '</th>';
    html += '<th>' + T('activityPlays', 'Plays') + '</th>';
    html += '<th>' + T('activityLastWatched', 'Last Watched') + '</th>';
    html += '<th>' + T('activityCompletion', 'Completion') + '</th>';
    html += '</tr></thead><tbody>';

    for (var r = 0; r < maxRows; r++) {
        var it = items[r];
        var completionPct = Math.max(0, Math.min(100, Math.round(Number(it.AverageCompletionPercent) || 0)));
        var statusClass = completionPct >= 90 ? 'activity-status-done'
            : completionPct > 0 ? 'activity-status-progress' : 'activity-status-new';

        // Build display name: for episodes show "SeriesName – S01E03"
        var displayName = it.ItemName || '—';
        if (it.SeriesName) {
            displayName = it.SeriesName;
            if (it.EpisodeLabel) {
                displayName += ' \u2013 ' + it.EpisodeLabel;
            }
        }

        html += '<tr>';
        html += '<td class="activity-cell-title">' + escHtml(displayName) + '</td>';
        html += '<td><span class="recs-tag recs-tag-type">' + escHtml(it.ItemType || '') + '</span></td>';
        html += '<td class="activity-cell-num">' + (it.TotalPlayCount || 0) + '</td>';
        html += '<td>' + (it.MostRecentWatch ? new Date(it.MostRecentWatch).toLocaleDateString() : '—') + '</td>';
        html += '<td>';
        html += '<div class="activity-completion-bar">';
        html += '<div class="activity-completion-fill ' + statusClass + '" style="width:' + completionPct + '%"></div>';
        html += '<span class="activity-completion-text">' + completionPct + '%</span>';
        html += '</div>';
        html += '</td>';
        html += '</tr>';
    }

    html += '</tbody></table>';

    if (items.length > maxRows) {
        html += '<div class="activity-more">' + T('andMore', 'and') + ' ' + (items.length - maxRows) + ' ' + T('more', 'more') + '…</div>';
    }

    container.innerHTML = html;
}

/**
 * Extracts the top N genre names from a GenreDistribution dictionary object,
 * sorted by watch count descending.
 * @param {Object} genreDistribution - The genre distribution map (genre -> count).
 * @param {number} maxGenres - Maximum number of genres to return.
 * @returns {string[]} Array of genre names.
 */
function getTopGenresFromDistribution(genreDistribution, maxGenres) {
    if (!genreDistribution || typeof genreDistribution !== 'object') {
        return [];
    }
    var entries = [];
    for (var genre in genreDistribution) {
        if (Object.prototype.hasOwnProperty.call(genreDistribution, genre)) {
            entries.push({ name: genre, count: genreDistribution[genre] || 0 });
        }
    }
    entries.sort(function (a, b) { return b.count - a.count; });
    var result = [];
    for (var i = 0; i < Math.min(entries.length, maxGenres); i++) {
        result.push(entries[i].name);
    }
    return result;
}