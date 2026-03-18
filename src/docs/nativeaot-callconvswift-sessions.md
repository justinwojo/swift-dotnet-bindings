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

## Session 1: Runtime & Infrastructure Cleanup — **Status: Complete** (`ee6a86ac`)

**Goal**: Remove proven-unnecessary infrastructure, simplify the runtime, update docs. Zero-risk changes backed by investigation evidence.

**Completed**: March 18, 2026. All sub-tasks delivered. 7829 unit tests passed, 90/90 validation, BindingTests build-and-test succeeded. 17 files changed (net -459 lines of source + golden file churn). DestroyWrapperEmitter deleted (157+302 lines), SwiftHandle simplified, direct CallConvSwift existential metadata P/Invoke added, upstream bug reports updated.

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

## Session 2: Generator Bug Fixes — **Status: Complete** (`31e746aa`)

**Goal**: Fix the 10 generator/runtime bugs that were misattributed to NativeAOT. Each fix unskips tests. Work in priority order — stop if the session runs long and defer remaining items.

**Completed**: March 18, 2026. Sub-tasks 2A, 2B, 2C delivered. 3 generator fixes: `load(as:)` → `assumingMemoryBound(to:).pointee` (8 skips recovered), generic metatype resolution (15 skips → MonoJitCrash), @convention(c) bool bridging. ~30 pre-existing Mono JIT crashes properly classified. 7829 unit tests, 90/90 validation, 432+ runtime tests. Deferred: 2D (struct singleton ARC), 2E Bug 2 (multi-closure ordering), 2F (subscript init).

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

## Session 3: CallConvSwift Architecture Migration — COMPLETE

**Goal**: Modify the generator's calling convention selection to use direct CallConvSwift for patterns proven safe, keeping @_cdecl only where required by upstream limitations. Target reduction: ~78.5% @_cdecl → ~55%.

**Result**: 78.5% → 54.1% @_cdecl (45.9% direct CallConvSwift). Target exceeded.

### Sub-task 3A: Implement Calling Convention Decision Logic — COMPLETE

Implemented `WrapperValidation.RequiresCdeclForAbiSafety()` with struct classification:

```
non-blittable param/return              → @_cdecl required
ValueTuple param/return                 → @_cdecl required
generic container (Array, Dict, etc.)   → @_cdecl required
custom struct with float/double fields  → @_cdecl required (param, return, AND self)
custom integer struct > 16 bytes param  → @_cdecl required
system frozen struct > 8 bytes          → @_cdecl required (Mono JIT multi-register)
self type: custom frozen struct > 8B    → @_cdecl required (SwiftSelf<T> by-value)
self type: no InlineSize + multi-field  → @_cdecl required (conservative heuristic)
closure params                          → @_cdecl required (adapter mechanism)
everything else                         → CallConvSwift safe
```

**Key implementation details**:
- `HasFloatFields` flag on TypeRecord, detected during parsing (Swift.Float, Swift.Double, CGFloat, nested)
- `IsSelfTypeCdeclRequired()` — new check for SwiftSelf<T> by-value self on frozen structs
- Property count heuristic: when InlineSize unavailable (simulator dylib metadata fails), uses stored property count > 1 as proxy for multi-register struct
- System frozen structs (CGRect, etc.) exempt from self-type checks (special runtime handling)

**Files modified**: `WrapperValidation.cs`, `TypeRecord.cs`, `ModuleProcessor.cs`, `MethodHandler.cs` (4 decision points), `PropertyHandler.cs` (1 decision point)

### Sub-task 3B: Update Wrapper Emitters — COMPLETE

The existing `ShouldEmitWrapper()` gates are ANDed with `RequiresCdeclForAbiSafety()`:
- `ShouldEmitWrapper() && RequiresCdeclForAbiSafety()` → emit @_cdecl wrapper
- `ShouldEmitWrapper() && !RequiresCdeclForAbiSafety()` → emit with `SB0001` obsolete warning (direct CallConvSwift, may crash on Mono)
- `!ShouldEmitWrapper()` → direct CallConvSwift (no change)

4 decision points in MethodHandler + 1 in PropertyHandler updated. Constructor and subscript paths already gated correctly by existing wrapper emitter checks.

### Sub-task 3C: Metrics — COMPLETE

