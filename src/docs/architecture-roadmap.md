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

## Interlude: Library Validation Fix Sessions A-E (Feb 20, 2026)

Between Phase 3 (Lock Down) and Phase 4 (Decompose), five sessions of binding error fixes were applied to improve library validation from 24/32 to 27/32 passing. These fixes were necessary — they resolved real bugs blocking library compilation — but they added technical debt to old-architecture code paths.

### Architecture Alignment Assessment

| Category | Lines Added | Files | Assessment |
|----------|-------------|-------|------------|
| Old architecture expansion | ~590 | WrapperEmitter.Return.cs, ProtocolProxyEmitter.Receivers.cs, ClosureEmitter.Async.cs, ProtocolConformanceValidator.cs | Adds to code Session 5 will replace |
| Old architecture fallback (factory limitation) | ~30 | PropertyHandler.cs, MemberEmissionValidator.cs | `GetIdiomaticCSharpType` fallback when factory returns null for bound generics |
| Old architecture refactoring | ~57 | WrapperEmitter.Marshalling.cs | Extracted `EmitOptionalExistentialParamConversion()` — still inline strings, but reduced duplication |
| Architecturally neutral (skip gates) | ~125 | ProtocolHandler.cs, MemberEmissionValidator.cs, EnumHandler.cs, EnumHandler.CaseConstruction.cs, MethodSignature.cs | Controls *whether* to emit, not *how* — both architectures need these |
| New architecture fixes | ~53 | ClosureProjection.cs, TypeProjectionFactory.cs, ExistentialHandler.cs, ClosureHandler.cs | Genuine improvements to the new system |
| Runtime fix | ~30 | AsyncClosureHelper.cs | Orthogonal to generator architecture |

**~55% of changes went to old-architecture paths.** This was largely unavoidable — the bugs lived in code that Session 5 explicitly deferred (WrapperEmitter emission, ProtocolProxyEmitter receiver marshalling, bound-generic fallbacks).

### Specific Debt Added

**1. ProtocolProxyEmitter.Receivers.cs (+321 lines) — highest impact.** Three new helper methods (`GetReceiverExistentialGetterConversion`, `GetReceiverExistentialSetterConversion`, `OverrideOptionalExistentialAbiType`) manually reproduce conversions that `ExistentialProjection`, `OptionalProjection`, `ArrayProjection`, and `DictionaryProjection` already implement. ~250 lines of hand-rolled string-template code duplicating existing factory projections. These instantiate `new ExistentialHandler(_typeDatabase)` and `new TypeConversionHandler(_typeDatabase)` inline — the exact pattern the factory was built to replace.

**2. WrapperEmitter.Return.cs (+194 lines).** Four near-identical Optional-existential blocks across the direct, conversion, indirect-result, and out-buffer return strategies. Each adds `if (publicType != "object")` guards with nested existential resolution. `OptionalProjection(ExistentialProjection(...)).GetReturnPlan()` handles this centrally in ~15 lines.

**3. ClosureEmitter.Async.cs (+24 lines).** Uses old-API `TypeConversionHandler.GetParameterConversion()` for async closure delegate type conversion. Slated for deletion in Session 5e.

**4. Factory fallback pattern (3 files) — RESOLVED by Session 5A.** `PropertyHandler.cs`, `MemberEmissionValidator.cs`, and `ProtocolConformanceValidator.cs` previously fell back to `GetIdiomaticCSharpType()` when factory returned null. Session 5A added `GenericContext` support, eliminating all `GetIdiomaticCSharpType` callers from Emitter (Gate 5 passes). Bound generic fallbacks now use `TranslateBoundGenericTypeToCSharp` to produce raw ABI type names.

### Impact on Session 5

These changes increase Session 5's scope:

| Session 5 Sub-task | Additional Debt to Resolve |
|---------------------|---------------------------|
| 5A prerequisites (GenericContext) | ~~3 new `GetIdiomaticCSharpType` fallback sites~~ → **RESOLVED** (Session 5A eliminated all `GetIdiomaticCSharpType` from Emitter) |
| 5B (Collapse WrapperEmitter) | 4 new Optional-existential blocks in Return.cs (~140 lines), extracted `EmitOptionalExistentialParamConversion` in Marshalling.cs |
| 5B (Collapse Receiver emission) | 3 new receiver helper methods in ProtocolProxyEmitter.Receivers.cs (~250 lines to replace with projection calls) |
| 5C (Delete dead code) | `GetParameterConversion()` usage in ClosureEmitter.Async.cs |

### New Architecture Improvements from Sessions A-E

Not all changes were debt. These improvements to the new system carry forward:

