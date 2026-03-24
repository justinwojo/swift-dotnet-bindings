# Design Doc: Finalizer-Safe VWT Destroy via Cdecl Trampoline

## Problem

Non-frozen structs and frozen structs with reference fields are projected as C# classes with `SwiftSafeHandle<T>`. Today, consumers **must** call `Dispose()` or use `using` on these types because the GC finalizer cannot safely clean them up on the Mono runtime (iOS Simulator):

```csharp
// Today: required for struct bindings, or you leak memory on simulator
using var config = ImagePipeline.Configuration();
config.MaxConcurrentDownloads = 4;
```

Swift classes don't have this problem — their finalizer calls `Arc.Release` via `CallingConvention.Cdecl`, which is safe from any thread on all runtimes.

### Root Cause

The Mono finalizer thread cannot JIT-compile new methods when earlier `CallConvSwift` P/Invoke compilations have contaminated the JIT state with an async flag. This triggers a `jit-info.c:918` assertion (`!ji->async`).

`SwiftSafeHandle<T>.PerformVwtDestroy()` calls VWT Destroy through a `delegate* unmanaged` function pointer, but the containing method itself requires JIT compilation (including generic specialization of `SwiftObjectHelper<T>.GetTypeMetadata()`). When the finalizer thread attempts this compilation, Mono crashes.

`SwiftClassHandle<T>` avoids this because `Arc.Release` is a `[DllImport]` with `CallingConvention.Cdecl` — DllImport stubs are resolved by the runtime loader, not JIT-compiled.

### Current Workaround

`SwiftSafeHandle<T>.ReleaseHandle()` detects Mono and skips ALL P/Invoke on the finalizer thread:

```csharp
// Mono finalizer → skip ALL P/Invoke (jit-info.c:918 crash risk)
if (!_explicitDispose && SwiftRuntimeInfo.IsMonoRuntime)
    return HandleMonoFinalizerCleanup(); // just zeroes the handle, leaks the buffer
```

This is safe but leaks memory on simulator dev builds. NativeAOT (production/device) is unaffected — its finalizer calls VWT Destroy successfully.

## Solution: Cdecl VWT Destroy Trampoline

Route VWT Destroy through a `@_cdecl` function in `SwiftBindingsRuntime.swift`, called from C# via `[DllImport]` with `CallingConvention.Cdecl`. This is the exact same pattern that makes `Arc.Release` finalizer-safe.

### Why This Works

| Path | JIT compilation needed? | Finalizer-safe on Mono? |
|---|---|---|
| `Arc.Release` via `[DllImport(Cdecl)]` | No — runtime resolves stub | Yes (proven) |
| **`SBW_VWTDestroy` via `[DllImport(Cdecl)]`** | **No — runtime resolves stub** | **Yes (same mechanism)** |
| `PerformVwtDestroy` via `delegate* unmanaged` | Yes — method body + generics | No (jit-info.c crash) |

### Precedent

`SwiftBindingsRuntime.swift` already has a type-specific @_cdecl destroy wrapper that proves the pattern:

```swift
@_cdecl("SBW_SwiftString_Destroy")
public func sbw_swiftStringDestroy(_ bufferPtr: UnsafeMutableRawPointer) {
    bufferPtr.assumingMemoryBound(to: String.self).deinitialize(count: 1)
}
```

The thunk migration proved the broader pattern at scale: all generated P/Invoke now routes through Cdecl entry points (@_cdecl wrappers or native ARM64 thunks), eliminating CallConvSwift from generated code entirely.

## Implementation

### Swift Side: Generic VWT Destroy Wrapper

Add to `SwiftBindingsRuntime.swift`:

```swift
/// Generic VWT Destroy: deinitializes any Swift value given its type metadata.
/// Called from the .NET GC finalizer thread via CallingConvention.Cdecl.
/// This avoids JIT compilation on the finalizer thread, which crashes Mono
/// when CallConvSwift compilations have contaminated JIT state.
///
/// - Parameters:
///   - ptr: Pointer to the Swift value to destroy (the SwiftSafeHandle buffer).
///   - metadataPtr: Pointer to the Swift type metadata for the value.
@_cdecl("SBW_VWTDestroy")
public func sbw_vwtDestroy(_ ptr: UnsafeMutableRawPointer, _ metadataPtr: UnsafeRawPointer) {
    // The VWT pointer is stored at metadata[-1] in Swift's ABI.
    // Load it, then call the Destroy witness (offset 1 in the VWT).
    let vwtPtr = metadataPtr.advanced(by: -MemoryLayout<UnsafeRawPointer>.size)
        .load(as: UnsafeRawPointer.self)
    let destroy = vwtPtr.advanced(by: MemoryLayout<UnsafeRawPointer>.size)
        .load(as: (@convention(c) (UnsafeMutableRawPointer, UnsafeRawPointer) -> Void).self)
    destroy(ptr, metadataPtr)
}
```

