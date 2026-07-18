# [Mono] arm64 exception-unwinder EXC_BAD_ACCESS (pointer-auth failure) when `OperationCanceledException` unwinds out of a canceled shared-reference-generic `Task<T>` on a UIKit synchronization-context continuation

> Standalone bug report for filing against [dotnet/runtime](https://github.com/dotnet/runtime/issues). Project: [swift-dotnet-bindings](https://github.com/justinwojo/swift-dotnet-bindings). Repro: [swift-interop-repro](https://github.com/justinwojo/swift-interop-repro) *(dedicated reduction pending — see Minimal reproduction)*. Contact: Justin Wojciechowski.

## Title

`[Mono] arm64 exception unwinder faults (EXC_BAD_ACCESS, PAC failure) at mono_arch_unwind_frame when OperationCanceledException unwinds through TaskAwaiter<T>.GetResult on a synchronization-context continuation`

## Labels

`area-Interop-Swift`, `os-ios`, `bug`, `runtime-mono`

## Description

**Environment:**
- .NET 10.0 (SDK 10.0.107), Mono runtime (iOS Simulator)
- macOS 26.2, iOS 26.2 Simulator, arm64 (Apple Silicon)
- Xcode 26.3 (build 17C529), iOS Simulator SDK 26.2, Swift 6.2.x
- iOS Simulator runtime: iPhone booted simulator, Mono JIT
- Reproduced in: swift-dotnet-bindings BindingTests, `PatParentAsyncMethodsTests.TestAsyncBagMock{String,Int}Item_CancelRespondAsyncSurfacesCancellation` (a dedicated `swift-interop-repro` reduction is pending — this filing currently relies on BindingTests evidence plus a pure-managed in-suite reduction)

**Summary:**

On the Mono iOS-Simulator runtime, throwing an `OperationCanceledException` out of an already-canceled, shared **reference-type** generic `Task<T>` — awaited via a generic async helper whose continuation resumes on the UIKit main-thread synchronization context (`NSAsyncSynchronizationContextDispatcher`) — faults inside Mono's exception unwinder. The crash is `EXC_BAD_ACCESS` / `SIGSEGV` with a **fixed** pointer-authentication-tagged fault address, raised from `mono_arch_unwind_frame` → `mono_find_jit_info_ext` while `mono_handle_exception` walks the stack for the throw.

The throw itself is ordinary, legal managed code: `await task` where `task.IsCanceled` is true, which the runtime surfaces via `TaskAwaiter<TResult>.ThrowForNonSuccess` → `throw`. There is **no** Swift, P/Invoke, or generated-interop frame anywhere on the faulting stack — the fault is entirely within Mono's unwinder walking managed frames.

The bug is **layout- and load-sensitive** (a heisenbug): it reproduces only under accumulated full-suite JIT load (hundreds of prior test methods JIT-compiled), and adding or removing *unrelated* sibling methods in the same class relocates the JIT code and makes the crash appear or disappear. It does **not** reproduce when the same test class runs in single-class isolation.

**Crash signature (from `RuntimeTestsApp-2026-07-18-161937.ips`; identical address on repeated crashes today, incl. session-7 reports `-114458`/`-114845`):**

```
Exception Type:  EXC_BAD_ACCESS (SIGSEGV)
Exception Codes: KERN_INVALID_ADDRESS at 0x038c1a03881a036c
                 -> 0xffff9a03881a036c (possible pointer authentication failure)
Triggered thread: 0 (main thread)
```

The fault address is **stable across every observed crash**, which indicates a fixed bad-pointer read in the unwinder rather than random use-after-free scribbling.

**Faulting stack (thread 0, abbreviated):**

```
libmonosgen  mono_arch_unwind_frame
libmonosgen  mono_find_jit_info_ext
libmonosgen  mono_handle_exception_internal
libmonosgen  mono_handle_exception
libmonosgen  mono_arm_throw_exception
RuntimeTestsApp  throw_exception
System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task)
System.Runtime.CompilerServices.TaskAwaiter`1[TResult_REF].GetResult()
RuntimeTestsApp.Infrastructure.TestBase.<WithTimeout>d__22`1[T_REF].MoveNext()
... AsyncTaskMethodBuilder / ExecutionContext.RunInternal ...
System.Threading.Tasks.SynchronizationContextAwaitTaskContinuation.<>c.<.cctor>b__8_0(object)
Foundation.NSAsyncSynchronizationContextDispatcher.Apply()
native_to_managed_trampoline -> NSThreadPerformPerform -> CFRunLoop (main thread)
```

Note `TResult_REF` / `T_REF`: the awaiter and helper are the **shared reference-type generic** instantiations (the two application types that surface the bug, `StringResponse`/`IntResponse`, are both classes and thus share one generic code body).

**Minimal reproduction:**

The essential shape, with **no Swift and no interop** on the crashing path — a pure-managed reduction that was run *in place* inside the failing suite (replacing the interop call with a canceled `TaskCompletionSource`) and **crashed identically**:

```csharp
// Generic timeout helper — the continuation of the inner `await task`
// resumes on the captured (UIKit main-thread) synchronization context.
static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout)
{
    var timeoutTask = Task.Delay(timeout);
    if (await Task.WhenAny(task, timeoutTask) == timeoutTask)
        throw new TimeoutException();
    return await task;          // <-- rethrows OCE here; Mono unwinder faults
}

// A reference-type result so the shared TResult_REF generic body is used.
sealed class Payload { }

async Task Repro()
{
    var tcs = new TaskCompletionSource<Payload>(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = Task.Run(() => tcs.TrySetCanceled(default));   // cancel from the thread pool

    try { await WithTimeout(tcs.Task, TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException) { /* expected */ }
}
```

Reproduction conditions that matter:
1. iOS Simulator, arm64, **Mono JIT**.
2. The `await` continuation resumes on the UIKit **main-thread** synchronization context (`NSAsyncSynchronizationContextDispatcher`) — i.e. the awaiting method was entered on the main thread with the UIKit `SynchronizationContext` installed.
3. The awaited `Task<T>` is **canceled** (not faulted with an ordinary exception) and `T` is a **reference type** (shared generic code).
4. Executed **after sufficient prior JIT load** — it does not reproduce cold / in isolation.

**Discriminating evidence that it is not the interop layer:**

| Variant | Result |
|---|---|
| Real interop test: Swift `CancellationError` → `@_cdecl` error callback → `TrySetCanceled` → `await` | **Crash** |
| Same test, Swift call swapped for a pure-managed canceled `TaskCompletionSource<Payload>` (Swift entirely removed from the await/throw path), everything else identical | **Crash (identical test, position, address)** |
| Same class run in single-class isolation (`--class-filter`) | Pass |
| Same suite with two *unrelated* sibling methods added to the class (JIT layout shifted) | Pass (crash vanishes) |
| Sibling **non-generic** `Task` (void) throwing `OperationCanceledException` under the same load | Pass |
| Same generic `Task<T>` **success** path (no throw) under the same load | Pass |

The distinguishing axis is **exception propagation out of a shared reference-generic `Task<T>` on a sync-context continuation**, not cancellation bridging or any interop ABI. Calling convention, parameter count/types, entry-point symbol, and `RunContinuationsAsynchronously` were all verified correct on the interop path; the Swift error callback has fully returned (the `Task` is already `Canceled`) before the fault.

**Relationship to already-filed issues:**

Distinct from Issues 1–4. Issue 1 (`!ji->async` at `jit-info.c:918`) is a `g_assert` **abort** during a *signal-handler* unwind through a `CallConvSwift` `wrapper_managed_to_native_*` frame after a native crash; this is a **hard memory fault** (`EXC_BAD_ACCESS`/PAC) in `mono_arch_unwind_frame` during a *normal managed throw*, with no `CallConvSwift` frame involved. Issue 4 is maccatalyst-x64-specific; this is ios-simulator arm64.

**Root-cause hypothesis (for the runtime team):**

The unwinder reads a corrupt/misauthenticated saved return address (or a stale `MonoJitInfo`) while walking the shared-generic `TaskAwaiter<TResult_REF>.GetResult` / async-state-machine frames during the OCE throw, on the specific code path where the continuation was dispatched through `NSAsyncSynchronizationContextDispatcher`. The fixed PAC-tagged fault address and the JIT-layout sensitivity point at either an incorrect unwind descriptor for a shared-generic frame or a PAC-signing/authentication mismatch on a saved LR in that frame under arm64e-style pointer signing.

**Impact:**

Any .NET-on-Mono iOS app that awaits a canceled reference-typed `Task<T>` on the UIKit synchronization context can crash under load, independent of Swift interop. NativeAOT (device) uses a different unwinder and is not expected to reproduce.

**Expected behavior:**

Throwing `OperationCanceledException` out of a canceled `Task<T>` must unwind normally regardless of generic sharing, prior JIT load, or JIT code placement; `mono_arch_unwind_frame` must not fault authenticating a saved pointer for a shared-generic async frame.

**Verified on 2026-07-18** with .NET SDK 10.0.107, iOS Simulator (Mono JIT), Xcode 26.3 (build 17C529), iOS Simulator SDK 26.2, macOS 26.2, Apple Silicon arm64. Reproduced deterministically (given fixed JIT layout) across multiple full-suite BindingTests runs; the pure-managed in-suite reduction above crashes identically to the interop test. Independent triage by two external code-review tools (Codex session `019f771f-80bd-79b2-bba5-792584018123`, Grok session `019f7725-3b54-7b90-b98b-19a4c5f6b5bb`) concurred: not an interop/ABI defect; upstream Mono exception-unwinder fault. **A standalone `swift-interop-repro` reduction (pure-managed, no Swift) should be authored before filing** so the runtime team can reproduce without the binding harness.
