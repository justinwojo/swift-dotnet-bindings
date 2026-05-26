# Session 8b — `EntityProperty.init<Entity>(…)` KeyPath-keyed convenience-init family

> **Archived — shipped-work record.** The singleton + factory half shipped (`cb07dfe3`). Remaining/forward-looking Phase 8 work (here: the blocked real-AppIntents `EntityProperty` inits = R1) is consolidated in [`../08-remaining.md`](../08-remaining.md), the single source of truth for what's left. Sections below describing unbuilt phases are retained for design background only.

Bind AppIntents' `EntityProperty<Value>` KeyPath-taking convenience inits against closed `AppEntity` conformers. This is the bulk of the AppIntents KeyPath surface — the 240-WritableKeyPath number from `00-overview.md`'s consumer table mostly lives here.

Depends on: Session 3 (KeyPath foundation), Session 4 (typed singleton emission). Builds on Session 8 v1 (AppIntents wrapperImportable + MockBook fixture).

## Real API shape (verified against iOS 26.2 `AppIntents.swiftinterface`)

```swift
@propertyWrapper final public class EntityProperty<Value> : AnyIntentValue, @unchecked Sendable
  where Value : _IntentValue, Value : Sendable {
  // No public designated init; convenience inits live in constrained extensions.
}

@available(macOS 26.0, iOS 26.0, watchOS 26.0, tvOS 26.0, visionOS 26.0, *)
extension EntityProperty where Value.ValueType == Swift.Int {
  convenience public init<Entity>(identifier: _const String, getter:    KeyPath<Entity, Value>)         where Entity : AppEntity
  convenience public init<Entity>(identifier: _const String, getSetter: WritableKeyPath<Entity, Value>) where Entity : AppEntity
  convenience public init<Entity>(identifier: _const String, asyncGetter: @escaping @Sendable (Entity) async throws -> Value) where Entity : AppEntity

  // … `indexingKey:` / `customIndexingKey:` / `title:` variants for ~16 init shapes total
  convenience public init<Entity>(identifier: _const String, title: LocalizedStringResource,
                                  getter: KeyPath<Entity, Value>)         where Entity : AppEntity
  convenience public init<Entity>(identifier: _const String, title: LocalizedStringResource,
                                  getSetter: WritableKeyPath<Entity, Value>) where Entity : AppEntity
  // …
}
// Same extension block exists for Value.ValueType ∈ {Int, AttributedString, Date, DateComponents,
// IntentFile, String, Measurement<…>, IntentEntity (recursive), ~15 more value types}.
```

Two structural facts that drive the emitter design:

1. **`Entity` is a method-own free generic** with `where Entity : AppEntity`. It is NOT a generic param of `EntityProperty`. Session 4's existing `KeyPathSingletonEmitter` walks "Root = parent's associated type" — that's a different shape.
2. **`Value.ValueType` discriminates which extension block holds the init.** A C# overload that takes `KeyPath<MockBook, nint>` (the C# projection of Swift `KeyPath<MockBook, Int>`) must call into the `extension EntityProperty where Value.ValueType == Swift.Int` block, not the `Foundation.AttributedString` block. The mapping from "C# value type passed by the consumer" to "which Swift extension provides the init" is part of the dispatch.

## Phase 0 spike result (2026-05-21) — cross-referenced from 8c

**Verdict: blocked at the same layer as 8c. `EntityProperty<Value>` is not currently emitted to C#.**

The apple-framework regen of AppIntents records:
> `// Unsupported: type 'EntityProperty' — IndeterminatePwtShape (TValue: AppIntents._IntentValue (protocol not projected in the type database))`

`AppIntents._IntentValue` is an underscored SPI protocol that `swift-api-digester` strips from ABI JSON (the dropped emission is upstream of any parser-side suppression). Until it is projected as a PAT-shaped protocol the type database can store, `EntityProperty<Value>` does not appear in `AppIntents.cs`, and there is nothing for an `IMethodPostProcessor` to attach convenience-init overloads to.

The shared prerequisite has shipped as `UnderscoreProtocolSynthesizer` (`src/Swift.Bindings/src/Parser/UnderscoreProtocolSynthesizer.cs`) — see the "Prerequisite shipped" section in `08c-appshortcut-parameter-presentation.md` for the full mechanism (swiftinterface-side synthesis, allowlisted module/name pairs, descriptor-symbol derivation through `ModuleProcessor.ConvertProtocolTypeToDescriptorSymbol`). 8b can proceed against the now-projected `EntityProperty<Value>` via the existing CSM machinery.

### Predicted downstream emitter surface (not yet observed — historical)

Once the synthesizer fires against real AppIntents, the previously-tombstoned types reach emitter for the first time and may surface bugs in code paths that have never been exercised. Categories that are plausible from SDK reading (each would be its own session with a BindingTests fixture):

- Missing `@available` on constructor-routing private protocols (`_SBW_CI_*` referencing iOS 16+ types like `AppDependencyManager`).
- Missing `@available` on foreign-type extension method wrappers (`SBSW_CoreLocation_CLPlacemark_get_displayRepresentation` referencing iOS 16 `DisplayRepresentation`).
- Result-builder return-type modeling collapsing `[AppShortcut]` to `AppShortcut` in `AppShortcutsBuilder.buildExpression`.
- Value-generic integer constant-literal init filtering — `IntentCollectionSize.init(min:max:)` requires compile-time constants the runtime wrapper cannot supply.
- Async-throws `@_silgen_name` dispatch wrappers emitted without `async throws` on the wrapper signature (e.g., `EmptySnippetIntent.perform()`).

These are predictions, not observations. The honest gate is "pack this SDK, consume it from `swift-dotnet-packages` against AppIntents, run regen, see what actually surfaces." See also `roadmap.md` → "AppIntents downstream emitter bugs (gate to enabling AppIntents in `validation-libraries.json`)".

### Observed downstream emitter surface (2026-05-23 — synthesizer-projected regen)

Regen against worktree HEAD `8ead3167` + `nuke pack --version 0.12.0 --skip-apple` → AppIntents downstream rebuild against the synthesizer-shipped 0.12.0 SDK. macOS + Mac Catalyst targets compiled the managed assembly; iOS + tvOS targets hit the known async/throws wrapper-Swift compile failures (doc 14 § "Wrapper-compile failures" items 4 + 5, declared out-of-scope here). The C# generator ran to completion for all four target frameworks. Observed tombstone categories in `AppIntents.cs` (macos26.2 slice):

