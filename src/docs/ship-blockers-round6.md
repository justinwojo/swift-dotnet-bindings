# Ship blockers — Round 6 (consumer-side closing revalidation)

**Date:** 2026-04-25
**SDK tested:** `SwiftBindings.Sdk 0.8.0` + `SwiftBindings.Runtime 0.8.0` + `SwiftBindings.Templates 0.8.0` + `SwiftBindings.Apple 26.2.0` (`local-packages/` drop 2026-04-25 00:30, post Sessions 5–7).
**Consumer repo state:** `/Users/wojo/Dev/swift-dotnet-packages` at `main` (757051b).
**Outcome:** Three of four Round 5 HOLD gates (StoreKit2 / WeatherKit / MusicKit) flipped from RED → GREEN cleanly. The CryptoKit gate is GREEN at the *symbol* level (12 non-`[Obsolete]` `Seal(` overloads emit) but the *runtime* AEAD round-trip is blocked by F3 below — CryptoKit primary AEAD is not yet reachable from C#. Sim 280/0/4 + device 280/0/4 (4 CryptoKit Skips trace to F3). Net-new findings F1, F2 are out-of-scope for ship; F3 is a ship blocker for "primary AEAD reachable" framing.

---

## Round 5 HOLD gate verification (all GREEN)

| Package | Gate | Result |
|---|---|---|
| StoreKit2 | `TryGetVerified` / `TryGetUnverified` emit | ✅ `StoreKit2.cs:3576` (`TryGetUnverified`), `:3656` (`TryGetVerified`) — both with `out TSignedType` payload extraction |
| WeatherKit | `Forecast<T>` collection projection | ✅ `WeatherKit.cs:14027` declares `Forecast<TElement> : ... IReadOnlyList<TElement>` |
| MusicKit | `MusicItemCollection<T>` ergonomics SB0001 cleared | ✅ `0` SB0001 (was 4) — Session 6 `DoesPairingSatisfyAssociatedTypeConstraints` relaxation landed |
| CryptoKit | AEAD reachable (symbol level) | ⚠️ 12 non-`[Obsolete]` `Seal(` overloads on AES.GCM + ChaChaPoly via Session 7 sync-throws CSM — but **runtime round-trip blocked by F3** |

CryptoKit residual: 38 SB0001 on the ancillary method-level-generics cluster (HMAC ctor, `Signature<D>`, `Unwrap`/`Decapsulate`/`ExportSecret`, `Open<TAuthenticatedData>` with the AD tuple, 3-arg `IsValidSignature<D>`). Even setting that aside, primary AEAD does **not** ship cleanly — see F3 below.

---

## Net-new findings (Round 6, out-of-scope for ship)

### F1. `InjectFrameworkDeps` regression strips ObjC-only deps + user PropertyGroup settings

**Where it surfaces:** Stripe two-pass build orchestration in `swift-dotnet-packages` (`build/Build.Dependencies.cs` → `InjectFrameworkDeps`).

**Symptom.** When `BuildLibrary --library Stripe --all-products` runs, the inject-framework-deps step rewrites each Stripe product csproj. Comparing `git HEAD` of e.g. `libraries/Stripe/StripePayments/SwiftBindings.Stripe.Payments.csproj` against the post-inject state shows two regressions:

1. **ObjC-only `SwiftFrameworkDependency` items are dropped.** HEAD includes `<SwiftFrameworkDependency Include="../Stripe3DS2/Stripe3DS2.xcframework" />` — necessary because the Swift wrapper for StripePayments has an `import Stripe3DS2` (ObjC module). After injection, the Stripe3DS2 entry is removed, so wrapper compile fails with `error: missing required module 'Stripe3DS2'`.
2. **User-managed `<SwiftWrapperRequired>false</SwiftWrapperRequired>` is dropped from the `<PropertyGroup>`.** This was set explicitly because Stripe products legitimately have wrapper-compile gaps and the user wants `SWIFTBIND051` suppressed. Inject removes it.

**Workaround used in Round 6.** Reverted all 11 Stripe csprojs to `git HEAD`, bypassed the Nuke `BuildLibrary` target, and ran plain `dotnet build` in dependency order (StripeCore → leaves → umbrella). All 12 products built rc=0 against the cached vendor xcframeworks. See `apple-frameworks/Stripe/library.json` § order vs. the dep-order list in §4 of `SHIP-READINESS.md`.