**Wrapper strategy breakdown** (BindingTests library, 778 classified P/Invokes):
| Strategy | Count | % |
|----------|------:|---:|
| LegacyCallConvSwift (no wrapper) | 357 | 45.9% |
| CdeclMethod | 198 | 25.4% |
| CdeclProperty | 141 | 18.1% |
| CdeclConstructor | 78 | 10.0% |
| CdeclSubscript | 4 | 0.5% |
| **Total @_cdecl** | **421** | **54.1%** |

**Validation gates**:
- Unit tests: 7874 passed, 0 failed
- Library validation: 90/90 passed (no regressions, BonMot improved: swift:fail → swift:ok)
- Build-and-test: bindings + bridge compilation successful
- Runtime tests: 239 passed before finalizer crash (pre-existing Mono JIT jit-info.c:918)

**Runtime crashes investigated and annotated**:
- `Animal.name` SIGSEGV — Session 3 regression (String 16B > 8B threshold). Fixed.
- `SafeDiv.numerator` returns 0 — Session 3 regression (float-field struct self type). Fixed.
- `RangedInt.value` returns 0 — Session 3 regression (multi-field struct self without InlineSize). Fixed.
- `BaseEntity` constructor crash — pre-existing Mono metadata cache corruption. `[MonoJitCrash]` added.
- `ValidateRangeTypedCatch` crash — pre-existing swifterror register mismatch. `[MonoJitCrash]` added.
- `BridgeAsyncViewTests` crash — pre-existing finalizer thread crash (jit-info.c:918). `[MonoJitCrash]` added.

