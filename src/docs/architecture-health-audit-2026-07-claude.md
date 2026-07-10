# Claude Third Opinion — Architecture, Ownership Model, and the Shape of 1.0

**Status**: Independent third review after 0.17.0, adjudicating the two prior reviews against repo evidence.

**Author**: Claude (Anthropic).

**Primary comparisons**:
[`architecture-health-audit-2026-07.md`](architecture-health-audit-2026-07.md) (Grok, "the audit") and
[`architecture-health-audit-2026-07-codex.md`](architecture-health-audit-2026-07-codex.md) (Codex, "the second opinion").

**Mode**: Review and strategy only. No implementation was performed. Every load-bearing factual claim
below was re-verified against the repository at review time (2026-07-09), not taken from either prior
document.

Evidence base independently re-read for this review:

- [`roadmap.md`](roadmap.md), [`Future/post-1.0-architecture-roadmap.md`](Future/post-1.0-architecture-roadmap.md)
- [`BindingAudit/_SUMMARY.md`](BindingAudit/_SUMMARY.md), [`next-release-remaining-work.md`](next-release-remaining-work.md)
- [`version-coexistence.md`](version-coexistence.md)
- `src/Swift.Runtime/src/Swift/Runtime/RuntimeContract.cs`, `src/Swift.Bindings/src/Emitter/RuntimeVersionRange.cs`
- `.github/workflows/ci.yml`, `.github/workflows/release.yml`
- `build/Build.Pack.cs`, `build/Build.PackGate.cs`, `build/Build.Validation.cs`,
  `build/Build.BindingTests.AppStoreHygiene.cs`
- `build/baselines/validation-baseline.json`, recent `main` history since the 0.17.0 window

---

## 1. Bottom line

Both prior reviews reach the right headline, and I confirm it independently:

> **Do not rewrite. Do not reopen broad internal archaeology. Ship a deliberately bounded,
> contract-first 1.0 — or, if the owner will not sign a major-only breaking-change promise for the
> Runtime/generated-binding contract, ship a maintenance 0.18 and stay 0.x honestly.**

Where the two reviews conflict, **Codex is right on almost every disputed point, and the repository
evidence is unambiguous** (§4). The Grok audit is an excellent map of the territory — its architecture
and maintainability analysis is careful and its "input-poor, not bug-poor" refinement is correct — but
its 1.0 checklist is scoped like a portfolio-perfection campaign, and its single combined "gates 8/10"
score hides the one operational fact that most threatens a trustworthy 1.0: **the release workflow
enforces far less than the gate catalog can prove** (§8, verified line-by-line).

Three things I add that neither review says plainly:

1. **The maintainability question is being scored against the wrong operating model.** Both reviews
   assess "can a human expert maintain this?" That is not this project. The owner is a non-coding
   product owner; all code is written and maintained by AI sessions steered by written constraints,
   tests, and gates. Under that model, `constraints.md`-as-load-bearing-prose and the enormous gate
   catalog are not a "second product tax" — they **are the maintainer interface**, and they are the
   reason six months of high-velocity change (~1,400 commits) landed with a zero-regression policy that
   mostly held. This does not raise the maintainability *score* — bus factor reshapes rather than
   disappears (§5.2) — but it re-ranks the investments: machine-checkable enforcement over
   simplification for human onboarding, and the rewrite case gets weaker still. (§5)
2. **Both reviews' work plans are already partially stale — in the good direction.** The A1/A2/A3
   trust/reporting items that Codex budgeted at 8–14 hours (rank 4) **have landed on `main`**:
   `3de82f4a` bridges ObjC skip diagnostics into the persisted binding report and closes the standard
   type-mapping holes (`NSOperatingSystemVersion`, `NSDataReadingOptions`, `NSUrlSessionTaskState`,
   `UIApplicationState` now present in `src/Swift.Bindings/src/Data/objc-type-mappings.json:172-173`),
   and `Program.cs:2471` routes error logging to stderr (`LogToStandardErrorThreshold = LogLevel.Error`),
   which was A3. The remaining pre-1.0 surface is smaller than either review believed. (§7)
3. **The 0.17.0 release itself is the best available case study for the gate-enforcement thesis.**
   The heavyweight, owner-invoked regression harness caught a real ship-blocking generator regression
   (RealityFoundation wrapper compile, `next-release-remaining-work.md` §0) that the entire enforced CI
   surface — strict compile-only + tier-2 sim — had passed. The gates work when they run. What ran was
   decided by memory and skill invocation, not by the release workflow. That is the precise failure mode
   to close before calling anything 1.0. (§8)

---

## 2. Scorecard (three-way)

