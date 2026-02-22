# Binding Errors — Third-Party Library Validation

Last updated: 2026-02-20 | Baseline: 24/32 passed, 8 failed, 55 errors

Previous: [Sessions A-C (binding-errors-sessions-ac.md)](binding-errors-sessions-ac.md) — fixed 8 categories, 13→21 pass
Session D: fixed 4 categories, 21→24 pass (Alamofire, BlinkIDUX, RxSwift flipped)

---

## Validation Summary

| Library | Targets | Result | Error Count | Error Categories |
|---------|---------|--------|-------------|-----------------|
| Alamofire | 1 | Pass | 0 | — |
| BlinkID | 1 | Pass | 0 | — |
| BlinkIDUX | 1 | Pass | 0 | — |
| BRLMPrinterKit | 1 | Pass | 0 | — |
| CryptoSwift | 1 | Pass | 0 | — |
| GRDB | 1 | **Fail** | 8 | Constructor dedup, SwiftVoid constraint, self-conformance |
| KeychainAccess | 1 | Pass | 0 | — |
| Kingfisher | 1 | **Fail** | 34 | SwiftVoid constraint, missing Foundation types, bare SwiftOptional |
| Lottie | 1 | Pass | 0 | — |
| Mappedin | 1 | Pass | 0 | — |
| MicroblinkPlatform | 1 | Pass | 0 | — |
| Mixpanel | 1 | **Fail** | 1 | Cross-module protocol AnyType |
| Nuke | 1 | Pass | 0 | — |
| RxSwift | 1 | Pass | 0 | — |
| SkeletonView | 1 | Pass | 0 | — |
| SmartCardIO | 1 | Pass | 0 | — |
| SnapKit | 1 | Pass | 0 | — |
| Starscream | 1 | **Fail** | 5 | Closure AnyType in wrapper methods, Data→string mismatch |
| Stripe | 14 | **Mixed** | 7 | See Stripe breakdown below |
| **Total** | **32** | **24 pass** | **55** | |

### Stripe Breakdown

| Framework | Result | Errors | Primary Pattern |
|-----------|--------|--------|-----------------|
| Stripe | Pass | 0 | — |
| StripeApplePay | Pass | 0 | — |
| StripeCameraCore | Pass | 0 | — |
| StripeCardScan | Pass | 0 | — |
| StripeConnect | Pass | 0 | — |
| StripeCore | **Fail** | 1 | Cross-module protocol AnyType in dict |
| StripeCryptoOnramp | Pass | 0 | — |
| StripeFinancialConnections | Pass | 0 | — |
| StripeIdentity | Pass | 0 | — |
| StripeIssuing | Pass | 0 | — |
| StripePayments | **Fail** | 2 | Cross-module protocol in dict return |
| StripePaymentSheet | **Fail** | 3 | Async closure return type mismatch |
| StripePaymentsUI | Pass | 0 | — |
| StripeUICore | **Fail** | 1 | Cross-module protocol array param |

---

## Session D Fixes (2026-02-20)

### Fixed: Tuple-with-Existential in Enum Case Construction (Category 7)
**File:** `EnumHandler.CaseConstruction.cs`
**Fix:** Added skip gate for tuple parameters containing `ExistentialContainer` in publicType. Prevents leaking ABI types into public enum case signatures.
**Result:** Eliminated 32 ExistentialContainer cascade errors from Kingfisher. Kingfisher still fails (34 errors) due to pre-existing SwiftVoid constraint, missing Foundation types (RunLoopMode, URLSessionAuthChallengeDisposition, FileAttributeKey), and bare SwiftOptional errors.

### Fixed: Optional\<Existential\> Protocol Proxy Receiver (Category 6)
**File:** `ProtocolProxyEmitter.Receivers.cs`
**Fix:** Added `Optional<existential>` handling to `GetReceiverExistentialGetterConversion()` and `GetReceiverExistentialSetterConversion()`. Also fixed priority ordering — existential conversions now checked before `GetParameterConversion`/`GetReturnConversion` which incorrectly resolve `Optional<any Error>` to `SwiftOptional<AnyType>`. Also fixed ABI type override for `Optional<existential>` in property receiver to use `SwiftOptional<ExistentialContainer>`.
**Result:** BlinkIDUX 1→0. **Flipped to pass.**

### Fixed: Closure Inner Type Not Native-Remapped (Category 5)
**File:** `ClosureHandler.cs`
**Fix:** Added native type remapping in `TranslateTypeSpecToCSharp()` for types with `NativeTypeName` (e.g., Foundation.Data → Foundation.NSData). Matches `GetIdiomaticCSharpType` output used for property signatures.
**Result:** Alamofire 1→0. **Flipped to pass.**

