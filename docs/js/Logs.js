// --- Logs Tab ---

var _logsAutoRefreshTimer = null;
var _logsAutoRefreshEnabled = true;
var _logsLoadSeq = 0;
var _logsTabInitialized = false;
var _logsInitSeq = 0;

function renderLogsTab() {
    var h = '';
    h += '<div class="logs-container">';

    // Toolbar
    h += '<div class="logs-toolbar">';
    h += '<label for="logsLevelFilter">' + T('logsLevel', 'Level') + ':</label>';
    h += '<div>';
    h += '<select id="logsLevelFilter">';
    h += '<option value="DEBUG">DEBUG</option>';
    h += '<option value="INFO">INFO</option>';
    h += '<option value="WARN">WARN</option>';
    h += '<option value="ERROR">ERROR</option>';
    h += '</select>';
    h += '</div>';

    h += '<label for="logsSourceFilter">' + T('logsSource', 'Source') + ':</label>';
    h += '<input type="text" id="logsSourceFilter" placeholder="' + T(
            'logsSourcePlaceholder', 'e.g. TrickplayCleaner')
        + '" style="width:150px;">';

    h += '<div class="logs-spacer"></div>';

    h += '<span class="logs-count" id="logsCount"></span>';

    h += '<div class="logs-auto-refresh" id="logsAutoRefreshIndicator">';
    h += '<span class="dot"></span>';
    h += '<span>' + T('logsAutoRefresh', 'Auto-refresh') + '</span>';
    h += '</div>';

    h += '<div class="logs-btn-group">';
    h += '<button class="logs-btn primary" id="btnLogsDownload" title="' + T(
            'logsDownload', 'Download') + '">📥 ' + T('logsDownload', 'Download')
        + '</button>';
    h += '<button class="logs-btn danger" id="btnLogsClear" title="' + T(
        'logsClear', 'Clear') + '">🗑️ ' + T('logsClear', 'Clear') + '</button>';
    h += '</div>';

    h += '</div>'; // toolbar

    // Table
    h += '<div class="logs-table-wrapper" id="logsTableWrapper">';
    h += '<div class="logs-empty"><div class="logs-empty-icon">📋</div>' + T(
        'logsLoading', 'Loading logs...') + '</div>';
    h += '</div>';

    h += '</div>'; // container
    return h;
}

function initLogsTab() {
    var initSeq = ++_logsInitSeq;

    if (!_logsTabInitialized) {
        var downloadBtn = document.getElementById('btnLogsDownload');
        var clearBtn = document.getElementById('btnLogsClear');
        var levelFilter = document.getElementById('logsLevelFilter');
        var sourceFilter = document.getElementById('logsSourceFilter');

        if (downloadBtn) {
            downloadBtn.addEventListener('click', downloadLogs);
        }
        if (clearBtn) {
            clearBtn.addEventListener('click', clearLogs);
        }
        if (levelFilter) {
            levelFilter.addEventListener('change', function () {
                saveLogLevelToConfig(levelFilter.value);
                loadLogs();
            });
        }
        if (sourceFilter) {
            var debounceTimer = null;
            sourceFilter.addEventListener('input', function () {
                clearTimeout(debounceTimer);
                debounceTimer = setTimeout(loadLogs, 400);
            });
        }
        _logsTabInitialized = true;
    }

    // Load persisted log level from config, then load logs
    loadLogLevelFromConfig(function () {
        // Guard against stale callbacks after tab was destroyed
        if (initSeq !== _logsInitSeq) {
            return;
        }
        loadLogs();
        startLogsAutoRefresh();
    });
}

