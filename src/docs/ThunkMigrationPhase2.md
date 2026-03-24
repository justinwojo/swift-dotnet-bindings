# Design Doc: Native ARM64 Thunks — Phase 2

**Status**: Session 3 complete
**Prerequisite**: Phase 1 complete (see `Completed/ThunkMigration.md`)

## Overview

Phase 1 delivered the thunk pipeline end-to-end: type lowering → assembly codegen → generator integration → build pipeline → CallConvSwift elimination. Several patterns were deferred to @_cdecl because the assembly emitter or P/Invoke signature builder couldn't handle them correctly. This document tracks those deferred items plus runtime validation.

All deferred items are safe — they fall back to @_cdecl wrappers which work correctly. These are optimizations that would reduce Swift compiler dependency and improve performance for more method patterns.

## Current State

After Session 3, the thunk pipeline is stable with zero runtime crashes:

| Metric | Before Phase 2 | After Session 3 |
|---|---|---|
| NativeThunk | 0 (broken) | **~700** (incl. constructors) |
| Unit tests | 9,249 pass | 8,822 pass |
| Library validation | 90/90 | 90/90 |
| Runtime (simulator) | — | 894 pass, 0 crashes |

The remaining CallConvSwift P/Invokes are genuinely un-thunkable: generic methods, inout params, protocol-constrained dispatch, and type metadata accessors.

## Work Order

| Order | Session | Scope | Complexity |
|---|---|---|---|
| ~~1~~ | ~~Runtime Validation~~ | ~~Critical~~ | ~~Done~~ |
| ~~2~~ | ~~Assembly Register Mapping~~ | ~~Setter crash fix + multi-register struct self~~ | ~~Done~~ |
| ~~3~~ | ~~Constructor Thunks~~ | ~~Class constructors (struct deferred to S4)~~ | ~~Done~~ |
| 4 | Indirect Result Returns | P/Invoke indirect return + struct ctors + multi-slot | Medium |

Sessions 2-4 are independent and can be done in any order.

---

## Session 1: Runtime Validation (COMPLETE)

**Three bugs found and fixed:**

### Bug 1: TBD symbol lookup prefix mismatch
`IsSwiftCallTargetExported()` in `NativeThunkEmitter.cs` added `_` prefix before checking `ExportedSymbols`, but the TBD parser strips `_` when populating `ExportedSymbols`. Lookup was `_$s...` vs stored `$s...` — **never matched**. Rejected 846/1239 methods (68%). One-line fix: removed the prefix.

### Bug 2: P/Invoke entry point double-underscore
`GenerateThunkSymbol()` returned `_thunk_{module}_{hash}`. On Apple platforms, .NET prepends `_` to entry points before calling `dlsym`. So `dlsym` looked for `__thunk_...` — **EntryPointNotFoundException** for all thunks. Fix: symbol returns `thunk_{module}_{hash}` (C name without prefix); assembly emitter adds `_` for Mach-O convention.

### Bug 3: IsSelfTypeLowerable optimistic fallback
When a frozen struct's TypeRecord had null `InlineSize`, the check fell through to `return true`. Large frozen structs (e.g., 32-byte LottieColorLike) were thunked when they shouldn't be → **SIGSEGV** on property access. Fix: reject when InlineSize is unknown.

### Also fixed
- BindingTests `build-async-wrapper.sh` and `build-wrapper-device.sh` weren't compiling `.arm64.s` thunk assembly files into the wrapper framework. Added `clang -c` + object file linking. (Internal test infrastructure only — the programmatic `SwiftWrapperCompiler.cs`/`NativeThunkCompiler.cs` pipeline already handles this for end users.)
- Removed stale `[Skip]` attributes on tests where CallConvSwift was replaced by thunks (`TestAnimationCacheClear`).
- Updated skip reasons on async tests to reference the actual blocker (upstream Mono async Issue 1), not the now-fixed CallConvSwift.

### Remaining: Class property setter crash (→ Session 2)
Runtime testing passed 29 tests then crashed on `FinalPropertyHolder.IntValue` setter. The thunk's register mapping for class instance method setters is incorrect — value + self parameter shuffling is wrong. Tracked as Session 2.

