# Phase 7: Protocol Proxy Emitter (January 2026)

**Status**: COMPLETE

This phase implemented the full Swift ↔ C# protocol proxy pattern, enabling C# code to implement Swift protocols.

---

## 7.1 Protocol Proxy Class Generation
**Status**: IMPLEMENTED (2026-01-30)

**Goal**: Enable C# code to implement Swift protocols using the EveryProtocol pattern, allowing custom implementations to be passed to Swift APIs.

**Architecture**:
```
┌─────────────────────────────────────────────────────────────────────────┐
│                           C# Side                                        │
├─────────────────────────────────────────────────────────────────────────┤
│  ISwiftImageProcessing (interface)                                       │
│       ▲                                                                  │
│       │ implements                                                       │
│  ImageProcessingProxy                                                    │
│    - _csharpImpl: ISwiftImageProcessing?   (user's C# implementation)   │
│    - _swiftContainer: ExistentialContainer1  (wraps EveryProtocol)      │
│    - static _vtable: ImageProcessingVTable                              │
│    - static ProtocolWitnessTable                                        │
│    - [UnmanagedCallersOnly] receiver methods                            │
└─────────────────────────────────────────────────────────────────────────┘
                                    │
                              P/Invoke calls
                                    ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                           Swift Side                                     │
├─────────────────────────────────────────────────────────────────────────┤
│  EveryProtocol (class)                                                   │
│    - Empty class, just exists to implement protocols                    │
│                                                                          │
│  extension EveryProtocol: ImageProcessing                               │
│    - Each method calls back to C# via vtable function pointers          │
└─────────────────────────────────────────────────────────────────────────┘
```

**Files created**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` - Swift EveryProtocol generation
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` - C# proxy class generation
- `src/Swift.Runtime/src/Swift/Runtime/EveryProtocol.cs` - Runtime support class
- `src/Swift.Runtime/src/Swift/Runtime/SwiftObjectRegistry.cs` - Container-to-proxy mapping

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` - Emit EveryProtocol conformances
- `src/Swift.Bindings/src/Emitter/StringEmitter/ModuleEmitter.cs` - Emit proxy classes

---

## 7.2 Generic Type Handling in Proxy Classes
**Status**: IMPLEMENTED (2026-01-30)

**Problem**: Proxy classes referenced generic types like `SwiftOptional<T>` without type arguments, causing CS0305 errors (27 instances).

**Solution**: Extended `GetCSharpTypeName()` in `ProtocolProxyEmitter` to handle generic types:
```csharp
if (typeSpec is NamedTypeSpec namedType && namedType.GenericParameters.Count > 0)
{
    var baseTypeSpec = new NamedTypeSpec(namedType.Name);
    var baseRecord = _typeDatabase.GetTypeRecordOrAnyType(baseTypeSpec);
    var baseTypeName = baseRecord.CSharpTypeName.FullyQualifiedName;
    var genericArgs = namedType.GenericParameters
        .Select(gp => GetCSharpTypeName(gp))
        .ToList();
    return $"{baseTypeName}<{string.Join(", ", genericArgs)}>";
}
```

---

## 7.3 Closure Type Translation
**Status**: IMPLEMENTED (2026-01-30)

**Problem**: Protocol methods with closure parameters/returns (e.g., `((Data, URLResponse) -> Void)`) fell back to interface types instead of delegate types.

**Solution**: Added `GetClosureCSharpType()` and `GetTupleCSharpType()` helper methods:
```csharp
private string GetClosureCSharpType(ClosureTypeSpec closureTypeSpec)
{
    var paramTypes = closureTypeSpec.EachArgument().Select(GetCSharpTypeName).ToList();
    var returnType = closureTypeSpec.ReturnType;
    bool hasReturn = !returnType.IsEmptyTuple;

    if (!hasReturn)
        return paramTypes.Count == 0 ? "Action" : $"Action<{string.Join(", ", paramTypes)}>";
    else
    {
        var returnTypeName = GetCSharpTypeName(returnType);
        return paramTypes.Count == 0 ? $"Func<{returnTypeName}>" : $"Func<{string.Join(", ", paramTypes)}, {returnTypeName}>";
    }
}
```

---

## 7.4 Existential Type Handling in Proxies
**Status**: IMPLEMENTED (2026-01-30)

**Problem**: Existential/protocol types in proxy signatures caused return type mismatches (7 errors).

**Solution**: Integrated `ExistentialHandler` into `GetCSharpTypeName()`:
```csharp
var existentialHandler = new ExistentialHandler(_typeDatabase);
if (existentialHandler.IsExistential(typeSpec))
{
    var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
    if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
        return existentialHandler.GetCSharpExistentialType(protocolList);
    return "Swift.Runtime.ExistentialContainer1";
}
```

---

## 7.5 VTable Deduplication Fix
**Status**: IMPLEMENTED (2026-01-30)

**Problem**: Static constructor emitted duplicate vtable member initializations (CS1912 - 81 errors).

**Solution**: Added HashSet tracking across all vtable initialization points:
```csharp
var emittedLocalAssignments = new HashSet<string>();

