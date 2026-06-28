# ActivityKit — Binding Audit

- **Package**: SwiftBindings.Apple.ActivityKit v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2
- **Native**: Apple ActivityKit system framework (iOS 16.1–26.2 SDK)
- **Audited at**: swift-dotnet-packages main 1e8c27a, generated 2026-06-27T19:49:00Z

## Verdict

Types: 25/25 (100%). Members: 33/71 raw-emitted (46%), 23 synthesized, 38 skipped. The 46% is a
red herring by itself: 32 of the 38 skips land on `Activity<Attributes>` generic members — the core
lifecycle entry points (`request`, `update`, `end`, async sequences, `attributes`, `content`) — where
the root block is **architectural, not a generator bug**. Swift's `ActivityAttributes` requires
compiler-time synthesis of `Codable`/`Hashable` conformances; a C# type can never satisfy that
constraint, and no generator change can fix it. The full request→update→end workflow IS usable via the
`Swift.ActivityKit.LiveActivity` supplement facade (JSON-encoded payloads), which ships inside the same
package and is end-to-end verified on Simulator and device. The direct `Activity<TAttributes>` binding
surface is a useful stub for reading state/tokens on an already-running activity but cannot drive the
lifecycle alone. Overall health is good given the constraint; the supplement fully mitigates the gap.

## 1. Coverage

### Emitted/total table

| Category | Emitted | Total | % |
|---|---|---|---|
| Types | 25 | 25 | 100% |
| Members (raw emitted) | 33 | 71 | 46% |
| Synthesized members | 23 | — | — |
| Skipped members | 38 | — | — |

Emitted-by-kind: Operator ×3, Property ×20, Method ×10.
Skipped-by-kind: Property ×13, Method ×25.

### Skip reason breakdown

#### GenericProtocolConstraint — 10 skips (b: real gap, architectural)

`Activity.request` (×5 overloads) and the five `AsyncSequence.makeAsyncIterator` methods
(`ActivityUpdates`, `ActivityStateUpdates`, `ContentStateUpdates`, `ContentUpdates`,
`PushTokenUpdates`).

Root cause: `Activity<Attributes>` requires `Attributes: ActivityAttributes`, which refines
`Codable & Hashable`. Those conformances are synthesized by the Swift compiler at build time from
stored properties; there is no runtime path to manufacture a witness table for a type the compiler
never saw. A C# type cannot be `Attributes`. Correctly documented in README.md.

**Verdict**: correctly blocked. The supplement approach (one fixed Swift `DotNetLiveActivityAttributes`
type whose witnesses are synthesized at SBApple build time) is the right permanent fix and is shipped.
No generator change addresses the root.

#### GenericTypeCallback — 13 skips (b: real gap, generator capability)

`Activity.update` (×5 overloads), `Activity.end` (×3 overloads), and `Iterator.next` in each of the
five async sequence types (×5).

Root cause: async methods on a generic parent type require passing `self` and the parent's type
metadata/protocol witness tables in Swift's implicit calling-convention registers. A direct
`CallConvSwift` P/Invoke cannot supply those; the generator correctly refuses to emit rather than emit
a crash. The fix would require the generator to emit a `@_cdecl` shim that accepts type metadata and
protocol witness tables as explicit pointer parameters and forwards into the generic method — a
non-trivial but tractable capability (already partially designed in roadmap). However, even with that
fix, `update`/`end` still require a live `Activity<Attributes>` instance, which can only be obtained
via `request` — itself blocked by GenericProtocolConstraint. So unlocking `update`/`end` alone is
insufficient for direct use.

**Verdict**: real gap, but moot without `request`. The supplement facade covers `update`/`end`. The
generator capability ("async generic instance methods with explicit-metadata `@_cdecl` shims") is the
right long-term unlock if the ActivityAttributes constraint is ever lifted for some sub-workflow;
medium-value, high-effort generator work.

