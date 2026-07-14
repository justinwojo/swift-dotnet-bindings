# Prior-Art Index — Deep Audit 2026-07

**Wave**: 0 (M0-E)  
**Date**: 2026-07-15  
**Policy** (orchestration): Roadmap and prior audits are **already-known**. Re-open only with **new reachability** (emission site, consumer repro, or code drift). Do not rewrite BindingAudit wholesale; delta only when forced.

Format per entry: **ID | Title | Path | Date/status | Do not re-chase? | Reuse for which deep-audit waves?**

---

## A. BindingAudit (per-library surface, June 2026)

| ID | Title | Path | Date/status | Do not re-chase? | Reuse for waves |
|---|---|---|---|---|---|
| **BA-SUM** | Binding Audit — Synthesis & Index | `src/docs/BindingAudit/_SUMMARY.md` | 2026-06-27; static audit of 26 shipped bindings | **Yes** for per-library coverage % and headline usability — unless generator fix claims to close a listed P0 | W7 G1 (degrade), W8/M4 (test depth), synthesis package claims; **not** W1 ABI re-audit of every lib |
| **BA-METH** | Binding Audit — Methodology & Rubric | `src/docs/BindingAudit/_METHODOLOGY.md` | 2026-06-27; coverage / C# quality / test-depth rubric | **Yes** as rubric; don’t invent a parallel grading scheme | Any consumer-surface delta; methodology consistency (L5) |
| **BA-LIBS** | Per-library audits (26 files) | `src/docs/BindingAudit/{Lottie,Nuke,StoreKit2,CryptoKit,RealityFoundation,RoomPlan,MusicKit,AppIntents,Stripe,…}.md` | 2026-06-27; one file per binding | **Yes** for closed findings; **delta only** for OPEN P0s (EveryProtocol Materials, RoomPlan view delegate, label collapse residuals, MusicKit library Items, etc.) | W7 G1 “compile but dead”; package claim honesty; never full re-walk unless emission forced |
| **BA-GAME** | BindingAudit Gameplan | `src/docs/BindingAudit/Gameplan.md` | Companion plan for audit execution | Historical process; don’t re-run full campaign | Reference only |

**Headline already-known themes from BA-SUM** (do not re-discover as novel P0s):
1. Pipeline healthy; low % often intentional exclusions / accounting.
2. **EveryProtocol proxy skips** → compile-but-dead (throws / silent callbacks) is highest-risk class.
3. One masked Skip (Stripe `AppInfo` Optional\<ObjC\>) was **RESOLVED** generator-side.
4. Universal weakness = **test depth**, not raw coverage.

---

## B. Binding surface audit (delta + packaging, July 2026)

| ID | Title | Path | Date/status | Do not re-chase? | Reuse for waves |
|---|---|---|---|---|---|
| **BSA-00** | Executive summary | `src/docs/binding-surface-audit/00-executive-summary.md` | 2026-07-11 | **Yes** for July delta verdict; still open P0 list is live | W7 G1, synthesis |
| **BSA-M** | Methodology | `src/docs/binding-surface-audit/00-methodology.md` | 2026-07-11 | Yes (process) | Delta methodology |
| **BSA-01** | Delta revalidation of BindingAudit top findings | `src/docs/binding-surface-audit/01-delta-revalidation.md` | 2026-07-11 | **Yes** for June→July status of top findings | Only re-check if code after 2026-07-11 claims fix |
| **BSA-02** | Project config & packaging | `src/docs/binding-surface-audit/02-project-config-and-packaging.md` | 2026-07-11 | Partial — packaging may drift; TN2435 direction is settled | M2 / Wave packaging; appstore-hygiene |
| **BSA-03** | Internal binding testing corpus | `src/docs/binding-surface-audit/03-internal-binding-testing.md` | 2026-07-11; ~15 OSS libs | **Yes** for smoke-depth diagnosis; green sim/device ≠ product workflow | W8 test honesty; input-poor thesis |
| **BSA-04** | C# quality & structure | `src/docs/binding-surface-audit/04-csharp-quality-and-structure.md` | 2026-07-11 | Yes unless naming/ergonomics work reopens | L2 quality; not ABI |
| **BSA-05** | Ranked recommendations | `src/docs/binding-surface-audit/05-recommendations.md` | 2026-07-11; P0 EveryProtocol, P1 labels/MusicKit/CryptoKit KAT | Treat OPEN items as backlog seeds, not new discoveries | Synthesis backlog; W7/W1 only if new reachability |
| **BSA-06** | Post-run verification | `src/docs/binding-surface-audit/06-post-run-verification.md` | 2026-07-11 | Yes | Close-out checklist pattern |

