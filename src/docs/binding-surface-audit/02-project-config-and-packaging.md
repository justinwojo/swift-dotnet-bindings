# Project configuration, min OS, and packaging audit

Companion to [`01-delta-revalidation.md`](01-delta-revalidation.md). Scope:
`swift-dotnet-packages` Apple packages + third-party libraries. Static review of
csproj TFMs, `library.json` min-OS pins, `SupportedOSPlatform*` usage, empty
packages, wrapper policy, and ship readiness (MapLibre/Facebook).

Severity tags: **P0** ship-blocker · **P1** consumer footgun · **P2** inconsistency · **P3** polish.

---

## 1. Apple packages force `net10.0-ios26.2` (and siblings)

### What ships today

Representative Apple csprojs:

```xml
<!-- StoreKit2 -->
<TargetFrameworks>net10.0-ios26.2;net10.0-macos26.2;net10.0-maccatalyst26.2;net10.0-tvos26.2</TargetFrameworks>
<Version>26.2.8</Version>

<!-- RealityFoundation / RealityKit (iOS-only) -->
<TargetFrameworks>net10.0-ios26.2</TargetFrameworks>

<!-- MusicKit, WeatherKit, TipKit, CryptoKit, … — multi-TFM 26.2 family -->
```

Guides sometimes state **runtime** floors differently:

| Package | Guide install line | Package TFM | Runtime API floor (examples) |
|---|---|---|---|
| StoreKit2 | “iOS 26.2+, macOS 26.2+, …” | `net10.0-ios26.2` etc. | StoreKit 2 members often available far earlier; attributes emit per-member OS checks |
| ActivityKit | “any `net10.0-ios` TFM” + **iOS 16.2+** runtime for Request | built against 26.2 supplement | Live Activities ~16.1/16.2 |
| RealityFoundation | ECS usable earlier | `net10.0-ios26.2` | Generated members throw `PlatformNotSupportedException` below e.g. iOS 13.0 for `ModelComponent` |

### Is `net10.0-ios26.2` intentional?

**Yes — as the compile-time SDK surface.** Apple-framework bindings are generated from the
**Xcode iOS 26.2 SDK** `.swiftinterface` / ABI. Pinning the package TFM to `net10.0-ios26.2`:

1. Matches the Apple SDK version the binding was produced against.
2. Aligns NuGet package versioning (`26.2.x`) with the SDK lane.
3. Lets the generator emit the full 26.2 API surface with per-member `[SupportedOSPlatform("iosN.N")]` gates.

This is **not** the same thing as “your app must ship with deployment target 26.2.”

### Does it block apps targeting a lower min OS?

**Two layers:**

| Layer | Mechanism | Effect |
|---|---|---|
| **NuGet / TFM graph** | Package assets only under `lib/net10.0-ios26.2/` (etc.) | Consumer must use a TFM NuGet considers compatible with `net10.0-ios26.2`. In practice many apps set `TargetFramework=net10.0-ios26.2` (or multi-TFM including it) even if they want a lower **deployment** min. |
| **Deployment min** | App `SupportedOSPlatformVersion` / Info.plist | Independent of package TFM **if** restore succeeds. Generator emits runtime `OperatingSystem.IsOSPlatformVersionAtLeast` + `PlatformNotSupportedException` on members newer than the running OS. |

**ActivityKit guide** explicitly says: target any `net10.0-ios` TFM and set min via
`SupportedOSPlatformVersion` — **that guidance conflicts** with packages that **only**
produce `net10.0-ios26.2` assets unless NuGet’s framework compatibility maps
`net10.0-ios` → `net10.0-ios26.2` (platform-version TFM compatibility is easy to get wrong).

**StoreKit2 guide** saying “iOS 26.2+” conflates **SDK binding version** with **minimum deployment OS** — a **P1 documentation footgun**. Consumers reading the guide will over-constrain deployment targets.

### Recommendation

| Priority | Action |
|---|---|
| **P1** | Document clearly: *Package TFM = SDK surface (26.2); app deployment min = `SupportedOSPlatformVersion` + per-API attributes.* Fix StoreKit2 (and peers) guide “Requirements” tables. |
| **P2** | Verify NuGet restore matrix: can `net10.0-ios` / `net10.0-ios18.0` apps reference `SwiftBindings.Apple.*` 26.2 packages without retargeting? If not, either multi-target lower platform TFMs or document the hard requirement. |
| **P3** | Consider dual messaging in README badges: `SDK 26.2` vs `min OS (member-gated)`. |

