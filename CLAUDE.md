# Claude Code Guide for Swift Bindings

## Project Overview

Experimental Swift/.NET interop project. Generates C# bindings from compiled Swift libraries (.dylib + ABI JSON) for .NET 10.0 on Apple platforms. Originally Microsoft, now maintained by Justin Wojciechowski. MIT License.

## Repository Structure

- `src/Swift.Bindings/src/` — Generator: Parser → TypeDatabase → Marshaler → Emitter
- `src/Swift.Bindings.Sdk/` — MSBuild SDK package (`Swift.Bindings.Sdk`): `Sdk.props`, `Sdk.targets`, build scripts
- `src/Swift.Bindings.Templates/` — `dotnet new swift-binding` project template
- `src/Swift.Runtime/src/Swift/` — Runtime: SwiftString, SwiftArray, SafeHandle, ARC (NuGet: `Swift.Runtime`)
- `TestFramework/` — Comprehensive test library + runtime tests (iOS Simulator)
- `validation-libraries.json` — Library validation manifest (32 targets across 19 libraries)
- `scripts/` — `fetch-libraries.sh` (build xcframeworks), `lib.sh` (shared helpers)
- `src/docs/` — Design docs, status, known issues
- `docs/` — High-level philosophy (`binding-overview.md`)

## Building & Testing

**Always use helper scripts, not raw commands.**

**IMPORTANT: Slow commands (`./run-tests.sh` ~2 min, `./build-and-test.sh` ~5 min, `./validate-libraries.sh` ~1 min) — ALWAYS pipe to a temp file with `2>&1 | tee /tmp/<name>-results.txt`. Then use the Read tool on the temp file to inspect results. This avoids re-running slow commands just to see different slices of output. NEVER run a slow command twice.**

```bash
./build.sh                    # Build the project
./run-tests.sh                # Run all unit + integration tests

# TestFramework (after generator changes):
cd TestFramework
./build-and-test.sh           # Full: xcframework + bindings + bridge
./generate-coverage-report.sh # Coverage matrix
./run-runtime-tests.sh --tier 2 --timeout 90  # Runtime on iOS Sim

# Runtime test iteration flags:
#   --skip-regen     Skip binding regeneration (incremental build)
#   --class NAME     Run only one test class
#   --safe-only      Skip [CrashRisk] classes (no Mono JIT crash)

# Real-world library validation:
scripts/fetch-libraries.sh              # Fetch xcframeworks (first time)
./validate-libraries.sh                 # Compile gate (all tiers, 53 targets, ~35s cached)
./validate-libraries.sh --tier 1        # Tier 1 only (32 targets)
./validate-libraries.sh --tier 2        # Tier 2 only (21 targets)
./validate-libraries.sh --filter Nuke   # Validate one library
```

## Generator CLI Usage

The generator (`src/Swift.Bindings/src/`) is a .NET CLI tool with two input modes.

### Recommended: `--xcframework` mode

Takes a single xcframework and auto-resolves all inputs (ABI JSON, dylib, TBD, swiftinterface). Also compiles the Swift wrapper and emits a ready-to-build `.csproj`.

```bash
# From any directory — use dotnet run with the project path:
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework /path/to/Library.xcframework \
  -o /path/to/output/

# Example: generate + compile bindings for Nuke
mkdir -p /tmp/nuke-output
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework .libraries/Nuke/Nuke.xcframework \
  -o /tmp/nuke-output/
```

**Compiling the generated bindings to verify correctness:**
```bash
cd /path/to/output && dotnet build {Module}.Swift.iOS.csproj -p:EnableDefaultCompileItems=false
```
Note: `-p:EnableDefaultCompileItems=false` is needed because the generated `.csproj` explicitly lists `<Compile>` items but the .NET SDK also auto-includes `*.cs` (known issue — the SDK mode avoids this).

All CLI options available via `dotnet run --project src/Swift.Bindings/src -- --help`.

### Manual mode (original)

For when you need fine-grained control over individual inputs:
```bash
dotnet run --project src/Swift.Bindings/src -- \
  -a path/to/abi.json -d path/to/dylib -t path/to/file.tbd \
  -o output/ -l LibraryName --async-library SwiftBindings
```
Mutually exclusive with `--xcframework`. Does NOT emit `.csproj`/`.targets`.

## Validating Third-Party Libraries

Track binding errors in `src/docs/Completed/binding-errors.md`. All validation libraries are declared in `validation-libraries.json`.

### Quick start

```bash
# First time: fetch all public libraries (~30-60 min, builds xcframeworks)
scripts/fetch-libraries.sh

# Run compile gate (tier 1 by default)
./validate-libraries.sh

# Tier 2 (additional coverage libraries)
./validate-libraries.sh --tier 2

# Both tiers
./validate-libraries.sh --tier all

# Validate a single library
./validate-libraries.sh --filter Nuke --verbose

# Fetch + validate in one command
./validate-libraries.sh --fetch --filter Nuke
```

### Validation tiers

