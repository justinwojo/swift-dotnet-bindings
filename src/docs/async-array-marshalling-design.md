# Async Array Callback Marshalling Design

**Phase**: 59
**Status**: Completed
**Priority**: P3 (unblocks TestArray async test)
**Depends on**: Phase 58 (async String marshalling)

## Problem Statement

Async methods returning `Array<String>` fail at runtime because `@convention(c)` callbacks only support blittable types. Swift arrays cannot be passed directly through the callback.

```swift
// Current (FAILS)
callback: @escaping @convention(c) (Swift.Array<Swift.String>, Int64) -> Void
```

## Solution

Serialize `Array<String>` to a flat UTF-8 buffer, pass `(ptr, len)` through the callback. This extends Phase 58's pattern from single strings to arrays of strings.

### Buffer Format

Uses explicit `Int64` for wire format to avoid platform-sized `Int` ambiguity between Swift and C#:

```
[count: Int64]              // Number of strings (8 bytes, explicit Int64)
[len0: Int64]               // Length of string 0 in bytes
[len1: Int64]               // Length of string 1
...
[lenN-1: Int64]             // Length of string N-1
[str0: UTF-8 bytes]         // String 0 data (NOT null-terminated)
[str1: UTF-8 bytes]         // String 1 data
...
[strN-1: UTF-8 bytes]       // String N-1 data
```

**Header size**: `8 * (1 + count)` bytes
**Total size**: `8 * (1 + count) + sum(lengths)` bytes

### Buffer Validation

C# callback validates buffer contents before deserialization to prevent out-of-bounds reads and integer overflow:

**Bounds checking:**
- `count` must be non-negative and `<= int.MaxValue`
- Header size must fit within buffer length
- Each string length must be non-negative and `<= int.MaxValue`
- Total data size (header + all string data) must fit within buffer length

**Why `int.MaxValue` guards:**
- Buffer values are `Int64` on the wire but cast to `int` for .NET APIs (`List` capacity, `Marshal.PtrToStringUTF8` length)
- Without explicit checks, values > `int.MaxValue` would overflow silently or throw unpredictably
- Explicit validation makes error messages deterministic and meaningful

If validation fails, the TaskCompletionSource is completed with an exception rather than crashing the `[UnmanagedCallersOnly]` callback.

### Memory Management

1. Swift allocates the flat buffer
2. C# receives `(ptr, len)` through callback
3. C# copies data to managed `List<string>`
4. C# calls `SBW_Free` to release Swift memory

For empty arrays, allocate 1 byte (same as empty strings) to simplify memory management.

## Implementation Details

### 1. Type Detection

Detect `Swift.Array<Swift.String>` returns in async methods:

```csharp
// In WrapperEmitter.Async.cs
bool isArrayStringReturn = !isEmptyTuple && IsArrayOfString(returnTypeSpec);

private bool IsArrayOfString(TypeSpec typeSpec)
{
    if (typeSpec is not NamedTypeSpec namedType)
        return false;

    if (namedType.ToString() != "Swift.Array")
        return false;

    if (namedType.GenericParameters.Count != 1)
        return false;

    return namedType.GenericParameters[0].ToString() == "Swift.String";
}
```

### 2. Swift Callback Signature

```swift
callback: @escaping @convention(c) (UnsafeMutablePointer<UInt8>, Int, Int64) -> Void
```

Same signature as String returns - just a pointer and length.

### 3. Swift Serialization Code

