'use strict';

var _lastStatisticsData = null;
var _filterState = { activeFilters: {}, libraryType: 'all', resultOffset: 0, innerTab: 'filters' };
var _statsCleanupCache = null;

var STATISTICS_PAGE_SIZE = 50;
var STATISTICS_MOBILE_BREAKPOINT = 640;

var STATISTICS_PATH_MAP = {
    'resolutions': 'ResolutionPaths',
    'videoCodecs': 'VideoCodecPaths',
    'videoAudioCodecs': 'VideoAudioCodecPaths',
    'musicAudioCodecs': 'MusicAudioCodecPaths',
    'bookFormats': 'BookFormatPaths',
    'containers': 'ContainerFormatPaths',
    'dynamicRanges': 'DynamicRangePaths',
    'videoBitrate': 'VideoBitrateTierPaths',
    'audioLanguages': 'AudioLanguagePaths',
    'subtitleLanguages': 'SubtitleLanguagePaths',
    'watched': 'WatchedTierPaths',
    'watchedByUser': 'WatchedByUserPaths'
};

var STATISTICS_CATEGORY_MAP = {
    'resolutions': { movies: true, tvShows: true, music: false, books: false, other: true },
    'videoCodecs': { movies: true, tvShows: true, music: false, books: false, other: true },
    'videoAudioCodecs': { movies: true, tvShows: true, music: false, books: false, other: true },
    'dynamicRanges': { movies: true, tvShows: true, music: false, books: false, other: true },
    'videoBitrate': { movies: true, tvShows: true, music: false, books: false, other: true },
    'audioLanguages': { movies: true, tvShows: true, music: false, books: false, other: true },
    'subtitleLanguages': { movies: true, tvShows: true, music: false, books: false, other: true },
    'watched': { movies: true, tvShows: true, music: false, books: false, other: true },
    'watchedByUser': { movies: true, tvShows: true, music: false, books: false, other: true },
    'containers': { movies: true, tvShows: true, music: true, books: true, other: true },
    'musicAudioCodecs': { movies: false, tvShows: false, music: true, books: false, other: false },
    'bookFormats': { movies: false, tvShows: false, music: false, books: true, other: false }
};

var STATISTICS_DIMENSIONS = [
    { id: 'resolutions', labelKey: 'resolutions', fallback: 'Resolution', icon: 'straighten' },
    { id: 'videoCodecs', labelKey: 'videoCodecs', fallback: 'Video Codec', icon: 'movie' },
    { id: 'videoAudioCodecs', labelKey: 'videoAudioCodecs', fallback: 'Audio Codec', icon: 'volume_up' },
    { id: 'videoBitrate', labelKey: 'videoBitrate', fallback: 'Bitrate', icon: 'speed' },
    { id: 'dynamicRanges', labelKey: 'dynamicRange', fallback: 'Dynamic Range', icon: 'palette' },
    { id: 'audioLanguages', labelKey: 'statDonutAudioLanguage', fallback: 'Audio Languages', icon: 'record_voice_over' },
    { id: 'subtitleLanguages', labelKey: 'statDonutSubtitleLanguage', fallback: 'Subtitle Languages', icon: 'subtitles' },
    { id: 'watched', labelKey: 'activityWatched', fallback: 'Watched', icon: 'visibility' },
    { id: 'containers', labelKey: 'containerFormats', fallback: 'Container', icon: 'inventory_2' },
    { id: 'musicAudioCodecs', labelKey: 'musicAudioCodecs', fallback: 'Music Audio Codec', icon: 'music_note' },
    { id: 'bookFormats', labelKey: 'bookFormats', fallback: 'Book Format', icon: 'library_books' }
];

var _statDonutTooltipData = {};

function statPolarToCartesian(cx, cy, radius, angleRad) {
    return { x: cx + radius * Math.cos(angleRad), y: cy + radius * Math.sin(angleRad) };
}

function statDescribeArc(cx, cy, outerR, innerR, startAngle, endAngle) {
    var arcSpan = endAngle - startAngle;
    if (arcSpan >= 2 * Math.PI - 0.0001) {
        var mid = startAngle + Math.PI;
        return statDescribeArc(cx, cy, outerR, innerR, startAngle, mid) + ' ' + statDescribeArc(cx, cy, outerR, innerR, mid, endAngle);
    }
    var largeArc = arcSpan > Math.PI ? 1 : 0;
    var oStart = statPolarToCartesian(cx, cy, outerR, startAngle);
    var oEnd = statPolarToCartesian(cx, cy, outerR, endAngle);
    var iStart = statPolarToCartesian(cx, cy, innerR, startAngle);
    var iEnd = statPolarToCartesian(cx, cy, innerR, endAngle);
    return 'M ' + oStart.x.toFixed(3) + ' ' + oStart.y.toFixed(3) + ' A ' + outerR.toFixed(3) + ' ' + outerR.toFixed(3) + ' 0 ' + largeArc + ' 1 ' + oEnd.x.toFixed(3) + ' ' + oEnd.y.toFixed(3) + ' L ' + iEnd.x.toFixed(3) + ' ' + iEnd.y.toFixed(3) + ' A ' + innerR.toFixed(3) + ' ' + innerR.toFixed(3) + ' 0 ' + largeArc + ' 0 ' + iStart.x.toFixed(3) + ' ' + iStart.y.toFixed(3) + ' Z';
}

function renderDonutSvg(data, libraries, libraryProperty, chartId) {
    var size = 160;
    var entries = [];
    var total = 0;
    for (var key in data) {
        if (Object.hasOwn(data, key) && data[key] > 0) {
            entries.push({ label: key, value: data[key] });
            total += data[key];
        }
    }
    if (total === 0) return '<p style="opacity:0.5;">' + escHtml(T('noData', 'No data')) + '</p>';
    entries.sort(function (a, b) { return b.value - a.value; });
    var cx = size / 2, cy = size / 2, r = size * 0.38, strokeWidth = size * 0.18;
    var outerR = r + strokeWidth / 2;
    var innerR = r - strokeWidth / 2;
    var startAngle = -Math.PI / 2;
    var donutContainer = '<div class="donut-container"><div class="donut-tooltip" aria-hidden="true"></div><svg class="donut-svg" width="' + size + '" height="' + size + '" viewBox="0 0 ' + size + ' ' + size + '"><circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="none" stroke="rgba(255,255,255,0.05)" stroke-width="' + strokeWidth + '"/>';
    for (var i = 0; i < entries.length; i++) {
        var pct = entries[i].value / total;
        var sweepAngle = pct * 2 * Math.PI;
        var endAngle = startAngle + sweepAngle;
        var color = (typeof DONUT_COLORS !== 'undefined' ? DONUT_COLORS[i % DONUT_COLORS.length] : '#00a4dc');
        var segId = chartId + '_' + i;
        var libEntries = [];
        if (libraries && libraries.length > 0) {
            for (var l = 0; l < libraries.length; l++) {
                var lib = libraries[l];
                var libProp = lib[libraryProperty];
                var c = libProp ? libProp[entries[i].label] : 0;
                if (c > 0) libEntries.push({ name: lib.LibraryName, count: c });
            }
        }
        libEntries.sort(function (a, b) { return b.count - a.count; });
        _statDonutTooltipData[segId] = { codec: entries[i].label, totalCount: entries[i].value, totalPct: (pct * 100).toFixed(1), libraries: libEntries };
        var arcPath = statDescribeArc(cx, cy, outerR, innerR, startAngle, endAngle);
        donutContainer += '<g class="donut-segment" data-segment-id="' + escAttr(segId) + '" data-codec="' + escAttr(entries[i].label) + '"><path d="' + arcPath + '" fill="' + color + '"/></g>';
        startAngle = endAngle;
    }
    donutContainer += '</svg></div>';
    return donutContainer;
}

function getCollectionBadgeStat(type) {
    var t = (type || '').toLowerCase();
    if (t === 'tvshows') return '<span class="badge badge-tvshows">' + escHtml(T('tvShows', 'TV Shows')) + '</span>';
    if (t === 'movies' || t === '') return '<span class="badge badge-movies">' + escHtml(T('movies', 'Movies')) + '</span>';
    if (t === 'music') return '<span class="badge badge-music">' + escHtml(T('music', 'Music')) + '</span>';
    if (t === 'books') return '<span class="badge badge-books">' + escHtml(T('books', 'Books')) + '</span>';
    return '<span class="badge badge-other">' + escHtml(type || T('mixed', 'Mixed')) + '</span>';
}

