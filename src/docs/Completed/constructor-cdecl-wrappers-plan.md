# Constructor `@_cdecl` Wrappers — Implementation Plan

**Created**: March 9, 2026
**Status**: Ready (v5 — incorporates Codex review rounds 1+2+3+4)
**Blocks**: Issues #10 (LottieAnimationView SIGSEGV), #12 (LottieColor SIGSEGV)
**Depends on**: Destroy wrapper infrastructure (completed)

---

## Problem

Constructor P/Invokes use `CallConvSwift`, which is broken on NativeAOT/ARM64 for:

- **Struct parameters** (CGRect etc.) — register splitting mismatch → SIGSEGV
- **Non-frozen struct indirect results** — NativeAOT mishandles `SwiftIndirectResult` → SIGSEGV

This blocks all constructors that take structs or return non-frozen structs on device. The `__swift_memcpy24_8` crash (24-byte memcpy for 32-byte struct) confirms ABI mismatch between NativeAOT's CallConvSwift implementation and actual Swift ABI on ARM64.

## Solution

Route ALL constructor P/Invokes through `@_cdecl` Swift wrappers using **C calling convention** (`CallingConvention.Cdecl`), eliminating `CallConvSwift` for constructors entirely. Only in xcframework mode (where the wrapper library exists).

**Core principle**: Preserve existing constructor wrapper/body semantics — only swap the native ABI boundary to cdecl. The existing projection/marshal-plan pipeline stays intact on the C# side. The `@_cdecl` wrapper on the Swift side absorbs all ABI translation.

### Why `@_cdecl` and not `@_silgen_name`

- `@_silgen_name` preserves Swift calling convention — same ABI, same crash
- `@_cdecl` uses C calling convention — NativeAOT handles C ABI correctly
- Destroy wrappers already use `@_cdecl` and are validated on device

---

## Design

### Key Design Principle: ABI Boundary Swap, Not Marshalling Rewrite

The `@_cdecl` wrapper sits between the C# P/Invoke and the actual Swift init. Its job is to translate the C calling convention into whatever the Swift init expects. The C# side continues to use the existing projection/marshal-plan infrastructure — it just talks to a different ABI at the boundary.

This means:
- **C# constructor body** (`WrapperEmitter.EmitConstructor`) keeps existing marshalling logic
- **P/Invoke signature** changes calling convention and parameter types to C-compatible
- **Swift `@_cdecl` wrapper** receives C-compatible params, reconstructs Swift types, calls init
- **Existing marshal-plan outputs** (projection conversions, PayloadBuffer, bound generic buffers) remain as-is on the C# side — only the final P/Invoke parameter types change

### Swift Side

For each constructor, emit a `@_cdecl` function in the wrapper library:

```swift
// Class constructor (returns retained pointer)
@_cdecl("SBW_Lottie_LottieAnimationView_init_8B933573")
public func _sbw_init_8B933573(_ frame: UnsafeRawPointer) -> UnsafeMutableRawPointer {
    let result = Lottie.LottieAnimationView(frame: frame.load(as: CGRect.self))
    return Unmanaged.passRetained(result).toOpaque()
}

// Non-frozen struct constructor (writes to result buffer)
@_cdecl("SBW_Lottie_LottieColor_init_A1B2C3D4")
public func _sbw_init_A1B2C3D4(
    _ resultPtr: UnsafeMutableRawPointer,
    _ r: Double, _ g: Double, _ b: Double, _ a: Double, _ denominator: Double
) {
    let result = Lottie.LottieColor(r: r, g: g, b: b, a: a, denominator: denominator)
    resultPtr.initializeMemory(as: Lottie.LottieColor.self, repeating: result, count: 1)
}

// Failable class constructor (returns nullable pointer)
@_cdecl("SBW_Module_MyClass_init_HASH")
public func _sbw_init_HASH(_ param: Int) -> UnsafeMutableRawPointer? {
    guard let result = Module.MyClass(param: param) else { return nil }
    return Unmanaged.passRetained(result).toOpaque()
}

// Failable non-frozen struct constructor (writes Optional<Self> to result buffer)
@_cdecl("SBW_Module_MyStruct_init_HASH")
public func _sbw_init_HASH(
    _ resultPtr: UnsafeMutableRawPointer,
    _ param: Int
) {
    let result: Module.MyStruct? = Module.MyStruct(param: param)
    resultPtr.initializeMemory(as: Optional<Module.MyStruct>.self, repeating: result, count: 1)
}

// Throwing class constructor (error out-pointer)
@_cdecl("SBW_Module_MyClass_init_throws_HASH")
public func _sbw_init_throws_HASH(
    _ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    _ param: Int
) -> UnsafeMutableRawPointer? {
    do {
        let result = try Module.MyClass(param: param)
        return Unmanaged.passRetained(result).toOpaque()
    } catch {
        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
        return nil
    }
}

// Throwing struct constructor (error out-pointer + result buffer)
@_cdecl("SBW_Module_MyStruct_init_throws_HASH")
public func _sbw_init_throws_HASH(
    _ resultPtr: UnsafeMutableRawPointer,
    _ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    _ param: Int
) {
    do {
        let result = try Module.MyStruct(param: param)
        resultPtr.initializeMemory(as: Module.MyStruct.self, repeating: result, count: 1)
    } catch {
        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
    }
}

// Failable + throwing class constructor (error out-pointer, null = failed-or-nil)
@_cdecl("SBW_Module_MyClass_init_failable_throws_HASH")
public func _sbw_init_failable_throws_HASH(
    _ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    _ param: Int
) -> UnsafeMutableRawPointer? {
    do {
        guard let result = try Module.MyClass(param: param) else { return nil }
        return Unmanaged.passRetained(result).toOpaque()
    } catch {
        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
        return nil
    }
}
```

### C# Side

The existing P/Invoke emission infrastructure (`PInvokeEmitHelper`, `PInvokeEmissionInfo`, `PInvokeHelperContext`) already supports `PInvokeCallingConvention.Cdecl`. Use it — no new DllImport path needed.

