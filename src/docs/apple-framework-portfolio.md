# Apple Framework Portfolio Plan

**Date**: 2026-04-10
**Status**: Planning — decisions recorded, not yet executed
**Sibling docs**: `roadmap.md` (remaining work), `Future/apple-framework-binding-strategy.md` (technical pipeline for binding Apple frameworks — *how*, not *which*)

This doc answers a single question: **which Apple frameworks should swift-bindings ship as NuGet packages?** It records the decision framework, the audit of what's currently in `swift-dotnet-packages/apple-frameworks`, the evaluation of new candidates, and the rationale for every keep/drop call.

The companion doc `Future/apple-framework-binding-strategy.md` covers the technical pipeline (wrapper emission, direct-mode generator invocation, runtime resolver, NuGet layout). Read that for *how* we bind; read this for *what* we bind.

---

## Scope rules

A framework is worth publishing only if **both** are true:

1. **It fills a real gap.** Either the framework is Swift-only, or it has a large Swift-only API surface that `dotnet/macios` does not bind. No duplication of existing Microsoft bindings — if macios already ships it, we don't.
2. **It is consumable from C#.** No macro-locked APIs (e.g. `@AppIntent`). Compile-time metadata extraction pipelines that require the consumer to write Swift source (AppIntents, SwiftData) are out of scope.

When rule 2 is partially violated — the framework has a useful runtime surface but a mandatory SwiftUI or Swift-source touchpoint — the framework becomes a **product-shaped** offering rather than a pure binding. Those are treated separately (see § Product-shaped offerings).

## SwiftUI bridging capability (important context)

The generator already ships an automated SwiftUI bridge (`SwiftUIBridgeEmitter.cs`, `SwiftUIBridgeCollector.cs`, `SwiftUIViewDetector.cs`). It auto-generates a `UIHostingController` wrapper + C# session class for any concrete View struct the generator encounters. This meaningfully changes what "SwiftUI-only" means for binding decisions.

**What the bridge auto-handles today:**

- Concrete (non-generic) `View` struct conformances
- Constructor parameters: primitives (Int, Bool, Double, Float), String, enums (raw-value), Swift classes
- `Binding<T>` where T is Primitive / String / BoundEnum / `Optional<supported>`
- Closures up to 4 args, `Result<T, E>` callbacks decomposed into onSuccess/onError
- Lifecycle hooks (`onAppear`, `onDisappear`)
- Modal/push presentation helpers (`PresentAsSheet`, `PushOnNav`)

**What the bridge does NOT handle (and why):**

- **Generic Views with `View` constraints** (`JournalingSuggestionsPicker<Label>`). Classifier skip reason: `SwiftUIConstraint`.
- **`@ViewBuilder` parameters.** No representation in C#.
- **View modifier extensions** (`.translationTask(...)`, `.familyActivityPicker(...)`). Modifiers apply to `some View` and return opaque modifier chains.
- **`Binding<Struct>` (non-optional).** Explicitly marked deferred in `SwiftUIBridgeEmitter.InitAnalyzer.cs:733` — needs two-way lifetime management that has not been built. This is a one-time generator enhancement, not a fundamental limit.
- **Widget extensions.** Widgets run in a separate process/bundle that the OS loads independently. No `UIHostingController` in the main app process can produce widget UI. This is an OS architectural constraint and cannot be solved by any SwiftUI bridge.
- **SwiftData `@Model`, Swift Charts `Chart`, Observation `@Observable`.** Macro-driven declarative DSLs with no runtime representation.

**Fallback pattern for unsupported cases:** a hand-written Swift wrapper source file can shim a problematic API into a concrete, bridgeable shape (e.g., wrap a View modifier in a concrete host View, or convert `Binding<Struct>` into a JSON-encoded callback). The generator then binds the shim as if it were any other Swift source. This is the *product-shaped* path — it works but requires per-framework Swift engineering, not just a flag flip.

---

## Existing portfolio audit

