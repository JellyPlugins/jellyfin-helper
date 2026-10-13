'use strict';

// Store last scan data for codec detail clicks
var _lastCodecData = null;

// Tooltip data store - avoids complex string parsing in DOM attributes
var _donutTooltipData = {};

// Track which segment currently shows a tooltip (cleared by tapping outside
// any segment, switching segments, or rescanning)
var _activeTooltipSegmentId = null;

// Guard: prevent duplicate document-level touchstart listener registration
var _touchOutsideListenerAttached = false;

// Canonical codec card order - MUST match CodecCardCatalog.Canonical on the server.
var CANONICAL_CODEC_ORDER = [
    'containers', 'resolutions', 'dynamicRanges', 'videoCodecs', 'videoAudioCodecs',
    'videoBitrate', 'watched', 'audioLanguages', 'subtitleLanguages', 'musicAudioCodecs', 'bookFormats'
];

// Persisted card order (null = not yet loaded from config; fall back to canonical).
var _codecCardOrder = null;

// Guard: load the persisted order from config at most once per page load.
var _codecCardOrderLoaded = false;

// In-flight save guard + queued latest order (exactly one follow-up PUT after the active one).
var _codecOrderSaving = false;
var _codecOrderQueued = null;

// Bumped on every local reorder commit. A config GET that was issued before a commit must not
// apply its (now stale) order over the newer local one; it compares this against its snapshot.
var _codecOrderGeneration = 0;

// Timestamp of last touchend - used to suppress touch-originated click events cross-browser
var _lastTouchEndTime = 0;

// Guard so the row sync listener registers once
var _codecRowSyncBound = false;

// Shared scroll flag owned by Shared.js (same IIFE scope after ComposeConfigPage
// concatenation, so this bare redeclaration binds to the same variable and is a
// no-op at runtime). Declared here so per-file analyzers see an explicit
// declaration at the assignment site below. Must stay a bare `var`: `let` would
// throw "already declared" once concatenated with Shared.js.
var _forceScrollOnPanelOpen;
// Touch companion to the flag above: skips the fresh-panel scroll so tooltip and
// tapped segment stay in view. Same shared-scope mechanics and constraints.
var _suppressScrollOnPanelOpen;

