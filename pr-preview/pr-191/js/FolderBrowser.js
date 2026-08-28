// This file adds a server-side folder picker to the Trash Folder Path setting.
// It hooks into the existing Settings.js by attaching to the Browse button after render.
'use strict';

/**
 * Initializes the folder browser functionality.
 * Called after the settings form is rendered to attach the browse button handler.
 * Must be called from loadSettings() after form.innerHTML is set.
 */
function initFolderBrowser() {
    var btn = document.getElementById('btnBrowseTrash');
    if (!btn) return;
    btn.addEventListener('click', function () {
        openFolderBrowserDialog();
    });
}

/**
 * Opens the folder browser dialog modal.
 * Starts browsing at the current trash path if it's absolute, otherwise shows library roots.
 */
function openFolderBrowserDialog() {
    removeDialogById('folderBrowserOverlay');

    var currentPath = (document.getElementById('cfgTrashPath') || {}).value || '';

    // Build the dialog shell
    var overlay = document.createElement('div');
    overlay.id = 'folderBrowserOverlay';
    overlay.className = 'dialog-overlay';
    overlay.style.cssText = 'position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,0.7);z-index:99999;display:flex;align-items:center;justify-content:center;';

    var dialog = document.createElement('div');
    dialog.className = 'dialog-content';
    dialog.setAttribute('role', 'dialog');
    dialog.setAttribute('aria-modal', 'true');
    dialog.setAttribute('aria-labelledby', 'folderBrowserTitle');
    dialog.style.cssText = 'background:var(--theme-card-bg, #1c1c1e);border-radius:10px;padding:1.5em;max-width:600px;width:90%;max-height:80vh;display:flex;flex-direction:column;box-shadow:0 8px 32px rgba(0,0,0,0.5);';

    // Header
    var header = document.createElement('div');
    header.style.cssText = 'display:flex;align-items:center;justify-content:space-between;margin-bottom:1em;';
    header.innerHTML = '<h3 id="folderBrowserTitle" style="margin:0;font-size:1.1em;display:flex;align-items:center;gap:0.4em;"><span style="color:#00a4dc;">' + mi('folder_open') + '</span>' + escHtml(T('trashBrowseTitle', 'Select Trash Folder')) + '</h3>'
        + '<button type="button" id="folderBrowserClose" style="background:none;border:none;color:inherit;font-size:1.5em;cursor:pointer;padding:0.2em;line-height:1;opacity:0.7;" aria-label="' + escAttr(T('close', 'Close')) + '">&times;</button>';
    dialog.appendChild(header);

    // Breadcrumb / current path display
    var breadcrumb = document.createElement('div');
    breadcrumb.id = 'folderBrowserBreadcrumb';
    breadcrumb.style.cssText = 'font-size:0.82em;opacity:0.7;margin-bottom:0.5em;word-break:break-all;min-height:1.2em;';
    dialog.appendChild(breadcrumb);

    // Quick jump: library roots
    var quickJump = document.createElement('div');
    quickJump.id = 'folderBrowserQuickJump';
    quickJump.style.cssText = 'margin-bottom:0.8em;display:none;';
    dialog.appendChild(quickJump);

    // Directory listing
    var listing = document.createElement('div');
    listing.id = 'folderBrowserListing';
    listing.style.cssText = 'flex:1;overflow-y:auto;border:1px solid rgba(255,255,255,0.1);border-radius:6px;min-height:200px;max-height:45vh;';
    dialog.appendChild(listing);

    // New folder name input
    var newFolderRow = document.createElement('div');
    newFolderRow.style.cssText = 'margin-top:0.8em;';
    newFolderRow.innerHTML = '<label style="font-size:0.82em;opacity:0.8;">' + escHtml(T('trashBrowseCreateNew', 'Or type a new folder name:')) + '</label>'
        + '<input type="text" id="folderBrowserNewName" placeholder=".jellyfin-trash" style="width:100%;margin-top:0.3em;padding:0.4em 0.6em;border-radius:4px;border:1px solid rgba(255,255,255,0.2);background:rgba(0,0,0,0.2);color:inherit;font-size:0.9em;">';
    dialog.appendChild(newFolderRow);

    // Action buttons
    var btnRow = document.createElement('div');
    btnRow.style.cssText = 'display:flex;gap:0.5em;justify-content:flex-end;margin-top:1em;';
    btnRow.innerHTML = '<button type="button" class="action-btn" id="folderBrowserCancel" style="padding:0.4em 1em;">' + escHtml(T('cancel', 'Cancel')) + '</button>'
        + '<button type="button" class="action-btn" id="folderBrowserSelect" style="padding:0.4em 1em;background:#00a4dc;color:#fff;">' + escHtml(T('trashBrowseSelect', 'Select This Folder')) + '</button>';
    dialog.appendChild(btnRow);

    overlay.appendChild(dialog);
    document.body.appendChild(overlay);

    // Store state
    var state = { currentPath: null };

    // Close handler shared across all dismiss paths
    function closeDialog() {
        removeDialogById('folderBrowserOverlay');
    }

    // Event handlers
    document.getElementById('folderBrowserClose').addEventListener('click', closeDialog);
    document.getElementById('folderBrowserCancel').addEventListener('click', closeDialog);
    overlay.addEventListener('click', function (e) {
        if (e.target === overlay) closeDialog();
    });

    // Escape key support - listener is scoped to the overlay element.
    // When the overlay is removed from the DOM, this listener is automatically cleaned up
    // (no orphan listeners on document).
    overlay.setAttribute('tabindex', '-1');
    overlay.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
            e.preventDefault();
            closeDialog();
        }
    });
    overlay.focus();

    document.getElementById('folderBrowserSelect').addEventListener('click', function () {
        var newName = (document.getElementById('folderBrowserNewName') || {}).value || '';
        var selectedPath = state.currentPath || '';
        if (newName.trim()) {
            // Append the new folder name to the current path
            var sep = selectedPath.includes('/') ? '/' : (selectedPath.includes('\\') ? '\\' : '/');
            if (selectedPath && !selectedPath.endsWith(sep) && !selectedPath.endsWith('/') && !selectedPath.endsWith('\\')) {
                selectedPath += sep;
            }
            selectedPath += newName.trim();
        }
        if (selectedPath) {
            // Validate path locally before attempting save - shows specific error in picker
            var pathError = validateTrashPath(selectedPath, true);
            if (pathError) {
                var listingEl = document.getElementById('folderBrowserListing');
                if (listingEl) {
                    listingEl.innerHTML = '<div style="padding:1em;text-align:center;color:var(--color-error,#e74c3c);">' + mi('error') + ' ' + escHtml(pathError) + '</div>';
                }
                return;
            }

            // Close the folder browser immediately so any subsequent dialogs
            // (e.g. the trash relocation dialog) appear in the foreground.
            closeDialog();

            // Update the input field with the selected path
            var input = document.getElementById('cfgTrashPath');
            if (input) {
                input.value = selectedPath;
                input.dispatchEvent(new Event('input', { bubbles: true }));
            }

            // Trigger save.
            var payload = buildSettingsPayload();
            payload.TrashFolderPath = selectedPath;
            doSaveSettings(payload, {
                quiet: true,
                element: null,
                onSuccess: function () {
                    // Show success feedback on the browse button
                    var icon = document.getElementById('btnBrowseTrash');
                    if (!icon) return;
                    icon.innerHTML = mi('check_circle');
                    icon.style.color = getCssVar('--color-success', '#2ecc71');
                    icon.style.opacity = '1';
                    setTimeout(function () {
                        icon.innerHTML = mi('folder_open');
                        icon.style.color = '#00a4dc';
                        icon.style.opacity = '0.8';
                    }, 2000);
                }
            });
        } else {
            closeDialog();
        }
    });

    // Load library roots for quick jump
    loadLibraryPathsForBrowser(quickJump, state, listing, breadcrumb);

    // Start browsing
    if (currentPath && (
        currentPath.startsWith('/') ||
        currentPath.startsWith('\\\\') ||
        /^[A-Za-z]:[\\/]/.test(currentPath)
    )) {
        // Absolute path (Unix, UNC, or Windows drive with separator) - try to browse there
        browseTo(currentPath, listing, breadcrumb, state);
    } else {
        // Relative or empty - show roots
        browseTo(null, listing, breadcrumb, state);
    }
}

