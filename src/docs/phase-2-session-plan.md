# Phase 2 Session Plan — `SwiftBindings.Apple` Supplement

> **Status:** Locked 2026-04-17. This plan breaks the 11 milestones in
> [`apple-swift-types-architecture.md`](apple-swift-types-architecture.md) §
> "Decision summary" / `0.8.0-ship-plan.md` §6.1 into **7 sequential sessions**.
> Each worker reads this doc plus the architecture doc as anchor. Scope is
> **Phase 2 ONLY** — no Phase 1 follow-ups (§5 of ship plan), no Phase 3
> packaging/publishing, no swift-dotnet-packages repo commits.

## Anchor docs

Every session worker MUST read, in order, before any implementation:

1. `src/docs/apple-swift-types-architecture.md` — authoritative spec
2. `src/docs/0.8.0-ship-plan.md` §6 (lines 221–280) — milestone enumeration
3. This doc — for the specific session's scope/risks/exit gate
4. `CLAUDE.md` — validation gates, zero-regression policy, conventions

The architecture doc is authoritative wherever this plan or the ship plan
contradict it.

## Global invariants (all sessions)

- **Zero-regression policy.** Every commit must hold baseline
  `.validation-baseline.json` (`cs_compile` + `swift_compile`), BindingTests
  runtime pass count, and unit test pass count. No exceptions, no "will fix
  later." If a change regresses any of these, fix before committing.
- **SDK patch version stays at `0.8.0`.** Per `feedback_sdk_version_stable.md`
  — rebuild in place, never bump the SDK patch during iterative 0.8.x work.
- **Commit hygiene.** Subject + 1–3 sentences on the *why*. No "Session N
  handoff", no gate-pass footers, no numbered per-file breakdowns. Co-author
  footer per orchestrator prompt.
- **Out of scope.** Phase 1 §5 follow-ups (MusicKit concrete CSM, StoreKit2
  `onStorefrontChange`), Phase 3 packaging, nuke pipeline wiring for
  `apple-frameworks/`, Stripe hardening, publishing to nuget.org,
  committing swift-dotnet-packages repo, NativeAOT device validation
  (requires physical iPhone).
- **Partial completion is acceptable** if a session is larger than
  anticipated. Commit a self-consistent slice that passes gates, clearly
  flag deferred items in the completion report, and the lead will spawn a
  follow-up session.

## Session breakdown

### Session 1 — M1 + M3: manifest schema + `SwiftBindings.Apple` skeleton — ✅ COMPLETE (commit `41d00b1e`)

**Scope:**
- Design the Apple-types metadata manifest schema (Swift identity / managed
  projection / ABI carrier split). JSON on disk (binary may come later).
  Fields per architecture doc §"Resolved questions" Q7: accessor symbols,
  size/alignment, VWT pointer references, conformance descriptor names,
  availability/weak-linking guards.
- Embed it under `src/Swift.Bindings.Sdk/tools/apple-types-manifest/` so the
  0.8 SDK is the single coordination surface. Design the on-disk format so
  a later extraction into `SwiftBindings.Apple.Metadata` is a relocation,
  not a reformat.
