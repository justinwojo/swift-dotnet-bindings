# Remaining Library Validation Errors

**Date**: 2026-02-22
**Baseline**: 29/32 libraries passing (3986 unit + 700 integration tests)

## Recent Fix: Unconditional Factory Routing for Protocol Proxy ABI Types

**Fixed**: `GetCSharpTypeName(forAbiMarshalling: true)` now unconditionally routes through
`TypeProjectionFactory` for all types. Uses `projection.MarshalFromSwiftType` for ABI types
and `projection.PublicType` for public types.

**Projection fixes** (safe for both protocol proxy and container composition):
- `NativeRemappedProjection`: Added `MarshalFromSwiftType => _swiftWrapperType` (was SafeHandle).
  Also fixed `GetReturnElementConversion` to not re-wrap elements that are already the wrapper type.
- `OptionalProjection`: Added `MarshalFromSwiftType => ContainerTypeName` (was SwiftOptional<IntPtr>)
- `OverrideOptionalExistentialAbiType` removed (factory handles Optional<existential>)

**Receiver conversion handling** (for types where MarshalFromSwiftType = IntPtr):
- ObjC bridged types: `MarshalFromSwift<IntPtr>` + `GetNSObject<T>()` conversion added to
  `GetReceiverSetterConversion`, `GetReceiverGetterConversion`, and Optional setter/getter.
  Using `MarshalFromSwiftType => _csharpTypeName` would crash at runtime (ObjC classes lack Swift metadata).

**Impact**: GRDB 27→22 errors, Kingfisher 3→2, Mixpanel 1 (unchanged).
Alamofire, StripeCore, StripePaymentsUI, StripeUICore all fixed (4 libraries recovered).

## Failing Libraries

### 1. GRDB — 22 errors

| Category | Count | Root Cause |
|----------|-------|------------|
| Throwing closure return type mismatch | ~10 | Swift closures that `throws` get projected as `Func<..., SwiftResult<T, SwiftError>>`. Async wrapper template calls `tcs.TrySetResult(result)` (returns `bool`) where the delegate expects `SwiftResult`. |
| Non-frozen struct to void* return | ~8 | Closure callbacks returning non-frozen structs emit the struct type where `void*` is expected. |
| Dictionary Optional covariance | ~2 | `IReadOnlyDictionary<string, SwiftOptional<DatabaseValue>>` vs `IReadOnlyDictionary<string, DatabaseValue?>`. |
| Dictionary RowAdapter covariance | ~1 | `Dictionary<string, RowAdapterProxy>` vs `IDictionary<string, IRowAdapter>`. |
| Closure SwiftString to string projection | ~1 | `Action<ResultCode, SwiftString>` can't convert to `Action<ResultCode, string>`. |

### 2. Kingfisher — 2 errors

| Category | Count | Root Cause |
|----------|-------|------------|
| UIImage to void* closure return | 1 | Closure returning UIImage (ObjC class) emits the class type where `void*` is expected. |
| UIViewAnimationOptions in SwiftObjectHelper | 1 | .NET enum (UIKit) in tuple metadata requires `SwiftObjectHelper<T>.GetTypeMetadata()` but the type doesn't implement `ISwiftObject`. |

### 3. Mixpanel — 1 error

| Category | Count | Root Cause |
|----------|-------|------------|
| Dictionary ternary covariance | 1 | `IReadOnlyDictionary<string, IMixpanelType>` vs `IReadOnlyDictionary<string, MixpanelTypeProxy>` in conditional expression. Both types are correct (existential resolved properly) but C# can't infer the common type. |
