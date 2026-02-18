# Environmental Type Errors: Investigation & Root Cause Analysis

**Date**: February 18, 2026
**Context**: Post-validation sweep after completing Roadmap Items 1 (cross-module), 2 (existential elimination), and 3 (native enums).

---

## Summary

A full library validation sweep (26 libraries, generate + compile) found **4 libraries with CS0234 compile errors** (314 total errors). Investigation across 4 commit checkpoints confirmed these are **not regressions from Steps 1-3**. The errors originate from the Foundation auto-bridge feature (Session F, commit `bc7fe98`) and pre-existing AVFoundation/UIKit gaps. The prior "25 of 25 at 0 errors" documentation was inaccurate.

---

## Current State (HEAD: `cdbf8d5`)

| Library | CS0234 Errors | Missing Types | Category |
|---------|:------------:|---------------|----------|
| Alamofire | 294 | 16 Foundation types | Session F regression |
| SkeletonView | 4 | 1 Foundation type | Session F regression |
| StripeCameraCore | 12 | 3 AVFoundation types | Pre-existing (never fixed) |
| StripeUICore | 4 | 1 UIKit type | Pre-existing (never fixed) |
| **22 other libraries** | **0** | — | Clean |

---

## Bisect Results

Tested the 4 failing libraries at 4 commit checkpoints:

| Library | Session B (`46a5084`) | Session F (`bc7fe98`) | Pass 5.1 (`7a3eb28`) | HEAD (`cdbf8d5`) |
|---------|:----:|:----:|:----:|:----:|
| Alamofire | **0** | 314 | 306 | 294 |
| SkeletonView | **0** | 4 | 4 | 4 |
| StripeCameraCore | 12 | 12 | 12 | 12 |
| StripeUICore | 4 | 8 | 8 | 4 |

**Key findings:**
- Session F (`bc7fe98`, "Foundation auto-bridge") introduced regressions in Alamofire (+314) and SkeletonView (+4)
- StripeCameraCore's 12 errors existed at every checkpoint — never fixed
- StripeUICore's 4 base errors existed at every checkpoint; Session F temporarily added 4 more (now resolved back to 4)
- Steps 1-3 (cross-module, existential, enums) **slightly improved** the count: Alamofire 314→294, StripeUICore 8→4

---

## Root Cause 1: Foundation Auto-Bridge Naming Mismatch

**Introduced**: Session F, commit `bc7fe98`
**Affected**: Alamofire (294 errors, 16 types), SkeletonView (4 errors, 1 type)

### The Feature

Session F added `Foundation` to the `AppleObjCFrameworkModules` allowlist in `TypeDatabaseExtensions.cs:504-516`. This causes Foundation types not in the explicit type database or value-type exclusion list to get **synthetic ObjCBridged records**, enabling them to be emitted as C# classes with `IntPtr` marshalling via `GetNSObject<T>()`.

### The Problem

The auto-bridge creates C# type names using **Swift naming conventions**, but .NET iOS uses **ObjC naming conventions** for Foundation types:

