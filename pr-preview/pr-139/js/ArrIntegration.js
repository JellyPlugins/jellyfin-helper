// --- Arr Integration Tab ---

var MAX_ARR_INSTANCES = 3;
var _testTimers = {};

function renderArrInstances(type, instances) {
    var h = '';
    var count = instances ? instances.length : 0;
    for (var i = 0; i < count; i++) {
        h += renderArrInstanceRow(type, i, instances[i]);
    }
    h += '<div id="' + type + 'AddBtnWrap">';
    h += '<button type="button" class="action-btn" id="btnAdd' + type + '"' +
        (count >= MAX_ARR_INSTANCES ? ' style="display:none;margin-top:0.5em;"'
            : ' style="margin-top:0.5em;"') +
        '>+ ' + T('addInstance', 'Add instance') + '</button>';
    h += '</div>';
    return h;
}

function renderArrInstanceRow(type, index, inst) {
    var prefix = type + '_' + index;
    var name = inst ? (inst.Name || '') : '';
    var url = inst ? (inst.Url || '') : '';
    var apiKey = inst ? (inst.ApiKey || '') : '';
    var placeholderUrl = type === 'Radarr' ? 'http://localhost:7878'
        : 'http://localhost:8989';
    var h = '<div class="arr-instance-row" data-type="' + type + '" data-index="'
        + index
        + '" style="border:1px solid rgba(255,255,255,0.1);border-radius:6px;padding:0.8em;margin-top:0.8em;position:relative;">';
    h += '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:0.3em;">';
    h += '<strong>' + type + ' #' + (index + 1) + '</strong>';
    h += '<button type="button" class="action-btn btn-arr-remove btnRemoveArr" data-type="'
        + type + '" data-index="' + index
        + '" style="padding:0.2em 0.6em;font-size:0.8em;"> ' + T('remove',
            'Remove') + '</button>';
    h += '</div>';
    var instanceNameId = prefix + '_name';
    h += '<label for="' + instanceNameId + '">' + T('instanceName',
            'Instance Name') + '</label><input type="text" id="' + instanceNameId
        + '" value="' + escAttr(name) + '" placeholder="e.g. ' + type + ' 4K">';
    var instanceUrlId = prefix + '_url';
    h += '<label for="' + instanceUrlId + '">' + T('url', 'URL')
        + '</label><input type="text" id="' + instanceUrlId + '" value="'
        + escAttr(url) + '" placeholder="' + placeholderUrl + '">';
    var instanceApiKeyId = prefix + '_key';
    h += '<label for="' + instanceApiKeyId + '">' + T('apiKey', 'API Key')
        + '</label><input type="password" id="' + instanceApiKeyId + '" value="'
        + escAttr(apiKey) + '">';
    h += '<button type="button" class="action-btn btn-arr-test btnTestArr" id="'
        + prefix + '_btnTest" data-type="' + type + '" data-index="' + index
        + '" style="padding:0.3em 0.8em;font-size:0.85em;">' + mi('extension') + T(
            'testConnection', 'Test Connection') + '</button>';
    h += '</div>';
    return h;
}

function collectArrInstances(type) {
    var rows = document.querySelectorAll(
        '.arr-instance-row[data-type="' + type + '"]');
    var result = [];
    for (var i = 0; i < rows.length; i++) {
        var idx = rows[i].getAttribute('data-index');
        var prefix = type + '_' + idx;
        var nameEl = document.getElementById(prefix + '_name');
        var urlEl = document.getElementById(prefix + '_url');
        var keyEl = document.getElementById(prefix + '_key');
        if (nameEl && urlEl && keyEl) {
            result.push({Name: nameEl.value, Url: urlEl.value, ApiKey: keyEl.value});
        }
    }
    return result;
}

function updateArrCollapsibleCount(type) {
    var rows = document.querySelectorAll(
        '.arr-instance-row[data-type="' + type + '"]');
    var countEl = document.getElementById('arrCount' + type);
    if (countEl) {
        countEl.textContent = createArrCountText(rows.length);
    }
}

