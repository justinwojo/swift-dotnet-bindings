# Binding Audit — Synthesis & Index

A correctness / quality / coverage audit of every generated Swift→C# binding shipped from
`swift-dotnet-packages` (third-party libs + Apple frameworks). One markdown file per library in this
folder; this is the index + cross-cutting synthesis. Rubric: [`_METHODOLOGY.md`](_METHODOLOGY.md).

- **Scope**: 26 bindings — 24 Swift-mode (binding-report.json), 2 ObjC-mode (Matter, Stripe3DS2). Stripe
  is a 14-product meta-package counted once in the headline but broken out per-submodule below.
- **Audited at**: `swift-dotnet-packages` HEAD, generated 2026-06-27. Static read-and-reason audit of the
  shipped `swift-binding/*.cs` + the tests that guard it. Not a runtime gate — suspected runtime/ABI bugs
  are flagged for a BindingTests repro, not proven here.
- **Verification**: 6 consequential per-library claims were independently re-checked against raw artifacts
  (abi.json / emitted .cs / .swiftinterface) — all held precisely. The two judgment-heaviest docs
  (AppIntents, Stripe) also got a Codex second opinion (session `019f0abf`), which confirmed every
  load-bearing conclusion and drove two corrections (folded in).

---

## TL;DR for the owner

1. **The pipeline is healthy.** Most bindings surface their library's headline use case usably from C#,
   with idiomatic `Task`/`async`, correct nullability, `IDisposable`, and clean PascalCase naming. The
   alarming-looking low coverage percentages are almost always **intended exclusions** (module-internal /
   `@_spi` pruning, synthesized-Codable, SwiftUI-views-bridged) or **accounting artifacts** (getter+setter
   counted as 2 member slots, availability-gated members), not real gaps. See the per-library table.

2. **One systemic correctness class deserves a generator fix above all others: EveryProtocol proxy
   skips.** When a protocol/delegate's EveryProtocol conformance isn't emitted, the binding still compiles
   but the type **fails at runtime** — three verified shapes: a getter that *throws* (RealityFoundation
   `ModelComponent.Materials`), a delegate whose callbacks *silently never fire* (RoomPlan
   `RoomCaptureViewDelegate`), and "bring-your-own" extension points that are dead (BlinkIDUX analyzers).
   This is the highest-value, highest-risk theme in the whole audit.

3. **One confirmed runtime bug was masked as a test `Skip`** (Stripe `STPAPIClient.AppInfo` NSString
   corruption) — now **RESOLVED**: it was a general `Optional<ObjC-rooted class>` accessor double-VWT-copy bug,
   fixed generator-side and pinned by a BindingTests gate; only an external test-mask cleanup remains. See the
   dedicated section.

4. **The universal weakness is test *depth*, not coverage.** Nearly every binding's tests prove
   construction / metadata / enum ordinals but **not the end-to-end functional flow** the library exists
   for. This is the single most actionable process recommendation and the reason these tests are weak as a
   regression sweep.

---

## Per-library verdict & coverage table

Coverage is `emitted/total`. **Effective** corrects for intended exclusions + accessor/availability
accounting (the honest "is the real API surfaced?" number). Verdict tags: ✅ strong/usable ·
⚠️ usable with a notable gap · 🔶 partial / facade-mediated · 🛑 not a live consumer package.

### Third-party libraries

| Library | Mode | Types | Members | Effective | Verdict |
|---|---|---|---|---|---|
| [Lottie](Lottie.md) | source | 80/83 | 438/471 (93%) | High | ✅ Animation playback usable; 2 sync DotLottie loaders blocked by `Result<T, any Error>`. |
| [Kingfisher](Kingfisher.md) | source | 129/133 | 574/544¹ | High | ✅ `KF` builder works; `KingfisherWrapper<UIImageView>.setImage` 19 overloads dead (GenericTypeCallback) — builder is the alt. |
| [Nuke](Nuke.md) | source | 63/63 | 319/360 (89%) | High | ✅ Strong; `[K: any Sendable]` userInfo dict blocked (existential); custom processor closure blocked. |
| [Mappedin](Mappedin.md) | source | 366/366 | 2553/2932 (87%) | ~98% | ✅ Strongest in set. Typed `on/off<T>` event *subscription* blocked (generic `(T?)->Void` closure). |
| [BlinkID](BlinkID.md) | source | 118/118 | 595/646 (92%) | High | ✅ Strong; `*TypeType` naming stutter; clean. |
| [BlinkIDUX](BlinkIDUX.md) | source | 36/41 | 170/230 (74%) | Med | ⚠️ Bridge `ViewSession.CreateAsync` usable; **headless model read-only** (9 GenericProtocolConstraint drops); `onResult` raw `int`. |

