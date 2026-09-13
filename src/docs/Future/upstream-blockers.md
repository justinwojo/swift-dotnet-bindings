# Confirmed upstream blockers

These are the **only** confirmed upstream issues. There are exactly 5 — issues 1–4 are reproduced in the standalone `swift-interop-repro` sibling repo; issue 5 is proven upstream via a pure-managed A/B substitution inside BindingTests (standalone reduction still pending, owner files it). If a crash doesn't match one of these, it's our bug. See `feedback_mono_jit_blame.md` for the full investigation checklist.

| Filing | Issue | Blocked By |
|--------|-------|-----------|
| 1 | **Mono: JIT assertion `!ji->async` on CallConvSwift P/Invoke** | Fatal `jit-info.c:918` during stack unwinding through a `wrapper_managed_to_native_*` frame after a native crash in a `CallConvSwift` callee. Workaround: `@_silgen_name` Swift wrappers / avoid native crashes through `CallConvSwift` |
| 2 | **Non-blittable type rejection with CallConvSwift** | .NET runtime design limitation. Workaround: `@_cdecl` wrappers (~67% of P/Invokes require them — see `src/docs/Future/upstream-issue-02-non-blittable-callconvswift.md`) |
| 3 | **Mono: `Cannot transition thread from STARTING with DONE_BLOCKING` on a CallConvSwift P/Invoke** | Mono's managed-to-native wrapper parks its GC-safe-region cookie in a callee-saved register without excluding `x20`/`x21`, which `CallConvSwift` reserves for `SwiftSelf`/`SwiftError`, then overwrites it with the call's own arguments; the post-call `mono_threads_exit_gc_safe_region_unbalanced` then reads a garbage thread record. Affects **any** CallConvSwift P/Invoke passing an untyped `SwiftSelf` (self in `x20`) — which of those members are hit is decided by Mono's register allocator, not by the Swift signature (originally filed against `Set<T>.insert`'s tuple return; that scoping was a correlation, corrected 2026-09-08). The parallel `x21`/`SwiftError` arm is mechanically possible but unobserved, and a typed `SwiftSelf<T>` is not implicated. See `src/docs/Future/upstream-issue-03-mono-set-insert-done-blocking.md`. Workaround: `@_cdecl` Swift wrapper — a Cdecl signature has no reserved register to collide with |
| 4 | **Mono: Mac Catalyst x64 instability** | Mono-JIT instability specific to the Mac Catalyst x64 runtime. See `src/docs/Future/upstream-issue-04-mono-catalyst-x64-instability.md`. Workaround: Mono interpreter is the default for `--catalyst-x64` (`[SkipOnCatalystX64]` retained as escape hatch, no call sites) |
| 5 | **Mono (ios-sim arm64): exception-unwinder PAC fault on canceled shared-ref-generic `Task<T>`** | `EXC_BAD_ACCESS` in `mono_arch_unwind_frame` throwing `OperationCanceledException` out of a canceled `Task<T>` (reference-type `T`) on a UIKit sync-context continuation; zero Swift/P-Invoke frames on the faulting stack. See `src/docs/Future/upstream-issue-05-mono-unwinder-oce-pac.md`. Workaround: `[SkipOnMonoJit]` on the two affected cancellation tests |
| comment | **Mono: SafeHandle async lifetime** (tracking-issue comment, no standalone filing) | GC may collect SafeHandle during async suspension. Workaround: manual ARC retain/release or singleton pattern |

| Other | Status |
|-------|--------|
| **Non-Int32 enum raw values** | Blocked on Swift compiler: `.swiftinterface` strips integer raw values. No workaround. 1 skipped test. |

See the [filing guide](upstream-issues-README.md) and its linked reproductions. Filing remains owner-driven.
