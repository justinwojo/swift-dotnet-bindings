# Phase 14: Async Tuple Return Support

## Overview

This phase added support for async methods returning tuples. Previously, methods like `data(_for:)` returning `(Data, URLResponse?)` would fall back to `AnyType`. Now they correctly return `Task<(T1, T2)>`.

## Problem

Swift's `@convention(c)` callbacks cannot accept tuple parameters because C doesn't support tuples. The binding generator was blocking async tuple returns to avoid crashes, but this meant methods like Nuke's `data(for:)` couldn't be properly bound.

## Solution

Flatten tuple elements into separate callback parameters. Instead of passing `(Data, URLResponse?)` as a single tuple to the callback, pass `Data` and `URLResponse?` as two separate arguments.

## Changes Made

### 1. MethodHandler.cs - Remove Async Exclusion (Wrapper Return Type)

**Line ~286-296**: Removed `&& !_env.MethodDecl.IsAsync` condition that blocked tuple handling for async methods.

```csharp
// Before
if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec) && !_env.MethodDecl.IsAsync)

// After
if (_env.TupleHandler.IsSupportedTuple(tupleTypeSpec))
```

### 2. MethodHandler.cs - Remove Async Exclusion (P/Invoke Return Type)

**Line ~552-562**: Same change for P/Invoke signature generation.

### 3. MethodHandler.cs - Flatten Tuples in Swift Callback Signature

**Lines ~1276-1310**: Added logic to detect tuple returns and expand elements into separate callback parameters.

```csharp
if (isTupleReturn)
{
    // Flatten tuple elements for @convention(c) compatibility
    var tupleTypeSpec = (TupleTypeSpec)returnTypeArg.SwiftTypeSpec;
    var elementTypes = tupleTypeSpec.Elements.Select(e => e.ToString()).ToList();
    callbackParams = string.Join(", ", elementTypes) + ", ";
    // For callback invocation, access tuple elements with .0, .1, etc.
    callbackResultArgs = string.Join(", ", Enumerable.Range(0, tupleTypeSpec.Elements.Count)
        .Select(i => $"result{_env.MethodDecl.Name}.{i}")) + ", ";
}
```

### 4. MethodHandler.cs - Update Callback Invocations

**Lines ~1435, 1451, 1476, 1493**: Changed Swift callback invocations to pass tuple elements separately.

```swift
// Before
callback(resultData, task)  // Where resultData is a tuple

// After
callback(resultData.0, resultData.1, task)  // Elements passed separately
```

### 5. MethodHandler.cs - New EmitAsyncWrapperForTuple Method

New method that generates C# callback handlers for async tuple returns with:
- Flattened callback parameters instead of `ValueTuple<...>`
- Element-wise marshalling (ObjC types via `GetNSObject`, Swift types via marshalling)
- Proper tuple construction from marshalled elements

```csharp
private static unsafe delegate* unmanaged[Cdecl]<Swift.Data, IntPtr, IntPtr, void>
    s_dataCallback = &dataOnComplete;

private static void dataOnComplete(Swift.Data rawItem0, IntPtr rawItem1, IntPtr task)
{
    var item0 = rawItem0;
    var item1 = rawItem1 == IntPtr.Zero
        ? Swift.SwiftOptional<Foundation.NSUrlResponse>.NewNone()
        : Swift.SwiftOptional<Foundation.NSUrlResponse>.NewSome(
            ObjCRuntime.Runtime.GetNSObject<Foundation.NSUrlResponse>(rawItem1));
    var result = (item0, item1);
    // ... complete TaskCompletionSource
}
```

### 6. MethodHandler.cs - New GetPInvokeTypeForTupleElement Method

Helper method to determine correct P/Invoke types for tuple elements:
- ObjC bridged types → `IntPtr`
- Optional ObjC types → `IntPtr`
- Non-frozen types → `.Buffer` type
- Frozen types with memory management → `.Buffer` type
- Other types → direct type name

### 7. TupleHandler.cs - Enhanced TranslateElementTypeToPInvoke

Updated P/Invoke type mapping to properly handle:
- ObjC bridged types → `IntPtr`
- Optional types containing ObjC types → `IntPtr`
- Types requiring memory management → `.Buffer` types

```csharp
// ObjC bridged types use IntPtr in P/Invoke
if (MarshallingHelpers.IsObjCBridged(typeRecord))
{
    return "IntPtr";
}

// Non-frozen types needing memory management use Buffer type
if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 &&
    (typeRecord.Flags & TypeRecordFlags.Frozen) == 0)
{
    return $"{typeRecord.CSharpTypeName.FullyQualifiedName}.Buffer";
}
```

## Results

### Before
```csharp
// data(for:) fell back to AnyType
public unsafe Task<AnyType> Data(ImageRequest _for)
```

### After
```csharp
// data(for:) returns proper tuple type
public unsafe Task<(Swift.Data, Swift.SwiftOptional<Foundation.NSUrlResponse>)> Data(ImageRequest _for)
```

### Generated Callback
```csharp
// Flattened parameters for @convention(c) compatibility
private static unsafe delegate* unmanaged[Cdecl]<Swift.Data, IntPtr, IntPtr, void>
    s_dataCallback_2A24987A = &dataOnComplete_2A24987A;

[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static void dataOnComplete_2A24987A(Swift.Data rawItem0, IntPtr rawItem1, IntPtr task)
{
    GCHandle handle = GCHandle.FromIntPtr(task);
    try
    {
        var item0 = rawItem0;
        var item1 = rawItem1 == IntPtr.Zero
            ? Swift.SwiftOptional<Foundation.NSUrlResponse>.NewNone()
            : Swift.SwiftOptional<Foundation.NSUrlResponse>.NewSome(
                ObjCRuntime.Runtime.GetNSObject<Foundation.NSUrlResponse>(rawItem1));
        var result = (item0, item1);
        // ... TCS completion
    }
    finally
    {
        handle.Free();
    }
}
```

### Generated Swift Wrapper
```swift
@_silgen_name("$s4Nuke13ImagePipelineC4data3for..._async")
public func PInvoke_data_2A24987A(
    callback: @escaping @convention(c) (Foundation.Data, Swift.Optional<Foundation.URLResponse>, Int64) -> Void,
    task: Int64,
    _for: UnsafeRawPointer
) {
    // ...
    Task {
        let resultdata = try! await __self.data(for: _forValue)
        callback(resultdata.0, resultdata.1, task)  // Elements passed separately
    }
}
```

## Test Impact

- **593 unit tests** - All passing
- **691 integration tests** - All passing
- **72 runtime tests** - All passing
- **Total: 1,356 tests passing**

## Pre-existing Issues

The 8 pre-existing compilation errors in ClosureEmitter (lines 3158, 3161, 3165, 3258, 3265) remain unchanged. These are related to closures with non-frozen struct parameters and existential return types, tracked as future work item 15.5.
