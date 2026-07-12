# Phase 12 — Headline-flow tests: prove one real workflow per shipped package

Executes `src/docs/sessions/2026-07-binding-quality/12-headline-flow-tests.md`. Audit finding:
~0 `await`s across Apple package test apps — "green" meant "constructs + reads metadata," not
"the feature works." This session regenerates every shipped package against the post-session-11
generator and adds a real headline-flow test (construct primary type → await the headline API →
assert a semantic value) per package. All test edits land in `swift-dotnet-packages` and stay
**uncommitted there** (that repo references an unpublished SDK). This repo's tracked artifact is
this summary.

**9 packages regenerate clean against the fresh generator and pass real headline-flow tests (sim):**
- CryptoKit **45/0/1** — P256/P384/P521 key-gen + public-key serialization with curve-length
  asserts (Test 34); ECDSA sign→verify KAT documented Skip (audit A4, Test 35).
- StoreKit2 **37/0/0** — `await Product.ProductsAsync(unknown IDs)` asserts `Count == 0`
  (marshaller must not materialize entries StoreKit never returned), not merely non-null.
- WeatherKit **27/0/1** — GetAttributionAsync awaited; location fetch Skip (unentitled headless sim).
- MusicKit **40/0/1** — MusicAuthorization CurrentStatus status-query path; `RequestAsync` Skip
  (request() hard-aborts via TCC in headless CLI sim even with `NSAppleMusicUsageDescription`).
- Nuke **74/0/3**, Lottie **89/0/0** (green post-renames), RoomPlan **29/0/0**,
  ActivityKit **24/0/0**, FamilyControls **16/0/0**.

**2 shipped packages are BLOCKED regenerating against the post-run generator — root-caused,
documented, NOT masked** (no `<SwiftWrapperRequired>false>`, no `--permissive`, no stale-generator
pin). Together they are a **0.18 release-readiness gate**: the post-run generator cannot rebuild
every currently-shipped package.
- **StripeCore — SWIFTBIND108** (`WrapperSymbolIntegrityGate`, added today in `3c3de627`).
  Three dangling EveryProtocol wrappers (`SBW_CreateEveryProtocol` + metadata/deinit) for
  `UnknownFieldsDecodable`/`Encodable`. Pre-existing latent `EntryPointNotFoundException` the new
  fail-closed gate correctly surfaces. Root cause: `ProtocolProxyEmissionPolicy.Decide` only
  returns `SuppressedByConformance` when `ConformanceDecisions.Count > 0`; an empty-`suitableProtocols`
  module falls through to a FULL proxy calling a never-emitted base factory. Fix needs BindingTests
  repro + full validate. See memory `project_stripecore_everyprotocol_dangling_blocker`.
- **RealityFoundation — SWIFTBIND050→051.** Generated wrapper doesn't compile:
  `cannot convert 'EmphasizeAction.EventParameterType?' (aka 'Optional<Never>') to
  'ActionEventDefinition<EmphasizeAction>'`, repeated across the whole EntityAction family.
  Generic emission defect for the associated-type-bound-to-`Never` shape (generator has zero
  refs to `ActionEventDefinition`/`EventParameterType`/`EntityAction`). See memory
  `project_realityfoundation_actionevent_wrapper_blocker`.

**Review:** Codex CLI absent on host → Grok review + inline self-synthesis. Two actionable
findings, both fixed and re-verified green on sim: (High) StoreKit2 Test 31 asserted only non-null
→ strengthened to `Count == 0`; (Med) MusicKit `Info.plist` comment falsely claimed the usage key
makes RequestAsync resolve to Denied → corrected to state it still TCC-aborts, Skip stays.

**Gates:** the 11 package sim runs ARE the gate for this session. No swift-bindings SDK source
changed (only this gitignored summary + two blocker memories under `~/.claude`), so `nuke test`/
`nuke binding-tests` exercise unchanged generator source and are not warranted (CLAUDE.md gate
table: external-repo/docs → No).
