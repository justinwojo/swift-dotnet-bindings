# Architecture Refactoring Plan

**Created**: March 13, 2026
**Last updated**: March 15, 2026

This document captures architectural improvements identified during a full-codebase deep dive (~103K LOC generator, ~12K LOC runtime, ~6K test facts, 88 validation targets). Each phase is independent and can be tackled in any order, though Phase 1 reduces risk for Phase 2.

---

## Current State Summary

The generator follows a clean 4-stage pipeline (Parser → TypeDatabase → Marshaler → Emitter) with well-designed abstractions at the type level (TypeProjectionFactory, ITypeProjection, MarshalPlan). The MSBuild SDK and runtime are production-quality. The ObjC binding pipeline is **completely independent** (zero shared code with the Swift emitter — safe to refactor without risk).

The pain points are concentrated in the **emitter layer** — specifically in how method/property/constructor handlers orchestrate validation, marshalling, and code generation. The original Microsoft team's emitter redesign proposal (`Completed/emitter-redesign-proposal.md`) identified this same problem; Phase 1 of that proposal (TypeProjectionFactory) shipped, but Phase 2 (method-level decomposition) was never implemented.

### Key Metrics

| Layer | LOC | Assessment |
|-------|-----|-----------|
| Generator handlers | ~33K | Duplicative, scattered validation — primary target |
| Generator wrapper emitters | ~14.4K | cdecl-specific; 24% of emitter layer |
| Closure infrastructure | ~14 files, cross-cutting | Self-contained subsystem, not decomposable into phases |
| Protocol proxy infrastructure | ~6.2K | Self-contained subsystem with inverted control flow |
| Generator projections | ~8K | Well-designed, don't touch |
| Generator parser | ~8K | Stable, minor improvements only |
| Runtime | ~12K | Clean abstractions, minor improvements only |
| MSBuild SDK | ~1K (props+targets) | Production-quality, don't touch |
| Tests | ~6K facts | Good coverage, large files need reorganization |

### Calling Convention Architecture (cdecl / CallConvSwift Hybrid)

The generator currently operates in a **hybrid mode**. It does NOT fully use cdecl or fully use CallConvSwift — it routes each method to one or the other via `WrapperStrategy` on `MethodDecl`:

| Strategy | When Used | Convention |
|----------|-----------|-----------|
| `CdeclMethod` | Methods where @_cdecl wrapper can be generated | Cdecl |
| `CdeclConstructor` | Frozen struct constructors (not failable, not generic) | Cdecl |
| `CdeclProperty` | Properties with non-blittable types (enums, strings, structs) | Cdecl |
| `LegacyCallConvSwift` | **Everything else** — async, generic parent with T in signature, inout, complex closures | CallConvSwift |

**The cdecl path generates TWO artifacts per method:**
1. A C# P/Invoke targeting the wrapper library (CallingConvention.Cdecl)
2. A Swift `@_cdecl` wrapper function that bridges from C calling convention to Swift calling convention

**The CallConvSwift path generates ONE artifact:**
1. A C# P/Invoke targeting the Swift dylib directly (CallConvSwift)

This means CallConvSwift is NOT dead code — it is the active fallback for every method that can't get an @_cdecl wrapper. The ~14.4K LOC of wrapper-specific code (MethodWrapperEmitter, ConstructorWrapperEmitter, PropertyWrapperEmitter, SubscriptWrapperEmitter, DestroyWrapperEmitter, WrapperEmitter partials, SwiftWrapperPostProcessor) exists solely because of the cdecl path.

**If CallConvSwift fully worked in the future**, the entire wrapper layer could be deleted with no changes to P/Invoke marshalling, type projections, or closure infrastructure. The refactoring phases in this document are designed to keep this flexibility — Phase 2's `MethodRepresentation` makes the wrapper a pluggable phase handler rather than a parallel code path woven through every handler.

### Subsystems That Are NOT Refactoring Targets

Two major subsystems were evaluated and determined to be **self-contained** — they interact with the handler layer but cannot be absorbed into it. They are excluded from Phase 2's decomposition scope.

**Closure Infrastructure** (~14 files, cross-cutting):
- ClosureHandler (analysis) → ClosureProjection (type mapping) → ClosureEmitter + 4 partials (code gen) → 4 bridge emitters (GenericClosureBridge, MethodClosureBridge, ProtocolExtensionClosureBridge, NestedClosureBridge)
- Closures are fundamentally cross-cutting: they affect P/Invoke signatures, Swift wrapper signatures, public C# signatures, AND callback declarations simultaneously. Bridge emitters can **take over method emission entirely** (not just contribute a phase).
- The closure subsystem cannot be expressed as a `IMethodPhaseHandler` because it branches into 4+ emission paths based on escaping/async/throwing/generic/nested combinations, and multiple outputs (1-3 callbacks + 1-3 P/Invokes + swift wrappers) are interdependent.
- **Refactoring approach**: Keep as self-contained subsystem. Phase 2's `MethodRepresentation` should have a `ClosureBridgeOverride` escape hatch for when bridge emitters take over. Phase 1's validation pipeline absorbs closure *gates* (IsSupportedClosure, IsCdeclCompatibleType) even though closure *emission* stays separate.

**Protocol Proxy Infrastructure** (~6.2K LOC):
- ProtocolProxyEmitter (4,126 LOC) + WitnessDispatchEmitter (2,098 LOC)
- Uses **inverted control flow**: Regular methods go C# → P/Invoke → Swift. Protocol dispatch goes Swift → receiver callback → C# impl → P/Invoke → witness accessor → result.
- Proxy methods use direct witness dispatch, not the standard method emission pipeline. Vtable structures, receiver methods (`[UnmanagedCallersOnly]`), and existential container handling are all protocol-specific.
- **Refactoring approach**: Keep as self-contained subsystem. Phase 1's validation pipeline can absorb protocol-specific gates (HasUnsupportedProtocolConstraints, etc.) but proxy emission stays independent.

---

## Phase 1: Unified Validation Pipeline ✅ COMPLETED

