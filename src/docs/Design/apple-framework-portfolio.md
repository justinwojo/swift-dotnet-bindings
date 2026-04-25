# Apple Framework Portfolio — Decision Reference

This doc records the decision framework swift-bindings uses to evaluate Apple frameworks for inclusion in the published NuGet portfolio. Use it when a new candidate surfaces (a new iOS release, a customer ask, a Microsoft.iOS gap) — not as a status tracker. Concrete shipping state lives with each package's repo and release notes.

Companion: [`apple-swift-types-architecture.md`](apple-swift-types-architecture.md) covers *how* Swift-only Apple types are bound; this doc covers *which* frameworks are worth binding.

---

## Scope rules

A framework is worth publishing only if **both** are true:

1. **It fills a real gap.** The framework is Swift-only, or has a large Swift-only API surface that `dotnet/macios` does not bind. No duplication of existing Microsoft bindings — if macios already ships it, we don't.
2. **It is consumable from C#.** No macro-locked APIs (e.g. `@AppIntent`). Compile-time metadata extraction pipelines that require the consumer to write Swift source (AppIntents, SwiftData) are out of scope.

When rule 2 is partially violated — the framework has a useful runtime surface but a mandatory SwiftUI or Swift-source touchpoint — the framework becomes a **product-shaped** offering rather than a pure binding (hand-written Swift wrapper + generator binding of the wrapper).

---

## SwiftUI bridging capability

The generator ships an automated SwiftUI bridge (`SwiftUIBridgeEmitter.cs`, `SwiftUIBridgeCollector.cs`, `SwiftUIViewDetector.cs`) that auto-generates a `UIHostingController` wrapper + C# session class for any concrete View struct it encounters. This shapes what "SwiftUI-only" means for binding decisions.

**The bridge auto-handles:**

- Concrete (non-generic) `View` struct conformances
- Constructor parameters: primitives (Int, Bool, Double, Float), String, enums (raw-value), Swift classes
- `Binding<T>` where T is Primitive / String / BoundEnum / `Optional<supported>`
- Closures up to 4 args, `Result<T, E>` callbacks decomposed into onSuccess/onError
- Lifecycle hooks (`onAppear`, `onDisappear`)
- Modal/push presentation helpers (`PresentAsSheet`, `PushOnNav`)

**The bridge does NOT handle (and why):**

- **Generic Views with `View` constraints** (e.g. `JournalingSuggestionsPicker<Label>`). Skip reason: `SwiftUIConstraint`.
- **`@ViewBuilder` parameters.** No representation in C#.
- **View modifier extensions** (`.translationTask(...)`, `.familyActivityPicker(...)`). Modifiers apply to `some View` and return opaque modifier chains.
- **`Binding<Struct>` (non-optional).** Deferred — needs two-way lifetime management (one-time generator enhancement, not a fundamental limit).
- **Widget extensions.** Widgets run in a separate process/bundle the OS loads independently. No `UIHostingController` in the main app process can produce widget UI. OS architectural constraint, not solvable by any SwiftUI bridge.
- **SwiftData `@Model`, Swift Charts `Chart`, Observation `@Observable`.** Macro-driven declarative DSLs with no runtime representation.

**Fallback for unsupported cases:** a hand-written Swift wrapper source file can shim a problematic API into a concrete, bridgeable shape. The generator then binds the shim like any other Swift library. This is the *product-shaped* path — works but requires per-framework Swift engineering.

---

## Hard skips (un-bindable)

Document these in the FAQ / wiki so users stop asking.

### AppIntents (iOS 16+)

Highest-demand framework that cannot be bound:

- `AppIntent` is a Swift protocol that can only be conformed to by Swift structs. `@objc` cannot be applied to Swift structs, so there is no bridging path.
- Framework metadata is extracted at **build time** by `appintentsmetadataprocessor`, which scans the consumer's Swift source code and embeds static metadata into the app binary. Siri, Shortcuts, and Spotlight read this static metadata at OS level *without running your app*.
- The only documented workaround (Expo, Capacitor, Flutter): the consumer writes a tiny Swift file with the intent alongside their other code, then bridges runtime parameter updates back via shared `UserDefaults` or app groups.
- Since any C# consumer would still have to write Swift source to define the intent, a C# binding of the protocol adds zero value.

**No path forward without Apple changing the framework design.**

### SwiftData / Observation / Swift Charts / WidgetKit / SwiftUI declarative DSLs

Macro-driven or SwiftUI-bound. Not consumable from C# regardless of binding effort.

### DockKit (iOS 17+)

Motorized phone-stand tracking. Addressable market is too small to justify the binding work. Revisit only on customer ask.

---

## Deferred candidates

### FinanceKit (iOS 17.4+)

Programmatic access to Wallet/Apple Card/Apple Cash transaction history.

- **Macios status**: Not bound.
- **Blocker**: Distribution entitlement is org-only and requires manual Apple approval. Launch partners are three apps (YNAB, Monarch, Copilot). Query APIs lean on Swift `Predicate` and `SortDescriptor` patterns that may not bind cleanly.
- **Verdict**: Defer until a specific customer asks.

