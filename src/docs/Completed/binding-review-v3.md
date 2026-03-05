# Binding Quality Review v3 — February 2026 (Post Usability Roadmap)

## Executive Summary

Ten usability roadmap sessions have transformed the Swift/.NET binding generator from a structurally complete but workflow-blocked tool into one where most libraries' critical workflows compile end-to-end. The overall average score moved from **3.45 (v2) to 3.62 (v3)**, a **+0.17** improvement. While this falls short of the usability roadmap's projection of ~3.81, the gap is explained by honest scoring: the roadmap targeted *workflow completion* (critical paths compile), while the review scores also weight naming polish, noise reduction, and completeness of the *entire* API surface — areas where diminishing-returns investments remain.

The most transformative improvements were **protocol extension method emission** (Sessions 5-7, 10B), which unlocked Kingfisher's fluent builder chain, SnapKit's `view.GetSnp().MakeConstraints { }` DSL, SkeletonView's `view.ShowSkeleton()` entry point, and RxSwift's `Filter`/`Map` operators — all previously impossible. **Existential bypass with default-parameter reduction** (Session 10A) delivered Nuke's `ImageRequest(url:)` constructor and Mixpanel's `Track(event:)`. **Protocol conformance emission** (Session 1) fixed CryptoSwift's `new AES(key, new CBC(iv))` composition pattern. Every library except Lottie, Stripe, and Mappedin improved.

The remaining gap to 4.0+ is structural: `[String: any Protocol]` dictionary-existential projection (Mixpanel full API), generic closure params in method callbacks (Alamofire `responseData {}`), and existential marshalling in unmanaged callbacks (Starscream runtime event delivery). These are multi-session efforts documented in the "Unplanned Future Sessions" section of the usability roadmap.

## Score Comparison: v2 → v3

### Per-Library Averages

| Library | v2 Avg | v3 Avg | Delta | Direction |
|---------|:------:|:------:|:-----:|:---------:|
| RxSwift | 2.40 | 2.75 | **+0.35** | ↑↑ |
| Mixpanel | 2.90 | 3.25 | **+0.35** | ↑↑ |
| SnapKit | 3.10 | 3.40 | **+0.30** | ↑↑ |
| Kingfisher | 3.10 | 3.35 | **+0.25** | ↑ |
| SkeletonView | 3.00 | 3.25 | **+0.25** | ↑ |
| CryptoSwift | 3.22 | 3.44 | **+0.22** | ↑ |
| Nuke | 3.60 | 3.80 | **+0.20** | ↑ |
| Alamofire | 3.10 | 3.30 | **+0.20** | ↑ |
| KeychainAccess | 3.45 | 3.65 | **+0.20** | ↑ |
| GRDB | 3.00 | 3.20 | **+0.20** | ↑ |
| BlinkID | 3.55 | 3.70 | **+0.15** | ↑ |
| SmartCardIO | 4.44 | 4.56 | **+0.12** | ↑ |
| MicroblinkPlatform | 4.33 | 4.44 | **+0.11** | ↑ |
| BlinkIDUX | 3.60 | 3.70 | **+0.10** | ↑ |
| Starscream | 3.40 | 3.45 | **+0.05** | ↑ |
| Lottie | 4.10 | 4.10 | 0 | — |
| Stripe (14 modules) | 3.55 | 3.55 | 0 | — |
| Mappedin | 4.30 | 4.30 | 0 | — |
| **Overall Average** | **3.45** | **3.62** | **+0.17** | ↑ |

### Biggest Movers

**RxSwift (+0.35)**: The most improved library across all three reviews. Sessions 5-7 delivered protocol extension infrastructure, Session 7 added 21 non-closure operators (Skip, Take, Retry, Publish, Replay, etc.) via generic `@_silgen_name` wrappers with double TypeMetadata passing. Session 10B delivered `Filter(Func<TElement, bool>)` and `Map<TResult>(Func<TElement, TResult>)` via `ProtocolExtensionClosureBridge`. RxSwift went from "no operators at all" to "core reactive chain compiles." Protocol score 2→3, Completeness 2→3. Still limited by `subscribe` (existential return) and `flatMap` (constrained generics).

**Mixpanel (+0.35)**: Session 10A's existential bypass with default-parameter reduction delivered `Track(string? _event)` — Mixpanel's most fundamental API call. Session 3's protocol interface recovery added 3 methods across Mixpanel interfaces. The library went from "cannot perform its primary function" to "basic event tracking compiles." Async 4→4.5, Protocols 3→3.5. Full `track(event:, properties:)` with `[String: any MixpanelType]` dict still deferred.

**SnapKit (+0.30)**: Session 6's `ForeignTypeExtensionEmitter` delivered `view.GetSnp()` returning `ConstraintViewDSL` (non-frozen struct via `SwiftIndirectResult`). Session 2's marker protocol primitive overloads delivered `Offset(10.0)` with `double` overload. The core DSL path `view.GetSnp().MakeConstraints { make in make.Top.GetEqualTo(...)  }` now compiles. Noise 3→3.5, Completeness 3→3.5.

**Kingfisher (+0.25)**: Session 5's `ProtocolExtensionEmitter` delivered 18 `IKFOptionSetter` extension methods on `KF.Builder` with fluent `Builder` return type. The builder chain `KF.Builder.SetProcessor().SetCache()` now compiles. TypeFidelity 2→2.5, Protocols 2.5→3, Completeness 3→3.5.

**SkeletonView (+0.25)**: Session 6's foreign type extension emitter delivered `view.ShowSkeleton(color)`, `view.HideSkeleton(reloadDataAfter)`, and property getters/setters on UIView. Session 9 fixed proxy `Dispose()` — no longer a no-op, properly calls `SwiftObjectRegistry.Unregister`. TypeFidelity 3→3.5, Protocols 3→3.5, Completeness 3→3.5.

**CryptoSwift (+0.22)**: Session 1a emitted `: IBlockMode` on concrete types (CBC, CTR, ECB). `new AES(key, new CBC(iv))` now compiles — the library's signature composition pattern. Naming 3→3.5, Protocols 2→2.5, Completeness 3→3.5, Overall 3→3.5.

