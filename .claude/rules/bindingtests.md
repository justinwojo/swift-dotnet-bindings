---
paths:
  - "BindingTests/**"
---

# BindingTests Guide

## Scripts
| Script | Purpose |
|--------|---------|
| `build-xcframework.sh` | Build Swift test library as xcframework |
| `regenerate-bindings.sh` | Generate C# bindings from xcframework |
| `build-and-test.sh` | Full pipeline: xcframework + bindings + bridge |
| `build-bridge.sh` | Compile generated SwiftUI bridge + test helpers |
| `run-runtime-tests.sh` | Build + run runtime tests on iOS Simulator |
| `generate-coverage-report.sh` | Generate `coverage-matrix.json` |

## Output Files
- `output/SwiftBindingsTestLib.cs` — Generated C# bindings
- `output/SwiftBindingsTestLib.SwiftUIBridge.swift` — Generated SwiftUI bridge
- `output/binding-report.json` — Binding completeness report
- `output/coverage-matrix.json` — Feature coverage matrix

## Coverage Report
Feature statuses: **passing** (goal), **degraded** (some members skipped), **missing** (no test), **compiled_out** (guarded by #if)

Skip reasons and fix areas:
- `UnsupportedSignature` → Marshaler handlers, TypeDatabase
- `AnyTypeFallback` → TypeDatabaseExtensions.cs, type XML files
- `UnsupportedExistential` → Marshaler existential handling
- `AsyncProperty` → PropertyHandler.cs
- `UnsupportedClosure` → ClosureHandler.cs
- `GenericProtocolConstraint` / `UnsatisfiedGenericConstraint` → Generic handling in Marshaler
- `DuplicateSignature` → Emitter deduplication
- `SwiftUIConstraint` / `CombineFramework` → By design (skipped)

Investigate degraded features: check `binding-report.json`, search generated bindings for `[UnsupportedSwiftType]`, trace skip reason to handler.

## Runtime Test Patterns
- Tests in `RuntimeTestsApp/` — iOS simulator app, discovery-based runner
- Tests extend `TestBase`, auto-discovered via reflection. Test attributes:
  - **Default** (no attribute) — runs on both simulator and device
  - **`[Skip("reason")]`** — always skipped (generator bugs, missing entry points)
  - **`[Slow]`** — stress tests, always runs
  - **`[MonoJitCrash]`** — DEPRECATED, do not use. All Mono crashes are our bugs. Use `[Skip]` with a specific reason instead.
- Properties return `SwiftString` (call `.ToString()`); methods return `string` directly
- `--class NAME` runs only the named test class (exact match, case-insensitive)
- `--platform simulator|device` selects execution mode (default: simulator)
- iOS args: use `NSProcessInfo.ProcessInfo.Arguments` (not `Main(string[] args)`)
- Main-queue: use `NSRunLoop.Current.RunUntil(NSDate)` instead of `Thread.Sleep()`
- RuntimeTestsApp needs `IncludeSwiftBindingsRuntimeNative=false` in csproj
- SafeHandle dispose: call `obj.Payload.Dispose()`, then access throws `ObjectDisposedException`
- Enum raw values come from Swift source (e.g. LogLevel: `"[DEBUG]"`, not `"debug"`)
- `EventHandler` name collides with `System.EventHandler` — use `using SwiftEventHandler = SwiftBindingsTestLib.EventHandler`

## Active Mono/Runtime Limitations (affects test classification)
- **NEVER use `[MonoJitCrash]`** — all Mono JIT crashes were traced to our own bugs (see src/docs/Completed/MONO-JIT-FINDINGS.md). If a test crashes on simulator, diagnose the root cause in our generator/runtime code and either fix it or use `[Skip("specific bug description")]`.
- **ALL runtime crashes are guilty-until-proven-innocent**: The same skepticism applies to ALL crash classifications, not just `[MonoJitCrash]`. Before labeling anything "upstream Mono" or "upstream NativeAOT", verify the generated C# P/Invoke matches the Swift @_cdecl wrapper exactly: calling convention (`CallConvCdecl` vs `CallConvSwift`), parameter count, parameter types, library name, entry point. Most "upstream" crashes turn out to be wrapper connection bugs.
- **Confirmed upstream issues** are documented in `src/docs/Future/upstream-bug-reports-draft.md` (single source of truth). Only 5 confirmed: Mono JIT async assertion (Issue 1), Mono non-blittable rejection (Issue 2), Mono SafeHandle async lifetime (Issue 3), NativeAOT float struct GPR/FPR mismatch for params (Issue 5) and returns (Issue 6). Everything else so far has been our bug. Consult this doc before classifying any crash as upstream.
- Test attributes: no attribute (runs everywhere), `[Skip("reason")]` (always skipped), `[SkipOnSimulator("reason")]` (skipped on Mono simulator, runs on NativeAOT device), `[SkipOnDevice("reason")]` (skipped on NativeAOT device, runs on Mono simulator), `[Slow]` (stress tests)
- Optional array on frozen struct → "Not enough bits" layout mismatch → `[Skip]`
- SafeHandle arg through CallConvSwift → non-blittable error → `[Skip("SafeHandle non-blittable in CallConvSwift")]`
- Class inheritance+protocol → entry points not exported from dylib → `[Skip]`
- GenericPair.swapped() → CS8500 (pointer to managed generic, guarded) — active C# compiler limitation
- TaskPriority (String raw value enum) → `[Skip]`. TaskStatus (Int32 raw value) works fine
- Integration tests have pre-existing nint generic errors (unrelated noise, ignore)

## Debugging Runtime Test Crashes
When a runtime test crashes (SIGSEGV, SIGKILL, Mono JIT assertion):
1. **Add diagnostic logging FIRST** — print the actual values before asserting. Don't iterate on blind fixes.
2. **Check generated code matches the wrapper** — verify C# P/Invoke calling convention, parameter count, and types match the Swift @_cdecl wrapper. Use `grep` on `output/SwiftBindingsTestLib.cs` and `output/SwiftBindingsTestLib.swift`.
3. **Isolate constructor vs getter** — if a property reads wrong after construction, hardcode the Swift wrapper to pass `nil`/a known value and see if the getter still returns the wrong thing. This tells you which side is broken.
4. **Check if the wrapper was stripped** — the build script silently strips Swift functions that fail compilation. After a build, `grep` the output `.swift` file for the @_cdecl symbol AND check `nm -g` on the compiled framework to confirm the symbol exists.
5. **Run `validate-libraries.sh` after any generator change** — emission pipeline changes (especially to OptionalProjection, WrapperEmitter.Marshalling, PInvokeEmitter) can cause regressions on third-party libraries. Catch them early.
6. **Don't iterate more than 3 times on the same approach** — if 3 attempts at the same strategy don't work, step back and question the hypothesis. The root cause is probably elsewhere.

## Emission Pipeline Dual-Path Hazard
`EmitBoundGenericArguments()` and `EmitTypeConversions()` (which calls `TryEmitParameterConversionViaProjection()`) BOTH run for the same parameters. They create variables with the `{name}Buffer` naming convention. If a new fast path in one creates `{name}Buffer`, verify the other path doesn't ALSO create it. Run `validate-libraries.sh --filter AMPopTip` as a canary — it has Optional<CGFloat> params that exercise both paths.

## Runtime Test Pre-Flight (saves 20+ min)
Before writing runtime tests for a new batch:
1. Read generated C# bindings for the types — identify non-blittable params, missing entry points
2. Pre-assign attributes: default (blittable), `[MonoJitCrash]` (CallConvSwift/non-blittable), `[Skip]` (missing entry points)
3. Copy `using` directives from existing test file
4. Fix ALL failures at once — analyze full failure list first
5. Never run slow test scripts multiple times — capture once, grep saved output

## Unit Test Gotchas
- `[Collection("ReportCollector")]` on ReportCollectorTests, SwiftUIBridgeEmitterTests, ExistentialBypassEmitterTests prevents xUnit parallel execution of shared static state
- Tests using `Swift.Int` without TypeDB registration get `Swift.AnyType` — call `RegisterSwiftInt32()` for `System.Int32`
