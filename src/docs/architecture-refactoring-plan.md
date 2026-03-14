# Architecture Refactoring Plan

**Created**: March 13, 2026
**Last updated**: March 13, 2026

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

## Phase 1: Unified Validation Pipeline

**Impact**: High | **Effort**: Medium | **Risk**: Low

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

### Proposed Design

Create a single `MemberValidationPipeline` that runs once per member declaration and returns a definitive answer:

```csharp
public sealed class MemberValidationPipeline
{
    // Single entry point — replaces 150+ scattered checks
    public ValidationResult Validate(BaseDecl member, ValidationContext context);
}

public record ValidationResult
{
    public bool ShouldEmit { get; init; }
    public bool ShouldEmitWrapper { get; init; }  // Tier 4 result
    public string? SkipReason { get; init; }       // For diagnostics/reporting
    public SkipCategory Category { get; init; }    // Maps to SkipReason enum (21 values)
}
```

**Gate categories to consolidate:**

1. **Module gates**: Unsupported modules (SwiftUI, Combine, _Concurrency) — currently in MemberEmissionValidator + inline handler checks
2. **Type gates**: Unsupported type patterns (bare generics, non-SwiftObject generic args, unsupported existentials) — currently in BoundGenericsHandler + ExistentialHandler + inline handler checks
3. **Closure gates**: Unsupported closure parameter types, thunk requirements — currently in ClosureHandler + inline handler checks
4. **Constraint gates**: Protocol constraint filtering — currently in MethodValidationGates + MemberEmissionValidator (three parallel paths)
5. **Dedup gates**: Raw signature dedup, projected C# key dedup — currently in BaseHandler.HandleBaseDecl
6. **Suppression gates**: Underscore-prefix, @_spi, synthesized protocol methods, implicit+overriding constructors — currently in BaseHandler + MemberEmissionValidator
7. **Member-specific gates**: Property-only checks (async accessors, AnyType), method-only checks (placeholder types)
8. **Wrapper eligibility gates**: All Tier 4 checks — currently in 4 separate wrapper emitter classes

**Each gate becomes a class implementing `IValidationGate`:**

```csharp
public interface IValidationGate
{
    GateCategory Category { get; }
    bool AppliesTo(MemberKind kind); // Method, Property, Constructor, Subscript
    ValidationResult Check(BaseDecl member, ValidationContext context);
}
```

### Integration with Existing Reporting

The existing reporting system (ReportCollector + EmissionReport) is well-designed with 21 skip reasons and workaround recommendations. The validation pipeline should enhance it, not replace it:

- Each `IValidationGate` maps to a `SkipReason` enum value
- `ValidationResult.SkipReason` feeds directly into `ReportCollector.RecordMemberSkipped()`
- Wrapper eligibility results feed into `ModuleEmissionContext.IncrementWrapperSkipReason()`
- The pipeline produces richer diagnostics than today because every skip has a single, traceable gate class

### What This Fixes

- Adding a new gate = adding one class, registering it in the pipeline
- "Three parallel paths must stay aligned" constraints disappear — one path
- Gate ordering is explicit and testable
- Wrapper eligibility gates (52 conditions across 4 classes) consolidate alongside emission gates
- Diagnostics get free skip-reason reporting (useful for `--verbose` output)
- ~30% of CLAUDE.md "critical constraints" become unnecessary

### Post-Processor Simplification (Bonus)

`SwiftWrapperPostProcessor` has 8 patterns, but **6 are safety nets** — they're now prevented at emission time and fire regression warnings if they match. Only 2 patterns actively fire:

| Pattern | Status | Action |
|---------|--------|--------|
| EveryProtocol conformance removal | **Active** | Keep |
| Internal type reference stripping | **Active** | Keep |
| Module/type name collision fix | **Active** | Keep |
| `self.` in free function | Safety net | Remove after Phase 1 (gate prevents emission) |
| `__self.init()` in async init | Safety net | Remove after Phase 1 |
| Non-escaping closure in Task | Safety net | Remove after Phase 1 |
| Raw generic params τ_0_0 | Safety net | Remove after Phase 1 |
| Mutating on let existential | Already removed | N/A |

