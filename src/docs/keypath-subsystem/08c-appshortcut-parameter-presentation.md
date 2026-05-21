# Session 8c — `AppShortcutParameterPresentation` higher-kinded `ParameterKeyPath`

Bind AppIntents' `AppShortcutParameterPresentation` family, all of which are parameterized by a **higher-kinded KeyPath generic param** — a generic-param-is-a-KeyPath-type constraint shape no existing emitter handles.

Depends on: Session 3 (KeyPath foundation), Session 4 (typed singleton emission), Session 8b (closed-`AppEntity` conformer enumeration may overlap with closed-`AppIntent` conformer enumeration; check at session time).

## Real API shape (verified against iOS 26.2 `AppIntents.swiftinterface`)

```swift
public struct AppShortcutParameterPresentation<Intent, Value, Parameter, ParameterKeyPath>
  where Intent : AppIntent,
        Value : _IntentValue,
        Value : Sendable,
        Parameter : IntentParameter<Value>,
        ParameterKeyPath : Swift.KeyPath<Intent, Parameter>  // higher-kinded
{
  public init(for keyPath: ParameterKeyPath,
              summary: AppShortcutParameterPresentationSummary<Intent, Value, Parameter, ParameterKeyPath>,
              @AppShortcutOptionsCollectionSpecificationBuilder<Value.UnwrappedType>
                  optionsCollections: () -> some AppShortcutOptionsCollectionSpecification<Value.UnwrappedType>)

  public typealias ParameterPresentation = AppShortcutParameterPresentation
}

// Plus parallel structs with identical generic-param signatures:
public struct AppShortcutParameterPresentationTitle<Intent, Value, Parameter, ParameterKeyPath> { … }
public struct AppShortcutParameterPresentationTitleString<Intent, Value, Parameter, ParameterKeyPath> { … }
public struct AppShortcutParameterPresentationSummary<Intent, Value, Parameter, ParameterKeyPath> { … }
public struct AppShortcutParameterPresentationSummaryString<Intent, Value, Parameter, ParameterKeyPath> { … }
```

Plus the `AppShortcut` factory:
```swift
public init<Intent, Value, Parameter, ParameterKeyPath>(
  intent: Intent,
  phrases: [AppShortcutPhrase<Intent>],
  shortTitle: LocalizedStringResource,
  systemImageName: _const String,
  parameterPresentation: AppShortcutParameterPresentation<Intent, Value, Parameter, ParameterKeyPath>
) where Intent : AppIntent, Value : _IntentValue, Value : Sendable,
        Parameter : IntentParameter<Value>, ParameterKeyPath : Swift.KeyPath<Intent, Parameter>
```

## Why this is a new emitter shape

The constraint `ParameterKeyPath : Swift.KeyPath<Intent, Parameter>` is **higher-kinded**: `ParameterKeyPath` is itself bound to be (a subtype of) `KeyPath<Intent, Parameter>`. Session 4's typed-singleton emitter handles "a method parameter typed `KeyPath<X, Y>` where X is a closed conformer of some protocol bag." It does NOT handle "a generic parameter constrained to be a KeyPath type" — there's no existing precedent for emitting a closed C# struct where one of the closed generic parameters is itself a typed KeyPath singleton's type.

The closed instantiation a C# consumer would write looks like (with Swift `Int` projected to C# `nint`):
```csharp
new AppShortcutParameterPresentation<MyIntent, nint, IntentParameter<nint>,
                                     KeyPath<MyIntent, IntentParameter<nint>>>(
    for: MyIntentKeyPaths.PageCountParameter,
    summary: …,
    optionsCollections: () => …);
```
The fourth generic param is the *type* of the typed-singleton, not the singleton's value. C# generics admit this — you can declare a type parameter that is itself a constructed generic — but the emitter must produce a closed C# struct generic shape that satisfies the higher-kinded `: KeyPath<Intent, Parameter>` constraint in a way the C# compiler will accept.

## Generator pieces required

