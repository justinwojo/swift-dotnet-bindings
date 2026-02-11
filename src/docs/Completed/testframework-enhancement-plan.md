# TestFramework Enhancement Plan

**Status**: Phase A Complete, Phase B Complete, Phase C Complete, Phase D Complete
**Date**: February 2026
**Context**: Phases 55-61 fixed bugs that TestFramework didn't catch

---

## Problem Statement

TestFramework validates **code generation** but doesn't exercise **runtime behavior**. This gap allowed 6 of 7 recent phases (55, 58, 59, 60, 61) to fix bugs that slipped through testing.

| What TestFramework Does | What It Doesn't Do |
|------------------------|-------------------|
| ✅ Verifies Swift types parse correctly | ❌ Call generated C# methods |
| ✅ Confirms bindings emit without errors | ❌ Validate marshalling round-trips |
| ✅ Tracks coverage statistics | ❌ Test callback/async patterns |
| ✅ Detects skip reasons | ❌ Exercise witness dispatch |

---

## Two-Layer Test Model

Adopt explicit separation to make failures actionable:

### Layer 1: Generator/Coverage Tests (Existing)

- **Purpose**: Verify Swift → C# binding generation
- **Location**: `TestFramework/` with `build-and-test.sh`, `generate-coverage-report.sh`
- **Failure means**: Generator bug (parser, marshaler, emitter)
- **Output**: `binding-report.json`, `coverage-matrix.json`

### Layer 2: Runtime ABI/Marshalling Tests (New)