- **ClosureProjection.cs** — Now correctly handles async and throwing closures (`Func<Task<T>>`, `Func<SwiftResult<T, SwiftError>>`). Previously produced incorrect delegate types.
- **TypeProjectionFactory.cs** — Returns null for `"Self"` and `"repeat"` Swift special forms (crash prevention).
- **ExistentialHandler.cs** — Returns `AnyType` for generic protocol existentials (`any EventStream<T>`) where associated types can't be resolved.
- **ClosureHandler.cs** — Checks `NativeTypeName` on type records so native-remapped types use correct C# names in closure signatures.

---

## Phase 4: Method Handler Decomposition

The original Session 5 has been split into three sessions (5A/5B/5C) based on dependency analysis and scope. Each session has a clear deliverable, validation gate, and can be completed independently.

**Dependency chain:**
```
Prerequisites ──→ 5A (Foundation) ──→ 5B (Collapse Emission) ──→ 5C (Decompose + Clean)
                                          5B also absorbs ProtocolProxyEmitter.Receivers debt
```

### Session 5A: Foundation — Fix Plans + GenericContext + MethodMarshalPlan — COMPLETE (Feb 20, 2026)

**Goal**: Fix the projection return plans to match actual emission, add GenericContext to the factory so it handles standard container types, and define the MethodMarshalPlan structure.

#### 5A.1. Fix Projection Return Plans — COMPLETE

Fixed StringProjection's Direct return plan to match WrapperEmitter emission: `MarshalFromSwift<SwiftString>(new IntPtr(&result)).ToString()` with `RequiresUnsafe = true`. ClassProjection's Direct return was already correct (verified). All `[Trait("Stability", "PreSession5")]` tests updated and pass without exclusion.

#### 5A.2. GenericContext Support in Factory — COMPLETE

Added `GenericContext?` to `ProjectionContext`. Factory now resolves generic type parameters (τ_0_0 → T0) via `BlittableProjection`. Standard containers (Optional, Array, Dictionary) with resolvable inner types now project correctly.

**Eliminated all `GetIdiomaticCSharpType` callers from Emitter**: Zero results from `grep -rn "GetIdiomaticCSharpType" src/Swift.Bindings/src/Emitter/ --type cs`. Gate 5 passes.

**`TranslateBoundGenericTypeToCSharp` scope**: The factory returns null for user-defined bound generic types (e.g., `SwiftResult<T1,T2>`, `Optional<FrozenWithMemoryStruct>`) because `ProjectBoundGeneric` produces public types that violate `ISwiftObject` constraints on generic type parameters. These sites use `TranslateBoundGenericTypeToCSharp` as a fallback to produce raw ABI type names. Present in 13 Emitter files:
- **3 deferred-to-5B** (legacy emission): WrapperEmitter.Return.cs, WrapperEmitter.cs, PInvokeEmitter.cs
- **10 non-emission paths** (necessary fallbacks): MethodSignature.cs, PropertyHandler.cs, EnumHandler.CaseConstruction.cs, ModuleHandler.cs, MemberEmissionValidator.cs, ProtocolHandler.cs, ProtocolProxyEmitter.Helpers.cs, ProtocolSignatureHelper.cs, ProtocolConformanceValidator.cs, DefaultParameterOverloadEmitter.cs

The 10 non-emission fallback sites fire only when the factory can't project a user-defined bound generic (inner type is unsupported, e.g., frozen-with-memory-management structs). 5B's first acceptance gate: remove `TranslateBoundGenericTypeToCSharp` from the 3 deferred files; the 10 non-emission sites remain until `ProjectBoundGeneric` can produce public-vs-raw types correctly.

#### 5A.3. Define MethodMarshalPlan — COMPLETE

Created `src/Swift.Bindings/src/Marshaler/Projection/MethodMarshalPlan.cs` — pure data definition capturing method-level concerns: PublicSignature, PInvokeDeclaration, ParameterPlans, ReturnPlan, SwiftSelf, SwiftError, GenericMetadata, IndirectResult, Async, OptionalPointerWrapper, CallbackDeclarations. Supporting types: MethodSignatureInfo, PInvokeDeclarationInfo, ParameterMarshalInfo, SwiftSelfSetup (7 SwiftSelfKind variants), SwiftErrorSetup, GenericMetadataSetup, IndirectResultSetup, AsyncSetup, OptionalPointerWrapperSetup. Tests in MethodMarshalPlanTests.cs.

**Validation**: 3934 unit tests (0 failures), 700 integration tests (0 failures), golden files pass, library validation 27/32 (baseline maintained). All PreSession5 tests pass without exclusion.

---

