# NativeAOT & CallConvSwift Work Sessions

**Created**: March 18, 2026
**Prerequisite**: NativeAOT investigation complete. All findings in `/Users/wojo/Dev/swift-interop-repro/NATIVEAOT-FINDINGS.md`.
**Replaces**: Roadmap Session 5 items that overlap (struct singleton, enum dispose, URL.init wrapper). Non-overlapping Session 5 items (ConfigurationValue collision, SwiftUI construction, Stripe config, protocol proxy co-gating) remain in the main roadmap.

---

## Background

The `@_cdecl` wrapper architecture routes ~78.5% of P/Invokes through Swift wrappers using `CallingConvention.Cdecl`. This was built to work around 6 perceived NativeAOT CallConvSwift issues. Investigation proved **4 of 6 were our bugs**, 1 is an upstream bug (different than claimed), and 1 is a documented limitation.

35 test skip annotations exist across the codebase (31 `[Skip("NativeAOT: ...")]` + 3 `"Generic constructor missing @_cdecl wrapper"` + 1 `"MarshalDirectiveException: StructLayout.Auto"`). Of these, **30 are caused by our generator/runtime bugs**, 3 are upstream limitations (non-blittable types, ValueTuple StructLayout.Auto), and 2 are Mono-specific (returned thick closures, ValueTuple class loading).

### What's Safe for CallConvSwift (proven on both runtimes)

Scalar params, SwiftSelf, SwiftError, TypeMetadata, PWT, generic functions, VWT Destroy/InitializeWithCopy via `delegate* unmanaged[Swift]`, `@convention(c)` callbacks (including Bool return), allocating inits (concrete symbol, not Tj dispatch thunks), SwiftIndirectResult, CGRect/system structs, custom integer structs ≤ 16B.

### What MUST Stay as @_cdecl

1. **Custom structs with float/double fields** (params AND returns) — NativeAOT puts floats in GPR instead of FPR; Mono crashes on float struct returns. Opposite directions, both broken.
2. **Custom integer structs > 16 bytes** — NativeAOT SIGSEGV (pointer vs register mismatch).
3. **Non-blittable types** (SafeHandle, SwiftString, SwiftOptional) — documented CallConvSwift scope limitation.
4. **Returned thick closures** (Mono only) — `delegate* unmanaged[Swift]` with SwiftSelf SIGSEGV on Mono.
5. **Class method dispatch thunks** (Tj symbols) — both runtimes crash on vtable indirection.
6. **ValueTuple / StructLayout.Auto** — MarshalDirectiveException (Mono), SIGSEGV during class loading (NativeAOT).

### Struct Asymmetry

Custom float structs fail in opposite directions on each runtime:

| Direction | Mono | NativeAOT |
|-----------|------|-----------|
| Custom float struct as **PARAMETER** | PASS | ABI MISMATCH (floats in GPR) |
| Custom float struct as **RETURN** | SIGSEGV | PASS |

@_cdecl wrappers needed for both directions on both runtimes.

### Decision Framework

```
IF any param/return is non-blittable (SafeHandle, SwiftString, SwiftOptional<T>):
    → @_cdecl (documented limitation, both runtimes)

ELSE IF any param/return involves ValueTuple (StructLayout.Auto):
    → @_cdecl (MarshalDirectiveException / SIGSEGV, both runtimes)

ELSE IF any PARAM is a custom struct with float/double fields:
    → @_cdecl (NativeAOT puts floats in GPR instead of FPR)

ELSE IF any RETURN is a custom struct with float/double fields:
    → @_cdecl (Mono SIGSEGV on float struct returns)

ELSE IF any param is a custom integer struct > 16 bytes (> 2 fields):
    → @_cdecl (NativeAOT SIGSEGV — pointer vs register mismatch)

ELSE:
    → CallConvSwift directly (proven safe on both runtimes)
```

This framework covers the *calling convention* dimension only. The generator's existing `ShouldEmitWrapper()` validation gates (async, actor isolation, inout, variadic, etc.) are orthogonal and remain as-is.

### Upstream Bugs

