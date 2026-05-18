# Bug: SDK 0.11.0 — Swift-mode macOS slice passes iOS `MinDeploymentVersion` straight into `-target arm64-apple-macos<X>`, swift-api-digester fails silently

> **Status: resolved.** Fix combines bug-doc suggestions #1 and #4:
>
> 1. **Per-platform metadata** on `<SwiftAppleFrameworkTarget>` —
>    `<MinIOSVersion>`, `<MinMacOSVersion>`, `<MinTvOSVersion>`,
>    `<MinMacCatalystVersion>`. A new property
>    `$(_SwiftEffectiveMinDeploymentVersion)` is resolved in
>    `_ResolveAppleFrameworkPaths` by seeding from the legacy
>    `<MinDeploymentVersion>` and then letting the platform-specific
>    override for the active slice win. The effective version threads
>    through every triple computation (digester, second-slice merge),
>    the fingerprint, and `binding-metadata.props` — so consumers'
>    `SupportedOSPlatformVersion` is also correctly per-slice.
> 2. **Fail-loudly digester validation** — `_DumpAppleFrameworkAbi`
>    now greps the produced `abi.json` for the literal placeholder
>    field `"name": "NO_MODULE"` (matched as `"name"\s*:\s*"NO_MODULE"`
>    so a Swift symbol named `NO_MODULE` cannot false-positive) and
>    raises `SWIFTBIND038` pointing at the target triple and the
>    per-platform metadata as the user-facing fix. No more silent
>    fallthrough to a misdiagnosed `BUILD_LIBRARY_FOR_DISTRIBUTION`
>    error one layer up.
>
> Consumer migration for MatterSupport (the package this blocked):
>
> ```xml
> <SwiftAppleFrameworkTarget Include="MatterSupport">
>   <MinIOSVersion>16.1</MinIOSVersion>
>   <MinMacOSVersion>13.3</MinMacOSVersion>
>   <MinMacCatalystVersion>16.4</MinMacCatalystVersion>
> </SwiftAppleFrameworkTarget>
> ```
>
> Unit coverage: `SdkTargetsContentTests`
> (`Targets_AppleFrameworkEffectiveMinVersion_CascadesPerPlatform`,
> `Targets_DigesterTriple_UsesEffectiveMinVersion`,
> `Targets_BindingMetadataProps_UsesEffectiveMinVersion`,
> `Targets_SecondSliceMerge_UsesEffectiveMinVersion`,
> `Targets_AppleFrameworkAbiDump_FailsOnDegenerateOutput`,
> `Targets_EffectiveMinVersion_SeedsFromLegacyFirst`).

## Summary

