# Usability Roadmap

**Revised**: February 2026 (post-v3 binding review, reset to future work only)
**Goal**: Push average binding quality score from 3.62 toward 4.0+
**Scoring reference**: `binding-review-v3.md` — 18-library quality review, 10-category scorecards
**Completed work**: `Completed/usability-roadmap-sessions-1-10.md` — Sessions 1–10B (all ✅); Ergonomic Polish Sessions 1–3 (all ✅)

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
| RxSwift | 2.75 | Deprioritized — unlikely .NET iOS use case; existential returns (S4) will help modestly |

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
| ~~Alamofire~~ | ~~`Session.request(url).responseData { }`~~ | ~~Generic closure in callback param~~ | ~~Session 3~~ ✅ |
| ~~Alamofire~~ | ~~`Session.request(url).serializingData()`~~ | ~~`Foundation.Data` not `ISwiftObject`~~ | ~~Session 2~~ ✅ |
| ~~Mixpanel~~ | ~~`Track(event:, properties:)` full form~~ | ~~`[String: any MixpanelType]` dict-existential~~ | ~~Session 5~~ ✅ |
| Starscream | Runtime event delivery via `IWebSocketDelegate` | Existential marshalling in callbacks | Session 6 |

---

## Session Plan

### Session 1: Ergonomic Polish ✅ COMPLETE

**Theme**: Small naming/noise fixes with broad impact across many libraries
**Effort**: 1 session | **Libraries improved**: 10+
**Status**: Complete — all 4 sub-tasks implemented, all acceptance gates met, 32/32 validation, 2 Codex review rounds (5 findings addressed)

| Sub-task | Description | Status |
|----------|-------------|--------|
| **1a. `nint` → `int` convenience overloads** | New `NativeIntOverloadEmitter` emits pure C# delegation overloads for methods/indexers with `nint`/`nuint` params. Handles generic parent types (Observable), protocol extension methods (unqualified `Int`/`UInt`), generic return types. Two-pass indexer emission ensures primary indexers take precedence. 19 unit tests. | ✅ |
| **1b. `Get` prefix refinement** | Added `parameterCount` parameter to `GetPublicMethodName` — only 0-param noun methods get "Get" prefix. Updated all 14 call sites. `EqualTo(view)` instead of `GetEqualTo(view)`. | ✅ |
| **1c. Async detection false positives** | Extracted `typeAttributes` from ABI JSON. `noescape` closures (builder/DSL pattern) are not treated as completion handlers. `MakeConstraints` no longer gets spurious `Async` variant. | ✅ |
| **1d. `@event` parameter naming** | C# keyword params use `@` verbatim prefix (`@event`, `@string`, `@object`) instead of `_` prefix. `StripVerbatimPrefix` for compound names. `GetBoundGenericBufferName` strips `@` for buffer variables. | ✅ |

**Acceptance gate results**: `Skip(int)` overload on RxSwift Observable ✅. SnapKit `EqualTo(view)` (no `Get`) ✅. No `MakeConstraintsAsync` ✅. Mixpanel `Track(string? @event)` ✅. 32/32 validation ✅.

**Key files**: `NativeIntOverloadEmitter.cs` (new), `NameProvider.cs` (1b, 1d), `SwiftABIParser.cs` (1c), `CompletionHandlerDetector.cs` (1c), `SubscriptHandler.cs` (1a indexers)

---

### Session 2: Foundation.Data Projection ✅ COMPLETE

**Theme**: Project `Foundation.Data` as `byte[]` in public APIs, unblock bound generics
**Effort**: 1 session | **Libraries improved**: Alamofire, Kingfisher, Nuke, Starscream, others with `Data` in APIs
**Status**: Complete — all sub-tasks implemented, 3 regressions found and fixed (enum case + async tuple edge cases), 32/32 validation (3 improved: Kingfisher, Nuke, Starscream)

