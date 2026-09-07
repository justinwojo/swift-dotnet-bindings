This is a re-review of the full original P1 scope against the round-2 packet. I’ll freeze the new hashes, read the round-1 adjudications, then re-check the accepted High and the new regressions.Round-2 packet is in. I’ll inventory the new diff, confirm live hashes, then allocate the same two area reviewers.Hashes match the r2 packet. I’ll read just enough of the r2-specific changes to split CI versus corpus, then launch the two reviewers.Scope is frozen. Launching the same two area reviewers against the r2 packet, with round-1 dispositions they must not relitigate.I'll inspect the r2 corpus files, hashes, and round-1 adjudication first, then verify the accepted fixes without expanding scope.I'll start by verifying snapshot hashes and reading the round-1 adjudication so I only re-check accepted and remaining defects.Hashes match. Next I’ll read the r2 diff, assigned sources, and the production exhaust/failure printers.Assigned files are in hand. Next I’ll verify live hashes against the r2 snapshot and inspect discovery/twin-selection logic.I’ll inspect production exhaust text, the CI classifier, and workflow wiring next.## 1. Area name

P1 CI retry / pretest classifier (r2 High-fix verification)

## 2. Files inspected

Assigned:
- `/Users/wojo/Dev/swift-bindings/.github/workflows/ci.yml`
- `/Users/wojo/Dev/swift-bindings/.github/workflows/release.yml`
- `/Users/wojo/Dev/swift-bindings/CLAUDE.md`
- `/Users/wojo/Dev/swift-bindings/build/scripts/ci/ci_ios_test.py`
- `/Users/wojo/Dev/swift-bindings/build/scripts/ci/test_ci_ios_test.py`

Callers / printers:
- `/Users/wojo/Dev/swift-bindings/build/Build.RuntimeTests.cs` (`ShouldRetryLauncherAbort` ~1663–1692, `RunOnSimulator` ~1773–1776, `ReportRuntimeTestResult` ~2653–2684)
- `/Users/wojo/Dev/swift-bindings/build/Models/LaunchDiagnostics.cs`
- `/Users/wojo/Dev/swift-bindings/BindingTests/RuntimeTestsApp/Program.cs` (TEST SUCCESS / TEST FAILURE: printers)
- `/Users/wojo/Dev/swift-bindings/BindingTests/RuntimeTestsApp/Infrastructure/TestLogger.cs`
- `/Users/wojo/Dev/swift-bindings/build/scripts/ci/sim_manager.py` (import surface only)

Authoritative:
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/review-r1/adjudication.md`
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/design.md`
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/results.md`
- A5 / A11 in `execution-plan-amendments.md`

Corpus-sweep files in the r2 diff were not traversed.

## 3. High-fix verification: **fixed**

The r1 collision is gone in current source.

`PRODUCT_EVIDENCE_PATTERNS` now uses the app delimiter `"test failure:"` (`ci_ios_test.py:92`), not bare `"test failure"`. The exhaust sentence still contains `"not a test failure."` and does **not** contain `"test failure:"`.

`EXHAUSTED_LAUNCH` is the real `ShouldRetryLauncherAbort` throw (`Build.RuntimeTests.cs:1676–1682`) with `legLabel="iOS Simulator"` and `MaxLauncherAbortAttempts=3`:

> `iOS Simulator: THE APP NEVER LAUNCHED. The launcher aborted before the app's process started on all 3 attempts, so no test ever executed and this run carries NO verdict about the bindings — it is a deploy/launch failure, not a test failure. …`

That fixture is used on **both** recover paths:
- nonzero `CompletedProcess` (`test_pretest_startup_failure_can_recover`)
- `TimeoutExpired` with **bytes** (`test_pretest_timeout_can_recover_with_actual_exhaust_text` via `EXHAUSTED_LAUNCH.encode()`)

Pretest phrases still match production:
- `"the app never launched"` ← throw `THE APP NEVER LAUNCHED`
- `"launcher never started the app"` ← `Log.Error` at `:1673`
- `"launcher aborted before the app started"` ← attempt 1–2 `Log.Warning` at `:1687` (the throw uses `app's process started`, so this arm is the warning, not the exception)

