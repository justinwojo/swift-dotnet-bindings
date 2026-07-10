# Codex Second Opinion — Architecture, Product Health, and a Bounded 1.0

**Status**: Independent companion review after 0.17.0.

**Author**: Codex.

**Primary comparison**: [`architecture-health-audit-2026-07.md`](architecture-health-audit-2026-07.md).

**Mode**: Review and strategy only; no implementation was performed.

**Scope**: Test the first audit's conclusions against the repository's strategy, shipped-binding audit,
release mechanics, contract code, and gate wiring. This is deliberately not another line-by-line audit of
the generator.

Companion evidence:

- [`roadmap.md`](roadmap.md)
- [`Future/post-1.0-architecture-roadmap.md`](Future/post-1.0-architecture-roadmap.md)
- [`BindingAudit/_SUMMARY.md`](BindingAudit/_SUMMARY.md)
- [`next-release-remaining-work.md`](next-release-remaining-work.md)
- [`version-coexistence.md`](version-coexistence.md)
- `src/Swift.Runtime/src/Swift/Runtime/RuntimeContract.cs`
- `src/Swift.Bindings/src/Emitter/RuntimeVersionRange.cs`
- `.github/workflows/ci.yml` and `.github/workflows/release.yml`
- `build/Build.PackGate.cs`, `build/Build.BindingTests.MixedPack.cs`, and
  `build/Build.BindingTests.AppStoreHygiene.cs`

---

## 1. Bottom line

The first audit's main recommendation is correct:

> **Do not rewrite. Do not resume broad architecture mining. Prepare a deliberately bounded 1.0, ship an
> RC, and let real users choose most post-1.0 work.**

I agree with roughly 80–85% of the audit. I disagree with or would tighten four important parts:

1. **The proposed contract freeze is not yet a real 1.0 contract.** The current policy says a Runtime minor
   may break ABI and generated bindings pin `[X.Y.Z, X.(Y+1).0)`. Carrying that policy unchanged into 1.x
   means 1.0 bindings and 1.1 bindings can still produce `NU1107` in one app, and 1.1 may break 1.0 Runtime
   consumers. That is late-0.x policy, not a normal 1.0 stability promise. The owner decision is broader
   than whether `MinimumSupportedGeneratedVersion` becomes 1000.
2. **The audit couples the core SDK's 1.0 too tightly to the quality of every separately shipped binding.**
   Core trust and representative package proof must block 1.0. A functional headline test for all 26
   audited bindings must not. Give packages explicit support tiers and stop marketing unproven flows.
3. **EveryProtocol is the top trust problem, but full capability expansion is not required.** A centralized,
   tested fail-closed result is sufficient for 1.0. Do not turn this into another reverse-dispatch campaign.
4. **Gate design is stronger than gate enforcement.** `PackGate`, mixed-pack/device, mixed-direct, x64 gates,
   and the full signed-IPA hygiene leg exist, but the release workflow does not run most of them. `Pack` is
   only ordered `.After(PackGate)`; it does not depend on it. The app-store gate also returns an honest
   non-failing skip when the signing identity is absent. A release checklist that merely invokes the target
   can therefore still fail to prove the IPA leg.

This is technically solid enough to own long-term. It is **not** psychologically sustainable if the owner
continues treating every documented latent, every package gap, and every architectural seam as active work.
The operating model has to change with 1.0.

---

## 2. Scorecard: where I agree and disagree

The first audit's numbers are broadly fair, but some combined scores hide the decision-relevant split.

| Dimension | Grok | Codex | Difference |
|---|---:|---:|---|
| Architecture fit | 8.5 | **8.0** | Right shape; slightly discounted for decision/emission coupling and orchestration leakage |
| Implementation maintainability | 6.5 | **6.0** | Expert-maintainable, but the load-bearing prose and multi-site invariants impose a real owner tax |
| Core Runtime/SDK/pack surface | 7.0 | **7.5** | Packaging shape, contract handshake, and happy path are unusually mature |
| Gate design | 8.0 combined | **8.5** | The gate catalog and failure philosophy are excellent |
| Gate enforcement | 8.0 combined | **6.5** | Release-critical opt-ins are not actually required by the release workflow |
| Core 1.0 readiness | 6.5 | **7.0** | A narrow 1.0 is close once trust and contract decisions are closed |
| Shipped-package portfolio trust | not split | **6.0** | BindingAudit found real compile-green/runtime-dead cases and shallow functional proof |
| Demonstrated demand/product validation | not scored | **4.5** | Some users and unique capability are encouraging; evidence is too thin for demand claims |

