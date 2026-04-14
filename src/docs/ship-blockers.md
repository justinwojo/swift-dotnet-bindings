# Ship Blockers — What Needs to Be Fixed

**Date:** 2026-04-13
**SDK Version:** 0.8.0
**Scope:** All libraries and Apple frameworks in swift-dotnet-packages (excluding GRDB)

This document lists every item that prevents a library or framework from reaching **Ship** status. Items are grouped by root cause in the generator (`swift-bindings`), since most blockers are systemic — fixing one root cause resolves items across many libraries simultaneously.

---

## Global SB0001 Summary

SB0001 items crash on NativeAOT (the default for published .NET 9+ apps). Total across all libraries (excluding GRDB): **278 SB0001 items**.

| Root Cause | Count | Fix |
|---|---|---|
| Plain Swift ABI (no wrapper emitted) | ~127 | Fix 1 |
| Existential parameter (`any Protocol`) | ~82 | Fix 11 (new) |
| Generic method (method-level type params) | ~70 | Fix 4 |
| Closure parameter | ~73 | Fix 11 |
| Protocol extension method | ~36 | Fix 11 |
| Method in generic type | ~24 | Fix 6 |
| `decodedObject(fromAPIResponse:)` | 75 | Fix 2 |
| `any Swift.Error` in callbacks | ~10 | Fix 3 |
| Async method (Swift async ABI) | ~10 | Fix 12 |
| Result-builder DSL (unfixable) | 9 | None |

---

## Current Status

| Library/Framework | Current | What's Needed for Ship |
|---|---|---|
| StripeCore | Ship | -- |
| StripeConnect | Ship | -- |
| StripeApplePay | Ship | -- |
| StripeFinancialConnections | Ship | -- |
| StripeIdentity | Ship | -- |
| StripeCardScan | Ship | -- |
| Stripe (umbrella) | Ship | -- |
| StripePaymentsUI | Ship | -- |
| ProximityReader | Ship | -- |
| LiveCommunicationKit | Ship | -- |
| Translation | Ship | -- |
| FamilyControls | Ship | -- |
| WeatherKit | Ship | -- |
| Nuke | Blocked | Fix 1, Fix 11 (10 SB0001: 5 existential + 5 closure) |
| StripePayments | Blocked | Fix 1, Fix 2, Fix 3 |
| StripePaymentSheet | Blocked | Fix 3, Fix 7 |
| StripeIssuing | Blocked | Fix 3 |
| StoreKit2 | Blocked | Fix 1, Fix 4, Fix 5 |
| MusicKit | Blocked | Fix 5 |
| RoomPlan | Blocked | Fix 8, Fix 9 |
| WorkoutKit | Blocked | Fix 8 |
| CryptoKit | Blocked | Fix 4 (all 38 SB0001 are DataProtocol generics) |
| TipKit | Blocked | Fix 4, Fix 6 (9 SB0001 are result-builder DSL — unfixable) |
| ActivityKit | Blocked | Fix 10 |
| Lottie | Blocked | Fix 1, Fix 11, Fix 12 |
| Kingfisher | Blocked | Fix 1, Fix 11, Fix 4 |
| BlinkID | Blocked | Fix 1, Fix 4 |
| BlinkIDUX | Blocked | Fix 1, Fix 13 |
| Mappedin | Blocked | Fix 1, Fix 11 |

---

## Fix 1: SB0001 — Plain Swift ABI Methods (No Wrapper Emitted)

**Items:** ~68 across third-party libraries + ~59 in StripePayments
**Severity:** High — crashes on NativeAOT
**Repo:** `swift-bindings` (generator)

### Problem

Ordinary methods where the generator failed to emit a `@_cdecl` wrapper despite no structural obstacle. Includes:
- `ToString()` overrides → calls `$s...descriptionSSvg` (mangled getter)
- `IsEqual(object?)` → calls `$s...isEqualySbypSgF_optbuf` (optional existential indirect buffer)
- Regular instance methods on ObjC-bridged classes where no wrapper was emitted

### Affected Libraries

| Library | ToString/IsEqual | Other Plain ABI | Total |
|---|---|---|---|
| StripePayments | 59 | 0 | 59 |
| Kingfisher | ~10 | 24 | ~34 |
| BlinkID | 1 | 7 | 8 |
| Lottie | ~5 | 1 | ~6 |
| Mappedin | ~3 | 1 | ~4 |

### Fix

Two sub-categories:

**ToString/IsEqual (ObjC-bridged):** Generate `@_cdecl` wrappers that call the ObjC message-send path (`objc_msgSend` for `description`, `isEqual:`) instead of the Swift mangled symbol. The ObjC selectors are stable ABI and Cdecl-compatible. For `isEqual`, the `any Any?` indirect buffer (`_optbuf`) can be avoided by the wrapper accepting `UnsafeRawPointer?` and performing the cast internally.

**Other plain ABI:** These are methods where the generator should be emitting wrappers but isn't. Likely missed member classes or conformance paths in the wrapper emitter. Highest-ROI fix — no new language features needed, just broader wrapper coverage.

---

## Fix 2: SB0001 — `decodedObject(fromAPIResponse:)` Protocol Conformance

**Items:** 75 (all in StripePayments)
**Severity:** Medium — only affects direct deserialization, not PaymentSheet-driven flows
**Repo:** `swift-bindings` (generator)

### Problem

Every class conforming to `STPAPIResponseDecodable` gets a static `DecodedObject(fromAPIResponse:)` method. The signature is `static func decodedObject(fromAPIResponse: [AnyHashable: Any]?) -> Self?`. The `-> Self?` return and `[AnyHashable: Any]?` dictionary parameter prevent `@_cdecl` wrapper generation.

### Affected Types

~50 types in StripePayments: STPPaymentIntent, STPPaymentMethod (and 30+ sub-types), STPSetupIntent, STPIntentAction (and 12+ sub-types), STPBankAccount, STPCard, STPSource, STPToken, etc.

### Fix

Since these are ObjC-bridged classes, the protocol method is exposed as an ObjC class method `+[STPPaymentIntent decodedObjectFromAPIResponse:]`. Generate a `@_cdecl` wrapper that calls the ObjC message-send path, taking `NSDictionary?` and returning the ObjC class instance.

Alternatively, mark these with a new `[SB0002]` diagnostic ("ObjC fallback available") and generate a C# interop path through ObjC instead of Swift ABI.

---

## Fix 3: SB0001 — `any Swift.Error` Existential in Closures

**Items:** ~10 across Stripe products
**Severity:** High — blocks core async error handling
**Repo:** `swift-bindings` (generator + runtime)

### Problem

When `any Swift.Error` appears as a parameter in a closure type (e.g., `Result<T, any Error>` in completion handlers), the generator cannot produce a `@_cdecl` wrapper because the existential container layout isn't Cdecl-compatible.

### Affected Methods

- `STPCardValidator.PossibleBrands(cardNumber:completion:)` — `Result<Set<STPCardBrand>, any Error>`
- `PaymentSheet.FlowController.Create(paymentIntentClientSecret:...)` — `Result<FlowController, any Error>`
- `STPIssuingCardService.RetrievePin/UpdatePin` — `(STPIssuingCardPin?, STPPinStatus, any Error?)`
- `StripeCustomerAdapter.CreateAsync` (3 overloads) — async initializers with `any Error`

### Fix

The `AnyError` runtime type already has `LocalizedDescription` in 0.8.0. What's missing is the wrapper side: generate a `@_cdecl` wrapper that:
1. Catches the `any Swift.Error` from the Swift closure
2. Extracts the error description via `SBW_AnyError_GetDescription`
3. Passes the result through a Cdecl-compatible callback (error code + description string)