Marker dominance still wins: `[FAIL]`/`[CRASH]` timeout tests plus `test_actual_app_verdict_and_loader_markers_dominate_exhaust_text` (`TEST FAILURE:`, `TEST SUCCESS`, `[FAIL]`, `[CRASH]`, `dyld:`, `[TEST]`, `RESULTS FLUSHED` on stderr).

App printers match the new delimiter: `Program.cs:364` / `:418` / `:423` emit `TEST FAILURE: …` and `TEST SUCCESS` (no colon). Inner `LaunchDiagnostics` still keys `"TEST FAILURE"` without a colon; that is a different classifier.

## 4. Confirmed candidates (0–4)

None.

Delimiter narrowing is not a reachable miss on the `--sim` path: every `RuntimeTestsApp` failure line uses `TEST FAILURE:`, and overlapping product markers (`[fail]`, `runtime tests failed`, `results flushed`) remain. `TEST SUCCESS` vs `TEST FAILURE:` is the actual printer shape, not an asymmetry bug. `process_output_text` accepts str and bytes (`decode(errors="replace")`) on both retry arms. Workflow invocation `python3 -B build/scripts/ci/test_ci_ios_test.py -v` from the default checkout cwd puts `build/scripts/ci` on `sys.path[0]`, so `import ci_ios_test` loads the sibling module; `-v` reaches `unittest.main()`; a non-zero Python exit fails the GHA step before `dotnet nuke test` (`set -eo pipefail`).

Original CI scope still holds: `run_tests` command is `dotnet nuke binding-tests --sim --timeout … --device-udid …` plus optional `--skip-regen`; no `--tier`, no `--class-filter`; stdout+stderr joined before the classifier; `max_test_retries` default 1 → two attempts. Workflows no longer pass `--tier 2`. `CLAUDE.md` release gate says “the full simulator suite”. Unrelated `nuke validate --tier` is untouched.

## 5. Unresolved

None for this area. Dismissed r1 items (SKIP marker text, timeout-with-product-evidence no-retry, tier removal, stream normalization, full `--sim` remaining) have no new contrary evidence.

## 6. Coverage gaps

- Did not execute the 7 unittests or any Nuke/workflow run (forbidden). Wiring and classifier behavior are source-checked only.
- Did not re-run the claimed “old classifier fails new fixtures” golden.
- Pattern `"launcher aborted before the app started"` does not occur inside the exhaust **exception** (`app's process`); exhaust retry is carried by `"the app never launched"` / `"launcher never started the app"`. Not a hole given those two.
- Corpus CI still absent, per adjudication; not rechecked.

## 7. Snapshot hash check

Match. Assigned SHA-256s equal `scope-r2.json`. HEADs remain `82ef2b43827e782dd2f6e010f19617452c75df5a` (swift-bindings) and `905e99e9a228ae8f29aaad72181b364405982fdb` (internal-binding-testing). No drift.Hashes match the r2 snapshot. I have enough to verify the three accepted fixes and whether the twin override can mislabel a non-twin.

# P1 r2 re-review — corpus `run_library` completeness

## 1. Area name
P1 corpus `run_library.py` missing-product reason + same-module twin completeness (r2)

## 2. Files inspected
- `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library.py`
- `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library_acceptance_test.py`
- `/private/tmp/paired-p1-olu5ta27/scope-r2.json`, `scope-r2.diff` (corpus from ~417; new test ~718)
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/review-r1/adjudication.md`
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/design.md`
- `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/results.md`

## 3. Fix verification

### Mismatch reason (accepted Grok Low) — **verified**
`classify_missing_products` now returns:

```282:287:/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library.py
    return "product_name_mismatch", {
        "reason": "converter reports success with all expected primaries "
                  "produced, but the requested product name(s) were not discovered",
        "expected_primary_products": _as_list(
            receipt.get("expected_primary_products")),
        "wanted": sorted(wanted),
```

Call from the missing-requested loop:

