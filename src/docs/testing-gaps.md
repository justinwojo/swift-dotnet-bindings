# Testing Gaps

**Date**: February 2026
**Context**: TestFramework Phases A-D complete, 25 real-world libraries binding clean (0 generator errors)

This document tracks known testing gaps to be addressed incrementally. Each gap includes priority, rationale, and what "done" looks like.

---

## Current Test Infrastructure

| Layer | What It Tests | Count | Location |
|-------|---------------|-------|----------|
| **Unit Tests** | Component logic (marshaler, emitter, parser) | 2,443 tests | `src/Swift.Bindings/tests/UnitTests/` |
| **Runtime Library Tests** | SwiftArray, SwiftString, metadata | 133 tests (1 skipped) | `src/Swift.Runtime/tests/` |
| **Integration Tests** | Full Swift↔.NET interop | 699 tests (11 skipped) | `src/Swift.Bindings/tests/IntegrationTests/` |
| **TestFramework Layer 1** | Generator correctness across feature matrix | 94/94 must-pass features | `TestFramework/` |
| **TestFramework Layer 2** | Runtime behavior on iOS simulator | 185 tests at Tier 2¹ | `TestFramework/RuntimeTestsApp/` |
| **Real-World Bindings** | End-to-end against shipping libraries | 25 clean + 5 env-only (see `binding-errors.md`) | `BindingTesting/` + `/Users/wojo/Dev/Libraries/` |

¹ 185 is the last confirmed passing count. RuntimeTestsApp compiles cleanly (only 2 pre-existing CS0103 errors in generated bindings for async property getters — not test code).

---

## Gap 0a: Runtime Tests Not in Default Pipeline — DONE (Phase A1)

**Priority**: P1 — Layer 2 regressions silently skipped
**Area**: Infrastructure
**Status**: Complete — `run-tests.sh` now calls `run-runtime-tests.sh --tier 2 --skip-regen --timeout 90` after coverage report, gated on macOS + simulator availability.

- [x] `run-tests.sh` calls `run-runtime-tests.sh` after the coverage report
- [x] Non-zero exit from runtime tests fails the overall `run-tests.sh`
- [x] Crash detection: allowlist-based — crashes tolerated only in `[CrashRisk]` classes (`EnumMarshallingTests`, `OwnershipGCStressTests`); crashes in other classes fail the gate

---

## Gap 0b: Generator Non-Zero Exit Tolerated — DONE (Phase A2)

**Priority**: P1 — Silent degradation of core binding flow
**Area**: TestFramework Layer 1
**Status**: Complete — `--strict` flag added to `regenerate-bindings.sh`, `run-tests.sh` uses strict mode and fails on degraded must-pass features > 0.

- [x] `--strict` flag added to `regenerate-bindings.sh` (fails on non-zero generator exit)
- [x] `build-and-test.sh` passes `--strict` through
- [x] `run-tests.sh` fails (not warns) when degraded must-pass features > 0

---

## Gap 0c: Test Pipeline Hardening — DONE (TH-1 through TH-7)

**Priority**: P1 — Prevents false greens and silent drift
**Area**: Infrastructure
**Status**: Complete — compile gate, baseline budget, crash allowlist, profile docs, simulator flake reduction.

- [x] **Compile gate** (TH-1): `CompileCheck/CompileCheck.csproj` in `build-and-test.sh` Step 2.5. Catches invalid C# in seconds. Infrastructure errors (NU/NETSDK/MSB) always fail; 2 known async property CS0103 errors filtered.
- [x] **Baseline budget** (TH-2/3/4): `baselines.json` + `check-baselines.sh` tracks generator exit code, degraded count, compiled-out count, known-unsupported total, crash-risk classes, wrapper strip count. Called from `run-tests.sh`.
- [x] **Crash allowlist** (TH-5): `run-tests.sh` extracts last test class from `=== ClassName ===` markers. Only `EnumMarshallingTests|OwnershipGCStressTests` crashes tolerated; new crash-risk classes fail the gate.
- [x] **Profile docs** (TH-6): `TestFramework/README.md` documents PR Gate and Nightly profiles.
- [x] **Simulator flake** (TH-7): Default timeout 60→90s, deterministic simulator selection (iPhone 16 > 15 Pro > 15 > any).
- [ ] **Semantic verification depth** (TH-8): Partially addressed — Gap 6 `EmitPInvoke` tests verify emitted DllImport/entry point semantics; remaining compile-based assertions deferred.

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

