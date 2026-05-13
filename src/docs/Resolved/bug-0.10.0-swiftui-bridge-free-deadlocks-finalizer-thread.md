# Bug: SwiftUI bridge `_Free` deadlocks against `GC.WaitForPendingFinalizers` on the Mono finalizer thread

> SDK 0.10.0 generator correctness bug. Discovered 2026-05-05 during
> Session 5's BindingTests backfill, surfaced as `[SkipOnSimulator]` on
> `BridgeSimpleViewTests.TestClassParamViewSessionFinalizerReleasesNativeHandle`
> and resolved in Session 8.

## Summary

The SwiftUI bridge `_Free` function (e.g.
`SBW_SwiftBindingsTestLib_ClassParamView_Free`) routed every release
through the shared `SBW_onMainThread { … }` helper, whose body is
`DispatchQueue.main.sync`. Reaching the cleanup from the .NET finalizer
thread while the test main thread is parked inside
`GC.WaitForPendingFinalizers()` produces a three-way deadlock:

1. Main thread is blocked in `GC.WaitForPendingFinalizers()` waiting for
   the finalizer thread to drain.
2. Finalizer thread enters the session's `Dispose(disposing: false)`,
   calls native `_Free`, which calls `SBW_onMainThread`, which calls
   `DispatchQueue.main.sync` and blocks waiting for the main queue to
   drain.
3. The main queue cannot drain because main is blocked in step 1.

The simulator runner detects the missing test terminator and kills the
app silently — no JIT assertion, no crash log, no exception text. The
device path passes because NativeAOT pumps the run loop while waiting
for finalizers (the `DispatchQueue.main.sync` resolves before the
finalizer thread re-enters Mono).

## Repro

Pre-fix, `nuke binding-tests --sim --class-filter BridgeSimpleViewTests`
hangs on the finalizer test until the runner's no-output timeout, then
the runner reports the whole class missing. The crash signature:

```
[TEST] --- BridgeSimpleViewTests.TestClassParamViewSessionFinalizerReleasesNativeHandle ---
<no further output, app killed by runner>
```

On device the same test runs in ~290ms and asserts `deinitCount == 1`.

## Root cause

`SwiftUIBridgeEmitter.cs:1005-1024` (pre-fix) emitted `_Free` as:

```swift
@_cdecl("SBW_<Module>_<View>_Free")
public func SBW_<Module>_<View>_Free(_ handle: UnsafeMutableRawPointer?) {
    SBW_onMainThread {
        guard let handle = handle,
              SBW_<…>_liveHandles.remove(handle) != nil else { return }
        Unmanaged<…_Session>.fromOpaque(handle).release()
    }
}
```

`SBW_onMainThread` is:

```swift
func SBW_onMainThread<T>(_ block: () -> T) -> T {
    if Thread.isMainThread { return block() }
    return DispatchQueue.main.sync { block() }
}
```

The `.sync` is correct for the bridge's create / getter / update paths
(those run on a known caller thread that is willing to wait for main).
It is fatal for `_Free`, which is the only entry point reachable from
the Mono finalizer thread, and the only entry point that the main
thread waits on synchronously (via `GC.WaitForPendingFinalizers()`).

## Fix

Emit `_Free` with a thread-aware dispatch instead of always going
through `SBW_onMainThread`:

```swift
@_cdecl("SBW_<Module>_<View>_Free")
public func SBW_<Module>_<View>_Free(_ handle: UnsafeMutableRawPointer?) {
    let release: () -> Void = {
        guard let handle = handle,
              SBW_<…>_liveHandles.remove(handle) != nil else { return }
        Unmanaged<…_Session>.fromOpaque(handle).release()
    }
    if Thread.isMainThread { release() }
    else { DispatchQueue.main.async(execute: release) }
}
```

The on-main path (explicit `Dispose()` from a `using` / SwiftUI's own
teardown) still runs inline and preserves the synchronous-cleanup
contract. The off-main path (the .NET finalizer thread, or any future
caller off-main) dispatches async so the finalizer thread returns
immediately and `GC.WaitForPendingFinalizers()` unblocks. The queued
release block then runs the next time the main run loop spins, which
the test drives with `NSRunLoop.Current.RunUntil(...)` between GC
rounds.

