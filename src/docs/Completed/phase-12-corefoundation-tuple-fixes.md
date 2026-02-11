# Phase 12: CoreFoundation Module Aliasing and TupleHandler Improvements

## Summary

This phase addressed two key issues preventing proper type resolution:
1. CGSize types appearing as `AnyType` due to module name mismatch
2. Tuples containing bound generic types (like `Optional<T>`) being rejected

## Changes Made

### 1. Module Aliasing for CoreFoundation → CoreGraphics

**File:** `src/Swift.Bindings/src/TypeDatabase/TypeDatabase.cs`

**Problem:** CGSize appears in ABI JSON as `CoreFoundation.CGSize` but is registered in the database under `CoreGraphics` module.

**Solution:** Added module aliasing to TypeDatabase:
- New `_moduleAliases` dictionary mapping `CoreFoundation` → `CoreGraphics`
- Updated `TryGetTypeRecord` to try aliased module when direct lookup fails
- Updated `IsTypeProcessed` to use module aliasing

**Result:**
- `ImageProcessors.Resize` constructor now has `Swift.CGSize size` instead of `AnyType size`
- `ImageRequest.ThumbnailOptions` constructor now has `Swift.CGSize size` instead of `AnyType size`

### 2. TupleHandler Support for Bound Generic Types

**File:** `src/Swift.Bindings/src/Marshaler/TupleHandler.cs`

**Problem:** Tuples containing `Optional<T>`, `Array<T>`, or other bound generic types were rejected because `IsSupportedTupleElementType` returned false for any type with generic parameters.

**Solution:**
- Added `IsSupportedGenericTupleElement` method to handle bound generic types
- Base type must be in database, generic parameters recursively validated
- Supports existential generic parameters (e.g., `Optional<any Protocol>`)
- Added `TranslateBoundGenericToCSharp` for proper C# type translation
- Relaxed frozen-only restriction (non-frozen types can be wrapped)

**Result:**
- `loadData` completion callback now shows proper types: `Action<SwiftResult<(Swift.Data data, SwiftOptional<Foundation.NSUrlResponse> response), Error>>`
- Tuples with `Optional<T>` elements now work in non-async contexts

### 3. Async Tuple Return Type Exclusion

**File:** `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**Problem:** Tuples were being marked as "supported" but async wrapper code couldn't handle tuple return types, causing runtime exceptions.

**Solution:**
- Added `!_env.MethodDecl.IsAsync` check to both tuple return type handlers (wrapper signature and P/Invoke signature)
- Async methods with tuple returns now fall back to `AnyType` instead of crashing

**Result:**
- `data(for:)` method properly shows as unsupported instead of crashing
- Tuple returns work for non-async methods

## Remaining Gaps

After these fixes, the following still show as `AnyType`:

| Issue | Type | Reason |
|-------|------|--------|
| `data(_for:)` return | `(Data, URLResponse?)` | Async tuple returns not yet supported |
| `progress` closure param | `(ImageResponse?, Int64, Int64) -> ()` | `ImageResponse` is module-local type, not in global database |
| `imagePublisher` return | `AnyPublisher<...>` | Combine framework out of scope |
| `ImageRequest` init `data` | `() async throws -> Data` | Throwing closures not supported |

## Test Results

All 1,354 tests passing (591 unit, 691 integration, 72 runtime).