function getCollectionBadge(type) {
    return getCollectionBadgeStat(type);
}

function buildBarSegments(data) {
    return buildBarSegmentsStat(data);
}

function buildBarSegmentsStat(data) {
    var total = (data.TotalMovieVideoSize || 0) + (data.TotalTvShowVideoSize || 0) +
        (data.TotalSubtitleSize || 0) + (data.TotalImageSize || 0) + (data.TotalTrickplaySize || 0) +
        (data.TotalNfoSize || 0) + (data.TotalMusicAudioSize || 0) + (data.TotalBookSize || 0);
    var otherSize = 0;
    var libs = data.Libraries || [];
    for (var i = 0; i < libs.length; i++) {
        otherSize += libs[i].OtherSize || 0;
        total += libs[i].OtherSize || 0;
    }
    if (total === 0) return '';
    var videoTotal = (data.TotalMovieVideoSize || 0) + (data.TotalTvShowVideoSize || 0);
    var categories = [
        { cls: 'bar-video', bytes: videoTotal, labelKey: 'video', labelFallback: 'Video' },
        { cls: 'bar-audio', bytes: data.TotalMusicAudioSize || 0, labelKey: 'audio', labelFallback: 'Audio' },
        { cls: 'bar-subtitle', bytes: data.TotalSubtitleSize || 0, labelKey: 'subtitles', labelFallback: 'Subtitles' },
        { cls: 'bar-image', bytes: data.TotalImageSize || 0, labelKey: 'images', labelFallback: 'Images' },
        { cls: 'bar-trickplay', bytes: data.TotalTrickplaySize || 0, labelKey: 'trickplay', labelFallback: 'Trickplay' },
        { cls: 'bar-nfo', bytes: data.TotalNfoSize || 0, labelKey: 'metadata', labelFallback: 'Metadata' },
        { cls: 'bar-other', bytes: otherSize, labelKey: 'other', labelFallback: 'Other' }
    ];
    if ((data.TotalBookFileCount || 0) > 0) {
        categories.splice(2, 0, { cls: 'bar-books', bytes: data.TotalBookSize || 0, labelKey: 'books', labelFallback: 'Books' });
    }
    var html = '<div class="total-bar">';
    for (var c = 0; c < categories.length; c++) {
        var cat = categories[c];
        var pct = cat.bytes / total * 100;
        if (pct > 0) {
            html += '<div class="bar-segment ' + cat.cls + '" style="width:' + pct.toFixed(2) + '%" title="' + escAttr(T(cat.labelKey, cat.labelFallback)) + '"></div>';
        }
    }
    html += '</div><div class="legend">';
    for (var k = 0; k < categories.length; k++) {
        var cc = categories[k];
        var label = T(cc.labelKey, cc.labelFallback) + ' (' + formatBytes(cc.bytes) + ')';
        html += '<div class="legend-item"><div class="legend-dot ' + cc.cls + '"></div>' + escHtml(label) + '</div>';
    }
    html += '</div>';
    return html;
}

function getLibraryFilteredData() {
    if (!_lastStatisticsData) return null;
    var d = _lastStatisticsData;
    var t = _filterState.libraryType;
    if (t === 'all') return d;
    if (t === 'movies') return { Libraries: d.Movies || [], Movies: d.Movies || [], TvShows: [], Music: [], Books: [], Other: [], MovieRootPaths: d.MovieRootPaths || [], TvShowRootPaths: [], MusicRootPaths: [], BookRootPaths: [], OtherRootPaths: [] };
    if (t === 'tvshows') return { Libraries: d.TvShows || [], Movies: [], TvShows: d.TvShows || [], Music: [], Books: [], Other: [], MovieRootPaths: [], TvShowRootPaths: d.TvShowRootPaths || [], MusicRootPaths: [], BookRootPaths: [], OtherRootPaths: [] };
    if (t === 'music') return { Libraries: d.Music || [], Movies: [], TvShows: [], Music: d.Music || [], Books: [], Other: [], MovieRootPaths: [], TvShowRootPaths: [], MusicRootPaths: d.MusicRootPaths || [], BookRootPaths: [], OtherRootPaths: [] };
    if (t === 'books') return { Libraries: d.Books || [], Movies: [], TvShows: [], Music: [], Books: d.Books || [], Other: [], MovieRootPaths: [], TvShowRootPaths: [], MusicRootPaths: [], BookRootPaths: d.BookRootPaths || [], OtherRootPaths: [] };
    if (t === 'other') return { Libraries: d.Other || [], Movies: [], TvShows: [], Music: [], Books: [], Other: d.Other || [], MovieRootPaths: [], TvShowRootPaths: [], MusicRootPaths: [], BookRootPaths: [], OtherRootPaths: d.OtherRootPaths || [] };
    return d;
}

function buildLibrarySelectorHtml() {
    var hasOther = _lastStatisticsData && _lastStatisticsData.Other && _lastStatisticsData.Other.length > 0;
    var items = [
        { id: 'all', key: 'statLibraryAll', fallback: 'All', icon: 'dashboard' },
        { id: 'movies', key: 'movies', fallback: 'Movies', icon: 'movie' },
        { id: 'tvshows', key: 'tvShows', fallback: 'Series', icon: 'tv' },
        { id: 'music', key: 'music', fallback: 'Music', icon: 'music_note' },
        { id: 'books', key: 'books', fallback: 'Books', icon: 'library_books' }
    ];
    if (hasOther) items.push({ id: 'other', key: 'other', fallback: 'Other', icon: 'folder' });
    var html = '<div class="stat-lib-selector" role="tablist" aria-label="' + escAttr(T('statLibraryAll', 'Libraries')) + '">';
    for (var i = 0; i < items.length; i++) {
        var it = items[i];
        var active = _filterState.libraryType === it.id ? ' active' : '';
        html += '<button class="stat-lib-btn' + active + '" data-lib="' + escAttr(it.id) + '" role="tab" aria-selected="' + (active ? 'true' : 'false') + '">' + mi(it.icon) + escHtml(T(it.key, it.fallback)) + '</button>';
    }
    html += '</div>';
    return html;
}

function attachLibrarySelectorHandlers() {
    var btns = document.querySelectorAll('.stat-lib-btn');
    for (var i = 0; i < btns.length; i++) {
        btns[i].addEventListener('click', function () {
            var lib = this.dataset.lib;
            _filterState.libraryType = lib;
            // Library switch resets dimension filters because file sets are disjoint.
            _filterState.activeFilters = {};
            _filterState.resultOffset = 0;
            renderStatisticsChrome();
        });
    }
}