function createArrCountText(count) {
    if (count === 0) {
        return '';
    }

    return '(' + count + ' / ' + MAX_ARR_INSTANCES + ')';
}

function addArrInstance(type) {
    var rows = document.querySelectorAll(
        '.arr-instance-row[data-type="' + type + '"]');
    if (rows.length >= MAX_ARR_INSTANCES) {
        return;
    }
    var newIndex = rows.length;
    var wrap = document.getElementById(type + 'AddBtnWrap');
    if (!wrap) {
        return;
    }
    var tmp = document.createElement('div');
    tmp.innerHTML = renderArrInstanceRow(type, newIndex, null);
    wrap.parentNode.insertBefore(tmp.firstChild, wrap);
    attachRemoveHandlers(type);
    attachTestHandlers();
    if (newIndex + 1 >= MAX_ARR_INSTANCES) {
        var btn = document.getElementById('btnAdd' + type);
        if (btn) {
            btn.style.display = 'none';
        }
    }
    // Expand the collapsible section and update count
    var collapsible = document.getElementById('arrCollapsible' + type);
    if (collapsible && !collapsible.classList.contains('arr-expanded')) {
        collapsible.classList.add('arr-expanded');
        var header = document.getElementById('arrCollapsibleHeader' + type);
        if (header) {
            header.setAttribute('aria-expanded', 'true');
        }
        var body = collapsible.querySelector('.arr-collapsible-body');
        if (body) {
            body.setAttribute('aria-hidden', 'false');
        }
    }
    updateArrCollapsibleCount(type);
}

// Note: This function performs multiple sequential DOM queries and updates.
// With MAX_ARR_INSTANCES = 3, layout thrashing is not a practical concern.
// If the instance limit were ever raised significantly, consider batching
// DOM reads and writes separately to avoid forced reflows.
function removeArrInstance(type, index) {
    // Clear all pending test timers for this type to prevent stale callbacks after reindexing
    for (var key in _testTimers) {
        if (key.indexOf(type + '_') === 0 && _testTimers[key]) {
            clearTimeout(_testTimers[key]);
            delete _testTimers[key];
        }
    }
    // Reset any test buttons that are in success/error state
    var testBtns = document.querySelectorAll(
        '.btnTestArr[data-type="' + type + '"]');
    for (var b = 0; b < testBtns.length; b++) {
        testBtns[b].classList.remove('success', 'error');
        testBtns[b].disabled = false;
        testBtns[b].innerHTML = mi('extension') + T('testConnection', 'Test Connection');
    }

    var row = document.querySelector(
        '.arr-instance-row[data-type="' + type + '"][data-index="' + index
        + '"]');
    if (row) {
        row.remove();
    }
    var remaining = document.querySelectorAll(
        '.arr-instance-row[data-type="' + type + '"]');
    for (var i = 0; i < remaining.length; i++) {
        remaining[i].setAttribute('data-index', i);
        var prefix = type + '_' + i;
        var inputs = remaining[i].querySelectorAll('input');
        var labels = remaining[i].querySelectorAll('label');
        var suffixes = ['_name', '_url', '_key'];
        for (var j = 0; j < inputs.length && j < suffixes.length; j++) {
            var oldId = inputs[j].id;
            var newId = prefix + suffixes[j];
            inputs[j].id = newId;

            // Update corresponding label if it exists
            var label = remaining[i].querySelector('label[for="' + oldId + '"]');
            if (label) {
                label.htmlFor = newId;
            } else if (labels[j]) {
                // Fallback to index-based if label[for] not found
                labels[j].htmlFor = newId;
            }
        }
        var strong = remaining[i].querySelector('strong');
        if (strong) {
            strong.textContent = type + ' #' + (i + 1);
        }
        var removeBtn = remaining[i].querySelector('.btnRemoveArr');
        if (removeBtn) {
            removeBtn.setAttribute('data-index', i);
        }
        var testBtn = remaining[i].querySelector('.btnTestArr');
        if (testBtn) {
            testBtn.setAttribute('data-index', i);
            testBtn.id = prefix + '_btnTest';
        }
    }
    var btn = document.getElementById('btnAdd' + type);
    if (btn && remaining.length < MAX_ARR_INSTANCES) {
        btn.style.display = '';
    }
    updateArrCollapsibleCount(type);

    // Auto-save settings after removal and show feedback on collapsible header (Finding 17: removed unnecessary typeof checks)
    var arrCollapsibleHeader = document.getElementById('arrCollapsibleHeader' + type);
    doSaveSettings(buildSettingsPayload(), {
        quiet: true,
        element: arrCollapsibleHeader
    });
}

