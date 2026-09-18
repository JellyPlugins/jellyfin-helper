'use strict';

// Library Explorer (Codecs tab): Collapsible targeted file filter; charts stay static.
// - Faceted filtering: Hides 0-result options (active selections stay toggleable).
// - Scope-aware: Disables empty dimensions; multi-select libraries (OR logic, null = all).
const _codecsExplorerState = {libraries: null, filters: {}, expanded: false, scope: null, bitrateRange: null};

// Which multi-dropdown panel is currently open (one at a time). Restored across
// control rebuilds so choosing values does not collapse the panel.
let _codecMultiOpen = null;

// Which filter popover view is open. Null hides it, add lists dimensions,
// a dimension id or bitrate shows its editor.
let _codecFilterOpen = null;

// Bitrate facet id used by the popover, pills, and range state. Buckets stay donut only.
const CODEC_BITRATE_DIM = 'bitrate';
const CODEC_BITRATE_HISTOGRAM_BIN = 4;
const CODEC_BITRATE_HISTOGRAM_MAX = 80;

// Guard: the deep-link and panel-close handlers are registered once at document level.
let _codecExploreLinkBound = false;

// Upper bound for rendered result files. The tree renders every leaf eagerly, so an
// unbounded 10k-file result would freeze low-end phones. The hint tells how to narrow down.
const CODEC_EXPLORER_MAX_FILES = 300;

// Library type scopes (used by Overview cards and the scope dropdown).
const CODEC_EXPLORER_TYPE_MOVIES = 'type:movies';
const CODEC_EXPLORER_TYPE_TVSHOWS = 'type:tvshows';
const CODEC_EXPLORER_TYPE_MUSIC = 'type:music';
const CODEC_EXPLORER_TYPE_BOOKS = 'type:books';

const CODEC_EXPLORER_TYPE_GROUPS = {};
CODEC_EXPLORER_TYPE_GROUPS[CODEC_EXPLORER_TYPE_MOVIES] = 'movies';
CODEC_EXPLORER_TYPE_GROUPS[CODEC_EXPLORER_TYPE_TVSHOWS] = 'tvshows';
CODEC_EXPLORER_TYPE_GROUPS[CODEC_EXPLORER_TYPE_MUSIC] = 'music';
CODEC_EXPLORER_TYPE_GROUPS[CODEC_EXPLORER_TYPE_BOOKS] = 'books';

