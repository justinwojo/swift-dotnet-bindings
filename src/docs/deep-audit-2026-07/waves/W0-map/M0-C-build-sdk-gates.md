# M0-C — Build / SDK / pack gate matrix

**Wave**: 0 (map)  
**Scope**: `build/Build*.cs`, `src/Swift.Bindings.Sdk/Sdk/`, PackGate, release gates, appstore hygiene, x64 gates  
**Mode**: Read-only inventory for deep-audit 2026-07  
**Date**: 2026-07-15  

---

## 0. Layout overview

| Area | Path | Role |
|------|------|------|
| Nuke entry | `build/Build.cs` | Parameters, `Compile`/`Clean`/`SmokeTest` |
| Partial targets | `build/Build.*.cs` | One concern per partial (often mega-files) |
| Helpers / models | `build/Helpers/`, `build/Models/` | Baselines, manifests, platform model, tools |
| Gate fixtures | `build/PackGate/`, `build/X64PackGate/`, `build/x64-thunk-gate/` | Scratch fixtures for packaging/ABI gates |
| Baselines | `build/baselines/*.json` | validation, parity, api-manifest, skip-surface, runtime-identity |
| SDK | `src/Swift.Bindings.Sdk/Sdk/{Sdk.props,Sdk.targets,scripts/}` | Consumer `dotnet build` two-pass binding pipeline |
| Release workflow | `.github/workflows/release.yml` (referenced from CLAUDE.md) | CI composition; not rewritten here |

Nuke host: `build/_build.csproj` → `nuke <target>`.

---

## 1. Nuke targets matrix

### 1.1 Everyday / CI-adjacent

| Target | File | What it proves | Fail mode |
|--------|------|----------------|-----------|
| **Compile** | `Build.cs` | Solution builds; also builds SwiftInterfaceParser (Darwin) | Hard fail |
| **Clean** | `Build.cs` | `dotnet clean` | Soft |
| **Test** | `Build.Test.cs` | Unit + Runtime unit + Analyzer tests | Hard fail on test fail |
| **UnitTests** / **RuntimeUnitTests** / **AnalyzerTests** | `Build.Test.cs` | Layer slices of Test | Hard fail |
| **BindingTests** | `Build.BindingTests.cs` + RuntimeTests + Mixed* + AppStore | E2E compile and/or runtime | See §4 |
| **Validate** | `Build.Validation.cs` | Real-world lib generate + compile gate vs baseline | Hard fail on regressions; tier/filter opt-in |
| **Fetch** | `Build.Validation.Fetch.cs` | Download/build xcframeworks into `.libraries/` | Hard fail on fetch errors |
| **Pack** | `Build.Pack.cs` | 4 nupkgs (Runtime, Sdk, Templates, Apple) | Hard fail; **requires** `--version` + `--apple-version` |
| **PackGate** | `Build.PackGate.cs` + MixedFixture | nupkg layout + consumer pack + (mixed fixture pieces) | Hard fail |
| **ValidateBlastRadius** | `Build.BindingTests.cs` | Apple supplement adds zero extra frameworks/symbols vs baseline | Hard fail on golden diff |

### 1.2 BindingTests modes (one target, many legs)

Invoked as `nuke binding-tests [flags]`. Dispatch in `Build.BindingTests.cs:902+`.

| Mode / flag | Proves | Platform | Default? |
|-------------|--------|----------|----------|
| *(none)* / `--sim` | Regen + compile + RuntimeTestsApp on **iOS Simulator** (Mono JIT) | sim | **Yes** (default when no platform) |
| `--device` | Same on physical device **NativeAOT** | device | Opt-in |
| `--macos` / `--macos-x64` | macOS host runtime | macos | Opt-in |
| `--catalyst` / `--catalyst-x64` | Mac Catalyst runtime | catalyst | Opt-in |
| `--tvos` | tvOS simulator | tvos | Opt-in |
| `--compile-only` | xcframework + gen + compile-check + wrapper + bridge + **parity** + **api-manifest** (no app) | host | CI gate |
| `--strict` | Generator non-zero exit is fatal (also input strictness) | any | Composer |
| `--permissive` | Downgrade compile-only catastrophic failures | compile-only only | Local explore |
| `--skip-regen` / `--skip-build` | Speed shortcuts | any | Inner loop |
| `--class-filter` | Single test class | runtime legs | Debug |
| `--mixed-pack` | Mixed ObjC+Swift **single PackageReference** pack→run | iOS sim and/or device | Release-adjacent |
| `--mixed-direct` | Mixed binding **SDK-direct** (app *is* binding) | sim only | Release-adjacent |
| `--appstore-hygiene` | TN2435 runtime packaging + IPA | host (needs codesign for IPA leg) | Release-adjacent |
| `--abi-grid` | Multi-platform ABI coverage grid | with runtime legs | Opt-in |
| `--skip-surface` | Skip-marker baseline trend | compile-only | Opt-in |