After Phase 1 lands, the post-processor can be reduced by ~60% — the validation pipeline prevents the patterns that the safety nets catch.

### Migration Strategy

1. Create `MemberValidationPipeline` with all existing gates as `IValidationGate` implementations
2. Add integration tests: for every member in the test library, assert pipeline result matches current emission behavior
3. Replace handler-level checks one at a time (PropertyHandler first — it has the most complete validation, ~20 conditions)
4. Migrate wrapper eligibility gates from 4 wrapper emitter classes into the pipeline
5. Remove redundant checks from BaseHandler.HandleBaseDecl as they move into the pipeline
6. Remove safety-net patterns from SwiftWrapperPostProcessor
7. Validate against all 88 library targets after each handler migration

---

## Phase 2: Method Representation Decomposition

**Impact**: Highest | **Effort**: Large | **Risk**: Medium

### Problem

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

## Phase 3: Data-Driven Apple Framework Definitions

**Impact**: Medium | **Effort**: Medium | **Risk**: Low

### Problem

`AppleFrameworkRegistry` is the single source of truth for Apple framework heuristics, but all knowledge is hardcoded in C#:

- **429 ValueType entries** — explicit catalog of types that should NOT be ObjC-bridged
- **209 TypeNameRemap entries** — Swift name → .NET name mappings (e.g., `URLSession` → `NSUrlSession`)
- **31 AutoBridge modules** — modules whose types are automatically ObjC-bridged
- **50 OptionalFallback modules** — superset of AutoBridge, used for container element fallback
- **32 ObjC prefixes** — heuristic prefix detection (UI, NS, MK, etc.)

This works but:
- Adding a new Apple framework requires editing C# source code and recompiling the generator
- Missing a ValueType entry silently bridges a struct as a class (no error, wrong behavior)
- Two parallel ObjC detection paths (NamedTypeSpec vs SwiftTypeName) must stay manually in sync
- No way for binding authors to contribute framework knowledge without modifying the generator

### Proposed Design

Extract framework definitions into per-framework data files loaded at startup:

```
src/Swift.Bindings/src/Data/AppleFrameworks/
├── Foundation.json
├── UIKit.json
├── AVFoundation.json
├── CoreGraphics.json
├── ...
└── _schema.json          # JSON Schema for validation
```

**Per-framework file format:**

```json
{
  "module": "UIKit",
  "autoBridge": true,
  "optionalFallback": true,
  "objcPrefixes": ["UI"],
  "namespace": "UIKit",
  "valueTypes": [
    "UIEdgeInsets",
    "UIOffset",
    "UIFloatRange",
    "UIView.ContentMode",
    "UIView.AnimationCurve"
  ],
  "typeRemaps": {
    "UIImage.RenderingMode": "UIImageRenderingMode",
    "UIView.AutoresizingMask": "UIViewAutoresizing"
  },
  "excludeFromXml": [
    "NSUnderlineStyle"
  ]
}
```

**AppleFrameworkRegistry becomes a loader:**

```csharp
public sealed class AppleFrameworkRegistry
{
    // Load all framework definitions from embedded resources at startup
    public static AppleFrameworkRegistry LoadFromEmbeddedResources();

    // Same API surface as today, but backed by data instead of hardcoded sets
    public bool IsValueType(string module, string typeName);
    public string? GetTypeRemap(string module, string swiftName);
    public bool IsAutoBridgeModule(string module);
    // ...
}
```

### What This Fixes

- Framework knowledge is auditable and version-controllable (JSON diffs in PRs)
- Adding a new framework = adding a JSON file, no C# changes needed
- Schema validation catches missing required fields at build time
- Eliminates the "NSUnderlineStyle excluded from XML intentionally" class of constraints — exclusions are explicit in the data file
- Unifies the two ObjC detection paths — both read from the same data source
- Enables future community contribution of framework definitions

