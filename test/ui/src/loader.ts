import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createInstrumenter } from 'istanbul-lib-instrument';
import { CONFIG_PAGE_MODULES, DISCOVERY_MODULE, NATIVE_TABS_MODULE } from './jsModules.js';

const HERE = dirname(fileURLToPath(import.meta.url));
export const REPO_ROOT = join(HERE, '..', '..', '..');
const CONFIG_JS_DIR = join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper', 'PluginPages', 'js');
const DISCOVERY_DIR = join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper', 'js');

export type UiBridge = Record<string, any>;

const instrumenter = createInstrumenter({ esModules: false, produceSourceMap: false });

// Instruments for per-file coverage, then strips strict mode so the bundle
// evaluates exactly like the shipped IIFE scope. The optional label overrides
// the coverage key: the discovery guard test and the unwrapped behavioral
// bundle are different code for the same file, so they must report under
// different keys or the merge collides on mismatched statement maps.
function prepared(absPath: string, label?: string): string {
  const rel = label ?? relative(REPO_ROOT, absPath).replaceAll('\\', '/');
  return instrumenter
    .instrumentSync(readFileSync(absPath, 'utf8'), rel)
    .replace(/^[ \t]*'use strict';[ \t]*\r?$/m, '');
}

// The shipped client scripts are self-running IIFEs, so their inner helpers are
// unreachable from the shared scope. For behavioral tests we unwrap the IIFE at
// the source level (before instrumentation) so the helpers become top-level
// declarations the bridge can expose. The listed bootstrap lines are dropped so
// evaluation only declares - tests drive each function explicitly, and no stray
// timers or listeners leak into other specs. The prod file is never touched;
// only this in-memory copy is reshaped.
function preparedUnwrapped(absPath: string, drops: Array<[RegExp, string]>): string {
  const rel = relative(REPO_ROOT, absPath).replaceAll('\\', '/');
  const name = absPath.split(/[\\/]/).pop();
  let src = readFileSync(absPath, 'utf8');
  const open = src.indexOf('(function () {');
  if (open === -1) {
    throw new Error(`${name} IIFE markers not found - prod shape changed?`);
  }
  src = src.slice(0, open) + src.slice(open + '(function () {'.length);
  const close = src.lastIndexOf('})();');
  if (close === -1) {
    throw new Error(`${name} IIFE markers not found - prod shape changed?`);
  }
  src = src.slice(0, close) + src.slice(close + '})();'.length);
  for (const [re, what] of drops) {
    if (!re.test(src)) {
      throw new Error(`${name} ${what} not found - prod shape changed?`);
    }
    src = src.replace(re, (m) => '\n'.repeat(m.split('\n').length - 1));
  }
  return instrumenter.instrumentSync(src, rel).replace(/^[ \t]*'use strict';[ \t]*\r?$/m, '');
}

const DISCOVERY_DROPS: Array<[RegExp, string]> = [
  // Early-returns the whole IIFE once the guard flag is set, so a second fresh
  // scope would return before reaching the test-only return. Driven separately
  // through the guard bundle (loadDiscovery).
  [/if \(window\.__jfhDiscoverySidebarInitialized\) \{\s*return;\s*\}/, 'double-init guard'],
  [/^[ \t]*initDiscoveryLifecycle\(\);[ \t]*\r?$/m, 'lifecycle initialization'],
];

const NATIVE_TABS_DROPS: Array<[RegExp, string]> = [
  [/if \(window\.JellyfinHelperTabs\) \{\s*return;\s*\}/, 'double-load guard'],
  [/^[ \t]*window\.JellyfinHelperTabs = \{[\s\S]*?\};[ \t]*\r?$/m, 'registry export'],
  [/^[ \t]*init\(\);[ \t]*\r?$/m, 'init call'],
];