Mutually exclusive: `--compile-only` vs mixed/hygiene; mixed-pack vs mixed-direct vs appstore-hygiene (fail loud).

Sub-targets used imperatively: `BuildXcframework`, `RegenerateBindings`, `CompileCheckBindings`, `BuildAsyncWrapper`, `BuildBridge`.

### 1.3 Packaging & architecture gates

| Target | File | What it proves |
|--------|------|----------------|
| **PackGate** | `Build.PackGate.cs` | Pack Runtime+Sdk+Apple; TipKit fixture nupkg has every expected wrapper xcframework slice + Info.plist; Runtime/Apple `buildTransitive` layout (ILLink descriptor, targets); AOT descriptor injection; HelloPack macOS consumer round-trip; mixed-fixture helpers in `Build.PackGate.MixedFixture.cs` |
| **X64ThunkGate** | `Build.X64ThunkGate.cs` | cdecl→swiftcc **thunk ABI only** under Rosetta (`arch -x86_64`); no Swift.Runtime |
| **X64PackGate** | `Build.X64PackGate.cs` | Fat arm64+x86_64 source xcframework → multi-TFM packed binding; `lipo` arch sets; **osx-x64** runtime round-trip under Rosetta |
| **X64SimGate** | `Build.X64SimGate.cs` | iOS/tvOS `iossimulator-x64` / `tvossimulator-x64` **packaging** (embed x86_64 framework); Apple StoreKit + TipKit second-slice / bridge fat compiles — **no** sim runtime (Silicon can’t boot x86_64 iOS sim) |
| **BehaviorTier** | `Build.BehaviorTier.cs` | After validate: pack feed + macOS consumer **runtime** round-trip (Foundation always; Alamofire opt-in) |
| **BuildAppleSupplementXcframework** | `Build.AppleSupplement.cs` | SBApple.xcframework for Apple pack |

### 1.4 Release composition

| Target | File | What it proves |
|--------|------|----------------|
| **ReleaseGates** | `Build.ReleaseGates.cs` | Orchestrates: `Test`, `BindingTests --strict --compile-only`, `PackGate`, appstore-hygiene **structural** leg; writes `artifacts/release-gates/release-gates-manifest.json` with pass\|fail\|skipped for full catalog | Hard fail if any executed leg fails; skips for device/mixed/IPA are intentional unless `--require-complete` |
| **ReleaseGatesAttest** | same | Record attended pass/waive for skipped legs into persisted manifest | RC checklist |

**Not wired into CI** as a single target — RC primitive. CI release.yml (per CLAUDE.md) runs overlapping legs: `nuke test`, `binding-tests --strict --compile-only`, tier-2 sim, `validate-blast-radius`, NuGet preflight.

### 1.5 Seed / maintenance sinks

| Target | Purpose |
|--------|---------|
| `SeedApiManifestBaseline` | Refresh api-manifest baseline |
| `SeedParityBaseline` | Refresh parity baseline |
| `SeedSkipSurfaceBaseline` | Refresh skip-surface baseline |
| `SeedWrapperStripBaseline` | Wrapper strip count baseline |
| `ValidateAppleTypesManifest` | Apple types manifest schema |
| `RegenStdlibConformances` | Stdlib conformance tables |
| `CompileSwiftInterfaceParser` | SwiftSyntax host binary for SDK pack |
| `RegenerateAppleSnapshot` / `RegenerateStoreKitSnapshot` | Snapshot regen |
| `SmokeTest` | Toolchain path smoke |

### 1.6 Dependency sketch

