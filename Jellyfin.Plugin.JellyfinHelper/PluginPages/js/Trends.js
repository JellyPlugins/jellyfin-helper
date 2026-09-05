'use strict';


function formatGranularityLabel(dateStr, granularity) {
    var d = new Date(dateStr);
    if (Number.isNaN(d.getTime())) return '-';

    switch (granularity) {
        case 'yearly':
            return d.getUTCFullYear().toString();
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

/** * Interpolates missing intermediate buckets between sparse data points. * When the backend deduplicates consecutive identical points, gaps appear * in the timeline. */
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
    var d = new Date(date);
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
        case 'yearly':
            d.setUTCFullYear(d.getUTCFullYear() + 1);
            break;
        default:
            d.setUTCMonth(d.getUTCMonth() + 1);
    }
    return d;
}

var TREND_DAY_MS = 24 * 60 * 60 * 1000;

/**
 * Snaps a date to the start of its bucket for the given level (UTC).
 * Mirrors the backend TimelineAggregator.GetBucketStart.
 */
function bucketStartDate(date, level) {
    var d = new Date(date);
    switch (level) {
        case 'weekly': {
            // ISO week start (Monday).
            var day = (d.getUTCDay() + 6) % 7;
            return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() - day));
        }
        case 'monthly':
            return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), 1));
        case 'yearly':
            return new Date(Date.UTC(d.getUTCFullYear(), 0, 1));
        case 'daily':
        default:
            return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()));
    }
}

/**
 * Projects a dense daily series onto a coarser display level by keeping the last
 * (highest cumulative) point per bucket. Mirrors TimelineAggregator.ConsolidateToGranularity.
 * Input points are objects {date, cumulativeSize, cumulativeFileCount} sorted ascending.
 */
function projectToGranularity(dailyPoints, level) {
    if (level === 'daily' || dailyPoints.length <= 1) return dailyPoints;

    var buckets = new Map();
    for (var i = 0; i < dailyPoints.length; i++) {
        var p = dailyPoints[i];
        var key = bucketStartDate(p.date, level).getTime();
        // Last point per bucket wins (points are sorted chronologically).
        buckets.set(key, {
            date: new Date(key).toISOString(),
            cumulativeSize: p.cumulativeSize,
            cumulativeFileCount: p.cumulativeFileCount
        });
    }
    return Array.from(buckets.values()).sort(function (a, b) {
        return new Date(a.date) - new Date(b.date);
    });
}

/**
 * Chooses the display level from the visible span in days. Mirrors the backend
 * DetermineGranularity thresholds (quarterly removed): the tighter the zoom, the finer the level.
 */
function pickLevelForSpan(spanDays) {
    if (spanDays > 5 * 365) return 'yearly';
    if (spanDays > 365) return 'monthly';
    if (spanDays > 90) return 'weekly';
    return 'daily';
}

/**
 * Computes a "nice" 1024-based Y-axis scale for a given peak byte value.
 * Returns { yMax, ticks } where ticks align to clean unit boundaries (e.g. 5 TB, 10 TB).
 * The unit is derived from the peak, so a zoomed-in GB window rescales from TB to GB.
 */
function computeNiceYScale(rawMax) {
    if (rawMax <= 0) rawMax = 1;
    var niceTickCount = 4;
    var binaryUnits = [1, 1024, 1024 * 1024, 1024 * 1024 * 1024, 1024 * 1024 * 1024 * 1024, 1024 * 1024 * 1024 * 1024 * 1024];
    var unitIdx = 0;
    var humanMax = rawMax;
    while (humanMax >= 1024 && unitIdx < binaryUnits.length - 1) {
        humanMax /= 1024;
        unitIdx++;
    }
    var rawIntervalHuman = humanMax / niceTickCount;
    var mag10 = Math.pow(10, Math.floor(Math.log10(rawIntervalHuman > 0 ? rawIntervalHuman : 1)));
    var resid = rawIntervalHuman / mag10;
    var niceIntervalHuman;
    if (resid <= 1) niceIntervalHuman = mag10;
    else if (resid <= 2) niceIntervalHuman = 2 * mag10;
    else if (resid <= 5) niceIntervalHuman = 5 * mag10;
    else niceIntervalHuman = 10 * mag10;

    var yMaxHuman = Math.ceil(humanMax / niceIntervalHuman) * niceIntervalHuman;
    if (yMaxHuman === 0) yMaxHuman = 1;
    var yMax = yMaxHuman * binaryUnits[unitIdx];
    var niceInterval = niceIntervalHuman * binaryUnits[unitIdx];

    var ticks = [];
    for (var t = 0; t <= yMax; t += niceInterval) {
        if (niceInterval <= 0) break;
        ticks.push(Math.round(t));
    }
    if (ticks.at(-1) < Math.round(yMax)) ticks.push(Math.round(yMax));
    return { yMax: yMax, ticks: ticks };
}

