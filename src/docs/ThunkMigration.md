# Design Doc: Native ARM64 Thunks — Replacing CallConvSwift (Phase 1)

**Status**: Complete (March 23, 2026)
**Phase 2**: See `ThunkMigrationPhase2.md` for deferred work and future improvements.

## Background

Microsoft's `CallConvSwift` in .NET 10 is incomplete: non-blittable types crash on Mono, struct returns >16 bytes fail, and there's no sign of fixes. We currently work around this by generating Swift `@_cdecl` wrapper functions, but these require the Swift compiler at build time and can't return structs natively.

**Research** (at `.claude/worktrees/native-thunk-experiments/experiments/RESEARCH.md`) proved that ARM64 assembly thunks bridging cdecl→swiftcc:
- Work for all synchronous non-generic function patterns (24/24 tests passing)
- Are ABI-stable since Swift 5.0 (register layout documented, never changed)
- Are 34% faster than `@_cdecl` for struct returns 17-32 bytes
- Compile for all Apple platforms (iOS, tvOS, macOS, Mac Catalyst, simulators)
- Work with code signing

## Architecture Change

```
BEFORE (3 paths):
  C# → CallConvSwift P/Invoke → Swift ABI           (BROKEN for non-blittable)
  C# → CallConvCdecl P/Invoke → @_cdecl wrapper      (works but needs swiftc)

AFTER (2 paths):
  C# → CallConvCdecl P/Invoke → ARM64 thunk → Swift ABI    (NEW — no swiftc needed)
  C# → CallConvCdecl P/Invoke → @_cdecl wrapper             (kept for async/generic/closure)
```

## What Was Delivered