These are not scientific measurements. Their value is in separating different questions:

- The architecture can be good while the product contract is unfinished.
- The gate system can be excellent while the release workflow fails to enforce it.
- The core tool can be ready for 1.0 while individual generated packages remain preview-grade.
- Engineering maturity does not prove market demand.

---

## 3. Architecture and design

### 3.1 The architectural shape is correct

The hybrid is the right answer:

- direct Swift ABI where the CLR can express it safely;
- generated C-compatible or Swift wrappers where it cannot;
- managed projections for usable C#;
- witness tables and EveryProtocol for reverse dispatch;
- explicit dropping/facades for shapes C# cannot honestly represent.

A pure P/Invoke design would fail on resilient types, async, closures, and protocols. A universal wrapper
design would discard useful direct ABI support and grow a second runtime. A source-only design cannot infer
the compiled ABI reliably. A hand-authored bridge per library does not scale. The repository's current
decomposition is therefore not accidental compromise; it reflects the domain.

Concrete healthy seams include:

- `TypeResolver` strategies under `src/Swift.Bindings/src/TypeDatabase/Resolver/`;
- exhaustive projection visitors under `src/Swift.Bindings/src/Marshaler/Projection/`;
- `MemberValidationPipeline.cs` as an explicit fail-closed decision point;
- `VtableLayout.cs` as the shared reverse-dispatch layout model;
- `ModuleEmissionContext.cs` for per-module identity/symbol state;
- `RuntimeContract.cs` and `RuntimeVersionRange.cs` coupling restore-time and load-time compatibility;
- persisted reports and cross-artifact gates rather than trusting emitted source by inspection.

### 3.2 The architecture is nevertheless expensive

The first audit is right that the gravity wells are domain-driven, but that should not be used to minimize
their ownership cost.

Focused repository measurements support its scale claims: about 217k production lines in the generator,
21k in Runtime, 22k in build orchestration, 105k in BindingTests, 14,248 unit-test pass floor, 3,160 simulator
runtime pass baseline, and 1,438 commits in the preceding six months. `EveryProtocolEmitter.cs` is about
7.3k lines, `SwiftABIParser.cs` about 4.2k, `Sdk.targets` about 3.8k, and the split `ProtocolProxyEmitter`
surface is larger still.

More importantly, `.claude/rules/constraints.md` records invariants such as:

- projected overload identity and vtable-slot identity are intentionally different domains;
- vtable membership and fillability must not be conflated;
- an async collision distinction remains coordinated across three paths;
- wrapper packaging must reason about what *will be produced*, not what exists during the first pass;
- promoted symbols and original ABI symbols have different readers;
- simulator and device correctness can diverge only at runtime.

That is good scar capture, but it is also proof that a correct change often requires system memory beyond
the local file. The architecture is maintainable because tests, reports, and written constraints compensate
for this—not because the complexity has disappeared.

### 3.3 Solid enough to own long-term?

**Yes, with a narrower operating model.**

The code is not the reason to park. The threat to long-term ownership is the combination of bus factor,
release ritual, portfolio breadth, and an owner who has been using issue discovery as the default source of
direction.

Long-term ownership is reasonable if these rules become policy:

1. Consumer repros, support-matrix breaks, and release-gate reds outrank latent inventories.
2. A package is either verified, preview, community-supported, or parked; all packages are not equally owned.
3. Architectural work needs a live failure, a measured maintenance bottleneck, or a compiler/runtime change.
4. A release has a single executable gate surface and a saved result, not a remembered sequence.
5. The owner has a hard budget for a release cycle and can downgrade a claim instead of implementing a feature.

