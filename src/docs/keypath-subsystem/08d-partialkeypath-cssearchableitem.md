# Session 8d — `PartialKeyPath<CSSearchableItemAttributeSet>` `indexingKey:` parameter

Bind the `indexingKey:` parameter that appears on many AppIntents convenience inits. Root is hardcoded to `CoreSpotlight.CSSearchableItemAttributeSet` — not a method-own generic — so this is fundamentally different from Sessions 4 / 8b / 8c.

Depends on: Session 3 (KeyPath foundation, including `PartialKeyPath` projection), Session 4 (typed singleton emission). Independent of 8b / 8c.

## Real API shape (verified against iOS 26.2 `AppIntents.swiftinterface`)

Across multiple `where Value.ValueType ==` extensions on `EntityProperty<Value>`:

```swift
@available(watchOS, unavailable)
@available(tvOS, unavailable)
convenience public init(indexingKey: Swift.PartialKeyPath<CoreSpotlight.CSSearchableItemAttributeSet>)

convenience public init(title: LocalizedStringResource,
                        indexingKey: Swift.PartialKeyPath<CoreSpotlight.CSSearchableItemAttributeSet>)

// And similar inits paired with getter:/getSetter: KeyPath<Entity, Value>:
convenience public init<Entity>(identifier: _const String,
                                indexingKey: Swift.PartialKeyPath<CoreSpotlight.CSSearchableItemAttributeSet>,
                                getter: KeyPath<Entity, Value>) where Entity : AppEntity
```

## Why this is distinct from 8b / 8c

The Root of the `PartialKeyPath<CSSearchableItemAttributeSet>` parameter is a fixed class declared in `CoreSpotlight`, not a method-own generic and not a generic-param-is-a-KeyPath-type constraint. The consumer needs to pass a typed singleton that wraps `\CSSearchableItemAttributeSet.title`, `\CSSearchableItemAttributeSet.contentDescription`, etc. — one per public storage property of `CSSearchableItemAttributeSet`.

Two prerequisites:
1. **`CoreSpotlight` must be `wrapperImportable: true`** in `apple-frameworks.json`. Verify current state; if unsupported, flip alongside (the wrapper-lib will need `import CoreSpotlight` for the typed-singleton trampoline to use `\CSSearchableItemAttributeSet.…`).
2. **`CSSearchableItemAttributeSet` must bind as a usable C# type.** It's an ObjC class with many `@NSManaged` storage properties; the binding generator already handles this category. Confirm at session time.

## Generator pieces required

### `CSSearchableItemAttributeSet` typed-singleton container

`CoreSpotlight.CSSearchableItemAttributeSetKeyPaths` — a single static partial class with one `PartialKeyPath<CSSearchableItemAttributeSet>` per public storage property of `CSSearchableItemAttributeSet`. Reuse Session 4's container-emission machinery; the driver is just one type (no per-conformer enumeration), so the emitter is simpler than 8b / 8c.

Property walk: enumerate `CSSearchableItemAttributeSet`'s public `var` storage. Public surface is large (~80 properties — title, contentDescription, keywords, authorNames, …); each becomes one typed singleton on the container.

Swift trampoline: `SBW_KP_CoreSpotlight_CSSearchableItemAttributeSet_{PropertySan}_{hash8}` returning the retained `PartialKeyPath`.

Note on `PartialKeyPath` vs `KeyPath`: the trampoline can emit `let kp: PartialKeyPath<CSSearchableItemAttributeSet> = \CSSearchableItemAttributeSet.title` and upcast at the literal. Verify with SIL that the `keypath` instruction emits the partial-erased shape correctly.

### Hookup at AppIntents `indexingKey:` parameter sites

The C# overloads from Session 8b that have an `indexingKey:` parameter accept `PartialKeyPath<CSSearchableItemAttributeSet>`. Since `PartialKeyPath<X>` is a base class of `KeyPath<X, Y>`, a typed `KeyPath<CSSearchableItemAttributeSet, String>` from Session 4 would normally satisfy a `PartialKeyPath<CSSearchableItemAttributeSet>` parameter via inheritance. But `CSSearchableItemAttributeSet`'s properties are public-readwrite reference-type properties, so the natural Session 4 emission would be `ReferenceWritableKeyPath<CSSearchableItemAttributeSet, X>`. The container should emit those at `PartialKeyPath<CSSearchableItemAttributeSet>` type (the strictest type the consumer ever needs for `indexingKey:`) — or alternatively at `ReferenceWritableKeyPath<CSSearchableItemAttributeSet, X>` and rely on C# inheritance to upcast to `PartialKeyPath`.

Pick at design time. The simpler choice — emit the singletons typed as `PartialKeyPath<CSSearchableItemAttributeSet>` — is what the API actually consumes; the loss of typed-`Value` is acceptable since the `indexingKey:` parameter doesn't use it.

## Phase 8d.1 preflight result (2026-05-21)

Three preflight items from `08-followups-execution-plan.md:88-95` executed; mixed result.

### Item 1 — `wrapperImportable` flip: GREEN