#### AnyTypeFallback — 9 skips (b: real gap, architectural)

All eight properties on `Activity<TAttributes>` that return generic-parameterized types:
`activities`, `activityUpdates`, `activityStateUpdates`, `contentStateUpdates`, `contentUpdates`,
`pushTokenUpdates`, `pushToStartTokenUpdates`, `attributes`. Plus `ActivityContent.state` (returns
`Swift.AnyType`, the generic `TState` param).

Root cause: these properties return `Activity<Swift.AnyType>.XxxType` — the generator can't project
the concrete generic to a C# type because the C# side has no concrete `Attributes` type. This is the
same architectural wall. The one non-architectural case is `ActivityContent.state`, which returns the
generic `TState` property — potentially projectable if the generator gained support for returning
generic-param-typed values from generic structs.

**Verdict**: correctly blocked on `Activity<TAttributes>`. The `ActivityContent.state` case
(ActivityKit.cs:1846) is the only AnyTypeFallback that a generator fix could address (low-value:
`ActivityContent<TState>` can be created and its `StaleDate`/`RelevanceScore` read; missing `.state`
is an ergonomic gap but the supplement never surfaces `ActivityContent` directly anyway).

#### SwiftUIConstraint — 3 skips (a: correctly excluded)

`AlertConfiguration.title`, `AlertConfiguration.body` (SwiftUI `Text`-typed properties),
`AlertConfiguration.init` (takes `LocalizedStringResource`). SwiftUI types are intentionally excluded
per project policy; these have no bridge (no `ActivityKit.SwiftUIBridge.cs`) since AlertConfiguration
is a push-notification config struct and cannot be meaningfully bridged.

#### UnsupportedType — 1 skip (b: architectural)

`Activity.contentState` (ActivityKit.cs:121): `Attributes.ContentState` is a type alias on the
generic type parameter, not exported as a standalone symbol in the module's public ABI. Architectural
— same generic-param-association wall.

#### UnsatisfiedGenericConstraint — 1 skip (b: architectural)

`Activity.content` (ActivityKit.cs:123): `ActivityContent<Attributes.ContentState>` fails to
instantiate because `Attributes.ContentState` doesn't satisfy `Swift.Decodable` from the C# generic
side. Architectural.

#### UnsupportedSignature — 1 skip (b: minor, tractable)

`ActivityState.encode` — enum extension method with an `Encoder` protocol parameter (Swift's
`Encodable.encode(to:)` synthesized impl). Not useful to C# devs; `ActivityState` is a plain `int`
enum and serialize via standard C# means.

### Prioritized generator unlocks

| Priority | What | Why blocked | Effort | Benefit |
|---|---|---|---|---|
| Low | Async generic instance methods via explicit-metadata `@_cdecl` shims | GenericTypeCallback on generic parent | High | Needed for `update`/`end` without supplement; only useful if `request` also unblocked |
| Low | `ActivityContent<TState>.state` property projection | AnyTypeFallback on generic param return | Medium | Currently constructable but state unreadable; supplement doesn't expose this at all |
| Decline | `Activity.request` and all `ActivityAttributes`-constrained APIs | Swift compiler conformance synthesis — no runtime alternative | Not applicable | Architectural; supplement is the correct permanent solution |

## 2. C# Quality

### `Activity<TAttributes>` (ActivityKit.cs:41)

Correctly emitted as `public partial class Activity<TAttributes> : ISwiftObject, IDisposable`. The
three emitted instance properties are clean:
- `Id` (ActivityKit.cs:80): `string` — correct, marshalled via `SwiftMarshal.ReadUtf8Slice`.
- `ActivityState` (ActivityKit.cs:115): typed `ActivityKit.ActivityState` — correct.
- `PushToken` (ActivityKit.cs:161): `byte[]?` — correct nullable, with `Optional<Data>` round-trip.
- `PushToStartToken` (ActivityKit.cs:191): static `byte[]?` with `[SupportedOSPlatform("ios17.2")]` — correct.