// Property receivers
foreach (var property in protocolDecl.Properties)
    EmitLocalVtablePropertyAssignment(writer, property, emittedLocalAssignments);

// Method receivers (check before emitting)
if (methodIndices.ContainsKey(method))
    // emit...
```

---

## 7.6 Variable Scope Conflict Fix
**Status**: IMPLEMENTED (2026-01-30)

**Problem**: Receiver methods had variable scope conflicts between IntPtr parameters and local variables (CS0136/CS0841 - 37 errors).

**Solution**: Renamed parameters and local variables to avoid conflicts:
- IntPtr parameters: `rawArg{i}` instead of `arg{i}`
- Local variables: `param{i}` instead of using parameter names

---

## 7.7 Marshalling Helper Simplification
**Status**: IMPLEMENTED (2026-01-30)

**Problem**: `MarshalFromSwift<T>` and `MarshalToSwiftBuffer<T>` had `where T : ISwiftObject` constraint, failing for primitives and delegates (CS0311/CS0315 - 11 errors).

**Solution**: Removed the constraint and use `Unsafe.Read<T>/Unsafe.Write` for all types:
```csharp
private static IntPtr MarshalToSwiftBuffer<T>(T value)
{
    var size = Unsafe.SizeOf<T>();
    var ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
    Unsafe.Write((void*)ptr, value);
    return ptr;
}

private static T MarshalFromSwift<T>(IntPtr ptr)
{
    return Unsafe.Read<T>((void*)ptr);
}
```

---

## 7.8 Integration Test Verification
**Status**: VERIFIED (2026-01-30)

**Test results** (updated 2026-01-30):
- Unit tests: 539 passed (+11 for witness table and PAT proxy tests)
- Integration tests: 691 passed
- Runtime tests: 72 passed (+18 SwiftObjectRegistry tests)
- **Total: 1302 tests passing**

**Nuke integration test**:
- Build completed with 0 errors, 109 warnings
- iOS Simulator test: Image loaded successfully
- `TEST SUCCESS` marker received within timeout

---

## 7.9 Generated Code Example

**C# Proxy class** (generated):
```csharp
public unsafe class ImageProcessingProxy : ISwiftImageProcessing, ISwiftObject
{
    private static IntPtr _protocolWitnessTable;
    private static ImageProcessingSwiftVTable _swiftVTable;
    private static ImageProcessingLocalVTable _localVTable;

    private readonly ISwiftImageProcessing? _csharpImpl;
    private readonly EveryProtocol? _everyProtocol;
    private readonly ExistentialContainer1 _swiftContainer;

    static ImageProcessingProxy()
    {
        InitializeVtable();
    }

    // Constructor for C# implementation
    public ImageProcessingProxy(ISwiftImageProcessing implementation)
    {
        _csharpImpl = implementation;
        _everyProtocol = new EveryProtocol();
        _swiftContainer = ExistentialContainerFactory.Create<EveryProtocol, ISwiftImageProcessing>(_everyProtocol);
        SwiftObjectRegistry.RegisterStrong(_everyProtocol.Handle, this);
    }

    // [UnmanagedCallersOnly] receiver for Swift callbacks
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr Receive_identifier_get(IntPtr vtHandle, IntPtr selfContainer)
    {
        var proxy = SwiftObjectRegistry.GetProxyFromContainer<ImageProcessingProxy>(selfContainer);
        var result = proxy._csharpImpl!.Identifier;
        return MarshalToSwiftBuffer(result);
    }

