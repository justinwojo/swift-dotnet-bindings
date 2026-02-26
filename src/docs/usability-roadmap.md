# Usability Roadmap

**Revised**: February 2026 (post-v3 binding review, reset to future work only)
**Goal**: Push average binding quality score from 3.62 toward 4.0+
**Scoring reference**: `binding-review-v3.md` — 18-library quality review, 10-category scorecards
**Completed work**: `Completed/usability-roadmap-sessions-1-10.md` — Sessions 1–10B (all ✅)

---

## Where We Are

### Current Scores (v3)

| Library | Score | Top Remaining Blocker |
|---------|:-----:|----------------------|
| SmartCardIO | 4.56 | Minor — `object _params` existential |
| MicroblinkPlatform | 4.44 | Minor — naming collisions |
| Mappedin | 4.30 | Minor — SCREAMING_CASE names |
| Lottie | 4.10 | AnyType in ~22 locations (IInterpolatable) |
| Nuke | 3.80 | `Create_529DA596` mangled name, naming polish |
| BlinkID | 3.70 | `DateResult<SwiftString>` in MRZ properties |
| BlinkIDUX | 3.70 | Empty `IUXThemeProtocol` (21 members skipped) |
| KeychainAccess | 3.65 | No protocol interfaces emitted |
| Stripe (14) | 3.55 | Complex enum closures in callbacks |
| Starscream | 3.45 | Runtime event delivery impossible (compile-only) |
| CryptoSwift | 3.44 | `ArraySlice<UInt8>` → AnyType (14 occurrences) |
| SnapKit | 3.40 | `GetEqualTo` naming, async false positives |
| Alamofire | 3.30 | Foundation.Data + generic closure callbacks |
| Mixpanel | 3.25 | `[String: any MixpanelType]` dict-existential |
| SkeletonView | 3.25 | Collections: `SwiftSet<AnyType>`, limited customization |
| GRDB | 3.20 | `ResultCode` as class (not enum), async APIs missing |
| RxSwift | 2.75 | `subscribe` (existential return), Map ISwiftObject constraint |

**Overall average**: 3.62 (range: 2.75 RxSwift — 4.56 SmartCardIO)

### Weakest Categories (v3 column averages)

| Category | v3 Avg | Gap to 4.0 | Key Lever |
|----------|:------:|:----------:|-----------|
| Protocol/Interface | 3.28 | 0.72 | Existential returns, constrained generics |
| Overall Usability | 3.28 | 0.72 | Alamofire/RxSwift critical workflows |
| Noise/Leakage | 3.28 | 0.72 | `Get` prefix, async false positives |
| Error Handling | 3.44 | 0.56 | Methods that would throw aren't emitted |
| Type Fidelity | 3.53 | 0.47 | `nint`, AnyType in generics |
| Naming | 3.61 | 0.39 | `Get` prefix, `_event` params |
| Completeness | 3.61 | 0.39 | Closure callbacks, existential params |

### Critical Workflows Still Blocked

| Library | Workflow | Blocker | Fixable In |
|---------|----------|---------|:----------:|
| Alamofire | `Session.request(url).responseData { }` | Generic closure in callback param | Session 3 |
| Alamofire | `Session.request(url).serializingData()` | `Foundation.Data` not `ISwiftObject` | Session 2 |
| Mixpanel | `Track(event:, properties:)` full form | `[String: any MixpanelType]` dict-existential | Session 5 |
| Starscream | Runtime event delivery via `IWebSocketDelegate` | Existential marshalling in callbacks | Session 6 |
| RxSwift | `observable.Subscribe { }` | `any Disposable` existential return | Session 4 |

---

## Session Plan

### Session 1: Ergonomic Polish

**Theme**: Small naming/noise fixes with broad impact across many libraries
**Effort**: 1 session | **Libraries improved**: 10+
**Priority**: Highest — best effort-to-impact ratio

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **1a. `nint` → `int` convenience overloads** | Swift `Int` maps to `nint` (correct for pointer-sized), but C# developers expect `int`. Emit an `int` overload that delegates to the `nint` overload for methods taking `Int` parameters. Affects `Skip(nint)`, `Take(nint)`, `Row[nint]`, etc. | Generator gap (ergonomics) |
| **1b. `Get` prefix refinement** | Only apply `Get` prefix for actual property getters (0 non-self parameters). Named methods with arguments should keep their original name: `GetEqualTo(view)` → `EqualTo(view)`, `GetSkip(nint)` → `Skip(nint)`. Refine the heuristic in `GetPublicMethodName`. | Generator bug (naming) |
| **1c. Async detection false positives** | Closure-only methods (`MakeConstraints(Action<...>)`, `ShowSkeleton(...)`) get spurious `Async` variants. Detect "all params are closures and method is synchronous" and suppress async generation. | Generator bug |
| **1d. `_event` parameter naming** | `Track(string? _event)` — the `_` prefix persists on some keyword-like params. Ensure `DeriveParameterNameFromType` runs before falling back to `_` prefix for remaining cases. Check `_event`, `_string`, `_data`. | Generator bug (naming) |

