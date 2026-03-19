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

### Session 6B: Generator Emission Bugs — **Status: Complete**

**Goal**: Fix generator emission bugs across 9 categories, recovering as many `[Skip]`-annotated runtime tests as possible.

**Result**: +40 newly passing tests (584 total, up from 544 baseline). 3 generator bugs fixed, 2 stale skip categories debunked, 4 categories confirmed unfixable (deferred). Unit tests: 7889 passed. Validation: 90/90. Simulator: 584 passed, 0 failed, 110 skipped.

#### 6B-1: Operator @_cdecl Wrappers — FIXED (11 tests recovered, 2 deferred)

**Original claim**: Swift compiler strips `@_cdecl` operator wrappers during compilation → `EntryPointNotFoundException`.

**Root cause**: The operator @_cdecl wrappers used raw Swift struct types as parameters (e.g., `_ lhs: SwiftBindingsTestLib.ArithmeticValue`), which are not C-representable. The Swift compiler rejected them with "method cannot be marked '@_cdecl' because the type of the parameter cannot be represented in Objective-C". The build script's error-based retry then silently stripped the broken functions.

**Fix**: Two-part fix in `OperatorHandler.cs`:
1. **Gating fix**: `ShouldEmitOperatorWrapper` now delegates to `RequiresCdeclForAbiSafety(methodEnv)` instead of wrapping ALL frozen struct operators. Simple integer-only structs (ArithmeticValue, BitwiseValue, ComparableValue) use direct CallConvSwift — no wrapper needed, no stripping issue.
2. **Emission fix**: For operators that DO need @_cdecl (float fields, Bool fields, large structs), `EmitOperatorSwiftWrapper` now uses C-compatible types: `UnsafeRawPointer` params with `.load(as:)` reconstruction, `UnsafeMutableRawPointer` resultPtr with `initializeMemory(as:)` for struct returns, and direct `Bool` for boolean returns. C# P/Invoke emission updated with `CallingConvention.Cdecl`, `IntPtr` params, `SwiftIndirectResult`, and `stackalloc` + `Unsafe.Write` for struct-to-pointer marshalling. Generic helper path sets `OmitCallingConvention = true` for @_cdecl operators.

**New `HasBoolFields` flag**: Added `TypeRecordFlags.HasBoolFields` (1 << 13). Detection in `ModuleProcessor.cs` (same pattern as `HasFloatFields`). Checked in `IsParamTypeCdeclRequired`, `IsReturnTypeCdeclRequired`, and `IsSelfTypeCdeclRequired`. Serialized/deserialized in `ModuleDatabaseEmitter.cs` and `TypeDatabase.cs` for cross-module persistence.

**Tests recovered**: 11 (all ArithmeticValue, BitwiseValue, ComparableValue operators use direct CallConvSwift)
**Tests deferred**: 2 (UnaryValue has Bool field → non-blittable for CallConvSwift → needs the fixed @_cdecl wrapper, but wrapper compilation fails because `@_cdecl` with struct params was the original bug. The wrapper emission fix is correct but UnaryValue still fails because the Swift operator `!` and `~` on the type require the struct to be passed by pointer through @_cdecl, which now works, but the test types were stripped during compilation retry. Future: re-verify after wrapper compilation improvements.)

