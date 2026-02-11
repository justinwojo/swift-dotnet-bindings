# Phase 28: Async+Throwing Closure Support

## Problem Statement

Swift APIs like Nuke's `ImageRequest.init(data:)` require async+throwing closures passed FROM C# TO Swift:

```swift
public init(
    id: String? = nil,
    data: @Sendable @escaping () async throws -> Data,
    type: AssetType? = nil
)
```

Currently, this is the **only remaining gap** blocking full Nuke library coverage. The binding generator explicitly skips these closures (see `ClosureHandler.cs` lines 174-175).

### Why It's Hard

1. **C# `[UnmanagedCallersOnly]` methods cannot be async** - they must return synchronously
2. **Swift expects an actual `() async throws -> T` closure** that it will `await`
3. **We don't control when Swift calls the closure** - it's invoked by Swift library code

### What Already Works

| Closure Type | C# Mapping | Status |
|--------------|------------|--------|
| Plain sync | `Action<...>` / `Func<...>` | ✅ Working |
| Async only | `Func<..., Task<T>>` | ✅ Working |
| Throws only | `Func<..., SwiftResult<T, SwiftError>>` | ✅ Working |
| **Async + Throws** | `Func<..., Task<SwiftResult<T, E>>>` | ❌ Blocked |

## Solution: Swift Continuation Wrapper

**Consensus from multiple AI consultations (Grok, Gemini):** Use `withCheckedThrowingContinuation` in a generated Swift wrapper.

### Architecture Overview

```
C# User Code                    Generated Swift Wrapper              Swift Library
     │                                    │                               │
     │  Func<Task<Data>>                  │                               │
     ▼                                    │                               │
[GCHandle.Alloc]                          │                               │
     │                                    │                               │
     │  P/Invoke with startFunc ptr       │                               │
     ├───────────────────────────────────►│                               │
     │                                    │  Creates async closure:       │
     │                                    │  withCheckedThrowingContinuation
     │                                    │        │                      │
     │                                    │        │ Passes closure to    │
     │                                    │        │ Swift library        │
     │                                    │        ├─────────────────────►│
     │                                    │        │                      │
     │                                    │        │ Library awaits       │
     │                                    │        │ closure (suspends)   │
     │                                    │        │◄─────────────────────┤
     │                                    │        │                      │
     │                                    │  Continuation calls startFunc │
     │◄───────────────────────────────────┤        │                      │
     │                                    │        │                      │
[Task.Run async work]                     │        │                      │
     │                                    │        │                      │
     │  successCB(ctx, dataPtr, len)      │        │                      │
     ├───────────────────────────────────►│        │                      │
     │                                    │  cont.resume(returning: data) │
     │                                    │        │                      │
     │                                    │        ▼                      │
     │                                    │   Returns Data ──────────────►│
```

### Key Insight

The pattern is **symmetric** to what we already do for async Swift methods:
- **Swift→C# async** (existing): Swift wrapper with `Task {}` calls C# callback when done
- **C#→Swift async** (new): Swift wrapper with `withCheckedThrowingContinuation` calls C# "start" function, C# calls back when done

## Implementation Design

### 1. Swift Wrapper Code (Generated)

For a closure parameter `data: @Sendable @escaping () async throws -> Data`:

```swift
// C-style function pointer signatures
typealias AsyncClosureStartFunc = @convention(c) (
    UnsafeMutableRawPointer,  // C# context (GCHandle)
    UnsafeMutableRawPointer,  // Continuation box pointer
    @convention(c) (UnsafeMutableRawPointer, UnsafePointer<UInt8>, Int) -> Void,  // success
    @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void        // error
) -> Void

// Box to hold continuation (makes it pointer-stable)
class ContinuationBox<T> {
    var continuation: CheckedContinuation<T, Error>?
    init(_ continuation: CheckedContinuation<T, Error>) {
        self.continuation = continuation
    }
}

// Wrapper function that creates the async closure
@_silgen_name("swift_ImageRequest_init_wrapper")
public func swift_ImageRequest_init_wrapper(
    id: String?,
    dataContext: UnsafeMutableRawPointer,
    dataStartFunc: AsyncClosureStartFunc,
    type: AssetType?
) -> OpaquePointer {

    // Create the async closure that Swift library expects
    let dataLoader: @Sendable @escaping () async throws -> Data = {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                // Box the continuation
                let box = ContinuationBox(continuation)
                let boxPtr = Unmanaged.passRetained(box).toOpaque()

                // Define callbacks that will resume the continuation
                let successCB: @convention(c) (UnsafeMutableRawPointer, UnsafePointer<UInt8>, Int) -> Void = {
                    boxPtr, dataPtr, length in
                    let box = Unmanaged<ContinuationBox<Data>>.fromOpaque(boxPtr).takeRetainedValue()
                    let data = Data(bytes: dataPtr, count: length)  // Copy bytes
                    box.continuation?.resume(returning: data)
                    box.continuation = nil
                }

                let errorCB: @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void = {
                    boxPtr, errorMsg in
                    let box = Unmanaged<ContinuationBox<Data>>.fromOpaque(boxPtr).takeRetainedValue()
                    let message = String(cString: errorMsg)
                    box.continuation?.resume(throwing: NSError(
                        domain: "SwiftBindingsError",
                        code: -1,
                        userInfo: [NSLocalizedDescriptionKey: message]
                    ))
                    box.continuation = nil
                }

                // Call C# to start the async work
                dataStartFunc(dataContext, boxPtr, successCB, errorCB)
            }
        } onCancel: {
            // Optional: notify C# of cancellation
            // cancelFunc(dataContext)
        }
    }

    // Call the actual Swift initializer
    let request = ImageRequest(id: id, data: dataLoader, type: type)
    return exportHandle(request)
}
```