| Draft | Verdict | Action |
|-------|---------|--------|
| #1: Mono JIT `!ji->async` | **VALID** (Mono-only) | Keep, file for Mono |
| #2: Non-blittable CallConvSwift | **VALID** | Keep as feature request |
| #3: SafeHandle in async P/Invoke | **MONO-ONLY** | Update draft |
| #4: VWT Destroy crash | **DISPROVEN** | Delete |
| #5: CGRect ABI mismatch | **REWRITE** | File as custom struct HFA bug (floats in GPR) |
| NEW: Custom struct float fields in GPR | **New upstream bug** | File with minimal repro (single `struct S { double A; }`) |

### Cross-Platform Evidence Matrix

| Pattern | Mono Simulator | NativeAOT Device | Safe? |
|---------|---------------|-------------------|-------|
| nint params | PASS | PASS | YES |
| double params (not in struct) | PASS | PASS | YES |
| SwiftSelf | PASS | PASS | YES |
| SwiftError (4 tests) | PASS | PASS | YES |
| TypeMetadata | PASS | PASS | YES |
| PWT | PASS | PASS | YES |
| Generic function (sumTwo\<T\>) | PASS | PASS | YES |
| VWT Destroy via func ptr | PASS | PASS | YES |
| VWT InitializeWithCopy via func ptr | PASS | PASS | YES |
| @convention(c) callback | PASS | PASS | YES |
| Bool-returning callback | PASS | PASS | YES |
| Returned closure (delegate* + SwiftSelf) | SIGSEGV | PASS | NO (Mono) |
| Allocating init (→ IntPtr) | PASS | PASS | YES |
| Dispatch thunk (Tj) | SIGSEGV | SIGKILL | NO (both) |
| SwiftIndirectResult (3×double) | PASS | PASS | YES |
| CGRect (32B) as param | PASS | PASS | YES |
| 2×nint (16B) as param | PASS | PASS | YES |
| 3×nint (24B) as param | not tested | SIGSEGV | NO |
| 4×nint (32B) as param | SIGSEGV | SIGSEGV | NO |
| 1–4 doubles as param (custom struct) | PASS | ABI MISMATCH | NO (NativeAOT) |
| 2 doubles as return (custom struct) | SIGSEGV | PASS | NO (Mono) |
| 5 doubles (40B, indirect) as param | PASS | PASS | YES |
| SafeHandle in CallConvSwift | InvalidProgramException | SIGKILL | NO (limitation) |
| ValueTuple (StructLayout.Auto) | MarshalDirectiveException | SIGSEGV (class load) | NO (limitation) |

---

## Session 1: Runtime & Infrastructure Cleanup

**Goal**: Remove proven-unnecessary infrastructure, simplify the runtime, update docs. Zero-risk changes backed by investigation evidence.

**Partially started**: Working tree on `nativeaot-investigation` branch has draft changes to EnumHandler, FrozenStructHandler, NonFrozenStructHandler, SwiftHandle.cs, TypeMetadata.cs, and SwiftClassHandleTests.cs. Review and complete these.

### Sub-task 1A: VWT Destroy Wrapper Elimination

Remove per-type `@_cdecl` destroy wrappers. VWT Destroy via `delegate* unmanaged[Swift]` is proven safe on both runtimes.

**Changes**:
- Remove `DestroyWrapperEmitter.EmitIfNeeded()` calls from `EnumHandler.cs`, `FrozenStructHandler.cs`, `NonFrozenStructHandler.cs`
- Delete `DestroyWrapperEmitter.cs` (157 lines) and `DestroyWrapperEmitterTests.cs` (302 lines, 29 tests)
- Remove `TryAddDestroyWrapperSymbol`/`HasDestroyWrapperSymbol` from `ModuleEmissionContext.cs`
- Simplify `SwiftHandle.cs`: remove `s_destroyAction` field, mark `RegisterDestroyAction` as `[Obsolete]` no-op (backward compat with already-generated bindings), collapse `ReleaseHandle()` to single VWT Destroy path with Mono finalizer skip
- Update `SwiftClassHandleTests.cs` — remove `RegisterDestroyAction` test usage
- Update golden files

**Impact**: Eliminates hundreds of per-type wrapper functions from generated bindings. Every non-generic struct/enum currently emits `SBW_Destroy_Module_Type` in both Swift and C#.

