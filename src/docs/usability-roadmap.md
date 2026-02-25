# Usability Roadmap

**Created**: February 2026 (post-v2 binding review)
**Revised**: February 2026 (post Codex + Claude correction pass)
**Goal**: Make every validation library's critical workflow fully usable from C#
**Scoring reference**: `binding-review-v2.md` — 18-library quality review, 10-category scorecards
**Current baseline**: 3.45 avg (range: 2.40 RxSwift — 4.44 SmartCardIO)

---

## Framing

There are two targets here, and they're not identical:

1. **Critical workflows fully usable** in each library (compile + works at runtime)
2. **Average binding quality score above 4.0/5.0**

A library can be "critically usable" and still score 3.7 because of naming/noise/polish. We optimize for **workflow completion first** — scores will follow.

### Success Metrics

Track two things in parallel:

**1. Average score** (current rubric, 10 categories, 18 libraries)

**2. Critical workflow pass matrix** (new):

| Library | Critical Workflow | Compiles? | Runs? |
|---------|------------------|:---------:|:-----:|
| Alamofire | `Session.request(url).responseData { }` | | |
| Kingfisher | `KF.Builder.setProcessor().setCache().set(imageView)` | | |
| SnapKit | `view.snp.makeConstraints { make in make.top.equalTo(other) }` | | |
| GRDB | `pool.read { db in db["table"] }` | | |
| Mixpanel | `Mixpanel.track(event: "click", properties: dict)` | | |
| RxSwift | `Observable.map { }.filter { }.subscribe { }` | | |
| CryptoSwift | `try AES(key: key, blockMode: CBC(iv: iv)).encrypt(data)` | | |
| Stripe | `STPAPIClient().confirmPaymentIntent(params) { result in }` | | |
| Nuke | `ImagePipeline.shared.loadImage(ImageRequest(url:))` | | |
| SkeletonView | `view.showSkeleton()` / `view.hideSkeleton()` | | |
| Starscream | `WebSocket(request:)` + `IWebSocketDelegate` events | | |
| KeychainAccess | `keychain["key"] = "value"` + fluent chain | | |
| Lottie | `LottieAnimationView(name:).play { finished in }` | ✅ | ✅ |
| BlinkID | `BlinkIdRecognizer()` + scan result access | ✅ | |
| BlinkIDUX | `CaptureService` with async stream | | |

---

## Where We Are

### Current Scores

| Library | Score | Core Blocker |
|---------|:-----:|-------------|
| SmartCardIO | 4.44 | Minor — `object _params` existential |
| MicroblinkPlatform | 4.33 | Minor — naming collisions |
| Mappedin | 4.30 | Minor — SCREAMING_CASE names |
| Lottie | 4.10 | AnyType in ~22 locations (IInterpolatable) |
| BlinkIDUX | 3.60 | Empty `IUXThemeProtocol` (21 members skipped), actor isolation |
| Nuke | 3.60 | Missing primary `ImageRequest(url:)` constructor |
| Stripe | 3.55 | Cross-module type duplication |
| BlinkID | 3.55 | Bound-generic optional projection failure |
| KeychainAccess | 3.45 | `Method` suffix naming, missing subscript |
| Starscream | 3.40 | `IWebSocketDelegate` empty (event delivery) |
| CryptoSwift | 3.22 | Concrete types don't declare `: IProtocol` |
| Alamofire | 3.10 | Self-returning methods skipped (`Self` → AnyType → ContainsPlaceholder) |
| Kingfisher | 3.10 | 32/38 builder methods are **protocol extension defaults** (need U5, not Self-resolution) |
| SnapKit | 3.10 | Empty marker interfaces block core DSL, no `view.snp` entry point |
| GRDB | 3.00 | Generic throwing closures (`(Database) throws -> T`) |
| SkeletonView | 3.00 | UIKit extensions not projected (`showSkeleton()` on UIView) |
| Mixpanel | 2.90 | Core `track(event:properties:)` completely absent (`[String: Any]` params) |
| RxSwift | 2.40 | All operators are protocol extension methods with generic constraints |

### Weakest Categories (column averages)