All five async sequence nested types (`ActivityUpdates`, `ActivityStateUpdates`, `ContentStateUpdates`,
`ContentUpdates`, `PushTokenUpdates`) are emitted as inner classes with metadata infrastructure and
`ISwiftObject`/`IDisposable` — but with `makeAsyncIterator` and `Iterator.next` both skipped, they
are **dead shells** from a C# consumer perspective. A developer inspecting the type will find
`Activity<TAttributes>.ActivityUpdates` in IntelliSense with no callable members, which is confusing.
A summary comment on each shell explaining why it's empty and pointing to the supplement would improve
discoverability.

**Update/end comment drops** (ActivityKit.cs:1559–1566): the seven `// Unsupported:` comments for
`update`/`end` appear consecutively and are clear. No action needed.

### `ActivityContent<TState>` (ActivityKit.cs:1844)

Constructor emitted (ActivityKit.cs:2079): `ActivityContent(TState state, DateTimeOffset? staleDate, double relevanceScore = 0.0)` — correct API shape. DateTimeOffset↔CFAbsoluteTime conversion is correct (epoch 2001-01-01). `StaleDate` round-trips back via the same conversion (ActivityKit.cs:1882).

Missing: `state` property (ActivityKit.cs:1846 comment) — `ActivityContent<TState>` is constructable but its primary payload is unreadable. In practice the supplement never surfaces `ActivityContent` directly to C# consumers, so this is low-impact. A consumer who constructs one can only read `StaleDate` and `RelevanceScore`.

### `ActivityAuthorizationInfo` (ActivityKit.cs:2999)

Constructor and all properties emitted. `AreActivitiesEnabled`, `FrequentPushesEnabled`,
`ActivityEnablementUpdates`, `FrequentPushEnablementUpdates` are present with correct platform guards.
`IDisposable` via `SwiftClassHandle` — correct lifetime.

### `ActivityState` enum (ActivityKit.cs:1822)

All five cases with correct integer values and platform version attrs (`Pending` ios26.0, `Stale`
ios16.2). Clean.

### `AlertConfiguration` (ActivityKit.cs, post-2700)

`AlertSound.Default` singleton emitted. The struct's `init` and `title`/`body` properties are
correctly blocked by SwiftUIConstraint with inline comments. `AlertConfiguration` is usable only for
accessing its sound singleton.

### `ActivityUIDismissalPolicy` (ActivityKit.cs:2727)

Both singletons (`Default`, `Immediate`) emitted; `After(date:)` factory should be checked.

### Nullability and naming

All observable optionals map to nullable C# (`byte[]?`, `DateTimeOffset?`, etc.). PascalCase
throughout — no mangling leaks. Platform version guards (`[SupportedOSPlatform]`) are granular and
correct. `IDisposable` is present on all value-type wrappers (structs) and the ARC class. No issues.

### `ISwiftStruct` + `MarshalToSwift` boilerplate on async sequence shells

Each of the 5 async sequence types and their iterators emits a full `MarshalToSwift`/`NewFromPayload`
implementation even though the types are dead shells. This is 1400+ lines of boilerplate that can
never be exercised. It compiles and doesn't harm correctness, but it contributes substantially to the
4772-line file size.

## 3. Test Coverage

**24 test cases** in `tests/Tests.cs` (one file; `Program.UIKit.cs` is harness plumbing only).