### New files
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/TypeLowering.cs` — Swift type → ARM64 register slot mapping
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/ThunkAssemblyEmitter.cs` — ARM64 assembly codegen (5 bridging templates)
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/SwiftCallTargetResolver.cs` — Shared symbol resolution (Tj suffix, `_` prefix)
- `src/Swift.Bindings/src/Emitter/ThunkEmitter/NativeThunkEmitter.cs` — Pipeline coordinator (eligibility + emission)
- `src/Swift.Bindings/src/Configuration/NativeThunkCompiler.cs` — `.s` → `.o` compilation via clang

### Key changes to existing files
- `MethodDecl.cs` — `WrapperStrategy.NativeThunk` enum variant, `UsesNativeThunk`, `ThunkAssemblyEmitted`
- `WrapperValidation.cs` — `GetCallingConvention()` distinguishes @_cdecl (Cdecl), thunk (Cdecl), @_silgen_name (Swift), direct (Swift)
- `MethodHandler.cs` — Thunk routing with @_cdecl fallback on emission failure
- `PropertyHandler.cs` — Per-accessor thunk routing with @_cdecl fallback
- `SubscriptHandler.cs` — Per-accessor thunk routing with @_cdecl fallback
- `PInvokeEmitter.cs` — Thunk entry points, SwiftSelf→IntPtr and SwiftError→out IntPtr for thunked methods, DynamicSelf fast path
- `ModuleEmitter.cs` / `ModuleEmissionContext.cs` — `.arm64.s` file output
- `SwiftWrapperCompiler.cs` — Links thunk `.o` files into wrapper xcframework, fatal on thunk compile failure
- `ModuleDatabaseEmitter.cs` / `TypeRecord.cs` / `TypeDatabase.cs` — `abiLayout` attribute persistence
- `ModuleProcessor.cs` — ABI field layout computation from StructDecl.Properties
- `PInvokeHelperEmitter.cs` — `PInvokeDeclaration.CallingConvention` property (was hardcoded Cdecl)
- `MethodMarshalPlanBuilder.cs` — SwiftSelf/SwiftError handling for thunked methods

### Tests
~160 tests across 4 test files:
- `TypeLoweringTests.cs` — Register slot mapping for all type categories
- `ThunkAssemblyEmitterTests.cs` — Assembly output for all template patterns
- `NativeThunkEmitterTests.cs` — Eligibility gates, symbol generation, emission, calling convention routing
- `PInvokeEmitterTests.cs` — P/Invoke signature correctness for thunked methods

### Validation results
- Unit tests: 8789 passed, 0 failed
- Library validation: 90/90 compile gate passed
- No regressions vs pre-thunk baseline

## Thunk Eligibility — What Gets Thunked vs @_cdecl

`NativeThunkEmitter.ShouldEmitThunk()` applies these gates (in order):

| Gate | Result | Rationale |
|---|---|---|
| Not xcframework mode | → @_cdecl | Thunks only in xcframework mode |
| Async | → @_cdecl | Different calling convention (swifttailcc) |
| Generic | → @_cdecl | Needs type metadata + witness tables |
| Typed throws | → @_cdecl | Needs Swift-side error boxing |
| Closure parameters | → @_cdecl | Need Swift adapter code |
| Constructor | → @_cdecl | C# codegen coupled with @_cdecl pattern (Phase 2) |
| Variadic params | → @_cdecl | Variable argument count |
| Inout params | → @_cdecl | Write-back semantics |
| Actor-isolated | → @_cdecl | Needs async dispatch |
| Module-internal / SPI | → skip | Not emitted at all |
| Tuple/closure return | → @_cdecl | Can't lower these return types |
| DynamicSelf return | → @_cdecl | Indirect result ABI mismatch |
| Multi-slot params (>1 register) | → @_cdecl | Assembly only does 1:1 register shifting |
| Unlowerable params | → @_cdecl | Unknown type layout |
| Return needs bridging but can't lower | → @_cdecl | Missing field layout for register store |
| Multi-register struct self (>8B) | → @_cdecl | Assembly only does single-register self move |
| Swift call target not in TBD exports | → @_cdecl | ObjC-routed symbols don't have Tj dispatch thunks |
| All gates pass | → **NativeThunk** | Thunk emitted |

## Deviations from Original Design

1. **Constructors deferred** — Design doc rows 3-4 planned thunk support for class/struct constructors. Deferred because ConstructorHandler's C# codegen (explicit resultPtr, UsesCdeclConstructorWrapper) is tightly coupled with @_cdecl. See Phase 2.

2. **Multi-register struct self deferred** — Row 8 planned thunk support for frozen struct self >8B. Assembly emitter only handles single-register self (`mov x20, x0`). See Phase 2.

3. **Multi-slot value parameters deferred** — Row 11 planned thunk support for non-blittable params >8B. Assembly only does 1:1 register shifting. See Phase 2.

4. **PInvokeCallingConvention.Swift restored** — Session 6 removed it per the design doc, but review proved it's needed for `WrapperStrategy.None` methods targeting raw Swift symbols (@_silgen_name wrappers, direct calls where cdecl/swiftcc are identical).

5. **Indirect result returns not thunked** — The thunk assembly CAN bridge registers to x8 buffer, but the C# P/Invoke can't express this correctly under CallConvCdecl (SwiftIndirectResult maps to x8 only under CallConvSwift). Methods needing indirect results route to @_cdecl. See Phase 2.

6. **TBD export symbol check added** — Not in the original design. ObjC-routed methods (@objc dynamic) don't emit Tj dispatch thunk symbols, causing linker failures. `IsSwiftCallTargetExported()` checks TBD before thunking.

## Key Technical Details

### Assembly bridging templates (ThunkAssemblyEmitter)

| Template | When | What it does |
|---|---|---|
| Tail call | Return ≤16B, no self, no throws | Single `b` instruction, zero overhead |
| Struct return bridge | Return 17-32B | Save x8, call Swift, store registers to [x8] |
| Self-in-x20 | Instance methods | `mov x20, x0`, shift remaining params |
| Metatype-in-x20 | Static methods | Save/restore all params around metadata accessor call |
| Swifterror-in-x21 | Throwing functions | Clear x21, call Swift, capture error |

Templates compose: throwing instance method with struct return uses self + error + return bridge.

### Thunk failure fallback

When `EmitThunk()` fails (e.g., missing metadata accessor), all three handlers (MethodHandler, PropertyHandler, SubscriptHandler) retry the @_cdecl wrapper path via `DetermineMethodWrapperDecision()` / `DeterminePropertyWrapperDecision()` / `ShouldEmitSubscriptWrapper()`. The `accessorCdeclFlags` dictionary is updated on late fallback for correct downstream bookkeeping (SBW_Free emission, etc.).