**Projected impact**: Naming 3.61→~3.80, Noise 3.28→~3.40, Overall +0.05-0.10 across board. Avg +0.10-0.15.

**Acceptance gate**: `Skip(int)` overload exists on RxSwift Observable. SnapKit `EqualTo(view)` (no `Get` prefix). No `MakeConstraintsAsync` in SnapKit output. Mixpanel `Track(string? event)` (no `_` prefix). 32/32 validation maintained.

---

### Session 2: Foundation.Data Projection

**Theme**: Add `Foundation.Data` as first-class runtime type
**Effort**: 1 session | **Libraries improved**: Alamofire, KeychainAccess, others with `Data` in APIs
**Priority**: High — unlocks Alamofire's async pathway and unblocks `Data` in bound generics everywhere

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **2a. `SwiftData` runtime type** | New C# type implementing `ISwiftObject`, wrapping `Foundation.Data`. Similar pattern to `SwiftString` and `SwiftArray<T>`. Implement `byte[]` / `ReadOnlySpan<byte>` / `NSData` marshalling. | Runtime |
| **2b. Generator projection** | Register `Foundation.Data` → `SwiftData` in type database. Project as `byte[]` in public APIs (like `SwiftString` → `string`). Handle `Optional<Data>` → `byte[]?`. | Generator gap |
| **2c. Bound generic unblock** | `DataTask<Data>` should pass `HasNonSwiftObjectGenericArg` now that `Data` implements `ISwiftObject`. Verify Alamofire `serializingData()` emits. | Generator gap |

**Projected impact**: Alamofire +0.20-0.30 (Completeness + Overall), KeychainAccess +0.10. Avg +0.03-0.05.

**Acceptance gate**: Alamofire `serializingData()` method appears in generated bindings and compiles. `Foundation.Data` parameters project as `byte[]`. 32/32 validation maintained.

**Note**: This does NOT fix Alamofire's callback-style `responseData {}` — that requires Session 3.

---

### Session 3: Closure Bridge Generalization

**Theme**: Extend closure bridging from protocol extensions to regular method P/Invoke
**Effort**: 1-2 sessions | **Libraries improved**: Alamofire, Stripe, various callback-heavy APIs
**Priority**: High — transforms Alamofire from "partially usable" to "fully usable"
**Depends on**: Session 2 (Foundation.Data needed for `responseData` return type)

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **3a. `@_cdecl` callback thunk generation** | Extend the `ProtocolExtensionClosureBridge` pattern to emit `@_cdecl` thunks for closure parameters in regular instance/static methods. Generate `[UnmanagedCallersOnly]` callback + function pointer + GCHandle context passing. | Design gap |
| **3b. Multi-closure and mixed-param support** | The protocol extension bridge only handles single-closure-only methods. Method callbacks often have additional non-closure params. Support mixed signatures: `responseData(queue:, completionHandler:)`. | Design gap |
| **3c. Alamofire validation** | `Session.request(url).responseData { response in ... }` compiles end-to-end. | Validation |

**Projected impact**: Alamofire +0.30-0.50 (combined with Session 2: 3.30 → 3.80+). Stripe +0.10-0.15. Avg +0.05-0.08.

**Acceptance gate**: Alamofire `responseData(completionHandler:)` appears in bindings and compiles. At least one Stripe callback method recovered. 32/32 validation maintained.

---

### Session 4: RxSwift Depth — `subscribe` + Value-Type Map

**Theme**: Unlock the two most important remaining RxSwift operations
**Effort**: 1 session | **Libraries improved**: RxSwift (deeply)
**Priority**: High — RxSwift is the bottom-scoring library at 2.75

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **4a. Existential return from protocol extensions** | `subscribe` returns `any Disposable`. The `ProtocolExtensionEmitter` currently gates on existential returns. Options: (1) return `IDisposable` by extracting the existential container and wrapping, (2) return `object` with documentation, (3) return a concrete `Disposable` wrapper that calls through. Option 1 is most idiomatic. | Design gap |
| **4b. Relax `ISwiftObject` constraint on `Map<TResult>`** | Currently `where TResult : class, ISwiftObject` — can't map to primitives, strings, or value types. The closure bridge result buffer infrastructure exists (Session 10B); needs a value-type marshalling path for the result. | Design gap |
| **4c. `flatMap` investigation** | `flatMap` uses `where Source: ObservableConvertibleType` — constrained generics in protocol extensions. Spike whether the `@_silgen_name` ABI can handle this. May need a monomorphized approach. | Investigation |