function loadLogLevelFromConfig(callback) {
    var logLevelFilter;
    // Finding 10: Reuse _currentLogLevel from Settings if already loaded, avoiding a duplicate API call
    if (typeof _currentLogLevel !== 'undefined' && _currentLogLevel) {
        logLevelFilter = document.getElementById('logsLevelFilter');
        if (logLevelFilter) {
            logLevelFilter.value = _currentLogLevel;
        }
        if (callback) {
            callback();
        }
        return;
    }
    try {
        apiGet('JellyfinHelper/Configuration', function (cfg) {
            var level = cfg.PluginLogLevel || 'INFO';
            if (typeof _currentLogLevel !== 'undefined') {
                _currentLogLevel = level;
            }
            var lf = document.getElementById('logsLevelFilter');
            if (lf) {
                lf.value = level;
            }
            if (callback) {
                callback();
            }
        }, function () {
            var lf = document.getElementById('logsLevelFilter');
            if (lf) {
                lf.value = 'INFO';
            }
            if (callback) {
                callback();
            }
        });
    } catch (e) {
        logLevelFilter = document.getElementById('logsLevelFilter');
        if (logLevelFilter) {
            logLevelFilter.value = 'INFO';
        }
        if (callback) {
            callback();
        }
    }
}

function saveLogLevelToConfig(newLevel) {
    try {
        var levelFilter = document.getElementById('logsLevelFilter');
        apiPut('JellyfinHelper/Configuration/LogLevel', {PluginLogLevel: newLevel},
            function () {
                // Update Settings tab safety-net variable if available
                if (typeof _currentLogLevel !== 'undefined') {
                    _currentLogLevel = newLevel;
                }
                showAutoSaveIndicatorOverlay(levelFilter, true);
            }, function () {
                console.warn('Failed to save log level');
                showAutoSaveIndicatorOverlay(levelFilter, false);
            });
    } catch (e) {
        console.warn('Failed to save log level', e);
    }
}

function destroyLogsTab() {
    _logsInitSeq++;
    stopLogsAutoRefresh();
}

function startLogsAutoRefresh() {
    stopLogsAutoRefresh();
    _logsAutoRefreshEnabled = true;
    _logsAutoRefreshTimer = setInterval(function () {
        if (_logsAutoRefreshEnabled && !document.hidden) {
            loadLogs();
        }
    }, 10000); // 10 seconds
}

function stopLogsAutoRefresh() {
    if (_logsAutoRefreshTimer) {
        clearInterval(_logsAutoRefreshTimer);
        _logsAutoRefreshTimer = null;
    }
    _logsAutoRefreshEnabled = false;
}

function loadLogs() {
    var wrapper = document.getElementById('logsTableWrapper');
    var countEl = document.getElementById('logsCount');
    if (!wrapper) {
        return;
    }

    var levelFilter = document.getElementById('logsLevelFilter');
    var sourceFilter = document.getElementById('logsSourceFilter');
    var minLevel = levelFilter ? levelFilter.value : '';
    var source = sourceFilter ? sourceFilter.value.trim() : '';

    var path = 'JellyfinHelper/Logs?limit=500';
    if (minLevel) {
        path += '&minLevel=' + encodeURIComponent(minLevel);
    }
    if (source) {
        path += '&source=' + encodeURIComponent(source);
    }

    var requestSeq = ++_logsLoadSeq;
    apiGet(path, function (data) {
        if (requestSeq !== _logsLoadSeq) {
            return;
        }
        var entries = data.Entries || [];
        var totalBuffered = data.TotalBuffered || 0;
        var returned = data.Returned || 0;

        if (countEl) {
            countEl.textContent = T('logsCountLabel', '{0} of {1} entries').replace(
                '{0}', returned).replace('{1}', totalBuffered);
        }

        if (entries.length === 0) {
            wrapper.innerHTML = '<div class="logs-empty"><div class="logs-empty-icon">📋</div>'
                + T('logsEmpty', 'No log entries.') + '</div>';
            return;
        }

        var h = '<table class="logs-table">';
        h += '<thead><tr>';
        h += '<th class="col-time">' + T('logsTime', 'Time') + '</th>';
        h += '<th class="col-level">' + T('logsLevelCol', 'Level') + '</th>';
        h += '<th class="col-source">' + T('logsSourceCol', 'Source') + '</th>';
        h += '<th class="col-message">' + T('logsMessage', 'Message') + '</th>';
        h += '</tr></thead><tbody>';

        for (var i = 0; i < entries.length; i++) {
            var entry = entries[i];
            var ts = formatLogTimestamp(entry.Timestamp);
            var levelClass = 'log-level-' + (entry.Level || 'INFO');

            h += '<tr>';
            h += '<td class="col-time">' + escHtml(ts) + '</td>';
            h += '<td class="col-level ' + levelClass + '">' + escHtml(
                entry.Level || '') + '</td>';
            h += '<td class="col-source">' + escHtml(entry.Source || '') + '</td>';
            h += '<td class="col-message">' + escHtml(entry.Message || '');
            if (entry.Exception) {
                h += '<div class="log-exception">' + escHtml(entry.Exception)
                    + '</div>';
            }
            h += '</td>';
            h += '</tr>';
        }

        h += '</tbody></table>';
        wrapper.innerHTML = h;
    }, function (err) {
        console.error('JellyfinHelper: Failed to load logs', err);
        if (requestSeq !== _logsLoadSeq) {
            return;
        }
        wrapper.innerHTML = '<div class="logs-empty"><div class="logs-empty-icon">⚠️</div>'
            + T('logsLoadError', 'Failed to load logs.') + '</div>';
    });
}

