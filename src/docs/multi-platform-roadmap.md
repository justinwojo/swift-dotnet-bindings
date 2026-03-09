# Multi-Platform Support Roadmap (macOS, tvOS, Mac Catalyst)

**Created**: March 8, 2026
**Goal**: Extend the generator, SDK, and test infrastructure from iOS-only to all Apple platforms .NET supports. Covers both Swift and ObjC xcframework pipelines.

---

## Current State

- **Generator**: Platform abstraction complete (Session 1). `PlatformInfo`/`SliceVariant` threaded through entire pipeline — both Swift and ObjC. All execution paths accept `PlatformInfo?` with iOS fallback. No hardcoded iOS strings remain in the generator pipeline.
- **MSBuild SDK**: Platform-aware (Session 2). `Sdk.props` auto-detects platform from TFM, `Sdk.targets` uses dynamic pack paths and platform-aware validation. Template supports `--platform` parameter.
- **Swift.Runtime**: Multi-targets `net10.0;net10.0-ios;net10.0-macos;net10.0-maccatalyst` (tvOS when workload ships). However, native dylib injection only covers macOS, iOS device, and iOS simulator — **Mac Catalyst has no native asset path today** (Session 3).
- **Test infrastructure**: Deeply tied to iOS Simulator (`xcrun simctl`, UIKit test app, iPhone device models). Existing `NativeAotTestApp.Mac` provides a macOS console test app that can be extended (Session 3).

---

## Preconditions (resolve before Session 1)

### Package naming convention

The DX design doc (`Completed/developer-experience.md`) already leans toward dropping `.iOS` for multi-platform packages. Lock this down before emitter/SDK work begins, since Session 1D and 2A both depend on it.

**Decision needed**: `{Library}.Swift.iOS` (per-platform) vs `{Library}.Swift` (multi-platform). Recommendation: per-platform suffix (`.iOS`, `.macOS`, `.tvOS`, `.MacCatalyst`) for v1 — simpler NuGet layout, no multi-TFM pack complexity. Multi-platform packages can be a future enhancement.

---

## Session 1: Platform Abstraction + Generator Pipeline ✅

**Status**: Complete (March 8, 2026)
**Validation**: 6614 unit tests pass, 88/88 validation targets pass, no regressions.

**Goal**: Introduce platform modeling and thread it through the entire generator pipeline (both Swift and ObjC), replacing all hardcoded iOS strings. Validate with per-platform smoke generation.

**Approach**: Done iteratively in-branch rather than parallel worktrees. Two codex reviews identified propagation gaps (platformInfo parsed at CLI but not forwarded through all execution paths); both rounds of fixes completed with targeted regression tests.

### Sub-task 1A: Platform modeling + CLI ✅

Create the type contracts defined in **Appendix A** below. This sub-task must complete before 1B/1C/1D start, since they all depend on these types.

- `ApplePlatform` enum, `SliceVariant` record, `PlatformInfo` record, `PlatformInfoFactory` static class — all in `src/Swift.Bindings/src/Configuration/`
- Add `--platform` CLI option to `Program.cs` (default: `ios`; accepts `ios`, `macos`, `tvos`, `maccatalyst`)
  - Keep `--platform-target` for simulator/device selection within a platform
  - Validate combinations (macOS/Catalyst reject `--platform-target simulator`)
- Wire `PlatformInfo` into `BindingGeneratorOptions`

### Sub-task 1B: XCFramework slice selection ✅

- Update `SelectSlice()` in `XCFrameworkResolver.cs` to filter by `ApplePlatform` dynamically instead of hardcoded `"ios"` + explicit `maccatalyst` exclusion
- Update `ResolveAll()` to resolve slices for the target platform (not iOS-only)
- Platform-aware error messages ("No macOS slice found" etc.)
- Handle Catalyst slices (currently explicitly excluded)

### Sub-task 1C: Compiler + extractors ✅

- `SwiftWrapperCompiler.cs`: Use `SliceVariant` for SDK name, target triple, slice directory naming, plist variant. The existing simulator/device dual-compile flow maps naturally to iterating over the platform's slice variants.
- `SymbolGraphExtractor.cs`: SDK selection from `SliceVariant`
- `ClangAstInvoker.cs`: SDK selection from `SliceVariant` (ObjC clang AST dump)

### Sub-task 1D: Project/package emission ✅

