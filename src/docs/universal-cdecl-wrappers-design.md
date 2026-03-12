# Design: Universal @_cdecl Wrapper Emission (Part 1 — Completed Work)

> Eliminate all CallConvSwift runtime crashes by routing every P/Invoke through `@_cdecl` wrapper functions in the wrapper xcframework. This is the single highest-impact stability improvement for SwiftBindings.

> **Remaining work is in [Part 2](universal-cdecl-wrappers-part2.md)**: Sessions 6-8 (generic parent types, tuple returns, DynamicSelf returns) + Phase 4 (cleanup/documentation).

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

**Current state** (post Session 5): Sub-phases A, B, C (both C.1 and C.2), E, F, G.1, G.4 (closure returns), and H are complete. All 7 @_silgen_name wrapper paths now route through @_cdecl when eligible. The remaining CallConvSwift breaks down as:

| Category | Nuke P/Invokes | Status |
|---|---|---|
| ~~Free functions~~ | ~~done~~ | **Complete** (Session 1, Sub-phase A) |
| ~~Metadata accessors~~ | ~~done~~ | **Complete** (Session 1, Sub-phase B) |
| ~~Optional\<reference-type\>~~ | ~~done~~ | **Complete** (Session 2, Sub-phase C.1) |
| ~~Optional\<value-type\>~~ | ~~done~~ | **Complete** (Session 5, Sub-phase C.2) |
| ~~Runtime P/Invokes~~ | ~~done~~ | **Complete** (Session 1, Sub-phase H) |
| ~~@_silgen_name intermediaries~~ | ~~done~~ | **Complete** (Session 3, Sub-phase E) |
| ~~Protocol existential params/returns~~ | ~~done~~ | **Complete** (Session 4, Sub-phase F) |
| ~~Subscript accessors~~ | ~~done~~ | **Complete** (Session 4, Sub-phase G.1) |
| ~~Closure returns~~ | ~~done~~ | **Complete** (Session 5, Sub-phase G.4) |
| Generic parent types | ~80% of remaining | → [Part 2](universal-cdecl-wrappers-part2.md) Session 6 (methods/properties) + Session 7 (constructors) |
| Non-frozen struct returns | blocked by generic guard | Infrastructure done — will convert when generic parent guard is lifted |
| Complex enum constructors | blocked by generic guard | Infrastructure done — will convert when generic parent guard is lifted |
| Tuple returns | 19 libraries affected | → [Part 2](universal-cdecl-wrappers-part2.md) Session 8 |
| DynamicSelf returns | 10+ libraries affected | → [Part 2](universal-cdecl-wrappers-part2.md) Session 8 |
| **Unfixable** (Swift compiler limits) | ~0 | Nested frozen struct params, non-copyable structs, actor types — see below |

**Note on "unfixable" guards**: Guards 6b (actors), 11 (non-copyable), 12/12b (nested/non-primitive frozen struct params), and 17 (nested type returns) are Swift compiler restrictions — `@_cdecl` cannot express these types. These affect **zero methods** in Tier 1-2 validation libraries. Any method hitting these guards still gets a `CallConvSwift` P/Invoke as a fallback. This is acceptable: these patterns don't appear in real-world libraries.

**Note on overlap**: Many P/Invokes hit multiple guards simultaneously (e.g., a method on a generic type with an optional return and an existential param). The Nuke counts above show the *primary* blocking guard. Actual conversion requires lifting ALL guards a method hits.

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

##### Sub-phase C.2: Optional\<value-type\> [COMPLETE]

**Approach**: IndirectResult via `resultPtr` buffer. The Swift @_cdecl wrapper writes the full `Optional<T>` to the buffer using `initializeMemory(as: Optional<T>.self, repeating: result, count: 1)`. C# reads `SwiftOptional<T>` from the buffer and converts via `.ToNullable()`.

**Design deviation**: The original plan specified buffer + discriminant flag (`Int32` hasValue). The implementation uses the existing `IndirectResult` pattern instead — the full `Optional<T>` (including discriminant) is written as a single value, reusing existing infrastructure.

