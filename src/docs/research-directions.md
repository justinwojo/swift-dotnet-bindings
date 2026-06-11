# Research Directions — Post-0.14, Toward 1.0

Status: **open discussion**, not committed work. Captured 2026-06-10 after shipping 0.14.

This doc holds the strategic options for what to work toward next — major themes, not
one-off roadmap items. The goal is to decide what actually buys a *confident* 1.0, given
that "no reason to rush, ship it when it's solid" is the standing constraint. Each section
below is a thread to develop further; treat them as living until we promote one into a real
plan.

---

## The diagnosis: the roadmap is a convergence signal

Before picking work, read what `roadmap.md` is actually telling us. Nearly every item in
Medium / Low / Latent is some variant of:

- *"no active repro"*
- *"not triggered by any current validation library"*
- *"trigger to revisit when a real consumer surfaces it"*
- *"verified-present mechanism with zero emission site in the current surface"* (the entire
  Latent section — and it explicitly says none are queued, because none have a reaching shape)

What's left is overwhelmingly (a) blocked upstream (exactly 4 confirmed .NET bugs),
(b) by-design out-of-scope (result builders, PAT erasure, source-gen territory), or
(c) latent code with **no input in our corpus that reaches it**.

That is not a backlog. It's convergence. We've done the reactive bug-chase (the ~100-defect
workflow) *and* the proactive audit (2026-06). The conclusion:

> **More bug-chasing of the same kind has sharply diminishing returns.** We are not
> bug-poor; we are *input-poor*. The marginal remaining bug lives behind input shapes our
> test corpus doesn't contain. Re-scanning the same validation libraries rediscovers the
> same latents that already have "no emission site."

This reframes the 1.0 question. It is **not** "are there more bugs to find." It is:

> **"How would we even know we're solid?"** Today the answer is vibes + absence-of-new-red.
> That — not an open defect list — is the real blocker to 1.0, and it's fixable.

Everything below is organized around that reframing.

---

## Direction 1 — Generative ABI-conformance matrix (flagship)

**Thesis.** Stop waiting for real libraries to surface inputs; *manufacture* the inputs
systematically, and produce a coverage map that makes "is it solid?" a measurable question
instead of a vibe.

**Shape.** A harness that:

1. Enumerates the Swift ABI feature space as an explicit grid. Rough axes:
   - layout: `{ frozen, resilient }`
   - kind: `{ struct, class, enum (no payload), enum (payload), actor }`
   - generics: `{ none, 1 param, 2+ params, parameter pack, constrained / where }`
   - closures: `{ none, param, return, escaping, @autoclosure, async, throwing }`
   - effects: `{ sync, async, throws, async throws }`
   - protocol: `{ none, plain, PAT, composition, Self-requirement, reverse-dispatch }`
   - transport edges: optional, tuple return, inout, indirect/`@out`, existential box
2. Synthesizes a minimal Swift library per cell (and per *combination* we've never
   hand-written a fixture for — the combinations are where the latents live).
3. Binds → compiles → runs on **sim (Mono JIT) + device (NativeAOT)**, round-tripping a
   representative value through each generated member.
4. Emits a **coverage grid**: "N of M cells green; here are the M−N cells we have never
   exercised, and here is which ones miscompile / crash / are correctly out-of-scope."

**Why this is the flagship.** It is the only direction that does *both* jobs at once: it
finds new bugs (in the untested combinatorial interior) *and* produces the durable artifact
that justifies a 1.0 go/no-go. It's the same energy as the workflow bug-chase, aimed at
unknown-unknowns instead of known libraries. The artifact + fixtures land in BindingTests —
exactly where our own policy says to invest (`feedback_bindingtests_durable_gate`,
`project_internal_binding_testing_temporary`), and it is concrete progress toward the
standing goal of retiring `nuke validate` as a gate.

### Measured coverage of the existing end-to-end corpus (2026-06-10)

Before committing, we characterized what the two existing runtime-test repos actually
exercise — `swift-dotnet-packages` (~24 test apps: Apple frameworks + Lottie/Kingfisher/
Stripe/Nuke/BlinkID/Mappedin) and `internal-binding-testing` (~20 apps over distinct
third-party libs: Alamofire/GRDB/RxSwift/Kingfisher/CryptoSwift/…). No library overlap
between the two. **Both run on sim (Mono JIT) and device (NativeAOT), with real
value-round-trip assertions** (not compile-only). This is a genuinely strong base — the
1.0 question is not "is there end-to-end coverage" but "what *shape* is it."

The measured shape:

| Feature | Corpus coverage | Appears in roadmap Latent list? |
|---|---|---|
| Enums (no-payload / payload / raw-value), optionals, structs (breadth) | **Heavy** | No |
| Plain protocols, forward existential boxing | Moderate | No |
| Reverse-dispatch (C# implements Swift protocol) | One shape (GDPerformanceView delegate, incl. nested-tuple callback) | F8 / F9 / F10, inout-bridgeable |
| Async → `Task<T>` | Moderate but shallow — mostly non-null checks; no `AsyncSequence` iteration | Async fan-out, `CreateAsync` parity |
| Throwing → C# exceptions | Happens pervasively, but exception type/message rarely asserted | Disposable failure carrier |
| Generics | Single-param container projection only (`Forecast<T>`, `HMAC<SHA256>`). **No** where-clauses, **no** parameter packs, **no** multi-param round-trip from C# | CS0305 arity, the ~6 CSM filter latents |
| **Closures** | A *little* — `Bool→Void`, nullable. escaping / async / throwing / closure-return = **none**; several closure ctors explicitly skipped | Closure/async fan-out, §2.1 escaping dead-code |
| **Tuples** | One single-element return + one nested-tuple callback | Mixed-indirect generic tuple returns |
| **inout** | Essentially **none** (one `NSError**` out-param) | inout ObjC-bridgeable, inout blittable round-trip |
| **Actors, PATs, protocol composition** | **None** | — |

**The load-bearing finding: the thin corners of the corpus and the Latent list are the same
corners — and that is causal, not coincidental.** The corpus is a *curated sample biased
toward what real libraries commonly do*, and real libraries are dominated by
enums/structs/optionals/simple-async. So coverage is thick exactly where real libraries are
thick and thin exactly where they're thin: closures, tuples, inout, constrained generics,
actors. The latents survive for the *same reason* — no test reaches them because no common
library uses that shape — yet the generator still **emits** code for all of it. The precise
1.0 risk is therefore: a consumer writes an escaping async closure / an inout struct / a
multi-element generic tuple return, the generator emits a binding, the consumer calls it,
and **no test in either repo has ever run that shape end-to-end on either runtime.**

This is why the matrix is *not* a reskin of the coverage audits (which are saturated on the
emission/skip axis). It is the only thing that systematically populates the thin corners the
real-library sample *structurally cannot reach*, and runs them on both runtimes with a value
oracle. An **all-green** cell is as valuable as a red one: it converts a Latent from "present
mechanism, never reached, unknown" into "exercised on sim+device, confirmed safe" — which is
exactly the 1.0-confidence currency we don't have today.

### Bounded first deliverable (proposed) — the "thin-corner" slice

Do **not** build the full grid first. Build the corner with the densest overlap between
"corpus is thin" and "Latent list is dense": **closures × {escaping, async, throwing,
closure-return} and inout × {blittable struct, ObjC-bridgeable, generic}, plus
multi-element / nested tuple returns under effects.** Concretely:

1. A small set of templated Swift fixtures spanning that corner (hand-authored axis
   templates, combinatorially expanded — deterministic, reviewable, no RNG).
2. Bind → compile → run on **sim + device**, round-tripping a known value through each.
3. Output a green / red / by-design-gray grid for that corner.

Outcome is meaningful either way: reds are real pre-1.0 bugs in code users can already reach;
greens retire a cluster of latents from "unknown" to "confirmed." If the slice proves the
harness + report end-to-end and the corner is dirty, widen to the next-thinnest corner
(generics: constrained / multi-param / packs). If it comes back clean, that itself is a
strong 1.0 signal and we stop cheaply.

**Open questions to resolve before building:**

- **Generation strategy.** Hand-authored axis templates with combinatorial expansion, vs.
  a true generative/property-based generator. Start with templated expansion (deterministic,
  reviewable, no `Math.random`); revisit fuzzing later.
- **Combinatorial explosion.** The full cross-product is enormous. Need a *covering-array /
  pairwise* strategy (every pair of axis values appears together at least once) rather than
  full cartesian, plus a hand-picked set of known-nasty triples (e.g. resilient × async ×
  optional-tuple-return).
- **Oracle.** What counts as "green"? Compile is cheap; runtime round-trip needs a value
  oracle per leaf type. Probably: emit a Swift function that returns a known value, assert C#
  reads it back equal. Crashes and miscompiles are the signal.
- **Out-of-scope classification.** Many cells are *correctly* unsupported (PAT erasure,
  result builders). The grid must distinguish "green / red / by-design-gray" so reds are real.
  This list already exists in roadmap "Not Worth Addressing" + "Explicitly Out of Scope" —
  reuse it as the gray mask.
- **Relationship to SurfaceArea corpus.** `BindingTests/Sources/SurfaceArea/` already exists
  as the validate-retirement scaffolding. Is the matrix an *extension* of SurfaceArea or a
  sibling? Likely extension.
- **First slice.** Don't boil the ocean. Pick ONE axis (candidate: closures, since
  `UnsupportedClosure` is the largest live skip bucket and async-closure shapes are explicitly
  "remaining"), grid it fully against the other axes held at defaults, and prove the
  harness + coverage-report end to end before widening.

