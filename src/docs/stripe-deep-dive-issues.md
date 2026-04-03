# Stripe Deep Dive: All Issues Resolved

**Last updated**: April 3, 2026
**SDK version**: 0.5.1 (uncommitted, includes fixes from issues 1-9)
**Test results**: 151 passed, 0 failed, 0 skipped (all 12 modules have wrapper xcframeworks)

---

## Resolved Issues (1-9)

Issues 1-8 resolved April 2, Issue 9 resolved April 3. Fixes are uncommitted on swift-bindings main.

| # | Issue | Root Cause | Fix |
|---|---|---|---|
| 1 | Unqualified type names in CdeclParamMapper | `RenderSwiftTypeSpec()` instead of module-qualified | Switch to `RenderModuleQualifiedSwiftTypeSpec()` |
| 2 | SwiftUI `Binding` bare emission | Fallthrough to `MapDatabaseType` stripped generic param | Prevent fallthrough + extend Binding<Optional<T>> |
| 3 | Cascading framework deps | Resolved by Issue 1 fix | — |
| 4 | STPAppInfo string corruption | `Optional<Class>` accessor used SwiftOptional instead of IntPtr | Return IntPtr directly for reference optionals |
| 5 | StripePayments excluded | Stale comment; enum bindings now correct | Uncommented ProjectReference |
| 6 | Simulator-only symbols break device | `#if targetEnvironment(simulator)` members emitted unconditionally | ABI JSON diff + `#if` guards + thunk filtering |
| 7 | @_spi enum case leak | `SimpleEnum` didn't check `IsSpiProtected` | Added checks to 4 iteration sites |
| 8 | SDK drops non-binding framework deps | Sdk.targets all-or-nothing switch + duplicate error + no wrapper fallback | Always include deps + skip dupes + search-path fallback |
| 9 | UIColor property roundtrip returns null | Native thunks for ObjC-bridged class dispatch (Tj) getters/setters have ARC ownership mismatch vs @_cdecl wrappers | Reject ObjC-bridged types in dispatch thunk gate → fall back to SBW wrappers |

## Binding Coverage

| Module | Types | Members | Skip Reasons |
|---|---|---|---|
| StripeCore | 18/106 | 67/424 | 92 internal, 2 unsupported sig |
| StripePayments | 200/240 | 1230/1633 | 32 internal, 121 member skips |
| StripePaymentSheet | 72/138 | 311/647 | 34 internal, 70 member skips |
| StripeConnect | 18/37 | 78/150 | 14 internal |
| StripeIdentity | 4/6 | 9/18 | 2 internal |
| StripeApplePay | 4/8 | 6/36 | 4 internal, 12 member skips |
| StripeIssuing | 7/7 | 18/22 | 4 member skips |
| StripeCardScan | 8/14 | 14/20 | 6 internal |
| StripeFinancialConnections | 7/10 | 12/22 | 2 internal, 6 member skips |
| StripePaymentsUI | 12/40 | 138/276 | 36 internal, 2 SwiftUI view |
| Stripe | 1/2 | 1/3 | 1 internal |

Type skips are overwhelmingly `ModuleInternal` (correct — these are `@_spi` or `@usableFromInline internal` APIs not meant for consumers). The public API surface is well covered.
