# Session 2 — Async boundary correctness

**From** `architecture-review-2026-06.md` Phase 1 / Lane A. Tags: ⚠device 🔍design.

This session fixes five async-boundary correctness defects. It is a **🔍design** session:
this document is the implementation plan and is reviewed (Grok design pass + Codex on the
highest-ABI-risk items) **before** any code is written. Each defect fix is **TDD red-first**.
The closing gate is the CLAUDE.md matrix for generator/emitter + runtime changes, including
`nuke binding-tests --device` (⚠device).

Scope boundary with **Session 13** (Async harness invariants, also 🔍design): Session 13
extracts the shared *invariant layer* and gives Finding 36 its *permanent answer* (a real
async witness on the continuation-handoff machinery) and Defect I its *redesign* (first-class
async shape on the shared harness). Session 2 is the **tactical** pass: stop the bleeding
(leaks, UAF, hangs, lost cancels, process-death holes) without the harness merge.

---

## Items

| # | Finding | Risk | Touches |
|---|---|---|---|
| 1 | Finding 39 — cancellation: lost-cancel race + 6 flagless TCS | Low | Swift registry emitter, 1 TCS emit site |
| 2 | Finding 38 — one UCO guard envelope (policy enum) + KVO + 32 catch-free UCOs | Medium | UCO-guard helpers, KVO, Start/stream thunks, validator |
| 3 | Finding 37 — mechanical resume-once (C# AsyncResumeGuard; Swift box unchanged) | High | AsyncClosureHelper, Start-thunk never-resume/double-resume |
| 4 | Finding 36 — async-witness exception policy (OCE-is-process-death) | High (policy) | Receiver async-unwrap path |
| 5 | Defect I — AsyncStream bridge tactical fix | High | `SwiftAsyncStream.cs`, AsyncStream emitter, type gate |

Findings 38/37/Defect-I share the **catch-free-UCO** surface (Start thunks belong to 37,
element/complete thunks to Defect I, KVO to 38). The single guard envelope (38) is the
mechanism; 37 and Defect I supply the per-policy bodies it wraps. Implementation order:
**39 → 38 (envelope + KVO) → 37 (ResumeScope + Start-thunk error-resume, routed through the
envelope) → Defect I (stream-fault, routed through the envelope) → 36**.

---

## Owner decision (Sessions 2/13): async-witness policy on Mono — **guarded block**

The review (line 2442) flags one owner decision for this session: *guarded block vs loud
reject* for async witnesses while upstream Issue 1 (Mono reverse-async assertion) stands.

**Decision: guarded block.** Rationale:
- Session 2's own scope line says "async-witness policy decision + **at minimum the
  `OperationCanceledException`-is-process-death hole**." A *loud reject* (refuse to emit async
  witnesses; emit a diagnostic instead) removes functionality that works today for the happy
  path (3 generated witnesses: `RefineModifyAsync`, `MixedFanModifyAsync`,
  `IntraEffectTagAsync`) and is incompatible with the "fix the OCE hole" framing — you cannot
  have an OCE hole in a path you no longer emit.
- Session 13 holds Finding 36's *permanent answer* (real async witness on the
  continuation-handoff machinery). Session 2 keeps the sync-blocked witness alive and only
  closes the exception hole, deferring the executor-starvation / MainActor-reentry hazards to
  13. This matches the review's explicit Session-2-tactical / Session-13-redesign split.

Resolved autonomously (per `/autonomous-mode`); cross-checked in the design review below
rather than escalated to the owner.

---

## Item 1 — Finding 39: cancellation

> **Status: IMPLEMENTED.** Shipped fix extends the original plan below with a second lost-cancel
> window discovered during implementation/review — *WINDOW A* (cancel arrives before
> `_sbwRegisterTask` runs at all) — closed with a cancellation **tombstone** + a `@_cdecl`
> unregister export and a foreground-catch reclaim. See *The bug (c)* and *Fix shape 3–4*.

### Current state
- Swift registry emitter `CancellationTaskEmitter.cs:38–69`: `_SBWTaskEntry { var task }`,
  `_sbwActiveTasks: [Int64: _SBWTaskEntry]`, `_sbwTaskLock = NSLock()`, and
  `_sbw_cancelTask` which reads the entry **under** the lock then calls
  `entry?.task?.cancel()` **outside** the lock (`:62–67`).
- Four Swift registration sites all emit the same ordering — register the entry, then assign
  `_entry.task = Task { … }` **after** registration and **not under the lock**:
  `AsyncProjection.cs:108–110`, `AsyncMethodGenericBridgeEmitter.cs:606–609`,
  `WrapperEmitter.Async.cs:1546–1548` (throwing) and `:1568–1570` (non-throwing).
- Six TCS allocations lack `RunContinuationsAsynchronously`, all from one emit site:
  `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs:906` (`new
  TaskCompletionSource<…>()`). Confirmed: `WrapperEmitter.Async.cs` (`:294,:314,:330,:348,
  :366`) and `MethodHandler.cs:1745` already pass the flag — the review's "ObjC emitter"
  attribution is wrong; the single offender is the CSM parent-only async specialization.

### The bug
(a) **Lost-cancel race.** `_sbw_cancelTask` fires in the window between `_sbwRegisterTask` and
the `_entry.task =` assignment → reads `.task == nil` → `nil?.cancel()` is a no-op. The C#
side has already `TrySetCanceled`'d the TCS, so the caller observes cancellation while the
Swift task runs to completion (callback then fires into a completed TCS — benign there, but
the *work* was never cancelled). There is no `wasCancelled` replay flag, and the `.task =`
assignment is not under the registry lock, so even adding a flag naively would still race on
memory visibility (an unlocked write to `.task` establishes no happens-before with the locked
reader).

(b) **6 flagless TCS.** Continuations on those tasks run **inline on Swift's executor**
(textbook reverse-deadlock setup). These CSM parent-only async specializations *also* carry
no cancellation at all (the public signature at `:902` takes no `CancellationToken`; no
`_sbwCancelKey`, no holder cancel slot) — a deeper gap than the flag.

(c) **WINDOW A — pre-registration lost cancel** (discovered during implementation). The
`wasCancelled` replay in (a) only helps when an entry already exists. If `_sbw_cancelTask`
fires *before* the Swift wrapper's `_sbwRegisterTask` runs at all, there is no entry to flag,
the cancel is dropped, and the later-registered task runs to completion. The registry key is a
process-monotonic `_sbwCancelKey` (never recycled), so the fix is recycle-safe by construction.

### Fix shape
1. **`wasCancelled` replay, fully under the lock.** Add `var wasCancelled = false` to
   `_SBWTaskEntry`. Centralize the task assignment in a new locked helper so every
   registration site uses the same happens-before:
   ```swift
   private func _sbwAssignTask(_ entry: _SBWTaskEntry, _ task: Task<Void, Never>) -> Bool {
       _sbwTaskLock.lock()
       entry.task = task
       let cancelledEarly = entry.wasCancelled
       _sbwTaskLock.unlock()
       return cancelledEarly
   }
   ```
   And make the cancel path set the flag under the same lock when the task is not yet
   assigned:
   ```swift
   @_cdecl("…")
   public func _sbw_cancelTask(_ taskId: Int64) {
       _sbwTaskLock.lock()
       let entry = _sbwActiveTasks[taskId]
       let task = entry?.task
       if let entry, task == nil { entry.wasCancelled = true }
       _sbwTaskLock.unlock()
       task?.cancel()
   }
   ```
   The four registration sites change from `_entry.task = Task { … }` to:
   ```swift
   let _sbwLaunchedTask = Task { … }
   if _sbwAssignTask(_entry, _sbwLaunchedTask) { _sbwLaunchedTask.cancel() }
   ```
   (Name `_sbwLaunchedTask` to avoid colliding with the existing `_sbwTask` identifier used in
   the `callback(…, _sbwTask)` line at `WrapperEmitter.Async.cs:1555/1576`.) The assignment
   and the cancel-check are now ordered by the single registry lock — race-free in both
   directions.

