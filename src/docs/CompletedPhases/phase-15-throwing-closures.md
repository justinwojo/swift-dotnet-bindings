# Phase 15: Throwing Closures Support

## Summary

Added support for Swift closures that can throw errors. Throwing closures are mapped to C# delegates returning `SwiftResult<T, SwiftError>`.

## Problem

Prior to this phase, closures with the `throws` attribute were explicitly blocked in `ClosureHandler.IsSupportedClosure()`. This prevented binding generation for methods/constructors that accept throwing closures as parameters, such as:

```swift
// ImageRequest constructor with throwing closure
init(id: String, data: @escaping () async throws -> Data, ...)
```

## Solution

### Type Mapping

| Swift Closure Type | C# Delegate Type |
|-------------------|------------------|
| `() throws -> Void` | `Func<SwiftResult<SwiftVoid, SwiftError>>` |
| `(Int) throws -> Bool` | `Func<long, SwiftResult<bool, SwiftError>>` |
| `() throws -> Data` | `Func<SwiftResult<Data, SwiftError>>` |

### Implementation

1. **SwiftVoid.cs** - Unit type for void-returning throwing closures
2. **ClosureHandler.cs** - Type mapping and support detection
3. **ClosureEmitter.cs** - Callback and marshalling code generation
4. **MethodHandler.cs** - Integration with method emission
5. **SwiftResult.cs** - Added `FromSuccess()` and `FromFailure()` factory methods

### Error Flow

**C# to Swift (passing throwing delegate):**
```
C# Func<A, SwiftResult<B, SwiftError>> delegate
    → [UnmanagedCallersOnly] Callback(A arg, SwiftError* errorOut, SwiftSelf context)
    → Call delegate(arg) → SwiftResult<B, SwiftError>
    → If failure: *errorOut = error, return default
    → If success: *errorOut = null, return value
    → Swift receives value + error in separate registers
```

**Swift to C# (receiving throwing closure):**
```
Swift closure: (A) throws -> B
    → Create C# invoker lambda
    → Call Swift function pointer with args + &error + context
    → If error.Value != IntPtr.Zero: return SwiftResult.FromFailure(error)
    → Else: return SwiftResult.FromSuccess(result)
    → Return Func<A, SwiftResult<B, SwiftError>> to user
```

## Limitations

**Async+throwing closures are NOT supported** because `[UnmanagedCallersOnly]` callbacks cannot await Tasks. The callback would receive `Task<SwiftResult<T, SwiftError>>` but cannot await it to get the actual result/error synchronously.

For `() async throws -> Data` closures, the method/constructor is marked with an unsupported signature and skipped during binding generation.

## Files Changed

- `src/Swift.Runtime/src/Swift/SwiftVoid.cs` (new)
- `src/Swift.Runtime/src/Swift/SwiftResult.cs`
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs`

## Tests Added

- `IsSupportedClosure_WithThrowingClosure_ReturnsTrue`
- `GetCSharpDelegateType_ThrowsVoidToVoid_ReturnsFuncSwiftResultVoid`
- `GetCSharpDelegateType_ThrowsVoidToInt_ReturnsFuncSwiftResultInt`
- `GetCSharpDelegateType_ThrowsIntToBool_ReturnsFuncIntSwiftResultBool`
- `IsThrowingClosure_WithThrowingClosure_ReturnsTrue`
- `IsThrowingClosure_WithNonThrowingClosure_ReturnsFalse`
- `GetPInvokeFunctionPointerTypeWithError_VoidToVoid_ReturnsCorrectType`
- `GetPInvokeFunctionPointerTypeWithError_IntToBool_ReturnsCorrectType`

## Test Results

- 600 unit tests passing
- 691 integration tests passing
- 72 runtime tests passing (1 skipped)
- **Total: 1,363 tests**
