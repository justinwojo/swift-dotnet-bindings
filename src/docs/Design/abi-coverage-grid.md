# Design: ABI Coverage Grid (thin-corner runtime coverage)

Status: **design pass — revised after Codex + Grok review**, not yet approved for
implementation. 2026-06-11. Companion to [`../research-directions.md`](../research-directions.md)
(Direction 1).

> **Revision note (v2).** Independent reviews by Codex (`019eb50f-2950-7001-96a4-57f7a4e5a81e`)
> and Grok (`019eb513-1365-73b2-aa3d-56ee30750f9f`) converged on six changes, all folded in
> below: (1) soften the "thin corner" premise — it holds for the *external* real-library corpus
> but the *local* BindingTests already covers much of the basic closure/tuple space, so the real
> gap is complex *combinations* and specific Latent shapes; (2) **audit existing fixtures into the
> manifest first** (Phase 0) before authoring anything new; (3) **full cross-product**, not
> pairwise, for the small first corner; (4) add a **third disposition** `supported-low-priority`
> (rare-but-supported ≠ by-design-gray); (5) **don't extend `coverage-report.py`** — separate
> C# Nuke helper; (6) **v1 maps cells to tests by name in the manifest** over the *existing*
> JSONL + inventory, touching none of the crash-recovery / descriptor / JSONL data plane — the
> `[AbiCell]` attribute is deferred to v2 and added only if name-joins prove fragile in practice.

---

## 1. Purpose

Make "is the generator solid enough for 1.0?" a **measurable** question for the
under-exercised corners of the ABI surface, by:

1. Deliberately *enumerating* the thin-corner feature-interaction cells (closures, inout,
   tuples first) instead of waiting for a real library to happen to contain them.
2. Running each cell **end-to-end on both runtimes** (sim / Mono-JIT and device / NativeAOT)
   with a value-round-trip oracle.
3. Emitting a **green / red / by-design-gray grid** as a first-class artifact, plus a **gate**
   that fails if an expected-green cell has no fixture or doesn't pass.

The motivating evidence (measured 2026-06-10, see research-directions.md): the existing
end-to-end corpus (~44 apps across `swift-dotnet-packages` + `internal-binding-testing`,
both runtimes, real value assertions) is **thick exactly where real libraries are thick**
(enums, structs, optionals, simple async/protocols) and **thin exactly where they're thin**
(closures, inout, tuples, constrained generics, actors). The roadmap's Latent list —
"present mechanism, zero emission site in current surface" — clusters in the *same* thin
corners, causally: nothing reaches that code because no common library uses that shape, yet
the generator still emits it. The 1.0 risk is a consumer calling an emitted binding for a
shape **no test has ever run on either runtime**.

A **green** cell is as valuable as a red one: it converts a Latent from "unknown, unreached"
to "exercised on sim+device, confirmed safe" — the confidence currency we lack today.

**Scope correction (post-review).** The thinness measurement above is of the *external*
real-library corpus (`swift-dotnet-packages` + `internal-binding-testing`). The *local*
BindingTests this design layers on is **not** thin on the basics: it already has ~18 closure
Swift fixtures (escaping, throwing, async, closure-returning, generic/nested/struct closure
bridges) + 13 closure C# test files, dozens of reverse-dispatch `*Delegate` entries in
`PreservedProtocols`, and 2/3/7-element + named + mixed tuple coverage. So the premise is
**not** "we don't test closures/tuples" — we do. The genuine local gap is narrower and more
specific: (a) the *complex combinations* real libraries never forced (resilient × async ×
optional-tuple-return), and (b) the named roadmap **Latent** shapes with no current emission
site — `inout` writeback observability, `inout` ObjC-bridgeable, generic-parent `inout`,
mixed-indirect generic tuple returns, same-signature closure/async fan-out. The grid's job is
to reach *those*, not to re-cover the basics. This is why Phase 0 (§11) audits existing
fixtures into the manifest *first* — so we add only what's actually missing.

