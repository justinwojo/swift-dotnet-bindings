# Test Infrastructure Migration Plan

## Problem Statement

The BindingTests runtime test suite (1,164 tests across 90 classes) uses a custom reflection-based test runner that has outgrown its design:

1. **One crash kills everything.** A single SIGSEGV or SIGKILL terminates the process and prevents all remaining tests from running. With tests running alphabetically, a crash in `ClosureEdgeCaseTests` (letter C) kills 700+ tests silently.

2. **No reliable test counting.** The build system parses raw console output for `"TEST SUCCESS"` / `"TEST FAILURE"` markers. FAIL lines are duplicated (once during test, once in summary). Skip counts vary depending on class-level vs method-level skips. The device csproj manually lists 15 directories but misses 7 (120 tests silently not compiled).

3. **Regressions sneak through.** There's no persisted baseline for runtime test pass counts (unlike `.validation-baseline.json` for the compile gate). A crash that drops the suite from 1,152 to 430 tests is reported as "crashed" with no indication of *how many* tests were lost.

4. **No test isolation.** Tests share process state. A test that corrupts Swift runtime metadata crashes all subsequent tests that touch existential containers — but passes fine in isolation with `--class-filter`.

5. **iOS-specific workarounds leak into test logic.** The test runner manually yields to `NSRunLoop` between tests to prevent the iOS watchdog from killing the process. This is infrastructure plumbing that shouldn't be in test code.

6. **NativeAOT incompatibility.** Reflection-based discovery (`Assembly.GetTypes()`, `GetMethods()`, `GetCustomAttribute<>()`, `Activator.CreateInstance()`, `method.Invoke()`) requires TrimmerRoots.xml hacks on NativeAOT and is fundamentally fragile. The device project manually lists directories to work around discovery failures, but misses 7 out of 22 directories.

## Current Architecture

```
RuntimeTestsApp/
├── Infrastructure/
│   ├── TestBase.cs         — Reflection-based discovery, assertions, skip attributes
│   ├── TestResults.cs      — Pass/fail/skip accumulator
│   ├── TestLogger.cs       — Structured logging with [PASS]/[FAIL] prefixes
│   └── LifetimeTracker.cs  — Swift allocation counter P/Invoke wrappers
├── Program.cs              — UIKit app delegate, test execution loop (simulator)
├── <domain>/*.cs           — 90 test classes extending TestBase
└── RuntimeTestsApp.csproj  — Auto-discovers all *.cs via default items

RuntimeTestsApp.Device/
├── Program.cs              — Separate entry point for NativeAOT (device)
├── RuntimeTestsApp.Device.csproj — Manually lists 15 directories (misses 7)
└── TrimmerRoots.xml        — Preserves test types for NativeAOT reflection
```

**Reflection points (all must be eliminated for NativeAOT):**
1. `Program.cs` line ~240: `Assembly.GetExecutingAssembly().GetTypes()` — discovers TestBase subclasses
2. `Program.cs` line ~320: `GetCustomAttribute<SkipAttribute>()` on classes — class-level skip check
3. `TestBase.cs` line ~34: `GetMethods(BindingFlags.Public | BindingFlags.Instance)` — discovers Test* methods
4. `TestBase.cs` line ~30-66: `GetCustomAttribute<>()` on methods — skip attribute checks
5. `TestBase.cs` line ~93: `method.Invoke(this, null)` — test method invocation
6. `Program.cs`: `Activator.CreateInstance(testClass, results)` — test class instantiation

**Result reporting:** Console markers (`TEST SUCCESS` / `TEST FAILURE`) parsed by `Build.RuntimeTests.cs`. Both `SimCtl.Launch` and `DeviceCtl.Launch` break their polling loops immediately on seeing the marker and may kill the process — any file written *after* the marker can be lost.

**Platform skipping:** `[Skip]`, `[SkipOnSimulator]`, `[SkipOnDevice]` attributes checked before each test.

## Ecosystem Assessment (March 2026)

