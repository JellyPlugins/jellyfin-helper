# Jellyfin Helper - UI JavaScript Tests (Vitest)

Fast unit tests for the dashboard scripts (`PluginPages/js/`) and the
Discovery sidebar (`js/discovery-sidebar.js`). They load the real shipped
sources in order and drive the actual functions, so visibility rules and
validators are pinned without a browser.

## What it covers

| Area | File |
|---|---|
| Shared helpers (bytes, escaping, thresholds, paths) | `tests/shared-utils.test.ts` |
| Shared trees, dialogs, api errors, indicators | `tests/shared-trees.test.ts`, `tests/shared-indicators.test.ts`, `tests/shared-indicators-behavior.test.ts` |
| Reviewed edge cases pinned (tree key decode, dir icon, zero coercion, union dedup, capped note, ensemble share) | `tests/shared-safety.test.ts` |
| Settings visibility (Discovery access, Trakt row, Recs tab, Seerr state) | `tests/settings-visibility.test.ts` |
| Settings form (payload, save, multiselect, backup, trash dialogs) | `tests/settings-form.test.ts`, `tests/settings-behavior.test.ts`, `settings-trash-dialog.test.ts` |
| Trash path validation | `tests/trash-path.test.ts` |
| Dashboard tabs, shell, scan lifecycle | `tests/main-tabs.test.ts`, `tests/main-shell.test.ts`, `tests/main-lifecycle.test.ts` |
| Codecs math, donuts, drilldown, reorder | `tests/codecs.test.ts`, `tests/codecs-core.test.ts` |
| Codecs interactions (tooltips, drag & drop, keyboard reorder, card-order saves) | `tests/codecs-interactions.test.ts` |
| Explorer bitrate maps, pills, paths, render | `tests/explorer-bitrate.test.ts`, `tests/codecs-explorer.test.ts` |
| Explorer helper edge cases (dimension index, filter intersection) and full behavioral coverage | `tests/codecs-explorer-interactions.test.ts`, `tests/codecs-explorer-coverage.test.ts` |
| Arr instances render, collect, add, remove, test | `tests/arr-integration.test.ts` |
| Recommendations cards, requests, diagnostics | `tests/recommendations.test.ts`, `tests/recommendations-flow.test.ts` |
| Trends math, charts, insights | `tests/trends.test.ts`, `tests/trends-render.test.ts`, `tests/trends-interaction.test.ts` |
| Health checks, trash section and overview | `tests/health-overview.test.ts`, `tests/health-trash.test.ts` |
| Logs tab and log level | `tests/logs-tab.test.ts`, `tests/logs-level.test.ts` |
| Folder browser dialog and navigation | `tests/folder-browser-dialog.test.ts`, `tests/folder-browser-paths.test.ts` |
| Discovery bundle load + double-init guard (the tab matrix needs real layout, see e2e `discovery-customtab.ui.spec.ts`) | `tests/discovery-load.test.ts` |
| Discovery sidebar helpers (escaping, cache, toasts, renderers) | `tests/discovery-helpers.test.ts` |
| Compose-order sync guard (.csproj vs loader) | `tests/csproj-order.test.ts` |
| README drift guard (every test file documented) | `tests/docs.test.ts` |

## Running it

```bash
cd test/ui
npm install --ignore-scripts --no-audit --no-fund  # npm ci once package-lock.json exists
npm test                # single run, no gate
npm run test:coverage   # run + coverage gate (fails below the mark)
```

`dotnet test` runs this suite automatically when `test/ui/node_modules`
exists (skip otherwise). Set `RunVitest=false` to opt out, e.g.
`dotnet test -p:RunVitest=false`.

## Coverage gate

`npm run test:coverage` instruments the real sources in `src/loader.ts`,
merges the per-process reports and enforces the mark in
`scripts/check-coverage.mjs` (lines 85 / branches 80, plus
`coverage/lcov.info` for Codecov). The run fails below the mark,
locally and in CI. Raise the numbers with every suite extension, never lower
them without a reason in the PR description.

## Adding a test

1. Add the `*.test.ts` file under `tests/`.
2. Add a row to the table above (the `tests/docs.test.ts` gate fails otherwise).
3. If the helper is new, append it to the expected list in `src/loader.ts`
   (test-only wiring; production files stay untouched).
4. Use `ajaxRouter` / `flush` from `src/apiStub.ts` rather than re-declaring them.