**Severity:** **P1** (docs/consumer confusion), not a generator bug. Binding against iOS 26.2 SDK is **correct** for shipping the current Apple surface.

---

## 2. Third-party `library.json` minIOS vs csproj TFM vs OS attributes

### Defaults and samples

Repo root default (`Directory.Build.props`):

```xml
<TargetFramework>net10.0-ios</TargetFramework>
```

| Library | library.json minIOS | Library csproj TFM | Test `SupportedOSPlatformVersion` | Notes |
|---|---|---|---|---|
| Nuke | **15.0** (+ minMacOS 12, minTvOS 15) | `net10.0-ios; net10.0-macos; net10.0-tvos` | 15.0 / 12.0 / 15.0 | Multi-platform complete |
| Kingfisher | **13.0** | `net10.0-ios` only | (tests typically 15.0 pattern) | Lower pin than Nuke; iOS-only TFM |
| Lottie | 15.0 | `net10.0-ios` | 15.0 | Consistent |
| Stripe (meta) | 15.0 | `net10.0-ios` per product | — | Version skew: json `26.0.0` vs csproj `25.15.0` (**P2**) |
| BlinkID | 15.0 | `net10.0-ios` | 15.0 | `SwiftWrapperRequired=false` (binary) |
| MapLibre | 15.0 | present | — | Spike, not ship-ready |
| Facebook | 15.0 | multi-product | — | Does not compile |

### Kingfisher 13.0 vs Nuke 15.0 — **P2 inconsistency (explained, still messy)**

- **Kingfisher** upstream historically supports older iOS; package pins `minIOS: 13.0`.
- **Nuke** 13.x line documents iOS 15+; package pins `15.0`.
- **Default** in packages repo docs is `minIOS` default `"15.0"`.

This is **not necessarily wrong** (mirrors upstream), but:

1. Apps that standardize on iOS 14 cannot use Nuke but can use Kingfisher — fine.
2. **Neither library csproj sets `SupportedOSPlatformVersion`** on the **shipping** assembly — only **test** projects do for Nuke/Lottie/etc.
3. Generated bindings may still emit per-member OS attributes when the Swift API has availability; library-level min is not always mirrored into package metadata consumers see in NuGet UI.

**Severity:** **P2**. Align docs: “minIOS follows upstream; not a global product floor.” Optionally emit package `SupportedOSPlatformVersion` from `library.json` during pack so NuGet shows the right badge.

### Missing `SupportedOSPlatformVersion` on library csprojs — **P2**

Pattern:

- **Apple test apps:** often set `SupportedOSPlatformVersion` (sometimes `26.0`, sometimes real API min like WorkoutKit `17.0`).
- **Apple library csprojs:** typically **no** `SupportedOSPlatformVersion` — rely on TFM `ios26.2` + per-member attributes in generated CS.
- **Third-party library csprojs:** almost never set it; only tests do.

**Implication:** an app can restore a package and call APIs that crash/`PlatformNotSupportedException` on older OS without compile-time CA1416 warnings **unless** the generated surface carries `[SupportedOSPlatform]` and the app enables platform analyzers.

Generator **does** emit member-level attributes on Apple frameworks (verified on RealityFoundation `ModelComponent`, LiveCommunicationKit `ios17.4`, TipKit, etc.). Third-party coverage depends on Swift availability annotations surviving ABI → emitter.

**Recommendation:** during pack, set library `SupportedOSPlatformVersion` from `library.json` minIOS (and multi-TFM mins) so project-level analyzers fire even when a specific member lacks attributes.

---

## 3. `SwiftWrapperRequired=false` on Stripe (and peers)

### Where it appears

```xml
<!-- StripePaymentSheet, StripePayments, StripeUICore, Stripe, Connect, Identity, … -->
<SwiftWrapperRequired>false</SwiftWrapperRequired>

<!-- Also: BlinkID, BlinkIDUX -->
```

SDK default (`Swift.Bindings.Sdk` `Sdk.props`): **`true`**.

### What it means

From `Sdk.targets`:

- When wrapper compilation fails and `SwiftWrapperRequired=true` → **build error** (constructors, destroy, async wrappers missing → `DllNotFoundException` at runtime).
- When `false` → **warning only**; package can still pack/ship without a working Swift wrapper xcframework.