### GroupActivities / SharePlay (iOS 15+)

Co-watch / co-play sessions across FaceTime.

- **Macios status**: Not bound. dotnet/macios#15597 open since 2022, "Future" milestone.
- **Blocker**: Protocol-oriented API that expects the consumer to define custom `GroupActivity` types — same shape problem as AppIntents. Weak demand signal.
- **Verdict**: Skip. Revisit only if demand surfaces.

### JournalingSuggestions (iOS 17.2+)

The public API is a generic SwiftUI picker plus a rich content struct:

- `JournalingSuggestionsPicker<Label> : View where Label : View` with `@ViewBuilder label: () -> Label`. Generic + `@ViewBuilder` → bridge skip reason `SwiftUIConstraint`.
- `.journalingSuggestionsPicker(isPresented:onCompletion:)` is a View modifier extension → not bridgeable.
- `onCompletion` receives a `JournalingSuggestion` struct with nested typed assets accessed via `content<T: JournalingSuggestionAsset>(forType:)` — a typed dynamic content bag that doesn't marshal cleanly.

Feasible via a hand-written Swift wrapper using `JournalingSuggestionsPicker<Text>` internally and a fixed JSON schema for content types. Deprioritized: Journal app is iPhone-only and unavailable on iPad/simulator.

### ProximityReader, LiveCommunicationKit (on-demand pure bindings)

Both ship cleanly when a customer asks. Entitlement-gated (ProximityReader) or narrow-audience (VoIP / default-dialer apps).

---

## Revisit triggers

Reopen this doc when:

- **A new iOS release adds a Swift-only framework.** Re-scan Apple's WWDC framework list against the Scope Rules above.
- **`Binding<Codable Struct>` ships in the SwiftUI bridge.** Re-scan the SDK for framework Views previously skipped due to `Binding<Struct>` params — they become candidates.
- **A specific customer asks for a deferred candidate.** Move it from Deferred to a sized binding task.
- **Microsoft publishes Swift-only bindings in macios** for a framework we ship. Evaluate whether to maintain or deprecate.
- **iOS makes a deferred candidate's API simpler.** E.g., a previously product-shaped framework gets a public initializer that removes the SwiftUI-modifier-only access pattern.

---

## Decision precedents

Recorded for "why we said no" continuity. If a future evaluator wants to revisit, they should read these first.

- **AuthenticationServices, CoreSpotlight, SoundAnalysis** — dropped from the published portfolio. Microsoft's macios bindings are complete and actively maintained. Shipping parallel packages forced consumers to disambiguate with `extern alias`, created ongoing maintenance burden, and provided zero new capability. Soft-landing: kept in `build/validation-libraries.json` as compile-gate targets so the generator keeps exercising those surfaces.
- **StoreKit 2** — flagship binding. macios binds exactly one method (`AppStore.RequestReview`); the rest of the StoreKit 2 surface (`Product`, `Transaction`, `VerificationResult<T>`, async sequences, `AppTransaction.shared`, external purchase APIs) only exists here. StoreKit 1 is deprecated and broken on iOS 18 (macios#21565), so this is the only path to correct in-app purchase on .NET MAUI.
- **ActivityKit** — shipped as a *product-shaped* offering, not a pure binding. `ActivityAttributes` is a Swift protocol with an `associatedtype ContentState`, and Live Activities require a SwiftUI widget extension that runs in a separate process. The binding ships a pre-baked generic `ActivityAttributes` conformance carrying JSON-shaped state plus C# wrapper methods; the consumer still has to write a SwiftUI widget extension declaring `ActivityConfiguration`. This is unavoidable — the OS requires the widget extension to render anything.
- **FamilyControls bundle** — `FamilyActivityPicker` is `public struct FamilyActivityPicker : SwiftUICore.View` and is the only API that produces `FamilyActivitySelection` tokens. There is no UIKit path. Currently bound via a hand-written Swift wrapper (concrete View holding `@State var selection`). Will simplify to a pure binding once `Binding<Codable Struct>` ships in the SwiftUI bridge. The programmatic surface (`AuthorizationCenter`, `ManagedSettingsStore`, `DeviceActivityMonitor`, shields, schedules) is a standard binding.
- **Translation** — currently product-shaped (hand-written `Color.clear` host view wrapper with `.translationTask` applied, capturing `TranslationSession` into a wrapper class). iOS 26 added `TranslationSession(installedSource:target:)`, so this simplifies to a pure binding once `net10.0-ios26` becomes the baseline.

---

## Research sources

When evaluating a new candidate, cross-validate against:

- macios source — `src/frameworks.sources` and the per-framework `.cs` definitions.
- Apple's SDK Swift interface files at `iPhoneOS<N>.sdk/.../<Framework>.framework/Modules/<Framework>.swiftmodule/<arch>.swiftinterface`. These tell you definitively whether an API is SwiftUI-only, generic, or `@MainActor`-bound.
- dotnet/macios issue tracker — search by framework name, look for "Future" milestone tags.
- Two independent LLMs (e.g. Codex + Grok). They reliably catch each other's hallucinations about API shape, especially around SwiftUI vs UIKit.
