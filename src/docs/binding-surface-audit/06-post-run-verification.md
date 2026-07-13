# Post-Run Verification — 2026-07-binding-quality vs. the Original Audit

**Date:** 2026-07-12
**Question answered:** Did the 12-session `sessions/2026-07-binding-quality` run actually land, and did it introduce any regressions in the candidate libraries?
**Verdict:** **Everything landed. No new regressions.** The only two build failures are the two pre-existing, already-documented 0.18 release blockers, reproducing with byte-identical diagnostic signatures. One original-audit claim was found to be **false** (CryptoKit ECDSA delta), and one audit FAIL was a **stale artifact** (SnapKit naming). A short residuals list follows at the end.

---

## 1. Method

- Packed SDK **0.18.0** + Apple supplement **26.2.9** from current `main` (`a07b8805`), deployed to `swift-dotnet-packages` local feed + `internal-binding-testing`, purged NuGet caches, stamped all project references (regression-validation skill steps 1–2 only).
- Regenerated and built **16 candidate bindings** and **12 test apps** against the fresh pack.
- Verified generated C# with a parallel fan-out (8 grunt-work inspection agents over the fresh `obj/Debug/net10.0-ios*/swift-binding/` trees), plus two adversarial deep-verification passes (CryptoKit ECDSA, RealityFoundation/RoomPlan SB0006) and a fresh SnapKit regeneration in `internal-binding-testing`.
- Generator-side: every session's claimed code, tests, and commits verified in-source; `nuke test` = **14434 passed / 0 failed** (floor 14434, run-claimed baseline ≥ 14295 holds).

## 2. Build matrix (fresh 0.18.0 pack)