    // Interface implementation
    public SwiftString Identifier
    {
        get
        {
            if (_csharpImpl != null)
                return _csharpImpl.Identifier;
            // Call Swift via P/Invoke for Swift implementations
            return NativeMethods.ImageProcessing_identifier_Get(_swiftContainer);
        }
    }
}
```

**Swift EveryProtocol extension** (generated):
```swift
extension EveryProtocol: ImageProcessing {
    public var identifier: String {
        var selfProto: ImageProcessing = self
        let resultPtr = _imageProcessing_vtable.func_identifier_get!(
            _imageProcessing_vtable.csVTHandle, &selfProto)
        return resultPtr.assumingMemoryBound(to: String.self).pointee
    }
}

@_silgen_name("SetImageProcessing_vtable")
public func setImageProcessing_vtable(uvt: UnsafeRawPointer) {
    let vt = uvt.assumingMemoryBound(to: ImageProcessing_vtable.self)
    _imageProcessing_vtable = vt.pointee
}
```

---

## 7.10 Tests Added

**Unit tests** (`ProtocolProxyEmitterTests.cs`):
- Proxy class structure tests (class declaration, interface implementation, vtable structs)
- Static fields tests (protocol witness table, vtable fields)
- Instance fields tests (csharpImpl, everyProtocol, swiftContainer)
- Static constructor tests (vtable initialization)
- Receiver method tests (property getters/setters, method receivers)
- Constructor tests (C# impl, existential container)
- Interface implementation tests (properties, methods)
- ISwiftObject implementation tests (GetTypeMetadata, NewFromPayload, MarshalToSwift)
- NativeMethods tests (SetVtable P/Invoke)
- Protocol conformance filtering tests (skip Self requirement)
- **Witness table lookup tests** (2026-01-30):
  - `EmitProxyClass_GeneratesWitnessTablePInvoke`
  - `EmitProxyClass_GetWitnessTableFromSwiftCallsNativeMethod`
- **PAT proxy class tests** (2026-01-30):
  - `EmitProxyClass_GeneratesGenericProxyForProtocolsWithAssociatedTypes`
  - `EmitProxyClass_GeneratesGenericConstraintsForAssociatedTypes`
  - `EmitProxyClass_GeneratesMultipleGenericParameters`
  - `EmitProxyClass_ImplementsGenericInterface`

**Unit tests** (`EveryProtocolEmitterTests.cs`) - Added 2026-01-30:
- **Witness table getter tests**:
  - `EmitWitnessTableGetter_GeneratesSilgenName`
  - `EmitWitnessTableGetter_GeneratesPublicFunction`
  - `EmitWitnessTableGetter_UsesCorrectProtocolName`
  - `EmitTypeMetadataGetter_GeneratesSilgenName`
  - `EmitTypeMetadataGetter_ReturnsUnsafeRawPointer`
- **PAT typealias tests**:
  - `EmitProtocolConformance_GeneratesTypealiasForAssociatedTypes`
  - `EmitProtocolConformance_GeneratesMultipleTypealiases`

**Runtime tests** (`SwiftObjectRegistryTests.cs`) - 18 new tests:
- Register/Unregister with valid/invalid handles
- RegisterStrong prevents garbage collection
- ReleaseStrong allows garbage collection
- TryGetProxy with unregistered/zero handle/wrong type
- GetProxy success and exception cases
- GetProxyFromContainer extracts proxy from Payload0
- Count and StrongCount reflect registered proxies
- Cleanup removes expired weak references
- Multiple registrations overwrite previous

**Integration test** (`NukeTestApp/Program.cs`):
- New "Test Protocol Proxy" button
- `MyCancellable` class implementing `ISwiftCancellable`
- Tests direct C# implementation
- Tests SwiftObjectRegistry register/lookup
- Tests CancellableProxy creation (identifies witness table limitation)

---

## 7.11 Protocol Witness Table Lookup
**Status**: IMPLEMENTED (2026-01-30)

The protocol proxy pattern now includes automatic witness table export, enabling full Swift → C# callbacks.

**Solution**: The Swift wrapper exports a function that extracts the protocol witness table pointer from an existential container:

**Swift side** (generated by `EmitWitnessTableGetter()`):
```swift
@_silgen_name("Get_EveryProtocol_ImageProcessing_WitnessTable")
public func getEveryProtocolImageProcessingWitnessTable() -> UnsafeRawPointer {
    let instance = EveryProtocol()
    return withExtendedLifetime(instance) {
        var proto: any Nuke.ImageProcessing = instance
        return withUnsafeBytes(of: &proto) { buffer in
            // Existential layout: [payload0-2] [metadata] [witness_tables...]
            let witnessTableOffset = 4 * MemoryLayout<Int>.size
            return buffer.baseAddress!.advanced(by: witnessTableOffset)
                .assumingMemoryBound(to: UnsafeRawPointer.self).pointee
        }
    }
}
```

**C# side** (generated by `ProtocolProxyEmitter`):
```csharp
private static IntPtr GetWitnessTableFromSwift()
{
    return NativeMethods.GetWitnessTable();
}