Both Swift and ObjC emitters:
- `BindingProjectEmitter.cs`: TFM, RID paths, package ID suffix from `PlatformInfo`
- `ObjCBindingProjectEmitter.cs`: Same — `net10.0-ios` → `PlatformInfo.Tfm`, `{Module}.ObjC.iOS` → `{Module}.ObjC.{PlatformInfo.PackageSuffix}`
- `ConsumerTargetsEmitter.cs`: Dynamic NativeReference paths from `PlatformInfo.Rid`
- `ObjCAvailabilityEmitter.cs`: Emit platform-appropriate `[Introduced]`/`[Deprecated]` attributes (currently filters to iOS-only, discards tvOS/macOS availability data)

### Sub-task 1E: Tests + smoke validation ✅

- Unit tests for `PlatformInfo` / `SliceVariant` (all platform × simulator combinations produce correct derived values)
- Unit tests for `XCFrameworkResolver` with non-iOS slices (mock xcframework metadata)
- Unit tests for wrapper compiler SDK/triple selection per platform
- Unit tests for emitted TFM/RID paths per platform (both Swift and ObjC emitters)
- `ShouldCompileWrapperPlatformTests` (4 tests): macOS/Catalyst always compile regardless of wrapperArchitectures
- `BinaryDependencyAnalyzerPlatformTests` (2 tests): platformInfo acceptance and iOS fallback
- `SymbolGraphExtractorPlatformTests` (5 tests): architecture override, default arm64, cross-platform SDK/triple
- **Smoke generation**: deferred to Session 2 (requires SDK TFM detection for end-to-end `dotnet build`)

### Key files modified

- `src/Swift.Bindings/src/Configuration/ApplePlatform.cs` (new)
- `src/Swift.Bindings/src/Configuration/SliceVariant.cs` (new)
- `src/Swift.Bindings/src/Configuration/PlatformInfo.cs` (new)
- `src/Swift.Bindings/src/Configuration/PlatformInfoFactory.cs` (new)
- `src/Swift.Bindings/src/Program.cs` — threaded `platformInfo` to all resolver, compiler, emitter, and analyzer calls; updated `ShouldCompileWrapper` for platforms without simulator
- `src/Swift.Bindings/src/Configuration/XCFrameworkResolver.cs`
- `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs`
- `src/Swift.Bindings/src/Configuration/SymbolGraphExtractor.cs` — architecture override from `resolution.SelectedArchitecture`
- `src/Swift.Bindings/src/Configuration/BinaryDependencyAnalyzer.cs` — all 9 internal resolver calls receive `platformInfo`
- `src/Swift.Bindings/src/Configuration/XCFrameworkMetadataExtractor.cs`
- `src/Swift.Bindings/src/ObjC/Parser/ClangAstInvoker.cs`
- `src/Swift.Bindings/src/ObjC/Pipeline/ObjCPipeline.cs`
- `src/Swift.Bindings/src/ObjC/Emitter/ApiDefinitionEmitter.cs`
- `src/Swift.Bindings/src/ObjC/Emitter/StructsAndEnumsEmitter.cs`
- `src/Swift.Bindings/src/Emitter/BindingProjectEmitter.cs`
- `src/Swift.Bindings/src/Emitter/ConsumerTargetsEmitter.cs`
- `src/Swift.Bindings/src/ObjC/Emitter/ObjCBindingProjectEmitter.cs`
- `src/Swift.Bindings/src/ObjC/Emitter/ObjCAvailabilityEmitter.cs`
- `src/Swift.Bindings/tests/UnitTests/ConfigurationTests/PlatformInfoTests.cs` — 11 targeted regression tests

---

## Session 2: MSBuild SDK + Template + Docs/Test Sweep + Validation ✅

**Status**: Complete (March 8, 2026)
**Validation**: 6628 unit tests pass, 88/88 validation targets pass (iOS unchanged), macOS/tvOS/MacCatalyst generation verified end-to-end with FirebaseCore.

**Goal**: Make the SDK and template platform-aware, sweep hardcoded `net10.0-ios` assumptions from tests and docs, and validate end-to-end with real xcframeworks on macOS.

### Sub-task 2A: MSBuild SDK ✅

