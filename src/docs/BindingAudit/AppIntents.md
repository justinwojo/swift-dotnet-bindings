# AppIntents — Binding Audit

- **Package**: SwiftBindings.Apple.AppIntents v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2, net10.0-macos26.2, net10.0-maccatalyst26.2, net10.0-tvos26.2
- **Native**: Apple AppIntents.framework (iOS 16.0+, macOS 13.0+, watchOS 9.0+, tvOS 16.0+, visionOS 1.0+)
- **Audited at**: swift-bindings 8dcc3032, generated 2026-06-27 (swift-dotnet-packages HEAD)

## Verdict

**You cannot author an App Intent, App Entity, or App Shortcut from C#, and you never will be able to through this binding alone.** The framework's entire product surface — `func perform()`, `@Parameter`, conforming a type to `AppIntent`/`AppEntity`, declaring `AppShortcuts` — depends on Swift macros, compiler-synthesized conformances, and build-time metadata extraction (`appintentsmetadataprocessor` scans *Swift source*, sees nothing for a C# app). The "309/312 types emitted" headline is misleading: the core authoring protocols come through as **empty or throw-only shells** (`IAppEntity<TSelf>` is a marker interface with no members, `AppIntents.cs:23619`; `IAppIntent<TSelf>` has only static-virtual props that throw, `:3819`; **there are zero `Perform` methods in the entire 50,615-line file**). What *is* usable is a peripheral data slice — 11 enums, a handful of constructible value types (`DisplayRepresentation.ImageType`, `AppShortcutPhrase`, `FileEntityIdentifier`) — plus a **limited management/interop slice that is genuinely callable from C#**: `IntentDonationManager.DeleteDonationsAsync` (`AppIntents.cs:27395`) and `IntentDonationMatchingPredicate` factories `DonationIdentifier`/`EntityIdentifier` (`AppIntents.cs:27020`). That slice is useful to a *mixed* Swift/C# app (or one consuming intents authored by a native component) — it manages/deletes donations made elsewhere — but it does nothing to enable a C#-*only* AppIntents product, since there is no way to author the intent in the first place. The package is **already correctly shelved**: the README declares it "not shipping for 1.0," intentionally absent from nuget.org. That decision is sound (the residual management/interop APIs do not, by themselves, justify shipping). The only correction this audit makes is that the README *oversells* the data surface (see §2): the marquee data types `EntityProperty<TValue>` and `DisplayRepresentation` are emitted but **unconstructable**, and their value accessors are skipped — "property values round-trip" is not supported by the emitted surface and there are no tests to prove otherwise.

## 1. Coverage

### Counts

| Dimension | Emitted | Total | % |
|---|---|---|---|
| Types | 309 | 312 | 99.0% |
| Members | 332 | 734 | 45.2% |
| Synthesized (generator-added) | 309 | — | — |
| Skipped (SkippedItems) | 2056 | — | — |

`EmittedMembersByKind`: Property 184, Method 104, Operator 44. Of the 134 real `@_cdecl` PInvoke entry points, the behavioral breakdown is **52 constructors, 39 equality (`_eq`), 28 other methods/props, 12 JSON Codable, 3 async** — i.e. the surfaced "behavior" is overwhelmingly *construct + compare + serialize value types*, not *do something*. The 13 `WrappedItems` are **all `ClosureParamTombstone` (SB0005)** — tombstoned-but-reachable inits on the comparator types, i.e. zero functional closure wrappers.

The 309 "types emitted" is real type-shell coverage but says nothing about usability. The report's `SkippedTypes: 3` counts only the three top-level **type** drops that fail at the type level: `AnyIntentValueProxy`, `AppShortcutOptionsCollectionProtocolProxy`, `ResultsCollectionProxy` (protocol proxies whose required members reference unsupported modules) — all correctly excluded. (Other dropped surface — the variadic-pack `Specification`, where `each R` has no C# equivalent, and the two underscore-internal types — is recorded under the member/`SkippedItems` buckets, not the 3-type denominator, because those drop at the member/usage level rather than as whole nominal types; don't read `SkippedTypes: 3` as "only six things were dropped.")

### Skip breakdown (2056) and classification

