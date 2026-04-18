// --- Overview Tab ---
function getCollectionBadge(type) {
    var t = (type || '').toLowerCase();
    if (t === 'tvshows') return '<span class="badge badge-tvshows">' + T('tvShows', 'TV Shows') + '</span>';
    if (t === 'movies' || t === '') return '<span class="badge badge-movies">' + T('movies', 'Movies') + '</span>';
    if (t === 'music') return '<span class="badge badge-music">' + T('music', 'Music') + '</span>';
    return '<span class="badge badge-other">' + escHtml(type || T('mixed', 'Mixed')) + '</span>';
}

function buildBarSegments(data) {
    var total = data.TotalMovieVideoSize + data.TotalTvShowVideoSize +
        data.TotalSubtitleSize + data.TotalImageSize + data.TotalTrickplaySize +
        data.TotalNfoSize + data.TotalMusicAudioSize;

    var otherSize = 0;
    for (var i = 0; i < data.Libraries.length; i++) {
        otherSize += data.Libraries[i].OtherSize;
        total += data.Libraries[i].OtherSize;
    }

    if (total === 0) return '';

    var videoTotal = data.TotalMovieVideoSize + data.TotalTvShowVideoSize;
    var categories = [
        {cls: 'bar-video', bytes: videoTotal, labelKey: 'video', labelFallback: 'Video'},
        {cls: 'bar-audio', bytes: data.TotalMusicAudioSize, labelKey: 'audio', labelFallback: 'Audio'},
        {cls: 'bar-subtitle', bytes: data.TotalSubtitleSize, labelKey: 'subtitles', labelFallback: 'Subtitles'},
        {cls: 'bar-image', bytes: data.TotalImageSize, labelKey: 'images', labelFallback: 'Images'},
        {cls: 'bar-trickplay', bytes: data.TotalTrickplaySize, labelKey: 'trickplay', labelFallback: 'Trickplay'},
        {cls: 'bar-nfo', bytes: data.TotalNfoSize, labelKey: 'metadata', labelFallback: 'Metadata'},
        {cls: 'bar-other', bytes: otherSize, labelKey: 'other', labelFallback: 'Other'}
    ];

    var barHtml = '<div class="total-bar">';
    for (var s = 0; s < categories.length; s++) {
        var pct = categories[s].bytes / total * 100;
        if (pct > 0) {
            barHtml += '<div class="bar-segment ' + categories[s].cls + '" style="width:' + pct.toFixed(2) + '%" title="' + escAttr(T(categories[s].labelKey, categories[s].labelFallback)) + '"></div>';
        }
    }
    barHtml += '</div>';

    barHtml += '<div class="legend">';
    for (var l = 0; l < categories.length; l++) {
        var label = T(categories[l].labelKey, categories[l].labelFallback) + ' (' + formatBytes(categories[l].bytes) + ')';
        barHtml += '<div class="legend-item"><div class="legend-dot ' + categories[l].cls + '"></div>' + label + '</div>';
    }
    barHtml += '</div>';

    return barHtml;
}

function loadCleanupStats() {
    apiGet('JellyfinHelper/CleanupStatistics', function (stats) {
        var cleanupContainer = document.getElementById('cleanup-stats-container');
        if (!cleanupContainer) return;
        var h = '<div class="section-title">🧹 ' + T('cleanupStatistics', 'Cleanup Statistics') + '</div>';
        h += '<div class="stats-grid">';
        h += '<div class="stat-card highlight"><h3>' + T('totalBytesFreed', 'Total Space Freed') + '</h3>';
        h += '<p class="stat-value">' + formatBytes(stats.TotalBytesFreed) + '</p></div>';
        h += '<div class="stat-card highlight"><h3>' + T('totalItemsDeleted', 'Total Items Deleted') + '</h3>';
        h += '<p class="stat-value">' + stats.TotalItemsDeleted + '</p>';
        var lastTs = stats.LastCleanupTimestamp && stats.LastCleanupTimestamp !== '0001-01-01T00:00:00' ? new Date(stats.LastCleanupTimestamp).toLocaleString() : T('never', 'Never');
        h += '<p class="stat-detail">' + T('lastCleanup', 'Last cleanup') + ': ' + lastTs + '</p></div>';
        h += '</div>';
        cleanupContainer.innerHTML = h;
    }, function () {
        var cleanupContainer = document.getElementById('cleanup-stats-container');
        if (cleanupContainer) {
            cleanupContainer.innerHTML = '<div class="section-title">🧹 ' + T('cleanupStatistics', 'Cleanup Statistics') + '</div>' +
                '<p style="opacity:0.5;">' + T('cleanupStatsError', 'Could not load cleanup statistics.') + '</p>';
        }
    });
}

