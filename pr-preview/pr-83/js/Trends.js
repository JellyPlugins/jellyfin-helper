// --- Trends Tab (Growth Timeline) ---

    function formatGranularityLabel(dateStr, granularity) {
        var d = new Date(dateStr);
        if (isNaN(d.getTime())) return dateStr;

        switch (granularity) {
            case 'yearly':
                return d.getFullYear().toString();
            case 'quarterly':
                var q = Math.floor(d.getMonth() / 3) + 1;
                return 'Q' + q + ' ' + d.getFullYear();
            case 'monthly':
                return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short' });
            case 'weekly':
                return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
            case 'daily':
                return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
            default:
                return d.toLocaleDateString();
        }
    }

    /**
     * Interpolates missing intermediate buckets between sparse data points.
     * When the backend deduplicates consecutive identical points, gaps appear
     * in the timeline. This function fills those gaps by carrying forward
     * the previous point's values so the chart has a continuous line.
     * A safety guard prevents runaway iteration.
     */
    function interpolateDataPoints(dataPoints, granularity) {
        if (dataPoints.length < 2) return dataPoints;

        var maxPoints = 10000;
        var result = [];
        var truncated = false;
        for (var i = 0; i < dataPoints.length; i++) {
            result.push(dataPoints[i]);

            if (result.length >= maxPoints) {
                truncated = true;
                break;
            }

            if (i < dataPoints.length - 1) {
                var currentDate = new Date(dataPoints[i].date);
                var nextDate = new Date(dataPoints[i + 1].date);

                // Advance one bucket at a time and fill gaps
                var fillDate = advanceBucketDate(currentDate, granularity);
                while (fillDate < nextDate && result.length < maxPoints - 1) {
                    result.push({
                        date: fillDate.toISOString(),
                        cumulativeSize: dataPoints[i].cumulativeSize,
                        cumulativeFileCount: dataPoints[i].cumulativeFileCount
                    });
                    fillDate = advanceBucketDate(fillDate, granularity);
                }

                if (fillDate < nextDate) {
                    truncated = true;
                    break;
                }
            }
        }

        // Ensure the last real data point is always included so the chart doesn't end early
        if (truncated) {
            result[result.length - 1] = dataPoints[dataPoints.length - 1];
            console.warn('[JellyfinHelper] Trend timeline truncated to ' + maxPoints + ' points.');
        }
        return result;
    }

    /**
     * Advances a date by one bucket interval based on the granularity.
     */
    function advanceBucketDate(date, granularity) {
        var d = new Date(date.getTime());
        switch (granularity) {
            case 'daily':
                d.setUTCDate(d.getUTCDate() + 1);
                break;
            case 'weekly':
                d.setUTCDate(d.getUTCDate() + 7);
                break;
            case 'monthly':
                d.setUTCMonth(d.getUTCMonth() + 1);
                break;
            case 'quarterly':
                d.setUTCMonth(d.getUTCMonth() + 3);
                break;
            case 'yearly':
                d.setUTCFullYear(d.getUTCFullYear() + 1);
                break;
            default:
                d.setUTCMonth(d.getUTCMonth() + 1);
        }
        return d;
    }

    function renderTrendChart(timeline) {
        if (!timeline || !timeline.dataPoints || timeline.dataPoints.length < 2) {
            return '<div class="trend-empty">' + T('trendEmpty', 'Not enough data yet. Growth timeline is computed during each scheduled scan.') + '</div>';
        }

        // The backend already groups data into the correct granularity and deduplicates
        // consecutive identical points for compact storage. We only need to interpolate
        // the gaps back for a continuous chart line.
        var validGranularities = ['daily', 'weekly', 'monthly', 'quarterly', 'yearly'];
        var rawGranularity = timeline.granularity || 'monthly';
        var granularity = String(rawGranularity).toLowerCase();
        if (validGranularities.indexOf(granularity) === -1) {
            console.warn('[JellyfinHelper] Unknown granularity "' + rawGranularity + '", falling back to "monthly".');
            granularity = 'monthly';
        }
        var dataPoints = interpolateDataPoints(timeline.dataPoints, granularity);

        // Find max cumulative size for threshold calculation
        var peakSize = 0;
        for (var p = 0; p < dataPoints.length; p++) {
            if (dataPoints[p].cumulativeSize > peakSize) peakSize = dataPoints[p].cumulativeSize;
        }
        // Treat points below 0.5% of peak as "visually zero" — they sit on the 0-line
        // and clutter the chart with a long flat baseline before actual growth starts
        var zeroThreshold = peakSize * 0.005;

        // Skip leading near-zero data points (keep at most one as visual baseline start)
        var firstSignificant = -1;
        for (var z = 0; z < dataPoints.length; z++) {
            if (dataPoints[z].cumulativeSize > zeroThreshold) {
                firstSignificant = z;
                break;
            }
        }
        if (firstSignificant < 0) firstSignificant = dataPoints.length - 1;
        // Keep one near-zero point before the first significant as the "start from zero" baseline
        var startIndex = Math.max(0, firstSignificant - 1);
        if (startIndex > 0) {
            dataPoints = dataPoints.slice(startIndex);
        }

        if (dataPoints.length < 2) {
            return '<div class="trend-empty">' + T('trendEmpty', 'Not enough data yet. Growth timeline is computed during each scheduled scan.') + '</div>';
        }

        var width = 880, height = 240, padL = 65, padR = 45, padT = 20, padB = 56;
        var chartW = width - padL - padR;
        var chartH = height - padT - padB;

        // Find max cumulative size and compute "nice" Y-axis scale
        var rawMax = 0;
        for (var i = 0; i < dataPoints.length; i++) {
            if (dataPoints[i].cumulativeSize > rawMax) rawMax = dataPoints[i].cumulativeSize;
        }
        if (rawMax === 0) rawMax = 1;

        // Compute nice tick interval in binary units (1024-based) so Y-axis labels
        // show clean values like "5 TB", "10 TB" instead of "9.09 TB", "27.28 TB".
        // formatBytes() uses 1024-based divisions, so we must calculate in the same base.
        var niceTickCount = 4;
        var binaryUnits = [1, 1024, 1024*1024, 1024*1024*1024, 1024*1024*1024*1024, 1024*1024*1024*1024*1024];
        var unitIdx = 0;
        var humanMax = rawMax;
        while (humanMax >= 1024 && unitIdx < binaryUnits.length - 1) {
            humanMax /= 1024;
            unitIdx++;
        }
        // humanMax is now in the display unit (e.g. 22.26 for TB)
        var rawIntervalHuman = humanMax / niceTickCount;
        var mag10 = Math.pow(10, Math.floor(Math.log10(rawIntervalHuman > 0 ? rawIntervalHuman : 1)));
        var res = rawIntervalHuman / mag10;
        var niceIntervalHuman;
        if (res <= 1) niceIntervalHuman = mag10;
        else if (res <= 2) niceIntervalHuman = 2 * mag10;
        else if (res <= 5) niceIntervalHuman = 5 * mag10;
        else niceIntervalHuman = 10 * mag10;

        var yMaxHuman = Math.ceil(humanMax / niceIntervalHuman) * niceIntervalHuman;
        if (yMaxHuman === 0) yMaxHuman = 1;
        // Convert back to bytes
        var yMax = yMaxHuman * binaryUnits[unitIdx];
        var niceInterval = niceIntervalHuman * binaryUnits[unitIdx];

        // Build Y-axis ticks (from 0 to yMax)
        var yTicks = [];
        for (var t = 0; t <= yMax; t += niceInterval) {
            yTicks.push(Math.round(t));
        }
        // Ensure yMax is included
        if (yTicks[yTicks.length - 1] < Math.round(yMax)) {
            yTicks.push(Math.round(yMax));
        }

        // Build points — map data against yMax (not rawMax) so points align with grid
        var points = [];
        var step = dataPoints.length > 1 ? chartW / (dataPoints.length - 1) : 0;
        for (var j = 0; j < dataPoints.length; j++) {
            var x = padL + j * step;
            var y = padT + chartH - (dataPoints[j].cumulativeSize / yMax * chartH);
            points.push(x.toFixed(1) + ',' + y.toFixed(1));
        }

        var svg = '<svg width="100%" viewBox="0 0 ' + width + ' ' + height + '" preserveAspectRatio="xMidYMid meet">';

        // Grid lines from nice Y-axis ticks
        for (var g = 0; g < yTicks.length; g++) {
            var gy = padT + chartH - (yTicks[g] / yMax * chartH);
            svg += '<line x1="' + padL + '" y1="' + gy.toFixed(1) + '" x2="' + (width - padR) + '" y2="' + gy.toFixed(1) + '" stroke="rgba(255,255,255,0.06)" />';
            svg += '<text x="' + (padL - 5) + '" y="' + (gy + 4).toFixed(1) + '" text-anchor="end" fill="rgba(255,255,255,0.4)" font-size="10">' + formatBytes(yTicks[g]) + '</text>';
        }

        // Area fill
        var areaPoints = padL + ',' + (padT + chartH) + ' ' + points.join(' ') + ' ' + (padL + (dataPoints.length - 1) * step) + ',' + (padT + chartH);
        svg += '<polygon points="' + areaPoints + '" fill="rgba(0,164,220,0.15)" />';

        // Line
        svg += '<polyline points="' + points.join(' ') + '" fill="none" stroke="#00a4dc" stroke-width="2" />';

        // Data points with tooltips — adapt circle size to density
        // At many data points the circles overlap, so shrink or hide them
        var dotRadius = dataPoints.length <= 60 ? 3 : (dataPoints.length <= 120 ? 1.5 : 0);
        for (var k = 0; k < points.length; k++) {
            var coords = points[k].split(',');
            var label = formatGranularityLabel(dataPoints[k].date, granularity);
            var sizeLabel = formatBytes(dataPoints[k].cumulativeSize);
            var filesLabel = dataPoints[k].cumulativeFileCount + ' files';
            var tooltip = '<title>' + label + ': ' + sizeLabel + ' (' + filesLabel + ')</title>';
            if (dotRadius > 0) {
                svg += '<circle cx="' + coords[0] + '" cy="' + coords[1] + '" r="' + dotRadius + '" fill="#00a4dc">' + tooltip + '</circle>';
            } else {
                // Invisible hover zone for tooltips when dots are hidden
                svg += '<rect x="' + (parseFloat(coords[0]) - step / 2).toFixed(1) + '" y="' + padT + '" width="' + Math.max(step, 2).toFixed(1) + '" height="' + chartH + '" fill="transparent">' + tooltip + '</rect>';
            }
        }

        // X-axis labels — show evenly spaced labels
        var labelCount = Math.min(dataPoints.length, 10);
        var labelStep = Math.max(1, Math.floor((dataPoints.length - 1) / (labelCount - 1)));
        for (var m = 0; m < dataPoints.length; m += labelStep) {
            var lx = padL + m * step;
            var lbl = formatGranularityLabel(dataPoints[m].date, granularity);
            svg += '<text x="' + lx + '" y="' + (padT + chartH + 18) + '" text-anchor="middle" fill="rgba(255,255,255,0.55)" font-size="10" font-weight="500">' + lbl + '</text>';
        }
        // Always show last label if not already shown
        if ((dataPoints.length - 1) % labelStep !== 0) {
            var lastX = padL + (dataPoints.length - 1) * step;
            var lastLbl = formatGranularityLabel(dataPoints[dataPoints.length - 1].date, granularity);
            svg += '<text x="' + lastX + '" y="' + (padT + chartH + 18) + '" text-anchor="end" fill="rgba(255,255,255,0.55)" font-size="10" font-weight="500">' + lastLbl + '</text>';
        }

        // X-axis baseline
        svg += '<line x1="' + padL + '" y1="' + (padT + chartH) + '" x2="' + (width - padR) + '" y2="' + (padT + chartH) + '" stroke="rgba(255,255,255,0.12)" />';

        svg += '</svg>';

        // Metadata line below chart
        var meta = '<div class="trend-meta" style="text-align:center;color:rgba(255,255,255,0.35);font-size:11px;margin-top:4px;">';
        meta += T('trendGranularity', 'Granularity') + ': ' + granularity;
        meta += ' · ' + (timeline.totalFilesScanned || 0) + ' ' + T('trendFiles', 'media files');
        if (timeline.earliestFileDate) {
            meta += ' · ' + T('trendEarliest', 'Earliest') + ': ' + new Date(timeline.earliestFileDate).toLocaleDateString();
        }
        meta += '</div>';

        return '<div class="trend-chart">' + svg + '</div>' + meta;
    }

    function loadTrendData(forceRefresh) {
        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/GrowthTimeline');
        if (forceRefresh) {
            url += (url.indexOf('?') === -1 ? '?' : '&') + 'forceRefresh=true';
        }

        apiClient.ajax({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (timeline) {
            var container = document.getElementById('trendChartContainer');
            if (container) {
                container.innerHTML = renderTrendChart(timeline);
            }
        }, function () {
            var container = document.getElementById('trendChartContainer');
            if (container) {
                container.innerHTML = '<div class="trend-empty">' + T('trendError', 'Could not load trend data.') + '</div>';
            }
        });
    }
