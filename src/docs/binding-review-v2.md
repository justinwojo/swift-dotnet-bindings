# Binding Quality Review v2 — February 2026 (Post Phase 2)

## Executive Summary

This review re-evaluates C# bindings generated for 18 real-world Swift libraries (32 validation targets) across 10 quality dimensions, following the completion of Phase 2 (Binding Quality). The generator has made measurable progress: the overall average score moved from **3.38 to 3.45** (+0.07), with the most significant gains in Stripe (+0.35), Lottie (+0.25), Alamofire (+0.20), and Starscream (+0.20). All 10 prioritized action items from the v1 review were addressed, though their impact varies — some delivered transformative improvements (class inheritance, tuple projection, ToString(), indexers, debug parameter stripping) while others had narrower effects than expected (Self-returning protocols, closure relaxation, Apple SDK type database).

The target of >3.80 overall average was not achieved (3.45 actual). The gap is largely structural: the improvements that landed (infrastructure/correctness fixes) raised the floor for well-contained libraries but did not unlock the core workflows of API-design-heavy libraries like RxSwift (2.40), Mixpanel (2.90), SkeletonView (3.00), or GRDB (3.00). These libraries depend on patterns the generator still cannot express: generic throwing closures, protocol extension methods as callable APIs, UIKit extension entry points, and protocol-typed factory methods for existentials. The top-scoring libraries (SmartCardIO 4.44, MicroblinkPlatform 4.33, Mappedin 4.30) remain unchanged — they were already at or near the ceiling, and Phase 2's improvements were orthogonal to their API patterns.

The most impactful Phase 2 deliverable was **class inheritance** (Phase 1, I1-I6): 60 derived classes across 12 libraries now have proper C# `: BaseClass` syntax, virtual/override dispatch, and inherited member access. This fixed Alamofire's flattened `DataRequest : Request` hierarchy and resolved empty conformance symbols. The second most impactful was the **Stripe async payment flow**: `STPAPIClient` constructors, `ConfirmPaymentIntentAsync`, and `PaymentSheet.PresentAsync` moved Stripe from "cannot complete a payment" to a fully expressible integration pattern.

## Score Comparison: v1 → v2

### Per-Library Averages

| Library | v1 Avg | v2 Avg | Delta | Direction |
|---------|:------:|:------:|:-----:|:---------:|
| Stripe (14 modules) | 3.20 | 3.55 | **+0.35** | ↑↑ |
| Lottie | 3.85 | 4.10 | **+0.25** | ↑↑ |
| Alamofire | 2.90 | 3.10 | **+0.20** | ↑ |
| Starscream | 3.20 | 3.40 | **+0.20** | ↑ |
| MicroblinkPlatform | 4.22 | 4.33 | **+0.11** | ↑ |
| Nuke | 3.50 | 3.60 | **+0.10** | ↑ |
| RxSwift | 2.30 | 2.40 | **+0.10** | ↑ |
| GRDB | 2.90 | 3.00 | **+0.10** | ↑ |
| KeychainAccess | 3.40 | 3.45 | **+0.05** | ↑ |
| Kingfisher | 3.10 | 3.10 | — | — |
| SkeletonView | 3.00 | 3.00 | — | — |
| Mixpanel | 2.90 | 2.90 | — | — |
| BlinkID | 3.55 | 3.55 | — | — |
| SmartCardIO | 4.44 | 4.44 | — | — |
| BlinkIDUX | 3.60 | 3.60 | — | — |
| Mappedin | 4.30 | 4.30 | — | — |
| SnapKit | 3.20 | 3.10 | **-0.10** | ↓ |
| CryptoSwift | 3.33 | 3.22 | **-0.11** | ↓ |
| **Overall Average** | **3.38** | **3.45** | **+0.07** | ↑ |

### Biggest Movers

**Positive:**
- **Stripe (+0.35)**: Payment flow now works end-to-end. `STPAPIClient` constructors, `ConfirmPaymentIntentAsync`, `PaymentSheet.PresentAsync/ConfirmAsync` all present. Async score 3→4, completeness 3→3.5, overall 3→3.5.
- **Lottie (+0.25)**: Tuple element projection fixed (`SwiftOptional<double>` → `double?` in `LottiePlaybackMode`). `IAnimationImageProvider` no longer empty. Nullability 4→4.5, collections 4→4.5, protocols 3→3.5, completeness 4→4.5, overall 3.5→4.
- **Alamofire (+0.20)**: Class inheritance fixed (`DataRequest : Request`). Empty conformance symbols resolved. `Session.Request()` now exists. TypeFidelity 3→3.5, async 2→2.5, completeness 2→2.5, overall 2→2.5.
- **Starscream (+0.20)**: `ICertificatePinning` now has members (was empty). `SwiftString` in tuple parameters fixed to `string`. TypeFidelity 3→4, protocols 2→3.