2. **Add the flag to the 6 TCS.** `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs:906`
   → `new TaskCompletionSource<…>(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)`.

3. **WINDOW A tombstone + carry-forward (shipped).** When `_sbw_cancelTask` finds no entry for
   the id, it inserts a pre-marked tombstone (`wasCancelled = true`) under the lock instead of
   dropping the cancel; `_sbwRegisterTask` then carries that flag forward onto the real entry:
   ```swift
   // _sbw_cancelTask, no-entry branch:
   } else { let t = _SBWTaskEntry(); t.wasCancelled = true; _sbwActiveTasks[taskId] = t }
   // _sbwRegisterTask, before storing the entry:
   if let existing = _sbwActiveTasks[taskId], existing.wasCancelled { entry.wasCancelled = true }
   ```
   The subsequent `_sbwAssignTask` (fix 1) sees `wasCancelled` and cancels the launched task —
   so a pre-registration cancel is replayed exactly like the register→assign window.

4. **Tombstone reclaim on foreground throw (shipped).** A tombstone is normally cleared by the
   wrapper's `defer { _sbwUnregisterTask }`. But if the C# foreground marshalling throws *before*
   the P/Invoke launches the wrapper, that `defer` never runs and the tombstone strands (and the
   recycle-safe-but-unbounded `_sbwActiveTasks` entry leaks until process exit). Closed at the
   root: a `@_cdecl("SBW_UnregisterTask_<module>")` export wrapping `_sbwUnregisterTask`, a
   per-type `SBW_UnregisterTask` P/Invoke emitted (deduped) alongside `SBW_CancelTask`, and a
   `SBW_UnregisterTask(_sbwCancelKey)` call in each foreground launch `catch` after `handle.Free()`
   and before `throw;`. Emitted by the two registry-participating foreground-catch sites —
   `WrapperEmitter.Return.cs` and `AsyncMethodGenericBridgeEmitter.cs` (P/Invoke decl in
   `AsyncHarnessEmitter.cs`). `removeValue` is a no-op when no tombstone exists, so the reclaim is
   safe on the normal path.

> **AsyncProjection note.** `AsyncProjection.GetSwiftWrapperCode` is a *legacy, non-production*
> async-wrapper template (no generator path invokes it; only its `GetReturnPlan` is live). It is
> left as-is with a doc warning: before any revival it must adopt the `_sbwTask`/`_sbwCancelKey`
> key separation the live emitters use. Not part of the shipped registry behavior.

### Open risks / deferred
- The CSM parent-only async specialization's **total absence of cancellation** (no token, no
  registry registration) is out of Session 2's stated scope ("the 6 TCS sites missing
  `RunContinuationsAsynchronously`") and belongs to **Session 13**'s harness consolidation. It
  is recorded under *Deferred / split-out units* below — not silently dropped.
- `_sbwAssignTask` must be `private` and emitted exactly once in the registry block (it lives
  next to `_sbwRegisterTask`). The four call sites must not reference `_entry.task` directly
  any more.

### Footprint
`CancellationTaskEmitter.cs` (entry struct + cancel func + assign helper + WINDOW A tombstone +
carry-forward + `@_cdecl` unregister export + `GetUnregisterSymbolName`),
`AsyncMethodGenericBridgeEmitter.cs` (registration + `SBW_UnregisterTask` decl + catch reclaim),
`WrapperEmitter.Async.cs` (×2 registration blocks), `WrapperEmitter.Return.cs` (catch reclaim),
`AsyncHarnessEmitter.cs` (`SBW_UnregisterTask` P/Invoke, deduped with `SBW_CancelTask`),
`ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` (1 line, TCS flag),
`AsyncProjection.cs` (legacy template — doc-warning only, see note above).

### Tests (red-first)
- **Unit (generator):** assert the emitted registry block contains `wasCancelled` and
  `_sbwAssignTask`, that `_sbw_cancelTask` sets `wasCancelled` under the lock, and that
  registration sites emit `if _sbwAssignTask(…) { …cancel() }` (not a bare `_entry.task =`).
  Assert the CSM async-parent TCS now carries `RunContinuationsAsynchronously`. **WINDOW A:**
  assert `_sbw_cancelTask` leaves a tombstone when no entry exists, `_sbwRegisterTask` carries
  the early-cancel flag forward, the `@_cdecl("SBW_UnregisterTask_<module>")` export is emitted,
  and each foreground `catch` calls `SBW_UnregisterTask(_sbwCancelKey)` after `handle.Free()` and
  before `throw;` (both emitter paths).
- **BindingTests:** `CancellationRaceProbe` + `CancellationRaceTests` in the async domain.
  WINDOW A cannot be hit deterministically from managed code, so the generated fix is pinned by
  the deterministic emitter unit tests; the runtime layer asserts the no-loss/no-crash invariant
  — a deterministic mid-flight cancel (`TestPostLaunchCancel…`) and a concurrent
  cancel-races-launch stress test (`TestConcurrentCancelRace…`). **Settle correctness:** the
  fixture tracks a `started` counter (incremented at body entry) and the tests wait until
  `started == resolved` rather than a fixed "N resolved" floor — a lost cancel increments
  `started` immediately but only `resolved` after the full slice budget (~400ms), so a fixed
  floor returns at the first lull and passes falsely. Verified red-first by simulating a lost
  cancel (both tests fail `completedWithoutCancel == 0`), then green with the fix reverted.

---

## Item 2 — Finding 38: one UCO guard envelope

### Current state
Three correct policies exist but are emitted by copy-paste with three deviations:
- **FailFast (non-throwing):** single source `ClosureEmitter.FailFastCatch.cs:31–40`
  (`EmitNonThrowingFailFastCatch`) and the receiver pair
  `ProtocolProxyEmitter.Receivers.cs:2064–2087` (`EmitUcoGuardOpen` /
  `EmitUcoGuardCloseFailFast`). Several sibling sites open-code the identical
  `catch (…) { FailFastUnhandledClosureException(__ex); throw; }` as string literals.
- **error-channel (throwing closures)** and **TCS-fault (async)**: their own sites.
- **Deviation 1 — KVO fail-soft.** `KvoExtensionEmitter.cs:162–189` wraps the dispatch in
  `try { … } catch (Exception ex) { Console.Error.WriteLine(…); }` — print-and-continue —
  under a comment (`:164–166`) falsely claiming "This matches the standard
  UnmanagedCallersOnly pattern across the runtime." It does not; the standard is FailFast.
- **Deviation 2 — 32 catch-free UCOs:** 7 `_OnElement` + 7 `_OnComplete` (AsyncStream;
  `AsyncStreamEmitter.cs:20–42`, `:58–79`) + 18 async-closure `…Callback` Start thunks
  (`ClosureEmitter.Async.cs:137–143`, early-return `if handle.Target is not … return;`).