| Category | Count | Predicted? | Notes |
|---|---|---|---|
| `init` — member signature reaches an @usableFromInline internal type, clustered on `EntityProperty<TValue>` | 467 | No | **This is the 8b convenience-init family.** The synthesizer projected `_IntentValue`, but the convenience-init signatures reach into additional `@usableFromInline internal` / underscored types (`_IntentValueRepresentable` swiftinterface line 9762, `_SystemIntentValue` line 2584, and related shape protocols not on the synthesizer allowlist). The `IsNodeModuleInternal` three-layer detection (`UsableFromInline` → always internal, per `parser-marshaler.md`) suppresses the init at the parser level before MemberValidationPipeline gets to see the KeyPath-typed parameters. Pre-8b blocker. |
| `init` — same shape, clustered on `IntentParameter<TValue>.DateKind` (nested type) | 226 | No | DateKind is a nested type on `IntentParameter` with the same suppression shape; same root cause as the EntityProperty 467. Closes once the above is fixed. |
| `init` — `C# does not support generic constructors with method-own type parameters` | 9 | Implicit | The exact `init<Entity>(getter: KeyPath<Entity, Value>) where Entity : AppEntity` shape that 8b's emitter plan addresses by emitting one closed C# overload per `(Entity, init-shape)` tuple. *Expected*; 8b's pre-image. |
| `resolve` (and friends) — generic-constraint specialization failures, `Type argument 'X' does not satisfy constraint 'AppIntents._IntentValue' on 'IntentParameterContext'` for X ∈ {`Swift.Int` (3), `Swift.Double` (3), `Swift.String` (1), `Swift.Bool` (1), `Foundation.AttributedString` (1), `AppIntents.StringSearchCriteria` (1), `AppIntents.IntentWidgetFamily` (1) — total 11 sites, plus 36 others on Method-PAT shape totaling 47} | 47 | No | **CSM closed-conformer enumeration doesn't see Swift.Int / Swift.Double / etc. as `_IntentValue` conformers.** The swiftinterface declares `extension Swift.Int : AppIntents._IntentValue {}` (line 2197), `extension Foundation.AttributedString : AppIntents._IntentValue {}` (line 2294), `extension AppIntents.EntityIdentifier : AppIntents._IntentValue {}` (line 899), etc. The synthesizer projects the protocol decl but does NOT ingest those conformance records. The Conformance Graph (or whichever table the Specialization engine queries) is missing these wirings. Pre-8b/8c blocker. |
| Property/method signature references unsupported module (SwiftUI/Combine) | 22 + 19 = 41 | No (but Session 9 scope) | Pre-existing 09-SwiftUI scope; surfaces post-synthesizer because previously-suppressed types now project and their SwiftUI-touching members get evaluated. Not blocking 8b/8c. |
| Synthesized Codable encode/init pruned by design (Encoder/Decoder existential) | 16 | No | Pre-existing pruning; surfaces post-synthesizer for the same reason. Not blocking. |
| `Method has constraints on protocols with associated types or self requirements` | 37 | No | Pre-existing PAT-shape gating, surfaces post-synthesizer; superset of 8b's known constraint shape. Some may collapse into the 9 method-own-generic-ctor count once 8b's per-conformer post-processor lands. |
| Closure parameter type cannot be marshalled (`arg0`/`arg2`/`arg3`/`dependency`) | 13 + 4 + 3 + 2 = 22 | No (8c-adjacent) | The `@AppShortcutOptionsCollectionSpecificationBuilder<…> optionsCollections: () -> some AppShortcutOptionsCollectionSpecification<…>` result-builder closure parameter on ASPP-family inits — opaque-return-type closures. Tracked under 8c risks; not blocking the 8b-only path. |
| `unsupported placeholder type in constructor` / `unsupported placeholder type` | 5 + 2 = 7 | No | Opaque-return-type (`some Foo`) constructor return positions. Adjacent to closure-shape work. |
| `type could not be resolved to a concrete projection` (`AnyType`, `Swift.SwiftOptional<Swift.AnyType>`, `Swift.SwiftArray<Swift.AnyType>`) | 10 | No | Untyped property/parameter slots — pre-existing AnyType pipeline limitation. Not specific to AppIntents. |
| `variadic generic parameter pack 'each R'` | 1 | No | A new variadic pack site that landed after doc 14's variadic-pack closure. Single site; investigate separately if needed. |
| `Type resolution failed for property type 'Swift.FloatingPointRoundingRule'` | 1 | No | Foundation rounding-rule enum not projected; not specific to AppIntents. |

#### Comparison to predicted (5 categories from the historical section above)

| Prediction | Observed? | Notes |
|---|---|---|
| #1 — `_SBW_CI_*` missing `@available` | No | Closed by doc 14 § "Wrapper-compile failures surfaced by the regen" item 1; no C# regen tombstones in this category. |
| #2 — Foreign-type-extension wrappers missing `@available` | No | Closed by doc 14 item 1 (same `ForeignTypeExtensionEmitter` availability propagation). |
| #3 — `AppShortcutsBuilder.buildExpression` return-type collapse | No | Closed by doc 14 item 2. |
| #4 — `IntentCollectionSize.init(min:max:)` value-generic constant literals | Not observed | Not in the tombstone surface. Either the constraint never reached emission (filtered upstream) or 8b/8c-blocker volume is hiding it. Re-check once the EntityProperty init cascade is unblocked. |
| #5 — async-throws `@_silgen_name` wrappers without async/throws | **Yes** | 8 wrapper-Swift compile errors on iOS/tvOS targets at `AppIntents.Wrapper.swift:5727` (tvOS) / `:5747` (iOS). Doc 14 explicitly held these out-of-scope (items 4 + 5 in its wrapper-compile table). Tracking continues here, not a new finding. |

#### Newly surfaced (not predicted) and blocking categories

1. **710× `init reaches @usableFromInline` on `EntityProperty<TValue>` (467) + `IntentParameter<TValue>.DateKind` (226)** — the 8b convenience-init family is being suppressed at the parser level *before* the convenience-init post-processor in 08b's plan can attach overloads. Pre-8b session needed: extend the synthesizer (or sibling synthesizer) to admit `_IntentValueRepresentable` / `_SystemIntentValue` / other underscored-but-load-bearing siblings, OR relax the `@usableFromInline → IsModuleInternal` gate where the only `@usableFromInline` reference is to an already-projected synthesizer-fronted protocol. The honest scoping question is which: needs trace through `IsNodeModuleInternal` to identify the exact reached type(s).
2. **47× CSM `_IntentValue` conformance failures** — the synthesizer projects `_IntentValue` as a TypeRecord but does NOT ingest the `extension X : _IntentValue {}` conformance records. The Conformance Graph / Specialization engine doesn't see Swift.Int, Swift.Double, Swift.String, Swift.Bool, Foundation.AttributedString, AppIntents.StringSearchCriteria, AppIntents.IntentWidgetFamily as conformers. Pre-8b/8c session: extend `UnderscoreProtocolSynthesizer` (or add a second pass) to also ingest conformance-extension declarations from the swiftinterface and inject them into the conformance graph for the synthesized protocol. This is the same structural pattern as the protocol-decl synthesis but for conformances.
3. **AppShortcutParameterPresentation silently dropped (5 sibling structs in swiftinterface, 0 in regen, 0 tombstones)** — the four-generic-param-pack with higher-kinded `ParameterKeyPath : Swift.KeyPath<Intent, Parameter>` is being filtered upstream of any tombstone-emitting gate. 8c-specific blocker, but it stays blocked even after the 8b prerequisites land. See `08c-…md` "Higher-priority 8c blocker" subsection for the detail.