### Session 5B: Collapse Emission — WrapperEmitter + ProtocolProxyEmitter.Receivers

**Goal**: Replace the inline marshalling code in WrapperEmitter and ProtocolProxyEmitter.Receivers with `MarshalPlan` rendering via `MarshalPlanRenderer`. This is the highest-impact session — it eliminates the largest concentration of old-architecture code and the debt added by Sessions A-E.

#### 5B.1. Collapse WrapperEmitter.Marshalling.cs

Replace the inline marshalling code (~630+ lines, increased by Sessions A-E) with `MarshalPlan` rendering. The projection produces the plan; the renderer walks `MarshalStatement` nodes and writes to CSharpWriter. The massive if/else chains for dictionaries, optionals, existentials, etc. collapse into `projection.GetParameterPlan(paramName)`.

Specific Session A-E debt to eliminate:
- `EmitOptionalExistentialParamConversion()` private method → replaced by `OptionalProjection(ExistentialProjection(...)).GetParameterPlan()`

#### 5B.2. Collapse WrapperEmitter.Return.cs

Replace the inline return code (~550+ lines, increased by Sessions A-E) with `MarshalPlan` rendering. The 4 near-identical Optional-existential blocks added by Sessions A-E all collapse into a single `OptionalProjection(ExistentialProjection(...)).GetReturnPlan()` call.

#### 5B.3. Collapse ProtocolProxyEmitter.Receivers Marshalling