**Impact**: High | **Effort**: Medium | **Risk**: Low | **Completed**: March 14, 2026

### Problem

A full inventory found **150+ distinct validation gates** scattered across 5 tiers:

**Tier 1 — MemberEmissionValidator** (7 public methods):
- `CanEmitMethod()`, `CanEmitProperty()`, `CanEmitSubscript()` — comprehensive per-member-kind validation
- `ReferencesUnsupportedModule()` — SwiftUI/Combine gate (recursive TypeSpec check)
- `IsSynthesizedProtocolProperty()` — duplicate suppression
- `HasUnsupportedPropertyType()` — early-exit for unresolvable types
- `CountEmittableMembers()` — type annotation reporting

**Tier 2 — MethodValidationGates** (4 static methods):
- `HasUnsupportedProtocolConstraints()` — protocol constraint filtering
- `IsUnsupportedProtocolConstraint()` — PAT / self-requirement check
- `IsConditionalExtensionConstraint()` — extra-constraint detection
- `IsParentBaselineConstraint()` — parent-level constraint dedup

**Tier 3 — Per-Handler Inline Gates** (~70 conditions total):
- MethodHandler: ~30 skip conditions (generic parent, placeholder types, closures, bare generics, etc.)
- PropertyHandler: ~20 skip conditions (async accessors, existentials, closures, AnyType fallback, etc.)
- SubscriptHandler: ~12 skip conditions (static, AnyType, complex index params, etc.)
- EnumHandler: ~8 skip conditions (namespace enum, constraint checks, case collisions)

**Tier 4 — Wrapper Eligibility Gates** (~52 conditions total):
- ConstructorWrapperEmitter: 11 gates (xcframework mode, generic parent, closure params, async, non-copyable, nested struct, buffer pointer, variadic)
- MethodWrapperEmitter: 17 gates (accessor, SPI, generic, actor-isolated, async, inout, metatype, opaque return, etc.)
- PropertyWrapperEmitter: 10 gates (closure, async, actor-isolated, non-copyable, nested type, ObjC Optional setter)
- SubscriptWrapperEmitter: 14 gates (static, closure index, async, actor-isolated, opaque return, nested type)

**Tier 5 — Marshaler-Level Type Gates** (~15 conditions):
- ClosureHandler: IsSupportedClosure (5 sub-checks), IsSupportedClosureParameterType, IsSupportedClosureReturnType
- ExistentialHandler: IsSupportedExistential (max 8 witness tables)
- BoundGenericsHandler: HasBareGenericUsage, HasNonSwiftObjectGenericArg, TryGetFirstUnsatisfiedConstraint, TryGetFirstExistentialTypeArgument, HasLargeOptionalParams

**Key duplication**: BoundGenericsHandler gates (HasBareGenericUsage, HasNonSwiftObjectGenericArg, TryGetFirstUnsatisfiedConstraint) are called independently from MethodHandler, PropertyHandler, SubscriptHandler, and ConstructorHandler — each handler performs the same checks in slightly different order with slightly different error handling.

### Implemented Design

A single `MemberValidationPipeline` class with ordered gate phases. Rather than the originally proposed per-gate classes (`IValidationGate`), the implementation uses inline checks in a single method — simpler, fewer files, same benefits:

```csharp
public class MemberValidationPipeline
{
    // Emission validation: 14 gates across 6 ordered phases
    public ValidationResult ValidateMethodEmission(MethodDecl methodDecl, ValidationContext? context);

    // Wrapper eligibility: forwards to existing ShouldEmitWrapper methods
    public WrapperValidationResult ValidateMethodWrapperEligibility(MethodEnvironment env);
    public WrapperValidationResult ValidatePropertyWrapperEligibility(PropertyDecl propertyDecl, MethodEnvironment accessorEnv);
    public WrapperValidationResult ValidateSubscriptWrapperEligibility(SubscriptDecl subscriptDecl, AccessorDecl accessor, MethodEnvironment env);
}
```

**Gate phases in `ValidateMethodEmission`:**

| Phase | Gates | Source |
|-------|-------|--------|
| 1. Suppression | @_spi, module-internal, implicit+overriding ctor, synthesized protocol | Was inline in HandleBaseDecl |
| 2. Closure + module | Synthesized Codable, unsupported closures, SwiftUI/Combine refs, async tuple | Was in `ShouldSkipMethodEmission` |
| 3. Generic type callback | Thunk closure in PInvokeHelperContext, async in generic type | Was inline in ConstructorHandler + MethodHandler |
| 4. Protocol constraint | PAT / Self-requirement constraints (non-constructor) | Was in MethodHandler via `MethodValidationGates` |
| 5. Bound generic | Bare generic, non-ISwiftObject, unsatisfied constraint (non-accessor) | Was inline in ConstructorHandler + MethodHandler |
| 6. Generic ctor params | Method-own generic params on constructors | Was inline in ConstructorHandler |

**What remains in handlers:** Existential type argument accumulation (feeds bypass/bridge fallback logic that requires emission context). Dedup gates remain in HandleBaseDecl (stateful, shared with post-processors). Property accessor-level protocol constraint checks remain in PropertyHandler (accessor MethodDecls don't go through HandleBaseDecl — they're processed by PropertyHandler directly after type-handler iteration).

### Integration with Existing Reporting

The existing reporting system (ReportCollector + EmissionReport) is well-designed with 21 skip reasons and workaround recommendations. The validation pipeline enhances it:

- Each gate phase returns a `ValidationResult` with `SkipReason` enum + human-readable details
- `HandleBaseDecl` feeds `ValidationResult.Reason` directly into `ReportCollector.RecordMemberSkipped()`
- Synthesized protocol members use `ValidationResult.IsSynthesized` → `ReportCollector.RecordMemberSynthesized()`
- Wrapper eligibility results feed into `ModuleEmissionContext.IncrementWrapperSkipReason()`
- Every skip has a traceable phase and reason string (visible in `--verbose` output)

### What This Fixed