function downloadLogs() {
    var btn = document.getElementById('btnLogsDownload');
    if (btn) {
        btn.disabled = true;
    }

    var levelFilter = document.getElementById('logsLevelFilter');
    var sourceFilter = document.getElementById('logsSourceFilter');
    var minLevel = levelFilter ? levelFilter.value : '';
    var source = sourceFilter ? sourceFilter.value.trim() : '';

    var path = 'JellyfinHelper/Logs/Download';
    var sep = '?';
    if (minLevel) {
        path += sep + 'minLevel=' + encodeURIComponent(minLevel);
        sep = '&';
    }
    if (source) {
        path += sep + 'source=' + encodeURIComponent(source);
    }

    apiFetchBlob(path, function (blob) {
        var a = document.createElement('a');
        var objUrl = URL.createObjectURL(blob);
        a.href = objUrl;
        a.download = 'jellyfin-helper-logs.txt';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(objUrl);
        if (btn) {
            btn.disabled = false;
        }
    }, function () {
        if (btn) {
            btn.disabled = false;
        }
        // Finding 13: Replace alert() with inline error message for consistent UI
        showButtonFeedback(btn, false,
            T('logsDownloadError', 'Failed to download logs.'),
            '📥 ' + T('logsDownload', 'Download'), 4000);
    });
}

function clearLogs() {
    // Finding 12: Replace native confirm() with custom dialog for consistent UI
    removeDialogById('logsClearDialogOverlay');
    var d = createDialogOverlay(
        'logsClearDialogOverlay',
        '🗑️ ' + T('logsClear', 'Clear Logs'),
        getCssVar('--color-danger', '#e74c3c'),
        T('logsClearConfirm', 'Are you sure you want to clear all plugin logs?'),
        false
    );
    d.btnRow.appendChild(
        createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
            removeDialogById('logsClearDialogOverlay');
        }));
    d.btnRow.appendChild(
        createDialogBtn('🗑️ ' + T('logsClear', 'Clear'), 'danger', function () {
            removeDialogById('logsClearDialogOverlay');
            apiDelete('JellyfinHelper/Logs', function () {
                loadLogs();
            }, function () {
                // Finding 13: Replace alert() with button feedback for consistent UI
                var clearBtn = document.getElementById('btnLogsClear');
                if (clearBtn) {
                    showButtonFeedback(clearBtn, false,
                        T('logsClearError', 'Failed to clear logs.'),
                        '🗑️ ' + T('logsClear', 'Clear'), 4000);
                }
            });
        }));
    document.body.appendChild(d.overlay);
}

function formatLogTimestamp(ts) {
    if (!ts) {
        return '';
    }
    try {
        var d = new Date(ts);
        if (isNaN(d.getTime())) {
            return ts;
        }
        var pad = function (n) {
            return n < 10 ? '0' + n : '' + n;
        };
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(
                d.getDate()) + ' '
            + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(
                d.getSeconds());
    } catch (e) {
        return ts;
    }
}

// escHtml is defined in shared.js