Without those rules, the system remains technically maintainable but the project is not personally
maintainable.

---

## 4. Rewrite vs evolve

### Verdict

**Do not rewrite the repository or reverse dispatch.** I agree with the audit's blunt conclusion, though
“strategic malpractice” is rhetoric rather than analysis.

A rewrite would initially look cleaner because it would omit the bugs, platform splits, wrapper fallbacks,
packaging edge cases, and consumer shapes already learned here. Reintroducing those facts would recreate the
complexity while losing the evidence base. The current moat is not LOC; it is the accumulated ABI truth plus
tests, reports, real packages, and release mechanics.

Reverse dispatch is the **worst** candidate for a greenfield replacement. It is where a superficially clean
model can compile, pass simulator tests, and corrupt a device vtable. `VtableLayout` is already the correct
incremental extraction seam. Extend it only when a failing shape demands it.

Narrow greenfield work is appropriate only around stable boundaries:

- a composed release-gates target/result manifest;
- a Runtime public-API snapshot/compatibility check;
- an explicit package support-tier manifest;
- reporting schema improvements such as ObjC skip persistence.

Those additions surround the product; they do not replace its ABI core.

---

## 5. The audit's biggest underweight: the 1.x contract

The current contract of record in `RuntimeVersionRange.cs` and [`version-coexistence.md`](version-coexistence.md)
says:

- patch is ABI-additive;
- minor may break ABI;
- each generated binding pins `[X.Y.Z, X.(Y+1).0)`;
- two bindings generated one Runtime minor apart can be mutually uninstallable.

That is a defensible pre-1.0 safety policy. It is a poor default 1.x policy.

If 1.0 still means “1.1 may break the generated-binding/Runtime contract,” then “contract freeze” is mostly
branding. Consumers will reasonably read 1.0 as “breaking Runtime/public-contract changes wait for 2.0.”

### Recommended 1.x promise

1. Existing compiled 1.x bindings continue to load on later 1.x Runtime versions.
2. Runtime ABI, generated-binding-facing public members, and stable CLI/MSBuild inputs do not break until 2.0.
3. Newer bindings still declare a minimum Runtime version, so a 1.1 binding cannot load on 1.0 when it uses
   new additive contract members.
4. Old 1.0 bindings may float to later compatible 1.x Runtime versions; the upper bound should be the next
   major, not the next minor.
5. Generator output may improve between minors, but reproducibility requires pinning the SDK, and any
   intentional source-surface change is release-noted. Silent ABI/behavior regression is never permitted.

The current epoch model can support this shape: a 1.0 binding has epoch 1000; a 1.1 Runtime has epoch 1001
and can accept 1000 while its floor remains 1000. The restore range, public API discipline, and compatibility
gate need to express the same promise.

If the owner is unwilling to make major-only breaking changes after 1.0, my recommendation changes:

> **Do not ship 1.0 yet. Ship a maintenance-focused 0.18/0.19, keep the bounded-minor policy, and wait for
> enough adoption evidence to justify the stronger contract.**

I do not recommend publishing 1.0 with a footnote that minors may still break Runtime ABI.

### RuntimeContract floor

Set `MinimumSupportedGeneratedVersion` to **1000** for 1.0.

Normal 0.17 bindings already request a Runtime range below 0.18 and therefore cannot restore with 1.0.
Leaving the load-time floor at 16 mainly permits bypass paths—direct references, static bundles, or custom
packaging—to load 0.x bindings under 1.0. That creates a compatibility claim with little practical benefit.
A major boundary is the clean time to reset, reject old compiled bindings loudly, and ask consumers to
regenerate.

This remains an owner decision, but the evidence strongly favors the reset.

### Public Runtime API

Do **not** block 1.0 on an `InternalsVisibleTo` migration or support-assembly split.

Generated bindings live in arbitrary consumer assemblies, so much of the generated-code-facing surface must
be public. `InternalsVisibleTo` cannot solve that generally. A support assembly can classify the surface but
does not remove its compatibility burden.

