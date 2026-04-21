// --- Main / Page Initialization ---

function initTabs() {
    var tabBtns = document.querySelectorAll('.tab-btn');
    for (var i = 0; i < tabBtns.length; i++) {
        tabBtns[i].addEventListener('click', function () {
            var clickedBtn = this;
            var tabId = clickedBtn.getAttribute('data-tab');

            // Check if we're leaving the settings tab with unsaved changes
            var currentActive = document.querySelector('.tab-btn.active');
            var currentTab = currentActive ? currentActive.getAttribute('data-tab')
                : '';
            if (currentTab === 'settings' && tabId !== 'settings'
                && typeof checkUnsavedAndProceed === 'function') {
                checkUnsavedAndProceed(function () {
                    doTabSwitch(clickedBtn, tabId);
                });
                return;
            }

            doTabSwitch(clickedBtn, tabId);
        });
    }
}

function doTabSwitch(clickedBtn, tabId) {
    // Cleanup previous tab (e.g. stop auto-refresh timers) — only if leaving the logs tab
    var previousTab = document.querySelector('.tab-content.active');
    if (previousTab && previousTab.id === 'tab-logs' && typeof destroyLogsTab
        === 'function') {
        destroyLogsTab();
    }

    // Deactivate all
    var allBtns = document.querySelectorAll('.tab-btn');
    var allContent = document.querySelectorAll('.tab-content');
    for (var j = 0; j < allBtns.length; j++) {
        allBtns[j].classList.remove(
            'active');
    }
    for (var k = 0; k < allContent.length; k++) {
        allContent[k].classList.remove(
            'active');
    }

    // Activate selected
    clickedBtn.classList.add('active');
    var target = document.getElementById('tab-' + tabId);
    if (target) {
        target.classList.add('active');
    }

    // Initialize tab-specific logic
    if (tabId === 'recommendations' && typeof initRecommendationsTab === 'function') {
        initRecommendationsTab();
    }
    if (tabId === 'logs' && typeof initLogsTab === 'function') {
        initLogsTab();
    }
}

// formatTimeAgo is now in Shared.js

// Update the "Last Scan" badge in the header
function updateLastScanBadge(utcTimestamp) {
    var badge = document.getElementById('lastScanBadge');
    if (!badge) {
        return;
    }
    if (utcTimestamp) {
        badge.textContent = '🕒 ' + T('lastScan', 'Last Scan') + ': '
            + formatTimeAgo(utcTimestamp);
        badge.style.display = '';
    } else {
        badge.style.display = 'none';
    }
}

// Load the latest persisted statistics (no new scan) and populate tabs if available
function loadLatestStatistics() {
    apiGetOptional('JellyfinHelper/MediaStatistics/Latest', function (data) {
        if (data && data.Libraries) {
            fillScanData(data);
            updateLastScanBadge(data.ScanTimestamp);
        }
    }, function () {
        // 204 — no persisted data yet, auto-trigger initial scan
        console.log('Jellyfin Helper: No persisted statistics (204), triggering initial scan...');
        loadStatistics();
    });
}

