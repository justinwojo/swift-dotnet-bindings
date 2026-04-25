# Ship blockers — Round 7 (closing revalidation)

**Date:** 2026-04-25
**SDK tested:** `SwiftBindings.Sdk 0.8.0` + `SwiftBindings.Runtime 0.8.0` + `SwiftBindings.Templates 0.8.0` + `SwiftBindings.Apple 26.2.0` (rebuilt fresh against `swift-bindings@f936e30d`; `local-packages/` drop 2026-04-25 04:25).
**Consumer repo state:** `/Users/wojo/Dev/swift-dotnet-packages` at `main` = `5523e23` (2 commits ahead of `origin/main`, both unpushed per `feedback_no_commit_packages.md`).
**Outcome:** Round 6 blockers F1, F2, F3 all GREEN at the gate level. **One Round 7 net-new blocker (F4)** surfaces in the regenerated Stripe xcframeworks: the `YamlLikeTbdFormatParser` does not handle multi-line `objc-eh-types: [ ... ]` continuation lines, blocking 5 of 12 Stripe products on both runtimes. CryptoKit primary AEAD ships clean. Apple frameworks 11/11 pass on both runtimes (269 sim + 269 device, 0 fail, 0 skip). All 6 third-party libraries (BlinkID/BlinkIDUX/GRDB/Kingfisher/Lottie/Mappedin/Nuke) pass on both runtimes. All 15 sim-validation libraries pass on both runtimes.

---

## Round 6 blocker verification (all GREEN)

| ID | Round 6 status | Round 7 verification | Result |
|---|---|---|---|
| **F1** — `InjectFrameworkDeps` strips ObjC-only deps + user PropertyGroup | RED | `git diff libraries/Stripe/Stripe*/SwiftBindings.Stripe.*.csproj \| grep -E "Stripe3DS2\|SwiftWrapperRequired"` after `dotnet nuke RunCiSimTest --library Stripe` | ✅ **GREEN** — empty diff across all 12 Stripe csprojs; user-authored `<SwiftFrameworkDependency Include="../Stripe3DS2/Stripe3DS2.xcframework" />` and `<SwiftWrapperRequired>false</SwiftWrapperRequired>` preserved (Session 1 / `swift-dotnet-packages@c73f7d3`). |
| **F2** — `spm-to-xcframework` mixed-framework Headers/+Modules drop | RED | `ls .../<Product>.xcframework/<slice>/<Product>.framework/{Headers,Modules}` for all 14 Stripe products × 2 slices | ✅ **GREEN** — every framework slice has `Headers/` + `Modules/module.modulemap` (Session 2 / `spm-to-xcframework@5909bd5+e9e46f2`; pin bump in `swift-dotnet-packages@5523e23`). |
| **F3** — CryptoKit `SymmetricKey → AEAD-CSM` marshalling | RED | Consumer-side CryptoKit Tests 26–29 (un-`Skip`-ed) on both runtimes | ✅ **GREEN** — sim 40/0/0 + device 40/0/0 (was 36/0/4 in Round 6, +4 from un-skipping). Tests 26 (AES.GCM round-trip), 27 (ChaChaPoly round-trip), 28 (tamper detection), 29 (Seal-with-AD dispatch) all PASS on Mono JIT sim and NativeAOT device (Session 3 / `swift-bindings@f936e30d` — `unsafeBitCast` non-frozen struct vs class discrimination in CSM `@_cdecl` wrapper PayloadHandle case). |

### F3 deviation analysis

Session 3's worker implemented Tests 28 and 29 with two design-doc deviations. Both are justified, and both still satisfy the Round 7 ship-readiness criterion ("primary AEAD reachable from C#"):