For 1.0:

- document “consumer API” versus “generated binding contract” even though both are public IL;
- freeze both for compatibility within 1.x;
- capture a machine-readable public API snapshot from the 1.0 RC/stable surface;
- require compatibility enforcement before the first post-1.0 release;
- use additive shims for later cleanup.

An assembly split can happen post-1.0 as an additive migration with old entry points retained.

---

## 6. What 1.0 should and should not certify

The audit is right that 1.0 means trust, not Swift feature completeness. I would define the certified product
more narrowly.

### Core 1.0 certifies

- the Runtime/SDK/Templates lane and its documented generator/consumer workflow;
- the advertised Apple platform and architecture support matrix;
- pure-Swift PackageReference consumption;
- pure-ObjC and mixed ObjC+Swift consumption where claimed;
- Runtime native packaging and App Store hygiene;
- loud failure or explicit reported omission when a safe binding cannot be emitted;
- the written 1.x compatibility contract.

### Core 1.0 does not certify

- that all Swift language features are projectable;
- that every public member in every input framework emits;
- that every package in the companion portfolio is equally production-supported;
- that regenerating with a newer minor produces byte- or source-identical C#;
- that every Apple framework is useful without a supplement or native Swift facade.

### Package portfolio policy

Give each published binding one explicit tier:

1. **Verified** — a package consumer build/run and at least one headline behavior are gated.
2. **Preview** — compiles/packages, limitations are honest, functional proof is incomplete.
3. **Community-supported** — useful artifact without an owner commitment to headline-flow coverage.
4. **Parked/not shipping** — structurally mismatched products such as AppIntents authoring.

The 1.0 blocker is that claims match tiers—not that 26 packages all become Verified.

This is where I differ most from “one functional behavior test per headline shipped package” as a single
core exit criterion. That recommendation is good portfolio work and bad 1.0 scope control. Require it only
for packages labeled Verified or used as representative core proofs.

---

## 7. Trust work before 1.0

### 7.1 EveryProtocol: top priority, bounded answer

The audit and [`BindingAudit/_SUMMARY.md`](BindingAudit/_SUMMARY.md) are convincing: a normal-looking API that
throws from a getter or accepts a delegate whose callbacks never fire is worse than an explicit missing
member. It breaks the meaning of a green compile.

For 1.0, the required outcome is:

> Every emitted reverse-dispatch surface is either demonstrably fillable for its required members or is
> omitted/rejected with a durable diagnostic before a consumer can rely on it.

It is **not** necessary to make every affected RoomPlan, RealityFoundation, BlinkIDUX, Stripe, or AppIntents
protocol work. A shared fail-closed capability decision, pinned by the three verified shapes, is enough.

Hard stop: spend at most roughly 24 owner-hours on this 1.0 item. If general capability expansion is larger,
ship the fail-closed classifier and move capability work to consumer-driven post-1.0 sessions.

### 7.2 Stripe `AppInfo`: reproduce before declaring a systemic blocker

The masked skip violates project policy, but the proposed tagged-NSString root cause is still a hypothesis.
The 1.0 requirement is a minimized BindingTests reproduction and a classification:

- general Runtime/marshalling corruption → fix before 1.0;
- package-specific generation problem → fix or downgrade/quarantine the package claim;
- invalid test assumption → correct the package test and document the evidence.

Do not let an unconfirmed package hypothesis expand into a broad string-marshalling audit.

### 7.3 ObjC reporting

`next-release-remaining-work.md` establishes that mixed bindings persist Swift skip triage while ObjC drops
only reach an INFO summary. This is a trust/reporting hole and should land before 1.0 if mixed binding is a
certified path. It does not require fixing every reported drop.

The standard Apple mappings in A2 and stderr/stdout contract in A3 are sensible, bounded work already
committed for the next release. Finish them; do not use their completion as permission to reopen the entire
ObjC surface.

---

## 8. Packaging and release proof

The first audit correctly praises packaging and correctly says release-critical coverage is partly manual.
The concrete gap is sharper than the audit states.