| Sub-task | Description | Status |
|----------|-------------|--------|
| **2a. `Data.FromByteArray()` runtime method** | Added `Data.FromByteArray(byte[])` static factory on existing `Swift.Data` struct, mirroring `FromNSData`. Fixed/unsafe pinning for byte array → native pointer. | ✅ |
| **2b. `DataProjection` class** | New `DataProjection : ITypeProjection` modeled on `StringProjection`. `PublicType="byte[]"`, `PInvokeType="Swift.Data"`, parameter via `FromByteArray()`, return via `ToByteArray()`, `ElementRequiresDisposal=false`. | ✅ |
| **2c. Factory wiring** | `Foundation.Data` early-exit in `TypeProjectionFactory.ProjectNamedType` before `NativeTypeName` triggers `NativeRemappedProjection`. `FoundationDatabase.xml` unchanged (ABI gate still needed). | ✅ |
| **2d. Bound generics unblock** | Early return in `BoundGenericsHandler.IsNonSwiftObjectMappedType` for `Foundation.Data`. Unblocks `DataTask<Data>`, `Array<Data>`, `Optional<Data>`. Alamofire `serializingData()` now emits. | ✅ |
| **2e. Emitter pattern matches** | DataProjection branches in PropertyHandler (3), SubscriptHandler (4), ProtocolProxyEmitter.Receivers (4), EnumHandler.CaseConstruction (3), EnumHandler.CaseInspection (2), EnumHandler.Marshalling (1), WrapperEmitter.Return (2), ClosureHandler (1). Total: 20 locations across 8 files. | ✅ |
| **2f. TypeConversionHandler** | `FromNSData` → `FromByteArray`, `ToNSData` → `ToByteArray` in backup conversion paths. | ✅ |
| **2g. Tests** | Updated 7 test files. Added 10 new DataProjection tests. Existing NativeRemapped tests redirected to URL. | ✅ |

**Acceptance gate results**: Alamofire `serializingData()` emits and compiles ✅. `Foundation.Data` params project as `byte[]` ✅. 32/32 validation ✅. 3 libraries improved (Kingfisher, Nuke, Starscream — enum/tuple Data edge cases now correct).

**Key files**: `DataProjection.cs` (new), `Data.cs` (FromByteArray), `TypeProjectionFactory.cs`, `BoundGenericsHandler.cs`, `EnumHandler.CaseConstruction.cs`, `EnumHandler.CaseInspection.cs`, `EnumHandler.Marshalling.cs`, `WrapperEmitter.Return.cs`

**Note**: This does NOT fix Alamofire's callback-style `responseData {}` — that requires Session 3.

---

### Session 3: Closure Bridge Generalization ✅ COMPLETE

**Theme**: Extend closure bridging from protocol extensions to regular method P/Invoke
**Effort**: 1 session | **Libraries improved**: Alamofire, Stripe (StripePayments)
**Depends on**: Session 2 (Foundation.Data needed for `responseData` return type)
**Status**: Complete — MethodClosureBridge emitter implemented, 2 Codex review rounds (5 findings addressed), 15 unit tests, 32/32 validation (StripePayments improved: fail→ok)

| Sub-task | Description | Status |
|----------|-------------|--------|
| **3a. `MethodClosureBridge` emitter** | New standalone emitter following `ProtocolExtensionClosureBridge` pattern. Emits `@_silgen_name` Swift wrapper + `[UnmanagedCallersOnly]` callback + function pointer + `[LibraryImport]` P/Invoke + public method. Handles bound generic closure args via `withUnsafePointer`/`UnsafeMutableRawPointer`, primitives via typed cdecl params, classes via `Unmanaged.passUnretained`. Generic parent type hoisting to `PInvokeHelperContext.RawCodeBlocks`. | ✅ |
| **3b. Mixed-param support** | Non-closure params with defaults omitted (Swift fills them). Non-closure class params passed through (`.Payload.DangerousGetHandle()` for Swift-native, `.Handle` for ObjC-bridged). Non-closure primitive params passed directly. Static method support (`Self.method()`, no SwiftSelf). DynamicSelf return type resolution. | ✅ |
| **3c. ABI correctness (Codex review)** | Typed cdecl params for primitives (Bool→UInt8/byte, Int→Int/nint). Bool closure args convert `(__p ? 1 : 0)` in Swift wrapper. Bool-return closures convert `cdecl(...) != 0`. ObjC-bridged generic arg rejection (ISwiftObject constraint). | ✅ |
| **3d. Alamofire validation** | `ResponseData`, `ResponseString`, `ResponseJSON`, `Response`, `ResponseURL`, `ResponseDecodable` on both `DataRequest` and `DownloadRequest`. All compile. | ✅ |
| **3e. Stripe validation** | StripePayments `PossibleBrands` static method with `SwiftResult<SwiftSet<STPCardBrand>, Error>` closure recovered. StripePayments regression fixed (fail→ok). | ✅ |

