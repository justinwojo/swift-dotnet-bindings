I'll inspect the P1 scope packet and review notes first, then allocate area reviewers against the exact two-repo diff.Next I’ll inventory the exact scoped diff, P1 amendments, and current file hashes so reviewer areas stay frozen.Scope hashes match. I’ll split the two-repo diff and launch area reviewers for CI retry versus corpus acceptance.Two independent areas, hashes frozen. Launching CI-retry and corpus-acceptance reviewers in parallel.I'll inspect the assigned corpus scripts, scope.diff, and design docs first, then verify hashes againstI'll the packet. inspect the assigned CI retry/tier files against the frozen scope packet, starting with hashes, the diff, and the surrounding production source.Reviewers are inspecting independently. I’ll wait for both reports, then adjudicate against the frozen hashes.Hashes match the scope packet. Next I’ll read the production retry loop and hunt the Nuke/runtime phrases the classifier claims to match.Hashes match. Next I’ll read the generation/classification paths and the rest of the acceptance tests.I’ll read classification, `ensure_node`, packing, and the rest of `main` next.The pretest log strings may overlap product-evidence substrings; I’ll read LaunchDiagnostics and the exact Nuke phrases next.The exhaust-path exception may contain “not a test failure”; I’ll trace whether that string actually reaches the sim retry classifier.I’ll check receipt tests and a few edge cases around missing-product status mapping and retries.1. Area name

CI outer retry / generation of retry judgment / tier removal / healthy full-suite command

2. Files inspected (path + whether fully read)

- `/Users/wojo/Dev/swift-bindings/build/scripts/ci/ci_ios_test.py` — fully read
- `/Users/wojo/Dev/swift-bindings/build/scripts/ci/test_ci_ios_test.py` — fully read
- `/Users/wojo/Dev/swift-bindings/.github/workflows/ci.yml` — sim-test step plus `--tier` search (not the whole workflow)
- `/Users/wojo/Dev/swift-bindings/.github/workflows/release.yml` — sim-test step plus `--tier` search (not the whole workflow)
- `/Users/wojo/Dev/swift-bindings/CLAUDE.md` — release-gate paragraph plus remaining `--tier` mentions
- Callers used to judge classifier phrases: `/Users/wojo/Dev/swift-bindings/build/Models/LaunchDiagnostics.cs` (fully), `/Users/wojo/Dev/swift-bindings/build/Build.RuntimeTests.cs` (`ShouldRetryLauncherAbort`, `RunOnSimulator`, result reporting), `/Users/wojo/Dev/swift-bindings/BindingTests/RuntimeTestsApp/Infrastructure/TestLogger.cs`, `/Users/wojo/Dev/swift-bindings/BindingTests/RuntimeTestsApp/Program.cs` (terminal markers)
- Design/results/amendments A5 and A11, and `/private/tmp/paired-p1-olu5ta27/scope.diff` lines 1–366

3. Confirmed candidates (1)

**High — `/Users/wojo/Dev/swift-bindings/build/scripts/ci/ci_ios_test.py:90` and `:119–124` — production launcher-abort exhaust text is classified as product evidence, so the outer retry never admits the established pre-test failure**

Issue: `is_retryable_pretest_startup_failure` lowercases the whole child stream and treats substring `"test failure"` as immutable product evidence. The Nuke sim path this driver actually runs (`nuke binding-tests --sim` → `RunOnSimulator` → `ShouldRetryLauncherAbort`) ends a spent abort budget by throwing a message that contains that substring as a negation. Established pretest phrases are also present, so `established and not product_evidence` is false and neither the nonzero path (`:293–296`) nor the `TimeoutExpired` path (`:310–313`) retries. Impact: the only outer-retry condition P1 added is dead against the real exhaust transcript; a later success cannot recover it. The focused tests stay green because they use a sanitized string that production never emits.

Triggering condition: three inner launcher aborts on iOS Simulator; Nuke throws and exits nonzero; `ci_ios_test.run_tests` sees that output with `max_test_retries >= 1`.

Evidence quotes:

```90:90:build/scripts/ci/ci_ios_test.py
    "test failure",
```

```119:124:build/scripts/ci/ci_ios_test.py
def is_retryable_pretest_startup_failure(output: str) -> bool:
    """True only for established launcher failure before any product evidence."""
    normalized = (output or "").lower()
    established = any(pat in normalized for pat in PRETEST_STARTUP_FAILURE_PATTERNS)
    product_evidence = any(pat in normalized for pat in PRODUCT_EVIDENCE_PATTERNS)
    return established and not product_evidence
```

