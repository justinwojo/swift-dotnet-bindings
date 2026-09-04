# Binding resilience design — always emit a sound, compiling, usable binding

**Status:** as-built — waves 1–2 complete; §8 is the outcome record, including where the landed
implementation deliberately drifted from this plan. Product goal: a user throws an arbitrary
`.xcframework` at the generator and *always* gets a clean, compiling, usable binding containing
everything bindable, with everything else tombstoned and reported — never a whole-binding failure
from one localized construct we didn't anticipate. Hard constraint: a degraded binding must be
**sound** (never compile-but-ABI-wrong, never crash at runtime). Dropping a member is fine;
leaving a type ABI-corrupt is not.

This synthesizes two independent design consultations (Fable + GPT-5.6 "Sol", each with full
repo access), which converged on the same primitives. Where they agree it is stated as consensus;
where one went further it is attributed.

---

## The one-sentence conclusion

The proposed spine's **diagnosis** is right — every residual whole-binding failure comes from
*predicting* what we can't handle without *verifying* the artifact we produced. But its **recovery
primitive is wrong.** Post-hoc text surgery on emitted artifacts (`SwiftWrapperPostProcessor` +
`StrippedSymbolCSharpReconciler`) **cannot be made sound** in this codebase, because member
existence feeds *upstream* emission decisions — collision-suffix naming, projected-key dedup,
vtable slot layout, conformance fillability, adopted-name reservation. Deleting text after the
fact leaves all of those stale.

The sound primitive is **regenerate-from-plan with a disabled-unit set**: attribute a failure to a
logical declaration, add it to a denylist, and *re-run emission* so the tombstone flows through the
**existing skip machinery** (`MemberValidationPipeline`, `TypeSkipConditions`,
`UnsupportedCommentEmitter`, `ReportCollector`, `VtableLayout` fillability), which already knows how
to produce a consistent binding around an absent member. **No recovery operation ever edits the
retained ABI model directly — it disables a declared capability, then the generator recomputes
every artifact from the settled plan.**

The target architecture is the spine's *shape* with the primitive swapped:

```
parse → immutable TypeDatabase (already frozen)
  ┌───────────────────────────────────────────────┐
  │ RENDER  (from plan, with disabled-unit set D)  │
  │   gates select typed lowering plans            │──emitter throws──► poison unit; re-render
  │ VERIFY                                         │
  │   swiftc (already runs)     →  attribute       │──errors──► D += needs-closure(culprit)
  │   Roslyn in-process probe   →  attribute       │──errors──► D += needs-closure(culprit)
  │   typed ABI / symbol / layout gates            │──violation─► D += culprit
  │ RECOVER: escalate per soundness ladder         │──no progress──► escalate granularity
  └───────────────────────────────────────────────┘
      clean → publish binding + degradation report (per unit: DeclId, reason, owner, workaround)
```

---

## 1. "Member" is the wrong recovery unit — the escalation ladder

A method is an emission convenience, not a soundness boundary. Recovery granularity must equal
**layout/capability ownership** granularity. Both models proposed the same lattice:

| Unit | Examples | Drop in isolation? |
|---|---|---|
| **Leaf API** | method, ctor, operator, free function, property/subscript *as a unit* | **Yes** (native always; managed subject to conformance obligations) |
| **Accessor group** | getter+setter of one property | Yes (but never the backing storage cell) |
| **Type representation** | frozen-struct field, enum payload, tuple/optional layout, by-value size/register class | **No — never.** Escalate to the type that owns the layout |
| **Type surface** | class/struct with broken type infrastructure (retain/release, metadata, boxing) | Escalate; may survive as `[OpaqueSwiftType]` shell iff every retained use is sound |
| **Forward protocol view** | C# consuming a Swift value that conforms to `P` | A missing requirement → omit that member from the view |
| **Managed reverse conformance** | C# object wrapped in a generated Swift carrier + witness table | **All-or-nothing.** A missing witness → disable `ManagedConformance<P>` entirely |
| **Conformance edge** | a generated `: IFoo` relation | Remove the edge, propagate to APIs requiring it |
| **Shared helper bundle** | UTF-8 helpers, error registries, EveryProtocol carriers, closure-context helpers | Only with their full owner-closure; escalate if mandatory |
| **Module** | whole binding | Last resort only — the floor, not the default |

Every artifact declares an **escalation parent**. Recovery starts at the smallest attributable
scope and walks up until every remaining obligation is closed.

### The protocol crux (both models nailed this identically)

Split the two protocol capabilities that today may be conflated into one emitted `IFoo`:

1. **Forward view** — C# receives a Swift value that *already* conforms to `P` and calls
   requirements through Swift's real witness table. A missing requirement can be safely omitted;
   the native conformance is still valid, C# just exposes a subset.
