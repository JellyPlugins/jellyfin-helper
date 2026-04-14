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
        var segments = [
            { cls: 'bar-video', pct: (videoTotal / total * 100), label: T('video', 'Video') },
            { cls: 'bar-audio', pct: (data.TotalMusicAudioSize / total * 100), label: T('audio', 'Audio') },
            { cls: 'bar-subtitle', pct: (data.TotalSubtitleSize / total * 100), label: T('subtitles', 'Subtitles') },
            { cls: 'bar-image', pct: (data.TotalImageSize / total * 100), label: T('images', 'Images') },
            { cls: 'bar-trickplay', pct: (data.TotalTrickplaySize / total * 100), label: T('trickplay', 'Trickplay') },
            { cls: 'bar-nfo', pct: (data.TotalNfoSize / total * 100), label: T('metadata', 'Metadata') },
            { cls: 'bar-other', pct: (otherSize / total * 100), label: T('other', 'Other') }
        ];

        var barHtml = '<div class="total-bar">';
        for (var s = 0; s < segments.length; s++) {
            if (segments[s].pct > 0) {
                barHtml += '<div class="bar-segment ' + segments[s].cls + '" style="width:' + segments[s].pct.toFixed(2) + '%" title="' + escAttr(segments[s].label) + '"></div>';
            }
        }
        barHtml += '</div>';

        barHtml += '<div class="legend">';
        var legendItems = [
            { cls: 'bar-video', label: T('video', 'Video') + ' (' + formatBytes(videoTotal) + ')' },
            { cls: 'bar-audio', label: T('audio', 'Audio') + ' (' + formatBytes(data.TotalMusicAudioSize) + ')' },
            { cls: 'bar-subtitle', label: T('subtitles', 'Subtitles') + ' (' + formatBytes(data.TotalSubtitleSize) + ')' },
            { cls: 'bar-image', label: T('images', 'Images') + ' (' + formatBytes(data.TotalImageSize) + ')' },
            { cls: 'bar-trickplay', label: T('trickplay', 'Trickplay') + ' (' + formatBytes(data.TotalTrickplaySize) + ')' },
            { cls: 'bar-nfo', label: T('metadata', 'Metadata') + ' (' + formatBytes(data.TotalNfoSize) + ')' },
            { cls: 'bar-other', label: T('other', 'Other') + ' (' + formatBytes(otherSize) + ')' }
        ];
        for (var l = 0; l < legendItems.length; l++) {
            barHtml += '<div class="legend-item"><div class="legend-dot ' + legendItems[l].cls + '"></div>' + legendItems[l].label + '</div>';
        }
        barHtml += '</div>';

        return barHtml;
    }

    function loadCleanupStats() {
        var apiClient = ApiClient;
        apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/CleanupStatistics'), dataType: 'json' }).then(function (stats) {
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
        overviewHtml += '<div class="section-title">⛃ ' + T('storageDistribution', 'Storage Distribution') + ' — <span style="color:#00a4dc;">' + formatBytes(grandTotal) + ' ' + T('total', 'Total') + '</span></div>';
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