// SVG donut tooltip - reads rich data from _donutTooltipData
function showDonutTooltip(container, evt, segment) {
    var tooltip = container.querySelector('.donut-tooltip');
    if (!tooltip) {
        return;
    }

    var segId = segment.dataset.segmentId;
    var info = _donutTooltipData[segId];
    if (!info) {
        return;
    }

    var html = '<div class="donut-tooltip-header">';
    html += '<span class="donut-tooltip-codec">' + escHtml(info.codec) + '</span>';
    html += '<span class="donut-tooltip-total">' + info.totalCount + ' '
        + (info.totalCount === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files'))) + '</span>';
    html += '</div>';
    html += '<div class="donut-tooltip-pct">' + info.totalPct + '%</div>';

    if (info.libraries.length > 0) {
        html += '<div class="donut-tooltip-divider"></div>';
        html += '<table class="donut-tooltip-table"><tbody>';
        for (var i = 0; i < info.libraries.length; i++) {
            var lib = info.libraries[i];
            html += '<tr>';
            html += '<td class="donut-tooltip-lib">' + escHtml(lib.name) + '</td>';
            html += '<td class="donut-tooltip-count">' + lib.count + ' ' + (lib.count === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files'))) + '</td>';
            html += '</tr>';
        }
        html += '</tbody></table>';
    }

    tooltip.innerHTML = html;
    tooltip.classList.add('visible');

    var containerRect = container.getBoundingClientRect();
    var tooltipX = evt.clientX - containerRect.left + 12;
    var tooltipY = evt.clientY - containerRect.top + 12;

    // Prevent tooltip from overflowing right edge of container
    tooltip.style.left = tooltipX + 'px';
    tooltip.style.top = tooltipY + 'px';

    // After rendering, check if it overflows and adjust
    var tooltipRect = tooltip.getBoundingClientRect();
    if (tooltipRect.right > containerRect.right) {
        tooltipX = evt.clientX - containerRect.left - tooltipRect.width - 12;
        tooltip.style.left = tooltipX + 'px';
    }
    if (tooltipRect.bottom > containerRect.bottom + 50) {
        tooltipY = evt.clientY - containerRect.top - tooltipRect.height - 12;
        tooltip.style.top = tooltipY + 'px';
    }

    // Clamp to viewport edges (especially important on mobile)
    tooltipRect = tooltip.getBoundingClientRect();
    if (tooltipRect.left < 4) {
        tooltip.style.left = (4 - containerRect.left) + 'px';
    }
    if (tooltipRect.top < 4) {
        tooltip.style.top = (4 - containerRect.top) + 'px';
    }
}

function hideDonutTooltip(container) {
    var tooltip = container.querySelector('.donut-tooltip');
    if (tooltip) {
        tooltip.classList.remove('visible');
    }
}

// Whether this segment's drill-down panel is currently open. DOM truth beats
// the tooltip variable: any outside tap or rescan may have cleared the variable
// while the panel stayed open, and only the row knows for sure.
function isSegmentPanelOpen(segment) {
    var chartBox = segment.closest ? segment.closest('.chart-box') : null;
    if (!chartBox || !segment.dataset) {
        return false;
    }
    var codecName = segment.dataset.codec;
    var rows = chartBox.querySelectorAll('.codec-clickable');
    for (var i = 0; i < rows.length; i++) {
        if (rows[i].dataset.codec === codecName) {
            return rows[i].classList.contains('codec-row-active');
        }
    }
    return false;
}

// Trigger the matching codec-row click for a donut segment. Touch passes
// suppressScroll so the tooltip and the tapped segment stay in view.
function triggerCodecRowForSegment(segment, suppressScroll) {
    var chartBox = segment.closest('.chart-box');
    if (!chartBox) {
        return;
    }
    var codecName = segment.dataset.codec;
    if (!codecName) {
        return;
    }
    var rows = chartBox.querySelectorAll('.codec-clickable');
    for (const row of rows) {
        if (row.dataset.codec === codecName) {
            // Force scroll when triggered from donut (user clicked far above the panel),
            // unless the touch path opts out to keep tooltip and segment in view.
            _forceScrollOnPanelOpen = !suppressScroll;
            _suppressScrollOnPanelOpen = !!suppressScroll;
            row.click();
            return;
        }
    }
}

// Helper: compute a point on a circle at a given angle (radians)
function polarToCartesian(cx, cy, radius, angleRad) {
    return {
        x: cx + radius * Math.cos(angleRad),
        y: cy + radius * Math.sin(angleRad)
    };
}

// Helper: build an SVG arc path for a donut segment (annular sector)
function describeArc(cx, cy, outerR, innerR, startAngle, endAngle) {
    var arcSpan = endAngle - startAngle;

    // Full circle: split into two half-arcs (SVG arc command cannot draw 360°)
    if (arcSpan >= 2 * Math.PI - 0.0001) {
        var mid = startAngle + Math.PI;
        return describeArc(cx, cy, outerR, innerR, startAngle, mid)
            + ' ' + describeArc(cx, cy, outerR, innerR, mid, endAngle);
    }

    var largeArc = arcSpan > Math.PI ? 1 : 0;
    var oStart = polarToCartesian(cx, cy, outerR, startAngle);
    var oEnd = polarToCartesian(cx, cy, outerR, endAngle);
    var iStart = polarToCartesian(cx, cy, innerR, startAngle);
    var iEnd = polarToCartesian(cx, cy, innerR, endAngle);

    return 'M ' + oStart.x.toFixed(3) + ' ' + oStart.y.toFixed(3)
        + ' A ' + outerR.toFixed(3) + ' ' + outerR.toFixed(3) + ' 0 ' + largeArc + ' 1 '
        + oEnd.x.toFixed(3) + ' ' + oEnd.y.toFixed(3)
        + ' L ' + iEnd.x.toFixed(3) + ' ' + iEnd.y.toFixed(3)
        + ' A ' + innerR.toFixed(3) + ' ' + innerR.toFixed(3) + ' 0 ' + largeArc + ' 0 '
        + iStart.x.toFixed(3) + ' ' + iStart.y.toFixed(3)
        + ' Z';
}

// SVG donut chart generator (returns only the SVG + container, no legend)
function renderDonutSvg(data, libraries, libraryProperty, chartId) {
    var size = 160;
    var entries = [];
    var total = 0;
    for (var key in data) {
        if (Object.hasOwn(data, key) && data[key] > 0) {
            entries.push({label: key, value: data[key]});
            total += data[key];
        }
    }
    if (total === 0) {
        return '<p style="opacity:0.5;">' + escHtml(T('noData', 'No data')) + '</p>';
    }

    entries.sort(function (a, b) {
        var aUnknown = (a.label || '').toLowerCase() === 'unknown';
        var bUnknown = (b.label || '').toLowerCase() === 'unknown';
        if (aUnknown !== bUnknown) return aUnknown ? 1 : -1;
        return b.value - a.value;
    });

    var cx = size / 2, cy = size / 2, r = size * 0.38, strokeWidth = size * 0.18;
    var outerR = r + strokeWidth / 2;
    var innerR = r - strokeWidth / 2;
    var startAngle = -Math.PI / 2; // 12 o'clock position

    var donutContainer = '<div class="donut-container">';
    donutContainer += '<div class="donut-tooltip" aria-hidden="true"></div>';
    donutContainer += '<svg class="donut-svg" width="' + size + '" height="' + size + '" viewBox="0 0 ' + size + ' '
        + size + '">';
    donutContainer += '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="none" stroke="rgba(255,255,255,0.05)"'
        + ' stroke-width="' + strokeWidth + '"/>';

    for (var i = 0; i < entries.length; i++) {
        var pct = entries[i].value / total;
        var sweepAngle = pct * 2 * Math.PI;
        var endAngle = startAngle + sweepAngle;
        var color = DONUT_COLORS[i % DONUT_COLORS.length];
        var segId = chartId + '_' + i;

        // Build tooltip data for this segment
        var libEntries = [];
        if (libraries && libraries.length > 0) {
            for (var l = 0; l < libraries.length; l++) {
                var lib = libraries[l];
                var libPropertyValue = lib[libraryProperty];
                var libCount = libPropertyValue?.[entries[i].label]
                    ? libPropertyValue[entries[i].label]
                    : 0;
                if (libCount > 0) {
                    libEntries.push({
                        name: lib.LibraryName,
                        count: libCount
                    });
                }
            }
        }

        // Sort library entries by count descending
        libEntries.sort(function (a, b) {
            return b.count - a.count;
        });

        _donutTooltipData[segId] = {
            codec: entries[i].label,
            totalCount: entries[i].value,
            totalPct: (pct * 100).toFixed(1),
            libraries: libEntries
        };

        var arcPath = describeArc(cx, cy, outerR, innerR, startAngle, endAngle);

        donutContainer += '<g class="donut-segment" data-segment-id="' + escAttr(segId) + '"'
            + ' data-codec="' + escAttr(entries[i].label) + '"'
            + ' tabindex="0" role="button" aria-label="' + escAttr(entries[i].label) + '">';
        donutContainer += '<path d="' + arcPath + '" fill="' + color + '"/>';
        donutContainer += '</g>';

        startAngle = endAngle;
    }

    donutContainer += '</svg>';
    donutContainer += '</div>';
    return donutContainer;
}

// Build the clickable codec breakdown table below the donut.
// Long lists collapse after a few rows with a Show all toggle so language
// facets with 20+ entries do not stretch the card. The file-tree panel stays
// outside the list, so expanding rows never nests a tree inside a collapsible.
function renderCodecBreakdown(countDict, sizeDict, chartId) {
    var entries = [];
    var total = 0;
    for (var key in countDict) {
        if (Object.hasOwn(countDict, key) && countDict[key] > 0) {
            var size = (sizeDict?.[key]) ? sizeDict[key] : 0;
            entries.push({label: key, count: countDict[key], size: size});
            total += countDict[key];
        }
    }
    if (entries.length === 0) {
        return '';
    }

    entries.sort(function (a, b) {
        var aUnknown = (a.label || '').toLowerCase() === 'unknown';
        var bUnknown = (b.label || '').toLowerCase() === 'unknown';
        if (aUnknown !== bUnknown) return aUnknown ? 1 : -1;
        return b.count - a.count;
    });

    var collapsible = entries.length > 0;
    var visibleCount = 0;

    var html = '<div class="codec-breakdown">';
    for (var i = 0; i < entries.length; i++) {
        var color = DONUT_COLORS[i % DONUT_COLORS.length];
        var pct = (entries[i].count / total * 100).toFixed(1);
        var hidden = collapsible && i >= visibleCount ? ' style="display:none;" data-breakdown-hidden="1"' : '';
        html += '<div class="codec-row codec-clickable" data-chart="' + escAttr(chartId) + '"' +
            ' data-codec="' + escAttr(entries[i].label) + '" role="button" tabindex="0"' + hidden + '>';
        html += '<div class="codec-row-color" style="background:' + color + '"></div>';
        html += '<div class="codec-row-info">';
        html += '<span class="codec-row-name">' + escHtml(entries[i].label) + '</span>';
        html += '<span class="codec-row-stats">' + entries[i].count + ' ' + escHtml(T('files', 'files')) + ' · '
            + pct + '% · ' + formatBytes(entries[i].size) + '</span>';
        html += '</div>';
        html += '<div class="codec-row-bar"><div class="codec-row-bar-fill" style="width:' + pct + '%;background:'
            + color + '"></div></div>';
        html += '<div class="codec-row-arrow">›</div>';
        html += '</div>';
    }
    if (collapsible) {
        var moreLabel = T('codecShowMore', 'Show all {count}').replace('{count}', String(entries.length));
        html += '<button type="button" class="codec-show-more" data-breakdown-toggle="1" data-expanded="false" data-total="' + entries.length + '">' + escHtml(moreLabel) + '</button>';
    }
    html += '</div>';

    // Detail panel placeholder
    html += '<div class="file-tree-panel" id="codecDetail_' + chartId + '"></div>';

    return html;
}

// Collapsed lists mirror the active row, so switching segments never piles up visible rows.
function syncCollapsedBreakdownRows(evt) {
    var row = evt.target?.closest?.('.codec-breakdown .codec-clickable');
    if (!row) {
        return;
    }
    var breakdowns = document.querySelectorAll('.codec-breakdown');
    for (const box of breakdowns) {
        var toggle = box.querySelector('[data-breakdown-toggle]');
        if (toggle?.dataset.expanded !== 'false') {
            continue;
        }
        var hidden = box.querySelectorAll('[data-breakdown-hidden]');
        for (const r of hidden) {
            r.style.display = r.classList.contains('codec-row-active') ? '' : 'none';
        }
    }
}

function toggleCodecBreakdown(btn) {
    var breakdown = btn.closest ? btn.closest('.codec-breakdown') : null;
    if (!breakdown) return;
    var expanded = btn.dataset.expanded === 'true';
    var hiddenRows = breakdown.querySelectorAll('[data-breakdown-hidden]');
    for (const row of hiddenRows) {
        // The open row stays visible while collapsed so its tree still closes with one click.
        if (expanded && row.classList.contains('codec-row-active')) {
            continue;
        }
        row.style.display = expanded ? 'none' : '';
    }
    btn.dataset.expanded = expanded ? 'false' : 'true';
    if (expanded) {
        var total = btn.dataset.total || '';
        btn.textContent = T('codecShowMore', 'Show all {count}').replace('{count}', total);
    } else {
        btn.textContent = T('codecShowLess', 'Show less');
    }
}

// Render a full chart box with donut + breakdown
function renderDonutChart(countDict, sizeDict, chartId, libraries, libraryProperty) {
    var svgHtml = renderDonutSvg(countDict, libraries, libraryProperty, chartId);
    var breakdownHtml = renderCodecBreakdown(countDict, sizeDict, chartId);
    return svgHtml + breakdownHtml;
}

// Collect paths for a specific codec from libraries, filtered by categories.
// Uses collectDictPaths from Shared.js for the per-library dict lookup.
function collectCodecPaths(data, pathsProp, codecName, categories) {
    var includeMovies = !categories || categories.movies;
    var includeTvShows = !categories || categories.tvShows;
    var includeMusic = !categories || categories.music;
    var includeBooks = !categories || categories.books;
    var includeOther = !categories || categories.other;

    return {
        movies: includeMovies ? collectDictPaths(data.Movies || [], pathsProp, codecName) : [],
        tvShows: includeTvShows ? collectDictPaths(data.TvShows || [], pathsProp, codecName) : [],
        music: includeMusic ? collectDictPaths(data.Music || [], pathsProp, codecName) : [],
        books: includeBooks ? collectDictPaths(data.Books || [], pathsProp, codecName) : [],
        other: includeOther ? collectDictPaths(data.Other || [], pathsProp, codecName) : [],
        rootPaths: {
            movies: data.MovieRootPaths || [],
            tvShows: data.TvShowRootPaths || [],
            music: data.MusicRootPaths || [],
            books: data.BookRootPaths || [],
            other: data.OtherRootPaths || []
        }
    };
}

// Merge the per file language track labels into one lookup, so the audio and
// subtitle drill downs can name the variants behind each collapsed facet value,
// the same way the resolution drill down shows true pixel sizes.
function collectTrackLabelMeta(data, kind) {
    var prop = kind === 'audio' ? 'AudioTrackLabels' : 'SubtitleTrackLabels';
    var merged = {};
    var groups = [data.Movies, data.TvShows, data.Other];
    for (var group of groups) {
        var libs = group || [];
        for (var lib of libs) {
            var labels = lib?.[prop];
            if (!labels) continue;
            for (var path in labels) {
                if (Object.hasOwn(labels, path) && !merged[path] && Array.isArray(labels[path])) {
                    merged[path] = labels[path].join(', ');
                }
            }
        }
    }
    return merged;
}

// Meta map for a drill down panel: true pixel sizes behind resolution tiers,
// track variants behind collapsed language facets, nothing elsewhere.
function collectDrilldownMeta(chartId) {
    if (chartId === 'resolutions') {
        return collectResolutionDimensions(_lastCodecData);
    }
    if (chartId === 'audioLanguages') {
        return collectTrackLabelMeta(_lastCodecData, 'audio');
    }
    if (chartId === 'subtitleLanguages') {
        return collectTrackLabelMeta(_lastCodecData, 'subs');
    }
    return null;
}

// Merge the per-library ResolutionDimensions maps (file path -> "1920x800") from every
// video library into one lookup, so the resolution drill-down can label each file with
// its true pixel size regardless of which library it came from.
function collectResolutionDimensions(data) {
    var merged = {};
    var groups = [data.Movies, data.TvShows, data.Other];
    for (var group of groups) {
        var libs = group || [];
        for (var lib of libs) {
            var dims = lib?.ResolutionDimensions;
            if (!dims) continue;
            for (var path in dims) {
                if (Object.hasOwn(dims, path)) {
                    merged[path] = dims[path];
                }
            }
        }
    }
    return merged;
}

// Per-user watched file counts across libraries. Usernames come from WatchedByUserPaths
// (one entry per file per watching user); the file totals behind them may overlap.
function countWatchedUsers(libraries) {
    var counts = {};
    for (const lib of libraries) {
        var byUser = lib.WatchedByUserPaths;
        if (!byUser) {
            continue;
        }
        for (var user in byUser) {
            if (Object.hasOwn(byUser, user) && byUser[user] && byUser[user].length > 0) {
                counts[user] = (counts[user] || 0) + byUser[user].length;
            }
        }
    }
    return counts;
}

// Per-library tooltip counts for the watched chart: Never watched comes from
// WatchedTiers, usernames from WatchedByUserPaths. Passed as lightweight rows so the
// shared donut renderer needs no watched-specific branch.
function buildWatchedTooltipLibraries(videoLibraries) {
    var rows = [];
    for (const lib of videoLibraries) {
        var merged = {};
        var tiers = lib.WatchedTiers || {};
        if (tiers['Never watched'] > 0) {
            merged['Never watched'] = tiers['Never watched'];
        }
        var byUser = lib.WatchedByUserPaths || {};
        for (var user in byUser) {
            if (Object.hasOwn(byUser, user) && byUser[user] && byUser[user].length > 0) {
                merged[user] = byUser[user].length;
            }
        }
        rows.push({LibraryName: lib.LibraryName, WatchedTooltip: merged});
    }
    return rows;
}

// Map chart IDs to their corresponding path property names
var CODEC_PATH_MAP = {
    'videoCodecs': 'VideoCodecPaths',
    'videoAudioCodecs': 'VideoAudioCodecPaths',
    'musicAudioCodecs': 'MusicAudioCodecPaths',
    'bookFormats': 'BookFormatPaths',
    'containers': 'ContainerFormatPaths',
    'resolutions': 'ResolutionPaths',
    'dynamicRanges': 'DynamicRangePaths',
    'videoBitrate': 'VideoBitrateTierPaths',
    'audioLanguages': 'AudioLanguagePaths',
    'subtitleLanguages': 'SubtitleLanguagePaths',
    'watched': 'WatchedTierPaths'
};

// Map chart IDs to which media categories should be included Video Codecs, Video Audio Codecs, Resolutions, Dynamic Ranges -> only Movies + TV Shows + Other Music Audio Codecs -> only Music Book Formats -> only Books Container Formats -> all libraries (Movies + TV Shows + Music +.
var CODEC_CATEGORY_MAP = {
    'videoCodecs': {movies: true, tvShows: true, music: false, other: true},
    'videoAudioCodecs': {movies: true, tvShows: true, music: false, other: true},
    'musicAudioCodecs': {movies: false, tvShows: false, music: true, other: false},
    'bookFormats': {movies: false, tvShows: false, music: false, other: false, books: true},
    'containers': {movies: true, tvShows: true, music: true, books: true, other: true},
    'resolutions': {movies: true, tvShows: true, music: false, other: true},
    'dynamicRanges': {movies: true, tvShows: true, music: false, other: true},
    'videoBitrate': {movies: true, tvShows: true, music: false, other: true},
    'audioLanguages': {movies: true, tvShows: true, music: false, other: true},
    'subtitleLanguages': {movies: true, tvShows: true, music: false, other: true},
    'watched': {movies: true, tvShows: true, music: false, other: true}
};

// Attach click handlers to codec rows - delegates to shared attachTogglePanelHandlers.
// The panel scope keeps donut drill-downs from wiping the Library Explorer results.
function attachCodecClickHandlers() {
    var toggles = document.querySelectorAll('[data-breakdown-toggle]');
    for (const toggle of toggles) {
        if (toggle.dataset.toggleBound) continue;
        toggle.dataset.toggleBound = '1';
        toggle.addEventListener('click', function () {
            toggleCodecBreakdown(this);
        });
        toggle.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                toggleCodecBreakdown(this);
            }
        });
    }
    // Collapsed visibility follows panel state, so the last closed row hides again.
    if (!_codecRowSyncBound) {
        _codecRowSyncBound = true;
        document.addEventListener('click', syncCollapsedBreakdownRows);
    }
    attachTogglePanelHandlers({
        itemSelector: '.codec-clickable',
        panelScope: '#tab-codecs .charts-row',
        activeClass: 'codec-row-active',
        groupAttr: 'data-chart',
        typeAttr: 'data-codec',
        getPanelId: function (item) {
            return 'codecDetail_' + item.dataset.chart;
        },
        renderContent: function (item) {
            if (!_lastCodecData) {
                return '';
            }
            var chartId = item.dataset.chart;
            var codecName = item.dataset.codec;
            var pathsProp = CODEC_PATH_MAP[chartId];
            // Watched slices span two maps: Never watched lives in WatchedTierPaths,
            // usernames in WatchedByUserPaths.
            if (chartId === 'watched' && codecName !== 'Never watched') {
                pathsProp = 'WatchedByUserPaths';
            }
            var categories = CODEC_CATEGORY_MAP[chartId];
            var result = collectCodecPaths(_lastCodecData, pathsProp, codecName,
                categories);
            return renderFileTree(result, codecName, collectDrilldownMeta(chartId));
        }
    });
}

