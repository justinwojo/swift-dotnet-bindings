# Binding Audit — Delta Re-validation (2026-07-11)

Static re-check of the 2026-06-27 BindingAudit findings against **current** generated C#
artifacts. Not a runtime gate.

## Artifacts consulted

| Source | Path |
|---|---|
| Preferred worktree CS | `/Users/wojo/Dev/swift-dotnet-packages/.claude/worktrees/agent-a8e7be4395c90a492/apple-frameworks/*/obj/Debug/net10.0-ios26.2/swift-binding/` |
| Main-tree Apple (when worktree incomplete) | `/Users/wojo/Dev/swift-dotnet-packages/apple-frameworks/*/obj/Release/net10.0-ios26.2/swift-binding/` |
| Third-party | `/Users/wojo/Dev/swift-dotnet-packages/libraries/{Nuke,Lottie,Stripe,Kingfisher,MapLibre,Facebook}/` |
| Baseline audit | `src/docs/BindingAudit/` (esp. `_SUMMARY.md`) |

**Status vocabulary**

| Status | Meaning |
|---|---|
| **STILL OPEN** | Same defect shape present in current CS / binding-report |
| **LIKELY FIXED** | Surface no longer shows the defect; runtime not re-proven here |
| **PARTIAL** | Material improvement, residual gap remains |
| **NEEDS RUNTIME PROOF** | Static surface looks usable; end-to-end ABI/flow not re-verified |

---

## A. Finding-by-finding status

| # | Finding | Severity | Status | Evidence (current) |
|---|---|---|---|---|
| 1 | RealityFoundation `ModelComponent.Materials` getter throws (EveryProtocol) | **P0 / Correctness** | **STILL OPEN** | Main Release CS: getter still hard-throws; `MaterialProxy` still `EveryProtocolConformanceSkipped` |
| 2 | RoomPlan `RoomCaptureViewDelegate` silent dead callbacks | **P0 / Correctness** | **STILL OPEN** | Getter throws; proxy skipped; no factory in `GetOrCreate` |
| 3 | LiveCommunicationKit `didActivate`/`didDeactivate` collapse | **P1 / Correctness** | **RESOLVED** (commit `52ac336a`; audit evidence was stale pre-fix artifact) | Now emits distinct `ConversationManagerDidActivate`/`DidDeactivate`; pinned by BindingTests + unit tests |
| 4 | CryptoKit P256/P384/P521 ECDSA sign+verify | **P1 / Coverage** | **PARTIAL → likely usable** | Open-generic still `GenericProtocolConstraint` in report; **CSM closed overloads** emit `Signature(byte[]/Data)` + `IsValidSignature(ECDSASignature, byte[]/Data/Digest)` — needs runtime proof |
| 5 | MusicKit library-read `MusicLibraryResponse.items` → AnyType | **P1 / Coverage** | **STILL OPEN** | binding-report: `AnyTypeFallback` on `items` |
| 6 | TipKit `shouldDisplay` / `statusUpdates` / `invalidate` on AnyTip | **P1 / Coverage** | **PARTIAL** | Now on `ITip` + reverse proxy; **not** re-emitted as working members on concrete `AnyTip`; stream props are `AnyType` |
| 7 | Name stutter `TypeType` / `OfferTypeTypeType` | **P3 / Quality** | **STILL OPEN** | StoreKit2 still emits `OfferTypeTypeType`, `OwnershipTypeType` |
| 8 | ActivityKit generic path vs facade | **Architectural** | **STILL OPEN (by design)** + facade **NEEDS RUNTIME PROOF** for this pass | Direct `Activity<T>` still unbindable; guide still points at `Swift.ActivityKit.LiveActivity` |
| 9 | Stripe PaymentSheet deferred `confirmHandler` closure gap | **P1 / Coverage** | **STILL OPEN** (by prior audit + no counter-evidence) | No generated `obj/.../swift-binding` in tree; API/docs still describe deferred flow as blocked; tests never construct IntentConfiguration confirm path |
| 10 | FamilyControls `IExistentialBoxable` / container leak | **P3 / Quality** | **STILL OPEN** | `FamilyActivitySelection : IExistentialBoxable`; revoke/auth callback paths still expose `ExistentialContainer1` / `SwiftResult` shapes |

