# Maccatalyst Swift Wrapper Compile Failures

Three Apple framework targets fail Swift wrapper compilation on the Mac Catalyst slice. The C# bindings compile cleanly; only the per-slice Swift wrapper binary is missing.

## Affected targets

- `LiveCommunicationKit@maccatalyst`
- `ProximityReader@maccatalyst`
- `StoreKit2@maccatalyst`

The other Mac Catalyst targets in the apple-framework tier compile and ship a Swift wrapper binary correctly. iOS, macOS, and tvOS slices for these three frameworks all pass.

## How the failure surfaces

`nuke validate` runs `Build.Validation.CheckSwiftWrapper` over each target's `*SwiftBindings.framework` directory and asserts that every slice contains both the compiled binary and an embedded `Info.plist`. For the three failing targets, the Mac Catalyst slice contains only `Info.plist` — the wrapper compile produces no binary, but the build pipeline does not propagate the failure as an exit code, so the gate's slice scan is the only signal.

```
$xcframework/ios-arm64-maccatalyst/<Lib>SwiftBindings.framework/
  Info.plist          # present
  <Lib>SwiftBindings  # MISSING
```

`.validation-baseline.json` records this as `swift_compile: "fail"` for each of the three targets. The C# `compile` field is `"ok"` — only the Mac Catalyst Swift wrapper is broken.

## How long it has been broken

`swift_compile: "fail"` was the value baked into the very first baseline that included these targets, commit `71523beb` on 2026-04-21 ("Add apple-framework validation tier with multi-slice packaging gate"). They have never compiled cleanly on Mac Catalyst within this validation tier. Released NuGet packages built against any version since that commit ship with a non-functional Mac Catalyst slice for these three frameworks.

## Likely root cause

The wrapper compile path for Mac Catalyst slices runs through `SwiftWrapperCompiler.CompileAll` / `CompileSlice` with target triple `arm64-apple-ios15.0-macabi`. The compile step swallows non-zero exit and the gate only checks the artifact, so the actual swiftc diagnostic is not surfaced in `nuke validate` output.

Suspected contributors:

1. **Framework search paths**: Mac Catalyst pulls frameworks from both `MacOSX*.sdk/System/iOSSupport/System/Library/Frameworks` (iOS-style frameworks made available on Catalyst) and `MacOSX*.sdk/System/Library/Frameworks` (macOS-native). The wrapper build may resolve a framework against the wrong root, producing an interface mismatch. ProximityReader and LiveCommunicationKit are iOS-only frameworks exposed via iOSSupport; StoreKit2 has both representations.
2. **Availability gates**: The C# bindings emit `[SupportedOSPlatform("ios" 26.0)]` for `IMobileDocumentRawDataRequest` and similar — those are the warnings (`CA1416`) visible during the C# build. The Swift wrapper may be referencing types that exist in the iOS interface but are unavailable in the Catalyst slice's swiftinterface, causing a `cannot find type` at swiftc time.
3. **Architecture filter**: The xcframework Info.plist for `ios-arm64-maccatalyst` may report only one architecture while the wrapper compile is invoked with both. Less likely given that other Mac Catalyst targets pass.

## What needs to happen to fix

1. Surface the swiftc diagnostic. `Build.Validation.CompileWrapper` only captures Swift error lines when `Verbose` is set and the swift status is `fail`; in practice the failure reaches the gate via `CheckSwiftWrapper`'s artifact check, not via swiftc exit code, so `result.SwiftVerbose` stays empty. The wrapper compile pipeline (`SwiftWrapperCompiler.CompileSlice`) needs to propagate stderr through the gate even when the artifact path branch fires.
2. Run swiftc by hand for `ProximityReader@maccatalyst` after generation: `cd /var/folders/.../T/binding-validation-main/ProximityReader@maccatalyst && xcrun --sdk macosx swiftc -target arm64-apple-ios15.0-macabi -emit-library …` against the same `Wrapper.swift` and observe the actual error.
3. Once the diagnostic is known, fix the wrapper emission or the wrapper-build invocation. If it is an availability mismatch, the fix is in `WrapperEmitter` or `XCFrameworkResolver`; if it is a search-path mismatch, the fix is in `SwiftWrapperCompiler.CompileSlice`.

## Mitigation for shipping

These three frameworks should be flagged as Mac-Catalyst-incompatible in the consumer-facing Known Limitations list until the wrapper compile is fixed. Existing NuGet packages built since 2026-04-21 ship with a Mac Catalyst slice that lacks the Swift wrapper binary; consumers who target Mac Catalyst will fail at link time on these three.
