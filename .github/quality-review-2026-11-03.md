# GitHub Code Quality review — 2026-11-03

Baseline: `d31085852c4828f7c44a73eb1c9bc75a3c2baaf8` (`main`).
Work branch: `fix/github-quality-findings-2026-11-03`.

The authenticated Code Quality REST API returned **310 open rules-based findings**
across all four pagination pages: 241 in tests and 69 in production code. The three
AI findings were supplied separately by the repository owner. This is a source
review, not a claim that GitHub has rescanned or closed the findings. No scanner
configuration was disabled, no suppressions were added, and no findings were
remotely dismissed.

## AI findings

1. **DiscoveryRequestDto media-type validation — retain.** ASP.NET validates the
   DTO before the controller normalizes the media type. The admin controller also
   explicitly validates the DTO before normalization when invoked directly.
   Removing the regex weakens validation; making it lowercase-only rejects the
   existing supported `Movie`, `MOVIE`, `Tv`, and `TV` inputs. Added regression
   tests for supported casing and invalid/null/empty inputs. `[Required]` already
   exists. The AI suggestion describes the validation order incorrectly.
2. **Zero-byte trickplay moves — fixed.** The legacy `MoveToTrash` return value
   conflates failure with successful zero-byte moves. Added
   `MoveToTrashWithResult`, whose success flag is set only after the move succeeds.
   Both directory-cleanup tasks now use that flag. The legacy byte-count method
   remains available and delegates to the same implementation. Tests cover empty
   directories, zero-byte files, nonempty successful moves, missing sources,
   already-trashed sources, and destination failures. Cleanup tests cover success
   and failure accounting. Completely empty wanted-list placeholders remain
   protected by the Empty Folder Cleaner's existing policy.
3. **Duplicate negative-ID validation — retain boundaries.** The current
   controller validates whether the user may use a server/profile/root-folder
   override. The DTO validates numeric ranges. The public service independently
   rejects malformed input, including direct callers that bypass model binding.
   These are different responsibilities, not duplicate controller/service range
   checks. Existing `DiscoveryRegressionTests` and
   `SeerrDiscoveryServiceHttpTests.SubmitRequestAsync_NegativeId_ReturnsFailure`
   cover both entry points. The proposed service-private helper would not share
   validation with the controller and would not solve the reported concern.

## Rules-based findings

"Changed" means the reported source pattern was addressed; closure still needs a
GitHub analysis of the new branch. "Retained" includes intentional behavior and
optional style suggestions whose replacement would not improve this code.

| Rule | Open at baseline | Changed | Retained |
| --- | ---: | ---: | ---: |
| `cs/path-combine` | 188 | 7 | 181 |
| `cs/local-not-disposed` | 18 | 13 | 5 |
| `cs/linq/missed-select` | 17 | 17 | 0 |
| `cs/linq/missed-where` | 17 | 11 | 6 |
| `cs/static-field-written-by-instance` | 17 | 0 | 17 |
| `cs/dereferenced-value-may-be-null` | 12 | 12 | 0 |
| `cs/inefficient-containskey` | 10 | 10 | 0 |
| `cs/missed-ternary-operator` | 8 | 8 | 0 |
| `cs/catch-of-all-exceptions` | 7 | 5 | 2 |
| `cs/equality-on-floats` | 7 | 0 | 7 |
| `cs/empty-catch-block` | 6 | 6 | 0 |
| `js/unneeded-defensive-code` | 1 | 0 | 1 |
| `cs/nested-if-statements` | 1 | 1 | 0 |
| `cs/constant-condition` | 1 | 1 | 0 |
| **Total** | **310** | **91** | **219** |

### Changed finding IDs

- Paths: #1979, #1980, #1875, #1691, #1692, #1558, #1557. Use `Path.Join` for
  explicitly relative child paths without changing the containment validators.
- Lifetime management: #1863, #1862, #1861, #1860, #1851, #1805, #1800, #1801,
  #1799, #1798, #1797, #1155, #215. Dispose cancellation sources, the barrier,
  and test-owned HTTP clients after their final use. HTTP mock responses are
  created per invocation, rather than reusing a response disposed by the service.
  Client-reuse tests now actually send another request: reading `BaseAddress`
  alone does not prove that an HTTP client is undisposed.
- Projection: #1976, #1850, #1829, #1828, #1827, #1826, #1759, #1681, #1571,
  #1548, #1547, #1541, #1212, #1209, #1210, #1207, #1206. Make projections
  explicit while retaining deferred evaluation, ordering, and early exits.
