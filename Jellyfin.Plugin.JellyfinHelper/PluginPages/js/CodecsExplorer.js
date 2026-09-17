'use strict';

// Library Explorer: collapsible section at the top of the Codecs tab that combines
// several codec dimensions (plus library scope) into one targeted file search, e.g. 4K
// with low bitrate. The donut charts below stay static; only this section filters.
// Filtering is faceted: options that would yield zero files under the other active
// filters are hidden, except values already selected (so they can be deselected).
// Dimensions with no options in the current scope are disabled (greyed out).
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
    const data = _lastCodecData || {};
    return [data.Movies || [], data.TvShows || [], data.Other || []];
}

// Libraries of one video group (movies or tvShows), used for type scopes.
function getCodecsExplorerTypeLibraries(type) {
    const data = _lastCodecData || {};
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
    const selected = _codecsExplorerState.library;
    if (!selected) {
        return [];
    }
    if (selected === CODEC_EXPLORER_TYPE_MOVIES || selected === CODEC_EXPLORER_TYPE_TVSHOWS) {
        const roots = [];
        for (const lib of getCodecsExplorerTypeLibraries(selected)) {
            for (const root of lib.RootPaths || []) {
                roots.push(root);
            }
        }
        return roots;
    }
    for (const lib of getCodecsExplorerLibraries()) {
        if (lib.LibraryName === selected) {
            return lib.RootPaths || [];
        }
    }
    return null;
}

function codecExplorerIsWindowsPath(path) {
    return path.length > 2 && path[1] === ':' && (path[2] === '/' || path[2] === '\\');
}

function codecExplorerTrimRoot(root) {
    let trimmed = root || '';
    while (trimmed.endsWith('/') || trimmed.endsWith('\\')) {
        trimmed = trimmed.slice(0, -1);
    }
    return trimmed;
}

// Directory-boundary prefix match so /media/movies never matches /media/movies2.
// Case follows PathComparison (the server-side source of truth): Windows-style roots
// compare case-insensitively, POSIX roots ordinally. The path style reveals the server
// OS because roots and file paths come from the same machine.
function codecExplorerPathInRoots(path, roots) {
    if (!roots || roots.length === 0) {
        return true;
    }
    let ignoreCase = false;
    for (const root of roots) {
        const probe = root || '';
        if (codecExplorerIsWindowsPath(probe) || probe.includes('\\')) {
            ignoreCase = true;
            break;
        }
    }
    const target = ignoreCase ? (path || '').toLowerCase() : (path || '');
    for (const entry of roots) {
        let root = codecExplorerTrimRoot(entry);
        if (ignoreCase) {
            root = root.toLowerCase();
        }
        if (root && (target === root || target.startsWith(root + '/') || target.startsWith(root + '\\'))) {
            return true;
        }
    }
    return false;
}

// Libraries defining the option universe under the current scope.
function getCodecsExplorerScopedLibraries() {
    const selected = _codecsExplorerState.library;
    if (selected === CODEC_EXPLORER_TYPE_MOVIES || selected === CODEC_EXPLORER_TYPE_TVSHOWS) {
        return getCodecsExplorerTypeLibraries(selected);
    }
    const scoped = [];
    for (const group of getCodecsExplorerVideoGroups()) {
        for (const lib of group) {
            if (!selected || lib.LibraryName === selected) {
                scoped.push(lib);
            }
        }
    }
    return scoped;
}

// Selected values of a dimension, always as an array (single-selects hold at most one).
function getCodecsExplorerSelection(dimId) {
    const value = _codecsExplorerState.filters[dimId];
    if (value === undefined || value === null || value === '') {
        return [];
    }
    return Array.isArray(value) ? value : [value];
}

// Active dimensions with values, optionally skipping one (for faceted counting).
function getCodecsExplorerActiveDims(exceptDimId) {
    const active = [];
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        if (dim.id === exceptDimId) {
            continue;
        }
        const values = getCodecsExplorerSelection(dim.id);
        if (values.length > 0) {
            active.push({dim: dim, values: values});
        }
    }
    return active;
}