Replace the 3 helper methods added by Sessions A-E with projection calls:
- `GetReceiverExistentialGetterConversion()` (~85 lines) → `ExistentialProjection.GetParameterElementConversion()` (parameter direction: C# interface → ExistentialContainer for MarshalToSwiftBuffer)
- `GetReceiverExistentialSetterConversion()` (~80 lines) → `ExistentialProjection.GetReturnElementConversion()` (return direction: ExistentialContainer → proxy class for C# implementation)
- `OverrideOptionalExistentialAbiType()` (~18 lines) → `OptionalProjection(ExistentialProjection(...)).PInvokeType`

These methods handle `Array<existential>` and `Dictionary<K, existential>` manually — `ArrayProjection(ExistentialProjection)` and `DictionaryProjection(_, ExistentialProjection)` already compute these compositions.

#### 5B.4. Replace ClosureEmitter.Async.cs Old-API Usage

Replace `TypeConversionHandler.GetParameterConversion()` call in async closure delegate wrapping with the inner return projection from `ClosureProjection`.

**Depends on**: Session 5A (fixed return plans, GenericContext, MethodMarshalPlan) — COMPLETE.

**First acceptance gate**: Remove `TranslateBoundGenericTypeToCSharp` from WrapperEmitter.Return.cs, WrapperEmitter.cs, and PInvokeEmitter.cs. These 3 files are the only remaining legacy emission paths using old bound generic translation.

**Required test**: For async methods with tuple/string/complex returns, assert that the emitted Swift callback parameter types exactly match the emitted C# callback signatures. This enforces the callback signature reconciliation that Session 2 deferred.

**Validation**: All unit + integration tests pass. Library validation 0 regressions. Golden-file diffs reviewed and updated. WrapperEmitter.Marshalling.cs and WrapperEmitter.Return.cs are each under 100 lines. The 3 receiver helper methods are deleted.

---

### Session 5C: Decompose + Clean — Conductor, MethodHandler, Dead Code

**Goal**: Clean up Conductor state, decompose the monolithic MethodHandler into composable handlers, and delete all remaining legacy code. After this session, the old 4-path type conversion system is fully replaced.

#### 5C.1. Conductor State Cleanup + Handler Interface Changes

Absorbed from Session 3e. Change `IHandler.Emit` to thread state explicitly instead of through Conductor:

- `CurrentPInvokeHelperContext` (28 usage sites) → pass as parameter through handler chain. Currently serves as parent→child communication between type handlers that don't directly call each other (dispatch goes through `HandleBaseDecl` → `IHandler.Emit`). Options: add to `IEnvironment` subtypes, or pass alongside Conductor.
- `NestedTypeRenames` (12 usage sites) → same pattern. Parent type handler sets renames, child reads them via Conductor. Move to TypeEnvironment or pass through HandleBaseDecl.
- `CompositionInterfaces` / `s_activeCompositionCollector` (ThreadStatic) → replace with explicit collector threaded through calls, or compute compositions in analysis phase.

#### 5C.2. Decompose into Method-Level Handlers

Split the monolithic MethodHandler into composable handlers that each populate one aspect of the `MethodMarshalPlan`:

- `ConstructorHandler` — payload assignment, skip result post-processing
- `InstanceMethodHandler` — SwiftSelf creation from type representation
- `StaticMethodHandler` — static keyword, no self parameter
- `SwiftErrorHandler` — SwiftError parameter + post-call exception check
- `GenericParameterHandler` — metadata pointers, PWT pointers, P/Invoke params
- `AsyncMethodHandler` — Swift Task wrapper, C# callback, TaskCompletionSource
- `IndirectResultHandler` — stack allocation, void P/Invoke return, result buffer read

Each handler implements `bool CanHandle(MethodDecl)` and `void Contribute(MethodMarshalPlan)`. The orchestrator runs all applicable handlers, then a renderer walks the plan and emits code.

#### 5C.3. Delete All Remaining Dead Code

Absorbed from Session 3f. After 5B collapses WrapperEmitter and receivers, the old APIs finally have zero callers:
- `TypeConversionHandler.GetIdiomaticCSharpType()`, `GetParameterConversion()`, `GetReturnConversion()`, `GetSwiftWrapperType()`, `IsConvertibleType()`
- `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp()` (all overloads)
- `WrapperEmitter.TranslateTypeSpecForConversion()` (both definitions)
- Supporting helpers: `IsElementTypeConverted`, `IsDictionaryKeyTypeConverted`, `GetRawArrayElementType`, etc.
- Session A-E artifacts: `EmitOptionalExistentialParamConversion`, `GetReceiverExistentialGetterConversion`, `GetReceiverExistentialSetterConversion`, `OverrideOptionalExistentialAbiType`
- If `TypeConversionHandler` / `BoundGenericsHandler` have no remaining public methods after deletion, delete the entire classes.

**Exit gate**: `grep -rn "GetIdiomaticCSharpType\|TranslateBoundGenericTypeToCSharp\|GetParameterConversion\|GetReturnConversion\|GetSwiftWrapperType\b\|IsConvertibleType\|TranslateTypeSpecForConversion\|GetReceiverExistentialGetterConversion\|GetReceiverExistentialSetterConversion\|EmitOptionalExistentialParamConversion" src/Swift.Bindings/src/ --include="*.cs" | grep -v "//\|test\|Test"` returns zero results.

**Validation**: All unit + integration tests pass. Library validation 0 regressions. Golden-file diffs reviewed. MethodHandler.cs and WrapperEmitter.cs are each under 200 lines (down from 896 and 978+).

---

## Summary

| Session | Phase | What | Status |
|---------|-------|------|--------|
| **1** | Build | MarshalledType DU + TypeProjectionFactory core + simple projections | **COMPLETE** (Feb 19) |
| **2** | Build | Complex projections (collections, optional, existential, closure, tuple, async) | **COMPLETE** (Feb 19) |
| **3** | Migrate | Factory-first for signatures/validators, ClassProjection, MarshalPlanRenderer, bool centralization | **COMPLETE** (Feb 20) |
| **4** | Lock Down | Consistency tests, MarshalPlan tests, golden files, deterministic hash fix, full validation | **COMPLETE** (Feb 20) |
| *A-E* | *(Bug fixes)* | *Library validation fixes (24/32 → 27/32); added ~590 lines of old-architecture debt* | *COMPLETE (Feb 20)* |
| **5A** | Decompose | Fix projection return plans, GenericContext support, MethodMarshalPlan definition | **COMPLETE** (Feb 20) |
| **5B** | Decompose | Collapse WrapperEmitter + ProtocolProxyEmitter.Receivers into MarshalPlan rendering | Pending — highest impact |
| **5C** | Decompose | Conductor state cleanup, MethodHandler decomposition, delete all dead legacy code | Pending |

Sessions 1-2 built the new type projection architecture alongside the old one. Session 3 wired the factory into all straightforward call sites. Session 4 proved the factory works via tests and golden files. Sessions A-E fixed library validation bugs but added ~590 lines of old-architecture debt (primarily in WrapperEmitter and ProtocolProxyEmitter.Receivers).

Session 5A built the foundation: correct projection plans, GenericContext for standard containers, and MethodMarshalPlan data structure. The factory handles all standard container types; user-defined bound generics (e.g., `SwiftResult<T1,T2>`) still fall back to `TranslateBoundGenericTypeToCSharp` in 13 Emitter files because `ProjectBoundGeneric` produces public types that violate `ISwiftObject` constraints. Session 5B is the highest-impact session — it collapses WrapperEmitter and the receiver helpers into MarshalPlan rendering, eliminating the largest concentration of old-architecture code including all Session A-E debt. Session 5C completes the migration by decomposing MethodHandler and deleting all legacy code. After Session 5C, the old 4-path type conversion system is fully replaced.
