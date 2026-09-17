'use strict';

// Library Explorer: collapsible section at the bottom of the Codecs tab that combines
// several codec dimensions (plus library) into one targeted file search, e.g. 4K with
// low bitrate. The donut charts above stay static; only this section filters.
var _codecsExplorerState = {library: '', filters: {}, expanded: false};

// Guard: the Overview deep-link handler is registered once at document level.
var _codecExploreLinkBound = false;

// Upper bound for rendered result files. The tree renders every leaf eagerly, so an
// unbounded 10k-file result would freeze low-end phones. The hint tells how to narrow down.
var CODEC_EXPLORER_MAX_FILES = 300;

// Explorer dimensions in display order. Paths/categories reuse the Codecs tab maps so the
// explorer always agrees with the donuts above it.
var CODEC_EXPLORER_DIMENSIONS = [
    {id: 'resolutions', pathsProp: 'ResolutionPaths', countProp: 'Resolutions', labelKey: 'resolutions', fallback: 'Resolution'},
    {id: 'videoCodecs', pathsProp: 'VideoCodecPaths', countProp: 'VideoCodecs', labelKey: 'videoCodecs', fallback: 'Video codec'},
    {id: 'videoAudioCodecs', pathsProp: 'VideoAudioCodecPaths', countProp: 'VideoAudioCodecs', labelKey: 'videoAudioCodecs', fallback: 'Audio codec'},
    {id: 'videoBitrate', pathsProp: 'VideoBitrateTierPaths', countProp: 'VideoBitrateTiers', labelKey: 'videoBitrate', fallback: 'Video bitrate'},
    {id: 'dynamicRanges', pathsProp: 'DynamicRangePaths', countProp: 'DynamicRanges', labelKey: 'dynamicRange', fallback: 'Dynamic range'},
    {id: 'audioLanguages', pathsProp: 'AudioLanguagePaths', countProp: 'AudioLanguages', labelKey: 'audioLanguages', fallback: 'Audio language'},
    {id: 'subtitleLanguages', pathsProp: 'SubtitleLanguagePaths', countProp: 'SubtitleLanguages', labelKey: 'subtitleLanguages', fallback: 'Subtitle language'},
    {id: 'watched', pathsProp: 'WatchedTierPaths', countProp: 'WatchedTiers', labelKey: 'watched', fallback: 'Watched'}
];

function getCodecsExplorerState() {
    return _codecsExplorerState;
}

// Libraries of the last scan, or an empty list when no scan ran yet.
function getCodecsExplorerLibraries() {
    if (!_lastCodecData || !Array.isArray(_lastCodecData.Libraries)) {
        return [];
    }
    return _lastCodecData.Libraries;
}

// Video-scoped libraries of the last scan (the only ones carrying codec dimensions).
function getCodecsExplorerVideoLibraries() {
    if (!_lastCodecData) {
        return [];
    }
    return (_lastCodecData.Movies || []).concat(_lastCodecData.TvShows || []).concat(_lastCodecData.Other || []);
}

// Root paths of the selected library, used to scope the intersected result set.
function getCodecsExplorerSelectedRoots() {
    var libs = getCodecsExplorerLibraries();
    for (var i = 0; i < libs.length; i++) {
        if (libs[i].LibraryName === _codecsExplorerState.library) {
            return libs[i].RootPaths || [];
        }
    }
    return [];
}

// Directory-boundary prefix match so /media/movies never matches /media/movies2.
function codecExplorerPathInRoots(path, roots) {
    if (!roots || roots.length === 0) {
        return true;
    }
    var lower = (path || '').toLowerCase();
    for (var i = 0; i < roots.length; i++) {
        var root = (roots[i] || '').toLowerCase().replace(/[/\\]+$/, '');
        if (root && (lower === root || lower.indexOf(root + '/') === 0 || lower.indexOf(root + '\\') === 0)) {
            return true;
        }
    }
    return false;
}

