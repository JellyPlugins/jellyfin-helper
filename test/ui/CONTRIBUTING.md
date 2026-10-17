# Contributing to the UI JavaScript tests

## Scope

`test/ui/` covers the dashboard scripts (`PluginPages/js/`) and the
Discovery sidebar (`js/discovery-sidebar.js`). It pins behavior that only
exists in JavaScript: visibility rules, validators, tab switching, dialog
flows. Backend logic stays in xUnit, real-browser flows stay in `test/e2e/`.
The Discovery tab matrix needs layout (`offsetParent`) and lives in the
Playwright spec `discovery-customtab.ui.spec.ts`, not here.

## No test code in production files

`src/loader.ts` evaluates the shipped sources unchanged and reads helpers
from the shared scope. If a helper is unreachable, write the test against
the public surface (DOM + stubbed `ApiClient`) instead of adding exports to
production files. Pure helpers that production code itself reuses are the
only exception.

## Running and gating

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