## 2. Non-goals

- **Not** an emission/skip-coverage tool. That axis is saturated and already served by
  `coverage-report.py` (`coverage-matrix.json`) and the `--skip-surface` ratchet
  (`build/baselines/skip-surface-baseline.json`). This grid is about **runtime correctness of
  what *does* emit.**
- **Not** a new test runner, app, or harness. It reuses `SwiftBindingsTestLib` +
  `RuntimeTestsApp` + the existing sim/device JSONL pipeline verbatim.
- **Not** a fuzzer. Fixtures are deterministic, hand-authored, reviewable (no RNG, no
  code-gen build step in the first slice).
- **Not** an attempt to cover the *full* ABI grid. First slice is one corner; widen only if
  it pays off.

## 3. Core concept

Four pieces, three of them tiny:

- **Cell** — a point in the feature-interaction grid, identified by a stable dotted id, e.g.
  `closure.escaping.async.arg-int.ret-int` or `inout.struct.frozen.blittable` or
  `tuple.ret.multi.under-throws`. A cell is the unit of coverage.
- **Manifest** (`BindingTests/abi-grid-manifest.json`, new) — the **single source of truth**:
  declares every intended cell, its **disposition** (see below), and how it maps to a test
  (by class+method name in v1 — see §6). Enumeration is enforced here, not by code-gen: a
  cell with no passing fixture is a visible hole.
- **Disposition** — one of three, *not* a binary green/gray (per review):
  - `expect-green` — supported surface we claim is solid. **Release-gating**: a missing or
    non-passing fixture fails the gate.
  - `supported-low-priority` — supported but uncommon surface, exercised for completeness.
    Reds here stay **red** (not hidden) but are **non-release-blocking** — they inform, they
    don't gate. This is the bucket for "rare in consumer code too," distinct from by-design.
  - `by-design-gray` — intentionally unsupported by product/architecture (PATs, result
    builders, autoclosure, …). Must cite a roadmap *Not Worth Addressing* / *Explicitly Out
    of Scope* entry. "Uncommon" alone is **not** grounds for gray — that's `supported-low-priority`.
- **Grid** — the report: manifest cells × merged sim+device JSONL → a table
  (cell, disposition, sim status, device status, fixture) + a JSON artifact
  (`output/abi-grid.json`) + a roll-up (e.g. "% of expect-green cells green on sim+device").
- **Gate** — fail the run only on `expect-green` cells that are missing a fixture or not
  `pass` on every declared runtime. `supported-low-priority` reds are reported, not gated.

## 4. Architecture — what's reused vs. what's new

**Reused unchanged** (this is the point — near-zero new execution machinery):

- Swift fixtures live in the existing `BindingTests/Sources/SwiftBindingsTestLib/` domain
  folders (`Closures/`, `Generics/`, `Types/`, …). They flow through the existing
  xcframework→generator→wrapper pipeline (`Build.BindingTests.cs`).
- C# round-trip tests live in the existing `RuntimeTestsApp/` domain classes, extend
  `TestBase`, assert via `AssertEqual`/`AssertThrows`/etc., are discovered by
  `TestDiscoveryGenerator`, and report pass/fail through the existing
  `Documents/test-results.jsonl`.
- Both runtimes, crash recovery, and JSONL collection (`SimCtl`/`DeviceCtl
  .CopyResultsFromSandbox`) are exactly as today. The grid consumes the JSONL the harness
  already produces.

**New, and deliberately small:**

1. **The manifest** (`abi-grid-manifest.json`) — declarative, hand-maintained, the spine.
2. **A cell↔test mapping** — in v1, `(class, method)` names declared in the manifest (§6); no
   test-infra change.