---

### 1. RealityFoundation `ModelComponent.Materials` — **STILL OPEN** (P0)

**Baseline (2026-06-27):** getter always throws `NotSupportedException` because `Material.MaterialProxy` was not emitted (`EveryProtocolConformanceSkipped` / higher-kind constraints).

**Current (main Release CS):**

```csharp
// RealityFoundation.cs ~79751 (net10.0-ios26.2 Release)
public IReadOnlyList<IMaterial> Materials
{
    get => throw new NotSupportedException(
        "Protocol proxy not available: EveryProtocol conformance was not emitted.");
    set { using var __val = SwiftArray<ExistentialContainer1>
            .FromEnumerable(value.Select(e =>
                ExistentialContainerFactory.CreateOwnedExistential1<IMaterial>(e)));
          Materials_Set(__val); }
}
```

`binding-report.json` still records:

```text
MaterialProxy / RealityFoundation.Material
  Reason: EveryProtocolConformanceSkipped
  Details: … UnsatisfiedHiddenRequirements
```

**Delta notes**

- **Setter path improved slightly** — uses `CreateOwnedExistential1<IMaterial>` (write-side existential packing), so materials can still be **set** from concrete `IMaterial` implementers (`SimpleMaterial`, etc.).
- **Getter path unchanged** — still throws; consumers cannot inspect materials after construction.
- Private `Materials_Get()` P/Invoke + `@_cdecl` wrapper **exist** — only the public projected getter is stubbed.

**Worktree caveat:** agent worktree RealityFoundation CS lacked `ModelComponent` entirely (incomplete/partial regen). Main Release is authoritative for this finding.

---

### 2. RoomPlan `RoomCaptureViewDelegate` — **STILL OPEN** (P0)

**Current worktree CS:**

```csharp
// RoomPlan.cs ~5015–5051
private IRoomCaptureViewDelegate? Delegate_Get()
{
    throw new NotSupportedException(
        "Protocol proxy not available: EveryProtocol conformance was not emitted.");
}

public virtual IRoomCaptureViewDelegate? Delegate
{
    get => Delegate_Get();
    set {
        // GetOrCreate WITHOUT a proxy factory callback:
        var __container = ExistentialContainerFactory
            .GetOrCreate<IRoomCaptureViewDelegate>(__v);
        …
    }
}
```

`binding-report.json`:

```text
RoomCaptureViewDelegateProxy
  Reason: EveryProtocolConformanceSkipped
```

`IRoomCaptureViewDelegate` only has two members, both protocol-extension defaults that throw NSE on the interface (`CaptureView(roomData…)` / `CaptureView(processedResult…)`).

**Contrast that proves partial EveryProtocol progress:** `IRoomCaptureSessionDelegate` **does** get `RoomCaptureSessionDelegateProxy` with a C#-impl ctor — session-level callbacks can be wired; **view** delegate still cannot.

**Impact unchanged:** compiling a C# `IRoomCaptureViewDelegate` and assigning it either (a) fails on get, or (b) set-side packing without a reverse-dispatch proxy → callbacks never fire / crash. Silent-dead risk remains for the view path.

---

### 3. LiveCommunicationKit activate/deactivate collapse — **RESOLVED** (was P1)

> **RESOLVED (session 03, 2026-07-12).** The generator fix landed in commit `52ac336a`
> (`ProtocolMethodDisambiguator`, threaded through every interface/proxy/receiver/validator/
> forward-witness site). The "STILL OPEN" evidence below is a **stale pre-fix artifact** (April
> `LiveCommunicationKit.cs`, mtime pre-`52ac336a`): the current generator emits
> `ConversationManagerDidActivate` / `ConversationManagerDidDeactivate` as distinct members, each
> routed to its own reverse-dispatch vtable slot. Verified by the `DuplicateSignatureDisambiguation`
> BindingTests fixture (pair + triple, reverse-dispatch identity assertions), `ProtocolHandlerOutputTests`,
> and `WitnessDispatchEmitterTests`. The label-inclusive `EveryProtocolEmitter.GetMethodKey` always
> allocated both native slots, so the C# fill (not the native layout) was the only gap, and it is closed.
> Residual (by design): **static** label-only requirements still collapse — see `roadmap.md` "Protocol-side
> dedup ignores argument labels". The stale snippet below is retained for the audit trail only.