- **Deviation 3 — AsyncStream truncate-on-error** (handled in Defect I).

### Fix shape
1. **One guard envelope with a policy enum.** Introduce a single emitter, e.g.
   `UcoGuardEmitter` with `enum UcoFaultPolicy { FailFast, StreamFault, ResumeBoxError }`
   exposing `EmitOpen(writer)` and `EmitClose(writer, policy, ctx)`. The existing
   `EmitNonThrowingFailFastCatch` and `EmitUcoGuardOpen/CloseFailFast` become the `FailFast`
   case (keep them as thin shims so unrelated callers' output is byte-identical). The
   error-channel and TCS-fault shapes are already structured; fold them in only where it does
   not change emitted output (low-risk consolidation — assert behavior, not exact strings).
2. **KVO → FailFast.** Replace the print-and-continue catch with the shared FailFast catch and
   delete the false comment. KVO handlers are non-throwing callbacks with no error channel —
   the standard policy applies. (A KVO change handler throwing is a consumer bug that must be
   loud, not swallowed mid-observation.)
3. **Start thunks (18) → `ResumeBoxError`.** The Start thunk currently early-returns on
   GCHandle-target mismatch (never-resume, owned by Finding 37) and is otherwise catch-free.
   Wrap its body in the envelope with the `ResumeBoxError` policy: on any exception (and on
   the target-mismatch path) resume the continuation box with an error via the C# ResumeScope
   (Item 3) so Swift's task never hangs and the box is consumed exactly once. This is the
   single change that closes both the catch-free hole (38) and the never-resume hole (37).
4. **Stream UCOs (14) → `StreamFault`.** Wrap `_OnElement`/`_OnComplete` in the envelope with
   the `StreamFault` policy: on exception, fault the channel (`TryComplete(ex)`) instead of
   crashing — a marshal failure in one element must not abort the process. This is the same
   change Defect I (e) needs; implemented once here (Item 5 owns the runtime side).
5. **Generator-time validator.** Add a check that no emitted `[UnmanagedCallersOnly]` method
   is catch-free. Altitude (to settle in review): a post-emission scan of the generated C#
   corpus in a unit test (assert zero catch-free UCOs across the BindingTests output), versus
   a structural assertion that all UCO emission routes through the envelope. Lean: a unit-test
   corpus scan keyed off `[UnmanagedCallersOnly]` declarations is the cheapest durable gate
   that matches the "`--compile-only` fail-closed spirit" without a large structural refactor;
   the structural route is Session 13's harness work.

### Open risks / questions
- The error-channel and TCS-fault policies are emitted by structurally different sites; full
  unification risks output churn. **Plan:** consolidate only FailFast + the two new policies
  (StreamFault, ResumeBoxError) now; leave error-channel/TCS-fault sites in place but routed
  through the enum where output is unchanged. Avoids a Finding-15-style "derive it twice"
  while not over-reaching into Session 13.
- The validator must not false-positive on the runtime's hand-written UCOs
  (`SwiftAsyncStreamInterop.OnElementCallback` etc. are placeholders, but they live in
  `Swift.Runtime`, not generated output) — scope the scan to generated `output/*.cs`.

### Footprint
New `UcoGuardEmitter.cs`; edits to `ClosureEmitter.FailFastCatch.cs` (shim),
`ProtocolProxyEmitter.Receivers.cs` (shim), `KvoExtensionEmitter.cs`,
`AsyncStreamEmitter.cs`, `ClosureEmitter.Async.cs`; new validator (unit test or generator
check).

### Tests (red-first)
- **Unit:** KVO trampoline emits the FailFast catch (not `Console.Error.WriteLine`) and no
  false "matches the standard pattern" comment. `_OnElement`/`_OnComplete` and Start thunks
  emit a guarded body (no catch-free UCO). The validator fails on a synthesized catch-free UCO
  and passes on the fixed corpus.
- **BindingTests:** a KVO change handler that throws → process FailFast (loud), asserted via
  the existing crash-policy harness pattern; an AsyncStream element whose marshal throws →
  stream faults (consumer sees the exception) rather than the process aborting.

---

## Item 3 — Finding 37: mechanical resume-once

> **Status: IMPLEMENTED.** Shipped per Decision 1 (C#-side once-ness is the sole guarantee; the
> Swift box is unchanged). The design's `ResumeScope` object was realized as the leaner, policy-free
> `AsyncResumeGuard` (an `Interlocked.Exchange` once-flag in `AsyncClosureHelper.cs`): the Start
> thunk constructs one guard and both the success and error callback delegates begin with
> `if (!__resumeGuard.TryClaim()) return;`, so the loser's `@_cdecl` resume call is unreachable.
> Because the raw `successFuncPtr`/`errorFuncPtr` are dereferenced *only* inside those guarded
> delegates — and `CompleteWithResult`/`ReportError` resume exclusively through them — no resume
> path bypasses the claim. The never-resume hole is closed: every Start-thunk exit resumes the box
> (the context-type-mismatch path and any arg-marshalling exception now route through a `try`/`catch`
> to `AsyncClosureHelper.ReportError` for throwing closures, or `FailFastNonThrowing` for
> non-throwing ones) instead of returning silently. The double-resume hole is closed at root cause:
> `CompleteWithResult` disposes the result via `DisposeResultQuietly`, which swallows-and-logs a
> post-resume `Dispose()` failure rather than letting it propagate into the completion catch (the
> guard is the backstop if it ever did). Verified: 18 generated Start thunks (13 throwing →
> `ReportError` both paths, 5 non-throwing → `FailFast` both paths), zero silent mismatch returns;
> generator unit tests (`ClosureEmitterAsyncTests` +5), runtime unit tests (`AsyncResumeGuardTests`
> +5, incl. a 64-thread contention single-winner check and the throwing-Dispose no-propagate check),
> sim gate 2831/0/38/0 baseline-match.
>
> **Review-round 2 (Codex + Grok, both High-converged) closed two remaining gaps:**
> (1) **Bad-context-handle escape** — `GCHandle.FromIntPtr(contextPtr)` was resolved in the pre-`try`
> preamble, so a zero/corrupt `contextPtr` threw `InvalidOperationException` *past* the
> `[UnmanagedCallersOnly]` boundary with the box never resumed. It now resolves as the first
> statement *inside* the guarded `try` (the guard + delegates stay in the preamble because the catch
> resumes through `errorAction`), so a bad handle is caught and routes to `ReportError` (throwing) /
> `FailFastNonThrowing` (non-throwing). Pinned by `AsyncCallback_ContextHandleResolution_IsInsideGuardedTry`.
> (2) **Non-throwing String/Data fail-closed** — non-throwing async String/Data returns are already
> gated out upstream (`ClosureHandler.IsBaselineAsyncNonThrowingClosure` requires a blittable-primitive
> return), so the `isDataReturn || isStringReturn` branch is throwing-only today. A generator-time
> `NotSupportedException` now fires if that gate is ever widened without teaching the emitter a
> non-throwing String/Data shape — preventing a silent state-type mismatch (`RunStringAsync`/
> `RunDataAsync` bind `AsyncThrowingClosureState`, not the non-throwing `AsyncClosureState`). Pinned by
> `AsyncCallback_NonThrowingStringReturn_FailsClosed`.
>
> **Two review-round-2 findings deliberately NOT actioned (with rationale):**
> - *OOM during the pre-`try` guard/delegate allocation can still escape without a resume* (Grok). This
>   is dismiss-by-design, not a fixable gap: the catch resumes *through* `errorAction`, so the guard and
>   the success/error delegates must be constructed before the `try` — you cannot resume a continuation
>   box without first allocating the delegate that resumes it. Wrapping their allocation in a `try` whose
>   recovery path needs them is circular, and total allocation failure is process-fatal regardless (the
>   downstream `Task.Run` + `ReportError` → resume path also allocate). The Stage 1 comment records this.
> - *The void-return branch (`RunVoidAsync` / `AsyncThrowingClosureStateVoid`) has no end-to-end corpus
>   coverage* (Grok, both rounds). Root cause is **not** a missing fixture: `() async throws -> Void` is
>   gated out of the pipeline entirely — `ClosureHandler.IsBaselineAsyncThrowingClosure` rejects an
>   empty-tuple return (`ReturnType is not NamedTypeSpec`), so `IsBaselineAsyncClosure` is false at the
>   emission site (`WrapperEmitter.Marshalling.cs:929`) and the method is skipped (projected to AnyType).
>   The emitter's void branch is therefore dead in the current pipeline and reachable only by direct
>   unit-test invocation (`AsyncCallback_VoidReturn_*`, `AsyncCallback_ContextHandleResolution_*`).
>   Enabling the shape is a distinct **feature** (widen the gate AND emit a void Swift continuation box
>   in `AsyncSwiftWrapper`, with device-test surface), out of scope for Finding 37's resume-once mandate.
>   Filed here as a discovered, non-blocking gap — not a Finding-37 regression.

### Current state
- Swift box (`ClosureEmitter.AsyncSwiftWrapper.cs:79–238`): `passRetained(box).toOpaque()`
  (+1) on creation; both `_success` and `_error` `@_cdecl` symbols do
  `Unmanaged<Box>.fromOpaque(boxPtr).takeRetainedValue()` (consumes the +1) then
  `cont.resume(...)`. No once-flag. (Data/String/primitive/throwing/non-throwing variants all
  share this shape; the non-throwing variant emits only `_success`.)
- C# Start thunk (`ClosureEmitter.Async.cs:141–143`): `if (handle.Target is not … state)
  return;` — silent early return, box never resumed.
- C# completion (`AsyncClosureHelper.cs:CompleteWithResult`): `successAction(box, …)` inside an
  inner `try`; outer `finally { (result as IDisposable)?.Dispose(); }`. If `Dispose()` throws
  **after** `successAction` consumed the box, the exception propagates to `RunAsync`'s catch →
  `ReportError` → `errorAction(box, …)` on the already-consumed box.

### The bug
- **Never-resume (hang + leak):** target-mismatch early-return leaves the Swift task awaiting
  forever; the +1 box is never released.
- **Double-resume (UAF + Swift trap):** the Dispose-in-finally path resumes the consumed box.
  The second `takeRetainedValue` over-releases (UAF) and `cont.resume` on an already-resumed
  `CheckedContinuation` is a Swift `fatalError`.

### Fix shape (revised per review Decision 1 — C# `ResumeScope` is the sole guarantee)
The `_success`/`_error` `@_cdecl` symbols are *only* ever called from C#, so once-ness is a
purely C#-side property: serialize to exactly one resume call and the box is consumed exactly
once. **No Swift-side once-flag** — Codex showed it is unsound (a flag stored inside the box
can't guard the box's own liveness; the loser must deref the possibly-freed box to even reach
it) and valueless (if C# serializes, the loser never fires).

1. **C#-side `ResumeScope`** (in `AsyncClosureHelper` / a small runtime helper). A scope that
   owns the `(successAction, errorAction, boxPtr)` triple and guarantees exactly one resume:
   - `TryResumeSuccess(...)` / `TryResumeError(...)` each do an `Interlocked.Exchange` on a
     `_resumed` flag; the loser is a no-op (optionally asserts in debug). This is the only
     once-ness mechanism — it makes the loser `@_cdecl` call literally unreachable from our
     generated code.
   - **Dispose ordering fix (root cause):** marshal/copy the result into the success buffer,
     call `TryResumeSuccess`, then dispose the disposable in a `finally` whose exception is
     caught-and-logged — **never rethrown** into `RunAsync`'s catch (which today re-enters
     `ReportError` → `errorAction` on the already-resumed box). A post-success Dispose failure
     therefore cannot un-resume the box or trigger a second `@_cdecl` resume call.
   - Every Start-thunk exit goes through the scope: the target-mismatch path and any
     marshalling exception call `TryResumeError(box, <diagnostic>)` (the `ResumeBoxError`
     policy from Item 2), never a silent `return`. (For a non-throwing box, which emits no
     `_error` symbol, the error path is the `FailFast` policy — there is no Swift error to
     resume with.)
2. **Swift box: unchanged shape, no flag.** The winner still consumes the `+1` via
   `takeRetainedValue()` then `cont.resume(...)`. Because C# guarantees a single call, this is
   correct as-is; we do not add a per-box lock, `Synchronization`/`Atomics` dependency, or
   unretained-read claim.

### Open risks / questions
- The C# ResumeScope must be the *only* path that reaches `_success`/`_error` for a given box —
  audit `AsyncClosureHelper`/`RunAsync`/`CompleteWithResult`/`ReportError` so no resume call
  bypasses the claim. (This is the entire safety argument now that the Swift side has no
  backstop.)
- Data/String/primitive/throwing/non-throwing box variants must all route their C# completion
  through the ResumeScope uniformly (5 emit branches in `EmitAsyncClosureBoxIfNeeded`), even
  though the Swift box emission is unchanged.
- The non-throwing box emits no `_error` symbol; its never-resume backstop is the ResumeScope
  on the C# side (FailFast policy). Verify the Start-thunk error path for non-throwing closures
  FailFasts rather than calling a non-existent error symbol.

### Footprint
`AsyncClosureHelper.cs` (ResumeScope + Dispose-ordering — the substantive fix),
`ClosureEmitter.Async.cs` (Start thunk → scope). The Swift box emitter
(`ClosureEmitter.AsyncSwiftWrapper.cs`) is **not** changed.

### Tests (red-first)
- **Runtime (AsyncClosureHelper):** a result whose `Dispose()` throws after success → the box
  resumes exactly once (no error-resume); a target-mismatch Start → the box resumes with an
  error (no hang). Use `LifetimeTracker` to assert the box is released exactly once.
- **BindingTests:** an async-throwing closure whose C# delegate returns a disposable whose
  Dispose throws → no crash, value delivered; a deliberately mismatched context → the Swift
  awaiter observes an error, not a hang.

---

## Item 4 — Finding 36: async-witness exception policy

### Current state
`ProtocolProxyEmitter.Receivers.cs:1127` sets `asyncResultUnwrap = ".GetAwaiter().GetResult()"`
for async requirements; the unwrapped call (`:1153`, `:1170`) runs inside the
`EmitUcoGuardOpen` / `EmitUcoGuardCloseFailFast` envelope (`:1173`). The receiver has **no
error channel** (grep: no `errorOut`/`SwiftError`/`throws` handling) — every exception, incl.
`OperationCanceledException`, FailFasts. 3 generated witnesses (`:309817`, `:310760`,
`:356037`), all non-throwing requirements.

### The bug
A non-throwing Swift async requirement has no way to carry an error, so the receiver
FailFasts. `OperationCanceledException` is routine in C# async code (any `await x(token)` /
`Task.Delay(t, token)`), so a binding crashing the host process because a consumer's async
cancellation propagated is hostile — and the FailFast is anonymous ("unhandled exception"),
misdiagnosed as a Swift-library fault per CLAUDE.md doctrine.

### Fix shape (guarded block — minimum OCE hole)
Keep the sync-blocked witness (Session 13 does the real async witness). Refine the exception
handling at the unwrap:
1. **Distinguish faults from cancellation.** A genuine fault (non-OCE) in a non-throwing
   requirement remains a FailFast — that is the documented non-throwing policy and a real bug.
2. **`OperationCanceledException` is not anonymous process death.** Two candidate behaviors
   for the non-throwing case (no honest value to return; fabricating one is the Defect-G
   disease, so that is off the table):
   - **(4a) Cancellation-specific loud FailFast.** Catch OCE separately and FailFast with a
     precise `[SwiftBindings]` diagnostic naming the protocol member and explaining that a
     non-throwing async Swift requirement cannot carry cancellation, so the C# conformance
     must not throw `OperationCanceledException`. Still a crash, but no longer a
     *misdiagnosed* one — it tells the consumer exactly what contract they broke.
   - **(4b) — DECIDED: gate `async throws` requirements out at generation.** Review confirmed
     (Codex High #2 + `WitnessDispatchEmitter.IsMethodWitnessDispatchEligible:1073` /
     `MemberEmissionValidator` reads) that `async throws` reverse-dispatch requirements are
     **not** gated today: `IsAsync` only maps the return type to `Task<T>`, the Swift
     EveryProtocol method preserves `throws`, but the C# receiver does
     `impl.FAsync(...).GetAwaiter().GetResult()` and the generic FailFast catches the thrown
     error — silently violating the `throws` contract the Swift signature advertises. There is
     no Swift error channel to honor it (that is Session-13 work). Honest behavior: **reject
     `async throws` reverse-dispatch requirements with a named SkipReason** rather than ship a
     happy-path that becomes process-death on the first thrown error. Corpus has none →
     zero-regression (confirm during TDD; if a member is rejected, it follows the existing
     unsupported-member skip path).
   **Decision:** (4a) for non-throwing `async` + (4b)-gate for `async throws`.
3. **Fix the over-broad deadlock comment.** `Receivers.cs:1124`'s "no SynchronizationContext,
   so blocking cannot self-deadlock" is too narrow (Codex Medium): it avoids SyncContext-capture
   deadlock but not main-actor/executor-starvation deadlock (a Swift main-actor caller blocked
   on a C# task that needs the same thread). Reword honestly; the Session-2 diagnostic must not
   imply the sync-blocked path is deadlock-safe (that hazard is tracked for Session 13).
4. **Write down the Issue-1 linkage.** The coupling currently lives only in an emitter comment
   (`:1119–1127`). Document the OCE/sync-witness workaround with the **reverse-async assertion**
   (the bug that forces the sync slot) — *not* `upstream-issue-01` (a different symptom:
   CallConvSwift unwind assert after a native crash, per both reviewers) — so the workaround is
   removable when upstream fixes.

### Open risks / questions
- `TaskCanceledException : OperationCanceledException`, so a single `catch
  (OperationCanceledException)` covers both; faults arrive as the raw exception (GetResult
  unwraps `AggregateException`), so the non-OCE FailFast path is unchanged.
- Gate granularity: confirm rejecting an `async throws` requirement skips the member cleanly
  (or rejects the conformance with a diagnostic) without leaving a CS0535 hole — match whatever
  the existing unsupported-member path does.

### Footprint
`ProtocolProxyEmitter.Receivers.cs` (OCE-specific catch + comment fix), the async-throws gate
(`WitnessDispatchEmitter`/`MemberEmissionValidator`), one doc note on the reverse-async
assertion. No ABI change (the witness still returns T via the sync slot).

### Tests (red-first)
- **Unit:** the async receiver emits an OCE-specific catch distinct from the general FailFast;
  the general (non-OCE) path is unchanged. An `async throws` protocol requirement is gated out
  with the named SkipReason (and a non-throwing `async` requirement is still emitted).
- **BindingTests:** a C# async conformance that throws `OperationCanceledException` →
  controlled, member-named FailFast (not an anonymous one); a C# async conformance that
  returns a value normally → round-trips (regression guard for the happy path).

### As-built (2026-06-13) — **DONE**
Shipped behavior, where it diverged from the plan above, and why:
- **(4a) shipped; (4b) gate REJECTED — kept-wired member-named FailFast instead.** The plan's
  Decision was "(4a) for non-throwing `async` + (4b)-gate for `async throws`." Investigation
  during implementation found the (4b) gate is **unsafe with the skip machinery as it stands**,
  so `async throws` requirements stay wired and get the same member-named FailFast as the
  non-throwing case. The reason is a vtable slot-misalignment trap:
  - The Swift producer (`EveryProtocolEmitter`) decides stub-vs-real-forward **independently** of
    the general `skippedMethodKeys`, via `MethodEmitsVtableField` — which returns `true` for a
    plain `async throws` requirement (it only suppresses for non-dispatchable-closure /
    method-level-generics / Self-type-param / mixed-generic).
  - The C# vtable struct (`ProtocolProxyEmitter.Vtables.cs`) only suppresses its field via
    `_closureSkippedMethodKeys`, **not** the general `_skippedMethodKeys`.
  - So routing `async throws` through `skippedMethodKeys` would drop the C# interface member +
    receiver + StaticInit assignment (leaving a **null** vtable field) while Swift still emits a
    real vtable forward → **nil-deref crash**. Maintaining parity would require mirroring the
    closure-skipped stub across the ~4–5 vtable sites Finding 8 already flags as "decade-risk" —
    i.e. adding a *5th* membership category to known-fragile machinery. That is the exact "N
    sites, same shape" trap (`feedback_no_session_cascade`) and a worse outcome than a loud,
    attributable runtime FailFast. Failing the requirement *closed at generation* is the right
    end state, but it is **blocked on Finding 8's vtable consolidation** and is recorded as such
    (Session 13 owns the real async/error witness that would carry the error back instead).
- **Both arms are member-named (4a generalized).** The async receiver close
  (`UcoGuardEmitter.EmitCloseAsyncWitnessFailFast`, wired from
  `ProtocolProxyEmitter.Receivers.cs` only when `method.IsAsync`) emits two catches,
  most-derived-first to satisfy CS0160: `OperationCanceledException` →
  `SwiftClosureMarshaller.FailFastAsyncWitnessCancellation(ex, "Proto.member")`, then
  `Exception` → `FailFastAsyncWitnessException(ex, "Proto.member")`. Both name the protocol
  member and end in `throw;` (CS0161 terminator). Sync receivers keep the anonymous plain
  `FailFastUnhandledClosureException`. The cancellation arm earns its own message because
  cancellation is routine C# async control flow (a token wired into the conformance would raise
  it on normal cancellation); the general arm covers a deliberate throw from an `async throws`
  conformance, whose throwing path is **unsupported** until the real witness lands.
- **Deadlock comment fixed (plan item 3).** `Receivers.cs`'s "no SynchronizationContext, so
  blocking cannot self-deadlock" claim was removed; the reworded comment states the sync-blocked
  slot **can** self-deadlock (main-actor reentry / cooperative-pool starvation) and that only the
  Session-13 real async witness removes that hazard — what this seam guarantees is only that an
  escape converts to a member-named FailFast, never silent boundary corruption.
- **Issue-1 linkage written down (plan item 4).** The coupling now lives in three durable places
  (not just one emitter comment): the reworded `Receivers.cs` unwrap comment, the
  `UcoGuardEmitter.EmitCloseAsyncWitnessFailFast` doc, and the two `SwiftClosureMarshaller`
  FailFast helper docs — each ties the sync-blocked slot + absent error channel to the Mono
  **reverse-async assertion** (upstream Issue 1), not `upstream-issue-01` (the distinct
  CallConvSwift-unwind-after-native-crash symptom).
- **Tests as-built: unit-only by necessity.** Three unit tests
  (`UcoGuardEmitterTests.EmitCloseAsyncWitnessFailFast_*` ×2,
  `ProtocolProxyEmitterTests.EmitProxyClass_AsyncReceiver_FailFastsWithMemberName_SyncReceiverKeepsPlainFailFast`)
  pin the emitted shape: OCE-before-Exception ordering, member-named both arms, string-literal
  escaping, and that a sibling **sync** receiver in the same proxy keeps the plain FailFast. The
  planned BindingTests legs are **not addable**: the async reverse-dispatch *happy path* cannot
  be driven at runtime on Mono (a Swift `await` of the requirement hits Issue 1 and crashes —
  this is exactly why `IntraProtocolEffectOverloadTests` / `SiblingMethodDispatchTests`
  deliberately exercise only the sync slot), and the FailFast-on-throw path *terminates the test
  runner* by design, so neither can be an in-process assertion. The existing sync-dispatch tests
  remain the durable structural/slot coverage; the exception policy is observable only at the
  emitter layer.

---

## Item 5 — Defect I: AsyncStream bridge tactical fix

### Current state (`SwiftAsyncStream.cs`)
- (a) `_thisHandle = GCHandle.Alloc(this)` in `GetContext()` (`:91–95`), freed **only** in
  `Dispose()` (`:185–188`). No finalizer. `OnComplete` (`:142–145`) only
  `_channel.Writer.TryComplete()` — does not free the handle. The generated surface returns
  `IAsyncEnumerable<T>`; `await foreach` disposes the *enumerator*
  (`GetAsyncEnumerator`/`:150`), never the stream → **every access leaks a Normal GCHandle +
  stream for process lifetime.**
- (b) `FromContext` (`:196–208`) does `GCHandle.FromIntPtr(...).Target as …` guarded only
  against an invalid handle, not a recycled slot.
- (c) `GetElementCallback`/`GetCompletionCallback` (`:69–83`) call `ThrowIfDisposed()`; the
  element/complete UCOs are catch-free (Item 2) → the runtime's own guard can throw inside an
  unguarded UCO.
- (d) `Cancel()` (`:165–170`) only `_cts.Cancel()` + closes the channel; the Swift producer
  Task has no cancel registration — a Swift stream suspended awaiting the next element never
  sees the cancel.
- (e) `AsyncThrowingStream` is accepted by the type gate (`AsyncStreamHandler.IsAsyncStream`)
  identically to `AsyncStream`; `IsThrowingStream` has zero production callers; marshal failure
  mid-stream is `return false; // Stop on error` (`:135`) — silent truncation.

### Fix shape (tactical; redesign is Session 13)
1. **Handle lifecycle owned by completion (a).** Free `_thisHandle` when the stream completes
   — `OnComplete` is the last Swift→C# callback, so freeing there is safe (Swift will not call
   back after completion). Idempotent one-shot free via `Interlocked.Exchange(ref _handleFreed,
   1)` so completion + a channel fault + Dispose can race without a double-free. Element UCOs
   no-op once freed (guarded by the same flag) so a late `OnElement` cannot deref a freed handle
   (closes (b) for the in-order case). **As-built note:** the planned `~SwiftAsyncStream`
   finalizer backstop was *dropped* — see As-built below. The strong `GCHandle.Alloc(this)`
   roots the instance for its whole live span, so a finalizer is unreachable dead code; the
   `await foreach`-only path is closed by completion freeing the handle, not a finalizer.
