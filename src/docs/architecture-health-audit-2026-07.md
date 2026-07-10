# Architecture & Product Health Audit (2026-07)

**Status**: Independent full-repo health assessment after the 0.17.0 release window.  
**Audience**: Owner + second-opinion reviewers (Claude / Codex / peers).  
**Scope**: Architecture, design, maintainability, quality gates, product maturity, 1.0 vs park.  
**Method**: Multi-agent deep dive over generator, runtime/SDK packaging, test/gate infrastructure, design docs, BindingAudit, git velocity, baselines — plus synthesis. Not a line-by-line code review of every file.  
**Companion docs**: `roadmap.md` (strategic posture + backlog), `Future/post-1.0-architecture-roadmap.md` (maintainability inventory), `BindingAudit/_SUMMARY.md` (shipped-package correctness), `next-release-remaining-work.md` (cycle-local ship work), `version-coexistence.md` (Runtime contract policy).

---

## 1. Executive take

This is a **real industrial interop product** solving a real, growing hole in the .NET-on-Apple stack. It is past “experiment” and into **late pre-1.0 tooling** — generator + runtime + MSBuild SDK + NuGet matrix + multi-platform gates + published third-party and Apple packages.

### One-line verdict

> **Solid late-0.x industrial tooling. Architecture is the right shape. Maintainability is expert + strong gates, not unmaintainable chaos. Do not rewrite. Do not open another broad architecture/audit campaign. Ship a deliberate 1.0 defined as contracted, trustworthy surface — then optimize for external inputs, not more internal archaeology.**

### Scores (honest, not aspirational)

| Dimension | Score | Read |
|---|---|---|
| Architecture fit for the problem | **8.5 / 10** | Right hybrid shape for Swift ABI → C# |
| Implementation maintainability | **6.5 / 10** | Industrial, scar-matured; mega-files + multi-site invariants; expert-sustainable |
| Shipping surface (Runtime / SDK / pack) | **7 / 10** | Production-capable pure-Swift PackageReference; TN2435 hygiene is a strength |
| Quality gates / operational maturity | **8 / 10** | Elite for solo ABI tooling; gate *catalog* larger than CI *enforcement* |
| 1.0 product readiness | **6.5 / 10** | Capability-ready; trust, contract freeze, and package depth still lag generator sophistication |

### What this is *not*

- Not a 3/10 prototype that should be abandoned or rewritten.
- Not a 9/10 mature platform with a frozen public contract and deep functional package tests.
- Not “too complex therefore wrong” — complexity here is mostly the tax of correctness against Swift ABI + Mono + NativeAOT.

---

## 2. Scale snapshot (calibrate the “too big” feeling)

Approximate production/test footprint at audit time (source only; exclude `bin`/`obj`/`artifacts` noise):

| Area | Rough size |
|---|---|
| Generator (`src/Swift.Bindings/src`) | ~217k LOC / ~428 files |
| Runtime (`src/Swift.Runtime/src`) | ~21k LOC |
| SDK (`Sdk.targets` / props) | ~4k LOC (dense, not sprawling) |
| BindingTests C# app + Swift fixtures | ~67k + ~36k LOC / ~340 Swift + ~350 C# domain files |
| Nuke / build gates (`build/`) | ~22k LOC |
| All unit/test project C# | ~310k LOC |
| Unit pass floor | **14,248** (`build/baselines/validation-baseline.json`) |
| BindingTests identity baseline (sim) | **~3,160 pass / 0 fail / ~37 skip** |
| Validation corpus | ~66 libraries; skip rate ~22.5% (mostly intentional buckets) |
| Design / Future / BindingAudit docs | ~58+ markdown files under `src/docs/` |
| Velocity | ~1,400 commits in the prior ~6 months |

This is a **small product company inside one repo**, not a hobby fork that grew mold.

---

## 3. Problem, uniqueness, and product shape

### Problem

.NET on Apple platforms historically interops via **Objective-C** (`dotnet/macios`, Objective Sharpie). Apple’s surface is migrating to **Swift-only** APIs (StoreKit 2, WeatherKit, large parts of MusicKit / CryptoKit / RealityKit, third-party SPM libs). The old path:

```text
Swift → manual @objc proxy → headers → Sharpie → hand-fixed C#
```