```799:805:/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library.py
    for product in missing_requested_products:
        label, note = classify_missing_products(receipt, {product.lower()})
        if product.lower() in discovered_requested:
            label = "input_provenance_unknown"
            note = {"reason": "requested product was discovered but collapsed with a same-module "
                              "product; it has no independent generation/publication verdict",
                    "wanted": [product]}
```

- **Partial subset, converter `success`:** survivor is in `selected_requested`; the missing name is passed as `wanted`. The sentence is about that requested name not being discovered, not “none matched this candidate's product filter.” Truthful even when another primary matched.
- **Empty match set:** every candidate name is missing and not on disk, so the override does not fire; each row uses the same mismatch (or `convert_incomplete` if the receipt is not clean `success`). Still truthful.
- **Twins:** on-disk collapsed names never keep the mismatch sentence; the override replaces it.
- **Empty disk:** line 397 still discards a mismatch label and does not publish this reason.

No acceptance test locks this string (the twin test overrides it; the surviving-subset test uses `receipt_status="partial"` → `convert_incomplete`).

### `missing_input_cause` docstring (accepted Claude M1) — **verified**
```78:84:/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library.py
def missing_input_cause(log_path):
    """Extract a genuine MISSING-INPUT cause from a failed generate log: only
    SWIFTBIND119 (import graph not closed — a required module was not supplied),
    with the missing module names. Returns None for any other failure — a
    verify-recover non-convergence (SWIFTBIND111) or a wrapper error remains a
    generation failure. A retained project may receive a diagnostic compile
    verdict; that evidence cannot authorize publication of failed generation."""
```

Semantics unchanged: only SWIFTBIND119 is a missing-input; SWIFTBIND111 stays a generation failure. Publication still requires `generate_exit == 0`; a later managed compile cannot reclassify (`generate_failed` at 777–782). Not asking for a compile_failed relabel.

### Twin completeness (accepted Claude L2 / Grok twins) — **verified, no non-twin mislabel on the cited path**

Selection vs disk:

```463:524:/Users/wojo/Dev/internal-binding-testing/corpus-sweep/scripts/run_library.py
    def norm(n):
        n = n.lower().replace("-", "").replace("_", "")
        for suffix in ("dynamic", "static"):
            if n.endswith(suffix):
                return n[: -len(suffix)]
        return n
    ...
    xcfw_by_norm.setdefault(norm(base(xcfw)), xcfw)   # first-wins for deps only
    seed_xcfws = [x for x in xcfws if not wanted or base(x).lower() in wanted]
    ...
    if nrm in seen_norm:   # BFS skip — this is what drops the second seed
        continue
    ...
    discovered_requested = {base(x).lower() for x in seed_xcfws}
    selected_requested = {nodes[m]["product"].lower() for m in wanted_modules}
    missing_requested_products = sorted(
        (p for p in cand.get("products", [])
         if p.lower() not in selected_requested),
        key=str.lower)
```

A requested product is on disk but not selected **iff** it was a `seed_xcfws` entry whose `norm()` was already in `seen_norm` (or, latently, its `nodes[module]` slot was overwritten). `xcfw_by_norm` does not remove seeds; dependency-graph twin exclusion remains.

**Discovery-failed unique product still in `seed_xcfws`:** not mislabelled. Failed sidecar → `module = product`, a node is still written, `selected_requested` contains that name, so it never enters the missing loop. Pass 2 still generates it. The override cannot fire.

**`test_requested_same_module_twins_each_receive_a_disposition`:** does exercise collapse. `_execute(["Probe", "ProbeDynamic"], ["Probe", "ProbeDynamic"])` puts both xcframeworks on disk with a clean success receipt. `norm("ProbeDynamic") == "probe"`. `find_xcframeworks` is sorted, so `Probe` is discovered and generated; `ProbeDynamic` is `seen_norm`-skipped, in `discovered_requested`, not in `selected_requested`. Assertions: exit 3, two named rows, exactly one unauthorized row, that row is `input_provenance_unknown` with `"no independent"` in `cause.reason`. If missing still used disk artifacts, there would be one row and exit 0. It does not name `ProbeDynamic` as the collapsed product or lock `result["status"] == "input_provenance_unknown"`; under this fixture those are implied.

