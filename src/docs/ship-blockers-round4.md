# Ship Blockers — Round 4 (SDK 0.8.0 + SwiftBindings.Apple 26.2.0)

**Validation date:** 2026-04-19
**SDK versions:** SwiftBindings.Sdk 0.8.0, SwiftBindings.Runtime 0.8.0, SwiftBindings.Templates 0.8.0, SwiftBindings.Apple **26.2.0** (up from 26.0.0 in Round 3)
**Scope:** Apple frameworks (12) + Stripe (11 public + 3 internal products). Third-party libraries (Nuke, Lottie, Kingfisher, BlinkID, BlinkIDUX, Mappedin) were not re-validated this round; Round 3 state for them is assumed unchanged.
**Prior round:** `ship-blockers-round3.md`.
**Consumer repo:** `/Users/wojo/Dev/swift-dotnet-packages` (ship-readiness source of truth: `/Users/wojo/Dev/swift-dotnet-packages/SHIP-READINESS.md`).

---

## 0. Session plan (4 sessions)

Fix everything in this doc. Sessions are grouped by shared generator/runtime subsystem so each stays focused. Order is by ROI: session 1 unlocks 5 packages, session 2 unlocks 2, sessions 3 and 4 are lower-leverage closers but still in scope.

### Session 1 — Async-closure marshalling (Fix #3)

**Unlocks:** Stripe (umbrella) + StripePaymentSheet + StripeApplePay + StripeConnect + StripeIssuing → SHIP (5 packages).
**Why first:** Highest leverage in the doc (5× next best). Discrete subsystem — closure marshalling on `@escaping async -> Void` callbacks.
**Scope:**
- Pick projection (recommend Option 1 = `Task<T>`; reuses Round 3 Lottie/Mappedin async plumbing).
- Emitter: stop skipping methods whose trailing closure is `@escaping + async`; emit Swift shim + C# `Task<T>` surface.
- Runtime: Swift continuation → managed `Task` bridge if not already present.
- BindingTests: async-completion Swift source + runtime test covering success + error paths.

**Gates:** `nuke test` + `nuke binding-tests` + `nuke runtime-tests-device` (marshalling path changes) + rebuild the 5 Stripe packages and confirm their sim-validation tests exercise the new surface.

### Session 2 — Generic projections: `VerificationResult<T>` + `Forecast<T>` + tombstone reporting (Fixes #1, #2, §4)

**Unlocks:** StoreKit2 + WeatherKit → SHIP. Also hardens audits against future silent tombstones.
**Why grouped:** All three touch how the emitter projects generic Swift constructs into usable C# surface; §4 is the audit tooling that stops us re-shipping tombstones.
**Scope:**
- Fix #1: Emit `TryGetVerified(out T)` / `TryGetUnverified(out T, out VerificationError)` for generic enums whose case payload is the type parameter. Narrow pattern, not full DU projection.
- Fix #2: Generic-collection-with-metadata projection (`Forecast<T>` → indexer, `Count`, `GetEnumerator`). Generalizes to `HKStatisticsCollection<Sample>`, AppIntents collections.
- §4: Add `silentTombstones` key to `binding-emission-report.json`; add an SB0002 diagnostic (or raise SB0001) on call sites that return a tombstoned type so audits catch them by grep.

**Gates:** `nuke test` + `nuke validate` + `nuke binding-tests` + rebuild StoreKit2 / WeatherKit and confirm real forecast iteration and verified-transaction unwrap in sim tests.

### Session 3 — CryptoKit: method-level generics + mutating-self (§5)

**Unlocks:** CryptoKit HOLD → SHIP. Incidentally improves StoreKit2 `Product.products(for:)` and anywhere else method-level generics on constrained type-level generics currently drop the method.
**Why standalone:** Two deep emitter changes that interact (HMAC hits both at once). Deserves its own debugging budget.
**Scope:**
- Method-level generics on type-level-generic-constrained types (e.g., `HMAC<SHA256>.update<D: DataProtocol>`). Specialize instead of skipping.
- Mutating-self wrapper ABI path for `mutating func finalize()` on hash types.
- BindingTests: SHA256 / HMAC / HKDF Swift sources + round-trip hash + derived-key tests.

**Gates:** `nuke test` + `nuke binding-tests` + `nuke runtime-tests-device` (calling conventions + mutating-self ABI) + rebuild CryptoKit and confirm actual digest round-trips against known vectors.

