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

## Observed variant — void `Task` faulted by a real thrown error (not `OperationCanceledException`)

A second cell of the same unwinder family was observed on 2026-07-21 in BindingTests `PatParentAsyncVoidMethodsTests.TestStringDonator_DonateOrThrowAsync_VoidThrowingErrorPath`, and is handled the same way (`[SkipOnMonoJit]`, Mono-sim only; macOS CoreCLR + device NativeAOT keep running it). It differs from the primary report above along two axes but shares the identical faulting mechanism:

- **Task shape:** a **non-generic** `Task` (void), not `Task<T>`. The awaited task is faulted by a **real thrown error** — a Swift error surfaced as a managed exception via the `@_cdecl` error callback → `TrySetException` — **not** an `OperationCanceledException`/cancel. This is a *different* cell from the "void + OCE cancel → Pass" row in the discriminating-evidence table above, which covers void **cancellation**, not a void **thrown error**.
- **Fault signature:** SIGSEGV inside `mono_arch_unwind_frame` accompanied by the fatal assertion `should not be reached` at `mono/mini/mini-exceptions.c:488` — an **IP-fault + unreachable-unwinder-state** signature, *not* the fixed PAC-tagged bad-address `0x038c1a03881a036c` of the generic-`Task<T>` OCE case.

Everything else matches the primary report: the exception is rethrown by `TaskAwaiter.GetResult` inside the `TestBase.WithTimeout` helper on the main-thread `NSAsyncSynchronizationContext` continuation; the faulting stack carries **zero Swift / generated-binding / P-Invoke frames** (the `CallConvCdecl` reverse-P/Invoke error callback has already returned and the `Task` is faulted before the crash); and it is a full-suite-load-only heisenbug — observed once (~1 in 14 full-suite runs), it did **not** recur across a focused 12-run full-suite soak, and it passes in single-class isolation.

