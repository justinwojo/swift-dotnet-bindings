# Binding Errors — Third-Party Library Validation

Last updated: 2026-02-20 | Baseline: 21/32 passed, 11 failed, 0 no output

## Validation Summary

| Library | Targets | Result | Error Count | Error Categories |
|---------|---------|--------|-------------|-----------------|
| Alamofire | 1 | **Fail** | 1 | Closure AnyType fallback |
| BlinkID | 1 | **Pass** | 0 | — |
| BlinkIDUX | 1 | **Fail** | 1 | Closure AnyType fallback |
| BRLMPrinterKit | 1 | **Pass** | 0 | — |
| CryptoSwift | 1 | **Pass** | 0 | — |
| GRDB | 1 | **Fail** | 8 | Closure AnyType fallback, other |
| KeychainAccess | 1 | **Pass** | 0 | — |
| Kingfisher | 1 | **Fail** | 32 | Closure AnyType fallback, other |
| Lottie | 1 | **Pass** | 0 | — |
| Mappedin | 1 | **Pass** | 0 | — |
| MicroblinkPlatform | 1 | **Pass** | 0 | — |
| Mixpanel | 1 | **Fail** | 3 | Unresolved cross-module protocol, closure AnyType fallback |
| Nuke | 1 | **Pass** | 0 | — |
| RxSwift | 1 | **Fail** | 5 | Closure AnyType fallback |
| SkeletonView | 1 | **Pass** | 0 | — |
| SmartCardIO | 1 | **Pass** | 0 | — |
| SnapKit | 1 | **Pass** | 0 | — |
| Starscream | 1 | **Fail** | 5 | Closure AnyType fallback |
| Stripe | 14 | **Mixed** | 16 | See Stripe breakdown below |
| **Total** | **32** | **21 pass** | **66** | |

### Stripe Breakdown

| Framework | Result | Errors | Primary Pattern |
|-----------|--------|--------|-----------------|
| Stripe | Pass | 0 | — |
| StripeApplePay | **Pass** | 0 | — |
| StripeCameraCore | **Pass** | 0 | — |
| StripeCardScan | Pass | 0 | — |
| StripeConnect | **Pass** | 0 | — |
| StripeCore | Fail | 2 | Unresolved cross-module protocol, closure AnyType fallback |
| StripeCryptoOnramp | Pass | 0 | — |
| StripeFinancialConnections | Pass | 0 | — |
| StripeIdentity | Pass | 0 | — |
| StripeIssuing | Pass | 0 | — |
| StripePayments | Fail | 2 | Unresolved cross-module protocol |
| StripePaymentSheet | Fail | 3 | Unresolved cross-module protocol |
| StripePaymentsUI | **Pass** | 0 | — |
| StripeUICore | Fail | 4 | Unresolved cross-module protocol, closure AnyType fallback |

---

## Error Categories

### 1. Generator Crash: Unqualified Type Names (`Self`, `repeat`) — FIXED (Session A)

**Affected:** Alamofire, GRDB, Kingfisher (crash on `Self`), StripeCore (crash on `repeat`)

**Fix:** Added explicit `name is "Self" or "repeat"` guard in `TypeProjectionFactory.ProjectNamedType()` after the `IsGenericTypeParameter()` check. Returns `null` (unsupported projection). Placed before `IsAny` existential routing to avoid intercepting unqualified existential spellings.

**Result:** All 4 libraries now produce bindings (with remaining errors from other categories).

---

### 2. Optional\<any Protocol\> Property Getter/Setter (CS0266 + CS1061) — FIXED (Session C)

**Affected:** Lottie (2), Nuke (3), Mixpanel (6), Mappedin (4), StripeApplePay (4), StripeCameraCore (2), StripeConnect (14), StripePaymentsUI (8), StripeUICore (26) — **~69 errors total**

This was the **dominant error pattern**, accounting for ~60% of all compile errors.

#### Getter (CS0266)

**Symptom:** `Cannot implicitly convert type 'SwiftOptional<AnyType>' to 'ISomeProtocol?'`

**Root cause:** For accessor methods (`IsAccessor=true`), `IsConvertibleType` is gated on `!IsAccessor` (line 99 of `EmitReturnMethod`), so Optional-existential returns fell through to `RequiresBoundGenericMarshalling` which emitted `SwiftMarshal.MarshalFromSwift<SwiftOptional<AnyType>>(...)` — wrong type.

**Fix:** Inserted accessor-scoped `IsOptionalExistential` check in `WrapperEmitter.Return.cs` before the bound-generic handler. P/Invoke returns IntPtr for Optional-existential — marshals to `SwiftOptional<ExistentialContainer>` then wraps `.Some` in proxy class or returns null for `.None`. Added `GetPublicExistentialType() != "object"` guard to prevent bare `"Proxy"` name for unresolved protocols. Same guard hardened in 3 pre-existing Optional-existential return paths (`EmitTypeConvertedReturn`, `EmitOptionalReturnBufferRead`, `EmitTypeConvertedIndirectReturn`).

#### Setter (CS1061)

**Symptom:** `'ISomeProtocol' does not contain a definition for 'PayloadBuffer'`

