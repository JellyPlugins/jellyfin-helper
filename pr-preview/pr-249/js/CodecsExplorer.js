'use strict';

// Library Explorer: collapsible section at the top of the Codecs tab that combines
// several codec dimensions (plus library scope) into one targeted file search, e.g. 4K
// with low bitrate. The donut charts below stay static; only this section filters.
// Filtering is faceted: options that would yield zero files under the other active
// filters are hidden, except values already selected (so they can be deselected).
var _codecsExplorerState = {library: '', filters: {}, expanded: false};

// Guard: the deep-link handlers are registered once at document level.
var _codecExploreLinkBound = false;

// Upper bound for rendered result files. The tree renders every leaf eagerly, so an
// unbounded 10k-file result would freeze low-end phones. The hint tells how to narrow down.
var CODEC_EXPLORER_MAX_FILES = 300;

// Library scope for "all movie libraries" / "all TV libraries" (used by Overview cards).
var CODEC_EXPLORER_TYPE_MOVIES = 'type:movies';
var CODEC_EXPLORER_TYPE_TVSHOWS = 'type:tvshows';

// Explorer dimensions in display order. Language dimensions allow multiple values (OR
// within the dimension, e.g. German + English audio); the rest take a single value.
// Paths reuse the Codecs tab maps so the explorer always agrees with the donuts below.
var CODEC_EXPLORER_DIMENSIONS = [
    {id: 'resolutions', pathsProp: 'ResolutionPaths', countProp: 'Resolutions', labelKey: 'resolutions', fallback: 'Resolution'},
    {id: 'videoCodecs', pathsProp: 'VideoCodecPaths', countProp: 'VideoCodecs', labelKey: 'videoCodecs', fallback: 'Video codec'},
    {id: 'videoAudioCodecs', pathsProp: 'VideoAudioCodecPaths', countProp: 'VideoAudioCodecs', labelKey: 'videoAudioCodecs', fallback: 'Audio codec'},
    {id: 'videoBitrate', pathsProp: 'VideoBitrateTierPaths', countProp: 'VideoBitrateTiers', labelKey: 'videoBitrate', fallback: 'Video bitrate'},
    {id: 'dynamicRanges', pathsProp: 'DynamicRangePaths', countProp: 'DynamicRanges', labelKey: 'dynamicRange', fallback: 'Dynamic range'},
    {id: 'audioLanguages', pathsProp: 'AudioLanguagePaths', countProp: 'AudioLanguages', labelKey: 'audioLanguages', fallback: 'Audio language', multi: true},
    {id: 'subtitleLanguages', pathsProp: 'SubtitleLanguagePaths', countProp: 'SubtitleLanguages', labelKey: 'subtitleLanguages', fallback: 'Subtitle language', multi: true},
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

// Video-scoped library groups of the last scan (the only ones carrying codec dimensions).
function getCodecsExplorerVideoGroups() {
    var data = _lastCodecData || {};
    return [data.Movies || [], data.TvShows || [], data.Other || []];
}

// Libraries of one video group (movies or tvShows), used for type scopes.
function getCodecsExplorerTypeLibraries(type) {
    var data = _lastCodecData || {};
    if (type === CODEC_EXPLORER_TYPE_MOVIES) {
        return data.Movies || [];
    }
    if (type === CODEC_EXPLORER_TYPE_TVSHOWS) {
        return data.TvShows || [];
    }
    return [];
}

// Root paths of the selected scope: one library by name, one type, or all video roots.
// Null means the selected scope matches nothing (stale name), which must yield no files.
function getCodecsExplorerSelectedRoots() {
    var selected = _codecsExplorerState.library;
    if (!selected) {
        return [];
    }
    if (selected === CODEC_EXPLORER_TYPE_MOVIES || selected === CODEC_EXPLORER_TYPE_TVSHOWS) {
        var roots = [];
        var libs = getCodecsExplorerTypeLibraries(selected);
        for (var i = 0; i < libs.length; i++) {
            roots = roots.concat(libs[i].RootPaths || []);
        }
        return roots;
    }
    var all = getCodecsExplorerLibraries();
    for (var j = 0; j < all.length; j++) {
        if (all[j].LibraryName === selected) {
            return all[j].RootPaths || [];
        }
    }
    return null;
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

// Libraries defining the option universe under the current scope.
function getCodecsExplorerScopedLibraries() {
    var selected = _codecsExplorerState.library;
    if (selected === CODEC_EXPLORER_TYPE_MOVIES || selected === CODEC_EXPLORER_TYPE_TVSHOWS) {
        return getCodecsExplorerTypeLibraries(selected);
    }
    if (!selected) {
        var scoped = [];
        var groups = getCodecsExplorerVideoGroups();
        for (var g = 0; g < groups.length; g++) {
            scoped = scoped.concat(groups[g]);
        }
        return scoped;
    }
    var libs = getCodecsExplorerVideoGroups();
    var match = [];
    for (var h = 0; h < libs.length; h++) {
        for (var i = 0; i < libs[h].length; i++) {
            if (libs[h][i].LibraryName === selected) {
                match.push(libs[h][i]);
            }
        }
    }
    return match;
}

// Selected values of a dimension, always as an array (single-selects hold at most one).
function getCodecsExplorerSelection(dimId) {
    var value = _codecsExplorerState.filters[dimId];
    if (value === undefined || value === null || value === '') {
        return [];
    }
    return Array.isArray(value) ? value : [value];
}

// Path set of one dimension value across all categories.
function collectExplorerValuePaths(dim, value) {
    var collected = collectCodecPaths(_lastCodecData, dim.pathsProp, value, CODEC_CATEGORY_MAP[dim.id]);
    return (collected.movies || []).concat(collected.tvShows || []).concat(collected.music || [])
        .concat(collected.books || []).concat(collected.other || []);
}

// Intersection of every active filter except one dimension, scoped to the library scope.
// Returns null when the scope itself is stale so callers show no files.
function computePathsExcluding(exceptDimId) {
    if (!_lastCodecData) {
        return null;
    }
    var roots = getCodecsExplorerSelectedRoots();
    if (roots === null) {
        return null;
    }
    var result = null;
    for (var d = 0; d < CODEC_EXPLORER_DIMENSIONS.length; d++) {
        var dim = CODEC_EXPLORER_DIMENSIONS[d];
        if (dim.id === exceptDimId) {
            continue;
        }
        var values = getCodecsExplorerSelection(dim.id);
        if (values.length === 0) {
            continue;
        }
        var union = {};
        for (var v = 0; v < values.length; v++) {
            var paths = collectExplorerValuePaths(dim, values[v]);
            for (var p = 0; p < paths.length; p++) {
                union[paths[p]] = true;
            }
        }
        if (result === null) {
            result = union;
        } else {
            var next = {};
            for (var key in result) {
                if (Object.hasOwn(result, key) && union[key]) {
                    next[key] = true;
                }
            }
            result = next;
        }
    }
    if (result === null) {
        return {};
    }
    var scoped = {};
    for (var path in result) {
        if (Object.hasOwn(result, path) && codecExplorerPathInRoots(path, roots)) {
            scoped[path] = true;
        }
    }
    return scoped;
}

// Full result: intersection of all active filters, scoped. Empty when nothing is active.
function computeCodecsExplorerPaths() {
    var active = [];
    for (var d = 0; d < CODEC_EXPLORER_DIMENSIONS.length; d++) {
        var values = getCodecsExplorerSelection(CODEC_EXPLORER_DIMENSIONS[d].id);
        if (values.length > 0) {
            active.push({dim: CODEC_EXPLORER_DIMENSIONS[d], values: values});
        }
    }
    if (active.length === 0 || !_lastCodecData) {
        return {paths: [], active: active};
    }
    var roots = getCodecsExplorerSelectedRoots();
    if (roots === null) {
        return {paths: [], active: active};
    }
    var result = null;
    for (var i = 0; i < active.length; i++) {
        var union = {};
        for (var v = 0; v < active[i].values.length; v++) {
            var paths = collectExplorerValuePaths(active[i].dim, active[i].values[v]);
            for (var p = 0; p < paths.length; p++) {
                union[paths[p]] = true;
            }
        }
        if (result === null) {
            result = union;
        } else {
            var next = {};
            for (var key in result) {
                if (Object.hasOwn(result, key) && union[key]) {
                    next[key] = true;
                }
            }
            result = next;
        }
    }
    var out = [];
    for (var path in result) {
        if (Object.hasOwn(result, path) && codecExplorerPathInRoots(path, roots)) {
            out.push(path);
        }
    }
    out.sort();
    return {paths: out, active: active};
}

// Whether any dimension other than one holds an active selection.
function hasOtherActiveFilters(exceptDimId) {
    for (var d = 0; d < CODEC_EXPLORER_DIMENSIONS.length; d++) {
        if (CODEC_EXPLORER_DIMENSIONS[d].id !== exceptDimId
            && getCodecsExplorerSelection(CODEC_EXPLORER_DIMENSIONS[d].id).length > 0) {
            return true;
        }
    }
    return false;
}

// Facet counts for one dimension: files per option within the other active filters.
// Without other filters the facet counts equal the scoped totals, so the initial view
// offers every available value. Selected values always count (so they stay visible
// for deselection); unselected zero-count options are dropped by the caller.
function countExplorerOptions(dim) {
    var universe = aggregateDict(getCodecsExplorerScopedLibraries(), dim.countProp);
    var counts = {};
    var options = Object.keys(universe).filter(function (k) {
        return universe[k] > 0;
    }).sort(function (a, b) {
        return universe[b] - universe[a];
    });
    if (!hasOtherActiveFilters(dim.id)) {
        for (var i = 0; i < options.length; i++) {
            counts[options[i]] = universe[options[i]];
        }
        return counts;
    }
    var subset = computePathsExcluding(dim.id);
    if (subset === null) {
        for (var j = 0; j < options.length; j++) {
            counts[options[j]] = 0;
        }
        return counts;
    }
    for (var o = 0; o < options.length; o++) {
        var paths = collectExplorerValuePaths(dim, options[o]);
        var count = 0;
        for (var p = 0; p < paths.length; p++) {
            if (subset[paths[p]]) {
                count++;
            }
        }
        counts[options[o]] = count;
    }
    return counts;
}

function buildCodecsExplorerSelect(dim) {
    var counts = countExplorerOptions(dim);
    var selected = getCodecsExplorerSelection(dim.id);
    var current = selected.length > 0 ? selected[0] : '';
    var html = '<div class="codec-explorer-field">';
    html += '<label for="codecExplorer_' + escAttr(dim.id) + '">' + escHtml(T(dim.labelKey, dim.fallback)) + '</label>';
    html += '<select id="codecExplorer_' + escAttr(dim.id) + '" data-explorer-dim="' + escAttr(dim.id) + '">';
    html += '<option value="">' + escHtml(T('explorerAny', 'Any')) + '</option>';
    var options = Object.keys(counts);
    for (var i = 0; i < options.length; i++) {
        if (counts[options[i]] === 0 && options[i] !== current) {
            continue;
        }
        html += '<option value="' + escAttr(options[i]) + '"' + (options[i] === current ? ' selected' : '') + '>'
            + escHtml(options[i]) + ' (' + counts[options[i]] + ')</option>';
    }
    html += '</select></div>';
    return html;
}

function buildCodecsExplorerChecks(dim) {
    var counts = countExplorerOptions(dim);
    var selected = getCodecsExplorerSelection(dim.id);
    var html = '<fieldset class="codec-explorer-field codec-explorer-checks" data-explorer-dim="' + escAttr(dim.id) + '">';
    html += '<legend>' + escHtml(T(dim.labelKey, dim.fallback)) + '</legend>';
    var options = Object.keys(counts);
    var visible = 0;
    for (var i = 0; i < options.length; i++) {
        var isChecked = selected.indexOf(options[i]) !== -1;
        if (counts[options[i]] === 0 && !isChecked) {
            continue;
        }
        visible++;
        var inputId = 'codecExplorer_' + dim.id + '_' + i;
        html += '<label class="codec-explorer-check" for="' + escAttr(inputId) + '">'
            + '<input type="checkbox" id="' + escAttr(inputId) + '" value="' + escAttr(options[i]) + '"'
            + (isChecked ? ' checked' : '') + '>'
            + '<span>' + escHtml(options[i]) + ' (' + counts[options[i]] + ')</span></label>';
    }
    if (visible === 0) {
        html += '<span class="codec-explorer-none">' + escHtml(T('explorerNoOptions', 'No matching options.')) + '</span>';
    }
    html += '</fieldset>';
    return html;
}

function buildCodecsExplorerLibrarySelect() {
    var libs = getCodecsExplorerLibraries();
    var selected = _codecsExplorerState.library;
    var html = '<div class="codec-explorer-field"><label for="codecExplorerLibrary">' + escHtml(T('explorerLibrary', 'Library')) + '</label>';
    html += '<select id="codecExplorerLibrary">';
    html += '<option value="">' + escHtml(T('explorerAllLibraries', 'All libraries')) + '</option>';
    html += '<option value="' + CODEC_EXPLORER_TYPE_MOVIES + '"' + (selected === CODEC_EXPLORER_TYPE_MOVIES ? ' selected' : '') + '>'
        + escHtml(T('explorerScopeMovies', 'Movies (all libraries)')) + '</option>';
    html += '<option value="' + CODEC_EXPLORER_TYPE_TVSHOWS + '"' + (selected === CODEC_EXPLORER_TYPE_TVSHOWS ? ' selected' : '') + '>'
        + escHtml(T('explorerScopeTvShows', 'TV Shows (all libraries)')) + '</option>';
    for (var i = 0; i < libs.length; i++) {
        html += '<option value="' + escAttr(libs[i].LibraryName) + '"'
            + (libs[i].LibraryName === selected ? ' selected' : '') + '>'
            + escHtml(libs[i].LibraryName) + '</option>';
    }
    html += '</select></div>';
    return html;
}

function buildCodecsExplorerControls() {
    var html = buildCodecsExplorerLibrarySelect();
    for (var d = 0; d < CODEC_EXPLORER_DIMENSIONS.length; d++) {
        var dim = CODEC_EXPLORER_DIMENSIONS[d];
        html += dim.multi ? buildCodecsExplorerChecks(dim) : buildCodecsExplorerSelect(dim);
    }
    return html;
}

function buildCodecsExplorerHtml() {
    var expanded = _codecsExplorerState.expanded;
    var html = '<div class="codec-explorer">';
    html += '<button class="codec-explorer-toggle" id="codecExplorerToggle" aria-expanded="' + (expanded ? 'true' : 'false') + '"'
        + ' aria-controls="codecExplorerBody">' + mi('search')
        + '<span>' + escHtml(T('explorerTitle', 'Library Explorer')) + '</span>'
        + '<span class="codec-explorer-arrow">' + (expanded ? '&#9660;' : '&#9654;') + '</span></button>';
    html += '<div class="codec-explorer-body' + (expanded ? ' open' : '') + '" id="codecExplorerBody">';
    html += '<p class="codec-explorer-hint">' + escHtml(T('explorerHint', 'Combine codec filters to find matching files.')) + '</p>';
    html += '<div class="codec-explorer-controls" id="codecExplorerControls">' + buildCodecsExplorerControls() + '</div>';
    html += '<div class="codec-explorer-actions"><button class="codec-explorer-reset" id="codecExplorerReset">'
        + escHtml(T('explorerReset', 'Reset filters')) + '</button></div>';
    html += '<div class="codec-explorer-results" id="codecExplorerResults"></div>';
    html += '</div></div>';
    return html;
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
        labels.push(outcome.active[i].values.join(', '));
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

// Remembers the focused control across the rebuild so keyboard and touch users keep
// their place while option lists update around them.
function describeActiveExplorerControl() {
    var active = document.activeElement;
    if (!active || !active.getAttribute) {
        return null;
    }
    var dim = active.getAttribute('data-explorer-dim');
    if (active.id === 'codecExplorerLibrary') {
        return {id: 'codecExplorerLibrary'};
    }
    if (dim && active.id) {
        return {id: active.id};
    }
    if (dim && active.value !== undefined) {
        return {dim: dim, value: active.value};
    }
    return null;
}

function restoreExplorerFocus(descriptor) {
    if (!descriptor) {
        return;
    }
    var target = null;
    if (descriptor.id) {
        target = document.getElementById(descriptor.id);
    } else if (descriptor.dim) {
        var group = document.querySelector('fieldset[data-explorer-dim="' + descriptor.dim + '"]');
        if (group) {
            var boxes = group.querySelectorAll('input[type="checkbox"]');
            for (var i = 0; i < boxes.length; i++) {
                if (boxes[i].value === descriptor.value) {
                    target = boxes[i];
                    break;
                }
            }
        }
    }
    if (target && target.focus) {
        target.focus({preventScroll: true});
    }
}

function refreshCodecsExplorerControls() {
    var controls = document.getElementById('codecExplorerControls');
    if (!controls) {
        return;
    }
    var focus = describeActiveExplorerControl();
    controls.innerHTML = buildCodecsExplorerControls();
    bindCodecsExplorerControlHandlers();
    restoreExplorerFocus(focus);
    runCodecsExplorerSearch();
}

function bindCodecsExplorerControlHandlers() {
    var library = document.getElementById('codecExplorerLibrary');
    if (library) {
        library.onchange = function () {
            _codecsExplorerState.library = library.value;
            _codecsExplorerState.filters = {};
            refreshCodecsExplorerControls();
        };
    }
    var selects = document.querySelectorAll('#codecExplorerControls select[data-explorer-dim]');
    for (var i = 0; i < selects.length; i++) {
        (function (select) {
            select.onchange = function () {
                var dimId = select.getAttribute('data-explorer-dim');
                if (select.value) {
                    _codecsExplorerState.filters[dimId] = select.value;
                } else {
                    delete _codecsExplorerState.filters[dimId];
                }
                refreshCodecsExplorerControls();
            };
        })(selects[i]);
    }
    var groups = document.querySelectorAll('#codecExplorerControls fieldset[data-explorer-dim]');
    for (var g = 0; g < groups.length; g++) {
        (function (group) {
            var dimId = group.getAttribute('data-explorer-dim');
            var boxes = group.querySelectorAll('input[type="checkbox"]');
            for (var b = 0; b < boxes.length; b++) {
                boxes[b].onchange = function () {
                    var values = [];
                    var current = group.querySelectorAll('input[type="checkbox"]:checked');
                    for (var c = 0; c < current.length; c++) {
                        values.push(current[c].value);
                    }
                    if (values.length > 0) {
                        _codecsExplorerState.filters[dimId] = values;
                    } else {
                        delete _codecsExplorerState.filters[dimId];
                    }
                    refreshCodecsExplorerControls();
                };
            }
        })(groups[g]);
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
    var reset = document.getElementById('codecExplorerReset');
    if (reset) {
        reset.onclick = function () {
            _codecsExplorerState.filters = {};
            refreshCodecsExplorerControls();
        };
    }
    bindCodecsExplorerControlHandlers();
    if (!_codecExploreLinkBound) {
        _codecExploreLinkBound = true;
        document.addEventListener('click', function (evt) {
            var target = evt.target && evt.target.closest ? evt.target.closest('[data-codec-explore-library]') : null;
            if (target && !target.disabled) {
                openCodecsExplorer(target.getAttribute('data-codec-explore-library') || '');
            }
        });
        document.addEventListener('keydown', function (evt) {
            if (evt.key !== 'Enter' && evt.key !== ' ') {
                return;
            }
            var target = evt.target && evt.target.closest ? evt.target.closest('[data-codec-explore-library][role="button"]') : null;
            if (target) {
                evt.preventDefault();
                openCodecsExplorer(target.getAttribute('data-codec-explore-library') || '');
            }
        });
    }
}

// Deep-link from the Overview tab: switch to Codecs, expand the explorer and pre-select
// the scope (a library name or a type scope like "type:movies") so the admin starts
// from a meaningful selection.
function openCodecsExplorer(scope) {
    _codecsExplorerState.library = scope || '';
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
    if (body && body.scrollIntoView) {
        body.scrollIntoView({block: 'start'});
    }
}

// Prepends the explorer above the donut grid. Called on every fillCodecsData so the
// explorer always reflects the latest scan; user selections survive via module state.
function renderCodecsExplorer(container) {
    var existing = container.querySelector('.codec-explorer');
    if (existing) {
        existing.parentNode.removeChild(existing);
    }
    var tmp = document.createElement('div');
    tmp.innerHTML = buildCodecsExplorerHtml();
    container.insertBefore(tmp.firstChild, container.firstChild);
    attachCodecsExplorerHandlers();
    runCodecsExplorerSearch();
}