- **Tier 1** (32 targets): Established baseline libraries (Alamofire, Nuke, Kingfisher, RxSwift, Stripe, etc.).
- **Tier 2** (21 targets): Additional coverage libraries (DeviceKit, ObjectMapper, SVGView, etc.).
- **Default**: `./validate-libraries.sh` runs all tiers (53 targets, ~35s with cached build). Baseline updates on full unfiltered runs.
- **Manual** (4 targets within tier 1): Proprietary libraries (BRLMPrinterKit, Mappedin, MicroblinkPlatform, SmartCardIO). Place xcframeworks in `.libraries/<name>/`.

### Adding a new library

1. Add entry to `validation-libraries.json` (repo URL, version, mode, tier)
2. `scripts/fetch-libraries.sh --filter NewLib`
3. `./validate-libraries.sh --filter NewLib`
4. Run full tier-1 validation to update `.validation-baseline.json`

### Known non-binding failures (not generator bugs)

- **RealmSwift** — generator crash: ABI JSON has empty module name (not built with `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`)
- **Realm, Stripe3DS2** — pure ObjC frameworks, no Swift module
- **Wrapper compilation failures** (SkeletonView internal types, Mixpanel `#if compiler` types) — C# bindings are correct, but Swift wrapper can't compile without the types. Not dependency-related.
- **Stripe inter-module dependencies** — use `--framework-dependency` to provide each dependency xcframework. See SDK section below for MSBuild equivalent.

## MSBuild SDK (`Swift.Bindings.Sdk`)

The SDK automates the entire workflow into `dotnet build && dotnet pack`. Design doc: `src/docs/Completed/dx-msbuild-sdk-design.md`.

**Binding author workflow:**
```bash
dotnet new swift-binding -n Library.Swift.iOS
cp -r Library.xcframework Library.Swift.iOS/
cd Library.Swift.iOS && dotnet build && dotnet pack
```

**Minimal project file:**
```xml
<Project Sdk="Swift.Bindings.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
  </PropertyGroup>
</Project>
```

The SDK auto-discovers `*.xcframework` in the project directory, runs the generator, compiles the Swift wrapper, and arranges NuGet pack layout. See `docs/Troubleshooting.md` for SWIFTBIND error codes.

**Project with framework dependencies:**
```xml
<Project Sdk="Swift.Bindings.Sdk">
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

Use `<SwiftFrameworkDependency>` when your library imports another Swift framework. Each item adds a `-F` search path for wrapper compilation and a `<PackageReference>` for NuGet consumers. Both `PackageId` and `PackageVersion` metadata are required for NuGet pack scenarios (SWIFTBIND040 warns if missing).

**Key SDK files:**
- `src/Swift.Bindings.Sdk/Sdk/Sdk.props` — default properties, implicit `Swift.Runtime` reference
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` — 9 build targets (discover → fingerprint → generate → compile → pack)
- `src/Swift.Bindings.Sdk/build-sdk.sh` — publishes generator + packs SDK NuGet

## Working Guidelines

- **All work must have tests.** Every session, feature, bug fix, and regression fix must include targeted unit or integration tests that exercise the specific behavior. Library validation passing alone is not sufficient — write tests that would catch a regression if the fix were reverted. If fixing a validation regression, add a test case that reproduces the specific pattern that broke.
- When fixing a bug pattern, grep the entire codebase for ALL instances before finishing.
- After code gen changes, verify generated output compiles — don't assume correctness.
- Use exact file paths verified by reading the filesystem. Don't guess paths.
- Address ALL code review findings in a single pass.
- Use logical/semantic cohesion for refactoring, not arbitrary LOC limits.
- Double-check memory management operations target the correct pointer/object.
- Do NOT commit unless the user explicitly asks.
- `run-tests.sh` is fine to run per sub-task. `validate-libraries.sh`, `build-and-test.sh`, and `golden/check-golden-files.sh` should only run at the end of all sub-tasks or when absolutely needed mid-session.
- NEVER use `git stash` — linter hooks detect reverted files and stash pop discards changes silently.

## Known Runtime Issues

- **Mono JIT assertion (jit-info.c:918)**: Simulator-only. Kills process on closure P/Invoke + SwiftString.PInvoke_GetLength via CallConvSwift. Bridge tests (`@_cdecl`) unaffected. NativeAOT (device builds) is unaffected.
- SafeHandle in async P/Invoke not preserved (workaround: singleton + IntPtr)
- See `src/docs/known-issues-workarounds.md` for full details

## Key References

- `src/docs/roadmap.md` — Master roadmap (sequencing, production readiness, future vision)
- `src/docs/usability-roadmap.md` — Active work: 8 sessions to push all libraries above 4.0
- `src/docs/binding-review-v2.md` — Latest binding quality scores (18 libraries, 10 categories)
- `src/docs/Completed/dx-msbuild-sdk-design.md` — MSBuild SDK design (Steps 1-5, all complete)
- `src/docs/Future/emitter-redesign-proposal.md` — Architecture direction
- `src/docs/known-issues-workarounds.md` — Runtime workarounds
