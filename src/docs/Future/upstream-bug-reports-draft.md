# Upstream Issues for dotnet/runtime

Prepared: February 2026
Project: Swift/.NET interop binding generator
Contact: Justin Wojciechowski

Three Mono runtime issues block real-world Swift interop scenarios. Searches of dotnet/runtime issues (February 2026) found no existing reports. The main Swift interop tracking issues ([#93631](https://github.com/dotnet/runtime/issues/93631) for .NET 9, [#108662](https://github.com/dotnet/runtime/issues/108662) for .NET 10) do not mention these specific issues.

> **Before filing:** These drafts are waiting on the swift-bindings repo going public so we can
> link to concrete reproduction code and the binding generator as context. Before submitting,
> have Claude re-review each draft against the current state of the repo — the bugs, workarounds,
> and runtime landscape may have changed. Re-search dotnet/runtime issues at that time to
> confirm nothing has been filed in the interim.

**Filing strategy:**
- **Issue 1** — File as a **bug report**. Clear-cut JIT defect with assertion failure and stack trace.
- **Issue 2** — File as a **feature request**. The error message suggests this is an intentional scope limitation in the initial `CallConvSwift` implementation, not a bug.
- **Issue 3** — **Do not file standalone**. Post as a **comment on the Swift interop tracking issue** asking whether async P/Invoke with SwiftSelf/SafeHandle is a supported scenario and what the recommended pattern is.

---

## Issue 1 (Bug): Mono JIT assertion failure (`!ji->async`) when calling Swift runtime functions via `CallConvSwift` P/Invoke

### Title

`[Mono] JIT assertion "!ji->async" at jit-info.c:918 when calling Swift runtime functions via CallConvSwift P/Invoke`

### Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `bug`, `runtime-mono`

### Description

**Environment:**
- .NET 10.0, Mono runtime (iOS / Mac Catalyst)
- macOS 15+ / iOS 18+, arm64
- Swift 5.10+ runtime (`libswiftCore.dylib`)

**Summary:**

Calling `swift_getExistentialTypeMetadata` (a synchronous Swift runtime function) via P/Invoke with `CallConvSwift` causes a fatal assertion in Mono's JIT. The JIT incorrectly marks the call frame as "async", then hits `!ji->async` during stack unwinding at `mono/metadata/jit-info.c:918`.

This blocks any .NET code that needs to construct existential type metadata at runtime — required for creating `SwiftArray<ExistentialContainer>` (arrays of protocol-typed objects).

**Stack trace:**

```
* Assertion at mono/metadata/jit-info.c:918, condition `!ji->async' not met

Managed Stacktrace:
  at Swift.Runtime.TypeMetadata:swift_getExistentialTypeMetadata
  at Swift.Runtime.TypeMetadata:GetExistentialTypeMetadata
  at Swift.Runtime.SwiftObjectHelper`1:GetTypeMetadata
  at Swift.SwiftArray`1:.cctor
```

**Minimal reproduction:**

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;

public readonly struct TypeMetadata
{
    private readonly IntPtr handle;
    public IntPtr Handle => handle;
    public bool IsValid => handle != IntPtr.Zero;
}

public enum TypeMetadataRequest
{
    Complete = 0,
}

public static class SwiftInteropRepro
{
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport("libswiftCore", EntryPoint = "swift_getExistentialTypeMetadata")]
    private static extern TypeMetadata swift_getExistentialTypeMetadata(
        TypeMetadataRequest request,
        IntPtr superclassConstraint,
        nuint numProtocols,
        IntPtr protocols);

    public static void Reproduce()
    {
        // Crashes with !ji->async assertion on Mono (iOS)
        var metadata = swift_getExistentialTypeMetadata(
            TypeMetadataRequest.Complete,
            IntPtr.Zero,
            0,        // unconstrained existential (Any)
            IntPtr.Zero);
    }
}
```

**Root cause analysis:**

The function `swift_getExistentialTypeMetadata` is synchronous — it performs no async operations. Mono's JIT appears to incorrectly infer that `CallConvSwift` frames may be async (possibly confused by the Swift async context register conventions). During stack unwinding, the JIT encounters the frame marked async and hits the assertion `!ji->async` in `jit-info.c:918`.

Workarounds attempted (all failed):
| Approach | Result |
|----------|--------|
| `[SuppressGCTransition]` | Same assertion |
| `CallingConvention.Cdecl` instead of `CallConvSwift` | Same assertion or incorrect results |
| `nint` return type | Same assertion |

**Workaround in use:**

Swift wrapper functions (`@_silgen_name`) perform the existential metadata lookup on the Swift side, returning results via a C-compatible interface. This avoids the Mono JIT code path entirely.

```swift
@_silgen_name("SBW_createExistentialArray")
public func createExistentialArray(_ items: [any SomeProtocol]) -> UnsafeMutableRawPointer {
    let ptr = UnsafeMutablePointer<[any SomeProtocol]>.allocate(capacity: 1)
    ptr.initialize(to: items)
    return UnsafeMutableRawPointer(ptr)
}
```

**Impact:**

This blocks creating arrays of protocol-typed objects from C# — a common Swift API pattern. Any constructor or method that takes `[any Protocol]` parameters cannot be called directly from .NET. Real-world libraries affected include Nuke (image loading), where constructors like `ImageRequest(url:processors:)` require existential arrays.

**Expected behavior:**

The P/Invoke call to `swift_getExistentialTypeMetadata` with `CallConvSwift` should succeed without assertion failures. The function is synchronous and should not be marked as async by the JIT.

---

## Issue 2 (Feature Request): Support non-blittable type marshalling with `CallConvSwift` P/Invoke

### Title

`[Mono] Support non-blittable type marshalling with CallConvSwift P/Invoke`

### Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `enhancement`, `runtime-mono`

### Description

**Environment:**
- .NET 10.0, Mono runtime (iOS / Mac Catalyst)
- macOS 15+ / iOS 18+, arm64
- Swift 5.10+ runtime

**Summary:**

P/Invoke declarations using `CallConvSwift` currently reject all non-blittable parameter and return types with `System.InvalidProgramException`. We understand this may be an intentional scope limitation of the initial `CallConvSwift` implementation. This request is to understand the roadmap for non-blittable type support and to document the real-world impact of the current restriction.

**Current behavior:**

```
System.InvalidProgramException: Cannot use non-blittable types with Swift calling convention
```

This occurs for any P/Invoke with `CallConvSwift` that includes non-blittable types in the signature — `SafeHandle` derivatives, managed strings, `SwiftOptional<T>`, etc. The same types marshal correctly with standard calling conventions (`Cdecl`, etc.).

**Example triggering the error:**

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;

public static class NonBlittableExample
{
    // Swift: enum Country: String { case US = "US" ... }
    // The getter for .rawValue returns SwiftString (non-blittable)
    //
    // This throws InvalidProgramException even though the only non-blittable
    // type in the signature could be trivially lowered to IntPtr
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport("libMyFramework", EntryPoint = "$s...rawValue...getter")]
    private static extern IntPtr Country_rawValue_getter(
        int enumValue,
        SwiftSelf self);
    // ^ Throws if SwiftString or SafeHandle appears anywhere in signature
}
```

**Workaround in use:**

We use `IntPtr` for all non-blittable positions and marshal manually. For string transfers across the boundary, we use a blittable `SBW_Utf8Slice` bridge struct `(IntPtr buffer, int length)` with Swift-side allocation and C#-side decoding.

```csharp
// Instead of relying on marshalling:
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[DllImport("libMyFramework", EntryPoint = "$s...rawValue...getter")]
private static extern IntPtr Country_rawValue_getter(int enumValue, SwiftSelf self);

// Then manual marshalling in wrapper code
```

**Real-world impact:**

This affects any Swift API with non-primitive types in its signature:
- **Enum raw values** — String-backed enums (`Country`, `DocumentType`, `DetectionStatus`) cannot use their getter or `init(rawValue:)` directly
- **Optional returns** — `URL.absoluteString` returning `Optional<String>`
- **SafeHandle parameters** — instance methods taking complex objects

In our BlinkID binding validation (18 runtime tests), 3 tests fail due to this — all String-based enum raw value accessors. The workaround (IntPtr + manual marshalling) works but adds complexity to the binding generator.

**Questions for the runtime team:**

1. Is non-blittable marshalling support for `CallConvSwift` on the roadmap?
2. Is the restriction specific to Mono, or does CoreCLR/NativeAOT have the same limitation?
3. Is the recommended long-term pattern to always use blittable types with `CallConvSwift` and marshal manually, or is automatic marshalling planned?

---

## Issue 3 (Tracking Issue Comment): SafeHandle/SwiftSelf lifetime across async P/Invoke with `CallConvSwift`

> **Do not file as a standalone issue.** Post as a comment on the current Swift interop tracking issue (successor to [#108662](https://github.com/dotnet/runtime/issues/108662)) to ask about the supported pattern.

### Suggested comment

**Subject:** Question: Recommended pattern for `SwiftSelf`/`SafeHandle` lifetime in async P/Invoke with `CallConvSwift`

We're building a binding generator that calls async Swift instance methods from C# via P/Invoke with `CallConvSwift`. The `self` parameter is passed via `SwiftSelf`, wrapping a `SafeHandle`-derived pointer.

We're observing that when an async Swift method suspends, the `SafeHandle` reference may not be preserved across the Task continuation boundary — the GC can collect the handle (triggering `swift_release()`) while the Swift async operation is still in flight.

Our current workaround is to generate Swift wrapper functions that either:
1. Use the Swift-side singleton directly (`ClassName.shared`) for singleton classes
2. Accept `UnsafeMutableRawPointer` and use `unsafeBitCast` / `Unmanaged` to recover the instance

```swift
@_silgen_name("MyClass_asyncMethod_wrapper")
public func myClass_asyncMethod_wrapper(_ selfPtr: UnsafeMutableRawPointer) async {
    let instance = Unmanaged<MyClass>.fromOpaque(selfPtr).takeUnretainedValue()
    await instance.asyncMethod()
}
```

```csharp
// C# side: explicit retain/release around the async call
public async Task AsyncMethod()
{
    var ptr = _handle.DangerousGetHandle();
    Arc.Retain(ptr);
    try { await MyClass_asyncMethod_wrapper(ptr); }
    finally { Arc.Release(ptr); }
}
```

**Questions:**
1. Is async P/Invoke with `CallConvSwift` + `SwiftSelf` a supported scenario on Mono?
2. If so, what is the recommended pattern for ensuring the `SafeHandle` stays alive across suspension points? Should callers use `DangerousGetHandle()` + manual ARC retain, or is there a runtime mechanism we're missing?
3. If this scenario isn't supported yet, is it on the roadmap? It's required for calling any async instance method on a Swift class from .NET.

This affects every async Swift API we bind — libraries like StoreKit 2 and Nuke rely heavily on async instance methods. Currently every such method requires a Swift wrapper function, which adds significant build complexity.

---

## Filing Notes

**Related dotnet/runtime issues:**
- [#93631](https://github.com/dotnet/runtime/issues/93631) — Runtime support for Swift Interop in .NET 9
- [#108662](https://github.com/dotnet/runtime/issues/108662) — Runtime support for Swift Interop in .NET 10
- [#64215](https://github.com/dotnet/runtime/issues/64215) — Introduce `CallConvSwift`
- [#96059](https://github.com/dotnet/runtime/issues/96059) — Swift into .NET using `CallConvSwift` and `UnmanagedCallersOnly`
- [#100543](https://github.com/dotnet/runtime/issues/100543) — `SwiftSelf<T>` and `SwiftIndirectResult`

**Context:**

These issues were discovered while building a Swift/.NET binding generator that produces C# bindings from compiled Swift frameworks. The project targets .NET 10 on iOS/macOS via Mono, generating P/Invoke declarations with `CallConvSwift` for direct Swift function calls. All three issues have workarounds in production use, but the workarounds add significant complexity (Swift wrapper generation, manual marshalling, singleton detection) that could be reduced or eliminated with runtime improvements.
