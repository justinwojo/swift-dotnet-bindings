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

### Prerequisite shipped: `UnderscoreProtocolSynthesizer`

The spike root-cause description above (lines 99, 109) attributed the omission to the parser's underscore-suppression at `SwiftABIParser.cs:1811`/`:2318`. That was incomplete. Verified mechanism: `swift-api-digester` (`-dump-sdk -abi`) emits **zero** `declKind=Protocol` nodes for underscore-prefixed protocols regardless of `public` access, while leaving references (conformance lists, mangled-name fragments, conformance records) intact. The protocol declaration never reaches our parser, so any parser-side keep-list / suppression-lift is a no-op for this case.

Resolution: the C#-side `UnderscoreProtocolSynthesizer` (`src/Swift.Bindings/src/Parser/UnderscoreProtocolSynthesizer.cs`) reads the swiftinterface directly for an allowlisted set of (module, name) pairs (currently `AppIntents._IntentValue` and `AppIntents._ParameterSummarySwitchCase`) and injects a synthetic `ProtocolDecl` with the correct mangled name (`$s10AppIntents12_IntentValueP`), associated-type list, Self-requirement flag, and inheritance, into `moduleDecl.Protocols` + `_moduleTypes` before `ModuleProcessor.RegisterProtocolType` runs. The synthesized decl is `IsModuleInternal=true` so no public `I_IntentValue` C# interface is emitted from the empty body — only the TypeRecord is needed for the PAT branch in `PInvokeHelperEmitter.cs:370-388` to resolve the descriptor symbol. Unit coverage: `tests/UnitTests/ParserTests/UnderscoreProtocolSynthesizerTests.cs` (12 tests). 8b/8c can proceed against the projected `IntentParameter<Value>` / `EntityProperty<Value>` types via the existing CSM machinery.

Downstream gate: once the synthesizer is consumed against real AppIntents (via `swift-dotnet-packages`), the previously-tombstoned types will reach emitter for the first time and may surface new emitter bugs. See `08b-entityproperty-init-keypath.md` "Predicted downstream emitter surface" for the catalogue of predicted categories, and `roadmap.md` → "AppIntents downstream emitter bugs" for the validation-libraries.json gate.

### Phase 0 spike re-run (2026-05-23 — synthesizer-projected `IntentParameter<TValue>`)

