# Session 9 — SwiftUI + SwiftUICore productionization

Second-largest consumer of `KeyPath<…>` in the iOS SDK (319 lines across the two frameworks). Three architecturally distinct shapes: environment-modifier subscripts, `@dynamicMemberLookup` on `Binding` / `ObservedObject` (reference-writable, the only place this matters in production), and view-tree `id:` and `ForEach` identity selectors.

## Goal

Bind SwiftUI's KeyPath surface:

1. **`Binding<T>.subscript(dynamicMember:)`** and **`ObservedObject<T>.Wrapper.subscript(dynamicMember:)`** — `@dynamicMemberLookup` subscripts that take `ReferenceWritableKeyPath<T, U>` and project to a nested `Binding<U>` / writeable proxy. This is the only place in the SDK where `ReferenceWritableKeyPath` distinction matters at the API surface.
2. **`EnvironmentValues` `@dynamicMemberLookup`** — `KeyPath<EnvironmentValues, V>` subscript projection. Mirrors AttributedString's pattern from Session 7.
3. **View modifiers** — `id(_:)`, `tag(_:)`, `selection(_:by:)` and similar APIs that take `KeyPath<Item, ID>` as identity selectors.
4. **`Picker(selection:)` and friends** — same pattern as view modifiers but with `Binding` rather than `KeyPath`.

## Why this session

- SwiftUI is the showcase consumer for native Apple-platform Swift. KeyPath is structural to its `@dynamicMemberLookup` API design — without it, `Binding` and `ObservedObject` projections are unusable from C#.
- First time `ReferenceWritableKeyPath` matters at a real consumer surface. Exercises the C# inheritance hierarchy from Session 3 (`KeyPath` ← `WritableKeyPath` ← `ReferenceWritableKeyPath`).
- Closed-conformer enumeration for `Binding<T>.subscript(dynamicMember:)` is *open by definition* — `T` is whatever the consumer declares. This is the same open-conformer problem as Session 8 AppIntents, scoped to SwiftUI.

## Dependencies

- **Session 3** (KeyPath foundation, including `ReferenceWritableKeyPath`).
- **Session 4** (typed singleton emission).
- **Sessions 1, 2, 5** if any SwiftUI `View` is a PAT-constrained generic parent (uncommon but possible — verify).

## Pre-image surface

### `Binding<Value>` `@dynamicMemberLookup`

```swift
@frozen
@propertyWrapper
@dynamicMemberLookup
public struct Binding<Value> {
    public subscript<Subject>(dynamicMember keyPath: WritableKeyPath<Value, Subject>) -> Binding<Subject> { get }
    public subscript<Subject>(dynamicMember keyPath: KeyPath<Value, Subject>) -> Binding<Subject> { get }
    public subscript<Subject>(dynamicMember keyPath: ReferenceWritableKeyPath<Value, Subject>) -> Binding<Subject> { get }
}
```

This is the **central SwiftUI binding pattern**. A `Binding<User>` projects to a `Binding<User.Name>` via `\.name`, etc. The C# consumer expects to call:

```csharp
var nameBinding = userBinding[UserKeyPaths.Name];  // or .Name property accessor
```

### `EnvironmentValues` projection

```swift
public struct EnvironmentValues {
    public subscript<K>(key: K.Type) -> K.Value where K : EnvironmentKey
}

extension View {
    public func environment<V>(_ keyPath: WritableKeyPath<EnvironmentValues, V>, _ value: V) -> some View
}
```

`environment(_:_:)` takes a `WritableKeyPath<EnvironmentValues, V>` and is the canonical environment-modifier path.

### View modifiers with KeyPath

```swift
extension View {
    public func id<ID>(_ id: ID) -> some View where ID : Hashable  // not KeyPath
    public func tag<V>(_ tag: V) -> some View where V : Hashable    // not KeyPath
}

extension ForEach where Content : View, Data : RandomAccessCollection, ID : Hashable {
    public init(_ data: Data, id: KeyPath<Data.Element, ID>, @ViewBuilder content: ...)
}
```

`ForEach.init(_:id:)` is the most-used KeyPath-taking view-builder in the SDK.

### `Picker(selection:)`

```swift
extension Picker {
    public init<C, V>(
        selection: Binding<V>,
        @ViewBuilder content: ...
    )
}
```

Uses `Binding`, not KeyPath directly — covered by Phase 9.1 transitively.

## Phase 9.1 — `Binding<T>.@dynamicMemberLookup` projection

For each public consumer of `Binding<T>` (i.e. for each type `T` that an Apple SDK or user wants to wrap in `@Binding`):

