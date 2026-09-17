'use strict';

// Library Explorer (Codecs tab): Collapsible targeted file filter; charts stay static.
// - Faceted filtering: Hides 0-result options (active selections stay toggleable).
// - Scope-aware: Disables empty dimensions; multi-select libraries (OR logic, empty = all).
var _codecsExplorerState = {libraries: [], filters: {}, expanded: false};

// Which multi-dropdown panel is currently open (one at a time). Restored across
// control rebuilds so choosing values does not collapse the panel.
var _codecMultiOpen = null;

// Guard: the deep-link and panel-close handlers are registered once at document level.
var _codecExploreLinkBound = false;

// Upper bound for rendered result files. The tree renders every leaf eagerly, so an
// unbounded 10k-file result would freeze low-end phones. The hint tells how to narrow down.
var CODEC_EXPLORER_MAX_FILES = 300;

// Library type scopes (used by Overview cards and the scope dropdown).
var CODEC_EXPLORER_TYPE_MOVIES = 'type:movies';
var CODEC_EXPLORER_TYPE_TVSHOWS = 'type:tvshows';
var CODEC_EXPLORER_TYPE_MUSIC = 'type:music';
var CODEC_EXPLORER_TYPE_BOOKS = 'type:books';

var CODEC_EXPLORER_TYPE_GROUPS = {};
CODEC_EXPLORER_TYPE_GROUPS[CODEC_EXPLORER_TYPE_MOVIES] = 'movies';
CODEC_EXPLORER_TYPE_GROUPS[CODEC_EXPLORER_TYPE_TVSHOWS] = 'tvshows';
CODEC_EXPLORER_TYPE_GROUPS[CODEC_EXPLORER_TYPE_MUSIC] = 'music';
CODEC_EXPLORER_TYPE_GROUPS[CODEC_EXPLORER_TYPE_BOOKS] = 'books';

// Explorer dimensions in display order. Dropdown dimensions allow multiple values (OR
// within the dimension, e.g. German + English audio); the rest take a single value.
// Paths reuse the Codecs tab maps so the explorer always agrees with the donuts below.
var CODEC_EXPLORER_DIMENSIONS = [
    {id: 'resolutions', pathsProp: 'ResolutionPaths', countProp: 'Resolutions', groups: ['movies', 'tvshows', 'other'], labelKey: 'resolutions', fallback: 'Resolution'},
    {id: 'videoCodecs', pathsProp: 'VideoCodecPaths', countProp: 'VideoCodecs', groups: ['movies', 'tvshows', 'other'], labelKey: 'videoCodecs', fallback: 'Video codec'},
    {id: 'videoAudioCodecs', pathsProp: 'VideoAudioCodecPaths', countProp: 'VideoAudioCodecs', groups: ['movies', 'tvshows', 'other'], labelKey: 'videoAudioCodecs', fallback: 'Audio codec'},
    {id: 'videoBitrate', pathsProp: 'VideoBitrateTierPaths', countProp: 'VideoBitrateTiers', groups: ['movies', 'tvshows', 'other'], labelKey: 'videoBitrate', fallback: 'Video bitrate'},
    {id: 'dynamicRanges', pathsProp: 'DynamicRangePaths', countProp: 'DynamicRanges', groups: ['movies', 'tvshows', 'other'], labelKey: 'dynamicRange', fallback: 'Dynamic range'},
    {id: 'audioLanguages', pathsProp: 'AudioLanguagePaths', countProp: 'AudioLanguages', groups: ['movies', 'tvshows', 'other'], labelKey: 'audioLanguages', fallback: 'Audio language', multi: true},
    {id: 'subtitleLanguages', pathsProp: 'SubtitleLanguagePaths', countProp: 'SubtitleLanguages', groups: ['movies', 'tvshows', 'other'], labelKey: 'subtitleLanguages', fallback: 'Subtitle language', multi: true},
    {id: 'watched', pathsProps: ['WatchedTierPaths', 'WatchedByUserPaths'], groups: ['movies', 'tvshows', 'other'], labelKey: 'watched', fallback: 'Watched', multi: true, perUser: true},
    {id: 'musicAudioCodecs', pathsProp: 'MusicAudioCodecPaths', countProp: 'MusicAudioCodecs', groups: ['music'], labelKey: 'musicAudioCodecs', fallback: 'Music audio codec'},
    {id: 'bookFormats', pathsProp: 'BookFormatPaths', countProp: 'BookFormats', groups: ['books'], labelKey: 'bookFormats', fallback: 'Book format'},
    {id: 'containers', pathsProp: 'ContainerFormatPaths', countProp: 'ContainerFormats', groups: ['movies', 'tvshows', 'music', 'books', 'other'], labelKey: 'containerFormats', fallback: 'Container'}
];