- **Purpose**: Verify generated bindings work at runtime
- **Location**: `TestFramework/RuntimeTests/` (new C# test project)
- **Failure means**: Interop bug (marshalling, memory, ABI mismatch)
- **Output**: Standard test results (pass/fail with assertions)

This separation answers: "Did we generate the right code?" vs "Does the code actually work?"

---

## Contract Matrix

Systematic coverage across dimensions. Each cell shows two-layer status: **G** (Generator/Layer 1) and **R** (Runtime/Layer 2).

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
- **G** = Generator (Layer 1): ✓ covered, ? unknown/gap
- **R** = Runtime (Layer 2): ✓ tested, ◐ needs test (Phase 55-61 bug), ? not tested
- `-` = Not applicable / intentionally unsupported

**Goal**: All applicable `G✓` cells should reach `G✓ R✓` status. Cells marked `-` are intentionally unsupported.

---

## Gaps Identified from Phases 55-61

| Phase | Issue Fixed | Matrix Cell | Test Needed |
|-------|-------------|-------------|-------------|
| 55 | String enum `FromRawValue()` | Enum × Sync | Nested String enums with factory round-trip |
| 58 | Async String return | String × Async | UTF-8 marshalling validation |
| 59 | Async Array\<String\> return | Array × Async | Buffer serialization round-trip |
| 60 | Async complex type return | Class/Enum × Async | OpaquePointer marshalling |
| 61 | `IntPtr<T>` generic emission | Generic × Pointer | `Container<UnsafePointer<T>>` patterns |
| 56 | Protocol conformance validation | Protocol × Various | Witness dispatch with non-blittable types |

---

## Ownership/Lifetime Tests

Common blind spot in interop. Add from day one.

### Retain/Release Balance

```csharp
[Test] void SwiftObject_Dispose_ReleasesOnce();
[Test] void SwiftObject_DoubleDispose_NoDoubleFree();
[Test] void SwiftObject_GCCollect_EventuallyReleases();
[Test] void SwiftObject_AccessAfterDispose_Throws();
```

### Callback Lifetime

```csharp
[Test] void EscapingClosure_SurvivesGCPressure();
[Test] void EscapingClosure_CalledAfterCreatorDisposed();
[Test] void ConventionC_Callback_ValidDuringCall();
```

### Async Lifetime

```csharp
[Test] void AsyncResult_ValidAfterTaskCompletes();
[Test] void AsyncCallback_NoUseAfterFree();
[Test] void AsyncString_ValidAfterBufferFreed();
```

### Negative-Path Tests

Explicitly test error conditions and invalid states:

```csharp
// Invalid handles
[Test] void InvalidHandle_MethodCall_Throws();
[Test] void NullHandle_MethodCall_Throws();

// Disposed object access
[Test] void DisposedObject_PropertyAccess_Throws();
[Test] void DisposedObject_MethodCall_Throws();

// Async cancellation/timeout
[Test] void AsyncMethod_Timeout_HandledGracefully();
[Test] void AsyncMethod_CancellationToken_Respected();

// Callback exceptions
[Test] void Callback_ThrowsException_PropagatesOrHandled();
[Test] void EscapingClosure_ThrowsException_NoCorruption();

// Invalid enum/raw values
[Test] void EnumFromRawValue_InvalidValue_ReturnsNil();
[Test] void StringEnum_EmptyString_HandledCorrectly();
```

### Concurrency and Stress Tests

Flush race conditions and use-after-free issues:

```csharp
// Parallel calls
[Test] void ParallelMethodCalls_NoDataCorruption();
[Test] void ParallelPropertyAccess_ThreadSafe();

// Repeated async completion
[Test] void RepeatedAsyncCalls_NoResourceLeak();
[Test] void AsyncCompletionRace_NoDeadlock();

// GC pressure
[Test] void GCPressureLoop_CallbacksSurvive();
[Test] void GCPressureLoop_NoUseAfterFree();
[Test] void RapidAllocDealloc_NoMemoryCorruption();
```

---

## New Swift Test Files Needed

```
TestFramework/Sources/SwiftBindingsTestLib/
├── Enums/
│   └── StringEnumRawValues.swift      # Nested String enums, collision testing
├── Async/
│   └── AsyncComplexTypes.swift        # Async returning enum/struct/class
├── Generics/
│   └── PointerGenerics.swift          # Container<UnsafePointer<T>> patterns
├── Protocols/
│   └── NonBlittableProtocols.swift    # String/enum properties via witness
└── Lifetime/
    └── OwnershipTests.swift           # Explicit retain/release scenarios
```

---

## New C# Runtime Test Project

```
TestFramework/
├── RuntimeTestsApp/                    # iOS simulator app (like LottieTestApp pattern)
│   ├── RuntimeTestsApp.csproj          # References generated bindings + xcframework
│   ├── Program.cs                      # App entry, discovery-based test runner
│   ├── Infrastructure/
│   │   ├── TestBase.cs                 # Common setup, GC/timeout/assertion helpers
│   │   ├── TestResults.cs              # Result tracking, tier enum, TestTierAttribute
│   │   ├── TestLogger.cs               # Console/UI logging
│   │   └── LifetimeTracker.cs          # Ref count assertions
│   ├── Marshalling/
│   │   ├── BlittableRoundTripTests.cs  ✓
│   │   ├── StringMarshallingTests.cs   ✓
│   │   ├── EnumMarshallingTests.cs     ✓
│   │   ├── ClassMarshallingTests.cs    ✓
│   │   ├── ArrayMarshallingTests.cs    ✓ (11 tests)
│   │   ├── OptionalMarshallingTests.cs ✓ (8 tests)
│   │   ├── TupleMarshallingTests.cs    ✓ (9 tests)
│   │   └── PointerMarshallingTests.cs  ✓ (9 tests)
│   ├── Lifetime/
│   │   ├── OwnershipTests.cs           ✓ (28 tests)
│   │   └── NegativePathTests.cs        ✓ (21 tests)
│   ├── Concurrency/
│   │   └── StressTests.cs             ✓ (12 tests)
│   ├── Async/
│   │   ├── AsyncStringTests.cs         (stub, deferred)
│   │   └── AsyncComplexTypeTests.cs    (stub, deferred)
│   ├── Protocols/
│   │   └── WitnessDispatchTests.cs     (stub, deferred — no protocol interfaces in bindings)
│   ├── Operators/
│   │   └── OperatorTests.cs            ✓ (13 tests)
│   ├── Closures/
│   │   └── ClosureTests.cs             ✓ (14 tests)
│   └── Generics/                       (planned)
└── run-runtime-tests.sh                # Build + run Layer 2 tests (--tier, --skip-regen, --timeout)
```

---

## Test Design Principles

### Deterministic and Self-Contained

- Fixed test data, no wall-clock assumptions
- Explicit timeout policy for async (e.g., 5 second max)
- No dependency on external frameworks/assets
- Each test independently runnable

### Flake Prevention Policy

Tests involving timing, GC, or concurrency are inherently flake-prone. Mitigations:

| Risk | Mitigation |
|------|------------|
| GC timing | Use `GC.Collect()` + `GC.WaitForPendingFinalizers()` explicitly, don't rely on implicit collection |
| Async timeout | Fixed 5-second timeout per async operation; test fails deterministically on timeout |
| Thread races | Use `ManualResetEvent` / `TaskCompletionSource` for synchronization, not `Thread.Sleep` |
| Callback ordering | Assert on completion, not on timing; use counters not timestamps |

**Flake detection**: Tier 3 runs each test 3 times. Any test that passes inconsistently (1-2 of 3) fails the entire suite and must be fixed or quarantined.

### Round-Trip Validation

```csharp
// Pattern: C# → Swift → C# with value preservation
var input = "test string with unicode: 日本語";
var result = SwiftStringWorker.Echo(input);
Assert.AreEqual(input, result);
```

### Edge Cases Per Type

| Type | Edge Cases to Test |
|------|-------------------|
| String | Empty, null, unicode, very long (>64KB), embedded nulls |
| Array | Empty, single element, large (>1000), nested arrays |
| Enum | All cases, associated values, raw value round-trip |
| Optional | Some, None, nested optionals |
| Class | Null handle, disposed handle, concurrent access |

---

## Implementation Phases

### Phase A: Swift Test Patterns ✓

- [x] Add `StringEnumRawValues.swift` with nested enums (includes name collision test)
- [x] Add `AsyncComplexTypes.swift` with enum/struct/class returns
- [x] Add `PointerGenerics.swift` with `Container<UnsafePointer<T>>`
- [x] Add `NonBlittableProtocols.swift` with String/enum witness (includes existential overloads)
- [x] Add `OwnershipTests.swift` with explicit lifetime scenarios
- [x] Regenerate bindings, verify Layer 1 passes (93/93 must-pass, 0 degraded)

### Phase B: C# Runtime Test Project (In Progress)

**Scope**: Runtime tests cover cells where `G✓` (generator coverage confirmed). Cells marked `G?` require Layer 1 investigation first. Cells marked `-` are intentionally excluded.

- [x] Create `RuntimeTestsApp.csproj` (iOS simulator app pattern, like LottieTestApp)
- [x] Add test infrastructure (TestBase, TestLogger, TestResults, LifetimeTracker)
- [x] Add discovery-based test runner (auto-discovers all `TestBase` subclasses via reflection)
- [x] Wire `--tier` CLI argument from `run-runtime-tests.sh` through to app execution
- [x] Support class-level `[TestTier]` attribute as fallback when method-level is absent
- [x] Implement `BlittableRoundTripTests` (Tier 1 smoke tests for Int32, Bool, Double, Float)
- [x] Create `run-runtime-tests.sh` script with tier selection
- [x] Implement `StringMarshallingTests` (20 tests: ASCII/unicode/emoji round-trips, string enum raw values, edge cases, >64KB stress)
- [x] Implement `EnumMarshallingTests` (17 tests: Direction, Color, StatusCode, Shape associated values, nested container enums, NetworkConfig)
- [x] Implement `ClassMarshallingTests` (15 tests: Animal, UniqueResource, MutableProps, StaticMethods, SafeHandle use-after-dispose, GC pressure)
- [x] Create async test stubs (AsyncStringTests, AsyncComplexTypeTests) — DEFERRED: async Swift sources in `.disabled/`, no async methods in bindings
- [x] Fix test expectation for multiple generic type parameter constraints
  - The generator code was already correct (`GenericTypeEmitter.GetWhereClause` at line 102)
  - The unit test asserted wrong expected value: `"where T0 : X, T1 : X"` → `"where T0 : X where T1 : X"`
- [x] Implement protocol witness dispatch tests — DEFERRED: no protocol interfaces in generated bindings; stub created with requirements documented
- [x] Implement lifetime/ownership tests (28 tests: retain/release balance, double-dispose safety, access-after-dispose for property get/set/method, shared-reference invalidation, independent references, GC stress)
- [x] Implement negative-path tests (21 tests: invalid enum FromRawValue, equality throws for non-Equatable types, disposed object edge cases, zero/invalid handle access, validate round-trip functions)
- [x] Implement concurrency/stress tests (12 tests: parallel method calls, parallel property reads, parallel object creation, rapid alloc/dealloc, GC pressure during active calls, mixed operations)

### Phase C: Integration

- [x] Create `run-runtime-tests.sh` script with tier selection (`--tier 1|2|3`)
  - **Must regenerate bindings first** (or fail if `--skip-regen` passed and bindings older than Swift sources)
  - Script flow: `build-and-test.sh` → compile RuntimeTestsApp → run selected tier
  - Tier argument passed through to app via `--tier N` CLI arg (app reads on startup)
- [x] Assign tests to tiers with `[TestTier(TestTier.TierN)]` attributes (method- and class-level)
- [x] Discovery-based test execution (new `TestBase` subclasses auto-discovered, no manual wiring)
- [x] Document Layer 1 vs Layer 2 in TestFramework README
- [x] Document toolchain requirements (Xcode, Swift, .NET versions)
- [x] Add flake detection: Tier 3 runs each test 3x, any inconsistency fails the suite
- [x] Add to `remaining-work.md` verification steps
- [ ] Consider golden API snapshot tooling (deferred)

### Phase D: Real-World Pattern Coverage ✓

Closed the gap between TestFramework and real-world binding patterns (BlinkID/Nuke/Lottie).

**Layer 1 (Swift Sources + Coverage):**
- [x] Re-enable Operators/ (4 files: Arithmetic, Bitwise, Comparison, Unary)
- [x] Re-enable Tuples/ (3 files: BasicTuples, Named, TupleReturns)
- [x] Re-enable Closures/ (3 files: ConventionC, Escaping, ClosureReturns; Autoclosures excluded)
- [x] Re-enable UnsafeTypes/ (3 files: Pointers, RawPointers, OpaquePointer; Span + PointerGenerics excluded)
- [x] Fix `getStaticBuffer()` pointer lifetime bug (dangling pointer from `withUnsafeBufferPointer`)
- [x] Add `Collections/ArrayOperations.swift` (array param/return/round-trip/class-element)
- [x] Add `Optionals/OptionalTypes.swift` (optional blittable/class return, optional param, struct with optional fields)
- [x] Update `build-xcframework.sh` to match Package.swift exclusions
- [x] Update `generate-coverage-report.sh` with FEATURE_MAP and FEATURE_DECLARATIONS for new features
- [x] Fix `UnsafePointer<T>` → use `UnsafeMutablePointer<T>` (immutable pointer maps to AnyType — generator bug)
- [x] Remove `makeNamedMixed()` (named tuples with String cause CS0029)
- [x] Remove throwing closure functions (thunk return type mismatch)
- [x] Fix RuntimeTestsApp.csproj: `IncludeSwiftBindingsRuntimeNative=false` (InstallNameTool workaround)
- [x] Verify: 99 must-pass features, 44 passing, 0 degraded, 0 regressions

**Layer 2 (Runtime Tests):**
- [x] `ArrayMarshallingTests.cs` (11 tests): create, count, sum, reverse, filter, empty, class arrays
- [x] `OptionalMarshallingTests.cs` (8 tests): Some/None blittable, Some/None class, optional param, struct optional fields
- [x] `TupleMarshallingTests.cs` (9 tests): 2-tuple, 3-tuple, 7-tuple, named tuples, mixed types, divmod, struct methods
- [x] `PointerMarshallingTests.cs` (9 tests): read/write, IntPtr param/return, opaque/raw pointer, fill buffer
- [x] `OperatorTests.cs` (13 tests): arithmetic (+,-,*,/,%), comparison (==,!=,<,>), bitwise (&,|,^), unary (!,~)
- [x] `ClosureTests.cs` (14 tests): @convention(c), @escaping, closure returns, struct closure methods, void/bool/multi-arg callbacks
- [x] RuntimeTestsApp compiles and produces iOS simulator app bundle

**Known generator limitations found (tracked as known-unsupported):**
- `UnsafePointer<T>` (immutable) maps to `AnyType` instead of `IntPtr`
- Named tuples with `String` elements: `(SwiftString.Buffer, ...)` cannot convert to `(SwiftString, ...)`
- Throwing closure thunks: `SwiftString` return emitted as `void*`
- `PointerContainer<IntPtr>` violates `ISwiftObject` generic constraint (CS0315)

**Coverage delta:**
- Must-pass features: 93 → 99 (+6 new features, +4 moved to known-unsupported)
- Layer 1 passing: 44/99 (operators, tuples, closures, pointers, arrays, optionals)
- Layer 2 runtime tests: +64 new tests across 6 test files
- Contract matrix: Array sync R? → R✓, Optional sync R? → R✓, Closure R? → R✓

---

## Test Tiers

Define tiers to balance signal quality with execution time:

### Tier 1: PR Gate (Fast Smoke)

- **Runtime budget**: < 30 seconds
- **Scope**: Core marshalling round-trips, one test per type category
- **Purpose**: Fast feedback on every change, catches obvious regressions
- **Runs**: Every PR, every commit

Tests included:
- Blittable sync round-trip
- String sync round-trip
- Async String return
- One protocol witness dispatch
- Basic retain/release balance

### Tier 2: Merge Gate (Standard)

- **Runtime budget**: < 3 minutes
- **Scope**: Full matrix coverage minus stress tests
- **Purpose**: Comprehensive validation before merge
- **Runs**: Before merge to main

Tests included:
- All Tier 1 tests
- Full async type matrix (String, Array, Class, Enum)
- All protocol witness dispatch variants
- Negative-path tests
- Closure tests

### Tier 3: Nightly (Full Matrix + Stress)

- **Runtime budget**: < 15 minutes
- **Scope**: Everything including concurrency and stress tests
- **Purpose**: Catch subtle race conditions and resource leaks
- **Runs**: Nightly, or manually before releases

Tests included:
- All Tier 2 tests
- Concurrency/parallel tests
- GC pressure loops
- Large data edge cases (>64KB strings, >1000 element arrays)
- Repeated async completion stress

---

## Success Criteria

### Coverage Targets

1. **Matrix coverage**: Every `G✓` cell reaches `G✓ R✓` status (Layer 2 test exists and passes)
2. **Gap investigation**: All `G?` cells investigated; either promoted to `G✓` or documented as unsupported
3. **Phase 55-61 repro**: All 6 bugs have dedicated regression tests that would have caught them
4. **Negative-path coverage**: At least 10 negative-path tests covering invalid states

### Quality Targets

5. **Zero flaky tests**: 0 flaky tests across 10 consecutive Tier 3 runs (3x repetition per test)
6. **Actionable failures**: Each test failure clearly indicates generator bug vs interop bug
7. **Ownership safety**: Retain/release balance verified for all object-returning paths

### Performance Targets

8. **Tier 1 budget**: < 30 seconds for PR gate tests
9. **Tier 2 budget**: < 3 minutes for merge gate tests
10. **Tier 3 budget**: < 15 minutes initial target for full nightly run (including 3x repetition); revisit as stress tests grow

### Reproducibility Targets

11. **Self-contained**: No external dependencies, runs on any macOS with .NET 10
12. **Deterministic**: Fixed test data, explicit timeouts, no wall-clock assumptions
13. **Fresh bindings**: `run-runtime-tests.sh` always regenerates bindings before testing (prevents stale binding false confidence)

---

## Toolchain Requirements

Pin versions for full reproducibility:

| Component | Version | Notes |
|-----------|---------|-------|
| .NET SDK | 10.0.x | Specified in `global.json` |
| Xcode | 16.0+ | Swift 6.0 toolchain |
| macOS | 14.0+ (Sonoma) | Required for .NET 10 iOS workload |
| iOS Simulator | 17.0+ | Test target runtime |

The `global.json` at repo root pins .NET SDK. Swift/Xcode version should be documented in TestFramework README with minimum requirements.

---

## Future Considerations (Deferred)

### Golden API Snapshots

Snapshot generated C# public API surface to catch signature drift. Useful but potentially noisy during active development. Consider gating on releases.

### CI Integration

Document expectation that runtime tests run as required gate on macOS. Actual CI setup depends on project infrastructure.

### NativeAOT Variant

Once NativeAOT validation complete (remaining-work.md #6), consider adding NativeAOT-specific runtime test lane.