```csharp
// Before (crashes on NativeAOT):
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[LibraryImport("Lottie", EntryPoint = "$s6Lottie0A13AnimationViewC5frameACSo6CGRectV_tcfC")]
private static partial IntPtr PInvoke_init(Swift.CGRect frame);

// After (safe @_cdecl, using existing PInvokeEmitHelper with CallingConvention = Cdecl):
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
[LibraryImport("LottieSwiftBindings", EntryPoint = "SBW_Lottie_LottieAnimationView_init_8B933573")]
private static partial IntPtr PInvoke_init(IntPtr frame);
```

---

## Parameter Marshalling: Reuse Existing Projections

**Critical insight from Codex review**: Constructor arguments already flow through projection/marshal-plan logic. Some "struct-like" inputs (frozen structs projected as classes) require PayloadBuffer handling, not raw pointer loads. The wrapper must **reuse existing projection/marshal-plan outputs** and only lower the final ABI to cdecl.

### How It Works

1. **C# side** — existing marshalling pipeline runs as-is:
   - `EmitTypeConversions()` → projection factory produces marshal-plan statements
   - `EmitBoundGenericArguments()` → PayloadBuffer extraction for frozen-struct-as-class
   - `EmitSafeHandleAddRef()` → SafeHandle reference counting
   - The marshalled values (already `IntPtr`, `PayloadBuffer`, etc.) feed into the P/Invoke call

2. **P/Invoke signature** — the `PInvokeSignatureBuilder` computes cdecl-compatible parameter types:
   - Primitives → pass directly (same as today)
   - Struct/class/enum params → `IntPtr` (pointer to data)
   - Bool → `[MarshalAs(UnmanagedType.U1)] byte`
   - Result buffer → `IntPtr` (for struct constructors, replaces `SwiftIndirectResult`)
   - Error out → `IntPtr` (for throwing constructors, replaces `SwiftError`)

3. **Swift `@_cdecl` wrapper** — receives C-compatible params, reconstructs Swift types:
   - Primitives → no conversion needed
   - Structs → `UnsafeRawPointer` → `.load(as: T.self)`
   - Classes → `UnsafeMutableRawPointer` → `Unmanaged<T>.fromOpaque(_).takeUnretainedValue()`
   - ObjC-bridged → `UnsafeMutableRawPointer` (passed as object pointer from .Handle)

### Swift-Side Parameter Type Mapping

| C# Marshal Output | Swift `@_cdecl` Param | Reconstruction |
|---|---|---|
| Primitives (int, double, etc.) | Same type directly | None |
| Bool (byte via MarshalAs) | `Int8` | `param != 0` |
| Frozen struct (PayloadBuffer → IntPtr) | `UnsafeRawPointer` | `.load(as: T.self)` |
| Non-frozen struct (SafeHandle → IntPtr) | `UnsafeMutableRawPointer` | `.load(as: T.self)` |
| Class (SafeHandle → IntPtr) | `UnsafeMutableRawPointer` | `Unmanaged<T>.fromOpaque(_).takeUnretainedValue()` |
| ObjC-bridged (.Handle → IntPtr) | `UnsafeMutableRawPointer` | Bridged via ObjC runtime |
| Simple enum (raw value) | Raw value type | Cast |
| Complex enum (IntPtr to data) | `UnsafeRawPointer` | `.load(as: T.self)` |
| Closures | Gate out initially | — |

### Return Type Handling

| Constructor Type | Swift Wrapper Return | C# P/Invoke Return |
|---|---|---|
| Class (Swift-rooted) | `UnsafeMutableRawPointer` (retained) | `IntPtr` → `SwiftSafeHandle<T>` |
| Class (ObjC-rooted) | `UnsafeMutableRawPointer` (retained) | `IntPtr` → `NativeHandle` |
| Struct (any) | void, writes to `resultPtr` param | Pass `IntPtr` buffer, read after call |

---

## Struct Result Buffer: `IntPtr`, NOT `SwiftIndirectResult`

**Codex P1 finding (v3)**: The `@_cdecl` wrapper takes a plain C ABI `resultPtr` parameter, NOT a Swift ABI indirect-result register. `SwiftIndirectResult` is a Swift calling convention concept — it tells the NativeAOT runtime to pass a buffer pointer through a specific Swift register. Under `@_cdecl`, the buffer is just a regular `IntPtr` parameter.

### What Changes

**`PInvokeSignatureBuilder.HandleReturnType()`** (line 129-133):
Currently injects `SwiftIndirectResult swiftIndirectResult` for non-frozen struct constructors. When `UsesCdeclConstructorWrapper` is set, inject a plain `IntPtr resultPtr` parameter instead:

```csharp
// Current (Swift ABI):
if (MarshallingHelpers.MethodRequiresIndirectResult(_env))
{
    AddParameter("SwiftIndirectResult", "swiftIndirectResult");
    SetReturnType("void");
    return;
}

// With cdecl wrapper:
if (methodDecl.UsesCdeclConstructorWrapper && MarshallingHelpers.MethodRequiresIndirectResult(_env))
{
    AddParameter("IntPtr", "resultPtr");  // plain C pointer, not Swift register
    SetReturnType("void");
    return;
}
```

**`MethodMarshalPlanBuilder.BuildIndirectResultSetup()`** (line 287-337):
Currently builds `SyncMethodPlan.IndirectResultConstructor` with:
```csharp
AllocationCode = $"""
    _payload = new SwiftSafeHandle<{safeHandleTypeName}>((IntPtr)NativeMemory.Alloc(_payloadSize));
    var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());
    """
```

When `UsesCdeclConstructorWrapper`, produce code that allocates the buffer BUT does NOT create `SwiftIndirectResult`:
```csharp
AllocationCode = $"""
    _payload = new SwiftSafeHandle<{safeHandleTypeName}>((IntPtr)NativeMemory.Alloc(_payloadSize));
    var resultPtr = _payload.DangerousGetHandle();
    """
```

**`MethodMarshalPlanBuilder.BuildPInvokeCallStatement()`** (line 451):
The `CallArgumentsString()` from `PInvokeSignatureBuilder` already produces the argument list. The variable name changes from `swiftIndirectResult` to `resultPtr`, which is enough — the call argument mapping uses the same variable name.

**`WrapperEmitter.EmitIndirectResultConstructor()`** (line 355):
Reads from `_syncPlan.IndirectResultConstructor.AllocationCode`. The plan builder produces the correct code based on the flag — no change needed in `WrapperEmitter` itself.

