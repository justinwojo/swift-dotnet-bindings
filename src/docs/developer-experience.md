# Developer Experience: Multi-Framework Packaging & Future Design Reference

> Forward-looking design reference for multi-framework dependencies (DX-3),
> multi-platform support, and advanced packaging scenarios.
>
> **For the current SDK implementation (Steps 1-5), see `dx-msbuild-sdk-design.md`.**
>
> See also: `/north-star.md` for the overall project vision.

## Table of Contents

- [Problem Statement](#problem-statement)
- [Apple Framework Rules](#apple-framework-rules)
- [Package Architecture](#package-architecture)
- [Dependency Enforcement](#dependency-enforcement)
- [Automatic iOS Version Detection](#automatic-ios-version-detection)
- [NuGet Package Structure](#nuget-package-structure)
- [Platform Coverage](#platform-coverage)
- [Open Questions](#open-questions)

---

## Problem Statement

Many Swift libraries ship as multiple dependent frameworks. For example:

| Library | Frameworks | Dependency Chain |
|---------|-----------|-----------------|
| **Nuke** | Nuke, NukeUI, NukeExtensions | NukeUI → Nuke, NukeExtensions → Nuke |
| **BlinkID** | BlinkID, BlinkIDUX | BlinkIDUX → BlinkID |
| **Firebase** | 20+ frameworks | Complex dependency graph |

Today's .NET iOS binding ecosystem has a critical failure mode: **packages don't declare native framework dependencies, so apps compile successfully but crash at launch** with `dyld: Library not loaded`. This happens because NuGet only tracks managed assembly dependencies — the native xcframework dependency graph is invisible to the build system.

Our packaging system must make this impossible. If you install `NukeUI.Swift.iOS` without `Nuke.Swift.iOS`, you should get a **clear build error**, not a runtime crash.

---

## Apple Framework Rules

### What's Allowed

- Multiple `.framework` bundles as **flat siblings** in the app's `Frameworks/` directory
- No limit on the number of embedded frameworks
- Each Swift library as its own `.xcframework` resolved at build time

### What's Forbidden

- **Umbrella frameworks** (framework containing other frameworks) — rejected by App Store on iOS with `ITMS-90171: Invalid Bundle Structure`
- **Standalone `.dylib` files** in the app bundle — must be wrapped in `.framework` bundles
- **Nested frameworks** on iOS/watchOS/tvOS — only macOS technically supports them (and even there, Apple discourages it)

### Correct Architecture

```
MyApp.app/
  Frameworks/
    Nuke.framework            ← flat sibling
    NukeUI.framework          ← flat sibling (links against Nuke, but NOT nested inside it)
    NukeExtensions.framework  ← flat sibling
    SwiftBindings.framework   ← our runtime bridge library
```

All frameworks are peers. The dynamic linker (`dyld`) resolves cross-framework references via `@rpath` at load time. The app target is responsible for embedding all frameworks, including transitive dependencies.

**Reference**: [TN2435: Embedding Frameworks In An App](https://developer.apple.com/library/archive/technotes/tn2435/_index.html), [Guidelines for Creating Frameworks](https://developer.apple.com/library/archive/documentation/MacOSX/Conceptual/BPFrameworks/Concepts/CreationGuidelines.html)

---

## Package Architecture

### Decision: Separate NuGet Packages with Explicit Dependencies

Each bound Swift framework becomes its own NuGet package. Dependencies between packages mirror the Swift framework dependency chain.

```
Swift.Runtime                    ← Core runtime (SwiftSafeHandle, ARC, marshalling)
  ↑
Nuke.Swift.iOS                   ← Nuke.xcframework + generated C# bindings
  ↑
NukeUI.Swift.iOS                 ← NukeUI.xcframework + generated C# bindings
  ↑                                 Declares dependency on Nuke.Swift.iOS
NukeExtensions.Swift.iOS         ← NukeExtensions.xcframework + generated C# bindings
                                    Declares dependency on Nuke.Swift.iOS
```

### Why Not a Single Monolithic Package?

A single package containing all xcframeworks (Nuke + NukeUI + NukeExtensions) is simpler but has significant downsides:

1. **Package bloat** — consumers pull all frameworks even if they only need the core library
2. **`NETSDK1152` conflict** — when multiple xcframeworks are placed directly in `runtimes/*/native/` (not nested in their own subdirectories), NuGet's file-flattening during pack can cause `Info.plist` collisions. This specifically affects packages where frameworks are dumped as loose files; it does **not** affect packages where each xcframework is in its own named subdirectory (see [SwiftUI Bridge Package](#swiftui-bridge-package-optional-add-on) for the safe pattern).
3. **Version coupling** — can't update NukeUI independently of Nuke
4. **Doesn't scale** — libraries like Firebase have 20+ frameworks; a monolith is untenable

### Consumer Experience

```xml
<!-- Consumer adds one line — NuGet handles the rest -->
<PackageReference Include="NukeUI.Swift.iOS" Version="12.0.0" />
<!-- Nuke.Swift.iOS and Swift.Runtime are automatically pulled in as transitive dependencies -->
```

---

## Dependency Enforcement

### The Problem Today

The .NET iOS ecosystem has a gap between two dependency worlds:

| Layer | Managed (.NET) | Native (xcframework) |
|-------|---------------|---------------------|
| Dependency manager | NuGet | CocoaPods / SPM (not available in .NET) |
| Missing dependency | `dotnet restore` fails (NU1101) | **No error** |
| Runtime failure | `TypeLoadException` | **`dyld: Library not loaded` — app crash** |

### Our Four-Layer Defense

We enforce dependencies at **four levels**, so a missing framework is caught long before the app launches.

#### Layer 1: NuGet Package Dependencies (Automatic)

The binding project's `.csproj` declares `PackageReference` items. `dotnet pack` converts these into NuGet dependency entries automatically.

```xml
<!-- NukeUI.Swift.iOS.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
    <IsPackable>true</IsPackable>
    <PackageId>NukeUI.Swift.iOS</PackageId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Nuke.Swift.iOS" Version="12.8.0" />
    <PackageReference Include="Swift.Runtime" Version="1.0.0" />
  </ItemGroup>
</Project>
```

**Result**: Installing `NukeUI.Swift.iOS` without a NuGet source containing `Nuke.Swift.iOS` fails at `dotnet restore` with error `NU1101`.

#### Layer 2: NativeReference Injection via `.targets` (Automatic)

Each package ships a `.targets` file in `buildTransitive/` (not duplicated in `build/` — see Decision 3 in `dx-msbuild-sdk-design.md`). This injects the xcframework as a `NativeReference` into the consuming project at build time.

```xml
<!-- NukeUI.Swift.iOS.targets (shipped in buildTransitive/net10.0-ios/) -->
<Project>
  <Target Name="_ResolveNukeUINativeReferences" BeforeTargets="ResolveNativeReferences"
          Condition="'$(_SwiftBinding_NukeUI_Injected)' == ''">
    <PropertyGroup>
      <_SwiftBinding_NukeUI_Injected>true</_SwiftBinding_NukeUI_Injected>
    </PropertyGroup>
    <ItemGroup>
      <NativeReference Include="$(MSBuildThisFileDirectory)../../runtimes/ios-arm64/native/NukeUI.xcframework">
        <Kind>Framework</Kind>
      </NativeReference>
    </ItemGroup>
  </Target>
</Project>
```

Since we target .NET 10.0 (NuGet 5.0+), `buildTransitive/` is sufficient for both direct and transitive consumers. The idempotency guard prevents duplicate NativeReference injection.

#### Layer 3: Build-Time Validation Target (Our Addition)

This is the layer that existing iOS binding packages are missing. Each package declares what native frameworks it requires and what it provides via a dedicated `SwiftBindingFramework` item. A validation target checks that all requirements are satisfied before the build proceeds.

The key challenge is that `NativeReference` `Identity` is a **full filesystem path** (e.g., `~/.nuget/packages/.../Nuke.xcframework`), not a bare framework name. Matching on `Identity == 'Nuke'` would always fail. Instead, each package registers the frameworks it provides via a separate item group, and the validation target checks that item group — not the raw NativeReference paths.

First, the **provider side** — each package's `.targets` registers what it provides (this ships inside the Nuke.Swift.iOS package):

```xml
<!-- Nuke.Swift.iOS.targets (ships inside the Nuke.Swift.iOS NuGet package) -->
<Project>
  <!-- Register that this package provides the "Nuke" framework -->
  <ItemGroup>
    <SwiftBindingFramework Include="Nuke">
      <SourcePackage>Nuke.Swift.iOS</SourcePackage>
    </SwiftBindingFramework>
  </ItemGroup>

  <!-- NativeReference injection (Layer 2) -->
  <Target Name="_ResolveNukeNativeReferences" BeforeTargets="ResolveNativeReferences">
    <ItemGroup>
      <NativeReference Include="$(MSBuildThisFileDirectory)..\..\runtimes\ios-arm64\native\Nuke.xcframework">
        <Kind>Framework</Kind>
        <SmartLink>True</SmartLink>
      </NativeReference>
    </ItemGroup>
  </Target>
</Project>
```

Then the **consumer side** — the dependent package validates its requirements (this ships inside the NukeUI.Swift.iOS package):

```xml
<!-- NukeUI.Swift.iOS.targets (ships inside the NukeUI.Swift.iOS NuGet package) -->
<Project>
  <!-- Register that this package provides the "NukeUI" framework -->
  <ItemGroup>
    <SwiftBindingFramework Include="NukeUI">
      <SourcePackage>NukeUI.Swift.iOS</SourcePackage>
    </SwiftBindingFramework>
  </ItemGroup>

  <!-- NativeReference injection (Layer 2) -->
  <Target Name="_ResolveNukeUINativeReferences" BeforeTargets="ResolveNativeReferences">
    <ItemGroup>
      <NativeReference Include="$(MSBuildThisFileDirectory)..\..\runtimes\ios-arm64\native\NukeUI.xcframework">
        <Kind>Framework</Kind>
        <SmartLink>True</SmartLink>
      </NativeReference>
    </ItemGroup>
  </Target>

  <!-- Layer 3: Validate that required companion frameworks are registered.
       Uses exact item identity match — NOT substring. "NukeUI" does not match "Nuke". -->
  <Target Name="_ValidateNukeUIDependencies"
          BeforeTargets="Build"
          Condition="'@(SwiftBindingFramework)' != ''">
    <!--
      Count items where Identity exactly equals 'Nuke'.
      SwiftBindingFramework items use bare framework names as Identity (Include="Nuke"),
      so this is an exact match. If Nuke.Swift.iOS is not installed, its .targets never
      runs, the <SwiftBindingFramework Include="Nuke"> item is never created, and
      the count is 0.
    -->
    <ItemGroup>
      <_NukeUIMatchedFramework Include="@(SwiftBindingFramework)" Condition="'%(Identity)' == 'Nuke'" />
    </ItemGroup>
    <Error
      Condition="@(_NukeUIMatchedFramework->Count()) == 0"
      Text="NukeUI.Swift.iOS requires the Nuke.Swift.iOS package. The Nuke native framework was not found. Add: &lt;PackageReference Include=&quot;Nuke.Swift.iOS&quot; /&gt;"
      Code="SWIFTBIND001" />
  </Target>
</Project>
```

**How it works**: Each package's `.targets` file registers a `SwiftBindingFramework` item with the bare framework name as the `Identity` (e.g., `Include="Nuke"`, `Include="NukeUI"`). The validation target filters `@(SwiftBindingFramework)` with `Condition="'%(Identity)' == 'Nuke'"` — an **exact string match**, not a substring check. This means `NukeUI` does not match `Nuke`, and the validation correctly fires when the Nuke package is missing. Since `.targets` from missing packages are never imported, their `SwiftBindingFramework` registrations are absent, and the `<Error>` fires.

**Result**: If the NuGet dependency is somehow bypassed (manual `.nupkg` install, version conflict, etc.), the build fails with a clear, actionable error:

```
error SWIFTBIND001: NukeUI.Swift.iOS requires the Nuke.Swift.iOS package.
The Nuke native framework was not found.
Add: <PackageReference Include="Nuke.Swift.iOS" />
```

#### Layer 4: Assembly Reference Validation (Free via C# Compiler)

If `NukeUI.Swift.iOS.dll` references types defined in `Nuke.Swift.iOS.dll`, the C# compiler will emit `CS0012` if the reference assembly is missing. This is automatic and requires no additional work — it's a natural consequence of the binding DLL having the correct assembly references.

### Summary

| Layer | Mechanism | Catches | Failure Mode |
|-------|-----------|---------|--------------|
| 1. NuGet deps | `PackageReference` → `.nuspec` | Missing package | `dotnet restore` error (NU1101) |
| 2. `.targets` injection | `buildTransitive/` NativeReference | Missing xcframework embed | Framework not in app bundle |
| 3. Build validation | `<Error>` MSBuild task | Bypassed NuGet, version conflicts | Build error (SWIFTBIND001) |
| 4. Assembly refs | C# compiler type resolution | Missing companion DLL | Build error (CS0012) |

With all four layers, **a missing framework dependency is caught at build time**. The `dyld: Library not loaded` crash from a missing companion framework should never reach the user for properly packaged bindings.

**Known limitations of this approach**: These layers validate framework presence, not completeness. Some vendor SDKs also require non-framework assets (resource bundles, privacy manifests, linker flags like `-ObjC` or `-lz`) that aren't covered by NativeReference validation. Phase 3C (MSBuild SDK) should add a `<SwiftFrameworkAsset>` item type and a corresponding validation layer for resource bundles and linker flags. Until then, packages that require extra assets must document them in their NuGet description and README.

---

## Automatic iOS Version Detection

> **Status**: Info.plist extraction is implemented (Step 4 in `dx-msbuild-sdk-design.md`).
> The Mach-O fallback chain below is documented for future edge-case support.

### Implemented: Info.plist Extraction

The generator extracts `MinimumOSVersion` from the inner framework's `Info.plist` via `PlistReader` (handles both binary and XML plists). The value is clamped to `max(raw, 15.0)` for the .NET 10 iOS floor and emitted in `binding-metadata.props`. The SDK's `_ImportSwiftBindingMetadata` target reads it via `XmlPeek` and sets `SupportedOSPlatformVersion`. Consumer `.targets` emit `SWIFTBIND010` if the consumer's version is too low.

### Future: Mach-O Fallback Chain

For edge cases where `MinimumOSVersion` is missing from the plist (e.g., self-built xcframeworks with minimal config), the deployment target can be extracted from the Mach-O binary directly. This is not yet implemented.

The minimum deployment target is available in three locations within an xcframework, in order of preference:

#### 1. Inner Framework Info.plist — `MinimumOSVersion` key

**Location**: `<Name>.xcframework/ios-arm64/<Name>.framework/Info.plist`

Most reliable for prebuilt third-party frameworks. The plist is usually in binary format, requiring `plutil -convert xml1` on macOS.

**Verified values from real frameworks:**

| Framework | MinimumOSVersion |
|-----------|-----------------|
| Nuke | 13.0 |
| NukeUI | 13.0 |
| BlinkID | 15.0 |
| BlinkIDUX | 16.0 |
| Lottie | 13.0 |

**Caveat**: Self-built xcframeworks (e.g., via `xcodebuild -create-xcframework` with minimal config) may omit this key entirely.

#### 2. `.swiftinterface` Target Triple

**Location**: `<Name>.xcframework/ios-arm64/<Name>.framework/Modules/<Name>.swiftmodule/*.swiftinterface`

The header contains `-target arm64-apple-ios<version>`:
```
// swift-module-flags: -target arm64-apple-ios16.0 ...
```

Available for all Swift frameworks (those with library evolution enabled). Not applicable to pure Objective-C frameworks.

#### 3. Mach-O Load Commands (Ultimate Fallback)

**Location**: The actual binary inside the framework bundle.

Modern frameworks (built with Xcode 11+ / iOS 13+ deployment target) use `LC_BUILD_VERSION`:

```bash
$ vtool -show BlinkIDUX.xcframework/ios-arm64/BlinkIDUX.framework/BlinkIDUX
Build Version:
  platform    IOS
  minos       16.0
  sdk         26.0
```

Can be extracted via `otool -l` or by parsing the Mach-O binary directly in C#. The `minos` field in `LC_BUILD_VERSION` (load command `0x32`) encodes the version as `(major << 16) | (minor << 8) | patch`.

**Legacy fallback**: Older binaries (pre-Xcode 11, or targeting iOS < 12) may use `LC_VERSION_MIN_IPHONEOS` (command `0x25`) instead of `LC_BUILD_VERSION`. The extraction code must check for both:

| Load Command | Cmd ID | Used By | Version Field |
|---|---|---|---|
| `LC_BUILD_VERSION` | `0x32` | Xcode 11+ (iOS 13+ deployment target) | `minos` |
| `LC_VERSION_MIN_IPHONEOS` | `0x25` | Older toolchains | `version` |
| `LC_VERSION_MIN_MACOSX` | `0x24` | macOS binaries (older toolchains) | `version` |

In practice, any framework targeting iOS 13+ will have `LC_BUILD_VERSION`, and our .NET 10 floor is iOS 15.0. But the parser should handle both to avoid hard failures on edge-case binaries (e.g., a static library compiled with an older toolchain and repackaged into an xcframework).

### Multi-Framework Version Resolution

When multiple frameworks are composed into one app, the **effective minimum is the highest minimum across all frameworks**:

```
Nuke:       iOS 13.0  ─┐
NukeUI:     iOS 13.0  ─┼─→ App minimum: iOS 15.0 (clamped by .NET runtime)
.NET 10:    iOS 15.0  ─┘

BlinkID:    iOS 15.0  ─┐
BlinkIDUX:  iOS 16.0  ─┼─→ App minimum: iOS 16.0 (driven by BlinkIDUX)
.NET 10:    iOS 15.0  ─┘
```

The MSBuild validation target can warn when a package raises the app's minimum:

```
warning SWIFTBIND010: BlinkIDUX.Swift.iOS requires iOS 16.0, which is higher than
your project's SupportedOSPlatformVersion (15.0). Your app will not run on
iOS 15.x devices. Update SupportedOSPlatformVersion to 16.0 or remove this package.
```

---

## NuGet Package Structure

> **Note**: Single-framework packaging is implemented (see Step 4-5 in `dx-msbuild-sdk-design.md`).
> The layouts below document the multi-framework and SwiftUI bridge scenarios for DX-3.

### Dependent Package Layout (NukeUI) — DX-3

```
NukeUI.Swift.iOS.12.8.0.nupkg/
├── NukeUI.Swift.iOS.nuspec
│   └── <dependencies>
│       └── <dependency id="Nuke.Swift.iOS" version="[12.0.0,13.0.0)" />
│       └── <dependency id="Swift.Runtime" version="[0.1.0-preview.1,)" />
├── lib/
│   └── net10.0-ios/
│       └── NukeUI.Swift.iOS.dll
├── buildTransitive/
│   └── net10.0-ios/
│       └── NukeUI.Swift.iOS.targets   # Includes Layer 3 validation
└── runtimes/
    └── ios-arm64/
        └── native/
            └── NukeUI.xcframework/
```

### SwiftUI Bridge Package (Optional Add-On)

For libraries with SwiftUI views, the generated bridge must be packaged as an **xcframework** (not a bare `.framework`), because NuGet packages need both device and simulator slices for development workflows. A bare `.framework` is single-architecture and will fail on whichever platform it wasn't built for.

The bridge build pipeline should produce:
```bash
# Build for device and simulator, then combine
xcodebuild -scheme NukeUIBridge -destination 'generic/platform=iOS' -archivePath device archive
xcodebuild -scheme NukeUIBridge -destination 'generic/platform=iOS Simulator' -archivePath sim archive
xcodebuild -create-xcframework \
  -framework device.xcarchive/.../NukeUIBridge.framework \
  -framework sim.xcarchive/.../NukeUIBridge.framework \
  -output NukeUIBridge.xcframework
```

**Option A**: Bundle in the same package (simpler, recommended for tightly coupled bridges):
```
NukeUI.Swift.iOS.12.8.0.nupkg/
└── runtimes/
    └── ios-arm64/
        └── native/
            ├── NukeUI.xcframework/           # Each xcframework in its own subdirectory
            └── NukeUIBridge.xcframework/      # Generated SwiftUI bridge (device + simulator)
```

> **Why this doesn't trigger `NETSDK1152`**: The `Info.plist` collision issue (mentioned in [Why Not a Single Monolithic Package?](#why-not-a-single-monolithic-package)) occurs when NuGet flattens loose framework files into a single directory during pack — multiple `Info.plist` files collide. Here, each xcframework is in its **own named subdirectory** (`NukeUI.xcframework/` and `NukeUIBridge.xcframework/`), so their internal files don't conflict. The monolithic-package concern applies to bundling unrelated libraries (Nuke + NukeUI + NukeExtensions) where it creates bloat and version coupling — not to bundling a library with its own tightly-coupled bridge.

**Option B**: Separate package (if the bridge is optional or large):
```
NukeUI.Swift.iOS.Bridge.12.8.0.nupkg/
└── depends on: NukeUI.Swift.iOS [12.0.0, 13.0.0)
```

---

## Multi-Framework Dependency Detection (DX-3 — Future)

> This section is the design blueprint for automatic dependency detection between
> multi-framework libraries (e.g., Nuke + NukeUI). Not yet implemented.

The SDK can detect dependencies through **two complementary methods**:

**1. Binary linkage analysis** (from the Mach-O binary — authoritative source):
- Inspects `LC_LOAD_DYLIB` / `LC_LOAD_WEAK_DYLIB` load commands in the framework binary
- Catches dependencies that don't surface in public API signatures
- Extraction: `otool -L <binary>` lists all linked dylibs

```bash
$ otool -L NukeUI.xcframework/ios-arm64/NukeUI.framework/NukeUI
  @rpath/Nuke.framework/Nuke (compatibility version 0.0.0)
  /usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
  ...
```

Only `@rpath` entries indicate companion framework dependencies — system dylibs (`/usr/lib/*`) are filtered out.

**2. Type-level analysis** (during binding generation):
- Cross-framework type references in the ABI (e.g., NukeUI methods that accept Nuke types)
- Generates correct `using` directives for cross-framework types
- Provides detail about *which* types are referenced

Both methods feed into a `dependency-manifest.json` that drives Layer 3 validation target generation and NuGet dependency declarations.

### Dependent Package Versioning

For multi-framework libraries, dependent packages use **semver ranges matching the major version** of the base package:

```
Nuke.Swift.iOS         version 12.8.0
NukeUI.Swift.iOS       version 12.8.0, depends on Nuke.Swift.iOS [12.0.0, 13.0.0)
```

This allows independent patch releases while preventing ABI-breaking mismatches across major versions.

---

## Platform Coverage

### Supported Platforms

This packaging design targets all Apple platforms that .NET supports and where Swift frameworks are distributed:

| Platform | TFM | Mach-O Platform ID | Status |
|----------|-----|-------------------|--------|
| **iOS** | `net10.0-ios` | `IOS` (2) / `IOSSIMULATOR` (7) | Primary target |
| **Mac Catalyst** | `net10.0-maccatalyst` | `MACCATALYST` (6) | Supported (Phase 3A) |
| **macOS** | `net10.0-macos` | `MACOS` (1) | Under investigation |
| **tvOS** | `net10.0-tvos` | `TVOS` (3) / `TVOSSIMULATOR` (8) | Under investigation |

### xcframework Platform Slices

An xcframework can contain slices for multiple platforms. The generator and packaging tools must handle multi-platform xcframeworks correctly:

```
Nuke.xcframework/
  Info.plist                              ← Lists available slices
  ios-arm64/Nuke.framework/               ← iOS device
  ios-arm64_x86_64-simulator/Nuke.framework/  ← iOS simulator
  macos-arm64_x86_64/Nuke.framework/     ← macOS (if provided)
```

Each slice can have a **different minimum platform version**. The `binding-metadata.json` records per-platform values:

```json
{
  "framework": "Nuke",
  "minimumPlatformVersions": {
    "ios": "13.0",
    "ios-simulator": "13.0",
    "maccatalyst": "13.1",
    "macos": "10.15"
  }
}
```

### Multi-Platform NuGet Packages

For libraries that support multiple Apple platforms, the NuGet package can include multiple TFM targets:

```
Nuke.Swift.nupkg/
├── lib/
│   ├── net10.0-ios/
│   │   └── Nuke.Swift.dll              # iOS bindings
│   └── net10.0-maccatalyst/
│       └── Nuke.Swift.dll              # Mac Catalyst bindings (may be same DLL)
├── buildTransitive/
│   ├── net10.0-ios/
│   │   └── Nuke.Swift.targets
│   └── net10.0-maccatalyst/
│       └── Nuke.Swift.targets
└── runtimes/
    └── ios-arm64/
        └── native/
            └── Nuke.xcframework/       # Full xcframework with all slices
```

Note the package name drops the `.iOS` suffix when it supports multiple platforms — `Nuke.Swift` instead of `Nuke.Swift.iOS`. Platform-specific packages (iOS-only) keep the platform suffix.

### Mac Catalyst Considerations

Mac Catalyst apps use iOS frameworks built for macOS. Key differences from iOS:

- Catalyst binaries use Mach-O platform ID `6` (`MACCATALYST`), not `2` (`IOS`)
- The minimum version numbering follows macOS conventions (e.g., `13.1` not `16.0`)
- xcframeworks may have a dedicated `maccatalyst-arm64_x86_64` slice, or the iOS slice may work with Catalyst compatibility mode
- The `.targets` file must resolve the correct xcframework slice based on the consuming project's TFM

---

## Resolved Decisions

1. **Version alignment** — **Match upstream version.** NuGet package version defaults to the upstream Swift library version, auto-extracted from `CFBundleShortVersionString`. Implemented in Step 4. See `dx-msbuild-sdk-design.md`.

2. **Dependency versioning strategy** — **Semver ranges with matching major versions.** Dependent packages use `[major.0.0, (major+1).0.0)` ranges. Design for DX-3.

3. **Simulator slices** — **Include them.** xcframeworks bundle both device and simulator slices. `SwiftWrapperArchitectures=all` is the SDK default.

4. **Swift runtime libraries** — **No action needed.** Swift stdlib ships with iOS 12.2+. Our .NET 10 floor is iOS 15.0.

5. **Swift.Runtime packaging** — **Separate NuGet, independent versioning.** `Swift.Runtime` 0.1.0-preview.1, SDK injects `PackageReference` via `Sdk.props`. Implemented in Step 3.

6. **Consumer targets placement** — **`buildTransitive/` only** (not duplicated in `build/`). NuGet 5.0+ covers both direct and transitive consumers. Idempotency guard as defense-in-depth.

7. **Architecture slices** — **User-controlled via `<SwiftWrapperArchitectures>`**, defaults to `all`. Pack-time validation (`SWIFTBIND030`) enforces both slices for NuGet packaging.

---

## Open Questions

1. **Package naming convention**: `{Library}.Swift.iOS` vs `{Library}.Swift` (multi-platform) vs `Swift.{Library}`? The `{Library}.Swift.iOS` pattern follows the `AdamE.Firebase.iOS.*` precedent. Multi-platform packages could drop the platform suffix.

2. **SwiftUI bridge packaging**: Bundle bridge xcframework in the main package (Option A above), or separate `*.Bridge` package? Depends on whether the bridge adds significant size.

3. **Source-module / overlay packaging**: For extension libraries like NukeExtensions that may be distributed as source (SPM target) rather than a prebuilt binary, do we need a source-compilation path? Or require all inputs to be prebuilt xcframeworks? (v2 SPM support in `dx-msbuild-sdk-design.md` addresses this partially.)

4. **Resource bundles and linker flags**: Some vendor SDKs require non-framework assets (resource bundles, privacy manifests, linker flags like `-ObjC`). A `<SwiftFrameworkAsset>` item type and corresponding validation layer is needed for DX-3.