**Alternative approach**: Use Swift's `Builtin` APIs or `UnsafeMutablePointer<T>.deinitialize()` if a type-erased path is available. The raw VWT offset approach is ABI-stable since Swift 5.0 but requires validation. If a cleaner Swift API exists, prefer it.

**Alternative approach**: A static ARM64 assembly thunk (3 instructions) that loads VWT Destroy from metadata and tail-calls it:

```asm
.globl _SBW_VWTDestroy
_SBW_VWTDestroy:
    ldr x2, [x1, #-8]    ; VWT pointer from metadata[-1]
    ldr x2, [x2, #8]     ; Destroy fn ptr (VWT offset 8 = second entry)
    br x2                 ; tail-call Destroy(ptr, metadata)
```

This could be linked into the `SwiftBindingsRuntime` library alongside existing thunks. The assembly approach has zero Swift compiler dependency but is ARM64-only (which is fine — all Apple platforms are ARM64).

### C# Side: Finalizer Uses Cdecl Trampoline

In `SwiftSafeHandle<T>`, replace the Mono finalizer skip with a Cdecl call:

```csharp
[DllImport("SwiftBindingsRuntime", CallingConvention = CallingConvention.Cdecl)]
private static extern void SBW_VWTDestroy(IntPtr ptr, IntPtr metadata);

protected override unsafe bool ReleaseHandle()
{
    if (handle == IntPtr.Zero)
        return true;

    if (IsProcessExiting && !_explicitDispose)
        return HandleProcessExitCleanup();

    // Explicit Dispose: use direct VWT Destroy (always safe from user thread)
    if (_explicitDispose)
        return HandleNormalRelease();

    // Finalizer: use Cdecl trampoline (safe on both Mono and NativeAOT)
    try
    {
        TypeMetadata metadata = SwiftObjectHelper<T>.GetTypeMetadata();
        if (metadata.IsValid)
            SBW_VWTDestroy(handle, metadata.Handle);
    }
    catch { }

    NativeMemory.Free((void*)handle);
    handle = IntPtr.Zero;
    return true;
}
```

**Open question**: `SwiftObjectHelper<T>.GetTypeMetadata()` itself may require JIT compilation on the finalizer thread. If so, the metadata handle needs to be cached eagerly during construction or first explicit use, then stored as a field on the SafeHandle. This adds 8 bytes per handle but eliminates all generic JIT work from the finalizer path.

### Metadata Caching (if needed)

```csharp
public sealed class SwiftSafeHandle<T> : SafeHandleZeroOrMinusOneIsInvalid where T : ISwiftObject
{
    private readonly IntPtr _metadataHandle; // Cached at construction time

    public SwiftSafeHandle(IntPtr handle) : base(ownsHandle: true)
    {
        SetHandle(handle);
        // Cache metadata now (user thread, safe to JIT)
        var metadata = SwiftObjectHelper<T>.GetTypeMetadata();
        _metadataHandle = metadata.IsValid ? metadata.Handle : IntPtr.Zero;
    }

    // Finalizer path: no JIT needed, just Cdecl P/Invoke
    private bool HandleFinalizerRelease()
    {
        if (_metadataHandle != IntPtr.Zero)
            SBW_VWTDestroy(handle, _metadataHandle);
        NativeMemory.Free((void*)handle);
        handle = IntPtr.Zero;
        return true;
    }
}
```

## Impact on Consumer Experience

### Before (current)

| Swift Type | C# Projection | Disposal |
|---|---|---|
| `class` | `class` with `SwiftClassHandle` | Optional (GC handles it) |
| Non-frozen struct | `class` with `SwiftSafeHandle` | **Required** — must use `using` |
| Frozen struct with ref fields | `class` with `SwiftSafeHandle` | **Required** — must use `using` |
| Frozen blittable struct | C# `struct` | Not needed |

### After

| Swift Type | C# Projection | Disposal |
|---|---|---|
| `class` | `class` with `SwiftClassHandle` | Optional (GC handles it) |
| Non-frozen struct | `class` with `SwiftSafeHandle` | **Optional (GC handles it)** |
| Frozen struct with ref fields | `class` with `SwiftSafeHandle` | **Optional (GC handles it)** |
| Frozen blittable struct | C# `struct` | Not needed |

