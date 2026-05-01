# Upstream Issues for dotnet/runtime

Updated: April 2026 (re-verified 2026-04-26)
Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings)
Contact: Justin Wojciechowski
Repro project: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro)

Eight issues are tracked in this draft (numbered 1, 2, 3, 5, 6, 7, 8, 9 — there is no Issue 4). After the 2026-04-30 fact-check pass, **four** are confirmed upstream issues (Issues 1, 2, 3, 9 — filed as bug, feature request, tracking-issue comment, and bug respectively) and the remaining four (Issues 5, 6, 7, 8) are closed. Issues 5/6/7 were a Swift resilience-vs-`@frozen` ABI mismatch on our side; Issue 8 was a wrong P/Invoke shape on our side. Issue 2 (non-blittable type support) has the highest impact — it's the primary driver of ~67% of P/Invokes needing wrapper functions across 51 third-party Swift library bindings. Searches of dotnet/runtime issues (February 2026) found no existing reports. The main Swift interop tracking issues ([#93631](https://github.com/dotnet/runtime/issues/93631) for .NET 9, [#108662](https://github.com/dotnet/runtime/issues/108662) for .NET 10) do not mention the four confirmed issues.

> **2026-04-26 re-verification environment** (used for every "Verified on" line below):
> - .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, runtime framework 10.0.3
> - Xcode 26.2 (build 17C52), iOS Simulator runtime 26.3, iPhone 13 device on iOS 26
> - macOS host arm64, repro project at `/Users/wojo/Dev/swift-interop-repro/`
>
> **Behavioral shifts caught by this run** (read before filing — flag loudly):
> - **Issue 1's original trigger no longer reproduces.** The direct `swift_getExistentialTypeMetadata` P/Invoke shown in the repro now PASSES on Mono (.NET 10.0.103). The `!ji->async` assertion at `jit-info.c:918` still fires, but only as a *secondary* crash during Mono signal-handler stack unwinding from an unrelated native SIGSEGV (e.g. Issue 7's `sumStruct4Ints`, or the SkipReduction `FourStringInit_Struct` 4-String struct call). The assertion bug is real and reproducible, but the minimal repro snippet currently in the draft is stale — see Issue 1 below for the updated trigger evidence and a TODO before filing.
> - All six remaining issues (2, 3, 5, 6, 7, 8) reproduce with the same symptoms documented previously. *[Superseded by the 2026-04-30 fact-check below: Issues 5/6/7 are now closed (resilience-vs-`@frozen` mismatch on our side, not a runtime bug); Issue 8 is closed (wrong P/Invoke shape on our side).]*
> - **Issue 9 added (2026-04-26).** Mono `Cannot transition thread 0x0 from STARTING with DONE_BLOCKING` when calling `Set<T>.insert` via `CallConvSwift`. The `(Bool direct, @out via x0)` tuple-return ABI shape corrupts Mono's thread state machine during the managed-to-native transition. `Set.contains` (simpler ABI, no `@out` tuple) passes. Confirmed in standalone repro (Issue 9 in `swift-interop-repro`). Root cause in swift-bindings: `SwiftSet<T>.InsertUnsafe` uses `SwiftSetPInvokes.Insert` with this ABI shape.

> **2026-04-30 fact-check pass** (Codex independent review + direct disassembly verification of `libswiftCore.dylib` arm64 simulator slice and the repro framework). Per-issue verdicts captured in each section's `2026-04-30 fact-check` block. **Outcome summary:**
> - **File as new issues:** Issue 1 (bug), Issue 2 (feature request, with minor source-pointer corrections), Issue 9 (bug).
> - **Post as tracking-issue comment:** Issue 3 (tightened wording, upgrade example to `DangerousAddRef`).
> - **Close / do not file:** Issue 8. Disassembly of `_$s13SwiftReproLib11genericPairyx_q_tx_q_tr0_lF` proves Swift's multi-result tuple-return convention places result destinations in `x0`/`x1` and metadata in `x4`/`x5` — not `x8` + sequential GPRs as the draft assumes. Our P/Invoke shape is wrong; with the correct shape, no SIGSEGV is expected. (See Issue 8 fact-check block.)
> - **Close / do not file:** Issues 5, 6, 7. The repro library at `/Users/wojo/Dev/swift-interop-repro/SwiftReproLib/Sources/ReproLib.swift` was built with `-enable-library-evolution` and the public structs were not marked `@frozen`, so Swift compiled them as resilient — the callee expected a *pointer* in `x0` and used `ldr d0, [x0]` / `ldp d0, d1, [x0]` to load fields. .NET passing the first field directly in `x0` then dereferenced an integer-as-address. The "garbage value" / SIGSEGV symptoms were entirely the resilience mismatch, not GPR/FPR or AAPCS64-pointer-fallback bugs. `CGRect` passed because it's an imported C struct (`So6CGRectV` in mangled name) and follows the platform direct-passing path; the failing structs were Swift-native (`AA…V`) and resilient. The **2026-04-30 rebuild** added `@frozen` (keeping `-enable-library-evolution`) and re-disassembled / re-tested on Mono Sim + NativeAOT device. The new disassembly shows direct register consumption (`fadd d0, d0, d1`, `adds x8, x0, x1`, etc.) and **every previously-failing case now passes on both runtimes** — see the `2026-04-30 rebuild result` blocks in each issue. Issue 6 is also merged into Issue 5 (single-`double` parameter case is the minimal-repro subsection of Issue 5, not a separate return-bug as the draft narrative claimed).

> **Before filing:**
> 1. Re-search dotnet/runtime issues to confirm nothing has been filed in the interim.
> 2. Re-verify each repro against the current .NET SDK version.
> 3. Update version numbers in each issue to match the SDK used for verification.
> 4. Replace `justinwojo/swift-interop-repro` URLs with the actual published repo URL if different.
> 5. **Issue 1 specifically:** rewrite the minimal repro to use the current trigger (a P/Invoke into Swift code that itself crashes — e.g. a struct with bad calling-convention layout — so Mono walks the stack and trips the assertion). The existential-metadata P/Invoke alone is no longer sufficient.
> 6. **Issues 5/6/7 specifically:** do not file. The 2026-04-30 `@frozen` rebuild eliminated every previously-observed garbage / SIGSEGV symptom on both Mono Sim and NativeAOT device. Issue 6 is merged into Issue 5 as the single-`double` minimal-repro subsection. The repro now ships with `@frozen` on the affected structs so the resilient-struct ABI mismatch is no longer present.
> 7. **Issue 8 specifically:** do not file as drafted. Either close, or rewrite as a corrected-shape repro (two result pointers in `x0`/`x1`, payloads in `x2`/`x3`, metadata in `x4`/`x5`) and re-test before claiming a runtime bug.

