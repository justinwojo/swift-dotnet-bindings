# Re-review — P1 trustworthy acceptance (round 2)

**Packet:** `scope-r2.diff` / `scope-r2.json`. Every hash in `scope-r2.json` corresponds to a file I opened; the content I read matches the r2 diff. `CLAUDE.md`'s hash is unchanged from r1, so that hunk was not re-examined beyond confirming it. I read `adjudication.md` in full and treat its dismissals as settled unless I found new contrary evidence (I did not).

**Constraints honored:** read-only; no builds, simulator/device operations, mutations, or review loops. No caller-side skills consulted. Guardrails from `~/.claude/review-notes/swift-bindings.md` applied.

---

## Verification of the accepted High

### ✅ H1 (r1) — fixed, verified against real harness text

`build/scripts/ci/ci_ios_test.py:92` now reads `"test failure:"` with the rationale inline at `:90-91`. Verified from both sides:

- **The collision is gone.** `build/Build.RuntimeTests.cs:1679` is the sole producer of the offending prose — `"carries NO verdict about the bindings — it is a deploy/launch failure, not a test failure. "` — and it has no colon, so it no longer matches. I re-walked the complete exhausted-abort message against all 22 `PRODUCT_EVIDENCE_PATTERNS`: none match, while `"the app never launched"` still satisfies `PRETEST_STARTUP_FAILURE_PATTERNS`. The retry is now reachable on the child-nonzero-exit path.
- **The narrowing loses no real evidence.** Every actual verdict emitter uses the colon: `BindingTests/RuntimeTestsApp/Program.cs:364` (`"TEST FAILURE: No test class matches filter"`) and `:423` (`$"TEST FAILURE: {results.Failed} tests failed"`), plus the same shape in the Mac/MacCatalyst/tvOS apps and the mixed-gate fixture apps (`build/Build.BindingTests.MixedPack.cs:602`, `build/Build.BindingTests.MixedDirect.cs:308`). A case-insensitive search for `test failure:` across `build/` returns *only* those app-side writes and the classifier itself — no Nuke prose. So the comment "The app emits this delimiter" is factually accurate, and no colon-less verdict marker exists to be missed.
- **The fixture is now source-exact.** `test_ci_ios_test.py:18-22` reproduces `Build.RuntimeTests.cs:1677-1682` character-for-character with `legLabel` = `"iOS Simulator"` and `MaxLauncherAbortAttempts` = 3 (`build/Models/LaunchDiagnostics.cs:77`), including the explanatory negation. Both prior report's gaps are closed: the nonzero path (`test_pretest_startup_failure_can_recover`), the `TimeoutExpired` path with bytes input (`test_pretest_timeout_can_recover_with_actual_exhaust_text`, exercising `process_output_text`'s decode), bounded exhaustion across mixed exit/timeout outcomes, and a 7-marker dominance sweep (`test_actual_app_verdict_and_loader_markers_dominate_exhaust_text:119-120`) that I checked pattern-by-pattern — every marker in that tuple does match a live pattern, so the control is real rather than vacuous.

### ✅ M1 (r1, documentation half) — fixed

`corpus-sweep/scripts/run_library.py:82-84` now states the actual contract (*"remains a generation failure. A retained project may receive a diagnostic compile verdict; that evidence cannot authorize publication of failed generation."*), and `results.md:74` discloses the label shift (*"Generation refusal now consistently reports generate_failed (previous compile_failed labels may shift)"*) plus the ratchet manual-trace consequence. The semantics were adjudicated as intentional; not re-raised.

### ✅ M2 (r1) — fixed for the main repo

`.github/workflows/ci.yml:64-68` and `.github/workflows/release.yml:160-163` now run `python3 -B build/scripts/ci/test_ci_ios_test.py -v` ahead of `dotnet nuke test`. Verified this actually gates: the job is `runs-on: macos-26` with `actions/checkout` (`ci.yml:17-22`), GitHub's default `run` shell carries `-e` so a nonzero unittest exit aborts before `nuke test`, `sys.path[0]` resolution makes `import ci_ios_test` and its `from sim_manager import …` work from the repo root, and `sim_manager.py:28-38` is stdlib-only with no import-time side effects. I also confirmed the suite cannot mutate a CI checkout: the fixture path `/nonexistent/audit/BindingTests` never satisfies the `os.path.isdir` guard before `shutil.rmtree` on the retry path (`ci_ios_test.py:243-246`). Corpus-side non-wiring was adjudicated to the existing local convention; not re-raised.