function fillOverviewData(data) {
    var overviewHtml = '';
    overviewHtml += '<div class="stats-grid">';
    overviewHtml += '<div class="stat-card"><h3>🎬 ' + T('movieVideoData', 'Video Data — Movies') + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalMovieVideoSize) + '</p>';
    var movieFiles = 0;
    for (var m = 0; m < data.Movies.length; m++) movieFiles += data.Movies[m].VideoFileCount;
    overviewHtml += '<p class="stat-detail">' + movieFiles + ' ' + (movieFiles === 1 ? T('file', 'file') : T('files', 'files')) + ' ' + T('across', 'across') + ' ' + data.Movies.length + ' ' + T('libraries', 'libraries') + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>📺 ' + T('tvVideoData', 'Video Data — TV Shows') + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalTvShowVideoSize) + '</p>';
    var tvFiles = 0;
    for (var t = 0; t < data.TvShows.length; t++) tvFiles += data.TvShows[t].VideoFileCount;
    overviewHtml += '<p class="stat-detail">' + tvFiles + ' ' + (tvFiles === 1 ? T('episode', 'episode') : T('episodes', 'episodes')) + ' ' + T('across', 'across') + ' ' + data.TvShows.length + ' ' + T('libraries', 'libraries') + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>🎵 ' + T('musicAudioData', 'Music / Audio') + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalMusicAudioSize) + '</p>';
    overviewHtml += '<p class="stat-detail">' + data.TotalAudioFileCount + ' ' + (data.TotalAudioFileCount === 1 ? T('file', 'file') : T('files', 'files')) + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>🖼️ ' + T('trickplayData', 'Trickplay Data') + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalTrickplaySize) + '</p>';
    var trickplayFolders = 0;
    for (var tp = 0; tp < data.Libraries.length; tp++) trickplayFolders += data.Libraries[tp].TrickplayFolderCount;
    overviewHtml += '<p class="stat-detail">' + trickplayFolders + ' ' + (trickplayFolders === 1 ? T('folder', 'folder') : T('folders', 'folders')) + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>📝 ' + T('subtitleData', 'Subtitles') + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalSubtitleSize) + '</p>';
    var subFiles = 0;
    for (var sb = 0; sb < data.Libraries.length; sb++) subFiles += data.Libraries[sb].SubtitleFileCount;
    overviewHtml += '<p class="stat-detail">' + subFiles + ' ' + (subFiles === 1 ? T('file', 'file') : T('files', 'files')) + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>📊 ' + T('totalFiles', 'Total Files') + '</h3>';
    var totalMediaFiles = data.TotalVideoFileCount + data.TotalAudioFileCount;
    overviewHtml += '<p class="stat-value">' + totalMediaFiles + ' ' + (totalMediaFiles === 1 ? T('mediaFile', 'media file') : T('mediaFiles', 'media files')) + '</p>';
    overviewHtml += '<p class="stat-detail">' + data.TotalVideoFileCount + ' ' + T('video', 'video') + ', ' + data.TotalAudioFileCount + ' ' + T('audio', 'audio') + '</p>';
    overviewHtml += '</div>';
    overviewHtml += '</div>';

    var grandTotal = 0;
    for (var gt = 0; gt < data.Libraries.length; gt++) grandTotal += data.Libraries[gt].TotalSize;
    overviewHtml += '<div class="section-title">⛃ ' + T('storageDistribution', 'Storage Distribution') + ' — <span class="color-primary">' + formatBytes(grandTotal) + ' ' + T('total', 'Total') + '</span></div>';
    overviewHtml += buildBarSegments(data);

    overviewHtml += '<div class="section-title">📚 ' + T('perLibraryBreakdown', 'Per-Library Breakdown') + '</div>';
    overviewHtml += '<div class="library-table-wrapper"><table class="library-table">';
    overviewHtml += '<thead><tr>';
    overviewHtml += '<th>' + T('library', 'Library') + '</th><th>' + T('type', 'Type') + '</th><th>' + T('video', 'Video') + '</th><th>' + T('audio', 'Audio') + '</th><th>' + T('subtitles', 'Subtitles') + '</th><th>' + T('images', 'Images') + '</th><th>' + T('trickplay', 'Trickplay') + '</th><th>' + T('total', 'Total') + '</th>';
    overviewHtml += '</tr></thead><tbody>';

    for (var i = 0; i < data.Libraries.length; i++) {
        var lib = data.Libraries[i];
        overviewHtml += '<tr>';
        overviewHtml += '<td>' + escHtml(lib.LibraryName) + '</td>';
        overviewHtml += '<td>' + getCollectionBadge(lib.CollectionType) + '</td>';
        overviewHtml += '<td>' + formatBytes(lib.VideoSize) + '</td>';
        overviewHtml += '<td>' + formatBytes(lib.AudioSize) + '</td>';
        overviewHtml += '<td>' + formatBytes(lib.SubtitleSize) + '</td>';
        overviewHtml += '<td>' + formatBytes(lib.ImageSize) + '</td>';
        overviewHtml += '<td>' + formatBytes(lib.TrickplaySize) + '</td>';
        overviewHtml += '<td><strong>' + formatBytes(lib.TotalSize) + '</strong></td>';
        overviewHtml += '</tr>';
    }

    overviewHtml += '</tbody></table></div>';

    overviewHtml += '<div id="cleanup-stats-container"></div>';

    var overviewContainer = document.getElementById('overviewContent');
    if (overviewContainer) {
        overviewContainer.innerHTML = overviewHtml;
    }
}