…does not scale and often **cannot** express modern Swift (async, generics, value types, protocols, result builders). Without a Swift ABI path, .NET apps lose access to the modern Apple surface year over year.

### What this ships

A **compiled framework → C# NuGet** pipeline:

- ABI JSON + demangle + type database + marshal/emit
- Generated `@_cdecl` / wrapper dylib where needed
- Managed runtime (ARC/SafeHandle, collections, existentials, reverse protocol dispatch)
- MSBuild SDK + templates
- ObjC / mixed-framework path (credible Sharpie replacement: no mass `[Verify]`)
- Prebuilt third-party + Apple framework packages in companion repos

Origin: Microsoft `dotnet/runtimelab` `feature/swift-bindings` (basic PoC). This fork is the productization.

### Uniqueness

**Very unique in practice.** Alternatives are weak or orthogonal:

| Alternative | Why it is not a substitute |
|---|---|
| Manual `@objc` shims + Sharpie | Does not cover Swift-only; high cost; Sharpie unmaintained |
| `dotnet/macios` only | ObjC-centric; modern Swift frameworks largely missing |
| Hand-written P/Invoke at framework scale | Unmaintainable |
| Per-library thin Swift bridges | Always available; this *automates* and packages that work |

Competitive risk is not “another open-source generator tomorrow.” It is **Microsoft eventually productizing Swift interop** or **Apple changing ABI/SDK packaging** — multi-year and not a substitute for a shipping NuGet generator today.

**Moat**: accumulated ABI/marshalling lore + gates + real library corpus — not a clever single algorithm.

**Caveat**: uniqueness of *capability* is high; uniqueness of *proven demand at scale* is still thinner than the engineering investment. The product is input-starved relative to its maturity.

---

## 4. Architecture assessment

### 4.1 Is the architectural philosophy right?

**Yes. For this problem it is the only serious shape.**

The chosen model is a **hybrid binding generator**, not pure P/Invoke and not a full second runtime:

1. **Direct Swift ABI** where blittable / CallConvSwift-safe  
2. **Generated Swift wrappers** (`@_cdecl`, async bridges, CSM closed specializations) where the CLR cannot express the ABI  
3. **Managed projections** for idiomatic C# (`string`, lists, optional, `Task`)  
4. **EveryProtocol / witness tables** for reverse dispatch (C# implements Swift protocols)  
5. **Fail-closed member drop** when projection is unsafe  
6. **Supplement facades** when the OS model is macro/widget-bound  
7. **Separate SPM → xcframework** tool (correct separation of concerns)

Why this is right:

- Swift’s type system is **not** a subset of C# generics; full fidelity is impossible. Honest products **drop, specialize, or facade**.
- Pure CallConvSwift everywhere collides with Mono non-blittable limits and async lifetime (exactly **4** confirmed upstream issues; everything else historically “ours”). Wrappers are load-bearing product, not a hack.
- Pure hand-written bridges do not scale to 60+ libs + Apple matrix.
- Pure source-gen without ABI metadata cannot get layout/symbols right for resilient types.

### 4.2 Pipeline stages

| Stage | Clean? | Notes |
|---|---|---|
| CLI / orchestration (`Program.cs`, `BindingsGeneratorCommand.cs`) | Procedural | Mode-heavy scripts (~2–2.5k LOC each); works; hard to test as stages |
| Parser (`SwiftABIParser` ~4.2k) + demangler + swiftinterface facts | Mostly | Decl trees + reconciliation census; still a monolith |
| TypeDatabase + strategy-chain resolver | **Strongest pure architecture** | Freeze-after-main-module, cross-module records, XML/Apple data |
| Marshaler (handlers, projections, environments) | OK / leaky | Projections + visitors coherent; handlers also own emission orchestration |
| Emitter (C# + Swift wrapper dual writers) | Dense | Reverse-dispatch stack is the densest surface |
| Post-process / wrapper compile / pack metadata | Leaky but load-bearing | Residual rewriters; “exists now vs will be produced” packaging traps documented |

There is **no intermediate EmissionPlan IR**. Validation, naming, symbol promotion, and string emission interleave. The project itself defers plan-vs-emit to post-1.0 (`Future/post-1.0-architecture-roadmap.md`) with a good litmus test: *does this prevent bad bindings or increase valid surface?*

### 4.3 Strengths (architecture)

- Right problem decomposition for Swift/.NET.
- Real domain models: `TypeDecl` tree, `TypeSpec`, `TypeRecord`, `ITypeProjection` + exhaustive visitors, `VtableLayout` / `VtableLayoutBuilder`, `MemberValidationPipeline`, TypeResolver strategies.
- Consolidation campaigns that are real, not vaporware:
  - Projected method keys → one core (`ProtocolSignatureHelper.BuildProjectedMethodKey`)
  - Reverse-dispatch layout → one `VtableLayout` model
  - Method emission gates → `MemberValidationPipeline`
  - Symbol promotion → env / side table, not mutating `MethodDecl.MangledName` (AF13)
  - Apple heuristics centralized in `AppleFrameworkRegistry` + JSON
- Fail-closed culture: skip reasons, reconciliation, ABI contract checker, wrapper strip tripwires, parity / API-manifest ratchets.
- Operational knowledge externalized (`.claude/rules/constraints.md`, design docs) — a second product, but a *written* one.
- Runtime ownership model (`PayloadConstructionSemantics`, ARC, exit guards) is production-grade.
- RuntimeContract three-way coupling (package minor ↔ emitted epoch ↔ NuGet range) is sophisticated for 0.x.

### 4.4 Top complexity hotspots

| Hotspot | Why it matters |
|---|---|
| `EveryProtocolEmitter.cs` (~7.3k) | Swift-side reverse dispatch; wrong membership/order vs C# = silent ABI corruption (device SIGSEGV class) |
| `ProtocolProxyEmitter.*` (~8.5k combined) | C# proxy / vtables / receivers / static init; must stay lockstep with EveryProtocol; fillability ≠ layout membership |
| `SwiftABIParser.cs` (~4.2k) | Root of “what exists to bind”; historical silent swallow risk mitigated by reconciliation |
| Closure / method / witness / async emitters | Everyday forward path; high fan-out into runtime |
| CLI + `SwiftWrapperCompiler` | Product reliability (archs, fingerprint, NativeReference) lives here more than in elegant IR |
| `SwiftMarshal.cs` (~2k, runtime) | Gravity well for generator + hand-written paths |

These are gravity wells **because the domain is dense**, not because nobody ever cleaned up.

### 4.5 `constraints.md` patterns: maturity or design fight?

**Both — maturity *from* fighting multi-copy bugs.**

Patterns like single-core projected keys, `VtableLayout` as sole layout oracle, AF13 symbol side tables, collision-suffix pre-reservation are **scar tissue that graduated into architecture**. That is healthy engineering after real failures.

They are also evidence the original design lacked:

- Clean separation of identity domains (projected-C# keys vs vtable slot keys — *correct* that both exist; dangerous if conflated)
- One membership oracle (now mostly fixed for reverse dispatch)
- Plan IR so validators and emitters cannot diverge

**Not over-engineered ceremony.** Residual smell: invariants are often **prose + tests**, not always type-system structure. Continuing consolidation (without rewrite) is the rational path.

---

## 5. Maintainability assessment

### 5.1 Verdict

**Maintainable by experts with strong gates. Not junior-friendly. Not textbook-clean. Not a rewrite candidate.**

Score **6.5 / 10**:

- Not 3–4: real layers, models, single-source consolidations, freeze, resolver strategies, massive tests/docs.
- Not 8–9: mega-files, multi-site invariants, static collectors, CLI script orchestration, leaky marshal/emit mean everyday changes still need expert memory and can fail only on NativeAOT.

### 5.2 Critical maintainability risks

| Severity | Risk |
|---|---|
| **Critical** | **Bus factor / cognitive load.** Correctness depends on multi-site invariants (async `CancellationToken` on three protocol paths; vtable layout vs fillability; projected-key vs slot-key domains; dual `{name}Buffer` paths; `ModuleEmissionContext` threading). Documented, but that documentation is a second product. One wrong “cleanup” → CS0111 or device-only SIGSEGV. |
| **Critical** | Mega reverse-dispatch files as gravity wells — hard to review, expensive to onboard. |
| **High** | No plan/emit separation — decisions and string emission interleave. |
| **High** | Marshaler/Emitter boundary fiction — same types do both; secondary gates re-check projected types. Sometimes defense-in-depth, sometimes drift risk. |
| **High** | Orchestration is a script (`GenerateBindings` / `BindingsGeneratorCommand`) — hard to test stages in isolation. |
| **High (1.0 product)** | Large public Runtime surface without stability tiers — hard to promise semver without `InternalsVisibleTo` / support assembly split. |
| **Medium** | Static / process-global collectors (`ReportCollector`, SwiftUI bridge collector) — parallelization friction. |
| **Medium** | Residual post-emission rewriters — footguns for packaging and “exists now vs will produce.” |
| **Medium** | Stale generator binary masking edits (codified in project rules; fingerprint mitigations exist). |
| **Medium** | Gate catalog enterprise-grade; packaging / device / full validate still partly tribal judgment + release checklist. |
| **Low–Medium** | Known incomplete edges that fail closed more often than silent (better than silent, still surface area). |

### 5.3 What is *not* true

- “We accidentally built spaghetti with no architecture.” **False.**
- “A clean rewrite would be half the size and just as correct.” **False.** Size is mostly ABI lore + dual Mono/NativeAOT truth.
- “More internal audits will unlock 1.0.” **False** for broad latent sweeps; **partially false** for shipped-package trust (see §7).

---

## 6. Runtime, SDK, and packaging

### 6.1 Runtime

Dense and battle-hardened, not chaotic sprawl:

- Native framework xcframework (not loose dylib — TN2435)
- ARC / SafeHandle / exit guards / finalizer-safe release
- Metadata / VWT / PWT
- `SwiftMarshal` + explicit `PayloadConstructionSemantics`
- Stdlib projections, existentials, EveryProtocol
- `SwiftFrameworkResolver`, `RuntimeContract`, Mono vs NativeAOT flavor switch
- Analyzers for dispose / retain-cycle

Coupling to the generator is **high but intentional** on a thin-ish contract (`ISwiftObject`, registration, marshal helpers). Runtime does not reference the generator. Risk for 1.0 is less “will the framework load?” and more “can we freeze a public surface?”

### 6.2 SDK / DX

| Path | Assessment |
|---|---|
| Happy path (`dotnet new` → drop xcframework → build/pack) | ~**8 / 10** — genuinely pleasant |
| App consumer of a packed binding | ~**8.5 / 10** — PackageReference + transitive Runtime |
| Mixed ObjC+Swift / multi-framework graphs | ~**5–6 / 10** — three consumption modes; easy to mis-wire without docs |
| SDK internals (`Sdk.targets` ~3.8k) | Correctness-dense; contributor-hostile; evaluation-order traps documented in comments |

Known durability gaps called out in cycle docs:

- Wrapper compile failures can surface as SWIFTBIND051 give-up while swallowing useful SWIFTBIND050 detail at normal verbosity.
- Runtime native does not flow across `ProjectReference` the way people expect (in-tree harnesses inject manually).

### 6.3 Packaging / release

**Among the strongest parts of the product:**

- Framework xcframework packaging; no reintroduction of loose `lib*.dylib` / `SwiftSupport/` injector
- Pack structural gates, PackGate, x64 gates, mixed-pack/direct, appstore-hygiene
- Release lanes (`release/sdk-*`, `release/apple-*`, combined, dry-run)
- VersionScope: pack should not mutate source; stamped versions asserted in nupkgs
- Apple train versioning independent of SDK lane

**Risks:**

| Risk | Severity |
|---|---|
| App Store hygiene / mixed-pack / full pack-gate often **opt-in**, not every PR | Medium (process) |
| RuntimeContract floor **fails open** if not raised on a real contract break | High (process) |
| No `PackageValidationBaselineVersion` / ApiCompat yet | Medium |
| Multi-binding apps must share a Runtime minor (`NU1107` otherwise) | Medium (product) |
| 0.x → 1.0 epoch jump needs explicit floor policy | High at 1.0 cut |
| Committed prebuilt runtime xcframework can drift from `swift/` sources | Medium |

**Production readiness of shipping surface: ~7 / 10** — ship 0.x carefully to real apps; freeze contract before calling 1.0.

---

## 7. Quality gates and the “input-poor” thesis

### 7.1 Gate pyramid

```text
RELEASE / HEAVYWEIGHT (mostly local or release-adjacent)
  pack-gate · x64-* · mixed-pack/direct · appstore-hygiene
  behavior-tier · multi-platform runtime · nuke validate

CI / MERGE (enforced)
  nuke test · binding-tests --compile-only · sim runtime (tier-2)
  blast-radius · pack smoke · Issue-1 attribution

EVERYDAY INNER LOOP
  nuke test · binding-tests [--compile-only | default sim]
  --skip-regen for runtime-only C#

UNIT / ANALYZER
  Generator unit (~14.2k floor) · Runtime lib · Analyzers
```

**Correct pyramid for an ABI product.** Unit tests catch logic; BindingTests catch wrong P/Invoke / marshalling; validate catches real-library surprise; pack gates catch nupkg/IPA shape.

### 7.2 What is excellent

- Fail-closed compile-only (generator exit, wrapper, parity, API-manifest, wrapper-strip).
- Pass floors (unit + runtime identity) catch silent test deletion.
- Platform realism: Mono JIT sim vs NativeAOT device; “crashes are ours until proven upstream” operationalized.
- ABI coverage grid (52 cells: 41 expect-green, 4 low-priority, 7 by-design gray) manufactures thin corners real libraries rarely hit.
- Knowledge written down (CLAUDE.md, scoped rules, BindingTests guide, roadmap posture).
- BindingAudit of 26 shipped bindings is unusually honest product-level QA.

### 7.3 What is fragile / high-burden

1. **Gate sprawl vs CI enforcement gap** — packaging and multi-runtime correctness live partly in owner memory + release checklist.
2. **`nuke validate` still necessary** — SurfaceArea migration scaffolding exists; directory effectively empty; validate retirement criteria not met.
3. **Tribal density** — `constraints.md` is excellent *if* always loaded; second product to maintain.
4. **Device-only signals** — some lifetime/native-runtime paths only green on device; device not in CI.
5. **Artifact / workspace weight** — large `artifacts/` and BindingTests outputs.
6. **Latent inventories** — high value as anti-rediscovery log; high burden if treated as open work list.

### 7.4 “Input-poor, not bug-poor” — refined

Roadmap posture (post-0.14):

> Nearly every open roadmap item is “no active repro / not reached by any current corpus library.” Highest-value remaining bug source is **new real consumer inputs**, not internal sweeps.

**~80% coherent as prioritization rule.** Supporting evidence:

- ~14k unit + ~3.2k BindingTests with ~0 fails
- Latents repeatedly “no emission site”
- Async-emitter merge correctly rejected after audit (divergence arrived via new shapes, not structure review)
- ABI grid closed many synthetic corners

**~50% coherent as “remaining work is only external.”** BindingAudit shows bugs/silent failures on **already-shipped** inputs:

1. **EveryProtocol proxy skips** → compile OK, runtime throw / silent dead delegates (e.g. RealityFoundation `Materials`, RoomPlan view delegate, BlinkIDUX analyzers).
2. **CSM / typed-struct concretization gaps** → CryptoKit NIST ECDSA unreachable; MusicKit library-read `.items` dark.
3. **Existential projection gaps** on real APIs.
4. **Universal weakness: package test depth** — construction/metadata, not headline functional flows.
5. Cycle work (ObjC skip reporting blind spots, mixed pack V-1 proof) shows product debt independent of novel ABI shapes.

**Accurate thesis:**

> We are **input-poor for novel ABI shapes**, but **not bug-poor for product-correctness of published packages**.  
> Another whole-repo architecture audit has low ROI.  
> A **shipped-package correctness pass** (EveryProtocol, headline flows, honest mixed/ObjC reports) still has high ROI.

---

## 8. From-scratch rewrite: buy vs cost

### What a rewrite might buy

- Cleaner stage model (plan vs emit, no static collectors, fewer post-emission rewriters)
- Single type IR / projection dispatcher instead of multi-emitter folklore
- Less “constraints.md as load-bearing product” cognitive load
- Possibly cleaner ObjC+Swift report unification from day one

### What it would cost (dominates)

- **Years of ABI truth**: CallConvSwift vs cdecl, VWT, existential containers, reverse dispatch slot layout, CSM, async bridges, frozen vs resilient, NativeAOT vs Mono
- **Gate re-creation**: BindingTests, validate corpus, pack/mixed/appstore, Apple portfolio packaging, TN2435 lessons
- **Regression risk**: silent ABI skew is the trust-killer; rewrite guarantees a long “looks green, dies on device” window
- **Opportunity cost**: months without real consumer packages, depth tests, or adoption work

`Future/post-1.0-architecture-roadmap.md` already lists ~150k LOC of *real* maintainability debt and correctly says most of it **does not increase valid surface**. That inventory is the rewrite argument’s best case — and it still fails the pre-1.0 litmus test.

### Verdict

**Rewrite is strategic malpractice at this stage.** Incremental strangulation of the worst seams *after* 1.0, only when a consumer bug or Swift compiler break forces it (SwiftSyntax / demangle graduation path already chosen), is rational. This fork *was* the rewrite relative to Microsoft’s abandoned PoC; doing it again would throw away the only moat.

---

## 9. Where we are toward 1.0

### Already 1.0-grade

- End-to-end pipeline that ships real NuGets
- Large slice of Swift projected idiomatically (async, generics, protocols, collections, closures, existentials, mixed ObjC)
- Production-grade ARC / packaging / dual Mono+NativeAOT support
- SDK happy path for binding authors
- Explicit non-goals (result builders, full PAT, AppIntents authoring)
- Architecture gameplan for *generator quality* treated as DONE; remaining inventory is maintainability

### Not yet 1.0

| Gap | Why it blocks calling 1.0 “done” |
|---|---|
| Silent runtime failures on compile-green bindings (EveryProtocol class) | Trust: compile-green ≠ usable |
| Package test depth thin | False confidence on shipped libs |
| Public API / contract freeze unfinished | Semver promise hard |
| Floor fails open if not raised | Safety net becomes false confidence |
| Mixed pack V-1 on real FB/MapLibre still “claim not fact” until consumer leg green | Packaging proof |
| Bus factor / multi-hour release ritual | Operational risk under fatigue |

### Explicitly not required for 1.0

- Full PAT / deep generic signature fidelity
- Result builders / composing SwiftUI trees from C#
- AppIntents authoring, full TipKit DSL
- Capability-typed projection model, async-emitter merge, PipelineContext / plan-vs-emit IR
- Retiring `nuke validate`
- ApiCompat baseline (revisit *at* 1.0, not as a pre-blocker for all other work)
- Emptying the roadmap latent table

### Why “straying from 1.0” felt right for a while

Real issues kept appearing; declaring 1.0 while reverse-dispatch and packaging were still settling would have been dishonest. At 0.17 the **pattern has shifted**: remaining high-value work is mostly **product trust and packaging proof**, not “the architecture is wrong so we cannot ship.”

---

## 10. What 1.0 should mean here

1.0 should **not** mean “Swift type system fully projected.”

It should mean:

1. Consumers can bind third-party xcframeworks and key Apple Swift frameworks with a **documented, stable tool**.
2. **What emits is trustworthy** — no silent dead delegates, no fake getters that always throw, honest reports for Swift *and* ObjC.
3. Headline paths on packages you ship are **behavior-tested**, not only constructed.
4. Non-goals are **public and stable**.
5. Version/runtime contract is **deliberate** (floor policy; minor window; what 0.x may load).
6. Architecture debt is **deferred by policy**, not denied.

---

## 11. Proposals: focus for the next phase

### 11.1 Do not

- Launch another broad internal latent / R1–R6-style archaeology expecting yield.
- Merge async emitters or introduce capability-typed IR “for purity.”
- Expand Apple portfolio for vanity.
- Chase emit-rate % against intentional SPI/Codable/Any/PAT buckets.
- Rewrite from scratch.
- Put full `nuke validate` or device on every PR unless hardware/time budget is bought deliberately.
- Grow unit-test *count* as a primary goal (14k floor is already a strong logic net).

### 11.2 Do for a deliberate 1.0 (priority order)

#### A. Trust before features

1. **EveryProtocol / reverse-impossible proxies** — emit working reverse dispatch, **or fail closed** so consumers never get compile-green silent-dead APIs. Highest-value BindingAudit theme.
2. Any **masked real marshalling bug** still sitting as a package `Skip` → red BindingTests → fix (and grep siblings).
3. **Mixed bindings: ObjC drops visible** in the same binding report / triage as Swift (cycle item A1 class of work).
4. Close **standard ObjC type-mapping holes** that silently drop common public members (JSON / URLSession / etc.).

#### B. Headline unlocks only where the package claim is half-true

Rank by *shipped-package headline lies*, not by emit-rate:

- CryptoKit ECDSA + byte export if CryptoKit is sold as “usable crypto”
- MusicKit library-read `.items` if library browsing is claimed
- Bounded existential-in-container work only where it unblocks real headline APIs

Do **not** try to finish all BindingAudit gameplan sessions before 1.0.

#### C. Ship mechanics that turn claims into facts

- MapLibre pure-ObjC PackageReference consumer on sim **and** device
- Facebook mixed kits real pack consumption (`--mixed-pack` / real V-1)
- App Store hygiene as a **hard release gate**, not memory
- Wiki Known Limitations aligned with deliberate non-goals
- **Owner decision**: RuntimeContract floor at 1.0 — compatibility reset (`MinimumSupportedGeneratedVersion = 1000`) vs keep late 0.x loadable

#### D. One functional test per shipped headline library

Nuke image load, Lottie play, StoreKit dry-run where possible, one delegate round-trip that *fires*, CryptoKit KAT if claimed. Metadata-only is not a 1.0 story.

#### E. Lower the barrier to real inputs

This is the correct reading of “input-poor”:

- Shortest path: “bind my SPM/xcframework”
- Public examples of *failures* and skip reports, not only success demos
- Treat external issue reports as the next validation tier

#### F. Operational hygiene (not more gates — better enforcement)

1. Encode release-critical opt-ins as a single composed target (e.g. `nuke release-gates`) or schedule them on `release/**` / weekly: pack-gate, appstore-hygiene, tier-1 validate canaries.
2. Keep PR CI lean (current shape is right for solo velocity).
3. Promote every validate surprise into BindingTests / SurfaceArea **in the same change** (already policy; needs execution volume).
4. Treat roadmap latents as **anti-rediscovery inventory**, not unfinished work, unless a consumer or validate red opens them.

### 11.3 Park until post-1.0 or a red repro

- Async-emitter consolidation, capability-typed model, Type IR under TypeResolver
- PipelineContext / plan-vs-emit / full post-processor strangle
- Most CSM edge filter polish without emission sites
- Performance benchmarks, API snapshot tooling, tvOS device runner
- Broad KeyPath / property-wrapper surface
- Full retirement of `nuke validate`
- Mechanical decompositions listed in `Future/post-1.0-architecture-roadmap.md`

### 11.4 Park forever (unless a customer pays)

- AppIntents authoring, full TipKit DSL, app-defined PAT conformers
- FinanceKit / entitlement-gated frameworks until someone pays the entitlement cost
- Composing SwiftUI view trees from C# (result-builder wall)

### 11.5 Park vs wait-for-feedback vs push 1.0

| Option | Verdict |
|---|---|
| Park indefinitely | **Premature.** Users exist; capability is unique; parking freezes a lead without harvesting it. |
| Only wait for feedback | OK as background mode; **insufficient alone** — silent-failure classes and unproven mixed packs stay landmines. |
| Push feature-complete 1.0 | **Wrong.** Re-enters months of archaeology. |
| **Ship a deliberate 1.0** | **Recommended.** Contracted surface + trust fixes + pack proof + documented non-goals; then slow down and let external inputs drive work. |

---

## 12. Suggested 1.0 exit criteria checklist

Use as a discussion artifact for second opinions. Owner can mark items done/deferred.

### Trust

- [ ] EveryProtocol silent-dead class closed or fail-closed on affected shipped APIs
- [ ] No known marshalling corruption masked as test `Skip` on published packages
- [ ] Mixed ObjC+Swift binding report includes ObjC skip triage
- [ ] Wiki Known Limitations matches deliberate non-goals (including AppIntents shell, result builders, PAT)

### Packaging / ship proof

- [ ] At least one pure-ObjC third-party nupkg consumed via single PackageReference on sim + device
- [ ] At least one mixed ObjC+Swift multi-kit graph consumed via PackageReference on sim + device
- [ ] `nuke binding-tests --appstore-hygiene` green on the release cut
- [ ] PackGate / mixed-pack green for any packaging policy change in the 1.0 window

### Contract

- [ ] Written 1.0 stability promise (what is public API vs generator infrastructure)
- [ ] RuntimeContract floor decision recorded and coded (reset vs keep 0.x)
- [ ] Patch-additivity policy remains (and ApiCompat baseline decision explicit even if deferred)
- [ ] Multi-binding apps pin one Runtime minor — documented as intentional

### Package depth

- [ ] One functional behavior test per headline shipped package (not metadata-only)
- [ ] CryptoKit / MusicKit / RoomPlan / RealityFoundation *claims* match what tests and docs assert

### Process

- [ ] Single machine-readable release checklist / composed Nuke target for ship gates
- [ ] PR CI stays lean; release/schedule runs packaging + canary validate
- [ ] Roadmap latents remain inventory, not 1.0 blockers

### Explicit non-goals locked for 1.0

- [ ] No full PAT fidelity
- [ ] No result-builder / SwiftUI tree composition from C#
- [ ] No AppIntents authoring product
- [ ] No rewrite / plan-vs-emit as a 1.0 gate

---

## 13. Questions for second-opinion reviewers (Claude / Codex)

Please challenge or confirm:

1. **Rewrite vs evolve** — Is the “rewrite is malpractice” conclusion correct, or is there a narrow greenfield subsystem worth extracting (e.g. reverse dispatch only)?
2. **1.0 definition** — Is “trustworthy contracted surface” the right bar, or should 1.0 wait for BindingAudit Tier-1 gaps (CryptoKit ECDSA, MusicKit items, etc.)?
3. **EveryProtocol priority** — Agree this is the top trust fix, or is fail-closed-only enough for 1.0 without expanding reverse-dispatch capability?
4. **Input-poor thesis** — Agree with the 80%/50% refinement, or is another internal audit still justified?
5. **Runtime API freeze** — Prefer `InternalsVisibleTo` / support assembly split before 1.0, or document “generator contract is public” and freeze the whole surface?
6. **Floor policy at 1.0** — Reset to epoch 1000 (reject 0.x bindings) vs keep floor at 16 / late 0.x?
7. **Gate automation** — Is “compose release-gates + keep PR CI lean” the right split, or should device/validate move into CI sooner?
8. **Missed critical risk** — What did this audit underweight (security, performance, Windows host, MAUI multi-TFM, upstream .NET timeline, demand risk)?

---

## 14. Synthesis for the owner

You asked, after six months and 0.17.0: park, wait for feedback, or push 1.0 — and whether the system is non-maintainable or better rewritten.

**Confirmation:**

1. **Architecture is sound** for an extremely hard problem.  
2. **Implementation is dense but intentional** — industrial, scar-matured, not accidental spaghetti.  
3. **Maintainability is “expert + strong gates,”** not “junior-friendly clean core.” Real cost; not a reason to rewrite.  
4. **From-scratch would not be better** at this maturity; it would burn the moat.  
5. **You are not stuck because the tool is rotten.** You are at the natural inflection of a successful interop stack: generator sophistication has outrun product packaging, trust hardening, and external adoption.  
6. **The right move is a lean, deliberate 1.0** — not more months of “one more subsystem,” and not parking the only serious Swift/.NET binding generator in the ecosystem.

**Single sentence:**

> Stop mining the mine; shore up the product you already dug out; put it in customers’ hands; let their inputs pick the next tunnels.

---

## 15. Audit metadata

| Field | Value |
|---|---|
| Date | 2026-07 |
| Trigger | Post-0.17.0 “what next / is this maintainable?” owner question |
| Primary author | Grok (xAI) full-repo multi-agent audit |
| Intended next step | Claude + Codex second opinions; owner decision on 1.0 checklist |
| Non-goals of this doc | Not a commit plan; not a design for any single feature; not a replacement for `roadmap.md` |

When second opinions land, fold durable agreements into `roadmap.md` strategic posture and (if accepted) a short `1.0-exit-criteria.md`; leave disagreement notes in git history rather than bloating this file forever.