---

## C. Roadmap buckets (do not re-row)

**Path**: `src/docs/roadmap.md`  
**Status**: Living; strategic posture post-0.14.  
**Do not re-chase?** **Yes** for listed medium/low/latent/blocked rows unless a **new emission site** or consumer report appears.

| Bucket | Summary (themes, not every row) | Reuse for waves |
|---|---|---|
| **Strategic posture** | Input-poor not bug-poor; ABI grid graded (41 expect-green / 4 low-pri / 7 gray); async-emitter merge **rejected** | W0 framing; ban re-proposing rejected merges (C1) |
| **Medium priority** | LocalNameRegistry; multi-PAT boxing; mixed-indirect generic tuples; static protocol label residual; CSM filter sugar/composition/dependent-member; Optional\<ObjC value\> closure args; ObjC protocol Phase 2 + `[any objcP]` collections; many “trigger to revisit” CSM compile reds | A6 (CSM), A4/A5 (closures/existentials), L2 ObjC, G1 |
| **Low priority** | Perf benchmarks; API snapshot; tvOS device runner; ApiCompat baseline decision; UnsupportedClosure remainder; Result\<T,E\> params; multi-protocol compositions; value-type generic conformers; SwiftUI beyond bridge (declined KeyPath DSL); weak/unowned; inout C# writeback; capability-typed projection (deferred); etc. | L3 perf; post-1.0 only |
| **Latent (zero emission site)** | Async CreateAsync parity; closure/async fan-out gap; owned existential collection fall-throughs; protocol-emitter CS0111/CS0108 latents; ObjC ApiDefinition dedup cosmetic; R1–R6 log points | **Do not file as P0** without max-case fixture proving reachability; consult before re-tracing |
| **Blocked (upstream only — exactly 4)** | (1) Mono JIT `!ji->async` CallConvSwift; (2) non-blittable CallConvSwift rejection; (3) Mono Set.insert DONE_BLOCKING; (4) Mono Catalyst x64; (+ SafeHandle async lifetime comment) | Skip taxonomy honesty (M4); never invent 5th upstream without standalone repro |

**Cross-ref latents log**: deleted `regression-audit-followups.md` content lives in git history + SB-Backup (below).

---

## D. 1.0 decision record (signed)

**Path**: `src/docs/1.0-decision-record.md`  
**Status**: **SIGNED 2026-07-10** — effective outcome **0.18 maintenance path (D1 = NO)**.

| ID | Decision | Outcome | Do not re-chase? | Waves |
|---|---|---|---|---|
| **D1** | 1.x major-only contract breaks | **NO** — stay 0.x / ship 0.18 path | **Yes** until owner reopens 1.0 | Synthesis branding/claims only |
| **D2** | RuntimeContract floor → 1000 at 1.0 | **MOOT** (floor stays 16) | Yes | Runtime contract tracks only if 1.0 reopened |
| **D3** | Four-tier package support labels | **NO** — no public tier labels; uniform claims | Yes — don’t re-propose tier taxonomy as required work | BSA/BA claims framing |
| **D4** | Certified platform matrix | **Amended** — no formal published matrix; iOS primary; gates keep running | Yes for “must publish certified matrix” | M0-C / release gates |
| **D5** | RC soak / budget caps | **MOOT** on 0.18 path | Yes | — |

Three-way architecture review adjudication (2026-07-09) docs were **deleted 2026-07-13** (git history / owner decision) — do not hunt missing files as process failure.

---

## E. Post-1.0 architecture roadmap

**Path**: `src/docs/Future/post-1.0-architecture-roadmap.md`  
**Status**: Reference inventory after 1.0; litmus = exposes binding failure earlier / prevents bad emission / increases valid surface.

