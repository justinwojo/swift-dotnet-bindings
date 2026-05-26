# Phase 8 (AppIntents + CoreSpotlight KeyPath) — remaining work

Single source of truth for what is left in Phase 8 of the KeyPath subsystem. The shipped
detail lives in the archived planning docs under `Done/` (`08-appintents-productionization.md`,
`08-followups-execution-plan.md`, `08b-entityproperty-init-keypath.md`,
`08c-appshortcut-parameter-presentation.md`, `08d-partialkeypath-cssearchableitem.md`). This
doc is the only place that tracks *remaining* Phase 8 work — the archived docs are
shipped-work records and their forward-looking sections are superseded by this file.

## What shipped (context only)

- **8 (v1)** — AppIntents `wrapperImportable` flip + `MockBook` fixture + CA1416 / availability
  categorical-audit fix. (`c742e3e0`)
- **8a-2 / 8a-3** — `UnderscoreProtocolSynthesizer` projects `_IntentValue` /
  `_ParameterSummarySwitchCase` and re-attaches digester-stripped conformances; the
  higher-kinded `ParameterKeyPath : KeyPath<Intent, Parameter>` constraint is no longer
  discarded (was throwing through `SwiftABIParser.HandleNode` and dropping the whole decl).
  (`510d8717` + `c5698e43`)
- **8b (singleton + factory half)** — `AppEntityKeyPathSingletonEmitter` emits per-conformer
  `{Conformer}AppEntityKeyPaths`; `ConformerKeyPathInitFactoryEmitter` emits consumer-side
  factories over a dependency's method-own-generic KeyPath init, validated against the
  `MiniEntityProperty<Value>` stand-in. (`cb07dfe3`)
- **8c (v1)** — higher-kinded constraint capability proven; Phase A/B relaxation lets
  `IntentParameter<TValue>` close over primitive/frozen-value `_IntentValue` conformers.
  (`a1152f1a` + `c5698e43`)
- **8d (partial)** — CoreSpotlight `wrapperImportable` flip + unit-test row. (`d408df92`)

## Remaining items

### R1 — Real AppIntents `EntityProperty` convenience inits (blocked; needs a direction decision)

*Was 8b.3.* The KeyPath singletons and the consumer-side factory shipped against a
BindingTests stand-in (`MiniEntityProperty`). The **real** `AppIntents.EntityProperty<Value>`
convenience-init family does not bind, for two independent reasons:

1. `EntityProperty<Value>` is consumed as a `--framework-dependency` (resolved, not
   C#-emitted), so there is no decl in the consumer module for a post-processor to attach
   constructors to, and C# cannot add ctors across assembly boundaries → a *factory* is
   required regardless (which is exactly what `ConformerKeyPathInitFactoryEmitter` does for
   the stand-in).
2. Even inside the real AppIntents binding the inits are parser-suppressed: 467 `init`
   tombstones on `EntityProperty<TValue>` (signatures reach `@usableFromInline internal` /
   underscored shape types `_IntentValueRepresentable`, `_SystemIntentValue` that are not on
   the synthesizer allowlist) + 9 "C# does not support generic constructors with method-own
   type parameters."

Needs a deliberate decision — not auto-deferral:
- **(a)** Ship a managed Apple-Supplement `EntityProperty<T>` (the `AttributedString`-supplement
  pattern from Session 7) with factory methods.
- **(b)** Resolve the parser-suppression prerequisites first — extend the synthesizer allowlist
  to the `_IntentValue…` shape types and confirm the init signatures then survive to emission.
- **(c)** Defer until a consumer asks.

Background: `Done/08b-entityproperty-init-keypath.md` → "Blocked: 8b.3 `EntityProperty`
convenience-init overloads".

### R2 — CoreSpotlight `CSSearchableItemAttributeSet` typed-singleton container (parked v2)

*Was 8d.* `CoreSpotlight.CSSearchableItemAttributeSetKeyPaths` (one
`PartialKeyPath<CSSearchableItemAttributeSet>` per ~80 public storage properties) cannot ship:
`CSSearchableItemAttributeSet` is ObjC-rooted (`declKind=Import`) and the Swift binding
pipeline emits Swift-defined types only — there is no C# class for the singletons to attach
to. The framework flip + Swift-literal compile check passed (preflight items 1–2); only item 3
(the type binding cleanly) is red. Two unblock paths, neither chosen:

- **ObjC-source projection** — enumerate the ObjC class via clang module metadata and emit a
  minimal C# host plus ObjC-aware trampolines. Large scope.
- **Manual XML supplement** — a hand-curated `CoreSpotlightSupplement.xml` listing each storage
  property, driving the existing manual-DB emission path. Medium scope, maintenance-coupled to
  Apple SDK changes.

Gated on ObjC-rooted projection being in scope (or a consumer asking). Background:
`Done/08d-partialkeypath-cssearchableitem.md` → "Phase 8d.1 preflight result".

### R3 — Cross-module / cross-assembly conformer enumeration (future architectural session)

*Followups Step 7.* `ConcreteSpecializationEngine.GetConformers` sees only conformers in the
bound module or co-bound dependencies (plus `specialization-hints.json`). So Apple-shipped
`AppEntity` / `AppIntent` conformers in sibling frameworks, and app-developer conformers in a
separate assembly consuming a pre-built binding, are **not** visible to closed-overload /
singleton emission — the `\Entity.prop` trampoline literal needs the conformer in the same
Swift TU as the trampoline. Current product story: "your AppEntity / AppIntent types live in
the same library as your generated bindings, which regenerate when you add or remove a type."
Lifting this is its own architectural session (changes to `ConcreteSpecializationEngine`,
TypeDatabase dependency-closure aggregation, and engine construction in `Program.cs`). Open
when a real consumer asks; roadmap-tracked. This limitation is shared by 8b and 8c.

### R4 — Shared singleton-core extraction (optional cleanup)

*Followups Step 6.* Session 12 already consolidated the singleton emitter on the shared
`KeyPathBagWalker`. The full three-driver / one-core extraction (PAT-generic-parent +
8b conformer-enumeration + 8d fixed-Root) only pays off once a *third* driver exists — i.e.
if R2 (8d) unparks. Low priority; land it as a byproduct when 8d ships, not speculatively
(YAGNI). Background: `Done/08-followups-execution-plan.md` → "Step 6".

### R5 — Value-type availability floor not merged (latent)

`AppEntityKeyPathSingletonEmitter` and `ConformerKeyPathInitFactoryEmitter` merge the
conformer + dep-class availability floors but **not** the property's *value*-type floor. A
property whose value type is introduced on a later OS than both the conformer and the dep
class would emit an under-annotated (and silently stripped) Swift trampoline + C# factory. Not
triggered by current fixtures (all value types are `String` / `Int`). Fix is a coordinated
change across both emitters plus a gated-value-type fixture. Surfaced, not patched.

## Not remaining (resolved / declined)

- **8c per-tuple `AppShortcutParameterPresentation` emission** — **declined, not deferred.** The
  Phase C feasibility audit found the whole family has no C#-constructible terminal sink: the
  main struct's sole `init` needs an `@AppShortcutOptionsCollectionSpecificationBuilder` result
  builder with no zero-arg `buildBlock()`; `…Title` needs a compile-time `StaticString` a
  runtime trampoline can't synthesize; the constructible leaves (`…TitleString` / `…SummaryString`
  / nil-table `…Summary`) all feed only blocked sinks. Emitting closed structs would be dead
  code. It becomes reusable only if a `DynamicOptionsProvider` + result-builder binding
  subsystem exists, or Apple adds a C-friendly construction path. Independently confirmed by
  Codex + Grok. Full audit: `Done/08c-appshortcut-parameter-presentation.md` → "Phase C
  feasibility audit".
- **8c higher-kinded constraint capability + primitive-conformer relaxation** — shipped (v1
  deliverable). Durable gate: `EquatableContainer<Int>` (→ `nint`) round-trip in
  `StdlibProtocolConstraintTests`.
