I've reviewed the full scope in both repos against current source. Report below.

---

# Review — P1 trustworthy acceptance

**Scope reviewed:** `scope.diff` in full (both repos); hashes in `scope.json` match the files I opened. Supporting source read beyond the patch: `build/Build.RuntimeTests.cs` (launch/abort/report paths), `build/Build.BindingTests.cs:1380-1404`, `build/Models/LaunchDiagnostics.cs`, both workflow call sites, and the corpus consumers `aggregate.py`, `ratchet_compare.py`, `resweep_compare.py`, `candidates/candidates.json`. No builds, simulator, device, generator, or mutating commands were run.

---

## Critical (P0)

None.

---

## High (P1)

### H1 — The only retryable condition is unreachable on the child-exit path: the harness's own abort message contains a product-evidence substring

`build/scripts/ci/ci_ios_test.py:119-124` gates the retry on `established and not product_evidence`. `PRODUCT_EVIDENCE_PATTERNS` (`ci_ios_test.py:82-108`) contains the bare substring `"test failure"`.

The message that terminates a launcher-abort run — the *only* way the sim leg exits nonzero having "established a pre-test startup failure" — is `build/Build.RuntimeTests.cs:1676-1682`:

```
$"{legLabel}: THE APP NEVER LAUNCHED. The launcher aborted before the app's process started on " +
$"all {LaunchDiagnostics.MaxLauncherAbortAttempts} attempts, so no test ever executed and this run " +
"carries NO verdict about the bindings — it is a deploy/launch failure, not a test failure. " +
```

Lower-cased, this string satisfies `PRETEST_STARTUP_FAILURE_PATTERNS` via `"the app never launched"` **and** satisfies `PRODUCT_EVIDENCE_PATTERNS` via `"test failure"` (inside *"not a test failure"*). `is_retryable_pretest_startup_failure` therefore returns `False`, and `ci_ios_test.py:293-299` falls through to `log.error("=== TESTS: FAILED …")` with no retry.

**Triggering condition:** any CI/release sim run where `simctl` aborts the launch on all 3 attempts (`LaunchDiagnostics.MaxLauncherAbortAttempts`, `LaunchDiagnostics.cs:77`) — the exact `LauncherNeverStartedApp` condition the change was built to retry. Reproduction shape: feed the exhausted-abort message through `is_retryable_pretest_startup_failure()`; it returns `False`.

**Impact:** the feature described in `results.md` ("The CI outer retry admits only the Nuke harness's established pre-test launcher-abort classification") and in `design.md`'s validation row *"established pre-test startup failure followed by success → one retry; zero"* does not fire against real harness output on the nonzero-exit path. Combined with the deliberate removal of the old timeout retry (`"runtime tests timeout"` is now product evidence), the practical result in CI is **no outer retry at all** except in the narrow case where the outer *subprocess* timeout fires while only the non-terminal abort warning (`Build.RuntimeTests.cs:1686-1690`) has been emitted — that warning happens not to contain a product substring, so `ci_ios_test.py:310-313` can still retry there.

**Why the new tests miss it:** `build/scripts/ci/test_ci_ios_test.py:78-80` injects a *paraphrase* of the harness message (`"THE APP NEVER LAUNCHED. The launcher aborted before the app started."`) rather than the harness's actual terminal text, so the collision is not exercised. The acceptance evidence in `results.md` ("PASS — 5 tests … eligible startup recovery") is therefore true of the fixtures but not of the harness.

**Root class, not just this string:** the product list matches short generic substrings against the harness's *prose*, while the app's real markers are `RESULTS FLUSHED` / `TEST SUCCESS` / `TEST FAILURE` (`build/Models/LaunchDiagnostics.cs:44-49`). `"test failure"`, `"test success"`, `"[test]"` and `"dyld:"` are all short enough to collide with explanatory text; this instance is the one that is currently reachable.

---

## Medium (P2)

### M1 — `real_verdict_csharp_fail` is now unreachable for a failed generation, contradicting a still-present in-file contract and shifting corpus statuses undisclosed

`corpus-sweep/scripts/run_library.py:772-777` short-circuits classification to `generate_failed` whenever `generate_exit != 0`, before the `code == 0` / `nu1101` / `real_verdict_csharp_fail` arms.

The dominant path into that `else` branch is `fail_cause = {"kind": "csharp_fail"}` at `run_library.py:663-664`, which is set precisely for a **SWIFTBIND111** non-convergence. The file's own contract, unchanged and still present at `run_library.py:78-83`, says the opposite:

> `"""… a verify-recover non-convergence (SWIFTBIND111) or a wrapper error is a C#/build failure, NOT a missing input, and must reach a real compile verdict instead."""`