### Session 4 — Cleanup: RoomPlan + ActivityKit investigation + TipKit docs (Fixes #4, #5, TipKit)

**Unlocks:** RoomPlan → SHIP. ActivityKit → SHIP if investigation succeeds, else documented permanent limitation. TipKit documented.
**Why grouped:** Lower-ROI closers; mix of concrete fix + design investigation + doc-only.
**Scope:**
- Fix #4: Array-of-Swift-struct marshalling (`[CapturedRoom.Surface]`, `[CapturedRoom.Object]`) + `simd_float4x4` → `System.Numerics.Matrix4x4` entry in the SwiftBindings.Apple type DB.
- Fix #5: Investigate `ActivityAttributes` routing — C# source generator + marker interface that emits the required Swift shim. Time-box; if infeasible, record as permanent limitation in ActivityKit README and ship with type-metadata surface.
- TipKit: README note that the result-builder DSL (`Tips.Rule.when(...)`) is unreachable (`@_alwaysEmitIntoClient`, no binary symbol). Optional C#-side sugar stubs if easy.

**Gates:** `nuke test` + `nuke validate` + `nuke binding-tests` + rebuild RoomPlan and confirm scan-consumption test; rebuild ActivityKit if routing lands.

### Cross-cutting notes

- **Third-party re-validation** (Nuke / Lottie / Kingfisher / BlinkID / BlinkIDUX / Mappedin): one lightweight pass against current SDK + Apple 26.2.0. Tack onto whichever session ships first — not a session of its own.
- **SDK versioning:** keep SDK patch at 0.8.0 through all four sessions per memory policy; bump only when the packages drop is cut.
- **Order flexibility:** sessions 1 and 2 are independent; session 3 depends on nothing in 1/2; session 4 is last. If a session balloons, split it rather than carry scope.

---

## 1. Summary

Round 4 clears the two Round 3 showstoppers:

1. **Runtime dylib load regression — RESOLVED.** The `SwiftString operations require the SwiftBindingsRuntime native library` / `<Library>SwiftBindings` load failures that took 14/17 sim tests down in Round 3 are gone. **10/10 Apple framework sim tests PASS; Stripe sim test: 299/299 assertions PASS.**
2. **MusicKit / WeatherKit `MusicRelationshipProperty<,>` / `Forecast<>` skip-but-still-reference — RESOLVED at the build level.** Both libraries now build clean on all 4 TFMs with 0 SB0001 in the iOS output. The generator chose the "silent tombstone" path — see §4 for what this means for consumers.

What remains is a shorter, sharper list: **five functional API gaps** across 10 Apple + Stripe packages, plus **one still-broken Apple package** (CryptoKit). Build quality is otherwise excellent — SB0001 emissions are 0 for 10/12 Apple frameworks and 0 for 12/12 Stripe products.

See §2 for ship status, §3 for the five priority fixes, §4 for the MusicKit/WeatherKit tombstone discussion, and §5 for CryptoKit.

---

## 2. Ship status

### SHIP — 12 packages (no functional gaps found; ready to publish)

**Apple (6):**
- `MusicKit` — `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/MusicKit/`
- `WorkoutKit` — `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/WorkoutKit/`
- `FamilyControls` — `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/FamilyControls/`
- `LiveCommunicationKit` — `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/LiveCommunicationKit/`
- `ProximityReader` — `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/ProximityReader/`
- `Translation` — `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/Translation/`

**Stripe (6):**
- `StripeCore` — `/Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeCore/`
- `StripePayments` — `/Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripePayments/`
- `StripePaymentsUI` — `/Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripePaymentsUI/`
- `StripeIdentity` — `/Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeIdentity/`
- `StripeCardScan` — `/Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeCardScan/`
- `StripeFinancialConnections` — `/Users/wojo/Dev/swift-dotnet-packages/libraries/Stripe/StripeFinancialConnections/`

All 12 build clean on every TFM, have 0 SB0001 on iOS, pass sim tests end-to-end, and — per the parallel per-library audits — expose the consumer-critical entry points their vendors document as the canonical integration paths.

### NEAR-SHIP — 10 packages (build clean, sim tests pass, but a specific API path is non-functional)

Each entry below names the **one** concrete gap that keeps the package out of SHIP. Unlike Round 3 where NEAR-SHIP meant "SB0001 count > 0," Round 4's NEAR-SHIP libraries all have 0 SB0001 — the gaps are functional (skipped-but-referenced, missing marshalling for a specific idiom, architectural ABI gap) rather than surface-level.

