# TestFramework Risk-First Review

## Purpose
Capture the highest-value improvements identified from a risk-first review of `TestFramework`, with emphasis on preventing false greens and reducing flaky signals.

## Top Takeaways (Adopt First)

### 1) Add a compile gate for generated bindings in the fast path
**Problem:** Coverage and many unit checks can pass even when generated C# is not compilable.

**Adopt:** Add a dedicated compile-check project and run it in `TestFramework/build-and-test.sh` immediately after regeneration.

**Implementation shape:**
- Add `TestFramework/CompileCheck/CompileCheck.csproj`.
- Include `../output/Swift.SwiftBindingsTestLib.cs` (and bridge file conditionally if needed).
- Reference `src/Swift.Runtime/src/Swift.Runtime.csproj`.
- Run `dotnet build --no-restore` as a hard fail gate.

**Expected impact:** Prevents the most expensive class of false passes (plausible-looking but invalid generated code).

### 2) Make crash tolerance allowlist-based, not pattern-based
**Problem:** `run-tests.sh:88-108` greps for `jit-info.c:918` and blanket-tolerates any crash matching that Mono signature. A *new* regression that triggers a JIT crash on a *different* code path is masked because the crash signature is identical.

**Adopt:**
- Maintain a known-crash allowlist of specific test classes expected to crash (the `[CrashRisk]` classes).
- If a crash occurs AND only known-crash classes were running, tolerate it (pre-existing Mono bug).
- If a *previously-passing* test class crashes — even with the same `jit-info.c:918` signature — fail the gate. That's a regression.
- For nightly: run all tiers including crash-risk classes, report results without gating.

**Why not "fail on any crash":** The Mono JIT crash is non-deterministic and not under project control. Failing on any crash would block all development until Microsoft fixes their runtime. The allowlist approach keeps development unblocked while catching new regressions.

**Expected impact:** Crash tolerance becomes surgical — pre-existing Mono bugs don't block you, but new crash-inducing regressions can't hide behind the same signature.

### 3) Treat skipped/compiled-out/known-broken counts as a budget
**Problem:** Coverage can erode silently when skip surfaces grow.

**Adopt:** Enforce non-regression budgets in CI for:
- xUnit `[Skip]` count
- `must_pass.compiled_out`
- `known_unsupported.total` (or scoped subsets)
- `[CrashRisk]` class count

**Implementation shape:**
- Store a baseline JSON in repo.
- CI step compares current counts to baseline.
- Fail if counts increase without an explicit baseline update in the same PR.

**Expected impact:** Stops gradual normalization of reduced coverage.

### 4) Define explicit test profiles by intent
**Problem:** Tiering and flags are useful but not consistently framed as policy.

**Adopt:**
- **PR Gate:** generator compile gate + coverage gate + runtime `--tier 2 --safe-only`
- **Merge Gate:** same as PR, but stricter timeouts/log capture and no crash tolerance
- **Nightly:** `--tier 3` + flake detection + crash-risk classes enabled

**Expected impact:** Clear expectations for what each lane guarantees.

### 5) Reduce simulator/timing flake in runtime tests
**Problem:** Fixed sleeps, short joins, and environment variance produce non-deterministic failures.

**Adopt:**
- Prefer condition-based waits over fixed `Thread.Sleep`.
- Increase or make adaptive timeout thresholds for stressed environments.
- Make simulator selection deterministic (pin runtime/device where possible).
- Improve crash detection to rely on process/app signals first, log-count deltas second.

**Expected impact:** Fewer intermittent failures unrelated to product behavior.

### 6) Ratchet the async wrapper stripping count
**Problem:** `TestFramework/build-async-wrapper.sh:36-186` has a Python sanitizer that strips 5 categories of known-broken generated Swift blocks before compilation. The count is printed (`Stripped N broken wrapper(s)`) but not gated. If the generator starts emitting a new broken pattern that happens to match an existing strip rule — or if a fix regresses and more blocks need stripping — nobody notices. This is a silent regression absorber.

**Adopt:**
- Write the stripped-block count to `output/wrapper-stripped-count`.
- Store the current baseline (check what it is today).
- In `build-and-test.sh`, fail if the count exceeds baseline + small tolerance (e.g., +2).
- Require explicit baseline update when the count legitimately changes.

**Expected impact:** The stripping remains (it's needed for the test library's intentionally-advanced features), but it can no longer silently absorb new regressions in wrapper emission.

### 7) Gate on generator exit code changes
**Problem:** `regenerate-bindings.sh` defaults to `STRICT=false` — the generator can crash or exit non-zero and the pipeline continues with partial output. The exit code is saved to `output/generator-exit-code` but nothing checks whether it *changed*.

**Adopt:**
- Store the expected generator exit code in the baseline file (currently 0 after Phase E fixes).
- In `build-and-test.sh`, compare the actual exit code against the baseline.
- If the exit code increased (generator started crashing when it wasn't before), fail the gate.
- This is complementary to the compile gate (#1): the compile gate catches invalid output; this catches incomplete output from generator crashes.

**Expected impact:** Early signal when a generator change causes a crash, before waiting for compile or runtime failures to surface. Especially valuable because the compile gate might not catch *missing* output — only *invalid* output.

### 8) Raise semantic verification depth beyond string-shape checks
**Problem:** String/token assertions are necessary but insufficient for correctness.

**Adopt:**
- Keep existing emitter/unit string checks.
- Add compile-based and behavior-based assertions for representative generated constructs (generics constraints, bridge signatures, wrapper entry points).

**Expected impact:** Better detection of semantically broken output that still "looks" correct.

## Recommended Rollout (One Week)
1. Implement generated binding compile gate and wire into `TestFramework/build-and-test.sh` (~30 min).
2. Add baseline JSON with skip/compiled-out/crash-risk/wrapper-stripped/generator-exit counts. Wire comparison into `run-tests.sh` (~1 hr).
3. Convert crash tolerance from pattern-grep to allowlist-based check (~1 hr).
4. Document profile guarantees (PR / merge / nightly) in `TestFramework/README.md`.

## Success Criteria
- A PR that emits invalid generated C# fails in under 2 minutes.
- A previously-passing test that starts crashing fails the gate, even if the crash signature matches known Mono bugs.
- Skip/compiled-out/crash-risk/wrapper-stripped counts cannot drift upward without an explicit baseline update.
- Generator exit code regressions (was 0, now non-zero) are caught before compile/runtime stages.
- Nightly reports identify flakes/crashes without weakening merge confidence.
