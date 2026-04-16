// --- Settings Tab ---

    // Track current language for detecting changes on save
    var _currentLang = '';

    // Track whether trash was enabled when settings were loaded (for deactivation dialog)
    var _wasTrashEnabled = false;

    // Preserve PluginLogLevel across Settings saves (managed in Logs tab)
    var _currentLogLevel = 'INFO';

    // Update Seerr greyed-out UI state based on whether URL+Key are configured
    function updateSeerrUIState(isConfigured) {
        var taskW = document.querySelector('.seerr-task-mode-wrapper');
        if (taskW) { taskW.style.opacity = isConfigured ? '' : '0.5'; taskW.style.pointerEvents = isConfigured ? '' : 'none'; }
        var ageW = document.querySelector('.seerr-age-wrapper');
        if (ageW) { ageW.style.opacity = isConfigured ? '' : '0.5'; ageW.style.pointerEvents = isConfigured ? '' : 'none'; }
        var count = document.getElementById('arrCountSeerr');
        if (count) count.textContent = isConfigured ? '✔' : '';
    }

    // Dirty-tracking: snapshot of settings payload after load/save
    var _settingsSnapshot = '';

    function takeSettingsSnapshot() {
        try { _settingsSnapshot = JSON.stringify(buildSettingsPayload()); } catch (e) { _settingsSnapshot = ''; }
    }

    function hasUnsavedSettings() {
        if (!_settingsSnapshot) return false;
        try { return JSON.stringify(buildSettingsPayload()) !== _settingsSnapshot; } catch (e) { return false; }
    }

    // Show unsaved-changes dialog, then call onProceed() or stay
    function checkUnsavedAndProceed(onProceed) {
        if (!hasUnsavedSettings()) { onProceed(); return; }
        removeDialogById('unsavedDialogOverlay');
        var d = createDialogOverlay(
            'unsavedDialogOverlay',
            '⚠️ ' + T('unsavedChangesTitle', 'Unsaved Changes'),
            '#00a4dc',
            T('unsavedChangesMsg', 'You have unsaved settings changes. What would you like to do?'),
            false
        );
        d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
            removeDialogById('unsavedDialogOverlay');
        }));
        d.btnRow.appendChild(createDialogBtn('🚪 ' + T('discardChanges', 'Discard Changes'), 'danger', function () {
            removeDialogById('unsavedDialogOverlay');
            onProceed();
        }));
        d.btnRow.appendChild(createDialogBtn('💾 ' + T('saveAndContinue', 'Save & Continue'), 'success', function () {
            removeDialogById('unsavedDialogOverlay');
            var payload = buildSettingsPayload();
            doSaveSettings(payload, { onSuccess: onProceed });
        }));
        document.body.appendChild(d.overlay);
    }

    // Browser navigation guard
    window.addEventListener('beforeunload', function (e) {
        if (hasUnsavedSettings()) {
            e.preventDefault();
            e.returnValue = '';
        }
    });

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
            // Remember log level so Settings save doesn't reset it
            _currentLogLevel = cfg.PluginLogLevel || 'INFO';
            // Remember trash state for deactivation dialog
            _wasTrashEnabled = !!cfg.UseTrash;
            var h = '';
            h += '<div class="section-title">' + T('settingsGeneralTitle', 'General settings') + '</div>';

            h += '<label>' + T('includedLibraries', 'Included Libraries (whitelist, comma-separated)') + '</label>';
            h += '<input type="text" id="cfgIncluded" value="' + escAttr(cfg.IncludedLibraries || '') + '">';
            h += '<div class="help-text">' + T('includedLibrariesHelp', 'Leave empty to include all libraries.') + '</div>';

            h += '<label>' + T('excludedLibraries', 'Excluded Libraries (blacklist, comma-separated)') + '</label>';
            h += '<input type="text" id="cfgExcluded" value="' + escAttr(cfg.ExcludedLibraries || '') + '">';

            h += '<label>' + T('orphanMinAgeDays', 'Orphan Minimum Age (days)') + '</label>';
            h += '<input type="number" id="cfgOrphanAge" min="0" value="' + (cfg.OrphanMinAgeDays || 0) + '">';
            h += '<div class="help-text">' + T('orphanMinAgeDaysHelp', 'Items younger than this are protected from deletion.') + '</div>';

            h += '<label>' + T('language', 'Dashboard Language') + '</label>';
            h += '<select id="cfgLang">';
            var langs = [['en','English'],['de','Deutsch'],['fr','Français'],['es','Español'],['pt','Português'],['zh','中文'],['tr','Türkçe']];
            for (var i = 0; i < langs.length; i++) {
                h += '<option value="' + langs[i][0] + '"' + (cfg.Language === langs[i][0] ? ' selected' : '') + '>' + langs[i][1] + '</option>';
            }
            h += '</select>';

            h += '<div class="section-title">' + T('settingsTaskTitle', 'Task settings') + '</div>';
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
            h += renderTaskModeSelect('cfgLinkMode', T('linkRepair', 'Link Repair'), cfg.LinkRepairTaskMode || 'DryRun');

            // Seerr Cleanup task mode - greyed out if not configured
            var seerrConfigured = !!(cfg.SeerrUrl && cfg.SeerrApiKey);
            h += '<div class="seerr-task-mode-wrapper" style="' + (!seerrConfigured ? 'opacity:0.5;pointer-events:none;' : '') + '">';
            h += renderTaskModeSelect('cfgSeerrMode', '<svg viewBox="0 0 24 24" width="16" height="16" style="vertical-align:middle;margin-right:4px;"><ellipse cx="12" cy="12" rx="10" ry="6" fill="none" stroke="currentColor" stroke-width="2"/><circle cx="12" cy="12" r="3.5" fill="currentColor"/></svg> ' + T('seerrCleanup', 'Seerr Cleanup'), cfg.SeerrCleanupTaskMode || 'Deactivate');
            if (!seerrConfigured) h += '<div class="help-text">⚠️ ' + T('seerrNotConfigured', 'Configure Seerr below to enable this task.') + '</div>';
            h += '</div>';

            h += '<div class="section-title">' + T('settingsTrashTitle', 'Trash settings') + '</div>';
            h += '<div class="checkbox-row"><input type="checkbox" id="cfgTrash"' + (cfg.UseTrash ? ' checked' : '') + '><label for="cfgTrash">' + T('useTrash', 'Use Trash (Recycle Bin)') + '</label></div>';
            
            h += '<label>' + T('trashFolder', 'Trash Folder Path') + '</label>';
            h += '<input type="text" id="cfgTrashPath" value="' + escAttr(cfg.TrashFolderPath || '.jellyfin-trash') + '">';
            
            h += '<label>' + T('trashRetention', 'Trash Retention (days)') + '</label>';
            h += '<input type="number" id="cfgTrashDays" min="0" value="' + (cfg.TrashRetentionDays != null ? cfg.TrashRetentionDays : 30) + '">';

            // --- Seerr Instance ---
            h += '<div class="section-title">' + T('settingsSeerrTitle', 'Seerr settings') + '</div>';
            h += '<div class="help-text">' + T('settingsSeerrHelp', 'Connect to Jellyseerr, Overseerr, or Seerr to automatically clean up old media requests.') + '</div>';
            var seerrHasCfg = !!(cfg.SeerrUrl && cfg.SeerrApiKey);
            h += '<div class="arr-collapsible' + (!seerrHasCfg ? ' arr-expanded' : '') + '" id="arrCollapsibleSeerr">';
            h += '<button type="button" class="arr-collapsible-header" aria-expanded="' + (!seerrHasCfg ? 'true' : 'false') + '" onclick="var p=this.parentElement;p.classList.toggle(\'arr-expanded\');this.setAttribute(\'aria-expanded\',p.classList.contains(\'arr-expanded\'))">';
            h += '<span><span class="arr-chevron">▶</span><span class="arr-section-label"><svg viewBox="0 0 24 24" width="16" height="16" style="vertical-align:middle;margin-right:4px;"><ellipse cx="12" cy="12" rx="10" ry="6" fill="none" stroke="currentColor" stroke-width="2"/><circle cx="12" cy="12" r="3.5" fill="currentColor"/></svg> ' + T('seerrInstance', 'Seerr Instance') + '</span><span class="arr-instance-count" id="arrCountSeerr">' + (seerrHasCfg ? '✔' : '') + '</span></span>';
            h += '<span class="help-text" style="margin:0;">' + T('clickToExpand', 'click to expand') + '</span>';
            h += '</button>';
            h += '<div class="arr-collapsible-body">';
            h += '<label>' + T('seerrUrl', 'Seerr URL') + '</label>';
            h += '<input type="text" id="cfgSeerrUrl" value="' + escAttr(cfg.SeerrUrl || '') + '" placeholder="http://localhost:5055">';
            h += '<label>' + T('seerrApiKey', 'Seerr API Key') + '</label>';
            h += '<input type="text" id="cfgSeerrApiKey" value="' + escAttr(cfg.SeerrApiKey || '') + '">';
            h += '<div class="seerr-age-wrapper" style="' + (!seerrHasCfg ? 'opacity:0.5;pointer-events:none;' : '') + '">';
            h += '<label>' + T('seerrCleanupAgeDays', 'Max Request Age (days)') + '</label>';
            h += '<input type="number" id="cfgSeerrAgeDays" min="1" value="' + (cfg.SeerrCleanupAgeDays || 365) + '">';
            h += '<div class="help-text">' + T('seerrCleanupAgeDaysHelp', 'Requests older than this will be deleted. Default: 365 days.') + '</div>';
            h += '</div>';
            h += '<div style="margin-top:0.5em;">';
            h += '<button type="button" class="action-btn btn-arr-test" id="btnTestSeerr" style="padding:0.3em 1em;font-size:0.85em;">🔌 ' + T('testConnection', 'Test Connection') + '</button>';
            h += '</div>';
            h += '</div></div>';

            // --- Radarr Instances ---
            h += '<div class="section-title">' + T('settingsArrTitle', 'Arr stack settings') + '</div>';
            var radarrInstances = cfg.RadarrInstances && cfg.RadarrInstances.length > 0
                ? cfg.RadarrInstances
                : (cfg.RadarrUrl ? [{ Name: 'Radarr', Url: cfg.RadarrUrl, ApiKey: cfg.RadarrApiKey }] : []);
            var radarrCount = radarrInstances.length;
            var radarrCountText = formatInstanceCount(radarrCount);
            h += '<div class="arr-collapsible' + (radarrCount === 0 ? ' arr-expanded' : '') + '" id="arrCollapsibleRadarr">';
            h += '<button type="button" class="arr-collapsible-header" aria-expanded="' + (radarrCount === 0 ? 'true' : 'false') + '" onclick="var p=this.parentElement;p.classList.toggle(\'arr-expanded\');this.setAttribute(\'aria-expanded\',p.classList.contains(\'arr-expanded\'))">';
            h += '<span><span class="arr-chevron">▶</span><span class="arr-section-label">🎬 ' + T('radarrInstances', 'Radarr Instances') + '</span><span class="arr-instance-count" id="arrCountRadarr">' + (radarrCountText ? '(' + radarrCountText + ')' : '') + '</span></span>';
            h += '<span class="help-text" style="margin:0;">' + T('clickToExpand', 'click to expand') + '</span>';
            h += '</button>';
            h += '<div class="arr-collapsible-body">';
            h += renderArrInstances('Radarr', radarrInstances);
            h += '</div></div>';

            // --- Sonarr Instances ---
            var sonarrInstances = cfg.SonarrInstances && cfg.SonarrInstances.length > 0
                ? cfg.SonarrInstances
                : (cfg.SonarrUrl ? [{ Name: 'Sonarr', Url: cfg.SonarrUrl, ApiKey: cfg.SonarrApiKey }] : []);
            var sonarrCount = sonarrInstances.length;
            var sonarrCountText = formatInstanceCount(sonarrCount);
            h += '<div class="arr-collapsible' + (sonarrCount === 0 ? ' arr-expanded' : '') + '" id="arrCollapsibleSonarr">';
            h += '<button type="button" class="arr-collapsible-header" aria-expanded="' + (sonarrCount === 0 ? 'true' : 'false') + '" onclick="var p=this.parentElement;p.classList.toggle(\'arr-expanded\');this.setAttribute(\'aria-expanded\',p.classList.contains(\'arr-expanded\'))">';
            h += '<span><span class="arr-chevron">▶</span><span class="arr-section-label">📺 ' + T('sonarrInstances', 'Sonarr Instances') + '</span><span class="arr-instance-count" id="arrCountSonarr">' + (sonarrCountText ? '(' + sonarrCountText + ')' : '') + '</span></span>';
            h += '<span class="help-text" style="margin:0;">' + T('clickToExpand', 'click to expand') + '</span>';
            h += '</button>';
            h += '<div class="arr-collapsible-body">';
            h += renderArrInstances('Sonarr', sonarrInstances);
            h += '</div></div>';

            h += '<div style="margin-top:2em;"><button class="action-btn" id="btnSaveSettings">' + T('saveSettings', 'Save Settings') + '</button></div>';
            h += '<div id="settingsMsg" style="margin-top:0.5em;"></div>';

            // --- Backup Section ---
            h += '<div class="section-title">💾 ' + T('settingsBackupTitle', 'Backup & Restore') + '</div>';
            h += '<div class="help-text">' + T('settingsBackupHelp', 'Export your settings, Arr integrations, and trend data for backup. Import to restore on a fresh installation.') + '</div>';
            h += '<div style="display:flex;gap:0.8em;flex-wrap:wrap;margin:1em 0;">';
            h += '<button class="action-btn" id="btnBackupExport" style="flex:1;min-width:0;padding:0.5em 1.2em;text-align:center;justify-content:center;">📥 ' + T('backupExport', 'Export Backup') + '</button>';
            h += '<label class="action-btn" id="btnBackupImportLabel" style="flex:1;min-width:0;padding:0.5em 1.2em;cursor:pointer;margin:0;text-align:center;justify-content:center;">📤 ' + T('backupImport', 'Import Backup') + '<input type="file" id="btnBackupImportFile" accept=".json,application/json" style="display:none;"></label>';
            h += '</div>';
            h += '<div id="backupMsg" style="margin-top:0.5em;"></div>';
            form.innerHTML = h;
            document.getElementById('btnSaveSettings').addEventListener('click', saveSettings);
            attachRemoveHandlers();
            attachTestHandlers();
            attachAddHandlers();
            attachBackupHandlers();
            attachSeerrHandlers();
            attachAutoSaveHandlers();

            initArrButtons(cfg);

            // Take snapshot after settings are fully rendered
            setTimeout(takeSettingsSnapshot, 0);
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
            LinkRepairTaskMode: document.getElementById('cfgLinkMode').value,
            SeerrCleanupTaskMode: document.getElementById('cfgSeerrMode') ? document.getElementById('cfgSeerrMode').value : 'Deactivate',
            SeerrUrl: (document.getElementById('cfgSeerrUrl') || {}).value || '',
            SeerrApiKey: (document.getElementById('cfgSeerrApiKey') || {}).value || '',
            SeerrCleanupAgeDays: (function () { var el = document.getElementById('cfgSeerrAgeDays'); var v = el ? parseInt(el.value, 10) : 365; return isNaN(v) || v < 1 ? 365 : v; })(),
            UseTrash: document.getElementById('cfgTrash').checked,
            TrashFolderPath: document.getElementById('cfgTrashPath').value,
            TrashRetentionDays: (function () { var v = parseInt(document.getElementById('cfgTrashDays').value, 10); return isNaN(v) || v < 0 ? 30 : v; })(),
            Language: document.getElementById('cfgLang').value,
            PluginLogLevel: _currentLogLevel,
            RadarrUrl: radarrInstances.length > 0 ? radarrInstances[0].Url : '',
            RadarrApiKey: radarrInstances.length > 0 ? radarrInstances[0].ApiKey : '',
            SonarrUrl: sonarrInstances.length > 0 ? sonarrInstances[0].Url : '',
            SonarrApiKey: sonarrInstances.length > 0 ? sonarrInstances[0].ApiKey : '',
            RadarrInstances: radarrInstances,
            SonarrInstances: sonarrInstances
        };
    }

    /**
     * Save settings to the server.
     * @param {Object} payload - The settings payload from buildSettingsPayload().
     * @param {Object} [options] - Optional. { quiet: true, element: HTMLElement } for auto-save (no button animation, shows ✔/✘ indicator instead).
     */
    function doSaveSettings(payload, options) {
        var quiet = options && options.quiet;
        var indicatorEl = options && options.element;
        var btn = document.getElementById('btnSaveSettings');

        if (!quiet) {
            btn.innerHTML = '<span class="btn-spinner"></span>' + T('savingSettings', 'Saving Settings...');
        }

        var apiClient = ApiClient;
        apiClient.ajax({
            type: 'POST', url: apiClient.getUrl('JellyfinHelper/Configuration'),
            data: JSON.stringify(payload), contentType: 'application/json'
        }).then(function () {
            var trashChanged = (!!payload.UseTrash) !== _wasTrashEnabled;
            _wasTrashEnabled = payload.UseTrash;
            _currentLang = payload.Language;

            // Update snapshot after successful save
            takeSettingsSnapshot();

            if (trashChanged) {
                rebuildUI();
            }

            if (quiet) {
                showAutoSaveIndicator(indicatorEl, true);
            } else {
                btn.innerHTML = '<div style="display: flex; align-items: center"><span class="btn-icon">✔</span>' + T('settingsSaved', 'Settings saved!') + '</div>';
                btn.classList.add('success');
                btn.disabled = false;
                setTimeout(function() {
                    btn.innerHTML = T('saveSettings', 'Save Settings');
                    btn.classList.remove('success');
                }, 3000);
            }

            initArrButtons(payload);
            var arrResult = document.getElementById('arrResult');
            if (arrResult) arrResult.innerHTML = '';

            // Sync Seerr greyed-out state after save (URL/Key may have been cleared)
            updateSeerrUIState(!!(payload.SeerrUrl && payload.SeerrApiKey));

            if (options && typeof options.onSuccess === 'function') {
                options.onSuccess();
            }
        }, function () {
            if (quiet) {
                showAutoSaveIndicator(indicatorEl, false);
            } else {
                btn.disabled = false;
                btn.innerHTML = '<div style="display: flex; align-items: center"><span class="btn-icon">X</span>' + T('settingsError', 'Failed to save settings.') + '</div>';
                btn.classList.add('error');
                setTimeout(function() {
                    btn.innerHTML = T('saveSettings', 'Save Settings');
                    btn.classList.remove('error');
                }, 5000);
            }
        });
    }

    // --- Dialog Helpers ---
    // Creates a modal dialog overlay with title, body, and button row.
    // Returns { overlay, dialog, btnRow } so callers can add buttons.
    function createDialogOverlay(overlayId, titleText, titleColor, bodyContent, bodyUseHtml) {
        var overlay = document.createElement('div');
        overlay.id = overlayId;
        overlay.style.cssText = 'position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.7);z-index:10000;display:flex;align-items:center;justify-content:center;';

        var dialog = document.createElement('div');
        dialog.style.cssText = 'background:#1c1c1e;border:1px solid rgba(255,255,255,0.15);border-radius:10px;padding:1.5em 2em;max-width:550px;width:90%;color:#fff;font-size:0.95em;';

        var title = document.createElement('h3');
        title.style.cssText = 'margin:0 0 0.8em 0;color:' + titleColor + ';';
        title.textContent = titleText;
        dialog.appendChild(title);

        var body = document.createElement('div');
        body.style.cssText = 'white-space:pre-wrap;margin-bottom:1.2em;line-height:1.5;opacity:0.9;';
        if (bodyUseHtml) {
            body.innerHTML = bodyContent;
        } else {
            body.textContent = bodyContent;
        }
        dialog.appendChild(body);

        var btnRow = document.createElement('div');
        btnRow.style.cssText = 'display:flex;gap:0.8em;justify-content:flex-end;flex-wrap:wrap;';
        dialog.appendChild(btnRow);
        overlay.appendChild(dialog);

        return { overlay: overlay, dialog: dialog, btnRow: btnRow };
    }

    // Creates a styled dialog button.
    // style: 'cancel' (transparent), 'danger' (#e74c3c), 'success' (#2ecc71), 'warning' (#00a4dc)
    function createDialogBtn(text, style, onclick) {
        var btn = document.createElement('button');
        btn.textContent = text;
        var bg = style === 'cancel' ? 'transparent' : style === 'danger' ? '#e74c3c' : style === 'success' ? '#2ecc71' : '#00a4dc';
        var border = style === 'cancel' ? '1px solid rgba(255,255,255,0.2)' : 'none';
        btn.style.cssText = 'padding:0.5em 1.2em;border:' + border + ';border-radius:4px;background:' + bg + ';color:#fff;cursor:pointer;font-size:0.9em;';
        btn.onclick = onclick;
        return btn;
    }

    function removeDialogById(id) {
        var existing = document.getElementById(id);
        if (existing) existing.remove();
    }

    function removeTrashDialog() { removeDialogById('trashDialogOverlay'); }

    function formatPathList(paths) {
        var s = '';
        for (var i = 0; i < paths.length; i++) { s += '\n  • ' + paths[i]; }
        return s;
    }

    function showTrashDisableDialog(payload) {
        var saveBtn = document.getElementById('btnSaveSettings');
        var apiClient = ApiClient;

        apiClient.ajax({
            type: 'GET', url: apiClient.getUrl('JellyfinHelper/Trash/Folders'), dataType: 'json'
        }).then(function (data) {
            var paths = data.Paths || [];
            if (paths.length === 0) { doSaveSettings(payload); return; }

            var bodyText = T('trashDisablePrompt', 'Trash is being disabled. The following trash folder(s) exist on disk:')
                + formatPathList(paths)
                + '\n\n' + T('trashDisableQuestion', 'What should happen with these folders?');

            removeTrashDialog();
            var d = createDialogOverlay('trashDialogOverlay', '🗑️ ' + T('trashDisableTitle', 'Trash Folders Detected'), '#e74c3c', bodyText, false);

            d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
                removeTrashDialog();
                var chk = document.getElementById('cfgTrash');
                if (chk) chk.checked = true;
                saveBtn.disabled = false;
            }));
            d.btnRow.appendChild(createDialogBtn('📁 ' + T('trashKeep', 'Keep Folders'), 'success', function () {
                removeTrashDialog();
                doSaveSettings(payload);
            }));
            d.btnRow.appendChild(createDialogBtn('🗑️ ' + T('trashDelete', 'Delete Folders'), 'danger', function () {
                removeTrashDialog();
                showTrashDeleteConfirmation(payload, paths);
            }));

            document.body.appendChild(d.overlay);
        }, function () {
            doSaveSettings(payload);
        });
    }

    function showTrashDeleteConfirmation(payload, paths) {
        var saveBtn = document.getElementById('btnSaveSettings');
        var msg = document.getElementById('settingsMsg');

        var bodyText = T('trashDeleteConfirmMsg', 'This will permanently delete the following folder(s) and all their contents:')
            + formatPathList(paths)
            + '\n\n' + T('trashDeleteConfirmWarn', 'This action cannot be undone!');

        var d = createDialogOverlay('trashDialogOverlay', '⚠️ ' + T('trashDeleteConfirmTitle', 'Are you sure?'), '#e74c3c', bodyText, false);

        d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
            removeTrashDialog();
            var chk = document.getElementById('cfgTrash');
            if (chk) chk.checked = true;
            saveBtn.disabled = false;
        }));
        d.btnRow.appendChild(createDialogBtn('🗑️ ' + T('trashDeleteConfirmOk', 'Yes, Delete All'), 'danger', function () {
            removeTrashDialog();
            msg.innerHTML = '<div style="opacity:0.6;">🗑️ ' + T('trashDeleting', 'Deleting trash folders…') + '</div>';

            var apiClient = ApiClient;
            apiClient.ajax({
                type: 'DELETE', url: apiClient.getUrl('JellyfinHelper/Trash/Folders'), dataType: 'json'
            }).then(function (result) {
                var summary = '';
                if (result.deleted > 0) {
                    summary += '✅ ' + T('trashDeletedCount', 'Deleted') + ': ' + result.deleted + ' ' + T('folders', 'folders');
                }
                if (result.failed > 0) {
                    summary += (summary ? ' | ' : '') + '❌ ' + T('trashFailedCount', 'Failed') + ': ' + result.failed;
                }
                if (summary) {
                    msg.innerHTML = '<div class="success-msg">' + summary + '</div>';
                }
                doSaveSettings(payload);
            }, function () {
                msg.innerHTML = '<div class="error-msg">❌ ' + T('trashDeleteError', 'Failed to delete trash folders.') + '</div>';
                saveBtn.disabled = false;
            });
        }));

        document.body.appendChild(d.overlay);
    }

    function attachBackupHandlers() {
        var btnExport = document.getElementById('btnBackupExport');
        if (btnExport) {
            btnExport.addEventListener('click', function () {
                triggerBackupExport();
            });
        }
        var fileInput = document.getElementById('btnBackupImportFile');
        if (fileInput) {
            fileInput.addEventListener('change', function () {
                if (this.files && this.files.length > 0) {
                    triggerBackupImport(this.files[0]);
                    this.value = ''; // Reset so same file can be re-selected
                }
            });
        }
    }

    function triggerBackupExport() {
        var btn = document.getElementById('btnBackupExport');
        var msg = document.getElementById('backupMsg');
        btn.disabled = true;
        msg.innerHTML = '';

        var apiClient = ApiClient;
        var url = apiClient.getUrl('JellyfinHelper/Backup/Export');

        apiClient.ajax({ type: 'GET', url: url, dataType: 'text' }).then(function (data) {
            var content = typeof data === 'string' ? data : JSON.stringify(data, null, 2);
            var blob = new Blob([content], { type: 'application/json' });
            var blobUrl = URL.createObjectURL(blob);
            var link = document.createElement('a');
            link.href = blobUrl;
            var timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
            link.download = 'jellyfin-helper-backup-' + timestamp + '.json';
            document.body.appendChild(link);
            link.click();
            document.body.removeChild(link);
            setTimeout(function () { URL.revokeObjectURL(blobUrl); }, 5000);

            msg.innerHTML = '<div class="success-msg">✅ ' + T('backupExportSuccess', 'Backup exported successfully.') + '</div>';
            btn.disabled = false;
            setTimeout(function () { msg.innerHTML = ''; }, 5000);
        }, function (err) {
            var errorText = T('backupExportError', 'Failed to export backup.');
            var response = err && (err.responseJSON || err);
            if (response && response.message) {
                errorText = escHtml(response.message);
            }

            msg.innerHTML = '<div class="error-msg">❌ ' + errorText + '</div>';
            btn.disabled = false;
        });
    }

    function triggerBackupImport(file) {
        var msg = document.getElementById('backupMsg');

        // Client-side size check (10 MB)
        if (file.size > 10 * 1024 * 1024) {
            msg.innerHTML = '<div class="error-msg">❌ ' + T('backupFileTooLarge', 'File too large. Maximum size is 10 MB.') + '</div>';
            return;
        }

        // Show confirmation dialog
        showBackupImportConfirmation(file);
    }

    function showBackupImportConfirmation(file) {
        removeBackupDialog();

        var bodyHtml = '<p>' + T('backupImportConfirmMsg', 'This will overwrite your current settings, Arr integrations, and trend data with the backup data.') + '</p>'
            + '<p><strong>' + T('backupImportConfirmFile', 'File') + ':</strong> ' + escHtml(file.name) + ' (' + formatBytes(file.size) + ')</p>'
            + '<p style="color:#e74c3c;">' + T('backupImportConfirmWarn', 'This action cannot be undone!') + '</p>';

        var d = createDialogOverlay('backupDialogOverlay', '📤 ' + T('backupImportConfirmTitle', 'Import Backup'), '#00a4dc', bodyHtml, true);

        d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () { removeBackupDialog(); }));
        d.btnRow.appendChild(createDialogBtn('📤 ' + T('backupImportConfirmOk', 'Yes, Import'), 'warning', function () {
            removeBackupDialog();
            doBackupImport(file);
        }));

        document.body.appendChild(d.overlay);
    }

    function removeBackupDialog() { removeDialogById('backupDialogOverlay'); }

    function doBackupImport(file) {
        var msg = document.getElementById('backupMsg');
        msg.innerHTML = '<div style="opacity:0.6;">📤 ' + T('backupImporting', 'Importing backup…') + '</div>';

        var reader = new FileReader();
        reader.onload = function (e) {
            var json = e.target.result;

            // Validate it's parsable JSON before sending
            try {
                JSON.parse(json);
            } catch (parseErr) {
                msg.innerHTML = '<div class="error-msg">❌ ' + T('backupInvalidJson', 'Invalid backup file. The file does not contain valid JSON.') + '</div>';
                return;
            }

            var apiClient = ApiClient;
            var url = apiClient.getUrl('JellyfinHelper/Backup/Import');

            apiClient.ajax({
                type: 'POST',
                url: url,
                data: json,
                contentType: 'application/json'
            }).then(function (result) {
                var data = typeof result === 'string' ? JSON.parse(result) : result;
                var summary = data.Summary || data.summary || {};
                var parts = [];
                if (summary.ConfigurationRestored || summary.configurationRestored) parts.push(T('backupConfigRestored', 'Settings'));
                if (summary.TimelineRestored || summary.timelineRestored) parts.push(T('backupTimelineRestored', 'Growth Timeline'));
                if (summary.BaselineRestored || summary.baselineRestored) parts.push(T('backupBaselineRestored', 'Baseline'));

                var successMsg = '✅ ' + T('backupImportSuccess', 'Backup imported successfully.');
                if (parts.length > 0) {
                    successMsg += ' (' + parts.join(', ') + ')';
                }

                // Show warnings if any
                var warnings = data.Warnings || data.warnings || [];
                if (warnings.length > 0) {
                    successMsg += '<br><span style="color:#e67e22;">⚠️ ' + warnings.length + ' ' + T('backupWarnings', 'warning(s)') + ':</span>';
                    for (var i = 0; i < Math.min(warnings.length, 5); i++) {
                        successMsg += '<br><span style="opacity:0.7;font-size:0.85em;">• ' + escHtml(warnings[i]) + '</span>';
                    }
                    if (warnings.length > 5) {
                        successMsg += '<br><span style="opacity:0.5;font-size:0.85em;">' + T('andMore', 'and') + ' ' + (warnings.length - 5) + ' ' + T('more', 'more') + '</span>';
                    }
                }

                msg.innerHTML = '<div class="success-msg">' + successMsg + '</div>';

                // Reload settings to reflect restored configuration (including possibly changed language)
                var scrollContainer = document.querySelector('.mainAnimatedPage') || document.documentElement;
                var savedScroll = scrollContainer.scrollTop;

                setTimeout(function () {
                    ApiClient.ajax({
                        type: 'GET',
                        url: ApiClient.getUrl('JellyfinHelper/Configuration'),
                        dataType: 'json'
                    }).then(function (cfg) {
                        _currentLang = (cfg && cfg.Language) || _currentLang;
                        loadTranslations(function () {
                            rebuildUI();
                            var settingsBtn = document.querySelector('.tab-btn[data-tab="settings"]');
                            if (settingsBtn) settingsBtn.click();
                            setTimeout(function () { scrollContainer.scrollTop = savedScroll; }, 50);
                        });
                    }, function () {
                        // Config load failed — reload with current language
                        loadTranslations(function () {
                            rebuildUI();
                            var settingsBtn = document.querySelector('.tab-btn[data-tab="settings"]');
                            if (settingsBtn) settingsBtn.click();
                            setTimeout(function () { scrollContainer.scrollTop = savedScroll; }, 50);
                        });
                    });
                }, 1500);
            }, function (err) {
                var errorText = T('backupImportError', 'Failed to import backup.');
                try {
                    var response = err && (err.responseJSON || (typeof err.responseText === 'string' ? JSON.parse(err.responseText) : null));
                    if (response && response.message) {
                        errorText = escHtml(response.message);
                    }
                } catch (ignored) { /* use default error text */ }
                msg.innerHTML = '<div class="error-msg">❌ ' + errorText + '</div>';
            });
        };
        reader.onerror = function () {
            msg.innerHTML = '<div class="error-msg">❌ ' + T('backupImportError', 'Failed to import backup.') + '</div>';
        };
        reader.readAsText(file);
    }

    function attachSeerrHandlers() {
        var btn = document.getElementById('btnTestSeerr');
        if (!btn) return;
        var _seerrTimer = null;
        btn.addEventListener('click', function () {
            var url = (document.getElementById('cfgSeerrUrl') || {}).value || '';
            var key = (document.getElementById('cfgSeerrApiKey') || {}).value || '';
            var originalHtml = '🔌 ' + T('testConnection', 'Test Connection');

            if (_seerrTimer) { clearTimeout(_seerrTimer); _seerrTimer = null; }

            if (!url || !key) {
                btn.innerHTML = '<span class="btn-icon">X</span>' + T('seerrFillFields', 'Please fill in URL and API Key first.');
                btn.classList.add('error');
                _seerrTimer = setTimeout(function () {
                    btn.innerHTML = originalHtml;
                    btn.classList.remove('error');
                    _seerrTimer = null;
                }, 3000);
                return;
            }
            btn.disabled = true;
            btn.innerHTML = '<span class="btn-spinner"></span>' + T('testing', 'Testing…');
            var apiClient = ApiClient;
            apiClient.ajax({
                type: 'POST',
                url: apiClient.getUrl('JellyfinHelper/Seerr/Test'),
                data: JSON.stringify({ Url: url, ApiKey: key }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (res) {
                btn.disabled = false;
                if (res && res.success) {
                    btn.innerHTML = '<span class="btn-icon">✔</span>' + escHtml(res.message || 'OK');
                    btn.classList.add('success');
                    // Auto-save settings after successful connection test
                    var payload = buildSettingsPayload();
                    doSaveSettings(payload);
                    // Enable previously greyed-out Seerr UI sections
                    updateSeerrUIState(true);
                    _seerrTimer = setTimeout(function () {
                        btn.innerHTML = originalHtml;
                        btn.classList.remove('success');
                        _seerrTimer = null;
                    }, 3000);
                } else {
                    btn.innerHTML = '<span class="btn-icon">X</span>' + escHtml(res.message || 'Failed');
                    btn.classList.add('error');
                    _seerrTimer = setTimeout(function () {
                        btn.innerHTML = originalHtml;
                        btn.classList.remove('error');
                        _seerrTimer = null;
                    }, 5000);
                }
            }, function () {
                btn.disabled = false;
                btn.innerHTML = '<span class="btn-icon">X</span>' + T('connectionFailed', 'Connection failed.');
                btn.classList.add('error');
                _seerrTimer = setTimeout(function () {
                    btn.innerHTML = originalHtml;
                    btn.classList.remove('error');
                    _seerrTimer = null;
                }, 5000);
            });
        });
    }

    /**
     * Attach auto-save change handlers to task-mode dropdowns and language select.
     * Called after the settings form is rendered.
     */
    function attachAutoSaveHandlers() {
        // Task mode dropdowns — auto-save on change
        var taskModeIds = ['cfgTrickplayMode', 'cfgEmptyFolderMode', 'cfgSubtitleMode', 'cfgLinkMode', 'cfgSeerrMode'];
        for (var i = 0; i < taskModeIds.length; i++) {
            (function (id) {
                var el = document.getElementById(id);
                if (!el) return;
                el.addEventListener('change', function () {
                    doSaveSettings(buildSettingsPayload(), { quiet: true, element: el });
                });
            })(taskModeIds[i]);
        }

        // Language dropdown — auto-save + UI rebuild with scroll restore
        var langEl = document.getElementById('cfgLang');
        if (langEl) {
            langEl.addEventListener('change', function () {
                var newLang = langEl.value;
                var scrollContainer = document.querySelector('.mainAnimatedPage') || document.documentElement;
                var savedScroll = scrollContainer.scrollTop;

                doSaveSettings(buildSettingsPayload(), {
                    quiet: true,
                    element: langEl,
                    onSuccess: function () {
                        _currentLang = newLang;
                        loadTranslations(function () {
                            rebuildUI();
                            // Restore scroll position after rebuild settles
                            setTimeout(function () {
                                scrollContainer.scrollTop = savedScroll;
                                // Show indicator on the newly rendered language select
                                var newLangEl = document.getElementById('cfgLang');
                                if (newLangEl) showAutoSaveIndicator(newLangEl, true);
                            }, 50);
                        });
                    }
                });
            });
        }
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