### Validation Gates

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh` → `run-runtime-tests.sh`

Full runtime test suite is critical for this session since calling convention changes can cause silent ABI mismatches (wrong values, not crashes).

---

## Session 4: Regression Fixes & Annotation Cleanup — COMPLETE

**Goal**: Fix Session 2/3 regressions found during NativeAOT device validation and Codex code review. Remove all `[MonoJitCrash]` annotations that Session 2/3 added. Replace with platform-aware `[SkipOnSimulator]` (skips on Mono, runs on NativeAOT device) for Mono-specific failures, preserving device test coverage.

**Result**: 49 `[MonoJitCrash]` annotations removed, `MonoJitCrashAttribute` deleted. New `[SkipOnSimulator]` attribute replaces the deprecated platform-specific skip mechanism. 3 generator bugs fixed (generic `_payloadSize`, duplicate TypeMetadata, constructor metatype). Simulator: 534 passed, 160 skipped, 0 failed. Unit tests: 7881 passed. Validation: 90/90.

### Sub-task 4A: Class Method CallConvSwift Guards — COMPLETE (`a16629a9`)

Session 3's `RequiresCdeclForAbiSafety()` incorrectly allowed direct CallConvSwift for two categories of class methods:

1. **Static class methods**: Swift's `@convention(method)` passes `@thick Self.Type` (metatype) as a hidden last parameter. C# P/Invoke declarations don't include this parameter → Swift reads garbage from the metatype register → SIGSEGV. Affects ALL static class methods (final and non-final). Proven on NativeAOT device: `EventHandler.createDefault()` crash.

2. **Non-final class instance methods**: Use Tj dispatch thunks (vtable indirection). Direct CallConvSwift against Tj symbols crashes on both runtimes. Proven in evidence matrix.

**Fix applied**: Both guards added to `RequiresCdeclForAbiSafety()` and property overload. 7 new ABI safety tests. NativeAOT device: 27 → 47 passes, EventHandler crash resolved.

### Sub-task 4B: `HasFloatFields` CGFloat Detection Fix — COMPLETE (`18da4e42`)

Session 3's `HasFloatFields` detection in `ModuleProcessor.cs` checked for `CoreFoundation.CGFloat` but the type database registers CGFloat under `CoreGraphics.CGFloat` (see `CoreGraphicsDatabase.xml`). Custom structs containing `CGFloat` fields were misclassified as safe for direct CallConvSwift — exactly the HFA bug bucket Session 3 is fencing off.

**Fix applied**: Added `"CoreGraphics.CGFloat"` to the float field detection in `ModuleProcessor.cs`.

### Sub-task 4C: Escaping Bool Bridge Delegate Lifetime — COMPLETE (`18da4e42`)

Session 2's `@convention(c)` bool bridge for escaping closures creates a local bridge delegate (`Func<byte>` wrapping the user's `Func<bool>`), then passes its function pointer to Swift via `Marshal.GetFunctionPointerForDelegate`. Nothing roots the bridge delegate after the call returns. If Swift stores and later invokes the function pointer, the managed thunk's target may have been GC'd.

**Fix applied**: Emit `GCHandle.Alloc(bridgeName)` to root the bridge delegate for escaping closures in `WrapperEmitter.Marshalling.cs`.

### Sub-task 4D: Remove ALL `[MonoJitCrash]` Annotations — COMPLETE

Removed all 49 `[MonoJitCrash]` annotations from 13 test files. Deleted `MonoJitCrashAttribute` from `TestResults.cs`. Cleaned up `TestBase.cs` runner and `run-runtime-tests.sh`.

**New `[SkipOnSimulator]` attribute**: Replaces `[MonoJitCrash]` with a proper mechanism. Skips on simulator (Mono), runs on device (NativeAOT). Both `TestBase.cs` (per-method) and `Program.cs` (class-level pre-instantiation) honor it. Preserves NativeAOT device coverage that `[Skip]` would have removed.

**Tests now passing** (formerly MonoJitCrash, 20 tests):
- `ExistentialMetadataTests`: TestGetExistentialTypeMetadata_ZeroProtocols, TestTryGetTypeMetadata_ExistentialContainer0
- `ClassSingletonTests`: TestScopeGetDescribe
- `ExistentialBoxingTests`: TestRunModeConsumerWithStrictMode
- `OptionSetTests`: TestTextStyleEqualitySame
- `ClassMarshallingTests`: TestClassSurvivesGCPressure, TestMultipleObjectsGCPressure (pass individually; skipped in full suite due to finalizer timing)
- All 30 `BasicThrowingTests` (including formerly-MonoJitCrash TestValidateRangeTypedCatchWithError success path)

**Tests now `[SkipOnSimulator]`** (Mono-specific crashes, 44 tests — run on device):
- 18 generic type tests (CallConvSwift P/Invoke in PInvokeHelper class crashes Mono JIT `jit-info.c:918`)
- 4 returned closure tests (delegate* unmanaged[Swift] with SwiftSelf SIGSEGV on Mono)
- 2 SwiftOptional<Shape> tests (generic metadata CallConvSwift)
- 4 Codec.Encoding tests (non-frozen String enum ARC copy crashes Mono finalizer)
- 2 Codec.Alignment tests (String-raw-value enum deferred Sys:Free)
- 1 typed throws test (swifterror register mismatch on Mono throwing path)
- 7 StateUpdate bridge tests (NSRunLoop.RunUntil triggers jit-info.c:918)
- 3 AsyncView bridge tests (finalizer SIGSEGV in Arc.Release)
- 1 SimpleView bridge test (NSRunLoop.RunUntil triggers jit-info.c:918)
- 2 GC stress tests (deliberate GC triggers Mono finalizer Sys:Free crash)

**Tests `[Skip]`** (broken on both platforms, 3 tests):
- 3 @convention(c) closure tests (AOT-only mode: lambda-based native-to-managed callbacks require JIT)

**Generator bugs fixed during 4D**:

1. **Generic `_payloadSize` static field crash**: `SwiftObjectHelper<Wrapper<T>>` in a static field initializer crashes Mono's generic sharing (`mini-generic-sharing.c:2759`). Fixed in `NonFrozenStructHandler.cs` and `EnumHandler.cs`: generic types now use `HelperClass.PInvoke_getMetadata(SwiftObjectHelper<T>.GetTypeMetadata()).Size` instead.

2. **Duplicate TypeMetadata in generic P/Invokes**: `HandleGenericMetadata()` added per-param TypeMetadata to the P/Invoke signature, AND `PInvokeHelperContext` added the same metadata as trailing parameters — doubling every TypeMetadata for generic types → ABI mismatch → Mono crash. Fixed in `PInvokeEmitter.cs`: skip PInvokeHelperContext trailing metadata when `HandleGenericMetadata()` already covered per-param metadata. Constructors keep one extra trailing TypeMetadata for the allocating init's Self.Type metatype.

3. **Constructor metatype Mono crash**: Allocating init metatype used `SwiftObjectHelper<ContainerType<T>>` which crashes Mono. Fixed in `MethodMarshalPlanBuilder.cs`: use `HelperClass.PInvoke_getMetadata(per-param-metadata)` instead.

### Sub-task 4E: Codex Review P3 Findings — COMPLETE

**P3 #1: DefaultParameterOverloadEmitter not gated on RequiresCdeclForAbiSafety**. Deferred — low priority. The overloads get MORE wrapping than needed (safe but wasteful), not less.

**P3 #2: RegisterDestroyAction [Obsolete] is source-breaking for TreatWarningsAsErrors** — FIXED. Removed the `[Obsolete]` attribute from `SwiftHandle.cs`, keeping the method as a silent no-op for backward compatibility.

### Sub-task 4F: Evidence-vs-Implementation Mismatches — Documented

**CGRect/system structs**: Session 3's implementation treats system frozen structs > 8 bytes as @_cdecl-required (intentionally conservative). Can be relaxed with targeted NativeAOT device testing.

**VWT Destroy test coverage**: Low priority. Session 1 removed destroy action assertions; remaining tests verify ReleaseHandle closes the handle.

### Sub-task 4G: Session 2 Deferred Sub-tasks — DEFERRED TO SESSION 5

These remain from Session 2:
- **2D**: Struct singleton ARC (`initializeMemory` vs `initializeWithCopy`) — Alamofire stability
- **2E Bug 2**: Multi-closure parameter ordering — 1 skip (TransformerChain)
- **2F**: Subscript class initialization — 1 class-level skip

### Validation Gates

- Unit tests: 7881 passed, 0 failed ✓
- Library validation: 90/90 passed ✓
- Runtime tests (simulator): 534 passed, 160 skipped, 0 failed ✓
- BindingTests compilation: verified via `--skip-regen` builds

---

## Session 5: Session 2 Deferred Fixes & Mono Finalizer Hardening

**Goal**: Fix Session 2 deferred generator bugs (3 items) and harden the Mono finalizer code path to recover ~17 simulator-skipped tests. These are self-contained fixes with known root causes.

**Scope**: 5D (deferred bugs) + 5C (finalizer hardening) + 5E (Codec ARC). Stop after validation gates.

### Sub-task 5A: Struct Singleton ARC (Session 2D) — HIGH

**Bug**: ARC reference count corruption from `initializeMemory` (bitwise copy) instead of `initializeWithCopy` (proper retains). SIGBUS on second access of singleton struct properties.
**Location**: Property getter emission
**Impact**: Alamofire `URLEncoding.Default` stability

### Sub-task 5B: Multi-Closure Parameter Ordering (Session 2E Bug 2) — MEDIUM

**Bug**: Multi-closure parameter ordering wrong in generated code.
**Location**: Closure emission
**Skips recovered**: 1 (TestTransformerChain in `CompositionTests.cs`)

### Sub-task 5C: Subscript Class Initialization (Session 2F) — MEDIUM

**Bug**: SubscriptTests class-level skip — class initialization crashes for subscript types.
**Location**: Metadata or parameter marshalling
**Skips recovered**: 1 class-level skip (all tests in `SubscriptTests`)

### Sub-task 5D: Mono Finalizer Thread Crashes (15 tests) — MEDIUM

**Bug**: Mono's finalizer thread crashes with `jit-info.c:918` when freeing Swift handles via `Sys:Free`. Affects:
- NSRunLoop.RunUntil-based SwiftUI bridge tests (8 tests)
- Async view tests with Arc.Release (3 tests)
- GC stress tests (2 + 1 class-level)
- String-raw-value enum deferred free (2 tests)

These all work individually but crash when the finalizer thread runs during or after the test. The root cause is likely in `SwiftSafeHandle.ReleaseHandle()` or `SwiftClassHandle.ReleaseHandle()` invoking VWT Destroy / Arc.Release from the finalizer thread on Mono.

**Potential fix**: Guard VWT Destroy and Arc.Release calls in ReleaseHandle to skip when called from the Mono finalizer thread (already partially implemented with `s_isMonoRuntime` but may need refinement for the `Sys:Free` code path).

### Sub-task 5E: Codec.Encoding ARC Copy (4 tests) — LOW

**Bug**: Codec.Encoding (non-frozen String enum) ARC copy crashes Mono finalizer thread. The Codec constructor takes a `Codec.Encoding` parameter; the enum's String raw value requires ARC retain/release during copy, which fails on the Mono finalizer thread. Likely same root cause as 5D.

**Tests affected**: TestCodecConstructionJson, TestCodecConstructionXml, TestCodecEncodingValueProperty, TestCodecGetDescribe in `NestedEnumTests.cs`

### Validation Gates

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh` → `run-runtime-tests.sh`