2. **Producer cancel registration (d).** *Deferred to Session 13 — see As-built below.* Session
   2 ships the C#-side tactical minimum only (`SignalProducerStop` = `_cts.Cancel()` +
   `_channel.Writer.TryComplete()`), which unblocks the consumer and stops an *active* producer
   at its next element boundary. Registering the Swift producer `Task` with the cancellation
   registry (`SBW_CancelTask`) to stop a *suspended* producer is the Session-13 redesign.
3. **Reject throwing streams with a diagnostic (e).** `AsyncStreamHandler.IsAsyncStream` must
   stop matching `AsyncThrowingStream`; route it to a fail-closed `SWIFTBINDxxx` diagnostic
   (member skipped with a clear message) instead of half-binding it as a non-throwing stream
   with no error path. The element/complete UCOs get the `StreamFault` policy (Item 2) so a
   marshal failure faults the channel rather than truncating silently or crashing.

### Open risks / questions
- Freeing the handle at completion vs the consumer still draining the channel: the channel
  holds copied elements (`ExtractCopiedValue`), so the consumer can drain after the handle is
  freed — the handle only gates **Swift→C# callbacks**, which stop at completion. Verify no
  path reads `_thisHandle` after completion except a (now-guarded) late `OnElement`.
- Finalizer + `GCHandle` interaction on NativeAOT vs Mono — assert on both (⚠device).
- Producer-cancel wiring depth: the AsyncStream Swift wrapper may not currently start a
  registry-tracked Task. If wiring a full registry registration is larger than tactical,
  fall back to the minimum that makes `Cancel()` observable on the Swift side and record the
  remainder for Session 13. Decide concretely once the AsyncStream Swift emitter
  (`AsyncStreamEmitter.EmitSwiftWrapper`) is read during implementation.