2. **Reverse conformance** — a C# implementation is wrapped in a generated Swift carrier with an
   installed witness table/vtable, and Swift calls back into C#. Swift conformance is
   **all-or-nothing**: every required witness and every positional vtable slot must be valid. A
   missing witness means you **disable the whole reverse capability** — you may *never* leave a
   null or trapping witness (a deliberate trap still violates "never crash at runtime").

Vtable slots are **layout, not members** (`VtableLayout.cs` already models pre-skipped /
skip-but-consume / included). Recovery must never physically delete a protocol member from the
layout model — it removes it from the *forward interface*, leaves a reserved index when the ABI
says the index is consumed, and disables the reverse capability so the empty slot is never
installed or called.

---

## 2. The soundness model: "safe to drop" is statically decidable

A removal is safe **iff** it does not alter any *retained* ABI footprint **and** leaves no
*retained* capability with an unsatisfied obligation. Computed statically over the recovery graph
(the plan), never guessed after a compiler error.

Each planned surface declares (a) its **ABI footprint** — representation, slot, symbol, metadata,
ownership state it contributes — and (b) its **consumer capability** — what the binding promises.
"Drop X" means dropping X's entire `needs`-closure (public member + P/Invoke(s) + wrapper symbol +
callbacks + default/narrowing overloads + exclusive thunks + report rows) as one bundle.

You do **not** prove the strip logic correct; you **fail closed on independent invariant gates**
after every recovery iteration and treat a gate failure as "escalate one rung," never "ship":

1. `WrapperSymbolIntegrityGate` — no dangling P/Invoke (exists, already fail-closed).
2. Silent-tombstone divergence invariant (exists, throws).
3. `AbiContractChecker` — **promoted from warn-only to blocking** (see Stage 0).
4. Vtable width/field parity on the recovered artifact set.
5. **New layout-hash gate** — for every emitted by-value struct, recompute expected
   size/offsets from ABI-JSON facts and assert the emitted layout matches. Converts "we believe
   stripping never touched a layout" into a checked invariant.

### The prediction/verification division of labor — the principled line

Compiler success **cannot prove ABI correctness.** swiftc and Roslyn are syntax/type-system/linkage
oracles; neither proves the two sides agree on calling convention, register class, ownership, field
offsets, or witness-table width. Therefore:

- **Freeze growth** of gates whose only job is predicting a *compile error* — the verify-recover
  loop is their general backstop; keep existing ones as fast-path optimizations.
- **Keep hand-writing** gates for *soundness* conditions (ABI mismatch, indeterminate layout,
  register-convention violations). No compiler backstop can replace them.
- **Criterion (Fable):** a new prediction gate is justified iff the failure it prevents would
  *compile*. If the compiler would catch it, let the loop handle it.

---

## 3. The three soundness holes (Stage 0) — *were* live; closed in wave 1

Both models independently flagged the **same three** places where the generator could ship a
compile-successful binding that was unsound. At design time these were live bugs, largely
independent of the larger architecture, and the highest immediate risk-reduction per line.
**Wave 1 closed all three** (see §8 outcome record). The historical problem statements and the
landed sites:

**H1 — `AbiContractChecker` result was discarded.** Pre-wave-1, `ModuleEmitter` called the
validator and dropped the return value; it detected CC-001..004 (non-blittable `CallConvSwift`
params/returns, wrapper targeting the wrong library, Cdecl-on-mangled-symbol) with structured
records including a ready-made `EntryPoint` attribution key. **Landed:** `ModuleEmitter` runs
`AbiContractChecker.ValidateModule(...)` and throws `AbiContractViolationException` when the
result is unclean — violations fail closed before files are written. *Caveat retained: treat the
checker as a blocking linter, not a soundness proof; typed-plan validation is Stage 5 / wave 2.*

**H2 — SDK-mode shipped a binding whose wrapper surface crashed.** Pre-wave-1, wrapper-compile
failure could be downgraded to a Warning in SDK mode, so wrapper-backed methods hit
`DllNotFoundException` at runtime — the compile-clean/runtime-broken outcome the constraint
forbids. **Landed:** `SwiftWrapperCompiler.EvaluateResult` returns `Fatal` whenever
`XCFrameworkPath` is empty, in every mode (no SDK-mode soft-downgrade).

**H3 — the reconciler kept a dead P/Invoke to satisfy an interface.** Pre-wave-1,
`StrippedSymbolCSharpReconciler` exempted a P/Invoke whose public caller implemented an
interface member (`FindExemptedPInvokes`) to dodge CS0535 — but the wrapper symbol was stripped,
so the call threw `EntryPointNotFoundException` at first use. **Landed:** exempted / interface-
protected members are rewritten to loud throwing stubs
(`throw new SwiftBindingUnavailableException(...)`) rather than preserved as dead native calls.

