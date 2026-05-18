# Session 8 — AppIntents productionization

The biggest single consumer of `KeyPath<…>` in the iOS SDK: 704 lines, 240 `WritableKeyPath` references, two distinct architectural shapes (`EntityProperty` factory + `AppShortcutParameterPresentation` constrained generic). This session brings AppIntents from "fully tombstoned KeyPath APIs" to "idiomatic and complete".

## Goal

Bind AppIntents' KeyPath surface:

1. **`EntityProperty<Entity, Value>` getter/getSetter overloads** — declarative entity-property descriptors used by `AppEntity` conformers. Both read-only (`getter: KeyPath<Entity, Value>`) and read-write (`getSetter: WritableKeyPath<Entity, Value>`) overloads — this is the bulk of the 240 WritableKeyPath references.
2. **`AppShortcutParameterPresentation` constrained-generic methods** — protocol-extension methods that take `KeyPath<Self, …>` on `AppEntity` conformers and project to UI presentation metadata.
3. **`@Parameter` / `@Property` macro-generated KeyPath surface** — declarative property wrappers that themselves expose KeyPath-typed inits at the binding boundary.

## Why this session

- Largest singleton surface in the SDK. Sessions 1–6 establish the machinery; this session is the stress test — does the generator emit ~240 trampolines without symbol collisions, build-time blowup, or runtime regression?
- AppIntents is a developer-visible API: Shortcuts, Spotlight, Siri all depend on it. Without KeyPath, the entire `EntityProperty` / `AppShortcutParameterPresentation` surface is tombstoned and the framework is unusable from C#.
- Catches the "PartialKeyPath as polymorphic property descriptor" shape — `EntityProperty` is built around `PartialKeyPath<Entity>` for type-erased collection storage of mixed-`Value` properties on the same entity.

## Dependencies

- **Session 3** (KeyPath foundation including `PartialKeyPath`).
- **Session 4** (typed singleton emission for closed conformers).
- **Sessions 1, 2, 5** if any `AppEntity` is itself a PAT-constrained generic parent (uncommon but possible — verify at session time).

## Pre-image surface — `EntityProperty<Entity, Value>`

From `AppIntents.swiftinterface`:

```swift
public struct EntityProperty<Entity, Value> where Entity : AppEntity {
    public static func property(
        getter: @escaping (Entity) -> Value,
        // … plus localised name, default value, etc.
    ) -> EntityProperty<Entity, Value>

    public static func property(
        getter: KeyPath<Entity, Value>,
        // …
    ) -> EntityProperty<Entity, Value>

    public static func property(
        getSetter: WritableKeyPath<Entity, Value>,
        // …
    ) -> EntityProperty<Entity, Value>
    
    // ~12 overload combinations per (read-only, read-write, with/without default, with/without localized name, etc.)
}
```

Pattern multiplies across:
- Every public `AppEntity` conformer in the SDK (~30-50 conformers).
- Every property of every conformer that's marked `@Property` (the macro emits `EntityProperty` factory call invocations).

The 240 `WritableKeyPath` references come from this combinatorial expansion.

## Phase 8.1 — Enumerate AppEntity conformers

Run `swiftc -emit-symbol-graph-dir` against the AppIntents `xcframework`'s `.swiftinterface` to enumerate every public conformer of `AppEntity`. Expected list (partial):