The existing `SBW_MCB_` block callback pattern (used successfully for `PossibleBrands(STPPaymentMethodCardParams)`) should be extended to handle the `any Error` existential case.

---

## Fix 4: SB0001 — Method-Level Generics (`method_level_generics`)

**Items:** 59 skipped + ~38 SB0001 across all libraries
**Severity:** High — blocks CryptoKit hash/HMAC, StoreKit2 ProductsAsync, generic constructors
**Repo:** `swift-bindings` (generator)

### Problem

Methods with their own generic type parameters (e.g., `func hash<D: DataProtocol>(data: D)`, `init<Bytes: ContiguousBytes>(raw: Bytes)`) cannot be directly projected to C# because C# generics don't support protocol-with-associated-types constraints.

SDK 0.8.0 added **concrete specialization** (Session 3 from the audit), which resolves many of these by emitting one overload per known conformer. However, some remain:

- **CryptoKit:** 38 generic constructors (`init<D: DataProtocol>`, `init<Bytes: ContiguousBytes>`) — concrete specialization needs to cover these constructor patterns
- **StoreKit2:** `Product.ProductsAsync<TIdentifiers>` — takes `some Collection<String>`, needs concrete specialization for `[String]` / `Set<String>`
- **TipKit:** 5 method-level generics in Tips.Event and TipOption
- **Kingfisher/BlinkID:** Various generic methods

### Fix

Extend the concrete specialization engine to:
1. Cover generic **constructors** (emit static factory methods, e.g., `FromRawRepresentation(Data data)`)
2. Cover `some Collection<T>` parameters (emit concrete overload for `[T]` / `Array<T>`)
3. Ensure all concrete specializations get proper `@_cdecl` wrappers (not CallConvSwift fallback)

### Key APIs Unblocked

- `SHA256.Hash(Data)`, `SHA384.Hash(Data)`, `SHA512.Hash(Data)`
- `P256.Signing.PublicKey(rawRepresentation: Data)`
- `SymmetricKey(data: Data)`
- `Product.ProductsAsync(["product_id_1", "product_id_2"])`

---

## Fix 5: Emission Skips — Existential Metatype Arrays (`UnsupportedExistential`)

**Items:** ~15 across MusicKit, StoreKit2
**Severity:** High — blocks MusicKit search, StoreKit2 verification
**Repo:** `swift-bindings` (generator)

### Problem

Methods taking `[any Protocol.Type]` (array of protocol metatypes) or returning `any Protocol` existentials with known conformers are skipped entirely.

### Affected Methods

- **MusicKit:** `MusicCatalogSearchRequest.init(term:types:)` — `types: [any MusicCatalogSearchable.Type]`
- **MusicKit:** `MusicCatalogChartsRequest.init(kinds:types:)` — same pattern
- **MusicKit:** `MusicLibrarySearchRequest.init(term:types:)` — same pattern
- **StoreKit2:** `VerificationResult<T>.jwsRepresentation`, `.payloadValue`, `.unsafePayloadValue` — constrained extensions + generic return type

### Fix

For metatype arrays: generate a `@_cdecl` wrapper that accepts a C array of type metadata pointers and constructs the Swift `[any T.Type]` array. Each metatype can be obtained from bound concrete types (Song.self, Album.self, Artist.self, etc.).

For StoreKit2 `VerificationResult`: the constrained-extension specialization engine (Session 1) was supposed to handle this. Verify it landed for `where SignedType == Transaction`, `== AppTransaction`, `== RenewalInfo`. Also extend to cover base-generic properties (`payloadValue` returns `SignedType` which needs closed helpers per concrete specialization).

---

## Fix 6: SB0001 — Async Methods in Generic Types (`GenericTypeCallback`)

**Items:** ~17 across ActivityKit, TipKit, Kingfisher
**Severity:** High — blocks ActivityKit update/end, TipKit Event donations
**Repo:** `swift-bindings` (generator)

### Problem

C# prohibits `[UnmanagedCallersOnly]` inside open generic types. Async methods on generic types (e.g., `Activity<T>.update()`) need a completion callback, but the callback can't live in the generic class.

Session 4 from the audit added **async callback hoisting** — moving callbacks to non-generic helper classes. This resolved the `GenericTypeCallback` skip reason, but exposed the underlying `GenericProtocolConstraint` on `ActivityAttributes` (which has associated type `ContentState`).

### Affected Methods

- **ActivityKit:** `Activity<T>.update()`, `Activity<T>.end()` — now blocked by GenericProtocolConstraint, not GenericTypeCallback
- **TipKit:** `Tips.Event<T>.sendDonation()` — async in generic type
- **Kingfisher:** Various async methods in generic builder types

### Fix

For ActivityKit: `request/update/end` are all blocked by `T: ActivityAttributes` where `ActivityAttributes` has associated type `ContentState`. Since most `ActivityAttributes` conformers are **app-defined**, this requires a user-supplied specialization mechanism — the app provides concrete types and the generator emits bindings for those specific instantiations. This is architectural work beyond a simple fix.

For TipKit/Kingfisher: Verify the callback hoisting fix handles these cases. If the constraint is satisfiable (no associated types), the hoisted callback should work.

---

## Fix 7: SwiftUI Bridge Missing Device Slice

**Items:** 2 xcframeworks (StripePaymentSheet, StripePaymentsUI)
**Severity:** High — blocks all device deployment with SwiftUI views
**Repo:** `swift-bindings` (generator — SwiftUIBridgeEmitter.cs)

### Problem

`StripePaymentSheetBridge.xcframework` and `StripePaymentsUIBridge.xcframework` only contain `ios-arm64-simulator` — no `ios-arm64` device slice. Root-caused to `SwiftUIBridgeEmitter.cs` using unqualified nested type names (e.g., `PaymentButton` instead of `PaymentSheet.PaymentButton`), causing the device-target compilation to fail silently.

### Fix

In `SwiftUIBridgeEmitter.cs`, use fully qualified type names for all nested types referenced in bridge code. Verify device slice is produced by checking the xcframework contains both `ios-arm64` and `ios-arm64-simulator` directories.

---

## Fix 8: Missing Type Database Entries (simd, HealthKit)

**Items:** ~27 across RoomPlan, WorkoutKit
**Severity:** High — blocks transforms, workout constructors
**Repo:** `swift-bindings` (generator type database)

### Problem

Several Apple framework types are not in the generator's type database, causing member-level skips when properties/parameters reference them.

### Missing Types

| Type | Module | ABI | Affects |
|---|---|---|---|
| `simd_float4x4` | simd | frozen struct, 64 bytes | RoomPlan `.Transform` (2 properties) |
| `SIMD3<Float>` | simd | frozen struct, 12 bytes | RoomPlan `.PolygonCorners` |
| `HKWorkoutActivityType` | HealthKit | enum, Int raw value | WorkoutKit constructors (9+ skips) |
| `HKWorkoutSessionLocationType` | HealthKit | enum, Int raw value | WorkoutKit constructors |

### Fix

Session 1 from the Apple audit was supposed to add these. Verify `HealthKitDatabase.xml`, `SimdDatabase.xml` are created and registered. For simd types, validate ABI layout (64-byte struct passed by value through P/Invoke requires correct `[StructLayout]` or VWT-backed marshalling).

---

## Fix 9: Existential with Known Conformers (`UnsupportedExistential`)