// Explorer dimensions in display order. Categorical dims match exact values while
// bitrate lives outside this list as an absolute range over measured values.
// Groups reuse the Codecs tab maps so the explorer always agrees with the donuts below.
const CODEC_EXPLORER_DIMENSIONS = [
    {id: 'resolutions', pathsProp: 'ResolutionPaths', countProp: 'Resolutions', groups: ['movies', 'tvshows', 'other'], labelKey: 'resolutions', fallback: 'Resolution'},
    {id: 'videoCodecs', pathsProp: 'VideoCodecPaths', countProp: 'VideoCodecs', groups: ['movies', 'tvshows', 'other'], labelKey: 'videoCodecs', fallback: 'Video codec'},
    {id: 'videoAudioCodecs', pathsProp: 'VideoAudioCodecPaths', countProp: 'VideoAudioCodecs', groups: ['movies', 'tvshows', 'other'], labelKey: 'videoAudioCodecs', fallback: 'Audio codec'},
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

function getCodecsExplorerDimension(dimId) {
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        if (dim.id === dimId) {
            return dim;
        }
    }
    return null;
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

// Root paths of the selected libraries (union). Null selection means all libraries.
// A non-null selection without known roots matches nothing.
function getCodecsExplorerSelectedRoots() {
    const selected = _codecsExplorerState.libraries;
    if (selected === null) {
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

function codecExplorerTrimRoot(root = '') {
    let trimmed = root;
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
    const target = path || '';
    for (const entry of roots) {
        const root = codecExplorerTrimRoot(entry);
        if (!root) {
            continue;
        }
        // Case follows the root style (mirrors PathComparison server-side):
        // Windows-style roots compare insensitively, POSIX roots ordinally.
        const probe = root.includes('\\') || codecExplorerIsWindowsPath(root);
        const mine = probe ? target.toLowerCase() : target;
        const theirs = probe ? root.toLowerCase() : root;
        if (mine === theirs || mine.startsWith(theirs + '/') || mine.startsWith(theirs + '\\')) {
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
            if (selected === null || selected.includes(lib.LibraryName)) {
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

// Merged measured bitrates of the scoped video libraries. Scoping mirrors the video
// groups of the categorical dims so slider and results always agree.
function getBitrateMap() {
    const map = {};
    const selected = _codecsExplorerState.libraries;
    const groups = ['movies', 'tvshows', 'other'];
    for (const group of groups) {
        for (const lib of getCodecsExplorerGroupLibraries(group)) {
            if (selected !== null && !selected.includes(lib.LibraryName)) {
                continue;
            }
            const rates = lib.VideoBitrates || {};
            for (const path of Object.keys(rates)) {
                if (typeof rates[path] === 'number') {
                    map[path] = rates[path];
                }
            }
        }
    }
    return map;
}

// Stable slider bounds from the scoped map. Bounds ignore the remaining filters so the
// thumbs never jump while the user refines the other facets.
function getBitrateBounds() {
    const map = getBitrateMap();
    let min = Infinity;
    let max = -Infinity;
    let count = 0;
    for (const path of Object.keys(map)) {
        const value = map[path];
        if (value < min) {
            min = value;
        }
        if (value > max) {
            max = value;
        }
        count++;
    }
    if (count === 0) {
        return null;
    }
    const lo = Math.floor(min);
    let hi = Math.ceil(max);
    if (hi <= lo) {
        hi = lo + 1;
    }
    return {min: lo, max: hi, count: count};
}

// Whether an absolute bitrate range currently narrows the result.
function hasActiveBitrateRange() {
    return _codecsExplorerState.bitrateRange !== null;
}

// Files of a path set whose measured bitrate falls inside the range.
// Files without a measured value never match an active range.
function applyBitrateRange(set, range) {
    const map = getBitrateMap();
    const next = {};
    for (const path of Object.keys(set)) {
        const value = map[path];
        if (typeof value === 'number' && value >= range.min && value <= range.max) {
            next[path] = true;
        }
    }
    return next;
}

// Histogram over the subset that ignores the range itself. Counts follow the remaining
// filters so the bars preview what the range would keep.
function getBitrateHistogram() {
    const subset = computePathsExcluding(CODEC_BITRATE_DIM);
    const edges = Math.ceil(CODEC_BITRATE_HISTOGRAM_MAX / CODEC_BITRATE_HISTOGRAM_BIN);
    const bins = [];
    for (let i = 0; i <= edges; i++) {
        bins.push(0);
    }
    const map = getBitrateMap();
    const keys = subset === null ? Object.keys(map) : Object.keys(subset);
    for (const path of keys) {
        const value = map[path];
        if (typeof value !== 'number') {
            continue;
        }
        let index = Math.floor(value / CODEC_BITRATE_HISTOGRAM_BIN);
        if (index < 0) {
            index = 0;
        }
        if (index >= bins.length) {
            index = bins.length - 1;
        }
        bins[index]++;
    }
    return bins;
}

// Measured bitrate of one file, or null when the scan predates per file values.
function getExplorerFileBitrate(path) {
    const value = getBitrateMap()[path];
    return typeof value === 'number' ? value : null;
}

// Short pill text for an active range relative to the data bounds.
function formatBitrateRange(range, bounds) {
    const lo = Math.round(range.min);
    const hi = Math.round(range.max);
    if (bounds !== null && lo <= bounds.min && hi >= bounds.max) {
        return T('explorerAny', 'Any');
    }
    if (bounds !== null && lo <= bounds.min) {
        return '≤ ' + hi + ' Mbps';
    }
    if (bounds !== null && hi >= bounds.max) {
        return '≥ ' + lo + ' Mbps';
    }
    return lo + '–' + hi + ' Mbps';
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
function unionExplorerPaths(dim, values) {
    const union = {};
    for (const value of values) {
        for (const path of collectExplorerValuePaths(dim, value)) {
            union[path] = true;
        }
    }
    return union;
}

function intersectPathMaps(first, second) {
    const next = {};
    for (const key of Object.keys(first)) {
        if (second[key]) {
            next[key] = true;
        }
    }
    return next;
}

function intersectExplorerFilters(active) {
    if (!_lastCodecData) {
        return null;
    }
    let result = null;
    for (const entry of active) {
        const union = unionExplorerPaths(entry.dim, entry.values);
        result = result === null ? union : intersectPathMaps(result, union);
    }
    return result === null ? {} : result;
}

// Applies the library scope to an intersected path map. A selection without known
// roots (stale names) yields no files.
function scopeExplorerPaths(result) {
    const selected = _codecsExplorerState.libraries;
    const roots = getCodecsExplorerSelectedRoots();
    if (selected !== null && roots.length === 0) {
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
// Facet subsets cached per control rebuild: every option count of one refresh pass
// reuses the same per-dimension subset instead of rescanning the library each time.
let _explorerSubsetCache = {};

function computePathsExcluding(exceptDimId) {
    if (!_lastCodecData) {
        return null;
    }
    if (Object.hasOwn(_explorerSubsetCache, exceptDimId)) {
        return _explorerSubsetCache[exceptDimId];
    }
    const subset = scopeExplorerPaths(intersectExplorerFilters(getCodecsExplorerActiveDims(exceptDimId)));
    // The range is orthogonal to every categorical dim, so it narrows each facet
    // subset. The histogram asks with the bitrate id to see the uncut distribution.
    if (exceptDimId !== CODEC_BITRATE_DIM && hasActiveBitrateRange()) {
        const ranged = applyBitrateRange(subset, _codecsExplorerState.bitrateRange);
        _explorerSubsetCache[exceptDimId] = ranged;
        return ranged;
    }

    _explorerSubsetCache[exceptDimId] = subset;
    return subset;
}

// All classified files of the selected libraries (union of their size maps),
// root-filtered and sorted. Used when a scope is chosen but no filter is active,
// so picking a library immediately shows its files instead of an empty hint.
function collectScopePaths() {
    const selected = _codecsExplorerState.libraries;
    if (selected === null) {
        return [];
    }
    const roots = getCodecsExplorerSelectedRoots();
    if (roots.length === 0) {
        return [];
    }
    const seen = {};
    for (const lib of getCodecsExplorerLibraries()) {
        if (!selected.includes(lib.LibraryName)) {
            continue;
        }
        const sizes = lib.FileSizes || {};
        for (const path of Object.keys(sizes)) {
            if (codecExplorerPathInRoots(path, roots)) {
                seen[path] = true;
            }
        }
    }
    const paths = Object.keys(seen);
    paths.sort(function (a, b) {
        return a.localeCompare(b);
    });
    return paths;
}

// Full result: intersection of all active filters, scoped. A bare scope without
// filters lists the scope files; no scope and no filters show the idle hint.
// A lone range starts from every measured file, or from the scope files when scoped.
function computeCodecsExplorerPaths() {
    const active = getCodecsExplorerActiveDims(null);
    const rangeActive = hasActiveBitrateRange();
    if (active.length === 0 && !rangeActive) {
        return {paths: collectScopePaths(), active: active, bitrateActive: false};
    }
    let scoped = null;
    if (active.length === 0) {
        scoped = {};
        if (_codecsExplorerState.libraries === null) {
            const map = getBitrateMap();
            for (const path of Object.keys(map)) {
                scoped[path] = true;
            }
        } else {
            for (const path of collectScopePaths()) {
                scoped[path] = true;
            }
        }
    } else {
        const intersected = intersectExplorerFilters(active);
        if (intersected === null) {
            return {paths: [], active: active, bitrateActive: rangeActive};
        }
        scoped = scopeExplorerPaths(intersected);
    }
    if (rangeActive) {
        scoped = applyBitrateRange(scoped, _codecsExplorerState.bitrateRange);
    }
    const paths = Object.keys(scoped);
    paths.sort(function (a, b) {
        return a.localeCompare(b);
    });
    return {paths: paths, active: active, bitrateActive: rangeActive};
}

// Whether any facet other than one narrows the result. The absolute range counts
// as a facet, so option counts stay narrowed while only it is active.
function hasOtherActiveFilters(exceptDimId) {
    if (exceptDimId !== CODEC_BITRATE_DIM && hasActiveBitrateRange()) {
        return true;
    }

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
    for (let index = 0; index < visible.length; index++) {
        const option = visible[index];
        const inputId = 'codecMulti_' + dim.id + '_' + index;
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

// Total media files of one library, used as the scope option count. Mirrors exactly
// what the scope listing can show (FileSizes covers video, audio and books only).
function countLibraryFiles(lib) {
    return (lib.VideoFileCount || 0) + (lib.AudioFileCount || 0) + (lib.BookFileCount || 0);
}

// Library scope as a multi-dropdown: empty means all libraries, otherwise the
// selected libraries are combined. Mirrors the dimension multi-dropdowns.
function buildCodecsExplorerLibraryMulti() {
    const libs = getCodecsExplorerLibraries();
    const selected = _codecsExplorerState.libraries || [];
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
    for (let index = 0; index < libs.length; index++) {
        const lib = libs[index];
        const inputId = 'codecLibrary_' + index;
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
    let html = '<div class="codec-filter-bar">';
    html += buildCodecsExplorerLibraryMulti();
    html += buildCodecsExplorerFilterAdd();
    html += buildCodecsExplorerPills();
    html += '</div>';
    return html;
}

// Add filter button with its popover. The popover shows either the dimension list or
// the editor of the picked dimension, so the bar itself always stays a single row.
function buildCodecsExplorerFilterAdd() {
    const open = _codecFilterOpen !== null;
    let html = '<div class="codec-filter-add" data-filter-add="1">';
    html += '<button type="button" class="codec-filter-add-btn" id="codecFilterAddBtn" data-filter-add-btn="1"'
        + ' aria-expanded="' + (open ? 'true' : 'false') + '" aria-haspopup="true">'
        + mi('movie_filter') + '<span>' + escHtml(T('explorerAddFilter', 'Add filter')) + '</span></button>';
    if (open) {
        html += '<div class="codec-filter-pop" data-filter-pop="1" role="dialog">';
        html += _codecFilterOpen === 'add' ? buildCodecsExplorerDimList() : buildCodecsExplorerDimEditor();
        html += '</div>';
    }
    html += '</div>';
    return html;
}

// Dimension picker rows with live summaries. A single open editor keeps long value
// lists from pushing each other off screen.
function buildCodecsExplorerDimList() {
    let html = '<div class="codec-filter-dimlist">';
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        const selected = getCodecsExplorerSelection(dim.id);
        const summary = selected.length > 0 ? selected.join(', ') : T('explorerAny', 'Any');
        html += '<button type="button" class="codec-filter-dim" data-filter-dim="' + escAttr(dim.id) + '">'
            + '<span class="codec-filter-dim-name">' + escHtml(T(dim.labelKey, dim.fallback)) + '</span>'
            + '<span class="codec-filter-dim-summary">' + escHtml(summary) + '</span>'
            + '<span class="codec-filter-dim-go">›</span></button>';
    }
    const bounds = getBitrateBounds();
    const bitrateSummary = hasActiveBitrateRange()
        ? formatBitrateRange(_codecsExplorerState.bitrateRange, bounds)
        : T('explorerAny', 'Any');
    html += '<button type="button" class="codec-filter-dim" data-filter-dim="' + CODEC_BITRATE_DIM + '">'
        + '<span class="codec-filter-dim-name">' + escHtml(T('videoBitrate', 'Video bitrate')) + '</span>'
        + '<span class="codec-filter-dim-summary">' + escHtml(bitrateSummary) + '</span>'
        + '<span class="codec-filter-dim-go">›</span></button>';
    html += '</div>';
    return html;
}

// Editor of the picked dimension with a step back to the list. Multi dims reuse the
// existing option widget forced open so counts and toggle logic stay in one place.
function buildCodecsExplorerDimEditor() {
    const dimId = _codecFilterOpen;
    let title = '';
    let body = '';
    if (dimId === CODEC_BITRATE_DIM) {
        title = T('videoBitrate', 'Video bitrate');
        body = buildBitrateEditor();
    } else {
        const dim = getCodecsExplorerDimension(dimId);
        if (!dim) {
            return '';
        }
        title = T(dim.labelKey, dim.fallback);
        if (dim.multi) {
            _codecMultiOpen = dim.id;
            body = buildCodecsExplorerMulti(dim);
        } else {
            body = buildCodecsExplorerSelect(dim);
        }
    }
    let html = '<div class="codec-filter-editor">';
    html += '<div class="codec-filter-editor-head"><button type="button" class="codec-filter-back" id="codecFilterBack" data-filter-back="1"'
        + ' aria-label="' + escAttr(T('explorerBack', 'Back')) + '">‹</button>'
        + '<span class="codec-filter-editor-title">' + escHtml(title) + '</span></div>';
    html += '<div class="codec-filter-editor-body">' + body + '</div>';
    html += '</div>';
    return html;
}

// Active filters as removable pills. One pill per dimension keeps the bar to one line
// no matter how many values hide behind a multi select.
function buildCodecsExplorerPills() {
    return '<div class="codec-filter-pills" id="codecFilterPills">' + buildCodecsExplorerPillsInner() + '</div>';
}

// Inner pills without the container. Live previews swap this markup alone so the
// surrounding bar and its handlers survive slider drags.
function buildCodecsExplorerPillsInner() {
    let html = '';
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        const selected = getCodecsExplorerSelection(dim.id);
        if (selected.length === 0) {
            continue;
        }
        html += '<span class="codec-pill"><span class="codec-pill-label">'
            + escHtml(T(dim.labelKey, dim.fallback) + ': ' + selected.join(', '))
            + '</span><button type="button" class="codec-pill-remove" data-pill-clear="' + escAttr(dim.id) + '"'
            + ' aria-label="' + escAttr(T('remove', 'Remove')) + '">×</button></span>';
    }
    if (hasActiveBitrateRange()) {
        html += '<span class="codec-pill"><span class="codec-pill-label">'
            + escHtml(T('videoBitrate', 'Video bitrate') + ': ' + formatBitrateRange(_codecsExplorerState.bitrateRange, getBitrateBounds()))
            + '</span><button type="button" class="codec-pill-remove" data-pill-clear-bitrate="1"'
            + ' aria-label="' + escAttr(T('remove', 'Remove')) + '">×</button></span>';
    }
    return html;
}

// Absolute range editor with a distribution preview. Numbers allow exact jumps like
// 30 while sliders give quick sweeps, so both edit the same range.
function buildBitrateEditor() {
    const bounds = getBitrateBounds();
    if (bounds === null) {
        return '<p class="codec-explorer-none">' + escHtml(T('explorerBitrateNoData', 'No bitrate data yet. Run a fresh scan to enable absolute bitrate filtering.')) + '</p>';
    }
    const range = _codecsExplorerState.bitrateRange || {min: bounds.min, max: bounds.max};
    const bins = getBitrateHistogram();
    let peak = 1;
    for (const count of bins) {
        if (count > peak) {
            peak = count;
        }
    }
    let html = '<div class="codec-bitrate" data-bitrate-editor="1">';
    html += '<div class="codec-bitrate-hist" aria-hidden="true">';
    for (let i = 0; i < bins.length; i++) {
        const pct = Math.max(2, Math.round(bins[i] / peak * 100));
        const edge = i * CODEC_BITRATE_HISTOGRAM_BIN;
        html += '<span class="codec-bitrate-bar" style="height:' + pct + '%" title="' + escAttr(edge + ' Mbps: ' + bins[i]) + '"></span>';
    }
    html += '</div>';
    html += '<div class="codec-bitrate-row"><label for="codecBitrateMin">' + escHtml(T('explorerBitrateMin', 'Min (Mbps)')) + '</label>'
        + '<input type="range" id="codecBitrateMinRange" data-bitrate="minRange" min="' + bounds.min + '" max="' + bounds.max + '" step="1" value="' + range.min + '">'
        + '<input type="number" id="codecBitrateMin" data-bitrate="min" min="' + bounds.min + '" max="' + bounds.max + '" step="1" value="' + range.min + '"></div>';
    html += '<div class="codec-bitrate-row"><label for="codecBitrateMax">' + escHtml(T('explorerBitrateMax', 'Max (Mbps)')) + '</label>'
        + '<input type="range" id="codecBitrateMaxRange" data-bitrate="maxRange" min="' + bounds.min + '" max="' + bounds.max + '" step="1" value="' + range.max + '">'
        + '<input type="number" id="codecBitrateMax" data-bitrate="max" min="' + bounds.min + '" max="' + bounds.max + '" step="1" value="' + range.max + '"></div>';
    html += '<div class="codec-bitrate-actions"><button type="button" class="codec-explorer-reset" data-bitrate-clear="1">'
        + escHtml(T('explorerAny', 'Any')) + '</button></div>';
    html += '</div>';
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
        labels.push(T(entry.dim.labelKey, entry.dim.fallback) + ': ' + entry.values.join(', '));
    }
    return labels;
}

// Per-file detail cards (rendered on demand below a result row): path, size, every
// matching codec/language value plus a Watched expander listing who watched it.
let _explorerDetailCache = {};

// First dimension value whose paths contain the file, or null.
function findExplorerDimValue(dim, path) {
    const scoped = getCodecsExplorerScopedLibraries(dim);
    const universe = getExplorerUniverse(dim, scoped);
    for (const option of Object.keys(universe)) {
        const paths = collectExplorerValuePaths(dim, option);
        for (const candidate of paths) {
            if (candidate === path) {
                return option;
            }
        }
    }
    return null;
}

// Every matching value (for multi-valued dimensions like languages).
function findExplorerDimValues(dim, path) {
    const scoped = getCodecsExplorerScopedLibraries(dim);
    const universe = getExplorerUniverse(dim, scoped);
    const hits = [];
    for (const option of Object.keys(universe)) {
        const paths = collectExplorerValuePaths(dim, option);
        for (const candidate of paths) {
            if (candidate === path) {
                hits.push(option);
                break;
            }
        }
    }
    return hits;
}

function getExplorerFileSize(path) {
    for (const lib of getCodecsExplorerLibraries()) {
        const sizes = lib.FileSizes || {};
        if (Object.hasOwn(sizes, path)) {
            return sizes[path];
        }
    }
    return null;
}

// Watchers of one file with play counts, or null when never watched.
function getExplorerWatchers(path) {
    const watchers = [];
    for (const lib of getCodecsExplorerLibraries()) {
        const details = lib.WatchedDetails || {};
        const entries = details[path] || [];
        for (const entry of entries) {
            watchers.push(entry);
        }
    }
    return watchers;
}

function explorerDetailRow(label, value) {
    return '<div class="codec-file-row"><span class="codec-file-label">' + escHtml(label) + '</span>'
        + '<span class="codec-file-value">' + escHtml(value) + '</span></div>';
}

function explorerWatchedDetail(watchers) {
    if (watchers.length === 0) {
        return explorerDetailRow(T('watched', 'Watched'), T('explorerNeverWatched', 'Never watched'));
    }
    let inner = '';
    for (const watcher of watchers) {
        const plays = watcher.PlayCount === 1
            ? T('explorerOnePlay', '1 play')
            : T('explorerManyPlays', '{count} plays').replace('{count}', String(watcher.PlayCount));
        let line = (watcher.Username || '?') + ' — ' + plays;
        if (watcher.LastPlayedDate) {
            line += ' · ' + new Date(watcher.LastPlayedDate).toLocaleString();
        }
        inner += '<div class="codec-file-watcher">' + escHtml(line) + '</div>';
    }
    return '<details class="codec-file-watched"><summary>'
        + escHtml(T('watched', 'Watched') + ' (' + watchers.length + ')') + '</summary>' + inner + '</details>';
}

function buildExplorerFileDetail(path) {
    if (Object.hasOwn(_explorerDetailCache, path)) {
        return _explorerDetailCache[path];
    }
    const singleDims = ['videoCodecs', 'containers', 'resolutions', 'dynamicRanges',
        'videoAudioCodecs', 'musicAudioCodecs', 'bookFormats'];
    const multiDims = ['audioLanguages', 'subtitleLanguages'];
    let html = '<div class="codec-file-detail">';
    html += '<div class="codec-file-path" title="' + escAttr(path) + '">' + escHtml(path) + '</div>';
    const size = getExplorerFileSize(path);
    if (size !== null) {
        html += explorerDetailRow(T('explorerFileSize', 'Size'), formatBytes(size));
    }
    const bitrate = getExplorerFileBitrate(path);
    if (bitrate !== null) {
        html += explorerDetailRow(T('videoBitrate', 'Video bitrate'), bitrate.toFixed(1) + ' Mbps');
    }
    for (const dimId of singleDims) {
        const dim = getCodecsExplorerDimension(dimId);
        const value = findExplorerDimValue(dim, path);
        if (value !== null) {
            html += explorerDetailRow(T(dim.labelKey, dim.fallback), value);
        }
    }
    for (const dimId of multiDims) {
        const dim = getCodecsExplorerDimension(dimId);
        const values = findExplorerDimValues(dim, path);
        if (values.length > 0) {
            html += explorerDetailRow(T(dim.labelKey, dim.fallback), values.join(', '));
        }
    }
    html += explorerWatchedDetail(getExplorerWatchers(path));
    html += '</div>';
    _explorerDetailCache[path] = html;
    return html;
}

function toggleExplorerFileDetail(leaf) {
    const next = leaf.nextElementSibling;
    if (next?.classList.contains('codec-file-detail')) {
        next.remove();
        leaf.classList.remove('codec-file-open');
        return;
    }
    const path = leaf.title || '';
    if (!path) {
        return;
    }
    const tmp = document.createElement('div');
    tmp.innerHTML = buildExplorerFileDetail(path);
    leaf.parentNode.insertBefore(tmp.firstChild, next);
    leaf.classList.add('codec-file-open');
}

function bindExplorerFileDetails(host) {
    for (const leaf of host.querySelectorAll('.tree-leaf')) {
        if (leaf.dataset.detailBound) {
            continue;
        }
        leaf.dataset.detailBound = '1';
        leaf.setAttribute('tabindex', '0');
        leaf.setAttribute('role', 'button');
        leaf.addEventListener('click', function () {
            toggleExplorerFileDetail(leaf);
        });
        leaf.addEventListener('keydown', function (evt) {
            if (evt.key === 'Enter' || evt.key === ' ') {
                evt.preventDefault();
                toggleExplorerFileDetail(leaf);
            }
        });
    }
}

// Splits result paths into media-type buckets like the donut drill-downs, so the
// tree shows Movies / TV Shows / Other sections instead of a flat list.
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
    const scoped = _codecsExplorerState.libraries !== null;
    if (outcome.active.length === 0 && !outcome.bitrateActive && !scoped) {
        host.innerHTML = '<p class="codec-explorer-empty">' + escHtml(T('explorerPickFilter', 'Pick at least one filter above to list matching files.')) + '</p>';
        return;
    }
    const summary = outcome.paths.length + ' ' + (outcome.paths.length === 1 ? escHtml(T('file', 'file')) : escHtml(T('files', 'files')));
    const labels = explorerSummaryLabels(outcome.active);
    if (outcome.bitrateActive) {
        labels.push(T('videoBitrate', 'Video bitrate') + ': ' + formatBitrateRange(_codecsExplorerState.bitrateRange, getBitrateBounds()));
    }
    const scope = _codecsExplorerState.libraries;
    if (scope !== null && scope.length > 0) {
        labels.unshift(scope.join(', '));
    }
    let html = '<div class="codec-explorer-summary"><span data-explorer-count="' + outcome.paths.length + '">'
        + escHtml(summary) + '</span>';
    html += '<span class="codec-explorer-active">' + escHtml(labels.join(' · ')) + '</span></div>';
    if (outcome.paths.length === 0) {
        html += '<p class="codec-explorer-empty">' + escHtml(T('noFilesFound', 'No files found.')) + '</p>';
        host.innerHTML = html;
        return;
    }
    const truncated = outcome.paths.length > CODEC_EXPLORER_MAX_FILES;
    const shown = truncated ? outcome.paths.slice(0, CODEC_EXPLORER_MAX_FILES) : outcome.paths;
    if (truncated) {
        html += '<p class="codec-explorer-truncated">' + escHtml(T('explorerTruncated', 'Showing first {count} matches — refine filters to narrow down.')
            .replace('{count}', String(CODEC_EXPLORER_MAX_FILES))) + '</p>';
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
    bindExplorerFileDetails(host);
}

// Remembers the focused control across the rebuild so keyboard and touch users keep
// their place while option lists update around them.
function describeActiveExplorerControl() {
    const active = document.activeElement;
    if (!active?.dataset) {
        return null;
    }
    if (active.id === 'codecMultiToggle_libraries') {
        return {id: 'codecMultiToggle_libraries'};
    }
    if (active.dataset.libraryOption !== undefined && active.value !== undefined) {
        return {libraries: active.value};
    }
    // Popover controls rebuild around the user, so remember them by intent:
    // dim buttons refocus the editor, pill buttons return to the add button.
    if (active.dataset.filterDim !== undefined) {
        return {filterDim: active.dataset.filterDim};
    }
    if (active.dataset.pillClear !== undefined || active.dataset.pillClearBitrate !== undefined) {
        return {filterAdd: true};
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
    if (descriptor.filterAdd) {
        document.getElementById('codecFilterAddBtn')?.focus({preventScroll: true});
        return;
    }
    if (descriptor.filterDim) {
        const back = document.getElementById('codecFilterBack');
        if (_codecFilterOpen === descriptor.filterDim && back) {
            back.focus({preventScroll: true});
            return;
        }
        const dimBtn = document.querySelector('[data-filter-dim="' + descriptor.filterDim + '"]');
        dimBtn?.focus({preventScroll: true});
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
    _explorerSubsetCache = {};
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
    // Keep dimension filters: faceting recomputes every option, and pruning drops
    // only values the new scope cannot produce. Unchecking everything lifts the
    // restriction (back to all libraries).
    _codecsExplorerState.scope = values.length > 0 ? values : null;
    _codecsExplorerState.libraries = values.length > 0 ? values : null;
    pruneCodecsExplorerState();
    refreshCodecsExplorerControls();
}

function onExplorerSelectChanged(select) {
    const dimId = select.dataset.explorerDim;
    if (select.value) {
        _codecsExplorerState.filters[dimId] = select.value;
    } else {
        delete _codecsExplorerState.filters[dimId];
    }
    // A single pick completes the choice, so the popover collapses back to the
    // bar. Multi editors stay open for picking further values.
    _codecFilterOpen = null;
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

// Closes the add filter popover after an outside pick. Guarded so result rerenders
// never collapse the editor while the user is still picking values.
function closeFilterPop() {
    if (_codecFilterOpen !== null) {
        _codecFilterOpen = null;
        refreshCodecsExplorerControls();
    }
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
    bindCodecsExplorerFilterHandlers();
}

function bindCodecsExplorerFilterHandlers() {
    const addBtn = document.querySelector('[data-filter-add-btn]');
    if (addBtn) {
        addBtn.onclick = function () {
            _codecFilterOpen = _codecFilterOpen === null ? 'add' : null;
            refreshCodecsExplorerControls();
        };
    }
    for (const dimBtn of document.querySelectorAll('[data-filter-dim]')) {
        dimBtn.onclick = function () {
            _codecFilterOpen = dimBtn.dataset.filterDim;
            refreshCodecsExplorerControls();
        };
    }
    const backBtn = document.querySelector('[data-filter-back]');
    if (backBtn) {
        backBtn.onclick = function () {
            _codecFilterOpen = 'add';
            refreshCodecsExplorerControls();
        };
    }
    bindPillHandlers();
    bindBitrateEditorHandlers();
}

function bindPillHandlers() {
    for (const pill of document.querySelectorAll('[data-pill-clear]')) {
        pill.onclick = function () {
            delete _codecsExplorerState.filters[pill.dataset.pillClear];
            refreshCodecsExplorerControls();
        };
    }
    const bitratePill = document.querySelector('[data-pill-clear-bitrate]');
    if (bitratePill) {
        bitratePill.onclick = function () {
            _codecsExplorerState.bitrateRange = null;
            refreshCodecsExplorerControls();
        };
    }
}

// Reads the editor inputs as one normalized range. Partial input falls back to the
// data bounds so typing never traps the user in an empty result.
function readBitrateEditor() {
    const bounds = getBitrateBounds();
    const minInput = document.getElementById('codecBitrateMin');
    const maxInput = document.getElementById('codecBitrateMax');
    let min = minInput ? parseFloat(minInput.value) : bounds.min;
    let max = maxInput ? parseFloat(maxInput.value) : bounds.max;
    if (!Number.isFinite(min)) {
        min = bounds.min;
    }
    if (!Number.isFinite(max)) {
        max = bounds.max;
    }
    if (min < bounds.min) {
        min = bounds.min;
    }
    if (max > bounds.max) {
        max = bounds.max;
    }
    if (min > max) {
        min = max;
    }
    return {min: min, max: max};
}

// Live preview without a control rebuild. Pills and results follow the thumbs at
// once while the editor keeps grab and focus until commit.
function previewBitrateRange() {
    _codecsExplorerState.bitrateRange = readBitrateEditor();
    const pills = document.getElementById('codecFilterPills');
    if (pills) {
        pills.innerHTML = buildCodecsExplorerPillsInner();
        bindPillHandlers();
    }
    runCodecsExplorerSearch();
}

// Commits the range on release. A range that spans everything equals Any,
// so it collapses back to null instead of filtering nothing.
function commitBitrateRange() {
    const bounds = getBitrateBounds();
    const range = readBitrateEditor();
    if (bounds !== null && range.min <= bounds.min && range.max >= bounds.max) {
        _codecsExplorerState.bitrateRange = null;
    } else {
        _codecsExplorerState.bitrateRange = range;
    }
    refreshCodecsExplorerControls();
}

function bindBitrateEditorHandlers() {
    const editor = document.querySelector('[data-bitrate-editor]');
    if (!editor || getBitrateBounds() === null) {
        return;
    }
    const minRange = document.getElementById('codecBitrateMinRange');
    const maxRange = document.getElementById('codecBitrateMaxRange');
    const minInput = document.getElementById('codecBitrateMin');
    const maxInput = document.getElementById('codecBitrateMax');
    if (minRange && minInput) {
        minRange.oninput = function () {
            minInput.value = minRange.value;
            previewBitrateRange();
        };
        minRange.onchange = function () {
            commitBitrateRange();
        };
    }
    if (maxRange && maxInput) {
        maxRange.oninput = function () {
            maxInput.value = maxRange.value;
            previewBitrateRange();
        };
        maxRange.onchange = function () {
            commitBitrateRange();
        };
    }
    if (minInput) {
        minInput.onchange = function () {
            commitBitrateRange();
        };
    }
    if (maxInput) {
        maxInput.onchange = function () {
            commitBitrateRange();
        };
    }
    const clear = editor.querySelector('[data-bitrate-clear]');
    if (clear) {
        clear.onclick = function () {
            _codecsExplorerState.bitrateRange = null;
            refreshCodecsExplorerControls();
        };
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
            _codecsExplorerState.scope = null;
            _codecsExplorerState.libraries = null;
            _codecsExplorerState.filters = {};
            _codecsExplorerState.bitrateRange = null;
            _codecFilterOpen = null;
            _codecMultiOpen = null;
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
            // Pills stay interactive without collapsing the editor, so removing
            // several filters never forces the popover through reopen hops.
            const filterArea = evt.target?.closest?.('[data-filter-add]');
            const pillArea = evt.target?.closest?.('#codecFilterPills');
            if (!filterArea && !pillArea) {
                closeFilterPop();
            }
            const target = evt.target?.closest?.('[data-codec-explore-library]');
            if (target && !target.disabled) {
                openCodecsExplorer(target.dataset.codecExploreLibrary || '');
            }
        });
        document.addEventListener('keydown', function (evt) {
            if (evt.key === 'Escape') {
                closeExplorerMultis();
                closeFilterPop();
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
        return null;
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
// The raw intent survives until scan data arrives: type markers can only resolve
// to library names once the libraries are known.
function openCodecsExplorer(scope) {
    _codecsExplorerState.scope = scope || null;
    _codecsExplorerState.libraries = resolveExplorerScope(scope);
    _codecsExplorerState.filters = {};
    _codecsExplorerState.bitrateRange = null;
    _codecFilterOpen = null;
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
    if (_codecsExplorerState.libraries !== null) {
        _codecsExplorerState.libraries = pruneExplorerScopeNames(
            getCodecsExplorerLibraries(), _codecsExplorerState.libraries);
    }
    for (const dim of CODEC_EXPLORER_DIMENSIONS) {
        pruneExplorerDimSelection(dim);
    }
    // The tier dimension retired in favor of the absolute range. Stale tier picks
    // from older state shapes must vanish instead of filtering by phantom values.
    if (_codecsExplorerState.filters.videoBitrate !== undefined) {
        delete _codecsExplorerState.filters.videoBitrate;
    }
    pruneBitrateRange();
}

// Clamps the absolute range into fresh bounds after rescan or scope change.
// A range without any measured files cannot match, so it is dropped.
function pruneBitrateRange() {
    const range = _codecsExplorerState.bitrateRange;
    if (range === null) {
        return;
    }
    const bounds = getBitrateBounds();
    if (bounds === null) {
        _codecsExplorerState.bitrateRange = null;
        return;
    }
    let min = Math.min(Math.max(range.min, bounds.min), bounds.max);
    let max = Math.min(Math.max(range.max, bounds.min), bounds.max);
    if (min > max) {
        min = max;
    }
    if (min === bounds.min && max === bounds.max) {
        _codecsExplorerState.bitrateRange = null;
        return;
    }
    _codecsExplorerState.bitrateRange = {min: min, max: max};
}

function pruneExplorerScopeNames(libs, names) {
    const known = [];
    for (const name of names) {
        if (libs.some(function (lib) {
            return lib.LibraryName === name;
        })) {
            known.push(name);
        }
    }
    return known;
}

function pruneExplorerDimSelection(dim) {
    const values = getCodecsExplorerSelection(dim.id);
    if (values.length === 0) {
        return;
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

// Prepends the explorer above the donut grid. Called on every fillCodecsData so the
// explorer always reflects the latest scan; user selections survive via module state.
function renderCodecsExplorer(container) {
    // Late-resolve a pending deep-link scope: type markers need scan data, which may
    // have arrived after the link was clicked.
    if (_lastCodecData) {
        _codecsExplorerState.libraries = resolveExplorerScope(_codecsExplorerState.scope);
    }
    pruneCodecsExplorerState();
    _explorerDetailCache = {};
    const existing = container.querySelector('.codec-explorer');
    existing?.remove();
    const tmp = document.createElement('div');
    tmp.innerHTML = buildCodecsExplorerHtml();
    container.insertBefore(tmp.firstChild, container.firstChild);
    attachCodecsExplorerHandlers();
    runCodecsExplorerSearch();
}