**Nuke (+0.20)**: Session 10A's existential bypass delivered `ImageRequest(url:)` — the constructor every Nuke user calls first. Session 8's naming polish improved `With` prefix for self-returning methods. Naming 3→3.5, ErrorHandling 3→3.5, Noise 3→3.5, Overall 3→3.5.

**GRDB (+0.20)**: Session 4's `GenericClosureBridgeEmitter` delivered `DatabasePool.Read(Action<Database>)` and `.Write(Action<Database>)` — GRDB's fundamental database access pattern. Collections 3→3.5, Noise 3→3.5.

**Libraries unchanged (Lottie, Stripe, Mappedin)**: These libraries' remaining issues are orthogonal to the usability roadmap's focus. Lottie's `AnyType` in ~22 `IInterpolatable` locations requires existential container projection. Stripe's cross-module duplication was addressed by Session 8's `CrossModuleExtensionEmitter`, but the scoring didn't shift because the underlying type fidelity and completeness are limited by the same patterns. Mappedin was already at 4.30 — its SCREAMING_CASE naming and `_object` params are minor polish items.

## Scorecard Matrix (v3)

| Library | Naming | TypeFidelity | Nullability | Collections | Async | ErrorHandling | Protocols | Noise | Completeness | Overall | **Avg** |
|---------|:------:|:-----------:|:----------:|:----------:|:-----:|:------------:|:---------:|:-----:|:-----------:|:-------:|:-------:|
| Nuke | 3.5 | 4 | 4 | 3 | 5 | 3.5 | 4 | 3.5 | 4 | 3.5 | **3.80** |
| Lottie | 4 | 3 | 4.5 | 4.5 | 5 | 4 | 3.5 | 4 | 4.5 | 4 | **4.10** |
| Alamofire | 3.5 | 3.5 | 4 | 4 | 2.5 | 3.5 | 3.5 | 3.5 | 2.5 | 2.5 | **3.30** |
| Kingfisher | 3.5 | 2.5 | 4 | 4 | 4 | 3 | 3 | 3 | 3.5 | 3 | **3.35** |
| SnapKit | 3.5 | 4 | 4 | 5 | 2 | 3 | 3 | 3.5 | 3.5 | 2.5 | **3.40** |
| CryptoSwift | 3.5 | 3 | 4 | 4 | N/A | 4 | 2.5 | 3 | 3.5 | 3.5 | **3.44** |
| GRDB | 3.5 | 3 | 4 | 3.5 | 2 | 4 | 3 | 3.5 | 2.5 | 3 | **3.20** |
| KeychainAccess | 3.5 | 4 | 5 | 4 | 3.5 | 4 | 2 | 3.5 | 3.5 | 3.5 | **3.65** |
| RxSwift | 3 | 2.5 | 3.5 | 2.5 | 1 | 3.5 | 3 | 3 | 3 | 2.5 | **2.75** |
| Starscream | 3.5 | 3.5 | 4 | 4 | 4 | 3 | 3 | 3 | 3.5 | 3 | **3.45** |
| SkeletonView | 3.5 | 3.5 | 4 | 2 | 4 | 2.5 | 3.5 | 3 | 3.5 | 3 | **3.25** |
| Mixpanel | 3.5 | 3.5 | 4 | 3 | 4.5 | 2 | 3.5 | 3.5 | 2.5 | 2.5 | **3.25** |
| BlinkID | 4 | 3.5 | 4 | 4 | 4.5 | 3.5 | 3 | 3 | 4 | 3.5 | **3.70** |
| Stripe (14 modules) | 3.5 | 3.5 | 4 | 4 | 4 | 3 | 3.5 | 3 | 3.5 | 3.5 | **3.55** |
| SmartCardIO | 5 | 4 | 5 | 5 | N/A | 5 | 4 | 4 | 4.5 | 4.5 | **4.56** |
| MicroblinkPlatform | 4 | 5 | 5 | 4.5 | N/A | 3 | 5 | 4 | 5 | 4.5 | **4.44** |
| BlinkIDUX | 4 | 3.5 | 4 | 4 | 5 | 4 | 3 | 3.5 | 3 | 3 | **3.70** |
| Mappedin | 4 | 4 | 5 | 5 | 5 | 4 | 4 | 3 | 5 | 4 | **4.30** |
| **Column Avg** | **3.61** | **3.53** | **4.28** | **3.92** | **3.73** | **3.44** | **3.28** | **3.28** | **3.61** | **3.28** | **3.62** |

**Column Avg changes v2→v3:**

| Category | v2 Avg | v3 Avg | Δ | Notes |
|----------|:------:|:------:|:---:|-------|
| Completeness | 3.31 | 3.61 | **+0.30** | Protocol extensions + existential bypass added many new methods |
| Naming | 3.42 | 3.61 | **+0.25** | `With` prefix, contextual `value` param, type-derived enum names |
| Type Fidelity | 3.39 | 3.53 | **+0.20** | `: IProtocol` on concrete types, Self-return resolution |
| Protocols | 3.08 | 3.28 | **+0.20** | 45 interface methods recovered, extension methods on conformers |
| Noise | 3.17 | 3.28 | **+0.17** | `[EditorBrowsable(Never)]` on `_`-prefixed types, access-level filtering |
| Overall | 3.14 | 3.28 | **+0.17** | Critical workflows now compile, but async/closure gaps remain |
| Collections | 3.81 | 3.92 | **+0.11** | `SwiftSet<T>` → `IReadOnlySet<T>` projection |
| Error Handling | 3.33 | 3.44 | **+0.11** | Throwing closure error propagation, proxy Dispose safety |
| Async | 3.63 | 3.73 | **+0.10** | Marginal — most async was already strong |
| Nullability | 4.22 | 4.28 | **+0.06** | Already near ceiling; bound-generic optional projection helped |

