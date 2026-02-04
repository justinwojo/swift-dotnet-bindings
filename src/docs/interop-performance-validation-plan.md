# Interop Performance Validation Plan

Plan for measuring Swift-only call performance versus C# -> Swift interop call performance, with both CI-safe regression checks and deeper ad-hoc benchmarking.

**Date**: February 2026
**Scope**: `TestFramework` + a new standalone benchmark harness

---

## Goals

- Quantify overhead introduced by .NET <-> Swift bindings.
- Catch major regressions in interop overhead during normal development.
- Keep CI checks fast and stable while preserving a path for deep investigation.

## Non-Goals

- Platform-wide perf certification across all hardware.
- Replacing functional/runtime validation tests.
- Enforcing hard nanosecond thresholds in flaky CI environments.

---

## Proposed Structure

### 1) CI Perf Smoke Checks (inside TestFramework workflow)

Add a small, stable set of perf scenarios to validate "no large regression":

- Primitive roundtrip (`Int`/`Bool`)
- String in/out (small and medium payload)
- Array roundtrip (small fixed-size array)
- Class/object method call (reference type path)
- Callback/delegate roundtrip (single callback hop)

Characteristics:

- Low runtime cost (target: <60s total for perf smoke).
- Run on Release builds only.
- Assert against relative ceilings (ratios), not absolute nanoseconds.
- Designed to detect large changes (e.g., +30% to +50%), not micro-noise.

### 2) Standalone Perf Benchmarks (new slim project)

Create a dedicated ad-hoc benchmark harness for accurate measurement and profiling:

- BenchmarkDotNet-based .NET benchmarks for C# -> Swift path.
- Matching Swift-native baseline runner for same functions/inputs.
- Rich metrics: mean, p95, allocations, throughput, ratio vs native.
- Parameter sweeps for payload sizes and call frequency.

Characteristics:

- Not part of default CI gate.
- Used for perf investigations, optimization work, and release/perf reports.
- Produces versioned benchmark artifacts (JSON/Markdown summaries).

---

## Test Case Matrix (Initial)

1. `noop_int`: `Int -> Int` identity call (boundary overhead floor).
2. `math_small`: tiny arithmetic operation (very fine-grained call).
3. `string_small`: short UTF-8 string roundtrip.
4. `array_64`: fixed 64-element numeric array roundtrip.
5. `callback_once`: pass delegate/closure and invoke once.

Each case should be measured in two paths:

- Swift-native baseline
- C# binding interop path

Primary comparison metric:

- `interop_ratio = interop_time / swift_native_time`

---

## Repo Layout (Proposed)

- `TestFramework/Sources/SwiftBindingsTestLib/Performance/`  
  Swift functions explicitly designed for repeatable perf measurement.
- `TestFramework/perf-smoke.sh`  
  Runs lightweight perf checks and emits pass/fail on ratio thresholds.
- `perf/SwiftBindings.InteropBenchmarks/`  
  Standalone .NET BenchmarkDotNet project.
- `perf/SwiftBaselineRunner/`  
  Swift-native benchmark runner for matching scenarios.
- `perf/results/`  
  Output artifacts (JSON/Markdown), ignored or archived per policy.

---

## CI and Gating Strategy

Phase 1 (observe only):

- Run perf smoke checks in CI and publish numbers.
- No fail gate; establish baseline variance by runner type.

Phase 2 (soft gate):

- Fail only on clear regressions (example: ratio worsens >40% from baseline).
- Keep thresholds coarse to avoid false positives.

Phase 3 (tighten where stable):

- Use scenario-specific thresholds for mature cases.
- Continue leaving deep benchmarks as ad-hoc/manual.

---

## Implementation Phases

### Phase A - Baseline Plumbing

- Add Swift perf functions under `TestFramework` `Performance/`.
- Add minimal .NET invocations for matching calls.
- Add smoke script and machine-readable output.

### Phase B - Standalone Bench Harness

- Create BenchmarkDotNet project and baseline runner.
- Add scenario parameterization (payload sizes, iteration counts).
- Document how to run local perf sessions.

### Phase C - Regression Policy

- Collect baseline data across several runs.
- Add soft thresholds to CI perf smoke checks.
- Document interpretation and triage playbook.

---

## Risks and Mitigations

- Machine variance/noise -> use ratio metrics and coarse thresholds.
- JIT/warmup distortion -> include warmup and steady-state iteration windows.
- GC/allocation noise -> track allocations explicitly in benchmark output.
- Scope creep -> keep CI suite to 5-8 stable scenarios max.

---

## Acceptance Criteria

- Perf smoke suite exists in `TestFramework` and runs in under 60s.
- Standalone benchmark harness can compare native vs interop for initial 5 scenarios.
- Results include interop/native ratio for each scenario.
- CI has an explicit perf policy (observe-only or soft-gated) documented in repo.