These three blockers are pre-implementation work — they prevent 8b's `IMethodPostProcessor` from finding the convenience inits to attach to (#1), prevent CSM closed-conformer enumeration from emitting the typed singletons (#2), and prevent 8c from having a type to bind to (#3).

#### Resolution — 8a-2 + 8a-3 (shipped, uncommitted in `keypath-worktree`)

- **#1 (710× internal-reach cascade) — closed by 8a-2 Gap B.** Root cause was narrower than the two options sketched above: the synthesized public-underscore protocol names (`_IntentValue`, `_ParameterSummarySwitchCase`) were entering the Pattern-2 internal-type-reach set and suppressing every member whose signature/constraint named them. They are `public` in Swift — only swift-api-digester strips them — so `UnderscoreProtocolSynthesizer.MergeSuppressedIntoInternalTypeNames` now folds the underscore-suppression set into `InternalTypeNames` **excluding** the synthesized names. A genuinely module-internal underscore type still flows through and suppresses. Regen: `Pattern2InternalTypeReach` 784 → 0.
- **#2 (47× CSM `_IntentValue` conformance failures) — partially closed by 8a-2 Gap A, by design.** `IngestStrippedConformances` re-attaches the digester-stripped `extension X : _IntentValue {}` records, but only for **local reference-typed** conformers (non-frozen struct / class / enum); the conformance is attached with an empty descriptor (type-database fact only, never emitted as runtime code). Foreign / stdlib conformers (`Swift.Int`, `Swift.Double`, `Swift.String`, `Swift.Bool`) intentionally stay unsatisfied — they have no local `TypeDecl`, and their C# projections are primitives that cannot implement `ISwiftObject` (the synthesizer's fallback bound on `IntentParameter<TValue>`; see the primitive-conformer caveat in `08c-…md`). Regen: `_IntentValue` suppressions 17 → 12. The residual 12 are the by-design primitive-conformer exclusions plus the Session 9 SwiftUI/Combine surface.
- **#3 (ASPP silent drop) — closed by 8a-3 Gap C.** `GenericSignatureParser.ParseConstraint` now returns null for a constructed-generic constraint target (`ParameterKeyPath : KeyPath<Intent, Parameter>`) instead of feeding it to `SwiftTypeName.FromModuleQualifiedName` (which threw on `<`) and propagating the throw up through `SwiftABIParser.HandleNode`, which had been swallowing it and discarding the entire enclosing decl. All five `AppShortcutParameterPresentation*` structs now emit. See `08c-…md` "Higher-priority 8c blocker" for the trace.

#### Empirical re-confirmation (2026-05-24 — fresh CLI regen at HEAD `c5698e43`)

A direct generator regen against the iOS 26.2 simulator SDK's public `AppIntents.swiftinterface`
(`swift-api-digester` dump → generator CLI, no downstream pack) confirms the internal-type-reach
cascade is **fully closed at HEAD**, not merely the `_IntentValue` portion:

- **`Pattern2InternalTypeReach` / `@usableFromInline`-reach suppressions: 0** in `binding-report.json`.
  The 467 `EntityProperty<Value>` + 226 `IntentParameter.DateKind` internal-reach tombstones from the
  2026-05-23 table do not reproduce. The input still declares `_IntentValueRepresentable` (swiftinterface
  line 9762) and `_SystemIntentValue` (line 2584) and the `extension EntityProperty where Value.ValueType :
  _SystemIntentValue` blocks (line 2162), so the signatures that *would* trip the gate are present — they
  are simply no longer suppressed for internal-reach.
