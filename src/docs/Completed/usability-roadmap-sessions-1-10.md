# Usability Roadmap — Completed Sessions 1–10B

**Period**: February 2026
**Baseline**: v2 binding review avg 3.45 (range: 2.40 RxSwift — 4.44 SmartCardIO)
**Final result**: v3 binding review avg 3.62 (+0.17)
**Scoring references**: `binding-review-v2.md` (baseline), `binding-review-v3.md` (post-roadmap)

---

## Summary

10 sessions (1–9, 10A, 10B) completed across February 2026. All sessions maintained 32/32 library validation. The roadmap focused on workflow completion first — making each library's critical use case compile from C# — with naming/polish second.

**Critical workflow results** (final state):

| Library | Critical Workflow | Status | Compiles? | Runs? |
|---------|------------------|:------:|:---------:|:-----:|
| Alamofire | `Session.Request(url).SerializingData()` (async) | ~~Skip¹~~ Full (EP2) | ✅ | |
| Kingfisher | `KF.Builder.setProcessor().setCache().set(imageView)` | Full | ✅ | |
| SnapKit | `view.GetSnp().MakeConstraints { }` | Full | ✅ | ✅ |
| GRDB | `pool.Read { db in ... }` | Full | ✅ | |
| Mixpanel | `Mixpanel.Track(event:)` (no properties) | Partial² | ✅ | |
| RxSwift | `Observable.Filter(...).Map(...)` + non-closure ops | Full | ✅ | ✅ |
| CryptoSwift | `new AES(key, new CBC(iv))` | Full | ✅ | |
| Stripe | `STPAPIClient().ConfirmPaymentIntentAsync(params)` | Full | ✅ | |
| Nuke | `ImagePipeline.Shared.LoadImage(new ImageRequest(url))` | Full | ✅ | |
| SkeletonView | `view.ShowSkeleton()` / `view.HideSkeleton()` | Full | ✅ | ✅ |
| Starscream | `IWebSocketDelegate` (interface, no runtime delivery) | Partial⁴ | ✅ | |
| KeychainAccess | `keychain["key"] = "value"` + fluent chain | Full | ✅ | |
| Lottie | `LottieAnimationView(name:).Play { finished in }` | Full | ✅ | ✅ |
| BlinkID | `BlinkIdRecognizer()` + scan result access | Full | ✅ | |

¹ `DataTask<Data>` failed `HasNonSwiftObjectGenericArg` — **fixed in Ergonomic Polish Session 2** (DataProjection + bound generics unblock).
² `properties:` param requires `[String: any MixpanelType]` dict-existential projection.
⁴ Interface recovery + compile only. Runtime event delivery requires existential marshalling.

**Final: 11 Full, 2 Partial, 1 Skip.** (Updated post-EP2: 12 Full, 2 Partial, 0 Skip.)

---

## Session Details

### Session 1: Foundation + Quick Wins ✅

**Theme**: Protocol conformance, concrete Self-resolution, collection + optional projection
**Libraries improved**: ~8-10

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **1a. Emit `: IProtocol` on concrete types** | Struct/class types register protocol conformance in `_protocolConformanceSymbols` but don't declare `: IProtocol` on the class. The conformance data exists; the class/struct emitter needs to add the interface declaration. Must validate method signatures match (skip if interface has methods the type can't implement). | Generator bug |
| **1b. Resolve `Self` returns on concrete types** | When a concrete instance method returns `Self` (same type), project it to the concrete C# return type instead of `AnyType`. Currently: `Self` → `AnyType` → `ContainsPlaceholder` → method skipped. Fix: detect concrete-Self pattern and emit `public DataRequest Cancel() { ... }`. Different from protocol `TSelf` (which Q4 already handles). | Generator bug |
| **1c. Project `SwiftSet<T>` → `IReadOnlySet<T>`** | Follow the `SwiftArray<T>` → `IReadOnlyList<T>` pattern. Runtime: `SwiftSet<T>` now implements `IReadOnlySet<T>`. Generator projects returns as `IReadOnlySet<T>`, parameters accept `IEnumerable<T>`. | Generator gap + Runtime |
| **1d. Bound-generic optional projection** | `Optional<DateResult<StringResult>>` falls back to `SwiftOptional<...>` because nested generics bypass the projection pipeline. Recurse into generic type arguments. | Generator bug |

