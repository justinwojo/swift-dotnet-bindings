# Library Validation Status

**Date**: 2026-02-22
**Baseline**: 32/32 libraries passing (3988 unit + 700 integration tests)

All validation libraries compile with 0 errors.

## Recent Fixes (this session)

### Bug 1: Throwing/non-throwing closure void* return for non-frozen structs
Closure callbacks returning non-frozen structs or ObjC classes as `void*` now marshal via
`TypeMetadata.GetTypeMetadataOrThrow<T>()` + `NativeMemory.Alloc` + `SwiftMarshal.MarshalToSwift`.
Affected: GRDB (8 errors), Kingfisher (1 error).

### Bug 2: Async TCS wrapping throwing closures
`CompletionHandlerDetector.IsCompletionHandler` now excludes throwing closures (`closureSpec.Throws`).
Throwing closures project to `Func<SwiftResult<T, SwiftError>>` with non-void return, making the
TCS lambda incompatible. Affected: GRDB (3 errors).

### Bug 3: SwiftString not projected to string in closure delegates
`ClosureHandler.TranslateTypeSpecToCSharp` now checks `MarshallingHelpers.IsSwiftString` before
typeRecord lookup, returning `"string"` instead of `"SwiftString"`. Affected: GRDB (1 error).

### Bug 4: Dictionary covariance in protocol proxy receivers
Two sub-fixes: (a) `GetReceiverDictionaryConversion` adds explicit public type casts in `.ToDictionary()`;
(b) `OptionalProjection.GetReturnElementConversion` converts `SwiftOptional<T>` → `T?` for dict values;
(c) `DictionaryProjection.GetReturnElementConversion` enables array-of-dictionary receiver conversion.
Affected: GRDB (2 errors).

### Bug 5: UIViewAnimationOptions enum in SwiftObjectHelper
Apple framework value types (remapped structs/enums like UIViewAnimationOptions) now use
`TypeMetadata.GetTypeMetadataOrThrow<T>()` instead of `SwiftObjectHelper<T>.GetTypeMetadata()`.
`TypeDatabaseExtensions.IsRemappedAppleValueType` made internal for access from EnumHandler.
Affected: Kingfisher (1 error).

### Bug 6: Ternary covariance in optional container setter receivers
`GetReceiverOptionalContainerSetterConversion` now casts the some arm to the idiomatic type.
Affected: Mixpanel (1 error).

### Bug 7: OpaquePointer void* → IntPtr in closure callbacks
`GetInvokeArgExpression` now handles pointer types (OpaquePointer, etc.) that are `void*` in
the callback but `IntPtr` in the delegate, using `new IntPtr(argN)`. Affected: GRDB (1 error).

### Bug 8: Pointer-return closure ABI regression guard (code review fix)
The Bug 1 void* marshalling branches also matched pointer return types (OpaquePointer,
UnsafeRawPointer, etc.) which should return the raw pointer value, not a buffer address.
Added `IsPointerType` guard before the buffer-allocation branch in both throwing and
non-throwing callback paths — pointer returns now emit `return (void*)result` directly.

## Previous Fix: Unconditional Factory Routing for Protocol Proxy ABI Types

`GetCSharpTypeName(forAbiMarshalling: true)` unconditionally routes through `TypeProjectionFactory`.
Uses `projection.MarshalFromSwiftType` for ABI types and `projection.PublicType` for public types.
Recovered 4 libraries: Alamofire, StripeCore, StripePaymentsUI, StripeUICore.