function testArrConnection(type, index) {
    var prefix = type + '_' + index;
    var urlEl = document.getElementById(prefix + '_url');
    var keyEl = document.getElementById(prefix + '_key');
    var btn = document.getElementById(prefix + '_btnTest');
    if (!urlEl || !keyEl || !btn) {
        return;
    }

    var url = urlEl.value.trim();
    var apiKey = keyEl.value.trim();

    var originalHtml = mi('extension') + T('testConnection', 'Test Connection');

    var timerKey = type + '_' + index;
    if (_testTimers[timerKey]) {
        clearTimeout(_testTimers[timerKey]);
        _testTimers[timerKey] = null;
    }

    if (!url || !apiKey) {
        _testTimers[timerKey] = showButtonFeedback(btn, false,
            T('testMissingFields', 'URL and API Key are required.'), originalHtml,
            3000);
        return;
    }

    btn.disabled = true;
    btn.innerHTML = '<span class="btn-spinner"></span>' + T('testing',
        'Testing…');

    apiPost('JellyfinHelper/ArrIntegration/TestConnection',
        {Url: url, ApiKey: apiKey}, function (data) {
            btn.disabled = false;
            if (data.success) {
                _testTimers[timerKey] = showButtonFeedback(btn, true,
                    escHtml(data.message), originalHtml);
                // Auto-save settings after successful connection test (Finding 17: removed unnecessary typeof checks)
                doSaveSettings(buildSettingsPayload(), {
                    quiet: true,
                    element: document.getElementById('arrCollapsibleHeader' + type)
                });
            } else {
                _testTimers[timerKey] = showButtonFeedback(btn, false,
                    escHtml(data.message), originalHtml);
            }
        }, function () {
            btn.disabled = false;
            _testTimers[timerKey] = showButtonFeedback(btn, false,
                T('testConnectionFailed', 'Connection test failed.'), originalHtml);
        });
}

function attachTestHandlers() {
    var btns = document.querySelectorAll('.btnTestArr');
    for (var i = 0; i < btns.length; i++) {
        // Use onclick assignment (not addEventListener) to prevent handler stacking on re-bind
        btns[i].onclick = function () {
            testArrConnection(this.getAttribute('data-type'),
                parseInt(this.getAttribute('data-index'), 10));
        };
    }
}

function attachRemoveHandlers(type) {
    var selector = type ? '.btnRemoveArr[data-type="' + type + '"]'
        : '.btnRemoveArr';
    var btns = document.querySelectorAll(selector);
    for (var i = 0; i < btns.length; i++) {
        // Use onclick assignment (not addEventListener) to prevent handler stacking on re-bind
        btns[i].onclick = function () {
            removeArrInstance(this.getAttribute('data-type'),
                parseInt(this.getAttribute('data-index'), 10));
        };
    }
}

function attachAddHandlers() {
    var btnRadarr = document.getElementById('btnAddRadarr');
    var btnSonarr = document.getElementById('btnAddSonarr');
    if (btnRadarr) {
        btnRadarr.onclick = function () {
            addArrInstance('Radarr');
        };
    }
    if (btnSonarr) {
        btnSonarr.onclick = function () {
            addArrInstance('Sonarr');
        };
    }
}

// Cached instance list per Arr type, populated by initArrButtons. Keeps
// URL + ApiKey out of the DOM (they only live in JS memory) so the compare
// tab never surfaces credentials as data-* attributes on select options.
var _arrInstancesCache = {Radarr: [], Sonarr: []};

