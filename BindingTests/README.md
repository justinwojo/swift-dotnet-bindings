# BindingTests

Comprehensive test suite for the Swift Bindings generator. Tests are organized into two layers that answer distinct questions about correctness.

## Two-Layer Test Model

### Layer 1: Generator/Coverage Tests

**Question answered**: "Did we generate the right C# code from the Swift ABI?"

- **Scripts**: `build-and-test.sh`, `generate-coverage-report.sh`
- **What it does**: Builds the Swift test library (`SwiftBindingsTestLib`) as an xcframework, runs the binding generator against it, and produces a coverage report tracking which Swift features emitted successfully.
- **Output**: `output/SwiftBindingsTestLib.cs` (generated bindings), `output/binding-report.json` (skip details), `output/coverage-matrix.json` (feature coverage)
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
| Pre-merge validation | Yes | Yes |

## Running the Tests

### Layer 1

```bash
cd BindingTests

# Full pipeline: build xcframework + generate bindings
./build-and-test.sh

# Generate coverage report (requires bindings to exist)
./generate-coverage-report.sh
```

### Layer 2

```bash
cd BindingTests

# Run on iOS Simulator (default) — skips [MonoJitCrash] and [Skip] tests
./run-runtime-tests.sh

# Skip binding regeneration (use existing bindings)
./run-runtime-tests.sh --skip-regen

# Run on physical iPhone (NativeAOT) — runs [MonoJitCrash] tests, skips [Skip]
./run-runtime-tests.sh --platform device

# Run a single test class
./run-runtime-tests.sh --class BlittableRoundTripTests --skip-regen

# Custom timeout
./run-runtime-tests.sh --timeout 120

# Flake detection (each test runs 3x)
./run-runtime-tests.sh --flake-detect
```

### Full Validation Sequence

After any generator code change:

```bash
# 1. Unit tests
./run-tests.sh

# 2. Layer 1 coverage
cd BindingTests
./build-and-test.sh
./generate-coverage-report.sh

# 3. Layer 2 runtime
./run-runtime-tests.sh
```

## Test Classification

Tests are classified by attributes instead of tiers:

| Attribute | Simulator | Device (NativeAOT) | Use Case |
|-----------|-----------|---------------------|----------|
| *(none)* | Runs | Runs | Default — all working tests |
| `[MonoJitCrash]` | Skipped | Runs | Mono JIT crash (jit-info.c:918, non-blittable CallConvSwift) |
| `[Skip("reason")]` | Skipped | Skipped | Generator bugs, missing entry points — broken everywhere |
| `[Slow]` | Runs | Runs | Stress/concurrency tests (just a marker) |

## Runtime Test Architecture

The `RuntimeTestsApp/` is an iOS simulator application with a discovery-based test runner:

- All test classes extend `TestBase` and are auto-discovered via reflection
- Tests use `[MonoJitCrash]`, `[Skip("reason")]`, or `[Slow]` attributes for classification
- The `--platform simulator|device` CLI argument controls which tests are skipped
- Infrastructure in `RuntimeTestsApp/Infrastructure/` provides assertion helpers, GC utilities, lifetime tracking, and structured logging

### Test Categories

```
RuntimeTestsApp/
├── Marshalling/          # Type round-trip tests (blittable, string, enum, class)
├── Lifetime/             # Retain/release, dispose safety, access-after-dispose
├── Concurrency/          # Parallel operations, GC pressure, stress tests
├── Async/                # Async method tests
├── Closures/             # Closure marshalling (escaping, @convention(c))
├── ErrorHandling/        # Throwing methods, typed throws
├── Generics/             # Generic type tests (including hand-crafted ABI tests)
├── Metadata/             # Existential metadata tests
├── Operators/            # Operator overloading, struct equality
├── Patterns/             # Builder, composition, static factory, struct-backed enum
├── Protocols/            # Protocol witness dispatch, existential boxing
├── Properties/           # Subscripts, static singletons
├── Collections/          # Constructor collections, dictionary constructors
├── SwiftUIBridge/        # SwiftUI bridge tests (gated by #if SWIFTUI_BRIDGE)
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

See `src/docs/Completed/bindingtests-enhancement-plan.md` for the full contract matrix and skip reason reference.

## Test Profiles

| Profile | Command | What Runs | Crash Tolerance |
|---------|---------|-----------|-----------------|
| **PR Gate** | `./run-tests.sh` | Unit + integration + compile gate + baselines + runtime (simulator) | Any crash is a regression  |
| **Device** | `./run-runtime-tests.sh --platform device` | All tests including `[MonoJitCrash]` on NativeAOT | `[Skip]` tests still skipped |

### PR Gate (`./run-tests.sh`)

The primary validation command. Runs in ~10 minutes:

1. **Unit tests** — xUnit tests (parser, marshaler, emitter, type database)
2. **Integration tests** — end-to-end binding generation tests
3. **BindingTests Layer 1** — build xcframework, regenerate bindings, compile-check, coverage report
4. **Baseline checks** — generator exit code, degraded count, compiled-out count, strip count
5. **BindingTests Layer 2** — runtime tests on iOS Simulator (skips `[MonoJitCrash]` and `[Skip]`)

On simulator, all MonoJitCrash-prone tests are skipped. Any crash is treated as a regression.