// The appended return is test-only wiring and never touches production files.
// A renamed helper fails loudly here instead of silently untesting code.
function evaluate(sources: string[], expected: string[]): UiBridge {
  // The shipped scripts are classic (non-module) scripts that must evaluate in
  // one shared function scope exactly as the browser IIFE does; new Function is
  // the test harness boundary that reproduces that scope. Input is our own
  // repo sources, never user data. typeof is required in the generated picker
  // because an unknown name would be an undeclared identifier (a bare === would
  // ReferenceError); the check below reads a property, so it compares directly.
  const pick = expected.map((n) => `${n}: typeof ${n} === 'undefined' ? undefined : ${n}`).join(', ');
  const run = new Function(`${sources.join('\n')}\nreturn { ${pick} };`) as () => UiBridge; // NOSONAR intentional harness eval of trusted repo sources
  const bridge = run();
  const missing = expected.filter((n) => bridge[n] === undefined);
  if (missing.length > 0) {
    throw new Error(`UI test bridge is missing helpers (renamed in prod?): ${missing.join(', ')}`);
  }
  return bridge;
}

let configPage: UiBridge | null = null;

function buildConfigPage(): UiBridge {
  return evaluate(
    CONFIG_PAGE_MODULES.map((f) => prepared(join(CONFIG_JS_DIR, f))),
    CONFIG_EXPECTED,
  );
}

export function loadConfigPage(): UiBridge {
  if (!configPage) configPage = buildConfigPage();
  return configPage;
}

// Fresh bundle scope per call: module state (trash tracking, caches, timers)
// starts pristine, so stateful tests never depend on execution order.
export function freshConfigPage(): UiBridge {
  return buildConfigPage();
}

