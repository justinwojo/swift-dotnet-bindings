# Troubleshooting

## MSBuild SDK Errors

These errors come from the SDK's build targets and have clear remediation steps.

| Code | Message | Fix |
|------|---------|-----|
| `SWIFTBIND001` | No xcframework found | Copy a `.xcframework` into the project directory, or add `<SwiftFramework Include="path/to/Library.xcframework" />` |
| `SWIFTBIND002` | Multiple xcframeworks found (ambiguous) | Declare explicit `<SwiftFramework>` items in your `.csproj` |
| `SWIFTBIND003` | xcframework path doesn't exist | Check the path in your `<SwiftFramework>` item |
| `SWIFTBIND010` | Consumer's `SupportedOSPlatformVersion` too low | Raise your app's `SupportedOSPlatformVersion` to match the framework's minimum |
| `SWIFTBIND020` | Version placeholder detected | The framework uses Xcode's default "1.0" version. Set `<PackageVersion>` manually in your `.csproj` |
| `SWIFTBIND030` | Packing without `SwiftWrapperArchitectures=all` | Set `<SwiftWrapperArchitectures>all</SwiftWrapperArchitectures>` before running `dotnet pack` |
| `SWIFTBIND031` | Wrapper xcframework missing device or simulator slice | Rebuild with `SwiftWrapperArchitectures=all` to compile both slices |
| `SWIFTBIND100` | `<SwiftPackage>` used (not yet available) | SPM support is planned. Build your SPM package into an xcframework first, then use `<SwiftFramework>`. |

## Generator Errors

### "Static xcframework detected"

The generator only supports **dynamic** xcframeworks (containing `.dylib` or `.framework` bundles). Static xcframeworks (`.a` archives) are not supported.

**Fix:** Rebuild the framework as a dynamic library. In Xcode, set `MACH_O_TYPE` to `mh_dylib`.

### "No Swift module found"

The xcframework doesn't contain a `.swiftmodule` directory. It may be an Objective-C-only framework.

**Fix:** This tool only binds Swift libraries. For ObjC frameworks, use the existing .NET iOS binding tools.

### "swift-frontend failed"

ABI JSON extraction failed. This usually means the `.swiftinterface` file is malformed or the Xcode toolchain version is incompatible.

**Fix:** Ensure Xcode is up to date. Check the error output for specific compiler messages.

## Build Errors in Generated Code

### Missing type references

```
error CS0246: The type or namespace name 'SomeType' could not be found
```

This usually means a type from a dependent framework wasn't resolved. The binding report (`binding-report.json`) will show these as `AnyTypeFallback` skip reasons.

**Fix:** Ensure all dependent frameworks are available. If the type is from UIKit/Foundation, it may need to be added to the type database.

### Duplicate member names

Swift allows method overloading that C# doesn't (e.g., methods differing only in return type). The generator renames these, but edge cases may produce duplicates.

**Fix:** Check `binding-report.json` for details. File an issue with the ABI JSON if you encounter this.

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

## Binding Report Analysis

The `binding-report.json` is the most useful diagnostic tool. When a binding doesn't cover an API you need:

1. Open `binding-report.json`
2. Search for the type or member name
3. Check the `skipReason` field

Common skip reasons and what they mean:

| Skip Reason | What It Means | What You Can Do |
|-------------|---------------|-----------------|
| `UnsupportedSignature` | Parameter/return type not handled | File an issue. May need a manual Swift wrapper. |
| `AnyTypeFallback` | Type couldn't be resolved | Check if a dependency is missing from the type database |
| `SwiftUIView` | It's a SwiftUI View | Check the SwiftUI bridge output instead |
| `SwiftUIConstraint` | Generic type parameter on a View | Can't be bound — use bridge hints to skip or template |
| `UnsupportedClosure` | Closure pattern not supported | Simplify the callback signature if possible |
| `GenericProtocolConstraint` | Generic constraint the generator can't express | May need manual wrapper |
| `DuplicateSignature` | Name collision after C# projection | Automatic dedup should handle this — file an issue if it doesn't |

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
