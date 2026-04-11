// --- Trends Tab ---

    function renderTrendChart(snapshots) {
        if (!snapshots || snapshots.length < 2) {
            return '<div class="trend-empty">' + T('trendEmpty', 'Not enough historical data yet. Trend data is collected with each scan.') + '</div>';
        }

        var width = 880, height = 180, padL = 60, padR = 20, padT = 15, padB = 30;
        var chartW = width - padL - padR;
        var chartH = height - padT - padB;

        // Find min/max
        var maxSize = 0;
        for (var i = 0; i < snapshots.length; i++) {
            if (snapshots[i].TotalSize > maxSize) maxSize = snapshots[i].TotalSize;
        }
        if (maxSize === 0) maxSize = 1;

        // Build points
        var points = [];
        var step = snapshots.length > 1 ? chartW / (snapshots.length - 1) : 0;
        for (var j = 0; j < snapshots.length; j++) {
            var x = padL + j * step;
            var y = padT + chartH - (snapshots[j].TotalSize / maxSize * chartH);
            points.push(x.toFixed(1) + ',' + y.toFixed(1));
        }

        var svg = '<svg width="100%" viewBox="0 0 ' + width + ' ' + height + '" preserveAspectRatio="xMidYMid meet">';

        // Grid lines
        for (var g = 0; g <= 4; g++) {
            var gy = padT + (chartH / 4) * g;
            var val = maxSize - (maxSize / 4) * g;
            svg += '<line x1="' + padL + '" y1="' + gy + '" x2="' + (width - padR) + '" y2="' + gy + '" stroke="rgba(255,255,255,0.06)" />';
            svg += '<text x="' + (padL - 5) + '" y="' + (gy + 4) + '" text-anchor="end" fill="rgba(255,255,255,0.4)" font-size="10">' + formatBytes(val) + '</text>';
        }

        // Area fill
        var areaPoints = padL + ',' + (padT + chartH) + ' ' + points.join(' ') + ' ' + (padL + (snapshots.length - 1) * step) + ',' + (padT + chartH);
        svg += '<polygon points="' + areaPoints + '" fill="rgba(0,164,220,0.15)" />';

        // Line
        svg += '<polyline points="' + points.join(' ') + '" fill="none" stroke="#00a4dc" stroke-width="2" />';

        // Data points
        for (var k = 0; k < points.length; k++) {
            var coords = points[k].split(',');
            var ts = snapshots[k].Timestamp ? new Date(snapshots[k].Timestamp).toLocaleDateString() : '';
            svg += '<circle cx="' + coords[0] + '" cy="' + coords[1] + '" r="3" fill="#00a4dc">' +
                '<title>' + ts + ': ' + formatBytes(snapshots[k].TotalSize) + '</title></circle>';
        }

        // X-axis labels (first and last)
        if (snapshots.length > 0) {
            var firstDate = snapshots[0].Timestamp ? new Date(snapshots[0].Timestamp).toLocaleDateString() : '';
            var lastDate = snapshots[snapshots.length - 1].Timestamp ? new Date(snapshots[snapshots.length - 1].Timestamp).toLocaleDateString() : '';
            svg += '<text x="' + padL + '" y="' + (height - 5) + '" text-anchor="start" fill="rgba(255,255,255,0.4)" font-size="10">' + firstDate + '</text>';
            svg += '<text x="' + (width - padR) + '" y="' + (height - 5) + '" text-anchor="end" fill="rgba(255,255,255,0.4)" font-size="10">' + lastDate + '</text>';
        }

        svg += '</svg>';
        return '<div class="trend-chart">' + svg + '</div>';
    }

    function loadTrendData() {
        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Statistics/History');

        apiClient.ajax({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (snapshots) {
            var container = document.getElementById('trendChartContainer');
            if (container) {
                container.innerHTML = renderTrendChart(snapshots);
            }
        }, function () {
            var container = document.getElementById('trendChartContainer');
            if (container) {
                container.innerHTML = '<div class="trend-empty">' + T('trendError', 'Could not load trend data.') + '</div>';
            }
        });
    }