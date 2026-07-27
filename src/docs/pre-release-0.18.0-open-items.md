# 0.18.0 pre-release open items — every known defect, all repos

Date opened: 2026-07-26. **Status: LIVE — being filled in by a full green sweep in progress.**

Purpose: the owner asked for one durable register of every open defect and unfinished
verification across **all three repos**, so nothing depends on a chat session surviving. Scope
is deliberately wider than `roadmap.md` (intent + policy) and `not-planned.md` (trigger-gated
latents): this file is the pre-release punch list. Items here are expected to be **fixed or
consciously accepted before `release/sdk-0.18.0+apple-26.2.8` is cut**.

Repos:
- **SB** = `/Users/wojo/Dev/swift-bindings` (this repo)
- **PKG** = `/Users/wojo/Dev/swift-dotnet-packages`
- **IBT** = `/Users/wojo/Dev/internal-binding-testing`

Status legend: `OPEN` needs a fix · `VERIFY` needs a run to confirm · `ACCEPT?` owner call ·
`DONE` closed this cycle.

---

## A. Verification gaps — things not yet proven green

| ID | Repo | Item | Status |
|---|---|---|---|
| A1 | SB | `nuke test` — unit/runtime/analyzer | **DONE** 2026-07-26: 15989/0 unit, 730/731 runtime, 35/35 analyzer. Floor raised 15868 → 15989. |
| A2 | SB | `nuke binding-tests --compile-only` | **DONE** 2026-07-26: exit 0. Parity 0 new; API-manifest 2842/2842, 0 added 0 removed; resilience-kitchen + ingestion-kitchen PASSED. |
| A3 | SB | `nuke validate` — full real-world compile sweep | **VERIFY** — fresh run in progress. Baseline is frozen at `git_sha 52ac336a` (~2026-06-28). Prior counts reported against it differ by run (`not-planned.md` records 12 failed / ~15–16 regressions; a later note says 26). Both predate the 0.18.0 regression fix. Real current number pending. |
| A4 | SB | `nuke binding-tests` (iOS Simulator, Mono JIT) | **VERIFY** — not run since the fix landed. |
| A5 | SB | `nuke binding-tests --device` (NativeAOT) | **VERIFY** — 9 cells were environmental on 2026-07-26 (devicectl 10002/EINVAL, `LaunchActionDeclaration` never invoked → app image never entered, zero product signal). Needs re-run after the phone reboot. |
| A6 | SB | `nuke binding-tests --macos` / `--catalyst` / `--tvos` | **VERIFY** — not run this cycle. |
| A7 | SB | `nuke binding-tests --partial-success-kitchen` | **VERIFY** — not run since the fix; its baseline compare is *exact*, so it is the sharpest skip-surface regression detector we have. |
| A8 | SB | `nuke binding-tests --mixed-pack` (sim + device) | **VERIFY** — pre-release trigger; packaging/marshalling changed this cycle. |
| A9 | SB | `nuke binding-tests --mixed-direct` | **VERIFY** — pre-release trigger. |
| A10 | SB | `nuke binding-tests --appstore-hygiene` | **VERIFY** — pre-release trigger (TN2435 IPA compliance). |
| A11 | SB | `nuke binding-tests --skip-surface` | **VERIFY** — CI release-gate step. |
| A12 | PKG | Downstream regression across all library + Apple-framework cells | **VERIFY** — last run `20260727-000956.281Z` was 13/13 non-device green, 9 device cells environmental. Needs a clean re-run against the committed generator. |
| A13 | IBT | `run-all-sim.sh` / `run-all-device.sh` over the 15 library cells | **VERIFY** — not run since the fix. |
| A14 | IBT | `corpus-sweep` (120 libraries) | **VERIFY** — last known 58/120 green; 6 compile-reds root-caused (see D1). |

---

## B. Confirmed defects — OPEN, need a fix