Other original authorities still hold in these files: publication iff generate exit 0; missing loop forces `all_ok = False`; completeness iterates `cand["products"]` only (deps outside); unique `generation-attempts/` logs plus `generate.log` copy; healthy one- and two-product controls still expect exit 0 / `ok`.

## 4. Confirmed candidates
None.

## 5. Unresolved
- `nodes` is keyed by sidecar `primaryModule`. Two requested products with **different** `norm()` but the same `primaryModule` could overwrite the first node; the lost name is on disk → the override would still say same-module collapse. That wording is arguably true (shared Swift module), and it is not the discovery-failed unique case. No corpus evidence in assigned files.
- Twin `compile.note` is still the canned `"not compiled: requested primary was not produced"` even when the xcframework exists. Classification/`cause.reason` are the real disposition. Not scored.
- `results.md` focused-validation table still says “PASS — 4 tests”; adjudication/prose say five including the twin control. Docs only; not an assigned-file defect.

## 6. Coverage gaps / original-scope not rechecked
- No test locks the mismatch reason string (empty-set or partial success-receipt mismatch).
- Twin test does not assert collapsed product name, aggregate status, `publication_authorized` on `Probe`, or that `ProbeDynamic` was never generated.
- No test that a discovery-failed **unique** product is *not* `input_provenance_unknown`.
- `generate.log` copy, post-feed attempt-log name, and a dependency-only xcframework excluded from completeness are not locked by the five acceptance tests (code-read only).
- CI / `ci_ios_test.py` / workflows not rechecked (out of assignment). Receipt tests not re-read. Tests not rerun.

## 7. Snapshot hash check
Live SHA-256 matches `scope-r2.json`. No drift.

| File | Expected / live |
|---|---|
| `.../corpus-sweep/scripts/run_library.py` | `40acd5aac9a29e1b9488291c4f347009782e872149e84d58d94d00314a941282` |
| `.../corpus-sweep/scripts/run_library_acceptance_test.py` | `ec740e7538bf7975b646cf7e95904aed2ab4305f0b8b2e27e2698fa87bf3fa73` |Both areas returned. I’ll freeze hashes again and spot-check the accepted High delimiter against the real Nuke exhaust text.## Paired review r2 — P1 trustworthy acceptance

**Status:** complete — no confirmed findings  
**Scope:** original full P1 uncommitted two-repo acceptance, re-read against `/private/tmp/paired-p1-olu5ta27/scope-r2.diff` and `scope-r2.json` (supersede round-1 bytes)  
**Refs:** swift-bindings `82ef2b43827e782dd2f6e010f19617452c75df5a`; internal-binding-testing `905e99e9a228ae8f29aaad72181b364405982fdb`  
**Snapshot:** live SHA-256 matches `scope-r2.json` for all 8 scoped files after both reviewers returned.  
**Prior dispositions:** `/Users/wojo/Dev/swift-bindings/src/docs/audit-2026-09/execution/P1/review-r1/adjudication.md`

**Reviewers:** CI retry/tier (5 files); corpus generation/publication (2 files). Adjudication rechecked the accepted High delimiter against current Nuke exhaust text.

---

### Accepted High — verified fixed

Round-1 collision: generic `"test failure"` matched Nuke’s `"not a test failure"`. Current source uses the app delimiter:

```90:92:build/scripts/ci/ci_ios_test.py
    # The app emits this delimiter. Bare "test failure" also occurs in Nuke's
    # pre-test explanation "not a test failure" and is not verdict evidence.
    "test failure:",
```

Against the real `ShouldRetryLauncherAbort` exception (`Build.RuntimeTests.cs:1676–1682`, `legLabel="iOS Simulator"`, `MaxLauncherAbortAttempts=3`): old needle `"test failure"` still matches; new needle `"test failure:"` does not. Pretest still matches via `"the app never launched"` (`THE APP NEVER LAUNCHED`). The `--sim` app printer is `TEST FAILURE: {n} tests failed` (`Program.cs:423`).