function buildKpiStripHtml() {
    var d = getLibraryFilteredData();
    if (!d) return '';
    var libs = d.Libraries || [];
    // Aggregate per-library file counts for the selected scope
    var videoFiles = 0;
    var audioFiles = 0;
    var trickplayFolders = 0;
    var totalVideoSize = 0;
    var grandTotal = 0;
    for (var i = 0; i < libs.length; i++) {
        videoFiles += libs[i].VideoFileCount || 0;
        audioFiles += libs[i].AudioFileCount || 0;
        trickplayFolders += libs[i].TrickplayFolderCount || 0;
        totalVideoSize += libs[i].VideoSize || 0;
        grandTotal += libs[i].TotalSize || 0;
    }
    // Include music audio when scope is all or music — already in libs above.
    // Freed card uses cached cleanup stats if available.
    var freedHtml = '';
    if (_statsCleanupCache) {
        var freed = formatBytes(_statsCleanupCache.TotalBytesFreed || 0);
        var count = _statsCleanupCache.TotalItemsDeleted || 0;
        var tsRaw = _statsCleanupCache.LastCleanupTimestamp;
        var parsed = tsRaw ? new Date(tsRaw) : null;
        var valid = parsed && tsRaw !== '0001-01-01T00:00:00' && !isNaN(parsed.getTime());
        var last = valid ? parsed.toLocaleString() : T('never', 'Never');
        freedHtml = '<div class="stat-kpi-card stat-kpi-freed" title="' + escAttr(count + ' ' + T('totalItemsDeleted', 'items') + ' · ' + T('lastCleanup', 'Last cleanup') + ': ' + last) + '"><h3>' + mi('cleaning_services') + escHtml(T('totalBytesFreed', 'Freed')) + '</h3><p class="stat-kpi-value">' + escHtml(freed) + '</p><p class="stat-kpi-detail">' + escHtml(count + ' ' + T('items', 'items')) + '</p></div>';
    } else {
        freedHtml = '<div class="stat-kpi-card stat-kpi-freed"><h3>' + mi('cleaning_services') + escHtml(T('totalBytesFreed', 'Freed')) + '</h3><p class="stat-kpi-value">—</p><p class="stat-kpi-detail">' + escHtml(T('loadingInsights', 'Loading…')) + '</p></div>';
    }

    var html = '<div class="stat-kpi-strip">';
    html += '<div class="stat-kpi-card"><h3>' + mi('storage') + escHtml(T('video', 'Video')) + '</h3><p class="stat-kpi-value">' + escHtml(formatBytes(grandTotal)) + '</p><p class="stat-kpi-detail">' + escHtml((videoFiles + audioFiles) + ' ' + T('files', 'files')) + '</p></div>';
    html += '<div class="stat-kpi-card"><h3>' + mi('movie') + escHtml(T('video', 'Video')) + '</h3><p class="stat-kpi-value">' + escHtml(formatBytes(totalVideoSize)) + '</p><p class="stat-kpi-detail">' + escHtml(videoFiles + ' ' + T('files', 'files')) + '</p></div>';
    html += '<div class="stat-kpi-card"><h3>' + mi('image') + escHtml(T('trickplay', 'Trickplay')) + '</h3><p class="stat-kpi-value">' + escHtml(formatBytes(d.TotalTrickplaySize || libs.reduce(function (a, b) { return a + (b.TrickplaySize || 0); }, 0))) + '</p><p class="stat-kpi-detail">' + escHtml(trickplayFolders + ' ' + T('folders', 'folders')) + '</p></div>';
    html += freedHtml;
    html += '<div class="stat-kpi-card"><h3>' + mi('description') + escHtml(T('totalFiles', 'Files')) + '</h3><p class="stat-kpi-value">' + escHtml(String(videoFiles + audioFiles)) + '</p><p class="stat-kpi-detail">' + escHtml(videoFiles + ' ' + T('video', 'video') + ', ' + audioFiles + ' ' + T('audio', 'audio')) + '</p></div>';
    html += '</div>';
    return html;
}

function refreshCleanupKpi() {
    apiGet('JellyfinHelper/CleanupStatistics', function (stats) {
        _statsCleanupCache = stats;
        var strip = document.getElementById('statKpiStrip');
        if (strip) strip.innerHTML = buildKpiStripHtml().replace('<div class="stat-kpi-strip">', '').replace('</div>', '');
        // Re-render whole chrome to update tooltip and counts consistently
        var kpiWrap = document.getElementById('statKpiWrap');
        if (kpiWrap) kpiWrap.innerHTML = buildKpiStripHtml();
    }, function () {
        // Keep placeholder; do not error out the whole tab
    });
}

function toggleFilter(dimension, value) {
    if (!_filterState.activeFilters[dimension]) _filterState.activeFilters[dimension] = {};
    if (_filterState.activeFilters[dimension][value]) {
        delete _filterState.activeFilters[dimension][value];
        if (Object.keys(_filterState.activeFilters[dimension]).length === 0) delete _filterState.activeFilters[dimension];
    } else {
        _filterState.activeFilters[dimension][value] = true;
    }
    _filterState.resultOffset = 0;
    renderStatisticsChrome();
}

function clearAllFilters() {
    _filterState.activeFilters = {};
    _filterState.resultOffset = 0;
    renderStatisticsChrome();
}

function computeFilteredPaths() {
    var data = getLibraryFilteredData();
    if (!data) return [];
    var dims = Object.keys(_filterState.activeFilters);
    if (dims.length === 0) {
        // Collect unique file paths from WatchedTierPaths (covers all video files) or fallback to ContainerFormatPaths
        var libs = data.Libraries || [];
        var seen = {};
        var result = [];
        var primary = 'WatchedTierPaths';
        for (var li = 0; li < libs.length; li++) {
            var dict = libs[li][primary];
            if (!dict) continue;
            for (var key in dict) {
                if (!Object.hasOwn(dict, key)) continue;
                var arr = dict[key];
                for (var j = 0; j < arr.length; j++) {
                    if (!seen[arr[j]]) { seen[arr[j]] = true; result.push(arr[j]); }
                }
            }
        }
        // Fallback if Watched not populated (old cache or no watched data yet)
        if (result.length === 0) {
            for (var l = 0; l < libs.length; l++) {
                var cdict = libs[l].ContainerFormatPaths;
                if (!cdict) continue;
                for (var kk in cdict) {
                    if (!Object.hasOwn(cdict, kk)) continue;
                    var a = cdict[kk];
                    for (var q = 0; q < a.length; q++) if (!seen[a[q]]) { seen[a[q]] = true; result.push(a[q]); }
                }
            }
        }
        // Music/Books when filtered to those libs need their own primary
        if (result.length === 0 && data.Libraries.length > 0) {
            var firstLib = data.Libraries[0];
            if ((firstLib.MusicAudioCodecPaths && Object.keys(firstLib.MusicAudioCodecPaths).length > 0)) {
                for (var mk in firstLib.MusicAudioCodecPaths) {
                    if (!Object.hasOwn(firstLib.MusicAudioCodecPaths, mk)) continue;
                    var ma = firstLib.MusicAudioCodecPaths[mk];
                    for (var r = 0; r < ma.length; r++) if (!seen[ma[r]]) { seen[ma[r]] = true; result.push(ma[r]); }
                }
            }
        }
        return result.sort();
    }
    var sets = [];
    for (var d2 = 0; d2 < dims.length; d2++) {
        var dimension = dims[d2];
        var values = Object.keys(_filterState.activeFilters[dimension]);
        var pathsProp = STATISTICS_PATH_MAP[dimension];
        var union = {};
        for (var v = 0; v < values.length; v++) {
            var val = values[v];
            var direct = collectDictPaths(data.Libraries || [], pathsProp, val);
            for (var pp = 0; pp < direct.length; pp++) union[direct[pp]] = true;
        }
        var set = new Set(Object.keys(union));
        sets.push(set);
    }
    if (sets.length === 0) return [];
    // Intersect starting with smallest set
    sets.sort(function (a, b) { return a.size - b.size; });
    var smallest = sets[0];
    var result2 = [];
    smallest.forEach(function (path) {
        for (var s = 1; s < sets.length; s++) {
            if (!sets[s].has(path)) return;
        }
        result2.push(path);
    });
    return result2.sort();
}

function isDimensionRelevant(categoryMap) {
    var t = _filterState.libraryType;
    if (t === 'all') return true;
    if (t === 'movies') return !!categoryMap.movies;
    if (t === 'tvshows') return !!categoryMap.tvShows;
    if (t === 'music') return !!categoryMap.music;
    if (t === 'books') return !!categoryMap.books;
    if (t === 'other') return !!categoryMap.other;
    return true;
}

