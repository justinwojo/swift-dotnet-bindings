# Phase 5: Testing & Validation

**Status**: COMPLETE

This phase established comprehensive testing and validation infrastructure.

---

## Test Results (January 2026)

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
| Closure bound generic params | PASS | `loadImage(completion:)` generates with `Action<SwiftResult<...>>` |
| Closure bound generic returns | PASS | Closures returning `SwiftOptional<T>` use indirect return marshalling |
| Existential properties | PASS | Properties with `any Protocol` generate `ExistentialContainer{N}` |
| Generic constructors | PASS | Constructors with generic params use TypeMetadata/witness tables |
| Closures in constructors | PASS | Constructors with closure params emit callbacks and marshalling |
| Hasher type mapping | PASS | 21 new `hash(into:)` methods generated |
| UIColor type mapping | PASS | ObjC-bridged to UIKit.UIColor, 7 usages |
| URLSession type mappings | PASS | URLSession, URLSessionConfiguration, URLCache ObjC-bridged, 8 usages |
| Unbound generic method parsing | PASS | GenericTypeParam nodes parsed, where clauses emitted |
| Dictionary with existential values | PASS | `Dictionary<K, Any>` now generates `SwiftDictionary<K, ExistentialContainer0>` |
| Closure callback signature consistency | PASS | Function pointer and callback signatures now match |
| Memory management tests | PASS | 7 new tests: double-dispose, handle validity, unowned refs, stress |
| Nuke memory stress test | PASS | 50 ImageRequest create/dispose cycles, no retain count drift |
| ExistentialContainerFactory | PASS | 8 new tests: Create methods, witness table handling, payload marshalling |
| Unbound generic types | PASS | 17 new tests: GenericTypeEmitter, parser, where clauses |
| AssociatedTypeReferenceSpec | PASS | 9 new tests: parsing, ToString, HasDynamicSelf, equality |
| Protocol subscript parsing | PASS | Subscripts parsed from ABI JSON, emitted as C# indexers |
| Protocol closure tuple params | PASS | `(Data, URLResponse) -> Void` → `Action<(Data, URLResponse)>` |
| Existential handling consistency | PASS | `NamedTypeSpec.IsAny` and `ProtocolListTypeSpec` both handled |
| Protocol Proxy Emitter | PASS | C# proxy classes generate with vtable callbacks |
| EveryProtocol Swift generation | PASS | Swift conformances emit with vtable function pointers |
| SwiftObjectRegistry | PASS | Container-to-proxy mapping for Swift callbacks |
| Generic type translation in proxies | PASS | `SwiftOptional<T>` with full generic arguments |
| Closure type translation in proxies | PASS | `ClosureTypeSpec` → `Action<...>`/`Func<...>` |
| CIFilter/CIImage enum cases | PASS | CoreImageDatabase.xml entries added |
| Existential property skip | PASS | ModuleProcessor skips `any Protocol` types without warning |
| Closure property detection | PASS | PropertyHandler recognizes closure-typed properties |
| Existentials in bound generics | PASS | `Optional<any Protocol>` supported in closures |
| Async self workaround | PASS | build-swift-wrapper.sh sed replacement works |

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

# Run all tests (unit, integration, runtime)
./run-tests.sh
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

## Summary

Phase 5 established comprehensive testing:
- 50+ individual test cases
- iOS Simulator testing workflow
- Framework resolution patterns
- Swift wrapper rebuilding
- Test environment setup guide

This phase ensures confidence in the binding quality through extensive testing.
