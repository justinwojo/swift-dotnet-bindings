# Usability Roadmap — Ergonomic Polish & Existential Sessions

**Period**: February 2026
**Baseline**: v3 binding review avg 3.62 (range: 2.75 RxSwift — 4.56 SmartCardIO)
**Final result**: Projected avg ~3.82 (range 3.75–3.90)
**Scoring references**: `binding-review-v3.md` (baseline)

---

## Summary

6 sessions completed across February 2026. All sessions maintained 32/32 library validation. The roadmap focused on ergonomic polish (naming, type fidelity, `Foundation.Data` projection) and existential container support (returns, dict/collection values, proxy receivers).

**Critical workflow results** (final state):

| Library | Critical Workflow | Status | Session |
|---------|------------------|:------:|:-------:|
| Alamofire | `Session.request(url).responseData { }` | Full | S3 |
| Alamofire | `Session.request(url).serializingData()` | Full | S2 |
| Mixpanel | `Track(event:, properties:)` full form | Full | S5 |
| Starscream | Runtime event delivery via `IWebSocketDelegate` | Generator ✅, Runtime blocked¹ | S6 |

¹ Mono JIT SIGSEGV on proxy through CallConvSwift. NativeAOT device builds expected to work.

---

## Session 1: Ergonomic Polish ✅ COMPLETE

**Theme**: Small naming/noise fixes with broad impact across many libraries
**Effort**: 1 session | **Libraries improved**: 10+

| Sub-task | Description | Status |
|----------|-------------|--------|
| **1a. `nint` → `int` convenience overloads** | New `NativeIntOverloadEmitter` emits pure C# delegation overloads for methods/indexers with `nint`/`nuint` params. Handles generic parent types (Observable), protocol extension methods (unqualified `Int`/`UInt`), generic return types. Two-pass indexer emission ensures primary indexers take precedence. 19 unit tests. | ✅ |
| **1b. `Get` prefix refinement** | Added `parameterCount` parameter to `GetPublicMethodName` — only 0-param noun methods get "Get" prefix. Updated all 14 call sites. `EqualTo(view)` instead of `GetEqualTo(view)`. | ✅ |
| **1c. Async detection false positives** | Extracted `typeAttributes` from ABI JSON. `noescape` closures (builder/DSL pattern) are not treated as completion handlers. `MakeConstraints` no longer gets spurious `Async` variant. | ✅ |
| **1d. `@event` parameter naming** | C# keyword params use `@` verbatim prefix (`@event`, `@string`, `@object`) instead of `_` prefix. `StripVerbatimPrefix` for compound names. `GetBoundGenericBufferName` strips `@` for buffer variables. | ✅ |

**Acceptance gate results**: `Skip(int)` overload on RxSwift Observable ✅. SnapKit `EqualTo(view)` (no `Get`) ✅. No `MakeConstraintsAsync` ✅. Mixpanel `Track(string? @event)` ✅. 32/32 validation ✅.

**Key files**: `NativeIntOverloadEmitter.cs` (new), `NameProvider.cs` (1b, 1d), `SwiftABIParser.cs` (1c), `CompletionHandlerDetector.cs` (1c), `SubscriptHandler.cs` (1a indexers)

---

## Session 2: Foundation.Data Projection ✅ COMPLETE

**Theme**: Project `Foundation.Data` as `byte[]` in public APIs, unblock bound generics
**Effort**: 1 session | **Libraries improved**: Alamofire, Kingfisher, Nuke, Starscream, others with `Data` in APIs

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

---

## Session 3: Closure Bridge Generalization ✅ COMPLETE

**Theme**: Extend closure bridging from protocol extensions to regular method P/Invoke
**Effort**: 1 session | **Libraries improved**: Alamofire, Stripe (StripePayments)
**Depends on**: Session 2 (Foundation.Data needed for `responseData` return type)

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

## Session 4: Existential Container Foundation + Returns ✅ COMPLETE

**Theme**: Investigate existential container layout and apply to the simplest case — existential returns from protocol extensions
**Effort**: 1 session | **Libraries improved**: Infrastructure for all libraries with existential-returning protocol extensions

| Sub-task | Description | Status |
|----------|-------------|--------|
| **4a. Existential container layout analysis** | 3-word payload (24 bytes on 64-bit) + 1 metadata pointer + N witness table pointers. `ExistentialContainer{N}` C# structs (N=0-8) in `Swift.Runtime`. Inline storage for values ≤24 bytes; heap allocation for larger (pointer in Payload0). `ExistentialContainerFactory` creates containers; `ISwiftExistentialConvertible<T>` on proxy classes for bidirectional marshalling. Mono JIT workaround: `swift_getExistentialTypeMetadata` wrapped via cdecl in `libSwiftBindingsRuntime`. | ✅ |
| **4b. Existential return from protocol extensions** | Lifted return gate in `ProtocolExtensionEmitter.TryInjectMethod()`. Added `IsSupportedExistentialReturn()` helper with ObjC filtering guard, proxy class validation, `object`/`AnyType` public-type blocking, and `TypeRecordFlags` checks for associated types, Self requirements, and inherited-requirements-only protocols. Fixed `EmitSwiftWrapper()` and `EmitClosureSwiftWrapper()` to classify existential returns as by-value (not `UnsafeMutableRawPointer`). | ✅ |
| **4c. Codex review hardening (3 rounds)** | Added `AnyType` blocking, `HasAssociatedTypes`/`HasSelfRequirement` flag checks, `InheritedRequirementsOnly` flag (TypeRecordFlags.1<<6), pipeline verification tests. | ✅ |
| **4d. Existential return validation** | 32/32 validation maintained. 26 total unit tests across 2 test files. | ✅ |