**Filing strategy:**
- **Issue 2** — File as a **feature request** (highest priority). Non-blittable `CallConvSwift` support. Includes source-level analysis of both Mono (`marshal.c:3729`, parameter-only blittable validation) and CoreCLR (`SwiftPhysicalLowering.cs` rejection of types containing GC pointers) rejection points, architectural suggestion (run marshalling before blittable validation), and incremental approach (SafeHandle first, then String).
- **Issue 1** — File as a **bug report**. Mono JIT `jit-info.c:918` assertion failure during signal-handler stack unwinding through a `wrapper_managed_to_native_*` frame.
- **Issue 3** — **Mono-only**. Post as a **comment on the Swift interop tracking issue** asking whether async P/Invoke with `SwiftSelf<SafeHandle>` is a supported scenario on Mono. Use the `DangerousAddRef`/`DangerousGetHandle`/`Arc.Retain` pattern in the example, not bare `DangerousGetHandle`.
- **Issue 5 (merged with Issue 6)** — **Close.** The 2026-04-30 `@frozen` rebuild fully resolved every previously-observed symptom on both Mono Sim and NativeAOT device. The original "garbage value / GPR-instead-of-FPR" framing was a misdiagnosis: `-enable-library-evolution` without `@frozen` made the Swift structs resilient, so the callee took a pointer in `x0` and read fields via `ldr d0, [x0]` / `ldp d0, d1, [x0]`. .NET (correctly) passed the first field directly in `x0`, which Swift then dereferenced as an address. `CGRect` worked because it's `So6CGRectV` (an imported C struct on the platform direct-passing path), not because it's a "system framework HFA." The Issue 6 single-`double` case is the minimal-repro subsection of Issue 5, not a separate return-bug.
- **Issue 7** — **Close.** Same root cause as Issues 5/6 — the `@frozen` rebuild also resolved the 24B/32B integer-struct SIGSEGVs on both runtimes. The "AAPCS64 indirect-passing fallback at >16B" hypothesis is incorrect (Mono's by-reference cutoff is 32B and the lowered-elements cap is 4; 3×`Int` and 4×`Int` should both lower to direct GPRs). Swift was forcing indirect passing because the struct had resilient layout.
- **Issue 8** — **Close.** The 2-type-param SIGSEGV is a wrong-P/Invoke-shape bug on our side, not a NativeAOT register-placement bug. Multi-result tuple returns use `(x0=result1, x1=result2, x2=payload1, x3=payload2, x4=Tmeta, x5=Umeta)`, not `(x8=SwiftIndirectResult, x0=payload, x1=Tmeta, x2=Umeta, …)` as the draft claims.
- **Issue 9** — File as a **bug report**, with the framing softened on the precise mechanism: empirical observation is that Mono asserts on this specific `(Bool direct, @out via x0)` ABI shape; controls (`Set.contains`, `Dictionary.updateValue`) pass. The exact corruption path inside Mono's `CallConvSwift` trampoline is not pinned down (Mono's thread-state implementation is in mono-mini sources we did not exhaustively trace).

---

## Issue 1 (Bug): Mono JIT assertion failure (`!ji->async` at `jit-info.c:918`) during signal-handler stack unwind through a `CallConvSwift` `wrapper_managed_to_native_*` frame after a native crash in the Swift callee

### Title

`[Mono] JIT assertion "!ji->async" at jit-info.c:918 during signal-handler unwind through a CallConvSwift frame after a native crash`

### Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `bug`, `runtime-mono`

### Description

**Environment:**
- .NET 10.0 (10.0.103), Mono runtime (iOS Simulator / Mac Catalyst)
- macOS 26.2 / iOS 26+, arm64
- Xcode 26.2 (build 17C52) / Swift 6.2.3
- Microsoft.iOS.Sdk 26.2.10197
- Reproduced in: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro), `Issue1_MonoSignalHandlerAssert` class

**Summary:**

When a Swift function called via `[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]` P/Invoke crashes natively (SIGSEGV / null deref / similar), Mono's signal handler walks the stack to dump native crash info. Walking encounters the `wrapper_managed_to_native_*` frame Mono synthesised for the `CallConvSwift` P/Invoke. The corresponding `MonoJitInfo` is classified as async, and the assertion `g_assert(!ji->async)` at `mono/metadata/jit-info.c:918` fires during the unwind, replacing the original native-crash report with a Mono assertion abort.

The bug is in the unwinder's frame-async classification, **not** in the call itself — the `CallConvSwift` call dispatches and returns correctly. The assertion only fires on the unwind path after a separate native crash, which clobbers the underlying SIGSEGV diagnostics.

**Stack trace (from `Issue1_MonoSignalHandlerAssert`):**

```
* Assertion at mono/metadata/jit-info.c:918, condition `!ji->async' not met

Native stacktrace:
  mono_jit_info_table_find_internal
  mono_jit_info_table_find
  mono_dump_native_crash_info
  ...signal-handler stack walk...
  wrapper_managed_to_native_SwiftInteropRepro_Issue1_MonoSignalHandlerAssert_issue1_callConvSwiftCrash
  issue1_callConvSwiftCrash (Swift, SIGSEGV writing to 0x10)