// Path set of one dimension value across all categories.
function collectExplorerValuePaths(dim, value) {
    const collected = collectCodecPaths(_lastCodecData, dim.pathsProp, value, CODEC_CATEGORY_MAP[dim.id]);
    return (collected.movies || []).concat(collected.tvShows || []).concat(collected.music || [])
        .concat(collected.books || []).concat(collected.other || []);
}

// Shared intersection core: union per dimension (OR within multi-selects), then AND
// across dimensions. Returns the path map, or null without scan data.
function intersectExplorerFilters(active) {
    if (!_lastCodecData) {
        return null;
    }
    let result = null;
    for (const entry of active) {
        const union = {};
        for (const value of entry.values) {
            for (const path of collectExplorerValuePaths(entry.dim, value)) {
                union[path] = true;
            }
        }
        if (result === null) {
            result = union;
        } else {
            const next = {};
            for (const key of Object.keys(result)) {
                if (union[key]) {
                    next[key] = true;
                }
            }
            result = next;
        }
    }
    return result === null ? {} : result;
}

// Applies the library scope to an intersected path map. Null scope yields no files.
function scopeExplorerPaths(result) {
    const roots = getCodecsExplorerSelectedRoots();
    if (roots === null) {
        return null;
    }
    const scoped = {};
    for (const path of Object.keys(result)) {
        if (codecExplorerPathInRoots(path, roots)) {
            scoped[path] = true;
        }
    }
    return scoped;
}

// Intersection of every active filter except one dimension, scoped to the library scope.
function computePathsExcluding(exceptDimId) {
    const intersected = intersectExplorerFilters(getCodecsExplorerActiveDims(exceptDimId));
    if (intersected === null) {
        return null;
    }
    return scopeExplorerPaths(intersected);
}

// Full result: intersection of all active filters, scoped. Empty when nothing is active.
function computeCodecsExplorerPaths() {
    const active = getCodecsExplorerActiveDims(null);
    if (active.length === 0) {
        return {paths: [], active: active};
    }
    const intersected = intersectExplorerFilters(active);
    if (intersected === null) {
        return {paths: [], active: active};
    }
    const scoped = scopeExplorerPaths(intersected);
    if (scoped === null) {
        return {paths: [], active: active};
    }
    const paths = Object.keys(scoped);
    paths.sort(function (a, b) {
        return a.localeCompare(b);
    });
    return {paths: paths, active: active};
}

// Whether any dimension other than one holds an active selection.
function hasOtherActiveFilters(exceptDimId) {
    return getCodecsExplorerActiveDims(exceptDimId).length > 0;
}

// Facet counts for one dimension: files per option within the other active filters.
// Without other filters the facet counts equal the scoped totals, so the initial view
// offers every available value. Selected values always count (so they stay visible
// for deselection); unselected zero-count options are dropped by the caller.
function countExplorerOptions(dim) {
    const universe = aggregateDict(getCodecsExplorerScopedLibraries(), dim.countProp);
    const options = Object.keys(universe).filter(function (k) {
        return universe[k] > 0;
    });
    options.sort(function (a, b) {
        return universe[b] - universe[a];
    });
    const counts = {};
    if (!hasOtherActiveFilters(dim.id)) {
        for (const option of options) {
            counts[option] = universe[option];
        }
        return counts;
    }
    const subset = computePathsExcluding(dim.id);
    if (subset === null) {
        for (const option of options) {
            counts[option] = 0;
        }
        return counts;
    }
    for (const option of options) {
        let count = 0;
        for (const path of collectExplorerValuePaths(dim, option)) {
            if (subset[path]) {
                count++;
            }
        }
        counts[option] = count;
    }
    return counts;
}

// Visible options: everything with matches, plus selected values (for deselection).
function visibleExplorerOptions(counts, selected) {
    return Object.keys(counts).filter(function (option) {
        return counts[option] > 0 || selected.includes(option);
    });
}

