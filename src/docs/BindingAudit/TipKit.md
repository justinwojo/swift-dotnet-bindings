# TipKit — Binding Audit

- **Package**: SwiftBindings.Apple.TipKit v26.2.8   **Mode**: apple   **TFM(s)**: net10.0-ios26.2, net10.0-macos26.2, net10.0-maccatalyst26.2, net10.0-tvos26.2
- **Native**: Apple TipKit.framework (iOS 17.0+, macOS 14.0+, macCatalyst 17.0+, tvOS 17.0+, visionOS 1.0+, watchOS 10.0+)
- **Audited at**: swift-dotnet-packages 1e8c27a, generated 2026-06-27

## Verdict

Types coverage is excellent (41/42, 97.6%), and the infrastructure layer — `Tips.Configure`, `ResetDatastore`, `ShowAllTipsForTesting`, error types, `TipUIPopoverViewController`, `TipUIView` — is fully usable. However, the **core tip-display workflow is not achievable from C# alone**. The `Tip` protocol relies on Swift compile-time macros (`@Parameter`, `#Rule`) that have no C# equivalent, meaning no C# class can conform to `Tip` and no tip instance can be created through the binding. Compounding this, `shouldDisplay`, `statusUpdates`, `shouldDisplayUpdates`, and `invalidate(reason:)` — the four protocol-extension properties used to query and control a tip — are **absent from the generated C# surface entirely** (not even listed in SkippedItems). The binding is usable as a *presentation* layer (plug in an `AnyTip` produced by a Swift helper) but cannot support the full define-register-display lifecycle on its own.

## 1. Coverage

### Counts

| Dimension | Emitted | Total | % |
|---|---|---|---|
| Types | 41 | 42 | 97.6% |
| Members | 143 | 177 | 80.8% |
| Synthesized (generator-added) | 82 | — | — |
| Skipped | 59 | — | — |

Note: SynthesizedMembers (82) are generator-added helpers (CSM factory methods, metadata accessors, conformance boilerplate) that inflate the emitted count; the binding wraps ~61 native Swift members out of 177 total.

### Skip-reason breakdown

| Reason | Count | Classification |
|---|---|---|
| SwiftUIConstraint | 15 | **(a) Correctly excluded** — `backgroundStyle`, `imageStyle`, `configureTip` on UIKit views; SwiftUI.View/Edge in signature |
| SynthesizedCodable | 6 | **(a) Correctly excluded** — synthesized `Codable` encode/init on `Event.Donation`, `EmptyDonation`, `DonationTimeRange` |
| AnyTypeFallback | 9 | **(b) Real gap** — see below |
| UnsupportedSignature | 8 | **(b) Real gap** — see below |
| GenericTypeCallback | 5 | **(b) Real gap** — see below |
| EveryProtocolConformanceSkipped | 5 | **(b) Structural** — `EventPredicateExpressionProxy`, `TipViewStyleProxy`, `RuleInputProxy`, `SequenceProxy`, `ViewProxy`; expected while EveryProtocol conformance emission is incomplete |
| UnsupportedExistential | 4 | **(b) Real gap** — see below |
| DuplicateSignature | 3 | **(b) Real gap** — see below |
| UnsupportedClosure | 3 | **(b) Real gap** — see below |
| UnsupportedType | 1 | **(a) Acceptable** — `TipKitError.~=` operator has no C# equivalent |
| SwiftUIView | 1 | **(a) Correctly handled** — `TipView` is SwiftUI; bridge template generated |

### (a) Correctly excluded — intended

15 SwiftUIConstraint + 6 SynthesizedCodable + 1 UnsupportedType + 1 SwiftUIView = 23. These match the project's deliberate decisions.

### (b) Real gaps

**Unreported gap (not in SkippedItems): `shouldDisplay`, `statusUpdates`, `shouldDisplayUpdates`, `invalidate(reason:)`**

These four protocol-extension properties/methods are in the Swift `extension Tip` block in the swiftinterface (lines 403–426 of arm64-apple-ios-simulator.swiftinterface), but they do **not appear anywhere in TipKit.cs** and are not listed in `SkippedItems`. The symbol graph doesn't emit protocol-extension members on concrete conforming types — the generator never sees them. For `AnyTip`, these map to fully concrete signatures:
- `AnyTip.shouldDisplay → Bool`
- `AnyTip.statusUpdates → AsyncStream<Tips.Status>`
- `AnyTip.shouldDisplayUpdates → AsyncMapSequence<AsyncStream<Tips.Status>, Bool>`
- `AnyTip.invalidate(reason: Tips.InvalidationReason) → Void`