### What Does NOT Change

- `_payload` allocation (`NativeMemory.Alloc`) — same
- `_payloadSize` computation — same
- Post-call `_payload.DangerousGetHandle()` reads — same
- `EmitReturnConstructor()` — same (struct path doesn't read a return value)
- `EmitFailableFactory()` struct path — same VWT tag inspection on the result buffer

The only concrete change is: `SwiftIndirectResult(buffer)` → plain `IntPtr buffer` in the P/Invoke parameter list and the allocation code.

---

## Failable Constructors (`init?`) — Explicit Contract

**Codex P1 finding (v2)**: The original plan's blanket `IntPtr.Zero == nil` rule only works for pointer-returning class cases. Value-type `Optional<Self>` requires the existing VWT enum tag inspection.

### Failable Class Constructors

For classes, `init?` returns a nullable pointer. The `@_cdecl` wrapper handles this naturally:

```swift
// Swift @_cdecl wrapper
@_cdecl("SBW_Module_MyClass_init_HASH")
public func _sbw_init_HASH(_ param: Int) -> UnsafeMutableRawPointer? {
    guard let result = Module.MyClass(param: param) else { return nil }
    return Unmanaged.passRetained(result).toOpaque()
}
```

C# side: The failable factory (`WrapperEmitter.EmitFailableFactory`) checks for `IntPtr.Zero` instead of VWT enum tag:

```csharp
// Simplified: no SwiftOptional metadata, no VWT tag inspection
var resultPtr = PInvoke_init(params);
if (resultPtr == IntPtr.Zero) { result = default; return false; }
result = new TypeName((SwiftHandle)resultPtr);
return true;
```

### Failable Value-Type Constructors (Structs/Enums)

For value types, `init?` returns `Optional<Self>` — the wrapper writes the full Optional into the result buffer, **preserving the existing enum-tag contract**:

```swift
// Swift @_cdecl wrapper — writes Optional<Self> into result buffer
@_cdecl("SBW_Module_MyStruct_init_HASH")
public func _sbw_init_HASH(
    _ resultPtr: UnsafeMutableRawPointer,
    _ param: Int
) {
    let result: Module.MyStruct? = Module.MyStruct(param: param)
    resultPtr.initializeMemory(as: Optional<Module.MyStruct>.self, repeating: result, count: 1)
}
```

C# side: The failable factory (`WrapperEmitter.EmitFailableFactory`) **keeps the existing VWT-based tag inspection**. The only change is:
- P/Invoke uses `IntPtr resultPtr` instead of `SwiftIndirectResult swiftIndirectResult`
- The allocation code creates the buffer without wrapping in `SwiftIndirectResult`
- All post-call inspection (`GetEnumTag`, `InitializeWithCopy`, payload extraction) stays identical

### Failable Constructor Decision Matrix

| Parent Type | `@_cdecl` Return | C# Factory Contract | VWT Tag Check? |
|---|---|---|---|
| Class (Swift-rooted) | `UnsafeMutableRawPointer?` | `IntPtr.Zero` == nil | No |
| Class (ObjC-rooted) | `UnsafeMutableRawPointer?` | `IntPtr.Zero` == nil | No |
| Struct (frozen value) | void + writes `Optional<Self>` to buffer | Existing FailableFactory path | Yes — `GetEnumTag` |
| Struct (non-frozen / frozen+MM) | void + writes `Optional<Self>` to buffer | Existing FailableFactory path | Yes — `GetEnumTag` + `InitializeWithCopy` |

---

## Throwing Constructors (`init() throws`) — Explicit Contract

**Codex P2 finding (v3)**: The current pipeline always carries `SwiftError` for throwing constructors. `SwiftError` is a Swift calling convention concept — the error is returned through a dedicated Swift register. Under `@_cdecl` (C ABI), that register doesn't exist. The plan must define how thrown Swift errors are reported through the C ABI boundary.

### Error Reporting via Out-Pointer

The `@_cdecl` wrapper catches Swift errors via `do/catch` and returns the error as a retained `AnyObject` pointer through an explicit out-parameter. This maps cleanly to an `IntPtr*` on the C# side.

**Swift side** — `@_cdecl` wrapper catches and boxes the error:

```swift
// Throwing class constructor
@_cdecl("SBW_Module_MyClass_init_throws_HASH")
public func _sbw_init_throws_HASH(
    _ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    _ param: Int
) -> UnsafeMutableRawPointer? {
    do {
        let result = try Module.MyClass(param: param)
        return Unmanaged.passRetained(result).toOpaque()
    } catch {
        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
        return nil  // signals failure; C# checks errorOut, not return value
    }
}

// Throwing struct constructor
@_cdecl("SBW_Module_MyStruct_init_throws_HASH")
public func _sbw_init_throws_HASH(
    _ resultPtr: UnsafeMutableRawPointer,
    _ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    _ param: Int
) {
    do {
        let result = try Module.MyStruct(param: param)
        resultPtr.initializeMemory(as: Module.MyStruct.self, repeating: result, count: 1)
    } catch {
        errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
    }
}
```

**C# side** — replaces `SwiftError swiftError` with `IntPtr* errorOut`:

```csharp
// P/Invoke signature (cdecl wrapper mode):
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]
[LibraryImport("ModuleSwiftBindings", EntryPoint = "SBW_Module_MyClass_init_throws_HASH")]
private static unsafe partial IntPtr PInvoke_init(IntPtr* errorOut, int param);

// Constructor body:
IntPtr errorPtr = IntPtr.Zero;
var resultPtr = PInvoke_init(&errorPtr, param);
if (errorPtr != IntPtr.Zero)
{
    // Existing error handling infrastructure — SBW_GetErrorDescription, SBW_ReleaseError,
    // SBW_ExtractTypedError — all use @_cdecl and are already in the wrapper library.
    // The error pointer is the same retained AnyObject as SwiftError.Value.
    string _errorMessage;
    var _descPtr = SBW_GetErrorDescription(errorPtr);
    try { _errorMessage = Marshal.PtrToStringUTF8(_descPtr) ?? "Unknown Swift error"; }
    finally { if (_descPtr != IntPtr.Zero) SBW_Free(_descPtr); SBW_ReleaseError(errorPtr); }
    throw new SwiftRuntimeException(_errorMessage);
}
```

### Why This Works Seamlessly

The retained error pointer from `Unmanaged.passRetained(error as AnyObject).toOpaque()` is **identical** to what `SwiftError.Value` contains today. The existing error infrastructure (`SBW_GetErrorDescription`, `SBW_ReleaseError`, `SBW_ExtractTypedError_*`) all accept this same opaque pointer and are themselves `@_cdecl` functions. No changes needed to the error description/extraction infrastructure.

### Changes Required

**`PInvokeSignatureBuilder.HandleSwiftError()`** (line 567):
Currently adds `SwiftError swiftError` with `out` modifier. When `UsesCdeclConstructorWrapper`:
```csharp
// Current:
AddParameter("SwiftError", "swiftError", "out");

// With cdecl wrapper:
AddParameter("IntPtr", "errorOut", modifier: null, isUnsafePointer: true);
// Emits: IntPtr* errorOut  (unsafe pointer parameter)
```

**`MethodMarshalPlanBuilder.BuildSwiftErrorSetup()`** (line 347):
The `ErrorCheckCode` currently references `swiftError.Value`. When `UsesCdeclConstructorWrapper`, the check code references `errorPtr` instead:
```csharp
// Current:
"if (swiftError.Value != null) { ... SBW_GetErrorDescription((IntPtr)swiftError.Value) ... }"

// With cdecl wrapper:
"if (errorPtr != IntPtr.Zero) { ... SBW_GetErrorDescription(errorPtr) ... }"
```
The error description/release/extraction calls stay identical — only the variable name and null check change.

**`WrapperEmitter.EmitSwiftError()`** (line 397):
Reads from `_syncPlan.SwiftError.ErrorCheckCode`. The plan builder produces the correct code based on the flag — no change needed in `WrapperEmitter`.

### Throwing Constructor Decision Matrix

| Constructor Type | `@_cdecl` Error Mechanism | C# Error Check | Reuses Existing Infra? |
|---|---|---|---|
| `init() throws` (class) | `errorOut` pointer + nil return | `errorPtr != IntPtr.Zero` | Yes — `SBW_GetErrorDescription` etc. |
| `init() throws` (struct) | `errorOut` pointer | `errorPtr != IntPtr.Zero` | Yes — same |
| `init?() throws` (class) | `errorOut` pointer + nil return | Check error first, then nil-check return | Yes |
| `init?() throws` (struct) | `errorOut` pointer + `Optional<Self>` in buffer | Check error first, then VWT tag check | Yes |
| Typed `init() throws(E)` | Same `errorOut` pointer | `SBW_ExtractTypedError_*` on errorPtr | Yes — same opaque pointer |

---

## P/Invoke Emission: Use Existing Infrastructure

**Codex P2 finding (v2)**: Don't create a separate DllImport path. The existing `PInvokeEmitHelper` / `PInvokeEmissionInfo` already supports `PInvokeCallingConvention.Cdecl`, and `PInvokeHelperContext` / `PInvokeDeclaration` already has `OmitCallingConvention` (which maps to Cdecl). Reuse this.

### For Non-Generic Types

`PInvokeEmitter.EmitPInvoke()` already builds a `PInvokeEmissionInfo` and calls `PInvokeEmitHelper.EmitDeclaration()`. When `UsesCdeclConstructorWrapper` is set:
- Set `CallingConvention = PInvokeCallingConvention.Cdecl`
- Update entry point and library path to the wrapper symbol/library
- The rest of the emission logic stays the same

### For Generic Types

`PInvokeEmitter.EmitPInvoke()` already routes generic types through `PInvokeHelperContext.AddDeclaration()` (line 678). The `PInvokeDeclaration` already has `OmitCallingConvention` which maps to Cdecl. Set this flag when `UsesCdeclConstructorWrapper` is true. No new code path needed.

### Generic Type Gate: Swift Side Is the Real Blocker

The generic constructor wrapper gate is on the **Swift side**, not just C# CS7042. `DefaultParameterOverloadEmitter` already skips generic parent types (line 49-53) because Swift extension syntax can't express generic parameters (`extension Keyframe` instead of `extension Keyframe<T>`), and generic type params (`τ_0_0`) aren't valid Swift identifiers. The same constraint applies to `@_cdecl` constructor wrappers.

For generic types: fall back to current `CallConvSwift` P/Invoke. This is acceptable because:
- Most consumer-facing constructors are on non-generic types
- The pattern matches destroy wrappers (which also skip generics)
- Generic constructor crashes on device would need a different solution (e.g., TypeMetadata-based dispatch)

---

## Implementation Steps

### Step 1: `ConstructorWrapperEmitter.cs` (new file)

New emitter following the `DestroyWrapperEmitter` pattern.

**Key methods:**
- `ShouldEmitWrapper(env)` — **pure query, no side effects.** Guards: xcframework mode, non-generic parent type, has wrapper lib. Called BEFORE `SignatureHandler` construction to set `MethodDecl` flags early enough.
- `EmitSwiftConstructorWrapper(swiftWriter, env, ctx)` — renders `@_cdecl` Swift function per constructor. Handles:
  - Parameter type conversion (primitives pass through, structs/classes as `UnsafeRawPointer`/`UnsafeMutableRawPointer`)
  - Return type: class → `UnsafeMutableRawPointer`, struct → void + `resultPtr` param
  - Failable: class → `UnsafeMutableRawPointer?`, struct → writes `Optional<Self>` to `resultPtr`
  - Throwing: `do/catch` block, error written to `errorOut` pointer parameter
  - Failable + throwing: combined pattern (error out-pointer + nil return or Optional buffer)
  - Default parameter overloads: wrapper calls the existing `@_silgen_name` function (no double-wrapping of Swift init logic)
- `GetConstructorSymbolName(moduleName, typeName, hash)` — naming: `SBW_{Module}_{Type}_init_{Hash}`
- Dedup via `ModuleEmissionContext.TryAddConstructorWrapperSymbol()`

**Does NOT emit C# P/Invoke** — that stays in `PInvokeEmitter.EmitPInvoke()` using existing infrastructure.

**Naming convention:** `SBW_{Module}_{Type}_init_{Hash}` where hash is the existing P/Invoke hash from `NameProvider`.

### Step 2: MethodHandler Integration — ALL Mutations BEFORE SignatureHandler

**Codex P1 findings (v4, v5)**: `SignatureHandler` construction triggers `PInvokeSignatureBuilder` (which reads `UsesCdeclConstructorWrapper` to decide `SwiftIndirectResult` vs `IntPtr`) AND `MethodMarshalPlanBuilder.BuildPInvokeCallStatement()` (which calls `NameProvider.GetPInvokeName()`, hashing `MangledName` to produce the P/Invoke method name). If either `UsesCdeclConstructorWrapper` or `MangledName` is set after `SignatureHandler` construction, the P/Invoke shape and/or the call-site name will be wrong.

**All three mutations must happen BEFORE `new SignatureHandler()`:**
1. `UsesCdeclConstructorWrapper = true` — so PInvokeSignatureBuilder emits `IntPtr`/`IntPtr*`
2. `UsesWrapperLibrary = true` — so PInvokeEmitter routes to wrapper library
3. `MangledName = cdeclSymbol` — so `GetPInvokeName()` hashes the correct symbol

The `@_cdecl` symbol name is a pure function of (moduleName, typeName, originalHash) — it can be computed without side effects, before any emission.

**Required ordering in `MethodHandler.cs`:**

```csharp
// BEFORE SignatureHandler creation (before line 319):
// Set ALL cdecl-related state so SignatureHandler sees the final method shape.
// GetConstructorSymbolName is a pure function — no Swift emission yet.
if (ConstructorWrapperEmitter.ShouldEmitWrapper(methodEnv))
{
    var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
        moduleName, typeName, methodEnv.MethodDecl.MangledName);
    methodEnv.MethodDecl.UsesCdeclConstructorWrapper = true;
    methodEnv.MethodDecl.UsesWrapperLibrary = true;
    methodEnv.MethodDecl.MangledName = cdeclSymbol;
    // Swift @_cdecl function emitted later (after signature validation)
}

// Existing: emit debug param wrapper (line 312-317)
// ...

var signatureHandler = new SignatureHandler(methodEnv);  // line 319
// SignatureHandler now sees:
//   - UsesCdeclConstructorWrapper → IntPtr resultPtr, IntPtr* errorOut
//   - MangledName = cdeclSymbol → GetPInvokeName() hashes the correct symbol
//   - UsesWrapperLibrary → routes to wrapper library path

if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)  // line 321
{
    // ... skip unsupported signatures (existing)
    return;
}

// Existing: closure/optional wrapper checks (lines 333-356)
// ...

// AFTER signature validation: emit the Swift @_cdecl wrapper.
// The symbol name was already computed above — this just generates the Swift code.
if (methodEnv.MethodDecl.UsesCdeclConstructorWrapper)
{
    ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(swiftWriter, methodEnv, ctx);
}

// Existing: CheckExportedSymbol, WrapperEmitter, EmitPInvoke (lines 358-384)
// WrapperEmitter reads _syncPlan.PInvokeCallStatement which uses GetPInvokeName()
//   → hashes MangledName (already set to cdeclSymbol) → correct name ✓
// PInvokeEmitter reads MangledName via ComputeEntryPoint()
//   → emits declaration with same hashed name → matches call site ✓
```

This is stricter than the existing closure/optional flag pattern (lines 333-356), which can afford to set flags between `SignatureHandler` and `WrapperEmitter` because those flags only affect library path and closure shapes — not the P/Invoke method name or parameter types.

### Step 3: DefaultParameterOverloadEmitter Integration — Same Mutations-First Pattern

**Codex P1 finding (v3)**: `DefaultParameterOverloadEmitter` emits constructor overloads and their P/Invokes **directly** (lines 128-141), bypassing the MethodHandler constructor branch. Without an explicit hook, DBW constructor overloads will keep emitting `CallConvSwift` P/Invokes to the `@_silgen_name` wrappers.

**Codex P1 findings (v4, v5)**: Same timing issue as MethodHandler — all three mutations (`UsesCdeclConstructorWrapper`, `UsesWrapperLibrary`, `MangledName`) must happen before `SignatureHandler` construction at line 88.

**Sequencing note for `MangledName`**: In this path, `EmitSwiftWrapper()` (line 115) sets `overloadDecl.MangledName` to the `@_silgen_name` symbol. Our `@_cdecl` wrapper wraps that `@_silgen_name` function, so the `@_cdecl` symbol is computed from the overload's original mangled name (before `EmitSwiftWrapper` changes it). We must compute and set the final `MangledName` (the `@_cdecl` symbol) BEFORE `SignatureHandler`, saving the intermediate `@_silgen_name` symbol for the Swift wrapper to reference internally.

**Required changes in `DefaultParameterOverloadEmitter.EmitOverload()`:**

```csharp
// Create environment with overload decl (existing, line 80-85)
var overloadEnv = new MethodEnvironment(overloadDecl, ...);

// NEW: Set ALL cdecl state BEFORE SignatureHandler construction.
// Compute the @_cdecl symbol from the original MangledName (before EmitSwiftWrapper changes it).
string? silgenSymbol = null;
if (overloadDecl.IsConstructor && ConstructorWrapperEmitter.ShouldEmitWrapper(overloadEnv))
{
    var cdeclSymbol = ConstructorWrapperEmitter.GetConstructorSymbolName(
        moduleName, typeName, overloadDecl.MangledName);
    overloadDecl.UsesCdeclConstructorWrapper = true;
    overloadDecl.UsesWrapperLibrary = true;
    // Save original MangledName so EmitSwiftWrapper can compute @_silgen_name from it
    var originalMangledName = overloadDecl.MangledName;
    overloadDecl.MangledName = cdeclSymbol;
    // EmitSwiftWrapper will need the @_silgen_name symbol — computed separately
    silgenSymbol = /* computed from originalMangledName by EmitSwiftWrapper */;
}

// Existing (line 88) — now sees UsesCdeclConstructorWrapper + final MangledName
var signatureHandler = new SignatureHandler(overloadEnv);
if (signatureHandler.GetWrapperSignature().ContainsPlaceholder) { ... continue; }

// Existing: emit Swift @_silgen_name wrapper (line 115)
// EmitSwiftWrapper uses the @_silgen_name symbol (not MangledName, which is now @_cdecl)
EmitSwiftWrapper(swiftWriter, methodDecl, overloadDecl, env);

// NEW: Emit @_cdecl wrapper that calls the @_silgen_name function
if (overloadDecl.UsesCdeclConstructorWrapper)
{
    ConstructorWrapperEmitter.EmitSwiftConstructorWrapper(
        swiftWriter, overloadEnv, emissionContext, silgenTarget: silgenSymbol);
}

// Existing: WrapperEmitter + PInvokeEmitter (lines 128-141)
// WrapperEmitter.BuildPInvokeCallStatement → GetPInvokeName() hashes MangledName (=cdeclSymbol)
// PInvokeEmitter.EmitPInvoke → GetPInvokeName() hashes same MangledName → names match ✓
var wrapperEmitter = new WrapperEmitter(overloadEnv, signatureHandler, fallbackInfo, emissionContext);
if (overloadDecl.IsConstructor && !overloadDecl.IsFailable && !overloadDecl.IsAsync)
    wrapperEmitter.EmitConstructor(csWriter);
else if (overloadDecl.IsConstructor && overloadDecl.IsFailable)
    wrapperEmitter.EmitFailableFactory(csWriter);
else
    wrapperEmitter.EmitMethod(csWriter, swiftWriter);
PInvokeEmitter.EmitPInvoke(csWriter, overloadEnv, signatureHandler);
```

**Implementation note**: `EmitSwiftWrapper()` currently reads and mutates `overloadDecl.MangledName` internally (line 704). This will need a minor refactor: either (a) `EmitSwiftWrapper` accepts the `@_silgen_name` symbol as a parameter instead of computing it from `MangledName`, or (b) `EmitSwiftWrapper` uses a stored original name. Option (a) is cleaner and avoids hidden state dependencies.

The `@_cdecl` wrapper calls the `@_silgen_name` function, chaining cleanly:

```
C# → @_cdecl (C ABI) → @_silgen_name (Swift ABI, default params) → Swift init
```

This ensures ALL constructor P/Invokes — both primary and default-parameter overloads — use `CallingConvention.Cdecl`, and the P/Invoke signatures are computed with the correct cdecl parameter types.

### Step 4: PInvokeEmitter / PInvokeSignatureBuilder Changes

**PInvokeEmitter.EmitPInvoke()** (line 664):
- When `UsesCdeclConstructorWrapper` is set, build `PInvokeEmissionInfo` with `CallingConvention = PInvokeCallingConvention.Cdecl`
- For generic types, set `OmitCallingConvention = true` on `PInvokeDeclaration` (already maps to Cdecl)
- Entry point and library path already handled by `ComputeEntryPoint()` + `UsesWrapperLibrary` flag

**PInvokeSignatureBuilder.HandleReturnType()** (line 129):
When `UsesCdeclConstructorWrapper` and `MethodRequiresIndirectResult`:
- Inject `IntPtr resultPtr` parameter instead of `SwiftIndirectResult swiftIndirectResult`
- Set return type to `void` (same as before)
- For class constructors: return `IntPtr` directly (same as current behavior post-fix #1)

**PInvokeSignatureBuilder.HandleSwiftError()** (line 567):
When `UsesCdeclConstructorWrapper` and method throws:
- Inject `IntPtr* errorOut` parameter instead of `SwiftError swiftError` with `out` modifier
- This produces a raw pointer parameter that requires `unsafe` on the P/Invoke declaration

**PInvokeEmitter unsafe detection** (line 711):
**Codex P2 finding (v4)**: The current `IsUnsafe` check uses substring matching: `pInvokeParams.Contains("void*") || pInvokeParams.Contains("delegate*")`. The new `IntPtr* errorOut` parameter won't match either pattern, so the emitted P/Invoke will fail to compile (CS0227: unsafe code requires `/unsafe`).

Fix: extend the check to include `IntPtr*`:
```csharp
// Current (line 711):
IsUnsafe = pInvokeParams.Contains("void*") || pInvokeParams.Contains("delegate*")

// Fixed:
IsUnsafe = pInvokeParams.Contains("void*") || pInvokeParams.Contains("delegate*") || pInvokeParams.Contains("IntPtr*")
```

Alternatively, `PInvokeSignatureBuilder` could track `RequiresUnsafe` structurally (set when adding any pointer parameter) and expose it on the signature object, avoiding substring matching entirely. But the minimal fix above is sufficient for this feature and matches the existing pattern.

### Step 5: MethodMarshalPlanBuilder Changes

**`BuildIndirectResultSetup()`** (line 287):
When `UsesCdeclConstructorWrapper`, produce allocation code that creates `IntPtr resultPtr` instead of `SwiftIndirectResult`:
```csharp
// Current:
_payload = new SwiftSafeHandle<T>((IntPtr)NativeMemory.Alloc(_payloadSize));
var swiftIndirectResult = new SwiftIndirectResult((void*)_payload.DangerousGetHandle());

// With cdecl wrapper:
_payload = new SwiftSafeHandle<T>((IntPtr)NativeMemory.Alloc(_payloadSize));
var resultPtr = _payload.DangerousGetHandle();
```

**`BuildSwiftErrorSetup()`** (line 347):
When `UsesCdeclConstructorWrapper`, produce error check code that references `errorPtr` (from the out-pointer) instead of `swiftError.Value`:
```csharp
// Current:
if (swiftError.Value != null) { var _errorPtr = (IntPtr)swiftError.Value; ... }

// With cdecl wrapper:
if (errorPtr != IntPtr.Zero) { ... SBW_GetErrorDescription(errorPtr) ... }
```
All downstream error handling (`SBW_GetErrorDescription`, `SBW_ReleaseError`, `SBW_ExtractTypedError_*`) stays identical — the opaque error pointer is the same format.

**`BuildPInvokeCallStatement()`** (line 451):
The `CallArgumentsString()` from `PInvokeSignatureBuilder` uses the parameter names from `HandleReturnType`/`HandleSwiftError`. Since those now produce `resultPtr`/`errorOut` instead of `swiftIndirectResult`/`swiftError`, the call statement updates automatically.

### Step 6: WrapperEmitter Changes (Minimal)

The key insight is that **most constructor body logic stays the same**. The marshal-plan pipeline on the C# side already converts parameters to their P/Invoke-compatible forms. What changes:

- `EmitConstructor()` — when using cdecl wrapper, struct parameters that were previously passed by value (e.g., `Swift.CGRect frame`) now need to be passed as `IntPtr` (pointer to the struct data). Add `fixed` or `Unsafe.AsPointer` conversion for these cases.
- `EmitIndirectResultConstructor()` — reads from `_syncPlan.IndirectResultConstructor.AllocationCode` which the plan builder has already adapted (plain `IntPtr` instead of `SwiftIndirectResult`). No change needed here.
- `EmitReturnConstructor()` — class return path: read `IntPtr` from P/Invoke return value (same as current post-fix #1 behavior). Struct return path: result buffer stays the same.
- `EmitObjCRootedConstructor()` — same minimal changes for the static helper.
- `EmitSwiftError()` — reads from `_syncPlan.SwiftError.ErrorCheckCode` which the plan builder has already adapted. No change needed here.
- `EmitFailableFactory()` — two sub-paths:
  - **Class failable**: simplified path (no VWT tag check, just `IntPtr.Zero` == nil)
  - **Struct failable**: existing path preserved (VWT tag check, payload copy), but with `IntPtr resultPtr` instead of `SwiftIndirectResult`

### Step 7: ModuleEmissionContext Tracking

Add parallel to destroy wrapper tracking:

```csharp
private readonly HashSet<string> _constructorWrapperSymbols = new();

public bool HasConstructorWrapperSymbol(string symbol) => _constructorWrapperSymbols.Contains(symbol);
public bool TryAddConstructorWrapperSymbol(string symbol) => _constructorWrapperSymbols.Add(symbol);
```

### Step 8: Edge Cases

**Generic parent types:** Skip — Swift can't express generic params in `@_cdecl` free functions (`τ_0_0` isn't a valid identifier). Same constraint as `DefaultParameterOverloadEmitter` (line 49-53). Fall back to current `CallConvSwift` P/Invoke.

**Closures in constructor params:** Gate out initially. These already have the separate `ClosureCdeclWrapper` path for the closure itself, but combining with constructor wrapper adds complexity. Defer to follow-up.

**Frozen structs projected as classes (PayloadBuffer):** The existing `EmitBoundGenericArguments()` and `EmitSafeHandleAddRef()` already produce `IntPtr` from `PayloadBuffer<T>`. These marshal-plan outputs feed directly into the cdecl P/Invoke — no additional conversion needed on the C# side. The Swift `@_cdecl` wrapper receives the `IntPtr` and reconstructs via `.load(as:)`.

### Step 9: Tests

- Unit tests for `ConstructorWrapperEmitter` (parallel to `DestroyWrapperEmitterTests`)
- TestFramework cases: struct param constructors, non-frozen struct constructors, throwing constructors, failable+throwing constructors
- Validation: Nuke (DataCache), Lottie (LottieAnimationView, LottieColor)
- Golden file updates

---

## Risk Assessment

| Risk | Severity | Mitigation |
|---|---|---|
| Breaking existing working constructors | High | Gate behind xcframework mode only; fallback to current CallConvSwift when no wrapper lib |
| Failable struct marshalling regression | High | Keep existing VWT tag/payload extraction; only change buffer parameter type |
| Throwing constructor error loss | High | Error pointer is same format as `SwiftError.Value` — existing infra handles it |
| DefaultParam overload bypass | High | Explicit hook in `DefaultParameterOverloadEmitter.EmitOverload()` with flag-before-SignatureHandler ordering |
| `SwiftIndirectResult` artifact | High | Explicitly replaced with `IntPtr resultPtr` in PInvokeSignatureBuilder + plan builder |
| SignatureHandler timing | High | ALL three mutations (`UsesCdeclConstructorWrapper`, `UsesWrapperLibrary`, `MangledName`) set BEFORE `SignatureHandler` construction in both paths — ensures P/Invoke shape AND call-site name are correct |
| `IntPtr*` unsafe detection | Medium | Extend `PInvokeEmitter` IsUnsafe check to match `IntPtr*` (line 711) |
| Parameter marshalling bugs | Medium | Reuse existing projection/marshal-plan outputs — only lower final ABI types |
| Test regressions | Medium | Run full test suite + golden file check after each step |
| Generic types | Low | Skip with clear gate; same constraint as destroy wrappers + DefaultParameterOverloadEmitter |

---

## What This Fixes

- **Issue #10**: LottieAnimationView(CGRect) — struct params via C ABI instead of CallConvSwift
- **Issue #12**: LottieColor(r:g:b:a:denominator:) — result buffer via C ABI instead of CallConvSwift
- **All future constructor crashes** from CallConvSwift mismatches on NativeAOT

## What This Does NOT Fix

- **Issue #11**: LottieAnimationView() returning null — likely `@MainActor` isolation, not CallConvSwift
- **Generic parent type constructors** — Swift can't express generic params in `@_cdecl` free functions
- **Closure parameter constructors** — deferred to follow-up (separate complexity)

---

## Files Affected

### New Files
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs`
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorWrapperEmitterTests.cs`

### Modified Files
- `src/Swift.Bindings/src/Model/TypeDecl/MethodDecl.cs` — new `UsesCdeclConstructorWrapper` flag
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` — integration point (emit Swift wrapper, set flags)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/DefaultParameterOverloadEmitter.cs` — **explicit hook** for constructor overload wrappers (lines 128-141)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` — set `CallingConvention.Cdecl` when flag is set
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeSignatureBuilder.cs` — `IntPtr resultPtr` instead of `SwiftIndirectResult`; `IntPtr* errorOut` instead of `SwiftError`
- `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs` — `BuildIndirectResultSetup` and `BuildSwiftErrorSetup` cdecl paths
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.cs` — minor: struct params as IntPtr
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Return.cs` — minor: class return from IntPtr
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.FailableFactory.cs` — class-failable simplified path; struct-failable `IntPtr` buffer
- `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmissionContext.cs` — dedup tracking

---

## Architecture Reference

### Destroy Wrapper Pattern (proven, model to follow)

```
DestroyWrapperEmitter.EmitIfNeeded()
├── Guards: xcframework mode, non-generic type, has wrapper lib
├── EmitSwiftDestroyWrapper() → @_cdecl("SBW_Destroy_{Module}_{Type}")
├── EmitCSharpDestroyRegistration() → [DllImport] + RegisterDestroyAction()
└── Dedup via ModuleEmissionContext.TryAddDestroyWrapperSymbol()
```

### Constructor Wrapper Pattern (proposed)

```
ConstructorWrapperEmitter.EmitIfNeeded()
├── Guards: xcframework mode, non-generic parent, has wrapper lib, no closure params
├── EmitSwiftConstructorWrapper() → @_cdecl("SBW_{Module}_{Type}_init_{Hash}")
│   ├── Non-failable class → returns UnsafeMutableRawPointer (retained)
│   ├── Non-failable struct → writes to resultPtr via initializeMemory
│   ├── Failable class → returns UnsafeMutableRawPointer? (nil == failure)
│   ├── Failable struct → writes Optional<Self> to resultPtr
│   ├── Throwing → do/catch, error to errorOut pointer
│   └── Failable+throwing → combined pattern
├── Sets UsesCdeclConstructorWrapper + UsesWrapperLibrary + MangledName
└── Dedup via ModuleEmissionContext.TryAddConstructorWrapperSymbol()
```

### Existing P/Invoke Infrastructure (reused, not duplicated)

```
PInvokeEmitter.EmitPInvoke()
├── Non-generic → PInvokeEmitHelper.EmitDeclaration(PInvokeEmissionInfo)
│   └── CallingConvention = Cdecl (when UsesCdeclConstructorWrapper)
├── Generic → PInvokeHelperContext.AddDeclaration(PInvokeDeclaration)
│   └── OmitCallingConvention = true (maps to Cdecl)
└── Entry point + library path from ComputeEntryPoint() + UsesWrapperLibrary
```

### Current Constructor Flow (to be modified)

```
MethodHandler.Emit()
  → ConstructorHandler guards
  → DefaultParameterOverloadEmitter (if defaults)
  │   → @_silgen_name wrapper
  │   → WrapperEmitter.EmitConstructor()
  │   → PInvokeEmitter.EmitPInvoke() → CallConvSwift  ← BYPASSES MethodHandler
  → SignatureHandler(methodEnv)  ← P/Invoke shape locked here
  → closure/optional wrapper flags (lines 333-356)
  → WrapperEmitter.EmitConstructor() → constructor body
    → EmitIndirectResultConstructor() → SwiftIndirectResult
    → EmitTypeConversions() → projection/marshal-plan
    → EmitBoundGenericArguments() → PayloadBuffer extraction
    → P/Invoke call (CallConvSwift)
    → EmitSwiftError() → swiftError.Value check
    → EmitReturnConstructor() → SafeHandle wrapping
  → PInvokeEmitter.EmitPInvoke() → CallConvSwift
```

### Proposed Constructor Flow

```
MethodHandler.Emit()
  → ConstructorHandler guards
  → DefaultParameterOverloadEmitter (if defaults)
  │   → UsesCdeclConstructorWrapper + MangledName = cdeclSymbol  ← NEW (BEFORE SigHandler)
  │   → SignatureHandler(overloadEnv)  ← sees cdecl flag + final MangledName
  │   → @_silgen_name wrapper
  │   → ConstructorWrapperEmitter.EmitSwiftWrapper() → @_cdecl  ← NEW
  │   → WrapperEmitter.EmitConstructor()  ← GetPInvokeName hashes cdeclSymbol ✓
  │   → PInvokeEmitter.EmitPInvoke() → CallingConvention.Cdecl  ← hashes same ✓
  → UsesCdeclConstructorWrapper + MangledName = cdeclSymbol  ← NEW (BEFORE SigHandler)
  → SignatureHandler(methodEnv)  ← sees cdecl flag + final MangledName
  → closure/optional wrapper flags (lines 333-356)
  → ConstructorWrapperEmitter.EmitSwiftWrapper() → @_cdecl  ← NEW
  → WrapperEmitter.EmitConstructor() → constructor body
    → EmitIndirectResultConstructor() → IntPtr resultPtr (NOT SwiftIndirectResult)  ← CHANGED
    → EmitTypeConversions() → projection/marshal-plan (same)
    → EmitBoundGenericArguments() → PayloadBuffer extraction (same)
    → P/Invoke call (Cdecl, through @_cdecl wrapper)  ← name matches declaration ✓
    → EmitSwiftError() → errorPtr != IntPtr.Zero check  ← CHANGED
    → EmitReturnConstructor() → SafeHandle wrapping (same)
  → PInvokeEmitter.EmitPInvoke() → CallingConvention.Cdecl  ← hashes same MangledName ✓
```

### Failable Constructor Flow

```
Non-failable:
  C# ──[cdecl]──→ @_cdecl wrapper ──[swift]──→ Swift init
                                                    │
                                          (class) return retained ptr
                                          (struct) write to resultPtr

Failable class:
  C# ──[cdecl]──→ @_cdecl wrapper ──[swift]──→ Swift init?
                                                    │
                                          guard let → retained ptr
                                          nil → return null (IntPtr.Zero)

Failable struct:
  C# ──[cdecl]──→ @_cdecl wrapper ──[swift]──→ Swift init?
                                                    │
                                          write Optional<Self> to resultPtr
                                          C# uses existing VWT tag check
```

### Throwing Constructor Flow

```
Throwing class:
  C# ──[cdecl]──→ @_cdecl wrapper ──[swift]──→ try Swift init()
                                                    │
                                          success → return retained ptr
                                          catch → write error to errorOut, return nil
                                          C# checks errorPtr, reuses SBW_GetErrorDescription

Throwing struct:
  C# ──[cdecl]──→ @_cdecl wrapper ──[swift]──→ try Swift init()
                                                    │
                                          success → write to resultPtr
                                          catch → write error to errorOut
                                          C# checks errorPtr before reading resultPtr

Failable + Throwing class:
  C# ──[cdecl]──→ @_cdecl wrapper ──[swift]──→ try Swift init?()
                                                    │
                                          success → return retained ptr
                                          nil → return null (no error)
                                          catch → write error to errorOut, return nil
                                          C# checks errorPtr FIRST, then nil-checks return
```