- `Sdk.props`: Auto-detect platform from `$(TargetFramework)` via `_SwiftBindingPlatform`
  - TFM → platform: `maccatalyst` (checked first), `tvos`, `macos`, `ios` — all explicit `Contains()` checks
  - Unsupported TFMs (`net10.0`, `net10.0-android`, typos): `_SwiftBindingPlatformUnsupported=true`, no dangling fallback — `_SwiftBindingPlatform` stays empty so invalid state is visible throughout
  - `_SwiftBindingHasSimulatorSlice` — true for iOS/tvOS only
  - `_SwiftBindingNuGetRid` — platform-specific RID (`ios-arm64`, `osx-arm64`, `tvos-arm64`, `maccatalyst-arm64`)
  - `_SwiftBindingDeviceSliceId`/`_SwiftBindingSimulatorSliceId` — platform-specific slice directory names
  - `SwiftPlatformTarget` defaults to `simulator` only for platforms with simulator variants
- `Sdk.targets`:
  - SWIFTBIND010: fail-fast error for unsupported TFMs (fires before discovery)
  - Generator invocation: pass `--platform $(_SwiftBindingPlatform)`, conditional `--platform-target`
  - NuGet pack layout: `buildTransitive/$(TargetFramework)/` and `runtimes/$(_SwiftBindingNuGetRid)/native/`
  - SWIFTBIND030: only fires for platforms with simulator slices
  - SWIFTBIND031: split into dual-slice (iOS/tvOS) and single-slice (macOS/Catalyst) validation
  - Fingerprint includes `$(_SwiftBindingPlatform)`

### Sub-task 2B: Template ✅

- `template.json`: `--platform` choice parameter (ios/macos/tvos/maccatalyst), `tfm` generated switch symbol
- `ProjectName.csproj`: XML comment showing `--platform` alternatives
- `Swift.Bindings.Templates.csproj`: PackageTags updated with all platforms
- Classifications updated from `["iOS", ...]` to `["Apple", ...]`

### Sub-task 2C: Test sweep ✅

- `SdkPropsTargetsTests.cs`: Updated `Targets_ConfiguresPackLayout` and `Targets_PackLayoutIncludesModuleDatabase` to assert dynamic paths (`$(TargetFramework)`, `$(_SwiftBindingNuGetRid)`) instead of hardcoded iOS
- Added 10 new tests: platform auto-detection, maccatalyst-before-macos ordering, NuGet RID per platform, slice IDs, simulator slice conditional, platform target conditional, generator platform arg, fingerprint includes platform, single-slice validation, unsupported TFM flag (props), SWIFTBIND010 error (targets)
- `docs/Customization.md`: Added `--platform` CLI option, updated `SwiftPlatformTarget` default to note macOS/Catalyst exception

### Sub-task 2D: macOS end-to-end validation ✅

- Tested FirebaseCore.xcframework with `--platform macos`, `--platform tvos`, `--platform maccatalyst`
- All three produce correct TFM/PackageId: `net10.0-macos`/`FirebaseCore.ObjC.macOS`, `net10.0-tvos`/`FirebaseCore.ObjC.tvOS`, `net10.0-maccatalyst`/`FirebaseCore.ObjC.MacCatalyst`
- `validate-libraries.sh` tier 1 (34/34) and tier 2 (54/54) pass — iOS path unchanged

### Key files modified

- `src/Swift.Bindings.Sdk/Sdk/Sdk.props` — platform auto-detection, NuGet RID, slice ID properties, unsupported TFM flag
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` — SWIFTBIND010, dynamic pack paths, platform-aware validation, generator `--platform` arg
- `src/Swift.Bindings.Templates/content/swift-binding/.template.config/template.json` — `--platform` parameter
- `src/Swift.Bindings.Templates/content/swift-binding/ProjectName.csproj` — comment
- `src/Swift.Bindings.Templates/Swift.Bindings.Templates.csproj` — PackageTags
- `src/Swift.Bindings/tests/UnitTests/SdkTests/SdkPropsTargetsTests.cs` — updated + 10 new tests
- `docs/Customization.md` — `--platform` CLI option, updated `SwiftPlatformTarget` docs

---

## Session 3: Runtime Gaps + Test Infrastructure

**Goal**: Fix runtime native asset gaps (Catalyst, tvOS), multi-platform test runner, and framework database audit.

### Sub-task 3A: Runtime — Catalyst + tvOS native assets (single agent)

The runtime `.csproj` already multi-targets `net10.0-maccatalyst`, but **no native dylib is injected for Catalyst** — only macOS, iOS device, and iOS simulator have `<Content>` items. This gap exists in **two places** that must both be fixed:

1. **`Swift.Runtime.csproj`** (ProjectReference path) — `<Content>` items for local dev/build
2. **`Swift.Runtime.targets`** (NuGet consumer path) — `<Content>` items that flow to NuGet consumers via `buildTransitive/`

Today `Swift.Runtime.targets` only has three `<ItemGroup>` blocks: macOS (non-ios, non-maccatalyst), iOS device (`ios-` RID), and iOS simulator (`iossimulator-` RID). Without matching blocks for Catalyst and tvOS, NuGet consumers on those platforms silently get no native dylib at runtime.

Changes:
- Add Catalyst `<Content>` items in both `.csproj` and `.targets`
  - Catalyst shares the macOS dylib (same underlying Darwin, different SDK) — verify or build separately
  - `.targets` condition: `$(TargetFramework.Contains('maccatalyst'))`
- Add tvOS `<Content>` items in both `.csproj` and `.targets` (conditional on workload availability)
  - `net10.0-tvos` TFM: add when .NET 10 tvOS workload ships, or prep with conditional `<TargetFrameworks>` include
  - `.targets` conditions for tvOS device (`tvos-` RID) and tvOS simulator (`tvossimulator-` RID)
- Verify `#if TVOS` / `#if MACCATALYST` guards compile correctly across all TFMs
- Build native runtime dylib for each new platform slice