## Gap 3: Async Runtime Tests — Tests Implemented (Blocked at Tier 3)

**Priority**: P1 — Biggest regression risk area
**Area**: TestFramework Layer 2
**Status**: Tests implemented. All 3 test classes complete with full coverage. Blocked at runtime by Mono JIT assertion (jit-info.c:918) — all tests are Tier 3.

### Test Classes

| File | Class | Tests | Tier |
|------|-------|-------|------|
| `RuntimeTestsApp/Async/AsyncStringTests.cs` | `AsyncStringTests` | 11 | All Tier 3 |
| `RuntimeTestsApp/Async/AsyncComplexTypeTests.cs` | `AsyncComplexTypeTests` | 8 | All Tier 3 |
| `RuntimeTestsApp/Async/AsyncMethodTests.cs` | `AsyncMethodTests` | 13 | All Tier 3 |

### Checklist

- [x] Move core async Swift sources out of `.disabled/` (Methods.swift, AsyncComplexTypes.swift active)
- [x] Layer 1 generation succeeds for async methods
- [x] `AsyncStringTests.cs` — UTF-8 round-trip through async boundary (11 tests)
- [x] `AsyncComplexTypeTests.cs` — Class/Enum/Array async returns (8 tests)
- [x] `AsyncMethodTests.cs` — void, blittable return, string, static, parameterized async (13 tests)

### Runtime Blocker

All 32 async tests are Tier 3 due to Mono JIT assertion on `CallConvSwift` in async P/Invoke paths. Tests are complete and ready for when the Mono blocker is resolved. Contract matrix cells `Async × {String, Array, Class, Enum}` remain at `R◐` (tests exist, runtime blocked).

### Files

- Swift sources: `TestFramework/Sources/SwiftBindingsTestLib/Async/` (active)
- Runtime tests: `TestFramework/RuntimeTestsApp/Async/`

---

## Gap 4: Protocol Witness Dispatch Runtime Tests — DONE (Interface Projection Path)

**Priority**: P2 — Critical for Nuke/BlinkID patterns
**Area**: TestFramework Layer 2
**Status**: Complete — `BasicProtocolDispatchTests` with 33 tests (14 Tier 1, 9 Tier 2, 10 Tier 3).

### Test Class

| File | Class | Tests | Tier |
|------|-------|-------|------|
| `RuntimeTestsApp/Protocols/WitnessDispatchTests.cs` | `BasicProtocolDispatchTests` | 14 | Tier 1 |
| | | 9 | Tier 2 |
| | | 10 | Tier 3 |

### Checklist

