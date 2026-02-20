# Architecture Improvement Roadmap

**Created**: February 2026
**Status**: Active
**Source**: [architecture-retrospective-findings.md](Future/architecture-retrospective-findings.md) and [supplement](Future/architecture-retrospective-supplement.md)

---

## Approach

Rip out the fragile architecture and replace it. Temporary breakage during the process is fine — we stabilize at the end. The goal is to get to a single unified type projection pipeline as fast as possible, not to incrementally polish the old one.

Three phases:
1. **Build** — Create the new architecture (MarshalledType, TypeProjectionFactory, MarshalPlan)
2. **Migrate** — Rip out the 4 fragmented conversion paths, wire everything through the factory, clean up Conductor state
3. **Lock Down** — Tests, golden files, library validation, confirm zero regressions

---

## Phase 1: Build the New Architecture

### Session 1: MarshalledType + TypeProjectionFactory Core — COMPLETE (Feb 19, 2026)

**Goal**: Build both foundation layers in one session — the type-safe parameter encoding and the projection infrastructure.

#### 1a. MarshalledType Discriminated Union — COMPLETE

Replaced the 25 string-encoded type markers in `Parameter.Type` with a sealed abstract record hierarchy (`MarshalledType`). All 25 variants implemented as nested record types with full C# pattern matching and deconstruction support:

```
// Prefixed variants (11)
Existential(ContainerType, PublicType), SimpleEnum(UnderlyingType, EnumTypeName),
ObjCBridged(CSharpTypeName), CdeclClosureFuncPtr(CallbackName, SourceCsName),
CdeclClosureContext(SourceCsName), AsyncThrowingContext(ParamName),
AsyncThrowingStartFunc(CallbackName), NativeRemappedFrozen(SwiftWrapperType),
FrozenBuffer(TypeName), ConventionCFuncPtr(FuncPtrType), SwiftSelfTyped(InnerType)

// Singleton variants (11)
AsyncCallback, AsyncErrorCallback, AsyncContext, AsyncTask,
NonFrozenIntPtr, EnumSafeHandle, NativeRemappedNonFrozen, NonFrozenSafeHandle,
SwiftClosureLegacy, Bool, SwiftSelfUntyped

// Catch-all
Simple(CSharpType)
```

Changed `Parameter.Type` from `string` to `MarshalledType`. Updated `SignatureString()`, `PInvokeSignatureString()`, and `GetCallArgumentString()` to pattern-match on variants. Converted all 59 `AddParameter()` call sites across PInvokeEmitter.cs and MethodSignature.cs. Added `PublicTypeName` property for cases needing a string representation. Bool `[MarshalAs(UnmanagedType.U1)]` centralized for parameters; 6 return-type bool checks deferred to Session 3 (they check `Signature.ReturnType` which remains a string).

**Key detail**: The string `AddParameter` convenience overload intercepts `"bool"` → `MarshalledType.Bool` to handle cases where the type database resolves `Swift.Bool` to `"bool"` as a string.

#### 1b. TypeProjectionFactory + Core Interfaces — COMPLETE

Implemented the revised interfaces:

- `ITypeProjection` — PublicType, PInvokeType, PInvokeAttribute, GetParameterPlan, GetReturnPlan, RequiresSwiftWrapper, GetSwiftWrapperCode
- `MarshalPlan` — SetupStatements, PInvokeExpression, CleanupStatements, UsingDeclarations, RequiresUnsafe, RequiresFixed, with `PassThrough()` factory
- `MarshalStatement` hierarchy — Line, Block (if/else, try/finally), Using
- `ReturnStrategy` enum — Direct, IndirectResult, OutBuffer, AsyncCallback
- `TypeProjectionFactory` — single entry point: `Project(TypeSpec, ProjectionContext) → ITypeProjection?` (nullable in Session 1; will tighten to non-null in Session 3)

#### 1c. Simple Projections — COMPLETE

