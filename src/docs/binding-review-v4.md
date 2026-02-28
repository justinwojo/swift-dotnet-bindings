# Binding Quality Review v4 — February 2026 (Post Ergonomic Polish)

## Executive Summary

Three ergonomic polish sessions (EP1-EP3), four usability sessions (S4-S6, S10B), three architectural cleanup sessions (A1-A3), and targeted fixes (SCREAMING_CASE, existential tuples) have brought the generator to **53/53 validation targets** (32 tier-1 + 21 tier-2) with zero compile failures. This review expands coverage from 18 tier-1 libraries (v3) to **39 scored libraries** (18 tier-1 + 21 tier-2).

The tier-1 average moved from **3.62 (v3) to 3.62 (v4)** — essentially flat. This surprises given the volume of polish work, but the explanation is calibration: v4 reviewers applied stricter standards to categories like Naming (penalizing remaining `nint` pollution, `Get` prefix friction, and residual `AnyType` returns that v3 accepted as "good enough") and Noise (penalizing `ExistentialContainer1` leakage and `SwiftResult<SwiftVoid, SwiftError>` in public signatures more heavily). The improvements are real — `byte[]` for `Foundation.Data`, `int` overloads for `nint` params, `MethodClosureBridge` recovering Alamofire callbacks, enum case construction/inspection — but they were absorbed by recalibration of the scoring rubric.

The tier-2 baseline is **3.22** across 21 new libraries, ranging from 2.75 (ObjectMapper — heavy protocol/generic usage) to 3.65 (AnimatedCollectionViewLayout — simple UIKit subclass). This establishes the first comprehensive view of binding quality beyond the original 18-library focus set.

Combined across all 39 scored libraries: **3.40 average**.

## Score Comparison: v3 → v4

### Tier-1 Per-Library Averages

| Library | v3 Avg | v4 Avg | Delta | Direction |
|---------|:------:|:------:|:-----:|:---------:|
| Lottie | 4.10 | 4.15 | **+0.05** | ↑ |
| SmartCardIO | 4.56 | 4.56 | 0 | — |
| MicroblinkPlatform | 4.44 | 4.44 | 0 | — |
| Mappedin | 4.30 | 4.33 | **+0.03** | ↑ |
| BlinkID | 3.70 | 3.70 | 0 | — |
| Stripe (14 modules) | 3.55 | 3.65 | **+0.10** | ↑ |
| Starscream | 3.45 | 3.55 | **+0.10** | ↑ |
| Nuke | 3.80 | 3.55 | **-0.25** | ↓↓ |
| KeychainAccess | 3.65 | 3.50 | **-0.15** | ↓ |
| CryptoSwift | 3.44 | 3.44 | 0 | — |
| Alamofire | 3.30 | 3.40 | **+0.10** | ↑ |
| Kingfisher | 3.35 | 3.40 | **+0.05** | ↑ |
| SnapKit | 3.40 | 3.40 | 0 | — |
| BlinkIDUX | 3.70 | 3.39 | **-0.31** | ↓↓ |
| Mixpanel | 3.25 | 3.30 | **+0.05** | ↑ |
| SkeletonView | 3.25 | 3.25 | 0 | — |
| GRDB | 3.20 | 3.20 | 0 | — |
| RxSwift | 2.75 | 2.95 | **+0.20** | ↑ |
| BRLMPrinterKit | N/A | N/A | — | (empty binding) |
| **Tier-1 Average** | **3.62** | **3.62** | **0** | **—** |

### Tier-2 Baselines (New in v4)

| Library | v4 Avg | Tier |
|---------|:------:|:----:|
| AnimatedCollectionViewLayout | 3.65 | 2 |
| KeychainSwift | 3.60 | 2 |
| DifferenceKit | 3.55 | 2 |
| AMPopTip | 3.50 | 2 |
| SVGView | 3.50 | 2 |
| Valet | 3.50 | 2 |
| SwiftyGif | 3.45 | 2 |
| DeviceKit | 3.40 | 2 |
| SwipeCellKit | 3.25 | 2 |
| PhoneNumberKit | 3.20 | 2 |
| Reachability | 3.20 | 2 |
| NVActivityIndicatorView | 3.20 | 2 |
| SwiftyBeaver | 3.10 | 2 |
| BonMot | 3.05 | 2 |
| FSPagerView | 3.05 | 2 |
| Quick | 3.05 | 2 |
| XMLCoder | 3.00 | 2 |
| Parchment | 2.95 | 2 |
| Swinject | 2.90 | 2 |
| TinyConstraints | 2.85 | 2 |
| ObjectMapper | 2.75 | 2 |
| **Tier-2 Average** | **3.22** | |

### Biggest Movers (v3 → v4)

**RxSwift (+0.20)**: The most improved tier-1 library. EP1's `NativeIntOverloadEmitter` added `int` overloads for `Skip(nint)` → `Skip(int)`, `Take(int)`, improving TypeFidelity and ergonomics. Protocol extension closure bridge (S10B) operators continue to work well. Naming improved with better `Get` prefix refinement. Collections improved with proper `IReadOnlyList<T>` projection. Still bottom-ranked due to fundamental async gap (1.0) and limited completeness without `subscribe`/`flatMap`.

