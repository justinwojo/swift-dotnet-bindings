# Binding Surface Audit — Ranked Recommendations (2026-07-11)

Prioritized by **consumer impact × tractability**, informed by BindingAudit (June) + this delta (July). Split by where the work lands.

---

## A. Generator correctness (P0–P1)

### A1. EveryProtocol / reverse-dispatch for remaining “compile but dead” carriers — **P0**

**Why:** Still the highest-risk class. Types and properties compile; getters throw or callbacks never fire.

| Library | Symptom | Status 2026-07 |
|---|---|---|
| RealityFoundation | `ModelComponent.Materials` getter → `NotSupportedException` (proxy not emitted); setter works | **OPEN** |
| RoomPlan | `RoomCaptureViewDelegate` getter throws; proxy skipped; session delegate improved | **OPEN** |
| BlinkIDUX / others | Analyzer / extension-point proxies | See BindingAudit |

**Direction:** Continue carrier-class partition + forward-only proxies; fail closed with honest report rows (already improved). Prefer **not emitting a throwing getter** over a public property that always throws when read.

**Gate:** BindingTests maximum fixtures for materials-like existential arrays + delegate reverse dispatch; RoomPlan-style view delegate in packages tests once generator-fixed.

### A2. Label-preserving protocol overload rename — **P1**

**Why:** Swift methods that differ only by argument labels still collapse in C#.

| Example | Impact |
|---|---|
| LiveCommunicationKit activate vs deactivate `(AVAudioSession)` | One C# method — wrong or incomplete dispatch |
| RoomPlan `IRoomCaptureSessionDelegate` | Multiple `CaptureSession(...)` overloads indistinguishable by name |

Selector-style renames landed for some LCK methods (`DidBegin`/`DidReset`); residual AVAudioSession pair and RoomPlan session delegate remain.

**Gate:** ProtocolHandler collision fixture + RoomPlan/LCK BindingTests.

### A3. MusicKit `MusicLibraryResponse<T>.Items` (AnyType) — **P1**

**Why:** Blocks the entire “read the user’s library” loop; catalog search still works.

**Direction:** Per-T closed projection of `MusicItemCollection<T>` (same family as CSM concretization).

### A4. CryptoKit NIST ECDSA — **P1 process, not emission**

**Why:** June audit said unreachable; **July surface shows CSM `Signature(byte[])` / `IsValidSignature`**. Risk is **false confidence without KAT**, not missing methods.

**Action:** Add CryptoKit package tests (and optional BindingTests) for P256 sign→verify round-trip; re-read CRYPTOKIT-GUIDE and fix any stale “works / doesn’t work” claims.

### A5. Closure + generic specialization gaps that kill product APIs — **P1**

| Shape | Victims |
|---|---|
| `GenericTypeCallback` | Kingfisher `setImage` overloads; ActivityKit generic update; WeatherKit `weather<T>` |
| `UnsupportedClosure` on config handlers | Stripe PaymentSheet deferred `confirmHandler`; some Apple Pay handlers |
| `MissingWrapperSymbol` | ObjectMapper `map*`; Kingfisher delegate call paths |

**Direction:** Keep expanding closed-generic `@_cdecl` wrappers and supported closure shapes; MissingWrapperSymbol is a **pipeline integrity** bug (Review-tier in SkipTriage) — treat as fail-closed CI if it grows.

### A6. Existential / AnyType in return & property position — **P1–P2**

Nuke `userInfo`, RoomPlan geometry arrays, MusicKit items, Alamofire `ResponseJSON` → `AnyType`. Recurring; each unlock multiplies library value.

---

## B. Generator quality / ergonomics (P2–P3)

| # | Item | Notes |
|---|---|---|
| B1 | Nested-type name stutter (`OfferTypeTypeType`, `OwnershipTypeType`) | Nested-type disambiguation work landed (`eeae439e`); residual stutter still consumer-visible |
| B2 | Factory / zero-arg verb shapes (`FrombyteArr_`, `Create_C11D4260`, SnapKit `GetequalToSuperview`) | Naming polish; SnapKit DSL particularly sensitive |
| B3 | Mega-file emission (RealityFoundation ~135k, Matter ~93k, Alamofire ~60k) | IDE/nav tax; consider type-partitioned files or `partial` modules |
| B4 | AsyncSequence → `IAsyncEnumerable` completeness | StoreKit2 strong; other streams still uneven |
| B5 | XML docs on ordinary members | GUIDEs compensate; generator remarks on dispose are good — extend lightly |
| B6 | Stdlib protocol sugar | Alamofire: `string` → `IURLConvertible` (or extension package) |

