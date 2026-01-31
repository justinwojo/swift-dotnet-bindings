# Phase 3: Method Signature Gaps

**Status**: COMPLETE

This phase addressed method signatures that were previously skipped due to unsupported types or patterns.

---

## 3.1 Existential Types (`any Protocol`)
**Status**: PARAMETERS IMPLEMENTED (January 2026)
**Issue**: [#2875](https://github.com/dotnet/runtimelab/issues/2875)

**Progress** (January 2026):
1. ✅ Existential parameters now generate valid C# code using `ExistentialContainer{N}` structs
2. ✅ Existential return types now generate valid C# code
3. ✅ Single-protocol existentials (`any Protocol`) are handled via `NamedTypeSpec.IsAny`
4. ✅ Protocol compositions (`any P1 & P2`) are handled via `ProtocolListTypeSpec`
5. ✅ Added `TypeRecordKind.Existential` to distinguish from protocol interfaces
6. ✅ Properties with existential types are explicitly skipped with warnings (future work)

**Implementation details**:
- Added `ExistentialHandler` to `MethodEnvironment` for unified existential handling
- Updated `WrapperSignatureBuilder` and `PInvokeSignatureBuilder` to use `ExistentialContainer{N}` types
- Single-protocol existentials (`any DataLoading`) → `ExistentialContainer1`
- Protocol compositions (`any P1 & P2 & P3`) → `ExistentialContainer3`
- Up to 8 protocols supported (`ExistentialContainer0` through `ExistentialContainer8`)

**Files modified**:
- `src/Swift.Bindings/src/TypeDatabase/TypeRecord.cs` - Added `Existential` enum value
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs` - Use `Existential` kind
- `src/Swift.Bindings/src/Marshaler/IEnvironment.cs` - Added `ExistentialHandler` property
- `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs` - Extended for single-protocol existentials
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Existential signature handling
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Skip existential properties

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ExistentialHandlerTests.cs` - 7 new tests (391 total)

**Generated code example**:
```csharp
// Constructor with existential parameter
public unsafe Configuration(Swift.Runtime.ExistentialContainer1 dataLoader)
{
    PInvoke_init(swiftIndirectResult, dataLoader);
}

[DllImport("Nuke", EntryPoint = "...")]
private static extern void PInvoke_init(SwiftIndirectResult swiftIndirectResult,
    Swift.Runtime.ExistentialContainer1 dataLoader);
```

---

## 3.1.1 Existential Properties
**Status**: IMPLEMENTED (January 2026)

**Problem**: Properties with existential types (`any Protocol`) were completely skipped.

**Solution**: Extended the existential handling from methods to properties:
1. Added `ExistentialHandler` to `PropertyEnvironment`
2. Updated `PropertyHandler` to allow supported existentials (0-8 protocols) through
3. Generate correct C# type name (`ExistentialContainer1`, `ExistentialContainer2`, etc.)
4. Skip unsupported existentials (9+ protocols) with warning

**Generated code example**:
```csharp
public Swift.Runtime.ExistentialContainer1 DataLoader
{
    get => DataLoader_Get();
}
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/IEnvironment.cs` - Added `ExistentialHandler` to `PropertyEnvironment`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Existential type handling

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/PropertyHandlerTests.cs` - 8 new tests for existential properties

---

## 3.1.2 ExistentialContainerFactory Helper Methods
**Status**: IMPLEMENTED (January 2026)

**Problem**: Creating `ExistentialContainer{N}` from C# objects required manual construction with no helper methods.

**Solution**: Added `ExistentialContainerFactory` static class with factory methods:
1. `CreateAny<T>(T value)` - Creates `ExistentialContainer0` for `Any` type (zero protocols)
2. `Create<T, TProtocol>(T value)` - Creates container with single protocol witness table
3. `Create<T, P1, P2>(T value)` - Creates container with two protocol witness tables
4. `Create<T, P1, P2, P3>(T value)` - Creates container with three protocol witness tables
5. `Create<T, P1, P2, P3, P4>(T value)` - Creates container with four protocol witness tables
6. `CreateWithWitnessTables<T>(T value, params ProtocolWitnessTable[] tables)` - Creates container with arbitrary witness tables (up to 8)

**Implementation details**:
- Payload marshalling handles inline (≤24 bytes) vs heap allocation based on `ValueWitnessFlags.IsNonInline`
- Added `Handle` property to `ProtocolWitnessTable` to expose the native IntPtr
- Added existential container handling to `SwiftMarshal.MarshalToSwift()`

**Generated code example**:
```csharp
// Create container for a type conforming to ISwiftHashable
var container = ExistentialContainerFactory.Create<MyType, ISwiftHashable>(myValue);

// Create container for Any (zero protocols)
var anyContainer = ExistentialContainerFactory.CreateAny(myValue);
```

**Files modified**:
- `src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs` - Added `ExistentialContainerFactory` class
- `src/Swift.Runtime/src/Swift/Runtime/ProtocolWitnessTable.cs` - Added `Handle` property
- `src/Swift.Runtime/src/Swift/Runtime/InteropServices/SwiftMarshal.cs` - Added existential marshalling

**Tests added**:
- `src/Swift.Runtime/tests/MetadataTests/ExistentialContainerFactoryTests.cs` - 8 new tests

---

## 3.2 Generic Protocol Types (PATs)
**Status**: IMPLEMENTED (January 2026)

Protocols with associated types are now fully supported with generic proxy class generation.

**Progress** (January 2026):
1. ✅ Added `AssociatedTypeReferenceSpec` class to model associated type references (e.g., `Self.Element`)
2. ✅ Parser now handles `DependentMember` nodes for associated type references
3. ✅ `ProtocolHandler` maps associated types to C# generic parameters in method signatures
4. ✅ Properties with associated type return values now emit with proper generic types
5. ✅ **Proxy classes** now generate with generic type parameters for PAT protocols (2026-01-30)
6. ✅ **Swift EveryProtocol** emits `typealias` declarations for type erasure (2026-01-30)
7. ✅ **Witness table export** works for PAT protocols (2026-01-30)

**Implementation details**:
- Created `AssociatedTypeReferenceSpec` TypeSpec for `Self.Element`, `τ_0_0.Iterator`, etc.
- Added `MapAssociatedTypeToGenericParam()` to `ProtocolHandler` for type mapping
- Protocol methods referencing associated types now emit with mapped C# generic parameters
- `ProtocolProxyEmitter` generates generic proxy classes: `ImageProcessingProxy<TElement>`
- `EveryProtocolEmitter` emits typealiases: `public typealias Element = Any`
- `GetCSharpTypeName()` maps `AssociatedTypeReferenceSpec` to generic type parameters

**Files added**:
- `src/Swift.Bindings/src/Model/TypeSpec/AssociatedTypeReferenceSpec.cs`

**Files modified**:
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` - Handle `DependentMember` in `CreateTypeSpec`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Protocol method/property emission with PATs
- `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` - Generic proxy class generation (2026-01-30)
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` - Typealias emission (2026-01-30)

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/AssociatedTypeReferenceSpecTests.cs` - 9 tests
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/ProtocolProxyEmitterTests.cs` - PAT proxy tests (2026-01-30)
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/EveryProtocolEmitterTests.cs` - Typealias tests (2026-01-30)

**Nuke protocols now supported**:
- ✅ `ImageProcessing` - Core image processing protocol
- ✅ `ImageDecoding` / `ImageEncoding` - Codec protocols
- ✅ `ImageCaching` / `DataCaching` - Cache protocols
- ✅ `DataLoading` - Data loader protocol
- ✅ `ImagePipelineDelegate` - Pipeline delegate protocol

**Known limitations**:
- Self-returning methods in PAT protocols throw `NotImplementedException`
- Nested associated types (e.g., `Self.Iterator.Element`) not supported
- Generic witness table instantiation with runtime type parameters not supported

---

## 3.3 Closure/Callback Parameters
**Status**: BOUND GENERIC PARAMETERS IMPLEMENTED (January 2026)
**Issue**: [#2874](https://github.com/dotnet/runtimelab/issues/2874) - Implemented

**Progress** (January 2026):
1. ✅ Closures accepting bound generic parameters (e.g., `Action<Result<T,E>>`) now generate valid C# code
2. ✅ Added `Swift.Result` type mapping to `SwiftDatabase.xml`
3. ✅ Created `SwiftResult<TSuccess, TFailure>` class in Swift.Runtime
4. ✅ Callback names now include method hash for overload disambiguation
5. ✅ Closures *returning* bound generic types now supported via indirect return marshalling (3.3.1)
6. ✅ `SwiftResult<T,E>` now has value extraction: `Success`, `Failure`, `TryGetSuccess`, `TryGetFailure`, `Match` (2026-01-30)

**Implementation details**:
- Added `IsSupportedGenericType()` to check if bound generic types are in the type database
- Added `TranslateBoundGenericToCSharp()` to translate bound generics to C# type names (e.g., `Result<ImageResponse, Error>` → `SwiftResult<ImageResponse, ImagePipeline.Error>`)
- Updated `TranslateTypeSpecToPInvokeType()` to return `void*` for non-blittable types (types requiring memory management)
- Added `IsSupportedClosureReturnType()` to exclude closures with complex return types
- Updated `GetCallbackFunctionName()` to include mangled name hash for uniqueness

**Supported closure parameter types**:
- `Action<Result<T,E>>` - Result type with success/failure
- `Action<Array<T>>` - Swift arrays
- `Action<Optional<T>>` - Swift optionals
- `Action<Set<T>>` - Swift sets
- `Action<Dictionary<K,V>>` - Swift dictionaries

**Generated code example** (Nuke's `loadImage` method):
```csharp
public ImageTask LoadImage(URL with, Action<SwiftResult<ImageResponse, ImagePipeline.Error>> completion)
{
    // Closure receives Result as void*, marshals to SwiftResult<T,E>
}

[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
private static void loadImage_completion_06E6974D_Callback(void* arg0, SwiftSelf context)
{
    var del = SwiftClosureMarshaller.GetDelegateFromContext<Action<SwiftResult<ImageResponse, ImagePipeline.Error>>>(new IntPtr(context.Value));
    del(SwiftMarshal.MarshalFromSwift<SwiftResult<ImageResponse, ImagePipeline.Error>>(new IntPtr(arg0)));
}
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` - Core bound generic support
- `src/Swift.Runtime/src/Swift/SwiftDatabase.xml` - Added Swift.Result mapping
- `src/Swift.Runtime/src/Swift/SwiftResult.cs` - New SwiftResult<T,E> class
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` - Callback marshalling
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Pass mangled name for hash

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs` - 11 new tests (402 total)

---

## 3.3.1 Closure Bound Generic Returns
**Status**: IMPLEMENTED (January 2026)

**Problem**: Closures returning bound generic types (like `SwiftOptional<T>`, `SwiftResult<S,E>`) were excluded because `[UnmanagedCallersOnly]` callbacks require blittable return types.

**Solution**: Implemented indirect return pattern where the callback receives a buffer pointer and marshals the result into it:
1. Added `RequiresIndirectReturnMarshalling()` to detect closures needing indirect return
2. Added `GetPInvokeFunctionPointerTypeWithIndirectReturn()` for modified function pointer signature
3. Added `EmitIndirectReturnCallback()` to generate callbacks that marshal to buffer
4. Updated `IsSupportedClosureReturnType()` to allow bound generics via indirect return

**Generated code example**:
```csharp
// Closure: (UIImage) -> SwiftOptional<UIImage>
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
private static void Anonymous_arg1_Callback(void* indirectResult, void* arg0, SwiftSelf context)
{
    var del = SwiftClosureMarshaller.GetDelegateFromContext<Func<UIImage, SwiftOptional<UIImage>>>(new IntPtr(context.Value));
    var result = del(SwiftMarshal.MarshalFromSwift<UIImage>(new IntPtr(arg0)));

    var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftOptional<UIImage>>();
    var resultSpan = new Span<byte>(indirectResult, (int)metadata.Size);
    SwiftMarshal.MarshalToSwift(result, ref resultSpan);
}
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` - Indirect return detection and function pointer types
- `src/Swift.Bindings/src/Emitter/StringEmitter/ClosureEmitter.cs` - Indirect return callback emission
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Route to indirect return emission

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/ClosureHandlerTests.cs` - 12 new tests for indirect return

---

## 3.4 Generic Constructors
**Status**: IMPLEMENTED (January 2026)

**Problem**: Generic constructors were explicitly skipped with TODO referencing issue #2890.

**Key Insight**: Generic static methods already work! The infrastructure for TypeMetadata retrieval, payload marshalling, protocol witness tables, and try/finally cleanup was already in place for `EmitMethod`. It just needed to be applied to `EmitConstructor`.

**Solution**: Updated `WrapperEmitter.EmitConstructor()` to include generic handling:
1. Detect if constructor has generic parameters or closure parameters
2. Call `EmitClosureCallbacks()` to emit callback functions for closures
3. Call `EmitDeclarationsForAllocations()` to declare TypeMetadata and payload variables
4. Wrap in try/finally block for proper cleanup
5. Call `EmitGenericArguments()` to marshal generic params via stackalloc
6. Call `EmitProtocolWitnessTables()` to retrieve witness tables
7. Call `EmitFinally()` for cleanup

**Generated code example**:
```csharp
public unsafe GenericBox<T>(T value)
{
    TypeMetadata TMetadata = TypeMetadata.GetTypeMetadataOrThrow<T>();
    IntPtr valuePayload = IntPtr.Zero;
    try
    {
        Span<byte> valuePayloadSpan = stackalloc byte[(int)TMetadata.Size];
        valuePayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(valuePayloadSpan));
        SwiftMarshal.MarshalToSwift(value, ref valuePayloadSpan);

        var TProtocolWitnessTable = ProtocolWitnessTable.GetOrThrow<T, IEquatable>();

        PInvoke_init(swiftIndirectResult, valuePayload, TMetadata, TProtocolWitnessTable);
    }
    finally
    {
        // cleanup
    }
}
```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Removed skip logic, added generic infrastructure to EmitConstructor

**Also fixed**: Closures passed to constructors now properly emit callback functions and marshalling code (previously only methods handled closures).

---

## 3.4.1 Unbound Generic Methods Support
**Status**: IMPLEMENTED (January 2026)

**Problem**: Methods with unbound generic parameters (e.g., `func process<T: ImageProcessing>(_ processor: T)`) were skipped because:
1. `CreateTypeSpec` in `SwiftABIParser.cs` didn't handle `GenericTypeParam` nodes
2. `GenericSignatureParser` required `sugared_genericSig` to be present

**Solution**:
1. Added `kGenericTypeParam` case to `CreateTypeSpec()` to parse generic type parameter nodes
2. Modified `ParseGenericSignature()` to use generic signature as fallback when sugared signature is missing
3. Added `BuildWhereClause()` method to emit C# where clauses with `ISwiftObject` and protocol constraints
4. Added `IsProtocolAvailable()` to gracefully skip unknown protocols instead of failing

**Generated code example**:
```csharp
public unsafe void Process<T0>(T0 processor)
    where T0 : ISwiftObject, ISwiftImageProcessing
{
    TypeMetadata T0Metadata = TypeMetadata.GetTypeMetadataOrThrow<T0>();
    var T0ImageProcessingPWT = ProtocolWitnessTable.GetOrThrow<T0, ISwiftImageProcessing>();

    Span<byte> processorPayloadSpan = stackalloc byte[(int)T0Metadata.Size];
    var processorPayload = (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(processorPayloadSpan));
    SwiftMarshal.MarshalToSwift(processor, ref processorPayloadSpan);

    PInvoke_Process(processorPayload, T0Metadata, T0ImageProcessingPWT);
}
```

**Files modified**:
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` - Added GenericTypeParam case
- `src/Swift.Bindings/src/Parser/GenericSignatureParser.cs` - Made sugaredSignature optional with fallback
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Added where clause emission, IsProtocolAvailable

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/ParserTests/GenericSignatureParserTests.cs` - Fallback tests
- `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/TypeSpecParserTests.cs` - GenericTypeParam parsing tests
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenericMethodEmitterTests.cs` - 12 new tests

---

## 3.4.2 Unbound Generic Type Definitions
**Status**: IMPLEMENTED (January 2026)

**Problem**: Generic type definitions (e.g., `struct Box<T>`) were completely skipped with a warning about unsupported generic types.

**Solution**: Added full support for parsing and emitting generic type definitions:
1. Added `GenericParameters` and `IsGeneric` properties to `TypeDecl`
2. Removed the skip logic in `SwiftABIParser` that blocked generic types
3. Created `GenericTypeEmitter` helper class for emitting generic type declarations
4. Updated `FrozenStructHandler`, `NonFrozenStructHandler`, and `ClassHandler` to emit generic types

**Implementation details**:
- Generic parameters use naming convention `T0`, `T1`, etc.
- All generic parameters get `ISwiftObject` constraint
- Protocol conformance constraints are added (e.g., `where T0 : ISwiftObject, ISwiftEquatable`)
- `Sendable` constraints are skipped (no C# equivalent)
- `GenericTypeMapping` added to `TypeEnvironment` for parameter name resolution

**Generated code example**:
```csharp
public unsafe struct Box<T0> : ISwiftObject
    where T0 : ISwiftObject
{
    static TypeMetadata ISwiftObject.GetTypeMetadata()
    {
        return TypeMetadata.Cache.GetOrAdd(typeof(Box<T0>), _ =>
            _MetadataAccessor(TypeMetadataRequest.Complete,
                TypeMetadata.GetTypeMetadataOrThrow<T0>()));
    }

    public T0 Value { get => ...; set => ...; }
}
```

**Files added**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs`

**Files modified**:
- `src/Swift.Bindings/src/Model/TypeDecl/TypeDecl.cs` - Added `GenericParameters`, `IsGeneric`
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` - Removed generic skip logic, parse generic params
- `src/Swift.Bindings/src/Marshaler/NameProvider.cs` - Added `GetGenericTypeMappingForType()`
- `src/Swift.Bindings/src/Marshaler/IEnvironment.cs` - Added `GenericTypeMapping` to `TypeEnvironment`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Use `GenericTypeEmitter` in handlers

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/EmitterTests/GenericTypeEmitterTests.cs` - 12 new tests
- `src/Swift.Bindings/tests/UnitTests/ParserTests/UnboundGenericsParserTests.cs` - 5 new tests

---

## 3.5 Dictionary with Existential Values (Any)
**Status**: IMPLEMENTED (January 2026)

**Problem**: Dictionaries with `Any` (Swift's zero-protocol existential type) as values weren't handled:
```
PropertyHandler: Couldn't process property userInfo of type Swift.Dictionary<Nuke.ImageRequest.UserInfoKey, Swift.Any>
```

**Root Cause**: Two issues:
1. `BoundGenericsHandler.TranslateTypeSpecToCSharp` didn't handle `ProtocolListTypeSpec` (which represents `Any` with 0 protocols)
2. `TypeSpecParser` didn't recognize bare `"Any"` string as a `ProtocolListTypeSpec`
3. `SwiftABIParser` didn't handle `ProtocolComposition` nodes in `CreateTypeSpec`

**Solution**:
1. Added `ExistentialHandler` to `BoundGenericsHandler` for existential type translation
2. Added `ProtocolListTypeSpec` handling in `TranslateTypeSpecToCSharp` to map to `ExistentialContainer0`
3. Updated `TypeSpecParser` to recognize `"Any"` as `ProtocolListTypeSpec` (empty protocol list)
4. Added `CreateProtocolCompositionTypeSpec` to `SwiftABIParser` for `ProtocolComposition` nodes
5. Updated `MethodHandler` to handle `ProtocolListTypeSpec` in both wrapper and P/Invoke signature builders
6. Added `Swift.Dictionary` to `s_bufferTypeMap` in `BoundGenericsHandler`

**Generated code example**:
```csharp
public SwiftDictionary<ImageRequest.UserInfoKey, ExistentialContainer0> UserInfo
{
    get => UserInfo_Get();
    set => UserInfo_Set(value);
}
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs` - Added ExistentialHandler, ProtocolListTypeSpec handling
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs` - Added ProtocolComposition handling
- `src/Swift.Bindings/src/Model/TypeSpecParsing/TypeSpecParser.cs` - Recognize "Any" as ProtocolListTypeSpec
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - ProtocolListTypeSpec in signature builders
- `src/Swift.Runtime/src/Swift/Runtime/TypeMetadata.cs` - Added IExistentialContainer handling, swift_getExistentialTypeMetadata P/Invoke

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/BoundGenericsHandlerTests.cs` - Tests for existential type arguments

**Also fixed**: Closure callback signature mismatch (CS8757 error)
- Made `TranslateTypeSpecToPInvokeType` public in `ClosureHandler`
- Updated to only use direct types for known blittable primitives (nint, int, long, etc.)
- All other types (including `Swift.Data`) now use `void*` for callback safety
- `ClosureEmitter.GetCallbackParameterType` now delegates to `ClosureHandler` for consistency

---

## Summary

Phase 3 addressed major method signature gaps:
- Existential types (`any Protocol`) with ExistentialContainer structs
- ExistentialContainerFactory for C# construction
- Protocol Associated Types (PATs) with generic proxies
- Closures with bound generic parameters
- Closures returning bound generic types via indirect return
- Generic constructors
- Unbound generic methods
- Unbound generic type definitions
- Dictionary with existential values

This phase enabled binding of methods with complex Swift type patterns.