**Stripe (+0.10)**: Session 8's naming polish and `CrossModuleExtensionEmitter` improved cross-module type unification. Naming up to 4.0 with consistent PascalCase, no mangled hashes, and good parameter names across all 14 modules. Cross-module extensions eliminate duplication.

**Starscream (+0.10)**: Protocols improved through better interface emission. Overall usability improved. Protocol extension methods and conformance validation are cleaner.

**Alamofire (+0.10)**: EP3's `MethodClosureBridge` recovered `ResponseData`, `ResponseString`, and `ResponseJSON` callback methods. EP2's `DataProjection` unblocked `serializingData()` async. Async and Completeness both improved. The fundamental `Session.request(url:)` workflow remains blocked by existential params, but more of the response-handling chain is reachable.

**Nuke (-0.25)**: Recalibration regression. v4 reviewers penalized naming issues (residual `_startPrefetching`, `Create_529DA596` hash suffix, `ConfigurationValue` property rename) and noise (`ExistentialContainer0` in some APIs) more heavily than v3. The actual code is unchanged or marginally better — the score reflects stricter evaluation.

**BlinkIDUX (-0.31)**: Largest regression. v4 reviewer identified `SwiftArray` lifetime issues in async stream patterns, `AnyType` in collection delegates, and `nint` parameters without `int` overloads. The binding's smaller surface area (9,319 lines) means individual issues have outsized impact on scores.

**KeychainAccess (-0.15)**: Naming improved to 4.0, Protocols improved to 3.0, but TypeFidelity and Overall were scored more strictly. The loss of 0.15 reflects recalibration rather than regression.

## Scorecard Matrix — Tier 1 (v4)

| Library | Naming | TypeFid | Null | Collect | Async | Error | Protocol | Noise | Complete | Overall | **Avg** |
|---------|:------:|:------:|:----:|:------:|:-----:|:-----:|:--------:|:-----:|:--------:|:-------:|:-------:|
| SmartCardIO | 5 | 4 | 5 | 5 | N/A | 5 | 4 | 4 | 4.5 | 4.5 | **4.56** |
| MicroblinkPlatform | 4 | 5 | 5 | 4.5 | N/A | 3 | 5 | 4 | 5 | 4.5 | **4.44** |
| Mappedin | 4 | 4 | 5 | 5 | 5 | 4 | 4 | 3 | 5 | 4 | **4.33** |
| Lottie | 4 | 3.5 | 4.5 | 4.5 | 5 | 4 | 3.5 | 4 | 4.5 | 4 | **4.15** |
| BlinkID | 4 | 3.5 | 4 | 4 | 4.5 | 3.5 | 3.5 | 3 | 4 | 3.5 | **3.70** |
| Stripe | 4 | 3.5 | 4 | 4 | 4 | 3 | 3.5 | 3 | 3.5 | 4 | **3.65** |
| Nuke | 3 | 3.5 | 4 | 3 | 5 | 3 | 4 | 3 | 3.5 | 3.5 | **3.55** |
| Starscream | 3.5 | 3.5 | 4 | 4 | 4 | 3 | 3.5 | 3 | 3.5 | 3.5 | **3.55** |
| KeychainAccess | 4 | 3 | 4.5 | 4 | 3 | 4 | 3 | 3.5 | 3.5 | 2.5 | **3.50** |
| CryptoSwift | 3.5 | 3 | 4 | 4 | N/A | 4 | 2.5 | 3 | 3.5 | 3.5 | **3.44** |
| Alamofire | 3.5 | 3.5 | 4 | 4 | 3 | 3.5 | 3.5 | 3 | 3 | 3 | **3.40** |
| Kingfisher | 3.5 | 3 | 4 | 4 | 4 | 3 | 3.5 | 2.5 | 3.5 | 3 | **3.40** |
| SnapKit | 3.5 | 3.5 | 4 | 5 | 2 | 3 | 3 | 3.5 | 3.5 | 2.5 | **3.40** |
| BlinkIDUX | 3.5 | 3.5 | 4 | 3 | 4 | 3.5 | 3 | 3 | 3 | 3 | **3.39** |
| Mixpanel | 3.5 | 3.5 | 4 | 3.5 | 4.5 | 2.5 | 3.5 | 3 | 3 | 2.5 | **3.30** |
| SkeletonView | 3.5 | 3.5 | 4 | 2 | 4 | 2.5 | 3.5 | 3 | 3.5 | 3 | **3.25** |
| GRDB | 3.5 | 3 | 4 | 3.5 | 2 | 4 | 3 | 3 | 3 | 3 | **3.20** |
| RxSwift | 3.5 | 3 | 3.5 | 3 | 1 | 3.5 | 3 | 3 | 3 | 2.5 | **2.95** |
| **Column Avg** | **3.67** | **3.50** | **4.22** | **3.92** | **3.67** | **3.44** | **3.44** | **3.14** | **3.64** | **3.31** | **3.62** |

## Scorecard Matrix — Tier 2 (v4 Baseline)

