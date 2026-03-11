# Design: Universal @_cdecl Wrapper Emission

> Eliminate all CallConvSwift runtime crashes by routing every P/Invoke through `@_cdecl` wrapper functions in the wrapper xcframework. This is the single highest-impact stability improvement for SwiftBindings.

---

## Problem Statement

The .NET runtime's `CallConvSwift` support has bugs on both Mono JIT (iOS Simulator) and NativeAOT (iOS Device):

| Runtime | Symptom | Severity |
|---------|---------|----------|
| Mono JIT | `jit-info.c:918` assertion — process-fatal abort | High |
| NativeAOT | SIGABRT/SIGSEGV on certain type patterns (enum returns, struct inits) | High |
| Both | Non-blittable types rejected (`SwiftOptional<T>`, `SafeHandle`, `NSUrl`) | Medium |
| NativeAOT | VWT Destroy indirect function pointer crash on `Dispose()` | High |

These crashes are **unpredictable by type pattern**. From real-world device testing (Nuke, Lottie):
- `LoopMode` enum getter **works**, but `Priority` enum getter **crashes**
- `OptionsValue` struct return **works**, but `ImageDecoders.Empty` constructor **crashes**
- `Play()` void method **works**, but `Play(from, to, loopMode, completion)` **crashes**

We cannot reliably predict which type combinations will crash, making targeted fixes a game of whack-a-mole.

Meanwhile, **every code path that uses `@_cdecl` wrappers works perfectly** — async methods, closures, SwiftString operations, existential metadata. Zero crashes across hundreds of tests and real-world library usage.

### Current State: Four Workarounds

We have implemented four workarounds (A–D) in `known-issues-workarounds.md` to handle Mono JIT crashes:

| Workaround | What it does | Scope |
|-----------|-------------|-------|
| A: SwiftString wrappers | `@_cdecl` functions in `libSwiftBindingsRuntime.dylib` | SwiftString only |
| B: Closure Cdecl expansion | `@_silgen_name` wrappers for closure parameters | Primitive closures only |
| C: Existential metadata wrapper | `@_cdecl` function in `libSwiftBindingsRuntime.dylib` | Existential metadata only |
| D: Signature risk detection | `MonoJitRiskDetector` flags risky signatures | Informational only |

These workarounds address Mono JIT crashes for specific patterns. They do **not** address NativeAOT device crashes (Issues 2, 7, 9 in KNOWN-ISSUES). Universal `@_cdecl` supersedes all four.

---

## Solution: Universal `@_cdecl` Wrapper Emission

### Core Principle

**Every C# P/Invoke calls a `@_cdecl` wrapper function in the wrapper xcframework, using `CallingConvention.Cdecl`. No generated binding uses `CallConvSwift` at the P/Invoke boundary.**

The wrapper function is precompiled Swift code that:
1. Receives C-compatible parameters via C calling convention
2. Reconstructs Swift types from raw pointers/values
3. Calls the real Swift API
4. Marshals the result back to C-compatible types

Since the wrapper is compiled Swift, it handles the Swift ABI internally — the .NET runtime never needs to understand Swift calling convention.

### Architecture: Before and After

**Before (current):**
```
C# code
  → [DllImport("Nuke", CallConvSwift)]
  → Swift ABI directly (registers x20/x21 for self/error)
  → Crashes on certain type patterns
```

**After:**
```
C# code
  → [DllImport("NukeSwiftBindings", Cdecl)]
  → @_cdecl wrapper function (C calling convention)
  → wrapper calls Swift API internally (Swift ABI handled in precompiled code)
  → Always works
```

### Concrete Example

**Property getter — current:**
```csharp
// C# (generated)
[DllImport("Nuke")]
[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
private static extern int PInvoke_priority_Get_46149C9C(SwiftSelf self);

public ImageRequest.Priority PriorityValue {
    get => (ImageRequest.Priority)PInvoke_priority_Get_46149C9C(SwiftSelf);
}
```
Result: **SIGABRT on device** (Issue 7).

**Property getter — after:**
```csharp
// C# (generated)
[DllImport("NukeSwiftBindings", CallingConvention = CallingConvention.Cdecl)]
private static extern int SBW_Get_Nuke_ImageRequest_priority(IntPtr self);

public ImageRequest.Priority PriorityValue {
    get => (ImageRequest.Priority)SBW_Get_Nuke_ImageRequest_priority(Handle);
}
```

```swift
// Swift wrapper (generated into wrapper xcframework)
@_cdecl("SBW_Get_Nuke_ImageRequest_priority")
public func _sbw_get_priority(_ self_: UnsafeRawPointer) -> Int32 {
    let obj = Unmanaged<Nuke.ImageRequest>.fromOpaque(self_).takeUnretainedValue()
    return Int32(obj.priority.rawValue)
}
```
Result: **Works on both Mono JIT and NativeAOT.**

### Dispose / VWT Destroy Example (Issue 2)

**Current:**
```csharp
// SwiftSafeHandle<T>.ReleaseHandle()
metadata.ValueWitnessTable->Destroy(handle);  // indirect CallConvSwift → SIGSEGV
```

**After:**
```csharp
// SwiftSafeHandle<T>.ReleaseHandle()
SBW_Destroy_Nuke_ImageRequest(handle);  // CallingConvention.Cdecl → works
```

```swift
@_cdecl("SBW_Destroy_Nuke_ImageRequest")
public func _sbw_destroy_ImageRequest(_ ptr: UnsafeMutableRawPointer) {
    ptr.assumingMemoryBound(to: Nuke.ImageRequest.self).deinitialize(count: 1)
    ptr.deallocate()
}
```

---

## Type Marshalling Reference

Every type that crosses the C#/Swift boundary needs a C-compatible representation at the `@_cdecl` function signature.

### Parameters (C# → Swift)

| Swift Type | C# P/Invoke Type | Swift Wrapper Receives | Conversion in Wrapper |
|-----------|------------------|----------------------|----------------------|
| `Int`, `Int32`, `Int64` | `int`, `long` | `Int32`, `Int64` | Direct |
| `Double`, `Float` | `double`, `float` | `Double`, `Float` | Direct |
| `Bool` | `byte` (MarshalAs U1) | `Int32` (0/1) | `value != 0` |
| Simple enum (frozen, Int raw) | `int` | `Int32` | `EnumType(rawValue:)` |
| Complex enum (associated values) | `IntPtr` to buffer | `UnsafeRawPointer` | `assumingMemoryBound(to:).pointee` |
| `String` | `IntPtr` + `Int32` (Utf8Slice) | `UnsafePointer<UInt8>` + `Int32` | `String(bytes:encoding:.utf8)` |
| Class instance (self) | `IntPtr` | `UnsafeRawPointer` | `Unmanaged<T>.fromOpaque().takeUnretainedValue()` |
| Struct instance (self) | `IntPtr` to buffer | `UnsafeRawPointer` | `assumingMemoryBound(to:).pointee` |
| Optional\<T\> (value type) | `IntPtr` to optional buffer | `UnsafeRawPointer` | `load(as: Optional<T>.self)` |
| Optional\<T\> (class) | `IntPtr` (0 = nil) | `UnsafeRawPointer?` | `Unmanaged<T>.fromOpaque()` or nil |
| `SwiftArray<T>` | `IntPtr` | `UnsafeRawPointer` | `Unmanaged<NSArray>.fromOpaque()` or typed load |
| Closure | `IntPtr` funcPtr + `IntPtr` context | C function pointer + context | Already handled by ClosureEmitter |

### Returns (Swift → C#)