| Theme | Deferred items (buckets) | Do not re-chase as 1.0 P0? | Waves |
|---|---|---|---|
| **Graduated already** | `libswiftDemangle` swap; SwiftSyntax producer | N/A — done/graduated | A8 historical only |
| **Pipeline structure** | PipelineContext/static collectors; IPipelineStage; Plan vs Emit IR; projection-only marshaler | **Yes** as “must do now” | C1 simplification inventory (L4) only |
| **Diagnostics** | Full SARIF / --explain / unified ids | Yes for 0.18 blocking | G1 reporting incremental only |
| **Type IR / resolvers** | TypeId underneath TypeResolver | Yes | M3 if resolver bugs found |
| **Post-emission strangle** | Full retirement of co-gater/postprocessor family | Yes — internal-receiver already emission-gated; postprocessor stays by design | A8/M2 constraints |
| **Test rebuild** | Substring→plan assertions; MockCommandRunner removal; domain taxonomy | Yes as big-bang; **honesty subset is in-scope for W8** | W8 L2 |
| **Runtime cleanup** | ExistentialContainer0..8; Mono/NativeAOT factory consolidation; buffer pooling | Yes | A3 optional |
| **Misc** | IGeneratedSwiftObject split; dead SwiftTypeInfo deletion; AppleTypesManifest carve-out | Yes | — |

---

## F. Design docs (`src/docs/Design/`)

Verified-against-implementation design notes (2026-06 cleanup). One line each.

| ID | Title | Path | One-line | Waves |
|---|---|---|---|---|
| **DES-README** | Design index | `Design/README.md` | Index of current design docs | W0 |
| **DES-STRUCT** | Binding structs | `Design/binding-structs.md` | Frozen vs non-frozen / payload kinds | A2 |
| **DES-CLOS** | Binding closures | `Design/binding-closures.md` | Callback + `@_cdecl` wrapper architecture | A4 |
| **DES-UMRBP** | UnsafeMutableRawBufferPointer | `Design/unsafe-mutable-raw-buffer-pointer.md` | Span\<byte\> projection | A2/A1 |
| **DES-VAR** | Binding variables | `Design/binding-variables.md` | Module globals + observers | A1 low |
| **DES-TDB** | Type database | `Design/binding-typedatabase.md` | TypeDatabase design | M3 |
| **DES-VWT** | Value witness table | `Design/binding-value-witness-table.md` | VWT layout/access | A2/A3 |
| **DES-ASYNC-NF** | Async non-frozen types | `Design/async-non-frozen-types.md` | Async + non-frozen params | A7 |
| **DES-MEM** | Memory management | `Design/memory-management.md` | Ownership at Swift–C# boundary | A3 |
| **DES-REV** | Reverse-dispatch lifetime | `Design/reverse-dispatch-lifetime.md` | Design B2 EveryProtocol lifetime/identity | A3/A5 |
| **DES-DEM** | Demangling | `Design/demangling.md` | Symbol demangle | A8 |
| **DES-DEM-SPIKE** | Demangle replacement spike | `Design/demangling-replacement-spike.md` | Replacement spike **NO-GO** | **Do not re-propose** |
| **DES-SYMB** | Symbols outside ABI JSON | `Design/retrieving-symbols-outside-abi-json.md` | TBD / extra symbols | A8/M2 |
| **DES-GRID** | ABI coverage grid | `Design/abi-coverage-grid.md` | Living 52-cell grid artifact | W8, A1–A4 |
| **DES-PORT** | Apple framework portfolio | `Design/apple-framework-portfolio.md` | Which Apple frameworks / priority | Synthesis claims |
| **DES-ASTRAT** | Apple framework binding strategy | `Design/apple-framework-binding-strategy.md` | Strategy for Apple modules | M2/M3 |
| **DES-ATYPES** | Apple Swift types architecture | `Design/apple-swift-types-architecture.md` | Supplement / typed remaps | M3, AppleSupplement tests |

---

## G. Constraints trap categories (`.claude/rules/constraints.md`)

**Path**: `.claude/rules/constraints.md`  
**Status**: Living load-bearing traps for generator source.  
**Do not re-chase?** Trap *existence* is known; **code drift** must still be verified (Wave 10 / C2). Do not re-derive the same trap from scratch as a “new finding.”

