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

### Phase 1: Property Accessor Wrappers

**Sessions**: 1 plan + 1 implement
**Goal**: Route all property getters and setters through `@_cdecl` wrappers.
**Fixes**: Issue 7 (enum/string property crashes on device), partially Issue 2 (property access after wrapper also needs Destroy).
**Scope**: ~60% of all P/Invokes in typical bindings.

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

- [ ] All existing unit tests pass (`./run-tests.sh`)
- [ ] All property getter/setter P/Invokes in generated code use `CallingConvention.Cdecl`
- [ ] No `CallConvSwift` remains in property accessor P/Invokes
- [ ] All 90/90 library validation passes (`./validate-libraries.sh`)
- [ ] TestFramework NativeAOT tests pass (`./run-nativeaot-tests.sh`)
- [ ] Nuke `ImageRequest.PriorityValue` getter works on device (was Issue 7 crash)

---

### Phase 2: Method + Constructor Wrappers

**Sessions**: 1 plan + 1-2 implement (plan session determines if methods + constructors fit one session)
**Goal**: Route all instance methods, static methods, and constructors through `@_cdecl` wrappers.
**Fixes**: Issue 9 (non-blittable params like NSUrl), remaining Issue 7 constructor crashes.
**Scope**: Remaining ~40% of P/Invokes.

#### Planning Checklist

During the plan session, investigate and document:

1. **Method emission call chain** — Trace `MethodHandler.Emit()` → `WrapperEmitter.EmitMethod()` → `PInvokeEmitter.EmitPInvoke()`.
   - Entry: `MethodHandler.cs:495` (MethodHandler class)
   - P/Invoke emission: `MethodHandler.cs:834`
   - Method body: `WrapperEmitter.EmitMethod()` at `WrapperEmitter.cs:285-332`

2. **Constructor emission** — `ConstructorHandler` is a separate class in `MethodHandler.cs:63-462`.
   - `ConstructorWrapperEmitter.cs` already exists (185 lines) with full `@_cdecl` constructor pattern
   - Key question: is `ConstructorWrapperEmitter` already wired in, or does it need activation for all constructors?
   - Check which constructors currently get wrappers vs which fall through to CallConvSwift

3. **Multi-parameter marshalling** — Methods have multiple parameters of mixed types. Study `PInvokeSignatureBuilder.HandleArguments()` (lines 206-410 in `PInvokeEmitter.cs`) for the full parameter type matrix.

4. **Throwing methods** — How are Swift errors currently marshalled?
   - `PInvokeSignatureBuilder.HandleSwiftError()`: lines 575-594
   - `WrapperEmitter.cs` error handling in method body

5. **Self parameter differences** — `PInvokeSignatureBuilder.HandleSwiftSelf()` (lines 482-570) handles multiple self patterns (class, frozen struct, async, free function). Method wrappers need equivalent routing.

6. **Subscripts** — `SubscriptHandler.cs` (642 lines). Subscripts are indexed properties — getter/setter with index parameters. Determine if Phase 1's property wrapper can be extended or if subscripts need method-style wrappers.

7. **Default parameter overloads** — `DefaultParameterOverloadEmitter`. Each overload is a separate method with fewer parameters. Each needs its own wrapper.

8. **Scope decision** — Based on investigation, decide:
   - Can methods + constructors + subscripts fit one implementation session?
   - Or split: session 2a (methods + constructors using existing `ConstructorWrapperEmitter`), session 2b (special cases: throwing, subscripts, operators)?

#### Reference Patterns

| What to implement | Follow this pattern | File:Lines |
|------------------|--------------------|----|
| Method wrapper Swift emission | Constructor wrapper (extended to multi-param) | `ConstructorWrapperEmitter.cs:222-400` |
| Parameter type mapping (all types) | `GetCdeclParamMapping()` | `ConstructorWrapperEmitter.cs:407-579` |
| Closure parameter C-type mapping | `ClosureEmitter.SwiftWrapper.GetSwiftConventionCType()` | `ClosureEmitter.SwiftWrapper.cs:19-99` |
| Swift type rendering in wrappers | `ExistentialBypassEmitter.RenderSwiftTypeSpec()` | `ExistentialBypassEmitter.cs:1107-1173` |
| Error handling in wrappers | Async wrapper error handling | `WrapperEmitter.Async.cs` |
| Existing constructor wrappers | Already-implemented `@_cdecl` constructors | `ConstructorWrapperEmitter.cs` (entire file) |