| Swift Type | C# P/Invoke Return | Swift Wrapper Returns | Conversion in Wrapper |
|-----------|-------------------|---------------------|----------------------|
| `Int`, `Double`, `Float` | Direct | Direct | None |
| `Bool` | `byte` | `Int32` | `value ? 1 : 0` |
| Simple enum (frozen, Int raw) | `int` | `Int32` | `.rawValue` |
| Complex enum (associated values) | Write to `IntPtr` out-buffer | `Void` (writes to `UnsafeMutableRawPointer` param) | `initializeMemory(as:to:)` |
| `String` | `SBW_Utf8Slice` (ptr + len) | `SBW_Utf8Slice` | Allocate + copy UTF-8 bytes |
| Class instance | `IntPtr` | `UnsafeRawPointer` | `Unmanaged.passRetained().toOpaque()` (+1 retain) |
| Struct (frozen, blittable) | Write to `IntPtr` out-buffer | `Void` (writes to buffer) | `storeBytes(of:as:)` |
| Struct (non-frozen / ref fields) | `IntPtr` (heap allocated) | `UnsafeRawPointer` | Allocate + initialize |
| Optional\<T\> (value type) | Write to out-buffer + `byte` null flag | `Void` + `Int32` flag | Check nil, write value if present |
| Optional\<T\> (class) | `IntPtr` (0 = nil) | `UnsafeRawPointer?` | `passRetained` or return nil (0) |
| Tuple | Write to out-buffer (flattened) | `Void` (writes to buffer) | Store each element at offset |
| `Void` | `void` | `Void` | None |

### Existing Patterns We Reuse

These marshalling patterns already exist in the codebase and are proven:

| Pattern | Where it's used today | Status |
|---------|----------------------|--------|
| Utf8Slice for String returns | `Utf8SliceEmitter`, `WrapperEmitter.Async.cs` | Production, all libraries |
| `Unmanaged.passRetained` for class returns | `WrapperEmitter.Async.cs` (async class returns) | Production |
| Buffer pointer for struct self | `ObjCOverridePropertyWrapperEmitter` | Production |
| Optional null flag + value | `WrapperEmitter.Return.cs` (async optional returns) | Production |
| Enum as raw int | `EnumHandler` (simple enum projection) | Production |
| `@_cdecl` function emission | `ExistentialBypassEmitter`, `SwiftBindingsRuntime` | Production |
| Per-type symbol naming | `EmitterUtility.DeterministicHash8` | Production |

**Key insight**: We are not inventing new marshalling patterns. Every type conversion in the table above has a working implementation somewhere in the codebase. Universal `@_cdecl` is about applying these patterns uniformly.

---

## Session Workflow

Each phase follows this workflow:

1. **Plan session** — Enter plan mode. Investigate the codebase, trace call chains, identify exact insertion points, and produce a detailed implementation plan. Have the plan reviewed externally (Codex).
2. **Clear context** — Start fresh implementation session with only the plan.
3. **Implement session** — Execute the plan. The plan contains all file paths, line numbers, and patterns needed — no exploratory reading required.
4. **Review** — Have Codex review the implementation.
5. **Commit** — After review approval.

The planning checklists and reference patterns below are designed to make each plan session efficient and each implementation session self-contained.

---

## Implementation Phases

### Phase 1: Property Accessor Wrappers [COMPLETE]

**Sessions**: 1 plan + 3 implement (plan + implement + 2 review-fix sessions)
**Goal**: Route all property getters and setters through `@_cdecl` wrappers.
**Fixes**: Issue 7 (enum/string property crashes on device), partially Issue 2 (property access after wrapper also needs Destroy).
**Scope**: ~60% of all P/Invokes in typical bindings.
**Status**: Complete. All validation gates pass (7147 unit tests, 90/90 library validation, TestFramework clean).

#### Planning Checklist

During the plan session, investigate and document:

1. **Property emission call chain** — Trace `PropertyHandler.Emit()` → accessor `MethodEnvironment` creation → `PInvokeEmitter.EmitPInvoke()`. Understand how property accessors become P/Invoke declarations.
   - Entry: `PropertyHandler.cs:64` (`Emit()`)
   - MethodEnvironment: created at `PropertyHandler.cs:370-373` for each accessor
   - P/Invoke: `PInvokeEmitter.cs:681-734` (`EmitPInvoke()`)

2. **Existing property wrapper pattern** — Read `ObjCOverridePropertyWrapperEmitter.cs` (152 lines). This already emits `@_silgen_name` getter/setter wrappers for ObjC override properties. The new universal wrapper follows the same structural pattern but uses `@_cdecl` and covers all properties.
   - Symbol naming: `ObjCOverridePropertyWrapperEmitter.cs:65-70`
   - Getter emission: `ObjCOverridePropertyWrapperEmitter.cs:76-109`
   - Setter emission: `ObjCOverridePropertyWrapperEmitter.cs:115-150`
   - Dedup: `ctx.TryAddObjCPropertyWrapperSymbol()` via `ModuleEmissionContext.cs:238-246`

3. **Constructor wrapper pattern** — Read `ConstructorWrapperEmitter.cs` (185 lines). This is the most complete `@_cdecl` wrapper implementation. Study `GetCdeclParamMapping()` (lines 407-579) — it maps every type category to a C-compatible parameter. This is the primary reference for type marshalling.
   - Guard pattern: `ConstructorWrapperEmitter.cs:25-76` (8 guards)
   - Symbol naming: `ConstructorWrapperEmitter.cs:206-211`
   - Dedup: `ctx.TryAddConstructorWrapperSymbol()`
   - @MainActor: `ConstructorWrapperEmitter.cs:339-342`
   - Parameter mapping: `ConstructorWrapperEmitter.cs:407-579` — **this is the Rosetta Stone for type conversion**

4. **CallingConvention routing** — Understand how `PInvokeCallingConvention.Cdecl` vs `.Swift` is selected.
   - Enum: `PInvokeEmitHelper.cs:9-12`
   - Selection: `PInvokeEmitter.cs:729-731` — currently only `UsesCdeclConstructorWrapper` triggers Cdecl
   - Formatting: `PInvokeEmitHelper.cs:90-149` — converts enum to `[UnmanagedCallConv]` attribute

5. **Library path routing** — Understand how P/Invokes target the wrapper lib vs native dylib.
   - `PInvokeEmitter.ComputeEntryPoint()`: lines 648-673 — `needsWrapperLib` flag
   - Library selection: `PInvokeEmitter.cs:689-691` — `AsyncLibraryName` vs `moduleLibPath`

6. **Generic type constraints** — `PInvokeHelperContext` is used for generic types because `[DllImport]` can't appear in generic classes (CS7042). Property wrappers must handle this.
   - Creation: `PInvokeHelperEmitter.cs:50-62`
   - Usage: `PInvokeEmitter.cs:697-714`

7. **Swift wrapper emission** — Understand how Swift code gets written to the wrapper file.
   - Writer: `SwiftWriter` passed through handler chain
   - Existing Swift emission in `ConstructorWrapperEmitter.cs:222-400`

8. **Decide on implementation approach** — Key decisions to make during planning:
   - Create new `PropertyWrapperEmitter.cs` (like `ConstructorWrapperEmitter.cs`) or extend `ObjCOverridePropertyWrapperEmitter.cs`?
   - Where to hook into `PropertyHandler.Emit()` — before or after existing accessor emission?
   - How to handle the `UseDirectSwiftPInvoke` flag from the start?

#### Reference Patterns

| What to implement | Follow this pattern | File:Lines |
|------------------|--------------------|----|
| Swift getter/setter wrapper functions | `ObjCOverridePropertyWrapperEmitter` getter/setter | `ObjCOverridePropertyWrapperEmitter.cs:76-150` |
| Per-type C-compatible parameter mapping | `ConstructorWrapperEmitter.GetCdeclParamMapping()` | `ConstructorWrapperEmitter.cs:407-579` |
| `@_cdecl` symbol naming + dedup | `ConstructorWrapperEmitter` symbol + `TryAdd` | `ConstructorWrapperEmitter.cs:206-211`, `ModuleEmissionContext.cs:228-236` |
| C# Cdecl P/Invoke declaration | Constructor wrapper P/Invoke | `PInvokeEmitter.cs:729-731`, `PInvokeEmitHelper.cs:90-149` |
| Guards (generic, closure, async skip) | Constructor wrapper guards | `ConstructorWrapperEmitter.cs:25-76` |
| Return type marshalling (string) | `Utf8SliceEmitter` + async string return | `Utf8SliceEmitter.cs:23-74`, `WrapperEmitter.Async.cs` |
| Return type marshalling (class) | Async class return (passRetained) | `WrapperEmitter.Async.cs` |
| @MainActor annotation | Constructor wrapper @MainActor | `ConstructorWrapperEmitter.cs:339-342` |

#### Implementation Scope

**Files to create:**
- `PropertyWrapperEmitter.cs` (~300-400 lines) — new emitter following `ConstructorWrapperEmitter` pattern

