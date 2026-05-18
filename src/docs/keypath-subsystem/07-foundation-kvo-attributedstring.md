# Session 7 — Foundation: KVO bridging + `AttributedString` `@dynamicMemberLookup`

First post-MusicKit productionization wave. Foundation has 218 lines of public `KeyPath<…>` surface across two structurally different clusters that both land in this session.

## Goal

Bind Foundation's two KeyPath-consuming surfaces:

1. **KVO bridging on `NSObject`** — extension methods `publisher(for:options:)`, `observe(_:options:changeHandler:)`, etc. that take `KeyPath<Root, Value>` where `Root: NSObject`. ~5 surface methods, all on `NSObject` itself or its `@objc dynamic` properties.
2. **`AttributedString` `@dynamicMemberLookup`** — subscripts of the form `subscript<K: AttributedStringKey>(dynamicMember keyPath: KeyPath<AttributeDynamicLookup, K>) -> K.Value? { get set }`. ~12 subscript variants across `AttributedString`, `AttributedSubstring`, `AttributedString.Runs`, attribute container types. Powers idiomatic Swift `attributedString.foregroundColor = .red`.

## Why this session

- Foundation is the highest-impact framework after MusicKit. KVO + AttributedString are everyday APIs; without them, downstream consumers in AppIntents and SwiftUI fall back to less-idiomatic surfaces.
- Validates the foundation against a `Root`-constrained class hierarchy (`NSObject`) — different from MusicKit's PAT-constrained value-type generic shape.
- Validates `@dynamicMemberLookup`-as-KeyPath-subscript projection — the most common KeyPath shape across SwiftUI, AppIntents, and Foundation. Get this right here and Sessions 8/9 inherit the projection rules.

## Dependencies

- **Session 3** — KeyPath foundation (Class type records, `KeyPathProjection`, SafeHandle).
- **Session 4** — Typed singleton emission, for the AttributedString `KeyPath<AttributeDynamicLookup, K>` case where the C# caller must originate the KeyPath.