**Acceptance gate results**: Alamofire `ResponseData(completionHandler:)` in bindings ✅. Stripe callback method recovered ✅. 32/32 validation ✅. 15 unit tests ✅.

**Key files**: `MethodClosureBridge.cs` (new), `MemberEmissionValidator.cs` (B20 carve-out), `MethodHandler.cs` (preflight gate + dispatch point), `MethodClosureBridgeTests.cs` (15 tests)

**Deferred**: Multi-closure params per method (no real-world library currently requires it).

---

### Session 4: Existential Container Foundation + Returns ✅ COMPLETE

**Theme**: Investigate existential container layout and apply to the simplest case — existential returns from protocol extensions
**Effort**: 1 session | **Libraries improved**: Infrastructure for all libraries with existential-returning protocol extensions

| Sub-task | Description | Status |
|----------|-------------|--------|
| **4a. Existential container layout analysis** | Documented below. 3-word payload (24 bytes on 64-bit) + 1 metadata pointer + N witness table pointers. `ExistentialContainer{N}` C# structs (N=0-8) in `Swift.Runtime`. Inline storage for values ≤24 bytes; heap allocation for larger (pointer in Payload0). `ExistentialContainerFactory` creates containers; `ISwiftExistentialConvertible<T>` on proxy classes for bidirectional marshalling. Mono JIT workaround: `swift_getExistentialTypeMetadata` wrapped via cdecl in `libSwiftBindingsRuntime`. | ✅ |
| **4b. Existential return from protocol extensions** | Lifted return gate in `ProtocolExtensionEmitter.TryInjectMethod()`. Added `IsSupportedExistentialReturn()` helper with ObjC filtering guard, proxy class validation, `object`/`AnyType` public-type blocking, and `TypeRecordFlags` checks for associated types, Self requirements, and inherited-requirements-only protocols. Fixed `EmitSwiftWrapper()` and `EmitClosureSwiftWrapper()` to classify existential returns as by-value (not `UnsafeMutableRawPointer`). Downstream pipeline (PInvokeEmitter, WrapperEmitter.Return) already handles existential returns — no changes needed. | ✅ |
| **4c. Codex review hardening (3 rounds)** | Round 1: Added `AnyType` blocking for generic protocol existentials (P1), `HasAssociatedTypes`/`HasSelfRequirement` flag checks (P2), closure wrapper path tests (P2). Round 2: Added `InheritedRequirementsOnly` flag (`TypeRecordFlags.1<<6`) computed in `ModuleProcessor.RegisterProtocolType`, serialized/deserialized in module database XML. Blocks protocols with no own instance members but inherited requirements (proxy not emitted). Round 3: Added pipeline verification tests — `ModuleProcessor` compute tests (3), `ModuleDatabaseEmitter` round-trip tests (2) — ensuring flag survives produce→serialize→deserialize. | ✅ |
| **4d. Existential return validation** | 32/32 validation maintained. Existential returns now flow through the EveryProtocol conformance wrappers across Nuke, Kingfisher, SmartCardIO, CryptoSwift, GRDB. Protocol extension methods with existential returns are correctly unblocked, though most real-world cases (RxSwift `subscribe`) are still gated by other constraints (closures, where clauses, throwing) — those gates will relax in future sessions. 26 total unit tests across 2 test files. | ✅ |

**Results**: Gate lifted, Swift wrapper ABI fixed for existential by-value returns. 4420 unit tests (+26 new across 3 rounds of Codex review), 700 integration, 221 runtime. 32/32 validation. Main value is laying groundwork for Sessions 5-6 and enabling protocol extension existential returns as other gates relax.

**Existential container layout** (64-bit):
```
┌─────────────────────────────────────────┐
│ Payload0 (8 bytes)                      │  ← Value buffer (inline for ≤24 bytes)
│ Payload1 (8 bytes)                      │  ← or heap pointer in Payload0 for larger
│ Payload2 (8 bytes)                      │
│ Metadata  (IntPtr)                      │  ← Type metadata pointer
│ WitnessTable0..N (IntPtr each)          │  ← Protocol witness tables (0-8)
└─────────────────────────────────────────┘
C# structs: ExistentialContainer0 (32 bytes) .. ExistentialContainer8 (96 bytes)
```

