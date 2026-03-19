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
| NEW: Mono CallConvSwift 16-byte struct return | **Confirmed** (standalone repro) | File with repro project — `makeAdder` returns wrong fn ptr. See `MONO-SIMULATOR-FINDINGS.md` |

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

## Session 5: Verification Pass & Optional Closure Fix — COMPLETE

**Goal**: Verify Session 2 deferred bug claims before fixing them. The "no assumptions" approach: remove skip annotations, run tests, observe actual behavior. Fix only verified bugs.

**Result**: 3 of 5 planned sub-tasks were false bug claims (tests pass with no changes). 1 real generator bug found and fixed (different from what was planned). 1 new generator gap identified. 15 tests recovered, 5 tests reclassified with accurate skip reasons. Unit tests: 7888 passed. Validation: 90/90. Build-and-test: succeeded.

### Sub-task 5A: Struct Singleton ARC — DEBUNKED

**Original claim**: ARC reference count corruption from `initializeMemory` (bitwise copy) instead of `initializeWithCopy`. SIGBUS on second access of singleton struct properties.

**Actual result**: All 18 `StaticStructSingletonTests` pass, including String property access (`FormatName`). Swift's `initializeMemory(as:repeating:count:)` properly handles ARC retain/release for non-trivial types — it is NOT a bitwise copy. The session doc's premise was factually wrong. No code changes needed.

### Sub-task 5B: Multi-Closure Parameter Ordering — MISDIAGNOSED

**Original claim**: Multi-closure parameter ordering wrong in generated code.

**Actual result**: The C# P/Invoke parameter order `[resultPtr, fFuncPtr, fContext, gFuncPtr, gContext]` exactly matches the Swift wrapper's parameter order. The crash in `TestTransformerChain` is actually the **returned thick closure** pattern (`delegate* unmanaged[Swift]` with `SwiftSelf` SIGSEGV on Mono) — the same root cause as `TestMakeAdder`/`TestMakeMultiplier`/`TestMakeGreaterThan`/`TestClosureFactory`.

**Change**: Reclassified from `[Skip("NativeAOT: SIGSEGV in static method with two closure parameters")]` to `[SkipOnSimulator("Returned thick closure via delegate* unmanaged[Swift] with SwiftSelf crashes Mono JIT")]`. Now runs on NativeAOT device.

### Sub-task 5C: Subscript Class Initialization — DEBUNKED

**Original claim**: Class initialization crashes for subscript types (SIGSEGV).

**Actual result**: All 14 `SubscriptTests` pass — including `KeyValueStore` with `String?` subscript returns (`Optional<SwiftString>`) and full CRUD lifecycle. No crash, no class initialization issue. The skip was completely false.

**Change**: Removed class-level `[Skip("NativeAOT: SIGSEGV — class initialization for subscript types")]`. 14 tests recovered.

### Sub-task 5D: Optional Closure GCHandle Lifetime — FIXED (new, not planned)

**Bug discovered during verification**: `TestEventHandlerWithClosure` crashed with `jit-info.c:918` when calling `fire()`. Root cause: the EventHandler constructor's optional closure parameter `((Int32) -> Bool)?` had its GCHandle freed in the `finally` block after the P/Invoke returned. Swift stored the adapted closure (capturing the GCHandle context pointer) in `EventHandler.onComplete`. When `fire()` later invoked the closure, it called back through a dangling GCHandle → crash.

**Root cause**: Optional closures in Swift are always escaping by definition (no `@noescape Optional<Closure>` exists). The ABI parser only propagates the `escaping` attribute to top-level `ClosureTypeSpec` nodes, not those wrapped in Optional. `WrapperEmitter.Marshalling` checked only `closureTypeSpec.IsEscaping` at three sites:
1. **Setup (line ~248)**: chose ThreadStatic vs Marshal.GetFunctionPointerForDelegate for `@convention(c)` closures
2. **Callback emission (line ~696)**: chose ThreadStatic `[UnmanagedCallersOnly]` vs skip for `@convention(c)` closures
3. **Cleanup (line ~980)**: chose GCHandle.Free() vs leak for thunk closures