**Impact:** every library whose generate exits nonzero now reports library status `generate_failed` instead of `compile_failed` (`run_library.py:824-837`). That is a corpus-wide relabel against `sweep-summary-baseline-2026-07-19.json`, which `ratchet_compare.end_statuses()` (`ratchet_compare.py:131-136`) diffs directly. `results.md` § "Compatibility and evidence limits" discloses only the additive product fields and the new missing-subset statuses; this relabel is not mentioned. The compile evidence itself is preserved (`prec["compile"]`, `prec["managed_compiles"]` at `run_library.py:764-766`), so this is a labelling/telemetry defect rather than lost evidence — but the stale docstring makes the intended contract ambiguous for the next reader.

**Minimal shape:** a candidate whose generate exits 1 without `SWIFTBIND119` and whose emitted csproj fails `dotnet build` — previously `real_verdict_csharp_fail` → status `compile_failed`; now `generate_failed`.

### M2 — Neither new acceptance test file is executed by any gate

`build/scripts/ci/test_ci_ios_test.py` (docstring: *"Focused acceptance tests for ci_ios_test.run_tests"*) and `corpus-sweep/scripts/run_library_acceptance_test.py` (docstring: *"Permanent acceptance regressions for run_library.main"*) are invoked nowhere. `.github/workflows/ci.yml` and `release.yml` reference `python3` only at `ci.yml:157,168` and `release.yml:252,263,355,724,755,776,778` — `coverage-report.py`, `ci_ios_test.py`, and release scripting; no unittest invocation. `nuke test` runs the C# suites.

**Impact:** the classifier in H1 and the publication-authority logic in M1 have no automated regression barrier, so "permanent" is aspirational — a future edit to `PRODUCT_EVIDENCE_PATTERNS` or the classification ladder goes unnoticed. This is what would have caught H1 had the fixture used real harness text. Mitigating context: the pre-existing `run_library_receipt_test.py` (11 groups) is likewise unwired, so the corpus repo has an existing convention; the swift-bindings-side file is the new deviation from CLAUDE.md's durable-gate posture.

---

## Low (P3 / Info)

### L1 — `generated_files_owned_by_attempt` embeds a full file inventory into every `result.json`
`run_library.py:327-351` snapshots every non-`.log` file under `pdir` except `bin`/`obj`/`generation-attempts`, and `run_library.py:714` writes the changed set into the product record. A real product directory is large: `corpus-sweep/output/Eureka/Eureka/` holds 294+ files including an entire `pack-staging/…/Eureka.xcframework/` tree (framework binaries, `_CodeSignature/`, `.swiftinterface`, `.abi.json`). A fresh generation lists essentially all of them. `aggregate.py` loads every `result.json`. Functional, but a material size/IO change not noted in `results.md`.

### L2 — A requested primary can still receive no disposition when two requested products collapse under `norm()`
`run_library.py:452-457` strips `-`/`_` and a trailing `dynamic`/`static`; `run_library.py:474-479` dedupes the BFS by `seen_norm`, so the second of a collapsing pair produces no node and no `wanted_modules` entry. It is nevertheless in `discovered_requested` (`run_library.py:515`, keyed on the xcframework basename), so it is excluded from `missing_requested_products` too — no product row either way. This weakens the `results.md` claim *"Every candidate-requested primary receives a product disposition."* **Latent only:** I found no candidate in `candidates/candidates.json` listing a `…Dynamic`/`…Static` twin or an otherwise norm-colliding pair, so it is not reachable from the current corpus.

### L3 — New library-level statuses land in `ratchet_compare`'s unclassified bucket, and outrank `named_missing_input`
`run_library.py:828-833` can now emit `convert_incomplete` / `input_provenance_unknown` / `product_name_mismatch` for a library that *does* have products, and places them **above** `named_missing_input` in the precedence ladder. `ratchet_compare.classify_red` has explicit arms only for `convert_failed`/`no_primary_products` (`ratchet_compare.py:197`) and `named_missing_input` (`ratchet_compare.py:207`); the new statuses fall through to `ratchet_compare.py:264-266` `"manual trace required"`. A library with one missing requested product plus a genuine `named_missing_input` survivor moves from `module_scoped` to `unclassified`. The statuses themselves pre-date this change (the `not result["products"]` branch), so this widens an existing gap rather than creating one.

### L4 — Post-feed retry overwrites the first attempt's ownership set
`run_library.py:657` reassigns `gen_owned_files[module]` from the second attempt alone. If the retry fails without rewriting `binding-report.json`, the record reports `binding_report: true` with `binding_report_owned_by_attempt: false`. Self-consistent with the "final attempt owns publication" model, but the first attempt's ownership evidence is discarded even though its log is retained.