---

### Session 5: Existential Dictionary/Collection Values ✅ COMPLETE

**Theme**: Marshal existential containers inside generic collections
**Effort**: 1 session | **Libraries improved**: Mixpanel (+1360 lines, 18% increase)
**Depends on**: Session 4 (existential container layout understanding)
**Status**: Complete — B6 gate lifted for Dict<K, any P>, all acceptance gates met, 32/32 validation, 1 Codex review round (1 P1 finding addressed)

| Sub-task | Description | Status |
|----------|-------------|--------|
| **5a. `IsContainerWithSupportedDirectExistential` helper** | Centralized container+existential validation in `BoundGenericsHandler`. Handles `Array<any P>`, `Dictionary<K, any P>`, `Optional<any P>`, and Optional-wrapped containers. Validates existential protocol count (≤8), TypeRecord availability, non-object public type, ObjC filter parity. Rejects existential dict keys (not Hashable). Replaces 5 inline gate implementations (MethodHandler ×2, MemberEmissionValidator ×2, PropertyHandler ×1). | ✅ |
| **5b. `TranslateTypeSpecToCSharp` fix** | Returns `ExistentialContainer{N}` for fully supported existentials (resolvable + non-object) instead of `AnyType`. Enables correct raw ABI types like `SwiftDictionary<SwiftString, ExistentialContainer1>` for property accessors. Unsupported/bare-Any existentials still return `AnyType`. | ✅ |
| **5c. `IReadOnlyDictionary` invariance fix** | `ExistentialProjection.GetReturnElementConversion` casts proxy to interface type: `(IProtocol)new ProtocolProxy(v)`. Required because `IReadOnlyDictionary<K,V>` is invariant in `V` (unlike `IReadOnlyList<T>` which is covariant). Without cast, `AsProjected` lambda produces CS0029. | ✅ |
| **5d. Mixpanel validation** | `Track(event:, properties:)`, `TrackWithGroups`, `OptInTracking`, `TrackCharge`, `Initialize`, `SuperProperties` getter, and ~15 other dict-existential methods now emit and compile. +1360 lines (18% increase). | ✅ |
| **5e. Codex review (P1 fix)** | `Dictionary<any K, any V>` edge case: added `!IsExistential(key)` guard in dictionary branch. Without it, both-existential dicts could slip through despite key not being Hashable. | ✅ |

**Acceptance gate results**: Mixpanel `Track(event:, properties:)` with `IDictionary<string, IMixpanelType>` parameter compiles ✅. `SuperProperties` getter returns `IReadOnlyDictionary<string, IMixpanelType>?` ✅. 32/32 validation ✅. 4432 unit tests (+12 new), 700 integration ✅.

**Key files**: `BoundGenericsHandler.cs` (helper + TranslateTypeSpecToCSharp), `ExistentialProjection.cs` (invariance cast), `MethodHandler.cs` (2 gates simplified), `MemberEmissionValidator.cs` (2 gates simplified), `PropertyHandler.cs` (1 gate simplified)

---

### Session 6: Existential Marshalling in Unmanaged Callbacks

**Theme**: Enable Swift-to-C# delegate dispatch through protocol proxies
**Effort**: 1-2 sessions | **Libraries improved**: Starscream, any library with delegate patterns
**Depends on**: Session 4 (existential container layout understanding)
**Priority**: Medium — deep structural work, primarily benefits Starscream runtime

| Sub-task | Description | Classification |
|----------|-------------|----------------|
| **6a. Existential container unmarshalling in callbacks** | `[UnmanagedCallersOnly]` callback functions currently can't marshal existential containers from Swift arguments to C# types. Build on Session 4's container layout to extract witness tables and reconstruct managed protocol objects. | Design gap |
| **6b. Starscream event delivery** | `IWebSocketDelegate.DidReceive(WebSocketEvent, IWebSocketClient)` actually invoked from Swift when events occur. Currently compile-only. | Validation |

**Projected impact**: Starscream 3.45 → 3.80+ (Protocols +1, Overall +0.5). Avg +0.02-0.03.