The rest of the bridge (`_Create`, `_GetViewController`, `_Update*`,
modifier setters) keeps `SBW_onMainThread` — those callers tolerate a
sync wait for main and require the block's return value, so the
existing semantics are intentional.

## Test coverage

- `BridgeSimpleViewTests.TestClassParamViewSessionFinalizerReleasesNativeHandle`
  re-enabled (no `[SkipOnSimulator]`). Drives the GC-only finalizer
  chain on a `ClassParamViewSession` + `SimpleModel` pair: pre-flush GC
  pump, reset + zero-baseline assert, orphan via
  `[MethodImpl(NoInlining)]` helper, up to six rounds of
  `GC.Collect()` + `GC.WaitForPendingFinalizers()` + a second
  `GC.Collect()` + `NSRunLoop.Current.RunUntil(... 0.1s)`
  (early-break on first observed `deinitCount >= 1`), then assert
  `deinitCount >= 1`. The second `GC.Collect()` after
  `WaitForPendingFinalizers` is intentional: any objects whose
  finalizers resurrected references via the queued main block can be
  re-collected on the next iteration. Pumping the main run loop
  between GC rounds is
  load-bearing — without it, the async-dispatched release block sits
  queued on main forever (or until a later test's main-thread work
  happens to drain it).
- `SwiftUIBridgeEmitterTests.EmitSimpleViewBridge_Free_DispatchesAsyncFromBackgroundCallers`
  pins the contract: the `_Free` body contains `Thread.isMainThread`
  and `DispatchQueue.main.async(execute:`, and does not contain
  `SBW_onMainThread` or `DispatchQueue.main.sync`. The companion
  `EmitSimpleViewBridge_GeneratesOnMainThreadHelper` test still
  asserts the helper exists for the rest of the bridge — only `_Free`
  bypasses it.

## Gates

- `nuke binding-tests --sim`: 2007 → 2008 pass (re-enabled test
  passes).
- `nuke binding-tests --device`: 2023 → 2027 pass (Session 6/7/8 net
  gains rolled in).
- Unit tests: 11285 + 20 + 563 green; the new emitter contract test
  pins the dispatch shape.

## Severity

**Correctness — Medium.** Latent on device (NativeAOT pumps through
the deadlock), fatal on simulator under any GC-only cleanup path that
calls `WaitForPendingFinalizers` from main. Real consumers don't hit
it today because the SwiftUI bridge `[Skip]`'d the test, but any
consumer who orphans a session and calls `GC.WaitForPendingFinalizers`
from the main thread would have seen a silent app hang under Mono.

## Follow-up: Swift owns GCHandle disposal

Codex flagged a Medium-severity ordering hazard against the
async-dispatch fix above: with `release()` queued onto the main run
loop, C# `Dispose` returned and freed every entry in
`_lifecycleHandles` / `_closureHandles` immediately, while the Swift
session state (`*_State`) still referenced those GCHandles through its
user-data pointers. If the queued main-thread block (or any deferred
SwiftUI cleanup) dereferenced a stale handle before the runtime
serviced the release, the process would crash with a use-after-free.

The fix transfers GCHandle ownership across the FFI boundary: the
generator widens `_Free` to a four-arg signature carrying a packed
handle buffer plus a post-release free-trampoline function pointer,
and the Swift wrapper calls the trampoline strictly *after*
`Unmanaged.release` runs inside the dispatched block. C# `Dispose`
stops iterating the handle lists locally.

### Emitter changes

`SwiftUIBridgeEmitter.cs`:

- Swift `_Free` now takes
  `(handle, handleBuffer, handleCount, postReleaseFreeFn)` and the
  release closure invokes `unsafeBitCast(fnPtr, to: FreeFn.self)` on
  the buffer pointer after `Unmanaged.release` completes. The
  `Thread.isMainThread / DispatchQueue.main.async` dispatch shape
  from the original fix is preserved.
- The C# P/Invoke signature widens to
  `(IntPtr handle, IntPtr handleBuffer, int handleCount, IntPtr postReleaseFreeFn)`.