/**
 * Loads library paths and shows quick-jump buttons.
 */
function loadLibraryPathsForBrowser(quickJumpEl, state, listing, breadcrumb) {
    apiGet('JellyfinHelper/Configuration/LibraryPaths', function (data) {
        var paths = (data && (data.libraryPaths || data.LibraryPaths)) || [];
        if (paths.length === 0) return;

        var h = '<div style="font-size:0.8em;opacity:0.7;margin-bottom:0.3em;">' + escHtml(T('trashBrowseLibraryRoots', 'Library Roots')) + ':</div>';
        h += '<div style="display:flex;flex-wrap:wrap;gap:0.3em;">';
        for (var i = 0; i < paths.length; i++) {
            var itemPath = paths[i].path || paths[i].Path || '';
            var itemName = paths[i].name || paths[i].Name || '';
            h += '<button type="button" class="action-btn folder-browser-quick-btn" data-path="' + escAttr(itemPath) + '" style="padding:0.2em 0.6em;font-size:0.78em;display:inline-flex;align-items:center;gap:0.2em;"><span style="font-size:0.9em;">' + mi('folder') + '</span>' + escHtml(itemName) + '</button>';
        }
        h += '</div>';
        quickJumpEl.innerHTML = h;
        quickJumpEl.style.display = '';

        // Attach click handlers
        var btns = quickJumpEl.querySelectorAll('.folder-browser-quick-btn');
        for (var j = 0; j < btns.length; j++) {
            btns[j].addEventListener('click', function () {
                var p = this.dataset.path;
                if (p) browseTo(p, listing, breadcrumb, state);
            });
        }
    }, function () {
        // Silently ignore - quick jump is optional
    });
}

