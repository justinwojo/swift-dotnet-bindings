# Session 8b — `EntityProperty.init<Entity>(…)` KeyPath-keyed convenience-init family

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

## References

- `04-typed-singleton-emission.md` — typed-singleton machinery to reuse / extend
- `08-appintents-productionization.md` — v1 (this is the follow-up)
- `AppIntents.swiftinterface` lines 6092 (class decl) + 223–290 (Int-Value extension) + 349+ (other Value.ValueType extensions)
