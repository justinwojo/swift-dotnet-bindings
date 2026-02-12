# Binding Errors by Library

Tracks compilation errors found when running real-world Swift libraries through the generator. Used to prioritize bug fixes and measure progress.

Last validated: 2026-02-12.

## Baseline Libraries (0 generator errors)

| Library | Lines | Notes |
|---------|-------|-------|
| Nuke | 20,766 | Image loading, async/await, ObjC bridging, heavy protocol use |
| CryptoSwift | 27,981 | Value types, frozen structs, byte arrays |
| BlinkID | 50,864 | ObjC-heavy, delegates, callback-driven API |
| Mappedin | 49,043 | Indoor mapping, largest library tested, clean on first try |
| SmartCardIO | 3,912 | Smart card reader abstraction, clean build |
| BRLMPrinterKit | 53 | Mostly ObjC with thin Swift overlay |
| Lottie | 28,896 | Animation framework, protocol-heavy |
| ACSSmartCardIO | 2,840 | Smart card reader (C# bindings compile clean; wrapper fails due to missing SmartCardIO dependency) |
| Stripe | 449 | Top-level Stripe framework, minimal Swift surface |
| StripeApplePay | 1,879 | Apple Pay integration |
| StripeCardScan | 2,560 | Card scanning |
| StripeIdentity | 1,717 | Identity verification |
| StripeIssuing | 1,271 | Card issuing |
| Alamofire | 36,474 | HTTP networking, async/await, closures (B5 fixed) |
| Mixpanel | 7,040 | Analytics, protocol existentials (B6, B7 fixed) |
| StripePaymentSheet | 45,702 | Payment UI, Result<Void,Error> (B8 fixed) |
| StripeCore | 31,444 | Core Stripe infrastructure (B9, B10 fixed) |
| StripeConnect | 12,012 | Connect platform (B11 fixed) |
| StripeCryptoOnramp | 6,128 | Crypto onramp (B12, B13 fixed) |
| StripeFinancialConnections | 2,223 | Financial connections (B14 fixed) |
| StripePayments | 88,697 | Payments (B15, B16, B17 fixed) |
| StripeUICore | 28,719 | UI core (B18 fixed; 4 environmental UIKit.NSWritingDirection remain) |
| MicroblinkPlatform | 4,522 | BlinkID platform layer (B19 fixed; SwiftUI members now skipped at member level) |

## Libraries with Environmental Errors Only

### SkeletonView (18 errors, 11,743 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 18 | CS0234 | `UIKit.NSTextAlignment` not found (environmental) |

All errors are environmental: `NSTextAlignment` doesn't exist in the .NET iOS SDK's `UIKit` namespace. The generator correctly maps the Apple framework type but the C# binding for it is missing from .NET. Requires .NET iOS SDK additions (out of scope).

### StripePaymentsUI (6 errors, 12,889 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 6 | CS0234 | `UIKit.NSTextAlignment` not found (environmental) |

Same `NSTextAlignment` environmental issue as SkeletonView.

### StripeCameraCore (12 environmental errors, 3,485 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 4 | CS0234 | `AVFoundation.AVCaptureDeviceDeviceType` not found (environmental) |
| 4 | CS0234 | `AVFoundation.AVCaptureDeviceAutoFocusRangeRestriction` not found (environmental) |
| 4 | CS0234 | `AVFoundation.AVCaptureSessionPreset` not found (environmental) |

All 12 errors are environmental: AVFoundation types missing from .NET iOS SDK. Generator B10 errors previously here are now fixed.

### StripeUICore (4 environmental errors, 28,719 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 4 | CS0234 | `UIKit.NSWritingDirection` not found (environmental) |

Generator B18 errors (18 `.Buffer` return type) are now fixed. Only 4 environmental errors remain.

## Non-Binding Failures

### SkeletonView (wrapper compilation failure)

C# binding generation succeeds (11.7K lines), but Swift wrapper compilation fails because `SkeletonLayer` is an **internal** class referenced in wrapper code. The wrapper generator emits Swift code referencing this type, but `swiftc` compiling against the public interface can't see it.

**Fix approach**: `SwiftWrapperPostProcessor` should filter out wrapper functions that reference internal types.

### RealmSwift (generator crash)

The ABI JSON has an empty module name — built without `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`. Generator throws `InvalidOperationException` with a clear error message about requiring library evolution.

### Realm (no Swift module)

Pure Objective-C framework — no Swift module found. Correctly rejected with user-friendly message.

### Stripe3DS2 (no Swift module)

Pure Objective-C framework — no Swift module found.

### ACSSmartCardIO (wrapper compilation failure)

Depends on `SmartCardIO` framework. Wrapper compilation fails because `swiftc` can't find the dependency module.

**Fix approach**: Add `SwiftFrameworkDependency` item type for `-F` search paths (v2 feature).

### Mixpanel (wrapper compilation failure)

Swift wrapper compilation fails because `ServerProxyResource` is not a public member of `Mixpanel.Mixpanel`. The swiftinterface references this type in a `#if compiler` block.

### Stripe sub-frameworks (wrapper compilation failures)

Most Stripe sub-frameworks fail wrapper compilation because they `import StripeCore` (or other Stripe modules) and `swiftc` can't find these dependencies. C# binding generation succeeds for all. Same root cause as ACSSmartCardIO — needs `-F` search path support.

Affected: Stripe, StripeApplePay, StripeCameraCore, StripeCardScan, StripeConnect, StripeCore, StripeCryptoOnramp, StripeFinancialConnections, StripeIdentity, StripeIssuing, StripePayments, StripePaymentSheet, StripePaymentsUI, StripeUICore.

## Fixed Bug Patterns

### Validation Pass 3 (2026-02-12) — 228 errors fixed

| ID | Pattern | Errors Fixed | Libraries | Fix |
|----|---------|-------------|-----------|-----|
| B5 | Optional tuple with existential element | 4 | Alamofire | `HasNonSwiftObjectGenericArg` extended to check tuple elements inside Optional for unresolvable existentials |
| B6 | Dictionary existential generic arg mismatch | 20 | Mixpanel | `TryGetFirstExistentialTypeArgument` guard in MethodHandler + MemberEmissionValidator for non-Array bound generics (Array<any P> allowed — has dedicated marshalling) |
| B7 | Closure thunk return void* vs struct | 4 | Mixpanel | `IsSupportedClosureReturnType` rejects bound generic returns with `RequiresMemoryManagement` inner types |
| B8 | `void` as generic type arg (Result<Void,Error>) | 30 | StripePaymentSheet | `Swift.Void` → `SwiftVoid` mapping extended to `ClosureHandler.TranslateBoundGenericToCSharp` |
| B9 | Existential→interface in proxy receiver | 2 | StripeCore | Protocol methods with existential params added to `_skippedMethodKeys` |
| B10 | Protocol proxy receiver type asymmetry | 4 | StripeCore (2), StripeCameraCore (2) | `GetReturnConversion` applied after unmarshalling in receiver to convert ABI→idiomatic type |
| B11 | DateTimeOffset in SwiftObjectHelper | 4 | StripeConnect | `HasNonSwiftObjectGenericArg` checks TypeRecord `NativeTypeName` for .NET-mapped types |
| B12 | ObjC-bridged type treated as Swift class | 18 | StripeCryptoOnramp | Existing `IsObjCBridgedType` guards now catch module-qualified ObjC types; belt-and-suspenders guard in `EmitTypeConversions` for Optional<ObjC> |
| B13 | Async closure arity mismatch | 2 | StripeCryptoOnramp | `IsSupportedClosure` rejects async+throwing closures with parameters |
| B14 | Duplicate P/Invoke parameter name | 2 | StripeFinancialConnections | `DeduplicateParameterNames` with HashSet-based collision avoidance |
| B15 | Duplicate async method after normalization | 6 | StripePayments | Secondary dedup in `HandleBaseDecl` based on projected C# public method signature |
| B16 | Non-blittable enum in UnmanagedCallersOnly | 4 | StripePayments | `IsSupportedClosureParameterType` rejects enum types |
| B17 | INSObject composition for ObjC root type | 2 | StripePayments | `GetCompositionInterfaceName` filters out ObjC root protocols |
| B18 | Enum .Buffer return type doesn't exist | 18 | StripeUICore | `CanEmitMethod`/`CanEmitProperty` skip non-simple enum returns requiring memory management |
| B19 | SwiftUI namespace in main binding | 108 | MicroblinkPlatform | `ReferencesUnsupportedModule` member-level check in `CanEmitMethod`/`CanEmitProperty` |
| **Total** | | **228** | | |

### Validation Pass 2 (2026-02-12) — 35 errors fixed

| Bug Pattern | Errors Fixed | Libraries | Fix |
|-------------|-------------|-----------|-----|
| B3 gap: `Swift.Void` as NamedTypeSpec | 15 | StripePaymentSheet | `NamedTypeSpec("Swift.Void")` → `SwiftVoid` mapping in BoundGenericsHandler |
| A4: Bare generic types | 6 | Alamofire (4), Mixpanel (2) | Two-layer bare generic detection: module-local TypeDecl lookup + stdlib fallback set |
| Generic constraint mismatch | 8 | StripeCore (4), SkeletonView (4) | Context-aware `HasNonSwiftObjectGenericArg` guard: blocks tuples (except Optional) and ObjC-bridged types |
| A6: AnyType type erasure dedup | 2 | Alamofire | Three-layer protocol method dedup: Swift signature → projected C# → emitted resolution |
| Duplicate `_` parameters | 4 | Lottie | `GetCSharpParameterName` derives name from type for `_` params + `DeduplicateParameterNames` in protocol emission |

## Remaining Environmental (out of scope)

- `UIKit.NSTextAlignment` missing from .NET iOS SDK (24 errors across SkeletonView + StripePaymentsUI).
- `UIKit.NSWritingDirection` missing from .NET iOS SDK (4 StripeUICore errors).
- `AVFoundation.AVCaptureDeviceDeviceType` / `AVCaptureDeviceAutoFocusRangeRestriction` / `AVCaptureSessionPreset` missing from .NET iOS SDK (12 StripeCameraCore errors).