### Footprint
`SwiftAsyncStream.cs` (completion-owned one-shot free, guarded element delivery, channel
fault — **no finalizer**), `AsyncStreamEmitter.cs` (UCO guards via Item 2),
`AsyncStreamHandler.cs` (`IsAsyncStream` rejects throwing) + `MemberEmissionValidator.cs` /
`PropertyHandler.cs` (throwing-stream skip), `BindingReport.cs` /
`WorkaroundRecommendations.cs` (the `UnsupportedThrowingAsyncStream` reason).

### Tests (red-first)
- **Runtime:** `await foreach` over a completed stream frees the GCHandle (assert via
  `GCHandle.IsAllocated` / `LifetimeTracker` — no leak); a never-disposed, never-completed
  stream is collected and finalized (handle freed). A late `OnElement` after completion is a
  safe no-op (no freed-handle deref).
- **BindingTests:** an AsyncStream consumed via `await foreach` (the leak path) over many
  iterations shows no unbounded GCHandle growth; `Cancel()` mid-stream stops the Swift
  producer; an `AsyncThrowingStream` member is rejected with the diagnostic at generation (no
  silent half-bind).

### As-built (2026-06-13) — **DONE**
Shipped behavior, where it diverged from the plan above, and why:
- **No finalizer (diverges from plan item 1).** `GetContext` allocates a strong (Normal)
  `GCHandle.Alloc(this)` that roots the instance for its whole live span; a `~SwiftAsyncStream`
  finalizer can therefore never run while the handle is allocated → it would be unreachable dead
  code. The `await foreach`-only leak (the plan's reason for the backstop) is closed by
  **completion freeing the handle**, not a finalizer. Matches the repo's leak-over-UAF policy
  (KVO omits a finalizer; SwiftClosure leaks). Residual: a stream that is dropped without ever
  completing *or* being disposed leaks one handle+instance — punted to Session 13 with producer
  cancel.
- **Free owned by completion *only*; neither fault nor Dispose frees.** `Complete` is the sole
  caller of `FreeContextHandleOnce` (`Interlocked.Exchange(ref _handleFreed, 1)`, one-shot).
  `Dispose` deliberately does **not** free — an in-flight callback could still resolve the handle,
  and an early free engages the GCHandle cookie-recycling hazard. Safe because the emitted Swift
  wrapper is a **single sequential `Task`** (`for await { elementCallback } … completionCallback`):
  element and completion callbacks are serialized on one task, and the wrapper *always* runs
  `completionCallback` after the consume loop breaks, so completion is the last callback per
  context and there is no concurrent free-vs-use and no cross-stream recycled-cookie window.
  **Correction (post-review, see below): `FaultChannel` originally also freed; that was a defect** —
  it is reachable from a *non-last* element trampoline (a mid-stream marshal fault returns "stop",
  the wrapper breaks the loop, then still calls `completionCallback`), so freeing in `FaultChannel`
  dropped the rooting handle while the trailing completion could still resolve the context,
  reopening the recycle window. `FaultChannel` now only faults the channel; the trailing
  `Complete` performs the (now no-op `TryComplete` +) free for the faulted run exactly as for a
  clean finish.
- **Producer cancel is C#-side only (diverges from plan item 2 — deferred).** No Swift-side
  registry registration this session. `DeliverElement` reads **no `_cts` state**: after `Dispose`
  disposes `_cts`, a `_cts.Token` getter on the Swift executor thread throws
  `ObjectDisposedException`, which would route through the `StreamFault` catch into `FaultChannel`,
  turning a clean consumer-side dispose into a spurious error-completion the consumer observes.
  (Post-fix `FaultChannel` no longer frees, so this is no longer a recycle hazard — but a bogus
  internal exception reaching the consumer is reason enough to read no `_cts` state here.)
  Producer-stop is delivered purely via channel completion (`SignalProducerStop`'s
  `TryComplete`); a suspended Swift producer is unblocked only at its next element boundary
  (full stop → Session 13).
- **Throwing-stream rejection (plan item 3) shipped fail-closed at both layers.**
  `AsyncStreamHandler.IsAsyncStream` no longer matches `AsyncThrowingStream`; `IsThrowingStream`
  gates it out at `MemberEmissionValidator` *and* `PropertyHandler` with
  `SkipReason.UnsupportedThrowingAsyncStream`. The sibling non-throwing property still emits.
  Element/complete UCOs carry the Item-2 `StreamFault` policy → a marshal failure faults the
  channel rather than truncating silently.
- **`_disposed` is `volatile`** for cross-thread visibility consistency with `_handleFreed`
  (best-effort early exit; a stale read is benign — see the field comment).
- **Post-hoc review.** Grok lifetime review (sessionId `019ec3ee-53dd-7362-a256-c7aeb9a4cc9d`,
  r2+r3). My own parallel review found and fixed the `_cts.Token`-on-Swift-thread
  `ObjectDisposedException` hazard above (Grok's abstract free-race framing missed it; Grok r3
  verified the fix). Remaining Grok Highs are the Session-13 residuals (suspended-producer
  cancel) — confirmed pre-existing scope, not regressions.
- **End-of-Session-2 paired review (Codex `019ec448-6dab-7f40-a518-6e3ada1da5e7`, Grok
  `019ec448-a620-70b2-b5dd-ebfe400648a6`).** Grok's four Highs were all by-design as-built (no
  action). Codex surfaced one genuine High: `FaultChannel`'s early free on a mid-stream element
  fault (detailed in the "Free owned by completion *only*" correction above) — a regression
  introduced by this session's Defect I work. Fixed at root cause (free is completion-only);
  `DisposeSafetyTests.SwiftAsyncStream_FaultChannel_DoesNotFreeContextHandle_CompletionDoes`
  is the regression guard. Re-verified green: unit 12908/0, runtime-unit 650/0, sim 2833/0/0,
  device 2845/0/0.