> **Tension (design-time, accepted under D-R2):** closing H1–H3 would make some corpus libraries
> that reported "green" turn red — because those greens were *false* (compile-clean,
> runtime-broken). That honest green-drop was accepted; wave-1/2 corpus movement is recorded in
> §8.

Also in Stage 0 (and landed with the wave): a **double-emit byte-identity determinism test**
(the regenerate loop's foundational assumption), and checkpoint/rollback support so a rolled-back
C# member cannot orphan its wrapper block.

---

## 4. Attribution: symbol-anchored provenance, not line maps

The line-drift problem is self-inflicted by a *stored span map* — spans are invalidated before the
file is even written (`QualifyNamespaceReferences` regex-rewrites the whole output; the
file-per-type splitter reslices it). Don't store positions. **Recompute the interval map from
immutable fragments on every render**, so a diagnostic is always interpreted against the map for
the *exact source version that produced it* — drift becomes definitionally impossible.

- **DeclId / ArtifactId** — the one piece of new foundational infrastructure. A stable, serializable
  identity per logical decl (module-qualified path + kind + label-inclusive signature hash / mangled
  name / USR). It is the denylist key, the report key, and the provenance value. Synthesized
  artifacts get `DeclId/csharp-public`, `DeclId/pinvoke`, `DeclId/swift-wrapper`, `DeclId/callback-N`,
  `ProtocolId/reverse-vtable`, etc. Captures fan-out (one decl → many artifacts) and fan-in (one
  shared helper → many owners) that a line map cannot.
- **Wrapper side** — every strippable block carries a `@_cdecl("SBW_…")` / `@_silgen_name("SBSW_…")`
  symbol (already regex-extracted in three places) or a `// SBW-ORIGIN: <DeclId>` anchor comment.
  Use `-serialize-diagnostics` for structured attribution (the current stderr scrape is
  preview-grade); map linker errors by *symbol* to artifact ownership; classify missing-module
  diagnostics as `InputConfiguration`, not last-source-line.
- **C# side** — in-process Roslyn probe (generator already references
  `Microsoft.CodeAnalysis.CSharp`): diagnostic `SourceSpan` → `AncestorsAndSelf<MemberDeclarationSyntax>`
  → the member's `EntryPoint` literal / `// SBW-ORIGIN` trivia → DeclId. No emission-time bookkeeping.
- **Cascade hygiene** — attribute only *primary* diagnostics; batch all culprits from one compile into
  one denylist increment; detect no-progress (identical error fingerprint two rounds, or a round
  attributing zero diagnostics) and escalate granularity instead of iterating; hard iteration cap.
- **Bisection is a bounded fallback only** — for diagnostics that attribute to nothing (shared
  prelude, linker errors with no location, compiler crash). Bisect over **decl-exclusion sets with
  regeneration**, never over text halves. Justification: symbol attribution usually resolves all
  culprits from one failing compile (~2-3 compiles total), vs O(log n) *per culprit* for bisection,
  where each probe is a full swiftc slice.

---

## 5. Exception containment: poison-and-regenerate, not in-place rollback

In-place per-member rollback is **not feasible** and should not be attempted. `SwiftWriter` has no
checkpoint (empty `IndentedTextWriter` subclass); `ModuleEmissionContext` carries ~100+ mutable
registries (wrapper-symbol sets, dedup reservations, conformance decisions, thunk builders);
`ReportCollector` is ambient AsyncLocal state; `TypeDatabase.ApplyEmissionResult` mutates post-freeze.
The existing `WrapperSymbolContractGate` rollback works *only* because its exception is thrown
eagerly before registrations commit and only the C# buffer is dirty — that precondition doesn't
generalize.

**The feasible mechanism (both models):**

1. Wrap per-member dispatch in a catch that records `(DeclId, exception)` in a **poison list**,
   writes nothing, and continues.
2. At end of module: if the poison list is non-empty, **discard the entire attempt's output and
   re-run emission once** with the poison list as **Gate 0** of `MemberValidationPipeline` /
   `TypeSkipConditions` (a `Skip(SkipReason.EmitterFault, …)` carrying the captured exception) — a
   fresh `ModuleEmissionContext`, fresh `ReportCollector` session, fresh writers, emission-result
   overlay. Never continue from tainted shared state.
3. Type-level exceptions poison the type (opaque-shell demotion on retry). Cap at ~3 attempts, then
   module failure.