**Acceptance gate**: Starscream `IWebSocketDelegate` implementation receives events at runtime on iOS Simulator. 32/32 validation maintained.

---

## Lower-Priority Items (not yet sessionized)

These are real improvements but have lower effort-to-impact ratios than Sessions 4-6. They can be bundled into future sessions or addressed opportunistically.

| Item | Impact | Effort | Notes |
|------|--------|:------:|-------|
| String enum raw values from swiftinterface | GRDB `ResultCode` as enum | Medium | ABI JSON lacks raw values; parse from swiftinterface |
| `Optional<Primitive/Enum>` in closures | Various closure-accepting APIs | Medium | Different ABI from pointer-based Optional |
| Complex enums in closures | Various | Medium | Structural emitter change |
| ExistentialContainer0 in tuples | Lottie edge case | Small | ~22 AnyType locations |
| `async throws(ErrorType)` free functions | Guarded, rare | Small | `_payload`/`this` in static context |
| Method bypass with marshalled passthrough params | Theoretical | Medium | 0 real-world methods currently bypass |
| SCREAMING_CASE naming (Mappedin) | Mappedin polish | Small | `THING_KEY` → `ThingKey` |
| `_object` parameter naming (Mappedin) | Mappedin polish | Small | Already partially fixed in S8b |

---

## Sequencing & Dependencies

```
Session 1: Ergonomic Polish                     ✅ COMPLETE
Session 2: Foundation.Data Projection           ✅ COMPLETE
Session 3: Closure Bridge Generalization        ✅ COMPLETE (depended on S2)

Session 4: Existential Foundation + Returns     ✅ COMPLETE (foundation for S5, S6)

Session 5: Dict-Existential Values              ✅ COMPLETE (depended on S4)
           (Mixpanel full API)

Session 6: Callback Existential Marshalling     (depends on S4 layout work)
           (Starscream runtime events)
```

Sessions 1-5 are complete. Session 6 is the recommended next session — it enables runtime existential delivery in callbacks (Starscream).

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

### After Sessions 1-4 (foundation + existential returns)

| Library | Current | Projected | Delta | Key Session |
|---------|:-------:|:---------:|:-----:|:----------:|
| Alamofire | 3.30 | 3.80 | +0.50 | S2 + S3 |
| SnapKit | 3.40 | 3.60 | +0.20 | S1 |
| RxSwift | 2.75 | 2.95 | +0.20 | S1 + S4 (subscribe recovered) |
| GRDB | 3.20 | 3.40 | +0.20 | S1 + S4 (query builder returns) |
| Mixpanel | 3.25 | 3.35 | +0.10 | S1 |
| SkeletonView | 3.25 | 3.35 | +0.10 | S1 |
| KeychainAccess | 3.65 | 3.75 | +0.10 | S2 |
| Others | — | — | +0.05 | S1 polish |
| **Average** | **3.62** | **~3.75** | **+0.13** | |

### After All 6 Sessions (full roadmap)

| Library | Current | Projected | Delta | Key Session |
|---------|:-------:|:---------:|:-----:|:----------:|
| Alamofire | 3.30 | 3.80 | +0.50 | S2 + S3 |
| Mixpanel | 3.25 | 3.70 | +0.45 | S1 + S5 |
| Starscream | 3.45 | 3.80 | +0.35 | S6 |
| SnapKit | 3.40 | 3.60 | +0.20 | S1 |
| RxSwift | 2.75 | 2.95 | +0.20 | S1 + S4 |
| GRDB | 3.20 | 3.40 | +0.20 | S1 + S4 |
| SkeletonView | 3.25 | 3.35 | +0.10 | S1 |
| KeychainAccess | 3.65 | 3.75 | +0.10 | S2 |
| Others | — | — | +0.05 | S1 polish |
| **Average** | **3.62** | **~3.82** | **+0.20** | |

**Realistic range**: 3.75–3.90.

**To reach 4.0+**: Would require string enum raw values (GRDB), deeper ObjC integration (Lottie IInterpolatable existentials), `Optional<Primitive/Enum>` in closures, and more complete protocol extension coverage. Also, RxSwift-specific features (Map value-type generics, flatMap constrained generics) were deprioritized as unlikely .NET iOS use cases.

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