function attachDonutHoverTooltips() {
    var charts = document.querySelectorAll('.donut-container');
    for (var c = 0; c < charts.length; c++) {
        (function (container) {
            // Bind events directly on <path> elements (not <g>) because mouseenter/mouseleave are non-bubbling and <g> with pointer-events:none would never receive them.
            var paths = container.querySelectorAll('.donut-segment path');

            for (var i = 0; i < paths.length; i++) {
                // Desktop: mouse hover shows tooltip + highlight. A touch emits a trailing synthetic
                // mouse sequence for click-compat; ignore it here (same guard as the click handler)
                // so the tap's tooltip is not immediately hidden and the tap-again state is not lost.
                paths[i].addEventListener('mouseenter', function (evt) {
                    if (Date.now() - _lastTouchEndTime < 800) {
                        return;
                    }
                    var seg = this.closest('.donut-segment');
                    seg.classList.add('donut-segment-hover');
                    showDonutTooltip(container, evt, seg);
                    _activeTooltipSegmentId = seg.dataset.segmentId;
                });

                paths[i].addEventListener('mousemove', function (evt) {
                    if (Date.now() - _lastTouchEndTime < 800) {
                        return;
                    }
                    var seg = this.closest('.donut-segment');
                    showDonutTooltip(container, evt, seg);
                });

                paths[i].addEventListener('mouseleave', function () {
                    if (Date.now() - _lastTouchEndTime < 800) {
                        return;
                    }
                    var seg = this.closest('.donut-segment');
                    seg.classList.remove('donut-segment-hover');
                    hideDonutTooltip(container);
                    _activeTooltipSegmentId = null;
                });

                // Desktop: click triggers the codec-row tree-view
                paths[i].addEventListener('click', function () {
                    // Suppress touch-originated clicks (cross-browser, not just Chromium)
                    if (Date.now() - _lastTouchEndTime < 800) {
                        return;
                    }
                    triggerCodecRowForSegment(this.closest('.donut-segment'));
                });

                // Mobile: one tap shows the tooltip and opens the drill-down
                // together - there is no hover on touch, so splitting them
                // across two taps only confuses. Tapping its segment while
                // open dismisses both (toggle off). The panel state decides,
                // not the tooltip variable, so a cleared variable can never
                // strand an open panel with a stuck tooltip.
                paths[i].addEventListener('touchend', function (evt) {
                    evt.preventDefault();
                    _lastTouchEndTime = Date.now();
                    var seg = this.closest('.donut-segment');
                    var segId = seg.dataset.segmentId;

                    if (isSegmentPanelOpen(seg)) {
                        seg.classList.remove('donut-segment-hover');
                        hideDonutTooltip(container);
                        _activeTooltipSegmentId = null;
                        triggerCodecRowForSegment(seg, true);
                        return;
                    }
                    // Remove highlight from any previously highlighted segment
                    // Clear tooltip and highlight state from ALL charts first: tapping
                    // here must not strand another chart's tooltip while the shared
                    // panel handler closes its drill-down (the outside-tap listener
                    // above only fires for taps outside any segment).
                    var allDonutContainers = document.querySelectorAll('.donut-container');
                    for (const donutContainer of allDonutContainers) {
                        hideDonutTooltip(donutContainer);
                        var prevHighlighted = donutContainer.querySelectorAll('.donut-segment-hover');
                        for (const highlighted of prevHighlighted) {
                            highlighted.classList.remove('donut-segment-hover');
                        }
                    }
                    seg.classList.add('donut-segment-hover');
                    // Create a synthetic position from touch coordinates
                    var touch = evt.changedTouches?.[0];
                    var syntheticEvt = touch
                        ? {clientX: touch.clientX, clientY: touch.clientY}
                        : evt;
                    showDonutTooltip(container, syntheticEvt, seg);
                    _activeTooltipSegmentId = segId;
                    triggerCodecRowForSegment(seg, true);
                });
            }
            // Keyboard: segments are focusable buttons mirroring the rows below.
            var segments = container.querySelectorAll('.donut-segment');
            for (const seg of segments) {
                (function (s) {
                    s.addEventListener('keydown', function (e) {
                        if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault();
                            triggerCodecRowForSegment(s);
                        }
                    });
                })(seg);
            }
        })(charts[c]);
    }

    // Close tooltip when tapping outside any donut segment (mobile)
    if (!_touchOutsideListenerAttached) {
        _touchOutsideListenerAttached = true;
        document.addEventListener('touchstart', function (evt) {
            if (_activeTooltipSegmentId && !evt.target.closest('.donut-segment')) {
                var allContainers = document.querySelectorAll('.donut-container');
                for (var d = 0; d < allContainers.length; d++) {
                    hideDonutTooltip(allContainers[d]);
                    // Remove highlight from all segments
                    var highlighted = allContainers[d].querySelectorAll('.donut-segment-hover');
                    for (var h = 0; h < highlighted.length; h++) {
                        highlighted[h].classList.remove('donut-segment-hover');
                    }
                }
                _activeTooltipSegmentId = null;
            }
        });
    }
}

