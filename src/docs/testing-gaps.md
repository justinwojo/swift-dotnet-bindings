# Testing Gaps

**Date**: February 2026
**Context**: TestFramework Phases A-D complete, 25 real-world libraries binding clean (0 generator errors)

This document tracks known testing gaps to be addressed incrementally. Each gap includes priority, rationale, and what "done" looks like.

---

## Current Test Infrastructure

| Layer | What It Tests | Count | Location |
|-------|---------------|-------|----------|
| **Unit Tests** | Component logic (marshaler, emitter, parser) | 2,395 tests | `src/Swift.Bindings/tests/UnitTests/` |
| **Runtime Library Tests** | SwiftArray, SwiftString, metadata | 116 tests | `src/Swift.Runtime/tests/` |
| **Integration Tests** | Full Swift↔.NET interop | 699 tests (11 skipped) | `src/Swift.Bindings/tests/IntegrationTests/` |
| **TestFramework Layer 1** | Generator correctness across feature matrix | 94/94 must-pass features | `TestFramework/` |
| **TestFramework Layer 2** | Runtime behavior on iOS simulator | 185 tests at Tier 2 | `TestFramework/RuntimeTestsApp/` |
| **Real-World Bindings** | End-to-end against shipping libraries | 25 clean + 5 env-only (see `binding-errors.md`) | `BindingTesting/` + `/Users/wojo/Dev/Libraries/` |

---

## Gap 0a: Runtime Tests Not in Default Pipeline — DONE (Phase A1)

**Priority**: P1 — Layer 2 regressions silently skipped
**Area**: Infrastructure
**Status**: Complete — `run-tests.sh` now calls `run-runtime-tests.sh --tier 2 --skip-regen --timeout 90` after coverage report, gated on macOS + simulator availability.

- [x] `run-tests.sh` calls `run-runtime-tests.sh` after the coverage report
- [x] Non-zero exit from runtime tests fails the overall `run-tests.sh`
- [x] Crash detection: known Mono JIT assertion (`jit-info.c:918`) reported as warning, other crashes fail pipeline

---

## Gap 0b: Generator Non-Zero Exit Tolerated — DONE (Phase A2)

**Priority**: P1 — Silent degradation of core binding flow
**Area**: TestFramework Layer 1
**Status**: Complete — `--strict` flag added to `regenerate-bindings.sh`, `run-tests.sh` uses strict mode and fails on degraded must-pass features > 0.

- [x] `--strict` flag added to `regenerate-bindings.sh` (fails on non-zero generator exit)
- [x] `build-and-test.sh` passes `--strict` through
- [x] `run-tests.sh` fails (not warns) when degraded must-pass features > 0

---

## Gap 1: Conductor Unit Tests — DONE (Phase A3)

**Priority**: P1 — Highest ROI safety improvement
**Area**: Unit tests
**Status**: Complete — `ConductorTests.cs` created with 20 tests.

- [x] Handler selection for each major type category (frozen struct, non-frozen struct, class, enum, protocol)
- [x] Method handler selection (struct constructor, class constructor, instance/static methods)
- [x] Priority resolution (struct constructor vs general method, frozen vs non-frozen)
- [x] Property and module handler selection
- [x] Empty argument handler fallback
- [x] PInvokeHelperContext set/clear
- [x] Fresh handler instances per `Construct()` call

---

## Gap 2: Coverage Report Active vs Future Reporting — DONE (Phase A4)

**Priority**: P1 — Immediate clarity improvement
**Area**: TestFramework Layer 1
**Status**: Complete — Summary line now shows `Active: N/M passing, D degraded | Compiled-out: K | Known-unsupported: J`. Missing count is 0 (all correctly reclassified). Pipeline fails if any features are truly missing.

- [x] Summary line explicitly shows active/compiled-out/known-unsupported
- [x] Both disabled patterns detected: `Dir.disabled/File.swift` and `Dir/File.swift.disabled`
- [x] `missing` count now 0 (was 51 — all correctly reclassified as `compiled_out`)
- [x] Pipeline fails if any features are truly `missing` (no test file)

---

## Gap 3: Async Runtime Tests

**Priority**: P1 — Biggest regression risk area
**Area**: TestFramework Layer 2
**Risk**: Phases 58-60 all fixed async bugs that slipped through testing

### Problem

9 Swift async test files sit in `.disabled/` directories. Runtime test stubs exist (`AsyncStringTests.cs`, `AsyncComplexTypeTests.cs`) but are deferred. Async is where bugs hide — 3 of 6 recent bug-fix phases (58, 59, 60) were async marshalling issues.

### What "Done" Looks Like