- v1 closed-conformer set: every type Apple's frameworks publicly use with `Binding` (~50-100 unique conformers across SwiftUI sample apps + framework APIs).
- v1 emission: per-conformer `*KeyPaths` container (Session 4 pattern) for every stored property.
- For the C# subscript projection: emit `Binding<T>.this[KeyPath<T, U>]` indexer (read-only) and `Binding<T>.this[WritableKeyPath<T, U>]` indexer (read-write) and `Binding<T>.this[ReferenceWritableKeyPath<T, U>]` indexer.

The three subscript variants in the Swift source become three C# indexers differing in parameter type — overload-resolution must disambiguate by static argument type. The singleton's static type (KeyPath vs WritableKeyPath vs ReferenceWritableKeyPath) drives which subscript dispatches.

## Phase 9.2 — `EnvironmentValues` `@dynamicMemberLookup`

`EnvironmentValues` extension wall — every public `EnvironmentKey` conformer. Walk Apple's SDK to enumerate:

- Foundation-level keys: `colorScheme`, `locale`, `font`, `lineLimit`, etc.
- SwiftUI-specific keys: `isEnabled`, `presentationMode`, `editMode`, etc.
- UIKit-bridged keys (in extension scopes).

For each `EnvironmentKey`-conformer property, emit:

- Typed singleton: `EnvironmentValuesKeyPaths.ColorScheme : WritableKeyPath<EnvironmentValues, ColorScheme>`.
- C# `View.Environment` extension method binding the `environment(_:_:)` call.

This is the same shape as Session 7's AttributedString `@dynamicMemberLookup`. Code-share where possible.

## Phase 9.3 — `ForEach.init(_:id:)` and view-builder consumers

`ForEach.init(_:id:)` is the main consumer of `KeyPath<Data.Element, ID>`. The C# call site:

```csharp
ForEach(users, UserKeyPaths.Id, user => Text(user.name))
```

This requires that the `ID` parameter `KeyPath` has been constructed via a typed singleton, which Session 4 covers.

Confirm: `ForEach.init(_:id:)` emits via CSM (generic struct with two generic params Data, ID) — closed-substitution happens per-call-site by the C# generic-call-site type inference.

## Phase 9.4 — `ReferenceWritableKeyPath` distinction matters

`ObservedObject<T>.Wrapper.subscript(dynamicMember:)` uses `ReferenceWritableKeyPath<T, U>`. The distinction matters: `T` is a class (`ObservableObject` requires `AnyObject` constraint), and a `WritableKeyPath` is *insufficient* to project a mutating proxy on a class — `WritableKeyPath` produces a *copy* setter; `ReferenceWritableKeyPath` produces a *property-on-the-existing-object* setter. The Swift compiler enforces this distinction; the C# binding must too.

Per Session 3's type hierarchy:
- `ReferenceWritableKeyPath<T, U> : WritableKeyPath<T, U>` — accepts where parent expected.
- C# overload resolution prefers `ReferenceWritableKeyPath` parameter over `WritableKeyPath` when both are candidate subscripts and the singleton's static type is `ReferenceWritableKeyPath`.
- The singleton emission (Session 4) must distinguish the static type per the Swift compiler's resolution rules: `\ClassType.prop` for `var` on class ↦ `ReferenceWritableKeyPath`, `\StructType.prop` for `var` on struct ↦ `WritableKeyPath`, `\Anything.prop` for `let` ↦ `KeyPath`.

**Verification**: the trampoline emitter (Session 4) must inspect the property's owning type (class vs struct) and the property's mutability (var vs let) to emit the correct singleton field type. Adding this to Session 4's predicate is a Session-4 concern; verify in this session that the emitted C# types are correct.

## Phase 9.5 — Closed-conformer enumeration for `Binding<T>`

Pragmatic enumeration:

- Walk every public `View` conformer in SwiftUI's swiftinterface (~hundreds).
- Walk every public `ObservableObject` conformer (fewer; mainly `@Published` properties).
- Cross-reference with usage sites: any `Binding<T>` parameter where the consumer would idiomatically chain `.subscript(dynamicMember:)`.

The v1 scope: emit the per-conformer `*KeyPaths` containers for every type that's *publicly bindable in SwiftUI* — leaving user-defined types out of scope (same as Session 8's user-defined-`AppEntity` constraint).

Open-conformer follow-up: user-defined types in C# consumer code can't yet bind. **Mitigation:** v2 work; document explicitly.

## Phase 9.6 — BindingTests fixture

`BindingTests/Sources/SwiftBindingsTestLib/SwiftUI/MockBinding.swift`:

```swift
import SwiftUI

public struct MockUser {
    public var name: String
    public var age: Int
}

public class MockObservable: ObservableObject {
    @Published public var greeting: String = "hi"
}

public func makeBinding() -> Binding<MockUser> { /* stub */ }
```

C# test: construct `Binding<MockUser>`, project `nameBinding = binding[MockUserKeyPaths.Name]`, verify the resulting `Binding<String>` is functional. Repeat for `MockObservable` projecting via `ReferenceWritableKeyPath<MockObservable, String>` to confirm the inheritance distinction works.