### Apple frameworks

| Library | Mode | Types | Members | Effective | Verdict |
|---|---|---|---|---|---|
| [CryptoKit](CryptoKit.md) | apple | 116/119 | 305/379 (80%) | Med-High | ⚠️ Hashing/keys usable; **NIST P-256/384/521 ECDSA sign+verify unreachable** (typed `Signature` concretization gap); no raw-byte accessor. |
| [StoreKit2](StoreKit2.md) | apple | 78/78 | 421/518 (81%) | High | ✅ Products/purchase/transactions usable; `OfferTypeTypeType` stutter; tests await nothing. |
| [WeatherKit](WeatherKit.md) | apple | 54/54 | 263/397 (66%) | High² | ✅ Usable; single-dataset `weather<T>(for:)` dark (GenericTypeCallback); async-name inconsistency. |
| [WorkoutKit](WorkoutKit.md) | apple | 27/27 | 110/171 (64%) | ~100%² | ✅ Accessor/availability accounting; usable. |
| [ProximityReader](ProximityReader.md) | apple | 104/104 | 365/552 (66%) | ~100%² | ✅ Accessor/availability accounting; clean. |
| [TipKit](TipKit.md) | apple | 41/42 | 143/177 (81%) | Med | ⚠️ Display path usable; **protocol-extension query members on `AnyTip` absent from symbol graph** (`shouldDisplay`/`statusUpdates`). |
| [ActivityKit](ActivityKit.md) | apple | 25/25 | 33/71 (46%) | Facade | 🔶 Generic `Activity<T>` direct path dead; `Swift.ActivityKit.LiveActivity` **supplement facade is the working path** (correct mitigation). |
| [FamilyControls](FamilyControls.md) | apple | 6/9 | 21/29 (72%) | High | ✅ 3 SwiftUI views bridged; `revokeAuthorization() async` wrapper missing; `IExistentialBoxable` leak. |
| [Translation](Translation.md) | apple | 9/9 | 43/43 (100%) | Full | ✅ Clean; SwiftUI `.translationTask` N/A; async paths untested. |
| [LiveCommunicationKit](LiveCommunicationKit.md) | apple | 33/33 | 107/146 (73%) | High | ⚠️ **Delegate `didActivate`/`didDeactivate(AVAudioSession)` collapse to one indistinguishable C# method** (DuplicateSignature, verified). |
| [MatterSupport](MatterSupport.md) | apple | 11/11 | 46/86 (53%) | ~100%² | ✅ Accessor accounting; small clean setup-flow binding. |
| [RoomPlan](RoomPlan.md) | apple | 39/39 | 142/188 (76%) | Med-High | ⚠️ Core scan usable; **`RoomCaptureViewDelegate` proxy not emitted → view-delegate callbacks silently dead**; `CapturedStructure` arrays → AnyType. |
| [RealityKit](RealityKit.md) | apple | 32/32 | 136/163 (83%) | High | ✅ Clean. |
| [RealityFoundation](RealityFoundation.md) | apple | 569/570 | 2375/2564 (93%) | High | ⚠️ ECS usable; **`ModelComponent.Materials` getter THROWS at runtime** (EveryProtocol proxy gap, verified); no `ComponentSet.Get<T>`. |
| [MusicKit](MusicKit.md) | apple | 134/134 | 677/966 (70%) | Med-High | ⚠️ Catalog search + player usable via shims; **library-read flow dead** (`MusicLibraryResponse<T>.items` → AnyType, verified). |
| [AppIntents](AppIntents.md) | apple | 309/312 | 332/734 (45%) | Shells | 🛑 **Cannot author intents/entities/shortcuts from C#** (macro + build-metadata bound). Correctly **not shipping for 1.0**. Limited donation-management interop slice only. |
| [Matter](Matter.md) | objc | ~1320 ↔ headers | n/a | ~100% | ✅ Near-complete ObjC binding; completion-handlers stay `Action<T,NSError>` (no `Task` overloads). |

### Stripe meta-package (one doc: [Stripe.md](Stripe.md))