| Option | NativeAOT? | iOS Runner? | Crash Isolation? | Maturity |
|---|---|---|---|---|
| **xUnit v3 + AOT packages** | Yes (source gen) | No official runner. DeviceRunners (community, modest). | No | Production for desktop; unproven on iOS |
| **TUnit** | Yes (source gen from day 1) | Not tested on iOS | No | Production for desktop; zero iOS validation |
| **Microsoft.Testing.Platform** | Yes (by design) | Hostable in any app (theoretical) | Optional process isolation (not on iOS) | Ships with .NET 9+; custom ITestFramework supported; no iOS reference impl |
| **XHarness** | Partial (via dotnet/runtime patches) | Yes (internal TestRunner libs, public repo) | No in-app; per-app-bundle only | Stable, powers dotnet/runtime CI. TestRunner libs not published as NuGet. |
| **Custom source gen + resume orchestration** | Yes | Uses existing runner | Yes (resume-on-crash) | Needs building |

**Decision: No framework migration now.** No framework has a proven, low-risk iOS runner story. The source generator approach gives NativeAOT compatibility without framework migration. Revisit when xUnit v3 or TUnit has validated iOS support (xUnit v3 has MTP support, MTP positions itself for NativeAOT/trimming — the pieces are converging but aren't assembled yet).

## Approved Plan: Five Independent Improvements

### Improvement 1: Source Generator — Zero-Reflection Test Discovery

**Goal:** Eliminate all 6 reflection points. Tests discover and invoke without any runtime reflection, working identically on Mono JIT and NativeAOT.

**Project:** `SwiftBindings.TestDiscovery` — a Roslyn incremental source generator.

**What it emits:**

```csharp
// Auto-generated by SwiftBindings.TestDiscovery
[GeneratedCode("SwiftBindings.TestDiscovery", "1.0")]
public static class TestRegistry
{
    public static IReadOnlyList<TestClassDescriptor> Classes { get; } = new TestClassDescriptor[]
    {
        new TestClassDescriptor(
            name: "ClosureEdgeCaseTests",
            factory: (results) => new ClosureEdgeCaseTests(results),
            skipReason: null,
            skipOnSimulator: null,
            skipOnDevice: null,
            methods: new TestMethodDescriptor[]
            {
                new("TestClosureReturningOptional",
                    invoker: (instance) => ((ClosureEdgeCaseTests)instance).TestClosureReturningOptional(),
                    skip: null, skipOnSim: null, skipOnDevice: null),
                new("TestClosureCapturingClass",
                    invoker: (instance) => ((ClosureEdgeCaseTests)instance).TestClosureCapturingClass(),
                    skip: "Missing entry point", skipOnSim: null, skipOnDevice: null),
                // ...
            }),
        // ...
    };
}

public record TestClassDescriptor(
    string Name,
    Func<TestResults, TestBase> Factory,       // replaces Activator.CreateInstance
    string? SkipReason,
    string? SkipOnSimulator,
    string? SkipOnDevice,
    IReadOnlyList<TestMethodDescriptor> Methods);

public record TestMethodDescriptor(
    string Name,
    Func<TestBase, ValueTask> Invoker,          // replaces method.Invoke — normalized async delegate
    string? Skip,
    string? SkipOnSim,
    string? SkipOnDevice);
```

The generator normalizes all invokers to `Func<TestBase, ValueTask>`:
- Sync methods: `(instance) => { ((T)instance).TestFoo(); return default; }`
- Async methods: `async (instance) => { await ((T)instance).TestFooAsync(); }`

**Key design points:**
- **Factory delegates** replace `Activator.CreateInstance()` — the generator emits `(results) => new ConcreteClass(results)` for each class
- **Invoker delegates** replace `method.Invoke()` — the generator emits direct delegates with unified `Func<TestBase, ValueTask>` signature (no `object?` return type ambiguity)
- **Attribute metadata** is read at compile time — no `GetCustomAttribute<>()` at runtime
- **Also emits `TestClasses.g.txt`** — a host-visible manifest listing every class and its methods (one `ClassName.MethodName` per line). The build orchestrator reads this to know the full test inventory, compute remaining classes after a crash, and synthesize CRASHED status for unfinished methods.

**TestBase changes:** `RunAllTestsAsync` takes a `TestClassDescriptor` parameter instead of using `GetMethods()`. The assertion helpers, GC helpers, timeout helpers, memory tracking, and LifetimeTracker all stay exactly as they are.

**Program.cs changes:** Iterates `TestRegistry.Classes` instead of `Assembly.GetTypes()`. No `Activator.CreateInstance`. No `GetCustomAttribute`.

**What this eliminates:**
- TrimmerRoots.xml (no reflection to preserve)
- NativeAOT type loading crashes during reflection
- The entire category of "test X works on simulator but crashes on device during discovery"

**What this does NOT fix:** The device project's manual 15-directory listing. The source generator only discovers classes from files that are already compiled into the project. Until Improvement 5 (unified project) ships with wildcard compile items, the device build still misses the 7 unlisted directories (120 tests). The source generator is a prerequisite for the unified project, not a replacement for it.

### Improvement 2: JSONL Result Output — Crash-Safe Structured Results

**Goal:** Machine-readable results that survive process crashes and don't depend on console marker parsing.

**Format: Append-only JSONL** (one JSON object per line, appended after each test). This is crash-safe by design — a crash mid-write at worst truncates the last line; all previous lines are valid.

```
{"class":"ClosureEdgeCaseTests","test":"TestClosureReturningOptional","status":"pass","ms":12}
{"class":"ClosureEdgeCaseTests","test":"TestClosureCapturingClass","status":"skip","reason":"Missing entry point"}
{"class":"ClosureEdgeCaseTests","test":"TestClosurePropertyGetter","status":"fail","error":"Expected 42, got 0","ms":3}
{"class_done":"ClosureEdgeCaseTests","tests_run":3}
```

Each test result is one line. After all methods in a class finish, a `class_done` record is emitted. This is the class-completion signal — only classes with a `class_done` record are considered fully completed. If the process crashes mid-class, that class has no `class_done` — the orchestrator excludes it from relaunch and synthesizes CRASHED status for its unfinished methods (see Improvement 4).

**Write protocol:**
1. Open file at start of test run (app Documents directory on iOS, working directory on macOS)
2. After each test completes: append one JSONL line + `StreamWriter.Flush()`
3. After all methods in a class finish: append `{"class_done":"ClassName","tests_run":N}` + flush
4. After all tests complete: write `{"done":true,"total":N,"passed":N,"failed":N,"skipped":N}`
5. Write `RESULTS FLUSHED` to stdout
6. *Then* write `TEST SUCCESS` or `TEST FAILURE` to stdout (backwards-compatible)

**Build system retrieval:**
- **Simulator:** `xcrun simctl get_app_container <udid> <bundleId> data` → read `Documents/test-results.jsonl`
- **Device:** `xcrun devicectl device copy from <udid> ...` or parse from stdout (JSONL lines also echo to console as structured log)
- **macOS:** Direct file read from working directory
- **Fallback:** If JSONL retrieval fails, fall back to existing console marker parsing (backwards-compatible during rollout)

**Build system changes to SimCtl.Launch/DeviceCtl.Launch:**
- Wait for `RESULTS FLUSHED` marker (with timeout) before checking `TEST SUCCESS`/`TEST FAILURE`
- Do NOT break the polling loop on `TEST SUCCESS`/`TEST FAILURE` alone — wait for `RESULTS FLUSHED` first
- This prevents killing the process before the file is fully written

### Improvement 3: Runtime Test Baseline — Regression Detection

**Goal:** Catch test count regressions the same way `.validation-baseline.json` catches compile gate regressions.

**Add to `.validation-baseline.json`:**
```json
"runtime_tests": {
    "simulator": { "pass": 1152, "fail": 0, "skip": 12, "crash": 0 },
    "device": { "pass": 1128, "fail": 1, "skip": 25, "crash": 0 }
}
```

**Build system behavior:**
- After JSONL aggregation, compare pass count against baseline
- **Fail if pass count drops** (regression)
- **Warn if pass count increases** (update baseline)
- On unfiltered runs with no crashes, auto-update baseline (same pattern as compile gate)

### Improvement 4: Resume-on-Crash Orchestration — Zero-Cost Crash Isolation

**Goal:** A crash in one test class does not prevent remaining classes from running. Zero overhead on healthy runs.

**How it works:**
1. Build system launches app with full test suite (normal run)
2. App writes JSONL incrementally as tests complete (including `class_done` markers)
3. **If app completes normally:** Done. Zero overhead. Identical to today.
4. **If app crashes (no `RESULTS FLUSHED` within timeout, or process exits abnormally):**
   a. Retrieve partial JSONL from app sandbox (`xcrun simctl get_app_container`)
   b. Identify completed classes = classes with a `class_done` record in the JSONL
   c. Identify the crashing class = last class with test records but no `class_done` (mark its incomplete tests as CRASHED)
   d. Compute remaining classes = (all classes from `TestClasses.g.txt`) − (completed classes) − (crashing class)
   e. Copy the JSONL out of the app sandbox into a host-side temp file (`/tmp/runtime-tests-run-{N}.jsonl`)
   f. Relaunch app with `--exclude-classes <comma-separated-list>` (completed + crashed classes passed as a single arg string)
   g. App filters `TestRegistry.Classes` to exclude the listed names; writes a fresh JSONL in its sandbox
   h. Repeat until all classes have either completed or crashed
   i. Aggregate all host-side JSONL files (`/tmp/runtime-tests-run-*.jsonl`) into final report

**Source of truth for the full class/method inventory:** `TestClasses.g.txt`, emitted by the source generator at compile time. This is a host-visible file (in the build output, not the app sandbox) containing one `ClassName.MethodName` per line. The orchestrator reads it to know the complete set of classes (by unique class name prefix) and their methods (needed to synthesize CRASHED status for unfinished methods). The app uses `TestRegistry.Classes` internally (the in-memory equivalent), but the orchestrator never reads `TestRegistry` directly.

**Class completion and deduplication:**
- A class is "completed" only when its `class_done` record appears in the JSONL
- A class that crashes mid-way has individual test records but no `class_done` — the orchestrator marks all remaining methods in that class as CRASHED based on the method list in `TestClasses.g.txt` (which includes method names per class)
- On relaunch, crashed classes are excluded (not rerun) — their partial results from the first run are kept in the host-side temp file
- Aggregation keys by fully-qualified test name (`ClassName.MethodName`). If a test name appears in multiple JSONL files (shouldn't happen with exclude-list, but defensive), last terminal result wins.

**Overhead analysis:**
- **Healthy run (99% of runs):** 0 extra launches, 0 extra seconds
- **One crash:** 1 extra launch (~1s overhead), loses only the crashing class's remaining tests
- **Multiple crashes:** N extra launches, but each subsequent launch excludes all previously-completed and crashed classes

**JSONL file lifecycle:**
1. App always writes to a fixed path in its sandbox (e.g., `Documents/test-results.jsonl`), starting fresh each launch
2. Before each relaunch, the orchestrator copies the JSONL out of the sandbox into `/tmp/runtime-tests-run-{N}.jsonl`
3. After the final launch (success or max retries), the orchestrator aggregates all `/tmp/runtime-tests-run-*.jsonl` files
4. On a healthy run (no crashes), there's only one file — no aggregation needed

**Implementation:**
- New CLI arg: `--exclude-classes <comma-separated-names>` — app parses and filters `TestRegistry.Classes` to skip these. This is a plain string argument, no file I/O needed — works on simulator, device, and macOS identically.
- `Build.RuntimeTests.cs` gets a retry loop around `SimCtl.Launch` / `DeviceCtl.Launch`
- Max retries: 5 (prevents infinite relaunch if every class crashes)
- `SimCtl` / `DeviceCtl` get a new `CopyResultsFromSandbox()` method using `xcrun simctl get_app_container` (simulator) or `xcrun devicectl device copy from` (device)

**Why this is better than per-class launches:**
- Per-class: 90 launches = ~90s overhead on EVERY run
- Resume-on-crash: 1 launch on healthy runs, 2-3 launches on crash runs. Typically 0-3s overhead.

### Improvement 5: Unified Test Project

**Goal:** One `.csproj` for simulator, device, and macOS. No manually-synced file lists.

**How:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <!-- Simulator (default) -->
  <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'iossimulator-arm64'">
    <MtouchLink>None</MtouchLink>
  </PropertyGroup>

  <!-- Device (NativeAOT) -->
  <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'ios-arm64'">
    <PublishAot>true</PublishAot>
    <PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>
  </PropertyGroup>

  <!-- macOS — separate TFM, conditional -->
  <!-- TBD: may keep RuntimeTestsApp.Mac as separate project if TFM switching is too complex -->
</Project>
```

**What this eliminates:**
- `RuntimeTestsApp.Device/` directory entirely
- `TrimmerRoots.xml` (source generator handles discovery)
- Manual 15-directory listing (wildcard includes all test files)
- Two separate `Program.cs` files (unified with `#if` for platform-specific entry points, or shared logic with platform-conditional UIKit/console)

**Risk mitigation (per Codex review):** Keep `RuntimeIdentifier`, `PublishAot`, linker/trimmer settings, native references, and `Program.cs` includes conditioned by RID/configuration. Don't merge RID-specific properties — condition them.

**Note:** macOS (`RuntimeTestsApp.Mac`) may stay as a separate project since it uses `net10.0` (no `-ios` TFM). Merging simulator + device is the primary win.

## Implementation Sessions

### Session 1: Source Generator (standalone, human-supervised)

**Improvements:** 1 (Source Generator)

**Why standalone:** This is the riskiest session — a new Roslyn incremental source generator that must produce exact delegate signatures for ~1,164 test methods across ~90 classes, handle async/sync method detection, parse custom attributes, and integrate without changing any test results. Course corrections are likely.

**Deliverables:**
1. Create `src/SwiftBindings.TestDiscovery/` project (Roslyn incremental source generator)
2. Implement `TestDiscoveryGenerator.cs`:
   - Discover all classes inheriting from `TestBase`
   - Discover all `Test*` public instance methods with 0 parameters
   - Detect async methods (return type `Task` or `ValueTask`) vs sync
   - Read `[Skip]`, `[SkipOnSimulator]`, `[SkipOnDevice]` attributes at compile time
   - Emit `TestRegistry` static class with `IReadOnlyList<TestClassDescriptor>`
   - Emit factory delegates: `(results) => new ConcreteClass(results)`
   - Emit invoker delegates: `Func<TestBase, ValueTask>` (normalized async for both sync and async methods)
   - Emit `TestClasses.g.txt` with one `ClassName.MethodName` per line
3. Create `TestDescriptors.cs` with `TestClassDescriptor` and `TestMethodDescriptor` records
4. Unit test the generator (verify discovery, attribute parsing, async detection, delegate signatures)
5. Reference generator from `RuntimeTestsApp.csproj`
6. Update `TestBase.RunAllTestsAsync` to use `TestMethodDescriptor.Invoker` instead of `method.Invoke()`
7. Update `Program.cs` to iterate `TestRegistry.Classes` instead of `Assembly.GetTypes()`
8. Remove reflection from skip attribute checks (use descriptor metadata)

**Validation gate:** `nuke runtime-tests-simulator` — exact same test count, same pass/fail/skip results as before. The source generator is a transparent replacement for reflection, not a behavior change.

**Success criteria:** All reflection eliminated from test discovery and invocation. `TrimmerRoots.xml` no longer needed (but not deleted yet — that's Session 4).

---

### Sessions 2-4: Build System Infrastructure (orchestrator, autonomous)

Use `/Users/wojo/Dev/session-orchestrator-prompt.md` to execute sessions 2-4 sequentially.

---

### Session 2: JSONL Results + Baseline (Improvements 2 + 3)

**Deliverables:**
1. Add JSONL append-write to `TestResults.cs`:
   - New `StreamWriter` opened at test run start
   - After each test: append `{"class":"...","test":"...","status":"...","ms":N}` + flush
   - After each class completes: append `{"class_done":"...","tests_run":N}` + flush
   - After all tests: append `{"done":true,"total":N,...}` + flush
2. Update `Program.cs` (all platforms):
   - Write JSONL to app Documents directory (iOS) or working directory (macOS)
   - Write `RESULTS FLUSHED` to stdout after JSONL is fully written
   - Then write `TEST SUCCESS` / `TEST FAILURE` (backwards-compatible)
3. Update `SimCtl.cs` and `DeviceCtl.cs`:
   - Wait for `RESULTS FLUSHED` before checking `TEST SUCCESS`/`TEST FAILURE`
   - Add `CopyResultsFromSandbox()` using `xcrun simctl get_app_container`
4. Update `Build.RuntimeTests.cs`:
   - Parse JSONL results after each launch
   - Fall back to console marker parsing if JSONL retrieval fails
5. Add `runtime_tests` baseline to `.validation-baseline.json`:
   - `"simulator": { "pass": N, "fail": N, "skip": N, "crash": 0 }`
   - Populate with actual counts from first successful run
6. Add baseline comparison to `Build.RuntimeTests.cs`:
   - Fail if pass count drops
   - Warn + auto-update if pass count increases (on unfiltered, no-crash runs)

**Validation gate:** `nuke runtime-tests-simulator` — tests pass, JSONL file is written and retrievable, baseline comparison works. Console markers still work as fallback.

---

### Session 3: Resume-on-Crash Orchestration (Improvement 4)

**Prior context:** Session 2 added JSONL output with `class_done` records and sandbox file retrieval.

**Deliverables:**
1. Add `--exclude-classes <comma-separated-names>` CLI arg to the test app:
   - Parse in `Main()` alongside existing `--class` and `--platform`
   - Filter `TestRegistry.Classes` to exclude listed names
2. Add retry loop to `Build.RuntimeTests.cs` (`RunOnSimulator` / `RunOnDevice`):
   - After crash detection: copy JSONL from sandbox, parse completed/crashed classes
   - Compute exclude list from `class_done` records + crashing class
   - Synthesize CRASHED status for unfinished methods using `TestClasses.g.txt`
   - Relaunch with `--exclude-classes`
   - Max 5 retries
3. Add JSONL aggregation:
   - Each run's JSONL copied to `/tmp/runtime-tests-run-{N}.jsonl`
   - After final run: aggregate all host-side JSONL files
   - Dedup by `ClassName.MethodName`, last terminal result wins
4. Update `ReportRuntimeTestResult` to use aggregated JSONL data:
   - Report total/pass/fail/skip/crash counts
   - List crashed classes explicitly

**Validation gate:** `nuke runtime-tests-simulator` — normal (no-crash) run works identically. Crash recovery is harder to test automatically — manually verify by temporarily adding a crash to a test class and confirming the orchestrator resumes.

---

### Session 4: Unified Test Project (Improvement 5)

**Prior context:** Sessions 1-3 eliminated reflection, added JSONL results, and added crash recovery.

**Deliverables:**
1. Add conditional properties to `RuntimeTestsApp.csproj` for device builds:
   - `<PublishAot>true</PublishAot>` conditioned on `RuntimeIdentifier == ios-arm64`
   - Linker/trimmer settings conditioned by RID
   - Native reference paths conditioned by RID
2. Merge `RuntimeTestsApp.Device/Program.cs` into `RuntimeTestsApp/Program.cs`:
   - Platform-specific entry point logic via `#if` or runtime RID check
   - Ensure `NSRunLoop` yield works on both paths
3. Update `Build.RuntimeTests.cs`:
   - `RuntimeTestsDevice` target builds `RuntimeTestsApp` with `ios-arm64` RID instead of separate project
4. Delete `RuntimeTestsApp.Device/` directory:
   - `RuntimeTestsApp.Device.csproj`
   - `Program.cs`
   - `TrimmerRoots.xml`
5. Verify all test files are now compiled for device (the 120 previously missing tests)

**Validation gate:** `nuke runtime-tests-simulator` AND `nuke runtime-tests-device` — both pass with unified project. Device test count should increase by ~120 (the previously missing tests). Some of these may need `[SkipOnDevice]` if they hit NativeAOT limitations.

---

## Files to Create

| File | Purpose |
|---|---|
| `src/SwiftBindings.TestDiscovery/` | Source generator project |
| `src/SwiftBindings.TestDiscovery/TestDiscoveryGenerator.cs` | Incremental source generator |
| `src/SwiftBindings.TestDiscovery/TestDescriptors.cs` | `TestClassDescriptor`, `TestMethodDescriptor` records |

## Files to Modify

| File | Session | Change |
|---|---|---|
| `BindingTests/RuntimeTestsApp/Infrastructure/TestBase.cs` | 1 | Remove reflection, use `TestMethodDescriptor.Invoker` delegates |
| `BindingTests/RuntimeTestsApp/Infrastructure/TestResults.cs` | 2 | Add JSONL append-write after each result |
| `BindingTests/RuntimeTestsApp/Program.cs` | 1, 2, 3, 4 | Session 1: use `TestRegistry`; Session 2: JSONL + markers; Session 3: `--exclude-classes`; Session 4: merge device entry point |
| `BindingTests/RuntimeTestsApp/RuntimeTestsApp.csproj` | 1, 4 | Session 1: reference source gen; Session 4: device conditional properties |
| `build/Build.RuntimeTests.cs` | 2, 3, 4 | Session 2: JSONL parsing + baseline; Session 3: retry loop; Session 4: unified project build |
| `build/Tools/SimCtl.cs` | 2 | Wait for `RESULTS FLUSHED`, sandbox file retrieval |
| `build/Tools/DeviceCtl.cs` | 2 | Same as SimCtl |
| `.validation-baseline.json` | 2 | Add `runtime_tests` section |

## Files to Delete (Session 4)

| File | Reason |
|---|---|
| `BindingTests/RuntimeTestsApp.Device/RuntimeTestsApp.Device.csproj` | Merged into RuntimeTestsApp |
| `BindingTests/RuntimeTestsApp.Device/Program.cs` | Merged into RuntimeTestsApp |
| `BindingTests/RuntimeTestsApp.Device/TrimmerRoots.xml` | Source generator eliminates need |

## Decision Log

| Decision | Rationale |
|---|---|
| No xUnit/TUnit/MSTest migration | No proven iOS runner exists (March 2026). Source gen is lower risk. |
| No per-class app relaunches | 90s overhead on every run is unacceptable. Resume-on-crash gives isolation with ~0s overhead on healthy runs. |
| No signal handler crash recovery | Process state after SIGSEGV is undefined. `siglongjmp` is unreliable. Resume-on-crash is deterministic. |
| No XHarness | Internal TestRunner libs aren't published NuGet. Overhead of integration outweighs benefit. |
| JSONL over JSON | Crash-safe by design. Truncated last line doesn't invalidate previous lines. |
| Source gen over MTP custom ITestFramework | MTP ITestFramework is complex to implement for iOS. Source gen is simpler and solves the same problem (NativeAOT discovery). |
| Keep console markers as fallback | Backwards-compatible during rollout. Remove after JSONL is proven. |
| Session 1 standalone | Source generator is highest-risk, benefits from human oversight. Sessions 2-4 are mechanical build system work suited for autonomous orchestration. |