- **Residual `EntityProperty` init skips (1035 total) are the expected 8b.3 surface**, not parser
  suppression: 479 `SwiftUIConstraint` (signature names `Foundation.LocalizedStringResource` — Session 9
  scope), **160 `UnsupportedSignature` = "C# does not support generic constructors with method-own type
  parameters"** (the `init<Entity>(getter: KeyPath<Entity, Value>) where Entity : AppEntity` family — this
  is exactly what 8b.3's per-`(Entity, init-shape, flavor)` overload emitter is designed to close; the
  pre-resolution table predicted only 9 because the rest were hidden behind the internal-reach gate), 159
  `UnsatisfiedGenericConstraint` (primitive `ISwiftObject` conformers — by-design per #2 above), 156
  `DuplicateSignature`, 80 `UnsupportedClosure` (`asyncGetter`).

**Consequence:** the "parser-suppression prerequisite" is already satisfied at HEAD. The remaining work to
land KeyPath-init construction is the 8b.3 overload emitter itself (against the 160 method-own-generic-ctor
surface) — subject to the still-open cross-assembly constraint (blocker 1 below) when `EntityProperty` is a
resolved `--framework-dependency` rather than the emit target.

#### Observed downstream emitter surface (2026-05-25 — fresh CLI regen at HEAD `a1152f1a`, post-8a Phase A+B)

First regen after `a1152f1a` ("allow C# primitives/frozen values as generic args to PAT/Self-constrained
Swift types" + stripped-conformer ingestion). The 8a Phase A+B relaxation means the **reference-typed**
`_IntentValue` conformers (`IntentFile`, `EntityIdentifier`, `IntentCurrencyAmount`, `IntentPaymentMethod`,
`IntentPerson`) now reach the CSM/`_SBW_CI_` init paths *for the first time*. Direct generator CLI →
`swiftc` on the emitted wrapper. **C# generation succeeded; the C# tombstone surface is clean of new bugs
(all by-design — SwiftUI `LocalizedStringResource` = Session 9, `asyncGetter` closures, method-own-generic
ctors = the 8b.3 surface above, primitive-`ISwiftObject` exclusions, collapsed duplicates).** The new bugs
are all in the **emitted Swift wrapper**, which fails `swiftc` with **30 errors (complete set — no error
cap in `SwiftWrapperCompiler`)**:

| Count | `swiftc` error | Emission mechanism | Root cause |
|---|---|---|---|
| 8 | `expect a compile-time constant literal` | CSM (`ConcreteSpecializationEngine` → `ConcreteProtocolSpecializationEmitter`) | CSM emits a concrete `SBW_CSM_*_init_*` `@_cdecl` wrapper that passes a *runtime* `String`/enum for a `_const` parameter (`identifier: _const String`, `inputConnectionBehavior: _const InputConnectionBehavior`, `mode: _const`). The **normal** ctor path filters these (`ConstructorWrapperEmitter` `IsConstLiteral` gate); the CSM path applies no such gate. |
| 4 | `'init()' is unavailable` | CSM | CSM emits an init marked `@available(*, unavailable)` on the conformer. Same gap: the normal path's unavailable filter is not mirrored in CSM admissibility. |
| 14 + 2 | `no exact matches in call to initializer` / `…requires 'IntentPerson.UnwrappedType' (aka 'IntentPerson') conform to 'Collection'` | CSM | CSM emits an init that lives in a **constrained extension** (`extension … where UnwrappedType: Collection`, `where Value.ValueType == X`) for a closed specialization that does **not** satisfy the extension's `where` clause. The extension constraints are never checked against the closed conformer. |
| 2 | `type 'EntityProperty<Value>' does not conform to protocol '_SBW_CI_E415B868'` | `_SBW_CI_*` (`GenericProtocolEmitter` → `ConstructorWrapperEmitter`) | `GenericProtocolEmitter` emits `extension EntityProperty: _SBW_CI_{hash} {}` **unconditionally** for an init that exists only inside a constrained extension — so the unconstrained type does not actually satisfy the routing protocol's init requirement. |

**Comparison to the 5 predictions (updates the historical table above):**

- **#4 (`IntentCollectionSize.init(min:max:)` value-generic constant literals) — MATERIALIZED, as the general CSM `_const` facet.** `IntentCollectionSize.init` itself is collapsed by `DuplicateSignature` (`ctor(nint)`), unrelated. But the *mechanism* it predicted — runtime wrappers feeding `_const` parameters — is exactly the 8× "compile-time constant literal" CSM failures above. The prediction was right about the mechanism, wrong about the specific decl.
- **#5 (async-throws `@_silgen_name` wrappers without `async throws`) — now CLOSED.** The 2026-05-23 run still saw these (doc 14 items 4+5); at `a1152f1a` the async `try await perform()` wrappers emit and compile. Zero occurrences in this regen's 30-error set.
- **#1/#2 (`_SBW_CI_*` / foreign-extension missing `@available`) — still not the failure mode.** `_SBW_CI_*` *did* surface (2 errors) but as a **constrained-extension conformance** bug, not a missing-`@available` bug. #2 stays closed.
- **#3 (`AppShortcutsBuilder.buildExpression` collapse) — still closed.**

**Shared deep root (Codex + Grok concur): three init-erasure paths — normal `@_cdecl` ctor wrapper, GSF,
and `_SBW_CI_` — each re-implement *part* of the ctor-admissibility contract, and CSM + `_SBW_CI_` skip the
parts that matter here.** The facets:
- Facet A (`_const`, 8×) and facet C (unavailable, 4×) are "the gate exists but CSM never calls it" — the
  normal path filters `_const` at `ConstructorWrapperEmitter.ShouldEmitWrapper` (`CSSignature.Skip(1).Any(a
  => a.IsConstLiteral)`, ~:113) and unavailable at parse time (`SwiftABIParser` → `IsModuleInternal`); CSM's
  `CanEmitConcreteOverloadForPairing` applies neither. (Grok also flagged pre-existing drift:
  `MemberValidationPipeline.GetConstructorWrapperRejectionReason` already *omits* the `_const` gate that
  `ShouldEmitWrapper` has.)
- Facet B (constrained-extension `where` unsatisfied, 14+2×) and the `_SBW_CI_` conformance bug (2×) need a
  *satisfaction* check. Grok's key finding: the extension `where` clauses are **already parsed** into the
  per-method `GenericConformances`/`RawGenericSig` (`GenericSignatureParser` via `CreateMethodDecl`; see the
  `BoundGenericsHandler` conditional-extension fallbacks and `ConstrainedExtensionEmitter.ExtractSameType…`).
  What is missing is a distinguished "from a constrained extension" marker plus *evaluation against the
  chosen conformer (or the open type, for `_SBW_CI_`)* at the decision point — not a new constraint model.

**Recommended cut — Codex/Grok synthesis: ONE scoped session built around a single shared admissibility
predicate, delivered in two stages within it** (Grok's unified-predicate architecture eliminates the
double-touch of the enumeration sites and the stranded-`_SBW_CI_` guard that a Codex-style independent A/B
split would create; Codex's fail-closed-first sequencing is preserved as the stage boundary; one restructured
charter up front honors `feedback_no_session_cascade`):

- **Stage 1 — extract `CanEmitConstructorForReceiver(MethodDecl, receiver)` and make CSM enumeration + the
  `_SBW_CI_` unconditional-conformance emission both consume it; wire in the existing cheap filters.**
  Mechanically safe (turns emission *off* for currently-failing shapes): closes facet A + facet C + the
  `_SBW_CI_` guard ("only emit `extension T: _SBW_CI_{hash}` when an admissible init exists on the
  *unconstrained* type"). 8+4+2 = 14 of the 30 errors, no satisfaction logic.
- **Stage 2 — extend the predicate to evaluate the already-parsed extension-origin `where` constraints
  against the closed conformer.** Satisfy-when-provable (`where Value.ValueType == Int` *is* valid for an
  `Int` conformer — do **not** blanket-skip constrained-extension inits, that loses real surface),
  skip-with-tombstone otherwise. Closes facet B (14+2 = 16 errors). Judgment-call stage; lands second so a
  satisfaction-logic regression is attributable.

One BindingTests fixture covers all three facets — a generic type over a known conformer with (a) a
`_const`-param init, (b) an `@available(*,unavailable)` init, (c) a constrained-extension init
(`extension … where Value: Collection { init(…) }`) — plus a *satisfying* and a *non-satisfying* conformer;
assert the closed specializations that should appear and the skips for the ones that shouldn't. Ships
**with the fix** (a red fixture would break the gate). General-mechanism hardening, not AppIntents-specific:
any framework with `_const` / unavailable / constrained-extension inits on a generic type with closed
conformers trips it. Gates enabling AppIntents in `validation-libraries.json`; Session 9 (SwiftUI) and
Session 10 (residual consumers) are independent and not blocked.

> Dissent recorded: Codex argued for two fully independent sessions (A then B) for risk isolation; Grok
> argued one session is mandatory because the A/B split strands mixed cases (a `_const` init that *also*
> lives in a constrained extension) and double-touches the enumeration/decision sites. Synthesis adopted
> Grok's single-predicate seam with Codex's stage ordering — i.e. one session, two stages, the predicate as
> the seam (the exact two-PR carve Grok offered as its fallback).

#### Shipped outcome (2026-05-26 — unified `ConstructorAdmissibility` predicate)

The single-session, two-stage cut above shipped. The seam is the static `ConstructorAdmissibility`
(`Emitter/StringEmitter/ConstructorAdmissibility.cs`): `HasConstLiteralParameter`,
`PassesConstructorCheapFilters(MethodDecl, out reason)` (rejects `_const`-param + `IsModuleInternal`), and
`HasUnsatisfiableParentGenericExtensionConstraint(MethodDecl, TypeDecl)` (evaluates already-parsed
extension-origin `where` keys against the chosen conformer — Stage 2). CSM enumeration and `_SBW_CI_`
conformance emission both consume it.

**A third consumer was required that the plan did not anticipate.** Suppressing the Swift `_SBW_CI_`/GSF
*wrappers* for an inadmissible generic-class init does not, by itself, remove the **C#** surface: the C#
`ConstructorHandler.Emit` (`MethodHandler.cs`) then falls back to a direct `[CallConvSwift]` P/Invoke against
the raw `$s…` generic-class init symbol. That *compiles* but is **not ABI-correct** — a generic-class init
needs type metadata / PWT in registers a plain P/Invoke cannot set up (a generic *struct* direct-CallConvSwift
init is the established, working path and is untouched). So a parallel suppression guard lives in
`ConstructorHandler.Emit`, deliberately **narrowed** to fire only when `ConstructorAdmissibility` actually
refuses the ctor (cheap-filter failure *or* unsatisfiable parent-generic extension constraint). The narrowing
matters: a broad "any no-wrapper generic-class init" guard also caught `CrossHostSiblingClass<T>(by:)` — a
T-typed designated init already on the established direct path, referenced by a `[Skip]`'d-but-still-compiled
test — and would have broken the compile gate.

**Facet (d) correction — the unavailable-init facet is NOT closed by an end-to-end fixture.** The plan
counted "facet C (unavailable, 4×)" as closed by Stage 1. In practice `@available(*, unavailable)` members are
**stripped from a from-source `.abi.json` by `swiftc`** before the parser ever sees them, so the
constructor-admissibility BindingTests fixture *cannot reproduce* facet (d): the unavailable init simply has
no decl for the predicate to reject. What shipped for facet (d) is a **defense-in-depth `IsModuleInternal`
reject** inside `PassesConstructorCheapFilters` — verified only by a unit test against a synthetic model, not
by the end-to-end Swift→C# fixture. The 4 AppIntents `'init()' is unavailable` errors are therefore **not
claimed closed by a tested path**; they would be caught *iff* an unavailable decl reaches the parser with
`IsModuleInternal` set, which the from-source pipeline does not exercise. Facets actually closed by the
shipping fixture: **A (`_const`)**, **B (constrained-extension `where`)**, and the **`_SBW_CI_`
unconditional-conformance guard**.

Fixture: `BindingTests/Sources/SwiftBindingsTestLib/Generics/ConstructorAdmissibility.swift` —
`final class CtorAdmBox<Value: CtorAdmValue>` with an admissible `init(tag:salt:)`, a `_const` init, an
`intMarker` init in `where Value.Element == Int`, and a `ropeFlag` init in
`where Value.Element: CtorAdmCollectionish`, over a satisfying (`CtorAdmIntValue`) and a non-satisfying
(`CtorAdmRopeValue`) conformer. C# coverage: `BindingTests/RuntimeTestsApp/Generics/ConstructorAdmissibilityTests.cs`
(functional CSM round-trips + structural reflection-absence) and
`src/Swift.Bindings/tests/UnitTests/EmitterTests/ConstructorAdmissibilityTests.cs` (predicate unit tests).

**Stage 2 refinement (end-of-task Codex review).** The independent review caught a latent false-acceptance
in the Stage 2 `DependentMemberClauseSatisfied` helper: a constrained-extension same-type clause whose LHS is
a dependent member and whose RHS is a bare generic-parameter placeholder (`τ_0_0.Element == τ_0_1`) was
deferred to coupling for *any* `τ_`-prefixed RHS — but coupling only registers when a **method-own** param is
an endpoint (`AddCoupling`, ~lines 702-741). A **parent-parent** RHS (another parent-tuple param) is
registered by no path, so deferring it would let CSM emit a closed form `swiftc` rejects. The fix distinguishes
the two: a method-own RHS still defers (coupling enforces at cartesian pairing); a parent-parent RHS is
rebound to its already-chosen conformer in the parent tuple and *proven* (satisfy-when-equal, fail-closed
otherwise) — matching the documented Stage 2 contract. This path is not reachable from the single-param
fixture or AppIntents `EntityProperty<Value>`; it's covered by three new unit tests in
`ConcreteSpecializationEngineTests` (parent-parent admit, parent-parent reject, method-own defer) that
complement the pre-existing bare-LHS coverage. The regenerated bindings are byte-identical before and after
the refinement (the reachable surface uses no such clause), so the runtime gates below are unaffected. (Grok's
parallel review returned clean on all four focus areas; the false-acceptance was Codex-only.)

Gates (zero-regression): unit **11967 pass** (+3 Stage-2-refinement tests); `binding-tests --compile-only`
**Succeeded** (fail-closed); sim (Mono JIT) **2338 pass / 0 fail / 0 crash**; device (NativeAOT) **2359 pass /
0 fail / 0 crash** — all 8 fixture tests green on both runtimes; generated output byte-identical across the
Stage 2 refinement.

---

## Generator pieces required

### Closed `AppEntity` conformer enumeration

`Session 4`'s `KeyPathBagWalker.BuildTypeDeclIndex` already builds module-scope `SwiftQualifiedName → TypeDecl` indexes; this session needs a cross-module variant that enumerates every closed conformer of `AppIntents.AppEntity` across all bound modules. The `ConcreteSpecializationEngine.GetConformers(protocolName)` API (`ConcreteSpecializationEngine.cs:534+`) is the right entry point — extend / verify it handles `AppEntity` conformers from outside the current emit module.

In practice, the closed-conformer set is small (AppIntents itself + any framework that ships an `AppEntity` conformer + the consumer's own bindings — `MockBook` for BindingTests, and zero or one in each validation-libraries entry that imports AppIntents). The combinatorial blow-up is in the **C# overload cross product**, which is `(Entity, Value, init-shape, KeyPath flavor)` — *not* per-property. Two same-Value-type storage properties on the same conformer (e.g. `MockBook.id: String` and `MockBook.title: String`) collapse to a single C# overload, because the C# signature `(string identifier, KeyPath<MockBook, string> getter)` does not embed property identity — the caller selects the property by passing `MockBookAppEntityKeyPaths.Id` vs `MockBookAppEntityKeyPaths.Title` into that one overload.

### `KeyPath<Entity, Value>` singleton emission for `AppEntity` conformers

For each closed `AppEntity` conformer, emit typed singletons for its **storage** properties whose `Value` type matches one of the `EntityProperty where Value.ValueType == X` extension blocks. Reuse `KeyPathBagWalker.IsEmittableProperty` for the property gates. New container-class naming: `{ConformerSan}AppEntityKeyPaths` (parallels Session 4's `{ConformerSan}{BagName}KeyPaths`).

For `MockBook` this would emit:
- `MockBookAppEntityKeyPaths.Id` → `WritableKeyPath<MockBook, String>` (since `var id: String`)
- `MockBookAppEntityKeyPaths.Title` → `WritableKeyPath<MockBook, String>`
- `MockBookAppEntityKeyPaths.PageCount` → `WritableKeyPath<MockBook, Int>`

Swift trampoline scheme matches Session 4: `SBW_KP_AppEntity_{ConformerSan}_{PropertySan}_{hash8}`.

### Per-(Entity × Value × init-shape × KeyPath-flavor) C# overload emission

For each closed `AppEntity` conformer × each `where Value.ValueType == X` extension × each KeyPath-taking init shape (getter / getSetter / asyncGetter / …) × each KeyPath flavor (`KeyPath` vs `WritableKeyPath`), emit one closed C# convenience-init overload. The overload signature substitutes `Entity` with the conformer's C# type and closes `Value` to the C# projection of the extension's `Value.ValueType`.

Pragma: this means a method-own-generic init like:
```swift
init<Entity>(identifier: String, getter: KeyPath<Entity, Value>) where Entity : AppEntity
// inside: extension EntityProperty where Value.ValueType == Swift.Int
```
produces **one** closed C# overload per `(Entity, init-shape, KeyPath flavor)` tuple (with `Value` closed by the extension block):
```csharp
public EntityProperty(string identifier, KeyPath<MockBook, nint> getter) { … }
// One overload for MockBook + Int extension. Caller picks the property by passing
// MockBookAppEntityKeyPaths.PageCount as the getter. Two Int-typed properties on
// MockBook would still produce ONE overload, not two.
```

Overload disambiguation (constraint #16 in `constraints.md`): the closed overloads must be method-overload-disambiguatable at the C# call site. Since `EntityProperty<X>` is itself generic on `Value`, and the convenience init signature is `(string, KeyPath<Conformer, ClosedValue>)`, the disambiguator is the `(Conformer, ClosedValue)` pair — *not* the property. Per-property emission would produce DuplicateSignature failures whenever a conformer has ≥2 same-Value-type storage properties (MockBook has `id` and `title`, both `String`). Verify no DuplicateSignature failures by collapsing properties of the same `Value.ValueType` into a single overload.

### `WasEmitted` plumbing

The standard `IMethodPostProcessor` pattern: when the new emitter claims a method-own-generic init, set `WasEmitted = true` so `MethodHandler` doesn't re-emit it as a tombstone. Reuse Session 4's `WasEmitted` discipline.

### `AppEntity` is a protocol with associated types

`AppEntity` requires `static var typeDisplayRepresentation: TypeDisplayRepresentation`, `var displayRepresentation: DisplayRepresentation`, `associatedtype DefaultQuery : EntityQuery`. The associated-type machinery from Sessions 1–6 already handles this; verify that closed-conformer enumeration picks up types whose `DefaultQuery` is itself an associated type.

## Phase 8b.1 — Conformer enumeration

Add a `GetAppEntityConformers()` (or generalize to `GetConformers("AppIntents.AppEntity")`) entry point on `ConcreteSpecializationEngine`. Walk the binding output's module-level type tree, filtering for `: AppEntity` conformance (direct or via `AssistantEntity` macro expansion). Test against:
- `MockBook` (BindingTests)
- Apple-shipped `AppEntity` conformers (none in the iOS SDK base layer; some in `AppIntentsFinanceKit`, etc. — count to confirm zero or few)
- Validation-libraries entries that adopt AppEntity (likely zero for v1; non-blocking)

## Phase 8b.2 — `AppEntityKeyPaths` container emission

New emitter file: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AppEntityKeyPathSingletonEmitter.cs`. Mirrors `KeyPathSingletonEmitter` shape but driven by conformer enumeration rather than by walking a generic parent's bag-demand. Per closed conformer, emit a `{Conformer}AppEntityKeyPaths` static partial class with one `WritableKeyPath<Conformer, ValueType>` or `KeyPath<Conformer, ValueType>` property per emittable storage property.

Hooked from `ClassHandler` / `FrozenStructHandler` / `NonFrozenStructHandler` after their existing per-type post-processing — same place `KeyPathSingletonEmitter.EmitKeyPathSingletonsForGenericParent` runs today, but with the conformer driver instead of the parent-bag driver.

## Phase 8b.3 — `EntityProperty` convenience-init overload emission

New emitter file: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EntityPropertyInitOverloadEmitter.cs`. Driven by the `EntityProperty<Value>` class's constrained extensions in the AppIntents `swiftinterface`. For each `where Value.ValueType == X` extension that has ≥1 storage property of matching type on a given closed `AppEntity` conformer, for each `init<Entity>` shape in that extension, emit one closed C# `EntityProperty` constructor overload (per `(Entity, init-shape, KeyPath flavor)` — not per-property). The presence of multiple matching-Value-type storage properties on the same conformer drives `{Conformer}AppEntityKeyPaths` singleton emission breadth, *not* C# overload multiplicity.

Wired as an `IMethodPostProcessor` against each ctor in the extensions, with the postprocessor responsible for setting `WasEmitted = true` on the source ctor decl so the default tombstone path doesn't re-emit.

The Swift trampoline this calls into is `init<Entity>(…)` directly — no separate `@_cdecl` wrapper needed for the init itself; the **getter:** parameter consumes a typed-singleton `IntPtr` and the init's Swift wrapper invokes the original `init<Entity>` with the closed `Entity` substituted in.

## Phase 8b.4 — BindingTests fixture

`BindingTests/Sources/SwiftBindingsTestLib/AppIntents/MockAppEntity.swift` already declares `MockBook`. Extend `BindingTests/RuntimeTestsApp/AppIntents/MockAppEntityTests.cs` with new tests:
- Construct `EntityProperty<nint>` (the C# closed form of Swift `EntityProperty<Int>`) via the new `(identifier:, getter:)` overload using `MockBookAppEntityKeyPaths.PageCount`. Verify the resulting wrapper has the expected identifier and value-typed wrapped value.
- Construct `EntityProperty<string>` (closed form of `EntityProperty<String>`) via `(identifier:, title:, getter: MockBookAppEntityKeyPaths.Title)`. Verify identifier + localized title.
- Construct via `getSetter:` against a `WritableKeyPath` singleton; verify the resulting wrapper accepts mutation.
- Cover at least one `where Value.ValueType ==` extension other than Int (e.g. `String`) to prove cross-Value-type dispatch.
- Sim + device (NativeAOT) gates.

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | Baseline + new unit coverage for `EntityPropertyInitOverloadEmitter` |
| `nuke binding-tests --sim` | New `AppIntentsEntityPropertyTests` cells pass |
| `nuke binding-tests --device` | Same |
| `nuke validate` (opt-in) | AppIntents `cs_compile` ratchets up; not a per-commit gate |

## Exit criteria

- For every closed `AppEntity` conformer × every `where Value.ValueType ==` extension that has ≥1 matching storage property on that conformer × every `init<Entity>(getter:|getSetter:|asyncGetter:)` shape in that extension × KeyPath/WritableKeyPath flavor: one C# overload emits and at least one runtime test exercises it.
- `MockBook` round-trips through `EntityProperty<nint>(identifier:getter:)` and `EntityProperty<string>(identifier:title:getSetter:)` from C#.
- BindingTests passes sim + device.
- No overload-disambiguation collisions.

## Risks

- **Wrapper-lib `.dylib` size.** Session 4 emits ~22 trampolines per conformer for MusicKit's filter bag; AppEntity could emit comparable numbers per conformer, but since the v1 conformer set is small (one in BindingTests, zero-to-few in validation-libraries), total binary impact is bounded for v1. Re-measure if the closed conformer set grows.
- **`Value.ValueType` to closed `Value` mapping.** `EntityProperty<Value>` is parameterized on the wrapper type, but the convenience inits in `where Value.ValueType ==` extensions discriminate by the **inner** `Value.ValueType` associated type. Verify the generator picks the correct closed `EntityProperty<X>` (i.e., the `X` such that `X.ValueType == Int`) when emitting the C# overload.
- **`asyncGetter:` variants** introduce a closure parameter shape that needs the existing closure-marshalling machinery; verify it composes (likely just routes through existing `ClosureEmitter` paths; flag if it doesn't).
- **iOS 26 / macOS 26 / etc.** — all the KeyPath-keyed inits are gated to the iOS 26 family of OSes. The C# overloads must carry `[SupportedOSPlatform("ios26.0")]` etc. The CA1416 / availability propagation fix from Session 8 v1 is a prerequisite for this to work end-to-end. Wrapper-lib `@available` for the per-conformer trampolines also needs this floor.
- **Open-conformer case** — C#-user-defined `AppEntity` subclasses remain unsupported; explicit user-facing limitation, tracked in the wiki.

## v1 limitations (cross-module conformer enumeration)

`ConcreteSpecializationEngine.GetConformers("AppIntents.AppEntity")` only sees conformers that the binding generator has direct ABI access to:

- **Visible**: `AppEntity` conformers declared in the bound module (the consumer's own library) or in dependent modules that are bound in the same generator invocation. Sufficient for the BindingTests `MockBook` fixture and for "consumer ships their AppEntity types in the same Swift library they bind to C#."
- **Not visible**: Apple-shipped `AppEntity` conformers in sibling frameworks (e.g. `AppIntentsFinanceKit.FinanceAccount`) unless added via `specialization-hints.json` `AllowedModules` scoping. These do not surface in the closed-overload emission.
- **Not visible**: `AppEntity` conformers declared by an app developer in *their* assembly when consuming a pre-built AppIntents binding NuGet. Trampoline emission requires the conformer type to live in the same Swift TU as the trampoline, which forecloses the "bind AppIntents once, then add conformers per app" workflow at the generator level.

This is acceptable for v1. The product story is: "your AppEntity types live in the same library as your generated AppIntents bindings, and the binding regenerates whenever you add or remove an AppEntity type." Documented in the wiki Known Limitations.

Roadmap item (see `roadmap.md`): cross-module / cross-assembly conformer enumeration is its own architectural session (changes to `ConcreteSpecializationEngine`, TypeDatabase dependency-closure aggregation, and `Program.cs` engine construction). Open when a real consumer asks.

## Implementation outcomes (shipped 2026-05-24)

The KeyPath-singleton half of this session (8b.1 + 8b.2 + 8b.4) shipped. The
`EntityProperty`-construction half (8b.3) shipped as a **consumer-side factory emitter**
(`ConformerKeyPathInitFactoryEmitter`) against a BindingTests dependency stand-in
(`MiniEntityProperty<Value>`); the *real* AppIntents `EntityProperty` remains blocked on
the parser-suppression prerequisites documented below.

### Shipped: `AppEntityKeyPathSingletonEmitter` (8b.1 + 8b.2)

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/AppEntityKeyPathSingletonEmitter.cs`.
Module-scope emitter, called from `ModuleHandler` at namespace scope after the type
walk (alongside the foreign-extension / CSM container classes), **not** from
`Class/Struct` per-type handlers as the plan above sketched. The handler hook the plan
named (`EmitKeyPathSingletonsForGenericParent`'s call site) is parent-bag-demand-driven;
the AppEntity surface has no parent-bag demand signal, so a module-scope driver that
enumerates conformers directly is the correct shape.

Key differences from the plan as written:

- **Root is the conformer itself**, not a nested bag. `\MockBook.title` →
  `WritableKeyPath<MockBook, String>`, container `MockBookAppEntityKeyPaths`. Simpler
  than Session 4 (no associated-type-bag resolution).
- **Conformer eligibility** is gated by `IsEligibleConformerType` (factored out, unit-
  tested): reject generic (`Foo<T>` has no single closed Root), SPI-protected, and
  module-internal conformers. The caller additionally requires the conformer's
  `TypeDecl` to be present in the emitted module's type-decl index (the local-to-module
  gate — see v1 limitations).
- **Computed properties are admitted** (unlike Session 4's stored-bag path). The shared
  `KeyPathBagWalker.IsEmittableProperty` gained an `allowComputed` parameter; the
  AppEntity emitter passes `allowComputed: true` because a concrete root forms valid
  KeyPaths for computed properties — `\Root.getOnly` is a read-only `KeyPath` and
  `\Root.getSet` is a `WritableKeyPath`. Flavor follows the presence of a setter, so a
  get-only computed property correctly yields `KeyPath` (not `WritableKeyPath`).
- **Effectful read-only getters are excluded.** `var foo: T { get throws }` /
  `{ get async }` are valid Swift but cannot be referenced by a `\Root.foo` KeyPath
  literal (Swift rejects key paths to accessors with effects). The walker rejects them
  (`EffectfulGetter`); the gate is dormant for stored properties (whose synthesized
  accessors are never effectful) and only bites once `allowComputed` admits computed
  leaves.
- **Symbol scheme** `SBW_KP_AppEntity_{module}_{conformerSan}_{propSan}_{hash8}` — a
  prefix disjoint from Session 4's `SBW_KP_`, method wrappers' `SBW_`, and CSM's
  `SBW_CSM_`, so the dedup registries never collide. Dedup key
  `AppEntity|{conformer.SwiftQualifiedName}` in the shared singleton-container registry.
- Availability is merged from property + ancestor + the conformer's own availability
  record so the Swift trampoline's `@available` floor and the C# `[SupportedOSPlatform]`
  agree with what swiftc type-checks against the device SDK. For a **writable** path the
  `\Root.prop` literal references the setter, so when the setter carries a tighter floor
  (`SetterAvailabilityAnnotations`, e.g. getter iOS 17.0 / setter iOS 17.4) that
  stricter list is the member base — the `WritableKeyPath` isn't exposed under the
  looser getter floor.

For `MockBook` this emits `MockBookAppEntityKeyPaths.{Id, Title, PageCount}` as
`WritableKeyPath` (stored `var`s), plus `Summary` as a read-only `KeyPath` (get-only
computed) and `DisplayTitle` as a `WritableKeyPath` (get/set computed).

### Shipped: BindingTests + unit coverage (8b.4)

- `BindingTests/Sources/SwiftBindingsTestLib/AppIntents/MockAppEntity.swift` —
  read-through (`readMockBookString`/`readMockBookInt`), write-through
  (`writeMockBookString`/`writeMockBookInt`, returning a mutated copy per the
  inout-write-back generator gap noted in `KeyPathFoundation.swift`), and
  AnyKeyPath-equality (`sameMockBookPath`) consumers. `MockBook` also carries `summary`
  (get-only computed) and `displayTitle` (get/set computed) to exercise the
  computed-property surface.
- `BindingTests/RuntimeTestsApp/AppIntents/MockAppEntityTests.cs` — Session-8b tests:
  container resolution, conformer-rooted flavor, lazy-caching, read/write round-trips
  through Swift consumers, Swift-sided equality, plus computed-property coverage
  (`Summary` is a read-only `KeyPath`, `DisplayTitle` is a `WritableKeyPath` whose write
  mutates the backing `title`). 2269 pass on sim, 2290 on device (NativeAOT), 0 crash.
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/AppEntityKeyPathSingletonEmitterTests.cs`
  — 10 tests: `IsEligibleConformerType`'s negative gates (generic / SPI / internal
  rejected; concrete and frozen-concrete accepted), the `allowComputed` switch (stored
  admitted either way; computed rejected by default, admitted with `allowComputed`;
  static computed still rejected), and the effectful-getter exclusion (throwing / async
  computed getters rejected even with `allowComputed`).

### Shipped: `ConformerKeyPathInitFactoryEmitter` (8b.3 via the factory route)

`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ConformerKeyPathInitFactoryEmitter.cs`.
A dependency's generic class can declare a method-own-generic, KeyPath-keyed init
(`init<Entity: AppEntity>(identifier:, getter:/getSetter: KeyPath<Entity, Value>)`) that
tombstones in the dependency's *own* binding (C# has no generic constructors with
method-own type parameters). The consumer-side emitter rescues it: for each closed
AppEntity conformer (`MockBook`), it emits a `{Conformer}{Dep}Factory` container with
`CreateFromGetter` / `CreateFromGetSetter` overloads (one per distinct property value
type) that build the dependency object through a Swift `@_cdecl` trampoline calling the
init with `Entity` closed to the conformer, then adopt the returned `+1` ARC handle via
`SwiftMarshal.MarshalFromSwiftObject<T>`. The factory consumes the already-shipped
`MockBookAppEntityKeyPaths` singletons as its KeyPath argument.

Key points:

- **Type-projection split.** The KeyPath parameter is typed in idiomatic C#
  (`KeyPath<MockBook, string>`, `projection.PublicType`) so it binds the emitted
  singleton; the dependency's `MiniEntityProperty<Value>` return generic argument and the
  `MarshalFromSwiftObject<…>` call use the **marshal** type (`SwiftString`, an
  `ISwiftObject` — `projection.MarshalFromSwiftType`) so the dep binding's
  `TypeMetadata.GetTypeMetadataOrThrow<Value>()` resolves at runtime. For blittable values
  (`Int`→`nint`) the two coincide; for classes both are the qualified name.
- **Recognizer scope (`TryRecognizeInitShape`, pure / unit-tested).** Admits exactly the
  shape it can faithfully emit: one class generic; a constructor with exactly one
  *method-own* generic (the ctor's `GenericParameters` also carries the class generic at
  depth-0, so the method-own set is isolated by excluding class-generic names); that
  generic constrained by exactly one protocol and no associated-type / concrete bound;
  the Value being the class's sole generic; scalar params restricted to `Swift.String`;
  one KeyPath param. `ReferenceWritableKeyPath` is rejected (value-type AppEntity roots
  only originate `KeyPath` / `WritableKeyPath` singletons, and the emission path collapses
  writable shapes to `WritableKeyPath`). Each rejected shape is a guard against emitting a
  trampoline that fails to type-check and is silently stripped by the wrapper build.
- **Availability** merges the dep-class floor (+ ancestors) with the conformer's floor,
  emitted as `@available` (Swift trampoline) + `[SupportedOSPlatform]` (C# factory +
  P/Invoke), mirroring `AppEntityKeyPathSingletonEmitter`.
- **Symbol scheme** `SBW_EPF_{module}_{conformerSan}_{depSan}_{labelSan}_{hash8}` — prefix
  disjoint from `SBW_KP_*` / `SBW_` / `SBW_CSM_`. Dedup key
  `{conformer}|{dep}|{label}|{csValueType}` in a dedicated `ModuleEmissionContext` set.
- **Coverage.** `BindingTests/Sources/SwiftBindingsTestLibDependency/MiniEntityProperty.swift`
  (dep stand-in), `BindingTests/RuntimeTestsApp/AppIntents/EntityPropertyFactoryTests.cs`
  (8 runtime tests: identifier round-trip, writable flag, AnyKeyPath-equality of the
  captured path vs. the singleton, string + nint, computed get-only / get-set, instance
  independence), and `ConformerKeyPathInitFactoryEmitterTests.cs` (17 recognizer unit
  tests). +8 on sim and +8 on device (NativeAOT), 0 crash.

**Known limitation — value-type availability (shared with `AppEntityKeyPathSingletonEmitter`).**
The merged availability floor combines the dep-class and conformer floors but **not** the
floor of the property's *value* type. A conformer property whose value type is introduced
on a later OS than both the conformer and the dep class would produce an under-annotated
Swift trampoline (stripped) and C# factory. Not triggered by the current fixture (all
value types are `String`/`Int`), and the sibling singleton emitter has the same gap — so
the fix is a coordinated change across both emitters plus a gated-value-type fixture, out
of scope for this pass. Surfaced for prioritization rather than silently patched untested.

### Blocked: 8b.3 `EntityProperty` convenience-init overloads (the *real* AppIntents type)

8b.3 as specified — attach closed convenience-init overloads to `EntityProperty<Value>`
via an `IMethodPostProcessor` — cannot proceed, for two independent reasons:

1. **`EntityProperty<Value>` is never in the consumer module's emit target.** In the
   BindingTests pipeline (and the real product story), AppIntents is a *dependency*
   (`--framework-dependency`), which is **resolved, not C#-emitted**. `EntityProperty`
   lives in the AppIntents binding, not in `SwiftBindingsTestLib.cs`. There is no
   `EntityProperty` decl in the emitted module for a post-processor to attach to, and
   C# constructors cannot be added to a type across assembly boundaries (partial
   classes don't span assemblies) — so a factory, not a constructor, would be required
   regardless.
2. **Even in the real AppIntents binding, the convenience-init family is parser-
   suppressed.** Per the 2026-05-23 regen table above: 467 `init` tombstones on
   `EntityProperty<TValue>` + 9 `C# does not support generic constructors with method-
   own type parameters`. The init signatures reach `@usableFromInline internal` /
   underscored shape types (`_IntentValueRepresentable`, `_SystemIntentValue`) not on
   the synthesizer allowlist, so `IsNodeModuleInternal` suppresses them before any
   post-processor sees the KeyPath-typed parameters. Line 91 flags this as a "Pre-8b
   session needed."

Both Codex and Grok independently confirmed the block (the `IMethodPostProcessor`-on-
ctors approach is also structurally impossible: `MemberValidationPipeline` Phase 6 skips
`init<Entity>` method-own-generic ctors *before* `handler.Emit`, so the post-processor
never fires for them). The KeyPath singletons shipped here are the standalone-useful
bulk of the surface; the `EntityProperty`-construction half needs a deliberate
direction decision (managed Apple-Supplement `EntityProperty<T>` with factories, vs.
resolving the parser-suppression prerequisites first, vs. deferring). Surfaced to the
project owner rather than auto-deferred to roadmap.

## References

- `04-typed-singleton-emission.md` — typed-singleton machinery to reuse / extend
- `08-appintents-productionization.md` — v1 (this is the follow-up)
- `AppIntents.swiftinterface` lines 6092 (class decl) + 223–290 (Int-Value extension) + 349+ (other Value.ValueType extensions)