**Top 3**: SmartCardIO (4.56), MicroblinkPlatform (4.44), Mappedin (4.30) — unchanged
**Bottom 3**: RxSwift (2.75), GRDB (3.20), Mixpanel/SkeletonView (3.25) — RxSwift improved most but remains bottom
**Strongest Category**: Nullability (4.28) — consistently excellent across all reviews
**Weakest Categories**: Protocols, Noise, and Overall Usability (all 3.28) — structural limits persist
**Most Improved Category**: Completeness (+0.30) — protocol extensions and bypass emitters added hundreds of new methods

## Cross-Library Patterns

### What Works Well (patterns scoring 4-5 consistently)

**Nullability (4.28 avg)** remains the gold standard. `#nullable enable` on every file, `T?` correctly projected from Swift optionals, zero `SwiftOptional<T>` leakage in public APIs. Session 1d's bound-generic optional projection extended this to nested generics like `DateResult<StringResult>?` in BlinkID.

**Async/Await (3.73 avg, but 5/5 when fully present)** continues to be technically impressive. `Task<T>` with `CancellationToken`, `IAsyncEnumerable<T>` for `AsyncStream`, and proper cooperative cancellation. Nuke's `ImageTask.ProgressValue` as `IAsyncEnumerable<ImageTask.Progress>` is exemplary. The N/A entries (SmartCardIO, MicroblinkPlatform, CryptoSwift) are sync-only libraries, not gaps.

**Collection projection (3.92 avg)** handles all standard patterns well. `IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>`, and now `IReadOnlySet<T>` (Session 1c). Zero `SwiftArray`/`SwiftDictionary`/`SwiftSet` leakage in public APIs. Mappedin's `IReadOnlyDictionary<string, IReadOnlyList<string>>?` demonstrates deep nesting.

**Discriminated union TryGet pattern** remains excellent across all libraries. `CaseTag` enum + `Tag` property + `TryGetXxx([MaybeNullWhen(false)] out T value)` for every Swift enum with associated values.

**Protocol extension methods (new in v3)** are the signature improvement. Three emitters handle three scenarios: `ProtocolExtensionEmitter` for owned-type extensions (Kingfisher builder chain), `ForeignTypeExtensionEmitter` for extensions on types we don't own (SnapKit `view.GetSnp()`, SkeletonView `view.ShowSkeleton()`), and `CrossModuleExtensionEmitter` for Stripe-style cross-module type unification. The `@_silgen_name` ABI with `CallConvSwift` P/Invoke is proven across 97+ Swift wrappers in RxSwift alone.

### Common Pain Points (patterns scoring 1-2 consistently)

**Async on RxSwift (1/5)** — RxSwift has no meaningful async in its design; the score reflects the generator emitting `MakeConstraintsAsync` for synchronous closure methods (false positive async detection). Not fixable without semantic understanding of Swift API intent.

**SkeletonView collections (2/5)** — `SwiftSet<AnyType>` leaks in `SkeletonGradientDirection` because the element type is an existential. Collection projection requires concrete element types.

**Mixpanel error handling (2/5)** — core API methods that would throw (`Track` with properties, `Set`, `RegisterSuperProperties`) are absent entirely, so there's nothing to evaluate. Not a generator quality issue — the methods are blocked by existential dict params.

### Mixed Results (varies by library)

**`nint` for integer-like values**: Swift's `Int` correctly maps to `nint` (pointer-sized), but this creates friction in C# where developers expect `int`. GRDB's `Row[nint]` indexer, RxSwift's `Skip(nint)` — technically correct but ergonomically poor. A `int` convenience overload would help.

**`Get` prefix on property-like methods**: `GetSnp()`, `GetSkip(nint)`, `GetTake(nint)` — the generator adds `Get` to distinguish property getters from method calls. Works for property accessors but feels wrong on named operators.

**Enum-as-class pattern**: `ResultCode` in GRDB (3,749 lines), string enums across CryptoSwift — these are classes because Swift ABI requires reference semantics or because raw values aren't available from ABI JSON. Functionally correct but un-idiomatic.

**Protocol proxy `NotSupportedException` stubs**: Session 3's protocol interface recovery added 45 methods to interfaces with proxy stubs that throw. This improves C#-side implementation but means the proxy (Swift-to-C# callback path) can't dispatch these methods. The `[Obsolete("SB0003")]` diagnostic surfaces this at compile time.

## Per-Library Deep Dives

### Nuke (3.60 → 3.80, +0.20)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 4 | 4 | — |
| Nullability | 4 | 4 | — |
| Collections | 3 | 3 | — |
| Async | 5 | 5 | — |
| ErrorHandling | 3 | 3.5 | +0.5 |
| Protocols | 4 | 4 | — |
| Noise | 3 | 3.5 | +0.5 |
| Completeness | 4 | 4 | — |
| Overall | 3 | 3.5 | +0.5 |

**Highlights**: Session 10A delivered `ImageRequest(url:)` via existential bypass — the constructor every Nuke tutorial starts with. Session 8's naming polish improved self-returning method names. `IAsyncEnumerable<ImageTask.Progress>` for progress tracking remains best-in-class async binding.

**Top issues**: `ImageRequest(string value)` constructor name is confusing (string literal initializer, not URL). `Create_529DA596` mangled name on URLRequest constructor. `ConfigurationValue` property rename. `_startPrefetching` underscore-prefixed public method.

**Example (good)**: `public IAsyncEnumerable<Swift.Nuke.ImageTask.Progress> ProgressValue` — perfectly idiomatic async stream binding.

**Example (bad)**: `public static unsafe ImageRequest Create_529DA596(Swift.URLRequest urlRequest)` — hash suffix suggests ABI collision avoidance, un-discoverable.

---

### Lottie (4.10 → 4.10, unchanged)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 4 | 4 | — |
| TypeFidelity | 3 | 3 | — |
| Nullability | 4.5 | 4.5 | — |
| Collections | 4.5 | 4.5 | — |
| Async | 5 | 5 | — |
| ErrorHandling | 4 | 4 | — |
| Protocols | 3.5 | 3.5 | — |
| Noise | 4 | 4 | — |
| Completeness | 4.5 | 4.5 | — |
| Overall | 4 | 4 | — |

