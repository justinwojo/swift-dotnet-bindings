---
paths:
  - "TestFramework/**"
---

# TestFramework Guide

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
- Tests extend `TestBase`, use `[TestTier(TestTier.TierN)]`, auto-discovered via reflection
- Properties return `SwiftString` (call `.ToString()`); methods return `string` directly
- `--class NAME` runs only the named test class (exact match, case-insensitive)
- iOS args: use `NSProcessInfo.ProcessInfo.Arguments` (not `Main(string[] args)`)
- Main-queue: use `NSRunLoop.Current.RunUntil(NSDate)` instead of `Thread.Sleep()`
- RuntimeTestsApp needs `IncludeSwiftBindingsRuntimeNative=false` in csproj
- SafeHandle dispose: call `obj.Payload.Dispose()`, then access throws `ObjectDisposedException`
- Enum raw values come from Swift source (e.g. LogLevel: `"[DEBUG]"`, not `"debug"`)
- `EventHandler` name collides with `System.EventHandler` — use `using SwiftEventHandler = SwiftBindingsTestLib.EventHandler`

## Active Mono/Runtime Limitations (affects tier assignment)
- See CLAUDE.md "Known Runtime Issues" for Mono JIT assertion details. Closure + SwiftString tests deferred to Tier 3.
- Runtime crash detection: test runner tolerates `jit-info.c:918` AND `RUNTIME TESTS CRASHED` (both Mono bugs); fails on other non-zero exits
- Optional array on frozen struct → "Not enough bits" layout mismatch
- SafeHandle arg through CallConvSwift → non-blittable error
- Class inheritance+protocol → entry points not exported from dylib
- GenericPair.swapped() → CS8500 (pointer to managed generic, guarded) — active C# compiler limitation
- TaskPriority (String raw value enum) → `FromRawValue("high")` routes through wrapper lib, not available at runtime → Tier 3. TaskStatus (Int32 raw value) works fine
- Integration tests have pre-existing nint generic errors (unrelated noise, ignore)

## Runtime Test Pre-Flight (saves 20+ min)
Before writing runtime tests for a new batch:
1. Read generated C# bindings for the types — identify non-blittable params, missing entry points
2. Pre-assign tiers: Tier 1 (blittable), Tier 2 (SwiftString success), Tier 3 (SwiftString+error, missing symbols)
3. Copy `using` directives from existing test file
4. Fix ALL failures at once — analyze full failure list first
5. Never run slow test scripts multiple times — capture once, grep saved output

## Unit Test Gotchas
- `[Collection("ReportCollector")]` on ReportCollectorTests, SwiftUIBridgeEmitterTests, ExistentialBypassEmitterTests prevents xUnit parallel execution of shared static state
- Tests using `Swift.Int` without TypeDB registration get `Swift.AnyType` — call `RegisterSwiftInt32()` for `System.Int32`
