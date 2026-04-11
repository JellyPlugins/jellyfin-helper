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
        if (btnRefresh) btnRefresh.innerHTML = '&#x21bb; ' + T('scanLibraries', 'Scan Libraries');

        var btnExportJson = document.getElementById('btnExportJson');
        if (btnExportJson) btnExportJson.textContent = '\ud83d\udce5 ' + T('exportJson', 'JSON');
        var btnExportCsv = document.getElementById('btnExportCsv');
        if (btnExportCsv) btnExportCsv.textContent = '\ud83d\udce5 ' + T('exportCsv', 'CSV');

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

    // Get the file name from a full path
    function getFileName(fullPath) {
        if (!fullPath) return '';
        var parts = fullPath.replace(/\\/g, '/').split('/');
        return parts[parts.length - 1] || fullPath;
    }

    // Get a display-friendly directory (parent folder)
    function getParentFolder(fullPath) {
        if (!fullPath) return '';
        var normalized = fullPath.replace(/\\/g, '/');
        var parts = normalized.split('/');
        if (parts.length >= 2) return parts[parts.length - 2];
        return '';
    }

    // Render a file list panel grouped by media type (movies, tvShows, music)
    // result: { movies: string[], tvShows: string[], music: string[] }
    // title: string displayed in the header
    function renderFileList(result, title) {
        var hasMovies = result.movies && result.movies.length > 0;
        var hasTvShows = result.tvShows && result.tvShows.length > 0;
        var hasMusic = result.music && result.music.length > 0;
        var hasOther = result.other && result.other.length > 0;
        var totalFiles = (result.movies ? result.movies.length : 0) + (result.tvShows ? result.tvShows.length : 0) + (result.music ? result.music.length : 0) + (result.other ? result.other.length : 0);

        if (totalFiles === 0) {
            return '<div class="codec-files-empty">' + T('noFilesFound', 'No files found.') + '</div>';
        }

        var sectionCount = (hasMovies ? 1 : 0) + (hasTvShows ? 1 : 0) + (hasMusic ? 1 : 0) + (hasOther ? 1 : 0);
        var html = '<div class="codec-files-header">';
        html += '<span class="codec-files-title">' + escHtml(title) + '</span>';
        html += '<span class="codec-files-count">' + totalFiles + ' ' + (totalFiles === 1 ? T('file', 'file') : T('files', 'files')) + '</span>';
        html += '</div>';

        html += '<div class="codec-files-columns' + (sectionCount > 1 ? ' codec-files-multi' : '') + '">';

        if (hasMovies) {
            html += '<div class="codec-files-section">';
            html += '<div class="codec-files-section-header"><span class="badge badge-movies">' + T('movies', 'Movies') + '</span> <span class="codec-files-section-count">(' + result.movies.length + ')</span></div>';
            html += '<div class="codec-files-list">';
            for (var i = 0; i < result.movies.length; i++) {
                html += '<div class="codec-file-item" title="' + escAttr(result.movies[i]) + '">';
                html += '<span class="codec-file-icon">🎬</span>';
                html += '<span class="codec-file-name">' + escHtml(getFileName(result.movies[i])) + '</span>';
                html += '<span class="codec-file-folder">' + escHtml(getParentFolder(result.movies[i])) + '</span>';
                html += '</div>';
            }
            html += '</div></div>';
        }

        if (hasTvShows) {
            html += '<div class="codec-files-section">';
            html += '<div class="codec-files-section-header"><span class="badge badge-tvshows">' + T('tvShows', 'TV Shows') + '</span> <span class="codec-files-section-count">(' + result.tvShows.length + ')</span></div>';
            html += '<div class="codec-files-list">';
            for (var j = 0; j < result.tvShows.length; j++) {
                html += '<div class="codec-file-item" title="' + escAttr(result.tvShows[j]) + '">';
                html += '<span class="codec-file-icon">📺</span>';
                html += '<span class="codec-file-name">' + escHtml(getFileName(result.tvShows[j])) + '</span>';
                html += '<span class="codec-file-folder">' + escHtml(getParentFolder(result.tvShows[j])) + '</span>';
                html += '</div>';
            }
            html += '</div></div>';
        }

        if (hasMusic) {
            html += '<div class="codec-files-section">';
            html += '<div class="codec-files-section-header"><span class="badge badge-music">' + T('music', 'Music') + '</span> <span class="codec-files-section-count">(' + result.music.length + ')</span></div>';
            html += '<div class="codec-files-list">';
            for (var k = 0; k < result.music.length; k++) {
                html += '<div class="codec-file-item" title="' + escAttr(result.music[k]) + '">';
                html += '<span class="codec-file-icon">🎵</span>';
                html += '<span class="codec-file-name">' + escHtml(getFileName(result.music[k])) + '</span>';
                html += '<span class="codec-file-folder">' + escHtml(getParentFolder(result.music[k])) + '</span>';
                html += '</div>';
            }
            html += '</div></div>';
        }

        if (hasOther) {
            html += '<div class="codec-files-section">';
            html += '<div class="codec-files-section-header"><span class="badge badge-other">' + T('other', 'Other') + '</span> <span class="codec-files-section-count">(' + result.other.length + ')</span></div>';
            html += '<div class="codec-files-list">';
            for (var l = 0; l < result.other.length; l++) {
                html += '<div class="codec-file-item" title="' + escAttr(result.other[l]) + '">';
                html += '<span class="codec-file-icon">📄</span>';
                html += '<span class="codec-file-name">' + escHtml(getFileName(result.other[l])) + '</span>';
                html += '<span class="codec-file-folder">' + escHtml(getParentFolder(result.other[l])) + '</span>';
                html += '</div>';
            }
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
