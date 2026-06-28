# BlinkIDUX — Binding Audit

- **Package**: SwiftBindings.BlinkIDUX v7.8.0   **Mode**: zip   **TFM(s)**: net10.0-ios
- **Native**: microblink/blinkid-ios 7.8.0, minIOS 16.0
- **Audited at**: swift-dotnet-packages main 1e8c27a, generated 2026-06-27

## Verdict

Types 36/41 (88%), members 170/230 (74%) before synthesized members (+115). The binding is **usable
from a non-SwiftUI .NET app via the bridge path** — `BlinkIDUXViewSession.CreateAsync(licenseKey, …)` →
`session.ViewController` gives a ready-to-embed `UIViewController` wrapping the full BlinkIDUX
scanning UI. That headline path works. However, the **headless path (BlinkIDUXModel)** is a dead
end: all 9 action methods on `ScanningViewModel<T,U,V,A>` are dropped by `GenericProtocolConstraint`,
leaving the class as a read-only observer with no way to call `StartScanning`, `PauseScanning`, etc.
The bridge result callback (`Action<int>?`) delivers a raw integer whose mapping to `ScanningResult`
case-tags is nowhere documented in the binding.

## 1. Coverage

### Counts

| Bucket | Types | Members |
|---|---|---|
| Emitted | 36 | 170 |
| Synthesized | — | +115 |
| Skipped | 4 (types) + 2 (AnyTypeFallback proxy-members) | 40 |
| Total | 41 | 230 |

Synthesized members (+115) are mostly `Dispose`, `GetTypeMetadata`, enum-case factories, and
constructor overloads — standard generator additions, not gaps.

### Skip-reason breakdown

#### ModuleInternal (11) — (a) correctly excluded

All 11 are `projectedX` Combine `@Published` property storage projections (e.g.
`projectedresult`, `projectedscanningResult`) and one implicit-override `init`. These are
internal Combine machinery — not consumer-facing. Correct.

#### SwiftUIView (4) — (a) correctly excluded, 2/4 bridged

| View | BridgeStatus | Notes |
|---|---|---|
| `BlinkIDUXView` | Generated | → `BlinkIDUXViewSession.CreateAsync()` in `.SwiftUIBridge.cs` |
| `NoInternetView` | Generated | → `NoInternetViewSession.Create()` in `.SwiftUIBridge.cs` |
| `CameraPreview` | TemplatePending | Simple init, but init takes `any PreviewSource` existential — bridge template not yet generated |
| `CameraView` | TemplatePending | Generic `Camera` parameter has no resolvable constraint — blocked |

`BlinkIDUXView` and `NoInternetView` are bridged and usable. `CameraPreview` and `CameraView` are
intentional template-pending stubs.

#### GenericProtocolConstraint (9) — **(b) real gap: headless scanning path blocked**

All 9 skips are on `ScanningViewModel<T,U,V,A>`:

| Skipped method | Impact |
|---|---|
| `startScanning` | Cannot begin a scan session |
| `pauseScanning` | Cannot pause |
| `resumeScanning` | Cannot resume |
| `restartScanning` | Cannot restart |
| `stopEventHandling` | Cannot stop the event stream |
| `presentAlert` | Cannot trigger alert presentation |
| `dismissAlert` | Cannot dismiss alerts |
| `licenseErrorAlertDismised` | Cannot acknowledge license errors |
| `timeoutAlertDismised` | Cannot acknowledge timeout alerts |

The constraint comes from `A : AlertTypeProtocol where A : Swift.Identifiable` (PAT constraint) and
`V : ReticleStateMachineProtocol` (Combine `ObservableObject` bound). The C#
`ScanningViewModel<T,U,V,A>` class IS emitted (`BlinkIDUX.cs:10198`) with its property surface
(`ScanningResult`, `Roi`, `ReticleStateMachine`, `IsTorchOn`, `ShowIntroductionAlert`, `AlertType`)
but has **zero callable action methods**. A consumer building a headless custom UI on top of
`BlinkIDUXModel` is stuck — they can observe state but cannot drive the scanner.

**Worth a generator fix?** Medium effort, high value for headless consumers. Requires lifting the PAT
constraint for methods whose parameter/return types don't actually involve the associated type at the
call site (e.g., `startScanning()` takes no arguments and returns `Void` — the constraint is a
Swift formality, not used at the call site). A targeted approach: emit a constrained non-generic
helper trampoline for each zero/simple-arg void method in `BlinkIDUXModel`'s concrete specialization
(`T=BlinkIDScanningResult, U=UIEvent, V=ReticleStateMachine, A=BlinkIDScanningAlertType`) via
`@_silgen_name` with the concrete type-metadata already known.