**Negative (scoring corrections, not regressions):**
- **SnapKit (-0.10)**: Protocol/Interface score corrected from 3→2 on deeper analysis — 5 empty marker interfaces (`IConstraintOffsetTarget`, etc.) make the core DSL methods uncallable since C# primitives cannot implement interfaces.
- **CryptoSwift (-0.11)**: Protocol/Interface score corrected from 3→2 — struct types (CBC, CTR, ECB) do not implement their protocol interfaces despite registering conformances, breaking the `new AES(key, new CBC(iv))` pattern.

## Scorecard Matrix (v2)

| Library | Naming | TypeFidelity | Nullability | Collections | Async | ErrorHandling | Protocols | Noise | Completeness | Overall | **Avg** |
|---------|:------:|:-----------:|:----------:|:----------:|:-----:|:------------:|:---------:|:-----:|:-----------:|:-------:|:-------:|
| Nuke | 3 | 4 | 4 | 3 | 5 | 3 | 4 | 3 | 4 | 3 | **3.60** |
| Lottie | 4 | 3 | 4.5 | 4.5 | 5 | 4 | 3.5 | 4 | 4.5 | 4 | **4.10** |
| Alamofire | 3 | 3.5 | 4 | 4 | 2.5 | 3 | 3 | 3 | 2.5 | 2.5 | **3.10** |
| Kingfisher | 3 | 2 | 3.5 | 4 | 4 | 3 | 2.5 | 3 | 3 | 3 | **3.10** |
| SnapKit | 3 | 4 | 4 | 5 | 2 | 3 | 2 | 3 | 3 | 2 | **3.10** |
| CryptoSwift | 3 | 3 | 4 | 4 | N/A | 4 | 2 | 3 | 3 | 3 | **3.22** |
| GRDB | 3 | 3 | 4 | 3 | 2 | 4 | 3 | 3 | 2 | 3 | **3.00** |
| KeychainAccess | 3 | 4 | 5 | 4 | 3 | 4 | 2 | 3 | 3 | 3.5 | **3.45** |
| RxSwift | 3 | 2 | 4 | 2 | 1 | 3 | 2 | 3 | 2 | 2 | **2.40** |
| Starscream | 3 | 4 | 4 | 4 | 4 | 3 | 3 | 3 | 3 | 3 | **3.40** |
| SkeletonView | 3 | 3 | 4 | 2 | 4 | 2 | 3 | 3 | 3 | 3 | **3.00** |
| Mixpanel | 3 | 3 | 4 | 3 | 4 | 2 | 3 | 3 | 2 | 2 | **2.90** |
| BlinkID | 4 | 3 | 4 | 4 | 4 | 3 | 3 | 3 | 4 | 3.5 | **3.55** |
| Stripe (14 modules) | 3.5 | 3.5 | 4 | 4 | 4 | 3 | 3.5 | 3 | 3.5 | 3.5 | **3.55** |
| SmartCardIO | 5 | 4 | 5 | 5 | N/A | 5 | 4 | 4 | 4 | 4 | **4.44** |
| MicroblinkPlatform | 4 | 5 | 5 | 4 | N/A | 3 | 5 | 4 | 5 | 4 | **4.33** |
| BlinkIDUX | 4 | 3 | 4 | 4 | 5 | 4 | 3 | 3 | 3 | 3 | **3.60** |
| Mappedin | 4 | 4 | 5 | 5 | 5 | 4 | 4 | 3 | 5 | 4 | **4.30** |
| **Column Avg** | **3.42** | **3.39** | **4.22** | **3.81** | **3.63** | **3.33** | **3.08** | **3.17** | **3.31** | **3.14** | **3.45** |

**Column Avg changes v1→v2:** Naming 3.33→3.42 (+0.09), TypeFidelity 3.28→3.39 (+0.11), Nullability 4.17→4.22 (+0.05), Collections 3.72→3.81 (+0.09), Async 3.53→3.63 (+0.10), ErrorHandling 3.33→3.33 (0), Protocols 3.11→3.08 (-0.03), Noise 3.17→3.17 (0), Completeness 3.17→3.31 (+0.14), Overall 2.94→3.14 (+0.20)