**Stale pre-fix interface** (`LiveCommunicationKit.cs` ~5650, April artifact):

```csharp
public interface IConversationManagerDelegate
{
    void ConversationManager(ConversationManager manager, Conversation conversation);
    void ConversationManagerDidBegin(ConversationManager manager);
    void ConversationManagerDidReset(ConversationManager manager);
    void ConversationManager(ConversationManager manager, ConversationAction action);
    void ConversationManager(ConversationManager manager, AVAudioSession audioSession);
}
```

Swift still has two distinct requirements:

- `conversationManager(_:didActivate:)`
- `conversationManager(_:didDeactivate:)`

**Delta notes**

- Selector-style rename **did land for some collisions** (`DidBegin` / `DidReset`).
- The **AVAudioSession pair is still collapsed** into a single C# method — consumers cannot distinguish activate vs deactivate.
- Wrapper Swift still declares **both** methods on the EveryProtocol side (`didActivate` / `didDeactivate`), so the native side expects two slots; C# only exposes one fillable method.

**Status nuance:** label-collision disambiguation is **PARTIAL** project-wide, **not fixed** for this high-value pair.

---

### 4. CryptoKit NIST ECDSA sign+verify — **PARTIAL → likely usable** (was P1 open in June)

`binding-report.json` still lists the **open-generic** methods as skipped:

| Member | Type | Reason |
|---|---|---|
| `isValidSignature` | `P256.Signing.PublicKey` | `GenericProtocolConstraint` (open generic) |
| `signature` | `P256.Signing.PrivateKey` | `GenericProtocolConstraint` (open generic) |
| same pattern | P384 / P521 | same |
| `ECDSASignature` types | P256/P384/P521.Signing | `OwnedByAppleSupplement` (type lives in `SwiftBindings.Apple`) |

**But the generated C# now emits concrete CSM overloads** (post–`af4f8aef` frozen-trivial CSM + related work). Worktree `CryptoKit.cs` shows, for P256 Signing:

```csharp
// PrivateKey — concrete specializations (not the obsolete open generic)
public ECDSASignature Signature(global::Swift.Foundation.Data data);
public ECDSASignature Signature(byte[] data);
public ECDSASignature Signature(CryptoKit.SHA256Digest data);
// … SHA384/512 + SHA3 digests …

// PublicKey
public bool IsValidSignature(ECDSASignature signature, byte[] data);
public bool IsValidSignature(ECDSASignature signature, global::Swift.Foundation.Data data);
public bool IsValidSignature(ECDSASignature signature, CryptoKit.SHA256Digest data);
// … digests …
```

`ECDSASignature` is intentionally owned by the Apple supplement (`// Unsupported: type 'ECDSASignature' — OwnedByAppleSupplement`) and referenced as `Swift.CryptoKit.P256.Signing.ECDSASignature` — that is **by design**, not a missing type.

**Revised judgment**

| Path | Status |
|---|---|
| Open-generic `Signature<D>` / `IsValidSignature<S,D>` | Still `[Obsolete SB0001]` / report-skipped — correct |
| Closed `byte[]` / `Data` / digest specializations | **Emitted** — headline C# path exists |
| Runtime round-trip NIST ECDSA | **NEEDS RUNTIME PROOF** — CryptoKit tests still cover Curve25519 + AEAD, not P256 ECDSA KATs |
| CRYPTOKIT-GUIDE accuracy | Re-check: June audit said the guide over-claimed ECDSA; if guide now documents CSM specializations, close the doc bug; if still wrong, update |

**Usable today (unchanged + improved)**

- Hashing, `SymmetricKey`, AES.GCM / ChaChaPoly, Curve25519, HMAC CSM, ML-DSA context paths.
- NIST P-curve **sign/verify now appears constructible** via CSM — treat as **NEEDS RUNTIME PROOF**, not “unreachable.”

---

### 5. MusicKit library-read `items` — **STILL OPEN** (P1)

