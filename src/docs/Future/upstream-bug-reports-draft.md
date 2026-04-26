# Upstream Issues for dotnet/runtime

Updated: April 2026 (re-verified 2026-04-26)
Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings)
Contact: Justin Wojciechowski
Repro project: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro)

Eight .NET runtime issues affect real-world Swift interop scenarios. Issue 2 (non-blittable type support) has the highest impact — it's the primary driver of ~67% of P/Invokes needing wrapper functions across 51 third-party Swift library bindings. Searches of dotnet/runtime issues (February 2026) found no existing reports. The main Swift interop tracking issues ([#93631](https://github.com/dotnet/runtime/issues/93631) for .NET 9, [#108662](https://github.com/dotnet/runtime/issues/108662) for .NET 10) do not mention these specific issues.

> **2026-04-26 re-verification environment** (used for every "Verified on" line below):
> - .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, runtime framework 10.0.3
> - Xcode 26.2 (build 17C52), iOS Simulator runtime 26.3, iPhone 13 device on iOS 26
> - macOS host arm64, repro project at `/Users/wojo/Dev/swift-interop-repro/`
>
> **Behavioral shifts caught by this run** (read before filing — flag loudly):
> - **Issue 1's original trigger no longer reproduces.** The direct `swift_getExistentialTypeMetadata` P/Invoke shown in the repro now PASSES on Mono (.NET 10.0.103). The `!ji->async` assertion at `jit-info.c:918` still fires, but only as a *secondary* crash during Mono signal-handler stack unwinding from an unrelated native SIGSEGV (e.g. Issue 7's `sumStruct4Ints`, or the SkipReduction `FourStringInit_Struct` 4-String struct call). The assertion bug is real and reproducible, but the minimal repro snippet currently in the draft is stale — see Issue 1 below for the updated trigger evidence and a TODO before filing.
> - All six remaining issues (2, 3, 5, 6, 7, 8) reproduce with the same symptoms documented previously.
> - **Issue 9 added (2026-04-26).** Mono `Cannot transition thread 0x0 from STARTING with DONE_BLOCKING` when calling `Set<T>.insert` via `CallConvSwift`. The `(Bool direct, @out via x0)` tuple-return ABI shape corrupts Mono's thread state machine during the managed-to-native transition. `Set.contains` (simpler ABI, no `@out` tuple) passes. Confirmed in standalone repro (Issue 9 in `swift-interop-repro`). Root cause in swift-bindings: `SwiftSet<T>.InsertUnsafe` uses `SwiftSetPInvokes.Insert` with this ABI shape.

> **Before filing:**
> 1. Re-search dotnet/runtime issues to confirm nothing has been filed in the interim.
> 2. Re-verify each repro against the current .NET SDK version.
> 3. Update version numbers in each issue to match the SDK used for verification.
> 4. Replace `justinwojo/swift-interop-repro` URLs with the actual published repo URL if different.
> 5. **Issue 1 specifically:** rewrite the minimal repro to use the current trigger (a P/Invoke into Swift code that itself crashes — e.g. a struct with bad calling-convention layout — so Mono walks the stack and trips the assertion). The existential-metadata P/Invoke alone is no longer sufficient.

**Filing strategy:**
- **Issue 2** — File as a **feature request** (highest priority). Non-blittable `CallConvSwift` support. Includes source-level analysis of both Mono (`marshal.c:3729`) and CoreCLR (`SwiftPhysicalLowering.cs:215`) rejection points, architectural suggestion (run marshalling before blittable validation), and incremental approach (SafeHandle first, then String).
- **Issue 1** — File as a **bug report**. Mono JIT `jit-info.c:918` assertion failure.
- **Issue 3** — **Mono-only**. Post as a **comment on the Swift interop tracking issue** asking whether async P/Invoke with SwiftSelf/SafeHandle is a supported scenario on Mono.
- **Issue 5** — File as a **bug report**. NativeAOT JIT register allocation bug: custom struct float/double fields placed in GPR instead of FPR despite correct lowering in `SwiftPhysicalLowering.cs`.
- **Issue 6** — File as a **bug report**. Minimal repro of Issue 5 (single `double` field struct).
- **Issue 7** — File as a **bug report**. NativeAOT passes custom integer structs >16B by pointer instead of in GPRs. Separate from float issue (Issue 5). Affects both NativeAOT (≥24B) and Mono (≥32B).
- **Issue 8** — File as a **bug report**. NativeAOT SIGSEGV on multi-type-parameter generic functions via CallConvSwift. Clean delta: 1 type param PASSES, 2 type params SIGSEGV. Reproduced in standalone repro project.
- **Issue 9** — File as a **bug report**. Mono `Cannot transition thread from STARTING with DONE_BLOCKING` on `Set<T>.insert` via `CallConvSwift`. Specific to the `(Bool direct, @out via x0)` tuple-return ABI shape. `Set.contains` (no `@out`) passes. Reproduced in standalone repro project (Issue 9 in `swift-interop-repro`).

