# Phase 9: Binding Gap Reduction (2026-01-30)

**Status**: COMPLETE

This phase focused on reducing the remaining binding warnings from 9 to 6 by fixing tractable issues and documenting the complex ones that require more infrastructure.

---

## 9.1 Add CIFilter and CIImage to CoreImageDatabase.xml
**Status**: DONE

**Problem**: Enum cases `Error.failedToApplyFilter(CIFilter)` and `Error.failedToCreateOutputCGImage(CIImage)` were failing because CIFilter and CIImage weren't in the type database.

**Solution**: Added entries for CIFilter and CIImage to `CoreImageDatabase.xml`:

```xml
<entity managedNameSpace="CoreImage" managedTypeName="CIFilter">
    <typedeclaration kind="class" name="CIFilter" module="CoreImage"
        mangledName="$sSo8CIFilterC" frozen="false"
        requiresMemoryManagement="true" objcBridged="true" />
</entity>
<entity managedNameSpace="CoreImage" managedTypeName="CIImage">
    <typedeclaration kind="class" name="CIImage" module="CoreImage"
        mangledName="$sSo7CIImageC" frozen="false"
        requiresMemoryManagement="true" objcBridged="true" />
</entity>
```

**Files modified**:
- `src/Swift.Runtime/src/Swift/CoreImageDatabase.xml`

---

## 9.2 Async Self Workaround in Build Script
**Status**: DONE

**Problem**: Async methods crash when using `self` in the Swift wrapper because SwiftSelf doesn't work correctly in async Task closures.

**Solution**: Added sed post-processing to `build-swift-wrapper.sh` to replace `self` with `Nuke.ImagePipeline.shared` for the image() async methods:

```bash
# Workaround: Replace self with shared instance for async methods
sed -i '' 's/try! await image(/try! await Nuke.ImagePipeline.shared.image(/g' Swift.Nuke.swift
```

**Limitation**: Only works for singleton classes. Proper fix requires generator changes (see Future Work).

**Files modified**:
- `BindingTesting/Nuke/build-swift-wrapper.sh`

---

## 9.3 Fix Existential Property Lookup in ModuleProcessor
**Status**: DONE

**Problem**: Properties with existential types (like `dataLoader: any DataLoading`) caused warnings "Not found in type declarations" because `ProcessStructProperties` was looking up protocol names in `_typeDecls`, which only contains concrete types.

**Solution**: Added checks in `ProcessStructProperties` to skip existential types:

```csharp
// Skip existential types - they don't have TypeDecl entries
if (propertyDecl.SwiftTypeSpec is ProtocolListTypeSpec)
    continue;

if (propertyDecl.SwiftTypeSpec is NamedTypeSpec namedPropertyType)
{
    if (namedPropertyType.IsAny)
        continue;
    // ... existing cross-module lookup logic
}
```

**Files modified**:
- `src/Swift.Bindings/src/Parser/ModuleProcessor.cs`

---

## 9.4 Fix Closure Return Types in MarshallingHelpers
**Status**: DONE

**Problem**: When PropertyHandler tried to emit closure properties, `MarshallingHelpers.MethodRequiresIndirectResult()` crashed because it called `GetTypeRecordOrThrow()` on the closure return type, which isn't in the type database.

**Solution**: Added check for ClosureTypeSpec at the start of `MethodRequiresIndirectResult()`:

```csharp
// Closure return types don't require indirect result - they are passed as function pointers
if (returnType.SwiftTypeSpec is ClosureTypeSpec)
    return false;
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs`

---

## 9.5 Add Closure Property Support to PropertyHandler
**Status**: DONE

**Problem**: PropertyHandler didn't recognize closure properties (where the property type itself is a closure). These were falling through to the "Couldn't process property" error.

**Solution**:
1. Added `ClosureHandler` to `PropertyEnvironment`
2. Added closure property detection and handling in `PropertyHandler.Emit()`
3. Added `CanInvokeFromCSharp()` method to validate closure parameters

```csharp
// Handle closure properties (property type is a closure/function type)
bool isClosure = propertyEnv.ClosureHandler.IsClosure(propertyDecl);
if (isClosure)
{
    var closureTypeSpec = propertyEnv.ClosureHandler.GetClosureTypeSpec(propertyDecl);
    if (!propertyEnv.ClosureHandler.IsSupportedClosure(closureTypeSpec))
        return; // Skip with warning
    if (!propertyEnv.ClosureHandler.CanInvokeFromCSharp(closureTypeSpec))
        return; // Skip with warning - non-primitive parameters
}
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/IEnvironment.cs` - Added ClosureHandler to PropertyEnvironment
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` - Added closure property handling
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` - Added `CanInvokeFromCSharp()` method