All three were wrong for optional closures.

**Fix**: Added `WrapperValidation.IsEffectivelyEscaping(closureTypeSpec, originalType, closureHandler)` — a single public method that checks both `IsEscaping` and `IsOptionalClosure`. All three WrapperEmitter.Marshalling sites delegate to it.

**Files modified**:
- `WrapperValidation.cs` — new `IsEffectivelyEscaping()` method
- `WrapperEmitter.Marshalling.cs` — 3 call sites updated
- `ClosureHandlerTests.cs` — 5 new tests (helper-level + projection-level)
- `MethodHandlerOutputTests.cs` — 2 emitter-output regression tests (one for thunk closures, one for `@convention(c)` closures)

**Tests recovered**: 1 (`TestEventHandlerWithClosure`)

### Sub-task 5E: Codec Tests — ROOT CAUSE CORRECTED

**Original claim**: Codec.Encoding non-frozen String enum ARC copy crashes Mono finalizer thread.

**Actual result**: Crash is NOT in the finalizer thread. It's in the test itself: `Codec.FormatValue` getter uses a Tj dispatch thunk (`$s...OvgTj`) via direct CallConvSwift. `Codec` is a non-final class, so its property getters use Tj vtable dispatch. `RequiresCdeclForAbiSafety()` correctly returns `true` for non-final class properties, but `ShouldEmitWrapper()` returns `false` because `Codec.Format` is a nested type (not yet supported in @_cdecl wrappers). Result: property emitted with direct CallConvSwift against a Tj thunk → SIGSEGV on both runtimes.

**Change**: Reclassified from `[SkipOnSimulator("Codec.Encoding non-frozen String enum ARC copy crashes Mono finalizer thread")]` to `[Skip("Codec.format/encoding Tj dispatch thunk: non-final class property with nested return type, @_cdecl wrapper blocked by nested type restriction")]`. Now correctly identified as a generator gap (not Mono-specific).

**Remaining work**: Support nested enum types in @_cdecl property wrappers, OR suppress properties that need @_cdecl but can't get one. See Session 6G.

### SkipOnSimulator Verification Results

| Category | Verified? | Result |
|----------|-----------|--------|
| Generic CallConvSwift (18 tests) | YES | Confirmed crash — Mono JIT limitation |
| Returned thick closures (5 tests) | YES | Confirmed crash — Mono `delegate* unmanaged[Swift]` + SwiftSelf |
| Codec.Encoding ARC (4 tests) | YES | **Misdiagnosed** — actually Tj dispatch thunk crash (both runtimes) |
| Finalizer Sys:Free (15 tests) | Partial | Full suite crash at 344 tests with `Sys:Free` at `jit-info.c:918` — non-deterministic, timing-dependent |
| Typed throws swifterror (1 test) | No | Not yet verified individually |

### Validation Gates

- Unit tests: 7888 passed, 0 failed ✓
- Library validation: 90/90 passed ✓
- Build-and-test: succeeded ✓

---

## Session 6: Complete Runtime Test Cleanup — All Fixable Skips

**Goal**: Fix every remaining fixable `[Skip]` and `[SkipOnSimulator]` annotation in the runtime test suite. Zero deferrals. After this session group, the only remaining skips will be genuinely unfixable upstream limitations.

**Prerequisite**: Mono simulator crash reproduction (`MONO-SIMULATOR-FINDINGS.md`, March 18, 2026). Standalone repro at `/Users/wojo/Dev/swift-interop-repro/` proved 4 of 5 "Mono bug" categories are actually our generator/runtime bugs. Only returned thick closures (Mono CallConvSwift 16-byte struct return ABI) is confirmed upstream.