When a Swift-mode apple-framework binding declares `<MinDeploymentVersion>` metadata on a `<SwiftAppleFrameworkTarget>` item (e.g. MatterSupport's `16.1`), the SDK uses that exact value when invoking `swift-api-digester` for the **macOS** slice — producing the invalid target triple `arm64-apple-macos16.1`.

swift-api-digester emits `error: invalid version number in '-target arm64-apple-macos16.1'`, fails to load the module, and then **writes a degenerate `abi.json`** containing `name: "NO_MODULE"` and `children: []`. The digester's own non-zero exit isn't surfaced — the SDK only fails later in `Swift.Bindings.dll` with:

```
fail: BindingsGeneration.BindingsGenerator[0]
      Binding generation failed: ABI JSON has invalid module name ''.
      The Swift library must be compiled with BUILD_LIBRARY_FOR_DISTRIBUTION=YES
      (swiftc -enable-library-evolution) to produce valid ABI metadata.
```

That error message is misleading — the .swiftinterface for MatterSupport is fine; the digester invocation is wrong.

## Reproduction (in swift-dotnet-packages)

`apple-frameworks/MatterSupport/SwiftBindings.Apple.MatterSupport.csproj`:

```xml
<Project Sdk="SwiftBindings.Sdk/0.11.0">
  <PropertyGroup>
    <TargetFramework />
    <TargetFrameworks>net10.0-ios26.2;net10.0-macos26.2;net10.0-maccatalyst26.2</TargetFrameworks>
    <PackageId>SwiftBindings.Apple.MatterSupport</PackageId>
    <Version>26.2.3</Version>
  </PropertyGroup>
  <ItemGroup>
    <SwiftAppleFrameworkTarget Include="MatterSupport">
      <MinDeploymentVersion>16.1</MinDeploymentVersion>
    </SwiftAppleFrameworkTarget>
  </ItemGroup>
</Project>
```

```bash
rm -rf apple-frameworks/MatterSupport/{obj,bin}
dotnet build apple-frameworks/MatterSupport/SwiftBindings.Apple.MatterSupport.csproj -f net10.0-macos26.2
```

Result: build fails. Direct invocation of swift-api-digester with the same triple confirms the cause:

```
$ xcrun swift-api-digester -dump-sdk -module MatterSupport -target arm64-apple-macos16.1 \
    -sdk /Applications/Xcode-26.3.0.app/.../MacOSX26.2.sdk -o /tmp/out.json \
    -F /Applications/Xcode-26.3.0.app/.../MacOSX26.2.sdk/System/Library/Frameworks
<unknown>:0: error: invalid version number in '-target arm64-apple-macos16.1'
Failed to load module: MatterSupport
```

Switching the triple to a real macOS version (`arm64-apple-macos13.3` — MatterSupport's actual macOS minimum per Apple's docs and the package README) succeeds:

```
size: 216062
name: MatterSupport
children count: 12
mentions MatterAddDeviceRequest: True
```

## Why iOS and Mac Catalyst slices work

- `-target arm64-apple-ios16.1` is a valid iOS triple → iOS slice succeeds.
- `-target arm64-apple-ios16.1-macabi` is a valid Mac Catalyst triple → maccatalyst slice succeeds (confirmed by `dotnet build -f net10.0-maccatalyst26.2` — clean, 0 errors).
- `-target arm64-apple-macos16.1` doesn't exist (macOS is on the 13.x–26.x train, not the 16.x iOS train) → macOS slice fails.

Apple's per-platform availability for `MatterAddDeviceRequest` (from MatterSupport.swiftinterface):

| Platform     | Minimum |
|--------------|---------|
| iOS          | 16.1    |
| iPadOS       | 16.1    |
| macOS        | 13.3    |
| Mac Catalyst | 16.4    |

A single `<MinDeploymentVersion>` field on the binding csproj cannot encode all four. The SDK currently treats it as iOS-flavoured and reuses it verbatim on every slice — works on iOS/maccatalyst, blows up on macOS.

## Why Matter (the sibling package) works

`apple-frameworks/Matter/SwiftBindings.Apple.Matter.csproj` uses `<SwiftFrameworkType>ObjC</SwiftFrameworkType>`, so the SDK skips swift-api-digester entirely. All three Matter slices build clean.

The bug bites only **Swift-mode apple-framework bindings whose macOS minimum differs from their iOS minimum** — which includes most newer Apple frameworks (RealityKit, WeatherKit, Translation, MatterSupport, …).

## Suggested fixes (any one of these)

1. **Per-platform metadata on `<SwiftAppleFrameworkTarget>`** — e.g. `<MinIOSVersion>`, `<MinMacOSVersion>`, `<MinMacCatalystVersion>`, `<MinTvOSVersion>`. The macOS slice would use `<MinMacOSVersion>` and fall back to a sensible default (or the consumer csproj's `SupportedOSPlatformVersion`) when absent. This matches how the Xamarin/.NET-for-iOS workload models per-platform minimums elsewhere.

2. **Apple-frameworks registry lookup** — if the SDK already keeps a registry (cf. `--detect-apple-cross-module-deps`'s `apple-frameworks.json` mention in the help text), record per-platform minimums there and use the registry value matching the current TFM. Consumers wouldn't need to specify `MinDeploymentVersion` at all for known frameworks.

3. **Cap to the slice's `-platform-version`** — the SDK already passes `--platform-version 26.2` to `Swift.Bindings.dll` (the consumer's `SupportedOSPlatformVersion`). Use that as the macOS triple's version instead of `MinDeploymentVersion`. Less precise but unblocks the macOS slice on any Swift-mode binding without consumer changes.

4. **At minimum, fail loudly** — if swift-api-digester returns non-zero, the SDK should surface that exit code with the captured stderr (`invalid version number in '-target arm64-apple-macos16.1'`) instead of letting it write a degenerate abi.json and then misdiagnose it as a "BUILD_LIBRARY_FOR_DISTRIBUTION" problem one layer up. The current error message points consumers at a Swift-vendor flag that has nothing to do with their actual problem.

## Status of the broader shipping work

This is the only known macOS blocker for `SwiftBindings.Apple.MatterSupport 26.2.3`:

- Matter (ObjC): iOS / macOS / maccatalyst all build clean.
- MatterSupport (Swift): iOS + maccatalyst slices build clean; macOS slice blocked on this bug.

Once the SDK fix lands the multi-TFM expansion of both packages should complete without further consumer-side changes — the test harnesses (Program.UIKit.cs for iOS+maccatalyst, Program.MacConsole.cs for macOS) and test csprojs are already in place.