---

## Direction 2 — AI-maintainability refactors (standing tax)

**Thesis.** 100% of edits to this codebase are AI-authored (non-coding owner). Under that
constraint, **navigability is correctness insurance** — a fresh-context agent cannot safely
modify a subsystem it can't hold in one context. This is a different justification than the
roadmap's bug-prevention cost/benefit, and a stronger one for *this* project.

**Candidates (the high-tax concentrations):**

- **Parallel async-emitter paths.** `WrapperEmitter.Async.cs` (2,781 LOC) +
  `AsyncMethodGenericBridgeEmitter.cs` (1,314) + `AsyncHarnessEmitter.cs` (1,703) — three
  diverged emitters, ~5,800 LOC, already the source of multiple divergence bugs. Roadmap
  defers this on bug-risk grounds; the maintainability lens flips the calculus.
- **Capability-typed projection model.** Per-emitter property checks (`IsFrozen`,
  `IsObjCBridged`, …) scattered across ~150 files. A `TypeCapabilities` record populated once
  in TypeDatabase. Roadmap deferred it (trigger: "3+ bugs sharing the 'two emitters chose
  differently' shape"); maintainability is a parallel argument independent of that counter.
- **CSM filter machinery.** The Concrete-Specialization-Engine filters have accreted ~6
  distinct latent bugs (composition `P & Q`, sugar canonicalization, dependent-member
  clauses, arity, SameType, where-clause) — each hand-rolled. A unified constraint-evaluation
  pass would collapse the whole family.

**Open questions:**

- This is **refactoring working code** — highest-risk direction. Needs the generative matrix
  (Direction 1) or at least a strong BindingTests net *first*, as the regression backstop.
  Arguably Direction 2 should *follow* Direction 1 for exactly this reason.
- Scope discipline: one subsystem at a time, behavior-preserving, gated on
  `nuke binding-tests --device` (all three candidates touch marshalling / calling conventions).
- Beware the `feedback_no_session_cascade` failure mode: audit the true number of divergent
  emission mechanisms *up front* before committing to a consolidation, not as discovery unfolds.

---

## Direction 3 — Hold + amplify reach (cheap, compounding)

**Thesis.** The roadmap's own verdict ("revisit when a real repro surfaces") says our
highest-value *remaining* bug source is real consumers, not internal scanning. So lower the
barrier to getting new real inputs, and widen the funnel.

**Candidates:**

- A **"paste your xcframework, get a gap report"** tool — runs the generator, classifies what
  bound / what skipped / why, hands back a readable report. Doubles as a lead-gen and a bug
  intake. (Synergy: it's a thin shell over Direction 1's classification logic.)
- Better issue templates that capture the xcframework / ABI JSON shape automatically.
- A sample/recipe gallery (the ActivityKit widget recipes in memory are a seed).
- Continued reach work. 77 stars / 15k+ downloads on a niche this narrow is healthy; each
  *new* real library thrown at the generator is worth more to 1.0 confidence than another
  internal sweep.

**Open questions:**

- How much of this is engineering vs. content/marketing? The tool is engineering; the gallery
  and reach are content.
- Privacy/trust: a hosted "paste your binary" tool has implications; a local CLI that produces
  the same report sidesteps them.

---

## Direction 0 — Map the ABI gaps first (cheap precursor, informs #1)

Before committing to the full matrix build, one bounded pass: **catalogue which ABI feature
cells our current BindingTests + validation corpus actually exercise.** Output is the
coverage hole as it stands today — the M−N before we've built anything. This de-risks
Direction 1 (we learn whether the hole is big enough to justify the harness) and is itself a
useful artifact. Low cost, high information.

---

## Recommendation (for discussion)

- **Direction 1 is the throughline to 1.0** — the only option that produces the "is it solid?"
  answer we don't currently have. Likely start with **Direction 0** as its scoping pass, then
  a single-axis vertical slice.
- **Direction 3 runs in parallel** — cheap, compounding, and its tooling shares logic with #1.
- **Direction 2 is a funded, bounded track that should *follow* #1**, because it needs #1's
  matrix as its regression backstop before we touch 5,800 LOC of working async emitters.

Next step: pick the thread and go deep. This doc is the shared context for that discussion.
