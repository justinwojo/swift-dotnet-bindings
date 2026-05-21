# Session 8 — AppIntents productionization (v1)

Flip AppIntents from `unsupported` → `wrapperImportable`, land a minimal `AppEntity` conformer fixture in BindingTests so the framework no longer surfaces tombstoned, and close all the CA1416 / availability-propagation gaps the categorical audit surfaced. Three follow-up sessions (`08b`, `08c`, `08d`) cover the KeyPath-keyed surface — see *Deferred follow-ups* below.

## Why the original v1 plan was rewritten

The first draft of this doc (history: see `git log -- 08-appintents-productionization.md`) described `EntityProperty<Entity, Value>` as a two-generic-parameter struct with `static func property(getter: KeyPath<Entity, Value>, …)` factories and `AppShortcutParameterPresentation<Entity>` with a `keywordTitle(_ keyPath:)` method. Inspecting `AppIntents.swiftinterface` (iOS 26.2 SDK, `arm64e-apple-ios.swiftinterface`) showed neither shape exists:

- The real type is `@propertyWrapper final public class EntityProperty<Value> where Value : _IntentValue, Value : Sendable` — **one** generic param. The KeyPath-keyed factories are `convenience public init<Entity>(identifier:, getter: KeyPath<Entity, Value>) where Entity : AppEntity` declarations inside `@available(macOS 26.0, iOS 26.0, …) extension EntityProperty where Value.ValueType == Swift.Int` (and ~20 other `Value.ValueType ==` extensions). `Entity` is a **method-own free generic** on the init, not a generic param of the wrapping class.
- The real `AppShortcutParameterPresentation<Intent, Value, Parameter, ParameterKeyPath>` (`AppIntents.swiftinterface:909`) is a **four-generic-parameter** struct with `ParameterKeyPath : Swift.KeyPath<Intent, Parameter>` — a higher-kinded generic-param-is-a-KeyPath constraint. No `keywordTitle(_ keyPath:)` method exists. The KeyPath enters via the `init(for keyPath: ParameterKeyPath, …)` initializer.
- The real `PartialKeyPath` use sites are `Swift.PartialKeyPath<CoreSpotlight.CSSearchableItemAttributeSet>` as a parameter type for `indexingKey:` (Root is **hardcoded**, not a method-own generic) — not the "typed-KeyPath ↦ PartialKeyPath upcast at the call site" the original plan described.

The original `(704 / 240 / 0)` consumer-surface numbers from `00-overview.md` are still real — they just describe a different and larger emitter problem than the original plan claimed. Each of the three real KeyPath surfaces is itself Session 4-scale machinery and is tracked as its own follow-up.

## Goal (v1)

1. **Framework visibility** — flip `AppIntents` in `apple-frameworks.json` from `unsupported: true` to `wrapperImportable: true` so the generator emits `import AppIntents` into the wrapper Swift lib and the AppIntents type-tree becomes available to subsequent emitters.
2. **`AppEntity` conformer smoke** — add `MockBook : AppEntity` to BindingTests with C# tests covering: type-display representation, conformer construction, property roundtrip via free-function helpers, `DefaultQuery` static accessor, multi-instance independence. Confirms the conformer compiles through the bindings on sim (Mono JIT) and device (NativeAOT).
3. **CA1416 / `@available` propagation gap fix** — the categorical audit found that every `IMethodPostProcessor` synthesized-overload site that does **not** route through `WrapperEmitter`'s attribute pipeline drops `[SupportedOSPlatform]` on the C# side and `@available` on the Swift side. Same shape repeated at `KvoExtensionEmitter` (a class-level, non-`IMethodPostProcessor` emitter). Fixed at all 5 surfaces.
4. **`MarshallingHelpers` single-source-of-truth refactor** — `GetObjCBaseTypeName` had a literal `module is "SwiftUI" or "SwiftUICore" or …` predicate that duplicated `apple-frameworks.json`'s `unsupported: true` set. Now delegates to `AppleFrameworkRegistry.IsUnsupportedModule(module)`.