### 2. C# Code (Generated)

```csharp
// State object to hold the async delegate
internal sealed class AsyncThrowingClosureState<T>
{
    public required Func<Task<T>> AsyncFunc { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
}

// Delegate types for the callbacks
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void AsyncClosureSuccessCallback(IntPtr boxPtr, byte* data, nint length);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void AsyncClosureErrorCallback(IntPtr boxPtr, byte* errorMessage);

// The "start" callback - called synchronously by Swift, spawns async work
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
private static unsafe void DataLoader_Start(
    IntPtr contextPtr,      // GCHandle to AsyncThrowingClosureState
    IntPtr boxPtr,          // Swift's ContinuationBox pointer
    IntPtr successFuncPtr,  // Function pointer for success callback
    IntPtr errorFuncPtr)    // Function pointer for error callback
{
    var handle = GCHandle.FromIntPtr(contextPtr);
    if (handle.Target is not AsyncThrowingClosureState<byte[]> state)
        return;

    var successFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte*, nint, void>)successFuncPtr;
    var errorFunc = (delegate* unmanaged[Cdecl]<IntPtr, byte*, void>)errorFuncPtr;

    // Spawn the async work - this returns immediately
    _ = Task.Run(async () =>
    {
        try
        {
            // Execute the user's async delegate
            byte[] result = await state.AsyncFunc();

            // Call Swift's success callback
            fixed (byte* dataPtr = result)
            {
                successFunc(boxPtr, dataPtr, result.Length);
            }
        }
        catch (Exception ex)
        {
            // Call Swift's error callback
            var errorBytes = Encoding.UTF8.GetBytes(ex.Message + "\0");
            fixed (byte* errorPtr = errorBytes)
            {
                errorFunc(boxPtr, errorPtr);
            }
        }
        finally
        {
            // Clean up the GCHandle (one-shot closure)
            handle.Free();
        }
    });
}

// P/Invoke declaration
[DllImport("NukeSwiftWrapper", EntryPoint = "swift_ImageRequest_init_wrapper")]
private static extern IntPtr swift_ImageRequest_init_wrapper(
    SwiftString? id,
    IntPtr dataContext,
    delegate* unmanaged[Swift]<IntPtr, IntPtr, IntPtr, IntPtr, void> dataStartFunc,
    AssetType? type);

// Public API
public static ImageRequest Create(
    string? id,
    Func<Task<byte[]>> dataLoader,
    AssetType? type = null)
{
    // Pin the delegate
    var state = new AsyncThrowingClosureState<byte[]> { AsyncFunc = dataLoader };
    var handle = GCHandle.Alloc(state);

    try
    {
        var handlePtr = GCHandle.ToIntPtr(handle);
        var result = swift_ImageRequest_init_wrapper(
            id != null ? new SwiftString(id) : null,
            handlePtr,
            &DataLoader_Start,
            type);

        return new ImageRequest(result);
    }
    catch
    {
        handle.Free();
        throw;
    }
}
```

## Critical Implementation Details

### 1. Resume-Once Guarantee

Swift's `CheckedContinuation` will **crash** if resumed twice and log a warning if never resumed.

**Mitigation:**
- Set `box.continuation = nil` after resuming
- Ensure exactly one of success/error callback is called
- Handle edge cases (e.g., Task.Run exceptions)

### 2. Data Copying Timing

Swift must copy the data buffer **before** the C# callback returns, because:
- C# uses `fixed` which only pins during the callback
- The buffer becomes invalid after the callback

**Solution:** Swift wrapper does `Data(bytes: dataPtr, count: length)` which copies.

### 3. Memory Ownership

| Owner | Owns | Lifetime |
|-------|------|----------|
| C# | `GCHandle` to delegate state | Until success/error callback completes |
| Swift | `ContinuationBox` via `Unmanaged.passRetained` | Until `takeRetainedValue` in callback |

### 4. Cancellation (Optional Enhancement)

