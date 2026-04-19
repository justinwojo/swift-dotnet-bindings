# Ship Blockers — Round 3 (SDK 0.8.0 + SwiftBindings.Apple 26.0.0)

**Validation date:** 2026-04-19
**SDK version:** SwiftBindings.Sdk 0.8.0, SwiftBindings.Runtime 0.8.0, SwiftBindings.Apple 26.0.0
**Scope:** 29 public packages — 11 Stripe products + 6 third-party libraries + 12 Apple frameworks.
**Source of truth for counts:** `/Users/wojo/Dev/swift-dotnet-packages/SHIP-READINESS.md` §Round 3 Results.
**Prior round:** `ship-blockers-round2.md`.

---

## 1. Summary

Round 3 is a large net-positive vs Round 2. Every Round 2 build regression (A–F) is fixed, the SwiftBindings.Apple 26.0.0 supplement eliminates most of the Swift-only value-type friction, and the full Stripe suite is now clean (0 SB0001 across all 12 products, was 28 total).

Two categories of blocker remain:

1. **Generator: skip-but-still-reference of undeclared generic types** (MusicKit, WeatherKit). Same shape as Round 2 Regression E but with different generics. Build-fail on all 4 TFMs.
2. **Runtime: sim-test dylib load regression.** 14/17 sim tests fail at runtime with one of two errors despite the dylibs being bundled. Not a library-level issue — blocks validation of everything. Must be diagnosed before shipping.

A secondary tooling blocker (`spm-to-xcframework` header injection) affects clean Stripe rebuilds from SPM; current xcframeworks still work.

---

## 2. Ship status

### SHIP — 20 packages (0 SB0001, builds clean on every TFM)

**Stripe (11 public):** Stripe (umbrella), StripeCore, StripePayments, StripePaymentSheet, StripePaymentsUI, StripeApplePay, StripeConnect, StripeIdentity, StripeIssuing, StripeCardScan, StripeFinancialConnections.
(StripeUICore, Stripe3DS2, StripeCameraCore are `internal` — xcframeworks built, no NuGet.)

**Apple frameworks (8):** ActivityKit, FamilyControls, LiveCommunicationKit, ProximityReader, RoomPlan, StoreKit2, Translation, WorkoutKit.

**Third-party (1):** BlinkIDUX.

### NEAR-SHIP — 7 packages (SB0001 > 0, builds clean)

| Library | SB0001 (iOS TFM) | Primary cause |
|---|---|---|
| BlinkID | 1 | (spot-check; remaining actor-isolated method) |
| Nuke | 5 | Callback/closure constructors |
| Lottie | 8 | Callback/closure, async Task factory |
| Mappedin | 10 | Callback/closure, async Task factory |
| Kingfisher | 39 | Fluent builder chains on `KingfisherWrapper<UIImageView>` (architectural) |
| TipKit | 48 | Result-builder DSL (`@_alwaysEmitIntoClient`, no binary symbol) |
| CryptoKit | 152 | SHA3/variadic hash methods; HPKE generics |

### BLOCKED — 2 packages (builds fail)

| Library | Failure | Errors | TFMs |
|---|---|---|---|
| **MusicKit** | `MusicRelationshipProperty<,>` referenced in `MusicKit.cs` but not declared (generator emits an SB0001 skip for the type itself, then still emits calls that reference it). | 432 × CS0234 | ios / tvos / macos / maccatalyst 26.2 |
| **WeatherKit** | `Forecast<>` referenced in `WeatherKit.cs` but not declared (same pattern). | 112 × CS0234 | ios / tvos / macos / maccatalyst 26.2 |

---

## 3. Blocker #1 — Generator: skip-but-still-reference on undeclared generic types

### Symptom

```
apple-frameworks/MusicKit/obj/Debug/net10.0-macos26.2/swift-binding/MusicKit.cs(7184,29):
  error CS0234: The type or namespace name 'MusicRelationshipProperty<,>' does not exist
                in the namespace 'MusicKit' (are you missing an assembly reference?)

apple-frameworks/WeatherKit/obj/Debug/net10.0-tvos26.2/swift-binding/WeatherKit.cs(9461,79):
  error CS0234: The type or namespace name 'Forecast<>' does not exist in the namespace 'WeatherKit'
```

### Shape

The emitter decides `MusicRelationshipProperty<Source, Target>` (and `Forecast<T>`) cannot be bound — reasonable — and drops an SB0001 skip for the generic type declaration. It does *not* prune the dozens of call sites and properties whose signatures reference `MusicRelationshipProperty<...>` / `Forecast<...>`. The remaining file therefore refers to a name that was never emitted. Every TFM hits this (not platform-specific).

### Relation to Round 2 Regression E

Round 2 had the same class: "unbound `TT1..TT6` generics (424 CS0246 per TFM) in WeatherKit (macos/maccatalyst)". The `TT1..TT6` symptom is gone in Round 3 — the fix resolved the unbound-type-parameter variant but did not generalize to "skipped generic type declaration whose uses were also not pruned." Expect more libraries to trip on this in future SDK frames until the emitter enforces the invariant **"if a type is skipped, every use must be skipped or stubbed."**