**Critical workflows advanced**: CryptoSwift `new AES(key, new CBC(iv))` compiles; Alamofire `Request.Cancel()/.Resume()/.Suspend()` chain works; BlinkID 9 date properties properly projected.

---

### Session 2: Swiftinterface + Actor Isolation + Marker Protocols ✅

**Theme**: Use .swiftinterface for noise reduction, actor correctness, and primitive overloads
**Libraries improved**: ~5-8

| Sub-task | Description | Result |
|----------|-------------|--------|
| **2a. Access-level filtering** | SwiftInterfaceAccessParser extended with access level, actor isolation, nonisolated, and marker protocol conformance extraction. Multi-line continuation support. `[EditorBrowsable(Never)]` on `_`-prefixed types across all 6 type handlers. | Complete |
| **2b. Parse `@MainActor`** | `@MainActor` / `@_Concurrency.MainActor` detection with nonisolated member opt-outs. Propagated to type/method metadata. | Complete |
| **2c. Emit actor isolation on wrappers** | `@MainActor` emitted on closure, async, marshalling, and marker protocol Swift wrappers. Nonisolated opt-out on methods/properties. | Complete |
| **2d. Marker protocol primitive overloads** | MarkerProtocolOverloadEmitter — CallConvSwift P/Invoke with LibraryImport, configurable wrapper library path, UnsafeMutableRawPointer self with unsafeBitCast/assumingMemoryBound. | Complete |

**Acceptance gate**: SnapKit `Offset(10.0)` compiles with double overload. BlinkIDUX wrapper compiles with 0 actor isolation errors. Internal types filtered. Includes 3 rounds of Codex review fixes (9 findings).

---

### Session 3: Existential Default-Arg Bypass & Protocol Receiver Relaxation ✅

**Theme**: Bypass existential params with defaults + recover protocol interface methods
**Libraries improved**: 13 (protocol recovery)
**Full results**: `session-3-results.md`

| Sub-task | Description | Result |
|----------|-------------|--------|
| **3a. Method bypass generalization** | Extend `ExistentialBypassEmitter` from constructors to class/struct instance methods. Refactored MethodHandler to accumulate pattern. | Infrastructure added; 0 real-world methods bypass (passthrough params require marshalling) |
| **3b. Protocol interface recovery** | Convert ProtocolHandler B9 gate from hard-skip to fall-through. Emit `NotSupportedException` proxy stubs (Q4b pattern). | **45 methods recovered across 32 protocols in 13 libraries** |
| **3c. Audit** | Measure actual impact, run full test + validation suite. | All tests pass at baseline |

**Measured impact**: 45 protocol interface methods recovered (Kingfisher 8, Starscream 7, StripeConnect 7, Alamofire 6, StripeUICore 4, Mixpanel 3, GRDB 3, SkeletonView 2, RxSwift 1, StripeCore 1, Stripe 1, StripeApplePay 1, BlinkIDUX 1).

**Deferred**: `[String: Any]` → `Dictionary<string, object>` projection; `any Protocol` → `object` parameter projection; method bypass with marshalled passthrough params.

---

### Session 4: Generic Throwing Closures (GRDB-Targeted Slice) ✅

**Theme**: Unlock `(T) throws -> U` closure parameters, scoped to GRDB's `read`/`write` pattern
**Libraries improved**: GRDB + any library with generic throwing closures