// Fixed chart geometry shared by renderer and interaction handler.
var TREND_GEOM = { width: 880, height: 240, padL: 65, padR: 45, padT: 20, padB: 56 };

/**
 * Builds the SVG for the current visible window over the dense daily series.
 * Pure with respect to the DOM: returns the SVG string plus the projected point data
 * and the y-axis max used, so the interaction handler can map coordinates.
 *
 * state: { fullDaily, startTime, endTime }
 */
function drawTrendWindow(state) {
    var g = TREND_GEOM;
    var chartW = g.width - g.padL - g.padR;
    var chartH = g.height - g.padT - g.padB;

    var spanDays = (state.endTime - state.startTime) / TREND_DAY_MS;
    var level = pickLevelForSpan(spanDays);
    var projected = projectToGranularity(state.fullDaily, level);

    // Visible points plus one boundary point on each side so the line reaches the edges.
    var visible = [];
    var firstBefore = null;
    var firstAfter = null;
    for (var i = 0; i < projected.length; i++) {
        var t = new Date(projected[i].date).getTime();
        if (t < state.startTime) {
            firstBefore = projected[i];
        } else if (t > state.endTime) {
            if (firstAfter === null) firstAfter = projected[i];
        } else {
            visible.push(projected[i]);
        }
    }
    var render = [];
    if (firstBefore) render.push(firstBefore);
    render = render.concat(visible);
    if (firstAfter) render.push(firstAfter);
    if (render.length === 0 && projected.length > 0) {
        render.push(projected[projected.length - 1]);
    }

    // Dynamic Y axis from the maximum of the VISIBLE window, so zooming rescales the unit.
    var rawMax = 0;
    for (var r = 0; r < render.length; r++) {
        if (render[r].cumulativeSize > rawMax) rawMax = render[r].cumulativeSize;
    }
    var yScale = computeNiceYScale(rawMax);
    var yMax = yScale.yMax;

    var startTime = state.startTime;
    var endTime = state.endTime;
    var timeSpan = endTime - startTime || 1;
    function xOf(t) {
        return g.padL + (t - startTime) / timeSpan * chartW;
    }
    function yOf(size) {
        return g.padT + chartH - (size / yMax * chartH);
    }

    var pointData = [];
    var points = [];
    for (var j = 0; j < render.length; j++) {
        var pt = render[j];
        var tt = new Date(pt.date).getTime();
        var x = xOf(tt);
        var y = yOf(pt.cumulativeSize);
        points.push(x.toFixed(1) + ',' + y.toFixed(1));
        pointData.push({ d: pt.date, s: pt.cumulativeSize, c: pt.cumulativeFileCount, x: x, y: y, t: tt });
    }

    var svg = '<svg width="100%" viewBox="0 0 ' + g.width + ' ' + g.height + '" preserveAspectRatio="xMidYMid meet">';

    for (var gi = 0; gi < yScale.ticks.length; gi++) {
        var gy = yOf(yScale.ticks[gi]);
        svg += '<line x1="' + g.padL + '" y1="' + gy.toFixed(1) + '" x2="' + (g.width - g.padR) + '" y2="' + gy.toFixed(1) + '" stroke="rgba(255,255,255,0.06)" />';
        svg += '<text x="' + (g.padL - 5) + '" y="' + (gy + 4).toFixed(1) + '" text-anchor="end" fill="rgba(255,255,255,0.4)" font-size="10">' + formatBytes(yScale.ticks[gi]) + '</text>';
    }

    var areaFillRaw = getComputedStyle(document.documentElement).getPropertyValue('--color-primary-light').trim() || 'rgba(0,164,220,0.15)';
    var areaFill = /^[a-zA-Z0-9#(),.\s%]+$/.test(areaFillRaw) ? areaFillRaw : 'rgba(0,164,220,0.15)';
    var trendColorRaw = getComputedStyle(document.documentElement).getPropertyValue('--color-primary').trim() || '#00a4dc';
    var trendColor = /^[a-zA-Z0-9#(),.\s%]+$/.test(trendColorRaw) ? trendColorRaw : '#00a4dc';

    if (points.length > 0) {
        var firstX = points[0].split(',')[0];
        var lastX = points[points.length - 1].split(',')[0];
        var baseY = (g.padT + chartH).toFixed(1);
        var areaPoints = firstX + ',' + baseY + ' ' + points.join(' ') + ' ' + lastX + ',' + baseY;
        svg += '<polygon points="' + areaPoints + '" fill="' + areaFill + '" />';
        svg += '<polyline points="' + points.join(' ') + '" fill="none" stroke="' + trendColor + '" stroke-width="2" />';
    }

    // Invisible interaction overlay for mouse/touch tracking.
    svg += '<rect class="trend-hit-area" x="' + g.padL + '" y="' + g.padT + '" width="' + chartW + '" height="' + chartH + '" fill="transparent" />';

    // Data dots, sized down as the visible point count grows.
    var dotRadius;
    if (pointData.length <= 60) dotRadius = 2.5;
    else if (pointData.length <= 200) dotRadius = 1.5;
    else dotRadius = 0;
    if (dotRadius > 0) {
        for (var k = 0; k < points.length; k++) {
            var coords = points[k].split(',');
            svg += '<circle cx="' + coords[0] + '" cy="' + coords[1] + '" r="' + dotRadius + '" fill="' + trendColor + '" opacity="0.6" />';
        }
    }

    // X-axis labels with a hard minimum pixel gap. No label is ever drawn without the gap
    // check, so two labels can never overlap at any zoom level or window position.
    var minLabelGapPx = 60;
    var lastLabelX = -Infinity;
    for (var m = 0; m < pointData.length; m++) {
        var lx = pointData[m].x;
        if (lx - lastLabelX < minLabelGapPx) continue;
        // Keep labels inside the plot so the last one is never clipped at the right edge.
        if (lx > g.width - g.padR - 4) continue;
        var lbl = formatGranularityLabel(pointData[m].d, level);
        svg += '<text x="' + lx.toFixed(1) + '" y="' + (g.padT + chartH + 18) + '" text-anchor="middle" fill="rgba(255,255,255,0.55)" font-size="10" font-weight="500">' + escHtml(lbl) + '</text>';
        lastLabelX = lx;
    }

    svg += '<line x1="' + g.padL + '" y1="' + (g.padT + chartH) + '" x2="' + (g.width - g.padR) + '" y2="' + (g.padT + chartH) + '" stroke="rgba(255,255,255,0.12)" />';
    svg += '</svg>';

    return { svg: svg, pointData: pointData, yMax: yMax, level: level };
}

function renderTrendChart(timeline) {
    if (!timeline || !timeline.dataPoints || timeline.dataPoints.length < 2) {
        return { html: '<div class="trend-empty">' + T('trendEmpty', 'Not enough data yet. Growth timeline is computed during each scheduled scan.') + '</div>', chartState: null };
    }

    // Storage is daily and lossless. Interpolate the deduped gaps back to a dense daily array
    // once; all zoom levels are projected from this on the fly.
    var validGranularities = ['daily', 'weekly', 'monthly', 'yearly'];
    var rawGranularity = timeline.granularity || 'daily';
    var storedLevel = String(rawGranularity).toLowerCase();
    if (!validGranularities.includes(storedLevel)) storedLevel = 'daily';
    var fullDaily = interpolateDataPoints(timeline.dataPoints, 'daily');

    // Skip a long flat near-zero baseline before real growth starts, keeping one zero point.
    var peakSize = 0;
    for (var p = 0; p < fullDaily.length; p++) {
        if (fullDaily[p].cumulativeSize > peakSize) peakSize = fullDaily[p].cumulativeSize;
    }
    var zeroThreshold = peakSize * 0.005;
    var firstSignificant = -1;
    for (var z = 0; z < fullDaily.length; z++) {
        if (fullDaily[z].cumulativeSize > zeroThreshold) { firstSignificant = z; break; }
    }
    if (firstSignificant < 0) firstSignificant = fullDaily.length - 1;
    var startIndex = Math.max(0, firstSignificant - 1);
    if (startIndex > 0) fullDaily = fullDaily.slice(startIndex);

    if (fullDaily.length < 2) {
        return { html: '<div class="trend-empty">' + T('trendEmpty', 'Not enough data yet. Growth timeline is computed during each scheduled scan.') + '</div>', chartState: null };
    }

    var minTime = new Date(fullDaily[0].date).getTime();
    var maxTime = new Date(fullDaily[fullDaily.length - 1].date).getTime();

    // Initial window = full domain, so the opening view auto-picks the same level the old
    // age-based logic would have shown (day/week/month/year by total span).
    var chartState = {
        fullDaily: fullDaily,
        minTime: minTime,
        maxTime: maxTime,
        startTime: minTime,
        endTime: maxTime
    };

    var drawn = drawTrendWindow(chartState);

    var overlays = '<div class="trend-crosshair"></div>';
    overlays += '<div class="trend-active-dot"></div>';
    overlays += '<div class="trend-tooltip"><div class="tt-date"></div><div class="tt-size"></div><div class="tt-files"></div></div>';

    var safeFileCount = Number(timeline.totalDirectoriesScanned);
    if (!Number.isFinite(safeFileCount) || safeFileCount < 0) safeFileCount = 0;
    var meta = '<div class="trend-meta" style="text-align:center;color:rgba(255,255,255,0.35);font-size:11px;margin-top:4px;">';
    meta += escHtml(T('trendGranularity', 'Granularity')) + ': <span class="trend-meta-level">' + escHtml(drawn.level) + '</span>';
    meta += ' &middot; ' + safeFileCount + ' ' + escHtml(T('trendFiles', 'media files'));
    if (timeline.earliestFileDate) {
        meta += ' &middot; ' + escHtml(T('trendEarliest', 'Earliest')) + ': ' + new Date(timeline.earliestFileDate).toLocaleDateString(undefined, {timeZone: 'UTC'});
    }
    meta += '</div>';

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

    var html = '<div class="trend-chart">' + drawn.svg + overlays + '</div>' + diffPanel + meta;
    return { html: html, chartState: chartState };
}

/**
 * Attaches interactive tooltip/crosshair behavior to the trend chart.
 * Called after renderTrendChart HTML is inserted into the DOM.
 */
function attachTrendInteraction(container, chartState) {
    var chart = container.querySelector('.trend-chart');
    if (!chart || !chartState) return;

    var g = TREND_GEOM;
    var chartW = g.width - g.padL - g.padR;
    var chartH = g.height - g.padT - g.padB;
    var vbWidth = g.width;
    var vbHeight = g.height;

    // Per-render state, refreshed by redraw(): the projected points, current level, y-axis max.
    var pointData = [];
    var level = 'daily';
    var yMax = 1;
    // "Now" is always the latest point of the full daily series, so the diff panel compares
    // against the true latest value even when panned into the past.
    var currentPt = (function () {
        var last = chartState.fullDaily[chartState.fullDaily.length - 1];
        return { d: last.date, s: last.cumulativeSize, c: last.cumulativeFileCount };
    })();

    var metaLevelEl = container.querySelector('.trend-meta-level');

    function redraw() {
        var drawn = drawTrendWindow(chartState);
        var svgHost = chart.querySelector('svg');
        if (svgHost) svgHost.outerHTML = drawn.svg;
        pointData = drawn.pointData;
        level = drawn.level;
        yMax = drawn.yMax;
        if (metaLevelEl) metaLevelEl.textContent = level;
        rebindSvg();
    }

    var svgEl = null;

    function nearestByClientX(clientX) {
        var rect = svgEl.getBoundingClientRect();
        var scale = Math.min(rect.width / vbWidth, rect.height / vbHeight);
        var offsetX = (rect.width - vbWidth * scale) / 2;
        var svgX = (clientX - rect.left - offsetX) / scale;
        var chartX = svgX - g.padL;
        if (chartX < 0) chartX = 0;
        if (chartX > chartW) chartX = chartW;
        // Nearest visible projected point by pixel x.
        var best = 0;
        var bestDist = Infinity;
        for (var i = 0; i < pointData.length; i++) {
            var dist = Math.abs((pointData[i].x - g.padL) - chartX);
            if (dist < bestDist) { bestDist = dist; best = i; }
        }
        return best;
    }

    function showTooltip(idx) {
        if (idx < 0 || idx >= pointData.length) return;
        var tooltip = chart.querySelector('.trend-tooltip');
        var crosshair = chart.querySelector('.trend-crosshair');
        var activeDot = chart.querySelector('.trend-active-dot');
        if (!tooltip || !crosshair || !activeDot) return;

        var pt = pointData[idx];
        var svgRect = svgEl.getBoundingClientRect();
        var chartRect = chart.getBoundingClientRect();

        var scale = Math.min(svgRect.width / vbWidth, svgRect.height / vbHeight);
        var renderedW = vbWidth * scale;
        var renderedH = vbHeight * scale;
        var offsetX = (svgRect.width - renderedW) / 2;
        var offsetY = (svgRect.height - renderedH) / 2;
        var pixelX = pt.x * scale + offsetX + (svgRect.left - chartRect.left);
        var pixelY = pt.y * scale + offsetY + (svgRect.top - chartRect.top);

        crosshair.style.left = pixelX + 'px';
        crosshair.classList.add('visible');
        activeDot.style.left = pixelX + 'px';
        activeDot.style.top = pixelY + 'px';
        activeDot.classList.add('visible');

        tooltip.querySelector('.tt-date').textContent = formatGranularityLabel(pt.d, level);
        tooltip.querySelector('.tt-size').textContent = formatBytes(pt.s);
        tooltip.querySelector('.tt-files').textContent = pt.c + ' ' + T('trendFiles', 'media files');

        var ttWidth = tooltip.offsetWidth || 120;
        var ttHeight = tooltip.offsetHeight || 50;
        var ttLeft = pixelX + 12;
        if (ttLeft + ttWidth > chartRect.width) ttLeft = pixelX - ttWidth - 12;
        var ttTop = pixelY - ttHeight / 2;
        if (ttTop < 0) ttTop = 4;
        if (ttTop + ttHeight > chartRect.height) ttTop = chartRect.height - ttHeight - 4;
        tooltip.style.left = ttLeft + 'px';
        tooltip.style.top = ttTop + 'px';
        tooltip.classList.add('visible');
    }

    var diffPanel = container.querySelector('.trend-diff-panel');
    var diffDates = diffPanel ? diffPanel.querySelector('.trend-diff-dates') : null;
    var diffThenSize = diffPanel ? diffPanel.querySelector('.trend-diff-then-size') : null;
    var diffThenCount = diffPanel ? diffPanel.querySelector('.trend-diff-then-count') : null;
    var diffNowDate = diffPanel ? diffPanel.querySelector('.trend-diff-now-date') : null;
    var diffNowSize = diffPanel ? diffPanel.querySelector('.trend-diff-now-size') : null;
    var diffNowCount = diffPanel ? diffPanel.querySelector('.trend-diff-now-count') : null;
    var diffSize = diffPanel ? diffPanel.querySelector('.trend-diff-size') : null;
    var diffFiles = diffPanel ? diffPanel.querySelector('.trend-diff-files') : null;

    function updateDiffPanel(idx) {
        if (!diffPanel || !diffDates || !diffSize || !diffFiles) return;
        if (idx < 0 || idx >= pointData.length) return;

        var pt = pointData[idx];
        var hoveredLabel = formatGranularityLabel(pt.d, level);
        var currentLabel = formatGranularityLabel(currentPt.d, level);

        diffDates.textContent = hoveredLabel;
        if (diffThenSize) diffThenSize.textContent = formatBytes(pt.s);
        if (diffThenCount) diffThenCount.textContent = pt.c + ' ' + T('trendFiles', 'media files');

        if (diffNowDate) diffNowDate.textContent = currentLabel + ' (' + T('trendNow', 'now') + ')';
        if (diffNowSize) diffNowSize.textContent = formatBytes(currentPt.s);
        if (diffNowCount) diffNowCount.textContent = currentPt.c + ' ' + T('trendFiles', 'media files');

        var deltaSize = currentPt.s - pt.s;
        var deltaFiles = currentPt.c - pt.c;
        var pctRaw = currentPt.s > 0 ? (deltaSize / currentPt.s) * 100 : 0;

        var sSign;
        if (deltaSize > 0) sSign = '+';
        else if (deltaSize < 0) sSign = '';
        else sSign = '\u00B1';
        var pctLabel = '';
        if (deltaSize !== 0 && pctRaw !== 0) {
            var pctDisplay = Number.parseFloat(pctRaw.toFixed(2));
            var pctSign = pctDisplay > 0 ? '+' : '';
            pctLabel = ' (' + pctSign + pctDisplay + '%)';
        }
        diffSize.textContent = sSign + formatBytes(deltaSize) + pctLabel;
        var deltaSizeClass;
        if (deltaSize > 0) deltaSizeClass = 'diff-up';
        else if (deltaSize < 0) deltaSizeClass = 'diff-down';
        else deltaSizeClass = 'diff-neutral';
        diffSize.className = 'trend-diff-stat trend-diff-size ' + deltaSizeClass;

        var fSign;
        if (deltaFiles > 0) fSign = '+';
        else if (deltaFiles < 0) fSign = '';
        else fSign = '\u00B1';
        diffFiles.textContent = fSign + deltaFiles + ' ' + T('trendFiles', 'media files');
        var deltaFilesClass;
        if (deltaFiles > 0) deltaFilesClass = 'diff-up';
        else if (deltaFiles < 0) deltaFilesClass = 'diff-down';
        else deltaFilesClass = 'diff-neutral';
        diffFiles.className = 'trend-diff-stat trend-diff-files ' + deltaFilesClass;

        diffPanel.classList.add('visible');
    }

    function hideDiffPanel() {
        if (diffPanel) diffPanel.classList.remove('visible');
    }

    function hideTooltip() {
        var tooltip = chart.querySelector('.trend-tooltip');
        var crosshair = chart.querySelector('.trend-crosshair');
        var activeDot = chart.querySelector('.trend-active-dot');
        if (tooltip) tooltip.classList.remove('visible');
        if (crosshair) crosshair.classList.remove('visible');
        if (activeDot) activeDot.classList.remove('visible');
        hideDiffPanel();
    }

    function onHover(clientX) {
        var idx = nearestByClientX(clientX);
        showTooltip(idx);
        updateDiffPanel(idx);
    }

    // Zoom / pan window model. The window is a [startTime, endTime] range over the full domain;
    // gestures mutate it and redraw. Minimum span is two days so daily zoom cannot invert.
    var MIN_SPAN_MS = 2 * TREND_DAY_MS;
    var domainStart = chartState.minTime;
    var domainEnd = chartState.maxTime;

    function clampWindow() {
        var span = chartState.endTime - chartState.startTime;
        if (span < MIN_SPAN_MS) {
            var mid = (chartState.startTime + chartState.endTime) / 2;
            chartState.startTime = mid - MIN_SPAN_MS / 2;
            chartState.endTime = mid + MIN_SPAN_MS / 2;
            span = MIN_SPAN_MS;
        }
        var fullSpan = domainEnd - domainStart;
        if (span >= fullSpan) {
            chartState.startTime = domainStart;
            chartState.endTime = domainEnd;
            return;
        }
        if (chartState.startTime < domainStart) {
            chartState.endTime += domainStart - chartState.startTime;
            chartState.startTime = domainStart;
        }
        if (chartState.endTime > domainEnd) {
            chartState.startTime -= chartState.endTime - domainEnd;
            chartState.endTime = domainEnd;
        }
    }

    // Maps a client X pixel to a time in the current window, accounting for letterboxing.
    function clientXToTime(clientX) {
        var host = chart.querySelector('svg');
        if (!host) return chartState.startTime;
        var rect = host.getBoundingClientRect();
        var scale = Math.min(rect.width / vbWidth, rect.height / vbHeight);
        var offsetX = (rect.width - vbWidth * scale) / 2;
        var svgX = (clientX - rect.left - offsetX) / scale;
        var frac = (svgX - g.padL) / chartW;
        if (frac < 0) frac = 0;
        if (frac > 1) frac = 1;
        return chartState.startTime + frac * (chartState.endTime - chartState.startTime);
    }

    // Zooms the window about a fixed time anchor so that point stays under the cursor/fingers.
    function zoomAbout(anchorTime, factor) {
        var newStart = anchorTime - (anchorTime - chartState.startTime) * factor;
        var newEnd = anchorTime + (chartState.endTime - anchorTime) * factor;
        chartState.startTime = newStart;
        chartState.endTime = newEnd;
        clampWindow();
        redraw();
    }

    function panByPixels(pixelDelta) {
        // Convert a horizontal pixel delta into a time shift over the current window.
        var host = chart.querySelector('svg');
        if (!host) return;
        var rect = host.getBoundingClientRect();
        var scale = Math.min(rect.width / vbWidth, rect.height / vbHeight) || 1;
        var svgDelta = pixelDelta / scale;
        var timeDelta = -(svgDelta / chartW) * (chartState.endTime - chartState.startTime);
        chartState.startTime += timeDelta;
        chartState.endTime += timeDelta;
        clampWindow();
        redraw();
    }

    // Desktop: wheel zooms toward the cursor.
    chart.addEventListener('wheel', function (e) {
        e.preventDefault();
        var anchor = clientXToTime(e.clientX);
        var factor = e.deltaY < 0 ? 0.85 : 1.18;
        hideTooltip();
        zoomAbout(anchor, factor);
    }, {passive: false});

    // Desktop: drag pans. Movement beyond a small threshold enters pan mode and suppresses hover.
    // Move/up listeners live on window only for the duration of a drag, so they never leak
    // across chart reloads.
    var mouseDown = false;
    var panning = false;
    var lastMouseX = 0;
    var downMouseX = 0;

    function onWindowMouseMove(e) {
        if (!mouseDown) return;
        if (!panning && Math.abs(e.clientX - downMouseX) > 4) {
            panning = true;
            hideTooltip();
        }
        if (panning) {
            panByPixels(e.clientX - lastMouseX);
            lastMouseX = e.clientX;
        }
    }
    function onWindowMouseUp() {
        mouseDown = false;
        panning = false;
        window.removeEventListener('mousemove', onWindowMouseMove);
        window.removeEventListener('mouseup', onWindowMouseUp);
    }
    chart.addEventListener('mousedown', function (e) {
        mouseDown = true;
        panning = false;
        downMouseX = e.clientX;
        lastMouseX = e.clientX;
        window.addEventListener('mousemove', onWindowMouseMove);
        window.addEventListener('mouseup', onWindowMouseUp);
    });

    // Mobile: one-finger swipe pans / tap shows tooltip; two-finger pinch zooms.
    var touchMode = null; // null | 'pan' | 'pinch'
    var touchStartX = 0;
    var touchStartY = 0;
    var touchStartT = 0;
    var lastTouchX = 0;
    var pinchStartDist = 0;

    function touchDistance(touches) {
        var dx = touches[0].clientX - touches[1].clientX;
        var dy = touches[0].clientY - touches[1].clientY;
        return Math.hypot(dx, dy);
    }
    function touchMidX(touches) {
        return (touches[0].clientX + touches[1].clientX) / 2;
    }

    chart.addEventListener('touchstart', function (e) {
        if (e.touches.length === 2) {
            touchMode = 'pinch';
            pinchStartDist = touchDistance(e.touches);
            hideTooltip();
            e.preventDefault();
        } else if (e.touches.length === 1) {
            touchMode = null;
            touchStartX = e.touches[0].clientX;
            touchStartY = e.touches[0].clientY;
            lastTouchX = touchStartX;
            touchStartT = Date.now();
        }
    }, {passive: false});

    chart.addEventListener('touchmove', function (e) {
        if (e.touches.length === 2 && touchMode === 'pinch') {
            e.preventDefault();
            var dist = touchDistance(e.touches);
            if (pinchStartDist > 0 && dist > 0) {
                var anchor = clientXToTime(touchMidX(e.touches));
                var factor = pinchStartDist / dist; // fingers apart -> factor < 1 -> zoom in
                zoomAbout(anchor, factor);
                pinchStartDist = dist;
            }
            return;
        }
        if (e.touches.length === 1) {
            var x = e.touches[0].clientX;
            var y = e.touches[0].clientY;
            if (touchMode === null) {
                // Decide pan vs tap once movement is clearly horizontal.
                if (Math.abs(x - touchStartX) > 8 && Math.abs(x - touchStartX) > Math.abs(y - touchStartY)) {
                    touchMode = 'pan';
                    hideTooltip();
                }
            }
            if (touchMode === 'pan') {
                e.preventDefault();
                panByPixels(x - lastTouchX);
                lastTouchX = x;
            }
        }
    }, {passive: false});

    chart.addEventListener('touchend', function (e) {
        // A short, near-stationary single-finger touch is a tap: show the tooltip at that point.
        if (touchMode === null && e.changedTouches.length === 1) {
            var dt = Date.now() - touchStartT;
            var moved = Math.abs(e.changedTouches[0].clientX - touchStartX);
            if (dt < 500 && moved < 8) {
                onHover(e.changedTouches[0].clientX);
            }
        }
        if (e.touches.length === 0) {
            touchMode = null;
            pinchStartDist = 0;
        }
    });

    chart.addEventListener('touchcancel', function () {
        touchMode = null;
        pinchStartDist = 0;
        hideTooltip();
    });

    // Rebinds hover listeners after each redraw replaces the <svg> element. Pan/zoom listeners
    // live on the stable chart container, so they are attached once, not here.
    function rebindSvg() {
        svgEl = chart.querySelector('svg');
        if (!svgEl) return;

        svgEl.addEventListener('mousemove', function (e) {
            if (mouseDown) return; // dragging pans, not hovers
            onHover(e.clientX);
        });
        svgEl.addEventListener('mouseleave', function () {
            if (!panning) hideTooltip();
        });
    }

    rebindSvg();
}



var _insightsLoadSeq = 0;

/**
 * Fetches library insights from the API and renders the two insight cards.
 */
function loadInsightsData() {
    var seq = ++_insightsLoadSeq;

    var container = document.getElementById('insightsContainer');
    if (container) {
        container.innerHTML = '<div class="trend-empty">' + T('loadingInsights', 'Loading insights…') + '</div>';
    }

    apiGet('JellyfinHelper/LibraryInsights', function (data) {
        if (seq !== _insightsLoadSeq) return;
        renderInsightCards(data);
    }, function (err) {
        if (seq !== _insightsLoadSeq) return;
        _apiDefaultError('GET', 'JellyfinHelper/LibraryInsights')(err);
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

    html += '<button class="insight-card" id="insightLargestBtn" type="button" aria-expanded="false">';
    html += '<span class="insight-icon">' + mi('save') + '</span>';
    html += '<span class="insight-value">' + formatBytes(data.LargestTotalSize) + '</span>';
    html += '<span class="insight-label">' + T('insightLargest', 'Largest') + '</span>';
    html += '</button>';

    html += '<button class="insight-card" id="insightRecentBtn" type="button" aria-expanded="false">';
    html += '<span class="insight-icon">' + mi('schedule') + '</span>';
    html += '<span class="insight-value">' + data.RecentTotalCount + '</span>';
    html += '<span class="insight-label">' + T('insightRecent', 'Recently') + '</span>';
    html += '</button>';

    html += '</div>';

    html += '<div class="insight-panel" id="insightLargestPanel"></div>';
    html += '<div class="insight-panel" id="insightRecentPanel"></div>';

    container.innerHTML = html;

    // Pre-render hidden tree content
    document.getElementById('insightLargestPanel').innerHTML = buildLargestTree(data);
    document.getElementById('insightRecentPanel').innerHTML = buildRecentTree(data);

    // Toggle handlers
    var largestBtn = document.getElementById('insightLargestBtn');
    var recentBtn = document.getElementById('insightRecentBtn');
    if (largestBtn) {
        largestBtn.addEventListener('click', function () {
            toggleInsightPanel('insightLargestPanel', 'insightRecentPanel', largestBtn, recentBtn);
        });
    }
    if (recentBtn) {
        recentBtn.addEventListener('click', function () {
            toggleInsightPanel('insightRecentPanel', 'insightLargestPanel', recentBtn, largestBtn);
        });
    }
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
    // Sort library groups: movies/homevideos/musicvideos first, then tvshows, then others.
    // This matches the "Recently" panel layout where movies appear above series.
    var libKeys = Object.keys(grouped).sort(function (a, b) {
        return insightLibrarySortOrder(a, grouped) - insightLibrarySortOrder(b, grouped);
    });
    var html = '<div class="insight-tree">';

    libKeys.forEach(function (lib) {
        var items = grouped[lib];
        var libSize = 0;
        for (var s = 0; s < items.length; s++) {
            var _sz = Number(items[s].Size);
            if (Number.isFinite(_sz) && _sz > 0) libSize += _sz;
        }

        html += '<div class="insight-tree-lib">';
        html += '<div class="insight-tree-lib-header">';
        html += '<span class="insight-tree-lib-name">' + escHtml(lib) + '</span>';
        html += '<span class="insight-tree-lib-size">' + formatBytes(libSize) + '</span>';
        html += '</div>';

        for (var i = 0; i < items.length; i++) {
            var e = items[i];
            var badge = getInsightTypeBadge(e.CollectionType);
            html += '<span class="insight-tree-badge">' + badge + '</span>';
            html += '<span class="insight-tree-name">' + escHtml(e.Name) + '</span>';
            var _itemSize = Number(e.Size);
            var safeSize = (Number.isFinite(_itemSize) && _itemSize > 0) ? _itemSize : 0;
            html += '<span class="insight-tree-size">' + formatBytes(safeSize) + '</span>';
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

    // Sort library groups: movies/homevideos/musicvideos first, then tvshows, then others.
    var libKeys = Object.keys(grouped).sort(function (a, b) {
        return insightLibrarySortOrder(a, grouped) - insightLibrarySortOrder(b, grouped);
    });
    libKeys.forEach(function (libName) {
        var groupItems = grouped[libName];
        var totalSize = 0;
        for (const groupItem of groupItems) {
            var cur = Number(groupItem.Size);
            if (Number.isFinite(cur) && cur > 0) totalSize += cur;
        }

        html += '<div class="insight-tree-lib">';
        html += '<div class="insight-tree-lib-header">';
        html += '<span class="insight-tree-lib-name">' + escHtml(libName) + '</span>';
        html += '<span class="insight-tree-lib-size">' + formatBytes(totalSize) + '</span>';
        html += '</div>';

        for (const e of groupItems) {
            var changeBadge = e.ChangeType === 'added'
                ? '<span class="insight-badge insight-badge-added">' + T('insightAdded', 'added') + '</span>'
                : '<span class="insight-badge insight-badge-changed">' + T('insightChanged', 'changed') + '</span>';
            var dateStr = e.ChangeType === 'changed'
                ? formatInsightDate(e.ModifiedUtc)
                : formatInsightDate(e.CreatedUtc);

            html += changeBadge;
            html += '<span class="insight-tree-name">' + escHtml(e.Name) + '</span>';
            var _itemSize2 = Number(e.Size);
            var safeSize = (Number.isFinite(_itemSize2) && _itemSize2 > 0) ? _itemSize2 : 0;
            html += '<span class="insight-tree-meta">' + formatBytes(safeSize) + ' · ' + dateStr + '</span>';
        }

        html += '</div>';
    });

    html += '</div>';
    return html;
}

/**
 * Returns a sort order for a library name based on its collection type.
 * Movies/homevideos/musicvideos first (0), tvshows second (1), others last (2).
 * Defined once to avoid re-creating the function on every .sort() comparison.
 */
function insightLibrarySortOrder(libName, grouped) {
    var items = grouped[libName];
    if (!items || items.length === 0) return 2;
    // Scan until non-empty CollectionType
    var ct = '';
    for (var i = 0; i < items.length; i++) {
        if (items[i].CollectionType) { ct = items[i].CollectionType.toLowerCase(); break; }
    }
    if (ct === 'movies' || ct === 'homevideos' || ct === 'musicvideos') return 0;
    if (ct === 'tvshows') return 1;
    return 2;
}

function groupByLibrary(entries) {
    var map = Object.create(null);
    for (var i = 0; i < entries.length; i++) {
        var lib = entries[i].LibraryName || 'Unknown';
        if (!map[lib]) map[lib] = [];
        map[lib].push(entries[i]);
    }
    return map;
}

function getInsightTypeBadge(collectionType) {
    if (!collectionType) return mi('folder');
    var ct = collectionType.toLowerCase();
    if (ct === 'movies' || ct === 'homevideos' || ct === 'musicvideos') return mi('movie');
    if (ct === 'tvshows') return mi('tv');
    if (ct === 'music') return mi('music_note');
    return mi('folder');
}

function formatInsightDate(isoStr) {
    if (!isoStr) return '-';
    var d = new Date(isoStr);
    if (Number.isNaN(d.getTime())) return '-';
    return d.toLocaleDateString(undefined, {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
        timeZone: 'UTC'
    });
}

var _trendLoadRequestSeq = 0;

function loadTrendData(forceRefresh) {
    var requestSeq = ++_trendLoadRequestSeq;
    var path = 'JellyfinHelper/GrowthTimeline' + (forceRefresh ? '?forceRefresh=true' : '');

    apiGet(path, function (timeline) {
        if (requestSeq !== _trendLoadRequestSeq) return;
        var container = document.getElementById('trendChartContainer');
        if (container) {
            var result = renderTrendChart(timeline);
            container.innerHTML = result.html;
            attachTrendInteraction(container, result.chartState);
        }
    }, function (err) {
        if (requestSeq !== _trendLoadRequestSeq) return;
        _apiDefaultError('GET', path)(err);
        var container = document.getElementById('trendChartContainer');
        if (container) {
            container.innerHTML = '<div class="trend-empty">' + T('trendError', 'Could not load trend data.') + '</div>';
        }
    });
}