function fillCodecsData(data) {
    _lastCodecData = data;
    _donutTooltipData = {};
    _activeTooltipSegmentId = null;

    // Video-only libraries (Movies + TV Shows + Other) - used for video-specific charts
    var videoLibraries = (data.Movies || []).concat(data.TvShows || []).concat(data.Other || []);
    // Music-only libraries - used for music-specific charts
    var musicLibraries = data.Music || [];
    // Book-only libraries - used for the book format chart
    var bookLibraries = data.Books || [];

    var videoCodecs = aggregateDict(videoLibraries, 'VideoCodecs');
    var videoAudioCodecs = aggregateDict(videoLibraries, 'VideoAudioCodecs');
    var musicAudioCodecs = aggregateDict(musicLibraries, 'MusicAudioCodecs');
    var bookFormats = aggregateDict(bookLibraries, 'BookFormats');
    var containers = aggregateDict(data.Libraries, 'ContainerFormats');
    var resolutions = aggregateDict(videoLibraries, 'Resolutions');
    var dynamicRanges = aggregateDict(videoLibraries, 'DynamicRanges');
    var videoBitrate = aggregateDict(videoLibraries, 'VideoBitrateTiers');
    var audioLanguages = aggregateDict(videoLibraries, 'AudioLanguages');
    var subtitleLanguages = aggregateDict(videoLibraries, 'SubtitleLanguages');
    // Watched shows who watched: one slice per username plus Never watched.
    var watched = countWatchedUsers(videoLibraries);
    var neverWatchedCount = aggregateDict(videoLibraries, 'WatchedTiers')['Never watched'] || 0;
    if (neverWatchedCount > 0) {
        watched['Never watched'] = neverWatchedCount;
    }

    var videoCodecSizes = aggregateDict(videoLibraries, 'VideoCodecSizes');
    var videoAudioCodecSizes = aggregateDict(videoLibraries, 'VideoAudioCodecSizes');
    var musicAudioCodecSizes = aggregateDict(musicLibraries, 'MusicAudioCodecSizes');
    var bookFormatSizes = aggregateDict(bookLibraries, 'BookFormatSizes');
    var containerSizes = aggregateDict(data.Libraries, 'ContainerSizes');
    var resolutionSizes = aggregateDict(videoLibraries, 'ResolutionSizes');
    var dynamicRangeSizes = aggregateDict(videoLibraries, 'DynamicRangeSizes');
    var videoBitrateSizes = aggregateDict(videoLibraries, 'VideoBitrateTierSizes');
    var audioLanguageSizes = aggregateDict(videoLibraries, 'AudioLanguageSizes');
    var subtitleLanguageSizes = aggregateDict(videoLibraries, 'SubtitleLanguageSizes');
    var watchedSizes = aggregateDict(videoLibraries, 'WatchedByUserSizes');
    var neverWatchedSize = aggregateDict(videoLibraries, 'WatchedTierSizes')['Never watched'] || 0;
    if (neverWatchedSize > 0) {
        watchedSizes['Never watched'] = neverWatchedSize;
    }

    var hasContainers = Object.keys(containers).length > 0;
    var hasResolutions = Object.keys(resolutions).length > 0;
    var hasDynamicRanges = Object.keys(dynamicRanges).length > 0;
    var hasVideoCodecs = Object.keys(videoCodecs).length > 0;
    var hasVideoAudio = Object.keys(videoAudioCodecs).length > 0;
    var hasMusicAudio = Object.keys(musicAudioCodecs).length > 0;
    var hasBookFormats = Object.keys(bookFormats).length > 0;
    var hasVideoBitrate = Object.keys(videoBitrate).length > 0;
    var hasAudioLanguages = Object.keys(audioLanguages).length > 0;
    var hasSubtitleLanguages = Object.keys(subtitleLanguages).length > 0;
    var hasWatched = Object.keys(watched).length > 0;
    var hasAnyCharts = hasContainers || hasResolutions || hasDynamicRanges
        || hasVideoCodecs || hasVideoAudio || hasMusicAudio || hasBookFormats
        || hasVideoBitrate || hasAudioLanguages || hasSubtitleLanguages || hasWatched;

    // One descriptor per card. Rendered in persisted order (_codecCardOrder); the drag engine reorders
    // these by their id. hasData gates empty cards exactly as the old if-chain did.
    var cardDescriptors = {
        containers: { hasData: hasContainers, icon: 'inventory_2', titleKey: 'containerFormats', titleFallback: 'Container Formats',
            body: function () { return renderDonutChart(containers, containerSizes, 'containers', data.Libraries, 'ContainerFormats'); } },
        resolutions: { hasData: hasResolutions, icon: 'straighten', titleKey: 'resolutions', titleFallback: 'Resolutions',
            body: function () { return renderDonutChart(resolutions, resolutionSizes, 'resolutions', videoLibraries, 'Resolutions'); } },
        dynamicRanges: { hasData: hasDynamicRanges, icon: 'palette', titleKey: 'dynamicRange', titleFallback: 'Dynamic Range',
            body: function () { return renderDonutChart(dynamicRanges, dynamicRangeSizes, 'dynamicRanges', videoLibraries, 'DynamicRanges'); } },
        videoCodecs: { hasData: hasVideoCodecs, icon: 'movie', titleKey: 'videoCodecs', titleFallback: 'Video Codecs',
            body: function () { return renderDonutChart(videoCodecs, videoCodecSizes, 'videoCodecs', videoLibraries, 'VideoCodecs'); } },
        videoAudioCodecs: { hasData: hasVideoAudio, icon: 'volume_up', titleKey: 'videoAudioCodecs', titleFallback: 'Video Audio Codecs',
            body: function () { return renderDonutChart(videoAudioCodecs, videoAudioCodecSizes, 'videoAudioCodecs', videoLibraries, 'VideoAudioCodecs'); } },
        videoBitrate: { hasData: hasVideoBitrate, icon: 'high_quality', titleKey: 'videoBitrate', titleFallback: 'Video Bitrate',
            body: function () { return renderDonutChart(videoBitrate, videoBitrateSizes, 'videoBitrate', videoLibraries, 'VideoBitrateTiers'); } },
        watched: { hasData: hasWatched, icon: 'group', titleKey: 'watched', titleFallback: 'Watched',
            body: function () { return renderDonutChart(watched, watchedSizes, 'watched', buildWatchedTooltipLibraries(videoLibraries), 'WatchedTooltip'); } },
        audioLanguages: { hasData: hasAudioLanguages, icon: 'description', titleKey: 'audioLanguages', titleFallback: 'Audio Languages',
            body: function () { return renderDonutChart(audioLanguages, audioLanguageSizes, 'audioLanguages', videoLibraries, 'AudioLanguages'); } },
        subtitleLanguages: { hasData: hasSubtitleLanguages, icon: 'edit_note', titleKey: 'subtitleLanguages', titleFallback: 'Subtitle Languages',
            body: function () { return renderDonutChart(subtitleLanguages, subtitleLanguageSizes, 'subtitleLanguages', videoLibraries, 'SubtitleLanguages'); } },
        musicAudioCodecs: { hasData: hasMusicAudio, icon: 'music_note', titleKey: 'musicAudioCodecs', titleFallback: 'Music Audio Codecs',
            body: function () { return renderDonutChart(musicAudioCodecs, musicAudioCodecSizes, 'musicAudioCodecs', musicLibraries, 'MusicAudioCodecs'); } },
        bookFormats: { hasData: hasBookFormats, icon: 'library_books', titleKey: 'bookFormats', titleFallback: 'Book Formats',
            body: function () { return renderDonutChart(bookFormats, bookFormatSizes, 'bookFormats', bookLibraries, 'BookFormats'); } }
    };

    var codecsHtml = '<div class="charts-row">';
    orderedCodecCardIds(cardDescriptors).forEach(function (id) {
        var d = cardDescriptors[id];
        if (!d || !d.hasData) {
            return;
        }
        var cardTitle = T(d.titleKey, d.titleFallback);
        // Name the card in the handle label so screen-reader users can tell the 11 grips apart,
        // instead of hearing the same "Reorder card" for every one.
        var handleLabel = T('codecCardReorder', 'Reorder card') + ': ' + cardTitle;
        var handle = '<span class="codec-card-handle" role="button" tabindex="0" aria-grabbed="false"'
            + ' aria-label="' + escAttr(handleLabel) + '">'
            + mi('drag_indicator') + '</span>';
        codecsHtml += '<div class="chart-box" data-chart-id="' + escAttr(id) + '">'
            + '<h4>' + handle + mi(d.icon) + escHtml(cardTitle) + '</h4>';
        codecsHtml += d.body();
        codecsHtml += '</div>';
    });
    if (!hasAnyCharts) {
        codecsHtml += '<div class="chart-box"><p style="opacity:0.5;">' + escHtml(T('noData', 'No data')) + '</p></div>';
    }
    codecsHtml += '</div>';

    var codecsContainer = document.getElementById('codecsContent');
    if (codecsContainer) {
        // A background rescan replaces the whole tab: keep the scroll position so
        // readers do not lose their place when fresh data arrives.
        var scrollY = window.scrollY;
        codecsContainer.innerHTML = codecsHtml;
        attachCodecClickHandlers();
        attachDonutHoverTooltips();
        attachCodecCardDragHandlers(codecsContainer);
        renderCodecsExplorer(codecsContainer);
        window.scrollTo(0, scrollY);
    }

    // Lazily load the persisted card order on first render, then re-render once so the
    // saved order takes effect (initial paint uses canonical until config arrives).
    loadCodecCardOrderOnce();
}