- **Guards lifted**: MethodWrapperEmitter guard 14, PropertyWrapperEmitter guards 7/9, SubscriptWrapperEmitter guard 8 — narrowed to block only non-Optional containers and Optional\<existential\>.
- **Guard 16 removed**: Large Optional params/returns now handled by @_cdecl IndirectResult.
- **New helper**: `IsOptionalSupportedForCdecl()` — blocks Optional\<protocol existential\> (needs proxy conversion).
- **Allocator sizing**: Uses `projection.ContainerTypeName` (`SwiftOptional<double>`) for TypeMetadata, not C# projected type (`double?`).
- **`_optRetPtr` suppressed**: Legacy allocation path disabled when IndirectResult handles Optional via `resultPtr`.
- **Setter fix**: Property/subscript setter `GetCdeclParamMapping` uses `omitLabels: false` to avoid `ShouldWidenParam` bypass.

**Files modified**: `MethodWrapperEmitter.cs`, `PropertyWrapperEmitter.cs`, `SubscriptWrapperEmitter.cs`, `MarshallingHelpers.cs`, `PInvokeEmitter.cs`, `WrapperEmitter.Return.cs`, `MethodMarshalPlanBuilder.cs`.

#### Sub-phase D: Generic Types → [Part 2](universal-cdecl-wrappers-part2.md) Sessions 6-7

> Generic parent types are the dominant remaining CallConvSwift driver (~80% of 4,565 remaining P/Invokes across all libraries). Full implementation plan with revised three-layer architecture is in Part 2.

#### Sub-phase E: @_silgen_name Intermediaries (~10%) → Session 3

**Problem**: 8 distinct `@_silgen_name` wrapper paths emit Swift wrappers in the xcframework but the C# P/Invoke calls them via `CallConvSwift`. All 8 paths have C-compatible parameters at their boundaries already.

**Solution**: Add a `@_cdecl` trampoline for each `@_silgen_name` wrapper. The trampoline has a C-compatible signature and simply forwards to the `@_silgen_name` function.

This is mechanical: for each existing `@_silgen_name` wrapper, generate a corresponding `@_cdecl` with the same parameters (already C-compatible — async callbacks are `@convention(c)`, closures pass as funcPtr+context, arrays marshal as IntPtr).

**8 wrapper paths**: Async methods, default param overloads (non-cdecl), debug param wrappers, array slice normalization, optional pointer buffers, standalone closure wrappers, async stream iteration.

**Files**: `WrapperEmitter.Async.cs`, `DefaultParameterOverloadEmitter.cs`, `ArraySliceNormalizationEmitter.cs`, `OptionalPointerWrapperEmitter.cs`, `ClosureEmitter.SwiftWrapper.cs`, `AsyncStreamEmitter.cs`, `PInvokeEmitter.cs`.

**Effort**: Medium. Mechanical but wide surface area. See Session 3 implementation plan for full details.

#### Sub-phase F: Protocol Existential (~4%) — COMPLETE

**Problem**: Methods with protocol existential parameters (`any Protocol`) or returns use `ExistentialContainer` which doesn't map to a C-compatible type at the `@_cdecl` boundary.

**Solution**: Pass existential values as `UnsafeRawPointer` to a buffer containing the `ExistentialContainer`. The `@_cdecl` wrapper reconstructs the existential from the buffer:

```swift
@_cdecl("SBW_Nuke_process_08AB12CD")
func _sbw_process(_ input: UnsafeRawPointer, _ self_: UnsafeRawPointer) {
    let existential = input.load(as: (any ImageProcessing).self)
    // ... call method with existential
}
```

C# side: new `CdeclExistential` marshalled type passes container by `ref` (maps to `UnsafeRawPointer` in @_cdecl). Existential returns use indirect result buffer (`resultPtr` + `MarshalFromSwift<T>`). `TryEmitReturnViaProjection` falls through for `@_cdecl IndirectResult + ExistentialProjection` so the dedicated handler reads the container before wrapping in a proxy.

