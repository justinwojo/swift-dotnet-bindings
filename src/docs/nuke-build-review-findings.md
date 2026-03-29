# Nuke Build Migration — Review Findings

Tracked findings from the code review of the `nuke-build-migration` branch.

---

## Part 1: Script Consolidation

### Vision

Today: **21 shell scripts** (6,200+ lines) scattered across root, `BindingTests/`, `scripts/`, and `src/`. Confusing for external contributors, hard to discover, lots of dead/internal scripts exposed as top-level entry points.

Target: **`nuke <target>`** is the only build interface. Zero shell scripts at the repo root. Internal pipeline scripts removed (Nuke replaces them). CI calls Nuke directly.

### The Complete Nuke API (9 targets)

| Target | What it does | Key parameters | Replaces |
|--------|-------------|----------------|----------|
| `compile` | Build SwiftBindings.sln | *(default target)* | `build.sh` |
| `test` | All unit + integration tests, BindingTests regression + runtime | — | `run-tests.sh` |
| `binding-tests` | Full BindingTests pipeline (xcframework → bindings → compile → bridge → coverage → baselines) | `--strict` | `build-and-test.sh`, `check-baselines.sh` |
| `runtime-tests-simulator` | iOS Simulator runtime tests | `--skip-regen`, `--skip-build`, `--timeout N`, `--class-filter NAME`, `--flake-detect` | `run-runtime-tests.sh --platform simulator` |
| `runtime-tests-device` | Physical device runtime tests | `--device-udid`, `--timeout N`, `--class-filter NAME` | `run-runtime-tests.sh --platform device` |
| `runtime-tests-macos` | macOS native runtime tests | `--skip-regen`, `--timeout N`, `--class-filter NAME` | `run-runtime-tests.sh --platform macos` |
| `validate` | Library validation compile gate | `--tier N`, `--filter NAME`, `--verbose`, `--quick`, `--fetch`, `--serial`, `--jobs N` | `validate-libraries.sh` |
| `fetch` | Fetch/build library xcframeworks | `--tier N`, `--filter NAME`, `--force`, `--list` | `scripts/fetch-libraries.sh` |
| `pack` | Build NuGet packages | `--version SEMVER`, `--output-dir DIR` | `pack-all.sh` |

### Decisions

**Golden files — DELETE, don't port.**
The `BindingTests/golden/` directory (115K lines of snapshot files, 2 shell scripts) is redundant with the existing test gates: 7,800+ unit tests catch emission patterns, `validate` (90 targets) catches real-world compile failures, BindingTests compile check catches the same library, and runtime tests catch behavior. Golden files only cover 1 library and every intentional generator change requires regenerating 115K lines of churn. The test suite has outgrown them.

**Coverage report — keep as standalone Python, not a Nuke target.**
`generate-coverage-report.sh` is a 1,200-line Python script wrapped in 20 lines of bash. It reads JSON files and produces a JSON report — no build dependencies. Porting 1,200 lines of Python to C# has no real benefit. Instead:
1. Extract the Python to `scripts/coverage-report.py` (standalone, no bash wrapper)
2. CI calls `python3 scripts/coverage-report.py` directly
3. Nuke's `binding-tests` target invokes it via `ProcessTasks`

**`check-baselines.sh` — already ported, just delete.**
Logic is in `Build.Test.cs` `RunCheckBaselines()`. Shell script is dead code.

### Scripts to DELETE (21 → 4 kept)

**Root (6 scripts → 1 Nuke shim):**
- [x] `build.sh` — Replaced with Nuke entry point shim
- [x] `run-tests.sh` + `run-tests.sh.original` — Use `nuke test`
- [x] `validate-libraries.sh` + `validate-libraries.sh.original` — Use `nuke validate`
- [x] `pack-all.sh` + `pack-all.sh.original` — Use `nuke pack`

**BindingTests/ (13 scripts → 0):**
- [x] `build-and-test.sh` + `build-and-test.sh.original` — Use `nuke binding-tests`
- [x] `run-runtime-tests.sh` + `run-runtime-tests.sh.original` — Use `nuke runtime-tests-simulator`
- [x] `build-xcframework.sh` — Internal; Nuke `Build.BindingTests.cs` handles this
- [x] `regenerate-bindings.sh` — Internal; Nuke handles this
- [x] `build-bridge.sh` — Internal; Nuke handles this
- [x] `build-async-wrapper.sh` — Internal; Nuke handles this
- [x] `build-wrapper-device.sh` — Internal; Nuke handles this
- [x] `generate-coverage-report.sh` — Extracted Python to `scripts/coverage-report.py`, deleted bash wrapper
- [x] `check-baselines.sh` — Already ported to `Build.Test.cs`; deleted
- [x] `generate-bridge-coverage.sh` — Deleted
- [x] `golden/` — Deleted entire directory (golden files + both scripts)

**scripts/ (2 build scripts → 0, CI scripts stay):**
- [x] `fetch-libraries.sh` — Use `nuke fetch`
- [x] `lib.sh` — Only used by `fetch-libraries.sh`; deleted

**src/ (1 script → 0):**
- [x] `Swift.Bindings.Sdk/build-sdk.sh` — Superseded by `nuke pack`

**.dotnet/ (keep):**
- `dotnet-install.sh` — .NET SDK bootstrapper, standard infrastructure, not ours

**KEEP (4 items):**
- `scripts/ci/ci_ios_test.py` — CI-specific simulator orchestrator (Python, manages tiered test execution with step timeouts). Different concern from build system.
- `scripts/ci/sim_manager.py` — Used by `ci_ios_test.py`
- `scripts/coverage-report.py` — NEW: extracted from `generate-coverage-report.sh` (standalone Python, no bash)
- `src/Swift.Runtime/swift/build-runtime.sh` — Builds native Swift dylib. This is Swift compilation, not .NET build orchestration. Could port to Nuke long-term but lower priority.

