# Roadmap

**Created**: February 2026
**Status**: Active — single source of truth for work items

For completed work, see `Completed/` (notably `phases-a-through-g.md` and `phases-h-through-wu.md`).
For detailed gap descriptions and contract matrix, see `testing-gaps.md`.
For DX design specs, see `developer-experience.md`.
For test pipeline hardening specs, see `testframework-review.md`.
For deferred/aspirational work, see `Future/`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 1,936 passing |
| Integration tests | 699 passing (11 skipped, pre-existing) |
| Runtime tests | 185 passing at Tier 2 safe-only (28 pre-existing failures) |
| TestFramework must-pass | 94/94 passing, 0 degraded |

| Library | Binding Errors | Simulator Tests | Notes |
|---------|---------------|-----------------|-------|
| **Lottie** | 0 | 15/15 passing | Clean |
| **BlinkID** | 0 | 13/13 passing | Modal presentation skipped (iOS async limitation) |
| **Nuke** | 0 | 15 safe + async passing | Async image load fully working end-to-end |
| **CryptoSwift** | 0 | 6 safe + 1 skip | Instance methods crash on CallConvSwift (see I3) |
| **BridgeTest** | 0 | 35/35 passing | Clean |

---

## Active Work Queue

Work is ordered by impact. The generator is solid (0 errors, 88-99% coverage across 4 libraries, idiomatic C# API). The bottleneck is now **consumability** — nobody outside the project can use any of this. DX work comes first, interleaved with hardening.

### DX-1: "Hello World" External Consumption

**Status**: Not Started
**Priority**: Highest — smallest useful increment for external users
**Effort**: 1-2 sessions
**Depends on**: Nothing — can start immediately
**Spec**: `developer-experience.md` § DX-1

**Goal**: An external user can take a generated binding + xcframework and use it in their .NET iOS app.

1. **Package Swift.Runtime as a NuGet** — flip `IsPackable` to `true`, add package metadata (id, description, license, authors). External users need this as a dependency.
2. **Generator emits a compilable `.csproj`** — today the generator outputs loose `.cs` files + a `Swift/` runtime copy. Emit a ready-to-use binding project that references `Swift.Runtime` via `PackageReference` and compiles the generated `.cs` files.
3. **Document the manual workflow** — "Getting Started" guide: obtain xcframework → extract ABI JSON → run generator → build Swift wrapper → reference in app. Codify what `build-all.sh` does into a reproducible guide.

**Done when**: Someone outside the project can follow the guide, generate Nuke bindings, and call `ImagePipeline.shared` from a .NET iOS app.

---

### TH: Test Pipeline Hardening

**Status**: Not Started
**Priority**: High — prevent silent regressions during DX work
**Effort**: 1 session (half-day)
**Depends on**: Nothing — can start immediately, interleave with DX-1
**Spec**: `testframework-review.md`

Quick wins from the TestFramework risk-first review. These are low-effort, high-value gates.

#### TH-1. Compile gate for generated bindings

Add `TestFramework/CompileCheck/CompileCheck.csproj` that includes the generated `.cs` file and runs `dotnet build` as a hard-fail gate in `build-and-test.sh`. Prevents the most expensive false-pass class (plausible-looking but non-compilable generated code).

#### TH-2. Baseline budgets for skip/compiled-out/wrapper-stripped counts

Store current counts in a baseline JSON. `run-tests.sh` compares against baseline and fails if counts increase without an explicit baseline update. Stops gradual normalization of reduced coverage.

#### TH-3. Generator exit code regression gate

Store expected generator exit code in baseline (currently 0). `build-and-test.sh` compares actual vs baseline. Catches generator crashes before waiting for compile or runtime failures.

#### TH-4. Ratchet async wrapper stripping count

Write stripped-block count to `output/wrapper-stripped-count`. Fail if count exceeds baseline + tolerance. The stripping remains needed but can no longer silently absorb new regressions.

**Done when**: A PR that emits invalid generated C# fails in under 2 minutes. Skip/stripped/exit-code counts cannot drift upward without explicit baseline update.

---

### DX-2: NuGet Packaging

**Status**: Not Started
**Priority**: High — automate distribution
**Effort**: 2-3 sessions
**Depends on**: DX-1 (compilable generated project exists)
**Spec**: `developer-experience.md` § DX-2

**Goal**: `dotnet pack` on the generated project produces a correct `.nupkg`.

1. **`.targets` file generation** — generator emits `build/` and `buildTransitive/` targets with NativeReference injection (Layer 2) and `SwiftBindingFramework` validation (Layer 3)
2. **iOS version extraction** — fallback chain: Info.plist `MinimumOSVersion` → `.swiftinterface` target triple → Mach-O `LC_BUILD_VERSION`/`LC_VERSION_MIN_*`
3. **Library version extraction** — `CFBundleShortVersionString` with placeholder detection heuristic
4. **`binding-metadata.json` emission** — alongside `binding-report.json`
5. **Pack script** (`pack-binding.sh`) — correct NuGet directory structure (lib/, build/, buildTransitive/, runtimes/) + `dotnet pack`

**Done when**: `./pack-binding.sh` produces a `.nupkg` that a consumer can install and get working NativeReference injection automatically.

---

### Phase J: Additional Library Validation

**Status**: Not Started
**Priority**: Medium — validates generator generalization, finds new patterns
**Effort**: 2-3 sessions
**Depends on**: DX-1 (use the documented workflow to bind the library)

Binding a new library serves two purposes: (1) find generator gaps we haven't hit yet, and (2) validate the DX-1 workflow with a fresh library.

#### J1. Select and bind a new library

Candidates (pick 1):
- **Alamofire** — networking, heavy closure/async patterns
- **Kingfisher** — image loading, different patterns from Nuke
- **SwiftProtobuf** — value types, generics, enums heavy

#### J2. Process

1. Build xcframework for the library
2. Run generator, check binding report
3. Compare member coverage to existing libraries (target: 90%+)
4. Verify golden scenario compiles without interop types
5. Fix any new generator bugs found
6. Add to `BindingTesting/` with build/validate scripts

#### J3. Document findings

- Update `CURRENT-STATUS.md` with new library stats
- Add any new skip reasons to `testing-gaps.md`

---

### DX-3: Multi-Framework Dependencies

**Status**: Not Started
**Priority**: Medium — needed for libraries like Nuke (Nuke + NukeUI + NukeExtensions)
**Effort**: 2-3 sessions
**Depends on**: DX-2 (single-framework packaging works)
**Spec**: `developer-experience.md` § DX-3

**Goal**: Libraries with multiple dependent frameworks package correctly with dependency tracking.

1. **Dependency manifest generation** — `dependency-manifest.json` from binary linkage (`otool -L` / `LC_LOAD_DYLIB`) + type-level cross-reference analysis
2. **`SwiftBindingFramework` MSBuild item** — cross-package registration and validation (Layer 3)
3. **`pack-all.sh`** — topological sort from dependency manifest, builds packages bottom-up
4. **End-to-end validation** — install generated packages in a clean project, verify all 4 enforcement layers work

**Done when**: Install `NukeUI.Swift.iOS` without `Nuke.Swift.iOS` → clear build error. Install both → app runs.

---

## Deferred Generator Work

These are generator improvements that are blocked on upstream runtime fixes or have lower priority than consumability.

### I2. Auto-route closure+CallConvSwift to wrapper library

**Status**: Partially Done (Strategy B covers primitive-arg closures)

Strategy B (Closure Cdecl Expansion) covers primitive-arg closures via standalone `@_silgen_name` wrappers with `@convention(c)` function pointers. Non-primitive closures (String, class, struct args) remain on the legacy `CallConvSwift` path.

### I3. Route instance methods through `@_cdecl` wrappers

**Status**: Not Started

Instance methods on Swift classes/structs currently use `CallConvSwift` for the `self` parameter, which triggers the `jit-info.c:918` assertion. Would unblock CryptoSwift's instance API (SHA2.Calculate, HMAC.Authenticate, ChaCha20.Encrypt/Decrypt, RSA, etc.).

---

## Future Work

After the active work queue is complete:
- 5-6 real-world libraries validated
- External users can generate, build, and package bindings
- Test pipeline catches regressions automatically

### DX-4: MSBuild SDK + Templates

**Spec**: `developer-experience.md` § DX-4

`dotnet new swift-binding` + `dotnet build` = NuGet package. Only pursue once the script-based workflow (DX-1 through DX-3) is proven and user feedback confirms the automation is worth the MSBuild SDK complexity.

### Generator Feature Improvements

- **Optional string properties** — `Swift.Optional<Swift.String>` → `string?` (extend TypeConversionHandler)
- **Cross-module protocol interface coverage** — expand `_runtimeProtocols` for stdlib protocols (Comparable, Sendable, CodingKey, etc.)
- **Full protocol witness dispatch** — mutating methods, throws, async

### Testing Depth (P2-P4 from testing-gaps.md)

- **Async runtime tests** (P1) — move core async Swift sources out of `.disabled/`, implement round-trip tests
- **Protocol witness dispatch runtime tests** (P2) — enable protocol Swift sources, test getter/setter/method dispatch
- **Complex composition tests** (P2) — class+closure, struct+optional-array, singleton+async patterns
- **PInvokeEmitter unit tests** (P3) — dedicated tests for P/Invoke generation
- **Generic runtime tests** (P3) — Container\<T>, generic methods, bound type params
- **Error handling tests** (P3) — enable ThrowingFunctions.swift, test throw→exception mapping
- **Golden API snapshot tooling** (P4) — detect API surface drift
- **CI integration** (P4) — GitHub Actions with tiered test profiles

### Deferred Items

- NativeAOT validation (`Future/nativeaot-investigation.md`)
- Roslyn analyzer for unsafe pattern detection
- Unsupported existential analysis (`Future/unsupported-existential-analysis.md`)
- Performance benchmarks (`Future/interop-performance-validation-plan.md`)
- Upstream .NET runtime bug reports (`Future/upstream-bug-reports-draft.md`)

---

## Completed Work Summary

All completed phases are archived in `Completed/`. Key milestones:

| Phase | What | Tests Added |
|-------|------|-------------|
| A–G | Core infrastructure through CryptoSwift validation | ~1,700 unit + 185 runtime |
| H1-H2 | Unit test gaps + 6 library binding bugs → all 4 libraries 0 errors | 17 regression |
| I1/I1a/I1b | Nuke wrapper path + BitwiseCopyable + ObjC async callbacks | 31 unit |
| K | Swift doc comments → C# XML doc comments | 30 unit |
| Strategy D | MonoJitRiskDetector static analysis | 34 unit |
| Strategy B | Closure Cdecl expansion for Mono JIT mitigation | 38 unit |
| Tier Promo | Tj dispatch thunks + IsFinal + tier promotions (172→185 runtime) | 13 runtime |
| WU1-WU6 | Idiomatic C# binding API + Codex review fixes | 17 regression |
| Enum/Nullable | Existential promotion in enum values + #nullable bridge | 7 unit |

---

## Known Runtime Blockers (Upstream)

- **Mono JIT assertion (jit-info.c:918)**: Kills process on closure P/Invoke + SwiftString via CallConvSwift
- **SafeHandle in async P/Invoke**: Not preserved through async continuation
- **Non-blittable CallConvSwift**: Mono rejects non-blittable types with Swift calling convention
- See `known-issues-workarounds.md` for details
