# Nuke Swift Binding Roadmap

This document tracks the effort to make the [Nuke](https://github.com/kean/Nuke) Swift image loading library fully consumable from .NET for iOS. Nuke serves as a real-world test case for validating the binding generator against a production Swift library.

## Why Nuke?

Nuke is an ideal test case because it:
- Is a popular, actively maintained Swift library (MIT licensed)
- Uses modern Swift features (async/await, protocols, generics)
- Has a non-trivial API surface (~30 classes, 8 protocols)
- Exercises many code paths in the binding generator

---

## Baseline Analysis

Initial binding generation revealed these gap categories:

| Category | Count | Impact |
|----------|-------|--------|
| Protocol conformance descriptors not found | 200 | Warnings only, types still emit |
| Unsupported accessor kinds (set/_modify) | 128 | Properties are read-only |
| Unsupported method signatures | 67 | Methods skipped |
| Unsupported property types | 70 | Properties skipped |
| Generic protocol types unsupported | 8 | Types skipped entirely |
| Unsupported constructor signatures | 22 | Constructors skipped |

**Initial result**: Generated 417KB of C# bindings with 52 types, but many methods/properties skipped.

---

## Current State

**Generated**: ~15,400+ lines of C# code
- 30+ classes implementing `ISwiftObject`
- 8 protocol interfaces
- Property getters and setters
- P/Invoke declarations with Swift calling convention
- **0 compilation errors** (down from 95+)

**Remaining gaps**:
- ~70 methods with unsupported signatures (skipped) - reduced due to existential parameter support
- ~10 properties with unsupported types (skipped) - reduced from 24
- Enum cases with associated values may have unsupported parameter types
- ~~Methods with unsupported closure parameter types (e.g., closures with `Result<T,E>` parameters)~~ FIXED (3.3)
- ~~Closures that *return* bound generic types (e.g., `Func<T, SwiftOptional<U>>`)~~ FIXED (3.3.1)
- ~~Properties with existential types are skipped~~ FIXED (3.1.1)
- ~~Generic constructors skipped~~ FIXED (3.4)
- **ObjC types use `Swift.*` wrappers instead of existing .NET iOS bindings** (see 2.8) - Major UX issue

**Fixed issues**:
- ~~11 errors related to enum types in Optional<T> parameters~~ FIXED (Phase 2.5)
- ~~20 errors related to `SwiftOptional<T>` missing `PayloadBuffer` property~~ FIXED (Phase 2.3)
- ~~266 hardcoded library paths~~ FIXED with `-l` flag
- ~~95 compilation errors from code generation bugs~~ FIXED (Phase 1.4)
- ~~24 compilation errors from other code generation issues~~ FIXED (Phase 1.5)
- ~~3 naming collision errors~~ FIXED (Phase 2.2.1)
- ~~Methods with existential parameters skipped~~ FIXED (Phase 3.1) - Now generate `ExistentialContainer{N}` types
- ~~Closures with bound generic parameters (Result<T,E>, Array<T>, etc.) skipped~~ FIXED (Phase 3.3) - Now properly marshal to C# delegates
- ~~Closures returning bound generics (SwiftOptional<T>, SwiftResult<S,E>) skipped~~ FIXED (Phase 3.3.1) - Now use indirect return marshalling
- ~~Properties with existential types skipped~~ FIXED (Phase 3.1.1) - Now generate `ExistentialContainer{N}` types
- ~~Generic constructors skipped~~ FIXED (Phase 3.4) - Now use same infrastructure as generic methods

---

## Phase 1: Infrastructure (Required to Run Anything)

### 1.1 Fix Hardcoded Library Paths
**Status**: DONE

**Problem**: All `DllImport` attributes contained absolute paths.

**Solution**: Added `-l` / `--library-name` CLI flag to specify runtime library name:
```bash
dotnet run --project src/Swift.Bindings/src -c Release -- \
  -a Nuke.abi.json \
  -d /path/to/Nuke.framework/Nuke \
  -t Nuke.tbd \
  -l "Nuke" \
  -o output/
```

The `-d` flag specifies the dylib for metadata extraction during generation.
The `-l` flag specifies the library name used in generated `DllImport` attributes.

**Important**: If the library name starts with `@` (e.g., `@rpath/Nuke.framework/Nuke`), you must escape it with a backslash because .NET interprets `@filename` as a response file directive:
```bash
-l '\@rpath/Nuke.framework/Nuke'
```

**Files modified**:
- `src/Swift.Bindings/src/Program.cs` - Added library name argument
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Separate dylib path from runtime library name

### 1.2 iOS Workload Setup
**Status**: DONE

```bash
sudo dotnet workload install ios maui maui-ios android
```

**Note**: The test app uses its own `global.json` and `Directory.Build.props/targets` files to isolate from the repo's Arcade SDK build system.

### 1.3 Framework Bundling
**Status**: DONE

Test project configured with:
- References xcframework
- Uses `NativeReference` for framework bundling
- **Build succeeds** with generated bindings

### 1.4 Code Generation Bugs
**Status**: DONE

Three bugs in the binding generator caused ~95 compilation errors. All fixed:

**1. Duplicate Interface Members** (~20 errors)
- `ProtocolHandler` now tracks emitted properties and methods to prevent duplicates
- Added `GetMethodSignatureKey()` for method signature comparison
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

**2. Missing ISwiftObject Implementations** (~60 errors)
- `EnumHandler` now emits stub implementations for all ISwiftObject methods
- Added `EmitEnumISwiftObjectImplementation()` method
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

**3. Duplicate Method Definitions** (~15 errors)
- `BaseHandler.HandleBaseDecl()` now tracks method signatures to prevent duplicates
- `NameProvider.GetPInvokeName()` now includes mangled name hash for uniqueness
- **Files modified**:
  - `src/Swift.Bindings/src/Marshaler/IHandler.cs`
  - `src/Swift.Bindings/src/Marshaler/NameProvider.cs`

### 1.5 Additional Code Generation Issues
**Status**: DONE

After fixing the Phase 1.4 bugs, 24 additional errors were revealed. All fixed:

**1. Enum Property Access Errors** (~8 errors)
- Added `_payload` field, `_payloadSize`, and `Payload` property to `EnumHandler`
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

**2. Missing Type Definitions** (~2 errors)
- Refactored `URL` class to use `SwiftSafeHandle<URL>` for P/Invoke compatibility
- Added protocol type detection to skip methods with interface parameters
- **Files modified**:
  - `src/Swift.Runtime/src/Swift/URL.cs`
  - `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**3. Missing Property Accessors** (~4 errors)
- `PropertyHandler` now checks if accessor methods will be skipped before emitting property
- Skips properties with `AnyType` or other unsupported types in signature
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`

**4. Bound Generic Buffer Issues** (~1 error)
- Added `EmitBoundGenericArguments()` call to `WrapperEmitter.EmitConstructor()`
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

**5. Protocol Parameter Errors** (~9 errors)
- `WrapperSignatureBuilder` now detects `TypeRecordKind.Protocol` and uses `AnyType` placeholder
- Methods with protocol parameters/return types are skipped (interfaces don't have `Payload`)
- **Files modified**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

---

## Phase 2: Type System Gaps

### 2.1 Optional<T> Support
**Status**: DONE

**Problem**: Properties returning `Optional<T>` were skipped:
```
Skipping property 'dataCache' of type 'Optional' from module 'Swift'
Skipping property 'url' of type 'Optional' from module 'Swift'
```

**Solution**: Two fixes were required:

1. **Type Database Registration**: Added `Swift.Optional` to `SwiftDatabase.xml` mapping to `SwiftOptional`:
   ```xml
   <entity managedNameSpace="Swift" managedTypeName="SwiftOptional">
       <typedeclaration kind="struct" name="Optional" module="Swift" mangledName="$sSq" frozen="true" requiresMemoryManagement="true" />
   </entity>
   ```

2. **Protocol Handler Bound Generics**: Fixed `EmitInterfaceProperty` and `EmitInterfaceMethod` in `ProtocolHandler` to use `BoundGenericsHandler` for proper generic type translation (e.g., `Optional<Int>` → `SwiftOptional<Int64>`).

**Files modified**:
- `src/Swift.Runtime/src/Swift/SwiftDatabase.xml` - Added Optional type mapping
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Fixed protocol handler bound generic support

**Tests added**:
- `src/Swift.Bindings/tests/UnitTests/MarshalerTests/OptionalHandlerTests.cs` - 12 new tests

### 2.2 Foundation/Platform Types
**Status**: DONE (but see 2.8 for planned refactoring)

**Types implemented**:
| Swift Type | Status | C# Mapping | Module |
|------------|--------|------------|--------|
| `URL` | DONE | `Swift.URL` with `SwiftSafeHandle` | Foundation |
| `OperationQueue` | DONE | `Swift.OperationQueue` | Foundation |
| `DispatchQueue` | DONE | `Swift.DispatchQueue` | Dispatch |
| `NSImage` | DONE | `Swift.NSImage` | AppKit |
| `NSColor` | DONE | `Swift.NSColor` | AppKit |
| `CIContext` | DONE | `Swift.CIContext` | CoreImage |
| `UIImage` | DONE | `Swift.UIImage` | UIKit |
| `URLRequest` | DONE | `Swift.URLRequest` with `SwiftSafeHandle` | Foundation |
| `URLResponse` | DONE | `Swift.URLResponse` with `SwiftSafeHandle` | Foundation |
| `Hasher` | DONE | `Swift.Hasher` with `SwiftSafeHandle` | Swift |
| `UIColor` | DONE | `UIKit.UIColor` (ObjC-bridged) | UIKit |
| `URLSession` | DONE | `Foundation.NSUrlSession` (ObjC-bridged) | Foundation |
| `URLSessionConfiguration` | DONE | `Foundation.NSUrlSessionConfiguration` (ObjC-bridged) | Foundation |
| `URLCache` | DONE | `Foundation.NSUrlCache` (ObjC-bridged) | Foundation |

> **Note**: These `Swift.*` wrapper types work but create UX friction. Section 2.8 describes the planned refactoring to instead map these Objective-C types to the existing .NET iOS bindings (e.g., `UIKit.UIImage` instead of `Swift.UIImage`). This will allow seamless interop with standard .NET iOS code.

**Files added**:
- `src/Swift.Runtime/src/Swift/OperationQueue.cs`
- `src/Swift.Runtime/src/Swift/DispatchQueue.cs`
- `src/Swift.Runtime/src/Swift/NSImage.cs`
- `src/Swift.Runtime/src/Swift/NSColor.cs`
- `src/Swift.Runtime/src/Swift/CIContext.cs`
- `src/Swift.Runtime/src/Swift/UIImage.cs`
- `src/Swift.Runtime/src/Swift/URLRequest.cs`
- `src/Swift.Runtime/src/Swift/URLResponse.cs`
- `src/Swift.Runtime/src/Swift/Hasher.cs`
- `src/Swift.Runtime/src/Swift/DispatchDatabase.xml`
- `src/Swift.Runtime/src/Swift/AppKitDatabase.xml`
- `src/Swift.Runtime/src/Swift/CoreImageDatabase.xml`
- `src/Swift.Runtime/src/Swift/UIKitDatabase.xml`

**Files modified**:
- `src/Swift.Runtime/src/Swift/FoundationDatabase.xml` - Added OperationQueue, URLRequest, URLResponse, URLSession, URLSessionConfiguration, URLCache mappings
- `src/Swift.Runtime/src/Swift/SwiftDatabase.xml` - Added Hasher mapping
- `src/Swift.Runtime/src/Swift/UIKitDatabase.xml` - Added UIColor mapping
- `src/Swift.Runtime/src/Swift/Runtime/KnownLibraries.cs` - Added library paths
- `src/Swift.Bindings/src/Program.cs` - Load new database files

### 2.2.1 Naming Collision Bug Fixes
**Status**: DONE

**1. Async Callback Duplicate Members** (CS0102)
- **Problem**: Multiple async method overloads generated identical callback field/method names
- **Example**: `image(URL)` and `image(ImageRequest)` both generated `s_imageCallback`
- **Solution**: Added hash suffix from mangled name to callback names
- **Result**: `s_imageCallback` → `s_imageCallback_40A088FB`

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/NameProvider.cs` - Added `GetAsyncCallbackFieldName()` and `GetAsyncCallbackMethodName()`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Use unique callback names

**2. Property/Method Name Collision** (CS0102)
- **Problem**: Swift allows property and method with same name; C# does not
- **Example**: `withDataCache` property and `withDataCache(name:sizeLimit:)` method
- **Solution**: Methods that collide with properties get "Method" suffix
- **Result**: `withDataCache()` → `withDataCacheMethod()`

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/NameProvider.cs` - Added `GetMethodName()` for collision detection
- `src/Swift.Bindings/src/Marshaler/IEnvironment.cs` - Added `SiblingPropertyNames` and `CSharpMethodName`
- `src/Swift.Bindings/src/Marshaler/IHandler.cs` - Pass property names to method handler
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Collect property names
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Use `CSharpMethodName`

### 2.3 SwiftOptional PayloadBuffer Support
**Status**: DONE

**Problem**: Generated code used `SwiftOptional<T>.PayloadBuffer` but the type didn't have this property.

**Solution**: Refactored `SwiftOptional<T>` to use `SwiftSafeHandle<SwiftOptional<T>>` storage (matching `SwiftArray<T>`, `SwiftSet<T>`, `SwiftDictionary<K,V>` pattern).

**Files modified**:
- `src/Swift.Runtime/src/Swift/SwiftOptional.cs` - Complete refactoring to SafeHandle pattern

### 2.4 Property Setters
**Status**: DONE

**Problem**: Property setters were implemented but had a bug affecting frozen structs. Frozen struct setters incorrectly used value semantics (`SwiftSelf<T>`) instead of pointer semantics (`SwiftSelf`), causing the setter to operate on a copy rather than the original struct.

**Root cause**: Per `docs/binding-properties.md`:
- Frozen struct **getters** use `SwiftSelf<T>` (value in registers) ✓
- Frozen struct **setters** should use `SwiftSelf` (pointer) because they modify memory in-place ✗

**Solution**:
1. Added `MarshallingHelpers.MethodIsSetter()` helper to detect setter methods
2. Updated `PInvokeSignatureBuilder.HandleSwiftSelf()` to use `SwiftSelf` (pointer) for frozen struct setters
3. Updated `WrapperEmitter` to use a `fixed` block for frozen struct setters, getting a pointer to `this`

**Generated code example** (frozen struct setter):
```csharp
public int MyProperty
{
    set => MyProperty_Set(value);
}

public unsafe void MyProperty_Set(int value)
{
    try
    {
        fixed (MyStruct* __self = &this)
        {
            var self = new SwiftSelf(__self);
            PInvoke_MyProperty_Set(value, self);
        }
    }
    finally { }
}
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs` - Added `MethodIsSetter()` helper
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Fixed P/Invoke signature and wrapper emission

**Note**: The `_modify` accessor kind remains unsupported (it's an internal Swift optimization). Properties with only `_modify` accessors are still read-only in C#.

### 2.5 Enum Type Registration
**Status**: DONE

**Problem**: Enums were not being registered in the type database. This caused:
1. `Optional<SomeEnum>` to become `SwiftOptional<AnyType>` instead of `SwiftOptional<SomeEnum>`
2. Properties like `cacheType` (type `Optional<CacheType>`) to be skipped
3. Generic types like `Result<T, E>` to generate invalid code

**Root cause**: The `ProcessEnum` method in `ModuleProcessor.cs` was empty.

**Solution**:
1. Added `IsFrozen`, `MetadataAccessor`, `Conformances` properties to `EnumDecl`
2. Updated `CreateEnumDecl` in `SwiftABIParser.cs` to populate new properties
3. Implemented `ProcessEnum` in `ModuleProcessor.cs`
4. Fixed AnyType generic parameter bug in `BoundGenericsHandler.cs`

**Files modified**:
- `src/Swift.Bindings/src/Model/TypeDecl/EnumDecl.cs`
- `src/Swift.Bindings/src/Parser/SwiftABIParser.cs`
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs`
- `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs`

### 2.6 Enum Case Constructors
**Status**: DONE

**Problem**: Enum cases without associated values were implemented as static properties, but cases with associated values were skipped.

**Solution**: Modified `EnumHandler` to emit:
- **Simple cases** (no associated values) → Static properties (existing behavior)
- **Cases with associated values** → Static methods with parameters

**Implementation details**:
1. Removed the filter that excluded cases with associated values
2. Added `EmitEnumCaseWithAssociatedValues()` method that:
   - Maps Swift associated value types to C# parameter types
   - Generates P/Invoke calls with proper argument marshalling
   - Handles non-frozen types by accessing `.Payload` property

**Generated code example**:
```csharp
// Simple case (no associated values) - static property
public static MyResult Success
{
    get
    {
        var result = new MyResult();
        IntPtr casePtr = PInvoke_Success();
        result._payload = new SwiftSafeHandle<MyResult>(casePtr);
        return result;
    }
}

// Case with associated values - static method
public static MyResult Failure(SwiftString message)
{
    var result = new MyResult();
    IntPtr casePtr = PInvoke_Failure(message.Payload);
    result._payload = new SwiftSafeHandle<MyResult>(casePtr);
    return result;
}
```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - Added `EmitEnumCaseWithAssociatedValues()` and helper methods

### 2.7 Cross-Platform Binding Generation (iOS on macOS)
**Status**: DONE

**Problem**: The binding generator couldn't generate iOS bindings when running on macOS because `DynamicLibraryLoader` can't load iOS dylibs.

**Solution**: Made dylib loading optional for structs and classes. When the dylib can't be loaded:
1. The generator logs a warning
2. Continues without metadata (size info looked up at runtime)
3. Generated bindings still work correctly

**Files modified**:
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Added try/catch around `DynamicLibraryLoader.invoke()`

### 2.8 Reuse Existing .NET iOS Bindings for Objective-C Types
**Status**: COMPLETE (January 2026)
**Impact**: Major UX improvement for ObjC-imported types

**Key Discovery**: There are two categories of types here with very different characteristics:

1. **ObjC classes imported into Swift** (mangled with `So` prefix, `C` suffix) - These ARE the same ObjC objects and can be directly mapped to .NET iOS bindings. ✅ **Done**

2. **Swift native structs** (mangled with `s10Foundation` prefix, `V` suffix) - These are VALUE types with different ABI. Remapping is not practical. ✅ **Conscious decision to keep as Swift.* wrappers**

**ObjC Classes - DONE ✅**

| Swift Type | Mangled Name | .NET iOS Type | Status |
|------------|--------------|---------------|--------|
| `UIImage` | `$sSo7UIImageC` | `UIKit.UIImage` | ✅ Working |
| `URLResponse` | `$sSo15NSURLResponseC` | `Foundation.NSUrlResponse` | ✅ Working |
| `OperationQueue` | `$sSo16NSOperationQueueC` | `Foundation.NSOperationQueue` | ✅ Working |
| `NSImage` | `$sSo7NSImageC` | `AppKit.NSImage` | ✅ Working |

These use `objcBridged="true"` in the type database and work because Swift literally imports the ObjC class - the pointer is the same `objc_object*`.

**Swift Structs - Intentionally Kept as Swift.* Wrappers ✅**

| Swift Type | Mangled Name | C# Type | Decision |
|------------|--------------|---------|----------|
| `URL` | `$s10Foundation3URLV` | `Swift.URL` | Keep as-is |
| `Data` | `s10Foundation4DataV` | `Swift.Data` | Keep as-is |
| `URLRequest` | `$s10Foundation10URLRequestV` | `Swift.URLRequest` | Keep as-is |

**Why this is the right decision:**

These Swift structs are fundamentally different from their ObjC counterparts:
- **Different memory layout**: Swift structs are value types (16+ bytes inline), ObjC classes are pointers (8 bytes)
- **Different ABI**: Swift expects struct bytes passed by value, not an `objc_object*` pointer
- **Different semantics**: Value types copy on assignment, reference types share

Bridging would require calling Swift's internal `_bridgeToObjectiveC()` functions at every boundary crossing - significant complexity for marginal benefit.

**Practical impact is low** because these types are typically **inputs** you construct:
```csharp
// This is fine - construct from string
var request = new ImageRequest(new SwiftString("https://example.com/image.jpg"));

// The important outputs (UIImage) already use .NET iOS types
UIImage image = response.Image;  // ✅ Works seamlessly
```

**Types that inherently need Swift.* wrappers** (pure Swift, no ObjC equivalent):
- `SwiftString` (Swift.String has different internal representation than NSString)
- `SwiftArray<T>`, `SwiftSet<T>`, `SwiftDictionary<K,V>` (Swift collections)
- `SwiftOptional<T>`
- All generated types from Swift libraries (e.g., `Nuke.ImagePipeline`, `Nuke.ImageRequest`)

---

## Phase 3: Method Signature Gaps

### 3.1 Existential Types (`any Protocol`)
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

**Remaining work**:
- ~~Properties with existential types (currently skipped)~~ FIXED (3.1.1)
- ~~Existentials inside closures (`(any Protocol) -> Void`)~~ FIXED (2026-01-30)
- ~~Existentials inside tuples (`(Int, any Protocol)`)~~ FIXED (2026-01-30)
- Helper methods for constructing `ExistentialContainer{N}` from C# objects
- Protocol witness table lookup at runtime

### 3.1.1 Existential Properties
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

**Note**: Many "unsupported signature" warnings in Nuke are due to **unsupported closure types** (closures with `Result<T,E>` parameters), not existential types. The closure handler needs to be extended to support more complex closure signatures.

### 3.2 Generic Protocol Types
**Status**: TODO

Protocols with associated types or generic requirements are skipped entirely.

**Skipped Nuke types**:
- `ImageProcessing` - Core image processing protocol
- `ImageDecoding` / `ImageEncoding` - Codec protocols
- `ImageCaching` / `DataCaching` - Cache protocols
- `DataLoading` - Data loader protocol
- `ImagePipelineDelegate` - Pipeline delegate protocol

### 3.3 Closure/Callback Parameters
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

**Current limitations**:
- ~~Closures *returning* bound generic types (e.g., `Func<T, SwiftOptional<U>>`) are excluded~~ FIXED (3.3.1)
- ~~`SwiftResult<T,E>` has `IsSuccess`/`IsFailure` properties but value extraction not yet implemented~~ FIXED (2026-01-30)

### 3.3.1 Closure Bound Generic Returns
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

### 3.4 Generic Constructors
**Status**: IMPLEMENTED (January 2026)

**Problem**: Generic constructors were explicitly skipped with TODO referencing issue #2890:
```
Constructor init has unsupported generic parameters
```

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

### 3.5 Dictionary with Custom Keys
**Status**: TODO

Dictionaries with custom key types aren't handled:
```
PropertyHandler: Couldn't process property userInfo of type Swift.Dictionary<Nuke.ImageRequest.UserInfoKey, Swift.Any>
```

---

## Phase 4: Runtime Infrastructure

### 4.1 ISwiftObject Implementation
**Status**: DONE

Implemented full `ISwiftObject` methods for both `ClassHandler` and `EnumHandler`:

1. **ClassISwiftObjectMethodWriter**:
   - `GetTypeMetadata()` via P/Invoke to class metadata accessor
   - `NewFromPayload()` for creating instances from native handles
   - `MarshalToSwift()` using `ValueWitnessTable.InitializeWithCopy`
   - `GetProtocolConformanceDescriptor()` with dictionary lookup

2. **EnumISwiftObjectMethodWriter**:
   - Same pattern as classes, adapted for enum types

**Runtime verification** (iOS simulator):
- Struct metadata (ImageProcessingContext) - Size: 34
- Class metadata (ImagePipeline) - Size: 8
- `ImagePipeline.shared` property - Works correctly

### 4.2 Memory Management
**Status**: TODO

Need to verify:
- `swift_retain` / `swift_release` calls work correctly
- `SwiftSafeHandle<T>` properly releases resources
- No memory leaks in object lifecycle

---

## Known Limitations

### Async P/Invoke with SafeHandle
**Status**: FIXED (January 2026)

The .NET runtime does not support passing non-blittable types (like `SafeHandle`) through P/Invoke with Swift calling convention.

**Original error**:
```
InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.
```

**Solution implemented**: Two-part fix for async methods with non-frozen type parameters:

1. **C# side**: Use `IntPtr` instead of `SafeHandle` for non-frozen parameters in async P/Invoke declarations

2. **Swift wrapper side**: Generate proper Swift copy semantics for non-frozen parameters:
   ```swift
   let _forCopy = UnsafeMutablePointer<Nuke.ImageRequest>.allocate(capacity: 1)
   _forCopy.initialize(from: _for.assumingMemoryBound(to: Nuke.ImageRequest.self), count: 1)

   Task {
       defer { _forCopy.deinitialize(count: 1); _forCopy.deallocate() }
       let result = try! await image(for: _forCopy.pointee)
       callback(result, task)
   }
   ```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Lines 957-1076

### URL.FromString Non-Blittable Issue
**Status**: Known issue

The `Swift.URL.FromString()` method fails with the same non-blittable type error.

**Workaround**: Use `ImageRequest(SwiftString)` constructor directly.

### Existential Types in Enum Cases
**Status**: FIXED (January 2026)

Enum cases with associated values containing tuples with existential types (like `any Swift.Error`) previously caused the binding generator to crash.

**Solution**: Added `IsExistentialTypeName()` helper in `TypeDatabaseExtensions` to detect and handle existential type names (returning `AnyType` instead of attempting to parse as module-qualified).

**Files modified**:
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs`

**Remaining issue**: Enum cases with generic dictionary associated values may generate invalid code (missing type arguments). This is a separate bug.

### Protocol Conformance Descriptors
**Status**: Warnings only

200 warnings about missing protocol conformance descriptors for built-in Swift 5.9+ protocols:
- `Swift.Copyable`
- `Swift.Escapable`
- `Swift.Sendable`

Bindings still generate, but conformance information is incomplete.

---

## Phase 5: Testing & Validation

### Test Results (January 2026)

| Test | Status | Notes |
|------|--------|-------|
| Struct metadata retrieval | PASS | ImageProcessingContext size: 34 |
| Class metadata retrieval | PASS | ImagePipeline size: 8 |
| `ImagePipeline.shared` | PASS | Successfully returns singleton |
| `SwiftString` creation | PASS | Works correctly |
| `ImageRequest(SwiftString)` | PASS | Constructor works |
| Async non-frozen parameter copy | PASS | Swift wrapper uses proper copy semantics |
| ObjC type remapping | PASS | UIImage returns as UIKit.UIImage |
| Existential type handling | PASS | Generator handles `any Protocol` without crashing |
| Existential parameters | PASS | Methods/constructors with `any Protocol` params generate `ExistentialContainer{N}` |
| `pipeline.image(request)` | PASS | Fixed defer cleanup issue |
| Swift wrapper imports | PASS | Generator now emits imports automatically |
| Closure bound generic params | PASS | `loadImage(completion:)` generates with `Action<SwiftResult<...>>` (2026-01-30) |
| Closure bound generic returns | PASS | Closures returning `SwiftOptional<T>` use indirect return marshalling (2026-01-30) |
| Existential properties | PASS | Properties with `any Protocol` generate `ExistentialContainer{N}` (2026-01-30) |
| Generic constructors | PASS | Constructors with generic params use TypeMetadata/witness tables (2026-01-30) |
| Closures in constructors | PASS | Constructors with closure params emit callbacks and marshalling (2026-01-30) |
| Hasher type mapping | PASS | 21 new `hash(into:)` methods generated (2026-01-30) |
| UIColor type mapping | PASS | ObjC-bridged to UIKit.UIColor, 7 usages (2026-01-30) |
| URLSession type mappings | PASS | URLSession, URLSessionConfiguration, URLCache ObjC-bridged, 8 usages (2026-01-30) |

---

## Setting Up a Test Environment

### Directory Structure
```
BindingTesting/{LibraryName}/
├── {LibraryName}.xcframework/   # Pre-built framework
├── output/                       # Generated bindings (macOS)
├── output-ios/                   # Generated bindings (iOS)
├── {LibraryName}TestApp/         # .NET test project
│   ├── {LibraryName}TestApp.csproj
│   ├── Program.cs
│   └── NuGet.config
├── global.json                   # Isolates from repo's Arcade SDK
├── Directory.Build.props         # Stops MSBuild traversal
└── Directory.Build.targets       # Stops MSBuild traversal
```

### Build Isolation Files

**global.json**:
```json
{
  "sdk": {
    "version": "10.0.100"
  }
}
```

**Directory.Build.props** / **Directory.Build.targets**:
```xml
<Project>
  <!-- Intentionally empty to stop traversal -->
</Project>
```

---

## Commands Reference

```bash
# Install iOS workload
sudo dotnet workload install ios

# Generate macOS bindings
dotnet run --project src/Swift.Bindings/src -c Release -- \
  -a BindingTesting/Nuke/output/Nuke-macos.abi.json \
  -d BindingTesting/Nuke/NukeSource/DerivedData/Build/Products/Release/Nuke.framework/Versions/A/Nuke \
  -t BindingTesting/Nuke/output/Nuke-macos.tbd \
  -l "Nuke" \
  -o BindingTesting/Nuke/output/

# Generate iOS bindings (works on macOS host)
dotnet run --project src/Swift.Bindings/src -c Release -- \
  -a BindingTesting/Nuke/output/Nuke-sim.abi.json \
  -d BindingTesting/Nuke/Nuke.xcframework/ios-arm64_x86_64-simulator/Nuke.framework/Nuke \
  -t BindingTesting/Nuke/output/Nuke-sim.tbd \
  -l "Nuke" \
  -o BindingTesting/Nuke/output-ios/

# Build and run test app
dotnet build BindingTesting/Nuke/NukeTestApp -c Debug -t:Run

# Run unit tests
dotnet test src/Swift.Bindings/tests/UnitTests
dotnet test src/Swift.Runtime/tests
```

### Building Nuke from Source

```bash
# Clone and build Nuke for macOS
cd BindingTesting/Nuke
git clone https://github.com/kean/Nuke.git NukeSource
cd NukeSource
xcodebuild -scheme Nuke -configuration Release -destination 'platform=macOS' \
    BUILD_LIBRARY_FOR_DISTRIBUTION=YES -derivedDataPath ./DerivedData

# Generate ABI and TBD
xcrun swift-frontend -compile-module-from-interface \
    "./DerivedData/Build/Products/Release/Nuke.framework/Versions/A/Modules/Nuke.swiftmodule/arm64-apple-macos.swiftinterface" \
    -target arm64-apple-macos14.0 -module-name "Nuke" \
    -sdk "$(xcrun --sdk macosx --show-sdk-path)" \
    -emit-abi-descriptor-path "./output/Nuke-macos.abi.json"

xcrun tapi stubify \
    ./DerivedData/Build/Products/Release/Nuke.framework/Nuke \
    --filetype=tbd-v4 -o ./output/Nuke-macos.tbd
```

---

## Success Criteria

The binding is "complete" when we can:

```csharp
// Create a pipeline
var pipeline = ImagePipeline.Shared;

// Create a request
var url = new URL("https://example.com/image.jpg");
var request = new ImageRequest(url);

// Load an image (async)
var response = await pipeline.Image(request);
var image = response.Image; // UIImage
```

**Checklist**:
- [x] Basic type generation
- [x] Property getters/setters
- [x] Library path configuration (`-l` flag)
- [x] iOS build infrastructure
- [x] Fix code generation bugs (Phase 1.4, 1.5)
- [x] Optional<T> handling
- [x] Foundation type wrappers
- [x] Fix naming collision bugs
- [x] Fix SwiftOptional<T> PayloadBuffer
- [x] URL type support
- [x] NSImage/UIImage support
- [x] Fix enum type registration
- [x] Proper ISwiftObject implementation
- [x] Property setters
- [x] URLRequest/URLResponse support
- [x] Enum case constructors (simple cases work, cases with associated values emit static methods)
- [x] **Async method support** - FIXED: Uses IntPtr + proper Swift copy semantics
- [x] **ObjC type remapping** - ObjC classes (UIImage, URLResponse, OperationQueue) map to .NET iOS types for both params and returns
- [x] **Existential type handling** - Generator handles `any Protocol` without crashing
- [x] **Swift wrapper imports** - Generator now emits imports automatically
- [x] **Async non-frozen parameter cleanup** - FIXED: Cleanup runs after callback, not in defer
- [x] **ObjC/Swift type strategy complete** - ObjC classes (UIImage, etc.) map to .NET iOS types; Swift structs (URL, Data) intentionally kept as Swift.* wrappers (see 2.8)
- [x] **Existential parameters** - Methods with `any Protocol` parameters now generate valid `ExistentialContainer{N}` types (2026-01-30)
- [x] **Closure bound generic parameters** - Closures accepting `Result<T,E>`, `Array<T>`, `Optional<T>`, etc. now generate valid C# code (2026-01-30)
- [x] **Closure bound generic returns** - Closures returning `SwiftOptional<T>`, `SwiftResult<S,E>`, etc. now use indirect return marshalling (2026-01-30)
- [x] **Existential properties** - Properties with `any Protocol` types now generate `ExistentialContainer{N}` types (2026-01-30)
- [x] **Generic constructors** - Constructors with generic parameters now work using same infrastructure as methods (2026-01-30)
- [x] **Hasher type support** - `hash(into:)` methods now generate correctly (2026-01-30)
- [x] **UIColor type support** - ObjC-bridged to `UIKit.UIColor` (2026-01-30)
- [x] **URLSession types support** - URLSession, URLSessionConfiguration, URLCache ObjC-bridged (2026-01-30)
- [x] Existentials in closures - ClosureHandler now supports `any Protocol` parameters/returns (2026-01-30)
- [x] Existentials in tuples - TupleHandler now supports `any Protocol` elements (2026-01-30)
- [x] SwiftResult value extraction - `Success`, `Failure`, `TryGetSuccess`, `TryGetFailure`, `Match` (2026-01-30)
- [x] Nested type handling in generics - `SwiftTypeName.FromTypeSpec` traverses `InnerType` chain (2026-01-30)
- [x] Integration tests fixed - Updated naming conventions (PascalCase), 684 tests pass (2026-01-30)
- [x] Runtime testing of async methods on iOS simulator - VERIFIED (2026-01-30)

---

## iOS Simulator Testing Workflow

### Quick Deploy and Test

The `dotnet build -t:Run` command times out waiting for the app to exit. For faster iteration:

```bash
# 1. Build the app
cd /Users/wojo/Dev/swift-bindings
dotnet build BindingTesting/Nuke/NukeTestApp -c Debug

# 2. Install and launch with console output (backgrounded with timeout)
xcrun simctl install booted BindingTesting/Nuke/NukeTestApp/bin/Debug/net10.0-ios/iossimulator-arm64/NukeTestApp.app && \
(xcrun simctl launch --console --terminate-running-process booted com.swiftbindings.nuketestapp 2>&1 &); \
sleep 5; echo "---DONE---"
```

The `sleep 5` captures the first 5 seconds of output. Adjust as needed.

### Framework Resolution for Bundled Libraries

Bundled frameworks (like Nuke) need a `NativeLibrary` resolver because `DllImport("Nuke")` doesn't know to look in `@rpath/Nuke.framework/Nuke`. Add this to your app's startup:

```csharp
using System.Reflection;
using System.Runtime.InteropServices;

public class Application
{
    static void Main(string[] args)
    {
        // Register resolver BEFORE any Swift types are accessed
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ResolveBundledFramework);
        UIApplication.Main(args, null, typeof(AppDelegate));
    }

    static IntPtr ResolveBundledFramework(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "Nuke" || libraryName == "SwiftBindings")
        {
            var frameworkPath = $"@rpath/{libraryName}.framework/{libraryName}";
            if (NativeLibrary.TryLoad(frameworkPath, out var handle))
            {
                Console.WriteLine($"Resolved {libraryName} -> {frameworkPath}");
                return handle;
            }
        }
        return IntPtr.Zero; // Fall back to default resolution
    }
}
```

---

## Known Runtime Issues

### Async Methods with Non-Frozen Parameters Crash

**Status**: FIXED (January 2026)

**Symptom**: SIGSEGV crash in `swift::metadataimpl::ValueWitnesses<SwiftRetainableBox>::initializeWithCopy` when calling async methods like `ImagePipeline.image(ImageRequest)`.

**Root Cause**: The Swift wrapper's `defer` block deallocated copied non-frozen parameters when the Task block exited, but the callback fired AFTER the defer ran. This caused use-after-free because Swift's internal references (e.g., in the async machinery) were invalidated.

**Solution**: Moved the cleanup code AFTER the callback call instead of using `defer`. The generated Swift wrapper now properly manages the parameter lifetime:

```swift
Task {
    let result = try! await actualMethod(param: paramCopy.pointee)
    callback(result, task)
    // Clean up AFTER callback completes (not in defer)
    paramCopy.deinitialize(count: 1)
    paramCopy.deallocate()
}
```

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Lines 1035-1090

### Swift Wrapper Missing Imports

**Status**: FIXED (January 2026)

**Symptom**: Generated `Swift.Nuke.swift` didn't include `import` statements, causing compilation to fail.

**Solution**: Added `EmitSwiftImports()` method to `ModuleHandler.cs` that emits import statements at the top of Swift wrapper files:
- Always imports the module being bound (e.g., `import Nuke`)
- Always imports `Foundation`
- Imports `UIKit` or `AppKit` if present in module dependencies

**Files modified**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs` - Added `EmitSwiftImports()` method

---

## Rebuilding SwiftBindings.framework

After regenerating bindings, recompile the Swift wrapper:

```bash
cd BindingTesting/Nuke/output-ios

# Compile the Swift wrapper
xcrun --sdk iphonesimulator swiftc -emit-library \
  -target arm64-apple-ios15.0-simulator \
  -module-name SwiftBindings \
  -o SwiftBindings.framework/SwiftBindings \
  Swift.Nuke.swift \
  -F ../Nuke.xcframework/ios-arm64_x86_64-simulator \
  -framework Nuke \
  -sdk $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -Xlinker -install_name -Xlinker @rpath/SwiftBindings.framework/SwiftBindings

# Verify the symbols
nm SwiftBindings.framework/SwiftBindings | grep "_async"
```

---

## Current Investigation: SwiftSelf Retention in Async Methods

**Status**: VERIFIED WORKING (2026-01-30)

### Key Discovery

The crash was NOT in the `ImageRequest` parameter handling - it was in how `self` (the `ImagePipeline`) is handled in async contexts.

**Root Cause**: The `SwiftSelf` parameter passed from C# does NOT participate in Swift's ARC (Automatic Reference Counting). When the Swift Task closure captures `self` implicitly, the reference becomes invalid because:

1. C# passes `self` via the `SwiftSelf` type (which uses register x20 in Swift calling convention)
2. `SwiftSelf` is just a raw pointer wrapper - no retain count management
3. When captured by Swift's `Task { }` closure, Swift doesn't know to retain it
4. The `self` reference becomes invalid when the Task executes on a background thread

### Proof

Testing proved this hypothesis:

1. **Using `self.image()` in Task** → CRASH in `makeStartedImageTask`
2. **Using `ImagePipeline.shared` in Task** → SUCCESS! Image loads correctly

The exact same `ImageRequest` from C# works perfectly when the pipeline is obtained fresh inside the Task.

### The Fix

For async instance methods, the Swift wrapper must explicitly retain and release `self`:

```swift
extension ImagePipeline {
    @_silgen_name("...")
    public func PInvoke_asyncMethod(...) {
        // Retain self for async context (SwiftSelf doesn't participate in ARC)
        _ = Unmanaged.passRetained(self)

        Task {
            let result = try! await actualMethod(...)
            callback(result, task)
            // Release self after async work completes
            Unmanaged.passUnretained(self).release()
        }
    }
}
```

### Implementation Status

**Code changes made to `MethodHandler.cs`** (lines ~1076-1114):
- Added `selfRetainCode` for instance methods: `_ = Unmanaged.passRetained(self)`
- Added `selfReleaseCode` in Task completion: `Unmanaged.passUnretained(self).release()`
- Static methods don't need this (no `self` parameter)

**Testing status**:
- Fix is implemented in code generator
- Generated bindings include retain/release
- ✅ Verified working in runtime testing (2026-01-30)

### Additional Findings from Investigation

1. **ImageRequest validation works**: Accessing `request.description` from C# returns correct data
2. **C#'s `MarshalToSwift` (InitializeWithCopy) works**: Copy operation succeeds when done from C#
3. **Swift's `.pointee` access works**: Reading ImageRequest via `.pointee` in Swift works
4. **ImageRequest size is 8 bytes**: Just a single pointer to an internal `Container` class (copy-on-write)
5. **The problem is timing/threading**: The crash happens when the Task runs on a background thread and tries to use `self`

### Files Modified

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - Lines ~1076-1114 (async wrapper generation with self retain/release)

### Files Involved in Testing

- `BindingTesting/Nuke/output-ios/Swift.Nuke.swift` - Generated Swift wrapper
- `BindingTesting/Nuke/output-ios/Swift.Nuke.cs` - Generated C# bindings
- `BindingTesting/Nuke/NukeTestApp/Program.cs` - Test app with diagnostics

### Test Commands

```bash
# Regenerate bindings
dotnet run --project src/Swift.Bindings/src/Swift.Bindings.csproj -- \
  -a BindingTesting/Nuke/Nuke.xcframework/ios-arm64_x86_64-simulator/Nuke.framework/Modules/Nuke.swiftmodule/arm64-apple-ios-simulator.abi.json \
  -d BindingTesting/Nuke/Nuke.xcframework/ios-arm64_x86_64-simulator/Nuke.framework/Nuke \
  -t BindingTesting/Nuke/output-ios/Nuke.tbd \
  -o BindingTesting/Nuke/output-ios \
  -l Nuke

# Rebuild Swift wrapper with correct install_name
cd BindingTesting/Nuke/output-ios
xcrun swiftc -emit-library \
  -target arm64-apple-ios15.0-simulator \
  -sdk $(xcrun --sdk iphonesimulator --show-sdk-path) \
  -F ../Nuke.xcframework/ios-arm64_x86_64-simulator/ \
  -module-name SwiftBindings \
  -Xlinker -install_name -Xlinker @rpath/SwiftBindings.framework/SwiftBindings \
  -o SwiftBindings-debug.dylib \
  Swift.Nuke.swift
cp SwiftBindings-debug.dylib SwiftBindings.xcframework/ios-arm64-simulator/SwiftBindings.framework/SwiftBindings

# Rebuild and run test
cd /Users/wojo/Dev/swift-bindings
rm -rf BindingTesting/Nuke/NukeTestApp/bin BindingTesting/Nuke/NukeTestApp/obj
dotnet build BindingTesting/Nuke/NukeTestApp -c Debug

# Install and run
xcrun simctl install booted BindingTesting/Nuke/NukeTestApp/bin/Debug/net10.0-ios/iossimulator-arm64/NukeTestApp.app
xcrun simctl launch --console --terminate-running-process booted com.swiftbindings.nuketestapp
```

---

## SOLVED: Async Non-Frozen Parameter Handling (2026-01-29)

### Problem Summary

Calling async Swift methods with non-frozen struct parameters (like `ImageRequest`) caused SIGSEGV/SIGBUS crashes inside Nuke's `makeStartedImageTask` function.

### Root Cause

Two separate issues were identified:

1. **Non-frozen parameter handling**: Using `UnsafeMutablePointer.move()` on memory allocated by C# didn't work correctly. The fix is to use `.pointee` instead (bitwise copy) and let C# manage the copy buffer's lifecycle.

2. **Self handling in async context**: The `self` parameter passed via SwiftSelf doesn't work correctly in async Task closures. Various approaches (Unmanaged.passRetained, etc.) all failed.

### Solution Implemented

**For non-frozen parameters:**
1. C# creates a proper copy using `ValueWitnessTable->InitializeWithCopy`
2. C# passes the copy buffer pointer to Swift
3. Swift reads the value via `.pointee` (bitwise copy that doesn't affect ref count)
4. C# keeps original parameter AND copy buffer alive in GCHandle holder
5. After callback, C# frees the copy buffer memory (no Destroy needed since `.pointee` doesn't take ownership)

**For self (WORKAROUND):**
- For singleton classes like `ImagePipeline`, use the singleton accessor (`.shared`) instead of `self`
- This is a temporary workaround; a proper fix for async instance methods is still needed

### Generated Code Pattern

**C# side:**
```csharp
var _forMetadata = SwiftObjectHelper<ImageRequest>.GetTypeMetadata();
IntPtr _forCopyBuffer = (IntPtr)NativeMemory.Alloc(_forMetadata.Size);
_forMetadata.ValueWitnessTable->InitializeWithCopy(
    (void*)_forCopyBuffer,
    (void*)_for.Payload.DangerousGetHandle(),
    _forMetadata);
object[] holder = new object[] { task, _forCopyBuffer, (object)_for, (object)this };
```

**Swift side:**
```swift
let _forValue = _for.assumingMemoryBound(to: Nuke.ImageRequest.self).pointee
Task {
    // WORKAROUND: Use ImagePipeline.shared for singleton
    let result = try! await ImagePipeline.shared.image(for: _forValue)
    callback(result, task)
}
```

### Remaining Issue: Self in Async Instance Methods

The `self` parameter passed from C# via SwiftSelf doesn't work correctly in async contexts. Attempts that failed:
- `Unmanaged.passRetained(self)` / `release()` - crashes
- `let retainedSelf = Unmanaged.passRetained(self); Task { let pipeline = retainedSelf.takeUnretainedValue() ... }` - crashes
- Keeping `this` in GCHandle holder - still crashes

**Current workaround**: For ImagePipeline (a singleton), use `ImagePipeline.shared` instead of `self`.

**Future work needed**: Investigate why `self` from SwiftSelf isn't properly recognized by Swift's ARC in async contexts.

---

## Related Issues

- [#2875 - Existential Containers](https://github.com/dotnet/runtimelab/issues/2875) - Parameters & properties implemented (2026-01-30)
- [#2996 - Async Properties](https://github.com/dotnet/runtimelab/issues/2996)
- [#2873 - Tuple Support](https://github.com/dotnet/runtimelab/issues/2873) - Implemented
- [#2874 - Closure Support](https://github.com/dotnet/runtimelab/issues/2874) - Implemented; Bound generic params & returns added (2026-01-30)
- [#2890 - Generic Constructors](https://github.com/dotnet/runtimelab/issues/2890) - Implemented (2026-01-30)
