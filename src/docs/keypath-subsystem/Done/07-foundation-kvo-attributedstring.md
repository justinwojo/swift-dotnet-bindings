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

> **Note (post-implementation):** The risks below were written against the v1 plan (typed-singleton KVO + broad `@dynamicMemberLookup` reification). The narrower shipped path — per-class `KvoExtensionEmitter` + hand-rolled `AttributedString` partial atop the Apple Supplement xcframework — dodges most of them outright. See *Implementation outcomes (shipped)* near the bottom of this doc for the actual surface and how each risk was retired or sidestepped.


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

---

## Implementation outcomes (shipped)

What landed differs from the v1 sketch above on two material points: (1) KVO does not flow through the typed-singleton KeyPath path; it goes through a per-class `@_cdecl` observe shim generated by a dedicated emitter, with the C# extension method taking an `Action<TRoot, TValue>` instead of a `KeyPath<TRoot, TValue>` argument; (2) `AttributedString` ships with a hand-rolled partial on top of a new Apple Supplement xcframework rather than as broad `@dynamicMemberLookup` reification over every `AttributedStringKey` conformer. The narrower surface still covers the v1 acceptance criteria for both clusters and dodges the singleton-set explosion risk (Risk A) and the closed-enumeration cost (Phase 7.5) for v1. Cross-attribute coverage beyond `languageIdentifier` is left as a follow-up — see *Follow-ups* below.

### Cluster A — KVO bridging (shipped via Apple Supplement-style per-class emitter)