### Pre-deletion work

- [x] **Extract coverage report Python** — Extracted to `scripts/coverage-report.py` with `--abi-json`, `--binding-report`, `--output-dir` flags. Nuke's `Build.Test.cs` calls it via `ProcessTasks`.
- [x] **Rename existing targets** — `ValidateLibraries` → `Validate`, `FetchLibraries` → `Fetch`, `--fetch` → `--fetch-first`

### Migration order

1. Extract coverage report Python to `scripts/coverage-report.py`
2. Delete golden files directory
3. Rename targets for brevity (`validate-libraries` → `validate`, `fetch-libraries` → `fetch`)
4. Update CI workflows to call `nuke` directly (see Part 3)
5. Update documentation (CLAUDE.md, CONTRIBUTING.md, rules, memory — see Part 4)
6. Delete all shell scripts and `.original` files
7. Final CI run to verify everything works

---

## Part 2: Code Quality Fixes

### Critical

- [x] **Error messages reference old shell scripts** — Updated to say `nuke fetch`.
- [x] **Schema out of sync** — Updated `FetchLibraries` → `Fetch`, `ValidateLibraries` → `Validate`, `Fetch` param → `FetchFirst`.
- [x] **Unused `[PathVariable]` injection** — Removed `NmTool` (unused). Kept `XcRunTool` (used in Build.BindingTests.cs). Cleaned up NoWarn.

### Moderate

- [x] **Hardcoded `net10.0` TFM** — Extracted to `Build.DotNetTfm` constant and `ApplePlatform.BaseTfm`. Updated all build files.
- [x] **No cycle detection in dependency resolution** — Added `visiting` set to `ComputeClosure()` for cycle detection.
- [ ] **`VersionScope.cs` uses regex for XML** — Fragile to whitespace changes in `.csproj` files. Works today with known inputs but could break silently.

### Low Priority

- [ ] **`SwiftSourceStripper.cs` brace counting** — Doesn't account for braces in string literals or comments. Low risk since inputs are generated Swift code.
- [ ] **`PlistGenerator.cs` no XML escaping** — Input params inserted directly. Low risk since inputs are code-generated module names.
- [ ] **`XcRun.cs` no caching** — SDK paths and tool locations queried repeatedly. Add a `Dictionary<string, string>` cache.

### Optimizations

- [ ] **Deduplicate wrapper build logic** — `Build.BindingTests.cs` `BuildModuleSlice()` and `BuildDeviceModuleSlice()` are ~95% identical. Extract shared method.
- [ ] **Deduplicate native artifact injection** — `Build.RuntimeTests.cs` has 4 `Inject*()` methods following the same copy pattern. Extract generic `InjectArtifact()`.
- [ ] **Centralize `EscapeArgument()`** — `SwiftCompiler`, `SwiftFrontend`, `XcodeBuild`, `SymbolGraphExtract` each have their own copy. Move to shared utility.
- [ ] **Extract app bundle path constants** — `"net10.0-ios"`, `"iossimulator-arm64"`, etc. appear in 10+ locations.

---

## Part 3: GitHub Actions Migration

Both `ci.yml` and `release.yml` still call shell scripts. After Part 1, those scripts won't exist.

### `ci.yml` changes

- [x] Add `dotnet tool restore` step (installs Nuke from `.config/dotnet-tools.json`)
- [x] **build-and-test job**: Replace `./build.sh` + 3x `dotnet test` with `nuke test`
- [x] **bindingtests job**: Replace `./build-and-test.sh --strict` + `./check-baselines.sh` + inline Python must-pass check with `nuke binding-tests --strict`
- [x] **bindingtests job**: Replace `./generate-coverage-report.sh` with `python3 scripts/coverage-report.py`
- [x] **bindingtests job**: Update `ci_ios_test.py` invocation — verified and updated to call `nuke runtime-tests-simulator`
- [x] **package-smoke job**: Replace manual `dotnet pack`/`dotnet publish` with `nuke pack --version 0.0.0-smoke`

### `release.yml` changes

- [x] Add `dotnet tool restore` step
- [x] **build-and-test job**: Same as `ci.yml` above
- [x] **bindingtests job**: Same as `ci.yml` above
- [x] **release job — build/pack**: Replaced with `nuke pack --version $VERSION --output-dir artifacts`
- [x] **release job — version patching**: Nuke's `VersionScope` handles this. No more `sed` blocks.
- [x] **release job — NuGet publish, git tag, release notes**: Kept as-is

### Prerequisites

- [x] Verify `dotnet tool restore` + `nuke` works in CI — added `build.sh` entry point shim
- [x] Check that `ci_ios_test.py` doesn't shell out to deleted scripts — updated to use `nuke`
- [ ] Test in a PR CI run before merging

---

## Part 4: Documentation Updates

- [x] **CLAUDE.md** — Rewrote "Building & Testing", repo structure, validation, SDK, and gates sections for Nuke.
- [x] **CONTRIBUTING.md** — Rewrote build/test/validation instructions for Nuke.
- [x] **`.claude/rules/bindingtests.md`** — Updated scripts table to Nuke targets, updated all command references.
- [x] **`.claude/rules/constraints.md`** — Updated validation cache invalidation reference.
- [x] **Memory files** — Updated `feedback_validation_workflow.md` and `feedback_no_redundant_runs.md` for Nuke commands.
- [ ] **Wiki** — Check if GitHub wiki pages reference shell scripts and update (separate task).
