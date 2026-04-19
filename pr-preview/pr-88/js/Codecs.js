// --- Codecs Tab ---

// Store last scan data for codec detail clicks
var _lastCodecData = null;

// Tooltip data store — avoids complex string parsing in DOM attributes
var _donutTooltipData = {};

// Track which segment currently shows a tooltip (for mobile tap-to-show, tap-again-to-click)
var _activeTooltipSegmentId = null;

// Guard: prevent duplicate document-level touchstart listener registration
var _touchOutsideListenerAttached = false;

// Timestamp of last touchend — used to suppress touch-originated click events cross-browser
var _lastTouchEndTime = 0;

// Flag: force scroll-into-view on next panel open (set by donut click, consumed by attachTogglePanelHandlers)
var _forceScrollOnPanelOpen = false;

// SVG donut tooltip — reads rich data from _donutTooltipData
function showDonutTooltip(container, evt, segment) {
    var tooltip = container.querySelector('.donut-tooltip');
    if (!tooltip) {
        return;
    }

    var segId = segment.getAttribute('data-segment-id');
    var info = _donutTooltipData[segId];
    if (!info) {
        return;
    }

    var html = '<div class="donut-tooltip-header">';
    html += '<span class="donut-tooltip-codec">' + escHtml(info.codec) + '</span>';
    html += '<span class="donut-tooltip-total">' + info.totalCount + ' '
        + (info.totalCount === 1 ? T('file', 'file') : T('files', 'files')) + '</span>';
    html += '</div>';
    html += '<div class="donut-tooltip-pct">' + info.totalPct + '%</div>';

    if (info.libraries.length > 0) {
        html += '<div class="donut-tooltip-divider"></div>';
        html += '<table class="donut-tooltip-table"><tbody>';
        for (var i = 0; i < info.libraries.length; i++) {
            var lib = info.libraries[i];
            html += '<tr>';
            html += '<td class="donut-tooltip-lib">' + escHtml(lib.name) + '</td>';
            html += '<td class="donut-tooltip-count">' + lib.count + ' ' + (lib.count === 1 ? T('file', 'file') : T('files', 'files')) + '</td>';
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
    var codecName = segment.getAttribute('data-codec');
    if (!codecName) {
        return;
    }
    var rows = chartBox.querySelectorAll('.codec-clickable');
    for (var i = 0; i < rows.length; i++) {
        if (rows[i].getAttribute('data-codec') === codecName) {
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
        if (Object.prototype.hasOwnProperty.call(data, key) && data[key] > 0) {
            entries.push({label: key, value: data[key]});
            total += data[key];
        }
    }
    if (total === 0) {
        return '<p style="opacity:0.5;">' + T('noData', 'No data') + '</p>';
    }

    entries.sort(function (a, b) {
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
                var libCount = libPropertyValue && libPropertyValue[entries[i].label]
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
            + ' data-codec="' + escAttr(entries[i].label) + '">';
        donutContainer += '<path d="' + arcPath + '" fill="' + color + '"/>';
        donutContainer += '</g>';

        startAngle = endAngle;
    }

    donutContainer += '</svg>';
    donutContainer += '</div>';
    return donutContainer;
}

// Build the clickable codec breakdown table below the donut
function renderCodecBreakdown(countDict, sizeDict, chartId) {
    var entries = [];
    var total = 0;
    for (var key in countDict) {
        if (Object.prototype.hasOwnProperty.call(countDict, key) && countDict[key] > 0) {
            var size = (sizeDict && sizeDict[key]) ? sizeDict[key] : 0;
            entries.push({label: key, count: countDict[key], size: size});
            total += countDict[key];
        }
    }
    if (entries.length === 0) {
        return '';
    }

    entries.sort(function (a, b) {
        return b.count - a.count;
    });

    var html = '<div class="codec-breakdown">';
    for (var i = 0; i < entries.length; i++) {
        var color = DONUT_COLORS[i % DONUT_COLORS.length];
        var pct = (entries[i].count / total * 100).toFixed(1);
        var isActive = '';

        html += '<div class="codec-row codec-clickable' + isActive + '" data-chart="' + escAttr(chartId) + '"' +
            ' data-codec="' + escAttr(entries[i].label) + '" role="button" tabindex="0">';
        html += '<div class="codec-row-color" style="background:' + color + '"></div>';
        html += '<div class="codec-row-info">';
        html += '<span class="codec-row-name">' + escHtml(entries[i].label) + '</span>';
        html += '<span class="codec-row-stats">' + entries[i].count + ' ' + T('files', 'files') + ' · '
            + pct + '% · ' + formatBytes(entries[i].size) + '</span>';
        html += '</div>';
        html += '<div class="codec-row-bar"><div class="codec-row-bar-fill" style="width:' + pct + '%;background:'
            + color + '"></div></div>';
        html += '<div class="codec-row-arrow">›</div>';
        html += '</div>';
    }
    html += '</div>';

    // Detail panel placeholder
    html += '<div class="file-tree-panel" id="codecDetail_' + chartId + '"></div>';

    return html;
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
    var includeOther = !categories || categories.other;

    return {
        movies: includeMovies ? collectDictPaths(data.Movies || [], pathsProp, codecName) : [],
        tvShows: includeTvShows ? collectDictPaths(data.TvShows || [], pathsProp, codecName) : [],
        music: includeMusic ? collectDictPaths(data.Music || [], pathsProp, codecName) : [],
        other: includeOther ? collectDictPaths(data.Other || [], pathsProp, codecName) : [],
        rootPaths: {
            movies: data.MovieRootPaths || [],
            tvShows: data.TvShowRootPaths || [],
            music: data.MusicRootPaths || [],
            other: data.OtherRootPaths || []
        }
    };
}

// Map chart IDs to their corresponding path property names
var CODEC_PATH_MAP = {
    'videoCodecs': 'VideoCodecPaths',
    'videoAudioCodecs': 'VideoAudioCodecPaths',
    'musicAudioCodecs': 'MusicAudioCodecPaths',
    'containers': 'ContainerFormatPaths',
    'resolutions': 'ResolutionPaths'
};

// Map chart IDs to which media categories should be included
// Video Codecs, Video Audio Codecs, Resolutions → only Movies + TV Shows + Other
// Music Audio Codecs → only Music
// Container Formats → all libraries (Movies + TV Shows + Music + Other)
var CODEC_CATEGORY_MAP = {
    'videoCodecs': {movies: true, tvShows: true, music: false, other: true},
    'videoAudioCodecs': {movies: true, tvShows: true, music: false, other: true},
    'musicAudioCodecs': {movies: false, tvShows: false, music: true, other: false},
    'containers': {movies: true, tvShows: true, music: true, other: true},
    'resolutions': {movies: true, tvShows: true, music: false, other: true}
};

// Attach click handlers to codec rows — delegates to shared attachTogglePanelHandlers
function attachCodecClickHandlers() {
    attachTogglePanelHandlers({
        itemSelector: '.codec-clickable',
        activeClass: 'codec-row-active',
        groupAttr: 'data-chart',
        typeAttr: 'data-codec',
        getPanelId: function (item) {
            return 'codecDetail_' + item.getAttribute('data-chart');
        },
        renderContent: function (item) {
            if (!_lastCodecData) {
                return '';
            }
            var chartId = item.getAttribute('data-chart');
            var codecName = item.getAttribute('data-codec');
            var pathsProp = CODEC_PATH_MAP[chartId];
            var categories = CODEC_CATEGORY_MAP[chartId];
            var result = collectCodecPaths(_lastCodecData, pathsProp, codecName,
                categories);
            return renderFileTree(result, codecName);
        }
    });
}

function attachDonutHoverTooltips() {
    var charts = document.querySelectorAll('.donut-container');
    for (var c = 0; c < charts.length; c++) {
        (function (container) {
            // Bind events directly on <path> elements (not <g>) because
            // mouseenter/mouseleave are non-bubbling and <g> with pointer-events:none
            // would never receive them. We use .closest() to reach the parent <g> data attributes.
            var paths = container.querySelectorAll('.donut-segment path');

            for (var i = 0; i < paths.length; i++) {
                // Desktop: mouse hover shows tooltip + highlight
                paths[i].addEventListener('mouseenter', function (evt) {
                    var seg = this.closest('.donut-segment');
                    seg.classList.add('donut-segment-hover');
                    showDonutTooltip(container, evt, seg);
                    _activeTooltipSegmentId = seg.getAttribute('data-segment-id');
                });

                paths[i].addEventListener('mousemove', function (evt) {
                    var seg = this.closest('.donut-segment');
                    showDonutTooltip(container, evt, seg);
                });

                paths[i].addEventListener('mouseleave', function () {
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
                    var segId = seg.getAttribute('data-segment-id');

                    if (_activeTooltipSegmentId === segId) {
                        // Second tap on same segment — trigger the click action
                        seg.classList.remove('donut-segment-hover');
                        hideDonutTooltip(container);
                        _activeTooltipSegmentId = null;
                        triggerCodecRowForSegment(seg);
                    } else {
                        // First tap — show tooltip + highlight
                        // Remove highlight from any previously highlighted segment
                        var prevHighlighted = container.querySelectorAll('.donut-segment-hover');
                        for (var h = 0; h < prevHighlighted.length; h++) {
                            prevHighlighted[h].classList.remove('donut-segment-hover');
                        }
                        seg.classList.add('donut-segment-hover');
                        // Create a synthetic position from touch coordinates
                        var touch = evt.changedTouches && evt.changedTouches[0];
                        var syntheticEvt = touch
                            ? {clientX: touch.clientX, clientY: touch.clientY}
                            : evt;
                        showDonutTooltip(container, syntheticEvt, seg);
                        _activeTooltipSegmentId = segId;
                    }
                });
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

    // Video-only libraries (Movies + TV Shows + Other) — used for video-specific charts
    var videoLibraries = (data.Movies || []).concat(data.TvShows || []).concat(data.Other || []);
    // Music-only libraries — used for music-specific charts
    var musicLibraries = data.Music || [];

    var videoCodecs = aggregateDict(videoLibraries, 'VideoCodecs');
    var videoAudioCodecs = aggregateDict(videoLibraries, 'VideoAudioCodecs');
    var musicAudioCodecs = aggregateDict(musicLibraries, 'MusicAudioCodecs');
    var containers = aggregateDict(data.Libraries, 'ContainerFormats');
    var resolutions = aggregateDict(videoLibraries, 'Resolutions');

    var videoCodecSizes = aggregateDict(videoLibraries, 'VideoCodecSizes');
    var videoAudioCodecSizes = aggregateDict(videoLibraries, 'VideoAudioCodecSizes');
    var musicAudioCodecSizes = aggregateDict(musicLibraries, 'MusicAudioCodecSizes');
    var containerSizes = aggregateDict(data.Libraries, 'ContainerSizes');
    var resolutionSizes = aggregateDict(videoLibraries, 'ResolutionSizes');

    var codecsHtml = '<div class="charts-row">';
    codecsHtml += '<div class="chart-box"><h4>🎬 ' + T('videoCodecs', 'Video Codecs') + '</h4>';
    codecsHtml += renderDonutChart(videoCodecs, videoCodecSizes, 'videoCodecs', videoLibraries, 'VideoCodecs');
    codecsHtml += '</div>';

    var hasVideoAudio = Object.keys(videoAudioCodecs).length > 0;
    var hasMusicAudio = Object.keys(musicAudioCodecs).length > 0;

    if (hasVideoAudio) {
        codecsHtml += '<div class="chart-box"><h4>🔊 ' + T('videoAudioCodecs', 'Video Audio Codecs') + '</h4>';
        codecsHtml += renderDonutChart(videoAudioCodecs, videoAudioCodecSizes, 'videoAudioCodecs', videoLibraries,
            'VideoAudioCodecs');
        codecsHtml += '</div>';
    }
    if (hasMusicAudio) {
        codecsHtml += '<div class="chart-box"><h4>🎵 ' + T('musicAudioCodecs', 'Music Audio Codecs') + '</h4>';
        codecsHtml += renderDonutChart(musicAudioCodecs, musicAudioCodecSizes, 'musicAudioCodecs', musicLibraries,
            'MusicAudioCodecs');
        codecsHtml += '</div>';
    }
    codecsHtml += '<div class="chart-box"><h4>📦 ' + T('containerFormats', 'Container Formats') + '</h4>';
    codecsHtml += renderDonutChart(containers, containerSizes, 'containers', data.Libraries, 'ContainerFormats');
    codecsHtml += '</div>';
    codecsHtml += '<div class="chart-box"><h4>📐 ' + T('resolutions', 'Resolutions') + '</h4>';
    codecsHtml += renderDonutChart(resolutions, resolutionSizes, 'resolutions', videoLibraries, 'Resolutions');
    codecsHtml += '</div>';
    codecsHtml += '</div>';

    var codecsContainer = document.getElementById('codecsContent');
    if (codecsContainer) {
        codecsContainer.innerHTML = codecsHtml;
        attachCodecClickHandlers();
        attachDonutHoverTooltips();
    }
}