---

## C. Package / packaging / docs (P1–P2)

| # | Item | Severity |
|---|---|---|
| C1 | **TFM vs deployment min docs** — Apple packages use `net10.0-ios26.2` (SDK surface). Guides that say “requires iOS 26.2+” over-constrain apps; ActivityKit guide is the better pattern. | **P1** |
| C2 | Verify NuGet restore: can `net10.0-ios` / lower platform TFM apps consume Apple 26.2 packages? Document hard requirement if not. | **P1** |
| C3 | Stripe `SwiftWrapperRequired=false` on critical modules — soft-fail footgun if wrappers actually needed | **P1** |
| C4 | Empty SPI packages (StripeUICore / CameraCore) — ship transitive-only; README / package description must say “do not use directly” (already partly true) | **P2** |
| C5 | Stripe version skew (`library.json` 26.0.0 vs some csproj 25.15.0) | **P2** |
| C6 | Kingfisher minIOS 13 vs Nuke 15 — upstream-true; document matrix | **P2** |
| C7 | MapLibre / Facebook ship gates (pack-and-consume, mixed-pack) — see packages `BINDING-CANDIDATES.md` | **P1 ship** |
| C8 | AppIntents remains **not a consumer product** — correct | — |

---

## D. Test depth (process — highest ROI for trust)

**Finding:** Nearly all Apple `tests/Tests.cs` have **zero `await`**. Third-party tests (Kingfisher/Lottie/Stripe/BlinkID) are large but still lean construction/metadata; MapLibre is the rare workflow-shaped app.

### Minimum bar for “this package is usable”

For each shipped package, at least one test (sim + preferably device) that:

1. Constructs the **primary entry type** with real parameters.
2. Calls the **headline async or mutating API** (awaited).
3. Asserts a **semantic value** (not just “didn’t throw” / type metadata).

| Package | Suggested first deep test |
|---|---|
| StoreKit2 | `Product.ProductsAsync` against Sandbox or skip-with-reason if no network; assert empty-or-list shape |
| CryptoKit | AES-GCM round-trip (exists) **+ P256 ECDSA sign/verify** |
| MusicKit | Auth status + catalog search suggestions (entitlement-gated) |
| WeatherKit | `WeatherAsync` with CLLocation (entitlement) |
| Nuke | Load one URL into pipeline / cache hit |
| Lottie | Play animation from bundled JSON (partially present) |
| Stripe | PresentPaymentSheet configuration construct + mock client secret path |
| RoomPlan | Session start **or** explicit Skip documenting view-delegate gap |
| RealityFoundation | Entity + mesh + materials **set** path; materials **get** expected throw until A1 fixed |

Internal-binding-testing: same lesson — green smoke ≠ product usable (RxSwift, Swinject, ObjectMapper).

---

## E. Product / corpus strategy

| Binding shape | Recommendation |
|---|---|
| Imperative / concrete Swift (KeychainAccess, PhoneNumberKit, CryptoKit AEAD, StoreKit2, Lottie) | **Ship and market** |
| Builder-first (Kingfisher `KF`, Nuke pipeline) | Ship; document dead extension overloads |
| Macro / PAT / reactive / DI (TipKit define-tip, RxSwift, Swinject, ObjectMapper, AppIntents) | **Honest “companion Swift required” or do not market as full bindings** |
| Heavy SPI (StripeUICore) | Transitive only |
| ObjC pure (MapLibre, Matter) | Ship when pack-consume gates pass; keep bgen personality documented |

---

## F. Suggested execution order (next 4–6 weeks)

1. **A1 + A2** generator sessions (P0 correctness) with BindingTests first.
2. **D** deepen tests on CryptoKit ECDSA + one StoreKit2 + one Nuke path (trust).
3. **C1–C2** docs/TFM consumer story (cheap, stops footguns).
4. **A3** MusicKit library items (product gap).
5. **A5** MissingWrapperSymbol fail-closed + Kingfisher/Stripe closure gaps as capacity allows.
6. **B1–B3** naming / mega-file as polish after correctness.

Do **not** open another full archaeology campaign — BindingAudit + this folder are enough to drive sessions. Update BindingAudit `_SUMMARY` only when a finding is closed with evidence.