### Sub-task 3B: Build scripts (can parallel with 3A)

- `TestFramework/build-xcframework.sh`: Add `--platform` flag
  - Map to correct SDK (`iphonesimulator`/`iphoneos`/`macosx`/`appletvos`/`appletvsimulator`)
  - Map to correct target triple
  - Generate correct slice directory names
- `TestFramework/build-bridge.sh`: Same platform parameterization
- `TestFramework/build-async-wrapper.sh`: Same

### Sub-task 3C: Test runner (can parallel with 3A, 3B)

- `TestFramework/run-runtime-tests.sh`: Add `--platform` flag (default: `ios`)
  - macOS path: build as console app, run directly (no simulator needed)
  - tvOS path: use tvOS simulator via `xcrun simctl`
  - Catalyst path: build as Catalyst app, run on Mac
  - Keep iOS simulator path as-is
- **Extend existing `NativeAotTestApp.Mac`** for macOS runtime testing (not a new project — it already exists at `TestFramework/NativeAotTestApp.Mac/`)
  - Add the standard runtime test suite alongside existing NativeAOT blocker tests
  - Or create a lightweight `RuntimeTestsApp.Mac` that reuses test classes
- Update `run-tests.sh` to optionally run on non-iOS platforms

### Sub-task 3D: Framework databases (can parallel with 3A, 3B, 3C)

- Audit `AppleFrameworkRegistry` for platform-conditional frameworks
  - UIKit types available on tvOS (subset — no `UIWebView`, no `UIBarButtonItem`, etc.)
  - AppKit types only on macOS (and partially on Catalyst via compatibility layer)
  - Some frameworks are platform-exclusive: TVMLKit (tvOS), WatchKit (watchOS)
- Add platform annotations if any types need conditional suppression
- Verify ObjC type databases work across platforms (most Apple ObjC frameworks are cross-platform)

### Parallelization

```
Agent 1 (main):     3A — Runtime Catalyst + tvOS native assets
Agent 2 (worktree): 3B — Build scripts
Agent 3 (worktree): 3C — Test runner + extend NativeAotTestApp.Mac
Agent 4 (worktree): 3D — Framework database audit
── merge ──
Final:              Integration test: build + run TestFramework on macOS
```

**Key files**:
- `src/Swift.Runtime/src/Swift.Runtime.csproj`
- `src/Swift.Runtime/src/build/Swift.Runtime.targets`
- `src/Swift.Runtime/src/Swift/UIImage.cs`
- `src/Swift.Runtime/src/Swift/NSImage.cs`
- `TestFramework/build-xcframework.sh`
- `TestFramework/build-bridge.sh`
- `TestFramework/build-async-wrapper.sh`
- `TestFramework/run-runtime-tests.sh`
- `TestFramework/NativeAotTestApp.Mac/`
- `run-tests.sh`

---

## Validation Criteria

Each session should end with these gates passing:

| Session | Gate |
|---------|------|
| 1 | `run-tests.sh` passes (existing iOS tests unbroken). New platform-specific unit tests pass for all 4 platforms. Smoke generation with `--platform macos` produces output with correct TFM/RID/paths. Both Swift and ObjC xcframework inputs tested. |
| 2 | ✅ SDK auto-detects platform from TFM. FirebaseCore (ObjC) generates correctly for macOS, tvOS, and MacCatalyst. `validate-libraries.sh` 88/88 for iOS (no regression). 6628 unit tests pass. Unsupported TFMs fail fast (SWIFTBIND010). |
| 3 | Runtime loads native dylib on Catalyst. TestFramework builds and runtime tests pass on macOS natively (via extended `NativeAotTestApp.Mac` or new console test app). iOS test path unchanged. tvOS: validated end-to-end when .NET 10 tvOS workload is available; until then, compile/pack logic only (unit-tested but no runtime gate). |

---

## ObjC-Specific Considerations

The ObjC pipeline shares most platform-hardcoded code with the Swift pipeline (XCFrameworkResolver, wrapper compiler), but has additional ObjC-only concerns:

| Component | Current State | Multi-platform change |
|-----------|---------------|----------------------|
| `ClangAstInvoker` | iOS SDK hardcoded for clang AST dump | Use `SliceVariant.SdkName` |
| `ObjCAvailabilityEmitter` | Filters to `platform == "ios"`, emits only `PlatformName.iOS` | Map platform strings to correct `PlatformName` enum; emit per-target-platform attributes |
| `ObjCBindingProjectEmitter` | `net10.0-ios` TFM, `.ObjC.iOS` package ID | Use `PlatformInfo.Tfm` / `PlatformInfo.PackageSuffix` |
| `ClangAstParser` | JSON parser only — no platform-specific logic | No change needed (SDK selection is in `ClangAstInvoker`, covered above) |
| `ObjCAvailability` model | Platform is a string (good — already flexible) | No change needed |
| Validation libraries | ObjC libs (Realm, Stripe3DS2) are `mode: "manual"` | Add macOS ObjC validation targets if available |

---

## Open Questions

1. **Multi-platform NuGet**: Single package with all platform slices, or one package per platform? Per-platform is simpler for v1. Multi-platform is a future enhancement.
2. **tvOS workload**: .NET 10 tvOS workload availability timeline — may need to defer tvOS runtime TFM.
3. **visionOS**: Include now or leave for later? xrOS/visionOS .NET workload status unclear.
4. **x86_64 macOS**: Support Intel Macs or arm64-only? Most xcframeworks are arm64-only now.
5. **Catalyst dylib**: Does Catalyst reuse the macOS dylib or need a separate build? Same Darwin kernel but different SDK linkage.

---

## Appendix A: Session 1 Type Contract Specification

This spec defines the exact types that Sub-task 1A creates. Sub-tasks 1B, 1C, and 1D code against these contracts in parallel worktrees.

### Platform String Reference Table

| Domain | iOS | macOS | tvOS | Mac Catalyst |
|--------|-----|-------|------|--------------|
| **Plist SupportedPlatform** | `"ios"` | `"macos"` | `"tvos"` | `"ios"` |
| **Plist SupportedPlatformVariant** | `null` / `"simulator"` | `null` | `null` / `"simulator"` | `"maccatalyst"` |
| **xcrun SDK (simulator)** | `"iphonesimulator"` | `"macosx"` | `"appletvsimulator"` | `"macosx"` |
| **xcrun SDK (device)** | `"iphoneos"` | `"macosx"` | `"appletvos"` | `"macosx"` |
| **Target triple (sim)** | `arm64-apple-ios{v}-simulator` | `arm64-apple-macos{v}` | `arm64-apple-tvos{v}-simulator` | `arm64-apple-ios{v}-macabi` |
| **Target triple (device)** | `arm64-apple-ios{v}` | `arm64-apple-macos{v}` | `arm64-apple-tvos{v}` | `arm64-apple-ios{v}-macabi` |
| **Slice ID (sim)** | `ios-arm64-simulator` | `macos-arm64` | `tvos-arm64-simulator` | `ios-arm64-maccatalyst` |
| **Slice ID (device)** | `ios-arm64` | `macos-arm64` | `tvos-arm64` | `ios-arm64-maccatalyst` |
| **Plist CFBundleSupportedPlatforms (sim)** | `iPhoneSimulator` | `MacOSX` | `AppleTVSimulator` | `MacOSX` |
| **Plist CFBundleSupportedPlatforms (device)** | `iPhoneOS` | `MacOSX` | `AppleTVOS` | `MacOSX` |
| **.NET TFM** | `net10.0-ios` | `net10.0-macos` | `net10.0-tvos` | `net10.0-maccatalyst` |
| **NuGet RID** | `ios-arm64` | `osx-arm64` | `tvos-arm64` | `maccatalyst-arm64` |
| **Package suffix (Swift)** | `.Swift.iOS` | `.Swift.macOS` | `.Swift.tvOS` | `.Swift.MacCatalyst` |
| **Package suffix (ObjC)** | `.ObjC.iOS` | `.ObjC.macOS` | `.ObjC.tvOS` | `.ObjC.MacCatalyst` |
| **ObjCRuntime PlatformName** | `iOS` | `MacOSX` | `TvOS` | `MacCatalyst` |
| **Has simulator variant** | Yes | No | Yes | No |
| **Default min OS** | `"15.0"` | `"12.0"` | `"15.0"` | `"15.0"` |