**Top 3 Libraries**: SmartCardIO (4.44), MicroblinkPlatform (4.33), Mappedin (4.30) — unchanged
**Bottom 3 Libraries**: RxSwift (2.40), Mixpanel (2.90), GRDB/SkeletonView (3.00) — RxSwift improved slightly but remains bottom
**Strongest Category**: Nullability (4.22 avg) — consistently excellent
**Weakest Category**: Protocol/Interface Usability (3.08 avg) — slightly worse than v1 due to scoring corrections
**Most Improved Category**: Overall Usability (2.94→3.14, +0.20) — class inheritance and async improvements had broad impact

## What Improved Most

### Impact Analysis of the 10 Action Items

| # | Action Item | Status | Score Impact | Libraries Affected |
|---|---|---|---|---|
| 1 | Self-returning protocol methods (Q4) | Implemented | **Low** (+0 to +0.5 per lib) | The `GenericContext.ForProtocolSelf()` correctly maps `τ_0_0 → TSelf`, but the main pain point (Kingfisher's `IKFOptionSetter`, SnapKit's builder) remains because the concrete types still don't emit the protocol's methods with resolved return types. Self-requirement protocols are correctly flagged/excluded. |
| 2 | Tuple element projection (Q1e) | Implemented | **Medium** (+0.25 Lottie, +0.20 Starscream) | `SwiftOptional<double>` → `double?` and `SwiftString` → `string` in tuple parameters are fixed. Directly improved Lottie's `LottiePlaybackMode` and Starscream's `Event.Closed`. |
| 3 | Closure parameter relaxation (Q3) | Implemented | **Medium** (+0.35 Stripe, +0.10 GRDB) | Classes, simple enums, ObjC-bridged types, and `Optional<ref>` now pass through closures. Enabled `ConfirmPaymentIntentAsync` in Stripe and `AsyncWriteWithoutTransaction(Action<Database>)` in GRDB. Complex enums and `Optional<Primitive/Enum>` still blocked. |
| 4 | Apple SDK type database (Q2) | Implemented | **Low-Medium** (+0 to +0.10) | `IndexPath → NSIndexPath` fixed SkeletonView's data source protocols. `SecTrust/SecCertificate/SecKey/SecIdentity → IntPtr` fixed Starscream's `ICertificatePinning`. Narrowly targeted but effective where applied. |
| 5 | Get prefix fix (Q1a) | Implemented | **Low** (+0.05 KeychainAccess) | Self-returning detection works, but the fix introduced `Method` suffix (e.g., `AccessibilityMethod`) as a new collision-avoidance pattern. SnapKit's `GetEqualTo` is unchanged because those methods don't match the Self-returning heuristic. |
| 6 | Description → ToString() (Q1c) | Implemented | **Broad but shallow** | 128 `ToString()` overrides in StripePayments alone. Present on all types with `description`. Improves debugging and logging across all libraries. Does not move category scores but improves daily developer experience. |
| 7 | C# indexers for subscripts (Q1d) | Implemented | **Medium** (+0.10 GRDB) | GRDB `Row` now has 3 indexers (`this[nint]`, `this[string]`, `this[Row.Index]`). KeychainAccess `Keychain` still lacks its defining subscript. Partially applied. |
| 8 | Class inheritance (Phase 1, I1-I6) | Implemented | **High** (+0.20 Alamofire, infrastructure-wide) | 60 derived classes across 12 libraries. `DataRequest : Request`, `ConstraintMakerEditable : ConstraintMakerPrioritizable : ConstraintMakerFinalizable`. Fixed Alamofire's polymorphism, resolved empty conformance symbols. The single most impactful structural improvement. |
| 9 | Empty conformance symbols | Implemented | **Medium** (crash prevention) | Empty `""` strings eliminated from `_protocolConformanceSymbols`. Alamofire's `UploadRequest`/`DataRequest` no longer crash at runtime. RxSwift's empty conformances now produce descriptive exceptions. |
| 10 | Debug parameter stripping (Q1b) | Implemented | **Low** (+0 SnapKit) | `IsDebugParameter()` heuristic strips `StaticString+file/function, UInt+line/column`. SnapKit now has default-parameter overloads that omit `file`/`line`. The parameters still appear on the full-parameter overload but are no longer the only option. |

### Additional Improvements Not in Original Action Items