`BindingTests/Sources/SwiftBindingsTestLib/SwiftUI/MockEnvironment.swift`:

```swift
import SwiftUI

struct MockEnvironmentKey: EnvironmentKey {
    static var defaultValue: String = "default"
}

extension EnvironmentValues {
    public var mockKey: String {
        get { self[MockEnvironmentKey.self] }
        set { self[MockEnvironmentKey.self] = newValue }
    }
}
```

C# test: call `view.Environment(EnvironmentValuesKeyPaths.MockKey, "hello")`, verify the value propagates through the view tree (assertable via SwiftUI's environment APIs or via a mock view that reads it back).

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | Baseline |
| `nuke binding-tests --sim` | `MockBinding` + `MockEnvironment` fixtures pass |
| `nuke binding-tests --device` | Same |
| `nuke validate` | SwiftUI + SwiftUICore `cs_compile` count ratchets up substantially |
| Inspect generated `Binding<T>.this[…]` indexers | Three overloads per conformer present (KeyPath, WritableKeyPath, ReferenceWritableKeyPath) |

## Exit criteria

- `Binding<T>.@dynamicMemberLookup` projects correctly for every closed conformer (KeyPath, WritableKeyPath, ReferenceWritableKeyPath variants).
- `EnvironmentValues` projection works for every public `EnvironmentKey` conformer.
- `ForEach(_:id:)` accepts typed `KeyPath` singletons.
- BindingTests fixture passes sim + device.
- Open-conformer case (user-defined types in C#) documented as v2 work in wiki.

## Risks specific to Session 9

- **Risk A (`ReferenceWritableKeyPath` projection accuracy)** — Session 4's trampoline emitter must correctly distinguish `ReferenceWritableKeyPath` for class-property `var`s vs `WritableKeyPath` for struct-property `var`s. If this distinction is wrong, `Binding<ObservedObject.Wrapper>` projection produces silent copy-write semantics that lose the mutation. **Diagnostic:** test asserts the round-trip through `@Published` actually publishes (rather than silently no-ops).
- **Risk B (`@dynamicMemberLookup` subscript-overload resolution)** — three subscript overloads on `Binding<T>` (KeyPath / WritableKeyPath / ReferenceWritableKeyPath) must dispatch correctly based on the singleton's static type. C# overload resolution may pick the wrong one if the singletons share a base type and inheritance isn't structurally exact. **Mitigation:** singletons emit with their *most-derived* declared type; verify generated `.cs` shows `static readonly WritableKeyPath<…>` (not `KeyPath<…>`) for `WritableKeyPath`-rooted properties.
- **Risk C (SwiftUI is two frameworks `SwiftUI` + `SwiftUICore`)** — they share types; cross-module proxy class qualification (constraint #29) must produce extension methods reachable from C# code that only imports one or the other. **Diagnostic:** test importing only `Swift.SwiftUICore` and verify `Binding<MockUser>` projects work without `Swift.SwiftUI`.
- **Risk D (`@frozen Binding<Value>`)** — `Binding<Value>` is `@frozen`. Generator must handle this correctly (frozen value-type, single-pointer-tag representation). Check the existing frozen-struct handling — should already work, but verify.
- **Risk E (Open-conformer expectation gap)** — SwiftUI consumers strongly expect to bind their own `ObservableObject` types from C# and project `Binding`s through them. Closed-conformer scope is a real limitation. **Mitigation:** explicit wiki documentation; track open-conformer support as a numbered follow-up (likely Session 9.5 / "Open KeyPath construction in C#").
- **Risk F (Result-builder `@ViewBuilder` integration)** — `ForEach(_:id:content:)` uses `@ViewBuilder` for its `content` parameter. This is a separate axis of work (result builders); if not in place when Session 9 lands, the `ForEach` binding emits but the `content:` closure cannot construct views. **Mitigation:** scope-check at session time; if result-builder support not landed, defer `ForEach` to a follow-up.
- **Risk G (`@MainActor` annotations on SwiftUI extensions)** — most SwiftUI extension methods are `@MainActor`. Actor-isolation marshalling must continue to work for these (already handled by the actor subsystem). Verify in this session — adding KeyPath shouldn't disturb it.

## References

- `00-overview.md` (consumer surface, SwiftUI second-largest)
- `03-keypath-foundation.md` (foundation types including `ReferenceWritableKeyPath` distinction)
- `04-typed-singleton-emission.md` (per-property trampoline emission, including `ReferenceWritableKeyPath` for class properties)
- SwiftUI + SwiftUICore `swiftinterface`
- Apple docs: `Binding`, `ObservedObject`, `EnvironmentValues`, `@dynamicMemberLookup`
- `.claude/rules/constraints.md` line 29 (cross-module proxy)
