import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createInstrumenter } from 'istanbul-lib-instrument';
import { CONFIG_PAGE_MODULES, DISCOVERY_MODULE } from './jsModules.js';

const HERE = dirname(fileURLToPath(import.meta.url));
export const REPO_ROOT = join(HERE, '..', '..', '..');
const CONFIG_JS_DIR = join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper', 'PluginPages', 'js');
const DISCOVERY_DIR = join(REPO_ROOT, 'Jellyfin.Plugin.JellyfinHelper', 'js');

export type UiBridge = Record<string, any>;

const instrumenter = createInstrumenter({ esModules: false, produceSourceMap: false });

// Instruments for per-file coverage, then strips strict mode so the bundle
// evaluates exactly like the shipped IIFE scope.
function prepared(absPath: string): string {
  const rel = relative(REPO_ROOT, absPath).replaceAll('\\', '/');
  return instrumenter
    .instrumentSync(readFileSync(absPath, 'utf8'), rel)
    .replace(/^\s*'use strict';[ \t]*\r?$/m, '');
}

// Evaluates the shipped sources in compose order inside one shared scope.
// The appended return is test-only wiring and never touches production files.
// A renamed helper fails loudly here instead of silently untesting code.
function evaluate(sources: string[], expected: string[]): UiBridge {
  const pick = expected.map((n) => `${n}: typeof ${n} === 'undefined' ? undefined : ${n}`).join(', ');
  const run = new Function(`${sources.join('\n')}\nreturn { ${pick} };`) as () => UiBridge;
  const bridge = run();
  const missing = expected.filter((n) => typeof bridge[n] === 'undefined');
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
        'refreshTraktRowState', 'loadTraktOfficialStatus', 'updateRecsTabVisibility',
        'updateSeerrUIState', 'validateTrashPath', 'isSensitiveSystemPath',
        'openFolderBrowserDialog', 'initFolderBrowser', 'browseTo', 'loadLibraryPathsForBrowser',
        'escHtml', 'escAttr', 'allowSafeHtml', 'mi', 'getCssVar',
        'formatBytes', 'getScoreThresholdClass', 'getPathSegments',
        'collectFlatPaths', 'collectDictPaths', 'buildPathTree', 'countTreeItems',
        'encodeTreeKey', 'resolveTreeNode', 'renderTreeLevel', 'renderTreeFolder',
        'renderTreeLeaf', 'renderFileTree', 'toggleTreeNode', 'ensureTreeChildren',
        'collapseAllTreeNodes', 'expandOneTreeNode', 'runTreeAction',
        'setTreeCappedNote', 'refreshTreeCappedNote', 'pruneFileTreeRegistry',
        'bindFileTreeHandlers', 'aggregateDict',
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
        'renderTrendChart', 'loadTrendData', 'loadInsightsData', 'renderInsightCards',
        'polarToCartesian', 'describeArc', 'renderDonutSvg', 'renderCodecBreakdown',
        'renderDonutChart', 'collectCodecPaths', 'collectTrackLabelMeta',
        'collectDrilldownMeta', 'collectResolutionDimensions', 'countWatchedUsers',
        'orderedCodecCardIds', 'reorderFullOrder', 'prefersReducedMotion',
        'groupCardsIntoRows', 'findSlotIndex', 'colsOf', 'indexOfCard',
        'restoreCardToIndex', 'moveCardInList', 'announceCodec', 'toggleCodecBreakdown',
        'showDonutTooltip', 'hideDonutTooltip',
        'isSegmentPanelOpen', 'triggerCodecRowForSegment', 'attachCodecClickHandlers',
        'fillCodecsData', 'loadCodecCardOrderOnce', 'renderCodecsExplorer',
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
        'takeSettingsSnapshot', 'hasUnsavedSettings', 'renderLibraryMultiSelect',
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
];

let discovery: UiBridge | null = null;

export function loadDiscovery(): UiBridge {
  if (!discovery) {
    discovery = evaluate(
      [prepared(join(DISCOVERY_DIR, DISCOVERY_MODULE))],
      [],
    );
  }
  return discovery;
}

// Re-evaluates the bundle to prove the double-init guard. Test-only helper.
export function reloadDiscoveryBundle(): void {
  evaluate([prepared(join(DISCOVERY_DIR, DISCOVERY_MODULE))], []);
}

export function saveCoverage(): void {
  // __coverage__ accumulates across files inside one worker, so overwriting
  // the per-pid file keeps the complete picture. Separate workers (and the
  // offline rig) write their own pid file; check-coverage merges them all.
  const cov = (globalThis as any).__coverage__;
  if (!cov) return;
  const dir = join(REPO_ROOT, 'test', 'ui', 'coverage', 'tmp');
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, `coverage-${process.pid}.json`), JSON.stringify(cov));
}