| Library | Naming | TypeFid | Null | Collect | Async | Error | Protocol | Noise | Complete | Overall | **Avg** |
|---------|:------:|:------:|:----:|:------:|:-----:|:-----:|:--------:|:-----:|:--------:|:-------:|:-------:|
| AnimatedCollectionViewLayout | 4 | 3.5 | 4 | 4 | N/A | 3 | 3.5 | 3 | 4 | 3.5 | **3.65** |
| KeychainSwift | 4 | 3.5 | 4 | 4 | N/A | 3 | 3.5 | 3.5 | 3.5 | 3.5 | **3.60** |
| DifferenceKit | 3.5 | 3.5 | 4 | 4 | N/A | 3 | 3.5 | 3.5 | 4 | 3.5 | **3.55** |
| AMPopTip | 4 | 3.5 | 4 | 3.5 | N/A | 3 | 3.5 | 3 | 3.5 | 3.5 | **3.50** |
| SVGView | 3.5 | 3.5 | 4 | 4 | N/A | 3 | 3.5 | 3 | 3.5 | 3.5 | **3.50** |
| Valet | 3.5 | 3.5 | 4 | 4 | N/A | 3 | 3.5 | 3 | 3.5 | 3.5 | **3.50** |
| SwiftyGif | 3.5 | 3.5 | 4 | 3.5 | N/A | 3 | 3 | 3.5 | 3.5 | 3.5 | **3.45** |
| DeviceKit | 3.5 | 3.5 | 4 | 3.5 | N/A | 3 | 3 | 3 | 3.5 | 3.5 | **3.40** |
| SwipeCellKit | 3.5 | 3 | 4 | 3.5 | N/A | 3 | 3 | 3 | 3.5 | 3 | **3.25** |
| PhoneNumberKit | 3.5 | 3 | 4 | 3.5 | N/A | 3 | 3 | 3 | 3 | 3 | **3.20** |
| Reachability | 3 | 3 | 4 | 3.5 | N/A | 3 | 3 | 3 | 3.5 | 3 | **3.20** |
| NVActivityIndicatorView | 3 | 3 | 4 | 3.5 | N/A | 3 | 3 | 3 | 3.5 | 3 | **3.20** |
| SwiftyBeaver | 3 | 3 | 4 | 3 | N/A | 3 | 3 | 3 | 3 | 3 | **3.10** |
| BonMot | 3 | 3 | 4 | 3 | N/A | 2.5 | 3 | 3 | 3 | 3 | **3.05** |
| FSPagerView | 3 | 3 | 4 | 3 | N/A | 2.5 | 3 | 3 | 3 | 3 | **3.05** |
| Quick | 3 | 3 | 3.5 | 3 | N/A | 3 | 3 | 3 | 3 | 3 | **3.05** |
| XMLCoder | 3 | 3 | 3.5 | 3 | N/A | 3 | 3 | 3 | 2.5 | 3 | **3.00** |
| Parchment | 3 | 3 | 3.5 | 3 | N/A | 2.5 | 3 | 2.5 | 3 | 3 | **2.95** |
| Swinject | 3 | 2.5 | 3.5 | 3 | N/A | 2.5 | 3 | 3 | 2.5 | 3 | **2.90** |
| TinyConstraints | 3 | 2.5 | 3.5 | 3 | N/A | 2.5 | 2.5 | 3 | 2.5 | 3 | **2.85** |
| ObjectMapper | 3 | 2.5 | 3.5 | 2.5 | N/A | 2.5 | 2.5 | 2.5 | 3 | 2.5 | **2.75** |
| **Column Avg** | **3.24** | **3.10** | **3.83** | **3.33** | **N/A** | **2.83** | **3.07** | **3.00** | **3.21** | **3.12** | **3.22** |

## Cross-Library Patterns

### What Works Well (4+ consistently)

**Nullability (4.22 tier-1 / 3.83 tier-2)** remains the strongest category across all libraries. `#nullable enable` on every file, `T?` for Swift optionals, `[MaybeNullWhen(false)]` on TryGet patterns. Zero `SwiftOptional<T>` leakage in public APIs. Even the weakest tier-2 libraries (Quick, XMLCoder, Parchment) score 3.5 — the nullable projection is essentially automatic and correct.

**Async/Await (3.67 avg where applicable)** is technically impressive where present. `Task<T>` with `CancellationToken`, `IAsyncEnumerable<T>` for `AsyncStream`, cooperative cancellation. Nuke's `IAsyncEnumerable<Progress>` and Lottie's async patterns remain best-in-class. Most tier-2 libraries are sync-only (N/A), which inflates the tier-1 average.

**Collection projection (3.92 tier-1 / 3.33 tier-2)** handles standard patterns well. `IReadOnlyList<T>`, `IReadOnlyDictionary<K,V>`, `IReadOnlySet<T>`, and `byte[]` for `Foundation.Data` (EP2). The gap between tiers reflects that tier-2 libraries tend to have simpler collection usage.

**Discriminated union TryGet pattern** continues to be a highlight. `CaseTag` enum + `Tag` property + `TryGetXxx([MaybeNullWhen(false)] out T value)` with EP2's enum case construction/inspection improvements.

### Common Pain Points (scoring 1-3 consistently)