---

## 9.6 Existentials Inside Bound Generics in Closures
**Status**: DONE

**Problem**: Closures with return types like `Optional<any Protocol>` (existentials nested inside bound generics) weren't being recognized as supported.

**Solution**: Added existential handling in `IsSupportedClosureReturnType()` and `IsSupportedGenericType()`:

```csharp
foreach (var genericParam in namedType.GenericParameters)
{
    // Handle existential generic parameters (e.g., Optional<any Protocol>)
    if (_existentialHandler.IsExistential(genericParam))
    {
        var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
        if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
            return false;
        continue;
    }
    // ... existing checks
}
```

**Files modified**:
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`

---

## Warning Summary

| Warning Type | Count Before | Count After | Status |
|-------------|--------------|-------------|--------|
| CIFilter/CIImage enum cases | 2 | 0 | FIXED (9.1) |
| dataLoader existential property | 1 | 0 | FIXED (9.3) |
| makeImageDecoder closure | 1 | 1 | Deferred - non-primitive params |
| makeImageEncoder closure | 1 | 1 | Deferred - non-primitive params |
| AsyncStream properties | 3 | 3 | Deferred - async iteration |
| didComplete async closure | 1 | 1 | Deferred - async callbacks |
| **Total** | **9** | **6** | **33% reduction** |

---

## Deferred Items Explanation

### Closure Properties: Why makeImageDecoder/makeImageEncoder Are Deferred

These closure properties have non-primitive parameters (e.g., `ImageDecodingContext`):

```swift
var makeImageDecoder: (ImageDecodingContext) -> ImageDecoding?
var makeImageEncoder: (ImageEncodingContext) -> ImageEncoding
```

When Swift returns these closures, we wrap them in C# delegates. To invoke the delegate, we must:
1. Allocate native memory for the parameter
2. Marshal the C# struct to Swift format
3. Call the Swift function pointer
4. Free the native memory

This is complex to do in a lambda expression. A proper implementation would require:
- Generated helper methods for each closure signature
- Memory management with `try/finally`
- Or switching to a different invocation pattern

**Recommendation**: Add to Phase 10 roadmap for comprehensive closure property support.

### AsyncStream Properties: Why progress/previews/events Are Deferred

AsyncStream is a Swift async iteration type:

```swift
var progress: AsyncStream<ImageTask.Progress>
var previews: AsyncStream<ImageResponse>
var events: AsyncStream<ImageTask.Event>
```

Supporting these requires:
- New Swift wrapper functions to iterate the stream
- Callback mechanism for each element
- Proper async/await integration on C# side

**Recommendation**: Add to Phase 10 roadmap for async iteration support.

### didComplete: Why It's Deferred

```swift
var didComplete: (@MainActor @Sendable () async -> Void)?
```

This is an async closure with MainActor constraint. Supporting it requires:
- Async closure callback infrastructure
- MainActor dispatch handling

**Recommendation**: Add to Phase 10 roadmap for async closure callbacks.

---

## Files Modified

| File | Changes |
|------|---------|
| `CoreImageDatabase.xml` | Added CIFilter and CIImage entities |
| `build-swift-wrapper.sh` | Added sed workaround for async self |
| `ModuleProcessor.cs` | Skip existential types in property processing |
| `MarshallingHelpers.cs` | Added closure return type check |
| `IEnvironment.cs` | Added ClosureHandler to PropertyEnvironment |
| `PropertyHandler.cs` | Added closure property handling |
| `ClosureHandler.cs` | Added existential support, CanInvokeFromCSharp |
| `ClosureEmitter.cs` | Minor cleanup (reverted complex changes) |

---

## Test Results

```
Unit tests:        539 passed
Integration tests: 691 passed
Runtime tests:      72 passed (1 skipped)
Simulator:         All 3 tests pass ✅
```

---

## Validated on iOS Simulator

```
PROTOCOL TEST SUCCESS: Full proxy pattern works!
IMAGE PROCESSING TEST SUCCESS: Full proxy pattern works!
=== TEST SUCCESS: Image loaded, size: 400x300 ===
=== VALIDATION PASSED ===
```

---

## Summary

Phase 9 reduced binding gaps:
- CIFilter/CIImage added to type database
- Async self workaround automated in build script
- Existential property lookup fixed
- Closure return type handling improved
- Closure property detection added
- Existentials in bound generics supported

Reduced warnings from 9 to 6 (33% reduction). Remaining items require new infrastructure.
