# Upstream Issues for dotnet/runtime

Prepared: February 2026
Project: Swift/.NET interop binding generator
Contact: Justin Wojciechowski

Five .NET runtime issues affect real-world Swift interop scenarios — three on Mono (Issues 1-3), one on NativeAOT (Issue 5), and one new cross-runtime issue (Issue 6). Searches of dotnet/runtime issues (February 2026) found no existing reports. The main Swift interop tracking issues ([#93631](https://github.com/dotnet/runtime/issues/93631) for .NET 9, [#108662](https://github.com/dotnet/runtime/issues/108662) for .NET 10) do not mention these specific issues.

> **Issue 4 (VWT Destroy) deleted March 2026**: NativeAOT investigation proved VWT Destroy via `delegate* unmanaged[Swift]` works correctly on both runtimes. The original crashes were caused by our generator bugs (wrong buffer sizes / corrupted metadata), not a runtime defect.

> **Before filing:** These drafts are waiting on the swift-bindings repo going public so we can
> link to concrete reproduction code and the binding generator as context. Before submitting,
> have Claude re-review each draft against the current state of the repo — the bugs, workarounds,
> and runtime landscape may have changed. Re-search dotnet/runtime issues at that time to
> confirm nothing has been filed in the interim.

**Filing strategy:**
- **Issue 1** — File as a **bug report**. Clear-cut Mono JIT defect with assertion failure and stack trace.
- **Issue 2** — File as a **feature request**. The error message suggests this is an intentional scope limitation in the initial `CallConvSwift` implementation, not a bug.
- **Issue 3** — **Mono-only**. Do not file standalone. Post as a **comment on the Swift interop tracking issue** asking whether async P/Invoke with SwiftSelf/SafeHandle is a supported scenario on Mono. NativeAOT investigation (March 2026) confirmed this issue does not reproduce on NativeAOT.
- ~~**Issue 4**~~ — **DELETED** (disproven). VWT Destroy via CallConvSwift works correctly. Crashes were our generator bugs.
- **Issue 5** — File as a **bug report**. NativeAOT passes custom struct float/double fields in GPR instead of FPR on ARM64.
- **Issue 6** — File as a **bug report**. New: Custom struct with single `double` field returns garbage via CallConvSwift on NativeAOT ARM64.

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

## Issue 3 (Tracking Issue Comment): SafeHandle/SwiftSelf lifetime across async P/Invoke with `CallConvSwift` (Mono-only)

> **Mono-only issue.** NativeAOT investigation (March 2026) confirmed this does not reproduce on NativeAOT.
> Do not file as a standalone issue. Post as a comment on the current Swift interop tracking issue (successor to [#108662](https://github.com/dotnet/runtime/issues/108662)) to ask about the supported pattern on Mono.

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

## ~~Issue 4~~ (DELETED): VWT Destroy crash — DISPROVEN

> **Deleted March 2026.** NativeAOT investigation proved that `delegate* unmanaged[Swift]` calls to VWT Destroy work correctly on both Mono and NativeAOT. The original crashes were caused by generator bugs (wrong buffer sizes, corrupted metadata), not a runtime defect. VWT Destroy wrappers have been removed from the generator.

---

## Issue 5 (Bug): NativeAOT CallConvSwift passes custom struct float/double fields in GPR instead of FPR on ARM64

### Title

`[NativeAOT] CallConvSwift on ARM64 passes custom struct float/double fields in GPR instead of FPR`

### Labels

`area-Interop-Swift`, `os-ios`, `bug`, `runtime-nativeaot`

### Description

**Environment:**
- .NET 10.0, NativeAOT (iOS device, `ios-arm64`)
- macOS 15+ / iOS 18+, arm64
- Swift 5.10+ runtime

**Summary:**

When passing custom C# structs containing `float` or `double` fields as parameters to Swift functions via `CallConvSwift` P/Invoke on NativeAOT ARM64, the floating-point fields are placed in general-purpose registers (GPR) instead of floating-point registers (FPR). Swift reads from FPR per the Swift ABI, receiving garbage values or causing SIGSEGV.

This is an ABI mismatch in NativeAOT's `CallConvSwift` register allocation for HFA (Homogeneous Floating-point Aggregate) structs.

**Key finding:** System framework types like `CGRect` (4 doubles, 32B) are **not affected** — they pass correctly via CallConvSwift on NativeAOT. Only **custom C# struct definitions** with float/double fields exhibit the bug. This suggests the issue is in how NativeAOT classifies custom structs for register allocation, not in the general HFA lowering path.

**Cross-runtime asymmetry:**

| Direction | Mono | NativeAOT |
|-----------|------|-----------|
| Custom float struct as **parameter** | PASS | **ABI MISMATCH** (floats in GPR) |
| Custom float struct as **return** | **SIGSEGV** | PASS |

Both runtimes have bugs with custom float structs, but in opposite directions.

**Minimal reproduction:**

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;

// Custom struct with double fields — triggers the bug
[StructLayout(LayoutKind.Sequential)]
public struct TwoDoubles
{
    public double A;
    public double B;
}

public static class FloatStructRepro
{
    // Swift function: func acceptTwoDoubles(_ s: TwoDoubles) -> Double { return s.A + s.B }
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport("MyLib", EntryPoint = "$s5MyLib16acceptTwoDoublesySdAA0cD0VF")]
    private static extern double AcceptTwoDoubles(TwoDoubles s);

    public static void Reproduce()
    {
        var s = new TwoDoubles { A = 1.0, B = 2.0 };
        // On NativeAOT: returns garbage (e.g., 0.0 or NaN) because
        // A and B are placed in x0/x1 (GPR) instead of d0/d1 (FPR).
        // Swift reads d0/d1 and gets whatever was previously in those registers.
        double result = AcceptTwoDoubles(s);
        // Expected: 3.0, Actual: garbage
    }
}
```

**Root cause analysis:**

The Swift calling convention on ARM64 places HFA struct fields in floating-point registers (`d0`–`d7`). NativeAOT's `CallConvSwift` implementation correctly handles system types (CGRect, CGSize, CGPoint — likely via special-casing or metadata), but for custom C# struct definitions with float/double fields, it incorrectly places the values in general-purpose registers (`x0`–`x7`).

Tested patterns:
- `struct { double A; }` (1 field, 8B) → garbage value on NativeAOT
- `struct { double A; double B; }` (2 fields, 16B) → garbage values on NativeAOT
- `struct { float A; float B; float C; float D; }` (4 fields, 16B) → garbage values on NativeAOT
- `struct { nint A; nint B; }` (2 integer fields, 16B) → PASS (correctly in GPR)

**Workaround in use:**

`@_cdecl` wrappers route parameters through `CallingConvention.Cdecl`, which has correct register allocation for all struct types:

```swift
@_cdecl("SBW_acceptTwoDoubles")
func _sbw_acceptTwoDoubles(_ a: Double, _ b: Double) -> Double {
    return acceptTwoDoubles(TwoDoubles(A: a, B: b))
}
```

**Impact:**

Affects any Swift API taking custom struct parameters with float/double fields on NativeAOT ARM64. The binding generator must detect float/double fields in custom structs and route them through `@_cdecl` wrappers. Integer-only structs ≤16 bytes pass correctly via CallConvSwift.

**Expected behavior:**

`CallConvSwift` P/Invoke should place custom struct float/double fields in FPR (`d0`–`d7`), matching the Swift ABI for HFA types on ARM64.

---

## Issue 6 (Bug): NativeAOT CallConvSwift returns garbage for single-field custom struct with `double`

### Title

`[NativeAOT] CallConvSwift returns garbage value when Swift function returns custom struct containing single double field on ARM64`

### Labels

`area-Interop-Swift`, `os-ios`, `bug`, `runtime-nativeaot`

### Description

**Environment:**
- .NET 10.0, NativeAOT (iOS device, `ios-arm64`)
- macOS 15+ / iOS 18+, arm64
- Swift 5.10+ runtime

**Summary:**

When a Swift function returns a custom struct containing a single `double` field, and the P/Invoke uses `CallConvSwift`, the returned value is garbage on NativeAOT ARM64. This is the simplest possible reproduction of the float-in-GPR bug (Issue 5) — a single-field struct eliminates any register splitting ambiguity.

**Minimal reproduction:**

Swift side:
```swift
public struct SingleDouble {
    public var value: Double

    public init(value: Double) {
        self.value = value
    }
}

public func makeSingleDouble(_ v: Double) -> SingleDouble {
    return SingleDouble(value: v)
}
```

C# side:
```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;

[StructLayout(LayoutKind.Sequential)]
public struct SingleDouble
{
    public double Value;
}

public static class SingleDoubleRepro
{
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport("MyLib", EntryPoint = "$s5MyLib16makeSingleDoubleyAA0cD0VSdF")]
    private static extern SingleDouble MakeSingleDouble(double v);

    public static void Reproduce()
    {
        // Swift places the return value in d0 (FPR).
        // NativeAOT reads from x0 (GPR) → garbage.
        SingleDouble result = MakeSingleDouble(42.0);
        Console.WriteLine(result.Value);
        // Expected: 42.0
        // Actual: garbage (whatever was in x0)
    }
}
```

**Root cause analysis:**

Swift returns `SingleDouble` (1 double field) in floating-point register `d0`. NativeAOT's `CallConvSwift` reads the return value from general-purpose register `x0`. The registers contain different values, so the returned struct has a garbage `Value` field.

This is the same underlying bug as Issue 5 (custom struct float fields in GPR instead of FPR), distilled to the absolute minimal reproduction: a single `double` field in a custom struct.

**Workaround in use:**

`@_cdecl` wrapper with `CallingConvention.Cdecl`:

```swift
@_cdecl("SBW_makeSingleDouble")
func _sbw_makeSingleDouble(_ v: Double) -> Double {
    return makeSingleDouble(v).value
}
```

Or use `SwiftIndirectResult` to bypass register allocation entirely (struct returned via buffer pointer in `x8`).

**Expected behavior:**

`CallConvSwift` should read custom struct return values with `double` fields from FPR (`d0`), matching the Swift ABI on ARM64.

---

## Filing Notes

**Related dotnet/runtime issues:**
- [#93631](https://github.com/dotnet/runtime/issues/93631) — Runtime support for Swift Interop in .NET 9
- [#108662](https://github.com/dotnet/runtime/issues/108662) — Runtime support for Swift Interop in .NET 10
- [#64215](https://github.com/dotnet/runtime/issues/64215) — Introduce `CallConvSwift`
- [#96059](https://github.com/dotnet/runtime/issues/96059) — Swift into .NET using `CallConvSwift` and `UnmanagedCallersOnly`
- [#100543](https://github.com/dotnet/runtime/issues/100543) — `SwiftSelf<T>` and `SwiftIndirectResult`

**Context:**

These issues were discovered while building a Swift/.NET binding generator that produces C# bindings from compiled Swift frameworks. The project targets .NET 10 on iOS/macOS, generating P/Invoke declarations with `CallConvSwift` for direct Swift function calls. Issues 1-3 are Mono-specific (iOS Simulator). Issue 4 was disproven (March 2026). Issues 5-6 affect NativeAOT on physical devices. All active issues have workarounds in production use (`@_cdecl` Swift wrapper functions using `CallingConvention.Cdecl`), but the workarounds add significant complexity (per-type/per-method Swift wrapper generation, wrapper xcframework bundling, manual marshalling) that could be reduced or eliminated with runtime improvements.