- [ ] Move core async Swift sources out of `.disabled/` (start with `AsyncMethods.swift`)
- [ ] Verify Layer 1 generation succeeds for async methods
- [ ] Implement `AsyncStringTests.cs` — UTF-8 round-trip through async boundary
- [ ] Implement `AsyncComplexTypeTests.cs` — Class/Enum/Array async returns
- [ ] Contract matrix cells `Async × {String, Array, Class, Enum}` move from `R◐` to `R✓`

### Blocked By

- Async Swift wrapper generation must work for test library (currently works for Nuke/Lottie)
- May need Swift wrapper source generation step in `build-and-test.sh`

### Files

- Swift sources: `TestFramework/Sources/SwiftBindingsTestLib/Async.disabled/`
- Runtime stubs: `TestFramework/RuntimeTestsApp/Async/`

---

## Gap 4: Protocol Witness Dispatch Runtime Tests

**Priority**: P2 — Critical for Nuke/BlinkID patterns
**Area**: TestFramework Layer 2
**Risk**: Witness dispatch regressions caught only by real-world binding tests

### Problem

Protocol witness dispatch (Phase A: blittable read-only) is implemented but has no runtime test coverage. `WitnessDispatchTests.cs` exists as a stub, deferred because protocol interfaces aren't in generated bindings for the test library. The generator supports protocol conformance emission for real-world libraries (Nuke, Lottie), but the TestFramework Swift sources don't yet produce protocol interfaces — the Swift protocol test files need version guard adjustments or new protocol definitions that the generator can consume.

### What "Done" Looks Like

- [ ] Protocol interfaces appear in TestFramework generated bindings
- [ ] `WitnessDispatchTests.cs` tests:
  - Property getter dispatch (blittable)
  - Property getter dispatch (String)
  - Method dispatch with blittable params/returns
  - Method dispatch with String params/returns
- [ ] Contract matrix "Protocol Witness Dispatch" rows move from `R?` to `R✓`

### Depends On

- Protocol Swift sources must be enabled (currently compiled-out due to Swift version guard)
- May need `BasicProtocols.swift` version guard adjusted or new protocol test file

### Files

- Swift: `TestFramework/Sources/SwiftBindingsTestLib/Protocols/BasicProtocols.swift`
- Runtime stub: `TestFramework/RuntimeTestsApp/Protocols/WitnessDispatchTests.cs`

---

## Gap 5: Complex Type Composition Tests

**Priority**: P2 — Covers real-world patterns Nuke/Lottie exercise
**Area**: TestFramework (Layer 1 + Layer 2)
**Risk**: Cross-cutting patterns break when individual features work fine

### Problem

Nuke, Lottie, and BlinkID exercise type composition patterns that TestFramework tests individually but not in combination:

| Pattern | Real-World Example | TestFramework Status |
|---------|-------------------|---------------------|
| Class with closure property | Lottie animation callbacks | Closures tested, but not as stored properties |
| Struct with optional array field | BlinkID config types | Optionals and arrays tested separately |
| Method returning generic optional | Nuke cache lookups | Not tested in combination |
| Existential collections | Nuke `[any ImageProcessing]` | Degraded (Mono JIT bug) |
| Deep inheritance + multi-protocol | Lottie animation hierarchy | Not in TestFramework |
| Singleton static property + async | Nuke `ImagePipeline.shared` | Not tested |

### What "Done" Looks Like

- [ ] Add `TestFramework/Sources/SwiftBindingsTestLib/Patterns/RealWorldCompositions.swift`:
  - Class with closure stored property
  - Struct with optional array field
  - Method returning optional class
  - Static singleton property on class
  - Class inheriting from base + conforming to protocol
- [ ] Layer 1: All compositions generate clean bindings
- [ ] Layer 2: Runtime test file `CompositionTests.cs` validates round-trips

### Files

- New Swift: `TestFramework/Sources/SwiftBindingsTestLib/Patterns/RealWorldCompositions.swift`
- New test: `TestFramework/RuntimeTestsApp/Patterns/CompositionTests.cs`

---

## Gap 6: PInvokeEmitter Unit Tests

**Priority**: P3 — Safety net for P/Invoke generation
**Area**: Unit tests
**Risk**: P/Invoke-specific bugs (calling conventions, parameter marshalling) caught only at integration level

### Problem

`PInvokeEmitter.cs` generates all P/Invoke declarations but is tested only indirectly through other emitter tests. Dedicated tests would catch:
- Incorrect `[UnmanagedCallConv]` attributes
- Wrong parameter marshalling for edge cases
- Missing `[DllImport]` attributes or incorrect library names
- Return type marshalling errors

### What "Done" Looks Like

- [ ] `PInvokeEmitterTests.cs` covering:
  - Basic P/Invoke generation for instance/static methods
  - Calling convention attributes (Swift, Cdecl)
  - Parameter marshalling for blittable, string, class, existential types
  - Return type handling (direct, indirect result, void)
  - Async method P/Invoke patterns

### Files