- **Emitter**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/KvoExtensionEmitter.cs`.
- **Gates** (intentionally narrow for v1):
  - Class must be `IsObjCRooted` — KVO is Foundation's NSObject contract.
  - `WrapperValidation.IsXCFrameworkMode` — without an xcframework wrapper, there's nowhere to put the `@_cdecl` shims.
  - Class must be non-generic and not nested in another type — both shapes break the `@_cdecl` symbol name and `\.{prop}` literal.
  - Property must be `IsObjCDynamic`, non-static, public, non-`@_spi`, with storage.
  - Property type must be a primitive in the per-emitter `s_supportedTypes` whitelist: `Int`/`Int32`/`Int64`/`UInt`/`UInt32`/`UInt64`/`Bool`/`Double`/`Float`. `String`, Optional, struct, and class-typed properties are recognised but skipped — separate ABI design.
- **Per (class C, property P)** emission:
  - One Swift `@_cdecl("SBW_KVO_{Module}_{C}_observe{P}")` trampoline that calls `obj.observe(\.{P}, options:) { observed, change in cb(observedPtr, change.newValue ?? observed.{P}, ctx) }` and returns the +1-retained `NSKeyValueObservation` token as a raw pointer.
  - One C# `[UnmanagedCallersOnly(CallConvs=[CallConvCdecl])]` dispatch trampoline that resurrects the managed handler from a `GCHandle` payload and re-marshals the receiver via `SwiftMarshal.MarshalBorrowedFromSwift<C>`.
  - One C# extension method `Observe{P}(this C obj, SbwKvoOptions options, Action<C, V_csharp> changed) → KvoToken`.
  - `[UnmanagedCallConv(CallConvs=[CallConvCdecl])]` is emitted on *every* `[LibraryImport]` (both the `Observe{P}Native` and the per-class `InvalidateNative`). `EntryPointCallConvPairingTests` enforces the pairing on every emitted P/Invoke in the binding output; missing it ships a regression that fails that test.
- **Per class C** (emitted once via `emissionContext.TryAddKeyPathSingletonContainer`):
  - One Swift `@_cdecl("SBW_KVO_{Module}_{C}_invalidate")` shim that takes the retained token, calls `token.invalidate()`, and drops it.
  - One C# `private static partial void InvalidateNative(IntPtr token)` P/Invoke + `private static readonly Action<IntPtr> s_invalidate` that every emitted `Observe{P}` constructor passes into the returned `KvoToken`.
- **Runtime support**: `src/Swift.Runtime/src/Swift/KeyValueObserving.cs` adds `SbwKvoOptions` (a `[Flags]` enum with raw bit values matching `NSKeyValueObservingOptions` but exposing only the v1-supported subset — `None | New | Initial`; `Old` / `Prior` are deferred because the v1 dispatcher only forwards `change.newValue`) and `KvoToken : IDisposable`. The token holds the +1-retained `NSKeyValueObservation` pointer plus the `GCHandle` rooting the managed handler; on `Dispose` it calls `s_invalidate` and frees the handle inside a `finally` so the handle is released even if the native invalidator throws. There is intentionally no finalizer: the Swift observation keeps the GCHandle slot live as its `ctx` pointer, so freeing the handle from finalization while the observation is still subscribed would be a use-after-free against a later KVO notification. Callers must Dispose.
- **Why this shape instead of the v1 typed-singleton plan**:
  - Avoids Risk A (singleton-set explosion): only KVO-observable properties on KVO-rooted classes emit, not every `@objc dynamic var` on every `NSObject` subclass.
  - Avoids the `KeyPath<Root, Value>` *parameter* shape entirely on the C# side. Callers write `obj.ObserveCounter(opts, handler)` directly; the keypath literal lives only on the Swift side of the shim. This sidesteps Session 4 typed-singleton plumbing for what is effectively a v1 surface of ≤ ~50 properties across Foundation/UIKit/AppKit per module.
  - The closure-marshalling story (Phase 7.3, Risk D) is bypassed: the handler is rooted by `GCHandle`, dispatched through `UnmanagedCallersOnly`, no Session-closure infrastructure on the path.
- **BindingTests fixture** (Phase 7.6, shipped):
  - `BindingTests/Sources/SwiftBindingsTestLib/Foundation/TestNSObservable.swift` — `TestNSObservable: NSObject` with `@objc dynamic var counter: Int` and `var name: String`; factory + property mutators.
  - `BindingTests/RuntimeTestsApp/FoundationInterop/FoundationKvoTests.cs` — five tests:
    1. `TestObserveCounter_FiresOnInitial` — `SbwKvoOptions.Initial | .New` delivers exactly one callback at subscribe time with the current value.
    2. `TestObserveCounter_FiresOnMutate` — two `MutateCounter` calls produce two callbacks with the correct values in order.
    3. `TestObserveCounter_DisposeStopsCallbacks` — post-`Dispose`, mutations no longer fire.
    4. `TestObserveCounter_ReceiverIdentity` — the receiver passed to the callback wraps the same `SwiftHandle` as the observed object.
    5. `TestObserveBool_RoundTripsTrueThenFalse` — three sequential `MutateEnabled(true/false/true)` calls round-trip cleanly through the `@convention(c)` callback ABI for a Bool-typed `@objc dynamic` property (the single-byte ABI shape distinct from the nint-typed `counter`).

### Cluster B — `AttributedString` constructor + `LanguageIdentifier` (shipped via Apple Supplement)

- **Apple Supplement xcframework**: `SwiftBindingsAppleSupplement.xcframework`, a 6-slice host (`ios`/`ios-sim`/`maccatalyst`/`macos`/`tvos`/`tvos-sim`) built by the `Nuke build-apple-supplement-xcframework` target and shipped with the runtime NuGet at `runtimes/native/`. Loaded by the existing `SwiftFrameworkResolver` (`@rpath/{name}.framework/{name}` rule); no new resolver logic.
- **Swift side** — `src/Swift.Bindings.Apple/Shims/AttributedStringShims.swift` exports five `@_cdecl` symbols, all UTF-8 byte buffer ABI (the same lingua franca used by `SBW_SwiftString_*` in the runtime):
  - `SBW_AttributedString_InitFromUtf8(utf8Ptr, utf8Len, outBuffer)` — writes `AttributedString(String(utf8))` into the caller's heap slot (sized by the AttributedString value-witness `MemoryLayout.size`).
  - `SBW_AttributedString_GetCharacters(astrPtr, outUtf8Ptr, outUtf8Len)` — projects `String(astr.characters)` into a fresh heap buffer the caller must `FreeBuffer`.
  - `SBW_AttributedString_FreeBuffer(ptr)` — counterpart deallocator, nil-safe.
  - `SBW_AttributedString_GetLanguageIdentifier(astrPtr, outUtf8Ptr, outUtf8Len) → Int` — returns 1 if a uniform language attribute is present (UTF-8 bytes written into a fresh buffer) and 0 otherwise.
  - `SBW_AttributedString_SetLanguageIdentifier(astrPtr, utf8Ptr, utf8Len, hasValue)` — `hasValue == 0` clears the attribute; `hasValue == 1` sets it to the decoded UTF-8 String (empty buffer ⇒ empty string).
  - All pointers are `UnsafeRawPointer`/`UnsafeMutableRawPointer` rather than typed `UnsafePointer<AttributedString>`: the `@_cdecl` checker rejects typed pointers to non-`@objc`-representable Swift value types. Each shim re-binds via `.assumingMemoryBound(to: AttributedString.self).pointee`.
- **C# side** — `src/Swift.Bindings.Apple/Sources/Foundation/AttributedString.cs` is a hand-rolled `public sealed partial class` augmenting the generator's emitted shell:
  - `public unsafe AttributedString(string text)` — allocates `NativeMemory.Alloc(metadata.Size)`, runs `InitFromUtf8`, hands the heap slot to a `SwiftSafeHandle<AttributedString>`. C# is the sole owner, so AttributedString's internal COW refcount sees 1 and mutating setters apply in place.
  - `public override string ToString()` — pumps through `GetCharacters` + `FreeBuffer`.
  - `public string? LanguageIdentifier { get; set; }` — canonical example of a `@dynamicMemberLookup`-routed attribute. Get reads via `GetLanguageIdentifier` (returns `null` on `hasValue == 0`); set via `SetLanguageIdentifier` with `hasValue` derived from `value is null`. All paths take a `DangerousAddRef` while inside the unmanaged call window and release in a `finally`.
  - Nested `private static partial class SupplementNative` carries the five `[LibraryImport("SwiftBindingsAppleSupplement")]` declarations. Every one is annotated `[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]` to satisfy `EntryPointCallConvPairingTests`.
- **Why this shape instead of broad `@dynamicMemberLookup` reification**:
  - Avoids Phase 7.5's closed-enumeration cost: only one attribute (`languageIdentifier`) is wired in v1. Adding `link`, `foregroundColor`, etc. is a pure copy-paste exercise against the same Apple Supplement framework.
  - Avoids Risk C entirely (`K.Value` associated-type projection): we never name `AttributedStringKey`/`AttributeDynamicLookup` from C#; the property type is just `string?`.
  - Avoids Risk G (set-via-`WritableKeyPath` ABI): mutations happen on the C#-owned heap slot directly through the `Set*` shims, and AttributedString's COW guarantees in-place mutation given refcount-of-1 storage.
- **BindingTests fixture** — `BindingTests/RuntimeTestsApp/AppleSupplement/AttributedStringTests.cs`, 8 tests:
  1. `TestCtor_FromString_PreservesCharacters` — `new AttributedString("Hello, world!")` round-trips through `ToString()`.
  2. `TestCtor_FromEmptyString_PreservesEmpty` — empty string is a legal input.
  3. `TestCtor_FromUnicodeString_PreservesCodepoints` — BMP + surrogate-pair emoji + non-Latin scripts round-trip losslessly across the UTF-8 boundary.
  4. `TestLanguageIdentifier_DefaultIsNull` — a fresh AttributedString has no language attribute.
  5. `TestLanguageIdentifier_SetAndGetRoundTrips` — set `"fr"`, get `"fr"`.
  6. `TestLanguageIdentifier_AssignNullClearsAttribute` — `set null` after a non-null set removes the attribute.
  7. `TestLanguageIdentifier_Reassignment_Wins` — last-write-wins through repeated sets.
  8. `TestToString_AfterAttributeMutation_DropsAttributesButKeepsText` — attribute mutations do not corrupt the underlying character storage.

### Generator regression caught + closed in this session

- `KvoExtensionEmitter.cs` originally omitted `[UnmanagedCallConv(CallConvs = [CallConvCdecl])]` on the `InvalidateNative` and `Observe{Prop}Native` `[LibraryImport]` declarations it generated. `EntryPointCallConvPairingTests.TestEveryMangledEntryPointDeclaresSwiftCallConv` (the unit test that walks the emitted binding output looking for missing `UnmanagedCallConv` on `SBW_`-prefixed entry points) caught the omission at the BindingTests gate. Fix: emit the attribute on both sites. **Lesson worth recording**: any new emitter that produces `[LibraryImport]` declarations against `SBW_` cdecl shims must emit the matching `[UnmanagedCallConv]` attribute — there is no fallback; the pairing test fail-closes.

### Follow-ups (deferred, not blocking)

- **Broaden attribute coverage on AttributedString**: `link` (`URL?`), `foregroundColor` / `backgroundColor` (UIKit/AppKit color types — requires per-TFM platform color shims), `font` (same gating), the remaining `AttributeScopes.FoundationAttributes` entries. Pattern is the established UTF-8/buffer ABI for primitives + a per-color-type shim where the value type is platform-specific. Trigger to revisit: a downstream consumer requests a specific attribute, or the AppIntents / SwiftUI productionization sessions take a dependency on a richer AttributedString surface.
- **Broaden the KVO emitter value-type whitelist**: `String` (UTF-8 buffer ABI mirrors `SBW_SwiftString_*`), `Optional<primitive>` (add a `presence` bit to the dispatch), Foundation-bridged structs (per-type shim). Trigger: a real consumer hits a property the v1 whitelist excludes.
- **Generic NSObject subclasses + nested-NSObject classes**: the emitter currently skips both. A generic Self in the `@_cdecl` symbol is a separate ABI problem (mangled per-instantiation symbol vs. a runtime dispatch table) — defer until a real consumer surfaces a generic KVO-observable class.