**Validation**: 9,249 unit tests pass, 90/90 library validation, simulator runtime: 29 pass before crash (crash is Session 2 scope).

---

## Session 2: Assembly Register Mapping (COMPLETE)

Parts A and B complete. Part C (stretch goal) assessed and deferred to Session 4.

### Part A: Class Property Setter Crash Fix

**Root cause**: `EmitFullFrame()` assumed cdecl puts self in x0 (first param), but `CdeclSignatureContract` orders `[Arguments] [Metadata] [Self] [ErrorOut]` — self is LAST. For a setter with 1 value param, cdecl had `x0=value, x1=self`, but the thunk did `mov x20, x0` (moved value to x20 instead of self), then `mov x0, x1` (moved self to x0 instead of value). Both registers wrong → SIGSEGV.

**Fix**: Changed self handling from `mov x20, x0` + parameter shift to `mov x20, x{ParameterCount}` with no shift. Value params are already in the correct swiftcc registers (x0..xN-1); only self needs to move to x20 from its position after all value params. Removed `EmitParameterShift()` call.

**Modified files**:
- `ThunkAssemblyEmitter.cs` — Self handling in `EmitFullFrame()`: `mov x20, x{ParameterCount}`, no param shift
- `ThunkAssemblyEmitterTests.cs` — Updated 3 existing tests, added 4 new tests (getter 0-param, setter Int, setter Double, explicit 0-param self-at-x0)

### Part B: Frozen Struct Self >8B

**Finding**: The Phase 1 rejection of >8B frozen struct self was based on the misconception that self decomposes across x20+x21+... registers. In reality, x21 is `swifterror` (not self overflow), and x20 is the ONLY self register via LLVM's `swiftself` attribute. For >8B value types, swiftcc passes self indirectly — a pointer in x20. PInvokeEmitter already emits `IntPtr` for self on all thunked methods (`UsesNativeThunk` path, line ~639). The thunk's `mov x20, x{ParameterCount}` forwards the pointer correctly. Field layout (int/float mix) is irrelevant — the thunk passes a pointer, not decomposed registers.

**Fix**: Removed the `InlineSize > 8` rejection in `IsSelfTypeLowerable()`. All frozen struct sizes with known InlineSize are now accepted.

**ABI caveat (pre-existing, not introduced here)**: For ≤8B frozen structs, swiftcc expects the VALUE in x20 (not a pointer), but PInvokeEmitter still passes IntPtr. This was accepted since Phase 1. Safe in practice: ≤8B frozen struct methods are `@inlinable`, have no TBD export, and are filtered by `IsSwiftCallTargetExported()` before reaching assembly emission. No ≤8B frozen struct instance methods are thunked in any validation library.

**Modified files**:
- `NativeThunkEmitter.cs` — `IsSelfTypeLowerable()`: removed >8B gate, added ABI doc comment
- `NativeThunkEmitterTests.cs` — Replaced 3 size-specific tests with: >8B theory (16/24/32), ≤8B fact with caveat comment, unknown-size rejection, static-method pass-through, float-field 32B, float-only 16B

### Part C: Multi-Slot Value Parameters (Assessed, Deferred)

Multi-slot value params (e.g., CGPoint 16B, CGRect 32B as method arguments) are common in Apple APIs. For homogeneous types (all-int or all-float), cdecl and swiftcc register allocation matches — no remapping needed. For mixed types (e.g., `{Int, Double}`), cdecl puts both in integer registers but swiftcc splits across int/float — needs `fmov`. Enabling this requires changes to both `AreAllParametersLowerable()` (gate) and parameter counting in `EmitThunk()` (multi-slot params occupy multiple cdecl registers but count as 1 logical param). Deferred to Session 4.

### Validation

| Gate | Result |
|---|---|
| Unit tests | 9,256 pass, 0 fail |
| Library validation | 90/90 pass |
| Runtime (simulator) | 167 pass (up from 29), 1 pre-existing failure, 1 pre-existing crash |

