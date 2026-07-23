// --- Settings Tab ---
'use strict';

// Track current language for detecting changes on save
var _currentLang = '';

// Track whether trash was enabled when settings were loaded (for deactivation dialog)
var _wasTrashEnabled = false;

// Track the saved trash path for detecting path changes (for relocation dialog)
var _previousTrashPath = '';

// Re-entrancy guard: prevents infinite loop when showTrashPathChangeDialog() calls doSaveSettings()
// which would otherwise re-detect the path change and re-show the dialog.
var _trashPathChangeHandled = false;

// Preserve PluginLogLevel across Settings saves (managed in Logs tab)
var _currentLogLevel = 'INFO';
var _logLevelLoaded = false;

/**
 * Shared predicate: returns true when Seerr URL and API Key are both non-empty after trimming.
 * Used across render, payload construction, and post-save UI sync to ensure a single source of truth.
 * @param {string} url - The Seerr URL value.
 * @param {string} key - The Seerr API Key value.
 * @returns {boolean}
 */
function isSeerrConfigured(url, key) {
    return !!((url || '').trim() && (key || '').trim());
}

// Refresh the Discovery access wrapper UI state based on current form values.
// Extracted to avoid duplicated DOM manipulation in multiple event handlers.
function refreshDiscoveryAccessState() {
    var recsMode = (document.getElementById('cfgRecommendationsMode') || {}).value || '';
    var seerrUrl = (document.getElementById('cfgSeerrUrl') || {}).value || '';
    var seerrKey = (document.getElementById('cfgSeerrApiKey') || {}).value || '';
    var discEnabled = recsMode === 'Activate' && isSeerrConfigured(seerrUrl, seerrKey);

    var wrapper = document.getElementById('discoveryAccessWrapper');
    if (wrapper) {
        wrapper.style.opacity = discEnabled ? '' : '0.5';
        wrapper.style.pointerEvents = discEnabled ? '' : 'none';
    }
    var chk = document.getElementById('cfgDiscoveryUserAccess');
    if (chk) {
        chk.disabled = !discEnabled;
        if (!discEnabled) chk.checked = false;
    }
    var hint = document.querySelector('.discovery-access-disabled-hint');
    if (hint) hint.style.display = discEnabled ? 'none' : '';
    // Keep the setup hint button/panel in sync with the enabled state
    var toggleBtn = document.getElementById('btnToggleDiscoveryHint');
    if (toggleBtn) toggleBtn.style.display = discEnabled ? '' : 'none';
    var setupHint = document.querySelector('.discovery-setup-hint');
    if (setupHint) setupHint.style.display = discEnabled ? '' : 'none';
    if (!discEnabled) {
        var panel = document.getElementById('discoveryHintPanel');
        if (panel) panel.style.display = 'none';
        if (toggleBtn) toggleBtn.setAttribute('aria-expanded', 'false');
    }
    return discEnabled;
}

// Show/hide the Recommendations tab button+content based on TaskMode
function updateRecsTabVisibility(taskMode) {
    var show = taskMode !== 'Deactivate';
    var btn = document.querySelector('.tab-btn[data-tab="recommendations"]');
    var panel = document.getElementById('tab-recommendations');
    if (btn) btn.style.display = show ? '' : 'none';
    if (panel) panel.style.display = show ? '' : 'none';
    // If the hidden tab was active, switch to overview
    if (!show && btn && btn.classList.contains('active')) {
        var overviewBtn = document.querySelector('.tab-btn[data-tab="overview"]');
        if (overviewBtn) overviewBtn.click();
    }
}

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
    if (count) count.innerHTML = isConfigured ? mi('check_circle') : '';
    var hint = document.querySelector('.seerr-not-configured-hint');
    if (hint) hint.style.display = isConfigured ? 'none' : '';
}

// Dirty-tracking: snapshot of settings payload after load/save
var _settingsSnapshot = '';

// True once the user has made at least one change since the form was (re)loaded.
// The save band stays hidden until this becomes true, so a freshly loaded,
// untouched form shows nothing.
var _settingsInteracted = false;

// Timer handles / flags for the floating save band.
var _settingsSavedHideTimer = null; // delayed fade-out of the "saved" confirmation
var _saveBandRevealTimer = null;    // debounced reveal of the "unsaved" prompt
var _saveBandSaving = false;        // true while a manual save is in flight

function takeSettingsSnapshot() {
    try {
        _settingsSnapshot = JSON.stringify(buildSettingsPayload());
    } catch (e) {
        _settingsSnapshot = '';
    }
    // Reflect the (now clean) state in the floating save band.
    refreshSaveBand();
}

function hasUnsavedSettings() {
    if (!_settingsSnapshot) return false;
    try {
        return JSON.stringify(buildSettingsPayload()) !== _settingsSnapshot;
    } catch (e) {
        return false;
    }
}

function getSaveBand() {
    return document.getElementById('settingsSaveBand');
}

function cancelSaveBandReveal() {
    if (_saveBandRevealTimer) {
        clearTimeout(_saveBandRevealTimer);
        _saveBandRevealTimer = null;
    }
}

// State classes (is-saved etc.) must survive the fade-out because they carry the
// CSS rule that hides the Save button. If we removed them synchronously on
// transition to "hidden", the button would flash for one frame while the band
// fades. Cleanup happens via this timer after the CSS transition completes.
var _saveBandHiddenCleanupTimer = null;

// Slightly longer than the 0.28s CSS transition to survive timing jitter.
var _saveBandHideDurationMs = 320;

function cancelSaveBandHiddenCleanup() {
    if (_saveBandHiddenCleanupTimer) {
        clearTimeout(_saveBandHiddenCleanupTimer);
        _saveBandHiddenCleanupTimer = null;
    }
}

/**
 * Renders the floating save band in a specific visual state.
 * @param {'hidden'|'unsaved'|'saving'|'saved'|'error'} kind
 */
function renderSaveBand(kind) {
    var band = getSaveBand();
    if (!band) return;
    var icon = band.querySelector('.settings-save-band-icon');
    var text = band.querySelector('.settings-save-band-text');

    // Cancel a pending fade-out unless we're (re)entering the saved state.
    if (kind !== 'saved' && _settingsSavedHideTimer) {
        clearTimeout(_settingsSavedHideTimer);
        _settingsSavedHideTimer = null;
    }

    // Cancel any pending "unsaved" reveal timer. Every explicit render() call
    // establishes the authoritative visual state; a stale reveal that fires
    // afterwards must not overwrite it. Without this cancel, an 'error' render
    // triggered by a failed quiet-save would be silently replaced by "Unsaved
    // changes" ~600ms later (the delegated dirty-tracker armed the timer on the
    // same keystroke that triggered the save, and the failed save never updated
    // the snapshot, so stillDirty=true when the timer wakes up).
    if (kind !== 'unsaved') {
        cancelSaveBandReveal();
    }

    if (kind === 'hidden') {
        // Start fade-out. Keep the previous state class (and icon/text) until the
        // transition finishes; otherwise the CSS rule that hides the Save button
        // stops applying and the button briefly appears on its own.
        band.classList.remove('is-visible');
        band.setAttribute('aria-hidden', 'true');
        cancelSaveBandHiddenCleanup();
        _saveBandHiddenCleanupTimer = setTimeout(function () {
            _saveBandHiddenCleanupTimer = null;
            // Skip cleanup if a new state made the band visible again in the meantime.
            if (band.classList.contains('is-visible')) return;
            band.classList.remove('is-unsaved', 'is-saving', 'is-saved', 'is-error');
            if (icon) icon.innerHTML = '';
            if (text) text.textContent = '';
        }, _saveBandHideDurationMs);
        return;
    }

    // Transition to a visible state: drop pending cleanup and reset state classes.
    cancelSaveBandHiddenCleanup();
    band.classList.remove('is-unsaved', 'is-saving', 'is-saved', 'is-error');
    band.setAttribute('aria-hidden', 'false');
    band.classList.add('is-visible');

    if (kind === 'unsaved') {
        band.classList.add('is-unsaved');
        if (icon) icon.innerHTML = '<span class="settings-save-band-dot" aria-hidden="true"></span>';
        if (text) text.textContent = T('settingsUnsavedChanges', 'Unsaved changes');
    } else if (kind === 'saving') {
        band.classList.add('is-saving');
        if (icon) icon.innerHTML = '<span class="btn-spinner"></span>';
        if (text) text.textContent = T('savingSettings', 'Saving Settings...');
    } else if (kind === 'saved') {
        band.classList.add('is-saved');
        if (icon) icon.innerHTML = mi('check_circle');
        if (text) text.textContent = T('settingsAllSaved', 'All changes saved');
    } else if (kind === 'error') {
        band.classList.add('is-error');
        if (icon) icon.innerHTML = mi('error');
        if (text) text.textContent = T('settingsError', 'Failed to save settings.');
    }
}

/**
 * Drives the floating save band from the current dirty state.
 *
 * Rules:
 *   • Untouched form / clean & never changed → hidden (transparent).
 *   • Unsaved change → "Unsaved changes" + Save button, revealed after a short
 *     debounce so quick auto-saves flip straight to "saved" without flashing.
 *   • Just saved (after an interaction) → "All changes saved", auto-fades.
 * A manual save in flight (_saveBandSaving) is left untouched; a transient error
 * stays until the next change or save.
 */
function refreshSaveBand() {
    var band = getSaveBand();
    if (!band) return;
    if (_saveBandSaving) return; // don't override an in-progress manual save

    var dirty;
    try { dirty = hasUnsavedSettings(); } catch (_e) { dirty = false; }

    if (!_settingsSnapshot) {
        cancelSaveBandReveal();
        renderSaveBand('hidden');
        return;
    }

    if (dirty) {
        _settingsInteracted = true;
        // Already showing the prompt, or a reveal is already pending → nothing to do.
        if (band.classList.contains('is-unsaved') || _saveBandRevealTimer) return;
        _saveBandRevealTimer = setTimeout(function () {
            _saveBandRevealTimer = null;
            if (_saveBandSaving) return;
            var stillDirty;
            try { stillDirty = hasUnsavedSettings(); } catch (_e) { stillDirty = false; }
            if (stillDirty) renderSaveBand('unsaved');
        }, 600);
        return;
    }

    // Clean.
    cancelSaveBandReveal();
    if (!_settingsInteracted) {
        renderSaveBand('hidden');
        return;
    }
    // Show the confirmation, then fade it out after a short delay.
    renderSaveBand('saved');
    if (_settingsSavedHideTimer) clearTimeout(_settingsSavedHideTimer);
    _settingsSavedHideTimer = setTimeout(function () {
        _settingsSavedHideTimer = null;
        var b = getSaveBand();
        if (b && b.classList.contains('is-saved')) {
            renderSaveBand('hidden');
            _settingsInteracted = false;
        }
    }, 2500);
}


