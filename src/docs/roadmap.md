# Roadmap

**Created**: February 2026
**Status**: Active — single source of truth for work items

This is the active work queue. Each phase should be completed before moving to the next. After completing a phase, update its status to "Done" and note any findings.

For detailed gap descriptions and contract matrix, see `testing-gaps.md`.
For deferred/aspirational work, see `Future/`.

---

## Phase A: Testing Infrastructure Hardening

**Status**: Done
**Completed**: February 2026
**Effort**: Small (1 session)

All P1 items from `testing-gaps.md` completed (Gaps 0a, 0b, 1, 2). Unit tests: 1459 passing (was 1439). Coverage report: Active 57/57 passing, 0 degraded, 55 compiled-out, 0 missing.

- **A1**: Runtime tests in default pipeline (Gap 0a)
- **A2**: Generator strict mode (Gap 0b)
- **A3**: Conductor unit tests — 20 tests (Gap 1)
- **A4**: Coverage report clarity (Gap 2)

---

## Phase B: Enable Disabled TestFramework Features

**Status**: Not Started
**Effort**: Medium (3-5 sessions)
**Why**: 51 must-pass features sit in `.disabled/` dirs. Enabling them closes the gap from 57/112 toward 90+.

Work in batches, easiest first. After each batch: run `build-and-test.sh`, check coverage report, fix any generator regressions before proceeding.

### B1. Error Handling (testing-gaps.md Gap 8)
- Enable `TestFramework/Sources/SwiftBindingsTestLib/ErrorHandling.disabled/`
- Start with `ThrowingFunctions.swift` — throwing functions work in Nuke/Lottie
- Verify Layer 1 generation succeeds
- Add runtime test `TestFramework/RuntimeTestsApp/ErrorHandling/ThrowingMethodTests.cs`

### B2. Generics (testing-gaps.md Gap 7)
- Enable `TestFramework/Sources/SwiftBindingsTestLib/Generics.disabled/`
- Generic structs, classes, bound generic params all work in real-world libraries
- Add runtime test `TestFramework/RuntimeTestsApp/Generics/GenericTests.cs`

### B3. Protocols (testing-gaps.md Gap 4)
- Enable protocol Swift sources (may need version guard adjustments)
- Verify protocol interfaces appear in generated bindings
- Add runtime tests for witness dispatch (blittable + String property getters/setters, method dispatch)
- File: `TestFramework/RuntimeTestsApp/Protocols/WitnessDispatchTests.cs`

### B4. Async (testing-gaps.md Gap 3)
- Enable `TestFramework/Sources/SwiftBindingsTestLib/Async.disabled/` starting with `AsyncMethods.swift`
- May need Swift wrapper generation step in `build-and-test.sh`
- Add runtime tests: `AsyncStringTests.cs`, `AsyncComplexTypeTests.cs`
- Blocked by: async Swift wrapper generation working for test library

### B5. Complex Compositions (testing-gaps.md Gap 5)
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

**Status**: In Progress (CryptoSwift done)
**Effort**: Medium (2-3 sessions)
**Why**: 3 libraries validated so far (Nuke, BlinkID, Lottie). Trying 1-2 more confirms the generator generalizes beyond tested patterns.

### C1. CryptoSwift (Done)
- Built xcframework, ran generator: 61.3% member coverage initially
- **Finding**: 34 of 42 skipped members blocked by `ArraySlice<T>` (no TypeDatabase registration)
- **Fix**: ArraySlice parameter normalization (Phase 62) — Swift wrapper accepts `Array<T>`, converts to `ArraySlice<T>` at call site
- **Result**: 65.1% member coverage (427/656), 21 methods recovered, 103/123 types emitted
- Remaining gaps: `CipherModeWorker` closure params (10 methods), mutating struct methods (2), `inout Array` secondary blockers (3)
- Added to `BindingTesting/CryptoSwift/` with regenerate-bindings.sh
- Added 4 ArraySlice features to TestFramework coverage matrix

### C2. Select and bind a second library
Candidates (pick 1):
- **Alamofire** — networking, heavy closure/async patterns
- **Kingfisher** — image loading, different patterns from Nuke
- **SwiftProtobuf** — value types, generics, enums heavy

### C3. Process (for remaining library)
1. Build xcframework for the library
2. Run generator, check binding report
3. Compare member coverage to existing libraries (target: 90%+)
4. Fix any new generator bugs found
5. Add to `BindingTesting/` with build/validate scripts
6. If bugs are found, add targeted TestFramework features for the patterns that failed

### C4. Document findings
- Update this roadmap if new architectural gaps found
- Update `CURRENT-STATUS.md` with new library stats
- Add any new skip reasons to `testing-gaps.md`

---

## After All Phases

Once A, B, C are complete:
- Must-pass features should be 80+ passing (up from 61)
- Runtime test coverage should cover most of the contract matrix
- 5-6 real-world libraries validated
- Test pipeline catches regressions automatically

Next priorities would be:
- Phase 3 DX work (MSBuild SDK, project templates) from `north-star.md`
- Remaining P3/P4 items from `testing-gaps.md` (PInvokeEmitter tests, golden snapshots, CI)
- Deferred work in `Future/` (NativeAOT validation, Roslyn analyzer, existential analysis)
