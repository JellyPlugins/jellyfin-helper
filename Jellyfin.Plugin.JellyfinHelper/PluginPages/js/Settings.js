// --- Settings Tab ---

    // Track current language for detecting changes on save
    var _currentLang = '';

    // Track whether trash was enabled when settings were loaded (for deactivation dialog)
    var _wasTrashEnabled = false;

    // Rebuild the entire UI after a language change
    function rebuildUI() {
        applyStaticTranslations();

        var placeholder = document.getElementById('statsPlaceholder');
        var result = document.getElementById('statsResult');
        if (placeholder) placeholder.style.display = 'none';
        if (result) {
            result.innerHTML = renderShell();
            result.style.display = 'block';
        }

        initTabs();
        loadSettings();
        initArrButtons();
        loadLatestStatistics();
        loadTrendData();

        // Switch back to the Settings tab after rebuild
        var settingsBtn = document.querySelector('.tab-btn[data-tab="settings"]');
        if (settingsBtn) settingsBtn.click();
    }

    function loadSettings() {
        var form = document.getElementById('settingsForm');
        if (!form) return;
        var apiClient = ApiClient;
        apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Configuration'), dataType: 'json' }).then(function (cfg) {
            // Remember the current language for change detection
            _currentLang = cfg.Language || 'en';
            // Remember trash state for deactivation dialog
            _wasTrashEnabled = !!cfg.UseTrash;
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
            h += '<input type="number" id="cfgTrashDays" min="0" value="' + (cfg.TrashRetentionDays != null ? cfg.TrashRetentionDays : 30) + '">';
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
            attachTestHandlers();
            attachAddHandlers();
        }, function () {
            form.innerHTML = '<div class="error-msg">' + T('settingsLoadError', 'Failed to load settings.') + '</div>';
        });
    }

    function buildSettingsPayload() {
        var radarrInstances = collectArrInstances('Radarr');
        var sonarrInstances = collectArrInstances('Sonarr');
        return {
            IncludedLibraries: document.getElementById('cfgIncluded').value,
            ExcludedLibraries: document.getElementById('cfgExcluded').value,
            OrphanMinAgeDays: parseInt(document.getElementById('cfgOrphanAge').value, 10) || 0,
            TrickplayTaskMode: document.getElementById('cfgTrickplayMode').value,
            EmptyMediaFolderTaskMode: document.getElementById('cfgEmptyFolderMode').value,
            OrphanedSubtitleTaskMode: document.getElementById('cfgSubtitleMode').value,
            StrmRepairTaskMode: document.getElementById('cfgStrmMode').value,
            UseTrash: document.getElementById('cfgTrash').checked,
            TrashFolderPath: document.getElementById('cfgTrashPath').value,
            TrashRetentionDays: (function () { var v = parseInt(document.getElementById('cfgTrashDays').value, 10); return isNaN(v) || v < 0 ? 30 : v; })(),
            Language: document.getElementById('cfgLang').value,
            RadarrUrl: radarrInstances.length > 0 ? radarrInstances[0].Url : '',
            RadarrApiKey: radarrInstances.length > 0 ? radarrInstances[0].ApiKey : '',
            SonarrUrl: sonarrInstances.length > 0 ? sonarrInstances[0].Url : '',
            SonarrApiKey: sonarrInstances.length > 0 ? sonarrInstances[0].ApiKey : '',
            RadarrInstances: radarrInstances,
            SonarrInstances: sonarrInstances
        };
    }

    function doSaveSettings(payload) {
        var btn = document.getElementById('btnSaveSettings');
        var msg = document.getElementById('settingsMsg');
        var apiClient = ApiClient;
        apiClient.ajax({
            type: 'POST', url: apiClient.getUrl('JellyfinHelper/Configuration'),
            data: JSON.stringify(payload), contentType: 'application/json'
        }).then(function () {
            var trashChanged = (!!payload.UseTrash) !== _wasTrashEnabled;
            _wasTrashEnabled = payload.UseTrash;
            var newLang = payload.Language;
            var langChanged = newLang !== _currentLang;
            if (langChanged || trashChanged) {
                _currentLang = newLang;
                btn.disabled = false;
                if (langChanged) {
                    loadTranslations(function () {
                        rebuildUI();
                        var newMsg = document.getElementById('settingsMsg');
                        if (newMsg) newMsg.innerHTML = '<div class="success-msg">✅ ' + T('settingsSaved', 'Settings saved!') + '</div>';
                    });
                } else {
                    rebuildUI();
                    var newMsg = document.getElementById('settingsMsg');
                    if (newMsg) newMsg.innerHTML = '<div class="success-msg">✅ ' + T('settingsSaved', 'Settings saved!') + '</div>';
                }
            } else {
                msg.innerHTML = '<div class="success-msg">✅ ' + T('settingsSaved', 'Settings saved!') + '</div>';
                btn.disabled = false;
                initArrButtons();
                var arrResult = document.getElementById('arrResult');
                if (arrResult) arrResult.innerHTML = '';
            }
        }, function () {
            msg.innerHTML = '<div class="error-msg">❌ ' + T('settingsError', 'Failed to save settings.') + '</div>';
            btn.disabled = false;
        });
    }

    function showTrashDisableDialog(payload) {
        var btn = document.getElementById('btnSaveSettings');
        var apiClient = ApiClient;

        apiClient.ajax({
            type: 'GET', url: apiClient.getUrl('JellyfinHelper/Trash/Folders'), dataType: 'json'
        }).then(function (data) {
            if (!data.Paths || data.Paths.length === 0) {
                doSaveSettings(payload);
                return;
            }

            var pathList = '';
            for (var i = 0; i < data.Paths.length; i++) {
                pathList += '\n  • ' + data.Paths[i];
            }

            var firstMsg = T('trashDisablePrompt', 'Trash is being disabled. The following trash folder(s) exist on disk:')
                + pathList
                + '\n\n' + T('trashDisableQuestion', 'What should happen with these folders?');

            removeTrashDialog();

            var overlay = document.createElement('div');
            overlay.id = 'trashDialogOverlay';
            overlay.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.7);z-index:10000;display:flex;align-items:center;justify-content:center;';

            var dialog = document.createElement('div');
            dialog.style.cssText = 'background:#1c1c1e;border:1px solid rgba(255,255,255,0.15);border-radius:10px;padding:1.5em 2em;max-width:550px;width:90%;color:#fff;font-size:0.95em;';

            var title = document.createElement('h3');
            title.style.cssText = 'margin:0 0 0.8em 0;color:#e74c3c;';
            title.textContent = '🗑️ ' + T('trashDisableTitle', 'Trash Folders Detected');
            dialog.appendChild(title);

            var body = document.createElement('div');
            body.style.cssText = 'white-space:pre-wrap;margin-bottom:1.2em;line-height:1.5;opacity:0.9;';
            body.textContent = firstMsg;
            dialog.appendChild(body);

            var btnRow = document.createElement('div');
            btnRow.style.cssText = 'display:flex;gap:0.8em;justify-content:flex-end;flex-wrap:wrap;';

            var btnKeep = document.createElement('button');
            btnKeep.textContent = '📁 ' + T('trashKeep', 'Keep Folders');
            btnKeep.style.cssText = 'padding:0.5em 1.2em;border:none;border-radius:4px;background:#2ecc71;color:#fff;cursor:pointer;font-size:0.9em;';
            btnKeep.onclick = function () {
                removeTrashDialog();
                doSaveSettings(payload);
            };

            var btnDelete = document.createElement('button');
            btnDelete.textContent = '🗑️ ' + T('trashDelete', 'Delete Folders');
            btnDelete.style.cssText = 'padding:0.5em 1.2em;border:none;border-radius:4px;background:#e74c3c;color:#fff;cursor:pointer;font-size:0.9em;';
            btnDelete.onclick = function () {
                removeTrashDialog();
                showTrashDeleteConfirmation(payload, data.Paths);
            };

            var btnCancel = document.createElement('button');
            btnCancel.textContent = T('cancel', 'Cancel');
            btnCancel.style.cssText = 'padding:0.5em 1.2em;border:1px solid rgba(255,255,255,0.2);border-radius:4px;background:transparent;color:#fff;cursor:pointer;font-size:0.9em;';
            btnCancel.onclick = function () {
                removeTrashDialog();
                var chk = document.getElementById('cfgTrash');
                if (chk) chk.checked = true;
                btn.disabled = false;
            };

            btnRow.appendChild(btnCancel);
            btnRow.appendChild(btnKeep);
            btnRow.appendChild(btnDelete);
            dialog.appendChild(btnRow);
            overlay.appendChild(dialog);
            document.body.appendChild(overlay);
        }, function () {
            doSaveSettings(payload);
        });
    }

    function showTrashDeleteConfirmation(payload, paths) {
        var btn = document.getElementById('btnSaveSettings');
        var msg = document.getElementById('settingsMsg');

        var pathList = '';
        for (var i = 0; i < paths.length; i++) {
            pathList += '\n  • ' + paths[i];
        }

        var overlay = document.createElement('div');
        overlay.id = 'trashDialogOverlay';
        overlay.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.7);z-index:10000;display:flex;align-items:center;justify-content:center;';

        var dialog = document.createElement('div');
        dialog.style.cssText = 'background:#1c1c1e;border:1px solid rgba(255,255,255,0.15);border-radius:10px;padding:1.5em 2em;max-width:550px;width:90%;color:#fff;font-size:0.95em;';

        var title = document.createElement('h3');
        title.style.cssText = 'margin:0 0 0.8em 0;color:#e74c3c;';
        title.textContent = '⚠️ ' + T('trashDeleteConfirmTitle', 'Are you sure?');
        dialog.appendChild(title);

        var body = document.createElement('div');
        body.style.cssText = 'white-space:pre-wrap;margin-bottom:1.2em;line-height:1.5;opacity:0.9;';
        body.textContent = T('trashDeleteConfirmMsg', 'This will permanently delete the following folder(s) and all their contents:')
            + pathList
            + '\n\n' + T('trashDeleteConfirmWarn', 'This action cannot be undone!');
        dialog.appendChild(body);

        var btnRow = document.createElement('div');
        btnRow.style.cssText = 'display:flex;gap:0.8em;justify-content:flex-end;flex-wrap:wrap;';

        var btnCancel = document.createElement('button');
        btnCancel.textContent = T('cancel', 'Cancel');
        btnCancel.style.cssText = 'padding:0.5em 1.2em;border:1px solid rgba(255,255,255,0.2);border-radius:4px;background:transparent;color:#fff;cursor:pointer;font-size:0.9em;';
        btnCancel.onclick = function () {
            removeTrashDialog();
            var chk = document.getElementById('cfgTrash');
            if (chk) chk.checked = true;
            btn.disabled = false;
        };

        var btnOk = document.createElement('button');
        btnOk.textContent = '🗑️ ' + T('trashDeleteConfirmOk', 'Yes, Delete All');
        btnOk.style.cssText = 'padding:0.5em 1.2em;border:none;border-radius:4px;background:#e74c3c;color:#fff;cursor:pointer;font-size:0.9em;';
        btnOk.onclick = function () {
            removeTrashDialog();
            msg.innerHTML = '<div style="opacity:0.6;">🗑️ ' + T('trashDeleting', 'Deleting trash folders…') + '</div>';

            var apiClient = ApiClient;
            apiClient.ajax({
                type: 'DELETE', url: apiClient.getUrl('JellyfinHelper/Trash/Folders'), dataType: 'json'
            }).then(function (result) {
                var summary = '';
                if (result.Deleted && result.Deleted.length > 0) {
                    summary += '✅ ' + T('trashDeletedCount', 'Deleted') + ': ' + result.Deleted.length + ' ' + T('folders', 'folders');
                }
                if (result.Failed && result.Failed.length > 0) {
                    summary += (summary ? ' | ' : '') + '❌ ' + T('trashFailedCount', 'Failed') + ': ' + result.Failed.length;
                }
                if (summary) {
                    msg.innerHTML = '<div class="success-msg">' + summary + '</div>';
                }
                doSaveSettings(payload);
            }, function () {
                msg.innerHTML = '<div class="error-msg">❌ ' + T('trashDeleteError', 'Failed to delete trash folders.') + '</div>';
                btn.disabled = false;
            });
        };

        btnRow.appendChild(btnCancel);
        btnRow.appendChild(btnOk);
        dialog.appendChild(btnRow);
        overlay.appendChild(dialog);
        document.body.appendChild(overlay);
    }

    function removeTrashDialog() {
        var existing = document.getElementById('trashDialogOverlay');
        if (existing) existing.remove();
    }

    function saveSettings() {
        var btn = document.getElementById('btnSaveSettings');
        var msg = document.getElementById('settingsMsg');
        btn.disabled = true;
        msg.innerHTML = '';

        var payload = buildSettingsPayload();

        // Check if trash is being disabled (was enabled, now unchecked)
        if (_wasTrashEnabled && !payload.UseTrash) {
            showTrashDisableDialog(payload);
            return;
        }

        doSaveSettings(payload);
    }