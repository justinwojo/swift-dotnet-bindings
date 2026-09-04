---
paths:
  - "BindingTests/**"
---

# BindingTests Guide

## Nuke Targets
| Target | Purpose |
|--------|---------|
| `nuke build-xcframework` | Build Swift test library as xcframework |
| `nuke regenerate-bindings` | Generate C# bindings from xcframework |
| `nuke binding-tests` | Default: compile + run iOS Simulator (Mono JIT) |
| `nuke binding-tests --compile-only` | Compile gate only — no app build, no tests |
| `nuke binding-tests --strict` | Fail on non-zero generator exit (compose with any mode) |
| `nuke binding-tests --device` | Compile + run on physical iPhone (NativeAOT) |
| `nuke binding-tests --device --mono-aot` | Compile + run on physical iPhone under **Mono full-AOT** — the .NET-for-iOS default device runtime (what a MAUI app ships). Opt-in; requires `--device` |
| `nuke binding-tests --macos` | Compile + run on macOS |
| `nuke binding-tests --catalyst` | Compile + run on Mac Catalyst |
| `nuke binding-tests --tvos` | Compile + run on tvOS Simulator |

Platform flags compose (`--sim --device` runs both). Inner-loop shortcuts: `--skip-regen` (~17s) skips bindings regen; `--skip-build` (~5s) skips app build; `--class-filter NAME` runs one test class.

## Output Files
- `output/SwiftBindingsTestLib.cs` — Generated C# bindings
- `output/SwiftBindingsTestLib.SwiftUIBridge.swift` — Generated SwiftUI bridge
- `output/binding-report.json` — Binding completeness report
- `output/binding-emission-report.json` — Emission-time diagnostics and rejection detail
- `output/binding-artifact-manifest.json` — Artifact manifest used to rederive reports

## Coverage Report
`output/coverage-matrix.json` is NOT produced by normal `nuke binding-tests`; it is emitted only when `build/scripts/coverage-report.py` is run manually with ABI JSON + `binding-report.json`.

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
- Tests extend `TestBase`; discovery is descriptor-based (`TestDiscoveryGenerator`), not runtime reflection enumeration. Test attributes:
  - **Default** (no attribute) — runs on both simulator and device
  - **`[Skip("reason")]`** — always skipped (generator bugs, missing entry points)
  - **`[SkipOnSimulator("reason")]`** — skipped on simulator/CLI simulator mode, runs on device
  - **`[SkipOnDevice("reason")]`** — skipped on the NativeAOT device lane only (`--platform device`); it describes the Release/NativeAOT app, so it does NOT apply to the Mono full-AOT device lane (`--device --mono-aot`), which runs the test
  - **`[SkipOnMonoJit("reason")]`** — method-level only; skipped wherever the process runs on Mono
  - **`[SkipOnCatalystX64("reason")]`** — method-level only; skipped on Mac Catalyst x64
  - **`[Slow]`** — stress tests, always runs
  - **`[MonoJitCrash]`** — DEPRECATED, do not use. Diagnose the root cause; use a targeted `[SkipOn*]` only for confirmed platform/runtime limitations, otherwise fix the generator/runtime bug or use `[Skip]` with a specific bug reason.
- Properties return `SwiftString` (call `.ToString()`); methods return `string` directly
- `--class-filter NAME` runs only the named test class (exact match, case-insensitive)
- `--platform simulator|device|device-monoaot` selects execution mode (default: simulator). `device` is the NativeAOT device lane, `device-monoaot` the Mono full-AOT one; the value is what decides which CLI-flag-keyed skips apply, and each lane is graded against its own baseline entry
- iOS args: use `NSProcessInfo.ProcessInfo.Arguments` (not `Main(string[] args)`)
- Main-queue: use `NSRunLoop.Current.RunUntil(NSDate)` instead of `Thread.Sleep()`
- RuntimeTestsApp needs `IncludeSwiftBindingsRuntimeNative=false` in csproj
- SafeHandle dispose: call `obj.Payload.Dispose()`, then access throws `ObjectDisposedException`
- Enum raw values come from Swift source (e.g. LogLevel: `"[DEBUG]"`, not `"debug"`)
- `EventHandler` name collides with `System.EventHandler` — use `using SwiftEventHandler = SwiftBindingsTestLib.EventHandler`