`apple-frameworks.json:229-234` flipped from `autoBridge + optionalFallback` to add `wrapperImportable: true`. `AppleFrameworkRegistryTests.cs:1240` adds `[InlineData("CoreSpotlight")]` to the wrapper-importable positive cases.

### Item 2 — Swift literal compile: GREEN

Standalone Swift compile of:
```swift
import CoreSpotlight
public func _kp_check() {
  let kp:  PartialKeyPath<CSSearchableItemAttributeSet>           = \CSSearchableItemAttributeSet.title
  let kp2: KeyPath<CSSearchableItemAttributeSet, String?>         = \CSSearchableItemAttributeSet.title
  let kp3: ReferenceWritableKeyPath<CSSearchableItemAttributeSet, String?> = \CSSearchableItemAttributeSet.title
}
```
emits SIL `keypath $ReferenceWritableKeyPath<CSSearchableItemAttributeSet, Optional<String>>, (objc "title"; root $CSSearchableItemAttributeSet; settable_property $Optional<String>, …)`. The `objc` keypath component variant emits at all three statically-declared keypath types — PartialKeyPath upcast works.

### Item 3 — `CSSearchableItemAttributeSet` binds cleanly as a usable C# type: **RED — STRUCTURAL BLOCKER**

Filtered apple-framework regen (`nuke validate --filter CoreSpotlight`) succeeded but the generated `CoreSpotlight.cs` is **38 lines and contains zero references to `CSSearchableItemAttributeSet`** (or any other `CS*` class). The abi.json contains 24 `CSSearchableItem*` declarations but every one is `"declKind": "Import"` — Swift re-exports of underlying Objective-C class declarations:

```json
{
  "name": "CoreSpotlight.CSSearchableItemAttributeSet",
  "printedName": "CoreSpotlight.CSSearchableItemAttributeSet",
  "declKind": "Import",
  "moduleName": "CoreSpotlight",
  "declAttributes": [ "Exported" ]
}
```

The Swift binding generator emits Swift-defined types only. ObjC-defined types (which is essentially all of CoreSpotlight — `CSSearchableItem`, `CSSearchableItemAttributeSet`, `CSPerson`, `CSIndexExtensionRequestHandler`, `CSSearchableIndex`, `CSUserQuery`, …) are intentionally out of scope for the apple-framework binding flow. There is no `CSSearchableItemAttributeSet` C# class for typed `PartialKeyPath<CSSearchableItemAttributeSet>` singletons to attach to.

The 8d plan's prerequisite #2 ("`CSSearchableItemAttributeSet` must bind as a usable C# type. It's an ObjC class with many `@NSManaged` storage properties; the binding generator already handles this category") is **wrong as written** — the Swift binding generator does NOT handle ObjC-rooted classes from a Swift module. The `@NSManaged` category that IS handled refers to *Swift-defined* `@objc` classes with `@NSManaged` storage (e.g. CoreData entities a consumer declares in Swift), not Apple-shipped ObjC classes re-exported via Swift module headers.

### Branching decision per plan line 99-100

> "Preflight surfaces framework-wide issues (…@NSManaged binding gap…) → park 8d, jump to Step 5 (8b first). AppIntents is already wrapperImportable, so 8b's substrate is the lower-risk path."

8b is also blocked at an upstream layer (`_IntentValue` projection — see `08b-entityproperty-init-keypath.md` Phase 0 result, cross-referenced from `08c-appshortcut-parameter-presentation.md`). **Both 8d and 8b cannot ship without prerequisite work.** Step 4 and Step 5 are both deferred pending a scope decision by the project owner.

Two viable shapes for unblocking 8d specifically (not chosen here — surface for decision):

1. **ObjC-source projection** — extend the binding pipeline (or add a sibling) to enumerate ObjC class declarations from `CSSearchableItemAttributeSet.h` (or via clang module metadata) and emit a minimal C# shape that hosts the typed-singleton container. The runtime call still has to land in ObjC, so the trampolines would need ObjC-aware emission as well. Significant scope.
2. **Manual XML supplement** — write a hand-curated `CoreSpotlightSupplement.xml` in the same shape `apple-frameworks.json` extension entries take, listing each public `CSSearchableItemAttributeSet` storage property as a synthetic property declaration. The generator's existing manual-DB mechanism could then drive typed-singleton emission as if the type were Swift-declared. Lower scope but still significant; the supplement file becomes maintenance-coupled to Apple SDK changes.

Neither is the right v1 path. The pragmatic exit is to land the framework flip + unit-test row that did succeed (preflight items 1 and 2), document the structural blocker (this section), and treat 8d as a v2 item.

What CAN ship now from 8d's scope:
- `wrapperImportable: true` flip — already in apple-frameworks.json with the unit-test row update.
- The flip is harmless in isolation: it lets a consumer's Swift wrapper write `import CoreSpotlight` if they ever bind a Swift-declared type that depends on CoreSpotlight Swift-defined symbols (there are none today, but the flip removes a future stumbling block).

What CANNOT ship: the actual `CSSearchableItemAttributeSetKeyPaths` typed-singleton container. Marked v1 limitation. See **Step 7 v1 limitations** in `08-followups-execution-plan.md`.

---

