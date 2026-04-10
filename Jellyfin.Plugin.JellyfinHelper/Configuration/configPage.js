(function () {
    'use strict';

    var DONUT_COLORS = [
        '#00a4dc', '#e67e22', '#2ecc71', '#e74c3c', '#9b59b6',
        '#f1c40f', '#1abc9c', '#3498db', '#e91e63', '#ff9800',
        '#795548', '#607d8b', '#8bc34a', '#00bcd4', '#ff5722'
    ];

    // Translation helper — loaded async from /JellyfinHelper/Translations
    var _translations = {};

    function T(key, fallback) {
        return Object.prototype.hasOwnProperty.call(_translations, key)
            ? _translations[key]
            : (fallback || key);
    }

    function loadTranslations(callback) {
        try {
            var apiClient = ApiClient;
            apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Translations'), dataType: 'json' }).then(function (t) {
                _translations = t || {};
                if (callback) callback();
            }, function () {
                _translations = {};
                if (callback) callback();
            });
        } catch (e) {
            _translations = {};
            if (callback) callback();
        }
    }

    function applyStaticTranslations() {
        var title = document.querySelector('.stats-header h2');
        if (title) title.textContent = T('title', 'Jellyfin Helper \u2014 Media Statistics');

        var btnRefresh = document.getElementById('btnRefresh');
        if (btnRefresh) btnRefresh.innerHTML = '&#x21bb; ' + T('scanLibraries', 'Scan Libraries');

        var btnExportJson = document.getElementById('btnExportJson');
        if (btnExportJson) btnExportJson.textContent = '\ud83d\udce5 ' + T('exportJson', 'JSON');
        var btnExportCsv = document.getElementById('btnExportCsv');
        if (btnExportCsv) btnExportCsv.textContent = '\ud83d\udce5 ' + T('exportCsv', 'CSV');

        var loadingText = document.querySelector('#loadingIndicator p');
        if (loadingText) loadingText.textContent = T('scanDescription', 'Scanning libraries\u2026 This may take a while for large collections.');
        var placeholder = document.querySelector('#statsPlaceholder p');
        if (placeholder) placeholder.innerHTML = T('scanPlaceholder', 'Click <strong>Scan Libraries</strong> to analyze your media folders.');
    }

    function formatBytes(bytes) {
        if (bytes === 0) return '0 B';
        if (bytes < 0) return '-' + formatBytes(-bytes);
        var units = ['B', 'KB', 'MB', 'GB', 'TB'];
        var i = Math.floor(Math.log(bytes) / Math.log(1024));
        if (i >= units.length) i = units.length - 1;
        return (bytes / Math.pow(1024, i)).toFixed(2) + ' ' + units[i];
    }

    function getCollectionBadge(type) {
        var t = (type || '').toLowerCase();
        if (t === 'tvshows') return '<span class="badge badge-tvshows">TV Shows</span>';
        if (t === 'movies' || t === '') return '<span class="badge badge-movies">Movies</span>';
        if (t === 'music') return '<span class="badge badge-music">Music</span>';
        return '<span class="badge badge-other">' + (type || 'Mixed') + '</span>';
    }

    // SVG donut chart generator
    function renderDonutChart(data, size) {
        size = size || 160;
        var entries = [];
        var total = 0;
        for (var key in data) {
            if (data.hasOwnProperty(key) && data[key] > 0) {
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

    function buildBarSegments(data) {
        var total = data.TotalMovieVideoSize + data.TotalTvShowVideoSize +
            data.TotalSubtitleSize + data.TotalImageSize + data.TotalTrickplaySize +
            data.TotalNfoSize + data.TotalMusicAudioSize;

        var otherSize = 0;
        for (var i = 0; i < data.Libraries.length; i++) {
            otherSize += data.Libraries[i].OtherSize;
            total += data.Libraries[i].OtherSize;
        }

        if (total === 0) return '';

        var videoTotal = data.TotalMovieVideoSize + data.TotalTvShowVideoSize;
        var segments = [
            { cls: 'bar-video', pct: (videoTotal / total * 100), label: T('video', 'Video') },
            { cls: 'bar-audio', pct: (data.TotalMusicAudioSize / total * 100), label: T('audio', 'Audio') },
            { cls: 'bar-subtitle', pct: (data.TotalSubtitleSize / total * 100), label: T('subtitles', 'Subtitles') },
            { cls: 'bar-image', pct: (data.TotalImageSize / total * 100), label: T('images', 'Images') },
            { cls: 'bar-trickplay', pct: (data.TotalTrickplaySize / total * 100), label: T('trickplay', 'Trickplay') },
            { cls: 'bar-nfo', pct: (data.TotalNfoSize / total * 100), label: T('metadata', 'Metadata') },
            { cls: 'bar-other', pct: (otherSize / total * 100), label: T('other', 'Other') }
        ];

        var barHtml = '<div class="total-bar">';
        for (var s = 0; s < segments.length; s++) {
            if (segments[s].pct > 0) {
                barHtml += '<div class="bar-segment ' + segments[s].cls + '" style="width:' + segments[s].pct.toFixed(2) + '%" title="' + segments[s].label + '"></div>';
            }
        }
        barHtml += '</div>';

        barHtml += '<div class="legend">';
        var legendItems = [
            { cls: 'bar-video', label: T('video', 'Video') + ' (' + formatBytes(videoTotal) + ')' },
            { cls: 'bar-audio', label: T('audio', 'Audio') + ' (' + formatBytes(data.TotalMusicAudioSize) + ')' },
            { cls: 'bar-subtitle', label: T('subtitles', 'Subtitles') + ' (' + formatBytes(data.TotalSubtitleSize) + ')' },
            { cls: 'bar-image', label: T('images', 'Images') + ' (' + formatBytes(data.TotalImageSize) + ')' },
            { cls: 'bar-trickplay', label: T('trickplay', 'Trickplay') + ' (' + formatBytes(data.TotalTrickplaySize) + ')' },
            { cls: 'bar-nfo', label: T('metadata', 'Metadata') + ' (' + formatBytes(data.TotalNfoSize) + ')' },
            { cls: 'bar-other', label: T('other', 'Other') + ' (' + formatBytes(otherSize) + ')' }
        ];
        for (var l = 0; l < legendItems.length; l++) {
            barHtml += '<div class="legend-item"><div class="legend-dot ' + legendItems[l].cls + '"></div>' + legendItems[l].label + '</div>';
        }
        barHtml += '</div>';

        return barHtml;
    }

    // Aggregate dictionaries across libraries
    function aggregateDict(libraries, prop) {
        var result = {};
        for (var i = 0; i < libraries.length; i++) {
            var dict = libraries[i][prop];
            if (dict) {
                for (var key in dict) {
                    if (dict.hasOwnProperty(key)) {
                        result[key] = (result[key] || 0) + dict[key];
                    }
                }
            }
        }
        return result;
    }

    function renderHealthChecks(data) {
        var totalNoSubs = 0, totalNoImages = 0, totalNoNfo = 0, totalOrphaned = 0;
        for (var i = 0; i < data.Libraries.length; i++) {
            totalNoSubs += data.Libraries[i].VideosWithoutSubtitles || 0;
            totalNoImages += data.Libraries[i].VideosWithoutImages || 0;
            totalNoNfo += data.Libraries[i].VideosWithoutNfo || 0;
            totalOrphaned += data.Libraries[i].OrphanedMetadataDirectories || 0;
        }

        var html = '<div class="health-grid">';

        html += '<div class="health-item"><div class="health-value ' + (totalNoSubs > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoSubs + '</div>';
        html += '<div class="health-label">' + T('noSubtitles', 'Videos without subtitles') + '</div></div>';

        html += '<div class="health-item"><div class="health-value ' + (totalNoImages > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoImages + '</div>';
        html += '<div class="health-label">' + T('noImages', 'Videos without images') + '</div></div>';

        html += '<div class="health-item"><div class="health-value ' + (totalNoNfo > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoNfo + '</div>';
        html += '<div class="health-label">' + T('noNfo', 'Videos without NFO') + '</div></div>';

        html += '<div class="health-item"><div class="health-value ' + (totalOrphaned > 0 ? 'health-bad' : 'health-ok') + '">' + totalOrphaned + '</div>';
        html += '<div class="health-label">' + T('orphanedDirs', 'Orphaned metadata dirs') + '</div></div>';

        html += '</div>';
        return html;
    }

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
            if (snapshots[i].totalSize > maxSize) maxSize = snapshots[i].totalSize;
        }
        if (maxSize === 0) maxSize = 1;

        // Build points
        var points = [];
        var step = snapshots.length > 1 ? chartW / (snapshots.length - 1) : 0;
        for (var j = 0; j < snapshots.length; j++) {
            var x = padL + j * step;
            var y = padT + chartH - (snapshots[j].totalSize / maxSize * chartH);
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
            var ts = snapshots[k].timestamp ? new Date(snapshots[k].timestamp).toLocaleDateString() : '';
            svg += '<circle cx="' + coords[0] + '" cy="' + coords[1] + '" r="3" fill="#00a4dc">' +
                '<title>' + ts + ': ' + formatBytes(snapshots[k].totalSize) + '</title></circle>';
        }

        // X-axis labels (first and last)
        if (snapshots.length > 0) {
            var firstDate = snapshots[0].timestamp ? new Date(snapshots[0].timestamp).toLocaleDateString() : '';
            var lastDate = snapshots[snapshots.length - 1].timestamp ? new Date(snapshots[snapshots.length - 1].timestamp).toLocaleDateString() : '';
            svg += '<text x="' + padL + '" y="' + (height - 5) + '" text-anchor="start" fill="rgba(255,255,255,0.4)" font-size="10">' + firstDate + '</text>';
            svg += '<text x="' + (width - padR) + '" y="' + (height - 5) + '" text-anchor="end" fill="rgba(255,255,255,0.4)" font-size="10">' + lastDate + '</text>';
        }

        svg += '</svg>';
        return '<div class="trend-chart">' + svg + '</div>';
    }

    var MAX_ARR_INSTANCES = 3;

    function renderArrInstances(type, instances) {
        var h = '';
        var count = instances ? instances.length : 0;
        for (var i = 0; i < count; i++) {
            h += renderArrInstanceRow(type, i, instances[i]);
        }
        h += '<div id="' + type + 'AddBtnWrap">';
        if (count < MAX_ARR_INSTANCES) {
            h += '<button type="button" class="action-btn" id="btnAdd' + type + '" style="margin-top:0.5em;">+ ' + T('addAnother', 'Add another') + ' ' + type + ' ' + T('instance', 'instance') + '</button>';
        }
        h += '</div>';
        return h;
    }

    function renderArrInstanceRow(type, index, inst) {
        var prefix = type + '_' + index;
        var name = inst ? (inst.Name || '') : '';
        var url = inst ? (inst.Url || '') : '';
        var apiKey = inst ? (inst.ApiKey || '') : '';
        var placeholderUrl = type === 'Radarr' ? 'http://localhost:7878' : 'http://localhost:8989';
        var h = '<div class="arr-instance-row" data-type="' + type + '" data-index="' + index + '" style="border:1px solid rgba(255,255,255,0.1);border-radius:6px;padding:0.8em;margin-top:0.8em;position:relative;">';
        h += '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:0.3em;">';
        h += '<strong>' + type + ' #' + (index + 1) + '</strong>';
        h += '<button type="button" class="action-btn btnRemoveArr" data-type="' + type + '" data-index="' + index + '" style="padding:0.2em 0.6em;font-size:0.8em;background:#e74c3c;">✕ ' + T('remove', 'Remove') + '</button>';
        h += '</div>';
        h += '<label>' + T('instanceName', 'Instance Name') + '</label><input type="text" id="' + prefix + '_name" value="' + escAttr(name) + '" placeholder="e.g. ' + type + ' 4K">';
        h += '<label>' + T('url', 'URL') + '</label><input type="text" id="' + prefix + '_url" value="' + escAttr(url) + '" placeholder="' + placeholderUrl + '">';
        h += '<label>' + T('apiKey', 'API Key') + '</label><input type="password" id="' + prefix + '_key" value="' + escAttr(apiKey) + '">';
        h += '</div>';
        return h;
    }

    function escAttr(s) { return (s || '').replace(/&/g,'&amp;').replace(/"/g,'&quot;').replace(/</g,'&lt;'); }
    function escHtml(s) { return String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;'); }

    function collectArrInstances(type) {
        var rows = document.querySelectorAll('.arr-instance-row[data-type="' + type + '"]');
        var result = [];
        for (var i = 0; i < rows.length; i++) {
            var idx = rows[i].getAttribute('data-index');
            var prefix = type + '_' + idx;
            var nameEl = document.getElementById(prefix + '_name');
            var urlEl = document.getElementById(prefix + '_url');
            var keyEl = document.getElementById(prefix + '_key');
            if (nameEl && urlEl && keyEl) {
                result.push({ Name: nameEl.value, Url: urlEl.value, ApiKey: keyEl.value });
            }
        }
        return result;
    }

    function addArrInstance(type) {
        var rows = document.querySelectorAll('.arr-instance-row[data-type="' + type + '"]');
        if (rows.length >= MAX_ARR_INSTANCES) return;
        var newIndex = rows.length;
        var wrap = document.getElementById(type + 'AddBtnWrap');
        if (!wrap) return;
        var tmp = document.createElement('div');
        tmp.innerHTML = renderArrInstanceRow(type, newIndex, null);
        wrap.parentNode.insertBefore(tmp.firstChild, wrap);
        // Attach remove handler
        attachRemoveHandlers();
        // Hide add button if at max
        if (newIndex + 1 >= MAX_ARR_INSTANCES) {
            var btn = document.getElementById('btnAdd' + type);
            if (btn) btn.style.display = 'none';
        }
    }

    function removeArrInstance(type, index) {
        var row = document.querySelector('.arr-instance-row[data-type="' + type + '"][data-index="' + index + '"]');
        if (row) row.remove();
        // Re-index remaining rows
        var remaining = document.querySelectorAll('.arr-instance-row[data-type="' + type + '"]');
        for (var i = 0; i < remaining.length; i++) {
            remaining[i].setAttribute('data-index', i);
            var prefix = type + '_' + i;
            // Update IDs for inputs
            var inputs = remaining[i].querySelectorAll('input');
            var suffixes = ['_name', '_url', '_key'];
            for (var j = 0; j < inputs.length && j < suffixes.length; j++) {
                inputs[j].id = prefix + suffixes[j];
            }
            // Update header text
            var strong = remaining[i].querySelector('strong');
            if (strong) strong.textContent = type + ' #' + (i + 1);
            // Update remove button data-index
            var removeBtn = remaining[i].querySelector('.btnRemoveArr');
            if (removeBtn) removeBtn.setAttribute('data-index', i);
        }
        // Show add button again if below max
        var btn = document.getElementById('btnAdd' + type);
        if (btn && remaining.length < MAX_ARR_INSTANCES) btn.style.display = '';
    }

    function attachRemoveHandlers() {
        var btns = document.querySelectorAll('.btnRemoveArr');
        for (var i = 0; i < btns.length; i++) {
            btns[i].onclick = function () {
                removeArrInstance(this.getAttribute('data-type'), parseInt(this.getAttribute('data-index'), 10));
            };
        }
    }

    function attachAddHandlers() {
        var btnRadarr = document.getElementById('btnAddRadarr');
        var btnSonarr = document.getElementById('btnAddSonarr');
        if (btnRadarr) btnRadarr.onclick = function () { addArrInstance('Radarr'); };
        if (btnSonarr) btnSonarr.onclick = function () { addArrInstance('Sonarr'); };
    }

    // --- Settings Tab Logic ---
    function loadSettings() {
        var form = document.getElementById('settingsForm');
        if (!form) return;
        var apiClient = ApiClient;
        apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Configuration'), dataType: 'json' }).then(function (cfg) {
            var h = '';
            h += '<label>' + T('includedLibraries', 'Included Libraries (whitelist, comma-separated)') + '</label>';
            h += '<input type="text" id="cfgIncluded" value="' + escAttr(cfg.IncludedLibraries || '') + '">';
            h += '<div class="help-text">' + T('includedLibrariesHelp', 'Leave empty to include all libraries.') + '</div>';
            h += '<label>' + T('excludedLibraries', 'Excluded Libraries (blacklist, comma-separated)') + '</label>';
            h += '<input type="text" id="cfgExcluded" value="' + escAttr(cfg.ExcludedLibraries || '') + '">';
            h += '<label>' + T('orphanMinAgeDays', 'Orphan Minimum Age (days)') + '</label>';
            h += '<input type="number" id="cfgOrphanAge" min="0" value="' + (cfg.OrphanMinAgeDays || 0) + '">';
            h += '<div class="help-text">' + T('orphanMinAgeDaysHelp', 'Items younger than this are protected from deletion.') + '</div>';
            h += '<div class="section-divider"></div>';
            h += '<div style="font-weight:600;font-size:0.9em;margin-top:0.5em;">' + T('taskModeTitle', 'Task Mode (per Task)') + '</div>';
            h += '<div class="help-text">' + T('taskModeHelp', 'Choose whether each task is active, runs in dry-run mode (only logs), or is deactivated.') + '</div>';
            var taskModes = [['Activate', T('activate', 'Activate')],['DryRun', T('dryRun', 'Dry Run')],['Deactivate', T('deactivate', 'Deactivate')]];
            function renderTaskModeSelect(id, label, currentVal) {
                var s = '<label>' + label + '</label><select id="' + id + '">';
                for (var tm = 0; tm < taskModes.length; tm++) {
                    s += '<option value="' + taskModes[tm][0] + '"' + (currentVal === taskModes[tm][0] ? ' selected' : '') + '>' + taskModes[tm][1] + '</option>';
                }
                s += '</select>';
                return s;
            }
            h += renderTaskModeSelect('cfgTrickplayMode', T('trickplayFolderCleaner', 'Trickplay Folder Cleaner'), cfg.TrickplayTaskMode || 'DryRun');
            h += renderTaskModeSelect('cfgEmptyFolderMode', T('emptyMediaFolderCleaner', 'Empty Media Folder Cleaner'), cfg.EmptyMediaFolderTaskMode || 'DryRun');
            h += renderTaskModeSelect('cfgSubtitleMode', T('orphanedSubtitleCleaner', 'Orphaned Subtitle Cleaner'), cfg.OrphanedSubtitleTaskMode || 'DryRun');
            h += renderTaskModeSelect('cfgStrmMode', T('strmFileRepair', '.strm File Repair'), cfg.StrmRepairTaskMode || 'DryRun');
            h += '<div class="section-divider"></div>';
            h += '<div class="checkbox-row"><input type="checkbox" id="cfgTrash"' + (cfg.UseTrash ? ' checked' : '') + '><label>' + T('useTrash', 'Use Trash (Recycle Bin)') + '</label></div>';
            h += '<label>' + T('trashFolder', 'Trash Folder Path') + '</label>';
            h += '<input type="text" id="cfgTrashPath" value="' + escAttr(cfg.TrashFolderPath || '.jellyfin-trash') + '">';
            h += '<label>' + T('trashRetention', 'Trash Retention (days)') + '</label>';
            h += '<input type="number" id="cfgTrashDays" min="0" value="' + (cfg.TrashRetentionDays || 30) + '">';
            h += '<div class="section-divider"></div>';
            h += '<label>' + T('language', 'Dashboard Language') + '</label>';
            h += '<select id="cfgLang">';
            var langs = [['en','English'],['de','Deutsch'],['fr','Français'],['es','Español'],['pt','Português'],['zh','中文'],['tr','Türkçe']];
            for (var i = 0; i < langs.length; i++) {
                h += '<option value="' + langs[i][0] + '"' + (cfg.Language === langs[i][0] ? ' selected' : '') + '>' + langs[i][1] + '</option>';
            }
            h += '</select>';

            // --- Radarr Instances ---
            h += '<div class="section-divider"></div>';
            h += '<div class="section-title" style="border-bottom:none;font-size:1em;margin-bottom:0;">🎬 ' + T('radarrInstances', 'Radarr Instances') + ' <span style="font-weight:400;font-size:0.8em;opacity:0.6;">(max ' + MAX_ARR_INSTANCES + ')</span></div>';
            var radarrInstances = cfg.RadarrInstances && cfg.RadarrInstances.length > 0
                ? cfg.RadarrInstances
                : (cfg.RadarrUrl ? [{ Name: 'Radarr', Url: cfg.RadarrUrl, ApiKey: cfg.RadarrApiKey }] : []);
            h += renderArrInstances('Radarr', radarrInstances);

            // --- Sonarr Instances ---
            h += '<div class="section-divider"></div>';
            h += '<div class="section-title" style="border-bottom:none;font-size:1em;margin-bottom:0;">📺 ' + T('sonarrInstances', 'Sonarr Instances') + ' <span style="font-weight:400;font-size:0.8em;opacity:0.6;">(max ' + MAX_ARR_INSTANCES + ')</span></div>';
            var sonarrInstances = cfg.SonarrInstances && cfg.SonarrInstances.length > 0
                ? cfg.SonarrInstances
                : (cfg.SonarrUrl ? [{ Name: 'Sonarr', Url: cfg.SonarrUrl, ApiKey: cfg.SonarrApiKey }] : []);
            h += renderArrInstances('Sonarr', sonarrInstances);

            h += '<div style="margin-top:1.5em;"><button class="refresh-btn" id="btnSaveSettings">' + T('saveSettings', 'Save Settings') + '</button></div>';
            h += '<div id="settingsMsg" style="margin-top:0.5em;"></div>';
            form.innerHTML = h;
            document.getElementById('btnSaveSettings').addEventListener('click', saveSettings);
            attachRemoveHandlers();
            attachAddHandlers();
        }, function () {
            form.innerHTML = '<div class="error-msg">' + T('settingsError', 'Failed to load settings.') + '</div>';
        });
    }

    function saveSettings() {
        var btn = document.getElementById('btnSaveSettings');
        var msg = document.getElementById('settingsMsg');
        btn.disabled = true;
        msg.innerHTML = '';

        var radarrInstances = collectArrInstances('Radarr');
        var sonarrInstances = collectArrInstances('Sonarr');

        var payload = {
            IncludedLibraries: document.getElementById('cfgIncluded').value,
            ExcludedLibraries: document.getElementById('cfgExcluded').value,
            OrphanMinAgeDays: parseInt(document.getElementById('cfgOrphanAge').value, 10) || 0,
            TrickplayTaskMode: document.getElementById('cfgTrickplayMode').value,
            EmptyMediaFolderTaskMode: document.getElementById('cfgEmptyFolderMode').value,
            OrphanedSubtitleTaskMode: document.getElementById('cfgSubtitleMode').value,
            StrmRepairTaskMode: document.getElementById('cfgStrmMode').value,
            UseTrash: document.getElementById('cfgTrash').checked,
            TrashFolderPath: document.getElementById('cfgTrashPath').value,
            TrashRetentionDays: parseInt(document.getElementById('cfgTrashDays').value, 10) || 30,
            Language: document.getElementById('cfgLang').value,
            RadarrUrl: radarrInstances.length > 0 ? radarrInstances[0].Url : '',
            RadarrApiKey: radarrInstances.length > 0 ? radarrInstances[0].ApiKey : '',
            SonarrUrl: sonarrInstances.length > 0 ? sonarrInstances[0].Url : '',
            SonarrApiKey: sonarrInstances.length > 0 ? sonarrInstances[0].ApiKey : '',
            RadarrInstances: radarrInstances,
            SonarrInstances: sonarrInstances
        };
        var apiClient = ApiClient;
        apiClient.ajax({
            type: 'POST', url: apiClient.getUrl('JellyfinHelper/Configuration'),
            data: JSON.stringify(payload), contentType: 'application/json'
        }).then(function () {
            msg.innerHTML = '<div class="success-msg">✅ ' + T('settingsSaved', 'Settings saved!') + '</div>';
            btn.disabled = false;
        }, function () {
            msg.innerHTML = '<div class="error-msg">❌ ' + T('settingsError', 'Failed to save settings.') + '</div>';
            btn.disabled = false;
        });
    }

    // --- Arr Tab Logic ---
    function initArrButtons() {
        var btnContainer = document.getElementById('arrButtons');
        if (!btnContainer) return;
        var apiClient = ApiClient;
        apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Configuration'), dataType: 'json' }).then(function (cfg) {
            var h = '';
            var radarrInstances = cfg.RadarrInstances && cfg.RadarrInstances.length > 0
                ? cfg.RadarrInstances
                : (cfg.RadarrUrl ? [{ Name: 'Radarr', Url: cfg.RadarrUrl }] : []);
            var sonarrInstances = cfg.SonarrInstances && cfg.SonarrInstances.length > 0
                ? cfg.SonarrInstances
                : (cfg.SonarrUrl ? [{ Name: 'Sonarr', Url: cfg.SonarrUrl }] : []);

            if (radarrInstances.length === 0 && sonarrInstances.length === 0) {
                btnContainer.innerHTML = '<p style="opacity:0.5;">' + T('arrNotConfigured', 'No Radarr or Sonarr instances configured. Add them in the Settings tab.') + '</p>';
                return;
            }

            if (radarrInstances.length > 0) {
                h += '<div style="margin-bottom:1em;">';
                h += '<h4 style="margin:0 0 0.5em 0;opacity:0.7;">🎬 Radarr</h4>';
                h += '<div class="header-actions" style="flex-wrap:wrap;">';
                for (var r = 0; r < radarrInstances.length; r++) {
                    var rName = radarrInstances[r].Name || ('Radarr #' + (r + 1));
                    h += '<button class="action-btn arr-compare-btn" data-type="Radarr" data-index="' + r + '">' + T('compareWith', 'Compare with') + ' ' + escAttr(rName) + '</button>';
                }
                h += '</div></div>';
            }

            if (sonarrInstances.length > 0) {
                h += '<div style="margin-bottom:1em;">';
                h += '<h4 style="margin:0 0 0.5em 0;opacity:0.7;">📺 Sonarr</h4>';
                h += '<div class="header-actions" style="flex-wrap:wrap;">';
                for (var s = 0; s < sonarrInstances.length; s++) {
                    var sName = sonarrInstances[s].Name || ('Sonarr #' + (s + 1));
                    h += '<button class="action-btn arr-compare-btn" data-type="Sonarr" data-index="' + s + '">' + T('compareWith', 'Compare with') + ' ' + escAttr(sName) + '</button>';
                }
                h += '</div></div>';
            }

            btnContainer.innerHTML = h;

            // Attach click handlers
            var compareBtns = btnContainer.querySelectorAll('.arr-compare-btn');
            for (var i = 0; i < compareBtns.length; i++) {
                compareBtns[i].addEventListener('click', function () {
                    var type = this.getAttribute('data-type');
                    var idx = parseInt(this.getAttribute('data-index'), 10);
                    var label = this.textContent;
                    compareArr(type, idx, label);
                });
            }
        }, function () {
            btnContainer.innerHTML = '<div class="error-msg">❌ ' + T('arrConfigError', 'Failed to load Arr configuration.') + '</div>';
        });
    }

    function compareArr(type, index, label) {
        var resultDiv = document.getElementById('arrResult');
        if (!resultDiv) return;
        resultDiv.innerHTML = '<div class="loading-overlay" style="padding:1em;"><div class="spinner"></div><p>' + T('comparing', 'Comparing') + ' ' + (label || type) + '…</p></div>';
        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Arr/' + type + '/Compare') + '?index=' + index;
        apiClient.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function (data) {
            var instanceLabel = label ? label.replace(T('compareWith', 'Compare with') + ' ', '') : type;
            var h = '<h3 style="margin-bottom:0.8em;">' + escHtml(instanceLabel) + '</h3>';
            h += '<div class="arr-section"><h4>✅ ' + T('inBoth', 'In Both') + ' — <span class="arr-count">' + data.InBoth.length + '</span></h4>';
            if (data.InBoth.length > 0) { h += '<div class="arr-list"><ul>'; for (var a = 0; a < Math.min(data.InBoth.length, 50); a++) h += '<li>' + escHtml(data.InBoth[a]) + '</li>'; if (data.InBoth.length > 50) h += '<li>… ' + T('andMore', 'and') + ' ' + (data.InBoth.length - 50) + ' ' + T('more', 'more') + '</li>'; h += '</ul></div>'; }
            h += '</div>';
            h += '<div class="arr-section"><h4>📦 ' + T('inArrOnly', 'In Arr Only (with file)') + ' — <span class="arr-count">' + data.InArrOnly.length + '</span></h4>';
            if (data.InArrOnly.length > 0) { h += '<div class="arr-list"><ul>'; for (var b = 0; b < data.InArrOnly.length; b++) h += '<li>' + escHtml(data.InArrOnly[b]) + '</li>'; h += '</ul></div>'; }
            h += '</div>';
            h += '<div class="arr-section"><h4>⚠️ ' + T('inArrOnlyMissing', 'In Arr Only (no file)') + ' — <span class="arr-count">' + data.InArrOnlyMissing.length + '</span></h4>';
            if (data.InArrOnlyMissing.length > 0) { h += '<div class="arr-list"><ul>'; for (var c = 0; c < data.InArrOnlyMissing.length; c++) h += '<li>' + escHtml(data.InArrOnlyMissing[c]) + '</li>'; h += '</ul></div>'; }
            h += '</div>';
            h += '<div class="arr-section"><h4>🔍 ' + T('inJellyfinOnly', 'In Jellyfin Only') + ' — <span class="arr-count">' + data.InJellyfinOnly.length + '</span></h4>';
            if (data.InJellyfinOnly.length > 0) { h += '<div class="arr-list"><ul>'; for (var d = 0; d < data.InJellyfinOnly.length; d++) h += '<li>' + escHtml(data.InJellyfinOnly[d]) + '</li>'; h += '</ul></div>'; }
            h += '</div>';
            resultDiv.innerHTML = h;
        }, function () {
            resultDiv.innerHTML = '<div class="error-msg">❌ ' + T('arrCompareError', 'Failed to compare. Check settings.') + '</div>';
        });
    }

    // --- Cleanup Stats (loaded async into overview) ---
    function loadCleanupStats() {
        var apiClient = ApiClient;
        apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Cleanup/Statistics'), dataType: 'json' }).then(function (stats) {
            var cleanupContainer = document.getElementById('cleanup-stats-container');
            if (!cleanupContainer) return;
            var h = '<div class="section-title">🧹 ' + T('cleanupStatistics', 'Cleanup Statistics') + '</div>';
            h += '<div class="stats-grid">';
            h += '<div class="stat-card highlight"><h3>' + T('totalBytesFreed', 'Total Space Freed') + '</h3>';
            h += '<p class="stat-value">' + formatBytes(stats.TotalBytesFreed) + '</p></div>';
            h += '<div class="stat-card highlight"><h3>' + T('totalItemsDeleted', 'Total Items Deleted') + '</h3>';
            h += '<p class="stat-value">' + stats.TotalItemsDeleted + '</p>';
            var lastTs = stats.LastCleanupTimestamp && stats.LastCleanupTimestamp !== '0001-01-01T00:00:00' ? new Date(stats.LastCleanupTimestamp).toLocaleString() : T('never', 'Never');
            h += '<p class="stat-detail">' + T('lastCleanup', 'Last cleanup') + ': ' + lastTs + '</p></div>';
            h += '</div>';
            cleanupContainer.innerHTML = h;
        });
    }

    function initTabs() {
        var tabBtns = document.querySelectorAll('.tab-btn');
        for (var i = 0; i < tabBtns.length; i++) {
            tabBtns[i].addEventListener('click', function () {
                var tabId = this.getAttribute('data-tab');

                // Deactivate all
                var allBtns = document.querySelectorAll('.tab-btn');
                var allContent = document.querySelectorAll('.tab-content');
                for (var j = 0; j < allBtns.length; j++) allBtns[j].classList.remove('active');
                for (var k = 0; k < allContent.length; k++) allContent[k].classList.remove('active');

                // Activate selected
                this.classList.add('active');
                var target = document.getElementById('tab-' + tabId);
                if (target) target.classList.add('active');
            });
        }
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

    function triggerExport(format) {
        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Statistics/Export/' + format);
        var mimeType = format === 'Json' ? 'application/json' : 'text/csv';
        var ext = format === 'Json' ? 'json' : 'csv';
        var timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
        var filename = 'jellyfin-statistics-' + timestamp + '.' + ext;

        apiClient.ajax({ type: 'GET', url: url }).then(function (data) {
            var content = typeof data === 'string' ? data : JSON.stringify(data, null, 2);
            var blob = new Blob([content], { type: mimeType });
            var blobUrl = URL.createObjectURL(blob);
            var link = document.createElement('a');
            link.href = blobUrl;
            link.download = filename;
            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);
            setTimeout(function () { URL.revokeObjectURL(blobUrl); }, 5000);
        }, function () {
            alert(T('exportError', 'Export failed. Please try again.'));
        });
    }

    // Format a UTC timestamp as "X ago" relative text
    function formatTimeAgo(utcTimestamp) {
        if (!utcTimestamp) return '';
        var then = new Date(utcTimestamp);
        var now = new Date();
        var diffMs = now - then;
        if (diffMs < 0) return '';
        var diffMin = Math.floor(diffMs / 60000);
        if (diffMin < 1) return T('justNow', 'just now');
        if (diffMin < 60) return diffMin + ' ' + T('minutesAgo', 'min ago');
        var diffH = Math.floor(diffMin / 60);
        if (diffH < 24) return diffH + ' ' + T('hoursAgo', 'hours ago');
        var diffD = Math.floor(diffH / 24);
        return diffD + ' ' + T('daysAgo', 'days ago');
    }

    // Update the "Last Scan" badge in the header
    function updateLastScanBadge(utcTimestamp) {
        var badge = document.getElementById('lastScanBadge');
        if (!badge) return;
        if (utcTimestamp) {
            badge.textContent = '🕒 ' + T('lastScan', 'Last Scan') + ': ' + formatTimeAgo(utcTimestamp);
            badge.style.display = 'inline-block';
        } else {
            badge.style.display = 'none';
        }
    }

    // Load the latest persisted statistics (no new scan) and populate tabs if available
    function loadLatestStatistics() {
        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Statistics/Latest');

        apiClient.ajax({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (data) {
            if (data && data.Libraries && data.Libraries.length > 0) {
                console.log('Jellyfin Helper: Loaded persisted statistics from server');
                fillScanData(data);
                updateLastScanBadge(data.ScanTimestamp);

                // Enable export buttons
                document.getElementById('btnExportJson').disabled = false;
                document.getElementById('btnExportCsv').disabled = false;
            }
        }, function () {
            // 204 or error — no persisted data, that's fine
            console.log('Jellyfin Helper: No persisted statistics available');
        });
    }

    // Render the initial tab shell (without scan data) so Settings/Arr are immediately accessible
    function renderShell() {
        var html = '';

        // Tab bar
        html += '<div class="tab-bar">';
        html += '<button class="tab-btn active" data-tab="overview">' + T('tabOverview', 'Overview') + '</button>';
        html += '<button class="tab-btn" data-tab="codecs">' + T('tabCodecs', 'Codecs') + '</button>';
        html += '<button class="tab-btn" data-tab="health">' + T('tabHealth', 'Health') + '</button>';
        html += '<button class="tab-btn" data-tab="trends">' + T('tabTrends', 'Trends') + '</button>';
        html += '<button class="tab-btn" data-tab="settings">' + T('tabSettings', '⚙️ Settings') + '</button>';
        html += '<button class="tab-btn" data-tab="arr">' + T('tabArr', '🔗 Arr') + '</button>';
        html += '</div>';

        // === OVERVIEW TAB (placeholder until scan) ===
        html += '<div class="tab-content active" id="tab-overview">';
        html += '<div id="overviewContent"><p style="text-align:center;padding:2em;opacity:0.5;">' + T('scanPlaceholder', 'Click <strong>Scan Libraries</strong> to analyze your media folders.') + '</p></div>';
        html += '</div>';

        // === CODECS TAB (placeholder until scan) ===
        html += '<div class="tab-content" id="tab-codecs">';
        html += '<div id="codecsContent"><p style="text-align:center;padding:2em;opacity:0.5;">' + T('scanPlaceholder', 'Click <strong>Scan Libraries</strong> to analyze your media folders.') + '</p></div>';
        html += '</div>';

        // === HEALTH TAB (placeholder until scan) ===
        html += '<div class="tab-content" id="tab-health">';
        html += '<div id="healthContent"><p style="text-align:center;padding:2em;opacity:0.5;">' + T('scanPlaceholder', 'Click <strong>Scan Libraries</strong> to analyze your media folders.') + '</p></div>';
        html += '</div>';

        // === TRENDS TAB ===
        html += '<div class="tab-content" id="tab-trends">';
        html += '<div class="section-title">' + T('trendTitle', 'Library Growth Trend') + '</div>';
        html += '<div id="trendChartContainer" class="trend-container"><div class="trend-empty">' + T('loadingTrends', 'Loading trend data…') + '</div></div>';
        html += '</div>';

        // === SETTINGS TAB ===
        html += '<div class="tab-content" id="tab-settings">';
        html += '<div class="section-title">' + T('settingsTitle', '⚙️ Plugin Settings') + '</div>';
        html += '<div class="settings-form" id="settingsForm">';
        html += '<div class="loading-overlay" style="padding:1em;"><div class="spinner"></div></div>';
        html += '</div>';
        html += '</div>';

        // === ARR TAB ===
        html += '<div class="tab-content" id="tab-arr">';
        html += '<div class="section-title">' + T('arrTitle', '🔗 Arr Stack Integration') + '</div>';
        html += '<div id="arrContent">';
        html += '<div id="arrButtons"><div class="loading-overlay" style="padding:1em;"><div class="spinner"></div></div></div>';
        html += '<div id="arrResult"></div>';
        html += '</div>';
        html += '</div>';

        return html;
    }

    // Fill scan-dependent tabs with data after a successful scan
    function fillScanData(data) {
        // === OVERVIEW ===
        var overviewHtml = '';
        overviewHtml += '<div class="stats-grid">';
        overviewHtml += '<div class="stat-card"><h3>' + T('movieVideoData', '🎬 Video Data — Movies') + '</h3>';
        overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalMovieVideoSize) + '</p>';
        var movieFiles = 0;
        for (var m = 0; m < data.Movies.length; m++) movieFiles += data.Movies[m].VideoFileCount;
        overviewHtml += '<p class="stat-detail">' + movieFiles + ' ' + (movieFiles === 1 ? T('file', 'file') : T('files', 'files')) + ' ' + T('across', 'across') + ' ' + data.Movies.length + ' ' + T('libraries', 'libraries') + '</p>';
        overviewHtml += '</div>';

        overviewHtml += '<div class="stat-card"><h3>' + T('tvVideoData', '📺 Video Data — TV Shows') + '</h3>';
        overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalTvShowVideoSize) + '</p>';
        var tvFiles = 0;
        for (var t = 0; t < data.TvShows.length; t++) tvFiles += data.TvShows[t].VideoFileCount;
        overviewHtml += '<p class="stat-detail">' + tvFiles + ' ' + (tvFiles === 1 ? T('episode', 'episode') : T('episodes', 'episodes')) + ' ' + T('across', 'across') + ' ' + data.TvShows.length + ' ' + T('libraries', 'libraries') + '</p>';
        overviewHtml += '</div>';

        overviewHtml += '<div class="stat-card"><h3>' + T('musicAudioData', '🎵 Music / Audio') + '</h3>';
        overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalMusicAudioSize) + '</p>';
        overviewHtml += '<p class="stat-detail">' + data.TotalAudioFileCount + ' ' + (data.TotalAudioFileCount === 1 ? T('file', 'file') : T('files', 'files')) + '</p>';
        overviewHtml += '</div>';

        overviewHtml += '<div class="stat-card"><h3>' + T('trickplayData', '🖼️ Trickplay Data') + '</h3>';
        overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalTrickplaySize) + '</p>';
        var trickplayFolders = 0;
        for (var tp = 0; tp < data.Libraries.length; tp++) trickplayFolders += data.Libraries[tp].TrickplayFolderCount;
        overviewHtml += '<p class="stat-detail">' + trickplayFolders + ' ' + (trickplayFolders === 1 ? T('folder', 'folder') : T('folders', 'folders')) + '</p>';
        overviewHtml += '</div>';

        overviewHtml += '<div class="stat-card"><h3>' + T('subtitleData', '📝 Subtitles') + '</h3>';
        overviewHtml += '<p class="stat-value">' + formatBytes(data.TotalSubtitleSize) + '</p>';
        var subFiles = 0;
        for (var sb = 0; sb < data.Libraries.length; sb++) subFiles += data.Libraries[sb].SubtitleFileCount;
        overviewHtml += '<p class="stat-detail">' + subFiles + ' ' + (subFiles === 1 ? T('file', 'file') : T('files', 'files')) + '</p>';
        overviewHtml += '</div>';

        overviewHtml += '<div class="stat-card"><h3>' + T('totalFiles', '📊 Total Files') + '</h3>';
        var totalMediaFiles = data.TotalVideoFileCount + data.TotalAudioFileCount;
        overviewHtml += '<p class="stat-value">' + totalMediaFiles + ' ' + (totalMediaFiles === 1 ? T('mediaFile', 'media file') : T('mediaFiles', 'media files')) + '</p>';
        overviewHtml += '<p class="stat-detail">' + data.TotalVideoFileCount + ' ' + T('video', 'video') + ', ' + data.TotalAudioFileCount + ' ' + T('audio', 'audio') + '</p>';
        overviewHtml += '</div>';
        overviewHtml += '</div>';

        var grandTotal = 0;
        for (var gt = 0; gt < data.Libraries.length; gt++) grandTotal += data.Libraries[gt].TotalSize;
        overviewHtml += '<div class="section-title">' + T('storageDistribution', 'Storage Distribution') + ' — <span style="color:#00a4dc;">' + formatBytes(grandTotal) + ' ' + T('total', 'Total') + '</span></div>';
        overviewHtml += buildBarSegments(data);

        overviewHtml += '<div class="section-title">' + T('perLibraryBreakdown', 'Per-Library Breakdown') + '</div>';
        overviewHtml += '<div class="library-table-wrapper"><table class="library-table">';
        overviewHtml += '<thead><tr>';
        overviewHtml += '<th>' + T('library', 'Library') + '</th><th>' + T('type', 'Type') + '</th><th>' + T('video', 'Video') + '</th><th>' + T('audio', 'Audio') + '</th><th>' + T('subtitles', 'Subtitles') + '</th><th>' + T('images', 'Images') + '</th><th>' + T('trickplay', 'Trickplay') + '</th><th>' + T('total', 'Total') + '</th>';
        overviewHtml += '</tr></thead><tbody>';

        for (var i = 0; i < data.Libraries.length; i++) {
            var lib = data.Libraries[i];
            overviewHtml += '<tr>';
            overviewHtml += '<td>' + escHtml(lib.LibraryName) + '</td>';
            overviewHtml += '<td>' + getCollectionBadge(lib.CollectionType) + '</td>';
            overviewHtml += '<td>' + formatBytes(lib.VideoSize) + '</td>';
            overviewHtml += '<td>' + formatBytes(lib.AudioSize) + '</td>';
            overviewHtml += '<td>' + formatBytes(lib.SubtitleSize) + '</td>';
            overviewHtml += '<td>' + formatBytes(lib.ImageSize) + '</td>';
            overviewHtml += '<td>' + formatBytes(lib.TrickplaySize) + '</td>';
            overviewHtml += '<td><strong>' + formatBytes(lib.TotalSize) + '</strong></td>';
            overviewHtml += '</tr>';
        }

        overviewHtml += '</tbody></table></div>';
        overviewHtml += '<div id="cleanup-stats-container"></div>';

        var overviewContainer = document.getElementById('overviewContent');
        if (overviewContainer) overviewContainer.innerHTML = overviewHtml;

        // === CODECS ===
        var videoCodecs = aggregateDict(data.Libraries, 'VideoCodecs');
        var audioCodecs = aggregateDict(data.Libraries, 'AudioCodecs');
        var containers = aggregateDict(data.Libraries, 'ContainerFormats');
        var resolutions = aggregateDict(data.Libraries, 'Resolutions');

        var codecsHtml = '<div class="charts-row">';
        codecsHtml += '<div class="chart-box"><h4>' + T('videoCodecs', '🎬 Video Codecs') + '</h4>';
        codecsHtml += renderDonutChart(videoCodecs);
        codecsHtml += '</div>';
        codecsHtml += '<div class="chart-box"><h4>' + T('audioCodecs', '🎵 Audio Codecs') + '</h4>';
        codecsHtml += renderDonutChart(audioCodecs);
        codecsHtml += '</div>';
        codecsHtml += '<div class="chart-box"><h4>' + T('containerFormats', '📦 Container Formats') + '</h4>';
        codecsHtml += renderDonutChart(containers);
        codecsHtml += '</div>';
        codecsHtml += '<div class="chart-box"><h4>' + T('resolutions', '📐 Resolutions') + '</h4>';
        codecsHtml += renderDonutChart(resolutions);
        codecsHtml += '</div>';
        codecsHtml += '</div>';

        var codecsContainer = document.getElementById('codecsContent');
        if (codecsContainer) codecsContainer.innerHTML = codecsHtml;

        // === HEALTH ===
        var healthHtml = '<div class="section-title">' + T('healthChecks', 'Library Health Checks') + '</div>';
        healthHtml += renderHealthChecks(data);

        var healthContainer = document.getElementById('healthContent');
        if (healthContainer) healthContainer.innerHTML = healthHtml;

        // Load cleanup stats into overview
        loadCleanupStats();
    }

    function loadStatistics() {
        var btn = document.getElementById('btnRefresh');
        var loading = document.getElementById('loadingIndicator');
        var placeholder = document.getElementById('statsPlaceholder');

        btn.disabled = true;
        btn.textContent = '⏳ ' + T('scanning', 'Scanning…');
        loading.style.display = 'block';
        if (placeholder) placeholder.style.display = 'none';

        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Statistics');

        apiClient.ajax({
            type: 'GET',
            url: url,
            dataType: 'json'
        }).then(function (data) {
            loading.style.display = 'none';
            btn.disabled = false;
            btn.innerHTML = '&#x21bb; ' + T('scanLibraries', 'Scan Libraries');

            // Enable export buttons
            document.getElementById('btnExportJson').disabled = false;
            document.getElementById('btnExportCsv').disabled = false;

            // Fill scan-dependent tab contents
            fillScanData(data);
            updateLastScanBadge(data.ScanTimestamp);

            // Load/refresh trend data
            loadTrendData();
        }, function (err) {
            loading.style.display = 'none';
            var overviewContainer = document.getElementById('overviewContent');
            if (overviewContainer) {
                overviewContainer.innerHTML = '<div class="error-msg">❌ ' + T('error', 'Failed to load statistics. Make sure you are an administrator.') + '</div>';
            }
            btn.disabled = false;
            btn.innerHTML = '&#x21bb; ' + T('scanLibraries', 'Scan Libraries');
            console.error('Jellyfin Helper: Error loading statistics', err);
        });
    }

    // --- Page initialization ---
    var _pageInitialized = false;
    var _initRetries = 0;
    var _maxInitRetries = 20;

    function initPage() {
        if (_pageInitialized) return;

        var btnRefresh = document.getElementById('btnRefresh');
        var btnExportJson = document.getElementById('btnExportJson');
        var btnExportCsv = document.getElementById('btnExportCsv');

        if (!btnRefresh) {
            _initRetries++;
            if (_initRetries < _maxInitRetries) {
                console.warn('Jellyfin Helper: DOM not ready, retry ' + _initRetries + '/' + _maxInitRetries);
                setTimeout(initPage, 250);
            } else {
                console.error('Jellyfin Helper: Could not find btnRefresh after ' + _maxInitRetries + ' retries');
            }
            return;
        }

        _pageInitialized = true;

        // Load translations first, then render the shell UI immediately
        loadTranslations(function () {
            applyStaticTranslations();

            // Render the tab shell immediately (Settings & Arr accessible without scan)
            var placeholder = document.getElementById('statsPlaceholder');
            var result = document.getElementById('statsResult');
            if (placeholder) placeholder.style.display = 'none';
            if (result) {
                result.innerHTML = renderShell();
                result.style.display = 'block';
            }

            // Initialize tab switching
            initTabs();

            // Load settings and arr buttons immediately (no scan needed)
            loadSettings();
            initArrButtons();

            // Load persisted statistics from server (if any previous scan exists)
            loadLatestStatistics();

            // Load trend data async
            loadTrendData();
        });

        btnRefresh.addEventListener('click', function (e) {
            e.preventDefault();
            console.log('Jellyfin Helper: Scan button clicked');
            loadStatistics();
        });
        if (btnExportJson) {
            btnExportJson.addEventListener('click', function () { triggerExport('Json'); });
        }
        if (btnExportCsv) {
            btnExportCsv.addEventListener('click', function () { triggerExport('Csv'); });
        }

        console.log('Jellyfin Helper: Page initialized successfully');
    }

    // Use Jellyfin's page lifecycle events
    var pageEl = document.querySelector('#JellyfinHelperConfigPage');
    if (pageEl) {
        pageEl.addEventListener('pageshow', function () {
            _pageInitialized = false;
            _initRetries = 0;
            setTimeout(initPage, 0);
        });
        pageEl.addEventListener('viewshow', function () {
            _pageInitialized = false;
            _initRetries = 0;
            setTimeout(initPage, 0);
        });
    }

    // Fallback: try immediately in case events already fired or won't fire
    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        setTimeout(initPage, 150);
    } else {
        document.addEventListener('DOMContentLoaded', function () {
            setTimeout(initPage, 150);
        });
    }
})();