| Category | Representative traps | Waves |
|---|---|---|
| **Swift 6 / memory ops** | BitwiseCopyable `storeBytes`; initializeMemory for ARC types | A2/A3 |
| **Closure lifetime** | Optional closures always escaping; two-layer gate (emit vs cdecl) | A4 |
| **ABI naming** | ObjC nested enums; Tj dispatch thunks; ModuleEmissionContext threading | A1/A8 |
| **Keys / dedup / overrides** | Projected-key one-core (AF05); vtable layout single model; collision-suffix pre-reserve | A5/C2 |
| **Projection parity** | ITypeProjection visitor exhaustiveness; optional ObjC-bridged parity; AppleFrameworkRegistry SSOT | M3/A5 |
| **Bool / nint** | MarshalAs U1; property nint narrow vs method return non-narrow | A1 |
| **Protocols / extensions** | ProtocolExtensionEmitter timing; generic extension dual TypeMetadata; iterator Arc.Retain | A5/A6 |
| **SwiftUI** | Two-path suppression | M1 |
| **Packaging / wrapper arch** | Consumer targets “will be produced”; shared arch decision + try/catch/finally lipo | M2 |
| **Emission symbols** | AF13: never mutate MethodDecl.MangledName; EmissionSymbol side table | A1/C2 |
| **Validation hygiene** | Branch validation cache; stale GeneratorDll | M0-C / all regen |

---

## H. Architecture review backup (outside repo)

**Root**: `/Users/wojo/Dev/SB-Backup-Docs/architecture-review-2026-06/`  
**Accessible**: **Yes** (read 2026-07-15).

| ID | Title | Path | Date/status | Do not re-chase? | Waves |
|---|---|---|---|---|---|
| **AR-MAIN** | Architecture Review June 2026 | `…/architecture-review-2026-06.md` | June 2026; 67 findings, ~52 shipped | **Yes** for shipped findings; remaining are planned | C1, L4 simplification; consult before mega-file refactors |
| **AR-SESS** | Remaining session plans index | `…/sessions/README.md` | Active plans: S08-S21-remaining, async-result-carrier-leak; many parked | Open work inventory — implement only if owner promotes | A2 (F44 ABI fixtures), A7 (carrier leak), C2 |
| **AR-S08…** | Session design archives | `…/sessions/S07a…S22…`, `AF05…AF13…` | As-built designs for completed sessions | **Yes** — contracts live in constraints.md + code | C2 verify drift only |
| **AR-RLOG** | R1–R6 refuted/clean log | Referenced from roadmap latent table + backup | Verified-clean / refuted mechanisms | **Mandatory consult** before re-chasing R1–R6 leads | All deep waves |

**Open tails to know about (not re-discover)**:
- S08b F44: SwiftValueLayout consumer reroute + frozen-struct ABI fixtures still missing.
- S08a F9: CdeclLoweringDescriptor fields populated-but-unread (drop vs finish Leg B).
- S21 F6: byte-snapshot versioning lock test.
- async-result-carrier-leak: frozen stdlib container async `+1` leak plan.
- Parked: F57 post-1.0 scalability; AF05 legacy async receiver CT edge; S18 decl-factory step 10; AF13 parser write-backs.

---

## I. Codebase-audit workflow tracks

**Path**: `.claude/workflows/codebase-audit.js`  
**Status**: Parameterized track skeleton; deep-audit 2026-07 **adapts** these into waves (orchestration).  
**Output convention** (workflow): `src/docs/audits/Track-*.md` — may be empty/partial; deep-audit writes under `src/docs/deep-audit-2026-07/`.

| Track ID | Title | Hunt focus | Deep-audit wave mapping |
|---|---|---|---|
| **A1** | P/Invoke ABI contract + x64 thunks | CallConv, sret, SwiftSelf, bool, metadata order | Wave 1 A1 |
| **A2** | Struct layout / VWT | Frozen/resilient, optional EI, tuples | Wave 1 A2 |
| **A3** | ARC / ownership / lifetime | passRetained, SafeHandle async, VWT misuse | Wave 1 A3 |
| **A4** | Closures / reabstraction | Optional-as-escaping, GCHandle, layer-1 vs layer-2 gates | Wave 1 A4 |
| **A5** | Existentials / witness dispatch | Composition size, PWT, Any fallbacks | Wave 1 A5 |
| **A6** | CSM / generics / PAT | Arity, Self, PWT, multi-PAT | Wave 1 A6 |
| **A7** | Async / throws / error carrier | GCHandle, error ownership, path divergence | Wave 1 A7 |
| **A8** | Parser / demangler fidelity | Internal classification, mangling, ABI JSON drift | Wave 1–2 A8 |
| **C1** | Maintainability hazard map | Mega-files, dual oracles, impl-assert tests | Wave 7/10 L4 |
| **C2** | Invariant drift / dedup keys | constraints.md vs code; WasEmitted; key sites | Wave 10 / C2 |
| **M1** | SwiftUI bridge matrix | Async inference, platform gating | Mid waves M1 |
| **M2** | Wrapper / SDK / packaging / arch | Fingerprints, NativeReference, arch paths | M0-C + M2 |
| **M3** | TypeDatabase / projection parity | Registry/XML/factory disagreement | M3 |
| **M4** | BindingTests skip taxonomy & matrix | Skip honesty; compile-only vs runtime | **Wave 8** (primary) |
| **L1** | Docs / roadmap drift | Stale known-issues | End-wave L1 |
| **L2** | ObjC interop pipeline | Availability, mixed binding | L2 |
| **L3** | Performance / API-drift readiness | Benchmarks, surface ratchet | L3 post-1.0 |

