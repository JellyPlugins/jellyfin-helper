// --- Recommendations Tab (Smart Suggestions) ---

var _recsLoaded = false;

function initRecommendationsTab() {
    if (_recsLoaded) return;
    _recsLoaded = true;
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

    // Summary cards row
    html += '<div class="recs-summary">';
    var totalRecs = 0;
    var totalUsers = results.length;
    for (var i = 0; i < results.length; i++) {
        totalRecs += results[i].Recommendations ? results[i].Recommendations.length : 0;
    }
    html += renderRecsSummaryCard('👥', T('recsUsers', 'Users Analyzed'), totalUsers);
    html += renderRecsSummaryCard('🎯', T('recsTotal', 'Total Recommendations'), totalRecs);
    html += renderRecsSummaryCard('🧠', T('recsEngine', 'Engine'), T('recsEngineType', 'Content + Collaborative'));
    html += '</div>';

    // Sub-tabs: Recommendations | Activity
    html += '<div class="recs-subtabs">';
    html += '<button class="recs-subtab active" data-panel="recsPanel">'
        + '🎯 ' + T('recsSubtabRecommendations', 'Recommendations') + '</button>';
    html += '<button class="recs-subtab" data-panel="activityPanel">'
        + '📊 ' + T('recsSubtabActivity', 'Watch Activity') + '</button>';
    html += '</div>';

    // === Panel 1: Recommendations ===
    html += '<div id="recsPanel" class="recs-subtab-panel active">';

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

    // Watch profile section
    html += '<div class="recs-section-title">' + T('recsWatchProfiles', 'Watch Profiles Overview') + '</div>';
    html += '<div id="recsWatchProfiles"><div class="loading-overlay" style="padding:1em;"><div class="spinner"></div></div></div>';

    html += '</div>'; // end recsPanel

    // === Panel 2: Activity ===
    html += '<div id="activityPanel" class="recs-subtab-panel">';
    html += '<div id="recsActivitySection"><div class="loading-overlay" style="padding:1em;"><div class="spinner"></div></div></div>';
    html += '</div>'; // end activityPanel

    container.innerHTML = html;

    // Store results globally for user switching
    window._recsResults = results;

    // Attach sub-tab click handlers via addEventListener (CSP blocks inline onclick)
    var subtabBtns = container.querySelectorAll('.recs-subtab');
    for (var s = 0; s < subtabBtns.length; s++) {
        subtabBtns[s].addEventListener('click', function () {
            switchRecsSubtab(this);
        });
    }

    // Attach change handler via addEventListener (not inline onchange — avoids race condition)
    var recsSelect = document.getElementById('recsUserSelect');
    if (recsSelect) {
        recsSelect.addEventListener('change', function () {
            renderUserRecommendations(parseInt(recsSelect.value, 10));
        });
    }

    // Render first user
    if (results.length > 0) {
        renderUserRecommendations(0);
    }

    // Load watch profiles
    loadWatchProfiles();

    // Load user activity
    loadActivityInRecsTab();
}

/**
 * Switches between Recommendations / Activity sub-tabs.
 */
function switchRecsSubtab(btn) {
    if (!btn) return;
    var panelId = btn.getAttribute('data-panel');

    // Deactivate all tabs & panels
    var tabs = btn.parentElement.querySelectorAll('.recs-subtab');
    for (var i = 0; i < tabs.length; i++) {
        tabs[i].classList.remove('active');
    }
    var panels = btn.parentElement.parentElement.querySelectorAll('.recs-subtab-panel');
    for (var p = 0; p < panels.length; p++) {
        panels[p].classList.remove('active');
    }

    // Activate clicked tab & its panel
    btn.classList.add('active');
    var panel = document.getElementById(panelId);
    if (panel) panel.classList.add('active');
}