**Independent of the generator change it was first seen on.** The crash surfaced during a run that had merged an unrelated ingestion-ledger generator change. A byte-for-byte diff of the BindingTests generated output (C# + wrapper Swift) between that generator and its immediate predecessor, fed the identical input xcframework, is **identical on the entire crash path** — `DonateOrThrowAsync`'s generated C#, its P/Invoke, its error callback, and the Swift `@_cdecl` wrapper are all unchanged (the only output deltas are four cosmetic operator-operand-qualifier changes on unrelated comparison types, which produce identical IL). The generator change therefore cannot have introduced or altered this crash.

**Belt-and-suspenders pure-managed substitution NOT run.** With 0 reproductions in a 12-run soak, an in-suite A/B substitution (swap the interop call for a pure-managed void `Task` faulted by a plain exception) is uninformative — at this rate a non-crash proves nothing. Attribution rests on the directly-observed zero-Swift-frame faulting stack, the byte-diff independence, ABI verification (`CallConvCdecl` signature match between the generated C# P/Invoke and the Swift wrapper), and independent triage (Grok concurred: Mono EH-unwinder fault, not a binding/ABI defect). A standalone `swift-interop-repro` reduction for this void-error cell remains to be authored alongside the OCE one before filing.

**Expected behavior:**

Throwing `OperationCanceledException` out of a canceled `Task<T>` must unwind normally regardless of generic sharing, prior JIT load, or JIT code placement; `mono_arch_unwind_frame` must not fault authenticating a saved pointer for a shared-generic async frame.

**Verified on 2026-07-18** with .NET SDK 10.0.107, iOS Simulator (Mono JIT), Xcode 26.3 (build 17C529), iOS Simulator SDK 26.2, macOS 26.2, Apple Silicon arm64. Reproduced deterministically (given fixed JIT layout) across multiple full-suite BindingTests runs; the pure-managed in-suite reduction above crashes identically to the interop test. Independent triage by two external code-review tools (Codex session `019f771f-80bd-79b2-bba5-792584018123`, Grok session `019f7725-3b54-7b90-b98b-19a4c5f6b5bb`) concurred: not an interop/ABI defect; upstream Mono exception-unwinder fault. **A standalone `swift-interop-repro` reduction (pure-managed, no Swift) should be authored before filing** so the runtime team can reproduce without the binding harness.

## Observed variant — void `Task` + real thrown error (2026-07-21)

A second cell of the same unwinder family surfaced in `PatParentAsyncVoidMethodsTests.TestStringDonator_DonateOrThrowAsync_VoidThrowingErrorPath` (BindingTests, iOS Simulator, Mono JIT). It shares this issue's mechanism — the fault is in Mono's exception unwinder walking managed frames while an exception is rethrown by `TaskAwaiter.GetResult` inside the `WithTimeout` helper, resumed on the main-thread `NSAsyncSynchronizationContext` continuation, with **zero Swift / generated-binding / P-Invoke frames on the faulting stack** (the `CallConvCdecl` error callback has already returned and the `Task` is faulted before the crash) — but differs from the OCE case above on three axes:

| Axis | OCE case (above) | Void-error variant |
|---|---|---|
| Faulting `Task` | generic `Task<TResult_REF>` (reference-type result, shared generic body) | **non-generic `Task` (void)** |
| Thrown exception | `OperationCanceledException` (task `Canceled`) | **a real thrown error** (task `Faulted` via `TrySetException` from a marshaled Swift error), rethrown through `TaskAwaiter.ThrowForNonSuccess` |
| Crash signature | `EXC_BAD_ACCESS` at the **fixed PAC-tagged address** `0x038c1a03881a036c` | **SIGSEGV in `mono_arch_unwind_frame` with fatal assertion `should not be reached` at `mini-exceptions.c:488`** (an IP-fault at the unwinder's unreachable-state guard, not the fixed PAC address) |

The top-of-stack chain is otherwise identical: `mono_arch_unwind_frame` ← `mono_find_jit_info_ext` ← `mono_handle_exception_internal` ← `mono_arm_throw_exception` ← `throw_exception` ← `ExceptionDispatchInfo.Throw` ← `TaskAwaiter.GetResult` ← `TestBase.WithTimeout.MoveNext` ← `SynchronizationContextAwaitTaskContinuation` ← `NSAsyncSynchronizationContextDispatcher.Apply`. The original OCE-case discriminating table lists "non-generic `Task` (void) throwing `OperationCanceledException` under the same load → Pass"; this variant shows that the void `Task` throwing a **real error** (rather than a cancellation) is *not* immune — it hits the same unwinder in a distinct failure mode.

**Load/rate:** full-suite-load-only heisenbug, same as the OCE case — observed once, then **0 recurrences in a focused 12-run full-suite soak** (the test itself passed every soak run); estimated ~1/14 full-suite runs.

**Independence from the S07 generator change:** the crash was first seen right after merging the S07 ingestion-ledger work, but a byte-diff of the BindingTests generated output (generator at `1e3b0645` vs pre-S07 `2d3f16c1`, identical input xcframework and args) proved the crash-path generated C# (`Donator.cs` — `DonateOrThrowAsync` P/Invoke + `OnError` callback) and the wrapper `.swift` (`donateOrThrow` `@_cdecl`) are **byte-identical** across the two generators; the only output deltas are four cosmetic synthesized-operator operand-type qualifications on unrelated comparison types (identical IL). So the variant is a pre-existing latent unwinder heisenbug, not an S07 regression. The generated `@_cdecl`↔`LibraryImport` ABI was verified matching (`CallConvCdecl` throughout).

**In-tree handling:** `[SkipOnMonoJit(...)]` on `PatParentAsyncVoidMethodsTests.TestStringDonator_DonateOrThrowAsync_VoidThrowingErrorPath` (Mono-sim only; macOS CoreCLR + device NativeAOT keep running it), mirroring the OCE siblings above. Independent triage by Grok concurred: fault is in the Mono EH unwinder, not a binding ABI/CC defect (the `mini-exceptions.c:488 "should not be reached"` guard = the unwinder reaching an unreachable/corrupt frame-walk state), and the fix must not be to alter the calling convention or the async error callback.

**Belt-and-suspenders pure-managed substitution was NOT run for this variant** — with 0 reproductions in 12 focused soak runs, an in-suite A/B (swap the interop call for a pure-managed faulted void `Task`) is uninformative, because a non-crash proves nothing at this reproduction rate. The attribution rests instead on the directly-observed zero-Swift-frame faulting stack, the byte-diff generator exoneration, and the shared mechanism with the OCE case. A standalone pure-managed reduction for this variant remains to be authored alongside the OCE one before filing.