- **Codex verifying re-review (r2, same session).** Confirmed the AsyncStream fix resolved (free is
  now `Complete()`-only). Surfaced three new items, triaged:
  - *High — async completion callback `handle.Free()` is not idempotent* (`AsyncHarnessEmitter.cs`
    success/error callbacks; `AsyncMethodGenericBridgeEmitter.cs` ditto). **Judged not a defect.**
    The emitted Swift wrapper (`WrapperEmitter.Async.cs:1543-1581`) is a single
    `Task { do { … callback(_sbwTask) } catch { errorCallback(_sbwTask) } }`: exactly one of
    {success, error} fires exactly once per `task` cookie (success is the last `do` statement; a
    `@convention(c)` callback can't throw a Swift error into the `do`; the C# UCO never propagates
    across the boundary; cancellation is cooperative — `task.cancel()` sets a flag, never a second
    native invocation). So the GCHandle has a *single* freer and `handle.Free()` is intentionally
    not idempotent. The asymmetry with the TCS (which IS idempotent via `Try*`) is correct: the TCS
    has two real writers (C# `TrySetCanceled` + native `TrySetResult`); the free has one. A silent
    idempotent free would mask a future genuine double-callback rather than fault on it. Codex's
    repro shape requires hand-forging a double native callback the generator cannot emit. Captured
    as an emitter-source contract comment at the `AsyncHarnessEmitter` callback site.
  - *Medium — `UcoGuardEmitter` `ResumeBoxError` throws `NotImplementedException`* (`:135-140`).
    **By design** — a fail-closed seam; Finding 37 emits the async-closure resume envelope inline
    and no caller routes through `EmitClose` with that policy. No action.
  - *Low — `CancellationRaceTests` can pass vacuously when `started == 0`.* **Fixed** with a
    meaningfulness gate (`AssertTrue(started > 0, …)`) so an all-cancelled-before-launch run fails
    loudly instead of passing without exercising the race.
  No r3: no new High was acted on (the r2 High was judged not-a-defect; only a Low test-hardening
  edit was made), so the re-review loop terminates per the fix-gated cadence.
- **Low-fix gate (sim):** after the `started > 0` meaningfulness gate, a clean full sim run is
  green at the baseline — 2833 pass / 0 fail / 38 skip / 0 crash — with both `CancellationRaceTests`
  cases passing (`TestConcurrentCancelRace_NeverLosesACancel` confirms `started > 0` holds: the
  concurrent stress launches at least one Swift task). No device leg: the edit is a managed-only
  test assertion with no ABI/marshalling surface, and the substantive AsyncStream correctness fix
  was already device-green (2845/0/26 skip/0 crash); `started > 0` rests on the runtime-invariant
  synchronous-launch-before-return semantics of the generated async wrapper.

---

## Cross-cutting build/test discipline
- After **every** generator-source edit, rebuild the Debug generator dll
  (`dotnet build src/Swift.Bindings/src -c Debug`) before any gate — `nuke binding-tests` /
  `nuke validate` run the prebuilt `bin/Debug` generator and never rebuild a stale one.
- Any **new reverse-dispatch test protocol** added in BindingTests must be added to
  `PreservedProtocols` in `build/Helpers/SwiftSourceStripper.cs`, or the harness strips the
  witness getter → `EntryPointNotFoundException`.
- Closing gate matrix (generator/emitter + runtime change): `nuke test` →
  `nuke binding-tests --compile-only` → `nuke binding-tests` (sim) → `nuke binding-tests
  --device` (⚠device — Mono and NativeAOT have different async/marshalling bugs). `nuke
  validate` is optional (cross-cutting only); this change is async-path-specific, so it is not
  required unless the post-hoc review surfaces category-wide risk.

## Review questions (for the Grok design pass + Codex on 37/36)
1. **Finding 37 box claim:** is a per-box `NSLock`-guarded resume-once acceptable, or does the
   C# ResumeScope's serialization make a lighter `takeUnretainedValue`-read claim sufficient?
   Is there any residual free-before-read window?
2. **Finding 37 Dispose ordering:** is "resume success, then dispose in a swallow-and-log
   finally" the right ordering, or should the disposable be marshalled/copied and disposed
   *before* the resume entirely?
3. **Finding 36 policy:** is guarded-block + (4a) cancellation-specific loud FailFast the
   right Session-2 minimum, or should OCE be made non-fatal even for non-throwing requirements
   (which appears to require fabricating a value or building the Session-13 error channel)?
4. **Finding 38 validator altitude:** unit-test corpus scan vs structural "all UCOs through
   the envelope" — which matches the project's durability bar without over-reaching into
   Session 13?
5. **Defect I producer cancel:** how much producer-cancel wiring is genuinely *tactical*
   versus Session-13 redesign, given the AsyncStream Swift wrapper's current shape?

## Review outcome

Dual design review run 2026-06-13 (Grok full-plan; Codex on Findings 37/36). Sessions:
- **Grok sessionId:** `019ec34f-25fb-7642-93dd-94a0c6d1165e`
- **Codex Session:** `019ec34e-e5a1-7881-9d7a-a653ce4508cf`

Both endorsed all five fixes as correctly shaped, minimal, and properly scoped, and confirmed
every file:line claim and the plan's own corrections (39 four registration sites + single
flagless TCS at `AsyncGenericParent.cs:906`; 37 double-/never-resume; 38 KVO fail-soft + 32
catch-free UCOs; 36 OCE hole; Defect I leak/catch-free/throwing-stream). Codex raised three
findings that change the plan; synthesized decisions below (best-of-three, not wholesale).

