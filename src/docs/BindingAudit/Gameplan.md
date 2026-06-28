# Binding Audit — Generator-Fix Gameplan (session-runner doc)

This is the execution plan that turns the [Binding Audit](_SUMMARY.md) findings into generator-tooling
fixes. It is written for the `session-runner` tool (`/Users/wojo/Dev/session-runner`), **one doc, many
numbered sessions** mode:

```bash
python3 run-sessions.py --repo ~/Dev/swift-bindings \
  --doc src/docs/BindingAudit/Gameplan.md --sessions 11 --panes
```

Every worker reads the whole **Context** section, then executes only its own `## Session N` section. Sessions
are ordered so confirmed runtime bugs land first and later sessions can build on earlier ones; a few explicit
cross-session dependencies are called out. Each session is a coherent, committable, PR-sized chunk with a
**Validation** line.

---

## Context (every session reads this)

### What this is

A static read-and-reason audit of all 26 shipped bindings produced a prioritized list of **generator
correctness/coverage unlocks**. This doc packages them into sessions. The evidence lives in this folder:
[`_SUMMARY.md`](_SUMMARY.md) (synthesis + the "Prioritized generator unlocks" tier list) and one file per
library (e.g. [`CryptoKit.md`](CryptoKit.md), [`RoomPlan.md`](RoomPlan.md)). **Before touching code for a
session, read the per-library audit file(s) it names** for the full evidence behind each finding.

### The repo and the pipeline

- Generator: `src/Swift.Bindings/src/` — Parser → TypeDatabase → Marshaler → Emitter. This is where almost
  every fix lands.
- The audit's `Module.cs:LINE` anchors point at **generated output in the `swift-dotnet-packages` consumer
  repo** (and at what `nuke validate` regenerates) — they are evidence of the *bug shape*, not files in this
  repo. **Do not** depend on the external repo as your gate.
- **BindingTests is the durable end-to-end gate.** For every behavioral change, reproduce the Swift pattern in
  `BindingTests/Sources/SwiftBindingsTestLib/` (organized by domain: `Closures/`, `Collisions/`, `Generics/`,
  `Protocols/`, `Marshalling/`, …) and add C# assertions to the matching domain file in
  `BindingTests/RuntimeTestsApp/`. Unit tests in `src/Swift.Bindings/tests/UnitTests/` cover emitter/parser
  logic. Match the layer to what actually exercises the change.

### Key generator source map (verified to exist)

| Area | Files |
|---|---|
| EveryProtocol / protocol proxies | `Emitter/StringEmitter/EveryProtocolEmitter.cs`, `Emitter/StringEmitter/ProtocolProxyEmitter*.cs`, `Emitter/StringEmitter/ProtocolProxyEmissionPolicy.cs` |
| Concrete generic specialization (CSM) | `Marshaler/ConcreteSpecializationEngine.cs`, `Emitter/StringEmitter/Handler/ConcreteProtocolSpecializationEmitter.{cs,Sync,Async,AsyncGenericParent}.cs`, `Emitter/StringEmitter/Handler/ConstrainedExtensionEmitter.cs` |
| Existential projection | `Marshaler/ExistentialHandler.cs`, `Marshaler/Projection/ExistentialProjection.cs`, `Marshaler/Projection/ExistentialElementCarrier.cs`, `TypeDatabase/Resolver/Strategies/ExistentialStrategy.cs`, `Emitter/StringEmitter/Handler/ExistentialBypassEmitter.cs` |
| Overload-collision / argument labels | `Emitter/StringEmitter/Handler/ProtocolHandler.cs` (~362, the documented deferred rename limitation) |
| Module classification (SwiftUI/Combine gate) | `TypeDatabase/AppleFrameworkRegistry.cs`, `Emitter/StringEmitter/Handler/ModuleHandler.cs`, `Emitter/StringEmitter/ValidationRuleSet.cs`, `Emitter/StringEmitter/MemberGateEvaluator.cs` |
| Symbol-graph parsing | `Parser/SymbolGraphDocParser.cs` |
| Naming / nested-type stutter | `Emitter/StringEmitter/NamespaceFacadeEmitter.cs`, `Emitter/StringEmitter/ConstructorWrapperEmitter.cs`, `TypeDatabase/Resolver/Strategies/MetatypeStrategy.cs` |
| Skip-reason taxonomy / report | `Reporting/WorkaroundRecommendations.cs`, `Reporting/BindingReport.cs` |

### Build/test commands (use `nuke`, never raw)

| Command | When |
|---|---|
| `dotnet build src/Swift.Bindings/src -c Debug` | **After every generator source edit.** `nuke binding-tests`/`validate` run the generator from `bin/Debug/` and only rebuild it when the dll is *missing*, never when stale. Skipping this makes regen emit pre-patch output. |
| `nuke test` | Unit + integration (the per-commit gate; `Swift.Bindings.Unit.Tests` Failed:0 is the real signal — analyzer-test refpack failures in the sandbox are environmental). |
| `nuke binding-tests --compile-only` | Regenerate + compile-check (fail-closed). |
| `nuke binding-tests` / `--skip-regen` / `--sim` | iOS Simulator (Mono JIT) runtime gate — the everyday end-to-end signal. |
| `nuke binding-tests --device --device-udid 559479FD-3C60-51E4-8B2C-872D8CBA8B54` | Physical iPhone (NativeAOT). Run when the change touches calling conventions, struct marshalling, or P/Invoke signatures — Mono and NativeAOT have different bugs. First device run after a sim regen cannot use `--skip-regen`. |
| `nuke validate` (~5 min) | Cross-cutting generator changes / pre-release only. **Not** the routine inner loop. After a validate run, `git checkout HEAD --` the transient `-behaviortier` version-stamp churn in `src/**` but **keep** `build/baselines/validation-baseline.json`. |