| Swift Name | Auto-Bridge Emits | .NET iOS Actual Name | Exists in .NET? |
|-----------|------------------|---------------------|:---------------:|
| `Foundation.FileManager` | `Foundation.FileManager` | `Foundation.NSFileManager` | Yes, wrong name |
| `Foundation.URLSessionTask` | `Foundation.URLSessionTask` | `Foundation.NSUrlSessionTask` | Yes, wrong name |
| `Foundation.HTTPURLResponse` | `Foundation.HTTPURLResponse` | `Foundation.NSHttpUrlResponse` | Yes, wrong name |
| `Foundation.CachedURLResponse` | `Foundation.CachedURLResponse` | `Foundation.NSCachedUrlResponse` | Yes, wrong name |
| `Foundation.DateFormatter` | `Foundation.DateFormatter` | `Foundation.NSDateFormatter` | Yes, wrong name |
| `Foundation.InputStream` | `Foundation.InputStream` | `Foundation.NSInputStream` | Yes, wrong name |
| `Foundation.Progress` | `Foundation.Progress` | `Foundation.NSProgress` | Yes, wrong name |
| `Foundation.URLAuthenticationChallenge` | `Foundation.URLAuthenticationChallenge` | `Foundation.NSUrlAuthenticationChallenge` | Yes, wrong name |
| `Foundation.URLSessionDataTask` | `Foundation.URLSessionDataTask` | `Foundation.NSUrlSessionDataTask` | Yes, wrong name |
| `Foundation.URLSessionDownloadTask` | `Foundation.URLSessionDownloadTask` | `Foundation.NSUrlSessionDownloadTask` | Yes, wrong name |
| `Foundation.URLSessionTaskMetrics` | `Foundation.URLSessionTaskMetrics` | `Foundation.NSUrlSessionTaskMetrics` | Yes, wrong name |
| `Foundation.URLSessionWebSocketTask` | `Foundation.URLSessionWebSocketTask` | ? | Possibly missing |
| `Foundation.URLSessionWebSocketTaskMessage` | `Foundation.URLSessionWebSocketTaskMessage` | ? | Possibly missing |
| `Foundation.URLSessionWebSocketTaskCloseCode` | `Foundation.URLSessionWebSocketTaskCloseCode` | ? | Possibly missing |
| `Foundation.JSONEncoder` | `Foundation.JSONEncoder` | — | Not in .NET |
| `Foundation.NSNotificationName` | `Foundation.NSNotificationName` | — | Not in .NET |
| `Foundation.objc_AssociationPolicy` | `Foundation.objc_AssociationPolicy` | — | Not in .NET (ObjC runtime type) |

Swift renamed many Foundation types from NS-prefixed to modern names (e.g., `NSFileManager` → `FileManager`, `NSURLSessionTask` → `URLSessionTask`) in Swift 3+. The .NET iOS binding kept the ObjC names. The auto-bridge's `CreateObjCBridgedTypeRecord()` uses the Swift name directly, producing non-existent C# type references.

### Error Distribution (Alamofire)

| Missing Type | Occurrences | Context |
|-------------|:-----------:|---------|
| URLSessionTask | 80 | Properties, method params/returns, protocol proxies |
| NSNotificationName | 32 | Static notification name properties |
| URLSessionDownloadTask | 30 | Delegate methods, factory methods |
| URLSessionDataTask | 30 | Delegate methods, factory methods |
| URLSessionTaskMetrics | 18 | Property getters, delegate methods |
| InputStream | 16 | Multipart form upload APIs |
| HTTPURLResponse | 16 | Response properties and delegate methods |
| CachedURLResponse | 16 | Cache delegate protocol methods |
| FileManager | 10 | Upload/download file management |
| URLSessionWebSocketTaskMessage | 8 | WebSocket send/receive APIs |
| URLAuthenticationChallenge | 8 | Authentication delegate methods |
| Progress | 8 | Upload/download progress tracking |
| JSONEncoder | 6 | Custom encoder configuration |
| URLSessionWebSocketTaskCloseCode | 4 | WebSocket close handling |
| DateFormatter | 4 | Date formatting configuration |
| URLSessionWebSocketTask | 2 | WebSocket session APIs |

### Why This Wasn't Caught

The Session F validation measured **AnyType fallback reduction** (32→13 in Alamofire) rather than **C# compilation**. The auto-bridge resolved types that were previously AnyType (and thus had their members skipped), which was the intended goal. But the newly-emitted members referenced C# types that don't exist under the Swift name in .NET.

### Location in Code

