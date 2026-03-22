# Claude Code Guide for Swift Bindings

## Project Overview

Experimental Swift/.NET interop project. Generates C# bindings from compiled Swift libraries (.dylib + ABI JSON) for .NET 10.0 on Apple platforms. Originally Microsoft, now maintained by Justin Wojciechowski. MIT License.

## Repository Structure

- `src/Swift.Bindings/src/` — Generator: Parser → TypeDatabase → Marshaler → Emitter
- `src/Swift.Bindings.Sdk/` — MSBuild SDK package (`SwiftBindings.Sdk`): `Sdk.props`, `Sdk.targets`, build scripts
- `src/Swift.Bindings.Templates/` — `dotnet new swift-binding` project template
- `src/Swift.Runtime/src/Swift/` — Runtime: SwiftString, SwiftArray, SafeHandle, ARC (NuGet: `SwiftBindings.Runtime`)
- `BindingTests/` — Comprehensive test library + runtime tests (iOS Simulator)
- `validation-libraries.json` — Library validation manifest (90 targets across 46 libraries)
- `scripts/` — `fetch-libraries.sh` (build xcframeworks), `lib.sh` (shared helpers)
- `src/docs/` — Internal design docs, status, known issues
- Public-facing documentation lives in the [GitHub wiki](https://github.com/justinwojo/swift-dotnet-bindings/wiki) (separate repo)

## Building & Testing

**Always use helper scripts, not raw commands.**

**IMPORTANT: Slow commands (`./run-tests.sh` ~2 min, `./build-and-test.sh` ~5 min, `./run-runtime-tests.sh` ~3 min, `./validate-libraries.sh` ~1 min) — ALWAYS pipe to a temp file with `2>&1 | tee /tmp/<name>-results.txt`. Then use the Read tool on the temp file to inspect results. This avoids re-running slow commands just to see different slices of output. NEVER run a slow command twice.**

```bash
./build.sh                    # Build the project
./run-tests.sh                # Run all unit + integration tests

# BindingTests (after generator changes):
cd BindingTests
./build-and-test.sh           # Full: xcframework + bindings + bridge
./generate-coverage-report.sh # Coverage matrix
./run-runtime-tests.sh --timeout 90            # Runtime on iOS Sim (default: simulator)

# Runtime test iteration flags:
#   --skip-regen     Skip binding regeneration (incremental build, ~17s)
#   --skip-build     Skip all builds, just install + run (~5s, use after --skip-regen)
#   --class NAME     Run only one test class

# Real-world library validation:
scripts/fetch-libraries.sh              # Fetch xcframeworks (first time)
./validate-libraries.sh                 # Compile gate (all tiers, 90 targets)
./validate-libraries.sh --tier 1        # Tier 1 only (34 targets)
./validate-libraries.sh --tier 2        # Tier 2 only (54 targets)
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

### ObjC frameworks

Pure ObjC frameworks are auto-detected — no flags needed:
```bash
# Generates ApiDefinition.cs + StructsAndEnums.cs + binding .csproj
dotnet run --project src/Swift.Bindings/src -- \
  --xcframework .libraries/Realm/Realm.xcframework \
  -o /tmp/realm-output/
```

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

- **Tier 1** (34 targets): Established baseline libraries (Alamofire, Nuke, Kingfisher, RxSwift, Stripe, Realm, Stripe3DS2, etc.).
- **Tier 2** (54 targets): Additional coverage libraries (DeviceKit, ObjectMapper, SVGView, Firebase, etc.).
- **Default**: `./validate-libraries.sh` runs all tiers (90 targets). Baseline updates on full unfiltered runs.
- **Manual** (35 targets across tiers): Proprietary/ObjC libraries and Firebase. Place xcframeworks in `.libraries/<name>/`. Firebase: download from GitHub releases.

### Adding a new library

1. Add entry to `validation-libraries.json` (repo URL, version, mode, tier)
2. `scripts/fetch-libraries.sh --filter NewLib`
3. `./validate-libraries.sh --filter NewLib`
4. Run full tier-1 validation to update `.validation-baseline.json`

### Known non-binding failures (not generator bugs)

- **RealmSwift** — generator crash: ABI JSON has empty module name (not built with `BUILD_LIBRARY_FOR_DISTRIBUTION=YES`)
- **Wrapper compilation failures** (SkeletonView internal types, Mixpanel `#if compiler` types) — C# bindings are correct, but Swift wrapper can't compile without the types. Not dependency-related.
- **Stripe inter-module dependencies** — use `--framework-dependency` to provide each dependency xcframework. See SDK section below for MSBuild equivalent.

## MSBuild SDK (`SwiftBindings.Sdk`)

The SDK automates the entire workflow into `dotnet build && dotnet pack`. Design doc: `src/docs/Completed/dx-msbuild-sdk-design.md`.

**Binding author workflow:**
```bash
dotnet new swift-binding -n Library.Swift.iOS
cp -r Library.xcframework Library.Swift.iOS/
cd Library.Swift.iOS && dotnet build && dotnet pack
```

**Minimal project file:**
```xml
<Project Sdk="SwiftBindings.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
  </PropertyGroup>
</Project>
```

The SDK auto-discovers `*.xcframework` in the project directory, runs the generator, compiles the Swift wrapper, and arranges NuGet pack layout. See the [Troubleshooting](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Troubleshooting) wiki page for SWIFTBIND error codes.

**Project with framework dependencies:**
```xml
<Project Sdk="SwiftBindings.Sdk">
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
- `src/Swift.Bindings.Sdk/Sdk/Sdk.props` — default properties, implicit `SwiftBindings.Runtime` reference
- `src/Swift.Bindings.Sdk/Sdk/Sdk.targets` — 9 build targets (discover → fingerprint → generate → compile → pack)
- `src/Swift.Bindings.Sdk/build-sdk.sh` — publishes generator + packs SDK NuGet

## NuGet Packages

The `Swift.*` prefix is reserved on NuGet.org (by Microsoft, the original project). Our packages use `SwiftBindings.*`:
- **SwiftBindings.Runtime** (assembly/namespace is still `Swift.Runtime`)
- **SwiftBindings.Sdk** (MSBuild SDK, `<Project Sdk="SwiftBindings.Sdk/...">`)
- **SwiftBindings.Templates** (`dotnet new install SwiftBindings.Templates`)

### Building local packages

To build local `.nupkg` files at a specific version (e.g. `0.1.1`) for testing:

1. **Temporarily set version** in these 6 locations, then revert after building:
   - `src/Swift.Runtime/src/Swift/Swift.Runtime.csproj` → `<PackageVersion>`
   - `src/Swift.Bindings.Sdk/SwiftBindings.Sdk.csproj` → `<PackageVersion>`
   - `src/Swift.Bindings.Templates/SwiftBindings.Templates.csproj` → `<PackageVersion>`
   - `src/Swift.Bindings.Sdk/Sdk/Sdk.props` → `<_SwiftBindingSdkVersion>` AND `<SwiftRuntimeVersion>`
   - `src/Swift.Bindings.Templates/content/ProjectName.csproj` → `<SwiftRuntimeVersion>`
2. **Build packages**:
   ```bash
   dotnet pack src/Swift.Runtime/src/Swift/Swift.Runtime.csproj -c Release -o /tmp/swift-nuget/
   cd src/Swift.Bindings.Sdk && ./build-sdk.sh && dotnet pack SwiftBindings.Sdk.csproj -c Release -o /tmp/swift-nuget/ && cd ../..
   dotnet pack src/Swift.Bindings.Templates/SwiftBindings.Templates.csproj -c Release -o /tmp/swift-nuget/
   ```
3. **Revert version changes** — do NOT commit version bumps to source.
4. **Copy to consumer**: `cp /tmp/swift-nuget/*.nupkg /path/to/local-packages/`
5. **Consumer override**: Use `-p:SwiftRuntimeVersion=0.1.1` or set `<SwiftRuntimeVersion>` in consumer `.csproj`.

## Working Guidelines

- **All work must have tests.** Every session, feature, bug fix, and regression fix must include targeted unit or integration tests that exercise the specific behavior. Library validation passing alone is not sufficient — write tests that would catch a regression if the fix were reverted. If fixing a validation regression, add a test case that reproduces the specific pattern that broke.
- **BindingTests for real binding flows.** For generator, emitter, or runtime changes, also check whether a BindingTests runtime test exists that exercises the pattern end-to-end (Swift source → generated binding → runtime execution on simulator). Unit tests validate internal logic but cannot catch ABI mismatches, calling convention bugs, or marshalling crashes that only surface when running real bindings. If no BindingTests coverage exists for the pattern you're changing, add Swift source to `BindingTests/Sources/SwiftBindingsTestLib/` and a C# runtime test to `BindingTests/RuntimeTestsApp/`. Place tests in the appropriate domain file (e.g., closure tests in closure test files).
- When fixing a bug pattern, grep the entire codebase for ALL instances before finishing.
- After code gen changes, verify generated output compiles — don't assume correctness.
- Use exact file paths verified by reading the filesystem. Don't guess paths.
- Address ALL code review findings in a single pass.
- Use logical/semantic cohesion for refactoring, not arbitrary LOC limits.
- Double-check memory management operations target the correct pointer/object.
- Do NOT commit unless the user explicitly asks.
- **Mid-session feedback loop**: Use `run-tests.sh` (~2 min) per sub-task for fast iteration. Avoid running `validate-libraries.sh`, `build-and-test.sh`, `run-runtime-tests.sh`, or `BindingTests/golden/check-golden-files.sh` mid-session — these are primarily end-of-session gates. Running 5+ minute commands repeatedly destroys productivity. Do as much work as possible using unit tests first. If you specifically need to validate something mid-session (e.g., confirming a tricky runtime behavior on-device), that's fine — just don't make it a habit.
- NEVER use `git stash` — linter hooks detect reverted files and stash pop discards changes silently.
- Test files are organized by domain, not by milestone/session/SDK version. Place tests in their respective domain test files (e.g., closure tests go in closure test files, not in a "phase-15" file).
- **Test quality**: Assert behavior, not implementation details. Prefer assertions on semantic correctness (e.g., "output contains CallConvCdecl", "method compiles", "round-trip marshalling preserves value") over exact string matching of generated code. This prevents tests from breaking when emitter internals change (e.g., extracting helper methods) while the behavior remains correct. Use `[Theory]`/`[InlineData]` when multiple tests differ only in input values.
- **Coverage tooling**: Coverlet is configured on both test projects. Run `dotnet test <project> --collect:"XPlat Code Coverage"` to generate coverage reports. Use coverage data to identify untested files, not as a percentage target.
- **Bug-first testing**: When writing tests for untested code, read and understand the code BEFORE writing tests. Don't assume existing behavior is correct — look for bugs first. If something looks wrong (e.g., missing null check, incorrect condition, off-by-one, wrong type cast), flag it as a potential bug and write a test that exposes the correct behavior, not one that enshrines the bug. Call out any suspected bugs explicitly so they can be triaged.

### Final Validation Gates (only when code changes warrant it)

These gates are for sessions that make **code changes to the generator, runtime, emitter, or test infrastructure**. Skip them entirely for research-only sessions, documentation updates, investigation tasks, or work on external projects (e.g., repro projects).

**When to run each gate:**

| What changed | `run-tests.sh` | `validate-libraries.sh` | `build-and-test.sh` / `run-runtime-tests.sh` |
|---|---|---|---|
| Generator/emitter/parser | Yes | Yes | Yes (`build-and-test.sh`) |
| Runtime (`Swift.Runtime`) | Yes | No (unless marshalling changed) | Yes (`run-runtime-tests.sh --skip-regen`) |
| Test infrastructure only | No | No | Yes (the specific test script) |
| Documentation / research | No | No | No |
| Repro project / external | No | No | No |

**How to run (when needed):**

1. **Unit tests**: `./run-tests.sh 2>&1 | tee /tmp/run-tests-results.txt`
2. **Library validation**: `./validate-libraries.sh 2>&1 | tee /tmp/validate-results.txt`
3. **BindingTests** (pick the fastest option that covers your changes):
   - Generator/emitter changes → full rebuild: `cd BindingTests && ./build-and-test.sh 2>&1 | tee /tmp/build-and-test-results.txt`
   - Runtime-only changes → skip regen: `cd BindingTests && ./run-runtime-tests.sh --skip-regen --timeout 90 2>&1 | tee /tmp/runtime-tests-results.txt`
   - If unsure, run `build-and-test.sh` (it includes runtime tests)

**ALWAYS output to temp files.** These commands are slow. Pipe to `/tmp/` as shown above, then use the Read tool to inspect. NEVER re-run a slow command just to see a different slice of output — read further back in the temp file instead.

If a gate fails, fix the regressions before signing off. Do not run gates that aren't relevant to the changes made.

## Known Issues

### Runtime
- **Mono JIT crashes are OUR BUGS, not upstream**: Investigation of 102 `[MonoJitCrash]`-annotated tests proved every single crash was a generator/runtime bug in our code. Zero upstream Mono issues confirmed. **NEVER use `[MonoJitCrash]` attribute** — diagnose the actual root cause and either fix it or use `[Skip("specific bug description")]`. See `src/docs/Completed/MONO-JIT-FINDINGS.md`.
- **ALL runtime crashes are guilty-until-proven-innocent**: Before labeling any crash "upstream", verify the generated C# P/Invoke matches the Swift @_cdecl wrapper: calling convention (`CallConvCdecl` vs `CallConvSwift`), parameter count, parameter types, library name, entry point symbol. Common generator bugs that look like runtime issues: wrong calling convention on P/Invoke targeting @_cdecl wrapper, extra metadata parameters the wrapper doesn't expect, missing @_cdecl wrapper (C# calls mangled symbol via CallConvSwift which Mono can't JIT).
- SafeHandle in async P/Invoke not preserved (workaround: singleton + IntPtr)
- DllImportResolver conflict: `[ModuleInitializer]` + consuming app both call `SetDllImportResolver` → `InvalidOperationException`. RuntimeTestsApp wraps in try-catch.
- See [wiki Known Limitations](https://github.com/justinwojo/swift-dotnet-bindings/wiki/Known-Limitations) for full consumer-facing details

### Generator (open bugs)
- String enum raw values use case names (ABI JSON lacks raw values)
- `UnsafePointer<T>` → AnyType (no concrete projection)
- `async throws(ErrorType)` free functions: `_payload`/`this` in static context (guarded)
- ExistentialContainer0 in tuple element (blocked by `HasClosureUnsafeTupleElements` gate)
- Optional<any Protocol> in closures: deferred (`MarshalFromSwift` limitation)

## Key References

- `src/docs/roadmap.md` — Single consolidated roadmap (remaining work to ship + post-ship improvements)
- `src/docs/swiftui-roadmap.md` — SwiftUI bridge sessions (4 remaining)
- `src/docs/Completed/nativeaot-stability-sessions.md` — NativeAOT device validation (373 pass, 14/15 success)
- `src/docs/Completed/dx-msbuild-sdk-design.md` — MSBuild SDK design (Steps 1-5, all complete)
- `src/docs/Future/future-roadmap.md` — Prioritized future vision items
- `src/docs/Completed/` — All archived roadmaps, reviews, session notes
