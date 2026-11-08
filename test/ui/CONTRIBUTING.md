# Contributing to the UI JavaScript tests

## Scope

`test/ui/` covers the dashboard scripts (`PluginPages/js/`) and the
Discovery sidebar (`js/discovery-sidebar.js`). It pins behavior that only
exists in JavaScript: visibility rules, validators, tab switching, dialog
flows. Backend logic stays in xUnit, real-browser flows stay in `test/e2e/`.
The Discovery tab matrix needs layout (`offsetParent`) and lives in the
Playwright spec `discovery-native-tab.ui.spec.ts`, not here.

## No test code in production files

`src/loader.ts` evaluates the shipped sources unchanged and reads helpers
from the shared scope. If a helper is unreachable, write the test against
the public surface (DOM + stubbed `ApiClient`) instead of adding exports to
production files. Pure helpers that production code itself reuses are the
only exception.

## Shared UI primitives

`tests/ui-primitives.test.ts` covers `PluginPages/js/UiPrimitives.js` through its
public `window.JellyfinHelperUi` API and the real dashboard and Discovery callers.
It checks modal cancellation and focus restoration, keyboard tabs, hidden panels,
stable card updates, poster fallbacks, localized counts, reduced-motion removal and paused toast
lifetimes. The loader and the isolated lifecycle documents load this module
before its consumers, matching the composed page and embedded script response.
Keyboard cases include Home, End, wrapping, unavailable controls and RTL. Real
browser fixtures compare Discovery description scrollbars with the shipped
Codec-tree styles and verify that opening details preserves card dimensions,
the continuous frame and the description's scroll position.
Panel cases verify immediate fallback state without native animation. Browser
fixtures observe actual Web Animations calls and check closing accessibility,
reversal continuity, final cleanup and reduced motion rather than advancing
production coverage counters or replacing the animation implementation.
Toast regressions cover missing, null and partial options, immutable caller
objects, default and zero lifetimes, localized labels, pointer accessibility and
cleanup of the last toast's stack. Browser fixtures verify default toast styling
and dismissal without loading Discovery.
Discovery hint regressions cover recommendation-mode and Seerr credential
round-trips; browser fixtures also check forced closure during an opening
transition and reopening without stale cleanup.
Native tab selection mocks use the callable `(index?: number) => number`
signature. Compound button selectors specify `HTMLButtonElement` through the
DOM query's type parameter so `.click()` needs no cast or suppression.
Explorer browser regressions select multiple audio languages and open a lazy
tree/file detail while the editor's native closing transition is still active.
They assert DOM identity after cleanup so waiting for closure cannot mask a
results rebuild race.

## Running and gating

Keep intentionally hostile markup fixtures unchanged even when IDE HTML
inspections flag them. Do not suppress inspections to hide these fixtures.
Build simulated navigation links in their owning document and inspect demo
script order through the parsed HTML.

```bash
npm install --ignore-scripts --no-audit --no-fund
npm test                # single run
npm run test:coverage   # run + coverage gate, fails below the mark
```

Raise the gate numbers in `package.json` with every extension, never lower
them without a reason in the PR description.

## Adding a test

1. Add `tests/*.test.ts` and list it in `README.md` (the `docs.test.ts`
   gate checks both directions and fails the run otherwise).
2. Keep comments to one or two sentences about why, never what.
3. Prefer tables (`test.each`) over copy-pasted cases, and assert behavior
   (visible, disabled, error shown), not implementation details.