**Decision 1 — Finding 37: drop the Swift-side once-flag; C# `ResumeScope` is the sole
guarantee (Codex High, overrides plan + Grok "backstop sound").** Codex correctly refutes the
Swift backstop: a once-flag stored *inside* the box cannot guard the box's own liveness — the
loser must `fromOpaque(boxPtr).take…Value()` (deref the box) *before* it can reach any in-box
flag/lock, and if the winner already released the `+1` the box is freed, so that is a read of
freed memory. The flag's storage **is** the freed memory; a per-box `NSLock` cannot protect the
raw-pointer-to-object recovery. Resolution: the `_success`/`_error` `@_cdecl` symbols are *only*
ever called from C#, so the **C# `ResumeScope` (Interlocked claim) is the airtight,
single-resume guarantee** — serialize to exactly one call managed-side and the box is consumed
exactly once; there is never a second raw-pointer recovery. No Swift-side flag is added (it
would be false safety with zero value: if C# serializes, the loser never fires; if C# has a bug,
the Swift flag can't save a freed box anyway). Root cause stays C#-side (the
Dispose-in-finally → ReportError → second resume path) and is fixed by gating both actions
behind the ResumeScope claim and reordering the result-Dispose so a throwing Dispose can't
re-enter error. This answers review questions 1 and 2: no per-box lock, no residual
free-before-read window, and the disposable is handled so a post-success Dispose failure can
never un-resume the box.