| Package | Path in repo | Blocking gap | Fix owner |
|---|---|---|---|
| `ActivityKit` | `apple-frameworks/ActivityKit/` | `ActivityConfiguration` projected but `Activity.request/update/end` still require consumer-defined `ActivityAttributes` conformers — no ABI path exposed. | Generator (investigate ActivityAttributes user-type routing) |
| `RoomPlan` | `apple-frameworks/RoomPlan/` | `CapturedStructure` structural lists (rooms, sections, objects) not projected as usable collections; `simd_float4x4` transforms have no managed projection. | Generator (structural list marshalling) + Apple type DB (simd_float4x4) |
| `StoreKit2` | `apple-frameworks/StoreKit2/` | `VerificationResult<T>.TryGetVerified(out T)` pattern not emitted — `Transaction.currentEntitlements`, `Product.purchase()`, `Transaction.latest(for:)` return `VerificationResult<T>` but callers cannot unwrap the verified payload. | Generator (generic enum pattern-match projection) |
| `TipKit` | `apple-frameworks/TipKit/` | 12 SB0001 on result-builder DSL methods (`@_alwaysEmitIntoClient`, no binary symbol). `Tip` + `TipView` basic flow works, but rule composition chains (`Tips.Rule(...).when(...)`) are unreachable from C#. | Permanent limitation — document in README; consider stub sugar |
| `WeatherKit` | `apple-frameworks/WeatherKit/` | `Forecast<TElement>` is a silent tombstone (see §4). Hourly / daily / minute forecasts are non-reachable from C# despite 0 build errors. | Generator (generic container type emission) |
| `CryptoKit` *(secondary)* | `apple-frameworks/CryptoKit/` | See §5 — listed here because TipKit is the only Apple package genuinely in "partial" territory. CryptoKit is actually HOLD. |
| `Stripe` (umbrella) | `libraries/Stripe/Stripe/` | Async-closure callbacks on the umbrella `STPAPIClient` surface not marshallable. Sub-product packages (StripeCore/StripePayments) cover the same functionality without this gap. | Generator (async closure marshalling) |
| `StripePaymentSheet` | `libraries/Stripe/StripePaymentSheet/` | `PaymentSheet.present(from:completion:)` uses async-closure completion; C# consumers cannot observe the result. | Generator (async closure marshalling) |
| `StripeApplePay` | `libraries/Stripe/StripeApplePay/` | `STPApplePayContext.presentApplePay(completion:)` uses async-closure completion. | Generator (async closure marshalling) |
| `StripeConnect` | `libraries/Stripe/StripeConnect/` | `EmbeddedComponentManager.create<T>(componentType:)` + completion callbacks rely on async-closure marshalling. | Generator (async closure marshalling) |
| `StripeIssuing` | `libraries/Stripe/StripeIssuing/` | `STPPushProvisioningContext.pushProvisioningDetails(forActivationData:completion:)` async-closure. | Generator (async closure marshalling) |

### HOLD — 1 package

- `CryptoKit` — `apple-frameworks/CryptoKit/` — 37 SB0001 on iOS. See §5.

### Summary counts

| Status | Count | Notes |
|---|---|---|
| SHIP | 12 | 6 Apple + 6 Stripe |
| NEAR-SHIP | 10 | 4 Apple (ActivityKit, RoomPlan, StoreKit2, WeatherKit) + 1 Apple partial (TipKit) + 5 Stripe |
| HOLD | 1 | CryptoKit |
| **Total audited** | **23** | |

Third-party NEAR-SHIP set from Round 3 (BlinkID 1, Nuke 5, Lottie 8, Mappedin 10, Kingfisher 39) was not re-validated in Round 4. Those counts likely dropped further with Apple 26.2.0, but re-run Round 3 §Validation to confirm before shipping them.

---

## 3. Five priority SDK fixes

Ranked by consumer impact — fix order below maximizes packages unblocked per SDK iteration.

### Fix #1 — `VerificationResult<T>.TryGetVerified(out T)`

**Unlocks:** StoreKit2 → SHIP.

