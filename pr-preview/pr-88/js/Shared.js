// noinspection JSUnusedLocalSymbols,JSUnresolvedReference
var DONUT_COLORS = [
    '#00a4dc', '#e67e22', '#2ecc71', '#e74c3c', '#9b59b6',
    '#f1c40f', '#1abc9c', '#3498db', '#e91e63', '#ff9800',
    '#795548', '#607d8b', '#8bc34a', '#00bcd4', '#ff5722'
];

var SVG = {
    EYE: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">'
        + '<ellipse cx="12" cy="12" rx="10" ry="6" fill="none" stroke="currentColor" stroke-width="2"/>'
        + '<circle cx="12" cy="12" r="3.5" fill="currentColor"/>'
        + '</svg>',
    REFRESH: '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor">'
        + '<path d="M17.65 6.35A7.958 7.958 0 0 0 12 4C7.58 4 4.01 7.58 4.01 12S7.58 20 12 20c3.73 0 6.84-2.55 7.73-6h-2.08A5.99 5.99 0 0 1 12 18c-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z"/>'
        + '</svg>'
};

/**
 * Reads a CSS custom property from :root.
 * Falls back to the provided default if the property is not set.
 */
function getCssVar(name, fallback) {
    var v = getComputedStyle(document.documentElement).getPropertyValue(name);
    return (v && v.trim()) || fallback || '';
}

// Translation helper — loaded async from /JellyfinHelper/Translations
var _translations = {};

function T(key, fallback) {
    return Object.prototype.hasOwnProperty.call(_translations, key)
        ? _translations[key]
        : (fallback || key);
}

function loadTranslations(callback) {
    try {
        apiGet('JellyfinHelper/Translations', function (t) {
            _translations = t || {};
            if (callback) callback();
        }, function () {
            _translations = {};
            if (callback) callback();
        });
    } catch (e) {
        _translations = {};
        if (callback) callback();
    }
}

function applyStaticTranslations() {
    var btnScanLibraries = document.getElementById('btnScanLibraries');
    if (btnScanLibraries) {
        var scanLabel = T('scanLibraries', 'Scan Libraries');
        btnScanLibraries.title = scanLabel;
        btnScanLibraries.setAttribute('aria-label', scanLabel);
    }

    var loadingText = document.querySelector('#loadingIndicator p');
    if (loadingText) loadingText.textContent = T('scanDescription', 'Scanning libraries\u2026 This may take a while for large collections.');
    var placeholder = document.querySelector('#statsPlaceholder p');
    if (placeholder) placeholder.innerHTML = T('scanPlaceholder', 'Click <strong>Scan Libraries</strong> to analyze your media folders.');
}

function formatBytes(bytes) {
    if (!Number.isFinite(bytes)) return '0 B';
    if (bytes === 0) return '0 B';
    if (bytes < 0) return '-' + formatBytes(-bytes);
    var units = ['B', 'KB', 'MB', 'GB', 'TB'];
    var i = Math.floor(Math.log(bytes) / Math.log(1024));
    if (i < 0) i = 0;
    if (i >= units.length) i = units.length - 1;
    return (bytes / Math.pow(1024, i)).toFixed(2) + ' ' + units[i];
}