This is cheap because parse + TypeDatabase construction dominate cold cost and are unaffected
(frozen registry); emission is a pure function of `(frozen DB, decl tree, denylist)`, so re-emission
is seconds. It **structurally contains** the `SwiftTypeName.FromModuleQualifiedName` throw-class
(30+ throwing sites vs ~2 `Try*` sites; a `τ_0_0.…` name reaching any of them is today a module
abort via `Program.cs:751`) — migrating hot sites to `TryFromModuleQualifiedName` stays worthwhile
as fast-path hygiene, but the poison loop is the structural guarantee for the *next* unanticipated
throw-site. Longer term, migrate hot leaf emitters to real fragment-local transactions
(transaction-local overlay + side-effect journal + reservation semantics for shared claims) for
performance; the clean-attempt restart is the correct *first* implementation.

---

## 6. Is there a fundamentally better architecture? (verdict: yes — the plan-carrying shape)

Both models rejected the brief's three alternatives:

- **Emit-everything-then-tree-shake** — strictly worse; maximizes reliance on sound post-hoc
  removal (the thing that's only achievable via regeneration anyway) and abandons the gate catalog's
  soundness knowledge. The ~2.7k-line reconciler (`StrippedSymbolCSharpReconciler`) is the evidence
  of how fast text tree-shaking becomes a second fragile compiler.
- **Probe-build / maximal-compiling-subset (ddmin)** — the compiler-as-only-oracle fallacy.
  "Maximal *compiling* subset" ≠ "maximal *sound* subset," and soundness is invisible to the
  compilers. Combinatorial, nonmonotonic, not unique. Valuable only as fallback attribution.
- **Compiler-as-oracle from the start** — right for the C# *compilability* check (always-on Roslyn
  probe), wrong as *the* architecture; the compiler is one oracle among several, blind to ABI.

**The answer is the spine's shape with a proof-carrying plan.** Prediction gates stop being an
ever-growing set of unrelated booleans and become **typed lowering-plan selection**:
`DirectSwiftCallPlan | CdeclWrapperPlan | NativeThunkPlan | OpaqueHandlePlan |
ForwardProtocolViewPlan | ManagedConformancePlan | UnsupportedPlan`. A plan constructor succeeds
only when it has the facts its ABI contract needs; missing facts → an explicit non-emitting plan.
`MemberValidationPipeline` evolves into this selector; `VtableLayout` is already the model example
(one typed object owns membership/index/width, both sides render from it). The compiler recovery
loop then handles only the unmodeled tail without owning ABI semantics.