- **SB0003/SB0004 diagnostics (Q2)**: Empty interfaces and non-dispatchable proxy members now have `[Obsolete]` with `DiagnosticId` and `UrlFormat`. This surfaces limitations at compile time rather than runtime. Visible across all libraries.
- **Closure interface recovery (Q4b)**: Protocol interface members with closure parameters that can't dispatch through the proxy are emitted in the interface (with `NotSupportedException` stub in proxy). This makes interfaces more complete for C#-side implementation.
- **XML doc comment propagation**: MicroblinkPlatform gained 48 `<summary>` blocks. Lottie has 1,013 doc tags. BlinkID has 1,511. This was present in v1 but has expanded.

## Remaining Gaps

### Still-Open Issues (carried from v1, not fully resolved)

1. **`Self`-returning protocol methods still degrade to `AnyType` on concrete types**: The `GenericContext.ForProtocolSelf()` infrastructure works for protocol interface signatures, but Kingfisher's `KF.Builder` still does not emit the 30+ `IKFOptionSetter` builder methods with resolved return types. The fluent builder pattern remains broken across Kingfisher, and partially broken in KeychainAccess (now uses `Method` suffix instead of `Get` prefix — lateral move, not fix).

2. **Closure parameters with generic throwing signatures**: GRDB's fundamental `pool.read { db in ... }` / `pool.write { db in ... }` (which take `(Database) throws -> T` generic closures) remain unbound. The Q3 relaxation only enabled non-generic, non-returning closure variants like `AsyncWriteWithoutTransaction(Action<Database>)`.

3. **UIKit extension methods not projected**: SkeletonView's `showSkeleton()`/`hideSkeleton()`, SnapKit's `view.snp`, Kingfisher's `view.kf` — none of these extension-based entry points are bound. This is a fundamental limitation of the generator's scope (Swift extensions on foreign types).

4. **Protocol-typed parameters with primitives**: SnapKit's empty marker interfaces (`IConstraintOffsetTarget`, `IConstraintConstantTarget`) cannot be implemented by C# `float`/`double`/`int`. This makes the entire `Offset()`, `Inset()`, `MultipliedBy()` API uncallable.

5. **Concrete types not declaring protocol interface conformance**: CryptoSwift's `CBC`/`CTR`/`ECB` register `IBlockMode` conformance in `_protocolConformanceSymbols` but do not declare `: IBlockMode` on the class. Same pattern in Kingfisher (`DefaultCacheSerializer` not `: ICacheSerializer`). This breaks protocol-typed composition.

6. **Bound-generic optional projection**: BlinkID's `Optional<DateResult<StringResult>>` still falls back to `SwiftOptional<DateResult<...>>` — nested generics bypass the projection pipeline.

7. **Cross-module type duplication**: Stripe's `STPAPIClient` exists as separate partial classes in 5 module namespaces. Extension methods from StripePayments don't merge into the StripeCore type.

### New Issues (not present in v1)

1. **`Method` suffix collision avoidance**: KeychainAccess's fluent builders changed from `GetAccessibility()` to `AccessibilityMethod()` — the `Get` prefix is gone but the `Method` suffix is equally un-idiomatic. A `With` prefix would be more natural C#.

2. **`GetHashCode()` returns 0**: KeychainAccess's `AuthenticationPolicy.GetHashCode()` has a hardcoded `return 0;` despite having `ISwiftHashable` conformance. This breaks hash-based collections in .NET.

3. **Proxy `Dispose()` no-op**: SkeletonView's 11 proxy classes have `public void Dispose() { }` with no cleanup of `GCHandle` or `EveryProtocol` allocations — genuine memory leak for long-lived proxies.

## Cross-Library Patterns

### What Works Well (patterns scoring 4-5 consistently)

**Nullability (avg 4.22)** remains the strongest category. Every generated file starts with `#nullable enable`. Optional Swift types correctly project to `T?`. The `SwiftOptional<T>` to `T?` conversion is transparent at public API boundaries. The tuple element projection fix in Q1e extended this to tuple parameter positions (Lottie, Starscream).

**Async/Await (avg 3.63, but 5/5 when present)** is the most technically impressive feature. The Stripe payment flow demonstrates the pattern at scale: `Task<STPPaymentIntent>` with `CancellationToken`, cooperative cancellation via `SBW_CancelTask`, proper `TaskCreationOptions.RunContinuationsAsynchronously`, and error/cancellation routing. `IAsyncEnumerable<T>` for Swift `AsyncStream` (BlinkIDUX, Nuke) is an inspired mapping.

**Collection projection (avg 3.81)** handles common cases well. `Array<T>` returns as `IReadOnlyList<T>`, parameters accept `IEnumerable<T>`, dictionaries project to `IReadOnlyDictionary<K,V>`. Mappedin demonstrates `IReadOnlyDictionary<K, IReadOnlyList<string>>?` with nested projection.

