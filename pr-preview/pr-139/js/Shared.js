// noinspection JSUnusedLocalSymbols,JSUnresolvedReference
'use strict';
var DONUT_COLORS = [
    '#00a4dc', '#e67e22', '#2ecc71', '#e74c3c', '#9b59b6',
    '#f1c40f', '#1abc9c', '#3498db', '#e91e63', '#ff9800',
    '#795548', '#607d8b', '#8bc34a', '#00bcd4', '#ff5722'
];
var _forceScrollOnPanelOpen = false;

// --- Material Icon helper ---
// Returns an inline SVG icon. No external font/CDN required.
var _mi = {"assignment":"M19 3h-4.18C14.4 1.84 13.3 1 12 1c-1.3 0-2.4.84-2.82 2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm-7 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm2 14H7v-2h7v2zm3-4H7v-2h10v2zm0-4H7V7h10v2z","bar_chart":"M5 9.2h3V19H5V9.2zM10.6 5h2.8v14h-2.8V5zm5.6 8H19v6h-2.8v-6z","check_circle":"M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z","cleaning_services":"M16 11h-1V3c0-1.1-.9-2-2-2h-2c-1.1 0-2 .9-2 2v8H8c-2.76 0-5 2.24-5 5v7h18v-7c0-2.76-2.24-5-5-5z","dashboard":"M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z","delete":"M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z","description":"M14 2H6c-1.1 0-2 .9-2 2v16c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zM13 9V3.5L18.5 9H13z","download":"M5 20h14v-2H5v2zM19 9h-4V3H9v6H5l7 7 7-7z","edit_note":"M3 10h11v2H3v-2zm0-2h11V6H3v2zm0 8h7v-2H3v2zm15.01-3.13l.71-.71c.39-.39 1.02-.39 1.41 0l.71.71c.39.39.39 1.02 0 1.41l-.71.71-2.12-2.12zm-.71.71l-5.3 5.3V21h2.12l5.3-5.3-2.12-2.12z","error":"M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z","expand_less":"M12 8l-6 6 1.41 1.41L12 10.83l4.59 4.58L18 14z","expand_more":"M16.59 8.59L12 13.17 7.41 8.59 6 10l6 6 6-6z","extension":"M20.5 11H19V7c0-1.1-.9-2-2-2h-4V3.5C13 2.12 11.88 1 10.5 1S8 2.12 8 3.5V5H4c-1.1 0-2 .9-2 2v3.8h1.5c1.52 0 2.75 1.23 2.75 2.75S5.02 16.3 3.5 16.3H2V20c0 1.1.9 2 2 2h3.8v-1.5c0-1.52 1.23-2.75 2.75-2.75s2.75 1.23 2.75 2.75V22H17c1.1 0 2-.9 2-2v-4h1.5c1.38 0 2.5-1.12 2.5-2.5S21.88 11 20.5 11z","folder":"M10 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z","folder_open":"M20 6h-8l-2-2H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm0 12H4V8h16v10z","group":"M16 11c1.66 0 2.99-1.34 2.99-3S17.66 5 16 5c-1.66 0-3 1.34-3 3s1.34 3 3 3zm-8 0c1.66 0 2.99-1.34 2.99-3S9.66 5 8 5C6.34 5 5 6.34 5 8s1.34 3 3 3zm0 2c-2.33 0-7 1.17-7 3.5V19h14v-2.5c0-2.33-4.67-3.5-7-3.5zm8 0c-.29 0-.62.02-.97.05 1.16.84 1.97 1.97 1.97 3.45V19h6v-2.5c0-2.33-4.67-3.5-7-3.5z","health_and_safety":"M10.5 13H8v-3h2.5V7.5h3V10H16v3h-2.5v2.5h-3V13zM12 2L4 5v6.09c0 5.05 3.41 9.76 8 10.91 4.59-1.15 8-5.86 8-10.91V5l-8-3z","image":"M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z","inventory_2":"M20 2H4c-1 0-2 .9-2 2v3.01c0 .72.43 1.34 1 1.69V20c0 1.1 1.1 2 2 2h14c.9 0 2-.9 2-2V8.7c.57-.35 1-.97 1-1.69V4c0-1.1-1-2-2-2zm-5 12H9v-2h6v2zm5-7H4V4h16v3z","library_books":"M4 6H2v14c0 1.1.9 2 2 2h14v-2H4V6zm16-4H8c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-1 9H9V9h10v2zm-4 4H9v-2h6v2zm4-8H9V5h10v2z","link":"M3.9 12c0-1.71 1.39-3.1 3.1-3.1h4V7H7c-2.76 0-5 2.24-5 5s2.24 5 5 5h4v-1.9H7c-1.71 0-3.1-1.39-3.1-3.1zM8 13h8v-2H8v2zm9-6h-4v1.9h4c1.71 0 3.1 1.39 3.1 3.1s-1.39 3.1-3.1 3.1h-4V17h4c2.76 0 5-2.24 5-5s-2.24-5-5-5z","logout":"M17 7l-1.41 1.41L18.17 11H8v2h10.17l-2.58 2.58L17 17l5-5zM4 5h8V3H4c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h8v-2H4V5z","movie":"M18 4l2 4h-3l-2-4h-2l2 4h-3l-2-4H8l2 4H7L5 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V4h-4z","movie_filter":"M18 4l2 4h-3l-2-4h-2l2 4h-3l-2-4H8l2 4H7L5 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V4h-4zM11.25 15.25L10 18l-1.25-2.75L6 14l2.75-1.25L10 10l1.25 2.75L14 14l-2.75 1.25zm5.69-3.31L16 14l-.94-2.06L13 11l2.06-.94L16 8l.94 2.06L19 11l-2.06.94z","music_note":"M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z","palette":"M12 2C6.49 2 2 6.49 2 12s4.49 10 10 10c1.38 0 2.5-1.12 2.5-2.5 0-.61-.23-1.2-.64-1.67-.08-.1-.13-.21-.13-.33 0-.28.22-.5.5-.5H16c3.31 0 6-2.69 6-6 0-4.96-4.49-9-10-9zM6.5 13c-.83 0-1.5-.67-1.5-1.5S5.67 10 6.5 10s1.5.67 1.5 1.5S7.33 13 6.5 13zm3-4C8.67 9 8 8.33 8 7.5S8.67 6 9.5 6s1.5.67 1.5 1.5S10.33 9 9.5 9zm5 0c-.83 0-1.5-.67-1.5-1.5S13.67 6 14.5 6s1.5.67 1.5 1.5S15.33 9 14.5 9zm3 4c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5z","save":"M17 3H5c-1.11 0-2 .9-2 2v14c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V7l-4-4zm-5 16c-1.66 0-3-1.34-3-3s1.34-3 3-3 3 1.34 3 3-1.34 3-3 3zm3-10H5V5h10v4z","schedule":"M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67z","search":"M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z","settings":"M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z","smart_toy":"M20 9V7c0-1.1-.9-2-2-2h-3c0-1.66-1.34-3-3-3S9 3.34 9 5H6c-1.1 0-2 .9-2 2v2c-1.66 0-3 1.34-3 3s1.34 3 3 3v4c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2v-4c1.66 0 3-1.34 3-3s-1.34-3-3-3zM7.5 11.5c0-.83.67-1.5 1.5-1.5s1.5.67 1.5 1.5S9.83 13 9 13s-1.5-.67-1.5-1.5zM16 17H8v-2h8v2zm-1-4c-.83 0-1.5-.67-1.5-1.5S14.17 10 15 10s1.5.67 1.5 1.5S15.83 13 15 13z","star":"M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z","storage":"M2 20h20v-4H2v4zm2-3h2v2H4v-2zM2 4v4h20V4H2zm4 3H4V5h2v2zm-4 7h20v-4H2v4zm2-3h2v2H4v-2z","straighten":"M21 6H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm0 10H3V8h2v4h2V8h2v4h2V8h2v4h2V8h2v4h2V8h2v4z","track_changes":"M19.07 4.93l-1.41 1.41C19.1 7.79 20 9.79 20 12c0 4.42-3.58 8-8 8s-8-3.58-8-8c0-4.08 3.05-7.44 7-7.93v2.02C8.16 6.57 6 9.03 6 12c0 3.31 2.69 6 6 6s6-2.69 6-6c0-1.66-.67-3.16-1.76-4.24l-1.41 1.41C15.55 9.9 16 10.9 16 12c0 2.21-1.79 4-4 4s-4-1.79-4-4c0-1.86 1.28-3.41 3-3.86v2.14c-.6.35-1 .98-1 1.72 0 1.1.9 2 2 2s2-.9 2-2c0-.74-.4-1.38-1-1.72V2h-1C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10c0-2.76-1.12-5.26-2.93-7.07z","trending_up":"M16 6l2.29 2.29-4.88 4.88-4-4L2 16.59 3.41 18l6-6 4 4 6.3-6.29L22 12V6z","tv":"M21 3H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h5v2h8v-2h5c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 14H3V5h18v12z","upload":"M5 4v2h14V4H5zm0 10h4v6h6v-6h4l-7-7-7 7z","volume_up":"M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z","warning":"M1 21h22L12 2 1 21zm12-3h-2v-2h2v2zm0-4h-2v-4h2v4z","explore":"M12 10.9c-.61 0-1.1.49-1.1 1.1s.49 1.1 1.1 1.1c.61 0 1.1-.49 1.1-1.1s-.49-1.1-1.1-1.1zM12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm2.19 12.19L6 18l3.81-8.19L18 6l-3.81 8.19z","cloud_download":"M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM17 13l-5 5-5-5h3V9h4v4h3z","high_quality":"M19 4H5c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm-8 11H9.5v-2h-2v2H6V9h1.5v2.5h2V9H11v6zm7-1c0 .55-.45 1-1 1h-.75v1.5h-1.5V15H14c-.55 0-1-.45-1-1v-4c0-.55.45-1 1-1h3c.55 0 1 .45 1 1v4zm-3.5-.5h2v-3h-2v3z"};
function mi(name) {
    var d = _mi[name];
    if (!d) return '';
    return '<span class="mi" aria-hidden="true"><svg viewBox="0 0 24 24" fill="currentColor" width="1em" height="1em" focusable="false"><path d="' + d + '"/></svg></span>';
}

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