**Results**: Gate lifted, Swift wrapper ABI fixed for existential by-value returns. 32/32 validation. Main value is laying groundwork for Sessions 5-6 and enabling protocol extension existential returns as other gates relax.

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

## Session 5: Existential Dictionary/Collection Values ✅ COMPLETE

**Theme**: Marshal existential containers inside generic collections
**Effort**: 1 session | **Libraries improved**: Mixpanel (+1360 lines, 18% increase)
**Depends on**: Session 4 (existential container layout understanding)

| Sub-task | Description | Status |
|----------|-------------|--------|
| **5a. `IsContainerWithSupportedDirectExistential` helper** | Centralized container+existential validation in `BoundGenericsHandler`. Handles `Array<any P>`, `Dictionary<K, any P>`, `Optional<any P>`, and Optional-wrapped containers. Validates existential protocol count (≤8), TypeRecord availability, non-object public type, ObjC filter parity. Rejects existential dict keys (not Hashable). | ✅ |
| **5b. `TranslateTypeSpecToCSharp` fix** | Returns `ExistentialContainer{N}` for fully supported existentials instead of `AnyType`. | ✅ |
| **5c. `IReadOnlyDictionary` invariance fix** | `ExistentialProjection.GetReturnElementConversion` casts proxy to interface type for invariant `V`. | ✅ |
| **5d. Mixpanel validation** | `Track(event:, properties:)`, `TrackWithGroups`, `OptInTracking`, `TrackCharge`, `Initialize`, `SuperProperties` getter, and ~15 other dict-existential methods now emit and compile. +1360 lines (18% increase). | ✅ |
| **5e. Codex review (P1 fix)** | `Dictionary<any K, any V>` edge case: added `!IsExistential(key)` guard. | ✅ |

**Acceptance gate results**: Mixpanel `Track(event:, properties:)` with `IDictionary<string, IMixpanelType>` parameter compiles ✅. `SuperProperties` getter returns `IReadOnlyDictionary<string, IMixpanelType>?` ✅. 32/32 validation ✅.

**Key files**: `BoundGenericsHandler.cs` (helper + TranslateTypeSpecToCSharp), `ExistentialProjection.cs` (invariance cast), `MethodHandler.cs`, `MemberEmissionValidator.cs`, `PropertyHandler.cs`

---

## Session 6: Existential Parameter Marshalling in Protocol Proxy Receivers ✅ COMPLETE

**Theme**: Enable Swift-to-C# delegate dispatch for methods with existential parameters
**Effort**: 1 session | **Libraries improved**: Starscream (correct receiver/vtable/dispatch generated)
**Depends on**: Session 4 (existential container layout understanding)

| Sub-task | Description | Status |
|----------|-------------|--------|
| **6a. ProtocolHandler skip-set split** | `IsInterfaceOnly` methods with only existential params (no closures) are no longer added to `skippedMethodKeys`. Only closure methods are skipped. `existentialSkippedMethodKeys` tracking set removed end-to-end. | ✅ |
| **6b. Swift test protocol** | Added `ExistentialParamDelegate` protocol with `didReceive(value: any HasValue)` method and `fireExistentialDelegate` free function to TestFramework. | ✅ |
| **6c. Wrapper build preservation** | `build-async-wrapper.sh` updated with `PRESERVED_PROTOCOLS` whitelist for `HasValue` and `ExistentialParamDelegate`. | ✅ |
| **6d. Runtime test** | `ExistentialCallbackTests.cs` — Tier 3 (Mono JIT SIGSEGV). Test written and ready for NativeAOT device builds. | ✅ |
| **6e. Starscream verification** | Generated Starscream output verified: receiver + vtable + dispatch all correct. | ✅ |
| **6f. RuntimeTestsApp fixes** | Fixed 49+ pre-existing method name mismatches across 8 test files. | ✅ |
| **6g. Codex review (2 rounds)** | End-to-end `ProtocolHandlerOutputTests` for existential-param receiver/vtable/dispatch. | ✅ |

**Acceptance gate**: Runtime acceptance gate blocked by Mono JIT limitation (proxy through CallConvSwift → SIGSEGV). Generator correctness proven by 9 unit tests + Starscream output verification + 32/32 library validation. NativeAOT device builds expected to work.

**Key files**: `ProtocolHandler.cs` (skip-set split), `ProtocolProxyEmitter.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`, `ProtocolProxyEmitterTests.cs` (7 tests), `ProtocolHandlerOutputTests.cs` (2 tests), `build-async-wrapper.sh`, `ExistentialCallbackTests.cs`

---

## Sequencing & Dependencies

```
Session 1: Ergonomic Polish                     ✅ COMPLETE
Session 2: Foundation.Data Projection           ✅ COMPLETE
Session 3: Closure Bridge Generalization        ✅ COMPLETE (depended on S2)

Session 4: Existential Foundation + Returns     ✅ COMPLETE (foundation for S5, S6)

Session 5: Dict-Existential Values              ✅ COMPLETE (depended on S4)
           (Mixpanel full API)

Session 6: Callback Existential Marshalling     ✅ COMPLETE (depends on S4 layout work)
           (Starscream proxy receivers)
```

## Items Deferred from These Sessions

| Item | Origin | Notes |
|------|--------|-------|
| Multi-closure params per method | Session 3 | No real-world library currently requires it |
| Runtime existential callback delivery | Session 6 | Generator correct; blocked by Mono JIT (NativeAOT expected to work) |