/**
 * Navigates the folder browser to the given path (or roots if null).
 */
function browseTo(path, listingEl, breadcrumbEl, state) {
    state.requestId = (state.requestId || 0) + 1;
    var requestId = state.requestId;

    listingEl.innerHTML = '<div style="padding:1em;text-align:center;opacity:0.6;"><span class="btn-spinner" style="display:inline-block;margin-right:0.5em;"></span>' + T('trashBrowseLoading', 'Loading\u2026') + '</div>';

    var url = 'JellyfinHelper/Configuration/BrowseFolders';
    if (path) url += '?path=' + encodeURIComponent(path);

    apiGet(url, function (result) {
        if (requestId !== state.requestId) return;
        state.currentPath = result.CurrentPath || result.currentPath || null;
        var parentPath = result.ParentPath || result.parentPath || null;
        var canGoUp = result.CanGoUp || result.canGoUp || false;
        var dirs = result.Directories || result.directories || [];
        var error = result.Error || result.error || null;

        // Clear selection when the server reports an error with no navigable directories,
        // preventing "Select This Folder" from persisting an inaccessible path.
        if (error && dirs.length === 0) {
            state.currentPath = null;
        }

        // Update breadcrumb
        breadcrumbEl.textContent = state.currentPath ? (T('trashBrowseCurrentPath', 'Current path') + ': ' + state.currentPath) : '';

        // Build listing
        var h = '';

        // Go up button
        if (canGoUp && parentPath) {
            h += '<div class="folder-browser-item folder-browser-up" data-path="' + escAttr(parentPath) + '" style="padding:0.5em 0.8em;cursor:pointer;display:flex;align-items:center;gap:0.5em;border-bottom:1px solid rgba(255,255,255,0.05);">';
            h += '<span style="font-size:1.1em;color:#00a4dc;">' + mi('expand_less') + '</span>';
            h += '<span style="opacity:0.8;">' + T('trashBrowseGoUp', 'Go up') + '</span>';
            h += '</div>';
        } else if (state.currentPath) {
            // Show "go to roots" when at top
            h += '<div class="folder-browser-item folder-browser-up" data-path="" style="padding:0.5em 0.8em;cursor:pointer;display:flex;align-items:center;gap:0.5em;border-bottom:1px solid rgba(255,255,255,0.05);">';
            h += '<span style="font-size:1.1em;color:#00a4dc;">' + mi('expand_less') + '</span>';
            h += '<span style="opacity:0.8;">' + T('trashBrowseGoUp', 'Go up') + '</span>';
            h += '</div>';
        }

        if (error && dirs.length === 0) {
            h += '<div style="padding:1em;text-align:center;opacity:0.6;">' + mi('error') + ' ' + escHtml(error) + '</div>';
        } else if (dirs.length === 0) {
            h += '<div style="padding:1em;text-align:center;opacity:0.5;">' + T('trashBrowseEmpty', 'This folder is empty.') + '</div>';
        } else {
            for (var i = 0; i < dirs.length; i++) {
                var dir = dirs[i];
                var dirName = dir.Name || dir.name || '';
                var dirPath = dir.Path || dir.path || '';
                var hasKids = dir.HasChildren || dir.hasChildren || false;
                h += '<div class="folder-browser-item" data-path="' + escAttr(dirPath) + '" style="padding:0.5em 0.8em;cursor:pointer;display:flex;align-items:center;gap:0.5em;border-bottom:1px solid rgba(255,255,255,0.05);transition:background 0.15s;">';
                h += '<span style="font-size:1.1em;color:' + (hasKids ? '#f39c12' : '#7f8c8d') + ';">' + mi(hasKids ? 'folder' : 'folder_open') + '</span>';
                h += '<span>' + escHtml(dirName) + '</span>';
                if (hasKids) h += '<span style="font-size:0.8em;opacity:0.4;margin-left:auto;">' + mi('expand_more') + '</span>';
                h += '</div>';
            }
        }

        if (error && dirs.length > 0) {
            h += '<div style="padding:0.5em 0.8em;font-size:0.82em;opacity:0.6;">' + mi('warning') + ' ' + escHtml(error) + '</div>';
        }

        listingEl.innerHTML = h;

        // Attach click/keyboard handlers to directory items
        var items = listingEl.querySelectorAll('.folder-browser-item');
        for (var j = 0; j < items.length; j++) {
            // Make items keyboard-operable (focusable + role)
            items[j].setAttribute('tabindex', '0');
            items[j].setAttribute('role', 'button');
            items[j].addEventListener('click', function () {
                var targetPath = this.dataset.path;
                if (targetPath === '') {
                    // Go to roots
                    browseTo(null, listingEl, breadcrumbEl, state);
                } else {
                    browseTo(targetPath, listingEl, breadcrumbEl, state);
                }
            });
            items[j].addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    this.click();
                }
            });
            // Hover/focus effect
            items[j].addEventListener('mouseenter', function () { this.style.background = 'rgba(0,164,220,0.1)'; });
            items[j].addEventListener('mouseleave', function () { this.style.background = ''; });
            items[j].addEventListener('focus', function () { this.style.background = 'rgba(0,164,220,0.1)'; });
            items[j].addEventListener('blur', function () { this.style.background = ''; });
        }
    }, function () {
        if (requestId !== state.requestId) return;
        state.currentPath = null;
        breadcrumbEl.textContent = '';
        listingEl.innerHTML = '<div style="padding:1em;text-align:center;">' + mi('error') + ' ' + T('trashBrowseError', 'Cannot access this directory.') + '</div>';
    });
}