## Goal (deferred — see follow-up sessions)

- **8b** — `EntityProperty.init<Entity>(…)` KeyPath-keyed convenience-init family. Multiple `Value.ValueType ==` extension blocks, method-own-generic `Entity` constrained `where Entity : AppEntity`. Requires closed-`AppEntity`-conformer enumeration + per-`(Entity, Value, init-shape, KeyPath flavor)` C# overload emission (NOT per-property — same-Value-type properties on a conformer share one overload and disambiguate via the singleton passed for `getter:`/`getSetter:`). The closed `Value` for each overload is picked by matching a conformer storage property's value type to a `Value.ValueType ==` extension at emit time; multiple matching properties drive `{Conformer}AppEntityKeyPaths` singleton breadth, not overload multiplicity.
- **8c** — `AppShortcutParameterPresentation<Intent, Value, Parameter, ParameterKeyPath>` higher-kinded KeyPath generic param. New emitter shape — no existing machinery handles "generic param constrained to be a KeyPath type."
- **8d** — `PartialKeyPath<CoreSpotlight.CSSearchableItemAttributeSet>` as the `indexingKey:` parameter shape. Requires CoreSpotlight wrapperImportability + `CSSearchableItemAttributeSet` typed-singleton emission for its public storage properties.

## Dependencies

- **Session 3** (KeyPath foundation including `PartialKeyPath`) — already shipped.
- **Session 4** (typed singleton emission for closed conformers) — already shipped; the 8b emitter will reuse / extend its conformer-walking machinery.
- **Apple-supplement xcframework** (`80470fd8`) — Foundation Swift-overlay binding now produces `Foundation.NS*` classes that `AppEntity`'s `Hashable` / `Codable` conformances depend on. Already in place.

## Implementation outcomes (shipped)