### What Stays in Code

- The `IsObjCModuleType()` / `TryCreateSyntheticRecord()` logic stays — it interprets the data
- The `ConcatWithOverlapDedup` nested type flattening stays (but gets tested more thoroughly)
- Module aliasing stays (but moves from hardcoded dict to framework file metadata)

### Migration Strategy

1. Define JSON schema with all current fields
2. Generate JSON files from current hardcoded data (script to extract from AppleFrameworkRegistry.cs)
3. Add JSON loader alongside existing hardcoded implementation
4. Add integration test: JSON-loaded registry produces identical results to hardcoded registry for all 88 validation libraries
5. Once confirmed identical, remove hardcoded data and switch to JSON-only
6. Full 88-library validation to confirm zero regressions

---

## Session Breakdown

4 sessions, each designed to be mostly autonomous. Start each session by referencing this doc. End each session with full validation (`run-tests.sh` + `validate-libraries.sh`). Use `/next-session` between sessions for continuity.

---

### Session 1: Validation Pipeline (Phase 1)

**Goal**: Extract all 150+ validation gates into a single `MemberValidationPipeline`. Migrate all handlers. Simplify post-processor.

**Steps:**
1. Read all gate source code directly: `MemberEmissionValidator.cs`, `MethodValidationGates.cs`, `BaseHandler`/`HandleBaseDecl`, all 4 handler Emit() methods, all 4 wrapper emitter ShouldEmitWrapper() methods, `ClosureHandler`, `ExistentialHandler`, `BoundGenericsHandler`
2. Design and implement `IValidationGate`, `MemberValidationPipeline`, `ValidationContext`, `ValidationResult`
3. Implement gate classes — most are mechanical extractions (take existing if-check, wrap in class). Expect ~35-45 gate classes covering all 5 tiers. Group by category (module, type, closure, constraint, dedup, suppression, member-specific, wrapper eligibility)
4. Write parity integration tests: for every member in the test library + at least 2 validation libraries (Nuke, Alamofire), assert pipeline result matches current emission behavior
5. Migrate handlers to use pipeline: PropertyHandler first (most complete validation, ~20 conditions), then MethodHandler (~30), ConstructorHandler, SubscriptHandler
6. Migrate wrapper eligibility gates from 4 wrapper emitter classes into the pipeline (52 conditions)
7. Remove 6 safety-net patterns from `SwiftWrapperPostProcessor`
8. Update CLAUDE.md: remove critical constraints that are now enforced by the pipeline
9. Run `run-tests.sh 2>&1 | tee /tmp/session1-tests.txt` + `validate-libraries.sh 2>&1 | tee /tmp/session1-validation.txt`

**Exit criteria**: All existing tests pass. All 88 validation targets match baseline. No behavioral change in emitted code.

**Risk**: Gate ordering dependencies. Some gates have implicit ordering (e.g., dedup must run after projection, module gate must run before type-specific gates). Parity tests will catch these — fix ordering until parity holds.

**Estimated scope**: ~2,500-3,500 LOC new (gate classes + pipeline + tests), ~1,500 LOC removed from handlers.

---

### Session 2: MethodRepresentation Foundation + MethodHandler (Phase 2a)

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

### Session 3: Remaining Handlers + Async/Wrapper Phases (Phase 2b)

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

### Session 4: Data-Driven Apple Frameworks (Phase 3)

**Goal**: Extract hardcoded AppleFrameworkRegistry data into per-framework JSON files.

**Steps:**
1. Read `AppleFrameworkRegistry.cs` — catalog all hardcoded data (429 value types, 209 remaps, module sets, prefixes)
2. Define JSON schema (`_schema.json`) with all current fields
3. Write extraction script to generate JSON files from current hardcoded data (one per module)
4. Implement `AppleFrameworkRegistry.LoadFromEmbeddedResources()` alongside existing implementation
5. Add parity integration test: JSON-loaded registry produces identical results to hardcoded registry for all 88 validation libraries
6. Once confirmed identical, remove hardcoded data — switch to JSON-only
7. Unify the two ObjC detection paths (NamedTypeSpec vs SwiftTypeName) to both read from the same loaded data
8. Update CLAUDE.md: remove "NSUnderlineStyle excluded from XML intentionally" and similar data-specific constraints
9. Run `run-tests.sh` + `validate-libraries.sh`