function escAttr(s) {
    return (s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
}

function escHtml(s) {
    return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

function getPathSegments(fullPath, rootPaths) {
    if (!fullPath) return [];
    var normalized = fullPath.replace(/\\/g, '/');

    var bestRoot = '';
    for (var i = 0; i < rootPaths.length; i++) {
        var root = rootPaths[i].replace(/\\/g, '/').replace(/\/+$/, '');
        var matchesRoot = normalized === root || normalized.startsWith(root + '/');
        if (matchesRoot && root.length > bestRoot.length) {
            bestRoot = root;
        }
    }

    if (bestRoot) {
        var offset = bestRoot.length;
        if (normalized[offset] === '/') offset++;
        normalized = normalized.substring(offset);
    }

    return normalized.split('/').filter(function (s) {
        return s.length > 0;
    });
}

// Builds a nested tree structure from a list of paths
function buildPathTree(paths, rootPaths) {
    var root = {name: 'root', children: {}, items: []};
    for (var i = 0; i < paths.length; i++) {
        var path = paths[i];
        var segments = getPathSegments(path, rootPaths || []);
        var currentNode = root;

        for (var j = 0; j < segments.length - 1; j++) {
            var segment = segments[j];
            if (!currentNode.children[segment]) {
                currentNode.children[segment] = {name: segment, children: {}, items: []};
            }
            currentNode = currentNode.children[segment];
        }

        var leafName = segments.length > 0 ? segments[segments.length - 1] : path;
        currentNode.items.push({name: leafName, fullPath: path});
    }
    return root;
}

function countTreeItems(node) {
    var count = node.items.length;
    for (var childName in node.children) {
        if (Object.prototype.hasOwnProperty.call(node.children, childName)) {
            count += countTreeItems(node.children[childName]);
        }
    }
    return count;
}

function renderTreeLevel(node, level, icon) {
    var html = '';
    var sortedChildren = Object.keys(node.children).sort();

    for (var i = 0; i < sortedChildren.length; i++) {
        var childName = sortedChildren[i];
        var childNode = node.children[childName];
        var hasContent = Object.keys(childNode.children).length > 0 || childNode.items.length > 0;

        html += '<div class="tree-node">';
        html += '<div class="tree-folder' + (hasContent ? ' tree-toggle' : '') + '" tabindex="0" role="button" aria-expanded="false" onclick="this.parentElement.classList.toggle(\'tree-expanded\');this.setAttribute(\'aria-expanded\',this.parentElement.classList.contains(\'tree-expanded\'))" onkeydown="if(event.key===\'Enter\'||event.key===\' \'){event.preventDefault();this.click()}">';
        html += '<span class="tree-icon">' + (hasContent ? '📁' : '📂') + '</span>';
        html += '<span class="tree-name">' + escHtml(childName) + '</span> <span class="tree-name-count">(' + countTreeItems(childNode) + ')</span>';
        html += '</div>';

        if (hasContent) {
            html += '<div class="tree-children">';
            html += renderTreeLevel(childNode, level + 1, icon);
            html += '</div>';
        }
        html += '</div>';
    }

    for (var j = 0; j < node.items.length; j++) {
        var item = node.items[j];
        html += '<div class="tree-leaf" title="' + escAttr(item.fullPath) + '">';
        html += '<span class="tree-leaf-icon">' + icon + '</span>';
        html += '<span class="tree-leaf-file-name">' + escHtml(item.name) + '</span>';
        html += '</div>';
    }

    return html;
}

// Render a file list panel grouped by media type (movies, tvShows, music)
// result: { movies: string[], tvShows: string[], music: string[], rootPaths: { movies: string[], tvShows: string[], music: string[], other: string[] } }
// title: string displayed in the header
function renderFileTree(result, title) {
    var hasMovies = result.movies && result.movies.length > 0;
    var hasTvShows = result.tvShows && result.tvShows.length > 0;
    var hasMusic = result.music && result.music.length > 0;
    var hasOther = result.other && result.other.length > 0;
    var totalFiles = (result.movies ? result.movies.length : 0) + (result.tvShows ? result.tvShows.length : 0) + (result.music ? result.music.length : 0) + (result.other ? result.other.length : 0);

    if (totalFiles === 0) {
        return '<div class="file-tree-empty">' + T('noFilesFound', 'No files found.') + '</div>';
    }

    var sectionCount = (hasMovies ? 1 : 0) + (hasTvShows ? 1 : 0) + (hasMusic ? 1 : 0) + (hasOther ? 1 : 0);
    var html = '<div class="file-tree-header">';
    html += '<span class="file-tree-title">' + escHtml(title) + '</span>';
    html += '<div style="display:flex;gap:0.5em;align-items:center;">';
    html += '<button class="tree-action-btn" onclick="var nodes=this.closest(\'.file-tree-panel\').querySelectorAll(\'.tree-node\');for(var i=0;i<nodes.length;i++){nodes[i].classList.add(\'tree-expanded\');var t=nodes[i].querySelector(\'.tree-toggle\');if(t)t.setAttribute(\'aria-expanded\',\'true\')}">' + T('expandAll', 'Expand All') + '</button>';
    html += '<button class="tree-action-btn" onclick="var nodes=this.closest(\'.file-tree-panel\').querySelectorAll(\'.tree-node\');for(var i=0;i<nodes.length;i++){nodes[i].classList.remove(\'tree-expanded\');var t=nodes[i].querySelector(\'.tree-toggle\');if(t)t.setAttribute(\'aria-expanded\',\'false\')}">' + T('collapseAll', 'Collapse All') + '</button>';
    html += '<span class="file-tree-count">' + totalFiles + ' ' + (totalFiles === 1 ? T('file', 'file') : T('files', 'files')) + '</span>';
    html += '</div></div>';

    html += '<div class="file-tree-columns' + (sectionCount > 1 ? ' file-tree-multi' : '') + '">';

    var roots = result.rootPaths || {};

    if (hasMovies) {
        html += '<div class="file-tree-section">';
        html += '<div class="file-tree-section-header"><span class="badge badge-movies">' + T('movies', 'Movies') + '</span> <span class="file-tree-section-count">(' + result.movies.length + ')</span></div>';
        html += '<div class="tree-view">';
        html += renderTreeLevel(buildPathTree(result.movies, roots.movies), 0, '🎬');
        html += '</div></div>';
    }

    if (hasTvShows) {
        html += '<div class="file-tree-section">';
        html += '<div class="file-tree-section-header"><span class="badge badge-tvshows">' + T('tvShows', 'TV Shows') + '</span> <span class="file-tree-section-count">(' + result.tvShows.length + ')</span></div>';
        html += '<div class="tree-view">';
        html += renderTreeLevel(buildPathTree(result.tvShows, roots.tvShows), 0, '📺');
        html += '</div></div>';
    }

    if (hasMusic) {
        html += '<div class="file-tree-section">';
        html += '<div class="file-tree-section-header"><span class="badge badge-music">' + T('music', 'Music') + '</span> <span class="file-tree-section-count">(' + result.music.length + ')</span></div>';
        html += '<div class="tree-view">';
        html += renderTreeLevel(buildPathTree(result.music, roots.music), 0, '🎵');
        html += '</div></div>';
    }

    if (hasOther) {
        html += '<div class="file-tree-section">';
        html += '<div class="file-tree-section-header"><span class="badge badge-other">' + T('other', 'Other') + '</span> <span class="file-tree-section-count">(' + result.other.length + ')</span></div>';
        html += '<div class="tree-view">';
        html += renderTreeLevel(buildPathTree(result.other, roots.other), 0, '📄');
        html += '</div></div>';
    }

    html += '</div>';
    return html;
}

// Aggregate dictionaries across libraries
function aggregateDict(libraries, prop) {
    var result = {};
    for (var i = 0; i < libraries.length; i++) {
        var dict = libraries[i][prop];
        if (dict) {
            for (var key in dict) {
                if (Object.prototype.hasOwnProperty.call(dict, key)) {
                    result[key] = (result[key] || 0) + dict[key];
                }
            }
        }
    }
    return result;
}

/**
 * Reusable auto-save feedback indicator.
 * Shows a brief ✔ or ✘ on top of the given element, then fades out.
 * Can be attached to any element — the indicator is inserted as an overlay.
 * @param {HTMLElement} element - The element to show the indicator over.
 * @param {boolean} [success=true] - true = green ✔, false = red ✘
 */
function showAutoSaveIndicatorOverlay(element, success) {
    if (!element || !element.parentNode) return;

    removeExistingSaveIndicatorOverlay(element);

    const fadeDelay = calculateFadeDelay(success);
    const indicator = createSaveIndicator(element, success);

    const indicatorContainer = document.createElement('div');
    indicatorContainer.style.position = 'absolute';
    indicatorContainer.style.top = getComputedStyle(element).marginTop;
    indicatorContainer.style.width = element.offsetWidth + 'px';
    indicatorContainer.style.height = element.offsetHeight + 'px';
    indicatorContainer.style.display = 'flex';
    indicatorContainer.style.alignItems = 'center';
    indicatorContainer.style.justifyContent = 'flex-end';
    indicatorContainer.style.pointerEvents = 'none';
    indicatorContainer.style.boxSizing = 'border-box';
    indicatorContainer.style.paddingRight = '20px';
    indicatorContainer.append(indicator);

    const emptyContainer = document.createElement('div');
    emptyContainer.style.position = 'relative';
    emptyContainer.append(indicatorContainer);

    addFadingDelay(emptyContainer, fadeDelay);

    element.before(emptyContainer);
}

/**
 * Removes an existing save indicator overlay if it is present as the previous sibling of the specified element.
 * The overlay is identified by having the 'fade-element' class.
 * Any associated fade or removal timers are cleared before the overlay is removed from the DOM.
 *
 * @param {HTMLElement} element - The element whose previous sibling will be checked and removed if it matches the criteria.
 * @return {void} This function does not return any value.
 */
function removeExistingSaveIndicatorOverlay(element) {
    const existing = element.previousElementSibling;

    if (existing && existing.classList.contains('fade-element')) {
        clearTimeout(existing._fadeTimer);
        clearTimeout(existing._removeTimer);
        existing.remove();
    }
}

/**
 * Creates a save indicator element with the specified success status.
 * The indicator is styled with a green ✔ for success and a red ✘ for failure.
 *
 * @param {HTMLElement} element The parent element to which the indicator will be attached.
 * @param {boolean} [success=true] - true = green ✔, false = red ✘
 * @return {HTMLElement} The created save indicator element.
 */
function createSaveIndicator(element, success) {
    let indicator = document.createElement('span');
    indicator.style.fontSize = '0.95em';
    indicator.style.color = success !== false ? getCssVar('--color-success', '#2ecc71') : getCssVar('--color-danger', '#e74c3c');
    indicator.textContent = success !== false ? '✔' : '✘';

    return indicator;
}

/**
 * Applies a fading effect to a given element with a specified delay.
 * The element will fade out after the provided delay, and then be removed
 * from the DOM shortly after the fade-out completes.
 *
 * @param {HTMLElement} element - The DOM element to which the fading effect will be applied.
 * @param {number} fadeDelay - The delay in milliseconds before the element starts fading out.
 * @return {void} This method does not return a value.
 */
function addFadingDelay(element, fadeDelay) {
    element.style.transition = 'opacity 0.4s';
    element.style.opacity = '1';

    // Force reflow then fade in
    void element.offsetWidth;
    element.classList.add('fade-element');

    element._fadeTimer = setTimeout(() => element.style.opacity = '0', fadeDelay);
    element._removeTimer = setTimeout(() => {
        if (element.parentNode) {
            element.remove();
        }
    }, fadeDelay + 500);
}

/**
 * Calculates the fade delay based on the success status.
 * Returns 2000ms for success, 3000ms for failure.
 *
 * @param {boolean} success - true for success, false for failure.
 * @return {number} The calculated fade delay in milliseconds.
 */
function calculateFadeDelay(success) {
    return success !== false ? 2000 : 3000;
}

/**
 * Reusable button feedback for success / error states.
 * Switches the button content to a ✔ or ✘ icon + message, adds a CSS class,
 * then resets to the original content after a timeout.
 *
 * @param {HTMLElement} btn - The button element.
 * @param {boolean} success - true = green success, false = red error.
 * @param {string} message - Text to display alongside the icon.
 * @param {string} originalHtml - HTML to restore after the timeout.
 * @param {number} [timeout] - ms before reset (default: 3000 for success, 5000 for error).
 * @returns {number} The timer ID so callers can clear it if needed.
 */
function showButtonFeedback(btn, success, message, originalHtml, timeout) {
    if (!btn) return 0;
    var icon = success ? '✔' : '✘';
    var cls = success ? 'success' : 'error';
    var delay = timeout || (success ? 3000 : 5000);
    btn.classList.remove('success', 'error');
    btn.innerHTML = '<span class="btn-icon">' + icon + '</span>' + message;
    btn.classList.add(cls);
    return setTimeout(function () {
        btn.innerHTML = originalHtml;
        btn.classList.remove('success', 'error');
    }, delay);
}

// ============================================================
// API Wrapper — centralizes ApiClient.ajax() calls
// ============================================================

/**
 * Default error handler for API calls — logs to console so failures are never silent.
 */
function _apiDefaultError(method, path) {
    return function (err) {
        console.error('JellyfinHelper ' + method + ' failed: ' + path, err);
    };
}

/**
 * Perform a GET request to a JellyfinHelper endpoint.
 * @param {string} path - Relative API path (e.g. 'JellyfinHelper/Configuration').
 * @param {function} onSuccess - Callback with parsed JSON data.
 * @param {function} [onError] - Optional error callback (defaults to console.error).
 */
function apiGet(path, onSuccess, onError) {
    var c = ApiClient;
    c.ajax({type: 'GET', url: c.getUrl(path), dataType: 'json'}).then(
        onSuccess || function () {
        },
        onError || _apiDefaultError('GET', path)
    );
}

/**
 * Perform a POST request to a JellyfinHelper endpoint.
 * @param {string} path - Relative API path.
 * @param {Object|string} payload - Data to send (will be JSON-stringified if object).
 * @param {function} onSuccess - Callback with response data.
 * @param {function} [onError] - Optional error callback (defaults to console.error).
 */
function apiPost(path, payload, onSuccess, onError) {
    var c = ApiClient;
    var body = typeof payload === 'string' ? payload : JSON.stringify(payload);
    c.ajax({type: 'POST', url: c.getUrl(path), data: body, contentType: 'application/json', dataType: 'json'}).then(
        onSuccess || function () {
        },
        onError || _apiDefaultError('POST', path)
    );
}

/**
 * Perform a PUT request to a JellyfinHelper endpoint.
 * @param {string} path - Relative API path.
 * @param {Object|string} payload - Data to send (will be JSON-stringified if object).
 * @param {function} onSuccess - Callback with response data.
 * @param {function} [onError] - Optional error callback (defaults to console.error).
 */
function apiPut(path, payload, onSuccess, onError) {
    var c = ApiClient;
    var body = typeof payload === 'string' ? payload : JSON.stringify(payload);
    c.ajax({type: 'PUT', url: c.getUrl(path), data: body, contentType: 'application/json'}).then(
        onSuccess || function () {
        },
        onError || _apiDefaultError('PUT', path)
    );
}

/**
 * Perform a DELETE request to a JellyfinHelper endpoint.
 * @param {string} path - Relative API path.
 * @param {function} onSuccess - Callback with response data.
 * @param {function} [onError] - Optional error callback (defaults to console.error).
 */
function apiDelete(path, onSuccess, onError) {
    var c = ApiClient;
    c.ajax({type: 'DELETE', url: c.getUrl(path), dataType: 'json'}).then(
        onSuccess || function () {
        },
        onError || _apiDefaultError('DELETE', path)
    );
}

/**
 * Perform a GET request that returns plain text (not JSON).
 * @param {string} path - Relative API path.
 * @param {function} onSuccess - Callback with text data.
 * @param {function} [onError] - Optional error callback (defaults to console.error).
 */
function apiGetText(path, onSuccess, onError) {
    var c = ApiClient;
    c.ajax({type: 'GET', url: c.getUrl(path), dataType: 'text'}).then(
        onSuccess || function () {
        },
        onError || _apiDefaultError('GET(text)', path)
    );
}

/**
 * Perform a POST request with a raw (pre-serialized) body.
 * Unlike apiPost, this does NOT set dataType:'json' on the response,
 * so the caller receives the raw response from the server.
 * @param {string} path - Relative API path.
 * @param {string} rawBody - Already serialized request body.
 * @param {string} contentType - MIME type (e.g. 'application/json').
 * @param {function} onSuccess - Callback with response data.
 * @param {function} [onError] - Optional error callback (defaults to console.error).
 */
function apiPostRaw(path, rawBody, contentType, onSuccess, onError) {
    var c = ApiClient;
    c.ajax({type: 'POST', url: c.getUrl(path), data: rawBody, contentType: contentType}).then(
        onSuccess || function () {
        },
        onError || _apiDefaultError('POST', path)
    );
}

/**
 * Perform a GET request where 204 No Content is a valid (expected) response.
 * Uses the native fetch API so we can inspect the HTTP status code.
 * @param {string} path - Relative API path.
 * @param {function} onSuccess - Callback with parsed JSON data (HTTP 200).
 * @param {function} onNoContent - Callback when server returns 204 (no data yet).
 * @param {function} [onError] - Optional error callback for network/server errors.
 */
function apiGetOptional(path, onSuccess, onNoContent, onError) {
    var c = ApiClient;
    var errHandler = onError || _apiDefaultError('GET', path);
    fetch(c.getUrl(path), {
        headers: {'Authorization': 'MediaBrowser Token="' + c.accessToken() + '"'}
    }).then(function (response) {
        if (response.status === 204) {
            if (onNoContent) onNoContent();
            return;
        }
        if (!response.ok) throw new Error('HTTP ' + response.status);
        return response.json().then(onSuccess || function () {
        });
    }).catch(errHandler);
}

/**
 * Fetch a resource as a Blob (e.g. file downloads).
 * Uses the native fetch API with Jellyfin auth header since ApiClient.ajax
 * does not support blob responses.
 * @param {string} path - Relative API path (may include query string).
 * @param {function} onSuccess - Callback with the Blob.
 * @param {function} [onError] - Optional error callback (defaults to console.error).
 */
function apiFetchBlob(path, onSuccess, onError) {
    var c = ApiClient;
    var errHandler = onError || _apiDefaultError('FETCH', path);
    fetch(c.getUrl(path), {
        headers: {'Authorization': 'MediaBrowser Token="' + c.accessToken() + '"'}
    }).then(function (response) {
        if (!response.ok) throw new Error('HTTP ' + response.status);
        return response.blob();
    }).then(
        onSuccess || function () {
        }
    ).catch(errHandler);
}

// ============================================================
// Pluralize helper
// ============================================================

/**
 * Return singular or plural translation based on count.
 * @param {number} count
 * @param {string} singularKey - Translation key for singular.
 * @param {string} pluralKey - Translation key for plural.
 * @param {string} [singularFallback]
 * @param {string} [pluralFallback]
 * @returns {string}
 */
function pluralize(count, singularKey, pluralKey, singularFallback, pluralFallback) {
    return count === 1
        ? T(singularKey, singularFallback || singularKey)
        : T(pluralKey, pluralFallback || pluralKey);
}

// ============================================================
// Format a UTC timestamp as "X ago" relative text
// ============================================================

function formatTimeAgo(utcTimestamp) {
    if (!utcTimestamp) return '';
    var then = new Date(utcTimestamp);
    var now = new Date();
    var diffMs = now - then;
    if (diffMs < 0) return '';
    var diffMin = Math.floor(diffMs / 60000);
    if (diffMin < 1) return T('justNow', 'just now');
    if (diffMin < 60) return diffMin + ' ' + pluralize(diffMin, 'minuteAgo', 'minutesAgo', 'min ago', 'min ago');
    var diffH = Math.floor(diffMin / 60);
    if (diffH < 24) return diffH + ' ' + pluralize(diffH, 'hourAgo', 'hoursAgo', 'hour ago', 'hours ago');
    var diffD = Math.floor(diffH / 24);
    return diffD + ' ' + pluralize(diffD, 'dayAgo', 'daysAgo', 'day ago', 'days ago');
}

// ============================================================
// Shared scan data — single source of truth for last scan result
// ============================================================

// noinspection JSUnusedGlobalSymbols
var _lastScanResult = null;

// ============================================================
// Collect paths from a list of libraries for a given property.
// Works for both flat arrays (Health) and keyed dictionaries (Codecs).
// ============================================================

/**
 * Collect flat path arrays from libraries.
 * @param {Array} libraries - Array of library objects.
 * @param {string} prop - Property name containing a string array.
 * @returns {string[]}
 */
function collectFlatPaths(libraries, prop) {
    var paths = [];
    if (libraries) {
        for (var i = 0; i < libraries.length; i++) {
            var libPaths = libraries[i][prop];
            if (libPaths) {
                for (var j = 0; j < libPaths.length; j++) {
                    paths.push(libPaths[j]);
                }
            }
        }
    }
    return paths;
}

/**
 * Collect paths from a dictionary property keyed by codec/format name.
 * @param {Array} libraries - Array of library objects.
 * @param {string} prop - Property name containing { key: string[] }.
 * @param {string} key - The dictionary key to collect for.
 * @returns {string[]}
 */
function collectDictPaths(libraries, prop, key) {
    var paths = [];
    if (libraries) {
        for (var i = 0; i < libraries.length; i++) {
            var dict = libraries[i][prop];
            if (dict && dict[key]) {
                for (var j = 0; j < dict[key].length; j++) {
                    paths.push(dict[key][j]);
                }
            }
        }
    }
    return paths;
}

// ============================================================
// Dialog Helpers — reusable modal dialogs
// ============================================================

/**
 * Creates a modal dialog overlay with title, body, and button row.
 * Returns { overlay, dialog, btnRow } so callers can add buttons.
 */
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

    return {overlay: overlay, dialog: dialog, btnRow: btnRow};
}

/**
 * Creates a styled dialog button.
 * style: 'cancel' (transparent), 'danger' (#e74c3c), 'success' (#2ecc71), 'warning' (#00a4dc)
 */
function createDialogBtn(text, style, onclick) {
    var btn = document.createElement('button');
    btn.textContent = text;
    var bg = style === 'cancel' ? 'transparent' : style === 'danger' ? getCssVar('--color-danger', '#e74c3c') : style === 'success' ? getCssVar('--color-success', '#2ecc71') : getCssVar('--color-primary', '#00a4dc');
    var border = style === 'cancel' ? '1px solid rgba(255,255,255,0.2)' : 'none';
    btn.style.cssText = 'padding:0.5em 1.2em;border:' + border + ';border-radius:4px;background:' + bg + ';color:#fff;cursor:pointer;font-size:0.9em;';
    btn.onclick = onclick;
    return btn;
}

function removeDialogById(id) {
    var existing = document.getElementById(id);
    if (existing) existing.remove();
}

// ============================================================
// Generic toggle-panel click handler
// Used by Codecs (codec rows) and Health (health items) for
// expanding/collapsing detail panels with file trees.
// ============================================================

/**
 * Attach click handlers to clickable items that toggle a detail panel.
 *
 * @param {Object} opts
 * @param {string} opts.itemSelector - CSS selector for the clickable items (e.g. '.codec-clickable').
 * @param {string} opts.activeClass - CSS class toggled on the active item (e.g. 'codec-row-active').
 * @param {string} opts.groupAttr - Data attribute used to group items (e.g. 'data-chart'). Optional.
 * @param {string} opts.typeAttr - Data attribute identifying the item type/value (e.g. 'data-codec').
 * @param {function} opts.getPanelId - Function(item) returning the panel element ID.
 * @param {function} opts.renderContent - Function(item) returning the HTML to put in the panel.
 */
function attachTogglePanelHandlers(opts) {
    var items = document.querySelectorAll(opts.itemSelector);
    for (var i = 0; i < items.length; i++) {
        if (items[i].dataset.toggleBound) continue;
        items[i].dataset.toggleBound = '1';
        items[i].addEventListener('keydown', function (e) {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                this.click();
            }
        });
        items[i].addEventListener('click', function () {
            var panelId = opts.getPanelId(this);
            var panel = document.getElementById(panelId);
            if (!panel) return;

            // Toggle: if same item is already active, close it
            if (this.classList.contains(opts.activeClass)) {
                panel.innerHTML = '';
                panel.classList.remove('file-tree-panel-visible');
                this.classList.remove(opts.activeClass);
                return;
            }

            // Remove active state from sibling items
            var groupVal = opts.groupAttr ? this.getAttribute(opts.groupAttr) : null;
            var allItems = document.querySelectorAll(opts.itemSelector);
            for (var j = 0; j < allItems.length; j++) {
                var sameGroup = !opts.groupAttr || allItems[j].getAttribute(opts.groupAttr) === groupVal;
                if (sameGroup) allItems[j].classList.remove(opts.activeClass);
            }

            // Close all other panels
            var allPanels = document.querySelectorAll('.file-tree-panel');
            for (var p = 0; p < allPanels.length; p++) {
                if (allPanels[p].id !== panelId) {
                    allPanels[p].innerHTML = '';
                    allPanels[p].classList.remove('file-tree-panel-visible');
                }
            }
            // Deactivate items in other groups
            if (opts.groupAttr) {
                for (var r = 0; r < allItems.length; r++) {
                    if (allItems[r].getAttribute(opts.groupAttr) !== groupVal) {
                        allItems[r].classList.remove(opts.activeClass);
                    }
                }
            }

            // Track whether panel was already visible (content switch vs. fresh open)
            var wasVisible = panel.classList.contains('file-tree-panel-visible');

            this.classList.add(opts.activeClass);
            panel.innerHTML = opts.renderContent(this);
            panel.classList.add('file-tree-panel-visible');

            // Scroll when: fresh panel open OR forced by donut click (user clicked far above panel)
            var forceScroll = typeof _forceScrollOnPanelOpen !== 'undefined' && _forceScrollOnPanelOpen;
            if (forceScroll) {
                _forceScrollOnPanelOpen = false;
            }
            if (!wasVisible || forceScroll) {
                var scrollPanel = panel;
                setTimeout(function () {
                    scrollPanel.scrollIntoView({behavior: 'smooth', block: 'nearest'});
                }, 50);
            }
        });
    }
}

// --- Finding 3: Resolve Arr instances with legacy single-instance fallback ---
function resolveArrInstances(cfg, type) {
    var key = type + 'Instances';     // e.g. RadarrInstances
    var urlKey = type + 'Url';        // e.g. RadarrUrl
    var apiKeyKey = type + 'ApiKey';  // e.g. RadarrApiKey
    if (cfg[key] && cfg[key].length > 0) return cfg[key];
    if (cfg[urlKey]) return [{Name: type, Url: cfg[urlKey], ApiKey: cfg[apiKeyKey]}];
    return [];
}