- **`apple-frameworks.json`** — AppIntents flipped from `unsupported: true` → `wrapperImportable: true`. The generator now emits `import AppIntents` into the Swift wrapper-lib, struct/class `AppEntity` conformers compile through the bindings, and `EntityProperty` itself binds as a closed-generic class (without its KeyPath-keyed inits, which are deferred to 8b).
- **`AppleFrameworkRegistryTests`** — unit-test rows updated to match the new state: `IsUnsupportedModule_ReturnsExpected("AppIntents", false)`, removed AppIntents from `JsonLoaded_AllUnsupportedModules_ArePresent`'s expected set, and added it to the pinned `IsWrapperImportableModule_ReturnsTrueForImportableModules` theory rows.
- **`BindingTests/Sources/SwiftBindingsTestLib/AppIntents/MockAppEntity.swift`** — `MockBook : AppEntity` fixture with `id`/`title`/`pageCount`, a `MockBookQuery : EntityQuery`, and free-function helpers gated on `#if canImport(AppIntents)` + iOS 16/macOS 13/tvOS 16/watchOS 9 availability.
- **`BindingTests/RuntimeTestsApp/AppIntents/MockAppEntityTests.cs`** — 6 tests covering free-function construction, constructor + property roundtrip, free-function/accessor agreement, DefaultQuery static accessor, multi-instance independence. Pass on sim (Mono JIT, +28 vs prior baseline) and device (NativeAOT, stable at 2254 — see `feedback_device_gate_flake_vs_regression.md` for the 2258 ↔ 2254 flake analysis).
- **CA1416 gap — categorical audit of all `IMethodPostProcessor` forwarders** — narrowing-overload and forwarder emitters were dropping `[SupportedOSPlatform]` from the source method. Codex/Grok round-1 review found the original three-site patch was incomplete: there are five total `IMethodPostProcessor` surfaces that synthesize public C# overloads/forwarders, and four of them re-emit the signature directly rather than routing through `WrapperEmitter`'s attribute pipeline. Fixed at all four direct-emit sites: `NativeIntOverloadEmitter.TryEmitOverload`, `NativeIntOverloadEmitter.TryEmitIndexerOverload`, `ThrowingClosureSimplificationEmitter.TryEmitOverload`, `MarkerProtocolOverloadEmitter.EmitOverloads` (C# side), and `MethodHandler.TryEmitCompletionHandlerOverload` (Task-returning async wrapper for completion-handler APIs). `DefaultParameterOverloadEmitter` is the fifth surface and is the one path that already gets attributes through its full `WrapperEmitter` re-emit. Without these fixes, any platform-gated method that triggered any of these forwarders produced a CA1416 in consumer code.
- **Swift-side `@available` parity for marker-protocol shims** — `MarkerProtocolOverloadEmitter.EmitSwiftWrapper` emits a top-level `@_silgen_name` function that does NOT inherit enclosing-type availability. Fixed via `WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, parentTypeDecl))` so the wrapper compiles for SDK deployment targets below the gated API's introduced OS.
- **KVO extension emitter — class + per-property + Swift `@_cdecl` availability** — `KvoExtensionEmitter` synthesizes a separate `{Class}KvoExtensions` static partial class with `Observe{Prop}` extension methods plus Swift `@_cdecl` shims, and the original Session 7 implementation emitted none of `[SupportedOSPlatform]` / `@available`. Fixed via class-level `EmitSupportedOSPlatformsFromAnnotations(classDecl.AvailabilityAnnotations)` on the partial class, per-method emission deduped against the class floor (`prop.AvailabilityAnnotations` vs `classDecl.AvailabilityAnnotations`), and Swift-side merged-availability on the per-property `@_cdecl` observe shim. Surfaced by Grok's expanded categorical audit; same CA1416 shape as the post-processor forwarders but lives in a class-level emitter, not the `IMethodPostProcessor` table.
- **`MarshallingHelpers.GetObjCBaseTypeName` no longer duplicates the unsupported-modules list** — the literal `module is "SwiftUI" or "SwiftUICore" or …` predicate was a second copy of `apple-frameworks.json`'s `unsupported: true` set. Replaced with `AppleFrameworkRegistry.IsUnsupportedModule(module)` so the JSON registry is the single source of truth for ObjC-superclass-fallback decisions.

## Deferred follow-ups

Each follow-up has its own plan doc. They can ship independently and in any order; nothing in v1 above blocks them.

- **`08b-entityproperty-init-keypath.md`** — KeyPath-keyed convenience-init family on `EntityProperty<Value>`. The bulk of the consumer-surface number from `00-overview.md` lives here.
- **`08c-appshortcut-parameter-presentation.md`** — Higher-kinded `ParameterKeyPath : KeyPath<Intent, Parameter>` generic-param binding for `AppShortcutParameterPresentation` and friends.
- **`08d-partialkeypath-cssearchableitem.md`** — `PartialKeyPath<CSSearchableItemAttributeSet>` `indexingKey:` parameter binding (requires CoreSpotlight wrapperImportability work).

## References

- `00-overview.md` (consumer surface table — AppIntents is largest)
- `03-keypath-foundation.md` (foundation types including `PartialKeyPath`)
- `04-typed-singleton-emission.md` (per-property trampoline emission)
- `07-foundation-kvo-attributedstring.md` (KVO bridging — KVO emitter shares the availability-propagation pattern fixed in this session)
- AppIntents `swiftinterface` — `EntityProperty` (`AppIntents.swiftinterface:6092` + `:223–290` for one `Value.ValueType` extension family)
- AppIntents `swiftinterface` — `AppShortcutParameterPresentation` (`AppIntents.swiftinterface:909`)
- `.claude/rules/constraints.md` line 16 (overload disambiguation), line 29 (cross-module proxy)