Implemented 7 projections in `src/Swift.Bindings/src/Marshaler/Projection/`:
- `BlittableProjection` (int, nint, double, float, IntPtr) — PassThrough plans
- `BoolProjection` (with intrinsic `[MarshalAs(UnmanagedType.U1)]`)
- `StringProjection` (SwiftString ↔ string, with Using disposal, Direct/IndirectResult strategies)
- `SimpleEnumProjection` (cast to/from underlying type)
- `ObjCBridgedProjection` (IntPtr + .Handle extraction / GetNSObject wrapping)
- `NativeRemappedProjection` (frozen value vs non-frozen SafeHandle variants)
- `NonFrozenStructProjection` (Payload.DangerousGetHandle / construct from IntPtr)

All 7 projections are reachable from `TypeProjectionFactory` — including `NativeRemappedProjection` which routes via `TypeRecord.NativeTypeName != null` (no `TypeConversionHandler` dependency needed).

**Validation**: 3573 unit tests (134 new), 700 integration tests, 207 runtime tests — all passing. 31/31 libraries compile at 0 errors, 0 regressions. **Key references**: Supplement Section 1 (string markers), Section 3.1 (interface design)

---

### Session 2: Complex Projections — COMPLETE (Feb 19, 2026)

**Goal**: Handle every hard case from the supplement. After this session, the factory can project any Swift type the generator encounters.

#### 2a. Collection Projections — COMPLETE

- `ArrayProjection(inner)` — composable with inner element projection. Param: `IEnumerable<T>` → `SwiftArray<T>` via `FromEnumerable` + `PayloadBuffer`. Return: `SwiftArray<T>` → `IReadOnlyList<T>` via `MarshalFromSwift` + `AsProjected`. Element-wise conversion via inner projection's `GetParameterElementConversion`/`GetReturnElementConversion`. Disposal in try/finally when `ElementRequiresDisposal=true`.
- `DictionaryProjection(key, value)` — parallel pattern to Array. Multi-statement MarshalPlan with `Select` + `FromDictionary` + per-key/per-value disposal. Supports `AsProjected(k => ..., v => ...)` with independent key/value conversion lambdas (supplement Case 1).
- Nested composition works recursively: `ArrayProjection(StringProjection)` → proper element-wise SwiftString conversion.

#### 2b. Optional Projection — COMPLETE

- `OptionalProjection(inner)` — `T?` for all inners, `SwiftOptional<T>` at P/Invoke level.
- Three parameter paths: (1) simple inner (blittable) → inline ternary NewSome/NewNone, (2) element-converting inner (string, enum) → if/else Block with conversion, (3) container inner (Array, Dictionary) → if/else Block embedding inner's full param plan.
- Return: `MarshalFromSwift + ToNullable()` for standard types, discriminant check (`SwiftOptionalCases.None`) for existential inners (supplement Case 4 + 8).
- Full nesting verified: `OptionalProjection(DictionaryProjection(StringProjection, ArrayProjection(StringProjection)))` (supplement Case 2).

#### 2c. Existential Projection — COMPLETE

- `ExistentialProjection(containerType, publicType, proxyClassName)` — three-tier resolution: well-known protocol (Swift.Error → AnyError), known protocol with proxy (IProtocol → proxy class), unknown → object.
- Parameter: `ISwiftExistentialConvertible<Container>.GetExistentialContainer()`. Return: proxy construction or well-known type construction.
- Element conversions enable composition: `ArrayProjection(ExistentialProjection)` (supplement Case 6), `OptionalProjection(ExistentialProjection)` (supplement Case 8).

#### 2d. Closure Projection — COMPLETE

- `ClosureProjection` — `Action<>/Func<>` with inner arg/return projections. Escaping → `SwiftClosureData` + `GCHandle.Alloc` + callback declaration. Non-escaping → function pointer type.
- `CallbackDeclarations` property provides `[UnmanagedCallersOnly]` callback method with reverse type conversions (P/Invoke → delegate types for args, forward conversion for return).
- Return plan: lambda body wrapping function pointer invocation with type conversion (supplement Case 5).
- Callback naming via `ProjectionContext.CallbackNamePrefix`.