| Dimension | Grok | Codex | **Claude** | Note |
|---|---:|---:|---:|---|
| Architecture fit | 8.5 | 8.0 | **8.0** | Right hybrid shape; discount for plan/emit interleave and script orchestration is warranted |
| Implementation maintainability | 6.5 | 6.0 | **6.5** | The AI operating model (§5.2) changes what to invest in, not the score: mega-files and hidden coupling consume AI context and raise model error just as they tax humans |
| Core Runtime/SDK/pack surface | 7.0 | 7.5 | **7.5** | The epoch/range/floor mechanism (`RuntimeContract.cs` + `RuntimeVersionRange.cs`) is genuinely well-designed for 0.x |
| Gate design | 8.0 (combined) | 8.5 | **8.5** | The catalog, fail-closed philosophy, and tri-state honesty (e.g. AppStoreHygiene "skip ≠ pass" logging) are excellent |
| Gate enforcement | 8.0 (combined) | 6.5 | **6.0** | Verified: release workflow runs neither PackGate, nor mixed-pack/direct, nor device, nor appstore-hygiene, nor validate; `Pack` merely orders after `PackGate` (`Build.Pack.cs:26`) |
| Core 1.0 readiness | 6.5 | 7.0 | **7.0** | Gated on one owner decision (the 1.x contract), not on engineering volume |
| Shipped-package portfolio trust | — | 6.0 | **6.0** | Three verified compile-green/runtime-dead shapes + one masked corruption skip + universal test-depth shallowness |
| Demonstrated demand / validation | — | 4.5 | **4.5** | Real users exist; evidence is far thinner than the engineering. The RC soak is the cheapest instrument to improve this number |

Grok's single combined gates score is the most consequential scoring error across both documents: it
averages a world-class gate *catalog* with a release *workflow* that would not notice if PackGate had
been red for a month. Those must be scored separately because they need different fixes (none vs. one
focused session).

---

## 3. Where the two reviews agree — and I confirm

All of the following are correct, evidence-checked, and should be treated as settled. Do not re-litigate
them next quarter:

1. **No rewrite, of anything, including reverse dispatch.** The moat is accumulated ABI truth
   (CallConvSwift vs cdecl, VWT/PWT, existential containers, vtable slot layout, Mono-vs-NativeAOT
   divergence) plus the test/gate/corpus infrastructure that proves it: 14,248 unit-test pass floor and
   3,160/3,161 sim/device runtime baselines (`build/baselines/validation-baseline.json`), a 66-library
   validation corpus, and a 52-cell ABI grid. A rewrite discards the evidence base and guarantees a long
   "green on sim, corrupt on device" window in exactly the subsystem (reverse dispatch) where
   superficially clean models fail silently. `Future/post-1.0-architecture-roadmap.md` already inventories
   the real debt with correct triggers; that inventory is the strongest possible rewrite argument, and it
   still fails its own litmus test.
2. **No more broad internal archaeology.** The roadmap's post-0.14 strategic posture ("input-poor, not
   bug-poor", `roadmap.md` §Strategic posture) is ~80% right as a prioritization rule, and Grok's 50%
   caveat is right too: the BindingAudit proved the remaining bug supply is *shipped-package product
   correctness*, not novel ABI shapes. Both refinements point the same direction: fix the known trust
   failures, then let external inputs drive.
3. **EveryProtocol silent-dead surfaces are the #1 trust problem.** Verified shapes per
   `BindingAudit/_SUMMARY.md`: RealityFoundation `ModelComponent.Materials` getter throws at runtime;
   RoomPlan `RoomCaptureViewDelegate` callbacks silently never fire; BlinkIDUX analyzer extension points
   are dead. Compile-green must imply runtime-alive or visibly-absent; nothing else on the list matters
   as much.
4. **Stripe `AppInfo` masked skip violates project policy** (no-expected-failures,
   corruption-is-ours-until-proven) and must be reproduced and classified — not expanded into a
   string-marshalling audit before the repro exists.
5. **1.0 means trust + contract, not Swift feature completeness.** Non-goals (result builders, full PAT,
   AppIntents authoring, SwiftUI tree composition) are already correctly documented in
   `roadmap.md` §Explicitly Out of Scope and must simply be published as stable.
6. **Keep PR CI lean; concentrate heavyweight proof at release.** The current `ci.yml` shape is right
   for solo velocity.

---

## 4. Where they conflict — adjudication with evidence

### 4.1 The 1.x contract (Codex's biggest add) — **Codex is right, and the repo already agrees**

Codex's core critique: carrying the current policy — "patch is ABI-additive, **a minor may break ABI**,
bindings pin `[X.Y.Z, X.(Y+1).0)`" — into 1.x makes "1.0" branding, not a contract. This is not just a
reasonable opinion; it is what the project's own contract-of-record anticipates. `version-coexistence.md`
says explicitly:

> "**Revisit at 1.0.** Once ABI-stability promises tighten (a 1.x line that pledges no ABI break within
> a major), the tradeoff inverts: a baseline ApiCompat gate becomes the right tool to *prove* the
> no-break pledge, and the minor window may widen. That is a 1.0 decision, not a pre-1.0 one."

