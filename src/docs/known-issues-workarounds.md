# Known Issues and Workarounds

This document tracks major runtime issues, Mono/Swift interop bugs, and their workarounds. It serves two purposes:

1. **End-user reference** — understand what limitations exist and how they're handled
2. **Revert checklist** — when upstream fixes land in dotnet/runtime, this documents exactly what to undo

---

## Simulator vs Device: When These Issues Apply

All issues documented here are **Mono JIT-specific**. The deployment target determines whether they affect you:

| Target | Runtime | JIT Bugs Apply? | Notes |
|--------|---------|-----------------|-------|
| **iOS Simulator** | Mono (JIT) | **Yes** | Developer inner loop. All workarounds (A–D) are necessary. |
| **iOS Device (NativeAOT)** | RyuJIT (AOT) | **No** | Production App Store builds. NativeAOT uses a completely different codegen — `CallConvSwift` works correctly, no JIT assertion crashes. Workarounds are harmless but unnecessary. |

**Bottom line**: These bugs affect your development experience on the simulator, not your shipped app. Production device builds using NativeAOT (`dotnet publish -r ios-arm64`) bypass the Mono JIT entirely. The workarounds remain in place so that simulator-based development and testing work correctly.

---

## Table of Contents

1. [Mono JIT Assertion Crash (CallConvSwift)](#mono-jit-assertion-crash-callconvswift)
   - [What It Is](#what-it-is)
   - [Workaround A: SwiftString Runtime Wrappers](#workaround-a-swiftstring-runtime-wrappers)
   - [Workaround B: Closure Cdecl Expansion](#workaround-b-closure-cdecl-expansion)
   - [Workaround C: Existential Metadata Wrapper](#workaround-c-existential-metadata-wrapper)
   - [Workaround D: Signature Risk Detection](#workaround-d-signature-risk-detection)
   - [Revert Plan (When Upstream Fix Lands)](#revert-plan-when-upstream-fix-lands)
2. [Non-Blittable Types with Swift Calling Convention](#non-blittable-types-with-swift-calling-convention)
3. [SafeHandle in Async P/Invoke](#safehandle-in-async-pinvoke)
4. [Upstream Bug Report Status](#upstream-bug-report-status)

---

## Mono JIT Assertion Crash (CallConvSwift)

### What It Is

Mono's JIT compiler incorrectly marks `CallConvSwift` P/Invoke frames as "async", then hits the assertion `!ji->async` at `jit-info.c:918` during stack unwinding. **This is a process-fatal abort** — it bypasses all managed exception handlers and kills the application immediately. It is a Mono-specific defect; the called Swift functions are synchronous.

The crash manifests in three distinct categories:

| Category | Trigger | Severity |
|----------|---------|----------|
| **SwiftString operations** | `SwiftString.Length`, `.ToString()`, `new SwiftString()` route through `CallConvSwift` P/Invokes to `libswiftCore` | High |
| **Closure callbacks** | `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]` callbacks invoked by Swift for escaping closures | High |
| **Existential metadata** | `swift_getExistentialTypeMetadata` called via `CallConvSwift` during `SwiftArray<ExistentialContainer>` construction | High |
| **VWT Destroy** | `ValueWitnessTable->Destroy()` indirect function pointer call via `CallConvSwift` during `Dispose()` on structs with reference-type fields | Medium (open) |

Four workarounds (A through D) have been implemented to address these categories. Together they make the generated bindings functional on Mono for the vast majority of Swift APIs.

> **Runtime dependency**: Workarounds A and C require `libSwiftBindingsRuntime.dylib` to be included in the application bundle. Without it, SwiftString operations and existential metadata lookups will throw `SwiftRuntimeException` at runtime.

> **Detailed implementation notes**: For the completed strategies (A–D), see [`Completed/mono-jit-mitigation-strategies.md`](Completed/mono-jit-mitigation-strategies.md). For remaining edge cases, see [`Future/mono-jit-future-work.md`](Future/mono-jit-future-work.md).

---

### Workaround A: SwiftString Runtime Wrappers

**Problem**: Every `SwiftString` operation (construction, `.ToString()`, `.Length`) called directly into `libswiftCore` via `CallConvSwift` P/Invokes, triggering the JIT crash.

**Solution**: Five `@_cdecl` wrapper functions in `libSwiftBindingsRuntime.dylib` that perform SwiftString operations entirely on the Swift side and return results via C-compatible types (`CallingConvention.Cdecl`). The C# runtime attempts the wrapper path first; if the dylib isn't available, it falls back to the direct `CallConvSwift` path (for non-Mono runtimes where the crash doesn't occur).

**What changed**:

| Component | Change |
|-----------|--------|
| `src/Swift.Runtime/src/Swift/SwiftString.cs` | Added `RuntimeNativeMethods` inner class with 5 `CallingConvention.Cdecl` P/Invokes. `ToString()`, `Length`, and constructor use wrapper-first pattern with `_useWrapperPath` static flag and `DllNotFoundException` fallback. |
| `TestFramework/SwiftBindingsRuntime/SwiftBindingsRuntime.swift` | 5 `@_cdecl` exports: `SBW_SwiftString_Create`, `SBW_SwiftString_ToUtf8`, `SBW_SwiftString_GetCount`, `SBW_SwiftString_Destroy`, `SBW_SwiftString_FreeUtf8` |
| `TestFramework/SwiftBindingsRuntime/` (build scripts) | Build pipeline for `libSwiftBindingsRuntime.dylib` |

**Approach**: Pass `IntPtr` pointing to the `SwiftString.Buffer` (16-byte raw representation). Swift side uses `UnsafeRawPointer` + `assumingMemoryBound(to: String.self).pointee` to get a retain-balanced copy. This avoids ABI assumptions about `String`'s internal word layout and works cleanly with Swift 6's `BitwiseCopyable` restrictions.

**What's NOT covered**: `SwiftSafeHandle<T>.ReleaseHandle()` still calls `ValueWitnessTable->Destroy()` via an indirect `CallConvSwift` function pointer. Explicit `Dispose()` on types with non-trivial fields (e.g., a struct containing a `String`) can still crash. The `SBW_SwiftString_Destroy` wrapper exists but isn't wired into the generic `SwiftSafeHandle` path yet.

---

### Workaround B: Closure Cdecl Expansion

**Problem**: When Swift calls back into C# through an escaping closure, the callback function uses `[UnmanagedCallersOnly(CallConvs = CallConvSwift)]`. Mono's JIT processes this frame and crashes.

**Solution**: The generator now emits `CallConvCdecl` callbacks for non-async escaping closures. For each method with closure parameters, a Swift `@_silgen_name` wrapper function is generated that:
1. Accepts `@convention(c)` function pointer + context as separate `IntPtr` parameters (instead of `SwiftClosureData`)
2. Creates a native `@convention(swift)` closure adapter on the Swift side
3. Calls the original Swift method with the adapted closure

This way, Mono only ever sees `CallConvCdecl` — the `@convention(swift)` closure is handled entirely within precompiled Swift code.

**What changed**:

| Component | Change |
|-----------|--------|
| `src/Swift.Bindings/src/Model/TypeDecl/MethodDecl.cs` | Added `HasClosureCdeclWrapper` and `UsesFreeFunctionWrapper` flags |
| `src/Swift.Bindings/src/Marshaler/MonoJitRiskDetector.cs` | Added `NeedsClosureCdeclWrapper()` detection helper |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` | Gated `CallConvCdecl` vs `CallConvSwift` via `useCdecl` parameter |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.Throwing.cs` | Same Cdecl gating |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.IndirectReturn.cs` | Same Cdecl gating |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.SwiftWrapper.cs` | **New file** — shared helpers for Swift wrapper generation + standalone wrapper emission |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` | Emits `(IntPtr funcPtr, IntPtr context)` instead of `SwiftClosureData` when flag is set; `_selfFixed` param for frozen struct value types |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodSignature.cs` | Call-argument mappings for `CdeclClosureFuncPtr`/`CdeclClosureContext` |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Marshalling.cs` | Simplified GCHandle-only marshalling for Cdecl path; passes `useCdecl` to callback emitters |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.cs` | `EmitSwiftSelf` skip for `UsesFreeFunctionWrapper`; `_requiresFixedBlock` for frozen struct self |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Sets flags + emits standalone Swift wrapper between `SignatureHandler`/`WrapperEmitter` construction |

**Scope and exclusions**:
- Only closures with **primitive args/returns** (Int, Bool, Double, Float) get Cdecl wrapping. Non-primitive closures (String, class, struct args) stay on the legacy `CallConvSwift` path.
- **Async methods** are excluded — they already use a safe wrapper-library path.
- **`@convention(c)` closures** are excluded — they already use Cdecl natively.
- **Constructors** are restricted to non-failable frozen struct types (class/non-frozen constructors require indirect return ABI).
- **Opaque return methods** (`some Protocol`) are excluded — combined closure + opaque wrapper is not yet implemented.
- **Wrapper generator paths** (DefaultParam, ArraySlice) are excluded — their `@_silgen_name` wrappers use original function types.

---

### Workaround C: Existential Metadata Wrapper

**Problem**: `swift_getExistentialTypeMetadata` is a Swift runtime function called via `CallConvSwift` when constructing `SwiftArray<ExistentialContainer>`. All three P/Invoke workaround variants (SuppressGCTransition, Cdecl, nint return) failed — the bug is in Mono's JIT frame classification, not in the P/Invoke marshalling.

**Solution**: A `@_cdecl` wrapper in `libSwiftBindingsRuntime.dylib` that calls `swift_getExistentialTypeMetadata` on the Swift side and returns the metadata pointer via `CallingConvention.Cdecl`.

**What changed**:

| Component | Change |
|-----------|--------|
| `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs` | Added `RuntimeNativeMethods.GetExistentialTypeMetadata()` via `CallingConvention.Cdecl`. All resolution paths now use wrapper-only (no crashy fallback). Old workaround P/Invokes retained in file for documentation only. |
| `TestFramework/SwiftBindingsRuntime/SwiftBindingsRuntime.swift` | `@_cdecl("SwiftBindings_GetExistentialTypeMetadata")` wrapper using `@_silgen_name` import of the Swift runtime function |

**Design**: Hard-fail without dylib. Unlike Workaround A (which falls back to direct `CallConvSwift`), this wrapper has no fallback — if `libSwiftBindingsRuntime.dylib` is missing, `GetExistentialTypeMetadata` throws `SwiftRuntimeException`. The direct `CallConvSwift` path is process-fatal on Mono and unnecessary on non-Mono, so fallback provides no benefit.

**Current scope**: Zero-protocol existentials (`Any` / `ExistentialContainer0`) are supported. N-protocol existentials (`ExistentialContainer1`+) require protocol descriptor pointers not yet passed through the wrapper — these throw `SwiftRuntimeException` with a descriptive message.

---

### Workaround D: Signature Risk Detection

**Problem**: The generator had no mechanism to detect which method signatures would trigger the Mono JIT crash. Wrapper routing was based on feature flags (`IsAsync`, `UsesWrapperLibrary`), not on signature analysis.

**Solution**: `MonoJitRiskDetector` — a static analysis pass that flags methods with risky patterns. This is **informational only** (sets `MethodDecl.DetectedJitRisks` flags) and does not directly control P/Invoke routing. It's consumed by Workaround B to decide which methods need closure Cdecl wrappers.

**What changed**:

| Component | Change |
|-----------|--------|
| `src/Swift.Bindings/src/Marshaler/MonoJitRiskDetector.cs` | `AnalyzeMethod()` returns flags enum, `NeedsClosureCdeclWrapper()` consumed by Workaround B |
| `src/Swift.Bindings/src/Model/TypeDecl/MethodDecl.cs` | Added `DetectedJitRisks` property |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/IHandler.cs` | `ApplyRiskDetection()` called in `HandleBaseDecl` before every method emission |

**Detected patterns**: Closure parameters (non-`@convention(c)`), existential parameters, SwiftString returns — including Optional-wrapped variants of each.

---

### Revert Plan (When Upstream Fix Lands)

When the Mono JIT `CallConvSwift` bug is fixed in dotnet/runtime, these workarounds can be reverted. They should be reverted in reverse order (D → C → B → A) to maintain test stability at each step.

#### Phase 1: Remove Risk Detection (Workaround D)

**Low risk — informational only, no behavioral change.**

1. Delete `MonoJitRiskDetector.cs`
2. Remove `DetectedJitRisks` property from `MethodDecl.cs`
3. Remove `ApplyRiskDetection()` call from `IHandler.cs:HandleBaseDecl()`
4. Delete `MonoJitRiskDetectorTests.cs`
5. Verify: `./run-tests.sh` passes, `./build-and-test.sh` 94/94

#### Phase 2: Remove Closure Cdecl Expansion (Workaround B)

**Medium risk — changes generated code output. Run full test suite after.**

1. Remove `HasClosureCdeclWrapper` and `UsesFreeFunctionWrapper` properties from `MethodDecl.cs`
2. Remove `NeedsClosureCdeclWrapper()` from `MonoJitRiskDetector.cs` (or entire file if Phase 1 already done)
3. Delete `ClosureEmitter.SwiftWrapper.cs`
4. Remove `useCdecl` parameter and all Cdecl-gated code from:
   - `ClosureEmitter.cs` — revert to unconditional `CallConvSwift`
   - `ClosureEmitter.Throwing.cs` — same
   - `ClosureEmitter.IndirectReturn.cs` — same
5. Remove `CdeclClosureFuncPtr`/`CdeclClosureContext` handling from:
   - `PInvokeEmitter.cs` — revert to unconditional `SwiftClosureData`
   - `MethodSignature.cs` — remove type markers and call-argument mappings
6. Remove `UsesFreeFunctionWrapper` checks from:
   - `PInvokeEmitter.cs:HandleSwiftSelf()` — remove the `_selfFixed` / explicit `IntPtr` self branch
   - `WrapperEmitter.cs:EmitSwiftSelf()` — remove the early return
   - `WrapperEmitter.cs` constructor — remove `_requiresFixedBlock` extension for `UsesFreeFunctionWrapper`
7. Remove flag-setting code from `MethodHandler.cs` (both constructor and method paths)
8. Remove `WrapperEmitter.Marshalling.cs` Cdecl-gated marshalling branch — revert to unconditional `SwiftClosureData` path
9. Delete `ClosureCdeclEmitterTests.cs`
10. Verify: `./run-tests.sh`, `cd TestFramework && ./build-and-test.sh` (94/94), `./validate-libraries.sh --filter Nuke`

#### Phase 3: Remove Existential Metadata Wrapper (Workaround C)

**Low risk — runtime-only change.**

1. In `TypeMetadata.cs`: Remove `RuntimeNativeMethods.GetExistentialTypeMetadata()` and `TryGetExistentialTypeMetadataViaWrapper()`. Restore the direct `CallConvSwift` P/Invoke as the primary resolution path.
2. Remove `SwiftBindings_GetExistentialTypeMetadata` from `SwiftBindingsRuntime.swift`
3. Delete `ExistentialMetadataWrapperTests.cs`
4. Update `run-runtime-tests.sh` if it has special dylib injection logic
5. Verify: `./run-tests.sh`, runtime tests on iOS Simulator with existential metadata

#### Phase 4: Remove SwiftString Wrappers (Workaround A)

**Low risk — runtime-only change.**

1. In `SwiftString.cs`: Remove `RuntimeNativeMethods` inner class and `_useWrapperPath` flag. Revert `ToString()`, `Length`, and constructor to direct `CallConvSwift` P/Invokes (the original code is still present as the fallback path).
2. Remove `SBW_SwiftString_*` functions from `SwiftBindingsRuntime.swift`
3. Delete `SwiftStringWrapperTests.cs`
4. If `libSwiftBindingsRuntime.dylib` has no remaining exports, remove the build pipeline and dylib injection from `run-runtime-tests.sh`
5. Verify: `./run-tests.sh`, runtime tests on iOS Simulator with SwiftString operations

#### Post-Revert Cleanup

- Remove `[CrashRisk]` attributes from test classes that were marked due to JIT crashes
- Promote Tier 3 tests (MutableProps dispose, etc.) back to Tier 2
- Update `Future/mono-jit-future-work.md` status to "Resolved upstream"
- Update this document

---

## Non-Blittable Types with Swift Calling Convention

**Severity**: Medium — Affects specific API patterns
**Status**: Documented limitation (requires upstream .NET changes)
**Affects**: P/Invoke calls with complex types and `CallConvSwift`

### Symptoms

```
System.InvalidProgramException: Cannot use non-blittable types with Swift calling convention
```

### Root Cause

.NET's implementation of `CallConvSwift` requires all parameters and return types to be blittable (directly mappable to native memory without marshalling). Types like `SwiftOptional<T>`, `SafeHandle` derivatives, and managed strings are not blittable.

### Affected Scenarios

1. **URL.AbsoluteString property** — Returns `SwiftOptional<SwiftString>` which requires marshalling
2. **Methods returning Optional types** — Need special handling
3. **SafeHandle parameters in async contexts** — See separate section below

### Workarounds

**For Optional returns**: Use wrapper methods that handle the marshalling on the Swift side.

**For general non-blittable types**:
- Use `IntPtr` in P/Invoke signatures and marshal manually
- Use Swift wrappers when marshalling is too complex

### Related Files

| File | Purpose |
|------|---------|
| `src/Swift.Runtime/src/Swift/URL.cs` | URL type with workarounds |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Non-blittable handling |

---

## SafeHandle in Async P/Invoke

**Severity**: Medium — Requires workaround for async instance methods
**Status**: Workaround implemented (singleton pattern, IntPtr conversion)
**Affects**: Async instance methods on Swift classes

### Symptoms

Async instance methods crash or behave incorrectly when the `self` parameter is passed as a `SafeHandle`.

### Root Cause

The .NET runtime does not support passing `SafeHandle` (or derivatives like `SwiftSafeHandle<T>`) through P/Invoke with Swift calling convention in async contexts. The Task continuation mechanism doesn't properly preserve the handle reference.

### Workarounds Applied

**Singleton Pattern Detection**: For classes with a `shared` static property, the generator automatically detects the singleton pattern and uses `ClassName.shared.method()` in Swift wrappers.

**IntPtr Conversion** (for non-singletons): Swift wrappers use `unsafeBitCast(_self, to: ClassName.self)` to convert the raw pointer back to a class instance.

### Impact

- Singleton classes (like `ImagePipeline`) work correctly with async methods
- Non-singleton classes may have edge cases with certain class hierarchies
- Proper fix requires .NET runtime changes to support `SwiftSelf` register with async Task closure capture

### Related Files

| File | Purpose |
|------|---------|
| `src/Swift.Bindings/src/Model/TypeDecl/TypeDecl.cs` | `HasSingletonPattern` detection |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Async wrapper generation |

---

## Upstream Bug Report Status

**Date**: February 2026
**Draft location**: `Future/upstream-bug-reports-draft.md`

Three Mono runtime issues have been documented with minimal reproduction cases, ready to file on [dotnet/runtime](https://github.com/dotnet/runtime). No existing reports were found for any of these (searched Feb 2026).

| # | Issue | Filing Strategy | Status |
|---|-------|-----------------|--------|
| 1 | `jit-info.c:918` JIT assertion crash with `CallConvSwift` | **Bug report** — clear-cut JIT defect with assertion failure | Draft ready |
| 2 | Non-blittable types rejected with `CallConvSwift` | **Feature request** — likely intentional scope limitation | Draft ready |
| 3 | SafeHandle not preserved across async P/Invoke | **Comment on tracking issue** — unclear if supported scenario | Draft ready |

**Related dotnet/runtime tracking issues**:
- [#93631](https://github.com/dotnet/runtime/issues/93631) — Runtime support for Swift Interop in .NET 9
- [#108662](https://github.com/dotnet/runtime/issues/108662) — Runtime support for Swift Interop in .NET 10
- [#64215](https://github.com/dotnet/runtime/issues/64215) — Introduce `CallConvSwift`

**Next step**: File after swift-bindings repo is public (so issues can link to concrete repro code).

---

## Primitive/ObjC Types Cannot Satisfy ISwiftObject Constraint

**Severity**: Low — Affects ~5 methods across all validated libraries
**Status**: Documented limitation (by design)
**Affects**: Methods where a bound generic type parameter is instantiated with a primitive or ObjC-bridged type

### Symptoms

Methods are skipped with:
```
Type argument 'Swift.Int' does not satisfy constraint 'SomeProtocol' on 'Container'.
```

### Root Cause

Some Swift APIs use generic types (e.g., `Container<T>`) where `T` is constrained to a protocol. When `T` is instantiated with a primitive type (`Int`, `Bool`, `Double`) or an ObjC-bridged type (`String`, `URL`), the C# binding cannot satisfy the constraint because these types map to .NET primitives (`System.Int64`, `System.Boolean`) or ObjC interop types (`NSUrl`) that do not implement `ISwiftObject`.

### Why This Cannot Be Fixed

C# generic constraints require `where T : ISwiftObject` for Swift interop types. Primitive types like `System.Int64` fundamentally cannot implement this interface. This is a design boundary between Swift's universal generics and C#'s constrained generics.

### Affected Patterns

| Pattern | Example | Count |
|---------|---------|-------|
| Primitive type arg | `Container<Int>` → `Container<System.Int64>` | ~3 |
| ObjC-bridged type arg | `Container<URL>` → `Container<NSUrl>` | ~2 |

### Workaround

No workaround — these methods cannot be bound. Use the equivalent non-generic Swift API directly if available, or write a Swift wrapper that hides the generic constraint.