- `TypeDatabaseExtensions.cs:504-516` — `AppleObjCFrameworkModules` allowlist (Foundation added)
- `TypeDatabaseExtensions.cs:524-566` — `AppleFrameworkValueTypes` exclusion list (incomplete)
- `TypeDatabaseExtensions.cs:633-661` — `CreateObjCBridgedTypeRecord()` (uses Swift name as C# name)
- `TypeDatabaseExtensions.cs:672-686` — `IsObjCModuleType()` (decision logic)

### Fix Approaches

**Option A: Swift→.NET name remapping table for Foundation classes**

Extend `AppleFrameworkTypeRemappings` (currently only covers 3 value types) to include Foundation class types. This is the analogous fix to how value types already handle name differences.

```
"Foundation.FileManager" → ("Foundation", "NSFileManager")
"Foundation.URLSessionTask" → ("Foundation", "NSUrlSessionTask")
... etc.
```

**Pro**: Precise, per-type control. **Con**: Large table to maintain; every new Foundation class needs an entry.

**Option B: Systematic NS-prefix heuristic**

For Foundation module types, apply a naming heuristic: if the type name doesn't start with "NS", prepend "NS" and convert URL-style casing to .NET conventions (`URLSession` → `NSUrlSession`).

**Pro**: Covers new types automatically. **Con**: Heuristic might not match all .NET naming conventions; some types genuinely don't exist in .NET.

**Option C: Add missing types to value-type exclusion list**

For types that don't exist in .NET at all (`JSONEncoder`, `NSNotificationName`, `objc_AssociationPolicy`), add them to `AppleFrameworkValueTypes` so they fall back to AnyType. This doesn't fix the naming issue for types that DO exist under different names.

**Pro**: Simple, targeted. **Con**: Only handles the "doesn't exist" cases, not the "wrong name" cases.

**Option D: Hybrid approach**

- Use a remapping table for Foundation types that exist in .NET under different names (Option A)
- Add non-existent types to the exclusion list (Option C)
- Long-term: ABI-driven classification using the `usr` field from ABI JSON (noted in code as KNOWN GAP)

---

## Root Cause 2: Pre-Existing AVFoundation/UIKit Gaps

**Existed since**: Before Session B (at least commit `46a5084`)
**Affected**: StripeCameraCore (12 errors, 3 types), StripeUICore (4 errors, 1 type)
**Never actually fixed**: The Pass 5 documentation claimed "12 AVCapture* errors → 0" and "2 NSWritingDirection errors → 0", but compilation testing at every commit shows these errors were always present.

### StripeCameraCore (12 errors)

| Missing Type | Occurrences | .NET Status |
|-------------|:-----------:|-------------|
| `AVFoundation.AVCaptureSessionPreset` | 4 | Not in .NET (string typedef in Swift, not a class) |
| `AVFoundation.AVCaptureDeviceAutoFocusRangeRestriction` | 4 | Not in .NET (enum, not a class) |
| `AVFoundation.AVCaptureDeviceDeviceType` | 4 | Not in .NET (struct/typedef) |

These AVFoundation types are auto-bridged as ObjC classes (AVFoundation is in `AppleObjCFrameworkModules`), but they're actually **value types or typedefs** in Swift/ObjC that don't exist as classes in .NET. They should be added to `AppleFrameworkValueTypes`.

### StripeUICore (4 errors)

| Missing Type | Occurrences | .NET Status |
|-------------|:-----------:|-------------|
| `UIKit.NSWritingDirection` | 4 | Not in .NET (ObjC enum, not a class) |

`NSWritingDirection` is an ObjC enum that gets auto-bridged as a UIKit class. It should be in `AppleFrameworkValueTypes`.

### Fix

Add these 4 types to `AppleFrameworkValueTypes`:
```csharp
"AVFoundation.AVCaptureSessionPreset",
"AVFoundation.AVCaptureDeviceAutoFocusRangeRestriction",
"AVFoundation.AVCaptureDeviceDeviceType",
"UIKit.NSWritingDirection",
```

This will cause them to fall back to AnyType, which means the members using these types will be skipped. This is correct behavior — these types genuinely cannot be marshalled without dedicated support.

---

## Documentation Accuracy Issues

The `binding-errors.md` document contains several inaccuracies identified by this investigation:

1. **"25 of 25 libraries at 0 generator errors"** — Alamofire, SkeletonView, StripeCameraCore, and StripeUICore all had compile errors at the claimed baseline. Actual clean count was 21-22 depending on commit.

2. **"Environmental errors eliminated (35 → 0)"** — Pass 5 notes claim SkeletonView went from 9 `NSTextAlignment` errors to 0. The NSTextAlignment errors were indeed fixed, but 4 new `objc_AssociationPolicy` errors were introduced in the same commit. Net: 9→4, not 9→0.

3. **"StripeCameraCore: 12 AVCapture* errors → 0"** — Never fixed. 12 errors present at every commit tested.

4. **"StripeUICore: 2 NSWritingDirection errors → 0"** — Never fixed. 4 errors present at every commit tested (grew to 8 at Session F, back to 4 at HEAD).

### Likely explanation

The previous validation sessions measured "generator errors" as distinct from "environmental errors" and may have used a methodology that didn't actually compile the C# output for libraries whose wrapper compilation failed. The auto-bridge reduced AnyType counts (a genuine improvement), and the reduction was reported as "errors eliminated" without a compilation check.

---

## Corrected Baselines

### Actual compile results (Feb 18, HEAD `cdbf8d5`)

| Tier | Libraries | Count |
|------|-----------|:-----:|
| Clean (0 CS errors) | Nuke, BlinkID, CryptoSwift, Lottie, ACSSmartCardIO, BRLMPrinterKit, Mappedin, MicroblinkPlatform, Mixpanel, SmartCardIO, BlinkIDUX, StripeApplePay, StripeCardScan, StripeConnect, StripeCore, StripeCryptoOnramp, StripeFinancialConnections, StripeIdentity, StripeIssuing, StripePayments, StripePaymentSheet, StripePaymentsUI | 22 |
| Naming mismatch (Session F) | Alamofire (294), SkeletonView (4) | 2 |
| Missing value-type exclusions (pre-existing) | StripeCameraCore (12), StripeUICore (4) | 2 |

### Wrapper compilation (separate from C# compile)

These libraries fail Swift wrapper compilation (expected, not generator bugs):
- Alamofire (1 error: internal `WebSocketTask` type)
- SkeletonView (internal `SkeletonLayer` type)
- Mixpanel (internal `ServerProxyResource` type)
- BlinkIDUX (dependency imports)
- All Stripe sub-modules (inter-module `import` dependencies)
- ACSSmartCardIO (dependency on SmartCardIO)

---

## Recommended Fix Priority

1. **Quick win — value-type exclusions** (Root Cause 2): Add 4 types to `AppleFrameworkValueTypes`. Fixes StripeCameraCore (12→0) and StripeUICore (4→0). Minimal code change, no risk.

2. **Medium — Foundation class name remapping** (Root Cause 1): Build a remapping table for Foundation types that exist in .NET under ObjC names. Fixes the ~12 types in Alamofire that DO have .NET equivalents. Alamofire errors would drop significantly (exact count depends on how many of the 16 types have .NET equivalents).

3. **Medium — Non-existent type exclusions** (Root Cause 1 subset): Add Foundation types with no .NET equivalent (`JSONEncoder`, `NSNotificationName`, `objc_AssociationPolicy`) to the exclusion list. These become AnyType, and members using them get skipped.

4. **Long-term — ABI-driven classification**: Use the `usr` field from ABI JSON to determine class-vs-struct, eliminating the need for hand-maintained exclusion lists. Noted in code as KNOWN GAP at `TypeDatabaseExtensions.cs:499-502`.

5. **Update `binding-errors.md`**: Correct the baseline documentation to reflect actual compile results.
