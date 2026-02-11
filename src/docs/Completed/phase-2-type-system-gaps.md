# Phase 2: Type System Gaps

**Status**: COMPLETE

This phase addressed gaps in the type system that prevented binding of common Swift types.

---

## 2.1 Optional<T> Support
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

---

## 2.2 Foundation/Platform Types
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

---

## 2.2.1 Naming Collision Bug Fixes
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

---

## 2.3 SwiftOptional PayloadBuffer Support
**Status**: DONE

**Problem**: Generated code used `SwiftOptional<T>.PayloadBuffer` but the type didn't have this property.

**Solution**: Refactored `SwiftOptional<T>` to use `SwiftSafeHandle<SwiftOptional<T>>` storage (matching `SwiftArray<T>`, `SwiftSet<T>`, `SwiftDictionary<K,V>` pattern).

**Files modified**:
- `src/Swift.Runtime/src/Swift/SwiftOptional.cs` - Complete refactoring to SafeHandle pattern

---

## 2.4 Property Setters
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

---

## 2.5 Enum Type Registration
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

---

## 2.6 Enum Case Constructors
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

---

## 2.7 Cross-Platform Binding Generation (iOS on macOS)
**Status**: DONE

**Problem**: The binding generator couldn't generate iOS bindings when running on macOS because `DynamicLibraryLoader` can't load iOS dylibs.

**Solution**: Made dylib loading optional for structs and classes. When the dylib can't be loaded:
1. The generator logs a warning
2. Continues without metadata (size info looked up at runtime)
3. Generated bindings still work correctly

**Files modified**:
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs` - Added try/catch around `DynamicLibraryLoader.invoke()`

---

## 2.8 Reuse Existing .NET iOS Bindings for Objective-C Types
**Status**: COMPLETE (January 2026)
**Impact**: Major UX improvement for ObjC-imported types

**Key Discovery**: There are two categories of types here with very different characteristics:

1. **ObjC classes imported into Swift** (mangled with `So` prefix, `C` suffix) - These ARE the same ObjC objects and can be directly mapped to .NET iOS bindings. ✅ **Done**

2. **Swift native structs** (mangled with `s10Foundation` prefix, `V` suffix) - These are VALUE types with different ABI. Remapping is not practical. ✅ **Conscious decision to keep as Swift.* wrappers**

### ObjC Classes - DONE ✅

| Swift Type | Mangled Name | .NET iOS Type | Status |
|------------|--------------|---------------|--------|
| `UIImage` | `$sSo7UIImageC` | `UIKit.UIImage` | ✅ Working |
| `URLResponse` | `$sSo15NSURLResponseC` | `Foundation.NSUrlResponse` | ✅ Working |
| `OperationQueue` | `$sSo16NSOperationQueueC` | `Foundation.NSOperationQueue` | ✅ Working |
| `NSImage` | `$sSo7NSImageC` | `AppKit.NSImage` | ✅ Working |

These use `objcBridged="true"` in the type database and work because Swift literally imports the ObjC class - the pointer is the same `objc_object*`.

### Swift Structs - Intentionally Kept as Swift.* Wrappers ✅

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

## Summary

Phase 2 addressed critical type system gaps:
- Optional<T> support
- 14+ Foundation/Platform types
- Naming collision fixes
- SwiftOptional PayloadBuffer
- Property setters for frozen structs
- Enum type registration
- Enum case constructors
- Cross-platform binding generation
- ObjC type remapping strategy

This phase enabled binding of real-world Swift libraries with common type patterns.