private static class NativeMethods
{
    [DllImport("Nuke", CallingConvention = CallingConvention.Cdecl,
               EntryPoint = "Get_EveryProtocol_ImageProcessing_WitnessTable")]
    public static extern IntPtr GetWitnessTable();
}
```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` - Added `EmitWitnessTableGetter()` and `EmitTypeMetadataGetter()`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` - Implemented `GetWitnessTableFromSwift()` with P/Invoke

**What now works**:
- ✅ C# implementation of protocol interfaces
- ✅ SwiftObjectRegistry registration and lookup
- ✅ Proxy class creation
- ✅ Direct C# implementation calls
- ✅ Protocol witness table export via Swift wrapper
- ✅ P/Invoke to retrieve witness table from Swift (targets SwiftBindings wrapper, not original module)

**Important fix**: P/Invoke declarations target `SwiftBindings` (the Swift wrapper module) rather than the original module name. The vtable and witness table functions are generated into the Swift wrapper, not the original Swift library.

**Validated (2026-01-30)**:
```
PROTOCOL TEST: CancellableProxy created, registry count = 1
MyCancellable.cancel() called! Count = 1
PROTOCOL TEST SUCCESS: Full proxy pattern works!
=== VALIDATION PASSED ===
```

---

## 7.12 Protocol Associated Types (PATs) Support in Proxies
**Status**: IMPLEMENTED (2026-01-30)

Protocols with associated types can now generate proxy classes with generic type parameters.

**Solution**: Removed the blocking check for `AssociatedTypes.Count > 0` and added generic proxy class generation:

**C# Proxy class** (generic for PATs):
```csharp
// For a protocol like: protocol ImageProcessing { associatedtype Element }
public unsafe class ImageProcessingProxy<TElement> : ISwiftImageProcessing<TElement>, ISwiftObject
    where TElement : ISwiftObject
{
    // ... proxy implementation
}
```

**Swift EveryProtocol extension** (with typealias):
```swift
extension EveryProtocol: Nuke.ImageProcessing {
    public typealias Element = Any

    // ... vtable-backed implementations
}
```

**Key changes**:
1. `ProtocolProxyEmitter` now generates generic proxy classes with type parameters for each associated type
2. `EveryProtocolEmitter` emits `typealias` declarations mapping associated types to `Any` for type erasure
3. `GetCSharpTypeName()` handles `AssociatedTypeReferenceSpec` by mapping `Self.Element` → `TElement`

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` - Removed PAT blocking, added typealias emission
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` - Added generic proxy class generation

**Tests added**:
- `EmitProtocolConformance_GeneratesTypealiasForAssociatedTypes`
- `EmitProtocolConformance_GeneratesMultipleTypealiases`
- `EmitProxyClass_GeneratesGenericProxyForProtocolsWithAssociatedTypes`
- `EmitProxyClass_GeneratesGenericConstraintsForAssociatedTypes`
- `EmitProxyClass_GeneratesMultipleGenericParameters`
- `EmitProxyClass_ImplementsGenericInterface`

**Nuke protocols now supported**:
- ✅ `ImageProcessing`
- ✅ `ImageDecoding` / `ImageEncoding`
- ✅ `ImageCaching` / `DataCaching`
- ✅ `DataLoading`
- ✅ `ImagePipelineDelegate`

**Known limitations** (documented, not fixed):
- Self-returning methods in PAT protocols throw `NotImplementedException`
- Nested associated types (e.g., `Self.Iterator.Element`) not supported
- Generic witness table instantiation with runtime type parameters not supported

---

## Summary

Phase 7 implemented the full protocol proxy pattern:
- C# proxy class generation
- Swift EveryProtocol conformances
- VTable-based Swift → C# callbacks
- Witness table export
- SwiftObjectRegistry for proxy lookup
- PAT support with generic proxies
- 1302 tests passing

This phase enables C# code to implement Swift protocols and pass implementations to Swift APIs.
