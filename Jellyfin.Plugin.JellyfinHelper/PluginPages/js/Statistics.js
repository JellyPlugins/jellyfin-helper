'use strict';

var _lastStatisticsData = null;
var _statsCleanupCache = null;

var SCOPE_ALL = 'all';
var STATISTICS_PAGE_SIZE = 50;

// Every donut/filter/tree explorer instance (the aggregate "All Libraries" view, plus one per
// expanded per-library row) keeps its own independent state so combining filters in one library
// never affects another. Keyed by scope key: SCOPE_ALL or libScopeKey(index).
var _explorerStates = {};

// Which library rows are expanded in the Per-Library Breakdown list, and whether the aggregate
// "All Libraries" explorer itself is expanded. Content for a collapsed scope is not rendered at
// all (lazy), keeping the default view light.
var _expandedLibraryRows = {};
var _allExplorerExpanded = false;

function libScopeKey(index) {
    return 'lib' + index;
}

function scopeLibraryIndex(scopeKey) {
    return scopeKey === SCOPE_ALL ? null : parseInt(scopeKey.slice(3), 10);
}

function getExplorerState(scopeKey) {
    if (!_explorerStates[scopeKey]) {
        _explorerStates[scopeKey] = { activeFilters: {}, activeTreeSelection: null, expandedDonutDims: {}, resultOffset: 0 };
    }
    return _explorerStates[scopeKey];
}

function classifyCollectionType(collectionType) {
    var t = (collectionType || '').toLowerCase();
    if (t === 'movies' || t === 'homevideos' || t === 'musicvideos') return 'movies';
    if (t === 'tvshows') return 'tvShows';
    if (t === 'music') return 'music';
    if (t === 'books') return 'books';
    return 'other';
}

function getScopedLib(scopeKey) {
    if (scopeKey === SCOPE_ALL || !_lastStatisticsData) return null;
    var idx = scopeLibraryIndex(scopeKey);
    return (_lastStatisticsData.Libraries || [])[idx] || null;
}

// Wraps a single library into the { Libraries, Movies, TvShows, Music, Books, Other, *RootPaths }
// shape the rest of this file already expects, so every rendering/filtering helper below works
// identically whether it is scoped to the whole server or to exactly one library.
function getScopedData(scopeKey) {
    if (!_lastStatisticsData) return null;
    if (scopeKey === SCOPE_ALL) return _lastStatisticsData;
    var lib = getScopedLib(scopeKey);
    if (!lib) return null;
    var cls = classifyCollectionType(lib.CollectionType);
    var roots = lib.RootPaths || [];
    return {
        Libraries: [lib],
        Movies: cls === 'movies' ? [lib] : [],
        TvShows: cls === 'tvShows' ? [lib] : [],
        Music: cls === 'music' ? [lib] : [],
        Books: cls === 'books' ? [lib] : [],
        Other: cls === 'other' ? [lib] : [],
        MovieRootPaths: cls === 'movies' ? roots : [],
        TvShowRootPaths: cls === 'tvShows' ? roots : [],
        MusicRootPaths: cls === 'music' ? roots : [],
        BookRootPaths: cls === 'books' ? roots : [],
        OtherRootPaths: cls === 'other' ? roots : []
    };
}

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

var STATISTICS_COUNT_PROP_MAP = {
    'resolutions': 'Resolutions',
    'videoCodecs': 'VideoCodecs',
    'videoAudioCodecs': 'VideoAudioCodecs',
    'musicAudioCodecs': 'MusicAudioCodecs',
    'bookFormats': 'BookFormats',
    'containers': 'ContainerFormats',
    'dynamicRanges': 'DynamicRanges',
    'videoBitrate': 'VideoBitrateTiers',
    'audioLanguages': 'AudioLanguages',
    'subtitleLanguages': 'SubtitleLanguages',
    'watched': 'WatchedTiers'
};