ModuleInternal (`@_spi`) pruning dominates every submodule **by design** — Codex confirmed at the
`.swiftinterface` level that the empty modules are genuinely `@_spi(STP)`-gated, not public-but-dropped.

| Submodule | Types | Members | Verdict |
|---|---|---|---|
| **StripePayments** | 205/244 | 1329/1654 (80%) | ✅ The workhorse public API — usable. |
| **StripePaymentSheet** | 74/164 | 325/828 (39%) | ⚠️ Drop-in UI usable; deferred/server-confirm flow not constructible (closure gap). |
| StripePaymentsUI | 12/41 | 140/277 | ✅ Usable (SwiftUI views bridged). |
| StripeApplePay | 18/36 | 75/211 | ✅ Apple Pay path usable; some handler closures dropped. |
| StripeConnect / FinancialConnections / Identity / Issuing / CardScan | — | — | ✅ Public surfaces bound; internal-heavy. |
| StripeCore | 21/113 | 65/452 (14%) | 🔶 Mostly `@_spi` internal support; transitive-only. |
| **StripeUICore** | 1/96 | **0/538** | 🔶 **Empty by design** (`@_spi(STP)`-only) — ship transitive-only, not standalone. |
| **StripeCameraCore** | 0/14 | **0/65** | 🔶 **Empty by design** (`@_spi(STP)`-only) — transitive-only. |
| Stripe3DS2 | objc | 1238 lines | ObjC-mode; 3DS challenge flow. |

