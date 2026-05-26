# Session 08 follow-ups — execution plan

> **Archived — shipped-work record.** Steps 1–5 executed (cohesion fixes, 8c spike, 8d preflight, 8b ship). The remaining items — Step 6 (optional shared-core extraction = R4) and Step 7 (cross-module conformer enumeration = R3) — plus the still-live 8b/8d residuals are consolidated in [`../08-remaining.md`](../08-remaining.md), which supersedes this doc as the source of truth for remaining work.

Working doc for the 8b/8c/8d sequencing. Created 2026-05-21 after a branch-wide audit (Grok), targeted review of the original "refactor first, then 8b/8c/8d as thin drivers" proposal (Grok), and a third independent assessment (Codex) including a C# generic-shape spike. All three reviewers converged on the plan below with refinements; this doc is the source of truth for upcoming work, superseding any ad-hoc ordering implied by the individual 8b/8c/8d session docs.

## Goal

Productionize the three deferred AppIntents / CoreSpotlight KeyPath surfaces (`EntityProperty<Value>` keypath-keyed inits, `AppShortcutParameterPresentation` higher-kinded `ParameterKeyPath`, `PartialKeyPath<CSSearchableItemAttributeSet>` indexingKey) without creating a maintenance hole. The keypath subsystem is now sprawling enough that the next emitter additions need to be cohesive with what's already there, not parallel re-implementations.

## State as of 2026-05-21

Sessions 1–8 shipped on the `keypath-worktree` branch (see `00-overview.md:310` status table). Foundation + typed-singleton emission for closed conformers of PAT-constrained generic parents + Route C per-V keypath-sort + CSM machinery + KVO/AttributedString via per-class emitter + AppIntents framework flip with availability propagation fixes are all in.

Branch-wide audit verdict: **0 High findings, 3 Medium, a handful of Low.** ABI contracts (Unmanaged.passRetained / takeRetainedValue, @guaranteed IN, value equality via shim, single-pointer @_cdecl), memory ownership (direct-wrap / copy-out / pure-value NewFromPayload discrimination, KVO GCHandle finally-free, SafeHandle adoption of +1 retains), and fail-closed gates are clean. The Mediums are cohesion seams that will widen if 8b/8c/8d are written without addressing them; not runtime bugs.

The three pending sessions (`08b-entityproperty-init-keypath.md`, `08c-appshortcut-parameter-presentation.md`, `08d-partialkeypath-cssearchableitem.md`) describe target API shapes and emission strategies. They were each written assuming "build a sibling emitter to Session 4." That is the part this plan revises.

## Source-of-truth ordering

1. Cohesion fixes (surgical) — `src/Swift.Bindings/`
2. 8c C# generic-shape spike (cheap, blocks further 8c design)
3. 8d preflight: CoreSpotlight `wrapperImportable` flip + `CSSearchableItemAttributeSet` binding sanity + Swift literal compile check
4. Conditional: ship 8d if preflight is clean, otherwise park and ship 8b first
5. Ship 8b (the larger emitter problem, regardless of order)
6. Extract shared singleton core as byproduct of 8d + 8b working
7. Document cross-module conformer enumeration as a v1 limitation

Steps 1 and 2 are cheap. Step 3 is a gate. Step 4 picks the order for 8d vs 8b. Step 5 always happens. Step 6 emerges from steps 4–5. Step 7 is a doc update.

---

## Step 1 — Cohesion fixes (surgical)

Land before any 8b/8c/8d work. Each item is small; no speculative abstractions.

### 1.1 Route Session 4 through `KeyPathBagWalker.TryResolveProjectableBagProps`

`KeyPathBagWalker.cs:36` was introduced as the single source of truth for "which bag properties admit as KeyPath leaves" (header comment cites prior Codex/Grok F2 design-review pushback against duplication). `KeyPathBagValueSpecializationEmitter.cs:180` calls `TryResolveProjectableBagProps` correctly. `KeyPathSingletonEmitter.cs:315-344` re-implements the same projection + `IsEmittableProperty` + `new TypeProjectionFactory().Project(...)` loop locally.

