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

## Phase 0 spike result (2026-05-21)

**Verdict: blocked upstream of 8c. The generic-shape spike could not be evaluated because the dependent types are not emitted at all.**

Spike procedure: add AppIntents as an apple-framework target in `validation-libraries.json` (filter run, baseline NOT updated), regenerate, read `IntentParameter<TValue>` and `AppShortcutParameterPresentation` in the generated `AppIntents.cs`.

Findings:
- `IntentParameter<Value>` — suppressed. Skip reason recorded inline in the generated source:
  > `// Unsupported: type 'IntentParameter' — IndeterminatePwtShape (TValue: AppIntents._IntentValue (protocol not projected in the type database))`
- `EntityProperty<Value>` — suppressed for the same reason (`AppIntents._IntentValue` not projected).
- `AppShortcutParameterPresentation` — not emitted at all (no `// Unsupported:` line either; the four-generic parameter pack with `Parameter : IntentParameter<Value>` is filtered upstream of the IndeterminatePwtShape gate).
- ~12 other types in `AppIntents.cs` carry the identical `_IntentValue (protocol not projected)` suppression message (e.g. `ParameterSummarySwitchCondition`, `ParameterSummaryCaseCondition`, `AppShortcutOptionsCollectionSpecificationBuilder`).
- `IAppIntent<TSelf>` and `IAppEntity<TSelf>` C# interfaces ARE emitted — the protocols themselves project; the missing piece is the underscored marker protocol they constrain their `Value` associated types against.

Root cause (one level deeper): `AppIntents._IntentValue` is the load-bearing constraint across every PAT-shaped type that exposes a typed `Value`. It's declared as `@_alwaysEmitConformanceMetadata public protocol _IntentValue { … }` in the swiftinterface at line 3075 — an underscored "SPI" protocol that the parser's underscore-suppression rules drop (see `SwiftABIParser.cs:1811`, `:2318`). The public sibling `AnyIntentValue` (line 217 of the swiftinterface) is a different protocol with `associatedtype Value : _IntentValue, Sendable`, and IS in the type database, but it is not the constraint used by `IntentParameter<Value>` / `EntityProperty<Value>` / `AppShortcutParameterPresentation`.

Implications:
- The plan's spike question — does `where TParameter : IntentParameter<TValue>` compose against the generated `IntentParameter<T>` — cannot be answered against current generator output because no such generated type exists.
- 8c is blocked.
- 8b is blocked at the same layer (`EntityProperty<Value>` is not emitted, so no convenience-init post-processor can target it).
- 8d (CoreSpotlight) is **not** affected — `CSSearchableItemAttributeSet` doesn't depend on `_IntentValue`.

Prerequisite for 8b/8c (call it **Session 8a-prereq**): project `AppIntents._IntentValue` (and the same-shape `_ParameterSummarySwitchCase` referenced by `ParameterSummarySwitchCondition`) as a PAT-shaped protocol the type database can store. Two viable approaches, neither costless:

1. **Lift underscore-suppression for keep-listed protocols.** Add `AppIntents._IntentValue` to a "do not suppress despite underscore prefix" allowlist (analogous to the keep-list mechanism described in `SwiftInterfaceFacts.cs:55`). The parser would then admit `_IntentValue` as a regular protocol declaration. Risk: `_IntentValue` is a Self-requirement PAT (`associatedtype ValueType : _IntentValue = Self`, plus a static `Specification : ResolverSpecification` requirement). Even projected, it would be `HasAssociatedTypes + HasSelfRequirement`, which the existential gate (`PInvokeHelperEmitter.cs:301-303`) rejects. The path that *does* unblock emission is the Route C / CSM machinery — closed conformers of a PAT generic parent — which is exactly what Sessions 1–7 built. So this path leans into the existing architecture: emit `IntentParameter<Value>` as a PAT-generic-parent type whose closed conformers are enumerated via the `Specialization` machinery against known `_IntentValue` conformers (`Swift.Int`, `Swift.String`, `Foundation.AttributedString`, `AppIntents.IntentCurrencyAmount`, `AppIntents.EntityIdentifier`, `AppIntents.IntentFile`, plus user-declared `AppEntity`s).
2. **Don't lift suppression; remap `_IntentValue` → `AnyIntentValue` in a manual XML database entry.** Reject. The two protocols are structurally different (`AnyIntentValue` has its own `Value` associated type that's itself constrained on `_IntentValue`); collapsing them would produce wrong generic substitutions and silent ABI mismatches against the Swift metadata accessors.

Path (1) is the right architectural fit. Estimated scope: introduce a parser-side keep-list entry for `AppIntents._IntentValue` and `AppIntents._ParameterSummarySwitchCase`; verify the closed-conformer enumeration finds the SDK-shipped `_IntentValue` conformers via `IndexModuleConformances` (the swiftinterface lists 8+ extension conformances at lines 189, 899, 2197, 2294, 2341, plus user `AppEntity` conformers); confirm `IntentParameter<Value>` then emits via the existing CSM path. This is its own session (call it **Session 8a-prereq** or **Session 9-IntentValue-projection**) and gates 8b/8c.

Re-spike point: once `_IntentValue` projection lands, re-run the apple-framework AppIntents regen, confirm `class IntentParameter<TValue>` emits as a C# class with SafeHandle (reference-shaped), and **then** run the original spike fixture:
```csharp
class P<TIntent, TValue, TParameter, TParameterKeyPath>
    where TIntent : IAppIntent
    where TParameter : IntentParameter<TValue>
    where TParameterKeyPath : KeyPath<TIntent, TParameter> {}
```

Until the prerequisite is decided, the rest of this document describes the *intended* 8c emission shape post-unblock — not actionable code-wise today.

---

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

## v1 limitations (cross-module conformer enumeration)

Same constraint as `08b`. `ConcreteSpecializationEngine.GetConformers("AppIntents.AppIntent")` enumerates only AppIntent conformers visible in the bound module or its dependent-bound modules. Apple-shipped `AppIntent` conformers in sibling frameworks and consumer-app-defined `AppIntent` types in a separate assembly are not visible to closed-overload emission. Documented as wiki Known Limitation; roadmap entry tracks cross-assembly conformer enumeration as a dedicated future session. See `08b-entityproperty-init-keypath.md` v1-limitations section for the full statement.

## References

- `04-typed-singleton-emission.md` — typed singleton machinery
- `08b-entityproperty-init-keypath.md` — sibling follow-up; conformer enumeration may be shared
- `AppIntents.swiftinterface` lines 486, 493, 909, 8889, 8895, 9645 (the five parallel structs + AppShortcut factory)