// Option counts are aggregated over the currently selected library scope so the explorer
// never offers values that cannot match.
function getCodecsExplorerScopedLibraries(dim) {
    void dim;
    var selected = _codecsExplorerState.library;
    if (!selected) {
        return getCodecsExplorerVideoLibraries();
    }
    var scoped = [];
    var groups = [(_lastCodecData || {}).Movies, (_lastCodecData || {}).TvShows, (_lastCodecData || {}).Other];
    for (var g = 0; g < groups.length; g++) {
        var libs = groups[g] || [];
        for (var i = 0; i < libs.length; i++) {
            if (libs[i].LibraryName === selected) {
                scoped.push(libs[i]);
            }
        }
    }
    return scoped;
}

function buildCodecsExplorerSelect(dim) {
    var counts = aggregateDict(getCodecsExplorerScopedLibraries(dim), dim.countProp);
    var options = Object.keys(counts).filter(function (k) {
        return counts[k] > 0;
    }).sort(function (a, b) {
        return counts[b] - counts[a];
    });
    var current = _codecsExplorerState.filters[dim.id] || '';
    var html = '<div class="codec-explorer-field">';
    html += '<label for="codecExplorer_' + escAttr(dim.id) + '">' + escHtml(T(dim.labelKey, dim.fallback)) + '</label>';
    html += '<select id="codecExplorer_' + escAttr(dim.id) + '" data-explorer-dim="' + escAttr(dim.id) + '">';
    html += '<option value="">' + escHtml(T('explorerAny', 'Any')) + '</option>';
    for (var i = 0; i < options.length; i++) {
        html += '<option value="' + escAttr(options[i]) + '"' + (options[i] === current ? ' selected' : '') + '>'
            + escHtml(options[i]) + ' (' + counts[options[i]] + ')</option>';
    }
    html += '</select></div>';
    return html;
}

function buildCodecsExplorerHtml() {
    var libs = getCodecsExplorerLibraries();
    var expanded = _codecsExplorerState.expanded;
    var html = '<div class="codec-explorer">';
    html += '<button class="codec-explorer-toggle" id="codecExplorerToggle" aria-expanded="' + (expanded ? 'true' : 'false') + '"'
        + ' aria-controls="codecExplorerBody">' + mi('search')
        + '<span>' + escHtml(T('explorerTitle', 'Library Explorer')) + '</span>'
        + '<span class="codec-explorer-arrow">' + (expanded ? '&#9660;' : '&#9654;') + '</span></button>';
    html += '<div class="codec-explorer-body' + (expanded ? ' open' : '') + '" id="codecExplorerBody">';
    html += '<p class="codec-explorer-hint">' + escHtml(T('explorerHint', 'Combine codec filters to find matching files.')) + '</p>';
    html += '<div class="codec-explorer-controls">';
    html += '<div class="codec-explorer-field"><label for="codecExplorerLibrary">' + escHtml(T('explorerLibrary', 'Library')) + '</label>';
    html += '<select id="codecExplorerLibrary">';
    html += '<option value="">' + escHtml(T('explorerAllLibraries', 'All libraries')) + '</option>';
    for (var i = 0; i < libs.length; i++) {
        html += '<option value="' + escAttr(libs[i].LibraryName) + '"'
            + (libs[i].LibraryName === _codecsExplorerState.library ? ' selected' : '') + '>'
            + escHtml(libs[i].LibraryName) + '</option>';
    }
    html += '</select></div>';
    for (var d = 0; d < CODEC_EXPLORER_DIMENSIONS.length; d++) {
        html += buildCodecsExplorerSelect(CODEC_EXPLORER_DIMENSIONS[d]);
    }
    html += '</div>';
    html += '<div class="codec-explorer-actions"><button class="codec-explorer-reset" id="codecExplorerReset">'
        + escHtml(T('explorerReset', 'Reset filters')) + '</button></div>';
    html += '<div class="codec-explorer-results" id="codecExplorerResults"></div>';
    html += '</div></div>';
    return html;
}

