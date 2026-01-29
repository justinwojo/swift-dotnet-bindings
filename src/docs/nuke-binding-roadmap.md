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

**Generated**: ~9,000+ lines of C# code
- 30+ classes implementing `ISwiftObject`
- 8 protocol interfaces
- Property getters and setters
- P/Invoke declarations with Swift calling convention
- **0 compilation errors** (down from 95+)

**Remaining gaps**:
- 86 methods with unsupported signatures (skipped)
- ~10 properties with unsupported types (skipped) - reduced from 24
- 59 enum cases throwing `NotImplementedException`
- 4 remaining AnyType references (existential types and closures - not enums)

**Fixed issues**:
- ~~11 errors related to enum types in Optional<T> parameters~~ FIXED (Phase 2.5)
- ~~20 errors related to `SwiftOptional<T>` missing `PayloadBuffer` property~~ FIXED (Phase 2.3)
- ~~266 hardcoded library paths~~ FIXED with `-l` flag
- ~~95 compilation errors from code generation bugs~~ FIXED (Phase 1.4)
- ~~24 compilation errors from other code generation issues~~ FIXED (Phase 1.5)
- ~~3 naming collision errors~~ FIXED (Phase 2.2.1)

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
**Status**: DONE

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
| `URLRequest` | TODO | Need wrapper class | Foundation |
| `URLResponse` | TODO | Need wrapper class | Foundation |

**Files added**:
- `src/Swift.Runtime/src/Swift/OperationQueue.cs`
- `src/Swift.Runtime/src/Swift/DispatchQueue.cs`
- `src/Swift.Runtime/src/Swift/NSImage.cs`
- `src/Swift.Runtime/src/Swift/NSColor.cs`
- `src/Swift.Runtime/src/Swift/CIContext.cs`
- `src/Swift.Runtime/src/Swift/UIImage.cs`
- `src/Swift.Runtime/src/Swift/DispatchDatabase.xml`
- `src/Swift.Runtime/src/Swift/AppKitDatabase.xml`
- `src/Swift.Runtime/src/Swift/CoreImageDatabase.xml`
- `src/Swift.Runtime/src/Swift/UIKitDatabase.xml`

**Files modified**:
- `src/Swift.Runtime/src/Swift/FoundationDatabase.xml` - Added OperationQueue mapping
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
**Status**: TODO

**Problem**: All enum cases throw `NotImplementedException`:
```csharp
public static CacheType Memory => throw new NotImplementedException("Enum case constructors not yet implemented");
```

Swift enum cases appear as metatype functions:
```
PropertyHandler: Couldn't process property running of type (Nuke.ImageTask.State.Type) -> Nuke.ImageTask.State
```

**Files to modify**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs` - `EnumHandler.EmitEnumCase()`

### 2.7 Cross-Platform Binding Generation (iOS on macOS)
**Status**: DONE

**Problem**: The binding generator couldn't generate iOS bindings when running on macOS because `DynamicLibraryLoader` can't load iOS dylibs.

**Solution**: Made dylib loading optional for structs and classes. When the dylib can't be loaded:
1. The generator logs a warning
2. Continues without metadata (size info looked up at runtime)
3. Generated bindings still work correctly

**Files modified**:
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Added try/catch around `DynamicLibraryLoader.invoke()`

---

## Phase 3: Method Signature Gaps

### 3.1 Existential Types (`any Protocol`)
**Status**: TODO
**Issue**: [#2875](https://github.com/dotnet/runtimelab/issues/2875)

Swift's existential types (`any Protocol`, `some Protocol`) are translated as `Swift.AnyType` and cause methods to be skipped.

**Examples**:
```
Method loadImage has unsupported signature: ( Swift.AnyType with,  Swift.AnyType completion) -> Swift.Nuke.ImageTask
Method process has unsupported signature: ( Swift.AnyType arg0) -> Swift.AnyType<Swift.AnyType>
```

**Affected Nuke APIs**:
- `loadImage(with:completion:)` - The main API for loading images
- `loadData(with:completion:)` - Data loading API
- `imagePublisher(with:)` - Combine publishers

**Potential solutions**:
1. Implement Swift existential container layout in C#
2. Generate appropriate witness table handling
3. Generate wrapper types that can hold existentials

**Files to check**:
- `src/Swift.Bindings/src/Marshaler/ExistentialHandler.cs`
- `src/Swift.Runtime/src/Swift/Runtime/ExistentialContainer.cs`

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
**Status**: Partially Implemented
**Issue**: [#2874](https://github.com/dotnet/runtimelab/issues/2874) - Implemented

Methods with closure parameters may not work correctly in all cases.

**Files to check**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`

### 3.4 Generic Methods
**Status**: Limited

Methods with generic type parameters are often skipped:
```
Constructor init has unsupported generic parameters
```

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
**Status**: BLOCKING for async methods

The .NET runtime does not support passing non-blittable types (like `SafeHandle`) through P/Invoke with Swift calling convention.

**Error**:
```
InvalidProgramException: Passing non-blittable types to a P/Invoke with the Swift calling convention is unsupported.
```

**Fix required**: Emit `IntPtr` instead of `SafeHandle` for Swift calling convention P/Invokes, then manually manage handle lifetime in wrapper methods.

**Files to modify**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

### URL.FromString Non-Blittable Issue
**Status**: Known issue

The `Swift.URL.FromString()` method fails with the same non-blittable type error.

**Workaround**: Use `ImageRequest(SwiftString)` constructor directly.

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
| `pipeline.image(request)` | FAIL | Async P/Invoke limitation |

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
- [ ] URLRequest/URLResponse support
- [ ] Enum case constructors
- [ ] Existential types
- [ ] **Async method support** - BLOCKED by .NET SafeHandle limitation

---

## Related Issues

- [#2875 - Existential Containers](https://github.com/dotnet/runtimelab/issues/2875)
- [#2996 - Async Properties](https://github.com/dotnet/runtimelab/issues/2996)
- [#2873 - Tuple Support](https://github.com/dotnet/runtimelab/issues/2873) - Implemented
- [#2874 - Closure Support](https://github.com/dotnet/runtimelab/issues/2874) - Implemented
