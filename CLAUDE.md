# Claude Code Guide for Swift Bindings

## Project Overview

Experimental Swift/.NET interop project. Generates C# bindings from compiled Swift libraries (.dylib + ABI JSON) for .NET 10.0 on Apple platforms. Originally Microsoft, now maintained by Justin Wojciechowski. MIT License.

## Repository Structure

- `src/Swift.Bindings/src/` — Generator: Parser → TypeDatabase → Marshaler → Emitter
- `src/Swift.Bindings.Sdk/` — MSBuild SDK package (`Swift.Bindings.Sdk`): `Sdk.props`, `Sdk.targets`, build scripts
- `src/Swift.Bindings.Templates/` — `dotnet new swift-binding` project template
- `src/Swift.Runtime/src/Swift/` — Runtime: SwiftString, SwiftArray, SafeHandle, ARC (NuGet: `Swift.Runtime 0.1.0-preview.1`)
- `TestFramework/` — Comprehensive test library + runtime tests (iOS Simulator)
- `validation-libraries.json` — Library validation manifest (32 targets across 19 libraries)
- `scripts/` — `fetch-libraries.sh` (build xcframeworks), `lib.sh` (shared helpers)
- `src/docs/` — Design docs, status, known issues
- `docs/` — High-level philosophy (`binding-overview.md`)

## Building & Testing

**Always use helper scripts, not raw commands.**

**IMPORTANT: `./run-tests.sh` takes ~2 minutes. When running it, ALWAYS capture enough output in a single invocation. Use `| tail -20` (not `tail -5`). NEVER run it twice to get different slices of the output.**

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
./validate-libraries.sh                 # Compile gate (all libraries)
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

**Output files:**
- `Swift.{Module}.cs` — main C# bindings
- `Swift.{Module}.swift` — Swift wrapper functions
- `{Module}SwiftBindings.xcframework/` — compiled Swift wrapper (module-unique name)
- `{Module}.Swift.iOS.csproj` — ready-to-build project (references `Swift.Runtime` NuGet)
- `{Module}.Swift.iOS.targets` — NuGet consumer targets
- `binding-metadata.json` / `binding-metadata.props` — extracted metadata

**Compiling the generated bindings to verify correctness:**
```bash
cd /path/to/output && dotnet build {Module}.Swift.iOS.csproj -p:EnableDefaultCompileItems=false
```
Note: `-p:EnableDefaultCompileItems=false` is needed because the generated `.csproj` explicitly lists `<Compile>` items but the .NET SDK also auto-includes `*.cs` (known issue — the SDK mode avoids this).

**All CLI options:**

| Option | Description |
|--------|-------------|
| `--xcframework <path>` | Path to xcframework (auto-resolves all inputs) |
| `--platform-target <target>` | `simulator` (default) or `device` |
| `-o, --output <path>` | Output directory (required) |
| `-l, --library-name <name>` | Runtime library name for DllImport |
| `--async-library <name>` | Library name for async wrapper functions |
| `-s, --swiftinterface <path>` | `.swiftinterface` for `@inlinable internal` detection |
| `--symbolgraph <path>` | Symbol graph JSON for C# XML doc comments |
| `--bridge-hints <path>` | Bridge hints JSON for SwiftUI bridge customization |
| `--namespace-pattern <pattern>` | C# namespace (supports `{Module}`, `{Framework}`) |
| `--sdk-mode` | Skips `.csproj` emission (used when the MSBuild SDK is the project system) |
| `--package-id <id>` | NuGet package ID override |
| `--wrapper-architectures <scope>` | `simulator`, `device`, or `all` |
| `--framework-dependency <path>` | Dependency xcframework path (repeatable). Adds `-F` search paths for wrapper compilation and `PackageReference` in emitted `.csproj`. Requires `--xcframework`. |
| `-v, --verbose <level>` | 0=silent, 1=normal, 2=debug |

### Manual mode (original)

For when you need fine-grained control over individual inputs:
```bash
dotnet run --project src/Swift.Bindings/src -- \
  -a path/to/abi.json -d path/to/dylib -t path/to/file.tbd \
  -o output/ -l LibraryName --async-library SwiftBindings
```
Mutually exclusive with `--xcframework`. Does NOT emit `.csproj`/`.targets`.