### Current enforcement reality

- `.github/workflows/release.yml` runs unit tests, strict compile-only BindingTests, a tier-2 simulator run,
  blast-radius validation, and `nuke pack`.
- It does **not** invoke `PackGate`.
- `Pack` declares `.After(PackGate)`, which only orders the targets when both are in a Nuke plan; it does not
  make `PackGate` a dependency.
- It does not run mixed-pack, mixed-direct, device Runtime tests, x64 gates, full validate, or app-store
  hygiene.
- `--appstore-hygiene` performs the structural package check but returns successfully after an explicit
  IPA-leg **SKIP** when the signing identity is absent.

Therefore “the target ran” is not enough. The release proof must record which legs actually executed.

### Recommended split

Keep PR CI lean. Add one composed release surface that produces a machine-readable result/attestation for:

- `nuke test`;
- strict compile-only BindingTests;
- simulator and physical-device Runtime tests;
- `PackGate`;
- mixed-pack on simulator and device;
- mixed-direct on simulator;
- a real pure-ObjC MapLibre package consumer on simulator and device;
- a real mixed Facebook package consumer on simulator and device;
- full signed-IPA app-store hygiene, with “signing unavailable” treated as **incomplete**, not pass;
- one final pre-release `nuke validate` sweep;
- macOS, Catalyst, and tvOS runtime legs if they remain advertised 1.0 platforms;
- x64/Rosetta gates if x64 remains advertised support.

Not all of this belongs on hosted CI. A signed local owner-Mac/device run is acceptable for 1.0 if its result
is explicit and the release workflow requires the attestation/checklist decision. Hardware absence should
block the certification claim, not masquerade as a test failure and not silently pass.

One small missing product proof also deserves attention: README claims compatibility with .NET MAUI, while
the repository strongly tests multi-Apple-TFM binding packages but does not show an obvious representative
MAUI multi-target consumer smoke. Either add a cheap build/restore proof for the documented consumption
shape or narrow the wording. Do not launch a MAUI feature campaign.

---

## 9. Concrete 1.0 exit criteria

### Contract

- [ ] Owner signs the 1.x compatibility promise: major-only Runtime/generated-contract breaks.
- [ ] Runtime dependency window for 1.x matches that promise rather than retaining a next-minor ceiling.
- [ ] `MinimumSupportedGeneratedVersion` is set to 1000.
- [ ] Consumer API, generated-binding contract, generator-output policy, and support matrix are written.
- [ ] A 1.0 public Runtime API snapshot is captured; enforcement is required before any later 1.x release.

### Trust

- [ ] The three known EveryProtocol failure shapes are working or fail closed before exposing a normal API.
- [ ] Stripe `AppInfo` is reproduced and classified; no known general corruption remains hidden as a skip.
- [ ] ObjC drops are persisted in the binding report/review signal.
- [ ] Known limitations and package claims match emitted behavior.

### Package and runtime proof

- [ ] Pure-Swift packed binding is consumed and exercises a real ABI call.
- [ ] Pure-ObjC real package is consumed on simulator and device; a delegate callback fires.
- [ ] Mixed real package is consumed on simulator and device; callbacks work and ObjC classes register once.
- [ ] Mixed SDK-direct simulator leg is green.
- [ ] `PackGate` is green, not merely ordered before another target.
- [ ] Full signed-IPA hygiene leg executes and passes; a structural-only run is recorded as incomplete.
- [ ] All advertised platform/architecture legs have a final RC result.
- [ ] One final validation-corpus sweep is green or every delta is explicitly dispositioned.

### Portfolio and release

- [ ] Published bindings have explicit support tiers.
- [ ] Verified packages have a headline functional test; Preview packages have honest limitations.
- [ ] `1.0.0-rc.1` is offered to current users for a short fixed soak.
- [ ] Only release blockers are accepted during the soak; no feature expansion.
- [ ] Stable 1.0 ships after the soak even if feedback volume is low, provided gates and known blockers are clear.

---

## 10. Ranked next actions in owner-hours

These estimates are active owner time, not build wall-clock time. Unknown root causes make them ranges, not
promises.

