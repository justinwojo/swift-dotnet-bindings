# WorkoutKit — Binding Audit

- **Package**: SwiftBindings.Apple.WorkoutKit v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2; net10.0-macos26.2; net10.0-maccatalyst26.2
- **Native**: Apple WorkoutKit (SDK ios26.2 / macos26.2 / maccatalyst26.2)
- **Audited at**: swift-dotnet-packages main `1e8c27a`, generated 2026-06-27T19:50:39Z

## Verdict

Essentially clean. All 27 types emit (100%); 110 of 171 tracked Swift members are surfaced — the 60-member gap is fully explained by availability-gating (watchOS-only and platform-variant members excluded from the iOS-targeted build), not real gaps, and only 1 member is explicitly skipped (a throwing static property on an enum extension). The full build-and-schedule-a-workout flow is usable end-to-end: `CustomWorkout` has a rich multi-overload constructor, `WorkoutPlan.WorkoutType.Custom()` wraps it, `WorkoutScheduler.ScheduleAsync` is a proper `Task` with `CancellationToken`, and the async auth flow (`RequestAuthorizationAsync`, `GetAuthorizationStateAsync`) is complete. Tests are adequate for a healthy-library gate but leave the most important end-to-end path (CustomWorkout construction + WorkoutPlan creation + async schedule) untested at the BindingTests level.

## 1. Coverage

### Member count reconciliation

