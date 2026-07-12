# Phase 11 — Packaging & docs (TFM/min-OS truth, wrapper & SPI policy)

All deliverables land in `swift-dotnet-packages` and stay **uncommitted there** (that repo
references an unpublished SDK; per policy it's committed only once the SDK is on NuGet). This
repo's tracked artifact is this summary; detailed record + evidence scripts are local under
`src/docs/sessions/2026-07-binding-quality/` (gitignored) — `11-results-restore-matrix-and-decisions.md`.

**Restore matrix (gating, measured 2026-07-12 vs local `Apple.Matter 26.2.8` nupkg):** app TFM
platform version must be **≥ 26.2** to restore (`net10.0-ios26.2`✅; `net10.0-ios18.0`❌NU1202;
bare `net10.0-ios`→26.0 here❌NU1202). Deploy floor independent (`26.2` TFM + `SupportedOSPlatformVersion=15.0`
builds clean). ⇒ **docs fix, not packaging fix.**

**Done (packages repo, uncommitted):** (2) two-layer Requirements rewrite across 13 Apple GUIDEs
+READMEs, ActivityKit + RoomPlan corrected; (3) min-OS = existing plist auto-fill path (option b),
Kingfisher README 13.0→**15.0** clamp truth; (4) Stripe `SwiftWrapperRequired` false→true on
Payments/PaymentSheet, explicit true on Core; all 14 csprojs `25.15.0`→`26.0.0`; SWIFTBIND051
gate demoed (`true`→exit1/error, `false`→exit0/warning); (5) SPI shells (UICore/ThreeDS2/CameraCore)
moved out of root table, `[Transitive]` descriptions, UICore CTA softened.

**Gates:** restore matrix + SWIFTBIND051 demo pass (numbers above); markdown/XML well-formed;
no leftover conflation bullets; no stray 25.15.0. No swift-bindings SDK change needed → no `nuke test`.

**Review:** Codex CLI absent on host (`command not found`) → 1 Grok round + inline self-synthesis.
Grok: no High. Fixed — ActivityKit wording tightened (Low), root README consumer line given the
two-layer+NU1202 split (Low). Accepted/documented — Stripe flip unproven under a real pack (needs
binaries-fetch; punted to release lane); wiki `NU1201` vs measured `NU1202` is reference-path-dependent
(ProjectReference vs PackageReference), flagged for session 06 not blind-flipped.

**Release-lane follow-ups (06/07):** commit packages tree once SDK is on NuGet + note the 26.0.0
Stripe version jump; run a full Stripe pack, then extend `SwiftWrapperRequired=true` to every product
whose wrapper generates clean; resolve the wiki NU1201/NU1202 by splitting reference paths.
