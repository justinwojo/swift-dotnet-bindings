# Session 09b — wrapper verify-recover loop — mechanism-gap pinning tests + wave-2 routing

**Outcome:** the four mechanism-gap fixes that keep the verify-recover loop's per-render restoration
sound are now each pinned by a test that goes red if the gap reopens, delivered at the layer where a
regression is actually observable; and the five remaining backlog items were scoped item-by-item against
the prime invariant, corroborated by two independent external reviewers, and routed to
`src/docs/not-planned.md` as trigger-gated wave-2/deferred work rather than forced in. No production
generator code changed this session — the wave-1 loop stays exactly as session 09 (`753548e8`) left it
(inert on the corpus, fail-closed on everything coarser than leaf/accessor).

## What landed (two new test files + doc/baseline updates, no production change)

1. `src/Swift.Bindings/tests/UnitTests/EmitterTests/EmissionFactsJournalTests.cs` (5 tests) — pins the
   **type-database undo log** (`EmissionFactsJournal`), the driver's gap-#2 seam, which
   `ModuleEmissionStateSnapshot`/`DeclEmissionStateSnapshot` do NOT cover. The driver rewinds it via
   `_outerJournal.RestoreInto(_typeDatabase)` before every render. Tests:
   - `RestoreInto_UndoesEveryStampedFact_LeavingTheRecordBitIdentical` — stamps three independent facts
     (`EmittedMemberCount`, `EmittedMetadataPInvoke`, `CSharpTypeName`) and asserts **whole-record**
     `Assert.Equal(before, restored)`, so a partial restore that fixes one field and leaks another goes
     red, not just a total no-op. **Genuine-red verified**: making `RestoreEmissionRecord` leak the
     stamped `CSharpTypeName` fails this test (and only this one).
   - `RestoreInto_UndoesAStampOnAnOutOfModuleRecord` — covers the separate out-of-module `RestoreEmissionRecord`
     branch.
   - `RestoreInto_RollsBackToTheOriginal_NotAnIntermediateStamp` — first-write-per-type wins.
   - `TransferTo_KeepsTheSettledStampButLeavesItUndoableByTheOuterJournal` — the exact mechanism gap #2
     keys on: a settled render moves its pre-images to the outer journal so the stamp stays on the record
     (the compile reads it) yet the outer loop can still undo it before the next render.
   - `TransferTo_KeepsTheEarliestPreImageAcrossRenders` — earliest pre-image survives across renders.

2. `src/Swift.Bindings/tests/UnitTests/Diagnostics/InEmissionDriverRestorationTests.cs` (5 tests) — the
   **production-driver orchestration** coverage both reviewers flagged in session 09 as the untested gap
   (Codex M2 / Grok M4). Drives the real `InEmissionDriver` through repeated renders on one instance:
   - `EmptyDenylistRenderedTwice…` / `NonEmptyDenylistRenderedTwice…` — render N is a pure function of its
     denylist (byte-identical output). The non-empty case now carries a local non-vacuity guard
     (`clean != first`) so "identical under a denylist" only passes once the denylist did observable work.
   - `WithdrawalBetweenTwoCleanRenders…` — render 1 == render 3, render 1 != render 2.
   - `SameDenylistReappliedAfterAnInterveningRender…` — render 1 == render 3.
   - `EachRender_InstallsAFreshSpecializationEngine…` — **genuine-red gap-#1 structural pin**: the engine
     the emission context carries differs by reference across renders, so a regression that dropped the
     rebuild (or swapped it for a reference-restore) is caught. This is the one per-channel leak the
     driver layer can assert directly.
   The output snapshot now prefixes each file's content with its relative path, so a render that split,
   renamed, or moved a file (not just changed text) is a real difference the byte-identity checks catch.

`build/baselines/validation-baseline.json`: `swift_bindings_unit_pass_floor` **15202 → 15212** (auto-written
by `nuke test` to the real deterministic count; +10 = 5 journal + 5 driver).

`src/docs/not-planned.md`: new section **"Wrapper verify-recover loop — wave-2 & deferred"** routing the
remaining items (six trigger-gated rows) with their soundness proofs and reopen triggers.

## The four mechanism gaps → their pinning tests (the map)