// Connection-status cache per instance key ("Radarr_0", "Sonarr_1", ...).
// { state: 'ok'|'error'|'testing'|'unknown', ts: <epoch ms> }
var _arrStatusCache = {};
var _arrStatusTtlMs = 60 * 1000;
// Monotonic request id per instance so late responses from a stale test
// (e.g. after the user switched instances or removed one in Settings)
// cannot overwrite the badge of the currently-selected instance.
var _arrStatusReqSeq = {};

function _arrCacheKey(type, index) {
    return type + '_' + index;
}

// Paint the status badge inside the select-wrapper for the given type.
// State enum: 'unknown' (empty), 'testing', 'ok', 'error'.
//
// The badge is a live region (role=status + aria-live=polite, set at
// render time in _renderArrTypeBlock) so assistive tech announces each
// state transition. The icon itself is aria-hidden (purely decorative);
// a visually-hidden .arr-status-sr-only sibling carries the localised
// text that screen readers actually read out.
function _renderArrStatusBadge(type, state) {
    var badge = document.getElementById('arrStatus' + type);
    if (!badge) {
        return;
    }
    badge.classList.remove('is-ok', 'is-error', 'is-testing');
    if (state === 'ok') {
        badge.classList.add('is-ok');
        badge.innerHTML = '<span aria-hidden="true">' + mi('check_circle') + '</span>'
            + '<span class="arr-status-sr-only">'
            + escHtml(T('arrStatusReachable', 'Reachable')) + '</span>';
    } else if (state === 'error') {
        badge.classList.add('is-error');
        badge.innerHTML = '<span aria-hidden="true">' + mi('error') + '</span>'
            + '<span class="arr-status-sr-only">'
            + escHtml(T('arrStatusUnreachable', 'Not reachable')) + '</span>';
    } else if (state === 'testing') {
        badge.classList.add('is-testing');
        badge.innerHTML = '<span class="arr-status-spinner" aria-hidden="true"></span>'
            + '<span class="arr-status-sr-only">'
            + escHtml(T('arrStatusTesting', 'Checking connection')) + '</span>';
    } else {
        // 'unknown' or anything else: clear the badge slot entirely.
        badge.innerHTML = '';
    }
}

// Kick off (or reuse cached) connection test for the currently-selected
// instance of the given type. Cache TTL is 60s so rapid dropdown toggling
// or tab-reopens don't hammer the Arr backend.
function refreshArrInstanceStatus(type, index) {
    var instances = _arrInstancesCache[type] || [];
    if (index < 0 || index >= instances.length) {
        _renderArrStatusBadge(type, 'unknown');
        return;
    }
    var inst = instances[index];
    if (!inst || !inst.Url || !inst.ApiKey) {
        _renderArrStatusBadge(type, 'unknown');
        return;
    }

    var cacheKey = _arrCacheKey(type, index);
    var cached = _arrStatusCache[cacheKey];
    if (cached && (Date.now() - cached.ts) < _arrStatusTtlMs
        && (cached.state === 'ok' || cached.state === 'error')) {
        _renderArrStatusBadge(type, cached.state);
        return;
    }

    _renderArrStatusBadge(type, 'testing');
    var reqId = (_arrStatusReqSeq[type] || 0) + 1;
    _arrStatusReqSeq[type] = reqId;

    apiPost('JellyfinHelper/ArrIntegration/TestConnection',
        {Url: inst.Url, ApiKey: inst.ApiKey}, function (data) {
            // Ignore stale responses (user picked a different instance in the
            // meantime, or a newer test superseded this one).
            if (reqId !== _arrStatusReqSeq[type]) {
                return;
            }
            var ok = !!(data && data.success);
            _arrStatusCache[cacheKey] = {state: ok ? 'ok' : 'error', ts: Date.now()};
            // Also only paint if the dropdown is still on this index.
            var sel = document.getElementById('arrSelect' + type);
            if (sel && parseInt(sel.value, 10) === index) {
                _renderArrStatusBadge(type, ok ? 'ok' : 'error');
            }
        }, function () {
            if (reqId !== _arrStatusReqSeq[type]) {
                return;
            }
            _arrStatusCache[cacheKey] = {state: 'error', ts: Date.now()};
            var sel = document.getElementById('arrSelect' + type);
            if (sel && parseInt(sel.value, 10) === index) {
                _renderArrStatusBadge(type, 'error');
            }
        });
}