function computeCountsWithinSelection(dimension) {
    var filtered = computeFilteredPaths();
    if (filtered.length === 0 && Object.keys(_filterState.activeFilters).length === 0) {
        // No filter active — return full counts for current library scope
        var d = getLibraryFilteredData();
        if (!d) return {};
        var libs = d.Libraries || [];
        var prop = dimension === 'watched' ? 'WatchedTiers' : dimension === 'audioLanguages' ? 'AudioLanguages' : dimension === 'subtitleLanguages' ? 'SubtitleLanguages' : dimension === 'watchedByUser' ? 'WatchedByUserPaths' : null;
        if (prop) {
            var agg = {};
            for (var i = 0; i < libs.length; i++) {
                var dict = libs[i][prop];
                if (!dict) continue;
                for (var k in dict) {
                    if (!Object.hasOwn(dict, k)) continue;
                    var raw = dict[k];
                    var cnt = (prop === 'WatchedByUserPaths' && raw) ? raw.length : raw;
                    agg[k] = (agg[k] || 0) + cnt;
                }
            }
            return agg;
        }
        // For codec-like dimensions use generic aggregate via lib property name map
        var countPropMap = { 'resolutions': 'Resolutions', 'videoCodecs': 'VideoCodecs', 'videoAudioCodecs': 'VideoAudioCodecs', 'musicAudioCodecs': 'MusicAudioCodecs', 'bookFormats': 'BookFormats', 'containers': 'ContainerFormats', 'dynamicRanges': 'DynamicRanges', 'videoBitrate': 'VideoBitrateTiers' };
        var cp = countPropMap[dimension];
        if (cp) {
            var ac = {};
            for (var j = 0; j < libs.length; j++) {
                var dd = libs[j][cp];
                if (!dd) continue;
                for (var kk in dd) if (Object.hasOwn(dd, kk)) ac[kk] = (ac[kk] || 0) + dd[kk];
            }
            return ac;
        }
        return {};
    }
    // Filtered: count occurrences of each value that appears in filtered set
    var data = getLibraryFilteredData();
    if (!data) return {};
    var libs2 = data.Libraries || [];
    var pathsProp = STATISTICS_PATH_MAP[dimension];
    // Build reverse map value -> set of paths for this dimension in current scope
    var reverse = {};
    for (var li = 0; li < libs2.length; li++) {
        var pDict = libs2[li][pathsProp];
        if (!pDict) continue;
        for (var val in pDict) {
            if (!Object.hasOwn(pDict, val)) continue;
            if (!reverse[val]) reverse[val] = {};
            var arr = pDict[val];
            for (var a = 0; a < arr.length; a++) reverse[val][arr[a]] = true;
        }
    }
    var filteredSet = {};
    for (var f = 0; f < filtered.length; f++) filteredSet[filtered[f]] = true;
    var counts = {};
    for (var vv in reverse) {
        if (!Object.hasOwn(reverse, vv)) continue;
        var cnt = 0;
        var map = reverse[vv];
        for (var fp in map) if (filteredSet[fp]) cnt++;
        if (cnt > 0) counts[vv] = cnt;
    }
    return counts;
}

function getSizeMapForDimension(dimension) {
    var d = getLibraryFilteredData();
    if (!d) return {};
    var libs = d.Libraries || [];
    var sizePropMap = {
        'resolutions': 'ResolutionSizes', 'videoCodecs': 'VideoCodecSizes', 'videoAudioCodecs': 'VideoAudioCodecSizes',
        'musicAudioCodecs': 'MusicAudioCodecSizes', 'bookFormats': 'BookFormatSizes', 'containers': 'ContainerSizes',
        'dynamicRanges': 'DynamicRangeSizes', 'videoBitrate': 'VideoBitrateTierSizes',
        'audioLanguages': 'AudioLanguageSizes', 'subtitleLanguages': 'SubtitleLanguageSizes', 'watched': 'WatchedTierSizes', 'watchedByUser': 'WatchedByUserSizes'
    };
    var sp = sizePropMap[dimension];
    if (!sp) return {};
    var agg = {};
    for (var i = 0; i < libs.length; i++) {
        var dict = libs[i][sp];
        if (!dict) continue;
        for (var k in dict) if (Object.hasOwn(dict, k)) agg[k] = (agg[k] || 0) + dict[k];
    }
    return agg;
}

function buildFilterStripHtml() {
    var active = _filterState.activeFilters;
    var keys = Object.keys(active);
    if (keys.length === 0) return '';
    var filteredCount = computeFilteredPaths().length;
    var totalCount = (function () {
        var d = getLibraryFilteredData();
        if (!d) return 0;
        var c = 0;
        for (var i = 0; i < d.Libraries.length; i++) c += d.Libraries[i].VideoFileCount || 0;
        // For music/books scope, count audio/book files
        if (_filterState.libraryType === 'music') { c = 0; for (var j = 0; j < d.Libraries.length; j++) c += d.Libraries[j].AudioFileCount || 0; }
        if (_filterState.libraryType === 'books') { c = 0; for (var k = 0; k < d.Libraries.length; k++) c += d.Libraries[k].BookFileCount || 0; }
        if (c === 0) {
            // fallback via watched tier total
            var tmp = computeFilteredPaths();
            // When no filter, computeFilteredPaths returns all; reuse
            if (keys.length === 0) return tmp.length;
        }
        return c || filteredCount;
    })();
    var html = '<div class="stat-filter-strip"><span class="stat-filter-label">' + escHtml(T('statFilterStripLabel', 'Active')) + ':</span>';
    for (var di = 0; di < keys.length; di++) {
        var dim = keys[di];
        var vals = Object.keys(active[dim]);
        for (var vi = 0; vi < vals.length; vi++) {
            var v = vals[vi];
            html += '<button class="stat-filter-pill" data-dimension="' + escAttr(dim) + '" data-value="' + escAttr(v) + '" aria-label="' + escAttr(T('remove', 'Remove') + ' ' + v) + '">' + escHtml(v) + ' ✕</button>';
        }
    }
    html += '<button class="stat-filter-clear" data-action="clearAll">' + escHtml(T('statResetAll', 'Reset all')) + '</button>';
    html += '<span class="stat-count-badge">' + escHtml(String(totalCount)) + ' → ' + escHtml(String(filteredCount)) + '</span>';
    html += '</div>';
    return html;
}

function renderFilterStrip() {
    var el = document.getElementById('statFilterStrip');
    if (!el) return;
    el.innerHTML = buildFilterStripHtml();
    var pills = el.querySelectorAll('.stat-filter-pill');
    for (var i = 0; i < pills.length; i++) {
        pills[i].addEventListener('click', function () {
            toggleFilter(this.dataset.dimension, this.dataset.value);
        });
    }
    var clear = el.querySelector('[data-action="clearAll"]');
    if (clear) clear.addEventListener('click', function () { clearAllFilters(); });
}

function buildDonutSectionHtml(dimension) {
    var meta = STATISTICS_DIMENSIONS.find(function (d) { return d.id === dimension; });
    if (!meta) return '';
    var categoryMap = STATISTICS_CATEGORY_MAP[dimension];
    var relevant = isDimensionRelevant(categoryMap);
    var counts = relevant ? computeCountsWithinSelection(dimension) : {};
    var sizes = relevant ? getSizeMapForDimension(dimension) : {};
    var total = 0;
    for (var k in counts) if (Object.hasOwn(counts, k)) total += counts[k];
    var dimmed = !relevant ? ' stat-donut-dimmed' : '';
    var hint = !relevant ? '<span class="stat-donut-hint">' + escHtml(T('noData', 'No data in this view')) + '</span>' : '';
    // Total file count for header when collapsed — current library scope
    var headerCount = total;
    if (Object.keys(_filterState.activeFilters).length === 0) {
        // header shows scope total, body shows breakdown
    }
    var html = '<div class="stat-donut-section' + dimmed + '" data-dimension="' + escAttr(dimension) + '">';
    html += '<button class="stat-donut-header" aria-expanded="false" aria-controls="stat-donut-body-' + escAttr(dimension) + '"><span class="stat-donut-title">' + mi(meta.icon) + escHtml(T(meta.labelKey, meta.fallback)) + '</span><span class="stat-donut-count">' + escHtml(String(headerCount)) + '</span><span class="stat-donut-chevron">' + mi('expand_more') + '</span>' + hint + '</button>';
    html += '<div class="stat-donut-body" id="stat-donut-body-' + escAttr(dimension) + '" hidden>';
    if (!relevant) {
        html += '<p class="stat-donut-empty">' + escHtml(T('noData', 'No data in this view')) + '</p>';
    } else if (total === 0) {
        html += '<p class="stat-donut-empty">' + escHtml(T('noData', 'No data')) + '</p>';
    } else {
        // Render donut SVG + breakdown
        var chartId = 'stat_' + dimension;
        var libs = (getLibraryFilteredData() || {}).Libraries || [];
        // For tooltip library breakdown, we need the count dict and library property name
        var countPropMap = { 'resolutions': 'Resolutions', 'videoCodecs': 'VideoCodecs', 'videoAudioCodecs': 'VideoAudioCodecs', 'musicAudioCodecs': 'MusicAudioCodecs', 'bookFormats': 'BookFormats', 'containers': 'ContainerFormats', 'dynamicRanges': 'DynamicRanges', 'videoBitrate': 'VideoBitrateTiers', 'audioLanguages': 'AudioLanguages', 'subtitleLanguages': 'SubtitleLanguages', 'watched': 'WatchedTiers' };
        var countProp = countPropMap[dimension];
        // Build SVG via shared renderDonutSvg if available, else fallback
        var svg = '';
        if (typeof renderDonutSvg === 'function') {
            svg = renderDonutSvg(counts, libs, countProp, chartId);
        } else {
            svg = '<p style="opacity:0.5;">' + escHtml(T('noData', 'No data')) + '</p>';
        }
        html += svg;
        // Breakdown rows
        var entries = [];
        for (var kk in counts) if (Object.hasOwn(counts, kk)) entries.push({ label: kk, count: counts[kk], size: sizes[kk] || 0 });
        entries.sort(function (a, b) { return b.count - a.count; });
        html += '<div class="stat-breakdown">';
        for (var e = 0; e < entries.length; e++) {
            var ent = entries[e];
            var pct = total > 0 ? (ent.count / total * 100).toFixed(1) : '0';
            var isActive = _filterState.activeFilters[dimension] && _filterState.activeFilters[dimension][ent.label];
            var color = (typeof DONUT_COLORS !== 'undefined' ? DONUT_COLORS[e % DONUT_COLORS.length] : '#00a4dc');
            html += '<button class="stat-breakdown-row' + (isActive ? ' stat-breakdown-active' : '') + '" data-dimension="' + escAttr(dimension) + '" data-value="' + escAttr(ent.label) + '" role="button" aria-pressed="' + (isActive ? 'true' : 'false') + '">';
            html += '<span class="stat-breakdown-color" style="background:' + escAttr(color) + '"></span>';
            html += '<span class="stat-breakdown-info"><span class="stat-breakdown-name">' + escHtml(ent.label) + '</span><span class="stat-breakdown-stats">' + escHtml(String(ent.count)) + ' ' + escHtml(T('files', 'files')) + ' · ' + escHtml(pct) + '% · ' + escHtml(formatBytes(ent.size)) + '</span></span>';
            html += '<span class="stat-breakdown-bar"><span class="stat-breakdown-bar-fill" style="width:' + escAttr(pct) + '%;background:' + escAttr(color) + '"></span></span>';
            html += '</button>';
        }
        html += '</div>';
        html += '<div class="file-tree-panel" id="statDetail_' + escAttr(dimension) + '"></div>';
    }
    html += '</div></div>';
    return html;
}