const CONFIG_EXPECTED = [
        'renderShell', 'initTabs', 'doTabSwitch', 'fillScanData', 'bindPageLifecycle',
        'updateLastScanBadge', 'loadStatistics', 'loadLatestStatistics',
        'isSeerrConfigured', 'refreshDiscoveryAccessState',
        'attachDiscoveryHintHandler',
        'refreshTraktRowState', 'loadTraktOfficialStatus', 'updateRecsTabVisibility',
        'updateSeerrUIState', 'validateTrashPath', 'isSensitiveSystemPath',
        'openFolderBrowserDialog', 'initFolderBrowser', 'browseTo', 'loadLibraryPathsForBrowser',
        'escHtml', 'escAttr', 'allowSafeHtml', 'mi', 'getCssVar', 'loadTranslations', 'applyStaticTranslations', 'showButtonFeedback', 'apiGetOptional',
        'formatBytes', 'getScoreThresholdClass', 'trimTrailingSlashes', 'getPathSegments',
        'collectFlatPaths', 'collectDictPaths', 'buildPathTree', 'countTreeItems',
        'encodeTreeKey', 'resolveTreeNode', 'renderTreeLevel', 'renderTreeFolder',
        'renderTreeLeaf', 'renderFileTree', 'toggleTreeNode', 'ensureTreeChildren',
        'collapseAllTreeNodes', 'expandOneTreeNode', 'runTreeAction',
        'setTreeCappedNote', 'refreshTreeCappedNote', 'pruneFileTreeRegistry',
        'bindFileTreeHandlers', 'aggregateDict',
        'attachTogglePanelHandlers',
        'normalizeStatisticsLibraries', 'orderLibrariesByName', 'appendMissingUnionLibraries',
        'pluralize', 'formatTimeAgo', 'describeApiError', 'createDialogOverlay',
        'createDialogBtn', 'removeDialogById',
        'showAutoSaveIndicatorOverlay', 'createSaveIndicator', 'calculateFadeDelay',
        'resolveArrInstances',
        'getCollectionBadge', 'librarySizeCell', 'buildBarSegments', 'loadCleanupStats',
        'fillOverviewData', 'collectHealthPaths', 'renderHealthChecks', 'fillHealthData',
        'loadTrashHealthSection',
        'renderLogsTab', 'initLogsTab', 'loadLogLevelFromConfig', 'saveLogLevelToConfig',
        'destroyLogsTab', 'resetLogsTabState', 'loadLogs', 'downloadLogs', 'clearLogs',
        'formatLogTimestamp',
        'formatGranularityLabel', 'interpolateDataPoints', 'advanceBucketDate',
        'bucketStartDate', 'projectToGranularity', 'pickLevelForSpan', 'computeNiceYScale',
        'clampTrendX', 'collectVisiblePoints', 'trendDeltaClass', 'trendDeltaSign',
        'wheelZoomFactor', 'wheelHitsZoomLimit', 'toggleInsightPanel', 'buildLargestTree',
        'sumPositiveSizes', 'buildRecentTree', 'buildRecentItemRow', 'insightLibrarySortOrder',
        'groupByLibrary', 'getInsightTypeBadge', 'formatInsightDate',
        'renderTrendChart', 'applyTrendFrame', 'loadTrendData', 'loadInsightsData', 'renderInsightCards',
        'polarToCartesian', 'describeArc', 'renderDonutSvg', 'renderCodecBreakdown',
        'renderDonutChart', 'collectCodecPaths', 'collectTrackLabelMeta',
        'collectDrilldownMeta', 'collectResolutionDimensions', 'countWatchedUsers',
        'orderedCodecCardIds', 'reorderFullOrder', 'prefersReducedMotion',
        'groupCardsIntoRows', 'findSlotIndex', 'colsOf', 'indexOfCard',
        'restoreCardToIndex', 'moveCardInList', 'announceCodec', 'toggleCodecBreakdown',
        'showDonutTooltip', 'hideDonutTooltip',
        'isSegmentPanelOpen', 'triggerCodecRowForSegment', 'attachCodecClickHandlers',
        'attachCodecCardDragHandlers', 'beginCodecDrag', 'moveCodecCloneToPointer',
        'onCodecDragPointerMove', 'onCodecDragPointerUp', 'reorderCodecCardTo',
        'resetCodecDragForRerender', 'settleCodecFlip', 'flipCodecReorder', 'teardownCodecDrag',
        'updateCodecAutoScroll', 'saveCodecCardOrder', 'showCodecSaveFeedback',
        'fillCodecsData', 'loadCodecCardOrderOnce', 'renderCodecsExplorer', 'openCodecsExplorer',
        'commitCodecCardOrderFromDom',
        'getBitrateMap', 'getBitrateBounds',
        'applyBitrateRange', 'hasActiveBitrateRange', 'getBitrateHistogram',
        'getExplorerFileBitrate', 'getTrackLabelMap', 'getExplorerFileTrackLabels',
        'formatBitrateRange', 'normalizeBitrateRange', 'getCodecsExplorerDimension',
        'getCodecsExplorerLibraries', 'getCodecsExplorerSelectedRoots',
        'getCodecsExplorerSelection', 'isCodecsExplorerExcluded',
        'codecExplorerIsWindowsPath', 'codecExplorerTrimRoot', 'codecExplorerPathInRoots',
        'getExplorerUniverse', 'countWatchedUniverse', 'getCachedExplorerMaps',
        'renderArrInstances', 'renderArrInstanceRow', 'setArrInstanceApiKeys',
        'collectArrInstances', 'updateArrCollapsibleCount', 'createArrCountText',
        'addArrInstance', 'removeArrInstance', 'testArrConnection', 'attachTestHandlers',
        'attachRemoveHandlers', 'attachAddHandlers', 'syncArrLibraryPickers',
        'filterLibrariesForArrType', 'initArrButtons', 'compareArr', 'refreshArrInstanceStatus',
        'parseCommaSeparatedSet', 'formatPathList', 'buildSettingsPayload',
        'takeSettingsSnapshot', 'hasUnsavedSettings', 'renderSaveBand', 'renderLibraryMultiSelect',
        'toggleLibraryDropdown', 'updateLibraryMultiSelectSummary',
        'getLibraryMultiSelectValue', 'libraryMultiSelectEmptyLabel',
        'libraryEntryName', 'libraryEntryType', 'showTrashPathError',
        'buildSaveErrorLabel', 'doSaveSettings', 'saveSettings', 'loadSettings', 'removeTrashDialog',
        'showTrashDisableDialog', 'showTrashDeleteConfirmation',
        'showTrashPathChangeDialog', 'probeBackendReachability',
        'attachBackupHandlers', 'triggerBackupExport', 'triggerBackupImport',
        'showBackupImportConfirmation', 'doBackupImport',
        'toggleCollapsible', 'ensembleShareText', 'ensembleShareHtml',
        'renderEnsembleDiagnostics', 'loadEnsembleDiagnostics', 'renderRecommendationCard',
        'renderCompactWatchProfile', 'renderCompactActivityTable', 'getTopGenresFromDistribution',
        'initRecommendationsTab', 'loadRecommendations',
        'renderDiscoverySection', 'loadDiscoveryForUser', 'findUserDiscovery',
        'renderDiscoveryCards', 'renderDiscoveryCard', 'handleDiscoveryRequest',
        'handleDiscoveryRequestResponse', 'handleDiscoveryRequestError',
        'submitDiscoveryRequest', 'submitDiscoveryRequestWithProfile',
        'markDiscoveryItemRequested', 'showSeerrUserPopup',
        'showInlineCheckboxIndicator', 'showLibraryMultiSelectIndicator',
        'getCodecsExplorerState', 'getDimPathIndex', 'intersectPathMaps', 'intersectExplorerFilters', 'findExplorerFocusBox',
        'commitBitrateOnEnter',
];