// Render one "type block" (Radarr or Sonarr) with a section header, a
// single dropdown listing all instances of that type, and a single fixed-
// width Compare button. Returns HTML string; caller wires up event handlers.
function _renderArrTypeBlock(type, icon, instances) {
    var h = '<div class="arr-compare-block">';
    h += '<div class="arr-compare-header">' + icon + '<span>' + escHtml(type) + '</span></div>';
    h += '<div class="arr-compare-row">';
    h += '<div class="arr-instance-select-wrap">';
    h += '<span class="arr-instance-status" id="arrStatus' + type + '" role="status" aria-live="polite" aria-atomic="true"></span>';
    h += '<select class="arr-instance-select" id="arrSelect' + type + '" aria-label="' + escAttr(T('arrSelectInstance', 'Select an instance')) + '">';
    for (var i = 0; i < instances.length; i++) {
        var name = instances[i].Name || (type + ' #' + (i + 1));
        h += '<option value="' + i + '">' + escHtml(name) + '</option>';
    }
    h += '</select>';
    h += '</div>';
    h += '<button type="button" class="action-btn arr-compare-btn" id="btnCompare' + type + '">'
        + mi('search') + '<span>' + T('compare', 'Compare') + '</span></button>';
    h += '</div>';
    h += '</div>';
    return h;
}

function initArrButtons(cfg) {
    var btnContainer = document.getElementById('arrButtons');
    if (!btnContainer) {
        return;
    }

    // Any previous compare result belongs to a stale instance list; wipe it
    // so a config change (URL/key edit, instance removed) never leaves an
    // outdated comparison visible next to the freshly-rendered controls.
    var stalResult = document.getElementById('arrResult');
    if (stalResult) {
        stalResult.innerHTML = '';
    }

    // Reset the result cache so a config change immediately re-tests
    // instead of showing a stale ✓/✗ for a URL that may have changed.
    _arrStatusCache = {};
    // IMPORTANT: do NOT reset _arrStatusReqSeq — advance it. If we reset
    // to {} an in-flight request that started before this call would still
    // hold reqId=1, and the very next refreshArrInstanceStatus() call
    // would issue reqId=1 again, so the stale-response guard
    // `reqId !== _arrStatusReqSeq[type]` would erroneously match and let
    // the old credentials' response overwrite the badge for the new ones.
    // Bumping each type's sequence guarantees any pending callback holds
    // a strictly smaller ID than the current one and is rejected.
    _arrStatusReqSeq.Radarr = (_arrStatusReqSeq.Radarr || 0) + 1;
    _arrStatusReqSeq.Sonarr = (_arrStatusReqSeq.Sonarr || 0) + 1;

    var radarrInstances = resolveArrInstances(cfg, 'Radarr').filter(
        function (inst) {
            return inst && inst.Url && inst.ApiKey;
        });

    var sonarrInstances = resolveArrInstances(cfg, 'Sonarr').filter(
        function (inst) {
            return inst && inst.Url && inst.ApiKey;
        });

    _arrInstancesCache.Radarr = radarrInstances;
    _arrInstancesCache.Sonarr = sonarrInstances;

    if (radarrInstances.length === 0 && sonarrInstances.length === 0) {
        btnContainer.innerHTML = '<div class="no-data-container"><p>' + T(
                'arrNotConfigured',
                'Not configured. Please set URL and API key in Settings.')
            + '</p></div>';
        return;
    }

    // Wrap the compare controls in an .arr-card so the Arr tab shares the
    // same visual card language as the Settings and Health tabs.
    var h = '<div class="arr-card">';
    if (radarrInstances.length > 0) {
        h += _renderArrTypeBlock('Radarr', mi('movie'), radarrInstances);
    }
    if (sonarrInstances.length > 0) {
        h += _renderArrTypeBlock('Sonarr', mi('tv'), sonarrInstances);
    }
    h += '</div>'; // /arr-card

    btnContainer.innerHTML = h;

    // Wire up per-type handlers: change on the dropdown re-runs the health
    // check for the newly-selected instance (cached 60 s), click on the
    // Compare button dispatches the comparison. Uses onclick/onchange
    // assignment (not addEventListener) so a subsequent re-render — e.g.
    // after settings save — never stacks duplicate listeners on the same
    // element instance.
    _wireArrCompareControls('Radarr', radarrInstances);
    _wireArrCompareControls('Sonarr', sonarrInstances);
}

