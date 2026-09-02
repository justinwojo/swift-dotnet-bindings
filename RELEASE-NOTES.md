# SwiftBindings 0.19.3

This release fixes two defects that prevented binding VisionKit, one of which affected any framework whose `.tbd` stub spans multiple documents such as PassKit and WebKit, plus memory corruption when inserting struct values into a Swift `Set`.

## Highlights

- **Frameworks with multi-document `.tbd` stubs now bind**: nine of the 252 frameworks in the iOS SDK ship a `.tbd` split across several documents, and the parser read only the last one. Every `get async` accessor came out synchronous, which failed the Swift wrapper compile, and every non-`@objc` protocol silently lost its delegate proxy. No package published under `SwiftBindings.Apple.*` is among the nine, so this unblocks frameworks you bind yourself rather than changing ones you already consume.
- **`NSString`-backed enum types no longer break the C# compile**: a type declared `NS_STRING_ENUM`, such as Vision's `VNBarcodeSymbology`, is a value type that Swift imports as a `RawRepresentable` struct, but an Objective-C prefix alone was being taken as proof of a class. Members accepting one emitted code referencing a `Handle` the enum does not have.
- **Inserting struct values into a Swift `Set` no longer corrupts memory**: the JIT mishandles the standard library insert's `(Bool, @out)` tuple return, so element types without a typed fast path now go through a C shim that lets the native toolchain lower the call.
- **Binding an Apple framework directly recovers from members that cannot compile**: the verify-and-recover loop already used for generated bindings now also runs for Apple system frameworks bound in place, so one unbindable member is declined and reported instead of taking the build down.

## Reported issues fixed

- **[#46](https://github.com/justinwojo/swift-dotnet-bindings/issues/46): VisionKit could not be bound.** Declaring `<SwiftAppleFrameworkTarget Include="VisionKit" />` failed in the Swift wrapper compile with `'async' property access in a function that does not support concurrency`, and the generated C# did not compile behind it. Two independent defects were involved, both fixed above; VisionKit now binds end to end, `DataScannerViewController` and its barcode symbology filter included.

## Packages

| Package                  | Version |
|--------------------------|---------|
| SwiftBindings.Runtime    | 0.19.3  |
| SwiftBindings.Sdk        | 0.19.3  |
| SwiftBindings.Templates  | 0.19.3  |

`SwiftBindings.Apple` is unchanged at `26.2.8`. It declares a floor-only Runtime range, so the published supplement rides forward without a republish.

See the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) for installation and usage.
