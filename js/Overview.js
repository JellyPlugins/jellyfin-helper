// --- Overview Tab ---
'use strict';
function getCollectionBadge(type) {
    var t = (type || '').toLowerCase();
    if (t === 'tvshows') return '<span class="badge badge-tvshows">' + escHtml(T('tvShows', 'TV Shows')) + '</span>';
    if (t === 'movies' || t === '') return '<span class="badge badge-movies">' + escHtml(T('movies', 'Movies')) + '</span>';
    if (t === 'music') return '<span class="badge badge-music">' + escHtml(T('music', 'Music')) + '</span>';
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
        var h = '<div class="section-title">' + mi('cleaning_services') + escHtml(T('cleanupStatistics', 'Cleanup Statistics')) + '</div>';
        h += '<div class="stats-grid">';
        h += '<div class="stat-card highlight"><h3>' + escHtml(T('totalBytesFreed', 'Total Space Freed')) + '</h3>';
        h += '<p class="stat-value">' + escHtml(formatBytes(stats.TotalBytesFreed)) + '</p></div>';
        h += '<div class="stat-card highlight"><h3>' + escHtml(T('totalItemsDeleted', 'Total Items Deleted')) + '</h3>';
        h += '<p class="stat-value">' + escHtml(String(stats.TotalItemsDeleted)) + '</p>';
        var parsedTs = new Date(stats.LastCleanupTimestamp);
        var hasValidTs = stats.LastCleanupTimestamp &&
            stats.LastCleanupTimestamp !== '0001-01-01T00:00:00' &&
            !Number.isNaN(parsedTs.getTime());
        var lastTs = hasValidTs ? parsedTs.toLocaleString() : T('never', 'Never');
        h += '<p class="stat-detail">' + escHtml(T('lastCleanup', 'Last cleanup')) + ': ' + escHtml(lastTs) + '</p></div>';
        h += '</div>';
        cleanupContainer.innerHTML = h;
    }, function () {
        var cleanupContainer = document.getElementById('cleanup-stats-container');
        if (cleanupContainer) {
            cleanupContainer.innerHTML = '<div class="section-title">' + mi('cleaning_services') + escHtml(T('cleanupStatistics', 'Cleanup Statistics')) + '</div>' +
                '<p style="opacity:0.5;">' + escHtml(T('cleanupStatsError', 'Could not load cleanup statistics.')) + '</p>';
        }
    });
}

function fillOverviewData(data) {
    var movies = (data.Movies && Array.isArray(data.Movies)) ? data.Movies : [];
    var tvShows = (data.TvShows && Array.isArray(data.TvShows)) ? data.TvShows : [];
    var libraries = (data.Libraries && Array.isArray(data.Libraries)) ? data.Libraries : [];
    var totalVideoFileCount = data.TotalVideoFileCount || 0;
    var totalAudioFileCount = data.TotalAudioFileCount || 0;

    var overviewHtml = '';
    overviewHtml += '<div class="stats-grid">';
    overviewHtml += '<div class="stat-card"><h3>' + mi('movie') + escHtml(T('movieVideoData', 'Video Data - Movies')) + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalMovieVideoSize) + '</p>';
    var movieFiles = 0;
    for (var m = 0; m < movies.length; m++) movieFiles += movies[m].VideoFileCount;
    overviewHtml += '<p class="stat-detail">' + movieFiles + ' ' + (movieFiles === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files'))) + ' ' + escHtml(T('across', 'across')) + ' ' + movies.length + ' ' + escHtml(T('libraries', 'libraries')) + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>' + mi('tv') + escHtml(T('tvVideoData', 'Video Data - TV Shows')) + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalTvShowVideoSize) + '</p>';
    var tvFiles = 0;
    for (var t = 0; t < tvShows.length; t++) tvFiles += tvShows[t].VideoFileCount;
    overviewHtml += '<p class="stat-detail">' + tvFiles + ' ' + (tvFiles === 1 ? escHtml(T('episode', 'episode')) : escHtml(T('episodes', 'episodes'))) + ' ' + escHtml(T('across', 'across')) + ' ' + tvShows.length + ' ' + escHtml(T('libraries', 'libraries')) + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>' + mi('music_note') + escHtml(T('musicAudioData', 'Music / Audio')) + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalMusicAudioSize) + '</p>';
    overviewHtml += '<p class="stat-detail">' + totalAudioFileCount + ' ' + (totalAudioFileCount === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files'))) + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>' + mi('image') + escHtml(T('trickplayData', 'Trickplay Data')) + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalTrickplaySize) + '</p>';
    var trickplayFolders = 0;
    for (var tp = 0; tp < libraries.length; tp++) trickplayFolders += libraries[tp].TrickplayFolderCount;
    overviewHtml += '<p class="stat-detail">' + trickplayFolders + ' ' + (trickplayFolders === 1 ? escHtml(T('folder', 'folder')) : escHtml(T('folders', 'folders'))) + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>' + mi('edit_note') + escHtml(T('subtitleData', 'Subtitles')) + '</h3>';
    overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalSubtitleSize) + '</p>';
    var subFiles = 0;
    for (var sb = 0; sb < libraries.length; sb++) subFiles += libraries[sb].SubtitleFileCount;
    overviewHtml += '<p class="stat-detail">' + subFiles + ' ' + (subFiles === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files'))) + '</p>';
    overviewHtml += '</div>';

    overviewHtml += '<div class="stat-card"><h3>' + mi('bar_chart') + escHtml(T('totalFiles', 'Total Files')) + '</h3>';
    var totalMediaFiles = totalVideoFileCount + totalAudioFileCount;
    overviewHtml += '<p class="stat-value">' + totalMediaFiles + ' ' + (totalMediaFiles === 1 ? escHtml(T('mediaFile', 'media file')) : escHtml(T('mediaFiles', 'media files'))) + '</p>';
    overviewHtml += '<p class="stat-detail">' + totalVideoFileCount + ' ' + escHtml(T('video', 'video')) + ', ' + totalAudioFileCount + ' ' + escHtml(T('audio', 'audio')) + '</p>';
    overviewHtml += '</div>';
    overviewHtml += '</div>';

    var grandTotal = 0;
    for (var gt = 0; gt < libraries.length; gt++) grandTotal += libraries[gt].TotalSize;
    overviewHtml += '<div class="section-title">' + mi('storage') + escHtml(T('storageDistribution', 'Storage Distribution')) + ' - <span class="color-primary">' + formatBytes(grandTotal) + ' ' + escHtml(T('total', 'Total')) + '</span></div>';
    overviewHtml += buildBarSegments(data);

    overviewHtml += '<div class="section-title">' + mi('library_books') + escHtml(T('perLibraryBreakdown', 'Per-Library Breakdown')) + '</div>';
    overviewHtml += '<div class="library-table-wrapper"><table class="library-table">';
    overviewHtml += '<thead><tr>';
    overviewHtml += '<th>' + escHtml(T('library', 'Library')) + '</th><th>' + escHtml(T('type', 'Type')) + '</th><th>' + escHtml(T('video', 'Video')) + '</th><th>' + escHtml(T('audio', 'Audio')) + '</th><th>' + escHtml(T('subtitles', 'Subtitles')) + '</th><th>' + escHtml(T('images', 'Images')) + '</th><th>' + escHtml(T('trickplay', 'Trickplay')) + '</th><th>' + escHtml(T('total', 'Total')) + '</th>';
    overviewHtml += '</tr></thead><tbody>';

    for (var i = 0; i < libraries.length; i++) {
        var lib = libraries[i];
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
