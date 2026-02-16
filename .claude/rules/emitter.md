---
paths:
  - "src/Swift.Bindings/src/Emitter/**"
---

# Emitter Architecture

## Type Projection
- **Properties** use idiomatic types (string, IReadOnlyList, T?) with conversion+disposal in getter/setter bodies. IsAccessor=true keeps accessor methods in raw types.
- **Methods** use idiomatic `string` (TypeConversionHandler applies conversion)
- Proxies must use the correct C# type for each context

## P/Invoke Projected Type Gates
- `WitnessDispatchEmitter.IsMethodDispatchable()` checks Swift-side types
- `ProtocolProxyEmitter.EmitPropertyImplementation()` and `EmitMethodImplementation()` have secondary gates checking projected C# types
- When TypeDatabase is incomplete, `Swift.Int` projects to `Swift.AnyType` — secondary gate catches this

## Witness Dispatch Architecture
- Swift side: `@_silgen_name` accessors in SwiftBindings wrapper lib
- C# side: P/Invoke declarations in NativeMethods nested class
- Blittable types: direct pointer allocation/load
- String types: `SBW_Utf8Slice` struct bridges UTF-8 bytes across boundary
- Setters use `UnsafeMutableRawPointer` containerPtr + typed pointee assignment
- No free function needed for setters (value consumed, no allocation returned)

## Key Files
- `WitnessDispatchEmitter.cs` — witness table dispatch P/Invoke emission
- `ProtocolProxyEmitter.cs` — protocol proxy class generation
- `TypeConversionHandler.cs` — string/type conversion for methods vs properties
- `ModuleEmitter.cs` — top-level module emission, resets collectors
- `ClosureEmitter.cs` — closure callback + return marshalling

## Type Marshalling Labels
- `Struct` — Frozen structs with only frozen fields → C# struct
- `ClassWithOpaquePayload` — Non-frozen structs → C# class with SafeHandle
- `ClassWithBufferStruct` — Frozen structs with ref type fields → C# class with Buffer
- `Class` — Swift classes → C# class with ARC
- `Unknown` — Unsupported → pruned from output
