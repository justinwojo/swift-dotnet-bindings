# Binding Quality Review — February 2026

## Executive Summary

This review evaluates C# bindings generated for 18 real-world Swift libraries (32 validation targets) across 10 quality dimensions. The generator produces structurally sound bindings that compile and correctly represent the vast majority of Swift type hierarchies, with genuinely impressive infrastructure for async/await bridging, collection projection, and nullable type handling. The best bindings (Mappedin, SmartCardIO, MicroblinkPlatform) achieve near-native C# ergonomics with zero `AnyType` fallbacks, full async coverage, and clean collection projections — demonstrating that the generator is capable of production-quality output when the input library's type surface aligns well with the projection pipeline.

However, the review reveals a consistent pattern: the generator excels at projecting data models (properties, constructors, simple methods) but struggles with the _verbs_ — the core workflows that define how each library is actually used. Libraries whose primary API relies on closure-based transactions (GRDB's `read`/`write`), protocol-associated-type chains (RxSwift's operators), fluent builder patterns returning `Self` (SnapKit, Kingfisher), or UIKit extension methods (SkeletonView's `showSkeleton()`) produce bindings that are structurally complete but functionally incomplete. The average overall usability score is 3.0/5, with a range from 2.0 (RxSwift, Alamofire, GRDB) to 4.5 (SmartCardIO).

The three highest-impact areas for improvement are: (1) projecting `Self`-returning protocol methods and fluent builders without degrading to `AnyType`, which would fix builder APIs across SnapKit, Kingfisher, and KeychainAccess; (2) supporting closure parameters with non-trivial types (enums, protocol existentials) in method signatures, which would unlock the core workflows of Alamofire, GRDB, Stripe, and RxSwift; and (3) completing the type database for common Apple SDK types (`IndexPath`, `SecTrust`, `AsyncStream`, `CGColorSpace`) that cause `AnyType` cascades in otherwise well-bound libraries.

## Scorecard Matrix

Scores are 1-5 (1=unusable, 2=significant gaps, 3=workable with friction, 4=good with minor issues, 5=excellent). N/A entries are excluded from averages.

| Library | Naming | TypeFidelity | Nullability | Collections | Async | ErrorHandling | Protocols | Noise | Completeness | Overall | **Avg** |
|---------|:------:|:-----------:|:----------:|:----------:|:-----:|:------------:|:---------:|:-----:|:-----------:|:-------:|:-------:|
| Nuke | 3 | 4 | 4 | 2 | 5 | 3 | 4 | 3 | 4 | 3 | **3.50** |
| Lottie | 4 | 3 | 4 | 4 | 5 | 4 | 3 | 4 | 4 | 3.5 | **3.85** |
| Alamofire | 3 | 3 | 4 | 4 | 2 | 3 | 3 | 3 | 2 | 2 | **2.90** |
| Kingfisher | 3 | 2 | 3 | 4 | 4 | 3 | 3 | 3 | 3 | 3 | **3.10** |
| SnapKit | 3 | 4 | 4 | 5 | 2 | 3 | 3 | 3 | 3 | 2 | **3.20** |
| CryptoSwift | 3 | 3 | 4 | 4 | N/A | 4 | 3 | 3 | 3 | 3 | **3.33** |
| GRDB | 3 | 3 | 4 | 3 | 2 | 4 | 3 | 3 | 2 | 2 | **2.90** |
| KeychainAccess | 3 | 4 | 5 | 4 | 3 | 4 | 2 | 3 | 3 | 3 | **3.40** |
| RxSwift | 2 | 2 | 4 | 2 | 1 | 3 | 2 | 3 | 2 | 2 | **2.30** |
| Starscream | 3 | 3 | 4 | 4 | 4 | 3 | 2 | 3 | 3 | 3 | **3.20** |
| SkeletonView | 3 | 3 | 4 | 2 | 4 | 2 | 3 | 3 | 3 | 3 | **3.00** |
| Mixpanel | 3 | 3 | 4 | 3 | 4 | 2 | 3 | 3 | 2 | 2 | **2.90** |
| BlinkID | 4 | 3 | 4 | 4 | 4 | 3 | 3 | 3 | 4 | 3.5 | **3.55** |
| Stripe (14 modules) | 3 | 3 | 4 | 4 | 3 | 3 | 3 | 3 | 3 | 3 | **3.20** |
| SmartCardIO | 5 | 4 | 5 | 5 | N/A | 5 | 4 | 4 | 4 | 4 | **4.44** |
| MicroblinkPlatform | 4 | 5 | 5 | 4 | N/A | 3 | 5 | 4 | 4 | 4 | **4.22** |
| BlinkIDUX | 4 | 3 | 4 | 4 | 5 | 4 | 3 | 3 | 3 | 3 | **3.60** |
| Mappedin | 4 | 4 | 5 | 5 | 5 | 4 | 4 | 3 | 5 | 4 | **4.30** |
| **Column Avg** | **3.33** | **3.28** | **4.17** | **3.72** | **3.53** | **3.33** | **3.11** | **3.17** | **3.17** | **2.94** | **3.37** |

**Top 3 Libraries**: SmartCardIO (4.44), Mappedin (4.30), MicroblinkPlatform (4.22)
**Bottom 3 Libraries**: RxSwift (2.30), Alamofire (2.90), GRDB (2.90)
**Strongest Category**: Nullability (4.17 avg) — consistently good across all libraries
**Weakest Category**: Overall Usability (2.94 avg) — the gap between structural completeness and workflow usability

## Cross-Library Patterns

### What Works Well (patterns scoring 4-5 consistently)

**Nullability (avg 4.17)** is the most consistently strong category. Every generated file begins with `#nullable enable`. Optional Swift types are correctly projected as `T?` for value types and nullable reference annotations for classes. The internal `SwiftOptional<T>` to `T?` conversion is transparent at the public API boundary. Example from KeychainAccess:
```csharp
public string? Label
{
    get { using var __ret = Label_Get(); return ((SwiftString?)__ret)?.ToString(); }
    set { using var __val = (value is {} valueVal
        ? SwiftOptional<SwiftString>.NewSome(new SwiftString(valueVal))
        : SwiftOptional<SwiftString>.NewNone()); Label_Set(__val); }
}
```
The `_optbuf` wrapper pattern correctly handles the extra-inhabitant encoding for `Optional<String>` returns where CallConvSwift would truncate the discriminator.

**Async/Await bridging (avg 3.53, but 5/5 when present)** is the most technically impressive feature. Libraries with async surfaces (Lottie, Nuke, Mappedin, BlinkIDUX) consistently achieve 4-5. The pattern includes `Task<T>` returns, `Async` suffix naming, `CancellationToken` with default values, cooperative cancellation via `SBW_CancelTask`, proper `TaskCreationOptions.RunContinuationsAsynchronously`, and cleanup in `finally` blocks. The 24 async methods in Mappedin with protocol-existential parameters and optional returns demonstrate the pattern scales to complex scenarios. Example from Nuke:
```csharp
public Task<UIKit.UIImage> ImageAsync(Foundation.NSUrl request,
    System.Threading.CancellationToken cancellationToken = default)
```

**Collection projection (avg 3.72)** handles the common cases well. `Array<T>` returns as `IReadOnlyList<T>`, parameters accept `IEnumerable<T>`, dictionaries project to `IReadOnlyDictionary<K,V>` / `IDictionary<K,V>`. Nested collections work — Mappedin demonstrates `IReadOnlyDictionary<K, IReadOnlyList<string>>?` with correct nested projection lambdas, and CryptoSwift handles `IReadOnlyList<IReadOnlyList<uint>>` for nested array round-trips.

**Lazy singleton enum caching** is a universally positive pattern. Every library's no-payload enum cases use `Lazy<T>`-backed singletons with `_isCachedSingleton` guarding against accidental disposal. This is both thread-safe and memory-efficient — better than what Xamarin.iOS bindings typically achieved for enum-like constants.

**TryGet pattern for discriminated unions** is consistently well-implemented across all libraries. Swift enums with associated values get `CaseTag` enum, `Tag` property, factory methods, and `bool TryGetX([MaybeNullWhen(false)] out T value)` extractors. This is a natural C# pattern that works with nullable analysis.

### Common Pain Points (patterns scoring 1-2 consistently)

**`Self`-returning protocol methods degrade to `AnyType`** (affects Kingfisher, SnapKit, KeychainAccess, RxSwift). When a Swift protocol method returns `Self` (the fluent builder pattern), the generator cannot resolve the concrete return type and falls back to `Swift.AnyType`. In Kingfisher, this makes the entire `IKFOptionSetter` builder interface unusable — all 30+ methods return `AnyType`. In SnapKit, the fluent constraint builder chain is broken. This is the single most impactful cross-library issue because builder/fluent APIs are extremely common in Swift libraries.

**Closure parameters with complex types block method emission** (affects Alamofire, GRDB, Stripe, RxSwift, SkeletonView). Methods taking closures like `(Database) throws -> T`, `(STPPaymentHandlerActionStatus, STPPaymentIntent?, NSError?) -> Void`, or `(ConstraintMaker) -> Void` are either skipped entirely or have their closure parameters degraded. GRDB's `read`/`write`, Alamofire's `responseData`/`responseString`, and Stripe's `confirmPayment` are all missing for this reason. These are the core workflows of each library — their absence makes the bindings structurally complete but functionally incomplete.

**Empty protocol interfaces** (affects Starscream, Lottie, Alamofire, SnapKit, CryptoSwift, RxSwift). Critical protocols lose their members during generation. Starscream's `IWebSocketDelegate` (the primary event callback) is completely empty. Lottie's `IAnimationFontProvider` and `IAnimationImageProvider` are empty. Alamofire's `IRequestAdapter` and `IParameterEncoding` are empty. When the most important protocol in a library has zero callable members, the binding loses its primary integration point.

**Class inheritance hierarchy is flattened** (affects Alamofire, SnapKit, and others). Swift subclass relationships (`DataRequest : Request`, `ConstraintMakerExtendable : ConstraintMakerRelatable`) are not modeled in C#. Each class is emitted independently. This means base-class members are inaccessible, polymorphic casting fails, and fluent chains that depend on inheritance break.

**UIKit extension methods are not projected** (affects SkeletonView, SnapKit, Kingfisher). Libraries that add methods to `UIView` via Swift extensions (`view.showSkeleton()`, `view.snp.makeConstraints(...)`, `view.kf.setImage(...)`) cannot express these extensions in the binding. The internal types are bound but the entry point from UIKit is missing.

### Mixed Results (varies by library)

**Type fidelity (avg 3.28)** ranges from 5 (MicroblinkPlatform — perfect ObjC bridging) to 2 (Kingfisher, RxSwift — extensive `AnyType` leakage). Self-contained libraries with no cross-module dependencies and simple type hierarchies score well. Libraries with protocol-associated types, `Self` returns, or Security framework types (`SecTrust`, `SecCertificate`) score poorly.

**Completeness (avg 3.17)** depends heavily on API design patterns. Data-model-heavy libraries (BlinkID: 4, Mappedin: 5) score high because their API surface is properties and simple methods. Workflow-heavy libraries (GRDB: 2, RxSwift: 2, Mixpanel: 2) score low because their API surface centers on closure-based operations the generator cannot fully project.

**Protocol/Interface usability (avg 3.11)** is bimodal. Simple protocols with concrete types (SmartCardIO: 4, MicroblinkPlatform: 5, Mappedin: 4) produce clean, implementable C# interfaces. Protocols with associated types, `Self` returns, or complex method signatures (RxSwift: 2, Starscream: 2, KeychainAccess: 2) produce empty or degraded interfaces. The proxy infrastructure (vtable, bidirectional dispatch, existential container marshalling) is consistently impressive — the gap is in what members get wired through to the interface.

**The `Get` prefix on fluent methods** is consistently wrong but not consistently impactful. Every library with builder-pattern methods (SnapKit, KeychainAccess, Kingfisher, GRDB) gets `GetEqualTo()`, `GetAccessibility()`, `GetTargetCache()` instead of the natural `EqualTo()`, `WithAccessibility()`, `TargetCache()`. This is a naming heuristic issue — the generator applies the `Get` prefix to computed properties, but Swift fluent methods that return `Self` are treated identically.

## Per-Library Deep Dives

### Nuke
**Scores**: Naming 3, TypeFidelity 4, Nullability 4, Collections 2, Async 5, ErrorHandling 3, Protocols 4, Noise 3, Completeness 4, Overall 3 — **Avg 3.50**

**Highlights**: The async image loading surface is excellent — `pipeline.ImageAsync(request, cancellationToken)` returns `Task<UIImage>` with full cancellation support. The `(NSData, NSUrlResponse?)` tuple return on `DataAsync` is idiomatic. Protocol proxies for `IImageCaching`, `IDataCaching`, and `IImagePipelineDelegate` are fully bidirectional. Default parameter overloads give 4 constructors for `ImageCache` from a single Swift init.

**Top issues**: `ConfigurationValue` property rename (nested type collision — affects every access to pipeline configuration). `ImageRequest` only has `init(stringLiteral:)` — the primary `init(url: URL)` constructor is missing. `DataLoader.Error.StatusCodeUnacceptable` uses `value0` instead of `statusCode`. `SwiftDictionary<SwiftString, AnyType>` leaks in `CoreImageFilter.Error.FailedToCreateFilter`.

**Specific example of friction**:
```csharp
// Must use ConfigurationValue instead of Configuration
var config = pipeline.ConfigurationValue;
// Cannot create request from URL — only string literal
var request = new ImageRequest("https://...");  // no ImageRequest(NSUrl) overload
```

### Lottie
**Scores**: Naming 4, TypeFidelity 3, Nullability 4, Collections 4, Async 5, ErrorHandling 4, Protocols 3, Noise 4, Completeness 4, Overall 3.5 — **Avg 3.85**

**Highlights**: The async bridge is production-grade — 19 `PlayAsync` overloads across 3 view types plus 12 static `*Async` loading methods. `TaskCreationOptions.RunContinuationsAsynchronously` prevents deadlocks. 1,013 XML doc comments carried through from Swift symbol graph. Failable initializer `AssetType.TryCreate(NSData, out AssetType)` is idiomatic C#.

**Top issues**: `SwiftOptional<SwiftString>` and `SwiftOptional<double>` leak into tuple parameters of `LottiePlaybackMode.FromProgress/FromFrame/FromMarker` — the tuple element projection pipeline is not applied. `IAnimationFontProvider` and `IAnimationImageProvider` interfaces are empty (Swift protocols have methods but they are not reflected). `AnyType` in 22 locations, mainly `IInterpolatable` and `ISpatialInterpolatable`.

**Specific example**:
```csharp
// Tuple elements bypass projection — SwiftOptional leaks
public static unsafe LottiePlaybackMode FromProgress(
    (Swift.SwiftOptional<double>, double toProgress, LottieLoopMode loopMode) value0)
// Should be: (double?, double toProgress, LottieLoopMode loopMode)
```

### Alamofire
**Scores**: Naming 3, TypeFidelity 3, Nullability 4, Collections 4, Async 2, ErrorHandling 3, Protocols 3, Noise 3, Completeness 2, Overall 2 — **Avg 2.90**

**Highlights**: `AFError` is comprehensively bound with 17 case constructors, `CaseTag`, and `TryGet*` extractors. `HTTPHeaders.Dictionary` returns `IReadOnlyDictionary<string, string>` with proper key/value conversion. `HTTPHeaders(IDictionary<string, string>)` accepts standard .NET dictionaries.

**Top issues**: The core request/response workflow is missing — no `Session.request(url, method:, parameters:)` (the URL-string overload) and no `DataRequest.responseData/responseString/responseDecodable` handlers. Class inheritance is flattened (`DataRequest` does not extend `Request`). `STPAPIClient` extension methods from other modules are not accessible. Empty protocol conformance symbols (`""`) on `UploadRequest` and `DataRequest` will crash at runtime.

**Specific example**: The canonical Alamofire usage is impossible:
```csharp
// Cannot do this — Session has no URL-string request method, DataRequest has no response handlers
// AF.request("https://api.example.com/users", method: .get)
//   .responseDecodable(of: UserList.self) { response in ... }
```

### Kingfisher
**Scores**: Naming 3, TypeFidelity 2, Nullability 3, Collections 4, Async 4, ErrorHandling 3, Protocols 3, Noise 3, Completeness 3, Overall 3 — **Avg 3.10**

**Highlights**: `KingfisherManager.Shared.RetrieveImageAsync()` works with proper `Task<RetrieveImageResult>` return and cancellation. `KingfisherOptionsInfoItem` enum-class with factory methods for each option case is well-designed. `StoreToDiskAsync` wraps completion handlers into clean async pattern.

**Top issues**: `IKFOptionSetter` returns `AnyType` on all 30+ builder methods — the entire builder API is broken. `SwiftSet<SwiftString>` leaks on `ImageDownloader.TrustedHosts` (no `IReadOnlySet` projection). `SwiftString`/`SwiftDictionary` leak in `CacheErrorReason` tuple parameters. `DefaultCacheSerializer` does not implement `ICacheSerializer` despite conforming in Swift.

### SnapKit
**Scores**: Naming 3, TypeFidelity 4, Nullability 4, Collections 5, Async 2, ErrorHandling 3, Protocols 3, Noise 3, Completeness 3, Overall 2 — **Avg 3.20**

**Highlights**: Zero `AnyType` occurrences. Array projection to `IReadOnlyList<T>` is seamless. `ConstraintPriority` struct with static properties (Required, High, Medium, Low) and `IEquatable` is well-modeled.

**Top issues**: `GetEqualTo()`, `GetOffset()`, `GetPriority()` naming breaks the fluent DSL — should be `EqualTo()`, `Offset()`, `Priority()`. Spurious async overloads (`MakeConstraintsAsync`) wrap synchronous closures as `Task<ConstraintMaker>` — semantically wrong. Empty marker interfaces (`IConstraintOffsetTarget`, `IConstraintConstantTarget`) cannot be implemented for primitives, making the builder uncallable. No `view.snp` extension property. `#file`/`#line` debug parameters leak into C# signatures.

### CryptoSwift
**Scores**: Naming 3, TypeFidelity 3, Nullability 4, Collections 4, Async N/A, ErrorHandling 4, Protocols 3, Noise 3, Completeness 3, Overall 3 — **Avg 3.33**

**Highlights**: AES encryption, hashing (SHA family, MD5), HMAC, key derivation (Scrypt, HKDF, PBKDF) all work. Named tuple return `(IReadOnlyList<byte> cipherText, IReadOnlyList<byte> authenticationTag)` from AEAD is excellent. Nested array `IReadOnlyList<IReadOnlyList<uint>>` for AES ExpandedKey round-trips correctly.

**Top issues**: `ArraySlice<UInt8>` maps to `AnyType` (13 occurrences), contaminating all protocol interfaces. `ICryptorAndUpdatable` composition proxy throws `NotSupportedException` on every method — `MakeEncryptor()`/`MakeDecryptor()` return dead objects. `PKCS7` emitted as empty C# enum.

### GRDB
**Scores**: Naming 3, TypeFidelity 3, Nullability 4, Collections 3, Async 2, ErrorHandling 4, Protocols 3, Noise 3, Completeness 2, Overall 2 — **Avg 2.90**

**Highlights**: String projection is excellent throughout. `DatabaseError` with `ResultCode`, `Message`, `Sql`, `Arguments` is well-structured. Throwing constructors properly extract error descriptions. `DateTimeOffset` projection from `Foundation.Date`.

**Top issues**: The fundamental GRDB operations (`pool.read { db in }`, `pool.write { db in }`) are completely missing — any method taking `(Database) throws -> T` closures is not bound. `Row` has no subscript/indexer (the primary data extraction API). `ResultCode` is an opaque 8,000-line class instead of a native C# enum. `IDatabaseReader`/`IDatabaseWriter` interfaces are missing `read`/`write`/`asyncRead`/`asyncWrite` — the core protocol methods.

### KeychainAccess
**Scores**: Naming 3, TypeFidelity 4, Nullability 5, Collections 4, Async 3, ErrorHandling 4, Protocols 2, Noise 3, Completeness 3, Overall 3 — **Avg 3.40**

**Highlights**: Nullability is exemplary — `string?`, `bool?`, `AuthenticationPolicy?` all correct. Error extraction with `SBW_GetErrorDescription` + cleanup trio is solid. `GetAllKeys()` returns `IReadOnlyList<string>`. `RequestSharedWebCredential` callback uses `IEnumerable<IDictionary<string, string>>`.

**Top issues**: No C# indexer for Swift subscripts (`keychain["key"]` — the defining API). Fluent builder `GetAccessibility()`, `GetSynchronizable()` uses wrong `Get` prefix. No protocol interfaces emitted at all. `_value` parameter name on `Set()` preserves Swift underscore convention. `Description` not wired to `ToString()`.

### RxSwift
**Scores**: Naming 2, TypeFidelity 2, Nullability 4, Collections 2, Async 1, ErrorHandling 3, Protocols 2, Noise 3, Completeness 2, Overall 2 — **Avg 2.30**

**Highlights**: `Event<TElement>` with `TryGetNext`/`TryGetError`/`Completed` is a well-crafted discriminated union. `BehaviorSubject.GetValue()` error extraction works. Subject constructors and `On()` method are present.

**Top issues**: ALL operators (map, filter, flatMap, merge, etc.) exist in vtable but are NOT callable — they are in `ObservableTypeProxy` infrastructure but not exposed as methods. `IObservableType.Subscribe` takes `Swift.AnyType` — type-erased, unusable. `IObserverType<TElement>.OnNext` also takes `AnyType` despite the generic parameter. No factory methods (`Observable.Create`, `Observable.Just`). No `AsyncThrowingStream` projection. Empty conformance symbol strings will crash at runtime.

### Starscream
**Scores**: Naming 3, TypeFidelity 3, Nullability 4, Collections 4, Async 4, ErrorHandling 3, Protocols 2, Noise 3, Completeness 3, Overall 3 — **Avg 3.20**

**Highlights**: `WebSocket` with `Connect`/`Disconnect`/`Write` works. `WriteAsync` completion-to-Task bridging is clean. `TryGetConnected` returns `IReadOnlyDictionary<string, string>` for response headers. `WebSocketEvent` enum with full `TryGet*` extractors.

**Top issues**: `IWebSocketDelegate` is completely empty — the primary event delivery mechanism is broken. `ICertificatePinning` is empty — no custom TLS validation. `SwiftString` leaks in `Event.Closed` tuple parameter (`(SwiftString, ushort)` instead of `(string, ushort)`).

### SkeletonView
**Scores**: Naming 3, TypeFidelity 3, Nullability 4, Collections 2, Async 4, ErrorHandling 2, Protocols 3, Noise 3, Completeness 3, Overall 3 — **Avg 3.00**

**Highlights**: `ISkeletonFlowDelegate` interface is clean and implementable. Bidirectional existential protocol marshalling works. `RemoveLayerAsync` completion-to-Task conversion is correct. Lazy singleton enum caching.

**Top issues**: UIView extension methods (`showSkeleton`, `hideSkeleton`) are not bound — the primary API entry point. `SkeletonGradient` has zero accessible members. `Foundation.IndexPath` falls to `AnyType`, degrading collection view data source protocols. Protocol proxy `Dispose()` is a no-op — memory leak for long-lived proxies. Empty `finally {}` blocks everywhere.

### Mixpanel
**Scores**: Naming 3, TypeFidelity 3, Nullability 4, Collections 3, Async 4, ErrorHandling 2, Protocols 3, Noise 3, Completeness 2, Overall 2 — **Avg 2.90**

**Highlights**: `IdentifyAsync`, `FlushAsync`, `ResetAsync` demonstrate clean completion-to-Task pattern. `MixpanelLogLevel` string-raw-value enum with `FromRawValue` and lazy singletons. `TryCreate` pattern for failable initializers.

**Top issues**: `track(event:properties:)` — the single most important analytics method — is completely absent. `Mixpanel.Initialize` and `GetMainInstance` are both SB0002 (entry points not exported) — the entire initialization path crashes. `IMixpanelType` has no factory to create from C# primitives, blocking all dictionary-based APIs. `_event` parameter naming.

### BlinkID
**Scores**: Naming 4, TypeFidelity 3, Nullability 4, Collections 4, Async 4, ErrorHandling 3, Protocols 3, Noise 3, Completeness 4, Overall 3.5 — **Avg 3.55**

**Highlights**: The full scanning lifecycle works: `BlinkIDSdkSettings` -> `CreateBlinkIDSdkAsync` -> `CreateScanningSessionAsync` -> process frames -> read 30+ result properties. `ScanningSettings` has a 22-parameter constructor with 5 overloads. `Country` enum with full country code list via `AllCases`. 1,511 XML doc comments.

**Top issues**: `SwiftOptional<DateResult<StringResult>>` leaks in 11 public properties (bound-generic projection failure). `DateResult<SwiftString>` in 6 MRZ properties. 41 `value0` unnamed parameters on enum factories.

### Stripe (14 modules)
**Scores**: Naming 3, TypeFidelity 3, Nullability 4, Collections 4, Async 3, ErrorHandling 3, Protocols 3, Noise 3, Completeness 3, Overall 3 — **Avg 3.20**

The Stripe ecosystem spans ~250,000 lines across 14 modules with ~800+ types.

**Highlights**: `PaymentSheetError` enum is excellently structured with named parameters. `ICustomerAdapter` interface with async Task methods, proper nullability, and meaningful names is a showcase. Cross-module type database resolves most inter-module references. `STPPaymentMethodType` with 40+ lazy-cached cases. Collection projection with `IReadOnlyDictionary<string, string>` from `SwiftDictionary` works cleanly.

**Top issues**: Cannot complete a payment transaction — `STPAPIClient` has no constructors, `STPPaymentHandler.confirmPayment` is skipped (complex closure type), `PaymentSheet.FlowController.confirm` is skipped. Cross-module resolution fails for `StripeCore.STPAPIClient` in StripePayments extensions. 77% member emission rate (375 of 1,670 skipped in StripePayments). 105 `[Obsolete]` warnings in StripePayments. The `[String: Any]` dictionary pattern (`additionalAPIParameters`) is pervasively skipped.

**Per-module variation**: The 3 main modules (StripeCore, StripePayments, StripePaymentSheet) have the richest surfaces. The 10 smaller modules (StripeApplePay, StripeCardScan, StripeCameraCore, StripeFinancialConnections, StripeIdentity, StripePaymentsUI, StripeUICore, Stripe3DS2, StripeConnect, StripePaymentMethodMessaging) range from simple protocol wrappers (Stripe umbrella: 493 lines, 2 empty interfaces) to substantial bindings (StripePaymentsUI: ~15,000 lines). Cross-module `AnyType` fallbacks are concentrated at module boundaries rather than within modules.

### Manual Libraries

#### SmartCardIO
**Scores**: Naming 5, TypeFidelity 4, Nullability 5, Collections 5, Async N/A, ErrorHandling 5, Protocols 4, Noise 4, Completeness 4, Overall 4 — **Avg 4.44**

The highest-scoring library. A well-contained API with no cross-module dependencies. Names are perfectly idiomatic (`TerminalFactory`, `ResponseAPDU`, `CommandAPDU`). `CardError` payload enum with 6 cases and full `TryGet*` extraction is textbook C#. `IReadOnlyList<byte>` for APDU data and ATR bytes. Java Card author/version/date metadata preserved in XML doc comments. Zero generator bugs observed.

**Only friction**: `TerminalFactory.GetShared()` takes `object _params` (existential `Any`) which loses type safety. Proxy `NotSupportedException` on Swift-backed instances.

#### MicroblinkPlatform
**Scores**: Naming 4, TypeFidelity 5, Nullability 5, Collections 4, Async N/A, ErrorHandling 3, Protocols 5, Noise 4, Completeness 4, Overall 4 — **Avg 4.22**

Excellent ObjC bridged type handling — `UIFont`, `UIColor`, `UIImage`, `UIViewController` all correctly projected via `ObjCRuntime.Runtime.GetNSObject<T>()`. `IMicroblinkPlatformSDKDelegate` with full proxy class is well-designed. 15+ theme properties (fonts, colors, corner radii) are clean public API.

**Only friction**: `StatusProperty` collision rename. No XML doc comments (no symbol graph data). Limited API surface means less opportunity for gaps.

#### BlinkIDUX
**Scores**: Naming 4, TypeFidelity 3, Nullability 4, Collections 4, Async 5, ErrorHandling 4, Protocols 3, Noise 3, Completeness 3, Overall 3 — **Avg 3.60**

**Highlights**: `Camera.StartAsync()`, `StopAsync()`, `CheckAuthorizationAsync()` with full cancellation bridge (C# `CancellationToken` -> `SBW_CancelTask` -> Swift `Task.cancel()` -> `isCancellation` callback -> `TrySetCanceled`). `ScanningResult<T, U>` generic enum with `TryGetCompleted`/`TryGetInterrupted`. `CameraStatus` with lazy singletons and `IEquatable`.

**Top issues**: Cross-module `DocumentClassInfo` from BlinkID core falls to `AnyType` (8 occurrences). `AsyncStream` not in type database. Several actor types (CaptureService, SampleBuffer) have metadata-only bindings with no callable methods. 20 `NotSupportedException` sites in proxies.

#### Mappedin
**Scores**: Naming 4, TypeFidelity 4, Nullability 5, Collections 5, Async 5, ErrorHandling 4, Protocols 4, Noise 3, Completeness 5, Overall 4 — **Avg 4.30**

The showcase binding. 51,327 lines, 60+ classes, zero `AnyType` anywhere. Nested generic collections (`IReadOnlyDictionary<THING_KEY, IReadOnlyList<string>>?`) with full projection. 24+ async methods spanning `Task`, `Task<string?>`, `Task<MPIDirections?>`, `Task<IEnumerable<MPIPolygon?>>`. Polymorphic `SearchAsync` returns `IEnumerable<IMPISearchResultCommon>` with 3 concrete implementations. 1,297 doc-comment lines.

**Only friction**: 44 `[Obsolete("Mono JIT crash risk")]` warnings. ~4,000 lines of per-class `ISwiftObject` boilerplate. `THING_KEY` type name leaks Swift source naming. `_object` parameter naming.

## Prioritized Action Items

### 1. Support `Self`-returning protocol methods / fluent builders
- **Issue**: When a Swift protocol method returns `Self` or a method returns the declaring type for chaining, the return type degrades to `AnyType`. This breaks all builder/fluent APIs.
- **Libraries affected**: Kingfisher (30+ builder methods), SnapKit (entire DSL), KeychainAccess (6 builder methods), GRDB (query builder), RxSwift (protocol operators)
- **Examples**: `IKFOptionSetter.GetTargetCache(cache)` returns `AnyType` instead of the concrete builder. `keychain.GetAccessibility(...)` returns `Keychain` in concrete context but `AnyType` in protocol context.
- **Estimated effort**: Large — requires detecting `Self` return in protocol context and either using generic `TSelf` constraint, concrete type substitution, or `this` return pattern.
- **Classification**: Design gap

### 2. Project tuple element types through the type projection pipeline
- **Issue**: `SwiftOptional<T>`, `SwiftString`, `SwiftDictionary` leak into public API inside tuple parameters of enum associated value constructors. The projection that works for standalone params/returns does not apply inside tuple elements.
- **Libraries affected**: Lottie (6 occurrences in `LottiePlaybackMode`), Starscream (`Event.Closed`, `ServerEvent.Disconnected`), Kingfisher (`CacheErrorReason` factories), BlinkID (bound-generic tuples)
- **Examples**: `LottiePlaybackMode.FromProgress((SwiftOptional<double>, double, LottieLoopMode))` should be `(double?, double, LottieLoopMode)`.
- **Estimated effort**: Medium — the projection pipeline exists; it needs to be recursively applied to tuple element types.
- **Classification**: Generator bug

### 3. Support closure parameters with enum/class types in method signatures
- **Issue**: Methods with closures containing non-primitive parameter or return types are either skipped or degraded. This blocks the core workflows of libraries that use callback-based transaction/response patterns.
- **Libraries affected**: GRDB (`read`/`write` with `(Database) throws -> T`), Alamofire (`responseData`/`responseString`), Stripe (`confirmPayment` with `(STPPaymentHandlerActionStatus, STPPaymentIntent?, NSError?) -> Void`), RxSwift (operators)
- **Examples**: GRDB's `pool.write { db in ... }` and Stripe's `paymentHandler.confirmPayment(params) { status, intent, error in ... }` are both missing.
- **Estimated effort**: Large — the closure parameter gate (`ClosureHandler` safety constraints) needs to be relaxed for known-safe types, with wrapper function generation for the Swift bridge side.
- **Classification**: Design gap / generator limitation

### 4. Expand Apple SDK type database (`IndexPath`, `SecTrust`, `AsyncStream`, `CGColorSpace`)
- **Issue**: Missing type database entries for common Apple framework types cascade into `AnyType` fallbacks that degrade entire protocol interfaces.
- **Libraries affected**: SkeletonView (`IndexPath` — 6 degraded protocol methods), Alamofire/Starscream (`SecTrust`, `SecCertificate`), BlinkIDUX (`AsyncStream`), Lottie (`CGColorSpace`)
- **Examples**: `ISkeletonCollectionViewDataSource.GetCollectionSkeletonView(skeletonView, AnyType indexPath)` should use `Foundation.IndexPath`.
- **Estimated effort**: Small per type (add database entry + projection), medium in aggregate (10-15 types)
- **Classification**: Generator gap (type database)

### 5. Fix `Get` prefix on fluent/builder methods
- **Issue**: Swift computed properties and `Self`-returning methods both get a `Get` prefix, making builder methods read as getters. `GetEqualTo()`, `GetAccessibility()`, `GetTargetCache()` should be `EqualTo()`, `WithAccessibility()`, `TargetCache()`.
- **Libraries affected**: SnapKit, KeychainAccess, Kingfisher, GRDB
- **Examples**: `make.Top.GetEqualTo(view).GetOffset(10)` should be `make.Top.EqualTo(view).Offset(10)`.
- **Estimated effort**: Small — adjust the naming heuristic for methods returning the declaring type or `Self`.
- **Classification**: Generator bug (naming heuristic)

### 6. Wire `Description` to `ToString()` override
- **Issue**: Every type with Swift's `CustomStringConvertible` gets a `Description` property but no `ToString()` override. C# developers expect `ToString()` to work in interpolation, logging, and debugger displays.
- **Libraries affected**: All 18 libraries (universal pattern)
- **Examples**: `keychain.Description` exists but `keychain.ToString()` returns `Swift.KeychainAccess.Keychain`.
- **Estimated effort**: Small — emit `public override string ToString() => Description;` when `Description` property exists.
- **Classification**: Generator gap

### 7. Emit C# indexers for Swift subscripts
- **Issue**: Swift subscripts are not emitted as C# indexers. Libraries whose primary API is subscript-based lose their most natural access pattern.
- **Libraries affected**: KeychainAccess (`keychain["key"]`), GRDB (`row["column"]`, `row[0]`), and others
- **Examples**: `keychain["key"] = "value"` must be written as `keychain.Set("value", "key")`.
- **Estimated effort**: Medium — requires mapping Swift subscript declarations to C# indexer syntax.
- **Classification**: Generator gap

### 8. Model class inheritance relationships
- **Issue**: Swift subclass relationships are flattened — each class is emitted independently. Base-class members are inaccessible via subclass references, and polymorphic casting fails. Across the validation suite, **60 derived classes** in 12 libraries are missing a combined **1,184 inherited members**.
- **Libraries affected**: Alamofire (399 missing members across Request hierarchy — `DataRequest : Request`, `UploadRequest : DataRequest : Request`), SnapKit (19 missing members — fluent chain completely broken: `ConstraintMakerExtendable` can't access `equalTo` from `ConstraintMakerRelatable`), Lottie (46 missing — `AnimatedButton`/`AnimatedSwitch` lose base `AnimatedControl` members), RxSwift (30 missing), StripePaymentsUI (184 missing)
- **Examples**: `DataRequest` does not extend `Request`, so `.Resume()`, `.Cancel()`, `.State`, `.Id`, `.Progress` and 66 other members are inaccessible. SnapKit's `make.left.equalTo(view).priority(.high)` is broken because `equalTo` is on the base class.
- **Estimated effort**: Large (6 sessions) — requires parsing superclass data from ABI JSON, class hierarchy resolution, emission changes (shared Dispose/payload, `: BaseClass` syntax), member deduplication, protocol conformance inheritance, and full validation pass.
- **Classification**: Generator gap — foundational
- **Implementation plan**: `src/docs/class-inheritance-implementation.md`
- **Indirect benefits**: Enables Self-return handling (knowing the hierarchy resolves `Self` to concrete types), fixes empty conformance symbols (inherited conformances resolved correctly), prerequisite for ObjC binding integration (NSObject hierarchy)

### 9. Fix empty protocol conformance symbols
- **Issue**: Multiple types register `""` (empty string) as protocol conformance symbols. `ProtocolConformanceDescriptor.LoadFromSymbol("Module", "")` will crash at runtime.
- **Libraries affected**: RxSwift (8 empty symbols), Alamofire (2 empty symbols), others
- **Examples**: `BehaviorSubject._protocolConformanceSymbols[typeof(IObservableType)] = ""` — runtime crash.
- **Estimated effort**: Small — either resolve inherited conformance symbols or omit the entry.
- **Classification**: Generator bug

### 10. Strip `#file`/`#line` debug parameters from C# signatures
- **Issue**: Swift's `#file`/`#line` default parameters for debugging leak into C# overloads as extra `string file, nuint line` parameters.
- **Libraries affected**: SnapKit (every `GetEqualTo`/`GetLessThanOrEqualTo` method has 3 overloads instead of 1)
- **Examples**: `GetEqualTo(other, file, line)` vs `GetEqualTo(other, file)` vs `GetEqualTo(other)` — two of these overloads are noise.
- **Estimated effort**: Small — detect `#file`/`#line` default expressions and suppress those parameters.
- **Classification**: Generator bug

## Comparison to ObjC Binding Experience

### What's Better Than Xamarin.iOS ObjC Bindings

**Async/await is dramatically better.** Xamarin.iOS ObjC bindings required manual `TaskCompletionSource` wrappers or `InvokeOnMainThread` gymnastics. The Swift generator produces `Task<T>` with `CancellationToken`, cooperative cancellation via `SBW_CancelTask`, and `RunContinuationsAsynchronously` automatically. The 24 async methods in Mappedin with protocol-existential parameters would have required weeks of manual binding work in Xamarin.

**Discriminated union projection is far superior.** ObjC had no equivalent to Swift enums with associated values. ObjC error codes required manual `NSError.Code` constants and switch statements. The `CaseTag` + `TryGet*` + `[MaybeNullWhen(false)]` pattern is a genuine C# idiom that integrates with nullable analysis and pattern matching. The `Lazy<T>` singleton caching for no-payload cases is more efficient than Xamarin's manually declared `static readonly` fields.

**Collection projection is more automatic.** Xamarin required `[Export]` attributes and manual `NSArray`/`NSDictionary` conversion. The generator automatically projects `[T]` to `IReadOnlyList<T>`, `[K:V]` to `IReadOnlyDictionary<K,V>`, and parameters to `IEnumerable<T>` / `IDictionary<K,V>`. Nested collections (`IReadOnlyDictionary<K, IReadOnlyList<string>>?`) work without manual intervention.

**Protocol proxy bidirectionality is more capable.** Xamarin's `[Protocol]`/`[Export]` model supported C#-to-ObjC direction (implementing ObjC protocols from C#) but was limited in the reverse direction. The Swift generator's vtable-based proxy with `ExistentialContainer` marshalling supports both directions — you can implement `IImageCaching` in C# AND receive protocol values from Swift as interfaces.

**XML documentation propagation is automatic.** Xamarin bindings rarely had documentation beyond what was manually added. The generator carries through Swift symbol graph documentation to C# `<summary>`, `<param>`, `<returns>`, and `<remarks>` tags. Lottie gets 1,013 doc tags, BlinkID gets 1,511.

### What's Worse Than Xamarin.iOS ObjC Bindings

**Completeness of core workflows.** Xamarin.iOS ObjC bindings, while tedious to write, could bind any ObjC method signature. Hand-written bindings for Alamofire/AFNetworking, GRDB/FMDB, or RxSwift/RxObjC could express the full API surface. The Swift generator's safety gates (closure parameter restrictions, `Self` return degradation, missing type database entries) produce bindings that are structurally rich but miss the core usage patterns.

**`IDisposable` on every type.** ObjC bindings had `NSObject` base class with reference counting handled transparently. The Swift generator puts `IDisposable` on every type including simple enums and small structs, requiring `using` blocks or explicit `Dispose()` everywhere. A C# developer writing `var priority = ImageRequest.Priority.High` would not expect to need `using` or disposal.

**No class inheritance.** Xamarin's `[BaseType]` attribute correctly modeled ObjC class hierarchies. The Swift generator flattens all classes. This is a significant regression for libraries with rich inheritance trees.

**Enum representation.** ObjC `NS_ENUM` mapped to native C# enums with `switch` support. Swift enums (even simple no-payload ones) are frequently emitted as classes with `CaseTag` discrimination. While the generator has improved this (simple enums are now native when safe), many libraries still get class-based enums where native C# enums would be more ergonomic.

**Type noise.** Xamarin bindings could hide ObjC runtime types completely behind a clean C# facade. The Swift generator exposes `ISwiftObject`, `IDisposable`, `ExistentialContainer`, proxy classes, and `[EditorBrowsable(Never)]`-hidden infrastructure. While IntelliSense hides much of this, source code reading and debugging still reveal the interop layer.

### What's Just Different

**Naming conventions.** Xamarin had a long-established set of naming guidelines (strip `NS`/`UI` prefixes, convert `delegate` to events, etc.). The Swift generator preserves Swift names more faithfully, which means `STPPaymentMethodType` stays as-is rather than becoming `PaymentMethodType`. Neither approach is objectively better — Xamarin's renaming was more C#-idiomatic but made it harder to cross-reference with Apple documentation.

**Error handling model.** Xamarin used `NSError` out-parameters or `NSErrorException`. The Swift generator uses `SwiftRuntimeException` with extracted message strings. Neither maps perfectly to C#'s `try`/`catch` exception model — both require knowing the error type to extract structured information.

**Memory management.** Xamarin's `GCHandle` + ref-counting was transparent for `NSObject` subclasses but tricky for value types. The Swift generator's `SafeHandle<T>` + ARC interop is explicit (`IDisposable`) but consistent across all types. The trade-off is ceremony vs. correctness — the Swift model is more predictable but more verbose.
