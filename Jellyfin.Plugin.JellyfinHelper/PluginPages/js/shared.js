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