Each gap is a prime-invariant closure — restoration that must fully rewind before the next render, or the
loop ships a binding that compiles but is wrong. Pinned at the layer where a regression goes red:

| Gap | What it restores | Pinning test (goes red if it reopens) |
|---|---|---|
| #1 engine rebuild | `ConcreteSpecializationEngine` memoizes rejected pairings in place → rebuilt fresh per render | `InEmissionDriverRestorationTests.EachRender_InstallsAFreshSpecializationEngine…` (structural, genuine-red) + `EmissionStateSnapshotCoverageTests` (context reference restore) |
| #2 typeDB emission facts | `EmissionFactsJournal` outer-journal pre-image restore | `EmissionFactsJournalTests` (whole-record round-trip, genuine-red) |
| #3 compile provenance before cleanup | provenance captured before the `.wrapper-build` staging tree is dropped | **no primitive behavioral pin yet** — cleanup is covered (`Compile_CleansUpTempDir`) but the capture-before-cleanup ordering is only observed indirectly by the driver tests; a dedicated pin is **wave-2** |
| #4 IntervalMap droppable-alone | `DroppableGate` wraps every provenance step so a non-droppable hit fails closed | `RecoveryModelTests` (droppable-alone rules) |

**Honest scope on the driver tests.** The `InEmissionDriverRestorationTests` byte-identity assertions are
an **orchestration smoke**, NOT per-channel leak detectors: on the shared `ContainmentFixture` the
emission facts and specialization graph do not change re-emission output, so byte-identity would still
hold if a single restoration channel were removed. The per-channel leak detection therefore lives at the
**primitive** layer (`EmissionFactsJournalTests`, `EmissionStateSnapshotCoverageTests`); the driver tests
prove the real driver *orchestrates* those primitives across renders without crashing, spinning, or
accumulating stale output, plus the one structural gap-#1 engine-rebuild assertion. The class and method
docs state this limitation explicitly rather than over-claiming. A driver-level per-channel leak detector
needs a corpus module with rejected specialization pairings / output-affecting emission facts — wave-2.

## The 8-probe evidence (why no natural "emitted-but-broken" fixture exists)

inc.4's original ask was a BindingTests resilience fixture: a real emitted-but-broken wrapper with a
recoverable healthy sibling. I empirically probed 8 shapes across the documented-fragile emitter areas
through the **real wrapper-compile path** (build a minimal sim xcframework → run the loop-active generator
with wrapper compilation → observe whether swiftc rejects the emitted wrapper):

| Probe | Shape | Result |
|---|---|---|
| HOpaque | `func makeOpaque() -> some Equatable` (opaque return) | generator exit 0, wrapper compiles clean, member tombstoned |
| HVariadic | `func sumAll(_ xs: Int...)` (variadic) | clean, tombstoned |
| HRethrows | `rethrows` function | clean, tombstoned |
| HTupleProp | tuple-typed property | clean, tombstoned |
| HExtProp | protocol-extension default properties on a constrained generic (`extension Flagged` on `Holder<V>`) | clean, tombstoned |
| HCsm | resilient-struct / custom-struct-marshalling return (RF root-cause neighborhood) | clean (204-line wrapper), tombstoned |
| HOpaqueProp | opaque-typed property | clean, tombstoned |
| HExtProp2 | extension-default properties on an AnimatableData-style constraint (RF-adjacent) | clean, tombstoned |

**Every shape emitted a clean-compiling wrapper** — the emitter reliably tombstones the unbindable member
up front and emits the healthy sibling, rather than emitting a broken wrapper. Corroborated by the 20-lib
corpus (`PostProcessorStrippedBlockCount: 0`, no `SWIFTBIND111/112` in the checked-in BindingTests
reports). Conclusion: a *natural* emitted-but-broken wrapper is essentially a live generator bug, which
policy says **fix, not enshrine**. This is a positive result about emitter maturity, not a coverage hole.

**Reopen trigger (verbatim, recorded in `not-planned.md`):** *when a real generator bug producing an
emitted-but-broken wrapper surfaces in the corpus — reproduce THAT as the fixture before fixing it; do not
manufacture one.*

## Integration-test determinism (session 09's inc.4, unchanged)