**`nint` pollution** remains the top ergonomic complaint across both tiers. Properties like `Count`, `HashValue`, `FetchCount`, `Index` return `nint` where C# developers expect `int`. EP1's `NativeIntOverloadEmitter` added `int` overloads for method parameters but not for properties or return types. Every library with `nint` properties loses 0.5-1.0 on TypeFidelity.

**`Swift.AnyType` as return type** (149 occurrences in GRDB alone, ~22 in Lottie, prevalent in RxSwift/Kingfisher query builders) renders method chains useless. Protocol-extension methods returning `Self` where the concrete type isn't resolvable fall back to `AnyType`, creating dead ends in IntelliSense. This is the single most impactful pattern blocking higher scores.

**Noise/leakage ratio** (3.14 tier-1 / 3.00 tier-2) is the weakest non-async category. `ExistentialContainer1` in public callback signatures, `SwiftResult<SwiftVoid, SwiftError>` in closure parameters, `_`-prefixed internal types exposed publicly, and the sheer volume of P/Invoke boilerplate co-located with public APIs make generated files hard to navigate. `[EditorBrowsable(Never)]` mitigates IDE impact but not code review/source reading.

**Overall usability** (3.31 tier-1 / 3.12 tier-2) — the holistic "would a C# developer be comfortable" score — is held back by the cumulative effect of `nint` casts, `AnyType` dead ends, `Get` prefix friction, and the gap between what compiles and what a developer would write idiomatically.

### Mixed Results

**`int` overloads (EP1)**: Successfully added for method parameters (`Skip(int)`, `Take(int)`, `Limit(int, nint?)`, `DatabaseQuestionMarks(int)`) but NOT for property return types (`nint Count`, `nint HashValue`). The improvement is real but incomplete — reviewers noted the discrepancy.

**`byte[]` for `Foundation.Data` (EP2)**: Full `DataProjection` pipeline working for parameters and return values. Visible in Alamofire's `serializingData()` async unblock. However, `Foundation.NSData` still appears in some P/Invoke signatures and the two layers (C# `byte[]` vs interop `NSData`) create confusion about which to use.

**Protocol extension methods (S5-S7, S10B)**: The three-emitter architecture (`ProtocolExtensionEmitter`, `ForeignTypeExtensionEmitter`, `CrossModuleExtensionEmitter`) works well for simple cases. Kingfisher builder chains, SnapKit DSL, SkeletonView entry points, and RxSwift operators all compile. But complex return types (existential, constrained generic) still fall through to `AnyType`.

**`Get` prefix refinement (EP1)**: The heuristic correctly avoids `Get` on parameterized methods while adding it to 0-param noun properties. But the boundary is fuzzy: `GetSnp()`, `GetEqualTo()` read poorly alongside Swift documentation that says `snp`, `equalTo`. No clear fix without semantic understanding of API intent.

## Per-Library Deep Dives — Tier 1

### Lottie (4.10 → 4.15, +0.05) — Best in Class

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 4 | 4 | — |
| TypeFidelity | 3 | 3.5 | +0.5 |
| Nullability | 4.5 | 4.5 | — |
| Collections | 4.5 | 4.5 | — |
| Async | 5 | 5 | — |
| ErrorHandling | 4 | 4 | — |
| Protocols | 3.5 | 3.5 | — |
| Noise | 4 | 4 | — |
| Completeness | 4.5 | 4.5 | — |
| Overall | 4 | 4 | — |

**Highlights**: TypeFidelity improved from 3 to 3.5 thanks to EP2's `byte[]` projection for `Foundation.Data` parameters. The critical workflow `LottieAnimationView(name:).Play { finished in }` continues to compile and run. 1,013 XML doc tags.

**Remaining issues**: `AnyType` in ~22 `IInterpolatable`/`ISpatialInterpolatable` locations. `IAnimationFontProvider` still SB0004 (empty). `ExistentialContainer0` in `AnyValueProviderStorage.SingleValue`.

---

### Nuke (3.80 → 3.55, -0.25) — Recalibration Regression

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3 | -0.5 |
| TypeFidelity | 4 | 3.5 | -0.5 |
| Nullability | 4 | 4 | — |
| Collections | 3 | 3 | — |
| Async | 5 | 5 | — |
| ErrorHandling | 3.5 | 3 | -0.5 |
| Protocols | 4 | 4 | — |
| Noise | 3.5 | 3 | -0.5 |
| Completeness | 4 | 3.5 | -0.5 |
| Overall | 3.5 | 3.5 | — |

**Note**: The code is unchanged or marginally better. v4 reviewer applied stricter standards to naming (`_startPrefetching`, `Create_529DA596`, `ConfigurationValue` rename) and noise (`ExistentialContainer0` in some returns). The `IAsyncEnumerable<Progress>` pattern remains exemplary (Async 5/5).

**Key example (good)**: `public IAsyncEnumerable<Swift.Nuke.ImageTask.Progress> ProgressValue`
**Key example (bad)**: `public static unsafe ImageRequest Create_529DA596(Swift.URLRequest urlRequest)`

---

### Alamofire (3.30 → 3.40, +0.10) — Closure Bridge Impact

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 3.5 | 3.5 | — |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 2.5 | 3 | +0.5 |
| ErrorHandling | 3.5 | 3.5 | — |
| Protocols | 3.5 | 3.5 | — |
| Noise | 3.5 | 3 | -0.5 |
| Completeness | 2.5 | 3 | +0.5 |
| Overall | 2.5 | 3 | +0.5 |

