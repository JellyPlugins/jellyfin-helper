// --- Trends Tab (Growth Timeline) ---

// In-memory cache for trend point data to avoid expensive DOM round-trips
var _lastTrendPointData = null;

function formatGranularityLabel(dateStr, granularity) {
    var d = new Date(dateStr);
    if (isNaN(d.getTime())) return '—';

    switch (granularity) {
        case 'yearly':
            return d.getUTCFullYear().toString();
        case 'quarterly': {
            var q = Math.floor(d.getUTCMonth() / 3) + 1;
            return 'Q' + q + ' ' + d.getUTCFullYear();
        }
        case 'monthly':
            return d.toLocaleDateString(undefined, {year: 'numeric', month: 'short', timeZone: 'UTC'});
        case 'weekly':
            return d.toLocaleDateString(undefined, {month: 'short', day: 'numeric', timeZone: 'UTC'});
        case 'daily':
            return d.toLocaleDateString(undefined, {month: 'short', day: 'numeric', timeZone: 'UTC'});
        default:
            return d.toLocaleDateString(undefined, {timeZone: 'UTC'});
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
        console.warn('[JellyfinHelper] Trend timeline truncated to ' + maxPoints + ' points (granularity: ' + granularity + ').');
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
    var binaryUnits = [1, 1024, 1024 * 1024, 1024 * 1024 * 1024, 1024 * 1024 * 1024 * 1024, 1024 * 1024 * 1024 * 1024 * 1024];
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

    // Area fill — use theme variable for consistent primary tint
    var areaFill = getComputedStyle(document.documentElement).getPropertyValue('--color-primary-light').trim() || 'rgba(0,164,220,0.15)';
    var areaPoints = padL + ',' + (padT + chartH) + ' ' + points.join(' ') + ' ' + (padL + (dataPoints.length - 1) * step) + ',' + (padT + chartH);
    svg += '<polygon points="' + areaPoints + '" fill="' + areaFill + '" />';

    // Line
    var trendColor = getComputedStyle(document.documentElement).getPropertyValue('--color-primary').trim() || '#00a4dc';
    svg += '<polyline points="' + points.join(' ') + '" fill="none" stroke="' + trendColor + '" stroke-width="2" />';

    // Invisible interaction overlay — full chart area rect for mouse/touch tracking
    svg += '<rect class="trend-hit-area" x="' + padL + '" y="' + padT + '" width="' + chartW + '" height="' + chartH + '" fill="transparent" />';

    // Small visible dots at each data point (always shown, small)
    var dotRadius = dataPoints.length <= 60 ? 2.5 : (dataPoints.length <= 200 ? 1.5 : 0);
    if (dotRadius > 0) {
        for (var k = 0; k < points.length; k++) {
            var coords = points[k].split(',');
            svg += '<circle cx="' + coords[0] + '" cy="' + coords[1] + '" r="' + dotRadius + '" fill="' + trendColor + '" opacity="0.6" />';
        }
    }

    // X-axis labels — dynamically adapt label count to prevent overlap
    var labelCount = Math.min(dataPoints.length, 10);
    // On narrow charts, reduce label count further
    if (dataPoints.length > 20) labelCount = Math.min(labelCount, 7);
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

    // Crosshair line, active dot, and tooltip (HTML overlays for interactivity)
    var overlays = '<div class="trend-crosshair"></div>';
    overlays += '<div class="trend-active-dot"></div>';
    overlays += '<div class="trend-tooltip"><div class="tt-date"></div><div class="tt-size"></div><div class="tt-files"></div></div>';

    // Metadata line below chart
    var meta = '<div class="trend-meta" style="text-align:center;color:rgba(255,255,255,0.35);font-size:11px;margin-top:4px;">';
    meta += T('trendGranularity', 'Granularity') + ': ' + granularity;
    var safeFileCount = Number(timeline.totalFilesScanned);
    if (!isFinite(safeFileCount) || safeFileCount < 0) safeFileCount = 0;
    meta += ' &middot; ' + safeFileCount + ' ' + T('trendFiles', 'media files');
    if (timeline.earliestFileDate) {
        meta += ' &middot; ' + T('trendEarliest', 'Earliest') + ': ' + new Date(timeline.earliestFileDate).toLocaleDateString(undefined, {timeZone: 'UTC'});
    }
    meta += '</div>';

    // Store chart metadata as data attributes for the interaction handler
    var chartDataAttr = ' data-trend-padl="' + padL + '"'
        + ' data-trend-padt="' + padT + '"'
        + ' data-trend-chartw="' + chartW + '"'
        + ' data-trend-charth="' + chartH + '"'
        + ' data-trend-width="' + width + '"'
        + ' data-trend-height="' + height + '"'
        + ' data-trend-count="' + dataPoints.length + '"'
        + ' data-trend-ymax="' + yMax + '"'
        + ' data-trend-granularity="' + granularity + '"';

    // Encode point data as HTML-safe data attribute for interaction lookup
    var pointData = [];
    for (var pd = 0; pd < dataPoints.length; pd++) {
        pointData.push({
            d: dataPoints[pd].date,
            s: dataPoints[pd].cumulativeSize,
            c: dataPoints[pd].cumulativeFileCount
        });
    }
    // Store as data attribute (HTML-encode quotes so it survives the single-file build)
    // Store point data in memory instead of serializing into the DOM
    _lastTrendPointData = pointData;

    // Diff panel — appears below chart on hover, shows delta vs current (last) data point
    var diffPanel = '<div class="trend-diff-panel">'
        + '<div class="trend-diff-content">'
        + '<div class="trend-diff-compare">'
        + '<div class="trend-diff-col">'
        + '<span class="trend-diff-dates"></span>'
        + '<span class="trend-diff-val trend-diff-then-size"></span>'
        + '<span class="trend-diff-cnt trend-diff-then-count"></span>'
        + '</div>'
        + '<span class="trend-diff-arrow">\u2192</span>'
        + '<div class="trend-diff-col">'
        + '<span class="trend-diff-now-date"></span>'
        + '<span class="trend-diff-val trend-diff-now-size"></span>'
        + '<span class="trend-diff-cnt trend-diff-now-count"></span>'
        + '</div>'
        + '</div>'
        + '<div class="trend-diff-delta">'
        + '<span class="trend-diff-stat trend-diff-size"></span>'
        + '<span class="trend-diff-stat trend-diff-files"></span>'
        + '</div>'
        + '</div></div>';

    return '<div class="trend-chart"' + chartDataAttr + '>'
        + svg + overlays
        + '</div>' + diffPanel + meta;
}

/**
 * Attaches interactive tooltip/crosshair behavior to the trend chart.
 * Called after renderTrendChart HTML is inserted into the DOM.
 */
function attachTrendInteraction(container) {
    var chart = container.querySelector('.trend-chart');
    if (!chart) return;

    var svgEl = chart.querySelector('svg');
    var tooltip = chart.querySelector('.trend-tooltip');
    var crosshair = chart.querySelector('.trend-crosshair');
    var activeDot = chart.querySelector('.trend-active-dot');
    if (!svgEl || !tooltip || !crosshair || !activeDot) return;

    var pointData = _lastTrendPointData;
    if (!pointData || pointData.length === 0) return;

    var padL = parseFloat(chart.getAttribute('data-trend-padl'));
    var padT = parseFloat(chart.getAttribute('data-trend-padt'));
    var chartW = parseFloat(chart.getAttribute('data-trend-chartw'));
    var chartH = parseFloat(chart.getAttribute('data-trend-charth'));
    var vbWidth = parseFloat(chart.getAttribute('data-trend-width'));
    var vbHeight = parseFloat(chart.getAttribute('data-trend-height'));
    var yMax = parseFloat(chart.getAttribute('data-trend-ymax'));
    var granularity = chart.getAttribute('data-trend-granularity');
    var count = pointData.length;
    var step = count > 1 ? chartW / (count - 1) : 0;

    function getPointIndex(clientX) {
        var rect = svgEl.getBoundingClientRect();
        // Account for preserveAspectRatio="xMidYMid meet" letterboxing
        var scale = Math.min(rect.width / vbWidth, rect.height / vbHeight);
        var offsetX = (rect.width - vbWidth * scale) / 2;
        // Convert client coordinate to SVG viewBox coordinate
        var svgX = (clientX - rect.left - offsetX) / scale;
        // Clamp to chart area
        var chartX = svgX - padL;
        if (chartX < 0) chartX = 0;
        if (chartX > chartW) chartX = chartW;
        // Find nearest data point index
        var idx = step > 0 ? Math.round(chartX / step) : 0;
        if (idx < 0) idx = 0;
        if (idx >= count) idx = count - 1;
        return idx;
    }

    function showTooltip(idx) {
        if (idx < 0 || idx >= count) return;

        var pt = pointData[idx];
        var svgRect = svgEl.getBoundingClientRect();
        var chartRect = chart.getBoundingClientRect();

        // Calculate position in SVG viewBox coordinates
        var ptX = padL + idx * step;
        var ptY = padT + chartH - (pt.s / yMax * chartH);

        // Convert viewBox coords to pixel coords relative to chart container.
        // The SVG uses preserveAspectRatio="xMidYMid meet", so on narrow/tall
        // containers the rendered content is centered with letterboxing.
        // We must account for the actual scale and offset.
        var scale = Math.min(svgRect.width / vbWidth, svgRect.height / vbHeight);
        var renderedW = vbWidth * scale;
        var renderedH = vbHeight * scale;
        var offsetX = (svgRect.width - renderedW) / 2;
        var offsetY = (svgRect.height - renderedH) / 2;
        var pixelX = ptX * scale + offsetX + (svgRect.left - chartRect.left);
        var pixelY = ptY * scale + offsetY + (svgRect.top - chartRect.top);

        // Update crosshair
        crosshair.style.left = pixelX + 'px';
        crosshair.classList.add('visible');

        // Update active dot
        activeDot.style.left = pixelX + 'px';
        activeDot.style.top = pixelY + 'px';
        activeDot.classList.add('visible');

        // Update tooltip content
        var dateLabel = formatGranularityLabel(pt.d, granularity);
        var sizeLabel = formatBytes(pt.s);
        tooltip.querySelector('.tt-date').textContent = dateLabel;
        tooltip.querySelector('.tt-size').textContent = sizeLabel;
        tooltip.querySelector('.tt-files').textContent = pt.c + ' ' + T('trendFiles', 'media files');

        // Position tooltip — prefer right side, flip to left if near edge
        var ttWidth = tooltip.offsetWidth || 120;
        var ttHeight = tooltip.offsetHeight || 50;
        var ttLeft = pixelX + 12;
        if (ttLeft + ttWidth > chartRect.width) {
            ttLeft = pixelX - ttWidth - 12;
        }
        var ttTop = pixelY - ttHeight / 2;
        if (ttTop < 0) ttTop = 4;
        if (ttTop + ttHeight > chartRect.height) ttTop = chartRect.height - ttHeight - 4;

        tooltip.style.left = ttLeft + 'px';
        tooltip.style.top = ttTop + 'px';
        tooltip.classList.add('visible');
    }

    // Diff panel elements (lives outside .trend-chart, inside container)
    var diffPanel = container.querySelector('.trend-diff-panel');
    var diffDates = diffPanel ? diffPanel.querySelector('.trend-diff-dates') : null;
    var diffThenSize = diffPanel ? diffPanel.querySelector('.trend-diff-then-size') : null;
    var diffThenCount = diffPanel ? diffPanel.querySelector('.trend-diff-then-count') : null;
    var diffNowDate = diffPanel ? diffPanel.querySelector('.trend-diff-now-date') : null;
    var diffNowSize = diffPanel ? diffPanel.querySelector('.trend-diff-now-size') : null;
    var diffNowCount = diffPanel ? diffPanel.querySelector('.trend-diff-now-count') : null;
    var diffSize = diffPanel ? diffPanel.querySelector('.trend-diff-size') : null;
    var diffFiles = diffPanel ? diffPanel.querySelector('.trend-diff-files') : null;
    var currentPt = pointData[count - 1];

    function updateDiffPanel(idx) {
        if (!diffPanel || !diffDates || !diffSize || !diffFiles) return;

        var pt = pointData[idx];
        var hoveredLabel = formatGranularityLabel(pt.d, granularity);
        var currentLabel = formatGranularityLabel(currentPt.d, granularity);

        // "Then" column (hovered point)
        diffDates.textContent = hoveredLabel;
        if (diffThenSize) diffThenSize.textContent = formatBytes(pt.s);
        if (diffThenCount) diffThenCount.textContent = pt.c + ' ' + T('trendFiles', 'media files');

        // "Now" column (latest point)
        if (diffNowDate) diffNowDate.textContent = currentLabel + ' (' + T('trendNow', 'now') + ')';
        if (diffNowSize) diffNowSize.textContent = formatBytes(currentPt.s);
        if (diffNowCount) diffNowCount.textContent = currentPt.c + ' ' + T('trendFiles', 'media files');

        // Delta row with percentage
        var deltaSize = currentPt.s - pt.s;
        var deltaFiles = currentPt.c - pt.c;
        // Only compute percentage when old value is meaningful (>= 1 MB).
        // Below that threshold the old value is essentially zero and the
        // percentage becomes astronomically large / meaningless.
        var MIN_SIZE_FOR_PCT = 1048576; // 1 MB
        var pctSize = pt.s >= MIN_SIZE_FOR_PCT ? Math.round((deltaSize / pt.s) * 100) : 0;

        var sSign = deltaSize > 0 ? '+' : (deltaSize < 0 ? '' : '\u00B1');
        var pctLabel = pctSize !== 0 ? ' (' + (pctSize > 0 ? '+' : '') + pctSize + '%)' : '';
        diffSize.textContent = sSign + formatBytes(deltaSize) + pctLabel;
        diffSize.className = 'trend-diff-stat trend-diff-size '
            + (deltaSize > 0 ? 'diff-up' : (deltaSize < 0 ? 'diff-down' : 'diff-neutral'));

        var fSign = deltaFiles > 0 ? '+' : (deltaFiles < 0 ? '' : '\u00B1');
        diffFiles.textContent = fSign + deltaFiles + ' ' + T('trendFiles', 'media files');
        diffFiles.className = 'trend-diff-stat trend-diff-files '
            + (deltaFiles > 0 ? 'diff-up' : (deltaFiles < 0 ? 'diff-down' : 'diff-neutral'));

        diffPanel.classList.add('visible');
    }

    function hideDiffPanel() {
        if (diffPanel) diffPanel.classList.remove('visible');
    }

    function hideTooltip() {
        tooltip.classList.remove('visible');
        crosshair.classList.remove('visible');
        activeDot.classList.remove('visible');
        hideDiffPanel();
    }

    // Mouse events
    svgEl.addEventListener('mousemove', function (e) {
        var idx = getPointIndex(e.clientX);
        showTooltip(idx);
        updateDiffPanel(idx);
    });

    svgEl.addEventListener('mouseleave', function () {
        hideTooltip();
    });

    // Touch events — show tooltip on tap/drag, hide on release
    var hideTimeoutId = null;

    svgEl.addEventListener('touchstart', function (e) {
        if (hideTimeoutId) {
            clearTimeout(hideTimeoutId);
            hideTimeoutId = null;
        }
        if (e.touches.length === 1) {
            var idx = getPointIndex(e.touches[0].clientX);
            showTooltip(idx);
            updateDiffPanel(idx);
        }
    }, {passive: true});

    svgEl.addEventListener('touchmove', function (e) {
        if (e.touches.length === 1) {
            var idx = getPointIndex(e.touches[0].clientX);
            showTooltip(idx);
            updateDiffPanel(idx);
        }
    }, {passive: true});

    svgEl.addEventListener('touchend', function () {
        // Keep tooltip visible briefly after touch ends, then hide
        if (hideTimeoutId) {
            clearTimeout(hideTimeoutId);
        }
        hideTimeoutId = setTimeout(function () {
            hideTooltip();
            hideTimeoutId = null;
        }, 1500);
    }, {passive: true});

    svgEl.addEventListener('touchcancel', function () {
        if (hideTimeoutId) {
            clearTimeout(hideTimeoutId);
        }
        hideTooltip();
        hideTimeoutId = null;
    }, {passive: true});
}

// --- Library Insights (Largest & Recently Added/Changed) ---

var _insightsLoadSeq = 0;

/**
 * Fetches library insights from the API and renders the two insight cards.
 */
function loadInsightsData() {
    var seq = ++_insightsLoadSeq;
    var apiClient = ApiClient;
    var url = apiClient.getUrl('JellyfinHelper/LibraryInsights');

    apiClient.ajax({type: 'GET', url: url, dataType: 'json'}).then(function (data) {
        if (seq !== _insightsLoadSeq) return;
        renderInsightCards(data);
    }, function () {
        if (seq !== _insightsLoadSeq) return;
        var c = document.getElementById('insightsContainer');
        if (c) c.innerHTML = '<div class="trend-empty">' + T('insightsError', 'Could not load insights.') + '</div>';
    });
}

/**
 * Renders the two insight summary cards (Largest / Recently) plus their expandable trees.
 */
function renderInsightCards(data) {
    var container = document.getElementById('insightsContainer');
    if (!container) return;

    var html = '<div class="insights-cards">';

    // --- Largest card ---
    html += '<button class="insight-card" id="insightLargestBtn" type="button" aria-expanded="false">';
    html += '<span class="insight-icon">💾</span>';
    html += '<span class="insight-value">' + formatBytes(data.LargestTotalSize) + '</span>';
    html += '<span class="insight-label">' + T('insightLargest', 'Largest') + '</span>';
    html += '</button>';

    // --- Recently card ---
    html += '<button class="insight-card" id="insightRecentBtn" type="button" aria-expanded="false">';
    html += '<span class="insight-icon">🕐</span>';
    html += '<span class="insight-value">' + data.RecentTotalCount + '</span>';
    html += '<span class="insight-label">' + T('insightRecent', 'Recently') + '</span>';
    html += '</button>';

    html += '</div>';

    // --- Expandable panels ---
    html += '<div class="insight-panel" id="insightLargestPanel"></div>';
    html += '<div class="insight-panel" id="insightRecentPanel"></div>';

    container.innerHTML = html;

    // Pre-render hidden tree content
    document.getElementById('insightLargestPanel').innerHTML = buildLargestTree(data);
    document.getElementById('insightRecentPanel').innerHTML = buildRecentTree(data);

    // Toggle handlers
    var largestBtn = document.getElementById('insightLargestBtn');
    var recentBtn = document.getElementById('insightRecentBtn');
    largestBtn.addEventListener('click', function () {
        toggleInsightPanel('insightLargestPanel', 'insightRecentPanel', largestBtn, recentBtn);
    });
    recentBtn.addEventListener('click', function () {
        toggleInsightPanel('insightRecentPanel', 'insightLargestPanel', recentBtn, largestBtn);
    });
}

function toggleInsightPanel(showId, hideId, activeBtn, otherBtn) {
    var show = document.getElementById(showId);
    var hide = document.getElementById(hideId);
    if (hide) hide.classList.remove('visible');
    if (otherBtn) otherBtn.setAttribute('aria-expanded', 'false');
    if (show) {
        show.classList.toggle('visible');
        var expanded = show.classList.contains('visible');
        if (activeBtn) activeBtn.setAttribute('aria-expanded', String(expanded));
    }
}

/**
 * Builds the tree HTML for the "Largest" insight panel.
 * Groups entries by library name, showing library total size.
 */
function buildLargestTree(data) {
    if (!data.Largest || data.Largest.length === 0) {
        return '<div class="trend-empty">' + T('insightNoData', 'No data available.') + '</div>';
    }

    var grouped = groupByLibrary(data.Largest);
    var html = '<div class="insight-tree">';

    Object.keys(grouped).forEach(function (lib) {
        var items = grouped[lib];
        var libSize = data.LibrarySizes && data.LibrarySizes[lib] ? data.LibrarySizes[lib] : 0;

        html += '<div class="insight-tree-lib">';
        html += '<div class="insight-tree-lib-header">';
        html += '<span class="insight-tree-lib-name">' + escHtml(lib) + '</span>';
        html += '<span class="insight-tree-lib-size">' + formatBytes(libSize) + '</span>';
        html += '</div>';

        for (var i = 0; i < items.length; i++) {
            var e = items[i];
            var badge = getInsightTypeBadge(e.CollectionType);
            html += '<div class="insight-tree-item">';
            html += '<span class="insight-tree-name">' + badge + ' ' + escHtml(e.Name) + '</span>';
            html += '<span class="insight-tree-size">' + formatBytes(e.Size) + '</span>';
            html += '</div>';
        }

        html += '</div>';
    });

    html += '</div>';
    return html;
}

/**
 * Builds the tree HTML for the "Recently" insight panel.
 * Groups entries by library, shows added vs changed badge + date.
 */
function buildRecentTree(data) {
    if (!data.Recent || data.Recent.length === 0) {
        return '<div class="trend-empty">' + T('insightNoRecent', 'No recent changes found.') + '</div>';
    }

    var grouped = groupByLibrary(data.Recent);
    var html = '<div class="insight-tree">';

    Object.keys(grouped).forEach(function (lib) {
        var items = grouped[lib];
        var libSize = data.LibrarySizes && data.LibrarySizes[lib] ? data.LibrarySizes[lib] : 0;

        html += '<div class="insight-tree-lib">';
        html += '<div class="insight-tree-lib-header">';
        html += '<span class="insight-tree-lib-name">' + escHtml(lib) + '</span>';
        html += '<span class="insight-tree-lib-size">' + formatBytes(libSize) + '</span>';
        html += '</div>';

        for (var i = 0; i < items.length; i++) {
            var e = items[i];
            var changeBadge = e.ChangeType === 'added'
                ? '<span class="insight-badge insight-badge-added">' + T('insightAdded', 'added') + '</span>'
                : '<span class="insight-badge insight-badge-changed">' + T('insightChanged', 'changed') + '</span>';
            var dateStr = e.ChangeType === 'changed'
                ? formatInsightDate(e.ModifiedUtc)
                : formatInsightDate(e.CreatedUtc);

            html += '<div class="insight-tree-item">';
            html += '<span class="insight-tree-name">' + changeBadge + ' ' + escHtml(e.Name) + '</span>';
            html += '<span class="insight-tree-meta">' + formatBytes(e.Size) + ' · ' + dateStr + '</span>';
            html += '</div>';
        }

        html += '</div>';
    });

    html += '</div>';
    return html;
}

function groupByLibrary(entries) {
    var map = {};
    for (var i = 0; i < entries.length; i++) {
        var lib = entries[i].LibraryName || 'Unknown';
        if (!map[lib]) map[lib] = [];
        map[lib].push(entries[i]);
    }
    return map;
}

function getInsightTypeBadge(collectionType) {
    if (!collectionType) return '📁';
    var ct = collectionType.toLowerCase();
    if (ct === 'movies' || ct === 'homevideos' || ct === 'musicvideos') return '🎬';
    if (ct === 'tvshows') return '📺';
    return '📁';
}

function formatInsightDate(isoStr) {
    if (!isoStr) return '—';
    var d = new Date(isoStr);
    if (isNaN(d.getTime())) return '—';
    return d.toLocaleDateString(undefined, {month: 'short', day: 'numeric', year: 'numeric'});
}

var _trendLoadRequestSeq = 0;

function loadTrendData(forceRefresh) {
    var requestSeq = ++_trendLoadRequestSeq;
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
        if (requestSeq !== _trendLoadRequestSeq) return;
        var container = document.getElementById('trendChartContainer');
        if (container) {
            container.innerHTML = renderTrendChart(timeline);
            attachTrendInteraction(container);
        }
    }, function () {
        if (requestSeq !== _trendLoadRequestSeq) return;
        var container = document.getElementById('trendChartContainer');
        if (container) {
            container.innerHTML = '<div class="trend-empty">' + T('trendError', 'Could not load trend data.') + '</div>';
        }
    });
}