3. **A report+gate step** — a **C# Nuke helper** (chosen over extending
   `coverage-report.py`, which is emission-axis, cwd-sensitive, and whose
   `KNOWN_UNSUPPORTED_FEATURES` is a feature-name set, not a runtime ABI-disposition authority).
   The helper consumes the *existing* merged JSONL + `TestClasses.g.txt` inventory, joins to
   the manifest, emits `output/abi-grid.json` + a human table + a roll-up, and returns the
   gate verdict. Wired to a `nuke binding-tests --abi-grid` flag (report only; the fixtures
   themselves run as ordinary tests in the normal run). Keeping it in the typed build avoids a
   new Python execution path.

## 5. First-slice cell enumeration (the thin corner)

Pick the corner with the densest overlap of "corpus thin" × "Latent dense." Concretely:

**Closures** (corpus: only `Bool→Void` + nullable; Latent: closure/async fan-out, escaping
dead-code §2.1):
- direction: `{ param (C#→Swift), return (Swift→C#) }`
- escaping: `{ escaping, non-escaping }`
- effects: `{ sync, async, throwing, async-throwing }`
- arity/payload: `{ 0-arg, 1-arg primitive, 1-arg struct, tuple-arg }` × return `{ void, primitive, struct, Data }`

**inout** (corpus: ~none; Latent: inout ObjC-bridgeable retains dead slot, inout blittable
round-trip loses writeback):
- type: `{ frozen blittable struct, resilient struct, ObjC-bridgeable (URL/Decimal), generic T }`
- assert the **writeback** actually lands back in C# (the known blittable-roundtrip latent).

**Tuples** (corpus: one single return + one nested callback; Latent: mixed-indirect generic
tuple returns):
- return shape: `{ (A,B), (A,B,C), nested ((A,B),C) }`
- element mix: `{ all-primitive, mixed primitive+struct, contains-generic, contains-optional }`
- under effects: `{ plain, async, throwing }`