Implemented Pattern A monomorphized bridge: specializes `T=UnsafeMutableRawPointer` in a `@_silgen_name` Swift wrapper, with cdecl callback pairs and GCHandle-based context passing on the C# side.

| Sub-task | Description | Result |
|----------|-------------|--------|
| **4a. Analyze generic closure ABI** | Mapped register layout for generic return via UnsafeMutableRawPointer specialization. | Complete |
| **4b. Monomorphized closure bridge** | GenericClosureBridgeEmitter (TryEmit pattern) emits both returning (T) and void variants with aligned result buffer allocation, VWT lifecycle. | Complete |
| **4c. Throwing closure error marshalling** | Full error propagation via SBW_CreateError/GetErrorDescription/ReleaseError/Free cdecl helpers. | Complete |

**Gates**: Closure must throw; generic params only in return position; concrete closure args must be class types with TypeRecord; no non-closure params; identity-forwarding return, noescape, no constraints, not async.

**Acceptance gate**: GRDB `DatabasePool.read`/`write` with `Database` closure parameter compile.

---

### Session 5: Protocol Extension Methods — Owned Types (Kingfisher-First) ✅

**Theme**: Project Swift protocol extension methods as callable API on conforming types
**Libraries improved**: Kingfisher + any library with class-conforming protocol extensions

Protocol extension methods don't appear in ABI JSON — parsed from `.swiftinterface` files and dispatched via `@_silgen_name` Swift wrappers (static dispatch, Swift calling convention compatible with existing `CallConvSwift` P/Invoke pipeline).

| Sub-task | Description | Result |
|----------|-------------|--------|
| **5a. Parse protocol extension methods from swiftinterface** | `GetProtocolNames()` + `GetProtocolExtensionMethods()` in SwiftInterfaceAccessParser. Handles `#if compiler` blocks, multi-line signatures, `@MainActor`, `where` constraints. | Complete |
| **5b. ProtocolExtensionEmitter** | Static emitter: conformance mapping, conservative gates (class-only self, no closures/existentials/structs/async/throwing/constrained), synthetic MethodDecl creation. | Complete |
| **5c. Pipeline wiring** | Injection in Program.cs after `typeDatabase.AddModuleDatabase()`, Swift wrapper emission in ModuleHandler after type loop. | Complete |
| **5d. Kingfisher validation** | 18 `KFOptionSetter` extension methods on `KF.Builder` with `Builder` return type. Fluent chain compiles. | Complete |

**Gates**: Class self only; no closures, existentials, async, throwing, or constrained extensions; parameters: class types + primitives only; return: Self, Void, or class type.

**Depends on**: Session 2 (swiftinterface parsing infrastructure)

---

### Session 6: Protocol Extensions — Foreign Types + UIKit ✅

**Theme**: Project extension methods on types we don't own (`UIView`, `UITableView`, etc.)
**Libraries improved**: 11 (SnapKit, SkeletonView, CryptoSwift, Kingfisher, Lottie, Mixpanel, Nuke, Starscream, StripeApplePay, StripeFinancialConnections, StripePaymentSheet)

| Sub-task | Description | Status |
|----------|-------------|--------|
| **6a. Detect foreign-type extensions in swiftinterface** | `GetForeignTypeExtensionMembers()` in `SwiftInterfaceAccessParser.cs`. Detects qualified foreign type names, filters ObjC classes via `TypeDatabaseExtensions.IsObjCModuleType()`. | ✅ |
| **6b. Emit C# extension methods** | New `ForeignTypeExtensionEmitter.cs` (~800 lines). Emits `public static class UIViewSnapKitExtensions { ... }` with `@_silgen_name` Swift wrappers. Handles property getters/setters, methods with default parameter reduction, 5 return kinds. | ✅ |
| **6c. SnapKit + SkeletonView validation** | Both compile clean. SnapKit: `view.GetSnp()` returns `ConstraintViewDSL`. SkeletonView: `view.ShowSkeleton(color)`, `view.HideSkeleton(reload)`. | ✅ |

