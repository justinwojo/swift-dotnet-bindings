# Architecture Gameplan v2 — Post-1.0 Round 1

**Status**: Plan of record for the first post-1.0 architecture track.
**Companion docs**:
- `architecture-gameplan.md` — the 1.0 plan (DONE).
- `Future/post-1.0-architecture-roadmap.md` — the remaining post-1.0 inventory.

**Decision**: The next two architecture items are the highest-ROI candidates pulled forward from the post-1.0 roadmap: a native `libswiftDemangle` swap, then a SwiftSyntax producer behind the `SwiftInterfaceFacts` aggregator that M4 landed.

This track is independent of `roadmap.md` (coverage / skip / library themes); both progress in parallel.

---

## The Litmus Test

Same bar as v1:

> Will this either expose a real binding failure earlier, prevent a known class of bad generated binding, or increase valid emitted API surface?

Both items below pass:

- **`libswiftDemangle`** eliminates ~5,800 LOC of hand-ported demangler — a known drift surface against the Swift compiler's mangling. Today it works; the moment Swift adds a new mangling node we silently produce wrong names.
- **SwiftSyntax producer** retires 4,066 LOC of regex parsing (`SwiftInterfaceAccessParser.cs`) plus 23 nullable side-channel maps' worth of string heuristics. This is the single largest "silent wrong binding" risk surface in the codebase. M4 deliberately shaped `SwiftInterfaceFacts` so this swap can land incrementally.

Items that don't pass the test stay in `Future/post-1.0-architecture-roadmap.md` until something pulls them forward.

---

## Why this order

`libswiftDemangle` first, SwiftSyntax second. Two reasons:

1. **Reversibility**. Apple's dylib is already on disk; we just don't link it. Hide the swap behind `IDemangler`, keep the managed port as a fallback strategy, and the entire migration is a feature flag away from rollback. SwiftSyntax adds a Swift host program as a new build artifact and a new toolchain dep — that's a much bigger commitment.
2. **ROI density per session**. The demangler swap is concentrated work — one interface, one P/Invoke surface, one parity test. SwiftSyntax migrates 23 fact dictionaries one at a time and gates each on parity against the regex parser. Front-loading the smaller win keeps the v2 track shipping value early even if SwiftSyntax stretches.

---

## v2 Plan: Two Milestones

### Milestone 1 — `libswiftDemangle` swap *(1–2 sessions)*

**Goal**: ~5,800 LOC of hand-ported demangler replaced by a P/Invoke into Apple's `libswiftDemangle.dylib`, behind a single `IDemangler` seam.