### Non-negotiable conventions (CLAUDE.md + project memory)

- **No shortcuts. Root-cause fixes only** — never weaken an assertion, skip a failing test, or paper over a
  symptom. If unsure whether a fix is root-cause, document the reasoning in the summary.
- **TDD for the verified runtime bugs**: write the BindingTests fixture first, confirm it goes **red**, then
  fix, then confirm **green**. Use maximum-case fixtures as the durable in-repo gate, not minimal repros.
- **ALL runtime crashes/corruption are OUR bug until proven otherwise.** The authoritative upstream-.NET list
  is in memory (`feedback_mono_jit_blame`); anything not on it is ours. Before blaming the runtime, verify the
  generated C# P/Invoke matches the Swift `@_cdecl` wrapper (calling convention, param count/types, lib name,
  entry symbol). Check `@frozen` before filing a register-placement bug.
- **No doc-file references in code comments** (no `src/docs/*` paths, no Finding/Defect numbers) — inline the
  technical rationale instead. The owner archives docs, so pointers dangle.
- When fixing a bug pattern, grep the whole codebase for ALL instances before finishing.
- After generator changes, verify generated output **compiles** — don't assume.
- **Assert behavior, not implementation** (semantic checks / round-trips over exact generated-string matches);
  `[Theory]`/`[InlineData]` for input-only variations.
- **Zero-regression policy**: unit-test and BindingTests pass counts must be ≥ baseline before committing.
- Keep your own context clean — offload multi-file investigation to Explore/Plan/general-purpose subagents
  (Sonnet for research). Sanity-check non-obvious approaches past Codex + Grok per `/coding-rules`.

### Standing expectation for every session

Each session must (a) land the generator fix at root cause, (b) add **functional round-trip BindingTests**
for the flow it unblocks (the audit's universal finding is that existing tests prove construction/metadata but
**not** the end-to-end ABI-crossing flow — fix that for the surface you touch), (c) add unit tests for the
emitter/parser logic changed, and (d) regenerate + verify compile + run the right runtime gate.

### Out of scope (document, do not chase — see `_SUMMARY.md` "Architectural limits")

- AppIntents **authoring** (macro + build-metadata bound) — correctly not shipping for 1.0. Only the
  module-misclassification slice (Session 9) is in scope, not intent/entity/shortcut authoring.
- ActivityKit `Activity.request` (compiler-synthesized `ActivityAttributes`) — the supplement facade is the
  correct answer. Only the generic-method *mechanism* work in Sessions 6/7 touches ActivityKit.
- SwiftUI-only presentation (Translation `.translationTask`, TipKit `TipView`, Stripe `*UI` views) — bridged.
- **ObjC-mode `[Async]`/`Task` overloads** for `completionHandler:` selectors (Matter, Stripe3DS2) — this is a
  **bgen / `ApiDefinition.cs` annotation** uplift, not a Swift-generator fix, and the audit classifies it as
  ergonomics not a bug. Deferred; not a session here.
- **RealityKit ARKit/UIKit TypeDatabase gaps** (`ARRaycastQuery`/`ARRaycastResult`/`ARTrackedRaycast`,
  `UIAccessibilityCustomRotor.Direction`, `RealityFoundation.AccessibilityComponent.RotorType`) — these need
  the relevant Apple type bindings shipped, not generator logic. Dependency-gated; track separately.

---

## Session 1 — EveryProtocol proxy emission for delegate/protocol carriers (+ pin 2 verified runtime bugs)

**This is the #1 finding in the whole audit.** A skipped EveryProtocol conformance makes a type *compile* but
*fail at runtime*: a getter that throws, a delegate whose callbacks silently never fire, or a dead extension
point. Builds directly on the recent *"Partition EveryProtocol emission plans by carrier class"* work
(`EveryProtocolEmitter.cs`). Read [`RealityFoundation.md`](RealityFoundation.md), [`RoomPlan.md`](RoomPlan.md),
[`BlinkIDUX.md`](BlinkIDUX.md).

**Deliverable:** Emit EveryProtocol conformance proxies for the carrier classes currently skipped, fixing the
`UnsatisfiedHigherKindConstraint` and `EveryProtocolConformanceSkipped` (no-decision) paths.

**Targets**
- **RealityFoundation `Material.MaterialProxy`** (skip = `UnsatisfiedHigherKindConstraint`) → fixes the
  **verified runtime bug**: `ModelComponent.Materials` getter unconditionally `throw new
  NotSupportedException("Protocol proxy not available…")` (generated `RealityFoundation.cs:79286`). Setter
  works; getter always throws.