---

## J. Other in-repo docs (brief)

| ID | Path | Role | Do not re-chase? | Waves |
|---|---|---|---|---|
| **UP-README** | `src/docs/Future/upstream-issues-README.md` | Filing guide for 4 upstream issues | Yes for “is this upstream?” process | M4 skips |
| **UP-01..04** | `src/docs/Future/upstream-issue-0{1,2,3,4}-*.md` | Per-issue evidence | Yes — confirmed set | Skip taxonomy |
| **VER-COEX** | `src/docs/version-coexistence.md` | Runtime version range / NU1107 | Yes policy; D1 deferred | Release / contract |
| **NEXT-REL** | `src/docs/next-release-remaining-work.md` | Near-term release checklist | Living | Synthesis / 0.18 |
| **SESS-CONTRACT** | `src/docs/sessions/2026-07-contract-and-ship/*` | 0.18 contract/reporting/gates sessions | Historical plans | Release hygiene |
| **FUT-PERF** | `src/docs/Future/interop-performance-validation-plan.md` | Perf validation plan | Low priority | L3 |
| **FUT-API** | `src/docs/Future/api-snapshot-tooling.md` | API drift tooling | Low | L3 |
| **FUT-PRIV** | `src/docs/Future/private-framework-dependencies-plan.md` | Private framework deps | Specialized | M2 edge |
| **FUT-XPKG** | `src/docs/Future/cross-package-nuspec-dependencies.md` | Cross-package nuspec | Specialized | M2 |
| **DA-ORCH** | `src/docs/deep-audit-2026-07/00-ORCHESTRATION.md` | This program’s wave plan | Active | All |
| **DA-METH** | `src/docs/deep-audit-2026-07/00-methodology.md` | Lenses L1–L5; prior-art policy | Active | All |
| **BT-RULES** | `.claude/rules/bindingtests.md` | BindingTests ops + skip taxonomy | Living ops | M0-D / M4 |
| **EM-RULES** | `.claude/rules/emitter.md` | Emitter architecture notes | Living | A* |
| **PM-RULES** | `.claude/rules/parser-marshaler.md` | Parser/marshaler patterns | Living | A8/A6 |
| **SUI-RULES** | `.claude/rules/swiftui-bridge.md` | SwiftUI bridge pipeline | Living | M1 |

---

## K. Explicit “do not re-litigate” product decisions

From methodology + BindingAudit + roadmap (orchestration lock):

1. SwiftUI `View` → bridge (not direct binding).
2. ModuleInternal / `@_spi` pruning.
3. TN2435: native runtime as **framework**, not loose dylib; no `SwiftSupport/` injector.
4. AppIntents full authoring surface **not shipping** for 1.0 / 0.18.
5. Confirmed upstream Mono set is **exactly four** (+ SafeHandle async note).
6. Async multi-emitter **merge rejected**.
7. Capability-typed projection model **deferred**.
8. Demangler replacement spike **NO-GO**.
9. D1–D5 signed 0.18 path (no 1.0 contract program now).
10. No public package support-tier labels (D3 = NO).

---

## L. How deep tracks should use this index

1. **Before filing a finding**: grep this index + `roadmap.md` latent/medium tables + SB-Backup R1–R6 log.
2. **If match without new reachability**: tag `already-known` / link ID; do not inflate P0 count.
3. **If match with new reachability**: reopen with fixture path + emission site; still cite prior ID.
4. **Consumer packages**: BA + BSA only; no full re-audit.
5. **Tests**: M0-D map + track M4; BindingAudit “shallow tests” theme is already established.

---

## Companion Wave 0 deliverables

| Deliverable | Path |
|---|---|
| Test landscape (M0-D) | `waves/W0-map/M0-D-test-landscape.md` |
| This index (M0-E) | `00-prior-art-index.md` |
| Codebase map / file ledger | `00-codebase-map.md`, `00-file-coverage-ledger.md` (sibling agents) |
