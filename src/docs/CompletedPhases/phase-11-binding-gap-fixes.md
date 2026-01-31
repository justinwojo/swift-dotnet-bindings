# Phase 11: Advanced Binding Gap Fixes (2026-01-31)

**Status**: COMPLETE

This phase implemented non-frozen struct closure parameters, AsyncStream property emission, and @MainActor closure infrastructure.

---

## 11.1 AsyncStream Property Emission ✅
**Status**: DONE

Completed emission of properties returning `AsyncStream<T>`.

**Problem**: Properties like `progress`, `previews`, and `events` return `AsyncStream<T>`, which requires Swift wrapper functions to iterate and callback to C#.

**Solution**:
- Added `AsyncStreamHandler.GetSwiftWrapperFunctionName()` and `GetCSharpElementType()` helpers
- Created `AsyncStreamEmitter.cs` with emission helpers for callbacks and Swift wrappers
- Added `PropertyHandler.EmitAsyncStreamProperty()` with `IAsyncEnumerable<T>` return type
- Swift wrapper iterates the AsyncStream and calls back for each element

**Generated Pattern**:
```swift
// Swift wrapper
public func progress_get_wrapper_YYY(callback: @escaping (Int64) -> Void) async {
    for await element in self.progress {
        callback(element.completed)
    }
}
```

```csharp
// C# property
public IAsyncEnumerable<long> Progress => GetProgressAsync();

private async IAsyncEnumerable<long> GetProgressAsync() {
    var channel = Channel.CreateUnbounded<long>();
    // Call Swift wrapper with callback that writes to channel
    // ...
}
```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/AsyncStreamEmitter.cs` (new - 163 lines)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`
- `src/Swift.Bindings/src/Marshaler/AsyncStreamHandler.cs`

**Properties now emitted**: `progress`, `previews`, `events`

---

## 11.2 Non-Frozen Struct Closure Parameters ✅
**Status**: DONE

Extended closure handling to support non-frozen structs as closure parameters.

**Problem**: Closure properties like `makeImageDecoder` have non-frozen struct parameters (e.g., `ImageDecodingContext`) that require different marshalling than frozen structs.

**Solution**:
- Added `ClosureHandler.IsNonFrozenStruct()` - detects non-frozen structs
- Added `ClosureHandler.RequiresNonFrozenMarshalling()` - checks if closure needs non-frozen handling
- Updated `IsInvocableParameter()` to support non-frozen structs via `ISwiftObject` interface
- Added `ClosureEmitter.EmitClosureReturnMarshallingWithNonFrozenParams()` with NativeMemory allocation

**Generated Pattern**:
```csharp
// For non-frozen struct parameters, allocate opaque payload
var param1Memory = NativeMemory.AllocZeroed((nuint)param1TypeMetadata.Size);
try {
    // Marshal ISwiftObject to native memory
    SwiftMarshal.MarshalToSwift(param1, (IntPtr)param1Memory, param1TypeMetadata);
    // Invoke the closure
    var result = closurePtr((IntPtr)param1Memory);
    return result;
} finally {
    NativeMemory.Free(param1Memory);
}
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` (+151 lines)
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` (+153 lines)

**Properties now emitted**: `makeImageDecoder`, `makeImageEncoder`

---

## 11.3 @MainActor Closure Property Infrastructure ✅
**Status**: INFRASTRUCTURE DONE

Added detection and handling infrastructure for @MainActor closures.

**Problem**: Properties like `didComplete` have closure types with `@MainActor` and `@Sendable` attributes plus Optional wrapping: `(@MainActor @Sendable () async -> Void)?`

**Solution**:
- Added `ClosureHandler.IsMainActor()` - detects @MainActor attribute on closures
- Added `ClosureHandler.IsSendable()` - detects @Sendable attribute
- Added `IsOptionalClosure()` - detects `Optional<Closure>` types
- Added `GetCSharpOptionalDelegateType()` - maps to nullable delegates
- Updated `IsClosure()` and `GetClosureTypeSpec()` to handle Optional-wrapped closures

**Limitation**: The `didComplete` property accessor method has an unsupported signature that prevents emission. The closure infrastructure is ready, but the accessor needs additional work.

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`

---

## Warning Summary

| Property | Phase 10 Status | Phase 11 Status |
|----------|-----------------|-----------------|
| progress | Infrastructure | ✅ EMITTED |
| previews | Infrastructure | ✅ EMITTED |
| events | Infrastructure | ✅ EMITTED |
| makeImageDecoder | Frozen only | ✅ EMITTED |
| makeImageEncoder | Frozen only | ✅ EMITTED |
| didComplete | Deferred | Infrastructure ready, accessor unsupported |

**Result**: 5 of 6 deferred properties now emit correctly.

---

## Test Results

```
Unit tests:        591 passed (+22 from Phase 10)
Integration tests: 691 passed
Runtime tests:      72 passed
Total:           1,354 tests passing
```

---

## Files Modified

| File | Changes |
|------|---------|
| `AsyncStreamEmitter.cs` | New: 163 lines for AsyncStream property emission |
| `ClosureEmitter.cs` | +153 lines for non-frozen struct marshalling |
| `MethodHandler.cs` | +11 lines minor adjustments |
| `PropertyHandler.cs` | +59 lines AsyncStream property emission |
| `AsyncStreamHandler.cs` | +25 lines helper methods |
| `ClosureHandler.cs` | +151 lines @MainActor, Optional closure, non-frozen support |

---

## Current Binding State

- **18,437 lines** of C# code generated
- **~30 classes** implementing ISwiftObject
- **8 protocol interfaces** with full type information
- **8 protocol proxy classes** with witness table export
- **1 remaining skipped property**: `didComplete` (accessor signature unsupported)

---

## Summary

Phase 11 completed:
- AsyncStream property emission with IAsyncEnumerable<T> return type
- Non-frozen struct closure parameter marshalling
- @MainActor closure detection infrastructure

Reduced skipped properties from 6 to 1 (83% reduction from Phase 9's deferred items).