```swift
// Serialize Array<String> to flat buffer
let result = try await __self.ArrayPassThrough(input: input)
let count = result.count

// Collect lengths and calculate total size
var lengths = [Int]()
var totalDataLen = 0
for s in result {
    let utf8 = Array(s.utf8)
    lengths.append(utf8.count)
    totalDataLen += utf8.count
}

// Calculate buffer size
let headerSize = MemoryLayout<Int>.size * (1 + count)  // count + lengths array
let totalSize = headerSize + totalDataLen
let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: max(totalSize, 1))

// Write header: count followed by lengths
buffer.withMemoryRebound(to: Int.self, capacity: 1 + count) { intPtr in
    intPtr[0] = count
    for i in 0..<count {
        intPtr[1 + i] = lengths[i]
    }
}

// Write string data after header
var dataOffset = headerSize
for s in result {
    var utf8 = Array(s.utf8)
    if !utf8.isEmpty {
        (buffer + dataOffset).initialize(from: &utf8, count: utf8.count)
    }
    dataOffset += utf8.count
}

callback(buffer, totalSize, task)
// Ownership transferred to C# - do NOT free here
```

### 4. C# Callback Deserialization

```csharp
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static void CallbackMethod(IntPtr bufferPtr, nint bufferLen, IntPtr task)
{
    GCHandle handle = GCHandle.FromIntPtr(task);
    try
    {
        // Deserialize array buffer
        List<string> result;
        unsafe
        {
            if (bufferLen <= sizeof(long))
            {
                // Empty array or just count field
                result = new List<string>();
            }
            else
            {
                long count = *(long*)bufferPtr;

                if (count == 0)
                {
                    result = new List<string>();
                }
                else
                {
                    // Read lengths from header
                    long* lengthsPtr = (long*)bufferPtr + 1;

                    // Calculate data section offset
                    int headerSize = sizeof(long) * (1 + (int)count);

                    // Read strings
                    result = new List<string>((int)count);
                    int dataOffset = headerSize;
                    for (int i = 0; i < count; i++)
                    {
                        int strLen = (int)lengthsPtr[i];
                        string s = strLen == 0
                            ? string.Empty
                            : Marshal.PtrToStringUTF8(bufferPtr + dataOffset, strLen)!;
                        result.Add(s);
                        dataOffset += strLen;
                    }
                }
            }
        }

        // Complete TaskCompletionSource with result as IReadOnlyList<string>
        // (List<T> implements IReadOnlyList<T>)
        holderTcs.TrySetResult(result);
    }
    finally
    {
        SBW_Free(bufferPtr);
        handle.Free();
    }
}
```

### 5. Return Type Mapping

The wrapper return type follows the non-async pattern:
- `Array<String>` returns `IReadOnlyList<SwiftString>` (matching TypeConversionHandler behavior)

Note: This differs from Phase 58 where `String` returns idiomatic `string`. The element type comes from TypeDatabase (SwiftString), not idiomatic conversion (string). This is consistent with how non-async Array returns work.

## Files Modified

| File | Changes |
|------|---------|
| `WrapperEmitter.Async.cs` | Add `isArrayStringReturn` detection, emit serialization code, add `EmitAsyncWrapperForArrayString()` |
| `AsyncTests.cs` | Remove Skip attribute, update expected return type to `IReadOnlyList<string>` |

## Test Changes

The test remains largely unchanged - just remove the Skip attribute:

Before:
```csharp
[Fact(Skip = "...")]
public async Task TestArray()
{
    IReadOnlyList<SwiftString> result = await myStruct.ArrayPassThrough(input);
    Assert.Equal("one", result[0].ToString());
}
```

After:
```csharp
[Fact]
public async Task TestArray()
{
    IReadOnlyList<SwiftString> result = await myStruct.ArrayPassThrough(input);
    Assert.Equal("one", result[0].ToString());
}
```

## Future Work

This implementation supports `Array<String>` only. Future phases could extend to:
- `Array<Int>`, `Array<Bool>` (primitive arrays)
- `Array<SomeStruct>` (struct arrays)
- Nested arrays

Each would need custom serialization logic appropriate to the element type.

## Key Learnings from Phase 58

1. **@convention(c) only allows primitives and pointers** - No custom structs, even @frozen ones
2. **Always allocate (even empty)** - Simplifies memory management
3. **Use module-specific symbol names** - `SBW_Free_ModuleName` avoids collisions
4. **Track P/Invoke deduplication** - Types with multiple async methods need single `SBW_Free` declaration