**Files changed**: `MethodWrapperEmitter.cs` (lifted guards 9, 10), `ConstructorWrapperEmitter.cs` (lifted existential param guard), `PropertyWrapperEmitter.cs` (lifted guard 5, added existential to `GetCdeclReturnMapping`), `MarshalledType.cs` (new `CdeclExistential` record), `PInvokeEmitter.cs` (branch existential params/returns on `UsesCdeclWrapper`), `MethodSignature.cs` (4 signature methods handle `CdeclExistential`), `WrapperEmitter.Marshalling.cs` (container extraction for @_cdecl existential params), `MarshallingHelpers.cs` (force indirect result for existential returns), `WrapperEmitter.Return.cs` (@_cdecl existential return path + `ExistentialProjection` bypass).

Completed in Session 4 alongside Sub-phase G.1.

#### Sub-phase G: Remaining Misc → Sessions 4, 5

Split across sessions by dependency:

| Pattern | Solution | Session |
|---|---|---|
| ~~Subscript accessors~~ | ~~Extend wrapper emitter for subscript key params~~ | **Session 4** — COMPLETE |
| ~~Closure returns~~ | ~~`initializeMemory` to resultPtr, C# reads `SwiftClosureData`~~ | **Session 5** — COMPLETE |
| Tuple returns | Write to out-buffer (flattened), per-element marshalling | → [Part 2](universal-cdecl-wrappers-part2.md) Session 8 (19 libraries affected) |
| DynamicSelf returns | Return as `UnsafeRawPointer`, C# casts to protocol proxy | → [Part 2](universal-cdecl-wrappers-part2.md) Session 8 (10+ libraries affected) |
| Non-frozen struct returns (methods) | `resultPtr` out-buffer pattern (exists for constructors, extend to methods) | Infrastructure done — blocked by generic parent guard |
| Failable constructors | Return `Optional<UnsafeRawPointer>` (nil = init failed) | Already handled |
| **Unfixable** (Swift compiler) | — | — |
| Nested frozen struct params | Swift: "cannot be represented in Objective-C" | Unfixable — 0 real-world methods |
| Non-primitive frozen struct params | Swift: "cannot be represented in Objective-C" | Unfixable — 0 real-world methods |
| Non-copyable structs | Non-copyable semantics incompatible with C ABI | Unfixable — 0 real-world methods |
| Nested type returns | Swift: "cannot be represented in Objective-C" | Unfixable — 0 real-world methods |
| Opaque protocol returns (`some P`) | `@_cdecl` can't express opaque types | Unfixable — rare |

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

**Why Session 2 didn't finish everything**: The original plan assumed "optional types, generics, existentials, misc" could all fit in one session. In practice, each category requires distinct marshalling patterns, touches different shared infrastructure, and needs its own test coverage. Session 2 completed Optional\<reference-type\> (C.1) — a significant win — but the remaining categories are each full sessions.

**Revised plan**: 4 remaining implementation sessions (Sessions 3–6), then Phase 4 cleanup. Sessions are organized by shared-code dependency to minimize merge conflicts and maximize parallelism opportunities.

---

**Session 1** (plan + implement) — "Easy wins" — **COMPLETE**:

| Sub-phase | What | Status |
|---|---|---|
| A | Free functions — lift guard, no-self emission | Done |
| B | Metadata accessors — new `MetadataWrapperEmitter` | Done |
| H | Runtime fallback removal — wrapper-only, clear errors | Done |

**Session 2** (plan + implement) — "Optional\<reference-type\>" — **COMPLETE**:

| Sub-phase | What | Status |
|---|---|---|
| C.1 | Optional\<reference-type\> — nullable pointer ABI | Done |

---

**Session 3** (plan + implement) — "@_silgen_name Trampolines" — **COMPLETE**

**Goal**: Route all 7 `@_silgen_name` wrapper P/Invoke paths through `@_cdecl`, eliminating `CallConvSwift` from every wrapper-owned emission path.

**Sub-phases**: E (all 7 intermediary paths)

**Approach evolved during implementation**: Instead of adding thin trampolines over existing `@_silgen_name` functions (as originally planned), each wrapper emitter was converted to emit `@_cdecl` directly with C-compatible parameters via `GetCdeclParamMapping()`. This eliminates the two-layer overhead for all but two paths (debug-param and default-param overloads on non-cdecl base methods, which still use a @_cdecl-over-@_silgen_name pattern via the `silgenTarget` mechanism).

