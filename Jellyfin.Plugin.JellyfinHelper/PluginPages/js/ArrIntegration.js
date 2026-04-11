// --- Arr Integration Tab ---

    var MAX_ARR_INSTANCES = 3;

    function renderArrInstances(type, instances) {
        var h = '';
        var count = instances ? instances.length : 0;
        for (var i = 0; i < count; i++) {
            h += renderArrInstanceRow(type, i, instances[i]);
        }
        h += '<div id="' + type + 'AddBtnWrap">';
        h += '<button type="button" class="action-btn" id="btnAdd' + type + '"' +
            (count >= MAX_ARR_INSTANCES ? ' style="display:none;margin-top:0.5em;"' : ' style="margin-top:0.5em;"') +
            '>+ ' + T('addAnother', 'Add another') + ' ' + type + ' ' + T('instance', 'instance') + '</button>';
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
        h += '<div style="margin-top:0.5em;display:flex;align-items:center;gap:0.5em;">';
        h += '<button type="button" class="action-btn btnTestArr" data-type="' + type + '" data-index="' + index + '" style="padding:0.3em 0.8em;font-size:0.85em;background:#3498db;">🔌 ' + T('testConnection', 'Test Connection') + '</button>';
        h += '<span class="arr-test-result" id="' + prefix + '_testResult"></span>';
        h += '</div>';
        h += '</div>';
        return h;
    }

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
        attachRemoveHandlers();
        attachTestHandlers();
        if (newIndex + 1 >= MAX_ARR_INSTANCES) {
            var btn = document.getElementById('btnAdd' + type);
            if (btn) btn.style.display = 'none';
        }
    }

    function removeArrInstance(type, index) {
        var row = document.querySelector('.arr-instance-row[data-type="' + type + '"][data-index="' + index + '"]');
        if (row) row.remove();
        var remaining = document.querySelectorAll('.arr-instance-row[data-type="' + type + '"]');
        for (var i = 0; i < remaining.length; i++) {
            remaining[i].setAttribute('data-index', i);
            var prefix = type + '_' + i;
            var inputs = remaining[i].querySelectorAll('input');
            var suffixes = ['_name', '_url', '_key'];
            for (var j = 0; j < inputs.length && j < suffixes.length; j++) {
                inputs[j].id = prefix + suffixes[j];
            }
            var strong = remaining[i].querySelector('strong');
            if (strong) strong.textContent = type + ' #' + (i + 1);
            var removeBtn = remaining[i].querySelector('.btnRemoveArr');
            if (removeBtn) removeBtn.setAttribute('data-index', i);
            var testBtn = remaining[i].querySelector('.btnTestArr');
            if (testBtn) testBtn.setAttribute('data-index', i);
            var resultSpan = remaining[i].querySelector('.arr-test-result');
            if (resultSpan) resultSpan.id = prefix + '_testResult';
        }
        var btn = document.getElementById('btnAdd' + type);
        if (btn && remaining.length < MAX_ARR_INSTANCES) btn.style.display = '';
    }

    function testArrConnection(type, index) {
        var prefix = type + '_' + index;
        var urlEl = document.getElementById(prefix + '_url');
        var keyEl = document.getElementById(prefix + '_key');
        var resultEl = document.getElementById(prefix + '_testResult');
        if (!urlEl || !keyEl || !resultEl) return;

        var url = urlEl.value.trim();
        var apiKey = keyEl.value.trim();

        if (!url || !apiKey) {
            resultEl.className = 'arr-test-result arr-test-fail';
            resultEl.textContent = '❌ ' + T('testMissingFields', 'URL and API Key are required.');
            return;
        }

        resultEl.className = 'arr-test-result arr-test-pending';
        resultEl.textContent = '⏳ ' + T('testing', 'Testing…');

        var apiClient = ApiClient;
        apiClient.ajax({
            type: 'POST',
            url: apiClient.getUrl('JellyfinHelper/Arr/TestConnection'),
            data: JSON.stringify({ Url: url, ApiKey: apiKey }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (data) {
            if (data.success) {
                resultEl.className = 'arr-test-result arr-test-ok';
                resultEl.textContent = '✅ ' + data.message;
            } else {
                resultEl.className = 'arr-test-result arr-test-fail';
                resultEl.textContent = '❌ ' + data.message;
            }
        }, function () {
            resultEl.className = 'arr-test-result arr-test-fail';
            resultEl.textContent = '❌ ' + T('testConnectionFailed', 'Connection test failed.');
        });
    }

    function attachTestHandlers() {
        var btns = document.querySelectorAll('.btnTestArr');
        for (var i = 0; i < btns.length; i++) {
            btns[i].onclick = function () {
                testArrConnection(this.getAttribute('data-type'), parseInt(this.getAttribute('data-index'), 10));
            };
        }
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
                    h += '<button class="action-btn arr-compare-btn" data-type="Radarr" data-index="' + r + '">' + T('compareWith', 'Compare with') + ' ' + escHtml(rName) + '</button>';
                }
                h += '</div></div>';
            }

            if (sonarrInstances.length > 0) {
                h += '<div style="margin-bottom:1em;">';
                h += '<h4 style="margin:0 0 0.5em 0;opacity:0.7;">📺 Sonarr</h4>';
                h += '<div class="header-actions" style="flex-wrap:wrap;">';
                for (var s = 0; s < sonarrInstances.length; s++) {
                    var sName = sonarrInstances[s].Name || ('Sonarr #' + (s + 1));
                    h += '<button class="action-btn arr-compare-btn" data-type="Sonarr" data-index="' + s + '">' + T('compareWith', 'Compare with') + ' ' + escHtml(sName) + '</button>';
                }
                h += '</div></div>';
            }

            btnContainer.innerHTML = h;

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
            if (data.InArrOnly.length > 0) { h += '<div class="arr-list"><ul>'; for (var b = 0; b < Math.min(data.InArrOnly.length, 50); b++) h += '<li>' + escHtml(data.InArrOnly[b]) + '</li>'; if (data.InArrOnly.length > 50) h += '<li>… ' + T('andMore', 'and') + ' ' + (data.InArrOnly.length - 50) + ' ' + T('more', 'more') + '</li>'; h += '</ul></div>'; }
            h += '</div>';
            h += '<div class="arr-section"><h4>⚠️ ' + T('inArrOnlyMissing', 'In Arr Only (no file)') + ' — <span class="arr-count">' + data.InArrOnlyMissing.length + '</span></h4>';
            if (data.InArrOnlyMissing.length > 0) { h += '<div class="arr-list"><ul>'; for (var c = 0; c < Math.min(data.InArrOnlyMissing.length, 50); c++) h += '<li>' + escHtml(data.InArrOnlyMissing[c]) + '</li>'; if (data.InArrOnlyMissing.length > 50) h += '<li>… ' + T('andMore', 'and') + ' ' + (data.InArrOnlyMissing.length - 50) + ' ' + T('more', 'more') + '</li>'; h += '</ul></div>'; }
            h += '</div>';
            h += '<div class="arr-section"><h4>🔍 ' + T('inJellyfinOnly', 'In Jellyfin Only') + ' — <span class="arr-count">' + data.InJellyfinOnly.length + '</span></h4>';
            if (data.InJellyfinOnly.length > 0) { h += '<div class="arr-list"><ul>'; for (var d = 0; d < Math.min(data.InJellyfinOnly.length, 50); d++) h += '<li>' + escHtml(data.InJellyfinOnly[d]) + '</li>'; if (data.InJellyfinOnly.length > 50) h += '<li>… ' + T('andMore', 'and') + ' ' + (data.InJellyfinOnly.length - 50) + ' ' + T('more', 'more') + '</li>'; h += '</ul></div>'; }
            h += '</div>';
            resultDiv.innerHTML = h;
        }, function () {
            resultDiv.innerHTML = '<div class="error-msg">❌ ' + T('arrCompareError', 'Failed to compare. Check settings.') + '</div>';
        });
    }