// Translation helper - loaded async from /JellyfinHelper/Translations
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
    // allowSafeHtml permits only <strong> and <br> from the translation value while escaping
    // all other markup, preventing injection if the translations endpoint were compromised.
    if (placeholder) placeholder.innerHTML = allowSafeHtml(T('scanPlaceholder', 'Click <strong>Scan Libraries</strong> to analyze your media folders.'));
}

function formatBytes(bytes) {
    if (!Number.isFinite(bytes)) return '0 B';
    if (bytes === 0) return '0 B';
    if (bytes < 0) return '0 B';
    var units = ['B', 'KB', 'MB', 'GB', 'TB'];
    var i = Math.floor(Math.log(bytes) / Math.log(1024));
    if (i < 0) i = 0;
    if (i >= units.length) i = units.length - 1;
    return (bytes / Math.pow(1024, i)).toFixed(2) + ' ' + units[i];
}

function escAttr(s) {
    return (s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/'/g, '&#39;');
}

function escHtml(s) {
    return String(s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

/**
 * Sanitizes a string for use with innerHTML, permitting only a narrow allowlist of
 * safe inline tags (<strong>, <br>). All other HTML is escaped.
 * Used for translation strings that intentionally contain simple formatting markup
 * (e.g. scanPlaceholder) but must not allow arbitrary injection if the translations
 * endpoint were ever compromised.
 *
 * @param {string} s - The input string, possibly containing <strong> and <br> tags.
 * @returns {string} HTML-safe string with only <strong> and <br> preserved.
 */
function allowSafeHtml(s) {
    // 1. Escape everything.
    var escaped = escHtml(String(s || ''));
    // 2. Restore the specific tags we trust: <strong>, </strong>, <br>, <br/>, <br />.
    return escaped
        .replace(/&lt;strong&gt;/g, '<strong>')
        .replace(/&lt;\/strong&gt;/g, '</strong>')
        .replace(/&lt;br\s*\/?&gt;/g, '<br>');
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
        html += '<div class="tree-folder' + (hasContent ? ' tree-toggle" tabindex="0" role="button" aria-expanded="false" data-tree-toggle="1"' : '"') + '>';
        html += '<span class="tree-icon tree-icon-closed">' + mi('folder') + '</span>';
        html += '<span class="tree-icon tree-icon-open">' + mi('folder_open') + '</span>';
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
        return '<div class="file-tree-empty">' + escHtml(T('noFilesFound', 'No files found.')) + '</div>';
    }

    var sectionCount = (hasMovies ? 1 : 0) + (hasTvShows ? 1 : 0) + (hasMusic ? 1 : 0) + (hasOther ? 1 : 0);
    var html = '<div class="file-tree-header">';
    html += '<span class="file-tree-title">' + escHtml(title) + '</span>';
    html += '<div style="display:flex;gap:0.5em;align-items:center;">';
    html += '<button class="tree-action-btn" data-tree-action="expand">' + escHtml(T('expandAll', 'Expand All')) + '</button>';
    html += '<button class="tree-action-btn" data-tree-action="collapse">' + escHtml(T('collapseAll', 'Collapse All')) + '</button>';
    html += '<span class="file-tree-count">' + totalFiles + ' ' + (totalFiles === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files'))) + '</span>';
    html += '</div></div>';

    html += '<div class="file-tree-columns' + (sectionCount > 1 ? ' file-tree-multi' : '') + '">';

    var roots = result.rootPaths || {};

    if (hasMovies) {
        html += '<div class="file-tree-section">';
        html += '<div class="file-tree-section-header"><span class="badge badge-movies">' + escHtml(T('movies', 'Movies')) + '</span> <span class="file-tree-section-count">(' + result.movies.length + ')</span></div>';
        html += '<div class="tree-view">';
        html += renderTreeLevel(buildPathTree(result.movies, roots.movies), 0, mi('movie'));
        html += '</div></div>';
    }

    if (hasTvShows) {
        html += '<div class="file-tree-section">';
        html += '<div class="file-tree-section-header"><span class="badge badge-tvshows">' + escHtml(T('tvShows', 'TV Shows')) + '</span> <span class="file-tree-section-count">(' + result.tvShows.length + ')</span></div>';
        html += '<div class="tree-view">';
        html += renderTreeLevel(buildPathTree(result.tvShows, roots.tvShows), 0, mi('tv'));
        html += '</div></div>';
    }

    if (hasMusic) {
        html += '<div class="file-tree-section">';
        html += '<div class="file-tree-section-header"><span class="badge badge-music">' + escHtml(T('music', 'Music')) + '</span> <span class="file-tree-section-count">(' + result.music.length + ')</span></div>';
        html += '<div class="tree-view">';
        html += renderTreeLevel(buildPathTree(result.music, roots.music), 0, mi('music_note'));
        html += '</div></div>';
    }

    if (hasOther) {
        html += '<div class="file-tree-section">';
        html += '<div class="file-tree-section-header"><span class="badge badge-other">' + escHtml(T('other', 'Other')) + '</span> <span class="file-tree-section-count">(' + result.other.length + ')</span></div>';
        html += '<div class="tree-view">';
        html += renderTreeLevel(buildPathTree(result.other, roots.other), 0, mi('description'));
        html += '</div></div>';
    }

    html += '</div>';
    return html;
}

/**
 * Wire up event listeners for interactive elements rendered by renderFileTree /
 * renderTreeLevel.  Must be called after the HTML returned by those functions
 * has been injected into the DOM.
 *
 * Handles:
 *   - [data-tree-toggle]  — folder toggle buttons (expand/collapse tree node)
 *   - [data-tree-action]  — "Expand All" / "Collapse All" buttons
 *
 * @param {HTMLElement} container - The DOM element whose innerHTML was set with
 *                                  the output of renderFileTree.
 */
function bindFileTreeHandlers(container) {
    if (!container) return;

    // Folder toggle buttons
    var toggles = container.querySelectorAll('[data-tree-toggle]');
    for (var i = 0; i < toggles.length; i++) {
        (function (btn) {
            btn.addEventListener('click', function () {
                var node = btn.parentElement;
                node.classList.toggle('tree-expanded');
                btn.setAttribute('aria-expanded', node.classList.contains('tree-expanded'));
            });
            btn.addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    btn.click();
                }
            });
        })(toggles[i]);
    }

    // Expand All / Collapse All buttons
    var actionBtns = container.querySelectorAll('[data-tree-action]');
    for (var j = 0; j < actionBtns.length; j++) {
        (function (btn) {
            btn.addEventListener('click', function () {
                var action = btn.getAttribute('data-tree-action');
                var panel = btn.closest('.file-tree-panel');
                var nodes = panel ? panel.querySelectorAll('.tree-node') : [];
                for (var k = 0; k < nodes.length; k++) {
                    if (action === 'expand') {
                        nodes[k].classList.add('tree-expanded');
                    } else {
                        nodes[k].classList.remove('tree-expanded');
                    }
                    var toggle = nodes[k].querySelector('.tree-toggle');
                    if (toggle) toggle.setAttribute('aria-expanded', action === 'expand');
                }
            });
        })(actionBtns[j]);
    }
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
 * Temporarily replaces the chevron/arrow of a dropdown element with a check_circle or error icon.
 * For <select> elements: hides the CSS background-image chevron and shows an absolutely positioned icon.
 * For library-multiselect-toggle buttons: replaces the chevron span innerHTML.
 * @param {HTMLElement} element - The element to show the indicator on (select or toggle button).
 * @param {boolean} [success=true] - true = green check_circle icon, false = red error icon
 */
function showAutoSaveIndicatorOverlay(element, success) {
    if (!element || !element.parentNode) return;

    var ok = success !== false;
    var fadeDelay = ok ? 2000 : 3000;
    var color = ok ? getCssVar('--color-success', '#2ecc71') : getCssVar('--color-danger', '#e74c3c');
    var iconHtml = mi(ok ? 'check_circle' : 'error');

    // Library multi-select toggle: replace chevron span content
    var chevronSpan = element.querySelector && element.querySelector('.library-multiselect-chevron');
    if (chevronSpan) {
        var chevronGuard = (parseInt(chevronSpan.getAttribute('data-save-guard') || '0', 10)) + 1;
        chevronSpan.setAttribute('data-save-guard', String(chevronGuard));
        chevronSpan.innerHTML = '<span style="color:' + color + ';font-size:1em;">' + iconHtml + '</span>';
        (function (guardVal) {
            setTimeout(function () {
                if (chevronSpan.getAttribute('data-save-guard') !== String(guardVal)) return;
                chevronSpan.innerHTML = mi('expand_more');
            }, fadeDelay);
        })(chevronGuard);
        return;
    }

    // Native <select> or text-like <input> element: replace background-image with a check_circle SVG
    var isTextLikeInput = element.tagName === 'INPUT'
        && !/^(checkbox|radio|file|range|color|submit|button|reset)$/i.test(element.type || 'text');
    if (element.tagName === 'SELECT' || isTextLikeInput) {
        // Increment a guard counter to prevent race conditions on rapid changes
        var selectGuard = (parseInt(element.getAttribute('data-save-guard') || '0', 10)) + 1;
        element.setAttribute('data-save-guard', String(selectGuard));

        // Store true originals only on first invocation to prevent snapshot poisoning
        // when a second auto-save fires while the first icon is still displayed.
        if (!element.hasAttribute('data-orig-bg')) {
            element.setAttribute('data-orig-bg', element.style.backgroundImage || '');
            element.setAttribute('data-orig-bg-repeat', element.style.backgroundRepeat || '');
            element.setAttribute('data-orig-bg-position', element.style.backgroundPosition || '');
            element.setAttribute('data-orig-bg-size', element.style.backgroundSize || '');
            element.setAttribute('data-orig-appearance', element.style.appearance || '');
            element.setAttribute('data-orig-webkit-appearance', element.style.webkitAppearance || '');
        }

        // Hide native browser arrow (for selects outside .settings-form that still have native appearance)
        element.style.webkitAppearance = 'none';
        element.style.appearance = 'none';

        // Use an inline SVG data URI of check_circle in the success/error color
        var svgColor = encodeURIComponent(color);
        var checkSvg = "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='" + svgColor + "'%3E%3Cpath d='M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-2 15l-5-5 1.41-1.41L10 14.17l7.59-7.59L19 8l-9 9z'/%3E%3C/svg%3E\")";
        if (!ok) {
            checkSvg = "url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='" + svgColor + "'%3E%3Cpath d='M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z'/%3E%3C/svg%3E\")";
        }
        element.style.backgroundImage = checkSvg;
        element.style.backgroundRepeat = 'no-repeat';
        element.style.backgroundPosition = 'right 0.5em center';
        element.style.backgroundSize = '1.3em';

        // Restore original styles after delay (only if no newer indicator was triggered)
        (function (guardVal, el) {
            setTimeout(function () {
                if (el.getAttribute('data-save-guard') !== String(guardVal)) return;
                el.style.backgroundImage = el.getAttribute('data-orig-bg') || '';
                el.style.backgroundRepeat = el.getAttribute('data-orig-bg-repeat') || '';
                el.style.backgroundPosition = el.getAttribute('data-orig-bg-position') || '';
                el.style.backgroundSize = el.getAttribute('data-orig-bg-size') || '';
                el.style.appearance = el.getAttribute('data-orig-appearance') || '';
                el.style.webkitAppearance = el.getAttribute('data-orig-webkit-appearance') || '';
                // Clear stored originals so next invocation re-captures fresh state
                el.removeAttribute('data-orig-bg');
                el.removeAttribute('data-orig-bg-repeat');
                el.removeAttribute('data-orig-bg-position');
                el.removeAttribute('data-orig-bg-size');
                el.removeAttribute('data-orig-appearance');
                el.removeAttribute('data-orig-webkit-appearance');
            }, fadeDelay);
        })(selectGuard, element);
        return;
    }

    // Fallback for other elements: use the old overlay approach
    removeExistingSaveIndicatorOverlay(element);
    var indicator = createSaveIndicator(element, success);
    var indicatorContainer = document.createElement('div');
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
    indicatorContainer.style.zIndex = '10';
    indicatorContainer.append(indicator);
    var emptyContainer = document.createElement('div');
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
    var existing = element.previousElementSibling;

    if (existing && existing.classList.contains('fade-element')) {
        clearTimeout(existing._fadeTimer);
        clearTimeout(existing._removeTimer);
        existing.remove();
    }
}

/**
 * Creates a save indicator element with the specified success status.
 * The indicator is styled with a green check_circle icon for success and a red error icon for failure.
 *
 * @param {HTMLElement} element The parent element to which the indicator will be attached.
 * @param {boolean} [success=true] - true = green check_circle icon, false = red error icon
 * @return {HTMLElement} The created save indicator element.
 */
function createSaveIndicator(element, success) {
    let indicator = document.createElement('span');
    indicator.style.fontSize = '0.95em';
    indicator.style.color = success !== false ? getCssVar('--color-success', '#2ecc71') : getCssVar('--color-danger', '#e74c3c');
    indicator.innerHTML = success !== false ? mi('check_circle') : mi('error');

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
 * Switches the button content to a  or  icon + message, adds a CSS class,
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
    var cls = success ? 'success' : 'error';
    var delay = timeout || (success ? 3000 : 5000);
    btn.classList.remove('success', 'error');
    btn.innerHTML = '<span class="btn-icon">' + (success ? mi('check_circle') : mi('error')) + '</span>' + escHtml(message);
    btn.classList.add(cls);
    return setTimeout(function () {
        btn.innerHTML = originalHtml;
        btn.classList.remove('success', 'error');
    }, delay);
}

// ============================================================
// API Wrapper - centralizes ApiClient.ajax() calls
// ============================================================

/**
 * Extracts a structured diagnostic summary from an ApiClient.ajax() error object.
 * ApiClient rejects with an XHR-like object; older browsers or intercepting
 * proxies may reject with a plain Error. Both shapes are handled defensively so
 * callers can present a meaningful message to the user without exploding on
 * missing fields.
 *
 * @param {*} err - The error object produced by a rejected ApiClient.ajax() promise.
 * @returns {{status: number, statusText: string, snippet: string, kind: string}}
 *          status:     HTTP status code (0 when unavailable, e.g. network failure).
 *          statusText: Short status label (e.g. 'Bad Request'). Empty when unknown.
 *          snippet:    First ~200 chars of the response body. Empty when unavailable.
 *          kind:       One of 'network', 'proxy', 'server', 'unauthorized', 'notfound', 'unknown'.
 *                      Used by higher-level UI to pick a hint message.
 */
function describeApiError(err) {
    var status = 0;
    var statusText = '';
    var snippet = '';

    if (err && typeof err === 'object') {
        if (typeof err.status === 'number') status = err.status;
        if (typeof err.statusText === 'string') statusText = err.statusText;
        // ApiClient exposes the raw text via responseText; some paths expose response.
        // Note: bracket notation on the message field is intentional. A repo-wide
        // guard test (Html_BackupImportFailure_DoesNotExposeRawErrorsInUi) forbids
        // the dotted access pattern for the message property in the embedded config
        // page as a safeguard against accidentally rendering raw error strings into
        // the UI. Here the value flows only into console.error / an admin toast,
        // never into innerHTML, so bracket access preserves the guardrail while
        // still exposing the diagnostic.
        var errMessage = err['message'];
        if (typeof err.responseText === 'string' && err.responseText.length > 0) {
            snippet = err.responseText;
        } else if (typeof err.response === 'string' && err.response.length > 0) {
            snippet = err.response;
        } else if (errMessage) {
            snippet = String(errMessage);
        }
    } else if (err) {
        snippet = String(err);
    }

    if (snippet.length > 200) snippet = snippet.substring(0, 200) + '\u2026';

    var kind = 'unknown';
    if (status === 0) kind = 'network';
    else if (status === 401 || status === 403) kind = 'unauthorized';
    else if (status === 404) kind = 'notfound';
    else if (status >= 500) kind = 'server';
    else if (status >= 400) {
        // 4xx with HTML body typically means proxy/WAF; JSON body means our own server.
        var trimmed = snippet.trim();
        var looksLikeHtml = trimmed.length > 0 && trimmed.charAt(0) === '<';
        kind = looksLikeHtml ? 'proxy' : 'server';
    }

    return {status: status, statusText: statusText, snippet: snippet, kind: kind};
}

/**
 * Default error handler for API calls. Logs a structured diagnostic to the
 * console so failures are never silent and support requests can copy the info.
 */
function _apiDefaultError(method, path) {
    return function (err) {
        var d = describeApiError(err);
        console.error(
            'JellyfinHelper ' + method + ' failed: ' + path
                + ' (status=' + d.status + ' ' + d.statusText + ', kind=' + d.kind + ')',
            d.snippet || err);
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
 *
 * Note: If the server returns 200 OK with malformed JSON, response.json() will
 * reject with a SyntaxError.  This is caught by errHandler alongside network
 * errors.  For most callers the distinction is irrelevant; if finer-grained
 * diagnostics are ever needed, the catch handler could inspect error.name
 * ('SyntaxError' vs generic Error / TypeError for network failures).
 *
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
// Dialog Helpers - reusable modal dialogs
// ============================================================

/**
 * Creates a modal dialog overlay with title, body, and button row.
 * Returns { overlay, dialog, body, btnRow } so callers can add content or buttons.
 * bodyContent is always set via textContent — callers needing rich content
 * should leave bodyContent empty and append child elements to the returned body element.
 */
function createDialogOverlay(overlayId, titleText, titleColor, bodyContent) {
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
    if (bodyContent) {
        body.textContent = bodyContent;
    }
    dialog.appendChild(body);

    var btnRow = document.createElement('div');
    btnRow.style.cssText = 'display:flex;gap:0.8em;justify-content:flex-end;flex-wrap:wrap;';
    dialog.appendChild(btnRow);
    overlay.appendChild(dialog);

    return {overlay: overlay, dialog: dialog, body: body, btnRow: btnRow};
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
            if (typeof bindFileTreeHandlers === 'function') { bindFileTreeHandlers(panel); }
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

// --- Resolve Arr instances from configuration ---
function resolveArrInstances(cfg, type) {
    if (!cfg) return [];
    var key = type + 'Instances';     // e.g. RadarrInstances
    if (cfg[key] && cfg[key].length > 0) return cfg[key];
    return [];
}