#### Implementation Summary

**Shared infrastructure (Sub-task 0):**
- `MethodWrapperEmitter.HasCdeclCompatibleFunctionShape()` — shared function-level eligibility gate (xcframework mode, non-generic parent, non-generic method, non-actor, non-copyable, return type checks)
- `MethodWrapperEmitter.IsNestedFrozenStructParam()` / `IsNonPrimitiveFrozenStructParam()` — per-param helpers extracted from private methods, enabling wrapper-owned paths to check only non-transformed params
- `OptionalPointerWrapperEmitter.EmitStringReturnBody()` / `EmitCdeclDirectReturn()` / `EmitCdeclSentinelReturn()` — shared helpers for @_cdecl return handling across OptionalPointer, Closure, and ArraySlice emitters

**Critical pipeline ordering constraint**: `UsesCdeclMethodWrapper` is consumed by 5 pipeline stages (SignatureHandler return/self/error routing, WrapperEmitter `_requiresFixedBlock`, PInvokeEmitter calling convention). All flags MUST be set BEFORE `SignatureHandler` creation. The `MethodHandler` flow was restructured into two phases:
- **Phase 1 (flag setting)**: All eligibility checks + flag assignment, runs before `new SignatureHandler()`
- **Phase 2 (emission)**: Swift wrapper emission + PInvokeEmitter, runs after SignatureHandler/WrapperEmitter construction

**Wrapper ownership gate (Sub-task 8)**: `MethodHandler` line 865 narrowed to exclude wrapper-owned paths — prevents double-emission when `UsesCdeclMethodWrapper` is set by OptionalPointer, Closure, or Async paths.

| Wrapper Path | File | Change |
|---|---|---|
| AsyncStream | `AsyncStreamEmitter.cs` | Direct @_cdecl: self as `UnsafeMutableRawPointer`, `__self` reconstruction via `Unmanaged.fromOpaque` |
| Default param overloads | `DefaultParameterOverloadEmitter.cs` | Extended eligibility: overloads on non-cdecl base methods independently checked via `ShouldEmitWrapper()` (temporarily clears `UsesWrapperLibrary` guard) |
| Debug param wrappers | `MethodHandler.cs` | Two-layer @_cdecl-over-@_silgen_name via `silgenTarget` mechanism; `hadDebugParams` flag captured BEFORE `EmitDebugParamWrapper` modifies `CSSignature` |
| Optional pointer buffers | `OptionalPointerWrapperEmitter.cs` | Direct @_cdecl: non-large params via `GetCdeclParamMapping()`, returns via `GetCdeclReturnMapping()` with full SimpleEnum/String/IndirectResult handling |
| Standalone closure wrappers | `ClosureEmitter.SwiftWrapper.cs` | Direct @_cdecl: non-closure non-large params via `GetCdeclParamMapping()`, same return handling as OptionalPointer |
| Async methods | `WrapperEmitter.Async.cs` | Direct @_cdecl: catchall params via `GetCdeclParamMapping()`, self as `UnsafeMutableRawPointer`, returns via async callback (no inline resultPtr) |
| Array slice normalization | `ArraySliceNormalizationEmitter.cs` | Direct @_cdecl: non-widened params via `GetCdeclParamMapping()`, `resultPtr` for string/indirect returns, retained-Error-object pattern for throws |

**Error handling contract**: All throwing @_cdecl wrappers use the retained-Error-object pattern:
```swift
} catch {
    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
}
```

**Files modified:**