| Reason | Count | Class | Note |
|---|---|---|---|
| SwiftUIConstraint | 793 | **mostly (b)** | **764 of 793 (96%) cite `Foundation.LocalizedStringResource`, NOT a real SwiftUI View.** Only 26 cite an actual `View`/SwiftUI type. See finding below. |
| UnsupportedClosure | 364 | (a) | Closure params: `IntentFile.withFile(fileHandler:)`, comparator `mappingTransform:`, dynamic-options providers. Architectural — most are authoring-time callbacks. |
| UnsupportedSignature | 217 | (a) + small (b) | Includes `AppIntent.perform` ("unresolvable associated type reference"), `AssistantSchema.init` (generic ctors), variadic packs. `IntentFile.init`/`file`/`data` ("unsupported placeholder type") is a possible small (b) gap. |
| DuplicateSignature | 175 | (a)/cosmetic | `ctor(string)` collisions (`AppShortcutPhrase`, `EnumURLRepresentation`, `AppShortcutParameterPresentationTitleString`) + comparator closure inits. Label-dropping theme — milder here (survivor is still a `ctor(string)`). |
| UnsatisfiedGenericConstraint | 171 | (a) | `IntResolver`/`DoubleResolver`/… `resolve` need internal `_IntegerResolverInput`-style protocols a C# type can't conform to. |
| GenericProtocolConstraint | 112 | (a) | `StringInterpolation.appendInterpolation`/`appendLiteral` on associated-type-constrained types. |
| EveryProtocolConformanceSkipped | 108 | (a) | `AssistantSchemas.*` proxy conformances (Apple-Intelligence schema domain) — C# can't supply the conformance. |
| AnyTypeFallback | 57 | (a) | `any`-erased values (e.g. `IntentParameter.defaultValue → SwiftOptional<AnyType>`). |
| SynthesizedCodable | 18 | (a) | Synthesized `Codable` pruned by design. |
| UnsupportedType / GenericTypeCallback / StaticProtocolMember / NonBlittableCallConvSwift / others | 24 / 9 / 4 / 3 / 5 | (a) | Architectural. |

### Real generator gaps (b) worth naming

1. **`LocalizedStringResource` misclassified as "unsupported module (SwiftUI/Combine)" — the single dominant blocker (764 skips, ~96% of the SwiftUIConstraint bucket).** Exact diagnostic: *"Method signature references unsupported module (SwiftUI/Combine) in 'Foundation.LocalizedStringResource'."* `LocalizedStringResource` is a **Foundation** type (iOS 16+), not SwiftUI/Combine. This classification kills nearly every user-facing label and constructor in the framework: `EntityProperty` (479 skips), `IntentParameter` (259), `DisplayRepresentation`/`TypeDisplayRepresentation` titles, `AppShortcut.init`, `IntentDescription`, `IntentDialog`, etc. **Value: high, and almost certainly cross-library** (this type names every localized string across Apple frameworks). **Tractability: medium** — needs a real `LocalizedStringResource` projection (even a thin `string` wrapper) *or* correcting the module-attribution that flags it. **Caveat: for *this* package it changes nothing user-visible** — even fully constructible `DisplayRepresentation`/`EntityProperty` are inert without an authored intent. File it as a generator/cross-library finding, not an AppIntents unlock. (Count is also inflated by open-generic init-overload multiplication on `EntityProperty`/`IntentParameter`.)
2. **`IntentFile` init/`file`/`data`/`withFile` dropped ("unsupported placeholder type" / UnsupportedClosure).** `IntentFile` is a genuinely useful concrete data type (file blob + name + type). Its constructors and accessors are skipped, leaving a hollow shell. Small, bounded (b) gap — low value for this shelved package.

