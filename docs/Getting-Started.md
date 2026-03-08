# Getting Started

## Prerequisites

- **macOS** with **[Xcode 26](https://developer.apple.com/xcode/)** or later installed (required for Apple platform tooling)
- **.NET 10 SDK** with the iOS workload:
  ```bash
  dotnet workload install ios
  ```
- A compiled framework (`.xcframework`) you want to bind — either Swift or Objective-C:
  - Must be a **dynamic** framework (not a static `.a` archive)
  - **Swift frameworks** must be built with **`BUILD_LIBRARY_FOR_DISTRIBUTION=YES`** (library evolution enabled). This flag tells the Swift compiler to emit stable ABI metadata (`.swiftinterface` files) that the generator needs. Without it, the generator will produce empty output or crash. Most well-maintained open-source libraries and vendor SDKs already build with this flag. See [Troubleshooting](Troubleshooting#generator-crash-or-emptyincomplete-output) if your xcframework wasn't built this way.
  - **ObjC frameworks** have no additional requirements — the generator uses `clang -ast-dump=json` to parse public headers directly.

  The framework type is auto-detected. No flags needed — drop any xcframework and the correct pipeline runs.

## Install the tooling

Install the project template (which includes the MSBuild SDK reference):

```bash
dotnet new install Swift.Bindings.Templates
```

---

## Create a Binding (MSBuild SDK)

The recommended workflow uses the Swift Bindings MSBuild SDK. Your `.xcframework` goes in, a NuGet package comes out.

### 1. Create a binding project

```bash
dotnet new swift-binding -n MyLibrary.Swift.iOS
```

This creates a project file that looks like:

```xml
<Project Sdk="Swift.Bindings.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
  </PropertyGroup>
</Project>
```

### 2. Add your xcframework

Copy the `.xcframework` into the project directory:

```bash
cp -r ~/Downloads/MyLibrary.xcframework ./MyLibrary.Swift.iOS/
```

The SDK auto-discovers `*.xcframework` files in the project directory. If you have multiple frameworks or a non-standard path, declare them explicitly:

```xml
<ItemGroup>
  <SwiftFramework Include="MyLibrary.xcframework" />
</ItemGroup>
```

### 3. Build

```bash
cd MyLibrary.Swift.iOS
dotnet build
```

This automatically:
- Extracts ABI metadata from the xcframework
- Runs the binding generator
- Compiles the Swift wrapper library
- Builds the C# bindings into a DLL

### 4. Package for distribution

```bash
dotnet pack
```

Produces a NuGet package (e.g., `MyLibrary.Swift.iOS.1.0.0.nupkg`) that any .NET iOS app can consume.

### 5. Consume in your app

```xml
<PackageReference Include="MyLibrary.Swift.iOS" Version="1.0.0" />
```

```csharp
using MyLibrary;

// Use your Swift library from C#
var result = MyClass.DoSomething();
```

The consumer doesn't need the Swift Bindings SDK, the generator, or any Swift knowledge. They just reference the NuGet package. It includes MSBuild targets that automatically bundle the native frameworks into the app and configure diagnostic suppression — no manual `NativeReference` items needed.

---

## Framework Dependencies

If your Swift library imports another Swift framework, you need to tell the SDK about it so the Swift wrapper can compile and the NuGet package declares the dependency:

```xml
<ItemGroup>
  <SwiftFrameworkDependency Include="../SmartCardIO.xcframework"
                            PackageId="SmartCardIO.Swift.iOS"
                            PackageVersion="1.0.0" />
</ItemGroup>
```

Each `<SwiftFrameworkDependency>` item:
- Adds a `-F` search path for Swift wrapper compilation
- Adds a `<PackageReference>` in the NuGet package for consumers

Both `PackageId` and `PackageVersion` are required for NuGet packaging (the build will warn if missing).

---

## Create a Binding (CLI)

If you prefer direct control or want to integrate into a custom build pipeline, you can run the generator as a CLI tool. This requires cloning the repository.

### From an xcframework (recommended)

```bash
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework MyLibrary.xcframework \
  -o output/
```

This resolves all inputs automatically (ABI JSON, dylib, TBD, swiftinterface) and generates all output files.

### From individual files

```bash
dotnet run --project src/Swift.Bindings/src -- \
  -a MyLibrary.abi.json \
  -d MyLibrary.dylib \
  -t MyLibrary.tbd \
  -o output/
```

### What gets generated

**Swift frameworks:**

| File | Purpose |
|------|---------|
| `{Module}.cs` | C# bindings (P/Invoke declarations, type wrappers) |
| `{Module}.swift` | Swift wrapper functions (async, protocol dispatch, etc.) |
| `{Module}SwiftBindings.xcframework/` | Compiled Swift wrapper (xcframework mode) |
| `{Module}.SwiftUIBridge.cs` + `.swift` | SwiftUI bridge (when views are detected) |
| `binding-report.json` | Coverage report — what was bound and what was skipped |
| `{Module}.Swift.iOS.csproj` + `.targets` | Ready-to-build project and NuGet consumer targets (xcframework mode) |
| `binding-metadata.json` + `.props` | Extracted framework metadata |

**ObjC frameworks** (auto-detected — no flags needed):

| File | Purpose |
|------|---------|
| `ApiDefinition.cs` | Binding interface definitions (`[BaseType]`, `[Export]`, `[Protocol]`) |
| `StructsAndEnums.cs` | Enums, structs, constants, C functions |
| `BgenDelegates.cs` | Block-based callback delegate definitions |
| `{Module}.ObjC.iOS.csproj` | Ready-to-build binding project (`<IsBindingProject>true`) |
| `binding-metadata.props` | Extracted framework metadata |

See [Customization](Customization.md) for the full set of CLI options.

---

## Building an xcframework from SPM

Many Swift libraries are distributed as Swift Package Manager packages (source code + `Package.swift`) rather than prebuilt xcframeworks. To bind one of these, you first need to build it into an xcframework.

### Using Xcode

1. Open or create an Xcode project/workspace that depends on the SPM package
2. Build for both device and simulator:
   ```bash
   xcodebuild archive -scheme MyLibrary -destination "generic/platform=iOS" \
     -archivePath ./build/ios SKIP_INSTALL=NO BUILD_LIBRARY_FOR_DISTRIBUTION=YES

   xcodebuild archive -scheme MyLibrary -destination "generic/platform=iOS Simulator" \
     -archivePath ./build/sim SKIP_INSTALL=NO BUILD_LIBRARY_FOR_DISTRIBUTION=YES
   ```
3. Create the xcframework:
   ```bash
   xcodebuild -create-xcframework \
     -framework ./build/ios.xcarchive/Products/Library/Frameworks/MyLibrary.framework \
     -framework ./build/sim.xcarchive/Products/Library/Frameworks/MyLibrary.framework \
     -output MyLibrary.xcframework
   ```

> **Important:** `BUILD_LIBRARY_FOR_DISTRIBUTION=YES` is required — it enables library evolution mode, which produces the stable ABI metadata the generator needs. Without it, the generator will fail or produce incomplete bindings.

> **Note:** The framework must be built as a **dynamic** library. Static libraries (`.a` archives) are not supported.

Direct SPM integration (`<SwiftPackage>` items) is planned for a future release.

---

## Understanding the Binding Report

Every generator run produces a `binding-report.json` that tells you exactly what was bound and what was skipped:

```json
{
  "ModuleName": "MyLibrary",
  "TotalTypes": 60,
  "EmittedTypes": 60,
  "SkippedTypes": 0,
  "TotalMembers": 352,
  "EmittedMembers": 295,
  "SkippedMembers": 57
}
```

Skipped members include a reason and a recommended workaround:

| Skip Reason | Meaning |
|-------------|---------|
| `UnsupportedSignature` | Parameter or return type the generator can't handle yet |
| `UnsupportedType` | Type uses an unsupported Swift pattern |
| `AnyTypeFallback` | Type couldn't be resolved (falls back to `object`) |
| `UnsupportedClosure` | Closure with unsupported argument types |
| `UnsupportedExistential` | Existential type the generator can't project |
| `UnsatisfiedGenericConstraint` | Generic type argument can't satisfy C# constraints |
| `AsyncProperty` | Async computed property (not yet supported) |
| `StaticProtocolMember` | Static protocol members can't be dispatched through witness tables |
| `DuplicateSignature` | Another member already emitted with the same C# signature |
| `SwiftUIView` | SwiftUI View (handled by the bridge, not normal binding) |
| `SwiftUIConstraint` | Generic View type parameter (can't be bound) |
| `SynthesizedCodable` | Codable protocol members (`encode`/`init(from:)`) pruned for cleaner API |

The report helps you understand coverage gaps and decide if manual Swift wrappers are needed for any skipped APIs.

---

## Next Steps

- **[Supported Features](Supported-Features.md)** — Full list of what Swift features are covered
- **[Customization](Customization.md)** — CLI options, MSBuild properties, namespace control
- **[SwiftUI Interop](SwiftUI-Interop.md)** — SwiftUI bridge usage, bridge hints, async views
- **[Troubleshooting](Troubleshooting.md)** — Common errors and how to fix them
- **[Known Limitations](Known-Limitations.md)** — Platform constraints and workarounds