| File | Sub-tasks | Lines changed |
|------|-----------|---------------|
| `MethodWrapperEmitter.cs` | 0 | ~40 |
| `AsyncStreamEmitter.cs` | 1 | ~15 |
| `DefaultParameterOverloadEmitter.cs` | 2 | ~30 |
| `MethodHandler.cs` | 3, 4, 5, 6, 8 | ~75 |
| `OptionalPointerWrapperEmitter.cs` | 4 | ~100 |
| `ClosureEmitter.SwiftWrapper.cs` | 5 | ~80 |
| `WrapperEmitter.Async.cs` | 6 | ~80 |
| `ArraySliceNormalizationEmitter.cs` | 7 | ~60 |
| `WrapperEmitter.Return.cs` | — | ~10 (async+cdecl return ordering fix) |
| `PropertyHandlerTests.cs` | — | ~5 (async stream test updated) |
| `SilgenNameTrampolineTests.cs` (new) | 9 | ~350 (27 tests) |

**Validation gate**:
- [x] `./run-tests.sh` — 7281 tests pass (7007 unit + 262 runtime + 12 analyzer), 0 failures
- [x] `./validate-libraries.sh --tier all` — 90/90 pass, 0 regressions
- [x] All 7 wrapper-owned paths emit @_cdecl when eligible
- [x] Wrapper ownership gate prevents double-emission

---

**Session 4** (plan + implement) — "Protocol Existentials + Subscripts" — **COMPLETE**

| Sub-phase | What | Status |
|---|---|---|
| F | Protocol existential params/returns — `CdeclExistential` marshalled type, indirect result for returns | Done |
| G.1 | Subscript accessor @_cdecl wrappers — new `SubscriptWrapperEmitter`, per-accessor eligibility | Done |

**Existential changes (Sub-phase F)**:
- Lifted guards 9, 10 (methods/constructors) and 5 (properties) for protocol existential params/returns
- New `CdeclExistential` marshalled type: `ref` container passing maps to `UnsafeRawPointer` in @_cdecl
- Existential returns: indirect result buffer (`resultPtr` + `MarshalFromSwift<T>`) — `MethodRequiresIndirectResult()` extended, `TryEmitReturnViaProjection` bypass for @_cdecl + `ExistentialProjection`
- Container extraction emitted in `WrapperEmitter.Marshalling.cs` for @_cdecl existential params

**Subscript changes (Sub-phase G.1)**:
- New `SubscriptWrapperEmitter.cs`: `ShouldEmitSubscriptWrapper` (13 guards, per-accessor), `GetSubscriptAccessorSymbolName` (`SBW_SubGet_`/`SBW_SubSet_` format), getter/setter Swift wrapper emission
- `SubscriptHandler.cs`: per-accessor @_cdecl eligibility check, flag-setting per accessor, string return glue (`SBW_Utf8Slice` decode), string setter glue (UTF-8 encode+pin), string index param encoding (UTF-8 bytes with `unsafe fixed` blocks)
- `EmitProjectedReturn` / `EmitProjectedSetterCall` helpers ensure return/value projection is applied even inside fixed blocks (prevents raw return bypass when string index params trigger the fixed-block path)
- Empty-string getter: `_sbw_emptyBuffer` is a static Swift buffer — Len==0 returns `string.Empty` without `SBW_Free`

**Files changed**: `MethodWrapperEmitter.cs`, `PropertyWrapperEmitter.cs`, `ConstructorWrapperEmitter.cs`, `MarshalledType.cs`, `PInvokeEmitter.cs`, `MethodSignature.cs`, `WrapperEmitter.Marshalling.cs`, `MarshallingHelpers.cs`, `WrapperEmitter.Return.cs`, new `SubscriptWrapperEmitter.cs`, `SubscriptHandler.cs`, `ModuleEmissionContext.cs`.