**Projected impact**: RxSwift 2.75 → 3.20+ (Protocols +1, Completeness +0.5, Overall +0.5). Avg +0.03-0.05.

**Acceptance gate**: `observable.Subscribe(onNext: { element in ... })` appears and compiles. `observable.Map(x => x.ToString())` compiles without `ISwiftObject` constraint on return type. 32/32 validation maintained.

---

### Session 5: Existential Dictionary/Collection Values

**Theme**: Marshal existential containers inside generic collections
**Effort**: 2+ sessions | **Libraries improved**: Mixpanel (deeply), various config APIs
**Priority**: Medium — high effort, primarily benefits Mixpanel

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **5a. Existential container layout analysis** | Map how existential containers are stored inside `SwiftDictionary<K,V>`. The layout varies by protocol witness table count. | Investigation |
| **5b. Dict-existential marshalling pipeline** | New marshalling path for `[String: any Protocol]` → `Dictionary<string, object>` or `Dictionary<string, IProtocol>`. May require per-element existential unwrapping. | Design gap |
| **5c. Mixpanel full API** | `track(event:, properties:)`, `set(properties:)`, `registerSuperProperties` with `[String: any MixpanelType]` parameters. | Validation |

**Projected impact**: Mixpanel 3.25 → 3.70+ (Completeness +1, Overall +0.5). Avg +0.03.

**Acceptance gate**: Mixpanel `Track(event:, properties:)` with `Dictionary<string, IMixpanelType>` parameter compiles. 32/32 validation maintained.

---

### Session 6: Existential Marshalling in Unmanaged Callbacks

**Theme**: Enable Swift-to-C# delegate dispatch through protocol proxies
**Effort**: 1-2 sessions | **Libraries improved**: Starscream, any library with delegate patterns
**Priority**: Medium — deep structural work, primarily benefits Starscream runtime

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **6a. Existential container unmarshalling in callbacks** | `[UnmanagedCallersOnly]` callback functions currently can't marshal existential containers from Swift arguments to C# types. Need to extract witness tables and reconstruct managed protocol objects. | Design gap |
| **6b. Starscream event delivery** | `IWebSocketDelegate.DidReceive(WebSocketEvent, IWebSocketClient)` actually invoked from Swift when events occur. Currently compile-only. | Validation |

**Projected impact**: Starscream 3.45 → 3.80+ (Protocols +1, Overall +0.5). Avg +0.02-0.03.

**Acceptance gate**: Starscream `IWebSocketDelegate` implementation receives events at runtime on iOS Simulator. 32/32 validation maintained.

---

## Lower-Priority Items (not yet sessionized)

These are real improvements but have lower effort-to-impact ratios than Sessions 1-6. They can be bundled into future sessions or addressed opportunistically.

