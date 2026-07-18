# Phase 8b — PatParent async-cancel sim SIGSEGV: root cause + in-tree handling

## Task
Root-cause and fix two deterministically-crashing BindingTests on the iOS Simulator (Mono JIT):
- `PatParentAsyncMethodsTests.TestAsyncBagMockStringItem_CancelRespondAsyncSurfacesCancellation`
- `PatParentAsyncMethodsTests.TestAsyncBagMockIntItem_CancelRespondAsyncSurfacesCancellation`

Crash: `EXC_BAD_ACCESS`/`SIGSEGV`, fixed pointer-auth-tagged fault address `0x038c1a03881a036c`, in Mono's arm64 exception unwinder.

## Root cause (PROVEN upstream — Mono, not our code)
The fault is a Mono iOS-Simulator arm64 exception-unwinder bug: throwing an `OperationCanceledException` out of an already-canceled, shared **reference-type** generic `Task<TResult_REF>` — awaited through the generic `WithTimeout<T>` helper whose continuation resumes on the UIKit main-thread `NSAsyncSynchronizationContextDispatcher` — faults inside `mono_arch_unwind_frame` ← `mono_find_jit_info_ext` ← `mono_handle_exception` ← `mono_arm_throw_exception` ← `TaskAwaiter<TResult_REF>.GetResult`. There is **no Swift / generated-binding / P-Invoke frame** anywhere on the faulting stack — an ordinary managed throw, not a `CallConvSwift`/signal-handler unwind (so NOT Issue 1).

Ironclad, Swift-independent proof: swapping the interop `CancelRespondAsync()` call for a pure-managed canceled `TaskCompletionSource<StringResponse>.Task` — everything else identical, zero Swift on the await/throw path — crashes **identically** (same test, position, fixed fault address). The fixed fault address across every observed crash indicates a fixed bad-pointer read in the unwinder, not random use-after-free.

Layout/load-sensitive heisenbug: reproduces only under accumulated full-suite JIT load; adding/removing unrelated sibling methods relocates JIT code and toggles it; passes in single-class isolation. A clean full-regen full-suite run also crashes, ruling out a stale-build artifact.

Independent triage concurred it is not an interop/ABI defect but an upstream Mono unwinder fault: Codex `019f771f-80bd-79b2-bba5-792584018123`, Grok `019f7725-3b54-7b90-b98b-19a4c5f6b5bb`.

## Why skip, not fix
The defect is in Mono's arm64 exception unwinder, reachable from pure-managed code with zero interop on the faulting stack. There is nothing in the generator, emitter, or runtime to fix — the P/Invoke, calling convention, param count/types, entry point, and cancellation bridging on the interop path were all verified correct, and the Swift error callback has fully returned (the Task is already `Canceled`) before the fault. Per the guilty-until-proven-innocent policy, this cleared the bar: proven upstream with a pure-managed repro.

## In-tree handling (team-lead Decision: Option A)
- `[SkipOnMonoJit(...)]` on exactly the two cancel tests, with an inline reason describing the full mechanism (no doc references in code). Runtime-detected & Mono-only: **macOS CoreCLR and device NativeAOT keep running them** (different unwinders).
- Authoritative memory list updated: `feedback_mono_jit_blame.md` Issue 5 (symptom, fixed PAC address, scope, layout/load sensitivity, pure-managed proof, Codex/Grok session ids, status "proven-upstream but NOT yet filed — owner action", verified 2026-07-18).
- Owner filing package written: `src/docs/Future/upstream-issue-05-mono-unwinder-oce-pac.md` (+ README index row). Submission-ready dotnet/runtime report with minimal pure-managed repro, crash signature, `.ips` evidence, environment. **Not filed** — dotnet/runtime filing is owner-only; a standalone `swift-interop-repro` reduction should be authored first.
  - Filename note: used `upstream-issue-05-*` to match the existing `upstream-issue-0{1..4}-*` convention + README index, rather than the literal `mono-unwinder-oce-pac-crash.md` named in the directive.

## Gates
- Sim leg (`nuke binding-tests --skip-regen`): **3242 pass, 0 fail, 37 skip, 0 crash** — both cancel tests SKIP via runtime detection; the two SIGSEGVs are gone with zero new failures.
- Runtime-identity + validation baselines reseeded to current-reality HEAD (`--seed-runtime-identity-baseline`): sim pass 3192→3242, +2 PatParent skips; also captured 2 pre-existing-drift `ClosureTests` SetterOnly tests that now pass (stale baseline had never been reseeded since git_sha 19aa99af).
- Unit gate (`nuke test`): **Swift.Bindings.Unit.Tests 15141 pass, 0 fail** (≥15,141), Analyzers 35/35, Runtime 719/719.

## Not chased (noted for visibility, per directive)
- `BorrowedCallbackArgLeakProbeTests` 999/1000 flake — not investigated.

## Device-leg recommendation
Device leg untouched by design — NativeAOT uses a different unwinder and is not expected to reproduce; the two tests keep running there. A `--device` run is *not* required to validate this change, but is worth a routine pass at the next device cadence to confirm the NativeAOT path stays green.

## Environment
.NET SDK 10.0.107, Xcode 26.3 (build 17C529), iOS-sim SDK 26.2, macOS 26.2, Apple Silicon arm64.
(Team-lead cited "Xcode 26.2.10233"; observed values recorded here.)
