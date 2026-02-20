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

### Session 2: Complex Projections

**Goal**: Handle every hard case from the supplement. After this session, the factory can project any Swift type the generator encounters.

#### 2a. Collection Projections

- `ArrayProjection(inner)` — composable with inner element projection. Param: `IEnumerable<T>` → `SwiftArray<T>` via `FromEnumerable`. Return: `SwiftArray<T>` → `IReadOnlyList<T>` via `AsProjected`.
- `DictionaryProjection(key, value)` — multi-statement MarshalPlan with `Select` + `FromDictionary` + `try/finally` disposal for converted elements (supplement Case 1).
- Nested composition: `ArrayProjection(StringProjection)` → proper element-wise conversion.

#### 2b. Optional Projection

- `OptionalProjection(inner)` — `T?` for simple inners (nullable annotation), `SwiftOptional<T>` for complex.
- None/Some branching as `MarshalStatement.Block` in the MarshalPlan.
- Full nesting: `OptionalProjection(DictionaryProjection(StringProjection, ArrayProjection(StringProjection)))` (supplement Case 2 — the 15-line emission with 3 disposal scopes).

#### 2c. Existential Projection

- `ExistentialProjection(containerType, interfaceType)` — three-tier: well-known protocol → named type, known protocol with proxy → `IProtocol`, unknown → `object`.
- `ISwiftExistentialConvertible` extraction in parameter direction, proxy wrapping in return direction.
- Compose: `OptionalProjection(ExistentialProjection)` (supplement Case 8), `ArrayProjection(ExistentialProjection)` (supplement Case 6).

#### 2d. Closure Projection

- Closure types → `Action<>/Func<>` with inner type projection on parameters/return.
- Closure return with non-frozen struct params — lambda body generation with native memory allocation and VWT calls (supplement Case 5).
- Optional closure → nullable delegate.

#### 2e. Tuple Projection

- Tuple types → `ValueTuple<>` with per-element projection.
- String elements in tuples require conversion in async callback context (supplement Case 7).

#### 2f. Async Projection

- Async return with Swift wrapper generation + C# callback emission (supplement Case 7).
- `ReturnStrategy.AsyncCallback` path in MarshalPlan.
- This likely requires `MarshalPlan` extensions for cross-file emission.

**Validation**: Unit tests on each projection in isolation. Verify composition produces correct plans for all 8 supplement Section 3 cases.

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
| **2** | Build | Complex projections (collections, optional, existential, closure, tuple, async) | New code, no existing code changed |
| **3** | Migrate | Rip out 4 old paths, wire factory everywhere, Conductor cleanup, delete dead code | Massive change across ~40+ files |
| **4** | Lock Down | Consistency tests, MarshalPlan tests, golden files, full library validation | Tests + validation only |
| **5** | Decompose | Split MethodHandler + WrapperEmitter into composable handlers + MethodMarshalPlan | Refactor ~6 files, add new handler classes |

Sessions 1-2 build the new type projection architecture alongside the old one. Session 3 rips out the old and wires in the new. Session 4 proves it works. Session 5 finishes the job by decomposing the method-level orchestration — the last piece of the original Microsoft redesign proposal.