#### Implementation Scope

**Files to create:**
- `MethodWrapperEmitter.cs` (~400-600 lines) — method/static/subscript `@_cdecl` wrapper emission

**Files to modify:**
- `MethodHandler.cs` — Hook method wrapper emitter into method emission (~30-40 lines)
- `ConstructorWrapperEmitter.cs` — Possibly expand guards to cover more constructor types (~20 lines)
- `PInvokeEmitter.cs` — Extend Cdecl routing for methods (~20 lines)
- `ModuleEmissionContext.cs` — Add method wrapper symbol dedup set (~10 lines)

**Files to create (tests):**
- `MethodWrapperEmitterTests.cs` (~1,000-1,500 lines)

**Estimated new code**: ~1,500-2,200 lines (emitter + tests)

#### Special Cases

- **Throwing methods**: Wrapper catches Swift errors, returns error info via out-parameter or error code.
- **Methods returning `Self`**: Wrapper must retain and return as opaque pointer.
- **Default parameter overloads**: Each overload gets its own wrapper with the appropriate parameter subset.
- **Subscripts**: Getter/setter wrapper pattern, similar to properties but with index parameters.

#### Validation Gate

- [ ] All method/constructor P/Invokes use `CallingConvention.Cdecl`
- [ ] Zero `CallConvSwift` in any generated P/Invoke declaration
- [ ] Nuke `ImagePipeline.ImageTask(NSUrl)` works on device (was Issue 9)
- [ ] Lottie `LottieAnimation.From(data, strategy)` works on device (was Issue 7)
- [ ] `./run-tests.sh` — all unit tests pass
- [ ] `./validate-libraries.sh` — 90/90 still passes

---

### Phase 3: Destroy Wrappers + DllImport Resolver

**Sessions**: 1 implement (no separate plan needed — both components are small and well-defined)
**Goal**: Fix `Dispose()` crash (Issue 2) and eliminate consumer DllImport boilerplate (Issue 4).
**Fixes**: Issue 2 (VWT Destroy SIGSEGV), Issue 4 (late-loaded assembly resolver), Issue 5 (SB1001 analyzer guidance).

#### Part A: Destroy Wrappers

**Already mostly implemented.** `DestroyWrapperEmitter.cs` (157 lines) exists with full `@_cdecl` destroy wrapper emission + C# P/Invoke registration. `SwiftSafeHandle<T>` already has `RegisterDestroyAction()` infrastructure.

**What changes:**

| File | Change |
|------|--------|
| `DestroyWrapperEmitter.cs` | Verify all bound types emit destroy wrappers (may already be complete) |
| Type handlers (ClassHandler, StructHandler) | Ensure `DestroyWrapperEmitter.EmitIfNeeded()` is called for all types |
| `SwiftHandle.cs` (runtime) | Verify `RegisterDestroyAction` + `ReleaseHandle()` fallback is correct |

**Reference**: `DestroyWrapperEmitter.cs` — entire file is the implementation. Tests: `DestroyWrapperEmitterTests.cs` (302 lines).

**Estimated changes**: ~50-100 lines (integration wiring only).

#### Part B: DllImport Resolver

| File | Change |
|------|--------|
| `SwiftBindings.Runtime` (new initializer) | Hook `AppDomain.CurrentDomain.AssemblyLoad` to register resolvers for `SwiftBindings.*` assemblies automatically |
| Generated `[ModuleInitializer]` | Delegate to runtime's centralized resolver registration |

**Estimated changes**: ~100-150 lines.

#### Validation Gate

- [ ] `Dispose()` / `using` works on device for all bound types without crash
- [ ] SB1001 analyzer guidance is now correct and safe to follow
- [ ] `cd-dispose-*` NativeAOT tests all pass
- [ ] Consumer app works without manual `AssemblyLoad` hook
- [ ] Multiple SwiftBindings packages load correctly in same app
- [ ] Issues 2 and 4 are closed

---

