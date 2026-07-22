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
- **`Binding<BoundType>` and non-Codable `Binding<BoundStruct>` (non-optional).** Deferred — need more complex two-way lifetime management (one-time generator enhancement, not a fundamental limit). `Binding<CodableStruct>` for non-frozen, non-generic Codable structs is already implemented in the SwiftUI bridge.
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

### VisualIntelligence (iOS 26)

Visual-search / "find similar" integration surfaced entirely through App Intents — the framework's only public type, `SemanticContentDescriptor`, feeds `@AppIntent`-tied entry points. Same macro-lock as AppIntents: a C# consumer would still have to author the Swift intent, so a binding adds nothing.

### DockKit (iOS 17+)

Motorized phone-stand tracking. Addressable market is too small to justify the binding work. Revisit only on customer ask.

---

## Deferred candidates

### FinanceKit (iOS 17.4+)

Programmatic access to Wallet/Apple Card/Apple Cash transaction history.

- **Macios status**: Not bound.
- **Blocker**: Distribution entitlement is org-only and requires manual Apple approval. Launch partners are three apps (YNAB, Monarch, Copilot). Query APIs lean on Swift `Predicate` and `SortDescriptor` patterns that may not bind cleanly.
- **iOS 26 note (May 2026 scan)**: the `FinanceStore` query/history surface is Swift-only and not macro-gated, so it is *technically* bindable now (iOS 26 also adds background delivery). The blocker is unchanged: the org-only entitlement still gates real use, so this stays defer-until-ask — the iOS 26 state just makes the binding more feasible once a customer is on the hook for the entitlement.
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

Feasible via a hand-written Swift wrapper using `JournalingSuggestionsPicker<Text>` internally and a fixed JSON schema for content types. The shipped hosting bridge now absorbs the concrete-`View` half of that wrapper (it auto-wraps a `JournalingSuggestionsPicker<Text>` shim in a `UIHostingController` with a typed `ViewController` accessor), so the remaining work is just the content-bag marshalling. Still deprioritized for the same reason: the Journal app is iPhone-only and unavailable on iPad/simulator, so there is no automated end-to-end gate.

---

## iOS 26 framework scan (May 2026)

Systematic pass against the iOS 26.2 SDK (Xcode 26.3). Grounded in `.swiftinterface` presence (a framework is Swift-only when it ships a `.swiftinterface` but no ObjC `Headers/`) plus a shape-read of each candidate's interface, cross-checked against two independent LLM reviews. Recorded so the scan is not repeated; the no's are as load-bearing as the yes's.

### FoundationModels (iOS 26) — flagship candidate

On-device LLM (Apple Intelligence). Swift-only; not bound by `dotnet/macios`.

- **C#-reachable surface (real gap, high demand):** `SystemLanguageModel` availability, a `LanguageModelSession` created from string `Instructions`, `respond(to: String)` → `Response<String>`, `streamResponse(to: String)` → `ResponseStream<String>` (an `AsyncSequence`), and `GenerationOptions`. Verified by interface read: 9 of the 27 `respond`/`streamResponse` overloads are the plain-`String` path with no macro dependency.
- **Out of reach (Swift-source-locked):** `@Generable` / `@Guide` structured output, custom `Tool` conformances, and the `@PromptBuilder` / `@InstructionsBuilder` result-builder forms. A C# consumer cannot define a `@Generable` type or conform a `Tool` — same wall as AppIntents.
- **Verdict:** strongest candidate in this scan, *product-shaped / partial*. The text-generation + streaming subset is the highest-value Apple-framework gap the portfolio currently lacks and needs no consumer Swift authoring. Binding work: string constructors for `Prompt`/`Instructions`, `Response<String>` projection, and `AsyncSequence` streaming. Structured generation ships as a documented limitation, not a blocker on the whole framework.

### Other new candidates

| Framework | macios | Shape | Verdict |
|---|---|---|---|
| **DeclaredAgeRange** (iOS 26) | not bound | pure binding | Candidate. Clean async `AgeRangeService` request (takes a host `UIViewController`); relevant to App Store age-signal compliance. No macros/Views. |
| **TabularData** (iOS 16+) | not bound | pure binding | Low-priority candidate. Large Swift-only DataFrame / CSV / JSON surface with zero macios coverage — but generic and `@dynamicMemberLookup`-heavy (projection difficulty), and C# already has DataFrame libraries (demand question). Real gap, weak pull. |
| **PermissionKit** (iOS 26) | not bound | pure binding | Niche candidate. Communication-consent / parental-approval (`AskCenter`, `PermissionQuestion`, async flows); narrow audience. |
| **PaperKit** (iOS 26) | not bound | pure binding | Candidate. UIKit markup view controllers (`PaperMarkupViewController`) + `PaperMarkup` model + delegate; PencilKit-adjacent drawing/annotation. |

### Swift-only subset only — verify macios before investing

ObjC-rooted frameworks where macios very likely already binds the `@objc` surface; only the Swift-only addition is a potential gap, so confirm coverage first.

- **ImagePlayground** — the `@objc ImagePlaygroundViewController` is macios territory; the Swift-only `ImageCreator` (programmatic Genmoji / image generation, no UI) plus its value types is the gap. Pure binding for `ImageCreator` only.
- **AlarmKit** — product-shaped. macios likely binds the ObjC surface; the Swift value-type `AlarmConfiguration` + ActivityKit-style presentation, plus the required `AppIntent` stop/repeat button actions, need a Swift shim (same shape as ActivityKit). Verify macios Swift coverage before sizing.

