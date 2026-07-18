# Binding resilience design — always emit a sound, compiling, usable binding

**Status:** design synthesis, not yet scheduled work. Product goal: a user throws an arbitrary
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

## 3. The three live soundness holes — fix these first (Stage 0)

Both models independently flagged the **same three** places where the generator ships a
compile-successful binding that is unsound *today*. These are bugs, largely independent of the
larger architecture, and are the highest immediate risk-reduction per line.

**H1 — `AbiContractChecker` result is discarded.** `ModuleEmitter.cs:131` calls `Validate(...)`
and drops the return value; it detects CC-001..004 (non-blittable `CallConvSwift` params/returns,
wrapper targeting the wrong library, Cdecl-on-mangled-symbol) with structured records including a
ready-made `EntryPoint` attribution key. Make violations actionable (at minimum, skip the
attributable member). *Caveat: its own comment claims ~83% precision and it assumes unknown custom
types are blittable — treat it as a blocking linter now, not a soundness proof; the real fix is
typed-plan validation, Stage 5.*

**H2 — SDK-mode ships a binding whose wrapper surface crashes.** `SwiftWrapperCompiler.cs:94-107`
(`EffectiveOutcome`) downgrades a **fatal** wrapper-compile failure to a Warning in SDK mode, with
the explicit comment that wrapper-backed methods get `DllNotFoundException` at runtime
(`Program.cs:2350`). That is exactly the compile-clean/runtime-broken outcome the constraint
forbids. Until recovery exists, wrapper failure must **fail publication.**

**H3 — the reconciler keeps a dead P/Invoke to satisfy an interface.** `StrippedSymbolCSharpReconciler`
Step A3 (`:101-110`, `FindExemptedPInvokes :548`) *exempts* a P/Invoke whose public caller
implements an interface member, to dodge CS0535 — but the wrapper symbol was just stripped, so the
call throws `EntryPointNotFoundException` at first use. Replace with a **loud throwing stub**
(`throw new SwiftBindingUnavailableException(...)`, the `PlatformNotSupportedException` pattern) or
fail-closed at the enclosing conformance. Never preserve a dead native call to satisfy a managed
interface.

> **Tension to surface to the owner:** fixing H1–H3 will make some corpus libraries that currently
> report "green" turn red — because those greens were *false* (compile-clean, runtime-broken).
> This is correct (the "no shortcuts / root-cause" policy), but it means the headline
> "39/120 green" number may *drop* before the recovery loop brings it back up soundly. That is a
> real, honest movement, not a regression.

Also in Stage 0: a **double-emit byte-identity determinism test** (the regenerate loop's
foundational assumption), and move `Checkpoint`/`RollbackTo` onto `IndentedTextWriter` so
`SwiftWriter` gets it (today a rolled-back C# member can orphan its wrapper block).

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
  soundness knowledge. The 2,437-line reconciler is the evidence of how fast text tree-shaking
  becomes a second fragile compiler.
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
- **Stage 3 — Swift wrapper verify loop (kills "Family A").** `-serialize-diagnostics` parsing;
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
  planned in full now in `src/docs/sessions/2026-07-binding-resilience-wave1/`. Wave 2 = Stages 4–7,
  whose session docs are **authored by wave 1's closeout session** from landed reality (folder
  `…-wave2/`), because their real shape depends on what Stages 1–3 actually build. Each wave is one
  session-runner invocation.
- **D-R4 — program-start baselines** (post corpus-fixes close-out, commit `a3a3f79e`): unit tests
  **14,772/0**; BindingTests sim **3238 pass / 0 crash** (+6 known-environmental LiveActivity
  fails); corpus **39/120 green** (2026-07 sweep). `nuke validate`'s baseline is stale by a routed
  decision (see `not-planned.md`) and is **not** a program gate; the corpus-sweep harness at
  `/Users/wojo/Dev/internal-binding-testing/corpus-sweep/` is the sweep instrument.

### Wave map

| Wave | Sessions | Stages (§7) | Highlights |
|---|---|---|---|
| 1 | `…-wave1/01–10` | 0, 1, 2, 3 | Determinism pin; H1/H2/H3 closed; DeclId + recovery units; immutable fragments; poison-and-regenerate; swiftc attribution + the wrapper verify-recover loop; resilience fixture gate; closeout re-sweep + wave-2 planning |
| 2 | `…-wave2/` (authored by wave-1 session 10) | 4, 5, 6, 7 | Roslyn probe loop + reconciler retirement; typed `AbiCallPlan` contracts; staged atomic publication; bounded bisection fallback, fragment transactions, prediction-gate freeze; final corpus soak + zero-whole-binding-failure ratchet |

### Status

- Wave 1: **planned** (docs written 2026-07-18, not yet run).
- Wave 2: not yet planned (by design — see D-R3).

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
