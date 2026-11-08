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
