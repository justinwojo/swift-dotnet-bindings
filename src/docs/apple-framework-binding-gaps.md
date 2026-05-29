# Apple-Framework Binding Gap Audit

**Date:** 2026-05-27
**Scope:** All 16 Apple-framework usage guides in `swift-dotnet-packages/apple-frameworks/` (+ ActivityKit, which has no guide by design). Each guide was authored against, and then re-verified against, the **generated C# bindings** (`apple-frameworks/<X>/obj/{Debug,Release}/net10.0-ios26.2/swift-binding/<X>.cs`), plus each co-located `tests/Tests.cs`.
**Source of truth:** the generated C#. Where the surface emits a member but the member is unusable, the audit quotes the exact `NotSupportedException` message, `[Obsolete(... DiagnosticId)]` reason, or `// Unsupported:` emitter comment, with `file:line`.
**Purpose:** capture every gap that makes a binding broken or not-useful, grouped by **root cause** so each cluster can be triaged for generator-fixability.

> Naming note: `[Obsolete(...)]` reasons that read "Deprecated in Swift" or `[ObsoletedOSPlatform(...)]` are **faithful upstream Apple deprecations**, not binding defects — they are catalogued in §4 (Not gaps) so they are not mis-triaged. The diagnostic IDs `SB0001` (no `@_cdecl` wrapper), `SB0003` (not witness-table-dispatchable), `SB0004` (interface empty — all members skipped), `SB0005` (closure shape not bridgeable) recur throughout and are the generator's own gap markers.

---

## 1. Executive summary

### 1a. Per-framework status at a glance

| Framework | Status | Headline gap | Root causes |
|---|---|---|---|
| **AppIntents** | 🔴 **Primary purpose broken** | No `IAppIntent.perform()`; intents cannot be authored in C# | RC‑STRUCTURAL, RC‑PAT, RC‑CLOSURE |
| **ActivityKit** | ⛔ **WILL NOT SHIP** (known, accepted) | `Activity<Attributes>` needs compiler-synthesized `Codable`+`Hashable` | RC‑STRUCTURAL |
| **RealityFoundation** | 🔴 **Core workflow broken** | Transform setters silently truncate; can't anchor to a Scene | RC‑SIMD, RC‑PROXY, RC‑SB0003, RC‑WILLSET, RC‑AOT, RC‑CLOSURE |
| **RealityKit** | 🔴 **Core workflow broken** | Entity gestures throw; `InstallGestures` throws | RC‑PROXY, RC‑CDECL (inherits RealityFoundation) |
| **ProximityReader** | 🟠 **`requestDocument` absent** | error-description P/Invoke fixed 2026-05-28; `requestDocument` still source-gen territory | RC‑PAT |
| **TipKit** | 🟠 **Authoring broken** | Result-builder DSL + `TipGroup` + SwiftUI views unusable | RC‑AEIC, RC‑CLOSURE, RC‑SB0003, RC‑SWIFTUI |
| **CryptoKit** | 🟠 **Partial** | HPKE, Ed25519 signing, incremental HMAC broken; one-shot AEAD/HMAC/hash work | RC‑GENERIC, RC‑PAT |
| **MusicKit** | 🟠 **Partial** | 3 request types not constructible (charts/library term search) | RC‑MISSING, RC‑SB0003/4 |
| **WeatherKit** | 🟠 **Partial** | Statistics/summaries unobtainable; query overloads absent | RC‑MISSING |
| **WorkoutKit** | 🟠 **Partial** | Range-based alerts not constructible | RC‑MISSING (`ClosedRange`), RC‑SB0003 |
| **LiveCommunicationKit** | 🟢 **Mostly usable** | A few query methods dropped; delegate forward-path works | RC‑MISSING, RC‑SB0003 |
| **FamilyControls** | 🟢 **Mostly usable** | `FamilyActivityPicker` not presentable from C# | RC‑SWIFTUI |
| **RoomPlan** | 🟢 **Usable** | View delegate dead; **session** delegate (the supported path) works | RC‑PROXY, RC‑SB0003 |
| **StoreKit2** | 🟢 **Clean** | — (one upstream OS deprecation only) | — |
| **MatterSupport** | 🟢 **Clean** | — (Codable pruned, `EncodeToJson` replaces) | RC‑CODABLE (benign) |
| **Matter** | 🟢 **Clean** | — (pure ObjC pipeline; 0 stubs) | — |

### 1b. Root-cause clusters, ranked by impact × fixability