**Consumer message**: "Disposal is never required. Use `using` or `SwiftDisposeScope` when you want deterministic cleanup of scarce resources — the same reason you'd use `using` on a `FileStream`."

### Analyzer Changes

- `SB1001` severity drops from **Warning** to **Info** for struct-projected-as-class locals (same as classes today)
- Code fixer (new): offers one-click `using` insertion for any `SB1001` diagnostic

### Documentation Changes

The [Ownership wiki page](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Ownership) simplifies significantly — the "For Struct Bindings Projected as Classes" section and the Mono/NativeAOT distinction become implementation details rather than consumer-facing requirements.

## Risks and Open Questions

1. **VWT ABI stability**: The VWT Destroy offset (metadata[-1] for VWT pointer, VWT[1] for Destroy) is stable since Swift 5.0 and documented in Apple's ABI spec. Verify against the Swift 6.x ABI.

2. **Swift-side implementation**: The raw pointer approach to VWT access may need adjustment. Validate that the @_cdecl wrapper correctly calls Destroy for all type categories (non-frozen structs, frozen structs with ref fields, enums with payloads).

3. **`NativeMemory.Free` on Mono finalizer**: Currently also skipped on Mono (`HandleMonoFinalizerCleanup` skips everything). If the JIT contamination only affects methods that *contain* CallConvSwift constructs, `NativeMemory.Free` (which is a simple Cdecl P/Invoke to `free()`) might actually be safe. Test whether `NativeMemory.Free` works from the Mono finalizer thread independently. If it does, we can free the buffer even without the trampoline.

4. **SwiftBindingsRuntime availability**: The trampoline lives in `SwiftBindingsRuntime.dylib`, which is compiled per-library by the generator/SDK. If a library has no Swift wrappers (all functions thunked), the runtime library may not be linked. Ensure the runtime library is always present when struct types exist in the binding.

5. **Generic metadata caching cost**: Caching `_metadataHandle` adds 8 bytes per `SwiftSafeHandle<T>` instance. For apps creating millions of small struct wrappers, this may matter. Profile before committing to this approach — the alternative is ensuring `SwiftObjectHelper<T>.GetTypeMetadata()` is pre-compiled via `RuntimeHelpers.PrepareMethod`.

## Other Improvements Evaluated (Not Pursuing)

| Mechanism | Why Not |
|---|---|
| `ref struct` projection | Can't be stored in fields, collections, or across await — breaks the object model |
| Consumer source generators | Can only add code, not modify consumer code; analyzer is superior |
| `IAsyncDisposable` | Disposal is synchronous P/Invoke; no async benefit |
| `GC.AddMemoryPressure` | Marginal benefit; doesn't solve the finalizer safety problem |
| `ConditionalWeakTable` | Tracks associations, doesn't trigger disposal |
| C# 14 / .NET 10 features | No new disposal features; RAII proposals actively rejected by C# language team |
| Weak/phantom GC handles | Problem is finalizer safety, not collection timing |
| Xamarin/MAUI ToggleRef | Requires native runtime integration hooks we don't have |

## Implementation Plan

### Session 1: Cdecl VWT Destroy Trampoline

1. Add `SBW_VWTDestroy` to `SwiftBindingsRuntime.swift`
2. Add C# `[DllImport]` declaration and wire into `SwiftSafeHandle<T>.ReleaseHandle()`
3. Add metadata caching if `SwiftObjectHelper<T>.GetTypeMetadata()` isn't safe from finalizer
4. Update `SwiftDispose.FinalizerCleanup` to use new path
5. Test on iOS Simulator (Mono) to confirm no jit-info.c crash
6. Test on iOS Device (NativeAOT) to confirm no regression

### Session 2: Analyzer + Documentation

1. Add `SB1001` code fixer (auto-insert `using` declaration)
2. Update `SB1001` severity: Warning → Info for `ISwiftStruct` types
3. Update Ownership wiki page
4. Update `SwiftRuntimeInfo.cs` and `SwiftHandle.cs` comments

### Validation

- `./run-tests.sh` — unit tests pass
- `cd BindingTests && ./run-runtime-tests.sh --timeout 90` — runtime tests pass on simulator (Mono)
- `cd BindingTests && ./run-runtime-tests.sh --timeout 90` — runtime tests pass on device (NativeAOT) if available
- Verify no `jit-info.c` assertion in simulator logs
- Verify struct types are cleaned up by finalizer (no memory leak) via `Arc.RetainCount` or allocation tracking