- `AnyAppEntity`
- `IntentParameterMetadata`
- App-side entity types (sample apps; not in the SDK surface but emerge from C# consumer code)

In practice, the SDK exposes few *concrete* `AppEntity` conformers. The 240 figure comes from `EntityProperty.property(getter:)` being usable from C# user code where the *consumer* declares an `AppEntity` and binds properties. This means generator-side closed-conformer enumeration is incomplete — we need to support **open-conformer KeyPath emission** for `EntityProperty` factory methods, which is the v1-deferred case from `00-overview.md`.

**Pragmatic call:** v1 supports the closed-conformer case (whatever public `AppEntity` types Apple ships). The open-conformer case (user-defined `AppEntity` types in C#) is **out of scope for v1 in this session** — explicitly tracked as a follow-up since it requires C# user code to *originate* a KeyPath at runtime against a user-defined Swift type, which the typed-singleton design does not cover (constraint #36 from `00-overview.md`).

Decision: ship v1 with closed-conformer EntityProperty support + tombstones for `getter:` parameter when the Root is a user-defined C# `AppEntity` subclass. Add an explicit limitation note to the public wiki.

## Phase 8.2 — `EntityProperty` projection

For each public `AppEntity` conformer + each of its properties + each `EntityProperty` overload, emit:

- Swift trampoline (Session 4 machinery): `@_cdecl("SBW_KP_AppIntents_<Conformer>_<Property>")` returning the retained KeyPath.
- C# typed-singleton field: `public static readonly KeyPath<Conformer, ValueType> Property { get; }`.
- C# `EntityProperty<Conformer, ValueType>.Property(getter: …, …)` static-method binding that accepts the singleton (compile-time-checked via the C# generic constraint).

The `EntityProperty<Entity, Value>` struct itself binds as the generic struct from Session 3 (closed-generic emission via CSM). Verify it surfaces as a usable C# `struct` with all 12 overload variants.

## Phase 8.3 — `AppShortcutParameterPresentation`

Pre-image:

```swift
public protocol AppShortcutsProvider {
    @AppShortcutsBuilder
    @MainActor public static var appShortcuts: [AppShortcut] { get }
}

public struct AppShortcutParameterPresentation<Entity> where Entity : AppEntity {
    public init(summary: ...) { ... }
    public func keywordTitle(_ keyPath: KeyPath<Entity, String>, ...) -> Self
}
```

The `keywordTitle(_ keyPath:)` extension takes a closed-conformer KeyPath; for each public `AppEntity` conformer, emit a per-property `keywordTitle` overload that accepts the typed singleton.

## Phase 8.4 — `@Property` / `@Parameter` macro-emitted code

AppIntents heavily uses macros: `@Parameter`, `@Property`. The macro expansion at user-code time produces `EntityProperty` factory calls with KeyPath arguments. From the binding perspective:

- The macros themselves are not bindable (macros are compile-time-only in Swift).
- The *expanded* code is what binds. The expanded code calls `EntityProperty.property(…)` factories — Phase 8.2 covers this.

No additional work for macro support directly; the macro-expanded factory calls are covered by 8.2. **Verification**: pick one Apple-provided sample AppEntity (if any are in the SDK) and confirm its generated C# has the expected `EntityProperty` factory call wiring.

## Phase 8.5 — `PartialKeyPath<Entity>` in entity-descriptor storage

`EntityProperty` is generic over `Value`, but at storage-level (in entity-descriptor metadata) the framework heap-stores collections of mixed-`Value` properties. This uses `PartialKeyPath<Entity>` (type-erased over `Value`). The `PartialKeyPath` surface lands as part of Session 3 (already covered); this session must verify the *open conversion* — typed `KeyPath<Entity, Value>` ↦ `PartialKeyPath<Entity>` — works correctly at the C# call site.

In Swift this is implicit upcast. In C#, `KeyPath<Entity, Value>` must `: PartialKeyPath<Entity>` so the same singleton instance satisfies both parameter types. Session 3 establishes this inheritance; this session validates it on the real consumer.

## Phase 8.6 — BindingTests fixture

`BindingTests/Sources/SwiftBindingsTestLib/AppIntents/MockAppEntity.swift`:

```swift
import AppIntents

public struct MockBook: AppEntity {
    public typealias DefaultQuery = MockBookQuery
    public static var typeDisplayRepresentation = TypeDisplayRepresentation(name: "Book")
    public static var defaultQuery = MockBookQuery()

    public var id: String
    public var title: String
    public var pageCount: Int

    public init(id: String, title: String, pageCount: Int) {
        self.id = id; self.title = title; self.pageCount = pageCount
    }

    public var displayRepresentation: DisplayRepresentation {
        DisplayRepresentation(title: "\(title)")
    }
}

public struct MockBookQuery: EntityQuery { /* stub */ }
```

C# test (`BindingTests/RuntimeTestsApp/AppIntents/AppIntentsEntityPropertyTests.cs`):

- Construct `MockBook`, retrieve `MockBookKeyPaths.Title`, pass to `EntityProperty.property(getter: …, name: "Title")`, verify the resulting `EntityProperty<MockBook, String>` has the expected `name` and that calling its `getter` against a `MockBook` instance returns the title.
- Repeat for `PageCount` (Int — different value type).
- Verify `PartialKeyPath<MockBook>` upcast: pass `MockBookKeyPaths.Title` to a function typed `PartialKeyPath<MockBook>` and confirm it's accepted (compile-check + runtime correctness).

**Note**: AppIntents has system-integration dependencies (Shortcuts app, Spotlight) — the fixture cannot test full end-to-end Shortcuts dispatch. It tests the binding shape only. The full Apple-side integration falls under the `regression-validation` skill flow (Path B from Session 6).

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | Baseline |
| `nuke binding-tests --sim` | `AppIntentsEntityPropertyTests` passes |
| `nuke binding-tests --device` | Same |
| `nuke validate` | AppIntents library `cs_compile` count ratchets up substantially (240+ new bindings) |
| Symbol size check | AppIntents wrapper-lib binary not bloated past acceptable bound (TBD — `du -h` the .dylib pre/post) |

## Exit criteria

- All AppEntity conformer × property × EntityProperty-overload combinations emit and pass test.
- AppShortcutParameterPresentation `keywordTitle` overloads emit per-conformer.
- `PartialKeyPath<Entity>` upcast works at C# call sites.
- Open-conformer case (user-defined `AppEntity` types in C#) tombstoned with a public-wiki limitation note.
- BindingTests fixture passes sim + device.
- Wrapper-lib `.dylib` size for AppIntents not unreasonable; bake the cap into `validation-libraries.json` if there's a size-check entry.

## Risks specific to Session 8

- **Risk A (open-conformer `getter:` case)** — Apple's design assumes the C# (or Swift) consumer declares the `AppEntity` type. With the typed-singleton design (Session 4), C# *cannot* originate a KeyPath against a Swift type that doesn't exist at SDK build time. **Mitigation:** scope-out for v1; explicit user-facing limitation; track as Phase-3+ work (it requires either Swift-side dynamic KeyPath construction or a parallel approach via Mirror/string-paths). The decision must be documented in the wiki at session-completion time.
- **Risk B (240 trampoline symbol-table bloat)** — emitting 240 `@_cdecl` trampolines into one `AppIntents.Wrapper.dylib` may exceed acceptable binary size or linker symbol-table limits. **Diagnostic:** measure post-emission. **Mitigation:** if too large, batch trampolines into per-conformer wrapper libs (`AppIntents.MockBook.Wrapper.dylib`); this requires generator support for multi-output libs per source framework. Track as follow-up if needed.
- **Risk C (`@Property` macro divergence between SDK versions)** — Apple updates AppIntents macros between iOS releases; emitted code shape may shift. **Mitigation:** test against the current SDK at session time; track upstream Apple SDK changes via the regression-validation skill flow at every Xcode bump.
- **Risk D (`EntityProperty` factory has 12 overloads — overload disambiguation explosion)** — generator must produce 12 distinct C# `EntityProperty.property` overloads per `(Conformer × Property)` pair, all of which must be method-overload-disambiguatable (constraint #16). 240 × 12 = ~2880 overloads. **Diagnostic:** confirm no `DuplicateSignature` failures during emission.
- **Risk E (cross-module proxy: `AppEntity` itself lives in `AppIntents` but user-defined entities live in user app)** — same cross-module class qualification concern as Session 7 KVO bridging. Confirm closed-conformer emission works when the consumer C# project only imports `Swift.AppIntents`.
- **Risk F (Macro-emitted `@_dynamicReplacement` and `@_implementationOnly`)** — AppIntents macro expansions may include attributes that the binding generator currently strips or fails on. **Diagnostic:** sample one or two macro-expanded interface entries and verify parser handles them.
- **Risk G (`AppShortcutsProvider` `@AppShortcutsBuilder` result builder)** — the appShortcuts array uses a result-builder; binding result-builders is a separate axis of work and may surface tombstones independent of KeyPath. **Mitigation:** in scope only as far as it relates to AppShortcutParameterPresentation; full result-builder binding is a separate project.

## References

- `00-overview.md` (consumer surface table — AppIntents is largest)
- `03-keypath-foundation.md` (foundation types including `PartialKeyPath`)
- `04-typed-singleton-emission.md` (per-property trampoline emission)
- AppIntents `swiftinterface` — `EntityProperty`, `AppShortcutParameterPresentation`
- Apple docs: AppIntents framework, `AppEntity` protocol
- `.claude/rules/constraints.md` line 16 (overload disambiguation), line 29 (cross-module proxy)