### Phase 4: Cleanup and Documentation

**Sessions**: 1 implement
**Goal**: Remove workaround code, update documentation, preserve upstream issue documentation for Microsoft.

#### What Gets Removed (Code)

| Component | Why it's unnecessary |
|-----------|---------------------|
| Workaround A: `SwiftString` runtime wrappers in `libSwiftBindingsRuntime` | SwiftString operations go through per-library `@_cdecl` wrappers |
| Workaround B: Closure Cdecl expansion (`ClosureEmitter.SwiftWrapper.cs`) | All methods already use `@_cdecl` |
| Workaround C: Existential metadata wrapper in `libSwiftBindingsRuntime` | Moved to per-library wrapper or kept in runtime with `@_cdecl` |
| Workaround D: `MonoJitRiskDetector` | No risky path exists |
| `libSwiftBindingsRuntime.dylib` build pipeline | Functions moved to per-library wrappers (or consolidated into runtime package's own `@_cdecl` helpers) |
| `[CrashRisk]` attributes on test classes | All tests should pass on both Mono and NativeAOT |
| `--safe-only` flag in test runner | No unsafe tests |
| Tier 3 deferral for Mono JIT tests | All tiers run everywhere |
| `_useWrapperPath` fallback logic in `SwiftString.cs` | Single code path |
| `HasClosureCdeclWrapper` / `UsesFreeFunctionWrapper` flags | All methods use wrappers |

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

- [ ] `MonoJitRiskDetector.cs` deleted
- [ ] `ClosureEmitter.SwiftWrapper.cs` deleted (or repurposed)
- [ ] `libSwiftBindingsRuntime.dylib` removed from build pipeline
- [ ] `[CrashRisk]` attributes removed from all test classes
- [ ] `--safe-only` flag removed from test runner
- [ ] All tests run as Tier 1/2 on both Mono and NativeAOT
- [ ] Full test suite passes without any workaround code
- [ ] Library validation 90/90 still passes
- [ ] `known-issues-workarounds.md` rewritten: explains `@_cdecl` approach, documents `UseDirectSwiftPInvoke` revert flag, retains upstream bug references
- [ ] `Future/upstream-bug-reports-draft.md` expanded with all 5 .NET runtime bugs, each with minimal reproduction case
- [ ] `UseDirectSwiftPInvoke` documented in `docs/Troubleshooting.md`

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

- [ ] **Zero `CallConvSwift` in generated code** — all P/Invokes use `CallingConvention.Cdecl` by default
- [ ] **`UseDirectSwiftPInvoke` flag works** — setting it to `true` switches back to direct `CallConvSwift` P/Invokes with no code gen changes
- [ ] **All 10 KNOWN-ISSUES resolved or documented as .NET runtime limitations** — Issues 1, 2, 7, 9, 10 fixed by this work; Issues 4, 5 fixed by Phase 3; Issues 6, 8 are separate feature work
- [ ] **Nuke + Lottie device test suites: 0 crashes** — all previously-skipped tests pass
- [ ] **No workaround infrastructure remains** — single clean code path
- [ ] **Upstream bugs documented** — `Future/upstream-bug-reports-draft.md` expanded with all 5 bugs, ready to file on dotnet/runtime when repo is public
- [ ] **End-user documentation clear** — `known-issues-workarounds.md` explains that SwiftBindings bypasses `CallConvSwift` via `@_cdecl` wrappers for stability, with the option to switch back via `UseDirectSwiftPInvoke` once Microsoft fixes the underlying runtime issues

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
- Closure adapter wrappers (`@_silgen_name`)
- ExistentialBypass witness dispatch wrappers (`@_cdecl`)
- ObjC override property wrappers (`@_silgen_name`)
- Utf8Slice helper struct

Universal `@_cdecl` adds:
- Property getter/setter wrappers (`@_cdecl`)
- Method wrappers (`@_cdecl`)
- Constructor wrappers (`@_cdecl`)
- Destroy wrappers (`@_cdecl`)

The existing `@_silgen_name` wrappers (async, closures) could also be migrated to `@_cdecl` for consistency in a future pass, but they already work correctly because they use `@convention(c)` callbacks — the Swift ABI is internal to the wrapper.

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