// Render the initial tab shell (without scan data) so Settings/Arr are immediately accessible
function renderShell() {
    var html = '';

    // Tab bar
    html += '<div class="tab-bar">';
    html += '<button class="tab-btn active" data-tab="overview">📱 ' + T(
        'tabOverview', 'Overview') + '</button>';
    html += '<button class="tab-btn" data-tab="codecs">🎞️ ' + T('tabCodecs',
        'Codecs') + '</button>';
    html += '<button class="tab-btn" data-tab="health">🩺 ' + T('tabHealth',
        'Health') + '</button>';
    html += '<button class="tab-btn" data-tab="trends">📈 ' + T('tabTrends',
        'Trends') + '</button>';
    html += '<button class="tab-btn" data-tab="settings">⚙️ ' + T('tabSettings',
        'Settings') + '</button>';
    html += '<button class="tab-btn" data-tab="arr">🔗 ' + T('tabArr', 'Arr')
        + '</button>';
    html += '<button class="tab-btn" data-tab="recommendations">🤖 ' + T('tabRecommendations',
        'Smart Recs') + '</button>';
    html += '<button class="tab-btn" data-tab="logs">📋 ' + T('tabLogs', 'Logs')
        + '</button>';
    html += '</div>';

    // === OVERVIEW TAB (placeholder until scan) ===
    html += '<div class="tab-content active" id="tab-overview">';
    html += '<div id="overviewContent"><p style="text-align:center;padding:2em;opacity:0.5;">'
        + T('initializingScan', 'Initializing media scan\u2026') + '</p></div>';
    html += '</div>';

    // === CODECS TAB (placeholder until scan) ===
    html += '<div class="tab-content" id="tab-codecs">';
    html += '<div id="codecsContent"><p style="text-align:center;padding:2em;opacity:0.5;">'
        + T('initializingScan', 'Initializing media scan\u2026') + '</p></div>';
    html += '</div>';

    // === HEALTH TAB (placeholder until scan) ===
    html += '<div class="tab-content" id="tab-health">';
    html += '<div id="healthContent"><p style="text-align:center;padding:2em;opacity:0.5;">'
        + T('initializingScan', 'Initializing media scan\u2026') + '</p></div>';
    html += '</div>';

    // === TRENDS TAB ===
    html += '<div class="tab-content" id="tab-trends">';
    html += '<div class="section-title">📈 ' + T('trendTitle',
        'Library Growth Trend') + '</div>';
    html += '<div id="trendChartContainer" class="trend-container"><div class="trend-empty">'
        + T('loadingTrends', 'Loading trend data…') + '</div></div>';
    html += '<div id="insightsContainer" class="insights-container"><div class="trend-empty">'
        + T('loadingInsights', 'Loading insights…') + '</div></div>';
    html += '</div>';

    // === SETTINGS TAB ===
    html += '<div class="tab-content" id="tab-settings">';
    html += '<div class="settings-form" id="settingsForm">';
    html += '<div class="loading-overlay" style="padding:1em;"><div class="spinner"></div></div>';
    html += '</div>';
    html += '</div>';

    // === ARR TAB ===
    html += '<div class="tab-content" id="tab-arr">';
    html += '<div class="section-title">🔗 ' + T('arrTitle',
        'Arr Stack Integration') + '</div>';
    html += '<div id="arrContent">';
    html += '<div id="arrButtons"><div class="loading-overlay" style="padding:1em;"><div class="spinner"></div></div></div>';
    html += '<div id="arrResult"></div>';
    html += '</div>';
    html += '</div>';

    // === RECOMMENDATIONS TAB ===
    html += '<div class="tab-content" id="tab-recommendations">';
    html += '<div class="section-title">🤖 ' + T('recsTitle',
        'Smart Recommendations') + '</div>';
    html += '<div id="recsContent"><p style="text-align:center;padding:2em;opacity:0.5;">'
        + T('recsLoading', 'Select this tab to load recommendations…') + '</p></div>';
    html += '</div>';

    // === LOGS TAB ===
    html += '<div class="tab-content" id="tab-logs">';
    html += renderLogsTab();
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
    var btn = document.getElementById('btnScanLibraries');
    var loading = document.getElementById('loadingIndicator');
    var placeholder = document.getElementById('statsPlaceholder');

    if (btn) {
        btn.disabled = true;
        btn.classList.add('spinning');
    }
    if (loading) {
        loading.style.display = 'block';
    }
    if (placeholder) {
        placeholder.style.display = 'none';
    }

    apiGet('JellyfinHelper/MediaStatistics/ScanLibraries', function (data) {
        if (loading) {
            loading.style.display = 'none';
        }
        if (btn) {
            btn.disabled = false;
            btn.classList.remove('spinning');
        }

        // Fill scan-dependent tab contents
        fillScanData(data);
        updateLastScanBadge(data.ScanTimestamp);

        // Load/refresh trend data (force recompute after a fresh scan)
        loadTrendData(true);
        loadInsightsData();
    }, function (err) {
        if (loading) {
            loading.style.display = 'none';
        }
        var overviewContainer = document.getElementById('overviewContent');
        if (overviewContainer) {
            overviewContainer.innerHTML = '<div class="error-msg">❌ ' + T('error',
                    'Failed to load statistics. Make sure you are an administrator.')
                + '</div>';
        }
        if (btn) {
            btn.disabled = false;
            btn.classList.remove('spinning');
        }
        console.error('Jellyfin Helper: Error loading statistics', err);
    });
}

