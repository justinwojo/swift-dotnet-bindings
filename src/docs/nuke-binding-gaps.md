# Nuke Library Binding Gaps

This document captures the gaps discovered when attempting to generate C# bindings for the [Nuke](https://github.com/kean/Nuke) image loading library. These findings inform the roadmap for improving Swift Bindings support.

## Summary

| Category | Count | Impact |
|----------|-------|--------|
| Protocol conformance descriptors not found | 200 | Warnings only, types still emit |
| Unsupported accessor kinds (set/_modify) | 128 | Properties are read-only |
| Unsupported method signatures | 67 | Methods skipped |
| Unsupported property types | 70 | Properties skipped |
| Generic protocol types unsupported | 8 | Types skipped entirely |
| Unsupported constructor signatures | 22 | Constructors skipped |

**Result**: Generated 417KB of C# bindings with 52 types, but many methods/properties skipped.

---

## Gap 1: Existential Types (`Swift.AnyType`)

**Priority: High**
**Issue**: [#2875](https://github.com/dotnet/runtimelab/issues/2875)

Swift's existential types (`any Protocol`, `some Protocol`) are translated as `Swift.AnyType` and cause methods to be skipped.

### Examples
```
Method loadImage has unsupported signature: ( Swift.AnyType with,  Swift.AnyType completion) -> Swift.Nuke.ImageTask
Method process has unsupported signature: ( Swift.AnyType arg0) -> Swift.AnyType<Swift.AnyType>
Method hash has unsupported signature: ( Swift.AnyType into) -> void
```

### Affected Nuke APIs
- `loadImage(with:completion:)` - The main API for loading images
- `loadData(with:completion:)` - Data loading API
- `imagePublisher(with:)` - Combine publishers
- All methods accepting `any Error` or protocol existentials

### Why This Matters
Existential types are pervasive in Swift. The `any Protocol` syntax is the modern way to express protocol-typed parameters. Without support, many real-world APIs are unusable.

### Potential Solutions
1. **Existential containers**: Implement the Swift existential container layout in C#
2. **Protocol witness tables**: Generate appropriate witness table handling
3. **Type erasure wrappers**: Generate wrapper types that can hold existentials

---

## Gap 2: Property Setters and Modifiers

**Priority: Medium**

Only property getters are supported. The `set` and `_modify` accessor kinds are unsupported.

### Statistics
- 128 "Unsupported accessor kind" warnings
- All mutable properties become read-only in C#

### Examples
```swift
// Swift
var costLimit: Int { get set }

// Generated C# - only getter works
public Int costLimit { get => costLimit_Get(); }
```

### Potential Solutions
1. Implement `set` accessor emission
2. The `_modify` accessor is for copy-on-write optimization - may not need full support

---

## Gap 3: Generic Protocol Types

**Priority: High**

Protocols with associated types or generic requirements are marked as unsupported and skipped entirely.

### Skipped Types
- `ImageProcessing` - Core image processing protocol
- `ImageDecoding` - Image decoder protocol
- `ImageEncoding` - Image encoder protocol
- `ImageCaching` - Cache protocol
- `DataCaching` - Data cache protocol
- `DataLoading` - Data loader protocol
- `ImagePipelineDelegate` - Pipeline delegate protocol
- `Cancellable` - Cancellation protocol

### Why This Matters
These are fundamental protocols in Nuke's architecture. Without them, users can't:
- Implement custom image processors
- Create custom decoders/encoders
- Implement custom caching strategies

### Potential Solutions
1. Generate C# interfaces for protocols
2. Handle protocol witness tables for conformance
3. Support protocols with associated types (PATs)

---

## Gap 4: Foundation/AppKit/UIKit Types

**Priority: Medium**

Types from Apple frameworks are not in the type database and cause properties to be skipped.

### Affected Types
- `Foundation.URL` / `Foundation.URLRequest` / `Foundation.URLResponse`
- `AppKit.NSImage` / `UIKit.UIImage`
- `CoreGraphics.CGFloat` / `CoreGraphics.CGSize`
- `Foundation.Data` (partially supported)

### Examples
```
PropertyHandler: Couldn't process property urlResponse of type Swift.Optional<Foundation.URLResponse>. Skipping.
PropertyHandler: Couldn't process property image of type AppKit.NSImage. Skipping.
PropertyHandler: Couldn't process property width of type CoreGraphics.CGFloat. Skipping.
```

### Potential Solutions
1. Add Foundation type mappings to the type database
2. Map to existing .NET types where appropriate (URL → Uri, Data → byte[])
3. Generate stubs for Apple framework types

---

## Gap 5: Enum Case Constructors (Static Factory Properties)

**Priority: Low**

Swift enum cases with associated values appear as metatype functions and aren't handled.

### Examples
```
PropertyHandler: Couldn't process property running of type (Nuke.ImageTask.State.Type) -> Nuke.ImageTask.State
PropertyHandler: Couldn't process property statusCodeUnacceptable of type (Nuke.DataLoader.Error.Type) -> (Swift.Int) -> Nuke.DataLoader.Error
PropertyHandler: Couldn't process property storeAll of type (Nuke.ImagePipeline.DataCachePolicy.Type) -> Nuke.ImagePipeline.DataCachePolicy
```

### Pattern
These are Swift enum cases like:
```swift
enum State {
    case running
    case cancelled
    case completed
}
```

The `.running` case appears as a function `(State.Type) -> State`.

### Potential Solutions
1. Detect enum case patterns and emit as static properties
2. Handle associated value cases as static factory methods

---

## Gap 6: Protocol Conformance Descriptors

**Priority: Low** (warnings only)

200 warnings about missing protocol conformance descriptors for built-in protocols.

### Affected Protocols
- `Swift.Copyable`
- `Swift.Escapable`
- `Swift.Sendable`
- `Swift.SendableMetatype`

### Example
```
Error while getting protocol conformance descriptor for 'Nuke.ImageProcessingContext' and protocol 'Swift.Copyable': Protocol conformance descriptor not found
```

### Notes
These are Swift 5.9+ implicit protocols. The bindings still generate, but conformance information is incomplete.

---

## Gap 7: Dictionary and Complex Generic Types

**Priority: Medium**

Dictionaries with custom key types and complex nested generics aren't handled.

### Examples
```
PropertyHandler: Couldn't process property userInfo of type Swift.Dictionary<Nuke.ImageRequest.UserInfoKey, Swift.Any>
Constructor init has unsupported signature: ... Swift.AnyType<Swift.AnyType<Swift.Nuke.ImageRequest.UserInfoKey, Swift.AnyType>>
```

### Potential Solutions
1. Map `Dictionary<K,V>` to `SwiftDictionary<K,V>` or .NET `Dictionary<K,V>`
2. Handle nested generic types recursively

---

## Gap 8: Optional Generic Types with Existentials

**Priority: Medium**

Optionals containing existential or protocol types.

### Examples
```
Constructor init has unsupported signature: ... Swift.AnyType<Swift.Nuke.ImageProcessingOptions.Border> border
PropertyHandler: Couldn't process property type of type Swift.Optional<Nuke.AssetType>
```

### Notes
`Swift.AnyType<T>` appears to be how optionals with certain types are represented.

---

## Gap 9: Async Methods

**Priority: Medium**
**Issue**: [#2996](https://github.com/dotnet/runtimelab/issues/2996)

Swift async methods are partially supported but have limitations.

### Examples from Nuke
```swift
func image(for url: URL) async throws -> PlatformImage
func image(for request: ImageRequest) async throws -> PlatformImage
func data(for request: ImageRequest) async throws -> (Data, URLResponse?)
```

### Notes
The generator attempts async support via Swift wrapper generation, but methods with existential types in async signatures are skipped.

---

## Recommended Priority Order

1. **Existential Types** - Blocks the majority of real-world APIs
2. **Generic Protocol Types** - Required for extensibility
3. **Property Setters** - Common pattern, wide impact
4. **Foundation Type Mappings** - Needed for iOS/macOS interop
5. **Dictionary Support** - Common collection type
6. **Enum Case Constructors** - Nice to have for complete enum support
7. **Async Improvements** - Build on existing foundation

---

## Test Case: Nuke Binding

To reproduce these findings:

```bash
# 1. Build Nuke for macOS
cd BindingTesting/Nuke
git clone https://github.com/kean/Nuke.git NukeSource
cd NukeSource
xcodebuild -scheme Nuke -configuration Release -destination 'platform=macOS' \
    BUILD_LIBRARY_FOR_DISTRIBUTION=YES -derivedDataPath ./DerivedData

# 2. Generate ABI and TBD
xcrun swift-frontend -compile-module-from-interface \
    "./DerivedData/Build/Products/Release/Nuke.framework/Versions/A/Modules/Nuke.swiftmodule/arm64-apple-macos.swiftinterface" \
    -target arm64-apple-macos14.0 -module-name "Nuke" \
    -sdk "$(xcrun --sdk macosx --show-sdk-path)" \
    -emit-abi-descriptor-path "./output/Nuke-macos.abi.json"

xcrun tapi stubify \
    ./DerivedData/Build/Products/Release/Nuke.framework/Nuke \
    --filetype=tbd-v4 -o ./output/Nuke-macos.tbd

# 3. Generate bindings
dotnet Swift.Bindings.dll \
    -a ./output/Nuke-macos.abi.json \
    -d ./DerivedData/Build/Products/Release/Nuke.framework/Nuke \
    -t ./output/Nuke-macos.tbd \
    -o ./output/
```

---

## Related Issues

- [#2875 - Existential Containers](https://github.com/dotnet/runtimelab/issues/2875)
- [#2996 - Async Properties](https://github.com/dotnet/runtimelab/issues/2996)
- [#2873 - Tuple Support](https://github.com/dotnet/runtimelab/issues/2873) ✅ Implemented
- [#2874 - Closure Support](https://github.com/dotnet/runtimelab/issues/2874) ✅ Implemented