- **RoomPlan `RoomCaptureViewDelegate` proxy** → fixes the **verified runtime bug**: `RoomCaptureView.Delegate`
  setter compiles but Swift cannot reverse-dispatch `captureView(shouldPresentProcessedResults:)` /
  `captureView(didPresent:)` — callbacks silently never fire; getter also throws (`RoomPlan.cs:6143-6145`
  getter throw, `:6188` setter with no proxy lambda, `:6494-6502` interface). Note `IRoomCaptureSessionDelegate`
  already works fully — use it as the reference for a correct proxy.
- **RealityFoundation `EntityActionProxy`, `PhysicsJointProxy`** (`EveryProtocolConformanceSkipped`, no
  decision recorded).
- **BlinkIDUX analyzer/protocol proxies** (`EveryProtocolConformanceSkipped`): highest value
  `CameraFrameAnalyzerProxy` (custom analyzer injection) and `EventStreamProxy`; also `ScanningResultProtocol`,
  `OnboardingStepProtocol`, `ReticleStateMachineProtocol`, `ReticleStateProtocol`.

**Tests (TDD — write red first):** Reproduce both verified bugs as BindingTests fixtures under
`Protocols/` *before* fixing — a Swift type with a delegate/protocol carrier whose getter currently throws and
whose callbacks currently don't fire — assert the getter returns and the callback round-trips into managed
code. Confirm red, fix, confirm green. Add `EveryProtocolEmitterTests` unit cases for each newly-handled
carrier class. At **minimum**, if a carrier genuinely cannot get a proxy, a property whose getter can only
throw must be modeled set-only / annotated, not emitted as a normal getter (see Session 11).

**Dependencies:** none. **Validation:** `nuke test`; `nuke binding-tests --compile-only`; `nuke binding-tests
--sim` then `--device` (callback reverse-dispatch is calling-convention-sensitive) — new fixtures green.

---

## Session 2 — Stripe `STPAPIClient.AppInfo` NSString round-trip corruption (confirmed bug, masked Skip)