**Highlights**: EP3's `MethodClosureBridge` recovered `ResponseData`, `ResponseString`, `ResponseJSON` callback methods — previously blocked by bound-generic closure args. EP2's `DataProjection` unblocked `serializingData()` async. Overall up from 2.5 to 3.0 — the response-handling chain is now partially reachable.

**Remaining gap**: `Session.request(url:, method:, parameters:)` with `URLConvertible` existential param is still missing — the entry point to the library. Noise slightly down due to increased infrastructure for closure bridge.

**Key example (good)**: `public DataRequest ResponseData(Action<AFDataResponse<byte[]>> completionHandler)`

---

### Kingfisher (3.35 → 3.40, +0.05) — Steady Improvement

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 2.5 | 3 | +0.5 |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 4 | 4 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 3 | 3.5 | +0.5 |
| Noise | 3 | 2.5 | -0.5 |
| Completeness | 3.5 | 3.5 | — |
| Overall | 3 | 3 | — |

**Highlights**: TypeFidelity up from `byte[]` projection and better type resolution. Protocols up with improved conformance emission and proxy dispatch. Noise down slightly due to stricter evaluation of `AnyType` in builder chain returns.

---

### SnapKit (3.40 → 3.40, unchanged)

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 4 | 3.5 | -0.5 |
| Nullability | 4 | 4 | — |
| Collections | 5 | 5 | — |
| Async | 2 | 2 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 3 | 3 | — |
| Noise | 3.5 | 3.5 | — |
| Completeness | 3.5 | 3.5 | — |
| Overall | 2.5 | 2.5 | — |

**Status**: The core DSL path `view.GetSnp().MakeConstraints { }` continues to work. TypeFidelity down slightly from stricter `nint` evaluation. Collections remain 5/5 — no collection types in the API.

---

### CryptoSwift (3.44 → 3.44, unchanged)

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 3 | 3 | — |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | N/A | N/A | — |
| ErrorHandling | 4 | 4 | — |
| Protocols | 2.5 | 2.5 | — |
| Noise | 3 | 3 | — |
| Completeness | 3.5 | 3.5 | — |
| Overall | 3.5 | 3.5 | — |

**Status**: The signature `new AES(key, new CBC(iv))` pattern continues to work. `ArraySlice<UInt8>` → `AnyType` (14 occurrences) is the main remaining gap.

---

### GRDB (3.20 → 3.20, unchanged) — Largest Binding, Structural Limits

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 3 | 3 | — |
| Nullability | 4 | 4 | — |
| Collections | 3.5 | 3.5 | — |
| Async | 2 | 2 | — |
| ErrorHandling | 4 | 4 | — |
| Protocols | 3 | 3 | — |
| Noise | 3.5 | 3 | -0.5 |
| Completeness | 2.5 | 3 | +0.5 |
| Overall | 3 | 3 | — |

**Highlights**: 92,915 lines — the largest binding. Completeness improved (existential recovery, protocol extension closures, `IReadOnlySet` from `FetchSet()`). Noise slightly down from stricter evaluation of `ExistentialContainer1` leakage in `AsyncRead` callback, `SwiftResult<SwiftVoid, SwiftError>` in migration API.