```1676:1682:build/Build.RuntimeTests.cs
            throw new Exception(
                $"{legLabel}: THE APP NEVER LAUNCHED. The launcher aborted before the app's process started on " +
                $"all {LaunchDiagnostics.MaxLauncherAbortAttempts} attempts, so no test ever executed and this run " +
                "carries NO verdict about the bindings — it is a deploy/launch failure, not a test failure. " +
                "Check the device/simulator state (connected, unlocked, developer mode, booted) and the app's " +
                "code signature. Do not read any recovered results as evidence: the app's data container is " +
                "persistent, so anything still in it belongs to an earlier run.");
```

On that same exhaust, `legLabel` is `"iOS Simulator"` (`Build.RuntimeTests.cs:1775`). `"not a test failure"` contains `"test failure"` after `.lower()`. `"THE APP NEVER LAUNCHED"` matches pretest `"the app never launched"`; the preceding `Log.Error` also contains `"the launcher never started the app"`. Attempt 1–2 warnings match `"launcher aborted before the app started"` exactly, so `established` is true and product-evidence domination still blocks retry.

The acceptance tests never feed that sentence:

```73:80:build/scripts/ci/test_ci_ios_test.py
    def test_pretest_startup_failure_can_recover(self):
        startup_failure = subprocess.CompletedProcess(
            ["dotnet"],
            1,
            stdout=(
                "The launcher never started the app in 3 attempts.\n"
                "THE APP NEVER LAUNCHED. The launcher aborted before the app started.\n"
            ),
```

`"test failure"` is the right needle for the app marker `TEST FAILURE:` in `Program.cs:423` and `LaunchDiagnostics.AppProducedOutput`, but it is not safe as a raw substring over Nuke’s own exhaust text.

4. Unresolved / extra known defects beyond the ceiling

None. One confirmed defect; no extras parked.

5. Coverage gaps

- TimeoutExpired and nonzero exits share one classifier (good; no drift). There is no test that a *TimeoutExpired* with only established pretest text is retried; recovery is proven only on `CompletedProcess` returncode 1 with the sanitized string.
- `[FAIL]` / crash dominance, bounded exhaustion, stdout+stderr join, healthy `--sim` / no `--class-filter` / no `--tier`, and workflow `--tier 2` removal were checked and look correct. Unrelated `nuke validate --tier` remains in `CLAUDE.md` and is unused by `ci_ios_test.py`.
- Skip console lines are `[WARN] SKIP: …`, not `[SKIP]`; that does not open a retry hole because `[TEST]` / `TEST SUCCESS` / `RESULTS FLUSHED` still dominate.

6. Snapshot hash check result for assigned files

Live SHA-256 matches `/private/tmp/paired-p1-olu5ta27/scope.json` for all five assigned files. No snapshot drift.

| File | Live SHA-256 |
|---|---|
| `.github/workflows/ci.yml` | `de3a4dab133e5cf41fe29425e9ecab61b422f7c6d1d24a50ce7a7f42776c9b6b` |
| `.github/workflows/release.yml` | `6be376b71f4df2b3433b902ed07d1d32dd645ca3e748813078696ef3421619fa` |
| `CLAUDE.md` | `3cf2c757bc6ac2a4bb0677eddd5ab977d8b13a8964d31499afd7ae5b5c211733` |
| `build/scripts/ci/ci_ios_test.py` | `3570c34a4569dde52db642e8bbbc6ad02ee9f4d523cfc39fc999a353f6da012a` |
| `build/scripts/ci/test_ci_ios_test.py` | `738e829998119813b12370a00ec908cdc602a0a3dea7a227ee0cdffb88a4172b` |CI review is in. I’ll verify the retry-classifier High against Nuke’s exhaust text while the corpus reviewer finishes.1. Area name  
Corpus generation/publication judgment (requested-primary completeness, provenance, retries, failure retention, healthy cases)

2. Files inspected  
- `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library.py` — fully read  
- `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library_acceptance_test.py` — fully read  
- `/private/tmp/paired-p1-olu5ta27/scope.diff` from line 368 — fully read (internal-binding-testing hunk + new acceptance test)  
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/design.md` — fully read (authority, not implementation)  
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/results.md` — fully read  
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/natural-refusal-classifier-result.json` — fully read (injected classification evidence)  
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/natural-refusal-classifier-probe.py` — read  
- `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library_receipt_test.py` — partial (classifier/provenance groups only)