| Result | Count | Detail |
|---|---|---|
| Candidate bindings PASS | **14 / 16** | ActivityKit, CryptoKit, FamilyControls, Kingfisher, LiveCommunicationKit, Lottie, MusicKit, Nuke, RealityFoundation *(C# gen OK — see FAIL)*, RoomPlan, StoreKit2, TipKit, WeatherKit, Stripe umbrella lanes |
| Candidate bindings FAIL (both **pre-existing, expected**) | 2 | **StripeCore** — SWIFTBIND108, 3 dangling `SBW_CreateEveryProtocol`/`GetMetadata`/`SetDeinitCallback` for `UnknownFieldsDecodable/Encodable` (known `ProtocolProxyEmissionPolicy` empty-`suitableProtocols` hole). **RealityFoundation** — SWIFTBIND051, `Optional<Never>` → `ActionEventDefinition<T>` across the EntityAction family. Signatures match the recorded blockers exactly; no new failure modes. |
| Test apps PASS | **12 / 12** | CryptoKit, StoreKit2, MusicKit, RoomPlan, FamilyControls, ActivityKit, TipKit, LiveCommunicationKit, WeatherKit, Lottie, Nuke, Kingfisher — all compile against 0.18.0 **unchanged**; no API-rename test updates were needed. |

Both gates the run was supposed to hold (SWIFTBIND108 integrity gate fail-closing StripeCore; SWIFTBIND051 fail-closing RealityFoundation) are doing their job — these are the two known 0.18 release-readiness work items, not verification findings.

## 3. Original audit P0s — where they stand

| Audit finding | Verdict in fresh 0.18.0 output |
|---|---|
| **P0-1 RealityFoundation `ModelComponent.Materials` dead getter** | **RESOLVED.** Real proxy-backed getter *and* setter: `Materials_Get()` P/Invoke projected through `MaterialProxy` (`ModelComponent.cs:173–177`); ctor round-trips too. All four session-02 carriers (`MaterialProxy`, `MaterialFunctionProxy`, `SynchronizationServiceProxy`, `RealityCoordinateSpaceProxy`) exist as full forward proxies. |
| **P0-2 RoomPlan `RoomCaptureViewDelegate` proxy missing / `Delegate` dead** | **RESOLVED.** `RoomCaptureViewDelegateProxy` emitted + vtable-installed; `RoomCaptureView.Delegate` has a real P/Invoke getter and a `GetOrCreate`-backed setter. |
| **P0-3 Label-collapsed protocol methods (LCK, RoomPlan)** | **RESOLVED for LCK** — `ConversationManagerDidActivate` / `DidDeactivate` are distinct members (including the AVAudioSession pair the audit flagged). **PARTIAL for RoomPlan** — 4/7 `IRoomCaptureSessionDelegate` members carry label suffixes; 3 remain type-only `CaptureSession` overloads (`didProvide`/`didStartWith`/`didEndWith` labels not folded). Distinct-by-type, so callable — residual polish, not a collapse. |
| **P1 MusicKit `Items` → `MusicItemCollection<AnyType>`** | **RESOLVED.** **87** closed typed `Items()` CSM getters across the response/section/chart family, **zero** `AnyType`. (Report hygiene residual: `binding-report.json` still lists the 5 open-generic base `items` rows as `AnyTypeFallback` skips even though the surface is recovered.) |
| **P1 CryptoKit ECDSA runtime proof** | **Audit's delta claim was FALSE — see §5.** Gap remains open (P1 coverage), correctly encoded as the Test 35 Skip. Not a regression: the closed overloads never existed in any artifact or git history. |
| **P1 TFM-vs-min-OS doc confusion** | **RESOLVED.** ActivityKit/StoreKit2/CryptoKit/WeatherKit guides + READMEs all separate the compile-SDK pin (TFM ≥ 26.2) from `SupportedOSPlatformVersion`. |
| **P1 Stripe wrapper/version policy** | **PASS on versions** (all 26.0.0, zero stray 25.15.0; transitive-only READMEs in place). **PARTIAL on `<SwiftWrapperRequired>`**: the three required `true` modules are correct, but `false` is set on 8 additional public modules and the property is absent (not explicit `false`) on StripeCameraCore/Stripe3DS2. |
| **Owner-deferred items (TipKit query members, Stripe confirmHandler, remaining AnyType, Kingfisher setImage GTC)** | **All verified still-deferred as intended** — no accidental scope creep, exit-ramps intact (Kingfisher: 22 GenericTypeCallback rows; Nuke `UserInfo` existential fallback; ActivityKit `Activity<T>` lifecycle comment-skipped with the supplement facade covering the product path). |

## 4. Session-by-session — did the work land?

Generator source, tests, and commits verified for all sessions; fresh output confirms each behavior end-to-end.

| Session | Landed? | Fresh-output evidence |
|---|---|---|
| 01 throwing-getter poison (SB0006) | **Yes** | Emitter + 5 unit tests + 2 CS0542 twins (`f1037d74`). Corpus-wide: **0 unpoisoned always-throw public getters on concrete types**. The 3 SB0006 `error:true` sites (Kingfisher TryGet\*) are the only remaining suppressed-proxy reads — RealityFoundation's 2 and RoomPlan's 1 poison sites from phase 1 are **gone because session 02 rescued them into real getters** (verified supersession, not a policy hole). |
| 02 EveryProtocol proxy rescue | **Yes** | Degraded carriers 13 → 1; Materials/RoomPlan proxies above; NSCoding fixtures in BindingTests. |
| 03 ProtocolMethodDisambiguator | **Yes** | LCK fully split; RoomPlan 4/7 (residual above). |
| 04 WrapperSymbolIntegrityGate | **Yes** | SWIFTBIND108 + non-zero exit wired; correctly fail-closes StripeCore; Kingfisher's old `MissingWrapperSymbol` dangling `Delegate.call` story is gone — real `SBW_Kingfisher_Delegate_call*` wrappers emit. |
| 05 existential-ctor NativeAOT | **Yes** | Fixture-only commit verified; CryptoSwift AES existential-ctor test un-skipped and asserting. |
| 06 typed collection projection | **Yes** | MusicKit 87/87 typed getters, zero AnyType. |
| 07 ClosedConstrainedClosureEmitter (v1 subset) | **Yes** | 5 distinct `SBW_CCC_` symbols in BindingTests output; Kingfisher setImage family correctly outside the v1 subset (documented exit-ramp). |
| 08 naming rules | **Yes** | Corpus-wide zeros: `TypeType` 0, `Sha3256` 0, `FrombyteArr` 0, hash-suffixed Create/From factories 0, fluent `Get*` stutter 0 (incl. fresh SnapKit — see §5). StoreKit collision names use `*Info` (`OfferTypeInfo` etc.) — collision-safe by design. |
| 09 IAsyncEnumerable | **Yes** | StoreKit2: 5 `IAsyncEnumerable<T>` sequences, typed `Task<T?> NextAsync`, all 9 corpus-wide `MakeAsyncIterator()` demoted `[EditorBrowsable(Never)]`, 0 undemoted. |
| 10 ModuleFileSplitter | **Yes** | 14/14 modules split into `*.Types.*.cs` (e.g. MusicKit 107, RealityFoundation 266); root `{Module}.cs` is scaffolding/proxy sidecar, not a type mega-file. *(The splitter landed in `acf12288`; phase-10 summary's `3bf250d7` correctly refers to the separate indent-collapse fix under Deliverable 3 — no misattribution, see §8.)* |
| 11 packaging & docs | **Yes** | TFM wording corrected everywhere checked; Stripe versions clean (residuals in §3). |
| 12 headline-flow tests | **Yes** | All 12 test apps compile on 0.18.0; the phase-12 pass counts' skip inventory matches observed generator surface (notably Test 35, below). |

## 5. Corrections to the original audit (not regressions)

1. **CryptoKit NIST ECDSA (audit A4 / delta finding 4) — the delta claim was false.** `01-delta-revalidation.md`'s claim that closed `Signature(byte[])` / `IsValidSignature(ECDSASignature, byte[])` CSM overloads emit was a stale scratch-tree misread — the overloads exist in **no artifact on disk and no commit in history** (`git log -S` empty across `--all`). Root cause: `ECDSASignature` is a **non-frozen (resilient), supplement-owned struct**, categorically outside the `af4f8aef` frozen-trivial CSM path the audit cited. The audit likely over-generalized from ML-DSA, whose signature return *is* frozen-trivial bytes and *does* get closed sign/verify overloads (Tests 32/33 exercise them). Current truth is encoded in `CryptoKit/tests/Tests.cs` Test 35 (explicit Skip with the correct mechanism in its reason). **To close for real:** extend CSM to concretize DataProtocol-parameterized methods whose return is a non-frozen supplement-owned struct (indirect-result `@_cdecl` wrapper), then un-skip Test 35.
2. **SnapKit fluent-naming FAIL was a stale artifact.** The `GetequalToSuperview`/`GetPriorityRequired` hits were in a 2026-07-08 pre-session `internal-binding-testing/SnapKit/SnapKit.cs`. Regenerated today under the current generator: **zero** Rule-2 stutter names. (Consistent with the standing note that several delta findings were stale scratch-tree reads.)

## 6. Residuals (documented, not blocking; none are new breakage)

1. **RoomPlan** — fold labels into the 3 remaining `CaptureSession(didProvide/didStartWith/didEndWith)` overload names (session-03 residual).
2. **Interface-default throwers (27 corpus-wide, incl. 4 on TipKit `ITip`)** — protocol-extension defaults throw bare `NotSupportedException` with no Obsolete poison. Outside session-01's suppressed-proxy scope (these are DIM defaults, a different shape); decide whether the poison policy should extend to them. Same bucket: `ICapturedRoomAttribute.ParentCategory` static-protocol getter.
3. **MusicKit report hygiene** — `binding-report.json` still lists the 5 open-generic `items` rows as `AnyTypeFallback` skips although closed CSM projections fully recover the surface; consider a "recovered" disposition.
4. **Stripe `<SwiftWrapperRequired>` matrix** — make CameraCore/3DS2 explicit and decide whether `false` on the 8 other public modules is intentional.
5. **AnyTip.GetShouldDisplay()** — fresh output has a live `SBW_AnyTip_shouldDisplay` P/Invoke where the audit said the concrete surface lacked it. A positive lift, but confirm it's intentional given the owner deferred the ITip query surface. `Invalidate` remains absent as expected.
6. **Doc nit** — the §4 nit itself was a misread: phase-10 summary's `3bf250d7` correctly cites the indent-collapse fix (Deliverable 3), separate from the ModuleFileSplitter's `acf12288`. Resolved in §8; no summary change needed.
7. **P/Invoke style note** — 703 `public static partial` LibraryImport methods sit inside `private NativeMethods` containers (not publicly reachable; keyword-level style question only).

## 7. Release blockers unchanged (pre-existing, tracked)

- **StripeCore SWIFTBIND108** — dangling EveryProtocol symbols; fix is in `ProtocolProxyEmissionPolicy.Decide` (full-proxy-in-empty-`suitableProtocols` hole).
- **RealityFoundation SWIFTBIND051** — `Optional<Never>` / `ActionEventDefinition<T>` associated-type=Never emission defect (EntityAction family). Generated C# for the rest of the module is healthy (267 files inspected).

Both must be fixed (or explicitly waived) before `release/sdk-0.18.0` is cut.

## 8. §E closing addendum — 2026-07-13 (clear-to-ship session 05)

Both §7 blockers are **cleared**, and the 16/16 regen + 12/12 sim gate is met on a fresh 0.18.0 pack (Apple supplement 26.2.9).

**Blockers cleared (with the fix SHAs from earlier clear-to-ship sessions):**

- **StripeCore SWIFTBIND108 — cleared** (session 01, `4a759cb3` "Suppress full EveryProtocol proxies when no carrier was emitted"). A clean standalone build of `SwiftBindings.Stripe.Core.csproj` (`net10.0-ios`) now exits 0 with only benign SWIFTBIND060/061 skip-count warnings; the binding-report carries no dangling `SBW_CreateEveryProtocol` / WrapperSymbolIntegrity entry. The shared Stripe test app (12 managed `ProjectReference` bindings plus Stripe3DS2/StripeCameraCore as native-only `.xcframework`s, spanning all 14 modules) builds exit 0, and the `Stripe` regression cell **PASSes** on the iOS simulator (build + run).
- **RealityFoundation SWIFTBIND051 — cleared** (session 02, `418153fb` "Project uninhabited Never associated-type members honestly in CSM wrappers"). The `RealityFoundation` regression cell **PASSes** on the iOS simulator.

**Regen gate (fresh 0.18.0 pack):** all 16 candidate bindings build (the 12 test-app libraries + RealityFoundation + the 3 Stripe lanes StripeCore/StripePayments/StripePaymentSheet), and all 12 candidate test apps + RealityFoundation + the Stripe app pass on `ios-sim` at their recorded baselines. The 4 sim-matrix failures (Facebook, ProximityReader, RealityKit, WorkoutKit) are **outside the 16-candidate scope** — pre-existing and not part of this gate; note `RealityKit` ≠ `RealityFoundation`.

**One transient diagnostic, not a blocker — SWIFTBIND064.** The first (parallel, `--regression-jobs 4`) matrix pass reported the Stripe cell as FAIL on a single SWIFTBIND064 in StripeCore (`_ImportSwiftBindingMetadata` hook did not run even though metadata was generated). It **does not reproduce**: a clean standalone StripeCore build, the full shared Stripe test app, and a serial (`--regression-serial`) Stripe cell all build/run clean with zero SWIFTBIND064. It appeared once, only in the parallel (`--regression-jobs 4`) matrix; the underlying cause is unconfirmed (a hook-ordering effect under concurrent multi-project build is the leading hypothesis, not established). Recorded here as an observed-once SDK diagnostic for sessions 01–04's awareness, **not** a §E gate failure and **not** hotfixed in this session (generator/SDK scope belongs to 01–04).

**Residuals ledger disposition (this session, §A–§D):**

- §6.1 RoomPlan folds — landed via session 03 (`651f0da7`); the shipped RoomPlan GUIDE was also corrected to reality (7 delegate members with label suffixes; `CategoryKind`/`CurveInfo`).
- §6.2 Interface-default throwers — policy recorded via session 04 (`896dd280`); no generator behavior change, so no new SB diagnostic to table.
- §6.3 MusicKit "recovered" disposition — session 03 Part B; MusicKit cell passes.
- §6.4 Stripe `<SwiftWrapperRequired>` matrix — made explicit on all 14 module csprojs (10 `true`, 4 `false`: the Stripe umbrella, StripeUICore, StripeCameraCore, Stripe3DS2). Every `false` module still builds and the Stripe cell passes, so no `false` module needs its wrapper — the matrix is honest, not a masked bug.
- §6.5 AnyTip.GetShouldDisplay() — pinned by TipKit Tests 21/22 (compile-time bound-accessor guard + runtime metadata assert; a documented Skip for the round-trip, since no C#-constructible seed `AnyTip` exists off-device — the only producer is circular and the generic `init<T: Tip>` is unsupported). The `SBW_AnyTip_shouldDisplay` P/Invoke is correctly emitted; not a defect.
- §6.6 Doc nit — investigated and dismissed: `git show` confirms `3bf250d7` is the indent-collapse fix (`Indent -= 2` → `Indent--`) and `acf12288` is the separate ModuleFileSplitter. Phase-10 summary's `3bf250d7` for Deliverable 3 (indent-collapse) was already correct; the §4 "nit" had conflated it with the splitter's SHA. No summary change — phase-10-summary.md left at `3bf250d7`.
- §6.7 P/Invoke style — no change (style-only, non-blocking).

**Conclusion:** with both §7 blockers cleared and the regen/sim gate met, **contract-and-ship 06 is unblocked** for the 0.18 maintenance cut. The only open item is the observed-once, non-reproducing SWIFTBIND064 diagnostic, tracked as a latent SDK note rather than a release blocker.