---

## Session 6: Generic CallConvSwift & Remaining Mono Gaps

**Goal**: Fix the generic type CallConvSwift P/Invoke pattern that crashes Mono JIT, and clean up remaining low-priority items. This is deep infrastructure work.

### Sub-task 6A: Generic Type CallConvSwift on Mono (18 tests) — HIGH

**Bug**: All P/Invokes in `PInvokeHelper` classes for generic types crash Mono JIT with `jit-info.c:918` when using `CallConvSwift`. The metadata accessor (`PInvoke_getMetadata`) also crashes. Session 4 fixed duplicate TypeMetadata params and `_payloadSize` static field initialization, but the fundamental issue remains: Mono's JIT can't compile CallConvSwift P/Invokes in certain contexts.

**Potential approaches**:
1. Emit @_cdecl metadata accessor wrappers (non-generic Swift functions that forward to the generic metadata accessor) — avoids CallConvSwift entirely for metadata resolution
2. Use `DllImport` with `CallingConvention.Cdecl` for metadata accessors on generic types (if a @_cdecl wrapper is emitted in the Swift wrapper library)
3. Investigate whether `RuntimeFeature.IsDynamicCodeSupported` branching can route around the Mono JIT crash path

**Tests affected**: 15 `[SkipOnSimulator("Generic type CallConvSwift P/Invoke crashes Mono JIT")]` + 3 `[SkipOnSimulator("BaseEntity metadata cache corruption")]` in `BasicGenericTests.cs`