| ID | Repo | Defect | Evidence / site |
|---|---|---|---|
| B1 | PKG | **Auto-detected `ProjectReference` injector deletes correct references.** `ApplyProjectRefs` strips the existing block — including hand-authored sibling refs — *before* deciding whether to re-emit, and `BuildPrBlock` returns `""` on a zero-hit scan. The scan is a literal `\bModule\.` token grep over generated wrapper C#, so any reference not surfacing as that exact text reads as zero hits and the ref is dropped, never replaced. **This is the root cause of the 25 missing managed references SWIFTBIND081 caught during the 0.18.0 run** — the generator was right, the fixtures were mis-wired. | `build/Helpers/CsprojRewriter.cs` (`ApplyProjectRefs`, `BuildPrBlock`, `MigrateSiblingProjectRefs`, `StripPrAutoBlock`); `GeneratedCsScanner.ContainsModuleReference`. Proven by commit `eb1b24cd`, which deleted StripeConnect's and StripeIdentity's `../StripeCore/…` refs with no replacement. **Still live**: untracked `libraries/Facebook/FBSDKCoreKit` and `FBAEMKit` already show SFD-without-PR. |
| B2 | PKG | **PR injection is all-or-nothing on freshness; SFD injection is not.** `InjectProjectRefsForLibrary` freshness-checks *every* product before any write, so one stale product silently aborts `ProjectReference` injection library-wide, while `InjectFrameworkDepsForLibrary` writes per-product regardless. That asymmetry is how the two marker blocks drift apart unnoticed. | `build/Build.Dependencies.cs` (freshness pass ~L194–203); contrast `InjectFrameworkDepsForLibrary` ~L72–153. |
| B3 | SB | **Wrapper emitter over-escapes Swift keywords in argument labels.** Emits `` `operator`: `` / `` `class`: `` in call argument position, where escaping is unnecessary. Swift warns (`keyword 'operator' does not need to be escaped in argument list`). Cosmetic — compiles and runs — but it pollutes every wrapper build log with `[ERR]`-tagged noise that makes real errors harder to spot. | `BindingTests/output/.wrapper-build/SwiftBindingsTestLib.Wrapper.swift:60684`. Observed in the 2026-07-26 `--compile-only` log. |

---

## C. Product-surface problems — real, pre-existing, need an owner call

| ID | Repo | Item |
|---|---|---|
| C1 | PKG | **`SwiftBindings.Stripe.UICore` ships with zero API.** StripeUICore emits **0 of 96 types / 0 of 538 members**; only 27 of those are quarantine withdrawals, and 0.16.0 emitted 1/96 from the same input — so the near-emptiness predates the 0.18.0 window and is *not* a regression. Publishing a package with no public surface is still a product problem. **Decide before release: fix, or don't ship the package.** |
| C2 | PKG | **`SwiftBindings.Stripe.Core` emits 19 of 111 types.** Skip volume is `ModuleInternal` + `@_spi`, pre-existing and understood, but it is the dependency root of the whole Stripe web. |
| C3 | SB | **Facebook/FBSDKCoreKit shipped dead API.** Defect C (RawRepresentable planner ignoring the discard writer) meant every shipped FB binding since ~0.14 carries 40 dangling P/Invokes — `EntryPointNotFoundException` if a consumer touches them. The generator now correctly refuses to ship it (SWIFTBIND108). Already-published packages are still affected; decide whether that warrants an advisory. |

---

## D. Known-red corpora — scoped, not yet funded

| ID | Repo | Item |
|---|---|---|
| D1 | IBT | **6 compile-red corpus packages**, all root-caused with fix sites and sizes in `src/docs/corpus-compile-red-root-causes-2026-07.md`: malformed marshalling-body emission (undefined emitter locals `_handle`/`resultPtr`/`riveBox`/`s_init_*_Callback`; `.Payload` access on Apple-projected non-payload types) in DGCharts/rive-ios; CombineCocoa cross-module `Runtime` ref + incomplete `DelegateProxy` (missing `IDisposable.Dispose`); Moya blocked on sibling Alamofire missing `IParameterEncoding`; Macaw/SwiftDate operator-return CS0029. Six of seven underlying bugs are localized; fixing all is worth +7 greens (58 → 65/120). **Not funded.** |
| D2 | SB | **Validation baseline is 134+ commits stale** (`52ac336a`, 2026-06-28). A re-baseline is owed, but only *after* the reds it currently reports are triaged — see A3. Detailed disposition in `not-planned.md`. |

---

## E. Accepted / environmental — documented so they are not re-hunted

| ID | Item |
|---|---|
| E1 | **devicectl launch abort** (`CoreDeviceError 10002` + `NSPOSIXErrorDomain 22`) after a clean install is the *launcher* aborting before the app starts — no `LaunchActionDeclaration` in the unified log, app image never entered, zero product signal. Not a binding defect. `build/Models/LaunchDiagnostics.cs` encodes the discriminator; the mixed legs retry 3×. |
| E2 | **MT7155/MT7156 `BundleResource` dedup warnings** on macOS (`Swift/*Database.xml` LogicalName collision) — benign, pre-existing. |
| E3 | **LiveActivity `request()`-path tests** fail on a foreground-active precondition under the CLI simulator — environmental. |
| E4 | **StoreKit `products(for:)` returning 0** and `.storekit` config-file crashes are ASC/sandbox and Apple-side respectively; marshalling proven correct on sim + device. |

---

## Changelog

- **2026-07-26** — File created during the full green sweep. Sections A–E seeded from the
  0.18.0 regression program, the packages-repo injector investigation, and the standing
  registers. A3/A4–A14 pending; B1–B3 open; C1–C3 await owner calls.