**Items:** ~4 in RoomPlan
**Severity:** Medium — blocks CapturedRoom.Object.attributes
**Repo:** `swift-bindings` (generator)

### Problem

`CapturedRoom.Object.attributes` returns `any CapturedRoomAttribute`. There are exactly 4 conformers: `ChairType`, `SofaType`, `TableType`, `StorageType` — all fully bound. The generator can emit a discriminated union / try-cast pattern.

Session 5 from the audit was supposed to add existential projection for known-conformer protocols.

### Fix

Verify the existential union emitter handles this case. The runtime needs to inspect type metadata from the existential container and cast to the correct concrete type.

---

## Fix 10: Generic Protocol Constraints with App-Defined Conformers

**Items:** ~10 in ActivityKit
**Severity:** Architectural — no simple fix
**Repo:** `swift-bindings` (generator — future work)

### Problem

`Activity<T>` where `T: ActivityAttributes` — the constraint protocol has associated type `ContentState`, and most conformers are defined by the consuming app, not the framework. The generator can't discover conformers at binding time, and Swift `@_cdecl` wrappers need compile-time concrete types.

### Affected Methods

- `Activity.request(attributes:content:pushType:)` — 4 overloads
- `Activity.update(content:)` — 3 overloads  
- `Activity.end(content:dismissalPolicy:)` — 3 overloads
- 13 properties on `Activity<T>`

### Fix Path

Requires a **user-supplied specialization mechanism**: the app provides a Swift file instantiating `Activity<MyAttributes>` and the generator binds the concrete type. This is significant architectural work.

**Interim:** Ship ActivityKit as Preview with documented limitation. The authorization, error, and configuration APIs are fully functional.

---

## Fix 11: Closure Parameter Wrapping (`closure_params`)

**Items:** 63 across all libraries
**Severity:** Medium-High — blocks Mappedin (41 items), Kingfisher (12), Lottie (5)
**Repo:** `swift-bindings` (generator)

### Problem

Methods taking Swift closures as parameters that can't be made `@_cdecl`-compatible. The specific closure signatures that fail include:
- Closures with existential parameters
- Closures with optional return types
- Closures with multiple generic parameters

### Fix

Extend the `SBW_MCB_` (managed callback block) mechanism to handle more closure parameter signatures. For each unsupported closure type, generate a bridge that:
1. Creates a `@_cdecl`-compatible callback
2. Marshals closure parameters through Cdecl-safe types
3. Routes the callback to the C# delegate

---

## Fix 12: Async Method Wrapping (`async_method`)

**Items:** 10 across Lottie, Kingfisher, BlinkIDUX
**Severity:** Medium — blocks some async APIs
**Repo:** `swift-bindings` (generator)

### Problem

Some async methods can't get `@_cdecl` wrappers. The existing async pipeline handles many cases (WeatherKit's 7 overloads work), but some patterns still fail:
- Async methods with metatype properties
- Async methods in actor-isolated contexts

### Fix

Audit remaining async failures case-by-case. The infrastructure (async callback hoisting, tuple return threading) exists from Sessions 4-5; these may be edge cases in the existing pipeline.

---

## Fix 13: Actor Type Support

**Items:** 9 actor types + 7 actor-isolated members + 5 actor properties across BlinkID/BlinkIDUX
**Severity:** Medium — blocks some scanning APIs
**Repo:** `swift-bindings` (generator)

### Problem

Swift `actor` types and `@MainActor`-isolated members are not yet supported by the generator. Actor types use the actor executor for method dispatch, which is incompatible with `@_cdecl`.

### Affected Types

- BlinkID: `BlinkIDRecognizer` (actor), various `@MainActor` isolated properties
- BlinkIDUX: `ScanningViewModel` (actor), `CameraFrameAnalyzer` (actor-isolated methods)

### Fix

For `@MainActor` members: dispatch through `DispatchQueue.main.async` in the `@_cdecl` wrapper.
For actor types: generate wrappers that use `nonisolated` entry points or bridge through actor executors.

---

## Priority Order

Based on impact (how many libraries unblocked) and difficulty:

| Priority | Fix | Items | Libraries Unblocked | Difficulty |
|---|---|---|---|---|
| 1 | Fix 1: ToString/IsEqual wrappers | ~120 | All with SB0001 | Low-Medium |
| 2 | Fix 4: Method-level generics specialization | ~97 | CryptoKit, StoreKit2, TipKit, Kingfisher, BlinkID | Medium |
| 3 | Fix 3: `any Swift.Error` in closures | ~10 | StripePayments, StripePaymentSheet, StripeIssuing | Medium |
| 4 | Fix 7: Bridge device slice | 2 | StripePaymentSheet, StripePaymentsUI | Low |
| 5 | Fix 8: Type database (simd, HealthKit) | ~27 | RoomPlan, WorkoutKit | Low |
| 6 | Fix 11: Closure params | 63 | Mappedin, Kingfisher, Lottie | Medium-High |
| 7 | Fix 2: decodedObject wrappers | 75 | StripePayments | Medium |
| 8 | Fix 5: Existential metatype arrays | ~15 | MusicKit, StoreKit2 | High |
| 9 | Fix 9: Known-conformer existentials | ~4 | RoomPlan | Medium |
| 10 | Fix 12: Remaining async methods | 10 | Lottie, Kingfisher, BlinkIDUX | Medium |
| 11 | Fix 13: Actor types | 21 | BlinkID, BlinkIDUX | High |
| 12 | Fix 6: GenericTypeCallback (remaining) | ~17 | TipKit, Kingfisher | Medium |
| 13 | Fix 10: App-defined conformers | ~10 | ActivityKit | Very High |

### What Gets Us to All-Ship

Fixes 1-5 and 7-8 would bring us to Ship for: **StripePayments, StripePaymentSheet, StripeIssuing, CryptoKit, StoreKit2, MusicKit, RoomPlan, WorkoutKit** — that's 8 of the 15 blocked libraries.

Adding Fixes 6, 11-12 would Ship: **Lottie, Kingfisher, TipKit, BlinkID, BlinkIDUX** — 5 more.

Fix 10 (ActivityKit app-defined conformers) and Fix 13 (actors) are architectural and can remain Preview for now.

Mappedin is proprietary/manual-mode and may not be appropriate for public NuGet regardless.

---

## Session Plan

**Strategy:** Batch all code changes per session, run validation gates once at the end. A mid-session `nuke test` (~2 min) is the only intermediate check. Each session ends with **all three gates green**: `nuke test`, `nuke validate`, `nuke binding-tests` (or `nuke runtime-tests-simulator`). Zero-regression policy applies — baseline numbers must not drop.

### Session 1 — ObjC Wrappers + Quick Wins ✅ COMPLETE (commit 1f31a421)

**Fixes:** 1, 2, 3, 7, 8
**Items resolved:** ~240
**Libraries unblocked:** StripePayments, StripePaymentSheet, StripeIssuing, RoomPlan (partial), WorkoutKit, Nuke (partial)

These fixes share a common theme: expanding wrapper coverage using known techniques. Fixes 7 and 8 are sub-hour work. Fixes 1 and 2 share the ObjC message-send wrapper technique. Fix 3 extends the existing MCB callback mechanism.

#### Pre-Session Research