Runtime improvements: The Session 1 setter crash at test #30 (`TestFinalPropertyHolderIntSetGet`) is fixed. All FinalPropertyHolder setter tests now pass (Int, Float, String, Bool, Summary). Execution proceeds to test #168 where a pre-existing crash occurs in `BasicProtocolDispatchTests.TestPriorityHandlerGetPriority` (witness dispatch with `SwiftIndirectResult` — protocol dispatch, not thunks).

---

## Session 3: Constructor Thunks (COMPLETE)

Class constructors enabled. Struct constructors deferred to Session 4 (x8 indirect return).

### Class Constructor Thunks

**Finding**: Class constructors are simpler than expected. The existing codegen already handles them without indirect result — `MethodRequiresIndirectResult` returns false for class constructors because classes return pointers directly in x0 (single register). The P/Invoke returns IntPtr, and `EmitReturnConstructor` wraps it in `SwiftClassHandle`. This is identical for both @_cdecl and thunk paths.

**Key insight**: No changes needed to PInvokeEmitter, MethodMarshalPlanBuilder, or WrapperEmitter. The existing class constructor codegen works with thunks out of the box — the only difference is the entry point symbol (thunk vs @_cdecl) and calling convention (both are CallConvCdecl).

**Fix**: Replaced the blanket `if (methodDecl.IsConstructor) return false` rejection in `ShouldEmitThunk()` with targeted checks:
- **Struct constructors**: Rejected (x8 indirect return — deferred to Session 4)
- **Failable constructors** (init?): Rejected (return Optional<Self>, needs indirect result)
- **Generic type constructors**: Already rejected (existing check at line 75-77)
- **Class constructors**: Now accepted — allocating init returns pointer in x0

The ConstructorHandler and MethodHandler were updated to try thunk first, with fallback to @_cdecl if thunk emission fails (metadata accessor missing, lowering failure, etc.).

**Modified files**:
- `NativeThunkEmitter.cs` — Replaced blanket constructor rejection with struct/failable checks
- `MethodHandler.cs` — Thunk routing in both ConstructorHandler.Emit and MethodHandler.Emit, including thunk assembly emission and fallback-to-@_cdecl logic
- `NativeThunkEmitterTests.cs` — Updated `ShouldEmitThunk_Constructor_ReturnsFalse` → `ShouldEmitThunk_ClassConstructor_ReturnsTrue`, added 8 new tests (struct ctor rejection, failable rejection, class ctor with params, thunk assembly metatype setup, param save/restore, closure param rejection, SwiftCallTargetResolver no-Tj)
- `ConstructorHandlerOutputTests.cs` — Updated `Emit_PrimaryClassConstructor_EmitsCdeclSwiftWrapper` → `Emit_PrimaryClassConstructor_UsesNativeThunk` (class ctors now thunked), added `Emit_ClassConstructorWithClosureParam_FallsBackToCdecl`

### Struct Constructor Assessment

Struct constructors use x8 for indirect return. The thunk assembly can bridge this (it already handles `mov x19, x8` for 17-32B frozen struct returns), but the C# P/Invoke side can't express the return correctly under CallConvCdecl — `SwiftIndirectResult` maps to x8 only under CallConvSwift. This is the same x8 research question as Session 4. Deferred.

### Bug Fix: SwiftIndirectResult under CallConvCdecl → SIGSEGV

**Root cause**: `ShouldEmitThunk()` did not check `MethodRequiresIndirectResult()`. Methods returning complex enums, non-frozen structs, and other types requiring indirect result were thunked despite the P/Invoke adding `SwiftIndirectResult`. Under CallConvCdecl, `SwiftIndirectResult` becomes a regular parameter (x0), but the thunk reads x8 (AAPCS64 indirect return convention) → SIGSEGV.

This was the cause of the Session 1/2 "pre-existing crash" at test #168 (`BasicProtocolDispatchTests.TestPriorityHandlerGetPriority`). The `SimplePriorityHandler.getPriority()` method returns `TaskPriority` (a String-based enum, non-blittable), was incorrectly thunked, and the SwiftIndirectResult/x8 mismatch caused the SIGSEGV.