```json
{
  "Name": "items",
  "ContainingType": "MusicKit.MusicLibraryResponse",
  "Reason": "AnyTypeFallback",
  "Details": "Property type resolved to AnyType (MusicKit.MusicItemCollection<Swift.AnyType>)."
}
```

`MusicLibraryResponse<TMusicItemType>` is emitted as a generic shell with metadata/equality, but **no usable `Items` projection**. Catalog search + players remain the workable paths (with shims on main tree). Library enumeration loop still dead without a per-T concretization or existential projection fix.

---

### 6. TipKit query members — **PARTIAL** (P1)

**What improved**

Protocol-extension members now appear on `ITip`:

```csharp
public interface ITip
{
    …
    bool ShouldDisplay => throw … protocol extension default …;
    Swift.AnyType StatusUpdates => throw …;
    Swift.AnyType ShouldDisplayUpdates => throw …;
    void Invalidate(Tips.InvalidationReason reason) => throw …;
}
```

Reverse-dispatch `TipProxy` has vtable slots + receivers for `shouldDisplay`, `statusUpdates`, `shouldDisplayUpdates`, `invalidate` (C#→Swift conformer path).

**What remains broken for consumers holding `AnyTip`**

- Concrete `AnyTip` only re-emits identity display members (`Id`, `Title`, `Message`, `Image`, `Options`) — **not** `ShouldDisplay` / `Invalidate` as P/Invoke-backed properties.
- Calling `ShouldDisplay` / `Invalidate` via the interface default on a Swift-backed value throws NSE.
- `StatusUpdates` / `ShouldDisplayUpdates` project as `Swift.AnyType` (Self-typed async sequences) — unusable typed streams.
- Wrapper still `fatalError`s for Self-typed stream properties on EveryProtocol.

**Judgment:** protocol-extension **symbol walk** landed; **concrete AnyTip query usability** did not. Not “fixed.”

---

### 7. Name stutter `*TypeType*` — **STILL OPEN** (P3)

StoreKit2 still ships:

- `Transaction.OfferTypeTypeType` (tests explicitly construct `Introductory` / `Promotional` / `Code`)
- `Transaction.OwnershipTypeType`
- Nested `OfferTypeType` vs `OfferTypeTypeType` distinction for offer payload vs offer kind

Cosmetic / discoverability only; no ABI risk.

---

### 8. ActivityKit generic path vs facade — **STILL OPEN (architectural)** / facade **NEEDS RUNTIME PROOF**

Guides still state the permanent limitation: C# cannot satisfy compiler-synthesized `ActivityAttributes` (`Codable & Hashable`), so direct `Activity<T>.request` is not a viable consumer path.

**Working path (documented):** `Swift.ActivityKit.LiveActivity.Request/Update/End` via `SwiftBindings.Apple` supplement + JSON payload + hand-copied SwiftUI widget attributes.

**This delta pass:** no independent runtime re-execution of Live Activity request/update/end. Treat facade as **documented correct mitigation**; ship confidence remains **NEEDS RUNTIME PROOF** outside this static audit (guide claims prior sim+device verification).

---

### 9. Stripe PaymentSheet deferred `confirmHandler` — **STILL OPEN** (P1)

- No `obj/.../swift-binding/` tree present under `StripePaymentSheet` in main or worktree (bin DLLs/XML only).
- Shipped XML surface shows `PaymentSheet`, `CustomerSheet`, configuration/appearance types; no counter-evidence that async `confirmHandler` / `confirmationTokenConfirmHandler` properties became constructible.
- Tests still exercise Appearance / Configuration property round-trips and result enum factories — **not** IntentConfiguration deferred confirm.

Absent a regenerated binding-report showing `confirmHandler` emitted, the June gap stands.

---

### 10. FamilyControls existential leak — **STILL OPEN** (P3)

Current CS still has:

- `FamilyActivitySelection : … IExistentialBoxable` (runtime marker on public type)
- Auth/revoke callback shapes historically typed with `SwiftResult<…, ExistentialContainer1>` (impl-detail leak)

**Not a functional blocker** for the headline auth flow (`RequestAuthorizationAsync` is present and idiomatic). Quality polish only. `AuthorizationCenter.Shared` / `RequestAuthorizationAsync` remain the right consumer path.

---

## B. Core feature usability (headline workflows)

Static constructibility / callability only.

### StoreKit2 — **headline path constructible** ✅ (runtime unproven here)

| Step | C# surface |
|---|---|
| Capability | `AppStore.CanMakePayments` |
| Load products | `Product.ProductsAsync(IEnumerable<string>, CancellationToken)` |
| Purchase | `product.PurchaseAsync(...)` → `Product.PurchaseResult` |
| Entitlements | `Transaction.CurrentEntitlements` |
| Finish | Transaction finish APIs on `Transaction` |

**Gaps vs native:** `OfferTypeTypeType` stutter; some purchase-option / advanced-commerce edges; tests still don’t `await` product load or purchase (see §D).

### CryptoKit — **hashing + AEAD + Curve25519 usable** ✅ / **NIST ECDSA emitted, unproven** ⚠️

| Workflow | Status |
|---|---|
| SHA2/SHA3 digest | Usable |
| AES.GCM / ChaChaPoly | Usable (tests round-trip) |
| SymmetricKey | Usable |
| Curve25519 sign/verify | Usable via CSM extensions (tests) |
| P256/P384/P521 ECDSA sign/verify | **CSM overloads emitted** (`Signature(byte[])` / `IsValidSignature`); open generic still skipped; **runtime KAT missing** |
| Raw digest byte export | Still limited / no general `ToByteArray` unlock noted |

### MusicKit — **catalog + player yes** / **library read no** ⚠️

| Workflow | Status |
|---|---|
| `MusicAuthorization.RequestAsync` / `CurrentStatus` | Present |
| `ApplicationMusicPlayer.Shared` / `SystemMusicPlayer.Shared` | Present |
| Catalog search (main + shims) | Documented workable path |
| `MusicLibraryResponse<T>.items` | **Dead** (AnyType) |

### WeatherKit — **service entry constructible** ✅

| Surface | Status |
|---|---|
| `WeatherService.Shared` | Present |
| `WeatherQuery<T>.Current/Hourly/Daily/…` | Present |
| Typed multi-dataset weather fetch | Present (single-dataset generic `weather<T>` historically dark) |
| `GetAttributionAsync` | Present; tests fire dispatch |

### Nuke — **headline load path constructible** ✅

README-level:

```csharp
var pipeline = ImagePipeline.Shared;
var request = new ImageRequest(url);
// load / prefetcher APIs on pipeline
```

Tests: construction + priority/options, not full download/cache e2e on every run.

### Stripe PaymentSheet — **drop-in UI path constructible** ⚠️ / **deferred confirm no**

| Workflow | Status |
|---|---|
| `PaymentSheet.Appearance` / `Configuration` | Present; property tests |
| Client-secret present path | Prior audit: constructible |
| Deferred / server confirm (`confirmHandler`) | **Still not constructible** |
| Apple Pay *inside sheet* handlers | Prior audit: blocked (closure / PassKit) |

### Lottie — **playback path constructible** ✅

```csharp
var animation = LottieAnimation.Named(...); // or Filepath
var animView = new LottieAnimationView();
animView.Animation = animation;
animView.Play();
```

Tests load bundled JSON and exercise playback lifecycle — **stronger than most Apple kits**.

---

## C. Cross-cutting generator themes (delta)

| Theme | June priority | July status |
|---|---|---|
| EveryProtocol proxy for protocol carriers | Tier 1 | **Still top risk** (Materials, RoomPlan view delegate) — some proxies *do* emit (RoomPlan session, Tip reverse) |
| Label-collision rename on protocol methods | Tier 2 | **Partial** (LCK DidBegin/DidReset); AVAudioSession pair still collapsed |
| CSM / DataProtocol concretization for typed returns | Tier 1 | CryptoKit ECDSA CSM appears landed (needs runtime KAT); MusicKit `items` still open |
| Protocol-extension member walk | Tier 3 | TipKit: **partial** (interface + reverse proxy, not concrete AnyTip) |
| `*TypeType*` stutter | Tier 3 | Unchanged |
| Test depth | Process | CryptoKit improved; StoreKit2/MusicKit/Stripe still shallow |

---

## D. Test depth spot-check (5 apps)

BindingAudit claim: tests are shallow (metadata vs real async flows). **Mostly still true**, with one notable improvement.

### 1. StoreKit2 — **shallow (unchanged)** 🛑

`apple-frameworks/StoreKit2/tests/Tests.cs`:

- `Transaction.All` / `CurrentEntitlements` / `Updates` — create sequence objects, **no enumeration / no await**
- Enum `CaseTag` ordinal checks, singleton non-null
- **No** `await Product.ProductsAsync`, **no** purchase, **no** finish

Confirms BindingAudit: “30 cases await nothing.”

### 2. CryptoKit — **improved (was shallow, now medium)** ⚠️→✅ partial

`CryptoKit/tests/Tests.cs` now includes:

- AES.GCM round-trip + tamper detection
- ChaChaPoly round-trip
- HMAC SHA256/384 incremental == one-shot
- Curve25519 sign+verify round-trip
- ML-DSA context-string verify
- Still lots of metadata / enum scaffolding

**Delta vs June:** no longer “no SHA/AEAD KAT”; AEAD + Curve25519 KATs exist. NIST P-curve ECDSA CSM overloads now appear in generated C# but are still untested at runtime.

### 3. MusicKit — **shallow (unchanged)** 🛑

`MusicKit/tests/Tests.cs`:

- `MusicAuthorization.CurrentStatus`, player `Shared` non-null
- Enum descriptions / CaseTags
- Metadata loads for Album/Artist/Song/…
- **No** catalog search await, **no** library response, **no** queue/play round-trip

### 4. Stripe — **config/metadata heavy (unchanged)** 🛑

`libraries/Stripe/tests/Program.cs`:

- Phases for Appearance corner radius, fonts, colors, Configuration properties
- Result enum factory + TryGet
- **No** `PresentAsync`, **no** PaymentIntent confirm, **no** deferred IntentConfiguration
- Historical AppInfo Skip branch still an external cleanup item (generator fix already landed)

### 5. WeatherKit — **slightly better than pure metadata** ⚠️

- Mostly enum/singleton/metadata
- **One** real async dispatch: `WeatherService.GetAttributionAsync` (expects error or success without crash)
- No location-based `weather` payload round-trip

### Bonus: Lottie — **deeper than Apple set** ✅

Bundled JSON load + animation view + playback lifecycle — closer to a headline functional gate.

---

## E. Priority residual list (after delta)

| Priority | Item | Action |
|---|---|---|
| **P0** | RealityFoundation Materials getter + Material EveryProtocol | Emit proxy or set-only surface + docs; pin BindingTests red test |
| **P0** | RoomPlan `RoomCaptureViewDelegate` proxy | Same EveryProtocol carrier class work; pin silent-callback repro |
| ~~**P1**~~ **DONE** | LCK activate/deactivate rename | Landed in `52ac336a` (`ProtocolMethodDisambiguator`); instance label-only pairs disambiguated + pinned. Residual: statics only (by design) |
| **P1** | CryptoKit NIST ECDSA | Add runtime KAT for P256 `Signature(byte[])` / `IsValidSignature`; confirm supplement `ECDSASignature` wiring |
| **P1** | MusicKit `MusicLibraryResponse.items` | Per-T collection projection |
| **P1** | Stripe deferred confirmHandler | Async closure-typed config properties |
| **P2** | TipKit AnyTip query members | Re-emit protocol-extension defaults onto concrete types with real wrappers |
| **P3** | `*TypeType*` stutter; FamilyControls container leak | Naming / API polish |
| **Process** | Test depth | One headline async/round-trip per package (StoreKit2 ProductsAsync empty-set; MusicKit auth; Stripe present error path) |

---

## F. Methodology notes / caveats

1. **Static only** — “STILL OPEN” is emission-surface truth; some setters/getters may have additional runtime bugs not re-probed.
2. **Artifact age variance** — worktree LCK `binding-report.json` timestamps predate June audit; CS content still matches the collapse finding. RealityFoundation Materials verified on **main Release** because worktree lacked `ModelComponent`.
3. **Stripe** — no local swift-binding tree; conclusion relies on shipped XML + tests + June binding-report conclusions without a regenerating counterexample.
4. **Does not modify generator source** — advisory only.

---

*Generated 2026-07-11 as a delta over BindingAudit (2026-06-27).*