// Intersection of every active filter path set, scoped to the selected library.
function computeCodecsExplorerPaths() {
    var active = [];
    for (var d = 0; d < CODEC_EXPLORER_DIMENSIONS.length; d++) {
        var value = _codecsExplorerState.filters[CODEC_EXPLORER_DIMENSIONS[d].id];
        if (value) {
            active.push({dim: CODEC_EXPLORER_DIMENSIONS[d], value: value});
        }
    }
    if (active.length === 0 || !_lastCodecData) {
        return {paths: [], active: active};
    }
    var result = null;
    for (var i = 0; i < active.length; i++) {
        var collected = collectCodecPaths(_lastCodecData, active[i].dim.pathsProp, active[i].value,
            CODEC_CATEGORY_MAP[active[i].dim.id]);
        var flat = (collected.movies || []).concat(collected.tvShows || []).concat(collected.music || [])
            .concat(collected.books || []).concat(collected.other || []);
        var set = {};
        for (var p = 0; p < flat.length; p++) {
            set[flat[p]] = true;
        }
        if (result === null) {
            result = set;
        } else {
            var next = {};
            for (var key in result) {
                if (Object.hasOwn(result, key) && set[key]) {
                    next[key] = true;
                }
            }
            result = next;
        }
    }
    var roots = getCodecsExplorerSelectedRoots();
    if (_codecsExplorerState.library && roots.length === 0) {
        return {paths: [], active: active};
    }
    var paths = [];
    for (var path in result) {
        if (Object.hasOwn(result, path) && codecExplorerPathInRoots(path, roots)) {
            paths.push(path);
        }
    }
    paths.sort();
    return {paths: paths, active: active};
}