- Filtering: #1874, #1848, #1819, #1730, #1688, #1687, #1516, #1203, #1200,
  #1199, #1193. Preserve first-match/duplicate handling and lazy-model ownership.
- Nullability: #1795, #1706, #1705, #1704, #1703, #1150, #1151, #1152, #1148,
  #1149, #1146, #1147. Assert and extract nonnullable dates instead of repeatedly
  dereferencing nullable properties after assertions the scanner does not model.
- Dictionary lookups: #1872, #1871, #1870, #1792, #1791, #1790, #1789, #1514,
  #1221, #1222. Combine membership assertions and value extraction with
  `TryGetValue`.
- Conditional assignments: #1847, #1842, #1818, #1817, #1786, #1785, #1136,
  #1137. Normalize once and use a single conditional assignment or
  `GetValueOrDefault` accumulation.
- Exception handling: #1977, #1978, #1811, #1130, #537. Retain best-effort
  handling without swallowing fatal exceptions, narrow path/probe catches to
  expected failures, and log a failed probe cleanup.
- Empty catches: #1231, #1229, #1230, #1228, #566, #567. Keep resilient test
  teardown but emit diagnostics when temporary-file cleanup fails.
- Nested condition: #1846. Select the longest matching route explicitly.
- Assertion ordering: #1515. Report and count mismatches before the debug
  assertion, so the assertion cannot make the diagnostic branch appear dead.

### Retained findings and rationale

- **181 path findings:** all baseline `cs/path-combine` findings other than the
  seven changed IDs above. Their later operands are known relative fixture
  names, fixed child names, or an intentional `..` input in a traversal regression
  test. None introduces a rooted later operand that discards the fixture root.
  Replacing hundreds of otherwise correct test expressions is unnecessary.
- **17 static-field findings:** #1751, #1752, #1748, #1749, #1750, #1745, #1746,
  #1747, #1742, #1743, #1744, #1739, #1740, #1741, #1736, #1737, #1738.
  These fields have `[ThreadStatic]`: they are scratch arrays confined to the
  calling thread, not shared model state. Synchronous scoring clears/overwrites
  them before use; model weights retain their existing lock protection. Do not
  replace them with shared instance buffers or add contention to the hot path.
- **7 float comparisons:** #1794, #1676, #1191, #1188, #1189, #1190, #1187.
  These compare exact dropout masks, class-label constants, a disabled blend
  sentinel, or an explicit zero meaning missing metadata. They do not compare
  independently rounded measurements. Epsilon comparisons could misclassify
  valid small values or alter training/backfill behavior.
- **6 imperative filters:** #1823, #1822, #1821, #1820, #1713, #1198. Keep the
  single-pass dictionary/mapper lookups, state-dependent maximum updates, and
  per-document `seen.Add` mutation explicit. Avoid extra lookups, captured
  mutable state, or unnecessary hot-path enumeration changes just to satisfy an
  optional LINQ suggestion.
- **5 transferred disposables:** #1873, #1845, #1844, #1810, #1154. Handler-created
  responses are returned to service code that disposes them; request-body memory
  streams are attached to the returned context and registered for response
  disposal. Disposing these inside the creating helper would return unusable
  objects. The hostless request fixtures use managed memory, not native handles.
- **2 broad test catches:** #1812, #1813. Concurrency tests collect all exceptions
  from worker tasks and subsequently assert that the collection is empty; they
  do not hide failures. Narrowing the catch would weaken the diagnostic test.
- **1 timeout guard:** #1975. Keep the inexpensive callback-type check so a
  timeout never tries to invoke an omitted/nonfunction callback. The known
  production call sites pass functions, but removing this guard has no meaningful
  performance benefit and makes the helper less defensive.

## Verification

- Debug and Release builds: successful, zero warnings/errors.
- Backend regression suites: 6822 passed in Debug and 6822 passed in Release,
  zero failed/skipped (xUnit's .NET-host runner).
- Targeted directory-cleanup regression suite: 166 passed, zero failed/skipped.
- UI regression suite: 1333 tests passed across 39 files, zero failures.
- Docker E2E: not run locally because the Docker Desktop Linux daemon is not
  available. No Docker service was started or system policy changed.
- GitHub Code Quality/CodeQL: no local rescan claimed; reanalysis of the new
  branch is needed to verify exact scanner closure and detect any new findings.

The Windows host denies launching the generated test apphost executable. The
test assembly runs normally via `dotnet <test-assembly>.dll`; no security software
or application-control policy was disabled.
