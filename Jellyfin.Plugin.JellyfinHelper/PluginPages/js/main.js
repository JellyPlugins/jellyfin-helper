// noinspection JSUnusedLocalSymbols,JSUnresolvedReference
// This file is the LAST module concatenated into configPage.html — it closes the IIFE from shared.js.
// --- Main / Page Initialization ---

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

    function triggerExport(format) {
        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Statistics/Export/' + format);
        var mimeType = format === 'Json' ? 'application/json' : 'text/csv';
        var ext = format === 'Json' ? 'json' : 'csv';
        var timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
        var filename = 'jellyfin-statistics-' + timestamp + '.' + ext;

        apiClient.ajax({ type: 'GET', url: url, dataType: 'text' }).then(function (data) {
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
        if (diffMin < 60) return diffMin + ' ' + (diffMin === 1 ? T('minuteAgo', 'min ago') : T('minutesAgo', 'min ago'));
        var diffH = Math.floor(diffMin / 60);
        if (diffH < 24) return diffH + ' ' + (diffH === 1 ? T('hourAgo', 'hour ago') : T('hoursAgo', 'hours ago'));
        var diffD = Math.floor(diffH / 24);
        return diffD + ' ' + (diffD === 1 ? T('dayAgo', 'day ago') : T('daysAgo', 'days ago'));
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
        fillOverviewData(data);
        fillCodecsData(data);
        fillHealthData(data);
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
        var url = apiClient.getUrl('JellyfinHelper/Statistics') + '?forceRefresh=true';

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
    var _handlersBound = false;

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

        if (!_handlersBound) {
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
            _handlersBound = true;
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