```

The original `SIGSEGV at 0x10` from Swift never makes it to the user — the stack-walk assertion fires first.

**Minimal reproduction:**

The full repro is `Issue1_MonoSignalHandlerAssert` in [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro); the essential pieces are:

```swift
// Swift side — a CallConvSwift function that null-derefs.
public func issue1_callConvSwiftCrash() {
    let p = UnsafeMutablePointer<Int>(bitPattern: 0x10)!
    p.pointee = 0
}
```

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Swift;

public static class Issue1Repro
{
    [UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
    [DllImport("SwiftReproLib", EntryPoint = "$s13SwiftReproLib015issue1_callConvA5CrashyyF")]
    private static extern void issue1_callConvSwiftCrash();

    public static void Reproduce()
    {
        // The Swift callee SIGSEGVs writing to 0x10. Expected: a normal
        // SIGSEGV crash report. Actual on Mono: !ji->async assertion at
        // jit-info.c:918 during signal-handler stack walk through the
        // wrapper_managed_to_native_* frame, which replaces the original
        // SIGSEGV diagnostics.
        issue1_callConvSwiftCrash();
    }
}
```

The triggering pattern is **any** Swift function called via `CallConvSwift` that crashes natively. Examples observed in the wider swift-bindings test suite that hit the same assertion include `Set<T>.insert` via `CallConvSwift` (separate Mono bug — see [companion issue], Mono `DONE_BLOCKING`), and any `@_cdecl` Swift wrapper that dereferences a stale buffer pointer.

**Root cause analysis:**

The function under `CallConvSwift` is synchronous — it performs no `async` operations. Mono's JIT appears to incorrectly infer that `CallConvSwift` frames may be async (possibly confused by the Swift async context register / Swift continuation conventions), and the synthesized `wrapper_managed_to_native_*` `MonoJitInfo` is marked async. During signal-handler stack unwinding, `mono_jit_info_get_method()` (or its callers in `mono_dump_native_crash_info`) is called on that `MonoJitInfo`, triggering `g_assert(!ji->async)` in `jit-info.c:918`.

Workarounds attempted (all failed):
| Approach | Result |
|----------|--------|
| `[SuppressGCTransition]` | Same assertion |
| `CallingConvention.Cdecl` instead of `CallConvSwift` | Different code path, doesn't avoid the assertion when the Swift callee crashes |
| Wrapping the Swift callee in `@_silgen_name` | Avoids the assertion only because the Swift wrapper doesn't go through `wrapper_managed_to_native_CallConvSwift` |

**Workaround in use:**

Avoid native crashes inside `CallConvSwift` callees. Where the bug surfaces, route the call through an `@_cdecl` Swift wrapper that uses `CallingConvention.Cdecl` and validates inputs before dereferencing.

**Impact:**

Any native crash inside a Swift function called via `CallConvSwift` (whether from a binding bug, a misspecified P/Invoke shape, or a Swift-side runtime issue) is masked by the `!ji->async` assertion. This makes diagnostic crashes much harder to read because the original SIGSEGV / null-deref information is lost — the developer sees only the Mono assertion. It also turns recoverable Swift errors into process aborts on Mono. NativeAOT does not exhibit this behavior; the same Swift crash on NativeAOT produces a normal SIGSEGV crash report.

**Expected behavior:**

The signal-handler stack walk should not abort with `!ji->async` when traversing a `wrapper_managed_to_native_*` frame for a `CallConvSwift` P/Invoke. Either the synthesized `MonoJitInfo` should not be classified as async, or `mono_dump_native_crash_info` and its callees should tolerate async-classified frames during unwind.

**Verified on 2026-04-30** with .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, Mono iOS Simulator runtime 26.3, Xcode 26.2 (build 17C52). The deliberately-crashing `CallConvSwift` callee in `Issue1_MonoSignalHandlerAssert` reliably triggers the assertion. The earlier framing in this draft, which claimed a synchronous direct call to `swift_getExistentialTypeMetadata` was the trigger, is **superseded** — that direct call now returns successfully on Mono with .NET 10.0.103; only the unwind path through a crashing `CallConvSwift` frame still reproduces. Verification scope: ABI symbol confirmed via `nm`; assertion path confirmed by signal-handler stack trace matching `mono/metadata/jit-info.c:918`. We have not stepped through Mono's frame-async classification with a custom Mono build; the reviewer can do so locally to confirm whether the wrapper synthesis or the consumer of `MonoJitInfo->async` is the right fix layer.

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

**Verified on 2026-04-30** with .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, Mono iOS Simulator runtime 26.3, NativeAOT iOS device (iPhone 13 / iOS 26 / `ios-arm64`), Xcode 26.2 (build 17C52). Reproduction confirmed across BindingTests / sim-validation / repro-app paths: `InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.` is still thrown for `SafeHandle` / `SwiftSelf<SafeHandle>` / managed `string` parameters under `CallConvSwift` on both Mono and NativeAOT. Source-pointer corrections (parameter-only blittable check on Mono, `ContainsGCPointers` rejection in CoreCLR `JitInterface`, Swift `String` as a separate bridge from `System.String`) have been applied inline. The architectural recommendation — compose the existing IL marshalling stub pipeline (e.g. `ILSafeHandleMarshaler`) with the Swift physical lowering, starting with `SafeHandle → IntPtr` and treating Swift `String` as a follow-up — is the proposal we'd like reviewer feedback on.

---

## Issue 3 (Tracking Issue Comment): SafeHandle/SwiftSelf lifetime across async P/Invoke with `CallConvSwift` (Mono-only)

