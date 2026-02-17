# AnyType Post-Fix Audit

Generated: 2026-02-17

## Changes Applied

1. **Foundation added to `AppleObjCFrameworkModules`** — Foundation class types (URLResponse, URLSession, URLSessionTask, URLSessionTaskMetrics, URLCredential, etc.) now auto-bridge as ObjC classes
2. **Foundation value types added to `AppleFrameworkValueTypes`** — Data, URL, UUID, URLError, URLError.Code, URLRequest, Calendar, Locale, TimeZone, Date, Selector, ComparisonResult, etc. correctly excluded from auto-bridging
3. **UnsafePointer\<T\> `IsBoundGeneric` fix** — typed pointer types excluded from bound generic detection (prevented `.Payload.DangerousGetHandle()` on IntPtr)
4. **Git worktree fix** — `FindRepoRoot()` in SDK tests now checks for `.git` as file (worktree) or directory (normal repo)

### Not Applied (investigated but reverted)

- **Optional\<Any\> → SwiftOptional\<object\>** — Investigated but reverted. Bare `Any` → `object` causes CS0311 in generics with `ISwiftObject` constraint (e.g., `Keyframe<object>`, `SwiftArray<object>`). Only `SwiftOptional<T>` has no constraint, but `Any` can appear nested inside constrained generics. Requires context-aware translation (future work).
- **QuartzCore auto-bridging** — QuartzCore types (CALayer, etc.) don't have C# namespace in .NET iOS (they're re-exported through UIKit). Adding QuartzCore to auto-bridge generates `QuartzCore.CALayer` references that don't compile.

## Summary

| Library | Before (AnyTypeFallback) | After (AnyTypeFallback) | Delta |
|---------|--------------------------|-------------------------|-------|
| **BlinkID** | 0 | 0 | -- |
| **Nuke** | 0 | 0 | -- |
| **Lottie** | 1 | 1 | -- |
| **Alamofire** | 32 | 13 | -19 |

## BlinkID
No AnyTypeFallback entries (before or after).

## Nuke
No AnyTypeFallback entries (before or after).

## Lottie
**Before and After:** 1 (`animationLayer` — `Optional<QuartzCore.CALayer>`, QuartzCore not available as C# namespace)

Root cause is QuartzCore module not being in auto-bridge list (can't add because QuartzCore types don't have a standalone C# namespace in .NET iOS — they're re-exported through UIKit).

## Alamofire (13 Residual AnyTypeFallback)

| # | Name | ContainingType | Root Cause |
|---|------|----------------|------------|
| 1 | defaultURLErrorOfflineCodes | OfflineRetrier | Foundation value type: `URLError.Code` |
| 2 | encoding | StringResponseSerializer | Foundation extension value type: `String.Encoding` |
| 3 | failedStringEncoding | AFError | Foundation extension value type: `String.Encoding` |
| 4 | refresh | Authenticator | Closure with Foundation types |
| 5 | credential | AuthenticationInterceptor | Generic associated type (`τ_0_0.Credential`) |
| 6 | adapt | RequestAdapter | Closure with Foundation types (URLRequest) |
| 7 | adapt | RequestInterceptor | Closure with Foundation types (URLRequest) |
| 8 | flags | NetworkReachabilityManager | SystemConfiguration: `SCNetworkReachabilityFlags` |
| 9 | defaultRetryableURLErrorCodes | RetryPolicy | Foundation value type: `URLError.Code` |
| 10 | retryableURLErrorCodes | RetryPolicy | Foundation value type: `URLError.Code` |
| 11 | certificates | AlamofireExtension | Security CF type: `SecCertificate` |
| 12 | publicKeys | AlamofireExtension | Security CF type: `SecKey` |
| 13 | publicKey | AlamofireExtension | Security CF type: `SecKey` |

### Residual Root Cause Breakdown
- **Foundation value types (URLError.Code, String.Encoding):** 5 entries — correct behavior, these are Swift structs/enums
- **Security CF types (SecCertificate, SecKey):** 3 entries — out of scope (CF-style types, not NSObject)
- **SystemConfiguration (SCNetworkReachabilityFlags):** 1 entry — separate module, not in auto-bridge list
- **Closure signatures with Foundation types:** 3 entries — closure handler doesn't recurse into closure param types for type resolution
- **Generic associated type:** 1 entry — unresolvable generic type parameter

## Compile Validation (12 Libraries)

All libraries that compiled before continue to compile. No regressions introduced.

3 additional libraries (Realm, RealmSwift, Stripe) were excluded — known non-binding failures unrelated to these changes (see CLAUDE.md "Known non-binding failures").

| Status | Library | Notes |
|--------|---------|-------|
| ✓ | Alamofire | 41,050 lines |
| ✓ | BlinkID | 52,445 lines |
| ✓ | BRLMPrinterKit | 43 lines |
| ✓ | CryptoSwift | 29,834 lines |
| ✓ | Lottie | 29,326 lines |
| ✓ | MicroblinkPlatform | 3,522 lines |
| ✓ | Mixpanel | 6,760 lines |
| ✓ | SkeletonView | 12,094 lines |
| ✓ | SmartCardIO | 4,514 lines |
| ✗ | ACSSmartCardIO | Pre-existing: NuGet dependency (SmartCardIO.Swift.iOS) |
| ✗ | Mappedin | Pre-existing: Swift.AnyObject not in TypeDB |
| ✗ | Nuke | Pre-existing: CS0102 Progress duplicate |