### ✅ L2 (r1, twin completeness) — fixed

`run_library.py:520` switches the completeness basis from artifacts-on-disk to selected work (`selected_requested = {nodes[m]["product"].lower() for m in wanted_modules}`), and `:801-805` assigns a discovered-but-collapsed requested product `input_provenance_unknown` with an accurate cause. I traced the twin control deterministically: `find_xcframeworks` sorts `Probe.xcframework` before `ProbeDynamic.xcframework` (`'.'` < `'D'`), so `seen_norm` retains `Probe` and `ProbeDynamic` becomes the unauthorized row — the assertion at `run_library_acceptance_test.py:874-880` is stable, not order-lucky. No double-rowing: the missing set and the real rows are both derived from `nodes[m]["product"]` over the same `wanted_modules`.

### ✅ Grok Low (mismatch wording) — fixed

`run_library.py:283` now reads *"produced, but the requested product name(s) were not discovered"*. Truthful on both call paths; the empty-`wanted` shape that would have made it awkward is unreachable (empty `wanted` ⇒ all xcframeworks seed ⇒ non-empty `products`).

---

## New findings

### Low (P3)

#### N1 — `results.md`'s primary validation table still reports the round-1 test counts
`src/docs/audit-2026-09/execution/P1/results.md:32` states `"PASS — 5 tests: fail+timeout dominance, crash+timeout cross-stream dominance, eligible startup recovery, bounded exhaustion, healthy full-suite command control"` and `:33` states `"PASS — 4 tests"`. The files now contain **7** and **5** test methods respectively, and `results.md:74` says so (*"7 CI tests pass … Corpus5 acceptance cases pass"*). The document therefore contradicts itself, and its authoritative evidence table omits the two controls that close the accepted High (`test_pretest_timeout_can_recover_with_actual_exhaust_text`, `test_actual_app_verdict_and_loader_markers_dominate_exhaust_text`) and the twin control. **Impact:** documentation only — a reader auditing coverage from the table under-counts the very tests that discharge the High.

#### N2 — `design.md` names a classification the missing-product path cannot emit
`src/docs/audit-2026-09/execution/P1/design.md:43`: *"set will receive its own `named_missing_input` product record derived from the"*. The implementation never produces that label there — `classify_missing_products` (`run_library.py:242-287`) returns only `input_provenance_unknown`, `convert_incomplete`, or `product_name_mismatch`, and `run_library.py:801-802` forces `input_provenance_unknown` for the twin case. The acceptance test asserts `convert_incomplete` (`run_library_acceptance_test.py:868`). `results.md:19` describes it correctly (*"a named incomplete/mismatch/unknown row"*), so only the design document is wrong. **Impact:** documentation only; no code path affected. **Disclosure:** this text was present in round 1 and I did not flag it — it is not a regression introduced by r2.

#### N3 — `EXHAUSTED_LAUNCH` is a hand-copied literal with no tripwire to its C# source
`build/scripts/ci/test_ci_ios_test.py:16-22` duplicates `build/Build.RuntimeTests.cs:1677-1682` by hand. Nothing links them: rewording the C# message (or changing `MaxLauncherAbortAttempts`) leaves the suite green while production silently reverts to the r1 behavior. This is the exact drift mechanism that produced the accepted High — the r1 fixture was a paraphrase, so the collision went unnoticed. Secondary: the fixture models only the thrown exception text, not the surrounding envelope a real run also emits (`Build.RuntimeTests.cs:1770-1771` `"=== APP OUTPUT ==="` + raw launcher output, and the `Log.Error` at `:1672-1674`). I checked that envelope for collisions — the `LaunchDiagnostics.LauncherAborted` markers (`LaunchDiagnostics.cs:32-40`) match no product pattern — so there is no current defect, only unmodelled surface. **Per the severity rule, future drift is Low.**