A **confirmed runtime bug currently hidden behind a test `Skip`** — it violates three project principles at
once (no-expected-failures, all-corruption-is-our-bug, don't-weaken-assertions). Read the
[`Stripe.md`](Stripe.md) "Confirmed runtime bug" section.

**Deliverable:** Turn the masked skip into a red BindingTests assertion, root-cause the NSString
marshalling, make it green.

**Targets**
- The test at `libraries/Stripe/tests/Program.cs:707` sets `STPAPIClient.AppInfo`, reads it back, and when the
  readback `Name` is corrupted it downgrades to `results.Skip("…String corruption: got '{readBack.Name}'")`.
  In-source comment hypothesizes `swift_retain` on NSString **tagged pointers** corrupts inline data on the
  getter (`NewSome`) path; the setter path does not corrupt. **The tagged-pointer mechanism is a hypothesis,
  not established** — the repro must confirm or refute it.

**Approach:** Reproduce in **BindingTests** (ObjC-bridged NSString set→get round-trip through the
`NewSome`/getter path), let it go **red**, then root-cause the NSString tagged-pointer retain/marshalling path
in the ObjC-bridge marshalling (Stripe binds in ObjC mode; the corruption is in the shared NSString
get-path, so the fix is general). Verify the generated getter's retain/copy semantics against the actual
Swift/ObjC ABI before assuming upstream. **Then grep the other ObjC-bridged bindings for similar masked
Skips** (Matter, Stripe3DS2, BlinkID) — an NSString-getter corruption would not be Stripe-specific.

**Dependencies:** none. **Validation:** `nuke test`; `nuke binding-tests --sim` **and** `--device` (NSString
marshalling differs Mono vs NativeAOT) — the new repro red→green.

---

## Session 3 — Existential projection in return / property / dictionary / array position

Project `any X` existentials (`any Sendable`, `any Error`, `any Protocol`, `[any P]`, `[any P.Type]`,
`[String: Any]`) to `object` or a typed projection instead of dropping the member. Recurring across the set.
Read [`Nuke.md`](Nuke.md), [`Lottie.md`](Lottie.md), [`RoomPlan.md`](RoomPlan.md), [`Mappedin.md`](Mappedin.md),
[`MusicKit.md`](MusicKit.md), [`FamilyControls.md`](FamilyControls.md), [`TipKit.md`](TipKit.md),
[`LiveCommunicationKit.md`](LiveCommunicationKit.md).

**Deliverable:** Generator projects existential return/property/dict/array positions rather than emitting a
`// Unsupported:` drop. Work in `Marshaler/Projection/ExistentialProjection.cs` +
`ExistentialElementCarrier.cs`, `Marshaler/ExistentialHandler.cs`, `TypeDatabase/Resolver/Strategies/ExistentialStrategy.cs`.

**Targets**
- **Nuke** `ImageContainer.userInfo` / `ImageRequest.userInfo` `[UserInfoKey: any Sendable]` → `object`
  dictionary value (`Nuke.cs:5654`, `:6275`). `UserInfoKey` constants already bound; the dict is the unlock.
  Cross-library `any Sendable`-in-dict-value pattern. **High value.**
- **Lottie** `DotLottieFile.SynchronouslyBlockingCurrentThread.loadedFrom`/`.named` returning
  `Result<DotLottieFile, any Error>` (`Lottie.cs:14795`) — project `any Error`. (Async alternatives exist;
  medium priority but same machinery.)
- **RoomPlan** `CapturedRoom.Object.attributes` `[any CapturedRoomAttribute]` (fine-grained furniture
  attributes — `ChairType`, `SofaType`, …).
- **Mappedin** `MVF` `SwiftArray<Any>` properties (11) + opaque `object` properties (12) (`Mappedin.cs:126339`)
  — low idiomatic value (internal format layer) but exercises the array-of-`Any` path.
- **MusicKit** `.types` `[any X.Type]` metatype-array on 5 request types (`MusicCatalogSearchRequest.types`,
  `MusicLibrarySearchRequest.types`, `MusicCatalogChartsRequest.types`, `MusicPersonalRecommendation.types`,
  `MusicCatalogSearchSuggestionsRequest.typesForTopResults`).
- **FamilyControls** `FamilyControlsError.errorUserInfo` `[String: Any]` → `Dictionary<string, object?>`.
- **TipKit** `Tips.showTipsForTesting`/`hideTipsForTesting` `[any Tip.Type]`.
- **LCK** `ConversationManager.pendingConversationActions` — verify the concrete element type in the symbol
  graph; if `[ConversationAction]` (a bound class) special-case the placeholder; if existential, project here.

**Dependencies:** none. **Validation:** `nuke test`; `nuke binding-tests --compile-only` + `--sim`; add a
BindingTests fixture round-tripping an `any Sendable` dict value and an `[any P.Type]` metatype array. Consider
`nuke validate` (cross-library projection change).

---

## Session 4 — Closed-generic concretization for typed return types (CSM) + CryptoKit usability cluster

Generalize the `ConcreteSpecializationEngine` / CSM concrete-extension emission **beyond `Data`** to
typed-struct and generic-param return types, and propagate containing-type generic args into nested
properties. This unblocks several libraries' headline read flows. Read [`CryptoKit.md`](CryptoKit.md),
[`MusicKit.md`](MusicKit.md), [`StoreKit2.md`](StoreKit2.md), [`WeatherKit.md`](WeatherKit.md),
[`BlinkID.md`](BlinkID.md), [`TipKit.md`](TipKit.md), [`ActivityKit.md`](ActivityKit.md).

**Deliverable:** CSM concretization fires for typed-struct returns (not only `Data`); per-`T` concrete
extension methods emitted for the targets below; containing-type generic argument propagated into nested
generic property element types.

**Targets**
- **CryptoKit NIST ECDSA (high value)** — `P256/P384/P521.Signing.PrivateKey.signature(for:)` and
  `.PublicKey.isValidSignature(_:for:)` are skipped (`GenericProtocolConstraint`, open `D: DataProtocol`),
  emitting only an `[Obsolete]` stub (`CryptoKit.cs:16052-16106`). Extend the `byte[]`/`Foundation.Data`
  concretization that already works for Curve25519 (`CryptoKit.cs:686,707`) and `HashedAuthenticationCode` to
  the `ECDSASignature` return type. This is the most common PKI op and is documented as callable.
- **CryptoKit raw-byte accessors** — synthesize `byte[] ToByteArray()` for the 13 `withUnsafeBytes`-only
  output types (every `*Digest`, `HashedAuthenticationCode`, `AES.GCM.Nonce`, `ChaChaPoly.Nonce`,
  `SymmetricKey`, `SharedSecret`), backed by a thin Swift `@_cdecl(UnsafeRawPointer, Int)` wrapper + C# copy.
  Without it SHA/HMAC output cannot reach any .NET crypto API (only a hex `Description` exists).
- **CryptoKit AES-GCM open-with-AAD** — investigate the Mono-JIT assertion on the generic `Open<TAD>` overload
  (CryptoKit test 29 blocks the authenticated-decrypt step while seal-with-AAD passes). Per "all crashes are
  ours," diagnose the generic dispatch; if needed emit a non-generic `@_cdecl` overload taking `byte[]` AAD.
- **CryptoKit doc fixes** — `CRYPTOKIT-GUIDE.md` lists P256/P384/P521 `Signature(byte[])` as callable (compile
  error today) and falsely lists `HKDF.DeriveKey` as "not projected" (it IS emitted,
  `HKDFSHA256/384/512CsmExtensions.DeriveKey`). Correct both once ECDSA lands.
- **MusicKit `MusicLibraryResponse<T>.items` (CRITICAL)** — `AnyTypeFallback` at `MusicKit.cs:8789` breaks the
  entire library-read loop at the result step (consumer can `await` but can't read items). Emit CSM concrete
  extension per `T` mirroring the working `ResponseAsync` pattern (`MusicKit.cs:4126`). Also
  `MusicCatalogResourceResponse<T>.items` (`:8789/:45877`).
- **StoreKit2** `VerificationResult<T>.payloadValue`/`unsafePayloadValue` (`AnyTypeFallback`,
  `:4174-4175/:4945-4946/:5097-5098/:5249-5250`) → `GetPayloadValue()`/`GetUnsafePayloadValue()` per
  specialization, mirroring the working `GetJwsRepresentation()` (`:4843-4995`).
- **WeatherKit** `Trend<TDimension>.baseline`/`.currentValue` (`AnyTypeFallback`, `:4974-4975`) when
  `TDimension` is a known `NSDimension` subclass; verify `Forecast<DayWeather>.summary` exists in
  `ForecastWeatherKit_DayWeatherCsmExtensions` (`:19877`), add `GetSummary()` if absent.
- **BlinkID** `DriverLicenseDetailedInfo.vehicleClassesInfo: [VehicleClassInfo<StringType>]?`
  (`AnyTypeFallback`, `BlinkID.cs:40934`) — propagate the containing type's `StringType` instantiation into the
  nested generic element → `IReadOnlyList<VehicleClassInfo<…StringResult>>?`.
- **TipKit** `AnyTip.actions`/`rules` — `[AnyTip.Action]`/`[AnyTip.Rule]` are concrete already-emitted types;
  resolve them before `AnyType` fallback.
- **ActivityKit** `ActivityContent<TState>.state` returns the generic param (`AnyTypeFallback`, `:1846`) —
  project generic-param-typed property returns from generic structs.

**Dependencies:** none (Session 6 MusicKit `response()` builds on `items` here). **Validation:** `nuke test`;
`nuke binding-tests --compile-only` + `--sim`; BindingTests: a SHA `ToByteArray()` known-answer round-trip, an
ECDSA sign+verify round-trip, and a generic-response `.items` read. `nuke validate` (CSM change is
cross-cutting).

---

## Session 5 — PAT/Self-constrained generic-method trampolines (`GenericProtocolConstraint`)

Emit concrete-specialization trampolines (Swift `@_silgen_name`/`@_cdecl` shim + C# concrete extension) for
generic methods whose constraints are protocol-with-associated-type / `Self` requirements that C# generics
can't satisfy, using the known concrete instantiations. Read [`BlinkIDUX.md`](BlinkIDUX.md),
[`MusicKit.md`](MusicKit.md), [`CryptoKit.md`](CryptoKit.md), [`ProximityReader.md`](ProximityReader.md),
[`Mappedin.md`](Mappedin.md). Work in `ConcreteProtocolSpecializationEmitter.Sync.cs` /
`ConcreteSpecializationEngine.cs`.

**Targets**
- **BlinkIDUX `ScanningViewModel<T,U,V,A>` control methods** (9: `startScanning`, `pauseScanning`,
  `resumeScanning`, `restartScanning`, `stopEventHandling`, `presentAlert`, `dismissAlert`,
  `licenseErrorAlertDismised`, `timeoutAlertDismised`) — `A: AlertTypeProtocol & Identifiable`,
  `V: ReticleStateMachineProtocol`. Concrete specialization is `BlinkIDUXModel` (T=BlinkIDScanningResult,
  U=UIEvent, V=ReticleStateMachine, A=BlinkIDScanningAlertType) at `BlinkIDUX.cs:10198`. Most take no args /
  return `Void`. Turns a read-only observer into a controllable model. **High value.** Also project the
  associated-type method on `ICameraFrameAnalyzer<TResult,TFrame,TEvent>` (`analyze`/`result`, currently
  `UnsupportedSignature` "unresolvable associated type", interface emitted empty at `BlinkIDUX.cs:7626`).
- **MusicKit** `MusicLibraryRequest<T>.filter(matching:KeyPath:equalTo:)` (7) +
  `MusicLibrarySectionedRequest.filterItems/filterSections/sortItems/sortSections` (15) — Route-C concrete
  extension per `(T, ValueT)` pair, same as the working Sort path (`MusicKit.cs:8032+`).
- **CryptoKit** `HKDF.extract`/`expand` (`:23634-23635`) — concretize for SHA256/384/512 like the working
  `HKDF.DeriveKey` (`:23639`); `SharedSecret.hkdfDerivedSymmetricKey`/`x963DerivedSymmetricKey` (double-generic
  — harder; may need a hand-rolled Swift shim).
- **ProximityReader** `requestDocument<Request: MobileDocumentRequest>(_) -> Request.Response` → a type-erased
  `ReadDocumentAsync(IMobileDocumentDataRequest) -> Task<MobileDocumentResponse>` via a Swift shim.
- **Mappedin** typed `getByType<T>`/`getById<T>`/`getByExternalId<T>` queries (also closure-shaped — coordinate
  with Session 7).

**Dependencies:** none (Session 6 BlinkIDUX async methods build on the BlinkIDUX trampolines here).
**Validation:** `nuke test`; `--compile-only` + `--sim`; BindingTests round-trip for a PAT-constrained void
control method and a typed filter. `nuke validate` (cross-cutting).

---

## Session 6 — `@_cdecl` shims for async/callback methods on generic parent types (`GenericTypeCallback`)

Emit closed-generic `@_cdecl` wrappers that pass `self` + the parent's type metadata / protocol witness tables
as **explicit pointer parameters** (so a direct P/Invoke can supply what Swift's implicit calling convention
needs) for async/callback methods on generic parents, for known concrete instantiations; surface as concrete
extension methods. Work in `ConcreteProtocolSpecializationEmitter.AsyncGenericParent.cs` /
`.Async.cs`. Read [`Kingfisher.md`](Kingfisher.md), [`WeatherKit.md`](WeatherKit.md), [`MusicKit.md`](MusicKit.md),
[`ActivityKit.md`](ActivityKit.md), [`TipKit.md`](TipKit.md), [`BlinkIDUX.md`](BlinkIDUX.md).

**Targets**
- **Kingfisher** all 19 `KingfisherWrapper<TBase>.setImage(…)` overloads + 2 `setBackgroundImage`
  (`GenericTypeCallback`, CLR can't emit `[UnmanagedCallersOnly]` inside a generic type; `Kingfisher.cs:17326`)
  — emit closed-generic wrappers for `UIImageView`/`UIButton` in `ConstrainedExtensionEmitter`, exposed as
  extension methods. Then emit the downstream **`.Kf` extension property** returning
  `KingfisherWrapper<UIImageView>`/`<UIButton>`. **High value (UX).**
- **WeatherKit** `weather<T>(for:including:)` all 6 overloads → `GetCurrentWeatherAsync`,
  `GetHourlyForecastAsync`, `GetDailyForecastAsync`, `GetMinuteForecastAsync` pre-specialized `@_cdecl`
  wrappers. **High value** (single-dataset fetch is the headline). (The iOS-18 variadic-pack statistics
  methods — `repeat each T` — have no C# equivalent; ship fixed concrete query combinations or defer.)
- **MusicKit** `MusicItemCollection<T>.nextBatch()` (`:20388-20389`) → `Task<MusicItemCollection<T>?>` per `T`
  (no pagination today → results truncate at 25); `MusicCatalogResourceRequest<T>.response()` (`:45877`,
  **depends on Session 4 `items`**).
- **ActivityKit** `Activity<T>.update`(5)/`end`(3) + the 5 `Iterator.next` async iterators (the mechanism is
  the long-term unlock even though `request` stays facade-bound).
- **TipKit** `Tips.Event<T>.donate`(2)/`sendDonation`(2)/`deleteDonations`.
- **BlinkIDUX** `BlinkIDUXModel.processAnalyzerResult`/`finishScan` (**depends on Session 5** BlinkIDUX
  trampolines).

**Dependencies:** Session 4 (MusicKit `response`), Session 5 (BlinkIDUX async). **Validation:** `nuke test`;
`--compile-only` + `--sim` **and** `--device` (async ABI / metadata-register passing is calling-convention
critical); BindingTests: an async generic-parent method round-trip (e.g. a `setImage`-shaped callback and a
`nextBatch`-shaped pagination).

---

## Session 7 — Generic closure parameter marshalling (`UnsupportedClosure` + `UnsatisfiedGenericConstraint`)

Marshal generic closure parameters in generic methods, and loosen the `ISwiftObject` generic-arg bound to
accept struct (`ISwiftStruct` / opaque-handle) type arguments. Read [`Mappedin.md`](Mappedin.md),
[`RealityFoundation.md`](RealityFoundation.md), [`Kingfisher.md`](Kingfisher.md), [`Nuke.md`](Nuke.md). Work in
the closure-marshalling path (`Marshaler/…` + `Emitter/StringEmitter/Handler/…`) and the generic-constraint
gate.

**Targets**
- **Mappedin typed event subscription (high value)** — `MapData.on<T>/off<T>`, `MapView.on<T>/off<T>`,
  `BlueDot.on<T>` taking `(T?) -> Void` (`UnsupportedClosure`; `Events` static class `Mappedin.cs:65058`,
  `BlueDotEvents` `:28966`). Without this consumers can hold tokens but **cannot subscribe** — the whole typed
  event model is dead. Also fix the paired `UnsatisfiedGenericConstraint` where `TypedEvent<T>` with a struct
  `T` (`()`, `UInt32`) is rejected by the `ISwiftObject` bound.
- **RealityFoundation** `Scene.subscribe`(2) + `RealityRenderer.subscribe` — generic-over-protocol closure
  `(Event) -> Void where Event: EventType` (`UnsupportedClosure`). Blocks `SceneEvents.Update`, collision
  events, all event-driven ECS. **High value.**
- **Kingfisher** `(T)->T` / `(T)->T?` transform closures (`Filter.init`, `AnyImageModifier.init`,
  `AnyModifier.init`, `AnyRedirectHandler.init`) and the `()->Data` lazy accessor `RetrieveImageResult.data`
  (`Kingfisher.cs:23717`); also the `ISwiftObject`-bound rejections (`ImageCache.memoryStorage`/`diskStorage`
  with `Foundation.Data` type args).
- **Nuke** `ImagePipeline.Configuration.makeImageDecoder` `@Sendable (ImageDecodingContext)->(any
  ImageDecoding)?` closure property + `ImageProcessors.Anonymous.init(id:closure:)`.

**Dependencies:** none. **Validation:** `nuke test`; `--compile-only` + `--sim` **and** `--device` (closure
trampolines cross the ABI); BindingTests: subscribe-to-typed-event round-trip (callback fires with a value) and
a `(T)->T` transform round-trip.

---

## Session 8 — `DuplicateSignature`: preserve Swift argument labels on C# overload collisions

Promote the **documented deferred protocol-collision-rename limitation** (`ProtocolHandler.cs` ~362) to a real
fix: when projected C# overloads collide after label erasure, disambiguate with ObjC-selector-style names built
from the Swift argument labels instead of silently dropping all-but-one. Broad cross-library reach. Read
[`LiveCommunicationKit.md`](LiveCommunicationKit.md), [`RoomPlan.md`](RoomPlan.md),
[`RealityFoundation.md`](RealityFoundation.md), [`MusicKit.md`](MusicKit.md), [`Stripe.md`](Stripe.md),
[`Kingfisher.md`](Kingfisher.md).

**Targets**
- **LCK (high value)** — `conversationManager(_:didActivate:AVAudioSession)` and `…didDeactivate(…)` both
  collapse to one indistinguishable `void ConversationManager(ConversationManager, AVAudioSession)`
  (`LiveCommunicationKit.cs:6777`, iface `:6771-6778`). VoIP audio-session lifecycle is unusable. →
  `ConversationManagerDidActivateAudioSession` / `…DidDeactivateAudioSession`. This also fixes the
  "method names communicate nothing" ergonomic finding systematically.
- **RoomPlan** `captureSession` `didAdd`/`didChange`/`didUpdate(room:)` collapse into one
  (`RoomPlan.cs:5264-5271`; proxy exists at `:12296`, callbacks arrive without add/change distinction) →
  `CaptureSessionDidAdd` / `CaptureSessionDidChange`.
- **RealityFoundation** the 41-collision bucket incl. `TextureResource.init` CGImage/URL/Data overloads.
- **MusicKit** `MusicPlayer.Transition.crossfade` (enum case vs static factory) and `MusicItemID.init`
  (`rawValue:` vs `stringLiteral:`) — both silently drop the second.
- **Stripe** `PaymentSheet.init` / `FlowController.presentPaymentOptions` colliding overloads.
- **Kingfisher** `cancelDownloadTask` (3 dropped, one survives `:18057`); **Lottie** deprecated
  `LottiePlaybackMode.paused` vs synthesized `Paused(at:)`.
- **AppIntents** the 175-collision bucket (largest) — apply the same rule; many are init label collisions.

**Dependencies:** none (but the rename rule is shared machinery — land it once, apply everywhere; grep all
`DuplicateSignature` drops). **Validation:** `nuke test`; `--compile-only` + `--sim`; BindingTests: a
two-overload delegate whose label-distinguished C# methods both round-trip. `nuke validate` (broad rename
impact — watch for unintended new collisions).

---

## Session 9 — Module-attribution misclassification (`SwiftUIConstraint` false positives)

A bounded, **high cross-library value** fix: the generator's unsupported-module gate misclassifies certain
**Foundation** types as "SwiftUI/Combine," dropping huge swaths of otherwise-bindable surface. Read
[`AppIntents.md`](AppIntents.md), [`LiveCommunicationKit.md`](LiveCommunicationKit.md), [`TipKit.md`](TipKit.md).
Work in `TypeDatabase/AppleFrameworkRegistry.cs` + `Emitter/StringEmitter/Handler/ModuleHandler.cs` +
`MemberGateEvaluator.cs`.

**Targets**
- **`Foundation.LocalizedStringResource` (high value, cross-library)** — misclassified as
  "unsupported module (SwiftUI/Combine)"; it is a Foundation type (iOS 16+). In AppIntents alone this drops
  **764 of 793** `SwiftUIConstraint` skips — effectively every user-facing label/ctor
  (`EntityProperty`/`IntentParameter`/`DisplayRepresentation.title`/`AppShortcut.init`/…). Project it as a thin
  `string` wrapper (or correct its module attribution). This benefits **every Apple framework using localized
  strings**, not just AppIntents. *(Authoring intents remains out of scope — this only un-drops the label
  surface and the residual donation-management interop slice.)*
- **`Foundation.Predicate`** — LCK `ConversationHistoryManager.recentConversations` uses
  `Foundation.Predicate<RecentConversation>`; decouple it from the SwiftUI module gate (treat as first-class
  Foundation).
- **TipKit** `TipUIView.init(tip:arrowEdge:)` iOS-17 overloads dropped as `SwiftUIConstraint` (the
  `SwiftUI.Edge` parameter, `TipKit.cs:10523-10524`); investigate whether the iOS-17 `init(tip:)` overload
  *without* `arrowEdge` is wrappable so iOS-17 UIKit consumers get an embedded tip view.

**Dependencies:** none. **Validation:** `nuke test`; `--compile-only` + `--sim`; BindingTests exercising a
`LocalizedStringResource`-typed member round-trip. **`nuke validate` (mandatory here** — this re-classifies a
type touched across many Apple frameworks; confirm the `cs_compile`/`swift_compile` baseline only *rises*).

---

## Session 10 — Protocol-extension symbol-graph walk (TipKit `AnyTip` query path)

A distinct **parser/symbol-graph** change: the symbol graph does not emit protocol-extension members on
concrete conforming types, so the generator never sees them. Walk `extension <Protocol> { … }` blocks and emit
their members onto concrete conformers using the fully-resolved concrete signatures. Read
[`TipKit.md`](TipKit.md). Work in `Parser/SymbolGraphDocParser.cs`.

**Targets (TipKit — the only production query path for tip display status)**
- `AnyTip.shouldDisplay: Bool`, `statusUpdates: AsyncStream<Tips.Status>`, `shouldDisplayUpdates`,
  `invalidate(reason:)` — all four have fully concrete signatures when `Self = AnyTip`
  (`…ios-simulator.swiftinterface:403-426`) but are **absent from `TipKit.cs` entirely** (not even in
  SkippedItems). Parse the `extension Tip { … }` block and emit them on `AnyTip`. **Critical** for TipKit's
  query half.
- `AnyTip.init<T: Tip>(_:)` — the type-erasure entry point is `UnsupportedSignature`; the emitted circular
  `FromTipKit_AnyTip(AnyTip)` factory (`TipKit.cs:577`) is useless. Provide a Swift factory-shim entrypoint so
  C# can build an `AnyTip` from a concrete tip (coordinate mechanism with Sessions 5/6).
- `TipGroup.currentTipUpdates` (AsyncSequence existential `AnyTypeFallback`, `TipKit.cs:721`) — resolve to a
  typed wrapper if the symbol-graph walk surfaces the element type.

**Dependencies:** none (the symbol-graph walk is general; TipKit is the proving ground — grep other Apple
frameworks for protocol-extension-on-conformer gaps once it works). **Validation:** `nuke test`;
`--compile-only` + `--sim`; BindingTests: read `shouldDisplay` / iterate `statusUpdates` on a concrete tip.

---

## Session 11 — Emitter polish: naming stutter, async-name consistency, dead-shell suppression, diagnostics

Cross-cutting low-risk quality work, all in the emitter. None of these block a flow, but they remove
machine-vomit, silent crashes, and IDE noise from the public surface. Read the per-library docs as needed
([`StoreKit2.md`](StoreKit2.md), [`Stripe.md`](Stripe.md), [`WeatherKit.md`](WeatherKit.md),
[`Nuke.md`](Nuke.md), [`RealityKit.md`](RealityKit.md), [`RealityFoundation.md`](RealityFoundation.md),
[`Lottie.md`](Lottie.md), [`RoomPlan.md`](RoomPlan.md)).

**Targets**
- **`*TypeType` naming stutter** — when a raw-value/nested wrapper would append `Type` to a parent already
  ending in `Type`, strip the duplicate. StoreKit2 `OfferTypeTypeType`/`OfferTypeType` (`:8232`,`:9042`),
  Stripe `WalletTypeType` (`StripeApplePay.cs:3846`), AppIntents `PaymentTypeType` (`:32085`), plus BlinkID,
  Nuke, MusicKit, RealityFoundation. Work in `NamespaceFacadeEmitter.cs` / `MetatypeStrategy.cs`.
- **Async wrapper naming** — apply a consistent `Get*Async` rule: WeatherKit `WeatherAsync`→`GetWeatherAsync`
  (`:23220` vs `GetAttributionAsync` `:22875`); Stripe synthesized-vs-native collision `PresentAsync2`
  (`StripePaymentSheet.cs:31051`) should de-dup so the native async name wins; Translation `StatusMethodAsync`;
  MusicKit.
- **Dead-shell suppression** — suppress emission of a nested/extension type when **all** its members are
  skipped (or emit an explanatory XML-doc): Lottie `DotLottieFile.SynchronouslyBlockingCurrentThread`
  (`:14795`), RealityKit empty `*RealityKitExtensions` (`:9500-9528`), Nuke `ImageProcessors.Anonymous` (no
  usable ctor). Work in the type/extension emission gate.
- **Compile-time diagnostics for skipped surface** — emit `[Obsolete(…, IsError:false)]` (and
  `[EditorBrowsable(Never)]` for always-throwing stubs) so consumers get a signal instead of a silent
  `// Unsupported:` comment or a runtime throw: Nuke `ImageDecoderRegistry.Register` (`:3047`, throws
  `NotSupportedException`), any skipped-existential property stub, and — for carriers Session 1 genuinely can't
  proxy — RealityFoundation `ModelComponent.Materials` getter (`:79286`) and RoomPlan `RoomCaptureView.Delegate`
  getter (`:6143-6145`) annotated/`<remarks>`-documented.
- **`IEquatable<T>` synthesis consistency** — emit the `IEquatable<T>` declaration whenever `Equals`/
  `GetHashCode` are synthesized (RealityKit `MultipeerConnectivityService` `:8741-8750` overrides but omits the
  interface → boxing; compare the correct `EntityTranslationGestureRecognizer`).
- **Default-parameter / convenience overloads** — RealityFoundation `AddChild`/`RemoveChild`/
  `RemoveFromParent` emit `preservingWorldTransform = false` default (Swift default; `:120297`).
- **Impl-detail leak hiding** — keep `IExistentialBoxable` / `ExistentialContainer0/1` out of public
  signatures (FamilyControls; Mappedin `ExistentialContainer0` recognized as a void marker).

**Dependencies:** Session 1 (the getter-throws annotations only apply to carriers that remain un-proxied).
**Validation:** `nuke test`; `--compile-only` + `--sim`. `nuke validate` (naming/suppression touches many libs;
confirm baseline only rises, and re-checkout the transient `-behaviortier` version stamp afterward).

---

## Cross-session notes

- **Ordering & dependencies:** 1→11 as written. Hard deps: Session 6 MusicKit `response()` needs Session 4
  (`items`); Session 6 BlinkIDUX async needs Session 5 (BlinkIDUX trampolines); Session 11 getter annotations
  apply only to carriers Session 1 leaves un-proxied.
- **Shared machinery to land once, apply everywhere:** the closed-generic concrete-extension emitter
  (Sessions 4/5/6), the argument-label rename (Session 8), and existential projection (Session 3) each touch
  one core path that several libraries hit — grep for *all* instances of the skip reason before declaring a
  session done.
- **Test depth is the audit's universal finding.** Every session's BindingTests must prove the *end-to-end
  ABI-crossing flow*, not just construction/metadata — that is the whole point of fixing these. Pin every
  verified runtime bug with a test that was red before the fix.