**Honest product contract (GPT-5.6's framing).** "Always return a binding" is achievable for
*localized declaration* failures via conservative escalation. It is **not** achievable — without
lying or emitting an empty artifact — for corrupt input, missing mandatory dependencies,
unsupported toolchains, resource exhaustion, or an invalid module root. State it as:

> Every localized failure produces a sound degraded binding. A successful generation never contains
> an unverified or known-unsafe surface. Non-local input/environment failures remain explicit
> failures (with actionable, owner-classified diagnostics).

**Degradation report** gains orthogonal **cause ownership** (`InputConfiguration` / `LibraryAuthor` /
`Generator` / `SwiftToolchain` / `DotNetToolchain` / `Environment` / `Unknown`), a `RecoveryStage`, and
**root-vs-cascade** structure — so one root failure that removes 40 dependent members reads as one
root + 40 cascade, not 40 unrelated bugs. An unanticipated compiler-attributed recovery defaults to
`Generator`/`Unknown`, never auto-becomes an accepted known limitation.

---

## 7. Staged implementation path

Ordered by risk-reduction-per-line. **Stage 0 is a self-contained bug-fix unit shippable on its own;
Stages 1-3 are "minimum viable resilience"; Stages 4-7 are the full proof-carrying end-state.**

- **Stage 0 — close live unsound "successes" (days).** H1 (act on `AbiContractChecker`), H2 (SDK-mode
  wrapper failure fails publication), H3 (reconciler exemption → throwing stub / fail-closed),
  double-emit determinism test, `Checkpoint` on `IndentedTextWriter`. *May temporarily increase hard
  failures — correct.*
- **Stage 1 — identity + capability graph (foundational).** `DeclId`/`ArtifactId`, `RecoveryUnit`/
  `RecoveryScope`, `Requires`/`Provides` graph, ABI-footprint classification, escalation parents,
  explicit forward-view vs reverse-conformance protocol capabilities, root/cascade + owner report
  fields. Wire existing prediction skips + reports through these same units. No compiler recovery yet.
- **Stage 2 — immutable fragments + clean-attempt restart.** Output becomes owned fragments
  (methods/ctors/props + their P/Invokes/wrappers/exclusive helpers first); per-render interval
  provenance; the poison-and-regenerate exception container (§5); untrusted-name boundary
  (`Try*` for all ABI-derived names). Eliminates abort-path #1.
- **Stage 3 — Swift wrapper verify loop (kills "Family A").** Structured swiftc stderr parsing
  (the `-serialize-diagnostics` `.dia` route sketched here pre-landing was dropped — see the
  wave-1 outcome record below);
  symbol/anchor attribution; needs-closure → denylist; re-render all promised slices from one settled
  source set; recompile until clean or no-progress; escalation ladder + iteration cap. Keep
  `SwiftWrapperPostProcessor` as a fast-path first iteration, recorded through the same recovery-unit
  machinery; retire pattern-specific compile-failure predictors once the loop has corpus coverage.
- **Stage 4 — C# verify loop (kills "Family B").** Always-on in-process Roslyn probe (or real MSBuild
  build + SARIF if project parity can't be matched); Roslyn-tree attribution; same loop; begin
  retiring `StrippedSymbolCSharpReconciler`.
- **Stage 5 — typed ABI contracts.** Replace `AbiContractChecker`'s text extraction with validation
  over a typed `AbiCallPlan` (lowered carriers, conventions, per-target symbol availability,
  size/align/register facts) vs matching wrapper descriptors. Text scanning stays as defense-in-depth;
  typed-vs-text disagreement becomes an invariant failure.
- **Stage 6 — settled publication.** Write attempts to staging; atomically promote only after swiftc
  (all slices) + C# + symbol-closure + ABI + layout/conformance invariants all pass; reports reflect
  the settled disabled set.
- **Stage 7 — fallback + optimization.** Dependency-aware delta-debug for unattributable crashes;
  verification caching by input/toolchain/plan fingerprint; migrate hot leaf emitters from
  whole-attempt restart to fragment transactions; freeze prediction-gate growth per the §2 criterion.

---

## 8. Workplan & decision record

This section makes this doc the **single umbrella** for the program: decisions, wave structure,
and status live here (tracked in git); the per-session implementation plans live in
`src/docs/sessions/` (gitignored, local-only, disposable). When a wave completes, its outcome is
recorded here — the session docs are not the record.

### Owner decisions (2026-07-18)

- **D-R1 — the 0.18.0 release is held.** No release pressure exists; the owner wants this entire
  program landed and verified before the next release ships. The contract-and-ship 06/07 sessions
  (device legs + release cut) are deferred until the program completes; they are unchanged, not
  cancelled.
- **D-R2 — honest green-drop accepted.** Stage 0 will turn some corpus "greens" red because those
  greens are false (compile-clean, runtime-broken). Baselines are ratcheted honestly in both
  directions with the reason recorded. No session may "fix" a red by weakening a gate.
- **D-R3 — two waves, separate session folders.** Wave 1 = Stages 0–3 (minimum viable resilience),
  planned in full in the wave-1 session folder (executed; session docs removed after closeout). Wave 2 = Stages 4–7,
  whose session docs are **authored by wave 1's closeout session** from landed reality (folder
  `…-wave2/`), because their real shape depends on what Stages 1–3 actually build. Each wave is one
  session-runner invocation.
- **D-R4 — program-start baselines** (post corpus-fixes close-out, commit `a3a3f79e`): unit tests
  **14,772/0**; BindingTests sim **3238 pass / 0 crash** (+6 known-environmental LiveActivity
  fails); corpus **39/120 green** (2026-07 sweep). `nuke validate` is **not** a program gate;
  the `internal-binding-testing` corpus-sweep harness is the sweep instrument.

### Owner decisions (2026-07-19, wave-2 scope review)

- **D-R5 — full wave 2 confirmed.** The owner pressure-tested wave 2's scope against wave 1's
  corpus movement (+3 green) and reaffirmed the product goal verbatim: anyone can toss a random
  third-party `.xcframework` at the tool and get a workable, compiling binding — skipping surface
  with clear language beats throwing errors. All eight wave-2 sessions run as authored; a proposed
  trim (drop 05–07) was considered and rejected. Time/token cost is not a constraint on this
  program.