Key observations:
- **macOS** has no simulator — single slice, SDK is always `"macosx"`.
- **Mac Catalyst** uses `SupportedPlatform="ios"` with `SupportedPlatformVariant="maccatalyst"` in xcframework plists. Its SDK is `"macosx"` and its triple uses `ios-macabi`. No simulator/device distinction.
- **tvOS** mirrors the iOS pattern with simulator/device.

### Type 1: `ApplePlatform` Enum

File: `src/Swift.Bindings/src/Configuration/ApplePlatform.cs`

```csharp
namespace BindingsGeneration
{
    public enum ApplePlatform
    {
        iOS,
        macOS,
        tvOS,
        MacCatalyst,
    }
}
```

### Type 2: `SliceVariant` Record

File: `src/Swift.Bindings/src/Configuration/SliceVariant.cs`

Per-slice properties for a specific build target (e.g., "iOS Simulator" or "macOS Device"). Replaces the hardcoded SDK/triple/slice strings scattered across the compiler and resolver.

```csharp
namespace BindingsGeneration
{
    public sealed record SliceVariant
    {
        public required ApplePlatform Platform { get; init; }
        public required bool IsSimulator { get; init; }

        /// <summary>xcrun SDK name: "iphonesimulator", "iphoneos", "macosx", etc.</summary>
        public required string SdkName { get; init; }

        /// <summary>Architecture (always "arm64" for now).</summary>
        public string Architecture { get; init; } = "arm64";

        /// <summary>xcframework slice directory: "ios-arm64-simulator", "macos-arm64", etc.</summary>
        public required string SliceId { get; init; }

        /// <summary>CFBundleSupportedPlatforms plist value: "iPhoneSimulator", "MacOSX", etc.</summary>
        public required string PlistPlatformName { get; init; }

        /// <summary>xcframework Info.plist SupportedPlatform: "ios", "macos", "tvos".</summary>
        public required string XCFrameworkPlatformString { get; init; }

        /// <summary>xcframework Info.plist SupportedPlatformVariant: "simulator", "maccatalyst", or null.</summary>
        public string? XCFrameworkPlatformVariant { get; init; }

        /// <summary>
        /// Build the swiftc/swift-frontend target triple.
        /// Example: "arm64-apple-ios17.0-simulator", "arm64-apple-macos12.0".
        /// </summary>
        public string GetTargetTriple(string minOSVersion)
        {
            return Platform switch
            {
                ApplePlatform.iOS when IsSimulator   => $"{Architecture}-apple-ios{minOSVersion}-simulator",
                ApplePlatform.iOS                    => $"{Architecture}-apple-ios{minOSVersion}",
                ApplePlatform.macOS                  => $"{Architecture}-apple-macos{minOSVersion}",
                ApplePlatform.tvOS when IsSimulator   => $"{Architecture}-apple-tvos{minOSVersion}-simulator",
                ApplePlatform.tvOS                   => $"{Architecture}-apple-tvos{minOSVersion}",
                ApplePlatform.MacCatalyst             => $"{Architecture}-apple-ios{minOSVersion}-macabi",
                _ => throw new ArgumentOutOfRangeException(nameof(Platform)),
            };
        }

        public string DisplayName => IsSimulator ? $"{Platform} Simulator" : $"{Platform} Device";
    }
}
```

### Type 3: `PlatformInfo` Record

File: `src/Swift.Bindings/src/Configuration/PlatformInfo.cs`

Platform-level composition. Holds TFM, NuGet RID, package naming, and references to its 1-2 slice variants. Does NOT carry a single `Rid`/`SlicePrefix` — those live on `SliceVariant`.