**Tests**: Existing `run-tests.sh` + `validate-libraries.sh` + `build-and-test.sh`. Golden file diffs expected.

### Sub-task 1B: Direct CallConvSwift for Existential Metadata

Replace the `SwiftBindingsRuntime` `@_cdecl` wrapper for `swift_getExistentialTypeMetadata` with a direct CallConvSwift P/Invoke.

**Changes**:
- Add `SwiftCoreNativeMethods` class in `TypeMetadata.cs` with direct `[DllImport("libswiftCore")]` using `CallConvSwift`
- Update `TryGetExistentialTypeMetadataViaWrapper()` to try direct call first, fall back to runtime wrapper
- Keep `RuntimeNativeMethods` as fallback for environments where `libswiftCore` isn't directly loadable

### Sub-task 1C: Upstream Bug Report Cleanup

- Delete Draft #4 (VWT Destroy crash — disproven)
- Rewrite Draft #5 as custom struct HFA bug: "CallConvSwift on NativeAOT ARM64 passes custom struct float/double fields in GPR instead of FPR"
- Update Draft #3 as Mono-only
- Add new upstream bug with minimal repro: `struct S { public double A; }` → garbage value

**File**: `src/docs/Future/upstream-bug-reports-draft.md`

### Validation Gates

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh`

---

## Session 2: Generator Bug Fixes

**Goal**: Fix the 10 generator/runtime bugs that were misattributed to NativeAOT. Each fix unskips tests. Work in priority order — stop if the session runs long and defer remaining items.

**Total skip recovery target**: up to 30 `[Skip]` annotations removed + 2 stability fixes.

### Sub-task 2A: Generic Class Metadata (15 skips) — HIGH

**Bug**: Generator passes T metadata instead of `Wrapper<T>` metadata to generic class allocating inits.
**Evidence**: `sumTwo<SummableInt>(10, 20) = 30 — PASS` proves generic dispatch works. The crash is in our metadata resolution, not CallConvSwift.
**Location**: Generator allocating init emission
**Skips recovered**: 12 `[Skip("NativeAOT: SIGSEGV in generic ... constructor")]` in `BasicGenericTests.cs`
**Bonus**: 3 `[Skip("Generic constructor missing @_cdecl wrapper")]` tests (TestGenericNamedBoxCreation, TestGenericNamedBoxName, TestTypedEntityCreation) may also be fixable — these fail because `[DllImport]` with `CallingConvention.Cdecl` can't exist inside generic types (CS7042). Moving to `[LibraryImport]` with `CallConvSwift` would bypass this restriction.

### Sub-task 2B: Closure Fixes (5 skips) — HIGH

**Bug 1**: Bool marshalling — using `bool` instead of `byte`. Swift Bool is 1-byte `byte`, not C# `bool`.
**Evidence**: `callPredicate(isPositive, 42) = 1 — PASS` on both runtimes using `byte`.
**Location**: `ClosureEmitter.cs`
**Skips recovered**: 1 (TestCPredicate)

**Bug 2**: Closure context/layout in returned closures.
**Evidence**: `makeAdder(10)(5) = 15 — PASS`, `makeMultiplier(7)(6) = 42 — PASS` on NativeAOT.
**Location**: `ClosureEmitter.cs`, `WrapperEmitter.Marshalling.cs`
**Skips recovered**: 4 (TestMakeAdder, TestMakeMultiplier, TestMakeGreaterThan, TestClosureFactory)
**Note**: 2 additional closure skips (TestClosureWithOptionalStringReturn, TestClosureWithStringArrayReturn) are non-blittable (SwiftString) — these correctly need @_cdecl and should have their skip reason updated, not removed.

### Sub-task 2C: Enum & Optional Fixes (6 skips + stability) — MEDIUM

**Bug 1**: Enum raw-value String wrapper buffer layout — SIGBUS in `load(as:)` in @_cdecl wrapper.
**Location**: Enum wrapper emission
**Skips recovered**: 4 in `NestedEnumTests.cs` (TestCodecConstructionJson, TestCodecConstructionXml, TestCodecEncodingValueProperty, TestCodecGetDescribe)

**Bug 2**: Optional pointer misalignment for enum payloads — SIGSEGV marshalling `SwiftOptional<Shape>`.
**Location**: `OptionalProjection`
**Skips recovered**: 2 in `EnumMarshallingTests.cs` (TestEnumPropertyHolder_SetOptionalShape, TestEnumPropertyHolder_ClearOptionalShape)

**Bug 3**: Enum case dispose cumulative corruption — SIGSEGV after ~3 enum cases created/destroyed. Same root cause as Mono Bugs #7, #8.
**Location**: Enum lifecycle management
**Impact**: Starscream stability

### Sub-task 2D: Struct Singleton ARC — MEDIUM

**Bug**: ARC reference count corruption from `initializeMemory` (bitwise copy) instead of `initializeWithCopy` (proper retains). SIGBUS on second access of singleton struct properties.
**Location**: Property getter emission
**Impact**: Alamofire `URLEncoding.Default` stability

### Sub-task 2E: Composition & Remaining (3 skips) — LOW

**Bug 1**: Frozen struct + optional array buffer sizes.
**Location**: Parameter marshalling
**Skips recovered**: 2 in `CompositionTests.cs` (TestDescribeConfigFreeFunction, TestDescribeConfigWithTags)

**Bug 2**: Multi-closure parameter ordering.
**Location**: Closure emission
**Skips recovered**: 1 in `CompositionTests.cs` (TestTransformerChain)

**Deferred to future**: DataProjection for Foundation.Data (new projection type, affects Starscream Data tests). Low priority, not a crash.

### Sub-task 2F: Subscript Class Initialization (1 skip) — LOW

**Bug**: SubscriptTests class-level skip — class initialization crashes for subscript types.
**Location**: Metadata or parameter marshalling
**Skips recovered**: 1 class-level skip (all tests in `SubscriptTests`)

### Validation Gates

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh`