Refactor the singleton emitter to obtain its emittable list from `TryResolveProjectableBagProps` (or, if Route C's `BagWalkResult` grouping is wrong-shaped for the singleton path, extract a lower-level `ProjectEmittableBagProperties` helper both can call). The shared-utility claim must be true for the main data path, not just the index/find/is-bag helpers.

### 1.2 Eliminate the `KeyPathFamilyNames` duplicate

`KeyPathSingletonEmitter.cs:63-70` declares a local `HashSet<string>` of the 5 KeyPath family names. `TypeProjectionFactory.cs:552-559` is already the single source for KeyPath family arities, with helpers (`IsKeyPathFamily`, `GetKeyPathArity`) consumed by Route C, `MethodClosureBridge`, `BoundGenericsHandler`. The singleton emitter's local copy is documented as "Mirrors `TypeProjectionFactory.KeyPathFamilyArities`" but doesn't delegate. Delete the local set; route through the factory helpers.

### 1.3 Fix `RouteCSortShapeEligibility` docstring or wire the predicate through CSM

`RouteCSortShapeEligibility.cs:7-40` claims a "three-way contract": Route C emitter + CSM open-generic-suppression + CSM sync/async eligibility predicates. Actual call sites (grep `src/Swift.Bindings/src`): only `MemberValidationPipeline.cs:283` (RoutedElsewhere suppression) and `KeyPathBagValueSpecializationEmitter.cs:107`. No reference in `ConcreteProtocolSpecializationEmitter.Sync.cs`, `.AsyncGenericParent.cs`, or `ConcreteSpecializationEngine.cs`.

Pick one: (a) wire the predicate into `IsCsmSyncEligibleForGenericParent` and its async sibling so the contract is real, or (b) update the docstring to describe the actual two-site + pipeline-centralized suppression reality. The docstring lying about the contract is exactly the seam the "three-way contract" language was meant to prevent.

### 1.4 Doc sync

`06-musiclibraryrequest-re-enablement.md:322` and `00-overview.md:322` mark the FromX() / NewFromPayload discrimination commit (`92461a07`) as "uncommitted, awaiting review." That commit is present on the branch. Remove the markers.

### 1.5 Explicitly deferred from Step 1

Do **not** add `GetKeyPathFlavorForProperty(PropertyDecl, UseSiteContext?)` to `KeyPathBagWalker` yet. Session 4's current `isWritable ? WritableKeyPath : KeyPath` rule at `KeyPathSingletonEmitter.cs:412` is fine until a second use site (8d's force-Partial case) exists. Add it as part of Step 4 when 8d actually needs the decision rule. The flavor decision is a real consistency hazard between 8b and 8d, but speculative addition before 8d ships is YAGNI.

### Exit gate for Step 1

- `nuke test` baseline
- `nuke binding-tests --sim` baseline (skip-regen acceptable)
- All four cohesion fixes landed in one or two small commits

---

## Step 2 — 8c C# generic-shape spike

`08c-appshortcut-parameter-presentation.md:151` handwaves the C# higher-kinded mapping: "C# generics allow type parameters constrained to a concrete generic instantiation (`where T : KeyPath<I, P>`), but … confirm the C# generic shape before designing emission."

Codex ran a minimal spike: the outer `where TParameterKeyPath : KeyPath<TIntent, TParameter>` constraint compiles on .NET 10. The unresolved question is the **inner** constraint: `where TParameter : IntentParameter<TValue>`. If our generated `IntentParameter<T>` is reference-type-shaped (class with SafeHandle, like the runtime KeyPath hierarchy at `SwiftKeyPath.cs:221-318`), the constraint composes. If it's value-shaped or sealed in a way C# generics can't constrain to, 8c's emission design has to change.

Cheap (~30 min) validation:
1. Regen AppIntents bindings and read the C# `IntentParameter<T>` declaration.
2. Write a one-page C# fixture asserting `class P<TIntent, TValue, TParameter, TParameterKeyPath> where TIntent : AppIntent where TParameter : IntentParameter<TValue> where TParameterKeyPath : KeyPath<TIntent, TParameter> {}` compiles against the generated types.
3. If yes → 8c emission design proceeds against this proven shape.
4. If no → rewrite `08c-appshortcut-parameter-presentation.md` before any 8c code lands (same rewrite-mid-flight pattern that hit Session 8 v1).

This is independent of 8d/8b and can run in parallel with Step 3.

### Exit gate for Step 2

- C# fixture compiles (or doc rewritten if not)
- Findings appended to `08c-appshortcut-parameter-presentation.md` under a new "Phase 0 spike result" section

---

## Step 3 — 8d preflight

CoreSpotlight is currently `"autoBridge": true, "optionalFallback": true` in `apple-frameworks.json:230-233` — **not** `wrapperImportable`. 8d's emitter assumes the framework can host `@_cdecl` trampolines that reference `\CSSearchableItemAttributeSet.title` literals. The framework flip is a precursor.

