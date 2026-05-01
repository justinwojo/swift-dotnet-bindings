# [Mono] JIT assertion `!ji->async` at `jit-info.c:918` during signal-handler unwind through a `CallConvSwift` frame after a native crash

> Standalone bug report for filing against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings). Repro: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro). Contact: Justin Wojciechowski.

## Title

`[Mono] JIT assertion "!ji->async" at jit-info.c:918 during signal-handler unwind through a CallConvSwift frame after a native crash`

## Labels

`area-Interop-Swift`, `os-ios`, `os-maccatalyst`, `bug`, `runtime-mono`

## Description

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

The triggering pattern is **any** Swift function called via `CallConvSwift` that crashes natively. Examples observed in the wider swift-bindings test suite that hit the same assertion include `Set<T>.insert` via `CallConvSwift` (separate Mono bug — see companion Issue 3 `[Mono] DONE_BLOCKING on (Bool direct, @out via x0) tuple-return`), and any `@_cdecl` Swift wrapper that dereferences a stale buffer pointer.

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

**Verified on 2026-04-30** with .NET SDK 10.0.103, Microsoft.iOS.Sdk 26.2.10197, Mono iOS Simulator runtime 26.3, Xcode 26.2 (build 17C52). The deliberately-crashing `CallConvSwift` callee in `Issue1_MonoSignalHandlerAssert` reliably triggers the assertion. An earlier framing of this issue claimed a synchronous direct call to `swift_getExistentialTypeMetadata` was the trigger; that direct call now returns successfully on Mono with .NET 10.0.103, so only the unwind path through a crashing `CallConvSwift` frame still reproduces. Verification scope: ABI symbol confirmed via `nm`; assertion path confirmed by signal-handler stack trace matching `mono/metadata/jit-info.c:918`. The frame-async classification path inside Mono has not been stepped through with a custom Mono build; the reviewer can do so locally to confirm whether the wrapper synthesis or the consumer of `MonoJitInfo->async` is the right fix layer.