let discovery: UiBridge | null = null;

// The guard bundle instruments under a throwaway key: it is the full IIFE and
// would collide with the unwrapped behavioral bundle (same file path, different
// code and statement maps) in the coverage merge. Its tiny coverage is not part
// of the real discovery-sidebar number.
const DISCOVERY_GUARD_LABEL = 'js/discovery-sidebar.guard.js';

export function loadDiscovery(): UiBridge {
  if (!discovery) {
    discovery = evaluate(
      [prepared(join(DISCOVERY_DIR, DISCOVERY_MODULE), DISCOVERY_GUARD_LABEL)],
      [],
    );
  }
  return discovery;
}

// Re-evaluates the bundle to prove the double-init guard. Test-only helper.
export function reloadDiscoveryBundle(): void {
  evaluate([prepared(join(DISCOVERY_DIR, DISCOVERY_MODULE), DISCOVERY_GUARD_LABEL)], []);
}

const DISCOVERY_EXPECTED = [
  'currentDiscoveryUserId', 'safeHttpUrl', 't', 'getDiscoveryScoreClass',
  'loadExternalLinksConfig', 'showToast', 'dismissToast', 'extractErrorMessage',
  'getUserFriendlyErrorMessage', 'getCachedDiscoveryResult', 'setCachedDiscoveryResult',
  'invalidateDiscoveryResult', 'discoveryGeneratedAt', 'discoveryVisibleKey',
  'isOnHomePage', 'registerDiscoveryTab', 'focusDiscoveryTab', 'notifyTabUnavailable',
  'resetTraktState', 'captureDiscoveryRequest', 'isCurrentDiscoveryRequest', 'isSameDiscoverySession',
  'isLoginPage', 'syncDiscoveryLifecycle', 'initDiscoveryLifecycle', 'teardownDiscoveryEntryPoints', 'disableDiscoveryUi',
  'beforeHostSessionChange', 'afterHostSessionChange',
  'isEmptyTraktResult', 'traktCacheFresh', 'makeTraktCacheEntry', 'spinnerHtml',
  'renderTraktNotLinked', 'renderCards', 'esc', 'formatReason', 'formatRawReasonFallback',
  'insertSlashBreaks', 'tabButton', 'checkEmptyDiscoveryState', 'renderOwnError',
  'renderTraktError', 'closeDiscoveryPopup',
  // Added for behavioral tests: each name is a real top-level function in
  // discovery-sidebar.js and is driven by at least one spec below.
  'renderShell', 'renderOwnTab', 'renderTraktPersonal', 'renderTraktTrending',
  'maybeRefreshInBackground', 'injectStyles', 'injectNavigation',
  'getDiscoveryNavigationContainer', 'removeDiscoveryNavigation',
  'handleRequest', 'handleDismissClick', 'decideAndSubmit', 'submitRequest',
  'executeDismiss', 'showProfilePopup', 'showDismissConfirmation',
  'renderDiscovery', 'fetchPermissionsAndRequest', 'setDismissSiblingHidden',
  'finalizeDiscoveryPopup', 'injectPopupStyles', 'handleDiscoveryProbe',
  'probeDiscoveryAvailability',
  'waitForApi', 'loadStrings', 'bootstrapDiscovery',
];