function buildDonutPanelHtml() {
    var html = '<div class="stat-donut-panel">';
    for (var i = 0; i < STATISTICS_DIMENSIONS.length; i++) {
        var dim = STATISTICS_DIMENSIONS[i].id;
        // Hide Music/Book sections entirely when irrelevant and empty in All view to avoid clutter
        if (dim === 'musicAudioCodecs' || dim === 'bookFormats') {
            var d = getLibraryFilteredData();
            var libs = (d && d.Libraries) || [];
            var hasData = false;
            var propCheck = dim === 'musicAudioCodecs' ? 'MusicAudioCodecs' : 'BookFormats';
            for (var li = 0; li < libs.length; li++) { if (libs[li][propCheck] && Object.keys(libs[li][propCheck]).length > 0) { hasData = true; break; } }
            if (!hasData && _filterState.libraryType === 'all') {
                // Check real total
                var totalCheck = d && d.Libraries ? d.Libraries : [];
                var any = false;
                for (var q = 0; q < totalCheck.length; q++) { if (totalCheck[q][propCheck] && Object.keys(totalCheck[q][propCheck]).length > 0) any = true; }
                if (!any) continue;
            }
        }
        html += buildDonutSectionHtml(dim);
    }
    html += '</div>';
    return html;
}

function buildWatchedByUserFilterHtml() {
    var d = getLibraryFilteredData();
    if (!d) return '';
    var libs = d.Libraries || [];
    if (!isDimensionRelevant(STATISTICS_CATEGORY_MAP['watchedByUser'])) return '';
    // Aggregate per-user counts within current filtered set (like donuts)
    var filteredSet = null;
    var activeFilters = _filterState.activeFilters;
    var hasAnyFilter = Object.keys(activeFilters).length > 0;
    var filteredPaths = hasAnyFilter ? computeFilteredPaths() : null;
    if (filteredPaths) {
        filteredSet = {};
        for (var fi = 0; fi < filteredPaths.length; fi++) filteredSet[filteredPaths[fi]] = true;
    }
    var userCounts = {};
    var userSizes = {};
    for (var li2 = 0; li2 < libs.length; li2++) {
        var dict2 = libs[li2].WatchedByUserPaths || libs[li2].watchedByUserPaths;
        var sizes2 = libs[li2].WatchedByUserSizes || libs[li2].watchedByUserSizes;
        if (!dict2) continue;
        for (var user2 in dict2) {
            if (!Object.hasOwn(dict2, user2)) continue;
            var arr2 = dict2[user2] || [];
            var cnt = 0;
            var sz = 0;
            for (var ai = 0; ai < arr2.length; ai++) {
                if (!filteredSet || filteredSet[arr2[ai]]) {
                    cnt++;
                    // size per file not stored per user, approximate via global sizes dict if available
                }
            }
            if (cnt > 0) {
                userCounts[user2] = (userCounts[user2] || 0) + cnt;
                if (sizes2 && sizes2[user2] != null) {
                    // Proportional size when filtered
                    var totalForUser = dict2[user2].length;
                    var ratio = totalForUser ? cnt / totalForUser : 1;
                    userSizes[user2] = (userSizes[user2] || 0) + Math.round(sizes2[user2] * ratio);
                }
            }
        }
    }
    // Fallback when no per-user data but filteredSet exists: show nothing (handled below)
    var users = Object.keys(userCounts).sort(function(a,b){ return userCounts[b]-userCounts[a]; });
    if (users.length === 0) return '';
    var html = '<div class="stat-donut-section" data-dimension="watchedByUser">';
    html += '<button class="stat-donut-header" aria-expanded="false" aria-controls="stat-watchedByUser-body"><span class="stat-donut-title">' + mi('group') + escHtml(T('watchedBy', 'Watched by')) + '</span><span class="stat-donut-count">' + escHtml(String(users.length)) + '</span><span class="stat-donut-chevron">' + mi('expand_more') + '</span></button>';
    html += '<div class="stat-donut-body" id="stat-watchedByUser-body" hidden>';
    html += '<div class="stat-breakdown">';
    for (var i = 0; i < users.length; i++) {
        var u = users[i];
        var cnt = userCounts[u];
        var size = userSizes[u] || 0;
        var isActive = _filterState.activeFilters['watchedByUser'] && _filterState.activeFilters['watchedByUser'][u];
        var color = (typeof DONUT_COLORS !== 'undefined' ? DONUT_COLORS[i % DONUT_COLORS.length] : '#00a4dc');
        html += '<button class="stat-breakdown-row' + (isActive ? ' stat-breakdown-active' : '') + '" data-dimension="watchedByUser" data-value="' + escAttr(u) + '" role="button" aria-pressed="' + (isActive ? 'true' : 'false') + '">';
        html += '<span class="stat-breakdown-color" style="background:' + escAttr(color) + '"></span>';
        html += '<span class="stat-breakdown-info"><span class="stat-breakdown-name">' + escHtml(u) + '</span><span class="stat-breakdown-stats">' + escHtml(String(cnt)) + ' ' + escHtml(T('files', 'files')) + (size ? ' \u00b7 ' + escHtml(formatBytes(size)) : '') + '</span></span>';
        html += '</button>';
    }
    html += '</div></div></div>';
    return html;
}


function attachDonutHoverTooltips() {
    var containers = document.querySelectorAll('.donut-container');
    for (var ci = 0; ci < containers.length; ci++) {
        (function(container) {
            var paths = container.querySelectorAll('.donut-segment path');
            var tooltip = container.querySelector('.donut-tooltip');
            if (!tooltip) return;
            for (var pi = 0; pi < paths.length; pi++) {
                (function(path) {
                    var seg = path.closest('.donut-segment');
                    if (!seg) return;
                    var segId = seg.getAttribute('data-segment-id');
                    var data = _statDonutTooltipData[segId];
                    if (!data) return;
                    path.addEventListener('mouseenter', function(e) {
                        var html = '<div class="donut-tooltip-header"><span class="donut-tooltip-codec">' + escHtml(data.codec) + '</span><span class="donut-tooltip-total">' + escHtml(String(data.totalCount)) + ' ' + escHtml(T('files', 'files')) + '</span></div><div class="donut-tooltip-pct">' + escHtml(data.totalPct) + '%</div>';
                        if (data.libraries && data.libraries.length) {
                            html += '<div class="donut-tooltip-divider"></div><table class="donut-tooltip-table"><tbody>';
                            for (var li = 0; li < data.libraries.length; li++) {
                                html += '<tr><td class="donut-tooltip-lib">' + escHtml(data.libraries[li].name) + '</td><td class="donut-tooltip-count">' + escHtml(String(data.libraries[li].count)) + '</td></tr>';
                            }
                            html += '</tbody></table>';
                        }
                        tooltip.innerHTML = html;
                        tooltip.classList.add('visible');
                        var rect = container.getBoundingClientRect();
                        tooltip.style.left = (e.clientX - rect.left + 12) + 'px';
                        tooltip.style.top = (e.clientY - rect.top + 12) + 'px';
                    });
                    path.addEventListener('mouseleave', function() { tooltip.classList.remove('visible'); });
                })(paths[pi]);
            }
        })(containers[ci]);
    }
}

