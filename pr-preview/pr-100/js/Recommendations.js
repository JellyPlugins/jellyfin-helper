// --- Recommendations Tab (Smart Suggestions) ---
var _profileReqId = 0;
var _activityReqId = 0;

function initRecommendationsTab() { loadRecommendations(); }

function loadRecommendations() {
    var container = document.getElementById('recsContent');
    if (!container) return;
    container.innerHTML = '<div class="loading-overlay" style="padding:2em;"><div class="spinner"></div><p>' + T('loadingRecommendations', 'Loading recommendations…') + '</p></div>';
    apiGet('JellyfinHelper/Recommendations', function (data) {
        renderRecommendations(container, data);
    }, function (err) {
        container.innerHTML = '<div class="error-msg">❌ ' + T('recsError', 'Failed to load recommendations. Make sure the recommendation task has run at least once.') + '</div>';
        console.error('Jellyfin Helper: Error loading recommendations', err);
    });
}

function renderRecommendations(container, results) {
    if (!results || results.length === 0) {
        container.innerHTML = '<div class="recs-empty"><div class="recs-empty-icon">🤖</div><p>' + T('recsEmpty', 'No recommendations available yet. Run the "Helper Cleanup" scheduled task first.') + '</p></div>';
        return;
    }
    var html = '';
    var totalRecs = 0, totalUsers = results.length;
    for (var i = 0; i < results.length; i++) { totalRecs += results[i].Recommendations ? results[i].Recommendations.length : 0; }
    html += '<div class="recs-info-line"><span>👥 ' + totalUsers + ' ' + T('recsUsers', 'Users') + '</span><span class="recs-info-sep">·</span><span>🎯 ' + totalRecs + ' ' + T('recsTotal', 'Recommendations') + '</span></div>';
    html += '<div class="recs-user-selector"><label for="recsUserSelect">' + T('recsSelectUser', 'Select User') + ': </label><select id="recsUserSelect" class="recs-select">';
    for (var u = 0; u < results.length; u++) {
        html += '<option value="' + u + '">' + escHtml(results[u].UserName) + ' (' + (results[u].Recommendations ? results[u].Recommendations.length : 0) + ' ' + T('recsItems', 'items') + ')</option>';
    }
    html += '</select></div>';
    html += '<div id="recsUserGrid"></div>';
    html += '<div class="recs-collapsible"><button class="recs-collapsible-toggle" id="recsActivityToggle"><span class="recs-collapsible-arrow">▶</span> 📊 ' + T('recsActivityToggle', 'Watch Activity') + '</button>';
    html += '<div class="recs-collapsible-body" id="recsActivityBody">';
    html += '<div id="recsUserProfile"><div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div></div>';
    html += '<div id="recsUserActivity"><div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div></div>';
    html += '</div></div>';
    container.innerHTML = html;
    window._recsResults = results;
    var recsSelect = document.getElementById('recsUserSelect');
    if (recsSelect) { recsSelect.addEventListener('change', function () { onUserChanged(parseInt(recsSelect.value, 10)); }); }
    var toggleBtn = document.getElementById('recsActivityToggle');
    if (toggleBtn) { toggleBtn.addEventListener('click', function () { toggleActivityCollapsible(); }); }
    if (results.length > 0) { onUserChanged(0); }
}

function onUserChanged(index) {
    renderUserRecommendations(index);
    var body = document.getElementById('recsActivityBody');
    var arrow = document.querySelector('.recs-collapsible-arrow');
    if (body) body.classList.remove('open');
    if (arrow) arrow.textContent = '▶';
    loadUserWatchProfile(index);
    loadUserActivity(index);
}

function toggleActivityCollapsible() {
    var body = document.getElementById('recsActivityBody');
    var arrow = document.querySelector('.recs-collapsible-arrow');
    if (!body) return;
    if (body.classList.contains('open')) { body.classList.remove('open'); if (arrow) arrow.textContent = '▶'; }
    else { body.classList.add('open'); if (arrow) arrow.textContent = '▼'; }
}

function renderUserRecommendations(index) {
    var grid = document.getElementById('recsUserGrid');
    if (!grid || !window._recsResults) return;
    var result = window._recsResults[index];
    if (!result) return;
    var recs = result.Recommendations || [];
    if (recs.length === 0) { grid.innerHTML = '<div class="recs-empty"><p>' + T('recsNoItems', 'No recommendations for this user yet. More watch history is needed.') + '</p></div>'; return; }
    var html = '<div class="recs-grid">';
    for (var i = 0; i < recs.length; i++) { html += renderRecommendationCard(recs[i], i + 1); }
    html += '</div>';
    grid.innerHTML = html;
}