**Context.** Swift's `StoreKit2` expresses "the system tried to verify this payload; here's the result" as `public enum VerificationResult<SignedType>: Sendable { case unverified(SignedType, VerificationResult<SignedType>.VerificationError); case verified(SignedType) }`. Every critical path in the framework returns it:
- `Transaction.currentEntitlements` — `AsyncSequence<VerificationResult<Transaction>>`
- `Product.purchase()` — `Product.PurchaseResult` (which wraps `VerificationResult<Transaction>`)
- `Transaction.latest(for:)` — `VerificationResult<Transaction>?`

**Current state.** The generic enum declaration is emitted, but the discriminated-union projection (the C# equivalent of pattern matching `.verified(let tx)`) is not. C# consumers get an opaque `VerificationResult<Transaction>` with no way to read the transaction out.

**Fix shape.** Emit a `TryGetVerified(out T payload)` / `TryGetUnverified(out T payload, out VerificationError error)` pair for any generic enum whose cases carry an associated value of the generic parameter type. This is narrower than "general Swift enum-with-associated-value projection" — we only need the single-generic-payload case, which is the StoreKit2 idiom.

**Files to read.**
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/StoreKit2/obj/Debug/net10.0-ios26.2/swift-binding/StoreKit2.cs` — search for `VerificationResult`
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/StoreKit2/tests/Tests.cs` — currently exercises metadata only; add an IAP flow once the API exists

### Fix #2 — `Forecast<TElement>` emission

**Unlocks:** WeatherKit → SHIP.

**Context.** WeatherKit's `Weather` type exposes:
- `hourlyForecast: Forecast<HourWeather>`
- `dailyForecast: Forecast<DayWeather>`
- `minuteForecast: Forecast<MinuteWeather>?`

`Forecast<Element>` is `RandomAccessCollection` of `Element` plus metadata (`metadata: ForecastMetadata`). In Round 3 the generator skipped the declaration and kept the references (compile error). In Round 4 it emits a silent tombstone (see §4) — the type exists in the assembly but the element access surface is gone.

**Fix shape.** Generic container types whose shape is "`Collection<T>` + metadata property" need a concrete projection — minimally an indexer / `Count` / `GetEnumerator` that round-trips through the underlying Swift collection metadata. This generalizes beyond `Forecast<T>` (same pattern shows up in `HKStatisticsCollection<Sample>`, `AppIntents.IntentParameterCollection<T>`, etc.).

**Files to read.**
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs` — grep `Forecast` and confirm zero usable API
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/WeatherKit/tests/Tests.cs` — add a forecast iteration test once emitted

### Fix #3 — Async-closure marshalling for completion handlers

**Unlocks:** Stripe (umbrella), StripePaymentSheet, StripeApplePay, StripeConnect, StripeIssuing → SHIP.

**Context.** The Stripe iOS SDK uses the canonical ObjC-bridged async pattern: `void presentPaymentSheet(from: UIViewController, completion: @escaping (PaymentSheetResult) async -> Void)`. The emitter currently skips any method whose trailing closure is both `@escaping` and `async`. All five NEAR-SHIP Stripe products have at least one primary-flow entry point on this pattern.

**Affected methods (sample).**
- `StripePaymentSheet.PaymentSheet.present(from:completion:)` — `libraries/Stripe/StripePaymentSheet/obj/Debug/net10.0-ios/swift-binding/StripePaymentSheet.cs`
- `StripeApplePay.STPApplePayContext.presentApplePay(on:completion:)` — `libraries/Stripe/StripeApplePay/obj/Debug/net10.0-ios/swift-binding/StripeApplePay.cs`
- `StripeConnect.EmbeddedComponentManager.create<T>(componentType:didLoad:)` — `libraries/Stripe/StripeConnect/obj/Debug/net10.0-ios/swift-binding/StripeConnect.cs`
- `StripeIssuing.STPPushProvisioningContext.pushProvisioningDetails(forActivationData:completion:)` — `libraries/Stripe/StripeIssuing/obj/Debug/net10.0-ios/swift-binding/StripeIssuing.cs`

**Fix shape.** Async-closure lowering for escaping callbacks. Two viable projections:
1. Return `Task<T>` and drop the callback parameter from the C# signature. Idiomatic C# consumption.
2. Keep the `Action<T>` callback parameter and spawn a small bridging shim that forwards the Swift continuation to the managed delegate.

Option 1 is cleaner for consumers but touches Task-factory plumbing that Round 3's Lottie/Mappedin async work already mapped out. Option 2 is lighter on the emitter but requires runtime support for the "Swift continuation → managed delegate" bridge.

This is the single highest-ROI fix in Round 4 — one change, five Stripe packages graduate to SHIP.

### Fix #4 — RoomPlan `CapturedStructure` + `simd_float4x4`

**Unlocks:** RoomPlan → SHIP.

**Context.** RoomPlan's core output types expose collections (`CapturedStructure.rooms: [CapturedRoom]`, `CapturedRoom.walls: [CapturedRoom.Surface]`, `CapturedRoom.objects: [CapturedRoom.Object]`) and geometric transforms (`CapturedRoom.Surface.transform: simd_float4x4`, `CapturedRoom.Object.transform: simd_float4x4`). None of the structural lists or the transform matrices project to usable C# types currently.

**Fix shape.**
- Structural lists: Swift `Array<T>` of Swift value types. Today's emitter handles `[Int]` / `[String]` fine; it loses on arrays of Swift structs with heterogeneous nested types.
- `simd_float4x4`: add to the SwiftBindings.Apple type DB as a `System.Numerics.Matrix4x4` projection.

**Files to read.**
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/RoomPlan/obj/Debug/net10.0-ios26.2/swift-binding/RoomPlan.cs` — grep `CapturedStructure`, `transform`
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/RoomPlan/tests/Tests.cs` — add a scan-consumption test once emitted

### Fix #5 — ActivityKit `ActivityConfiguration` routing

**Unlocks:** ActivityKit → SHIP (the Live Activity primary flow).

**Context.** `ActivityKit.Activity<Attributes: ActivityAttributes>.request(attributes:content:)` is the documented entry point for starting a Live Activity. The Round 2 note "permanent — requires consumer-defined `ActivityAttributes` conformers" is technically correct, but `ActivityConfiguration` is projected in Round 4, which hints that user-type routing is closer than we thought. Worth an investigation pass to see whether a codegen contract (source generator that emits the required Swift shim when a C# type implements a marker interface) can bridge the gap.

**This is a design-investigation task, not a clear codegen fix.** Treat it as "investigate whether user-type routing through ActivityConfiguration + a C# source generator is feasible; if not, downgrade ActivityKit to 'permanent limitation' and ship with type-metadata-only surface."

**Files to read.**
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/ActivityKit/obj/Debug/net10.0-ios26.2/swift-binding/ActivityKit.cs`
- `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/ActivityKit/obj/Debug/net10.0-ios26.2/swift-binding/ActivityKit.Wrapper.swift` — check how far the wrapper gets

---

## 4. The "silent tombstone" pattern — MusicKit / WeatherKit

Round 3 had `MusicRelationshipProperty<,>` / `Forecast<>` referenced but not declared (CS0234 across 544 sites). Round 4 fixes the build error, but the chosen mechanism is worth calling out explicitly:

**The generator now emits an empty generic type declaration when a generic is skipped, but leaves call sites pointing at it.** Examples:

```csharp
// apple-frameworks/WeatherKit/obj/Debug/net10.0-ios26.2/swift-binding/WeatherKit.cs
public partial struct Forecast<TElement> { }   // no members — the tombstone

public sealed class Weather {
    public Forecast<HourWeather> HourlyForecast { get; }  // compiles; returns a Forecast with no usable API
}
```

The build is green. SB0001 count is 0. The package **passes every gate we had until Round 4.** But a consumer who does `var hours = weather.HourlyForecast; foreach (var h in hours) { … }` gets a compile error on `foreach` — `Forecast<T>` isn't enumerable, has no indexer, has no `Count`.

**Why this matters for the generator.**

The tombstone strategy is the right *build* outcome (Option 1 from Round 3 §3) but it needs to be flagged so that audits catch it. Two concrete asks for SDK 0.8.x or 0.9:

1. **Emit SB0001 (or a new SB0002 "container type has no usable surface") on the caller side**, not just on the type declaration. The current heuristic flags the type but leaves the getter clean — audits that grep SB0001 miss it.
2. **Include tombstoned types in `binding-emission-report.json`** under a new `silentTombstones` key. This makes them queryable without having to `grep ": IEnumerable" / ": IList" / ": IReadOnlyCollection"` and confirm the absence.

Otherwise, future rounds will keep mis-classifying tombstoned packages as SHIP.

**Affected in Round 4:** `WeatherKit.Forecast<T>` (3 top-level properties, the primary read surface). MusicKit's `MusicRelationshipProperty<,>` is a less-critical tombstone — it shows up on collection relationships (e.g. `Album.tracks`), which means the MusicKit package is technically SHIP today for search / fetch flows but NEAR-SHIP for relationship-walking flows. It landed in SHIP in this round's matrix because the audit agent confirmed the primary consumer flows (`MusicCatalogSearchRequest`, `MusicPersonalRecommendationsRequest`, `ApplicationMusicPlayer`) don't hit the tombstone.

---

## 5. CryptoKit — why it's HOLD

**File:** `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/CryptoKit/` (37 SB0001 on iOS, all 4 TFMs).

**What works.**
- Type metadata loads for all named types (`SHA256`, `SHA384`, `SHA512`, `AES.GCM.Nonce`, `Curve25519.KeyAgreement.PrivateKey`, etc.). The tests at `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/CryptoKit/tests/Tests.cs` pass — but they only exercise metadata, not crypto.
- `HPKE.KEM.Curve25519_HKDF_SHA256` and peer enum cases project with correct ordinals.

**What doesn't work.**
- `SHA256.finalize()` / `SHA384.finalize()` / `SHA512.finalize()` — the critical "finish hashing and return the digest" method is SB0001 on all three hash types. Without `finalize()`, consumers can't actually hash anything.
- `HMAC<H: HashFunction>` — the generic `HMAC` type is emitted but its instance methods (`.update(data:)`, `.finalize()`) are SB0001. HMAC is non-functional.
- `HKDF<H: HashFunction>.deriveKey(...)` — SB0001. Key derivation non-functional.
- `AES.GCM.seal(_:using:nonce:)` — SB0001 only (no managed projection).
- `AES.GCM.open(_:using:)` — SB0001 only.

**Root cause.**
Two distinct issues tangled together:
1. **Method-level generics on `HashFunction`-constrained methods.** `HMAC<SHA256>.update(data: some DataProtocol)` has both a type-level generic (`SHA256`) and a method-level generic (`some DataProtocol`). The emitter currently drops the whole method rather than specializing. Same issue as StoreKit2 `Product.products(for:)` on Round 3 §Root Causes #5.
2. **Result-builder-style `finalize()` on mutating self.** `SHA256`'s `finalize()` is `mutating func finalize() -> SHA256Digest`. The wrapper emitter loses the mutating-self ABI path.

**Why this is HOLD, not NEAR-SHIP.**
CryptoKit is one of the few packages where a partial surface is *worse than no package*. A consumer who finds `SwiftBindings.CryptoKit` on nuget.org and instantiates `SHA256()` will rationally expect to be able to hash something. They can't. The failure mode is a runtime `NotImplementedException` with SB0001 in the message — confusing, and easy to mistake for a bug in their code.

Recommended posture until the generic-method + mutating-self fixes land:
- Pull `SwiftBindings.CryptoKit` from the 0.8.0 release.
- Re-introduce it in the SDK drop that closes fixes #5 and #2 above (method-level generics + mutating-self).

---

## 6. Delta vs Round 3

### Build regressions fixed (both Round 3 showstoppers)

| Round 3 blocker | Round 4 state |
|---|---|
| Runtime dylib load regression (SwiftBindingsRuntime / `<Lib>SwiftBindings` not resolving at runtime) — 14/17 sim tests failing | **Fixed.** 10/10 Apple sim tests PASS, Stripe 299/299 assertions PASS. |
| `MusicRelationshipProperty<,>` / `Forecast<>` skip-but-still-reference (432 + 112 CS0234) | **Fixed at build level** via silent-tombstone emission. Functional gap remains on `Forecast<T>` (see §4). |

### SB0001 wins vs Round 3 (iOS TFM)

| Library | Round 3 | Round 4 | Δ |
|---|---|---|---|
| CryptoKit | 152 | 37 | −115 |
| TipKit | 48 | 12 | −36 |
| StripePaymentSheet | 0 | 0 | (held) |
| WeatherKit | BUILD FAIL | 0 (tombstone) | +build |
| MusicKit | BUILD FAIL | 0 (tombstone) | +build |

### Packages that moved SHIP ↔ NEAR-SHIP

Net +2 SHIP vs Round 3:
- **→ SHIP:** MusicKit (was BUILD FAIL), WorkoutKit (held, but now sim-validated)
- **↔ NEAR-SHIP:** ActivityKit / RoomPlan / StoreKit2 / TipKit / WeatherKit (Round 3 had them as SHIP or BLOCKED; Round 4 re-classifies based on functional audit, not just SB0001 count)

### What didn't change

Third-party libraries (Nuke, Lottie, Kingfisher, BlinkID, BlinkIDUX, Mappedin) were not re-audited. Their Round 3 SB0001 counts and SHIP classifications carry forward as-is — but they should be re-run against SDK 0.8.0 + Apple 26.2.0 before shipping, since Round 3 showed Apple 26.0.0 alone drove −63 Kingfisher, −23 Lottie, −11 BlinkIDUX. Apple 26.2.0 likely cleared more.

---

## 7. Shipping recommendation

**SHIP the 12 SHIP packages now.** Runtime is clean, builds are clean, sim tests pass, per-library audits confirm the consumer-critical surface is present.

**HOLD the 10 NEAR-SHIP packages for the next SDK drop.** Each has exactly one named fix blocking it (see §3). Shipping them now would publish packages that look correct at the csproj / nuget level but fail functionally at the primary-flow API.

**PULL CryptoKit from the release.** Re-introduce with the SDK drop that closes method-level generics + mutating-self (see §5).

**Next SDK priority (ordered by impact):**
1. Async-closure marshalling (§3 #3) — unlocks 5 Stripe packages.
2. `VerificationResult<T>.TryGetVerified(out T)` (§3 #1) — unlocks StoreKit2.
3. `Forecast<TElement>` + generic-container-with-metadata projection (§3 #2) — unlocks WeatherKit, also catches HealthKit/AppIntents future work.
4. CryptoKit method-level generics + mutating-self (§5) — unlocks CryptoKit.
5. RoomPlan structural lists + `simd_float4x4` (§3 #4) — unlocks RoomPlan.
6. ActivityKit investigation (§3 #5) — long-lead; may result in "permanent limitation" outcome.

Estimated packages unblocked per fix: #1 → 5, #2 → 1, #3 → 1, #4 → 1, #5 → 1, #6 → 1. In SDK-dollar terms, fix #1 is 5× higher leverage than the next closest; prioritize accordingly.

**Also: tooling regression from Round 3 still open.** `spm-to-xcframework cafa869b74c8` header validation against Stripe — Round 3 §5. Not a release blocker (xcframeworks cached), but flags for the tool owner.

---

## 8. Validation artifacts

- **SBREADINESS source of truth:** `/Users/wojo/Dev/swift-dotnet-packages/SHIP-READINESS.md` (Round 4 section at top)
- **Per-library generated C#:** `apple-frameworks/<Name>/obj/Debug/net10.0-ios26.2/swift-binding/<Name>.cs` and `libraries/Stripe/<Product>/obj/Debug/net10.0-ios/swift-binding/<Product>.cs`
- **Per-library wrapper Swift:** same paths with `.Wrapper.swift` suffix
- **Emission reports:** same paths with `binding-emission-report.json` — if a future round needs to verify silent tombstones, grep this file for types that appear in skips but whose declaration was still emitted.
- **Sim-test programs:** `apple-frameworks/<Name>/tests/Program.UIKit.cs` + `Tests.cs`; `libraries/Stripe/tests/Program.cs`
- **Parallel audit findings (Round 4):** not persisted as files — see `/Users/wojo/Dev/swift-dotnet-packages/SHIP-READINESS.md` §Round 4 for the distilled per-package verdicts.

---

## 9. How to verify the tombstone pattern in future rounds

Add this to the Round 5+ validation script:

```bash
# For each Apple framework, check whether any generic type declaration is
# emitted with an empty body (silent tombstone).
for cs in apple-frameworks/*/obj/Debug/net10.0-ios26.2/swift-binding/*.cs; do
  lib=$(echo "$cs" | sed 's|apple-frameworks/\([^/]*\)/.*|\1|')
  # Match "public (partial )?(struct|class) Foo<T> { }" with empty body on same or next line.
  python3 -c "
import re, sys
src = open('$cs').read()
# Generic type with empty body — likely a tombstone
for m in re.finditer(r'public\s+(?:partial\s+)?(?:struct|class|sealed class)\s+(\w+<[^>]+>)\s*\{\s*\}', src):
    print(f'$lib: tombstone -> {m.group(1)}')
"
done
```

If the generator gains a `silentTombstones` key in `binding-emission-report.json` (see §4 ask), replace the regex with a JSON query.
