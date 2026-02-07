# Roadmap

**Created**: February 2026
**Status**: Active — work items in priority order

This is the active work queue. Each phase should be completed before moving to the next. After completing a phase, update its status to "Done" and note any findings.

---

## Phase A: Testing Infrastructure Hardening

**Status**: Done
**Completed**: February 2026
**Effort**: Small (1 session)

### Summary

All P1 items from `testing-gaps.md` completed. Unit tests: 1459 passing (was 1439). Coverage report: Active 57/57 passing, 0 degraded, 55 compiled-out, 0 missing.

### A1. Add runtime tests to default pipeline (Gap 0a) -- Done
- `run-tests.sh` calls `run-runtime-tests.sh --tier 2 --skip-regen --timeout 90` after coverage report
- Gated on: macOS, `xcrun` availability, simulator runtime, available iPhone simulator
- **Note**: Runtime tests currently crash on the known Mono JIT assertion (`jit-info.c:918`) when Tier 1 enum tests call P/Invoke. Crashes are detected by signature (`jit-info.c:918`) or by `RUNTIME TESTS CRASHED` marker and reported as warnings. Non-crash failures (genuine test regressions) fail the pipeline. Bridge tests (Tier 2) pass before the crash.

### A2. Generator strict mode (Gap 0b) -- Done
- `--strict` flag added to `regenerate-bindings.sh` (fails on non-zero generator exit)
- `build-and-test.sh` passes `--strict` through; `run-tests.sh` uses strict mode
- `run-tests.sh` fails (not warns) when degraded must-pass features > 0

### A3. Conductor unit tests (Gap 1) -- Done
- Created `ConductorTests.cs` with 20 tests covering:
  - Type handler selection (frozen struct, non-frozen struct, class, enum, protocol)
  - Method handler selection (struct constructor, class constructor, instance/static methods)
  - Priority resolution (struct constructor vs general method, frozen vs non-frozen)
  - Property and module handler selection
  - Empty argument handler fallback (returns false)
  - PInvokeHelperContext set/clear
  - Fresh handler instances per Construct() call

### A4. Coverage report clarity (Gap 2) -- Done
- Summary line: `Active: N/M passing, D degraded | Compiled-out: K | Known-unsupported: J`
- Both disabled patterns detected: `Dir.disabled/File.swift` and `Dir/File.swift.disabled`
- `missing` count now 0 (was 51 — all correctly reclassified as `compiled_out`)
- Pipeline fails if any features are truly `missing` (no test file at all)

### Verification
```bash
./run-tests.sh  # 1459 unit tests + strict mode + runtime tests (with known crash warning)
```

---

## Phase B: Enable Disabled TestFramework Features

**Status**: Not Started
**Effort**: Medium (3-5 sessions)
**Why**: 51 must-pass features are "missing" because Swift sources sit in `.disabled/` dirs. Enabling them systematically closes the gap from 57/93 toward 90+.

Work in batches, easiest first. After each batch: run `build-and-test.sh`, check coverage report, fix any generator regressions before proceeding.

### B1. Error Handling (Gap 8)
- Enable `TestFramework/Sources/SwiftBindingsTestLib/ErrorHandling.disabled/`
- Start with `ThrowingFunctions.swift` — throwing functions work in Nuke/Lottie
- Verify Layer 1 generation succeeds
- Add runtime test `TestFramework/RuntimeTestsApp/ErrorHandling/ThrowingMethodTests.cs` (success path + throw path)

### B2. Generics (Gap 7)
- Enable `TestFramework/Sources/SwiftBindingsTestLib/Generics.disabled/` (or create new files if needed)
- Generic structs, classes, bound generic params all work in real-world libraries
- Add runtime test `TestFramework/RuntimeTestsApp/Generics/GenericTests.cs`

### B3. Protocols (Gap 4)
- Enable protocol Swift sources (may need version guard adjustments)
- Verify protocol interfaces appear in generated bindings
- Add runtime tests for witness dispatch (blittable + String property getters/setters, method dispatch)
- File: `TestFramework/RuntimeTestsApp/Protocols/WitnessDispatchTests.cs`

### B4. Async (Gap 3)
- Enable `TestFramework/Sources/SwiftBindingsTestLib/Async.disabled/` starting with `AsyncMethods.swift`
- May need Swift wrapper generation step in `build-and-test.sh`
- Add runtime tests: `AsyncStringTests.cs`, `AsyncComplexTypeTests.cs`
- Blocked by: async Swift wrapper generation working for test library

### B5. Complex Compositions (Gap 5)
- Add `TestFramework/Sources/SwiftBindingsTestLib/Patterns/RealWorldCompositions.swift`
- Class with closure property, struct with optional array, method returning optional class, singleton static property, inheritance + protocol conformance
- Add runtime test `TestFramework/RuntimeTestsApp/Patterns/CompositionTests.cs`

### Verification after each batch
```bash
cd TestFramework
./build-and-test.sh && ./generate-coverage-report.sh
# Check: passing count increased, degraded didn't increase
# Then:
./run-runtime-tests.sh --tier 2 --timeout 90
```

---

## Phase C: New Library Validation

**Status**: Not Started
**Effort**: Medium (2-3 sessions)
**Why**: 3 libraries validated so far (Nuke, BlinkID, Lottie). Trying 1-2 more confirms the generator generalizes beyond tested patterns.

### C1. Select and bind a new library
Candidates (pick 1-2):
- **Alamofire** — networking, heavy closure/async patterns
- **Kingfisher** — image loading, different patterns from Nuke
- **SwiftProtobuf** — value types, generics, enums heavy
- **CryptoKit** (Apple framework) — system framework, different from StoreKit

### C2. Process
1. Build xcframework for the library
2. Run generator, check binding report
3. Compare member coverage to existing libraries (target: 90%+)
4. Fix any new generator bugs found
5. Add to `BindingTesting/` with build/validate scripts
6. If bugs are found, add targeted TestFramework features for the patterns that failed

### C3. Document findings
- Update `remaining-work.md` if new architectural gaps found
- Update `CURRENT-STATUS.md` with new library stats
- Add any new skip reasons to `testing-gaps.md`

---

## After All Phases

Once A, B, C are complete:
- Must-pass features should be 80+ passing (up from 57)
- Runtime test coverage should cover most of the contract matrix
- 4-5 real-world libraries validated
- Test pipeline catches regressions automatically

Next priorities would be:
- Phase 3 DX work (MSBuild SDK, project templates) from `north-star.md`
- Remaining P3/P4 items from `testing-gaps.md` (PInvokeEmitter tests, golden snapshots, CI)
- NativeAOT hands-on validation
