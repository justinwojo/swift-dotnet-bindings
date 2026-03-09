# Troubleshooting

## Setup Issues

### Xcode not found / wrong Xcode selected

The generator requires a full Xcode installation (not just Command Line Tools). If you see errors about `swift-frontend` or `xcodebuild` not being found:

```bash
# Check what Xcode is selected
xcode-select -p

# Should show something like /Applications/Xcode.app/Contents/Developer
# If it shows /Library/Developer/CommandLineTools, fix it:
sudo xcode-select -s /Applications/Xcode.app
```

### Missing platform workload

If `dotnet build` fails with errors about `net10.0-ios` (or `net10.0-macos`, `net10.0-maccatalyst`, `net10.0-tvos`) being an unknown target framework, install the appropriate workload:

```bash
dotnet workload install ios           # for net10.0-ios
dotnet workload install macos         # for net10.0-macos
dotnet workload install maccatalyst   # for net10.0-maccatalyst
dotnet workload install tvos          # for net10.0-tvos
```

---

## MSBuild SDK Errors

These errors come from the SDK's build targets and have clear remediation steps.

| Code | Message | Fix |
|------|---------|-----|
| `SWIFTBIND001` | No xcframework found | Copy a `.xcframework` into the project directory, or add `<SwiftFramework Include="path/to/Library.xcframework" />` |
| `SWIFTBIND002` | Multiple xcframeworks found (ambiguous) | Declare explicit `<SwiftFramework>` items in your `.csproj` |
| `SWIFTBIND003` | xcframework path doesn't exist | Check the path in your `<SwiftFramework>` item |
| `SWIFTBIND010` | Unsupported target framework | Use a supported Apple platform TFM (`net10.0-ios`, `net10.0-macos`, `net10.0-maccatalyst`, `net10.0-tvos`) |
| `SWIFTBIND020` | Version placeholder detected | The framework uses Xcode's default "1.0" version. Set `<PackageVersion>` manually in your `.csproj` |
| `SWIFTBIND021` | Dependency version placeholder | A framework dependency has a placeholder version. Set `<PackageVersion>` manually or verify the dependency xcframework's Info.plist |
| `SWIFTBIND030` | Packing without `SwiftWrapperArchitectures=all` | Set `<SwiftWrapperArchitectures>all</SwiftWrapperArchitectures>` before running `dotnet pack` |
| `SWIFTBIND031` | Wrapper xcframework missing device or simulator slice | Rebuild with `SwiftWrapperArchitectures=all` to compile both slices |
| `SWIFTBIND040` | `SwiftFrameworkDependency` missing metadata | Add `PackageId` and `PackageVersion` metadata to each `<SwiftFrameworkDependency>` item for correct NuGet dependency propagation |
| `SWIFTBIND050` | Swift wrapper compilation failed | Check for missing dependency frameworks (use `--framework-dependency` or `<SwiftFrameworkDependency>`). C# bindings remain valid — wrapper-dependent methods will throw `DllNotFoundException` at runtime. |
| `SWIFTBIND060` | Dependency detected but xcframework not found | Use `--framework-dependency` (CLI) or `<SwiftFrameworkDependency>` (SDK) to provide the dependency xcframework. |
| `SWIFTBIND070` | Module database not found | Check path in `--module-database` or `ModuleDatabasePath` metadata. |
| `SWIFTBIND071` | Module database targets current module | Don't pass the current module's own database as a dependency. |
| `SWIFTBIND072` | Invalid module database XML | Verify XML validity; regenerate by building the dependency project. |
| `SWIFTBIND073` | Module database path doesn't exist (SDK) | Build dependency project first, or remove `ModuleDatabasePath` metadata. |
| `SWIFTBIND080` | Cross-module dependency detected, no sibling project found | Add a `<ProjectReference>` to the dependency binding project, or use `<SwiftFrameworkDependency>` with `PackageId`/`PackageVersion` for NuGet. The warning message includes both options with concrete paths. |
| `SWIFTBIND100` | `<SwiftPackage>` used (not yet available) | SPM support is planned. Build your SPM package into an xcframework first, then use `<SwiftFramework>`. |

---

## Generator Errors

### "Static xcframework detected"

The generator only supports **dynamic** xcframeworks (containing `.dylib` or `.framework` bundles). Static xcframeworks (`.a` archives) are not supported.

