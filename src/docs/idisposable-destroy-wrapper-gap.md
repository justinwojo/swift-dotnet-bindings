# IDisposable and Dispose Safety — Resolved

Updated: April 2026
Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings)

## Summary

All generated types implementing `IDisposable` have a working destroy mechanism. `Dispose()` and `using var` are **safe on all ISwiftObject types**. The downstream convention "never dispose ISwiftObject types" can be removed.

## Architecture

The destroy mechanism varies by type category, but all are correct:

| Type category | C# projection | Dispose mechanism | Notes |
|---|---|---|---|
| Non-frozen struct | `class` with `SwiftSafeHandle<T>` | VWT Destroy → `NativeMemory.Free` | Decrements ARC refs, then frees .NET buffer |
| Frozen struct (value-only) | `struct` | No-op (`Dispose() { }`) | Pure value type, no native resources |
| Frozen struct (ref fields) | `class` with `SwiftSafeHandle<T>` | VWT Destroy → `NativeMemory.Free` | Same as non-frozen struct |
| Swift class | `class` with `SwiftClassHandle<T>` | `Arc.Release` (`swift_release`) | Standard ARC release |
| Enum (singleton cases) | `class` with `_isCachedSingleton` | Guarded no-op | Singleton instances skip Dispose |
| Enum (non-singleton) | `class` with `SwiftSafeHandle<T>` | VWT Destroy → `NativeMemory.Free` | FromRawValue instances are disposable |

### VWT Destroy paths

- **Explicit Dispose** (user thread): Calls VWT Destroy directly via `delegate* unmanaged` function pointer from the cached value witness table. Always safe on user threads.
- **GC Finalizer**: Calls VWT Destroy via `SBW_VWTDestroy` Cdecl trampoline in `SwiftBindingsRuntime.dylib`. DllImport stubs are resolved by the runtime loader (no JIT), making this safe on both Mono and NativeAOT finalizer threads.
- **Process exit**: Skips VWT Destroy (Swift runtime may be torn down), frees .NET buffer only.

### Double-Dispose safety

All paths are double-Dispose safe:
- `SwiftSafeHandle<T>.ReleaseHandle()` checks `handle == IntPtr.Zero` and returns early
- `SwiftClassHandle<T>` inherits SafeHandle's built-in double-release protection
- Frozen struct `Dispose() { }` is idempotent by definition
- Singleton enum Dispose returns early on `_isCachedSingleton`

## History

The original architecture (pre-`ee6a86ac`) emitted `@_cdecl` destroy wrappers per type. These were inconsistently generated — some types got them, others didn't. Calling Dispose on types without a destroy wrapper crashed.

This was resolved in commit `ee6a86ac` ("NativeAOT Session 1: eliminate VWT destroy wrappers") which replaced per-type `@_cdecl` destroy wrappers with universal VWT Destroy. VWT Destroy works for all Swift types because it uses the value witness table function pointer obtained from the type's metadata — no per-type wrapper needed.

The Cdecl trampoline for finalizer safety was added in commit `b912a9c1` ("Finalizer-safe VWT Destroy via Cdecl trampoline").

## Verification

Runtime tests in `BindingTests/RuntimeTestsApp/MemoryManagement/DisposeTests.cs` explicitly verify:
- Dispose on all type categories (non-frozen struct, class, frozen struct, enum, frozen struct with ref)
- Double-Dispose safety
- SafeHandle state after Dispose
- Use-then-Dispose patterns

## Downstream action items

In `swift-dotnet-packages`:
1. Delete the `check-dispose-safety.sh` script
2. Remove the "never dispose ISwiftObject types" convention from CLAUDE.md
3. Encourage `using var` on all ISwiftObject types (standard C# pattern)