function buildCodecsExplorerSelect(dim) {
    const counts = countExplorerOptions(dim);
    const selected = getCodecsExplorerSelection(dim.id);
    const current = selected.length > 0 ? selected[0] : '';
    const visible = visibleExplorerOptions(counts, selected);
    const disabled = visible.length === 0 ? ' disabled' : '';
    let html = '<div class="codec-explorer-field' + (disabled ? ' codec-explorer-field--disabled' : '') + '">';
    html += '<label for="codecExplorer_' + escAttr(dim.id) + '">' + escHtml(T(dim.labelKey, dim.fallback)) + '</label>';
    html += '<select id="codecExplorer_' + escAttr(dim.id) + '" data-explorer-dim="' + escAttr(dim.id) + '"' + disabled + '>';
    html += '<option value="">' + escHtml(T('explorerAny', 'Any')) + '</option>';
    for (const option of visible) {
        html += '<option value="' + escAttr(option) + '"' + (option === current ? ' selected' : '') + '>'
            + escHtml(option) + ' (' + counts[option] + ')</option>';
    }
    html += '</select></div>';
    return html;
}

function buildCodecsExplorerChecks(dim) {
    const counts = countExplorerOptions(dim);
    const selected = getCodecsExplorerSelection(dim.id);
    const visible = visibleExplorerOptions(counts, selected);
    const disabled = visible.length === 0 ? ' disabled' : '';
    let html = '<fieldset class="codec-explorer-field codec-explorer-checks' + (disabled ? ' codec-explorer-field--disabled' : '') + '"'
        + ' data-explorer-dim="' + escAttr(dim.id) + '"' + disabled + '>';
    html += '<legend>' + escHtml(T(dim.labelKey, dim.fallback)) + '</legend>';
    for (const option of visible) {
        const inputId = 'codecExplorer_' + dim.id + '_' + visible.indexOf(option);
        html += '<label class="codec-explorer-check" for="' + escAttr(inputId) + '">'
            + '<input type="checkbox" id="' + escAttr(inputId) + '" value="' + escAttr(option) + '"'
            + (selected.includes(option) ? ' checked' : '') + '>'
            + '<span>' + escHtml(option) + ' (' + counts[option] + ')</span></label>';
    }
    if (visible.length === 0) {
        html += '<span class="codec-explorer-none">' + escHtml(T('explorerNoOptions', 'No matching options.')) + '</span>';
    }
    html += '</fieldset>';
    return html;
}

function buildCodecsExplorerLibrarySelect() {
    const selected = _codecsExplorerState.library;
    let html = '<div class="codec-explorer-field"><label for="codecExplorerLibrary">' + escHtml(T('explorerLibrary', 'Library')) + '</label>';
    html += '<select id="codecExplorerLibrary">';
    html += '<option value="">' + escHtml(T('explorerAllLibraries', 'All libraries')) + '</option>';
    html += '<option value="' + CODEC_EXPLORER_TYPE_MOVIES + '"' + (selected === CODEC_EXPLORER_TYPE_MOVIES ? ' selected' : '') + '>'
        + escHtml(T('explorerScopeMovies', 'Movies (all libraries)')) + '</option>';
    html += '<option value="' + CODEC_EXPLORER_TYPE_TVSHOWS + '"' + (selected === CODEC_EXPLORER_TYPE_TVSHOWS ? ' selected' : '') + '>'
        + escHtml(T('explorerScopeTvShows', 'TV Shows (all libraries)')) + '</option>';
    for (const lib of getCodecsExplorerLibraries()) {
        html += '<option value="' + escAttr(lib.LibraryName) + '"'
            + (lib.LibraryName === selected ? ' selected' : '') + '>'
            + escHtml(lib.LibraryName) + '</option>';
    }
    html += '</select></div>';
    return html;
}

function buildCodecsExplorerControls() {
    let html = buildCodecsExplorerLibrarySelect();
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        html += dim.multi ? buildCodecsExplorerChecks(dim) : buildCodecsExplorerSelect(dim);
    }
    return html;
}