function getCodecsExplorerState() {
    return _codecsExplorerState;
}

function getCodecsExplorerLibraries() {
    if (!_lastCodecData || !Array.isArray(_lastCodecData.Libraries)) {
        return [];
    }
    return _lastCodecData.Libraries;
}

// Libraries of one group (movies, tvshows, music, books, other).
function getCodecsExplorerGroupLibraries(group) {
    const data = _lastCodecData || {};
    if (group === 'movies') {
        return data.Movies || [];
    }
    if (group === 'tvshows') {
        return data.TvShows || [];
    }
    if (group === 'music') {
        return data.Music || [];
    }
    if (group === 'books') {
        return data.Books || [];
    }
    return data.Other || [];
}

// Root paths of the selected libraries (union). Empty selection means all libraries.
// Unknown names resolve to no roots, which yields no files.
function getCodecsExplorerSelectedRoots() {
    const selected = _codecsExplorerState.libraries;
    if (selected.length === 0) {
        return [];
    }
    const roots = [];
    for (const lib of getCodecsExplorerLibraries()) {
        if (selected.includes(lib.LibraryName)) {
            for (const root of lib.RootPaths || []) {
                roots.push(root);
            }
        }
    }
    return roots;
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

// Directory-boundary match (e.g., prevents /movies matching /movies2).
// Case-sensitivity uses PathComparison (Windows = insensitive, POSIX = ordinal).
// Path style identifies the server OS (roots and paths are local to it).
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

// Libraries defining the option universe of one dimension: its media groups,
// narrowed to the selected libraries when any are chosen.
function getCodecsExplorerScopedLibraries(dim) {
    const selected = _codecsExplorerState.libraries;
    const scoped = [];
    for (const group of dim.groups) {
        for (const lib of getCodecsExplorerGroupLibraries(group)) {
            if (selected.length === 0 || selected.includes(lib.LibraryName)) {
                scoped.push(lib);
            }
        }
    }
    return scoped;
}

// Option universe of one dimension: scoped counts, or per-user watched breakdown.
function getExplorerUniverse(dim, scoped) {
    if (dim.perUser) {
        return countWatchedUniverse(scoped);
    }
    return aggregateDict(scoped, dim.countProp);
}

// Per-user watched universe: Never watched plus one entry per username that watched
// at least one scoped file. Counts come from path lengths (files), matching the donut.
function countWatchedUniverse(scoped) {
    const universe = {};
    for (const lib of scoped) {
        const never = lib.WatchedTiers ? lib.WatchedTiers['Never watched'] : 0;
        if (never > 0) {
            universe['Never watched'] = (universe['Never watched'] || 0) + never;
        }
        const byUser = lib.WatchedByUserPaths || {};
        for (const user of Object.keys(byUser)) {
            const count = byUser[user] ? byUser[user].length : 0;
            if (count > 0) {
                universe[user] = (universe[user] || 0) + count;
            }
        }
    }
    return universe;
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

// Categories object for collectCodecPaths, derived from the dimension groups.
function getExplorerCategories(dim) {
    const has = function (group) {
        return dim.groups.includes(group);
    };
    return {movies: has('movies'), tvShows: has('tvshows'), music: has('music'), books: has('books'), other: has('other')};
}

// Path set of one dimension value across its categories (watched spans two maps:
// Never watched lives in WatchedTierPaths, usernames in WatchedByUserPaths).
function collectExplorerValuePaths(dim, value) {
    const props = dim.pathsProps || [dim.pathsProp];
    const categories = getExplorerCategories(dim);
    let flat = [];
    for (const prop of props) {
        const collected = collectCodecPaths(_lastCodecData, prop, value, categories);
        flat = flat.concat(collected.movies || []).concat(collected.tvShows || []).concat(collected.music || [])
            .concat(collected.books || []).concat(collected.other || []);
    }
    return flat;
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

// Applies the library scope to an intersected path map. A selection without known
// roots (stale names) yields no files.
function scopeExplorerPaths(result) {
    const roots = getCodecsExplorerSelectedRoots();
    if (_codecsExplorerState.libraries.length > 0 && roots.length === 0) {
        return {};
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

// Facet counts per dimension option based on active filters (equals scope totals if unfiltered).
// Selected options always retain counts so they can be deselected; callers drop unselected 0-count options.
function countExplorerOptions(dim) {
    const universe = getExplorerUniverse(dim, getCodecsExplorerScopedLibraries(dim));
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
    const disabled = visible.length === 0;
    let html = '<div class="codec-explorer-field' + (disabled ? ' codec-explorer-field--disabled' : '') + '">';
    html += '<label for="codecExplorer_' + escAttr(dim.id) + '">' + escHtml(T(dim.labelKey, dim.fallback)) + '</label>';
    html += '<select id="codecExplorer_' + escAttr(dim.id) + '" data-explorer-dim="' + escAttr(dim.id) + '"'
        + (disabled ? ' disabled' : '') + '>';
    html += '<option value="">' + escHtml(T('explorerAny', 'Any')) + '</option>';
    for (const option of visible) {
        html += '<option value="' + escAttr(option) + '"' + (option === current ? ' selected' : '') + '>'
            + escHtml(option) + ' (' + counts[option] + ')</option>';
    }
    html += '</select></div>';
    return html;
}

// Summary text for the multi-dropdown toggle: up to 3 names, then "+n".
function explorerMultiSummary(selected) {
    if (selected.length === 0) {
        return T('explorerAny', 'Any');
    }
    if (selected.length <= 3) {
        return selected.join(', ');
    }
    return selected.slice(0, 2).join(', ') + ' +' + (selected.length - 2);
}

// Multi-value dropdown mirroring the Settings library multi-select: a toggle button
// with a summary plus a panel of checkboxes with per-option match counts.
function buildCodecsExplorerMulti(dim) {
    const counts = countExplorerOptions(dim);
    const selected = getCodecsExplorerSelection(dim.id);
    const visible = visibleExplorerOptions(counts, selected);
    const disabled = visible.length === 0;
    const open = _codecMultiOpen === dim.id && !disabled;
    let html = '<div class="codec-explorer-field codec-multi' + (disabled ? ' codec-explorer-field--disabled' : '') + '"'
        + ' data-multi-dim="' + escAttr(dim.id) + '">';
    html += '<span class="codec-multi-label" id="codecMultiLabel_' + escAttr(dim.id) + '">'
        + escHtml(T(dim.labelKey, dim.fallback)) + '</span>';
    html += '<button type="button" class="codec-multi-toggle" id="codecMultiToggle_' + escAttr(dim.id) + '" data-multi-toggle="' + escAttr(dim.id) + '"'
        + ' aria-expanded="' + (open ? 'true' : 'false') + '" aria-labelledby="codecMultiLabel_' + escAttr(dim.id) + '"'
        + (disabled ? ' disabled' : '') + '>';
    html += '<span class="codec-multi-summary">' + escHtml(explorerMultiSummary(selected)) + '</span>';
    html += '<span class="codec-multi-chevron">' + mi('expand_more') + '</span></button>';
    html += '<div class="codec-multi-panel" data-multi-panel="' + escAttr(dim.id) + '"' + (open ? '' : ' hidden') + '>';
    for (const option of visible) {
        const inputId = 'codecMulti_' + dim.id + '_' + visible.indexOf(option);
        html += '<label class="codec-multi-item" for="' + escAttr(inputId) + '">'
            + '<input type="checkbox" id="' + escAttr(inputId) + '" value="' + escAttr(option) + '"'
            + (selected.includes(option) ? ' checked' : '') + '>'
            + '<span class="codec-multi-name">' + escHtml(option) + '</span>'
            + '<span class="codec-multi-count">(' + counts[option] + ')</span></label>';
    }
    if (visible.length === 0) {
        html += '<span class="codec-explorer-none">' + escHtml(T('explorerNoOptions', 'No matching options.')) + '</span>';
    }
    html += '</div></div>';
    return html;
}

// Total media files of one library, used as the scope option count.
function countLibraryFiles(lib) {
    return (lib.VideoFileCount || 0) + (lib.AudioFileCount || 0) + (lib.BookFileCount || 0) + (lib.OtherFileCount || 0);
}

// Library scope as a multi-dropdown: empty means all libraries, otherwise the
// selected libraries are combined. Mirrors the dimension multi-dropdowns.
function buildCodecsExplorerLibraryMulti() {
    const libs = getCodecsExplorerLibraries();
    const selected = _codecsExplorerState.libraries;
    const disabled = libs.length === 0;
    const open = _codecMultiOpen === 'libraries' && !disabled;
    let html = '<div class="codec-explorer-field codec-multi' + (disabled ? ' codec-explorer-field--disabled' : '') + '"'
        + ' data-library-widget="1">';
    html += '<span class="codec-multi-label" id="codecMultiLabel_libraries">'
        + escHtml(T('explorerLibrary', 'Library')) + '</span>';
    html += '<button type="button" class="codec-multi-toggle" id="codecMultiToggle_libraries" data-library-toggle="1"'
        + ' aria-expanded="' + (open ? 'true' : 'false') + '" aria-labelledby="codecMultiLabel_libraries"'
        + (disabled ? ' disabled' : '') + '>';
    html += '<span class="codec-multi-summary">' + escHtml(libraryMultiSummary(selected)) + '</span>';
    html += '<span class="codec-multi-chevron">' + mi('expand_more') + '</span></button>';
    html += '<div class="codec-multi-panel" data-library-panel="1"' + (open ? '' : ' hidden') + '>';
    for (const lib of libs) {
        const inputId = 'codecLibrary_' + libs.indexOf(lib);
        html += '<label class="codec-multi-item" for="' + escAttr(inputId) + '">'
            + '<input type="checkbox" id="' + escAttr(inputId) + '" value="' + escAttr(lib.LibraryName) + '"'
            + (selected.includes(lib.LibraryName) ? ' checked' : '') + ' data-library-option="1">'
            + '<span class="codec-multi-name">' + escHtml(lib.LibraryName) + '</span>'
            + '<span class="codec-multi-count">(' + countLibraryFiles(lib) + ')</span></label>';
    }
    html += '</div></div>';
    return html;
}

function libraryMultiSummary(selected) {
    if (selected.length === 0) {
        return T('explorerAllLibraries', 'All libraries');
    }
    return explorerMultiSummary(selected);
}

function buildCodecsExplorerControls() {
    let html = buildCodecsExplorerLibraryMulti();
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        html += dim.multi ? buildCodecsExplorerMulti(dim) : buildCodecsExplorerSelect(dim);
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

// Splits result paths into media-type buckets like the donut drill-downs, so the
// tree shows Movies / TV Shows / Other sections instead of one flat list.
function groupExplorerResults(paths) {
    const grouped = {movies: [], tvShows: [], music: [], books: [], other: []};
    const roots = {movies: [], tvShows: [], music: [], books: [], other: []};
    const buckets = [
        {key: 'movies', libs: getCodecsExplorerGroupLibraries('movies')},
        {key: 'tvShows', libs: getCodecsExplorerGroupLibraries('tvshows')},
        {key: 'music', libs: getCodecsExplorerGroupLibraries('music')},
        {key: 'books', libs: getCodecsExplorerGroupLibraries('books')},
        {key: 'other', libs: getCodecsExplorerGroupLibraries('other')}
    ];
    for (const bucket of buckets) {
        for (const lib of bucket.libs) {
            for (const root of lib.RootPaths || []) {
                roots[bucket.key].push(root);
            }
        }
    }
    for (const path of paths) {
        let placed = false;
        for (const bucket of buckets) {
            if (codecExplorerPathInRoots(path, roots[bucket.key]) && roots[bucket.key].length > 0) {
                grouped[bucket.key].push(path);
                placed = true;
                break;
            }
        }
        if (!placed) {
            grouped.other.push(path);
        }
    }
    return {grouped: grouped, roots: roots};
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
    if (_codecsExplorerState.libraries.length > 0) {
        labels.unshift(_codecsExplorerState.libraries.join(', '));
    }
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
    const split = groupExplorerResults(shown);
    html += '<div class="file-tree-panel file-tree-panel-visible">';
    html += renderFileTree(
        {movies: split.grouped.movies, tvShows: split.grouped.tvShows, music: split.grouped.music, books: split.grouped.books, other: split.grouped.other, rootPaths: split.roots},
        labels.join(' · '),
        null);
    html += '</div>';
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
    if (active.id === 'codecMultiToggle_libraries') {
        return {id: 'codecMultiToggle_libraries'};
    }
    if (active.dataset.libraryOption !== undefined && active.value !== undefined) {
        return {libraries: active.value};
    }
    const dim = active.dataset.explorerDim || active.dataset.multiDim || active.dataset.multiToggle;
    if (!dim) {
        return null;
    }
    // Checkboxes are located by value: option order follows live match counts and
    // may reshuffle between rebuilds, so element ids are not stable for them.
    if (active.type === 'checkbox' && active.value !== undefined) {
        return {dim: dim, value: active.value};
    }
    if (active.id) {
        return {id: active.id};
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
    } else if (descriptor.libraries) {
        const widget = document.querySelector('[data-library-widget]');
        const boxes = widget?.querySelectorAll('[data-library-option]') || [];
        for (const box of boxes) {
            if (box.value === descriptor.libraries) {
                target = box;
                break;
            }
        }
    } else if (descriptor.dim) {
        const scope = document.querySelector('[data-multi-dim="' + descriptor.dim + '"]');
        const boxes = scope?.querySelectorAll('input[type="checkbox"]') || [];
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

function onExplorerLibrariesChanged() {
    const widget = document.querySelector('[data-library-widget]');
    const values = [];
    if (widget) {
        for (const checked of widget.querySelectorAll('[data-library-option]:checked')) {
            values.push(checked.value);
        }
    }
    _codecsExplorerState.libraries = values;
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

function onExplorerMultiChanged(dimId) {
    const widget = document.querySelector('[data-multi-dim="' + dimId + '"]');
    const values = [];
    if (widget) {
        for (const checked of widget.querySelectorAll('input[type="checkbox"]:checked')) {
            values.push(checked.value);
        }
    }
    if (values.length > 0) {
        _codecsExplorerState.filters[dimId] = values;
    } else {
        delete _codecsExplorerState.filters[dimId];
    }
    refreshCodecsExplorerControls();
}

function setMultiPanelOpen(dimId, open) {
    if (open) {
        _codecMultiOpen = dimId;
    } else if (_codecMultiOpen === dimId) {
        _codecMultiOpen = null;
    }
    for (const widget of document.querySelectorAll('[data-multi-dim]')) {
        const id = widget.dataset.multiDim;
        const isOpen = open && id === dimId;
        const panel = widget.querySelector('[data-multi-panel]');
        const toggle = widget.querySelector('[data-multi-toggle]');
        panel?.toggleAttribute('hidden', !isOpen);
        toggle?.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
    }
    const libWidget = document.querySelector('[data-library-widget]');
    if (libWidget) {
        const isOpen = open && dimId === 'libraries';
        libWidget.querySelector('[data-library-panel]')?.toggleAttribute('hidden', !isOpen);
        libWidget.querySelector('[data-library-toggle]')?.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
        if (isOpen) {
            _codecMultiOpen = 'libraries';
        } else if (_codecMultiOpen === 'libraries') {
            _codecMultiOpen = null;
        }
    }
}

function closeExplorerMultis() {
    setMultiPanelOpen(null, false);
}

function bindCodecsExplorerControlHandlers() {
    const libToggle = document.querySelector('[data-library-toggle]');
    if (libToggle) {
        libToggle.onclick = function () {
            setMultiPanelOpen('libraries', _codecMultiOpen !== 'libraries');
        };
    }
    for (const box of document.querySelectorAll('[data-library-option]')) {
        box.onchange = function () {
            onExplorerLibrariesChanged();
        };
    }
    for (const select of document.querySelectorAll('#codecExplorerControls select[data-explorer-dim]')) {
        select.onchange = function () {
            onExplorerSelectChanged(select);
        };
    }
    for (const toggle of document.querySelectorAll('[data-multi-toggle]')) {
        toggle.onclick = function () {
            const dimId = toggle.dataset.multiToggle;
            setMultiPanelOpen(dimId, _codecMultiOpen !== dimId);
        };
    }
    for (const widget of document.querySelectorAll('[data-multi-dim]')) {
        const dimId = widget.dataset.multiDim;
        for (const box of widget.querySelectorAll('input[type="checkbox"]')) {
            box.onchange = function () {
                onExplorerMultiChanged(dimId);
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
            const multi = evt.target?.closest?.('[data-multi-dim]');
            const libraries = evt.target?.closest?.('[data-library-widget]');
            if (!multi && !libraries) {
                closeExplorerMultis();
            }
            const target = evt.target?.closest?.('[data-codec-explore-library]');
            if (target && !target.disabled) {
                openCodecsExplorer(target.dataset.codecExploreLibrary || '');
            }
        });
        document.addEventListener('keydown', function (evt) {
            if (evt.key === 'Escape') {
                closeExplorerMultis();
                return;
            }
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

// Resolves a deep-link scope to concrete library names: a type marker expands to
// every library of that group, a single name stays as-is, an array passes through.
function resolveExplorerScope(scope) {
    if (!scope) {
        return [];
    }
    if (Array.isArray(scope)) {
        return scope.slice();
    }
    const typeGroup = CODEC_EXPLORER_TYPE_GROUPS[scope];
    if (typeGroup) {
        const names = [];
        for (const lib of getCodecsExplorerGroupLibraries(typeGroup)) {
            names.push(lib.LibraryName);
        }
        return names;
    }
    return [scope];
}

// Deep-link from the Overview tab: switch to Codecs, expand the explorer and pre-select
// the libraries (a name, name list, or type marker) so the admin starts scoped.
function openCodecsExplorer(scope) {
    _codecsExplorerState.libraries = resolveExplorerScope(scope);
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
    const known = [];
    for (const name of _codecsExplorerState.libraries) {
        let found = false;
        for (const lib of libs) {
            if (lib.LibraryName === name) {
                found = true;
                break;
            }
        }
        if (found) {
            known.push(name);
        }
    }
    _codecsExplorerState.libraries = known;
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        const values = getCodecsExplorerSelection(dim.id);
        if (values.length === 0) {
            continue;
        }
        const universe = getExplorerUniverse(dim, getCodecsExplorerScopedLibraries(dim));
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