¹ Kingfisher emitted members exceed the denominator because the generator synthesizes CSM extension
members beyond the parsed total. ² Low member-% is getter+setter-as-2-slots + availability-gated members
not emitted on the iOS build; effective API coverage is near-total (see [theme 10](#theme-10)).

---

## Prioritized generator unlocks (value × tractability)

Ranked. "Libs" lists the bindings each unlock would materially improve.

### Tier 1 — highest value, tractable, fixes real correctness gaps

1. **Emit EveryProtocol proxies for delegate/protocol carriers.** *The #1 finding.* A skipped proxy makes
   a type compile but fail at runtime (throw / silent-dead callbacks / dead extension points).
   **Libs**: RealityFoundation (`Materials` getter throws), RoomPlan (`RoomCaptureViewDelegate` silent),
   BlinkIDUX (6 analyzer proxies), AppIntents/Stripe buckets. Ties to the recent *"Partition EveryProtocol
   emission plans by carrier class"* work — extend that to these carriers. **At minimum**, a property whose
   getter can only throw should be modeled set-only / documented, not emitted as a normal getter.

2. **Generalize DataProtocol/CSM concretization to typed-struct return types** (today it only fires when
   the return type is `Data`). **Libs**: CryptoKit (unlocks NIST ECDSA `signature`/`isValidSignature` —
   core PKI, currently unreachable); MusicKit (`MusicLibraryResponse<T>.items` per-T projection — unblocks
   the entire library-read loop). High value: both are the library's headline capability.

3. **Project existentials in return/property/dict position** (`any Sendable`/`any Error`/`any Protocol`
   → `object` or a typed projection). **Libs**: Nuke (`userInfo` request-tagging), Lottie (2 DotLottie
   loaders), RoomPlan (`CapturedStructure` geometry arrays + `Object.attributes`). Recurring across the set.

### Tier 2 — medium value/effort

4. **Preserve Swift argument labels on C# overload collisions** (delegate methods → ObjC-selector-style
   names, e.g. `CaptureSessionDidAdd`). *Note: this is the **documented deferred protocol-collision-rename
   limitation** (`ProtocolHandler.cs ~362`), not a new bug* — but its consumer impact is real and verified:
   LCK (audio activate vs deactivate indistinguishable), RoomPlan (room add vs change vs update), with
   large buckets in RealityFoundation (41) and AppIntents (175). Worth promoting from "deferred" to "do."

5. **Closed-generic `@_cdecl` wrappers for known instantiations** (GenericTypeCallback). **Libs**:
   Kingfisher `setImage`, ActivityKit `update`/`end`, WeatherKit `weather<T>(for:)`, MusicKit `nextBatch`.
   Most have working alternatives (builders/facades), so medium priority.

6. **Marshal generic closure params `(T?)->Void`.** **Libs**: Mappedin (the entire typed `on/off<T>` event
   *subscription* model — consumers can hold tokens but can't subscribe), generic transform closures.

7. **Concrete-specialization trampolines for PAT/Self-constrained generic methods**
   (GenericProtocolConstraint). **Libs**: BlinkIDUX (`ScanningViewModel` — turns a read-only observer into
   a controllable model), MusicKit (`MusicLibrarySectionedRequest.filter/sortItems`), Mappedin typed
   queries, RoomPlan `Object.attribute<T>`.

### Tier 3 — lower / quality polish

8. **Walk protocol-extension members onto concrete conforming types** (symbol-graph gap). **Libs**: TipKit
   (`AnyTip.shouldDisplay`/`statusUpdates`/`invalidate` entirely absent) — unblocks the query half of TipKit.
9. **Synthesize a raw-byte accessor** for `withUnsafeBytes`-only types (`ToByteArray()`). **Libs**: CryptoKit
   (Digest/MAC/Nonce/Key have no byte export).
10. **ObjC-mode: emit `[Async]`/`Task` overloads for `completionHandler:` selectors.** **Libs**: Matter,
    Stripe3DS2 — ergonomic uplift, not a bug.
11. **Fix `*TypeType` naming stutter** (nested-type name doubling). Multi-binding cosmetic: StoreKit2
    `OfferTypeTypeType`, BlinkID, Nuke, StripeApplePay, RealityFoundation, AppIntents, Matter.
12. **Standardize async wrapper naming** (`Get*Async`). Minor: WeatherKit `WeatherAsync` vs
    `GetAttributionAsync`; Translation `StatusMethodAsync`.
13. **Suppress or comment dead shells** (public types with zero usable members) and impl-detail leaks
    (`IExistentialBoxable`, `ExistentialContainer0/1`) in public signatures.

---

## The universal test-depth finding

**Every binding audited proves construction / metadata / enum ordinals but not the end-to-end functional
flow the library exists for.** Examples: CryptoKit has *no* SHA round-trip KAT; StoreKit2's 30 cases await
nothing; Kingfisher's ~230 cases never exercise a download/cache path; Stripe awaits no async flow;
ActivityKit/Translation/MusicKit leave their headline async paths untested; RoomPlan/RealityFoundation pin
no geometry round-trip and don't pin the verified runtime bugs above.

These tests are good *regression scaffolding* (they catch "did it stop compiling / loading / resolving
metadata") but they are **weak as the regression sweep** because they don't prove the ABI-crossing call
actually works. Many real flows legitimately need entitlements / network / hardware → guarded `Skip`s are
fine. But a meaningful subset is runnable headless and simply missing: a SHA known-answer test, a
cache/JSON round-trip, an `await`-over-empty-sequence, a value round-trip across the P/Invoke. **Top process
recommendation: for each binding, add functional round-trip coverage for its ONE headline flow, headless
where possible** — and pin every verified runtime bug below with a red test.

---

## Confirmed runtime bug (masked as a Skip) — RESOLVED

*The original finding is preserved below as historical context; the imperative actions it lists (reproduce in
BindingTests, grep siblings) are now done — see the **RESOLVED** blockquote at the end of this section for
current status.*

**Stripe `STPAPIClient.AppInfo` — NSString round-trip corruption**
(`libraries/Stripe/tests/Program.cs:707`). The test sets `AppInfo`, reads it back, and when the readback
`Name` is corrupted it downgrades to `results.Skip("...String corruption: got '{readBack.Name}'")`. In-source
comment hypothesizes `swift_retain` on NSString tagged pointers corrupts inline data on the getter
(`NewSome`) path; the setter path does not corrupt. This **violates three project principles at once**
(no-expected-failures, all-corruption-is-our-bug-until-proven, don't-weaken-assertions). Action: reproduce
in **BindingTests**, let it go **red**, and root-cause the NSString tagged-pointer retain/marshalling path in
`StripeCore`. The tagged-pointer mechanism is a hypothesis, not established — the repro should confirm it.
**Recommend grepping the other ObjC-bridged bindings (Matter, Stripe3DS2, BlinkID) for similar masked
Skips**, since an NSString-getter corruption would not be Stripe-specific.

> **RESOLVED (classification: real *general* marshalling bug — not package-specific, not a bad test).**
> Root cause: the emitter returned `Optional<Class/ObjC-rooted class>` property getters as `SwiftOptional<T>`,
> forcing them through `MarshalFromSwift` + `NewSome` — **two VWT `InitializeWithCopy` calls** that mangled the
> returned object's inline small-string (SSO) ivar (+2 at byte offset 4, cumulative). The tagged-pointer
> `swift_retain` mechanism is **refuted**: the corruption *location* (the SSO ivar) matched, but the *cause* was
> the double VWT copy in the accessor return. **The corruption was accessor-only**, fixed by `19560c96`
> (accessor → direct `IntPtr` + `GetINativeObject<T>(ptr, owns:true)` / `MarshalFromSwift<T>`, zero VWT ops;
> also closes a retain leak). The sibling method-**return** path (`OptionalProjection`) always bypassed
> `SwiftOptional` (the IntPtr result IS the payload), so it never had the corruption; `793e77a4` fixed a
> *separate* retain **leak** there (`ownsReference:false → true`), not string corruption.
> **Gated:** BindingTests `OptionalObjCClassPropertyTests` over the faithful `InfoCarrier`/`ClientCarrier` shape.
> The property-accessor path is green on Mono JIT (sim) + NativeAOT (device) (original gate `7ae2ed3c`); the
> method-return copy-out (`snapshotInfo`/`makeInfoCarrier`) was added here and is green on Mono JIT (sim),
> exercising the identical `IntPtr` + `GetINativeObject(owns:true)` copy-out as the accessor (no new NativeAOT
> ABI surface), so string integrity is now gated on `OptionalProjection` too, not just the accessor (the
> accessor is the path that had the corruption; the return path never did). **This repo is done; the only
> remaining action is external:**
> `swift-dotnet-packages/libraries/Stripe/tests/Program.cs:684–718` — replace the corruption
> `results.Skip("StripeCore_STPAPIClient_AppInfo", …)` branch with `results.Pass` asserting
> `readBack.Name == "TestApp"` (all four fields ideally); the generator fix already makes it pass.
> **Sibling sweep (done):** no other NSString/ObjC-bridging masked Skips exist in the audit corpus — Matter's
> lone skip is a legitimate factory/storage-delegate setup skip; BlinkID's "skips" are the generator's
> compile-time skip-reason taxonomy (not runtime masks); Stripe3DS2 records none; Lottie's are missing-test-asset
> skips. The "grep the other ObjC-bridged bindings" recommendation is closed with no new repro campaigns opened.

---

## Architectural limits (document, don't chase)

Not generator-fixable; the binding is doing the right thing by surfacing shells / bridging / facading:

- **Macro + OS-integration frameworks** (AppIntents, TipKit `#Rule`): the authoring model needs Swift
  macros + compiler-synthesized conformances + build-time metadata extraction that a C# app can't produce.
  AppIntents is correctly **not shipping for 1.0**. A C# source generator emitting a Swift companion target
  is the only path, and it's out of 1.0 scope.
- **C# can't satisfy Swift-compiler-synthesized conformances** (`ActivityAttributes`, Codable/Hashable on
  C# types). ActivityKit's `Activity.request` is permanently direct-unbindable → the **supplement facade**
  is the correct answer; note where `Swift.*.supplement` facades are the real consumer entry point.
- **SwiftUI-only presentation** (Translation `.translationTask`, TipKit `TipView`, the Stripe `*UI`
  modules' views) — bridged or N/A, not a defect.

---

## Notes on calibration & verification

- **6 consequential claims independently verified** against raw artifacts this pass (all precise): LCK
  delegate collapse (abi.json + cs:6777/9772), RoomPlan view-delegate + CapturedStructure (report + doc),
  RealityFoundation Materials-getter-throws (cs:79286), MusicKit `items` AnyType (report), AppIntents
  zero-`Perform` / shelved (cs + README), Stripe AppInfo Skip (Program.cs:707).
- **Codex second opinion** (AppIntents + Stripe, the two judgment-heaviest): confirmed all load-bearing
  conclusions; independently verified Stripe's empty modules are `@_spi(STP)`-gated at the `.swiftinterface`
  source-of-truth level; drove two corrections to the AppIntents doc (residual donation-management interop
  slice is callable — not "wholly inert"; `SkippedTypes:3` counting clarified).
- **Coverage-% caution**: never read a raw member-% as "missing API." Getter+setter count as 2 slots,
  init overloads collapse, availability-gated members aren't emitted on every TFM, and `SynthesizedCodable`
  is intended. The **Effective** column is the honest figure.

<a name="theme-10"></a>
*Full cross-cutting working notes (13 themes, with line-anchored evidence) are retained in the audit
scratch; the prioritized unlocks above are their actionable distillation.*