**Fix 7 — Bridge device slice:**
- Bug is in `SwiftUIBridgeEmitter.InitAnalyzer.cs`, method `MapDatabaseType()` at lines 438–440, 459, 480, 507–508
- `IndexOf('.')` strips first dot (module prefix), but when `namedSpec.Name` arrives without module prefix, it produces unqualified names like `PaymentButton` instead of `PaymentSheet.PaymentButton`
- Same pattern in 4 locations: BoundEnum (440), BoundStruct (459), Class (480), Struct (507)
- `BridgeTypeName` is used in `SwiftUIBridgeEmitter.cs` at lines 802, 808, 1214, 1218, 1222, 1234, 1239, 1246
- Test file: `SwiftUIBridgeEmitterTests.cs` — no nested type test exists yet

**Fix 8 — Type database entries:**
- **Databases already exist:** `SimdDatabase.xml` and `HealthKitDatabase.xml` in `src/Swift.Runtime/src/Swift/`
- `SimdDatabase.xml` has `simd_float4x4` and `simd_float3` — but NOT `SIMD3<Float>` (Swift stdlib generic, different from C `simd_float3`)
- `HealthKitDatabase.xml` has both `HKWorkoutActivityType` and `HKWorkoutSessionLocationType`
- **Tests already exist:** `TypeDatabaseTests.cs` lines 634–669 verify both databases
- **Real gap:** `SIMD3<Float>` is a bound generic in `Swift` module, not a plain named type in `simd` module. Needs entry in a Swift stdlib database.
- Registration: `TypeDatabase.cs` `LoadModuleDatabaseFromFile()` lines 59–76, loaded in `Program.cs` lines 108/156

**Fix 1 — Plain Swift ABI wrappers:**
- Central gate: `MethodWrapperEmitter.cs` `ShouldEmitWrapper()` lines 27–115
- `description` blocked at line 36 (`IsAccessor`) — routes to `PropertyWrapperEmitter`, not `MethodWrapperEmitter`
- `isEqual(_:)` blocked by `HasUnsupportedGenericContainerParamsOrReturn()` — `Any?` is `Optional<existential>`, blocked at line 126 via `IsUnsupportedGenericContainer`
- **No `objc_msgSend` support exists anywhere in the generator.** ObjC pipeline is separate. This is new code.
- Wrapper symbol format: `SBW_{moduleName}_{safeTypeName}_{methodName}_{hash}` (line 163–167)
- "Other plain ABI" methods: need to audit which methods are silently dropped by `MemberValidationPipeline`/`MemberEmissionValidator` before reaching `ShouldEmitWrapper()`

