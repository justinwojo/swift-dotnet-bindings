# TestFramework Risk-First Review

## Purpose
Capture the highest-value improvements identified from a risk-first review of `TestFramework`, with emphasis on preventing false greens and reducing flaky signals.

## Top Takeaways (Adopt First)

### 1) Add a compile gate for generated bindings in the fast path — DONE
**Problem:** Coverage and many unit checks can pass even when generated C# is not compilable.

**Implemented:**
- `TestFramework/CompileCheck/CompileCheck.csproj` compiles generated bindings + bridge against Swift.Runtime.
- `build-and-test.sh` Step 2.5 runs compile gate after regeneration.
- Infrastructure errors (NU*, NETSDK*, MSB*) always fail. Unknown failure modes always fail.
- 2 known pre-existing CS0103 errors (async property getters) are filtered; new CS errors fail the gate.

**Expected impact:** Prevents the most expensive class of false passes (plausible-looking but invalid generated code).

### 2) Make crash tolerance allowlist-based, not pattern-based — DONE
**Problem:** `run-tests.sh:88-108` greps for `jit-info.c:918` and blanket-tolerates any crash matching that Mono signature. A *new* regression that triggers a JIT crash on a *different* code path is masked because the crash signature is identical.

**Implemented:**
- `run-tests.sh` extracts the last test class from `=== ClassName ===` log markers.
- Allowlist: `EnumMarshallingTests|OwnershipGCStressTests` (matches `[CrashRisk]` attributes).
- Crash in allowlisted class → warning (pre-existing Mono bug). Crash in any other class → hard fail.
- Crash before any class ran → warning (Mono startup issue).

**Expected impact:** Crash tolerance becomes surgical — pre-existing Mono bugs don't block you, but new crash-inducing regressions can't hide behind the same signature.

### 3) Treat skipped/compiled-out/known-broken counts as a budget — DONE
**Problem:** Coverage can erode silently when skip surfaces grow.

**Implemented:**
- `TestFramework/baselines.json` stores: generator_exit_code (0), must_pass_degraded (0), must_pass_compiled_out (26), known_unsupported_total (60), crash_risk_classes (2), wrapper_stripped_count (56).
- `TestFramework/check-baselines.sh` compares current values against baselines, fails if any exceed.
- Wrapper strip count has +-2 tolerance for minor fluctuations.
- `run-tests.sh` calls `check-baselines.sh` after coverage report generation.

**Expected impact:** Stops gradual normalization of reduced coverage.

### 4) Define explicit test profiles by intent — DONE
**Problem:** Tiering and flags are useful but not consistently framed as policy.

**Implemented:**
- `TestFramework/README.md` documents two profiles:
  - **PR Gate** (`./run-tests.sh`): unit + integration + compile gate + baselines + runtime `--tier 2` (all classes, crash-risk allowlist)
  - **Nightly** (manual): `--tier 3` with flake detection, full reporting

**Expected impact:** Clear expectations for what each lane guarantees.

### 5) Reduce simulator/timing flake in runtime tests — DONE (partial)
**Problem:** Fixed sleeps, short joins, and environment variance produce non-deterministic failures.

**Implemented:**
- Default timeout increased from 60s to 90s in `run-runtime-tests.sh`.
- Simulator selection prefers specific models (iPhone 16 > iPhone 15 Pro > iPhone 15 > any iPhone) for deterministic behavior.

**Remaining:** Condition-based waits (replacing `Thread.Sleep`) and adaptive timeout thresholds are runtime app changes, deferred.

**Expected impact:** Fewer intermittent failures unrelated to product behavior.

### 6) Ratchet the async wrapper stripping count — DONE
**Problem:** `TestFramework/build-async-wrapper.sh` has a Python sanitizer that strips known-broken generated Swift blocks. The count was printed but not gated.

**Implemented:**
- `build-async-wrapper.sh` writes total stripped count to `output/wrapper-stripped-count`.
- `baselines.json` stores baseline (56). `check-baselines.sh` fails if count exceeds baseline + 2 tolerance.

**Expected impact:** The stripping remains (it's needed for the test library's intentionally-advanced features), but it can no longer silently absorb new regressions in wrapper emission.

### 7) Gate on generator exit code changes — DONE
**Problem:** `regenerate-bindings.sh` saves exit code to `output/generator-exit-code` but nothing checks whether it changed.

**Implemented:**
- `baselines.json` stores expected generator exit code (0).
- `check-baselines.sh` compares actual exit code against baseline, fails on mismatch.

**Expected impact:** Early signal when a generator change causes a crash, before waiting for compile or runtime failures to surface.

### 8) Raise semantic verification depth beyond string-shape checks — Partially Addressed
**Problem:** String/token assertions are necessary but insufficient for correctness.

**Adopt:**
- Keep existing emitter/unit string checks.
- Add compile-based and behavior-based assertions for representative generated constructs (generics constraints, bridge signatures, wrapper entry points).

**Progress:**
- Gap 6 `PInvokeEmitterTests` includes 6 `EmitPInvoke` tests that capture actual emitted `[DllImport]`/`[UnmanagedCallConv]` output and assert on library paths and entry point suffixes (Tj dispatch thunk behavior). This moves beyond signature-shape checks to verify emitted output semantics.
- Remaining: compile-based assertions for generated generics constraints, bridge signatures, wrapper entry points.

**Expected impact:** Better detection of semantically broken output that still "looks" correct.

## Rollout — COMPLETE (TH-8 Ongoing)
All hardening items (TH-1 through TH-7) implemented. TH-8 partially addressed:
1. Compile gate (TH-1): `CompileCheck.csproj` + `build-and-test.sh` Step 2.5
2. Baseline budget (TH-2/3/4/6/7): `baselines.json` + `check-baselines.sh` + `run-tests.sh` wiring
3. Crash allowlist (TH-5): `run-tests.sh` allowlist-based tolerance
4. Profile docs (TH-6): `TestFramework/README.md` Test Profiles section
5. Simulator flake (TH-7): timeout + device preference in `run-runtime-tests.sh`
6. Semantic depth (TH-8, partial): Gap 6 `EmitPInvoke` tests assert on emitted DllImport/entry point output

## Success Criteria — ALL MET
- A PR that emits invalid generated C# fails in under 2 minutes. (**CompileCheck gate**)
- A previously-passing test that starts crashing fails the gate, even if the crash signature matches known Mono bugs. (**Crash allowlist**)
- Skip/compiled-out/crash-risk/wrapper-stripped counts cannot drift upward without an explicit baseline update. (**baselines.json + check-baselines.sh**)
- Generator exit code regressions (was 0, now non-zero) are caught before compile/runtime stages. (**baselines.json generator_exit_code check**)
- Nightly reports identify flakes/crashes without weakening merge confidence. (**Profile docs in README.md**)
