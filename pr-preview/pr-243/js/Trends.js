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

/**
 * Interpolates missing intermediate buckets between sparse data points.
 * When the backend deduplicates consecutive identical points, gaps appear
 * in the timeline.
 */
function interpolateDataPoints(dataPoints, granularity) {
    if (dataPoints.length < 2) return dataPoints;

    // Matches the backup-side MaxTimelineDataPoints cap so a full dense daily series is not
    // silently halved when rendered.
    var maxPoints = 20000;
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
        result[result.length - 1] = dataPoints.at(-1);
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
var TREND_HOUR_MS = 60 * 60 * 1000;

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
    for (const p of dailyPoints) {
        var key = bucketStartDate(p.date, level).getTime();
        // Last point per bucket wins (points are sorted chronologically).
        buckets.set(key, {
            date: new Date(key).toISOString(),
            _t: key,
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

function clampTrendX(x, g) {
    var left = g.padL;
    var right = g.width - g.padR;
    if (x < left) return left;
    if (x > right) return right;
    return x;
}

function collectVisiblePoints(projected, startTime, endTime) {
    var visible = [];
    var firstBefore = null;
    var firstAfter = null;
    for (const pt of projected) {
        // Cache the epoch on the point the first time it is seen. The projection arrays are
        // memoized per level and reused across pan/pinch frames, so this reparses each date
        // string once instead of on every animation frame over a series up to 20000 points.
        if (pt._t === undefined) {
            pt._t = new Date(pt.date).getTime();
        }
        var t = pt._t;
        if (t < startTime) {
            firstBefore = pt;
        } else if (t > endTime) {
            if (firstAfter === null) firstAfter = pt;
        } else {
            visible.push(pt);
        }
    }
    var render = [];
    if (firstBefore) render.push(firstBefore);
    for (const v of visible) render.push(v);
    if (firstAfter) render.push(firstAfter);
    if (render.length === 0 && projected.length > 0) {
        render.push(projected.at(-1));
    }
    return render;
}

function buildTrendSvgPoints(render, startTime, endTime, g, yMax, chartW, chartH) {
    var timeSpan = endTime - startTime || 1;
    function xOf(t) {
        return g.padL + (t - startTime) / timeSpan * chartW;
    }
    function yOf(size) {
        return g.padT + chartH - (size / yMax * chartH);
    }
    var pointData = [];
    var points = [];
    for (const pt of render) {
        // collectVisiblePoints already cached the epoch on each rendered point; reuse it instead
        // of reparsing pt.date every frame over a window that can hold thousands of points.
        var tt = pt._t !== undefined ? pt._t : new Date(pt.date).getTime();
        var x = clampTrendX(xOf(tt), g);
        var y = yOf(pt.cumulativeSize);
        points.push(x.toFixed(1) + ',' + y.toFixed(1));
        pointData.push({ d: pt.date, s: pt.cumulativeSize, c: pt.cumulativeFileCount, x: x, y: y, t: tt });
    }
    return { pointData: pointData, points: points, xOf: xOf, yOf: yOf };
}

/**
 * Chooses X-axis labels for the visible window, enforcing a hard minimum pixel gap so two
 * labels can never overlap at any zoom level or window position. Edge labels use start/end
 * anchoring so the first/last are never clipped (previous bug showed "016" instead of "2016").
 * Returns an array of { x, anchor, text }.
 */
function computeTrendLabels(pointData, level, g) {
    var minLabelGapPx = 60;
    var lastLabelX = -Infinity;
    var labels = [];
    for (var idx = 0; idx < pointData.length; idx++) {
        var pt = pointData[idx];
        var lx = pt.x;
        if (lx - lastLabelX < minLabelGapPx) continue;
        var isFirst = idx === 0;
        var isLast = idx === pointData.length - 1;
        var lxDisplay = lx;
        var anchor = 'middle';
        if (isFirst && lx <= g.padL + 2) {
            anchor = 'start';
            lxDisplay = g.padL + 2;
        } else if (isLast && lx >= g.width - g.padR - 2) {
            anchor = 'end';
            lxDisplay = g.width - g.padR - 2;
        } else {
            if (lx > g.width - g.padR - 4) continue;
            if (lx < g.padL + 4) continue;
        }
        labels.push({ x: lxDisplay, anchor: anchor, text: formatGranularityLabel(pt.d, level) });
        lastLabelX = lx;
    }
    return labels;
}

/**
 * Computes a full render frame for the current visible window over the dense daily series.
 * Pure with respect to the DOM (no getComputedStyle, no node access): returns the projected
 * point data, polyline/polygon coordinates, y-axis ticks, and x-axis labels. Both the initial
 * string render and the in-place per-frame updater consume this, so gesture redraws never
 * reparse SVG.
 *
 * state: { fullDaily, startTime, endTime, projectionCache }
 */
function computeTrendFrame(state) {
    var g = TREND_GEOM;
    var chartW = g.width - g.padL - g.padR;
    var chartH = g.height - g.padT - g.padB;

    var spanDays = (state.endTime - state.startTime) / TREND_DAY_MS;
    var level = pickLevelForSpan(spanDays);
    if (!state.projectionCache) state.projectionCache = Object.create(null);
    var projected = state.projectionCache[level];
    if (!projected) {
        projected = projectToGranularity(state.fullDaily, level);
        state.projectionCache[level] = projected;
    }

    var render = collectVisiblePoints(projected, state.startTime, state.endTime);

    // Dynamic Y axis from the maximum of the VISIBLE window, so zooming rescales the unit.
    var rawMax = 0;
    for (const pt of render) {
        if (pt.cumulativeSize > rawMax) rawMax = pt.cumulativeSize;
    }
    var yScale = computeNiceYScale(rawMax);
    var yMax = yScale.yMax;

    var built = buildTrendSvgPoints(render, state.startTime, state.endTime, g, yMax, chartW, chartH);
    var pointData = built.pointData;
    var points = built.points;
    var yOf = built.yOf;

    var baseY = g.padT + chartH;
    var areaPoints = '';
    if (points.length > 0) {
        var firstX = points[0].split(',')[0];
        var lastX = points.at(-1).split(',')[0];
        areaPoints = firstX + ',' + baseY.toFixed(1) + ' ' + points.join(' ') + ' ' + lastX + ',' + baseY.toFixed(1);
    }

    // Data dots, sized down as the visible point count grows.
    var dotRadius;
    if (pointData.length <= 60) dotRadius = 2.5;
    else if (pointData.length <= 200) dotRadius = 1.5;
    else dotRadius = 0;

    var ticks = [];
    for (const tick of yScale.ticks) {
        ticks.push({ y: yOf(tick), label: formatBytes(tick) });
    }

    return {
        pointData: pointData,
        points: points,
        polyline: points.join(' '),
        areaPoints: areaPoints,
        dotRadius: dotRadius,
        ticks: ticks,
        labels: computeTrendLabels(pointData, level, g),
        yMax: yMax,
        level: level
    };
}

/**
 * Reads and caches the theme colors used by the chart once, so per-frame redraws never
 * trigger a forced style recalc via getComputedStyle.
 */
function readTrendColors() {
    var safe = /^[a-zA-Z0-9#(),.\s%]+$/;
    var areaFillRaw = getComputedStyle(document.documentElement).getPropertyValue('--color-primary-light').trim() || 'rgba(0,164,220,0.15)';
    var trendColorRaw = getComputedStyle(document.documentElement).getPropertyValue('--color-primary').trim() || '#00a4dc';
    return {
        areaFill: safe.test(areaFillRaw) ? areaFillRaw : 'rgba(0,164,220,0.15)',
        trendColor: safe.test(trendColorRaw) ? trendColorRaw : '#00a4dc'
    };
}

/**
 * Builds the initial SVG string for a frame. Used once when the chart HTML is first inserted;
 * subsequent gesture redraws mutate this DOM in place via applyTrendFrame.
 */
function renderTrendSvgString(frame, colors) {
    var g = TREND_GEOM;
    var chartW = g.width - g.padL - g.padR;
    var chartH = g.height - g.padT - g.padB;
    var baseY = g.padT + chartH;

    var svg = '<svg width="100%" viewBox="0 0 ' + g.width + ' ' + g.height + '" preserveAspectRatio="xMidYMid meet"'
        + ' role="img" aria-label="' + escHtml(T('trendChartLabel', 'Cumulative media library growth over time')) + '">';

    // Grid group: one line + one text per tick. Rebuilt in place on unit rescale.
    svg += '<g class="trend-grid">';
    for (const tick of frame.ticks) {
        svg += '<line x1="' + g.padL + '" y1="' + tick.y.toFixed(1) + '" x2="' + (g.width - g.padR) + '" y2="' + tick.y.toFixed(1) + '" stroke="rgba(255,255,255,0.06)" />';
        svg += '<text x="' + (g.padL - 5) + '" y="' + (tick.y + 4).toFixed(1) + '" text-anchor="end" fill="rgba(255,255,255,0.4)" font-size="10">' + escHtml(tick.label) + '</text>';
    }
    svg += '</g>';

    svg += '<polygon class="trend-area" points="' + frame.areaPoints + '" fill="' + colors.areaFill + '" />';
    svg += '<polyline class="trend-line" points="' + frame.polyline + '" fill="none" stroke="' + colors.trendColor + '" stroke-width="2" />';

    // Invisible interaction overlay for mouse/touch tracking.
    svg += '<rect class="trend-hit-area" x="' + g.padL + '" y="' + g.padT + '" width="' + chartW + '" height="' + chartH + '" fill="transparent" />';

    svg += '<g class="trend-dots">';
    if (frame.dotRadius > 0) {
        for (const p of frame.points) {
            var coords = p.split(',');
            svg += '<circle cx="' + coords[0] + '" cy="' + coords[1] + '" r="' + frame.dotRadius + '" fill="' + colors.trendColor + '" opacity="0.6" />';
        }
    }
    svg += '</g>';

    svg += '<g class="trend-xlabels">';
    for (const lbl of frame.labels) {
        svg += '<text x="' + lbl.x.toFixed(1) + '" y="' + (baseY + 18) + '" text-anchor="' + lbl.anchor + '" fill="rgba(255,255,255,0.55)" font-size="10" font-weight="500">' + escHtml(lbl.text) + '</text>';
    }
    svg += '</g>';

    svg += '<line x1="' + g.padL + '" y1="' + baseY + '" x2="' + (g.width - g.padR) + '" y2="' + baseY + '" stroke="rgba(255,255,255,0.12)" />';
    svg += '</svg>';
    return svg;
}

var TREND_SVG_NS = 'http://www.w3.org/2000/svg';

/**
 * Mutates an existing chart SVG to match a new frame without reparsing markup. Grid lines,
 * the area/line polygons, data dots, and x-axis labels are updated node-by-node (creating or
 * removing only the delta), so pinch-zoom and pan stay smooth on mobile where an outerHTML
 * reparse per frame drops touch events. Node pools are keyed off the stable <g> groups.
 */
function applyTrendFrame(svgEl, frame, colors) {
    var g = TREND_GEOM;
    var chartH = g.height - g.padT - g.padB;
    var baseY = g.padT + chartH;

    var area = svgEl.querySelector('.trend-area');
    if (area) area.setAttribute('points', frame.areaPoints);
    var line = svgEl.querySelector('.trend-line');
    if (line) line.setAttribute('points', frame.polyline);

    // Grid: reconcile line/text pairs to the tick count, then reposition.
    var gridGroup = svgEl.querySelector('.trend-grid');
    if (gridGroup) {
        var lines = gridGroup.querySelectorAll('line');
        var texts = gridGroup.querySelectorAll('text');
        var wantTicks = frame.ticks.length;
        var haveTicks = lines.length;
        while (haveTicks < wantTicks) {
            var nl = document.createElementNS(TREND_SVG_NS, 'line');
            nl.setAttribute('x1', g.padL);
            nl.setAttribute('x2', g.width - g.padR);
            nl.setAttribute('stroke', 'rgba(255,255,255,0.06)');
            gridGroup.appendChild(nl);
            var nt = document.createElementNS(TREND_SVG_NS, 'text');
            nt.setAttribute('x', g.padL - 5);
            nt.setAttribute('text-anchor', 'end');
            nt.setAttribute('fill', 'rgba(255,255,255,0.4)');
            nt.setAttribute('font-size', '10');
            gridGroup.appendChild(nt);
            haveTicks++;
        }
        if (haveTicks !== lines.length) {
            lines = gridGroup.querySelectorAll('line');
            texts = gridGroup.querySelectorAll('text');
        }
        for (var gi = lines.length - 1; gi >= wantTicks; gi--) {
            lines[gi].remove();
            if (texts[gi]) texts[gi].remove();
        }
        lines = gridGroup.querySelectorAll('line');
        texts = gridGroup.querySelectorAll('text');
        for (var ti = 0; ti < frame.ticks.length; ti++) {
            var tk = frame.ticks[ti];
            lines[ti].setAttribute('y1', tk.y.toFixed(1));
            lines[ti].setAttribute('y2', tk.y.toFixed(1));
            texts[ti].setAttribute('y', (tk.y + 4).toFixed(1));
            texts[ti].textContent = tk.label;
        }
    }

    // Dots: reconcile the circle count to the visible points, then reposition.
    var dotsGroup = svgEl.querySelector('.trend-dots');
    if (dotsGroup) {
        var wantDots = frame.dotRadius > 0 ? frame.points.length : 0;
        var circles = dotsGroup.querySelectorAll('circle');
        // Track the count locally instead of re-querying the whole group per appended node: a pan
        // crossing a dot-count boundary can grow the pool by ~200 in one frame.
        var haveDots = circles.length;
        while (haveDots < wantDots) {
            var nc = document.createElementNS(TREND_SVG_NS, 'circle');
            nc.setAttribute('fill', colors.trendColor);
            nc.setAttribute('opacity', '0.6');
            dotsGroup.appendChild(nc);
            haveDots++;
        }
        if (haveDots !== circles.length) circles = dotsGroup.querySelectorAll('circle');
        for (var di = circles.length - 1; di >= wantDots; di--) circles[di].remove();
        if (wantDots > 0) {
            circles = dotsGroup.querySelectorAll('circle');
            for (var ci = 0; ci < frame.points.length; ci++) {
                var xy = frame.points[ci].split(',');
                circles[ci].setAttribute('cx', xy[0]);
                circles[ci].setAttribute('cy', xy[1]);
                circles[ci].setAttribute('r', frame.dotRadius);
            }
        }
    }

    // X-axis labels: reconcile count then reposition and relabel.
    var xGroup = svgEl.querySelector('.trend-xlabels');
    if (xGroup) {
        var labelNodes = xGroup.querySelectorAll('text');
        var haveLabels = labelNodes.length;
        while (haveLabels < frame.labels.length) {
            var nlbl = document.createElementNS(TREND_SVG_NS, 'text');
            nlbl.setAttribute('y', baseY + 18);
            nlbl.setAttribute('fill', 'rgba(255,255,255,0.55)');
            nlbl.setAttribute('font-size', '10');
            nlbl.setAttribute('font-weight', '500');
            xGroup.appendChild(nlbl);
            haveLabels++;
        }
        if (haveLabels !== labelNodes.length) labelNodes = xGroup.querySelectorAll('text');
        for (var li = labelNodes.length - 1; li >= frame.labels.length; li--) labelNodes[li].remove();
        labelNodes = xGroup.querySelectorAll('text');
        for (var lj = 0; lj < frame.labels.length; lj++) {
            var lb = frame.labels[lj];
            labelNodes[lj].setAttribute('x', lb.x.toFixed(1));
            labelNodes[lj].setAttribute('text-anchor', lb.anchor);
            labelNodes[lj].textContent = lb.text;
        }
    }
}

function renderTrendChart(timeline) {
    if (!timeline || !timeline.dataPoints || timeline.dataPoints.length < 2) {
        return { html: '<div class="trend-empty">' + T('trendEmpty', 'Not enough data yet. Growth timeline is computed during each scheduled scan.') + '</div>', chartState: null };
    }

    // Storage is daily and lossless. Interpolate the deduped gaps back to a dense daily array
    // once; all zoom levels are projected from this on the fly.
    var validGranularities = ['daily', 'weekly', 'monthly', 'yearly'];
    var rawGranularity = timeline.granularity || 'daily';
    var lowered = String(rawGranularity).toLowerCase();
    if (!validGranularities.includes(lowered)) {
        console.warn('[JellyfinHelper] Unknown granularity "' + rawGranularity + '", falling back to "daily".');
    }
    var fullDaily = interpolateDataPoints(timeline.dataPoints, 'daily');

    // Skip a long flat near-zero baseline before real growth starts, keeping one zero point.
    var peakSize = 0;
    for (const p of fullDaily) {
        if (p.cumulativeSize > peakSize) peakSize = p.cumulativeSize;
    }
    var zeroThreshold = peakSize * 0.005;
    var firstSignificant = fullDaily.findIndex(function (p) { return p.cumulativeSize > zeroThreshold; });
    if (firstSignificant < 0) firstSignificant = fullDaily.length - 1;
    var startIndex = Math.max(0, firstSignificant - 1);
    if (startIndex > 0) fullDaily = fullDaily.slice(startIndex);

    if (fullDaily.length < 2) {
        return { html: '<div class="trend-empty">' + T('trendEmpty', 'Not enough data yet. Growth timeline is computed during each scheduled scan.') + '</div>', chartState: null };
    }

    var dailyMin = new Date(fullDaily[0].date).getTime();
    var dailyMax = new Date(fullDaily.at(-1).date).getTime();
    var spanDays = (dailyMax - dailyMin) / TREND_DAY_MS;
    var initialLevel = pickLevelForSpan(spanDays);
    var projectedInitial = projectToGranularity(fullDaily, initialLevel);
    var projectionCache = Object.create(null);
    projectionCache[initialLevel] = projectedInitial;

    // Domain is the true daily data range, not the initial projection's bucket range.
    // A coarse projection snaps the first/last point to its bucket start (e.g. yearly
    // pulls Oct 2016 back to Jan 2016), which would leave dead space at the edges once
    // the user zooms to a finer level where no point falls in that snapped-off region.
    var minTime = dailyMin;
    var maxTime = dailyMax;
    var chartState = {
        fullDaily: fullDaily,
        minTime: minTime,
        maxTime: maxTime,
        startTime: minTime,
        endTime: maxTime,
        projectionCache: projectionCache
    };

    var initialFrame = computeTrendFrame(chartState);
    var initialSvg = renderTrendSvgString(initialFrame, readTrendColors());

    var overlays = '<div class="trend-crosshair"></div>';
    overlays += '<div class="trend-active-dot"></div>';
    overlays += '<div class="trend-tooltip"><div class="tt-date"></div><div class="tt-size"></div><div class="tt-files"></div></div>';

    var safeFileCount = Number(timeline.totalDirectoriesScanned);
    if (!Number.isFinite(safeFileCount) || safeFileCount < 0) safeFileCount = 0;
    var meta = '<div class="trend-meta" style="text-align:center;color:rgba(255,255,255,0.35);font-size:11px;margin-top:4px;">';
    meta += escHtml(T('trendGranularity', 'Granularity')) + ': <span class="trend-meta-level">' + escHtml(initialFrame.level) + '</span>';
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

    var html = '<div class="trend-chart">' + initialSvg + overlays + '</div>' + diffPanel + meta;
    return { html: html, chartState: chartState };
}

function touchDistance(touches) {
    var dx = touches[0].clientX - touches[1].clientX;
    var dy = touches[0].clientY - touches[1].clientY;
    return Math.hypot(dx, dy);
}

function touchMidX(touches) {
    return (touches[0].clientX + touches[1].clientX) / 2;
}

/**
 * Creates the zoom/pan window controller. The window is a [startTime, endTime] range over the
 * full domain; gestures mutate it and redraw. Kept out of attachTrendInteraction so the window
 * math (clamp, pixel-to-time, zoom, pan) is isolated and testable. Minimum span is two days so
 * daily zoom cannot invert.
 */
function createWindowController(chart, chartState, g, chartW, vbWidth, vbHeight, scheduleRedraw) {
    var domainStart = chartState.minTime;
    var domainEnd = chartState.maxTime;
    var fullDomainSpan = domainEnd - domainStart;

    // Minimum zoom-in span. Normally two days so daily zoom cannot invert, but never more than
    // a quarter of the domain: a brand-new server with a single day of history would otherwise
    // have its full view already sitting at the floor, so every zoom-in clamps straight back to
    // full and wheel events get handed to the browser as page scroll (silent no-op gestures).
    // Floor at one hour so the window can never collapse to zero span.
    var MIN_SPAN_MS = Math.max(TREND_HOUR_MS, Math.min(2 * TREND_DAY_MS, fullDomainSpan / 4));

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
        chartState.startTime = anchorTime - (anchorTime - chartState.startTime) * factor;
        chartState.endTime = anchorTime + (chartState.endTime - anchorTime) * factor;
        clampWindow();
        scheduleRedraw();
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
        scheduleRedraw();
    }

    return {
        clientXToTime: clientXToTime,
        zoomAbout: zoomAbout,
        panByPixels: panByPixels,
        domainStart: domainStart,
        domainEnd: domainEnd,
        MIN_SPAN_MS: MIN_SPAN_MS
    };
}

/**
 * Formats a signed delta with an arrow-direction CSS class. Shared by the size and file-count
 * rows of the diff panel so the sign/class branching lives in one place.
 */
function trendDeltaClass(delta) {
    if (delta > 0) return 'diff-up';
    if (delta < 0) return 'diff-down';
    return 'diff-neutral';
}

/**
 * Returns the leading sign for a delta: '+' when positive, the given negative sign when negative,
 * and '±' when zero. File counts use '' for negatives (the number already carries the minus).
 */
function trendDeltaSign(delta, negativeSign) {
    if (delta > 0) return '+';
    if (delta < 0) return negativeSign;
    return '±';
}

/**
 * Creates the tooltip / crosshair / diff-panel controller. Kept out of attachTrendInteraction so
 * the pointer-to-point mapping and the "then vs now" diff rendering are isolated. The controller
 * reads live render state (svg element, projected points, current level) through getState(), which
 * attachTrendInteraction refreshes on every redraw.
 */
function createTooltipController(chart, container, g, currentPt, getState) {
    var chartW = g.width - g.padL - g.padR;
    var vbWidth = g.width;
    var vbHeight = g.height;

    var diffPanel = container.querySelector('.trend-diff-panel');
    var diffDates = diffPanel ? diffPanel.querySelector('.trend-diff-dates') : null;
    var diffThenSize = diffPanel ? diffPanel.querySelector('.trend-diff-then-size') : null;
    var diffThenCount = diffPanel ? diffPanel.querySelector('.trend-diff-then-count') : null;
    var diffNowDate = diffPanel ? diffPanel.querySelector('.trend-diff-now-date') : null;
    var diffNowSize = diffPanel ? diffPanel.querySelector('.trend-diff-now-size') : null;
    var diffNowCount = diffPanel ? diffPanel.querySelector('.trend-diff-now-count') : null;
    var diffSize = diffPanel ? diffPanel.querySelector('.trend-diff-size') : null;
    var diffFiles = diffPanel ? diffPanel.querySelector('.trend-diff-files') : null;

    function nearestByClientX(clientX) {
        var s = getState();
        var rect = s.svgEl.getBoundingClientRect();
        var scale = Math.min(rect.width / vbWidth, rect.height / vbHeight);
        var offsetX = (rect.width - vbWidth * scale) / 2;
        var chartX = (clientX - rect.left - offsetX) / scale - g.padL;
        if (chartX < 0) chartX = 0;
        if (chartX > chartW) chartX = chartW;
        var best = 0;
        var bestDist = Infinity;
        for (var idx = 0; idx < s.pointData.length; idx++) {
            var dist = Math.abs((s.pointData[idx].x - g.padL) - chartX);
            if (dist < bestDist) { bestDist = dist; best = idx; }
        }
        return best;
    }

    function showTooltip(idx) {
        var s = getState();
        if (idx < 0 || idx >= s.pointData.length) return;
        var tooltip = chart.querySelector('.trend-tooltip');
        var crosshair = chart.querySelector('.trend-crosshair');
        var activeDot = chart.querySelector('.trend-active-dot');
        if (!tooltip || !crosshair || !activeDot) return;

        var pt = s.pointData[idx];
        var svgRect = s.svgEl.getBoundingClientRect();
        var chartRect = chart.getBoundingClientRect();
        var scale = Math.min(svgRect.width / vbWidth, svgRect.height / vbHeight);
        var offsetX = (svgRect.width - vbWidth * scale) / 2;
        var offsetY = (svgRect.height - vbHeight * scale) / 2;
        var pixelX = pt.x * scale + offsetX + (svgRect.left - chartRect.left);
        var pixelY = pt.y * scale + offsetY + (svgRect.top - chartRect.top);

        crosshair.style.left = pixelX + 'px';
        crosshair.classList.add('visible');
        activeDot.style.left = pixelX + 'px';
        activeDot.style.top = pixelY + 'px';
        activeDot.classList.add('visible');

        tooltip.querySelector('.tt-date').textContent = formatGranularityLabel(pt.d, s.level);
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

    function updateDiffPanel(idx) {
        var s = getState();
        if (!diffPanel || !diffDates || !diffSize || !diffFiles) return;
        if (idx < 0 || idx >= s.pointData.length) return;

        var pt = s.pointData[idx];
        diffDates.textContent = formatGranularityLabel(pt.d, s.level);
        if (diffThenSize) diffThenSize.textContent = formatBytes(pt.s);
        if (diffThenCount) diffThenCount.textContent = pt.c + ' ' + T('trendFiles', 'media files');
        if (diffNowDate) diffNowDate.textContent = formatGranularityLabel(currentPt.d, s.level) + ' (' + T('trendNow', 'now') + ')';
        if (diffNowSize) diffNowSize.textContent = formatBytes(currentPt.s);
        if (diffNowCount) diffNowCount.textContent = currentPt.c + ' ' + T('trendFiles', 'media files');

        var deltaSize = currentPt.s - pt.s;
        var deltaFiles = currentPt.c - pt.c;
        var pctRaw = currentPt.s > 0 ? (deltaSize / currentPt.s) * 100 : 0;
        var sSign = trendDeltaSign(deltaSize, '-');
        var pctLabel = '';
        if (deltaSize !== 0 && pctRaw !== 0) {
            var pctDisplay = Number.parseFloat(pctRaw.toFixed(2));
            pctLabel = ' (' + (pctDisplay > 0 ? '+' : '') + pctDisplay + '%)';
        }
        diffSize.textContent = sSign + formatBytes(Math.abs(deltaSize)) + pctLabel;
        diffSize.className = 'trend-diff-stat trend-diff-size ' + trendDeltaClass(deltaSize);

        var fSign = trendDeltaSign(deltaFiles, '');
        diffFiles.textContent = fSign + deltaFiles + ' ' + T('trendFiles', 'media files');
        diffFiles.className = 'trend-diff-stat trend-diff-files ' + trendDeltaClass(deltaFiles);

        diffPanel.classList.add('visible');
    }

    function hideTooltip() {
        var tooltip = chart.querySelector('.trend-tooltip');
        var crosshair = chart.querySelector('.trend-crosshair');
        var activeDot = chart.querySelector('.trend-active-dot');
        if (tooltip) tooltip.classList.remove('visible');
        if (crosshair) crosshair.classList.remove('visible');
        if (activeDot) activeDot.classList.remove('visible');
        if (diffPanel) diffPanel.classList.remove('visible');
    }

    function onHover(clientX) {
        var idx = nearestByClientX(clientX);
        showTooltip(idx);
        updateDiffPanel(idx);
    }

    return { onHover: onHover, hideTooltip: hideTooltip };
}

/**
 * Creates the render loop. Owns the mutable per-frame render state (projected points, level) and
 * the rAF-coalesced redraw, so attachTrendInteraction stays a thin wiring function. redraw mutates
 * the existing SVG in place rather than reparsing markup, keeping pinch-zoom and pan smooth on
 * touch devices.
 */
function createRenderLoop(chartState, svgEl, trendColors, metaLevelEl, initialFrame) {
    var state = { pointData: initialFrame.pointData, level: initialFrame.level };
    var redrawPending = false;

    function redraw() {
        if (!svgEl) return;
        var frame = computeTrendFrame(chartState);
        applyTrendFrame(svgEl, frame, trendColors);
        state.pointData = frame.pointData;
        state.level = frame.level;
        if (metaLevelEl) metaLevelEl.textContent = state.level;
    }

    function scheduleRedraw() {
        if (redrawPending) return;
        redrawPending = true;
        (window.requestAnimationFrame || function (cb) { return setTimeout(cb, 16); })(function () {
            redrawPending = false;
            redraw();
        });
    }

    return { state: state, scheduleRedraw: scheduleRedraw };
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

    var initialFrame = computeTrendFrame(chartState);
    // Theme colors are read once here; per-frame redraws never call getComputedStyle so a gesture
    // cannot trigger a forced style recalc.
    var trendColors = readTrendColors();
    var svgEl = chart.querySelector('svg');
    // "Now" is always the latest point of the full daily series, so the diff panel compares against
    // the true latest value even when panned into the past.
    var lastFull = chartState.fullDaily.at(-1);
    var currentPt = { d: lastFull.date, s: lastFull.cumulativeSize, c: lastFull.cumulativeFileCount };

    var metaLevelEl = container.querySelector('.trend-meta-level');
    var loop = createRenderLoop(chartState, svgEl, trendColors, metaLevelEl, initialFrame);

    var tip = createTooltipController(chart, container, g, currentPt, function () {
        return { svgEl: svgEl, pointData: loop.state.pointData, level: loop.state.level };
    });
    var onHover = tip.onHover;
    var hideTooltip = tip.hideTooltip;

    var win = createWindowController(chart, chartState, g, chartW, g.width, g.height, loop.scheduleRedraw);

    setupWheelZoom(chart, {
        clientXToTime: win.clientXToTime,
        zoomAbout: win.zoomAbout,
        hideTooltip: hideTooltip,
        domainStart: win.domainStart,
        domainEnd: win.domainEnd,
        chartState: chartState,
        minSpanMs: win.MIN_SPAN_MS
    });
    setupDragPan(chart, win.panByPixels, hideTooltip);
    setupTouchGestures(chart, win.clientXToTime, win.zoomAbout, win.panByPixels, hideTooltip, onHover);

    // Hover listeners are attached once to the stable <svg>. The SVG element is never replaced
    // (redraw mutates it in place), so these never need rebinding across gestures or redraws.
    if (svgEl) {
        svgEl.addEventListener('mousemove', function (e) {
            if (e.buttons !== 0) return; // dragging pans, not hovers
            onHover(e.clientX);
        });
        svgEl.addEventListener('mouseleave', function () {
            hideTooltip();
        });
    }
}

// Computes the zoom factor for one wheel notch. Mouse wheels move in larger, coarser steps than
// trackpad pinches, so they use a stronger multiplier per notch.
function wheelZoomFactor(isPinch, rawDelta, absDelta) {
    var scale = Math.min(absDelta / 80, 2.5);
    var zoomIn = rawDelta < 0;
    if (isPinch) {
        return zoomIn ? Math.pow(0.84, scale) : Math.pow(1.18, scale);
    }
    return zoomIn ? Math.pow(0.70, scale) : Math.pow(1.38, scale);
}

// Returns true when a wheel event at the current window edge should be left to the browser
// (page scroll) instead of consumed as a no-op zoom past the domain or minimum span.
function wheelHitsZoomLimit(ctx, rawDelta) {
    var currentSpan = ctx.chartState.endTime - ctx.chartState.startTime;
    var fullSpan = ctx.domainEnd - ctx.domainStart;
    if (rawDelta > 0) {
        return currentSpan >= fullSpan - 1;
    }
    return currentSpan <= ctx.minSpanMs + 1;
}

function setupWheelZoom(chart, ctx) {
    chart.addEventListener('wheel', function (e) {
        var isPinch = e.ctrlKey || e.metaKey;
        var rawDelta = e.deltaMode === 1 ? e.deltaY * 40 : e.deltaY; // deltaMode 1 = lines -> pixels
        var absDelta = Math.abs(rawDelta);

        // Trackpad two-finger swipe without pinch: let the browser handle page scroll.
        if (!isPinch && absDelta < 35) {
            return;
        }
        if (wheelHitsZoomLimit(ctx, rawDelta)) {
            return;
        }

        e.preventDefault();
        ctx.hideTooltip();
        ctx.zoomAbout(ctx.clientXToTime(e.clientX), wheelZoomFactor(isPinch, rawDelta, absDelta));
    }, {passive: false});
}

function setupDragPan(chart, panByPixels, hideTooltip) {
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
        // Only left button drags
        if (e.button !== 0) return;
        mouseDown = true;
        panning = false;
        downMouseX = e.clientX;
        lastMouseX = e.clientX;
        window.addEventListener('mousemove', onWindowMouseMove);
        window.addEventListener('mouseup', onWindowMouseUp);
    });
}

function setupTouchGestures(chart, clientXToTime, zoomAbout, panByPixels, hideTooltip, onHover) {
    var touchMode = null; // null | 'pan' | 'pinch'
    var touchStartX = 0;
    var touchStartY = 0;
    var touchStartT = 0;
    var lastTouchX = 0;
    var pinchStartDist = 0;

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
                var raw = pinchStartDist / dist; // fingers apart -> raw < 1 -> zoom in
                // Amplify the ratio so a modest spread covers more zoom range per frame.
                var factor = Math.pow(raw, 1.35);
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
                } else if (Math.abs(y - touchStartY) > 12) {
                    // Vertical swipe: let the browser scroll, do not enter pan mode
                    touchMode = 'scroll';
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
        } else if (e.touches.length === 1) {
            // Transition from pinch to single-finger after releasing one finger
            touchMode = null;
            touchStartX = e.touches[0].clientX;
            touchStartY = e.touches[0].clientY;
            lastTouchX = touchStartX;
            touchStartT = Date.now();
            pinchStartDist = 0;
        }
    });

    chart.addEventListener('touchcancel', function () {
        touchMode = null;
        pinchStartDist = 0;
        hideTooltip();
    });
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

    for (const lib of libKeys) {
        var items = grouped[lib];
        var libSize = sumPositiveSizes(items);

        html += '<div class="insight-tree-lib">';
        html += '<div class="insight-tree-lib-header">';
        html += '<span class="insight-tree-lib-name">' + escHtml(lib) + '</span>';
        html += '<span class="insight-tree-lib-size">' + formatBytes(libSize) + '</span>';
        html += '</div>';

        for (const e of items) {
            var badge = getInsightTypeBadge(e.CollectionType);
            html += '<span class="insight-tree-badge">' + badge + '</span>';
            html += '<span class="insight-tree-name">' + escHtml(e.Name) + '</span>';
            var _itemSize = Number(e.Size);
            var safeSize = (Number.isFinite(_itemSize) && _itemSize > 0) ? _itemSize : 0;
            html += '<span class="insight-tree-size">' + formatBytes(safeSize) + '</span>';
        }

        html += '</div>';
    }

    html += '</div>';
    return html;
}

