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
- Async Swift/C# callback type divergence — `GetSwiftWrapperCode` and `CallbackDeclarations` independently choose callback parameter types. For non-trivial returns (tuples, strings, structs), the Swift wrapper template may use different type names than the C# callback signature. Session 3's emitter must reconcile these when it has full ABI context. The `SwiftCallbackReturnType` field on `SwiftWrapperContext` is the hook.

**Validation**: 3700 unit tests (261 new: 127 complex projection + 134 Session 1), 700 integration tests, 207 runtime tests — all passing. All 8 supplement hard cases verified as composition tests. **Key references**: Supplement Section 3 (hard cases), Section 2 (real-world types)

---

## Phase 2: Migrate

### Session 3: Rip Out the Old Paths

**Goal**: Replace all 4 fragmented type conversion paths with `TypeProjectionFactory.Project()`. Rip out dead code. Clean up Conductor state.

#### 3a. Migrate WrapperSignatureBuilder

Replace calls to `GetIdiomaticCSharpType` + `TranslateTypeSpecForConversion` + `TranslateBoundGenericTypeToCSharp` in `MethodSignature.cs` with `TypeProjectionFactory.Project()`. The projection provides both PublicType and PInvokeType from a single call.

#### 3b. Migrate ProtocolProxyEmitter

Replace `GetCSharpTypeName` internals in `ProtocolProxyEmitter.Helpers.cs` with `TypeProjectionFactory.Project()`. The `forAbiMarshalling` flag maps to requesting PInvokeType vs PublicType from the same projection. This automatically fixes:
- The `isParameter` always-true bug (supplement rows 5, 7)
- The missing native type remapping (supplement row 18)

#### 3c. Migrate WrapperEmitter.Marshalling + WrapperEmitter.Return

Replace the inline marshalling code (~1600 combined lines) with `MarshalPlan` rendering. The projection produces the plan; the emitter walks `MarshalStatement` nodes and writes them to the CodeWriter. The massive if/else chains for dictionaries, optionals, existentials, etc. collapse into `projection.GetParameterPlan(paramName)`.

**Required test**: For async methods with tuple/string/complex returns, assert that the emitted Swift callback parameter types exactly match the emitted C# callback signatures. This enforces the callback signature reconciliation that Session 2 deferred (see Session 2 deferred limitations).

#### 3d. Migrate Remaining Callers

- `DefaultParameterOverloadEmitter` — use projection for overload key computation
- `ProtocolConformanceValidator` — use projection for type comparison
- `MemberEmissionValidator` — use projection for type support checking
- `EnumHandler.CaseConstruction` / `EnumHandler.CaseInspection` — use projection for associated value types
- `PropertyHandler` — use projection for property type resolution

#### 3e. Conductor State Cleanup

While everything is already torn open:
- `CurrentPInvokeHelperContext` → pass as explicit parameter to member emission methods
- `NestedTypeRenames` → compute during type pre-processing, store on TypeDecl
- `CompositionInterfaces` / `s_activeCompositionCollector` → replace ThreadStatic with explicit collector threaded through calls, or compute compositions in analysis phase

#### 3f. Delete Dead Code

Remove:
- `TypeConversionHandler.GetIdiomaticCSharpType()` — absorbed into projections
- `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp()` — absorbed into projections
- `WrapperSignatureBuilder.TranslateTypeSpecForConversion()` — absorbed into factory
- `ProtocolProxyEmitter.GetCSharpTypeName()` — absorbed into factory
- All string marker parsing in `Parameter.SignatureString()` / `GetCallArgumentString()` — replaced by MarshalledType pattern matching
- Any helpers that existed solely to bridge the old fragmented paths

---

## Phase 3: Lock Down

### Session 4: Tests + Validation + Stabilization

**Goal**: Prove it works. Lock it down. Make regressions impossible.

#### 4a. Cross-Path Consistency Tests

Create `TypeProjectionConsistencyTests.cs` — but now this is trivial because there's only one path. The test verifies that for 50+ real-world Swift types (from supplement Section 2), `TypeProjectionFactory.Project()` produces correct PublicType, PInvokeType, and MarshalPlan.