## Validating Third-Party Libraries

Track binding errors in `src/docs/Completed/binding-errors.md`. All 31 validation libraries are declared in `validation-libraries.json`.

### Quick start

```bash
# First time: fetch all public libraries (~30-60 min, builds xcframeworks)
scripts/fetch-libraries.sh

# Run compile gate
./validate-libraries.sh

# Validate a single library
./validate-libraries.sh --filter Nuke --verbose

# Fetch + validate in one command
./validate-libraries.sh --fetch --filter Nuke
```

### Validation profiles

- **public** (27 targets): Auto-fetchable via SPM. Any contributor can run `scripts/fetch-libraries.sh`.
- **full** (31 targets): Includes 4 proprietary/manual libraries (BRLMPrinterKit, Mappedin, MicroblinkPlatform, SmartCardIO). Place xcframeworks in `.libraries/<name>/`.

### Adding a new library

1. Add entry to `validation-libraries.json` (repo URL, version, mode)
2. `scripts/fetch-libraries.sh --filter NewLib`
3. `./validate-libraries.sh --filter NewLib`
4. Run full validation to update `.validation-baseline.json`

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
<Project Sdk="Swift.Bindings.Sdk/0.1.0-preview.1">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
  </PropertyGroup>
</Project>
```

The SDK auto-discovers `*.xcframework` in the project directory, runs the generator, compiles the Swift wrapper, and arranges NuGet pack layout. See `docs/Troubleshooting.md` for SWIFTBIND error codes.

**Project with framework dependencies:**
```xml
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

Use `<SwiftFrameworkDependency>` when your library imports another Swift framework. Each item adds a `-F` search path for wrapper compilation and a `<PackageReference>` for NuGet consumers. Both `PackageId` and `PackageVersion` metadata are required for NuGet pack scenarios (SWIFTBIND040 warns if missing).

**Key SDK files:**
- `src/Swift.Bindings.Sdk/Sdk/Sdk.props` — default properties, implicit `Swift.Runtime` reference
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` — 9 build targets (discover → fingerprint → generate → compile → pack)
- `src/Swift.Bindings.Sdk/build-sdk.sh` — publishes generator + packs SDK NuGet

## Validation After Generator Changes

Run after changes to `src/Swift.Bindings/src/{Marshaler,Emitter,Parser,TypeDatabase,Model}/`:
```bash
./run-tests.sh                                                    # Unit tests first
cd TestFramework && ./build-and-test.sh && ./generate-coverage-report.sh  # Then coverage
```

Coverage report shows must-pass features as passing/degraded/missing. Verify no regressions.

## Working Guidelines

- When fixing a bug pattern, grep the entire codebase for ALL instances before finishing.
- After code gen changes, verify generated output compiles — don't assume correctness.
- Use exact file paths verified by reading the filesystem. Don't guess paths.
- Address ALL code review findings in a single pass.
- Use logical/semantic cohesion for refactoring, not arbitrary LOC limits.
- Double-check memory management operations target the correct pointer/object.
- Do NOT commit unless the user explicitly asks.

## Known Runtime Issues

- **Mono JIT assertion (jit-info.c:918)**: Simulator-only. Kills process on closure P/Invoke + SwiftString.PInvoke_GetLength via CallConvSwift. Bridge tests (`@_cdecl`) unaffected. NativeAOT (device builds) is unaffected.
- SafeHandle in async P/Invoke not preserved (workaround: singleton + IntPtr)
- See `src/docs/known-issues-workarounds.md` for full details

## Key References

- `src/docs/roadmap.md` — Path to production-grade (Phases 1-4: Inheritance → Quality → Readiness → Future)
- `src/docs/class-inheritance-implementation.md` — Class inheritance implementation plan (6 sessions)
- `src/docs/Completed/dx-msbuild-sdk-design.md` — MSBuild SDK design (Steps 1-5, all complete)
- `src/docs/Completed/binding-errors.md` — Third-party library binding error tracking
- `src/docs/Future/emitter-redesign-proposal.md` — Architecture direction
- `src/docs/known-issues-workarounds.md` — Runtime workarounds