### L5 — `generation-attempts/` grows without bound
`run_library.py:311,314-317` keys each log on `time_ns()-pid`, so every sweep adds a log per product per phase, never pruned. Deliberate retention; noting the disk-growth consequence across repeated 250-library sweeps.

### L6 (Info) — Timeout/hang retry removed
The pre-change condition `"RUNTIME TESTS TIMEOUT" in last_output or "launch_failure" in last_output` retried an app hang; `"runtime tests timeout"` is now product evidence (`ci_ios_test.py:93`, matching `Build.RuntimeTests.cs:2682`), so a hang never retries. This is explicitly the documented design (`design.md`: *"an established pre-test startup failure is the sole retryable condition"*), recorded here only because it is the other half of H1's net effect. Note `"launch_failure"` never appeared in harness output either (grep: only the comment at `ci_ios_test.py:71`), so that arm was already dead before the change.

---

## Verified-correct / dismissed candidates

Preserved with reasons, per instruction:

- **`--tier` removal is genuinely dead-code removal, not a behavior change.** `run_tests`'s `cmd` (`ci_ios_test.py:195-202`) never contained `--tier`; the diff removes no such element, and `nuke binding-tests` has no `--tier` flag. Both call sites updated (`ci.yml:167-171`, `release.yml:262-266`); repo-wide grep finds no remaining `--tier`/`tier=` reference to this script — only `nuke validate --tier` in `CLAUDE.md:24` and an unrelated comment at `build/Build.BehaviorTier.cs:154`. The `CLAUDE.md:95` doc edit ("the full simulator suite") matches actual behavior. **Dropped — not a defect.**
- **`generate.log` compatibility.** `retain_generation_log` (`run_library.py:319-325`) keeps the path both consumers glob: `ratchet_compare.py:142` and `resweep_compare.py:32` use `output/<name>/*/generate.log` (single level), which does not reach `generation-attempts/`. The loss of `generate.pre-feed-retry.log` has no reader (grep: zero). **Dropped.**
- **stdout/stderr merge and `process_output_text`.** `ci_ios_test.py:281-287, 301-309` correctly normalize `bytes`/`None` on both the `CompletedProcess` and `TimeoutExpired` paths; `build_completed` (`ci_ios_test.py:216`) still matches `Build.RuntimeTests.cs:1701`. **Dropped.**
- **`wanted_modules` fallback narrowing** (`run_library.py:512`). With a non-empty `products` filter and no matching xcframework, `nodes` is empty anyway, so `list(nodes)` was already `[]`; the guard only prevents a fallback that could not fire. **Dropped — no behavior change.**
- **`resweep`/`aggregate` breakage from the additive product fields.** `aggregate.py` reads only `r["status"]` and worker-report classifications; `run_library.py:707-718`'s new keys are additive. **Dropped.**
- **Acceptance-test fidelity for the corpus side.** `test_publication_refusal_cannot_be_upgraded_by_compile_success` does drive the real `run_library.main()` through the new `run_library.py:772` branch (pre-created csproj, `SWIFTBIND111` log, no `SWIFTBIND119`, injected compile exit 0). The test is sound; only its swift-bindings sibling has the fixture-realism problem in H1. **Dropped as a finding, retained as context.**
- **Copyright header on `run_library_acceptance_test.py`.** Absent, but so is one on `run_library.py`, and the corpus repo carries no instruction file. **Dropped by convention.**

---

## Coverage and completeness

**Reviewed:** every hunk and both new files in `scope.diff`; the current state of `ci_ios_test.py` and `run_library.py` in full; the Nuke launch/abort/report paths that produce the strings the classifier keys on; the four corpus consumers of `result.json`/`generate.log`; both workflow call sites; `candidates.json` for L2 reachability.

**Not verified (no execution permitted):** I did not run the five CI tests, the four corpus tests, the 11 receipt groups, `py_compile`, or `git diff --check`, so `results.md`'s PASS column is taken as reported. I did not replay `natural-refusal-classifier-probe.py` or open `natural-refusal-classifier-result.json`; the natural-refusal control is asserted, not independently confirmed here — note that it exercises the corpus classifier (M1's area), not the CI classifier where H1 lives.

**Evidence gap behind H1:** confirming it end-to-end requires a real Nuke sim log from an exhausted-abort run piped through `is_retryable_pretest_startup_failure`. I established the collision from the exact source strings on both sides (`Build.RuntimeTests.cs:1679` and `ci_ios_test.py:93-108`); the one assumption is that Nuke surfaces a failing target's exception message on stdout/stderr, which I did not execute to confirm. If it did not, the `Log.Error` at `Build.RuntimeTests.cs:1672-1674` would carry the retry marker cleanly and H1 would not apply — that is the single check that would falsify it.

**Review status: complete for the requested scope**, with the two execution-dependent gaps named above. No findings were dropped for lack of time.