| Rank | Action | Owner-hours | Stop condition / output |
|---:|---|---:|---|
| 1 | Decide the 1.x contract, floor, core-vs-portfolio scope, package tiers, and supported architectures | **3–5** | One short decision record; no code work until these are fixed |
| 2 | Minimize/classify the known trust cases: EveryProtocol shapes + Stripe `AppInfo` | **4–6** | Red tests or evidence-backed non-core classification; no broad audit |
| 3 | Close EveryProtocol silent-dead behavior, preferring shared fail-closed validation over capability expansion | **12–24** | All known shapes work or are omitted with diagnostics; hard cap at ~24h |
| 4 | Finish already-committed A1/A2/A3 from `next-release-remaining-work.md` | **8–14** | Persisted ObjC triage, bounded mappings, stdout/stderr contract |
| 5 | Turn real packaging claims into facts: MapLibre, Facebook mixed, PackGate, mixed-direct, signed IPA | **6–12** | Saved sim/device/IPA outcomes; no new generator features unless a gate finds a release blocker |
| 6 | Write the 1.0 stability/support contract and capture the Runtime public API baseline | **4–8** | Reviewable contract + baseline artifact |
| 7 | Compose release gates and make incomplete/skip distinct from pass | **4–6** | One release command/result manifest; hosted CI may remain lean |
| 8 | Classify package tiers and trim claims; add only the smallest missing representative headline/MAUI proofs | **3–5** | No requirement to upgrade every package to Verified |
| 9 | Run final platform/validate/x64 gates and cut `1.0.0-rc.1` | **3–6** | RC artifacts and gate result |
| 10 | Fixed 7–10 day external soak with a maximum triage budget | **0–8** | Stable 1.0 if no blockers; no feature work during soak |

Expected total: **about 50–80 owner-hours**, plus passive soak and build time. Set a hard re-planning ceiling at
**90 owner-hours**. If the trust work crosses that ceiling or exposes another systemic silent-corruption
class, ship a maintenance 0.18 release and re-evaluate the 1.0 bar. Do not respond by starting a rewrite.

---

## 11. Explicitly do not do for 1.0

### Park for the 1.0 cut

- CryptoKit ECDSA and MusicKit `.items` capability unlocks; fix the claims or mark the packages Preview.
- Existential/container expansion without a release-blocking verified flow.
- Full reverse-dispatch support for every skipped carrier; fail closed instead.
- A functional test for every audited package.
- API percentage improvement as a goal.
- More Apple/third-party portfolio expansion.
- Performance optimization without a consumer measurement.
- Retirement of `nuke validate`.
- Device or full validate on every PR.
- A comprehensive security audit. Write a small trust-boundary statement instead: the generator/runtime is
  unsafe interop tooling, a binding input is not a security sandbox, and build/package inputs must be trusted.

### Post-1.0, only on a trigger

- plan-vs-emit IR;
- `PipelineContext` and static-collector removal;
- parser/marshaler/emitter decomposition;
- async-emitter exact-duplicate extraction;
- support-assembly split or `IGeneratedSwiftObject` migration;
- post-emission rewriter strangulation;
- broad API snapshot tooling beyond the Runtime contract baseline;
- performance benchmarks;
- full MAUI scenario expansion;
- validate retirement;
- deeper x64/tvOS infrastructure beyond the declared support matrix.

The triggers in [`Future/post-1.0-architecture-roadmap.md`](Future/post-1.0-architecture-roadmap.md) are good:
a consumer bug, compiler/runtime break, measurable bottleneck, or required valid-surface gain.

### Never, unless the product becomes different or a customer funds it

- from-scratch generator/runtime rewrite;
- full Swift type-system fidelity;
- result-builder projection and general SwiftUI tree composition from C#;
- AppIntents authoring through bindings alone;
- app-defined PAT conformers as a general runtime feature;
- entitlement-bound framework investment without a user who can exercise it;
- Windows-host support for a workflow whose authoritative toolchain and SDK exist only on macOS.

---