// Debounced dirty-check listener on the settings form. The form element itself
// persists across loadSettings() calls (only innerHTML gets replaced), so we
// remove previously registered listeners before adding new ones to avoid
// stacking handlers on repeated reloads.
//
// Two exclusive detach mechanisms — only one is ever active in a given runtime
// because `typeof AbortController === 'function'` is deterministic per browser:
//   1. Preferred: AbortController (near-universal since ~2020).
//      Stored in _dirtyTrackingController; abort() detaches both listeners atomically.
//   2. Legacy fallback: keep the handler+form pair so removeEventListener() can
//      strip both listeners individually. Stored in _dirtyTrackingHandler/_Form.
//
// Test coverage note: the fallback branch is not covered by automated tests (the
// repo has no JS test framework and modern browsers won't take that branch). The
// invariant we rely on is "runtime picks one branch and stays there", which makes
// interleaving impossible — a runtime that lacks AbortController on load will
// keep lacking it, so mixed-state cleanup is not a real failure mode.
var _dirtyDebounceTimer = null;
var _dirtyTrackingController = null;
var _dirtyTrackingHandler = null;
var _dirtyTrackingForm = null;

function attachDirtyTracking() {
    var form = document.getElementById('settingsForm');
    if (!form) return;

    // Detach previous listeners before wiring new ones. Only one of the two
    // branches below will find non-null state in a given runtime, because
    // typeof AbortController is deterministic — the other branch's tracking
    // fields are guaranteed to still be their initial null.
    if (_dirtyTrackingController && typeof _dirtyTrackingController.abort === 'function') {
        try { _dirtyTrackingController.abort(); } catch (_e) { /* ignore */ }
        _dirtyTrackingController = null;
    }
    if (_dirtyTrackingHandler && _dirtyTrackingForm) {
        try {
            _dirtyTrackingForm.removeEventListener('input', _dirtyTrackingHandler);
            _dirtyTrackingForm.removeEventListener('change', _dirtyTrackingHandler);
        } catch (_e) { /* ignore */ }
        _dirtyTrackingHandler = null;
        _dirtyTrackingForm = null;
    }

    var handler = function () {
        if (_dirtyDebounceTimer) clearTimeout(_dirtyDebounceTimer);
        _dirtyDebounceTimer = setTimeout(refreshSaveBand, 120);
    };

    if (typeof AbortController === 'function') {
        _dirtyTrackingController = new AbortController();
        var opts = { signal: _dirtyTrackingController.signal };
        form.addEventListener('input', handler, opts);
        form.addEventListener('change', handler, opts);
    } else {
        // Legacy fallback: no AbortController → track the handler + form so the
        // next attachDirtyTracking() call can removeEventListener() it, keeping
        // the "one active listener pair per form" invariant intact.
        form.addEventListener('input', handler);
        form.addEventListener('change', handler);
        _dirtyTrackingHandler = handler;
        _dirtyTrackingForm = form;
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
        T('unsavedChangesTitle', 'Unsaved Changes'),
        getCssVar('--color-primary', '#00a4dc'),
        T('unsavedChangesMsg', 'You have unsaved settings changes. What would you like to do?')
    );
    d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
        removeDialogById('unsavedDialogOverlay');
    }));
    d.btnRow.appendChild(createDialogBtn(T('discardChanges', 'Discard Changes'), 'danger', function () {
        removeDialogById('unsavedDialogOverlay');
        _settingsSnapshot = '';
        onProceed();
    }));
    d.btnRow.appendChild(createDialogBtn(T('saveAndContinue', 'Save & Continue'), 'success', function () {
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
    loadInsightsData();

    // Switch back to the Settings tab after rebuild
    var settingsBtn = document.querySelector('.tab-btn[data-tab="settings"]');
    if (settingsBtn) settingsBtn.click();
}


function loadSettings() {
    var form = document.getElementById('settingsForm');
    if (!form) return;
    // Reset save-band state on every (re)load so the band stays hidden until the
    // user makes the first change on the fresh form.
    _settingsInteracted = false;
    _saveBandSaving = false;
    if (_settingsSavedHideTimer) {
        clearTimeout(_settingsSavedHideTimer);
        _settingsSavedHideTimer = null;
    }
    if (_saveBandRevealTimer) {
        clearTimeout(_saveBandRevealTimer);
        _saveBandRevealTimer = null;
    }
    apiGet('JellyfinHelper/Configuration', function (cfg) {
        // Remember the current language for change detection
        _currentLang = cfg.Language || 'en';
        // Remember log level so Settings save doesn't reset it
        _currentLogLevel = cfg.PluginLogLevel || 'INFO';
        _logLevelLoaded = true;
        // Remember trash state for deactivation dialog
        _wasTrashEnabled = !!cfg.UseTrash;
        // Remember trash path for relocation dialog
        _previousTrashPath = cfg.TrashFolderPath || '.jellyfin-trash';
        var h = '';

        // Message area for longer operation feedback (e.g. trash folder actions).
        // The Save button + save-state now live in the floating band rendered at
        // the end of the form (see #settingsSaveBand below).
        h += '<div id="settingsMsg" style="margin-top:0.5em;"></div>';

        // ── Card 1: General ──
        h += '<div class="settings-card">';
        h += '<div class="section-title">' + escHtml(T('settingsGeneralTitle', 'General settings')) + '</div>';

        h += '<label>' + escHtml(T('excludedLibraries', 'Excluded Libraries')) + '</label>';
        h += '<div id="cfgExcludedWrapper" class="library-multiselect-wrapper"></div>';

        h += '<label for="cfgOrphanAge">' + escHtml(T('orphanMinAgeDays', 'Orphan Minimum Age (days)')) + '</label>';
        h += '<input type="number" id="cfgOrphanAge" min="0" max="3650" step="1" value="' + (cfg.OrphanMinAgeDays || 0) + '">';
        h += '<div class="help-text">' + escHtml(T('orphanMinAgeDaysHelp', 'Items younger than this are protected from deletion.')) + '</div>';

        h += '<label for="cfgLang">' + escHtml(T('language', 'Dashboard Language')) + '</label>';
        h += '<select id="cfgLang">';
        var langs = [['en', 'English'], ['de', 'Deutsch'], ['fr', 'Français'], ['es', 'Español'], ['pt', 'Português'], ['zh', '中文'], ['tr', 'Türkçe'], ['sv', 'Svenska']];
        for (var i = 0; i < langs.length; i++) {
            h += '<option value="' + langs[i][0] + '"' + (cfg.Language === langs[i][0] ? ' selected' : '') + '>' + langs[i][1] + '</option>';
        }
        h += '</select>';
        h += '</div>'; // /Card 1 (General)

        // ── Card 2: Task settings (cleanup tasks + trash + recommendations chain) ──
        h += '<div class="settings-card">';
        h += '<div class="section-title">' + escHtml(T('settingsTaskTitle', 'Task settings')) + '</div>';
        h += '<div style="font-weight:600;font-size:0.9em;margin-top:0.5em;">' + escHtml(T('taskModeTitle', 'Task Mode (per Task)')) + '</div>';
        h += '<div class="help-text">' + escHtml(T('taskModeHelp', 'Choose whether each task is active, runs in dry-run mode (only logs), or is deactivated.')) + '</div>';

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

        // Cleanup task selects render in a responsive 2-column grid on wide screens.
        h += '<div class="task-mode-grid">';
        h += '<div class="task-mode-cell">' + renderTaskModeSelect('cfgTrickplayMode', escHtml(T('trickplayFolderCleaner', 'Trickplay Folder Cleaner')), cfg.TrickplayTaskMode || 'DryRun') + '</div>';
        h += '<div class="task-mode-cell">' + renderTaskModeSelect('cfgEmptyFolderMode', escHtml(T('emptyMediaFolderCleaner', 'Empty Media Folder Cleaner')), cfg.EmptyMediaFolderTaskMode || 'DryRun') + '</div>';
        h += '<div class="task-mode-cell">' + renderTaskModeSelect('cfgSubtitleMode', escHtml(T('orphanedSubtitleCleaner', 'Orphaned Subtitle Cleaner')), cfg.OrphanedSubtitleTaskMode || 'DryRun') + '</div>';
        h += '<div class="task-mode-cell">' + renderTaskModeSelect('cfgLinkMode', escHtml(T('linkRepair', 'Link Repair')), cfg.LinkRepairTaskMode || 'DryRun') + '</div>';
        h += '</div>';
        // Recommendations select stays full-width because it is followed by its own toggle+hint block.
        h += renderTaskModeSelect('cfgRecommendationsMode', escHtml(T('recommendations', 'Recommendations')), cfg.RecommendationsTaskMode || 'DryRun');

        // Playlist sync toggle - greyed out if Recommendations is not Activate
        var recsActive = (cfg.RecommendationsTaskMode || 'DryRun') === 'Activate';
        h += '<div class="playlist-sync-wrapper" id="playlistSyncWrapper" style="margin:0.3em 0 0.8em 0;' + (!recsActive ? 'opacity:0.5;pointer-events:none;' : '') + '">';
        h += '<div class="checkbox-row"><input type="checkbox" id="cfgSyncPlaylist"' + (cfg.SyncRecommendationsToPlaylist ? ' checked' : '') + (!recsActive ? ' disabled' : '') + '><label for="cfgSyncPlaylist">' + escHtml(T('syncPlaylistToggle', 'Sync recommendations to Jellyfin playlist')) + '</label></div>';
        h += '<div class="help-text">' + escHtml(T('syncPlaylistHelp', 'Creates a per-user playlist visible in the Jellyfin UI. Updated on each scheduled run.')) + '</div>';
        h += '<div class="help-text playlist-sync-disabled-hint" style="' + (recsActive ? 'display:none;' : '') + '">' + escHtml(T('syncPlaylistDisabledHint', 'Set Recommendations to Activate to enable this option.')) + '</div>';
        h += '</div>';

        // Discovery user access toggle - greyed out if Recommendations deactivated OR Seerr not configured
        var seerrConfigured = isSeerrConfigured(cfg.SeerrUrl, cfg.SeerrApiKey);
        var discoveryEnabled = recsActive && seerrConfigured;
        h += '<div class="discovery-access-wrapper" id="discoveryAccessWrapper" style="margin:0.3em 0 0.8em 0;' + (!discoveryEnabled ? 'opacity:0.5;pointer-events:none;' : '') + '">';
        h += '<div class="checkbox-row"><input type="checkbox" id="cfgDiscoveryUserAccess"' + (discoveryEnabled && cfg.DiscoveryUserAccessEnabled ? ' checked' : '') + (!discoveryEnabled ? ' disabled' : '') + '><label for="cfgDiscoveryUserAccess">' + escHtml(T('discoveryUserAccess', 'Allow users to view Discovery and submit requests')) + '</label></div>';
        h += '<div class="help-text">' + escHtml(T('discoveryUserAccessHelp', 'When enabled, non-admin users can see personalized download suggestions and request media via the Seerr Discovery page.')) + ' <button type="button" class="material-icons" id="btnToggleDiscoveryHint" style="color:#00a4dc;font-size:1em;cursor:pointer;vertical-align:middle;user-select:none;background:none;border:none;padding:0;line-height:1;' + (!discoveryEnabled ? 'display:none;' : '') + '" title="' + escHtml(T('discoverySetupHintTitle', 'Setup Instructions')) + '" aria-label="' + escHtml(T('discoverySetupHintTitle', 'Setup Instructions')) + '">info</button></div>';
        h += '<div class="help-text discovery-access-disabled-hint" style="' + (discoveryEnabled ? 'display:none;' : '') + '">' + escHtml(T('discoveryAccessDisabledHint', 'Requires Recommendations set to Activate and Seerr configured.')) + '</div>';
        // Discovery setup hint — collapsible panel (default: closed)
        h += '<div class="discovery-setup-hint" style="margin:0.3em 0 0;' + (!discoveryEnabled ? 'display:none;' : '') + '">';
        h += '<div id="discoveryHintPanel" style="display:none;margin-top:0.5em;padding:0.7em 1em;background:rgba(0,164,220,0.06);border:1px solid rgba(0,164,220,0.2);border-radius:6px;font-size:0.85em;">';
        h += '<strong>' + escHtml(T('discoverySetupHintTitle', 'Setup Instructions')) + '</strong>';
        h += '<ol style="margin:0.4em 0 0.4em 1.2em;padding:0;line-height:1.7;">';
        h += '<li>' + escHtml(T('discoverySetupHint1', 'Install the following two plugins:')) + ' <a href="https://github.com/IAmParadox27/jellyfin-plugin-file-transformation" target="_blank" rel="noopener" style="color:#00a4dc;">' + escHtml(T('discoverySetupHintFT', 'File Transformation')) + '</a> &amp; <a href="https://github.com/IAmParadox27/jellyfin-plugin-custom-tabs" target="_blank" rel="noopener" style="color:#00a4dc;">' + escHtml(T('discoverySetupHintCT', 'Custom Tabs')) + '</a></li>';
        h += '<li>' + escHtml(T('discoverySetupHint2', 'Then in Custom Tabs plugin settings, add a new tab with:')) + '<br>';
        h += '<span style="opacity:0.7;">' + escHtml(T('discoverySetupHintDisplay', 'Display Text')) + ':</span> <code style="background:rgba(255,255,255,0.08);padding:0.1em 0.4em;border-radius:3px;">' + escHtml(T('discoveryTitle', 'Seerr Discovery')) + '</code><br>';
        h += '<span style="opacity:0.7;">' + escHtml(T('discoverySetupHintHtml', 'HTML Content')) + ':</span></li>';
        h += '</ol>';
        h += '<div style="display:flex;align-items:center;gap:0.5em;">';
        h += '<button type="button" class="action-btn" id="btnCopyDiscoveryHtml" style="padding:0.2em 0.6em;font-size:0.82em;display:inline-flex;align-items:center;gap:0.3em;"><span class="material-icons" style="font-size:1em;">content_copy</span><span>' + escHtml(T('discoveryCopySnippet', 'Copy')) + '</span></button>';
        h += '<code style="background:rgba(0,0,0,0.3);padding:0.3em 0.6em;border-radius:4px;font-size:0.9em;">&lt;div class=&quot;jellyfinhelper discovery&quot;&gt;&lt;/div&gt;</code>';
        h += '</div>';
        h += '<div style="margin-top:0.6em;font-size:0.9em;">' + escHtml(T('discoverySetupHintAlreadyInstalled', 'Plugins already installed?')) + ' <a href="#/configurationpage?name=Custom%20Tabs" style="color:#00a4dc;">' + escHtml(T('discoverySetupHintConfigureLink', 'Configure Custom Tabs →')) + '</a></div>';
        h += '</div></div>';
        h += '</div>';

        // Seerr Cleanup task mode - greyed out if not configured
        h += '<div class="seerr-task-mode-wrapper" style="' + (!seerrConfigured ? 'opacity:0.5;pointer-events:none;' : '') + '">';
        h += renderTaskModeSelect('cfgSeerrMode', escHtml(T('seerrCleanup', 'Seerr Cleanup')), cfg.SeerrCleanupTaskMode || 'Deactivate');
        h += '<div class="help-text seerr-not-configured-hint" style="' + (seerrConfigured ? 'display:none;' : '') + '">' + escHtml(T('seerrNotConfigured', 'Configure Seerr below to enable this task.')) + '</div>';
        h += '</div>';

        // Trash / Recycle Bin lives inside the Task card because it is exclusively
        // used by the cleanup tasks configured above. We keep the original
        // "Trash settings" section-title token (i18n key preserved for tests) but
        // style it as a subgroup divider via the additional class.
        h += '<div class="section-title settings-subgroup-title">' + mi('delete') + escHtml(T('settingsTrashTitle', 'Trash settings')) + '</div>';
        h += '<div class="checkbox-row"><input type="checkbox" id="cfgTrash"' + (cfg.UseTrash ? ' checked' : '') + '><label for="cfgTrash">' + escHtml(T('useTrash', 'Use Trash (Recycle Bin)')) + '</label></div>';

        h += '<fieldset id="trashSettingsWrapper" ' + (!cfg.UseTrash ? 'disabled ' : '') + 'style="border:0;padding:0;margin:0;min-inline-size:0;' + (!cfg.UseTrash ? 'opacity:0.5;' : '') + '">';
        h += '<label for="cfgTrashPath">' + escHtml(T('trashFolder', 'Trash Folder Path')) + '</label>';
        h += '<div style="position:relative;">';
        h += '<input type="text" id="cfgTrashPath" value="' + escAttr(cfg.TrashFolderPath || '.jellyfin-trash') + '" style="padding-right:3em;">';
        h += '<button type="button" id="btnBrowseTrash" style="position:absolute;right:0.6em;top:0;bottom:0;display:flex;align-items:center;cursor:pointer;color:#00a4dc;opacity:0.8;background:none;border:none;padding:0;font-size:1.3em;line-height:1;" title="' + escHtml(T('trashBrowse', 'Browse\u2026')) + '" aria-label="' + escHtml(T('trashBrowse', 'Browse\u2026')) + '">' + mi('folder_open') + '</button>';
        h += '</div>';

        h += '<label for="cfgTrashDays">' + escHtml(T('trashRetention', 'Trash Retention (days)')) + '</label>';
        h += '<div style="position:relative;">';
        h += '<input type="number" id="cfgTrashDays" min="0" max="3650" step="1" value="' + (cfg.TrashRetentionDays != null ? cfg.TrashRetentionDays : 30) + '">';
        h += '</div>';
        h += '</fieldset>';
        h += '</div>'; // /Card 2 (Task settings + Trash)

        // ── Card 3: Integrations (Seerr, Radarr, Sonarr) ──
        h += '<div class="settings-card">';

        function renderArrCollapseButton(expanded, icon, text, countText, type) {
            var arrCollapseButton = '<button type="button" id="arrCollapsibleHeader' + type + '" class="arr-collapsible-header" aria-expanded="' + (expanded ? 'true' : 'false') + '" onclick="var p=this.parentElement;p.classList.toggle(\'arr-expanded\');var ex=p.classList.contains(\'arr-expanded\');this.setAttribute(\'aria-expanded\',ex?\'true\':\'false\');var b=p.querySelector(\'.arr-collapsible-body\');if(b)b.setAttribute(\'aria-hidden\',ex?\'false\':\'true\')">';
            arrCollapseButton += '<span class="arr-chevron">▶</span>' + icon + '<span>' + text + '</span><span class="arr-instance-count" id="arrCount' + type + '">' + countText + '</span>';
            arrCollapseButton += '<span class="help-text">' + escHtml(T('clickToExpand', 'click to expand')) + '</span>';
            arrCollapseButton += '</button>';
            return arrCollapseButton;
        }

        // --- Seerr Instance ---
        h += '<div class="section-title">' + escHtml(T('settingsSeerrTitle', 'Seerr settings')) + '</div>';
        h += '<div class="help-text">' + escHtml(T('settingsSeerrHelp', 'Connect to Jellyseerr, Overseerr, or Seerr to automatically clean up old media requests.')) + '</div>';
        var seerrHasCfg = !!(cfg.SeerrUrl && cfg.SeerrApiKey);
        h += '<div class="arr-collapsible' + (!seerrHasCfg ? ' arr-expanded' : '') + '" id="arrCollapsibleSeerr">';
        h += renderArrCollapseButton(!seerrHasCfg, SVG.EYE, escHtml(T('seerrInstance', 'Seerr Instance')), seerrHasCfg ? mi('check_circle') : '', 'Seerr');
        h += '<div class="arr-collapsible-body" aria-hidden="' + (seerrHasCfg ? 'true' : 'false') + '">';
        h += '<label for="cfgSeerrUrl">' + escHtml(T('seerrUrl', 'Seerr URL')) + '</label>';
        h += '<input type="text" id="cfgSeerrUrl" value="' + escAttr(cfg.SeerrUrl || '') + '" placeholder="http://localhost:5055">';
        h += '<label for="cfgSeerrApiKey">' + escHtml(T('seerrApiKey', 'Seerr API Key')) + '</label>';
        h += '<input type="password" id="cfgSeerrApiKey" value="' + escAttr(cfg.SeerrApiKey || '') + '">';
        h += '<div class="seerr-age-wrapper" style="' + (!seerrHasCfg ? 'opacity:0.5;pointer-events:none;' : '') + '">';
        h += '<label for="cfgSeerrAgeDays">' + escHtml(T('seerrCleanupAgeDays', 'Max Request Age (days)')) + '</label>';
        h += '<input type="number" id="cfgSeerrAgeDays" min="1" max="3650" value="' + (cfg.SeerrCleanupAgeDays || 365) + '">';
        h += '<div class="help-text">' + escHtml(T('seerrCleanupAgeDaysHelp', 'Requests older than this will be deleted. Default: 365 days.')) + '</div>';
        h += '</div>';
        h += '<div style="margin-top:0.5em;">';
        h += '<button type="button" class="action-btn btn-arr-test" id="btnTestSeerr" style="padding:0.3em 1em;font-size:0.85em;">' + mi('extension') + escHtml(T('testConnection', 'Test Connection')) + '</button>';
        h += '</div>';
        h += '</div></div>';

        // --- Radarr Instances ---
        h += '<div class="section-title">' + escHtml(T('settingsArrTitle', 'Arr stack settings')) + '</div>';
        var radarrInstances = resolveArrInstances(cfg, 'Radarr');
        var radarrCount = radarrInstances.length;
        h += '<div class="arr-collapsible' + (radarrCount === 0 ? ' arr-expanded' : '') + '" id="arrCollapsibleRadarr">';
        h += renderArrCollapseButton(radarrCount === 0, mi('movie'), escHtml(T('radarrInstances', 'Radarr Instances')), createArrCountText(radarrCount), 'Radarr');
        h += '<div class="arr-collapsible-body" aria-hidden="' + (radarrCount === 0 ? 'false' : 'true') + '">';
        h += renderArrInstances('Radarr', radarrInstances);
        h += '</div></div>';

        // --- Sonarr Instances ---
        var sonarrInstances = resolveArrInstances(cfg, 'Sonarr');
        var sonarrCount = sonarrInstances.length;
        h += '<div class="arr-collapsible' + (sonarrCount === 0 ? ' arr-expanded' : '') + '" id="arrCollapsibleSonarr">';
        h += renderArrCollapseButton(sonarrCount === 0, mi('tv'), escHtml(T('sonarrInstances', 'Sonarr Instances')), createArrCountText(sonarrCount), 'Sonarr');
        h += '<div class="arr-collapsible-body" aria-hidden="' + (sonarrCount === 0 ? 'false' : 'true') + '">';
        h += renderArrInstances('Sonarr', sonarrInstances);
        h += '</div></div>';
        h += '</div>'; // /Card 3 (Integrations)

        // ── Card 4: Backup & Restore ──
        h += '<div class="settings-card">';
        h += '<div class="section-title">' + escHtml(T('settingsBackupTitle', 'Backup & Restore')) + '</div>';
        h += '<div class="help-text">' + escHtml(T('settingsBackupHelp', 'Export your settings, Arr integrations, and trend data for backup. Import to restore on a fresh installation.')) + '</div>';
        h += '<div class="export-import-button-container">';
        h += '<button class="action-btn export-import-button" id="btnBackupExport">' + mi('download') + escHtml(T('backupExport', 'Export Backup')) + '</button>';
        h += '<button type="button" class="action-btn export-import-button" id="btnBackupImport">' + mi('upload') + escHtml(T('backupImport', 'Import Backup')) + '</button>';
        h += '<input type="file" id="btnBackupImportFile" accept=".json,application/json" style="display:none;">';
        h += '</div>';
        h += '<div id="backupMsg" style="margin-top:0.5em;"></div>';
        h += '</div>'; // /Card 4 (Backup)

        // ── Floating save band (fixed, bottom-centre) ──
        // Always in the viewport regardless of scroll position. Transparent/idle by
        // default; shows "unsaved" (with the Save button), a spinner while saving,
        // "saved" (auto-fades), or an error. The button keeps id=btnSaveSettings so
        // all existing save logic keeps working unchanged.
        h += '<div class="settings-save-band" id="settingsSaveBand" role="status" aria-live="polite" aria-hidden="true">';
        h += '<span class="settings-save-band-status"><span class="settings-save-band-icon" aria-hidden="true"></span><span class="settings-save-band-text"></span></span>';
        h += '<button type="button" class="action-btn settings-save-band-btn" id="btnSaveSettings">' + mi('save') + escHtml(T('saveSettings', 'Save Settings')) + '</button>';
        h += '</div>';

        form.innerHTML = h;
        setArrInstanceApiKeys('Radarr', radarrInstances);
        setArrInstanceApiKeys('Sonarr', sonarrInstances);
        document.getElementById('btnSaveSettings').addEventListener('click', saveSettings);
        attachRemoveHandlers();
        attachTestHandlers();
        attachAddHandlers();
        attachBackupHandlers();
        attachSeerrHandlers();
        attachDiscoveryCopyHandler();
        attachAutoSaveHandlers();
        attachOrphanAgeInputHandler();
        attachTrashPathInputHandler();
        attachTrashDaysInputHandler();
        // Toggle trash settings disabled state when checkbox changes
        var trashChk = document.getElementById('cfgTrash');
        if (trashChk) {
            trashChk.addEventListener('change', function () {
                var trashWrapper = document.getElementById('trashSettingsWrapper');
                if (trashWrapper) {
                    trashWrapper.disabled = !trashChk.checked;
                    trashWrapper.style.opacity = trashChk.checked ? '' : '0.5';
                }
            });
        }
        initFolderBrowser();
        initLibraryMultiSelects(cfg);

        initArrButtons(cfg);

        // Show/hide Recommendations tab based on task mode
        updateRecsTabVisibility(cfg.RecommendationsTaskMode || 'DryRun');

        // Delegated dirty-check on the form: keeps the sticky-toolbar indicator
        // in sync with any DOM edit — including auto-save fields (they trigger the
        // change event which the debounced handler picks up, then re-renders the
        // indicator to "clean" after the snapshot is refreshed by takeSettingsSnapshot).
        attachDirtyTracking();

        // Take snapshot after settings are fully rendered
        setTimeout(takeSettingsSnapshot, 0);
    }, function () {
        form.innerHTML = '<div class="error-msg">' + escHtml(T('settingsLoadError', 'Failed to load settings.')) + '</div>';
    });
}

function buildSettingsPayload() {
    var radarrInstances = collectArrInstances('Radarr');
    var sonarrInstances = collectArrInstances('Sonarr');
    return {
        ExcludedLibraries: getLibraryMultiSelectValue('cfgExcludedWrapper'),
        OrphanMinAgeDays: (function () {
            var v = parseInt(document.getElementById('cfgOrphanAge').value, 10);
            if (isNaN(v) || v < 0) return 0;
            if (v > 3650) return 3650;
            return v;
        })(),
        TrickplayTaskMode: document.getElementById('cfgTrickplayMode').value,
        EmptyMediaFolderTaskMode: document.getElementById('cfgEmptyFolderMode').value,
        OrphanedSubtitleTaskMode: document.getElementById('cfgSubtitleMode').value,
        LinkRepairTaskMode: document.getElementById('cfgLinkMode').value,
        RecommendationsTaskMode: document.getElementById('cfgRecommendationsMode').value,
        SyncRecommendationsToPlaylist: document.getElementById('cfgSyncPlaylist') ? document.getElementById('cfgSyncPlaylist').checked : false,
        SeerrUrl: (document.getElementById('cfgSeerrUrl') || {}).value || '',
        SeerrApiKey: (document.getElementById('cfgSeerrApiKey') || {}).value || '',
        SeerrCleanupTaskMode: (function () {
            var modeEl = document.getElementById('cfgSeerrMode');
            var url = (document.getElementById('cfgSeerrUrl') || {}).value || '';
            var key = (document.getElementById('cfgSeerrApiKey') || {}).value || '';
            return (modeEl && isSeerrConfigured(url, key)) ? modeEl.value : 'Deactivate';
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
            if (isNaN(v) || v < 0) return 30;
            if (v > 3650) return 3650;
            return v;
        })(),
        DiscoveryUserAccessEnabled: (function () {
            var checkbox = document.getElementById('cfgDiscoveryUserAccess');
            if (!checkbox || !checkbox.checked) return false;
            // Force false when prerequisites are not met (Recommendations must be active + Seerr configured).
            // This prevents stale "true" from being persisted when the admin disables recommendations
            // or clears Seerr config while the checkbox was previously enabled.
            var recsMode = (document.getElementById('cfgRecommendationsMode') || {}).value || '';
            var seerrUrl = (document.getElementById('cfgSeerrUrl') || {}).value || '';
            var seerrKey = (document.getElementById('cfgSeerrApiKey') || {}).value || '';
            return recsMode === 'Activate' && isSeerrConfigured(seerrUrl, seerrKey);
        })(),
        Language: document.getElementById('cfgLang').value,
        PluginLogLevel: _currentLogLevel,
        RadarrInstances: radarrInstances,
        SonarrInstances: sonarrInstances
    };
}

/**
 * Save settings to the server.
 * @param {Object} payload - The settings payload from buildSettingsPayload().
 * @param {Object} [options] - Optional. { quiet: true, element: HTMLElement } for auto-save (no button animation, shows / indicator instead).
 */
function doSaveSettings(payload, options) {
    var quiet = options && options.quiet;
    var indicatorEl = options && options.element;
    var btn = document.getElementById('btnSaveSettings');

    // Pre-save validation: reject invalid trash paths before sending to server
    var trashError = validateTrashPath(payload.TrashFolderPath, payload.UseTrash);
    if (trashError) {
        showTrashPathError(trashError);
        if (options && options.onError) options.onError();
        return;
    }
    if (payload.UseTrash && typeof payload.TrashFolderPath === 'string') {
        payload.TrashFolderPath = payload.TrashFolderPath.trim();
    }
    showTrashPathError(null);

    // Intercept: if trash path changed, show relocation dialog before saving.
    // This covers all save paths: explicit Save button, auto-save dropdowns, and "Save & Continue" from unsaved-changes dialog.
    // The _trashPathChangeHandled guard prevents infinite recursion: when showTrashPathChangeDialog()
    // calls back into doSaveSettings() after the user has made their choice, the guard is set to
    // true so we skip this check and proceed with the actual save.
    if (hasTrashPathChanged(payload) && !_trashPathChangeHandled) {
        showTrashPathChangeDialog(payload, options);
        return;
    }
    // Reset the guard after passing the check. This ensures:
    // 1. The guard only suppresses one recursive call (the immediate callback from the dialog).
    // 2. If the user later changes the path again, the dialog will show again as expected.
    _trashPathChangeHandled = false;

    if (!quiet) {
        _saveBandSaving = true;
        if (btn) btn.disabled = true;
        renderSaveBand('saving');
    }

    // PluginLogLevel used to be race-prone here: the Settings form captured it at page load, so a
    // concurrent change from the Logs tab would be silently overwritten on save. That race is now
    // closed on the SERVER (ConfigurationController.ApplyRequestToConfig ignores the field on POST;
    // only PUT /Configuration/LogLevel mutates it). We therefore no longer need a preflight GET —
    // whatever we send here is discarded server-side and the on-disk value is preserved.
    postSettingsPayload(payload, quiet, indicatorEl, btn, options);
}

/**
 * Internal: performs the actual POST once the caller has validated the payload. Extracted from
 * doSaveSettings mainly to keep the trash-path / recursion guards separate from the network call.
 * PluginLogLevel is intentionally NOT rewritten here — the server-side handler
 * (ConfigurationController.ApplyRequestToConfig) ignores that field, so whatever the client sends
 * is preserved server-side. See the block comment in doSaveSettings for the TOCTOU history.
 */
function postSettingsPayload(payload, quiet, indicatorEl, btn, options) {
    apiPut('JellyfinHelper/Configuration', payload, function (response) {
        var trashChanged = (!!payload.UseTrash) !== _wasTrashEnabled;
        _wasTrashEnabled = payload.UseTrash;
        _previousTrashPath = (payload.TrashFolderPath || '.jellyfin-trash').trim();
        _currentLang = payload.Language;

        // Update snapshot after successful save
        takeSettingsSnapshot();

        if (trashChanged) {
            rebuildUI();
        }

        if (quiet) {
            showAutoSaveIndicatorOverlay(indicatorEl, true);
        } else {
            // Clear the in-flight guard first, then let the band reflect the now
            // clean state ("All changes saved", which auto-fades).
            _saveBandSaving = false;
            if (btn) btn.disabled = false;
            refreshSaveBand();
        }

        initArrButtons(payload);
        var arrResult = document.getElementById('arrResult');
        if (arrResult) arrResult.innerHTML = '';

        // Sync Seerr greyed-out state after save (URL/Key may have been cleared)
        updateSeerrUIState(isSeerrConfigured(payload.SeerrUrl, payload.SeerrApiKey));
        // Refresh the Discovery wrapper (depends on Seerr + Recommendations mode)
        refreshDiscoveryAccessState();

        if (options && typeof options.onSuccess === 'function') {
            options.onSuccess();
        }
    }, function (err) {
        // Structured diagnostic: try to give the user (and support) a concrete hint
        // instead of a generic "Failed to save settings." toast. The classification
        // uses HTTP status code and body shape (HTML => proxy/WAF, JSON => our server).
        var diag = describeApiError(err);

        var errorMsg = '';
        // Prefer the server-provided message field (our controllers always emit
        // { message: "..." } on validation / model-binding errors).
        try {
            var errData = err && (err.responseJSON
                || (typeof err.responseText === 'string' && err.responseText.length > 0
                    ? JSON.parse(err.responseText)
                    : null));
            if (errData && errData.message) errorMsg = String(errData.message);
        } catch (_e) { /* body was not JSON (e.g. HTML from a proxy) - fall through */ }

        // Emit a rich console log so users copy/pasting into a GitHub issue give us
        // everything we need in one shot (status, kind, body snippet).
        console.error('[JellyfinHelper] Save configuration failed',
            {status: diag.status, statusText: diag.statusText, kind: diag.kind,
             message: errorMsg, snippet: diag.snippet});

        if (quiet) {
            _saveBandSaving = false;
            showAutoSaveIndicatorOverlay(indicatorEl, false);
            renderSaveBand('error');
            if (options && typeof options.onError === 'function') {
                options.onError(errorMsg);
            }
        } else {
            _saveBandSaving = false;
            if (btn) btn.disabled = false;
            renderSaveBand('error');
            // Override the generic error text with a diagnostic-aware label
            // (server message > proxy/network/auth hint > generic fallback).
            var band = getSaveBand();
            var errText = band ? band.querySelector('.settings-save-band-text') : null;
            if (errText) errText.textContent = buildSaveErrorLabel(diag, errorMsg); // textContent intentional — prevents XSS from server error messages
        }

        // When the HTTP layer looks like something between the browser and Jellyfin
        // dropped the request (network error, HTML body, 5xx), fire a lightweight
        // Ping. If Ping succeeds, we KNOW the backend is reachable and the payload
        // was rejected on purpose - useful signal for the console.  If Ping ALSO
        // fails, we log a clear "backend unreachable" line so infrastructure issues
        // are unmistakable in the report.
        if (diag.kind === 'network' || diag.kind === 'proxy' || diag.kind === 'server') {
            probeBackendReachability(diag);
        }
    });
}

/**
 * Compose a compact user-facing error label for the save band.
 * Priority: server-provided message > HTTP status hint > generic fallback.
 *
 * @param {{status:number,statusText:string,kind:string}} diag
 * @param {string} serverMessage - Parsed 'message' field from the JSON error body, or ''.
 * @returns {string}
 */
function buildSaveErrorLabel(diag, serverMessage) {
    if (serverMessage) return serverMessage;
    if (diag.kind === 'proxy') {
        return T('settingsErrorProxy',
            'Save blocked (HTTP ' + diag.status + '). Check reverse proxy / WAF logs.');
    }
    if (diag.kind === 'network') {
        return T('settingsErrorNetwork',
            'Save failed: backend unreachable. Check network / proxy.');
    }
    if (diag.kind === 'unauthorized') {
        return T('settingsErrorUnauthorized',
            'Save failed: not authorized. Try re-logging in.');
    }
    if (diag.status > 0) {
        return T('settingsError', 'Failed to save settings.') + ' (HTTP ' + diag.status + ')';
    }
    return T('settingsError', 'Failed to save settings.');
}

/**
 * Fires the /Ping endpoint after a failed save to distinguish
 * "backend unreachable" from "backend reachable, payload rejected".
 * Purely informational: writes a diagnostic line to console. Never
 * throws and never blocks any user-visible flow.
 *
 * @param {{status:number,statusText:string,kind:string,snippet:string}} originalDiag
 */
function probeBackendReachability(originalDiag) {
    try {
        apiGet('JellyfinHelper/Ping', function () {
            console.info('[JellyfinHelper] Ping OK after failed save. '
                + 'Backend is reachable - the Configuration POST itself was rejected. '
                + 'Original: HTTP ' + originalDiag.status + ' (' + originalDiag.kind + ').');
        }, function (pingErr) {
            var pingDiag = describeApiError(pingErr);
            console.warn('[JellyfinHelper] Ping ALSO failed. '
                + 'The entire backend path appears to be blocked (reverse proxy / WAF / firewall). '
                + 'Save: HTTP ' + originalDiag.status + ' (' + originalDiag.kind + '); '
                + 'Ping: HTTP ' + pingDiag.status + ' (' + pingDiag.kind + ').');
        });
    } catch (_e) {
        // Diagnostic-only path - never let a Ping failure surface to the user.
    }
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
        var d = createDialogOverlay('trashDialogOverlay', T('trashDisableTitle', 'Trash Folders Detected'), getCssVar('--color-danger', '#e74c3c'), bodyText);

        d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
            removeTrashDialog();
            var chk = document.getElementById('cfgTrash');
            if (chk) chk.checked = true;
            saveBtn.disabled = false;
        }));
        d.btnRow.appendChild(createDialogBtn(T('trashKeep', 'Keep Folders'), 'success', function () {
            removeTrashDialog();
            doSaveSettings(payload);
        }));
        d.btnRow.appendChild(createDialogBtn(T('trashDelete', 'Delete Folders'), 'danger', function () {
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

    var d = createDialogOverlay('trashDialogOverlay', T('trashDeleteConfirmTitle', 'Are you sure?'), getCssVar('--color-danger', '#e74c3c'), bodyText);

    d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
        removeTrashDialog();
        var chk = document.getElementById('cfgTrash');
        if (chk) chk.checked = true;
        saveBtn.disabled = false;
    }));
    d.btnRow.appendChild(createDialogBtn(T('trashDeleteConfirmOk', 'Yes, Delete All'), 'danger', function () {
        removeTrashDialog();
        msg.innerHTML = '<div style="opacity:0.6;">' + escHtml(T('trashDeleting', 'Deleting trash folders…')) + '</div>';

        apiDelete('JellyfinHelper/Trash/Folders', function (result) {
            var summary = '';
            var statusClass = 'success-msg';
            if (result.deleted > 0) {
                summary += mi('check_circle') + ' ' + escHtml(T('trashDeletedCount', 'Deleted')) + ': ' + (Math.max(0, parseInt(result.deleted, 10) || 0)) + ' ' + escHtml(T('folders', 'folders'));
            }
            if (result.failed > 0) {
                summary += (summary ? ' | ' : '') + mi('error') + ' ' + escHtml(T('trashFailedCount', 'Failed')) + ': ' + (Math.max(0, parseInt(result.failed, 10) || 0));
                statusClass = 'error-msg';
            }
            if (!summary) {
                summary = mi('error') + ' ' + escHtml(T('trashDeleteError', 'Failed to delete trash folders.'));
                statusClass = 'error-msg';
            }
            msg.innerHTML = '<div class="' + statusClass + '">' + summary + '</div>';
            doSaveSettings(payload);
        }, function () {
            msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + escHtml(T('trashDeleteError', 'Failed to delete trash folders.')) + '</div>';
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
    var btnImport = document.getElementById('btnBackupImport');
    var fileInput = document.getElementById('btnBackupImportFile');
    if (btnImport && fileInput) {
        btnImport.addEventListener('click', function () {
            fileInput.click();
        });
    }
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

        msg.innerHTML = '<div class="success-msg">' + mi('check_circle') + ' ' + escHtml(T('backupExportSuccess', 'Backup exported successfully.')) + '</div>';
        btn.disabled = false;
        setTimeout(function () {
            msg.innerHTML = '';
        }, 5000);
    }, function (err) {
        var errorText = escHtml(T('backupExportError', 'Failed to export backup.'));
        var response = err && (err.responseJSON || err);
        if (response && response.message) {
            errorText = escHtml(response.message);
        }

        msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + errorText + '</div>';
        btn.disabled = false;
    });
}

function triggerBackupImport(file) {
    var msg = document.getElementById('backupMsg');

    // Client-side size check (10 MB)
    if (file.size > 10 * 1024 * 1024) {
        msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + escHtml(T('backupFileTooLarge', 'File too large. Maximum size is 10 MB.')) + '</div>';
        return;
    }

    // Show confirmation dialog
    showBackupImportConfirmation(file);
}

function showBackupImportConfirmation(file) {
    removeBackupDialog();

    var d = createDialogOverlay('backupDialogOverlay', T('backupImportConfirmTitle', 'Import Backup'), getCssVar('--color-primary', '#00a4dc'), '');

    var p1 = document.createElement('p');
    p1.textContent = T('backupImportConfirmMsg', 'This will overwrite your current settings, Arr integrations, and trend data with the backup data.');
    d.body.appendChild(p1);

    var p2 = document.createElement('p');
    var strong = document.createElement('strong');
    strong.textContent = T('backupImportConfirmFile', 'File') + ': ';
    p2.appendChild(strong);
    p2.appendChild(document.createTextNode(file.name + ' (' + formatBytes(file.size) + ')'));
    d.body.appendChild(p2);

    var p3 = document.createElement('p');
    p3.className = 'color-danger';
    p3.textContent = T('backupImportConfirmWarn', 'This action cannot be undone!');
    d.body.appendChild(p3);

    d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
        removeBackupDialog();
    }));
    d.btnRow.appendChild(createDialogBtn(T('backupImportConfirmOk', 'Yes, Import'), 'warning', function () {
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
    msg.innerHTML = '<div style="opacity:0.6;">' + escHtml(T('backupImporting', 'Importing backup…')) + '</div>';

    var reader = new FileReader();
    reader.onload = function (e) {
        var json = e.target.result;

        // Validate it's parsable JSON before sending
        try {
            JSON.parse(json);
        } catch (parseErr) {
            msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + escHtml(T('backupInvalidJson', 'Invalid backup file. The file does not contain valid JSON.')) + '</div>';
            return;
        }

        apiPostRaw('JellyfinHelper/Backup/Import', json, 'application/json', function (result) {
            var data = typeof result === 'string' ? JSON.parse(result) : result;
            var summary = data.Summary || data.summary || {};
            var parts = [];
            if (summary.ConfigurationRestored || summary.configurationRestored) parts.push(T('backupConfigRestored', 'Settings'));
            if (summary.TimelineRestored || summary.timelineRestored) parts.push(T('backupTimelineRestored', 'Growth Timeline'));
            if (summary.BaselineRestored || summary.baselineRestored) parts.push(T('backupBaselineRestored', 'Baseline'));

            var successMsg = mi('check_circle') + ' ' + escHtml(T('backupImportSuccess', 'Backup imported successfully.'));
            if (parts.length > 0) {
                successMsg += ' (' + parts.map(escHtml).join(', ') + ')';
            }

            // Show warnings if any
            var warnings = data.Warnings || data.warnings || [];
            if (warnings.length > 0) {
                successMsg += '<br><span class="color-warning">' + warnings.length + ' ' + T('backupWarnings', 'warning(s)') + ':</span>';
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
            var errorText = escHtml(T('backupImportError', 'Failed to import backup.'));
            try {
                var response = err && (err.responseJSON || (typeof err.responseText === 'string' ? JSON.parse(err.responseText) : null));
                if (response && response.message) {
                    errorText = escHtml(response.message);
                }
            } catch (ignored) { /* use default error text */
            }
            msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + errorText + '</div>';
        });
    };
    reader.onerror = function () {
        msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + escHtml(T('backupImportError', 'Failed to import backup.')) + '</div>';
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
        var originalHtml = mi('extension') + T('testConnection', 'Test Connection');

        if (_seerrTimer) {
            clearTimeout(_seerrTimer);
            _seerrTimer = null;
        }

        if (!url || !key) {
            _seerrTimer = showButtonFeedback(btn, false, T('seerrFillFields', 'Please fill in URL and API Key first.'), originalHtml, 3000);
            return;
        }
        btn.disabled = true;
        btn.innerHTML = '<span class="btn-spinner"></span>' + escHtml(T('testing', 'Testing…'));
        apiPost('JellyfinHelper/Seerr/Test', {Url: url, ApiKey: key}, function (res) {
            btn.disabled = false;
            if (res && res.success) {
                _seerrTimer = showButtonFeedback(btn, true, escHtml(res.message || 'OK'), originalHtml);
                // Auto-save settings after successful connection test (quiet to avoid double feedback)
                var payload = buildSettingsPayload();
                doSaveSettings(payload, {quiet: true, element: document.getElementById('arrCollapsibleHeaderSeerr')});
                // Enable previously greyed-out Seerr UI sections
                updateSeerrUIState(true);
                // Refresh the Discovery wrapper (depends on Seerr being configured)
                refreshDiscoveryAccessState();
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
 * Attach the toggle + copy-to-clipboard handlers for the Discovery setup hint.
 */
function attachDiscoveryCopyHandler() {
    // Toggle handler: ℹ️ icon opens/closes the hint panel
    var toggleBtn = document.getElementById('btnToggleDiscoveryHint');
    var panel = document.getElementById('discoveryHintPanel');
    if (toggleBtn && panel) {
        toggleBtn.setAttribute('aria-expanded', 'false');
        toggleBtn.setAttribute('aria-controls', 'discoveryHintPanel');
        toggleBtn.addEventListener('click', function () {
            var isOpen = panel.style.display !== 'none';
            panel.style.display = isOpen ? 'none' : 'block';
            toggleBtn.setAttribute('aria-expanded', isOpen ? 'false' : 'true');
        });
    }

    // Copy handler
    var btn = document.getElementById('btnCopyDiscoveryHtml');
    if (!btn) return;
    btn.addEventListener('click', function () {
        var text = '<div class="jellyfinhelper discovery"></div>';
        var span = btn.querySelector('span:last-child');

        function onCopySuccess() {
            if (span) span.textContent = T('discoveryCopied', 'Copied!');
            btn.style.background = '#2ecc71';
            btn.style.color = '#fff';
            setTimeout(function () {
                if (span) span.textContent = T('discoveryCopySnippet', 'Copy');
                btn.style.background = '';
                btn.style.color = '';
            }, 2000);
        }

        function onCopyFailure() {
            if (span) span.textContent = '\u2717';
            btn.style.background = '#e74c3c';
            btn.style.color = '#fff';
            setTimeout(function () {
                if (span) span.textContent = T('discoveryCopySnippet', 'Copy');
                btn.style.background = '';
                btn.style.color = '';
            }, 2000);
        }

        // Try modern clipboard API first
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(onCopySuccess).catch(function () {
                if (fallbackCopy(text)) {
                    onCopySuccess();
                } else {
                    onCopyFailure();
                }
            });
        } else {
            if (fallbackCopy(text)) {
                onCopySuccess();
            } else {
                onCopyFailure();
            }
        }
    });
}

/** Fallback copy using textarea + execCommand for non-HTTPS contexts. */
function fallbackCopy(text) {
    var textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.style.position = 'fixed';
    textarea.style.left = '-9999px';
    textarea.style.opacity = '0';
    document.body.appendChild(textarea);
    textarea.focus();
    textarea.select();
    try {
        return document.execCommand('copy');
    } catch (e) {
        return false;
    } finally {
        document.body.removeChild(textarea);
    }
}

/**
 * Attach auto-save change handlers to task-mode dropdowns and language select.
 * Called after the settings form is rendered.
 */
function attachAutoSaveHandlers() {
    // Task mode dropdowns - auto-save on change
    var taskModeIds = ['cfgTrickplayMode', 'cfgEmptyFolderMode', 'cfgSubtitleMode', 'cfgLinkMode', 'cfgRecommendationsMode', 'cfgSeerrMode'];
    for (var i = 0; i < taskModeIds.length; i++) {
        (function (id) {
            var el = document.getElementById(id);
            if (!el) return;
            el.addEventListener('change', function () {
                // Update Recommendations tab visibility only after save succeeds
                if (id === 'cfgRecommendationsMode') {
                    var isActive = el.value === 'Activate';
                    doSaveSettings(buildSettingsPayload(), {
                        quiet: true,
                        element: el,
                        onSuccess: function () {
                            updateRecsTabVisibility(el.value);
                            // Clear the persisted playlist preference only on Deactivate.
                            // Performed inside onSuccess so the DOM stays in sync with the server
                            // even if the save request fails.
                            if (el.value === 'Deactivate') {
                                var chkPost = document.getElementById('cfgSyncPlaylist');
                                if (chkPost) chkPost.checked = false;
                            }
                            // Update playlist sync toggle greyed-out state
                            var wrapper = document.getElementById('playlistSyncWrapper');
                            if (wrapper) {
                                wrapper.style.opacity = isActive ? '' : '0.5';
                                wrapper.style.pointerEvents = isActive ? '' : 'none';
                            }
                            var chk = document.getElementById('cfgSyncPlaylist');
                            if (chk) chk.disabled = !isActive;
                            var hint = document.querySelector('.playlist-sync-disabled-hint');
                            if (hint) hint.style.display = isActive ? 'none' : '';

                            // Update discovery access toggle greyed-out state
                            refreshDiscoveryAccessState();
                            // Uncheck discovery when deactivating recommendations
                            var discChk = document.getElementById('cfgDiscoveryUserAccess');
                            if (!isActive && discChk) discChk.checked = false;
                        }
                    });
                    return;
                }
                doSaveSettings(buildSettingsPayload(), {quiet: true, element: el});
            });
        })(taskModeIds[i]);
    }

    // Playlist sync toggle - auto-save on change
    // Uses inline indicator appended to the label (not the overlay function which positions badly for checkboxes)
    var syncEl = document.getElementById('cfgSyncPlaylist');
    if (syncEl) {
        syncEl.addEventListener('change', function () {
            doSaveSettings(buildSettingsPayload(), {
                quiet: true,
                element: null, // suppress default overlay
                onSuccess: function () { showInlineCheckboxIndicator(syncEl, true); },
                onError: function () { showInlineCheckboxIndicator(syncEl, false); }
            });
        });
    }

    // Discovery user access toggle - auto-save on change
    var discoveryEl = document.getElementById('cfgDiscoveryUserAccess');
    if (discoveryEl) {
        discoveryEl.addEventListener('change', function () {
            doSaveSettings(buildSettingsPayload(), {
                quiet: true,
                element: null, // suppress default overlay
                onSuccess: function () { showInlineCheckboxIndicator(discoveryEl, true); },
                onError: function () { showInlineCheckboxIndicator(discoveryEl, false); }
            });
        });
    }

    // Language dropdown - auto-save + UI rebuild with scroll restore
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

/**
 * Shows a small inline indicator AFTER the label text of a checkbox toggle.
 * Used for checkbox auto-save confirmation instead of showAutoSaveIndicatorOverlay
 * which doesn't position correctly for checkboxes.
 * @param {HTMLInputElement} checkbox - The checkbox input element.
 * @param {boolean} [success] - Whether the save succeeded (true) or failed (false). Defaults to true.
 */
function showInlineCheckboxIndicator(checkbox, success) {
    if (!checkbox) return;
    var label = checkbox.nextElementSibling;
    if (!label || label.tagName !== 'LABEL') return;

    var ok = success !== false;

    // Remove any existing indicator on this label
    var existing = label.querySelector('.inline-save-indicator');
    if (existing) existing.remove();

    // Create inline indicator
    var indicator = document.createElement('span');
    indicator.className = 'inline-save-indicator';
    indicator.innerHTML = ' ' + mi(ok ? 'check_circle' : 'error');
    indicator.style.color = ok ? '#2ecc71' : '#e74c3c';
    indicator.style.marginLeft = '0.4em';
    indicator.style.opacity = '1';
    indicator.style.transition = 'opacity 0.5s';
    label.appendChild(indicator);

    // Fade out after 2 seconds
    setTimeout(function () {
        indicator.style.opacity = '0';
        setTimeout(function () { indicator.remove(); }, 600);
    }, 2000);
}

/**
 * Shows a brief inline save indicator after the "Excluded Libraries" label.
 * Uses the same visual pattern as showInlineCheckboxIndicator but targets
 * the label element preceding the library multi-select wrapper.
 * @param {string} wrapperId - The DOM id of the wrapper div (e.g. 'cfgExcludedWrapper').
 * @param {boolean} [success] - Whether the save succeeded (true) or failed (false).
 */
function showLibraryMultiSelectIndicator(wrapperId, success) {
    var wrapper = document.getElementById(wrapperId);
    if (!wrapper) return;
    // The label is the previous sibling of the wrapper
    var label = wrapper.previousElementSibling;
    if (!label || label.tagName !== 'LABEL') return;

    var ok = success !== false;

    // Remove any existing indicator on this label
    var existing = label.querySelector('.inline-save-indicator');
    if (existing) existing.remove();

    // Create inline indicator (same style as checkbox indicators)
    var indicator = document.createElement('span');
    indicator.className = 'inline-save-indicator';
    indicator.innerHTML = ' ' + mi(ok ? 'check_circle' : 'error');
    indicator.style.color = ok ? '#2ecc71' : '#e74c3c';
    indicator.style.marginLeft = '0.4em';
    indicator.style.opacity = '1';
    indicator.style.transition = 'opacity 0.5s';
    label.appendChild(indicator);

    // Fade out after 2 seconds
    setTimeout(function () {
        indicator.style.opacity = '0';
        setTimeout(function () { indicator.remove(); }, 600);
    }, 2000);
}

// ===== Library Multi-Select Widget =====

/**
 * Initializes the library multi-select dropdown by fetching available libraries
 * from the server and rendering a checkbox list inside the wrapper element.
 * @param {Object} cfg - The current plugin configuration (contains ExcludedLibraries).
 */
function initLibraryMultiSelects(cfg) {
    var wrapper = document.getElementById('cfgExcludedWrapper');
    if (wrapper) {
        wrapper.setAttribute('data-initial-value', cfg.ExcludedLibraries || '');
    }

    apiGet('JellyfinHelper/Configuration/Libraries', function (data) {
        var libraries = (data && data.libraries) || [];
        var excludedSet = parseCommaSeparatedSet(cfg.ExcludedLibraries || '');

        renderLibraryMultiSelect('cfgExcludedWrapper', libraries, excludedSet, 'excluded');
    }, function () {
        // Fallback: show simple text input if API fails
        var excWrap = document.getElementById('cfgExcludedWrapper');
        if (excWrap) excWrap.innerHTML = '<input type="text" id="cfgExcludedFallback" value="' + escAttr(cfg.ExcludedLibraries || '') + '">';
    });
}

/**
 * Parses a comma-separated string into a Set of trimmed, lowercased values.
 */
function parseCommaSeparatedSet(str) {
    var set = {};
    if (!str) return set;
    var parts = str.split(',');
    for (var i = 0; i < parts.length; i++) {
        var v = parts[i].trim();
        if (v) set[v.toLowerCase()] = v;
    }
    return set;
}

/**
 * Renders a multi-select checkbox list widget inside the given wrapper element.
 * @param {string} wrapperId - The DOM id of the wrapper div.
 * @param {Array} libraries - Array of {name, collectionType} from the API.
 * @param {Object} selectedSet - Object with lowercase keys of currently selected library names.
 * @param {string} type - 'excluded' for styling/ids.
 */
function renderLibraryMultiSelect(wrapperId, libraries, selectedSet, type) {
    var wrapper = document.getElementById(wrapperId);
    if (!wrapper) return;

    // Identify selected libraries that are no longer returned by the API
    // (renamed/deleted). Store them so getLibraryMultiSelectValue() can preserve them.
    var available = {};
    for (var ai = 0; ai < libraries.length; ai++) {
        available[libraries[ai].name.toLowerCase()] = true;
    }
    var missingSelected = [];
    for (var key in selectedSet) {
        if (Object.prototype.hasOwnProperty.call(selectedSet, key) && !available[key]) {
            missingSelected.push(selectedSet[key]);
        }
    }
    wrapper.setAttribute('data-missing-values', missingSelected.join(', '));

    var selectedCount = Object.keys(selectedSet).length;
    var noneSelectedLabel = T('libraryNoneExcluded', 'None excluded (default)');

    var h = '<div class="library-multiselect" data-type="' + type + '">';
    // Summary/toggle button
    h += '<button type="button" class="library-multiselect-toggle">';
    var summaryText = noneSelectedLabel;
    if (selectedCount > 0) {
        var selectedNames = [];
        for (var k in selectedSet) { if (Object.prototype.hasOwnProperty.call(selectedSet, k)) selectedNames.push(selectedSet[k]); }
        summaryText = selectedNames.length <= 3 ? selectedNames.join(', ') : selectedNames.slice(0, 2).join(', ') + ' +' + (selectedNames.length - 2);
    }
    h += '<span class="library-multiselect-summary">' + escHtml(summaryText) + '</span>';
    h += '<span class="library-multiselect-chevron">' + mi('expand_more') + '</span>';
    h += '</button>';
    // Dropdown panel (hidden by default)
    h += '<div class="library-multiselect-panel" style="display:none;">';
    if (libraries.length === 0) {
        h += '<div class="help-text" style="padding:0.5em;">' + escHtml(T('noData', 'No data')) + '</div>';
    } else {
        for (var i = 0; i < libraries.length; i++) {
            var lib = libraries[i];
            var isChecked = !!(selectedSet[lib.name.toLowerCase()]);
            var checkId = wrapperId + '_lib_' + i;
            h += '<div class="library-multiselect-item">';
            h += '<input type="checkbox" id="' + checkId + '" value="' + escAttr(lib.name) + '"' + (isChecked ? ' checked' : '') + '>';
            h += '<label for="' + checkId + '">' + escHtml(lib.name) + ' <span class="library-type-badge">' + escHtml(lib.collectionType) + '</span></label>';
            h += '</div>';
        }
    }
    h += '</div></div>';

    wrapper.innerHTML = h;

    // Attach click handler via addEventListener (more robust than inline onclick in Jellyfin plugin context)
    var toggleBtn = wrapper.querySelector('.library-multiselect-toggle');
    if (toggleBtn) {
        toggleBtn.addEventListener('click', function () {
            toggleLibraryDropdown(toggleBtn);
        });
    }

    // Attach change handlers to checkboxes for auto-save with overlay indicator on the toggle button
    var checkboxes = wrapper.querySelectorAll('input[type="checkbox"]');
    for (var ci = 0; ci < checkboxes.length; ci++) {
        checkboxes[ci].addEventListener('change', function () {
            updateLibraryMultiSelectSummary(wrapperId, type);
            var indicatorTarget = wrapper.querySelector('.library-multiselect-toggle') || wrapper;
            doSaveSettings(buildSettingsPayload(), { quiet: true, element: indicatorTarget });
        });
    }
}

/**
 * Toggles the visibility of the dropdown panel in a library multi-select.
 */
function toggleLibraryDropdown(btn) {
    var panel = btn.nextElementSibling;
    if (!panel) return;
    var isOpen = panel.style.display !== 'none';
    panel.style.display = isOpen ? 'none' : 'block';
    // Chevron stays as expand_more (pointing down) regardless of state — matches native <select> behavior
}

/**
 * Updates the summary text after a checkbox change.
 */
function updateLibraryMultiSelectSummary(wrapperId, type) {
    var wrapper = document.getElementById(wrapperId);
    if (!wrapper) return;
    var checkboxes = wrapper.querySelectorAll('input[type="checkbox"]');
    var count = 0;
    for (var i = 0; i < checkboxes.length; i++) {
        if (checkboxes[i].checked) count++;
    }
    var summary = wrapper.querySelector('.library-multiselect-summary');
    if (!summary) return;

    var noneSelectedLabel = T('libraryNoneExcluded', 'None excluded (default)');

    if (count === 0) {
        summary.textContent = noneSelectedLabel;
    } else {
        var names = [];
        for (var j = 0; j < checkboxes.length; j++) {
            if (checkboxes[j].checked) names.push(checkboxes[j].value);
        }
        summary.textContent = names.length <= 3 ? names.join(', ') : names.slice(0, 2).join(', ') + ' +' + (names.length - 2);
    }
}

/**
 * Gets the comma-separated value from a library multi-select wrapper.
 * Returns empty string if nothing is selected (=all included / none excluded).
 */
function getLibraryMultiSelectValue(wrapperId) {
    var wrapper = document.getElementById(wrapperId);
    if (!wrapper) return '';
    // Check for fallback text input
    var fallback = wrapper.querySelector('input[type="text"]');
    if (fallback) return fallback.value;
    // Read checkboxes
    var checkboxes = wrapper.querySelectorAll('input[type="checkbox"]');
    if (checkboxes.length === 0) {
        // Widget not yet rendered (async API call pending) - return initial value to avoid data loss
        return wrapper.getAttribute('data-initial-value') || '';
    }
    var selected = [];
    for (var i = 0; i < checkboxes.length; i++) {
        if (checkboxes[i].checked) selected.push(checkboxes[i].value);
    }
    // Preserve previously-excluded library names that are no longer returned by the API
    // (e.g. renamed or deleted libraries). Without this, those exclusions would be silently
    // dropped on the next save, potentially causing unexpected cleanup of those libraries.
    var missingValues = wrapper.getAttribute('data-missing-values');
    if (missingValues) {
        var parts = missingValues.split(',');
        for (var m = 0; m < parts.length; m++) {
            var v = parts[m].trim();
            if (v) selected.push(v);
        }
    }
    return selected.join(', ');
}

/**
 * Attaches keydown and input handlers to the OrphanMinAgeDays number field.
 * Blocks non-numeric characters (e, E, +, .) that browsers allow in type="number" fields
 * due to scientific notation support, and clamps the value to [0, 3650] on input.
 */
function attachOrphanAgeInputHandler() {
    var input = document.getElementById('cfgOrphanAge');
    if (!input) return;
    // Block characters that type="number" allows but are invalid for integer days
    input.addEventListener('keydown', function (e) {
        if (e.key === 'e' || e.key === 'E' || e.key === '+' || e.key === '.') {
            e.preventDefault();
        }
    });
    // Clamp on input to enforce max visually (handles paste, spinner clicks, etc.)
    input.addEventListener('input', function () {
        var v = parseInt(input.value, 10);
        if (!isNaN(v)) {
            if (v > 3650) input.value = '3650';
            if (v < 0) input.value = '0';
        }
    });
}

/**
 * Attaches an input event listener to the trash path field that clears
 * the validation error state as soon as the user starts editing.
 * This creates the UX flow: error on save → user edits → error clears → save again.
 */
function attachTrashPathInputHandler() {
    var input = document.getElementById('cfgTrashPath');
    if (!input) return;
    input.addEventListener('input', function () {
        showTrashPathError(null);
    });
}

/**
 * Attaches keydown and input handlers to the TrashRetentionDays number field.
 * Blocks non-numeric characters and clamps the value to [0, 3650] on input.
 */
function attachTrashDaysInputHandler() {
    var input = document.getElementById('cfgTrashDays');
    if (!input) return;
    input.addEventListener('keydown', function (e) {
        if (e.key === 'e' || e.key === 'E' || e.key === '+' || e.key === '.') {
            e.preventDefault();
        }
    });
    input.addEventListener('input', function () {
        var v = parseInt(input.value, 10);
        if (!isNaN(v)) {
            if (v > 3650) input.value = '3650';
            if (v < 0) input.value = '0';
        }
    });
}

/**
 * Validates the trash folder path on the client side before saving.
 * Returns an i18n error message string if invalid, or null if valid.
 * @param {string} path - The trash folder path value.
 * @param {boolean} useTrash - Whether the trash feature is enabled.
 * @returns {string|null}
 */
function validateTrashPath(path, useTrash) {
    // When trash is disabled, path is irrelevant
    if (!useTrash) return null;

    // When trash is enabled, path must not be empty
    if (!path || !path.trim()) {
        return T('trashPathEmpty', 'Trash folder path is required when trash is enabled.');
    }

    var trimmed = path.trim();

    // 1. Global invalid characters (filesystem-unsafe + control chars)
    if (/[*?<>|"\x00-\x1f]/.test(trimmed)) {
        return T('trashPathInvalidChars', 'Path contains invalid characters.');
    }

    // 2. Must not end with slash or backslash
    if (/[/\\]$/.test(trimmed)) {
        return T('trashPathTrailingSlash', 'Path must not end with a slash or backslash.');
    }

    // 3. Strip optional absolute prefix for segment analysis
    var segmentPart = trimmed
        .replace(/^\\\\[^\\/]+[\\/][^\\/]+/, '') // strip UNC prefix (\\server\share)
        .replace(/^[A-Za-z]:[/\\]/, '')          // strip Windows drive prefix (C:\ or D:/)
        .replace(/^[/\\]/, '');                   // strip leading Unix separator

    // 4. Must not contain consecutive separators in the remaining path
    if (/[/\\]{2,}/.test(segmentPart)) {
        return T('trashPathDoubleSlash', 'Path must not contain consecutive slashes or backslashes.');
    }

    // 5. Must have at least one real segment after stripping prefix
    if (!segmentPart) {
        return T('trashPathInvalid', 'The trash folder path is invalid.');
    }

    // 6. Split into segments and validate each one
    var segments = segmentPart.split(/[/\\]/);
    for (var i = 0; i < segments.length; i++) {
        var seg = segments[i];

        // Empty segment (defensive — should not happen after double-slash check)
        if (!seg) {
            return T('trashPathInvalid', 'The trash folder path is invalid.');
        }

        // Only block actual filesystem navigation markers (. = current dir, .. = parent dir)
        if (seg === '.' || seg === '..') {
            return T('trashPathDotSegment', "Path must not contain '.' or '..' directory references.");
        }


        // Segment is only whitespace
        if (/^\s+$/.test(seg)) {
            return T('trashPathInvalid', 'The trash folder path is invalid.');
        }
    }

    return null;
}

/**
 * Shows or clears the trash path validation error UI on the input field.
 * @param {string|null} errorMsg - The error message, or null to clear.
 */
function showTrashPathError(errorMsg) {
    var input = document.getElementById('cfgTrashPath');
    var existingErr = document.getElementById('trashPathErrorMsg');

    if (!errorMsg) {
        // Clear error state
        if (input) input.style.borderColor = '';
        if (existingErr) existingErr.remove();
        return;
    }

    // Set error state
    if (input) input.style.borderColor = '#e74c3c';
    if (existingErr) {
        existingErr.innerHTML = mi('error') + ' ' + escHtml(errorMsg);
    } else {
        var errDiv = document.createElement('div');
        errDiv.id = 'trashPathErrorMsg';
        errDiv.className = 'error-msg';
        errDiv.style.marginTop = '0.3em';
        errDiv.style.fontSize = '0.85em';
        errDiv.innerHTML = mi('error') + ' ' + escHtml(errorMsg);
        // Insert after the position:relative wrapper div, not inside it
        var inputWrapper = input.parentNode;
        if (inputWrapper && inputWrapper.parentNode) {
            inputWrapper.parentNode.insertBefore(errDiv, inputWrapper.nextSibling);
        }
    }
}

/**
 * Checks whether the trash path has changed compared to the last saved value.
 * Only returns true when trash was previously enabled (so old content may exist)
 * AND trash remains enabled AND the path is different.
 * @param {Object} payload - The settings payload to check.
 * @returns {boolean}
 */
function hasTrashPathChanged(payload) {
    if (!_wasTrashEnabled) return false;
    if (!payload.UseTrash) return false;
    var oldPath = (_previousTrashPath || '').trim();
    var newPath = (payload.TrashFolderPath || '').trim();
    if (!oldPath || !newPath) return false;
    return oldPath !== newPath;
}

/**
 * Shows a dialog when the trash path has changed, asking the user whether to
 * move existing trash content to the new location, delete it, or cancel.
 * @param {Object} payload - The settings payload to save after the user decides.
 * @param {Object} [options] - Optional doSaveSettings options to forward.
 */
function showTrashPathChangeDialog(payload, options) {
    var saveBtn = document.getElementById('btnSaveSettings');
    var msg = document.getElementById('settingsMsg');
    var oldPath = _previousTrashPath;
    var newPath = (payload.TrashFolderPath || '').trim();

    // Query the server for existing folders at the OLD path
    apiPost('JellyfinHelper/Trash/FoldersForPath', {TrashFolderPath: oldPath}, function (data) {
        var paths = (data && data.Paths) || [];
        if (paths.length === 0) {
            // No old content exists — save directly, update tracking on success.
            // Set the re-entrancy guard so doSaveSettings() won't re-trigger this dialog.
            _trashPathChangeHandled = true;
            doSaveSettings(payload, {
                quiet: !!(options && options.quiet),
                element: (options && options.element) || null,
                onSuccess: function () {
                    _previousTrashPath = newPath;
                    if (options && typeof options.onSuccess === 'function') options.onSuccess();
                },
                onError: (options && options.onError) || undefined
            });
            return;
        }

        var bodyText = T('trashPathChangePrompt', 'The trash folder path has changed. Existing trash content was found at the old location:')
            + formatPathList(paths)
            + '\n\n' + T('trashPathChangeQuestion', 'What should happen with the existing trash content?');

        removeTrashDialog();
        var d = createDialogOverlay('trashDialogOverlay', T('trashPathChangeTitle', 'Trash Path Changed'), getCssVar('--color-primary', '#00a4dc'), bodyText);

        // Cancel button — revert the path in the input
        d.btnRow.appendChild(createDialogBtn(T('cancel', 'Cancel'), 'cancel', function () {
            removeTrashDialog();
            var input = document.getElementById('cfgTrashPath');
            if (input) input.value = oldPath;
            if (saveBtn) saveBtn.disabled = false;
        }));

        // Move content button
        d.btnRow.appendChild(createDialogBtn(T('trashPathMoveContent', 'Move Content'), 'success', function () {
            removeTrashDialog();

            // Extracted helper: saves settings then relocates trash content.
            // Used by both the access-check success path and the graceful-degradation fallback.
            function doRelocateTrash() {
                if (msg) msg.innerHTML = '<div style="opacity:0.6;">' + escHtml(T('trashPathMoving', 'Moving trash content…')) + '</div>';
                _trashPathChangeHandled = true;
                doSaveSettings(payload, {
                    quiet: !!(options && options.quiet),
                    element: (options && options.element) || null,
                    onSuccess: function () {
                        _previousTrashPath = newPath;
                        apiPost('JellyfinHelper/Trash/Relocate', {OldTrashPath: oldPath, NewTrashPath: newPath}, function (result) {
                            var moved = result && result.Moved || 0;
                            var failed = result && result.Failed || 0;
                            if (failed === 0 && moved > 0) {
                                if (msg) {
                                    msg.innerHTML = '<div class="success-msg">' + mi('check_circle') + ' ' + escHtml(T('trashPathMoveSuccess', 'Trash content moved successfully.')) + '</div>';
                                    setTimeout(function () { if (msg) msg.innerHTML = ''; }, 5000);
                                }
                            } else if (failed > 0) {
                                var partial = escHtml(T('trashPathMovePartial', 'Partially moved: {0} moved, {1} failed.').replace('{0}', moved).replace('{1}', failed));
                                if (msg) msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + partial + '</div>';
                            } else {
                                // 0 moved, 0 failed: likely a permission issue on the source or empty source
                                if (msg) msg.innerHTML = '<div class="error-msg" style="opacity:0.85;">' + mi('warning') + ' ' + escHtml(T('trashPathMoveNothingMoved', 'No items were moved. The source may be empty or inaccessible due to permissions.')) + '</div>';
                            }
                            if (options && typeof options.onSuccess === 'function') options.onSuccess();
                        }, function () {
                            if (msg) msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + escHtml(T('trashPathMoveError', 'Failed to move trash content.')) + '</div>';
                            if (options && typeof options.onSuccess === 'function') options.onSuccess();
                        });
                    },
                    onError: function (errMsg) {
                        if (options && typeof options.onError === 'function') options.onError(errMsg);
                    }
                });
            }

            if (msg) msg.innerHTML = '<div style="opacity:0.6;">' + escHtml(T('trashPathCheckingAccess', 'Checking permissions…')) + '</div>';

            // Proactive access check on the NEW path before attempting relocation
            apiPost('JellyfinHelper/Trash/CheckAccess', {TrashFolderPath: newPath}, function (accessData) {
                if (accessData && accessData.AllAccessible === false) {
                    // Access check failed — show the specific error from the server
                    var accessErrors = (accessData.Results || []).filter(function(r) { return !r.HasFullAccess; });
                    var errorDetail = accessErrors.length > 0 ? (accessErrors[0].ErrorMessage || '') : '';
                    var errorText = errorDetail || T('trashPathAccessDenied', 'Permission denied on new trash path.');
                    if (msg) msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + escHtml(errorText) + '</div>';
                    if (saveBtn) saveBtn.disabled = false;
                    return;
                }
                doRelocateTrash();
            }, function () {
                // CheckAccess API call failed — proceed with move anyway (graceful degradation)
                doRelocateTrash();
            });
        }));

        // Delete & start fresh button
        d.btnRow.appendChild(createDialogBtn(T('trashPathDeleteContent', 'Delete & Start Fresh'), 'danger', function () {
            removeTrashDialog();
            if (msg) msg.innerHTML = '<div style="opacity:0.6;">' + escHtml(T('trashPathDeleting', 'Deleting old trash content…')) + '</div>';

            // Delete old folders first (uses current saved config which still has old path)
            apiDelete('JellyfinHelper/Trash/Folders', function () {
                if (msg) {
                    msg.innerHTML = '<div class="success-msg">' + mi('check_circle') + ' ' + escHtml(T('trashPathDeleteSuccess', 'Old trash content deleted.')) + '</div>';
                    setTimeout(function () { if (msg) msg.innerHTML = ''; }, 5000);
                }
                // Now save the new path — update tracking only on success.
                // Set the re-entrancy guard so doSaveSettings() won't re-trigger this dialog.
                _trashPathChangeHandled = true;
                doSaveSettings(payload, {
                    quiet: !!(options && options.quiet),
                    element: (options && options.element) || null,
                    onSuccess: function () {
                        _previousTrashPath = newPath;
                        if (options && typeof options.onSuccess === 'function') options.onSuccess();
                    },
                    onError: (options && options.onError) || undefined
                });
            }, function () {
                if (msg) msg.innerHTML = '<div class="error-msg">' + mi('error') + ' ' + escHtml(T('trashDeleteError', 'Failed to delete trash folders.')) + '</div>';
                if (saveBtn) saveBtn.disabled = false;
            });
        }));

        document.body.appendChild(d.overlay);
    }, function () {
        // API error checking old path — proceed with save anyway, update tracking on success.
        // Set the re-entrancy guard so doSaveSettings() won't re-trigger this dialog.
        _trashPathChangeHandled = true;
        doSaveSettings(payload, {
            quiet: !!(options && options.quiet),
            element: (options && options.element) || null,
            onSuccess: function () {
                _previousTrashPath = newPath;
                if (options && typeof options.onSuccess === 'function') options.onSuccess();
            },
            onError: (options && options.onError) || undefined
        });
    });
}

function saveSettings() {
    var btn = document.getElementById('btnSaveSettings');
    var msg = document.getElementById('settingsMsg');
    btn.disabled = true;
    msg.innerHTML = '';

    var payload = buildSettingsPayload();

    // Validate trash folder path before saving
    var trashError = validateTrashPath(payload.TrashFolderPath, payload.UseTrash);
    if (trashError) {
        showTrashPathError(trashError);
        btn.disabled = false;
        return;
    }
    showTrashPathError(null); // Clear any previous error

    // Check if trash is being disabled (was enabled, now unchecked)
    if (_wasTrashEnabled && !payload.UseTrash) {
        showTrashDisableDialog(payload);
        return;
    }

    // Trash path change detection is handled inside doSaveSettings() for all save paths.
    doSaveSettings(payload);
}