let discoveryBridge: UiBridge | null = null;

// Behavioral surface of the discovery sidebar: the inner helpers unwrapped from
// the shipped IIFE. Fresh scope so module caches (discovery result, Trakt cache,
// toast state) never bleed across specs.
export function loadDiscoveryHelpers(): UiBridge {
  if (!discoveryBridge) {
    discoveryBridge = evaluate(
      [preparedUnwrapped(join(DISCOVERY_DIR, DISCOVERY_MODULE), DISCOVERY_DROPS)],
      DISCOVERY_EXPECTED,
    );
  }
  return discoveryBridge;
}

export function freshDiscoveryHelpers(): UiBridge {
  return evaluate(
    [preparedUnwrapped(join(DISCOVERY_DIR, DISCOVERY_MODULE), DISCOVERY_DROPS)],
    DISCOVERY_EXPECTED,
  );
}

export function loadClientLifecycleBundle(): void {
  evaluate([prepared(join(DISCOVERY_DIR, 'client-lifecycle.js'))], []);
}

const NATIVE_TABS_EXPECTED = [
  'entries', 'isOnHomePage', 'findEntry', 'isActiveTabContainer', 'getTabsRoot',
  'isOwnSlot', 'slotIndex', 'highestForeignIndex', 'relabel', 'createReservedSlot',
  'alignSlots', 'getHeaderButtonTray', 'getOrCreateGroup', 'removeGroupIfEmpty',
  'isTabButtonVisible', 'removeHeaderLink', 'createHeaderLink', 'ensureDiscoverable',
  'hashParam', 'homeRoute', 'watchUserTabChanges', 'clearStraySelection', 'syncSelectedTabHash', 'syncActiveEntry',
  'repairRemovedTabSelection',
  'handleRouteNavigation',
  'noteTabStripInput', 'deepLinkHash', 'syncDeepLink', 'applyPendingFocus',
  'ensureInjected', 'scheduleInject', 'register', 'unregister', 'focus', 'init',
];

// Behavioral surface of the native Home tab registry: inner helpers unwrapped
// from the shipped IIFE. Fresh scope per call so entries and deep-link state
// never bleed across specs.
export function freshNativeTabsHelpers(): UiBridge {
  return evaluate(
    [preparedUnwrapped(join(DISCOVERY_DIR, NATIVE_TABS_MODULE), NATIVE_TABS_DROPS)],
    NATIVE_TABS_EXPECTED,
  );
}

let nativeTabsBridge: UiBridge | null = null;

export function loadNativeTabsHelpers(): UiBridge {
  if (!nativeTabsBridge) {
    nativeTabsBridge = freshNativeTabsHelpers();
  }
  return nativeTabsBridge;
}

// The shipped IIFE, instrumented under a throwaway key so its guard-bundle
// coverage never counts toward the product gate. Used to prove the double-load
// guard against the real source shape.
export function loadNativeTabsBundle(): void {
  evaluate([prepared(join(DISCOVERY_DIR, NATIVE_TABS_MODULE), 'js/native-tabs.guard.js')], []);
}

export function saveCoverage(): void {
  // Each isolated worker writes a unique report because thread workers share the process ID.
  const g = globalThis as any;
  const cov = g.__coverage__;
  if (!cov) return;
  const dir = join(REPO_ROOT, 'test', 'ui', 'coverage', 'tmp');
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, `coverage-${process.pid}-${randomUUID()}.json`), JSON.stringify(cov));
}