- Adding a new emission gate = adding one check in `ValidateMethodEmission`, no handler changes needed
- "Three parallel paths must stay aligned" for protocol constraints → one path in pipeline Phase 4
- Gate ordering is explicit (6 ordered phases) and tested (41 pipeline tests including 4 E2E)
- Handler inline gates (thunk closure, protocol constraints, bound generics, generic ctor) consolidated from MethodHandler + ConstructorHandler into pipeline Phases 3-6
- Wrapper eligibility gates (52 conditions across 4 classes) forwarded through pipeline API (ready for future consolidation)
- Safety net patterns removed from post-processor (~60% reduction)
- Diagnostics enhanced: every skip has a traceable `SkipReason` + details string

### Post-Processor Simplification ✅ DONE

Safety net patterns (b)-(f) have been removed from `SwiftWrapperPostProcessor`. Only 3 active patterns remain:

| Pattern | Status |
|---------|--------|
| EveryProtocol conformance removal | **Active** — unconditional by design |
| Internal type reference stripping | **Active** — requires swiftinterface parsing not yet available |
| Module/type name collision fix | **Active** — post-hoc rewriting for name conflicts |
| `self.` in free function | ✅ Removed — never fired (all emitters use extension blocks) |
| `__self.init()` in async init | ✅ Removed — prevented at emission time |
| Non-escaping closure in Task | ✅ Removed — prevented at emission time |
| Raw generic params τ_0_0 | ✅ Removed — prevented by `HasRawGenericTypeParams` gate in `DefaultParameterOverloadEmitter` + wrapper emitters |
| Mutating on let existential | Already removed (pre-Phase 1) |

Post-processor reduced by ~60% as planned. `ClosureParamPattern`, `RawGenericParamPattern` regexes and `ContainsRawGenericParam` method deleted.

### Migration Strategy (as executed)

1. ✅ Created `MemberValidationPipeline` + `ValidationContext` + `ValidationResult` (Session 1)
2. ✅ Integrated into `HandleBaseDecl` — replaces inline SPI, implicit+overriding, synthesized, ShouldSkipMethodEmission (Session 1)
3. ✅ Moved exact handler inline checks (not EvaluateHardGates superset) into pipeline Phases 3-6 (Session 2)
4. ✅ Removed duplicate checks from ConstructorHandler.Emit + MethodHandler.Emit (Session 2)
5. ✅ Fixed emission-time gap: `DefaultParameterOverloadEmitter` raw generic param gate (Session 2)
6. ✅ Removed safety-net patterns (b)-(f) from `SwiftWrapperPostProcessor` (Session 2)
7. ✅ Validated against all 90 library targets after each step — zero regressions
8. ✅ Migrated PropertyHandler bound generic gates to `ValidatePropertyEmission` (Session 2 — codex review follow-up)
9. Wrapper eligibility gates forwarded through pipeline API (ready for future inlining)

---

## Phase 2: Method Representation Decomposition — RECONSIDERED (Not Needed)

**Impact**: ~~Highest~~ Low | **Effort**: Large | **Risk**: Medium | **Status**: Reconsidered (2026-03-14)

### Reconsidered Rationale (2026-03-14)

A thorough analysis of the actual handler code revealed that the MethodRepresentation decomposition is not justified:

1. **Handlers are not duplicating each other.** PropertyHandler and SubscriptHandler already **delegate to `MethodHandler.Emit()`** for accessor emission — they're consumers of the method pipeline, not parallel reimplementations. The "~33K handler LOC" estimate included closure infrastructure (~9.9K), protocol proxy (~6.2K), and enum handlers (~4.5K), all of which are explicitly excluded from Phase 2.

2. **Shared infrastructure is already extracted.** `SignatureHandler`, `PInvokeEmitter`, `WrapperEmitter`, `TypeProjectionFactory`, `AccessorConversionVisitors`, and `MemberValidationPipeline` already handle the common patterns. Only ~2.5% genuine duplication exists across wrapper emitters (~50 lines of self-reconstruction + string return + direct return switch).

3. **The "projection parity pattern" is already solved.** PropertyHandler and SubscriptHandler use the visitor pattern via `IProjectionVisitor<T>` — adding a new projection type without implementing visitor methods causes a **compile error**. Only ProtocolProxyEmitter.Receivers still uses switch dispatches with `_ => null` fallback.

4. **Actual Phase 2 target is ~12.6K LOC** (MethodHandler 1,187 + PropertyHandler 994 + SubscriptHandler 777 + wrapper emitters ~8,400 + supporting ~1,600), not 33K. The handlers are already ~1K LOC each after Phase 1 removed validation logic.

**Conclusion:** Phase 1 addressed the actual pain point (scattered validation across 150+ gates). The remaining handler code is well-factored with clean delegation boundaries. A `MethodRepresentation` abstraction would add complexity over working code with minimal structural benefit.

**Alternative actions taken:** Small targeted improvements (utility dedup, return-marshalling unification in closure emitters) that address the few genuine duplication points without introducing new abstractions.

### Original Problem (preserved for reference)

MethodHandler (1,348 LOC + 5 partials), PropertyHandler (1,001 LOC), and ConstructorHandler all replicate the same orchestration logic:

1. Build environment (ClosureHandler, ExistentialHandler, BoundGenericsHandler, etc.)
2. Project each parameter type
3. Build P/Invoke signature
4. Build public C# signature
5. Generate marshalling code (setup → call → cleanup)
6. Generate Swift wrapper code
7. Handle return value marshalling
8. Handle error/async wrapping

Each handler implements this pipeline independently with copy-pasted logic. The "projection parity pattern" constraint ("when adding a new projection type, it must be handled in ALL switch dispatches across PropertyHandler, SubscriptHandler, ProtocolProxyEmitter.Receivers, EnumHandler, WrapperEmitter.Return, ClosureHandler") exists because there are 6+ parallel implementations of what should be one dispatch.

### Proposed Design

Implement the Phase 2 concept from the original emitter redesign proposal: decompose method emission into composable phases that build up a `MethodRepresentation`.