// Bind the change + click handlers for one type block and kick off the
// initial connection check for the currently-selected instance (usually
// index 0). Extracted from initArrButtons to keep that function focused
// on rendering the shell.
function _wireArrCompareControls(type, instances) {
    if (!instances || instances.length === 0) {
        return;
    }
    var sel = document.getElementById('arrSelect' + type);
    var btn = document.getElementById('btnCompare' + type);
    if (sel) {
        sel.onchange = function () {
            var idx = parseInt(sel.value, 10);
            if (isNaN(idx)) {
                return;
            }
            refreshArrInstanceStatus(type, idx);
        };
    }
    if (btn) {
        btn.onclick = function () {
            var idxSel = document.getElementById('arrSelect' + type);
            var idx = idxSel ? parseInt(idxSel.value, 10) : 0;
            if (isNaN(idx) || idx < 0) {
                idx = 0;
            }
            var opt = idxSel && idxSel.options[idxSel.selectedIndex];
            var label = opt ? opt.textContent : type;
            compareArr(type, idx, label);
        };
    }
    // Trigger the first health check so the user immediately sees whether
    // the default instance is reachable, without having to click anything.
    refreshArrInstanceStatus(type, 0);
}

// Render a single Arr comparison section (list with max 50 items and "and X more" hint)
function renderArrSection(icon, titleKey, titleFallback, items) {
    items = Array.isArray(items) ? items : [];
    var h = '<div class="arr-section"><h4 class="icon-label">' + icon + ' ' + T(titleKey,
            titleFallback) + ' - <span class="arr-count">' + items.length
        + '</span></h4>';
    if (items.length > 0) {
        h += '<div class="arr-list"><ul>';
        for (var i = 0; i < Math.min(items.length, 50); i++) {
            h += '<li>' + escHtml(items[i]) + '</li>';
        }
        if (items.length > 50) {
            h += '<li>… ' + T('andMore', 'and') + ' ' + (items.length - 50) + ' ' + T(
                'more', 'more') + '</li>';
        }
        h += '</ul></div>';
    }
    h += '</div>';
    return h;
}

function compareArr(type, index, label) {
    var resultDiv = document.getElementById('arrResult');
    if (!resultDiv) {
        return;
    }
    resultDiv.innerHTML = '<div class="loading-overlay" style="padding:1em;"><div class="spinner"></div><p>'
        + T('comparing', 'Comparing') + ' ' + escHtml(label || type)
        + '…</p></div>';
    apiGet('JellyfinHelper/ArrIntegration/Compare/' + type + '?index=' + index,
        function (data) {
            var instanceLabel = label || type;
            // Wrap the result block in an .arr-card so it visually matches the
            // Settings + Health card language (single container per logical
            // result, same border/radius/padding treatment).
            var h = '<div class="arr-card">';
            h += '<h3 style="margin-bottom:0.8em;">' + escHtml(instanceLabel)
                + '</h3>';
            h += renderArrSection(mi('check_circle'), 'inBoth', 'In Both', data.InBoth);
            h += renderArrSection(mi('inventory_2'), 'inArrOnly', 'In Arr Only (with file)',
                data.InArrOnly);
            h += renderArrSection(mi('warning'), 'inArrOnlyMissing', 'In Arr Only (no file)',
                data.InArrOnlyMissing);
            h += renderArrSection(mi('search'), 'inJellyfinOnly', 'In Jellyfin Only',
                data.InJellyfinOnly);
            h += '</div>'; // /arr-card
            resultDiv.innerHTML = h;
        }, function () {
            resultDiv.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + T('arrCompareError',
                'Failed to compare. Check settings.') + '</div>';
        });
}
