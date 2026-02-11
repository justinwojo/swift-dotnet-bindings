# Phase 10: Remaining Binding Gap Fixes (2026-01-30)

**Status**: COMPLETE

This phase implemented infrastructure for closure properties with frozen struct parameters, AsyncStream handling, and async closure support.

---

## 10.1 Frozen Struct Closure Parameters ✅
**Status**: DONE

Extended closure handling to support frozen structs as closure parameters.

**Problem**: Closure properties like `onDiskSizeLimit` with frozen struct parameters weren't emitting because `CanInvokeFromCSharp()` rejected non-primitive types.

**Solution**:
- Added `ClosureHandler.IsFrozenStruct()` - detects frozen structs in TypeDatabase
- Added `ClosureHandler.RequiresStructMarshalling()` - checks if closure needs struct marshalling
- Added `ClosureEmitter.EmitClosureReturnMarshallingWithStructParams()` - generates stackalloc + MarshalToSwift code
- Updated `IsInvocableParameter()` to allow frozen structs

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs`

**Limitation**: Only supports frozen structs. Non-frozen structs like `ImageDecodingContext` require additional opaque payload handling (addressed in Phase 11).

---

## 10.2 AsyncStream Infrastructure ✅
**Status**: DONE

Created foundation for AsyncStream property support.

**Problem**: Properties returning `AsyncStream<T>` (like `progress`, `previews`, `events`) had no handling infrastructure.

**Solution**:
- Created `AsyncStreamHandler.cs` - detects `_Concurrency.AsyncStream` and `AsyncThrowingStream` types
- Created `SwiftAsyncStream<T>.cs` - runtime type implementing `IAsyncEnumerable<T>` with Channel-based buffering
- Added `AsyncStreamHandler` to `PropertyEnvironment`

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/AsyncStreamHandler.cs` (new)
- `src/Swift.Runtime/src/Swift/SwiftAsyncStream.cs` (new)
- `src/Swift.Bindings/src/Marshaler/IEnvironment.cs`

**Note**: PropertyHandler emission of AsyncStream properties with Swift wrapper generation was completed in Phase 11.

---

## 10.3 Async Closure Support ✅
**Status**: DONE

Extended closure handling to support async closures.

**Problem**: Closures with async return types (like `() async -> Void`) were rejected as unsupported.

**Solution**:
- Updated `IsSupportedClosure()` to allow async closures (only rejects async+throwing)
- Updated `GetCSharpDelegateType()` to map async closures to `Func<..., Task>` or `Func<..., Task<T>>`
- Added `IsAsyncClosure()` helper method

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`

**Note**: Properties with async closures may still be skipped if their accessor methods have other unsupported types.

---

## Test Results

```
Unit tests:        569 passed (+30 from Phase 9)
Integration tests: 691 passed
Runtime tests:      72 passed
```

---

## Summary

Phase 10 established the infrastructure for:
- Frozen struct parameters in closures
- AsyncStream type detection
- Async closure delegate mapping

This laid the groundwork for Phase 11's full property emission support.