#### 4b. MarshalPlan Unit Tests

Test every projection's parameter and return MarshalPlan against expected output. Cover all 8 hard cases from the supplement. These are the regression tests for the factory — if someone adds a new projection or modifies an existing one, these catch mistakes.

#### 4c. Golden-File Tests

Capture generated `Swift.{Module}.cs` output for every library in `BindingTesting/` and the TestFramework as golden files. A script regenerates and diffs. Any future change that affects output requires explicit review.

#### 4d. Full Library Validation

Run `./validate-libraries.sh` — all 31 libraries must pass. Run `./run-tests.sh` — all unit + integration tests. Run `cd TestFramework && ./build-and-test.sh` — 94/94 must-pass features.

#### 4e. Update Baselines

Update MEMORY.md baselines, roadmap.md metrics, and CLAUDE.md documentation to reflect the new architecture.

---

## Phase 4: Method Handler Decomposition

### Session 5: Decompose MethodHandler + WrapperEmitter

**Goal**: With type conversion logic removed by the factory, MethodHandler.cs and WrapperEmitter.cs are now purely method-level orchestration. Decompose them into composable handlers per the [emitter redesign proposal](Future/emitter-redesign-proposal.md) Group 1 concept.

After Session 3, what remains in these files is: SwiftSelf setup, SwiftError handling, generic metadata passing, async Task wrapping, constructor payload assignment, indirect result allocation, and the P/Invoke call itself. These are independent concerns currently tangled in monolithic methods.

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

#### 5b. Decompose into Method-Level Handlers

Split the monolithic MethodHandler into composable handlers that each populate one aspect of the `MethodMarshalPlan`:

- `ConstructorHandler` — payload assignment, skip result post-processing
- `InstanceMethodHandler` — SwiftSelf creation from type representation
- `StaticMethodHandler` — static keyword, no self parameter
- `SwiftErrorHandler` — SwiftError parameter + post-call exception check
- `GenericParameterHandler` — metadata pointers, PWT pointers, P/Invoke params
- `AsyncMethodHandler` — Swift Task wrapper, C# callback, TaskCompletionSource
- `IndirectResultHandler` — stack allocation, void P/Invoke return, result buffer read

Each handler implements `bool CanHandle(MethodDecl)` and `void Contribute(MethodMarshalPlan)`. The orchestrator runs all applicable handlers, then a renderer walks the plan and emits code.

#### 5c. Collapse WrapperEmitter

With per-parameter marshalling handled by `MarshalPlan` and method-level concerns handled by `MethodMarshalPlan`, `WrapperEmitter.cs` + its partial files (`Marshalling.cs`, `Return.cs`, `Async.cs`) collapse into a single plan renderer. The renderer walks the `MethodMarshalPlan` and writes statements to the CodeWriter in order — no decision logic, just serialization.

**Validation**: All unit + integration tests pass. Library validation 0 regressions. Golden-file diffs reviewed. MethodHandler.cs and WrapperEmitter.cs are each under 200 lines (down from 896 and 978).

---

## Summary

| Session | Phase | What | Scope |
|---------|-------|------|-------|
| **1** | Build | MarshalledType DU + TypeProjectionFactory core + simple projections | **COMPLETE** — new infra + Parameter.Type refactor |
| **2** | Build | Complex projections (collections, optional, existential, closure, tuple, async) | **COMPLETE** — 7 new projections, recursive factory routing |
| **3** | Migrate | Rip out 4 old paths, wire factory everywhere, Conductor cleanup, delete dead code | Massive change across ~40+ files |
| **4** | Lock Down | Consistency tests, MarshalPlan tests, golden files, full library validation | Tests + validation only |
| **5** | Decompose | Split MethodHandler + WrapperEmitter into composable handlers + MethodMarshalPlan | Refactor ~6 files, add new handler classes |

Sessions 1-2 build the new type projection architecture alongside the old one. Session 3 rips out the old and wires in the new. Session 4 proves it works. Session 5 finishes the job by decomposing the method-level orchestration — the last piece of the original Microsoft redesign proposal.
