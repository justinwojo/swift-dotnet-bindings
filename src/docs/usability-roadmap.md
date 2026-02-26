# Usability Roadmap

**Created**: February 2026 (post-v2 binding review)
**Revised**: February 2026 (Session 10A/10B split — honest Full/Partial delivery labels)
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

| Library | Critical Workflow | Status | Compiles? | Runs? |
|---------|------------------|:------:|:---------:|:-----:|
| Alamofire | `Session.Request(url).SerializingData()` (async) | Skip¹ | | |
| Kingfisher | `KF.Builder.setProcessor().setCache().set(imageView)` | Full | | |
| SnapKit | `view.GetSnp().MakeConstraints { }` | Full | ✅ | |
| GRDB | `pool.Read { db in ... }` | Full | | |
| Mixpanel | `Mixpanel.Track(event:)` (no properties) | Partial² | | |
| RxSwift | `Observable.Skip(n).Take(n).Publish()` (non-closure ops) | Partial³ | | |
| CryptoSwift | `new AES(key, new CBC(iv))` | Full | | |
| Stripe | `STPAPIClient().ConfirmPaymentIntent(params) { }` | Full | | |
| Nuke | `ImagePipeline.Shared.LoadImage(new ImageRequest(url))` | Full | | |
| SkeletonView | `view.ShowSkeleton()` / `view.HideSkeleton()` | Full | ✅ | |
| Starscream | `IWebSocketDelegate` (interface, no runtime delivery) | Partial⁴ | | |
| KeychainAccess | `keychain["key"] = "value"` + fluent chain | Full | | |
| Lottie | `LottieAnimationView(name:).Play { finished in }` | Full | ✅ | ✅ |
| BlinkID | `BlinkIdRecognizer()` + scan result access | Full | ✅ | |
| BlinkIDUX | `CaptureService` with async stream | — | | |

**Status legend**: **Full** = original roadmap workflow delivered. **Partial** = reduced or alternate pathway; original workflow still blocked. **Skip** = not fixable with current patterns.