- **Test 28** — Worker exercised tamper detection by attempting `Open(sealedBox, wrongKey)` instead of byte-flipping the ciphertext (design-doc recipe). Justification: `SealedBox(nonce:, ciphertext:, tag:)` is a Swift generic initializer that the binding generator does not currently emit, so byte-flipping requires reconstructing a `SealedBox` from modified parts — not reachable from C#. The wrong-key path exercises the same Swift-side `authenticationFailure` code: AES-GCM auth binds `(key, nonce, ciphertext, tag)` together; mutating any of those causes the same auth-tag verification failure. **Verdict: tamper detection is proven; deviation is justified.**
- **Test 29** — Worker stopped at `Seal(plaintext, key, aad)` verification (non-empty ciphertext + 16-byte tag) rather than the full `Seal → Open<TAD>` round-trip. Justification: the generic `Open<TAD>(SealedBox, SymmetricKey, AD)` overload uses `CallConvSwift` directly and hits **Issue 1** in `feedback_mono_jit_blame.md` (Mono JIT assertion `!ji->async` on synchronous `CallConvSwift` P/Invoke) — confirmed upstream and on the authoritative blame list. Adding the `Open<TAD>` call would either crash on Mono sim or require a runtime-conditional skip. The current test still proves Seal-with-AD CSM dispatch works on the multi-conformer CSM path (a different specialization than the 3-arg Tests 26/27 use). **Verdict: Seal-with-AD dispatch is proven; deviation is justified by the confirmed upstream-Mono bug list.**

Both deviations match the design-doc framing ("F3 fix lands; primary AEAD reachable") at the level Round 7 needs to ship CryptoKit.

---

## Net-new finding (Round 7, blocks fresh-build Stripe pipeline)

### F4. `YamlLikeTbdFormatParser` does not consume multi-line `objc-eh-types` continuation lines — RESOLVED (Session 5)

**Status:** ✅ RESOLVED in `swift-bindings` Session 5 — parser change is generalized (Option 1 below) and verified against all 12 Stripe products. Generator step now succeeds on the 5 previously-blocked products (StripePayments, StripePaymentsUI, StripePaymentSheet, StripeIssuing, Stripe umbrella). Stripe test app now builds and links cleanly. A new Stripe sim *runtime* crash surfaced on the first execution (post-F4 unblock) — see "Round 8 candidate" note at the end of this section; it is independent of F4 and out of scope for Session 5.

**Verification (Session 5, against rebuilt SDK 0.8.0 + Apple 26.2.0):**
- `nuke test` — Bindings 9994/0/1, Analyzers 20/0/0, Runtime 598/0/1 (≥ baseline; 2 new TBD-parser unit tests included).
- `nuke binding-tests --strict` (sim, full regen) — 1664 PASS / 0 FAIL / 53 SKIP — exact baseline match.
- All 12 Stripe products `dotnet build` — 0 errors each (5 previously-F4-blocked products now compile).
- Stripe sim test app `dotnet build` — 0 errors (links umbrella `SwiftBindings.Stripe` successfully; was the gate that was previously failing on F4).

**Where it surfaces:** Generator binding step on Stripe products that import or vend ObjC exception types (Stripe3DS2 transitive consumers). Reproduces on both `--platform-target simulator` (sim build) and `--platform-target device` (NativeAOT build) — same parser, same input.

**Symptom.** The generator's TBD parser bails on the second line of any multi-line `objc-eh-types: [ ... ]` array in the regenerated Stripe xcframeworks. Example (StripePayments.tbd line 7757–7758):

```yaml
    objc-eh-types:   [ STDSAlreadyInitializedException, STDSException, STDSInvalidInputException,
                       STDSNotInitializedException, STDSRuntimeException ]
```

The parser logs `Unknown export property at line 7757: objc-eh-types` (warning), continues to the next line, then throws because it tries to parse `STDSNotInitializedException, STDSRuntimeException ]` as a key-value pair (no colon).

```
fail: TbdParsing.TbdParser[0]
      Error parsing TBD file System.FormatException: Invalid key-value pair format: STDSNotInitializedException, STDSRuntimeException ]
fail: BindingsGeneration.BindingsGenerator[0]
      Binding generation failed: Error parsing TBD file: Invalid key-value pair format: STDSNotInitializedException, STDSRuntimeException ]
```