function renderRecommendationCard(rec, rank) {
    var scorePercent = Math.max(0, Math.min(100, Math.round((Number(rec.Score) || 0) * 100)));
    var scoreClass = scorePercent >= 80 ? 'recs-score-high' : scorePercent >= 50 ? 'recs-score-mid' : 'recs-score-low';
    var html = '<div class="recs-item"><div class="recs-item-rank">#' + rank + '</div><div class="recs-item-body">';
    html += '<div class="recs-item-title">' + escHtml(rec.Name || 'Unknown') + '</div><div class="recs-item-meta">';
    if (rec.ItemType) { html += '<span class="recs-tag recs-tag-type">' + escHtml(rec.ItemType) + '</span>'; }
    if (rec.Genres && rec.Genres.length > 0) { for (var g = 0; g < Math.min(rec.Genres.length, 3); g++) { html += '<span class="recs-tag">' + escHtml(rec.Genres[g]) + '</span>'; } }
    if (typeof rec.Year === 'number' && rec.Year > 0) { html += '<span class="recs-tag recs-tag-year">' + rec.Year + '</span>'; }
    html += '</div>';
    html += '<div class="recs-item-reason"><span class="recs-reason-label">' + T('recsReason', 'Why') + ':</span> ';
    var reasonText = rec.ReasonKey ? T(rec.ReasonKey, rec.Reason || '') : (rec.Reason || T('recsReasonGeneric', 'Based on your viewing history'));
    if (rec.RelatedItemName && reasonText.indexOf('{0}') !== -1) { reasonText = reasonText.split('{0}').join(rec.RelatedItemName); }
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
    container.innerHTML = '<div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div>';
    var reqId = ++_profileReqId;
    apiGet('JellyfinHelper/Recommendations/WatchProfile/' + result.UserId, function (profile) {
        if (reqId !== _profileReqId) return;
        renderCompactWatchProfile(container, profile);
    }, function () {
        if (reqId !== _profileReqId) return;
        container.innerHTML = '<div class="recs-profile-compact-empty">' + T('recsNoProfiles', 'No watch profile available.') + '</div>';
    });
}

function renderCompactWatchProfile(container, profile) {
    if (!profile) { container.innerHTML = '<div class="recs-profile-compact-empty">' + T('recsNoProfiles', 'No watch profile available.') + '</div>'; return; }
    var totalWatched = (profile.WatchedMovieCount || 0) + (profile.WatchedEpisodeCount || 0);
    var topGenres = getTopGenresFromDistribution(profile.GenreDistribution, 5);
    var html = '<div class="recs-profile-compact"><div class="recs-profile-compact-stats">';
    html += '<span class="recs-profile-compact-stat">🎬 ' + totalWatched + ' ' + T('recsWatched', 'Watched') + '</span>';
    html += '<span class="recs-profile-compact-stat">📺 ' + (profile.WatchedSeriesCount || 0) + ' ' + T('recsSeries', 'Series') + '</span>';
    html += '<span class="recs-profile-compact-stat">⭐ ' + (profile.FavoriteCount || 0) + ' ' + T('recsFavorites', 'Favorites') + '</span></div>';
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
    container.innerHTML = '<div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div>';
    var reqId = ++_activityReqId;
    apiGet('JellyfinHelper/UserActivity/User/' + result.UserId, function (items) {
        if (reqId !== _activityReqId) return;
        renderCompactActivityTable(container, items);
    }, function () {
        if (reqId !== _activityReqId) return;
        container.innerHTML = '<div class="recs-profile-compact-empty">' + T('activityNoData', 'No watch activity data available.') + '</div>';
    });
}

function renderCompactActivityTable(container, items) {
    if (!items || items.length === 0) { container.innerHTML = '<div class="recs-profile-compact-empty">' + T('activityNoData', 'No watch activity data available.') + '</div>'; return; }
    var maxRows = Math.min(items.length, 15);
    var html = '<div class="recs-activity-section-title">' + T('recsRecentActivity', 'Recent Activity') + '</div>';
    html += '<table class="activity-table"><thead><tr>';
    html += '<th>' + T('activityItemName', 'Title') + '</th>';
    html += '<th>' + T('activityItemType', 'Type') + '</th>';
    html += '<th>' + T('activityPlays', 'Plays') + '</th>';
    html += '<th>' + T('activityLastWatched', 'Last Watched') + '</th>';
    html += '<th>' + T('activityCompletion', 'Completion') + '</th>';
    html += '</tr></thead><tbody>';
    for (var r = 0; r < maxRows; r++) {
        var it = items[r];
        var pct = Math.max(0, Math.min(100, Math.round(Number(it.AverageCompletionPercent) || 0)));
        var sc = pct >= 90 ? 'activity-status-done' : pct > 0 ? 'activity-status-progress' : 'activity-status-new';
        var dn = it.ItemName || '\u2014';
        if (it.SeriesName) { dn = it.SeriesName; if (it.EpisodeLabel) { dn += ' \u2013 ' + it.EpisodeLabel; } }
        html += '<tr><td class="activity-cell-title">' + escHtml(dn) + '</td>';
        html += '<td><span class="recs-tag recs-tag-type">' + escHtml(it.ItemType || '') + '</span></td>';
        html += '<td class="activity-cell-num">' + (it.TotalPlayCount || 0) + '</td>';
        html += '<td>' + (it.MostRecentWatch ? new Date(it.MostRecentWatch).toLocaleDateString() : '\u2014') + '</td>';
        html += '<td><div class="activity-completion-bar"><div class="activity-completion-fill ' + sc + '" style="width:' + pct + '%"></div>';
        html += '<span class="activity-completion-text">' + pct + '%</span></div></td></tr>';
    }
    html += '</tbody></table>';
    if (items.length > maxRows) { html += '<div class="activity-more">' + T('andMore', 'and') + ' ' + (items.length - maxRows) + ' ' + T('more', 'more') + '\u2026</div>'; }
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