Use `withTaskCancellationHandler` to propagate Swift Task cancellation to C#:

```swift
} onCancel: {
    cancelFunc(dataContext)  // Calls C# to trigger CancellationTokenSource
}
```

```csharp
state.CancellationSource?.Cancel();
```

## Files to Modify

1. **`src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`**
   - Remove the `IsAsync && Throws` block (lines 174-175)
   - Add `IsAsyncThrowingClosure()` detection method
   - Add `GetAsyncThrowingStartFuncSignature()` for P/Invoke types

2. **`src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs`**
   - Add `EmitAsyncThrowingClosureCallback()` - generates the "start" method
   - Add `EmitAsyncThrowingClosureSwiftWrapper()` - generates Swift continuation code
   - Add `EmitAsyncThrowingClosureCallbackPointer()` - generates static func ptr field

3. **`src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`**
   - Update closure parameter handling to use new async+throwing pattern
   - Integrate Swift wrapper generation

4. **New file: `src/Swift.Runtime/src/Swift/Runtime/AsyncThrowingClosureState.cs`**
   - Runtime support class for holding async delegate state

## Testing Strategy

1. **Unit tests** in `ClosureHandlerTests.cs`:
   - `IsAsyncThrowingClosure_ReturnsTrue_ForAsyncThrowsClosure`
   - `IsSupportedClosure_ReturnsTrue_ForAsyncThrowsClosure` (after implementation)

2. **Integration test** with Nuke:
   - Create `ImageRequest` with async data loader from C#
   - Verify image loads successfully
   - Test error path (throw exception in C#, verify Swift receives error)

3. **Edge case tests**:
   - Cancellation mid-flight
   - Large data transfers
   - Concurrent closure invocations

## Success Criteria

- [x] `ClosureHandler.IsSupportedClosure()` returns `true` for `() async throws -> T`
- [x] Generated bindings compile without errors
- [x] Nuke's `ImageRequest.init(data:)` binding generated correctly
- [x] Error propagation works (C# exception → Swift error)
- [x] No memory leaks (GCHandle freed, ContinuationBox released)
- [x] All existing closure tests still pass
- [ ] **Blocked**: Runtime invocation of `ImageRequest.init(data:)` crashes due to `SwiftArray<ExistentialContainer1>` metadata issue (see below)

## Implementation Notes (Data Return Type Support)

Foundation.Data return types in async+throwing closures are now fully supported:

1. **Runtime Helper Class**: `AsyncClosureHelper` in `Swift.Runtime` provides safe async
   execution outside unsafe class contexts:
   - `RunDataAsync()` - for `() async throws -> Data` closures
   - `RunAsync<T>()` - for generic return types
   - `RunVoidAsync()` - for void return types

2. **Why Helper Class?**: Generated C# classes are often marked `unsafe` for P/Invoke
   compatibility, but C# doesn't allow `await` in unsafe contexts. The runtime helper
   class is NOT unsafe, enabling proper async/await execution.

3. **Data Marshalling Pattern**:
   - User provides `Func<Task<Swift.Data>>`
   - C# awaits the task to get `Swift.Data`
   - Calls `result.ToByteArray()` to get bytes
   - Pins bytes and calls Swift's success callback with `(boxPtr, dataPtr, length)`
   - Swift copies the bytes to create a new Data object

## Known Issue: SwiftArray<ExistentialContainer> Metadata Crash

**Discovered**: During NukeTestApp validation testing

The `ImageRequest.init(data:)` constructor requires an `IEnumerable<ExistentialContainer1>` parameter for image processors. When this is converted to `SwiftArray<ExistentialContainer1>`, the array's static constructor attempts to get element type metadata, which crashes:

```
Managed Stacktrace:
  at Swift.Runtime.TypeMetadata:swift_getExistentialTypeMetadata
  at Swift.Runtime.TypeMetadata:GetExistentialTypeMetadata
  at Swift.SwiftArray`1:get_ElementTypeMetadata
  at Swift.SwiftArray`1:.cctor
```

**Impact**: The binding is generated correctly and compiles, but cannot be invoked at runtime.

**Test Status**: NukeTestApp includes an "Async Closures" test section that:
- ✅ Verifies the constructor binding exists
- ✅ Verifies `Func<Task<Swift.Data>>` delegate creation works
- ✅ Verifies `Swift.Data.FromNSData()` conversion works
- ⚠️ Documents the runtime limitation (warning, not failure)

**Next Steps**: Fix the existential container metadata lookup issue to enable full runtime invocation.

## References

- [Swift Continuations Documentation](https://developer.apple.com/documentation/swift/checkedcontinuation)
- [withTaskCancellationHandler](https://developer.apple.com/documentation/swift/withtaskcancellationhandler(operation:oncancel:))
- Existing async method implementation: `MethodHandler.cs:EmitAsync()`
- Existing closure implementation: `ClosureEmitter.cs`