**Root cause.** `src/Swift.Bindings/src/Demangler/TbdParser/Parsing/YamlLikeTbdFormatParser.cs` `ParseExports()` (lines 242–336) recognizes a fixed switch of export properties (`- targets`, `symbols`, `objc-classes`, `objc-ivars`, `weak-symbols`). Each known list-valued property calls `ParseMultiLineArray()` to consume continuation lines. The `default` arm only emits a warning — it does NOT skip continuation lines. So when an unknown property has a multi-line value, the parser advances to the continuation line and `ParseKeyValuePair()` throws on the missing colon.

**Why it surfaces in Round 7 (not Round 6).** Round 6 used "cached vendor xcframeworks at `libraries/Stripe/<Product>/<Product>.xcframework` from a prior known-good run" (Round 6 §F2 workaround). Session 2's F2 fix bumped the `spm-to-xcframework` pin to `5909bd5`+`e9e46f2`, then Session 2 ran `BuildXcframework --library Stripe --all-products` to regenerate all 14 Stripe xcframeworks with proper Headers/+Modules. The newly regenerated xcframeworks' TBD files contain the `objc-eh-types` directive (which the cached pre-Session-2 xcframeworks may not have included, or which the parser may have happened not to reach). The TBD format itself is well-formed; the parser is missing coverage.

**Affected products (5 of 12; same on both sim and device):**
- `SwiftBindings.Stripe.Payments`
- `SwiftBindings.Stripe.PaymentsUI`
- `SwiftBindings.Stripe.PaymentSheet`
- `SwiftBindings.Stripe.Issuing`
- `SwiftBindings.Stripe` (umbrella)

**Unaffected Stripe products (7 of 12 — build clean on both runtimes):** StripeCore, StripeApplePay, StripeCardScan, StripeFinancialConnections, StripeIdentity, StripeConnect, StripeUICore.

**Suggested fix area (for SDK team).** Two options, in order of preference:

1. **Default arm in `ParseExports`'s switch:** when the value contains an unclosed `[` (multi-line array opener), call `ParseMultiLineArray()` to consume continuation lines (and discard the result). One-line change at `YamlLikeTbdFormatParser.cs:329-332`.
2. Add an explicit `case "objc-eh-types":` that calls `ParseMultiLineArray` and discards (matching `weak-symbols` shape). Less general but more discoverable.

Option 1 is the right long-term fix — any future TBD-format addition would otherwise re-trigger the same failure. Per CLAUDE.md "when fixing a bug pattern, grep the whole codebase for ALL instances": Session 5 grep confirmed two `default → bare warning` switch arms in this file — both in `YamlLikeTbdFormatParser` (`Parse` top-level and `ParseExports`). Session 5 applied the generalized Option 1 fix (extracted into a `ConsumeIfMultiLineArray(...)` helper) to **both** default arms, even though only `ParseExports` is the active failure path — `Parse`'s top-level loop is already wrapped in try/catch so it tolerates the malformed continuation, but the same pattern bug exists there and the helper makes parsing cleaner across the file.

**How to verify the fix.** From `swift-dotnet-packages`:
```bash
rm -rf libraries/Stripe/StripePayments/obj libraries/Stripe/StripePayments/bin
dotnet build libraries/Stripe/StripePayments/SwiftBindings.Stripe.Payments.csproj -v q
# Expected: rc=0, no "Invalid key-value pair format" error.
dotnet nuke RunCiSimTest --library Stripe --reuse-sim --timeout 180
# Expected: generator step + sim build succeed (umbrella links cleanly). Sim runtime now reaches
# the test runner — see Round 8 candidate note below for the post-F4 runtime finding.
```

**Round 8 candidate (NEW finding emerged from F4 resolution; not part of F4 itself).** With F4's generator-step blocker removed, the Stripe sim test app now reaches the runtime test runner and crashes on the first `STPPaymentHandler.SharedHandler` access:

```
* Assertion at /Users/runner/work/1/s/src/runtime/src/mono/mono/metadata/jit-info.c:918,
  condition `!ji->async' not met
Managed Stacktrace:
  at StripePayments.STPPaymentHandler:PInvoke_sharedHandler_Get_4C910585 <0x00007>
  at StripePayments.STPPaymentHandler:SharedHandler_Get
  at StripeSimTests.MainViewController:RunStripePaymentsTests