- [x] Protocol interfaces appear in TestFramework generated bindings (20 interfaces + proxy classes)
- [x] Protocol conformance dispatch (concrete type → C# interface → concrete P/Invoke)
- [x] Blittable property getter/setter dispatch (`TestHasValueGet`, `TestHasValueSet`, `TestPersonAge`)
- [x] Blittable method dispatch with params/returns (4 arithmetic tests + `SetValue`/`GetValue`)
- [x] String method dispatch (`EchoProcessor.Process`, `Describe`, `Display`, `GetOutput`)
- [x] Enum method/property dispatch (`HandleStatus`, `TransitionStatus`, `GetCurrentStatus`)
- [x] Contract matrix "Protocol Interface Dispatch" cells updated

### Scope Note

Tests exercise the **interface projection path**: concrete Swift types conforming to protocols are cast to C# protocol interfaces, with dispatch routing through concrete P/Invoke entry points. This is the primary real-world usage pattern (Nuke, BlinkID, Lottie). The **proxy-based witness dispatch path** (existential container → proxy class → witness table thunks) remains untested — it requires the SwiftBindings wrapper library bundled in RuntimeTestsApp, which is a separate infrastructure concern.

### Tier 3 Blockers

- SwiftString property getter/setter: Mono JIT assertion on `CallConvSwift` (same as Gap 3)
- TaskPriority (String raw value enum): routes through wrapper lib, not available at runtime

### Files

- Swift: `TestFramework/Sources/SwiftBindingsTestLib/Protocols/` (`BasicProtocols.swift`, `Composition.swift`, `Conformance.swift`, `NonBlittableProtocols.swift`)
- Runtime tests: `TestFramework/RuntimeTestsApp/Protocols/WitnessDispatchTests.cs`

---

## Gap 5: Complex Type Composition Tests — DONE

**Priority**: P2 — Covers real-world patterns Nuke/Lottie exercise
**Area**: TestFramework (Layer 1 + Layer 2)
**Status**: Complete — `BasicCompositionTests` with 23 tests (4 Tier 1, 2 Tier 2, 17 Tier 3).

### Checklist

- [x] `TestFramework/Sources/SwiftBindingsTestLib/Patterns/RealWorldCompositions.swift` with class+closure, struct+optional-array, singleton+async, inheritance+protocol patterns
- [x] Layer 1: All compositions generate clean bindings
- [x] Layer 2: `BasicCompositionTests` — 6 tests passing at Tier 1-2, 17 at Tier 3 (Mono JIT blockers)

### Files

- Swift: `TestFramework/Sources/SwiftBindingsTestLib/Patterns/RealWorldCompositions.swift`
- Runtime test: `TestFramework/RuntimeTestsApp/Patterns/CompositionTests.cs`

---

## Gap 6: PInvokeEmitter Unit Tests — DONE

**Priority**: P3 — Safety net for P/Invoke generation
**Area**: Unit tests
**Status**: Complete — `PInvokeEmitterTests.cs` with 48 tests covering all major P/Invoke paths.

### Test Regions

| Region | Tests | Coverage |
|--------|-------|----------|
| Return Type Handling | 11 | Primitive, SwiftString, closure, existential, bound generic, ObjC-bridged, class, non-frozen indirect, frozen struct, enum |
| Parameter Marshalling | 11 | Bound generic, closure (legacy + Cdecl), non-frozen (sync + async), frozen struct, inout, ObjC-bridged, enum, tuple |
| Self Parameter | 6 | Static (no self), class instance, frozen struct getter/setter, memory-managed getter, free function wrapper |
| Library Selection + Entry Point | 6 | Tj suffix (non-final class, final class, final member, wrapper lib skip), library path (module vs async), async/wrapper lib routing |
| Async/Error Parameters | 4 | Async callbacks, SwiftError, async+throwing, neither |
| Constructors | 2 | No self, no async |
| Signature Strings | 3 | PInvokeParametersString, CallArgumentsString patterns |

### Checklist

- [x] `PInvokeEmitterTests.cs` with 48 tests via `SignatureHandler.GetPInvokeSignature()` + `PInvokeEmitter.EmitPInvoke()` entry points
- [x] Return types: primitive mapping, SwiftString buffer, closure data, existential, bound generic (Array/Optional), ObjC-bridged IntPtr, class IntPtr, indirect result, frozen struct direct, simple enum underlying type
- [x] Parameters: bound generic buffer, closure legacy + Cdecl wrapper (FuncPtr + Context), non-frozen SafeHandle vs async IntPtr, frozen struct (with/without mem mgmt), inout ref modifier, ObjC-bridged, enum SafeHandle + simple enum, tuple
- [x] Self: static methods, class SwiftSelf, frozen struct SwiftSelf<T>/SwiftSelf<Buffer>, setter pointer, free function wrapper IntPtr
- [x] Library: Tj suffix (non-final, final class, final member, wrapper skip), module vs async library path
- [x] Async/error: callback/error callback/task params, SwiftError out param, combined async+throwing, clean non-async/non-throwing
- [x] All 2,443 unit tests passing (48 new + 2,395 existing)

### Files

- Source: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- Test: `src/Swift.Bindings/tests/UnitTests/EmitterTests/PInvokeEmitterTests.cs`

---

## Gap 7: Generic Runtime Tests — DONE (Tests Written, Tier 3 Pending Confirmation)

**Priority**: P3 — Previously no runtime coverage for unbound generics or generic free functions
**Area**: TestFramework Layer 2
**Status**: Tests written. 20 existing bound-specialization tests (passing Tier 1-2) + 10 new unbound generic / generic free function tests (Tier 3 — expected Mono JIT assertion on TypeMetadata via CallConvSwift). RuntimeTestsApp compiles cleanly; Tier 3 execution pending simulator run.

### Test Summary

| Category | Tests | Tier | Status |
|----------|-------|------|--------|
| BoundIntPair (frozen struct) | 4 | Tier 1 | Passing |
| SummableInt32 (protocol conformance) | 4 | Tier 1 | Passing |
| MutableItem (non-frozen struct) | 4 | Tier 1 | Passing |
| BoundStringPair (string methods) | 2 | Tier 2 | Passing |
| SimpleItem (protocol + string) | 1 | Tier 2 | Passing |
| DisplayItem (protocol inheritance) | 2 | Tier 2 | Passing |
| IntContainer (associated types) | 3 | Tier 3 | Array marshalling blocker |
| **Wrapper\<T\> (unbound generic struct)** | **2** | **Tier 3** | **NEW — TypeMetadata + CallConvSwift** |
| **GenericPair\<T0,T1\> (two type params)** | **2** | **Tier 3** | **NEW — TypeMetadata + CallConvSwift** |
| **GenericClass\<T\> (generic class)** | **3** | **Tier 3** | **NEW — TypeMetadata + CallConvSwift, includes setter** |
| **GetIdentity\<T\> (generic free function)** | **2** | **Tier 3** | **NEW — round-trip identity** |
| **GetPair\<T0,T1\> (generic free function)** | **1** | **Tier 3** | **NEW — tuple return** |
| **Total** | **30** | — | **20 existing + 10 new** |

### Constrained Generic Functions — Deferred

`SumTwo<T0>` (requires `ISummable`), `GetDescribeConstrained<T0>` (requires `IDescribable` + `ITestIdentifiable`) have generated proxy types (`SummableProxy`, `DescribableProxy`, `TestIdentifiableProxy`) that satisfy both `ISwiftObject` and the protocol interface constraints. However, proxy types route through witness table dispatch which requires the SwiftBindings wrapper library bundled into RuntimeTestsApp — the same infrastructure blocker as proxy-based witness dispatch (Gap 4 scope note). Tests for these functions are deferred until that infrastructure is in place. Only `GetIdentity` and `GetPair` (requiring only `ISwiftObject`) are tested currently.

### Checklist

- [x] Swift sources: 4 active files in `Generics/` (Types.swift, Functions.swift, Constraints.swift, Existentials.swift)
- [x] Layer 1: Generated bindings include `Wrapper<T0>`, `GenericPair<T0,T1>`, `GenericClass<T0>`, `GetIdentity<T0>`, `GetPair<T0,T1>`
- [x] Layer 2: 20 existing tests covering bound specializations (Tier 1-2)
- [x] Layer 2: 10 new tests covering unbound generics + generic free functions (Tier 3)
- [ ] Tier 3 execution: Deferred — requires Mono JIT fix for CallConvSwift TypeMetadata

### Files

- Swift: `TestFramework/Sources/SwiftBindingsTestLib/Generics/` (4 files, active)
- Runtime tests: `TestFramework/RuntimeTestsApp/Generics/BasicGenericTests.cs`

---

## Gap 8: Error Handling Tests — DONE

**Priority**: P3 — Throwing functions work but aren't tested end-to-end
**Area**: TestFramework (Layer 1 + Layer 2)
**Status**: Complete — `BasicThrowingTests` with 34 tests (17 Tier 1, 7 Tier 2, 10 Tier 3).

### Checklist

- [x] Enable `ErrorHandling/ThrowingFunctions.swift` (active, not in `.disabled/`)
- [x] Layer 1: Throwing functions generate clean
- [x] Layer 2: `BasicThrowingTests` validates:
  - Successful call (no throw) — Tier 1
  - Call that throws → C# exception caught — Tier 1-2
  - Custom error type preservation — Tier 2
  - Typed throws (Swift 6.0) — Tier 3 (Mono JIT blocker)
- [x] 24 tests passing at Tier 1-2, 10 at Tier 3 with documented blockers

### Files

- Swift: `TestFramework/Sources/SwiftBindingsTestLib/ErrorHandling/`
- Runtime test: `TestFramework/RuntimeTestsApp/ErrorHandling/ThrowingMethodTests.cs`

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
| Generic\<T\> return |   G✓ R◐   |  G✓ R?  |  G✓ R?  |  G? R?  |  G? R?  |  G? R?   |      -      |

### Parameter Types

|                     | Blittable | String  | Array   | Class   | Enum    | Optional | Existential |
|---------------------|:---------:|:-------:|:-------:|:-------:|:-------:|:--------:|:-----------:|
| Sync param          |   G✓ R✓   |  G✓ R✓  |  G✓ R✓  |  G✓ R✓  |  G✓ R✓  |  G✓ R✓   |    G✓ R?    |
| Generic\<T\> param  |   G✓ R◐   |  G✓ R?  |  G✓ R?  |  G? R?  |  G? R?  |  G? R?   |      -      |

### Protocol Interface Dispatch¹

|                     | Blittable | String  | Array   | Class   | Enum    |
|---------------------|:---------:|:-------:|:-------:|:-------:|:-------:|
| Property getter     |   G✓ R✓   |  G✓ R◐  |  G? R?  |  G? R?  |  G✓ R◐  |
| Property setter     |   G✓ R✓   |  G✓ R◐  |  G? R?  |  G? R?  |  G✓ R◐  |
| Method param        |   G✓ R✓   |  G✓ R✓  |  G? R?  |  G? R?  |  G✓ R✓  |
| Method return       |   G✓ R✓   |  G✓ R✓  |  G? R?  |  G? R?  |  G✓ R✓  |

¹ Tests cover the interface projection path (concrete type cast to C# protocol interface). Proxy-based existential witness dispatch is not yet tested.

### Closures

|                     | Blittable | String  | Array   | Class   | Enum    |
|---------------------|:---------:|:-------:|:-------:|:-------:|:-------:|
| Closure param       |   G✓ R✓   |  G✓ R?  |  G? R?  |  G? R?  |  G? R?  |
| Closure return      |   G✓ R✓   |  G✓ R?  |  G? R?  |  G? R?  |  G? R?  |
| @escaping callback  |   G✓ R✓   |  G? R?  |  G? R?  |  G? R?  |  G? R?  |

**Legend**:
- **G✓ R✓** — Generator and runtime tested
- **G✓ R◐** — Generator tested, runtime test exists but blocked (Mono JIT or wrapper lib dependency)
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
| **P1** | ~~Runtime tests not in default pipeline~~ | Infrastructure | **Done** (Phase A1) |
| **P1** | ~~Generator non-zero exit tolerated~~ | TestFramework L1 | **Done** (Phase A2) |
| **P1** | ~~Test pipeline hardening (TH-1–7)~~ | Infrastructure | **Done** (compile gate, baselines, allowlist, docs, flake) |
| **P1** | ~~Conductor unit tests~~ | Unit tests | **Done** (Phase A3) |
| **P1** | ~~Coverage report active vs future~~ | TestFramework L1 | **Done** (Phase A4) |
| **P1** | ~~Async runtime tests~~ | TestFramework L2 | **Tests Implemented** — blocked at Tier 3 (Mono JIT) |
| **P2** | ~~Protocol witness dispatch runtime tests~~ | TestFramework L2 | **Done** (33 tests: 23 passing Tier 1-2, 10 Tier 3) |
| **P2** | ~~Complex composition tests~~ | TestFramework L1+L2 | **Done** (23 tests, 6 passing Tier 1-2) |
| **P3** | ~~PInvokeEmitter unit tests~~ | Unit tests | **Done** (48 tests, all passing) |
| **P3** | ~~Generic runtime tests~~ | TestFramework L2 | **Done** (30 tests: 12 Tier 1, 5 Tier 2, 13 Tier 3 pending) |
| **P3** | ~~Error handling tests~~ | TestFramework L1+L2 | **Done** (34 tests, 24 passing Tier 1-2) |
| **P4** | Golden API snapshots | Infrastructure | Medium — new tooling |
| **P4** | CI integration | Infrastructure | Large — GitHub Actions setup |