The Grok audit lists "floor policy" as an owner decision but never makes the *window* question a 1.0
prerequisite. It is the prerequisite. Concretely, at 1.0 the decision has three coupled code touchpoints:

1. `RuntimeVersionRange.Build` (`src/Swift.Bindings/src/Emitter/RuntimeVersionRange.cs:55`) — for a 1.x
   input, the emitted ceiling should become the next **major** (`[1.Y.Z, 2.0.0)`), not the next minor,
   iff the promise is signed.
2. `RuntimeContract.MinimumSupportedGeneratedVersion` (`RuntimeContract.cs:82`, currently `16`) — the
   floor decision (§4.2).
3. The ApiCompat baseline — deferred pre-1.0 for a real reason (a `PackageValidationBaselineVersion`
   forces a `PackageDownload` that breaks offline/dry-run packing). Codex's sequencing is correct and I
   sharpen it: **1.0 itself needs no baseline** (there is nothing to diff against); the baseline (or an
   equivalent release-lane-only ApiCompat step that records "skipped offline" as incomplete) must be in
   place **before the first post-1.0 release**, because from 1.0.1 onward the widened window is only as
   good as its enforcement.

And Codex's conditional is the honest fork: **if the owner cannot sign "Runtime/generated-contract
breaks wait for 2.0," do not ship 1.0.** A 1.0 whose minors may still fracture consumers via `NU1107`
is a worse product than an honest 0.18. One calibration point in favor of signing it: the load-time
dispatch contract has broken exactly once in the observable record (floor = 16, i.e. the 0.16
payload-construction-semantics break), and the epoch handshake + reflective backstops were designed
precisely to let older bindings ride newer runtimes additively. The machinery for a major-only promise
exists; what's missing is the commitment and its enforcement.

### 4.2 RuntimeContract floor at 1.0 — **Reset to 1000; agree with Codex; genuinely owner's call**

Normal NuGet restore already separates 0.x bindings from a 1.0 runtime (a 0.17 binding pins
`[0.17.0, 0.18.0)`). Keeping the floor at 16 therefore only promises that *bypass paths* — direct
references, static bundles, NativeAOT slice selection, mixed-pack harnesses (exactly the paths
`RuntimeContract.cs`'s remarks name as the gate's purpose) — may load 0.x bindings under 1.x. That is a
compatibility claim with near-zero practical benefit and real reasoning cost. Resetting to 1000 buys a
clean quantifier: *every binding a 1.x runtime will load was generated by a 1.x-era generator*, which is
the same population the 1.x promise quantifies over. The floor↔minor unit guard already exists to keep
this honest. This remains the owner's signature, not autopilot — but the evidence is one-sided.

### 4.3 Core SDK vs package portfolio — **Codex is right; Grok's exit criteria overreach**