```
Clean
  └─ Compile ──► Test
           ├─► BindingTests (many legs)
           ├─► PackGate ──► (Validate Triggers PackGate + BehaviorTier)
           ├─► Pack (needs Version + AppleVersion)
           ├─► X64*Gate / BehaviorTier / Validate
           └─► ReleaseGates (subprocess legs; no DependsOn Compile)
```

`Validate` `.Triggers(PackGate, BehaviorTier)` — those run after validate when validate is the entry.

---

## 2. SDK two-pass flow

**Files**: `Sdk.props` (evaluation defaults, package refs) + `Sdk.targets` (execution pipeline) + `scripts/compile-wrapper-locked.sh`.

### 2.1 Modes

| Mode | Item | Generator path |
|------|------|----------------|
| **XCFramework** (third-party) | `<SwiftFramework Include="…xcframework">` or auto-discover | `_GenerateSwiftBindings` |
| **AppleFramework** | `<SwiftAppleFrameworkTarget Include="StoreKit" Module="…"/>` | `_GenerateSwiftBindingsAppleFramework` (+ digester / interface) |
| **ObjC** (either path) | `SwiftFrameworkType=ObjC` + `IsBindingProject=true` | ObjC/bgen companion path |

Hard errors: unsupported TFM (`_SwiftBindingPlatformUnsupported`), both SwiftFramework + Apple target, missing xcframework, ObjC without IsBindingProject (`SWIFTBIND021`), Swift interface + ObjC type mismatch (`SWIFTBIND019`), etc.

### 2.2 Two-pass (XCFramework mode) — why

Wrapper compilation needs **resolved ProjectReference dependency frameworks** on the `-F` search path. Those are not available before `ResolveProjectReferences`. Therefore:

```
Pass A — Before ResolveProjectReferences
  _DiscoverSwiftFrameworks
  _ComputeSwiftFingerprint  (hash xcframework + props + deps)
  _GenerateSwiftBindings
      generator --skip-wrapper-compilation
      → emits C#, .swift wrapper source, metadata props, consumer .targets
      → sets _SwiftBindingHasWrapperXCFramework from "will produce" signal
      (NOT bare "exists now" — see constraints.md)

Pass B — After ProjectReferences resolved
  _CompileSwiftWrapper
      generator --compile-wrapper-only
      → locked compile (compile-wrapper-locked.sh) so multi-TFM peers don’t race
      → may lipo extra arches from SwiftTargetArchitectures
  _ImportSwiftBindingMetadata
  pack-time slice validation / consumer NativeReference injection
```

AppleFramework mode mirrors with `_GenerateSwiftBindingsAppleFramework` and second-slice targets:

- `_CompileAppleFrameworkSecondWrapperSlice` — device-first builds still get simulator fat slice  
- `_CompileAppleFrameworkSecondBridgeSlice` — TipKit-style SwiftUI bridge fat sim slice  

Fingerprint skip (`_SwiftBindingUpToDate`) short-circuits regen **and** requires wrapper xcframework still on disk when claiming wrapper present.

### 2.3 Hook integrity (fail-closed)

Targets assert hooks actually ran (`_SwiftHookRan_GenerateSwiftBindings`, etc.). If MSBuild renames `ResolveProjectReferences`, bindings would otherwise compile empty without error — SDK errors with explicit re-anchor message.

### 2.4 Consumer packaging outputs

- Managed DLL(s) per TFM  
- `runtimes/<rid>/native/*SwiftBindings.xcframework` (wrapper)  
- Optional bridge xcframework  
- `buildTransitive/*.targets` with `NativeReference` + mixed companion refs  
- ILLink descriptors for NativeAOT  

`ConsumerTargetsEmitter` (generator, not SDK file) must use **will-produce** wrapper flags when writing consumer targets under skip-wrapper-compilation (constraints.md).

### 2.5 Graceful vs integrity in SDK (consumer `dotnet build`)

| Situation | Consumer experience |
|-----------|---------------------|
| Unsupported member shapes | Prefer generator **skip** + report (L3 product goal) — not SDK concern per se |
| Generator exit non-zero | MSBuild **Error** (hard) |
| Wrapper compile fails | Primary path: ContinueOnError warn in some SDK legs; metadata must stay honest (returning primary result if x86_64 fold fails — constraints.md) |
| Missing interface / SDK framework | `SWIFTBIND*` Error with actionable text |
| Incremental fingerprint match | Silent skip of regen (fast path) |
| Platform unsupported TFM | Error early |