```

The generated P/Invoke is `[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]` against the `@_cdecl` thunk `thunk_StripePayments_6d7d9617`. Per `feedback_mono_jit_blame.md` rule 3 ("If CallConvCdecl — it's NEVER upstream"), this is OUR bug, not Mono Issue 1 (which is for synchronous `CallConvSwift` direct P/Invoke into Swift runtime functions). Most likely candidates: the `@_cdecl` thunk body raises a Swift error / accesses an ObjC class that Mono can't unwind, or there's a stack-frame ABI mismatch around the CSM @_cdecl wrapper for static class properties on ObjC-derived classes (`STPPaymentHandler` extends `NSObject`). Round 8 should:
1. Dump the Swift `@_cdecl` wrapper body for `thunk_StripePayments_6d7d9617` (search Session 2's regenerated `StripePayments.swift` wrapper sources).
2. Verify the wrapper handles `STPPaymentHandler.shared` ObjC singleton dispatch correctly (likely the wrapper just calls `STPPaymentHandler.shared` and casts to `IntPtr` via `Unmanaged.passRetained(...).toOpaque()` — confirm the retain).
3. If the SIL/ABI looks correct, this is the first non-CallConvSwift CSM @_cdecl crash in the program; investigate whether `MarshalFromSwift<NSObject-subclass>` correctly handles the returned pointer.

Round 7 reported Stripe 300/0 sim against pre-Session-2 cached xcframeworks (Round 6 §F2 workaround). Those older xcframeworks may have had different `@_cdecl` wrapper symbols (Session 2 regenerated all xcframeworks via `spm-to-xcframework`); the symbol/ABI changes may have introduced the runtime crash. Either way, the runtime crash is **distinct** from F4 (which was strictly a TBD parser gap on the generator side), and resolving it is its own session.

---

## Validation totals (Round 7)

### Apple frameworks (11 packages × 2 runtimes — ActivityKit shelved)

| Framework | Sim (Mono JIT) | Device (NativeAOT) | Δ vs Round 6 |
|---|---|---|---|
| **CryptoKit** | **PASS 40/0/0** | **PASS 40/0/0** | **+4 (F3 un-skip; primary AEAD reachable)** |
| FamilyControls | PASS 15/0/0 | PASS 15/0/0 | — |
| LiveCommunicationKit | PASS 18/0/0 | PASS 18/0/0 | — |
| MusicKit | PASS 37/0/0 | PASS 37/0/0 | — |
| ProximityReader | PASS 10/0/0 | PASS 10/0/0 | — |
| RoomPlan | PASS 29/0/0 | PASS 29/0/0 | — |
| StoreKit2 | PASS 36/0/0 | PASS 36/0/0 | — |
| TipKit | PASS 20/0/0 | PASS 20/0/0 | — |
| Translation | PASS 12/0/0 | PASS 12/0/0 | — |
| WeatherKit | PASS 27/0/0 | PASS 27/0/0 | — |
| WorkoutKit | PASS 25/0/0 | PASS 25/0/0 | — |

**Apple frameworks totals:** **269 sim + 269 device, 0 fail, 0 skip.** (Round 6 stated `283 + 4 skip = 287` totals; the 287/287 design-doc target was based on that. Recounting Round 6's per-row data also yields ~265 + 4 skip, so the Round 6 stated total appears to have been an arithmetic artefact, not a Round 7 regression. Per-row deltas vs Round 6 are zero across the board except CryptoKit `+4 PASS / -4 SKIP` from un-skipping.)

### Stripe (12 products via umbrella)

**Sim and device: BLOCKED at generator step on 5 of 12 products by F4.** The 7 unblocked products (StripeCore, StripeApplePay, StripeCardScan, StripeFinancialConnections, StripeIdentity, StripeConnect, StripeUICore) generate fine and would build, but the consumer test app links the umbrella `SwiftBindings.Stripe` which is in the blocked set, so end-to-end test app build fails:

```
RunCiSimTest Stripe (sim):    Pipeline failed: dotnet build (sim) failed with exit 1
ValidateDevice Stripe (AOT):  BuildTestApp failed; ValidateDevice not invoked
```

(Round 6 reported Stripe sim 300/0 + device 300/0 against cached pre-Session-2 xcframeworks. Round 7's regenerated xcframeworks are F2-correct but expose F4 in the parser.)

### Third-party + sim-validation

| Source | Sim | Device |
|---|---|---|
| **`swift-dotnet-packages` vendors** (BlinkID, BlinkIDUX, GRDB, Kingfisher, Lottie, Mappedin, Nuke) | 7/7 PASS — 1369 assertions / 0 fail / 2 skip | 7/7 PASS — same |
| **`sim-validation`** (15 libs: Alamofire, Kingfisher, RxSwift, SnapKit, CryptoSwift, KeychainAccess, Starscream, DeviceKit, PhoneNumberKit, Reachability, Swinject, ObjectMapper, SwiftyBeaver, XMLCoder, BonMot) | 15/15 PASS — 439 assertions / 0 fail | 15/15 PASS — 439 assertions / 0 fail |

Vendor per-test counts (identical sim and device):
- BlinkID 305/0/0, BlinkIDUX 146/0/1, GRDB 247/0/0, Kingfisher 248/0/1, Lottie 89/0/0, Mappedin 257/0/0, Nuke 77/0/0.

(The two skips on BlinkIDUX and Kingfisher are source-coded with documented reasons — pre-existing, not regressions.)

### Aggregate

| Bucket | Sim PASS | Device PASS | Sim FAIL | Device FAIL | Notes |
|---|---|---|---|---|---|
| Apple frameworks | 269 | 269 | 0 | 0 | CryptoKit AEAD primary flow now reachable |
| Vendor (incl. Stripe) | 1369 | 1369 | 5 of 12 Stripe products fail to build (F4) | same | Stripe non-Stripe-Payments products would pass; umbrella build aborts |
| sim-validation | 439 | 439 | 0 | 0 | — |
| **Total runnable assertions** | **2077** | **2077** | — | — | Stripe assertions not counted (build blocked by F4) |

---

## SHIP-readiness impact

| Package family | Round 6 classification | Round 7 classification | Driver |
|---|---|---|---|
| **CryptoKit** | HOLD (F3) | **SHIP** | F3 landed; AEAD primary flow reachable on both runtimes |
| Stripe (12 products) | SHIP (against cached xcframeworks via F1/F2 workarounds) | **HOLD pending F4** | Fresh-build pipeline aborts at generator step on 5 of 12 products. Cached pre-Session-2 xcframeworks would still build (F4 not exercised), but shipping demands a fresh-build path. |
| All other Apple + third-party + sim-validation | SHIP | SHIP | unchanged |

Per the Round 6 design doc: "If Round 7 finds a Round 8 blocker, document it in `ship-blockers-round7.md` and stop — do not attempt to fix in this session." F4 fits that profile and is documented above. Per the design doc footer, the orchestrator may choose to spawn a dynamic Session 5 for the contained parser fix; that is not this session's call.

`SHIP-READINESS.md` in `swift-dotnet-packages` is updated to reflect:
- CryptoKit promoted from HOLD → SHIP (F3 cleared).
- Stripe moved from SHIP → HOLD pending F4 (fresh-build pipeline blocked).
- F1/F2 marked RESOLVED (no longer "open generator-side blockers").
- F4 added as the new sole open generator-side blocker.

---

## Commit / push policy

Per `feedback_no_commit_packages.md`: do NOT push consumer-side commits until the SDK that fixes downstream-blocking bugs is published to NuGet. Round 7 does not publish — all 4 NuGets are local-packages only. Consequently:

- `swift-bindings` Round 7 commit: `src/docs/ship-blockers-round7.md` only. (Round 6 doc kept as historical record per design-doc convention; not deleted.)
- `swift-dotnet-packages` working-tree changes from Round 6/7 verification (CryptoKit Tests.cs un-Skip from Session 3, SHIP-READINESS.md update from Round 7) committed locally but **not pushed**. Two prior commits (`c73f7d3` F1 fix, `5523e23` F2 pin bump) also remain unpushed pending the next NuGet publish.
