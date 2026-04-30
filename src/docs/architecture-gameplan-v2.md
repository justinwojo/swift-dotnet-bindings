# Architecture Gameplan v2 — Post-1.0 Round 1

**Status**: M2 (SwiftSyntax producer) is plan-of-record and in flight; Session 4 closes it. M1 as originally specified (a P/Invoke swap into `libswiftDemangle.dylib` with byte-equal parity tests) was rejected after a pre-implementation audit and is replaced by **M3 — Demangler Track Redesign**, a kill-gate spike followed by conditional migration, sequenced after M2.

**Companion docs**:
- `architecture-gameplan.md` — the 1.0 plan (DONE).
- `Future/post-1.0-architecture-roadmap.md` — the remaining post-1.0 inventory.

**Decision**: v2 ships two architectural changes. (1) SwiftSyntax producer behind `SwiftInterfaceFacts` (M2, in flight). (2) Demangler track redesigned around `swift-symbolgraph-extract` (M3, post-M2, gated by a 1-session spike).

This track is independent of `roadmap.md` (coverage / skip / library themes); both progress in parallel.

---

## The Litmus Test

Same bar as v1:

> Will this either expose a real binding failure earlier, prevent a known class of bad generated binding, or increase valid emitted API surface?

Status of each item:

- **SwiftSyntax producer (M2 — passes, in flight)**: retires ~4.2k LOC of regex parsing in `SwiftInterfaceAccessParser.cs` (4,223 lines as of this commit) plus the regex-inferred fact dictionaries on `SwiftInterfaceFacts`. The parent docs flagged this as the single largest "silent wrong binding" risk surface. M4 deliberately shaped `SwiftInterfaceFacts` so this swap can land incrementally.
- **Demangler track (M3 — passes conditionally on spike)**: in scope only if the M3 spike confirms `swift-symbolgraph-extract` + a small forward suffix mangler can fully replace the managed demangler. Pass → retires ~5,800 LOC of hand-ported demangler. Fail → the demangler swap returns to `Future/post-1.0-architecture-roadmap.md` and v2 closes after M2.

Items that don't pass the test stay in `Future/post-1.0-architecture-roadmap.md` until something pulls them forward.

---

## Why This Order

Original v2 placed `libswiftDemangle` first and SwiftSyntax second. The redesign reversed this:

1. **M2 is in flight and has high-confidence delivery.** Sessions 1–3 have shipped; Session 4 closes the milestone. Finishing locks in the regex retirement before opening a new track.
2. **M3 opens with a kill-gate spike.** One session answers a yes/no question. Pass → migrate. Fail → drop. This protects against burning 4–5 sessions on a milestone whose viability isn't yet proven.
3. **M3's migration cost lives in declaration-mapping work, not in vendoring upstream compiler source.** Symbol-graph JSON is Apple's documented, versioned, DocC-grade interchange format — AI-maintainable across toolchain bumps in a way the rejected `libswiftDemangle` shapes were not.

---

## Milestones

### Milestone 2 — SwiftSyntax producer behind `SwiftInterfaceFacts` *(3–5 sessions; in flight, S4 remaining)*

**Goal**: ~4.2k LOC of regex parsing in `SwiftInterfaceAccessParser.cs` (4,223 lines as of this commit) replaced by a Swift host program that uses SwiftSyntax to populate the same `SwiftInterfaceFacts` aggregator M4 introduced (24 required fact fields as of M2 Session 1).

**Scope**:
- New Swift host program (`SwiftInterfaceParser`) built via SPM, distributed alongside the generator.
- Output format: a single JSON document matching `SwiftInterfaceFacts`'s shape (one fact dict per top-level field). Producer writes JSON, .NET side deserializes into the existing immutable record.
- Producer strategy on the .NET side: `IInterfaceFactsProducer` with `RegexProducer` (current) and `SwiftSyntaxProducer` (new). Both producers must yield byte-equal output for the same input.
- Migrate fact-by-fact: each session picks N facts, lights them up in the SwiftSyntax producer, and asserts parity against the regex producer across the validation corpus before flipping the default.
- Source-position fidelity improves automatically — SwiftSyntax gives precise spans where the regex parser had to estimate offsets. M4's `SourcePosition` plumbing already handles non-null positions; the upgrade is just better data.
- Once every fact is migrated and parity holds, retire `SwiftInterfaceAccessParser.cs` and the regex code path.