- `Dispose(bool)` packs `_closureHandles + _lifecycleHandles` into a
  native buffer (`NativeMemory.Alloc((nuint)(count * sizeof(IntPtr)))`),
  writes each `GCHandle.ToIntPtr` into the slot, passes the buffer +
  count + a function-pointer-as-`IntPtr` to `_Free`, and clears the
  local lists. No `h.Free()` calls remain.
- A new helper class `SwiftUIBridgePostReleaseHelpers` is emitted once
  per bridge file with `[UnmanagedCallersOnly(CallConvs = ...)]`
  `FreeGCHandles(IntPtr buffer, int count)` that iterates the buffer
  freeing each `GCHandle.FromIntPtr` then calls `NativeMemory.Free`.

The same widening applies to async-view bridges: their generated
session owns a single `_stateHandle` GCHandle wrapping the
`CreateState` (TaskCompletionSource + optional `OnResult` Action) plus
the result-monitor `userData`. Pre-architectural-fix C# `Dispose`
freed `_stateHandle` locally immediately after invoking native `Free`,
while the Swift async session state (`resultTask` etc.) still
referenced it. The async-view `_Free` was also still routing through
`SBW_onMainThread` (`DispatchQueue.main.sync`), so the original
finalizer-thread deadlock applied to async sessions too. Both shapes
now share the same Swift-owned post-release trampoline plus
thread-aware dispatch. The post-release helper class is emitted once
per bridge file and gated on `r.IsFunctional` (covering both simple
and async views) rather than `r.IsFunctional && r.AsyncPattern == null`.

### Test coverage

- `SwiftUIBridgeEmitterTests.EmitSimpleViewBridge_Free_DelegatesGCHandleDisposalToSwift`
  pins the simple-view Swift signature, helper class, P/Invoke shape,
  and Dispose body (no local `h.Free()`).
- `SwiftUIBridgeEmitterTests.EmitAsyncViewBridge_Free_DelegatesGCHandleDisposalToSwift`
  pins the same contract for async views: the widened `_Free`
  signature, thread-aware dispatch, `unsafeBitCast` to invoke the
  post-release trampoline, `NativeMemory.Alloc` + `GCHandle.ToIntPtr`
  in the C# Dispose, and the absence of local `_stateHandle.Free()`.
- `BridgeSimpleViewTests.TestClassParamViewSessionWithLifecycleHandlesSurvivesAsyncFreeOrdering`
  exercises GC-only teardown of a session carrying onAppear /
  onDisappear `Action` callbacks — basic "no crash + deinit ran"
  smoke under the architectural shape.
- `BridgeSimpleViewTests.TestClassParamViewFreeRunsPostReleaseTrampolineWithLiveHandle`
  is the direct ordering contract test. Drives the Swift `_Free`
  wrapper from a background thread via `Task.Run` (forces the off-main
  `DispatchQueue.main.async` dispatch path) with a sentinel GCHandle
  in a `NativeMemory.Alloc` buffer and a test-controlled
  `postReleaseFreeFn` `[UnmanagedCallersOnly]` trampoline. The test
  pumps the main run loop while waiting and asserts four invariants:
  (1) the trampoline fired; (2) the sentinel GCHandle was still
  allocated when the trampoline ran (proves the caller did not free
  it locally before `_Free` completed); (3) the trampoline observed
  itself running on the main thread (proves the queued release block
  ran on main rather than inline on the background caller); (4)
  `GetViewController(sessionHandle)` returned `IntPtr.Zero` from
  inside the trampoline (proves the release block — which removes the
  handle from `liveHandles` and calls `Unmanaged.release` — executed
  BEFORE the trampoline, i.e. ordering is strictly post-release).
  Any regression to caller-frees-first, sync-on-caller-thread, or
  trampoline-before-release ordering would trip one of these
  assertions deterministically.

### Gates

- `nuke binding-tests --sim`: 2008 → 2010 pass (new lifecycle test
  + new direct-ordering contract test, auto-ratcheted).
- `nuke binding-tests --device`: 2027 → 2029 pass across two runs
  (lifecycle test then ordering-contract test, auto-ratcheted).