function buildCodecsExplorerHtml() {
    const expanded = _codecsExplorerState.expanded;
    let html = '<div class="codec-explorer">';
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

function explorerSummaryLabels(active) {
    const labels = [];
    for (const entry of active) {
        labels.push(entry.values.join(', '));
    }
    return labels;
}

function runCodecsExplorerSearch() {
    const host = document.getElementById('codecExplorerResults');
    if (!host || !_lastCodecData) {
        return;
    }
    const outcome = computeCodecsExplorerPaths();
    if (outcome.active.length === 0) {
        host.innerHTML = '<p class="codec-explorer-empty">' + escHtml(T('explorerPickFilter', 'Pick at least one filter above to list matching files.')) + '</p>';
        return;
    }
    const summary = outcome.paths.length + ' ' + (outcome.paths.length === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files')));
    const labels = explorerSummaryLabels(outcome.active);
    let html = '<div class="codec-explorer-summary"><span>' + escHtml(summary) + '</span>';
    html += '<span class="codec-explorer-active">' + escHtml(labels.join(' · ')) + '</span></div>';
    if (outcome.paths.length === 0) {
        html += '<p class="codec-explorer-empty">' + escHtml(T('noFilesFound', 'No files found.')) + '</p>';
        host.innerHTML = html;
        return;
    }
    const truncated = outcome.paths.length > CODEC_EXPLORER_MAX_FILES;
    const shown = truncated ? outcome.paths.slice(0, CODEC_EXPLORER_MAX_FILES) : outcome.paths;
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
    const active = document.activeElement;
    if (!active || !active.dataset) {
        return null;
    }
    if (active.id === 'codecExplorerLibrary') {
        return {id: 'codecExplorerLibrary'};
    }
    const dim = active.dataset.explorerDim;
    if (!dim) {
        return null;
    }
    if (active.id) {
        return {id: active.id};
    }
    if (active.value !== undefined) {
        return {dim: dim, value: active.value};
    }
    return null;
}

function restoreExplorerFocus(descriptor) {
    if (!descriptor) {
        return;
    }
    let target = null;
    if (descriptor.id) {
        target = document.getElementById(descriptor.id);
    } else if (descriptor.dim) {
        const group = document.querySelector('fieldset[data-explorer-dim="' + descriptor.dim + '"]');
        const boxes = group?.querySelectorAll('input[type="checkbox"]') || [];
        for (const box of boxes) {
            if (box.value === descriptor.value) {
                target = box;
                break;
            }
        }
    }
    target?.focus({preventScroll: true});
}

function refreshCodecsExplorerControls() {
    const controls = document.getElementById('codecExplorerControls');
    if (!controls) {
        return;
    }
    const focus = describeActiveExplorerControl();
    controls.innerHTML = buildCodecsExplorerControls();
    bindCodecsExplorerControlHandlers();
    restoreExplorerFocus(focus);
    runCodecsExplorerSearch();
}

function onExplorerLibraryChanged(library) {
    _codecsExplorerState.library = library.value;
    _codecsExplorerState.filters = {};
    refreshCodecsExplorerControls();
}

function onExplorerSelectChanged(select) {
    const dimId = select.dataset.explorerDim;
    if (select.value) {
        _codecsExplorerState.filters[dimId] = select.value;
    } else {
        delete _codecsExplorerState.filters[dimId];
    }
    refreshCodecsExplorerControls();
}

function onExplorerChecksChanged(group) {
    const dimId = group.dataset.explorerDim;
    const values = [];
    for (const checked of group.querySelectorAll('input[type="checkbox"]:checked')) {
        values.push(checked.value);
    }
    if (values.length > 0) {
        _codecsExplorerState.filters[dimId] = values;
    } else {
        delete _codecsExplorerState.filters[dimId];
    }
    refreshCodecsExplorerControls();
}

function bindCodecsExplorerControlHandlers() {
    const library = document.getElementById('codecExplorerLibrary');
    if (library) {
        library.onchange = function () {
            onExplorerLibraryChanged(library);
        };
    }
    for (const select of document.querySelectorAll('#codecExplorerControls select[data-explorer-dim]')) {
        select.onchange = function () {
            onExplorerSelectChanged(select);
        };
    }
    for (const group of document.querySelectorAll('#codecExplorerControls fieldset[data-explorer-dim]')) {
        for (const box of group.querySelectorAll('input[type="checkbox"]')) {
            box.onchange = function () {
                onExplorerChecksChanged(group);
            };
        }
    }
}

function attachCodecsExplorerHandlers() {
    const toggle = document.getElementById('codecExplorerToggle');
    if (toggle) {
        toggle.onclick = function () {
            const body = document.getElementById('codecExplorerBody');
            const arrow = toggle.querySelector('.codec-explorer-arrow');
            _codecsExplorerState.expanded = !_codecsExplorerState.expanded;
            body?.classList.toggle('open', _codecsExplorerState.expanded);
            toggle.setAttribute('aria-expanded', _codecsExplorerState.expanded ? 'true' : 'false');
            if (arrow) {
                arrow.innerHTML = _codecsExplorerState.expanded ? '&#9660;' : '&#9654;';
            }
        };
    }
    const reset = document.getElementById('codecExplorerReset');
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
            const target = evt.target?.closest?.('[data-codec-explore-library]');
            if (target && !target.disabled) {
                openCodecsExplorer(target.dataset.codecExploreLibrary || '');
            }
        });
        document.addEventListener('keydown', function (evt) {
            if (evt.key !== 'Enter' && evt.key !== ' ') {
                return;
            }
            const target = evt.target?.closest?.('[data-codec-explore-library][role="button"]');
            if (target) {
                evt.preventDefault();
                openCodecsExplorer(target.dataset.codecExploreLibrary || '');
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
    const tabBtn = document.querySelector('.tab-btn[data-tab="codecs"]');
    tabBtn?.click();
    if (!_lastCodecData) {
        return;
    }
    const container = document.getElementById('codecsContent');
    if (!container) {
        return;
    }
    renderCodecsExplorer(container);
    document.getElementById('codecExplorerBody')?.scrollIntoView({block: 'start'});
}

// Drops scope/filter selections that no longer exist after a rescan, so the search
// never filters by phantom values while the controls show "All libraries" or "Any".
function pruneCodecsExplorerState() {
    const libs = getCodecsExplorerLibraries();
    const scope = _codecsExplorerState.library;
    if (scope && scope !== CODEC_EXPLORER_TYPE_MOVIES && scope !== CODEC_EXPLORER_TYPE_TVSHOWS) {
        let known = false;
        for (const lib of libs) {
            if (lib.LibraryName === scope) {
                known = true;
                break;
            }
        }
        if (!known) {
            _codecsExplorerState.library = '';
            _codecsExplorerState.filters = {};
            return;
        }
    }
    const scoped = getCodecsExplorerScopedLibraries();
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        const values = getCodecsExplorerSelection(dim.id);
        if (values.length === 0) {
            continue;
        }
        const universe = aggregateDict(scoped, dim.countProp);
        const kept = values.filter(function (v) {
            return universe[v] > 0;
        });
        if (kept.length === 0) {
            delete _codecsExplorerState.filters[dim.id];
        } else if (dim.multi) {
            _codecsExplorerState.filters[dim.id] = kept;
        } else {
            _codecsExplorerState.filters[dim.id] = kept[0];
        }
    }
}

// Prepends the explorer above the donut grid. Called on every fillCodecsData so the
// explorer always reflects the latest scan; user selections survive via module state.
function renderCodecsExplorer(container) {
    pruneCodecsExplorerState();
    const existing = container.querySelector('.codec-explorer');
    existing?.remove();
    const tmp = document.createElement('div');
    tmp.innerHTML = buildCodecsExplorerHtml();
    container.insertBefore(tmp.firstChild, container.firstChild);
    attachCodecsExplorerHandlers();
    runCodecsExplorerSearch();
}