| Item | Impact | Effort | Notes |
|------|--------|:------:|-------|
| String enum raw values from swiftinterface | GRDB `ResultCode` as enum | Medium | ABI JSON lacks raw values; parse from swiftinterface |
| `Optional<Primitive/Enum>` in closures | Various closure-accepting APIs | Medium | Different ABI from pointer-based Optional |
| Complex enums in closures | Various | Medium | Structural emitter change |
| ExistentialContainer0 in tuples | Lottie edge case | Small | ~22 AnyType locations |
| `async throws(ErrorType)` free functions | Guarded, rare | Small | `_payload`/`this` in static context |
| Method bypass with marshalled passthrough params | Theoretical | Medium | 0 real-world methods currently bypass |
| `flatMap` constrained generics (if S4c doesn't solve) | RxSwift composition | Medium-Large | `where Source: ObservableConvertibleType` |
| SCREAMING_CASE naming (Mappedin) | Mappedin polish | Small | `THING_KEY` → `ThingKey` |
| `_object` parameter naming (Mappedin) | Mappedin polish | Small | Already partially fixed in S8b |

---

## Sequencing & Dependencies

```
Session 1: Ergonomic Polish                     (independent, do first)
           (nint overloads, Get prefix,
            async false positives, _event naming)

Session 2: Foundation.Data Projection           (independent)
           (SwiftData runtime type,
            Data→byte[] projection)
     │
     └─► Session 3: Closure Bridge Generalization  (depends on S2 for Alamofire)
                    (responseData {}, Stripe callbacks)

Session 4: RxSwift Depth                        (independent)
           (subscribe existential return,
            Map ISwiftObject relaxation)

Session 5: Existential Dict/Array Values        (independent, multi-session)
           (Mixpanel full API)

Session 6: Existential Marshalling in Callbacks (independent)
           (Starscream runtime events)
```

Sessions 1, 2, and 4 are independent and can be done in any order (or in parallel). Session 3 depends on Session 2 for the Alamofire `responseData` return type. Sessions 5 and 6 are independent deep-structural work.

**If you only have 3 sessions**: Do 1, 2, 4 — best coverage across the most libraries.
**If you only have 5 sessions**: Do 1, 2, 3, 4, then either 5 or 6 based on whether Mixpanel or Starscream matters more.

---

## Projected Outcomes

### After Session 1 (polish only)

| Library | Current | Projected | Delta | Notes |
|---------|:-------:|:---------:|:-----:|-------|
| SnapKit | 3.40 | 3.60 | +0.20 | `EqualTo` naming, no async false positives |
| RxSwift | 2.75 | 2.90 | +0.15 | `Skip(int)`, `Take(int)`, better naming |
| GRDB | 3.20 | 3.35 | +0.15 | `Row[int]` indexer, naming |
| Mixpanel | 3.25 | 3.35 | +0.10 | `Track(string? event)` naming fix |
| SkeletonView | 3.25 | 3.35 | +0.10 | No `ShowSkeletonAsync` false positive |
| Others | — | — | +0.05 | Broad naming improvement |
| **Average** | **3.62** | **~3.72** | **+0.10** | |

### After Sessions 1-4 (~4 sessions, recommended minimum)

| Library | Current | Projected | Delta | Key Session |
|---------|:-------:|:---------:|:-----:|:----------:|
| Alamofire | 3.30 | 3.80 | +0.50 | S2 + S3 |
| RxSwift | 2.75 | 3.30 | +0.55 | S1 + S4 |
| SnapKit | 3.40 | 3.60 | +0.20 | S1 |
| GRDB | 3.20 | 3.35 | +0.15 | S1 |
| Mixpanel | 3.25 | 3.35 | +0.10 | S1 |
| SkeletonView | 3.25 | 3.35 | +0.10 | S1 |
| KeychainAccess | 3.65 | 3.75 | +0.10 | S2 |
| Others | — | — | +0.05 | S1 polish |
| **Average** | **3.62** | **~3.85** | **+0.23** | |

### After All 6 Sessions (full roadmap)

| Library | Current | Projected | Delta | Key Session |
|---------|:-------:|:---------:|:-----:|:----------:|
| Alamofire | 3.30 | 3.80 | +0.50 | S2 + S3 |
| RxSwift | 2.75 | 3.30 | +0.55 | S1 + S4 |
| Mixpanel | 3.25 | 3.70 | +0.45 | S1 + S5 |
| Starscream | 3.45 | 3.80 | +0.35 | S6 |
| SnapKit | 3.40 | 3.60 | +0.20 | S1 |
| GRDB | 3.20 | 3.35 | +0.15 | S1 |
| SkeletonView | 3.25 | 3.35 | +0.10 | S1 |
| KeychainAccess | 3.65 | 3.75 | +0.10 | S2 |
| Others | — | — | +0.05 | S1 polish |
| **Average** | **3.62** | **~3.90** | **+0.28** | |

**Realistic range**: 3.80–3.95.

**To reach 4.0+**: Would require string enum raw values (GRDB), deeper ObjC integration (Lottie IInterpolatable existentials), `Optional<Primitive/Enum>` in closures, and more complete protocol extension coverage. Achievable but not in 6 sessions.

---

## Issues Carried Forward

| Issue | Origin | Session |
|-------|--------|:-------:|
| `Optional<Primitive/Enum>` in closures | Q3 (Phase 2) | Unscheduled |
| Complex enums in closures | Q3 (Phase 2) | Unscheduled |
| ExistentialContainer0 in tuple elements | Pre-existing | Unscheduled |
| `async throws(ErrorType)` free functions | Pre-existing | Unscheduled |

---

## Completed Work Reference

- `Completed/usability-roadmap-sessions-1-10.md` — Sessions 1–10B (v2→v3 roadmap, all complete)
- `Completed/roadmap-completed-feb2026.md` — Phase 2 sessions Q1–Q4
- `Completed/binding-review-feb-23.md` — v1 binding review
- `binding-review-v2.md` — v2 binding review (post-Phase 2)
- `binding-review-v3.md` — v3 binding review (post-usability roadmap Sessions 1–10B)