## Phase 8d.1 — Pre-flight: `CoreSpotlight` framework state

- Check `apple-frameworks.json` for `CoreSpotlight`. Current state: unsupported / wrapperImportable / fully-supported (verify before designing).
- If unsupported, plan the flip as a precursor; reuse the Session 8 v1 pattern (apple-frameworks flip, fixture, unit-test rows).
- Confirm `CSSearchableItemAttributeSet` itself binds. Run regen against a fixture that imports CoreSpotlight; verify the type appears in the generated C#.

## Phase 8d.2 — `CSSearchableItemAttributeSet` typed-singleton container

Extend Session 4's `KeyPathSingletonEmitter` (or add a sibling) to support "single-type fixed-Root closed driver" mode — given a single class type, emit its `KeyPaths` container without needing a conformer-enumeration round. The simplest path is a thin entry point that constructs the same per-property emission Session 4 uses but skips the protocol-bag-walking phase.

For `CSSearchableItemAttributeSet`: emit `CoreSpotlight.CSSearchableItemAttributeSetKeyPaths` with one `PartialKeyPath<CSSearchableItemAttributeSet>` per public storage property.

## Phase 8d.3 — AppIntents `indexingKey:` parameter consumption

No additional emitter work — the 8b post-processor will see `indexingKey:` parameters typed `PartialKeyPath<CSSearchableItemAttributeSet>` and produce C# overloads taking that type directly. The 8d container provides the C# side's typed singletons that satisfy the parameter.

## Phase 8d.4 — BindingTests fixture

`BindingTests/Sources/SwiftBindingsTestLib/CoreSpotlight/MockSpotlightUsage.swift` (or extend MockAppEntity.swift):
```swift
@available(iOS 26, macOS 26, watchOS 26, tvOS 26, *)
@available(watchOS, unavailable)
@available(tvOS, unavailable)
public func makeBookProperty() -> EntityProperty<Swift.String> {
  EntityProperty<Swift.String>(
    identifier: "book-title",
    indexingKey: \CSSearchableItemAttributeSet.title)
}
```

`BindingTests/RuntimeTestsApp/CoreSpotlight/CSSearchableItemAttributeSetKeyPathTests.cs`:
- `CoreSpotlight.CSSearchableItemAttributeSetKeyPaths.Title` is non-null and yields a working `PartialKeyPath<CSSearchableItemAttributeSet>`.
- Construct an `EntityProperty<string>` (closed form of Swift `EntityProperty<String>`) via the `indexingKey:` overload from C# using the typed singleton; verify identity / equality semantics survive the `PartialKeyPath` upcast.
- Sim + device gates.

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | Baseline (no new unit tests unless we add a fixture for the single-type fixed-Root emitter) |
| `nuke binding-tests --sim` | `CSSearchableItemAttributeSetKeyPathTests` cells pass |
| `nuke binding-tests --device` | Same |
| `nuke validate` (opt-in) | CoreSpotlight `cs_compile` ratchets up if the framework was unsupported pre-session |

## Exit criteria

- Every public storage property on `CSSearchableItemAttributeSet` has a typed `PartialKeyPath<CSSearchableItemAttributeSet>` singleton on the new container.
- The 8b `indexingKey:` C# overloads on `EntityProperty<…>` accept those singletons and the resulting wrapper passes round-trip tests.
- BindingTests passes sim + device.

## Risks

- **CoreSpotlight flip itself.** If CoreSpotlight is currently `unsupported`, flipping it surfaces whatever CA1416 / availability gaps the audited emitters caught for AppIntents — but Session 8 v1 already plugged those. Should be a clean flip.
- **`CSSearchableItemAttributeSet` property surface is large** (~80 properties). Single-type emit, so no combinatorial blowup, but trampoline count per wrapper-lib ticks up. Measure post-emission; not expected to be load-bearing.
- **watchOS / tvOS unavailability.** The `indexingKey:` overloads on `EntityProperty` are `@available(watchOS, unavailable) @available(tvOS, unavailable)`. The C# overloads and the typed-singleton container must mirror that. Reuses Session 8 v1's availability-propagation infrastructure.
- **Property-type heterogeneity.** `CSSearchableItemAttributeSet` has `String?`, `[String]?`, `Date?`, `Bool?`, etc. The typed-singleton emitter must handle erasure to `PartialKeyPath<Root>` (`Value`-slot type-erased) cleanly. Session 4 already supports `PartialKeyPath` projection (`TypeProjectionFactory.cs:552–558`); confirm the singleton emitter respects the `PartialKeyPath` family arity.

## References

- `03-keypath-foundation.md` (PartialKeyPath foundation)
- `04-typed-singleton-emission.md` (singleton container emitter to reuse / extend)
- `08-appintents-productionization.md` (Session 8 v1 — AppIntents flip + availability-propagation fix that 8d builds on)
- `08b-entityproperty-init-keypath.md` (sibling follow-up — emits the C# overloads that 8d's singletons satisfy)
- `AppIntents.swiftinterface` lines 234, 237, 251, 260, 263, 266, 269, 281, 284, 287, 290 (representative `indexingKey:` parameter sites)