```csharp
namespace BindingsGeneration
{
    public sealed record PlatformInfo
    {
        public required ApplePlatform Platform { get; init; }

        /// <summary>"net10.0-ios", "net10.0-macos", etc.</summary>
        public required string Tfm { get; init; }

        /// <summary>NuGet RID for native pack paths: "ios-arm64", "osx-arm64", etc.</summary>
        public required string NuGetRid { get; init; }

        /// <summary>".Swift.iOS", ".Swift.macOS", etc.</summary>
        public required string SwiftPackageIdSuffix { get; init; }

        /// <summary>".ObjC.iOS", ".ObjC.macOS", etc.</summary>
        public required string ObjCPackageIdSuffix { get; init; }

        /// <summary>ObjCRuntime.PlatformName enum value name: "iOS", "MacOSX", "TvOS", "MacCatalyst".</summary>
        public required string ObjCRuntimePlatformName { get; init; }

        /// <summary>Plist SupportedPlatform for xcframework filtering: "ios", "macos", "tvos".</summary>
        public required string PlistPlatformString { get; init; }

        /// <summary>ObjC availability annotation platform: "ios", "macos", "tvos", "maccatalyst".</summary>
        public required string AvailabilityPlatformString { get; init; }

        /// <summary>Default minimum OS version fallback.</summary>
        public required string DefaultMinimumOS { get; init; }

        /// <summary>Whether this platform has distinct simulator and device slices.</summary>
        public required bool HasSimulatorVariant { get; init; }

        /// <summary>Simulator slice, or null for macOS/Catalyst.</summary>
        public SliceVariant? SimulatorSlice { get; init; }

        /// <summary>Device slice. Always non-null. For macOS/Catalyst, this is the only slice.</summary>
        public required SliceVariant DeviceSlice { get; init; }

        public SliceVariant GetSlice(bool isSimulator) =>
            (isSimulator && SimulatorSlice != null) ? SimulatorSlice : DeviceSlice;

        public IReadOnlyList<SliceVariant> AllSlices =>
            SimulatorSlice != null ? new[] { SimulatorSlice, DeviceSlice } : new[] { DeviceSlice };

        public string GetDefaultSwiftPackageId(string moduleName) => $"{moduleName}{SwiftPackageIdSuffix}";
        public string GetDefaultObjCPackageId(string moduleName) => $"{moduleName}{ObjCPackageIdSuffix}";
        public string GetNativePackPath(string frameworkName) => $"runtimes/{NuGetRid}/native/{frameworkName}/";
        public string GetBuildTransitivePath() => $"buildTransitive/{Tfm}/";
    }
}
```

### Type 4: `PlatformInfoFactory` Static Class

File: `src/Swift.Bindings/src/Configuration/PlatformInfoFactory.cs`

Single source of truth for all per-platform constants. See the reference table above for all values.

```csharp
namespace BindingsGeneration
{
    public static class PlatformInfoFactory
    {
        /// <summary>Create PlatformInfo for a given platform.</summary>
        public static PlatformInfo Create(ApplePlatform platform) => platform switch
        {
            ApplePlatform.iOS => CreateiOS(),
            ApplePlatform.macOS => CreatemacOS(),
            ApplePlatform.tvOS => CreatetvOS(),
            ApplePlatform.MacCatalyst => CreateMacCatalyst(),
            _ => throw new ArgumentOutOfRangeException(nameof(platform)),
        };

        /// <summary>
        /// Parse platform from CLI string. Accepts: "ios", "macos", "tvos", "maccatalyst".
        /// Returns null for unrecognized input.
        /// </summary>
        public static ApplePlatform? ParsePlatform(string? value) => value?.ToLowerInvariant() switch
        {
            "ios" or null => ApplePlatform.iOS,
            "macos" => ApplePlatform.macOS,
            "tvos" => ApplePlatform.tvOS,
            "maccatalyst" or "mac-catalyst" => ApplePlatform.MacCatalyst,
            _ => null,
        };

        /// <summary>
        /// Detect platform from xcframework plist SupportedPlatform/SupportedPlatformVariant.
        /// </summary>
        public static ApplePlatform DetectFromPlistPlatform(
            string supportedPlatform, string? supportedPlatformVariant)
        {
            if (string.Equals(supportedPlatformVariant, "maccatalyst", StringComparison.OrdinalIgnoreCase))
                return ApplePlatform.MacCatalyst;
            return supportedPlatform.ToLowerInvariant() switch
            {
                "ios" => ApplePlatform.iOS,
                "macos" => ApplePlatform.macOS,
                "tvos" => ApplePlatform.tvOS,
                _ => ApplePlatform.iOS,
            };
        }

        // Private factory methods — one per platform.
        // Each constructs the PlatformInfo with its SliceVariants using
        // the exact string values from the reference table above.
        // See full implementations in the source files.
    }
}
```