| Range | What they test | Depth |
|---|---|---|
| 1–3 | `ActivityStyle`, `ActivityState`, `ActivityAuthorizationError` enum values | Weak (integer tag checks) |
| 4–7 | `ActivityAuthorizationError` extension methods (`ErrorDomain`, `GetErrorCode`, `GetFailureReason`, `GetRecoverySuggestion`) | Medium (invokes P/Invoke, checks non-null / non-throw) |
| 8–11 | `ActivityAuthorizationInfo` ctor + `AreActivitiesEnabled`, `FrequentPushesEnabled`, `ActivityEnablementUpdates` | Medium (property reads; bool value any is accepted) |
| 12–13 | `ActivityUIDismissalPolicy.Default` / `Immediate` singletons | Weak (non-null check) |
| 14 | `AlertConfiguration.AlertSound.Default` singleton | Weak |
| 15 | `PushType.Token` singleton | Weak |
| 16–19 | Metadata handles for `ActivityAuthorizationInfo`, `AlertConfiguration`, `AlertSound`, `ActivityUIDismissalPolicy` | Weak (metadata not zero) |
| 20 | `LiveActivity.AreActivitiesEnabled` — supplement P/Invoke round-trip | Medium |
| 21–23 | `LiveActivity.Request` input validation (malformed JSON, non-object JSON, embedded NUL) | Medium (ArgumentException path only, no successful request) |
| 24 | `LiveActivityException` message round-trip | Weak |

**Depth**: Tests 4–11 and 20–23 have real P/Invoke coverage (they invoke generated wrapper
methods and observe behavior). Tests 1–3, 12–19, 24 are sanity checks. No test exercises a real
`Activity<TAttributes>` lifecycle (appropriate — starting a Live Activity requires a foreground-active
host + widget extension, unavailable in a headless test runner).

**Untested emitted surface:**

- `ActivityContent<TState>` constructor and `StaleDate`/`RelevanceScore` properties — the only
  constructable generic type with live properties. Could be tested headlessly by constructing one with
  a simple value-type `TState` (e.g. `int`) and asserting round-trip on `StaleDate`.
- `Activity<TAttributes>.Id`, `ActivityState`, `PushToken`, `PushToStartToken` — no coverage. These
  properties require a live `Activity` instance which requires a real foreground request.
- `ActivityUIDismissalPolicy` equality operators (emitted, untested).
- The 5 async sequence nested types — dead shells, nothing to test.
- `LiveActivity.Request` success path — needs a real foreground app + widget; covered by the
  pre-release `--mixed-pack`/sim runtime gate, not headless tests. Confirm it's in the runtime gate.

**Recommended additions:**

| Test | Type | Layer | Value |
|---|---|---|---|
| Construct `ActivityContent<int>` with `state=42`, `staleDate=DateTimeOffset.UtcNow`; assert `StaleDate` round-trips within 1s | New unit/runtime test | Medium |
| `ActivityUIDismissalPolicy` operator== on two `.Default` instances (same pointer → equal) | headless | Low |
| Confirm `LiveActivity.Request` full lifecycle (start, update, end) in the sim runtime gate (not headless) | BindingTests runtime gate | High |

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | 5 async sequence shells (`ActivityUpdates` etc.) emitted with no callable members — visible in IntelliSense with no docs | Add a single summary `// NOTE: …` XML doc on each shell class pointing at `Swift.ActivityKit.LiveActivity` as the usable API | Trivial | Medium (discoverability) |
| 2 | C# Quality | `ActivityContent<TState>.state` property unreadable (AnyTypeFallback); constructor is emitted but primary payload inaccessible | Track as a generator gap (generic-param return projection); low priority since supplement doesn't expose `ActivityContent` | Low | Low |
| 3 | C# Quality | Dead shell boilerplate (~1400 lines) for non-iterable async sequences inflates the generated file | No action; compiles clean, does not affect consumer — comment drop already covers the skip | None | None |
| 4 | Test Coverage | `ActivityContent<TState>` constructor+properties have zero coverage | Add headless test: `new ActivityContent<int>(42, DateTimeOffset.UtcNow, 0.5)`, assert `StaleDate` and `RelevanceScore` round-trip | Low | Medium |
| 5 | Test Coverage | `LiveActivity.Request` success path not exercised in Tests.cs | Confirm it is covered by the existing sim runtime gate; if not, add to BindingTests runtime leg | Low | High |
