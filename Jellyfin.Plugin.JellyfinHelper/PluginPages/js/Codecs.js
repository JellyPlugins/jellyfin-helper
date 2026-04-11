// --- Codecs Tab ---

    // SVG donut chart generator
    function renderDonutChart(data, size) {
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

        // Sort by value descending
        entries.sort(function (a, b) { return b.value - a.value; });

        var cx = size / 2, cy = size / 2, r = size * 0.38, strokeWidth = size * 0.18;
        var circumference = 2 * Math.PI * r;
        var offset = 0;

        var svg = '<svg width="' + size + '" height="' + size + '" viewBox="0 0 ' + size + ' ' + size + '">';

        // Background circle
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

        // Legend
        var legend = '<div class="donut-legend">';
        for (var j = 0; j < entries.length; j++) {
            var c = DONUT_COLORS[j % DONUT_COLORS.length];
            var p = (entries[j].value / total * 100).toFixed(1);
            legend += '<div class="donut-legend-item">' +
                '<div class="donut-legend-dot" style="background:' + c + '"></div>' +
                escHtml(entries[j].label) + ' (' + p + '%)</div>';
        }
        legend += '</div>';

        return '<div class="donut-container">' + svg + '</div>' + legend;
    }

    function fillCodecsData(data) {
        var videoCodecs = aggregateDict(data.Libraries, 'VideoCodecs');
        var videoAudioCodecs = aggregateDict(data.Libraries, 'VideoAudioCodecs');
        var musicAudioCodecs = aggregateDict(data.Libraries, 'MusicAudioCodecs');
        var containers = aggregateDict(data.Libraries, 'ContainerFormats');
        var resolutions = aggregateDict(data.Libraries, 'Resolutions');

        var codecsHtml = '<div class="charts-row">';
        codecsHtml += '<div class="chart-box"><h4>' + T('videoCodecs', '🎬 Video Codecs') + '</h4>';
        codecsHtml += renderDonutChart(videoCodecs);
        codecsHtml += '</div>';
        var hasVideoAudio = Object.keys(videoAudioCodecs).length > 0;
        var hasMusicAudio = Object.keys(musicAudioCodecs).length > 0;
        if (hasVideoAudio) {
            codecsHtml += '<div class="chart-box"><h4>' + T('videoAudioCodecs', '🔊 Video Audio Codecs') + '</h4>';
            codecsHtml += renderDonutChart(videoAudioCodecs);
            codecsHtml += '</div>';
        }
        if (hasMusicAudio) {
            codecsHtml += '<div class="chart-box"><h4>' + T('musicAudioCodecs', '🎵 Music Audio Codecs') + '</h4>';
            codecsHtml += renderDonutChart(musicAudioCodecs);
            codecsHtml += '</div>';
        }
        codecsHtml += '<div class="chart-box"><h4>' + T('containerFormats', '📦 Container Formats') + '</h4>';
        codecsHtml += renderDonutChart(containers);
        codecsHtml += '</div>';
        codecsHtml += '<div class="chart-box"><h4>' + T('resolutions', '📐 Resolutions') + '</h4>';
        codecsHtml += renderDonutChart(resolutions);
        codecsHtml += '</div>';
        codecsHtml += '</div>';

        var codecsContainer = document.getElementById('codecsContent');
        if (codecsContainer) codecsContainer.innerHTML = codecsHtml;
    }