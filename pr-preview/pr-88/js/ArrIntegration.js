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
        + '" style="padding:0.2em 0.6em;font-size:0.8em;">✕ ' + T('remove',
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
        + '" style="padding:0.3em 0.8em;font-size:0.85em;">🔌 ' + T(
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
        var header = collapsible.querySelector('.arr-collapsible-header');
        if (header) {
            header.setAttribute('aria-expanded', 'true');
        }
    }
    updateArrCollapsibleCount(type);
}

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
        testBtns[b].innerHTML = '🔌 ' + T('testConnection', 'Test Connection');
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

    var originalHtml = '🔌 ' + T('testConnection', 'Test Connection');

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

function initArrButtons(cfg) {
    var btnContainer = document.getElementById('arrButtons');
    if (!btnContainer) {
        return;
    }
    var h = '';
    var radarrInstances = resolveArrInstances(cfg, 'Radarr').filter(
        function (inst) {
            return inst && inst.Url && inst.ApiKey;
        });

    var sonarrInstances = resolveArrInstances(cfg, 'Sonarr').filter(
        function (inst) {
            return inst && inst.Url && inst.ApiKey;
        });

    if (radarrInstances.length === 0 && sonarrInstances.length === 0) {
        btnContainer.innerHTML = '<div class="no-data-container"><p>' + T(
                'arrNotConfigured',
                'Not configured. Please set URL and API key in Settings.')
            + '</p></div>';
        return;
    }

    if (radarrInstances.length > 0) {
        h += '<div style="margin-bottom:1em;">';
        h += '<h4 style="margin:0 0 0.5em 0;opacity:0.7;">🎬 Radarr</h4>';
        h += '<div class="header-actions" style="flex-wrap:wrap;">';
        for (var r = 0; r < radarrInstances.length; r++) {
            var rName = radarrInstances[r].Name || ('Radarr #' + (r + 1));
            h += '<button class="action-btn arr-compare-btn" data-type="Radarr" data-index="'
                + r + '">' + T('compareWith', 'Compare with') + ' ' + escHtml(rName)
                + '</button>';
        }
        h += '</div></div>';
    }

    if (sonarrInstances.length > 0) {
        h += '<div style="margin-bottom:1em;">';
        h += '<h4 style="margin:0 0 0.5em 0;opacity:0.7;">📺 Sonarr</h4>';
        h += '<div class="header-actions" style="flex-wrap:wrap;">';
        for (var s = 0; s < sonarrInstances.length; s++) {
            var sName = sonarrInstances[s].Name || ('Sonarr #' + (s + 1));
            h += '<button class="action-btn arr-compare-btn" data-type="Sonarr" data-index="'
                + s + '">' + T('compareWith', 'Compare with') + ' ' + escHtml(sName)
                + '</button>';
        }
        h += '</div></div>';
    }

    btnContainer.innerHTML = h;

    // Use onclick assignment (not addEventListener) to prevent handler stacking on re-bind
    var compareBtns = btnContainer.querySelectorAll('.arr-compare-btn');
    for (var i = 0; i < compareBtns.length; i++) {
        compareBtns[i].onclick = function () {
            var type = this.getAttribute('data-type');
            var idx = parseInt(this.getAttribute('data-index'), 10);
            var label = this.textContent;
            compareArr(type, idx, label);
        };
    }
}

// Render a single Arr comparison section (list with max 50 items and "and X more" hint)
function renderArrSection(icon, titleKey, titleFallback, items) {
    items = Array.isArray(items) ? items : [];
    var h = '<div class="arr-section"><h4>' + icon + ' ' + T(titleKey,
            titleFallback) + ' — <span class="arr-count">' + items.length
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
            var instanceLabel = label ? label.replace(
                T('compareWith', 'Compare with') + ' ', '') : type;
            var h = '<h3 style="margin-bottom:0.8em;">' + escHtml(instanceLabel)
                + '</h3>';
            h += renderArrSection('✅', 'inBoth', 'In Both', data.InBoth);
            h += renderArrSection('📦', 'inArrOnly', 'In Arr Only (with file)',
                data.InArrOnly);
            h += renderArrSection('⚠️', 'inArrOnlyMissing', 'In Arr Only (no file)',
                data.InArrOnlyMissing);
            h += renderArrSection('🔍', 'inJellyfinOnly', 'In Jellyfin Only',
                data.InJellyfinOnly);
            resultDiv.innerHTML = h;
        }, function () {
            resultDiv.innerHTML = '<div class="error-msg">❌ ' + T('arrCompareError',
                'Failed to compare. Check settings.') + '</div>';
        });
}
