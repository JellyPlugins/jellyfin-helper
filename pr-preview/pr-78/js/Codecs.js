// --- Codecs Tab ---

    // Store last scan data for codec detail clicks
    var _lastCodecData = null;

    // SVG donut chart generator (returns only the SVG + container, no legend)
    function renderDonutSvg(data, size) {
        size = size || 160;
        var entries = [];
        var total = 0;
        for (var key in data) {
            if (Object.prototype.hasOwnProperty.call(data, key) && data[key] > 0) {
                entries.push({ label: key, value: data[key] });
                total += data[key];
            }
        }
        if (total === 0) return '<p style="opacity:0.5;">' + T('noData', 'No data') + '</p>';

        entries.sort(function (a, b) { return b.value - a.value; });

        var cx = size / 2, cy = size / 2, r = size * 0.38, strokeWidth = size * 0.18;
        var circumference = 2 * Math.PI * r;
        var offset = 0;

        var svg = '<svg width="' + size + '" height="' + size + '" viewBox="0 0 ' + size + ' ' + size + '">';
        svg += '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="none" stroke="rgba(255,255,255,0.05)" stroke-width="' + strokeWidth + '"/>';

        for (var i = 0; i < entries.length; i++) {
            var pct = entries[i].value / total;
            var dashLen = pct * circumference;
            var dashGap = circumference - dashLen;
            var color = DONUT_COLORS[i % DONUT_COLORS.length];

            svg += '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="none" ' +
                'stroke="' + color + '" stroke-width="' + strokeWidth + '" ' +
                'stroke-dasharray="' + dashLen.toFixed(2) + ' ' + dashGap.toFixed(2) + '" ' +
                'stroke-dashoffset="' + (-offset).toFixed(2) + '" ' +
                'transform="rotate(-90 ' + cx + ' ' + cy + ')">' +
                '<title>' + escHtml(entries[i].label) + ': ' + (pct * 100).toFixed(1) + '%</title></circle>';

            offset += dashLen;
        }

        svg += '</svg>';
        return '<div class="donut-container">' + svg + '</div>';
    }

    // Build the clickable codec breakdown table below the donut
    function renderCodecBreakdown(countDict, sizeDict, chartId) {
        var entries = [];
        var total = 0;
        for (var key in countDict) {
            if (Object.prototype.hasOwnProperty.call(countDict, key) && countDict[key] > 0) {
                var size = (sizeDict && sizeDict[key]) ? sizeDict[key] : 0;
                entries.push({ label: key, count: countDict[key], size: size });
                total += countDict[key];
            }
        }
        if (entries.length === 0) return '';

        entries.sort(function (a, b) { return b.count - a.count; });

        var html = '<div class="codec-breakdown">';
        for (var i = 0; i < entries.length; i++) {
            var color = DONUT_COLORS[i % DONUT_COLORS.length];
            var pct = (entries[i].count / total * 100).toFixed(1);
            var isActive = '';

            html += '<div class="codec-row codec-clickable' + isActive + '" data-chart="' + escAttr(chartId) + '" data-codec="' + escAttr(entries[i].label) + '" role="button" tabindex="0">';
            html += '<div class="codec-row-color" style="background:' + color + '"></div>';
            html += '<div class="codec-row-info">';
            html += '<span class="codec-row-name">' + escHtml(entries[i].label) + '</span>';
            html += '<span class="codec-row-stats">' + entries[i].count + ' ' + T('files', 'files') + ' · ' + pct + '% · ' + formatBytes(entries[i].size) + '</span>';
            html += '</div>';
            html += '<div class="codec-row-bar"><div class="codec-row-bar-fill" style="width:' + pct + '%;background:' + color + '"></div></div>';
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

    // Collect paths for a specific codec from libraries, filtered by categories
    function collectCodecPaths(data, pathsProp, codecName, categories) {
        var moviePaths = [];
        var tvPaths = [];
        var musicPaths = [];
        var otherPaths = [];

        var includeMovies = !categories || categories.movies;
        var includeTvShows = !categories || categories.tvShows;
        var includeMusic = !categories || categories.music;
        var includeOther = !categories || categories.other;

        // Movies
        if (includeMovies && data.Movies) {
            for (var m = 0; m < data.Movies.length; m++) {
                var lib = data.Movies[m];
                var dict = lib[pathsProp];
                if (dict && dict[codecName]) {
                    for (var i = 0; i < dict[codecName].length; i++) {
                        moviePaths.push(dict[codecName][i]);
                    }
                }
            }
        }

        // TV Shows
        if (includeTvShows && data.TvShows) {
            for (var t = 0; t < data.TvShows.length; t++) {
                var tvLib = data.TvShows[t];
                var tvDict = tvLib[pathsProp];
                if (tvDict && tvDict[codecName]) {
                    for (var j = 0; j < tvDict[codecName].length; j++) {
                        tvPaths.push(tvDict[codecName][j]);
                    }
                }
            }
        }

        // Music
        if (includeMusic && data.Music) {
            for (var mu = 0; mu < data.Music.length; mu++) {
                var muLib = data.Music[mu];
                var muDict = muLib[pathsProp];
                if (muDict && muDict[codecName]) {
                    for (var k = 0; k < muDict[codecName].length; k++) {
                        musicPaths.push(muDict[codecName][k]);
                    }
                }
            }
        }

        // Other Libraries
        if (includeOther && data.Other) {
            for (var o = 0; o < data.Other.length; o++) {
                var oLib = data.Other[o];
                var oDict = oLib[pathsProp];
                if (oDict && oDict[codecName]) {
                    for (var l = 0; l < oDict[codecName].length; l++) {
                        otherPaths.push(oDict[codecName][l]);
                    }
                }
            }
        }

        return {
            movies: moviePaths,
            tvShows: tvPaths,
            music: musicPaths,
            other: otherPaths,
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
        'videoCodecs': { movies: true, tvShows: true, music: false, other: true },
        'videoAudioCodecs': { movies: true, tvShows: true, music: false, other: true },
        'musicAudioCodecs': { movies: false, tvShows: false, music: true, other: false },
        'containers': { movies: true, tvShows: true, music: true, other: true },
        'resolutions': { movies: true, tvShows: true, music: false, other: true }
    };

    // Attach click handlers to codec rows
    function attachCodecClickHandlers() {
        var rows = document.querySelectorAll('.codec-clickable');
        for (var i = 0; i < rows.length; i++) {
            rows[i].addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); this.click(); }
            });
            rows[i].addEventListener('click', function () {
                var chartId = this.getAttribute('data-chart');
                var codecName = this.getAttribute('data-codec');
                var panel = document.getElementById('codecDetail_' + chartId);
                if (!panel || !_lastCodecData) return;

                // Toggle: if same codec is already shown, close it
                if (this.classList.contains('codec-row-active')) {
                    panel.innerHTML = '';
                    panel.classList.remove('file-tree-panel-visible');
                    this.classList.remove('codec-row-active');
                    return;
                }

                // Remove active state from all rows in this chart
                var chartRows = document.querySelectorAll('.codec-clickable[data-chart="' + chartId + '"]');
                for (var j = 0; j < chartRows.length; j++) {
                    chartRows[j].classList.remove('codec-row-active');
                }

                // Close all other detail panels
                var allPanels = document.querySelectorAll('.file-tree-panel');
                for (var p = 0; p < allPanels.length; p++) {
                    if (allPanels[p].id !== 'codecDetail_' + chartId) {
                        allPanels[p].innerHTML = '';
                        allPanels[p].classList.remove('file-tree-panel-visible');
                    }
                }
                // Also deactivate rows in other charts
                var allRows = document.querySelectorAll('.codec-clickable');
                for (var r = 0; r < allRows.length; r++) {
                    if (allRows[r].getAttribute('data-chart') !== chartId) {
                        allRows[r].classList.remove('codec-row-active');
                    }
                }

                this.classList.add('codec-row-active');

                var pathsProp = CODEC_PATH_MAP[chartId];
                var categories = CODEC_CATEGORY_MAP[chartId];
                var result = collectCodecPaths(_lastCodecData, pathsProp, codecName, categories);
                panel.innerHTML = renderFileTree(result, codecName);
                panel.classList.add('file-tree-panel-visible');

                // Smooth scroll the panel into view
                setTimeout(function () { panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' }); }, 50);
            });
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
        codecsHtml += '<div class="chart-box"><h4>' + T('videoCodecs', '🎬 Video Codecs') + '</h4>';
        codecsHtml += renderDonutChart(videoCodecs, videoCodecSizes, 'videoCodecs');
        codecsHtml += '</div>';

        var hasVideoAudio = Object.keys(videoAudioCodecs).length > 0;
        var hasMusicAudio = Object.keys(musicAudioCodecs).length > 0;

        if (hasVideoAudio) {
            codecsHtml += '<div class="chart-box"><h4>' + T('videoAudioCodecs', '🔊 Video Audio Codecs') + '</h4>';
            codecsHtml += renderDonutChart(videoAudioCodecs, videoAudioCodecSizes, 'videoAudioCodecs');
            codecsHtml += '</div>';
        }
        if (hasMusicAudio) {
            codecsHtml += '<div class="chart-box"><h4>' + T('musicAudioCodecs', '🎵 Music Audio Codecs') + '</h4>';
            codecsHtml += renderDonutChart(musicAudioCodecs, musicAudioCodecSizes, 'musicAudioCodecs');
            codecsHtml += '</div>';
        }
        codecsHtml += '<div class="chart-box"><h4>' + T('containerFormats', '📦 Container Formats') + '</h4>';
        codecsHtml += renderDonutChart(containers, containerSizes, 'containers');
        codecsHtml += '</div>';
        codecsHtml += '<div class="chart-box"><h4>' + T('resolutions', '📐 Resolutions') + '</h4>';
        codecsHtml += renderDonutChart(resolutions, resolutionSizes, 'resolutions');
        codecsHtml += '</div>';
        codecsHtml += '</div>';

        var codecsContainer = document.getElementById('codecsContent');
        if (codecsContainer) {
            codecsContainer.innerHTML = codecsHtml;
            attachCodecClickHandlers();
        }
    }