**Files to modify:**
- `PropertyHandler.cs` — Hook new emitter into getter/setter emission (~20-30 lines changed)
- `PInvokeEmitter.cs` — Extend Cdecl routing for property accessors (~15-20 lines)
- `ModuleEmissionContext.cs` — Add property wrapper symbol dedup set (~10 lines)
- `PInvokeEmitHelper.cs` — Minor: ensure Cdecl formatting handles property self-as-IntPtr (~5 lines)

**Files to create (tests):**
- `PropertyWrapperEmitterTests.cs` (~800-1200 lines) — follow `ConstructorWrapperEmitterTests.cs` (2,021 lines) pattern

**Estimated new code**: ~1,200-1,600 lines (emitter + tests)

#### Validation Gate

- [x] All existing unit tests pass (`./run-tests.sh`) — 7147 tests, 0 failures
- [x] All property getter/setter P/Invokes in generated code use `CallingConvention.Cdecl`
- [x] No `CallConvSwift` remains in property accessor P/Invokes
- [x] All 90/90 library validation passes (`./validate-libraries.sh`)
- [ ] TestFramework NativeAOT tests pass (`./run-nativeaot-tests.sh`) — requires device
- [ ] Nuke `ImageRequest.PriorityValue` getter works on device (was Issue 7 crash) — requires device

---

### Phase 2: Method Wrappers [COMPLETE]

**Sessions**: 1 plan + 2 implement (plan + implement + review-fix session)
**Goal**: Route all instance methods and static methods through `@_cdecl` wrappers.
**Fixes**: Issue 9 (non-blittable params like NSUrl), remaining Issue 7 method crashes on device.
**Scope**: Remaining ~40% of non-async, non-bridge, non-accessor method P/Invokes. Subscript accessors deferred (separate string decode/encode logic in SubscriptHandler).
**Status**: Complete. All validation gates pass (6914 unit tests, 90/90 library validation, TestFramework clean).

#### Implementation Summary

**Files created:**
- `MethodWrapperEmitter.cs` (~450 lines) — `ShouldEmitWrapper()` (20 guards), `GetMethodSymbolName()`, `EmitSwiftMethodWrapper()`
- `MethodWrapperEmitterTests.cs` (~1,000 lines) — 38 tests covering guards, symbols, emission, computed properties

**Files modified:**
- `MethodDecl.cs` — Added `UsesCdeclMethodWrapper` flag, expanded `UsesCdeclWrapper` computed property
- `ModuleEmissionContext.cs` — Added `_methodWrapperSymbols` dedup HashSet
- `MethodHandler.cs` — Flag-setting block + Swift wrapper emission + SBW_Free P/Invoke for string returns
- `PInvokeEmitter.cs` — Extended `HandleSwiftSelf()` for method wrapper IntPtr self
- `WrapperEmitter.cs` — Extended `_requiresFixedBlock` for frozen struct instance method self
- `MarshallingHelpers.cs` — Extended `MethodRequiresIndirectResult()` for method wrappers + void return guard
- `MethodMarshalPlanBuilder.cs` — Skip SwiftSelf for method wrappers
- `WrapperEmitter.Return.cs` — String decode+free for method returns, `resultPtr` vs `swiftIndirectResult` routing, blittable indirect result fallback
- `DefaultParameterOverloadEmitter.cs` — Method wrapper @_cdecl on top of @_silgen_name overloads (checks original method flag to avoid `UsesWrapperLibrary` guard conflict)

**Key design decisions:**
- Self parameter patterns: Class (`Unmanaged.fromOpaque`), Struct non-mutating (`load(as:)`), Struct mutating (`assumingMemoryBound.pointee`), Static (none)
- String returns: Full inline decode+free via `SBW_Utf8Slice` (unlike properties which use two-stage accessor→PropertyHandler decode)
- Actor types excluded (require async context); @MainActor methods get `@MainActor` annotation on wrapper
- Default-parameter overloads inherit cdecl flag from original method (not re-evaluated via `ShouldEmitWrapper`)

#### Validation Gate

- [x] All non-async, non-bridge, non-accessor method P/Invokes use `CallingConvention.Cdecl`
- [x] `./run-tests.sh` — 6914 unit tests pass, 0 failures
- [x] `./validate-libraries.sh --tier all` — 90/90 pass
- [x] TestFramework: C# compile-check pass, Swift wrapper compile pass
- [ ] Nuke `ImagePipeline.ImageTask(NSUrl)` works on device (was Issue 9) — requires device
- [ ] Lottie `LottieAnimation.From(data, strategy)` works on device (was Issue 7) — requires device

---

### Phase 3: Destroy Wrappers + DllImport Resolver [COMPLETE]

**Sessions**: 1 implement
**Goal**: Fix `Dispose()` crash (Issue 2) and eliminate consumer DllImport boilerplate (Issue 4).
**Fixes**: Issue 2 (VWT Destroy SIGSEGV), Issue 4 (late-loaded assembly resolver).
**Status**: Complete. All validation gates pass (6915 unit tests, 90/90 library validation, TestFramework clean).

#### Part A: Destroy Wrappers

**Verified complete from earlier device validation work.** `DestroyWrapperEmitter.cs` (157 lines) with full `@_cdecl` destroy wrapper emission, all 4 type handlers wired up, runtime `RegisterDestroyAction()` + `ReleaseHandle()` working, 29 unit tests passing. No additional code changes needed.

#### Part B: DllImport Resolver Centralization

**Implementation summary:**

| File | Change |
|------|--------|
| `SwiftFrameworkResolver.cs` (new, runtime) | Centralized `RegisterForAssembly()` with idempotent try-catch around `SetDllImportResolver` |
| `ModuleHandler.cs` (generator) | Generated `[ModuleInitializer]` now calls `global::Swift.Runtime.SwiftFrameworkResolver.RegisterForAssembly()` instead of inline try-catch + lambda |
| `ModuleHandlerTests.cs` | Replaced 2 obsolete tests with `CallsSwiftFrameworkResolver` + `UsesGlobalQualification` |
| 4 test apps | Replaced manual try-catch `SetDllImportResolver` with `SwiftFrameworkResolver.RegisterForAssembly()` |

#### Part C: `cd-dispose-*` NativeAOT Tests

Added 3 Tier 3 NativeAOT dispose tests (`cd-dispose-class`, `cd-dispose-struct-string`, `cd-dispose-struct-nested`) to `NativeAotTestApp`, `NativeAotTestApp.Device`, and `run-nativeaot-tests.sh`.

#### Bonus: XMLCoder namespace shadowing fix

Fixed `System` namespace shadowing in generated code — `XMLDocumentType.System` (a Swift case) shadowed the `System` namespace in `Marshal.PtrToStringUTF8(...)` calls. Added `global::` prefix to `PropertyHandler.cs` and `WrapperEmitter.Return.cs`. Library validation improved from 89/90 to **90/90**.

#### Validation Gate

- [x] `./run-tests.sh` — 6915 unit tests pass, 0 failures
- [x] `./validate-libraries.sh --tier all` — 90/90 pass (XMLCoder namespace shadowing fixed)
- [x] TestFramework: C# compile-check pass, Swift wrapper compile pass, golden files updated
- [x] `cd-dispose-*` NativeAOT tests added to simulator + device apps
- [x] Generated `[ModuleInitializer]` delegates to centralized runtime resolver
- [ ] `Dispose()` / `using` works on device for all bound types without crash — requires device
- [ ] `cd-dispose-*` NativeAOT tests pass on simulator — requires `./run-nativeaot-tests.sh`

---

### Phase 2.5: Closure Parameters in @_cdecl Wrappers [COMPLETE]

**Sessions**: 1 implement
**Goal**: Extend `MethodWrapperEmitter` and `ConstructorWrapperEmitter` to handle closure parameters inline within @_cdecl wrappers, eliminating the separate standalone closure wrapper path for methods/constructors that qualify for @_cdecl wrappers.

**Scope**: Methods and constructors with closure parameters where all closure arg/return types are Cdecl-compatible (same coverage as Workaround B). Closures with non-Cdecl-compatible callback types (String, non-frozen struct args in the closure signature) are deferred.