**Why (litmus)**: prevents a known class of bad generated binding. Regex-driven inference of internal members, actor isolation, typed throws, availability, default args, subscript labels, etc. is the single largest "silent wrong binding" surface in the codebase.

**Sessions** (4):

1. **Session 1 — Swift host program scaffold + first fact**. SPM package, build integration, JSON contract, one fact migrated end-to-end (`MainActorTypePositions`), `IInterfaceFactsProducer` seam, parity test. **Gate**: parity green for the migrated fact; baselines at-or-above; regex producer remains default. **(DONE)**
2. **Session 2 — Migrate fact batch (round 1)**. Migrate ~7–8 facts by drift risk. Maintain parity tests. **Gate**: parity green; baselines at-or-above. **(DONE)**
3. **Session 3 — Migrate fact batch (round 2) + flip default**. Remaining facts; flip default to SwiftSyntax; regex producer behind a flag for one release cycle. **Gate**: full sweep at-or-above baseline with SwiftSyntax default; parity tests still green. **(DONE)**
4. **Session 4 — Retire regex parser + close v2-M2**. Delete `SwiftInterfaceAccessParser.cs` and the regex code path. Remove `RegexProducer` strategy and producer-selection flag. Tighten `SourcePosition` invariants. **Gate (target)**: `nuke compile` / `nuke test` / `nuke validate` / `nuke binding-tests --sim --device --macos --catalyst --tvos` all at-or-above baseline. M2 marked DONE.

### Milestone 3 — Demangler Track Redesign *(1 spike + 3–5 conditional sessions; sequenced after M2)*

**Background**. The original M1 proposed a P/Invoke swap into `libswiftDemangle.dylib` with byte-equal parity testing. A pre-implementation audit found three blockers:

1. The dylib's **public C API does not expose AST traversal** — only display strings. Existing call sites (`SwiftABIParser.cs`, `DemanglingResults.cs`) consume a structured `IReduction` tree. A single `IDemangler` seam satisfied by both managed and native strategies is not deliverable.
2. **"Byte-equal parity" has no operational definition** — the managed port has no textual printer, so direct comparison is undefined.
3. The proposed dylib path and exported symbol names were wrong (`/usr/lib/swift/libswiftDemangle.dylib` does not exist on macOS; the actual C exports are `swift_demangle_getDemangledName` etc., not `swift_demangle`).

A multi-round design consultation (3 Codex consultations + independent investigation) considered eight alternative shapes:

| Option | Shape | Verdict |
|---|---|---|
| A | Sidecar text demangler, managed port retained | Rejected — retires 0 LOC; doesn't address any class of bad binding on its own. |
| B | String → `IReduction` reverse parser | Rejected — snake-eating-tail (the validator IS the thing being retired); unbounded grammar scope. |
| C | Drop the demangler track entirely | Held in reserve as the fallback if the M3 spike fails. |
| D | A now + B later | Rejected — D's structured half is B by another name. |
| E | Vendor Swift demangler C++ source / link `libswiftDemangle.dylib` via Swift/C++ interop | Rejected — vendor-source pulls in 10–15k LOC including LLVM ADT subset; header+dylib path has unstable C++ ABI; neither is AI-maintainable for a non-coding owner. |
| F | Parse `swift-demangle --tree-only` text output | Rejected — text format is "lossy and not stable" per SE-0498; less LOC retirement than L; less stable. |
| G | Swift-callable structured demangling module | Rejected — no such module exists at the SDK surface; SE-0498's official `Runtime.demangle()` returns text, not a tree. |
| H | Status quo + corpus drift monitoring | Held in reserve as a complementary option (see "Held in reserve" below). |
| L | `swift-symbolgraph-extract` + forward suffix mangling | **Selected for spike.** Apple's official documented JSON format (DocC's input), stable since Swift 5.5, AI-maintainable. |