**Total scope**: 71 fixable tests across 3 sub-sessions. After completion:
- **71 `[Skip]`/`[SkipOnSimulator]` removed**
- **5 `[SkipOnSimulator]` remain** — returned thick closures (confirmed Mono 16-byte struct return ABI bug)
- **19 `[Skip]` remain** — genuinely unfixable (string enum raw values: 8, noncopyable types: 8, non-blittable closures: 2, ValueTuple: 1)

---

### Session 6A: Investigation Bugs (38 tests)

Bugs where the standalone repro proves the pattern works on Mono — our generator/runtime code is wrong. Requires debugging against the repro's working patterns.

#### 6A-1: Generic CallConvSwift P/Invoke (20 tests) — HIGH

**Old claim**: "Mono JIT can't compile CallConvSwift P/Invokes inside generic C# types."
**Repro result**: **ALL PASS on Mono.** `GenericCaller<object>.CallGenericIdentity(42) = 42 — PASS`. The repro calls `genericIdentity<T>` via CallConvSwift from a generic C# class with `SwiftIndirectResult + IntPtr + IntPtr typeMetadata`. Works perfectly.

**Real bug**: Our generator's P/Invoke signatures or marshalling code for generic types is wrong. The repro uses simple `IntPtr` for metadata and `SwiftIndirectResult` for generic returns. Our generator uses `TypeMetadata`, `ProtocolWitnessTable`, and complex `SwiftMarshal.MarshalFromSwift` chains. The bug is in this complexity.

**Debugging strategy**: Compare our generated P/Invoke signatures (in `SwiftBindingsTestLib.cs`) against the repro's working pattern for each generic function. Focus on:
1. Register assignment for TypeMetadata/PWT parameters
2. SwiftIndirectResult buffer allocation and lifetime
3. `SwiftMarshal.MarshalFromSwift` pointer correctness

**Tests**: 15 `[SkipOnSimulator("Generic type CallConvSwift P/Invoke")]` + 3 `[SkipOnSimulator("BaseEntity metadata cache corruption")]` in `BasicGenericTests.cs` + 2 `[SkipOnSimulator("SwiftOptional<Shape> generic metadata")]` in `EnumMarshallingTests.cs`

#### 6A-2: Finalizer Lifecycle Bugs (15 tests) — HIGH

**Old claim**: "Mono finalizer thread crashes when calling NativeMemory.Free / Arc.Release / VWT Destroy."
**Repro result**: **ALL PASS on Mono.** 50 abandoned `SwiftObjectHandle` objects → `GC.Collect` → `GC.WaitForPendingFinalizers` → no crash. Also tested `NativeMemory.Free` and P/Invoke free on finalizer thread — all pass.

**Real bug**: Our SafeHandle implementations have dispose/finalize lifecycle bugs — double-free, use-after-free, invalid pointers, or thread-safety issues with NSRunLoop dispatch.

**Debugging strategy**: Run each subcategory individually on simulator with logging in `ReleaseHandle()`:
1. **NSRunLoop + finalizer (8 tests)**: NSRunLoop.RunUntil may pump the run loop while finalizers execute. Check for reentrancy.
2. **Arc.Release on finalizer (3 tests)**: Log the handle pointer in `ReleaseHandle()` — is it valid?
3. **GC stress (2 + class-level)**: Check for double-dispose (Dispose + finalizer both calling release).
4. **String enum deferred free (2 tests)**: Check string raw value buffer lifecycle.

**Tests**: 8 in `StateUpdateBridgeTests.cs` + 1 in `SimpleViewBridgeTests.cs` + 3 in `AsyncViewBridgeTests.cs` + 1 class-level in `OwnershipGCStressTests.cs` + 2 in `ClassMarshallingTests.cs` + 2 in `NestedEnumTests.cs`

#### 6A-3: Typed Throws & Error Handling (3 tests)

**Old claim**: "Mono JIT puts the error in wrong register for typed throws."
**Repro result**: **ALL PASS on Mono.** Both `throws` and `throws(ReproTypedError)` work correctly via CallConvSwift + SwiftError, including the throwing path. The @_cdecl wrapper also works.