Sessions 1, 2, 5 not needed (Foundation's KeyPath surface is not on PAT-constrained generic parents — `NSObject` is unconstrained, `AttributedString` is concrete).

## Cluster A — KVO bridging on `NSObject`

### Pre-image surface

From `Foundation.swiftinterface`:

```swift
extension NSObjectProtocol where Self : NSObject {
    public func publisher<Value>(for keyPath: KeyPath<Self, Value>, options: NSKeyValueObservingOptions = [.initial, .new]) -> NSObject.KeyValueObservingPublisher<Self, Value>
    public func observe<Value>(_ keyPath: KeyPath<Self, Value>, options: NSKeyValueObservingOptions = [.initial], changeHandler: @escaping (Self, NSKeyValueObservedChange<Value>) -> Swift.Void) -> NSKeyValueObservation
}

public class NSKeyValueObservedChange<Value> { /* ... */ }
public class NSKeyValueObservation { /* ... */ }
```

Both methods are CSM-emission targets: protocol-extension methods with method-own generic `Value` parameter, constrained by `where Self : NSObject`. The closed-conformer set is "every public `NSObject` subclass" — too large to enumerate. **Therefore**: emit these as plain C# extension methods on `Swift.Foundation.NSObject` (and inherit to all subclasses), not as per-conformer specializations.

The `Root` parameter (`Self`) is the receiver — must be the concrete subclass at C# call site. The KeyPath signature is `KeyPath<NSObject, Value>` (in the most general form) or `KeyPath<ConcreteSubclass, Value>` (when C# uses a typed singleton).

### Phase 7.1 — KVO C# extension methods

For both `publisher(for:options:)` and `observe(_:options:changeHandler:)`:

- Emit as static extension method on `Swift.Foundation.NSObject` (or whatever the projected class is):

```csharp
public static class NSObjectKvoExtensions
{
    public static KeyValueObservingPublisher<TRoot, TValue> Publisher<TRoot, TValue>(
        this TRoot self,
        KeyPath<TRoot, TValue> keyPath,
        NSKeyValueObservingOptions options = NSKeyValueObservingOptions.Initial | NSKeyValueObservingOptions.New
    ) where TRoot : NSObject
    { /* P/Invoke into emitted trampoline */ }

    public static NSKeyValueObservation Observe<TRoot, TValue>(
        this TRoot self,
        KeyPath<TRoot, TValue> keyPath,
        NSKeyValueObservingOptions options = NSKeyValueObservingOptions.Initial,
        Action<TRoot, NSKeyValueObservedChange<TValue>> changeHandler = null
    ) where TRoot : NSObject
    { /* P/Invoke; changeHandler is a closure passed via Session-closure machinery */ }
}
```

The `where TRoot : NSObject` constraint is C# side; the Swift side reads the receiver via `passRetained.toOpaque()`/`takeUnretainedValue()` exactly as the foundation passes `NSObject` references.

### Phase 7.2 — `KeyPath<TRoot, TValue>` parameter from C#

This is the **IN-path** case from Session 3/4. The C# caller writes:

```csharp
var observation = myView.Observe(NSViewKeyPaths.Bounds, options: .New, changeHandler: ...);
```

`NSViewKeyPaths.Bounds` is a typed singleton emitted by Session 4 (one Swift trampoline per `@objc dynamic` KVO-observable property per `NSObject` subclass that exposes one).

**Tricky point**: KVO-observable properties on `NSObject` subclasses are `@objc dynamic` and the KVO machinery resolves the keypath at runtime via `_kvoKeyPathString`. The actual `KeyPath<Self, Value>` is materialised normally (`swift_getKeyPath` with the keypath descriptor), so Session 4's singleton path works unchanged.

**Conformer enumeration for KVO singletons**: only emit singletons for `NSObject` subclasses that have at least one `@objc dynamic` property *and* that Foundation/UIKit/AppKit publicly exposes as KVO-friendly. This is the heuristic — there is no metadata flag. **Pragmatic v1 rule**: emit KeyPath singletons for any `@objc dynamic var` on any `NSObject` subclass; let consumers use them where KVO is documented. False positives (a singleton for a non-KVO `@objc dynamic` property) are harmless — calling `observe(_:)` against a non-KVO property is a Foundation API misuse the runtime catches.

### Phase 7.3 — `changeHandler` closure marshalling

`changeHandler: @escaping (Self, NSKeyValueObservedChange<Value>) -> Void` is a Swift closure. Closure subsystem (already in place from prior work) handles this — but verify `NSKeyValueObservedChange<Value>` projects correctly (it's a generic class). If `NSKeyValueObservedChange<NSString>` and `NSKeyValueObservedChange<NSNumber>` produce distinct closed C# types via the existing closed-generic-class path, no additional work needed here.

If a regression appears (e.g. `NSKeyValueObservedChange<Value>` binds as the open generic form only): hot-fix in Session 3 territory (open-generic class projection); not a Session 7 deliverable.

## Cluster B — `AttributedString` `@dynamicMemberLookup`

### Pre-image surface

From `Foundation.swiftinterface`:

```swift
@dynamicMemberLookup
public struct AttributedString : Sendable {
    public subscript<K>(dynamicMember keyPath: WritableKeyPath<AttributeDynamicLookup, K>) -> K.Value? where K : AttributedStringKey
    public subscript<K>(dynamicMember keyPath: KeyPath<AttributeDynamicLookup, K>) -> K.Value? where K : AttributedStringKey
    // … and parallel subscripts on .Runs, .CharacterView, .UnicodeScalarView
}

public struct AttributeDynamicLookup { /* opaque — purely a phantom type for KeyPath routing */ }
extension AttributeDynamicLookup {
    public subscript<T>(dynamicMember keyPath: KeyPath<AttributeScopes, T.Type>) -> T where T : AttributeScope
    public subscript<T>(dynamicMember keyPath: KeyPath<T, T>) -> T where T : AttributedStringKey
}
```

`AttributedString.foregroundColor` desugars to two chained `@dynamicMemberLookup` subscript calls: first `attributeDynamicLookup[dynamicMember: \\AttributeScopes.foregroundColor] -> AttributeScopes.ForegroundColor.Type`, then projection.

### Phase 7.4 — Project `@dynamicMemberLookup` to per-attribute C# properties

`@dynamicMemberLookup` is a *Swift compile-time* feature — at the ABI level, every attribute access compiles to a `subscript(dynamicMember:)` call with a synthesized `keypath` instruction. C# has no equivalent, so the bindings must reify each known attribute as a named C# property.

The set of known attributes is closed at SDK build time (`AttributeScopes.FoundationAttributes`, `AttributeScopes.UIKitAttributes`, etc. — listed in `Foundation.swiftinterface`). Generator strategy:

1. Walk all `AttributedStringKey` conformers nested in `AttributeScopes.*Attributes` value types (`ForegroundColorAttribute`, `BackgroundColorAttribute`, etc.).
2. For each, emit a Swift `@_cdecl` trampoline returning `KeyPath<AttributeDynamicLookup, ConformerType>` retained.
3. Emit a C# property on `Swift.Foundation.AttributedString` that calls `swift_getAtKeyPath` (or `swift_setAtWritableKeyPath`) with the typed singleton, projected to `K.Value?`.

```csharp
public partial struct AttributedString
{
    public Color? ForegroundColor {
        get => GetAttribute<Color>(AttributedStringForegroundColorKeyPath);
        set => SetAttribute(AttributedStringForegroundColorKeyPath, value);
    }
    // … plus dozens more, one per AttributedStringKey conformer
}
```

The `KeyPath<AttributeDynamicLookup, ConformerType>` singleton is emitted exactly per Session 4's `*KeyPaths` container, but here the *container* is the per-attribute-scope hosting type rather than a generic parent's nested bag.

### Phase 7.5 — Closed attribute-scope conformer enumeration

The conformer scan: walk Foundation's `AttributeScopes` extension wall, collect every type implementing `AttributedStringKey`. Expected list (from Foundation 26.2):

- `AttributeScopes.FoundationAttributes.LanguageAttribute`
- `AttributeScopes.FoundationAttributes.LinkAttribute`
- `AttributeScopes.FoundationAttributes.LocalizedStringArgumentAttribute`
- `AttributeScopes.FoundationAttributes.LocalizedNumberFormatAttribute`
- … (~25 in Foundation core, more in UIKit/AppKit/SwiftUI extension scopes)

UIKit/AppKit/SwiftUI scopes are excluded from this session (they cross-cut multiple frameworks; covered in Session 9). Foundation's own attribute-scope contents are sufficient for first-pass cover.

## Phase 7.6 — BindingTests fixture

`BindingTests/Sources/SwiftBindingsTestLib/Foundation/FoundationKvo.swift`:

```swift
import Foundation

public class TestNSObservable: NSObject {
    @objc dynamic public var counter: Int = 0
    @objc dynamic public var name: String = ""
}

public func makeObservable() -> TestNSObservable { TestNSObservable() }
```

C# test (`BindingTests/RuntimeTestsApp/Foundation/FoundationKvoTests.cs`):

- Create a `TestNSObservable`, set up `Observe(TestNSObservableKeyPaths.Counter, options: .New, changeHandler: (obs, change) => { … })`, mutate `counter`, verify changeHandler fires with the new value.
- Repeat for `Name` (String — exercises a different value type through KVO).
- Negative: verify singleton field is identity-stable (`TestNSObservableKeyPaths.Counter == TestNSObservableKeyPaths.Counter` returns true on the lazy-init field).

`BindingTests/Sources/SwiftBindingsTestLib/Foundation/AttributedStringDynamic.swift`:

```swift
import Foundation

public func makeAttributed(_ text: String, color: Int) -> AttributedString {
    var attr = AttributedString(text)
    // touch an attribute via @dynamicMemberLookup to ensure SIL emits keypath()
    return attr
}
```

C# test: construct `AttributedString`, set `.ForegroundColor` via the projected property, read it back, verify round-trip. **Catch**: `Color`-typed attributes may need a `Color` projection if the test wants real UIKit Color values. Use a primitive attribute that doesn't require platform color types (e.g. `LinkAttribute = URL`) to keep the fixture minimal.

## Validation gates

| Gate | Expected |
|---|---|
| `nuke test` | Baseline; any new emitter unit tests for `@dynamicMemberLookup` reification pass |
| `nuke binding-tests --sim` | `FoundationKvoTests` + `AttributedStringDynamicTests` pass |
| `nuke binding-tests --device` | Same (KVO on NativeAOT can differ — block runs are still managed pointers) |
| `nuke validate` | Foundation library `cs_compile` count ratchets up by the number of newly-bound members |

## Exit criteria

- All ~5 KVO methods on `NSObject` extensions emit and pass test.
- All ~25 Foundation-core `AttributedStringKey` conformers project as named C# properties on `AttributedString`.
- Closure passing for `changeHandler` round-trips (already tested by closure subsystem, but verify in this fixture too).
- BindingTests fixture passes sim + device.

## Risks specific to Session 7

- **Risk A (KVO singleton-set explosion)** — naïve emission of a typed singleton for every `@objc dynamic var` across every public NSObject subclass blows up the binary size. **Mitigation:** lazy `static readonly KeyPath` initialisation (only the singletons actually accessed pay their P/Invoke cost at runtime; the unused trampolines still occupy Swift wrapper-lib `__text`). If wrapper-lib bloat exceeds an acceptable bound (TBD threshold), restrict to KVO-observable properties whitelisted via a `KvoExposureHint` opt-in in `FoundationDatabase.xml` — but treat that as a follow-up, not a v1 blocker.
- **Risk B (`AttributeDynamicLookup` is a phantom)** — `AttributeDynamicLookup` is a stateless type whose only purpose is to host `@dynamicMemberLookup` subscripts. The KeyPath descriptor for `\AttributeDynamicLookup.foregroundColor` is generated by the Swift compiler at the subscript-call site; the *receiver* (an `AttributedString`) never actually instantiates `AttributeDynamicLookup`. **Diagnostic:** verify SIL output of a probe shows `keypath $WritableKeyPath<AttributeDynamicLookup, ForegroundColorAttribute>` followed by `swift_getKeyPath`; confirm runtime materialisation works without ever touching `AttributeDynamicLookup` storage.
- **Risk C (`AttributedStringKey.Value` associated-type projection)** — `K.Value` is an associated type whose closed substitution is per-attribute (e.g. `ForegroundColorAttribute.Value = Color`). The C# property must surface `K.Value`, not `Any`. Closed substitution happens per Session 4 (the conformer set is closed at SDK build time). **Diagnostic:** generated `.cs` for `AttributedString.ForegroundColor` should be typed as `Color?` (or whatever `ForegroundColorAttribute.Value` substitutes to), not `object?`.
- **Risk D (KeyPath subscript-with-default-argument ABI)** — `observe(_:options:changeHandler:)` has a default for `options`. Generator's existing default-argument handling must continue to work when a parameter is a Swift closure. **Diagnostic:** verify the emitted C# extension method exposes the default and the runtime call dispatches correctly when called with three vs. two args.
- **Risk E (cross-module proxy for `NSObject`)** — `NSObject` lives in Foundation but is consumed (and subclassed) by AppKit, UIKit, SwiftUI, etc. The KVO extensions emit on the Foundation-projected `NSObject`. Cross-module proxy class qualification (constraint #29) must produce extension methods reachable from C# code that has only imported `Swift.Foundation`. **Diagnostic:** grep emitted C# for cross-module `NSObjectKvoExtensions` references; ensure no `using Swift.UIKit;` is required to call `Observe()`.
- **Risk F (KVO + structural sharing of `KeyPath` interning)** — the same `\TestNSObservable.counter` literal used in two different C# call sites (test method A, test method B) should resolve to the same singleton field, not two distinct KeyPath objects, to prevent KVO observation duplication. Test the equality invariant explicitly.
- **Risk G (AttributedString set-via-WritableKeyPath ABI)** — the setter path uses `swift_setAtWritableKeyPath` (value-type mutation). The C# wrapper must arrange for `AttributedString` to be passed by reference (via `inout` shim) or the mutation is lost (the captured copy is overwritten then discarded). **Diagnostic:** end-to-end test — set `.ForegroundColor`, read back, expect the new value (this is exactly the BindingTests fixture above).

## References

- `00-overview.md` — design decision (typed singletons via Session 4)
- `03-keypath-foundation.md` (foundation types)
- `04-typed-singleton-emission.md` (per-property trampoline emission)
- Foundation `swiftinterface` — `@dynamicMemberLookup` cluster, KVO extensions
- `.claude/rules/constraints.md` line 29 (cross-module proxy)
- Apple docs: KVO via Swift KeyPath, AttributedString attribute scopes