**Key details**: Default parameter reduction; `Foundation.TimeInterval` → `double`; `QuartzCore` → `CoreAnimation` namespace mapping; `[MarshalAs(UnmanagedType.U1)]` for bool; `IsForeignObjCClassType()` gate.

**Depends on**: Session 5 (protocol extension parsing infrastructure)

---

### Session 7: Protocol Extensions — RxSwift Operators (Bounded Scope) ✅

**Theme**: Project constrained generic protocol extension methods (operators)
**Libraries improved**: RxSwift (deeply)

Proved generic `@_silgen_name` ABI with explicit+implicit TypeMetadata passing (9/9 spike tests), then extended `ProtocolExtensionEmitter` to handle generic conforming types (`Observable<Element>`, etc.).

| Sub-task | Description | Result |
|----------|-------------|--------|
| **7a. Generic @_silgen_name ABI spike** | Proved double TypeMetadata passing works from C# `CallConvSwift` P/Invoke. 9 tests. See `generic-silgen-name-abi.md`. | ✅ |
| **7b. Generic type support in ProtocolExtensionEmitter** | Removed `ContainsGenericParameters` rejection. Added `<Element>` generic clause, `unsafeBitCast`, explicit `Element.Type` metatype params, `ResolveSelfElement`. | ✅ |
| **7c. ABI correctness fixes** | Fixed P/Invoke param ordering, suppressed PInvokeHelperContext metadata, `passUnretained` → `passRetained` for class returns. | ✅ |
| **7d. Closure-based operators** | Delivered in Session 10B via `ProtocolExtensionClosureBridge`. | ✅ (10B) |

**Results**: 97 `@_silgen_name` Swift wrappers, 21 non-closure operators per `ObservableType` conformer, RxSwift bindings 12,384 → 15,055 lines (+22%).

**Depends on**: Sessions 5-6 (protocol extension infrastructure)

---

### Session 8: Naming + Polish + Cross-Module ✅

**Theme**: Fix naming heuristics, parameter names, GetHashCode, Stripe unification
**Libraries improved**: ~10+

| Sub-task | Description | Result |
|----------|-------------|--------|
| **8a. Fix `Method` suffix** | Self-returning methods now use `With` prefix; non-self-returning keep `Method` suffix. | ✅ |
| **8b. Fix `_value`/`_object` parameter naming** | `value` treated as contextual keyword. `DeriveParameterNameFromType` before `_` prefix fallback. | ✅ |
| **8c. Wire `ISwiftHashable` into `GetHashCode()`** | Consistent `Swift.Hashable` conformance checks at all 4 sites. | ✅ |
| **8d. Apple SDK type database expansion** | `UIEdgeInsets` added as frozen struct shim. `NSTextAlignment` excluded (CS0234). | ✅ |
| **8e. `value0` tuple element naming** | `DeriveParameterNameFromType` for unnamed associated values. Dedup with numeric suffixes. | ✅ |
| **8f. Cross-module type unification** | `CrossModuleExtensionEmitter` (792 lines). Detects cross-module members, emits extension classes. | ✅ |

---

### Session 9: Safety & Hardening ✅

**Theme**: Fix runtime memory issues, smoke-test newly unlocked workflows

| Sub-task | Description | Status |
|----------|-------------|--------|
| **9a. Fix proxy `Dispose()` no-op** | Proxy Dispose now calls `SwiftObjectRegistry.Unregister` + `EveryProtocol.Dispose()`. `_disposed` field + `ObjectDisposedException` guards. | ✅ |
| **9b. Finalizer leak diagnostics** | `[DebuggerDisplay]` on `SwiftSafeHandle<T>` and `EveryProtocol`. XML doc `<remarks>` on `Dispose()`. | ✅ |
| **9c. Proxy lifecycle tests** | 10 emitter unit tests + 5 Tier 2 runtime tests. | ✅ |