function renderRecsSummaryCard(icon, label, value) {
    return '<div class="recs-card">'
        + '<div class="recs-card-icon">' + icon + '</div>'
        + '<div class="recs-card-value">' + escHtml(String(value)) + '</div>'
        + '<div class="recs-card-label">' + escHtml(label) + '</div>'
        + '</div>';
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
    var scorePercent = Math.round((rec.Score || 0) * 100);
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

    if (rec.Year && rec.Year > 0) {
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

function loadWatchProfiles() {
    var container = document.getElementById('recsWatchProfiles');
    if (!container) return;

    apiGet('JellyfinHelper/Recommendations/WatchProfiles', function (profiles) {
        renderWatchProfiles(container, profiles);
    }, function () {
        container.innerHTML = '<div class="recs-empty"><p>'
            + T('recsProfilesError', 'Could not load watch profiles.')
            + '</p></div>';
    });
}

function renderWatchProfiles(container, profiles) {
    if (!profiles || profiles.length === 0) {
        container.innerHTML = '<div class="recs-empty"><p>'
            + T('recsNoProfiles', 'No watch profiles available.')
            + '</p></div>';
        return;
    }

    var html = '<div class="recs-profiles-grid">';
    for (var i = 0; i < profiles.length; i++) {
        var p = profiles[i];
        html += '<div class="recs-profile-card">';
        html += '<div class="recs-profile-name">👤 ' + escHtml(p.UserName || 'Unknown') + '</div>';
        // Compute totals from actual DTO properties
        var totalWatched = (p.WatchedMovieCount || 0) + (p.WatchedEpisodeCount || 0);

        html += '<div class="recs-profile-stats">';
        html += '<div class="recs-profile-stat">'
            + '<span class="recs-profile-stat-val">' + totalWatched + '</span>'
            + '<span class="recs-profile-stat-lbl">' + T('recsWatched', 'Watched') + '</span></div>';
        html += '<div class="recs-profile-stat">'
            + '<span class="recs-profile-stat-val">' + (p.WatchedSeriesCount || 0) + '</span>'
            + '<span class="recs-profile-stat-lbl">' + T('recsSeries', 'Series') + '</span></div>';
        html += '<div class="recs-profile-stat">'
            + '<span class="recs-profile-stat-val">' + (p.FavoriteCount || 0) + '</span>'
            + '<span class="recs-profile-stat-lbl">' + T('recsFavorites', 'Favorites') + '</span></div>';

        // Top genres from GenreDistribution (sorted by count descending)
        var topGenres = getTopGenresFromDistribution(p.GenreDistribution, 5);
        if (topGenres.length > 0) {
            html += '<div class="recs-profile-genres">';
            html += '<span class="recs-profile-genres-label">' + T('recsTopGenres', 'Top Genres') + ':</span> ';
            var genreLabels = [];
            for (var g = 0; g < topGenres.length; g++) {
                genreLabels.push(escHtml(topGenres[g]));
            }
            html += genreLabels.join(', ');
            html += '</div>';
        }

        html += '</div>'; // stats
        html += '</div>'; // card
    }
    html += '</div>';

    container.innerHTML = html;
}

// --- Activity Section (integrated into Recommendations tab) ---

function loadActivityInRecsTab() {
    var container = document.getElementById('recsActivitySection');
    if (!container) return;

    apiGet('JellyfinHelper/UserActivity/Latest', function (data) {
        renderActivitySection(container, data);
    }, function () {
        container.innerHTML = '<div class="recs-empty"><p>'
            + T('activityNoData', 'No watch activity data available. Run the \'Update User Watch Activity\' scheduled task first.')
            + '</p></div>';
    });
}

function renderActivitySection(container, result) {
    if (!result || !result.Items || result.Items.length === 0) {
        container.innerHTML = '<div class="recs-empty"><p>'
            + T('activityNoData', 'No watch activity data available. Run the \'Update User Watch Activity\' scheduled task first.')
            + '</p></div>';
        return;
    }

    var html = '';

    // Summary stats row (reusing recs-summary style)
    html += '<div class="recs-summary">';
    html += renderRecsSummaryCard('🎬', T('activityTotalItems', 'Items with Activity'), result.TotalItemsWithActivity || 0);
    html += renderRecsSummaryCard('▶️', T('activityTotalPlays', 'Total Plays'), result.TotalPlayCount || 0);
    html += renderRecsSummaryCard('👥', T('activityTotalUsers', 'Users Analyzed'), result.TotalUsersAnalyzed || 0);
    html += '</div>';

    // Collect all unique users for filter
    var userNames = {};
    for (var i = 0; i < result.Items.length; i++) {
        var acts = result.Items[i].UserActivities || [];
        for (var u = 0; u < acts.length; u++) {
            if (acts[u].UserName) {
                userNames[acts[u].UserName] = true;
            }
        }
    }
    var userList = Object.keys(userNames).sort();

    if (userList.length === 0) {
        container.innerHTML = '<div class="recs-empty"><p>' + T('activityNoData', 'No data') + '</p></div>';
        return;
    }

    // User filter — no "All Users" option, default to first user
    html += '<div class="recs-user-selector">';
    html += '<label for="activityUserFilter">' + T('activityFilterUser', 'Filter by User') + ': </label>';
    html += '<select id="activityUserFilter" class="recs-select">';
    for (var n = 0; n < userList.length; n++) {
        html += '<option value="' + escHtml(userList[n]) + '">' + escHtml(userList[n]) + '</option>';
    }
    html += '</select>';
    html += '</div>';

    // Table
    html += '<div id="activityTableWrapper"></div>';

    // Generated-at timestamp
    if (result.GeneratedAt) {
        html += '<div class="activity-generated">'
            + T('activityGeneratedAt', 'Data generated') + ': '
            + new Date(result.GeneratedAt).toLocaleString()
            + '</div>';
    }

    container.innerHTML = html;

    // Store data for filtering
    window._activityItems = result.Items;

    // Attach change handler via addEventListener
    var activitySelect = document.getElementById('activityUserFilter');
    if (activitySelect) {
        activitySelect.addEventListener('change', function () {
            renderActivityTable(activitySelect.value);
        });
    }

    // Render initial table for first user
    renderActivityTable(userList[0]);
}

function renderActivityTable(filterUser) {
    var wrapper = document.getElementById('activityTableWrapper');
    if (!wrapper || !window._activityItems) return;

    var items = window._activityItems;
    var filtered = [];

    for (var i = 0; i < items.length; i++) {
        var item = items[i];
        if (filterUser) {
            // Check if this user has activity on this item
            var hasUser = false;
            var acts = item.UserActivities || [];
            for (var u = 0; u < acts.length; u++) {
                if (acts[u].UserName === filterUser) { hasUser = true; break; }
            }
            if (!hasUser) continue;
        }
        filtered.push(item);
    }

    if (filtered.length === 0) {
        wrapper.innerHTML = '<div class="recs-empty"><p>' + T('activityNoData', 'No data') + '</p></div>';
        return;
    }

    // Limit to top 50 for performance
    var maxRows = Math.min(filtered.length, 50);

    var html = '<table class="activity-table">';
    html += '<thead><tr>';
    html += '<th>' + T('activityItemName', 'Title') + '</th>';
    html += '<th>' + T('activityItemType', 'Type') + '</th>';
    html += '<th>' + T('activityPlays', 'Plays') + '</th>';
    html += '<th>' + T('activityViewers', 'Viewers') + '</th>';
    html += '<th>' + T('activityLastWatched', 'Last Watched') + '</th>';
    html += '<th>' + T('activityCompletion', 'Completion') + '</th>';
    html += '<th>' + T('activityGenres', 'Genres') + '</th>';
    html += '</tr></thead><tbody>';

    for (var r = 0; r < maxRows; r++) {
        var it = filtered[r];
        var completionPct = Math.round(it.AverageCompletionPercent || 0);
        var statusClass = completionPct >= 90 ? 'activity-status-done'
            : completionPct > 0 ? 'activity-status-progress' : 'activity-status-new';

        html += '<tr>';
        html += '<td class="activity-cell-title">' + escHtml(it.ItemName || '—') + '</td>';
        html += '<td><span class="recs-tag recs-tag-type">' + escHtml(it.ItemType || '') + '</span></td>';
        html += '<td class="activity-cell-num">' + (it.TotalPlayCount || 0) + '</td>';
        html += '<td class="activity-cell-num">' + (it.UniqueViewers || 0) + '</td>';
        html += '<td>' + (it.MostRecentWatch ? new Date(it.MostRecentWatch).toLocaleDateString() : '—') + '</td>';
        html += '<td>';
        html += '<div class="activity-completion-bar">';
        html += '<div class="activity-completion-fill ' + statusClass + '" style="width:' + completionPct + '%"></div>';
        html += '<span class="activity-completion-text">' + completionPct + '%</span>';
        html += '</div>';
        html += '</td>';

        var genres = (it.Genres || []).slice(0, 3);
        html += '<td>';
        for (var g = 0; g < genres.length; g++) {
            html += '<span class="recs-tag">' + escHtml(genres[g]) + '</span> ';
        }
        html += '</td>';

        html += '</tr>';
    }

    html += '</tbody></table>';

    if (filtered.length > maxRows) {
        html += '<div class="activity-more">' + T('andMore', 'and') + ' ' + (filtered.length - maxRows) + ' ' + T('more', 'more') + '…</div>';
    }

    wrapper.innerHTML = html;
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