- Source: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- Test: `src/Swift.Bindings/tests/UnitTests/EmitterTests/PInvokeEmitterTests.cs` (to create)

---

## Gap 7: Generic Runtime Tests

**Priority**: P3 — Currently no runtime coverage
**Area**: TestFramework Layer 2
**Risk**: Generic type instantiation bugs caught only by real-world bindings

### Problem

The contract matrix shows all `Generic<T> × {return, param}` cells as `R?`. Generic types work in generated bindings (Nuke, BlinkID use them), but TestFramework has no runtime tests for generic patterns. The Swift test files for generics haven't been created yet.

### What "Done" Looks Like

- [ ] Add `TestFramework/Sources/SwiftBindingsTestLib/Generics/GenericTypes.swift` with:
  - Generic struct `Container<T>`
  - Generic class with methods
  - Bound generic parameters in methods
- [ ] Layer 1: Generic features move from "missing" to "passing"
- [ ] Layer 2: `GenericTests.cs` validates:
  - Instantiation with blittable type parameter
  - Instantiation with class type parameter
  - Method calls on generic types
  - Generic method calls on non-generic types

### Files

- New Swift: `TestFramework/Sources/SwiftBindingsTestLib/Generics/`
- New test: `TestFramework/RuntimeTestsApp/Generics/GenericTests.cs`

---

## Gap 8: Error Handling Tests

**Priority**: P3 — Throwing functions work but aren't tested end-to-end
**Area**: TestFramework (Layer 1 + Layer 2)
**Risk**: Throwing method marshalling regressions

### Problem

Error handling Swift sources (`ThrowingFunctions.swift`, `ErrorTypes.swift`, `TypedThrows.swift`) sit in `.disabled/`. Throwing functions and typed throws work in the generator (used in Nuke/Lottie), but TestFramework doesn't exercise them.

### What "Done" Looks Like

- [ ] Enable `ErrorHandling/ThrowingFunctions.swift` (at minimum)
- [ ] Layer 1: Throwing functions generate clean
- [ ] Layer 2: `ThrowingMethodTests.cs` validates:
  - Successful call (no throw)
  - Call that throws → C# exception caught
  - Custom error type preservation
  - Typed throws (Swift 6.0)

### Files

- Swift: `TestFramework/Sources/SwiftBindingsTestLib/ErrorHandling.disabled/`
- New test: `TestFramework/RuntimeTestsApp/ErrorHandling/ThrowingMethodTests.cs`

---

## Gap 9: Golden API Snapshot Tooling

**Priority**: P4 — Deferred from TestFramework Phase C
**Area**: Infrastructure
**Risk**: Generated API surface can drift without detection

### Problem

No mechanism to detect when a generator change alters the public API surface of generated bindings (method signatures, type names, parameter types). Currently detected only by manual review or downstream compile failures.

### What "Done" Looks Like

- [ ] Script that extracts public API surface from generated `.cs` file
- [ ] Baseline snapshot checked into repo
- [ ] `build-and-test.sh` optionally compares against baseline
- [ ] Clear diff output showing added/removed/changed members

### Consideration

Potentially noisy during active development. May be better gated on releases or opt-in.

---

## Gap 10: CI Integration

**Priority**: P4 — Deferred from TestFramework Phase C
**Area**: Infrastructure

### Problem

All tests run locally. No CI pipeline ensures tests pass before merge. The tiered test system (Tier 1 < 30s, Tier 2 < 3min, Tier 3 < 15min) was designed for CI but not yet integrated.

### What "Done" Looks Like

- [ ] GitHub Actions workflow for macOS runner
- [ ] Tier 1 runs on every PR
- [ ] Tier 2 runs before merge to main
- [ ] Tier 3 runs nightly
- [ ] Real-world bindings (Nuke, Lottie, BlinkID) validated on merge

---

## Contract Matrix Status

Updated snapshot of runtime test coverage. Goal: all `G✓` cells reach `G✓ R✓`.

### Return Types

|                     | Blittable | String  | Array   | Class   | Enum    | Optional | Existential |
|---------------------|:---------:|:-------:|:-------:|:-------:|:-------:|:--------:|:-----------:|
| Sync return         |   G✓ R✓   |  G✓ R✓  |  G✓ R✓  |  G✓ R✓  |  G✓ R✓  |  G✓ R✓   |    G✓ R?    |
| Async return        |   G✓ R?   |  G✓ R◐  |  G✓ R◐  |  G✓ R◐  |  G✓ R◐  |  G? R?   |    G? R?    |
| Generic\<T\> return |   G✓ R?   |  G✓ R?  |  G✓ R?  |  G? R?  |  G? R?  |  G? R?   |      -      |

### Parameter Types