**Fix**: Added `MarshallingHelpers.MethodRequiresIndirectResult(env)` check in `ShouldEmitThunk()`. Methods requiring indirect result now correctly fall back to @_cdecl wrappers. Guarded with catch-all since `GetTypeRecordOrThrow` can throw for ObjC types not in the database.

### Bug Fix: Throwing function x21 save/restore

**Root cause**: x21 is the `swifterror` register in swiftcc. Throwing Swift functions write the error pointer to x21. `EmitFullFrame()` didn't save/restore x21, so after a throwing call, the saved x21 was the error pointer (or garbage), corrupting the frame on function return.

**Fix**: `ThunkAssemblyEmitter.cs` — save x21 on function entry (`stp x20, x21, [sp, #-16]!`), restore before return (`ldp x20, x21, [sp], #16`). Frame size math updated accordingly.

### Bug Fix: NeedsReturnBridging overly permissive

**Root cause**: `NeedsReturnBridging()` returned `false` by default for types where `TryGetTypeRecord` failed (unknown types). This allowed thunking of methods with unlowerable return types that actually needed bridging → SIGSEGV.

**Fix**: Conservative default — `NeedsReturnBridging()` now returns `true` for unknown types, so the thunk correctly bails when `TypeLowering.LowerReturnType()` returns null.

### Bug Fix: Property getter dispatch thunk x8 indirect return

**Root cause**: Getter dispatch thunks (`vgTj`) use x9 for vtable lookup, preserving x8. The getter writes value-type results to `[x8]`. Our bridge thunk doesn't set x8 → SIGSEGV. Discovered by disassembling `Codec.format` getter dispatch thunk at 0xeb34 vs `SimpleModel.getValue()` method dispatch thunk at 0x9c784 — getters use x9, methods use x8 for vtable.

**Fix**: Added gate in `ShouldEmitThunk()` rejecting value-type-returning accessors that use dispatch thunks. Uses `SwiftCallTargetResolver.Resolve()` (single source of truth for Tj gating) to determine if the accessor goes through a dispatch thunk. Direct-dispatch getters (final method, final class) don't have this hazard and remain eligible.

### Bug Fix: Constructor class reference +1 ownership double-release

**Root cause**: Swift init parameters follow +1 owned convention for class references — the init body does `retain(param) → store → release(param)`, consuming the caller's reference. Our thunk passes +0 (raw pointer from C#), so the release underflows → double-release when both the C# GC and the object's deinit release the same reference. Crash manifested as `doDecrementSlow` SIGSEGV after GC collection.

**Discovery method**: Added `GC.Collect()` after each test class → narrowed to ConstructorParamTests. Per-test GC → crash at TestOptionalClassParamWithValue. Disassembled `LinkedNode` init body, confirmed retain+release pattern. Decoded stub table: 0x9dcb4 = swift_retain, 0x9dca8 = swift_release.

**Fix**: Added `HasClassReferenceParameters()` helper and gate in `ShouldEmitThunk()`. Rejects constructors with class reference parameters (including `Optional<Class>`) from thunking. Falls back to @_cdecl wrapper where Swift handles retain/release automatically.

### Bug Fix: Over-scoped accessor gate (Codex P1)

**Finding**: The initial accessor gate rejected ALL non-class-returning property getters, but the x8 hazard only exists for dispatch thunk getters (vgTj). Final getters on non-final classes, getters on final classes, and subscript getters with direct dispatch don't use Tj — they call the implementation directly, and TypeLowering handles standard swiftcc return correctly.

**Fix**: Narrowed the gate to check `SwiftCallTargetResolver.Resolve()` — only reject when the resolved symbol differs from the mangled name (Tj was appended). Added two new tests: `FinalGetterEnumReturn_ReturnsTrue`, `FinalClassGetterEnumReturn_ReturnsTrue`.

### Bug Fix: Stale emission report after thunk fallback (Codex P2)

**Finding**: `IncrementWrapperStrategy()` was called before `EmitThunk()` could fail and revert to @_cdecl. The emission report showed `NativeThunk` for methods that actually fell back to `CdeclConstructor` or `None`.