| Category | Avg | Notes |
|----------|:---:|-------|
| Protocol/Interface | 3.08 | Concrete types missing `: IProtocol`, empty interfaces |
| Overall Usability | 3.14 | Core workflows blocked by closure/extension gaps |
| Noise/Leakage | 3.17 | Internal types leak, `_value` params |
| Completeness | 3.31 | Methods silently skipped |

---

## Session Plan

### Session 1: Foundation + Quick Wins

**Theme**: Protocol conformance, concrete Self-resolution, collection + optional projection
**Effort**: 1 session | **Libraries improved**: ~8-10
**Status**: ✅ COMPLETE

Merges the highest-leverage bug fixes into one session. Everything here is bounded and has existing infrastructure to build on.

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **1a. Emit `: IProtocol` on concrete types** | Struct/class types register protocol conformance in `_protocolConformanceSymbols` but don't declare `: IProtocol` on the class. The conformance data exists; the class/struct emitter needs to add the interface declaration. Must validate method signatures match (skip if interface has methods the type can't implement). | Generator bug |
| **1b. Resolve `Self` returns on concrete types** | When a concrete instance method returns `Self` (same type), project it to the concrete C# return type instead of `AnyType`. Currently: `Self` → `AnyType` → `ContainsPlaceholder` → method skipped. Fix: detect concrete-Self pattern and emit `public DataRequest Cancel() { ... }`. Different from protocol `TSelf` (which Q4 already handles). | Generator bug |
| **1c. Project `SwiftSet<T>` → `IReadOnlySet<T>`** | Follow the `SwiftArray<T>` → `IReadOnlyList<T>` pattern. **Runtime change required**: `SwiftSet<T>` currently implements `ICollection<T>` + `IReadOnlyCollection<T>` but NOT `IReadOnlySet<T>`. Add `IReadOnlySet<T>` implementation (requires `Contains`, `IsSubsetOf`, `IsProperSubsetOf`, `IsSupersetOf`, `IsProperSupersetOf`, `Overlaps`, `SetEquals` — some already exist). Then generator projects returns as `IReadOnlySet<T>`, parameters accept `IEnumerable<T>`. | Generator gap + Runtime |
| **1d. Bound-generic optional projection** | `Optional<DateResult<StringResult>>` falls back to `SwiftOptional<...>` because nested generics bypass the projection pipeline. Recurse into generic type arguments. | Generator bug |

**Critical workflows advanced**:
- CryptoSwift: `new AES(key, new CBC(iv))` compiles (CBC `: IBlockMode`)
- Alamofire: `Request.Cancel()`, `.Resume()`, `.Suspend()` builder chain works
- BlinkID: 9 date properties properly projected