#### 2e. Tuple Projection — COMPLETE

- `TupleProjection(elements)` — `ValueTuple<>` with per-element projection. All-blittable tuples → PassThrough. Mixed types → per-element conversion in setup statements.
- Composes with all inner projections: `TupleProjection(StringProjection, BlittableProjection)` → element-wise `ToString()` in return direction.

#### 2f. Async Projection — COMPLETE

- `AsyncProjection(innerReturn, throws)` — `Task<T>` / `Task`. `RequiresSwiftWrapper=true`. P/Invoke returns void; result delivered via callback.
- `GetSwiftWrapperCode` generates Swift `@_silgen_name` wrapper with `Task { }` pattern, callback/errorCallback invocations, do/catch for throwing methods.
- `CallbackDeclarations` provides success callback (with inner projection's return element conversion) and error callback (OperationCanceledException/SwiftException).
- `ReturnStrategy.AsyncCallback` plan produces `TaskCompletionSource` + `GCHandle` setup.
- Composes: `AsyncProjection(TupleProjection(StringProjection, BlittableProjection))` (supplement Case 7).

#### 2g. ITypeProjection Extensions — COMPLETE

Added default interface methods to `ITypeProjection`:
- `GetParameterElementConversion(elementVar)` / `GetReturnElementConversion(elementVar)` — element-wise conversion for use in container `Select()` lambdas.
- `ElementRequiresDisposal` — controls try/finally disposal in container parameter plans.
- `CallbackDeclarations` — sibling callback methods for closures and async.

Added `CallbackDeclaration` record to MarshalPlan.cs. Extended `ProjectionContext` with `IsAsync`, `Throws`, `CallbackNamePrefix`.

Overrode element methods on 5 simple projections: StringProjection (SwiftString + disposal), SimpleEnumProjection (cast), ObjCBridgedProjection (Handle/GetNSObject), NonFrozenStructProjection (Payload/construct), NativeRemappedProjection (wrapper + disposal).

#### 2h. Factory Routing — COMPLETE

`TypeProjectionFactory.Project()` now handles every TypeSpec:
- **Async wrapping** (first check): `IsAsync && !IsParameter` → wraps inner return in `AsyncProjection`, strips IsAsync before recursing.
- **TupleTypeSpec** → recursive per-element projection, empty tuples → null.
- **ClosureTypeSpec** → recursive arg/return projection, callback name from context.
- **ProtocolListTypeSpec** → existential via `ExistentialHandler`.
- **NamedTypeSpec.IsAny** → converts to `ProtocolListTypeSpec` via `ExistentialHandler.ToProtocolListTypeSpec()`.
- **Swift.Optional/Array/Dictionary** → recursive generic parameter projection.
- Simple types preserved: Bool, String, ObjC, SimpleEnum, NativeRemapped, NonFrozen, Blittable.

**Deferred limitations**:
- `Optional<Optional<T>>` — inner `OptionalProjection` reports `ContainerTypeName = IntPtr` (the default), so nested optionals produce `SwiftOptional<IntPtr>` instead of `SwiftOptional<SwiftOptional<T>>`. No real Swift API in any of the 31 validated libraries uses `Optional<Optional<T>>`. A skip test (`NestedOptionalOptional_IsKnownLimitation`) guards against silent regression.
- Async Swift/C# callback type divergence — `GetSwiftWrapperCode` and `CallbackDeclarations` independently choose callback parameter types. For non-trivial returns (tuples, strings, structs), the Swift wrapper template may use different type names than the C# callback signature. Session 5c's WrapperEmitter collapse must reconcile these when it has full ABI context. The `SwiftCallbackReturnType` field on `SwiftWrapperContext` is the hook.

**Validation**: 3700 unit tests (261 new: 127 complex projection + 134 Session 1), 700 integration tests, 207 runtime tests — all passing. All 8 supplement hard cases verified as composition tests. **Key references**: Supplement Section 3 (hard cases), Section 2 (real-world types)

---

## Phase 2: Migrate

### Session 3: Factory-First Migration — COMPLETE (Feb 20, 2026)

**Goal**: Wire `TypeProjectionFactory.Project()` into all call sites where straightforward. Build supporting infrastructure (ClassProjection, MarshalPlanRenderer). Centralize bool handling. Delete dead code.

**Scope adjustment**: The original plan called for replacing all 4 conversion paths and collapsing WrapperEmitter inline. In practice, the WrapperEmitter emission migration (originally 3c.3/3c.4) requires fixing projection return plans to match actual emission code — that work is the same as Session 5c ("collapse WrapperEmitter"), so it's been moved there. Conductor state cleanup (originally 3e) requires IHandler interface changes that Session 5 will make when decomposing MethodHandler — moved there too.

#### 3a. Migrate WrapperSignatureBuilder — COMPLETE

Replaced calls to `GetIdiomaticCSharpType` + `TranslateTypeSpecForConversion` + `TranslateBoundGenericTypeToCSharp` in `MethodSignature.cs` with `TypeProjectionFactory.Project()`. The projection provides both PublicType and PInvokeType from a single call.

#### 3b. Migrate ProtocolProxyEmitter — COMPLETE

Replaced `GetCSharpTypeName` internals in `ProtocolProxyEmitter.Helpers.cs` with `TypeProjectionFactory.Project()`. The `forAbiMarshalling` flag maps to requesting PInvokeType vs PublicType from the same projection.

#### 3c. ClassProjection + MarshalPlanRenderer + Bool Centralization — COMPLETE

- **ClassProjection** (`src/Swift.Bindings/src/Marshaler/Projection/ClassProjection.cs`) — Swift class type marshalling: `NativeMemory.Alloc` + pointer store + `MarshalFromSwift` try/catch return pattern. Parameters pass `Payload.DangerousGetHandle()`. Wired into `TypeProjectionFactory` between SimpleEnum and NativeRemapped checks.
- **MarshalPlanRenderer** (`src/Swift.Bindings/src/Emitter/StringEmitter/MarshalPlanRenderer.cs`) — Pure statement-tree-to-text renderer. Handles `MarshalStatement.Line`, `.Block`, `.Using`. `RenderReturnPlan` emits setup → optional `return {PInvokeExpression};` → cleanup. ClassProjection embeds return inside try block (PInvokeExpression empty), renderer skips the return line.
- **Bool centralization** — Renamed `IsBoolReturnType` → `IsBoolType`. All 7 `== "bool"` string comparisons in Emitter (4 return + 3 parameter) now use `MarshallingHelpers.IsBoolType()`. Zero raw string comparisons remain.

#### 3d. Migrate Remaining Callers — COMPLETE

Most call sites were already factory-first from Sessions 1-2 migrations. Migrated `ProtocolSignatureHelper.ProjectTypeToCSharp` to factory-first (replaced ~50 lines of existential/closure/tuple/idiomatic cascading). Remaining old API calls (~68 sites) are either:
- **WrapperEmitter callers** — deferred to Session 5c (WrapperEmitter collapse)
- **Bound-generic fallback paths** — factory returns null, legacy `TranslateBoundGenericTypeToCSharp` is the correct fallback (needs GenericContext support in factory, see Session 5 prerequisites)

#### 3f. Delete Dead Code — COMPLETE

Deleted `ProjectClosureToCSharp` and `ProjectTupleToCSharp` from `ProtocolSignatureHelper.cs` (zero callers after factory migration). Removed unused `boundGenericsHandler` variable. Most old APIs (`GetIdiomaticCSharpType`, `TranslateBoundGenericTypeToCSharp`, `GetReturnConversion`, etc.) still have active WrapperEmitter callers — final deletion deferred to after Session 5c.

**Validation**: 3711 unit tests (22 new), 700 integration tests — all passing. 31/31 libraries compile at 0 errors, 0 regressions.

**What moved to Session 5 (and why)**:
- **WrapperEmitter emission migration** (was 3c.3/3c.4) → Session 5c. Projection return plans designed in Session 2 have bugs vs actual emission code (e.g., StringProjection's Direct return does `result.ToString()` but WrapperEmitter does `MarshalFromSwift<SwiftString>(new IntPtr(&result)).ToString()`). Fixing these plans and wiring them in is the same work as "collapse WrapperEmitter" — no reason to do it twice.
- **Conductor state cleanup** (was 3e) → Session 5b. `CurrentPInvokeHelperContext` and `NestedTypeRenames` are parent→child communication through `IHandler.Emit(CSharpWriter, SwiftWriter, IEnvironment, Conductor)`. Removing them requires changing the interface, which Session 5 will do when decomposing MethodHandler into composable handlers.
- **Final dead code deletion** (was 3f remainder) → after Session 5c. Old APIs become truly dead only after WrapperEmitter stops calling them.

---

## Phase 3: Lock Down

### Session 4: Tests + Validation + Stabilization — COMPLETE (Feb 20, 2026)

**Goal**: Prove it works. Lock it down. Make regressions impossible.

#### 4a. Type Projection Consistency + Signature Agreement Tests — COMPLETE

Created `TypeProjectionConsistencyTests.cs` with 119 tests in two parts:

**Part 1: Type Matrix** (~54 test cases via `[Theory]` + `[MemberData]`). Systematic verification that `TypeProjectionFactory.Project()` produces correct `(PublicType, PInvokeType, ProjectionType)` for all real-world Swift types. Categories: well-known simple (8), TypeDB-resolved (8), container params (5), container returns (5), optionals (8), existentials (5), tuples (3), closures (4), async (4), deep nesting (4), null returns (6).

**Part 2: Cross-Layer Signature Agreement** (17 entries x 3 theories = 51 tests + 15 standalone facts = 66 total). Verifies the triple agreement within each projection: PublicType, PInvokeType, and parameter plan PInvokeExpression are all internally consistent. Covers all 17 projection variants: String, Bool, Blittable, SimpleEnum, ObjCBridged, NonFrozen, Class, NativeRemapped (frozen + non-frozen), Array, Dictionary, Optional, Existential, Tuple, Closure, Async.

#### 4b. MarshalPlan Rendered Regression Tests — COMPLETE

Created `MarshalPlanRegressionTests.cs` with 54 tests. For each of the 16 projection types, renders `GetParameterPlan` and `GetReturnPlan` via `MarshalPlanRenderer` and asserts on rendered C# code. Known pre-Session-5 divergences marked with `[Trait("Stability", "PreSession5")]` (StringProjection Direct return, ClassProjection Direct return). Covers all `ReturnStrategy` variants per projection.

#### 4c. Golden-File Tests — COMPLETE

Created `golden/` directory with first-party golden file testing. Policy: only first-party generated output is committed; third-party library validation uses `validate-libraries.sh` (local-only, not committed).

- `golden/SwiftBindingsTestLib.cs.golden` — committed first-party golden file (~42K lines)
- `golden/update-golden-files.sh` — regenerates first-party golden file from current generator output
- `golden/check-golden-files.sh` — diffs against stored golden file, exit 1 on delta

**Bug fix during 4c**: Discovered that `NameProvider.GetPInvokeName()` and 5 other naming functions used `string.GetHashCode()` which is non-deterministic across .NET processes, making golden file comparison impossible. Replaced all 6 call sites (NameProvider.cs lines 176, 756, 766, 776, 786 and ClosureHandler.cs line 1244) with `EmitterUtility.DeterministicHash8()` (FNV-1a). This makes ALL generator output fully deterministic.

#### 4d. Full Library Validation — COMPLETE

- Unit tests: 3924 passing (0 failures, 1 skipped) — +213 from new Session 4 tests
- Integration tests: 700 passing (0 failures, 11 skipped)
- Runtime library tests: 221 passing (0 failures, 1 skipped)
- TestFramework: 94/94 must-pass features, 0 degraded, coverage report generated
- Golden file: SwiftBindingsTestLib matches (deterministic check passes)

#### 4e. Update Baselines — COMPLETE

Updated MEMORY.md baselines and architecture-roadmap.md.

**Validation**: 3924 unit + 700 integration + 221 runtime library tests, all passing. 5 golden files deterministically verified. Generator output is now fully deterministic across runs.

---

## Phase 4: Method Handler Decomposition

### Session 5: Decompose MethodHandler + WrapperEmitter

**Goal**: Fix projection return plans, add GenericContext to the factory, collapse WrapperEmitter emission into MarshalPlan rendering, clean up Conductor state, decompose MethodHandler into composable handlers. This is the culmination of the architecture migration — after this session, the old 4-path type conversion system is fully replaced.

**Prerequisites** (absorbed from Session 3 deferrals):
- **Fix projection return plans**: Session 2's projections have return plan bugs — they don't match actual WrapperEmitter emission code. Example: StringProjection's Direct return does `result.ToString()` but actual emission does `MarshalFromSwift<SwiftString>(new IntPtr(&result)).ToString()`. Each projection's `GetReturnPlan()` must be audited against the corresponding WrapperEmitter code path for Direct, IndirectResult, OutBuffer, and AsyncCallback strategies.
- **GenericContext support in factory**: `TypeProjectionFactory` currently returns null for bound generic types where resolution requires `GenericContext` (τ_0_0 → T0). ~15 remaining `TranslateBoundGenericTypeToCSharp` fallback sites in PropertyHandler, PInvokeEmitter, MemberEmissionValidator, and EnumHandler.CaseConstruction depend on this. Add `GenericContext?` to `ProjectionContext` so the factory can resolve generic type parameters.

#### 5a. Define `MethodMarshalPlan`

A method-level plan that composes per-parameter `MarshalPlan` objects (from the factory) with method-level concerns:

```
MethodMarshalPlan {
    PublicSignature           // C# method signature
    PInvokeDeclaration        // [LibraryImport] declaration
    ParameterPlans[]          // Per-param MarshalPlan from TypeProjectionFactory
    ReturnPlan                // MarshalPlan from TypeProjectionFactory
    SwiftSelfSetup?           // SwiftSelf creation (instance methods)
    SwiftErrorSetup?          // SwiftError + post-call check (throwing methods)
    GenericMetadataSetup?     // TypeMetadata + PWT extraction (generic methods)
    IndirectResultSetup?      // Stack allocation for large returns
    AsyncWrapperSetup?        // Task + callback infrastructure (async methods)
    SwiftWrapperCode?         // Generated Swift @_silgen_name wrapper
}
```

#### 5b. Conductor State Cleanup + Handler Interface Changes

Absorbed from Session 3e. While decomposing MethodHandler, change `IHandler.Emit` to thread state explicitly instead of through Conductor:

- `CurrentPInvokeHelperContext` (28 usage sites) → pass as parameter through handler chain. Currently serves as parent→child communication between type handlers that don't directly call each other (dispatch goes through `HandleBaseDecl` → `IHandler.Emit`). Options: add to `IEnvironment` subtypes, or pass alongside Conductor.
- `NestedTypeRenames` (12 usage sites) → same pattern. Parent type handler sets renames, child reads them via Conductor. Move to TypeEnvironment or pass through HandleBaseDecl.
- `CompositionInterfaces` / `s_activeCompositionCollector` (ThreadStatic) → replace with explicit collector threaded through calls, or compute compositions in analysis phase.

#### 5c. Collapse WrapperEmitter

Absorbed from Session 3 (was 3c.3/3c.4). Replace the inline marshalling code in `WrapperEmitter.Marshalling.cs` (~630 lines) and `WrapperEmitter.Return.cs` (~550 lines) with `MarshalPlan` rendering via `MarshalPlanRenderer`. The projection produces the plan; the renderer walks `MarshalStatement` nodes and writes to CSharpWriter. The massive if/else chains for dictionaries, optionals, existentials, etc. collapse into `projection.GetParameterPlan(paramName)`.

**Depends on**: Fixed projection return plans (prerequisite above) and GenericContext support.

**Required test**: For async methods with tuple/string/complex returns, assert that the emitted Swift callback parameter types exactly match the emitted C# callback signatures. This enforces the callback signature reconciliation that Session 2 deferred.

#### 5d. Decompose into Method-Level Handlers

Split the monolithic MethodHandler into composable handlers that each populate one aspect of the `MethodMarshalPlan`:

- `ConstructorHandler` — payload assignment, skip result post-processing
- `InstanceMethodHandler` — SwiftSelf creation from type representation
- `StaticMethodHandler` — static keyword, no self parameter
- `SwiftErrorHandler` — SwiftError parameter + post-call exception check
- `GenericParameterHandler` — metadata pointers, PWT pointers, P/Invoke params
- `AsyncMethodHandler` — Swift Task wrapper, C# callback, TaskCompletionSource
- `IndirectResultHandler` — stack allocation, void P/Invoke return, result buffer read

Each handler implements `bool CanHandle(MethodDecl)` and `void Contribute(MethodMarshalPlan)`. The orchestrator runs all applicable handlers, then a renderer walks the plan and emits code.

#### 5e. Delete Remaining Dead Code

Absorbed from Session 3f. After 5c collapses WrapperEmitter, the old APIs finally have zero callers:
- `TypeConversionHandler.GetIdiomaticCSharpType()`, `GetParameterConversion()`, `GetReturnConversion()`, `GetSwiftWrapperType()`, `IsConvertibleType()`
- `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp()` (all overloads)
- `WrapperEmitter.TranslateTypeSpecForConversion()` (both definitions)
- Supporting helpers: `IsElementTypeConverted`, `IsDictionaryKeyTypeConverted`, `GetRawArrayElementType`, etc.
- If `TypeConversionHandler` / `BoundGenericsHandler` have no remaining public methods after deletion, delete the entire classes.

**Exit gate**: `grep -rn "GetIdiomaticCSharpType\|TranslateBoundGenericTypeToCSharp\|GetParameterConversion\|GetReturnConversion\|GetSwiftWrapperType\b\|IsConvertibleType\|TranslateTypeSpecForConversion" src/Swift.Bindings/src/ --include="*.cs" | grep -v "//\|test\|Test"` returns zero results.

**Validation**: All unit + integration tests pass. Library validation 0 regressions. Golden-file diffs reviewed. MethodHandler.cs and WrapperEmitter.cs are each under 200 lines (down from 896 and 978).

---

## Summary

| Session | Phase | What | Status |
|---------|-------|------|--------|
| **1** | Build | MarshalledType DU + TypeProjectionFactory core + simple projections | **COMPLETE** (Feb 19) |
| **2** | Build | Complex projections (collections, optional, existential, closure, tuple, async) | **COMPLETE** (Feb 19) |
| **3** | Migrate | Factory-first for signatures/validators, ClassProjection, MarshalPlanRenderer, bool centralization | **COMPLETE** (Feb 20) |
| **4** | Lock Down | Consistency tests, MarshalPlan tests, golden files, deterministic hash fix, full validation | **COMPLETE** (Feb 20) |
| **5** | Decompose | Fix projection plans, GenericContext, collapse WrapperEmitter, Conductor cleanup, decompose MethodHandler | Pending — largest session |

Sessions 1-2 built the new type projection architecture alongside the old one. Session 3 wired the factory into all straightforward call sites and built supporting infrastructure. Session 4 proved the factory works via 179 new tests (125 consistency + 54 regression), first-party golden file testing, and a deterministic hash fix that made all generator output reproducible across runs. Session 5 is the culmination — it fixes projection return plans, collapses WrapperEmitter into MarshalPlan rendering, cleans up Conductor state, decomposes MethodHandler, and deletes all dead legacy code. After Session 5, the old 4-path type conversion system is fully replaced.
