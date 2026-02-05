# Async Complex Type Callback Marshalling Design

**Phase**: 60
**Status**: Completed
**Priority**: P3 (unblocks BlinkID async methods)
**Depends on**: Phase 58 (async String marshalling), Phase 59 (async Array<String> marshalling)

## Problem Statement

Async methods returning classes, enums, or structs fail at Swift compile time because `@convention(c)` callbacks only support blittable types:

```swift
// Current (FAILS)
callback: @escaping @convention(c) (BlinkID.BlinkIDSession, Int64) -> Void
```

Error: `'(BlinkIDSession, Int64) -> Void' is not representable in Objective-C, so it cannot be used with '@convention(c)'`

## Solution

Use `OpaquePointer` in the callback signature. Swift allocates memory, stores the result, and passes the pointer. C# receives the pointer, reads the value, and frees the memory.

### Type Classification

| Type | @convention(c) Compatible | Marshalling Strategy |
|------|--------------------------|---------------------|
| Primitives (Int, Bool, etc.) | ✅ Yes | Pass directly |
| String | ❌ No | UTF-8 `(ptr, len)` (Phase 58) |
| Array<String> | ❌ No | Flat buffer (Phase 59) |
| Class/Enum/Struct | ❌ No | `OpaquePointer` (Phase 60) |
| Generic type parameter | ✅ Yes | Pass directly (cast at Swift level) |

## Implementation Details

### 1. Type Detection

In `WrapperEmitter.Async.cs`, detect non-primitive return types:

```csharp
bool isComplexType = !IsSwiftPrimitive(returnTypeName);

private static bool IsSwiftPrimitive(string swiftTypeName)
{
    return swiftTypeName switch
    {
        "Swift.Int" or "Swift.UInt" or
        "Swift.Int8" or "Swift.Int16" or "Swift.Int32" or "Swift.Int64" or
        "Swift.UInt8" or "Swift.UInt16" or "Swift.UInt32" or "Swift.UInt64" or
        "Swift.Float" or "Swift.Double" or "Swift.Bool" => true,
        _ => false
    };
}
```

### 2. Swift Callback Signature

```swift
callback: @escaping @convention(c) (OpaquePointer, Int64) -> Void
```

### 3. Swift Memory Allocation and Result Storage

```swift
// Marshal complex type to pointer (C# will free via SBW_Free)
let _resultPtr: OpaquePointer
do {
    let _rawPtr = UnsafeMutableRawPointer.allocate(
        byteCount: MemoryLayout<ResultType>.size,
        alignment: MemoryLayout<ResultType>.alignment)
    _rawPtr.storeBytes(of: result, as: ResultType.self)

    // For classes: retain to prevent ARC deallocation before C# processes
    _ = Unmanaged.passRetained(result as AnyObject)

    _resultPtr = OpaquePointer(_rawPtr)
}

callback(_resultPtr, task)
// Ownership transferred to C# - do NOT free here
```

### 4. C# Callback Handling

**Important**: For classes, `resultPtr` points to a buffer containing the object reference, not the object itself. We must dereference to get the actual object pointer for `Arc.Release`.

```csharp
[DllImport("<wrapper-lib>", EntryPoint = "SBW_Free_<ModuleName>")]
private static extern void SBW_Free(IntPtr ptr);

private static unsafe delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> _callbackField = &CallbackMethod;

[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static void CallbackMethod(IntPtr resultPtr, IntPtr task)
{
    GCHandle handle = GCHandle.FromIntPtr(task);
    // For classes: read the object pointer from the buffer BEFORE we free it
    IntPtr _retainedObjPtr = *(IntPtr*)resultPtr;
    try
    {
        // Read result from pointer
        var result = SwiftMarshal.MarshalFromSwift<ResultType>(resultPtr);

        // Complete TaskCompletionSource (existing logic)
        // ...
    }
    finally
    {
        // For classes: release the retain added by Swift
        // CRITICAL: Use dereferenced object pointer, not the buffer pointer!
        Arc.Release(_retainedObjPtr);

        // Free Swift-allocated memory
        SBW_Free(resultPtr);
        handle.Free();
    }
}
```

### 5. Class Type ARC Handling

For class types, Swift needs to retain the object to prevent ARC from deallocating it before C# processes the result:

**Swift side:**
```swift
// Retain class to keep it alive through the callback
_ = Unmanaged.passRetained(result as AnyObject)
```

**C# side:**
```csharp
// CRITICAL: resultPtr is a pointer to the buffer, not the object!
// The buffer contains the object pointer - we must dereference to get it.
IntPtr objPtr = *(IntPtr*)resultPtr;

// Release the extra retain after marshalling (in finally block)
Arc.Release(objPtr);
```

**Why dereference is required**: Swift stores the class instance in allocated memory via `storeBytes(of:as:)`. For a class, the "value" stored is the object reference (a pointer). So `resultPtr` points to a buffer containing a pointer, not directly to the object. Calling `Arc.Release(resultPtr)` would try to release the buffer address, causing undefined behavior or crashes.

For value types (enums, structs), no ARC handling is needed - the bytes are simply copied.

## Files Modified

| File | Changes |
|------|---------|
| `WrapperEmitter.Async.cs` | Add `IsSwiftPrimitive()`, detect complex types, emit `OpaquePointer` callback, add `EmitAsyncWrapperForComplexType()` |
| `build-swift-wrapper.sh` (BlinkID) | Add `Info.plist` generation for framework |
| `BlinkIdTestApp.csproj` | Enable SwiftBindings.framework reference |

## Test Results

**BlinkID**: 15/18 → **18/18 tests passing** ✅

The 3 previously failing tests now pass:
- `DetectionStatus.FromRawValue(string)` - String raw value (Phase 55)
- `Country.RawValue` getter - String raw value (Phase 55)
- `DocumentType.RawValue` getter - String raw value (Phase 55)

The Swift wrapper now compiles without `@convention(c)` errors thanks to the `OpaquePointer` marshalling for async methods returning:
- `BlinkIDSession` (class)
- `BlinkIDSdk` (class)
- `PingStatus` (enum)

## Key Learnings

1. **@convention(c) limitations extend beyond String**: Classes, enums, and structs also can't be passed directly. Use `OpaquePointer` for all non-primitive types.

2. **ARC lifecycle management**: For classes, the object must be retained in Swift before passing through the callback, then released in C# after marshalling. Without this, ARC may deallocate the object while the callback is in flight.

3. **Memory layout consistency**: Swift and C# both use the same memory layout for types. Storing bytes via `storeBytes(of:as:)` and reading via `SwiftMarshal.MarshalFromSwift` maintains type consistency.

4. **Framework packaging**: iOS frameworks require `Info.plist` with bundle identifier, version info, and supported platforms.

## Future Work

Consider extending this pattern to:
- Async methods returning `Optional<Class>`
- Async methods returning protocol existentials
- Async methods returning generic bound types
