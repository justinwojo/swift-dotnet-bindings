# DX Design: MSBuild SDK for Swift Bindings

> Design document for the end-to-end developer experience of creating and consuming Swift binding NuGet packages.
> For review by Codex and external feedback before implementation.

---

## Goal

**Enable a .NET developer on macOS to turn a dynamic Swift xcframework into a NuGet package with `dotnet build && dotnet pack`, and enable consumers to use that package with a single `PackageReference`.**

**v1 scope: dynamic Swift xcframeworks only** (frameworks containing `.framework` bundles with Mach-O dylibs and `.swiftinterface` files). Static xcframeworks (`.a` archives) are out of scope for v1 — they lack the dylib/TBD artifacts the generator requires and would need a separate extraction pipeline. All tested libraries (Nuke, BlinkID, Lottie, CryptoSwift) are dynamic.

The binding author should not need to understand Swift ABI, the generator's internals, or manual xcframework tooling. The consumer should not know Swift is involved at all — they just call a C# API.

---

## The Two Users

### Binding Author

Creates the NuGet package from a Swift xcframework. Requires macOS + Xcode (non-negotiable — you're binding Apple frameworks). May or may not know Swift.

**Their workflow:**

```bash
# 1. Create a binding project
dotnet new swift-binding -n Nuke.Swift.iOS

# 2. Drop the xcframework into the project directory
cp -r ~/Downloads/Nuke.xcframework ./Nuke.Swift.iOS/

# 3. Build — does everything automatically
cd Nuke.Swift.iOS
dotnet build

# 4. Pack — produces a NuGet package
dotnet pack

# Output: Nuke.Swift.iOS.12.8.0.nupkg
```

**Their project file (entire contents):**

```xml
<Project Sdk="Swift.Bindings.Sdk/1.0.0">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
  </PropertyGroup>
  <!-- SwiftFramework items are auto-discovered from *.xcframework in the project directory.
       Explicit items are only needed to override defaults or when multiple xcframeworks are present. -->
</Project>
```

**xcframework discovery:** The SDK's `Sdk.targets` auto-populates `SwiftFramework` items by globbing `*.xcframework` in the project directory (discovery runs in `.targets`, not `.props`, so user-declared items in the project body are seen first). If exactly one is found, it's used automatically (zero edits after `dotnet new`). If zero are found, `dotnet build` emits a clear error: *"No xcframework found. Copy an xcframework into the project directory or add `<SwiftFramework Include="path/to/Library.xcframework" />`."* If multiple are found and none are explicitly declared, the build errors with a list of found frameworks and instructions to declare them explicitly.

For explicit control (multiple frameworks or non-standard paths):
```xml
<ItemGroup>
  <SwiftFramework Include="Nuke.xcframework" />
</ItemGroup>
```

The SDK handles:
- ABI JSON extraction from the xcframework
- Running the binding generator
- Compiling the Swift wrapper library into an xcframework
- Compiling the generated C# into a DLL
- Extracting metadata (iOS version, library version) for the NuGet package
- Generating `.targets` files for consumer-side NativeReference injection
- Arranging the correct NuGet package structure on `dotnet pack`

**Optional customization (for advanced authors):**

```xml
<SwiftFramework Include="Nuke.xcframework">
  <!-- Optional: custom namespace (default: Swift.{Module}) -->
  <NamespacePattern>Nuke</NamespacePattern>
  <!-- Optional: symbol graph for C# XML doc comments -->
  <SymbolGraph>Nuke.symbols.json</SymbolGraph>
  <!-- Optional: bridge hints for SwiftUI views -->
  <BridgeHints>bridge-hints.json</BridgeHints>
  <!-- Optional: swiftinterface for @inlinable internal detection -->
  <SwiftInterface>Nuke.swiftinterface</SwiftInterface>
</SwiftFramework>
```

### Binding Consumer

Uses the NuGet package in their .NET iOS app. Still requires macOS + Xcode (or a paired Mac) to build iOS apps — that's a .NET iOS requirement, not specific to Swift bindings. Does not interact with Swift or the Swift Bindings SDK at all.

**Their project file:**

```xml
<PackageReference Include="Nuke.Swift.iOS" Version="12.8.0" />
```

**Their C# code:**

```csharp
using Swift.Nuke;

var pipeline = ImagePipeline.Shared;
var image = await pipeline.GetImageAsync(new ImageRequest("https://example.com/photo.jpg"));
```

**What happens automatically:**
- NuGet restore pulls `Nuke.Swift.iOS` + transitive `Swift.Runtime`
- The package's `.targets` file injects `NativeReference` items for `Nuke.xcframework` and `NukeSwiftBindings.xcframework` (the module-specific wrapper library)
- The xcframeworks end up in the app's `Frameworks/` directory at build time
- If the consumer's `SupportedOSPlatformVersion` is too low for the framework, they get a clear build warning

The consumer never sees Swift, never runs the generator, never touches xcframeworks.

---

## Current State

### Generator

The generator is a .NET CLI tool (`src/Swift.Bindings/src/`) that supports two input modes:

**Mode 1: `--xcframework` (Step 1 — implemented)**
```bash
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework Nuke.xcframework -o output/
```
Takes an xcframework directory and automatically resolves all inputs:
- Parses `Info.plist` to find iOS platform slices (simulator preferred, device fallback)
- Discovers the Swift module from `Modules/*.swiftmodule`
- Locates the dylib via `BinaryPath` from plist, verifies it's dynamic via `file` command
- Auto-discovers `.swiftinterface` (excludes `.private.swiftinterface`)
- Finds `.abi.json` in swiftmodule dir, or generates via `swift-frontend -compile-module-from-interface`
- Finds `.tbd` in swiftmodule dir, or generates via `xcrun tapi stubify`
- Derives module name and library name from the swiftmodule directory name
- `--platform-target simulator|device` controls slice selection (default: simulator)
- Implemented in `XCFrameworkResolver.cs` with `ICommandRunner` abstraction for testability (32 unit tests)

**Mode 2: Manual inputs (original)**
- `-a` ABI JSON, `-d` dylib, `-t` TBD, `-o` output directory
- Plus optional: `--async-library`, `-s` swiftinterface, `--symbolgraph`, `--bridge-hints`, `--namespace-pattern`
- Mutually exclusive with `--xcframework`

**It produces:**
- `Swift.{Module}.cs` — main C# bindings
- `Swift.{Module}.swift` — Swift wrapper functions (protocol dispatch, async wrappers, array normalization, closure Cdecl stubs)
- `Swift.{Module}.Wrappers.cs` — manual wrapper code (when needed)
- `Swift.{Module}.SwiftUIBridge.cs` + `.swift` — SwiftUI bridge (when views detected)
- `binding-report.json` — coverage metrics
### What the Generator Also Does (in `--xcframework` mode)

- Emits a ready-to-build `.csproj` with `PackageReference` to `Swift.Runtime`, NativeReference items, and NuGet pack layout
- Emits a `.targets` file for NuGet consumers (NativeReference injection, platform version validation)
- Extracts xcframework metadata (version, min iOS, SDK version) into `binding-metadata.json`
- Detects version placeholders (Xcode default "1.0"/"1.0.0") and emits `PackageVersion=0.0.0` with a warning comment

### Swift.Runtime

- Multi-target: `net10.0;net10.0-ios;net10.0-macos;net10.0-maccatalyst`
- Packable as a NuGet package (`Swift.Runtime`, version `0.1.0-preview.1`)
- Contains: SwiftString, SwiftArray, SwiftOptional, ARC, SafeHandle, ValueWitnessTable, type database XMLs, native dylibs per platform
- NuGet package includes: compiled DLL per TFM, XML type databases, native dylibs in `native/{platform}/`, `.targets` in `buildTransitive/` for automatic dylib injection, README
- Binding test projects reference it as `<ProjectReference>` within the repo (unaffected by NuGet packaging)

### Binding Test Projects (How Bindings Are Consumed Today)

All four test libraries (Nuke, Lottie, BlinkID, CryptoSwift) follow this pattern:

```
BindingTesting/{Library}/
├── {Library}.xcframework/                    # Pre-built framework
├── output-ios/                               # Generator output
│   ├── Swift.{Library}.cs                    # Generated bindings
│   ├── Swift.{Library}.swift                 # Swift wrapper source
│   ├── {Library}SwiftBindings.xcframework/   # Compiled Swift wrapper (module-unique name)
│   ├── {Library}.Swift.iOS.csproj            # Ready-to-build project (Step 4)
│   ├── {Library}.Swift.iOS.targets           # Consumer .targets (Step 4)
│   └── binding-metadata.json                 # Extracted metadata (Step 4)
├── {Library}TestApp/                         # iOS test app
│   └── {Library}TestApp.csproj               # References everything manually
├── regenerate-bindings.sh                    # Runs generator with correct flags
├── build-swift-wrapper.sh                    # Compiles Swift wrapper → xcframework
├── build-testapp.sh                          # Builds the .NET iOS app
├── build-all.sh                              # Orchestrates all three
└── validate-sim.sh                           # Runs on iOS Simulator
```

Test app `.csproj` references:
```xml
<ProjectReference Include="../../../src/Swift.Runtime/src/Swift.Runtime.csproj" />
<Compile Include="../output-ios/Swift.Nuke.cs" />
<Compile Include="../output-ios/Swift.Nuke.Wrappers.cs" />
<NativeReference Include="../Nuke.xcframework" Kind="Framework" />
<NativeReference Include="../output-ios/SwiftBindings.xcframework" Kind="Framework" />
```

This is a **source-inclusion** pattern — generated C# is compiled directly into the test app. There is no separate binding DLL.

### Shell Scripts That Encode Domain Knowledge

Each binding test has shell scripts (~50-100 lines each) that automate steps a developer would need to know:

- `regenerate-bindings.sh` — knows how to extract ABI JSON from xcframework, find the dylib/TBD, and invoke the generator with correct flags
- `build-swift-wrapper.sh` — knows how to compile the generated `.swift` file into an xcframework for both device and simulator architectures
- `build-testapp.sh` — knows the right .NET build flags for iOS

This domain knowledge is what the MSBuild SDK needs to encode.

---

## Why MSBuild SDK (Not a CLI Tool)

### Precedent in .NET

| Project Type | Input | Build-Time Magic | Output |
|-------------|-------|-------------------|--------|
| **gRPC** | `.proto` files | Protobuf compiler generates C# | DLL with gRPC clients |
| **Razor** | `.cshtml` files | Razor compiler generates C# | DLL with page handlers |
| **ObjC Bindings** | `ApiDefinition.cs` | `btouch`/`bgen` generates P/Invoke | Binding DLL |
| **Swift Bindings** | `.xcframework` | Generator + `swiftc` | Binding DLL + NuGet |

All of these use MSBuild targets that run during `dotnet build`. The Swift Bindings SDK follows the same pattern.

### Why Not a Standalone CLI Tool?

A CLI tool (`dotnet swift-bind Nuke.xcframework`) would:
- Require the user to learn a new command and its flags
- Produce files that the user then manually incorporates into a project
- Not integrate with `dotnet pack` for NuGet output
- Not participate in incremental builds (regenerate only when xcframework changes)
- Be partially throwaway — the MSBuild SDK would need to wrap or replace it

The MSBuild SDK approach means the user never leaves the standard .NET workflow (`dotnet new`, `dotnet build`, `dotnet pack`). The generator is invisible.

### What an MSBuild SDK Actually Is

An MSBuild SDK is a NuGet package with a specific layout:

```
Swift.Bindings.Sdk.nupkg/
├── Sdk/
│   ├── Sdk.props          # Imported at TOP of every project using this SDK
│   └── Sdk.targets        # Imported at BOTTOM — defines custom build targets
├── tools/
│   ├── net10.0/
│   │   └── swift-bindings.dll   # The generator, shipped as a tool
│   └── [platform-specific tools if needed]
└── Swift.Bindings.Sdk.nuspec
```

When a project declares `Sdk="Swift.Bindings.Sdk/1.0.0"`, MSBuild automatically:
1. Restores the SDK package
2. Imports `Sdk.props` before the project body
3. Imports `Sdk.targets` after the project body

`Sdk.props` can define:
- The `SwiftFramework` item type
- Default properties (`TargetFramework`, `IsPackable`, etc.)
- Implicit `PackageReference` to `Swift.Runtime`

`Sdk.targets` can define build targets:
- `ExtractSwiftABI` (BeforeTargets="CoreCompile")
- `GenerateBindings` (BeforeTargets="CoreCompile", AfterTargets="ExtractSwiftABI")
- `CompileSwiftWrapper` (BeforeTargets="CoreCompile", AfterTargets="GenerateBindings")
- `EmitNuGetMetadata` (BeforeTargets="Pack")
- `GenerateConsumerTargets` (BeforeTargets="Pack")

---

## What `dotnet build` Does Under the Hood

When the binding author runs `dotnet build`, the SDK's targets execute automatically. The full target chain (9 targets) is documented in [Step 5](#step-5-msbuild-sdk-package-) above, covering:

1. **Validate + Discover** — auto-discover `*.xcframework`, validate inputs (SWIFTBIND001/002/003/100)
2. **Fingerprint** — hash all build-affecting inputs into a stamp file (~50ms)
3. **Generate** — invoke the generator via `dotnet exec` (skipped if fingerprint unchanged)
4. **Import metadata** — `XmlPeek` reads `binding-metadata.props` for version/module info
5. **Compile** — add generated `.cs` to `Compile` item group → standard `CoreCompile`
6. **NativeReference** — inject xcframework references for the .NET iOS linker
7. **Pack** (during `dotnet pack`) — validate wrapper slices, arrange NuGet layout

The resulting NuGet package structure:
```
Nuke.Swift.iOS.12.8.0.nupkg/
├── lib/net10.0-ios/
│   └── Nuke.Swift.iOS.dll
├── buildTransitive/net10.0-ios/
│   └── Nuke.Swift.iOS.targets          # Single canonical import path
└── runtimes/ios-arm64/native/
    ├── Nuke.xcframework/
    └── NukeSwiftBindings.xcframework/   # Module-unique wrapper name
```

---

## Implementation: Incremental Path (Nothing Is Throwaway)

Each step builds permanently toward the SDK. No step is discarded when the next is implemented.

### Step 1: Generator accepts `--xcframework` directly ✅

**Status: Complete.**

**What was implemented:**
- `XCFrameworkResolver.cs` — self-contained resolver with `ICommandRunner` abstraction for subprocess testability
- Plist parsing (XML), iOS slice selection (simulator/device with fallback), Swift module discovery
- ABI JSON fallback chain: existing `.abi.json` → `swift-frontend` generation from `.swiftinterface`
- TBD fallback: existing `.tbd` → `xcrun tapi stubify` generation from dylib
- Static xcframework detection (`.a` archives and static libraries in `.framework` bundles)
- Concurrent stdout/stderr reads with timeout enforcement via `CancellationTokenSource`
- `--xcframework` and `--platform-target` CLI options in `Program.cs`, mutually exclusive with `-a/-d/-t`
- 32 unit tests across 8 test classes (plist parsing, slice selection, module discovery, ABI JSON fallback, TBD generation, static detection, validation, swiftinterface discovery)
- Validated against all 5 xcframeworks in repo (Nuke, CryptoSwift, Lottie, BlinkID, TestFramework)

**After this step:** `dotnet run --project src/Swift.Bindings/src -- --xcframework Nuke.xcframework -o output/` produces all generated files from a single input.

**Contributes to SDK:** Target 1 (ExtractSwiftABI) and Target 2 (GenerateBindings) will invoke this same code path.

### Step 2: Generator compiles Swift wrapper automatically ✅

**Status: Complete.**

**What was implemented:**
- `SwiftWrapperCompiler.cs` — orchestrates wrapper compilation: file collection, post-processing, deployment target resolution, xcframework structure creation, swiftc invocation
- `SwiftWrapperPostProcessor.cs` — C# port of the Python post-processing from `build-async-wrapper.sh`. Line-by-line brace-counting block detector that strips 4 categories of known-broken patterns (EveryProtocol, @_silgen_name broken functions, broken extensions, standalone broken funcs)
- **Module-unique wrapper name**: `{Module}SwiftBindings` (e.g., `NukeSwiftBindings.xcframework`) — prevents collisions when multiple binding packages are consumed in one app
- Auto-sets `--async-library` to `{Module}SwiftBindings` before `GenerateBindings()` when using `--xcframework` with a simulator slice
- Deployment target derived from source framework's `Info.plist` `MinimumOSVersion` (falls back to 15.0)
- Produces simulator (arm64) slice. Device slices deferred to Step 5.
- Error handling: SDK resolution failures, swiftc failures, all-code-stripped detection. `EvaluateResult()` centralizes outcome logic (Fatal/Warning/Success) with full unit test coverage. Auto-wired failures set non-zero exit code and abort.
- Gated on resolved simulator slice (`IsSimulatorSlice`) — skips compilation for device-only frameworks
- 3 new properties on `XCFrameworkResolution`: `FrameworkSearchPath`, `LibraryIdentifier`, `IsSimulatorSlice`
- ~82 unit tests across post-processor patterns, compiler internals, end-to-end compilation, and fatal exit-code branches

**After this step:** `dotnet run --project src/Swift.Bindings/src -- --xcframework Nuke.xcframework -o output/` produces all generated files including the compiled Swift wrapper xcframework from a single input.

**Contributes to SDK:** Target 3 (CompileSwiftWrapper) invokes this same capability.

### Step 3: Swift.Runtime as a NuGet package ✅

**Status: Complete.**

**What was implemented:**
- `Swift.Runtime.csproj`: Removed `IsPackable=false` (inherits `true` from `Directory.Build.props`), added NuGet metadata (`PackageId=Swift.Runtime`, `PackageVersion=0.1.0-preview.1`, description, authors, tags, readme)
- Content glob narrowed from `Swift/**/*.*` to `Swift/**/*.xml` — C# files compile into the DLL; only the 10 XML type databases need to be copied as content (used by the generator at code-gen time)
- Added `Pack="false"` to existing conditional Content items for native dylibs — prevents double-packing via both Content items and explicit pack items. Content items continue to work for ProjectReference consumers (all test apps).
- Added NuGet pack items: native dylibs in `native/{platform}/`, `.targets` file in `buildTransitive/`, README at package root
- Created `build/Swift.Runtime.targets` — conditional native dylib injection for NuGet consumers with `IncludeSwiftBindingsRuntimeNative` opt-out (default: true). Three conditional ItemGroups with RID-starts-with guards: macOS (non-ios/maccatalyst TFM), iOS device (`RuntimeIdentifier.StartsWith('ios-')`), iOS simulator (`RuntimeIdentifier.StartsWith('iossimulator-')`)
- Generator: removed `CopyDirectory` call and helper method from `Program.cs` — generator no longer copies `Swift/` runtime source directory to output. The generator still reads XML type databases from its own bin directory via its ProjectReference to Swift.Runtime.
- Versioning: independent from binding packages, starts at `0.1.0-preview.1` (pre-release). Uses `<PackageVersion>` (not `<Version>`) to keep assembly version simple.

**After this step:** `dotnet pack` in `src/Swift.Runtime/src/` produces a `.nupkg` with compiled DLLs, XML type databases, native dylibs, consumer `.targets`, and README. External consumers can reference Swift.Runtime from a NuGet feed.

**Contributes to SDK:** `Sdk.props` will inject the `PackageReference` to `Swift.Runtime` automatically.

### Step 4: Generator emits compilable `.csproj` + NuGet packaging support ✅

**Status: Complete.**

**What was implemented:**

- `PlistReader.cs` — Shared binary/XML plist reader. Uses `plutil -convert xml1 -o /dev/stdout` to handle binary plists (which inner framework Info.plists use), with XML `XmlDocument.Load` fallback for self-generated XML plists. Fixes a latent bug where `SwiftWrapperCompiler.ResolveDeploymentTarget()` silently fell back to "15.0" on binary plists because `XmlDocument.Load()` can't parse binary plist format.
- `XCFrameworkMetadataExtractor.cs` — Extracts `CFBundleShortVersionString`, `MinimumOSVersion`, and `DTPlatformVersion` from the inner framework's `Info.plist` via `PlistReader`. Detects Xcode default version placeholders ("1.0", "1.0.0") and clamps MinimumOSVersion to max(raw, 15.0) for the .NET 10 iOS floor. Reads platform list from outer xcframework `Info.plist`. Emits `binding-metadata.json` with all extracted metadata.
- `BindingProjectEmitter.cs` — Emits `{Module}.Swift.iOS.csproj` targeting `net10.0-ios` with:
  - `PackageReference` to `Swift.Runtime` (version `0.1.0-preview.1`)
  - `PackageVersion` from xcframework metadata (or `0.0.0` with XML warning comment for placeholders)
  - `SupportedOSPlatformVersion` from clamped MinimumOSVersion
  - Explicit `Compile` items for generated `.cs` files (with `Condition="Exists()"` on optional Wrappers/SwiftUIBridge files)
  - `NativeReference` items for source xcframework (relative path) and optional wrapper xcframework
  - NuGet pack layout: `.targets` in `buildTransitive/net10.0-ios/`, xcframeworks in `runtimes/ios-arm64/native/`
- `ConsumerTargetsEmitter.cs` — Emits `{PackageId}.targets` for NuGet consumers with:
  - Idempotency guard (`_SwiftBinding_{Module}_Injected` property) to prevent duplicate NativeReference injection
  - `NativeReference` items for source and wrapper xcframeworks via `$(MSBuildThisFileDirectory)` relative paths
  - `SwiftBindingFramework` registration for downstream tooling (Layer 3 validation)
  - `SWIFTBIND010` platform version warning using `System.Version.CompareTo()` for correct numeric comparison (not lexicographic `<` which miorders `15.10` vs `15.2`)
  - Module name sanitization (dots/hyphens → underscores for MSBuild target/property names)
- `Program.cs` — Project emission after wrapper compilation, gated on `hasXcframework && resolution != null`. Fatal on failure (exit code 1) — in `--xcframework` mode, a ready-to-build project is the primary contract.
- `SwiftWrapperCompiler.ResolveDeploymentTarget()` — Refactored to delegate to `PlistReader.ReadPlistDict()` instead of direct `XmlDocument.Load()`, fixing the binary plist silent fallback bug.
- 74 unit tests across 4 new test files:
  - `PlistReaderTests.cs` (8 tests): plutil success/failure, XML fallback, binary plist null return, key type parsing, real-world Nuke data
  - `XCFrameworkMetadataExtractorTests.cs` (22 tests): version placeholder detection, MinOS clamping, full extraction, metadata JSON emission
  - `BindingProjectEmitterTests.cs` (19 tests): project structure, compile items, NativeReference, NuGet layout, placeholder warning, custom runtime version
  - `ConsumerTargetsEmitterTests.cs` (23 tests): NativeReference injection, idempotency guard, platform version warning, `Version.CompareTo` correctness across 8 edge-case version pairs, module name sanitization
  - Plus 2 additional tests in existing `SwiftWrapperCompilerTests.cs` for PlistReader delegation

**Verified real-world metadata:**
| Framework | CFBundleShortVersionString | MinimumOSVersion | IsPlaceholder? | PackageVersion |
|-----------|---------------------------|------------------|----------------|----------------|
| Nuke | 12.8.0 | 13.0 | No | 12.8.0 |
| BlinkIDUX | 1.0 | 16.0 | Yes | 0.0.0 |

**After this step:** The manual workflow works end-to-end:
```bash
dotnet run --project src/Swift.Bindings/src -- --xcframework Nuke.xcframework -o Nuke.Swift.iOS/
cd Nuke.Swift.iOS && dotnet build && dotnet pack
# → Nuke.Swift.iOS.12.8.0.nupkg
```

**Manual mode regression:** `-a/-d/-t` flags do NOT emit .csproj/.targets — project emission is gated on `hasXcframework`.

**Contributes to SDK:** Targets 5-7 reuse the metadata extraction and `.targets` generation logic.

### Step 5: MSBuild SDK package ✅

**Status: Complete.**

**What was implemented:**

**Generator changes (Phase 1):**
- `Program.cs` — Three new CLI options: `--sdk-mode` (skips `.csproj` emission — the SDK IS the project system), `--package-id` (overrides default `{Module}.Swift.iOS` in consumer `.targets`), `--wrapper-architectures` (`simulator`/`device`/`all` — CLI default `simulator` for backward compat, SDK default `all` for pack correctness)
- `XCFrameworkMetadataExtractor.cs` — New `EmitMetadataProps()` writes `binding-metadata.props` as MSBuild-native XML (consumed by `XmlPeek` — `<Import>` inside a `<Target>` doesn't work in MSBuild). Emits 7 properties: `_SwiftBindingPackageVersion`, `_SwiftBindingMinimumOSVersion`, `_SwiftBindingModuleName`, `_SwiftBindingIsVersionPlaceholder`, `_SwiftBindingHasWrapperXCFramework`, `_SwiftBindingWrapperModuleName`, `_SwiftBindingWrapperSliceCount`
- `SwiftWrapperCompiler.cs` — Major refactor: extracted `CompileSlice()` (public, parameterized target triple + SDK name), added `CompileAll()` for multi-arch orchestration (post-process once → compile simulator + device → assemble multi-slice xcframework with `WriteXCFrameworkPlist()`). `SliceCount` property on result for metadata emission
- `XCFrameworkResolver.cs` — New `ResolveAll()` returns `(XCFrameworkResolution Simulator, XCFrameworkResolution? Device)`, handles missing device slice gracefully with warning
- `Program.cs` — Extracted `ShouldCompileWrapper()` as a testable static method covering the `isSimulatorSlice × wrapperArchitectures` matrix. Three branches: `simulator` (existing path), `device` (resolves device slice, calls `CompileSlice` with `iphoneos` SDK), `all` (calls `CompileAll` with both resolutions)

**SDK package (Phase 2):**
- `src/Swift.Bindings.Sdk/Sdk/Sdk.props` — SDK property defaults: `net10.0-ios`, `AllowUnsafeBlocks`, `IsPackable=true`, `Nullable=enable`, `SwiftWrapperArchitectures=all` (both slices by default), `SwiftPlatformTarget=simulator`, implicit `PackageReference` to `Swift.Runtime` with `$(SwiftRuntimeVersion)` version floor. NO auto-discovery (that's in `.targets`, after project body evaluation)
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` — 9 MSBuild targets:

| # | Target | Fires | Purpose |
|---|--------|-------|---------|
| 0 | `_ValidateSwiftPackageItems` | Before `_DiscoverSwiftFrameworks` | Error `SWIFTBIND100` on `<SwiftPackage>` (v2 stub) |
| 1 | `_DiscoverSwiftFrameworks` | Before `_ComputeSwiftFingerprint` | Auto-discover `*.xcframework`; validate count (`SWIFTBIND001`/`002`/`003`) |
| 2 | `_ComputeSwiftFingerprint` | Before `_GenerateSwiftBindings` | Hash dylib + Info.plist + swiftinterface + optional inputs + properties → stamp file |
| 3 | `_GenerateSwiftBindings` | Before `CoreCompile` | Invoke generator (skipped if fingerprint unchanged) |
| 4 | `_ImportSwiftBindingMetadata` | After `_GenerateSwiftBindings` | `XmlPeek` reads `binding-metadata.props` → sets `PackageVersion`, `SupportedOSPlatformVersion` |
| 5 | `_IncludeGeneratedSwiftBindings` | After `_ImportSwiftBindingMetadata` | Add generated `.cs` to `Compile` |
| 6 | `_ResolveSwiftNativeReferences` | Before `ResolveNativeReferences` | Inject `NativeReference` items for source + wrapper xcframeworks |
| 7a | `_ValidateSwiftBindingPackSlices` | Before `_ConfigureSwiftBindingPack` | Two guards: property check (`SWIFTBIND030`), artifact check (`SWIFTBIND031`) |
| 7b | `_ConfigureSwiftBindingPack` | Before `GenerateNuspec` | Arrange NuGet pack layout (`.targets` + xcframeworks in `runtimes/`) |

- `src/Swift.Bindings.Sdk/Swift.Bindings.Sdk.csproj` — `PackageType=MSBuildSdk`, `NoBuild=true`, packs `Sdk/` and `tools/net10.0/any/`
- `src/Swift.Bindings.Sdk/build-sdk.sh` — Publishes generator then packs SDK NuGet

**Fingerprint-based incremental build (Target 2):**
- Shell-based `shasum -a 256` pipeline covers extensionless framework binaries (`Module.framework/Module`) that `*.dylib` globs would miss
- Hashes: outer Info.plist, framework binaries, public `.swiftinterface`, optional supplementary inputs (SymbolGraph, BridgeHints, SwiftInterface), all generation-affecting properties
- Cost: ~50ms per build. Fingerprint stored in `obj/swift-binding/swift-binding.stamp`
- Optional file loop uses `if/then/fi` with `done;` (not `&&` chaining) to keep property echo unconditional — prevents fingerprint drops when optional files are unset

**Template package (Phase 3):**
- `src/Swift.Bindings.Templates/` — `dotnet new swift-binding` template with `sdkVersion` parameter
- Template project: `<Project Sdk="Swift.Bindings.Sdk/0.1.0-preview.1">` with `net10.0-ios`

**Unit tests (51 new tests):**
- `XCFrameworkMetadataPropsTests.cs` (4 tests): file creation, all properties present, no-wrapper state, valid MSBuild XML
- `ProgramSdkModeTests.cs` (8 tests): CLI option parsing for `--sdk-mode`, `--package-id`, `--wrapper-architectures`
- `ShouldCompileWrapperTests.cs` (6 tests): full 2×3 truth table (isSimulatorSlice × wrapperArchitectures)
- `SdkPropsTargetsTests.cs` (25 tests): Sdk.props content (Microsoft.NET.Sdk import, default properties, Swift.Runtime reference, no auto-discovery), Sdk.targets content (all 9 targets, SWIFTBIND error codes, fingerprint, XmlPeek metadata, pack layout, auto-discovery placement)
- `SwiftWrapperCompilerTests.cs` (2 updated): `targetTriple` parameter on `InvokeSwiftCompiler`

**After this step:** The full vision works:
```bash
dotnet new swift-binding -n Nuke.Swift.iOS
cp -r Nuke.xcframework Nuke.Swift.iOS/
cd Nuke.Swift.iOS && dotnet build && dotnet pack
```

---

## Open Questions

### Q1: xcframework Layout Reliability ✅ Resolved

**Answer: Yes.** All five tested xcframeworks (Nuke, CryptoSwift, Lottie, BlinkID, TestFramework) follow the expected layout. Step 1's `XCFrameworkResolver` handles all observed variations:
- Two-slice (Nuke: device + simulator), single-slice (TestFramework: simulator only), multi-platform (Lottie: 8 slices across iOS/tvOS/macOS/xrOS)
- SPM-built (CryptoSwift), Xcode-built (BlinkID), custom-built (TestFramework)
- With and without pre-existing TBD/ABI JSON files
- macOS Catalyst slices correctly excluded from iOS slice selection

Actionable errors cover: static xcframeworks, ObjC-only frameworks, missing Info.plist, no iOS slices, multiple Swift modules, missing `file` command.

### Q2: MSBuild SDK Packaging Mechanics ✅ Resolved

**Answer:** `Sdk.props` sets `_SwiftBindingGeneratorDir` to `$(MSBuildThisFileDirectory)../tools/net10.0/any/`. `Sdk.targets` invokes `dotnet exec "$(_SwiftBindingGeneratorDir)Swift.Bindings.dll"` with all CLI flags. The generator runs on .NET; platform tools (`swiftc`, `xcodebuild`, `plutil`) are system tools invoked by the generator via `ICommandRunner`, not shipped in the package. The SDK is a single NuGet (`PackageType=MSBuildSdk`) with `NoBuild=true` — the generator is published to `tools/` at pack time by `build-sdk.sh`.

### Q3: Swift.Runtime Versioning Strategy ✅ Resolved

**Answer: Independent versioning.** Swift.Runtime 0.1.0-preview.1 (pre-release, signals experimental status), SDK on its own cadence, binding packages use upstream library versions (e.g., Nuke.Swift.iOS 12.8.0). SDK will declare a minimum Swift.Runtime version via version-ranged `PackageReference` in `Sdk.props`.

### Q4: Swift Wrapper Compilation — Architecture Slices ✅ Resolved

**Answer: User-controlled via `<SwiftWrapperArchitectures>`.** Defaults to `all` (both device + simulator) in the SDK — so `dotnet pack` always produces correct NuGet output. Fast-iteration users can override to `simulator` for quicker dev builds. The property is included in the fingerprint hash, so changing it triggers regeneration. Pack-time validation (`SWIFTBIND030`) errors if `SwiftWrapperArchitectures != all` during `dotnet pack`. Missing source slices (e.g., simulator-only xcframework) produce `SWIFTBIND031` with clear instructions.

### Q5: Incremental Build Support ✅ Resolved

**Answer: Fingerprint-based stamp file.** Implemented as `_ComputeSwiftFingerprint` target (Target 2 in Sdk.targets). Shell-based `shasum -a 256` pipeline hashes: outer `Info.plist`, all framework binaries (extensionless Mach-O inside `*.framework/`), public `.swiftinterface` files, optional supplementary inputs (SymbolGraph, BridgeHints, SwiftInterface), and all generation-affecting properties (`_SwiftBindingSdkVersion`, `SwiftPlatformTarget`, `SwiftWrapperArchitectures`, `NamespacePattern`, `PackageId`). Sorted for determinism. Stamp stored at `obj/swift-binding/swift-binding.stamp`. Generation target (`_GenerateSwiftBindings`) is gated on `'$(_SwiftBindingUpToDate)' != 'true'`. Cost: ~50ms per build.

Edge cases all covered: xcframework replacement (dylib hash changes), SDK update (version string changes), property changes (included in hash). Optional file loop uses `if/then/fi` to prevent fingerprint drops when supplementary files are unset.

### Q6: Multi-Framework Libraries (e.g., Nuke + NukeUI + NukeExtensions) ✅ Resolved

**Answer: One project per framework + `<SwiftFrameworkDependency>` for inter-framework imports.**

Each framework becomes a separate NuGet package. When a framework imports another (e.g., ACSSmartCardIO imports SmartCardIO), the binding author declares the dependency:

```xml
<!-- ACSSmartCardIO.Swift.iOS.csproj -->
<Project Sdk="Swift.Bindings.Sdk/0.1.0-preview.1">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <SwiftFrameworkDependency Include="../SmartCardIO.xcframework"
                              PackageId="SmartCardIO.Swift.iOS"
                              PackageVersion="1.0.0" />
  </ItemGroup>
</Project>
```

**What `<SwiftFrameworkDependency>` provides:**
- **Build time:** Adds `-F` search path so `swiftc` can find the dependency module when compiling the Swift wrapper
- **NuGet time:** Injects `<PackageReference>` in `Sdk.props` (evaluation-time, so NuGet restore sees it)
- **Local build:** Adds `<NativeReference>` for the dependency xcframework
- **Fingerprinting:** Dependency xcframework contents are hashed — changes trigger regeneration
- **Validation:** `SWIFTBIND040` warns if `PackageId` or `PackageVersion` metadata is missing when `IsPackable=true`

**CLI equivalent:**
```bash
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework ACSSmartCardIO.xcframework \
  --framework-dependency SmartCardIO.xcframework \
  -o output/
```

Dependency NuGet packages are NOT included in the pack output — they come from their own packages via `buildTransitive/` targets. Multi-framework projects (one project → multiple NuGet packages) remain a future consideration.

### Q7: Error Experience ✅ Partially Resolved

**Implemented SWIFTBIND error codes:**

| Code | Target | Condition |
|------|--------|-----------|
| `SWIFTBIND001` | `_DiscoverSwiftFrameworks` | No xcframework found |
| `SWIFTBIND002` | `_DiscoverSwiftFrameworks` | Multiple xcframeworks (ambiguous) |
| `SWIFTBIND003` | `_DiscoverSwiftFrameworks` | xcframework path doesn't exist |
| `SWIFTBIND010` | Consumer `.targets` | Consumer's `SupportedOSPlatformVersion` too low |
| `SWIFTBIND020` | `_ImportSwiftBindingMetadata` | Version placeholder detected (Xcode default "1.0") |
| `SWIFTBIND030` | `_ValidateSwiftBindingPackSlices` | Pack without `SwiftWrapperArchitectures=all` |
| `SWIFTBIND031` | `_ValidateSwiftBindingPackSlices` | Wrapper xcframework missing device or simulator slice |
| `SWIFTBIND040` | `_ValidateSwiftDependencyMetadata` | `SwiftFrameworkDependency` missing `PackageId` or `PackageVersion` when `IsPackable=true` |
| `SWIFTBIND050` | Generator (SDK mode) | Wrapper compilation failed — C# bindings still valid, wrapper-dependent methods throw `DllNotFoundException` at runtime |
| `SWIFTBIND100` | `_ValidateSwiftPackageItems` | `<SwiftPackage>` used (v2 stub) |

**Also handled by the generator** (non-SWIFTBIND errors surfaced as build output):
- Static xcframework detection (no dylib) → clear error
- ObjC-only framework (no `.swiftinterface`) → clear error
- `swift-frontend` failure → compiler stderr surfaced
- `swiftc` wrapper compilation failure → compiler stderr surfaced
- All code stripped by post-processor → warning with context

**Not yet implemented:** Xcode version validation (detecting too-old Xcode), `binding-report.json` summary as MSBuild warnings.

### Q8: What About Existing Binding Test Projects?

Should the binding test projects (Nuke, Lottie, BlinkID, CryptoSwift) migrate to the SDK?

**Recommendation:** Yes, eventually. They serve as integration tests for the SDK itself. But not until the SDK is functional — keep the current shell-script workflow as a fallback during development.

### Q9: SwiftUI Bridge in the SDK

The generator can produce SwiftUI bridge files (`.cs` + `.swift`). How does this integrate?

**Current state:** The SwiftUI bridge Swift code is compiled into a separate `{Library}Bridge.framework` by `build-bridge.sh`. The consumer's app includes both the binding DLL and the bridge framework.

**In the SDK:** The bridge Swift compilation would be another build target, and the bridge framework would be included in the NuGet package alongside the main wrapper xcframework.

**Recommendation:** Defer SwiftUI bridge SDK integration. It adds complexity and the bridge is already well-tested via shell scripts. Add it after the core SDK workflow is proven.

### Q10: Consumer Validation Test Matrix

What consumer scenarios must be validated before shipping?

**Minimum test matrix:**

| Scenario | What to verify |
|----------|---------------|
| Single-package app | Install one binding, `dotnet build`, app runs on simulator |
| Multi-package app | Install 2+ bindings (e.g., Nuke + Lottie), no wrapper name collision, both work |
| Transitive dependency | App references project that references binding — `buildTransitive/` targets fire |
| Missing dependency | Remove required companion package → build error (Layer 3 validation) |
| iOS version mismatch | Consumer's `SupportedOSPlatformVersion` < framework minimum → build warning |
| Pack/restore round-trip | `dotnet pack` then `dotnet add package` from local feed → identical behavior to direct reference |
| Device + simulator parity | NuGet works for both device and simulator builds |

**When:** Implement during Step 4 (NuGet packaging support), validate during Step 5 (SDK packaging).

---

## Swift Package Manager Support (v2 — Designed for Now, Built Later)

Many Swift libraries are distributed as SPM packages (source code + `Package.swift`), not prebuilt xcframeworks. SPM support is critical for long-term adoption but fits cleanly as an additive layer — no rework required on v1.

### Architecture: SPM as a Pre-Step

The v1 pipeline works entirely on xcframeworks:

```
xcframework → ExtractSwiftABI → GenerateBindings → CompileSwiftWrapper → CoreCompile → Pack
```

SPM support adds a resolution step at the front:

```
SwiftPackage → ResolveSwiftPackages → xcframework → [same pipeline unchanged]
```

The generator, wrapper compilation, NuGet packaging — none of that changes. `ResolveSwiftPackages` converts SPM input into a **dynamic** xcframework, then the v1 pipeline takes over. This means **Steps 1-5 require zero rework** when SPM is added, provided `ResolveSwiftPackages` outputs dynamic frameworks.

**Critical constraint:** Many SPM libraries use `.automatic` product type, which often resolves to static linking. `ResolveSwiftPackages` MUST force dynamic library output (via `xcodebuild` build settings: `MACH_O_TYPE=mh_dylib`). If a library cannot be built as dynamic (e.g., it has no public `@_exported` module or uses link-time-only features), SPM support should emit a clear error explaining the limitation. v2 scope is **dynamic-capable SPM products only** — mirroring the v1 constraint on dynamic xcframeworks.

### SDK Item Types (Define Both in v1)

```xml
<!-- v1: prebuilt xcframework -->
<SwiftFramework Include="Nuke.xcframework" />

<!-- v1: dependency xcframework (for libraries that import other Swift frameworks) -->
<SwiftFrameworkDependency Include="../SmartCardIO.xcframework"
                          PackageId="SmartCardIO.Swift.iOS"
                          PackageVersion="1.0.0" />

<!-- v2: SPM package from URL -->
<SwiftPackage Include="https://github.com/kean/Nuke" Version="12.8.0" />

<!-- v2: local SPM package -->
<SwiftPackage Include="../my-swift-lib/" />
```

`SwiftFrameworkDependency` metadata:
| Metadata | Required | Description |
|----------|----------|-------------|
| `Identity` (path) | Yes | Path to the dependency xcframework |
| `PackageId` | For NuGet pack | NuGet package ID (e.g., `SmartCardIO.Swift.iOS`) |
| `PackageVersion` | For NuGet pack | Dependency package version |

**Groundwork for v1:** `Sdk.props` defines both `SwiftFramework` and `SwiftPackage` item types from day one. If someone uses `SwiftPackage` in v1, they get a clear error:

```
error SWIFTBIND100: Swift Package Manager support is not yet available.
Build your SPM package into an xcframework first, then use <SwiftFramework>.
See the "Building xcframeworks from SPM packages" section in the Getting Started guide.
```

The error message links to the Getting Started guide created in Step 4 (DX-1 item 3), which documents the `xcodebuild -create-xcframework` workflow for SPM packages.

**Validation ordering:** `SWIFTBIND100` (SwiftPackage not supported) MUST fire before the xcframework discovery check. Otherwise a project with only `<SwiftPackage>` items gets the wrong error ("No xcframework found") instead of the actionable SPM message. The target order in `Sdk.targets` is:

1. `ValidateSwiftPackageItems` — if any `SwiftPackage` items exist, emit `SWIFTBIND100` and stop
2. `DiscoverSwiftFrameworks` — glob `*.xcframework`, validate count
3. `ExtractSwiftABI` — proceed with resolved `SwiftFramework` items

This keeps the public API surface stable — adding SPM in v2 doesn't change the item types or break existing projects.

### Internal Abstraction Boundary

All build targets after resolution work exclusively on `SwiftFramework` items:

```
Sdk.props
  ├── Defines SwiftFramework (resolved input)
  └── Defines SwiftPackage (unresolved input — v2)

Sdk.targets
  ├── ResolveSwiftPackages        ← v2: converts SwiftPackage → SwiftFramework
  ├── ExtractSwiftABI             ← works on SwiftFramework
  ├── GenerateBindings            ← works on SwiftFramework
  ├── CompileSwiftWrapper         ← works on SwiftFramework
  └── Pack                        ← works on SwiftFramework
```

The generator's contract is `--xcframework`. It never accepts SPM packages directly. This separation means:
- Generator code doesn't change for SPM support
- SPM resolution logic is isolated in one target

**Integration testing caveat:** Although the generator code doesn't change, SPM-built xcframeworks may have different internal layouts, module structures, or build artifacts compared to prebuilt vendor xcframeworks. When SPM support is implemented, a focused integration test suite must validate the generator against resolver-produced xcframeworks (at minimum: one SPM-built library from each build system — Xcode workspace, `swift build`, and `xcodebuild` CLI). This is integration risk, not generator rework.

### What `ResolveSwiftPackages` Will Do (v2)

When implemented, this target would:

1. **Resolve the package** — clone from URL or resolve local path, then **pin to a resolved commit SHA**
2. **Build for all platforms** — `xcodebuild` for device (arm64) and simulator (arm64 + x86_64), using `MACH_O_TYPE=mh_dylib`
3. **Create xcframework** — `xcodebuild -create-xcframework` from the platform builds

**Reproducibility: lock file + commit pinning**

SPM inputs are mutable (version tags can move, branches advance, local paths change). Without pinning, the same project can produce different binaries over time. `ResolveSwiftPackages` must:

- **Write a lock file** (`swift-binding.lock.json`) recording the resolved commit SHA, resolved version, and content hash for each `SwiftPackage` item:
  ```json
  {
    "packages": {
      "https://github.com/kean/Nuke": {
        "requestedVersion": "12.8.0",
        "resolvedCommit": "a1b2c3d4e5f6...",
        "resolvedVersion": "12.8.0",
        "contentHash": "sha256:..."
      }
    }
  }
  ```
- **On subsequent builds**, if the lock file exists and the requested version hasn't changed, use the pinned commit SHA — don't re-resolve. This guarantees reproducible builds.
- **`dotnet build -p:SwiftPackageForceResolve=true`** re-resolves and updates the lock file. (`--force-resolve` is not a valid `dotnet build` switch — MSBuild properties via `-p:` are the canonical mechanism.)
- **The lock file should be committed to source control** (like `packages.lock.json` in NuGet or `Package.resolved` in SPM itself).
- **Incremental build fingerprint** includes the lock file's content hash per package. If the lock file changes (re-resolve), regeneration triggers. If unchanged, cached xcframework is reused.

For local path packages (`<SwiftPackage Include="../my-swift-lib/" />`), the lock file records a content hash of the package's source files (or `Package.swift` + `Sources/` directory hash). This handles the case where local source changes but the path doesn't.
4. **Extract SPM metadata** — `Package.swift` can provide richer metadata than xcframework Info.plist, but is not always complete. Fallback/precedence rules:

| Metadata | SPM Source | Fallback | Notes |
|----------|-----------|----------|-------|
| Platform version | `platforms: [.iOS(.v15)]` | xcframework `MinimumOSVersion` → Mach-O `LC_BUILD_VERSION` | `platforms` can be omitted in Package.swift — means "no minimum", default to .NET floor (iOS 15.0) |
| Library version | Git tag (semver tags only) | `CFBundleShortVersionString` from built xcframework → `0.0.0` with warning | Dependencies can be pinned by branch/revision/local path — no tag available. Emit warning and require manual `<PackageVersion>` override |
| Dependencies | `Package.swift` `dependencies` array | Binary linkage (`otool -L`) on built xcframework | See dependency mapping rules below |
| Library products | `Package.swift` `products` array | N/A | Only `.library` products are bindable; `.executable` and `.plugin` products are skipped |

**Version resolution precedence for `<SwiftPackage>`:**
1. Explicit `Version` attribute on the item → used as-is
2. Git tag matching semver on the resolved commit → auto-detected
3. No version determinable → `0.0.0` with `SWIFTBIND020` warning and instructions to set `<PackageVersion>`

**Dependency mapping rules:**

SPM's `Package.swift` declares dependencies at the package level, but target-level `dependencies` arrays determine which targets actually use them. The resolver must handle this correctly:

- **Package-level deps** are the universe of available dependencies. **Target-level deps** determine which are actually linked. Only deps that appear in the target-level `dependencies` of a library product's targets (and their transitive closure) are required companion frameworks.
- **Test targets** (`type: .testTarget`) and their deps are excluded — they're not part of the public library.
- **Build tool plugins** (`.plugin` targets) and their deps are excluded.
- **Conditional dependencies** (`condition: .when(platforms: [.iOS])`) are evaluated against the target platform. A dependency conditioned on `.macOS` only is excluded from the iOS NuGet package.
- **Transitive closure**: If product A depends on target B which depends on package C's target D, then C is a required companion. Walk the full target dependency graph, not just direct product deps.
- **Cross-validation**: After resolving from `Package.swift`, cross-check against `otool -L` on the built xcframework. Any `@rpath` dependency found in binary linkage but NOT in the `Package.swift` analysis is flagged as a warning (possible internal dependency not declared as a product dep).

This complexity is one reason multi-product SPM support is deferred — getting single-product dependency mapping right is the prerequisite.

5. **Handle multi-product packages (deferred to v2)** — one SPM package can produce multiple library products (e.g., a package with both `Nuke` and `NukeUI` targets). Each product would become a separate `SwiftFramework` item with auto-generated dependency relationships. This is deferred to the same phase as SPM support itself — v1's one-project-per-framework model (Q6) applies until then.

### What This Means for v1 Implementation

| Step | SPM Impact | Action |
|------|-----------|--------|
| Step 1 (generator `--xcframework`) | None | Generator works on xcframeworks, not SPM |
| Step 2 (wrapper compilation) | None | Compiles from generated Swift, not SPM source |
| Step 3 (Swift.Runtime NuGet) | None | Runtime is independent of input format |
| Step 4 (`.csproj` + NuGet packaging) | None | Packages xcframeworks regardless of origin |
| Step 5 (MSBuild SDK) | **Define both item types + validation ordering** | `SwiftPackage` item type exists, `ValidateSwiftPackageItems` fires before framework discovery, errors with "not yet implemented" message |

**No code changes needed in Steps 1-4.** The groundwork in Step 5 is: defining the `SwiftPackage` item type, the `ValidateSwiftPackageItems` early-exit target, and the `ResolveSwiftPackages` stub in `Sdk.props`/`Sdk.targets`.

---

## Decisions (Resolved During Review)

1. **v1 targets dynamic Swift xcframeworks only.** Static xcframeworks (`.a` archives) are out of scope — they lack dylib/TBD and need a separate extraction pipeline. All four tested libraries are dynamic. Static support is future work.

2. **Wrapper framework name is module-unique: `{Module}SwiftBindings.xcframework`.** The current fixed name `SwiftBindings.xcframework` would collide when an app references multiple binding packages. Renaming to `NukeSwiftBindings.xcframework`, `LottieSwiftBindings.xcframework`, etc. prevents this. Requires changes to the wrapper compilation step and the generated `.targets` file.

3. **Consumer targets go in `buildTransitive/` only (not duplicated in `build/`).** Since we target .NET 10.0 (NuGet 5.0+), `buildTransitive/` covers both direct and transitive consumers. Duplicating into `build/` risks double-importing targets and injecting duplicate `NativeReference` items. Targets include an idempotency guard as defense-in-depth.

4. **Consumer prerequisites: macOS + Xcode required for building iOS apps.** This is a .NET iOS requirement, not specific to Swift bindings. The doc now states this clearly. What the consumer does NOT need is the Swift Bindings SDK, the generator, or Swift knowledge.

5. **SPM support is v2, but the abstraction boundary is designed now.** The SDK defines both `SwiftFramework` and `SwiftPackage` item types from v1. All build targets work on `SwiftFramework` exclusively. SPM support adds a `ResolveSwiftPackages` pre-step that converts `SwiftPackage` → dynamic `SwiftFramework`. v2 SPM scope is dynamic-capable products only (mirroring v1's dynamic xcframework constraint). Multi-product SPM packages are deferred to the same phase. `ValidateSwiftPackageItems` fires before framework discovery to ensure correct error ordering. Integration testing against SPM-built xcframeworks is required when implementing.

---

## Dependencies and Constraints

### macOS + Xcode Required

The binding author MUST be on macOS with Xcode installed. The following system tools are required:
- `swift-frontend` (part of Xcode toolchain) — ABI extraction
- `xcrun swiftc` (part of Xcode toolchain) — Swift wrapper compilation
- `xcodebuild` (part of Xcode) — xcframework creation
- `plutil` (macOS built-in) — Info.plist reading

The binding consumer needs macOS + Xcode (or a paired Mac) to build iOS apps — that's a .NET iOS requirement, not specific to Swift bindings. They do NOT need the Swift Bindings SDK, the generator, or Swift knowledge. They just reference the NuGet package.

### .NET 10.0 Required

The project targets .NET 10.0 with iOS workload. The SDK would require:
- .NET SDK 10.0+
- iOS workload installed (`dotnet workload install ios`)

### Swift.Runtime NuGet Package (Step 3 — Complete)

Swift.Runtime is packable as `Swift.Runtime` version `0.1.0-preview.1`. The package includes compiled DLLs for all TFMs, XML type databases, native dylibs, and a `.targets` file for automatic native library injection. It must be published to a NuGet feed before the SDK (Step 5) can reference it.

---

## Relationship to Existing Documentation

| Document | Relationship |
|----------|-------------|
| `Completed/developer-experience.md` | Detailed NuGet packaging design (layers 1-4, metadata extraction, package structure). The SDK wraps this into automated targets. |
| `testframework-review.md` | Test pipeline hardening. Independent track, interleaved with SDK work. |
| `roadmap.md` | Active work queue. DX-1 through DX-3 map to Steps 1-4 above. DX-4 maps to Step 5. |
| `north-star.md` | Phase 3 (Developer Experience) describes the end state this design achieves. |
| `testing-gaps.md` | Runtime test gaps. Independent track. |

---

## Success Criteria

1. **Binding author:** `dotnet new swift-binding` + drop xcframework + `dotnet build && dotnet pack` produces a correct NuGet package
2. **Consumer:** `<PackageReference Include="Nuke.Swift.iOS" />` + `dotnet build` runs the app with working Swift interop
3. **Errors are actionable:** Every failure mode produces a message that tells the user what to do
4. **Incremental builds work:** Repeat `dotnet build` without xcframework changes completes in seconds
5. **No Swift knowledge required:** The binding author doesn't need to know Swift ABI, calling conventions, or xcframework internals
