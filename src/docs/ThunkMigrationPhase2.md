# Design Doc: Native ARM64 Thunks — Phase 2

**Status**: Session 1 complete
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

Sessions 2-6 are independent of each other and can be done in any order. Priority recommendation:

| Order | Session | Impact | Complexity |
|---|---|---|---|
| ~~1~~ | ~~Runtime Validation~~ | ~~Critical~~ | ~~Done~~ |
| 2 | Constructor Thunks | High — constructors are very common | Medium-large |
| 3 | Indirect Result Returns | Medium-high — unlocks many return patterns | Medium |
| 4 | Multi-Register Struct Self | Medium — less common pattern | Medium |
| 5 | Multi-Slot Value Params | Low-medium — rare in practice | Large |
| **6** | **Thunk ABI: Class Property Setters** | **High — crash on setter thunks** | **Medium** |

**Session 6 should be done before any other session** — it fixes a crash in class property setter thunks discovered during Session 1 runtime validation.

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

### Remaining: Class property setter crash (→ Session 6)
Runtime testing passed 29 tests then crashed on `FinalPropertyHolder.IntValue` setter. The thunk's register mapping for class instance method setters is incorrect — value + self parameter shuffling is wrong. Tracked as Session 6.

**Validation**: 9,249 unit tests pass, 90/90 library validation, simulator runtime: 29 pass before crash (crash is Session 6 scope).

---

## Session 2: Constructor Thunks
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

**Validation**: `./run-tests.sh` + `./validate-libraries.sh` + `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90`

---

## Session 3: Multi-Register Struct Self
<!-- commit: pending -->

**What**: Enable thunking for instance methods on frozen structs where self >8B (Phase 1 condition map row 8).

**Why deferred**: ThunkAssemblyEmitter only handles single-register self (`mov x20, x0`). Frozen structs 9-32B need multi-register self.

**Modified files**:
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/ThunkAssemblyEmitter.cs` — New assembly template for multi-register self
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/NativeThunkEmitter.cs` — Update `IsSelfTypeLowerable()` to accept multi-register self
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThunkAssemblyEmitterTests.cs` — Assembly output tests
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/NativeThunkEmitterTests.cs` — Eligibility tests

**Implementation details**:
1. **Research first**: How does swiftcc handle struct self >8B? Two possibilities:
   - **Pointer in x20**: Swift passes a pointer to the struct in x20. Thunk just needs to pass the cdecl pointer through to x20 (like classes). This would be simple.
   - **Decomposed in registers**: Swift expects struct fields in individual registers around x20. Thunk needs to load from the cdecl pointer and distribute. This is complex.
   - Check the experiments worktree (`RESEARCH.md`) and Swift ABI docs.
2. If pointer-in-x20: Update `EmitSelfSetup` to handle struct pointers (may be identical to class self — just `mov x20, x0`)
3. If decomposed: New template that loads fields from the pointer into the correct registers per `TypeLoweringResult.Slots`
4. Update `IsSelfTypeLowerable()` in NativeThunkEmitter to allow `InlineSize > 8` for frozen structs

**Validation**: `./run-tests.sh` + `./validate-libraries.sh` + `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90`

---

## Session 4: Multi-Slot Value Parameters
<!-- commit: pending -->

**What**: Enable thunking for methods with value-type parameters >8B (Phase 1 condition map row 11).

**Why deferred**: ThunkAssemblyEmitter only does 1:1 register shifting for parameters. A 16B struct parameter occupies 2 cdecl registers (x0+x1) but may need different register placement in swiftcc (e.g., `{ Int, Double }` → x0 + d0).

**Modified files**:
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/ThunkAssemblyEmitter.cs` — Register remapping for multi-slot params
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/NativeThunkEmitter.cs` — Update `AreAllParametersLowerable()` and `EmitThunk()` parameter handling
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/TypeLowering.cs` — May need parameter-specific lowering adjustments
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThunkAssemblyEmitterTests.cs` — Assembly output tests

**Implementation details**:
1. Parameter lowering in the thunk: use `TypeLoweringResult.Slots` to map each cdecl register to the correct swiftcc register
2. Handle mixed int/float decomposition: `{ Int, Double }` arrives in x0+x1 (cdecl, all integer) but Swift expects x0+d0 (int+float). Thunk needs `fmov d0, x1` for the float field.
3. Handle parameter count expansion: a method with 2 params where one is a 16B struct uses 3 cdecl registers but potentially different swiftcc registers
4. Update `AreAllParametersLowerable()` to accept multi-slot params once assembly supports it
5. Consider scope: if multi-slot struct parameters are rare in real-world Swift APIs, this may not be worth the complexity. Run `./validate-libraries.sh --verbose` and grep for methods rejected by the multi-slot gate to assess impact.

**Validation**: `./run-tests.sh` + `./validate-libraries.sh` + `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90`

---

## Session 5: Indirect Result Returns via Thunks
<!-- commit: pending -->

**What**: Enable thunking for methods that return types requiring the indirect result pattern (SwiftIndirectResult).

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

**Validation**: `./run-tests.sh` + `./validate-libraries.sh` + `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90`

---

## Session 6: Thunk ABI Fix — Class Property Setters
<!-- commit: pending -->

**What**: Fix the thunk assembly register mapping for class instance method property setters. Discovered during Session 1 runtime validation — SIGSEGV on `FinalPropertyHolder.IntValue` setter.

**Root cause**: For a class property setter like `intValue { set }`:
- cdecl ABI: x0 = value (Int32), x1 = self pointer
- Swift ABI: x0 = value (Int32), x20 = self pointer
- The thunk must move x1 → x20 while preserving x0. The current assembly template isn't handling this correctly for the setter pattern.

**Symptoms**: Runtime crash at test #30 (`TestFinalPropertyHolderIntSetGet`). Tests 1-29 pass (getters, methods, constructors all work). The setter-specific register shuffle is broken.

**Modified files**:
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/ThunkAssemblyEmitter.cs` — Fix register mapping for instance method setters (value in x0, self in x1 → self to x20)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ThunkAssemblyEmitterTests.cs` — Add setter-specific assembly output tests
- `BindingTests/RuntimeTestsApp/` — Verify `FinalPropertyHolder` setter tests pass

**Implementation details**:
1. Read the current `EmitInstanceMethodSetup()` in ThunkAssemblyEmitter — understand how it maps cdecl params to swiftcc
2. The issue is likely in parameter counting: for a setter, the "value" parameter is x0 and "self" is x1 (cdecl). The thunk sees 1 parameter + self. But the self-movement code may be incorrectly treating x0 as self.
3. Write a targeted assembly test: `ThunkDescriptor` with `IsInstanceMethod=true`, `ParameterCount=1` (the value), verify the assembly moves x1→x20 (self) and keeps x0 (value)
4. After fixing, run the full BindingTests runtime suite to verify no other ABI patterns regressed

**Validation**: `./run-tests.sh` + `cd BindingTests && ./run-runtime-tests.sh --timeout 90`