| ID | Cluster | Generator-fixability (assessment) | Worst impact |
|---|---|---|---|
| **RC‑SIMD** | SIMD/struct marshalling truncates `Vector3`/`Quaternion`/`Matrix4x4` writes | ✅ **Likely fixable** — marshalling-layer layout bug (12- vs 16-byte, AAPCS register split). Highest value: silent data corruption, no throw. | RealityFoundation transforms |
| **RC‑CDECL‑PARITY** | *Resolved.* Audit-doc claim was stale; both sides already emit. BindingTests fixture `DirectConformanceLocalizedError` is the permanent gate. | ✅ Fixed pre-Session 04 | — |
| **RC‑PROXY** | `EveryProtocol` existential conformance not emitted → getter/delegate throws | ✅ **Likely fixable** — emission feature; same mechanism works elsewhere | RealityKit gestures, RoomPlan view delegate, RF `Scene.AddAnchor` |
| **RC‑GENERIC** | Generic methods emitted only as `[Obsolete]` SB0001 stubs; no concrete specialization | ◑ **Partially fixable** — emit concrete `byte[]`/`Data` overloads (already done for some) | CryptoKit HPKE, Ed25519 sign |
| **RC‑MISSING** | API entirely absent: generic/variadic async initializers, or a type "missing from the type database" (`Swift.ClosedRange`) | ◑ **Mixed** — `ClosedRange` is concrete & fixable; generic-async-init is a capability gap | WeatherKit stats, MusicKit requests, WorkoutKit range alerts |
| **RC‑CLOSURE** | Closure / `@autoclosure` / result-builder params not bridgeable (SB0005) | ◑ **Partially fixable** — some shapes bridgeable via fn-pointers; `() -> [T]` builders harder | TipKit `TipGroup`, AppIntents `add`, RF audio handlers |
| **RC‑SB0003** | Protocol member not witness-table-dispatchable (non-blittable enum/ObjC/optional/stream param) | ◑ **Partial** — forward (C#-implements) path already works; reverse (Swift-vended) is the limit | many delegates; RF 104 stubs |
| **RC‑AOT** | NativeAOT generic-specialization metadata trimmed | ◑ **Possibly** — metadata-preservation/trimmer hints | RF typed mesh buffers (device release) |
| **RC‑PAT** | Generic constrained by protocol-with-associated-types / `Self` requirement | ✗ **Hard** — fundamental Swift-generics limit | CryptoKit HMAC/HKDF, AppIntents `donate`, ProximityReader `requestDocument` |
| **RC‑AEIC** | `@_alwaysEmitIntoClient` — inlined, exports no ABI symbol | ✗ **Not bindable** — no `@_cdecl` target exists | TipKit builder DSL |
| **RC‑SWIFTUI** | SwiftUI `View` types emit only a bridge template, not a callable type | ✗ **Needs new subsystem** (SwiftUI hosting bridge) | FamilyControls picker, TipKit `TipView` |
| **RC‑STRUCTURAL** | Conformances/metadata are Swift-compiler/macro-synthesized; C# can't supply witnesses for types Swift never saw | ✗ **Not fixable in binding generator** — needs a Swift source-generator path | AppIntents, ActivityKit |
| **RC‑WILLSET** | Swift `willSet` observer traps without a live driver (Scene) | ✗ **Runtime/upstream**, not generator | RF `Observable.Transform` setter |
| **RC‑CODABLE** | Synthesized `Codable` `encode`/`init(from:)` pruned (Encoder/Decoder are unresolvable existentials) | n/a **By design** — `EncodeToJson`/`DecodeFromJson` replace | benign everywhere |

**If only three things get fixed**, fix the silent/fatal ones first: **RC‑SIMD** (silent corruption of the most basic 3D operation), **RC‑PROXY** (unblocks gestures, the RoomPlan view delegate, and Scene anchoring), and the remaining **RC‑GENERIC**/CryptoKit hint completions. (RC‑CDECL‑PARITY is already resolved — see §2.)

---

## 2. Gaps by root cause (with evidence)

### RC‑SIMD — SIMD/struct marshalling truncates writes  ✅ likely fixable
The generated PInvoke marshals `System.Numerics.Vector3` (12 bytes, 3 lanes) where Swift expects `SIMD3<Float>` (16 bytes, 4 lanes); the AAPCS register split drops trailing lanes. Quaternion and 4×4 matrix writes lose lanes the same way. **The setters compile and do not throw — they silently corrupt data**, which makes this the most dangerous gap in the audit.

- `RealityFoundation` — `Transform.Translation` / `Transform.Scale` setters, `Transform.Rotation` setter, `new Transform(Matrix4x4)`.
  Evidence: `tests/Tests.cs:178-184` four pins — `"SDK gap: SIMD3<Float> setter marshalling truncates Vector3 to first lane"`, `"simd_quatf setter marshalling drops written Quaternion lanes"`, `"simd_float4x4 init parameter marshalling drops lanes past first"`. Reads round-trip correctly; only writes corrupt.
  Impact: **you cannot position, scale, or rotate an entity from C#** — the foundational RealityKit/RealityFoundation operation.
  Workaround: none for writing.

### RC‑CDECL‑PARITY — *resolved* (audit-doc stale)
The Session 04 spike (2026-05-28) re-ran the generator against `ProximityReader` and verified both sides at runtime: the Swift wrapper *does* emit `@_cdecl("SBW_ProximityReader_MobileDocumentReaderError_get_errorDescription_13178F70")` (`ProximityReader.Wrapper.swift:2688`), matching the C# `[LibraryImport]` (`ProximityReader.cs:15941`). `nm -gU` on both simulator and device dylib slices confirms the symbol is exported. Permanent regression coverage lives at `BindingTests/Sources/SwiftBindingsTestLib/ErrorHandling/DirectConformanceLocalizedError.swift` + `BindingTests/RuntimeTestsApp/ErrorHandling/DirectConformanceLocalizedErrorTests.cs` — a direct-on-decl `LocalizedError` enum (the `MobileDocumentReaderError` shape; sibling to the prior extension-conformance `DemoLocalizedError` fixture). The `ProximityReader/tests/Tests.cs:75-81` skip comment was stale (left behind after the underlying fix landed) — re-enabling the call exercises the working path.

### RC‑PROXY — `EveryProtocol` existential conformance not emitted  ✅ likely fixable
Members that return or accept an existential (`any SomeProtocol`) throw `NotSupportedException("Protocol proxy not available: EveryProtocol conformance was not emitted.")`. The same value-side problem makes a concrete type fail to project a protocol conformance, so an existential box refuses the cast.

- `RealityKit` — `EntityTranslationGestureRecognizer.Entity` / `EntityScaleGestureRecognizer.Entity` / `EntityRotationGestureRecognizer.Entity` getters. Evidence: getter bodies `RealityKit.cs:33`, `:647`, `:900` throw the message above. The recognizer's whole purpose — knowing which entity was gestured — is unreadable.
- `RealityKit` — `ARView.InstallGestures(EntityGestures, IHasCollision)`. Evidence: `RealityKit.cs:7389` throws the message; a real `PInvoke_installGestures_A125CDCA` exists at `:7396` but is unreachable. Installing entity gesture interaction is impossible.
- `RoomPlan` — `RoomCaptureView.Delegate` getter. Evidence: `RoomPlan.cs:5790` throws the message; `IRoomCaptureViewDelegate` callbacks are extension-default stubs (`:6092`, `:6095`). The **view** capture-callback path is dead. (Workaround: the **session** delegate has a real proxy — `RoomCaptureSessionDelegateProxy` `RoomPlan.cs:11539` — and is the supported path.)
- `RealityFoundation` — `Scene.AddAnchor(IHasAnchoring)` (and `AnchorCollection.Append/Remove`). The members exist and are **not** obsolete (`RealityFoundation.cs:82174`, `:81869`, `:81976`), but `AnchorEntity` doesn't project the `IHasAnchoring` conformance, so the existential box refuses it. Pinned `RealityKit/tests/Tests.cs:177`: `"AnchorEntity binding does not project HasAnchoring; existential box refuses the cast"`. **You cannot attach an anchor to a scene** — core AR placement is blocked.

### RC‑GENERIC — generic methods emitted only as SB0001 stubs  ◑ partially fixable
Open-generic methods are emitted with `[Obsolete("No @_cdecl wrapper or native thunk available. P/Invoke calling convention may not match Swift ABI.", DiagnosticId = "SB0001")]` and no concrete specialization. The generator already emits concrete `byte[]`/`Foundation.Data` overloads for *some* of these (e.g. AES.GCM `Seal`, P256/384/521 signing) — the gap is incompleteness.

- `CryptoKit` — **HPKE** `HPKE.Sender.Seal<…>` (`CryptoKit.cs:19341`, `:19395`), `HPKE.Recipient.Open<…>` (`:19680`, `:19734`). Only generic stubs; no concrete overload. **The entire HPKE encrypt/decrypt workflow is unusable.** (Note: `HPKE.*.ExportSecret(byte[]/Data)` `:19447/:19461` and `Decapsulate(byte[]/Data)` `:22762` etc. **do** get concrete overloads and work — the guide overstated these as obsolete.)
- `CryptoKit` — **AEAD open-with-AAD**: `AES.GCM.Open<TAD>` (`:17050`), `ChaChaPoly.Open<TAD>` (`:16160`) are generic-only, though `Seal`-with-AAD has concrete overloads (`:17159`). So you can seal with associated data but cannot decrypt-and-verify it on the working path.
- `CryptoKit` — **Ed25519 signing**: `Curve25519.Signing.PrivateKey.Signature<D>` (`:269`) is generic-only → cannot **produce** Ed25519 signatures (verification works: `PublicKey.IsValidSignature(byte[]/Data,…)` `:541` is concrete).
- `CryptoKit` — **context-string sign/verify**: P256/etc. `Signature<D,C>` (`:21252`) / `IsValidSignature<S,D,C>` (`:20676`) have no concrete overload.
  Workaround for all: perform these operations in a Swift companion target.

### RC‑MISSING — required API entirely absent  ◑ mixed
Two sub-flavors.

**(a) Generic/variadic `async` request initializers & service methods skipped.** Result *types* are emitted but no method produces them (orphaned), or a request class has `ResponseAsync()` but no constructor.
- `WeatherKit` — `WeatherService.weather(for:including:)` query overloads absent (only `WeatherAsync(CLLocation)` exists, `:20095`). The `WeatherQuery<T>` selectors (`:11712`+) are emitted but unconsumed. Guide attributes the skip to *"closure or async in generic type member."* PARTIAL — fetch the full `Weather` bundle as a workaround.
- `WeatherKit` — `WeatherService.dailyStatistics`/`monthlyStatistics`/`hourlyStatistics`/`dailySummary` have **no producing method**. Types like `MonthPrecipitationStatistics` (`:35`), `DayTemperatureStatistics` (`:5305`) are constructible only via `DecodeFromJson`, never returned by the framework. **BROKEN** — the historical-statistics workflow cannot be performed; no workaround.
- `MusicKit` — no public constructor / factory for `MusicCatalogSearchRequest` (`:37703`), `MusicLibrarySearchRequest` (`:4774`), `MusicCatalogChartsRequest` (`:31468`). Each has a working `ResponseAsync()` but cannot be created. **BROKEN** for charts & library term search; catalog term search has a workaround (`MusicCatalogSearchSuggestionsRequest.Create_C11D4260(term)` `:36358`). Root: Swift `init(term:types:)` (term + variadic result-kind) not emitted.

**(b) A required type is "missing from the type database."**
- `WorkoutKit` — `HeartRateRangeAlert`/`CadenceRangeAlert`/`PowerRangeAlert`/`SpeedRangeAlert` have no constructor: `WorkoutKit.cs:309/4650/6821/8976` emit `// Unsupported: method 'init' — parameter or return type not yet supported`, and the factories carry `[UnsupportedSwiftType("Type is missing from the type database", "Swift.ClosedRange")]` (`:4664`+). **`Swift.ClosedRange<…>` is concrete and a tractable fix.** PARTIAL — threshold/zone alerts (`HeartRateZoneAlert`, `PowerThresholdAlert`, …) have ctors and work.

**(c) Misc dropped methods (unsupported placeholder / unsupported module).**
- `LiveCommunicationKit` — `ConversationHistoryManager.recentConversations(Predicate)` dropped: `:7783` `"generic constraint on SwiftUI View type … 'Foundation.Predicate<…>'"`. PARTIAL (can mark-read + decode from JSON, can't run the predicate query). Also `ConversationManager.pendingConversationActions(...)` (`:6022`, MINOR — `PendingActions` property exists) and `ConversationHistoryDidUpdate.makeMessage(...)` (`:7778`, MINOR).

### RC‑CLOSURE — closure / autoclosure / result-builder params not bridgeable (SB0005)  ◑ partially fixable
Bodies throw `NotSupportedException("Closure parameter shape not yet bridgeable from C#. This API is exposed for visibility only and cannot be invoked.")` with `[Obsolete(... DiagnosticId="SB0005")]`.

- `TipKit` — `TipGroup(Priority, builder)` ctor: `TipKit.cs:825-828`, `[UnsupportedSwiftType("Unsupported closure fallback", "() -> Swift.Array<any TipKit.Tip>")]`. Can't build a `TipGroup` in C#.
- `RealityFoundation` — `PlayAudio(AudioGeneratorConfiguration, generatorRenderHandler)` / `PrepareAudio(...)`: `:60877`, `:105873`, `:105880` throw the SB0005 message (render handler `@escaping (…AudioBufferList) -> OSStatus`). The `PlayAudio(AudioResource)` overload (`:105884`) works.
- `AppIntents` — `AppDependencyManager.add(...)`: `:20278` `"closure signature not yet supported … unsupported closure type that cannot be marshalled"`; `AppDependency<TValue>` closure ctor: `:2199` SB0005 + `[UnsupportedSwiftType(... "@escaping @autoclosure () -> Value")]`.

### RC‑AEIC — `@_alwaysEmitIntoClient` exports no ABI symbol  ✗ not bindable
Swift-stdlib result-builder methods are inlined into each caller and export no stable symbol, so there is no `@_cdecl` target to bind.
- `TipKit` — the entire result-builder DSL: `Tips.ActionBuilder`/`OptionsBuilder`/`GroupBuilder` `BuildExpression`/`BuildBlock`/`BuildEither`/`BuildPartialBlock`/`BuildArray`/`BuildOptional`/`BuildLimitedAvailability`/`BuildFinalResult` — 19 SB0001 stubs (`TipKit.cs:4219-4435`, `:5059-5329`, `:5866`). The `Rule`/`RuleBuilder`/`ActionBuilder`/`OptionsBuilder`/`GroupBuilder` types are empty shells. **You cannot assemble tip rules/options/groups in C#.** Workaround: author them in a Swift companion target and publish concrete `Tips.Rule` values.

### RC‑SB0003 — protocol member not witness-table-dispatchable  ◑ partial (forward path works)
The member's param/return type is a non-frozen Swift enum, ObjC class, optional/collection existential, or async stream, so it can't dispatch through the witness table. Two observable behaviors: (1) `"Use a concrete type instead"` — works if you have the concrete type; (2) Swift-backed existential — **the forward direction (you implement the interface in C#, Swift calls in) works**; only the reverse (calling the member on a proxy that wraps a Swift value) throws.

- `RoomPlan` — `IRoomCaptureSessionDelegate.CaptureSession(session, Instruction)` (`:11786`) and `CaptureSession(session, CapturedRoomData, AnyError?)` (`:11818`): SB0003 (`"non-dispatchable type 'RoomCaptureSession.Instruction'"`, `"'Swift.Optional<any Swift.Error>'"`). **Forward path works** (the guide's C#-implements-delegate approach); the `Configuration`/`CapturedRoom` overloads dispatch natively.
- `LiveCommunicationKit` — `IConversationManagerDelegate.ConversationManager(…, AVAudioSession)`: SB0003 `:9282` (`"non-dispatchable type 'AVFoundation.AVAudioSession'"`). Forward path is fully wired (`Receive_conversationManager_4` `:9118` marshals via `GetNSObject<AVAudioSession>`).
- `WorkoutKit` — `WorkoutAlertProxy.Metric` (`:10752`, `"'WorkoutAlertMetric' is not dispatchable"`) and `Supports(activity, location)` (`:10765`, `"'HealthKit.HKWorkoutActivityType'"`). Only affects reading off a bare `IWorkoutAlert` existential; concrete alert types work.
- `MusicKit` — 12 `[Obsolete(... SB0003)]` filter-proxy getters in `MusicKit.SwiftInterop` (`:54705` etc., e.g. `"'Swift.Optional<MusicItemCollection<Genre>>' is not dispatchable via witness table"`) + 27 `"Inherited protocol member — dispatch via parent protocol proxy."` MINOR — interop-namespace plumbing, use the concrete item type.
- `TipKit` — `ITip.Status` (`:9788`), `Invalidate` (`:9852`), `Message`/`Image`/`StatusUpdates`/`ShouldDisplayUpdates`/`ResetEligibilityAsync` throw on a Swift-backed tip (the normal case). (`ITip.ShouldDisplay` `:9801` and `ITip.Options` `:9766` **do** dispatch — the guide listed `ShouldDisplay` as throwing, which is inaccurate; see §5.)
- `RealityFoundation` — **104 protocol-extension-default stubs**: 63 properties `NotSupportedException("This property uses a Swift protocol extension default. Access it on the concrete type instead.")` + 41 methods (`"…Call it on the concrete type instead."`), plus `BlendWeight`-typed SB0003 member (`:189430`). Throw through the interface; work on the concrete type.

### RC‑AOT — NativeAOT generic-specialization metadata trimmed  ◑ possibly fixable
- `RealityFoundation` — `MeshBuffer<T>`, `MeshBuffers.Semantic<T>` (`Positions`/`Normals`/`Tangents`), `UnsafeForceEffectBuffer<T>`. Works on the Mono interpreter (simulator), fails on NativeAOT (device release). `tests/Tests.cs:73-124` runtime-gates with `Skip("…","SDK gap: constraint-relaxation generic metadata not reachable on this runtime")` when `!IsDynamicCodeSupported`.

### RC‑PAT — generic constrained by protocol-with-associated-types / `Self`  ✗ hard
The generator flags these explicitly with `// Unsupported: … protocol with associated types used as constraint (Method has constraints on protocols with associated types or self requirements.)` and drops the member entirely.
- `CryptoKit` — `HMAC<H>(SymmetricKey)` ctor (`:2283`; comment `:2282` cites `isValidAuthenticationCode` PAT constraint) → incremental HMAC unavailable. `SharedSecret.hkdfDerivedSymmetricKey`/`x963DerivedSymmetricKey` entirely absent (`:18194-18195`). Workarounds: one-shot `HMAC{SHA256}CsmExtensions.AuthenticationCode(...)` (`:2325`) and `SymmetricKey.FromCryptoKit_SharedSecret(secret)` (`:17783`, default derivation only).
- `ProximityReader` — `MobileDocumentReaderSession.requestDocument(...)` entirely absent (`:16078`). You can `MobileDocumentReader.PrepareAsync`/`GetConfigurationAsync` but **cannot perform the read** — the mobile-document workflow is uncompletable. No workaround.
- `AppIntents` — `IntentDonationManager.donate(...)` not bound (`:14608`, 4×). The `Shared` singleton is reachable but has nothing useful to call.

### RC‑SWIFTUI — SwiftUI `View` types emit only a bridge template  ✗ needs new subsystem
SwiftUI `View`-conforming types are not bound as callable C# types; a `*.SwiftUIBridge.cs` template is emitted that "requires manual completion before use." The corresponding `IView<TSelf>` interface is empty (`[Obsolete(... SB0004 …)]`, all members skipped).
- `FamilyControls` — `FamilyActivityPicker` not presentable: `FamilyControls.cs:1110` `"SwiftUI View type (bridge file generated instead)"`; `FamilyControls.SwiftUIBridge.cs:441` is a commented stub. You can construct/read/persist/apply a `FamilyActivitySelection` obtained elsewhere, but cannot drive the picker that produces one. (Display-only `FamilyActivityTitleViewSession`/`IconViewSession` exist.)
- `TipKit` — `TipView`, `PopoverTipView` not generated (`TipKit.SwiftUIBridge.cs` is a template); `IView<TSelf>` empty (`:9200`). UIKit path works: `TipUIPopoverViewController(ITip, sourceItem, Action<Tips.Action>)` (`:8548`).
- `MusicKit` — `IMusicPropertyContainer` empty (`:14979`, SB0004, "All 4 protocol member(s) were skipped") — read properties on concrete item types instead.

### RC‑STRUCTURAL — compiler/macro-synthesized conformances can't be supplied from C#  ✗ not fixable in the binding generator
Swift synthesizes protocol conformance witnesses (and macro-expanded metadata) at compile time for the user's own types. C# can't supply those witnesses for types the Swift compiler never saw. **This requires a Swift source-generator path, not a binding-generator change.**
- `AppIntents` — `IAppIntent<TSelf>` has **no `perform()` / `PerformAsync`** (`AppIntents.cs:3418-3453` body has only metadata properties). The 18 `static virtual` metadata members throw `NotSupportedException("Static protocol members must be accessed on concrete types, not through the protocol interface.")` (`:3426` etc.). The only `PerformAsync` in the file (`:29346`) belongs to the built-in `EmptySnippetIntent`, not a user-authorable intent. **Siri/Shortcuts intents cannot be authored in C#** — the package is useful only for its value types (`DisplayRepresentation`, `IntentDialog`, `OpenURLIntent`, etc., which do work). 977 `// Unsupported:` drops corroborate the pervasiveness.
- `ActivityKit` — `Activity<Attributes>` requires the user's attributes type to conform to `Codable`+`Hashable`, which Swift synthesizes; C# can't provide working witnesses. **This is a known, accepted limitation: ActivityKit is on the do-not-ship list and will not be published to nuget.org** — it is *not* a generator fix target (a Swift source-generator path would be required). See `apple-frameworks/ActivityKit/README.md`. Same class of limitation as AppIntents.

### RC‑WILLSET — Swift `willSet` observer traps without a live driver  ✗ runtime/upstream
- `RealityFoundation` — `Entity.Observable.Transform` (and `.Position`/`.Scale`) setter on a **detached** entity → `EXC_BREAKPOINT` hard crash (uncatchable). `tests/Tests.cs:292` pins it: `"Observable.Transform setter traps in RealityKit ecs2 willSet without an attached Scene"`. Reads work. Only set transforms on entities attached to a running Scene.
- **Consumer-side preflight pattern.** No ABI route bypasses a property observer, so the generator cannot intercept the trap. The safe consumer pattern is to check the public `Entity.Scene` predicate before any `Observable.Transform`/`.Position`/`.Scale` setter and surface a clear `InvalidOperationException("Cannot set Transform on a detached entity; attach to a Scene first.")` instead of letting the native `willSet` fire:
  ```csharp
  if (entity.Scene is null)
      throw new InvalidOperationException(
          "Cannot set Transform on a detached entity; attach to a Scene first.");
  entity.ObservableValue.Transform = newTransform;
  ```
  The RealityFoundation smoke test demonstrates this guard at `tests/Tests.cs` (Entity.ObservableValue.Transform write block) — read-only access keeps working unchanged.

### RC‑CODABLE — synthesized `Codable` pruned by design  n/a (benign)
`encode(to:)` / `init(from:)` are pruned because `Encoder`/`Decoder` are unresolvable existential protocols; each affected value type ships `EncodeToJson()` / static `DecodeFromJson(byte[])` as the sanctioned replacement. Seen in WeatherKit, FamilyControls (`:1045`), MatterSupport (19×), LiveCommunicationKit (`:824` etc.). **Not a capability loss.**

---

## 3. Per-framework appendix (concise index)

Severity legend: **BROKEN** = surfaced but throws/unusable, or a core workflow can't be completed · **PARTIAL** = works only via a workaround/one path · **MINOR** = cosmetic/rare.

### AppIntents 🔴
- BROKEN — no `IAppIntent.perform()`; can't author intents (RC‑STRUCTURAL). `AppIntents.cs:3418`.
- BROKEN — `IntentDonationManager.donate` unbound (RC‑PAT) `:14608`; `AppDependencyManager.add` unbound (RC‑CLOSURE) `:20278`.
- PARTIAL — `AppDependency` closure ctor throws (RC‑CLOSURE) `:2199`; `@Parameter` constrained-extension props unbound `:5148`.
- Works: `DisplayRepresentation`, `IntentDescription`, `IntentDialog`, `IntentCurrencyAmount`, `OpenURLIntent`, manager `Shared` accessors, enums.

### ActivityKit ⛔ WILL NOT SHIP
- **Known, accepted — not a fix target.** ActivityKit is on the do-not-ship list and will not be published to nuget.org. Structural: `Activity<Attributes>` Codable+Hashable synthesis (RC‑STRUCTURAL); needs a Swift source-generator path, not a binding-generator change. See `apple-frameworks/ActivityKit/README.md`.

### RealityFoundation 🔴
- BROKEN — transform setters truncate (RC‑SIMD) `tests/Tests.cs:178`.
- BROKEN — `Scene.AddAnchor(IHasAnchoring)` refused (RC‑PROXY) `:82174`.
- BROKEN — `Observable.Transform` setter traps (RC‑WILLSET) `tests/Tests.cs:292`.
- PARTIAL — typed mesh buffers NativeAOT-only-fail (RC‑AOT); `ComponentSet.Has/Remove(object)` SB0001 `:101363` (typed `Set` works); 104 protocol-extension-default stubs (RC‑SB0003); `PlayAudio`/`PrepareAudio` render handlers throw (RC‑CLOSURE) `:105873`.
- BROKEN (not in guide) — `IActionHandlerProtocol<TSelf>` empty (SB0004) `:56553`; `ActionEvent` tombstoned out of binding.
- MINOR — Swift/visionOS deprecations (`playAnimation(named:)`, `resetPhysicsTransform`, reanchor).

### RealityKit 🔴
- BROKEN — gesture `.Entity` getters throw (RC‑PROXY) `:33/:647/:900`; `InstallGestures` throws `:7389`.
- PARTIAL — `MultipeerConnectivityService.Owner(Entity)` SB0001 `:7700`.
- MINOR — 2-arg `ARView` ctor deprecated `:6939`; `RenderOptionsType` legacy aliases deprecated `:6076/:6210`.
- Inherits all RealityFoundation gaps. The `ARView`/Scene/Environment read surface works.

### ProximityReader 🔴
- BROKEN — `MobileDocumentReaderSession.requestDocument` absent (RC‑PAT) `:16078`. (`MobileDocumentReaderError.GetErrorDescription()` `:15926` was previously listed here under RC‑CDECL‑PARITY but the Session 04 spike confirmed the wrapper symbol is emitted and works at runtime — see §2.)
- MINOR — `ObsoletedOSPlatform` event-handler / `vasMerchants` overloads (upstream deprecations).

### TipKit 🟠
- BROKEN — result-builder DSL (RC‑AEIC) `:4219`+; `TipGroup(builder)` ctor (RC‑CLOSURE) `:825`; `TipView`/`PopoverTipView` (RC‑SWIFTUI); `ITip` Status/Invalidate/etc. through interface (RC‑SB0003) `:9788`.
- PARTIAL — `AnyTip` only obtainable from Swift `:570`.
- Works: `Tips.Configure`, status/invalidation enums, frequency/option presets, UIKit presentation (`TipUIPopoverViewController`), `TipKitError` singletons.

### CryptoKit 🟠
- BROKEN — HPKE Seal/Open (RC‑GENERIC) `:19341`+; Ed25519 signing (RC‑GENERIC) `:269`; incremental `HMAC<H>` ctor + HKDF/X9.63 (RC‑PAT) `:2283`,`:18194`; context-string sign/verify (RC‑GENERIC) `:21252`.
- PARTIAL — AEAD open-with-AAD generic-only `:17050`; no one-shot `SHA256.Hash(data)`; no `SealedBox(nonce:ciphertext:tag:)` ctor.
- Works (confirmed Tests 26/27): AES.GCM/ChaChaPoly seal↔open via byte[]/Data; one-shot HMAC helpers; P256/384/521 sign+verify; incremental hashing; `SymmetricKey.FromCryptoKit_SharedSecret`.

### MusicKit 🟠
- BROKEN — `MusicCatalogChartsRequest` / `MusicLibrarySearchRequest` not constructible (RC‑MISSING) `:31468/:4774`.
- PARTIAL — `MusicCatalogSearchRequest` not constructible (use `…SearchSuggestionsRequest.Create_C11D4260`) `:37703/:36358`.
- MINOR — `IMusicPropertyContainer` empty (SB0004) `:14979`; SB0003 filter-proxy plumbing; `SystemMusicPlayer` iOS-only.

### WeatherKit 🟠
- BROKEN — statistics/summaries have no producing method (RC‑MISSING); types orphaned.
- PARTIAL — `weather(for:including:)` query overloads absent (use full-bundle `WeatherAsync`).
- MINOR — per-property Codable pruned (use `EncodeToJson`/`DecodeFromJson`). `Measurement<T>` value-only is by design.

### WorkoutKit 🟠
- PARTIAL — range alerts (`HeartRate/Cadence/Power/SpeedRangeAlert`) not constructible (RC‑MISSING, `Swift.ClosedRange`) `:309`+ — threshold/zone alerts work.
- MINOR — `IWorkoutAlert` static range factories + proxy `Metric`/`Supports` (RC‑SB0003) `:10752`; `WorkoutGoal.PoolSwimDistanceWithTime` not constructible (readable via `TryGet*`).

### LiveCommunicationKit 🟢
- PARTIAL — `ConversationHistoryManager.recentConversations(Predicate)` dropped (RC‑MISSING) `:7783`; audio-session delegate reverse-dispatch SB0003 `:9282` (forward path works).
- MINOR — `pendingConversationActions` `:6022`, `makeMessage` `:7778` dropped; Codable pruned.

### FamilyControls 🟢
- PARTIAL — `FamilyActivityPicker` not presentable from C# (RC‑SWIFTUI) `:1110`.
- MINOR — Codable pruned (`EncodeToJson`/`DecodeFromJson` work) `:1045`; `IView<TSelf>` empty `:1628`. `FamilyControlsError.GetErrorDescription()` works.

### RoomPlan 🟢
- BROKEN — `RoomCaptureView.Delegate` view-callback path dead (RC‑PROXY) `:5790`.
- PARTIAL — session-delegate `Instruction`/`(data,error)` SB0003 `:11786/:11818` — **forward C#-implements path works** and is the supported capture mechanism.
- MINOR — static protocol member through interface throws `:373`.

### StoreKit2 🟢
- MINOR only — `Transaction.CurrentEntitlementAsync(productID)` `[ObsoletedOSPlatform("ios18.4")]` (use `CurrentEntitlementsMethod` `:13938`). Full purchase lifecycle + AppStore UI helpers verified present.

### MatterSupport 🟢
- MINOR — synthesized Codable pruned (RC‑CODABLE), 19× `:768`+; `EncodeToJson`/`DecodeFromJson` ship as the path. Cross-module Matter ref (issue #38) round-trips. `PerformAsync` works (drives system UI, device-only).

### Matter 🟢
- No binding gaps. Pure ObjC pipeline: 0 `NotSupportedException`, 0 `[Obsolete]`. Documented "limitations" are runtime/scope caveats (device + storage delegate needed to exercise commissioning).

---

## 4. Not gaps (don't mis-triage)

These surfaced during the audit and are **faithful upstream/by-design behavior**, not binding defects:
- **Upstream Apple deprecations** carried over verbatim: `[Obsolete("Deprecated in Swift..")]` and all `[ObsoletedOSPlatform(...)]` (StoreKit2 `CurrentEntitlementAsync`, ProximityReader event-handler/`vasMerchants`, RealityKit `ARView` 2-arg ctor + render aliases, RF `playAnimation(named:)` etc.). Non-deprecated replacements exist.
- **SwiftUI-vended runtime objects**: `Translation.TranslationSession` is bound and callable but only becomes *functional* once SwiftUI's `.translationTask` wires it to a context — so end-to-end translation can't be exercised purely from C#. The request/response/availability/error value types are fully usable. (No generator issue.)
- **Platform restrictions**: `MusicKit.SystemMusicPlayer` is iOS-only (Apple); use `ApplicationMusicPlayer` elsewhere.
- **Device/entitlement-gated UI**: `MatterAddDeviceRequest.PerformAsync`, `MTRDeviceController` commissioning, FamilyControls picker — require real hardware/entitlements to run, but the bindings are correct.
- **Synthesized `Codable` pruning** (RC‑CODABLE) — replaced by `EncodeToJson`/`DecodeFromJson`.
- **`Measurement<T>` exposing only `.Value`** (WeatherKit) — Foundation type behavior, by design.

## 5. Guide-accuracy follow-ups discovered during this audit

Two of the shipped guides slightly overstate breakage (the generated C# is *better* than the guide claims). Worth a quick correction pass:
- **CryptoKit guide** groups HPKE `ExportSecret` / `Decapsulate` as obsolete, but both have **working** concrete `byte[]`/`Data` overloads (`CryptoKit.cs:19447`, `:22762`). Only HPKE `Seal`/`Open` are broken.
- **TipKit guide** lists `ITip.ShouldDisplay` among the throwing protocol defaults, but `ShouldDisplay` (`:9801`) and `Options` (`:9766`) **do** dispatch via real witness-table thunks. Only `Status`/`Invalidate` (and the SwiftUI-typed members) throw.

---

## 6. Fixability gameplan (2026-05-27 research pass)

This section is the output of a no-code investigation that traced every cluster in §1b/§2 to the actual generator/runtime code, cross-checked the "not fixable" verdicts with **Codex** and **Grok** (independent consults), and read the live `roadmap.md` + `Design/apple-framework-portfolio.md`. The headline correction: **RC‑PAT is overstated** — for CryptoKit it is the *same* monomorphization mechanism as RC‑GENERIC (a coverage/hint gap, not a fundamental wall). Three "✗" clusters (RC‑AEIC, RC‑STRUCTURAL, RC‑WILLSET) are confirmed correctly diagnosed.

Effort key: **S** = data/config, ~1 file, hours · **M** = emitter/runtime change following an established in-repo pattern, days · **L** = new subsystem or architectural change · **✗** = not worth doing / not possible as a generator change.

### 6a. Revised fixability table

| Cluster | §1b verdict | Revised verdict | Effort | Where the fix lands |
|---|---|---|---|---|
| **RC‑SIMD** | ✅ likely | ✅ **Confirmed P0** | **M** | `WrapperValidation.IsNonPrimitiveFrozenStructParam` + `CdeclParamMapper.Map` (simd branch) — route simd vectors through the pointer/indirect path, not by-value |
| **RC‑CDECL‑PARITY** | ✅ already resolved | — | — | Session 04 spike (2026-05-28) confirmed parity holds; permanent gate is the `DirectConformanceLocalizedError` BindingTests fixture |
| **RC‑PROXY (A: Scene.AddAnchor)** | ✅ likely | ✅ **Surgical** | **M** | `TypeHandlerHelpers.ShouldEmitConformance:1446-1452` — split "emit conformance-descriptor dict entry / `IExistentialBoxable`" from "emit interface declaration" |
| **RC‑PROXY (B: gestures, class-rooted protos)** | ✅ likely | ◑ **Larger** | **L** | `EveryProtocolEmitter` — needs `EveryEntityProtocol : Entity` analogous to existing `EveryObjCProtocol : NSObject` (one superclass limit) |
| **RC‑GENERIC** | ◑ partial | ✅ **Largely fixable** | **S–M** | `Data/specialization-hints.json` — add `ContiguousBytes`/`AuthenticatedData` conformers; CSM cartesian product already handles multi-param once hints exist |
| **RC‑PAT (CryptoKit HMAC/HKDF)** | ✗ hard | ⚠️ **Overstated — fixable** | **M** | Same CSM path as RC‑GENERIC + relax `ConcreteProtocolSpecializationEmitter.Sync.cs:42` ctor filter; complete `HashFunction` hints |
| **RC‑PAT (AppIntents donate / ProximityReader requestDocument)** | ✗ hard | ✗ **unless conformers are Apple-finite** | **L / ✗** | CSM works only if the legal conformer set is known at bind time; app-defined → source-gen territory (verify per-API) |
| **RC‑MISSING (`ClosedRange`)** | ◑ mixed | ✅ **Fixable, mechanical** | **M** | Mirror `SwiftArray`: `SwiftDatabase.xml` + `SwiftClosedRange<Bound>` runtime class + `BoundGenericsHandler.s_stdlibGenerics` + `BareGenericGuardStrategy` + `ILLink.Descriptors.xml`. Unblocks WorkoutKit range alerts |
| **RC‑MISSING (MusicKit `init(term:types:)`)** | ◑ mixed | ◑ **Fixable via shim** | **M** | Variadic generic parameter pack (`repeat each MusicType`) → emit a Swift array-flattening wrapper overload |
| **RC‑MISSING (WeatherKit stats / `weather(for:including:)`)** | ◑ mixed | ✗ **hard** | **L** | 6-way method-own generic `async` tuple return; blocked by CSM `MaxCsmCartesianProductSize` cap |
| **RC‑CLOSURE (PlayAudio render handler)** | ◑ partial | ◑ **Tractable** | **S–M** | Register `Darwin.OSStatus`/`AudioBufferList` in TypeDB, or short-circuit `IsPointerType` so any `Unsafe*Pointer<T>` (→ `IntPtr`) passes the closure gate |
| **RC‑CLOSURE (`@autoclosure () -> Value`)** | ◑ partial | ◑ **Medium** | **M** | Treat `@autoclosure () -> Concrete` like `() -> Concrete`; CSM-specialize when `Value` is a parent generic |
| **RC‑CLOSURE (`() -> [T]` builder)** | ◑ partial | ◑ **Harder** | **L** | Result-builder return; needs per-conformer specialization or existential container |
| **RC‑SB0003** | ◑ partial | ◑ **Forward works; reverse case-by-case** | varies | Reverse witness dispatch for non-blittable enum/ObjC/optional/stream members; many are by-design Swift limits |
| **RC‑AOT** | ◑ possibly | ✅ **Fixable, emitter pattern** | **M** | Emitter synthesizes `SwiftArray.TryEagerInitialize`-style eager `cctor` + `ILLink`/`[DynamicDependency]` for generated generic `ISwiftObject` types |
| **RC‑AEIC** | ✗ not bindable | ✅ **Confirmed** (symbol shimmable, feature not) | ✗ | A forwarding `@_cdecl` shim can expose raw `build*` entrypoints, but result-builder DSL *authoring* in C# is not restorable — not worth it |
| **RC‑SWIFTUI (FamilyActivityPicker)** | ✗ new subsystem | ◑ **Reframed — bridge exists** | **M** | Bridge already returns a typed `UIViewController`; FamilyActivityPicker becomes pure once non-optional `Binding<Struct>` lands ("not a fundamental limit" — portfolio doc) |
| **RC‑SWIFTUI (general composition)** | ✗ new subsystem | ✗ **Confirmed** | **L** | `@ViewBuilder`/result-builder wall; KeyPath-binding productionization already *declined* in roadmap |
| **RC‑STRUCTURAL** | ✗ not fixable | ✅ **Confirmed** | **L** (different product) | AppIntents `perform()` / ActivityKit need a C#→Swift source-gen + macro-expansion + secondary binding subsystem. Portfolio doc lists AppIntents as a **hard skip** |
| **RC‑WILLSET** | ✗ runtime | ✅ **Confirmed** | ✗ (guardrail S) | Framework `willSet` precondition; no ABI route bypasses a property observer. Only a best-effort C#/Swift preflight guard + docs |
| **RC‑CODABLE** | n/a by design | ✅ **Confirmed by design** | — | `EncodeToJson`/`DecodeFromJson` are the sanctioned replacement |

### 6b. Per-cluster detail & root cause confirmed in code

**RC‑SIMD (P0).** Mapping lives in `Swift.Runtime/.../SimdDatabase.xml` (`simd_float3`→`Vector3`, `simd_quatf`→`Quaternion`, `simd_float4x4`→`Matrix4x4`). Because every simd record hits `CdeclParamMapper.IsSystemFrozenStruct` (`module=="simd"`), `WrapperValidation.IsNonPrimitiveFrozenStructParam` returns `false` and the setter parameter is passed **by value**. The corruption is a **register-class mismatch**, deeper than the documented "12 vs 16 byte" framing: Swift's `simd_floatN` is a Clang `ext_vector_type` passed in a *single* 128-bit NEON register (`v0`), while .NET passes `System.Numerics.Vector3`/`Quaternion` as an **HFA** spread across separate single-float registers (`s0,s1,s2,…`) — so only lane 0 aligns. This is why the test pins show `simd_quatf` (16 B = 16 B, *no* size mismatch) *also* losing lanes. **Implication for the fix: gate on "is a simd vector type," not on size mismatch**, or Quaternion stays broken. The fix forces the indirect/pointer path (C# `stackalloc` 16/64-byte buffer + `MarshalToSwift`; Swift `UnsafeRawPointer` + `.assumingMemoryBound(...).pointee`), which sidesteps register classification because memory layout is unambiguous. Reads already work (struct returns go through `resultPtr`). Must ship with a BindingTests round-trip per type on **both** sim (Mono) and device (NativeAOT).

**RC‑CDECL‑PARITY — already resolved.** Session 04 spike (2026-05-28) re-ran the generator against ProximityReader and verified both sides at runtime: Swift wrapper at `ProximityReader.Wrapper.swift:2688` emits `@_cdecl("SBW_ProximityReader_MobileDocumentReaderError_get_errorDescription_13178F70")`, C# `[LibraryImport]` at `ProximityReader.cs:15941` references the same entry point, and `nm -gU` on both `ios-arm64-simulator` and `ios-arm64` (device) dylib slices shows the symbol exported. The audit claim was stale (carried over from the `ProximityReader/tests/Tests.cs:75-81` skip comment, which itself was left behind after the underlying fix landed). Permanent regression coverage is the direct-on-decl `LocalizedError` fixture at `BindingTests/.../ErrorHandling/DirectConformanceLocalizedError.swift` + matching runtime test — sibling to the extension-conformance `DemoLocalizedError` fixture that pinned the original fix.

**RC‑PROXY.** Two separable modes. **Failure A** (`Scene.AddAnchor(IHasAnchoring)`): `TypeHandlerHelpers.ShouldEmitConformance:1446-1452` skips cross-module conformances whose protocol has members, so `AnchorEntity`'s `_protocolConformanceSymbols` dict never gets the `IHasAnchoring` descriptor symbol and the existential box can't resolve the witness table. But that descriptor is only needed for `swift_getWitnessTable` — it does **not** require C# member stubs. Surgical fix: split `ShouldEmitForDictionary` from `ShouldEmitForInterface` (~6 call sites in `TypeHandlerHelpers.cs`/`ClassHandler.cs`, no Swift change). **Failure B** (gesture `.Entity` getters, `InstallGestures`): the protocol is class-rooted (`HasAnchoring : Entity`), and `EveryProtocol` can hold only one superclass, so `EveryProtocolEmitter` skips the conformance (`HasClassSuperclassRequirement`). Needs an `EveryEntityProtocol : Entity` mirroring the proven `EveryObjCProtocol : NSObject` path — larger, new generated Swift class + P/Invokes.

**RC‑GENERIC + RC‑PAT (CryptoKit) — the same engine.** The repo has a `ConcreteSpecializationEngine` (CSM) that monomorphizes PAT/Self-constrained generics into one concrete C# overload + one `SBW_CSM_*` `@_cdecl` shim per known conformer. AES.GCM.`Seal` works because `DataProtocol` conformers (`Foundation.Data`, `[UInt8]`) are in `specialization-hints.json`; `Open<TAD>`/Ed25519 `Signature<D>`/HMAC<H> fail because the constraining protocol (`ContiguousBytes`, `AuthenticatedData`, full `HashFunction` set) has **no/incomplete hint entries**, so the engine finds zero conformers and the member falls to the SB0001/drop gate at `MemberValidationPipeline.cs:298-302`. Parent-generic constructors (`HMAC<H>(SymmetricKey)`) are additionally filtered by `if (method.IsConstructor) return false` at `ConcreteProtocolSpecializationEmitter.Sync.cs:42`. **Fix = add hint entries (+ associated-type maps) and relax the ctor filter** — both Codex and Grok independently flagged this. *Caveat to validate during implementation:* HPKE `Seal`/`Open` may carry additional/multi generic params; confirm the conformer set resolves before assuming a pure hint fix.

**RC‑PAT (AppIntents `donate` / ProximityReader `requestDocument`).** CSM only works when the legal conformer set is finite and visible at bind time. If these constrain over **app-defined** types (consumer's own entities/request types), they fall outside intra-module conformer discovery (`roadmap.md:59` cross-assembly limitation) and become source-gen territory. Determine the conformer source per-API before scoping.

**RC‑MISSING (`ClosedRange`).** No `Swift.ClosedRange` (or `Swift.Range`) entry exists in `SwiftDatabase.xml`; resolution falls through all 13 strategies to `TryGetAnyTypeFallbackInfo` → "Type is missing from the type database." Mechanical fix mirroring `SwiftArray`/`SwiftOptional`: XML record (two-field `lowerBound`/`upperBound`), `SwiftClosedRange<Bound>` runtime class with `GetTypeMetadata` P/Invoke, add to `BoundGenericsHandler.s_stdlibGenerics` + `BareGenericGuardStrategy.KnownGenericTypes`, and an `ILLink` preserve entry. Verify the real `$sSN…Ma` metadata-accessor symbol against a dylib. Unblocks WorkoutKit `HeartRate/Cadence/Power/SpeedRangeAlert`.

**RC‑MISSING (request/query initializers).** MusicKit `init(term:types:)` is blocked by gate 4b (variadic generic parameter pack `repeat each MusicType` → `UnsupportedSignature`); C# has no parameter-pack equivalent, so the path is a Swift wrapper overload that takes a `[MusicType]` array and splats it. WeatherKit `weather(for:including:)` and the statistics/summaries are 6-way method-own-generic `async` tuple returns that exceed the CSM cartesian cap — hard; full-bundle `WeatherAsync` stays the workaround.

**RC‑AOT.** `MeshBuffer<T>` et al. are *generated* generic types whose `cctor` lacks the eager `SwiftObjectHelper<T>.GetTypeMetadata()` call that hand-written `SwiftArray` uses (`SwiftArray.cs:80-106`), and no `TrimmerRootDescriptor` is emitted for generated binding assemblies — so ILC can't see the instantiation. Works on Mono (reflection path) but not NativeAOT. Fix in the emitter: synthesize the eager-`cctor` pattern under `SwiftRuntimeInfo.IsNativeAotRuntime` and emit an `ILLink` descriptor or `[DynamicDependency]` for generated generic `ISwiftObject` types.

**RC‑CLOSURE.** Single gate `ClosureHandler.IsSupportedClosure`. PlayAudio's `(UnsafeMutablePointer<AudioBufferList>) -> OSStatus` fails only because `OSStatus`/`AudioBufferList` aren't module-qualified/registered, or because `IsSupportedGenericType` doesn't short-circuit pointer instantiations (every `Unsafe*Pointer<T>` is `IntPtr` on the wire) — tractable. `@autoclosure () -> Value` fails because `Value` is a parent generic and `IsMethodGenericClosureEligible` rejects `@escaping`; medium. `() -> [T]` result-builder closures need per-conformer specialization — harder.

**RC‑AEIC / RC‑STRUCTURAL / RC‑WILLSET (confirmed not generator-fixable).** AEIC: we *can* emit a forwarding shim that inlines the stdlib `build*` method into a stable `@_cdecl` symbol (we already emit Swift helpers), but that exposes raw entrypoints, not the result-builder authoring experience — the usage model, not the ABI symbol, is the blocker. STRUCTURAL: C#-authored intent/attribute types are never seen by `swiftc`, so no real `Codable`/`Hashable`/macro witnesses can exist; closing it requires a C#→Swift source-emission + macro-expansion + secondary-binding subsystem (a different product; portfolio doc lists AppIntents as a hard skip). WILLSET: the trap is inside the framework's own `willSet`; no `@_cdecl`/direct-ABI/runtime route bypasses a property observer. Only a best-effort preflight guard + documentation, which does not make detached mutation work.

### 6c. Recommended sequencing

1. **P0 — silent/fatal, clear fixes:** RC‑SIMD (silent corruption of the most basic 3D op), RC‑PROXY Failure A (surgical, unblocks `Scene.AddAnchor` / core AR placement). (RC‑CDECL‑PARITY previously listed here was already resolved — see §6b note.) Matches §1's "if only three things."
2. **Tier 2 — high value, established-pattern M:** RC‑GENERIC + RC‑PAT(CryptoKit) hint additions (unblocks much of CryptoKit in one workstream), RC‑MISSING `ClosedRange` (WorkoutKit range alerts), RC‑AOT (typed mesh buffers on device), RC‑CLOSURE PlayAudio handler.
3. **Tier 3 — larger but bounded:** RC‑PROXY Failure B (`EveryEntityProtocol`, unblocks gestures), RC‑MISSING MusicKit array-shim, RC‑SWIFTUI FamilyActivityPicker (rides on a `Binding<Struct>` enhancement), RC‑CLOSURE `@autoclosure`.
4. **Do not pursue as generator work:** RC‑AEIC (DSL unrestorable), RC‑STRUCTURAL (separate product), RC‑WILLSET (upstream; guardrail only), WeatherKit 6-generic async tuple, RC‑CLOSURE `() -> [T]` builders, RC‑PAT app-defined-conformer cases — and confirm AppIntents/ActivityKit stay on the hard-skip list (`Design/apple-framework-portfolio.md`).

Every fix above must land its regression coverage at the right layer per `CLAUDE.md` — generator/emitter changes get unit tests **and** a BindingTests Swift+C# round-trip; RC‑SIMD and RC‑AOT specifically require the `--device` (NativeAOT) gate, not just sim.

> **Consult provenance:** RC‑PAT/AEIC/SWIFTUI/STRUCTURAL/WILLSET verdicts independently corroborated by Codex (`session 019e6bf7-9250-76a1-bde3-fb5634f55d6d`) and Grok (`sessionId 019e6bf7-a725-76d1-bf4e-22ca0b24bfc8`) on 2026-05-27; both flagged RC‑PAT as overstated for finite-conformer (CryptoKit) cases and agreed the other four are outside an incremental binding-generator change.

---

*Generated from the 2026-05-27 guide-authoring + accuracy-review pass across all 16 Apple-framework packages. Every `file:line` references `apple-frameworks/<Framework>/obj/Debug/net10.0-ios26.2/swift-binding/<Framework>.cs` (AppIntents: `obj/Release/...`) unless a `tests/Tests.cs` path is given. §6 added by the 2026-05-27 fixability research pass (generator/runtime `file:line` references are to `src/Swift.Bindings/src/` and `src/Swift.Runtime/src/`).*