### Bindable but gated — defer until a customer asks

Technically clean Swift surfaces blocked by entitlement / hardware / narrow audience, not by binding feasibility — note them, don't invest pre-emptively:

- **WiFiAware** (P2P Wi-Fi; entitlement-gated; macios ships only the Network error domain), **SecureElementCredential** (NFC, entitlement), **EnergyKit** (grid/EV scheduling, niche), **CarKey** (digital car keys, entitlement), **TelephonyMessagingKit** (RCS / carrier), **GeoToolbox** (tiny place-descriptor API), **CreateML / CreateMLComponents** (training-focused; CoreML inference is already covered), **ManagedAppDistribution / AutomatedDeviceEnrollment** (MDM / enterprise).

### Confirmed no-change

No shipped framework was obsoleted by new macios Swift coverage. No hard-skip was relaxed by iOS 26 — if anything FoundationModels reinforces the macro pattern for AI surfaces. `VisualIntelligence` is added to Hard skips above (AppIntents-coupled).

---

## Revisit triggers

Reopen this doc when:

- **A new iOS release adds a Swift-only framework.** Re-scan Apple's WWDC framework list against the Scope Rules above.
- **`Binding<CodableStruct>` already ships in the SwiftUI bridge.** Re-scan the SDK for framework Views previously skipped due to non-Codable `Binding<BoundStruct>` / `Binding<BoundType>` params when those remaining Binding shapes land; for Codable-struct Views, re-evaluate pure-binding conversion product decisions (capability is no longer the blocker).
- **A specific customer asks for a deferred candidate.** Move it from Deferred to a sized binding task.
- **Microsoft publishes Swift-only bindings in macios** for a framework we ship. Evaluate whether to maintain or deprecate.
- **iOS makes a deferred candidate's API simpler.** E.g., a previously product-shaped framework gets a public initializer that removes the SwiftUI-modifier-only access pattern.

---

## Decision precedents

Recorded for "why we said no" continuity. If a future evaluator wants to revisit, they should read these first.

- **AuthenticationServices, CoreSpotlight, SoundAnalysis** — dropped from the published portfolio. Microsoft's macios bindings are complete and actively maintained. Shipping parallel packages forced consumers to disambiguate with `extern alias`, created ongoing maintenance burden, and provided zero new capability. A soft-landing that kept them in `build/validation-libraries.json` as compile-gate targets was planned so the generator would keep exercising those surfaces; that soft-landing lapsed — none of the three is in the validation-libraries list today, and they are not re-added.
- **StoreKit 2** — flagship binding. macios binds exactly one method (`AppStore.RequestReview`); the rest of the StoreKit 2 surface (`Product`, `Transaction`, `VerificationResult<T>`, async sequences, `AppTransaction.shared`, external purchase APIs) only exists here. StoreKit 1 is deprecated and broken on iOS 18 (macios#21565), so this is the only path to correct in-app purchase on .NET MAUI. Published package identity uses the StoreKit2 naming (`SwiftBindings.Apple.StoreKit2`).
- **ActivityKit** — shipped as a *product-shaped* offering, not a pure binding. `ActivityAttributes` is a Swift protocol with an `associatedtype ContentState`, and Live Activities require a SwiftUI widget extension that runs in a separate process. The binding ships a pre-baked generic `ActivityAttributes` conformance carrying JSON-shaped state plus C# wrapper methods; the consumer still has to write a SwiftUI widget extension declaring `ActivityConfiguration`. This is unavoidable — the OS requires the widget extension to render anything.
- **ProximityReader, LiveCommunicationKit** — shipped tier-1 pure bindings (also listed in the README pre-built portfolio). Entitlement-gated (ProximityReader) or narrow-audience (VoIP / default-dialer apps), so consumer uptake is expected to be small — but both bindings shipped cleanly; nothing in the binding path was blocked.
- **FamilyControls bundle** — `FamilyActivityPicker` is `public struct FamilyActivityPicker : SwiftUICore.View` and is the only API that produces `FamilyActivitySelection` tokens. There is no UIKit path. Currently bound via a hand-written Swift wrapper (concrete View holding `@State var selection`). `Binding<CodableStruct>` already exists in the SwiftUI bridge; pure-binding conversion of the FamilyControls picker shim is a product decision, not blocked on a missing bridge capability. The programmatic surface (`AuthorizationCenter`, `ManagedSettingsStore`, `DeviceActivityMonitor`, shields, schedules) is a standard binding.
- **Translation** — currently product-shaped (hand-written `Color.clear` host view wrapper with `.translationTask` applied, capturing `TranslationSession` into a wrapper class). iOS 26 added `TranslationSession(installedSource:target:)`, and the in-repo Apple TFM baseline is already `net10.0-ios26.2` (validation + packaged Apple tags `apple-v26.2.*`), so the pure-binding conversion is **unblocked**. Whether Translation should convert from product-shaped to pure is an open owner decision — not declared here.

---

## Research sources

When evaluating a new candidate, cross-validate against:

- macios source — `src/frameworks.sources` and the per-framework `.cs` definitions.
- Apple's SDK Swift interface files at `iPhoneOS<N>.sdk/.../<Framework>.framework/Modules/<Framework>.swiftmodule/<arch>.swiftinterface`. These tell you definitively whether an API is SwiftUI-only, generic, or `@MainActor`-bound.
- dotnet/macios issue tracker — search by framework name, look for "Future" milestone tags.
- Two independent LLMs (e.g. Codex + Grok). They reliably catch each other's hallucinations about API shape, especially around SwiftUI vs UIKit.
