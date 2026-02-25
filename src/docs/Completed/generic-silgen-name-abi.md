# Generic `@_silgen_name` ABI: TypeMetadata Passing from C#

## Session 7 Spike Results (Proven)

All 9 tests passed on iOS Simulator (Mono JIT, arm64).

## Key Discovery: Double Metadata Passing

For generic `@_silgen_name` functions with **explicit `T.Type` metatype parameters**, Swift passes TypeMetadata **twice**:

1. **Explicit `T.Type` parameter** — in its declared position among the function parameters
2. **Implicit trailing TypeMetadata** — appended after all explicit parameters by the Swift calling convention

From C#, `CallConvSwift` P/Invoke must pass `TypeMetadata` in **both** positions.

### Why explicit `T.Type` is needed

Swift 6 treats "generic parameter not used in function signature" as a **hard error** (not warning). For `@_silgen_name` functions where all parameters are `UnsafeMutableRawPointer`, the generic parameter `T` has no way to participate in overload resolution unless we add an explicit `_ t: T.Type` parameter.

Since `T.Type` is ABI-equivalent to `TypeMetadata*` at the calling convention level, C# passes `TypeMetadata` in that position — and Swift treats it as the metatype.

### ABI Layout: Single Generic Parameter

```swift
@_silgen_name("SBW_Spike_sizeOfT")
public func SBW_Spike_sizeOfT<T>(
    _ self_: UnsafeMutableRawPointer,       // explicit param 0
    _ resultBuf: UnsafeMutablePointer<Int>, // explicit param 1
    _ t: T.Type                             // explicit param 2 (metatype)
)
```

C-level ABI:
```
SBW_Spike_sizeOfT(void* self_, int* resultBuf, TypeMetadata* t, /*implicit*/ TypeMetadata* T_metadata)
```

C# P/Invoke:
```csharp
[LibraryImport("Lib", EntryPoint = "SBW_Spike_sizeOfT")]
[UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvSwift) })]
internal static partial void SBW_Spike_sizeOfT(
    IntPtr self_, IntPtr resultBuf,
    TypeMetadata explicitType,    // position of T.Type param
    TypeMetadata implicitMetadata // trailing implicit metadata
);
```

### ABI Layout: Two Generic Parameters

```swift
@_silgen_name("SBW_Spike_map")
public func SBW_Spike_map<Element, Result>(
    _ self_: UnsafeMutableRawPointer,
    _ transformFuncPtr: UnsafeMutableRawPointer,
    _ transformContext: UnsafeMutableRawPointer?,
    _ resultBuf: UnsafeMutableRawPointer,
    _ elementType: Element.Type,   // explicit metatype 1
    _ resultType: Result.Type      // explicit metatype 2
)
```

C-level ABI:
```
SBW_Spike_map(void* self_, void* funcPtr, void* ctx, void* resultBuf,
              TypeMetadata* elementType, TypeMetadata* resultType,
              /*implicit*/ TypeMetadata* Element_meta, TypeMetadata* Result_meta)
```

C# P/Invoke (4 metadata params total):
```csharp
internal static partial void SBW_Spike_map(
    IntPtr self_, IntPtr transformFuncPtr, IntPtr transformContext, IntPtr resultBuf,
    TypeMetadata explicitElementType, TypeMetadata explicitResultType,     // explicit T.Type params
    TypeMetadata implicitElementMetadata, TypeMetadata implicitResultMetadata // implicit trailing
);
```

### ABI Layout: Identity (single metatype, no extra explicit params)

```swift
@_silgen_name("SBW_Spike_identity")
public func SBW_Spike_identity<T>(
    _ value: UnsafeMutableRawPointer,
    _ t: T.Type
) -> UnsafeMutableRawPointer
```

**Note**: Identity works with just ONE metadata param in C# (`TypeMetadata tMetadata`). This is because the explicit `T.Type` IS the metatype, and the implicit trailing may coincide. However, for consistency and safety, passing it twice is recommended for complex signatures.

## Error Propagation Pattern

Error out-params go **before** metatype params:

```swift
@_silgen_name("SBW_Spike_filterThrows")
public func SBW_Spike_filterThrows<Element>(
    _ self_: UnsafeMutableRawPointer,
    _ predicateFuncPtr: UnsafeMutableRawPointer,
    _ predicateContext: UnsafeMutableRawPointer?,
    _ resultBuf: UnsafeMutablePointer<Bool>,
    _ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>,
    _ elementType: Element.Type
)
```

C# side:
```csharp
internal static unsafe partial void SBW_Spike_filterThrows(
    IntPtr self_, IntPtr predicateFuncPtr, IntPtr predicateContext,
    IntPtr resultBuf, IntPtr* errorOut,
    TypeMetadata explicitElementType, TypeMetadata implicitElementMetadata);
```

## Element Marshalling in Swift Wrappers

For generic `Element` → pointer conversion in Swift closure wrappers:

```swift
let element = box.value
let elementSize = MemoryLayout<Element>.size
let elementAlignment = MemoryLayout<Element>.alignment
let buf = UnsafeMutableRawPointer.allocate(byteCount: max(elementSize, 1), alignment: elementAlignment)
defer { buf.deallocate() }

withUnsafePointer(to: element) { src in
    buf.copyMemory(from: UnsafeRawPointer(src), byteCount: elementSize)
}
```

This works for both value types (Int, Bool, Double) and reference types (classes). The `allocate + copyMemory + defer deallocate` pattern is safe because the buffer is only needed for the duration of the callback.

## Mono JIT Workarounds

- **Result via out-param buffer**: Avoid returning non-IntPtr values from `CallConvSwift` P/Invoke. Write results to `UnsafeMutablePointer<T>` instead.
- **IntPtr identity works**: Simple `IntPtr → IntPtr` with trailing TypeMetadata does not crash.
- **Bool returns crash**: Even with `[MarshalAs(UnmanagedType.U1)]`, Bool return from CallConvSwift crashes Mono JIT.

## Summary

| Generic params | Explicit metatype params | Implicit trailing params | Total metadata params in C# |
|---|---|---|---|
| `<T>` | 1 (`T.Type`) | 1 (`T`) | 2 |
| `<T, U>` | 2 (`T.Type`, `U.Type`) | 2 (`T`, `U`) | 4 |
| `<T, U, V>` | 3 | 3 | 6 |