---

## 3. Pack / mixed / x64 / hygiene gates

### 3.1 Pack (`nuke pack --version X --apple-version A`)

Order: Runtime → Sdk (publish generator first) → Templates → Apple (unless `--skip-apple`).

Integrity hard fails:

- Missing `--apple-version` even with `--skip-apple` (Sdk.props must advertise supplement floor)  
- Missing/non-universal2 `SwiftInterfaceParser` binary  
- VersionScope must not mutate source-controlled version files (snapshot gate)  
- Windows MAX_PATH ship gate on produced nupkgs  

### 3.2 PackGate

Throwaway versions `0.0.0-packgate` / Apple `26.2.0-packgate`.

Assertions (non-exhaustive):

1. SwiftInterfaceParser present + universal2  
2. Apple xcframework Windows path safety  
3. Runtime `buildTransitive`: targets + `ILLink.Descriptors.xml`; **no** loose dylib packaging regression  
4. Apple supplement Runtime dependency is **floor-only** range  
5. AOT descriptor injection under `PublishAot=true`  
6. TipKit fixture nupkg: every RID’s wrapper xcframework slices + framework binary + Info.plist  
7. Bridge macOS-exclusion rules (empty native macOS bridge not shipped wrongly)  
8. HelloPack end-to-end consumer on macOS host  

### 3.3 Mixed pack / mixed direct

| Leg | Consumption path | Proves |
|-----|------------------|--------|
| `--mixed-pack` | Single packed `PackageReference` | ObjC class registers once on **sim Mono + device NativeAOT**; nupkg structure (static source dropped, companion in `lib/`) |
| `--mixed-direct` | App csproj imports Sdk + `<SwiftFramework>` | Companion `<Reference>` injection + single registration; sim-only by design |

Both opt-in, heavyweight, mutually exclusive with each other and hygiene/compile-only.

### 3.4 App Store hygiene (`--appstore-hygiene`)