---

## Session 3: CallConvSwift Architecture Migration

**Goal**: Modify the generator's calling convention selection to use direct CallConvSwift for patterns proven safe, keeping @_cdecl only where required by upstream limitations. Target reduction: ~78.5% @_cdecl → ~55%.

**Prerequisite**: Sessions 1–2 complete. Bug fixes must land first so the migration doesn't conflate our bugs with calling convention issues.

### Sub-task 3A: Implement Calling Convention Decision Logic

Add a new decision point in the wrapper emission pipeline that determines whether @_cdecl is *required for ABI safety* vs merely used because everything was wrapped historically.

**Decision framework** (implement as a method, e.g., `RequiresCdeclForAbiSafety()`):
```
non-blittable param/return         → @_cdecl required
ValueTuple param/return            → @_cdecl required
custom struct with float/double    → @_cdecl required (param OR return)
custom integer struct > 16 bytes   → @_cdecl required (param only)
everything else                    → CallConvSwift safe
```

This is orthogonal to the existing `ShouldEmitWrapper()` gates. A method that passes all validation gates AND is CallConvSwift-safe can use direct P/Invoke. A method that passes validation gates but needs @_cdecl for ABI safety still gets a wrapper.

**Key files**:
- `ConstructorWrapperEmitter.cs` — `ShouldEmitWrapper()` needs ABI safety check
- `MethodWrapperEmitter.cs` — `ShouldEmitWrapper()` needs ABI safety check
- `PropertyWrapperEmitter.cs` — `ShouldEmitWrapper()` needs ABI safety check
- `PInvokeEmitter.cs` — calling convention selection (`Cdecl` vs `Swift`)
- New: Helper to classify struct types (has float fields? integer-only? size threshold?)

**Struct classification needs ABI JSON / TypeDatabase introspection**:
- Walk struct fields to detect float/double members (including nested structs)
- Compute struct size or field count for the >16B integer threshold
- System types (CGRect, CGSize, CGPoint, etc.) have special runtime handling and PASS — need an allowlist or detection

### Sub-task 3B: Update Wrapper Emitters

For methods/constructors/properties where `RequiresCdeclForAbiSafety()` returns false AND all params/returns are blittable:
- Don't emit Swift @_cdecl wrapper function
- Use mangled Swift symbol directly as `[LibraryImport]` entry point
- Set `CallConvSwift` instead of `CallConvCdecl`