3. Confirmed candidates (0–4)

**P3 / Low** — `run_library.py:281` and `run_library.py:794` — per-missing-product reuse of the empty-set mismatch reason  
- Issue: The new completeness loop calls `classify_missing_products(receipt, {product.lower()})` for each candidate primary absent from disk. That helper’s `product_name_mismatch` reason is still the old empty-loop sentence (“none matched this candidate's product filter”). When a survivor did match, the missing row’s `cause.reason` is false; `wanted` is only the missing name, not the candidate filter. Label, `publication_authorized=false`, and nonzero aggregate stay correct.  
- Trigger: candidate `products: ["Probe", "WrongName"]`, `Probe.xcframework` on disk, converter receipt `status=success` with empty `missing_required` (WrongName not a converter-required primary).  
- Evidence:  
  - `return "product_name_mismatch", { "reason": "converter reports success with all expected primaries " "produced, but none matched this candidate's product filter",` (`run_library.py:281-283`)  
  - `for product in missing_requested_products: label, note = classify_missing_products(receipt, {product.lower()})` (`run_library.py:794-795`)  
- The four acceptance tests never take this receipt shape (the subset test forces `receipt_status="partial"` → `convert_incomplete`). Receipt unit tests still describe the empty-products meaning of that reason.

No Critical/High. Core authorities hold in current source: generator exit 0 is the only publication grant (`publication_authorized = generated_success` at `run_library.py:703-713`); the diagnostic compile cannot overwrite a failed attempt (`if not generated_success: classification = "generate_failed"` at `772-777`); missing candidate primaries get their own row and force `all_ok = False` (`791-813`); dependency-only modules stay out of that set (`wanted_modules` is seed-norm filtered at `511`; the missing loop iterates only `cand["products"]`); `built_ok` still classifies `real_verdict_pass` (`720-726`). Natural-refusal evidence matches: exit 3, `status=generate_failed`, `publication_authorized=false`, `managed_compiles=true`.

4. Unresolved / extra known defects beyond the ceiling  
- Same-norm twins (`Foo` + `Foo-Dynamic`/`Foo-Static`): both can be in `discovered_requested` (so not missing) while `seen_norm` drops the second from `wanted_modules`, so a requested name can get no product row and the aggregate can still be `ok`. Twin collapse is pre-existing; current `candidates.json` does not list such pairs. Not scored as High.  
- Aggregate status prefers `convert_incomplete` over residual `generate_failed` (`828-837`). Mixed refusal + missing requested stays nonzero and the product row keeps `generate_failed`; only the library-level label changes. Judgment, not a green-path bug.

5. Coverage gaps  
- The four tests do call production `run_library.main()` with injected `run()`. They lock: SWIFTBIND111 + retained compile → `generate_failed` / unauthorized; partial subset → survivor pass + named missing + exit 3; one- and two-product healthy → `ok` / authorized.  
- Not locked: `product_name_mismatch` and `input_provenance_unknown` missing rows; dependency-only xcframeworks excluded from completeness; post-feed retry log (`…-post-feed.log`) as the final `generate.log` / `result.json` log; `generate.log` compatibility copy; `generate.attempt_id` matching the filename; healthy `classification == "real_verdict_pass"`.  
- Existing receipt tests still agree with `classify_missing_products` / provenance; they do not drive the new product-list completeness loop.

6. Snapshot hash check  
Live SHA-256 matches `/private/tmp/paired-p1-olu5ta27/scope.json`:  
- `run_library.py` `0442069d02fdbf18b2c29dec4241e22209e047a9304848cc3bc7354cf3c68880`  
- `run_library_acceptance_test.py` `aedb15690d86ae48aa533f298d42033dc1ad3c041af4fa63a35d652b130b070f`  
No snapshot drift.CI High looks real. I’ll confirm the exception reaches captured Nuke output, then check the corpus completeness-reason claim.The substring collision is real; I still need to confirm the thrown Nuke exception actually lands in the CI child’s captured stdout/stderr.Checking one remaining path: whether Nuke’s unhandled exception is written into the captured child streams.## Paired review — P1 trustworthy acceptance

**Status:** complete, with findings  
**Scope:** uncommitted two-repository diff in `/private/tmp/paired-p1-olu5ta27/scope.diff`  
**Refs:** swift-bindings `82ef2b43827e782dd2f6e010f19617452c75df5a`; internal-binding-testing `905e99e9a228ae8f29aaad72181b364405982fdb`  
**Snapshot:** live SHA-256 matches `/private/tmp/paired-p1-olu5ta27/scope.json` for all 8 scoped files (rechecked after reviewers returned). Unrelated corpus dirty files were not reviewed.

**Reviewers:** CI retry/tier (5 files); corpus generation/publication (2 files). Adjudication verified the High locus in current production source plus Nuke callers.

---

### High (P1)

**`build/scripts/ci/ci_ios_test.py:90` and `:119–124` — production launcher-abort exhaust is classified as product evidence, so the outer retry never admits the only retryable condition**

`is_retryable_pretest_startup_failure` lowercases the whole child stream and treats substring `"test failure"` as immutable product evidence:

```90:90:build/scripts/ci/ci_ios_test.py
    "test failure",
```

```119:124:build/scripts/ci/ci_ios_test.py
def is_retryable_pretest_startup_failure(output: str) -> bool:
    """True only for established launcher failure before any product evidence."""
    normalized = (output or "").lower()
    established = any(pat in normalized for pat in PRETEST_STARTUP_FAILURE_PATTERNS)
    product_evidence = any(pat in normalized for pat in PRODUCT_EVIDENCE_PATTERNS)
    return established and not product_evidence
```

The Nuke sim path this driver actually runs (`nuke binding-tests --sim` → `RunOnSimulator` → `ShouldRetryLauncherAbort`) ends a spent abort budget by throwing a message that contains that substring as a negation:

```1676:1682:build/Build.RuntimeTests.cs
            throw new Exception(
                $"{legLabel}: THE APP NEVER LAUNCHED. The launcher aborted before the app's process started on " +
                $"all {LaunchDiagnostics.MaxLauncherAbortAttempts} attempts, so no test ever executed and this run " +
                "carries NO verdict about the bindings — it is a deploy/launch failure, not a test failure. " +
                "Check the device/simulator state (connected, unlocked, developer mode, booted) and the app's " +
                "code signature. Do not read any recovered results as evidence: the app's data container is " +
                "persistent, so anything still in it belongs to an earlier run.");
```

`"not a test failure"` contains `"test failure"` after `.lower()`. The same throw also contains `"THE APP NEVER LAUNCHED"`, and the preceding `Log.Error` contains `"the launcher never started the app"`, so `established` is true. Attempts 1–2 log `"the launcher aborted before the app started"` exactly. The throw is uncaught in `RunOnSimulator` (it never reaches `ReportRuntimeTestResult`), so Nuke fails the target with that `Exception` text in the captured streams — the same mechanism as the other terminal `throw new Exception(...)` messages in this file.

Nonzero and `TimeoutExpired` both use this classifier (`:293–296` and `:310–313`), so neither path retries. Impact: the only outer-retry condition P1 added is dead against the real exhaust transcript; a later success cannot recover it.

The focused tests stay green because they inject a sanitized string production never emits:

```73:80:build/scripts/ci/test_ci_ios_test.py
    def test_pretest_startup_failure_can_recover(self):
        startup_failure = subprocess.CompletedProcess(
            ["dotnet"],
            1,
            stdout=(
                "The launcher never started the app in 3 attempts.\n"
                "THE APP NEVER LAUNCHED. The launcher aborted before the app started.\n"
            ),
```

The app’s real verdict marker is `TEST FAILURE:` / `TEST SUCCESS` (`BindingTests/RuntimeTestsApp/Program.cs:418–423`; `LaunchDiagnostics.AppProducedOutput` uses `"TEST FAILURE"`). `"test failure"` as a raw substring is not safe over Nuke’s exhaust text.

**Minimal reproduction:** feed `run_tests` the actual exhaust sentence (`…not a test failure…` plus the established pretest phrases) as a nonzero `CompletedProcess` (or `TimeoutExpired`), with a later success available. Expect one retry and exit 0; current code returns 1 after a single invocation.

---

### Low (P3)

**`corpus-sweep/scripts/run_library.py:281–283` and `:794–795` — per-missing-product reuse of the empty-set mismatch reason**

The new completeness loop calls `classify_missing_products(receipt, {product.lower()})` for each candidate primary absent from disk. That helper’s `product_name_mismatch` reason is still the old empty-loop sentence:

```281:286:corpus-sweep/scripts/run_library.py
    return "product_name_mismatch", {
        "reason": "converter reports success with all expected primaries "
                  "produced, but none matched this candidate's product filter",
        "expected_primary_products": _as_list(
            receipt.get("expected_primary_products")),
        "wanted": sorted(wanted),
```

When a sibling *did* match, the missing row’s `cause.reason` is false; `wanted` is only the missing name. Label, `publication_authorized=false`, and nonzero aggregate stay correct. Trigger: candidate `products: ["Probe", "WrongName"]`, `Probe.xcframework` on disk, converter receipt `status=success` with empty `missing_required`. The four acceptance tests never take this receipt shape (the subset test forces `receipt_status="partial"` → `convert_incomplete`).

---

### Candidate dispositions

| Candidate | Disposition | Reason |
|---|---|---|
| CI `"test failure"` vs Nuke `"not a test failure"` | **Keep High** | Verified in current source: substring collision, both retry arms, uncaught throw reaches child output. Design matrix case “established pretest then success → one retry” is unreachable against production exhaust. |
| Acceptance tests using sanitized exhaust | **Merged into High** | Same defect; tests are why the gate stayed green, not a separate finding. |
| Missing `TimeoutExpired`-only pretest recovery test | **Dropped as finding** | Shared classifier; ordinary test gap once the High is fixed. Noted under coverage. |
| `[SKIP]` vs actual `[WARN] SKIP:` console lines | **Dropped** | Not a retry hole: `[TEST]` / `TEST SUCCESS` / `RESULTS FLUSHED` still dominate. |
| Same-norm twins (`Foo` + `Foo-Dynamic`) omitting a requested row | **Dropped** | Pre-existing `seen_norm` collapse; current `candidates.json` does not list such pairs; this change does not newly green a twin. |
| Aggregate `status` prefers `convert_incomplete` over residual `generate_failed` | **Dropped** | Still nonzero; the product row keeps `generate_failed`. Library-level label only. |
| Corpus mismatch-reason sentence | **Keep Low** | Reachable on the new per-product loop; fail-closed classification is correct. |

---

### What held

Corpus generation/publication authorities hold in current source:

- Generator exit 0 is the only publication grant (`publication_authorized = generated_success` at `run_library.py:703–713`).
- A diagnostic managed compile cannot overwrite a failed attempt (`if not generated_success: classification = "generate_failed"` at `:772–777`).
- Every missing candidate primary gets its own row and forces `all_ok = False` (`:791–813`); dependency-only modules stay out of that set.
- Healthy packed products still classify `real_verdict_pass` (`:720–726`).
- Injected natural-refusal evidence (`natural-refusal-classifier-result.json`) matches: exit 3, `status=generate_failed`, `publication_authorized=false`, `managed_compiles=true`. That is classification replay, not a fresh generation.

CI besides the High:

- `[FAIL]` / crash dominance, bounded exhaustion, stdout+stderr join, healthy `--sim` / no `--class-filter` / no `--tier`, and workflow `--tier 2` removal look correct.
- Unrelated `nuke validate --tier` remains; `ci_ios_test.py` no longer transports `tier`. Full simulator suite remains selected.
- Timeout and nonzero paths share one classifier (no path drift).

---

### Coverage

**Inspected:** all 8 scoped files (CI production + tests + both workflows’ sim-test steps + CLAUDE.md release-gate paragraph; corpus `run_library.py` fully; new acceptance tests fully). Necessary callers: `LaunchDiagnostics.cs`, `Build.RuntimeTests.cs` (`ShouldRetryLauncherAbort`, `RunOnSimulator`, `ReportRuntimeTestResult`), `Program.cs` terminal markers, `classify_missing_products`, receipt-test classifier groups, P1 design/results/A5/A11.

**Not re-run:** Python tests, `py_compile`, Nuke, simulator/device, conversion, generator, managed compile (caller-forbidden). Results.md claims those focused tests passed; this review did not re-execute them.

**Gaps:** no test feeds the real Nuke exhaust sentence; no `TimeoutExpired` recovery with only pretest text; corpus tests do not lock `product_name_mismatch` / `input_provenance_unknown` missing rows, post-feed attempt-log naming, or `generate.log` copy. Workflow files were not read in full beyond the sim-test step and `--tier` search — sufficient for this one-line call-site change.

**Agents:** 2 area reviewers + 1 adjudicating pass. Reviewer runtimes ~5 min (CI) and ~10 min (corpus), parallel; do not add those as wall time.