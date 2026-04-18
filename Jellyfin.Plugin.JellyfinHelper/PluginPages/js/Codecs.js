// --- Codecs Tab ---

// Store last scan data for codec detail clicks
var _lastCodecData = null;

// SVG donut tooltip helpers
function showDonutTooltip(container, evt, segment) {
    var tooltip = container.querySelector('.donut-tooltip');
    if (!tooltip) {
        return;
    }

    tooltip.textContent = segment.getAttribute('data-title') || '';
    tooltip.classList.add('visible');

    var containerRect = container.getBoundingClientRect();
    tooltip.style.left = (evt.clientX - containerRect.left + 12) + 'px';
    tooltip.style.top = (evt.clientY - containerRect.top + 12) + 'px';
}

function hideDonutTooltip(container) {
    var tooltip = container.querySelector('.donut-tooltip');
    if (tooltip) {
        tooltip.classList.remove('visible');
    }
}

// SVG donut chart generator (returns only the SVG + container, no legend)
function renderDonutSvg(data, size) {
    size = size || 160;
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
    var circumference = 2 * Math.PI * r;
    var offset = 0;

    var donutContainer = '<div class="donut-container">';
    donutContainer += '<div class="donut-tooltip" aria-hidden="true"></div>';
    donutContainer += '<svg class="donut-svg" width="' + size + '" height="' + size + '" viewBox="0 0 ' + size + ' '
        + size + '">';
    donutContainer += '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="none" stroke="rgba(255,255,255,0.05)"'
        + ' stroke-width="' + strokeWidth + '"/>';

    for (var i = 0; i < entries.length; i++) {
        var pct = entries[i].value / total;
        var dashLen = pct * circumference;
        var dashGap = circumference - dashLen;
        var color = DONUT_COLORS[i % DONUT_COLORS.length];
        var titleText = entries[i].label + ': ' + (pct * 100).toFixed(1) + '%';

        donutContainer += '<g class="donut-segment" data-title="' + escAttr(titleText) + '">';
        donutContainer += '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="none" ' +
            'stroke="' + color + '" stroke-width="' + strokeWidth + '" ' +
            'stroke-dasharray="' + dashLen.toFixed(2) + ' ' + dashGap.toFixed(2) + '" ' +
            'stroke-dashoffset="' + (-offset).toFixed(2) + '" ' +
            'transform="rotate(-90 ' + cx + ' ' + cy + ')"></circle>';
        donutContainer += '</g>';

        offset += dashLen;
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
function renderDonutChart(countDict, sizeDict, chartId) {
    var svgHtml = renderDonutSvg(countDict);
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
            var segments = container.querySelectorAll('.donut-segment');

            for (var i = 0; i < segments.length; i++) {
                segments[i].addEventListener('mouseenter', function (evt) {
                    showDonutTooltip(container, evt, this);
                });

                segments[i].addEventListener('mousemove', function (evt) {
                    showDonutTooltip(container, evt, this);
                });

                segments[i].addEventListener('mouseleave', function () {
                    hideDonutTooltip(container);
                });
            }
        })(charts[c]);
    }
}

function fillCodecsData(data) {
    _lastCodecData = data;

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
    codecsHtml += renderDonutChart(videoCodecs, videoCodecSizes, 'videoCodecs');
    codecsHtml += '</div>';

    var hasVideoAudio = Object.keys(videoAudioCodecs).length > 0;
    var hasMusicAudio = Object.keys(musicAudioCodecs).length > 0;

    if (hasVideoAudio) {
        codecsHtml += '<div class="chart-box"><h4>🔊 ' + T('videoAudioCodecs', 'Video Audio Codecs') + '</h4>';
        codecsHtml += renderDonutChart(videoAudioCodecs, videoAudioCodecSizes, 'videoAudioCodecs');
        codecsHtml += '</div>';
    }
    if (hasMusicAudio) {
        codecsHtml += '<div class="chart-box"><h4>🎵 ' + T('musicAudioCodecs', 'Music Audio Codecs') + '</h4>';
        codecsHtml += renderDonutChart(musicAudioCodecs, musicAudioCodecSizes, 'musicAudioCodecs');
        codecsHtml += '</div>';
    }
    codecsHtml += '<div class="chart-box"><h4>📦 ' + T('containerFormats', 'Container Formats') + '</h4>';
    codecsHtml += renderDonutChart(containers, containerSizes, 'containers');
    codecsHtml += '</div>';
    codecsHtml += '<div class="chart-box"><h4>📐 ' + T('resolutions', 'Resolutions') + '</h4>';
    codecsHtml += renderDonutChart(resolutions, resolutionSizes, 'resolutions');
    codecsHtml += '</div>';
    codecsHtml += '</div>';

    var codecsContainer = document.getElementById('codecsContent');
    if (codecsContainer) {
        codecsContainer.innerHTML = codecsHtml;
        attachCodecClickHandlers();
        attachDonutHoverTooltips();
    }
}