---

### Session 10A: Targeted Bypass & Gate Fixes ✅

**Theme**: Existential-bypass with default-param reduction + library-specific gate fixes

| Sub-task | Description | Delivery | Target Library |
|----------|-------------|:--------:|---------------|
| **10a. `ImageRequest(url:)` constructor** | Reduced constructor via `@_silgen_name` wrapper (4 existential params with defaults omitted). | **Full** | Nuke |
| **10b. Mixpanel `Track(event:)`** | Reduced `Track(event:)` omitting `properties`. | **Partial** | Mixpanel |
| **10c. Alamofire `SerializingData()`** | Blocked by `HasNonSwiftObjectGenericArg` (Foundation.Data), not existential. | **Skip** | Alamofire |
| **10d. KeychainAccess subscript** | Fixed specific gate blocking subscript emission. | **Full** | KeychainAccess |
| **10e. Starscream `IWebSocketDelegate`** | Interface recovery with `NotSupportedException` proxy stub. Compile-time only. | **Partial** | Starscream |

---

### Session 10B: Closure Operators in Protocol Extensions ✅

**Theme**: Bridge closure TypeSpec params in ProtocolExtensionEmitter's `@_silgen_name` wrappers
**Depends on**: Session 7 (generic protocol extension ABI), Session 4 (GenericClosureBridgeEmitter pattern)

| Sub-task | Description | Result |
|----------|-------------|--------|
| **10f-1. ProtocolExtensionEmitter closure acceptance** | Relaxed `IsCdeclCompatibleType` to accept bridgeable closures. Swift wrapper generates `@convention(c)` function pointer + context → native closure reconstruction. | ✅ |
| **10f-2. ProtocolExtensionClosureBridge** | New C# emitter (749 lines). `[UnmanagedCallersOnly]` callback, static function pointer field, P/Invoke, public method with `Func<>`/`Action<>`. | ✅ |
| **10f-3. MemberEmissionValidator carve-out** | B20 closure check extended with `IsProtocolExtensionMethod && IsClosureBridgeable`. | ✅ |
| **10f-4. `filter`** | `Filter(Func<TElement, bool> predicate)` on 6 conforming types. | ✅ |
| **10f-5. `map<Result>`** | `Map<TResult>(Func<TElement, TResult> transform)` with method-level generic return. | ✅ |
| **10f-6. `subscribe`** | Returns `any Disposable` (existential). Blocked by return gate. | Deferred |

**Gates**: Single closure param; closure args: generic params or class types only; closure return: Void, Bool, or method-level generic only; no `where` constraints; no async closures.

**Results**: 12 new methods (6 `Filter` + 6 `Map`), GRDB 6 errors → 0.

---

## Sequencing & Dependencies (as executed)

```
Session 1: Foundation + Quick Wins                    ✅
 │         (conformance, Self-concrete, SwiftSet,
 │          bound-generic optional)
 │
 ├─► Session 2: Swiftinterface + Actor + Markers     ✅
 │
 ├─► Session 3: Existential Bypass + Protocol Recovery ✅
 │
 ├─► Session 4: Generic Throwing Closures            ✅
 │
 └─► Session 5: Protocol Extensions — Owned Types    ✅
      │         (Kingfisher builder chain)
      │
      └─► Session 6: Protocol Extensions — Foreign    ✅
           │         (SnapKit snp, SkeletonView)
           │
           └─► Session 7: Protocol Extensions — RxSwift  ✅
                │         (21 non-closure operators)
                │
                └─► Session 10B: Closure Operators         ✅
                              (RxSwift map/filter)

Session 8: Naming + Polish + Cross-Module             ✅ (independent)
Session 9: Safety & Hardening                         ✅
Session 10A: Targeted Bypass & Gate Fixes             ✅
```

---

## Issues Resolved

