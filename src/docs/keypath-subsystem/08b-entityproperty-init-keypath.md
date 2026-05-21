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

## References

- `04-typed-singleton-emission.md` — typed-singleton machinery to reuse / extend
- `08-appintents-productionization.md` — v1 (this is the follow-up)
- `AppIntents.swiftinterface` lines 6092 (class decl) + 223–290 (Int-Value extension) + 349+ (other Value.ValueType extensions)