#### GenericTypeCallback (2) — (b) real gap, lower priority

`BlinkIDUXModel.processAnalyzerResult` and `BlinkIDUXModel.finishScan` are async methods on a generic
parent — the Swift wrapper needs the parent's type metadata in implicit registers, which a direct
CallConvSwift P/Invoke can't supply. Both are internal orchestration methods; `processAnalyzerResult`
feeds results into the ViewModel's published properties, `finishScan` terminates the session. Without
these, the headless path can't advance through the scan lifecycle. Downstream of the
GenericProtocolConstraint gap — fix that first.

#### EveryProtocolConformanceSkipped (6) — (b) real gaps, varying priority

All 6 are protocol proxies that were not emitted because EveryProtocol conformance had no decision
recorded:

| Proxy | Protocol | Consumer impact |
|---|---|---|
| `ScanningResultProtocolProxy` | `ScanningResultProtocol` | Can't implement custom scan-result carriers |
| `EventStreamProxy` | `EventStream<Event>` | Can't pass a custom C# event stream to Swift |
| `CameraFrameAnalyzerProxy` | `CameraFrameAnalyzer<Frame, Event>` | Can't implement a custom analyzer in C# |
| `OnboardingStepProtocolProxy` | `OnboardingStepProtocol` | Can't supply custom onboarding steps |
| `ReticleStateMachineProtocolProxy` | `ReticleStateMachineProtocol` | Can't swap in a custom reticle state machine |
| `ReticleStateProtocolProxy` | `ReticleStateProtocol` | Can't define custom reticle states |

`CameraFrameAnalyzerProxy` is the highest value: if you want to connect a non-BlinkID analyzer, you
need this. The concrete `BlinkIDAnalyzer` is provided, so for BlinkID-only consumers this is not a
blocker. `EventStreamProxy` similarly: `BlinkIDEventStream` covers the standard case. The remaining
four are largely internal machinery. These are EveryProtocol coverage gaps with no immediate
workaround other than using the concrete provided types.

#### UnsupportedSignature (7) — (b) real gaps on CameraFrameAnalyzer, minor on ReticleStateMachineProtocol

`CameraFrameAnalyzer.analyze`, `CameraFrameAnalyzer.result`, and five `ReticleStateMachineProtocol`
members fail with "unresolvable associated type reference." The `ICameraFrameAnalyzer<TResult, TFrame,
TEvent>` interface is emitted (`BlinkIDUX.cs:7626`) but has no methods in it — both core methods are
dropped. A C# dev implementing this interface via the proxy would have nothing to implement. The
`ReticleStateMachineProtocol` property/method drops are lower priority (internal UI state machinery).

**Worth a fix?** For `CameraFrameAnalyzer`: medium value (custom analyzers), high effort (associated
type resolution in interface context). The `IReticleStateMachineProtocol<TReticleStateType>` interface
at `BlinkIDUX.cs:11560` has the same issue.

#### AnyTypeFallback (2) — (b) minor gaps

`ScanningResultProtocol.scanResult` (`any T.Result` with AnyType as generic argument) and
`ReticleStateMachineProtocol.eventCounter` (`AnyPublisher<Int, Never>` — Combine publisher with
constrained type). Both are observable/reactive members where the type system can't resolve a
concrete C# equivalent. Low consumer priority.

#### UnsupportedType (3) — (a) correctly excluded

`unownedExecutor` on `BlinkIDEventStream`, `BlinkIDAnalyzer`, and `CaptureService` — actor runtime
bookkeeping property, not user-facing. Correct.

### UnsupportedCommentDrops worth noting