### Implications for Stripe — **P1**

1. **Soft-fail packaging:** any module whose wrapper fails to compile still produces a NuGet package. Public APIs that depend on `@_cdecl` wrappers (many inits, async bridges, destroy paths) may throw at runtime with little build-time signal if warnings are ignored in CI.
2. **Why it was set:** Stripe’s modules are large, dependency-heavy, and historically painful to wrapper-compile (ObjC/Swift mix, SPI surface). Soft-fail keeps the pipeline green while public subset still works via direct framework symbols / partial wrappers.
3. **Empty-ish modules** (UICore — see below) “succeed” even when there is nothing meaningful to wrap.
4. **Consumer risk:** drop-in PaymentSheet path may work while edge APIs fail with `DllNotFoundException` / missing entry points — hard to distinguish from app misconfiguration.

**Recommendation**

| Priority | Action |
|---|---|
| **P1** | CI: fail on wrapper **warning** for consumer-critical packages (`StripePayments`, `StripePaymentSheet`) even if property stays false for internal modules. |
| **P2** | Split policy: `SwiftWrapperRequired=true` for Payments/PaymentSheet/Core public; `false` only for pure-SPI shells (UICore, CameraCore). |
| **P2** | Document in each README when the package is known to ship without a full wrapper. |

BlinkID’s `false` is **justified** (closed-source binary; wrapper compile often impossible) — different case from Stripe source/zip modules.

---

## 4. Empty packages shipping (StripeUICore / StripeCameraCore)

### Facts

- **StripeUICore:** BindingAudit reported **0 emitted members / 538** — effectively empty public C# surface; everything `@_spi(STP)` / ModuleInternal. README now states: *almost never add this package directly; transitive only.*
- **StripeCameraCore:** same empty-by-design pattern.
- Both still have:

```xml
<PackageId>SwiftBindings.Stripe.UICore</PackageId>
<Version>25.15.0</Version>
```

and ship as real NuGet packages (and xcframeworks for native load).

### Packaging policy judgment

| Stance | Pros | Cons |
|---|---|---|
| **Ship transitive nupkgs (current)** | NuGet graph resolves native frameworks; consumers of PaymentSheet get UICore.framework embedded without manual NativeReference | Empty managed API confuses; `dotnet add package UICore` looks like a product |
| **Private/transitive-only assets** | Clearer product surface | Harder with current SwiftFrameworkDependency model |
| **Meta-package only** | One Stripe package | Loses granular versioning |

**Severity:** **P2 policy**. Not a generator failure — SPI pruning is correct. Shipping empty **managed** APIs as first-class packages is a **product clarity** issue.

**Recommendations**

1. Keep shipping **native** UICore/CameraCore as dependencies (required).
2. Mark packages on NuGet as **dependency-only** (description already starts this): disable public listing if possible, or prefix description with `[Transitive]`.
3. Avoid showcasing them in top-level package tables next to PaymentSheet.
4. Version align with parent Stripe lane (see §5).

---

## 5. Version / TFM skew and multi-TFM completeness

### Stripe version skew — **P2**

| Source | Version |
|---|---|
| `libraries/Stripe/library.json` | `"version": "26.0.0"` |
| Product csprojs (`PackageVersion` / `Version`) | **25.15.0** (PaymentSheet, UICore, …) |

Risk: fetch/pack scripts pull 26.0.0 binaries while NuGet identity stays 25.15.0, or vice versa — supply-chain / support confusion.

### Multi-TFM completeness

| Package class | TFMs | Completeness |
|---|---|---|
| Nuke | ios + macos + tvos | **Good** third-party multi-platform model |
| Most third-party (Lottie, Kingfisher, Stripe, BlinkID) | `net10.0-ios` only | OK if upstream is iOS-only; Kingfisher has macOS upstream not bound |
| Apple StoreKit2 / MusicKit / CryptoKit / WeatherKit / TipKit | ios26.2 + macos26.2 + maccatalyst26.2 + tvos26.2 (where applicable) | **Strong** |
| RealityFoundation / RealityKit / RoomPlan / FamilyControls / Translation | ios26.2 only | Matches platform reality |
| ActivityKit | ios26.2 | Correct (no macOS Live Activities package) |
| ProximityReader | ios + maccatalyst | OK |

**Gap (P3):** Kingfisher/Lottie could grow macos TFMs if product demand exists; not a defect.