- **D-R6 — OD-W2-2 resolved: ship degenerate bindings while any usable surface remains.** A module
  whose Swift wrapper surface strips to nothing (or that never had one) ships as an honest
  degenerate binding **as long as the settled binding still exposes usable public surface**; the
  report says exactly what was withdrawn. Fail closed **only** when the settled binding would
  expose no usable surface at all — and that error must say so in clear language ("nothing could
  be emitted; without it the binding is unusable, so this is an error"). The precise
  usable-surface predicate is delegated to session 06's judgment within that principle.
- **D-R7 — OD-W2-3 resolved in direction: ingestion follow-up program.** The residual red
  population expected after the soak (InputConfiguration causes, convert-stage failures) is not
  accepted as out of scope: after wave 2 closes, a follow-up ingestion-hardening program is
  planned — a stranger's random framework hits ingestion first. Session 08 routes the soak's cause
  tally as that program's seed evidence rather than presenting a disposition fork; program shape
  and roadmap placement remain owner calls at that point.

### Wave map

| Wave | Sessions | Stages (§7) | Highlights |
|---|---|---|---|
| 1 | `…-wave1/01–10` | 0, 1, 2, 3 | Determinism pin; H1/H2/H3 closed; DeclId + recovery units; immutable fragments; poison-and-regenerate; swiftc attribution + the wrapper verify-recover loop; closeout re-sweep + wave-2 planning (resilience fixture trigger fired at closeout → routed to wave 2) |
| 2 | `…-wave2/` (authored by wave-1 session 10) | 4, 5, 6, 7 | Roslyn probe loop + reconciler retirement; typed `AbiCallPlan` contracts; staged atomic publication; bounded bisection fallback, fragment transactions, prediction-gate freeze; final corpus soak + zero-whole-binding-failure ratchet |

### Status

- Wave 1: **done and verified** (run 2026-07-18/19, ten sessions). Gates at closeout: unit
  **15,214/0** (from 14,772), BindingTests sim **3,242/0/0** (37 skips, from 3,238), device
  **3,255/0/0** (verified on hardware), `--compile-only` exit 0, corpus **42/120 green** (from
  39/120).
- Wave 2: **done, sim-verified** (run 2026-07-19/20, eight sessions). Gates at closeout: unit
  **15,459/0** (from 15,214), BindingTests sim **3,242/0/0** (37 skips, unchanged), `--compile-only`
  exit 0 (the ResilienceKitchen ratchet gate is green), corpus **46/120 green** (from 42/120). The
  NativeAOT device leg is pending — see the wave-2 outcome record.
- **Program status: implementation complete, sim-verified.** All eight wave-2 sessions landed and the
  localized-construct ratchet holds across the full 120-lib soak (0 localized-escalated reds). The
  release hold (D-R1) can lift once the device leg runs: the deferred contract-and-ship 06/07 sessions
  (device legs + the 0.18.0 cut) resume unchanged. No further resilience machinery is planned; the
  residual red population is routed to the ingestion-hardening follow-up program (D-R7).

### Wave-1 outcome record (2026-07-19)

**Corpus movement, decomposed** (120-lib re-sweep vs the D-R4 39/120 baseline; four-way accounting):

| Bucket | Count | Libraries |
|---|---|---|
| Recovered (loop-attributable green) | 3 | CSV.swift, PromiseKit, ReSwift — generate→ok, each via SWIFTBIND112 withdrawal of 1 unit |
| Honest advancement (loop, still red) | 3 | CocoaMQTT (1 unit), Eureka (5), Hero (10) — withdrawals settle the Swift wrapper; now fail at the **C# compile** stage, i.e. exactly Family B, wave-2 Stage 4's target |
| Honest red (loop fail-closed, SWIFTBIND111) | 19 | generate_failed→generate_failed; cause tally across products: 12 InputConfiguration, 7 RequiresGraphClosure, 1 IterationCapExhausted, 1 NoProgress, 2 Unattributable |
| Degraded-green | 0 | no green binding carries a withdrawal |
| Regression | 0 | worsened bucket empty |

Environmental drift, excluded from the movement claim: 3 generate→compile flips with zero loop
evidence (Macaw/MessageKit/YPImagePicker — NU1101 missing dependency-binding packages in the harness
feed) and 8 convert-cache flips (convert_failed→no_primary_products/compile_failed). Withdrawal
report rows were spot-checked (CSV.swift, Hero): roots, cascades, owner, and details are honest; the
one mislabel found (withdrawal rows stamped `RecoveryStage: Emit`) was fixed in closeout —
withdrawal-origin `EmitterFault` rows now classify at `SwiftCompile`.

**Decisions that changed during execution** (the plan said one thing; landed reality is another):

- S01: determinism is pinned via an **epoch-gated Swift rollback**, not a plain double-emit compare.
- S03: all five ABI checker rules (one `AbiContractChecker` type) ship **blocking** — the planned warn-only intermediate step was
  skipped after the 91 CC-001 hits all proved false positives (checker retuned to managed-string
  carriers only; violations throw `AbiContractViolationException`, SWIFTBIND095).
- S05: the full recovery lattice (9 scopes), `RecoveryGraph`, and `RecoveryPolicy` were built, but
  the loop consumes only **LeafApi + AccessorGroup**; graph/policy have zero production callers —
  their activation is trigger-gated in `not-planned.md`, not silently live.
- S07: exception containment landed as a **snapshot/restore journal** (poison-and-regenerate,
  27 `EmissionSeam.Guard` sites, cap 3 → SWIFTBIND110), not the fragment-overlay design sketched
  in §5.
- S08: swiftc attribution parses **structured stderr**, not `.dia` serialized diagnostics — swiftc
  emits no `.dia` under `-emit-library` and .NET has no managed reader; fingerprint is an FNV-1a
  count-multiset.
- S09: the wrapper verify-recover loop runs as an **in-emission driver** (pristine re-render per
  iteration, cross-slice union of failing units, monotonic denylist, cap 4) wired to the in-process
  simulator wrapper-arch path only; SDK two-pass and device paths keep the fast path — the parity
  asymmetry is ratified in `not-planned.md`. Post-loop recompile of settled source stays the
  authoritative ship gate.
- S8b: the two persistent sim crashes were **root-caused to the Mono unwinder** (confirmed upstream,
  Issue 5 in the authoritative memory list) → 2 runtime-detected `[SkipOnMonoJit]` skips, device
  legs re-prove both under NativeAOT.
- S09b: an 8-probe search found **no natural emitted-but-broken wrapper** in the 20-lib proof
  corpus; loop mechanics are pinned by mechanism-gap tests instead
  (`EmissionFactsJournalTests`, `InEmissionDriverRestorationTests`). The first natural firings came
  from this closeout's 120-lib re-sweep (the 6 libs above).

**Leftover routing:** all wave-1 residuals live as trigger-gated rows in `not-planned.md`
(§"Wrapper verify-recover loop — wave-2 & deferred": RecoveryGraph completeness + Gate-0 actuators,
ABI-as-loop-input, strip-as-iteration-0, consume-converged-outcome + convergence-predicate
precision, loop path parity, BindingTests resilience fixture); near-term intent lives in the wave-2
session docs; roadmap carries policy only.

### Wave-2 outcome record (2026-07-20)

**Corpus movement, decomposed** (120-lib soak vs the wave-2-start 42/120 set — the wave-1 closeout
green set; four-way accounting):

| Bucket | Count | Libraries |
|---|---|---|
| Recovered (loop-attributable green) | 4 | FloatingPanel (2 accessor-group), Hero (3 accessor-group), OAuthSwift (1 accessor-group), Resolver (4 leaf-api) — **all** via the C#-plane verify-recover loop (session 03 / Stage 4), every withdrawal at the CSharpCompile stage, every root localized. Hero was wave-1's "honest advancement / Family B" waypoint (Swift wrapper settled, C#-stage red) — Stage 4 closed it, exactly as designed. |
| Honest red (module-scoped) | 59 | cause tally below; every terminal cause is intrinsically module-scoped — none is red on a localized construct (the ratchet) |
| Degraded-green | 7 | CSV.swift, PromiseKit, ReSwift (wave-1 recoveries, 1 withdrawal each) + the 4 recovered above — green bindings carrying honest localized withdrawal tombstones |
| Regression | 0 | worsened bucket empty (zero-tolerance) |

**Honest-red cause tally (59, all module-scoped originating cause):** 18 convert_failed (ingestion
could not produce a swiftinterface/ABI), 14 SWIFTBIND111 RequiresGraphClosure (coarse type/conformance
root), 9 SWIFTBIND111 InputConfiguration (missing module/toolchain input), 8 no_primary_products (no
public bindable surface), 5 SWIFTBIND109 (ObjC-mixed fail-closed on upstream packaging defects — a
missing/invalid public ObjC header), 4 escalation-symptom, and 1 cross-module metadata/conformance
resolution. The 4 escalation-symptom reds — 2 Unattributable / 1 NoProgress / 1 IterationCapExhausted
— each carry a terminal enum that *looks* localized but whose **originating** error is a module-level
ingestion failure the loop could not attribute to any unit: Amplitude-Swift = "missing required
modules 'AmplitudeCore', 'AnalyticsConnector'"; Euclid = cross-module "Metadata accessor not found"
across RealityFoundation / SIMD / Range; combine-schedulers + CasePathsCore = cross-module
conformance/type resolution ("should have been processed in a previous module"). Zero of the 74 reds
carries a synthesized-wrapper per-member compile error.

**Environmental drift, excluded from the movement claim:** 15 NU1101 compile_failed — missing
dependency-binding packages in the harness feed (Macaw, MessageKit, Moya, Needle, SwiftDraw, SwiftOTP,
YPImagePicker, analytics-swift, dd-sdk-ios, epoxy-ios, swift-argument-parser, swift-clocks,
swift-concurrency-extras, swift-dependencies, swift-identified-collections). These are harness-feed
artifacts, not generator results; none was green at wave-2-start, and regression=0 confirms no
start-green library regressed.

**The localized-construct ratchet — HOLDS.** A library may be red only for an intrinsically
module-scoped cause, never because one member/accessor/type defeated a compiler. The verdict keys on
the **originating** cause, not the terminal enum: any gated SWIFTBIND111 cause — an escalation
(NoProgress/IterationCapExhausted/Unattributable), a *dependency escalation* (RequiresGraphClosure),
or an input-config report (InputConfiguration) — is cleared as module-scoped only with positive
module-level ingestion evidence (a marker) AND zero synthesized-wrapper per-member errors; a
per-member error under it is a *proven* localized escalation, and a cause with neither signal is
*unresolved* — both fail the ratchet (fail-closed). Because a dependency escalation gets the **same**
originating-cause test as the escalation enums, "the escalation ladder relabeled it module-scoped" is
never an exemption. Across all 74 reds: **0 localized-escalated, 0 unclassified** (every gated red
carries a module-origin marker and zero per-member wrapper errors). Encoded durably in two places:
(1) the corpus-harness classifier (`corpus-sweep/scripts/ratchet_compare.py`, exit 2 on any
localized-escalated red, exit 3 on any unresolved red, exit 4 on an incomplete soak — the soak
instrument, local scaffolding); (2) the in-repo `nuke binding-tests --compile-only` ResilienceKitchen
gate (`build/Build.BindingTests.ResilienceKitchen.cs`), which asserts the loop engaged (SWIFTBIND112
present), never escalated (SWIFTBIND111 absent), and that **every** recovery-loop withdrawal in the
report — across all four planes (Swift-wrapper, C#, typed ABI validation, bounded bisection) —
resolved to a leaf-api / accessor-group scope.

**Decisions that changed during execution** (plan vs landed reality):

- **Stage 4 (C# verify loop) is the sole source of wave-2 recovery.** All 4 recovered libs moved green
  through the C#-plane joint fixed-point (10 localized withdrawals: 6 accessor-group + 4 leaf-api), all
  at the CSharpCompile stage. The loop still consumes only LeafApi + AccessorGroup; RecoveryGraph /
  RecoveryPolicy remain trigger-gated with no production callers, unchanged from wave 1.
- **Stage 5 (typed `AbiCallPlan`) unblocked no library.** Validating call plans against typed
  descriptors (demoting the text scan to a backstop) is a soundness / defense-in-depth layer, not a
  recovery expander — zero corpus libs moved green because of it. Typed-vs-text disagreement is an
  invariant failure, as designed.
- **Stage 7 (bounded bisection, SWIFTBIND117) fired on no soak library.** Every recovery came from
  direct attribution; no unattributable failure needed the dependency-aware bisection fallback.
  Verification caching stays opt-in (explicit root, package-mode only). Fragment-transaction migration
  measured NOT WARRANTED (mandatory recompile dominates) → routed to `not-planned.md`.

**Device leg — PENDING.** Wave 2's recovery machinery is generator-side emission that only engages on
hostile shapes; the sim BindingTests are unchanged (3,242/0/0) because the main test lib contains no
loop-triggering member (the ResilienceKitchen fixture exercises the loop in the compile gate). The
NativeAOT device re-prove of the wave-2 emission path is **owner-attended before the 0.18.0 cut and
recorded here** (not a separate not-planned tracker). The related not-planned row is
**"Loop path parity (SDK two-pass / device / all)"** — that row is about deferred *loop wiring* on
device/all paths, not this BindingTests device re-run.

**Leftover routing:** the soak cause tally above is packaged as the ingestion-hardening follow-up
program's seed evidence (D-R7 / OD-W2-3) — recorded here and pointer-linked from `not-planned.md`;
machine-readable per-library detail lives in the corpus-sweep harness
(`logs/wave2-ratchet-s8.json`, local scaffolding). Remaining wave-2 residuals stay as trigger-gated
rows in `not-planned.md`.

---

## Where the two consultations differed

Near-total convergence on primitives. Complementary emphasis:

- **Fable** — leaner, reuses existing machinery hardest; frames the near-term win as
  "closed-loop regeneration" and gets to a working resilience loop in ~5 stages.
- **GPT-5.6 (Sol)** — more ambitious end-state: full proof-carrying plan with an explicit 13-point
  publication proof-obligation checklist, typed `AbiCallPlan` replacing text extraction, staged
  atomic staging→promote publication, and the honest non-local-failure product contract. Also added
  **target-slice consistency** (a wrapper valid on sim but not device → remove the API *globally*,
  don't leave inconsistent native availability).