**What changed**: Phase 2 explicitly skipped methods with closure parameters (guard #8 in `MethodWrapperEmitter.ShouldEmitWrapper()`). These methods fell through to Workaround B (`ClosureEmitter.SwiftWrapper.cs`) which only handled closures with Cdecl-compatible types. Phase 2.5 lifts this guard, delegating to `NeedsClosureCdeclWrapper()` for condition parity plus an explicit async closure guard (since `GetSwiftClosureAdapterCode()` only emits synchronous adapter code).

#### Implementation Summary

| File | Change |
|------|--------|
| `MethodDecl.cs` | Added `HasClosureParams` flag + `HasCdeclClosureMarshalling` computed property |
| `MethodWrapperEmitter.cs` | Lifted guard #8 (delegate to `NeedsClosureCdeclWrapper()` + `HasAnyAsyncClosure()`), extended param loop for closure adapter emission |
| `ConstructorWrapperEmitter.cs` | Same guard lift + closure param handling |
| `ClosureEmitter.SwiftWrapper.cs` | Added `GetSwiftArgLabelForCdecl()` internal wrapper |
| `MethodHandler.cs` | Set `HasClosureParams` flag at 3 locations after @_cdecl wrapper flag assignment |
| `PInvokeEmitter.cs` | Gate change: `HasClosureCdeclWrapper` → `HasCdeclClosureMarshalling` |
| `WrapperEmitter.Marshalling.cs` | Gate changes: lines 257, 519 → `HasCdeclClosureMarshalling` |
| `DefaultParameterOverloadEmitter.cs` | Propagated `HasClosureParams` to method + constructor overload decls |
| `WrapperEmitter.cs` | Fixed `EmitCdeclIndirectResultCleanup` to skip constructors (SafeHandle, not raw payload) |
| `MethodWrapperClosureTests.cs` (new) | 20 tests covering guards, emission, constructors, marshalling |

#### Key Design Decisions

1. **Reuse `ClosureEmitter` helpers** — `GetSwiftClosureAdapterCode()`, `GetSwiftConventionCType()`, `IsClosureCdeclCompatible()` are already public and handle edge cases (optional closures, throwing, indirect return).
2. **Delegate to `NeedsClosureCdeclWrapper()`** for guard parity — ensures exact condition parity with standalone closure wrapper path. The `HasAnyAsyncClosure()` guard adds the one missing check (plain async closures).
3. **`HasCdeclClosureMarshalling` bridges both paths** — uses existing `UsesCdeclWrapper` aggregate + `HasClosureParams`, so PInvokeEmitter and WrapperEmitter.Marshalling work for both standalone and inline closure handling.

#### Validation Gate

- [x] `./run-tests.sh` — 6937 unit tests pass, 0 failures (20 new closure wrapper tests)
- [x] `./validate-libraries.sh --tier all` — 90/90 pass (Kingfisher improved from fail to pass)
- [x] TestFramework: C# compile-check pass, Swift wrapper compile pass, golden files updated
- [x] Kingfisher `AnyImageModifier` constructor with throwing closure — correctly emits @_cdecl wrapper

---

### Phase 3.5: Complete CallConvSwift Elimination

**Goal**: Route 100% of generated P/Invokes through `@_cdecl` wrappers. Zero `CallConvSwift` in generated bindings.

**Current state** (post Session 1): ~55% → ~70% of P/Invokes are on `@_cdecl`. Sub-phases A, B, and H are complete. The remaining ~30% still use `CallConvSwift`, broken down as:

| Category | Share | Status |
|---|---|---|
| ~~Free functions~~ | ~~29%~~ | **Complete** (Session 1, Sub-phase A) |
| ~~Metadata accessors~~ | ~~22%~~ | **Complete** (Session 1, Sub-phase B) |
| Optional types | 18% | Sub-phase C: Optional-aware parameter/return marshalling |
| Generic parent/method | 12% | Sub-phase D: @_cdecl trampoline over @_silgen_name |
| @_silgen_name intermediaries | 4.5% | Sub-phase E: @_cdecl trampolines for async/default-param/array-slice/optbuf wrappers |
| Protocol existential | 4% | Sub-phase F: Opaque pointer + ExistentialContainer reconstruction |
| Remaining misc | ~10% | Sub-phase G: Closure returns, tuple returns, nested types, non-copyable, non-primitive frozen structs, subscripts |
| ~~Runtime P/Invokes~~ | ~~—~~ | **Complete** (Session 1, Sub-phase H) |

All remaining sub-phases are independent — no ordering dependencies. Ordered by impact (biggest CallConvSwift reduction first).

#### Sub-phase A: Free Functions (~29%) — COMPLETE

**Problem**: `MethodWrapperEmitter.ShouldEmitWrapper()` guard #5 rejects methods where `ParentDecl is not TypeDecl`. Module-level free functions have `ModuleDecl` as parent.

**Solution**: Lifted guard #5 to accept `ModuleDecl`. Free functions force `isStatic = true`, emit no `self` parameter, and use empty `selfRef` (no type prefix in call expression). Symbol naming uses `"Free"` as the type segment: `SBW_{module}_Free_{method}_{hash}`.

**Files changed**: `MethodWrapperEmitter.cs` (guards #5/#5b/#6b null-safe, free function emission), `MethodHandler.cs` (null-safe flag-setting), `MethodWrapperEmitterTests.cs` (6 new tests).

**Validation**: 90/90 libraries pass, 7223 unit tests pass.

#### Sub-phase B: Metadata Accessors (~22%) — COMPLETE

**Problem**: Every bound type emits a `PInvoke_getMetadata()` that calls the Swift metadata accessor (`$s...Ma` symbol) via `CallConvSwift`.

**Solution**: New `MetadataWrapperEmitter` emits per-type `@_cdecl` wrappers returning metadata as raw pointers:
```swift
@_cdecl("SBW_GetMetadata_Nuke_Nuke_ImageRequest_A1B2C3D4")
public func _sbw_getMetadata_A1B2C3D4() -> UnsafeMutableRawPointer {
    unsafeBitCast(Nuke.ImageRequest.self as Any.Type, to: UnsafeMutableRawPointer.self)
}
```

C# P/Invoke returns `TypeMetadata` directly (blittable `readonly struct` wrapping `IntPtr`, marshals identically via Cdecl). Dedup via `ModuleEmissionContext._metadataWrapperSymbols`. `TypeMetadata.FromHandle(IntPtr)` public factory added for code outside the runtime assembly.

**Prerequisite completed**: Added `TypeMetadata.FromHandle(IntPtr)` public factory method.

**Files changed**: New `MetadataWrapperEmitter.cs`, `ModuleEmissionContext.cs` (dedup set), `TypeHandlerHelpers.cs`/`ClassHandler.cs`/`EnumISwiftObjectMethodWriter.cs` (3-way branch: generic/xcframework-cdecl/manual), `NonFrozenStructHandler.cs`/`FrozenStructHandler.cs`/`EnumHandler.cs` (thread SwiftWriter+context), `MetadataWrapperEmitterTests.cs` (5 new tests), `CompileSmokeTests.cs` (xcframework-mode metadata pattern).

**Validation**: 90/90 libraries pass, 7223 unit tests pass.

#### Sub-phase C: Optional Types (~18%)

**Problem**: `Optional<T>` hits the generic container guard in `PropertyWrapperEmitter` and `MethodWrapperEmitter`. Properties with `Optional<NSURLResponse>`, `Optional<CacheType>`, etc. fall through to `CallConvSwift`.

**Solution**: Split into two sub-phases by type kind:

##### Sub-phase C.1: Optional\<reference-type\> [COMPLETE]

**Approach**: Nullable pointer ABI (`UnsafeMutableRawPointer?`). The IntPtr from C# IS the object pointer (or 0 for nil). No buffer or flag needed.

- **Guards lifted**: MethodWrapperEmitter guard 14 (generic containers) and guard 16 (large optionals) now allow Optional\<Class\>, Optional\<ObjC-bridged\>, and Optional\<ObjC-rooted\> through.
- **PropertyWrapperEmitter**: Guard 7 (large optionals) and guard 9 (generic containers) allow reference-type optionals. ObjC-bridged read-write properties remain blocked (setter IntPtr alias incompatibility).
- **New return kind**: `CdeclReturnKind.OptionalClassPointer` — returns `result.map { Unmanaged.passRetained($0).toOpaque() }`.
- **New param mapping**: `GetCdeclParamMapping` intercepts Optional\<reference\> before the generic-container path: receives `UnsafeMutableRawPointer?`, reconstructs via `label.map { Unmanaged<T>.fromOpaque($0).takeUnretainedValue() }`.
- **Key insight**: C# side needs NO changes — PInvokeSignatureBuilder short-circuits all bound-generic returns to IntPtr (correct for nullable pointer).
- **Helper**: `IsOptionalWithReferenceInner()` is the single gate, handling both TypeRecord-based classification and unresolved Apple framework ObjC fallback heuristic.
- **NSString typedef exclusion**: NSString typedef structs (`CALayerContentsGravity`, `CATransitionType`, etc.) are `ObjCBridged` in the type database but are Swift structs wrapping NSString — `Unmanaged<T>` requires a class. The helper excludes these via `TryGetNetTypeName` → `"Foundation.NSString"`, mirroring the existing carveouts in `GetCdeclReturnMapping` and `GetCdeclParamMapping`.

**Files modified**: `MethodWrapperEmitter.cs`, `PropertyWrapperEmitter.cs`, `ConstructorWrapperEmitter.cs`.

##### Sub-phase C.2: Optional\<value-type\> [DEFERRED]

**Approach**: Buffer + discriminant flag. Optional\<Bool\>, Optional\<Int\>, Optional\<SwiftString\>, Optional\<FrozenStruct\>, Optional\<Enum\> require:
1. Buffer approach in @_cdecl (result buffer + discriminant flag)
2. Changes to PInvokeSignatureBuilder to NOT short-circuit Optional\<value\> returns
3. Changes to MethodMarshalPlanBuilder for IndirectResult allocation with `ContainerTypeName`
4. Changes to WrapperEmitter.Return.cs for accessor Optional\<value\> returns

These are complex and high-risk — separate session.

**Effort**: Medium (C.1 complete), Medium-large (C.2 remaining).

#### Sub-phase D: Generic Types (~12%)

**Problem**: `@_cdecl` cannot express Swift generic type parameters. Methods on `ImageCache<Key>` or generic methods like `func map<T>()` have no way to pass `T` through a C function signature.

**Solution**: Two-layer approach (same pattern as async wrappers):
1. `@_silgen_name` function handles the generic Swift types internally
2. `@_cdecl` trampoline wraps it with C-compatible signature, forwarding all params as `UnsafeRawPointer` + `TypeMetadata`

```swift
// Layer 1: @_silgen_name (generic, internal Swift ABI)
@_silgen_name("_silgen_Nuke_ImageCache_removeAll")
func _silgen_removeAll<Key>(_ self_: UnsafeRawPointer) { ... }

// Layer 2: @_cdecl trampoline (C ABI, no generics)
@_cdecl("SBW_Nuke_ImageCache_removeAll_ABCD1234")
func _sbw_removeAll(_ self_: UnsafeRawPointer) {
    _silgen_removeAll(self_)
}
```

For generic methods with type parameters in the signature, the @_cdecl trampoline passes all values as `UnsafeRawPointer` and the @_silgen_name layer reconstructs typed values using the metadata.

**Files**: `MethodWrapperEmitter.cs`, `ConstructorWrapperEmitter.cs`, new trampoline emission logic.

**Effort**: Medium-large. The trampoline pattern is straightforward, but generic parameter marshalling (passing values as opaque pointers + reconstructing via metadata) adds complexity. Start with the common case: generic parent types with concrete method signatures (no method-level generics).

#### Sub-phase E: @_silgen_name Intermediaries (~4.5%)

**Problem**: Existing async wrappers, default-parameter overloads, array-slice wrappers, and optional-buffer (`_optbuf`) wrappers already use `@_silgen_name` functions in the wrapper xcframework. But the C# P/Invoke calls them via `CallConvSwift`.

**Solution**: Add a `@_cdecl` trampoline for each `@_silgen_name` wrapper. The trampoline has a C-compatible signature and simply forwards to the `@_silgen_name` function.

This is mechanical: for each existing `@_silgen_name` wrapper, generate a corresponding `@_cdecl` with the same parameters (already C-compatible for most — async callbacks are `@convention(c)`, closures pass as funcPtr+context).

**Files**: Async wrapper emission (`WrapperEmitter.Async.cs`), `DefaultParameterOverloadEmitter.cs`, `ArraySliceEmitter.cs`, `OptionalPointerWrapperEmitter.cs`, `PInvokeEmitter.cs`.

**Effort**: Medium. Mechanical but wide surface area — every existing @_silgen_name emission point needs a companion @_cdecl.

#### Sub-phase F: Protocol Existential (~4%)

**Problem**: Methods with protocol existential parameters (`any Protocol`) or returns use `ExistentialContainer` which doesn't map to a C-compatible type at the `@_cdecl` boundary.

**Solution**: Pass existential values as `UnsafeRawPointer` to a buffer containing the `ExistentialContainer`. The `@_cdecl` wrapper reconstructs the existential from the buffer:

```swift
@_cdecl("SBW_Nuke_process_08AB12CD")
func _sbw_process(_ input: UnsafeRawPointer, _ self_: UnsafeRawPointer) {
    let existential = input.load(as: (any ImageProcessing).self)
    // ... call method with existential
}
```

**Files**: `MethodWrapperEmitter.cs`, `ConstructorWrapperEmitter.cs`, `PropertyWrapperEmitter.cs` (lift existential guards), `GetCdeclParamMapping()`.

**Effort**: Medium. The ExistentialContainer layout is well-understood in the codebase. Main risk is ensuring correct memory management for existential values across the boundary.

#### Sub-phase G: Remaining Misc (~10%)

Smaller categories, each with specific solutions:

| Pattern | Solution | Effort |
|---|---|---|
| Closure returns | @_cdecl returns `(funcPtr, context)` pair, C# reconstructs delegate | Small |
| Tuple returns | Write to out-buffer (flattened), same pattern as complex enum returns | Small |
| Nested types | Naming fix — use full `ParentType.NestedType` in wrapper | Small |
| Non-copyable structs | Pass as `UnsafeRawPointer` (no copy needed in wrapper) | Small |
| Non-primitive frozen structs | Pass as `UnsafeRawPointer` to buffer (like non-frozen) | Small |
| Subscript accessors | Extend `PropertyWrapperEmitter` to handle subscript key params | Medium |
| Failable constructors | Return `Optional<UnsafeRawPointer>` (nil = init failed) | Small |
| Non-frozen struct returns (methods) | `resultPtr` out-buffer pattern (exists for constructors, extend to methods) | Small |

**Effort**: Medium total. Each item is small but there are many.

#### Sub-phase H: Runtime P/Invokes — COMPLETE

**Problem**: `SwiftString.cs` had `_useWrapperPath` fallback — tried `libSwiftBindingsRuntime` @_cdecl wrappers first, fell back to direct `CallConvSwift` P/Invokes on `DllNotFoundException`. `TypeMetadata.cs` had similar fallback for existential metadata. On Mono (iOS Simulator), missing wrapper library would silently fall through to the process-fatal CallConvSwift path.

**Solution**:
1. Removed ALL `CallConvSwift` P/Invokes from `SwiftString.cs` (`PInvoke_Create`, `PInvoke_GetLength`, `PInvoke_GetUtf8ContiguousArray`, `PInvoke_WithUnsafeBytes`, `ToStringDirect()`)
2. Removed `_useWrapperPath` flag, `_isMonoRuntime` flag, `ToStringCallbackContext` struct
3. Wrapper library is now the **only** path — missing library throws `SwiftRuntimeException` with clear message (no silent fallback)
4. Metadata path: Cdecl wrapper → direct `$sSSN` symbol lookup fallback (no CallConvSwift for metadata)
5. `TypeMetadata.cs`: Removed `swift_getExistentialTypeMetadata` CallConvSwift P/Invoke and `_isMonoRuntime` flag
6. Added `SBW_SwiftString_GetMetadata` to `SwiftBindingsRuntime.swift`
7. Runtime test project copies `libSwiftBindingsRuntime.dylib` to test output

Zero `CallConvSwift` code paths remain in `SwiftString.cs`. Zero in `TypeMetadata.cs` (only comments).

**Files changed**: `SwiftString.cs` (major rewrite), `TypeMetadata.cs` (fallback removal), `SwiftBindingsRuntime.swift` (new export), `SwiftRuntimeException.cs` (inner exception ctor), `SwiftStringWrapperTests.cs` (6th required symbol), `Swift.Runtime.Tests.csproj` (dylib copy).

**Validation**: 90/90 libraries pass, 7223 unit tests pass, 262 runtime tests pass.

#### Implementation Strategy

Three plan+implement sessions total. All sub-phases use the same wrapper emitter patterns established in Phases 1–3.

**Session 1** (plan + implement) — "Easy wins" — **COMPLETE**:

| Sub-phase | What | Status |
|---|---|---|
| A | Free functions — lift guard, no-self emission | Done |
| B | Metadata accessors — new `MetadataWrapperEmitter` | Done |
| H | Runtime fallback removal — wrapper-only, clear errors | Done |
| G partial | Investigation only — easy items already handled, rest deferred to Session 2 | N/A |

Result: ~55% → ~70% Cdecl. G partial items (non-frozen struct returns, failable ctors, nested type naming) were already handled by existing code. Closure returns, tuple returns deferred to Session 2 (new marshalling patterns required).

**Session 2** (plan + implement) — "New marshalling patterns":

| Sub-phase | What | Effort |
|---|---|---|
| C | Optional types — per-type null encoding | Medium |
| D | Generic types — @_cdecl trampoline over @_silgen_name | Medium |
| E | @_silgen_name intermediaries — async/default-param/optbuf trampolines | Medium |
| F | Protocol existential — opaque pointer + ExistentialContainer | Medium |
| G remainder | Non-primitive frozen structs, subscripts, non-copyable | Small-medium |

Expected result: **100% CallConvSwift eliminated**. Phase 4 fully unblocked.

**Session 3** (plan + implement) — Phase 4: Full cleanup and documentation. Delete all workaround infrastructure.

#### Validation Gate (per session)

Session 1 results:
- [x] `grep -c "CallConvSwift" <generated>.cs` decreases (Nuke: ~200→151, significant Cdecl increase)
- [x] `./run-tests.sh` — 7223 unit + 262 runtime tests pass, 0 failures
- [x] `./validate-libraries.sh --tier all` — 90/90 pass, 0 regressions
- [x] TestFramework: golden files updated
- [x] `SwiftString.cs` — zero CallConvSwift code paths
- [x] `TypeMetadata.cs` — zero CallConvSwift P/Invokes (only comments)
- [x] `CompileSmokeTests.cs` — xcframework-mode metadata pattern covered

#### Final Validation (after Session 4)

- [ ] `grep -rc "CallConvSwift" /tmp/binding-validation/*/` returns **zero** across all 90 libraries
- [ ] `grep "CallConvSwift" TestFramework/output/SwiftBindingsTestLib.cs` returns **zero**
- [ ] Runtime: `SwiftString.cs` and `TypeMetadata.cs` have no `CallConvSwift` P/Invokes
- [ ] All `ShouldEmitWrapper()` guards that returned false now either return true (wrappable) or suppress the method entirely (not emittable)

---

### Phase 4: Cleanup and Documentation

**Prerequisite**: Phase 3.5 complete (zero `CallConvSwift` in generated code and runtime).
**Sessions**: 1 implement
**Goal**: Remove ALL workaround infrastructure. Single clean code path. Update documentation.

#### What Gets Removed (Code)

| Component | Why it's unnecessary |
|-----------|---------------------|
| ~~Workaround A: `_useWrapperPath` fallback in `SwiftString.cs`~~ | **Already removed** (Phase 3.5 Session 1, Sub-phase H) |
| ~~Workaround A: `PInvoke_Create`, `PInvoke_GetLength`, `PInvoke_ToString` CallConvSwift P/Invokes~~ | **Already removed** (Phase 3.5 Session 1, Sub-phase H) |
| Workaround B: `ClosureEmitter.SwiftWrapper.cs` standalone closure wrappers | All closure-parameter methods route through @_cdecl method/constructor wrappers (Phase 2.5 + 3.5) |
| Workaround B: `HasClosureCdeclWrapper` / `UsesFreeFunctionWrapper` flags | No standalone closure path exists — all closures handled inline in @_cdecl wrappers |
| Workaround B: `NeedsClosureCdeclWrapper()` in `MonoJitRiskDetector` | No callers remain |
| ~~Workaround C: `SwiftBindings_GetExistentialTypeMetadata` fallback in `TypeMetadata.cs`~~ | **Already removed** (Phase 3.5 Session 1, Sub-phase H) |
| Workaround D: `MonoJitRiskDetector.cs` (entire file) | No risky path exists — nothing uses CallConvSwift |
| Workaround D: `DetectedJitRisks` on `MethodDecl` | Informational flag with no consumers |
| `libSwiftBindingsRuntime.dylib` build pipeline | All functions either moved to per-library wrappers or kept as @_cdecl-only in runtime (no CallConvSwift fallback) |
| `[CrashRisk]` attributes on test classes | All tests pass on both Mono and NativeAOT |
| `--safe-only` flag in test runner | No unsafe tests exist |
| Tier 3 deferral for Mono JIT tests | All tiers run everywhere |
| Dual `PInvokeCallingConvention` routing in `PInvokeEmitter` | Single path: all P/Invokes are Cdecl |

#### What Gets Preserved (Documentation)

The underlying .NET runtime bugs are real and should be fixed upstream. Removing our workaround code does **not** mean removing documentation of the bugs:

| Document | Purpose | Action |
|----------|---------|--------|
| `known-issues-workarounds.md` | Rewrite to explain the `@_cdecl` approach, note it bypasses CallConvSwift, and document the `UseDirectSwiftPInvoke` flag for switching back when upstream fixes land. Keep the Upstream Bug Report Status section. | Update |
| `Future/upstream-bug-reports-draft.md` | Expand existing draft to cover all 5 runtime bugs (currently has 3). Add NativeAOT-specific Bugs #2 and #3 with device crash data. | Update |
| Revert plan in `known-issues-workarounds.md` | Rewrite from "revert workarounds A–D" to "enable `UseDirectSwiftPInvoke` and verify" — simpler, single-flag activation. | Update |

#### Upstream Bug Report Documentation

An existing draft lives at `Future/upstream-bug-reports-draft.md` with 3 of the 5 bugs documented. Phase 4 expands it to cover all 5 bugs discovered through real-world device testing.

**Existing coverage** (already drafted with reproduction cases):

| Draft Issue | Maps to | Runtime | Status |
|------------|---------|---------|--------|
| Issue 1: `jit-info.c:918` assertion crash | Bug #1 | Mono JIT | Draft ready — has reproduction code + stack trace |
| Issue 2: Non-blittable types rejected | Bug #4 | Both | Draft ready — has reproduction code + workaround |
| Issue 3: SafeHandle across async P/Invoke | Bug #5 | Both | Draft ready — comment for tracking issue |

**Needs to be added** (discovered via Nuke/Lottie device testing, not in original draft):

| New Bug | Runtime | What to document |
|---------|---------|-----------------|
| Bug #2: NativeAOT SIGABRT/SIGSEGV on certain `CallConvSwift` enum returns | NativeAOT | Specific enum layouts crash (e.g., Nuke `Priority` crashes but Lottie `LoopMode` works). Include the confirmed-crash vs confirmed-working table from KNOWN-ISSUES Issue 7. Minimal repro: Swift enum with specific size/alignment + NativeAOT `CallConvSwift` getter. |
| Bug #3: NativeAOT SIGSEGV on VWT Destroy via indirect `CallConvSwift` function pointer | NativeAOT | `ValueWitnessTable->Destroy()` called through indirect function pointer crashes. Minimal repro: create a Swift struct with `String` field, call Destroy via VWT from C#. Include crash stack from KNOWN-ISSUES Issue 2. |

Each bug report follows the existing draft format:
1. **Title** (concise, with `[Mono]` or `[NativeAOT]` prefix)
2. **Labels** (`area-Interop-Swift`, runtime tag, severity)
3. **Minimal reproduction** — standalone C# + Swift code
4. **Root cause analysis** — what we think is happening
5. **Workaround** — `@_cdecl` wrapper approach, with code
6. **Real-world impact** — which libraries and APIs are blocked

**Filing strategy**: File after the swift-bindings repo is public so issues can link to concrete reproduction code. The draft already notes this prerequisite. Before filing, re-search dotnet/runtime to confirm nothing has been filed in the interim.

**All 5 bugs at a glance:**

| # | Bug | Runtime | Upstream Filing |
|---|-----|---------|----------------|
| 1 | `jit-info.c:918` assertion — JIT marks `CallConvSwift` frames as async | Mono JIT | Bug report |
| 2 | SIGABRT/SIGSEGV on certain enum returns via `CallConvSwift` | NativeAOT | Bug report |
| 3 | SIGSEGV on VWT Destroy via indirect `CallConvSwift` function pointer | NativeAOT | Bug report |
| 4 | Non-blittable types rejected with `CallConvSwift` | Both | Feature request |
| 5 | SafeHandle not preserved across async P/Invoke | Both | Comment on tracking issue |

#### Validation Gate

- [ ] `MonoJitRiskDetector.cs` deleted (entire file)
- [ ] `ClosureEmitter.SwiftWrapper.cs` deleted — all closure handling is inline in @_cdecl wrappers
- [ ] `HasClosureCdeclWrapper`, `UsesFreeFunctionWrapper`, `DetectedJitRisks` removed from `MethodDecl`
- [ ] `_useWrapperPath` and CallConvSwift fallback P/Invokes removed from `SwiftString.cs`
- [ ] CallConvSwift fallback removed from `TypeMetadata.cs` existential metadata path
- [ ] `libSwiftBindingsRuntime.dylib` — either removed entirely (functions in per-library wrappers) or retained as @_cdecl-only (no CallConvSwift inside)
- [ ] `PInvokeCallingConvention.Swift` enum value unused — all generated P/Invokes are `.Cdecl`
- [ ] `[CrashRisk]` attributes removed from all test classes
- [ ] `--safe-only` flag removed from test runner
- [ ] All tests run as Tier 1/2 on both Mono and NativeAOT — no crash-risk segregation
- [ ] `grep -rc "CallConvSwift" /tmp/binding-validation/*/` returns **zero** for all 90 libraries
- [ ] Full test suite passes without any workaround code
- [ ] Library validation 90/90 still passes
- [ ] `known-issues-workarounds.md` rewritten: explains `@_cdecl` approach, retains upstream bug references
- [ ] `Future/upstream-bug-reports-draft.md` expanded with all 5 .NET runtime bugs, each with minimal reproduction case

---

## Test Strategy

### Principle: Test the Boundaries, Not the Internals

The `@_cdecl` wrapper is a thin translation layer. The important thing to test is that **every type combination marshals correctly across the C boundary** — not the wrapper generation logic itself (which is covered by unit tests on the emitter).

### Test Tiers

#### Tier 1: Emitter Unit Tests (Fast, CI)

Verify the generator emits correct wrapper code for each type combination.

| Test Category | What it verifies | Example |
|--------------|-----------------|---------|
| Property getter wrapper emission | Correct `@_cdecl` function + C# P/Invoke for each return type | `int` return, `string` return, `enum` return, `class` return, `optional` return |
| Property setter wrapper emission | Correct parameter marshalling in wrapper | `int` param, `string` param, `enum` param |
| Method wrapper emission | Multi-parameter wrapper with mixed types | `(String, Int, NSUrl) → Bool` |
| Constructor wrapper emission | Correct return type (opaque pointer + retain for classes) | Class init, struct init |
| Destroy wrapper emission | Correct deinit for classes vs structs | Class ARC release, struct field deinit |
| Symbol naming | Deterministic, collision-free symbol names | Module-qualified + hash |
| Static member wrappers | No self parameter in wrapper | Static property, static method |

**Location**: `src/Swift.Bindings/tests/UnitTests/EmitterTests/`
**Run with**: `./run-tests.sh`
**Test patterns**: Follow `ConstructorWrapperEmitterTests.cs` (2,021 lines) for fixture setup, mock TypeDatabase, assertion style.

#### Tier 2: Compile Gate (Medium, CI)

Verify that generated wrappers compile for all 90 validated libraries.

| What it verifies | How |
|-----------------|-----|
| Swift wrapper code compiles | `./validate-libraries.sh` — wrapper xcframework builds successfully |
| C# P/Invokes compile | Same — generated `.csproj` builds |
| No symbol collisions | Wrapper compilation succeeds (linker catches dupes) |
| Type access | Wrapper can reference all public types it needs |

**Run with**: `./validate-libraries.sh`

#### Tier 3: NativeAOT Simulator Tests (Slow, Pre-Release)

Verify runtime correctness on iOS Simulator under NativeAOT.

| Test Category | What it verifies | Tests |
|--------------|-----------------|-------|
| Enum marshalling | Enum return, enum param, enum property get/set, associated values | `cr-enum-basic`, `cr-enum-string`, `cr-enum-shape`, `cr-enum-nested` |
| Array marshalling | SwiftArray param, return, class arrays, string arrays | `cr-array-basic`, `cr-array-advanced` |
| GC + ownership | ForceGC with class/struct objects, Dispose safety | `cr-gc-basic`, `cr-gc-mutableprops`, `cr-gc-stress` |
| Existential callbacks | Protocol proxy through CallConvSwift boundary | `cr-existential` |
| String round-trip | SwiftString create/read/dispose | `b1-string-*` |
| Async methods | Async instance + static methods | `b3-async-*` |
| VWT operations | Destroy, InitCopy on structs with ref fields | `b1-vwt-*` |
| Dispose safety | Dispose on all type categories without crash | New: `cd-dispose-*` |

**Run with**: `./run-nativeaot-tests.sh`

#### Tier 4: Real-Library Device Tests (Manual, Release Gate)

Verify that Nuke and Lottie work end-to-end on a real device.

| What it verifies | How |
|-----------------|-----|
| Issue 7 crashes are fixed | Run Nuke/Lottie device test suite — previously-crashing APIs now work |
| Issue 2 Dispose crash is fixed | `using` declarations don't crash |
| Issue 9 non-blittable params work | NSUrl-based Nuke APIs work |
| No regressions | Previously-passing APIs still pass |

**Run with**: Device test suite in `swift-dotnet-packages` repo.

### New Tests to Add (Per Phase)

**Phase 1 — add during implementation:**
- `cd-prop-enum-return` — Property getter returning enum (reproduces Issue 7 Priority crash)
- `cd-prop-string-return` — Property getter returning String (reproduces Issue 7 AnimationKeypath.String crash)
- `cd-prop-optional-objc-return` — Property getter returning optional ObjC type
- `cd-prop-enum-set` — Property setter with enum value
- `cd-prop-struct-return` — Property getter returning struct (regression guard)

**Phase 3 — add during implementation:**
- `cd-dispose-class` — Dispose() on a Swift class
- `cd-dispose-struct-string` — Dispose() on a struct with String field (reproduces Issue 2)
- `cd-dispose-struct-nested` — Dispose() on a struct with nested struct containing ref fields

---

## Overall "Done" Definition

- [ ] **Zero `CallConvSwift` in generated code** — `grep -rc "CallConvSwift"` returns zero for all 90 validated libraries, TestFramework, and runtime
- [ ] **Zero workaround infrastructure** — `MonoJitRiskDetector`, `ClosureEmitter.SwiftWrapper.cs`, `_useWrapperPath`, `HasClosureCdeclWrapper`, `UsesFreeFunctionWrapper`, `DetectedJitRisks`, `[CrashRisk]`, `--safe-only` all deleted
- [ ] **All 10 KNOWN-ISSUES resolved or documented as .NET runtime limitations** — Issues 1, 2, 7, 9, 10 fixed by @_cdecl wrappers; Issues 4, 5 fixed by Phase 3; Issues 6, 8 are separate feature work
- [ ] **Nuke + Lottie device test suites: 0 crashes** — all previously-skipped tests pass
- [ ] **Single code path** — every P/Invoke goes C# → @_cdecl → Swift. No dual-path routing, no fallbacks, no risk detection
- [ ] **Upstream bugs documented** — `Future/upstream-bug-reports-draft.md` expanded with all 5 bugs, ready to file on dotnet/runtime when repo is public
- [ ] **End-user documentation clear** — `known-issues-workarounds.md` explains that SwiftBindings routes all calls through `@_cdecl` wrappers for stability, bypassing `CallConvSwift` entirely

---

## Risks and Mitigations

### Risk 1: Wrapper Compilation Failures

**Risk**: `@_cdecl` wrapper needs to import the original library's types. If a type is internal/private or uses conditional compilation (`#if compiler`), the wrapper can't access it.

**Mitigation**: This is an existing problem — async wrappers already fail for SkeletonView and Mixpanel internal types. Universal `@_cdecl` doesn't make it worse. For inaccessible types, fall back to `CallConvSwift` P/Invoke with a build warning (opt-in risk). Track with a `[WrapperFallback]` attribute on the P/Invoke.

**Current impact**: 2-3 libraries out of 90 have wrapper compilation failures. These are due to internal types, not the wrapper approach.

### Risk 2: Swift Generics at `@_cdecl` Boundary

**Risk**: `@_cdecl` can't express Swift generic type parameters in the function signature. Methods like `SwiftArray<T>.append(element:)` need special handling.

**Mitigation**: Two strategies:
1. **Monomorphization**: Emit one wrapper per concrete type instantiation (already done for async generic methods).
2. **Type-erased pointer + metadata**: Pass `UnsafeRawPointer` + `TypeMetadata` and use `withUnsafePointer(to:)` in the wrapper (matches how VWT functions work).

For Phase 1-2, most properties and methods have concrete types. Generic methods are the long tail.

### Risk 3: Build Time Increase

**Risk**: More Swift wrapper functions = longer wrapper xcframework compilation.

**Mitigation**: Measured impact is small. A library with 200 bound members generates ~200 wrapper functions. Swift compiler handles this in seconds. The wrapper functions are trivial (2-5 lines each) — no complex control flow or optimization needed.

### Risk 4: Performance Overhead

**Risk**: Extra function call + marshalling per P/Invoke.

**Mitigation**: The overhead is one C function call + pointer dereference per cross-language call. This is negligible compared to the cost of the language boundary crossing itself. No binding consumer will notice the difference. If a hot path is ever identified, we can add a `[DirectPInvoke]` opt-in that uses `CallConvSwift` for that specific method.

### Risk 5: Incremental Rollout Complexity

**Risk**: During implementation, some P/Invokes use `@_cdecl` and others use `CallConvSwift`. Mixed state could be confusing.

**Mitigation**: Phase by P/Invoke category (properties, then methods, then constructors). Each phase is independently shippable — properties alone fixes Issues 2 and 7 for the most common crash patterns. The `validate-libraries.sh` gate ensures no regressions at each phase.

---

## Appendix: What Stays on CallConvSwift

After full implementation, the **only** remaining `CallConvSwift` usage is in the Swift runtime internals:

| Component | CallConvSwift Usage | Status |
|-----------|-------------------|--------|
| `SwiftString.PInvoke_Create/GetLength` | Direct libswiftCore calls | Replaced by per-library wrappers |
| `TypeMetadata.GetExistentialTypeMetadata` | Swift runtime function | Replaced by `@_cdecl` wrapper in runtime |
| `ValueWitnessTable` function pointers | Indirect calls for VWT operations | Replaced by per-type destroy wrappers |
| `SwiftObjectHelper<T>` metadata lookups | Type metadata accessor calls | May keep (these work reliably on both runtimes) |

The generated bindings will have **zero** `CallConvSwift` P/Invokes.

---

## Appendix: Relationship to Existing Wrapper Infrastructure

The wrapper xcframework already exists for every library that uses the `--xcframework` generator mode or the MSBuild SDK. It's compiled as part of the binding generation pipeline.

Currently the wrapper xcframework contains:
- Async method wrappers (`@_silgen_name` with `@convention(c)` callbacks)
- Closure adapter wrappers (`@_silgen_name` — standalone path for non-Cdecl closure types and edge cases)
- ExistentialBypass witness dispatch wrappers (`@_cdecl`)
- ObjC override property wrappers (`@_silgen_name`)
- Utf8Slice helper struct

Universal `@_cdecl` adds:
- Property getter/setter wrappers (`@_cdecl`)
- Method wrappers (`@_cdecl`) — including inline closure adapter code for Cdecl-compatible closures (Phase 2.5)
- Constructor wrappers (`@_cdecl`) — same inline closure handling (Phase 2.5)
- Destroy wrappers (`@_cdecl`)

The existing `@_silgen_name` async wrappers could also be migrated to `@_cdecl` for consistency in a future pass, but they already work correctly because they use `@convention(c)` callbacks — the Swift ABI is internal to the wrapper. The standalone `@_silgen_name` closure wrappers are mostly superseded by Phase 2.5's inline closure handling but remain for edge cases (non-Cdecl closure types, generic parents, free functions).

---

## Appendix: Reverting to CallConvSwift

This design is a **routing change**, not a rewrite. The generator retains the ability to emit direct `CallConvSwift` P/Invokes alongside the `@_cdecl` wrapper P/Invokes. Switching back requires no architectural changes.

### What Gets Retained

The emitter generates **both** P/Invoke paths for every member:

```csharp
// Always emitted — the @_cdecl wrapper path (default, used at runtime)
[DllImport("NukeSwiftBindings", CallingConvention = CallingConvention.Cdecl)]
private static extern int SBW_Get_Nuke_ImageRequest_priority(IntPtr self);

// Always emitted — the direct CallConvSwift path (retained, not called by default)
[DllImport("Nuke")]
[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
private static extern int PInvoke_priority_Get_46149C9C(SwiftSelf self);
```

The property/method body calls the `@_cdecl` version. The `CallConvSwift` declaration is retained but unreferenced — it compiles, has the correct signature, and can be activated by changing which P/Invoke the property body calls.

### How to Switch Back

An MSBuild property controls which path is active:

```xml
<PropertyGroup>
  <!-- Default: false (use @_cdecl wrappers for stability) -->
  <!-- Set to true to use direct CallConvSwift P/Invokes (requires fixed .NET runtime) -->
  <UseDirectSwiftPInvoke>true</UseDirectSwiftPInvoke>
</PropertyGroup>
```

When `UseDirectSwiftPInvoke=true`:
1. Property/method bodies call the `CallConvSwift` P/Invoke instead of the wrapper
2. The wrapper xcframework is still compiled (no build break) but not called at runtime
3. The `@_cdecl` wrapper P/Invoke declarations are still emitted (no code gen change)

This is a single flag flip — no code generation logic changes, no marshalling rewrite, no test infrastructure changes. The wrapper layer is simply bypassed.

### When to Switch Back

Switch back when **all** of the upstream .NET runtime bugs are fixed. Each bug is documented with a minimal reproduction case in `Future/upstream-bug-reports-draft.md`, ready to file on [dotnet/runtime](https://github.com/dotnet/runtime):

| Upstream Bug | dotnet/runtime Fix Required | Draft Issue |
|-------------|---------------------------|------------|
| Mono JIT `jit-info.c:918` assertion on `CallConvSwift` frames | JIT must not mark `CallConvSwift` P/Invoke frames as async | Draft Issue 1 (existing) |
| NativeAOT SIGABRT/SIGSEGV on certain `CallConvSwift` enum returns | NativeAOT codegen must handle all enum layouts in Swift calling convention | Draft Issue 4 (to add) |
| NativeAOT SIGSEGV on VWT Destroy via indirect `CallConvSwift` function pointer | Indirect function pointer calls via `CallConvSwift` must work in NativeAOT | Draft Issue 5 (to add) |
| Non-blittable types rejected with `CallConvSwift` | `SafeHandle`, `NSUrl`, `SwiftOptional<T>` must be marshallable through Swift calling convention | Draft Issue 2 (existing) |
| `SafeHandle` not preserved across async P/Invoke | GC must not collect `SafeHandle` during async suspension | Draft Issue 3 (existing) |

Until all five are fixed, `@_cdecl` wrappers remain the default. The `UseDirectSwiftPInvoke` flag exists for testing against preview runtime builds and for future activation when upstream fixes land.

**Verification process for switching back**: Set `UseDirectSwiftPInvoke=true`, run `./validate-libraries.sh` (compile gate), run `./run-nativeaot-tests.sh` (runtime gate), run Nuke + Lottie device test suites (real-world gate). All must pass with zero crashes.