| Field | Count |
|---|---|
| TotalMembers (Swift public API) | 171 |
| EmittedMembers (surfaced in C#) | 110 |
| SkippedMembers (explicit drops) | 1 |
| Gap (171 − 110 − 1) | **60** |
| SynthesizedMembers (generator-added) | 152 |

**Gap explanation**: The 60-member gap is not missing coverage — it is availability-excluded members that the generator counts in `TotalMembers` but does not emit or explicitly log as skipped. WorkoutKit is a watchOS-first API; a substantial subset of its symbol graph is watchOS-only or carries divergent availability that the iOS-targeted pass discards silently. The `SynthesizedMembers` (152) are generator additions: factory constructors, `IDisposable`, `IEquatable<T>`, equality operators, existential boxing helpers, and async callback infrastructure — they are not in the Swift symbol count.

Effective C# binding surface: 110 native + 152 synthesized = **262 public members** across 27 types.

### Skipped items

| Reason | Count | Correctly excluded? |
|---|---|---|
| `UnsupportedSignature` (SWIFTBIND107: throwing property getter dropped) | 1 | **(a) Generator limitation — minor gap** |

**The single skip — `WorkoutAlertMetric.countPerMinute` (Property)**  
Swift: `public static var countPerMinute: HKQuantityType { get throws }` — a throwing static extension property on the `WorkoutAlertMetric` enum. The `@_cdecl` property wrapper cannot emit try/catch for throwing accessors (SWIFTBIND107), and the generator additionally flags the return type as unsupported for simple enum extensions. The missing value is an `HKQuantityTypeIdentifier`-backed `HKQuantityType` (used to connect alert thresholds to HealthKit queries). A developer can substitute `HKObjectType.quantityType(forIdentifier: .heartRate)!` directly from the HealthKit binding. **Not worth a dedicated generator fix** — the SWIFTBIND107 throwing-property gap is the cross-cutting blocker.

### Generator unlocks

No high-value unlocks identified. The only gap (throwing property getter on enum extension) already has a workaround and is a known cross-cutting limitation.

## 2. C# Quality

**Naming/shape**: Clean throughout. PascalCase everywhere; no leaked Swift mangling. Enum types are either plain C# `enum` (`WorkoutAlertMetric`, `StateError`, `IntervalStep.PurposeType`) or discriminated-union classes (`WorkoutGoal`, `WorkoutPlan.WorkoutType`, `SwimBikeRunWorkout.Activity`) with `CaseTag` discriminators and per-case factory methods. The nested-type approach for `WorkoutPlan.WorkoutType` and `SwimBikeRunWorkout.Activity` is appropriate for Swift enums with associated values.

**Async**: All seven `WorkoutScheduler` async methods surface correctly as C# `Task`/`Task<T>` with optional `CancellationToken`:
- `WorkoutKit.cs:2289` — `Task<IReadOnlyList<ScheduledWorkoutPlan>> GetScheduledWorkoutsAsync(CancellationToken)`
- `WorkoutKit.cs:2450` — `Task<AuthorizationStateType> GetAuthorizationStateAsync(CancellationToken)`
- `WorkoutKit.cs:2771` — `Task ScheduleAsync(WorkoutPlan, DateComponents, CancellationToken)`
- `WorkoutKit.cs:2949` — `Task RemoveAsync(WorkoutPlan, DateComponents, CancellationToken)`
- `WorkoutKit.cs:3127` — `Task MarkCompleteAsync(WorkoutPlan, DateComponents, CancellationToken)`
- `WorkoutKit.cs:3305` — `Task RemoveAllWorkoutsAsync(CancellationToken)`
- `WorkoutKit.cs:3467` — `Task<AuthorizationStateType> RequestAuthorizationAsync(CancellationToken)`

No blocking-only fallbacks. The async plumbing (callback infrastructure, error path, cancellation relay) is complete.

**Nullability**: Correct across all examined paths.
- `WorkoutKit.cs:1399` — `CustomWorkout.DisplayName` → `string?`
- `WorkoutKit.cs:1483` — `CustomWorkout.Warmup` → `WorkoutStep?`
- `WorkoutKit.cs:1652` — `CustomWorkout.Cooldown` → `WorkoutStep?`
- `WorkoutKit.cs:1116` — `IntervalStep(purpose, goal, alert?)` — alert parameter is correctly `IWorkoutAlert?`

**Lifetime**: All struct wrappers implement `IDisposable` with XML-doc warnings. `WorkoutGoal` cached-singleton path skips disposal correctly (`WorkoutKit.cs:4811`). Async result marshalling disposes carrier+1 in `finally` before returning the projected collection (`WorkoutKit.cs:2192`).

**Ergonomics**:
- `WorkoutKit.cs:8419` — `IntervalBlock.Steps` typed as `IReadOnlyList<IntervalStep>` with setter accepting `IEnumerable<IntervalBlock>` — idiomatic .NET collection bridging.
- `WorkoutKit.cs:1916` — `CustomWorkout` full ctor: `(HKWorkoutActivityType, HKWorkoutSessionLocationType, string?, WorkoutStep?, IEnumerable<IntervalBlock>, WorkoutStep? = null)` — the optional-defaulted cooldown matches Swift's default arg, and four shorter overloads cover common construction patterns.
- `WorkoutKit.cs:4819` — `WorkoutGoal.Distance(double, NSUnitLength)` factory and siblings (`Time`, `Energy`) are clear for associated-value cases; `WorkoutGoal.Open` is a pre-materialized singleton (`WorkoutKit.cs:4812`).
- `WorkoutKit.cs:10638` — `WorkoutPlan.WorkoutType.Custom(CustomWorkout)` wraps the discriminated union cleanly for the scheduling flow.

**One ergonomic note (non-blocking)**: `WorkoutKit.cs:4762` — `IWorkoutAlert.Supports(activity, location)` throws `NotSupportedException("This method uses a Swift protocol extension default. Call it on the concrete type instead.")`. This is the deliberate protocol-extension-default behavior. Consumers who pattern-match to a concrete alert type (the expected usage) are unaffected; if they hold `IWorkoutAlert` and call `Supports`, the error message is clear.

**`WorkoutScheduler` is a class, not a struct** (`WorkoutKit.cs:2088`) — correct since it's a reference type in Swift. The `Shared` static property returns a `WorkoutScheduler` instance without `IDisposable` on the static reference itself, which is correct for a singleton.

## 3. Test Coverage

**29 test cases** in `tests/Tests.cs`. The harness uses a simple `Pass`/`Fail` pattern executed on simulator (Mono JIT) and device (NativeAOT).

| Category | Cases | Depth |
|---|---|---|
| Type metadata loads | 11 | Weak — proves symbol resolution, not ABI |
| Enum value checks (plain + discriminated-union CaseTags) | 7 | Moderate — proves discriminant layout |
| Singleton / static accessors | 4 | Moderate — proves P/Invoke round-trip for statics |
| Alert range construction (`SwiftClosedRange<Measurement<T>>`) | 4 | **Strong** — exercises full cross-type construction |
| `IntervalStep` constructor + property round-trip | 1 | Moderate |
| `IntervalBlock` default constructor | 1 | Weak |
| `WorkoutStep` default constructor | 1 | Weak |

### Untested surface

| API | Why it matters |
|---|---|
| `CustomWorkout(activity, location, displayName, warmup, blocks, cooldown)` | The primary output type — construction path unproven at runtime |
| `WorkoutPlan.WorkoutType.Custom(workout)` + `WorkoutPlan(type, id)` | Cannot schedule without a `WorkoutPlan`; these ctors are untested |
| `WorkoutGoal.Distance/Time/Energy` factory methods | Associated-value enum construction unproven |
| `IntervalStep(purpose, goal, alert)` | The alert-carrying overload exercises `IWorkoutAlert?` existential marshalling — untested |
| `WorkoutScheduler.RequestAuthorizationAsync` | Auth gate for scheduling; async path completely untested |
| `WorkoutScheduler.GetScheduledWorkoutsAsync` | Async collection result path untested |
| `ScheduledWorkoutPlan` properties (`Plan`, `Date`) | The result type of `GetScheduledWorkoutsAsync` has zero property coverage |

### Recommended additions (BindingTests layer)

1. **CustomWorkout round-trip** — construct `IntervalStep(Work, WorkoutGoal.Open)` → `IntervalBlock` with steps → `CustomWorkout(activity, location, displayName, warmup, [block], cooldown)` → assert `Activity`, `DisplayName`, `Blocks.Count`, `Warmup != null`. Proves the full warmup/blocks/cooldown construction path in a single test.

2. **WorkoutPlan construction** — `WorkoutPlan.WorkoutType.Custom(workout)` → `new WorkoutPlan(type, Guid.NewGuid())` → assert `Plan.Id != Guid.Empty` and `Plan.Workout.Tag == CaseTag.Custom`. The scheduling flow is gated on this working.

3. **WorkoutGoal associated-value factories** — `WorkoutGoal.Distance(5.0, NSUnitLength.Kilometers)` → assert `Tag == CaseTag.Distance`. One case; proves the buffer-allocation path for value-carrying cases.

4. **IntervalStep with alert** — `new IntervalStep(Work, WorkoutGoal.Open, new HeartRateRangeAlert(range))` → assert `Purpose == Work`, no crash. Exercises the `IWorkoutAlert?` optional existential marshalling path.

5. **Async auth state** — `await WorkoutScheduler.Shared.GetAuthorizationStateAsync()` → assert non-throwing (value will be `NotDetermined` in CI, which is fine). Proves the async callback dispatch path for this type.

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `WorkoutAlertMetric.countPerMinute` skipped (SWIFTBIND107 — throwing static property on enum extension) | No action: workaround available via HealthKit binding directly; fix requires lifting the throwing-property-on-cdecl-wrapper limitation cross-cuttingly | Low | Low |
| 2 | Tests | `CustomWorkout` full construction path completely untested | Add BindingTests round-trip: `IntervalBlock` + `IntervalStep` → `CustomWorkout` → assert `Blocks.Count` / `Warmup` / `DisplayName` | Low | **High** |
| 3 | Tests | `WorkoutPlan` construction untested (prerequisite for scheduling) | Add `WorkoutPlan.WorkoutType.Custom(workout)` + `new WorkoutPlan(type, id)` → assert `Id` and `Tag` | Low | **High** |
| 4 | Tests | Async paths completely uncovered | Add `GetAuthorizationStateAsync` smoke (non-throwing assert) | Low | High |
| 5 | Tests | `WorkoutGoal` associated-value factories untested | Add `WorkoutGoal.Distance(5.0, NSUnitLength.Kilometers)` → assert `Tag == Distance` | Low | Medium |
| 6 | Tests | `IntervalStep(purpose, goal, alert)` — `IWorkoutAlert?` existential marshalling unproven | Add constructor call with `HeartRateRangeAlert`; assert `Purpose` round-trips | Low | Medium |