### Fix shape (for next SDK)

Two plausible approaches:

1. **Emit stubs for skipped generic types** — declare the generic as an empty `public struct` (or `public static class`) in the target namespace so references compile. All members degrade to SB0001 but the file links.
2. **Prune references in lockstep** — when the emitter marks a generic type "skipped," walk the members/properties/methods already queued for emission and drop anyone whose signature mentions it. Emit SB0001 at each call site instead.

Option 2 is cleaner but touches scheduling in the emitter. Option 1 is a smaller surgical patch — suitable for an 0.8.x revision.

### Repro

```bash
cd /Users/wojo/Dev/swift-dotnet-packages
dotnet build apple-frameworks/MusicKit/SwiftBindings.MusicKit.csproj -v q  # 432 errors
dotnet build apple-frameworks/WeatherKit/SwiftBindings.WeatherKit.csproj -v q  # 112 errors
```

---

## 4. Blocker #2 — Runtime: sim-test dylib load regression

### Symptom (two distinct errors)

**Error 2a — SwiftBindingsRuntime not resolving symbols:**
```
SwiftString operations require the SwiftBindingsRuntime native library.
  Ensure libSwiftBindingsRuntime.dylib is included in your application bundle.
SwiftRuntimeException: Failed to get existential metadata for ExistentialContainer1
  (1 protocol(s)). Ensure libSwiftBindingsRuntime.dylib is included in your application bundle.
```
Affects: Nuke, Lottie, Kingfisher, BlinkID, Mappedin, Stripe — i.e. anything that marshals Swift strings or reads existential metadata.

**Error 2b — per-library wrapper dylib not found:**
```
[FAIL: AuthorizationCenter.Shared — FamilyControlsSwiftBindings]
[FAIL: WorkoutScheduler.Shared singleton — WorkoutKitSwiftBindings]
[FAIL: Tips.Configure() — TipKitSwiftBindings]
[FAIL: AppStore.CanMakePayments — StoreKitSwiftBindings]
```
Affects: ActivityKit, FamilyControls, RoomPlan, StoreKit2, TipKit, WorkoutKit, Translation, plus any library whose generated wrapper exposes a method called from C#.

### Scope — sim validation

| Result | Count | Libraries |
|---|---|---|
| **PASS** | 3 | BlinkIDUX, LiveCommunicationKit, ProximityReader |
| **FAIL at runtime** | 13 | Nuke, Lottie, Kingfisher, BlinkID, Mappedin, Stripe, ActivityKit, FamilyControls, RoomPlan, StoreKit2, TipKit, Translation, WorkoutKit |
| **Build failed (test code)** | 1 | CryptoKit (test references `P521.Signing.ECDSASignature` — test-level typo; library builds) |

The 3 passing tests only exercise type metadata via the Swift runtime's type resolver. They do *not* call into the per-library wrapper dylib and do *not* marshal strings. Every other test does one or both and fails at the first attempt.

### Diagnosis hints

- The dylibs are *bundled*: `<app>.app/Frameworks/libSwiftBindingsRuntime.dylib` and `<app>.app/Frameworks/<Library>SwiftBindings.framework/` are present.
- Errors are thrown from managed code's fallback-path check (`if (runtimeHandle == 0) throw ...`), implying `NativeLibrary.TryLoad` / `dlopen` is failing or the P/Invoke is resolving to a stub.
- Likely surfaces: `[LibraryImport]` entry-point resolution, `DllImportResolver` registration order, rpath on the dylib, or a codesign / load-command mismatch between the 0.8.0 dylib and what the generator emits.
- Ground-truth next step: run one failing test under a debugger or with `DYLD_PRINT_LIBRARIES=1 DYLD_PRINT_BINDINGS=1` and check (a) whether the dylib was dlopened, (b) whether the expected symbol exists in it.

### Why this is Round 3's top priority

Even with perfect build quality, no library outside the 3 passing tests can actually be validated end-to-end. Shipping 0.8.0 in this state ships packages that throw on first use of any string or wrapper-backed call. The generator-build gate does not cover this — it relies on dlopen working.

---

## 5. Tooling regression — `spm-to-xcframework` header injection

### Symptom

```
spm-to-xcframework cafa869b74c8 (short-sha pinned in build/Build.Xcframework.cs)

Stripe.xcframework: plan expected Mixed framework but no public .h files were produced
  under Headers/ (ObjC header injection likely failed); plan expected Mixed framework but
  no module.modulemap was produced

(same message for all 14 Stripe xcframeworks — StripeCore, StripePayments, StripeUICore,
 StripePaymentSheet, StripePaymentsUI, StripeApplePay, StripeIdentity, StripeIssuing,
 StripeCardScan, StripeFinancialConnections, StripeConnect, Stripe3DS2, StripeCameraCore,
 and the umbrella Stripe)

spm-to-xcframework exit 8
```

### Scope

Only Stripe (mixed ObjC + Swift frameworks). Single-Swift-target libraries (Nuke, Lottie, etc.) are unaffected. All 12 Apple framework rebuilds succeed via the normal SDK path (no spm-to-xcframework needed).