### Consumer Site Migration Summary

Each consumer replaces hardcoded strings with the appropriate property:

| Consumer | Before | After |
|----------|--------|-------|
| `XCFrameworkResolver.SelectSlice()` | `s.SupportedPlatform == "ios"` | `s.SupportedPlatform == platformInfo.PlistPlatformString` + Catalyst variant check |
| `SwiftWrapperCompiler.CompileSlice()` | `"iphonesimulator"`, `"arm64-apple-ios{v}-simulator"` | `slice.SdkName`, `slice.GetTargetTriple(minOS)` |
| `SwiftWrapperCompiler` slice dirs | `"ios-arm64-simulator"`, `"ios-arm64"` | `slice.SliceId` |
| `SwiftWrapperCompiler` plist | `"iPhoneSimulator"`, `"iPhoneOS"` | `slice.PlistPlatformName` |
| `SymbolGraphExtractor` | `isSimulator ? "iphonesimulator" : "iphoneos"` | `platformInfo.GetSlice(isSimulator).SdkName` |
| `ClangAstInvoker` | `isSimulator ? "iphonesimulator" : "iphoneos"` | `slice.SdkName` |
| `BindingProjectEmitter` TFM | `"net10.0-ios"` | `platformInfo.Tfm` |
| `BindingProjectEmitter` pack paths | `"runtimes/ios-arm64/native/"` | `platformInfo.GetNativePackPath(...)` |
| `BindingProjectEmitter` package ID | `$"{module}.Swift.iOS"` | `platformInfo.GetDefaultSwiftPackageId(module)` |
| `ConsumerTargetsEmitter` | `"runtimes/ios-arm64/native/"` | `platformInfo.NuGetRid` |
| `ObjCBindingProjectEmitter` TFM | `"net10.0-ios"` | `platformInfo.Tfm` |
| `ObjCBindingProjectEmitter` pkg ID | `$"{module}.ObjC.iOS"` | `platformInfo.GetDefaultObjCPackageId(module)` |
| `ObjCAvailabilityEmitter` | `avail.Platform == "ios"`, `PlatformName.iOS` | `avail.Platform == platformInfo.AvailabilityPlatformString`, `PlatformName.{platformInfo.ObjCRuntimePlatformName}` |
| `Program.cs` default pkg ID | `$"{module}.Swift.iOS"` | `platformInfo.GetDefaultSwiftPackageId(module)` |
| `FrameworkDependencyInfo` | `$"{ModuleName}.Swift.iOS"` | `platformInfo.GetDefaultSwiftPackageId(ModuleName)` (method, not property) |

### Agent Dependency Boundaries

```
Agent 1A creates: ApplePlatform, SliceVariant, PlatformInfo, PlatformInfoFactory
                  (leaf types — no dependencies on existing code)
                  Also: --platform CLI option, wire into BindingGeneratorOptions

Agent 1B consumes: PlatformInfo (for SelectSlice, ResolveAll filtering)
                   SliceVariant (for XCFrameworkPlatformVariant matching)
                   Touches: XCFrameworkResolver.cs only

Agent 1C consumes: SliceVariant (for SdkName, GetTargetTriple, SliceId, PlistPlatformName)
                   PlatformInfo (for GetSlice, AllSlices)
                   Touches: SwiftWrapperCompiler.cs, SymbolGraphExtractor.cs, ClangAstInvoker.cs

Agent 1D consumes: PlatformInfo (for Tfm, NuGetRid, GetDefaultSwiftPackageId,
                   GetDefaultObjCPackageId, GetNativePackPath, GetBuildTransitivePath,
                   AvailabilityPlatformString, ObjCRuntimePlatformName)
                   Touches: BindingProjectEmitter.cs, ConsumerTargetsEmitter.cs,
                   ObjCBindingProjectEmitter.cs, ObjCAvailabilityEmitter.cs

No agent touches another agent's files. All depend only on Agent 1A's types.
```
