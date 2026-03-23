# Design Doc: Native ARM64 Thunks — Phase 2

**Status**: Not started
**Prerequisite**: Phase 1 complete (see `Completed/ThunkMigration.md`)

## Overview

Phase 1 delivered the thunk pipeline end-to-end: type lowering → assembly codegen → generator integration → build pipeline → CallConvSwift elimination. Several patterns were deferred to @_cdecl because the assembly emitter or P/Invoke signature builder couldn't handle them correctly. This document tracks those deferred items plus runtime validation.

All deferred items are safe — they fall back to @_cdecl wrappers which work correctly. These are optimizations that would reduce Swift compiler dependency and improve performance for more method patterns.

---

## Session 1: Runtime Validation

**What**: Verify thunked methods work at runtime on the iOS simulator. Phase 1 confirmed everything compiles (90/90 library validation, 8789 unit tests), but the P/Invoke signature changes (SwiftSelf→IntPtr, SwiftError→out IntPtr, TBD export check, DynamicSelf gate) haven't been validated with actual Swift calls on-device.

**Deliverables**:
1. Run `cd BindingTests && ./build-and-test.sh 2>&1 | tee /tmp/build-and-test-results.txt`
2. If runtime tests fail, diagnose whether the failure is:
   - Thunk ABI mismatch (wrong register layout) → fix thunk or tighten eligibility
   - P/Invoke signature mismatch (SwiftSelf/SwiftError/SwiftIndirectResult handling) → fix PInvokeEmitter
   - Pre-existing failure unrelated to thunks
3. Run `cd BindingTests && ./run-runtime-tests.sh --timeout 90 2>&1 | tee /tmp/runtime-tests-results.txt`
4. If all pass, Phase 1 is fully validated

**Context**: The BindingTests Swift test library (SwiftBindingsTestLib) exercises many method patterns. When bindings are regenerated, some methods will be thunked and some will be @_cdecl. The runtime tests call these methods and verify return values — a thunk ABI mismatch would crash or return garbage.

---

## Session 2: Constructor Thunks

**What**: Enable thunking for class allocating constructors and struct constructors (Phase 1 condition map rows 3-4).

**Why deferred**: ConstructorHandler's C# codegen is coupled with the @_cdecl pattern:
- `UsesCdeclConstructorWrapper` flag controls resultPtr handling
- Explicit `IntPtr resultPtr` parameter in the P/Invoke
- `SwiftIndirectResult` for struct constructors
- Different code paths in MethodHandler (~lines 239-260) vs method wrappers (~lines 612-680)

**What's needed**:
1. Understand how ConstructorHandler generates P/Invoke signatures and return handling
2. Add `NativeThunk` path to ConstructorHandler that uses thunk-compatible signatures
3. For class constructors: thunk calls metadata accessor (x20) then allocating init — assembly template already exists (metatype-in-x20)
4. For struct constructors: thunk handles x8 indirect return — but P/Invoke must NOT use `SwiftIndirectResult` under CallConvCdecl (see Phase 1 deviation #5). Need alternative approach, possibly returning the struct directly so AAPCS64 uses x8 naturally.
5. Tests + validation

**Estimated scope**: Medium-large. Constructor codegen is one of the more complex emitter paths.

---

## Session 3: Multi-Register Struct Self

**What**: Enable thunking for instance methods on frozen structs where self >8B (Phase 1 condition map row 8).

**Why deferred**: ThunkAssemblyEmitter only handles single-register self (`mov x20, x0`). Frozen structs 9-32B need multi-register self: the cdecl side passes the struct in x0+x1 (or more), and Swift expects it decomposed across registers per field layout.

**What's needed**:
1. New assembly template: load struct fields from cdecl registers into the correct Swift registers
   - For a 16B struct `{ Int, Int }`: cdecl has x0=self.field0, x1=self.field1. Swift wants x20=self (pointer to struct?). Actually — need to investigate whether swiftcc passes struct self in registers or via pointer. The `SwiftSelf<T>` type suggests pointer.
2. Research: How does swiftcc handle struct self >8B? Is it passed as a pointer in x20, or decomposed into registers? This determines whether the thunk needs register decomposition or just pointer forwarding.
3. Update `IsSelfTypeLowerable()` to accept multi-register self once assembly supports it
4. Tests + validation

**Estimated scope**: Medium. Depends on swiftcc struct self passing convention (needs research).

---

## Session 4: Multi-Slot Value Parameters

**What**: Enable thunking for methods with value-type parameters >8B (Phase 1 condition map row 11).

**Why deferred**: ThunkAssemblyEmitter only does 1:1 register shifting for parameters. A 16B struct parameter occupies 2 cdecl registers (x0+x1) but may need different register placement in swiftcc (e.g., `{ Int, Double }` → x0 + d0).

**What's needed**:
1. Parameter lowering in the thunk: use `TypeLoweringResult.Slots` to map each cdecl register to the correct swiftcc register
2. Handle mixed int/float decomposition: `{ Int, Double }` arrives in x0+x1 (cdecl, all integer) but Swift expects x0+d0 (int+float). Thunk needs `fmov d0, x1` for the float field.
3. Handle parameter count expansion: a method with 2 parameters where one is a 16B struct uses 3 cdecl registers but 3 swiftcc registers (potentially in different files)
4. Update `AreAllParametersLowerable()` to accept multi-slot params once assembly supports it
5. Tests + validation

**Estimated scope**: Large. Register remapping for mixed int/float structs is complex. Consider whether the benefit justifies the complexity — multi-slot struct parameters as method arguments may not be common enough.

---

## Session 5: Indirect Result Returns via Thunks

**What**: Enable thunking for methods that return types requiring the indirect result pattern (SwiftIndirectResult).

**Why deferred**: The thunk assembly CAN bridge registers to x8 buffer (it already does this for 17-32B frozen struct returns). But the C# P/Invoke side can't express this cleanly:
- `SwiftIndirectResult` maps to x8 only under `CallConvSwift`
- Under `CallConvCdecl`, `SwiftIndirectResult` is a regular parameter (x0), not x8
- The @_cdecl pattern uses `IntPtr resultPtr` as an explicit parameter — but the thunk doesn't take resultPtr as a parameter, it reads x8 from the cdecl struct return convention

**What's needed**:
1. Research: Can the P/Invoke return a correctly-sized blittable struct so .NET uses AAPCS64 x8 for the return buffer? This would let the thunk's `mov x19, x8` pattern work naturally.
2. If yes: generate a `[StructLayout(Size=N)]` return type for the P/Invoke, matched to the Swift return size. The thunk stores registers to [x8], .NET reads from the buffer.
3. If no: investigate whether `SwiftIndirectResult` under `CallConvCdecl` actually uses x8 (it might — AAPCS64 uses x8 for all indirect returns, not just CallConvSwift).
4. Types that would benefit: non-frozen struct returns, Optional<value-type> returns, string returns (in wrapper context), existential returns
5. Tests + validation

**Estimated scope**: Medium. The key unknown is how .NET marshals `SwiftIndirectResult` under `CallConvCdecl`. Needs empirical testing.

---

## Priority Assessment

| Session | Impact | Complexity | Recommendation |
|---|---|---|---|
| 1 (Runtime validation) | **Critical** | Low | Do first — validates Phase 1 correctness |
| 2 (Constructors) | High | Medium-large | High value — constructors are very common |
| 5 (Indirect results) | Medium-high | Medium | Unlocks many return type patterns |
| 3 (Multi-reg struct self) | Medium | Medium | Less common pattern |
| 4 (Multi-slot params) | Low-medium | Large | Complex for marginal benefit |