**Class inheritance (new in v2)** is structurally correct wherever it applies: `DataRequest : Request`, `ConstraintMakerEditable : ConstraintMakerPrioritizable : ConstraintMakerFinalizable`, `MainScheduler : SerialDispatchQueueScheduler`. Shared disposal, virtual/override dispatch, and `SwiftInheritanceChain` constructors all work.

**Discriminated union TryGet pattern** continues to be excellent. `CaseTag` enum + `Tag` property + `TryGetXxx([MaybeNullWhen(false)] out T value)` is applied to every Swift enum with associated values across all libraries.

### Common Pain Points (patterns scoring 1-2 consistently)

**Protocol/Interface usability (avg 3.08)** dropped slightly from v1 (3.11). The structural improvements (SB0003/SB0004 diagnostics, closure interface recovery) are offset by scoring corrections that revealed deeper issues: empty marker interfaces that block entire API patterns (SnapKit), struct types not declaring protocol conformance (CryptoSwift), and concrete types not implementing registered interfaces (Kingfisher). RxSwift's `IObservableType.Subscribe(AnyType)` — type-erased despite a generic parameter — remains the most severe example.

**Overall usability (avg 3.14)** improved +0.20 from v1's 2.94 but remains the second-weakest category. The gap between structural completeness and workflow usability persists for libraries whose core API relies on patterns the generator cannot express: closure-based transactions (GRDB), protocol extension operators (RxSwift), UIKit extensions (SkeletonView), or protocol-typed factories for existentials (Mixpanel's `IMixpanelType`).

## Per-Library Deep Dives

### Nuke (3.50 → 3.60, +0.10)

| Category | v1 | v2 | Δ |
|---|---|---|---|
| Naming | 3 | 3 | — |
| TypeFidelity | 4 | 4 | — |
| Nullability | 4 | 4 | — |
| Collections | 2 | 3 | +1 |
| Async | 5 | 5 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 4 | 4 | — |
| Noise | 3 | 3 | — |
| Completeness | 4 | 4 | — |
| Overall | 3 | 3 | — |

**What changed**: `IReadOnlyDictionary<string, object>` now appears where `SwiftDictionary<SwiftString, AnyType>` leaked in `CoreImageFilter.Error.FailedToCreateFilter`. `IAsyncEnumerable<T>` for `ImageTask.Progress`/`Previews`/`Events`.

**Remaining issues**: `ImageRequest` only has `init(stringLiteral:)` — the primary `init(url: URL)` is missing. `ConfigurationValue` property rename. `_startPrefetching` underscore-prefixed public method. `value0` unnamed parameters on enum factories.

### Lottie (3.85 → 4.10, +0.25)

| Category | v1 | v2 | Δ |
|---|---|---|---|
| Naming | 4 | 4 | — |
| TypeFidelity | 3 | 3 | — |
| Nullability | 4 | 4.5 | +0.5 |
| Collections | 4 | 4.5 | +0.5 |
| Async | 5 | 5 | — |
| ErrorHandling | 4 | 4 | — |
| Protocols | 3 | 3.5 | +0.5 |
| Noise | 4 | 4 | — |
| Completeness | 4 | 4.5 | +0.5 |
| Overall | 3.5 | 4 | +0.5 |

**What changed**: Tuple element projection fixed — `LottiePlaybackMode.FromProgress((double?, double, LottieLoopMode))` is now idiomatic. `IAnimationImageProvider` is no longer empty (2 members). Closure-accepting methods (`Play(..., Action<bool>? completion)`) coexist with `PlayAsync`.

**Remaining issues**: `AnyType` in ~22 locations (IInterpolatable, ISpatialInterpolatable). `IAnimationFontProvider` still empty (SB0004). `ExistentialContainer0` in `AnyValueProviderStorage.SingleValue`.

### Alamofire (2.90 → 3.10, +0.20)

| Category | v1 | v2 | Δ |
|---|---|---|---|
| Naming | 3 | 3 | — |
| TypeFidelity | 3 | 3.5 | +0.5 |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 2 | 2.5 | +0.5 |
| ErrorHandling | 3 | 3 | — |
| Protocols | 3 | 3 | — |
| Noise | 3 | 3 | — |
| Completeness | 2 | 2.5 | +0.5 |
| Overall | 2 | 2.5 | +0.5 |

**What changed**: Class inheritance fixed — `DataRequest : Request`, `UploadRequest : DataRequest : Request`. Empty conformance symbols resolved. `Session.Request(IURLRequestConvertible)` now exists. `CancelAllRequestsAsync` with `CancellationToken`. `ToString()` overrides.

