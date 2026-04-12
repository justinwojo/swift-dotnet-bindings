# Deployment Versions and Availability Attributes

## Platform Floors

The minimum deployment version for each platform is set by .NET 10's supported OS matrix:

| Platform | Floor | Source |
|----------|-------|--------|
| iOS | 15.0 | .NET 10 minimum |
| macOS | 12.0 | .NET 10 minimum |
| tvOS | 15.0 | .NET 10 minimum |
| Mac Catalyst | 15.0 | Follows iOS versioning |

These floors are enforced at two layers:

1. **`Sdk.props`** — `SwiftAppleFrameworkMinDeploymentVersion` defaults are platform-specific (conditioned on TFM). Used for the `swift-api-digester` target triple and wrapper compilation. Users can override per-project.

2. **`XCFrameworkMetadataExtractor`** — `ClampMinimumOSVersion()` enforces a 15.0 floor for third-party xcframeworks. If a library's `Info.plist` declares `MinimumOSVersion < 15.0`, it's raised to 15.0.

3. **`SwiftWrapperCompiler`** — `ResolveDeploymentTarget()` reads the source framework's `Info.plist` and enforces the same 15.0 floor via `EnforceMinimumDeploymentTarget()`.

## Availability Attributes Handle Everything Above the Floor

APIs that require a newer OS version than the floor are gated by `@available` / `[SupportedOSPlatform]` attributes, not by raising the deployment target. This means:

- A library targeting iOS 15.0 can still bind APIs introduced in iOS 16+, 17+, etc.
- The Swift compiler enforces that generated `@_cdecl` / `@_silgen_name` wrappers using newer-SDK types carry `@available` annotations. Without them, the Swift compiler errors (not warns).
- The C# bindings carry `[SupportedOSPlatform("ios16.0")]` etc., so the .NET analyzer warns consumers calling these APIs on older targets.

### What availability covers

The generator propagates availability annotations through the full chain:

| Level | Swift (`@available`) | C# (`[SupportedOSPlatform]`) |
|-------|---------------------|------------------------------|
| Types | Inherited from ABI JSON / swiftinterface | Emitted on class/struct/enum |
| Methods | Inherited from parent type + own annotations | Emitted on method |
| Properties | Same as methods | Emitted on property |
| Property accessors | Merged: property-level + accessor-level (setter can be tighter) | Accessor-level `[SupportedOSPlatform]` when setter is tighter |
| Constructors | Inherited from parent type | Emitted on constructor |
| Enum cases | Inherited from ancestor chain | Emitted per case factory |
| `@_cdecl` wrappers | `MergeAvailabilityFromAncestors` walks full parent chain | N/A (Swift-side only) |
| `@_silgen_name` extension wrappers | `@available` on `extension` keyword line | N/A (Swift-side only) |
| Default parameter overloads | Copied from original method | Emitted on overload |
| Protocol witnesses | `MergeAvailabilityFromAncestors` | N/A |

### Platforms parsed

iOS, macOS, tvOS, watchOS, macCatalyst, visionOS are all parsed from ABI JSON and swiftinterface. visionOS is currently skipped in C# emission (no .NET equivalent). watchOS is parsed but not targeted by the build system.

## Why not raise the floor to iOS 16?

Historically, the deployment floor was 16.0 as a blanket workaround for parameterized existentials (`any AsyncSequence<Element, Failure>`) requiring Swift 5.7 runtime support (iOS 16+). This was overly broad:

- Libraries that don't use parameterized existentials were unnecessarily restricted.
- The availability attribute system now handles this correctly — wrapper functions using parameterized existentials carry `@available(iOS 16.0, *)`, and the Swift compiler enforces it.
- .NET 10's iOS floor is 15.0, and consumers expect to be able to target it.

The correct approach: set the deployment target to the platform floor (15.0 for iOS), and let `@available` attributes gate individual APIs that require newer SDKs. The Swift compiler itself acts as the enforcer.

## macOS Specifics

macOS deployment version defaults to 12.0 in `Sdk.props` (conditioned on `$(TargetFramework.Contains('macos'))`). The `maccatalyst` check comes first to avoid substring overlap — Mac Catalyst follows iOS versioning (15.0).

The `ClampMinimumOSVersion` in `XCFrameworkMetadataExtractor` is platform-agnostic (uses 15.0 for all platforms). For macOS xcframeworks, this means a declared `MinimumOSVersion` of 12.0 would be raised to 15.0. This is a known limitation — the clamping logic doesn't distinguish between iOS and macOS version numbering. In practice, the Apple-framework path (which uses `Sdk.props` per-platform defaults) is not affected.

## TODO: Regenerate swift-dotnet-packages apple-frameworks

The generated bindings in `swift-dotnet-packages/apple-frameworks/*/obj/Release/` were built with the old 16.0 floor. Every Release csproj has `<SupportedOSPlatformVersion>16.0</SupportedOSPlatformVersion>` and every Release `.targets` warns at `< 16.0`. These need to be regenerated with the current SDK (which uses the 15.0 floor) so that the actual Apple SDK `MinimumOSVersion` flows through correctly. Rebuild with `nuke pack --version 0.8.0`, deploy to `swift-dotnet-packages/local-packages/`, and `dotnet build` each apple-framework project in Release.

## Namespace Collisions with Microsoft.iOS

Some Apple frameworks share namespace names with Microsoft.iOS's ObjC bindings. These require `NamespacePattern` metadata on `<SwiftAppleFrameworkTarget>` to avoid collisions:

| Framework | Collides? | Workaround |
|-----------|-----------|------------|
| StoreKit | Yes | `NamespacePattern="StoreKit2"` |
| SoundAnalysis | Yes | Needs `NamespacePattern` before publish |
| CoreSpotlight | Yes | Needs `NamespacePattern` before publish |
| AuthenticationServices | Yes | Needs `NamespacePattern` before publish |
| WeatherKit | No | — |
| TipKit | No | — |
| CryptoKit | No | — |
| WorkoutKit | No | — |
| RoomPlan | No | — |
| ProximityReader | No | — |
| LiveCommunicationKit | No | — |

The generator has no automatic collision detection against Microsoft.iOS namespaces. The `NamespacePattern` metadata must be set manually in the framework's csproj.