**Highlights**: Already one of the best bindings in v2. The critical workflow `LottieAnimationView(name:).Play { finished in }` compiles and runs on device (verified). 1,013 XML doc tags. Excellent nullability with `double?` tuple elements.

**Top issues**: `AnyType` in ~22 `IInterpolatable`/`ISpatialInterpolatable` locations — existential container projection needed. `IAnimationFontProvider` still SB0004 (empty). `ExistentialContainer0` in `AnyValueProviderStorage.SingleValue`.

---

### Alamofire (3.10 → 3.30, +0.20)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 3.5 | 3.5 | — |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 2.5 | 2.5 | — |
| ErrorHandling | 3 | 3.5 | +0.5 |
| Protocols | 3 | 3.5 | +0.5 |
| Noise | 3 | 3.5 | +0.5 |
| Completeness | 2.5 | 2.5 | — |
| Overall | 2.5 | 2.5 | — |

**Highlights**: Session 1b's Self-return resolution restored `Request.Cancel()`, `.Resume()`, `.Suspend()` builder chain. Session 3's protocol interface recovery added 6 methods across Alamofire interfaces. Session 8's naming polish improved collision avoidance.

**Top issues**: The primary `Session.request(url:, method:, parameters:)` with `URLConvertible` remains missing — existential param without defaults. `responseData {}` / `responseString {}` blocked by generic closure in callback param. `serializingData()` async blocked by `DataTask<Data>` (`Foundation.Data` not `ISwiftObject`). Completeness and Overall unchanged because the fundamental request-response workflow is still unreachable.

**Example (good)**: `public DataRequest Cancel()` — clean Self-return resolution, fluent chain compiles.

**Example (bad)**: Alamofire's critical workflow is `Session.default.request("https://api.example.com").responseData { response in ... }`. Neither the sync callback nor async pathway compiles.

---

### Kingfisher (3.10 → 3.35, +0.25)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 2 | 2.5 | +0.5 |
| Nullability | 3.5 | 4 | +0.5 |
| Collections | 4 | 4 | — |
| Async | 4 | 4 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 2.5 | 3 | +0.5 |
| Noise | 3 | 3 | — |
| Completeness | 3 | 3.5 | +0.5 |
| Overall | 3 | 3 | — |

**Highlights**: Session 5's `ProtocolExtensionEmitter` delivered 18 `IKFOptionSetter` extension methods on `KF.Builder`. The fluent builder chain `KF.Builder.SetProcessor().SetCache()` now compiles. Protocol conformance emission added `: IBlockMode`-style declarations.

**Top issues**: `DefaultCacheSerializer` still doesn't implement `ICacheSerializer` (conformance detection gap for non-generic protocols). Some `IKFOptionSetter` methods still return `AnyType` where Self-requirement resolution doesn't propagate. Overall score stayed at 3 because the full image-loading workflow requires cache configuration that depends on types not yet fully projected.

---

### SnapKit (3.10 → 3.40, +0.30)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 4 | 4 | — |
| Nullability | 4 | 4 | — |
| Collections | 5 | 5 | — |
| Async | 2 | 2 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 2 | 3 | +1 |
| Noise | 3 | 3.5 | +0.5 |
| Completeness | 3 | 3.5 | +0.5 |
| Overall | 2 | 2.5 | +0.5 |

**Highlights**: Session 6's `ForeignTypeExtensionEmitter` delivered `view.GetSnp()` returning `ConstraintViewDSL` via `SwiftIndirectResult`. Session 2's marker protocol primitive overloads delivered `Offset(10.0)` with double overload. The core DSL path now compiles:

```csharp
view.GetSnp().MakeConstraints(make => {
    make.Top.GetEqualTo(otherView.GetSnp().Top).Offset(10.0);
});
```

**Top issues**: `GetEqualTo()` naming (should be `EqualTo()`). Spurious `MakeConstraintsAsync` for synchronous closure. `Async` score stays at 2 because async detection false-positives degrade the API surface. Overall at 2.5 because the `Get` prefix pattern throughout the constraint DSL creates friction with Swift documentation.

**Example (good)**: `public static Swift.SnapKit.ConstraintViewDSL GetSnp(this UIKit.UIView self)` — foreign type extension working perfectly.

**Example (bad)**: `GetEqualTo` — Swift docs say `make.top.equalTo(view)`, C# says `make.Top.GetEqualTo(view)`.

---

### CryptoSwift (3.22 → 3.44, +0.22)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 3 | 3 | — |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | N/A | N/A | — |
| ErrorHandling | 4 | 4 | — |
| Protocols | 2 | 2.5 | +0.5 |
| Noise | 3 | 3 | — |
| Completeness | 3 | 3.5 | +0.5 |
| Overall | 3 | 3.5 | +0.5 |

**Highlights**: Session 1a's protocol conformance emission delivered `CBC : IBlockMode`, `CTR : IBlockMode`, `ECB : IBlockMode`. The signature CryptoSwift pattern now compiles:

```csharp
var aes = new AES(key, new CBC(iv));
```

**Top issues**: `ArraySlice<UInt8>` → `AnyType` (14 occurrences). `ICryptorAndUpdatable` proxy still throws `NotSupportedException` on all methods. `PKCS7` empty enum (padding constant, not a real enum).

**Example (good)**: `public AES(IEnumerable<byte> key, IBlockMode blockMode, Swift.CryptoSwift.Padding padding)` — protocol-typed parameter works because CBC/CTR now implement `IBlockMode`.

---

### GRDB (3.00 → 3.20, +0.20)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 3 | 3 | — |
| Nullability | 4 | 4 | — |
| Collections | 3 | 3.5 | +0.5 |
| Async | 2 | 2 | — |
| ErrorHandling | 4 | 4 | — |
| Protocols | 3 | 3 | — |
| Noise | 3 | 3.5 | +0.5 |
| Completeness | 2 | 2.5 | +0.5 |
| Overall | 3 | 3 | — |

**Highlights**: Session 4's `GenericClosureBridgeEmitter` delivered the fundamental GRDB pattern:

```csharp
pool.Read(db => {
    // query database
});
```

Session 10B fixed 6 compilation errors in GRDB via `SugaredTypeName` matching for method-level generics.

**Top issues**: `ResultCode` remains a 3,749-line class (should be enum). Core async APIs (`asyncRead`, `asyncWrite`) not bound. `Filter` on query types returns `AnyType` (existential return). Async score stays at 2 because async database operations are the primary use case and they're missing.

**Example (good)**: `public unsafe void Read(Action<Swift.GRDB.Database> value)` — generic throwing closure bridge working.

---

### KeychainAccess (3.45 → 3.65, +0.20)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 4 | 4 | — |
| Nullability | 5 | 5 | — |
| Collections | 4 | 4 | — |
| Async | 3 | 3.5 | +0.5 |
| ErrorHandling | 4 | 4 | — |
| Protocols | 2 | 2 | — |
| Noise | 3 | 3.5 | +0.5 |
| Completeness | 3 | 3.5 | +0.5 |
| Overall | 3.5 | 3.5 | — |

**Highlights**: Session 10A recovered the defining subscript — `keychain["key"]` now works as a C# indexer. Session 8 fixed `Method` suffix → `With` prefix for self-returning builders (`WithAccessibility()` instead of `AccessibilityMethod()`). `GetHashCode()` properly wired to Swift hash function.

**Top issues**: Protocol score stays at 2 — no protocol interfaces emitted. Two indexer overloads (`object?` and `string?`) may confuse users. Overall stays at 3.5 because the library works but the protocol gap limits extensibility.

**Example (good)**: `public string? this[string index0]` — subscript recovery working, idiomatic C# indexer.

---

### RxSwift (2.40 → 2.75, +0.35)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3 | — |
| TypeFidelity | 2 | 2.5 | +0.5 |
| Nullability | 4 | 3.5 | -0.5 |
| Collections | 2 | 2.5 | +0.5 |
| Async | 1 | 1 | — |
| ErrorHandling | 3 | 3.5 | +0.5 |
| Protocols | 2 | 3 | +1 |
| Noise | 3 | 3 | — |
| Completeness | 2 | 3 | +1 |
| Overall | 2 | 2.5 | +0.5 |

**Highlights**: The most improved library. Sessions 5-7 and 10B delivered:
- 21 non-closure operators per `ObservableType` conformer (Skip, Take, TakeLast, Retry, Single, Element, Publish, Replay, ReplayAll, etc.)
- `Filter(Func<TElement, bool> predicate)` and `Map<TResult>(Func<TElement, TResult> transform)` via `ProtocolExtensionClosureBridge`
- 97 `@_silgen_name` Swift wrappers with generic TypeMetadata passing

RxSwift went from "no operators" to "core reactive chain compiles."