function attachDonutPanelHandlers() {
    var headers = document.querySelectorAll('.stat-donut-header');
    for (var i = 0; i < headers.length; i++) {
        headers[i].addEventListener('click', function () {
            var expanded = this.getAttribute('aria-expanded') === 'true';
            this.setAttribute('aria-expanded', expanded ? 'false' : 'true');
            var body = document.getElementById(this.getAttribute('aria-controls'));
            if (body) body.hidden = expanded;
            var chev = this.querySelector('.stat-donut-chevron');
            if (chev) chev.style.transform = expanded ? '' : 'rotate(180deg)';
        });
    }
    var rows = document.querySelectorAll('.stat-breakdown-row');
    for (var r = 0; r < rows.length; r++) {
        rows[r].addEventListener('click', function () {
            toggleFilter(this.dataset.dimension, this.dataset.value);
        });
    }
    // Donut segment clicks — segments rendered by renderDonutSvg have class donut-segment
    var segs = document.querySelectorAll('.stat-donut-body .donut-segment');
    for (var s = 0; s < segs.length; s++) {
        segs[s].addEventListener('click', function () {
            var seg = this.closest ? this.closest('.donut-segment') : this;
            var code = seg ? seg.dataset.codec : null;
            // Find nearest dimension via parent section
            var section = this.closest ? this.closest('.stat-donut-section') : null;
            if (section && code) toggleFilter(section.dataset.dimension, code);
        });
    }
    // Mirror hover tooltips if Codecs tooltip logic exists
    if (typeof attachDonutHoverTooltips === 'function') {
        try { attachDonutHoverTooltips(); } catch (e) { }
    }
}

function collectResolutionDimensionsMap() {
    var d = getLibraryFilteredData();
    if (!d) return {};
    var merged = {};
    var groups = [d.Movies, d.TvShows, d.Other];
    // Also include Libraries when All selected and those groups not present
    if (!d.Movies && !d.TvShows && d.Libraries) groups = [d.Libraries];
    for (var gi = 0; gi < groups.length; gi++) {
        var grp = groups[gi] || [];
        for (var li = 0; li < grp.length; li++) {
            var dims = grp[li] ? grp[li].ResolutionDimensions : null;
            if (!dims) continue;
            for (var p in dims) if (Object.hasOwn(dims, p)) merged[p] = dims[p];
        }
    }
    // Also check Libraries directly for cases where Movies/TvShows not sliced
    if (d.Libraries) {
        for (var q = 0; q < d.Libraries.length; q++) {
            var dd = d.Libraries[q].ResolutionDimensions;
            if (!dd) continue;
            for (var pp in dd) if (Object.hasOwn(dd, pp)) merged[pp] = dd[pp];
        }
    }
    return merged;
}

function buildFileDetail(path) {
    var d = _lastStatisticsData;
    if (!d) return '';
    var libs = (d.Libraries || []);
    var info = { path: path, videoCodec: null, container: null, resolution: null, resolutionDim: null, bitrate: null, dynamicRange: null, audioCodec: null, audioLangs: [], subtitleLangs: [], watchedBy: [], watchedDetails: [] };
    var dimMap = collectResolutionDimensionsMap();
    info.resolutionDim = dimMap[path] || null;
    for (var li = 0; li < libs.length; li++) {
        var lib = libs[li];
        for (var key in (lib.VideoCodecPaths || {})) if (Object.hasOwn(lib.VideoCodecPaths, key) && lib.VideoCodecPaths[key].indexOf(path) !== -1) info.videoCodec = key;
        for (var kk in (lib.ContainerFormatPaths || {})) if (Object.hasOwn(lib.ContainerFormatPaths, kk) && lib.ContainerFormatPaths[kk].indexOf(path) !== -1) info.container = kk;
        for (var rk in (lib.ResolutionPaths || {})) if (Object.hasOwn(lib.ResolutionPaths, rk) && lib.ResolutionPaths[rk].indexOf(path) !== -1) info.resolution = rk;
        for (var bk in (lib.VideoBitrateTierPaths || {})) if (Object.hasOwn(lib.VideoBitrateTierPaths, bk) && lib.VideoBitrateTierPaths[bk].indexOf(path) !== -1) info.bitrate = bk;
        for (var dk in (lib.DynamicRangePaths || {})) if (Object.hasOwn(lib.DynamicRangePaths, dk) && lib.DynamicRangePaths[dk].indexOf(path) !== -1) info.dynamicRange = dk;
        for (var ak in (lib.VideoAudioCodecPaths || {})) if (Object.hasOwn(lib.VideoAudioCodecPaths, ak) && lib.VideoAudioCodecPaths[ak].indexOf(path) !== -1) info.audioCodec = ak;
        for (var al in (lib.AudioLanguagePaths || {})) if (Object.hasOwn(lib.AudioLanguagePaths, al) && lib.AudioLanguagePaths[al].indexOf(path) !== -1) info.audioLangs.push(al);
        for (var sl in (lib.SubtitleLanguagePaths || {})) if (Object.hasOwn(lib.SubtitleLanguagePaths, sl) && lib.SubtitleLanguagePaths[sl].indexOf(path) !== -1) info.subtitleLangs.push(sl);
        // New per-file per-user details (preferred) — fallback to legacy string list
        var details = lib.WatchedDetails ? (lib.WatchedDetails[path] || lib.watchedDetails?.[path]) : null;
        if (details && details.length) {
            info.watchedDetails = details;
            // also populate simple list for compatibility
            var names = [];
            for (var di = 0; di < details.length; di++) {
                var det = details[di];
                var uname = det.username || det.Username || '';
                if (uname) names.push(uname);
            }
            info.watchedBy = names;
        } else if (lib.WatchedByUsers && lib.WatchedByUsers[path]) {
            info.watchedBy = lib.WatchedByUsers[path];
        } else if (lib.watchedByUsers && lib.watchedByUsers[path]) {
            info.watchedBy = lib.watchedByUsers[path];
        }
    }

    var html = '<div class="stat-file-detail">';
    html += '<div class="stat-detail-row"><span class="stat-detail-label">' + escHtml(T('video', 'Video')) + '</span><span class="stat-detail-value">';
    var chips = [];
    if (info.videoCodec) chips.push('<button class="stat-chip" data-dimension="videoCodecs" data-value="' + escAttr(info.videoCodec) + '">' + escHtml(info.videoCodec) + '</button>');
    if (info.container) chips.push('<button class="stat-chip" data-dimension="containers" data-value="' + escAttr(info.container) + '">' + escHtml(info.container) + '</button>');
    if (info.resolution) chips.push('<button class="stat-chip" data-dimension="resolutions" data-value="' + escAttr(info.resolution) + '">' + escHtml(info.resolution) + (info.resolutionDim ? ' (' + escHtml(info.resolutionDim) + ')' : '') + '</button>');
    if (info.bitrate) chips.push('<button class="stat-chip" data-dimension="videoBitrate" data-value="' + escAttr(info.bitrate) + '">' + escHtml(info.bitrate) + '</button>');
    html += chips.length ? chips.join(' \u00b7 ') : escHtml(T('noData', '\u2014'));
    html += '</span></div>';

    html += '<div class="stat-detail-row"><span class="stat-detail-label">' + escHtml(T('dynamicRange', 'HDR')) + '</span><span class="stat-detail-value">';
    if (info.dynamicRange) html += '<button class="stat-chip" data-dimension="dynamicRanges" data-value="' + escAttr(info.dynamicRange) + '">' + escHtml(info.dynamicRange) + '</button>';
    else html += escHtml(T('noData', '\u2014'));
    html += '</span></div>';

    html += '<div class="stat-detail-row"><span class="stat-detail-label">' + escHtml(T('audio', 'Audio')) + '</span><span class="stat-detail-value">';
    if (info.audioCodec) html += '<button class="stat-chip" data-dimension="videoAudioCodecs" data-value="' + escAttr(info.audioCodec) + '">' + escHtml(info.audioCodec) + '</button> ';
    if (info.audioLangs.length) {
        for (var ai = 0; ai < info.audioLangs.length; ai++) html += '<button class="stat-chip" data-dimension="audioLanguages" data-value="' + escAttr(info.audioLangs[ai]) + '">' + escHtml(info.audioLangs[ai]) + '</button> ';
    } else if (!info.audioCodec) html += escHtml(T('noData', '\u2014'));
    html += '</span></div>';

    html += '<div class="stat-detail-row"><span class="stat-detail-label">' + escHtml(T('subtitles', 'Subtitles')) + '</span><span class="stat-detail-value">';
    if (info.subtitleLangs.length) {
        for (var si = 0; si < info.subtitleLangs.length; si++) html += '<button class="stat-chip" data-dimension="subtitleLanguages" data-value="' + escAttr(info.subtitleLangs[si]) + '">' + escHtml(info.subtitleLangs[si]) + '</button> ';
        html += '<span class="stat-detail-hint">(' + escHtml(T('subtitleEmbeddedHint', 'embedded')) + ')</span>';
    } else html += escHtml(T('noData', '\u2014'));
    html += '</span></div>';

    // Watched — collapsible per-file hint: "X Users Watched" → expand to per-user with when/how often
    var watchedCount = info.watchedDetails.length || info.watchedBy.length || 0;
    html += '<div class="stat-detail-row stat-detail-watched"><span class="stat-detail-label">' + escHtml(T('watched', 'Watched')) + '</span><span class="stat-detail-value">';
    if (watchedCount === 0) {
        html += escHtml(T('never', 'Never watched'));
    } else {
        // Show count as button to expand details
        html += '<button class="stat-watched-toggle" aria-expanded="false">' + mi('visibility') + escHtml(String(watchedCount)) + ' ' + escHtml(T('watched', 'Watched')) + ' \u25be</button>';
        html += '<div class="stat-watched-details" hidden>';
        if (info.watchedDetails.length) {
            for (var wi = 0; wi < info.watchedDetails.length; wi++) {
                var det = info.watchedDetails[wi];
                var unameRaw = det.username || det.Username || '';
                var uname = escHtml(unameRaw);
                var pc = det.playCount != null ? det.playCount : (det.PlayCount != null ? det.PlayCount : 1);
                var lpdRaw = det.lastPlayedDate || det.LastPlayedDate || det.lastPlayed || null;
                var when = '';
                if (lpdRaw) {
                    try { when = formatTimeAgo(lpdRaw) || new Date(lpdRaw).toLocaleString(); } catch(e) { when = String(lpdRaw); }
                } else {
                    when = escHtml(T('never', 'unknown'));
                }
                var plays = pc === 1 ? '1 ' + escHtml(T('play', 'play')) : escHtml(String(pc)) + ' ' + escHtml(T('plays', 'plays'));
                html += '<div class="stat-watched-user">' + mi('person') + '<span class="stat-watched-name">' + uname + '</span><span class="stat-watched-meta">' + plays + ' \u00b7 ' + escHtml(when) + '</span><button class="stat-chip stat-chip-small" data-dimension="watchedByUser" data-value="' + escAttr(unameRaw) + '">' + escHtml(T('filterByUser', 'Filter')) + '</button></div>';
            }
        } else {
            for (var wj = 0; wj < info.watchedBy.length; wj++) {
                var u = escHtml(info.watchedBy[wj]);
                html += '<div class="stat-watched-user">' + mi('person') + '<span class="stat-watched-name">' + u + '</span><button class="stat-chip stat-chip-small" data-dimension="watchedByUser" data-value="' + escAttr(info.watchedBy[wj]) + '">' + escHtml(T('filterByUser', 'Filter')) + '</button></div>';
            }
        }
        html += '</div>';
    }
    html += '</span></div>';

    html += '<div class="stat-detail-row stat-detail-path"><span class="stat-detail-label">' + escHtml(T('path', 'Path')) + '</span><span class="stat-detail-value" title="' + escAttr(path) + '">' + escHtml(path) + '</span></div>';
    html += '</div>';
    return html;
}