### Fixed: Closure AnyType in Protocol Proxy Receivers (Category 1, partial)
**File:** `ProtocolHandler.cs`
**Fix:** Added unsupported closure check to `skippedMethodKeys` flow (methods) and `skippedPropertyNames` flow (properties). Uses `ClosureHandler.IsSupportedClosure()` to detect closures that would produce AnyType in receiver marshalling. Propagates to receiver, static init, and interface impl via existing skip set plumbing.
**Result:** RxSwift 5→0 (**flipped to pass**), StripeCore 2→1, StripeUICore 4→1, Mixpanel 3→1. Starscream errors are in wrapper methods (not protocol receivers), so unchanged.

### Hardened: Optional\<Existential\> ABI Type Override for All Receiver Sites
**File:** `ProtocolProxyEmitter.Receivers.cs`
**Fix:** Extracted `OverrideOptionalExistentialAbiType()` helper and applied it to all 5 `forAbiMarshalling: true` call sites: property receiver, subscript return type, subscript index params (getter + setter), and method params. Previously only property receivers had the override; subscript and method receivers could regress to `SwiftOptional<AnyType>` (incorrect memory layout for `MarshalFromSwift`).
**Result:** No validation change (no current libraries exercise `Optional<any Protocol>` in subscript/method receiver params). Latent bug prevention.
**Residual risk:** No targeted test case yet for `Optional<any Protocol>` in subscript/method parameters. Structurally covered, but runtime behavior unverified.

---

## Remaining Error Categories

### 1. Closure AnyType in Wrapper Methods — CS1503
**Count:** ~5 errors | **Libraries:** Starscream (4), others
**Root cause:** Methods with unsupported closures as parameters still get emitted in wrapper methods (non-protocol context). The Session D fix only covers protocol proxy receivers.
**Fix approach:** Extend `ShouldSkipMethodEmission` to catch unsupported closures in more contexts.

### 2. Cross-Module Protocol → AnyType — CS0266/CS1503
**Count:** ~5 errors | **Libraries:** Mixpanel (1), StripeCore (1), StripePayments (2), StripeUICore (1)
**Root cause:** Protocol TypeRecord defined in a different module not loaded as dependency.
**Fix approach:** Build config — use `--framework-dependency` + cross-module databases.

### 3. Async Closure Return Type Mismatch — CS0029
**Count:** 3 errors | **Libraries:** StripePaymentSheet (3)
**Root cause:** Async closures projected as sync closures in public signature.
**Fix approach:** Detect async closures at signature level and wrap return type in `Task<>`.

### 4. GRDB-Specific Errors — CS0111/CS0315/CS0535/CS0314
**Count:** 8 errors | **Libraries:** GRDB (8)
- CS0111 (3): Constructor dedup after closure skip
- CS0315 (3): SwiftVoid constraint
- CS0314 (1): Cross-module protocol constraint
- CS0535 (1): Self-referential protocol conformance

### 5. Kingfisher Residual Errors
**Count:** 34 errors | **Libraries:** Kingfisher (34)
- CS0315 (16): SwiftVoid as type parameter constrained to ISwiftObject
- CS0234 (14): Missing Foundation types (RunLoopMode, URLSessionAuthChallengeDisposition, FileAttributeKey)
- CS0305 (4): Bare SwiftOptional (missing type arguments)

### 6. Starscream Data→String Mismatch — CS1503
**Count:** 1 error | **Libraries:** Starscream (1)
**Root cause:** `Swift.Data` passed where `string` expected — native remapping not applied in this context.

---

## Error Count by Category

| # | Category | Errors | Libraries | Fix Complexity |
|---|----------|--------|-----------|---------------|
| 1 | Closure AnyType in wrapper methods | ~5 | 2 | Moderate — expand skip gate |
| 2 | Cross-module protocol → AnyType | ~5 | 4 | Low — build config |
| 3 | Async closure return type mismatch | 3 | 1 | Moderate — async closure projection |
| 4 | GRDB-specific (dedup, SwiftVoid, self-conformance) | 8 | 1 | Mixed — 4 distinct sub-issues |
| 5 | Kingfisher residual (SwiftVoid, Foundation types, bare generics) | 34 | 1 | Mixed — multiple distinct issues |
| 6 | Data→string mismatch | 1 | 1 | Low — native remapping scope |

---

## Recommended Fix Order

**Quick wins (flip libraries to pass):**

1. **Category 3** (StripePaymentSheet, 3 errors) — Async closure return projection. Flips StripePaymentSheet to pass.
2. **Category 1** (Starscream, ~4 closure errors) — Extend `ShouldSkipMethodEmission` for wrapper methods. May flip Starscream.

**Medium effort:**

3. **Category 4** (GRDB, 8 errors) — Four distinct sub-issues.
4. **Category 5** (Kingfisher, 34 errors) — SwiftVoid needs `ISwiftObject` impl or skip gate; Foundation types need Apple framework TypeRecords; bare generics need detection.

**Build config (not generator fixes):**

5. **Category 2** (Stripe inter-module, Mixpanel) — Cross-module protocol TypeRecords.

**Potential pass rate after quick wins:** 24 → 25-26/32
**Potential pass rate after all:** 28-30/32