The real-swiftc-in-the-loop coverage is `WrapperRecoveryLoopIntegrationTests` (session 09): recorded
genuine swiftc `.stderr.txt` fixtures are replayed through the REAL `SwiftDiagnosticParser` → attributor →
`WrapperRecoveryController`. It is **deterministic and CI-safe** — no live toolchain, no `xcrun`/`swiftc`
invocation — and asserts on **attribution/convergence outcome** (which recovery units get withdrawn, that
the loop converges), not on diagnostic-text matches that would drift with a compiler-version bump.

## Why the rest is wave-2 / deferred (three-brain-agreed)

Scope determination = my own code reading + Codex + Grok, all three converging. External-reviewer
soundness holes below are theirs, independently reached; the routing decisions are mine.

- **Item 1 — production `RecoveryGraph` for coarse recovery = genuinely wave-2.** A certainty-only but
  *incomplete* graph is **unsound in the dangerous direction**: `SafeToDrop`'s `UnknownUnit` guard fires
  only when the withdrawal *target* is unmodelled; a **known** unit with a **missing** inverse `Requires`
  edge reads as "no retained dependent" → `Safe`, and `Escalate` builds its retained universe from
  `graph.Units`, so an unmodelled dependent is invisible to escalation too → `Closed` with an under-sized
  withdrawal set = compile-clean/runtime-wrong. The required proof boundary is not "recorded edges are
  certain" but "every unit eligible for a `Safe` verdict has a **known-complete** emitted-dependent set" —
  evidence the emitter does not produce today (that is the wave-2 dependency-capture work). Separately,
  **Gate-0 cannot even enact most coarse scopes**: `WrapperDenylistSeed` seeds by `unit.Decl` and
  `EmitterPoisonList` keys on `DeclId.Canonical`, erasing scope; whole-type withdrawal works, but
  `ManagedProtocolConformance`/`ForwardProtocolView` collapse onto the protocol's whole-type suppression,
  and `ConformanceEdge`/`SharedHelperBundle` synthesize discriminator-qualified `DeclId`s **no emitter gate
  queries** → inert no-ops. Sound coarse recovery needs both emitted-dependency-completeness capture and
  unit-identity-aware Gate-0 actuators. Until then coarse culprits must stay `RequiresGraphClosure`
  fail-closed (which is exactly what the wave-1 controller already does).

- **Items 2/3 — ABI-as-loop-input, strip-as-iteration-0 = doable-but-narrow, deferred.** Both have a
  sound leaf/accessor-only path via `ModuleEmissionContext.TryGetWrapperSymbolOwner` for `SBW_*`/`SBSW_*`
  wrapper-owned symbols, failing closed for unresolved/mangled/shared/coarse. But: `$s…` mangled and `Tj`
  dispatch-thunk symbols have no reverse map (`MethodName` is not overload/scope-unique, so not a sound
  fallback); both are inert on the corpus (0 SWIFTBIND095, 0 strips per the checked-in manifest); both
  touch the emission fault path; and neither has a natural end-to-end fixture without a live generator bug
  (forbidden to manufacture). Current hard-fail behavior is already sound. Strip-as-iteration-0 is
  additionally coupled to the convergence-predicate work (its strip-everything path is one of the two
  false-convergence sites) and its full form is the wave-2 reconciler retirement.

- **No-recompile consume of the converged outcome + convergence-predicate precision = deferred, coupled.**
  These cannot land independently. The loop's `verifyRecoverCompile` compiles the **simulator slice
  single-arch** for attribution only; the authoritative post-loop compile builds the **fat device+sim
  xcframework** via `lipo` + emits consumer-targets + the manifest. Consuming `LastConvergedOutcome` today
  would ship a sim-only single-arch wrapper. And the convergence predicate (`AllSlicesClean = !AnyFailed`)
  returns clean for **zero recorded slices** — latent-safe today because the post-loop recompile contains
  it, but it must become precise (require ≥1 recorded successful promised slice, or an explicit
  no-wrapper-surface signal) *before* the loop result is authoritative. Tightening it now would regress a
  legitimately-no-wrapper module and a strip-everything module into fail-closed — a module-ship policy
  change that must move with the reconciler-retirement decision, not alone.

