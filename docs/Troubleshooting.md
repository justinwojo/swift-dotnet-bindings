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

### Missing iOS workload

If `dotnet build` fails with errors about `net10.0-ios` being an unknown target framework:

```bash
dotnet workload install ios
```

---

## MSBuild SDK Errors

These errors come from the SDK's build targets and have clear remediation steps.

| Code | Message | Fix |
|------|---------|-----|
| `SWIFTBIND001` | No xcframework found | Copy a `.xcframework` into the project directory, or add `<SwiftFramework Include="path/to/Library.xcframework" />` |
| `SWIFTBIND002` | Multiple xcframeworks found (ambiguous) | Declare explicit `<SwiftFramework>` items in your `.csproj` |
| `SWIFTBIND003` | xcframework path doesn't exist | Check the path in your `<SwiftFramework>` item |
| `SWIFTBIND010` | Consumer's `SupportedOSPlatformVersion` too low | Raise your app's `SupportedOSPlatformVersion` to match the framework's minimum |
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
| `SWIFTBIND100` | `<SwiftPackage>` used (not yet available) | SPM support is planned. Build your SPM package into an xcframework first, then use `<SwiftFramework>`. |

---

## Generator Errors

### "Static xcframework detected"

The generator only supports **dynamic** xcframeworks (containing `.dylib` or `.framework` bundles). Static xcframeworks (`.a` archives) are not supported.

**Fix:** Rebuild the framework as a dynamic library. In Xcode, set `MACH_O_TYPE` to `mh_dylib`.

### "No Swift module found"

The xcframework doesn't contain a `.swiftmodule` directory. It may be an Objective-C-only framework.

**Fix:** This tool only binds Swift libraries. For ObjC frameworks, use the existing .NET iOS binding tools.

### Generator crash or empty/incomplete output

If the generator crashes with an empty module name or produces very few bindings, the framework was likely not built with library evolution enabled.

**Fix:** Rebuild the xcframework with `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`:

```bash
xcodebuild archive -scheme MyLibrary \
  -destination "generic/platform=iOS Simulator" \
  SKIP_INSTALL=NO BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
  -archivePath ./build/sim
```

This flag enables the stable ABI metadata (`.swiftinterface` files) that the generator relies on. Without it, the ABI JSON may be missing or malformed.

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
error CS2002: Source file 'Swift.MyLibrary.cs' specified multiple times
```

When building a `.csproj` generated by CLI mode (`--xcframework` without `--sdk-mode`), the .NET SDK auto-includes `*.cs` files while the generated project also lists them explicitly.

**Fix:** Build with the extra property:

```bash
dotnet build MyLibrary.Swift.iOS.csproj -p:EnableDefaultCompileItems=false
```

This is not needed when using the MSBuild SDK (`Swift.Bindings.Sdk`), which handles this automatically.

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

The Mono JIT workarounds require `libSwiftBindingsRuntime.dylib` in the app bundle.

**Fix:** Ensure the `Swift.Runtime` NuGet package is referenced and the native dylib is included in your build output.

### `InvalidProgramException: Cannot use non-blittable types with Swift calling convention`

.NET's `CallConvSwift` requires all P/Invoke parameters to be blittable. This affects `SwiftOptional<T>`, `SafeHandle`, and managed strings.

**Fix:** The generator handles this automatically via wrapper functions. If you see this at runtime, it's likely an edge case — file an issue.

### `ObjectDisposedException` on Swift object access

A Swift object was accessed after its `Dispose()` was called (or after GC collection).

**Fix:** Ensure you maintain a reference to Swift objects for as long as you need them. Use `using` statements for deterministic cleanup.

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
| `SB0001` | **Mono JIT crash risk** — method may crash on Mono (iOS Simulator). Safe on NativeAOT (device). | Suppressed automatically in NativeAOT builds. See [NativeAOT Deployment](NativeAOT-Deployment). |
| `SB0002` | **Missing symbol** — P/Invoke entry point not found in the library. Will throw `EntryPointNotFoundException`. | The Swift symbol wasn't exported. May need `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`. |
| `SB0003` | **Non-dispatchable protocol member** — can't dispatch through the witness table. Throws `NotSupportedException` on Swift-backed existentials. | Concrete type calls work fine. Only affects existential dispatch. |
| `SB0004` | **Empty protocol interface** — all members were skipped. Interface exists for type identity only. | Check `binding-report.json` for skip reasons. |

These IDs are scoped to Swift binding packages — suppressing them doesn't affect other `[Obsolete]` warnings.

---

## Reporting Issues

When filing an issue, include:

1. **Generator logs** — run with `-v 2` for verbose output
2. **Binding report** — the `binding-report.json` from the output directory
3. **The xcframework** (if possible) — or at minimum the ABI JSON (`-a` output) and TBD file

The binding report alone often contains enough information to diagnose the root cause.

---

## Next Steps

- **[Known Limitations](Known-Limitations)** — Platform and runtime constraints
- **[Getting Started](Getting-Started)** — Setup instructions