// Returns the effective card order: persisted order first (known ids only), then any
// canonical ids it omitted, so a new/unknown card still appears. Mirrors CodecCardCatalog.Normalize.
function orderedCodecCardIds(descriptors) {
    var seen = {};
    var out = [];
    var source = (_codecCardOrder && _codecCardOrder.length) ? _codecCardOrder : CANONICAL_CODEC_ORDER;
    source.forEach(function (id) {
        if (descriptors[id] && !seen[id]) {
            seen[id] = true;
            out.push(id);
        }
    });
    CANONICAL_CODEC_ORDER.forEach(function (id) {
        if (descriptors[id] && !seen[id]) {
            seen[id] = true;
            out.push(id);
        }
    });
    return out;
}

// Loads the persisted order from the plugin config exactly once per page load. On success,
// if the order differs from what we rendered, re-render from the cached scan data.
function loadCodecCardOrderOnce() {
    if (_codecCardOrderLoaded) {
        return;
    }
    _codecCardOrderLoaded = true;
    // Snapshot the local mutation generation now. If the user drops a card while this GET is still
    // in flight, the local order is newer than the config we are about to read, so the stale GET
    // must not overwrite it (which would leave the UI showing the old order the server has replaced).
    var genAtRequest = _codecOrderGeneration;
    apiGet('JellyfinHelper/Configuration', function (cfg) {
        if (_codecOrderGeneration !== genAtRequest) {
            return;
        }
        var order = cfg?.CodecCardOrder;
        if (!Array.isArray(order)) {
            order = cfg?.codecCardOrder || null;
        }
        if (Array.isArray(order) && order.length) {
            _codecCardOrder = order.slice();
            if (_lastCodecData) {
                fillCodecsData(_lastCodecData);
            }
        }
    }, _apiDefaultError('GET', 'JellyfinHelper/Configuration'));
}