**Decision 2 — Finding 36: split policy by throwing-ness (Codex High #2 + Medium).**
- *Non-throwing `async`* → (4a) cancellation-specific, member-named loud FailFast for OCE +
  existing FailFast for other faults. Both reviewers confirm this is the honest tactical
  behavior ("no honest non-fatal OCE behavior for a non-throwing requirement in the current
  receiver ABI"). Answers review question 3: (4a), not a fabricated value.
- *`async throws`* → **gate out at generation** with a named SkipReason. Codex High #2 + the
  `MemberEmissionValidator`/`WitnessDispatchEmitter.IsMethodWitnessDispatchEligible` read
  confirm async-throws is *not* gated today (it binds, then FailFasts on throw, silently
  violating the `throws` contract the Swift signature advertises). Honest build-time rejection
  beats process-death on first throw. Corpus has none → zero-regression (confirm during TDD;
  the full error channel is Session 13). This resolves the Item-4 open question.
- *Medium (comment)* → fix the "no SynchronizationContext, so blocking cannot self-deadlock"
  comment (`Receivers.cs:1124`): it does not cover main-actor/executor-starvation deadlock.
  Reword honestly and ensure the Session-2 diagnostic never implies the sync-blocked path is
  deadlock-safe.

**Decision 3 — Findings 39 / 38 / Defect I: proceed as planned**, with two doc-hygiene fixes
both reviewers surfaced: (1) the Issue-1 linkage belongs with the *reverse-async assertion*, NOT
`upstream-issue-01` (a different symptom — CallConvSwift unwind assert after a native crash);
(2) the Finding-38 validator scopes to generated `output/*.cs` (the "32" is corpus-derived; a
structural "all UCOs through the envelope" rule would over-reach — answers review question 4).
Adjacent note (Grok/Codex Medium, no Session-2 action beyond a doc pointer): the CSM
parent-only async UCOs (`AsyncGenericParent.cs` ~754) use an inner `catch { /* swallow */ }`
defense-in-depth that routes to `TCS.TrySetException` — not catch-free, but a guard deviation;
tightening to a clean fault is part of Session-13 policy unification. Producer-cancel for
Defect I (review question 5) is confirmed >1 line (the AsyncStream Swift wrapper emits an
unregistered anonymous `Task`); wire the registry registration if tactical, else fall back to
the minimum that makes `Cancel()` observable and record the remainder for Session 13.

No design-phase r2: the re-review loop verifies fixes-to-*code* (the post-hoc paired review),
and the design gate (plan + dual review before code) is satisfied. Proceed to TDD red-first in
order 39 → 38 → 37 → Defect I → 36.

## Deferred / split-out units
- **CSM parent-only async specialization has no cancellation** (no `CancellationToken`, no
  registry registration) — only the `RunContinuationsAsynchronously` flag is fixed in Session
  2. Full cancel wiring → Session 13 harness consolidation.
- **Finding 36 permanent answer** (real async witness on the continuation-handoff machinery;
  executor-starvation + MainActor-reentry hazards) → Session 13.
- **Defect I full redesign** (first-class async shape on the shared harness; throwing-stream
  *support* rather than rejection) → Session 13.
- **Finding 38 full policy unification** (error-channel + TCS-fault sites through one
  structural envelope) → Session 13, if the Session-2 enum consolidation proves insufficient.