**Exit criteria**: All framework data in JSON. Registry API unchanged. All tests pass. All validation targets match baseline.

**Risk**: Lowest risk session. Mechanical data extraction + parity testing. The only subtle part is ensuring the JSON loading preserves exact HashSet ordering for deterministic behavior (use sorted collections).

**Estimated scope**: ~1,000 LOC new (loader + tests), ~800 LOC removed (hardcoded data), ~30 JSON files created.

**Note**: If Session 3 finishes early, this work could potentially merge into Session 3.

---

### Session Summary

| Session | Phase | Primary Deliverable | LOC Delta | Risk |
|---------|-------|--------------------|-----------| -----|
| 1 | Phase 1 | Validation pipeline + handler migration + post-processor simplification | +3K / -1.5K | Low |
| 2 | Phase 2a | MethodRepresentation + phases + renderer + MethodHandler migration | +3.5K / -2.5K | Medium |
| 3 | Phase 2b | PropertyHandler + ConstructorHandler + SubscriptHandler migration | +1.5K / -8K | Medium |
| 4 | Phase 3 | JSON framework definitions + registry loader | +1K / -0.8K | Low |
| **Total** | | | **+9K / -12.8K net reduction** | |

**Autonomous operation**: Each session can run with minimal human input. The doc provides the design, the code provides the source of truth, and the validation scripts provide the gate. The only reason to pause is if validation fails in an unexpected way that requires a design decision.

---

## Success Criteria

| Metric | Current | Target |
|--------|---------|--------|
| Validation gate locations | 150+ across 5 tiers, 10+ files | 1 pipeline, N gate classes (Phase 1) |
| Handler LOC (excl. closures/protocols) | ~33K | ~15K (Phase 2) |
| CLAUDE.md critical constraints | ~30 entries | ~15 entries (Phases 1+2) |
| Files to edit for new validation gate | 3-5 | 1 (Phase 1) |
| Files to edit for new projection type | 6+ switch dispatches | 1 renderer + 1 projection (Phase 2) |
| Post-processor patterns | 8 (6 safety nets) | 3 active patterns (Phase 1 bonus) |
| Framework data format | C# source code | JSON files (Phase 3) |
| 88-library validation | Baseline | Zero regressions after each phase |

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
- Removed patterns (b)-(f) from `SwiftWrapperPostProcessor` → caused regressions:
  - Alamofire: pattern (b) `self.` without `_self:` still fires — closure bridge methods emit `self.adapt()` calls in free functions that lack `_self:` parameter. Emission-time gate doesn't prevent this.
  - ObjectMapper: pattern (f) raw generic `τ_0_0` still leaks into wrapper code. `WrapperValidation.HasRawGenericTypeParams` doesn't catch all cases.
- Root cause: the emission-time gates that are supposed to replace these safety nets have coverage gaps. The safety nets are still catching real broken patterns in production libraries.

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

### Session 2 (Phase 1 completion): Handler Gate Migration + Safety Net Closure

**Goal**: Move handler inline gates into the pipeline, fix emission-time gate gaps, remove safety nets.

**Prerequisite reading**: Session 1 log above (attempted approaches and root causes).

**Steps:**

