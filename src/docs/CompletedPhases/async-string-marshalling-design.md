# Async String Callback Marshalling Design

**Phase**: 58
**Status**: Completed
**Priority**: P3 (unblocks TestString async test + BlinkID validation)

## Problem Statement

Async methods returning `String` fail at runtime because `@convention(c)` callbacks only support blittable types:

```swift
// Current (FAILS)
callback: @escaping @convention(c) (Swift.String, Int64) -> Void
```

Error: `Cannot marshal type System.String from Swift`

## Solution

Use UTF-8 encoding with separate `(ptr, len)` parameters in the callback. Custom structs (even `@frozen` ones) cannot be used as `@convention(c)` callback parameters.

1. Swift allocates UTF-8 buffer
2. Passes `(ptr, len)` through callback (primitives only)
3. C# copies to managed string
4. C# calls `SBW_Free` to release memory

## Implementation Details

### 1. Callback Signature

**Before (FAILS):**
```swift
callback: @escaping @convention(c) (Swift.String, Int64) -> Void
```

**After (WORKS):**
```swift
callback: @escaping @convention(c) (UnsafeMutablePointer<UInt8>, Int, Int64) -> Void
```

Note: Custom structs like `SBW_Utf8Slice` cannot be callback parameters — only primitives and pointers are allowed in `@convention(c)`.

### 2. Swift Wrapper Generation

```swift
@_silgen_name("...")
public func AsyncStringPassThrough(
    callback: @escaping @convention(c) (UnsafeMutablePointer<UInt8>, Int, Int64) -> Void,
    errorCallback: @escaping @convention(c) (UnsafePointer<CChar>, Int64) -> Void,
    task: Int64
) {
    Task {
        do {
            let result = try await __self.StringPassThrough(...)

            // Marshal String to UTF-8 (C# will free via SBW_Free)
            var _utf8 = Array(result.utf8)
            let _sliceLen = _utf8.count
            let _slicePtr = UnsafeMutablePointer<UInt8>.allocate(capacity: max(_sliceLen, 1))
            if _sliceLen > 0 {
                _slicePtr.initialize(from: &_utf8, count: _sliceLen)
            }

            callback(_slicePtr, _sliceLen, task)
            // Ownership transferred to C# - do NOT free here

        } catch {
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, task) }
        }
    }
}
```

### 3. C# Callback Generation

```csharp
[DllImport("<wrapper-lib>", EntryPoint = "SBW_Free")]
private static extern void SBW_Free(IntPtr ptr);

private static unsafe delegate* unmanaged[Cdecl]<IntPtr, nint, IntPtr, void> _callbackField = &CallbackMethod;

[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
private static void CallbackMethod(IntPtr slicePtr, nint sliceLen, IntPtr task)
{
    GCHandle handle = GCHandle.FromIntPtr(task);
    try
    {
        // Unmarshal UTF-8 to string
        string result;
        if (sliceLen == 0)
        {
            result = string.Empty;
        }
        else
        {
            result = Marshal.PtrToStringUTF8(slicePtr, (int)sliceLen)!;
        }

        // Complete TaskCompletionSource (existing logic)
        // ...
    }
    finally
    {
        // Always free Swift-allocated memory
        SBW_Free(slicePtr);
        handle.Free();
    }
}
```

### 4. SBW_Free Function

Emitted once per module in Swift wrapper library. Uses module-specific symbol name
to avoid collisions when multiple modules are linked into the same wrapper library:

```swift
@_silgen_name("SBW_Free_<ModuleName>")
public func SBW_Free(_ ptr: UnsafeMutableRawPointer?) {
    ptr?.deallocate()
}
```

C# P/Invoke uses the actual wrapper library path and module-specific entry point.
The P/Invoke is emitted once per type (tracked via `Utf8SliceEmitter`) to avoid
duplicate member errors when a type has multiple async string methods:

```csharp
[DllImport("<wrapper-lib>", EntryPoint = "SBW_Free_<ModuleName>")]
private static extern void SBW_Free(IntPtr ptr);
```

### 5. Empty String Handling

Empty strings always allocate at least 1 byte (`max(_sliceLen, 1)`) to simplify memory management:

| Scenario | Swift | C# |
|----------|-------|-----|
| Empty string `""` | `ptr: valid 1-byte buffer, len: 0` | Check `len == 0`, return `string.Empty` |
| Non-empty string | `ptr: valid buffer, len: byte count` | Use `Marshal.PtrToStringUTF8(ptr, len)` |
| Free behavior | Always deallocates | Always calls `SBW_Free(ptr)` |

This avoids the complexity of nil pointer handling in `@convention(c)` callbacks.

## Files Modified

| File | Changes |
|------|---------|
| `WrapperEmitter.Async.cs` | Detect String return, emit `(ptr, len)` callback, add `EmitAsyncWrapperForString()` |
| `Utf8SliceEmitter.cs` | Add `EmitFreeIfNeeded()` for `SBW_Free` function |
| `ModuleHandler.cs` | Call `Utf8SliceEmitter.EmitIfNeeded()` and `EmitFreeIfNeeded()` at module start |
| `AsyncTests.cs` | Remove Skip attribute from `TestString` test |

## Test Results

- **TestString** — PASSES (async string round-trip)
- **TestInstanceMethods** — PASSES (no regression)
- **TestStaticMethods** — PASSES (no regression)
- **TestArray** — Skipped (deferred to Phase 59)

## Key Learnings

1. **@convention(c) limitations**: Custom structs (even `@frozen`) cannot be callback parameters. Must pass fields separately as primitives.

2. **Empty string handling**: Allocating 1 byte for empty strings avoids nil pointer edge cases while ensuring consistent free behavior.

3. **Library path**: P/Invoke must use the actual wrapper library path (from TypeDatabase), not a hardcoded name.

4. **Symbol collisions**: When multiple modules may be linked into one library, use module-specific symbol names (e.g., `SBW_Free_ModuleName`).

5. **Duplicate P/Invoke declarations**: Types with multiple async string methods need tracking to emit the `SBW_Free` P/Invoke only once per type, avoiding CS0111 duplicate member errors. Use fully-qualified type identity (`SwiftTypeName.ModuleQualifiedName`) as the dedup key to avoid collisions between nested types with the same simple name in different containers (e.g., `OuterA.ErrorType` vs `OuterB.ErrorType`).

## Future Work (Phase 59)

Array returns will use a similar pattern, likely serializing to a flat buffer:
- Option A: JSON serialization (simple but slow)
- Option B: Length-prefixed array of UTF-8 strings
- Option C: Two arrays (offsets + data)

Deferred to keep Phase 58 focused on String.