Eight packages currently live in `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/`. Four of them (**AuthenticationServices**, **CoreSpotlight**, **SoundAnalysis**, **StoreKit2**) ship with collision-avoidance namespaces (`ASServices`, `SwiftCoreSpotlight`, `SwiftSoundAnalysis`, `StoreKit2`) because Microsoft already binds those frameworks under the same name. Collision namespaces are a *red flag*: they signal duplication unless the Swift-only surface is materially different.

| Package | In macios? | Swift-only gap | Verdict |
|---|---|---|---|
| **AuthenticationServices** | Yes, full (~80 interfaces, full passkey + async) | None material. iOS 17 passkey `RequestStyle` is bound. | **DROP** |
| **CoreSpotlight** | Yes, full (18 interfaces, iOS 16 `CSUserQuery` bound) | None. ObjC-native framework. | **DROP** |
| **SoundAnalysis** | Yes, full (12 interfaces, ObjC-friendly by design) | None. | **DROP** |
| **StoreKit2** | **Minimal.** `SwiftAPI.cs` exposes exactly one method: `AppStore.RequestReview()`. Maintainer (rolfbjarne) stated in macios#16893 that Swift-only APIs are unsupported. | **Essentially the entire StoreKit 2 surface**: `Product`, `Transaction`, `VerificationResult<T>`, `AppStore.sync()`, `showManageSubscriptions`, `Transaction.updates` async sequence, `Transaction.currentEntitlements`, `AppTransaction.shared`, `StoreKit.Message`, external purchase APIs. | **KEEP (flagship)** |
| **CryptoKit** | No | Entire framework | **KEEP** |
| **TipKit** | No | Entire framework | **KEEP** |
| **WeatherKit** | No | Entire framework | **KEEP** |

### Drops

**AuthenticationServices, CoreSpotlight, SoundAnalysis** — drop from the published NuGet portfolio. Microsoft's bindings are complete and actively maintained. Shipping parallel packages forces consumers to disambiguate with `extern alias`, creates ongoing maintenance burden, and provides zero new capability.

Soft-landing option: leave them in `build/validation-libraries.json` as compile-gate targets so the generator keeps exercising those surfaces, but do not publish NuGet packages.

### Keeps