**Real bug**: Our error extraction or marshalling code is wrong — likely dereferencing an invalid error pointer, misaligned typed error, or error lifecycle issue (releasing before extracting).

**Tests**: 1 `[SkipOnSimulator("Typed throws swifterror")]` in `ThrowingMethodTests.cs` + 2 `[Skip("Typed throws: swifterror ABI mismatch")]` in `ThrowingMethodTests.cs`

### Validation Gates (6A)

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh` → `run-runtime-tests.sh` (simulator) → `run-runtime-tests.sh --device` (NativeAOT)

**Both runtimes required.** 6A fixes SkipOnSimulator tests that currently pass on NativeAOT device. Any P/Invoke signature or marshalling change could break the device path. Verify: still pass on device, now also pass on simulator.

---

### Session 6B: Generator Emission Bugs (26 tests)

Bugs where the generator emits incorrect code — clear fixes, no deep investigation needed.

#### 6B-1: Operator @_cdecl Wrappers Stripped (13 tests)

**Bug**: The Swift compiler strips `@_cdecl` wrappers for operator functions during compilation because the linker sees them as unused. C# gets `EntryPointNotFoundException`.

**Fix**: Ensure operator wrapper symbols survive linking (e.g., via `@_used` attribute, or export list).

**Tests**: 13 in `OperatorTests.cs`

#### 6B-2: Async DllImport Wrong Module (5 tests)

**Bug**: Generated async P/Invokes target the wrong library name in `[DllImport]`.

**Fix**: Generator emits wrong module string for async wrappers.

**Tests**: 3 class-level skips (`AsyncMethodTests.cs`, `AsyncComplexTypeTests.cs`, `AsyncStringTests.cs`) + 2 in `ThrowingMethodTests.cs`

#### 6B-3: Optional\<Int32\> None Marshalling (3 tests)

**Bug**: `Optional<Int32>` with `None` value is incorrectly read as `Some`. The marshalling code misinterprets the tag byte.

**Tests**: 3 in `OptionalMarshallingTests.cs`

#### 6B-4: Error Description Type Code (2 tests)

**Bug**: Error description returns the type code string instead of the case name.

**Tests**: 2 in `ThrowingMethodTests.cs`

#### 6B-5: Shape.point Wrapper Stripped (2 tests)

**Bug**: `Shape.point` case wrapper is stripped during compilation — similar to operator wrappers.

**Tests**: 2 in `EnumMarshallingTests.cs`

#### 6B-6: Missing Swift Wrapper Exports (2 tests)

**Bug**: `EntryPointNotFoundException` for expected wrapper functions — wrapper emission gap.

**Tests**: 1 in `ClassMarshallingTests.cs` + 1 in `OwnershipTests.cs`

#### 6B-7: Optional Array Layout Mismatch (1 test)

**Bug**: Buffer size calculation wrong for frozen struct + optional array combination.

**Tests**: 1 in `CompositionTests.cs`

#### 6B-8: IntContainer Array Marshalling (3 tests)

**Bug**: Generic array marshalling for `IntContainer` returns count=0 or crashes on element access.

**Tests**: 3 in `BasicGenericTests.cs`

#### 6B-9: Non-Standard Enum Raw Values (1 test)

**Bug**: ABI JSON lacks enum raw values — generator falls back to case names.

**Note**: This specific test may actually be unfixable (same root cause as string enum raw values). Verify during session.

**Tests**: 1 in `NonStandardEnumTests.cs`

### Validation Gates (6B)

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh` → `run-runtime-tests.sh` (simulator) → `run-runtime-tests.sh --device` (NativeAOT)

**Both runtimes required.** 6B fixes `[Skip]` tests that are broken on both platforms. Generator emission changes (operators, async module, Optional marshalling) could have different ABI behavior on each runtime.

---

### Session 6C: New Emission Patterns (7 tests + cleanup)

Bugs that need new generator emission logic, not just fixes to existing code.

#### 6C-1: Nested Type @_cdecl Property Wrappers (4 tests)