// --- Page initialization state machine ---
// _pageInitialized: guards against duplicate init calls once the page is fully set up.
// _initRetries / _maxInitRetries: retry counter for deferred DOM-ready detection
//   (Jellyfin may inject the plugin page asynchronously, so required elements
//    might not exist on the first call to initPage).
// _handlersBound: prevents duplicate event-handler registration when the view
//   is re-entered without a full page reload (SPA navigation).
var _pageInitialized = false;
var _initRetries = 0;
var _maxInitRetries = 20;
var _handlersBound = false;

function initPage() {
    if (_pageInitialized) {
        return;
    }

    var btnScanLibraries = document.getElementById('btnScanLibraries');

    if (!btnScanLibraries) {
        _initRetries++;
        if (_initRetries < _maxInitRetries) {
            console.warn('Jellyfin Helper: DOM not ready, retry ' + _initRetries + '/'
                + _maxInitRetries);
            setTimeout(initPage, 250);
        } else {
            console.error(
                'Jellyfin Helper: Could not find btnScanLibraries after ' + _maxInitRetries
                + ' retries');
        }
        return;
    }

    btnScanLibraries.innerHTML = SVG.REFRESH;

    _pageInitialized = true;

    // Load translations first, then render the shell UI immediately
    loadTranslations(function () {
        applyStaticTranslations();

        // Render the tab shell immediately (Settings & Arr accessible without scan)
        var placeholder = document.getElementById('statsPlaceholder');
        var result = document.getElementById('statsResult');
        if (placeholder) {
            placeholder.style.display = 'none';
        }
        if (result) {
            result.innerHTML = renderShell();
            result.style.display = 'block';
            // Reset tab-level state after DOM re-render so handlers get rebound
            if (typeof resetLogsTabState === 'function') {
                resetLogsTabState();
            }
        }

        // Initialize tab switching
        initTabs();

        // Load settings and arr buttons immediately (no scan needed)
        loadSettings();

        // Load persisted statistics from server (if any previous scan exists)
        loadLatestStatistics();

        // Load trend data async
        loadTrendData();
        loadInsightsData();
    });

    if (!_handlersBound) {
        btnScanLibraries.addEventListener('click', function (e) {
            e.preventDefault();
            loadStatistics();
        });
        _handlersBound = true;
    }
}

// Use Jellyfin's page lifecycle events
var _pageLifecycleBound = false;

function bindPageLifecycle() {
    if (_pageLifecycleBound) {
        return;
    }
    var pageEl = document.querySelector('#JellyfinHelperConfigPage');
    if (!pageEl) {
        return;
    }
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
    // Teardown when navigating away from the plugin page
    pageEl.addEventListener('pagehide', function () {
        if (typeof destroyLogsTab === 'function') {
            destroyLogsTab();
        }
    });
    pageEl.addEventListener('viewhide', function () {
        if (typeof destroyLogsTab === 'function') {
            destroyLogsTab();
        }
    });
    _pageLifecycleBound = true;
}

bindPageLifecycle();

// Fallback: try immediately in case events already fired or won't fire
if (document.readyState === 'complete' || document.readyState
    === 'interactive') {
    bindPageLifecycle();
    setTimeout(initPage, 150);
} else {
    document.addEventListener('DOMContentLoaded', function () {
        bindPageLifecycle();
        setTimeout(initPage, 150);
    });
}