/**
 * Sums the positive, finite Size values of a list of insight entries.
 */
function sumPositiveSizes(items) {
    var total = 0;
    for (const item of items) {
        var cur = Number(item.Size);
        if (Number.isFinite(cur) && cur > 0) total += cur;
    }
    return total;
}

/**
 * Builds the HTML for a single "Recently" entry: change badge, name, size and date.
 */
function buildRecentItemRow(e) {
    var badgeClass = e.ChangeType === 'added' ? 'insight-badge-added' : 'insight-badge-changed';
    var badgeText = e.ChangeType === 'added' ? T('insightAdded', 'added') : T('insightChanged', 'changed');
    var dateStr = e.ChangeType === 'changed' ? formatInsightDate(e.ModifiedUtc) : formatInsightDate(e.CreatedUtc);
    var itemSize = Number(e.Size);
    var safeSize = (Number.isFinite(itemSize) && itemSize > 0) ? itemSize : 0;

    return '<span class="insight-badge ' + badgeClass + '">' + badgeText + '</span>'
        + '<span class="insight-tree-name">' + escHtml(e.Name) + '</span>'
        + '<span class="insight-tree-meta">' + formatBytes(safeSize) + ' · ' + dateStr + '</span>';
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
    for (const libName of libKeys) {
        var groupItems = grouped[libName];

        html += '<div class="insight-tree-lib">';
        html += '<div class="insight-tree-lib-header">';
        html += '<span class="insight-tree-lib-name">' + escHtml(libName) + '</span>';
        html += '<span class="insight-tree-lib-size">' + formatBytes(sumPositiveSizes(groupItems)) + '</span>';
        html += '</div>';

        for (const e of groupItems) {
            html += buildRecentItemRow(e);
        }

        html += '</div>';
    }

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
    for (const it of items) {
        if (it.CollectionType) { ct = it.CollectionType.toLowerCase(); break; }
    }
    if (ct === 'movies' || ct === 'homevideos' || ct === 'musicvideos') return 0;
    if (ct === 'tvshows') return 1;
    return 2;
}

function groupByLibrary(entries) {
    var map = Object.create(null);
    for (const e of entries) {
        var lib = e.LibraryName || 'Unknown';
        if (!map[lib]) map[lib] = [];
        map[lib].push(e);
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