**Bug (from Session 5E)**: Non-final class properties returning nested types (e.g., `Codec.format` returning `Codec.Format`) use Tj dispatch thunks but can't get @_cdecl wrappers because nested types aren't supported in wrapper emission. Property emitted with direct CallConvSwift against Tj thunk → SIGSEGV on both runtimes.

**Fix options**:
1. Support nested enum types (Int32 raw value) in @_cdecl wrappers — pass as Int32, reconstruct on return
2. Suppress properties that need @_cdecl but can't get one (safety net)
3. Both: option 1 for simple nested enums, option 2 as general fallback

**Tests**: 4 in `NestedEnumTests.cs`

#### 6C-2: AOT @convention(c) Closure Callbacks (3 tests)

**Bug**: `@convention(c)` closure callbacks use C# lambdas that require JIT compilation for the native-to-managed wrapper. Fails in AOT-only mode with "Attempting to JIT compile method."

**Fix**: Generate `[UnmanagedCallersOnly]` static methods for `@convention(c)` callbacks instead of lambdas.

**Tests**: 3 in `ClosureTests.cs`

#### 6C-3: Nested Enum Associated Values (3 tests)

**Bug**: Cannot marshal nested enum types used as associated values in parent enums.

**Tests**: 3 in `NestedEnumTests.cs`

#### 6C-4: Cleanup

- **DefaultParameterOverloadEmitter**: Lines 98/150 promote overloads to @_cdecl based on `ShouldEmitWrapper()` alone, not gated on `RequiresCdeclForAbiSafety()`. Safe but wasteful.
- **CGRect/system struct conservative fence**: Session 3 treats system frozen structs > 8 bytes as @_cdecl-required. Can be relaxed with targeted NativeAOT device testing.
- **Update `[SkipOnSimulator]` reasons**: 5 returned closure tests should reference "Mono CallConvSwift 16-byte struct return ABI returns wrong pointer values (confirmed upstream, standalone repro at swift-interop-repro)".

### Validation Gates (6C)

`run-tests.sh` → `validate-libraries.sh` → `build-and-test.sh` → `run-runtime-tests.sh` (simulator) → `run-runtime-tests.sh --device` (NativeAOT)

**Both runtimes required.** 6C adds new emission patterns (UnmanagedCallersOnly, nested type wrappers) that must work on both Mono and NativeAOT. This is the final session — device run is the sign-off gate for the entire Session 6 effort.

---

### Post-Session 6 Skip Inventory

After all three sub-sessions, the runtime test suite should have:

| Annotation | Count | Reason |
|------------|------:|--------|
| `[SkipOnSimulator]` | 5 | Returned thick closures — Mono CallConvSwift 16-byte struct return ABI (confirmed upstream) |
| `[Skip]` — string enum raw values | 8 | No data source in ABI JSON (blocked) |
| `[Skip]` — noncopyable types | 8 | `~Copyable` needs move semantics in wrappers (future roadmap) |
| `[Skip]` — non-blittable closures | 2 | SwiftString in closure callback (upstream limitation) |
| `[Skip]` — ValueTuple | 1 | StructLayout.Auto (upstream limitation) |
| **Total remaining** | **24** | **0 false skips, 0 deferrals** |

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

## Full Runtime Test Skip Inventory (Updated After Mono Simulator Repro, March 18, 2026)

### SkipOnSimulator Annotations (41 total → 36 fixable, 5 upstream)

