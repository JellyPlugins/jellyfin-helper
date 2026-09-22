'use strict';

// Store last scan data for codec detail clicks
var _lastCodecData = null;

// Tooltip data store - avoids complex string parsing in DOM attributes
var _donutTooltipData = {};

// Track which segment currently shows a tooltip (for mobile tap-to-show, tap-again-to-click)
var _activeTooltipSegmentId = null;

// Guard: prevent duplicate document-level touchstart listener registration
var _touchOutsideListenerAttached = false;

// Timestamp of last touchend - used to suppress touch-originated click events cross-browser
var _lastTouchEndTime = 0;

// Guard so the row sync listener registers once
var _codecRowSyncBound = false;

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

// Trigger the matching codec-row click for a donut segment
function triggerCodecRowForSegment(segment) {
    var chartBox = segment.closest('.chart-box');
    if (!chartBox) {
        return;
    }
    var codecName = segment.dataset.codec;
    if (!codecName) {
        return;
    }
    var rows = chartBox.querySelectorAll('.codec-clickable');
    for (var i = 0; i < rows.length; i++) {
        if (rows[i].dataset.codec === codecName) {
            // Force scroll when triggered from donut (user clicked far above the panel)
            _forceScrollOnPanelOpen = true;
            rows[i].click();
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

                // Mobile: first tap shows tooltip, second tap triggers click
                paths[i].addEventListener('touchend', function (evt) {
                    evt.preventDefault();
                    _lastTouchEndTime = Date.now();
                    var seg = this.closest('.donut-segment');
                    var segId = seg.dataset.segmentId;

                    if (_activeTooltipSegmentId === segId) {
                        // Second tap on same segment - trigger the click action
                        seg.classList.remove('donut-segment-hover');
                        hideDonutTooltip(container);
                        _activeTooltipSegmentId = null;
                        triggerCodecRowForSegment(seg);
                    } else {
                        // First tap - show tooltip + highlight
                        // Remove highlight from any previously highlighted segment
                        var prevHighlighted = container.querySelectorAll('.donut-segment-hover');
                        for (var h = 0; h < prevHighlighted.length; h++) {
                            prevHighlighted[h].classList.remove('donut-segment-hover');
                        }
                        seg.classList.add('donut-segment-hover');
                        // Create a synthetic position from touch coordinates
                        var touch = evt.changedTouches?.[0];
                        var syntheticEvt = touch
                            ? {clientX: touch.clientX, clientY: touch.clientY}
                            : evt;
                        showDonutTooltip(container, syntheticEvt, seg);
                        _activeTooltipSegmentId = segId;
                    }
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

    var codecsHtml = '<div class="charts-row">';
    if (hasContainers) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('inventory_2') + escHtml(T('containerFormats', 'Container Formats')) + '</h4>';
        codecsHtml += renderDonutChart(containers, containerSizes, 'containers', data.Libraries, 'ContainerFormats');
        codecsHtml += '</div>';
    }
    if (hasResolutions) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('straighten') + escHtml(T('resolutions', 'Resolutions')) + '</h4>';
        codecsHtml += renderDonutChart(resolutions, resolutionSizes, 'resolutions', videoLibraries, 'Resolutions');
        codecsHtml += '</div>';
    }
    if (hasDynamicRanges) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('palette') + escHtml(T('dynamicRange', 'Dynamic Range')) + '</h4>';
        codecsHtml += renderDonutChart(dynamicRanges, dynamicRangeSizes, 'dynamicRanges', videoLibraries, 'DynamicRanges');
        codecsHtml += '</div>';
    }
    if (hasVideoCodecs) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('movie') + escHtml(T('videoCodecs', 'Video Codecs')) + '</h4>';
        codecsHtml += renderDonutChart(videoCodecs, videoCodecSizes, 'videoCodecs', videoLibraries, 'VideoCodecs');
        codecsHtml += '</div>';
    }
    if (hasVideoAudio) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('volume_up') + escHtml(T('videoAudioCodecs', 'Video Audio Codecs')) + '</h4>';
        codecsHtml += renderDonutChart(videoAudioCodecs, videoAudioCodecSizes, 'videoAudioCodecs', videoLibraries,
            'VideoAudioCodecs');
        codecsHtml += '</div>';
    }
    if (hasVideoBitrate) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('high_quality') + escHtml(T('videoBitrate', 'Video Bitrate')) + '</h4>';
        codecsHtml += renderDonutChart(videoBitrate, videoBitrateSizes, 'videoBitrate', videoLibraries,
            'VideoBitrateTiers');
        codecsHtml += '</div>';
    }
    if (hasWatched) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('group') + escHtml(T('watched', 'Watched')) + '</h4>';
        codecsHtml += renderDonutChart(watched, watchedSizes, 'watched', buildWatchedTooltipLibraries(videoLibraries), 'WatchedTooltip');
        codecsHtml += '</div>';
    }
    if (hasAudioLanguages) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('description') + escHtml(T('audioLanguages', 'Audio Languages')) + '</h4>';
        codecsHtml += renderDonutChart(audioLanguages, audioLanguageSizes, 'audioLanguages', videoLibraries,
            'AudioLanguages');
        codecsHtml += '</div>';
    }
    if (hasSubtitleLanguages) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('edit_note') + escHtml(T('subtitleLanguages', 'Subtitle Languages')) + '</h4>';
        codecsHtml += renderDonutChart(subtitleLanguages, subtitleLanguageSizes, 'subtitleLanguages', videoLibraries,
            'SubtitleLanguages');
        codecsHtml += '</div>';
    }
    if (hasMusicAudio) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('music_note') + escHtml(T('musicAudioCodecs', 'Music Audio Codecs')) + '</h4>';
        codecsHtml += renderDonutChart(musicAudioCodecs, musicAudioCodecSizes, 'musicAudioCodecs', musicLibraries,
            'MusicAudioCodecs');
        codecsHtml += '</div>';
    }
    if (hasBookFormats) {
        codecsHtml += '<div class="chart-box"><h4>' + mi('library_books') + escHtml(T('bookFormats', 'Book Formats')) + '</h4>';
        codecsHtml += renderDonutChart(bookFormats, bookFormatSizes, 'bookFormats', bookLibraries,
            'BookFormats');
        codecsHtml += '</div>';
    }
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
        renderCodecsExplorer(codecsContainer);
        window.scrollTo(0, scrollY);
    }
}