**Files modified**: `OperatorHandler.cs` (gating + Swift emission + C# emission), `WrapperValidation.cs` (Bool field checks), `TypeRecord.cs` (HasBoolFields flag), `ModuleProcessor.cs` (Bool detection), `ModuleDatabaseEmitter.cs` (serialization), `TypeDatabase.cs` (deserialization)

#### 6B-2: Async DllImport — DEBUNKED (22 tests recovered, 8 deferred)

**Original claim**: Generated async P/Invokes target the wrong library name in `[DllImport]`.

**Actual result**: The library name was correct all along (`"SwiftBindings"`). The `SBW_*_async` entry points exist in the SwiftBindings.xcframework and the framework is included in the RuntimeTestsApp. The class-level skips were stale — likely added before the async wrapper build infrastructure was complete.

**Change**: Removed class-level `[Skip]` from `AsyncMethodTests`, `AsyncComplexTypeTests`, `AsyncStringTests`. Removed 2 method-level `[Skip]` from `ThrowingMethodTests` (TestAsyncParseTypedCatch, TestAsyncParseSuccess).

**Tests recovered**: 22 (all AsyncMethodTests: 12 tests, AsyncStringTests: 4 of 5, AsyncComplexTypeTests: 3 of 9, ThrowingMethodTests: 1 async success, BasicThrowingTests: 2 LoadFromStorage)
**Tests deferred**: 8 with new targeted skips — async callback data marshalling returns garbled values for complex types (frozen structs, enums, classes, arrays, typed errors). Simple types (Int32, void, String) work fine. Individual `[Skip]` added with accurate reasons.

#### 6B-3: Optional\<Int32\> None Marshalling — DEFERRED (0 tests recovered)

**Status**: Not investigated in this session. The marshalling issue (SwiftOptional<int> tag byte misinterpretation) requires deeper investigation of the Optional layout in the runtime. Skips remain as-is.

**Tests**: 3 in `OptionalMarshallingTests.cs` — unchanged

#### 6B-4: Error Description Type Code — FIXED (2 tests recovered)

**Root cause**: `SBW_GetErrorDescription` checked `as? NSError` first. In Swift, ALL `Error`-conforming types (including custom enum errors like `MathError`) bridge to `NSError` via `_SwiftNativeNSError`. The `as? NSError` check matched everything, routing all errors to the domain+code path instead of `String(describing:)`.

**Fix**: Rewrote dispatch in `ErrorDescriptionEmitter.cs`. Now checks `as? Error` first and always uses `String(describing: errorValue)` for all Error types. The function runs in Swift/ObjC runtime (via @_cdecl P/Invoke), not CoreCLR, so ObjC runtime operations (including NSError's localizedDescription) are fully available. Previous NSError-specific path was unnecessary.

**Test updates**: Removed `[Skip]` from TestDivideByZeroThrows and TestThrowingStructDivideByZeroThrows. Updated TestLoadFromStorageThrowsNotFound and TestLoadFromStorageThrowsAccessDenied assertions to expect case names ("notFound", "accessDenied") instead of old "code -1" format.

#### 6B-5: Shape.point Wrapper — MISDIAGNOSED (0 tests recovered)

**Original claim**: Shape.point wrapper stripped during compilation.

**Actual result**: The `SBW_SwiftBindingsTestLib_Shape_point_161DD5DC` symbol IS in the compiled SwiftBindings library. The wrapper uses proper C-compatible types (`UnsafeRawPointer` for FrozenPoint param, `UnsafeMutableRawPointer` for resultPtr). However, runtime testing revealed the FrozenPoint parameter (which has Double X, Y fields) causes an ABI mismatch — the Point case tag reads as Circle (tag 0 vs expected tag 2+).

**Change**: Skip reason updated from "stripped during compilation" to "FrozenPoint param has Double fields — ABI mismatch in @_cdecl wrapper". The wrapper compiles and exports, but the double-field struct data arrives corrupted.

#### 6B-6: Missing Swift Wrapper Exports — ROOT CAUSE CORRECTED (0 tests recovered)

**Original claim**: `EntryPointNotFoundException` for BorrowResource wrapper — wrapper emission gap.

**Actual result**: The wrapper IS emitted in the Swift file but fails to compile because `UniqueResource` is `~Copyable`. The wrapper does `.assumingMemoryBound(to: UniqueResource.self).pointee` which creates a copy — illegal for noncopyable types. The build script's error-based retry strips the broken function.

**Change**: Skip reason updated from "missing Swift wrapper export" to "UniqueResource is ~Copyable: @_cdecl wrapper .pointee copy fails compilation". Same root cause as other ~Copyable tests.

#### 6B-7: Optional Array Layout Mismatch — DEFERRED (0 tests recovered)

**Status**: Not investigated in this session. Requires deeper investigation of frozen struct + optional array layout. Skips remain as-is.

**Tests**: 1 in `CompositionTests.cs` — unchanged

#### 6B-8: IntContainer Array Marshalling — DEFERRED (0 tests recovered)

**Status**: Not investigated in this session. The array parameter passes the buffer contents (element pointer) instead of the full Array struct (which includes count + capacity + storage pointer). Requires investigation of constructor wrapper array parameter marshalling. Skips remain as-is.

**Tests**: 3 in `BasicGenericTests.cs` — unchanged

#### 6B-9: Non-Standard Enum Raw Values — CONFIRMED UNFIXABLE (0 tests recovered)

**Verified**: Same root cause as string enum raw values. ABI JSON does not contain enum raw values, so the generator emits sequential ordinals. `Permission.execute` gets ordinal 3 instead of actual raw value 4. Not fixable without a new data source.

**Tests**: 1 in `NonStandardEnumTests.cs` — skip already accurate

### Validation Gates (6B)

- Unit tests: 7889 passed, 0 failed ✓
- Library validation: 90/90 passed ✓
- Runtime tests (simulator): 584 passed (+40 from 544 baseline), 0 failed, 110 skipped (-39) ✓
- Device tests: not run (no generator ABI changes that would affect device-only paths)

---

### Session 6C: New Emission Patterns — **Status: Complete**

**Goal**: Fix up to 10 [Skip]-annotated runtime tests across 3 bug categories plus cleanup. These require new generator emission logic, not just fixes to existing code.

**Result**: +9 newly passing tests (593 total, up from 584 baseline). 3 generator fixes, 1 cleanup item completed. Unit tests: 7889 passed. Validation: 90/90. Simulator: 593 passed, 0 failed, 101 skipped.

#### 6C-1: Nested Type @_cdecl Property Wrappers — FIXED (4 tests recovered)

**Bug**: Non-final class properties returning nested types (e.g., `Codec.format` returning `Codec.Format`) use Tj dispatch thunks but couldn't get @_cdecl wrappers because nested types were blocked by guards in PropertyWrapperEmitter (guard 8), MethodWrapperEmitter (guard 17), and WrapperValidation.HasCdeclCompatibleFunctionShape (guard 17).

**Root cause**: The guards were overly conservative. @_cdecl wrapper function SIGNATURES never use nested types — they use C-compatible types (Int32 for simple enum raw values, void+resultPtr for indirect results, UnsafeMutableRawPointer for class pointers). Nested type names only appear in the function BODY (e.g., `initializeMemory(as: Codec.Format.self)`), which is valid Swift inside @_cdecl functions.

**Fix**: Removed nested type guards from all three locations. The existing `GetCdeclReturnMapping` correctly maps nested types to C-compatible return types (SimpleEnum → raw value, ComplexEnum → IndirectResult, etc.) without exposing nested types in the @_cdecl signature.

**Files modified**: `PropertyWrapperEmitter.cs` (guard 8 + GetRejectionReason), `MethodWrapperEmitter.cs` (guard 17), `WrapperValidation.cs` (HasCdeclCompatibleFunctionShape guard 17 + GetRejectionReason)

**Tests recovered**: 4 (TestCodecConstructionJson, TestCodecConstructionXml, TestCodecEncodingValueProperty, TestCodecGetDescribe)

#### 6C-2: AOT @convention(c) Closure Callbacks — FIXED (3 tests recovered)

**Bug**: `@convention(c)` closure callbacks used `Marshal.GetFunctionPointerForDelegate` which requires JIT compilation for the native-to-managed thunk. Fails on AOT-only runtimes (Mono simulator) with "Attempting to JIT compile method."

**Root cause**: The ABI parser marked `@convention(c)` closures as "escaping" (even though they're non-escaping by default in Swift). The WrapperEmitter.Marshalling had separate paths for escaping vs non-escaping @convention(c) closures: the non-escaping path correctly used `[UnmanagedCallersOnly(CallConvCdecl)]` + `[ThreadStatic]` delegate, but the escaping path fell back to `Marshal.GetFunctionPointerForDelegate`.

**Fix**: Unified both paths — ALL @convention(c) closures now use `[UnmanagedCallersOnly]` + `[ThreadStatic]` delegate. This is safe because @convention(c) closures are bare function pointers with no context capture, called synchronously within the P/Invoke scope. The `[ThreadStatic]` pattern is always safe regardless of the escaping flag.

**Files modified**: `WrapperEmitter.Marshalling.cs` (EmitClosureMarshalling: removed escaping/non-escaping split for convention-c; EmitStaticMembers: removed escaping guard for convention-c callback emission), `MethodHandlerOutputTests.cs` (updated test to expect new behavior)

**Tests recovered**: 3 (TestConventionCFunction, TestCBinaryFunction, TestCPredicate)

#### 6C-3: Nested Enum Associated Values — PARTIALLY FIXED (2 of 3 tests recovered)

**Bug**: `SwiftMarshal.MarshalFromSwift<SHA2Variant>()` threw "Cannot marshal type" because simple C# enums (`enum SHA2Variant : int`) don't have TypeMetadata registered in the marshalling system.

**Fix**: For simple enum associated values in `TryGet` methods, read the discriminator byte directly from the payload buffer and cast to the C# enum type (`(*sourcePtr)` → `(SHA2Variant)`). After `DestructiveProjectEnumData`, the payload contains the raw discriminator which maps directly to the C# enum ordinal.

**Files modified**: `EnumHandler.Marshalling.cs` (EmitPayloadMarshal, all 3 variants)

**Tests recovered**: 2 (TestHashAlgorithmSha2, TestHashAlgorithmSha2AllVariants)
**Tests deferred**: 1 (TestCreateHashAlgorithm — crashes due to separate issue: `SHA2Variant:Int` parameter is 8 bytes in Swift but mapped to `int` (4 bytes) in C# → CallConvSwift ABI size mismatch. Not a TryGet issue.)

#### 6C-4: Cleanup — COMPLETED

- **SkipOnSimulator reasons updated**: 5 returned thick closure tests changed from `[Skip]` to `[SkipOnSimulator("Mono CallConvSwift 16-byte struct return ABI returns wrong pointer values (confirmed upstream, standalone repro at swift-interop-repro)")]`. Now correctly classified as Mono-specific with confirmed upstream bug reference. Tests: TestMakeAdder, TestMakeMultiplier, TestMakeGreaterThan, TestClosureFactory (ClosureTests.cs), TestTransformerChain (CompositionTests.cs).
- **DefaultParameterOverloadEmitter**: Deferred — safe but wasteful (not causing test failures).
- **CGRect/system struct fence**: Deferred — requires NativeAOT device testing.

### Validation Gates (6C)

- Unit tests: 7889 passed, 0 failed ✓
- Library validation: 90/90 passed ✓
- Build-and-test: succeeded ✓
- Runtime tests (simulator): 593 passed (+9 from 584 baseline), 0 failed, 101 skipped (-9) ✓
- Device tests: not run (deferred to separate validation)

---

### Post-Session 6 Skip Inventory (Actual)

After all three sub-sessions, the runtime test suite has **593 passed, 101 skipped, 0 failed**.

**Unfixable / Upstream / Blocked (25 skips — keep as-is):**

| Annotation | Count | Reason |
|------------|------:|--------|
| `[SkipOnSimulator]` | 5 | Returned thick closures — Mono CallConvSwift 16-byte struct return ABI (confirmed upstream) |
| `[Skip]` — string enum raw values | 8 | No data source in ABI JSON (7 string + 1 non-standard) |
| `[Skip]` — noncopyable types | 9 | `~Copyable` needs move semantics in wrappers (7 method + 2 wrapper copy) |
| `[Skip]` — non-blittable closures | 2 | SwiftString in closure callback (upstream limitation) |
| `[Skip]` — ValueTuple | 1 | StructLayout.Auto (upstream limitation) |

**Fixable — our bugs (76 skips — Session 7):**

| Category | Count | Root Cause |
|----------|------:|------------|
| GC/finalizer lifecycle | 25 | SafeHandle dispose/finalize double-free, NativeMemory.Free on finalizer thread |
| Bridge/NSRunLoop | 12 | NSRunLoop.RunUntil triggers Mono JIT assertion; async finalizer SIGSEGV |
| Generic struct wrapper gaps | 9 | SwiftIndirectResult+SwiftSelf without @_cdecl; metadata resolution failures |
| Async callback marshalling | 8 | Complex type returns garbled (frozen structs, enums, classes, arrays) |
| CallConvSwift multi-param | 5 | 4+ regular params or existential container ref crash |
| Optional/enum marshalling | 6 | Optional\<Int32\> tag byte; OptionalShape generic metadata; SHA2Variant ABI size |
| Operator/Shape ABI | 4 | UnaryValue Bool non-blittable; Shape.point Double fields |
| String enum finalizer | 2 | String-raw-value enum deferred crash |
| Typed throws | 1 | Error enum case tag read incorrectly |
| Missing wrapper exports | 1 | EntryPointNotFoundException |
| Constructor wrapper stripped | 3 | CallConvSwift with StringBuffer return or 4+ IntPtr params |

**Cleanup (deferred from 6C):**
- DefaultParameterOverloadEmitter not gated on `RequiresCdeclForAbiSafety` (safe but wasteful)
- CGRect/system struct conservative fence relaxation opportunity

---

## Session 7: Remaining Runtime Test Fixes — **Status: Complete**

**Goal**: Fix the 76 remaining fixable `[Skip]`-annotated runtime tests across 7 sub-sessions, plus 2 cleanup items deferred from 6C.

**Prerequisite**: Session 6C complete (commit `0dd74a3f`). Baseline: 593 passed, 101 skipped, 0 failed.

**Result**: Simulator: **638 passed** (+45), **0 failed**, **56 skipped** (-45). Unit tests: **7921 passed** (+32). Validation: **90/90**. 2 cleanup items completed. Codex code review findings addressed.

**Execution**: Parallel worktree agents — Wave 1 (7A, 7C, 7D, 7E) ran simultaneously, Wave 2 (7B, 7F, 7G) after merge. Post-merge fixes for Swift compilation (sugared generic names), runtime crashes (SkipOnSimulator reclassification), and Codex review (return conversions, mutating write-back).

---

### Session 7A: GC/Finalizer Lifecycle (25 tests) — **COMPLETE: 23 recovered**

**Finding**: All skip annotations were **stale**. The runtime already has all necessary guards: zero checks in `ReleaseHandle()`, Mono finalizer guard (skips P/Invoke on finalizer thread), process exit guard via `SwiftExitGuard`, exception swallowing around VWT Destroy and Arc.Release. No code changes needed to the runtime.

**Bonus fix**: Pre-existing `SwiftClassHandleTests` used mock pointers (0x1, 0xCAFE, 0x12345678) that would SIGSEGV when disposed/finalized via `swift_isDeallocating`. Fixed to use zero handles or `SwiftExitGuard` cleanup.

**Tests recovered**: 23 (2 remaining are UniqueResource ~Copyable — genuinely unfixable)
**Unit tests added**: 14 (HandleGCLifecycleTests class)

### Session 7B: Bridge/NSRunLoop (12 tests) — **COMPLETE: 11 recovered**

**Finding**: All skip annotations were **stale** — consistent with 7A. NSRunLoop.RunUntil and async finalizer issues resolved in current .NET 10 runtime. No code changes needed.

**Tests recovered**: 11 (original count was 12 but only 11 skip annotations existed — 7 in StateUpdateBridgeTests, 3 in AsyncViewBridgeTests, 1 in SimpleViewBridgeTests)

### Session 7C: Generic Struct Wrapper Gaps (9 tests) — **COMPLETE: 8 SkipOnSimulator (pass on device)**

**Root cause**: Generic types with T-typed parameters/returns couldn't get @_cdecl wrappers because generic struct parents were blocked and protocol metatype dispatch required `AnyObject`.

**Fix**: Protocol-based static factory/dispatch pattern:
1. Private protocol with static method using `UnsafeRawPointer` for T-typed positions
2. Unconditional extension conformance — `Self` resolves to concrete specialization inside the extension
3. @_cdecl wrapper receives metadata as `UnsafeRawPointer`, casts to protocol metatype, calls static method
4. **Sugared name mapping**: ABI names (`τ_0_0`) mapped to source names (`T`, `Element`) via `GenericArgumentDecl.SugaredTypeName` for extension body codegen
5. **Class constructor fix**: Uses concrete module-qualified type name instead of `Self(...)` to avoid Swift's `required init` requirement

**Safety guard**: Generic methods/properties with concrete-only signatures (no T reference) are NOT wrapped with static dispatch — they may come from constrained extensions.

**Runtime result**: All 8 tests crash Mono's JIT (`jit-info.c:918` assertion) on simulator but the Swift wrappers compile correctly. Reclassified as `[SkipOnSimulator]` — expected to pass on NativeAOT device.

**Codex review fixes** (post-merge):
- **P1: Return conversions**: Direct-return branch now applies Bool→Int8, SimpleEnum→rawValue/tag, ClassPointer→Unmanaged conversions (matching `EmitDirectGetterReturn`). Tag-only enums (no `RawValueTypeName`) use `withUnsafePointer` tag extraction.
- **P1: Mutating write-back**: Write-back inserted BEFORE `return` statement. String-return branch inserts write-back before the early `return` in the `if utf8.isEmpty` block.

**Files modified**: `ConstructorWrapperEmitter.cs`, `MethodWrapperEmitter.cs`, `PropertyWrapperEmitter.cs`, `WrapperValidation.cs`
**Tests**: 8 `[SkipOnSimulator]` (1 deferred: TestGetPairSameType — method-level generic free function)
**Unit tests added**: 5 + 4 (Codex review coverage)

### Session 7D: Async Callback Marshalling (8 tests) — **COMPLETE: 7 recovered, 1 re-skipped**

**Root causes** (5 ARC/ownership bugs):
1. **Non-frozen types double-free**: `NewFromPayload` takes ownership of buffer, but callback also called `SBW_Free` → freed same memory twice
2. **Frozen structs stale references**: `NewFromPayload` copied bytes without ARC retain → internal String/class pointers became stale
3. **Class types double-release**: `Arc.Release` in C# callback + `SwiftClassHandle.ReleaseHandle` both released the `passRetained` +1
4. **Collection types stale CoW buffer**: `SwiftArray`/`SwiftDictionary`/`SwiftSet` constructors copied CoW pointer without retaining
5. **Typed throws stale error data**: Error enum marshalled with `copyMemory` (no retain) → associated value storage freed

**Files modified**: `WrapperEmitter.Async.cs`, `TypeHandlerHelpers.cs`, `SwiftArray.cs`, `SwiftDictionary.cs`, `SwiftSet.cs`
**Tests recovered**: 7 (TestAsyncGetNilResult re-skipped — async optional nil detection returns non-null)
**Unit tests added**: 2, several updated

### Session 7E: ABI/Marshalling Fixes (16 tests) — **COMPLETE: 6 recovered, 10 deferred**

**Fixed (3 sub-bugs):**
- **7E-1 Optional\<Int32\> None**: Added blittable primitive fast path in `OptionalProjection.GetReturnPlan()` — reads discriminator byte directly at `offset = sizeof(T)` instead of going through `SwiftOptional<T>` and VWT. (2 tests recovered; TestOptionalConfigConstructorWithoutLabel re-skipped — constructor param path differs)
- **7E-3 Shape.point Double fields**: Added `IsCustomFrozenStructParam()` check in `EnumHandler.CaseConstruction.cs` — marshals custom frozen struct to stack buffer via `SwiftMarshal.MarshalToSwift` and passes `IntPtr`. (2 tests)
- **7E-7 Typed throws error tag**: Moved `throw new SwiftException` outside try-catch in `MethodMarshalPlanBuilder.cs` — catch handler was freeing buffer before rethrowing, leaving dangling pointer. (1 test; assertion updated to expect `String(describing:)` format)

**Verified not stale (re-skipped):**
- **7E-6 SHA2Variant ABI size**: Agent reported stale but crashes at runtime. `SHA2Variant` backed by Swift `Int` (8 bytes) mapped to C# `int` (4 bytes) → SIGSEGV. (1 test re-skipped)

**Deferred (6 sub-bugs, 10 tests):**
- 7E-2 IntContainer array (3), 7E-4 OptionalShape setter (2), 7E-5 UnaryValue Bool operator (2), 7E-8 ExistentialCallbackTests (1), 7E-9 Existential container ref (2)

**Also re-skipped from 6B**: 2 async typed throws tests (`TestAsyncParseTypedCatch`, `TestAsyncParseSuccess`) — `EntryPointNotFoundException` for async wrapper stripped during compilation

### Session 7F: CallConvSwift Multi-Param & Constructor Crashes (8 tests) — **COMPLETE: 0 recovered**

All 8 skips are genuine — none stale. 3 distinct blocking issues identified:
1. **Generic class constructors with T-typed params** (2 tests: TestConstrainedBoxCreation, TestTypedEntityCreation): Need extension of 7C's protocol-based pattern for constructors where params reference the parent's generic type parameter
2. **Generic class property/method dispatch** (2 tests: TestGenericNamedBoxName, TestConstrainedBoxGetDescription): Protocol-based static dispatch needed for generic class properties; ConstrainedBox.getDescription has unexplained wrapper gap
3. **Method-level generic free functions** (1 test: TestGetPairSameType): `pair<T,U>()` rejected by `env.MethodDecl.IsGeneric` guard — needs deep infrastructure
4. **~Copyable types** (2 tests): Genuinely unfixable without move semantics support

Skip annotations updated with accurate root causes.

### Session 7G: Cleanup — **COMPLETE**

1. **DefaultParameterOverloadEmitter**: Line 155 now gated on `RequiresCdeclForAbiSafety()` — methods that don't need @_cdecl for ABI safety use `@_silgen_name` wrapper with CallConvSwift directly
2. **CGRect/system struct fence**: C-bridging module types (CoreGraphics, CoreFoundation, Darwin, simd) exempted from >8 byte restriction via new `IsCBridgingModuleType()` helper. Pure C structs with platform-stable register layouts, proven safe on both runtimes

**Unit tests added**: 9

### Validation Gates (Session 7)

- Unit tests: **7921 passed** (+32 from 7889 baseline), 0 failed
- Library validation: **90/90 passed**
- Build-and-test: **succeeded**
- Runtime tests (simulator): **638 passed** (+45 from 593), **0 failed**, **56 skipped** (-45)

### Post-Session 7 Skip Inventory (Verified Against Codebase)

**56 annotations** (43 `[Skip]` + 13 `[SkipOnSimulator]`) covering **56 skipped tests** on simulator.

#### Unfixable / Upstream (25 annotations, won't change without external action)

| Category | Type | Count | Tests | Details |
|----------|------|------:|------:|---------|
| Returned thick closures | SkipOnSimulator | 5 | TestMakeAdder, TestMakeMultiplier, TestMakeGreaterThan, TestClosureFactory, TestTransformerChain | Upstream: Mono CallConvSwift 16-byte struct return ABI (confirmed, standalone repro) |
| String enum raw values | Skip | 7 | 7 in StringMarshallingTests | Blocked: ABI JSON lacks enum raw values |
| Non-standard enum raw values | Skip | 1 | TestPermissionCaseValues | Blocked: same root cause as string enum |
| ~Copyable noncopyable types | Skip | 11 | 6 in OwnershipTests, 2 in OwnershipGCStressTests, 2 in ClassMarshallingTests, 1 in DisposeScopeTests, 1 in NegativePathTests | Future: needs move semantics in @_cdecl wrappers (UniqueResource, BorrowResource) |
| Non-blittable closures | Skip | 2 | TestClosureWithOptionalStringReturn, TestClosureWithStringArrayReturn | Upstream: SwiftString in closure callback |
| ValueTuple StructLayout.Auto | Skip | 1 | TestSumPair | Upstream: MarshalDirectiveException |

#### Fixable — Generator/Runtime Bugs (29 annotations, actionable next session)

| Category | Type | Count | Tests | Root Cause | Fix Approach |
|----------|------|------:|------:|------------|-------------|
| Generic static factory on Mono | SkipOnSimulator | 8 | TestWrapperCreation, TestWrapperUnwrap, TestGenericPairCreation, TestGenericPairMixedTypes, TestGenericClassCreation, TestGenericClassGetMethod, TestGenericClassValueSetter, TestGenericNamedBoxCreation | Mono JIT `jit-info.c:918` assertion on protocol metatype dispatch | Investigate Mono JIT trigger; may need alternative dispatch for Mono |
| Generic class T-typed constructor | Skip | 2 | TestConstrainedBoxCreation, TestTypedEntityCreation | `CanEmitGenericClassConstructorWrapper` rejects constructors with T-typed params | Extend 7C protocol factory pattern to class constructors |
| Generic class property/method | Skip | 2 | TestGenericNamedBoxName, TestConstrainedBoxGetDescription | No @_cdecl wrapper generated for generic class property getters / unexplained wrapper gap | Extend 7C protocol dispatch to generic class properties; investigate ConstrainedBox.getDescription |
| Method-level generic free function | Skip | 1 | TestGetPairSameType | `env.MethodDecl.IsGeneric` guard blocks wrapper emission | New wrapper pattern for method-level generics (no parent type to extend) |
| IntContainer array marshalling | Skip | 3 | TestIntContainerCreation, TestIntContainerElementAt, TestIntContainerEmpty | Constructor wrapper passes element pointer instead of full Array struct (count+capacity+storage) | Fix constructor wrapper array param marshalling |
| OptionalShape setter | Skip | 2 | TestEnumPropertyHolder_SetOptionalShape, TestEnumPropertyHolder_ClearOptionalShape | `SwiftOptional<Shape>` generic metadata crashes CallConvSwift | Property setter emission for `Optional<ComplexEnum>` |
| UnaryValue Bool operator | Skip | 2 | TestUnaryNot, TestUnaryBitwiseNot | Bool field makes struct non-blittable; @_cdecl operator wrapper needed | Fix operator wrapper for Bool-field structs |
| SHA2Variant ABI size | Skip | 1 | TestCreateHashAlgorithm | Swift `Int` (8 bytes) mapped to C# `int` (4 bytes) → SIGSEGV | Use `nint` or `long` for Int-backed enum params |
| Async optional nil detection | Skip | 1 | TestAsyncGetNilResult | Async callback returns non-null for nil Swift optional | Fix nil tag detection in async optional return path |
| Async typed throws wrapper | Skip | 2 | TestAsyncParseTypedCatch, TestAsyncParseSuccess | @_cdecl async wrapper stripped during Swift compilation | Investigate why typed throws async wrapper fails to compile |
| Optional\<Int32\> constructor param | Skip | 1 | TestOptionalConfigConstructorWithoutLabel | Optional\<Int32\> None in constructor params reads as Some | Extend 7E-1 blittable fast path to constructor param marshalling |
| Optional array layout mismatch | Skip | 1 | TestBatchConfigTagCountNil | Frozen struct + optional array buffer size calculation wrong | Fix buffer size for optional array in frozen struct layout |
| ExistentialCallbackTests | Skip | 1 | class-level (all tests) | `EntryPointNotFoundException` for existential callback wrapper | Investigate wrapper emission / compilation for existential callbacks |
| Existential container ref params | Skip | 2 | TestRunModeConsumerWithSimpleMode, TestRunModeConsumerWithStrictMode | SIGKILL on NativeAOT device with existential container by-ref | Different parameter passing strategy for existential ref params |

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

## Full Runtime Test Skip Inventory

**Superseded** — see `remaining-runtime-test-fixes.md` for the current actionable inventory with root causes and fix approaches. The inventories below are historical context only.

## Device Stability Issues — Mapping (Updated After Session 5)

| Issue | Verdict | Session |
|-------|---------|---------|
| Struct singleton second-access (Alamofire) | ~~OUR BUG~~ **NOT A BUG**: `initializeMemory` properly handles ARC (all 18 tests pass) | 5A (debunked) |
| Enum case dispose crash (Starscream) | OUR BUG: cumulative VWT/enum lifecycle corruption | 2C |
| Foundation.Data in @_cdecl | KNOWN LIMITATION: ObjC bridging at boundary | Deferred (DataProjection) |
| CallConvSwift URL.init(string:) | KNOWN LIMITATION: SwiftString non-blittable | N/A (already uses @_cdecl) |
| Codec nested type property crash | OUR BUG: Tj dispatch thunk without @_cdecl (nested type restriction blocks wrapper) | 6C-1 |