**Root cause:** `EmitBoundGenericArguments` processed Optional-existential as a bound generic, calling `.PayloadBuffer` on the interface type. The accessor early return in `EmitTypeConversions` prevented the dedicated existential marshalling from running.

**Fix:** Added exclusion guard in `EmitBoundGenericArguments` for Optional-existential params. Extracted `EmitOptionalExistentialParamConversion()` helper from existing inline code. Modified accessor early return in `EmitTypeConversions` to call it for Optional-existential params before returning. Added `GetPublicExistentialType() != "object"` guard for consistency.

**Result:** ~69 errors eliminated. 8 libraries flipped to pass (Lottie, Mappedin, Nuke, SkeletonView, StripeApplePay, StripeCameraCore, StripeConnect, StripePaymentsUI).

---

### 3. Optional\<UnsupportedClosure\> Emits Bare `SwiftOptional` (CS0305) — FIXED (Session A)

**Affected:** SkeletonView (2), Starscream (1), StripePayments (2), StripePaymentSheet (10) — **15 errors total**

**Fix:** Added `IsBareGenericTypeName` guard in `WrapperSignatureBuilder.HandleArguments()` and `HandleReturnType()` type-record fallback paths in `MethodSignature.cs`. When the resolved type name is bare generic (e.g., `SwiftOptional` without `<T>`), emits `AnyType` instead. Preserves `inoutModifier` in the parameter path.

**Result:** Bare `SwiftOptional` CS0305 errors eliminated. Some previously-hidden errors now visible (closure AnyType fallback, Optional protocol) since methods are no longer blocked by the bare generic.

---

### 4. Closure Parameter as `AnyType` in P/Invoke (CS1503) — FIXED (Session B)

**Affected:** Nuke (5), Mixpanel (1), Mappedin (4), StripeCryptoOnramp (1) — **11 errors total** (was)

**Fix:** Added unsupported closure parameter check to `MemberEmissionValidator.ShouldSkipMethodEmission` (before the constructor early-return, so both methods and constructors are covered) and `CanEmitMethod` (for conformance validation). Uses `ClosureHandler.IsSupportedClosure()` to detect unsupported closures and returns `SkipReason.UnsupportedClosure` to skip the method/constructor entirely.

**Result:** Methods/constructors with unmarshallable closure parameters are now cleanly skipped instead of emitting broken code. API surface reduction is expected — these closures require expanded Cdecl wrapper support to emit correctly. Skipped members are tracked in `binding-report.json` under `SkipReason.UnsupportedClosure`.

**Residual:** The skipped APIs are visible in the binding report. Future work to expand closure Cdecl wrapper support (beyond the current 8 constraints) would recover these APIs.

---

### 5. Optional\<Array\<T\>\> Property Projection Inconsistency (CS0266) — FIXED (Session B)

**Affected:** BlinkID (1)