**The L approach**.

`swift-symbolgraph-extract` ships in every Xcode toolchain. For each Swift module it emits structured JSON containing one entry per public declaration:
- `identifier.precise` — the mangled name (with `s:` prefix)
- `kind` — `swift.struct` / `swift.method` / `swift.protocol` / etc.
- `names.title`, `declarationFragments` — structured declaration info
- `relationships` — `conformsTo`, `inheritsFrom`, `memberOf`, `requirementOf`, etc., between mangled names

Replacement strategy:

1. Spawn `swift-symbolgraph-extract` once per module being parsed (same pattern as M2's `SwiftInterfaceParser`). Cache output.
2. Build managed in-memory map: mangled name → structured declaration info.
3. ABI JSON demangle call sites (3 sites in `SwiftABIParser.cs`) become map lookups.
4. TBD synthesized symbols (`Ma`, `Mn`, `MP`, `Mu`, etc.) are categorized by suffix-stripping + lookup against the map.
5. Cross-module conformance descriptors (`Mc`, `WP`) — **the open question** — are constructed forward from `conformsTo` relationships in the symbol graph.

**Open risk**. `Mc` / `WP` symbols encode a (concrete-type, protocol) relationship using Swift mangling substitutions. If forward-constructing `<source-mangled><target-mangled>{Mc|WP}` from a `conformsTo` edge does not exactly match the actual TBD symbol — because the toolchain applies substitutions — we'd need to implement a subset of Swift mangling, which is the territory we're trying to retire. The spike answers whether substitutions are needed and how bounded the rule set is.

#### M3 Session 1 — Spike (kill-gate)

Single session. **Doc-only output. No production code.**

**Question**: Can symbol graph `conformsTo` relationships deterministically recover every `Mc` and `WP` symbol present in BindingTests + a sample of validation library TBDs, without implementing Swift mangling substitutions?

**Scope**:
1. Run `swift-symbolgraph-extract` against the BindingTests Swift module(s) plus 3–5 representative validation libraries (pick by complexity — at least one with cross-module conformances).
2. Enumerate every `Mc` and `WP` symbol from each library's TBD.
3. For each symbol, attempt forward construction from a `conformsTo` edge: `<source-mangled><target-mangled>{Mc|WP}`. Measure hit rate.
4. Characterize the misses: small set of substitution rules (e.g., `s` for stdlib types) or unbounded grammar work?

**Pass condition**: ≥99% hit rate WITH a substitution rule set bounded at ≤50 LOC.

**Fail condition**: substitutions require non-trivial mangling logic — drops us into demangler-shaped territory.

**Gate**: a spike report in `src/docs/scratch/` with hit rates, gap analysis, and a go/no-go recommendation. No code shipped this session.

#### M3 Sessions 2–N — Migration (conditional on spike pass)

Only proceeds if the spike passes. 3–5 sessions.

**Scope** (high-level — refined after spike):
- New managed component `SymbolGraphFactsProducer` consuming `swift-symbolgraph-extract` JSON.
- Vendor / probe the toolchain binary the same way M2 vendors / probes its tools.
- Replace `DemanglingResults.FromTbd` and the 3 `SwiftABIParser` demangle call sites with map lookups + suffix classifier + (if needed) the bounded substitution rule set from the spike.
- Migrate behind a feature flag. Parity-test against the managed demangler for one full validation cycle before flipping the default.
- Once parity holds across `nuke validate` + `nuke binding-tests --sim --device --macos --catalyst --tvos`, retire the managed demangler (`Swift5Demangler.cs`, `Swift5Reducer.cs`, `DemanglingResults.cs`, ancillary types — ~5,800 LOC).

**Why (litmus)**: prevents a known class of bad generated binding (demangling drift against Swift compiler changes). Eliminates ~5,800 LOC of hand-ported compiler logic the team doesn't want to own long-term.

#### Held in reserve

**Option H — corpus drift monitoring** *(1 session, not committed)*. Adds a CI gate that runs the managed port against `xcrun swift-demangle --tree-only` on a representative symbol corpus and fails fast on category divergence. Doesn't retire any LOC; provides drift insurance regardless of L outcome.

**Status**: not committed. Revisit after the M3 Session 1 spike result. If L migrates, H is optional ongoing insurance during transition. If L is dropped, H becomes the consolation deliverable to close the demangler track.

### Total: M2 = 3–5 sessions (S4 remaining); M3 = 1 spike + 0 or 3–5 conditional = 1 to 6 sessions

Same elapsed-time framing as v1 — validation-bound, not session-stacked. Each milestone ends with a full sweep; expect at least one fix-and-rerun cycle per milestone.

---

## Execution Strategy

Same validation tiers, agent usage, and standing rules as v1. See `architecture-gameplan.md` for the table and rules — not duplicating here. Two additions specific to v2:

- **Parity tests are the gate.** Both M2 and M3 swap one implementation for another behind a seam. Parity tests are how we prove the swap is safe; they are not optional and they do not get weakened to make a session ship.
- **One-release-cycle deprecation window.** Both swaps keep the old strategy reachable behind a flag for one published release cycle of `swift-dotnet-packages`. Delete only after that cycle ships clean.

### Checkpoints

Two checkpoints — one per milestone. Each runs the full sim + device + validate sweep and updates baselines.

1. End of M2: SwiftSyntax is the producer; regex parser deleted.
2. End of M3 *(conditional on spike pass)*: symbol-graph producer is the source of truth for demangling; managed port retired.

If the M3 spike fails, M3 closes immediately without a checkpoint and the demangler swap returns to `Future/post-1.0-architecture-roadmap.md`.

---

## Standing Rules

Inherited from v1 verbatim. Notably:

- Trunk-based by default; no long-running per-milestone branches.
- Milestone scaffolding under `src/docs/scratch/` and deleted in the milestone's completion commit.
- Zero-regression policy active throughout.
- Concise commit messages: subject + 1–3 sentences on *why*. No "v2 Session N" footers.

---

## Open Questions

1. **SwiftSyntax host program distribution.** *(resolved)* Vendor pre-built binaries in the NuGet package, **one binary per macOS host architecture** (arm64 and x86_64; the parser only ever runs on macOS — never iOS/tvOS/Catalyst slices). M2 Session 1 ships the host-arch binary only; a universal `lipo` binary is the M2 Session 2+ TODO. Mirrors how we ship xcframework slices today, with the simplification that swift-syntax 601.0.x links statically into the executable.

2. **SwiftSyntax version pinning.** *(resolved)* M2 Session 1 pins **swift-syntax 601.0.1**, declared in `tools/SwiftInterfaceParser/Package.swift`. Verified compatible with the host toolchain (Apple Swift 6.2.3). Bump only deliberately, in a session dedicated to adopting new node shapes.

3. **Fact batching order in M2.** Resolved at session boundaries — pick the next batch by *drift risk*, not by code locality.

4. **`Mc` / `WP` substitution rule scope.** *(open — answered by M3 Session 1)* Spike output determines whether the L migration ships or the demangler track drops.

5. **Apple framework symbol graphs.** *(open — characterized by M3 Session 1)* Apple does not pre-ship `.symbols.json` files in Xcode SDKs; we extract on demand. Verified locally that `Foundation` and `SwiftUI` extract cleanly (18k and 74k symbols respectively). The cost / cache strategy is part of the M3 migration scope if the spike passes.

6. **H corpus monitoring decision point.** *(open — revisit after M3 Session 1)* Whether to add corpus drift monitoring is gated on the spike result.

---

## Out of Scope

Explicitly not in v2:

- Everything else in `Future/post-1.0-architecture-roadmap.md`. Those items are real but didn't make the ROI cut for round 1.
- Changes to what either producer outputs. M2 is a producer swap, not a fact-shape redesign. Adding new facts is post-v2.
- Demangler API surface changes. M3 swaps the implementation behind the existing call sites; it does not redesign how the rest of the codebase asks for demangled names.