function renderResultsPanel() {
    var container = document.getElementById('statResultsPanel');
    if (!container) return;
    var filtered = computeFilteredPaths();
    var hasFilters = Object.keys(_filterState.activeFilters).length > 0;
    var offset = _filterState.resultOffset || 0;
    var page = filtered.slice(offset, offset + STATISTICS_PAGE_SIZE);
    var html = '';
    if (!hasFilters) {
        // Curated defaults: Top 5 largest + Top 5 never-watched
        var libs = (getLibraryFilteredData() || {}).Libraries || [];
        // Largest: sort by known sizes — collect path->size via container sizes? Use file-size via bitrate sizes as proxy; for curated we can sort filtered (all) by path length proxy? Better use library VideoSize distribution not per-file size. Use Watched tier to get never-watched, and for largest we take first 5 of filtered sorted alphabetically as placeholder; true per-file size not stored per-file, so we show curated via watched + arbitrary largest from size dict
        // Simpler: show Top 5 from filtered sorted, and Top 5 never-watched slice
        var neverWatched = [];
        for (var li = 0; li < libs.length; li++) {
            var wt = libs[li].WatchedTierPaths && libs[li].WatchedTierPaths['Never watched'];
            if (wt) for (var n = 0; n < wt.length; n++) neverWatched.push(wt[n]);
        }
        html += '<div class="stat-curated">';
        html += '<p class="stat-curated-hint">' + escHtml(T('statNoFiltersHint', 'Select a filter to explore your library.')) + '</p>';
        html += '<div class="stat-curated-cols">';
        html += '<div class="stat-curated-col"><h4>' + escHtml(T('statTopNeverWatched', 'Top 5 never-watched')) + '</h4>';
        if (neverWatched.length === 0) html += '<p style="opacity:0.5;">' + escHtml(T('noData', 'No data')) + '</p>';
        else {
            for (var b = 0; b < Math.min(5, neverWatched.length); b++) {
                var pp = neverWatched[b];
                var nm = pp.split('/').pop() || pp;
                html += '<div class="stat-curated-item" title="' + escAttr(pp) + '"><span class="stat-curated-name">' + escHtml(nm) + '</span></div>';
            }
        }
        html += '</div></div></div>';
        container.innerHTML = html;
        return;
    }
    if (filtered.length === 0) {
        container.innerHTML = '<p class="stat-no-results">' + escHtml(T('noFilesFound', 'No files found.')) + ' <button class="stat-link" data-action="clearAll">' + escHtml(T('statResetAll', 'Reset all')) + '</button></p>';
        var clr = container.querySelector('[data-action="clearAll"]');
        if (clr) clr.addEventListener('click', function () { clearAllFilters(); });
        return;
    }
    html += '<div class="stat-results-header">' + escHtml(String(filtered.length)) + ' ' + escHtml(T('files', 'files')) + '</div>';
    html += '<div class="stat-results-list">';
    for (var i = 0; i < page.length; i++) {
        var path = page[i];
        var fileName = path.split('/').pop() || path.split('\\').pop() || path;
        html += '<div class="stat-file-entry" data-path="' + escAttr(path) + '" role="button" tabindex="0" aria-expanded="false"><div class="stat-file-row"><span class="stat-file-icon">' + mi('description') + '</span><span class="stat-file-name">' + escHtml(fileName) + '</span><span class="stat-file-path-hint" title="' + escAttr(path) + '">' + escHtml(path) + '</span><span class="stat-file-chevron">' + mi('expand_more') + '</span></div><div class="stat-file-detail-wrap" hidden></div></div>';
    }
    html += '</div>';
    if (offset + STATISTICS_PAGE_SIZE < filtered.length) {
        var remaining = filtered.length - (offset + STATISTICS_PAGE_SIZE);
        html += '<button class="stat-load-more" data-action="loadMore">' + escHtml(T('loadMore', 'Load more')) + ' (' + escHtml(String(remaining)) + ')</button>';
    }
    container.innerHTML = html;
    // Bind file entry expand
    var entries = container.querySelectorAll('.stat-file-entry');
    for (var e = 0; e < entries.length; e++) {
        entries[e].addEventListener('click', function () {
            var expanded = this.getAttribute('aria-expanded') === 'true';
            // Collapse others (accordion)
            var all = container.querySelectorAll('.stat-file-entry');
            for (var k = 0; k < all.length; k++) {
                if (all[k] !== this) {
                    all[k].setAttribute('aria-expanded', 'false');
                    var wrap = all[k].querySelector('.stat-file-detail-wrap');
                    if (wrap) { wrap.hidden = true; wrap.innerHTML = ''; }
                    var ch = all[k].querySelector('.stat-file-chevron');
                    if (ch) ch.style.transform = '';
                }
            }
            this.setAttribute('aria-expanded', expanded ? 'false' : 'true');
            var w = this.querySelector('.stat-file-detail-wrap');
            var chev = this.querySelector('.stat-file-chevron');
            if (expanded) {
                if (w) { w.hidden = true; w.innerHTML = ''; }
                if (chev) chev.style.transform = '';
            } else {
                if (w) { w.innerHTML = buildFileDetail(this.dataset.path); w.hidden = false; }
                if (chev) chev.style.transform = 'rotate(180deg)';
                // Bind watched collapsible inside file detail
    var watchedToggles = w.querySelectorAll('.stat-watched-toggle');
    for (var wt = 0; wt < watchedToggles.length; wt++) {
        watchedToggles[wt].addEventListener('click', function(ev) {
            ev.stopPropagation();
            var exp = this.getAttribute('aria-expanded') === 'true';
            this.setAttribute('aria-expanded', exp ? 'false' : 'true');
            var details = this.nextElementSibling;
            if (details) details.hidden = exp;
            this.innerHTML = this.innerHTML.replace(exp ? '\u25b2' : '\u25be', exp ? '\u25be' : '\u25b2');
        });
    }
    // Bind chip clicks inside detail to toggleFilter
                var chips = w.querySelectorAll('.stat-chip');
                for (var c = 0; c < chips.length; c++) {
                    chips[c].addEventListener('click', function (ev) {
                        ev.stopPropagation();
                        toggleFilter(this.dataset.dimension, this.dataset.value);
                    });
                }
            }
        });
        entries[e].addEventListener('keydown', function (ev) {
            if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); this.click(); }
        });
    }
    var more = container.querySelector('[data-action="loadMore"]');
    if (more) more.addEventListener('click', function () { _filterState.resultOffset += STATISTICS_PAGE_SIZE; renderResultsPanel(); });
    var clearBtn = container.querySelectorAll('[data-action="clearAll"]');
    for (var cb = 0; cb < clearBtn.length; cb++) clearBtn[cb].addEventListener('click', function () { clearAllFilters(); });
}