**Critical gaps**: `Read<T>` / `Write<T>` constrained to `where T : ISwiftObject` (can't return `int`/`string`). Core async `read`/`write` not surfaced as `Task<T>`. `ResultCode` is a 3,749-line class rather than an enum (raw values unavailable from ABI JSON).

**Key example (good)**: `public IReadOnlySet<TRowDecoder> FetchSet(Database db)` — correct set projection.
**Key example (bad)**: `AsyncRead(Action<SwiftResult<Database, ExistentialContainer1>> value)` — infrastructure leak.

---

### KeychainAccess (3.65 → 3.50, -0.15)

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 4 | +0.5 |
| TypeFidelity | 4 | 3 | -1 |
| Nullability | 5 | 4.5 | -0.5 |
| Collections | 4 | 4 | — |
| Async | 3.5 | 3 | -0.5 |
| ErrorHandling | 4 | 4 | — |
| Protocols | 2 | 3 | +1 |
| Noise | 3.5 | 3.5 | — |
| Completeness | 3.5 | 3.5 | — |
| Overall | 3.5 | 2.5 | -1 |

**Note**: Mixed movement. Naming improved (SCREAMING_CASE fix, better parameter names). Protocols improved (conformance emission). TypeFidelity down from stricter `nint` evaluation. Overall down from stricter "would a C# dev be comfortable" assessment.

---

### RxSwift (2.75 → 2.95, +0.20) — Most Improved

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3 | 3.5 | +0.5 |
| TypeFidelity | 2.5 | 3 | +0.5 |
| Nullability | 3.5 | 3.5 | — |
| Collections | 2.5 | 3 | +0.5 |
| Async | 1 | 1 | — |
| ErrorHandling | 3.5 | 3.5 | — |
| Protocols | 3 | 3 | — |
| Noise | 3 | 3 | — |
| Completeness | 3 | 3 | — |
| Overall | 2.5 | 2.5 | — |

**Highlights**: EP1's `int` overloads improved TypeFidelity (`Skip(int)`, `Take(int)`). Naming improved with better `Get` prefix refinement. Collections up with `IReadOnlyList<T>` projection. Still bottom-ranked: Async stays at 1 (RxSwift has no async/await by design), and the reactive chain fundamentally requires `subscribe` (existential return) and `flatMap` (constrained generics) to be useful.

---

### Starscream (3.45 → 3.55, +0.10)

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 3.5 | 3.5 | — |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 4 | 4 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 3 | 3.5 | +0.5 |
| Noise | 3 | 3 | — |
| Completeness | 3.5 | 3.5 | — |
| Overall | 3 | 3.5 | +0.5 |

**Highlights**: Protocol interface emission improved with better proxy dispatch. Overall usability up — the `WebSocket(url:)` + delegate pattern is closer to functional.

---

### SkeletonView (3.25 → 3.25, unchanged)

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 3.5 | 3.5 | — |
| Nullability | 4 | 4 | — |
| Collections | 2 | 2 | — |
| Async | 4 | 4 | — |
| ErrorHandling | 2.5 | 2.5 | — |
| Protocols | 3.5 | 3.5 | — |
| Noise | 3 | 3 | — |
| Completeness | 3.5 | 3.5 | — |
| Overall | 3 | 3 | — |

**Status**: `view.ShowSkeleton(color)` and `view.HideSkeleton()` continue to work via foreign type extensions. Collections remain at 2 (`SwiftSet<AnyType>` for existential gradient directions).

---

### Mixpanel (3.25 → 3.30, +0.05)

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 3.5 | — |
| TypeFidelity | 3.5 | 3.5 | — |
| Nullability | 4 | 4 | — |
| Collections | 3 | 3.5 | +0.5 |
| Async | 4.5 | 4.5 | — |
| ErrorHandling | 2 | 2.5 | +0.5 |
| Protocols | 3.5 | 3.5 | — |
| Noise | 3.5 | 3 | -0.5 |
| Completeness | 2.5 | 3 | +0.5 |
| Overall | 2.5 | 2.5 | — |

**Highlights**: Existential dict projection (S5) improved Collections and Completeness — `Track()` with basic properties is closer. Error handling slightly up. Noise down from stricter evaluation.

---

### BlinkID (3.70 → 3.70, unchanged)

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 4 | 4 | — |
| TypeFidelity | 3.5 | 3.5 | — |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 4.5 | 4.5 | — |
| ErrorHandling | 3.5 | 3.5 | — |
| Protocols | 3 | 3.5 | +0.5 |
| Noise | 3 | 3 | — |
| Completeness | 4 | 4 | — |
| Overall | 3.5 | 3.5 | — |

---

### Stripe (3.55 → 3.65, +0.10) — 14 Modules, Cross-Module Success

| Category | v3 | v4 | Δ |
|----------|:--:|:--:|:---:|
| Naming | 3.5 | 4 | +0.5 |
| TypeFidelity | 3.5 | 3.5 | — |
| Nullability | 4 | 4 | — |
| Collections | 4 | 4 | — |
| Async | 4 | 4 | — |
| ErrorHandling | 3 | 3 | — |
| Protocols | 3.5 | 3.5 | — |
| Noise | 3 | 3 | — |
| Completeness | 3.5 | 3.5 | — |
| Overall | 3.5 | 4 | +0.5 |

**Highlights**: Session 8's `CrossModuleExtensionEmitter` eliminates cross-module duplication. Naming up to 4.0 — consistent PascalCase across all 14 modules with no mangled hashes. Overall up to 4.0 reflecting ecosystem-level quality when viewed as a complete payment SDK.

---

### Manual Libraries (SmartCardIO 4.56, MicroblinkPlatform 4.44, Mappedin 4.33, BlinkIDUX 3.39)

SmartCardIO, MicroblinkPlatform, and Mappedin remain at their v3 levels — small, well-structured libraries that produce near-ideal bindings. BlinkIDUX dropped 0.31 due to stricter evaluation of `SwiftArray` lifetime issues in async streams and `nint` parameter pollution.

## Per-Library Deep Dives — Tier 2 (Selected)

### AnimatedCollectionViewLayout (3.65) — Top Tier-2

Simple UIKit subclass library. Clean PascalCase naming, correct type projections, minimal noise. The small API surface (2,861 lines) means few opportunities for complex patterns to fail. Good baseline for "what a small, well-designed Swift library looks like in C#."

### DifferenceKit (3.55) — Strong Generic Support

Protocol-heavy diffing library. The `Differentiable` protocol with associated types compiles correctly. `IChangeset<T>` generic interface works. `StagedChangeset<T>` generic collection type projects as `IReadOnlyList<T>`. Minor gaps in `nint` for count/index properties.

### KeychainSwift (3.60) — Clean Keychain Wrapper

Minimal API surface projects cleanly. `Get(string key)` → `string?`, `Set(string, string, KeychainSwiftAccessOptions?)` work correctly. `KeychainSwiftAccessOptions` enum projects as expected. SCREAMING_CASE fix applied correctly to accessibility constants.

### ObjectMapper (2.75) — Bottom Tier-2, Protocol Limits

Heavy reliance on `Mappable` protocol with generic `Map` type makes this the worst-scored tier-2 library. `BaseMappable` protocol with `mapping(map:)` requires proxy dispatch that doesn't work for closure-based mapping. `TransformType` protocol with associated types blocks most transform implementations. Representative of "protocol-first" Swift libraries that challenge the binding generator.

### Swinject (2.90) — DI Container Generics

Dependency injection library where the core API is entirely generic: `Container.Register<Service>(factory:)`, `Container.Resolve<Service>()`. The `where T : ISwiftObject` constraint on generic closures blocks the primary use case (registering arbitrary types). TypeFidelity at 2.5 — `nint`-typed `hashValue`, `AnyType` returns on resolution methods.

### TinyConstraints (2.85) — Extension Method Gaps

Auto Layout DSL library where most APIs are UIView extensions. `ForeignTypeExtensionEmitter` covers basic constraints, but many extension methods with complex return types fall through to `AnyType`. The `ConstraintAttribute` enum projects correctly, but builder-pattern chains break at `AnyType` boundaries.

## Ergonomic Polish Impact Analysis

### EP1: `nint` → `int` Overloads (NativeIntOverloadEmitter)
- **Scope**: Method parameters only (not properties, not return types)
- **Impact**: +0.5 TypeFidelity on RxSwift, minor lift on GRDB, SnapKit
- **Remaining gap**: `nint Count`, `nint HashValue`, `nint FetchCount()` properties/returns untouched
- **Verdict**: Correct direction, incomplete coverage. Property return type overloads would have ~2x the impact.

### EP2: `Foundation.Data` → `byte[]` (DataProjection)
- **Scope**: Parameters, return values, enum associated values, container elements
- **Impact**: +0.5 TypeFidelity on Lottie, enabled `serializingData()` on Alamofire
- **Remaining gap**: `NSData` still visible in P/Invoke layer (required for interop)
- **Verdict**: High-value change. `byte[]` is universally understood in C#.

### EP3: Closure Bridge Generalization (MethodClosureBridge)
- **Scope**: Single-closure methods with bound-generic closure args
- **Impact**: Recovered Alamofire `ResponseData`/`ResponseString`/`ResponseJSON`, Stripe `PossibleBrands`
- **Remaining gap**: Multi-closure methods, constrained generic closures
- **Verdict**: Substantial workflow unblock. Each recovered method represents a real user scenario.

### Enum Case Construction/Inspection (EP2)
- **Scope**: `TryGet` pattern improvements, type-derived parameter names
- **Impact**: Better naming on associated value parameters across all libraries with enums
- **Verdict**: Polish-level improvement, cumulative effect across many libraries.

### SCREAMING_CASE Fix
- **Scope**: Type names like `RESULT_CODE` → `ResultCode`
- **Impact**: Naming improvement where applicable (GRDB `ResultCode`, various constants)
- **Verdict**: Essential fix. SCREAMING_CASE in C# types is immediately jarring.

### Existential Tuples in Closures
- **Scope**: Closure parameters with existential tuple elements
- **Impact**: Enables callbacks with complex parameter types
- **Verdict**: Gate removal — prevents silent skipping of methods.

## Critical Workflow Status

### Works End-to-End
- **Lottie**: `LottieAnimationView(name:).Play { }` ✅
- **CryptoSwift**: `new AES(key, new CBC(iv)).Encrypt(data)` ✅
- **SnapKit**: `view.GetSnp().MakeConstraints { make in ... }` ✅
- **SkeletonView**: `view.ShowSkeleton(color)` / `view.HideSkeleton()` ✅
- **GRDB**: `pool.Read(db => { db.Execute(...) })` ✅ (sync only)
- **Nuke**: `ImageRequest(url:)` constructor ✅

### Partially Reachable
- **Alamofire**: Response handlers compile (`ResponseData`, `ResponseString`), but entry point `Session.request(url:)` blocked by existential
- **Stripe**: Payment types and configuration accessible, but inter-module flow requires `--framework-dependency`
- **Kingfisher**: Builder chain `SetProcessor().SetCache()` works, but full image loading workflow needs cache type resolution
- **RxSwift**: `Filter`/`Map`/`Skip`/`Take` operators work, but `subscribe` (existential return) and `flatMap` (constrained generics) missing
- **BlinkID**: Scanner types accessible, but document scanning workflow needs callback dispatch

### Blocked
- **Mixpanel**: `Track(event:, properties:)` with `[String: any MixpanelType]` dictionary — dict existential projection incomplete
- **Starscream**: WebSocket event delivery requires existential marshalling in unmanaged callbacks

## Generator Bugs Observed (New in v4)

1. **`ExistentialContainer1` in public callback signatures**: `AsyncRead(Action<SwiftResult<Database, ExistentialContainer1>>)` in GRDB exposes internal marshalling infrastructure. Should be projected to a domain-specific error type or at minimum wrapped.

2. **`SwiftResult<SwiftVoid, SwiftError>` in public `Func<>` parameters**: Migration and setup callbacks (`RegisterMigration`, `PrepareDatabase`) require callers to construct `SwiftResult` values. Idiomatic C# would be `Action<Database>` with generator-handled error wrapping.

3. **`_`-prefixed internal types public**: `_SQLAssociation`, `_LayoutedRowAdapter`, `_RowLayout` in GRDB are Swift-internal types that shouldn't be in the public API surface. Access-level filtering should exclude these.

4. **Property return type `nint` without `int` overload**: EP1's `NativeIntOverloadEmitter` only covers method parameters. Properties like `Count`, `HashValue`, `FetchCount()` returning `nint` affect TypeFidelity across all libraries.

5. **`AnyType` on protocol-builder returns**: Methods returning `Self` on protocol types fall back to `AnyType` when the concrete type can't be resolved. This creates dead-end method chains in IntelliSense. Returning the protocol interface type would be more useful.

## Prioritized Action Items

### High Priority (would move average +0.15-0.25)

1. **`nint` property/return overloads**: Extend `NativeIntOverloadEmitter` to generate `int`-returning convenience properties alongside `nint` originals. Would lift TypeFidelity by 0.5 across 80%+ of libraries.

2. **`AnyType` → protocol interface for Self-returns**: When a protocol method returns `Self`, project the return type as the protocol interface type (e.g., `IFilteredRequest`) rather than `AnyType`. Enables fluent chaining through IntelliSense.

3. **Hide `_`-prefixed types**: Filter Swift-internal types (leading underscore) from public API emission. These are implementation details that inflate noise scores and confuse developers.

### Medium Priority (would move average +0.05-0.10)

4. **`Action<T>` projection for `Func<T, SwiftResult<SwiftVoid, SwiftError>>`**: Common callback pattern (migrations, setup) should project to simpler `Action<T>` with generator-wrapped error handling.

5. **`ExistentialContainer` hiding**: Wrap or type-erase `ExistentialContainer{N}` in public callback signatures. At minimum, provide a domain-specific wrapper type.

6. **Enum raw values from swiftinterface**: String enum raw values are unavailable from ABI JSON. Parsing swiftinterface for raw value declarations would fix `ResultCode` (3,749 lines as class → compact enum).

### Low Priority (polish, diminishing returns)

7. **`Get` prefix heuristic refinement**: Consider not adding `Get` prefix when the method name matches a known Swift property accessor pattern (e.g., `snp`, `rx`).

8. **`SwiftVoid` → `void` projection in closure signatures**: Replace `SwiftVoid` with standard C# void in public-facing closure types.

9. **Async false-positive suppression**: `MakeConstraintsAsync` on SnapKit (synchronous closure) is a false positive. Heuristic could check for `@escaping` attribute to distinguish real async candidates.

## Comparison to ObjC Binding Experience

The Swift binding generator in v4 produces bindings that are **structurally comparable to Xamarin/MAUI ObjC bindings** for simple libraries (UIKit subclasses, delegate protocols, property bags). For these patterns, the quality is equivalent or better — nullability annotations are more comprehensive, collection types are more specific (`IReadOnlyList<T>` vs `NSArray`), and enum discriminated unions are more type-safe.

Where Swift bindings fall behind ObjC bindings:
- **Protocol-heavy APIs**: ObjC protocols map cleanly to C# interfaces via the ObjC runtime. Swift protocols with associated types, Self requirements, and existential containers have no direct ObjC equivalent, and the binding generator's proxy pattern introduces friction (SB0003 stubs, `AnyType` returns).
- **Async/callback patterns**: ObjC completion handlers map directly to C# `Action<T>` delegates. Swift's typed `throws` in closures produces `SwiftResult<SwiftVoid, SwiftError>` in C# — a significant ergonomic regression from the ObjC experience.
- **Generic types**: ObjC generics (lightweight) are simple type hints. Swift's full generics with constraints, associated types, and conditional conformances challenge the binding generator's type resolution, producing `AnyType` fallbacks and `where T : ISwiftObject` constraints.

Where Swift bindings are **better** than ObjC bindings:
- **Value types**: Swift structs bind as C# structs with proper copy semantics. ObjC has no value types beyond primitives.
- **Enums with associated values**: The `CaseTag` + `TryGet` pattern is more type-safe than ObjC's `NS_ENUM` → `nint` mapping.
- **Async/await (when present)**: `Task<T>` with `CancellationToken` is superior to ObjC completion handler patterns.
- **Collection generics**: `IReadOnlyList<ConcreteType>` vs ObjC's `NSArray` (untyped).

## Summary Statistics

| Metric | Value |
|--------|-------|
| Total libraries scored | 39 (18 tier-1 + 21 tier-2) |
| Tier-1 average | 3.62 (unchanged from v3) |
| Tier-2 average | 3.22 (new baseline) |
| Combined average | 3.40 |
| Compile gate | 53/53 (100%) |
| Top scorer | SmartCardIO (4.56) |
| Bottom scorer | ObjectMapper (2.75) |
| Strongest category | Nullability (4.22 tier-1 / 3.83 tier-2) |
| Weakest category | Noise (3.14 tier-1 / 3.00 tier-2) |
| Most improved (v3→v4) | RxSwift (+0.20) |
| Largest regression (v3→v4) | BlinkIDUX (-0.31, recalibration) |
