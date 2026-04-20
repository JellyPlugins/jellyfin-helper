// --- Settings Tab ---

// Track current language for detecting changes on save
var _currentLang = '';

// Track whether trash was enabled when settings were loaded (for deactivation dialog)
var _wasTrashEnabled = false;

// Preserve PluginLogLevel across Settings saves (managed in Logs tab)
var _currentLogLevel = 'INFO';
var _logLevelLoaded = false;

// Update Seerr greyed-out UI state based on whether URL+Key are configured
function updateSeerrUIState(isConfigured) {
    var taskW = document.querySelector('.seerr-task-mode-wrapper');
    if (taskW) {
        taskW.style.opacity = isConfigured ? '' : '0.5';
        taskW.style.pointerEvents = isConfigured ? '' : 'none';
    }
    var ageW = document.querySelector('.seerr-age-wrapper');
    if (ageW) {
        ageW.style.opacity = isConfigured ? '' : '0.5';
        ageW.style.pointerEvents = isConfigured ? '' : 'none';
    }
    var count = document.getElementById('arrCountSeerr');
    if (count) count.textContent = isConfigured ? '✔' : '';
}

// Dirty-tracking: snapshot of settings payload after load/save
var _settingsSnapshot = '';

function takeSettingsSnapshot() {
    try {
        _settingsSnapshot = JSON.stringify(buildSettingsPayload());
    } catch (e) {
        _settingsSnapshot = '';
    }
}

function hasUnsavedSettings() {
    if (!_settingsSnapshot) return false;
    try {
        return JSON.stringify(buildSettingsPayload()) !== _settingsSnapshot;
    } catch (e) {
        return false;
    }
}