**If you control the build:** Rebuild the framework as a dynamic library. In Xcode, set `MACH_O_TYPE` to `mh_dylib`. For SPM packages, use [spm-to-xcframework](https://github.com/justinwojo/spm-to-xcframework) which handles this automatically.

**If this is a vendor xcframework:** Contact the library vendor and request a dynamic framework build. If a dynamic build isn't available, see [Alternative approaches for incompatible xcframeworks](#alternative-approaches-for-incompatible-xcframeworks) below.

### "No Swift module found"

The xcframework doesn't contain a `.swiftmodule` directory. This typically means it's a pure Objective-C framework.

**What happens:** The generator auto-detects ObjC frameworks and runs the ObjC pipeline instead, producing standard `ApiDefinition.cs` + `StructsAndEnums.cs` binding definitions with a ready-to-build `.csproj`. No flags needed — just point the generator at the xcframework and it picks the right pipeline.

If you're seeing this as an error rather than an informational message, it may indicate the xcframework is malformed or uses an unsupported structure. File an issue with the xcframework layout.

### Generator crash or empty/incomplete output

If the generator crashes with an empty module name or produces very few bindings, the framework was likely not built with library evolution enabled.

**Fix:** Rebuild the xcframework with `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`:

```bash
xcodebuild archive -scheme MyLibrary \
  -destination "generic/platform=iOS Simulator" \
  SKIP_INSTALL=NO BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
  -archivePath ./build/sim
```

This flag enables the stable ABI metadata (`.swiftinterface` files) that the generator relies on. Without it, the ABI JSON may be missing or malformed. This is a hard requirement — the generator cannot extract type information without `.swiftinterface` files.

**If you control the build:** Add `BUILD_LIBRARY_FOR_DISTRIBUTION=YES` to your `xcodebuild` invocation. For SPM packages, use [spm-to-xcframework](https://github.com/justinwojo/spm-to-xcframework) which sets this flag automatically.

**If this is a vendor xcframework:** Contact the library vendor and request a build with library evolution enabled. If that isn't possible, see [Alternative approaches for incompatible xcframeworks](#alternative-approaches-for-incompatible-xcframeworks) below.

### "swift-frontend failed"

ABI JSON extraction failed. This usually means the `.swiftinterface` file is malformed or the Xcode toolchain version is incompatible.

**Fix:** Ensure Xcode is up to date. Check the error output for specific compiler messages. Also verify `xcode-select -p` points to the correct Xcode installation.

---

## Swift Wrapper Compilation Failures

The generator produces a Swift wrapper library (compiled automatically in `--xcframework` mode). Wrapper compilation can fail for two reasons that are **not** generator bugs.

### "No such module" in Swift wrapper

The library imports another Swift framework that isn't available during wrapper compilation.

```
error: no such module 'DependencyFramework'
```

**Fix (CLI mode):** Add `--framework-dependency` for each dependency:

```bash
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework MyLibrary.xcframework \
  --framework-dependency ../DependencyA.xcframework \
  --framework-dependency ../DependencyB.xcframework \
  -o output/
```

**Fix (MSBuild SDK):** Add `<SwiftFrameworkDependency>` items:

```xml
<ItemGroup>
  <SwiftFrameworkDependency Include="../DependencyA.xcframework"
                            PackageId="DependencyA.Swift.iOS"
                            PackageVersion="1.0.0" />
</ItemGroup>
```

### Internal types referenced in Swift wrapper

Some libraries have public APIs that use `@usableFromInline internal` types. The generator correctly binds the public API, but the Swift wrapper can't compile because it can't access the internal types.

**What you'll see:** The C# bindings (`.cs` files) are correct and complete. The Swift wrapper compilation fails with errors about unknown types.

**Fix:** The affected members will still appear in the C# bindings but won't have wrapper-based features (async, ArraySlice normalization). The core bindings remain usable. File an issue if this blocks a critical API.

---

## Build Errors in Generated Code

### Missing type references (dependency frameworks)

```
error CS0246: The type or namespace name 'SomeType' could not be found
```

A type from a dependent Swift framework wasn't resolved. The binding report (`binding-report.json`) will show these as `AnyTypeFallback` skip reasons.

**Fix:** Ensure all dependent frameworks are provided via `--framework-dependency` (CLI) or `<SwiftFrameworkDependency>` (SDK). See [Framework Dependencies](Getting-Started#framework-dependencies) in Getting Started.

### Missing type references (Apple framework types)

```
error CS0246: The type or namespace name 'UIViewController' could not be found
```

Some Apple framework types (UIKit, AVFoundation, AppKit) don't have .NET iOS SDK equivalents. These errors come from the .NET SDK, not the generator.

**What this means:** The generated binding is correct — it accurately reflects the Swift API. But .NET iOS doesn't expose all Apple types yet, so some members that reference those types won't compile.

**Fix:** These members can be safely commented out or removed. They'll work once the .NET iOS SDK adds the missing types. The binding report flags these members so you can identify them.

### Duplicate compile items (CLI mode)

```
error CS2002: Source file 'MyLibrary.cs' specified multiple times
```

When building a `.csproj` generated by CLI mode (`--xcframework` without `--sdk-mode`), the .NET SDK auto-includes `*.cs` files while the generated project also lists them explicitly.

**Fix:** Build with the extra property:

```bash
dotnet build MyLibrary.Swift.iOS.csproj -p:EnableDefaultCompileItems=false
```

This is not needed when using the MSBuild SDK (`SwiftBindings.Sdk`), which handles this automatically.

### Duplicate member names

Swift allows method overloading that C# doesn't (e.g., methods differing only in return type). The generator renames these, but edge cases may produce duplicates.

**Fix:** Check `binding-report.json` for details. File an issue with the ABI JSON if you encounter this.

---

## Runtime Errors

### Mono JIT Crash (`jit-info.c:918`)

```
* Assertion at jit-info.c:918, condition '!ji->async' not met
```

This is a known Mono runtime defect. The JIT incorrectly marks `CallConvSwift` P/Invoke frames as async. **This kills the process — no managed exception handler can catch it.**

The generator includes four automatic workarounds (A through D) that route around this crash for most APIs. These are transparent — you don't need to do anything.

**Still affected:**
- Explicit `Dispose()` on structs with String/reference-type fields
- Closures with non-primitive arguments (String, class, struct args)
- These are tracked and will be resolved when the upstream Mono fix lands

### `SwiftRuntimeException: libSwiftBindingsRuntime.dylib not found`

The Mono JIT workarounds require `libSwiftBindingsRuntime.dylib` in the app bundle. This dylib is bundled inside the `SwiftBindings.Runtime` NuGet package and is automatically included when the package is referenced.

**Fix:** Verify that your app references the `SwiftBindings.Runtime` NuGet package (binding packages include it as a transitive dependency). If the package is referenced but the dylib is still missing, try a clean rebuild (`dotnet clean && dotnet build`).

### `InvalidProgramException: Cannot use non-blittable types with Swift calling convention`

.NET's `CallConvSwift` requires all P/Invoke parameters to be blittable. This affects `SwiftOptional<T>`, `SafeHandle`, and managed strings.

**Fix:** The generator handles this automatically via wrapper functions. If you see this at runtime, it's likely an edge case — file an issue.

### `ObjectDisposedException` on Swift object access

A Swift object was accessed after its `Dispose()` was called (or after GC collection).

**Fix:** Ensure you maintain a reference to Swift objects for as long as you need them. Use `using` statements for deterministic cleanup.

---

## Debugging Generated Bindings

### Where generated files live

**MSBuild SDK mode** (recommended): Generated files are in your project's intermediate output directory:

```
obj/Debug/<tfm>/swift-binding/
├── MyLibrary.cs                         # C# bindings (P/Invoke declarations, type wrappers)
├── MyLibrary.swift                      # Swift wrapper (async support, protocol dispatch)
├── MyLibrarySwiftBindings.xcframework/  # Compiled Swift wrapper
├── binding-report.json                  # What was bound and what was skipped
├── binding-metadata.props               # Extracted framework metadata
├── MyLibrary.Swift.<Platform>.targets   # Consumer NuGet targets
└── MyLibraryDatabase.xml                # Module database for cross-module resolution
```

Where `<tfm>` is your target framework (e.g., `net10.0-ios`, `net10.0-macos`) and `<Platform>` is the platform suffix (e.g., `iOS`, `macOS`, `MacCatalyst`, `tvOS`).

**CLI mode**: Files are in whatever `-o` output directory you specified.

### Reading the generated code

The `.cs` file is the place to look when something goes wrong at runtime. Each bound method has:

- A `[DllImport]` P/Invoke declaration with the mangled Swift symbol name
- A public C# method that marshals arguments, calls the P/Invoke, and marshals the return value
- `[Obsolete("SBxxxx")]` attributes flagging known risks (see [Binding Diagnostic IDs](#binding-diagnostic-ids))

When you hit a runtime error, find the method in the generated `.cs` and look at the P/Invoke signature — this tells you exactly what's being passed to Swift and how.

If you have the binding project source, the generated files are in `obj/Debug/net10.0-ios/swift-binding/`. If you only have the NuGet package, you can extract it (`.nupkg` is a zip) and decompile the DLL with [ILSpy](https://github.com/icsharpcode/ILSpy) or JetBrains dotPeek.

---

## Binding Report Analysis

The `binding-report.json` is the most useful diagnostic tool. When a binding doesn't cover an API you need:

1. Open `binding-report.json`
2. Search for the type or member name
3. Check the `skipReason` field

Common skip reasons and what they mean:

| Skip Reason | What It Means | What You Can Do |
|-------------|---------------|-----------------|
| `UnsupportedSignature` | Parameter/return type not handled | File an issue. May need a manual Swift wrapper. |
| `AnyTypeFallback` | Type couldn't be resolved | Check if a dependency framework is missing |
| `SwiftUIView` | It's a SwiftUI View | Check the SwiftUI bridge output instead |
| `SwiftUIConstraint` | Generic type parameter on a View | Can't be bound — use bridge hints to skip or template |
| `UnsupportedClosure` | Closure pattern not supported | Simplify the callback signature if possible |
| `GenericProtocolConstraint` | Generic constraint the generator can't express | May need manual wrapper |
| `DuplicateSignature` | Name collision after C# projection | Automatic dedup should handle this — file an issue if it doesn't |
| `SynthesizedCodable` | Codable `encode`/`init(from:)` pruned | By design — these are implementation details, not useful API surface |

---

## Binding Diagnostic IDs

Generated bindings use custom diagnostic IDs (via `[Obsolete]` attributes) to flag specific conditions at compile time:

| ID | Meaning | Action |
|----|---------|--------|
| `SB0001` | **Mono JIT crash risk** — method may crash on Mono (iOS Simulator). Safe on NativeAOT (device). | Suppressed automatically in NativeAOT builds. See [NativeAOT Deployment](NativeAOT-Deployment.md). |
| `SB0002` | **Missing symbol** — P/Invoke entry point not found in the library. Will throw `EntryPointNotFoundException`. | The Swift symbol wasn't exported. May need `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`. |
| `SB0003` | **Non-dispatchable protocol member** — can't dispatch through the witness table. Throws `NotSupportedException` on Swift-backed existentials. | Concrete type calls work fine. Only affects existential dispatch. |
| `SB0004` | **Empty protocol interface** — all members were skipped. Interface exists for type identity only. | Check `binding-report.json` for skip reasons. |

These IDs are scoped to Swift binding packages — suppressing them doesn't affect other `[Obsolete]` warnings.

---

## Alternative Approaches for Incompatible xcframeworks

Swift Bindings requires xcframeworks built as **dynamic** libraries with **`BUILD_LIBRARY_FOR_DISTRIBUTION=YES`** (library evolution enabled). This is a hard requirement — the generator extracts type information from `.swiftinterface` files, which only exist when library evolution is enabled. Without them, binding generation is not possible.

If you have an xcframework that doesn't meet these requirements and you can't rebuild it (e.g., a vendor SDK), the recommended alternative is **[Maui.NativeLibraryInterop](https://github.com/CommunityToolkit/Maui.NativeLibraryInterop)** (also called "Slim Bindings"). This Community Toolkit approach uses a native Swift/ObjC intermediary project to expose the APIs you need to .NET. It requires manual work per API surface, but doesn't depend on library evolution metadata. See the [Microsoft Learn docs](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/maui/native-library-interop/) for a full walkthrough.

---

## Reporting Issues

When filing an issue, include:

1. **Generator logs** — run with `-v 2` for verbose output
2. **Binding report** — the `binding-report.json` from the output directory
3. **The xcframework** (if possible) — or at minimum the ABI JSON (`-a` output) and TBD file

The binding report alone often contains enough information to diagnose the root cause.

---

## Next Steps

- **[Known Limitations](Known-Limitations.md)** — Platform and runtime constraints
- **[Getting Started](Getting-Started.md)** — Setup instructions