- `ScanningResult.Completed` and `ScanningResult.Interrupted` P/Invokes were stripped because
  "generic-enum payload-case constructor has no exported function symbol." These are the two payload
  cases that would carry `BlinkIDResultState` data. **Impact on consumers**: these cases are
  *received from Swift* (not constructed in C#), so the strip matters only when reflecting on a
  received value via `TryGetCompleted(out var result)` / `TryGetInterrupted(out var interrupted)`.
  The `TryGet*` methods are emitted — the gap is that the matching factory constructors are absent,
  which means C# code can't synthesize a `Completed` result independently. Low consumer impact.

### Prioritized generator unlocks

| Priority | API | Gap | Estimated effort |
|---|---|---|---|
| 1 | `ScanningViewModel` action methods (9) | GenericProtocolConstraint on PAT-constrained class | Medium — concrete specialization trampolines |
| 2 | `CameraFrameAnalyzer.analyze` / `.result` | UnsupportedSignature on associated type | Medium-high |
| 3 | `GenericTypeCallback` on `finishScan` / `processAnalyzerResult` | Async on generic parent | High |
| 4 | EveryProtocol proxies for EventStream, CameraFrameAnalyzer | EveryProtocol coverage gap | Medium (per-protocol) |

## 2. C# Quality

### Naming and shape

Clean throughout. PascalCase enforced, no leaked Swift mangling, namespacing under `BlinkIDUX` is
correct. Nested enum `Camera.CameraPosition` (`BlinkIDUX.cs:8285`) maps the Swift `Camera.Position`
cleanly. `UIEvent` is aliased as `BUXUIEvent` in the test to avoid collision with UIKit — the type
is correctly in the `BlinkIDUX` namespace, so consumers need `BUXUIEvent = BlinkIDUX.UIEvent`; this
is a mild ergonomic note, not a generator issue.

### Bridge entry point (headline path)

```csharp
// BlinkIDUX.SwiftUIBridge.cs:152
public static async Task<BlinkIDUXViewSession> CreateAsync(
    string licenseKey,
    bool showIntroductionAlert = true,
    bool showHelpButton = true,
    bool allowHapticFeedback = true,
    bool preferFrontCamera = true,   // NOTE: defaults to front camera; ID scanning typical is back
    Action<int>? onResult = null)
```

**Issue 1 — `onResult: Action<int>?` is opaque.** The `int resultCode` corresponds to a
`ScanningResult<T,U>.CaseTag` raw value (Completed=0, Interrupted=1, Cancelled=2, Ended=3 based on
`BlinkIDUX.cs:7310`), but there is no documentation, no conversion helper, and no named type in the
generated binding. A consumer receiving `onResult: code => { }` cannot determine programmatically
whether the scan completed successfully or was cancelled without magic-numbering or cross-referencing
the enum. **Recommendation**: emit a `/// <remarks>See ScanningResult.CaseTag for values.</remarks>`
XML doc comment on the bridge method, or add a static helper `BlinkIDUXViewSession.DecodeResultCode(
int code)` → `ScanningResult<BlinkIDResultState, UIEvent>.CaseTag`.

**Issue 2 — `preferFrontCamera` defaults to `true`.** Most ID scanning uses the back camera. This
default will surprise consumers who don't read the parameter name carefully. Not a bug, but worth a
`/// <param name="preferFrontCamera">Defaults to true (front camera). Pass false for the back camera,
which is typical for document scanning.</param>` note.

### BlinkIDUXModel constructor type safety

```csharp
// BlinkIDUX.cs:2981
public unsafe BlinkIDUXModel(ISwiftObject analyzer, BlinkIDUX.ScanningUXSettings uxSettings)
```

`analyzer` is typed as `ISwiftObject` (the root interface), not `BlinkIDAnalyzer` or
`ICameraFrameAnalyzer`. This is forced by the constrained-existential bridge pattern
(`ConstrainedExistentialBridge` wrapping the `CameraFrameAnalyzer` protocol constraint). A consumer
can accidentally pass any Swift object and get a runtime crash. The XML doc inherited by the generator
should at minimum note "pass a `BlinkIDAnalyzer` instance."

### Async surface

`BlinkIDAnalyzer.CreateAsync()` (`BlinkIDUX.cs:786`) returns `Task<BlinkIDAnalyzer>` — correct async
pattern. `BlinkIDEventStream.Stream` (`BlinkIDUX.cs:76`) exposes `IAsyncEnumerable<IReadOnlyList<
BlinkIDUX.UIEvent>>` with proper cancellation hookup via `SwiftAsyncCancellation.NextCancelKey()` —
solid async/cancel integration. `BlinkIDUXViewSession.CreateAsync()` is a `Task<BlinkIDUXViewSession>`
using `TaskCompletionSource` — clean pattern. No blocking-only async surfacing observed.

### Nullability

`BlinkIDUXModel.result` is `BlinkIDResultState?` (optional in Swift → nullable in C#). `NetworkMonitor
.IsConnected` is `bool` (non-optional). `Camera.Status` (`CameraStatus`) is non-optional. Nullable
annotations appear consistent with the Swift optionals.

### Lifetime / IDisposable

All bound types implement `IDisposable` with finalizer ARC-release backup. `BlinkIDUXViewSession` has
a careful two-phase dispose (packs GCHandles into a native buffer, lets Swift's `_Free` wrapper invoke
the post-release trampoline AFTER `Unmanaged.release`). `NoInternetViewSession` similarly. The
pattern is sound and avoids the use-after-free window on queued main-thread work.

### BlinkIDTheme — static-accessor ergonomics

`BlinkIDTheme` partial class in `.SwiftUIBridge.cs` exposes theme customization as static
`SetXxx(SwiftColor)/GetXxx()` methods — no instance required. This is a clean design for a singleton
theme service. The `SwiftColor` type is from `Swift.Runtime`, which a consumer needs to know about
to use. Not a problem, but worth a code sample in the README.

### ScanningViewModel<T,U,V,A> — effectively dead class

```csharp
// BlinkIDUX.cs:10198
public partial class ScanningViewModel<T, U, V, A> : ISwiftObject, IDisposable where A : ISwiftObject, IAlertTypeProtocol
```

Properties are emitted: `ScanningResult` (generic `T?`), `Roi` (CGRect wrapper), `ReticleStateMachine`
(generic `V`), `IsTorchOn` (bool), `IsToastVisible` (bool), `ShowIntroductionAlert` (bool),
`AlertType` (generic `A?`). But no action methods. A developer who finds this class and tries to
call `model.StartScanning()` will find nothing — no IntelliSense, no docs. The class reads like a
ViewModel but has no controller surface. This will generate support questions. The XML doc on the
class should explicitly say "scanning lifecycle methods are not available from C# due to protocol
constraint limitations; use BlinkIDUXViewSession.CreateAsync() for a ready-to-use scanning UI."

## 3. Test Coverage

### Count and structure

One file (`tests/Program.cs`), 15 test phases, approximately 107 distinct test cases.

| Phase | Focus | Depth |
|---|---|---|
| 1 | Cross-module BlinkID type metadata (5 types) + DetectionStatus case construction | Weak — metadata size only except 1 case construction |
| 2 | 16 BlinkIDUX type metadata probes | Weak — metadata size only |
| 3 | PassportOrientation (3 cases), CaptureActivity, Camera.CameraPosition raw values | Moderate — raw values + distinctness |
| 4 | CameraStatus (6 cases): tags + distinctness + CaseTag raw values | Moderate |
| 5 | BlinkIDScanningAlertType (2 cases + Title/Description + CaseTag raw values) | Moderate; wrapper-dependent Title/Description defensively guarded |
| 6 | DocumentSide (4 no-payload + Passport payload + TryGetPassport + CaseTag raw values) | Moderate-strong |
| 7 | ReticleState (6 no-payload + 3 payload: Error/Passport/InactiveWithMessage + CaseTag raw values) | Moderate-strong |
| 8 | UIEvent (13 no-payload + 2 payload: RequestDocumentSide/WrongSidePassport + CaseTag raw values) | Moderate-strong |
| 9 | MicroblinkColor (7 CaseByIndex + CaseTag raw values + RawValue + FromRawValue) | Moderate; CaseByIndex wrapper-dependent |
| 10 | SKIP (CaptureMode opaque, correctly noted) | — |
| 11 | ScanningResult<BlinkIDResultState, UIEvent>: Cancelled/Ended tags + TryGetCompleted/Interrupted + CaseTag + generic metadata | Moderate-strong |
| 12 | Class constructors: BlinkIDEventStream, NetworkMonitor, BlinkIDTheme.Shared | Weak (construct-only; wrapper-dependent) |
| 13 | ScanningUXSettings constructors (default, full params, back-camera) | Weak (construct-only) |
| 14 | Property access: NetworkMonitor.IsConnected/IsOffline, protocol conformance, ToString | Moderate |
| 15 | Protocol interfaces existence (6) + proxy-implements-protocol (6) + ISwiftObject/IDisposable (14+14) | Weak (reflection-only; proves shape, not behavior) |

### Wrapper-dependency test pattern

Phases 5, 6 (Passport payload), 7 (payload cases), 8 (payload cases), 9 (CaseByIndex), 12, 13, 14
all catch `DllNotFoundException` tagged with "SWIFTBIND051 — BlinkIDUXSwiftBindings wrapper
compilation failed." The wrapper xcframework IS present in `obj/Debug/net10.0-ios/swift-binding/`
at HEAD, so this may reflect a defensive pattern from an earlier build state rather than a live
runtime failure. If the wrapper loads correctly at runtime, these tests will exercise the wrapper
path. If the wrapper consistently compiles, the defensive guards are harmless noise but should be
promoted to a named helper (`AssertWrapperOrSkip`) to reduce boilerplate.

### Untested surface (critical)

| Surface | Gap | Recommended test |
|---|---|---|
| `BlinkIDUXViewSession.CreateAsync()` | Zero coverage — the headline bridge entry point | Launch with a real license key (or a mock license key expected to fail with a known error), assert the `InvalidOperationException` message or `GetViewController()` returns non-null |
| `BlinkIDEventStream.Stream` | No test consumes the async stream | Construct `BlinkIDEventStream()`, call `Stream` and begin iteration with a short timeout (stream is infinite, just assert it starts without throwing) |
| `BlinkIDAnalyzer.CreateAsync()` | Zero coverage — the key factory for the headless path | Construct `BlinkIDAnalyzer.CreateAsync(sdk, settings, stream)` with a dummy `BlinkIDSdk`; assert it succeeds or throws the expected license error |
| `BlinkIDUXModel` construction | Zero coverage | `new BlinkIDUXModel(analyzer, new ScanningUXSettings())` |
| `ScanningUXSettings` property round-trip | Constructors tested but no property getter assertions | After `new ScanningUXSettings(showHelpButton: false)`, assert `ShowHelpButton == false` |
| `BlinkIDTheme` bridge setters/getters | Zero coverage for the bridge theme path | `BlinkIDTheme.SetAlertTitleColor(new SwiftColor(1,0,0,1))` then `GetAlertTitleColor()` and assert RGBA round-trips |
| `NoInternetViewSession.Create()` | Zero coverage for the secondary bridge | `NoInternetViewSession.Create(retryAction: () => {})` → `GetViewController()` non-null |

The most important gap is `BlinkIDUXViewSession.CreateAsync()` — the headline consumer scenario has
zero runtime coverage. Even a negative test (invalid license key → `InvalidOperationException` with
the license error string) would prove the bridge plumbing works end-to-end.

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `ScanningViewModel` 9 action methods all GenericProtocolConstraint-skipped; headless path has no control methods | Concrete specialization trampolines for `BlinkIDUXModel`'s known type params (T=BlinkIDScanningResult, U=UIEvent, V=ReticleStateMachine, A=BlinkIDScanningAlertType) | Med | High |
| 2 | C# Quality | `onResult: Action<int>?` in `BlinkIDUXViewSession.CreateAsync` — raw int, undocumented mapping | Add XML doc comment mapping to `ScanningResult<..>.CaseTag`; or emit a `DecodeResultCode` helper | Low | High |
| 3 | C# Quality | `BlinkIDUXModel(ISwiftObject analyzer, …)` — `ISwiftObject` parameter loses type safety | XML doc note: "pass a `BlinkIDAnalyzer` instance" | Low | Med |
| 4 | C# Quality | `ScanningViewModel<T,U,V,A>` has properties but no action methods — confusing dead surface | XML doc: explicitly state lifecycle methods are unavailable from C# and direct to bridge path | Low | Med |
| 5 | C# Quality | `preferFrontCamera` defaults to `true` in bridge; atypical for document scanning | XML param doc noting back camera is typical for ID scanning | Low | Low |
| 6 | Coverage | `ICameraFrameAnalyzer<TResult,TFrame,TEvent>` interface emitted but all methods dropped (UnsupportedSignature) | Track: custom analyzer injection blocked | Med-High | Med |
| 7 | Tests | `BlinkIDUXViewSession.CreateAsync()` — headline bridge path has zero test coverage | Add a bridge smoke test: invalid license key → catches `InvalidOperationException` (no network/device needed) | Low | High |
| 8 | Tests | `BlinkIDEventStream.Stream` / `BlinkIDAnalyzer.CreateAsync()` / `BlinkIDUXModel` constructor — zero coverage for the headless path | Add construction smoke tests; stream begin-iteration with timeout | Med | High |
| 9 | Tests | `BlinkIDTheme` bridge set/get round-trip untested | `SetAlertTitleColor` → `GetAlertTitleColor` → assert RGBA round-trip | Low | Med |
| 10 | Tests | `ScanningUXSettings` property round-trip untested after construction | After `new ScanningUXSettings(showHelpButton: false)` assert `ShowHelpButton == false` | Low | Med |
