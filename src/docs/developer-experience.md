# Developer Experience: Multi-Framework Packaging & NuGet Distribution

> Phase 3 design document for the Swift Bindings NuGet packaging system.
> See also: `/north-star.md` for the overall project vision.

## Table of Contents

- [Problem Statement](#problem-statement)
- [Apple Framework Rules](#apple-framework-rules)
- [Package Architecture](#package-architecture)
- [Dependency Enforcement](#dependency-enforcement)
- [Automatic iOS Version Detection](#automatic-ios-version-detection)
- [Automatic Library Version Extraction](#automatic-library-version-extraction)
- [NuGet Package Structure](#nuget-package-structure)
- [MSBuild SDK Vision](#msbuild-sdk-vision)
- [Platform Coverage](#platform-coverage)
- [Implementation Roadmap](#implementation-roadmap)

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

Each package ships a `.targets` file in both `build/` and `buildTransitive/` directories. This injects the xcframework as a `NativeReference` into the consuming project at build time.

```xml
<!-- NukeUI.Swift.iOS.targets (shipped in both build/ and buildTransitive/) -->
<Project>
  <Target Name="_ResolveNukeUINativeReferences" BeforeTargets="ResolveNativeReferences">
    <ItemGroup>
      <NativeReference Include="$(MSBuildThisFileDirectory)..\..\runtimes\ios-arm64\native\NukeUI.xcframework">
        <Kind>Framework</Kind>
        <SmartLink>True</SmartLink>
      </NativeReference>
    </ItemGroup>
  </Target>
</Project>
```

The `buildTransitive/` folder (NuGet 5.0+) ensures the `.targets` file is imported even by projects that consume the package transitively — not just direct consumers.

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

### Problem

Each xcframework has a minimum iOS deployment target baked into it. Today, developers must manually discover this and set `SupportedOSPlatformVersion` in their `.csproj`. If they get it wrong, the app either:
- Fails to build (version too high for the framework)
- Crashes on older devices (version too low, framework uses unavailable APIs)

### Solution: Extract from xcframework at generation time

The generator will automatically extract the minimum iOS version from the xcframework and embed it in the generated bindings and NuGet package metadata.

### Extraction Sources (Fallback Chain)

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

### Implementation

The generator will:

1. **At generation time**: Extract the minimum iOS version using the fallback chain above
2. **Emit metadata**: Include the version in `binding-report.json` and a new `binding-metadata.json`
3. **Apply to generated `.csproj`**: Set `SupportedOSPlatformVersion` in the binding project's `.csproj`. This is a **build-time project property**, not a `.nuspec` field — NuGet itself has no concept of platform version constraints. The enforcement happens at build time via the .NET SDK's platform compatibility analyzer (`CA1416`) and the iOS build toolchain.
4. **Propagate via `.targets`**: The package's `.targets` file emits an MSBuild warning if the consuming project's `SupportedOSPlatformVersion` is lower than the framework's minimum (see [Multi-Framework Version Resolution](#multi-framework-version-resolution) below).
5. **Enforce floor**: Apply `max(framework_min, dotnet_runtime_min)` — .NET 10 on iOS requires iOS 15.0 minimum, so even if a framework supports iOS 13.0, the effective minimum is 15.0

```json
// binding-metadata.json (generated alongside bindings)
// See "Automatic Library Version Extraction" for full schema with version fields
{
  "framework": "NukeUI",
  "libraryVersion": "12.8.0",
  "libraryVersionSource": "CFBundleShortVersionString",
  "libraryVersionConfidence": "high",
  "minimumPlatformVersions": { "ios": "13.0" },
  "effectiveMinimumOSVersion": "15.0",
  "sdkVersion": "17.5",
  "platforms": ["ios-arm64", "ios-arm64_x86_64-simulator"]
}
```

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

## Automatic Library Version Extraction

### Decision: NuGet Package Version Matches Upstream Library Version

The NuGet package version defaults to the upstream Swift library's version. This makes it immediately clear to consumers which native version they're getting — `Nuke.Swift.iOS 12.8.0` wraps Nuke 12.8.0. Developers can override this in the generated `.csproj` if they need independent versioning, but the auto-detected value is the sensible default.

### Where the Version Lives in xcframeworks

The upstream library version is stored in `CFBundleShortVersionString` in the inner framework's Info.plist. This is Apple's standard "marketing version" field.

**Verified extraction from real frameworks:**

| Framework | CFBundleShortVersionString | Actual Upstream Version | Match? |
|-----------|---------------------------|------------------------|--------|
| Nuke | `12.8.0` | 12.8.0 | Yes |
| NukeUI | `12.8.0` | 12.8.0 | Yes |
| Lottie | `4.6.0` | 4.6.0 | Yes |
| BlinkID | `1.0` | 7.6.2 | **No** (placeholder) |
| BlinkIDUX | `1.0` | 7.6.2 | **No** (placeholder) |

**Other version sources investigated:**

| Source | Reliability | Notes |
|--------|-------------|-------|
| `CFBundleShortVersionString` | Best available | Standard Apple convention; most well-maintained libraries populate correctly |
| `CFBundleVersion` | Poor | Often `1` (placeholder) or encoded (Lottie: `460` for 4.6.0) |
| Mach-O `LC_ID_DYLIB` current_version | Poor | `0.0.0` or `1.0.0` for all tested frameworks; Swift/SPM builds rarely set this |
| `.swiftinterface` headers | None | Contains Swift compiler version, not library version |
| Top-level xcframework Info.plist | None | Only has `XCFrameworkFormatVersion: 1.0` |

### Extraction Strategy

```
1. Read CFBundleShortVersionString from inner Info.plist
   └── If valid semver (not "1.0" or "1.0.0" placeholder) → use it
       └── If placeholder or missing → emit warning, default to "0.0.0"
```

**Placeholder detection heuristic**: `CFBundleShortVersionString` values of exactly `"1.0"` or `"1.0.0"` are treated as likely Xcode defaults (not real versions) and trigger the warning path. This heuristic can produce false positives for libraries genuinely at version 1.0 — in that case, the developer confirms the version and it's stored in an override.

When the version cannot be auto-detected:

```
warning SWIFTBIND020: Could not determine upstream version for BlinkID.xcframework.
CFBundleShortVersionString is "1.0" (likely an Xcode default).
Package version defaulting to 0.0.0. Set <PackageVersion> in the .csproj to override:
  <PackageVersion>7.6.2</PackageVersion>
```

### Version Override

The generated `.csproj` includes the auto-detected version with a comment explaining its source:

```xml
<PropertyGroup>
  <!-- Auto-detected from Nuke.xcframework CFBundleShortVersionString -->
  <PackageVersion>12.8.0</PackageVersion>
</PropertyGroup>
```

Or when detection fails:

```xml
<PropertyGroup>
  <!-- WARNING: Could not detect version from BlinkID.xcframework (CFBundleShortVersionString="1.0").
       Set this to the actual upstream library version. -->
  <PackageVersion>0.0.0</PackageVersion>
</PropertyGroup>
```

### Metadata Output

The `binding-metadata.json` includes the version extraction result:

```json
{
  "framework": "Nuke",
  "libraryVersion": "12.8.0",
  "libraryVersionSource": "CFBundleShortVersionString",
  "libraryVersionConfidence": "high",
  "minimumPlatformVersions": { "ios": "13.0" },
  "effectiveMinimumOSVersion": "15.0",
  "sdkVersion": "17.5",
  "platforms": ["ios-arm64", "ios-arm64_x86_64-simulator"]
}
```

For undetectable versions:

```json
{
  "framework": "BlinkID",
  "libraryVersion": "0.0.0",
  "libraryVersionSource": "CFBundleShortVersionString",
  "libraryVersionConfidence": "low",
  "libraryVersionRaw": "1.0",
  "minimumPlatformVersions": { "ios": "15.0" },
  "effectiveMinimumOSVersion": "15.0",
  "sdkVersion": "26.0",
  "platforms": ["ios-arm64", "ios-arm64_x86_64-simulator"]
}
```

### Dependent Package Versioning

For multi-framework libraries, dependent packages use **semver ranges matching the major version** of the base package:

```
Nuke.Swift.iOS         version 12.8.0
NukeUI.Swift.iOS       version 12.8.0, depends on Nuke.Swift.iOS [12.0.0, 13.0.0)
```

This allows independent patch releases (e.g., NukeUI.Swift.iOS 12.8.1 for a binding-only fix) while preventing ABI-breaking mismatches across major versions. When all frameworks come from the same upstream release, their versions start aligned. Binding-only fixes increment the patch version.

---

## NuGet Package Structure

### Single-Framework Package Layout

```
Nuke.Swift.iOS.12.8.0.nupkg/
├── Nuke.Swift.iOS.nuspec              # Package metadata + dependencies
├── lib/
│   └── net10.0-ios/
│       └── Nuke.Swift.iOS.dll         # Generated C# binding assembly
├── build/
│   └── net10.0-ios/
│       └── Nuke.Swift.iOS.targets     # NativeReference injection + validation
├── buildTransitive/
│   └── net10.0-ios/
│       └── Nuke.Swift.iOS.targets     # Same targets, for transitive consumers
└── runtimes/
    └── ios-arm64/
        └── native/
            └── Nuke.xcframework/      # The original xcframework
                ├── Info.plist
                ├── ios-arm64/
                │   └── Nuke.framework/
                └── ios-arm64_x86_64-simulator/
                    └── Nuke.framework/
```

### Dependent Package Layout (NukeUI)

```
NukeUI.Swift.iOS.12.8.0.nupkg/
├── NukeUI.Swift.iOS.nuspec
│   └── <dependencies>
│       └── <dependency id="Nuke.Swift.iOS" version="[12.0.0,13.0.0)" />
│       └── <dependency id="Swift.Runtime" version="[1.0.0,)" />
├── lib/
│   └── net10.0-ios/
│       └── NukeUI.Swift.iOS.dll
├── build/ + buildTransitive/
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

## MSBuild SDK Vision

The end goal (Phase 3 milestone) is an MSBuild SDK that automates the entire pipeline:

```xml
<Project Sdk="Swift.Bindings.Sdk/1.0.0">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
    <PackageId>Nuke.Swift.iOS</PackageId>
    <!-- SupportedOSPlatformVersion auto-detected from xcframework -->
  </PropertyGroup>

  <ItemGroup>
    <!-- Just point at xcframeworks — the SDK does the rest -->
    <SwiftFramework Include="Nuke.xcframework" />
  </ItemGroup>

  <!-- Optional: declare dependency on another Swift binding package -->
  <ItemGroup>
    <PackageReference Include="Swift.Runtime" Version="1.0.0" />
  </ItemGroup>
</Project>
```

The SDK would:
1. Extract ABI JSON from the xcframework (`swift-frontend -dump-api`)
2. Run the binding generator
3. Compile the generated C# into the assembly
4. Extract minimum iOS version and set `SupportedOSPlatformVersion`
5. Generate the `.targets` file with NativeReference injection and validation
6. Package everything into a `.nupkg` with correct structure

### Multi-Framework Project

For libraries with multiple dependent frameworks:

```xml
<!-- Nuke.Swift.iOS.csproj -->
<ItemGroup>
  <SwiftFramework Include="Nuke.xcframework" />
</ItemGroup>

<!-- NukeUI.Swift.iOS.csproj -->
<ItemGroup>
  <SwiftFramework Include="NukeUI.xcframework" />
  <PackageReference Include="Nuke.Swift.iOS" Version="12.8.0" />
</ItemGroup>
```

The SDK detects dependencies through **two complementary methods**:

**1. Type-level analysis** (during binding generation):
- Cross-framework type references in the ABI (e.g., NukeUI methods that accept Nuke types)
- Generates correct `using` directives for cross-framework types
- Emits the Layer 3 validation target automatically

**2. Binary linkage analysis** (from the Mach-O binary):
- Inspects `LC_LOAD_DYLIB` / `LC_LOAD_WEAK_DYLIB` load commands in the framework binary
- These record the dylib install names the framework links against at the binary level
- Catches dependencies that don't surface in public API signatures (e.g., internal use of a companion framework, or Objective-C runtime dependencies)
- Extraction: `otool -L <binary>` lists all linked dylibs

```bash
$ otool -L NukeUI.xcframework/ios-arm64/NukeUI.framework/NukeUI
  @rpath/Nuke.framework/Nuke (compatibility version 0.0.0)
  /usr/lib/libobjc.A.dylib (compatibility version 1.0.0)
  /usr/lib/libSystem.B.dylib (compatibility version 1.0.0)
  ...
```

The `@rpath/Nuke.framework/Nuke` entry tells us NukeUI has a binary dependency on Nuke, even if no public NukeUI API exposes a Nuke type. System dylibs (`/usr/lib/*`, `/System/Library/*`) are filtered out — only `@rpath` entries indicate companion framework dependencies.

Both analysis methods feed into the `dependency-manifest.json`. The binary linkage check is the authoritative source; type-level analysis provides additional detail about *which* types are referenced.

---

## Implementation Roadmap

### Phase 3A: Generator Metadata Emission (Foundation)

**Goal**: The generator emits all metadata needed for packaging, without building the full MSBuild SDK yet.

1. **Platform version extraction** — Add `--extract-metadata` flag to the generator CLI
   - Implement fallback chain: Info.plist → .swiftinterface → Mach-O (`LC_BUILD_VERSION` + `LC_VERSION_MIN_*`)
   - Output `binding-metadata.json` alongside `binding-report.json`
   - Extract per-platform (iOS, MacCatalyst) minimum versions from xcframework slices
2. **Dependency manifest** — When generating multiple frameworks, emit a `dependency-manifest.json`:
   ```json
   {
     "frameworks": [
       {
         "name": "Nuke",
         "minimumPlatformVersions": { "ios": "13.0", "maccatalyst": "13.1" },
         "providedTypes": ["Nuke.ImagePipeline", "Nuke.ImageRequest", ...],
         "binaryDependencies": [],
         "requiredFrameworks": []
       },
       {
         "name": "NukeUI",
         "minimumPlatformVersions": { "ios": "13.0", "maccatalyst": "13.1" },
         "providedTypes": ["NukeUI.LazyImage", ...],
         "binaryDependencies": ["Nuke"],
         "requiredFrameworks": ["Nuke"],
         "requiredTypes": ["Nuke.ImagePipeline", ...]
       }
     ]
   }
   ```
   The `binaryDependencies` field is populated from `LC_LOAD_DYLIB` analysis; `requiredFrameworks` combines binary + type-level analysis.
3. **`.targets` template generation** — Generator emits a `.targets` file per framework with NativeReference injection and Layer 3 validation pre-configured

### Phase 3B: NuGet Packaging Scripts

**Goal**: Shell scripts that take generator output and produce correct `.nupkg` files.

1. **`pack-binding.sh`** — Takes a framework's generated output and produces a `.nupkg`
   - Creates correct directory structure (lib/, build/, buildTransitive/, runtimes/)
   - Injects dependency declarations from `dependency-manifest.json`
   - Sets `SupportedOSPlatformVersion` from `binding-metadata.json`
2. **`pack-all.sh`** — Processes multiple frameworks in dependency order
   - Topological sort from `dependency-manifest.json`
   - Builds packages bottom-up (Nuke before NukeUI)
3. **Validation** — Install generated packages into a test project and verify:
   - `dotnet restore` pulls transitive dependencies
   - Removing a dependency causes build error (not runtime crash)
   - `SupportedOSPlatformVersion` is correctly applied

### Phase 3C: MSBuild SDK

**Goal**: Full `Swift.Bindings.Sdk` that automates the pipeline end-to-end.

1. **SDK package structure** — MSBuild SDK distributed as NuGet package
2. **`SwiftFramework` item type** — MSBuild target that triggers ABI extraction and generation
3. **Automatic dependency detection** — Binary linkage (`otool -L`) + type-level analysis combined
4. **`dotnet pack` integration** — Correct `.nupkg` structure from `dotnet pack`
5. **IDE integration** — IntelliSense, build errors surfaced in VS/Rider
6. **Resource bundle / linker flag handling** — `<SwiftFrameworkAsset>` item type for resource bundles, privacy manifests, and linker flags (`-ObjC`, `-lz`, etc.) that some vendor SDKs require alongside their frameworks

### Phase 3D: Project Templates

**Goal**: `dotnet new` templates for creating binding projects.

```bash
dotnet new swift-binding -n Nuke.Swift.iOS
# Creates:
#   Nuke.Swift.iOS/
#   ├── Nuke.Swift.iOS.csproj  (with Swift.Bindings.Sdk reference)
#   └── README.md              (instructions to add xcframework)
```

---

## Pragmatic Implementation Sequence

> Added February 2026. The 3A → 3B → 3C → 3D roadmap above is the right *logical* decomposition, but for getting external users consuming bindings, the work should be reordered by what unblocks real usage first.

### Current State Assessment

- **Binding generation works** but requires manual orchestration (5+ shell scripts per framework: `regenerate-bindings.sh`, `build-swift-wrapper.sh`, `build-testapp.sh`, etc.)
- **No NuGet packaging pipeline exists** — only `generate.sh`'s `CreateProject()` produces a basic `.csproj` for Apple framework bindings
- **No metadata extraction** (iOS version, library version) is implemented in the generator
- **No `.targets` file generation** — the 4-layer dependency enforcement is entirely on paper
- **Swift.Runtime is `IsPackable=false`** — external users have no way to get the runtime support library
- **The main roadmap (roadmap.md) now prioritizes DX work first**, interleaved with test hardening and library validation

### DX-1: "Hello World" External Consumption (smallest useful increment)

**Goal**: An external user can take a generated binding + xcframework and use it in their .NET iOS app.

**Prerequisite**: None. Can start immediately.

1. **Package Swift.Runtime as a NuGet** — currently `IsPackable=false` in Swift.Runtime.csproj. External users need this as a dependency. Flip the flag, add package metadata (id, description, license), publish.
2. **Generator emits a compilable `.csproj`** — today the generator outputs loose `.cs` files + a `Swift/` runtime copy. Instead, emit a ready-to-use binding project that references `Swift.Runtime` via PackageReference and compiles the generated `.cs` files. This replaces the manual project setup users currently have to do.
3. **Document the manual workflow** — a clear "Getting Started" guide that walks through: obtain xcframework → extract ABI JSON (`swift-frontend -compile-module-from-interface`) → run generator → build Swift wrapper (`xcrun swiftc`) → reference in app. Codify what `build-all.sh` does into a reproducible guide for someone who doesn't have the repo.

**Success criteria**: Someone outside the project can follow the guide, generate bindings for Nuke, and call `ImagePipeline.shared` from a .NET iOS app.

### DX-2: NuGet Packaging (automate distribution)

**Goal**: `dotnet pack` on the generated project produces a correct `.nupkg`.

**Prerequisite**: DX-1 (compilable generated project exists).

1. **`.targets` file generation** from the generator — emit `build/` and `buildTransitive/` targets with NativeReference injection (Layer 2) and `SwiftBindingFramework` validation (Layer 3)
2. **iOS version extraction** — implement the fallback chain: Info.plist `MinimumOSVersion` → `.swiftinterface` target triple → Mach-O `LC_BUILD_VERSION`/`LC_VERSION_MIN_*`
3. **Library version extraction** — `CFBundleShortVersionString` with placeholder detection heuristic
4. **`binding-metadata.json` emission** — alongside `binding-report.json`
5. **Pack script** (`pack-binding.sh`) — arranges the correct NuGet directory structure (lib/, build/, buildTransitive/, runtimes/) and runs `dotnet pack`

**Success criteria**: `./pack-binding.sh` produces a `.nupkg` that a consumer can install and get working NativeReference injection automatically.

### DX-3: Multi-Framework Dependencies

**Goal**: Libraries like Nuke (Nuke + NukeUI + NukeExtensions) package correctly with dependency tracking.

**Prerequisite**: DX-2 (single-framework packaging works).

1. **Dependency manifest generation** — `dependency-manifest.json` from binary linkage (`otool -L` / `LC_LOAD_DYLIB`) + type-level cross-reference analysis
2. **`SwiftBindingFramework` MSBuild item** — cross-package registration and validation (Layer 3 in the design)
3. **`pack-all.sh`** — topological sort from dependency manifest, builds packages bottom-up
4. **End-to-end validation** — install generated packages in a clean project, verify all 4 enforcement layers work (NuGet restore fails without deps, build error with `SWIFTBIND001`, `CS0012` on missing assembly)

**Success criteria**: Install `NukeUI.Swift.iOS` without `Nuke.Swift.iOS` → clear build error. Install both → app runs.

### DX-4: MSBuild SDK + Templates (the full vision)

**Goal**: `dotnet new swift-binding` + `dotnet build` = NuGet package.

**Prerequisite**: DX-1 through DX-3 validated with real users.

This maps to Phase 3C + 3D from the original roadmap. Only pursue once the script-based workflow is proven and user feedback confirms the automation is worth the MSBuild SDK complexity.

### Relationship to Roadmap Phases B and C

Phases B (enable TestFramework features) and C (new library validation) are about **generator completeness** — making the bindings cover more Swift patterns. The DX phases are about **consumability** — making existing bindings usable by people outside the project. These are largely independent tracks:

- **DX-1 can start immediately** — it doesn't require more features, just packaging of what already works
- **Phase B improves confidence** — more test coverage means fewer surprises for external users
- **Phase C validates generalization** — trying new libraries finds patterns the generator misses
- **DX-2 and DX-3 benefit from Phase C** — multi-framework packaging is easier to test with real multi-framework libraries

Adopted interleaving (see `roadmap.md`): **DX-1 → TH (test hardening) → DX-2 → Phase J (new library) → DX-3**. External users can start experimenting (DX-1) while test gates harden (TH), then packaging automation (DX-2) lands before new library validation (J) exercises the workflow end-to-end.

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
├── build/
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

These were previously open questions that have been resolved:

1. **Version alignment** — **Resolved: Match upstream version.** NuGet package version defaults to the upstream Swift library version, auto-extracted from `CFBundleShortVersionString`. Developers can override in the `.csproj` if needed. See [Automatic Library Version Extraction](#automatic-library-version-extraction).

2. **Dependency versioning strategy** — **Resolved: Semver ranges with matching major versions.** Dependent packages use `[major.0.0, (major+1).0.0)` ranges (e.g., NukeUI.Swift.iOS depends on `Nuke.Swift.iOS [12.0.0, 13.0.0)`). This allows independent binding-only patch releases while preventing ABI-breaking mismatches.

3. **Simulator slices** — **Resolved: Include them.** The developer experience improvement (build without a physical device) outweighs the size cost. xcframeworks bundle both device and simulator slices by convention.

4. **Swift runtime libraries** — **Resolved: No action needed.** The Swift standard library ships with iOS 12.2+. Our .NET 10 floor is iOS 15.0, so Swift runtime availability is guaranteed on all supported platforms.

---

## Open Questions

1. **Package naming convention**: `{Library}.Swift.iOS` vs `{Library}.Swift` (multi-platform) vs `Swift.{Library}`? The `{Library}.Swift.iOS` pattern follows the `AdamE.Firebase.iOS.*` precedent. Multi-platform packages could drop the platform suffix.

2. **SwiftUI bridge packaging**: Bundle bridge xcframework in the main package, or separate `*.Bridge` package? Depends on whether the bridge adds significant size.

3. **Source-module / overlay packaging**: For extension libraries like NukeExtensions that may be distributed as source (SPM target) rather than a prebuilt binary, do we need a source-compilation path in the packaging pipeline? Or do we require all inputs to be prebuilt xcframeworks?

4. **Swift.Runtime packaging strategy**: Should Swift.Runtime be a separate NuGet package (as the architecture assumes with `Swift.Runtime` at the bottom of the dependency graph) or bundled into each binding package? Separate is cleaner and avoids duplication, but adds a dependency for users to manage. If separate, what's the versioning strategy — does it version independently from bindings, or lock-step?

5. **Target audience for DX-1**: Are we targeting binding *authors* (someone who builds their own bindings from an xcframework) or binding *consumers* (someone who installs a pre-made NuGet)? The DX phases assume authors first, since there's no binding marketplace yet. But if the immediate goal is to publish a few "reference" packages (Nuke, StoreKit) for consumers, the priorities shift toward packaging quality over workflow documentation.

6. **DX work vs. generator completeness sequencing**: DX-1 can start immediately without more generator features. But should DX-2/DX-3 wait for Phases B and C (test coverage + new library validation), or proceed in parallel? Risk of doing DX too early: packaging a generator that still has coverage gaps. Risk of waiting: nobody outside the project can use the tool until everything is polished. See [Pragmatic Implementation Sequence](#pragmatic-implementation-sequence) for a proposed interleaving.