### Workaround used in Round 3

Pre-existing Stripe xcframeworks (built Feb 13, 2026 under the prior `spm-to-xcframework` pin) were kept in place. The manual `dotnet build` per product then produced clean output. This is not a repeatable workflow for a clean checkout.

### Fix location

`spm-to-xcframework` tool script, post-archive validation phase. The Round 3 regression is specifically in the Mixed-framework plan — expecting public `.h` headers and a `module.modulemap` that the archive step no longer emits for recent Stripe SDK layouts. Likely triggered by a Stripe 25.6.2 Package.swift change that moved public ObjC headers.

### Release blocker?

**No** — xcframeworks can be re-cut manually or from the cached copies. Flag it for the tool owner; don't hold the SDK on it.

---

## 6. Delta vs Round 2

### Build regressions fixed

| Round 2 regression | Subsystem | Round 3 state |
|---|---|---|
| A — Flattened `Locale.Language`/`Locale.Region` | C# emitter / type DB | **Fixed** — Translation, ProximityReader, LiveCommunicationKit all build clean on every TFM |
| B — Missing `import ManagedSettings`; `_LocationEssentials` leakage | Wrapper emitter | **Fixed** — FamilyControls clean; WeatherKit's `_LocationEssentials` gone (build fails on a different type now) |
| C — `Self.AssocType` inside `@_cdecl` | Wrapper emitter | **Fixed** — TipKit builds |
| D — Missing `@available` guards | Wrapper emitter | **Fixed** — CryptoKit, TipKit build |
| E — Unbound `TT1..TT6` generics | C# emitter | **Partially fixed** — `TT1..TT6` gone, but same class regression on `MusicRelationshipProperty<,>` / `Forecast<>` (see §3) |
| F — `.SwiftHandle` on nested structs | C# emitter | **Fixed** — CryptoKit builds on macos/maccatalyst |

### SB0001 wins

| Library | Round 2 | Round 3 | Δ |
|---|---|---|---|
| Kingfisher | 102 | 39 | −63 |
| Lottie | 31 | 8 | −23 |
| Mappedin | 21 | 10 | −11 |
| BlinkIDUX | 11 | 0 | −11 (now SHIP) |
| StripePaymentSheet | 10 | 0 | −10 (now SHIP) |
| StoreKit2 | 10 | 0 | −10 (now SHIP) |
| BlinkID | 10 | 1 | −9 |
| StripePayments | 6 | 0 | −6 (now SHIP) |
| Nuke | 10 | 5 | −5 |
| StripeIssuing | 2 | 0 | −2 (now SHIP) |
| **Stripe (all 12)** | **28 total** | **0** | **−28** |

### New packages at SHIP status that were build-failing in Round 2

Translation, ProximityReader, LiveCommunicationKit, FamilyControls — all four moved from BUILD FAIL → SHIP (0 SB0001) thanks to Regressions A/B/C fixes plus SwiftBindings.Apple 26.0.0.

TipKit and CryptoKit moved from BUILD FAIL → NEAR-SHIP (48 and 152 SB0001 respectively); builds compile clean, surface is honest.

---

## 7. Shipping recommendation

**Hold 0.8.0.** Build quality is excellent; runtime validation is not. The release bar is "libraries work when users consume them," and we cannot confirm that for any library outside BlinkIDUX / LiveCommunicationKit / ProximityReader.

Unblocks required before cutting 0.8.0:

1. **Diagnose and fix the sim dylib load regression (§4)** — same SDK drop. Re-run sim validation for all 17 libraries; at minimum the 20 SHIP candidates must pass.
2. **Fix the MusicKit / WeatherKit generic-skip regression (§3)** — same SDK drop. Both libraries currently fail to build; shipping 0.8.0 with them broken would strand two target frameworks.

Unblocks that can slip past 0.8.0:

- NEAR-SHIP reductions for Kingfisher, TipKit, CryptoKit (architectural work; 0.8.x patch or 0.9.0).
- `spm-to-xcframework` Stripe header-injection regression (tool-level; doesn't block the .nupkg drop).

Once §3 and §4 are resolved and sim tests re-run clean, the 20-library SHIP set is ready to publish alongside SwiftBindings.Apple 26.0.0. The NEAR-SHIP libraries can ship in parallel with SB0001 documented in their READMEs — Round 3 confirms their builds are clean and their SB0001 counts accurately reflect honest diagnostics, not wrapper failures.

---

## 8. Artifacts

- **Ship-readiness doc:** `/Users/wojo/Dev/swift-dotnet-packages/SHIP-READINESS.md` (Round 3 section at top; per-library SB0001 table)
- **Build logs:** `/tmp/ship-val/{apple-*,stripe-build,stripe-p1/*,thirdparty-*}.log`
- **Sim-test logs:** `/tmp/ship-val/sim-tests/*.log` (17 libraries)
- **Sim-test summary:** `/tmp/ship-val/sim-progress.log`
- **Stripe xcframeworks used:** `libraries/Stripe/<Product>/<Framework>.xcframework` (from Feb 13, 2026 build pre-dating the `spm-to-xcframework` regression)