// Shows a small success/error badge in the card's top-right corner after a reorder save, then fades
// it out. feedbackEl may be the card or any element inside it; the badge always anchors to the
// enclosing .chart-box (which is position:relative in CSS). A per-card guard prevents a stale fade
// from clearing a fresher badge when saves land back-to-back.
function showCodecSaveBadge(feedbackEl, success) {
    var card = feedbackEl?.closest ? feedbackEl.closest('.chart-box') : null;
    if (!card) {
        return;
    }
    var ok = success !== false;
    var badge = card.querySelector('.codec-save-badge');
    if (!badge) {
        badge = document.createElement('span');
        badge.className = 'codec-save-badge';
        badge.setAttribute('aria-hidden', 'true');
        card.appendChild(badge);
    }
    badge.classList.toggle('codec-save-badge--error', !ok);
    badge.innerHTML = mi(ok ? 'check_circle' : 'error');
    // Restart the fade: force reflow so re-adding the visible class re-triggers the transition.
    badge.classList.remove('codec-save-badge--visible');
    void badge.offsetWidth;
    badge.classList.add('codec-save-badge--visible');

    var guard = (Number.parseInt(badge.dataset.guard || '0', 10)) + 1;
    badge.dataset.guard = String(guard);
    setTimeout(function () {
        if (badge.dataset.guard !== String(guard)) {
            return;
        }
        badge.classList.remove('codec-save-badge--visible');
    }, ok ? 2000 : 3000);
}

// Persists the given order optimistically. One PUT in flight at a time; a drop during a save
// queues the latest order and fires exactly one follow-up PUT when the active one settles.
// feedbackEl is the dropped card; its enclosing .chart-box shows a corner save badge.
// prevOrder is the order that was persisted before this attempt, so a failure can roll back to it
// rather than re-rendering the rejected order (which would leave the UI and server diverged).
function saveCodecCardOrder(order, feedbackEl, prevOrder) {
    if (_codecOrderSaving) {
        // Keep the oldest known-good order as the rollback target across a queued chain of saves,
        // so a failure of the final PUT still restores a server-accepted order, not an interim one.
        _codecOrderQueued = {
            order: order.slice(),
            el: feedbackEl,
            prevOrder: (_codecOrderQueued && _codecOrderQueued.prevOrder) || prevOrder
        };
        return;
    }
    _codecOrderSaving = true;
    apiPut('JellyfinHelper/Configuration/CodecCardOrder', { codecCardOrder: order }, function (resp) {
        _codecOrderSaving = false;
        var saved = resp?.CodecCardOrder || resp?.codecCardOrder;
        if (Array.isArray(saved)) {
            _codecCardOrder = saved.slice();
        }
        showCodecSaveBadge(feedbackEl, true);
        announceCodec(T('codecCardOrderSaved', 'Card order saved.'));
        flushQueuedCodecOrderSave();
    }, function (xhr) {
        _codecOrderSaving = false;
        _apiDefaultError('PUT', 'JellyfinHelper/Configuration/CodecCardOrder')(xhr);
        showCodecSaveBadge(feedbackEl, false);
        announceCodec(T('codecCardOrderSaveFailed', 'Could not save card order.'));
        // Roll back to the last persisted order so the UI never diverges from the server. prevOrder
        // was captured before the optimistic DOM update, so it reflects what the server still holds.
        _codecCardOrder = Array.isArray(prevOrder) ? prevOrder.slice() : null;
        _codecOrderQueued = null;
        if (_lastCodecData) {
            fillCodecsData(_lastCodecData);
        }
    });
}

function flushQueuedCodecOrderSave() {
    if (_codecOrderQueued) {
        var next = _codecOrderQueued;
        _codecOrderQueued = null;
        saveCodecCardOrder(next.order, next.el, next.prevOrder);
    }
}

// Reads the current DOM order of the cards and persists it. Captures the previously persisted
// order first so a failed save can roll back to it instead of to the rejected order.
function commitCodecCardOrderFromDom(chartsRow, feedbackEl) {
    // A background rescan can detach the whole charts row mid-interaction. Reading an empty DOM then
    // would persist [] -> the server normalizes that to the full canonical order and silently wipes
    // the user's custom layout. Bail instead so a transient detach never destroys saved data.
    if (!chartsRow || !document.contains(chartsRow)) {
        return;
    }
    var prevOrder = Array.isArray(_codecCardOrder) ? _codecCardOrder.slice() : null;

    var domIds = [];
    chartsRow.querySelectorAll('.chart-box[data-chart-id]').forEach(function (box) {
        if (document.contains(box)) {
            domIds.push(box.dataset.chartId);
        }
    });
    if (!domIds.length) {
        return;
    }

    // The DOM only holds cards that currently have data; cards hidden this scan are absent. Rebase
    // the move onto the last full known order so a card temporarily without data keeps its custom
    // slot instead of being appended canonically at the end on the next save.
    var order = reorderFullOrder(prevOrder, domIds);
    _codecCardOrder = order.slice();
    _codecOrderGeneration++;
    saveCodecCardOrder(order, feedbackEl, prevOrder);
}

// Applies the relative order of the currently-visible ids (domIds) onto the last full order,
// leaving ids absent from the DOM in their existing relative positions. The visible ids fill the
// slots they already occupied in the full order, in their new DOM sequence; absent ids stay put.
function reorderFullOrder(prevOrder, domIds) {
    // Seed from the last persisted order, or the canonical order on the very first reorder, so
    // cards hidden this scan still occupy their proper slots rather than being dropped.
    var base = (Array.isArray(prevOrder) && prevOrder.length) ? prevOrder : CANONICAL_CODEC_ORDER;
    if (!base.length) {
        return domIds.slice();
    }
    var visible = {};
    domIds.forEach(function (id) { visible[id] = true; });
    var queue = domIds.slice();
    var result = [];
    for (const id of base) {
        // Each slot that previously held a visible card now takes the next card from the new DOM
        // sequence; slots for absent (data-less) cards are preserved verbatim.
        result.push(visible[id] ? queue.shift() : id);
    }
    // Any visible id not already placed (e.g. a brand-new card) lands at the end in DOM order.
    while (queue.length) {
        var extra = queue.shift();
        if (!result.includes(extra)) {
            result.push(extra);
        }
    }
    return result;
}

// ---- Drag engine: reorder codec cards by their header handle (pointer + touch + keyboard) ----

