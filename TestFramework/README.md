# TestFramework

Comprehensive test suite for the Swift Bindings generator. Tests are organized into two layers that answer distinct questions about correctness.

## Two-Layer Test Model

### Layer 1: Generator/Coverage Tests

**Question answered**: "Did we generate the right C# code from the Swift ABI?"

- **Scripts**: `build-and-test.sh`, `generate-coverage-report.sh`
- **What it does**: Builds the Swift test library (`SwiftBindingsTestLib`) as an xcframework, runs the binding generator against it, and produces a coverage report tracking which Swift features emitted successfully.
- **Output**: `output/Swift.SwiftBindingsTestLib.cs` (generated bindings), `output/binding-report.json` (skip details), `output/coverage-matrix.json` (feature coverage)
- **A failure means**: Generator bug in the parser, marshaler, or emitter. The generator either crashed, produced invalid C#, or skipped a member it should have handled.

### Layer 2: Runtime ABI/Marshalling Tests

**Question answered**: "Does the generated C# code actually work at runtime?"

- **Script**: `run-runtime-tests.sh`
- **What it does**: Regenerates bindings (Layer 1), compiles the `RuntimeTestsApp` iOS simulator app against them, deploys to a simulator, and runs the test suite.
- **Output**: Console output with pass/fail per test, overall `TEST SUCCESS` or `TEST FAILURE` marker
- **A failure means**: Interop bug — marshalling produces wrong values, memory management is incorrect, ABI calling conventions don't match, or SafeHandle lifecycle is broken.

### When to Run Each

| Scenario | Layer 1 | Layer 2 |
|----------|---------|---------|
| Changed parser/marshaler/emitter code | Yes | Yes (after Layer 1 passes) |
| Added new Swift test files | Yes | If runtime tests exist for the feature |
| Changed runtime C# test code only | No | Yes (with `--skip-regen`) |
| Pre-merge validation | Yes | Yes (`--tier 2`) |
| Nightly/release validation | Yes | Yes (`--tier 3`) |

## Running the Tests

### Layer 1

```bash
cd TestFramework

# Full pipeline: build xcframework + generate bindings
./build-and-test.sh

# Generate coverage report (requires bindings to exist)
./generate-coverage-report.sh
```

### Layer 2

```bash
cd TestFramework

# Run Tier 1 smoke tests (default, < 30 seconds)
./run-runtime-tests.sh

# Run Tier 2 merge gate tests (< 3 minutes)
./run-runtime-tests.sh --tier 2

# Run Tier 3 nightly tests with flake detection (< 15 minutes)
./run-runtime-tests.sh --tier 3

# Skip binding regeneration (use existing bindings)
./run-runtime-tests.sh --tier 2 --skip-regen

# Custom timeout
./run-runtime-tests.sh --timeout 120
```

### Full Validation Sequence

After any generator code change:

```bash
# 1. Unit tests
./run-tests.sh

# 2. Layer 1 coverage
cd TestFramework
./build-and-test.sh
./generate-coverage-report.sh

# 3. Layer 2 runtime
./run-runtime-tests.sh --tier 2
```

## Test Tiers

| Tier | Purpose | Budget | Scope |
|------|---------|--------|-------|
| **Tier 1** | PR gate (fast smoke) | < 30 seconds | Core marshalling round-trips: blittable, string, enum, class |
| **Tier 2** | Merge gate (standard) | < 3 minutes | Full matrix minus stress: all marshalling, lifetime, negative-path tests |
| **Tier 3** | Nightly (full + stress) | < 15 minutes | Everything including concurrency, GC pressure, large data. Each test runs 3x for flake detection. |

## Runtime Test Architecture

The `RuntimeTestsApp/` is an iOS simulator application with a discovery-based test runner:

- All test classes extend `TestBase` and are auto-discovered via reflection
- Tests are tagged with `[TestTier(TestTier.TierN)]` at method or class level
- The `--tier N` CLI argument controls which tiers execute (runs tiers 1 through N)
- Infrastructure in `RuntimeTestsApp/Infrastructure/` provides assertion helpers, GC utilities, lifetime tracking, and structured logging

### Test Categories

```
RuntimeTestsApp/
├── Marshalling/          # Type round-trip tests (blittable, string, enum, class)
├── Lifetime/             # Retain/release, dispose safety, access-after-dispose
├── Concurrency/          # Parallel operations, GC pressure, stress tests
├── Async/                # Async method tests (stubs — deferred until async bindings land)
├── Protocols/            # Protocol witness dispatch (stub — deferred until protocol interfaces land)
└── Infrastructure/       # TestBase, TestResults, TestLogger, LifetimeTracker
```

## Toolchain Requirements

| Component | Version | Notes |
|-----------|---------|-------|
| .NET SDK | 10.0.x | `global.json` at repo root sets base version with `latestMajor` roll-forward |
| Xcode | 16.0+ | Swift 6.0 toolchain required for test library compilation |
| macOS | 14.0+ (Sonoma) | Required for .NET 10 iOS workload |
| iOS Simulator | 17.0+ | Runtime target for Layer 2 tests |

Ensure the iOS Simulator runtime is installed via Xcode. The `run-runtime-tests.sh` script will attempt to boot an iPhone 16 or iPhone 15 simulator if none is already running.

## Understanding Coverage Report Output

After running `./generate-coverage-report.sh`, you'll see:

```
Must-pass features: 92/93 passing, 1 degraded, 0 missing
Known-unsupported features: 47/52 have tests (5 compiled out)
```

- **must_pass / passing**: Feature has Swift test code and all binding members emitted successfully.
- **must_pass / degraded**: Some binding members were skipped. The WARNING section shows which members and why.
- **must_pass / missing**: No test file exists (should not happen).
- **known_unsupported**: Features the generator intentionally doesn't handle yet (actors, property wrappers, etc.).

See `src/docs/testframework-enhancement-plan.md` for the full contract matrix and skip reason reference.

## Test Profiles

Two execution profiles cover different validation needs. All are local (no CI yet).

| Profile | Command | What Runs | Crash Tolerance |
|---------|---------|-----------|-----------------|
| **PR Gate** | `./run-tests.sh` | Unit + integration + compile gate + baselines + runtime `--tier 2` (all classes) | Allowlist: crashes tolerated only in `[CrashRisk]` classes |
| **Nightly** | Manual | `./run-runtime-tests.sh --tier 3` with flake detection (3x per test) | Full reporting |

### PR Gate (`./run-tests.sh`)

The primary validation command. Runs in ~10 minutes:

1. **Unit tests** — 2,395 xUnit tests (parser, marshaler, emitter, type database)
2. **Integration tests** — 699 end-to-end binding generation tests
3. **TestFramework Layer 1** — build xcframework, regenerate bindings, compile-check, coverage report
4. **Baseline checks** — generator exit code, degraded count, compiled-out count, strip count, crash-risk count
5. **TestFramework Layer 2** — runtime tests at `--tier 2` on iOS Simulator

Crashes during runtime tests are tolerated only if they occur in a class on the crash allowlist (matching `[CrashRisk]` attributes). A crash in any other class fails the run.

### Nightly (manual)

For thorough validation after significant changes:

```bash
cd TestFramework
./run-runtime-tests.sh --tier 3 --timeout 120
```

Tier 3 runs every test 3x for flake detection and includes stress/concurrency tests. Results are reported but not gated — used to identify intermittent failures for investigation.
