# TestFramework Enhancement Research: Pass 2

> **Goal**: Identify additional real-world Swift interop patterns from validation libraries that are not yet covered by the TestFramework, to further increase regression detection confidence.
>
> **Method**: Generated bindings (C#, Swift wrappers, ABI JSON) for 15 libraries using `validate-libraries.sh`, analyzed generated output for patterns not covered by existing tests.
>
> **Libraries analyzed**: GRDB, Lottie, Valet, Quick, Parchment, DifferenceKit, KeychainSwift, Mixpanel, NVActivityIndicatorView, SVGView, SDWebImage, CocoaLumberjack, CocoaLumberjackSwift, FirebaseCore, FirebaseCoreExtension, SkeletonView, SwipeCellKit, AMPopTip, StripeCore, StripePayments, Nuke, Alamofire, SnapKit, FSPagerView, SwiftyGif, TinyConstraints
>
> **Baseline**: All 25 patterns from the original `testframework-enhancement-plan.md` (Pass 1) have been implemented and are active.

---

## New Patterns Found: 44 Gaps

### Legend
- **Priority**: P0 = exercises untested code path in generator/runtime, P1 = important real-world pattern, P2 = coverage depth / edge case
- **Category**: The type of interop pattern
- **Evidence**: Which library demonstrated the need

---

### GROUP L: Enum Patterns (6 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| L1 | Enum with collection-typed associated value (`[String]` payload) | P0 | Lottie `LottiePlaybackMode.Markers([String])` |
| L2 | Nested enum-with-associated-values inside enum-with-associated-values | P1 | Lottie `LottiePlaybackMode.Paused(PausedState.Progress(Double))` |
| L3 | All-payload enum (every case has associated value, no empty cases) | P1 | Lottie `LottieAnimationSource` (2 cases, both class refs) |
| L4 | Mixed payload + no-payload enum with heterogeneous types | P1 | GRDB `DatabaseValue.Storage` (int64, double, string, blob, null), Lottie `LottiePlaybackMode` (11 cases) |
| L5 | Caseless enum used as namespace (contains nested types only) | P1 | GRDB `ValueReducers`, `DatabasePublishers` — emitted as `static partial class` |
| L6 | Nested enum with String rawValue + CaseIterable inside a class | P2 | SVGView `SVGPreserveAspectRatio.Align` — `AllCases` returns `SwiftArray<Align>` |

**L1: Enum with Collection Payload** — The existing `MultiAssociatedValues.swift` covers Int32/String/Bool payloads but not arrays. This exercises `SwiftArray<SwiftString>.FromEnumerable()` inside `DestructiveProjectEnumData` extraction — a distinct marshalling path.

```swift
// Lottie pattern: enum case carrying an array
public enum MediaSource {
    case single(name: String)
    case playlist(names: [String])
    case empty
}
```

**L4: Mixed Payload Enum with Heterogeneous Types** — GRDB's `DatabaseValue.Storage` is the canonical example: `int64(Int64)`, `double(Double)`, `string(String)`, `blob(Data)`, `null`. Each case marshals through a different type (blittable, SwiftString, byte[], singleton). The `TryGet*` extractors with `[MaybeNullWhen(false)]` are generated for each payload case. Current tests have homogeneous payload types.

**L5: Caseless Enum as Namespace** — Swift allows `enum Foo {}` with no cases, used purely as a namespace for nested types. Generator emits `public static partial class`. Not testable as an enum — testing is about whether nested types inside it resolve correctly.

---

### GROUP M: Generic Patterns (5 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| M1 | Multi-type-parameter generic struct (`<T1, T2>`) | P0 | DifferenceKit `ArraySection<TModel, TElement>`, GRDB `MapCursor<TBase, TElement>` |
| M2 | Generic constructor with Protocol Witness Table argument | P0 | DifferenceKit `DifferentiableBox<T: Differentiable>` — PWT threaded through P/Invoke |
| M3 | Generic class implementing protocol interface | P1 | Quick `AsyncBehavior<TContext> : IAsyncDSLUser` — conformance symbol + generic metadata |
| M4 | Generic struct with optional generic property (`T?` get/set) | P1 | Quick `TestState<T>.WrappedValue` — `SwiftOptional<T>` with generic metadata |
| M5 | Constructor taking `SwiftArray<GenericType<T>>` (nested generics in collections) | P2 | DifferenceKit `StagedChangeset<T>(SwiftArray<Changeset<T>>)` |

**M1: Multi-Type-Parameter Generics** — The test framework only has single-parameter generics (`Wrapper<T>`, `GenericPair<T,U>` exists but `GenericPair` has both params constrained the same way). The real-world pattern requires TWO separate metadata arguments: `PInvoke_getMetadata(SwiftObjectHelper<TModel>.GetTypeMetadata(), SwiftObjectHelper<TElement>.GetTypeMetadata())`. Tests should verify both metadata arguments are threaded correctly.

**M2: Generic Constructor with PWT** — Current generic tests pass type metadata but not protocol witness tables through constructors. The `DifferentiableBox` pattern requires `ProtocolWitnessTable.GetOrThrow<TBase, IContentEquatable>()` alongside metadata. This is a distinct code path in the runtime.

---

### GROUP N: Protocol Patterns (5 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| N1 | Protocol with default implementation (protocol extension) | P0 | Lottie `IAnimationImageProvider.CacheEligible` — emitted as `throw NotSupportedException` |
| N2 | Protocol methods accepting existential parameters (`any Protocol` in param) | P1 | Parchment `IPagingViewControllerDelegate` methods take `IPagingItem` existential |
| N3 | Class conforming to multiple custom protocols simultaneously | P1 | Parchment `PagingViewController : IPagingMenuDataSource, IPagingMenuDelegate` |
| N4 | Marker protocol (zero members, used as type constraint) | P2 | SVGView `IXMLNode` — empty interface, used in `IReadOnlyList<IXMLNode>` |
| N5 | Protocol with associated type projected as generic interface | P2 | DifferenceKit `IDifferentiableSection<TCollection>`, Parchment `IPagingIndicatorStyle<TBody>` |

**N1: Protocol Default Implementations** — Swift protocol extensions provide default implementations. The generator correctly identifies these and emits `throw new NotSupportedException("This property/method uses a Swift protocol extension default...")` since the default can't be called through the existential container. Important to test that consumers understand this limitation.

**N3: Multiple Protocol Conformance** — Current tests verify single protocol conformance. A class implementing 2+ module-defined protocols requires multiple witness table registrations and multiple `IExistentialBoxable` boxing paths. The generated `_protocolConformanceSymbols` dictionary must contain entries for each protocol.

---

### GROUP O: Collection & Dictionary Patterns (5 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| O1 | Dictionary property (get + set) | P0 | SVGView `XMLElement.Attributes` — `IReadOnlyDictionary<string, string>` property with both getter and setter |
| O2 | Dictionary with existential values (`[String: any Protocol]`) | P1 | Mixpanel `Track(properties: [String: any MixpanelType]?)` — `SwiftDictionary<SwiftString, ExistentialContainer1>` |
| O3 | Array of class instances as property | P1 | SVGView `SVGGroup.Contents: IReadOnlyList<SVGNode>` — `SwiftArray<SVGNode>` |
| O4 | Existential array property (`[any Protocol]`) | P1 | SVGView `XMLElement.Contents: IReadOnlyList<IXMLNode>` — `SwiftArray<ExistentialContainer1>` with proxy projection |
| O5 | `CaseIterable` enum's `AllCases` as static `IReadOnlyList<T>` property | P2 | Valet 5 enum types, SVGView nested enums — `SwiftArray<Align>` |

**O1: Dictionary Property** — Current tests only cover dictionary in constructors (`HeaderMap(headers:)`). A dictionary as a read-write property requires `SwiftDictionary` marshalling in both getter (Swift→C#) and setter (C#→Swift) directions — a different code path from constructor-only usage.

**O2: Dictionary with Existential Values** — Combines dictionary marshalling with `ExistentialContainer1` boxing for each value. The `SwiftDictionary<SwiftString, ExistentialContainer1>` pattern requires the projected dictionary to box/unbox protocol existentials on every access. Not tested.

---

### GROUP P: Closure Patterns (3 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| P1 | Nullable closure property (get/set) — `Action<T, U>?` | P0 | Lottie `LottieAnimationLayer.AnimationLoaded: Action<LottieAnimationLayer, LottieAnimation>?` |
| P2 | Static closure property (stored as static, get/set) | P1 | NVActivityIndicatorView `DefaultFadeInAnimation: static Action<UIView>`, GRDB `Database.LogError: static Action<ResultCode, string>?` |
| P3 | Optional closure parameter on method (`Action?` param) | P1 | Mixpanel `Identify(distinctId, completion: Action?)`, `Flush(completion: Action?)` |

**P1: Nullable Closure Property** — The getter wraps a Swift closure handle in `SwiftEscapingClosure` with function pointer casting. The setter must handle both non-null (creates `SwiftClosureData` with `GCHandle.Alloc` + `[UnmanagedCallersOnly]` trampoline) and null (sends `default` / zero-initialized `SwiftClosureData`). This exercises the nullable closure marshalling path which is distinct from required closures.

**P2: Static Closure Property** — Combines static property access with closure marshalling. The P/Invoke uses a function pointer without `SwiftSelf` (static context). Currently no test has a static property whose type is a closure.

---

### GROUP Q: Inheritance & Type Hierarchy (3 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| Q1 | 3+ level class inheritance hierarchy | P1 | SVGView `SVGNode→SVGShape→SVGCircle`, `SVGPaint→SVGGradient→SVGLinearGradient`; Quick `NSObject→_ExampleBase→ExampleBase→Example` |
| Q2 | Generic class inheriting non-generic class | P1 | GRDB `TableAlias<TRowDecoder> : TableAliasBase` |
| Q3 | Self-referencing optional class property | P2 | SVGView `SVGNode.Clip: SVGNode?`, `SVGNode.Mask: SVGNode?` |

**Q1: Deep Class Hierarchy** — Current tests have 2-level (`Animal→Dog`). A 3+ level hierarchy tests that property/method dispatch resolves correctly through multiple inheritance layers, and that the C# class chain mirrors the Swift hierarchy.

**Q2: Generic Child of Non-Generic Parent** — The `TableAlias<TRowDecoder>` inherits from `TableAliasBase`. The constructor chains via `base()`, and `GetTypeMetadata()` takes the generic parameter. This pattern combines generics with class inheritance — two features that interact in the ABI.

---

### GROUP R: Existential & Type Erasure (3 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| R1 | Property returning `Any` via `ExistentialContainer0` | P1 | DifferenceKit `AnyDifferentiable.Base` — unconstrained existential, marshalled through `ExistentialContainer0` to `object` |
| R2 | Property returning `AnyHashable` (type-erased Hashable) | P2 | DifferenceKit `AnyDifferentiable.DifferenceIdentifier: AnyHashable` |
| R3 | Custom `StringInterpolation` type (ExpressibleByStringInterpolation) | P2 | CocoaLumberjackSwift `DDLogMessageFormat` — nested `StringInterpolation` struct with 14+ `AppendInterpolation` overloads |

**R1: ExistentialContainer0** — The test framework exercises `ExistentialContainer1` (single-protocol existential) but not `ExistentialContainer0` (unconstrained `Any`). The `AnyDifferentiable.Base` property returns `Any`, which is marshalled as `ExistentialContainer0→object`. This is a distinct container layout.

---

### GROUP S: Failable Initializers & Error Patterns (2 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| S1 | Failable init (`init?`) projected as `TryCreate` with `out` param | P0 | Valet `SharedGroupIdentifier.TryCreate`, `Identifier.TryCreate`; NVActivityIndicatorView `TryCreate(coder:)` |
| S2 | Typed error extraction (`SBW_ExtractTypedError_*`) from traditional `throws` | P1 | Valet `SecureEnclaveValet.SetObject` — extracts `SwiftException<KeychainError>` |

**S1: Failable Init as TryCreate** — The `Initializers.disabled/Failable.swift` files exist but are disabled. Valet and NVActivityIndicatorView demonstrate this is a real, working pattern in the generator. The projection uses `SwiftOptional<Self>`, checks enum tag for None/Some, copies payload if Some. This should be enabled and runtime-tested — it's one of the highest-value gaps.

**S2: Typed Error Extraction** — Current `TypedThrows.swift` tests Swift 6.0 `throws(ErrorType)` syntax. Valet uses traditional `throws` with catch-and-extract at the wrapper level (`SBW_ExtractTypedError_Valet_KeychainError`). The error is a simple C# enum (`KeychainError : long`). This is the more common pattern in real libraries.

---

### GROUP T: ObjC Interop Patterns (2 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| T1 | ObjC-rooted Swift class (`UIView`/`NSObject` subclass) | P1 | NVActivityIndicatorView `: UIView`; SVGView `SVGHelper : NSObject` — `objcRooted=true`, uses `NativeHandle` not `SwiftSafeHandle` |
| T2 | Extension methods on Foundation/UIKit types | P2 | Lottie `UIColorLottieExtensions.GetLottieColorValue(this UIColor)`; GRDB 7 extension classes on NSData/NSDate/NSString/etc. |

**T1: ObjC-Rooted Swift Class** — These use `ObjCRuntime.NativeHandle` instead of `SwiftSafeHandle`, different marshalling paths, and ObjC-style retain/release. The `ObjCInterop.disabled/` files exist but are disabled. The generator handles this pattern — NVActivityIndicatorView compiles. Consider enabling a minimal test.

---

### GROUP U: Static & Property Patterns (2 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| U1 | Struct as pure static constants namespace (all static, no instance members) | P1 | KeychainSwift `KeychainSwiftConstants` — 8 static string properties, zero instance members |
| U2 | Multiple typed static mutable properties on a class | P1 | NVActivityIndicatorView — 6 static properties of different types (enum, UIColor, CGFloat, Optional<String>, closure) |

**U1: Static Constants Struct** — A struct with no instance methods and only static computed properties. Distinct from `StaticStructSingleton` (which has instance members and `static let` returning Self). The `KeychainSwiftConstants` pattern is purely a namespace for string constants, each returned via `Utf8Slice`.

---

### GROUP V: Miscellaneous (2 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| V1 | Method overloading by parameter type (3+ overloads, same name) | P1 | KeychainSwift `Set(string/byte[]/bool, key)` — 3 overloads; Mixpanel `Identify` — 3 overloads; Mixpanel `CreateAlias` — 4 overloads |
| V2 | `@available` / `[SupportedOSPlatform]` annotations | P2 | Parchment — `[SupportedOSPlatform("ios14.0")]` from Swift `@available(iOS 14.0, *)` |

**V1: Method Overloading** — The test framework has methods with different names. Real libraries heavily use overloaded method names differing only in parameter types. The generator must correctly disambiguate Swift mangled names and route each overload to a different P/Invoke entry. KeychainSwift's `Set` has 3 overloads where the first param type changes (String, Data→byte[], Bool) — each requires different marshalling.

---

### GROUP W: CoreGraphics / UIKit Types (3 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| W1 | CGFloat/CGSize/CGRect/CGPoint as params and properties | P0 | FSPagerView (`CGSize ItemSize`, `CGRect frame` constructor), AMPopTip (`CGFloat borderWidth/cornerRadius`, `CGSize ShadowOffset`, `CGRect From`), Nuke (`CGSize` in `ImageProcessors.Resize`), SwipeCellKit (`CGPoint` in `HitTest`) |
| W2 | UIEdgeInsets as property and builder param | P1 | AMPopTip `EdgeInsets`, SwipeCellKit `LayoutMargins`, SkeletonView `SetPadding(UIEdgeInsets)` |
| W3 | Swift `Float` (32-bit) properties distinct from CGFloat/Double | P2 | AMPopTip `ShadowRadius: Float`, `ShadowOpacity: Float` — `Sf` suffix in mangled name |

**W1: CoreGraphics Types** — The runtime defines `Swift.CGPoint`, `Swift.CGSize`, `Swift.CGRect` (at `src/Swift.Runtime/src/Swift/CGPoint.cs`, `CGSize.cs`, `CGRect.cs`) with implicit conversions to/from `CoreGraphics.*` types. These types are **fully implemented in the runtime but have zero test coverage** — no Swift test source uses them, so they never appear in generated bindings. Every UI-oriented library uses these extensively. This is arguably the highest-value P0 gap.

```swift
// FSPagerView / AMPopTip pattern
public struct LayoutConfig {
    public var origin: CGPoint
    public var size: CGSize
    public var frame: CGRect
    public var spacing: CGFloat  // projected as Double

    public init(origin: CGPoint, size: CGSize) {
        self.origin = origin
        self.size = size
        self.frame = CGRect(origin: origin, size: size)
        self.spacing = 0
    }
}
```

---

### GROUP X: AsyncStream & Concurrency (2 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| X1 | AsyncStream property (`IAsyncEnumerable<T>`) | P0 | Nuke `ImageTask.progress`, `ImageTask.previews`, `ImageTask.events` — full `SwiftAsyncStream<T>` with callback-based iteration, emitted but never tested |
| X2 | Method with multiple closure params (one nullable, one required) | P1 | StripePayments `CollectBankAccountForPayment(onEvent: Action<T>?, completion: Action<T?, U?>)` |

**X1: AsyncStream Properties** — The generator has `AsyncStreamEmitter.cs` and the runtime has `SwiftAsyncStream.cs`. Nuke's `ImageTask` has three `AsyncStream<T>` properties emitted as `IAsyncEnumerable<T>` with `[UnmanagedCallersOnly]` element/completion callbacks. This entire feature (AsyncStream → IAsyncEnumerable) has **zero test coverage** despite being fully implemented in both generator and runtime. A regression would be completely undetectable.

---

### GROUP Y: Struct & Class Modifiers (3 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| Y1 | `nonmutating set` property on struct | P1 | SnapKit `ConstraintViewDSL.contentHuggingHorizontalPriority` — setter doesn't mutate struct memory, mutates underlying UIView |
| Y2 | `@_hasMissingDesignatedInitializers` class (no public init) | P1 | SnapKit `ConstraintItem`, `Constraint`; Nuke `ImageTask` — classes obtainable only via factory/return, no constructor emitted |
| Y3 | Enum with CGFloat raw value + failable init | P2 | SwipeCellKit `SwipeActionsOrientation: CGFloat` — `FromRawValue(double)` returns optional |

**Y1: Nonmutating Set** — A struct property declared `{ get; nonmutating set }` in Swift means the setter modifies external state (e.g., the underlying UIView) rather than the struct's own memory. The generator correctly emits read-write C# properties, but the ABI behavior is distinct — the setter doesn't write back to the struct's payload. Not tested.

**Y2: No Public Init Classes** — Classes annotated `@_hasMissingDesignatedInitializers` have NO public constructors. The generator correctly omits constructors — instances are obtained only as return values from other APIs. Tests should verify these types work correctly when received from factory methods without ever calling `new`.

---

### GROUP Z: Cross-Module & Multi-Module (2 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| Z1 | Cross-module type extension (class defined in Module A, extended in Module B) | P1 | StripePayments extends `StripeCore.STPAPIClient` with 20+ new methods — emitted as separate `partial class` |
| Z2 | Constructor/method taking `any Swift.Error` as optional existential | P1 | StripePayments `STPCustomerDeserializer(data: byte[]?, urlResponse: NSUrlResponse?, error: AnyError?)` |

**Z1: Cross-Module Extensions** — StripePayments adds methods to `STPAPIClient` (defined in StripeCore). The generator emits a second `partial class` declaration in the StripePayments namespace. The Swift wrapper `import`s StripeCore and the P/Invoke targets the StripePayments dylib. This is untestable in a single-module test library, but the generator mechanics (partial class emission, cross-module symbol resolution) could be validated.

---

### GROUP AA: Async Tuple Returns & Result Types (2 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| AA1 | Async method returning tuple with existential error `(Enum, Class?, AnyError?)` | P1 | StripePayments `ConfirmPaymentIntentAsync` — returns `Task<(STPPaymentHandlerActionStatus, STPPaymentIntent?, Swift.AnyError?)>` |
| AA2 | `Result<T, E>` as enum associated value | P1 | Nuke `ImageTask.Event.finished(Result<ImageResponse, ImagePipeline.Error>)` — bound generic stdlib type as enum payload |

**AA1: Async Tuple with Existential** — An async method returning a 3-element tuple where one element is an optional protocol existential (`any Swift.Error` → `AnyError?`). Each tuple element requires different unmarshalling: enum cast from `long`, `SwiftOptional<T>` for the class, `SwiftOptional<ExistentialContainer1>` for the error. This combines async + tuple + existential — three features interacting simultaneously.

**AA2: Result as Enum Payload** — `Swift.Result<T, E>` used as an associated value in an enum case. The generator handles this as a bound generic standard library type, emitting `SwiftResult<ImageResponse, ImagePipeline.Error>`. Distinct from simple enum payloads because it requires generic type resolution for a standard library type.

---

### GROUP AB: Extension Methods & Protocol Chains (2 patterns)

| # | Pattern | Priority | Evidence |
|---|---------|----------|----------|
| AB1 | Extension methods on foreign UIKit types (emitted as C# extension classes) | P0 | SwiftyGif `UIImageSwiftyGifExtensions`, `UIImageViewSwiftyGifExtensions` — 15+ extension methods on `UIImage`/`UIImageView` |
| AB2 | 3-level protocol inheritance chain with extension-only defaults | P1 | SnapKit `ConstraintDSL → ConstraintBasicAttributesDSL → ConstraintAttributesDSL` — all members throw `NotSupportedException` |

**AB1: Extension Methods on Foreign Types** — SwiftyGif adds 15+ methods to `UIImageView` emitted as a C# static extension method class. Each method gets a `@_silgen_name` wrapper function in the Swift wrapper that casts `UnsafeMutableRawPointer` to the UIKit type. A disabled test file exists at `TestFramework/Sources/SwiftBindingsTestLib/Foundation/Extensions.swift.disabled` but was never completed. This is implemented and working across multiple libraries.

---

## Coverage Matrix: New Libraries → TestFramework

| Library | Key New Patterns Found | # New |
|---------|----------------------|-------|
| **GRDB** | Caseless enum namespace, generic child + non-generic parent class, conditional extensions on Foundation, massive throws surface, RawRepresentable struct, cursor/iterator | 5 |
| **Lottie** | Collection in enum payload, nested enum-in-enum-with-AVs, all-payload enum, nullable closure property, static factory with existential param, protocol default impl | 6 |
| **Valet** | Failable init → TryCreate, typed error extraction, CaseIterable AllCases | 3 |
| **Quick** | Generic class + protocol conformance, generic optional property, 3+ level class hierarchy, protocol proxy vtable registration | 3 |
| **Parchment** | Multiple protocol conformance, protocol with existential params, `@available` annotations, SupportedOSPlatform | 3 |
| **DifferenceKit** | Multi-type-parameter generics, ExistentialContainer0 (Any), generic constructor with PWT, AnyHashable return, Collection conformance | 5 |
| **KeychainSwift** | Static constants struct, method overloading by type | 2 |
| **Mixpanel** | Dictionary with existential values, optional closure param, many-default-param methods, method overload families | 3 |
| **NVActivityIndicatorView** | ObjC-rooted class, static closure property, failable init on ObjC class, large simple enum | 3 |
| **SVGView** | Deep class hierarchy, existential array property, array of class property, self-referencing class property, marker protocol, nested struct in class | 5 |
| **CocoaLumberjackSwift** | ExpressibleByStringInterpolation, nested structs 2-deep | 2 |
| **FirebaseCore/Ext** | Non-contiguous enum values (ObjC), DisableDefaultCtor (no parameterless init) | 1 |
| **SkeletonView** | ObjC-rooted class, builder with CGFloat/UIEdgeInsets params, generic protocol, opaque type marker | 2 |
| **SwipeCellKit** | CGPoint/CGRect params, UIEdgeInsets property, nested enum+struct in struct, enum with CGFloat rawValue, nonmutating set | 3 |
| **AMPopTip** | CGFloat/CGSize/CGRect properties+params, UIFont/UIColor properties, NSAttributedString param, Float type, method overloading with generic | 4 |
| **FSPagerView** | CGSize property, CGRect constructor param, CGFloat properties | 1 |
| **SwiftyGif** | Extension methods on UIKit types (full C# extension class emission) | 1 |
| **TinyConstraints** | Protocol extension defaults (throw stubs), extension on foreign types | 1 |
| **StripeCore** | Protocol with mutable property + extension default, cross-module type declarations | 2 |
| **StripePayments** | Cross-module extension, async tuple with existential, constructor with existential array, multiple closure params | 4 |
| **Nuke** | AsyncStream → IAsyncEnumerable (implemented, untested), Result as enum payload, @MainActor @Sendable closure, async property | 3 |
| **Alamofire** | Combine publisher as opaque generic struct | 1 |
| **SnapKit** | nonmutating set, 3-level protocol chain, @_hasMissingDesignatedInitializers, Strideable conformance, @discardableResult chaining | 3 |

---

## Implementation Priorities

### P0: Untested Code Paths in Generator/Runtime (9 patterns)

These exercise code paths that have zero test coverage — a regression would be undetectable.

| # | Pattern | Effort | Notes |
|---|---------|--------|-------|
| W1 | CGFloat/CGSize/CGRect/CGPoint params & properties | Medium | Runtime types exist (`Swift.CGPoint/CGSize/CGRect`) but no test Swift source uses them. Add to test library + C# tests. Pervasive across ALL UI libraries. |
| X1 | AsyncStream property (`IAsyncEnumerable<T>`) | Medium | Generator (`AsyncStreamEmitter.cs`) + runtime (`SwiftAsyncStream.cs`) fully implemented. Nuke uses it. Zero tests. |
| AB1 | Extension methods on foreign UIKit types | Medium | Disabled test at `Extensions.swift.disabled`. SwiftyGif emits 15+ extension methods on `UIImageView`. Working in generator. |
| S1 | Failable init → TryCreate | Low | Swift source already exists in `.disabled`; enable + add C# tests |
| L1 | Collection in enum payload | Medium | New Swift enum case with `[String]` payload |
| M1 | Multi-type-parameter generic struct | Medium | New `GenericPair<T1, T2>` with separate metadata |
| M2 | Generic constructor with PWT | Medium | Extend generic constraint tests to include constructor |
| P1 | Nullable closure property (get/set) | Medium | New property on existing closure consumer class |
| O1 | Dictionary property (get/set) | Low | Extend `HeaderMap` to have dict property, not just constructor |

### P1: Important Real-World Patterns (24 patterns)

Common patterns seen across multiple libraries.

| # | Pattern | Effort |
|---|---------|--------|
| L2 | Nested enum-with-AVs in enum-with-AVs | Medium |
| L3 | All-payload enum | Low |
| L4 | Mixed payload enum with heterogeneous types | Medium |
| L5 | Caseless enum as namespace | Low |
| M3 | Generic class + protocol conformance | Medium |
| M4 | Generic struct with optional generic property | Medium |
| N1 | Protocol default implementation | Low |
| N2 | Protocol methods with existential params | Medium |
| N3 | Multiple protocol conformance | Medium |
| O2 | Dictionary with existential values | Medium |
| O3 | Array of class instances property | Low |
| O4 | Existential array property | Medium |
| P2 | Static closure property | Medium |
| P3 | Optional closure parameter | Low |
| Q1 | 3+ level class hierarchy | Medium |
| Q2 | Generic class inheriting non-generic class | Medium |
| R1 | ExistentialContainer0 (Any return) | Medium |
| S2 | Typed error extraction from `throws` | Medium |
| T1 | ObjC-rooted Swift class | High (requires ObjC infra) |
| U1 | Static constants struct | Low |
| U2 | Multiple typed static mutable properties | Low |
| V1 | Method overloading by type | Medium |
| W2 | UIEdgeInsets as property and builder param | Medium |
| X2 | Method with multiple closure params | Medium |
| Y1 | `nonmutating set` property on struct | Medium |
| Y2 | `@_hasMissingDesignatedInitializers` class (no public init) | Low |
| Z1 | Cross-module type extension | High (multi-module) |
| Z2 | Constructor taking `any Swift.Error` existential | Medium |
| AA1 | Async method returning tuple with existential error | Medium |
| AA2 | `Result<T, E>` as enum associated value | Medium |
| AB2 | 3-level protocol inheritance chain with extension defaults | Medium |

### P2: Coverage Depth / Edge Cases (11 patterns)

Nice-to-have for completeness; lower regression risk.

| # | Pattern | Effort |
|---|---------|--------|
| L6 | Nested enum with String rawValue + CaseIterable | Low |
| N4 | Marker protocol (empty interface) | Low |
| N5 | Protocol with associated type → generic interface | Low |
| O5 | CaseIterable AllCases property | Low |
| Q3 | Self-referencing optional class property | Low |
| R2 | AnyHashable return type | Medium |
| R3 | ExpressibleByStringInterpolation | High |
| V2 | `@available` / `[SupportedOSPlatform]` | Low |
| W3 | Swift `Float` (32-bit) properties | Low |
| Y3 | Enum with CGFloat raw value + failable init | Low |

---

## Patterns NOT Worth Adding (and why)

| Pattern | Why Skip |
|---------|----------|
| ObjC `[Protocol, Model]` delegate pattern | ObjC binding path, not Swift ABI |
| ObjC `[Category]` methods on UIKit types | ObjC binding path via sharpie |
| ObjC `[Native][Flags]` bitmask enum | ObjC enum, not Swift OptionSet |
| `[DisableDefaultCtor]` | ObjC API design, not ABI pattern |
| `INSCopying` conformance | Foundation protocol, not user-facing |
| `[Bind("isXxx")]` getter rename | ObjC naming convention, not Swift |
| `[Field]` constants | ObjC static fields, not Swift |
| `@Sendable` closure annotation | Compile-time concurrency check, no ABI impact |
| `Result<T, Error>` in closure | Result type in closures deferred (known limitation) |
| `some Protocol` opaque params | Monomorphized at call site, maps to generic constraints in ABI — same as existing generic tests |
| `@dynamicMemberLookup` | Compile-time feature, emits concrete subscripts (already covered) |
| `final class` → `sealed` | Generator doesn't emit `sealed` currently — cosmetic |
| Struct conforming to Collection (StartIndex/EndIndex) | Projection is partial (subscript skipped), low value |
| Conditional extension on external types | Extension method emission works; the novelty is the target type, not the mechanism |
| `@discardableResult` | No C# equivalent — method returns are always available. Same as regular return. |
| Strideable / ExpressibleByFloatLiteral conformance | Protocol conformance plumbing, not distinct ABI pattern |
| Combine Publisher types as opaque structs | Emitted but unusable without Combine interop — not a binding pattern |
| `@IBDesignable` / `@IBInspectable` | Interface Builder attributes, no ABI impact |
| KVO-compatible properties | ObjC runtime feature, not Swift ABI |
| Nested protocol inside class | Not found in any analyzed library — may not occur in practice |
| ObjC override property wrapper routing | Internal generator mechanism, not user-facing pattern |

---

## Estimated Size

- **New Swift source**: ~600-800 lines across ~12-15 new/extended files
- **New C# tests**: ~1200-1800 lines across ~15-18 test files
- **Total**: ~1800-2600 lines
- **Highest-value subset (P0 only)**: ~350 lines Swift + ~500 lines C# = ~850 lines

---

## Summary

Pass 1 identified 25 patterns, all now implemented. Pass 2 identifies **44 additional patterns** across 14 groups, from analysis of **26 libraries**:

| Group | Patterns | P0 | P1 | P2 |
|-------|----------|----|----|-----|
| L: Enums | 6 | 1 | 4 | 1 |
| M: Generics | 5 | 2 | 2 | 1 |
| N: Protocols | 5 | 1 | 2 | 2 |
| O: Collections | 5 | 1 | 3 | 1 |
| P: Closures | 3 | 1 | 2 | 0 |
| Q: Inheritance | 3 | 0 | 2 | 1 |
| R: Type Erasure | 3 | 0 | 1 | 2 |
| S: Init/Error | 2 | 1 | 1 | 0 |
| T: ObjC Interop | 2 | 0 | 1 | 1 |
| U: Static/Props | 2 | 0 | 2 | 0 |
| V: Misc | 2 | 0 | 1 | 1 |
| W: CoreGraphics/UIKit | 3 | 1 | 1 | 1 |
| X: AsyncStream/Concurrency | 2 | 1 | 1 | 0 |
| Y: Struct/Class Modifiers | 3 | 0 | 2 | 1 |
| Z: Cross-Module | 2 | 0 | 2 | 0 |
| AA: Async Tuple/Result | 2 | 0 | 2 | 0 |
| AB: Extension/Protocol Chain | 2 | 1 | 1 | 0 |
| **Total** | **52** | **9** | **30** | **11** |

The **9 P0 patterns** should be implemented first — they exercise code paths with zero test coverage where a regression would be completely undetectable:

1. **W1**: CGFloat/CGSize/CGRect/CGPoint — runtime types exist, never tested
2. **X1**: AsyncStream → IAsyncEnumerable — full implementation in generator+runtime, never tested
3. **AB1**: Extension methods on foreign types — disabled test exists, working in generator
4. **S1**: Failable init → TryCreate — disabled Swift source exists
5. **L1**: Collection in enum payload — distinct marshalling path
6. **M1**: Multi-type-parameter generics — separate metadata threading
7. **M2**: Generic constructor with PWT — distinct code path
8. **P1**: Nullable closure property — bidirectional nullable closure marshalling
9. **O1**: Dictionary property get/set — distinct from constructor-only

---

> **See also:** [Generator Skip Analysis](generator-skip-analysis.md) — analysis of what the generator doesn't emit across all 90 validation targets, with prioritized recommendations for which unsupported patterns would unlock the most API surface.