Regen procedure: `nuke pack --version 0.12.0 --skip-apple` against this worktree (head `8ead3167`) → drop `SwiftBindings.Sdk.0.12.0.nupkg` + `SwiftBindings.Runtime.0.12.0.nupkg` into `/Users/wojo/Dev/swift-dotnet-packages/local-packages/` → wipe `~/.nuget/packages/swiftbindings.{sdk,runtime}/0.12.0/` → `dotnet build -c Release` on `apple-frameworks/AppIntents/SwiftBindings.Apple.AppIntents.csproj`. macOS and Mac Catalyst targets produced the managed `SwiftBindings.Apple.AppIntents.dll`; iOS and tvOS targets failed at the swiftc wrapper-Swift compile step on the known async/throws cdecl-wrapper categories (doc 14 § "Wrapper-compile failures" items 4 + 5, declared out-of-scope per this doc's parent — *not* a regression).

**Verdict: the four-generic-param shape with higher-kinded `ParameterKeyPath : KeyPath<TIntent, TParameter>` is admitted by the C# compiler, with two adjustments to the verbatim shape proposed in the original spike (lines 116–120 above).**

Spike workspace: `/tmp/8c-spike/Spike.{csproj,cs}` (throwaway; references the regen's `obj/Release/net10.0-macos26.2/SwiftBindings.Apple.AppIntents.dll`). All five variants below compiled cleanly (`0 Warning(s), 0 Error(s)`).

Adjustments to the original proposed shape:

1. `IAppIntent` is emitted as the F-bounded form `IAppIntent<TSelf> where TSelf : IAppIntent<TSelf>` (regen line 3418, macOS slice). The spike constraint must read `TIntent : IAppIntent<TIntent>`, not the unparameterized `IAppIntent`.
2. `IntentParameter<TValue>` carries the fallback constraint `where TValue : ISwiftObject` (regen line 5109). The synthesizer is `IsModuleInternal=true` so no public `I_IntentValue` C# interface is emitted from the synthetic protocol body; `ISwiftObject` is what the generator falls back to as the most-specific projected bound. The spike `TValue` must therefore be constrained `TValue : class, ISwiftObject` to satisfy `IntentParameter<TValue>`.

Compiled variants (`/tmp/8c-spike/Spike.cs`):

```csharp
// V2 — open four-generic with all constraints
public class V2_FBoundedIntent<TIntent, TValue, TParameter, TParameterKeyPath>
    where TIntent : class, IAppIntent<TIntent>
    where TValue : class, ISwiftObject
    where TParameter : IntentParameter<TValue>
    where TParameterKeyPath : KeyPath<TIntent, TParameter> { }

// V3 — same with a constructor consuming TParameterKeyPath (the actual
// AppShortcutParameterPresentation.init(for:…) signature shape)
public class V3_ConstructorShape<TIntent, TValue, TParameter, TParameterKeyPath>
    where TIntent : class, IAppIntent<TIntent>
    where TValue : class, ISwiftObject
    where TParameter : IntentParameter<TValue>
    where TParameterKeyPath : KeyPath<TIntent, TParameter>
{
    public V3_ConstructorShape(TParameterKeyPath forKeyPath) { _ = forKeyPath; }
}

// V4 — closed substitution against a reference-typed _IntentValue conformer
// (AppIntents.EntityIdentifier is a `partial class : ISwiftObject` in the regen).
public static class V4_ClosedSubstitution {
    public static void Accept(KeyPath<MockIntent, IntentParameter<EntityIdentifier>> forKeyPath) { }
}

// V5 — call-site emission of a closed AppShortcutParameterPresentation.init,
// shape equivalent to what the per-tuple emitter would produce.
public static class V5_CallSite {
    public static void Construct<TIntent, TValue, TParameter, TParameterKeyPath>(TParameterKeyPath forKeyPath)
        where TIntent : class, IAppIntent<TIntent>
        where TValue : class, ISwiftObject
        where TParameter : IntentParameter<TValue>
        where TParameterKeyPath : KeyPath<TIntent, TParameter> { }
}
```

#### Caveat: primitive `_IntentValue` conformers do not close against `IntentParameter<TValue>`

`IntentParameter<TValue> where TValue : ISwiftObject` rejects C# primitive projections of value-type `_IntentValue` conformers. Specifically: Swift declares `extension Swift.Int : AppIntents._IntentValue {}` (swiftinterface line 2197), but `Swift.Int` projects to C# `nint`, and `nint` does not implement `ISwiftObject`. So `IntentParameter<nint>` does not type-check on the C# side. The same applies to every `_IntentValue` conformer whose C# projection is a primitive: `Swift.Bool → bool`, `Swift.Double → double`, `Swift.String → string`. Only reference-typed conformers (`AppIntents.EntityIdentifier`, `AppIntents.IntentFile`, `AppIntents.IntentCurrencyAmount`, `Foundation.AttributedString` if/when it lands as a SafeHandle-backed C# class, plus user-declared `AppEntity` types) compose against the synthesizer's fallback `ISwiftObject` bound.

The per-tuple emitter for 8c must therefore either (a) decline to emit closed `AppShortcutParameterPresentation<…>` overloads where Value is a primitive _IntentValue conformer, with a tombstone, or (b) lift `IntentParameter<TValue>`'s C# constraint to allow primitive TValues — e.g. by changing the synthesizer to project `_IntentValue` as a TypeRecord without the `ISwiftObject` fallback constraint surfacing on dependent generic types. Path (b) is a larger redesign and probably bleeds into the same change that lets the type database see Swift.Int as an `_IntentValue` conformer (see the CSM conformance gap below). Recommend revisiting this together with the conformance ingestion gap rather than as a tactical patch.

**Resolved — 8c Phase A + B (shipped, uncommitted in `keypath-worktree`).** Took path (b), in two parts:

- **Phase A — seed relaxation (`GenericTypeEmitter.GetWhereClause`).** The emitter previously seeded an `ISwiftObject` bound on every generic param that had *any* protocol conformance but no concrete constraint, which is what forced `IntentParameter<TValue>` to require `TValue : ISwiftObject` and excluded primitives. The seed is now dropped *only* when every filtered conformance on the param is **descriptor-path-safe** (PAT / Self-requirement / method-Self — the shapes that resolve through the unconstrained `TypeMetadata.GetTypeMetadataOrThrow<T>()` descriptor-symbol path rather than the `ProtocolWitnessTable.GetOrThrowAuto<T, IFoo>()` resolvable-interface path that genuinely needs `ISwiftObject`) **and** no *conservative* filter fired (unsupported-module, empty-marker, cross-module-unregistered, well-known-runtime). When a conservative filter is mixed in, the seed is kept — fail-closed. `IntentParameter<TValue>` whose only constraint is the synthesized `_IntentValue` PAT now type-checks for primitive and frozen-value TValues. Covered by `GenericTypeEmitterTests` descriptor-path-safe seed-drop tests.

- **Phase B — conformance visibility (`UnderscoreProtocolSynthesizer` + `BoundGenericsHandler`).** Relaxing the C# constraint is necessary but not sufficient: `BoundGenericsHandler.SatisfiesConstraint` still has to *believe* the closed type argument conforms, or it skips the binding. The digester strips the protocol decl and its conformance records together, so the synthesizer now re-attaches the stripped records in the same pass. **Local** conformers (reference- *or* frozen-value-typed — the old frozen exclusion is gone) get a `TypeConformance` with an empty descriptor appended to their decl; this persists across modules via `TypeRecord.ProtocolConformances`. **Foreign** conformers (`Swift.Int`, `Foundation.Date`, …) have no local decl, so their `(concrete, protocol)` fact is registered on `ITypeDatabase.RegisterStrippedConformance`, which `SatisfiesConstraint` consults in its `typeArgumentDecl == null` branch.

  **Persistence boundary (intentional).** The foreign fact table (`TypeDatabase._strippedForeignConformances`) is **in-memory and scoped to the current generator run** — it is *not* serialized into the module database XML. This is sufficient because the decision to emit a closed binding over a foreign conformer is made during the same run that synthesizes the protocol and ingests the extension headers. The same boundary applies whenever the conformer-owning module is *not* the module being bound this run: a dependency is loaded either from a pre-built database XML or by re-parsing its ABI JSON, and neither path runs `UnderscoreProtocolSynthesizer` for the dependency (it runs only for the bound module). So a consumer that closes a *dependency's* generic over a foreign conformer — e.g. `AppIntents.IntentParameter<Swift.Int>` where `AppIntents` is a framework dependency — would not see the fact and would fail closed (skip the binding) — which is safe, not a correctness bug. Local conformer facts do not have this boundary (they ride `TypeRecord.ProtocolConformances`). If a future cross-module scenario needs the foreign facts to survive into a dependent module's run, the fix is to project `_strippedForeignConformances` into `ModuleDatabaseEmitter` / the database XML schema (and/or run the synthesizer for re-parsed dependency ABI); deferred until a real consumer needs it. No live surface hits this today — the motivating consumer (`AppShortcutParameterPresentation`) is framework-blocked per the Phase C audit below.

#### Higher-priority 8c blocker: `AppShortcutParameterPresentation` is silently dropped before emission

Every one of the five parallel structs declared in the swiftinterface (`AppShortcutParameterPresentation`, `…Title`, `…TitleString`, `…Summary`, `…SummaryString`, at swiftinterface lines 909, 486, 493, 8889, 8895) is **completely absent** from the regen — no `partial class`, no `// Unsupported:` tombstone, no decl at all. `grep -n "AppShortcutParameterPresentation" AppIntents.cs` returns zero hits. The four-generic-param-pack with `Parameter : IntentParameter<Value>` + higher-kinded `ParameterKeyPath : Swift.KeyPath<Intent, Parameter>` is being filtered upstream of every tombstone-emitting gate in the type pipeline. This is *not* the synthesizer's doing — `_IntentValue` is now projected and `IntentParameter<TValue>` *does* emit as a partial class, so the filter killing ASPP is a separate one.

This is the gating problem for 8c and is the next investigation: trace where in the parser / type-database / closed-conformer pipeline the four-generic-param-pack with higher-kinded constraints is being discarded silently. Likely a `where T : ConstructedGeneric<…, …>` shape filter that returns "drop this type" without writing a tombstone, in the type-registration phase before MemberValidationPipeline runs. Once that filter is identified and either lifted or made tombstone-producing, the generic-shape emission can proceed.

**Resolved — 8a-3 (shipped, uncommitted in `keypath-worktree`).** The silent drop was *not* a type-registration shape filter — it was `GenericSignatureParser.ParseConstraint` throwing on the higher-kinded constraint. The where-clause `ParameterKeyPath : Swift.KeyPath<Intent, Parameter>` was first torn apart by a naive `Split(',')` on the inner comma, and the constructed-generic target was then fed to `SwiftTypeName.FromModuleQualifiedName`, which throws on `<`. That throw propagated up to `SwiftABIParser.HandleNode`, which swallowed it and discarded the *entire enclosing decl* — hence zero decls and zero tombstones. The fix: split the where-clause at top-level commas only (`SwiftTypeListText.SplitTopLevelCommas`) and have `ParseConstraint` return null (dropping just that one unrepresentable constraint) instead of throwing. All five `AppShortcutParameterPresentation*` structs now emit. The primitive-`_IntentValue`-conformer caveat above still applies to the 8c per-tuple emitter.

#### Phase C feasibility audit — construction surface is framework-blocked; no per-tuple emitter ships

With Phase A + B + 8a-3 in place the type-system novelty this session set out to prove — closing the four-generic-param-pack with the higher-kinded `ParameterKeyPath : KeyPath<Intent, Parameter>` constraint, over both primitive and reference `_IntentValue` conformers — **is proven**: `IntentParameter<TValue>` now emits and type-checks for primitive `TValue` (Phase A), `SatisfiesConstraint` accepts the stripped foreign conformers (Phase B), all five `AppShortcutParameterPresentation*` structs survive parsing (8a-3), and a throwaway spike confirmed the C# compiler accepts the closed four-generic shape.

What remains — Phases 8c.3 / 8c.4 / 8c.5 below (per-tuple closed-struct emission + `AppShortcut.init(parameterPresentation:)` overloads) — **will not ship**, because an empirical audit of the *member surface* of the family in the iOS 26.2 `AppIntents.swiftinterface` shows there is **no C#- or trampoline-constructible path** to any value that participates in a public API. The entire family is designed for Swift result-builder + string-literal syntax inside an `AppShortcutsProvider`; it has no non-Swift construction path. Concretely:

- **Main `AppShortcutParameterPresentation`** — its sole `init` requires `optionsCollections: () -> some AppShortcutOptionsCollectionSpecification<Value.UnwrappedType>` built by `@AppShortcutOptionsCollectionSpecificationBuilder`. That builder has **no zero-arg `buildBlock()`** (lowest arity is `buildBlock<C0>(_ c0:)`), so `{ }` does not type-check even inside a trampoline we emit. Its only public conformer is `AppShortcutOptionsCollection<Provider> where Provider : DynamicOptionsProvider` (an app-defined PAT) + a `LocalizedStringResource` title. Constructing it from C# would mean binding a whole `DynamicOptionsProvider` + result-builder + `LocalizedStringResource` subsystem.
- **`…Title`** — `init(specific:, generic: StaticString, table: StaticString? = nil)`. `StaticString` is a compile-time literal; a runtime trampoline cannot synthesize one from a C# `string`. Not constructible.
- **`…Summary`** — `init(_ summaryString:, table: StaticString? = nil)`. Constructible (pass `table: nil`), but its only sink is the blocked main struct.
- **`…TitleString` / `…SummaryString`** — `init(_ value: String)`. Directly constructible from a C# string, but they feed only `…Title` (blocked) / `…Summary` → main (blocked).
- **`AppShortcut.init(…, parameterPresentation:)`** — requires a main-struct value (blocked) plus `[AppShortcutPhrase<Intent>]` + `LocalizedStringResource` + `_const String`.

The constructible leaves (`…TitleString` / `…SummaryString` from a string; `…Summary` via a `nil`-table trampoline) all terminate in types that are themselves unconstructible from C#. Emitting closed structs for them would be **dead code**: a C# type with a ctor that round-trips an opaque handle but feeds no usable sink. That violates the "no unusable bindings / no dead code to claim a roadmap item" rule, so per-tuple emission is declined rather than half-shipped. This was reviewed independently by Codex and Grok; both converged on the same call.

This is an **upstream framework-design blocker**, not a generator gap: there is no hidden simpler `init`, no `@_alwaysEmitIntoClient` convenience init, and no extension adding one (audited against the full swiftinterface). It becomes reusable the day a `DynamicOptionsProvider` + result-builder binding subsystem exists, or Apple adds a C-friendly construction path. The proven higher-kinded substitution capability and the Phase A/B primitive-conformer relaxation are the shippable v1 deliverable; the durable in-repo gate for Phase A's seed-drop is the `EquatableContainer<Int>` (→ `nint`) round-trip in `StdlibProtocolConstraintTests` (a Swift struct conformer always projects to an `ISwiftObject`-implementing C# type, so only a *primitive* type argument actually exercises the dropped seed).

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

> **Superseded by the Phase C feasibility audit above.** Phases 8c.3 / 8c.4 / 8c.5 are
> retained as the design record for when the upstream construction surface becomes
> bindable, but they are **not built** in this session: the family has no
> C#-constructible terminal sink (see the audit). v1 ships Phase A + B + 8a-3 plus the
> documentation of this blocker.

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

## Validation gates (v1 — Phase A + B + 8a-3)

| Gate | Expected |
|---|---|
| `nuke test` | Baseline + `GenericTypeEmitterTests` descriptor-path-safe seed-drop cases + `UnderscoreProtocolSynthesizerTests` stripped-conformance cases |
| `nuke binding-tests --compile-only` | Regen + compile-check clean; `EquatableContainer<nint>` emits with the `ISwiftObject` seed dropped and compiles |
| `nuke binding-tests --sim` | New `StdlibProtocolConstraintTests.TestEquatableContainer_PrimitiveElement_SeedDropRoundTrips` passes (construct over `nint` via factory, read `.Item` back) |
| `nuke binding-tests --device` | Same (NativeAOT — Phase A changes the generic where-clause / PWT arg path) |
| `nuke validate` (opt-in) | No regression; Phase A's `GetWhereClause` change is cross-cutting so a full sweep is warranted once |

## Exit criteria (v1)

- `IntentParameter<TValue>` type-checks and emits for primitive/frozen-value `_IntentValue` conformers (Phase A), and `SatisfiesConstraint` accepts the stripped foreign conformers (Phase B). Covered by unit tests.
- The seed-drop produces a *usable* binding end-to-end: a PAT/Self-requirement-constrained generic instantiated over a C# primitive constructs and round-trips a value (`EquatableContainer<nint>` in BindingTests, sim + device).
- The `AppShortcutParameterPresentation` family's construction-surface blocker is documented with swiftinterface cites (Phase C audit above); no dead per-tuple structs are emitted.

### Not in v1 (framework-blocked, see Phase C audit)

- Per-tuple closed `AppShortcutParameterPresentation*` structs and `AppShortcut.init<…>(parameterPresentation:)` overloads — no C#-constructible terminal sink exists upstream.

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

## Recommendation for the next implementation session (2026-05-23)

**8a-2 and 8a-3 have shipped (uncommitted in `keypath-worktree`); 8b and 8c are now both actionable.** The two pre-8b structural gaps the synthesizer downstream regen surfaced — `_IntentValue` conformance-record ingestion (Gap A; closes the reference-typed conformer CSM failures, primitive conformers excluded by design) and the synthesized-name internal-reach cascade (Gap B; `Pattern2InternalTypeReach` 784 → 0) — are closed by 8a-2. The 8c-specific gap, the silent drop of all five `AppShortcutParameterPresentation*` structs, is closed by 8a-3 (it was `GenericSignatureParser.ParseConstraint` throwing through `SwiftABIParser.HandleNode` on the higher-kinded `KeyPath<Intent, Parameter>` constraint — see "Higher-priority 8c blocker" above). The C#-side four-generic constraint shape proposed in this doc compiles cleanly when adjusted for `IAppIntent<TSelf>` self-reference and `IntentParameter<TValue>`'s `ISwiftObject` fallback bound (spike workspace at `/tmp/8c-spike/`), so 8c's emission design from Phase 8c.3 onward stands. Realistic ordering: 8b → 8c (8c carries the additional primitive-`_IntentValue`-conformer decision for its per-tuple emitter — see the caveat subsection above). Both gated only on committing 8a-2 + 8a-3 and re-running the AppIntents downstream regen.