Everything else (perform's associated type, macro conformances, `_`-prefixed resolver protocols, AssistantSchemas proxies, variadic packs) is **architectural and not generator-fixable** — consistent with the cross-cutting "macro-driven frameworks" theme.

### Prioritized unlocks (value × tractability)

| Rank | Unlock | Value | Tractability | Caveat |
|---|---|---|---|---|
| 1 | Project/reclassify `Foundation.LocalizedStringResource` | High **cross-library** | Medium | Zero authoring value *here*; do it for the other Apple frameworks. |
| 2 | `IntentFile` ctor/accessor (placeholder-type) | Low | Medium | Hollow shell otherwise; minor. |
| — | Anything enabling `perform()` / `@AppIntent` authoring | — | **Not generator-fixable** | Needs a Swift-companion source generator (README "path to shipping"). |

## 2. C# Quality

**Authoring core — empty/throw-only shells (not a quality nit; the framework's reason for existing):**
- `IAppIntent<TSelf>` (`AppIntents.cs:3819`): five static-virtual props (`OpenAppWhenRun`, `SupportedModes`, `AuthenticationPolicy`, `IsDiscoverable`, `Description`), **every one throws `NotSupportedException("Static protocol members must be accessed on concrete types…")`**. No `perform()`. `AppIntent.perform` is skipped `UnsupportedSignature` — it returns an opaque `some IntentResult & …` associated type the generator can't project.
- `IAppEntity<TSelf>` (`:23619`): `{ }` — a marker interface, zero members.
- `IAppShortcutsProvider` (`:26175`): two static-virtual props (`AppShortcuts`, `ShortcutTileColor`), both throw.
- `AppShortcut` (`:40174`): **no public constructor** — all three `init`s skipped (LocalizedStringResource/SwiftUI). `AppShortcutsBuilder.BuildBlock`/`BuildExpression` (`:24995`) ARE real cdecl wrappers, but since no `AppShortcut` can be constructed, they can never be fed input. Dead end by construction.

**Marquee data types — emitted but hollow (this is where the README is too generous):**
- `EntityProperty<TValue>` (`:29983`): the `@Property` backing type. **No public ctor** (all inits skipped: SwiftUI/Combine, `NonBlittableCallConvSwift`, unsatisfiable `ISwiftObject` constraint). `wrappedValue` and `projectedValue` **both skipped** ("resolved to AnyType"). Usable surface = `IsOptional`, `Description`, `Equals`/`GetHashCode`/`ToString` only. You cannot create one or read its value → the README's "EntityProperty binds as a closed-generic class … property values round-trip" is **not supported by the emitted surface**.
- `DisplayRepresentation` (`:35138`): `title`, `subtitle`, `synonyms` all skipped (LocalizedStringResource); all inits skipped → **unconstructable**. Only `Image` (get), equality survive.
- `IntentParameter<TValue>` (`:5874`): `title`, `defaultValue`, `displayName`, `controlStyle`, `inputOptions`, `displayStyle`, `unit`, `inclusiveRange`, … all skipped (LocalizedStringResource or constrained-extension). Usable surface ≈ `IsOptional` + equality + nested `ValueState`.

**What IS genuinely usable (constructible + round-trippable):**
- **11 enums**, clean PascalCase, sensible: `IntentAuthenticationPolicy` (`:3861`), `ShortcutTileColor` (`:5597`), the comparison-operator family `HasValue`/`Equatable`/`OneOf`/`Comparable`/`StringComparisonOperator` (`:18832`–`:18877`), `EntityQueryComparatorMode` (`:36194`), and the **error enum `SetFocusFilterIntentError`** (`:39709`).
- **`DisplayRepresentation.ImageType`** (nested, ~`:35420`): the best example of a fully-usable type — real ctors `ImageType(string name, bool? isTemplate)`, `ImageType(byte[] data, …)`, `ImageType(NSUrl url, …)` and `(NSUrl, width, height, …)`, plus a `DisplayStyle` with `Circular`/`Default` static factories and `EncodeToJson()`/`DecodeFromJson(byte[])`.
- **Value/data types with real init + `_eq` + JSON-Codable cdecl wrappers**: `AppShortcutPhrase`, `ConfirmationConditions`, `EntityPropertyModifiers`, `EntityQuerySortingOptions`, `EntityURLRepresentation`, `EnumURLRepresentation`, `FocusFilterAppContext`, `Handle`, `FileEntityIdentifier`.
- **Async (good affordance)**: 3 methods surface as `Task` correctly — `IntentDonationManager.deleteDonations` (`…_async`), `RelevantIntentManager.updateRelevantIntents` (`…_async`), `FileEntityIdentifier.getFileURL` (`…_async`). These are system-side and need authored intents to be meaningful, but the async marshalling itself is the wanted shape.

**Naming / nullability / lifetime:**
- **`*TypeType` stutter theme: present but minimal — ONE instance**, the nested enum `PaymentTypeType` (`:32085`, referenced 7×). Confirms the cross-cutting generator naming bug; scope here is trivial.
- **`DuplicateSignature` label-dropping (175):** real but mild here — collisions collapse multiple Swift string inits to `ctor(string)` (`AppShortcutPhrase`, `EnumURLRepresentation`, `AppShortcutParameterPresentationTitleString`). A consumer can still construct (the survivor is `ctor(string)`); they just can't pick which Swift initializer. Less harmful than the VoIP-delegate case in LiveCommunicationKit, but the same root fix (disambiguate by Swift argument label) applies.
- Nullability/lifetime where members exist look correct (optionals → `T?`; `IDisposable` + dispose-remark on every native-owning type; `bool? isTemplate = null` defaults). No `SwiftUIBridge.cs` is generated (`BridgedViews: 0`) — correct, since the 26 real-View skips are protocol-proxy/param positions, not top-level `View` types to bridge.

## 3. Test Coverage

**Zero tests. There is no `tests/` directory for AppIntents at all** (`apple-frameworks/AppIntents/` contains only `bin/`, `obj/`, `README.md`, `.csproj`; no test project references the package anywhere in the repo). Given the package is intentionally unshipped, the *absence* is acceptable — but it has a concrete consequence: the README's surface-1 functional claims ("a conformer compiles through the bindings … queries resolve, and property values round-trip") are **entirely unverified**, and this static audit **contradicts** them (`EntityProperty`/`DisplayRepresentation` are unconstructable; `wrappedValue` is skipped). If the package is ever de-shelved, the first tests must be functional round-trips of the genuinely-usable slice — `DisplayRepresentation.ImageType` ctor + `EncodeToJson`/`DecodeFromJson` round-trip, the comparison-operator enum ordinals, and `FileEntityIdentifier` construct + async `getFileURL` — not metadata pokes. Until then, no claim of "the data surface works" should ship in the README without a test behind it.

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | 764 skips (96% of SwiftUIConstraint) are `Foundation.LocalizedStringResource` misclassified as "SwiftUI/Combine" | File a **cross-library** generator finding: project `LocalizedStringResource` (thin `string` wrapper) or fix its module attribution. Do it for the *other* Apple frameworks; it does not unlock AppIntents authoring. | Med | High (cross-lib) / nil (here) |
| 2 | Verdict/Docs | README oversells surface 1: `EntityProperty`/`DisplayRepresentation` are **unconstructable** and `wrappedValue` is skipped — "property values round-trip" is unsupported by the emitted surface | Tighten the README "What works" bullets to "type shells reference-resolve" rather than "construct + round-trip," or add a test that proves any round-trip. | Low | Med |
| 3 | Verdict | Authoring (`perform()`, `@AppIntent`/`@AppEntity`, AppShortcuts) is structurally impossible (associated types + macros + Swift-source metadata extraction) | Keep shelved for 1.0 (decision is correct). The only path is a Swift-companion source generator, as the README states. | — | — |
| 4 | Quality | `DuplicateSignature` (175) drops Swift argument labels on `ctor(string)` collisions; `PaymentTypeType` stutter (1) | Roll into the broad cross-library label-disambiguation + nested-name-doubling fixes (not AppIntents-specific). | Med | Low (here) |
| 5 | Tests | No tests exist; README's functional claims unverified | If de-shelved, add functional round-trips for `DisplayRepresentation.ImageType`, comparison-operator enums, `FileEntityIdentifier` (incl. async). | Low | Med (if de-shelved) |

**Bottom line for the owner:** *Not shippable as a functional binding, and correctly already shelved.* The README's "not shipping for 1.0 / authoring requires Swift" caveat is accurate and well-reasoned — keep it. The one refinement: stop claiming the data-modeling surface round-trips, because the two marquee data types (`EntityProperty`, `DisplayRepresentation`) are emitted-but-unconstructable and nothing tests them. A real, narrow usable subset exists (`DisplayRepresentation.ImageType`, the comparison/error enums, `FileEntityIdentifier`, `AppShortcutPhrase`) but it is peripheral and inert without an authored intent — document it as "scaffolding/data-types only," not as a capability.