### `AppIntent` closed-conformer enumeration

Mirrors `AppEntity` enumeration from Session 8b. The `AppIntent` protocol has its own conformer set (apps' user-declared intents in the validation-libraries entries; zero in the iOS SDK base layer; one or more in BindingTests if we add a `MockAppIntent` fixture).

### `IntentParameter<Value>` closed-conformer enumeration

For each closed `AppIntent` conformer × each `IntentParameter<X>`-typed stored property of that intent, the corresponding `(Intent, Value, Parameter, ParameterKeyPath)` tuple is closed. Enumerate by walking each `AppIntent` conformer's storage properties whose declared type matches `IntentParameter<X>` (likely `@Parameter`-macro-expanded). Each such `(Intent, Property)` gives one closed `Value = X` and `Parameter = IntentParameter<X>`.

### Higher-kinded `ParameterKeyPath` substitution

The fourth generic param `ParameterKeyPath` is `KeyPath<Intent, Parameter>` (or a subtype). At closed-conformer emission time, substitute it with the concrete closed type `KeyPath<ClosedIntent, IntentParameter<ClosedValue>>`. The C# type expression becomes `global::Swift.KeyPath<ClosedIntent, global::AppIntents.IntentParameter<ClosedValue>>`.

The closed `AppShortcutParameterPresentation<…>` struct emission then looks like Session 4-style per-instantiation emission, except the generic-param substitution is driven by the higher-kinded constraint rather than by a parent's associated-type bag.

### Per-tuple closed-struct emission

For each `(Intent, Value, Parameter, ParameterKeyPath)` tuple emitted by the enumeration above, emit:
- A closed `AppShortcutParameterPresentation_{IntentSan}_{ValueSan}` C# struct that internalizes the four-generic substitution. Same for the four parallel structs (`Title`, `TitleString`, `Summary`, `SummaryString`).
- Per-conformer constructors and methods that mirror the Swift extension methods on each parallel struct.
- The `AppShortcut.init<Intent, Value, Parameter, ParameterKeyPath>(…)` factory takes a closed `AppShortcutParameterPresentation<…>` — emit per-tuple `AppShortcut` constructor overloads similarly.

### Trampoline emission

The Swift trampoline for the `AppShortcutParameterPresentation` init wraps the underlying Swift initializer. Symbol scheme: `SBW_APP_AppShortcutPP_{IntentSan}_{ValueSan}_{hash8}`.

## Phase 8c.1 — `AppIntent` conformer enumeration

Reuse Session 8b's `ConcreteSpecializationEngine.GetConformers` extension; specialize to `AppIntents.AppIntent`. Inventory: BindingTests adds a `MockAppIntent` fixture, validation-libraries entries declare zero or more.

## Phase 8c.2 — `IntentParameter<X>` property walking per intent

For each closed `AppIntent`, walk its declared `@Parameter` storage properties (or properties whose type matches `IntentParameter<X>`). The macro-expanded form binds these as `let pageCount: IntentParameter<Int>` in Swift (the `@Parameter` macro expansion produces a property of `IntentParameter<X>` type, where `X` is the closed parameter value type — `Swift.Int`, `Swift.String`, etc.).

### `MockAppIntent` BindingTests fixture (proposed)

```swift
@available(iOS 16, macOS 13, watchOS 9, tvOS 16, *)
public struct MockBookLookupIntent : AppIntent {
  public static var title: LocalizedStringResource = "Look up book"
  @Parameter(title: "Book") public var book: MockBook
  public func perform() async throws -> some IntentResult { /* … */ }
  public init() {}
}
```

The macro expansion synthesizes `let book: IntentParameter<MockBook>` (or similar — verify against the macro expansion in a sample app). This closed `(MockBookLookupIntent, MockBook, IntentParameter<MockBook>, KeyPath<MockBookLookupIntent, IntentParameter<MockBook>>)` tuple is what the closed C# struct emission targets.

## Phase 8c.3 — Closed-struct emission for the five parallel structs

For each closed tuple from 8c.2, emit:
- `MockBookLookupIntentMockBookAppShortcutPresentation` (the closed `AppShortcutParameterPresentation`)
- `MockBookLookupIntentMockBookAppShortcutPresentationTitle`
- `MockBookLookupIntentMockBookAppShortcutPresentationTitleString`
- `MockBookLookupIntentMockBookAppShortcutPresentationSummary`
- `MockBookLookupIntentMockBookAppShortcutPresentationSummaryString`

Naming: `{IntentSan}{ValueSan}{ShortName}`. Each closed struct carries the underlying opaque payload of the corresponding Swift struct (4-pointer or similar; verify SIL ABI before designing the C# layout).

## Phase 8c.4 — `AppShortcut` factory closed overloads

For each closed tuple, emit a per-tuple `AppShortcut` constructor overload that takes the closed `AppShortcutParameterPresentation` as the `parameterPresentation:` parameter. The closed-Intent + closed-Value substitution drives overload disambiguation.

## Phase 8c.5 — BindingTests fixture

`BindingTests/Sources/SwiftBindingsTestLib/AppIntents/MockAppShortcut.swift`:
- `MockBookLookupIntent : AppIntent` with `@Parameter` for `MockBook`.
- Conformance to `AppShortcutsProvider` with a single `appShortcut` declaration.

`BindingTests/RuntimeTestsApp/AppIntents/AppShortcutParameterPresentationTests.cs`:
- Construct the closed `AppShortcutParameterPresentation_…` from C# using the typed singleton for `MockBookLookupIntent.book`.
- Construct `AppShortcut` with the closed `parameterPresentation`.
- Verify the shortcut shape round-trips through whatever public read paths AppIntents exposes (likely limited at the binding level; full integration test requires the Shortcuts app and is out of scope — covered by the regression-validation skill flow at session-completion time).

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | Baseline + unit coverage for the new emitter |
| `nuke binding-tests --sim` | New `AppShortcutParameterPresentationTests` cells pass |
| `nuke binding-tests --device` | Same |
| `nuke validate` (opt-in) | AppIntents `cs_compile` ratchets up further beyond 8b's contribution |

## Exit criteria

- For every closed `(AppIntent, IntentParameter<X>)` pair × each of the five parallel structs in the `AppShortcutParameterPresentation` family: a closed C# struct emits.
- `AppShortcut.init<…>(parameterPresentation:)` has a closed-overload form for each tuple.
- BindingTests fixture passes sim + device.

## Risks

- **Higher-kinded constraint emission to C#.** C# generics allow type parameters constrained to a concrete generic instantiation (`where T : KeyPath<I, P>`), but the closed-conformer emitter must substitute *the entire fourth generic param* with the concrete closed type — not emit it as a C# generic param. Confirm the C# generic shape before designing emission.
- **Closed-Intent set is sparse in our test corpus.** Validation-libraries entries that adopt `AppIntent` are zero or very few. Most of the work for v1 is exercising the emitter against `MockBookLookupIntent` only. Full breadth comes when downstream apps are bound.
- **`@Parameter` macro expansion** — the `book: MockBook` declaration becomes `let book: IntentParameter<MockBook>` post-macro. Confirm the binding generator sees the post-expansion shape (it should — the swiftinterface is post-macro-expansion).
- **`@AppShortcutOptionsCollectionSpecificationBuilder` result builder** — the `optionsCollections:` parameter on `AppShortcutParameterPresentation.init` is a result-builder closure. Binding result-builder closures from C# is a separate axis of work; for v1 of this session, defer to the existing closure machinery and accept whatever loss-of-DSL-ergonomics that produces.

## References

- `04-typed-singleton-emission.md` — typed singleton machinery
- `08b-entityproperty-init-keypath.md` — sibling follow-up; conformer enumeration may be shared
- `AppIntents.swiftinterface` lines 486, 493, 909, 8889, 8895, 9645 (the five parallel structs + AppShortcut factory)