**Apple multi-TFM vs single SDK pin:** all Apple legs use **26.2** platform versions together — consistent internally, same min-OS messaging issue as §1.

---

## 6. MapLibre / Facebook ship readiness

### MapLibre — **not ship-ready (spike)** 🛑

- README: *“SPIKE STATUS — exploratory binding attempt.”*
- Pure ObjC clang module over C++ core (`MLNMapView`).
- Present under `libraries/MapLibre/` with xcframework + csproj + tests scaffolding.
- Not positioned as a 1.0 consumer package.

**Action:** keep out of public package index / release notes until spike closes; optional: move under `experiments/`.

### Facebook — **not ship-ready (does not compile)** 🛑

`libraries/Facebook/tests/README.md` (explicit):

> DOES NOT COMPILE (SDK 0.16.0) … FBSDKLoginKit and FBSDKShareKit produce no C# bindings … Core/AEM fail to compile.

Recorded blockers:

| Code | Issue |
|---|---|
| G-ObjC-F | clang AST-dump ignores framework dependency `-F` for Login |
| G-Proxy | Share `SharingContentProxy` EveryProtocol not emitted |
| G-AEM | AEMNetworker CS0535 |
| G-StoreKit | Core references `StoreKit.Transaction` absent from C# StoreKit binding |

ObjC bridging work mentioned in generator history may help later; **current main tree is not consumer-ready**.

**Severity:** **P0** if listed as shipped; **OK** if kept experimental and unpublished.

---

## 7. Per-member OS floor vs package TFM (generator behavior)

Platform-aware min OS floor **is** present in generated Apple CS, e.g.:

```csharp
[SupportedOSPlatform("ios17.4")]
public interface IConversationManagerDelegate { … }

// RealityFoundation ModelComponent materials_Get:
if (OperatingSystem.IsOSPlatform("ios") &&
    !OperatingSystem.IsOSPlatformVersionAtLeast("ios", 13, 0))
    throw new PlatformNotSupportedException("… requires iOS 13.0 …");
```

This is the **right** long-term model: package compiled against newest SDK, members gated to real availability.

**Remaining gaps**

1. Package-level messaging still says “requires iOS 26.2” for some kits (**docs**).
2. Third-party packages often lack package-level `SupportedOSPlatformVersion`.
3. Apps must enable platform compatibility analyzers to benefit at compile time.

---

## 8. Summary table

| Issue | Severity | Status | Fix owner |
|---|---|---|---|
| Apple TFM = SDK 26.2 surface | — | **Intentional / correct** | Document only |
| Guides claim app min OS = 26.2 | **P1** | Misleading | Docs (StoreKit2 etc.) |
| NuGet TFM compatibility for lower app TFMs | **P1** | Verify | Packages + docs |
| Kingfisher minIOS 13 vs Nuke 15 | **P2** | Upstream-true, inconsistent defaults | library.json policy note |
| Library csproj missing SupportedOSPlatformVersion | **P2** | Common | Pack pipeline from library.json |
| Stripe `SwiftWrapperRequired=false` | **P1** | Footgun for critical modules | Split policy + CI |
| Empty StripeUICore/CameraCore nupkgs | **P2** | By design SPI | Packaging policy / listing |
| Stripe json 26.0.0 vs csproj 25.15.0 | **P2** | Skew | Align versions |
| MapLibre spike | **P3**/gate | Not ship | Keep unpublished |
| Facebook non-compiling | **P0** if published | Blocked | Generator ObjC work; do not ship |
| Multi-TFM Apple completeness | OK | Good | — |
| Multi-TFM third-party (Nuke) | OK | Model to copy | — |

---

## 9. Recommended packaging policy (concise)

1. **Apple packages:** keep `net10.0-*-26.2` TFMs as SDK pins; document deployment min separately; emit/rely on member `[SupportedOSPlatform]`.
2. **Third-party:** `library.json` minIOS is source of truth → write `SupportedOSPlatformVersion` into csproj at generate/pack time.
3. **Wrapper policy:** default true; allow false only for binary/SPI shells; never soft-fail PaymentSheet/Payments without a tracked warning budget.
4. **Empty SPI packages:** ship as transitive native carriers; hide from primary catalog.
5. **Experimental (MapLibre, Facebook):** do not include in release lanes until compile + one smoke test pass.

---

*Generated 2026-07-11. Does not modify generator or package source — advisory audit only.*