¹ Original `responseData { }` blocked by generic closure in callback param. Async `SerializingData()` also blocked — `DataTask<Data>` fails `HasNonSwiftObjectGenericArg` (Foundation.Data doesn't satisfy ISwiftObject). Neither pathway fixable with current bypass patterns.
² `properties:` param requires `[String: any MixpanelType]` dict-existential projection (deferred structural work).
³ Closure-based operators (`map`, `filter`, `subscribe`) deferred to Session 10B. 21 non-closure operators shipped (Session 7).
⁴ Interface recovery + compile only. Runtime event delivery requires existential marshalling in `[UnmanagedCallersOnly]` callbacks.

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

### Session 6: Protocol Extensions — Foreign Types + UIKit ✅ COMPLETE

**Theme**: Project extension methods on types we don't own (`UIView`, `UITableView`, etc.)
**Effort**: 1 session | **Libraries improved**: 11 (SnapKit, SkeletonView, CryptoSwift, Kingfisher, Lottie, Mixpanel, Nuke, Starscream, StripeApplePay, StripeFinancialConnections, StripePaymentSheet)

Extensions on foreign types (SnapKit's `view.snp`, SkeletonView's `view.showSkeleton()`) are the entry points for these libraries. Without them, the entire library is unreachable.

| Sub-task | Description | Status |
|----------|-------------|--------|
| **6a. Detect foreign-type extensions in swiftinterface** | `GetForeignTypeExtensionMembers()` in `SwiftInterfaceAccessParser.cs`. Detects qualified foreign type names, filters ObjC classes via `TypeDatabaseExtensions.IsObjCModuleType()`. | ✅ |
| **6b. Emit C# extension methods** | New `ForeignTypeExtensionEmitter.cs` (~800 lines). Emits `public static class UIViewSnapKitExtensions { ... }` with `@_silgen_name` Swift wrappers. Handles property getters/setters, methods with default parameter reduction, 5 return kinds (Void, Primitive, ObjCClass, SwiftClass, NonFrozenStruct). | ✅ |
| **6c. SnapKit + SkeletonView validation** | Both compile clean. SnapKit: `view.GetSnp()` returns `ConstraintViewDSL` (non-frozen struct via `SwiftIndirectResult`). SkeletonView: `view.ShowSkeleton(color)`, `view.HideSkeleton(reload)`, property getters/setters. | ✅ |

**Results**:
- SnapKit: `view.GetSnp()` compiles (returns `ConstraintViewDSL` via `SwiftIndirectResult`)
- SkeletonView: `view.ShowSkeleton()` / `view.HideSkeleton()` / property getters+setters compile
- 11 libraries improved (fixed pre-existing foreign extension errors across the suite)
- 32/32 validation maintained

**Key implementation details**:
- `ForeignTypeExtensionEmitter.cs`: New static emitter class, parallel to `ProtocolExtensionEmitter`
- Default parameter reduction: incompatible params with defaults omitted, Swift fills them
- Type alias resolution: `Foundation.TimeInterval` → `double`
- Module namespace mapping: `QuartzCore` → `CoreAnimation`
- Bool P/Invoke: `[MarshalAs(UnmanagedType.U1)]` for proper byte↔bool marshalling
- Foreign type gate: `IsForeignObjCClassType()` — only ObjC classes (not `Swift.Double`, etc.)

**Depends on**: Session 5 (protocol extension parsing infrastructure)

---

### Session 7: Protocol Extensions — RxSwift Operators (Bounded Scope) ✅ PARTIAL

**Theme**: Project constrained generic protocol extension methods (operators)
**Effort**: 1 session | **Libraries improved**: RxSwift (deeply)
**Status**: ✅ PARTIAL — non-closure operators shipped; closure operators (map/filter/subscribe) deferred

Proved generic `@_silgen_name` ABI with explicit+implicit TypeMetadata passing (9/9 spike tests), then extended `ProtocolExtensionEmitter` to handle generic conforming types (`Observable<Element>`, etc.).

| Sub-task | Description | Result |
|----------|-------------|--------|
| **7a. Generic @_silgen_name ABI spike** | Proved double TypeMetadata passing (explicit `T.Type` + implicit trailing) works from C# `CallConvSwift` P/Invoke. 9 tests: identity, sizeOf/strideOf, filter with closures, map with two generic params, throwing filter with error propagation. | ✅ Complete — see `Completed/generic-silgen-name-abi.md` |
| **7b. Generic type support in ProtocolExtensionEmitter** | Removed `ContainsGenericParameters` rejection. Added `<Element>` generic clause, `unsafeBitCast` (Unmanaged requires non-generic T), explicit `Element.Type` metatype params, `ResolveSelfElement` for `Self.Element` → `τ_0_0` resolution. | ✅ Complete |
| **7c. ABI correctness fixes** | Fixed P/Invoke param ordering (`IsProtocolExtensionMethod` self_ before args, scoped to NOT affect `@_cdecl` closure wrappers). Suppressed PInvokeHelperContext metadata for protocol extension methods (prevents triple TypeMetadata). Fixed `passUnretained` → `passRetained` for class returns (prevents dangling pointer on new objects). | ✅ Complete |
| **7d. Closure-based operators** | `map`, `filter`, `subscribe`, `flatMap`, `disposed(by:)` — require closure TypeSpec bridging in protocol extension wrappers. | Deferred to Session 10 |

**Results**:
- 97 new `@_silgen_name` Swift wrappers across RxSwift
- 21 unique non-closure operators per `ObservableType` conformer: `Skip`, `Take`, `TakeLast`, `Retry`, `Single`, `Element`, `ElementAt`, `AsObservable`, `RefCount`, `Publish`, `Replay`, `ReplayAll`, etc.
- RxSwift bindings: 12,384 → 15,055 lines (+22%)
- 32/32 validation maintained

**Deferred**: Closure-based operators (`map`, `filter`, `subscribe`) require extending ProtocolExtensionEmitter to bridge closure TypeSpec params in `@_silgen_name` wrappers — passing func pointers and contexts, generating `@_cdecl` callbacks. This is the same pattern proven in the spike (tests S2a/S2b/S3a/S3b) but needs integration into the emitter pipeline. Folded into Session 10f.

**Depends on**: Sessions 5-6 (protocol extension infrastructure)

---

### Session 8: Naming + Polish + Cross-Module ✅ COMPLETE

**Theme**: Fix naming heuristics, parameter names, GetHashCode, Stripe unification
**Effort**: 1 session | **Libraries improved**: ~10+
**Status**: ✅ COMPLETE

| Sub-task | Description | Result |
|----------|-------------|--------|
| **8a. Fix `Method` suffix collision avoidance** | Self-returning methods now use `With` prefix (`WithAccessibility`); non-self-returning keep `Method` suffix. `isSelfReturning` param flows through `GetProjectedCSharpMethodKey` and `GetProjectedOverloadKey`. | ✅ |
| **8b. Fix `_value`/`_object` parameter naming** | `value` treated as contextual keyword (no `_` prefix). Parser-generated `_value`/`_object`/`_event` names use `DeriveParameterNameFromType` before falling back to `_` prefix. | ✅ |
| **8c. Wire `ISwiftHashable` into `GetHashCode()`** | Consistent `Swift.Hashable` conformance checks with `|| c.Protocol.Name == "Hashable"` fallback at all 4 sites (TypeHandlerHelpers ×3, ClassHandler ×1). | ✅ |
| **8d. Apple SDK type database expansion** | `UIEdgeInsets` added to UIKitDatabase.xml as frozen struct shim. `NSTextAlignment` excluded — .NET iOS doesn't expose it as `UIKit.NSTextAlignment` (causes CS0234). | ✅ |
| **8e. `value0` tuple element naming** | Enum case unnamed associated values use `DeriveParameterNameFromType` (e.g., `dateResult` instead of `value0`). Primitives still get `value{i}`. Post-loop dedup appends numeric suffixes. | ✅ |
| **8f. Cross-module type unification** | New `CrossModuleExtensionEmitter` (792 lines). Detects `classDecl.SwiftTypeName.Module != moduleDecl.Name`, emits `static partial class {TypeName}{CurrentModule}Extensions`. No Swift wrappers — uses existing mangled names. Gates: no generics, no async, no throws, no mutating. | ✅ |

**Acceptance gate**: All met. `With` prefix on self-returning methods. Contextual `value` param naming. Hashable conformance at all 4 sites. UIEdgeInsets in type database. Enum case type-derived names. Stripe cross-module extensions. 32/32 validation maintained.

**Depends on**: Sessions 1-7 (workflow unlocks first, naming polish second)

---

### Session 9: Safety & Hardening

**Theme**: Fix runtime memory issues, smoke-test newly unlocked workflows
**Effort**: 0.5-1 session
**Status**: ✅ COMPLETE

| Sub-task | Description | Classification | Status |
|----------|-------------|----------------|--------|
| **9a. Fix proxy `Dispose()` no-op** | Proxy Dispose now calls `SwiftObjectRegistry.Unregister` + `EveryProtocol.Dispose()`. `_disposed` field + `ObjectDisposedException` guards on all member access paths (properties, methods, subscripts, stubs, `GetExistentialContainer`, `MarshalToSwift`). | Generator fix | ✅ |
| **9b. Finalizer leak diagnostics** | `[DebuggerDisplay]` on `SwiftSafeHandle<T>` and `EveryProtocol` showing handle address or `[DISPOSED]`. XML doc `<remarks>` on `Dispose()` documenting leak risk. | Safety | ✅ |
| **9c. Proxy lifecycle tests** | 10 emitter unit tests + 5 Tier 2 runtime tests in TestFramework (container-path: dispose, double-dispose, post-dispose property/method/setter access). Original workflow smoke tests deferred (no third-party library dependency in TestFramework). | Validation | ✅ |

**Acceptance gate**: Proxy Dispose properly cleans up. No new memory leaks from supported workflows.

---

### Session 10A: Targeted Bypass & Gate Fixes

**Theme**: Existential-bypass with default-param reduction + library-specific gate fixes
**Effort**: 1 session
**Status**: ✅ COMPLETE

Sessions 1-9 unlocked the general infrastructure. Session 10A is the "pragmatic endgame" — targeted fixes for remaining critical workflow gaps. Where the exact roadmap workflow can't be delivered by a targeted fix, we deliver the best available alternate pathway and label it honestly.

**Core mechanism** (10a-10b): Extend `ExistentialBypassEmitter` to handle the case where ALL existential-containing params have default values. Emit a **reduced signature** (only non-existential params) with a `@_silgen_name` Swift wrapper that calls the full method/constructor and lets Swift fill in the defaults. Same pattern as `ForeignTypeExtensionEmitter`'s default parameter reduction (Session 6). (10c was investigated but blocked by a different gate — see table.)

| Sub-task | Description | Delivery | Target Library |
|----------|-------------|:--------:|---------------|
| **10a. `ImageRequest(url:)` constructor** | Constructor has 5 params. Only `url: URL?` lacks a default. The other 4 contain existentials with defaults. Emit reduced constructor `ImageRequest(url:)` via `@_silgen_name` wrapper. | **Full** | Nuke |
| **10b. Mixpanel `Track(event:)`** | `track(event:, properties:)` — `properties` is `Optional<Dict<String, any MixpanelType>>` with default `= nil`. Emit reduced `Track(event:)` omitting `properties`. Full `[String: Any]` projection deferred. | **Partial** | Mixpanel |
| **10c. Alamofire `SerializingData()`** | Original callback `responseData {}` blocked by generic closure in param. Alternative: async `serializingData()` returns `DataTask<Data>` — blocked by `HasNonSwiftObjectGenericArg` (Foundation.Data doesn't satisfy ISwiftObject), not existential. Cannot fix with bypass pattern. | **Skip** | Alamofire |
| **10d. KeychainAccess subscript** | Investigate why subscripts are skipped (likely `throws` on getter or type resolution). Fix specific gate. | **Full** | KeychainAccess |
| **10e. Starscream `IWebSocketDelegate`** | Interface recovery — `DidReceive(WebSocketEvent, IWebSocketClient)` in interface, `NotSupportedException` proxy stub. Compile-time unlock (C# can declare `: IWebSocketDelegate`), NOT runtime event delivery. | **Partial** | Starscream |

**Acceptance gate**: Each delivered workflow compiles (10c skipped — not fixable with current patterns). Critical workflow matrix updated. 32/32 validation maintained.

---

### Session 10B: Closure Operators in Protocol Extensions (pending)

**Theme**: Bridge closure TypeSpec params in ProtocolExtensionEmitter's `@_silgen_name` wrappers
**Effort**: 1 session
**Depends on**: Session 7 (generic protocol extension ABI), Session 4 (GenericClosureBridgeEmitter pattern)

| Sub-task | Description | Target Library |
|----------|-------------|---------------|
| **10f. RxSwift `map`, `filter`, `subscribe`** | Remove blanket `ClosureTypeSpec` rejection. Generate `@_cdecl` callback thunks in Swift wrapper for each closure param. Marshal C# delegates → func pointer + context `IntPtr` pairs. Reference: `GenericClosureBridgeEmitter.cs` + spike tests S2a/S2b/S3a/S3b. | RxSwift |

**Acceptance gate**: RxSwift `Observable<T>.Map(...)`, `.Filter(...)`, `.Subscribe(...)` appear in bindings and compile.

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
      └─► Session 6: Protocol Extensions — Foreign    ✅ COMPLETE
           │         (SnapKit snp, SkeletonView)
           │
           └─► Session 7: Protocol Extensions — RxSwift  ✅ PARTIAL
                │         (21 non-closure operators shipped,
                │          closure operators → Session 10B)
                │
                └─► Session 10B: Closure Operators         (pending)
                              (RxSwift map/filter/subscribe)

Session 8: Naming + Polish + Cross-Module             ✅ COMPLETE (independent)
Session 9: Safety & Hardening                         ✅ COMPLETE
Session 10A: Targeted Bypass & Gate Fixes             ✅ COMPLETE
           (Nuke constructor, Mixpanel Track,
            KeychainAccess subscript, Starscream delegate;
            Alamofire SerializingData skipped — not fixable)
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

### After Sessions 10A + 10B (realistic)

| Library | Current | Projected | Delta | Key Session |
|---------|:-------:|:---------:|:-----:|:----------:|
| SmartCardIO | 4.44 | 4.55 | +0.11 | 1, 8 |
| MicroblinkPlatform | 4.33 | 4.45 | +0.12 | 2, 8 |
| Mappedin | 4.30 | 4.45 | +0.15 | 2, 8 |
| Lottie | 4.10 | 4.30 | +0.20 | 3, 8 |
| BlinkIDUX | 3.60 | 3.95 | +0.35 | 2, 5 |
| Nuke | 3.60 | 3.90 | +0.30 | 2, 10A |
| Stripe | 3.55 | 3.95 | +0.40 | 1, 8 |
| BlinkID | 3.55 | 3.80 | +0.25 | 1 |
| KeychainAccess | 3.45 | 3.90 | +0.45 | 1, 8, 10A |
| Starscream | 3.40 | 3.70 | +0.30 | 1, 10A |
| CryptoSwift | 3.22 | 3.60 | +0.38 | 1 |
| Alamofire | 3.10 | 3.65 | +0.55 | 1, 4, 10A |
| Kingfisher | 3.10 | 3.85 | +0.75 | 5 |
| SnapKit | 3.10 | 3.75 | +0.65 | 2, 6 |
| GRDB | 3.00 | 3.65 | +0.65 | 4 |
| SkeletonView | 3.00 | 3.50 | +0.50 | 6 |
| Mixpanel | 2.90 | 3.40 | +0.50 | 3, 10A |
| RxSwift | 2.40 | 3.00 | +0.60 | 7 (partial), 10B |
| **Average** | **3.45** | **~3.81** | **+0.36** | |

**Realistic range**: 3.70–3.90 depending on how well protocol extension projection (Sessions 5-7) lands.

**To reach 4.0+**: Would require additional work beyond this roadmap (ObjC integration for NSObject hierarchy, deeper generic constraint support, full existential dictionary projection). Achievable but not in the usability roadmap scope.

---

## Issues Carried from Completed Work

| Issue | Origin | Addressed In |
|-------|--------|-------------|
| `Method` suffix collision avoidance produces un-idiomatic names | Q1a (Get prefix fix) | ✅ Session 8a |
| `Optional<Primitive/Enum>` in closures still blocked (different ABI) | Q3 (closure relaxation) | Deferred |
| Complex enums in closures still blocked (structural emitter change) | Q3 (closure relaxation) | Deferred |
| Concrete types don't get resolved Self-returning protocol methods | Q4 (Self returns) | ✅ Session 5 |
| Closure interface recovery stubs — dispatch still impossible | Q4b | ✅ Sessions 5-7 |
| SB0004 empty interfaces for genuinely-missing-implementation protocols | Q2 (diagnostics) | ✅ Sessions 5-7 |
| Proxy `Dispose()` no-op — memory leak | New in v2 | ✅ Session 9a |
| ExistentialContainer0 in tuple elements (Lottie edge case) | Pre-existing | Deferred |
| `async throws(ErrorType)` free functions: `_payload`/`this` in static context | Pre-existing, guarded | Low priority |
| Bare `Any` in generic positions → AnyType | Pre-existing | ✅ Session 3 |
| Existential params with defaults block method/constructor emission | Sessions 3, 10A | Session 10A (default-param reduction bypass) |
| Closure TypeSpec rejection in protocol extensions | Session 7 | Session 10B |

---

## What We Deliberately De-Prioritize

| Item | Why |
|------|-----|
| Full RxSwift operator parity (all 76) | 21 non-closure operators shipped (Session 7). Closure operators (map/filter/subscribe) in 10B. Diminishing returns after those. |
| Cross-module unification as a standalone session | Narrow (Stripe only). Bundled into Session 8. |
| Naming polish before workflow unlocks | Scores follow usability. Do naming last. |
| Universal generic closure solution before targeted GRDB fix | General solution may take 2+ sessions. Get one library working first. |
| `[String: Any]` full projection | New marshalling pipeline for existential dictionaries. Blocked Mixpanel `properties:` param. |
| `any Protocol` → `object` parameters | Related to existential dictionary work. |
| Full `responseData(completionHandler:)` | Generic closure bridging in method callbacks. Partially addressed by 10B (protocol extensions only). |

---

## Deferred Structural Work (beyond usability roadmap)

| Item | Blocked Workflow | Effort |
|------|-----------------|:------:|
| `[String: any Protocol]` dict-existential projection | Mixpanel `track(event:, properties:)` full form | Multi-session |
| `any Protocol` → `object` parameters | Various existential method params | Multi-session |
| `Optional<Primitive/Enum>` in closures | Different ABI from pointer-based Optional | Medium |
| Complex enums in closures | Structural emitter change | Medium |
| ExistentialContainer0 in tuples | Lottie edge case | Small |
| Existential marshalling in `[UnmanagedCallersOnly]` callbacks | Starscream runtime event delivery | Large |
| Generic closure params in method callbacks (non-protocol-extension) | Alamofire `responseData {}`, Stripe callbacks | Large |

---

## Phase 2 Quality Work — Completed Reference

For details on what was already done (sessions Q1-Q4), see:
- `Completed/roadmap-completed-feb2026.md` — full session details
- `Completed/binding-review-feb-23.md` — original v1 scores
- `binding-review-v2.md` — post-Phase 2 scores and gap analysis