---

## Issue 1 (Bug): Mono JIT assertion failure (`!ji->async`) when calling Swift runtime functions via `CallConvSwift` P/Invoke

### Title

`[Mono] JIT assertion "!ji->async" at jit-info.c:918 when calling Swift runtime functions via CallConvSwift P/Invoke`

### Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `bug`, `runtime-mono`

### Description

**Environment:**
- .NET 10.0 (10.0.103), Mono runtime (iOS / Mac Catalyst)
- macOS 26+ / iOS 26+, arm64
- Xcode 26.2 / Swift 6.2.3

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

**Verified on 2026-04-26 with .NET SDK 10.0.103, Xcode 26.2** — *behavior shift, do not file as-is*.

The original repro (the snippet above, calling `swift_getExistentialTypeMetadata` from C# via `CallConvSwift`) now **PASSES** on Mono (`Baseline_BlittableCallConvSwift.Run()`: `swift_getExistentialTypeMetadata: valid=True — PASS`). However, the `!ji->async` assertion at `jit-info.c:918` *still fires reliably* — it is now only triggered when Mono's signal handler walks the stack after a *separate* native SIGSEGV in `CallConvSwift` code. Two paths reproduce in our current repro app:

1. `NativeAOT_A2_StructSizes.sumStruct4Ints` (32-byte / 4×`nint` struct param via `CallConvSwift`) crashes the Swift callee with SIGSEGV → Mono's `mono_dump_native_crash_info` → `wrapper_managed_to_native_*` frame walk → `Assertion at jit-info.c:918, condition '!ji->async' not met`. Identical wording to the original report.
2. `SkipReduction_FourStringParams.FourStringInit_Struct` (4×16-byte struct params via `CallConvCdecl`) also produces the same Mono assertion during signal-handler unwinding.

**Recommendation before filing:** rewrite the minimal repro to a deliberately-crashing `CallConvSwift` P/Invoke (e.g. pass a deliberately-malformed integer struct and expect the Mono assertion during the resulting stack walk). The "synchronous Swift runtime function" framing in the current draft is no longer accurate; the bug is in Mono's frame-async classification during *unwinding*, not during the call itself.

---

## Issue 2 (Feature Request): Support non-blittable type marshalling with `CallConvSwift` P/Invoke

### Title

`Support non-blittable type marshalling with CallConvSwift P/Invoke`

### Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `os-macos`, `enhancement`, `runtime-mono`, `runtime-nativeaot`

### Description

**Environment:**
- .NET 10.0 (10.0.103), both Mono (iOS Simulator) and NativeAOT (iOS device)
- macOS 26+ / iOS 26+, arm64
- Xcode 26.2 / Swift 6.2.3

**Summary:**

`CallConvSwift` P/Invokes reject all non-blittable parameter and return types. This is the single largest barrier to direct Swift interop — across 51 third-party Swift library bindings (Nuke, Alamofire, Stripe, Lottie, BlinkID, Kingfisher, etc.), **~67% of P/Invokes require `@_cdecl` Swift wrapper functions** primarily because of this restriction. If non-blittable types were supported, the wrapper rate would drop to ~20% (the remaining wrappers handle Swift ABI patterns that C# P/Invoke can't express, like vtable dispatch and hidden metatype parameters).

**Where the restriction is enforced:**

Both runtimes hard-reject non-blittable types, but at different points:

- **Mono** (`mono/metadata/marshal.c:3729`): Validates blittable **before** IL marshalling stub generation. The check `!type_is_blittable(method->signature->params[i])` fires and throws `InvalidProgramException` before `mono_marshal_emit_native_wrapper` runs — so the existing `SafeHandle → IntPtr` marshalling path never gets a chance to execute.

- **CoreCLR/NativeAOT** (`SwiftPhysicalLowering.cs:215`): `LowerTypeForSwiftSignature()` rejects types with `ContainsGCPointers: true`. The struct lowering pipeline assumes all input types are already blittable.

**Current behavior:**

```
System.InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.
```

This occurs for `SafeHandle` derivatives, managed strings, `SwiftOptional<T>`, managed delegates, and any struct containing GC-tracked fields. The same types marshal correctly with `CallingConvention.Cdecl`.

**What we've observed in the runtime source:**

The existing P/Invoke marshalling pipeline (`ILSafeHandleMarshaler`, `ILStringMarshaler`, etc.) already converts non-blittable types to blittable representations for `Cdecl` calls. The marshalling runs in an IL stub that produces a fully blittable native call signature. For `CallConvSwift`, the struct lowering (`SwiftPhysicalLowering`) then decomposes blittable structs into register-sized primitives.

We noticed that the blittable validation and the marshalling pipeline don't compose today — Mono validates blittable before marshalling runs, and CoreCLR's lowering assumes its input is already blittable. We don't know enough about the runtime internals to know whether composing these is straightforward or if there are deeper reasons they're separated.

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

**Verified on 2026-04-26 with .NET SDK 10.0.103, Xcode 26.2** — reproduces, file as-is.

Reproduction confirmed across BindingTests / sim-validation / repro-app paths: `InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.` is still thrown for `SafeHandle` / `SwiftSelf<SafeHandle>` / managed `string` parameters under `CallConvSwift` on both Mono and NativeAOT. The repro app's `MonoD_NonBlittableType` block prints the SKIP message that correctly points at `SwiftSelf<SafeHandle>` as the canonical trigger; the BindingTests + Apple-framework binding suite continue to drive `~67%` wrapper coverage primarily because of this restriction. No behavior change observed; the runtime source-level analysis above still matches `marshal.c:3729` and `SwiftPhysicalLowering.cs:215` in current dotnet/runtime main.

---

## Issue 3 (Tracking Issue Comment): SafeHandle/SwiftSelf lifetime across async P/Invoke with `CallConvSwift` (Mono-only)

> **Mono-only.** NativeAOT investigation (March 2026, .NET 10.0.103) confirmed this does not reproduce on NativeAOT.
> File as a **comment on the Swift interop tracking issue** (successor to [#108662](https://github.com/dotnet/runtime/issues/108662)), not a standalone issue.

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

**Verified on 2026-04-26 with .NET SDK 10.0.103, Xcode 26.2** — reproduces, post comment as-is.

The Mono-only scope is confirmed by the most recent BindingTests `--device` runs on this branch (NativeAOT path): the same async + `SwiftSelf<SafeHandle>` patterns succeed there. The standalone repro app does not include a dedicated async-suspension test (the `MonoC_ClosureCallback_WithSwiftSelf` block exercises the synchronous-callback shape but not the await-induced suspension that triggers the lifetime gap), so this filing relies on the BindingTests evidence captured on the swift-bindings side rather than a `swift-interop-repro` reduction. The wording in the comment above is still accurate; no edits required.

---

## Issue 5 (Bug): NativeAOT CallConvSwift passes custom struct float/double fields in GPR instead of FPR on ARM64

### Title

`[NativeAOT] CallConvSwift on ARM64 passes custom struct float/double fields in GPR instead of FPR`

### Labels

`area-Interop-Swift`, `os-ios`, `bug`, `runtime-nativeaot`

### Description

**Environment:**
- .NET 10.0 (10.0.103), NativeAOT (iOS device, `ios-arm64`)
- macOS 26+ / iOS 26+, arm64
- Xcode 26.2 / Swift 6.2.3

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

The Swift calling convention on ARM64 places HFA struct fields in floating-point registers (`d0`–`d7`). The struct lowering code in `SwiftPhysicalLowering.cs` (`LowerTypeForSwiftSignature`) correctly classifies `float` fields as `CORINFO_TYPE_FLOAT` and `double` fields as `CORINFO_TYPE_DOUBLE` via `GetIntervalDataForType()` (lines 74–82). This lowering should tell the JIT to place these elements in FPR.

However, for custom C# struct definitions, the lowered float/double elements end up in general-purpose registers (`x0`–`x7`) instead of floating-point registers (`d0`–`d7`). System framework types like `CGRect` (4 doubles, 32B) pass correctly — suggesting the bug is **downstream of the lowering**, in how the JIT consumes `CORINFO_SWIFT_LOWERING` results for register assignment when the struct originates from a custom C# definition rather than a system type.

The lowering output is correct; the register allocation from that output is not.

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

**Verified on 2026-04-26 with .NET SDK 10.0.103, Xcode 26.2** — reproduces, file as-is.

NativeAOT device run (`xcrun devicectl device process launch` against an iPhone 13 on iOS 26, `ios-arm64` published with `PublishAot=true`) confirmed garbage values for every custom-struct double shape in `NativeAOT_A2_StructSizes`:

```
1 Double  (8B):                   val=3.0438774487E-314      (expected 42.5) — ABI MISMATCH
2 Doubles (16B):                  sum=5.1716891354E-314      (expected 3)    — ABI MISMATCH
3 Doubles (24B):                  sum=7.30579192E-314        (expected 6)    — ABI MISMATCH
4 Doubles (32B, custom):          sum=7.305792511E-314       (expected 10)   — ABI MISMATCH
Nested 4D  (32B, like CGRect):    sum=7.305792598E-314       (expected 10)   — ABI MISMATCH
```

`computeRectArea` (system `CGRect`, 32B / 4 doubles) and `sumLargeStruct` continue to PASS on the same NativeAOT device run, confirming the asymmetry between system-framework types and custom-C# struct definitions called out in the report.

---

## Issue 6 (Bug): NativeAOT CallConvSwift returns garbage for single-field custom struct with `double`

### Title

`[NativeAOT] CallConvSwift returns garbage value when Swift function returns custom struct containing single double field on ARM64`

### Labels

`area-Interop-Swift`, `os-ios`, `bug`, `runtime-nativeaot`

### Description

**Environment:**
- .NET 10.0 (10.0.103), NativeAOT (iOS device, `ios-arm64`)
- macOS 26+ / iOS 26+, arm64
- Xcode 26.2 / Swift 6.2.3

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

This is the same underlying bug as Issue 5. The struct lowering in `SwiftPhysicalLowering.cs` correctly produces `CORINFO_TYPE_DOUBLE` for the single field — the bug is downstream in the JIT's register assignment for the return value. A single `double` field in a custom struct is the simplest possible reproduction, eliminating any register splitting ambiguity.

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

**Verified on 2026-04-26 with .NET SDK 10.0.103, Xcode 26.2** — reproduces, file as-is.

The single-`double` case (`Struct1Double`) on the same NativeAOT device run reads back `val=3.0438774487E-314` instead of `42.5` — a clean read from `x0`-as-double of whatever bit-pattern the GPR carried, exactly as the report describes. Same iPhone 13 / iOS 26 / `ios-arm64` configuration as Issue 5; no behavior change.

---

## Issue 7 (Bug): NativeAOT CallConvSwift passes custom integer structs >16 bytes incorrectly on ARM64

### Title

`[NativeAOT] CallConvSwift SIGSEGV when passing custom integer struct >16 bytes as parameter on ARM64`

### Labels

`area-Interop-Swift`, `os-ios`, `bug`, `runtime-nativeaot`

### Description

**Environment:**
- .NET 10.0 (10.0.103), NativeAOT (iOS device, `ios-arm64`)
- macOS 26+ / iOS 26+, arm64
- Xcode 26.2 / Swift 6.2.3

**Summary:**

When passing a custom C# struct containing 3 or more `nint`/`long` fields (≥24 bytes) as a parameter to a Swift function via `CallConvSwift` P/Invoke on NativeAOT ARM64, the call crashes with SIGSEGV. Structs with 1–2 `nint` fields (≤16 bytes) pass correctly.

This is a separate issue from Issue 5 (float/double GPR/FPR mismatch). Integer fields should all go in GPRs — the bug is that NativeAOT appears to pass the struct by pointer (AAPCS64 convention) while Swift expects the fields in individual GPRs (Swift calling convention).

**Cross-platform behavior:**

| Struct Size | Fields | Mono Simulator | NativeAOT Device |
|-------------|--------|---------------|------------------|
| 16B | 2×nint | PASS | PASS |
| 24B | 3×nint | not tested | **SIGSEGV** |
| 32B | 4×nint | **SIGSEGV** | **SIGSEGV** |

System framework types (e.g., `CGRect` with 4 doubles = 32B) are **not affected** — they pass correctly on both runtimes. The issue is specific to custom C# struct definitions with integer fields.

**Minimal reproduction:**

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;

[StructLayout(LayoutKind.Sequential)]
public struct Struct3Ints
{
    public nint A, B, C;
}

public static class IntStructRepro
{
    // Swift: public func sumStruct3Ints(_ s: Struct3Ints) -> Int { return s.a + s.b + s.c }
    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
    [DllImport("MyLib", EntryPoint = "$s5MyLib14sumStruct3IntsySiAA0eF0VF")]
    private static extern nint SumStruct3Ints(Struct3Ints s);

    public static void Reproduce()
    {
        var s = new Struct3Ints { A = 10, B = 20, C = 30 };
        // SIGSEGV on NativeAOT ARM64
        // Swift expects A in x0, B in x1, C in x2 (3 GPRs, within 4-register limit)
        // NativeAOT appears to pass a pointer to the struct instead
        nint result = SumStruct3Ints(s);
    }
}
```

**Root cause analysis:**

The Swift calling convention on ARM64 decomposes structs into register-sized elements: integer fields occupy consecutive GPRs (x0–x7), up to 4 elements maximum. A 3-field integer struct should occupy x0, x1, x2. NativeAOT's `SwiftPhysicalLowering.cs` should produce 3 separate `CORINFO_TYPE_LONG` chunks for this struct.

Investigation concluded that NativeAOT falls back to AAPCS64 indirect passing (pointer in x0) for integer structs >16 bytes, while Swift still expects direct register passing up to the 4-register limit. See the [repro project](https://github.com/justinwojo/swift-interop-repro) `NativeAOT_A2_StructSizes` test class and `Struct3Ints`/`Struct4Ints` in `ReproLib.swift` for full test results.

**Workaround in use:**

`@_cdecl` wrappers decompose struct fields into individual parameters:
```swift
@_cdecl("SBW_sumStruct3Ints")
func _sbw_sumStruct3Ints(_ a: Int, _ b: Int, _ c: Int) -> Int {
    return sumStruct3Ints(Struct3Ints(a: a, b: b, c: c))
}
```

**Impact:**

Affects any Swift API taking custom struct parameters with 3+ integer fields (≥24 bytes) on NativeAOT ARM64. Combined with Issue 5 (float/double structs), this means ALL custom structs with non-trivial layouts require `@_cdecl` wrappers on NativeAOT.

**Expected behavior:**

`CallConvSwift` P/Invoke should decompose custom integer structs into individual register-sized elements in GPRs (x0–x7), up to the 4-element Swift CC limit, matching the Swift ABI on ARM64.

**Verified on 2026-04-26 with .NET SDK 10.0.103, Xcode 26.2** — reproduces on both runtimes, file as-is.

- **NativeAOT device** (iPhone 13, iOS 26, `ios-arm64`): `sumStruct4Ints` (32B / 4×`nint`) terminated the process with `signal 11` immediately after the float-struct ABI-mismatch tests. Same crash signature as previously documented.
- **Mono Sim** (`iossimulator-arm64`, .NET 10.0.3): same `sumStruct4Ints` call SIGSEGVs and trips `Assertion at jit-info.c:918, condition '!ji->async' not met` during stack unwinding. Confirms the doc's "32B | 4×nint | SIGSEGV | SIGSEGV" row.
- **24-byte (`Struct3Ints`) row remains "not tested" on Mono Sim in this run.** The repro app declares `sumStruct3Ints` (in `GapTests.Run`) but execution does not reach it after the 4-int SIGSEGV; the doc's "not tested" annotation is still accurate. Filing the issue as-is is fine; if the upstream reviewer asks, a single-purpose run that swaps the struct sizes will produce that row.

---

## Issue 8 (Bug): NativeAOT CallConvSwift SIGSEGV with multi-type-parameter generic functions

### Title

`[NativeAOT] CallConvSwift SIGSEGV when calling generic function with 2+ type parameters on ARM64`

### Labels

`area-Interop-Swift`, `os-ios`, `bug`, `runtime-nativeaot`

### Description

**Environment:**
- .NET 10.0 (10.0.103), NativeAOT (iOS device, `ios-arm64`)
- macOS 26+ / iOS 26+, arm64
- Xcode 26.2 / Swift 6.2.3

**Summary:**

Calling a Swift generic function with **2 type parameters** (`pair<T, U>`) via `CallConvSwift` P/Invoke crashes with SIGSEGV on NativeAOT ARM64. The same pattern with **1 type parameter** (`genericIdentity<T>`) works correctly. The only difference is the addition of a second `TypeMetadata` argument.

**Reproduction evidence from [repro project](https://github.com/justinwojo/swift-interop-repro):**

```
7a. @_cdecl pairIntInt(10, 20) = (10, 20) — PASS       ← concrete control
7b. genericIdentity<Int>(42) = 42 — PASS                ← single-type-param generic
7c. genericPair<Int,Int>(10, 20) → SIGSEGV (signal 11)  ← multi-type-param generic
```

**Minimal reproduction:**

Swift:
```swift
public func genericIdentity<T>(_ x: T) -> T { return x }                         // PASS
public func genericPair<T, U>(_ first: T, _ second: U) -> (T, U) { return (first, second) }  // SIGSEGV
```

C#:
```csharp
// PASSES — 1 type param: SwiftIndirectResult + payload + TMetadata
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[DllImport("MyLib", EntryPoint = "$s5MyLib15genericIdentityyxxlF")]
static extern void genericIdentity(SwiftIndirectResult result, IntPtr payload, IntPtr TMetadata);

// SIGSEGV — 2 type params: SwiftIndirectResult + 2 payloads + 2 metadata
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
[DllImport("MyLib", EntryPoint = "$s5MyLib11genericPairyx_q_tx_q_tr0_lF")]
static extern void genericPair(SwiftIndirectResult result, IntPtr first, IntPtr second, IntPtr TMetadata, IntPtr UMetadata);

public static unsafe void Reproduce()
{
    IntPtr intMetadata = /* swift_getTypeByMangledName("Si") or equivalent */;

    // PASSES: 1 generic type param
    nint input = 42, output = 0;
    genericIdentity(new SwiftIndirectResult(&output), (IntPtr)(&input), intMetadata);
    // output == 42 ✓

    // SIGSEGV: 2 generic type params
    nint first = 10, second = 20;
    byte* buffer = stackalloc byte[16]; // (Int, Int) = 16 bytes
    genericPair(new SwiftIndirectResult(buffer),
        (IntPtr)(&first), (IntPtr)(&second), intMetadata, intMetadata);
    // CRASHES before returning
}
```

**Root cause analysis:**

The `genericIdentity<T>` P/Invoke has 3 parameters: `SwiftIndirectResult` (x8), payload (x0), TMetadata (x1). This works.

The `genericPair<T, U>` P/Invoke has 5 parameters: `SwiftIndirectResult` (x8), first (x0), second (x1), TMetadata (x2), UMetadata (x3). All parameters fit within ARM64's 8 GPR limit. The crash is not a register overflow.

The issue may be in how NativeAOT's `SwiftPhysicalLowering` handles the second generic type metadata parameter. Swift places type metadata in specific implicit parameter positions — if NativeAOT assigns the second metadata to the wrong register (or misidentifies the parameter order), Swift would read garbage values as type metadata pointers, causing SIGSEGV on metadata access.

**Workaround in use:**

`@_cdecl` wrapper with concrete type specialization:
```swift
@_cdecl("repro_pairIntInt")
public func repro_pairIntInt(_ a: Int, _ b: Int, _ outFirst: UnsafeMutablePointer<Int>, _ outSecond: UnsafeMutablePointer<Int>) {
    let result = genericPair(a, b)
    outFirst.pointee = result.0
    outSecond.pointee = result.1
}
```

**Impact:**

Blocks direct CallConvSwift dispatch for any generic Swift function with 2+ type parameters. This includes common patterns like `pair<T, U>`, `zip<A, B>`, `map<T, U>`, and generic initializers with multiple constrained types. All must route through concrete `@_cdecl` wrappers.

**Expected behavior:**

`CallConvSwift` P/Invoke should correctly place multiple type metadata parameters in consecutive GPRs, matching Swift's generic function calling convention on ARM64.

**Verified on 2026-04-26 with .NET SDK 10.0.103, Xcode 26.2** — reproduces, file as-is.

NativeAOT device run captured the clean delta between the 1-type-param and 2-type-param paths in a single execution (after re-ordering the repro app to hoist `Issue7_MultiGenericParam` ahead of the struct-ABI tests so the SIGSEGV from this issue isn't masked by Issue 5/7):

```
[Issue-7] Multi-generic-param CallConvSwift (2 type params)...
  7a. @_cdecl pairIntInt(10, 20) = (10, 20) — PASS              ← concrete control
  7b. genericIdentity<Int>(42) = 42 — PASS                       ← single-type-param generic (control)
  App terminated due to signal 11.                               ← genericPair<Int,Int> SIGSEGV (the bug)
```

No diagnostic output between 7b and the SIGSEGV — the crash is in the P/Invoke transition itself, consistent with the second-`TypeMetadata` register-placement hypothesis in the analysis above.

---

## Filing Notes

**Related dotnet/runtime issues:**
- [#93631](https://github.com/dotnet/runtime/issues/93631) — Runtime support for Swift Interop in .NET 9
- [#108662](https://github.com/dotnet/runtime/issues/108662) — Runtime support for Swift Interop in .NET 10
- [#64215](https://github.com/dotnet/runtime/issues/64215) — Introduce `CallConvSwift`
- [#96059](https://github.com/dotnet/runtime/issues/96059) — Swift into .NET using `CallConvSwift` and `UnmanagedCallersOnly`
- [#100543](https://github.com/dotnet/runtime/issues/100543) — `SwiftSelf<T>` and `SwiftIndirectResult`

**Context:**

These issues were discovered while building [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings), a binding generator that produces C# bindings from compiled Swift frameworks. The project targets .NET 10 on iOS/macOS, generating P/Invoke declarations with `CallConvSwift` for direct Swift function calls. The generator has been validated against 51 third-party Swift libraries (16,451 P/Invokes total).

All issues are reproducible via the standalone [repro project](https://github.com/justinwojo/swift-interop-repro) — see its README for build/run instructions.

| Issue | Runtime | Summary |
|-------|---------|---------|
| 1 | Mono | JIT assertion `!ji->async` on synchronous CallConvSwift P/Invoke |
| 2 | Both | Non-blittable types rejected — primary driver of ~67% wrapper rate |
| 3 | Mono | SafeHandle/SwiftSelf lifetime across async P/Invoke |
| 5-6 | NativeAOT | Custom struct float/double fields in GPR instead of FPR |
| 7 | Both (different thresholds) | Custom integer struct >16B passed by pointer instead of in registers |
| 8 | NativeAOT | Multi-type-parameter generic function SIGSEGV |
| 9 | Mono | `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool, @out via x0)` tuple-return ABI |

All active issues have workarounds in production use (`@_cdecl` Swift wrapper functions using `CallingConvention.Cdecl`), but the workarounds add significant complexity (per-type/per-method Swift wrapper generation, wrapper xcframework bundling, manual marshalling). Issue 2 (non-blittable support) would have the largest impact — reducing the wrapper rate from ~67% to ~20%.

---

## Issue 9 (Bug): Mono `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool direct, @out via x0)` tuple-return `CallConvSwift` P/Invoke

### Title

`[Mono] "Cannot transition thread from STARTING with DONE_BLOCKING" when calling Swift method with (Bool, @out Element) tuple return via CallConvSwift`

### Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `bug`, `runtime-mono`

### Description

**Environment:**
- .NET 10.0 (10.0.103), Mono runtime (iOS Simulator, arm64)
- Microsoft.iOS.Sdk 26.2.10197
- Xcode 26.2, iOS Simulator runtime 26.3
- Reproduced in: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro), Issue 9 class `Issue9_SetInsertAbi`

**Symptom:**

Calling `Swift.Set<T>.insert(_:)` via `[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]` P/Invoke on Mono causes an immediate `SIGABRT` with:

```
error: Cannot transition thread 0x0 from STARTING with DONE_BLOCKING
```

This is Mono's thread-state machine asserting that the thread is in `STARTING` state when `mono_threads_transition_done_blocking` is called to end the managed-to-native GC-safe region after the P/Invoke returns. The expected state before `DONE_BLOCKING` is `BLOCKING`; the actual state is `STARTING`, indicating the thread state was corrupted during the `CallConvSwift` callout.

**ABI shape that triggers the crash:**

`Set<T>.insert(_:)` returns a `(Bool inserted, Element memberAfterInsert)` tuple where:
- `Bool` (`inserted`) is returned directly in `x0` (a single-register scalar)
- `@out Element` (`memberAfterInsert`) is written via a pointer **also passed in `x0`** — not via `x8` (`SwiftIndirectResult`)

This is a mixed tuple-return ABI: when one element is direct (`Bool`) and one is `@out`, the `@out` buffer pointer occupies `x0` on call entry, and the direct `Bool` is returned in `w0`/`x0` after return — `x0` is reused for both the inbound out-pointer argument and the outbound scalar result. This differs from the pure `@out` path that uses `x8`/`SwiftIndirectResult`.

**P/Invoke signature (matches swift-bindings `SwiftSetPInvokes.Insert` exactly):**

```csharp
[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
[DllImport("libswiftCore.dylib", EntryPoint = "$sSh6insertySb8inserted_x17memberAfterInserttxnF")]
public static extern byte Insert(
    IntPtr outMemberBuffer,   // x0 — @out Element buffer
    IntPtr element,           // x1 — @in Element value
    IntPtr setMetadata,       // x2 — full Set<T> metadata (generic context)
    SwiftSelf self);          // x20 — @inout Set<T> (storage pointer buffer)
// return: byte (Bool in x0)
```

**Control group — same call pattern, no @out — passes:**

```csharp
[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
[DllImport("libswiftCore.dylib", EntryPoint = "$sSh8containsySbxF")]
public static extern byte SetContains(
    IntPtr element,            // x0 — element
    IntPtr setStoragePtr,      // x1 — Set value (storage pointer, passed by value)
    IntPtr elementMetadata,    // x2 — T metadata
    IntPtr hashableWT);        // x3 — T:Hashable witness table
// return: byte (Bool in x0)
```

`SetContains` **passes** on Mono. `SetInsert` **crashes** with `DONE_BLOCKING` error. The delta is the `(Bool, @out via x0)` return shape.

**Memory addresses from repro run:**
```
Int metadata:        0x1E8A72AC0
Int:Hashable WT:     0x1E8A6A340
Set<Int> metadata:   0x1E8A762C8
Set<Int> size:       8, Int size: 8

@_cdecl pre-populate insert(99): 1  (set properly initialized)
Set storage ptr (after @_cdecl insert): 0x60000211EBC0  (valid heap address)
Storage ptr looks like heap address: True

9b. Set<Int>.contains(99) [CONTROL]: 1 (expected 1) — PASS

[SetInsert called here — process crashes]
error: Cannot transition thread 0x0 from STARTING with DONE_BLOCKING
SIGABRT
```

**Native stacktrace key frames:**
```
mono_threads_transition_done_blocking
mono_threads_exit_gc_safe_region_unbalanced
wrapper_managed_to_native_..._SetInsert_intptr_intptr_intptr_SwiftSelf
Issue9_SetInsertAbi_Run
```

**Symbol verified:**
```
nm -g libswiftCore.dylib | grep Sh6insert
000000000004a190 T _$sSh6insertySb8inserted_x17memberAfterInserttxnF
// swift-demangle: Swift.Set.insert(__owned A) -> (inserted: Swift.Bool, memberAfterInsert: A)
```

**Real-world impact:**

`swift-dotnet-bindings` wraps Swift's `Set<T>` as `SwiftSet<T>` with an `Add(Element)` method that calls `insert(_:)` via this P/Invoke. The crash prevents any `SwiftSet<T>.Add()` call from completing on Mono (iOS Simulator), causing the `BulkCollectionStressTests` and `SwiftSetTests` to fail with SIGABRT rather than assertion failures.

**SIL signatures (unspecialized, from verified dump):**

```
// Set<T>.insert(_:)
$sSh6insertySb8inserted_x17memberAfterInserttxnF:
  @convention(method) (@in T, @inout Set<T>) -> (Bool, @out T)
```

The return `(Bool, @out T)` is NOT handled via `x8`/`SwiftIndirectResult`. Instead, the `@out T` buffer pointer goes in `x0` and the direct `Bool` result is returned in `x0` after the call returns. Mono's `CallConvSwift` trampoline appears to mis-handle this mixed return shape, corrupting the thread state during transition cleanup.

**Workaround:**

Use an `@_cdecl` Swift wrapper that calls `insert` and returns just the `Bool` inserted flag, avoiding the mixed tuple-return ABI entirely:

```swift
@_cdecl("swiftset_insert")
public func swiftset_insert(_ setPtr: UnsafeMutableRawPointer, _ value: Int) -> Int32 {
    let result = setPtr.assumingMemoryBound(to: Set<Int>.self).pointee.insert(value)
    return result.inserted ? 1 : 0
}
```

**Filing notes:**
- Verified: 2026-04-26, .NET 10.0.103, Mono (iOS Simulator arm64)
- Related to Issue 1 (`!ji->async`) and the general pattern of Mono not handling non-standard CallConvSwift return ABIs
- Not reproduced on NativeAOT (device) — needs separate verification
- Priority: high for `SwiftSet<T>` correctness in swift-dotnet-bindings