## Active Mono/Runtime Limitations (affects test classification)
- **NEVER use `[MonoJitCrash]`** — if a test crashes under Mono, diagnose the root cause first. Most historical "Mono crashes" were generator/runtime bugs. If the crash matches a confirmed upstream limitation, use the narrowest current attribute (`[SkipOnMonoJit]`, `[SkipOnCatalystX64]`, `[SkipOnSimulator]`, or `[SkipOnDevice]`) with a specific reason; otherwise fix it or use `[Skip("specific bug description")]` for a known project bug.
- **ALL runtime crashes are guilty-until-proven-innocent**: The same skepticism applies to ALL crash classifications, not just `[MonoJitCrash]`. Before labeling anything "upstream Mono" or "upstream NativeAOT", verify the generated C# P/Invoke matches the Swift @_cdecl wrapper exactly: calling convention (`CallConvCdecl` vs `CallConvSwift`), parameter count, parameter types, library name, entry point. Most "upstream" crashes turn out to be wrapper connection bugs.
- **Confirmed upstream issues** are documented in `src/docs/Future/upstream-issues-README.md` (filing guide) with one file per filed issue at `src/docs/Future/upstream-issue-*.md`. Current filed issues: Issue 1 (Mono JIT async assertion), Issue 2 (non-blittable CallConvSwift rejection), Issue 3 (Mono `Set.insert` DONE_BLOCKING), and Issue 4 (Mono Catalyst x64 instability). The `SwiftSelf<SafeHandle>` async-lifetime item is tracked as a non-standalone note. Everything else has been our bug. The authoritative confirmed-issues list lives in memory at `feedback_mono_jit_blame.md`; consult it before classifying any crash as upstream.
- Test attributes: no attribute (runs everywhere), `[Skip("reason")]` (always skipped), `[SkipOnSimulator("reason")]` (skipped on simulator mode, runs on device), `[SkipOnDevice("reason")]` (skipped on the NativeAOT device lane only — the Mono full-AOT device lane runs it), `[SkipOnMonoJit("reason")]` (method-level, runtime-detected: fires wherever the process is Mono — simulator, Catalyst, **and** the Mono full-AOT device lane), `[SkipOnCatalystX64("reason")]` (method-level Mac Catalyst x64 skip), `[Slow]` (stress tests)
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
5. **Run the validation gate the change warrants** — `nuke test` + `nuke binding-tests` are the everyday generator/runtime signals. `nuke validate` is opt-in for larger or cross-cutting generator/emitter changes, pre-release sweeps, or when a real-world library canary is needed.
6. **Don't iterate more than 3 times on the same approach** — if 3 attempts at the same strategy don't work, step back and question the hypothesis. The root cause is probably elsewhere.

## Emission Pipeline Dual-Path Hazard
`EmitBoundGenericArguments()` and `EmitTypeConversions()` (which calls `TryEmitParameterConversionViaProjection()`) BOTH run for the same parameters. They create variables with the `{name}Buffer` naming convention. If a new fast path in one creates `{name}Buffer`, verify the other path doesn't ALSO create it. Run `nuke validate` with a library that has Optional<CGFloat> params as a canary — it exercises both paths.

## New Reverse-Dispatch Test → no allowlist step (Session 7b)
When adding a BindingTests test where Swift dispatches **back into** a C# conformer through a protocol's witness table / EveryProtocol vtable (reverse dispatch — receiver getters/setters, vtable round-trips), **there is nothing to allowlist.** Session 7b deleted the harness's bespoke `SwiftSourceStripper` (and its hand-maintained `PreservedProtocols` set). The harness now scrubs the generated wrapper with the generator's OWN `SwiftWrapperPostProcessor.Process` (`src/Swift.Bindings/src/Configuration/`, link-compiled into `build/`), the same oracle the generator uses for its own wrapper — so every valid EveryProtocol conformance and its witness-table getter survive by construction (only genuinely-uncompilable blocks are stripped — e.g. an `EveryProtocol()` placeholder block for an un-emittable proxy conformance, or a Swift-*unavailable* ObjC type reference like `NSInvocation`). Internal-receiver shapes — a public member on a `@usableFromInline internal` parent — are now ALL gated at emission, not stripped: the *sync* case falls back to CallConvSwift (`WrapperValidation` arm 2b), and the *async/closure/operator* cases are DROPPED at emission (`MemberValidationPipeline` gate 3c / `OperatorHandler` parent-internal guard → `SkipReason.ParentModuleInternalNoFallback`), so they never emit a wrapper to strip. Gate 3c scans the whole `CSSignature`, so a closure RETURN is dropped as well as a closure parameter (a closure return through a direct CallConvSwift P/Invoke crashes Mono+NativeAOT); the operator guard drops every operator on an internal parent by design (frozen-struct = parent-naming wrapper with no fallback; class / non-frozen-struct = unreachable dead surface). The `wrapper-strip` tripwire (`baselines.json` → `wrapper_stripped_count`) fails on any INCREASE in stripped blocks, and the getter-parity gate asserts the harness wrapper exports the IDENTICAL witness-getter set as the generator-own wrapper, so an accidental over-strip is caught at the compile gate rather than as a runtime `EntryPointNotFoundException`. Caveat unchanged: `--skip-regen` reuses the already-built wrapper, so a freshly-added fixture won't be picked up — run a full `nuke binding-tests` (regen) when validating the new entry.

## Runtime Test Pre-Flight (saves 20+ min)
Before writing runtime tests for a new batch:
1. Read generated C# bindings for the types — identify non-blittable params, missing entry points
2. Pre-assign attributes: default (expected to run everywhere), targeted `[SkipOn*]` only for confirmed platform/runtime limitations, `[Skip]` for missing entry points or known generator bugs
3. Copy `using` directives from existing test file
4. Fix ALL failures at once — analyze full failure list first
5. Never run slow test scripts multiple times — capture once, grep saved output

## Unit Test Gotchas
- `[Collection("ReportCollector")]` on ReportCollectorTests, SwiftUIBridgeEmitterTests, ExistentialBypassEmitterTests prevents xUnit parallel execution of shared static state
- Tests using `Swift.Int` without TypeDB registration get `Swift.AnyType` — call `RegisterSwiftInt32()` for `System.Int32`