Both recover paths now use that complete exhaust fixture: nonzero `CompletedProcess` and `TimeoutExpired` (bytes). Marker dominance is tested (`TEST FAILURE:`, `TEST SUCCESS`, `[FAIL]`, `[CRASH]`, `dyld:`, `[TEST]`, `RESULTS FLUSHED`). CI and release **test** jobs run `python3 -B build/scripts/ci/test_ci_ios_test.py -v` before `dotnet nuke test`.

---

### Other accepted r2 repairs — verified

| r1 candidate | r2 check |
|---|---|
| Grok Low mismatch reason | `classify_missing_products` now says requested name(s) were not discovered (`run_library.py:282–284`). Truthful for empty and partial subsets. Twin override replaces it when the name is on disk. |
| Claude M1 `missing_input_cause` docstring | Updated; SWIFTBIND119 remains the only missing-input; SWIFTBIND111 stays a generation failure; compile still cannot authorize publication. |
| Twin completeness (now accepted) | Completeness uses `selected_requested` from `wanted_modules`. A `seen_norm`-collapsed requested twin in `discovered_requested` is forced to `input_provenance_unknown`, unauthorized, `all_ok=False`. Discovery-failed **unique** products still get a node (`module = product`) and do not take this override. Twin test exercises `Probe` + `ProbeDynamic`. Dependency-graph twin exclusion remains. |
| Claude M2 tests not wired | Python regressions are in both CI/release unit-test jobs. Corpus still has no CI (kept). |

Original corpus authorities still hold: publication iff generate exit 0; surviving subset cannot green a partial receipt; dependency-only products stay outside completeness; unique attempt logs plus `generate.log` compatibility; healthy one- and two-product controls still `ok`.

---

### Findings

None.

---

### Candidate dispositions (r1 preserved; r2 new)

| Candidate | Disposition | Reason |
|---|---|---|
| r1 High `"test failure"` vs `"not a test failure"` | **Resolved** | Delimiter `"test failure:"`; exhaust fixture on exit and timeout; dominance controls. Rechecked in current source. |
| r1 Low mismatch “none matched this candidate's product filter” | **Resolved** | Reason now names requested product(s). |
| Delimiter too narrow vs LaunchDiagnostics `"TEST FAILURE"` without colon | **Dropped** | `--sim` `RuntimeTestsApp` always prints `TEST FAILURE:`; overlapping markers remain (`[fail]`, `runtime tests failed`, `results flushed`). |
| Twin override mislabels discovery-failed unique product | **Dropped** | Failed sidecar still writes `nodes[product]`; name is selected; override cannot fire. |
| Twin `compile.note` still “not produced” when xcframework exists | **Dropped** | Classification/`cause.reason` are the disposition; canned compile note is not a green-path bug. |
| Same-`primaryModule` overwrite with different `norm()` | **Unresolved, not scored** | No assigned-file evidence; wording would still be shared-module. |
| r1 SKIP marker, product-timeout no-retry, tier removal, L1/L3/L4/L5, aggregate precedence | **Not relitigated** | No new contrary evidence. |

---

### Coverage

**Rechecked:** all 8 scoped files; `ShouldRetryLauncherAbort` exhaust text; app `TEST FAILURE:` / `TEST SUCCESS` printers; `LaunchDiagnostics` as the inner classifier; discovery/`seen_norm`/selected vs discovered completeness; missing-product loop; publication/compile split; workflow Python-then-nuke wiring; CLAUDE.md “full simulator suite”.

**Not re-executed** (forbidden): 7 CI tests, 5 corpus tests, 11 receipt groups, `py_compile`, golden old-classifier failure, Nuke/sim/device, conversion/generation. Results.md claims those passes; this review is source-only.

**Gaps:** no test locks the mismatch-reason string; twin test does not pin which product collapsed or `result["status"]`; no negative test that a unique discovery failure is *not* `input_provenance_unknown`; `generate.log` copy / post-feed log name / dependency-only exclusion remain code-read. `results.md` focused table still says “4 tests” while r2 prose/adjudication say five — that file is outside the scope packet.

**Agents:** 2 area reviewers + 1 adjudicating pass (~7 min CI, ~9 min corpus, parallel).