#### N4 (Info, latent) — completeness basis is computed before pass 2, but read after it
`run_library.py:520` builds `selected_requested` from `nodes` *before* `build()` runs, while the verdict loop reads `nodes[module]["product"]` at `:706` *after*. `ensure_node` (`run_library.py:539-566`) writes `nodes[m]` keyed on the sidecar's `primaryModule`, which can differ from the module it was asked for; if such a key collided with a wanted module's key, the verdict row's product name would diverge from `selected_requested` and the same requested product could receive both a real row and a missing row. **Not reachable from the current corpus:** this needs a binary-linkage dependency whose xcframework reports a wanted primary's module name under a different product name, and I found no such candidate in `candidates/candidates.json`. Recorded because r1's basis (`discovered_requested`) could not produce a duplicate row and r2's can.

---

## Candidate dispositions

**Confirmed fixed (verified in current source, not re-raised):** r1 H1; r1 M1 documentation half; r1 M2 main-repo wiring; r1 L2 twin completeness; Grok's mismatch-wording Low.

**Adjudicated as intentional — accepted, not re-litigated (no new contrary evidence found):**
- `compile_failed` → `generate_failed` semantics (a failed generator cannot publish) — `run_library.py:772-777`.
- Corpus tests remaining manually invoked (no corpus CI exists to wire).
- L1 full artifact-ownership inventory IO/size; L4 final-attempt ownership scope; L5 accumulating attempt logs; L6 no retry for product timeout/hang; L3 aggregate-status precedence and the ratchet manual-trace bucket. All disclosed at `results.md:74-76`.
- Grok's SKIP-marker dismissal — I independently confirmed `"[skip]"` is present in `PRODUCT_EVIDENCE_PATTERNS` (`ci_ios_test.py:86`) and found no retry hole.
- r1 dismissals (tier removal as dead-code, `generate.log` compatibility for `ratchet_compare.py:142` / `resweep_compare.py:32`, stdout/stderr normalization, `wanted_modules` fallback narrowing, additive-field consumers, copyright convention) — re-checked as unchanged in r2; still dismissed.

**New policy consequence, deferred to the adjudication (not raised as a finding):** the twin correction makes any future candidate that legitimately declares both a static and dynamic product of one module permanently non-green (`run_library.py:823` sets `all_ok = False` unconditionally for the collapsed twin), with no way to express "these two products are the same module." The adjudication explicitly chose *"unauthorized publication and nonzero aggregate"* for this shape; recorded here so the choice is visible, not contested.

---

## Coverage

**Rechecked across the full original scope:** both workflow files (test-job wiring and the `--tier` call sites), `ci_ios_test.py` classifier and both retry paths, `test_ci_ios_test.py` in full against the C# it models, `run_library.py`'s changed regions plus the surrounding discovery/build/verdict flow, `run_library_acceptance_test.py` in full, `results.md`, `design.md`, and `adjudication.md`. Supporting source re-opened: `Build.RuntimeTests.cs` (abort/report paths), `Build.BindingTests.cs:1380-1404`, `Models/LaunchDiagnostics.cs`, `Tools/SimCtl.cs` / `DeviceCtl.cs` verdict scanners, all four `RuntimeTestsApp*/Program.cs` marker emitters, `sim_manager.py`, `candidates/candidates.json`, and the corpus consumers `ratchet_compare.py` / `resweep_compare.py` / `aggregate.py`.

**Not rechecked / not verifiable here:** `CLAUDE.md` (hash identical to r1; the single doc hunk was verified then). I could not execute anything, so the reported results — 7 CI tests, 5 corpus tests, 11 receipt groups, `py_compile`, `git diff --check`, the golden-old-classifier-fails-new-tests demonstration (`ci-golden-before-r2.log`), and the natural-refusal classification replay — are taken as reported; I did not open the referenced logs or `natural-refusal-classifier-result.json`. Per the stated evidence limits, none of this constitutes simulator, device, fresh-generation, or native qualification.

**Status: re-review complete for the requested scope.** The accepted High is genuinely repaired and independently confirmed from both the classifier and the harness/app emitters. Remaining findings are documentation accuracy (N1, N2), test-fixture drift risk (N3), and one latent completeness edge (N4); none is a defect in current behavior. No area of the original scope was skipped, and no High/Medium candidate was dropped without a recorded reason above.