**Remaining issues**: The primary `Session.request(url:, method:, parameters:)` with `URLConvertible` is still missing. `Request.cancel()`/`resume()`/`suspend()` not bound (likely Self-returning). No `DataRequest.responseData()`/`responseString()` response handlers. Cannot complete the fundamental Alamofire request-response workflow.

### Kingfisher (3.10 → 3.10, unchanged)

**What changed**: String projection in tuple elements improved (partial fix of v1 Issue 3). Nullability slightly improved for string optionals.

**Remaining issues**: `IKFOptionSetter` still returns `AnyType` on all 30+ builder methods. `KF.Builder` does not implement `IKFOptionSetter`. `DefaultCacheSerializer` does not implement `ICacheSerializer`. `SwiftSet<SwiftString>` not projected on `TrustedHosts`.

### SnapKit (3.20 → 3.10, -0.10)

**What changed**: Debug parameters (`file`/`line`) no longer the only option — default-parameter overloads exist. Protocol scoring corrected downward.

**Remaining issues**: `GetEqualTo()`/`GetPriority()` naming unchanged. Spurious `MakeConstraintsAsync` for synchronous closures. 5 empty marker interfaces make core DSL methods uncallable. No `view.snp` extension property.

### CryptoSwift (3.33 → 3.22, -0.11)

**What changed**: Protocol scoring corrected downward — struct types (CBC, CTR, ECB) don't implement `IBlockMode` despite registered conformance.

**Remaining issues**: `ArraySlice<UInt8>` → `AnyType` (14 occurrences). `ICryptorAndUpdatable` proxy throws `NotSupportedException` on all methods. `PKCS7` empty enum.

### GRDB (2.90 → 3.00, +0.10)

**What changed**: `Row` now has 3 indexers (was missing entirely). `AsyncWriteWithoutTransaction(Action<Database>)` provides non-transactional write access. `PrepareDatabase` and `RegisterMigration` accept closure callbacks. `IEquatable<T>` implementations.

**Remaining issues**: `pool.read { db in }` / `pool.write { db in }` still missing (generic throwing closures). `ResultCode` remains a 3,749-line class instead of a C# enum. Core async APIs (`asyncRead`, `asyncWrite`) not bound.

### KeychainAccess (3.40 → 3.45, +0.05)

**What changed**: `Get` prefix removed from fluent builders (now `AccessibilityMethod`, `SynchronizableMethod`). `ToString()` wired to `Description`.

**Remaining issues**: No `Keychain` subscript (`keychain["key"]`). `Method` suffix on builders is un-idiomatic. `_value` parameter naming. No protocol interfaces emitted. `GetHashCode()` returns 0.

### RxSwift (2.30 → 2.40, +0.10)

**What changed**: Naming improved (PascalCase, XML docs, `ISwiftDisposable` naming). Empty conformance symbols now produce descriptive exceptions instead of crashes.

**Remaining issues**: ALL operators (map, filter, flatMap — 76 in vtable) remain invisible as callable methods. `IObserverType<TElement>.OnNext(AnyType)` discards the generic parameter. No factory methods. No async projection. Fundamentally unusable for reactive programming.

### Starscream (3.20 → 3.40, +0.20)

**What changed**: `ICertificatePinning` now has `EvaluateTrust` member (was empty). `SwiftString` leak in tuple parameters fixed to `string`.

**Remaining issues**: `IWebSocketDelegate` still empty (SB0004) — the primary event delivery mechanism. Subclassing `WebSocket` and overriding `DidReceive(WebSocketEvent)` is the workaround.

### SkeletonView (3.00 → 3.00, unchanged)

**What changed**: `Foundation.IndexPath` → `Foundation.NSIndexPath` fix via type database expansion.

**Remaining issues**: `showSkeleton()`/`hideSkeleton()` UIView extensions not bound. `SkeletonGradient` has zero members. Proxy `Dispose()` is a no-op (memory leak). `UIKit.NSTextAlignment`/`UIEdgeInsets` fall to `AnyType`.

### Mixpanel (2.90 → 2.90, unchanged)

**What changed**: Nothing.

**Remaining issues**: `track(event:properties:)` completely absent from `MixpanelInstance`. All 6 methods on `Mixpanel` static class are SB0002. `IMixpanelType` has no factory methods for C# primitives. The library cannot perform its primary function from C#.

### BlinkID (3.55 → 3.55, unchanged)

**What changed**: Nothing.