**Fix 2 — `decodedObject(fromAPIResponse:)` wrappers:**
- Blocked by two gates: (1) `[AnyHashable: Any]?` → `HasUnsupportedGenericContainerParamsOrReturn()` line 1248, (2) `Self?` return → `DynamicSelf` via optional
- No special handling for `decodedObject`/`fromAPIResponse` exists
- No protocol conformance detection mechanism exists
- Fix requires new ObjC class method wrapper path (same as Fix 1's objc_msgSend infrastructure)

**Fix 3 — `any Swift.Error` in closures:**
- Layer 1 gate: `ClosureHandler.IsSupportedClosureParameterType()` line 445 — existentials checked by `IsSupportedExistential()`
- Layer 2 gate: `ClosureEmitter.SwiftWrapper.IsCdeclCompatibleType()` line 529 — has NO path for existentials, falls through to `return false`
- MCB = `MethodClosureBridge` (`MethodClosureBridge.cs`), naming: `MCB_{mangledHash}` (line 152)
- `MethodClosureBridge.IsEligible()` line 38 — requires closure with bound generic or complex enum arg, does NOT support `any Swift.Error`
- `SBW_AnyError_GetDescription` defined in `AnyError.cs` line 107 (C# P/Invoke) and `SwiftBindingsRuntime.swift` (Swift side)
- `AnyErrorCallbackFixture.swift` has manual `@_cdecl` helpers for testing `AnyError.LocalizedDescription` — no closure callback path yet

#### Work Order

1. **Fix 7 — Bridge device slice** (~15 min)
   - File: `SwiftUIBridgeEmitter.cs` — qualify nested type names (e.g., `PaymentSheet.PaymentButton` not `PaymentButton`)
   - Verify: both `ios-arm64` and `ios-arm64-simulator` slices in output xcframework
   - Tests: existing `SwiftUIBridgeEmitterTests.cs` — add case for nested type qualification

2. **Fix 8 — Type database entries** (~30 min)
   - Add `simd_float4x4` (64-byte frozen struct), `SIMD3<Float>` (12-byte frozen struct) to simd database
   - Add `HKWorkoutActivityType`, `HKWorkoutSessionLocationType` (Int-backed enums) to HealthKit database
   - Validate ABI layout with `[StructLayout]` for simd types
   - Tests: `TypeDatabaseTests.cs` or `AppleFrameworkRegistryTests.cs` — add lookup assertions for new types

3. **Fix 1 — Plain Swift ABI wrappers** (bulk of session)
   - **ToString/IsEqual:** Generate `@_cdecl` wrappers calling `objc_msgSend` for `description`/`isEqual:` on ObjC-bridged classes. The `isEqual:` wrapper accepts `UnsafeRawPointer?` to avoid the `_optbuf` indirect existential.
   - **Other plain ABI:** Identify missed member classes in wrapper emitter — these are ordinary methods where no structural obstacle prevents wrapper generation.
   - Tests:
     - Unit: `MethodWrapperEmitterTests.cs` — assert ToString/IsEqual on ObjC-bridged class emits `@_cdecl` with `objc_msgSend` call
     - BindingTests Swift: add `NSObject` subclass with `description` and `isEqual` overrides to `ObjCInterop/NSObjectSubclass.swift`
     - Runtime: add `ObjCInteropTests.cs` cases verifying `ToString()` returns correct string, `Equals()` works correctly

4. **Fix 2 — `decodedObject(fromAPIResponse:)` wrappers** (~45 min)
   - Same ObjC message-send technique: `+[STPPaymentIntent decodedObjectFromAPIResponse:]` takes `NSDictionary?`
   - Generate `@_cdecl` wrapper calling ObjC class method path
   - Tests: unit test asserting wrapper emission for `Self?`-returning ObjC class methods with dictionary params. Validate coverage via `nuke validate` (StripePayments target).

5. **Fix 3 — `any Swift.Error` in closures** (~45 min)
   - Extend `SBW_MCB_` callback mechanism: wrapper catches `any Swift.Error`, extracts description via `SBW_AnyError_GetDescription`, passes through Cdecl-compatible callback (error code + string)
   - Tests:
     - Unit: `ClosureCdeclEmitterTests.cs` — assert MCB emission for `Result<T, any Error>` closure param
     - BindingTests Swift: add completion handler taking `Result<String, any Error>` to `ErrorHandling/AnyErrorCallbackFixture.swift`
     - Runtime: `AnyErrorDescriptionTests.cs` — verify error description round-trips through callback

6. **Mid-session sanity:** `nuke test` (~2 min)

7. **End-of-session gates:**
   - `nuke test` — all unit tests pass
   - `nuke validate` — baseline pass counts equal or better
   - `nuke binding-tests` — full pipeline (xcframework + bindings + bridge + runtime)

---

### Session 2 — Generics + Existentials ✅ PARTIAL (commit f2e6cf2e — Fix 4 + Fix 9 done; Fix 5 deferred to Session 4)

**Fixes:** 4, 5, 9 (Fix 4 + Fix 9 ✅ complete; Fix 5 deferred to follow-up)
**Items resolved:** ~116
**Libraries unblocked:** CryptoKit, StoreKit2, MusicKit, RoomPlan (complete), TipKit (partial), Kingfisher (partial), BlinkID (partial)

All three fixes operate in the specialization/existential area of the generator. Fix 4 extends the concrete specialization engine. Fix 5 adds metatype array bridging. Fix 9 adds discriminated union projection for known-conformer existentials.

#### Pre-Session Research

**Fix 4 — Concrete specialization engine:**
- Engine: `src/Marshaler/ConcreteSpecializationEngine.cs`
  - `IndexModuleConformances(ModuleDecl)` line 97 — call once per module
  - `FindSpecializableMethods(TypeDecl)` line 178 — iterates `typeDecl.Methods` (includes constructors), identifies method-own generic params, calls `FindSpecializableProtocolConstraint()`
  - `GetConformers(SwiftTypeName)` line 149 — combines hint-based + ABI-based conformers
- Emitter: `src/Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.cs`
  - `EmitConcreteSpecializations` line 29 — skip condition at line 56: `IsAsync || Throws || IsAccessor || IsMutating` — **constructors NOT skipped**
  - `TryEmitConcreteOverload` line 75 — `isConstructor` detected at line 89, routes to struct init (line 461) or class retained (line 456)
  - Symbol format: `SBW_CSM_{module}_{type}_{conformer}_{method}_{hash}` (line 109)
- **Constructors already supported in the emitter.** The engine finds them via `typeDecl.Methods`. Gap is likely in `MemberValidationPipeline` blocking constructors with `HasUnsupportedProtocolConstraints` before specialization runs.
- Hints: `src/Data/specialization-hints.json` — has `Swift.DataProtocol` (Data, [UInt8]), `Foundation.ContiguousBytes` (Data, [UInt8]), `Swift.Sequence` ([UInt8], Data), `SwiftBindingsTestLib.AttributeKind` (3), `RoomPlan.CapturedRoomAttribute` (4). **No `Swift.Collection` entry.**
- `some Collection` gap: parser synthesizes opaque param at `SwiftABIParser.cs:2448-2452` → constraint to `Swift.Collection` (PAT) → `GetConformers()` returns empty → no specialization. **Fix: add `Swift.Collection` to hints.**
- Tests: `ConcreteSpecializationEngineTests.cs` — existing: `LoadedHints_ContainsDataProtocol` (19), `GetConformers_*` (35, 126, 140), `FindSpecializableMethods_*` (88). Helper: `CreateStructWithProtocolConstrainedMethod` (296).
- Fixtures: `Generics/MethodLevelGenerics.swift` has `GenericMethodHost` class with `printDescription<T: Describable>` etc. No generic constructor or `some Collection` fixture exists.
- Runtime tests: `MethodLevelGenericTests.cs` — 4 tests, one `[Skip]`-ped.

**Fix 5 — Existential metatype arrays:**
- Skip source: `MemberEmissionValidator.cs` lines 530–537 — `TryGetFirstExistentialTypeArgument` finds `any Protocol.Type` inside `Swift.Array<any Protocol.Type>` → `IsContainerWithSupportedDirectExistential` returns false → `SkipReason.UnsupportedExistential`
- `WrapperValidation.IsMetatypeType()` line 367 — only checks top-level `TypeSpec`, does NOT catch array-of-metatype
- **No existing array-of-metatypes handling anywhere.** `MetatypeHelperEmitter` is unrelated (handles generic parent type metadata accessors).
- Proposed Swift wrapper: receive `(UnsafeRawPointer, Int)` pair, reconstruct `[any Protocol.Type]` via loop + `unsafeBitCast(ptr, to: Any.Type.self)`
- C# side: pass `IntPtr` + `int` count
- Fixtures: `Generics/Metatypes.swift` has `typeName<T>(of type: T.Type)`, `createInstance<T>()`, `TypeFactory<T>`. No `[any Protocol.Type]` fixture.

**Fix 9 — Known-conformer existentials (ExistentialUnion):**
- `ExistentialHandler.GetPublicExistentialType` already returns `"Swift.Runtime.ExistentialUnion"` at line 486 when protocol is PAT AND `SpecializationEngine.GetConformers()` returns >0
- `TypeProjectionFactory.cs` line 443–448 skips proxy creation for ExistentialUnion
- **The gap is two missing branches in `WrapperEmitter.Return.cs`:**
  - Cdecl path (~line 559): no case for `publicType == "Swift.Runtime.ExistentialUnion"` — falls through to proxy creation (crash)
  - Non-cdecl path (~line 596): same gap — falls through to `GetQualifiedProxyClassName` (wrong)
- `ExistentialUnionTests.cs` (RuntimeTestsApp) — **tests already written but class-level `[Skip]` at line 14.** Ready to un-skip.
- PAT fixtures already in `Generics/Existentials.swift`: `AttributeKind` protocol (line 86) with `ColorAttribute`, `SizeAttribute`, `FlagAttribute` conformers (93–125), `AttributeHolder` struct (131) with `attribute: any AttributeKind` property (146)
- Hints: `specialization-hints.json` lines 34–41 has `RoomPlan.CapturedRoomAttribute` → 4 conformers (ChairType, SofaType, TableType, StorageType)

#### Work Order

1. **Fix 4 — Method-level generics: constructors + `some Collection`** (bulk of session)
   - Extend concrete specialization engine to emit:
     - Generic **constructors** as static factory methods (e.g., `SHA256.Hash(Data)` from `init<D: DataProtocol>(data: D)`)
     - `some Collection<T>` params as concrete `Array<T>` overloads
   - Ensure all specializations get `@_cdecl` wrappers (not `CallConvSwift` fallback)
   - Tests:
     - Unit: `ConcreteSpecializationEngineTests.cs` — add cases for generic constructors, `some Collection` params
     - BindingTests Swift: extend `Generics/MethodLevelGenerics.swift` — add `init<D: DataProtocol>` style constructor, `func process<C: Collection>(items: C) where C.Element == String`
     - Runtime: `MethodLevelGenericTests.cs` — verify concrete specialization constructors work, collection params marshal correctly

2. **Fix 5 — Existential metatype arrays** (~1.5 hrs)
   - Generate `@_cdecl` wrapper accepting C array of type metadata pointers, constructing `[any T.Type]` in Swift
   - Each metatype obtained from bound concrete types (`Song.self`, `Album.self`, etc.)
   - Tests:
     - Unit: new test cases in `MetatypeHelperEmitterTests.cs` — assert metatype array wrapper emission
     - BindingTests Swift: add to `Generics/Metatypes.swift` — function taking `[any SomeProtocol.Type]` with known conformers
     - Runtime: add metatype array passing test in `Generics/BasicGenericTests.cs`

3. **Fix 9 — Known-conformer existentials** (~1 hr)
   - Emit discriminated union / try-cast pattern: inspect type metadata from existential container, cast to correct concrete type
   - Tests:
     - Unit: assert existential union emission for protocol with 3-4 known conformers
     - BindingTests Swift: add protocol with known conformers + property returning `any Protocol` to `Protocols/ExistentialReturns.swift`
     - Runtime: `ExistentialUnionTests.cs` — verify try-cast returns correct concrete type

4. **Mid-session sanity:** `nuke test`

5. **End-of-session gates:**
   - `nuke test` — all unit tests pass
   - `nuke validate` — CryptoKit, StoreKit2, MusicKit, RoomPlan targets all improve
   - `nuke binding-tests` — full pipeline green
   - `nuke runtime-tests-simulator` — new runtime tests pass

---

### Session 3 — Closures + Async + Actors ✅ PARTIAL (commit f593a924 — Fix 13 done; Fix 11/12/6 deferred to Session 4)

**Fixes:** 11, 12, 6, 13
**Items resolved:** ~111
**Libraries unblocked:** Lottie, Kingfisher, Nuke (complete), Mappedin, BlinkID (complete), BlinkIDUX, TipKit (complete minus 9 unfixable DSL items)

This is the heaviest session — Fix 11 has multiple sub-patterns and Fix 13 is a new generator feature. If this session is too large, split Fix 13 into a separate Session 4.

#### Pre-Session Research

**Fix 11 — Closure parameter wrapping (two-layer gate):**
- Layer 1: `ClosureHandler.IsSupportedClosureParameterType()` at `ClosureHandler.cs:445` — allows existentials if `IsSupportedExistential()` passes
- Layer 2: `ClosureEmitter.SwiftWrapper.IsCdeclCompatibleType()` at `ClosureEmitter.SwiftWrapper.cs:529` — **no existential path** (falls through to `return false`). Only handles: empty tuple, Bool, primitives, pointers, classes, ObjC-bridged, simple enums, frozen structs, complex enums, some Optional<T>
- `closure_params` skip applied at `WrapperValidation.GetMethodWrapperRejectionReason` lines 873–880 and `MemberValidationPipeline.GetConstructorWrapperRejectionReason` lines 363–368
- `NeedsClosureCdeclWrapper` at `ClosureEmitter.SwiftWrapper.cs:974-1026` — returns false if no thunk closures pass `IsClosureCdeclCompatible` (which calls `IsCdeclCompatibleType`)
- **Sub-pattern A (existential params):** e.g., `(any ImageDecoding) -> Void`. Layer 1 allows it, Layer 2 blocks it. Note: `ClosureEmitter.StructParams.cs` lines 105–112 already handle existentials in the non-cdecl path — need to wire into cdecl emitter.
- **Sub-pattern B (optional returns):** e.g., `() -> SomeStruct?`. `IsCdeclCompatibleType` lines 569–587 only allow `Optional<Class/ObjC/Numeric/Bool/SimpleEnum>`. Optional frozen structs fall through to `return false`.
- **Sub-pattern C (multi-closure):** `NeedsClosureCdeclWrapper` uses `.All(...)` at line 1022 — ALL closures must be cdecl-compatible. Fix: widen per-closure `IsCdeclCompatibleType`, not structural change.
- Tests: `ClosureCdeclEmitterTests.cs` — tests `NeedsClosureCdeclWrapper` but NOT existential params, optional returns, or multi-closure compat
- Fixtures: `BindingTests/Sources/SwiftBindingsTestLib/Closures/` — 9 files covering autoclosures, escaping, throwing, struct bridge, generic bridge. No existential-param closure fixture.

**Fix 12 — Remaining async methods:**
- Async pipeline: `WrapperEmitter.Async.cs` `EmitAsync()` at line 64
- `async_method` skip at `WrapperValidation.cs:871` — after actor check at lines 862–867
- **Most of the 10 remaining items are actually blocked by Fix 13's actor gate first.** Actor types hit `"actor_type"` at line 862 before reaching `"async_method"`. Fixing actors may auto-resolve several async items.
- Metatype async properties hit `AsyncProperty` SkipReason in `PropertyHandler` before wrapper emission
- Tests: `AsyncSwiftWrapperTests.cs` — no actor-isolated async tests

**Fix 6 — GenericTypeCallback (tractable parts):**
- Gate: `MemberValidationPipeline.cs` lines 96–153 (Phase 3)
  - Line 111: constructor with thunk closure in generic type
  - Line 138: async callback referencing parent generic params in return type
  - Line 149: method needing `[UnmanagedCallersOnly]` in generic type
- Exception at lines 128–151: `MethodClosureBridge.IsEligible()` and `NestedClosureBridge.IsEligible()` allow hoisting
- **Tractable (TipKit, Kingfisher):** callback hoisting via `MethodClosureBridge.IsEligible()` at `MethodClosureBridge.cs:38-118`. If these methods aren't let through, diagnose each eligibility condition.
- **Intractable (ActivityKit):** return type references parent `T` → `TypeSpecHelpers.ContainsAnyTypeName(returnTypeSpec, parentParamNames)` returns true at lines 131–141 → fundamental limitation

**Fix 13 — Actor types (more infrastructure than expected):**
- Model: `ClassDecl.IsActor` at `ClassDecl.cs:21`. Actors are `ClassDecl` with `IsActor = true`.
- Detection: `SwiftABIParser.cs:1321` — detected by `$sScA` conformance. `IsActor` set at line 1336.
- Isolation parsing: `SwiftInterfaceAccessParser.cs` — `ActorDeclRegex` (70), `GetMainActorTypes()` (221), `GetCustomActorTypes()` (299), `GetActorIsolatedMembers()` (384). Fields: `IsActorIsolated`, `IsMainActorIsolated`, `IsNonisolated` on both `MethodDecl` and `PropertyDecl`.
- **`@MainActor` classes already work!** `MainActorTests.cs` tests `MainActorViewModel`, `MainActorMethods`, `MainActorService` — all passing.
- Blocking gates (all check `parentDecl is ClassDecl { IsActor: true }`):
  - `WrapperValidation.IsActorIsolatedMember` lines 239–253
  - `WrapperValidation.GetMethodWrapperRejectionReason` line 862 → `"actor_type"`
  - `WrapperValidation.CanEmitMemberWrapper` line 618
  - `PropertyHandler.cs` line 113 → `ActorIsolatedAsyncStream`
  - `SubscriptWrapperEmitter.cs` line 144 → `"actor_type_subscript"`
- **Quick win: `nonisolated` members.** Currently blocked because gate checks `IsActor` on parent, not `IsNonisolated` on member. `MethodDecl.IsNonisolated` is already parsed (line 1648). Fix: narrow gate to skip only non-`nonisolated` actor members.
- **Full actor support (isolated members):** route through async pipeline (actor methods can only be safely called via `await`). Requires async C# wrappers through Swift actor executor.
- Fixtures: `Async/Actors.swift` has `Counter` actor (increment/decrement/getCount/add + `nonisolated description()` and `typeName`), `AsyncProcessor` actor. No runtime tests for actor types yet — only `@MainActor` classes.

#### Work Order

1. **Fix 11 — Closure parameter wrapping** (bulk of session)
   - Extend `SBW_MCB_` mechanism for three sub-patterns:
     - Closures with existential parameters (bridge through type-erased pointer)
     - Closures with optional return types (bridge through nullable pointer + flag)
     - Closures with multiple generic parameters (emit concrete overloads)
   - Tests:
     - Unit: `ClosureCdeclEmitterTests.cs` — add cases for each sub-pattern
     - BindingTests Swift: extend `Closures/` fixtures — add closures with `any Protocol` param, `Optional` return, multi-generic params
     - Runtime: `ClosureTests.cs` — verify all three sub-patterns invoke correctly

2. **Fix 12 — Remaining async methods** (~1 hr)
   - Audit remaining failures case-by-case (metatype properties, actor-isolated contexts)
   - Infrastructure exists from async callback hoisting — these are edge cases
   - Tests:
     - Unit: case-specific tests in `AsyncSwiftWrapperTests.cs`
     - Runtime: extend `AsyncMethodTests.cs` with edge-case async invocations

3. **Fix 6 — GenericTypeCallback (tractable parts)** (~1 hr)
   - TipKit `Tips.Event<T>.sendDonation()` — verify callback hoisting handles this (no associated-type constraint)
   - Kingfisher generic builder async methods — same verification
   - Skip ActivityKit (overlaps Fix 10, app-defined conformers)
   - Tests: extend `AsyncMCBCallbackTests.cs` if new patterns are needed

4. **Fix 13 — Actor types** (~2 hrs)
   - `@MainActor` members: `@_cdecl` wrapper dispatches through `DispatchQueue.main.async`
   - Actor types: generate `nonisolated` entry points or bridge through actor executors
   - Tests:
     - Unit: new test file or extend `ClassHandlerTests.cs` for actor type emission
     - BindingTests Swift: `Async/Actors.swift` already exists — extend with actor methods + `@MainActor` properties
     - Runtime: extend `MainActorTests.cs` with actor method dispatch, add actor type construction test

5. **Mid-session sanity:** `nuke test`

6. **End-of-session gates:**
   - `nuke test` — all unit tests pass
   - `nuke validate` — Lottie, Kingfisher, BlinkID, BlinkIDUX targets improve
   - `nuke binding-tests` — full pipeline green
   - `nuke runtime-tests-simulator` — new runtime tests pass

---

### Session 4 — Overflow ✅ PARTIAL (commit a35505b9 — Fix 11B done; Fix 5, 11A, 11C, 12, 6 deferred)

**Condition:** Only needed if Session 3 is too large, or if Fix 10 is worth pursuing.

**Fixes:** Any Session 3 overflow + Fix 10 (app-defined conformers)
**Libraries:** ActivityKit → Ship (if Fix 10 is done), otherwise remains Preview

Fix 10 requires a user-supplied specialization mechanism: the app provides a Swift file instantiating `Activity<MyAttributes>` and the generator binds the concrete type. This is significant architectural work and may be better deferred post-ship, with ActivityKit documented as Preview.

---

### Session 5 — Closure Existentials + Multi-Closure ✅ DONE

**Fixes:** 11A, 11C (shipped)
**Items:** ~25 closure items across Mappedin, Kingfisher, Lottie
**Libraries unblocked:** Mappedin (complete), Kingfisher (partial closures), Lottie (partial closures)

**What shipped:**
- **Fix 11A — Closure existential parameters.** `IsCdeclCompatibleType` accepts both `ProtocolListTypeSpec` and `NamedTypeSpec{IsAny=true}` as existential-param forms. The Swift adapter heap-allocates an `ExistentialContainer` per call via `UnsafeMutableRawPointer.allocate` + `initializeMemory`, defers dealloc, and passes the pointer across the cdecl boundary; the C# callback dereferences `*(ExistentialContainer{N}*)arg` and wraps via `new {Proxy}(…)` (or the well-known wrap class). `any Error` stays on the MCB path.
- **Fix 11C — Multi-closure methods.** Once 11A made per-closure `IsCdeclCompatibleType` return true for existential params, the `.All(...)` gate in `NeedsClosureCdeclWrapper` accepts mixed existential/primitive closure methods end-to-end. No code change needed beyond 11A.
- **Collateral fix:** `ClosureHandler.IsFrozenStruct` now guards against generic-parameter TypeSpecs (`τ_0_0`, `T`, …) — widened gate surface exposed a latent crash in Swinject. Covered by a regression test.

**Tests (all green):** Unit — `ClosureCdeclEmitterTests.cs` "Existential Closure Param Tests" region (9 tests) + `ClosureHandlerTests.IsFrozenStruct_WithGenericTypeParameter_DoesNotThrow`. BindingTests Swift — `Closures/Escaping.swift` `callWithExistentialCallback` / `callExistentialCallbackTwice` / `callWithMixedCallbacks`. Runtime — `ClosureTests.cs` "Existential Closure Parameters (Fix 11A / Fix 11C)" region. Validation — 95/95 (Swinject: skip → ok, +5483 lines back).

---

### Session 6 — Existential Metatype Arrays (Fix 5)

**Fixes:** 5
**Items:** ~15 across MusicKit, StoreKit2
**Libraries unblocked:** MusicKit (search APIs), StoreKit2 (VerificationResult constrained extensions)

**Scope:** `[any Protocol.Type]` parameter support. Currently blocked at `MemberEmissionValidator.cs:530-537` via `UnsupportedExistential` — `TryGetFirstExistentialTypeArgument` finds `any Protocol.Type` inside the array and has no supported container path. No existing infrastructure.

New work:
1. Detector: `IsArrayOfExistentialMetatypes(TypeSpec)` — check if type is `Swift.Array<X>` where `X` is `any P.Type`
2. Lift the block in `MemberEmissionValidator.CanEmitMethod` for this specific pattern
3. Swift wrapper: accept `(UnsafeRawPointer, Int)` pair (pointer + count), reconstruct `[any Protocol.Type]` via loop + `unsafeBitCast(ptr, to: Any.Type.self)`
4. C# side: P/Invoke takes `IntPtr` + `int` count, build from generated type metadata pointers
5. For each known conformer, emit metatype accessor helper (`{ConformerType}.self` in Swift, metadata pointer in C#)

**Tests:** Unit — new `MetatypeArrayEmitterTests.cs` or extend `MetatypeHelperEmitterTests.cs`. BindingTests Swift — add to `Generics/Metatypes.swift` function taking `[any SomeProtocol.Type]`. Runtime — metatype array passing test.

---

### Session 7 — Async Audit + GenericTypeCallback Verification ✅ DIAGNOSIS + MCB OPTIONAL-CLOSURE FIX

**Fixes surveyed:** 12, 6
**Items inspected:** 17 `GenericTypeCallback` + 1 `ActorIsolatedAsyncStream`
**Libraries unblocked:** none (all remaining items require new infrastructure, not edge fixes)

**Fix 12 — Async audit outcome.** The `async_method` skip reason is no longer emitted as a `SkipReason` enum value. The current `.validation-baseline.json` contains **zero** `async_method` entries — the 10 items from the original audit were absorbed by Fix 13 (nonisolated actor members, Session 3) and Sessions 4-5 infrastructure. Only one async residual remains: `ActorIsolatedAsyncStream: 1` (BlinkIDUX `BlinkIDEventStream.stream`, an actor-isolated AsyncStream property — requires async dispatch through actor executor, new infrastructure).

**Fix 6 — GenericTypeCallback diagnosis.** 17 items across Alamofire (8), GRDB (4), Kingfisher (3), RxSwift (1), YouTubePlayerKit (1). TipKit is not in the validation corpus — `Tips.Event<T>.sendDonation()` cannot be diagnosed here without adding TipKit to `build/validation-libraries.json` (out of scope for a verification session). The 17 items fall into three architectural patterns:

| Pattern | Count | Pipeline Site | Root cause |
|---|---|---|---|
| A — Async property with parent-generic return type | 4 | `PropertyHandler.cs:1149` (short-circuit) | Would still fail `MemberValidationPipeline.cs:138` even if routed — all four Alamofire `DataTask<Value>`/`DownloadTask<Value>` async properties return `Value` or `Result<Value, AFError>`. |
| B — Async method with parent-generic return type | 3 | `MemberValidationPipeline.cs:138` | Non-generic helper class can't carry the parent `T`. Applies to `Kingfisher.Delegate<Input,Output>.callAsync → Output?`, `Alamofire.StreamOf<Element>.Iterator.next → Element?`, `GRDB.AsyncValueObservation<Element>.Iterator.next → Element?`. |
| C — Non-async method where neither MCB nor NCB is eligible | 10 | `MemberValidationPipeline.cs:149` | Individual diagnoses below. |

**Pattern C per-method diagnoses** (MCB = `MethodClosureBridge`, NCB = `NestedClosureBridge`):

- `Kingfisher.KingfisherWrapper.setImage` / `setBackgroundImage` — two Optional closures (`progressBlock?`, `completionHandler?`); MCB and NCB only collect closures via `GetClosureTypeSpec(arg)` which does not see through `Swift.Optional<Closure>`, so `closureArgs.Count == 0` → rejected. Also `@MainActor`-isolated extension with `Base: KingfisherImageSettable` constraint.
- `RxSwift.Infallible._do` — seven throwing Optional closures (`((Element) throws -> Void)?`). Throwing closures aren't supported by MCB/NCB, and the closure args reference the parent generic `Element`.
- `Alamofire.AuthenticationInterceptor.adapt` — closure arg is `Result<URLRequest, any Error>`. `IsClosureArgSupported` rejects `URLRequest` as an ObjC-bridged generic type argument (MCB line 1173).
- `Alamofire.AuthenticationInterceptor.retry` — non-closure parameter `dueTo error: any Swift.Error` is an existential; `ClassifyParam` returns `Unsupported` (existentials aren't `NamedTypeSpec` with a type record).
- `Alamofire.AlamofireExtension.validate` — throwing closure typealias (`Validation` resolves to `throws -> ValidationResult`), and overloads use method-level generic `<S>` inside a generic parent struct.
- `GRDB.ValueObservation.handleEvents` — seven Optional closures; one arg is `Reducer.Value` (associated type / `DependentMember`), which does not resolve in MCB.
- `GRDB.QueryInterfaceRequest.filterWhenConnected` / `havingWhenConnected` — throwing closure returning `any SQLExpressible` existential. Both throwing-closure and existential-return unsupported by MCB.
- `YouTubePlayerKit.JavaScriptEvaluationResponseConverter.decode` — method-level generic `<D>` inside a generic parent struct, with `@autoclosure @escaping @Sendable () -> JSONDecoder`. Method-level generics in generic parents + autoclosure both unsupported by MCB.

**Pattern D — ActorIsolatedAsyncStream (1 item):** `BlinkIDUX.BlinkIDEventStream.stream` is a plain (non-async) getter on a Swift `actor` returning `AsyncStream<[UIEvent]>`. Session 3's Fix 13 unblocked explicit `nonisolated` members only. Actor-isolated stream properties need async-dispatch-through-executor wrapping (new infrastructure).

**MCB Optional-closure recognition — landed.** One of the post-ship infrastructure items above turned out to be a self-contained generator change rather than a new subsystem, so it shipped in this session. `MethodClosureBridge` now recognizes `Swift.Optional<Closure>` parameters: the C# wrapper emits a nullable delegate (`Action<T>? callback`), guards the `GCHandle.Alloc` behind a null-check, and skips publishing a function pointer when the caller passes `null`. The Swift `@_cdecl` adapter uses `funcPtr.map { __fp in ... }` so a `nil` funcPtr round-trips as `nil` (no force-unwrap). Confirmed in generated output for Nuke and GRDB wrappers; the new `OptionalErrorCallbackFixture` BindingTests fixture covers both non-null and null round-trips on simulator.

**Outcome on the 17 `GenericTypeCallback` items — unchanged count.** The MCB fix did not flip any of the 17 items because each one has a secondary blocker beyond Optional-closure-recognition:

- **Kingfisher `setImage`/`setBackgroundImage`** — actual blocker is the non-closure `with: Source?` parameter. `ClassifyParam` returns `Unsupported` for `Swift.Optional<Source>` because Optional non-closure marshalling is not yet wired through the relevant code path. This is a separate scope ("Optional non-closure param support") from Optional closures.
- **GRDB `ValueObservation.handleEvents`** — also blocked by a `Reducer.Value` `DependentMember` (associated type) in one of the closure args; the Optional fix alone doesn't resolve that.
- **RxSwift `Infallible._do`** — Optional AND throwing closures; throwing-closure bridge is the gating piece.
- The remaining items (Alamofire, YouTubePlayerKit) have orthogonal blockers (ObjC-bridged generic args, existential params, method-level generics in generic parents).

**NCB mirror deferred.** None of the target libraries (Nuke / GRDB / Kingfisher / RxSwift) exercise nested (multi-arity, non-flat) Optional closures, so the parallel `NestedClosureBridge` change was skipped. It can be added later if a real library motivates it.

**Remaining items** (throwing-closure bridges under generic parents, method-level generics under generic parents, actor-executor async dispatch, Optional non-closure param marshalling for Kingfisher) stay as post-ship roadmap work. Session 7's value is the diagnosis above plus the MCB Optional fix that supports future Optional-closure callers.

**Tests:** Unit — 4 new Optional-closure cases in `MethodClosureBridgeTests.cs` (nullable delegate type, `.map` adapter emission, no force-unwrap, `GCHandle.Alloc` only when non-null). BindingTests — `OptionalErrorCallbackFixture` in `GenericClosureBridge.swift` with Swift `((any Error) -> Void)?` param; two runtime tests (non-null invocation round-trip + null round-trip). Gates: `nuke test` (551 passed), `nuke validate` (all libraries `compile: ok`), `nuke binding-tests` (build succeeded). One pre-existing SwiftUI Text disposal test failure is unrelated to this session — see pre-existing failure note.

---

### Session Summary

| Session | Fixes | Items | Libraries to Ship | Gate Runs |
|---|---|---|---|---|
| 1 | 1, 2, 3, 7, 8 | ~240 | StripePayments, StripePaymentSheet, StripeIssuing, WorkoutKit | 1 full cycle |
| 2 | 4, 5, 9 | ~116 | CryptoKit, StoreKit2, MusicKit, RoomPlan | 1 full cycle |
| 3 | 11, 12, 6, 13 | ~111 | Lottie, Kingfisher, Nuke, TipKit, BlinkID, BlinkIDUX, Mappedin | 1 full cycle |
| 4? | 10 + overflow | ~10 | ActivityKit (if pursued) | 1 full cycle |
| 7 | 12, 6 (diagnosis) + MCB Optional closures | Optional MCB (infra) | — | test + validate + binding-tests |

**Session 7 outcome:** Diagnosis + one shipped fix. `async_method` skip reason is fully resolved (0 items). MCB now recognizes Optional closure parameters (nullable delegate + `funcPtr.map` adapter + guarded `GCHandle.Alloc`); the new pattern appears in generated Nuke/GRDB wrappers and is exercised by a BindingTests fixture. The 17 `GenericTypeCallback` count is unchanged because each remaining item has a secondary blocker orthogonal to Optional-closure recognition (Kingfisher's actual blocker is Optional non-closure params, not Optional closures; GRDB mixes in an associated-type; RxSwift adds throwing closures). Throwing-closure bridges under generic parents, method-level generics under generic parents, actor-executor async dispatch, and Optional non-closure param marshalling remain post-ship roadmap work.

---

## Items That Are NOT Blockers

These items appeared in the prior audit but do not prevent shipping:

| Item | Why Not a Blocker |
|---|---|
| `@_spi` type suppression | Correct behavior — internal Stripe/Apple APIs |
| SynthesizedCodable pruning (~159 items) | By design — Codable encode/init uses existentials |
| Variadic parameter packs (12 WeatherKit methods) | No C# equivalent — known language limitation |
| Protocol collision disambiguation (protocol members) | Architectural limitation documented in ProtocolHandler.cs |
| StripeUICore empty package | Correctly marked `"internal": true` |
| AnyError.LocalizedDescription | Resolved in 0.8.0 |