```csharp
public record MethodRepresentation
{
    // Phase 1: Public API signature
    public SignatureSpec PublicSignature { get; set; }

    // Phase 2: P/Invoke declaration
    public PInvokeSpec PInvoke { get; set; }

    // Phase 3: Pre-call marshalling (setup buffers, create SwiftSelf, etc.)
    public List<MarshallingStep> PreCallSteps { get; set; }

    // Phase 4: The actual P/Invoke call
    public CallSpec Call { get; set; }

    // Phase 5: Post-call processing (error check, result conversion)
    public List<MarshallingStep> PostCallSteps { get; set; }

    // Phase 6: Cleanup (dispose temporaries, free buffers)
    public List<MarshallingStep> CleanupSteps { get; set; }

    // Phase 7: Swift wrapper code (if needed)
    public SwiftWrapperSpec? SwiftWrapper { get; set; }

    // Escape hatch: closure bridge emitters can take over entirely
    public ClosureBridgeOverride? BridgeOverride { get; set; }
}
```

**Composable phase handlers:**

```csharp
public interface IMethodPhaseHandler
{
    // What kind of methods this handler applies to
    bool AppliesTo(MethodDecl decl, PhaseContext context);

    // Mutate the representation — add steps, modify signatures, etc.
    void Apply(MethodRepresentation repr, MethodDecl decl, PhaseContext context);
}
```

**Phase handler examples:**

| Handler | Responsibility |
|---------|---------------|
| `InstanceMethodPhase` | Add SwiftSelf to P/Invoke, add self-creation to PreCall |
| `StaticMethodPhase` | Add `static` keyword to public signature |
| `ConstructorPhase` | Route return to field assignment instead of return statement |
| `ThrowingPhase` | Add SwiftError to P/Invoke, add error check to PostCall |
| `AsyncPhase` | Wrap in Task, add continuation callbacks |
| `GenericMetadataPhase` | Add TypeMetadata + witness table params to P/Invoke |
| `IndirectReturnPhase` | Allocate result buffer in PreCall, convert in PostCall |
| `DirectReturnPhase` | Set P/Invoke return type, add conversion in PostCall |
| `CdeclWrapperPhase` | Generate Swift @_cdecl wrapper from representation |

**The `MethodRepresentation` is then rendered by a single `MethodRenderer`:**

```csharp
public sealed class MethodRenderer
{
    // Renders any MethodRepresentation to C# + Swift code
    // No per-handler rendering logic — one renderer for all methods
    public void Render(MethodRepresentation repr, CSharpWriter cs, SwiftWriter swift);
}
```

### Closure Bridge Escape Hatch

Closures are cross-cutting — bridge emitters (GenericClosureBridge, MethodClosureBridge, ProtocolExtensionClosureBridge, NestedClosureBridge) can **take over method emission entirely**. This happens when a method has closure parameters that require specialized handling (generic monomorphization, nested callback composition, protocol extension dispatch).

The `ClosureBridgeOverride` on `MethodRepresentation` handles this:

```csharp
public record ClosureBridgeOverride
{
    // The bridge emitter that will handle this method
    public IBridgeEmitter Bridge { get; init; }

    // Pre-computed emission artifacts (callbacks, P/Invokes, wrappers)
    public BridgeEmissionResult Result { get; init; }
}
```

**Flow:**
1. Phase pipeline runs normally (signature, P/Invoke, marshalling phases)
2. A `ClosureDetectionPhase` runs and checks if bridge emitters apply
3. If a bridge takes over: sets `BridgeOverride`, renderer delegates to it
4. If no bridge: normal rendering continues

This preserves the current architecture (bridge emitters are independent subsystems) while giving the `MethodRepresentation` pipeline a clean way to hand off control. The bridge emitters themselves don't need refactoring.

### Exclusions from Phase 2

These subsystems interact with the representation model but are NOT absorbed by it:

| Subsystem | LOC | Why Excluded | Interaction Point |
|-----------|-----|--------------|-------------------|
| Closure bridge emitters | ~14 files | Take over emission entirely; 4+ branching paths based on escaping/async/generic/nested | `ClosureBridgeOverride` escape hatch |
| ProtocolProxyEmitter | 4,126 | Inverted control flow (Swift → C# receivers); witness dispatch, vtables | None — fully independent pipeline |
| WitnessDispatchEmitter | 2,098 | Protocol-specific Swift accessors | None — fully independent pipeline |
| EnumHandler (case emission) | ~1,580 across 5 partials | Enum case construction/inspection is structurally different from method emission | Enum *method* emission uses MethodRepresentation; case emission stays separate |

### ModuleEmissionContext Integration

`ModuleEmissionContext` is the per-module state holder (dedup sets, wrapper tracking, deferred emission). It's well-designed and survives the transition to phases:

- **6 independent dedup systems** (signature, projected key, wrapper symbols, per-type infrastructure, composition interfaces, closure callbacks) are already encapsulated in ModuleEmissionContext
- Phases receive context explicitly: `phase.Execute(repr, decl, emissionContext)`
- The key constraint ("all code paths must pass `context.GetEmissionContext()`") becomes easier to enforce — phases always receive context as a parameter, not via handler nesting

### Calling Convention Flexibility

The `SwiftWrapperSpec` on `MethodRepresentation` is the key to calling convention flexibility. Currently, the wrapper layer (~14.4K LOC) is implemented as 5 separate emitter classes (MethodWrapperEmitter, ConstructorWrapperEmitter, PropertyWrapperEmitter, SubscriptWrapperEmitter, DestroyWrapperEmitter) that each reimplement self-parameter handling, error out-pointers, and return marshalling in parallel with the C# handlers.

Under the MethodRepresentation model, wrapper generation becomes a **phase handler** (`CdeclWrapperPhase`) that reads the same representation used for C# emission and produces the corresponding Swift wrapper. Self-handling, error-handling, and return marshalling are defined once by earlier phases — the wrapper phase just renders them in Swift syntax instead of C#.

This means:
- If CallConvSwift fully works in the future, remove `CdeclWrapperPhase` and set `SwiftWrapper = null`. No other phases change.
- If a new calling convention appears, add a new phase handler. No existing phases change.
- Wrapper eligibility guards (currently 52 conditions across 4 emitter classes) are already in the Phase 1 validation pipeline.

### What This Fixes

- Handler duplication drops from ~33K LOC to ~15K LOC (excluding closures + protocols which stay separate)
- "Projection parity pattern" constraint disappears — one dispatch in the renderer
- Each phase handler is independently testable (given a MethodDecl, does it produce the right steps?)
- Adding new calling convention features = adding one phase handler
- Properties and subscripts reuse the same phase pipeline with different phase handler sets
- WrapperEmitter (3,000+ LOC across 6 partials) collapses into `CdeclWrapperPhase`

### Migration Strategy

1. Define `MethodRepresentation`, `IMethodPhaseHandler`, and `ClosureBridgeOverride` contracts
2. Implement phase handlers for the simplest case first: static void method with blittable args
3. Add phases incrementally: instance methods → throwing → generics → async
4. Add `ClosureDetectionPhase` that delegates to existing bridge emitters (no bridge refactoring needed)
5. For each phase, write tests that compare MethodRepresentation output against current emitted code
6. Once all phases pass, swap MethodHandler to use the new pipeline
7. Repeat for PropertyHandler (property = getter method + setter method) and ConstructorHandler
8. Full 88-library validation after each handler migration

### Relationship to Phase 1

Phase 1 (validation pipeline) should land first. It removes the validation logic from handlers, leaving them as pure emission orchestrators. This makes the Phase 2 decomposition cleaner — phase handlers only deal with marshalling and code generation, not "should I emit this?"

---

## Phase 3: Data-Driven Apple Framework Definitions ✅ COMPLETED

**Impact**: Medium | **Effort**: Medium | **Risk**: Low | **Completed**: March 14, 2026

### Summary

All hardcoded Apple framework data in `AppleFrameworkRegistry.cs` has been extracted into a single `apple-frameworks.json` data file loaded as an embedded resource at startup. The public API is completely unchanged — only the backing store changed from C# initializers to JSON.

### What Was Delivered

- **Single `apple-frameworks.json`** in `src/Swift.Bindings/src/Data/` with all 70 framework definitions covering module sets, value types, type remaps, ObjC prefixes, namespace remaps, and platform availability
- **JSON schema** (`apple-frameworks.schema.json`) for validation
- **Static constructor** loads and deserializes the single embedded JSON at startup
- **11 parity tests** verifying JSON-loaded data matches original hardcoded data for every public method
- `AppleFrameworkRegistry.cs` reduced from ~480 LOC of hardcoded data to ~170 LOC of loader + API

### Benefits Realized

- Adding a new Apple framework = adding an entry to `apple-frameworks.json`, no C# changes needed
- Framework knowledge is auditable and version-controllable (JSON diffs in PRs)
- Schema validation catches missing required fields

---

## Session Breakdown

3 completed, Phase 2 reconsidered (not needed). Each session is mostly autonomous. Start each session by referencing this doc. End each session with full validation (`run-tests.sh` + `validate-libraries.sh`). Use `/next-session` between sessions for continuity.

---

### Sessions 1-2: Validation Pipeline (Phase 1) ✅ COMPLETED

Executed across two sessions. See Session Log below for details. Summary:
- Session 1: Pipeline foundation + HandleBaseDecl integration + codex review fixes
- Session 2: Handler gate migration (Phases 3-6) + emission-time gap fixes + safety net removal + codex review fixes

---

### Session 3: MethodRepresentation Foundation + MethodHandler (Phase 2a)

**Goal**: Define the `MethodRepresentation` model, implement core phase handlers, build the renderer, and migrate `MethodHandler` to the new pipeline.

**Steps:**
1. Read all MethodHandler code (main + 5 partials), WrapperEmitter code (main + 5 partials), PInvokeEmitter, SignatureHandler
2. Define `MethodRepresentation`, `IMethodPhaseHandler`, `PhaseContext`, `ClosureBridgeOverride` contracts
3. Implement core phases in order of complexity:
   - `StaticMethodPhase` — add `static` keyword (simplest)
   - `InstanceMethodPhase` — add SwiftSelf to P/Invoke + self-creation
   - `ConstructorPhase` — route return to field assignment
   - `ThrowingPhase` — add SwiftError to P/Invoke + error check
   - `GenericMetadataPhase` — add TypeMetadata + witness table params
   - `DirectReturnPhase` — set P/Invoke return type + conversion
   - `IndirectReturnPhase` — allocate result buffer + conversion
4. Build `MethodRenderer` — single class that renders any `MethodRepresentation` to C# + Swift
5. Add `ClosureDetectionPhase` — checks if bridge emitters apply, sets `BridgeOverride` (delegates to existing emitters, no bridge refactoring)
6. Migrate `MethodHandler.Emit()` to build `MethodRepresentation` + call `MethodRenderer`. Start with non-closure non-async methods, expand to full coverage
7. Write phase-level unit tests (given a MethodDecl, does each phase produce correct steps?)
8. Run `run-tests.sh` + `validate-libraries.sh`

**Exit criteria**: MethodHandler fully migrated. All tests pass. All validation targets match baseline. PropertyHandler/ConstructorHandler/SubscriptHandler still use old path (migrated next session).

**Risk**: MethodHandler is the largest and most complex handler. The WrapperEmitter partials (~9K LOC) need careful mapping to `CdeclWrapperPhase`. If a single session isn't enough, split the WrapperEmitter migration to Session 3.

**Estimated scope**: ~3,000-4,000 LOC new (phases + renderer + tests), ~2,500 LOC removed from MethodHandler.

---

### Session 4: Remaining Handlers + Async/Wrapper Phases (Phase 2b)

**Goal**: Migrate PropertyHandler, ConstructorHandler, and SubscriptHandler to `MethodRepresentation`. Add remaining phases.

**Steps:**
1. Add remaining phases:
   - `AsyncPhase` — Task wrapping, continuation callbacks
   - `CdeclWrapperPhase` — generate Swift @_cdecl wrapper from representation
   - `DefaultParameterOverloadPhase` — generate overloads for defaulted params
2. Migrate PropertyHandler — property = getter method + setter method through same phase pipeline. ~1,001 LOC handler → reuse existing phases
3. Migrate ConstructorHandler — uses ConstructorPhase + existing phases. Handle failable constructors
4. Migrate SubscriptHandler — subscript = getter + setter through same pipeline with index parameters
5. Clean up: remove old handler code that's now dead, remove WrapperEmitter partials absorbed by CdeclWrapperPhase
6. Update CLAUDE.md: remove critical constraints eliminated by unified rendering (projection parity pattern, etc.)
7. Run `run-tests.sh` + `validate-libraries.sh`

**Exit criteria**: All handlers migrated. Handler LOC reduced from ~33K to ~15K. All tests pass. All validation targets match baseline.

**Risk**: PropertyHandler and SubscriptHandler have their own quirks (property-specific dedup, subscript index parameter complexity). These are smaller than MethodHandler though — migration should be more straightforward since the phase infrastructure already exists.

**Estimated scope**: ~1,500 LOC new (remaining phases + tests), ~8,000 LOC removed from handlers.

---

### Session 5: Data-Driven Apple Frameworks (Phase 3) ✅ COMPLETED

**Goal**: Extract hardcoded AppleFrameworkRegistry data into per-framework JSON files.

**Completed** (2026-03-14): All steps executed successfully. See Session Log below for details.

**Actual scope**: 70 JSON files created (vs estimated ~30), ~1,444 LOC added, ~462 LOC removed. 11 parity tests. Registry API unchanged. All 7485 tests pass. 89/90 validation targets pass (1 pre-existing).

Note: Executed as Session 5 (out of plan order — Phase 3 was tackled before Phase 2 because it was lower risk and independent).

---

### Session Summary

| Session | Phase | Primary Deliverable | LOC Delta | Risk | Status |
|---------|-------|--------------------|-----------| -----| -------|
| 1 | Phase 1 | Validation pipeline foundation + HandleBaseDecl integration | +1K / -0.1K | Low | ✅ Done |
| 2 | Phase 1 | Handler gate migration + safety net removal + codex fixes | +0.3K / -0.5K | Low | ✅ Done |
| 3 | Phase 2a | ~~MethodRepresentation + phases + renderer + MethodHandler migration~~ | ~~+3.5K / -2.5K~~ | ~~Medium~~ | Reconsidered |
| 4 | Phase 2b | ~~PropertyHandler + ConstructorHandler + SubscriptHandler migration~~ | ~~+1.5K / -8K~~ | ~~Medium~~ | Reconsidered |
| 5 | Phase 3 | JSON framework definitions + registry loader | +1.4K / -0.5K | Low | ✅ Done |

**Autonomous operation**: Each session can run with minimal human input. The doc provides the design, the code provides the source of truth, and the validation scripts provide the gate. The only reason to pause is if validation fails in an unexpected way that requires a design decision.

---

## Success Criteria

| Metric | Current | Target |
|--------|---------|--------|
| Validation gate locations | 150+ across 5 tiers, 10+ files | ✅ 1 pipeline with 14 ordered gates across 6 phases (Phase 1 — done) |
| Handler LOC (excl. closures/protocols) | ~33K (original estimate) | ~17K actual — already well-factored, Phase 2 not needed |
| CLAUDE.md critical constraints | ~30 entries | ~15 entries (Phases 1+2) |
| Files to edit for new validation gate | 3-5 | ✅ 1 pipeline method (Phase 1 — done) |
| Files to edit for new projection type | 6+ switch dispatches | Visitor pattern (compile-time exhaustive) in PropertyHandler/SubscriptHandler — already safe |
| Post-processor patterns | 8 (6 safety nets) | ✅ 3 active patterns (Phase 1 — done) |
| Framework data format | ~~C# source code~~ | ✅ JSON files (Phase 3 — done) |
| 88-library validation | Baseline | ✅ Zero regressions after Phases 1 + 3 |

---

## Out of Scope

These are important but separate from this refactoring:

- **Closure bridge emitter internals** — GenericClosureBridge, MethodClosureBridge, ProtocolExtensionClosureBridge, NestedClosureBridge are self-contained and working. Phase 2 adds an escape hatch for them, not a rewrite.
- **ProtocolProxyEmitter / WitnessDispatchEmitter internals** — Inverted control flow is architecturally correct for protocol dispatch. Not a candidate for MethodRepresentation absorption.
- **Test reorganization** — Large test files (EmitterTests has 105 files) need subdirectory grouping. Worth doing but orthogonal.
- **Runtime improvements** — Unifying metadata caching patterns, moving marshalling helpers to generator. Low priority.
- **Golden file expansion** — Adding golden files for third-party libraries. Worth doing but orthogonal.
- **ObjC binding integration** — Replacing Objective Sharpie. Separate initiative (see `Future/objc-binding-integration.md`). ObjC pipeline is fully isolated (zero shared code with Swift emitter).

---

## Session Log

### Session 1 (2026-03-14): Pipeline Foundation

**Completed:**
- Created `ValidationContext.cs` (87 LOC): `ValidationContext`, `ValidationResult`, `WrapperValidationResult`
- Created `MemberValidationPipeline.cs` (162 LOC): emission validation + wrapper eligibility forwarding
- Integrated pipeline into `HandleBaseDecl` in `IHandler.cs` — replaces 4 inline checks (SPI, implicit+overriding ctor, synthesized protocol, ShouldSkipMethodEmission) with `pipeline.ValidateMethodEmission()`
- Created `MemberValidationPipelineTests.cs` (701 LOC): 25 tests covering emission validation, parity, gate ordering
- Wrapper eligibility API complete (`ValidateMethodWrapperEligibility`, `ValidatePropertyWrapperEligibility`, `ValidateSubscriptWrapperEligibility`) — all forwarding to existing ShouldEmitWrapper methods, ready for handler integration

**Attempted and reverted — handler inline gate migration (Step 4+6):**
- Added `MemberGateEvaluator.EvaluateHardGates` to the pipeline → caused behavioral changes. EvaluateHardGates is a SUPERSET of what handler inline checks do (adds associated type refs, internal type refs, raw generic params). This caused methods that previously emitted (even if broken) to be skipped earlier, creating wrapper symbol mismatches: C# referenced wrapper library symbols, but the Swift wrapper no longer contained them.
- Root cause: handler inline checks are NOT the same as EvaluateHardGates. Handlers check {bare generic, non-SwiftObject, unsatisfied constraint} per-argument. EvaluateHardGates additionally checks {associated type refs, internal types, raw generic params}. Using EvaluateHardGates as a "consolidation" was actually adding new gates.
- Lesson: to consolidate, must move the EXACT handler checks (not a superset). Must verify each moved check doesn't change which methods are emitted.

**Attempted and reverted — safety net removal (Step 8):**
- Removed patterns (b)-(f) from `SwiftWrapperPostProcessor` → caused regressions at the time.
- Session 2 later proved: pattern (b) never actually fired on Alamofire (closure bridge methods are inside extension blocks, correctly excluded by `isInsideExtension`). Pattern (f) was fixed by adding `HasRawGenericTypeParams` gate to `DefaultParameterOverloadEmitter.EmitDebugParamWrapper`. Both gaps resolved and safety nets successfully removed in Session 2.

**Codex review fixes (applied same session):**
- Finding 1 (P2): ModuleHandler now uses the pipeline — `ValidateMethodEmission` called for free functions. Added gate 2a: `IsModuleInternal` for module-level functions (ParentDecl is ModuleDecl).
- Finding 2 (P2): `GetConstructorWrapperRejectionReason` now mirrors all 11 gates from `ConstructorWrapperEmitter.ShouldEmitWrapper` (was missing closure/async-closure, non-copyable params, nested frozen params, buffer pointer, variadic expansion, `CanEmitGenericClassConstructorWrapper`). 5 methods made `internal` on ConstructorWrapperEmitter for pipeline access.
- Finding 3 (P3): `ValidationResult` now has typed `IsSynthesized` flag + `Synthesized()` factory. IHandler.cs checks `result.IsSynthesized` instead of string-matching on `Details`.

**Files touched in Session 1:**

| File | Status | Notes |
|------|--------|-------|
| `ValidationContext.cs` | NEW | 93 LOC — context + result records (with `IsSynthesized` flag) |
| `MemberValidationPipeline.cs` | NEW | 180 LOC — pipeline class with full constructor rejection reasons |
| `MemberValidationPipelineTests.cs` | NEW | ~780 LOC — 29 tests |
| `IHandler.cs` | MODIFIED | pipeline call replaces 4 inline checks in HandleBaseDecl |
| `ModuleHandler.cs` | MODIFIED | pipeline call replaces inline IsModuleInternal/IsSpiProtected |
| `ConstructorWrapperEmitter.cs` | MODIFIED | 5 methods `private` → `internal` for pipeline access |
| `MethodValidationGates.cs` | UNCHANGED | |
| `SwiftWrapperPostProcessor.cs` | UNCHANGED | safety nets remain |

**Verification at session end:**
- 7504 unit tests pass (0 failures, 1 known skip)
- Tier 1 validation: 35/36 (1 pre-existing failure)
- No baseline regressions from pipeline changes

---

### Session 2 (2026-03-14): Handler Gate Migration (Phases 3-6)

**Goal**: Move handler inline gates into the pipeline. Fix emission-time gaps. Remove safety nets.

**Completed:**

1. **Added `MethodValidationGates.HasUnsupportedProtocolConstraints(MethodDecl, ITypeDatabase)` overload** — pipeline can call without MethodEnvironment. Original `(MethodEnvironment)` overload delegates to it.

2. **Extended `MemberValidationPipeline.ValidateMethodEmission` with 4 new gate phases:**
   - **Phase 3 — Generic type callback** (thunk closure in PInvokeHelperContext):
     - Constructor: closure requiring thunk → skip
     - Method: closure thunk OR async, with MethodClosureBridge/NestedClosureBridge eligibility exceptions → skip
     - Protocol extension methods always let through
     - Creates fresh `ClosureHandler(typeDatabase)` for RequiresThunk/GetClosureTypeSpec checks
   - **Phase 4 — Protocol constraint** (non-constructor only):
     - `HasUnsupportedProtocolConstraints(methodDecl, typeDatabase)` → skip
   - **Phase 5 — Bound generic gates** (non-accessor only):
     - Per-argument loop: bare generic usage, non-ISwiftObject bound generic, unsatisfied constraint → skip
     - Creates fresh `BoundGenericsHandler(typeDatabase)` — same as handlers did
     - Existential type argument checks intentionally remain in handlers (accumulate state for bypass/bridge)
   - **Phase 6 — Generic constructor own params** (constructor only):
     - Method-own generic params (not inherited from parent type) → skip

3. **Updated HandleBaseDecl** to pass real `ValidationContext` with `PInvokeHelperContext` from `TypeHandlerContext` (was passing `null`).

4. **Cleaned up ConstructorHandler.Emit:**
   - Removed: thunk closure check, bare generic/non-SwiftObject/unsatisfied constraint loop, generic constructor own params
   - Kept: existential type argument accumulation loop + bypass/bridge fallback logic
   - Net: ~80 lines removed

5. **Cleaned up MethodHandler.Emit:**
   - Removed: thunk closure + async check, HasUnsupportedProtocolConstraints, bare generic/non-SwiftObject/unsatisfied constraint loop
   - Kept: existential type argument accumulation loop (unsupported + supported in non-container) + bridge dispatch
   - Net: ~90 lines removed

6. **Updated 5 tests** that called handler.Emit() directly to test through the pipeline instead:
   - `Emit_GenericConstructor_SkippedBecauseCSharpDoesNotSupportGenericConstructors` → tests Phase 6
   - `Emit_GenericMethod_WithAssociatedTypeProtocolConstraint_SkipsEmission` → tests Phase 4
   - `MethodHandler_ThunkClosureInGenericType_SkipsEmission` → tests Phase 3
   - `MethodHandler_AsyncMethodInGenericType_SkipsEmission` → tests Phase 3
   - `ConstructorHandler_ThunkClosureInGenericType_SkipsEmission` → tests Phase 3

7. **Added 8 new pipeline tests** (Phase 3-6 coverage):
   - Thunk closure, async in generic type, no PInvokeHelper bypass, protocol extension bypass
   - Protocol constraint with associated types, constructor skips protocol check
   - Generic constructor own params, inherited params emit

8. **Gap investigation — pattern (b) self-without-_self:** Investigated all 90 validation libraries. Pattern (b) does NOT fire on any library. The `self.adapt()` calls in Alamofire are inside extension blocks (correctly excluded by `isInsideExtension` check). No gap to fix.

9. **Gap fix — pattern (f) raw generic params:** Found 2 libraries (ObjectMapper, RxSwift) with `τ_0_0` in `_dbg_` debug-parameter-overload wrappers. Root cause: `DefaultParameterOverloadEmitter.EmitDebugParamWrapper` used `RenderSwiftTypeSpec` on raw generic return types. Fix: added `WrapperValidation.HasRawGenericTypeParams` early return that skips the entire wrapper (no Swift emission, no MangledName retarget, no UsesWrapperLibrary flag). Only strips debug params from CSSignature so the method falls through to its original mangled name via CallConvSwift.

10. **Removed safety net patterns (b)-(f) from SwiftWrapperPostProcessor:**
    - Patterns (b) self-without-_self, (c) __self.init, (e) non-escaping-closure-in-Task, (f) raw-generic-param all removed
    - Pattern (a) EveryProtocol() remains (unconditional by design, not a safety net)
    - Removed `ClosureParamPattern`, `RawGenericParamPattern` regexes, `ContainsRawGenericParam` method
    - Simplified `IsSilgenNameBroken`, `IsExtensionBroken`, `IsStandaloneFuncBroken` to only check EveryProtocol
    - Updated 11 tests from "stripped" to "preserved" assertions, deleted 10 safety-net-warning tests

**Codex review fixes (applied same session):**
- Finding 1 (P1): First version of `EmitDebugParamWrapper` raw generic fix retargeted `MangledName` to the nonexistent `DBG_*` symbol even when no Swift wrapper was emitted. Fixed: early return skips the entire wrapper including `MangledName`/`UsesWrapperLibrary` update. Method falls through to original mangled name.
- Finding 2 (P3): Updated tests replaced end-to-end emission assertions with direct pipeline calls, leaving handler integration under-tested. Fixed: added 4 end-to-end integration tests that go through `ModuleHandler → HandleBaseDecl → pipeline → handler`, verifying gates actually prevent emission. Thunk closure test validates pipeline directly (module-level free functions don't have `PInvokeHelperContext`; gate fires in type handler `HandleBaseDecl`).

**Files touched:**

| File | Status | Notes |
|------|--------|-------|
| `MemberValidationPipeline.cs` | MODIFIED | +65 LOC (Phases 3-6), updated doc comment |
| `MethodValidationGates.cs` | MODIFIED | +6 LOC (MethodDecl overload, original delegates) |
| `IHandler.cs` | MODIFIED | ValidationContext creation, pass to pipeline |
| `MethodHandler.cs` | MODIFIED | -170 LOC (removed gates from ConstructorHandler.Emit + MethodHandler.Emit) |
| `DefaultParameterOverloadEmitter.cs` | MODIFIED | +10 LOC (raw generic param early return in EmitDebugParamWrapper) |
| `SwiftWrapperPostProcessor.cs` | MODIFIED | -80 LOC (safety net patterns removed) |
| `SwiftWrapperPostProcessorTests.cs` | MODIFIED | 11 tests updated, 10 tests deleted |
| `ConstructorHandlerOutputTests.cs` | MODIFIED | 1 test updated to use pipeline |
| `MethodHandlerOutputTests.cs` | MODIFIED | 4 tests updated to use pipeline |
| `MemberValidationPipelineTests.cs` | MODIFIED | +12 tests (8 gate + 4 E2E), +4 helpers |

**Verification:**
- 7516 unit tests pass (0 failures, 1 known skip)
- 89/90 validation targets pass (1 pre-existing failure)
- No regressions (compile status unchanged across all libraries)
- Zero raw generic params across all 90 validation libraries (verified post-fix)
- Zero safety net pattern matches across all 90 validation libraries (verified pre-removal)
- Zero orphaned `DBG_*` symbols (verified: ObjectMapper/RxSwift have no `DBG_` references in Swift or C# output)

---

### Session 5 (2026-03-14): Data-Driven Apple Frameworks (Phase 3) ✅

**Completed:**
- Replaced all hardcoded Apple framework data in `AppleFrameworkRegistry.cs` with a single `apple-frameworks.json` loaded as an embedded resource at startup
- Created JSON schema (`apple-frameworks.schema.json`) for validation
- Static constructor deserializes the single embedded JSON at startup
- `AppleFrameworkRegistry.cs` reduced from ~480 LOC hardcoded data to ~170 LOC loader + API
- Public API completely unchanged — only backing store changed

**Files touched:**

| File | Status | Notes |
|------|--------|-------|
| `src/Swift.Bindings/src/Data/apple-frameworks.json` | NEW | All 70 framework definitions in one file |
| `src/Swift.Bindings/src/Data/apple-frameworks.schema.json` | NEW | JSON schema for validation |
| `src/Swift.Bindings/src/Swift.Bindings.csproj` | MODIFIED | EmbeddedResource for single JSON file |
| `src/Swift.Bindings/src/TypeDatabase/AppleFrameworkRegistry.cs` | MODIFIED | Hardcoded data → JSON loader (480→170 LOC) |
| `AppleFrameworkRegistryTests.cs` | NEW | 11 parity tests (480 LOC) |
| `.validation-baseline.json` | MODIFIED | Updated SHA |

**Verification at session end:**
- 7485 unit tests pass (0 failures)
- 89/90 validation targets pass (1 pre-existing failure)
- No baseline regressions
