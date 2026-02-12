# Binding Errors by Library

Tracks compilation errors found when running real-world Swift libraries through the generator. Used to prioritize bug fixes and measure progress.

Last validated: 2026-02-12.

## Baseline Libraries (0 errors)

| Library | Lines | Notes |
|---------|-------|-------|
| Nuke | 21,344 | Image loading, async/await, ObjC bridging, heavy protocol use |
| CryptoSwift | 27,981 | Value types, frozen structs, byte arrays |
| BlinkID | 50,864 | ObjC-heavy, delegates, callback-driven API |
| Mappedin | 49,112 | Indoor mapping, largest library tested, clean on first try |
| SmartCardIO | 3,912 | Smart card reader abstraction, clean build |
| BRLMPrinterKit | 53 | Mostly ObjC with thin Swift overlay |
| Lottie | 28,896 | Animation framework, protocol-heavy |
| Alamofire | 36,527 | Networking, generics-heavy, protocol monitors |
| StripePaymentSheet | 47,189 | Payments, Result<Void, Error> patterns |
| StripeCore | 31,659 | Payments core, generic Future types |
| Mixpanel | 7,066 | Analytics, dictionary type aliases |

## Libraries with Environmental Errors Only

### SkeletonView (9 errors, 12,590 lines)

| Count | CS Code | Category |
|-------|---------|----------|
| 9 | CS0234 | `UIKit.NSTextAlignment` not found (environmental) |

All 9 errors are environmental: `NSTextAlignment` doesn't exist in the .NET iOS SDK's `UIKit` namespace. The generator correctly maps the Apple framework type but the C# binding for it is missing from .NET. Requires .NET iOS SDK additions (out of scope).

## Recently Fixed (2026-02-12)

35 generator-caused errors fixed across 6 libraries in 5 bug patterns:

| Bug Pattern | Errors Fixed | Libraries | Fix |
|-------------|-------------|-----------|-----|
| B3 gap: `Swift.Void` as NamedTypeSpec | 15 | StripePaymentSheet | `NamedTypeSpec("Swift.Void")` → `SwiftVoid` mapping in BoundGenericsHandler |
| A4: Bare generic types | 6 | Alamofire (4), Mixpanel (2) | Two-layer bare generic detection: module-local TypeDecl lookup + stdlib fallback set |
| Generic constraint mismatch | 8 | StripeCore (4), SkeletonView (4) | Context-aware `HasNonSwiftObjectGenericArg` guard: blocks tuples (except Optional) and ObjC-bridged types |
| A6: AnyType type erasure dedup | 2 | Alamofire | Three-layer protocol method dedup: Swift signature → projected C# → emitted resolution |
| Duplicate `_` parameters | 4 | Lottie | `GetCSharpParameterName` derives name from type for `_` params + `DeduplicateParameterNames` in protocol emission |

## Non-Binding Failures

### SkeletonView (wrapper compilation failure)

C# binding generation succeeds (12.6K lines), but Swift wrapper compilation fails because `SkeletonLayer` is an **internal** class referenced in wrapper code. The wrapper generator emits Swift code referencing this type, but `swiftc` compiling against the public interface can't see it.

**Fix approach**: `SwiftWrapperPostProcessor` should filter out wrapper functions that reference internal types.

### RealmSwift (generator crash)

The ABI JSON contains `"name": "NO_MODULE"` — built without `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`. Generator crashes with `ArgumentException`.

**Fix approach**: Detect `NO_MODULE` early and emit a user-friendly error.

### ACSSmartCardIO (wrapper compilation failure)

Depends on `SmartCardIO` framework. Wrapper compilation fails because `swiftc` can't find the dependency module.

**Fix approach**: Add `SwiftFrameworkDependency` item type for `-F` search paths (v2 feature).

## Remaining Bug Patterns

### Environmental (out of scope)
- `UIKit.NSTextAlignment` missing from .NET iOS SDK (9 SkeletonView errors).