| Category | Count | Verdict | Session | Details |
|----------|------:|---------|---------|---------|
| Generic CallConvSwift P/Invoke | 18 | **OUR BUG** (repro passes) | 6A-1 | Generator P/Invoke signature or marshalling wrong |
| SwiftOptional\<Shape\> generic metadata | 2 | **OUR BUG** (repro passes) | 6A-1 | Same root cause as generic CallConvSwift |
| Finalizer Sys:Free (NSRunLoop) | 8 | **OUR BUG** (repro passes) | 6A-2 | SafeHandle lifecycle bugs, not Mono finalizer limitation |
| Finalizer Sys:Free (Arc.Release) | 3 | **OUR BUG** (repro passes) | 6A-2 | SafeHandle lifecycle bugs |
| Finalizer Sys:Free (GC stress) | 2+class | **OUR BUG** (repro passes) | 6A-2 | Dispose/finalize double-free or invalid pointer |
| Finalizer Sys:Free (string enum) | 2 | **OUR BUG** (repro passes) | 6A-2 | String buffer lifecycle bug |
| Typed throws swifterror | 1 | **OUR BUG** (repro passes) | 6A-3 | Error extraction/marshalling wrong |
| Returned thick closures | 5 | **MONO BUG** (confirmed) | — | Mono CallConvSwift 16-byte struct return ABI. Keep `[SkipOnSimulator]`. |

### Skip Annotations (55 total → 35 fixable, 20 unfixable)

| Category | Count | Verdict | Session | Details |
|----------|------:|---------|---------|---------|
| Operator wrappers stripped | 13 | OUR BUG | 6B-1 | Linker strips unused @_cdecl symbols |
| String enum raw values | 7 | BLOCKED | — | No data source in ABI JSON |
| Async DllImport wrong module | 5 | OUR BUG | 6B-2 | Generator emits wrong library name |
| Codec Tj dispatch + nested type | 4 | OUR BUG | 6C-1 | Nested types not supported in wrapper emission |
| AOT @convention(c) callbacks | 3 | OUR BUG | 6C-2 | Need `[UnmanagedCallersOnly]` static methods |
| IntContainer array marshalling | 3 | OUR BUG | 6B-8 | Generic array count/element access broken |
| Optional\<Int32\> None marshalling | 3 | OUR BUG | 6B-3 | Tag byte misinterpretation |
| Nested enum associated values | 3 | OUR BUG | 6C-3 | New projection needed |
| Non-blittable closures (SwiftString) | 2 | UPSTREAM | — | Documented CallConvSwift limitation |
| Typed throws swifterror ABI | 2 | OUR BUG | 6A-3 | Error extraction/marshalling wrong |
| Error description type code | 2 | OUR BUG | 6B-4 | String formatting bug |
| Shape.point wrapper stripped | 2 | OUR BUG | 6B-5 | Linker strips enum case wrapper |
| Missing Swift wrapper exports | 2 | OUR BUG | 6B-6 | Wrapper emission gap |
| ~Copyable noncopyable types | 8 | FUTURE | — | Needs move semantics in wrappers (roadmap) |
| Non-standard enum raw values | 1 | BLOCKED | — | Same root cause as string enum raw values |
| Optional array layout mismatch | 1 | OUR BUG | 6B-7 | Buffer size calculation wrong |
| ValueTuple StructLayout.Auto | 1 | UPSTREAM | — | Documented limitation |

### Summary

| | Fixable (Session 6) | Upstream/Blocked | Future Roadmap |
|--|---------------------:|-----------------:|---------------:|
| `[SkipOnSimulator]` | 36 | 5 | 0 |
| `[Skip]` | 35 | 12 | 8 |
| **Total** | **71** | **17** | **8** |

## Device Stability Issues — Mapping (Updated After Session 5)

| Issue | Verdict | Session |
|-------|---------|---------|
| Struct singleton second-access (Alamofire) | ~~OUR BUG~~ **NOT A BUG**: `initializeMemory` properly handles ARC (all 18 tests pass) | 5A (debunked) |
| Enum case dispose crash (Starscream) | OUR BUG: cumulative VWT/enum lifecycle corruption | 2C |
| Foundation.Data in @_cdecl | KNOWN LIMITATION: ObjC bridging at boundary | Deferred (DataProjection) |
| CallConvSwift URL.init(string:) | KNOWN LIMITATION: SwiftString non-blittable | N/A (already uses @_cdecl) |
| Codec nested type property crash | OUR BUG: Tj dispatch thunk without @_cdecl (nested type restriction blocks wrapper) | 6C-1 |