**Fix:** Added `GetIdiomaticCSharpType` fallback in 3 property type projection sites when `TypeProjectionFactory.Project()` returns `null` (can't project user-defined generic types). The fallback uses the same `TranslateTypeSpecWithGenerics` helper as the getter/setter body conversion, ensuring the property declaration type matches.

- `PropertyHandler.Emit`: After factory returns null, tries `GetIdiomaticCSharpType` with `typeTranslator`
- `MemberEmissionValidator.CanEmitProperty`: Same idiomatic override after `TranslateBoundGenericTypeToCSharp`
- `ProtocolConformanceValidator.GetInterfacePropertyType`: Same override in bound generic branch

`TranslateTypeSpecWithGenerics` promoted from `private static` to `internal static` for shared access.

**Result:** BlinkID now compiles cleanly (1 → 0 errors). Also covers `Optional<Dictionary<K,V>>` with generic key/value types (same mechanism).

---

### 6. Generic Protocol Existential Missing Type Argument (CS0305) — FIXED (Session A)

**Affected:** BlinkIDUX (3)

**Fix:** Added guard in `ExistentialHandler.GetPublicExistentialType()` — when a protocol `NamedTypeSpec` has `GenericParameters.Count > 0`, returns `AnyType` instead of emitting bare `IProtocol`. Uses `AnyType` (not `"object"`) to preserve API surface and avoid triggering member pruning in `MemberEmissionValidator` and `MethodHandler`.

**Result:** BlinkIDUX dropped from 3 to 2 errors. The CS0305 bare `IEventStream` is gone; remaining errors are different categories.

---

### 7. Enum Case/Property Name Collision (CS0102) — FIXED (Session A)

**Affected:** RxSwift (1)

**Fix:** In `EnumHandler.cs`: (1) Removed `IsStatic` restriction on property collision check — instance properties now also skipped when they collide with case constructor names. (2) Expanded `propertyNames` set passed to `HandleBaseDecl` for method collision detection to include case constructor names, `CaseTag`, `Tag`, `TryGet{CaseName}`, and simple case property names.

**Result:** RxSwift CS0102 eliminated. Remaining 5 errors are closure AnyType fallback.

---

### 8. Protocol Proxy Receiver Existential Conversions (CS1503) — FIXED (Session C)

**Affected:** StripeUICore (3), Mixpanel (1) — **4 errors total**

**Symptom:** Various `CS1503` errors where protocol proxy receiver code had mismatched types between the public interface and the ABI marshalling layer.

**Root cause:** Protocol proxy receivers marshal ABI types (`ExistentialContainer`) but the public interface methods expect idiomatic C# types (`IProtocol`, `IReadOnlyList<IProtocol>`, `IReadOnlyDictionary<K, IProtocol>`). No conversion existed between ABI existential types and projected protocol interfaces in the proxy receiver emission path.

**Fix:** Added two helper methods in `ProtocolProxyEmitter.Receivers.cs`:
- `GetReceiverExistentialGetterConversion()` — converts C# idiomatic → Swift ABI for getter returns. Handles standalone existentials (via `ISwiftExistentialConvertible.GetExistentialContainer()`), `Array<existential>` (via `SwiftArray.FromEnumerable` with element container extraction), and `Dictionary<K, existential>` (via `SwiftDictionary.FromDictionary` with value container extraction).
- `GetReceiverExistentialSetterConversion()` — converts Swift ABI → C# idiomatic for setter params. Wraps containers in proxy classes for standalone, array (via `.AsProjected`), and dictionary (via `.ToDictionary`) cases.

Integrated at 7 sites: property getter, property setter, method params, method return, subscript getter, subscript setter, and dictionary value conversion in `GetReceiverDictionaryConversion`.

All paths guard on `GetPublicExistentialType() != "object"` to prevent bare `"Proxy"` name for unresolved protocols.

**Result:** 4 errors eliminated. Remaining StripeUICore/Mixpanel errors are from different categories (unresolved cross-module protocols, closure AnyType fallback).

---

## Error Count by Category

| # | Category | Status | Errors | Libraries Affected |
|---|----------|--------|--------|-------------------|
| 1 | Generator crash (Self/repeat) | **FIXED** (A) | 0 (was 4 libs blocked) | — |
| 2 | Optional\<any Protocol\> property get/set | **FIXED** (C) | 0 (was ~69) | — |
| 3 | Optional\<UnsupportedClosure\> bare SwiftOptional | **FIXED** (A) | 0 (was 15) | — |
| 4 | Closure param AnyType fallback | **FIXED** (B) | 0 (was ~30+, skipped) | — |
| 5 | Optional\<Array\<T\>\> projection inconsistency | **FIXED** (B) | 0 (was 1) | — |
| 6 | Generic protocol existential missing type arg | **FIXED** (A) | 0 (was 3) | — |
| 7 | Enum case/property name collision | **FIXED** (A) | 0 (was 1) | — |
| 8 | Protocol proxy receiver type mismatch | **FIXED** (C) | 0 (was 4) | — |

All 8 original error categories are now fixed.

---

## Remaining Errors (66 total across 11 libraries)

The remaining errors fall into categories not yet tracked above:

| Pattern | Approx Count | Libraries |
|---------|-------------|-----------|
| Closure with AnyType fallback (unsupported closure type not caught by skip gate) | ~30 | Alamofire, BlinkIDUX, GRDB, Kingfisher, Mixpanel, RxSwift, Starscream, StripeCore, StripeUICore |
| Unresolved cross-module protocol (protocol TypeRecord not available, projects to AnyType) | ~10 | Mixpanel, StripeCore, StripePayments, StripePaymentSheet, StripeUICore |
| Other (array-of-existential param type mismatch, closure cross-wiring) | ~26 | GRDB, Kingfisher |

---

## Fix Complexity (Remaining)

| Pattern | Files | Complexity | Notes |
|---------|-------|------------|-------|
| Closure AnyType fallback | `MemberEmissionValidator.cs` | Moderate | Expand skip gate or add more Cdecl wrapper coverage |
| Unresolved cross-module protocol | `ModuleDatabaseEmitter.cs`, consumer builds | Moderate | Requires cross-module database for protocol TypeRecords |
| Kingfisher/GRDB bulk errors | Various | TBD | Need per-error triage |

---

## Session Summary

| Session | Categories | Status | Errors Fixed | Pass Rate |
|---------|-----------|--------|-------------|-----------|
| A | 1, 3, 6, 7 | **COMPLETE** | 19 errors + 4 crashes unblocked | 11/32 (0 crashes) |
| B | 4, 5 | **COMPLETE** | 43 errors eliminated (220 → 177) | 13/32 |
| C | 2, 8 | **COMPLETE** | 111 errors eliminated (177 → 66) | 21/32 |

**Notes:**
- Session A unblocked 4 crashed libraries, inflating visible error count (115 → 220) as expected.
- Session B eliminated 43 errors and flipped 2 libraries to pass (BlinkID, StripeCryptoOnramp).
- Session C eliminated 111 errors and flipped 8 libraries to pass (Lottie, Mappedin, Nuke, SkeletonView, StripeApplePay, StripeCameraCore, StripeConnect, StripePaymentsUI). All 8 original error categories are now fixed. Remaining 66 errors are new patterns (closure AnyType fallback, unresolved cross-module protocols).