Grok's "one functional behavior test per headline shipped package" as a 1.0 exit criterion quietly
converts 1.0 into a 26-package portfolio-hardening campaign — the exact unbounded work-selection loop
that produced the "never felt ready" feeling. Codex's tiering (Verified / Preview / Community /
Parked) is the correct scope valve: **the 1.0 blocker is that claims match tiers**, not that every
package becomes Verified. Grok's underlying instinct (metadata-only tests are false confidence — the
universal test-depth finding in `BindingAudit/_SUMMARY.md`) is correct and survives intact inside the
tier model: a package may not carry the **Verified** label without a headline functional round-trip.
CryptoKit ECDSA and MusicKit `.items` are tier/claim decisions ("Preview, with these documented dark
paths"), not 1.0 engineering.

### 4.4 EveryProtocol: fail-closed vs capability — **Fail-closed is the 1.0 bar; one addition**

Codex is right that a centralized, tested fail-closed outcome (every emitted reverse-dispatch surface is
demonstrably fillable, or the member is omitted/rejected with a durable diagnostic) is sufficient for
1.0, with a hard cap, and that capability expansion is post-1.0 consumer-driven work. Two additions:

- The plumbing for "durable diagnostic" now exists on both surfaces: the Swift-path `SkipTriage` and —
  since `3de82f4a` — the ObjC-path drops persist into the same binding report. A new fail-closed decline
  must land as a classified skip reason, not a log line, or it recreates the "no decision recorded"
  review-tier noise already seen on `ICAPIReporter` (`roadmap.md`, FB mixed-binding drops row).
- Be honest about the consumer-visible consequence: fail-closing RealityFoundation `Materials` means the
  member *disappears* from the next regenerated package rather than throwing. That is the right trade
  (visible absence beats invisible death), but it is a surface reduction on shipped packages and belongs
  in release notes and tier labels, not silently in a diff.

The audit's fallback ("at minimum, a getter that can only throw should be modeled set-only /
documented") is weaker than omission and should not be the chosen shape: a getter that exists-but-throws
is precisely the trust failure being eliminated.

### 4.5 Gate enforcement truth — **Codex is right on every verified particular**

Verified directly (2026-07-09):

- `.github/workflows/release.yml` runs: `nuke test`, `binding-tests --strict --compile-only`, the
  Issue-1 skip-attribution test, a tier-2 simulator run, `validate-blast-radius`, and `nuke pack`.
  It runs **no** `PackGate`, no mixed-pack/mixed-direct, no device leg, no x64 gates, no
  `--appstore-hygiene`, no full `nuke validate`.
- `Pack` declares `.After(BindingTests, PackGate)` (`build/Build.Pack.cs:26`) — Nuke ordering, not
  dependency. `PackGate` executes only when explicitly invoked or via `Validate`'s
  `.Triggers(PackGate, BehaviorTier)` (`build/Build.Validation.cs:44`) — and release.yml does not run
  `Validate` either.
- `--appstore-hygiene` is deliberately tri-state (`build/Build.BindingTests.AppStoreHygiene.cs:109-119`):
  structural nupkg checks always run and fail closed; the signed-IPA leg logs an "honest SKIP —
  skipped, not passed" when the codesigning identity is absent — **and the target exits green**. The
  design is honest; any release checklist keyed on exit codes is not.

So the correct statement is sharper than Grok's "opt-ins are process risk": today, **a green release
pipeline is compatible with every defect class that only PackGate, the mixed-consumption legs, and
signed-IPA inspection uniquely cover** (`nuke pack` itself does perform some structural checks — the
gap is the consumption/hygiene proof, not all packaging validation). The 0.17.0 near-miss (§1, point 3)
shows both halves: the heavyweight
local harness caught what enforced CI could not, and only owner discipline caused it to run.

The fix is small and Codex scoped it correctly: one composed release surface producing a
machine-readable result manifest where every leg is `pass | fail | skipped(reason)` and the release
decision requires explicit disposition of every `skipped`. One cheap addition Codex didn't make explicit:
**`PackGate` needs no signing identity and no hardware — put it directly in `release.yml` as a job.**
There is no reason the structural packaging gate should wait for the manifest work.

### 4.6 IVT / Runtime API freeze timing — **Agree with Codex: document + snapshot, don't migrate**

Generated bindings live in arbitrary consumer assemblies; the generated-code-facing surface must remain
public IL, so `InternalsVisibleTo` cannot express the boundary and a support-assembly split reclassifies
without removing the compatibility burden. For 1.0: write the two-tier doc (consumer API vs generated
binding contract), freeze both for 1.x, snapshot the public surface at RC, enforce from the first
post-1.0 release. `Future/post-1.0-architecture-roadmap.md` already holds the `IGeneratedSwiftObject`
migration as post-1.0 additive work — keep it there.

### 4.7 Owner-hour budgets — **Codex's discipline is right; its unit is wrong for this project**

Codex's 50–80h estimate with a 90h re-planning ceiling is the right *shape* of constraint. But this
project's operating model (non-coding owner; AI-implemented; see §5) means owner cost concentrates in
**decisions, reviews, and hardware-attended runs**, not implementation hours. I restate the budget in
§10 as *owner decision/supervision hours* + *AI implementation sessions*, which is the unit that
actually predicts whether this drags into another six-month campaign. The hard ceiling concept stays.

---

## 5. My own assessment: architecture, maintainability, and the ownership question

### 5.1 Architecture — the right shape, honestly costed

I confirm both reviews' structural analysis and won't repeat it. The hybrid (direct ABI where safe,
generated wrappers where the CLR can't express the ABI, managed projections, EveryProtocol reverse
dispatch, fail-closed drops, supplement facades) is the only serious design for this problem; the strong
seams (`TypeResolver` strategies, projection visitors with compile-time-exhaustive arms,
`MemberValidationPipeline`, `VtableLayout` as the single slot-layout oracle, `ModuleEmissionContext` side
tables, the `RuntimeContract`/`RuntimeVersionRange` coupling) are real architecture, not archaeology.
The weaknesses (no plan/emit IR, marshal/emit boundary leaks, script orchestration, mega-files in
reverse dispatch) are real, inventoried, trigger-gated, and correctly deferred.

### 5.2 Maintainability — reframe the question

Both prior reviews score maintainability against an implicit "competent human staff" model: Grok says
"expert + strong gates, not junior-friendly"; Codex says the load-bearing prose "imposes a real owner
tax." For this repository, that model is fiction. There are no juniors, there is no expert staff, and
the owner does not write code. The maintainer is a rotating set of AI sessions whose entire working
memory of the system *is* `CLAUDE.md`, `.claude/rules/constraints.md`, the memory files, the test
floors, and the gates.

Under that model:

- **The "second product" (written invariants) is not overhead — it is the primary interface.** A
  multi-site invariant captured as prose + a pinning test is *more* durable here than a clever type
  refactor a future session might not understand the motivation for. The evidence is the last six
  months: ~1,400 commits of high-risk ABI work landed by AI sessions with a zero-regression policy that
  held everywhere the gates had coverage — and the one recent escape (the 0.17.0 RealityFoundation
  wrapper regression) was an enforcement gap, not a comprehension gap.
- **Bus factor inverts.** The classic risk ("one human's memory") is replaced by a different one:
  **doc/gate drift** — a constraint that silently stops being true, a gate that silently stops running.
  That re-ranks investments: machine-checkable enforcement (result manifests, floors, parity gates,
  lockstep unit guards like the floor↔minor test) is worth more per hour than any decomposition of a
  7k-line emitter, because it is the thing that keeps future AI sessions honest.
- **The rewrite case gets *weaker*, not stronger.** A rewrite's classic payoff is human onboarding
  velocity. There is no onboarding cohort. The costs (discarding the ABI evidence base) remain in full.

Two honest limits on this reframe (per Codex's review of this document, which I accept). First, bus
factor **reshapes, it does not disappear**: the owner remains the sole authority for product boundaries,
release disposition, signing/device runs, and — critically — judging whether an AI change actually
addressed the root cause rather than papering over it. Second, the code-quality debt still bites the AI
maintainer: mega-files and hidden multi-site coupling consume context and raise model error, which is
why the trigger-gated decompositions stay on the post-1.0 roadmap rather than being cancelled.

So the score stays ~6.5. What the operating model changes is the *investment ranking* (enforcement and
pinning tests over readability decomposition; machine enforcement replacing prose wherever both can
express the same rule) — not the number. The threat to long-term ownership was never the code; it is
unbounded definition-of-done plus gates that depend on remembering to run them.

### 5.3 Rewrite verdict

**No.** Full agreement with both reviews, with §5.2's addendum that the strongest human argument for
rewriting doesn't even apply here. Narrow greenfield remains appropriate only around stable boundaries
(release-gate composition + manifest, API snapshot capture, tier manifest, reporting schema) — additive
surfaces around the product, never the ABI core. The graduation paths already chosen
(`libswiftDemangle` swap, SwiftSyntax producer, per `architecture-gameplan-v2.md`) are the model.

### 5.4 Park / wait / push

Parking freezes a unique capability without harvesting it; waiting passively leaves known silent-failure
landmines under real users. Push the bounded 1.0 — with Codex's contract condition as the honest gate.
If the contract answer is "not yet," the same trust work ships as 0.18 and nothing is wasted.

---

## 6. What 1.0 certifies (adopted definition)

I adopt Codex's §6 core-certifies / does-not-certify split verbatim, with Grok's §10 framing folded in.
Compressed:

**1.0 certifies**: the Runtime/SDK/Templates lane and documented workflows; the advertised
platform/architecture matrix; pure-Swift, pure-ObjC, and mixed consumption paths as claimed; TN2435
packaging; loud failure or reported omission when a safe binding can't be emitted; and the written 1.x
compatibility contract (major-only Runtime/generated-contract breaks; floor 1000; widened restore
ceiling).

**1.0 does not certify**: full Swift-language projectability; every member of every input emitting;
equal support across the package portfolio (tiers, §4.3); byte-identical regeneration across minors; or
that every Apple framework is useful without a facade.

**Explicit non-goals published as stable**: result builders / SwiftUI tree composition, AppIntents
authoring, app-defined PAT conformers, full generic-signature fidelity, Windows host — all already in
`roadmap.md`; the 1.0 act is publishing them in the wiki Known Limitations as commitments, not notes.

One small claims item to close (Codex caught it, verified: `README.md:83` promises "a NuGet package
compatible with .NET MAUI"): add the cheapest representative restore/build proof of the documented MAUI
consumption shape, or narrow the wording. Do not start a MAUI campaign.

---

## 7. Trust work — updated for what has already landed

The pre-1.0 trust list is shorter than both reviews state:

| Item | Status at this review | Remaining work |
|---|---|---|
| A1 ObjC skip reporting invisible | **LANDED** (`3de82f4a`; `Reporting/ObjCSkipProjection.cs`) | Close it in `next-release-remaining-work.md`; confirm the review gate consumes the merged report |
| A2 standard Apple type mappings | **LANDED** (`objc-type-mappings.json:172-184`) | Confirm the paired fixtures exist per the doc's own bar |
| A3 stderr routing + stdout grammar | **LANDED** (`Program.cs:2471`) | Confirm the D6 grammar regression test landed with it |
| 0.17.0 RF wrapper regression | **FIXED** (`52d6ff58`, fixture + unit coverage) | Re-run the full regression-validation matrix it aborted |
| EveryProtocol silent-dead class | Open | Fail-closed centralization, three verified shapes pinned, hard cap (§4.4) |
| Stripe `AppInfo` masked skip | Open | Minimal BindingTests repro → classify (general corruption / package-specific / bad test); grep sibling ObjC-bridged bindings for the same masked-skip shape |
| Package test depth | Open | Resolved via tiers: Verified requires one headline functional round-trip; others get honest labels |
| §B ship mechanics (MapLibre V-1, FB mixed V-1, appstore-hygiene run) | Open, already committed this cycle | These are 0.17-era portfolio shipping, not new 1.0 scope — run them as specified in `next-release-remaining-work.md` §B |

Guardrails both reviews stated and I endorse: no broad string-marshalling audit before the Stripe repro
exists; no reverse-dispatch capability campaign disguised as the fail-closed fix; A1–A3's completion is
not permission to reopen the whole ObjC surface.

---

## 8. Gates: design vs enforcement

Design: 8.5/10 — the pyramid is right, fail-closed compile gates are real, floors catch silent test
deletion, the tri-state AppStoreHygiene skip is *more* honest than most industrial CI.

Enforcement: 6/10 — the verified facts in §4.5. The whole fix:

1. **Now, independent of 1.0**: add `PackGate` as a `release.yml` job (hosted-CI-safe, no signing, no
   hardware). This closes the largest free gap in one small PR.
2. **For the 1.0 window**: one composed release surface (`nuke release-gates` or equivalent) emitting a
   machine-readable manifest — every leg `pass | fail | skipped(reason)`; hardware/signing-dependent
   legs (device, mixed-pack device, signed IPA) run attended on the owner Mac and their results attach
   to the same manifest; the publish decision requires zero undispositioned `skipped`. This is the
   direct antidote to the 0.17.0 pattern where gate coverage was a function of memory.
3. **Post-1.0, before 1.0.1**: the ApiCompat baseline (or release-lane-only equivalent) per §4.1, so the
   widened 1.x window is proven, not pledged.

Keep PR CI exactly as lean as it is.

---

## 9. Do-not lists

### Park for the 1.0 cut (do not let these back in)

- CryptoKit ECDSA / MusicKit `.items` / existential-in-container capability unlocks — tier/claim
  decisions, not blockers.
- Reverse-dispatch capability expansion beyond the fail-closed cap.
- Functional tests for all 26 packages (Verified-tier only).
- Emit-rate % improvement against intentional buckets (`roadmap.md` §Not Worth Addressing).
- Any new Apple/third-party portfolio additions.
- `nuke validate` retirement (its own criterion in `roadmap.md` is not met).
- Device or full validate on every PR.
- IVT / support-assembly split (§4.6).
- Performance work without a consumer measurement.
- A comprehensive security audit — write the short trust-boundary statement (unsafe interop tooling;
  binding inputs and build execution are trusted; a binding is not a sandbox) and stop.
- A MAUI scenario campaign (one restore proof or narrower wording only).

### Post-1.0, on trigger only (triggers as written in `Future/post-1.0-architecture-roadmap.md`)

Plan-vs-emit IR; `PipelineContext` / static-collector removal; parser/marshaler/emitter and Build/SDK
decompositions; async-emitter Tier-1 extraction; post-emission rewriter strangle; `IGeneratedSwiftObject`
migration; `ExistentialContainer0..8` consolidation; broad API-snapshot tooling; benchmarks; tvOS device
runner; deeper x64 infrastructure.

### Never (unless the product changes or a customer funds it)

From-scratch rewrite of generator, runtime, or reverse dispatch; full Swift type-system fidelity；
result-builder projection / composing SwiftUI trees from C#; AppIntents authoring via bindings;
app-defined PAT conformers as a general feature; entitlement-gated frameworks without a paying user;
Windows-host support.

---

## 10. Ranked next actions

Estimates split into **owner hours** (decisions, reviews, attended hardware runs — the scarce resource)
and **AI sessions** (implementation — bounded but not owner time). Stop conditions are hard.

| # | Action | Owner hrs | AI sessions | Stop condition / output |
|--:|---|---:|---:|---|
| 1 | **Decision record**: 1.x contract (major-only breaks: yes/no), floor→1000, restore-ceiling widening, package tiers, supported platform/arch matrix, RC-soak policy | **2–4** | 0 (Claude drafts, owner signs) | One short signed doc. **Nothing else starts until this exists.** If the contract answer is "no": re-target everything below at 0.18 and stop reading the 1.0 rows. |
| 2 | Close out the already-landed A1/A2/A3 (verify fixtures/tests match the doc's bar; update `next-release-remaining-work.md`) | 0.5 | 1 | Doc reflects reality; no phantom work items remain |
| 3 | Stripe `AppInfo`: minimized BindingTests repro → classify; grep sibling ObjC-bridged bindings for masked skips | 0.5–1 (review) | 1–2 | Red test or evidence-backed non-core classification. **No broad audit.** |
| 4 | EveryProtocol fail-closed: shared capability decision, three verified shapes pinned (work or omit-with-diagnostic), declines land as classified skip reasons | 1–2 (review) | 2–4, **hard cap** | All known shapes closed. If the general mechanism exceeds the cap: ship the classifier alone, tier-downgrade affected packages, move capability to post-1.0. |
| 5 | §B ship mechanics: MapLibre V-1 (sim+device), FB mixed V-1 (`--mixed-pack` sim+device), `--appstore-hygiene` with signing present | **3–5** (attended device/IPA runs) | 1–2 | Saved sim/device/IPA outcomes. Generator changes only if a leg finds a release blocker. |
| 6 | Gate enforcement: `PackGate` into `release.yml` now; composed release surface + result manifest (skip ≠ pass) | 1 (review) | 1–2 | One release command; manifest artifact; publish decision requires dispositioned skips |
| 7 | Contract artifacts: written 1.x promise, public API snapshot at RC, `RuntimeVersionRange` 1.x ceiling change + floor reset (per #1), floor↔major unit guard updated | 1 (review) | 1–2 | Reviewable contract + baseline artifact + code matching the promise |
| 8 | Tier the portfolio; align wiki Known Limitations + README (MAUI wording or proof); add headline functional tests **only** for Verified-tier packages | 1–2 (tier calls are product decisions) | 1–2 | Claims match tiers; no requirement that all packages become Verified |
| 9 | Final RC gates (all advertised platform legs + one validate sweep, dispositioned) and cut `1.0.0-rc.1` | 1–2 | 1 | RC artifacts + full manifest |
| 10 | Fixed 7–10 day soak, blockers-only triage; ship stable even if feedback is silent, provided gates are clean | 0–3 | 0–1 | Stable 1.0, or a named blocker list |

Totals (adopting Codex's re-budget of this plan, which corrects my initial optimism — AI sessions are
not a zero-owner-cost unit; each needs review, steering, and possible failure triage):
**15–30 owner-attention hours** and **10–17 AI implementation sessions**, plus calendar soak.
**Hard caps: ~30 owner hours / ~20 sessions.** Global stop condition: if trust work (#3–#4) exposes a
new systemic silent-corruption class, or either cap is hit, ship maintenance 0.18, re-evaluate the 1.0
bar, and under no circumstances respond with a rewrite or a new audit campaign.

Pure owner decisions (cannot be delegated, listed once): the 1.x breaking-change promise; the floor
reset; package tier assignments and any claim downgrades; the supported platform/arch matrix; the
ship/hold call at RC.

---

## 11. Direct answers to the audit's §13 questions

1. **Rewrite vs evolve** — Evolve. No greenfield subsystem, explicitly including reverse dispatch (the
   worst candidate: clean-looking models compile, pass sim, and corrupt device vtables). New code only
   around stable boundaries: release orchestration, manifests, tiers, API snapshot.
2. **1.0 definition** — Trustworthy contracted core, per §6 — **with the 1.x contract decision as a
   prerequisite, which the audit's checklist omitted.** Do not wait for BindingAudit Tier-1 capability
   gaps; they are tier labels.
3. **EveryProtocol priority** — Yes, top trust fix. Fail-closed is sufficient for 1.0; omission beats
   set-only modeling; declines must persist as classified skips.
4. **Input-poor thesis** — The 80/50 refinement is correct. No further whole-repo audits (this document
   should be the last of the genre for a long time). Focused repro of known trust failures + new
   external inputs only.
5. **Runtime API freeze** — No IVT/support-assembly migration pre-1.0. Document the two-tier surface,
   freeze for 1.x, snapshot at RC, enforce from the first post-1.0 release.
6. **Floor policy** — Reset to 1000, coupled to the bigger decision the question undersells: the 1.x
   window itself moves to major-only breaks, or 1.0 waits.
7. **Gate automation** — Compose release gates + manifest; PR CI stays lean; `PackGate` goes into the
   release workflow immediately; hardware/signing legs run attended but attest into the same manifest;
   skip is never pass.
8. **Missed critical risk** — Codex's ranked list is right (contract contradiction, demand risk,
   enforcement truth, support-matrix proof, trust-boundary statement, upstream cadence). I add one:
   **doc/gate drift under the AI operating model** (§5.2) — the project's real bus-factor risk is a
   written invariant silently going stale, which is another argument for machine-checkable enforcement
   over prose wherever both can express the same rule.

---

## 12. Where I disagree with both

1. **Both mis-frame the maintainability owner.** "Expert-sustainable, not junior-friendly" (Grok) and
   "owner tax of load-bearing prose" (Codex) both assume a human maintainer economy. This project is
   AI-maintained by design (that is a stated ownership requirement, not an accident). The prose+gates
   corpus is the maintainer interface and has empirically carried ~1,400 commits of ABI-critical change.
   Consequences: the rewrite case is even weaker than both state; decomposition-for-readability drops
   near the bottom of the ROI table; and enforcement/attestation work rises to the top. Both reviewers
   accepted the reframe with the caveat (which I adopt in §5.2) that it must not inflate the score —
   the disagreement resolved to investment ranking, not maintainability rating. (§5.2)
2. **Both reviews' plans were stale on arrival regarding A1/A2/A3** — landed on `main` before this
   review (`3de82f4a`, `Program.cs:2471`). Not a fault of either analysis, but the 1.0 program should be
   re-baselined on §7's table, not on either document's work list. It also shrinks Codex's total by its
   rank-4 line item.
3. **Grok's gate scoring methodology** — one combined 8/10 for "quality gates / operational maturity"
   obscured the enforcement gap that its own §7.3 prose correctly flagged. Scores that average a
   strength and a defect into one number are how defects survive reviews.
4. **Codex's owner-hour unit** — right discipline, wrong denominator for this project; restated in §10
   as decisions/reviews/attended-runs vs AI sessions. Codex accepted the split and corrected my totals
   back up (15–30 owner hours, not ~a dozen — sessions still cost review, steering, and failure
   triage); I adopt that correction in §10. The net reframing stands: 1.0 is **decision-close**, and
   the scarce resource is owner attention, not 50–90 hours of implementation — but trust and packaging
   proof still carry real engineering uncertainty.
5. **Minor factual correction to Codex §8**: `PackGate` is not purely manual — `Validate` `.Triggers`
   it (`build/Build.Validation.cs:44`), so it runs whenever a full validate sweep runs. This does not
   rescue the release workflow (which runs neither), but the enforcement statement should be precise:
   the gap is that *the release lane* proves neither PackGate nor any consumption/IPA leg — not that
   the gates never execute anywhere.

---

## 13. 1.0 exit criteria (delta view)

I adopt Codex's §9 checklist as the base — it is tighter than the audit's §12 and every line is
verifiable. Deltas:

- **Add**: `PackGate` present as a required job in `release.yml` (not merely green once locally).
- **Add**: release result manifest exists; the RC and stable cuts each attach one with zero
  undispositioned `skipped` legs.
- **Add**: EveryProtocol fail-closed declines appear as classified entries in the persisted binding
  report (the A1-merged artifact), pinned by at least one fixture per verified shape.
- **Amend**: "A 1.0 public Runtime API snapshot is captured" → and the enforcement mechanism's
  offline/dry-run behavior is decided (release-lane-only is acceptable; silent skip is not).
- **Strike as 1.0 criteria** (portfolio-tier work, per §4.3): any requirement that non-Verified packages
  gain functional tests.
- **Re-affirm from the audit's list** (it was right and Codex kept it): wiki Known Limitations aligned
  with the published non-goals; multi-binding one-Runtime-minor pinning documented as intentional for
  0.x and superseded by the widened 1.x window.

---

## 14. Post-review convergence (2026-07-09)

Both prior reviewers read this document and endorsed it as the working decision document, closing the
audit phase. Their accepted corrections are already folded into the body above:

1. **Maintainability stays ~6.5** (§2, §5.2) — the AI-operating-model reframe re-ranks investments; it
   does not raise the score. Bus factor reshapes (owner remains sole authority for boundaries, release
   disposition, hardware runs, and root-cause judgment); mega-files still tax AI context.
2. **Budget re-corrected upward** (§10) — 15–30 owner-attention hours / 10–17 AI sessions, hard caps
   ~30 hours / ~20 sessions. AI sessions are not free or deterministic; "a dozen owner-hours away" was
   the dangerous sentence and is retracted. 1.0 is *decision-close*, not effort-free.
3. **Gate rhetoric narrowed** (§4.5) — `nuke pack` performs some structural checks; the enforced-green
   gap is specifically the defect classes only PackGate / mixed consumers / signed-IPA inspection cover.

Agreed outcome across all three reviewers: **no fourth synthesis document.** The next artifact is a
short owner-signed decision record (`1.0-decision-record.md`) covering the 1.x contract, floor 1000,
package tiers, the certified platform/arch matrix, and RC-soak + stop rules — then bounded
implementation per §10. Residual risk is not strategy; it is (a) optimism becoming another unbounded
campaign when EveryProtocol gets hard, and (b) shipping "1.0" without the contract decision. The caps
and the decision record are the mitigations.

---

## 15. Review metadata

| Field | Value |
|---|---|
| Date | 2026-07-09 |
| Trigger | Owner request for a full third independent analysis after Grok + Codex reviews |
| Verification performed | All disputed factual claims re-checked against workflows, Nuke targets, contract code, baselines, and `main` history; prior docs' work lists re-baselined against landed commits |
| Status | **Endorsed by all three reviewers as the working decision document** (corrections in §14 applied) |
| Headline | Own it; don't rewrite; decide the 1.x contract first; fail closed; make the release lane prove what the catalog can prove; ship a narrow 1.0 or an honest 0.18 |
| Non-goals of this doc | Not a commit plan; not a design doc for any single fix; the Grok/Codex companions remain the supporting analyses |

**Single sentence:** The product is sound, the gates are excellent, the enforcement is the gap, the
contract is the decision — 1.0 is decision-close, executed as capped, bounded sessions
(≤30 owner hours / ≤20 sessions), or it becomes an honest 0.18.