**Sampling (revised per review): full cross-product, not pairwise.** The enumerated corner is
small enough that the full product is reviewable and far higher-signal — pairwise guarantees
every *pair* appears but can miss the exact 3-/4-way combination that actually trips a Latent,
and the motivating examples here *are* triples (`resilient × async × optional-tuple-return`).
For `inout` the full enumeration is tiny; for tuples it's ~`3 shapes × 4 element-mixes × 3
effects = 36` — author it fully. Combinations deliberately out of this slice's scope are
recorded in the manifest as such (documented absence), not silently dropped.

**Author only what the Phase-0 audit shows is missing.** Per review, much of the basic
closure/tuple space already has local fixtures. So the new authored cells concentrate on the
genuinely-unreached, Latent-tied shapes:
- `inout` writeback **observability** (the confirmed `ParameterTests` gap: today inout
  mutation is *not* asserted to reach the caller — assert it does),
- `inout` ObjC-bridgeable (URL/Decimal) and generic-parent `inout`,
- mixed / generic tuple returns **under async / throws**,
- same-signature closure/async fan-out (the roadmap Latent repro).

**Disposition, not gray-mask.** Cells are dispositioned per §3 (`expect-green` /
`supported-low-priority` / `by-design-gray`). Only genuinely product/architecture-unsupported
shapes are `by-design-gray` and must cite a roadmap *Not Worth Addressing* / *Explicitly Out
of Scope* entry; uncommon-but-supported shapes are `supported-low-priority` (reported red, not
gated), never grayed for rarity alone.

## 6. Cell↔test mapping — decided: name-join in v1, attribute deferred

Both reviewers agreed the manifest must be the source of truth; they split on *whether to
also add an `[AbiCell]` attribute now*. Decision (siding with Grok's evidence on harness
fragility): **v1 maps cells to tests by `(class, method)` name in the manifest**, and the
report joins those names against the *existing* JSONL + the `TestClasses.g.txt` inventory.

- **v1 — manifest name-join (chosen).** Touches **none** of the delicate execution plane:
  no change to `TestDiscoveryGenerator`, `TestMethodDescriptor`, `TestResults`/JSONL emission,
  or the crash-recovery synthesis loop (all of which are name-oriented and NativeAOT-safe by
  design). The report does a best-effort `(class, method) → cell` join. Fragility to renames
  is real but **caught by the gate** (a manifest cell citing a method absent from the run's
  inventory fails). Allows **1:N** — one test may carry multiple cell ids; the manifest maps
  each cell to its covering `(class, method)`, and an `expect-green` cell requires ≥1 passing
  fixture (not 1:1).
- **v2 — `[AbiCell]` attribute (deferred, not rejected).** Co-locates the cell id with the
  method (rename-safe, self-documenting). Add it **only if** name-joins prove fragile in
  practice. It's a clean one-way upgrade: the manifest stays source of truth, so adopting the
  attribute later is additive. Deferring it avoids threading a `cell` field through the
  source generator, descriptors, the JSONL writer, *and* the crash-synthesis path (which
  fabricates entries from name inventory alone) — exactly the reliability-critical machinery
  to leave untouched in a first slice.

**Crash-entry attribution.** Crash recovery synthesizes `"crash"` entries from the
class/method inventory (no cell field). The report maps a synthesized crash on a mapped
method to its cell and treats it as **red** for an `expect-green` cell (a crash is a fail);
for `supported-low-priority` it's reported, not gated. Partial runs (`--class-filter`,
smoke-flag, `--skip-regen` staleness) yield a **partial grid** — the report marks it partial
and the gate only enforces on a full `--abi-grid` run (§10).

## 7. Both-runtimes handling

- A cell declares its runnable runtimes (default: `[sim, device]`). The grid has a column per
  runtime. `green` = `pass` on every declared runtime; `red` = `fail`/`crash` on any (gating
  for `expect-green`, reported-only for `supported-low-priority`); `gray` = by-design (§3).
- Sim-only (`[SkipOnDevice]`) or device-only (`[SkipOnSimulator]`) is allowed but must be
  justified in the manifest (a cell intentionally one-runtime is recorded, not silently
  dropped) — this honors the "no silent caps" rule.
- **Inner loop**: `nuke binding-tests --sim --abi-grid` produces a sim-only grid in seconds-
  to-minutes. **Full grid**: `nuke binding-tests --sim --device --abi-grid` (device leg is
  NativeAOT-publish, minutes). The report merges both JSONL sets when present; with only one,
  it emits a partial grid and says so.

## 8. Reverse-dispatch caveat (must not be missed)

Any closure cell exercised via **reverse dispatch** (C# implements a Swift protocol, Swift
calls back through `any P` / `EveryProtocol`) requires its protocol added to
`PreservedProtocols` in `build/Helpers/SwiftSourceStripper.cs` — otherwise the stripper
removes the `EveryProtocol` conformance + `Get_EveryProtocol_<P>_WitnessTable` getter and the
test dies with `EntryPointNotFoundException` (confirmed: explore + memory
`new_reverse_dispatch_test_preserved_protocols`). The design notes this so the first slice's
closure-param cells (which are naturally reverse-dispatch-shaped) land the `PreservedProtocols`
entry as part of fixture authoring, not after a red. Direct (non-reverse) closure-return cells
don't need it.

## 9. Ratchet / baseline — deferred

The first slice is **discovery**: the first full run establishes ground truth. A committed
`abi-grid-baseline.json` ratchet (mirroring `skip-surface-baseline.json`: reds can't regress,
new greens ratchet up) is a natural follow-up but is **out of scope for slice 1** — we need to
see the grid before locking a baseline. The slice-1 gate is simpler: every `expect-green` cell
must be green on its declared runtimes.

## 10. nuke / CI integration

- Fixtures run inside the normal `nuke binding-tests` run (they're ordinary tagged tests) — no
  separate execution path, so they're covered by the everyday gate automatically.
- `--abi-grid` adds the post-run report + gate. Not in `--compile-only` (it's a runtime grid).
- Cadence mirrors the marshalling-sensitive gates: the grid is most meaningful on
  `--sim --device` and should run before a release and after calling-convention / struct-
  marshalling / closure / P-Invoke changes — the same triggers as `--mixed-pack`/`--device`.

## 11. Phased delivery

- **Phase 0 — audit existing fixtures + report skeleton.** Inventory the *existing* closure /
  tuple / inout tests (Grok's evidence: ~18 closure fixtures, 2/3/7-tuple coverage, the 4-func
  `Inout.swift`), map a representative subset into the manifest as `expect-green` cells, build
  the C# report+gate helper, and prove the grid renders green on sim against tests that already
  pass. **No new fixtures yet** — this both validates the plumbing end-to-end cheaply *and*
  establishes which cells are already covered, so later phases add only what's genuinely
  missing. Exercising the report path before authoring is a hard prerequisite (per review).
- **Phase 1 — fill the genuine gaps.** Author only the unreached, Latent-tied cells the audit
  exposed (inout writeback observability, inout ObjC-bridgeable, generic-parent inout,
  mixed/generic tuple returns under async/throws, same-signature closure/async fan-out), incl.
  any `PreservedProtocols` entries for reverse-dispatch cells. Full cross-product per corner.
  Run `--sim --device`, triage reds (real pre-1.0 bugs → root-cause fix → cell goes green),
  disposition the rest (`supported-low-priority` vs `by-design-gray`). First real grid.
- **Decision point.** If the gaps are dirty, widen to constrained/multi-param/pack generics.
  If clean, that itself is a strong 1.0 signal — stop cheaply, keep the grid as a standing
  artifact. (Ratchet baseline is a later add once the first full grid exists.)

## 12. Decisions from review (was: open questions)

The two-reviewer pass resolved the open questions:

1. **Cell↔test mapping** — manifest name-join in v1; `[AbiCell]` attribute deferred to v2,
   added only if name-joins prove fragile (§6).
2. **Report implementation** — C# Nuke helper consuming the existing JSONL + inventory; do
   **not** extend `coverage-report.py` (§4).
3. **Sampling** — full cross-product for the small first corner, not pairwise (§5).
4. **Premise** — sound for the *external* corpus; softened for *local* BindingTests, which
   already covers the basics. The grid targets complex combinations + named Latent shapes, not
   the basics (§1 scope correction). Phase 0 audits existing coverage first (§11).
5. **Disposition** — three buckets (`expect-green` / `supported-low-priority` /
   `by-design-gray`); rarity ≠ gray (§3).
6. **Manifest is source of truth** — it enumerates cells explicitly (not derived from
   attributes), which is what makes "missing cell" detectable (§3, §6).

Remaining genuinely-open items for the implementer: the exact roll-up metric(s) to surface;
when (if ever) to introduce the ratchet baseline; and whether `supported-low-priority` reds
should appear in any CI summary or only the local grid artifact.

## 13. Risks

- **Premise risk**: if thin corners are thin because consumers don't use them either, reds may
  be low-value. Mitigated by: the `supported-low-priority` disposition (such reds inform but
  don't gate), starting with shapes that are supported (not by-design-unsupported), and the
  value of greens-as-confirmation regardless.
- **Manifest rot** — the v1 name-join is fragile to test renames. The gate (a manifest cell
  citing a method absent from the run inventory fails the build) is the defense; the deferred
  `[AbiCell]` attribute (v2) is the escalation if rot proves frequent in practice.
- **Device-leg cost** — NativeAOT publish is minutes. Mitigated by sim-only inner loop; full
  grid is a pre-release / marshalling-change gate, not every iteration.
- **Scope creep into a fuzzer / full grid** — explicitly resisted; first slice is one corner,
  deterministic, with a hard decision point before widening.
- **Cascade risk** — per `feedback_no_session_cascade`: enumerate the corner's cells up front
  in the manifest; do not discover-as-you-go across N follow-on sessions.