- **Path parity = ratified permanent asymmetry (SDK fast path) + deferred (device/all).**
  `--compile-wrapper-only` and the SDK two-pass `--skip-wrapper-compilation` pass deliberately skip
  parse/generate and have no decl tree, type database, emission context, or Gate-0 re-render capability —
  they **cannot** recover, and keeping them fail-closed is sound and permanent, not a gap. Device/`all`
  could recover via equivalent in-process compile delegates, but that rides on the no-recompile work
  (the loop would need to produce the device slice).

- **Item 5 — BindingTests resilience fixture = genuinely wave-2, trigger not fired.** See the 8-probe
  section: no natural emitted-but-broken wrapper with a recoverable healthy sibling exists in the repo, and
  manufacturing one would enshrine a live bug. Coverage today: hermetic real-swiftc integration
  (`WrapperRecoveryLoopIntegrationTests`, session 09) + the mechanism-gap pinning tests (this session).

## Gates

- `nuke test`: **15,212 passed / 0 failed / 1 skipped** (Swift.Bindings.Unit.Tests) + Analyzers **35/35** +
  Runtime **719/720** (1 pre-existing skip). Floor ratcheted **15202 → 15212** (+10 new tests). Floor met.
- Both new files are **unit-only, no emitted-byte change** ⇒ the session-09 sim leg (`nuke binding-tests`
  default sim: **3242 pass / 0 fail**) and `--compile-only` **EXIT 0** from the loop commit `753548e8`
  still stand and were NOT re-run — nothing this session can change generated output.
- `--device` leg: **flagged, not run.** This session adds only unit tests; the loop is inert on the corpus
  and no emitted byte moved. A device (NativeAOT) leg is warranted before any release that depends on the
  loop actually firing on the recovery path — not for this test-only landing.

## Review (paired Codex + Grok)

- Scope-determination consult (earlier this program): Codex `019f7881-5e37-7373-9664-1197431b180e`;
  Grok `019f7881-6ca6-77f0-8a00-0afb41a09a74`. Both independently reached the two `SafeToDrop`
  incompleteness holes and the Gate-0 scope-erasure finding, and agreed the wave-2 vs doable-but-narrow
  split recorded above.
- Final pinning-test diff review: Codex `019f7895-8a2f-78e3-962d-bb4bccb9f244`;
  Grok `019f7895-979e-7a20-ae61-860555113d78`. **Both: NO High.** Dispositions:
  - Codex/Grok Medium (driver byte-identity does not prove per-channel restoration on this fixture) —
    **dispositioned by design**: the driver tests are an orchestration smoke, the per-channel leak
    detectors are the primitive `EmissionFactsJournalTests`/`EmissionStateSnapshotCoverageTests`, and the
    engine-rebuild test is the one direct gap-#1 pin. The docs now state this explicitly rather than
    over-claiming (the fix is honesty, not a stronger driver assertion the fixture cannot support).
  - Codex/Grok Low (journal round-trip compared only `EmittedMemberCount`) — **FIXED**: whole-record
    `Assert.Equal(before, restored)` over three stamped facts, genuine-red verified.
  - Grok L1 (non-empty driver test lacked local non-vacuity) — **FIXED** (`clean != first` guard).
  - Grok L3 (multi-field journal restore omits list-valued `EmittedClassMethods`) — residual Low, the
    restore path writes the full pre-image so the channel is covered; adding the list field is nice-to-have.
  - Codex/Grok Low (summary stale counts + placeholders + "five" vs six rows) — **FIXED** by this rewrite.
  - No High and no fixed-High ⇒ the re-review-round gate (one verifying pass per fixed High) is not
    triggered.

## Backlog carried to wave-2

All items are trigger-gated in `src/docs/not-planned.md` §"Wrapper verify-recover loop — wave-2 &
deferred" (six rows: RecoveryGraph, ABI-as-input, strip-as-iteration-0, consume-converged-outcome,
loop-path-parity, BindingTests-resilience-fixture). Session 10 (wave-1 closeout + wave-2 planning) owns
sequencing them; the wave-2 dependency that unblocks the most (item 1, device/all parity, no-recompile)
is **emitted-dependency-completeness capture** — build that first.