All four are wrappable with the current toolchain. Without them, the only way to know whether to display a tip is to call `Tips.ShowAllTipsForTesting()` (a debug override) — there is no production query path.

**AnyTypeFallback (9):**
- `TipKit.Tip::actions` / `TipKit.Tip::rules` — associated-type members on the protocol itself; `Self.Action`/`Self.Rule` can't be projected without knowing `Self`. **Architectural**: not fixable without PAT specialization.
- `TipKit.AnyTip::actions` / `AnyTip::rules` — on the concrete `AnyTip` struct, `actions` returns `[AnyTip.Action]` and `rules` returns `[AnyTip.Rule]`, both of which ARE concrete types that the generator has already emitted. These two are **tractable** — the generator should resolve them as concrete specialized types, not fall back to `AnyType`.
- `TipKit.Tips.Event::donations` / `Tips.Event.Donation::subscript` — `[Event<DonationInfo>.Donation]` generic property; emittable with specialized concrete handling. **Medium tractability.**
- `TipKit.Tips.Parameter::wrappedValue` — typed as `Value` (associated type). **Architectural** on the protocol; concrete on each Parameter specialization.
- `TipKit.TipUIPopoverViewController::presentationDelegate` / `sourceItem` — existential inner protocol fallback, typed as `object`. **Minor usability gap**: consumer can't cast to anything useful.

**UnsupportedSignature (8):**
- `AnyTip::init` ×2 — `AnyTip.init<T: Tip>(_:T)` is the primary way to erase a concrete tip to `AnyTip`. C# doesn't support generic constructors with method-own type parameters (TipKit.cs:577). The generator emits a concrete specialization `AnyTip.FromTipKit_AnyTip(AnyTip)` (TipKit.cs:585) — circular, wraps an `AnyTip` in itself. **No path exists to create an `AnyTip` from C# without a Swift factory shim.** High impact; architectural.
- `TipUIPopoverViewController::init` — generic constructor; present via non-generic overload (ITip, object, Action<Tips.Action>), so display still works.
- `Tips.Event::init` ×2, `Tips.Parameter::init`, `Tips.Action::init` — generic constructors. Without these, `Event<DonationInfo>` and `Parameter<Value>` instances can't be created from C#.
- `TipViewStyle::makeBody` — associated type in View return; SwiftUI constraint.

**GenericTypeCallback (5):**
- `Tips.Event<TDonationInfo>::donate` ×2, `sendDonation` ×2, `deleteDonations` — all five async/closure mutation methods on the generic `Event<T>` type are completely skipped. **Entire event-donation workflow unavailable.** The generator can't emit a direct CallConvSwift P/Invoke for async members on a generic parent; a Swift wrapper shim is the documented workaround. Medium tractability (requires emitter support for thunk generation on generic types).

**UnsupportedExistential (4):**
- `Tips.OptionsBuilder::buildExpression` / `buildOptional` — take `[any TipOption]`. Medium value; result builder plumbing, not primary API.
- `Tips::showTipsForTesting` / `hideTipsForTesting` — take `[any Tip.Type]` (metatype existential). Cannot target specific tip types for testing from C#; only `ShowAllTipsForTesting` / `HideAllTipsForTesting` work. Medium value.

**DuplicateSignature (3):**
- `Tips.Event::init` ×2 — two constructor overloads resolve to the same C# signature; generator drops both. An init with only `id: String` (TipKit.cs comment: `ctor:init(id:Swift.SwiftString)`) and one with `id + donationLimit` are the victims. With `UnsupportedSignature` also killing the remaining constructors, `Event<T>` is completely uncreatable from C#.
- `Tips.GroupBuilder::buildPartialBlock` ×1 — result builder plumbing, low consumer impact.

**UnsupportedClosure (3):**
- `Tips.Rule::init` ×2 — `#Rule` predicates are compiled as closure arguments (`(PredicateExpressions.Variable<Event/Parameter>) -> any StandardPredicateExpression<Bool>`). This closure shape can't be marshalled, so no `Rule` can be created from C#. **Confirms the macro gap**: rules are Swift-only.
- `Tips.Action::label` — getter-only closure property typed as `() -> Text`; can't round-trip. **Low impact**: action label is a display property.