1. **Move exact handler inline checks into pipeline** (NOT EvaluateHardGates — see Session 1 lesson):
   - From ConstructorHandler.Emit and MethodHandler.Emit, move these pure-skip checks into `ValidateMethodEmission`:
     - Bare generic usage (`BoundGenericsHandler.HasBareGenericUsage` per argument)
     - Non-SwiftObject bound generic (`HasNonSwiftObjectGenericArg`)
     - Unsatisfied constraint (`TryGetFirstUnsatisfiedConstraint`)
     - PInvokeHelperContext + thunk closure (constructor: hasThunkClosure; method: hasThunkClosure || isAsync with MethodClosureBridge/NestedClosureBridge exceptions) — requires `ValidationContext.PInvokeHelperContext`
     - Generic constructor own params — already in pipeline from Session 1 codex fix, verify it's working
   - LEAVE in handlers: existential arg accumulation + bypass/bridge fallback (these emit code, not just validate)
   - Create a fresh `BoundGenericsHandler(typeDatabase)` in the pipeline — same as handlers do, produces identical results
   - The PInvokeHelperContext thunk check needs `ClosureHandler(typeDatabase)` — create fresh in pipeline
   - **Critical**: pass real `ValidationContext` from HandleBaseDecl (not `null`), with `PInvokeHelperContext` from `TypeHandlerContext`
   - Test: for each moved check, write a parity test that constructs the same MethodDecl and asserts the pipeline produces the same result as the handler would

2. **Remove duplicate checks from handlers** once pipeline handles them:
   - ConstructorHandler.Emit: remove bare generic loop (lines 125-138), non-SwiftObject (145-156), unsatisfied constraint (158-168), thunk closure (99-115)
   - MethodHandler.Emit: remove bare generic loop (633-646), non-SwiftObject (653-664), unsatisfied constraint (666-676), thunk closure/async (577-604), HasUnsupportedProtocolConstraints (609-619) — already in pipeline from Session 1
   - Keep existential accumulation (lines 678-700 in MethodHandler, 170-221 in ConstructorHandler) — these feed into bridge dispatch
   - Run `./run-tests.sh 2>&1 | tee /tmp/session2-step2-tests.txt` after each handler cleanup

3. **Fix emission-time gate gaps** (required before safety nets can be removed):
   - Gap 1 (pattern b): Closure bridge methods emit `self.X()` without `_self:` — investigate `ClosureEmitter` / bridge adapters for Alamofire's `RequestAdapter.adapt()`. Reproduce with `./validate-libraries.sh --filter Alamofire --verbose`. The wrapper emits `self.adapt(...)` inside a `@_silgen_name` free function that has no `_self:` parameter.
   - Gap 2 (pattern f): Raw generic params `τ_0_0` leak into ObjectMapper wrappers — investigate with `./validate-libraries.sh --filter ObjectMapper --verbose`. Look at line 289 of the generated wrapper. Find which emitter path fails to check `WrapperValidation.HasRawGenericTypeParams` or `ContainsRawGenericTypeParam`. Likely a property or subscript accessor wrapper path.
   - Each gap fix needs a unit test that reproduces the specific pattern.

4. **Remove safety nets** once gaps are closed:
   - After gap 1: remove patterns (b) `self.` without `_self:`, (c) `__self.init(`, (e) non-escaping closure in Task
   - After gap 2: remove pattern (f) raw generic params `τ_0_0`
   - Remove `ClosureParamPattern` and `RawGenericParamPattern` regexes once no longer referenced
   - Update 21 post-processor tests (change from "stripped" to "preserved" assertions)
   - Run full validation to confirm zero regressions

5. **Update CLAUDE.md** — remove constraints now enforced by the pipeline:
   - "Conditional extension constraint gates: Three parallel paths must stay aligned" → one path
   - "Closure two-layer gate" ordering → in pipeline gate ordering

**Verification:**
```bash
./run-tests.sh 2>&1 | tee /tmp/session2-tests.txt
rm -rf /tmp/binding-validation && ./validate-libraries.sh 2>&1 | tee /tmp/session2-validation.txt
git diff .validation-baseline.json  # should show no regressions
```

**Exit criteria**: All handler inline gates consolidated in pipeline or documented as intentionally remaining (existential bypass/bridge). Safety nets removed. All tests pass. All 88 validation targets match baseline.