|                     | Blittable | String  | Array   | Class   | Enum    | Optional | Existential |
|---------------------|:---------:|:-------:|:-------:|:-------:|:-------:|:--------:|:-----------:|
| Sync param          |   G✓ R✓   |  G✓ R✓  |  G✓ R✓  |  G✓ R✓  |  G✓ R✓  |  G✓ R✓   |    G✓ R?    |
| Generic\<T\> param  |   G✓ R?   |  G✓ R?  |  G✓ R?  |  G? R?  |  G? R?  |  G? R?   |      -      |

### Protocol Witness Dispatch

|                     | Blittable | String  | Array   | Class   | Enum    |
|---------------------|:---------:|:-------:|:-------:|:-------:|:-------:|
| Property getter     |   G✓ R?   |  G✓ R◐  |  G? R?  |  G? R?  |  G✓ R◐  |
| Property setter     |   G✓ R?   |  G✓ R◐  |  G? R?  |  G? R?  |  G? R?  |
| Method param        |   G✓ R?   |  G✓ R◐  |  G? R?  |  G? R?  |  G? R?  |
| Method return       |   G✓ R?   |  G✓ R◐  |  G? R?  |  G? R?  |  G? R?  |

### Closures

|                     | Blittable | String  | Array   | Class   | Enum    |
|---------------------|:---------:|:-------:|:-------:|:-------:|:-------:|
| Closure param       |   G✓ R✓   |  G✓ R?  |  G? R?  |  G? R?  |  G? R?  |
| Closure return      |   G✓ R✓   |  G✓ R?  |  G? R?  |  G? R?  |  G? R?  |
| @escaping callback  |   G✓ R✓   |  G? R?  |  G? R?  |  G? R?  |  G? R?  |

**Legend**:
- **G✓ R✓** — Generator and runtime tested
- **G✓ R◐** — Generator tested, runtime needs test (known bug area)
- **G✓ R?** — Generator tested, runtime not tested
- **G? R?** — Generator coverage unknown, runtime not tested
- **-** — Not applicable

---

## Known Generator Bugs (Tracked)

These are known limitations found during TestFramework Phase D. They affect test design (tests must work around these):

| Bug | Impact | Workaround |
|-----|--------|------------|
| `UnsafePointer<T>` (immutable) → `AnyType` | Immutable pointer params unresolvable | Use `UnsafeMutablePointer<T>` |
| Named tuples with `String` elements | `(SwiftString.Buffer, ...)` → `(SwiftString, ...)` CS0029 | Avoid String in named tuples |
| Throwing closure thunks | `SwiftString` return emitted as `void*` | Exclude throwing closures from tests |
| `PointerContainer<IntPtr>` | Violates `ISwiftObject` generic constraint (CS0315) | Exclude pointer generics from tests |

---

## Real-World Binding Skip Reasons

Most common reasons members are skipped in Nuke/Lottie/BlinkID. Tests should ensure these don't regress (skipped for valid reasons) and that the skip reason reporting stays accurate.

| Skip Reason | Nuke | Lottie | BlinkID | Test Coverage |
|-------------|------|--------|---------|---------------|
| UnsupportedSignature | 2 | 8 | 2 | Unit tests (MarshalerTests) |
| UnsupportedClosure | 7 | 7 | 0 | Unit tests (ClosureHandlerTests) |
| StaticProtocolMember | 5 | 0 | 2 | No dedicated test |
| UnsupportedExistential | 2 | 6 | 0 | Unit tests (ExistentialHandlerTests) |
| AsyncProperty | 4 | 0 | 0 | No runtime test |
| UnsatisfiedGenericConstraint | 0 | 7 | 0 | Partial (GenericContextTests) |
| SwiftUIConstraint | 0 | 1 | 0 | N/A (by design) |

---

## Summary by Priority

| Priority | Gap | Area | Effort |
|----------|-----|------|--------|
| **P1** | Runtime tests not in default pipeline | Infrastructure | Small — script change |
| **P1** | Generator non-zero exit tolerated | TestFramework L1 | Small — strict mode flag |
| **P1** | Conductor unit tests | Unit tests | Small — one test file |
| **P1** | Coverage report active vs future reporting | TestFramework L1 | Small — script change |
| **P1** | Async runtime tests | TestFramework L2 | Medium — requires enabling async Swift sources |
| **P2** | Protocol witness dispatch runtime tests | TestFramework L2 | Medium — requires protocol Swift source adjustment |
| **P2** | Complex composition tests | TestFramework L1+L2 | Medium — new Swift + C# test files |
| **P3** | PInvokeEmitter unit tests | Unit tests | Small — one test file |
| **P3** | Generic runtime tests | TestFramework L2 | Medium — new Swift + C# test files |
| **P3** | Error handling tests | TestFramework L1+L2 | Medium — enable disabled Swift sources |
| **P4** | Golden API snapshots | Infrastructure | Medium — new tooling |
| **P4** | CI integration | Infrastructure | Large — GitHub Actions setup |