### Prioritized generator unlocks

| Priority | Gap | Unlock | Effort |
|---|---|---|---|
| **P1** | `shouldDisplay`, `statusUpdates`, `shouldDisplayUpdates`, `invalidate` absent from `AnyTip` | Generator: surface protocol-extension members from `extension Tip { … }` on concrete conforming types (`AnyTip`). These have fully concrete signatures once `Self = AnyTip`. | Medium |
| **P2** | `AnyTip::actions`, `AnyTip::rules` falling back to `AnyType` | Generator: resolve `AnyTip.Action` / `AnyTip.Rule` as concrete specializations (already emitted types); stop falling back to `AnyType` for protocol-extension members on a type-erasing struct. | Low-Medium |
| **P3** | `Tips.Event.donate/sendDonation/deleteDonations` skipped as GenericTypeCallback | Emitter: generate a non-generic Swift thunk shim for async methods on generic types. Unblocks event-based rule conditions entirely. | High |
| **P4** | `Tips.showTipsForTesting/hideTipsForTesting` (UnsupportedExistential metatype array) | Generator: emit a Swift shim that accepts a fixed concrete-type list; reduces to calling the no-arg form. Low priority — debug helpers. | Medium |

## 2. C# Quality

**Naming/shape**: Clean. PascalCase, no leaked mangling. The `Tips` static class nests subtypes (`Tips.Event<T>`, `Tips.Parameter<T>`, `Tips.Rule`, `Tips.Action`, `Tips.Status`, `Tips.ConfigurationOption.*`) correctly. Enum names (`InvalidationReason`, `Priority`, `CaseTag`) read naturally.

**ITip interface (TipKit.cs:188)**: Correctly carries `string Id`, `Swift.SwiftUI.Text Title`, `Swift.SwiftUI.Text? Message`, `SwiftUI.Image? Image`, `IReadOnlyList<ITipOption> Options`. The `actions` and `rules` protocol requirements are absent (correctly excluded as AnyTypeFallback). The missing `shouldDisplay`/`statusUpdates` are the real deficit — a consumer holding an `ITip` reference has *no query surface* beyond inspecting title/message.

**Async**: `Tips.Status` `statusUpdates` / `shouldDisplayUpdates` are not surfaced at all, so there is no async observation story. `TipGroup.CurrentTipUpdates` returns `object` (TipKit.cs:721), not a typed async sequence — entirely opaque to the consumer.

**Nullability**: Correct throughout. `ITip? CurrentTip` (TipKit.cs:677), `Swift.SwiftUI.Text? Message` (TipKit.cs:353), `SwiftUI.Image? Image` (TipKit.cs:404) all carry `?` as expected.

**Lifetime**: `IDisposable` present on value-type wrappers (`AnyTip`, `Tips.Event<T>`, `Tips.Parameter<T>`, `Tips.Rule`, `Tips.Action`). `TipGroup` is an ARC class — finalizer handles release; `Dispose()` offered for deterministic cleanup. `TipUIView` / `TipUIPopoverViewController` inherit ObjC lifetime. Pattern is correct.

**TipGroup constructor (TipKit.cs:888)**: Decorated `[Obsolete("Closure parameter shape not yet bridgeable...", SB0005)]` and throws `NotSupportedException` at runtime. The `[UnsupportedSwiftType]` attribute and `Obsolete` + `SB0005` diagnostic properly warn at compile time. The `currentTip` (TipKit.cs:677) and `currentTipUpdates` (TipKit.cs:721) properties work at iOS 18+ — `currentTip` returns a usable `ITip?`, `currentTipUpdates` returns `object` (the AsyncSequence existential — opaque but ARC-managed). Neither can be obtained without first constructing a `TipGroup`, which requires the tombstoned ctor. Consumer is stuck.

**TipUIView.FromTipKit_AnyTip (TipKit.cs:10660)**: Requires iOS 18.0+ (`[SupportedOSPlatform("ios18.0")]`). The original Swift `TipUIView.init(tip:arrowEdge:)` is iOS 17.0+, but the SwiftUI.Edge argument triggers a SwiftUIConstraint skip on both iOS 17 overloads (TipKit.cs:10523–10524), and the concrete-specialization emitter targets the iOS 18 variant. **An iOS 17 consumer has no UIKit-embedded tip view from C#** — only `TipUIPopoverViewController` (which works at iOS 17).

