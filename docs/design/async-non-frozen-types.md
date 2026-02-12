# Async Methods with Non-Frozen Type Parameters

## Status: Partially Implemented

This document describes the current state and known limitations of async Swift methods that take non-frozen type parameters.

## Background

Swift async methods with non-frozen type parameters require special handling in the binding generator because:

1. **Swift calling convention limitation**: P/Invoke with `[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]` only supports blittable (unmanaged) types. `SafeHandle` is a managed type and cannot be used.

2. **Async wrapper architecture**: Async Swift methods use a generated Swift wrapper that bridges C#'s `Task<T>` to Swift's `async`/`await`. The wrapper receives a callback function pointer and task handle from C#.

## What's Implemented (Commit e3575a3)

The initial fix addresses the `InvalidProgramException` that occurred at runtime:

```
InvalidProgramException: Passing non-blittable types to a P/Invoke
with the Swift calling convention is unsupported.
```

### Changes Made

1. **P/Invoke signature**: For async methods, non-frozen type parameters use `IntPtr` instead of `SafeHandle` in the P/Invoke signature (via `IntPtrFromNonFrozen` marker type).

2. **Parameter handling**: Added `IntPtrFromNonFrozen` case to `Parameter.SignatureString()` and `GetCallArgumentString()`.

3. **Lifetime management**: Added `DangerousAddRef`/`DangerousGetHandle`/`DangerousRelease` pattern to manage SafeHandle lifetime during async calls.

### Generated Code Pattern

```csharp
public async Task<UIImage> image(ImageRequest _for)
{
    TaskCompletionSource<UIImage> task = new TaskCompletionSource<UIImage>();
    GCHandle handle = GCHandle.Alloc(task, GCHandleType.Normal);
    bool _forSuccess = false;
    _for.Payload.DangerousAddRef(ref _forSuccess);
    IntPtr _forHandle = _for.Payload.DangerousGetHandle();
    try
    {
        var self = new SwiftSelf((void*)_payload.DangerousGetHandle());
        PInvoke_image(..., _forHandle, self, out var error);
        // ...
        return task.Task;
    }
    finally
    {
        if (_forSuccess)
            _for.Payload.DangerousRelease();
    }
}

[DllImport("...", EntryPoint = "...")]
private static extern void PInvoke_image(..., IntPtr _for, ...);  // IntPtr, not SafeHandle
```

## Known Limitation: Value Copy Semantics

### The Problem

While the P/Invoke signature issue is fixed, there's a deeper problem when actually calling async methods with non-frozen types at runtime.

The Swift wrapper receives a raw pointer (`IntPtr`) to the non-frozen type's memory. To call the actual Swift async method, it needs to:
1. Interpret the pointer as the Swift type
2. Pass the value to the async method

The current approach uses `.assumingMemoryBound(to:).pointee` to load the value:

```swift
extension Nuke.ImagePipeline {
    @_silgen_name("$s4Nuke13ImagePipelineC5image3forSo7UIImageCAA0B7RequestV_tYaKF_async")
    public func PInvoke_image(callback: @escaping (UIKit.UIImage, Int64) -> Void,
                               task: Int64,
                               _for: UnsafeRawPointer) {
        let _forValue = _for.assumingMemoryBound(to: Nuke.ImageRequest.self).pointee
        Task {
            let result = try! await image(for: _forValue)
            callback(result, task)
        }
    }
}
```

### Why It Crashes

The `.pointee` access performs a **bitwise copy** of the memory. For Swift value types with:
- Reference-counted fields (strings, arrays, class references)
- Indirect storage
- Complex internal structure

A bitwise copy doesn't properly:
- Retain reference-counted fields
- Update value witness table metadata
- Handle copy-on-write semantics

When the copied value is later used (e.g., when Nuke's `image(for:)` accesses the URL inside `ImageRequest`), it may access freed or invalid memory, causing a crash.

### Stack Trace Example

```
SIGSEGV in:
$s4Nuke13ImagePipelineC011makeStartedB4Task...
$s4Nuke13ImagePipelineC5image3forSo7UIImageCAA0B7RequestV_tYaKFTY0_
```

## Potential Solutions

### 1. Use Swift's Value Witness Table

Swift's ABI includes a value witness table for each type that provides functions for:
- `initializeWithCopy`: Properly copy a value
- `assignWithCopy`: Assign with proper semantics
- `destroy`: Clean up a value

The Swift wrapper could use these functions to properly copy the value:

```swift
// Conceptual - requires access to value witness table
let metadata = type(of: _for).metadata
let vwt = metadata.valueWitnessTable
let copy = vwt.initializeWithCopy(dest, source)
```

### 2. Keep Value Alive Without Copying

Instead of copying, keep the original value alive throughout the async operation:

```swift
public func PInvoke_image(..., _for: UnsafeRawPointer) {
    // Don't copy - pass the pointer and ensure C# keeps it alive
    withExtendedLifetime(_for) {
        Task {
            // Use the value directly through the pointer
        }
    }
}
```

This requires coordination with C# to not release the reference until the callback fires.

### 3. Serialize/Deserialize

For some types, serialize to a format (JSON, property list) that can be safely passed and reconstructed:

```swift
// C# serializes ImageRequest to JSON
// Swift wrapper deserializes and creates new ImageRequest
```

This is type-specific and may not preserve all semantics.

### 4. Synchronous Bridge

Instead of passing the value to the async task, call a synchronous Swift function that:
1. Receives the pointer
2. Starts the async operation
3. Returns immediately
4. Calls back when complete

```swift
public func PInvoke_image_sync(..., _for: UnsafeRawPointer) {
    let request = _for.assumingMemoryBound(to: Nuke.ImageRequest.self)
    // Start async operation with pointer still valid
    startAsyncImage(request: request.pointee) { result in
        callback(result, task)
    }
}
```

## Current Workarounds

### Use Frozen Types

Frozen types (marked with `@frozen` in Swift) have a fixed memory layout and can be safely copied bitwise. If possible, use frozen type parameters for async methods.

### Use Foundation.URL Instead

For Nuke specifically, there's an `image(for: URL)` overload that takes a `Foundation.URL` instead of `ImageRequest`. URL is a frozen type and works correctly:

```csharp
var url = Swift.URL.FromString(new SwiftString("https://example.com/image.jpg"));
var image = await pipeline.image(url);  // Works with URL
```

## Files Involved

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`
  - `Parameter.SignatureString()`: Lines 172-179
  - `GetCallArgumentString()`: Lines 195-212
  - `PInvokeSignatureBuilder.HandleArguments()`: Lines 539-548
  - `EmitSafeHandleAddRef()`: Lines 1095-1128
  - `EmitSafeHandleRelease()`: Lines 1130-1188
  - `EmitAsync()`: Lines 890-967 (Swift wrapper generation)

## Testing

The `BindingTesting/Nuke/NukeTestApp` project tests this scenario:
- Creates an `ImageRequest` from a URL string
- Calls `ImagePipeline.image(ImageRequest)` async method
- Currently crashes due to the value copy issue

## Related Issues

- Original issue: Async methods with non-frozen parameters throw `InvalidProgramException`
- Remaining issue: Async methods with non-frozen parameters crash at runtime due to improper value copying

## References

- [Swift ABI Stability Manifesto](https://github.com/apple/swift/blob/main/docs/ABIStabilityManifesto.md)
- [Swift Value Witness Table](https://github.com/apple/swift/blob/main/docs/ABI/TypeMetadata.rst)
- [Swift Calling Convention](https://github.com/apple/swift/blob/main/docs/ABI/CallingConvention.rst)