## 12. §13 reviewer questions — direct answers

### 1. Rewrite vs evolve

**Evolve.** No greenfield reverse-dispatch subsystem. The safe new seams are release orchestration, reporting,
support-tier metadata, and API compatibility capture. Reverse dispatch should be changed through its current
shared models and tests.

### 2. 1.0 definition

**Trustworthy contracted core is the right bar.** Do not wait for CryptoKit ECDSA, MusicKit items, or all
BindingAudit Tier-1 capability gaps. Those block only a package claim/tier, not the SDK's 1.0, unless the
project explicitly chooses to include that headline capability in the core 1.0 promise.

### 3. EveryProtocol priority

**Yes, top trust priority. Fail-closed-only is enough for 1.0.** Capability expansion is desirable only when
it stays inside the timebox.

### 4. Input-poor thesis

The audit's 80%/50% refinement is fair. The project is input-poor for new ABI shapes and still has known
product-correctness gaps in existing packages. Another whole-repo audit is not justified. Focused
reproduction of known trust failures and new consumer inputs is justified.

### 5. Runtime API freeze

Do not perform an IVT/support-assembly migration before 1.0. Classify the public surface in documentation,
freeze the generated-binding-facing contract for 1.x, snapshot it, and preserve later cleanup through
additive shims. Arbitrary generated assemblies make “just internalize it” unrealistic.

### 6. Floor policy

**Reset to 1000.** Normal NuGet restore already separates 0.x and 1.0. Keeping floor 16 mostly expands a
bypass-path promise. More importantly, change the 1.x dependency/ABI policy from minor-bounded breaks to
major-only breaks.

### 7. Gate automation

Compose release gates and keep PR CI lean. Device, x64, full validate, real-package consumption, and signed
IPA proof belong on RC/release or scheduled hardware, not every PR. A skipped hardware/signing leg must be
incomplete, never green.

### 8. Missed critical risks

Ranked:

1. **1.x semver/compatibility contradiction** — the audit notices the minor window but does not make its
   resolution a prerequisite for calling the release 1.0.
2. **Demand risk** — unique capability and some users do not prove a market proportional to the engineering
   cost. The RC should seek evidence; silence should cause maintenance mode, not more internal features.
3. **Gate enforcement truth** — release workflow coverage is materially narrower than the available gate
   catalog, and signed IPA can skip successfully.
4. **Support-matrix proof** — platform, x64, and MAUI claims need either representative proof or narrower
   wording.
5. **Security trust boundary** — unsafe interop/code generation is not a sandbox. Document trusted-input and
   build-execution assumptions; do not start a broad security campaign without a threat signal.
6. **Upstream cadence** — Xcode/Swift/.NET changes can force urgent work. A declared tested matrix and
   maintenance policy matter more than speculative adaptation.

Performance is under-measured but not a 1.0 blocker without a reported bottleneck. Windows host support is
not a credible goal while Xcode and Apple SDK tooling are authoritative macOS dependencies.

---

## 13. Recommendation to the owner

Ship a deliberate 1.0, but make it **narrower in features and stronger in contract** than the first audit's
checklist.

The project is not rotten. The feeling that it “never felt right” is better explained by an unbounded work
selection loop than by a failed architecture. Six months of issue-chasing created a system with unusually
strong evidence and unusually high cognitive cost. More archaeology will increase the second faster than the
first.

The sane sequence is:

1. decide what 1.x compatibility actually means;
2. eliminate or fail-close known silent trust failures;
3. prove the real package and IPA paths;
4. tier the package portfolio instead of perfecting it;
5. cut an RC and ask current users to break it;
6. ship stable if no blockers appear;
7. move to maintenance/external-input mode.

If the owner cannot accept major-only breaking changes for the Runtime/generated contract, ship 0.18 and
wait. If the owner can accept that promise, the remaining work is measured in **weeks of bounded owner time,
not another six-month architecture campaign**.

The direct answer is therefore:

> **Own it. Do not rewrite it. Stop expanding the definition of done. Freeze a real contract, fail closed,
> prove the packages, and ship.**