**TipUIPopoverViewController (TipKit.cs:9854)**: The `Init(ITip, object, Action<Tips.Action>)` constructor is fully functional at iOS 17+. The `object sourceItem` parameter carries `[OriginalSwiftType("any UIKit.UIPopoverPresentationControllerSourceItem")]` (TipKit.cs:9854) — tells the consumer what's expected. `presentationDelegate` and `sourceItem` degrade to `object` (AnyTypeFallback), which is a minor but acknowledged quality gap.

**AnyTip.FromTipKit_AnyTip (TipKit.cs:585)**: The concrete-specialization factory wraps an existing `AnyTip` inside a new `AnyTip` — semantically valid (double-erasure is a no-op in Swift), but there is no path to get the *first* `AnyTip`. This is documentation of the limitation, not a usable escape hatch.

**No outright broken code**: All skipped members are noted with inline `// Unsupported:` comments. The tombstoned `TipGroup(priority, object?)` constructor throws clearly. Platform availability guards are thorough and correct.

## 3. Test Coverage

### Case count and structure

Tests.cs contains **20 test cases**, all in a single `Tests.Run()` method. No additional domain files beyond `Program.UIKit.cs` (harness entry point) and `Program.MacConsole.cs`.

| Test # | Surface | Depth |
|---|---|---|
| 1 | `Tips.Configure()` (no-arg) | Weak — smoke only; Swift errors are caught and re-classified as pass |
| 2 | `Tips.ShowAllTipsForTesting()` | Weak — no-throw smoke |
| 3 | `Tips.HideAllTipsForTesting()` | Weak — no-throw smoke |
| 4 | `Tips.Status.Pending`, `.Available` singletons non-null | Weak — nullability only |
| 5 | `Tips.Status.CaseTag` enum ordinals (0, 1, 2) | Medium — ABI tag ordering |
| 6 | `Tips.Status.Pending.Tag == Pending` | Medium — round-trip tag on singleton |
| 7 | `Tips.Status.Available.Tag == Available` | Medium — round-trip tag on singleton |
| 8 | `Tips.InvalidationReason` enum ordinals | Medium — ABI ordering |
| 9 | `TipGroup.Priority` enum ordinals | Medium — ABI ordering |
| 10 | `TipKitError.TipsDatastoreAlreadyConfigured` non-null | Weak — nullability only |
| 11 | `TipKitError.InvalidPredicateValueType` non-null | Weak — nullability only |
| 12 | `TipKitError.MissingGroupContainerEntitlements` non-null | Weak — nullability only |
| 13 | `TipKitError` metadata loads | Weak — metadata probe |
| 14 | `Tips.Status` metadata loads | Weak — metadata probe |
| 15 | `Tips.DonationTimeRange` singletons (Minute, Hour, Day, Week) non-null | Weak — nullability only |
| 16 | `Tips.DonationTimeRange` factory methods (Minutes, Hours, Days, Weeks) non-null | Weak — nullability only |
| 17 | `Tips.DonationLimit(3).MaximumCount == 3` | **Strong** — round-trip constructor → property |
| 18 | `Tips.IgnoresDisplayFrequency(true)` non-null | Weak — ctor smoke |
| 19 | `Tips.MaxDisplayCount(5)` non-null | Weak — ctor smoke |
| 20 | `Tips.ParameterOption.Transient` non-null | Weak — nullability only |

**Depth**: 1 strong test (DonationLimit round-trip), 4 medium (enum ABI ordinals + Status singleton tags), 15 weak (smoke/nullability/metadata). No test calls into the core tip workflow.

### Zero-coverage surface (most important)