**Scope**:
- Define `IDemangler` interface covering every entry point the codebase currently calls into the managed demangler with.
- Native strategy: P/Invoke into `libswiftDemangle.dylib` (publicly exposes `swift_demangle` returning a malloc'd C string the caller frees).
- Managed strategy: keep the existing port behind the same interface, selectable via runtime flag.
- Default to native; fall back to managed if the dylib is unavailable at startup (e.g., non-Apple toolchain edge cases).
- Parity tests: drive a representative corpus of mangled symbols (Foundation TBDs, validation libs, BindingTests output) through both strategies and assert byte-equal output.
- Once parity holds and runtime gates are clean, retire the managed port from the default build (keep behind a flag for one release cycle, then delete).

**Why (litmus)**: prevents a known class of bad generated binding (demangling drift against Swift compiler changes). Eliminates ~5,800 LOC of hand-port we don't control.

**Sessions** (2):

1. **Session 1 — `IDemangler` seam + native strategy + parity**
   - Introduce `IDemangler` interface, refactor existing call sites to depend on it.
   - `LibSwiftDemangleStrategy` P/Invoke implementation.
   - `ManagedDemangleStrategy` wraps the existing port unchanged.
   - Parity test fixture covering: (a) every BindingTests symbol, (b) sampled symbols from Foundation / Alamofire / SwiftUI TBDs, (c) every mangling node the managed port handles explicitly.
   - **Gate**: parity test green; `nuke test` + `nuke validate` + `nuke binding-tests --sim --device` at-or-above baseline.

2. **Session 2 — Default switch + retire managed port**
   - Flip default strategy to native.
   - Add startup probe + diagnostic when the dylib is unavailable; fall through to managed.
   - Mark the managed port as deprecated in code; gate behind `--demangler=managed` for one release cycle.
   - **Gate**: full sweep at-or-above baseline with native default. After the next NuGet release ships clean, delete the managed port in a follow-up commit.

### Milestone 2 — SwiftSyntax producer behind `SwiftInterfaceFacts` *(3–5 sessions)*

**Goal**: 4,066 LOC of regex parsing in `SwiftInterfaceAccessParser.cs` replaced by a Swift host program that uses SwiftSyntax to populate the same `SwiftInterfaceFacts` aggregator M4 introduced.

**Scope**:
- New Swift host program ("`SwiftInterfaceParser`" or similar) built via SPM, distributed alongside the generator.
- Output format: a single JSON document matching `SwiftInterfaceFacts`'s shape (one fact dict per top-level field). Keeps the boundary trivial: producer writes JSON, .NET side deserializes into the existing immutable record.
- Producer strategy on the .NET side: `IInterfaceFactsProducer` with `RegexProducer` (current) and `SwiftSyntaxProducer` (new). `SwiftInterfaceFacts` cares only about the aggregated record — both producers must yield byte-equal output for the same input.
- Migrate fact-by-fact: each session picks N facts, lights them up in the SwiftSyntax producer, and asserts parity against the regex producer across the validation corpus before flipping the default.
- Source-position fidelity improves automatically — SwiftSyntax gives precise spans where the regex parser had to estimate offsets. M4's `SourcePosition` plumbing already handles non-null positions; the upgrade is just better data.
- Once every fact is migrated and parity holds, retire `SwiftInterfaceAccessParser.cs` and the regex code path.

**Why (litmus)**: prevents a known class of bad generated binding. Regex-driven inference of internal members, actor isolation, typed throws, availability, default args, subscript labels, etc. is the single largest "silent wrong binding" surface in the codebase. Every regex pattern we eliminate is a class of drift that goes away forever.

**Sessions** (4):

1. **Session 1 — Swift host program scaffold + first fact**
   - SPM package for the host program; build integration into `nuke compile`.
   - Distribution decision: vendor the built binary in the NuGet package (one binary per Apple host platform) versus build-on-first-use. Recommendation: vendor, mirroring how we ship xcframework slices.
   - JSON contract matching `SwiftInterfaceFacts`.
   - Migrate one fact end-to-end as a proof-of-shape — recommend `MainActorTypePositions` since M4 already exercises both the fact path and the position path.
   - `IInterfaceFactsProducer` seam on the .NET side; CLI flag picks producer.
   - Parity test: regex vs SwiftSyntax for the migrated fact across the validation corpus.
   - **Gate**: parity green for the migrated fact; `nuke test` + `nuke validate` at-or-above baseline; the regex producer remains the default.

2. **Session 2 — Migrate fact batch (round 1)**
   - Migrate the next ~7–8 facts. Pick by drift risk: `AvailabilityAnnotations`, `TypedThrowsAnnotations`, `ActorIsolationAnnotations`, `DefaultArgValues`, `SubscriptParameterLabels`, etc. — the ones whose regex patterns have the most known edge cases.
   - Maintain parity tests for everything migrated so far.
   - **Gate**: parity green for all migrated facts; baselines at-or-above.

3. **Session 3 — Migrate fact batch (round 2) + flip default**
   - Migrate remaining facts.
   - Flip default producer to SwiftSyntax.
   - Regex producer stays available behind a flag for one release cycle.
   - **Gate**: full sweep at-or-above baseline with SwiftSyntax default; parity tests still green.

4. **Session 4 — Retire regex parser + close v2**
   - Delete `SwiftInterfaceAccessParser.cs` and the regex code path.
   - Remove the `RegexProducer` strategy and the producer-selection flag.
   - Tighten `SourcePosition` invariants now that positions come from real spans rather than estimates.
   - **Gate (target)**: `nuke compile` / `nuke test` / `nuke validate` (with PackGate + BehaviorTier triggers) / `nuke binding-tests --sim --device --macos --catalyst --tvos` all at-or-above baseline. v2 marked DONE.

**Why (litmus)**: prevents a known class of bad generated binding. The regex parser is correctness-fragile by construction; SwiftSyntax is the Swift compiler's own parsing front-end.

### Total: ~5–7 sessions

Allocation: M1 (1–2) + M2 (3–5). Same elapsed-time framing as v1 — validation-bound, not session-stacked. Each milestone ends with a full sweep; expect at least one fix-and-rerun cycle per milestone.

---

## Execution Strategy

Same validation tiers, agent usage, and standing rules as v1. See `architecture-gameplan.md` for the table and rules — not duplicating here. Two additions specific to v2:

- **Parity tests are the gate.** Both milestones swap one implementation for another behind a seam. Parity tests are how we prove the swap is safe; they are not optional and they do not get weakened to make a session ship.
- **One-release-cycle deprecation window.** Both swaps keep the old strategy reachable behind a flag for one published release cycle of `swift-dotnet-packages`. Delete only after that cycle ships clean.

### Checkpoints

Two checkpoints — one per milestone. Each runs the full sim + device + validate sweep and updates baselines.

1. End of M1: `libswiftDemangle` is the default; managed port reachable behind a flag.
2. End of M2: SwiftSyntax is the producer; regex parser deleted.

---

## Standing Rules

Inherited from v1 verbatim. Notably:

- Trunk-based by default; no long-running per-milestone branches.
- Milestone scaffolding under `src/docs/scratch/` and deleted in the milestone's completion commit.
- Zero-regression policy active throughout.
- Concise commit messages: subject + 1–3 sentences on *why*. No "v2 Session N" footers.

---

## Open Questions

1. **Demangler dylib provenance.** Use `/usr/lib/swift/libswiftDemangle.dylib` (system Swift), the toolchain-bundled dylib via `xcrun --find swift`, or both with a probe order? Recommendation: system first, toolchain fallback. The system dylib's API surface has been stable for years and avoids requiring a full Xcode install at runtime.

2. **SwiftSyntax host program distribution.** Vendor pre-built binaries in the NuGet package (one slice per Apple host platform), or build-on-first-use from sources shipped alongside? Recommendation: vendor, mirroring how we ship xcframework slices — first-run UX matters and SPM resolution at first invocation has failed for users before.

3. **SwiftSyntax version pinning.** Track the host Swift toolchain's bundled SwiftSyntax, or pin a specific tag? Recommendation: pin a tag and bump deliberately. The Swift toolchain ships SwiftSyntax versions tied to its release cadence; we want to control when we adopt new node shapes.

4. **Fact batching order in M2.** Resolved at session boundaries — pick the next batch by *drift risk*, not by code locality. The facts whose regex patterns have the most special cases are the ones most likely to be wrong now.

5. **When does v2 start.** Immediately after 1.0 candidate ships, or after the soak window completes (Open Question #6 of v1)? Recommendation: after the soak window — a 1.0 consumer bug could pull a different item forward.

---

## Out of Scope

Explicitly not in v2:

- Everything else in `Future/post-1.0-architecture-roadmap.md`. Those items are real but didn't make the ROI cut for round 1.
- Changes to what either producer outputs. M2 is a producer swap, not a fact-shape redesign. Adding new facts is post-v2.
- Demangler API surface changes. M1 swaps the implementation behind the existing call sites; it does not redesign how the rest of the codebase asks for demangled names.