function runCodecsExplorerSearch() {
    var host = document.getElementById('codecExplorerResults');
    if (!host || !_lastCodecData) {
        return;
    }
    var outcome = computeCodecsExplorerPaths();
    if (outcome.active.length === 0) {
        host.innerHTML = '<p class="codec-explorer-empty">' + escHtml(T('explorerPickFilter', 'Pick at least one filter above to list matching files.')) + '</p>';
        return;
    }
    var summary = outcome.paths.length + ' ' + (outcome.paths.length === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files')));
    var labels = [];
    for (var i = 0; i < outcome.active.length; i++) {
        labels.push(outcome.active[i].value);
    }
    var html = '<div class="codec-explorer-summary"><span>' + escHtml(summary) + '</span>';
    html += '<span class="codec-explorer-active">' + escHtml(labels.join(' · ')) + '</span></div>';
    if (outcome.paths.length === 0) {
        html += '<p class="codec-explorer-empty">' + escHtml(T('noFilesFound', 'No files found.')) + '</p>';
        host.innerHTML = html;
        return;
    }
    var truncated = outcome.paths.length > CODEC_EXPLORER_MAX_FILES;
    var shown = truncated ? outcome.paths.slice(0, CODEC_EXPLORER_MAX_FILES) : outcome.paths;
    if (truncated) {
        html += '<p class="codec-explorer-truncated">' + escHtml(T('explorerTruncated', 'Showing first 300 matches — refine filters to narrow down.')
            .replace('300', String(CODEC_EXPLORER_MAX_FILES))) + '</p>';
    }
    html += renderFileTree({movies: shown, tvShows: [], music: [], books: [], other: [], rootPaths: {}}, labels.join(' · '), null);
    host.innerHTML = html;
    bindFileTreeHandlers(host);
}

// Rebuilds only the dimension selects (their options depend on the library scope),
// keeps the toggle/library/reset wiring untouched so focus is not stolen.
function refreshCodecsExplorerSelects() {
    var controls = document.querySelector('.codec-explorer-controls');
    if (!controls) {
        return;
    }
    var fields = controls.querySelectorAll('[data-explorer-dim]');
    for (var i = 0; i < fields.length; i++) {
        (function (select) {
            var dimId = select.getAttribute('data-explorer-dim');
            for (var d = 0; d < CODEC_EXPLORER_DIMENSIONS.length; d++) {
                if (CODEC_EXPLORER_DIMENSIONS[d].id === dimId) {
                    var tmp = document.createElement('div');
                    tmp.innerHTML = buildCodecsExplorerSelect(CODEC_EXPLORER_DIMENSIONS[d]);
                    select.parentNode.replaceChild(tmp.firstChild, select);
                    break;
                }
            }
        })(fields[i]);
    }
    bindCodecsExplorerSelectHandlers();
}

function bindCodecsExplorerSelectHandlers() {
    var selects = document.querySelectorAll('[data-explorer-dim]');
    for (var i = 0; i < selects.length; i++) {
        (function (select) {
            select.onchange = function () {
                var dimId = select.getAttribute('data-explorer-dim');
                if (select.value) {
                    _codecsExplorerState.filters[dimId] = select.value;
                } else {
                    delete _codecsExplorerState.filters[dimId];
                }
                runCodecsExplorerSearch();
            };
        })(selects[i]);
    }
}

function attachCodecsExplorerHandlers() {
    var toggle = document.getElementById('codecExplorerToggle');
    if (toggle) {
        toggle.onclick = function () {
            var body = document.getElementById('codecExplorerBody');
            var arrow = toggle.querySelector('.codec-explorer-arrow');
            _codecsExplorerState.expanded = !_codecsExplorerState.expanded;
            if (body) {
                body.classList.toggle('open', _codecsExplorerState.expanded);
            }
            toggle.setAttribute('aria-expanded', _codecsExplorerState.expanded ? 'true' : 'false');
            if (arrow) {
                arrow.innerHTML = _codecsExplorerState.expanded ? '&#9660;' : '&#9654;';
            }
        };
    }
    var library = document.getElementById('codecExplorerLibrary');
    if (library) {
        library.onchange = function () {
            _codecsExplorerState.library = library.value;
            _codecsExplorerState.filters = {};
            refreshCodecsExplorerSelects();
            runCodecsExplorerSearch();
        };
    }
    var reset = document.getElementById('codecExplorerReset');
    if (reset) {
        reset.onclick = function () {
            _codecsExplorerState.filters = {};
            refreshCodecsExplorerSelects();
            runCodecsExplorerSearch();
        };
    }
    bindCodecsExplorerSelectHandlers();
    if (!_codecExploreLinkBound) {
        _codecExploreLinkBound = true;
        document.addEventListener('click', function (evt) {
            var target = evt.target && evt.target.closest ? evt.target.closest('[data-codec-explore-library]') : null;
            if (target) {
                openCodecsExplorer(target.getAttribute('data-codec-explore-library') || '');
            }
        });
    }
}

// Deep-link from the Overview library table: switch to Codecs, expand the explorer
// and pre-select the library so the admin starts from a meaningful scope.
function openCodecsExplorer(libraryName) {
    _codecsExplorerState.library = libraryName || '';
    _codecsExplorerState.filters = {};
    _codecsExplorerState.expanded = true;
    var tabBtn = document.querySelector('.tab-btn[data-tab="codecs"]');
    if (tabBtn) {
        tabBtn.click();
    }
    if (!_lastCodecData) {
        return;
    }
    var container = document.getElementById('codecsContent');
    if (!container) {
        return;
    }
    renderCodecsExplorer(container);
    var body = document.getElementById('codecExplorerBody');
    if (body) {
        body.scrollIntoView({block: 'start'});
    }
}

// Appends the explorer below the donut grid. Called on every fillCodecsData so the
// explorer always reflects the latest scan; user selections survive via module state.
function renderCodecsExplorer(container) {
    var existing = container.querySelector('.codec-explorer');
    if (existing) {
        existing.parentNode.removeChild(existing);
    }
    var tmp = document.createElement('div');
    tmp.innerHTML = buildCodecsExplorerHtml();
    container.appendChild(tmp.firstChild);
    attachCodecsExplorerHandlers();
    runCodecsExplorerSearch();
}