| Issue | Origin | Resolved In |
|-------|--------|-------------|
| Concrete types don't declare `: IProtocol` | v2 review | Session 1a |
| Self-returning methods skipped (`Self` → AnyType → ContainsPlaceholder) | v2 review | Session 1b |
| `SwiftSet<T>` not projected to `IReadOnlySet<T>` | v2 review | Session 1c |
| Bound-generic optional falls to `SwiftOptional<...>` | v2 review | Session 1d |
| Internal types leak into public API | v2 review | Session 2a |
| Actor isolation missing on Swift wrappers | v2 review | Session 2b-2c |
| Marker protocol primitives uncallable (SnapKit `Offset`) | v2 review | Session 2d |
| Protocol interface methods silently skipped (45 methods) | v2 review | Session 3b |
| Generic throwing closures (`(T) throws -> U`) | v2 review | Session 4 |
| Protocol extension methods not projected | v2 review | Sessions 5-7 |
| UIKit extension entry points missing (view.snp, view.showSkeleton) | v2 review | Session 6 |
| `Method` suffix collision avoidance produces un-idiomatic names | Q1a | Session 8a |
| `_value`/`_object` parameter naming | v2 review | Session 8b |
| `GetHashCode()` returns 0 | v2 review | Session 8c |
| `value0` unnamed tuple element parameters | v2 review | Session 8e |
| Cross-module type duplication (Stripe STPAPIClient) | v2 review | Session 8f |
| Proxy `Dispose()` no-op — memory leak | v2 review | Session 9a |
| `ImageRequest(url:)` constructor missing (Nuke) | v2 review | Session 10A |
| `Track(event:)` missing (Mixpanel) | v2 review | Session 10A |
| KeychainAccess subscript missing | v2 review | Session 10A |
| `IWebSocketDelegate` empty (Starscream) | v2 review | Session 10A |
| Closure TypeSpec rejection in protocol extensions | Session 7 | Session 10B |
| Concrete types don't get resolved Self-returning protocol methods | Q4 | Session 5 |
| Closure interface recovery stubs — dispatch still impossible | Q4b | Sessions 5-7 |
| SB0004 empty interfaces for genuinely-missing-implementation protocols | Q2 | Sessions 5-7 |
| Bare `Any` in generic positions → AnyType | Pre-existing | Session 3 |
| Existential params with defaults block method/constructor emission | Sessions 3, 10A | Session 10A |

## Issues Still Open (carried forward to new roadmap)

| Issue | Origin | Notes |
|-------|--------|-------|
| `Optional<Primitive/Enum>` in closures | Q3 | Different ABI from pointer-based Optional |
| Complex enums in closures | Q3 | Structural emitter change |
| ExistentialContainer0 in tuple elements | Pre-existing | Lottie edge case |
| `async throws(ErrorType)` free functions | Pre-existing | Guarded, low priority |
| `[String: any Protocol]` dict-existential | Deferred from S3 | Multi-session effort |
| `any Protocol` → `object` parameters | Deferred from S3 | Multi-session effort |
| Method bypass with marshalled passthrough params | S3 result | 0 real-world methods currently bypass |
| `subscribe` existential return | S10B | `any Disposable` return from protocol extension |
| `flatMap` constrained generics | S7 | `where Source: ObservableConvertibleType` |
| Foundation.Data not ISwiftObject | S10A | Blocks Alamofire `DataTask<Data>` |
| Generic closure params in method callbacks | S10B | Alamofire `responseData {}`, Stripe callbacks |
| Existential marshalling in `[UnmanagedCallersOnly]` | Pre-existing | Starscream runtime event delivery |

---

## Phase 2 Quality Work — Reference

For Phase 2 (Q1-Q4) details, see:
- `roadmap-completed-feb2026.md` — full session details
- `binding-review-feb-23.md` — original v1 scores
- `../binding-review-v2.md` — post-Phase 2 scores and gap analysis
- `../binding-review-v3.md` — post-usability roadmap scores and gap analysis