> **Mono-only.** NativeAOT investigation (March 2026, .NET 10.0.103) confirmed this does not reproduce on NativeAOT.
> File as a **comment on the Swift interop tracking issue** (successor to [#108662](https://github.com/dotnet/runtime/issues/108662)), not a standalone issue.

### Suggested comment

**Subject:** Question: Is `SwiftSelf<T>` with a managed lifetime-bearing `T` (e.g. `SafeHandle`) supported across Swift `async` suspension on Mono with `CallConvSwift`?

We're building a binding generator that calls async Swift instance methods from C# via P/Invoke with `CallConvSwift`. The `self` parameter is passed via `SwiftSelf<T>`, where `T` is a `SafeHandle`-derived pointer holder.

We're observing that when an async Swift method suspends, the `SafeHandle` reference is not preserved across the Task continuation boundary on Mono — the GC can collect the handle (triggering `swift_release()`) while the Swift async operation is still in flight, even though the awaiting `Task` is still alive on the managed side.

Our reading of the marshalling code is that this might be by-design rather than a bug:

- The standard CoreCLR P/Invoke `SafeHandle` lifetime guarantee is *call-scoped*: `ILSafeHandleMarshaler::ArgumentOverride` (`coreclr/vm/ilmarshalers.cpp:~2899`) emits an `AddRef` before dispatch and `Release` in the cleanup block of the generated stub (`coreclr/vm/dllimport.cpp:~2280`). It guarantees liveness for the duration of the native call, not across an arbitrary Swift async suspension that continues after the P/Invoke returns.
- Mono's `CallConvSwift` path *bypasses* the normal `SafeHandle → IntPtr + AddRef/Release` marshalling for `SwiftSelf` (special-cased at `marshal.c:~3725` before the blittable check), so even on runtimes that *do* run `SafeHandle` marshalling, `SwiftSelf` doesn't go through it today.

Our current workaround is to generate Swift wrapper functions that use `Unmanaged` to recover the instance from a raw pointer, and on the C# side, explicitly `DangerousAddRef` → read pointer → `Arc.Retain` (Swift-side retain) → `DangerousRelease`, with a holder that releases the retained pointer when the async operation completes:

```swift
@_silgen_name("MyClass_asyncMethod_wrapper")
public func myClass_asyncMethod_wrapper(_ selfPtr: UnsafeMutableRawPointer) async {
    let instance = Unmanaged<MyClass>.fromOpaque(selfPtr).takeUnretainedValue()
    await instance.asyncMethod()
}
```

```csharp
public async Task AsyncMethod()
{
    bool addedRef = false;
    try
    {
        _handle.DangerousAddRef(ref addedRef);              // pin SafeHandle
        var ptr = _handle.DangerousGetHandle();             // safe to read after AddRef
        Arc.Retain(ptr);                                    // Swift-side ARC retain
        try { await MyClass_asyncMethod_wrapper(ptr); }
        finally { Arc.Release(ptr); }
    }
    finally
    {
        if (addedRef) _handle.DangerousRelease();
    }
}
```

The `DangerousAddRef` / `DangerousRelease` bracket avoids a finalizer race between reading the handle and retaining it on the Swift side; the Swift-side `Arc.Retain` keeps the underlying object alive even if the C# `SafeHandle` is collected after the P/Invoke returns. The repo's generator emits this pattern (see `WrapperEmitter.Async.cs`).

**Questions:**
1. Is async P/Invoke with `CallConvSwift` + `SwiftSelf<T>` (where `T` carries a managed lifetime) a supported scenario on Mono today, or is it explicitly out of scope for current `SwiftSelf` semantics?
2. If supported, what is the recommended pattern for ensuring the underlying handle stays alive across Swift async suspension points? Is there a runtime mechanism we're missing, or is the `DangerousAddRef` + Swift-side `Arc.Retain` pattern above the expected approach?
3. If unsupported today, is extending `SwiftSelf<T>` to a Task-scoped lifetime contract on the roadmap? It's required for calling any async instance method on a Swift class from .NET without per-method Swift wrapper generation.

This affects every async Swift instance method we bind — libraries like StoreKit 2 and Nuke rely heavily on async instance methods. Currently every such method requires an `@_silgen_name` Swift wrapper, which adds significant build complexity.

**Verified on 2026-04-30** with .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, Mono iOS Simulator runtime 26.3, Xcode 26.2 (build 17C52). The Mono-only scope is confirmed by recent BindingTests `--device` runs (NativeAOT path): the same async + `SwiftSelf<SafeHandle>` patterns succeed on NativeAOT iOS device. The standalone repro app does not include a dedicated async-suspension test (the `MonoC_ClosureCallback_WithSwiftSelf` block exercises the synchronous-callback shape but not the await-induced suspension that triggers the lifetime gap), so this filing relies on BindingTests evidence rather than a `swift-interop-repro` reduction.

---

## Issue 5 (Closed — not a runtime bug): NativeAOT CallConvSwift passes custom struct float/double fields in GPR instead of FPR on ARM64

> **Status: closed 2026-04-30 after the `@frozen` rebuild.** Every "garbage value" / "ABI MISMATCH" symptom dissolved once the repro structs were marked `@frozen`. The original framing was a misdiagnosis — see the `2026-04-30 rebuild result` block at the end of this section. The investigation history below is preserved for the audit trail; do not file. Issue 6 (single-`double` minimal repro) is now the minimal-repro subsection of this issue, not a separate filing.

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

**Key finding (corrected 2026-04-30 — see closure note at top of section):** `CGRect` (4 doubles, 32B) passes correctly because it is `So6CGRectV` — an *imported C struct* on the platform direct-passing path, not because it is a "system framework HFA." The structs that exhibited the bug were Swift-native (`AA…V`) compiled under `-enable-library-evolution` without `@frozen`, which made them resilient and forced the callee to take a pointer in `x0`. Once `@frozen`, the Swift-native structs use direct register passing too, and the symptoms disappear on both runtimes.

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

However, for custom C# struct definitions, the values appeared garbled — which the original analysis attributed to lowered float/double elements ending up in general-purpose registers (`x0`–`x7`) instead of floating-point registers (`d0`–`d7`). The 2026-04-30 disassembly disproved that hypothesis: Swift was reading the input via `ldr d0, [x0]` / `ldp d0, d1, [x0]` because the structs were resilient (`-enable-library-evolution` without `@frozen`). `CGRect` worked because it's `So6CGRectV`, an imported C struct on the platform direct-passing path — not because the JIT special-cases system types. After the rebuild added `@frozen`, the Swift-native structs use direct register passing too and the symptoms disappear.

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

**2026-04-30 fact-check (Codex independent review + direct disassembly):** **BLOCKED on Swift `@frozen` rebuild.** The current repro library at `/Users/wojo/Dev/swift-interop-repro/SwiftReproLib/Sources/ReproLib.swift` is built with `-enable-library-evolution` (see `build-swift-lib.sh:21,36`) and the public structs are not `@frozen`. Disassembly of the device framework shows Swift compiles them as resilient and consumes a *pointer* in `x0`:

```text
_$s13SwiftReproLib16getStruct1DoubleySdAA0eF0VF:
    ldr  d0, [x0]            ; load Double from address in x0

_$s13SwiftReproLib17sumStruct2DoublesySdAA0eF0VF:
    ldp  d0, d1, [x0]        ; load 2 Doubles from address in x0
    fadd d0, d0, d1
```

By contrast, `_$s13SwiftReproLib15computeRectAreaySo6CGRectVF` for `CGRect` uses `d0`/`d1`/`d2`/`d3` directly — `CGRect` is `So6CGRectV` (imported C struct) and follows the platform direct-passing path; the failing structs are `AA…V` (Swift-native, resilient).

If .NET passes the first `Double` field directly in `x0` (interpreting its bit pattern as a pointer), Swift dereferences an integer-as-address and reads garbage / SIGSEGVs. That is the simpler explanation than "JIT consumer of `CORINFO_SWIFT_LOWERING` mishandles `DOUBLE`/`FLOAT` lowered elements." We did not find evidence in the snapshot for a `CGRect`-specific runtime special case; the asymmetry is more likely "imported C struct vs. Swift-native resilient struct."

The `SwiftPhysicalLowering` producer reading is consistent with the source (`Struct1Double` → 1 `DOUBLE`, `Struct2Doubles` → 2 `DOUBLE`, etc., the by-reference fallback is `loweredTypes.Count > 4`). But its output does not match the resilient-struct ABI of the actual Swift binary — Swift is passing indirectly regardless of what the lowering producer thinks.

**Required before filing:** mark `Struct1Double`, `Struct2Doubles`, `Struct4Doubles`, `NestedRect` as `@frozen public struct` (keeping `-enable-library-evolution` so the resilience semantics are explicit), rebuild, re-disassemble, and re-test on NativeAOT device. If the rebuilt binary uses `d0`–`d3` directly and NativeAOT *still* passes via GPR, the runtime-bug framing stands. Otherwise, this is a documentation/support question about whether `CallConvSwift` P/Invoke is supposed to support resilient (non-frozen) Swift structs by value.

**2026-04-30 rebuild result (Mono Sim + NativeAOT device, post-`@frozen`):** **CLOSE — not a runtime bug.** Marked `Struct1Double`, `Struct2Doubles`, `Struct4Doubles`, `InnerPair`, `NestedRect` as `@frozen` (keeping `-enable-library-evolution`), confirmed `@frozen public struct` made it through to the public `swiftinterface`, and rebuilt the device + simulator slices.

Re-disassembly (device slice; simulator slice is identical in shape):

```text
_$s13SwiftReproLib16getStruct1DoubleySdAA0eF0VF:
    ret                                   ; identity on d0 — input and return both d0,
                                          ; merged with Struct1Double.init at the same address

_$s13SwiftReproLib17sumStruct2DoublesySdAA0eF0VF:
    fadd  d0, d0, d1                      ; fields a/b consumed directly from d0/d1
    ret

_$s13SwiftReproLib17sumStruct4DoublesySdAA0eF0VF:
    b     _$s13SwiftReproLib13sumNestedRectySdAA0eF0VF   ; tail-call into 4-double sum

_$s13SwiftReproLib13sumNestedRectySdAA0eF0VF:
    fadd  d0, d0, d1                      ; fields consumed directly from d0..d3
    fadd  d0, d0, d2
    fadd  d0, d0, d3
    ret
```

No `[x0]` loads anywhere. Swift consumes fields directly from `d0`–`d3`, exactly like the `CGRect` (imported C struct) case in the original draft.

Re-run output (Mono Sim, iossimulator-arm64, .NET 10.0.3 / Microsoft.iOS.Sdk 26.2.10197):

```
[Issue 6] NativeAOT single-double-field struct returns garbage via CallConvSwift...
  Custom 1 Double (8B): val=42.5 (expected 42.5) — PASS
[Issue 5] NativeAOT places custom-struct float/double fields in GPR instead of FPR...
  Custom 2 Doubles (16B): sum=3 (expected 3) — PASS
  Custom 4 Doubles (32B): sum=10 (expected 10) — PASS
  Custom Nested 2x{2D} (32B, CGRect-shaped): sum=10 (expected 10) — PASS
  System CGRect (32B, control): area=20 (expected 20) — PASS
```

Re-run output (NativeAOT device, iPhone 13 / iOS 26 / ios-arm64, `PublishAot=true`):

```
[Issue 6] NativeAOT single-double-field struct returns garbage via CallConvSwift...
  Custom 1 Double (8B): val=42.5 (expected 42.5) — PASS
[Issue 5] NativeAOT places custom-struct float/double fields in GPR instead of FPR...
  Custom 2 Doubles (16B): sum=3 (expected 3) — PASS
  Custom 4 Doubles (32B): sum=10 (expected 10) — PASS
  Custom Nested 2x{2D} (32B, CGRect-shaped): sum=10 (expected 10) — PASS
  System CGRect (32B, control): area=20 (expected 20) — PASS
```

Both runtimes return the expected values for every previously-failing case. The original "garbage value" / "ABI MISMATCH" symptoms (`val=3.0438774487E-314`, `sum=5.1716891354E-314`, etc.) were entirely the consequence of Swift expecting a pointer in `x0` because the structs were resilient — not a NativeAOT JIT register-class bug. The "asymmetry" the original draft cited ("custom C# struct" vs "system framework HFA") is more accurately "Swift-native resilient struct (`AA…V`)" vs "imported C struct (`So6CGRectV`)" — `CGRect` followed the platform direct-passing path the entire time.

**Verdict:** close. Do not file. The repro now ships with `@frozen` so the resilience mismatch can't recur, and Issue 6 is the minimal-repro subsection of this issue rather than a separate filing.

### Minimal repro subsection (formerly Issue 6): single-`double` parameter case

The simplest reduction passes a one-field `@frozen public struct Struct1Double { public var a: Double }` as a parameter and returns `s.a` as a scalar `Double`. This eliminates any register-splitting ambiguity — both the parameter and the return value live entirely in `d0`. After the `@frozen` rebuild, `getStruct1Double(_:)` is identical to a `Double → Double` identity function (the linker even merges it with `Struct1Double.init(a:)` to the same `ret` instruction). Both runtimes return `42.5` as expected. The Issue 6 narrative ("returns custom struct containing single double field") was inaccurate against the actual repro at `/Users/wojo/Dev/swift-interop-repro/ReproApp/Program.cs` — `getStruct1Double` takes the struct as a *parameter* and returns scalar `Double`, not the other way around.

---

## Issue 6 (Closed — merged into Issue 5)

> **Status: merged into Issue 5 on 2026-04-30.** The "single-`double` field" case is the minimal-repro subsection of Issue 5 (parameter-passing), not a separate return-passing bug. The original Issue 6 narrative (Swift function returning a custom struct) did not match the actual repro source, which takes `Struct1Double` as a *parameter* and returns scalar `Double`. After the `@frozen` rebuild both runtimes return the correct value; do not file.

The historical Issue 6 draft below is retained for audit only — read Issue 5's `2026-04-30 rebuild result` block for the resolved evidence.

### Title (historical)

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

**2026-04-30 fact-check (Codex independent review):** **DO NOT FILE AS A SEPARATE ISSUE.** Two problems with the current draft:

1. The narrative says "the failure is *returning* a custom struct containing a single `double`," but the actual repro at `/Users/wojo/Dev/swift-interop-repro/ReproApp/Program.cs:215` passes the struct as a *parameter* and returns a scalar `double`:
   ```csharp
   private static extern double getStruct1Double(Struct1Double s);
   ```
   ```swift
   public func getStruct1Double(_ s: Struct1Double) -> Double { return s.a }
   ```
   So this is the minimal *parameter*-passing case, not a return-passing case. The Issue 5 vs Issue 6 split is also internally inconsistent: Issue 5 says "custom float struct return passes on NativeAOT" while Issue 6 describes a NativeAOT custom-struct return failure. The current repro supports Issue 5's parameter framing, not a separate return-bug claim.
2. Same `@frozen` contamination as Issue 5 — disassembly shows Swift expects a pointer in `x0`, not the field directly.

**Recommended action:** merge into Issue 5 as the "minimal repro" subsection ("`Struct1Double` parameter — single-field minimal case"). Remove Issue 6 as a separate filing. If a genuine return-passing bug exists for custom float structs on NativeAOT, build a dedicated repro that *returns* a custom struct (e.g. `func makeSingleDouble(_ v: Double) -> SingleDouble`) and re-test after the `@frozen` rebuild before filing.

**2026-04-30 rebuild result:** merge applied. The single-`double` parameter case is now the minimal-repro subsection of Issue 5 (see Issue 5's `2026-04-30 rebuild result` block for the disassembly + Mono Sim + NativeAOT device output). Closed; do not file. No dedicated return-passing repro was built, since the closed Issue 5 (which covers the parameter case definitively) provides no evidence that a separate return-passing bug exists.

---

## Issue 7 (Closed — not a runtime bug): NativeAOT CallConvSwift passes custom integer structs >16 bytes incorrectly on ARM64

> **Status: closed 2026-04-30 after the `@frozen` rebuild.** Same root cause as Issues 5/6 — Swift expected a pointer in `x0` because the structs were resilient. After marking `Struct3Ints` and `Struct4Ints` as `@frozen`, both runtimes returned the correct sums on every previously-failing case. See the `2026-04-30 rebuild result` block at the end of this section. Do not file.

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

`CGRect` (4 doubles, 32B) is **not affected** because it is `So6CGRectV` — an imported C struct on the platform direct-passing path. The original draft framed this as "system framework type vs custom C# struct" but the actual asymmetry is "imported C struct vs Swift-native resilient struct." After the 2026-04-30 `@frozen` rebuild the Swift-native structs use direct register passing too and the symptoms disappear; see the closure note at the top of this section.

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

**2026-04-30 fact-check (Codex independent review + direct disassembly):** **BLOCKED on Swift `@frozen` rebuild.** Same root cause as Issues 5/6. Disassembly of the repro framework:

```text
_$s13SwiftReproLib14sumStruct3IntsySiAA0eF0VF:
    ldp x8, x9, [x0]
    ...
    ldr x9, [x0, #0x10]

_$s13SwiftReproLib14sumStruct4IntsySiAA0eF0VF:
    ldp x8, x9, [x0]
    ...
    ldr x9, [x0, #0x10]
    ldr x9, [x0, #0x18]
```

Swift expects `x0` to be a *pointer* into struct memory, not the first field directly. This is the resilient-struct ABI of `-enable-library-evolution` + non-`@frozen`. If .NET passes the first field (an integer) directly in `x0`, Swift dereferences that integer as an address and SIGSEGVs.

The "AAPCS64 indirect-passing fallback" hypothesis in the draft is not what's happening — Swift itself is *requiring* indirect passing because the struct has resilient layout. NativeAOT/Mono's `SwiftPhysicalLowering` would in theory lower 3×`LONG` / 4×`LONG` to direct GPR slots (the "by reference" cutoff is `loweredTypes.Count > 4`, so 3 and 4 elements stay direct), but that's the wrong ABI for this particular Swift binary.

I also did not find a Mono 24B-vs-32B threshold in the marshal.c snapshot. The check at `marshal.c:7012` flags by-reference only when `vtype_size > 4 * TARGET_SIZEOF_VOID_P` (i.e. > 32 bytes), and the lowered-elements cap trips only when adding the fifth (`marshal.c:7079, 7122`). A 24-byte 3×`Int` struct should be lowered identically to a 32-byte 4×`Int` struct on Mono. The 32B Mono SIGSEGV is consistent with .NET passing fields directly while Swift expects a pointer; the 24B Mono row is still untested.

**Required before filing:** mark `Struct3Ints` and `Struct4Ints` as `@frozen`, rebuild, re-disassemble (expecting `mov x8, x0; mov x9, x1; …` direct-GPR consumption), and re-test on NativeAOT device + Mono Sim. If the rebuilt binary uses GPRs directly and the runtimes still SIGSEGV, the original framing stands. If it passes after the rebuild, this is a documentation/support question about resilient struct support, not a runtime bug.

**2026-04-30 rebuild result (Mono Sim + NativeAOT device, post-`@frozen`):** **CLOSE — not a runtime bug.** Marked `Struct3Ints` and `Struct4Ints` as `@frozen` (keeping `-enable-library-evolution`), rebuilt, and confirmed the public `swiftinterface` carries `@frozen public struct`.

Re-disassembly (device slice; simulator slice is identical in shape):

```text
_$s13SwiftReproLib14sumStruct3IntsySiAA0eF0VF:
    adds  x8, x0, x1            ; fields a/b consumed directly from x0/x1
    b.vs  <overflow-trap>
    adds  x0, x8, x2            ; field c from x2
    b.vs  <overflow-trap>
    ret

_$s13SwiftReproLib14sumStruct4IntsySiAA0eF0VF:
    adds  x8, x0, x1            ; fields a/b/c/d consumed directly from x0..x3
    adds  x8, x8, x2
    adds  x0, x8, x3
    ret
```

Direct-GPR consumption, exactly as predicted. No `[x0]` loads.

Re-run output (Mono Sim, `iossimulator-arm64`, .NET 10.0.3):

```
[Issue 7] Custom integer struct >16B passed by pointer instead of in registers...
  Struct3Ints (24B): sum=60 (expected 60) — PASS
  Struct4Ints (32B): sum=10 (expected 10) — PASS
```

Re-run output (NativeAOT device, iPhone 13 / iOS 26 / `ios-arm64`, `PublishAot=true`):

```
[Issue 7] Custom integer struct >16B passed by pointer instead of in registers...
  Struct3Ints (24B): sum=60 (expected 60) — PASS
  Struct4Ints (32B): sum=10 (expected 10) — PASS
```

`sumStruct4Ints` (the 32B case) was the symptom that previously SIGSEGVed both runtimes — under `@frozen` it returns the correct sum on both. The 24B `sumStruct3Ints` row, which the original verification listed as "not tested" on Mono Sim because Issue 9's earlier crash masked it, also passes (the repro app's test ordering was tightened in this rebuild so the @frozen-affected tests run before the still-crashing Issue 9). The "AAPCS64 indirect-passing fallback at >16B" hypothesis was wrong in two ways: Mono's marshal.c doesn't have that threshold (`vtype_size > 4 * TARGET_SIZEOF_VOID_P` cuts at 32B and the lowered-elements cap trips at 5+), and Swift was forcing indirect passing because the struct had resilient layout — not because .NET fell back to AAPCS.

**Verdict:** close. Do not file. Same root cause as Issues 5/6.

---

## Issue 8 (Closed — wrong P/Invoke shape on our side, not a runtime bug)

> **2026-04-30 closure:** disassembly of `_$s13SwiftReproLib11genericPairyx_q_tx_q_tr0_lF` showed Swift's multi-result tuple return uses `(x0=res1, x1=res2, x2=pay1, x3=pay2, x4=Tmeta, x5=Umeta)`, not `(x8=SwiftIndirectResult, x0=pay1, x1=Tmeta, x2=Umeta, …)` as the original draft assumed. The SIGSEGV the repro produced was the consequence of the wrong P/Invoke shape, not a NativeAOT register-placement bug. Generator was updated the same day to emit the correct N-`@out`-register shape for fully bare-generic multi-element tuple returns; `BasicGenericTests.TestGetPairSameType` and heterogeneous-/class-element variants now pass on Mono Sim and NativeAOT Device. Mixed shapes (`(T, Int)`, `(Array<T>, T)`, …) remain on the legacy path — see `roadmap.md` *Lower Priority*. The original draft narrative is preserved below for context; **do not file** as drafted.

**Original draft (historical, do not file):**

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

**2026-04-30 fact-check (Codex independent review + direct disassembly):** **DO NOT FILE.** This is not a NativeAOT register-placement bug — it's a wrong P/Invoke shape on our side.

Disassembly of the actual repro framework:

```text
_$s13SwiftReproLib15genericIdentityyxxlF:        ; single-result, x8 = SwiftIndirectResult
  mov x2, x1     ; x2 = T metadata (was x1)
  mov x1, x0     ; x1 = payload (was x0)
  mov x0, x8     ; x0 = result pointer (was SwiftIndirectResult)
  ldur x8, [x2, #-0x8]  ; VWT load from T metadata
  ...

_$s13SwiftReproLib11genericPairyx_q_tx_q_tr0_lF:  ; multi-result, NO x8
  mov x19, x5    ; x5 = U metadata
  mov x20, x3    ; x3 = second payload
  mov x21, x1    ; x1 = second result destination
  ldur x8, [x4, #-0x8]   ; VWT load from T metadata = x4
  ...
  ; first result destination was x0
  ; first payload was x2
```

Swift's tuple-return convention for `genericPair<T, U>(_, _) -> (T, U)` puts each result destination in a normal GPR (`x0`, `x1`) and shifts payloads + metadata down by two registers. There is no `x8`/`SwiftIndirectResult`. The corresponding LLVM IR confirms:

```text
genericIdentity:
  swiftcc void (ptr noalias sret(%swift.opaque) %0, ptr noalias %1, ptr %T)

genericPair:
  swiftcc void (ptr noalias %0, ptr noalias %1, ptr noalias %2, ptr noalias %3, ptr %T, ptr %U)
```

The current draft's P/Invoke for `genericPair` declares `(SwiftIndirectResult result, IntPtr first, IntPtr second, IntPtr TMetadata, IntPtr UMetadata)` which lowers to `(x8=result, x0=first, x1=second, x2=TMeta, x3=UMeta)`. Swift then reads `x4`/`x5` as the metadata pointers, sees garbage (whatever was caller-saved), and SIGSEGVs in the VWT load. That's not a runtime bug; the C# shape doesn't match the Swift ABI.

A corrected `genericPair` P/Invoke would be:

```csharp
[UnmanagedCallConv(CallConvs = [typeof(CallConvSwift)])]
[DllImport("SwiftReproLib", EntryPoint = "$s13SwiftReproLib11genericPairyx_q_tx_q_tr0_lF")]
private static extern void genericPair(
    IntPtr firstResult,    // x0
    IntPtr secondResult,   // x1
    IntPtr firstPayload,   // x2
    IntPtr secondPayload,  // x3
    IntPtr TMetadata,      // x4
    IntPtr UMetadata);     // x5
```

**Recommended action:** close Issue 8. If the corrected-shape repro still crashes, file a new issue with that as the minimal repro.

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

| Issue | Runtime | Status (post 2026-04-30 fact-check) | Summary |
|-------|---------|--------------------------------------|---------|
| 1 | Mono | **File** | JIT assertion `!ji->async` during signal-handler unwinding through a `wrapper_managed_to_native_*` frame after a native crash in a `CallConvSwift` callee |
| 2 | Both | **File** (with source-pointer corrections) | Non-blittable parameter types rejected — primary driver of ~67% wrapper rate |
| 3 | Mono | **Comment, not bug** | `SwiftSelf<SafeHandle>` lifetime across async P/Invoke — likely outside documented `SafeHandle` P/Invoke semantics |
| 5 (with Issue 6 merged) | NativeAOT + Mono | **Close** (resilience mismatch, not a runtime bug) | After the 2026-04-30 `@frozen` rebuild every "garbage value / ABI MISMATCH" symptom resolved on both Mono Sim and NativeAOT device. The repro structs were resilient under `-enable-library-evolution`, so Swift took a pointer in `x0` and read fields via `ldr d0, [x0]` / `ldp d0, d1, [x0]`; .NET correctly passed the first field directly. `CGRect` worked because it's `So6CGRectV` (imported C struct on the direct-passing path). Issue 6 is the single-`double` minimal-repro subsection of Issue 5. |
| 7 | NativeAOT + Mono | **Close** (resilience mismatch, not a runtime bug) | Same root cause as 5/6. Post-rebuild disassembly shows direct-GPR consumption (`adds x8, x0, x1; …`) and both `Struct3Ints` (24B) and `Struct4Ints` (32B) sums return correctly on both runtimes. The "AAPCS64 indirect fallback at >16B" hypothesis was wrong — Mono's by-reference cutoff is 32B and the lowered-elements cap is 4. |
| 8 | NativeAOT | **Close** | Wrong P/Invoke shape on our side. Multi-result tuple returns use `(x0=res1, x1=res2, x2=pay1, x3=pay2, x4=Tmeta, x5=Umeta)`, not `(x8=SwiftIndirectResult, x0=pay1, x1=Tmeta, x2=Umeta, …)`. With the corrected shape, no SIGSEGV is expected. |
| 9 | Mono | **File** (soft framing on mechanism) | `Cannot transition thread from STARTING with DONE_BLOCKING` on `(Bool direct, @out via x0)` tuple-return ABI. ABI verified empirically; precise corruption mechanism inside Mono's trampoline not pinned down. |

After the 2026-04-30 fact-check **and the 2026-04-30 `@frozen` rebuild**, three issues are filed as new dotnet/runtime issues — Issue 1 (bug), Issue 2 (feature request), Issue 9 (bug). Issue 3 is posted as a tracking-issue comment. Four are closed without filing (5, 6, 7, 8). Issue 2 (non-blittable support) remains the highest impact — it drives the ~67% wrapper rate alone.

All active issues have workarounds in production use (`@_cdecl` Swift wrapper functions using `CallingConvention.Cdecl`), but the workarounds add significant complexity (per-type/per-method Swift wrapper generation, wrapper xcframework bundling, manual marshalling).

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

The return `(Bool, @out T)` is NOT handled via `x8`/`SwiftIndirectResult`. Instead, the `@out T` buffer pointer goes in `x0` and the direct `Bool` result is returned in `x0` after the call returns — the same register is reused for the inbound out-pointer argument and the outbound scalar result. The `(Bool direct, @out via x0)` shape correlates uniquely with the failure: `Set.contains` (no `@out`, single-direct return) and `Dictionary.updateValue` (uses `x8`/`SwiftIndirectResult`) both pass on Mono with `CallConvSwift`. The corruption mechanism inside Mono's `CallConvSwift` managed-to-native trampoline is hypothesized but not pinned down — see "Verification scope" below.

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
- Verified on 2026-04-30 (.NET 10.0.103, Mono iOS Simulator arm64, Xcode 26.2)
- Related to Issue 1 (`!ji->async`) and the general pattern of Mono not handling non-standard CallConvSwift return ABIs
- Not reproduced on NativeAOT (device) — needs separate verification
- Priority: high for `SwiftSet<T>` correctness in swift-dotnet-bindings

**Verification scope (2026-04-30):**

- **ABI shape — verified.** Direct disassembly of `libswiftCore.dylib` (arm64 simulator slice) and the SIL signature confirm `Set.insert` takes `(x0=@out T*, x1=@in T*, x2=Set<T> metadata, x20=@inout Set<T> self via Swift context register)` and returns `Bool` in `w0`/`x0`, reusing `x0` for the inbound `@out` pointer and the outbound scalar.
- **P/Invoke shape match — verified.** Our `Insert(IntPtr outMemberBuffer, IntPtr element, IntPtr setMetadata, SwiftSelf self) -> byte` lowers to `(x0, x1, x2, x20) → x0` per Mono's `SwiftSelf → ARMREG_R20` mapping at `mini-arm64.c:~1927`. It matches the Swift ABI.
- **Failure correlates with shape — verified.** `Set.contains` (no `@out`, single-direct return) and `Dictionary.updateValue` (uses `x8`/`SwiftIndirectResult` for the indirect result) both pass on Mono with `CallConvSwift`. The unique failing shape is `(Bool direct, @out via x0)`.
- **Root cause inside Mono's trampoline — hypothesized, not pinned down.** Reviewed `marshal.c`, `marshal-lightweight.c`, `mini-arm64.c`, `mono-threads-state-machine.c`, `mono-threads.c`. The IL stub uses `mono_threads_enter_gc_safe_region_unbalanced` / `mono_threads_exit_gc_safe_region_unbalanced` brackets; `mono_threads_transition_done_blocking` (`state-machine.c:772`) only accepts `STATE_BLOCKING` / `STATE_BLOCKING_SUSPEND_REQUESTED`; `STATE_STARTING == 0` (`mono-threads.h:146`). The "thread `0x0` from STARTING" wording is consistent with a zeroed `MonoThreadInfo*` or a state field reading zero, but the exact path from the `(Bool direct, @out via x0)` shape to that zeroed state was not isolated. A reviewer with a local Mono build can instrument the trampoline directly.