**Remaining issues**: `SwiftOptional<DateResult<StringResult>>` leaks in 9 properties (bound-generic projection failure). `DateResult<SwiftString>` in 5 MRZ properties. 12 `value0` unnamed parameters.

### Stripe (3.20 → 3.55, +0.35)

| Category | v1 | v2 | Δ |
|---|---|---|---|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 3 | 3.5 | +0.5 |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 3 | 4 | +1 |
| ErrorHandling | 3 | 3 | — |
| Protocols | 3 | 3.5 | +0.5 |
| Noise | 3 | 3 | — |
| Completeness | 3 | 3.5 | +0.5 |
| Overall | 3 | 3.5 | +0.5 |

**What changed**: Payment transaction lifecycle now works end-to-end. `STPAPIClient` has constructors. `ConfirmPaymentIntentAsync` / `ConfirmSetupIntentAsync` with `Task<T>` + `CancellationToken`. `PaymentSheet.PresentAsync` / `FlowController.ConfirmAsync`. Member emission rate 77%→95%. 128 `ToString()` overrides.

**Remaining issues**: Cross-module `STPAPIClient` type duplication. `STPPaymentHandler.confirmPayment` still missing (complex enum closure). `additionalAPIParameters` on concrete types. `SwiftSet<SwiftString>` on `Betas`.

### SmartCardIO (4.44 → 4.44, unchanged)

**What changed**: SB0003 diagnostics added for compile-time warnings (was runtime surprise). No functional changes.

**Remaining issues**: `TerminalFactory.GetShared()` takes `object _params` (existential `Any`). Protocol members on Swift-backed proxies mostly throw `NotSupportedException`.

### MicroblinkPlatform (4.22 → 4.33, +0.11)

**What changed**: XML doc comments added (48 `<summary>` blocks). Default-parameter overloads for `MicroblinkPlatformServiceSettings` and `MicroblinkPlatformConsent`.

**Remaining issues**: `StatusProperty` collision rename. `_delegate` parameter naming (should be `@delegate`).

### BlinkIDUX (3.60 → 3.60, unchanged)

**What changed**: Cross-module `DocumentClassInfo` `AnyType` reduced from 8 to 1. `NotSupportedException` sites reduced from 20 to 15.

**Remaining issues**: `IUXThemeProtocol` empty (21 members skipped). `CaptureService`/`SampleBuffer` are opaque shells. `AsyncStream` in interface declarations still `AnyType` (though concrete classes have `IAsyncEnumerable<T>`).

### Mappedin (4.30 → 4.30, unchanged)

**What changed**: `EditorBrowsable(Never)` improved IntelliSense experience (4,000+ lines of boilerplate now hidden).

**Remaining issues**: `THING_KEY` and other SCREAMING_CASE type names. `_object` parameter naming. 44 SB0001 Mono JIT warnings.

## Prioritized Action Items (Updated)

### 1. Emit protocol interface conformance on concrete types
- **Issue**: Struct/class types register protocol conformance in `_protocolConformanceSymbols` but do not declare `: IProtocol` on the class. This breaks protocol-typed parameter passing and composition.
- **Libraries affected**: CryptoSwift (CBC/CTR/ECB not `: IBlockMode`), Kingfisher (DefaultCacheSerializer not `: ICacheSerializer`, DefaultImageProcessor not `: IImageProcessor`), and likely others
- **Estimated effort**: Small-Medium — the conformance data exists; the class declaration emission needs to reference it
- **Classification**: Generator bug

### 2. Support generic throwing closures in method signatures
- **Issue**: Methods taking `(T) throws -> U` where `T` or `U` is generic are silently omitted. This blocks the fundamental APIs of several libraries.
- **Libraries affected**: GRDB (`read`/`write`/`asyncRead`/`asyncWrite`), and any library with generic callback patterns
- **Estimated effort**: Large — requires monomorphized or type-erased closure bridge generation
- **Classification**: Design gap

### 3. Resolve `Self` return types to concrete types on conforming classes
- **Issue**: When `KF.Builder` conforms to `KFOptionSetter` (which uses `-> Self`), the generator should emit the builder methods on `Builder` with `Builder` return type. Currently the methods either don't appear or return `AnyType`.
- **Libraries affected**: Kingfisher (30+ builder methods), KeychainAccess (6 builder methods)
- **Estimated effort**: Medium — requires propagating concrete type resolution from protocol conformance to method emission
- **Classification**: Design gap