// Touch long-press and movement thresholds.
var CODEC_LONGPRESS_MS = 450;
var CODEC_TOUCH_MOVE_CANCEL_PX = 10;
var CODEC_MOUSE_LIFT_PX = 4;
var CODEC_AUTOSCROLL_ZONE_PX = 64;
var CODEC_AUTOSCROLL_MAX_PX_PER_FRAME = 10;

function prefersReducedMotion() {
    return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

// Wires drag handles for every card inside the charts row. Idempotent: innerHTML was just
// replaced, so handlers attach to fresh nodes each render.
function attachCodecCardDragHandlers(container) {
    var chartsRow = container.querySelector('.charts-row');
    if (!chartsRow) {
        return;
    }
    chartsRow.querySelectorAll('.codec-card-handle').forEach(function (handle) {
        var card = handle.closest('.chart-box');
        if (!card) {
            return;
        }
        handle.addEventListener('pointerdown', function (e) {
            onCodecHandlePointerDown(e, handle, card, chartsRow);
        });
        handle.addEventListener('keydown', function (e) {
            onCodecHandleKeyDown(e, handle, card, chartsRow);
        });
        // Suppress the long-press context menu / selection callout on touch so the grip arms a
        // drag instead of popping the browser's own menu (Android Chrome) or text selection.
        handle.addEventListener('contextmenu', function (e) {
            e.preventDefault();
        });
    });
}

// Shared live-drag state (only one card can be dragged at a time).
var _codecDrag = null;

function onCodecHandlePointerDown(e, handle, card, chartsRow) {
    // Only a non-primary *mouse* button aborts: touch and pen can report button -1 for a normal
    // press, so gating every pointer type on button===0 would swallow legitimate touch drags.
    if (_codecDrag || (e.pointerType === 'mouse' && e.button !== 0)) {
        return;
    }
    var isTouch = e.pointerType === 'touch';
    var state = {
        handle: handle, card: card, chartsRow: chartsRow,
        startX: e.clientX, startY: e.clientY,
        pointerId: e.pointerId, isTouch: isTouch,
        active: false, armTimer: null, placeholder: null,
        autoScrollRaf: 0, autoScrollSpeed: 0,
        lastClientX: e.clientX, lastClientY: e.clientY
    };

    var move = function (ev) { onCodecDragPointerMove(ev, state); };
    var up = function (ev) { onCodecDragPointerUp(ev, state, move, up); };
    state.move = move;
    state.up = up;

    window.addEventListener('pointermove', move, { passive: false });
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', up);

    if (isTouch) {
        // Long-press to arm, so a normal touch still scrolls the page.
        state.armTimer = setTimeout(function () {
            state.armTimer = null;
            beginCodecDrag(state);
            if (navigator.vibrate) {
                navigator.vibrate(10);
            }
        }, CODEC_LONGPRESS_MS);
    }
    _codecDrag = state;
}

function beginCodecDrag(state) {
    if (state.active) {
        return;
    }
    state.active = true;
    try {
        state.handle.setPointerCapture(state.pointerId);
    } catch (err) {
        // setPointerCapture can throw if the pointer already released; drag still works via window listeners.
    }
    var rect = state.card.getBoundingClientRect();
    // Anchor the card to the pointer so the lifted card tracks the finger/cursor rather than
    // sitting static in its slot (on touch the finger would otherwise cover a motionless card).
    state.grabOffsetX = state.startX - rect.left;
    state.grabOffsetY = state.startY - rect.top;
    // Mirror the keyboard path: expose the active grab to assistive tech for pointer/touch too.
    state.handle.setAttribute('aria-grabbed', 'true');
    var placeholder = document.createElement('div');
    placeholder.className = 'chart-box codec-drop-placeholder';
    placeholder.style.height = rect.height + 'px';
    state.card.after(placeholder);
    state.placeholder = placeholder;
    state.card.classList.add('codec-dragging');
    // Reduced motion: skip the floating lift entirely and reorder via the placeholder only, so no
    // card flies across the screen. Otherwise fix the card's box so the transform moves it over the
    // grid without reflowing siblings (the placeholder already holds the slot open).
    if (!prefersReducedMotion()) {
        state.lifted = true;
        state.card.style.width = rect.width + 'px';
        state.card.style.position = 'fixed';
        state.card.style.left = '0';
        state.card.style.top = '0';
        state.card.style.zIndex = '1000';
        state.card.style.pointerEvents = 'none';
        moveCodecCardToPointer(state, state.startX, state.startY);
    }
}

// Translate the lifted (fixed-position) card so its grab point stays under the pointer.
function moveCodecCardToPointer(state, x, y) {
    if (!state.card || !state.lifted) {
        return;
    }
    var tx = x - state.grabOffsetX;
    var ty = y - state.grabOffsetY;
    state.card.style.transform = 'translate(' + tx + 'px, ' + ty + 'px) scale(1.01)';
}

function onCodecDragPointerMove(e, state) {
    if (!state.active) {
        var dx = e.clientX - state.startX;
        var dy = e.clientY - state.startY;
        var dist = Math.hypot(dx, dy);
        if (state.isTouch) {
            // Movement before the long-press fires means the user is scrolling: cancel arming.
            if (dist > CODEC_TOUCH_MOVE_CANCEL_PX && state.armTimer) {
                clearTimeout(state.armTimer);
                state.armTimer = null;
                teardownCodecDrag(state);
            }
            return;
        }
        if (dist >= CODEC_MOUSE_LIFT_PX) {
            beginCodecDrag(state);
        } else {
            return;
        }
    }

    e.preventDefault();
    state.lastClientX = e.clientX;
    state.lastClientY = e.clientY;
    moveCodecCardToPointer(state, e.clientX, e.clientY);
    moveCodecPlaceholderTo(state, e.clientX, e.clientY);
    updateCodecAutoScroll(state);
}

// Places the placeholder next to the card nearest the pointer. Instead of a row-major first-match
// (which snaps between columns the instant the pointer crosses a center line), it picks the box
// whose center is closest to the pointer, then drops before or after it based on which side the
// pointer is on. This feels stable in a multi-column grid and keeps the placeholder near the finger.
function moveCodecPlaceholderTo(state, x, y) {
    var boxes = Array.prototype.slice.call(
        state.chartsRow.querySelectorAll('.chart-box:not(.codec-dragging):not(.codec-drop-placeholder)'));
    if (!boxes.length) {
        state.chartsRow.appendChild(state.placeholder);
        return;
    }

    var nearest = null;
    var nearestDist = Infinity;
    var nearestRect = null;
    for (const box of boxes) {
        var r = box.getBoundingClientRect();
        var cx = r.left + (r.width / 2);
        var cy = r.top + (r.height / 2);
        var dx = x - cx;
        var dy = y - cy;
        var dist = (dx * dx) + (dy * dy);
        if (dist < nearestDist) {
            nearestDist = dist;
            nearest = box;
            nearestRect = r;
        }
    }

    // Decide which side of the nearest card the pointer favours. Compare on the dominant axis of the
    // card's own layout: in a single column the vertical position decides; in a grid, horizontal
    // position within the same row decides, falling back to vertical across rows.
    var midX = nearestRect.left + (nearestRect.width / 2);
    var midY = nearestRect.top + (nearestRect.height / 2);
    var sameRow = y >= nearestRect.top && y <= nearestRect.bottom;
    var after = sameRow ? (x > midX) : (y > midY);

    if (after) {
        nearest.after(state.placeholder);
    } else {
        nearest.before(state.placeholder);
    }
}

// Recomputes the auto-scroll speed from the pointer's current Y and stores it on state. A single
// rAF loop reads that live value every frame, so moving from the bottom zone to the top reverses
// direction instead of staying stuck on the speed captured when the loop first started.
function updateCodecAutoScroll(state) {
    if (prefersReducedMotion()) {
        return;
    }
    var vh = window.innerHeight;
    var y = state.lastClientY;
    var speed = 0;
    if (y < CODEC_AUTOSCROLL_ZONE_PX) {
        speed = -((CODEC_AUTOSCROLL_ZONE_PX - y) / CODEC_AUTOSCROLL_ZONE_PX) * CODEC_AUTOSCROLL_MAX_PX_PER_FRAME;
    } else if (y > vh - CODEC_AUTOSCROLL_ZONE_PX) {
        speed = ((y - (vh - CODEC_AUTOSCROLL_ZONE_PX)) / CODEC_AUTOSCROLL_ZONE_PX) * CODEC_AUTOSCROLL_MAX_PX_PER_FRAME;
    }
    state.autoScrollSpeed = speed;
    if (speed === 0 || state.autoScrollRaf) {
        // Zero speed lets the running loop idle itself out; a live loop already reads the new speed.
        return;
    }
    var step = function () {
        if (!state.active || state.autoScrollSpeed === 0) {
            state.autoScrollRaf = 0;
            return;
        }
        window.scrollBy(0, state.autoScrollSpeed);
        // Re-run the hit-test at the pointer's *current* X so the placeholder lands in the right
        // column after a scroll offset, instead of the X captured at drag start.
        moveCodecPlaceholderTo(state, state.lastClientX, state.lastClientY);
        state.autoScrollRaf = requestAnimationFrame(step);
    };
    state.autoScrollRaf = requestAnimationFrame(step);
}

function onCodecDragPointerUp(e, state, move, up) {
    if (state.armTimer) {
        clearTimeout(state.armTimer);
        state.armTimer = null;
    }
    var wasActive = state.active;
    if (wasActive && state.placeholder) {
        // Drop. Settle the card out of its fixed-position lift *before* moving it to the new slot,
        // so it never shows for a frame as a fixed element sitting at the new DOM position (which
        // read as the card briefly vanishing). Order: unlift -> insert into flow -> persist.
        settleCardIntoFlow(state);
        state.placeholder.before(state.card);
        commitCodecCardOrderFromDom(state.chartsRow, state.card);
    }
    // A touch drag ends with the finger over a donut segment or row; the browser then emits a
    // compatibility click there (the lifted card had pointer-events:none). Stamp the shared
    // touch-end timestamp the donut/row handlers guard on, so that synthetic click cannot open an
    // unintended drill-down just because the drag happened to finish over a chart.
    if (wasActive && state.isTouch) {
        _lastTouchEndTime = Date.now();
    }
    teardownCodecDrag(state, move, up);
}

// Clears the fixed-position lift styles so the card returns to normal grid flow. Called both on
// drop (before the DOM insert, to avoid a fixed-at-new-slot flash) and on teardown (for cancels).
// Idempotent: clearing already-empty inline styles is a no-op.
function settleCardIntoFlow(state) {
    if (!state.card) {
        return;
    }
    state.lifted = false;
    state.card.style.transform = '';
    state.card.style.position = '';
    state.card.style.left = '';
    state.card.style.top = '';
    state.card.style.width = '';
    state.card.style.zIndex = '';
    state.card.style.pointerEvents = '';
}

function teardownCodecDrag(state, move, up) {
    if (state.autoScrollRaf) {
        cancelAnimationFrame(state.autoScrollRaf);
        state.autoScrollRaf = 0;
    }
    state.autoScrollSpeed = 0;
    if (state.placeholder) {
        state.placeholder.remove();
    }
    state.card.classList.remove('codec-dragging');
    state.handle.setAttribute('aria-grabbed', 'false');
    // Idempotent: the drop path already settled the card into flow before the DOM insert; a cancel
    // (no drop) still needs the reset here.
    settleCardIntoFlow(state);
    try {
        state.handle.releasePointerCapture(state.pointerId);
    } catch (err) {
        // No capture to release.
    }
    window.removeEventListener('pointermove', move || state.move, { passive: false });
    window.removeEventListener('pointerup', up || state.up);
    window.removeEventListener('pointercancel', up || state.up);
    state.active = false;
    _codecDrag = null;
}

// Keyboard reorder: linear list model. Enter/Space grabs, ArrowUp/Down moves one position,
// Enter drops+saves, Escape cancels. Announced via the handle's aria-grabbed state.
function onCodecHandleKeyDown(e, handle, card, chartsRow) {
    var grabbed = handle.getAttribute('aria-grabbed') === 'true';
    var key = e.key;

    if (key === 'Enter' || key === ' ' || key === 'Spacebar') {
        e.preventDefault();
        if (!grabbed) {
            handle.setAttribute('aria-grabbed', 'true');
            card.classList.add('codec-dragging');
            handle.dataset.originIndex = String(indexOfCard(chartsRow, card));
            announceCodec(T('codecCardGrabbed', 'Card grabbed. Use arrow keys to move, Enter to drop, Escape to cancel.'));
        } else {
            handle.setAttribute('aria-grabbed', 'false');
            card.classList.remove('codec-dragging');
            delete handle.dataset.originIndex;
            commitCodecCardOrderFromDom(chartsRow, card);
            announceCodec(T('codecCardDropped', 'Card dropped and order saved.'));
        }
        return;
    }

    if (!grabbed) {
        return;
    }

    if (key === 'Escape') {
        e.preventDefault();
        handle.setAttribute('aria-grabbed', 'false');
        card.classList.remove('codec-dragging');
        // Restore the card to the slot it was grabbed from, in-place, so focus stays on this handle.
        // A full re-render would rebuild the DOM and drop focus to <body>.
        var origin = Number.parseInt(handle.dataset.originIndex, 10);
        delete handle.dataset.originIndex;
        if (!Number.isNaN(origin)) {
            restoreCardToIndex(chartsRow, card, origin);
        }
        card.querySelector('.codec-card-handle').focus();
        announceCodec(T('codecCardCancelled', 'Reorder cancelled.'));
        return;
    }

    if (key === 'ArrowUp' || key === 'ArrowLeft') {
        e.preventDefault();
        moveCardInList(chartsRow, card, -1);
    } else if (key === 'ArrowDown' || key === 'ArrowRight') {
        e.preventDefault();
        moveCardInList(chartsRow, card, 1);
    }
}

function indexOfCard(chartsRow, card) {
    var boxes = Array.prototype.slice.call(chartsRow.querySelectorAll('.chart-box[data-chart-id]'));
    return boxes.indexOf(card);
}

// Moves card back to targetIndex among its siblings without a re-render, so Escape can undo a
// keyboard reorder while keeping focus on the handle.
function restoreCardToIndex(chartsRow, card, targetIndex) {
    var boxes = Array.prototype.slice.call(
        chartsRow.querySelectorAll('.chart-box[data-chart-id]')).filter(function (b) { return b !== card; });
    var ref = boxes[targetIndex] || null;
    if (ref) {
        ref.before(card);
    } else {
        chartsRow.appendChild(card);
    }
}

function moveCardInList(chartsRow, card, delta) {
    var boxes = Array.prototype.slice.call(chartsRow.querySelectorAll('.chart-box[data-chart-id]'));
    var idx = boxes.indexOf(card);
    var target = idx + delta;
    if (idx < 0 || target < 0 || target >= boxes.length) {
        return;
    }
    if (delta > 0) {
        boxes[target].after(card);
    } else {
        boxes[target].before(card);
    }
    card.querySelector('.codec-card-handle').focus();
    announceCodec(T('codecCardMoved', 'Moved to position') + ' ' + (target + 1));
}

// Lazily-created aria-live region so keyboard reorders are announced to screen readers.
function announceCodec(message) {
    var live = document.getElementById('codecCardLiveRegion');
    if (!live) {
        live = document.createElement('div');
        live.id = 'codecCardLiveRegion';
        live.setAttribute('aria-live', 'polite');
        live.className = 'sr-only';
        document.body.appendChild(live);
    }
    live.textContent = message;
}