- Scaffold the `SwiftBindings.Apple` package project under
  `src/Swift.Bindings.Apple/` — multi-TFM targeting all four Apple TFMs
  (`net10.0-ios`, `-maccatalyst`, `-tvos`, `-macos`). Monolithic single
  assembly (D, not D'). NuGet package id `SwiftBindings.Apple`. Package
  major = Apple SDK train major (18.x today).
- `nuke pack` plumbing so `SwiftBindings.Apple` builds alongside the other
  NuGet packages at version `0.8.0` (patch stays stable — see invariants).
- One or two seed sample manifest entries + minimal placeholder emitted
  types so the skeleton actually compiles end-to-end. Full generation
  lands in Sessions 2–4.

**Exit gate:** `nuke test`, `nuke validate`, `nuke binding-tests`,
`nuke pack --version 0.8.0` all green (baseline held). New
`SwiftBindings.Apple` nupkg produced at `/tmp/swift-nuget/`.

---

### Session 2 — M2: ABI JSON → manifest pipeline — ✅ COMPLETE (commit `9eb42bdc`)

**Scope:**
- Build the generator-side pipeline that consumes Apple Xcode SDK ABI JSON
  and emits the manifest designed in Session 1. Drive it from the same ABI
  JSON the framework-package generator already consumes.
- Handle **typealias-as-projection** correctly (e.g.
  `ApplicationToken = Token<Application>`) — emit alias/projection metadata,
  NOT duplicate type identity. Per architecture doc §Q10 item 4.
- Availability / weak-linking guards per §Q10 item 1 — metadata accessors
  must carry the `@available` gate information so Session 4's emitter can
  platform-guard accessor calls.
- Run the pipeline once locally against the installed Apple SDK, check
  manifest plausibility (spot-check a few types: `Foundation.Locale.Language`,
  `ManagedSettings.Application`, `CryptoKit.P256.Signing.ECDSASignature`).
  Do not need to be exhaustive yet — bootstrap/full coverage is Session 7.

**Exit gate:** `nuke test`, `nuke validate`, `nuke binding-tests` green.
Manifest file(s) check into the repo under the path decided in Session 1.

---

### Session 3 — TypeRecord refactor + M5: `TypeOwnerRegistry` — ✅ COMPLETE (commit `7f18efe9`)

**Scope:**
- **Prerequisite refactor:** split `TypeRecord` in the generator into three
  concepts per architecture doc §"Implementation specifics" item 5:
  (1) **Swift identity** (e.g. `Foundation.Locale.Language`),
  (2) **managed projection** (the consumer-facing C# type),
  (3) **ABI carrier** (the C# type used to copy/destroy/pass across the
  Swift→C boundary). Keep existing type-DB behavior intact while adding
  the three-way split so later sessions can plug in supplement owners.
- **`TypeOwnerRegistry`** lives in `SwiftBindings.Runtime` with the
  6-level resolver order (architecture doc §"Resolved questions" Q5 /
  §Implementation specifics item 7):
  1. Per-type owner override (legacy canonicals pinned to
     `SwiftBindings.Runtime`: `Foundation.Date`, `.Data`, `.URL`,
     `.Decimal`, `.Measurement<T>`, `.AnyError`, `ManagedSettings.Token<T>`,
     `SwiftUI.Text`)
  2. Swift stdlib known type
  3. ObjC workload type / projection (e.g. NSDate for NSLocale)
  4. Module-default supplement lookup
  5. Same-module type being generated
  6. Unsupported (skip member)
- **Cross-module protocol conformance** handling per §Q10 item 3 — type
  ownership is module-local, conformance ownership may not be.

**Exit gate:** Full gates (`nuke test`, `nuke validate`, `nuke binding-tests`)
green. Legacy canonical types still resolve to `SwiftBindings.Runtime`.
Apple-module types not yet emitted resolve to `SwiftBindings.Apple` owner
record (even if the supplement is still empty).

---

### Session 4 — M4: VWT-backed opaque storage emission — ✅ COMPLETE (commit `05968dc1`)

**Scope:**
- Extend the supplement emitter so supplement-owned types default to
  **VWT-backed opaque storage** (per architecture doc §Decision summary
  item 3 / §Q8). No `[StructLayout(Sequential)]` by default.
- Per-type **sequential-layout whitelist** behind an explicit gate. Gate
  requires ALL of: `frozen=true` in ABI JSON, non-generic (or fully
  layout-known instantiation), all stored fields known and layout-known,
  ABI size/alignment validated by metadata accessor, copy/destroy trivial
  OR explicitly handled, runtime round-trip test passing.
- Wire up emission end-to-end: manifest entry → generator → VWT-backed
  C# struct in the `SwiftBindings.Apple` assembly with working
  copy/destroy/optional/container round-trip.

**Exit gate:** `nuke test`, `nuke binding-tests`, `nuke validate` green.
BindingTests cover at least one emitted supplement type round-trip
(simulator). Whitelist path verified on at least one type that qualifies.

---

### Session 5 — M6 + M7 + M9: generator integration + prototyping + blast-radius smoke — ✅ COMPLETE (commit `c88eb9ea`)

**Scope:**
- **M6 — Generator integration.** Type DB resolver consults
  `TypeOwnerRegistry`. Consumer framework csprojs emit
  `<PackageReference Include="SwiftBindings.Apple" ...>` **only when** the
  registry resolves a referenced type to the supplement. Non-Apple
  consumers must NOT pick up the Apple supplement.
- **M7 — Demand-driven prototyping mode.** SDK MSBuild targets emit a
  supplement project into `obj/` for prototyping; the consumer references
  it as a **project dependency**, NOT as duplicate sources compiled into
  each consumer assembly. Canonical identity must be preserved
  (architecture doc §Decision summary item 8 — Option B is a trap).
- **M9 — Framework-linkage blast-radius smoke test.** NativeAOT
  single-framework app: does referencing one framework package force-link
  other Apple frameworks it does not use? This gate may send work back
  into M4/M6 (e.g. lazy P/Invoke, per-module conditional symbols,
  runtime probing). **If blast-radius failure cannot be mitigated in-session,
  commit the partial + clearly flag rather than fake success.**

**Exit gate:** `nuke test`, `nuke validate`, `nuke binding-tests` green.
Blast-radius smoke result recorded in the completion report with numbers
(which frameworks appeared in the final NativeAOT binary, what their
byte footprint was).

---

### Session 6 — M8 + M10: cross-module identity test + live-SDK CI validation — ✅ COMPLETE

**Scope:**
- **M8 — Cross-module type identity test** (permanent regression
  guardrail). Two consumer assemblies reference `SwiftBindings.Apple`,
  instantiate a Swift-only type (e.g. `Foundation.Locale.Language`) in
  one, pass to the other, assert `typeof(T)` matches / reference equality
  on the CLR `Type`. Lives under `BindingTests/` so `nuke binding-tests`
  catches identity regressions.
- **M10 — CI validation against live Apple SDK.** Smoke test that for
  every manifest entry on the currently-installed Apple SDK: metadata
  accessor symbol exists, size/alignment match the manifest, VWT
  copy/destroy works, optional + container round-trip works. Catches
  manifest drift from a shipped Apple SDK.

**Exit gate:** `nuke test`, `nuke validate`, `nuke binding-tests` green.
M8 identity test + M10 live-SDK validation both executing in CI (or
locally via `nuke`) and passing.

---

### Session 7 — M11: bootstrap the 7 target frameworks

**Scope:**
- Discover every Swift-only type referenced by the 7 target frameworks
  (Translation, ProximityReader, LiveCommunicationKit, FamilyControls,
  WeatherKit, TipKit, CryptoKit). Populate the manifest exhaustively.
- Regenerate each of the 7 frameworks — members Phase 1 skipped due to
  Swift-only-type references should now bind. `nuke validate` SB0001
  counts for these frameworks should drop to permanent-limit floors only.
- **Risk flagged in architecture doc Appendix A:** generic-param emitter
  bug (`T`, `TT1`, `TT2`, `TT3` leaking in LiveCommunicationKit +
  WeatherKit) is **orthogonal** to Swift-only types. If it blocks, split
  into **M11a** (bootstrap for frameworks that compile with current
  emitter) and **M11b** (generic-param emitter fix + remaining frameworks).
  Do NOT conflate into Phase 2 scope-creep.

**Exit gate:** `nuke test`, `nuke validate`, `nuke binding-tests` green.
Previously-skipped Swift-only-type members in the 7 frameworks now bind.
If split into M11a/M11b, M11b lands as a second commit / follow-up
session with the same gate.

## Orchestration notes

- **Orchestrator does not write code.** Lead directs workers, verifies
  commits via `git log --stat`, and delegates deeper verification to
  Sonnet Explore subagents.
- **One worker at a time.** Sessions are sequential — later sessions
  depend on earlier ones.
- **Workers aggressively delegate to subagents** (Sonnet Explore for
  mapping source, Opus Plan for approach questions, parallel
  general-purpose Agents for multi-file investigations) to protect their
  own context.
- **Workers run `/ai-pair-programming`** (default OpenAI) for code review
  before committing, when available. Fall back to careful self-review
  only if the skill is unavailable on this machine.
- **Completion reporting:** workers MUST SendMessage the lead with the
  per-deliverable summary + gate results + commit SHA before going idle.
  Idle ≠ done; only the explicit completion message counts.
- **Team name:** `apple-types-supplement`.