### Sub-task 6B: Returned Thick Closures on Mono (4 tests) — MEDIUM

**Bug**: Invoking returned closures via `delegate* unmanaged[Swift]` with `SwiftSelf` context SIGSEGV on Mono. Proven in evidence matrix (line 89). Works on NativeAOT.

**Tests affected**: TestMakeAdder, TestMakeMultiplier, TestMakeGreaterThan, TestClosureFactory in `ClosureTests.cs`

### Sub-task 6C: SwiftOptional<Shape> Generic Metadata (2 tests) — MEDIUM

**Tests affected**: TestEnumPropertyHolder_SetOptionalShape, TestEnumPropertyHolder_ClearOptionalShape. Same root cause family as 6A — generic metadata CallConvSwift on Mono.

### Sub-task 6D: AOT @convention(c) Closure Callbacks (3 tests) — LOW

**Bug**: TestConventionCFunction, TestCBinaryFunction, TestCPredicate fail on iOS simulator with "Attempting to JIT compile method while running in aot-only mode." The generated callbacks use C# lambdas that require a JIT-compiled native-to-managed wrapper. Fix: generate `[UnmanagedCallersOnly]` static methods for @convention(c) callbacks.

**Tests affected**: 3 tests in `ClosureTests.cs` (currently `[Skip]` — broken on both platforms)

### Sub-task 6E: P3 #1 DefaultParameterOverloadEmitter (cleanup) — LOW

Lines 98 and 150 of `DefaultParameterOverloadEmitter.cs` still promote overloads to @_cdecl based on `ShouldEmitWrapper()` alone, not gated on `RequiresCdeclForAbiSafety()`. Safe but wasteful.

### Sub-task 6F: Evidence-vs-Implementation Reconciliation — LOW

**CGRect/system structs**: Session 3 treats system frozen structs > 8 bytes as @_cdecl-required (intentionally conservative). Can be relaxed with targeted NativeAOT device testing.

**VWT Destroy test coverage**: Add focused test verifying actual VWT Destroy invocation.

### Validation Gates

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh` → `run-runtime-tests.sh` (both simulator AND device)

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