### 4. Provide concrete overloads for empty marker protocol interfaces
- **Issue**: When a protocol exists solely to allow primitives as parameters (e.g., SnapKit's `IConstraintOffsetTarget`), and C# primitives cannot implement interfaces, the generator should emit `double`/`float`/`int` convenience overloads.
- **Libraries affected**: SnapKit (Offset, Inset, MultipliedBy, DividedBy all uncallable)
- **Estimated effort**: Medium — requires detecting the "marker protocol for primitives" pattern and emitting typed overloads
- **Classification**: Design gap

### 5. Cross-module type unification for Swift extensions
- **Issue**: When module B extends a type from module A, the generated partial classes live in different namespaces. Consumers must know which namespace to use for which members.
- **Libraries affected**: Stripe (STPAPIClient across 5 modules)
- **Estimated effort**: Medium — requires either namespace merging or C# extension method generation
- **Classification**: Generator gap

### 6. Project `SwiftSet<T>` to `IReadOnlySet<T>`
- **Issue**: `SwiftSet<SwiftString>` leaks into public APIs where `IReadOnlySet<string>` would be idiomatic.
- **Libraries affected**: Kingfisher (`TrustedHosts`), Stripe (`Betas`)
- **Estimated effort**: Small — similar to existing `SwiftArray<T>` → `IReadOnlyList<T>` projection
- **Classification**: Generator gap

### 7. Bound-generic optional projection
- **Issue**: `Optional<GenericType<Concrete>>` falls back to `SwiftOptional<...>` — nested generics bypass the projection pipeline.
- **Libraries affected**: BlinkID (9 date properties)
- **Estimated effort**: Medium — requires recursive projection through nested generic type arguments
- **Classification**: Generator bug

### 8. Expand Apple SDK type database (NSTextAlignment, UIEdgeInsets, CGColorSpace)
- **Issue**: Common Apple framework types still cause `AnyType` fallbacks.
- **Libraries affected**: SkeletonView (NSTextAlignment, UIEdgeInsets), Lottie (CGColorSpace)
- **Estimated effort**: Small per type
- **Classification**: Generator gap (type database)

### 9. Fix `Method` suffix and `_value`/`_object` parameter naming
- **Issue**: Collision-avoidance renames produce un-idiomatic C# names. Swift's `_` external label produces `_value` instead of `value`.
- **Libraries affected**: KeychainAccess (`AccessibilityMethod`), Mappedin (`_object`), Mixpanel (`_event`), Starscream (`_string`)
- **Estimated effort**: Small — naming heuristic adjustment
- **Classification**: Generator bug

### 10. Wire `ISwiftHashable` into `GetHashCode()`
- **Issue**: Some types have `GetHashCode()` returning 0 despite having `ISwiftHashable` conformance.
- **Libraries affected**: KeychainAccess (AuthenticationPolicy), potentially others
- **Estimated effort**: Small — route through Swift hash function
- **Classification**: Generator bug

## Phase 2 Retrospective

### Did we hit the KPIs?

| KPI | Target | Actual | Status |
|---|---|---|---|
| Overall average score | >3.80 | 3.45 | **Not met** |
| All 10 action items addressed | 10/10 | 10/10 | **Met** |
| No library below 3.0 | 0 below 3.0 | 2 below 3.0 (RxSwift 2.40, Mixpanel 2.90) | **Not met** |
| Compile gate | 32/32 | 32/32 | **Met** |
| No regressions | 0 | 0 code regressions (2 scoring corrections) | **Met** |

### What should Phase 3 prioritize?

Based on the v2 results, Phase 3 (Binding Polish & Safety) should focus on:

1. **Protocol conformance emission (#1 above)** — small effort, broad impact. Fixes CryptoSwift and Kingfisher's core composition patterns.

2. **Concrete `Self` resolution (#3 above)** — medium effort, high impact for fluent builder APIs. Would transform Kingfisher from 3.10 to potentially 4.0+.

3. **swiftinterface/actor support (P1)** — addresses the structural limitation that actors are opaque shells. Would improve BlinkIDUX, and any future actor-heavy libraries.

4. **Finalizer safety (P2)** — proxy `Dispose()` no-ops and potential double-free issues in the SafeHandle layer.

5. **Cross-module type unification (#5 above)** — particularly important for Stripe's multi-module ecosystem.

The >3.80 target is achievable if items #1-#3 are completed, as they would raise Kingfisher (+0.5-1.0), CryptoSwift (+0.5), Alamofire (+0.3), and KeychainAccess (+0.3). The bottom-tier libraries (RxSwift, Mixpanel) require deeper structural changes (protocol extension method emission, `[String: Any]` projection) that are more Phase 4-level work.