**Acceptance gate**: CryptoSwift `new AES(key, new CBC(iv))` compiles (runtime: constructor doesn't throw). Alamofire Self-returning methods emit and chain compiles. `SwiftSet<T>` implements `IReadOnlySet<T>` (runtime: existing SwiftSet tests pass + new IReadOnlySet tests). BlinkID `Optional<DateResult<StringResult>>` projects to `DateResult<StringResult>?`. 32/32 validation maintained.

---

### Session 2: Swiftinterface + Actor Isolation + Marker Protocols

**Theme**: Use .swiftinterface for noise reduction, actor correctness, and primitive overloads
**Effort**: 1 session | **Libraries improved**: ~5-8
**Status**: ✅ COMPLETE

| Sub-task | Description | Result |
|----------|-------------|--------|
| **2a. Access-level filtering** | SwiftInterfaceAccessParser extended with access level, actor isolation, nonisolated, and marker protocol conformance extraction. Multi-line continuation support. `[EditorBrowsable(Never)]` on `_`-prefixed types across all 6 type handlers. | Complete |
| **2b. Parse `@MainActor`** | `@MainActor` / `@_Concurrency.MainActor` detection with nonisolated member opt-outs. Propagated to type/method metadata. | Complete |
| **2c. Emit actor isolation on wrappers** | `@MainActor` emitted on closure, async, marshalling, and marker protocol Swift wrappers. Nonisolated opt-out on methods/properties. | Complete |
| **2d. Marker protocol primitive overloads** | MarkerProtocolOverloadEmitter — CallConvSwift P/Invoke with LibraryImport, configurable wrapper library path, UnsafeMutableRawPointer self with unsafeBitCast/assumingMemoryBound. | Complete |

**Acceptance gate**: All met. SnapKit `Offset(10.0)` compiles with double overload. BlinkIDUX wrapper compiles with 0 actor isolation errors. Internal types filtered. 32/32 validation maintained. Includes 3 rounds of Codex review fixes (9 findings).

---

### Session 3: Existential Default-Arg Bypass & Protocol Receiver Relaxation

**Theme**: Bypass existential params with defaults + recover protocol interface methods
**Effort**: 1 session | **Libraries improved**: 13 (protocol recovery)
**Status**: ✅ COMPLETE — see `src/docs/Completed/session-3-results.md`

**Actual scope** (revised from original roadmap): The dictionary/existential projection work (original 3a-3c) was deferred in favor of the bypass + protocol recovery plan (`session-3-plan.md`), which targets infrastructure that unblocks method and protocol interface emission.

| Sub-task | Description | Result |
|----------|-------------|--------|
| **3a. Method bypass generalization** | Extend `ExistentialBypassEmitter` from constructors to class/struct instance methods. Refactored MethodHandler to accumulate pattern. | Infrastructure added; 0 real-world methods bypass (passthrough params require marshalling) |
| **3b. Protocol interface recovery** | Convert ProtocolHandler B9 gate from hard-skip to fall-through. Emit `NotSupportedException` proxy stubs (Q4b pattern). | **45 methods recovered across 32 protocols in 13 libraries** |
| **3c. Audit** | Measure actual impact, run full test + validation suite. | All tests pass at baseline. Documented in results. |

**Measured impact**: 45 protocol interface methods recovered (Kingfisher 8, Starscream 7, StripeConnect 7, Alamofire 6, StripeUICore 4, Mixpanel 3, GRDB 3, SkeletonView 2, RxSwift 1, StripeCore 1, Stripe 1, StripeApplePay 1, BlinkIDUX 1).

**Deferred to future sessions**:
- `[String: Any]` → `Dictionary<string, object>` projection (original roadmap 3a)
- `any Protocol` → `object` parameter projection (original roadmap 3b)
- Method bypass with marshalled passthrough params

---

### Session 4: Generic Throwing Closures (GRDB-Targeted Slice)

**Theme**: Unlock `(T) throws -> U` closure parameters, scoped to GRDB's `read`/`write` pattern first
**Effort**: 1 session | **Libraries improved**: GRDB + any library with generic throwing closures
**Status**: ✅ COMPLETE

Implemented Pattern A monomorphized bridge: specializes `T=UnsafeMutableRawPointer` in a `@_silgen_name` Swift wrapper, with cdecl callback pairs and GCHandle-based context passing on the C# side.

| Sub-task | Description | Result |
|----------|-------------|--------|
| **4a. Analyze generic closure ABI** | Mapped register layout for generic return via UnsafeMutableRawPointer specialization. | Complete |
| **4b. Monomorphized closure bridge** | GenericClosureBridgeEmitter (TryEmit pattern) emits both returning (T) and void variants with aligned result buffer allocation, VWT lifecycle. | Complete |
| **4c. Throwing closure error marshalling** | Full error propagation via SBW_CreateError/GetErrorDescription/ReleaseError/Free cdecl helpers. | Complete |

**Gates** (hardened via two rounds of Codex review):
- Closure must throw (non-throwing generates invalid Swift throw)
- Generic params only in return position (input ABI mismatch)
- Concrete closure args must be class types with TypeRecord (AnyObject cast)
- No non-closure params supported (no marshalling emitted)
- Identity-forwarding return, noescape, no constraints, not async

**Acceptance gate**: All met. GRDB `DatabasePool.read`/`write` with `Database` closure parameter compile. 32/32 validation maintained.

---

### Session 5: Protocol Extension Methods — Owned Types (Kingfisher-First)

**Theme**: Project Swift protocol extension methods as callable API on conforming types
**Effort**: 1 session | **Libraries improved**: Kingfisher + any library with class-conforming protocol extensions
**Status**: ✅ COMPLETE

Protocol extension methods don't appear in ABI JSON — parsed from `.swiftinterface` files and dispatched via `@_silgen_name` Swift wrappers (static dispatch, Swift calling convention compatible with existing `CallConvSwift` P/Invoke pipeline).

| Sub-task | Description | Result |
|----------|-------------|--------|
| **5a. Parse protocol extension methods from swiftinterface** | `GetProtocolNames()` + `GetProtocolExtensionMethods()` in SwiftInterfaceAccessParser. Handles `#if compiler` blocks, multi-line signatures, `@MainActor`, `where` constraints. | Complete |
| **5b. ProtocolExtensionEmitter** | Static emitter: conformance mapping, conservative gates (class-only self, no closures/existentials/structs/async/throwing/constrained), synthetic MethodDecl creation (`UsesWrapperLibrary` + `UsesFreeFunctionWrapper`), `@_silgen_name` Swift wrapper generation. | Complete |
| **5c. Pipeline wiring** | Injection in Program.cs after `typeDatabase.AddModuleDatabase()`, Swift wrapper emission in ModuleHandler after type loop. | Complete |
| **5d. Kingfisher validation** | 18 `KFOptionSetter` extension methods on `KF.Builder` with `Builder` return type. Fluent chain compiles. | Complete |

**Gates** (hardened via two rounds of Codex review):
- Class self only (struct self ABI deferred to Session 6+)
- No closures, existentials, async, throwing, or constrained extensions
- Parameters: class types (IntPtr) + primitives only (no SimpleEnum/ObjCBridged — wrapper marshals as `Unmanaged`)
- Return: Self, Void, or class type
- ABI collision check via PrintedName-style keys
- Symbol naming uses parameter labels + type suffixes for overload disambiguation

**Acceptance gate**: All met. 18 KF.Builder extension methods generated. 32/32 validation maintained. Two rounds of Codex review fixes applied (static state lifecycle, gate narrowing, async/throws gates, overload handling).

**Depends on**: Session 2 (swiftinterface parsing infrastructure)

---

### Session 6: Protocol Extensions — Foreign Types + UIKit

**Theme**: Project extension methods on types we don't own (`UIView`, `UITableView`, etc.)
**Effort**: 1 session | **Libraries improved**: 2-4

Extensions on foreign types (SnapKit's `view.snp`, SkeletonView's `view.showSkeleton()`) are the entry points for these libraries. Without them, the entire library is unreachable.

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **6a. Detect foreign-type extensions in swiftinterface** | Parse `extension UIView` blocks in `.swiftinterface`. Map to the ObjC-bridged C# type. | Design gap |
| **6b. Emit C# extension methods** | Generate `public static class UIViewExtensions { public static SnapKitDSL Snp(this UIView view) { ... } }`. Dispatch via the mangled symbol. Handle computed properties as extension methods. | Design gap |
| **6c. SnapKit + SkeletonView validation** | Verify `view.Snp().MakeConstraints(...)` and `view.ShowSkeleton()` compile. | Validation |

**Critical workflows advanced**:
- SnapKit: `view.Snp().MakeConstraints(...)` compiles
- SkeletonView: `view.ShowSkeleton()` / `view.HideSkeleton()` compiles

**Acceptance gate**: Entry-point extension methods emit for both libraries. Runtime: `view.ShowSkeleton()` executes on simulator without crash (SkeletonView); `view.Snp()` returns a non-null DSL object (SnapKit). 32/32 validation maintained.

**Fallback**: If foreign-type extension dispatch is unreliable (ObjC class metadata complications), generate targeted `@_cdecl` Swift wrappers for the top entry-point extensions (`snp`, `kf`, `showSkeleton`, `hideSkeleton`). These are leaf functions — shims are straightforward.

**Depends on**: Session 5 (protocol extension parsing infrastructure)

---

### Session 7: Protocol Extensions — RxSwift Operators (Bounded Scope)

**Theme**: Project constrained generic protocol extension methods (operators)
**Effort**: 1-2 sessions | **Libraries improved**: 1-2 (but deeply)

RxSwift's operators (`map`, `filter`, `flatMap`, `subscribe`) are protocol extension methods with generic constraints. This is the hardest case on the entire roadmap. **Scope this tightly**: target top-N operators for `Observable`, not "all operators."

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **7a. Handle generic constraints in extension methods** | Map Swift `where` clauses to C# generic constraints where possible. Fall back to runtime checks where C# can't express the constraint. | Design gap |
| **7b. Emit operator methods on Observable** | Target the top operators: `map`, `filter`, `flatMap`, `subscribe`, `disposed(by:)`. Each has generic parameters and `ObservableType` constraints. | Design gap |
| **7c. RxSwift validation** | `Observable<int>.map { x -> string }.filter { ... }.subscribe { }` compiles. | Validation |

**Critical workflows advanced**:
- RxSwift: Basic reactive pipeline works

**Acceptance gate**: Top 5-10 operators emit on `Observable<T>`. Basic pipeline compiles (runtime: `Observable.just(42).map { $0 * 2 }.subscribe { }` executes on simulator, observer receives value). 32/32 validation maintained.

**Risk note**: RxSwift may cap at ~3.5 even with good operator support due to the complexity of its full API surface. Define success as "top N operators work," not "all 76 operators."

**Fallback**: If generic constraint mapping proves intractable for RxSwift's operator signatures, fall back to emitting the operators as unconstrained extension methods with runtime type checks. Less type-safe but functionally usable. Alternatively, provide hand-written `@_cdecl` wrappers for the top 5 operators as a stopgap.

**Depends on**: Sessions 5-6 (protocol extension infrastructure)

---

### Session 8: Naming + Polish + Cross-Module

**Theme**: Fix naming heuristics, parameter names, GetHashCode, Stripe unification
**Effort**: 1 session | **Libraries improved**: ~10+

De-prioritized until after workflow unlocks (Sessions 1-7). These are "finish quality" improvements that move scores +0.10-0.20 across many libraries.

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **8a. Fix `Method` suffix collision avoidance** | Use `With` prefix (`WithAccessibility`) instead of `Method` suffix. | Generator bug |
| **8b. Fix `_value`/`_object` parameter naming** | Strip leading underscore from omitted external label parameters. | Generator bug |
| **8c. Wire `ISwiftHashable` into `GetHashCode()`** | Route through Swift `hashValue` instead of returning 0. | Generator bug |
| **8d. Apple SDK type database expansion** | `NSTextAlignment`, `UIEdgeInsets`, `CGColorSpace`, `UIColor`. | Generator gap |
| **8e. `value0` tuple element naming** | Use type name lowercased when no label exists. | Generator bug |
| **8f. Cross-module type unification** | Stripe's `STPAPIClient` across 5 modules. Emit C# extension methods or merge into canonical partial class. | Generator gap |

**Acceptance gate**: No `Method` suffix. No `_value` from omitted labels. Non-zero `GetHashCode()` on hashable types. Stripe `STPAPIClient` unified. 32/32 validation maintained.

---

### Session 9: Safety & Hardening

**Theme**: Fix runtime memory issues, smoke-test newly unlocked workflows
**Effort**: 0.5-1 session

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **9a. Fix proxy `Dispose()` no-op** | SkeletonView's 11 proxy classes leak `GCHandle`/`EveryProtocol`. Implement proper cleanup. | Runtime bug |
| **9b. Finalizer leak mitigation** | `[DebuggerDisplay]` on finalized handles showing "LEAKED — call Dispose()". Document in XML doc comments. | Safety |
| **9c. Runtime smoke tests** | Targeted tests for newly unlocked workflows: GRDB read/write, CryptoSwift composition, Kingfisher builder chain (where runtime test infra allows). | Validation |

**Acceptance gate**: Proxy Dispose properly cleans up. No new memory leaks from supported workflows.

---

### Session 10: Library-Specific Patches

**Theme**: Targeted fixes for remaining critical workflow gaps
**Effort**: 1 session

Pragmatic endgame. Sometimes the fastest path to "usable" is a targeted fix, not a general solution.

| Sub-task | Description | Target Library |
|----------|-------------|---------------|
| **10a. `ImageRequest(url:)` constructor** | The primary Nuke constructor is missing — only `init(stringLiteral:)` exists. Investigate why `URL` parameter blocks emission and fix specifically. | Nuke |
| **10b. Mixpanel `track()` exact pathway** | If Session 3's `[String: Any]` doesn't fully unlock `track(event:properties:)`, patch the specific gap. | Mixpanel |
| **10c. Alamofire response handlers** | `DataRequest.responseData { }` / `responseString { }` — the response side of the request-response workflow. Likely needs closure + generic return. | Alamofire |
| **10d. Keychain subscript** | The defining `keychain["key"]` pattern. Investigate why the subscript was skipped (likely complex index type or optional return). | KeychainAccess |
| **10e. `IWebSocketDelegate` event delivery** | Starscream's primary event mechanism. The delegate has closure/enum parameters that block emission. Targeted fix for the specific closure signature. | Starscream |

**Acceptance gate**: Each targeted workflow compiles. Matrix updated.

---

## Sequencing & Dependencies

```
Session 1: Foundation + Quick Wins                    ✅ COMPLETE
 │         (conformance, Self-concrete, SwiftSet,
 │          bound-generic optional)
 │
 ├─► Session 2: Swiftinterface + Actor + Markers     ✅ COMPLETE
 │
 ├─► Session 3: Existential Bypass + Protocol Recovery ✅ COMPLETE
 │
 ├─► Session 4: Generic Throwing Closures            ✅ COMPLETE
 │
 └─► Session 5: Protocol Extensions — Owned Types    ✅ COMPLETE
      │         (Kingfisher builder chain)
      │
      └─► Session 6: Protocol Extensions — Foreign    ← NEXT (depends on Session 5)
           │         (SnapKit snp, SkeletonView)
           │
           └─► Session 7: Protocol Extensions — RxSwift  ← Depends on 5-6, high risk
                         (bounded operator scope)

Session 8: Naming + Polish + Cross-Module             ← Independent, defer until after 1-7
Session 9: Safety & Hardening                         ← After workflows are unlocked
Session 10: Library-Specific Patches                  ← Endgame
```

**If you only have 5 sessions**, do: 1, 2, 3, 4, 5 — best chance of moving critical workflows.

---

## Projected Outcomes

### After Sessions 1-3 (~3 sessions, "quick wins")

| Library | Current | Projected | Delta | Confidence |
|---------|:-------:|:---------:|:-----:|:----------:|
| SmartCardIO | 4.44 | 4.50 | +0.06 | High |
| MicroblinkPlatform | 4.33 | 4.40 | +0.07 | High |
| Mappedin | 4.30 | 4.40 | +0.10 | High |
| Lottie | 4.10 | 4.15 | +0.05 | High |
| BlinkIDUX | 3.60 | 3.80 | +0.20 | Medium |
| Nuke | 3.60 | 3.75 | +0.15 | Medium |
| Stripe | 3.55 | 3.70 | +0.15 | High |
| BlinkID | 3.55 | 3.75 | +0.20 | High |
| KeychainAccess | 3.45 | 3.65 | +0.20 | Medium |
| Starscream | 3.40 | 3.55 | +0.15 | Medium |
| CryptoSwift | 3.22 | 3.55 | +0.33 | High |
| Alamofire | 3.10 | 3.40 | +0.30 | High |
| Kingfisher | 3.10 | 3.20 | +0.10 | Medium |
| SnapKit | 3.10 | 3.35 | +0.25 | Medium |
| GRDB | 3.00 | 3.10 | +0.10 | Medium |
| SkeletonView | 3.00 | 3.15 | +0.15 | Medium |
| Mixpanel | 2.90 | 3.30 | +0.40 | Medium |
| RxSwift | 2.40 | 2.50 | +0.10 | High |
| **Average** | **3.45** | **~3.57** | **+0.12** | |

### After All 10 Sessions (realistic)

| Library | Current | Projected | Delta | Key Session |
|---------|:-------:|:---------:|:-----:|:----------:|
| SmartCardIO | 4.44 | 4.55 | +0.11 | 1, 8 |
| MicroblinkPlatform | 4.33 | 4.45 | +0.12 | 2, 8 |
| Mappedin | 4.30 | 4.45 | +0.15 | 2, 8 |
| Lottie | 4.10 | 4.30 | +0.20 | 3, 8 |
| BlinkIDUX | 3.60 | 3.95 | +0.35 | 2, 5 |
| Nuke | 3.60 | 3.90 | +0.30 | 2, 10 |
| Stripe | 3.55 | 3.95 | +0.40 | 1, 8 |
| BlinkID | 3.55 | 3.80 | +0.25 | 1 |
| KeychainAccess | 3.45 | 3.90 | +0.45 | 1, 8, 10 |
| Starscream | 3.40 | 3.70 | +0.30 | 1, 10 |
| CryptoSwift | 3.22 | 3.60 | +0.38 | 1 |
| Alamofire | 3.10 | 3.65 | +0.55 | 1, 4, 10 |
| Kingfisher | 3.10 | 3.85 | +0.75 | 5 |
| SnapKit | 3.10 | 3.75 | +0.65 | 2, 6 |
| GRDB | 3.00 | 3.65 | +0.65 | 4 |
| SkeletonView | 3.00 | 3.50 | +0.50 | 6 |
| Mixpanel | 2.90 | 3.40 | +0.50 | 3, 10 |
| RxSwift | 2.40 | 3.15 | +0.75 | 7 |
| **Average** | **3.45** | **~3.81** | **+0.36** | |

**Realistic range**: 3.70–3.90 depending on how well protocol extension projection (Sessions 5-7) lands.

**To reach 4.0+**: Would require Sessions 5-7 to land exceptionally well AND additional work beyond this roadmap (ObjC integration for NSObject hierarchy, deeper generic constraint support). Achievable but not in 10 sessions.

---

## Issues Carried from Completed Work

| Issue | Origin | Addressed In |
|-------|--------|-------------|
| `Method` suffix collision avoidance produces un-idiomatic names | Q1a (Get prefix fix) | Session 8a |
| `Optional<Primitive/Enum>` in closures still blocked (different ABI) | Q3 (closure relaxation) | Session 4 follow-up |
| Complex enums in closures still blocked (structural emitter change) | Q3 (closure relaxation) | Session 4 follow-up |
| Concrete types don't get resolved Self-returning protocol methods | Q4 (Self returns) | Session 5 |
| Closure interface recovery stubs — dispatch still impossible | Q4b | Session 5-7 |
| SB0004 empty interfaces for genuinely-missing-implementation protocols | Q2 (diagnostics) | Session 5-7 |
| Proxy `Dispose()` no-op — memory leak | New in v2 | Session 9a |
| ExistentialContainer0 in tuple elements (Lottie edge case) | Pre-existing | Session 3 |
| `async throws(ErrorType)` free functions: `_payload`/`this` in static context | Pre-existing, guarded | Low priority |
| Bare `Any` in generic positions → AnyType | Pre-existing | Session 3 |

---

## What We Deliberately De-Prioritize

| Item | Why |
|------|-----|
| Full RxSwift operator parity (all 76) | Diminishing returns after top 10. Cap at "basic pipeline works." |
| Cross-module unification as a standalone session | Narrow (Stripe only). Bundled into Session 8. |
| Naming polish before workflow unlocks | Scores follow usability. Do naming last. |
| Universal generic closure solution before targeted GRDB fix | General solution may take 2+ sessions. Get one library working first. |

---

## Phase 2 Quality Work — Completed Reference

For details on what was already done (sessions Q1-Q4), see:
- `Completed/roadmap-completed-feb2026.md` — full session details
- `Completed/binding-review-feb-23.md` — original v1 scores
- `binding-review-v2.md` — post-Phase 2 scores and gap analysis