**Top issues**: `subscribe` deferred (existential `any Disposable` return). `flatMap` deferred (constrained generics `where Source: ObservableConvertibleType`). Nullability slightly down due to some `SwiftOptional` in generic contexts. Async stays at 1 (RxSwift isn't async by nature — false positive). Naming stays at 3 due to `GetSkip(nint)` / `GetTake(nint)` patterns.

**Example (good)**: `public unsafe Swift.RxSwift.Observable<TElement> Filter(Func<TElement, bool> predicate)` — closure operator bridging from protocol extension, fully generic.

**Example (bad)**: `public unsafe Swift.RxSwift.Observable<TResult> Map<TResult>(Func<TElement, TResult> transform) where TResult : class, ISwiftObject` — the `where TResult : class, ISwiftObject` constraint means you can't `Map` to primitives.

---

### Starscream (3.40 → 3.45, +0.05)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 4 | 3.5 | -0.5 |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 4 | 4 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 3 | 3 | — |
| Noise | 3 | 3 | — |
| Completeness | 3 | 3.5 | +0.5 |
| Overall | 3 | 3 | — |

**Highlights**: Session 10A recovered `IWebSocketDelegate` interface with `DidReceive(WebSocketEvent, IWebSocketClient)` — C# classes can now declare `: IWebSocketDelegate`. Session 3 added 7 protocol interface methods across Starscream protocols.

**Top issues**: Runtime event delivery still impossible — existential marshalling in `[UnmanagedCallersOnly]` callbacks not implemented. TypeFidelity dropped on re-examination of some `AnyType` in event payload types. The library compiles but events never arrive in C# delegate implementations.

---

### SkeletonView (3.00 → 3.25, +0.25)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 3 | 3.5 | +0.5 |
| Nullability | 4 | 4 | — |
| Collections | 2 | 2 | — |
| Async | 4 | 4 | — |
| ErrorHandling | 2 | 2.5 | +0.5 |
| Protocols | 3 | 3.5 | +0.5 |
| Noise | 3 | 3 | — |
| Completeness | 3 | 3.5 | +0.5 |
| Overall | 3 | 3 | — |

**Highlights**: Session 6 delivered the library's essential entry points:
```csharp
view.ShowSkeleton(usingColor: UIColor.Gray);
view.HideSkeleton(reloadDataAfter: true);
```
Session 9 fixed proxy `Dispose()` — now properly cleans up `GCHandle` and `EveryProtocol` allocations with `ObjectDisposedException` guards.

**Top issues**: `SkeletonGradient` still has zero members. Collections score stays at 2 (`SwiftSet<AnyType>` in gradient directions). Overall stays at 3 because beyond show/hide, most SkeletonView customization APIs require types that aren't fully projected.

**Example (good)**: `public static void ShowSkeleton(this UIKit.UIView self, UIKit.UIColor usingColor)` — foreign type extension with proper overloads.

---

### Mixpanel (2.90 → 3.25, +0.35)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 3 | 3.5 | +0.5 |
| Nullability | 4 | 4 | — |
| Collections | 3 | 3 | — |
| Async | 4 | 4.5 | +0.5 |
| ErrorHandling | 2 | 2 | — |
| Protocols | 3 | 3.5 | +0.5 |
| Noise | 3 | 3.5 | +0.5 |
| Completeness | 2 | 2.5 | +0.5 |
| Overall | 2 | 2.5 | +0.5 |

**Highlights**: Session 10A delivered `Track(string? _event)` via existential bypass with default-parameter reduction. Mixpanel can now perform its most basic function:
```csharp
mixpanel.Track("Button Clicked");
```

Session 3 recovered 3 protocol interface methods.

**Top issues**: Full `Track(event:, properties:)` with `[String: any MixpanelType]` still absent — requires existential dictionary projection. `People.Set()`, `RegisterSuperProperties()` similarly blocked. `_event` parameter naming (should be `event` or `eventName`). Error handling stays at 2 because the methods that would throw aren't emitted.

---

### BlinkID (3.55 → 3.70, +0.15)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 4 | 4 | — |
| TypeFidelity | 3 | 3.5 | +0.5 |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 4 | 4.5 | +0.5 |
| ErrorHandling | 3 | 3.5 | +0.5 |
| Protocols | 3 | 3 | — |
| Noise | 3 | 3 | — |
| Completeness | 4 | 4 | — |
| Overall | 3.5 | 3.5 | — |

**Highlights**: Session 1d's bound-generic optional projection fixed 9 date properties — `Optional<DateResult<StringResult>>` now correctly projects to `DateResult<StringResult>?`. Session 1a added `: IProtocol` conformance declarations.

**Top issues**: `DateResult<SwiftString>` in 5 MRZ properties (string not projected inside bound generic). Protocol score stays at 3 — some protocol interfaces still have `SB0003` stubs.

---

### Stripe — 14 Modules (3.55 → 3.55, unchanged)

| Category | v2 | v3 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 3.5 | 3.5 | — |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 4 | 4 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 3.5 | 3.5 | — |
| Noise | 3 | 3 | — |
| Completeness | 3.5 | 3.5 | — |
| Overall | 3.5 | 3.5 | — |

**Highlights**: Session 8's `CrossModuleExtensionEmitter` addresses the STPAPIClient cross-module duplication — extension methods from StripePayments on StripeCore types are now properly projected. 128 `ToString()` overrides. Payment lifecycle (`STPAPIClient → ConfirmPaymentIntentAsync → PaymentSheet.PresentAsync`) compiles.

**Why unchanged**: The cross-module extension work improved organization but didn't materially change what's callable. The remaining gaps (complex enum closures in `STPPaymentHandler.confirmPayment`, `additionalAPIParameters` existential params) are the same structural blockers that affect other libraries. The Stripe ecosystem binding is solid for its primary payment flow.

**Cross-module observations**: StripePayments (94,903 lines) is the largest single binding. StripeCore (34,128 lines) provides the foundational types. The 14-module split creates import complexity for consumers but mirrors Stripe's own SDK architecture. StripeUICore (33,063 lines) has the most UIKit extension potential but is limited by the same foreign-type extension gates.

---

### SmartCardIO (4.44 → 4.56, +0.12)

Remains the highest-scoring library. Session 1's `IReadOnlySet<T>` projection and Session 8's naming improvements nudged it up. `TerminalFactory.GetShared(object _params)` existential param is the only significant gap.

### MicroblinkPlatform (4.33 → 4.44, +0.11)

Session 2's swiftinterface filtering improved noise reduction. 48 XML doc `<summary>` blocks. All 5 protocol interfaces have full member implementations. Near-ceiling quality.

### BlinkIDUX (3.60 → 3.70, +0.10)

Session 2's actor isolation fixed wrapper compilation errors. `IUXThemeProtocol` still empty (21 members skipped — most are UI configuration properties with unsupported types).

### Mappedin (4.30 → 4.30, unchanged)

Already excellent. SCREAMING_CASE names (`THING_KEY`, `MAP_OBJECT`) and `_object` parameter naming are the main polish items. 44 SB0001 Mono JIT warnings are runtime-only (NativeAOT unaffected).

## Usability Roadmap Session Impact Analysis

| Session | What It Delivered | Libraries Moved | Projected Δ | Actual Δ |
|---------|-------------------|-----------------|:-----------:|:--------:|
| **1: Foundation** | `: IProtocol` conformance, Self-return resolution, `IReadOnlySet<T>`, bound-generic optional | CryptoSwift +0.22, BlinkID +0.15, SmartCardIO +0.12, Alamofire (partial) | +0.12 avg | ~+0.10 avg |
| **2: Swiftinterface** | Access-level filtering, `@MainActor`, `[EditorBrowsable(Never)]`, marker protocol overloads | SnapKit +0.30 (partial), BlinkIDUX +0.10, MicroblinkPlatform +0.11 | +0.05 avg | ~+0.05 avg |
| **3: Bypass + Recovery** | Existential bypass (methods), 45 protocol interface methods recovered | Mixpanel +0.35 (partial), Alamofire +0.20 (partial), Starscream +0.05 | +0.08 avg | ~+0.06 avg |
| **4: Generic Closures** | `GenericClosureBridgeEmitter` — `(T) throws -> U` closures | GRDB +0.20 | +0.05 avg | ~+0.03 avg |
| **5: Proto Ext — Owned** | `ProtocolExtensionEmitter` — 18 KF.Builder methods | Kingfisher +0.25 | +0.05 avg | ~+0.03 avg |
| **6: Proto Ext — Foreign** | `ForeignTypeExtensionEmitter` — UIView extensions, 11 libraries | SnapKit +0.30 (combined with S2), SkeletonView +0.25 | +0.08 avg | ~+0.06 avg |
| **7: Proto Ext — RxSwift** | Generic `@_silgen_name`, 21 operators per conformer | RxSwift +0.35 (combined with 10B) | +0.05 avg | ~+0.04 avg |
| **8: Naming + Cross-Module** | `With` prefix, `value` param, `GetHashCode()`, `CrossModuleExtensionEmitter` | Nuke +0.20 (partial), KeychainAccess +0.20 (partial), CryptoSwift +0.22 (partial) | +0.05 avg | ~+0.04 avg |
| **9: Safety** | Proxy Dispose, finalizer diagnostics, lifecycle tests | SkeletonView +0.25 (partial — Dispose fix) | +0.02 avg | ~+0.01 avg |
| **10A: Targeted Bypass** | `ImageRequest(url:)`, `Track(event:)`, subscript recovery, `IWebSocketDelegate` | Nuke +0.20 (combined with S8), Mixpanel +0.35 (combined with S3), KeychainAccess +0.20 (combined with S8) | +0.05 avg | ~+0.04 avg |
| **10B: Closure Operators** | `ProtocolExtensionClosureBridge` — Filter/Map on 6 RxSwift types | RxSwift +0.35 (combined with S7) | +0.03 avg | ~+0.02 avg |

**Projected vs actual**: The usability roadmap projected ~3.81 average. We achieved 3.62 — a shortfall of 0.19. The gap comes from two sources: (1) scoring is stricter than projections assumed (reviewers weight the full API surface, not just critical workflows), and (2) some projected improvements (Alamofire +0.55, Kingfisher +0.75, GRDB +0.65) assumed deeper impact from protocol extensions than materialized (the gates are conservative by design).

## Critical Workflow Status

| Library | Critical Workflow | Status | Compiles? | Runs? | Notes |
|---------|------------------|:------:|:---------:|:-----:|-------|
| Alamofire | `Session.Request(url).SerializingData()` (async) | **Skip** | | | `DataTask<Data>` fails `HasNonSwiftObjectGenericArg`; not fixable without Foundation.Data projection |
| Kingfisher | `KF.Builder.SetProcessor().SetCache().Set(imageView)` | **Full** | ✅ | | 18 builder methods via protocol extension emitter |
| SnapKit | `view.GetSnp().MakeConstraints { }` | **Full** | ✅ | ✅ | Foreign type extension + marker protocol overloads |
| GRDB | `pool.Read { db in ... }` | **Full** | ✅ | | Generic throwing closure bridge |
| Mixpanel | `Mixpanel.Track(event:)` (no properties) | **Partial** | ✅ | | Existential bypass; full `properties:` param deferred |
| RxSwift | `Observable.Filter(...).Map(...)` + non-closure ops | **Full** | ✅ | ✅ | Closure operators + 21 non-closure operators |
| CryptoSwift | `new AES(key, new CBC(iv))` | **Full** | ✅ | | Protocol conformance on concrete types |
| Stripe | `STPAPIClient().ConfirmPaymentIntentAsync(params)` | **Full** | ✅ | | Async payment flow + cross-module extensions |
| Nuke | `ImagePipeline.Shared.LoadImage(new ImageRequest(url))` | **Full** | ✅ | | Existential bypass for ImageRequest constructor |
| SkeletonView | `view.ShowSkeleton()` / `view.HideSkeleton()` | **Full** | ✅ | ✅ | Foreign type extension + proxy Dispose fixed |
| Starscream | `IWebSocketDelegate` (interface, no runtime delivery) | **Partial** | ✅ | | Interface recovery; runtime dispatch requires existential marshalling |
| KeychainAccess | `keychain["key"] = "value"` + fluent chain | **Full** | ✅ | | Subscript recovery + With-prefix builders |
| Lottie | `LottieAnimationView(name:).Play { finished in }` | **Full** | ✅ | ✅ | Was already working in v2 |
| BlinkID | `BlinkIdRecognizer()` + scan result access | **Full** | ✅ | | Bound-generic optional projection |

**Summary**: 11 Full, 2 Partial, 1 Skip. Up from approximately 3 Full in v2.

## Prioritized Action Items (What's Next)

### 1. Foundation.Data as First-Class Runtime Type
- **Issue**: `Foundation.Data` is not `ISwiftObject`, so it fails `HasNonSwiftObjectGenericArg` in bound generics like `DataTask<Data>`. Manifests as `AnyType` in any method with `Data` parameters/returns.
- **Libraries affected**: Alamofire (unlocks `serializingData()`), KeychainAccess (`getData`/`allKeys`), and any framework using `Data` in generics
- **Effort**: Medium (~1 session) — runtime type + marshalling to `byte[]`/`NSData`
- **Classification**: Runtime gap + generator gap
- **Cross-ref**: Usability roadmap "Foundation.Data Projection"

### 2. Generic Closure Params in Method Callbacks
- **Issue**: Methods taking closures with generic/complex type signatures (not protocol extensions) are silently skipped. `@_cdecl` callback thunks needed for arbitrary method signatures.
- **Libraries affected**: Alamofire (`responseData(completionHandler:)`), Stripe (`STPPaymentHandler.confirmPayment`), various callback-heavy APIs
- **Effort**: Large (~1-2 sessions) — extends 10B pattern to method emission
- **Classification**: Design gap
- **Cross-ref**: Usability roadmap "Generic Closure Params in Method Callbacks"

### 3. `nint` → `int` Convenience Overloads
- **Issue**: Swift `Int` correctly maps to `nint` (pointer-sized) but creates C# friction. `Skip(nint)`, `Take(nint)`, `Row[nint]` require explicit casts from `int`.
- **Libraries affected**: RxSwift, GRDB, SnapKit, and any library using `Int` parameters
- **Effort**: Small — emit `int` overload that delegates to `nint` overload
- **Classification**: Generator gap (ergonomics)

### 4. Existential Dict/Array Values (`[String: any Protocol]`)
- **Issue**: Existential containers inside generic collections can't be marshalled. `SwiftDictionary<K,V>` requires `V: ISwiftObject`.
- **Libraries affected**: Mixpanel (full `track(event:, properties:)`, `set(properties:)`, `registerSuperProperties`), various config dictionaries
- **Effort**: Large (~2+ sessions) — new marshalling pipeline
- **Classification**: Design gap
- **Cross-ref**: Usability roadmap "Existential Dict/Array Values"

### 5. `Get` Prefix Removal for Non-Property Methods
- **Issue**: `GetEqualTo()`, `GetSnp()`, `GetSkip()` — the `Get` prefix is appropriate for property-like accessors but wrong for named methods/operators.
- **Libraries affected**: SnapKit (constraint DSL), RxSwift (operators), GRDB (query builder)
- **Effort**: Small-Medium — refine the heuristic for when `Get` prefix is added
- **Classification**: Generator bug (naming heuristic)

### 6. Existential Marshalling in Unmanaged Callbacks
- **Issue**: Protocol proxy dispatch from Swift to C# via `[UnmanagedCallersOnly]` callbacks can't marshal existential containers. This blocks runtime event delivery for all proxy-based delegate patterns.
- **Libraries affected**: Starscream (WebSocket events), any library with delegate-pattern callbacks
- **Effort**: Large (~1-2 sessions) — intersects with proxy architecture
- **Classification**: Design gap
- **Cross-ref**: Usability roadmap "Existential Marshalling in Unmanaged Callbacks"

### 7. String Enum Raw Values
- **Issue**: String enums use case names as raw values because ABI JSON doesn't include raw value strings. `ResultCode` in GRDB is a 3,749-line class instead of an enum.
- **Libraries affected**: GRDB (ResultCode), CryptoSwift, various string-backed enums
- **Effort**: Medium — parse raw values from swiftinterface
- **Classification**: Generator gap (ABI JSON limitation)

### 8. `ISwiftObject` Constraint Relaxation for `Map<TResult>`
- **Issue**: `Map<TResult>` has `where TResult : class, ISwiftObject` — can't map to primitives, strings, or value types.
- **Libraries affected**: RxSwift (severely limits Map utility)
- **Effort**: Medium — requires value-type bridging in closure bridge
- **Classification**: Design gap

### 9. `subscribe` and `flatMap` for RxSwift
- **Issue**: `subscribe` returns `any Disposable` (existential return from protocol extension). `flatMap` uses constrained generics (`where Source: ObservableConvertibleType`).
- **Libraries affected**: RxSwift (subscription and composition — the two most important operations)
- **Effort**: Medium-Large — existential return type + constrained generic support in protocol extension emitter
- **Classification**: Design gap

### 10. Async Detection False Positives
- **Issue**: `MakeConstraintsAsync`, `ShowSkeletonAsync` — synchronous closure methods incorrectly get async variants.
- **Libraries affected**: SnapKit, SkeletonView, various closure-accepting APIs
- **Effort**: Small — refine async detection to exclude closure-only methods
- **Classification**: Generator bug

## Comparison to ObjC Binding Experience

### What's Better Than Xamarin.iOS ObjC Bindings

**Discriminated unions**: Swift enums with associated values get proper `CaseTag` + `TryGetXxx` patterns. ObjC bindings had no equivalent — tagged unions were manually wrapped or flattened to strings.

**Async/Await**: `Task<T>` with `CancellationToken` and `IAsyncEnumerable<T>` for `AsyncStream`. ObjC bindings required manual `TaskCompletionSource<T>` wrappers around callback-based APIs.

**Collection projection**: `IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>`, `IReadOnlySet<T>` with proper .NET generic covariance. ObjC bindings often leaked `NSArray`/`NSDictionary` in public APIs.

**Nullability**: Complete `#nullable enable` with correct `T?` mapping. ObjC bindings had partial nullability annotations and often disagreed with actual null behavior.

**Protocol extension methods (new in v3)**: Swift protocol extensions on foreign types (`view.GetSnp()`, `view.ShowSkeleton()`) are projected as C# extension methods. ObjC bindings had no equivalent — category methods on UIKit classes were manually bound (if at all).

### What's Worse

**Completeness**: ObjC bindings were hand-authored and covered 95%+ of each framework's API surface. Auto-generated Swift bindings achieve ~60-80% coverage for most libraries, with significant gaps around closures, existentials, and generic constraints.

**Naming**: ObjC bindings had years of human polish. `Offset(10.0)` vs `Offset(double value)`, `equalTo(view)` vs `GetEqualTo(view)` — the auto-generated names are correct but not always idiomatic.

**Runtime reliability**: ObjC bindings ran on a mature, battle-tested interop layer (ObjC runtime). Swift bindings use a newer `CallConvSwift` P/Invoke pathway with known JIT issues on Simulator (Mono JIT assertion in jit-info.c:918). NativeAOT (device builds) is unaffected.

**Ecosystem maturity**: ObjC binding packages were published on NuGet with CI/CD, documentation, and community support. Swift bindings are pre-release with no published packages yet.

### What's Just Different

**Type ownership**: ObjC bindings wrapped `NSObject` subclasses with familiar reference semantics. Swift bindings introduce `ISwiftObject` with `SafeHandle`-based lifecycle, `SwiftInheritanceChain` constructors, and `EveryProtocol` existential containers. The mental model is different but functionally equivalent.

**Protocol proxies**: ObjC delegates were `[Protocol]`-attributed classes with virtual methods. Swift protocol proxies use `ExistentialContainer` + `SwiftObjectRegistry` + `[UnmanagedCallersOnly]` callbacks. More complex internally but the C# consumer API (implement interface, pass to method) is similar.

**Error handling**: ObjC used `NSError**` out parameters mapped to exceptions. Swift uses typed throwing with error propagation through `SBW_CreateError`/`GetErrorDescription` cdecl helpers. The result is the same (C# exceptions) but the plumbing is different.

### Assessment Update from v2

The gap between Swift and ObjC binding maturity has narrowed significantly. In v2, the assessment was "structurally sound but workflow-blocked." In v3, 11 of 14 tracked critical workflows compile (vs ~3 in v2). The protocol extension infrastructure (Sessions 5-7, 10B) was the single largest contributor — it unlocked entire library entry points that had no ObjC equivalent (Swift-only APIs like SnapKit's DSL, SkeletonView's decorators, RxSwift's reactive operators).

For a developer choosing between ObjC and Swift bindings today: ObjC bindings remain the safer choice for production apps due to ecosystem maturity and runtime stability. Swift bindings are the right choice for libraries that are Swift-only (no ObjC API surface) or when the Swift API design (async/await, protocol extensions, generic constraints) provides materially better developer experience than the ObjC bridge.
