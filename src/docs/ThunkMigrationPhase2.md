# Design Doc: Native ARM64 Thunks — Phase 2

**Status**: Session 2 complete
**Prerequisite**: Phase 1 complete (see `Completed/ThunkMigration.md`)

## Overview

Phase 1 delivered the thunk pipeline end-to-end: type lowering → assembly codegen → generator integration → build pipeline → CallConvSwift elimination. Several patterns were deferred to @_cdecl because the assembly emitter or P/Invoke signature builder couldn't handle them correctly. This document tracks those deferred items plus runtime validation.

All deferred items are safe — they fall back to @_cdecl wrappers which work correctly. These are optimizations that would reduce Swift compiler dependency and improve performance for more method patterns.

## Current State

After Session 1 bug fixes, the thunk pipeline is operational:

| Metric | Before Session 1 | After Session 1 |
|---|---|---|
| NativeThunk | 0 (broken) | **660** |
| CdeclMethod | 494 | 244 |
| CdeclProperty | 661 | 255 |
| CallConvSwift remaining | ~70 | 69 (3.1% of 2,252 P/Invokes) |
| Unit tests | 9,249 pass | 9,249 pass |
| Library validation | 90/90 | 90/90 |

The 69 remaining CallConvSwift P/Invokes are genuinely un-thunkable: generic methods, inout params, protocol-constrained dispatch, and type metadata accessors.

## Work Order

| Order | Session | Scope | Complexity |
|---|---|---|---|
| ~~1~~ | ~~Runtime Validation~~ | ~~Critical~~ | ~~Done~~ |
| ~~2~~ | ~~Assembly Register Mapping~~ | ~~Setter crash fix + multi-register struct self~~ | ~~Done~~ |
| 3 | Constructor Thunks | Class + struct constructors | Medium-large |
| 4 | Indirect Result Returns | P/Invoke indirect return + remaining multi-slot work | Medium |

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

## Session 3: Constructor Thunks
<!-- commit: pending -->

**What**: Enable thunking for class allocating constructors and struct constructors (Phase 1 condition map rows 3-4). These are currently handled by @_cdecl wrappers.

**Why deferred**: ConstructorHandler's C# codegen is coupled with the @_cdecl pattern:
- `UsesCdeclConstructorWrapper` flag controls resultPtr handling
- Explicit `IntPtr resultPtr` parameter in the P/Invoke
- `SwiftIndirectResult` for struct constructors
- Different code paths in MethodHandler (~lines 239-260) vs method wrappers (~lines 612-680)

**Modified files** (read each before modifying):
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/NativeThunkEmitter.cs` — Remove constructor rejection gate, add constructor-specific eligibility checks
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` — Constructor thunk routing (lines ~239-260)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs` — Constructor P/Invoke signature for thunked path
- `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlanBuilder.cs` — Constructor marshal plan for thunked path
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/NativeThunkEmitterTests.cs` — Constructor eligibility tests

**Implementation details**:
1. Read ConstructorHandler codegen thoroughly — understand how `UsesCdeclConstructorWrapper`, `resultPtr`, and `SwiftIndirectResult` interact
2. For **class constructors**: The metatype-in-x20 assembly template already works. The thunk calls the metadata accessor, puts metatype in x20, then calls the allocating init. Return is a class pointer in x0 (single register, no indirect result). The P/Invoke should return IntPtr. This is the simpler case.
3. For **struct constructors**: The thunk handles x8 indirect return. But the P/Invoke must NOT use `SwiftIndirectResult` under CallConvCdecl (it maps to x8 only under CallConvSwift). Research whether returning a correctly-sized blittable struct causes .NET to use AAPCS64 x8 naturally. If not, struct constructors may need to stay on @_cdecl.
4. Add constructor-specific tests for both class and struct cases

**Note**: The x8 research question here overlaps with Session 4 (indirect result returns). If Session 4 is done first, its findings apply directly to struct constructors.

**Validation**: `./run-tests.sh` + `./validate-libraries.sh` + `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90`

---

## Session 4: Indirect Result Returns
<!-- commit: pending -->

**What**: Enable thunking for methods that return types requiring the indirect result pattern (SwiftIndirectResult). Also pick up any multi-slot value parameter work deferred from Session 2 Part C.

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

**Note**: The x8 research findings here also apply to struct constructors in Session 3. Whichever session runs first should document the result.

**Validation**: `./run-tests.sh` + `./validate-libraries.sh` + `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90`