- **StoreKit 2** is the single highest-value binding in the portfolio. StoreKit 1 is deprecated and broken on iOS 18 (macios#21565). There is no other path to correct in-app purchase on .NET MAUI. Document this package prominently in README and docs; it is the headline reason the project exists for many users.
- **CryptoKit, TipKit, WeatherKit** — clean Swift-only wins. No macios overlap, no collision, no caveats. Continue shipping.

---

## New framework candidates — Tier A (ship as pure bindings)

These frameworks pass both scope rules cleanly and should be added to the ship queue.

### 1. MusicKit (iOS 15+)

The modern Swift-only `import MusicKit` — **not** the legacy ObjC `MediaPlayer` framework. Apple Music catalog search, library access, playback via `ApplicationMusicPlayer`, subscription offers.

- **Macios status**: Not bound. Issue #17894 open since 2023-03, marked "Future" by maintainer.
- **SwiftUI coupling**: None in the model/playback layer. Artwork rendering has SwiftUI helpers that are ancillary, not required.
- **Demand**: Strong. Longest-open Swift-only request in macios. Broad consumer audience (music apps are a top-tier MAUI use case).
- **Feasibility**: Clean. Designed as a Swift model layer with async/await.

### 2. WorkoutKit (iOS 17+)

Create, schedule, and sync custom workouts (`CustomWorkout`, `IntervalBlock`, `WorkoutStep`, `WorkoutPlan`) to the Apple Workout app on watchOS.

- **Macios status**: Not bound.
- **SwiftUI coupling**: None. All plain Swift value types. `presentPreview` is an out-of-process system UI call that the consumer triggers via an async method — no custom SwiftUI view hosting required.
- **Demand**: Moderate. Natural pairing with HealthKit (which macios binds). Fitness and health apps are a real category on MAUI.
- **Feasibility**: Extremely clean. Codex verified the iOS 26.2 SDK interface — no hidden SwiftUI dependencies, no `@MainActor` traps that break C# consumption.

### 3. RoomPlan (iOS 16+ / iPadOS 16+)

LiDAR/camera-based 3D room scanning producing parametric USDZ models (walls, doors, windows, furniture, dimensions).

- **Macios status**: Not bound.
- **SwiftUI coupling**: None. `RoomCaptureSession` + delegate flow is fully programmatic. `RoomCaptureView` is UIKit-based and can be hosted natively in MAUI.
- **Demand**: Moderate. Unique capability — no alternatives for AR/interior-design/measurement apps.
- **Feasibility**: Clean. Requires LiDAR-equipped device, which is a consumer limitation, not a binding limitation.

### 4. ProximityReader (iOS 17+) — Tap to Pay on iPhone

Contactless card/wallet reading via iPhone NFC, a.k.a. "Tap to Pay on iPhone" for merchant/POS apps.

- **Macios status**: Not bound.
- **SwiftUI coupling**: None. Plain async session + delegate flow.
- **Demand**: Narrow but high-value per customer. Entitlement-gated (PSP + Apple approval + regional availability), so the addressable market is small, but each customer is a serious merchant-payments use case with no alternative path.
- **Feasibility**: Clean. Defer until first customer request surfaces to justify the binding work.

### 5. LiveCommunicationKit (iOS 17.4+)

VoIP + default-dialer integration. `ConversationManager` is a plain final class with async methods and a delegate surface, replacing parts of the older CallKit flow for modern voice/video apps.

- **Macios status**: Not bound.
- **SwiftUI coupling**: None.
- **Demand**: Narrow. VoIP and "be the default phone app" scenarios only.
- **Feasibility**: Clean. Defer alongside ProximityReader.

---

## New framework candidates — Tier B (defer or drop)

### FinanceKit (iOS 17.4+) — DEFER

Programmatic access to Wallet/Apple Card/Apple Cash transaction history.

- **Macios status**: Not bound.
- **Blocker**: Distribution entitlement is org-only and requires manual Apple approval. Launch partners are three apps (YNAB, Monarch, Copilot). Query APIs lean on Swift `Predicate` and `SortDescriptor` patterns that may not bind cleanly.
- **Verdict**: Defer until a specific customer asks. Not worth building speculative.

### GroupActivities / SharePlay (iOS 15+) — SKIP

Co-watch / co-play sessions across FaceTime.

- **Macios status**: Not bound. Issue #15597 open since 2022-07, "Future" milestone.
- **Blocker**: Protocol-oriented API that expects the consumer to define custom `GroupActivity` types. Similar to AppIntents — the consumer is supposed to write app-specific Swift types conforming to the protocol. Weak demand signal (1 issue, 0 thumbs, 2 comments).
- **Verdict**: Skip. Revisit only if demand surfaces.

### Translation framework (iOS 17.4+/18) — product-shaped, feasible now

On-device language translation API.

- **Macios status**: Not bound.
- **Shape**: `TranslationSession` has no public initializer on iOS 17/18. It is vended exclusively from the `.translationTask(source:target:action:)` SwiftUI view modifier, with session lifetime tied to the host view. Apple explicitly designed it this way because the session may need to present UI for offline model downloads.
- **Path**: Hand-written Swift wrapper. Create a hidden `Color.clear` host View with `.translationTask` applied, capture the `TranslationSession` via the closure into a wrapper class, expose `translate(string) async throws -> String` as a `@_cdecl` function. The generator binds the wrapper like any other Swift library. Small shim, clean C# surface.
- **Revisit trigger**: iOS 26 adds `TranslationSession(installedSource:target:)` — a direct initializer. When `net10.0-ios26` becomes the target baseline, Translation simplifies from product-shaped to pure binding.
- **Verdict**: Feasible as a product-shaped offering now; deprioritized behind Tier A pure bindings but promoted from "Skip" to "Ship when ready".

### JournalingSuggestions (iOS 17.2+) — feasible, deprioritized

The public API is a SwiftUI picker plus a rich content struct.

- `JournalingSuggestionsPicker<Label> : View where Label : View` with `@ViewBuilder label: () -> Label`. Generic + `@ViewBuilder` → **bridge skip reason `SwiftUIConstraint`**. The `Label == Text` specialization exists but the bridge does not handle generic specializations.
- `.journalingSuggestionsPicker(isPresented:onCompletion:)` is a View modifier extension → not bridgeable.
- `onCompletion` receives a `JournalingSuggestion` struct with nested typed assets (`Workout`, `Contact`, `Location`, `Song`, `Photo`, `MotionActivity`, etc.) accessed via `content<T: JournalingSuggestionAsset>(forType:)` — a typed dynamic content bag that doesn't marshal cleanly.

**Path**: Hand-written Swift wrapper using `JournalingSuggestionsPicker<Text>` internally, exposing a fixed JSON schema for the content types the consumer cares about. Feasible but non-trivial Swift engineering.

**Verdict**: Deprioritized. The Journal app is iOS-only and unavailable on iPad/simulator, so the addressable surface is narrow. Ship only if a specific customer asks.

---

## Product-shaped offerings (not pure bindings)

Two frameworks have substantial runtime surface worth exposing but fail the "pure binding" rule because they require either (a) consumer-written Swift source, or (b) SwiftUI hosting. They are recorded here as *product* decisions: ship only if we are willing to expand scope beyond what the generator emits.

### ActivityKit (iOS 16.1+) — product-shaped

Live Activities and Dynamic Island. Strongest demand signal in the entire candidate set: Microsoft currently ships a hand-rolled Swift XCFramework bridge as their *official* MAUI Live Activities sample (`platformintegration-live-activity` on Microsoft Learn). A proper NuGet would replace that sample.

**Why not a pure binding.** Two structural obstacles make a direct `ActivityKit → C#` binding impossible:

1. **`ActivityAttributes` is a Swift protocol with an `associatedtype ContentState`.** `Activity<Attributes>` is generic over this protocol. The generator does not (and cannot in a useful way) emit C# types that conform to a Swift protocol with an associated type — the Swift compiler instantiates these per-consumer at compile time.
2. **Live Activities require a WidgetKit widget extension** registered with `ActivityConfiguration` to render the UI. Widget extensions are SwiftUI-only and live in a separate framework that cannot be bound for MAUI consumption.

**Viable path — Option B (Swift wrapper bridge).** Ship `SwiftBindings.ActivityKit` as a Swift wrapper bridge containing:

- A pre-baked generic `ActivityAttributes` conformance carrying a JSON-shaped or dictionary-shaped `ContentState` (the consumer does not define a type; they pass data as a serialized payload).
- C# wrapper methods: `RequestActivity(attributesJson, stateJson, pushType)`, `UpdateActivity(id, stateJson, alertConfig?, timestamp?)`, `EndActivity(id, finalStateJson?, dismissalPolicy)`, plus async streams for `ActivityUpdates`, `ContentUpdates`, `PushTokenUpdates`, `ActivityStateUpdates`.
- C# binding for `ActivityAuthorizationInfo` (fully programmatic, no caveats).

**What the consumer still has to do.** Add a SwiftUI widget extension to their MAUI app that declares an `ActivityConfiguration` referencing our pre-baked generic attributes type. This is unavoidable — Apple requires the widget extension for the OS to render anything. Microsoft's current sample does this manually. Our package would document the extension template and ship it as a starter file.

**Option C (full product).** Ship template SwiftUI widget views as part of the package so consumers get default layouts without writing any SwiftUI. Rejected for initial scope — too large, template layouts would not satisfy real design requirements anyway. Revisit if the user base grows.

**Verdict**: Ship as Option B. Queue after Tier A pure bindings — do not let ActivityKit scope creep block MusicKit/WorkoutKit/RoomPlan.

### FamilyControls + DeviceActivity + ManagedSettings (iOS 15/16+) — conditionally bindable via bridge enhancement

Screen Time APIs. Only path on iOS to building parental-control apps. Would unlock an entire app category for MAUI.

**Shape.** Verified directly against the iOS 26.2 SDK Swift interface at `iPhoneOS26.2.sdk/System/Library/Frameworks/FamilyControls.framework/Modules/FamilyControls.swiftmodule/arm64e-apple-ios.swiftinterface`:

- `FamilyActivityPicker` is declared as `public struct FamilyActivityPicker : SwiftUICore.View`. It is the only API that produces `FamilyActivitySelection` tokens (which are opaque and cannot be fabricated programmatically).
- The constructor is `init(headerText: String? = nil, footerText: String? = nil, selection: Binding<FamilyActivitySelection>)`.
- The picker is **non-generic** and **concrete** — in principle it is a candidate for auto-bridging by `SwiftUIBridgeEmitter`.
- There is no UIKit `UIViewController` path anywhere in the framework.

**Current blocker.** The SwiftUI bridge does not yet support `Binding<Struct>` (non-optional). Per `SwiftUIBridgeEmitter.InitAnalyzer.cs:733`, this is explicitly deferred pending two-way lifetime management. `FamilyActivitySelection` is a `Codable, Equatable` struct containing `Set<ApplicationToken>`, `Set<ActivityCategoryToken>`, `Set<WebDomainToken>`. Today, the bridge falls back to template for any View with a `Binding<Struct>` param.

**Two paths forward:**

- **(a) Extend the bridge** to support `Binding<Codable Struct>`. The inner struct would be serialized to JSON across the ABI boundary on update, and decoded on the C# side. One-time generator investment (~1–2 days); reusable for any other Apple or third-party framework that exposes state via `Binding<Struct>` — which is a common SwiftUI pattern. After this lands, `FamilyActivityPicker` becomes a pure auto-bridged binding with no hand-written Swift.
- **(b) Hand-written Swift wrapper.** Write a concrete wrapper View that holds `@State var selection: FamilyActivitySelection` internally and uses `FamilyActivityPicker(selection: $selection)`, with a JSON-encoded callback on `onChange(of: selection)`. Faster for this one framework, does not generalize.

**Rest of the bundle.** The programmatic surface (`AuthorizationCenter.requestAuthorization`, `ManagedSettingsStore`, `DeviceActivityMonitor`, shields, schedules) is fully bindable without SwiftUI involvement. Bind that as a standard Swift library.

**Verdict**: Promote from "deprioritized product" to "Tier B — ship after bridge enhancement, or alongside it." The `Binding<Codable Struct>` enhancement is a strategic investment worth making independently, and FamilyControls is the first real-world driver for it.

---

## Hard skips

Document these publicly (FAQ / wiki) so users stop asking.

### AppIntents (iOS 16+) — un-bindable

Highest-demand framework that we cannot bind. Both Grok and Codex confirmed independently:

- `AppIntent` is a Swift protocol that can only be conformed to by Swift structs. `@objc` cannot be applied to Swift structs, so there is no bridging path.
- The framework's metadata is extracted at **build time** by `appintentsmetadataprocessor`, which scans the consumer's Swift source code and embeds static metadata into the app binary. Siri, Shortcuts, and Spotlight read this static metadata at OS level *without running your app*.
- The only workaround documented in the wild (Expo, Capacitor, Flutter) is: the consumer writes a tiny Swift file containing the intent alongside their other code, then bridges runtime parameter updates back via shared `UserDefaults` or app groups.
- Since any C# consumer would still have to write Swift source to define the intent, a C# binding of the protocol adds zero value.

**There is no path forward without Apple changing the framework design.** Document this as the #1 FAQ item.

### DockKit (iOS 17+) — niche hardware

Motorized phone-stand tracking (Insta360 Flow, Belkin Auto-Tracking). Addressable market is tiny. SKIP unless a specific customer asks.

### SwiftData, Observation, Swift Charts, WidgetKit, SwiftUI — not bindable

Macro-driven or SwiftUI-bound. Not consumable from C# regardless of binding effort. SKIP.

---

## Decision summary (master table)

| Framework | Category | Verdict | Notes |
|---|---|---|---|
| StoreKit 2 | Existing | **KEEP (flagship)** | macios binds 1 method; we bind the rest |
| CryptoKit | Existing | KEEP | Swift-only |
| TipKit | Existing | KEEP | Swift-only |
| WeatherKit | Existing | KEEP | Swift-only |
| AuthenticationServices | Existing | **DROP** | macios has full coverage |
| CoreSpotlight | Existing | **DROP** | macios has full coverage |
| SoundAnalysis | Existing | **DROP** | macios has full coverage |
| MusicKit (Swift) | New | **SHIP — Tier A** | Longest-open macios request |
| WorkoutKit | New | **SHIP — Tier A** | Clean, pairs with HealthKit |
| RoomPlan | New | **SHIP — Tier A** | Unique AR capability |
| ProximityReader | New | Ship on demand | Tap-to-Pay |
| LiveCommunicationKit | New | Ship on demand | VoIP niche |
| ActivityKit | New | **SHIP — product-shaped (Option B)** | Swift wrapper + consumer writes widget extension (widget cannot be bridged) |
| FamilyControls bundle | New | **Tier B** — ship after `Binding<Codable Struct>` bridge enhancement | First real driver for the bridge enhancement; reusable beyond this framework |
| Translation | New | Ship when ready — product-shaped | Small hand-written Swift shim; simplifies to pure binding on iOS 26 |
| JournalingSuggestions | New | Deprioritized — feasible on demand | Generic+`@ViewBuilder` picker; hand-written shim required; iPhone-only |
| FinanceKit | New | Defer | Entitlement-gated, tiny market |
| GroupActivities | New | Skip | Protocol-shaped, weak demand |
| AppIntents | New | **Skip — un-bindable** | Swift macros + compile-time metadata (unrelated to SwiftUI) |
| DockKit | New | Skip | Niche hardware |

---

## Ship order

Existing cleanup, do first:

0a. **Drop** AuthenticationServices, CoreSpotlight, SoundAnalysis NuGet packages. Leave in `build/validation-libraries.json` as compile-gate targets.

0b. **Flagship-ify** StoreKit 2 documentation — it is the strongest durable reason the project exists, and should be front-and-center in README, wiki, and NuGet descriptions.

Pure bindings (Tier A), ordered by impact × feasibility:

1. **MusicKit** — highest confidence clean binding, broadest audience, longest-open macios request.
2. **WorkoutKit** — clean, pairs with macios HealthKit, natural fitness/health story.
3. **RoomPlan** — unique AR capability, clean binding, good differentiation.
4. **ProximityReader** — ship when a specific customer asks.
5. **LiveCommunicationKit** — ship when a specific customer asks.

Strategic generator investment (unblocks multiple frameworks):

6. **`Binding<Codable Struct>` bridge enhancement.** One-time work in `SwiftUIBridgeEmitter.InitAnalyzer.cs`. Unblocks FamilyControls as a pure binding. Reusable for any future Apple or third-party framework that exposes state via `Binding<Struct>` — which is a very common SwiftUI pattern. Worth doing independently of any specific framework.

Product-shaped offerings (hand-written Swift shim + generator binding of the shim):

7. **ActivityKit** (Option B Swift wrapper bridge) — highest overall demand signal, but the widget extension still has to be written by the consumer in SwiftUI. Scope the Swift wrapper carefully.
8. **FamilyControls bundle** — ships as a pure binding after step 6 (bridge enhancement) lands. Before that, a hand-written shim is the alternative path.
9. **Translation** — small hand-written `Color.clear` host view wrapper with `.translationTask` applied, captures `TranslationSession`, exposes a C# async translate API. Simplifies to pure binding when iOS 26 baseline kicks in.
10. **JournalingSuggestions** — only if demand surfaces. Hand-written wrapper + JSON schema for content types.

---

## Research sources

Decisions in this doc were cross-validated against multiple independent sources:

- **Research agent** (general-purpose, web + GitHub): verified macios coverage for all candidates; identified StoreKit 2 as the flagship gap; confirmed ActivityKit demand via Microsoft's own MAUI sample.
- **Grok** (external, via user): confirmed AppIntents un-bindability; initially claimed FamilyControls had a UIKit path (incorrect); suggested RoomPlan addition.
- **Codex** (external, via user): verified FamilyControls SwiftUI-only against iOS 26.2 SDK interface files directly; flagged ActivityKit product-shape concern; recommended priority reorder.
- **Direct SDK inspection** (this doc): verified FamilyControls `FamilyActivityPicker : SwiftUICore.View` in `iPhoneOS26.2.sdk/System/Library/Frameworks/FamilyControls.framework/Modules/FamilyControls.swiftmodule/arm64e-apple-ios.swiftinterface`; verified ActivityKit `protocol ActivityAttributes` with `associatedtype ContentState` in the equivalent ActivityKit module interface.
- **dotnet/macios source**: `src/frameworks.sources`, `src/storekit.cs`, `src/StoreKit/SwiftAPI.cs`, `src/authenticationservices.cs`, `src/corespotlight.cs`, `src/soundanalysis.cs`.

Key issues tracked:

- [dotnet/macios#16893](https://github.com/dotnet/macios/issues/16893) — maintainer confirms StoreKit 2 is Swift-only, unsupported in macios.
- [dotnet/macios#21565](https://github.com/dotnet/macios/issues/21565) — StoreKit 1 broken on iOS 18.
- [dotnet/macios#17894](https://github.com/dotnet/macios/issues/17894) — MusicKit binding request, open since 2023.
- [dotnet/macios#15597](https://github.com/dotnet/macios/issues/15597) — GroupActivities binding request, open since 2022.
- [dotnet/maui#19130](https://github.com/dotnet/maui/discussions/19130) — AppIntents discussion (consensus: no path forward).

---

## Revisit triggers

This plan should be reopened when any of the following happen:

- **iOS 26 becomes the baseline target.** Translation framework simplifies from product-shaped to pure binding via the `TranslationSession(installedSource:target:)` direct initializer.
- **`Binding<Codable Struct>` ships in the SwiftUI bridge.** FamilyControls moves from product-shaped to pure binding automatically. Re-scan the SDK for other framework Views that were previously skipped due to `Binding<Struct>` params — they become candidates too.
- **A specific customer asks for ProximityReader or LiveCommunicationKit.** Both are ready to ship on demand, no further research needed.
- **ActivityKit Option B ships and demand holds.** Consider whether Option C (pre-built widget extension templates) is worth the additional scope.
- **Microsoft publishes StoreKit 2 bindings in macios.** Our flagship loses its reason to exist. Evaluate whether to maintain or deprecate.
- **A new iOS release adds a Swift-only framework.** Re-scan Apple's WWDC framework list against the Scope Rules above.

---

## What this doc does not cover

- **How to bind an Apple framework.** See `Future/apple-framework-binding-strategy.md` for the generator pipeline, wrapper compilation, resolver behavior, and NuGet layout.
- **Individual package status / release history.** See [`0.8.0-ship-plan.md`](0.8.0-ship-plan.md) and future release docs.
- **Open generator bugs blocking specific frameworks.** See `roadmap.md`.

---

## Tier A status — 2026-04-11 (append-only)

All five Tier A pure bindings now compile, pack, and run against the iOS 26.2 Simulator under `SwiftBindings.Sdk/0.8.9`. Sim test projects live at `swift-dotnet-packages/tests/<Framework>.SimTests/` and exercise real binding surface (metadata loads, enum value round-trips, `@_cdecl` extension calls). Totals below are runtime-test assertions, not unit tests.

| Framework | SDK | Sim test assertions | Notes |
|---|---|---|---|
| MusicKit | 0.8.9 | 9/9 | Metadata loads for `MusicAuthorization`, `Artwork` (class-constrained existential) and `MusicSubscription`; `MusicAuthorization.CurrentStatus`; `AudioVariant.AllCases`; `AudioVariant.GetDescription` on `DolbyAtmos` and the `#available`-guarded `SpatialAudio`; plain-enum `ContentRating` values. |
| WorkoutKit | 0.8.9 | 13/13 | Metadata for `HeartRateRangeAlert`, `HeartRateZoneAlert`, `IntervalStep`, `CustomWorkout`, `CadenceRangeAlert`, `PacerWorkout`, `SingleGoalWorkout`, `WorkoutPlan`, `IntervalBlock`, `SwimBikeRunWorkout`, `ScheduledWorkoutPlan`; `StateError` and `WorkoutAlertMetric` enum values. |
| RoomPlan | 0.8.9 | 13/13 | Metadata for `CapturedElementCategory`, `CapturedRoomData`, `CapturedStructure`, `CapturedRoom` + nested `Section`/`Surface`/`Object`/`USDExportOptions`, `RoomBuilder.ConfigurationOptions`; `RoomCaptureSession.CaptureError` + `Instruction` values; real `@_cdecl` round-trip via `RoomCaptureSessionCaptureErrorExtensions.GetErrorDescription`; `CapturedRoom.Surface.Edge.AllCases`. |
| ProximityReader | 0.8.9 | 10/10 | Metadata for `PaymentCardReadResult`, both `StoreAndForwardBatch*` types, `StoreAndForwardStatus`, `PaymentCardTransactionRequest`, `PaymentCardVerificationRequest`, `VASRequest`/`VASReadResult`, `MobileDocumentAnyOfDataRequest`; `MobileDocumentReaderError` values. `MobileDocumentReaderError.GetErrorDescription` is intentionally omitted — the C# extension emits a P/Invoke to `SBW_ProximityReader_MobileDocumentReaderError_get_errorDescription_*` but the Swift wrapper never emits the matching `@_cdecl`, so the call would throw `EntryPointNotFoundException`. Tracked as a generator emitter-asymmetry bug (re-enable test when fixed). |
| LiveCommunicationKit | 0.8.9 | 18/18 | Class-metadata fallback path exercised via `Conversation` + nested `Event`/`Update`/`Capabilities`; struct metadata for all 11 `*ConversationAction` wrappers (`Start`, `StartCellular`, `Join`, `End`, `Merge`, `Unmerge`, `Mute`, `Pause`, `PlayTone`, `SetTranslating`, generic `ConversationAction`); `Handle`, `CellularService`; `SetTranslatingAction.TranslationEngine` values. `SupportedOSPlatformVersion=26.0` required for iOS 18.4+/26.0 types. |

Aggregate: **63 / 63 sim-test assertions passing** across all Tier A frameworks on the iOS 26.2 simulator. Regression gates (`nuke test`, `nuke validate`, `nuke binding-tests`) all green against the published SDK 0.8.9.

### Generator bugs surfaced and fixed while shipping Tier A

- **Native SIGSEGV in `ModuleInitializer` for generic Swift classes.** `SwiftObjectHelper<MusicKit.MusicRelationshipProperty<Album, RecordLabel>>.GetTypeMetadata()` crashed in Swift's `swift_initClassMetadataImpl` during module init — not catchable by C# `try/catch` because the failure is a native signal, not a managed exception. Fix: `ModuleHandler.cs` now skips the eager metadata pre-load for any generated type whose name contains `<`, keeping only the factory registration. On-demand `GetTypeMetadata()` at call time works fine because the Swift class is fully initialized by then. Shipped in 0.8.9. Caught by MusicKit sim tests; no unit test would have found it.

### Generator bug still open

- **`MobileDocumentReaderError.errorDescription` Swift wrapper missing.** C# side emits the `errorDescription` extension + P/Invoke, Swift side never emits the matching `@_cdecl`. Sister enums (`MusicKit.AudioVariant`, `RoomPlan.RoomCaptureSession.CaptureError`) work, so it's an emitter asymmetry — hypothesis is that the `LocalizedError` inherited-member emission path differs between top-level plain enums and nested enums, but the exact gap still needs to be located. Test is disabled with an explanatory comment at `ProximityReader.SimTests/Program.cs`.