// Show unsaved-changes dialog, then call onProceed() or stay
function checkUnsavedAndProceed(onProceed) {
    if (!hasUnsavedSettings()) {
        onProceed();
        return;
    }
    removeDialogById('unsavedDialogOverlay');
    var d = createDialogOverlay(
        'unsavedDialogOverlay',
        '⚠️ ' + T('unsavedChangesTitle', 'Unsaved Changes'),
        getCssVar('--color-primary', '#00a4dc'),
        T('unsavedChangesMsg', 'You have unsaved settings changes. What would you like to do?'),
        false
    );
    d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
        removeDialogById('unsavedDialogOverlay');
    }));
    d.btnRow.appendChild(createDialogBtn('🚪 ' + T('discardChanges', 'Discard Changes'), 'danger', function () {
        removeDialogById('unsavedDialogOverlay');
        _settingsSnapshot = '';
        onProceed();
    }));
    d.btnRow.appendChild(createDialogBtn('💾 ' + T('saveAndContinue', 'Save & Continue'), 'success', function () {
        removeDialogById('unsavedDialogOverlay');
        var payload = buildSettingsPayload();
        doSaveSettings(payload, {onSuccess: onProceed});
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
    apiGet('JellyfinHelper/Configuration', function (cfg) {
        // Remember the current language for change detection
        _currentLang = cfg.Language || 'en';
        // Remember log level so Settings save doesn't reset it
        _currentLogLevel = cfg.PluginLogLevel || 'INFO';
        _logLevelLoaded = true;
        // Remember trash state for deactivation dialog
        _wasTrashEnabled = !!cfg.UseTrash;
        var h = '';
        h += '<div class="section-title">' + T('settingsGeneralTitle', 'General settings') + '</div>';

        h += '<label for="cfgIncluded">' + T('includedLibraries', 'Included Libraries (comma-separated)') + '</label>';
        h += '<input type="text" id="cfgIncluded" value="' + escAttr(cfg.IncludedLibraries || '') + '">';
        h += '<div class="help-text">' + T('includedLibrariesHelp', 'Leave empty to include all libraries.') + '</div>';

        h += '<label for="cfgExcluded">' + T('excludedLibraries', 'Excluded Libraries (comma-separated)') + '</label>';
        h += '<input type="text" id="cfgExcluded" value="' + escAttr(cfg.ExcludedLibraries || '') + '">';

        h += '<label for="cfgOrphanAge">' + T('orphanMinAgeDays', 'Orphan Minimum Age (days)') + '</label>';
        h += '<input type="number" id="cfgOrphanAge" min="0" value="' + (cfg.OrphanMinAgeDays || 0) + '">';
        h += '<div class="help-text">' + T('orphanMinAgeDaysHelp', 'Items younger than this are protected from deletion.') + '</div>';

        h += '<label for="cfgLang">' + T('language', 'Dashboard Language') + '</label>';
        h += '<select id="cfgLang">';
        var langs = [['en', 'English'], ['de', 'Deutsch'], ['fr', 'Français'], ['es', 'Español'], ['pt', 'Português'], ['zh', '中文'], ['tr', 'Türkçe']];
        for (var i = 0; i < langs.length; i++) {
            h += '<option value="' + langs[i][0] + '"' + (cfg.Language === langs[i][0] ? ' selected' : '') + '>' + langs[i][1] + '</option>';
        }
        h += '</select>';

        h += '<div class="section-title">' + T('settingsTaskTitle', 'Task settings') + '</div>';
        h += '<div style="font-weight:600;font-size:0.9em;margin-top:0.5em;">' + T('taskModeTitle', 'Task Mode (per Task)') + '</div>';
        h += '<div class="help-text">' + T('taskModeHelp', 'Choose whether each task is active, runs in dry-run mode (only logs), or is deactivated.') + '</div>';

        var taskModes = [['Activate', T('activate', 'Activate')], ['DryRun', T('dryRun', 'Dry Run')], ['Deactivate', T('deactivate', 'Deactivate')]];

        function renderTaskModeSelect(id, label, currentVal) {
            var s = '<label for="' + id + '">';
            s += label;
            s += '</label><select id="' + id + '">';

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
        h += renderTaskModeSelect('cfgSeerrMode', T('seerrCleanup', 'Seerr Cleanup'), cfg.SeerrCleanupTaskMode || 'Deactivate');
        if (!seerrConfigured) h += '<div class="help-text">⚠️ ' + T('seerrNotConfigured', 'Configure Seerr below to enable this task.') + '</div>';
        h += '</div>';

        h += '<div class="section-title">' + T('settingsTrashTitle', 'Trash settings') + '</div>';
        h += '<div class="checkbox-row"><input type="checkbox" id="cfgTrash"' + (cfg.UseTrash ? ' checked' : '') + '><label for="cfgTrash">' + T('useTrash', 'Use Trash (Recycle Bin)') + '</label></div>';

        h += '<label for="cfgTrashPath">' + T('trashFolder', 'Trash Folder Path') + '</label>';
        h += '<input type="text" id="cfgTrashPath" value="' + escAttr(cfg.TrashFolderPath || '.jellyfin-trash') + '">';

        h += '<label for="cfgTrashDays">' + T('trashRetention', 'Trash Retention (days)') + '</label>';
        h += '<input type="number" id="cfgTrashDays" min="0" value="' + (cfg.TrashRetentionDays != null ? cfg.TrashRetentionDays : 30) + '">';

        function renderArrCollapseButton(expanded, icon, text, countText, type) {
            var arrCollapseButton = '<button type="button" id="arrCollapsibleHeader' + type + '" class="arr-collapsible-header" aria-expanded="' + (expanded ? 'true' : 'false') + '" onclick="var p=this.parentElement;p.classList.toggle(\'arr-expanded\');var ex=p.classList.contains(\'arr-expanded\');this.setAttribute(\'aria-expanded\',ex?\'true\':\'false\');var b=p.querySelector(\'.arr-collapsible-body\');if(b)b.setAttribute(\'aria-hidden\',ex?\'false\':\'true\')">';
            arrCollapseButton += '<span class="arr-chevron">▶</span>' + icon + '<span>' + text + '</span><span class="arr-instance-count" id="arrCount' + type + '">' + countText + '</span>';
            arrCollapseButton += '<span class="help-text">' + T('clickToExpand', 'click to expand') + '</span>';
            arrCollapseButton += '</button>';
            return arrCollapseButton;
        }

        // --- Seerr Instance ---
        h += '<div class="section-title">' + T('settingsSeerrTitle', 'Seerr settings') + '</div>';
        h += '<div class="help-text">' + T('settingsSeerrHelp', 'Connect to Jellyseerr, Overseerr, or Seerr to automatically clean up old media requests.') + '</div>';
        var seerrHasCfg = !!(cfg.SeerrUrl && cfg.SeerrApiKey);
        h += '<div class="arr-collapsible' + (!seerrHasCfg ? ' arr-expanded' : '') + '" id="arrCollapsibleSeerr">';
        h += renderArrCollapseButton(!seerrHasCfg, SVG.EYE, T('seerrInstance', 'Seerr Instance'), seerrHasCfg ? '✔' : '', 'Seerr');
        h += '<div class="arr-collapsible-body" aria-hidden="' + (seerrHasCfg ? 'true' : 'false') + '">';
        h += '<label for="cfgSeerrUrl">' + T('seerrUrl', 'Seerr URL') + '</label>';
        h += '<input type="text" id="cfgSeerrUrl" value="' + escAttr(cfg.SeerrUrl || '') + '" placeholder="http://localhost:5055">';
        h += '<label for="cfgSeerrApiKey">' + T('seerrApiKey', 'Seerr API Key') + '</label>';
        h += '<input type="password" id="cfgSeerrApiKey" value="' + escAttr(cfg.SeerrApiKey || '') + '">';
        h += '<div class="seerr-age-wrapper" style="' + (!seerrHasCfg ? 'opacity:0.5;pointer-events:none;' : '') + '">';
        h += '<label for="cfgSeerrAgeDays">' + T('seerrCleanupAgeDays', 'Max Request Age (days)') + '</label>';
        h += '<input type="number" id="cfgSeerrAgeDays" min="1" max="3650" value="' + (cfg.SeerrCleanupAgeDays || 365) + '">';
        h += '<div class="help-text">' + T('seerrCleanupAgeDaysHelp', 'Requests older than this will be deleted. Default: 365 days.') + '</div>';
        h += '</div>';
        h += '<div style="margin-top:0.5em;">';
        h += '<button type="button" class="action-btn btn-arr-test" id="btnTestSeerr" style="padding:0.3em 1em;font-size:0.85em;">🔌 ' + T('testConnection', 'Test Connection') + '</button>';
        h += '</div>';
        h += '</div></div>';

        // --- Radarr Instances ---
        h += '<div class="section-title">' + T('settingsArrTitle', 'Arr stack settings') + '</div>';
        var radarrInstances = resolveArrInstances(cfg, 'Radarr');
        var radarrCount = radarrInstances.length;
        h += '<div class="arr-collapsible' + (radarrCount === 0 ? ' arr-expanded' : '') + '" id="arrCollapsibleRadarr">';
        h += renderArrCollapseButton(radarrCount === 0, '<span>🎬</span>', T('radarrInstances', 'Radarr Instances'), createArrCountText(radarrCount), 'Radarr');
        h += '<div class="arr-collapsible-body" aria-hidden="' + (radarrCount === 0 ? 'false' : 'true') + '">';
        h += renderArrInstances('Radarr', radarrInstances);
        h += '</div></div>';

        // --- Sonarr Instances ---
        var sonarrInstances = resolveArrInstances(cfg, 'Sonarr');
        var sonarrCount = sonarrInstances.length;
        h += '<div class="arr-collapsible' + (sonarrCount === 0 ? ' arr-expanded' : '') + '" id="arrCollapsibleSonarr">';
        h += renderArrCollapseButton(sonarrCount === 0, '<span>📺</span>', T('sonarrInstances', 'Sonarr Instances'), createArrCountText(sonarrCount), 'Sonarr');
        h += '<div class="arr-collapsible-body" aria-hidden="' + (sonarrCount === 0 ? 'false' : 'true') + '">';
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
        OrphanMinAgeDays: (function () {
            var v = parseInt(document.getElementById('cfgOrphanAge').value, 10);
            return isNaN(v) || v < 0 ? 0 : v;
        })(),
        TrickplayTaskMode: document.getElementById('cfgTrickplayMode').value,
        EmptyMediaFolderTaskMode: document.getElementById('cfgEmptyFolderMode').value,
        OrphanedSubtitleTaskMode: document.getElementById('cfgSubtitleMode').value,
        LinkRepairTaskMode: document.getElementById('cfgLinkMode').value,
        SeerrUrl: (document.getElementById('cfgSeerrUrl') || {}).value || '',
        SeerrApiKey: (document.getElementById('cfgSeerrApiKey') || {}).value || '',
        SeerrCleanupTaskMode: (function () {
            var modeEl = document.getElementById('cfgSeerrMode');
            var url = (document.getElementById('cfgSeerrUrl') || {}).value || '';
            var key = (document.getElementById('cfgSeerrApiKey') || {}).value || '';
            return (url && key && modeEl) ? modeEl.value : 'Deactivate';
        })(),
        SeerrCleanupAgeDays: (function () {
            var el = document.getElementById('cfgSeerrAgeDays');
            var v = el ? parseInt(el.value, 10) : 365;
            return isNaN(v) || v < 1 ? 365 : v;
        })(),
        UseTrash: document.getElementById('cfgTrash').checked,
        TrashFolderPath: document.getElementById('cfgTrashPath').value,
        TrashRetentionDays: (function () {
            var v = parseInt(document.getElementById('cfgTrashDays').value, 10);
            return isNaN(v) || v < 0 ? 30 : v;
        })(),
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

    apiPost('JellyfinHelper/Configuration', payload, function () {
        var trashChanged = (!!payload.UseTrash) !== _wasTrashEnabled;
        _wasTrashEnabled = payload.UseTrash;
        _currentLang = payload.Language;

        // Update snapshot after successful save
        takeSettingsSnapshot();

        if (trashChanged) {
            rebuildUI();
        }

        if (quiet) {
            showAutoSaveIndicatorOverlay(indicatorEl, true);
        } else {
            btn.disabled = false;
            showButtonFeedback(btn, true, T('settingsSaved', 'Settings saved!'), T('saveSettings', 'Save Settings'));
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
            showAutoSaveIndicatorOverlay(indicatorEl, false);
        } else {
            btn.disabled = false;
            showButtonFeedback(btn, false, T('settingsError', 'Failed to save settings.'), T('saveSettings', 'Save Settings'));
        }
    });
}

// Dialog helpers (createDialogOverlay, createDialogBtn, removeDialogById) are now in Shared.js

function removeTrashDialog() {
    removeDialogById('trashDialogOverlay');
}

function formatPathList(paths) {
    var s = '';
    for (var i = 0; i < paths.length; i++) {
        s += '\n  • ' + paths[i];
    }
    return s;
}

function showTrashDisableDialog(payload) {
    var saveBtn = document.getElementById('btnSaveSettings');

    apiGet('JellyfinHelper/Trash/Folders', function (data) {
        var paths = data.Paths || [];
        if (paths.length === 0) {
            doSaveSettings(payload);
            return;
        }

        var bodyText = T('trashDisablePrompt', 'Trash is being disabled. The following trash folder(s) exist on disk:')
            + formatPathList(paths)
            + '\n\n' + T('trashDisableQuestion', 'What should happen with these folders?');

        removeTrashDialog();
        var d = createDialogOverlay('trashDialogOverlay', '🗑️ ' + T('trashDisableTitle', 'Trash Folders Detected'), getCssVar('--color-danger', '#e74c3c'), bodyText, false);

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

    var d = createDialogOverlay('trashDialogOverlay', '⚠️ ' + T('trashDeleteConfirmTitle', 'Are you sure?'), getCssVar('--color-danger', '#e74c3c'), bodyText, false);

    d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
        removeTrashDialog();
        var chk = document.getElementById('cfgTrash');
        if (chk) chk.checked = true;
        saveBtn.disabled = false;
    }));
    d.btnRow.appendChild(createDialogBtn('🗑️ ' + T('trashDeleteConfirmOk', 'Yes, Delete All'), 'danger', function () {
        removeTrashDialog();
        msg.innerHTML = '<div style="opacity:0.6;">🗑️ ' + T('trashDeleting', 'Deleting trash folders…') + '</div>';

        apiDelete('JellyfinHelper/Trash/Folders', function (result) {
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

    apiGetText('JellyfinHelper/Backup/Export', function (data) {
        var content = typeof data === 'string' ? data : JSON.stringify(data, null, 2);
        var blob = new Blob([content], {type: 'application/json'});
        var blobUrl = URL.createObjectURL(blob);
        var link = document.createElement('a');
        link.href = blobUrl;
        var timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
        link.download = 'jellyfin-helper-backup-' + timestamp + '.json';
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        setTimeout(function () {
            URL.revokeObjectURL(blobUrl);
        }, 5000);

        msg.innerHTML = '<div class="success-msg">✅ ' + T('backupExportSuccess', 'Backup exported successfully.') + '</div>';
        btn.disabled = false;
        setTimeout(function () {
            msg.innerHTML = '';
        }, 5000);
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
        + '<p class="color-danger">' + T('backupImportConfirmWarn', 'This action cannot be undone!') + '</p>';

    var d = createDialogOverlay('backupDialogOverlay', '📤 ' + T('backupImportConfirmTitle', 'Import Backup'), getCssVar('--color-primary', '#00a4dc'), bodyHtml, true);

    d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
        removeBackupDialog();
    }));
    d.btnRow.appendChild(createDialogBtn('📤 ' + T('backupImportConfirmOk', 'Yes, Import'), 'warning', function () {
        removeBackupDialog();
        doBackupImport(file);
    }));

    document.body.appendChild(d.overlay);
}

function removeBackupDialog() {
    removeDialogById('backupDialogOverlay');
}

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

        apiPostRaw('JellyfinHelper/Backup/Import', json, 'application/json', function (result) {
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
                successMsg += '<br><span class="color-warning">⚠️ ' + warnings.length + ' ' + T('backupWarnings', 'warning(s)') + ':</span>';
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

            function reloadAfterImport() {
                loadTranslations(function () {
                    rebuildUI();
                    var settingsBtn = document.querySelector('.tab-btn[data-tab="settings"]');
                    if (settingsBtn) settingsBtn.click();
                    setTimeout(function () {
                        scrollContainer.scrollTop = savedScroll;
                    }, 50);
                });
            }

            setTimeout(function () {
                apiGet('JellyfinHelper/Configuration', function (cfg) {
                    _currentLang = (cfg && cfg.Language) || _currentLang;
                    reloadAfterImport();
                }, function () {
                    reloadAfterImport();
                });
            }, 1500);
        }, function (err) {
            var errorText = T('backupImportError', 'Failed to import backup.');
            try {
                var response = err && (err.responseJSON || (typeof err.responseText === 'string' ? JSON.parse(err.responseText) : null));
                if (response && response.message) {
                    errorText = escHtml(response.message);
                }
            } catch (ignored) { /* use default error text */
            }
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

        if (_seerrTimer) {
            clearTimeout(_seerrTimer);
            _seerrTimer = null;
        }

        if (!url || !key) {
            _seerrTimer = showButtonFeedback(btn, false, T('seerrFillFields', 'Please fill in URL and API Key first.'), originalHtml, 3000);
            return;
        }
        btn.disabled = true;
        btn.innerHTML = '<span class="btn-spinner"></span>' + T('testing', 'Testing…');
        apiPost('JellyfinHelper/Seerr/Test', {Url: url, ApiKey: key}, function (res) {
            btn.disabled = false;
            if (res && res.success) {
                _seerrTimer = showButtonFeedback(btn, true, escHtml(res.message || 'OK'), originalHtml);
                // Auto-save settings after successful connection test (quiet to avoid double feedback)
                var payload = buildSettingsPayload();
                doSaveSettings(payload, {quiet: true, element: btn});
                // Enable previously greyed-out Seerr UI sections
                updateSeerrUIState(true);
            } else {
                _seerrTimer = showButtonFeedback(btn, false, escHtml(res.message || 'Failed'), originalHtml);
            }
        }, function () {
            btn.disabled = false;
            _seerrTimer = showButtonFeedback(btn, false, T('testConnectionFailed', 'Connection test failed.'), originalHtml);
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
                doSaveSettings(buildSettingsPayload(), {quiet: true, element: el});
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
                            if (newLangEl) showAutoSaveIndicatorOverlay(newLangEl, true);
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
