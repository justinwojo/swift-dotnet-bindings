# Binding Surface Audit — Executive Summary

**Date:** 2026-07-11  
**Scope:** Generator (`swift-bindings`) + shipped/sample bindings in `swift-dotnet-packages` and `internal-binding-testing`  
**Method:** Static analysis of generated C#, binding reports, package config, tests, and guides; delta re-check of the June 2026 BindingAudit. Not a full runtime regression matrix.  
**Detail docs:** see [index below](#document-index).

---

## One-line verdict

> **The generator produces a credible, largely 1:1 C# projection of real Swift SDKs — headline paths for many packages are usable today. Trust is limited more by shallow product tests and a handful of P0 “compiles but dead” proxy gaps than by raw coverage percentages.**

---

## What we audited

| Corpus | Contents |
|---|---|
| **Apple frameworks** (packages) | StoreKit2, CryptoKit, MusicKit, WeatherKit, TipKit, WorkoutKit, RealityKit/Foundation, RoomPlan, ActivityKit, Translation, FamilyControls, LiveCommunicationKit, ProximityReader, Matter/MatterSupport, AppIntents (shell), … |
| **Third-party** (packages) | Nuke, Lottie, Kingfisher, Stripe (14 products), BlinkID/UX, Mappedin, MapLibre/Facebook (ship-in-progress) |
| **Internal canaries** | Alamofire, CryptoSwift, SnapKit, KeychainAccess, RxSwift, Swinject, ObjectMapper, Kingfisher, … (~17 with reports) |
| **Prior baseline** | [`BindingAudit/`](../BindingAudit/) (2026-06-27) — full per-library coverage; this pass is delta + gaps |

---

## Core features: usable or not?

### Strong / ship-shaped headline paths

These map cleanly enough that a C# developer following native docs + package GUIDEs can do the **main job** of the library:

| Binding | Headline path that works (surface-level) |
|---|---|
| **StoreKit2** | `Product.ProductsAsync` → `PurchaseAsync` → `Transaction` streams / `FinishAsync` / entitlements (`IAsyncEnumerable`) |
| **CryptoKit** | Hash, AES-GCM / ChaChaPoly, HMAC, Curve25519; **NIST P-curve CSM overloads now emit** (`Signature(byte[])` etc.) — **runtime KAT still missing** |
| **WeatherKit** | Service weather fetch async surface |
| **Nuke** | Image pipeline / request / cache surface (extension-callback gaps remain) |
| **Lottie** | Animation view playback / modes |
| **Stripe Payments + PaymentSheet** | Config + drop-in present path (deferred confirmHandler still blocked) |
| **KeychainAccess / PhoneNumberKit / Starscream** (internal) | Practical CRUD / parse / socket cores |
| **Matter (ObjC)** | Near-complete bgen surface |
| **Translation / WorkoutKit / ProximityReader** | High effective coverage after accounting |

### Usable with friction or facade

| Binding | Notes |
|---|---|
| **MusicKit** | Catalog search + player usable (shims help); **library `Items` → AnyType** blocks library enumeration |
| **TipKit** | Configure + UIKit presentation; **cannot define tips in pure C#** (macro/PAT); query members PARTIAL on `ITip` |
| **ActivityKit** | Generic `Activity<T>` dead; **supplement facade** is the intended path |
| **Kingfisher** | `KF` builder path; many `setImage` overloads dead (`GenericTypeCallback`) |
| **Alamofire** | Session/Request exist; no `string`→URL sugar; Codable/async serialize paths weak |
| **SnapKit** | Constraints work; naming awkward (`GetequalToSuperview`) |
| **CryptoSwift** | AES path works; existential ctor crash risk on NativeAOT noted in internal audit |

### Not product-usable as a full native substitute

| Binding | Why |
|---|---|
| **AppIntents** | Correctly unpublished — authoring intents needs Swift macros + build metadata |
| **RxSwift / Swinject / ObjectMapper / XMLCoder** | Generics + closures + PATs dominate; smoke tests green, product API dead |
| **RoomPlan view delegate path** | `RoomCaptureViewDelegate` proxy missing — view callbacks fail/silent |
| **RealityFoundation materials read** | `ModelComponent.Materials` **getter always throws** (EveryProtocol) |
| **StripeUICore / CameraCore** | Empty by design (`@_spi`) — transitive only |

---

## Top defects (priority)

### P0 — correctness (compile ≠ work)

1. **EveryProtocol / proxy gaps on reverse-dispatch carriers**  
   - RealityFoundation `ModelComponent.Materials` get → hard `NotSupportedException`  
   - RoomPlan `RoomCaptureViewDelegate` — proxy skipped; getter throws  
   - Session-level RoomPlan delegate **did** improve (proves partial progress)

2. **Label-collapsed protocol methods**  
   - LiveCommunicationKit audio activate/deactivate still one C# shape  
   - RoomPlan `IRoomCaptureSessionDelegate` → multiple `CaptureSession(...)` overloads by type only

### P1 — core feature or footgun

3. **MusicKit library read** — `MusicLibraryResponse.items` AnyType  
4. **Stripe deferred PaymentSheet** — confirmHandler closure not constructible  
5. **Docs/TFM confusion** — packages pin `net10.0-ios26.2` (SDK surface); several guides imply **app min OS = 26.2**, which over-constrains deployment (ActivityKit guide is the better model)  
6. **CryptoKit NIST ECDSA** — emission looks fixed via CSM; **needs runtime proof + guide accuracy check**  
7. **Test depth** — almost no Apple package test **awaits** a real async product flow  
8. **SwiftWrapperRequired=false** soft-fail risk on some Stripe modules  
9. **MapLibre/Facebook** — not fully ship-gated (pack-consume / mixed-pack)

### P2–P3 — quality

10. Name stutter (`OfferTypeTypeType`), hash factories, mega-files (RF ~135k lines)  
11. AnyType / `// Unsupported:` walls in large modules  
12. minIOS inconsistency across third-party `library.json` (often upstream-true)  
13. Stripe version skew between `library.json` and some csprojs  

---

## C# quality (structure)

**Score: B+** for a 1:1 ABI generator.

**Preserve:** SafeHandle/dispose, `Task`+`CancellationToken`, payload enums (`CaseTag`/`TryGet*`), nested static facades (`AES.GCM`), closed specializations + honest `[Obsolete SB0001]`, private P/Invokes, `SupportedOSPlatform` density on Apple members.

**Tax:** Mega-files, residual stutter, incomplete AsyncSequence projection in some places, thin XML docs (GUIDEs carry ergonomics), dual ObjC-bgen vs Swift personality (expected).

Full write-up: [`04-csharp-quality-and-structure.md`](04-csharp-quality-and-structure.md).

---

## Project configuration / min OS

| Topic | Finding |
|---|---|
| Apple TFM `net10.0-ios26.2` | **Intentional SDK-surface pin** — correct for binding the 26.2 toolchain |
| App deployment min | Should be `SupportedOSPlatformVersion` + per-member attributes — **docs often wrong** |
| Third-party | Default `net10.0-ios`; minIOS 13–16 from upstream; weak package-level `SupportedOSPlatformVersion` on some library csprojs |
| Empty SPI packages | By design; keep out of “consumer API” marketing |

Detail: [`02-project-config-and-packaging.md`](02-project-config-and-packaging.md).

---

## Tests vs bindings

| Layer | Reality |
|---|---|
| Generator BindingTests | Strong ABI/marshalling gate (in-repo) |
| Package sim/device smoke | **Green does not mean product works** — metadata/construct heavy |
| Apple `Tests.cs` | **~0 `await`** across the set |
| CryptoKit | Improved (AEAD + Curve25519 KATs) — best Apple counterexample |
| Kingfisher/Lottie/Stripe/BlinkID | Large pass counts; still few true end-to-end product flows |
| Internal validate 0.16.0 | 0 fails — construction smoke only |

**Process recommendation:** For each shipped package, require at least one awaited headline-flow test (or explicit documented Skip with reason).

---

## Generator progress since June audit

Many BindingAudit/Gameplan items advanced (forward-only proxies, Optional ObjC string integrity / Stripe AppInfo, label renames, protocol-extension defaults, CSM frozen-trivial, ObjC third-party path, platform-aware min OS floor, report triage).  

**Still open at the consumer edge:** materials getter, RoomPlan view delegate, MusicKit library items, residual label collisions, test depth, packaging docs.

CryptoKit ECDSA is the important **delta correction**: report still shows open-generic skips, but **closed CSM overloads are present** in generated C#.

---

## What “fully functional, well-written” would require

1. **No public always-throw getters / silent-dead delegates** on advertised paths (P0 proxies).  
2. **Headline workflow tests** with real values (P1 process).  
3. **Honest support tiers** in marketing: imperative/concrete Swift yes; macro/PAT/reactive/DI only with Swift companion or “unsupported.”  
4. **Docs:** SDK TFM ≠ deployment min OS.  
5. **Ergonomic polish** after correctness: stutter, mega-files, URL sugar, deeper IAsyncEnumerable.

Ranked backlog: [`05-recommendations.md`](05-recommendations.md).

---

## Document index

| File | Contents |
|---|---|
| [`00-methodology.md`](00-methodology.md) | Scope, rubric, non-goals |
| [`00-executive-summary.md`](00-executive-summary.md) | This file |
| [`01-delta-revalidation.md`](01-delta-revalidation.md) | June findings re-checked on current CS |
| [`02-project-config-and-packaging.md`](02-project-config-and-packaging.md) | TFMs, min OS, empty packages, ship readiness |
| [`03-internal-binding-testing.md`](03-internal-binding-testing.md) | Secondary corpus usability + inventory |
| [`04-csharp-quality-and-structure.md`](04-csharp-quality-and-structure.md) | Idiomatic C# / structure review |
| [`05-recommendations.md`](05-recommendations.md) | Ranked generator / package / test actions |
| [`../BindingAudit/_SUMMARY.md`](../BindingAudit/_SUMMARY.md) | June per-library coverage synthesis (still authoritative for skip-reason depth) |

---

## Confidence & limits

- **High** for emission-surface claims backed by current `.cs` / `binding-report.json` lines.  
- **Medium** for “usable in production” without re-running sim/device product flows.  
- **Low** for any claim that needs a specific entitlement, Sandbox, or device (MusicKit, StoreKit purchase, WeatherKit, Tap to Pay).  
- Worktree RealityFoundation lacked `ModelComponent` (partial regen); **main Release CS** was authoritative for that finding.