Three preflight items:
1. Flip `CoreSpotlight` to `wrapperImportable: true` in `apple-frameworks.json`. Mirror the AppIntents flip from Session 8 v1 (`08-appintents-productionization.md:36`).
2. Confirm `CSSearchableItemAttributeSet` binds cleanly as a usable C# type via regen on a fixture importing CoreSpotlight. The class has many `@NSManaged` storage properties; the generator handles this category but it hasn't been exercised for this specific type.
3. Compile a Swift snippet `let kp: PartialKeyPath<CSSearchableItemAttributeSet> = \CSSearchableItemAttributeSet.title` inside a `wrapperImportable` test wrapper to confirm the `keypath` SIL instruction emits correctly for a `PartialKeyPath`-typed literal.

### Branching decision (gate output)

- **Preflight clean** → proceed to Step 4 with 8d ordering.
- **Preflight surfaces framework-wide issues** (CA1416 ripple, `@NSManaged` binding gap, `keypath` literal can't type-narrow to `PartialKeyPath`) → park 8d, jump to Step 5 (8b first). AppIntents is already `wrapperImportable` (`apple-frameworks.json:72-74`), so 8b's substrate is the lower-risk path.

### Exit gate for Step 3

- Decision recorded in `08d-partialkeypath-cssearchableitem.md` under "Phase 8d.1 preflight result"
- If clean: commit the framework flip alongside the unit-test row updates in `AppleFrameworkRegistryTests`

---

## Step 4 — Ship 8d (conditional on Step 3)

Skip this step if Step 3 surfaced blockers; proceed directly to Step 5.

### Scope narrowing

`08d-partialkeypath-cssearchableitem.md` exit criteria currently include "the 8b `indexingKey:` C# overloads on `EntityProperty<…>` accept those singletons and the resulting wrapper passes round-trip tests" (line 98). That consumes 8b's overloads, which haven't shipped yet. **Narrow 8d's exit to:** typed `PartialKeyPath<CSSearchableItemAttributeSet>` singletons emit for every public storage property; runtime construction of each singleton succeeds (`CoreSpotlight.CSSearchableItemAttributeSetKeyPaths.Title.Equals(...)` works); BindingTests covers a representative sample (not all ~80) sim + device. The full `EntityProperty(indexingKey:)` round-trip belongs in Step 5.

### Implementation shape

Extend `KeyPathSingletonEmitter` with a fixed-Root driver, OR add a controlled sibling. Either is acceptable for 8d alone — the shared core extraction is Step 6. Use the per-type handler hook points (`ClassHandler.cs:426`, `FrozenStructHandler.cs:444`, `NonFrozenStructHandler.cs:362` — same place Session 4's PAT-generic-parent driver runs today).

Flavor: explicit `PartialKeyPath<CSSearchableItemAttributeSet>` for every singleton even though many backing properties are reference-writable. This is the first force-Partial use site. If `KeyPathBagWalker` doesn't yet have a flavor decider (deferred in Step 1.5), add it now — this is the second use site that justifies it.

Symbol scheme: `SBW_KP_CoreSpotlight_CSSearchableItemAttributeSet_{PropertySan}_{hash8}` per `08d-partialkeypath-cssearchableitem.md:41`.

### Exit gate for Step 4

- `CoreSpotlight.CSSearchableItemAttributeSetKeyPaths.X` resolves for every public storage property
- BindingTests fixture in `BindingTests/RuntimeTestsApp/CoreSpotlight/` passes sim + device
- Wrapper-lib size delta measured and recorded in the session doc (expected ~4KB / platform × 6 platforms = ~24KB total, per the 8d doc estimate)

---

## Step 5 — Ship 8b

8b is **not** a thin driver on top of refactored singleton emission. It has three independent emitter problems, all comparable in weight to Route C:

1. **Conformer enumeration driver** for `AppEntity`. Calls `ConcreteSpecializationEngine.GetConformers("AppIntents.AppEntity")` (`ConcreteSpecializationEngine.cs:357`). Currently the engine combines current-module ABI conformers with hints; this is enough for BindingTests + bound-module consumers but not Apple-shipped conformers in sibling frameworks (see Step 7).
2. **Singleton container emission** per closed `AppEntity` conformer — `{ConformerSan}AppEntityKeyPaths` (`08b-entityproperty-init-keypath.md:47`). This part *can* share with the 8d driver post-extraction; pre-extraction, accept controlled duplication.
3. **`EntityPropertyInitOverloadEmitter` as `IMethodPostProcessor`**. This is the bulk of the new emitter work. One closed C# convenience-init overload per `(Entity, Value, init-shape, KeyPath flavor)` tuple, **not** per-property. Same-Value-type properties on one conformer collapse to one overload — the caller selects the property by passing the right singleton.

### `IMethodPostProcessor` scope discipline (8b-specific)

`IMethodPostProcessor.cs:23` and `MethodHandler.cs:730` show that the postprocessor pipeline includes constructors only when `Scope == All`. 8b's emitter targets `EntityProperty<Value>`'s constrained-extension convenience inits. Set scope explicitly. Verify the ordering against existing post-processors (NativeIntOverloadEmitter, ThrowingClosureSimplificationEmitter, MarkerProtocolOverloadEmitter, the default-parameter overload emitter) so 8b's emit isn't swallowed by an earlier WasEmitted-claim.

### `Value.ValueType == X` mapping

`08b-entityproperty-init-keypath.md:34-35` calls this the load-bearing part. The `where Value.ValueType == Swift.Int` extension block on `EntityProperty<Value>` discriminates which init signature the C# side has to mirror. The mapping is **C# Value type passed by the consumer → which Swift extension provides the init.** Before designing emission, confirm via regen: does our generated `EntityProperty<T>` carry the `ValueType` associated type, and can the post-processor enumerate the constrained extensions per `(EntityProperty<T>, T.ValueType)` pair?

### DuplicateSignature gate

`08b-entityproperty-init-keypath.md:73-74` correctly calls out that same-Value-type props on a conformer must collapse to one overload. Validate against the existing DuplicateSignature pipeline (Constraint #16 in `.claude/rules/constraints.md`) — the disambiguator is `(Conformer, ClosedValue)`, not the property.

### Exit gate for Step 5

- Every `(closed AppEntity conformer × Value.ValueType extension × init-shape × KeyPath flavor)` tuple emits one C# overload
- `MockBook` round-trips via `EntityProperty<nint>(identifier:getter:)` and `EntityProperty<string>(identifier:title:getSetter:)` from C#, sim + device
- If 8d shipped first: `MockBook` round-trips via `EntityProperty<string>(identifier:indexingKey:)` consuming the 8d singletons
- No DuplicateSignature failures
- Doc updated in `08b-entityproperty-init-keypath.md` with cross-module visibility findings (see Step 7)

---

## Step 6 — Extract shared singleton core (byproduct, not prerequisite)

Once 8d (or 8b, in the parked-8d case) ships, you have two non-PAT driver shapes alongside Session 4. The shared core extraction emerges naturally.

### Target shape — two-stage planner/emitter, not policy swarm

The initial proposal was a `KeyPathSingletonRequest` record carrying ~6 policy objects (FlavorDecider, NamingPolicy, DedupKeyFactory, PropertyFilter, AvailabilityAugmentations, …). Codex correctly flagged this as over-decomposed. The cleaner shape:

**Driver builds request rows.** Each row carries plain data:
- Root decl + root C# type spelling
- Root Swift spelling (for trampoline `\Root.prop` literal)
- Container name (e.g. `MockBookAppEntityKeyPaths`)
- Per-property: name, projection, family/flavor, dedup key, symbol prefix
- Availability extras (for namespace-scope attribute emission — see `KeyPathSingletonEmitter.cs:417` which already handles parent-suppression explicitly)

**Core emitter consumes a list of request rows.** Owns:
- Projection finalization
- Lazy field emission (C#)
- P/Invoke declaration emission (C#)
- Swift trampoline emission (`@_cdecl` per row)
- `ModuleEmissionContext.TryAddKeyPathSingletonContainer` dedup

Three drivers feed one core:
- PAT generic-parent driver (the current `EmitKeyPathSingletonsForGenericParent`) — builds rows from `CollectBagDemand` + conformer enumeration per generic param.
- 8d fixed-Root driver — builds one row group for `CSSearchableItemAttributeSet` (force-Partial flavor).
- 8b conformer-enumeration driver — builds one row group per closed `AppEntity` conformer (filtered to properties whose Value matches an active `Value.ValueType ==` extension).

### What this doesn't fix

KVO singleton emission (`KvoExtensionEmitter.cs:113`) currently reuses `ModuleEmissionContext.TryAddKeyPathSingletonContainer("KVO|...")` for dedup but is structurally a different emitter (per-class observer methods, not per-property singletons). It does not fold into this core.

### Exit gate for Step 6

- All three drivers produce request rows; core emits without driver-specific branches
- Existing MusicKit Route C + Session 4 paths produce byte-identical output (or only formatting deltas)
- BindingTests baseline holds (sim + device)
- The "shared utility" claim in `KeyPathBagWalker.cs:31-34` is now true for the main data path

---

## Step 7 — Cross-module conformer enumeration: documented v1 limitation

`ConcreteSpecializationEngine.GetConformers` (`ConcreteSpecializationEngine.cs:357`) unions `_abiConformers` (populated by `IndexModuleConformances`, lines 176-278 — **intra-module only**) with `_hintConformers` (from `specialization-hints.json` with `AllowedModules` scoping). Cross-module conformer enumeration only works via hints today.

For 8b/8c v1 this means:
- **User-declared `AppEntity` / `AppIntent` conformers in the bound module → visible.** Sufficient for BindingTests (`MockBook`, `MockBookLookupIntent`) and for "consumer ships their own AppEntity, binds it as part of their library."
- **Apple-shipped conformers in sibling frameworks (e.g. `AppIntentsFinanceKit`) → not visible** unless added via hints.
- **"Bind AppIntents once, let app developers add their own conformers in their own assembly later" → not supported** by this generator architecture for keypath-keyed inits. The trampoline literal `\Entity.prop` requires the conformer type to be in the same Swift TU as the trampoline; cross-assembly conformers can't drive trampoline emission at consumer compile time.

Action items for Step 7:
1. Add a "Limitations" section to `08b-entityproperty-init-keypath.md` documenting the bound-module-conformers-only constraint.
2. Same for `08c-appshortcut-parameter-presentation.md`.
3. Open a tracking note in `src/docs/roadmap.md` for "cross-module conformer enumeration as a dedicated session when a real consumer asks." Do not preempt the architecture; the current product story is "your AppEntity types live in the same library as your generated AppIntents bindings."

### Out of scope here

Solving cross-module conformer enumeration is its own architectural session (changes how `ConcreteSpecializationEngine` is built, how TypeDatabase aggregates dependency closures, how `Program.cs` / `ModuleHandler` constructs engines). Do not block 8b/8c on it.

---

## Cross-cutting risks (already accepted)

These are real but not blocking. Resurfacing here so a fresh session sees them.

- **Wrapper-lib symbol-count growth scales with consumer code, not Apple SDK.** 8d alone adds ~80 trampolines for `CSSearchableItemAttributeSet`. 8b/8c grow combinatorially with consumer-declared types. Measure post-emission; not load-bearing today.
- **Cross-module C# type visibility for emitted KeyPath signatures.** When AppIntents emits `KeyPath<OtherModule.Conformer, V>`, the conformer-declaring module must be loaded into the same TypeDatabase for the AppIntents emit pass. `CurrentModuleName` in `ProjectionContext` is the emitting module. Works for `MockBook` (same lib) but untested cross-framework. Investigate at Step 5 start before locking 8b's overload emission.
- **8c trampoline reconstruction via `as?` chains.** The Swift trampoline for a closed `init<Intent, ..., ParameterKeyPath : KeyPath<Intent, Parameter>>(for keyPath: ParameterKeyPath, …)` must accept an opaque `IntPtr` and reconstruct the exact `KeyPath<ClosedI, ClosedP>` before invoking the original generic init. Route C does analogous chains (`KeyPathBagValueSpecializationEmitter.cs:516-529`); 8c will need similar.
- **`asyncGetter:` + method-own-generic Entity composability.** `EntityProperty.init<Entity>(identifier:, asyncGetter: @escaping @Sendable (Entity) async throws -> Value)` combines async + closure + method-own generic. Verify against existing closure machinery (ClosureEmitter, ThrowingClosureSimplificationEmitter) before locking 8b's emission.

## Open decisions for the next session

- After Step 3 preflight: 8d-first or 8b-first?
- After Step 2 spike: does 8c's design hold or does the doc need rewriting?
- Step 6 extraction: byte-identical regen target, or accept formatting deltas?

## Reviewer trail (for audit)

- Grok branch-wide audit findings: `/private/tmp/grok-cli-review-keypath-worktree-20260521-150506-branchwide-r1.md`. Session: `019e4c25-ef23-7dc3-9384-8c33ddd89035`.
- Grok targeted review of original refactor proposal: `/private/tmp/grok-cli-review-keypath-worktree-20260521-150507-recommendations-r1.md`. Session: `019e4c25-fbb5-79f3-b058-a36e3e4a883e`.
- Codex independent assessment + 8c spike: `/private/tmp/codex-review-keypath-worktree-20260521-152015-r1.md`. Session: `019e4c32-b608-71b0-8c39-754818ec6562`.

Resumable; each session has the full prior context if a follow-up consultation is useful mid-execution.