function buildStorageOverviewHtml() {
    var data = _lastStatisticsData;
    if (!data) return '';
    var grandTotal = 0;
    var libs = data.Libraries || [];
    for (var i = 0; i < libs.length; i++) grandTotal += libs[i].TotalSize || 0;
    var html = '<div class="stat-storage-section">';
    html += '<button class="stat-storage-header" aria-expanded="false" aria-controls="statStorageBody"><span class="stat-storage-title">' + mi('storage') + escHtml(T('storageDistribution', 'Storage Overview')) + ' · ' + escHtml(formatBytes(grandTotal)) + '</span><span class="stat-storage-chevron">' + mi('expand_more') + '</span></button>';
    html += '<div class="stat-storage-body" id="statStorageBody" hidden>';
    html += buildBarSegmentsStat(data);
    html += '<div class="section-title">' + mi('library_books') + escHtml(T('perLibraryBreakdown', 'Per-Library Breakdown')) + '</div>';
    html += '<div class="library-table-wrapper"><table class="library-table"><thead><tr>';
    html += '<th>' + escHtml(T('library', 'Library')) + '</th><th>' + escHtml(T('type', 'Type')) + '</th><th>' + escHtml(T('video', 'Video')) + '</th><th>' + escHtml(T('audio', 'Audio')) + '</th><th>' + escHtml(T('subtitles', 'Subtitles')) + '</th><th>' + escHtml(T('images', 'Images')) + '</th><th>' + escHtml(T('trickplay', 'Trickplay')) + '</th><th>' + escHtml(T('total', 'Total')) + '</th>';
    html += '</tr></thead><tbody>';
    for (var j = 0; j < libs.length; j++) {
        var lib = libs[j];
        html += '<tr><td>' + escHtml(lib.LibraryName) + '</td><td>' + getCollectionBadgeStat(lib.CollectionType) + '</td><td>' + escHtml(formatBytes(lib.VideoSize || 0)) + '</td><td>' + escHtml(formatBytes(lib.AudioSize || 0)) + '</td><td>' + escHtml(formatBytes(lib.SubtitleSize || 0)) + '</td><td>' + escHtml(formatBytes(lib.ImageSize || 0)) + '</td><td>' + escHtml(formatBytes(lib.TrickplaySize || 0)) + '</td><td><strong>' + escHtml(formatBytes(lib.TotalSize || 0)) + '</strong></td></tr>';
    }
    html += '</tbody></table></div>';
    html += '</div></div>';
    return html;
}

function renderStatisticsChrome() {
    var container = document.getElementById('statisticsContent');
    if (!container) return;
    var html = '';
    html += buildLibrarySelectorHtml();
    html += '<div id="statKpiWrap">' + buildKpiStripHtml() + '</div>';
    html += '<div id="statFilterStrip">' + buildFilterStripHtml() + '</div>';
    // Inner tab switcher for mobile
    var isMobile = window.innerWidth <= STATISTICS_MOBILE_BREAKPOINT;
    var filteredCount = computeFilteredPaths().length;
    if (isMobile) {
        var activeFiltersTab = _filterState.innerTab === 'filters';
        html += '<div class="stat-inner-tabs" role="tablist"><button class="stat-inner-tab' + (activeFiltersTab ? ' active' : '') + '" data-inner="filters" role="tab" aria-selected="' + (activeFiltersTab ? 'true' : 'false') + '">' + mi('filter_list') + escHtml(T('statFilters', 'Filters')) + '</button><button class="stat-inner-tab' + (!activeFiltersTab ? ' active' : '') + '" data-inner="results" role="tab" aria-selected="' + (!activeFiltersTab ? 'true' : 'false') + '">' + mi('list') + escHtml(T('statResults', 'Results')) + ' (' + escHtml(String(filteredCount)) + ')</button></div>';
    }
    html += '<div class="stat-panel-layout">';
    var filtersHidden = isMobile && _filterState.innerTab !== 'filters' ? ' hidden' : '';
    var resultsHidden = isMobile && _filterState.innerTab !== 'results' ? ' hidden' : '';
    html += '<div class="stat-filter-panel' + filtersHidden + '" id="statFilterPanel">' + buildDonutPanelHtml() + buildWatchedByUserFilterHtml() + '</div>';
    html += '<div class="stat-results-panel' + resultsHidden + '" id="statResultsPanel"></div>';
    html += '</div>';
    html += buildStorageOverviewHtml();
    container.innerHTML = html;
    attachLibrarySelectorHandlers();
    renderFilterStrip();
    attachDonutPanelHandlers();
    renderResultsPanel();
    // Storage collapsible
    var storHeader = container.querySelector('.stat-storage-header');
    if (storHeader) storHeader.addEventListener('click', function () {
        var exp = this.getAttribute('aria-expanded') === 'true';
        this.setAttribute('aria-expanded', exp ? 'false' : 'true');
        var body = document.getElementById(this.getAttribute('aria-controls'));
        if (body) body.hidden = exp;
        var chev = this.querySelector('.stat-storage-chevron');
        if (chev) chev.style.transform = exp ? '' : 'rotate(180deg)';
    });
    // Mobile inner tabs
    var innerTabs = container.querySelectorAll('.stat-inner-tab');
    for (var t = 0; t < innerTabs.length; t++) {
        innerTabs[t].addEventListener('click', function () {
            _filterState.innerTab = this.dataset.inner;
            renderStatisticsChrome();
        });
    }
}

function fillStatisticsData(data) {
    _lastStatisticsData = data;
    _filterState.activeFilters = {};
    _filterState.resultOffset = 0;
    if (!_filterState.libraryType) _filterState.libraryType = 'all';
    renderStatisticsChrome();
    refreshCleanupKpi();
}