File: `Build.BindingTests.AppStoreHygiene.cs` (issue #42 / TN2435).

1. **Structural** (no signing): Runtime nupkg has `SwiftBindingsRuntime.xcframework` slices; lipo archs (device arm64, sim arm64+x86_64); **no** loose `libSwiftBindingsRuntime.dylib`; **no** `add-swiftsupport-folder.sh`.  
2. **IPA leg** (needs codesign identity): single PackageReference consumer → `ios-arm64` `BuildIpa=true` → assert signed framework embed, zero `libswift*.dylib`, no `SwiftSupport/`, signature verifies.  

If host cannot sign: **structural pass + honest SKIP** of IPA (not a greenwash pass). Shared structural helper used by `ReleaseGates`.

### 3.5 x64 gate stack

```
X64ThunkGate     thunk ABI under Rosetta (manual P/Invoke)
      ↓
X64PackGate      packaged multi-platform fat + osx-x64 runtime
      ↓
X64SimGate       iOS/tvOS x64 sim RID packaging + Apple second-slice/bridge
```

BindingTests cells separately cover maccatalyst-x64 / osx-x64 Mono/CoreCLR runtime behavior.

---

## 4. Partial failure vs hard fail

### 4.1 Integrity fail-closed (must stay hard)

| Layer | Examples |
|-------|----------|
| Pack | Missing apple-version; missing parser binary; source version rewrite; MAX_PATH |
| PackGate | Missing slice / Info.plist; broken buildTransitive AOT wiring |
| BindingTests `--compile-only` (default) | Generator exit ≠ 0; dep-gen exit; wrapper compile give-up; parity/api-manifest **new** divergence |
| Appstore hygiene structural | Loose dylib / missing framework slice |
| RuntimeContract (runtime, not nuke) | Epoch outside window → load abort |
| SDK | Hook disconnection; missing framework; SWIFTBIND* validation |

### 4.2 Fail-open / degrade / skip (intentional)

| Layer | Behavior |
|-------|----------|
| BindingTests `--permissive` | compile-only: non-zero gen/wrapper become non-fatal (local explore only) |
| Generator member skip | Unsupported signatures → skip reason + continue (product L3) |
| Wrapper x86_64 fold failure (SDK) | Degrade to primary arch; warn; keep wrapper metadata True if primary exists |
| Appstore IPA without codesign | Structural pass; IPA skipped (not fail) |
| ReleaseGates skipped legs | `skipped(not run)` in manifest; exit 0 unless `--require-complete` |
| Validate tier/filter | Only selected libs; baseline compare on what ran |
| `EnsureGeneratorBuilt` | Only builds if Debug dll **missing** — **stale binary hazard** (constraints.md): not a gate fail, silent wrong certify |

### 4.3 Exit codes / Nuke

- Nuke target throw / `Assert.Fail` → non-zero process exit  
- Generator child process: BindingTests inspects `exitCode`; strict/failClosed throws  
- Wrapper compile: boolean `RunBuildAsyncWrapper()`; failClosed throws  
- ReleaseGates: failed legs recorded then orchestrator Assert.Fail after all legs attempted  

### 4.4 Compile-only fail-closed checklist

From `Build.BindingTests.cs:931–978`:

1. `RunBuildXcframework`  
2. `RunRegenerateBindings(strict: Strict \|\| failClosed)` (+ `--strict-inputs` when strict)  
3. `RunCompileCheck`  
4. `RunBuildAsyncWrapper` — failClosed on false  
5. `RunBuildBridge`  
6. `RunParityGate(failClosed)`  
7. `RunApiManifestGate(failClosed)`  
8. Optional `RunSkipSurfaceGate`  

---

## 5. Graceful-degradation relevance (consumer `dotnet build`)

**L3 product goal** (methodology): arbitrary xcframework → usable partial binding, not total pipeline death.

| Layer | Relevance |
|-------|-----------|
| **Generator** | Primary L3 surface: skip unsupported members; emission reports; wrapper post-processor strip vs emission admission |
| **SDK** | Mostly **integrity-hard** on project misconfiguration (good: actionable SWIFTBIND errors). Incremental fingerprint can hide generator bugs if stamp not invalidated (fingerprints include generator-affecting props + framework hashes — good). Multi-TFM lock script prevents partial races. |
| **Pack / PackGate** | Prove that **what ships** is complete (slices, descriptors). Partial binding content is generator’s job; packaging lies are integrity fails. |
| **BindingTests compile-only** | Fail-closed on **integrity** of gen+wrapper+parity, not on count of skipped members (skip-surface is separate opt-in). |
| **Validate** | Compile gate across real libs — measures whether partial emit still **compiles**. BehaviorTier adds one runtime canary. |
| **Runtime nupkg targets** | `IncludeSwiftBindingsRuntimeNative` opt-out for harnesses; Apple-TFM guard prevents invalid NativeReference on non-Apple multi-target legs. |

**Consumer pain points to re-audit later (G1)**:

- Does wrapper compile failure fail the whole package or leave usable managed-only surface? (today: generally hard)  
- Are skip reports visible in `dotnet build` log / artifacts for partial success storytelling?  
- Dual fail-closed vs fail-open between SDK Exec ContinueOnError and BindingTests compile-only  

---

## 6. Simplification notes (L4 — mega Build.*.cs)

### 6.1 Size / ownership heatmap (approx)

| File | Role | Complexity |
|------|------|------------|
| `Build.RuntimeTests.cs` | Simulator/device/macOS/catalyst/tvOS run orchestration | **Mega** (~3k+ LOC) |
| `Build.Validation.cs` | Parallel generate/compile for validation libs | **Mega** (~2k+) |
| `Sdk/Sdk.targets` | Full consumer pipeline | **Mega** (~2.8k+ lines of MSBuild) |
| `Build.PackGate.cs` + `.MixedFixture.cs` | Packaging assertions | Large |
| `Build.BindingTests.cs` | Flags + compile-only + dispatch | Large |
| `Build.BindingTests.MixedPack.cs` / `.MixedDirect.cs` / `.AppStoreHygiene.cs` | Opt-in legs | Medium-large each |
| `Build.X64*.cs` | x64 gate stack | Medium each |
| `Build.ReleaseGates.cs` | Manifest orchestrator | Medium |
| `Build.Pack.cs` | Ship pack | Medium |

### 6.2 Simplification candidates

| ID | Observation | Suggested shape | Risk |
|----|-------------|-----------------|------|
| B-S1 | Pack / PackGate / BehaviorTier / Mixed* / Hygiene all re-pack Runtime+Sdk with VersionScope + throwaway versions | Shared `PackThrowawayFeed(version, appleVersion)` helper | Behavior-preserving if assertion order preserved |
| B-S2 | X64PackGate vs X64SimGate share fixture xcframework build | Shared fixture builder module | Medium |
| B-S3 | `Build.RuntimeTests.cs` multi-platform runners | Extract per-platform runner types | High touch / low urgency |
| B-S4 | SDK.targets AppleFramework + XCFramework dual pipelines | Document matrix; extract `.targets` imports per mode | High risk of MSBuild order bugs |
| B-S5 | Seed* baseline targets pattern | Already parallel; keep | — |
| B-S6 | Stale generator binary (`EnsureGeneratorBuilt` missing-only) | Always rebuild Debug generator when source newer, or hash stamp | **Correctness** fix more than simplification |

### 6.3 Dual-oracle / drift risks (gate honesty L2)

| Pair | Must stay aligned |
|------|-------------------|
| PackGate Runtime layout asserts vs actual `Swift.Runtime.csproj` pack | Packaging contract |
| Appstore hygiene structural vs Runtime.targets NativeReference | TN2435 |
| API-manifest / parity baselines vs generator emission | Compile-only CI |
| Wrapper strip baseline vs `SwiftWrapperPostProcessor` | Fail-closed strip growth |
| SDK fingerprint echoes vs `SwiftTargetArchitectures` | Stale arm64-only wrapper |
| ReleaseGates catalog leg IDs vs real target flags | Manifest honesty |

---

## 7. Baselines & artifacts (gate inputs/outputs)

| Artifact | Used by |
|----------|---------|
| `build/baselines/validation-baseline.json` | `nuke validate` |
| `build/baselines/parity-baseline.json` | BindingTests compile-only parity |
| `build/baselines/api-manifest-baseline.json` | API retarget gate |
| `build/baselines/skip-surface-baseline.json` | Optional skip trend |
| `build/baselines/runtime-identity-baseline.json` | Runtime identity checks |
| `build/validation-libraries.json` | Validate + BehaviorTier opt-in |
| `artifacts/pack-gate/` | PackGate scratch |
| `artifacts/release-gates/release-gates-manifest.json` | ReleaseGates |
| `artifacts/appstore-hygiene/` | Hygiene scratch |
| `artifacts/x64-*-gate/` | x64 scratches |
| `/tmp/binding-validation-<branch>/` | Validate cache (invalidate when generator changes) |

---

## 8. Parameter cheat sheet (Nuke)

| Parameter | Affects |
|-----------|---------|
| `Platform` | ios/macos/tvos default for BindingTests xcframework |
| `Filter` / `Tier` / `Quick` / `Jobs` / `Serial` / `FetchFirst` | Validate |
| `Version` / `AppleVersion` / `SkipApple` / `OutputDir` | Pack |
| `Strict` / `Permissive` / `CompileOnly` | BindingTests fail policy |
| `Sim` / `Device` / `Macos` / `Catalyst` / `Tvos` / `*X64` | Runtime platforms |
| `MixedPack` / `MixedDirect` / `AppstoreHygiene` | Exclusive heavy legs |
| `SkipRegen` / `SkipBuild` / `ClassFilter` / `Timeout` / `Lifetime` | Runtime inner loop |
| `RequireComplete` / `Leg` / `Result` / `By` / `Evidence` / `Manifest` | ReleaseGates* |

---

## 9. Pointers for later waves

| Lens | Where to dig |
|------|----------------|
| L2 gate honesty | Permissive path; skipped hygiene IPA; EnsureGeneratorBuilt staleness; baseline fail-open vs fail-closed |
| L3 graceful degradation | Generator↔SDK error surface; validate “compile but empty”; consumer log quality |
| L4 simplification | PackThrowawayFeed; RuntimeTests split; Sdk.targets modularization |
| Integrity | PackGate + appstore structural + RuntimeContract (runtime map) |

---

*End of M0-C build/SDK/gates map.*