**Affected csprojs (all 11 in the consumer repo's working tree right now):**
```
libraries/Stripe/StripeApplePay/SwiftBindings.Stripe.ApplePay.csproj
libraries/Stripe/StripeCardScan/SwiftBindings.Stripe.CardScan.csproj
libraries/Stripe/StripeConnect/SwiftBindings.Stripe.Connect.csproj
libraries/Stripe/StripeCore/SwiftBindings.Stripe.Core.csproj
libraries/Stripe/StripeFinancialConnections/SwiftBindings.Stripe.FinancialConnections.csproj
libraries/Stripe/StripeIdentity/SwiftBindings.Stripe.Identity.csproj
libraries/Stripe/StripeIssuing/SwiftBindings.Stripe.Issuing.csproj
libraries/Stripe/StripePayments/SwiftBindings.Stripe.Payments.csproj
libraries/Stripe/StripePaymentSheet/SwiftBindings.Stripe.PaymentSheet.csproj
libraries/Stripe/StripePaymentsUI/SwiftBindings.Stripe.PaymentsUI.csproj
libraries/Stripe/StripeUICore/SwiftBindings.Stripe.UICore.csproj
```
Plus the umbrella `libraries/Stripe/Stripe/SwiftBindings.Stripe.csproj`. (Total 12 — 11 sub-products + umbrella.)

**Suggested fix.** `InjectFrameworkDeps` should:
- Treat `SwiftFrameworkDependency` items inside the auto-detected block (`<!-- BEGIN auto-detected … -->` / `<!-- END … -->`) as managed; treat items outside the block as user-authored and preserve them.
- Never touch `<PropertyGroup>` content — only edit `<ItemGroup>` blocks marked auto-detected.

**How to verify the fix.** From the consumer repo (`/Users/wojo/Dev/swift-dotnet-packages`):
```bash
git checkout -- libraries/Stripe/Stripe*/SwiftBindings.Stripe.*.csproj
dotnet nuke BuildLibrary --library Stripe --all-products
git diff libraries/Stripe/StripePayments/SwiftBindings.Stripe.Payments.csproj | grep -E "Stripe3DS2|SwiftWrapperRequired"
```
Expected after fix: the `git diff` should be empty for those two patterns (no removal of the user-authored Stripe3DS2 entry; no removal of the user-authored `<SwiftWrapperRequired>false</SwiftWrapperRequired>`). All 12 Stripe products should build rc=0 through the standard two-pass.

### F2. `spm-to-xcframework` (pinned at `cafa869b74c84e578eb7ed5710139b29fb3f611c`) Stripe build broken (mixed framework header injection)

**Where it surfaces:** `dotnet nuke BuildXcframework --library Stripe` (and the `BuildXcframework` step inside `BuildLibrary`).

**Symptom.** Fresh xcframework build for Stripe products fails because the produced xcframework directories are missing `Headers/` and `module.modulemap`. This is the regression mentioned in `ship-blockers-round5.md` §Out-of-scope, but Round 6 is the first round to confirm it actively blocks fresh builds (Round 5 used cached vendor xcframeworks at the library root, masking it).

**Workaround used in Round 6.** Bypassed `BuildXcframework` for Stripe and used cached xcframeworks at `libraries/Stripe/<Product>/<Product>.xcframework` from a prior known-good run. The `--skip BuildXcframework` flag does not help — `BuildXcframeworkForLibrary` is invoked as a method by `BuildLibraryEndToEnd`, not as a Nuke target dep.

**Suggested fix.** `spm-to-xcframework` mixed-framework header injection regression needs a fix in the tool. Once fixed, bump the pinned `Ref` / `Sha256` constants in `swift-dotnet-packages/build/Helpers/SpmToXcframeworkInstaller.cs` (currently `Ref = "cafa869b74c84e578eb7ed5710139b29fb3f611c"` at line 24).

**How to verify the fix.** After bumping the pin in `SpmToXcframeworkInstaller.cs`:
```bash
rm -rf libraries/Stripe/StripePayments/StripePayments.xcframework
dotnet nuke BuildXcframework --library Stripe --products StripePayments
ls libraries/Stripe/StripePayments/StripePayments.xcframework/ios-arm64/StripePayments.framework/{Headers,Modules}
```
Expected after fix: both `Headers/` and `Modules/module.modulemap` exist in the produced xcframework. Once verified for one product, re-run `BuildXcframework --library Stripe --all-products` to rebuild all 14 (12 public + 2 internal) and confirm none are missing those subdirectories.

### F3. CryptoKit primary AEAD round-trip blocked by SymmetricKey → AEAD-CSM marshalling defect

**Where it surfaces:** Any C# AEAD round-trip — `AES.GCM.Seal/Open` or `ChaChaPoly.Seal/Open` — using a `SymmetricKey` constructed via the standard public API. New tests in `apple-frameworks/CryptoKit/tests/Tests.cs` (Tests 25a–c diagnostic + 26–29 round-trip) demonstrate the failure.

**When this regressed in.** The AEAD `Seal` / `Open` overloads only emit at all because of the Session 7 sync-throws CSM landing — i.e., this defect is **specific to the Session 7 emission path** for AEAD CSMs, not a long-standing latent bug. Pre-Session-7 builds had `[Obsolete]` SB0001 stubs at every `Seal` / `Open` site, so no C# caller could reach the broken code path.

**Runtimes affected.** Reproduces identically on **both Mono JIT (simulator, `iossimulator-arm64`) and NativeAOT (physical iPhone 13, `ios-arm64`)**. Identical exception, identical message — which rules out runtime-specific marshalling and points to the Swift-side CSM wrapper itself.

**Symptom.** `SymmetricKey(SymmetricKeySize.Bits256)` and `SymmetricKey(new SymmetricKeySize((nint)256))` both construct successfully. `key.BitCount` round-trips correctly at 256 (proves the underlying handle points to a valid Swift `SymmetricKey` struct with the expected internal state). But when the same key handle is passed into `AES.GCM.Seal(byte[], SymmetricKey)` or `ChaChaPoly.Seal(byte[], SymmetricKey)`, Swift's CryptoKit throws `CryptoKitError.incorrectKeySize`.

**Minimal repro (paste into any iOS test app referencing `SwiftBindings.Apple` 26.2.0):**
```csharp
using Swift.CryptoKit;

var key = new SymmetricKey(SymmetricKeySize.Bits256);
Console.WriteLine($"BitCount={key.BitCount}");           // prints 256 ✓
try {
    var box = AES.GCM.Seal(new byte[] { 1, 2, 3 }, key); // throws CryptoKitError.incorrectKeySize ✗
    Console.WriteLine($"sealed {box.Ciphertext.Length} bytes");
} catch (Exception ex) {
    Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
}
```

**Diagnostic evidence (logged by the new tests):**
```
SymmetricKeySize.Bits256.BitCount = 256                    PASS
new SymmetricKeySize(256).BitCount = 256                   PASS
SymmetricKey(SymmetricKeySize(256)).BitCount = 256         PASS
AES.GCM round-trip                                          FAIL: incorrectKeySize
ChaChaPoly round-trip                                       FAIL: incorrectKeySize
AES.GCM tamper detection                                    FAIL: incorrectKeySize
AES.GCM.Seal with AD dispatch                              FAIL: incorrectKeySize
```

The C# `Seal` wrapper passes `((ISwiftObject)key).SwiftHandle` — the same `IntPtr` that `BitCount_Get` reads from successfully. Yet the AEAD primitive sees a mis-sized key. The defect is in the Swift-side CSM wrapper for `seal(_:using:)` / `open(_:using:)` — the way it reconstructs `SymmetricKey` from the IntPtr does not preserve the byte buffer that `SymmetricKey.bitCount` reports against.

**Generated C# evidence:**
- `apple-frameworks/CryptoKit/obj/Debug/net10.0-ios26.2/swift-binding/CryptoKit.cs:16505` — `Seal(byte[], SymmetricKey)` calls CSM `SBW_CSM_CryptoKit_GCM_Swift_Array_Swift_UInt8_seal_3E6CC09E` with `((ISwiftObject)key).SwiftHandle`.
- `apple-frameworks/CryptoKit/obj/Debug/net10.0-ios26.2/swift-binding/CryptoKit.cs:16967–16976` — `SymmetricKey(SymmetricKeySize size)` constructor delegates to `PInvoke_init_F96E31ED(resultPtr, size.Payload)`.

**Consumer impact.** The Round 5 §8 grep gate ("CryptoKit AEAD reachable") flips RED → GREEN at the *symbol* level — 12 non-`[Obsolete]` `Seal(` overloads + 2 `Open(` overloads emit. But the *runtime* round-trip is unreachable from any standard C# call path. The "primary AEAD ships" framing of the Round 6 SHIP-READINESS doc is inaccurate; CryptoKit must remain in a stronger HOLD state until F3 is resolved.

**Suggested fix area (for SDK team):** investigate the Swift-side CSM wrapper for `AES.GCM.seal/open` and `ChaChaPoly.seal/open`. The wrapper takes `key` as an `UnsafeMutableRawPointer` (or similar) and must reconstruct `SymmetricKey` such that its `.bitCount` and underlying byte buffer remain accessible. The fact that `BitCount_Get` succeeds but AEAD fails suggests the issue is specific to how AEAD's internal `key.withUnsafeBytes { ... }` interacts with the C#-allocated payload memory.

**How to verify the fix.** From the consumer repo, after the SDK drop with the F3 fix lands in `local-packages/`:
```bash
# 1. Un-Skip Tests 26-29 in apple-frameworks/CryptoKit/tests/Tests.cs (replace each Skip(...) with the original try/catch round-trip body).
# 2. Wipe binding output and rebuild:
rm -rf apple-frameworks/CryptoKit/obj apple-frameworks/CryptoKit/bin
dotnet build apple-frameworks/CryptoKit/SwiftBindings.CryptoKit.csproj -v q

# 3. Sim run:
dotnet nuke BuildTestApp --library CryptoKit && dotnet nuke ValidateSim --library CryptoKit --timeout 60

# 4. Device run (NativeAOT):
dotnet nuke BuildTestApp --library CryptoKit --device --aot && dotnet nuke ValidateDevice --library CryptoKit --aot --timeout 120
```
Expected after fix: Tests 26 (AES.GCM round-trip), 27 (ChaChaPoly round-trip), 28 (AES.GCM tamper-detection negative test — must throw `authenticationFailure` after byte-flipping the ciphertext), and 29 (AES.GCM `Seal` with authenticated-data parameter) all PASS on both runtimes. If any still throws `incorrectKeySize`, F3 is not actually fixed — keep CryptoKit on HOLD.

---

## Validation totals (Round 6 with new tests)

- **Apple frameworks:** 11 packages × 2 runtimes (ActivityKit shelved).
  - CryptoKit: 36 pass / 0 fail / **4 skip** (4 skips trace to F3 — AEAD round-trip blocked).
  - StoreKit2: 36 pass / 0 fail / 0 skip (was 35; +1 reflection check on `TryGetVerified` / `TryGetUnverified` shape).
  - WeatherKit: 27 pass / 0 fail / 0 skip (was 25; +2 reflection checks on `Forecast<HourWeather>` / `Forecast<DayWeather>` IReadOnlyList projection).
  - MusicKit: 37 pass / 0 fail / 0 skip (was 36; +1 reflection check on `MusicItemCollection<Song>` ergonomics shape).
  - Other 7 frameworks unchanged from Round 5.
- **Stripe:** 12 products × 2 runtimes. 300 pass / 0 fail.
- **Third-party:** 6 libraries × 2 runtimes. Totals match Round 5.

Total Apple-framework assertions: **283 sim + 283 device**, 0 failures, 4 skips on CryptoKit AEAD round-trip pending F3 resolution.

---

## Session plan (for `session-orchestrator-prompt.md`)

Four baseline sessions. The orchestrator may spawn 1–2 dynamic follow-up sessions if F2 or F3 turns out bigger than estimated; treat that as expected, not a failure mode. Sessions are sequential — F3 fix must land before final revalidation, and F1 + F2 must land before the final revalidation reruns the consumer build pipeline.

Multi-repo layout — workers must `cd` to the right repo for each session:
- **swift-bindings** — `/Users/wojo/Dev/swift-bindings` (this repo, generator + runtime)
- **swift-dotnet-packages** — `/Users/wojo/Dev/swift-dotnet-packages` (consumer repo, library packaging)
- **spm-to-xcframework** — `/Users/wojo/Dev/spm-to-xcframework/spm-to-xcframework` (external xcframework build tool)

### Session 1 — F1: `InjectFrameworkDeps` preserves user-authored content (✅ commit `c73f7d3` in swift-dotnet-packages)

**Repo:** `swift-dotnet-packages`
**Scope:** Fix the build-orchestration regression described in F1 above.

**Deliverables:**
1. Modify `InjectFrameworkDeps` in `swift-dotnet-packages/build/Build.Dependencies.cs` so that:
   - `<SwiftFrameworkDependency>` items inside the `<!-- BEGIN auto-detected … -->` / `<!-- END … -->` sentinel block are managed (overwrite freely).
   - `<SwiftFrameworkDependency>` items outside that block are user-authored and preserved verbatim.
   - `<PropertyGroup>` content is never edited. Only `<ItemGroup>` blocks marked auto-detected are touched.
2. Restore the 11 Stripe sub-product csprojs + the umbrella csproj (listed in F1) from `git HEAD` before exercising the new code path.

**Validation:**
- Run the verification recipe from F1 verbatim:
  ```bash
  git checkout -- libraries/Stripe/Stripe*/SwiftBindings.Stripe.*.csproj
  dotnet nuke BuildLibrary --library Stripe --all-products
  git diff libraries/Stripe/StripePayments/SwiftBindings.Stripe.Payments.csproj | grep -E "Stripe3DS2|SwiftWrapperRequired"
  ```
  Expected: empty grep output, all 12 Stripe products build rc=0.
- Confirm at least one non-Stripe library still builds via `BuildLibrary` (regression check that the sentinel-aware rewrite didn't break the common case).

**Out-of-scope:** Do not touch the spm-to-xcframework pin (that's Session 2). Do not modify CryptoKit (that's Session 3).

### Session 2 — F2: `spm-to-xcframework` mixed-framework header injection (✅ spm-to-xcframework `5909bd5`+`e9e46f2`; swift-dotnet-packages pin bump `5523e23`)

**Repos:** `spm-to-xcframework` (primary), `swift-dotnet-packages` (pin bump only).
**Scope:** Fix the regression that drops `Headers/` and `Modules/module.modulemap` from xcframeworks built for mixed-framework Stripe products, then bump the pin.

**Deliverables:**
1. In `spm-to-xcframework`, root-cause why mixed framework header injection no longer produces `Headers/` and `module.modulemap` in the output xcframework. Reference commit: pinned `Ref = "cafa869b74c84e578eb7ed5710139b29fb3f611c"` is the broken build; bisect against last known-good if needed.
2. Land the fix in `spm-to-xcframework`, push, capture new commit SHA + tarball SHA256.
3. Bump `Ref` and `Sha256` constants in `swift-dotnet-packages/build/Helpers/SpmToXcframeworkInstaller.cs` (currently line 24).

**Validation:** Run the F2 verification recipe verbatim against StripePayments first, then `BuildXcframework --library Stripe --all-products`. All 14 products (12 public + 2 internal) must produce xcframeworks with `Headers/` and `Modules/module.modulemap` present.

**Stuck criteria:** If the upstream regression's root cause requires a non-trivial rewrite of the mixed-framework injection logic (>1 day of work), message lead — orchestrator may spawn a 2nd session for the spm-to-xcframework side.

**Out-of-scope:** Do not modify CryptoKit. Do not touch `InjectFrameworkDeps` (Session 1).

### Session 3 — F3: CryptoKit AEAD `SymmetricKey` CSM marshalling (✅ swift-bindings `f936e30d`)

**Repo:** `swift-bindings` (primary), `swift-dotnet-packages` (verification).
**Scope:** Fix the Swift-side CSM wrapper regression that makes `AES.GCM.Seal/Open` and `ChaChaPoly.Seal/Open` throw `CryptoKitError.incorrectKeySize` even though `SymmetricKey.BitCount` round-trips at 256.

**Deliverables:**
1. Add a BindingTests reproduction of the F3 minimal repro before fixing, per CLAUDE.md ("when fixing a `nuke validate` bug, reproduce the Swift pattern in BindingTests so it's permanently covered"). Test must fail on the current SDK and pass after the fix. Both Mono JIT (sim) and NativeAOT (device) coverage required — F3 reproduces on both.
2. Investigate the Session 7 sync-throws CSM emission path for AEAD primitives. Per `feedback_verify_swift_abi_sil.md`, dump SIL + assembly to confirm how the CSM wrapper reconstructs `SymmetricKey` from the `IntPtr` parameter — do NOT guess from mangled names. Confirm whether the defect is:
   - (a) generator-side (wrong CSM wrapper emission for sync-throws CSMs that take ref-typed params), or
   - (b) runtime-side (`SymmetricKey` payload extraction in `Swift.Runtime`), or
   - (c) both.
3. Land the fix at the right layer. If the fix turns out to apply to all sync-throws CSMs that take ref-typed params (not just AEAD), grep the codebase for analogous call sites and fix them all in this session per CLAUDE.md ("when fixing a bug pattern, grep the whole codebase for ALL instances").
4. Update consumer-side `apple-frameworks/CryptoKit/tests/Tests.cs` Tests 26–29 — un-`Skip` them per the F3 verification recipe.

**Validation:**
- `nuke test` (unit tests) — must remain ≥ baseline.
- `nuke validate` (compile gate) — must remain ≥ baseline `.validation-baseline.json`.
- `nuke binding-tests --sim --device --strict` — both runtimes must pass the new BindingTests AEAD reproduction. Mono and NativeAOT have different bugs (per CLAUDE.md), so device pass is required, not optional.
- Consumer-side: rebuild + redeploy the SDK to `swift-dotnet-packages/local-packages/`, run the F3 verification recipe (build CryptoKit, then `nuke ValidateSim` + `nuke ValidateDevice` with `--aot`). All four un-skipped tests (26 round-trip AES.GCM, 27 round-trip ChaChaPoly, 28 tamper-detection, 29 AES.GCM with AD) must PASS on both runtimes.

**Stuck criteria:** If SIL inspection reveals the bug is in `Swift.Runtime` `SymmetricKey` marshalling specifically (not the broader sync-throws CSM emission path), the fix may be larger than estimated. Acceptable to land partial progress (BindingTests repro + investigation summary) and request a follow-up session for the implementation. Partial > rushed (per orchestrator constraints).

**Out-of-scope:** Do not address the residual 38 SB0001 on the ancillary method-level-generics cluster (HMAC ctor, `Signature<D>`, `Unwrap`/`Decapsulate`/`ExportSecret`, `Open<TAuthenticatedData>` with the AD tuple, 3-arg `IsValidSignature<D>`). Those are post-ship per `roadmap.md`.

### Session 4 — Round 7 closing revalidation

**Repos:** `swift-bindings` (SDK rebuild), `swift-dotnet-packages` (full consumer regression).
**Scope:** Rebuild SDK 0.8.0 in place (per `feedback_sdk_version_stable.md` — do NOT bump patch), redeploy, run the full regression-validation flow, document Round 7 outcomes.

**Deliverables:**
1. From `swift-bindings`: `nuke pack --version 0.8.0`. Wipe any prior 0.8.0 nupkgs in `local-packages/` per `feedback_sdk_version_stable.md`.
2. Run the `regression-validation` skill (Mono JIT sim + NativeAOT device on swift-dotnet-packages and sim-validation). Per `feedback_no_expected_failures.md`, any non-pass result outside source-coded skips is a real regression.
3. Author `src/docs/ship-blockers-round7.md` capturing:
   - Verification of F1, F2, F3 fixes (all three flipped GREEN at the gate level).
   - Validation totals — should be Apple frameworks 287 sim + 287 device (Round 6's 283 + 4 CryptoKit AEAD tests un-skipped), 0 failures, 0 skips on the AEAD path.
   - Any net-new findings (Round 7 may surface its own — do not paper over them; CLAUDE.md "no shortcuts").
4. If all gates green, mark CryptoKit's "primary AEAD ships" framing as accurate in the consumer-side `SHIP-READINESS.md`.

**Validation:** All `regression-validation` outputs green. Zero-regression policy (CLAUDE.md): `.validation-baseline.json` `cs_compile` + `swift_compile` ≥ baseline, BindingTests pass count ≥ baseline, unit-test pass count ≥ baseline.

**Out-of-scope:** Do not bump SDK to 0.9.0 or beyond. Do not edit `roadmap.md` post-ship items. If Round 7 finds a Round 8 blocker, document it in `ship-blockers-round7.md` and stop — do not attempt to fix in this session.

### Dynamic follow-up sessions (allowed)

The orchestrator may spawn 1–2 unplanned sessions if:
- Session 2 hits a non-trivial spm-to-xcframework rewrite — split the upstream fix from the pin bump.
- Session 3's investigation reveals the CSM regression is broader than AEAD — split the BindingTests repro/investigation from the implementation fix.
- Session 4 surfaces a Round 7 net-new blocker that has a small, contained fix — fix it in a 5th session and rerun validation, rather than punting to Round 8.

If a dynamic session is spawned, append it here as Session 5 / Session 6 with the same structure (scope, deliverables, validation, out-of-scope) before kicking off the worker.
