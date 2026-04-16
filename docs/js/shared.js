// noinspection JSUnusedLocalSymbols,JSUnresolvedReference
// This file is the FIRST module concatenated into configPage.html — it opens the IIFE.
// Do NOT add a closing })(); here — that lives in main.js.
(function () {
    'use strict';

    var DONUT_COLORS = [
        '#00a4dc', '#e67e22', '#2ecc71', '#e74c3c', '#9b59b6',
        '#f1c40f', '#1abc9c', '#3498db', '#e91e63', '#ff9800',
        '#795548', '#607d8b', '#8bc34a', '#00bcd4', '#ff5722'
    ];

    // Translation helper — loaded async from /JellyfinHelper/Translations
    var _translations = {};

    function T(key, fallback) {
        return Object.prototype.hasOwnProperty.call(_translations, key)
            ? _translations[key]
            : (fallback || key);
    }

    function loadTranslations(callback) {
        try {
            var apiClient = ApiClient;
            apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Translations'), dataType: 'json' }).then(function (t) {
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
        var title = document.querySelector('.stats-header h2');
        if (title) title.textContent = T('title', 'Jellyfin Helper \u2014 Media Statistics');

        var btnRefresh = document.getElementById('btnRefresh');
        if (btnRefresh) btnRefresh.title = T('scanLibraries', 'Scan Libraries');

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

    function escAttr(s) { return (s || '').replace(/&/g,'&amp;').replace(/"/g,'&quot;').replace(/</g,'&lt;'); }
    function escHtml(s) { return String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;'); }

    function getFileName(fullPath) {
        if (!fullPath) return '';
        var parts = fullPath.replace(/\\/g, '/').split('/');
        return parts[parts.length - 1] || fullPath;
    }

    function getParentFolder(fullPath) {
        if (!fullPath) return '';
        var normalized = fullPath.replace(/\\/g, '/');
        var parts = normalized.split('/');
        if (parts.length >= 2) return parts[parts.length - 2];
        return '';
    }

    function getPathSegments(fullPath, rootPaths) {
        if (!fullPath) return [];
        var normalized = fullPath.replace(/\\/g, '/');

        var bestRoot = '';
        for (var i = 0; i < rootPaths.length; i++) {
            var root = rootPaths[i].replace(/\\/g, '/');
            if (normalized.startsWith(root) && root.length > bestRoot.length) {
                bestRoot = root;
            }
        }

        if (bestRoot) {
            var offset = bestRoot.length;
            if (normalized[offset] === '/') offset++;
            normalized = normalized.substring(offset);
        }

        return normalized.split('/').filter(function (s) { return s.length > 0; });
    }

    // Builds a nested tree structure from a list of paths
    function buildPathTree(paths, rootPaths) {
        var root = { name: 'root', children: {}, items: [] };
        for (var i = 0; i < paths.length; i++) {
            var path = paths[i];
            var segments = getPathSegments(path, rootPaths || []);
            var currentNode = root;

            for (var j = 0; j < segments.length - 1; j++) {
                var segment = segments[j];
                if (!currentNode.children[segment]) {
                    currentNode.children[segment] = { name: segment, children: {}, items: [] };
                }
                currentNode = currentNode.children[segment];
            }

            var leafName = segments.length > 0 ? segments[segments.length - 1] : path;
            currentNode.items.push({ name: leafName, fullPath: path });
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
            html += '<div class="tree-folder' + (hasContent ? ' tree-toggle' : '') + '" tabindex="0" role="button" aria-expanded="false" onclick="this.parentElement.classList.toggle(\'tree-expanded\')" onkeydown="if(event.key===\'Enter\'||event.key===\' \'){event.preventDefault();this.parentElement.classList.toggle(\'tree-expanded\')}">';
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
        html += '<button class="tree-action-btn" onclick="var nodes=this.closest(\'.file-tree-panel\').querySelectorAll(\'.tree-node\');for(var i=0;i<nodes.length;i++)nodes[i].classList.add(\'tree-expanded\')">' + T('expandAll', 'Expand All') + '</button>';
        html += '<button class="tree-action-btn" onclick="var nodes=this.closest(\'.file-tree-panel\').querySelectorAll(\'.tree-node\');for(var i=0;i<nodes.length;i++)nodes[i].classList.remove(\'tree-expanded\')">' + T('collapseAll', 'Collapse All') + '</button>';
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
     * Shows a brief ✔ or ✘ next to the given element, then fades out.
     * @param {HTMLElement} element - The form element (select, input, etc.) to attach the indicator to.
     * @param {boolean} [success=true] - true = green ✔, false = red ✘
     */
    function showAutoSaveIndicator(element, success) {
        if (!element || !element.parentNode) return;
        var cls = 'auto-save-check';
        var existing = element.parentNode.querySelector('.' + cls);
        if (existing) {
            clearTimeout(existing._fadeTimer);
            clearTimeout(existing._removeTimer);
            existing.remove();
        }
        var span = document.createElement('span');
        span.className = cls;
        span.style.cssText = 'margin-left:0.4em;font-size:0.95em;transition:opacity 0.4s;opacity:0;';
        span.style.color = success !== false ? '#2ecc71' : '#e74c3c';
        span.textContent = success !== false ? '✔' : '✘';
        element.parentNode.insertBefore(span, element.nextSibling);
        // Force reflow then fade in
        void span.offsetWidth;
        span.style.opacity = '1';
        var fadeDelay = success !== false ? 2000 : 3000;
        span._fadeTimer = setTimeout(function () { span.style.opacity = '0'; }, fadeDelay);
        span._removeTimer = setTimeout(function () { if (span.parentNode) span.remove(); }, fadeDelay + 500);
    }

    // NOTE: Do NOT close the IIFE here — it is closed in main.js (the last concatenated module).
