# Support non-blittable type marshalling with `CallConvSwift` P/Invoke

> Standalone feature request for filing against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings). Repro: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro). Contact: Justin Wojciechowski.

## Title

`Support non-blittable type marshalling with CallConvSwift P/Invoke`

## Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `os-macos`, `enhancement`, `runtime-mono`, `runtime-nativeaot`

## Description

**Environment:**
- .NET 10.0 (10.0.103), both Mono (iOS Simulator) and NativeAOT (iOS device)
- macOS 26+ / iOS 26+, arm64
- Xcode 26.2 / Swift 6.2.3

**Summary:**

`CallConvSwift` P/Invokes reject all non-blittable parameter and return types. This is the single largest barrier to direct Swift interop — across 51 third-party Swift library bindings (Nuke, Alamofire, Stripe, Lottie, BlinkID, Kingfisher, etc.), **~67% of P/Invokes require `@_cdecl` Swift wrapper functions** primarily because of this restriction. If non-blittable types were supported, the wrapper rate would drop to ~20% (the remaining wrappers handle Swift ABI patterns that C# P/Invoke can't express, like vtable dispatch and hidden metatype parameters).

**Where the restriction is enforced:**

Both runtimes hard-reject non-blittable types, but at different points:

- **Mono** (`mono/metadata/marshal.c:3700-3735`): The `CallConvSwift` validation pass walks `method->signature->params` and rejects any non-blittable parameter via `!type_is_blittable(...)` at `marshal.c:3729`, throwing `InvalidProgramException` before `mono_marshal_emit_native_wrapper` runs — so the existing `SafeHandle → IntPtr` IL marshalling path never gets a chance to execute. The check fires only against parameters; non-blittable returns hit a separate `MarshalDirectiveException` from `mono_marshal_get_native_wrapper` (`marshal.c:~4169`) for generic-instance returns.

- **CoreCLR/NativeAOT** (`src/coreclr/tools/Common/JitInterface/SwiftPhysicalLowering.cs:~215`): `LowerTypeForSwiftSignature()` checks `if (!type.IsValueType || type.ContainsGCPointers) return ByReference;`. Types containing GC pointers (managed strings, `SafeHandle` derivatives, `SwiftOptional<T>`, structs with GC-tracked fields) are forced to indirect / by-reference passing rather than running through the standard IL marshalling pipeline first.

**Current behavior:**

```
System.InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.
```

This occurs for `SafeHandle` derivatives, managed strings, `SwiftOptional<T>`, managed delegates, and any struct containing GC-tracked fields. The same types marshal correctly with `CallingConvention.Cdecl`.

**What we've observed in the runtime source:**

The existing P/Invoke marshalling pipeline (`ILSafeHandleMarshaler`, etc.) already converts certain managed types to blittable representations for `Cdecl` calls. `ILSafeHandleMarshaler` in particular converts a `SafeHandle` parameter into an `IntPtr` plus the standard `AddRef`/`Release` cleanup block in the IL stub. The marshalling runs in an IL stub that produces a fully blittable native call signature. For `CallConvSwift`, the struct lowering (`SwiftPhysicalLowering`) then decomposes blittable structs into register-sized primitives.

We noticed that the blittable validation and the marshalling pipeline don't compose today — Mono's `CallConvSwift` validation rejects non-blittable params before marshalling runs, and CoreCLR's lowering assumes its input is already blittable. We don't know enough about the runtime internals to know whether composing these is straightforward or if there are deeper reasons they're separated. (Note: managed `System.String → native UTF-8/UTF-16 buffer` via `ILStringMarshaler` is the C/Cdecl bridge; Swift's `String` ABI — e.g. the 2×64-bit `_StringObject` representation — is a different bridge that would need its own marshaller. We treat Swift `String` as a follow-up to the simpler `SafeHandle → IntPtr` case.)

**Types by complexity (from our perspective as consumers):**

- `SafeHandle → IntPtr` appears to be the simplest case — 1:1 marshalling with no layout change. This is also the highest-value for our use case (class instance methods are a large category of wrappers).
- Managed strings, delegates, and structs with GC fields are more complex and involve buffer allocation, lifetime management, and pinning.

**Real-world impact:**

| Non-blittable pattern | % of wrappers | Example Swift API |
|---|---|---|
| String params/returns | ~20% | `func name() -> String` |
| Class instances (SafeHandle) | ~15% | `func create() -> MyClass` |
| Optional params/returns | ~10% | `func find(_ id: Int) -> User?` |
| Array/Dictionary/Set | ~5% | `func items() -> [Item]` |
| Non-frozen structs (opaque) | ~5% | Instance methods on library-evolution structs |

Across 51 libraries (16,451 P/Invokes), 11,042 use `@_cdecl` wrappers. The majority are driven by non-blittable types in the signature.

**Workaround in use:**

We generate `@_cdecl` Swift wrapper functions that present a C-compatible signature to .NET, then internally call the real Swift function. This works but adds per-method Swift wrapper generation, a wrapper xcframework compilation step, and an extra function call indirection at runtime.

```swift
// Generated Swift wrapper — converts C-compatible types to Swift types
@_cdecl("SBW_MyType_name")
public func _sbw_name(_ self_: UnsafeRawPointer, _ resultPtr: UnsafeMutableRawPointer) {
    let instance = self_.assumingMemoryBound(to: MyType.self).pointee
    let result = instance.name  // Returns Swift.String
    // Marshal String → UTF-8 slice for C# consumption
    resultPtr.initializeMemory(as: SBW_Utf8Slice.self, to: ...)
}
```

```csharp
// Generated C# P/Invoke — uses Cdecl to call the wrapper
[DllImport("SwiftBindings", EntryPoint = "SBW_MyType_name",
    CallingConvention = CallingConvention.Cdecl)]
private static extern void SBW_MyType_name(IntPtr self, IntPtr resultPtr);
```

**Expected behavior:**

Non-blittable types in `CallConvSwift` P/Invoke signatures should be marshalled to their blittable representations (using the existing IL marshalling stub infrastructure) before Swift struct lowering and register assignment.

**Verified on 2026-04-30** with .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, Mono iOS Simulator runtime 26.3, NativeAOT iOS device (iPhone 13 / iOS 26 / `ios-arm64`), Xcode 26.2 (build 17C52). Reproduction confirmed across BindingTests / internal-binding-testing / repro-app paths: `InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.` is still thrown for `SafeHandle` / `SwiftSelf<SafeHandle>` / managed `string` parameters under `CallConvSwift` on both Mono and NativeAOT. The architectural recommendation — compose the existing IL marshalling stub pipeline (e.g. `ILSafeHandleMarshaler`) with the Swift physical lowering, starting with `SafeHandle → IntPtr` and treating Swift `String` as a follow-up — is the proposal we'd like reviewer feedback on.