- **`Tips.Configure(IEnumerable<ConfigurationOption>)` with options** — the overload that matters for production (CloudKit, DatastoreLocation, DisplayFrequency) is untested.
- **`AnyTip` at all** — no test instantiates or reads any `AnyTip` property (`Id`, `Title`, `Message`, `Options`). `AnyTip.FromTipKit_AnyTip(AnyTip)` is untested.
- **`shouldDisplay` / `statusUpdates` / `invalidate`** — not testable yet (missing from binding).
- **`TipUIPopoverViewController`** — the working UIKit display path is completely untested.
- **`TipUIView.FromTipKit_AnyTip`** — untested (iOS 18+ only, but still worth covering).
- **`Tips.ResetDatastore()`** — untested.
- **`Tips.Status.Invalidated(reason:)`** — constructor + `TryGetInvalidated` round-trip untested.
- **`Tips.DonationLimit(maximumCount:timeRange:)`** — two-arg overload untested.
- **`TipGroup`** — no test even attempts construction (tombstoned, but `SB0005` Obsolete diagnostic should be verified to compile-warn).
- **`TipKitError` equality / typed throw** — no test throws and catches a typed `SwiftException<TipKitError>`.

### Concrete tests to add

1. **`Tips.Configure` with options round-trip** — call `Tips.Configure([Tips.ConfigurationOption.DisplayFrequencyMethod(Tips.ConfigurationOption.DisplayFrequency.Hourly)])` (or similar); assert no DllNotFoundException/EntryPointNotFoundException. Add to Tests.cs after test 1.
2. **`Tips.ResetDatastore()`** — call after Configure; assert no crash. (Tests.cs after Configure tests.)
3. **`Tips.Status.Invalidated` + `TryGetInvalidated` round-trip** — `var s = Tips.Status.Invalidated(Tips.InvalidationReason.TipClosed); s.TryGetInvalidated(out var reason); assert reason == TipClosed`. Proves enum-with-associated-value ABI.
4. **`AnyTip` property read** — `AnyTip.FromTipKit_AnyTip(anyTipInstance)` then read `.Id` / `.Title`; requires a Swift factory to produce the first `AnyTip`. Worth adding a test-only `@_cdecl` Swift factory in the test Swift lib.
5. **`TipUIPopoverViewController` ctor smoke** — construct with a stub `ITip` returned from Swift; assert non-null, no crash. UIKit-only test; add to Program.UIKit.cs.
6. **`shouldDisplay` / `statusUpdates`** — add once P1 generator unlock lands.

## Action Items

| # | Dimension | Finding | Recommendation | Effort | Value |
|---|---|---|---|---|---|
| 1 | Coverage | `shouldDisplay`, `statusUpdates`, `shouldDisplayUpdates`, `invalidate(reason:)` absent from `AnyTip` — not in SkippedItems, never generated | Generator: parse `extension Tip { … }` protocol-extension members from symbol graph; emit them on concrete conforming types (`AnyTip`) using fully-concrete resolved signatures | Medium | **Critical** — these are the only way to query tip status in production |
| 2 | Coverage | `AnyTip::actions` / `AnyTip::rules` fall back to AnyType despite having concrete resolved types `[AnyTip.Action]` / `[AnyTip.Rule]` | Generator: resolve action/rule types for protocol-extension members on type-erasing structs before falling back to AnyType | Low-Medium | High — actions and rules are visible protocol requirements |
| 3 | Coverage | `Tips.Event<T>` donate/sendDonation/deleteDonations all GenericTypeCallback — entire donation workflow gone | Emitter: generate Swift thunk shims for async/closure methods on generic types; or document manual-shim pattern in wiki | High | Medium — event-based rules blocked |
| 4 | Coverage | `Tips.showTipsForTesting(tips:)` / `hideTipsForTesting(tips:)` UnsupportedExistential | Generator/wiki: emit a Swift shim that delegates to the type-array overload, or document workaround; the no-arg forms already work | Medium | Low — test helpers |
| 5 | Quality | `TipGroup.CurrentTipUpdates` returns `object` — opaque async sequence unusable | AnyTypeFallback degradation on AsyncSequence existential; note in type XML doc that the value must be observed via Swift interop | Low | Medium |
| 6 | Quality | `TipUIView.FromTipKit_AnyTip` requires iOS 18.0+ but the underlying Swift API is iOS 17.0+ | Investigate whether a wrapper for the iOS 17 `init(tip:)` overload (without the `arrowEdge` SwiftUI.Edge param) is wrappable | Low | Medium — iOS 17 UIKit users miss embedded TipView |
| 7 | Tests | 0 tests for `shouldDisplay`, `TipUIPopoverViewController`, `Tips.Status.Invalidated` round-trip, `Configure` with options, `ResetDatastore` | Add 6 targeted tests as described in §3 | Low | High |