For methods where `RequiresCdeclForAbiSafety()` returns true:
- Continue emitting @_cdecl wrapper (no change from current behavior)

**SwiftIndirectResult optimization**: Struct returns that would otherwise need @_cdecl due to float fields can instead use `SwiftIndirectResult` (buffer pointer in x8). This sidesteps both the NativeAOT float-in-GPR bug and the Mono float-struct-return SIGSEGV. Evaluate whether this is simpler than the float-field @_cdecl path.

### Sub-task 3C: Measure Impact

After migration, measure:
- Wrapper percentage: target ~55% (down from ~78.5%)
- Generated Swift wrapper line count reduction
- Compile time improvement (fewer Swift wrapper functions to compile)
- Generated C# binary size reduction
- Runtime test pass rate (must not regress)

### Validation Gates

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh` → `run-runtime-tests.sh`

Full runtime test suite is critical for this session since calling convention changes can cause silent ABI mismatches (wrong values, not crashes).

---

## Original 6 Architecture Issues — Final Verdicts

For reference, these are the 6 issues that originally motivated the @_cdecl wrapper architecture:

| Issue | Original Claim | Verdict | Session |
|-------|---------------|---------|---------|
| A: Large struct ABI mismatch | CGRect (32B) crashes on NativeAOT | **UPSTREAM** (but different: custom float structs, not CGRect) | 3 (keep @_cdecl for float structs) |
| B: VWT Destroy via func ptr | Crashes on NativeAOT device | **OUR BUG** (wrong buffer sizes / corrupted metadata) | 1A (eliminate wrappers) |
| C: Non-blittable type rejection | InvalidProgramException | **UPSTREAM LIMITATION** (documented) | N/A (keep @_cdecl, correct behavior) |
| D: Returned closures crash | NativeAOT closure invocation | **OUR BUG** (closure marshalling wrong; also Mono SwiftSelf bug) | 2B (fix closures) |
| E: Bool-returning callback | SIGABRT | **OUR BUG** (bool vs byte marshalling) | 2B (fix closures) |
| F: Generic dispatch crashes | SIGSEGV in generic constructors | **OUR BUG** (wrong metadata for allocating inits) | 2A (fix generics) |

## 35 Test Skips — Mapping

| Category | Count | Verdict | Session | Details |
|----------|------:|---------|---------|---------|
| Generics (NativeAOT) | 12 | OUR BUG | 2A | Wrong metadata for generic class allocating inits |
| Generics (CS7042) | 3 | OUR LIMITATION | 2A | @_cdecl can't live in generic types; CallConvSwift + LibraryImport can |
| Closures (returned) | 4 | OUR BUG | 2B | Closure context/layout wrong |
| Closures (Bool) | 1 | OUR BUG | 2B | bool vs byte marshalling |
| Closures (String) | 2 | CORRECT | — | Non-blittable limitation; update skip reason |
| Enum raw-value String | 4 | OUR BUG | 2C | @_cdecl wrapper buffer layout |
| Optional enum | 2 | OUR BUG | 2C | Pointer misalignment for enum payloads |
| Composition | 3 | OUR BUG | 2E | Buffer sizes, closure ordering |
| Composition (closure context) | 1 | OUR BUG | 2E | Closure context not preserved |
| Subscripts | 1 | OUR BUG | 2F | Class initialization crash |
| Tuples (annotation) | 1 | UPSTREAM | — | StructLayout.Auto limitation |
| Tuples (runtime skip) | 1 | UPSTREAM | — | NativeAOT class loading SIGSEGV |
| **Total** | **35** | | | **30 fixable, 3 correct/upstream, 2 update reason** |

## Device Stability Issues — Mapping

| Issue | Verdict | Session |
|-------|---------|---------|
| Struct singleton second-access (Alamofire) | OUR BUG: initializeMemory vs initializeWithCopy | 2D |
| Enum case dispose crash (Starscream) | OUR BUG: cumulative VWT/enum lifecycle corruption | 2C |
| Foundation.Data in @_cdecl | KNOWN LIMITATION: ObjC bridging at boundary | Deferred (DataProjection) |
| CallConvSwift URL.init(string:) | KNOWN LIMITATION: SwiftString non-blittable | N/A (already uses @_cdecl) |
