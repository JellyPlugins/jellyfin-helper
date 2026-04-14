// --- Logs Tab ---

    var _logsAutoRefreshTimer = null;
    var _logsAutoRefreshEnabled = true;
    var _logsLoadSeq = 0;

    function renderLogsTab() {
        var h = '';
        h += '<div class="logs-container">';

        // Toolbar
        h += '<div class="logs-toolbar">';
        h += '<label>' + T('logsLevel', 'Level') + ':</label>';
        h += '<select id="logsLevelFilter">';
        h += '<option value="DEBUG">DEBUG</option>';
        h += '<option value="INFO">INFO</option>';
        h += '<option value="WARN">WARN</option>';
        h += '<option value="ERROR">ERROR</option>';
        h += '</select>';

        h += '<label>' + T('logsSource', 'Source') + ':</label>';
        h += '<input type="text" id="logsSourceFilter" placeholder="' + T('logsSourcePlaceholder', 'e.g. TrickplayCleaner') + '" style="width:150px;">';

        h += '<div class="logs-spacer"></div>';

        h += '<span class="logs-count" id="logsCount"></span>';

        h += '<div class="logs-auto-refresh" id="logsAutoRefreshIndicator">';
        h += '<span class="dot"></span>';
        h += '<span>' + T('logsAutoRefresh', 'Auto-refresh') + '</span>';
        h += '</div>';

        h += '<div class="logs-btn-group">';
        h += '<button class="logs-btn primary" id="btnLogsDownload" title="' + T('logsDownload', 'Download') + '">📥 ' + T('logsDownload', 'Download') + '</button>';
        h += '<button class="logs-btn danger" id="btnLogsClear" title="' + T('logsClear', 'Clear') + '">🗑️ ' + T('logsClear', 'Clear') + '</button>';
        h += '</div>';

        h += '</div>'; // toolbar

        // Table
        h += '<div class="logs-table-wrapper" id="logsTableWrapper">';
        h += '<div class="logs-empty"><div class="logs-empty-icon">📋</div>' + T('logsLoading', 'Loading logs...') + '</div>';
        h += '</div>';

        h += '</div>'; // container
        return h;
    }

    function initLogsTab() {
        var downloadBtn = document.getElementById('btnLogsDownload');
        var clearBtn = document.getElementById('btnLogsClear');
        var levelFilter = document.getElementById('logsLevelFilter');
        var sourceFilter = document.getElementById('logsSourceFilter');

        if (downloadBtn) downloadBtn.addEventListener('click', downloadLogs);
        if (clearBtn) clearBtn.addEventListener('click', clearLogs);
        if (levelFilter) {
            levelFilter.addEventListener('change', function() {
                saveLogLevelToConfig(levelFilter.value);
                loadLogs();
            });
        }
        if (sourceFilter) {
            var debounceTimer = null;
            sourceFilter.addEventListener('input', function() {
                clearTimeout(debounceTimer);
                debounceTimer = setTimeout(loadLogs, 400);
            });
        }

        // Load persisted log level from config, then load logs
        loadLogLevelFromConfig(function() {
            loadLogs();
            startLogsAutoRefresh();
        });
    }

    function loadLogLevelFromConfig(callback) {
        try {
            var apiClient = ApiClient;
            apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Configuration'), dataType: 'json' }).then(function(cfg) {
                var level = cfg.PluginLogLevel || 'INFO';
                var levelFilter = document.getElementById('logsLevelFilter');
                if (levelFilter) levelFilter.value = level;
                if (callback) callback();
            }, function() {
                if (callback) callback();
            });
        } catch (e) {
            if (callback) callback();
        }
    }

    function saveLogLevelToConfig(newLevel) {
        try {
            var apiClient = ApiClient;
            apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Configuration'), dataType: 'json' }).then(function(cfg) {
                cfg.PluginLogLevel = newLevel;
                return apiClient.ajax({
                    type: 'POST',
                    url: apiClient.getUrl('JellyfinHelper/Configuration'),
                    data: JSON.stringify(cfg),
                    contentType: 'application/json'
                });
            }, function() {
                console.warn('Failed to load configuration for log level update');
            });
        } catch (e) {
            console.warn('Failed to save log level', e);
        }
    }

    function destroyLogsTab() {
        stopLogsAutoRefresh();
    }

    function startLogsAutoRefresh() {
        stopLogsAutoRefresh();
        _logsAutoRefreshEnabled = true;
        _logsAutoRefreshTimer = setInterval(function() {
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
        if (!wrapper) return;

        var levelFilter = document.getElementById('logsLevelFilter');
        var sourceFilter = document.getElementById('logsSourceFilter');
        var minLevel = levelFilter ? levelFilter.value : '';
        var source = sourceFilter ? sourceFilter.value.trim() : '';

        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Logs') + '?limit=500';
        if (minLevel) url += '&minLevel=' + encodeURIComponent(minLevel);
        if (source) url += '&source=' + encodeURIComponent(source);

        var requestSeq = ++_logsLoadSeq;
        apiClient.ajax({ type: 'GET', url: url, dataType: 'json' }).then(function(data) {
            if (requestSeq !== _logsLoadSeq) return;
            var entries = data.Entries || [];
            var totalBuffered = data.TotalBuffered || 0;
            var returned = data.Returned || 0;

            if (countEl) {
                countEl.textContent = T('logsCountLabel', '{0} of {1} entries').replace('{0}', returned).replace('{1}', totalBuffered);
            }

            if (entries.length === 0) {
                wrapper.innerHTML = '<div class="logs-empty"><div class="logs-empty-icon">📋</div>' + T('logsEmpty', 'No log entries.') + '</div>';
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
                h += '<td class="col-level ' + levelClass + '">' + escHtml(entry.Level || '') + '</td>';
                h += '<td class="col-source">' + escHtml(entry.Source || '') + '</td>';
                h += '<td class="col-message">' + escHtml(entry.Message || '');
                if (entry.Exception) {
                    h += '<div class="log-exception">' + escHtml(entry.Exception) + '</div>';
                }
                h += '</td>';
                h += '</tr>';
            }

            h += '</tbody></table>';
            wrapper.innerHTML = h;
        }, function() {
            if (requestSeq !== _logsLoadSeq) return;
            wrapper.innerHTML = '<div class="logs-empty"><div class="logs-empty-icon">⚠️</div>' + T('logsLoadError', 'Failed to load logs.') + '</div>';
        });
    }

    function downloadLogs() {
        var btn = document.getElementById('btnLogsDownload');
        if (btn) btn.disabled = true;

        var levelFilter = document.getElementById('logsLevelFilter');
        var sourceFilter = document.getElementById('logsSourceFilter');
        var minLevel = levelFilter ? levelFilter.value : '';
        var source = sourceFilter ? sourceFilter.value.trim() : '';

        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Logs/Download');
        var sep = '?';
        if (minLevel) { url += sep + 'minLevel=' + encodeURIComponent(minLevel); sep = '&'; }
        if (source) { url += sep + 'source=' + encodeURIComponent(source); }

        // Use fetch to get the file as blob and trigger download
        fetch(url, {
            headers: {
                'Authorization': 'MediaBrowser Token="' + apiClient.accessToken() + '"'
            }
        }).then(function(response) {
            if (!response.ok) throw new Error('Download failed');
            return response.blob();
        }).then(function(blob) {
            var a = document.createElement('a');
            var objUrl = URL.createObjectURL(blob);
            a.href = objUrl;
            a.download = 'jellyfin-helper-logs.txt';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(objUrl);
            if (btn) btn.disabled = false;
        }).catch(function() {
            if (btn) btn.disabled = false;
            alert(T('logsDownloadError', 'Failed to download logs.'));
        });
    }

    function clearLogs() {
        if (!confirm(T('logsClearConfirm', 'Are you sure you want to clear all plugin logs?'))) {
            return;
        }

        var apiClient = ApiClient;
        apiClient.ajax({ type: 'DELETE', url: apiClient.getUrl('JellyfinHelper/Logs') }).then(function() {
            loadLogs();
        }, function() {
            alert(T('logsClearError', 'Failed to clear logs.'));
        });
    }

    function formatLogTimestamp(ts) {
        if (!ts) return '';
        try {
            var d = new Date(ts);
            if (isNaN(d.getTime())) return ts;
            var pad = function(n) { return n < 10 ? '0' + n : '' + n; };
            return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' '
                + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
        } catch (e) {
            return ts;
        }
    }

    // escHtml is defined in shared.js