**Fix**: Moved `IncrementWrapperStrategy()` to after the EmitThunk/fallback block in both the constructor path (MethodHandler constructor handling) and the method path (MethodHandler.Emit).

### Modified Files (All Session 3)

- `NativeThunkEmitter.cs` — Constructor eligibility, indirect result gate, accessor dispatch thunk gate, class reference param gate, NeedsReturnBridging conservative default
- `MethodHandler.cs` — Thunk routing for constructors and methods, thunk-first with @_cdecl fallback, emission report placement fix
- `ThunkAssemblyEmitter.cs` — x21 save/restore for throwing functions
- `NativeThunkEmitterTests.cs` — 14 new tests across all gates
- `ConstructorHandlerOutputTests.cs` — Updated constructor tests for thunk-first routing

### Validation

| Gate | Result |
|---|---|
| Unit tests | 8,822 pass, 0 fail |
| Library validation | 90/90 pass |
| Runtime (simulator) | 894 pass, 103 skip, 1 pre-existing failure, **0 crashes** |

**Runtime improvement**: From 178 pass / 1 crash (initial Session 3) to 894 pass / 0 crashes. The pre-existing failure is `TestDataBufferGetFirstEmpty` (assertion failure in Optional return from empty DataBuffer — generator bug, not thunk-related).

---

## Session 4: Indirect Result Returns
<!-- commit: pending -->

**What**: Enable thunking for methods that return types requiring the indirect result pattern (SwiftIndirectResult). Also pick up struct constructor thunks (deferred from Session 3) and any multi-slot value parameter work deferred from Session 2 Part C.

**Why deferred**: The thunk assembly CAN bridge registers to x8 buffer (it already does this for 17-32B frozen struct returns). But the C# P/Invoke side can't express this cleanly:
- `SwiftIndirectResult` maps to x8 only under `CallConvSwift`
- Under `CallConvCdecl`, `SwiftIndirectResult` is a regular parameter (x0), not x8
- The @_cdecl pattern uses `IntPtr resultPtr` as an explicit parameter — but the thunk reads x8 from the cdecl struct return convention, not x0

**Modified files**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` — Return type handling for thunked indirect results
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/NativeThunkEmitter.cs` — Update eligibility gates for indirect result methods
- `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs` — Marshal plan for thunked indirect results
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/PInvokeEmitterTests.cs` — P/Invoke signature tests

**Implementation details**:
1. **Research first**: Can the P/Invoke return a correctly-sized blittable struct so .NET uses AAPCS64 x8 for the return buffer? This is the key question.
   - Create a test: P/Invoke with `CallConvCdecl` returning a `[StructLayout(Size=24)]` struct. Does .NET put the buffer address in x8? If yes, thunks work naturally.
   - If no: Investigate whether `SwiftIndirectResult` under `CallConvCdecl` actually uses x8. AAPCS64 uses x8 for all indirect struct returns regardless of calling convention annotation — it might work.
   - The experiments worktree (`/Users/wojo/Dev/swift-interop-repro/`) can be used for empirical testing.
2. If blittable struct return works: Generate a `[StructLayout(LayoutKind.Sequential, Size=N)]` return type for the P/Invoke, matched to the Swift return size. The thunk stores registers to [x8], .NET reads from the buffer. No explicit resultPtr parameter needed.
3. Types that would benefit: non-frozen struct returns, Optional<value-type> returns, string returns (in wrapper context), existential returns
4. Update `ShouldEmitThunk()` to accept these return types once the P/Invoke side supports them
5. If Session 2 Part C (multi-slot value params) was deferred, pick it up here — the register remapping patterns from Session 2 Parts A/B will inform the implementation.

**Note**: The x8 research findings here directly apply to struct constructors (deferred from Session 3). If blittable struct return works, enable struct constructors by removing the `env.ParentDecl is StructDecl` rejection in `NativeThunkEmitter.ShouldEmitThunk()`.

**Validation**: `./run-tests.sh` + `./validate-libraries.sh` + `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90`
