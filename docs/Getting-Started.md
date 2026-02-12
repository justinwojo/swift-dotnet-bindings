# Getting Started

## Prerequisites

- **macOS** with **Xcode** installed (required for Apple platform tooling)
- **.NET 10 SDK** with the iOS workload:
  ```bash
  dotnet workload install ios
  ```
- A compiled Swift framework (`.xcframework`) you want to bind

## Create a Binding (MSBuild SDK)

The recommended workflow uses the Swift Bindings MSBuild SDK. Your `.xcframework` goes in, a NuGet package comes out.

### 1. Create a binding project

```bash
dotnet new swift-binding -n MyLibrary.Swift.iOS
```

This creates a project file that looks like:

```xml
<Project Sdk="Swift.Bindings.Sdk/0.1.0-preview.1">
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
using Swift.MyLibrary;

// Use your Swift library from C#
var result = MyClass.DoSomething();
```

The consumer doesn't need the Swift Bindings SDK, the generator, or any Swift knowledge. They just reference the NuGet package.

---

## Create a Binding (CLI)

If you prefer direct control, you can run the generator as a CLI tool:

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

| File | Purpose |
|------|---------|
| `Swift.{Module}.cs` | Main C# bindings |
| `Swift.{Module}.swift` | Swift wrapper functions (async, protocol dispatch, etc.) |
| `Swift.{Module}.Wrappers.cs` | Additional C# wrapper code (when needed) |
| `Swift.{Module}.SwiftUIBridge.cs` + `.swift` | SwiftUI bridge (when views are detected) |
| `binding-report.json` | Coverage metrics — what was bound and what was skipped |
| `{Module}.Swift.iOS.csproj` | Ready-to-build project (xcframework mode only) |

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

> **Note:** The framework must be built as a **dynamic** library. Static libraries (`.a` archives) are not supported.

Direct SPM integration (`<SwiftPackage>` items) is planned for a future release.

---

## Understanding the Binding Report

Every generator run produces a `binding-report.json` that tells you exactly what was bound and what was skipped:

```json
{
  "types": {
    "bound": 60,
    "skipped": 8,
    "total": 68
  },
  "members": {
    "bound": 323,
    "skipped": 19,
    "total": 342
  }
}
```

Skipped members include a reason:

| Skip Reason | Meaning |
|-------------|---------|
| `UnsupportedSignature` | Parameter or return type the generator can't handle yet |
| `AnyTypeFallback` | Type couldn't be resolved (falls back to `object`) |
| `SwiftUIView` | SwiftUI View (handled by bridge, not normal binding) |
| `SwiftUIConstraint` | Generic View type parameter (can't be bound) |
| `AsyncProperty` | Async computed property (not yet supported) |

The report helps you understand coverage gaps and decide if manual wrappers are needed for any skipped APIs.

---

## Let AI Create Your Binding

The repository includes structured scripts and diagnostic reports designed to work with AI coding assistants (Claude Code, Codex, etc.). The vision: point an AI agent at your Swift framework, and get back a working, tested NuGet package.

The binding report, validation tooling, and helper scripts provide the feedback signals an AI agent needs to iterate toward a working binding without human intervention.

---

## Next Steps

- **[Supported Features](Supported-Features)** — Full list of what Swift features are covered
- **[Customization](Customization)** — How to control the generator's output
- **[Troubleshooting](Troubleshooting)** — Common errors and how to fix them