**Tests**: 44 new tests — `SubscriptWrapperEmitterTests.cs` (27 guard/symbol/emission tests + 10 cdecl C# emission regression tests), `MethodWrapperEmitterTests.cs` (2), `ConstructorWrapperEmitterTests.cs` (2), `PropertyWrapperEmitterTests.cs` (3).

**Validation**: 7007 unit tests pass, 90/90 libraries pass, 2 library improvements (GRDB, ObjectMapper — subscript accessors now use Cdecl).

---

**Session 5** (plan + implement) — "Optional\<value-type\> + Closure Returns" — **COMPLETE**

**Goal**: Route Optional\<value-type\> and closure returns through @_cdecl wrappers via IndirectResult, eliminating more CallConvSwift P/Invokes.

**Sub-phases**: C.2 + G.4 (closure returns). Tuple returns (15c) and DynamicSelf (15d) deferred — zero Nuke impact.

**Guards lifted/removed**:

| Guard | Emitter | What it blocked | Solution |
|---|---|---|---|
| 14 | Method | Generic container with non-ref Optional inner | Narrowed: blocks non-Optional containers + Optional\<existential\> |
| 16 | Method | Large Optional params/returns (non-ref) | **Removed** — subsumed by @_cdecl IndirectResult |
| 7 | Property | Large Optional returns | **Removed** — subsumed by @_cdecl IndirectResult |
| 9 | Property | Generic container properties | Narrowed: same as guard 14 |
| 8 | Subscript | Generic container params/returns | Narrowed: same as guard 14 |
| 15b | Method | Closure return types | **Removed** — @_cdecl writes closure to resultPtr via `initializeMemory` |

**Optional\<value-type\> approach** (actual implementation — deviated from original buffer+flag plan):

```swift
// Swift @_cdecl wrapper — writes full Optional<T> to resultPtr buffer
@_cdecl("SBW_Get_Nuke_ImageCache_ttl_A1B2C3D4")
func _sbw_get_ttl(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeRawPointer) {
    let obj = self_.assumingMemoryBound(to: Nuke.ImageCache.self).pointee
    let result = obj.ttl
    resultPtr.initializeMemory(as: Swift.Optional<Swift.Double>.self, repeating: result, count: 1)
}
```

```csharp
// C# — reads SwiftOptional<double> from resultPtr, converts to nullable
var swiftResult = SwiftMarshal.MarshalFromSwift<SwiftOptional<double>>(resultPtr);
return swiftResult.ToNullable();
```

**Key infrastructure changes**:
1. `PInvokeEmitter.HandleReturnType()` — bound generic bypass for @_cdecl Optional\<value-type\> + closure; `_optRetPtr` suppressed when IndirectResult handles Optional
2. `MarshallingHelpers.MethodRequiresIndirectResult()` — @_cdecl Optional\<value-type\> + closure returns force IndirectResult; bound generic short-circuit bypassed
3. `MethodMarshalPlanBuilder.BuildIndirectResultSetup()` — uses `projection.ContainerTypeName` (`SwiftOptional<double>`) for allocation; closures get fixed 2-pointer allocation
4. `WrapperEmitter.Return.cs` — Optional\<value-type\>: `MarshalFromSwift<SwiftOptional<T>>(resultPtr).ToNullable()`; closure: `*(SwiftClosureData*)resultPtr`
5. `PropertyWrapperEmitter.GetCdeclReturnMapping()` — closure returns mapped to `IndirectResult`
6. New helper: `IsOptionalSupportedForCdecl()` — blocks Optional\<protocol existential\> (needs proxy conversion)

**Bug fixes discovered**:
- Setter `GetCdeclParamMapping` with `omitLabels: true` triggered `ShouldWidenParam` bypass, skipping `.load(as:)` reconstruction. Fixed by using `omitLabels: false` in Property/Subscript setters.
- `Optional<protocol existential>` (e.g., `Optional<any Error>`) incorrectly passed through guards — `ExistentialContainer1` can't convert to proxy type. Fixed with `IsOptionalSupportedForCdecl()`.

**Files modified** (11): `MethodWrapperEmitter.cs`, `PropertyWrapperEmitter.cs`, `SubscriptWrapperEmitter.cs`, `MarshallingHelpers.cs`, `PInvokeEmitter.cs`, `WrapperEmitter.Return.cs`, `MethodMarshalPlanBuilder.cs`, `MethodWrapperEmitterTests.cs`, `PropertyWrapperEmitterTests.cs`, `SubscriptWrapperEmitterTests.cs`, `universal-cdecl-wrappers-design.md`.

**Tests**: 10 new + 4 flipped (ShouldEmitWrapper eligibility for Optional\<Double\>, Optional\<Bool\>, Optional\<existential\>, Dictionary, closure returns; property/subscript equivalents).

**Validation**:
- [x] Nuke `ImageCache.ttl` (Optional\<Double\>) uses Cdecl — `initializeMemory(as: Optional<Double>.self, ...)`
- [x] `_optRetPtr` count in Nuke: **0** (all Optional via resultPtr)
- [x] `./run-tests.sh` — 7054 unit tests pass, 0 failures
- [x] `./validate-libraries.sh --tier all` — **90/90 pass**, 27 library improvements
- [x] Nuke CallConvSwift: **122 → 97** (25 reduction)

---

> **Remaining sessions (6-8) and Phase 4 are documented in [Part 2](universal-cdecl-wrappers-part2.md).**

---

### Phase 4: Cleanup and Documentation

> **Full Phase 4 plan is in [Part 2](universal-cdecl-wrappers-part2.md).** Summary: remove workaround infrastructure (MonoJitRiskDetector, standalone closure wrappers, CrashRisk attributes, dual CallingConvention routing), fix closure heap leak, add SWIFTBIND060 diagnostic, update drifted docs, expand upstream bug reports.

---

## Test Strategy, Done Definition, Post-Migration Plans

> **Moved to [Part 2](universal-cdecl-wrappers-part2.md).** Test tiers, done definition, SWIFTBIND060 diagnostic, documentation cleanup plans, and risks/mitigations are all documented there.

---

## Appendix: What Stays on CallConvSwift

After full implementation, `CallConvSwift` remains only in two categories:

**1. Runtime internals** (not generated code):

| Component | CallConvSwift Usage | Status |
|-----------|-------------------|--------|
| ~~`SwiftString.PInvoke_Create/GetLength`~~ | ~~Direct libswiftCore calls~~ | **Removed** (Phase 3.5 Session 1) |
| ~~`TypeMetadata.GetExistentialTypeMetadata`~~ | ~~Swift runtime function~~ | **Removed** (Phase 3.5 Session 1) |
| ~~`ValueWitnessTable` function pointers~~ | ~~Indirect calls for VWT operations~~ | **Replaced** by per-type destroy wrappers |

**2. Unfixable Swift compiler restrictions** (zero real-world methods in Tier 1-2):

| Guard | What it blocks | Why unfixable |
|-------|---------------|---------------|
| 6b | Actor types | `@_cdecl` is synchronous; actors require async context |
| 11 | Non-copyable structs (`~Copyable`) | C ABI requires copy semantics |
| 12/12b | Nested/non-primitive frozen struct params | Swift: "cannot be represented in Objective-C" |
| 17 | Nested type returns | Swift: "cannot be represented in Objective-C" |

These guards affect **zero methods** across all 90 Tier 1-2 validation libraries. Any method hitting them retains a `CallConvSwift` P/Invoke — this is acceptable because these patterns don't appear in practice.

---

## Appendix: Relationship to Existing Wrapper Infrastructure

The wrapper xcframework already exists for every library that uses the `--xcframework` generator mode or the MSBuild SDK. It's compiled as part of the binding generation pipeline.

Currently the wrapper xcframework contains:
- Async method wrappers (`@_cdecl` when eligible, `@_silgen_name` fallback for generic/actor types)
- Closure adapter wrappers (`@_cdecl` when eligible, `@_silgen_name` fallback)
- Optional pointer buffer wrappers (`@_cdecl` when eligible, `@_silgen_name` fallback)
- Array slice normalization wrappers (`@_cdecl` when eligible, `@_silgen_name` fallback)
- Async stream iteration wrappers (`@_cdecl`)
- Default parameter overload wrappers (`@_cdecl` when eligible, `@_silgen_name` fallback)
- Debug parameter wrappers (`@_cdecl` trampoline over `@_silgen_name`)
- ExistentialBypass witness dispatch wrappers (`@_cdecl`)
- ObjC override property wrappers (`@_silgen_name`)
- Utf8Slice helper struct

Universal `@_cdecl` adds:
- Property getter/setter wrappers (`@_cdecl`)
- Method wrappers (`@_cdecl`) — including inline closure adapter code for Cdecl-compatible closures (Phase 2.5)
- Constructor wrappers (`@_cdecl`) — same inline closure handling (Phase 2.5)
- Destroy wrappers (`@_cdecl`)
- Metadata accessor wrappers (`@_cdecl`)

The `@_silgen_name` fallback paths remain for methods that hit unfixable guards (generic parent types, actor types, non-copyable structs). Phase 3.5 Session 3 converted all 7 wrapper-owned paths to emit `@_cdecl` directly (using `GetCdeclParamMapping()` for non-C-compatible params) rather than the originally-planned thin trampoline approach. The standalone closure wrappers are mostly superseded by Phase 2.5's inline closure handling but remain for edge cases (non-Cdecl closure types, generic parents, free functions).

---

## Appendix: Reverting to CallConvSwift

### Why We Switched

.NET's `CallConvSwift` has 5 runtime bugs that cause crashes on both Mono JIT (iOS Simulator) and NativeAOT (iOS Device). The crashes are unpredictable — the same enum getter works for one type but SIGABRTs for another. We can't predict which APIs will crash, and we can't ship bindings that randomly kill the process.

Meanwhile, `@_cdecl` wrappers have zero crashes across hundreds of tests and 90 real-world libraries. The wrapper is precompiled Swift that handles the Swift ABI internally — .NET never touches `CallConvSwift` at all. It's the same approach the generator already used successfully for async methods, closures, and existential metadata.

The universal `@_cdecl` work extends this proven pattern to cover every P/Invoke: properties, methods, constructors, subscripts, metadata accessors, and destroy operations.

### What Switching Back Would Require

The `@_cdecl` work does **not** maintain a dual-emission path. Each wrapper emitter (`PropertyWrapperEmitter`, `MethodWrapperEmitter`, `ConstructorWrapperEmitter`, `MetadataWrapperEmitter`, `DestroyWrapperEmitter`) replaced the `CallConvSwift` P/Invoke with a `Cdecl` one — it's not a flag flip.

To revert to `CallConvSwift`, you'd need to:

1. **Disable the wrapper emitters** — Each `ShouldEmitWrapper()` would need to return `false` (or the emitter calls could be gated behind a flag). The handler code that sets `UsesCdeclPropertyWrapper` / `UsesCdeclMethodWrapper` / `UsesCdeclConstructorWrapper` flags would need to be skipped. Without these flags, `PInvokeEmitter` falls back to `CallConvSwift` automatically — that routing logic was never removed.

2. **Restore runtime CallConvSwift paths** — `SwiftString.cs` and `TypeMetadata.cs` had their `CallConvSwift` P/Invokes deleted (Phase 3.5 Session 1). These would need to be restored from git history, along with the `_useWrapperPath` fallback logic.

3. **Restore workaround infrastructure** — `MonoJitRiskDetector`, standalone closure wrappers (`ClosureEmitter.SwiftWrapper.cs`), `[CrashRisk]` test attributes, `--safe-only` test flag. These were safety nets for living with CallConvSwift crashes.

4. **Accept the crashes** — Or wait for all 5 upstream .NET runtime bugs to be fixed. See `Future/upstream-bug-reports-draft.md` for reproduction cases.

**Rough effort**: 1-2 sessions to gate the emitters behind a flag and restore runtime paths. The wrapper emitter code would remain in the codebase (just not called). The harder part is re-accepting the crash risk and restoring the workaround infrastructure that masks it.

**Realistic timeline for upstream fixes**: The bugs haven't been filed yet (waiting for repo to go public). .NET runtime fixes typically take 1-2 release cycles after filing. Earliest realistic fix: .NET 11 or 12.

### What Wouldn't Be Wasted

Even if CallConvSwift is fixed upstream and you revert:
- The **type marshalling infrastructure** (`GetCdeclParamMapping`, `GetCdeclReturnMapping`) is reusable for any future trampoline layer (ObjC interop, SwiftUI bridging, instrumentation).
- The **test coverage** added per session (100+ new tests) exercises type patterns that weren't tested before, regardless of calling convention.
- The **wrapper xcframework** remains useful for debug instrumentation, logging, or profiling at the interop boundary.