var STATISTICS_SIZE_PROP_MAP = {
    'resolutions': 'ResolutionSizes',
    'videoCodecs': 'VideoCodecSizes',
    'videoAudioCodecs': 'VideoAudioCodecSizes',
    'musicAudioCodecs': 'MusicAudioCodecSizes',
    'bookFormats': 'BookFormatSizes',
    'containers': 'ContainerSizes',
    'dynamicRanges': 'DynamicRangeSizes',
    'videoBitrate': 'VideoBitrateTierSizes',
    'audioLanguages': 'AudioLanguageSizes',
    'subtitleLanguages': 'SubtitleLanguageSizes',
    'watched': 'WatchedTierSizes',
    'watchedByUser': 'WatchedByUserSizes'
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

function buildStatKpiCard(icon, labelKey, labelFallback, value, detail, extraClass, linkCategory) {
    var cls = 'stat-kpi-card' + (extraClass ? ' ' + extraClass : '') + (linkCategory ? ' stat-kpi-card-linked' : '');
    var linkAttr = linkCategory ? ' data-link-category="' + escAttr(linkCategory) + '" role="button" tabindex="0"' : '';
    return '<div class="' + cls + '"' + linkAttr + '><h3>' + mi(icon) + escHtml(T(labelKey, labelFallback)) + '</h3><p class="stat-kpi-value">' + escHtml(value) + '</p><p class="stat-kpi-detail">' + escHtml(detail) + '</p></div>';
}

function buildFreedKpiCardsHtml() {
    if (!_statsCleanupCache) {
        return buildStatKpiCard('cleaning_services', 'totalBytesFreed', 'Total Space Freed', '—', T('loadingInsights', 'Loading…'), 'stat-kpi-freed')
            + buildStatKpiCard('delete', 'totalItemsDeleted', 'Total Items Deleted', '—', T('loadingInsights', 'Loading…'), 'stat-kpi-freed');
    }
    var freed = formatBytes(_statsCleanupCache.TotalBytesFreed || 0);
    var count = _statsCleanupCache.TotalItemsDeleted || 0;
    var tsRaw = _statsCleanupCache.LastCleanupTimestamp;
    var parsed = tsRaw ? new Date(tsRaw) : null;
    var valid = parsed && tsRaw !== '0001-01-01T00:00:00' && !isNaN(parsed.getTime());
    var last = valid ? parsed.toLocaleString() : T('never', 'Never');
    return buildStatKpiCard('cleaning_services', 'totalBytesFreed', 'Total Space Freed', freed, T('lastCleanup', 'Last cleanup') + ': ' + last, 'stat-kpi-freed')
        + buildStatKpiCard('delete', 'totalItemsDeleted', 'Total Items Deleted', String(count), T('lastCleanup', 'Last cleanup') + ': ' + last, 'stat-kpi-freed');
}

function buildKpiStripHtml() {
    var libs = (_lastStatisticsData && _lastStatisticsData.Libraries) || [];
    var videoFiles = 0, audioFiles = 0, bookFiles = 0, trickplayFolders = 0;
    var totalVideoSize = 0, totalAudioSize = 0, totalBookSize = 0, totalTrickplaySize = 0;
    for (var i = 0; i < libs.length; i++) {
        videoFiles += libs[i].VideoFileCount || 0;
        audioFiles += libs[i].AudioFileCount || 0;
        bookFiles += libs[i].BookFileCount || 0;
        trickplayFolders += libs[i].TrickplayFolderCount || 0;
        totalVideoSize += libs[i].VideoSize || 0;
        totalAudioSize += libs[i].AudioSize || 0;
        totalBookSize += libs[i].BookSize || 0;
        totalTrickplaySize += libs[i].TrickplaySize || 0;
    }
    var totalFiles = videoFiles + audioFiles + bookFiles;

    var cards = [];
    cards.push(buildStatKpiCard('description', 'totalFiles', 'Total Files', String(totalFiles), videoFiles + ' ' + T('video', 'video') + ', ' + audioFiles + ' ' + T('audio', 'audio') + (bookFiles > 0 ? ', ' + bookFiles + ' ' + T('books', 'books') : '')));
    if (totalVideoSize > 0) cards.push(buildStatKpiCard('movie', 'video', 'Video', formatBytes(totalVideoSize), videoFiles + ' ' + T('files', 'files'), null, 'video'));
    if (totalAudioSize > 0) cards.push(buildStatKpiCard('music_note', 'audio', 'Audio', formatBytes(totalAudioSize), audioFiles + ' ' + T('files', 'files'), null, 'audio'));
    if (totalBookSize > 0) cards.push(buildStatKpiCard('library_books', 'books', 'Books', formatBytes(totalBookSize), bookFiles + ' ' + T('files', 'files'), null, 'books'));
    if (totalTrickplaySize > 0) cards.push(buildStatKpiCard('image', 'trickplay', 'Trickplay', formatBytes(totalTrickplaySize), trickplayFolders + ' ' + T('folders', 'folders')));
    cards.push(buildFreedKpiCardsHtml());

    return '<div class="stat-kpi-strip">' + cards.join('') + '</div>';
}

function refreshFreedSummary() {
    apiGet('JellyfinHelper/CleanupStatistics', function (stats) {
        _statsCleanupCache = stats;
        var strip = document.getElementById('statKpiWrap');
        if (strip) strip.innerHTML = buildKpiStripHtml();
    }, function () {
        // Keep the "Loading…" placeholder; a failed fetch should not error out the whole tab.
    });
}

function toggleFilter(scopeKey, dimension, value) {
    var state = getExplorerState(scopeKey);
    if (!state.activeFilters[dimension]) state.activeFilters[dimension] = {};
    var wasActive = !!state.activeFilters[dimension][value];
    if (wasActive) {
        delete state.activeFilters[dimension][value];
        if (Object.keys(state.activeFilters[dimension]).length === 0) delete state.activeFilters[dimension];
    } else {
        state.activeFilters[dimension][value] = true;
    }
    // Selecting a value keeps its donut section open and focuses the results panel on that
    // single value's file tree, scoped to this explorer instance only.
    state.expandedDonutDims[dimension] = true;
    if (wasActive) {
        if (state.activeTreeSelection && state.activeTreeSelection.dimension === dimension && state.activeTreeSelection.value === value) {
            state.activeTreeSelection = null;
        }
    } else {
        state.activeTreeSelection = { dimension: dimension, value: value };
    }
    state.resultOffset = 0;
    renderStatisticsChrome();
}

function clearAllFilters(scopeKey) {
    var state = getExplorerState(scopeKey);
    state.activeFilters = {};
    state.activeTreeSelection = null;
    state.resultOffset = 0;
    renderStatisticsChrome();
}

function collectPathsFromLibsDict(libs, dictName, seen, result) {
    for (var li = 0; li < libs.length; li++) {
        var dict = libs[li][dictName];
        if (!dict) continue;
        for (var key in dict) {
            if (!Object.hasOwn(dict, key)) continue;
            var arr = dict[key];
            for (var j = 0; j < arr.length; j++) {
                if (!seen[arr[j]]) { seen[arr[j]] = true; result.push(arr[j]); }
            }
        }
    }
}

function computeFilteredPaths(scopeKey) {
    var data = getScopedData(scopeKey);
    if (!data) return [];
    var state = getExplorerState(scopeKey);
    var dims = Object.keys(state.activeFilters);
    var libs = data.Libraries || [];
    if (dims.length === 0) {
        // Different library types populate different "primary" dictionaries, so try them in
        // relevance order and iterate every library in scope, not just the first.
        var seen = {};
        var result = [];
        collectPathsFromLibsDict(libs, 'WatchedTierPaths', seen, result);
        if (result.length === 0) collectPathsFromLibsDict(libs, 'ContainerFormatPaths', seen, result);
        if (result.length === 0) collectPathsFromLibsDict(libs, 'MusicAudioCodecPaths', seen, result);
        if (result.length === 0) collectPathsFromLibsDict(libs, 'BookFormatPaths', seen, result);
        return result.sort();
    }
    var sets = [];
    for (var d2 = 0; d2 < dims.length; d2++) {
        var dimension = dims[d2];
        var values = Object.keys(state.activeFilters[dimension]);
        var pathsProp = STATISTICS_PATH_MAP[dimension];
        var union = {};
        for (var v = 0; v < values.length; v++) {
            var direct = collectDictPaths(libs, pathsProp, values[v]);
            for (var pp = 0; pp < direct.length; pp++) union[direct[pp]] = true;
        }
        sets.push(new Set(Object.keys(union)));
    }
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

function computeScopeTotalFileCount(scopeKey) {
    var state = getExplorerState(scopeKey);
    var saved = state.activeFilters;
    state.activeFilters = {};
    var total = computeFilteredPaths(scopeKey).length;
    state.activeFilters = saved;
    return total;
}

function isDimensionRelevant(scopeKey, categoryMap) {
    if (scopeKey === SCOPE_ALL) return true;
    var lib = getScopedLib(scopeKey);
    if (!lib) return true;
    return !!categoryMap[classifyCollectionType(lib.CollectionType)];
}

function computeCountsWithinSelection(scopeKey, dimension) {
    var state = getExplorerState(scopeKey);
    var filtered = computeFilteredPaths(scopeKey);
    var data = getScopedData(scopeKey);
    if (!data) return {};
    var libs = data.Libraries || [];
    if (filtered.length === 0 && Object.keys(state.activeFilters).length === 0) {
        var prop = dimension === 'watched' ? 'WatchedTiers'
            : dimension === 'audioLanguages' ? 'AudioLanguages'
                : dimension === 'subtitleLanguages' ? 'SubtitleLanguages'
                    : dimension === 'watchedByUser' ? 'WatchedByUserPaths'
                        : STATISTICS_COUNT_PROP_MAP[dimension];
        if (!prop) return {};
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
    // Filtered: count occurrences of each value that appears in the filtered set.
    var pathsProp = STATISTICS_PATH_MAP[dimension];
    var reverse = {};
    for (var li = 0; li < libs.length; li++) {
        var pDict = libs[li][pathsProp];
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
        var cnt2 = 0;
        var map = reverse[vv];
        for (var fp in map) if (filteredSet[fp]) cnt2++;
        if (cnt2 > 0) counts[vv] = cnt2;
    }
    return counts;
}

function getSizeMapForDimension(scopeKey, dimension) {
    var data = getScopedData(scopeKey);
    if (!data) return {};
    var sp = STATISTICS_SIZE_PROP_MAP[dimension];
    if (!sp) return {};
    var libs = data.Libraries || [];
    var agg = {};
    for (var i = 0; i < libs.length; i++) {
        var dict = libs[i][sp];
        if (!dict) continue;
        for (var k in dict) if (Object.hasOwn(dict, k)) agg[k] = (agg[k] || 0) + dict[k];
    }
    return agg;
}

function buildFilterStripHtml(scopeKey) {
    var state = getExplorerState(scopeKey);
    var keys = Object.keys(state.activeFilters);
    if (keys.length === 0) return '';
    var filteredCount = computeFilteredPaths(scopeKey).length;
    var totalCount = computeScopeTotalFileCount(scopeKey);
    var html = '<div class="stat-filter-strip"><span class="stat-filter-label">' + escHtml(T('statFilterStripLabel', 'Active')) + ':</span>';
    for (var di = 0; di < keys.length; di++) {
        var dim = keys[di];
        var vals = Object.keys(state.activeFilters[dim]);
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

// Builds the { movies, tvShows, music, books, other, rootPaths } shape expected by the shared
// renderFileTree() from a keyed paths-dictionary property (e.g. "VideoCodecPaths") + a value
// (e.g. "HEVC"), scoped to whatever explorer instance requested it.
function collectStatCodecPaths(data, pathsProp, value) {
    var d = data || {};
    return {
        movies: collectDictPaths(d.Movies || [], pathsProp, value),
        tvShows: collectDictPaths(d.TvShows || [], pathsProp, value),
        music: collectDictPaths(d.Music || [], pathsProp, value),
        books: collectDictPaths(d.Books || [], pathsProp, value),
        other: collectDictPaths(d.Other || [], pathsProp, value),
        rootPaths: {
            movies: d.MovieRootPaths || [],
            tvShows: d.TvShowRootPaths || [],
            music: d.MusicRootPaths || [],
            books: d.BookRootPaths || [],
            other: d.OtherRootPaths || []
        }
    };
}

function buildDonutSectionHtml(scopeKey, dimension) {
    var meta = STATISTICS_DIMENSIONS.find(function (d) { return d.id === dimension; });
    if (!meta) return '';
    var state = getExplorerState(scopeKey);
    var categoryMap = STATISTICS_CATEGORY_MAP[dimension];
    var relevant = isDimensionRelevant(scopeKey, categoryMap);
    var counts = relevant ? computeCountsWithinSelection(scopeKey, dimension) : {};
    var sizes = relevant ? getSizeMapForDimension(scopeKey, dimension) : {};
    var total = 0;
    for (var k in counts) if (Object.hasOwn(counts, k)) total += counts[k];
    var dimmed = !relevant ? ' stat-donut-dimmed' : '';
    var hint = !relevant ? '<span class="stat-donut-hint">' + escHtml(T('noData', 'No data in this view')) + '</span>' : '';
    var isExpanded = !!state.expandedDonutDims[dimension];
    var treeValue = state.activeTreeSelection && state.activeTreeSelection.dimension === dimension ? state.activeTreeSelection.value : null;
    var bodyId = 'stat-donut-body-' + escAttr(scopeKey) + '-' + escAttr(dimension);
    var html = '<div class="stat-donut-section' + dimmed + '" data-dimension="' + escAttr(dimension) + '">';
    html += '<button class="stat-donut-header" aria-expanded="' + (isExpanded ? 'true' : 'false') + '" aria-controls="' + bodyId + '"><span class="stat-donut-title">' + mi(meta.icon) + escHtml(T(meta.labelKey, meta.fallback)) + '</span><span class="stat-donut-count">' + escHtml(String(total)) + '</span><span class="stat-donut-chevron">' + mi('expand_more') + '</span>' + hint + '</button>';
    html += '<div class="stat-donut-body" id="' + bodyId + '"' + (isExpanded ? '' : ' hidden') + '>';
    if (!relevant) {
        html += '<p class="stat-donut-empty">' + escHtml(T('noData', 'No data in this view')) + '</p>';
    } else if (total === 0) {
        html += '<p class="stat-donut-empty">' + escHtml(T('noData', 'No data')) + '</p>';
    } else {
        var chartId = 'stat_' + scopeKey + '_' + dimension;
        var libs = (getScopedData(scopeKey) || {}).Libraries || [];
        var countProp = STATISTICS_COUNT_PROP_MAP[dimension];
        var svg = typeof renderDonutSvg === 'function'
            ? renderDonutSvg(counts, libs, countProp, chartId)
            : '<p style="opacity:0.5;">' + escHtml(T('noData', 'No data')) + '</p>';
        html += svg;
        var entries = [];
        for (var kk in counts) if (Object.hasOwn(counts, kk)) entries.push({ label: kk, count: counts[kk], size: sizes[kk] || 0 });
        entries.sort(function (a, b) { return b.count - a.count; });
        html += '<div class="stat-breakdown">';
        for (var e = 0; e < entries.length; e++) {
            var ent = entries[e];
            var pct = total > 0 ? (ent.count / total * 100).toFixed(1) : '0';
            var isActive = state.activeFilters[dimension] && state.activeFilters[dimension][ent.label];
            var isTreeOpen = treeValue === ent.label;
            var color = (typeof DONUT_COLORS !== 'undefined' ? DONUT_COLORS[e % DONUT_COLORS.length] : '#00a4dc');
            html += '<button class="stat-breakdown-row' + (isActive ? ' stat-breakdown-active' : '') + (isTreeOpen ? ' stat-breakdown-tree-open' : '') + '" data-dimension="' + escAttr(dimension) + '" data-value="' + escAttr(ent.label) + '" role="button" aria-pressed="' + (isActive ? 'true' : 'false') + '">';
            html += '<span class="stat-breakdown-color" style="background:' + escAttr(color) + '"></span>';
            html += '<span class="stat-breakdown-info"><span class="stat-breakdown-name">' + escHtml(ent.label) + '</span><span class="stat-breakdown-stats">' + escHtml(String(ent.count)) + ' ' + escHtml(T('files', 'files')) + ' · ' + escHtml(pct) + '% · ' + escHtml(formatBytes(ent.size)) + '</span></span>';
            html += '<span class="stat-breakdown-bar"><span class="stat-breakdown-bar-fill" style="width:' + escAttr(pct) + '%;background:' + escAttr(color) + '"></span></span>';
            html += '</button>';
        }
        html += '</div>';
    }
    html += '</div></div>';
    return html;
}

function buildDonutPanelHtml(scopeKey) {
    var html = '<div class="stat-donut-panel">';
    var data = getScopedData(scopeKey);
    var libs = (data && data.Libraries) || [];
    for (var i = 0; i < STATISTICS_DIMENSIONS.length; i++) {
        var dim = STATISTICS_DIMENSIONS[i].id;
        // Hide Music/Book sections entirely when the whole scope has no such files, rather than
        // showing an always-empty donut.
        if (dim === 'musicAudioCodecs' || dim === 'bookFormats') {
            var propCheck = dim === 'musicAudioCodecs' ? 'MusicAudioCodecs' : 'BookFormats';
            var hasData = false;
            for (var li = 0; li < libs.length; li++) {
                if (libs[li][propCheck] && Object.keys(libs[li][propCheck]).length > 0) { hasData = true; break; }
            }
            if (!hasData) continue;
        }
        html += buildDonutSectionHtml(scopeKey, dim);
    }
    html += '</div>';
    return html;
}

function buildWatchedByUserFilterHtml(scopeKey) {
    var data = getScopedData(scopeKey);
    if (!data) return '';
    var libs = data.Libraries || [];
    if (!isDimensionRelevant(scopeKey, STATISTICS_CATEGORY_MAP.watchedByUser)) return '';
    var state = getExplorerState(scopeKey);
    var hasAnyFilter = Object.keys(state.activeFilters).length > 0;
    var filteredPaths = hasAnyFilter ? computeFilteredPaths(scopeKey) : null;
    var filteredSet = null;
    if (filteredPaths) {
        filteredSet = {};
        for (var fi = 0; fi < filteredPaths.length; fi++) filteredSet[filteredPaths[fi]] = true;
    }
    var userCounts = {};
    var userSizes = {};
    for (var li2 = 0; li2 < libs.length; li2++) {
        var dict2 = libs[li2].WatchedByUserPaths;
        var sizes2 = libs[li2].WatchedByUserSizes;
        if (!dict2) continue;
        for (var user2 in dict2) {
            if (!Object.hasOwn(dict2, user2)) continue;
            var arr2 = dict2[user2] || [];
            var cnt = 0;
            for (var ai = 0; ai < arr2.length; ai++) {
                if (!filteredSet || filteredSet[arr2[ai]]) cnt++;
            }
            if (cnt > 0) {
                userCounts[user2] = (userCounts[user2] || 0) + cnt;
                if (sizes2 && sizes2[user2] != null) {
                    var ratio = arr2.length ? cnt / arr2.length : 1;
                    userSizes[user2] = (userSizes[user2] || 0) + Math.round(sizes2[user2] * ratio);
                }
            }
        }
    }
    var users = Object.keys(userCounts).sort(function (a, b) { return userCounts[b] - userCounts[a]; });
    if (users.length === 0) return '';
    var isExpanded = !!state.expandedDonutDims.watchedByUser;
    var treeValue = state.activeTreeSelection && state.activeTreeSelection.dimension === 'watchedByUser' ? state.activeTreeSelection.value : null;
    var bodyId = 'stat-watchedByUser-body-' + escAttr(scopeKey);
    var html = '<div class="stat-donut-section" data-dimension="watchedByUser">';
    html += '<button class="stat-donut-header" aria-expanded="' + (isExpanded ? 'true' : 'false') + '" aria-controls="' + bodyId + '"><span class="stat-donut-title">' + mi('group') + escHtml(T('watchedBy', 'Watched by')) + '</span><span class="stat-donut-count">' + escHtml(String(users.length)) + '</span><span class="stat-donut-chevron">' + mi('expand_more') + '</span></button>';
    html += '<div class="stat-donut-body" id="' + bodyId + '"' + (isExpanded ? '' : ' hidden') + '>';
    html += '<div class="stat-breakdown">';
    for (var i = 0; i < users.length; i++) {
        var u = users[i];
        var cnt2 = userCounts[u];
        var size = userSizes[u] || 0;
        var isActive = state.activeFilters.watchedByUser && state.activeFilters.watchedByUser[u];
        var isTreeOpen = treeValue === u;
        var color = (typeof DONUT_COLORS !== 'undefined' ? DONUT_COLORS[i % DONUT_COLORS.length] : '#00a4dc');
        html += '<button class="stat-breakdown-row' + (isActive ? ' stat-breakdown-active' : '') + (isTreeOpen ? ' stat-breakdown-tree-open' : '') + '" data-dimension="watchedByUser" data-value="' + escAttr(u) + '" role="button" aria-pressed="' + (isActive ? 'true' : 'false') + '">';
        html += '<span class="stat-breakdown-color" style="background:' + escAttr(color) + '"></span>';
        html += '<span class="stat-breakdown-info"><span class="stat-breakdown-name">' + escHtml(u) + '</span><span class="stat-breakdown-stats">' + escHtml(String(cnt2)) + ' ' + escHtml(T('files', 'files')) + (size ? ' \u00b7 ' + escHtml(formatBytes(size)) : '') + '</span></span>';
        html += '</button>';
    }
    html += '</div></div></div>';
    return html;
}

function attachDonutHoverTooltips() {
    var containers = document.querySelectorAll('.donut-container');
    for (var ci = 0; ci < containers.length; ci++) {
        (function (container) {
            var paths = container.querySelectorAll('.donut-segment path');
            var tooltip = container.querySelector('.donut-tooltip');
            if (!tooltip) return;
            for (var pi = 0; pi < paths.length; pi++) {
                (function (path) {
                    var seg = path.closest('.donut-segment');
                    if (!seg) return;
                    var segId = seg.getAttribute('data-segment-id');
                    var data = _statDonutTooltipData[segId];
                    if (!data) return;
                    path.addEventListener('mouseenter', function (e) {
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
                    path.addEventListener('mouseleave', function () { tooltip.classList.remove('visible'); });
                })(paths[pi]);
            }
        })(containers[ci]);
    }
}

function collectResolutionDimensionsMap(scopeKey) {
    var d = getScopedData(scopeKey);
    if (!d) return {};
    var merged = {};
    var libs = d.Libraries || [];
    for (var i = 0; i < libs.length; i++) {
        var dims = libs[i].ResolutionDimensions;
        if (!dims) continue;
        for (var p in dims) if (Object.hasOwn(dims, p)) merged[p] = dims[p];
    }
    return merged;
}

function buildFileDetail(path) {
    var d = _lastStatisticsData;
    if (!d) return '';
    var libs = d.Libraries || [];
    var info = { path: path, videoCodec: null, container: null, resolution: null, resolutionDim: null, bitrate: null, dynamicRange: null, audioCodec: null, audioLangs: [], subtitleLangs: [], watchedBy: [], watchedDetails: [] };
    var dimMap = collectResolutionDimensionsMap(SCOPE_ALL);
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
        var details = lib.WatchedDetails ? lib.WatchedDetails[path] : null;
        if (details && details.length) {
            info.watchedDetails = details;
            var names = [];
            for (var di = 0; di < details.length; di++) {
                var uname = details[di].username || details[di].Username || '';
                if (uname) names.push(uname);
            }
            info.watchedBy = names;
        } else if (lib.WatchedByUsers && lib.WatchedByUsers[path]) {
            info.watchedBy = lib.WatchedByUsers[path];
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

    var watchedCount = info.watchedDetails.length || info.watchedBy.length || 0;
    html += '<div class="stat-detail-row stat-detail-watched"><span class="stat-detail-label">' + escHtml(T('watched', 'Watched')) + '</span><span class="stat-detail-value">';
    if (watchedCount === 0) {
        html += escHtml(T('never', 'Never watched'));
    } else {
        html += '<button class="stat-watched-toggle" aria-expanded="false">' + mi('visibility') + escHtml(String(watchedCount)) + ' ' + escHtml(T('watched', 'Watched')) + ' \u25be</button>';
        html += '<div class="stat-watched-details" hidden>';
        if (info.watchedDetails.length) {
            for (var wi = 0; wi < info.watchedDetails.length; wi++) {
                var det = info.watchedDetails[wi];
                var unameRaw = det.username || det.Username || '';
                var uname = escHtml(unameRaw);
                var pc = det.playCount != null ? det.playCount : (det.PlayCount != null ? det.PlayCount : 1);
                var lpdRaw = det.lastPlayedDate || det.LastPlayedDate || det.lastPlayed || null;
                var when;
                if (lpdRaw) {
                    try { when = formatTimeAgo(lpdRaw) || new Date(lpdRaw).toLocaleString(); } catch (e) { when = String(lpdRaw); }
                } else {
                    when = T('statUnknownDate', 'Unknown date');
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

// Wires the watched-details toggle and the codec/language chips inside a rendered
// buildFileDetail() block. scopeKey routes chip clicks back to the correct explorer instance.
function bindFileDetailInteractions(container, scopeKey) {
    var watchedToggles = container.querySelectorAll('.stat-watched-toggle');
    for (var wt = 0; wt < watchedToggles.length; wt++) {
        watchedToggles[wt].addEventListener('click', function (ev) {
            ev.stopPropagation();
            var exp = this.getAttribute('aria-expanded') === 'true';
            this.setAttribute('aria-expanded', exp ? 'false' : 'true');
            var details = this.nextElementSibling;
            if (details) details.hidden = exp;
            this.innerHTML = this.innerHTML.replace(exp ? '\u25b2' : '\u25be', exp ? '\u25be' : '\u25b2');
        });
    }
    var chips = container.querySelectorAll('.stat-chip');
    for (var c = 0; c < chips.length; c++) {
        chips[c].addEventListener('click', function (ev) {
            ev.stopPropagation();
            toggleFilter(scopeKey, this.dataset.dimension, this.dataset.value);
        });
    }
}

function renderCuratedDefaults(scopeKey) {
    var libs = (getScopedData(scopeKey) || {}).Libraries || [];
    var watchedEligible = isDimensionRelevant(scopeKey, STATISTICS_CATEGORY_MAP.watched);
    var neverWatched = [];
    var largest = [];
    for (var li = 0; li < libs.length; li++) {
        if (watchedEligible) {
            var wt = libs[li].WatchedTierPaths && libs[li].WatchedTierPaths['Never watched'];
            if (wt) for (var n = 0; n < wt.length; n++) neverWatched.push(wt[n]);
        }
        var sizes = libs[li].FileSizes;
        if (sizes) {
            for (var sp in sizes) if (Object.hasOwn(sizes, sp)) largest.push({ path: sp, size: sizes[sp] });
        }
    }
    largest.sort(function (a, b) { return b.size - a.size; });
    var html = '<div class="stat-curated">';
    html += '<p class="stat-curated-hint">' + escHtml(T('statNoFiltersHint', 'Select a filter to explore your library.')) + '</p>';
    html += '<div class="stat-curated-cols">';
    html += '<div class="stat-curated-col"><h4>' + escHtml(T('statTopLargest', 'Top 5 largest files')) + '</h4>';
    if (largest.length === 0) html += '<p style="opacity:0.5;">' + escHtml(T('noData', 'No data')) + '</p>';
    else {
        for (var lgi = 0; lgi < Math.min(5, largest.length); lgi++) {
            var lp = largest[lgi].path;
            var lnm = lp.split('/').pop().split('\\').pop() || lp;
            html += '<div class="stat-curated-item" title="' + escAttr(lp) + '"><span class="stat-curated-name">' + escHtml(lnm) + '</span><span class="stat-curated-meta">' + escHtml(formatBytes(largest[lgi].size)) + '</span></div>';
        }
    }
    html += '</div>';
    if (watchedEligible) {
        html += '<div class="stat-curated-col"><h4>' + escHtml(T('statTopNeverWatched', 'Top 5 never-watched')) + '</h4>';
        if (neverWatched.length === 0) html += '<p style="opacity:0.5;">' + escHtml(T('noData', 'No data')) + '</p>';
        else {
            for (var b = 0; b < Math.min(5, neverWatched.length); b++) {
                var pp = neverWatched[b];
                var nm = pp.split('/').pop().split('\\').pop() || pp;
                html += '<div class="stat-curated-item" title="' + escAttr(pp) + '"><span class="stat-curated-name">' + escHtml(nm) + '</span></div>';
            }
        }
        html += '</div>';
    }
    html += '</div></div>';
    return html;
}

function renderTreeResultsView(container, scopeKey) {
    var state = getExplorerState(scopeKey);
    var sel = state.activeTreeSelection;
    var data = getScopedData(scopeKey);
    var pathsProp = STATISTICS_PATH_MAP[sel.dimension];
    var treeData = collectStatCodecPaths(data, pathsProp, sel.value);
    var treeMeta = sel.dimension === 'resolutions' ? collectResolutionDimensionsMap(scopeKey) : null;
    var totalFiles = treeData.movies.length + treeData.tvShows.length + treeData.music.length + treeData.books.length + treeData.other.length;
    var html = '<div class="stat-tree-view">';
    html += '<div class="stat-results-header">' + escHtml(String(totalFiles)) + ' ' + escHtml(T('files', 'files')) + '</div>';
    if (totalFiles === 0) {
        html += '<p class="stat-no-results">' + escHtml(T('noFilesFound', 'No files found.')) + '</p>';
    } else {
        html += '<div class="file-tree-panel file-tree-panel-visible" id="statTree_' + escAttr(scopeKey) + '">' + renderFileTree(treeData, sel.value, treeMeta) + '</div>';
    }
    html += '<div class="stat-file-detail-wrap" id="statTreeDetail_' + escAttr(scopeKey) + '" hidden></div>';
    html += '</div>';
    container.innerHTML = html;

    var treeContainer = document.getElementById('statTree_' + scopeKey);
    if (!treeContainer) return;
    if (typeof bindFileTreeHandlers === 'function') bindFileTreeHandlers(treeContainer);

    var detailWrap = document.getElementById('statTreeDetail_' + scopeKey);
    var leaves = treeContainer.querySelectorAll('.tree-leaf');
    for (var li = 0; li < leaves.length; li++) {
        leaves[li].setAttribute('tabindex', '0');
        leaves[li].setAttribute('role', 'button');
        leaves[li].addEventListener('click', function () {
            var path = this.getAttribute('title');
            var allLeaves = treeContainer.querySelectorAll('.tree-leaf');
            var alreadySelected = this.classList.contains('tree-leaf-selected');
            for (var k = 0; k < allLeaves.length; k++) allLeaves[k].classList.remove('tree-leaf-selected');
            if (alreadySelected) {
                if (detailWrap) { detailWrap.hidden = true; detailWrap.innerHTML = ''; }
                return;
            }
            this.classList.add('tree-leaf-selected');
            if (detailWrap) {
                detailWrap.innerHTML = buildFileDetail(path);
                detailWrap.hidden = false;
                bindFileDetailInteractions(detailWrap, scopeKey);
            }
        });
        leaves[li].addEventListener('keydown', function (ev) {
            if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); this.click(); }
        });
    }
}

function renderFilteredListView(container, scopeKey) {
    var state = getExplorerState(scopeKey);
    var filtered = computeFilteredPaths(scopeKey);
    var offset = state.resultOffset || 0;
    var page = filtered.slice(offset, offset + STATISTICS_PAGE_SIZE);
    if (filtered.length === 0) {
        container.innerHTML = '<p class="stat-no-results">' + escHtml(T('noFilesFound', 'No files found.')) + ' <button class="stat-link" data-action="clearAll">' + escHtml(T('statResetAll', 'Reset all')) + '</button></p>';
        var clr = container.querySelector('[data-action="clearAll"]');
        if (clr) clr.addEventListener('click', function () { clearAllFilters(scopeKey); });
        return;
    }
    var html = '<div class="stat-results-header">' + escHtml(String(filtered.length)) + ' ' + escHtml(T('files', 'files')) + '</div>';
    html += '<div class="stat-results-list">';
    for (var i = 0; i < page.length; i++) {
        var path = page[i];
        var fileName = path.split('/').pop().split('\\').pop() || path;
        html += '<div class="stat-file-entry" data-path="' + escAttr(path) + '" role="button" tabindex="0" aria-expanded="false"><div class="stat-file-row"><span class="stat-file-icon">' + mi('description') + '</span><span class="stat-file-name">' + escHtml(fileName) + '</span><span class="stat-file-path-hint" title="' + escAttr(path) + '">' + escHtml(path) + '</span><span class="stat-file-chevron">' + mi('expand_more') + '</span></div><div class="stat-file-detail-wrap" hidden></div></div>';
    }
    html += '</div>';
    if (offset + STATISTICS_PAGE_SIZE < filtered.length) {
        var remaining = filtered.length - (offset + STATISTICS_PAGE_SIZE);
        html += '<button class="stat-load-more" data-action="loadMore">' + escHtml(T('loadMore', 'Load more')) + ' (' + escHtml(String(remaining)) + ')</button>';
    }
    container.innerHTML = html;

    var entries = container.querySelectorAll('.stat-file-entry');
    for (var e = 0; e < entries.length; e++) {
        entries[e].addEventListener('click', function () {
            var expanded = this.getAttribute('aria-expanded') === 'true';
            var all = container.querySelectorAll('.stat-file-entry');
            for (var k = 0; k < all.length; k++) {
                if (all[k] !== this) {
                    all[k].setAttribute('aria-expanded', 'false');
                    var wrap2 = all[k].querySelector('.stat-file-detail-wrap');
                    if (wrap2) { wrap2.hidden = true; wrap2.innerHTML = ''; }
                }
            }
            this.setAttribute('aria-expanded', expanded ? 'false' : 'true');
            var w = this.querySelector('.stat-file-detail-wrap');
            if (expanded) {
                if (w) { w.hidden = true; w.innerHTML = ''; }
            } else if (w) {
                w.innerHTML = buildFileDetail(this.dataset.path);
                w.hidden = false;
                bindFileDetailInteractions(w, scopeKey);
            }
        });
        entries[e].addEventListener('keydown', function (ev) {
            if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); this.click(); }
        });
    }
    var more = container.querySelector('[data-action="loadMore"]');
    if (more) more.addEventListener('click', function () { state.resultOffset += STATISTICS_PAGE_SIZE; renderResultsPanel(scopeKey); });
    var clearBtn = container.querySelectorAll('[data-action="clearAll"]');
    for (var cb = 0; cb < clearBtn.length; cb++) clearBtn[cb].addEventListener('click', function () { clearAllFilters(scopeKey); });
}

function renderResultsPanel(scopeKey) {
    var container = document.getElementById('statResultsPanel_' + scopeKey);
    if (!container) return;
    var state = getExplorerState(scopeKey);

    if (state.activeTreeSelection) {
        var counts = computeCountsWithinSelection(scopeKey, state.activeTreeSelection.dimension);
        if (Object.hasOwn(counts, state.activeTreeSelection.value)) {
            renderTreeResultsView(container, scopeKey);
            return;
        }
        state.activeTreeSelection = null;
    }

    if (Object.keys(state.activeFilters).length === 0) {
        container.innerHTML = renderCuratedDefaults(scopeKey);
        return;
    }
    renderFilteredListView(container, scopeKey);
}

function buildExplorerHtml(scopeKey) {
    var html = '<div class="stat-explorer" data-scope="' + escAttr(scopeKey) + '">';
    html += '<div id="statFilterStrip_' + escAttr(scopeKey) + '">' + buildFilterStripHtml(scopeKey) + '</div>';
    html += '<div class="stat-panel-layout">';
    html += '<div class="stat-filter-panel">' + buildDonutPanelHtml(scopeKey) + buildWatchedByUserFilterHtml(scopeKey) + '</div>';
    html += '<div class="stat-results-panel" id="statResultsPanel_' + escAttr(scopeKey) + '"></div>';
    html += '</div></div>';
    return html;
}

function buildAllExplorerSectionHtml() {
    var html = '<div class="stat-explorer-section">';
    html += '<button class="stat-explorer-header" aria-expanded="' + (_allExplorerExpanded ? 'true' : 'false') + '" aria-controls="statAllExplorerBody"><span class="stat-explorer-title">' + mi('explore') + escHtml(T('statExploreAll', 'All Libraries — Explore & Filter')) + '</span><span class="stat-donut-chevron">' + mi('expand_more') + '</span></button>';
    html += '<div class="stat-explorer-body" id="statAllExplorerBody"' + (_allExplorerExpanded ? '' : ' hidden') + '>';
    if (_allExplorerExpanded) html += buildExplorerHtml(SCOPE_ALL);
    html += '</div></div>';
    return html;
}

function buildLibraryTableRowHtml(lib, index) {
    var scopeKey = libScopeKey(index);
    var isExpanded = !!_expandedLibraryRows[index];
    var bodyId = 'statLibRowBody_' + index;
    var fileCount = (lib.VideoFileCount || 0) + (lib.AudioFileCount || 0) + (lib.BookFileCount || 0);
    var cls = classifyCollectionType(lib.CollectionType);
    var html = '<tr class="stat-lib-table-row" data-lib-index="' + index + '" data-lib-category="' + escAttr(cls) + '" role="button" tabindex="0" aria-expanded="' + (isExpanded ? 'true' : 'false') + '" aria-controls="' + bodyId + '">';
    html += '<td class="stat-lib-table-name">' + escHtml(lib.LibraryName) + '<span class="stat-lib-row-count">' + escHtml(String(fileCount)) + ' ' + escHtml(T('files', 'files')) + '</span></td>';
    html += '<td>' + getCollectionBadgeStat(lib.CollectionType) + '</td>';
    html += '<td>' + escHtml(formatBytes(lib.VideoSize || 0)) + '</td>';
    html += '<td>' + escHtml(formatBytes(lib.AudioSize || 0)) + '</td>';
    html += '<td>' + escHtml(formatBytes(lib.SubtitleSize || 0)) + '</td>';
    html += '<td>' + escHtml(formatBytes(lib.ImageSize || 0)) + '</td>';
    html += '<td>' + escHtml(formatBytes(lib.TrickplaySize || 0)) + '</td>';
    html += '<td class="stat-lib-table-total"><strong>' + escHtml(formatBytes(lib.TotalSize || 0)) + '</strong><span class="stat-donut-chevron">' + mi('expand_more') + '</span></td>';
    html += '</tr>';
    html += '<tr class="stat-lib-table-detail-row" id="' + bodyId + '"' + (isExpanded ? '' : ' hidden') + '><td colspan="7">';
    if (isExpanded) html += buildExplorerHtml(scopeKey);
    html += '</td></tr>';
    return html;
}

function buildStorageBarSectionHtml() {
    var data = _lastStatisticsData;
    if (!data) return '';
    var libs = data.Libraries || [];
    var grandTotal = 0;
    for (var i = 0; i < libs.length; i++) grandTotal += libs[i].TotalSize || 0;
    var html = '<div class="stat-storage-section">';
    html += '<div class="stat-storage-title">' + mi('storage') + escHtml(T('storageDistribution', 'Storage Overview')) + ' · ' + escHtml(formatBytes(grandTotal)) + '</div>';
    html += buildBarSegmentsStat(data);
    html += '</div>';
    return html;
}

function buildPerLibraryBreakdownHtml() {
    var data = _lastStatisticsData;
    if (!data) return '';
    var libs = data.Libraries || [];
    var html = '<div class="stat-lib-breakdown-section">';
    html += '<div class="section-title">' + mi('library_books') + escHtml(T('perLibraryBreakdown', 'Per-Library Breakdown')) + '</div>';
    html += '<div class="library-table-wrapper"><table class="library-table"><thead><tr>';
    html += '<th>' + escHtml(T('library', 'Library')) + '</th><th>' + escHtml(T('type', 'Type')) + '</th><th>' + escHtml(T('video', 'Video')) + '</th><th>' + escHtml(T('audio', 'Audio')) + '</th><th>' + escHtml(T('subtitles', 'Subtitles')) + '</th><th>' + escHtml(T('images', 'Images')) + '</th><th>' + escHtml(T('trickplay', 'Trickplay')) + '</th><th>' + escHtml(T('total', 'Total')) + '</th>';
    html += '</tr></thead><tbody>';
    for (var j = 0; j < libs.length; j++) html += buildLibraryTableRowHtml(libs[j], j);
    html += '</tbody></table></div></div>';
    return html;
}

// One event-delegation pass handles every explorer instance currently in the DOM (the "All
// Libraries" section plus any expanded per-library rows): each interactive element resolves its
// own scope via the nearest [data-scope] ancestor, so no per-instance rebinding is needed.
function attachExplorerHandlers() {
    var headers = document.querySelectorAll('.stat-donut-header');
    for (var i = 0; i < headers.length; i++) {
        headers[i].addEventListener('click', function () {
            var expanded = this.getAttribute('aria-expanded') === 'true';
            this.setAttribute('aria-expanded', expanded ? 'false' : 'true');
            var body = document.getElementById(this.getAttribute('aria-controls'));
            if (body) body.hidden = expanded;
            var scopeEl = this.closest('[data-scope]');
            var section = this.closest('.stat-donut-section');
            var dim = section ? section.dataset.dimension : null;
            if (scopeEl && dim) getExplorerState(scopeEl.dataset.scope).expandedDonutDims[dim] = !expanded;
        });
    }
    var rows = document.querySelectorAll('.stat-breakdown-row');
    for (var r = 0; r < rows.length; r++) {
        rows[r].addEventListener('click', function () {
            var scopeEl = this.closest('[data-scope]');
            if (scopeEl) toggleFilter(scopeEl.dataset.scope, this.dataset.dimension, this.dataset.value);
        });
    }
    var segs = document.querySelectorAll('.stat-donut-body .donut-segment');
    for (var s = 0; s < segs.length; s++) {
        segs[s].addEventListener('click', function () {
            var seg = this.closest('.donut-segment');
            var code = seg ? seg.dataset.codec : null;
            var section = this.closest('.stat-donut-section');
            var scopeEl = this.closest('[data-scope]');
            if (section && code && scopeEl) toggleFilter(scopeEl.dataset.scope, section.dataset.dimension, code);
        });
    }
    var pills = document.querySelectorAll('.stat-filter-pill');
    for (var p = 0; p < pills.length; p++) {
        pills[p].addEventListener('click', function () {
            var scopeEl = this.closest('[data-scope]');
            if (scopeEl) toggleFilter(scopeEl.dataset.scope, this.dataset.dimension, this.dataset.value);
        });
    }
    var clears = document.querySelectorAll('.stat-filter-clear');
    for (var cl = 0; cl < clears.length; cl++) {
        clears[cl].addEventListener('click', function () {
            var scopeEl = this.closest('[data-scope]');
            if (scopeEl) clearAllFilters(scopeEl.dataset.scope);
        });
    }
    if (typeof attachDonutHoverTooltips === 'function') {
        try { attachDonutHoverTooltips(); } catch (e) { /* tooltips are a progressive enhancement */ }
    }
}

function collectExpandedResultScopes() {
    var scopes = [];
    if (_allExplorerExpanded) scopes.push(SCOPE_ALL);
    for (var idx in _expandedLibraryRows) {
        if (Object.hasOwn(_expandedLibraryRows, idx) && _expandedLibraryRows[idx]) scopes.push(libScopeKey(idx));
    }
    return scopes;
}

function attachAllExplorerToggle(container) {
    var header = container.querySelector('.stat-explorer-header');
    if (!header) return;
    header.addEventListener('click', function () {
        _allExplorerExpanded = !_allExplorerExpanded;
        renderStatisticsChrome();
    });
}

function attachLibraryRowToggles(container) {
    var rows = container.querySelectorAll('.stat-lib-table-row');
    for (var i = 0; i < rows.length; i++) {
        rows[i].addEventListener('click', function () {
            var idx = this.dataset.libIndex;
            _expandedLibraryRows[idx] = !_expandedLibraryRows[idx];
            renderStatisticsChrome();
        });
        rows[i].addEventListener('keydown', function (ev) {
            if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); this.click(); }
        });
    }
}

// KPI category cards ("Video" / "Audio" / "Books") jump straight to the matching library rows in
// the Per-Library Breakdown table below and expand them, instead of duplicating the codec/tree
// exploration UI a second time at the top of the page.
var KPI_CATEGORY_TO_LIB_TYPES = {
    video: ['movies', 'tvShows', 'other'],
    audio: ['music'],
    books: ['books']
};

function attachKpiCardLinks(container) {
    var cards = container.querySelectorAll('.stat-kpi-card[data-link-category]');
    for (var i = 0; i < cards.length; i++) {
        cards[i].addEventListener('click', function () {
            var libTypes = KPI_CATEGORY_TO_LIB_TYPES[this.dataset.linkCategory] || [];
            var libs = (_lastStatisticsData && _lastStatisticsData.Libraries) || [];
            var firstMatch = null;
            for (var idx = 0; idx < libs.length; idx++) {
                if (libTypes.indexOf(classifyCollectionType(libs[idx].CollectionType)) === -1) continue;
                _expandedLibraryRows[idx] = true;
                if (firstMatch === null) firstMatch = idx;
            }
            renderStatisticsChrome();
            if (firstMatch !== null) {
                var row = document.querySelector('.stat-lib-table-row[data-lib-index="' + firstMatch + '"]');
                if (row) row.scrollIntoView({ behavior: 'smooth', block: 'center' });
            }
        });
    }
}

function renderStatisticsChrome() {
    var container = document.getElementById('statisticsContent');
    if (!container) return;
    var html = '';
    html += '<div id="statKpiWrap">' + buildKpiStripHtml() + '</div>';
    html += buildStorageBarSectionHtml();
    html += buildAllExplorerSectionHtml();
    html += buildPerLibraryBreakdownHtml();
    container.innerHTML = html;

    attachKpiCardLinks(container);
    attachAllExplorerToggle(container);
    attachLibraryRowToggles(container);
    attachExplorerHandlers();

    var scopes = collectExpandedResultScopes();
    for (var i = 0; i < scopes.length; i++) renderResultsPanel(scopes[i]);
}

function fillStatisticsData(data) {
    _lastStatisticsData = data;
    _explorerStates = {};
    _expandedLibraryRows = {};
    _allExplorerExpanded = false;
    renderStatisticsChrome();
    refreshFreedSummary();
}
