# BindingTests

BindingTests are the repository's end-to-end ABI and runtime gate. Unit tests catch generator logic bugs; BindingTests catch generated-code compile failures, ABI mismatches, calling-convention bugs, marshalling crashes, and lifetime mistakes that unit tests cannot prove.

## What This Directory Contains

- `Sources/SwiftBindingsTestLib/` - Swift test library used as generator input.
- `RuntimeTestsApp/` - iOS simulator/device runtime tests.
- `RuntimeTestsApp.MacCatalyst/` - Mac Catalyst runner.
- `RuntimeTestsApp.tvOS/` - tvOS simulator runner.
- `output/` - generated bindings and reports from the latest BindingTests generation pass.
- `build/` and `obj/` - build artifacts and framework slices.

## Current Workflow

Use Nuke from the repository root. Do not use the old shell-script workflow.

| Command | Purpose |
|---|---|
| `nuke binding-tests` | Default inner loop: regenerate, compile, build app, run iOS Simulator runtime tests. |
| `nuke binding-tests --compile-only` | Compile gate only: regenerate + compile-check; no app build or runtime execution. Fail-closed by default. |
| `nuke binding-tests --skip-regen` | Reuse existing generated bindings, build/run runtime tests. |
| `nuke binding-tests --skip-build` | Reuse existing app build, install/run only. |
| `nuke binding-tests --class-filter NAME` | Run one runtime test class in the simulator path. |
| `nuke binding-tests --device` | Run physical iOS device / NativeAOT path. |
| `nuke binding-tests --macos` | Run macOS path. |
| `nuke binding-tests --catalyst` | Run Mac Catalyst path. |
| `nuke binding-tests --tvos` | Run tvOS simulator path. |
| `nuke binding-tests --permissive` | Opt out of compile-only fail-closed behavior for local exploration. |

Platform flags compose. For example, `nuke binding-tests --sim --device` runs both simulator and device paths.

For generator, parser, emitter, or marshaler changes, the usual sequence is:

```bash
nuke test
nuke binding-tests --compile-only
nuke binding-tests --skip-regen
```

Add `nuke binding-tests --device` when changes touch calling conventions, struct marshalling, P/Invoke signatures, or a NativeAOT-specific skip. `nuke validate` is not a routine BindingTests step; run it only for larger/cross-cutting generator changes, pre-release sweeps, or a specific real-world library canary.

## Output Files

Normal generation output includes:

- `output/SwiftBindingsTestLib.cs` - generated C# bindings.
- `output/SwiftBindingsTestLib.SwiftUIBridge.swift` - generated SwiftUI bridge when applicable.
- `output/binding-report.json` - binding completeness and skip details.
- `output/binding-emission-report.json` - emission-time diagnostics and rejection details.
- `output/binding-artifact-manifest.json` - artifact manifest used to rederive reports.

`output/coverage-matrix.json` is not produced by normal `nuke binding-tests`. It is emitted only when `build/scripts/coverage-report.py` is run manually with ABI JSON and `binding-report.json`.

## Runtime Test Architecture

Runtime tests extend `TestBase`. Discovery is descriptor-based through `TestDiscoveryGenerator`, not runtime reflection enumeration. The runner receives `--platform simulator|device`; macOS and Catalyst currently use simulator-mode runner semantics where relevant, while runtime-detected attributes handle Mono-specific and Catalyst-x64-specific skips.

Common test directories:

- `RuntimeTestsApp/Marshalling/` - type round trips, optionals, strings, enums, structs, classes.
- `RuntimeTestsApp/Lifetime/` - retain/release, ownership, dispose safety, GC stress.
- `RuntimeTestsApp/Closures/` - escaping/non-escaping closures, throwing closures, callback lifetimes.
- `RuntimeTestsApp/Async/` - async method and callback patterns.
- `RuntimeTestsApp/Generics/` - generic types, constraints, specialization behavior.
- `RuntimeTestsApp/Protocols/` - witness dispatch, existentials, protocol proxies.
- `RuntimeTestsApp/CrossModule/` - cross-module bindings and existential behavior.
- `RuntimeTestsApp/SwiftUIBridge/` - SwiftUI bridge behavior.
- `RuntimeTestsApp/Infrastructure/` - `TestBase`, descriptors, assertions, logging, results, skip attributes.

## Test Classification

Tests are classified by attributes. Prefer fixing generator/runtime bugs over adding skips.

| Attribute | Behavior | Use case |
|---|---|---|
| none | Runs everywhere the target path executes. | Default for working tests. |
| `[Skip("reason")]` | Always skipped. | Known project bug, missing entry point, or unsupported generated surface that is broken everywhere. |
| `[SkipOnSimulator("reason")]` | Skips simulator-mode paths, runs on device. | CLI-platform-specific simulator limitation. |
| `[SkipOnDevice("reason")]` | Skips physical device / NativeAOT path, runs on simulator. | Device-specific or NativeAOT-specific limitation. |
| `[SkipOnMonoJit("reason")]` | Method-level only; skips wherever the process runs on Mono. | Confirmed Mono runtime limitation such as filed Issue 1. |
| `[SkipOnCatalystX64("reason")]` | Method-level only; skips Mac Catalyst x64. | Confirmed Catalyst-x64 instability covered by upstream Issue 4. |
| `[Slow]` | Marker only; still runs. | Stress or long-running tests. |
| `[MonoJitCrash]` | Deprecated; do not use. | Historical only. Diagnose the root cause and use a narrow current attribute only for confirmed limitations. |

Before classifying a runtime crash as upstream, verify the generated C# P/Invoke exactly matches the Swift `@_cdecl` wrapper: calling convention, parameter count, parameter types, library name, and entry point symbol. Most historical "upstream" crashes were generator/runtime bugs.

Confirmed upstream issues are cataloged in `src/docs/Future/upstream-issues-README.md` and `src/docs/Future/upstream-issue-*.md`. The authoritative classification memory is `feedback_mono_jit_blame.md`.

## Adding Coverage

When fixing or adding generator behavior, add the Swift pattern to `Sources/SwiftBindingsTestLib/` and add matching runtime assertions under the appropriate `RuntimeTestsApp/` domain. Test files are organized by domain, not by milestone or session.

Useful habits before writing runtime tests:

1. Regenerate and inspect `output/SwiftBindingsTestLib.cs` first.
2. Identify non-blittable parameters, missing entry points, and generated wrapper symbols before running slow gates.
3. Use default/no attribute for tests expected to run everywhere.
4. Use targeted `[SkipOn*]` only for confirmed platform/runtime limitations.
5. Capture slow command output once and inspect the saved log instead of rerunning just to see more lines.

## Debugging Runtime Failures

When a test crashes or returns wrong values:

1. Add diagnostic logging first; do not iterate blindly.
2. Compare the generated C# P/Invoke declaration with the Swift `@_cdecl` wrapper in `output/SwiftBindingsTestLib.swift`.
3. Isolate constructor vs getter/setter failures by hardcoding known Swift wrapper values when useful.
4. Confirm the wrapper symbol was not stripped: check the generated Swift file and `nm -g` on the compiled framework.
5. If the same approach fails repeatedly, step back and question the hypothesis.

## Toolchain Notes

- .NET SDK: 10.0.x, governed by `global.json`.
- macOS + Xcode are required for Apple-platform BindingTests.
- iOS simulator runtime is required for the default `nuke binding-tests` path.
- Device runs require a reachable physical iOS device.
