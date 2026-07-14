# Deep Audit 2026-07 — Orchestration

**Status**: Active  
**Companion**: [`00-methodology.md`](00-methodology.md)  
**Workflow skeleton**: `.claude/workflows/codebase-audit.js` (tracks A1–A8 / C / M / L — adapted here)

---

## Decisions locked (owner 2026-07-15)

| Decision | Choice |
|----------|--------|
| Success metric | **Multi-primary**: correctness + meaningful tests + partial-success bindings + simplification inventory + complete map |
| “Every line” | **Ledger model** — inventory all files; deep branch-level review on load-bearing paths |
| Prior-art policy | Roadmap/prior audits = **already-known**; only re-open with new reachability |
| Verification | Probes and/or multi-reader for P0/P1; candidates otherwise |
| Consumer packages | Do not re-audit BindingAudit wholesale; delta only when forced |
| Output root | `src/docs/deep-audit-2026-07/` |
| Code changes | **None** until owner promotes work items from synthesis |
| Budget | Do not skip a valuable lens for cost; still wave-gated for quality |

### Extra lenses (owner addenda)

1. **Simplification without capability reduction** — first-class lens L4 in methodology; also dedicated sub-tracks in Waves 7/10 and a synthesis section.
2. **Graceful degradation** — first-class lens L3; dedicated mini-audit track **G1** (Wave 7 + threaded through all emitter waves).

---

## Agent concurrency policy

| Activity | Max parallel | Notes |
|----------|--------------|--------|
| Wave 0 map | 4–6 | Read-only, no write contention |
| Deep finders (same track) | 3 | Seek *different* findings |
| Deep tracks at once | **2–3** | Orchestrator synthesizes between waves |
| Verifiers per finding | 2 (P0/P1) / 1 (P2/P3) | Prefer model-family diversity when available |
| Report writers | 1 per track after verify | Single file ownership |

**Banned:** swarm-grepping the same mega-file with 10 agents; writing “confirmed” without evidence; mid-audit production edits; rediscovering roadmap latents as new P0s.

### Tooling roles

| Role | Preferred tool |
|------|----------------|
| Map / search | Explore subagent (read-only) |
| Deep find | Grok CLI or general-purpose, read-only on repo |
| Adversarial verify | Second model family when claim is load-bearing |
| Probes | Shell in `/tmp` only |
| Orchestration | Main session (human + lead agent) |

---

## Folder layout

```text
src/docs/deep-audit-2026-07/
  00-ORCHESTRATION.md          # this file
  00-methodology.md
  00-prior-art-index.md        # Wave 0
  00-codebase-map.md           # Wave 0
  00-file-coverage-ledger.md   # Wave 0 → updated every wave
  waves/
    W0-map/
    W1-abi-marshalling/
    …
  tracks/                      # per-track deep reports
  synthesis/
    executive-summary.md       # end
    work-items-backlog.md      # ranked; no auto-implement
    simplification-opportunities.md
    graceful-degradation-map.md
    open-questions.md
    refuted-claims.md
```

---

## Wave plan

### Wave 0 — Map & ledger (current)

| Agent | Deliverable |
|-------|-------------|
| M0-A | Generator pipeline map (CLI → parse → typeDB → marshal → emit → wrapper/SDK) |
| M0-B | Runtime ownership / ARC / collections map |
| M0-C | Nuke + SDK + pack + release gate matrix |
| M0-D | Unit tests + BindingTests topology + skip taxonomy sketch |
| M0-E | Prior-art index (don’t re-chase) |
| M0-F | File coverage ledger seed (all in-scope files → `inventory` or `unreviewed`) |

**Exit:** `00-codebase-map.md`, `00-prior-art-index.md`, `00-file-coverage-ledger.md`, Wave 1 prompts adjusted for real mega-file splits.

---

### Wave 1 — ABI / marshalling core

| Track | Focus | Lenses |
|-------|--------|--------|
| A1 | P/Invoke contract, CallConv, sret, SwiftSelf, x64 thunks | L1, L3, L5 |
| A2 | Struct layout, VWT, frozen/resilient, optional EI | L1, L4 |
| A3 | ARC, SafeHandle, async lifetime, existential ownership | L1, L3 |

**Method:** sample generated BindingTests wrapper↔P/Invoke pairs; adversarial probes.

---

### Wave 2 — Protocols / reverse dispatch / existentials

| Track | Focus | Lenses |
|-------|--------|--------|
| A5 | EveryProtocol, vtable SSOT, receivers, witness dispatch | L1, L3, L5 |
| A5b | Projected-key vs vtable-slot axes | L1, L5 |
| A5c | StaticInit same/cross-module fillability | L1 |

---

### Wave 3 — Generics / CSM / TypeDatabase

| Track | Focus | Lenses |
|-------|--------|--------|
| A6 | CSM, bound generics, PAT, PWT ordering | L1, L3, L4 |
| M3 | TypeDB / projection parity / Apple registry | L1, L4, L5 |

---

### Wave 4 — Closures / async / throws

| Track | Focus | Lenses |
|-------|--------|--------|
| A4 | Closures, optional-escaping, reabstraction | L1, L3 |
| A7 | Async / throws / error carriers; intentional path divergence | L1, L4 |

---

### Wave 5 — Parser / demangler / model

| Track | Focus | Lenses |
|-------|--------|--------|
| A8 | ABI JSON fidelity, interface facts, demangler | L1, L3 |

---

### Wave 6 — Runtime line-complete

Full ledger to `reviewed` / `reviewed-deep` for `src/Swift.Runtime/**` + native Swift. Lenses L1, L4, L5.

---

### Wave 7 — SDK / packaging / Nuke **+ graceful degradation mini-audit**

| Track | Focus | Lenses |
|-------|--------|--------|
| M2 | Wrapper compile, SDK two-pass, arch, consumer targets | L1, L3, L5 |
| **G1** | **Graceful degradation end-to-end** (see below) | **L3 primary** |
| B1 | Nuke gate integrity, pack, appstore hygiene, release attest | L2, L3 |

#### Track G1 — Graceful degradation (owner priority)

**Question:** If a user drops a new xcframework with a few unsupported shapes, do they get:

1. A **compile-clean** binding missing those members + an honest report, or  
2. CS*/swiftc failures / hard exit that blocks *all* use and forces a GitHub issue?

**Inventory required:**

1. **Admission points** — every place we decide emit vs skip (`MemberValidationPipeline`, `WrapperValidation`, handler gates, co-gater, stripper, ObjC WouldEmit, CSM filters).
2. **Failure modes taxonomy** — member skip, type skip, wrapper strip, C# compile fail, pipeline exit codes, SDK MSBuild errors.
3. **Emit-then-break sites** — code that emits C#/Swift known to fail compile (poison, incomplete marshal, wrong arity).
4. **Continue-on-error policy** — per stage: what is recoverable?
5. **Reporting surface** — does `binding-report.json` / SWIFTBIND* / SB000* give a consumer a Sharpie-like “here’s what’s missing” story?
6. **SDK experience** — does `dotnet build` on a binding project leave usable artifacts after partial failure?
7. **Opportunities** — each as `degrade-opportunity` findings with risk notes (never degrade *integrity* checks).

**Output:** `synthesis/graceful-degradation-map.md` (draft after W7; finalize in W11).

---

### Wave 8 — Tests as product

| Track | Focus | Lenses |
|-------|--------|--------|
| T1 | Unit tests: behavior vs string-match theater | L2, L4 |
| T2 | BindingTests skip honesty vs 4 upstream-only | L2 |
| T3 | Feature × platform coverage matrix | L2 |
| T4 | Baseline / strip / parity / compile-only fail-closed | L2, L3 |

---

### Wave 9 — ObjC / Apple / SwiftUI bridge / analyzers

| Track | Focus | Lenses |
|-------|--------|--------|
| M1 | SwiftUI bridge matrix | L1, L3 |
| L2 | ObjC pipeline | L1, L3 |
| AP1 | Apple supplement + analyzers | L1, L4 |

---

### Wave 10 — Maintainability / dual-oracle / simplification deep dive

| Track | Focus | Lenses |
|-------|--------|--------|
| C1 | Mega-file hazard map + AI footguns | L4, L5 |
| C2 | Invariant drift, dedup keys, constraints.md parity | L1, L4, L5 |
| **S1** | **Simplification catalog** (repo-wide rollup) | **L4 primary** |
| L1 | Docs/roadmap drift | L5 |

#### Track S1 — Simplification without capability loss

Roll up L4 findings from all waves; add targeted searches:

- Copy-pasted emitters / visitors missing an arm risk
- Parallel “must stay in sync” functions not sharing a core
- Dead models / NotImplemented paths
- Safe extract-methods that shrink mega-files without behavior change
- Post-emission rewriters that could move to emission admission (reduces strip complexity)

**Output:** `synthesis/simplification-opportunities.md`.

---

### Wave 11 — Synthesis

1. Cross-track dedup + severity recalibration  
2. Fold already-known into prior-art index  
3. `synthesis/work-items-backlog.md` ranked by **severity × reachability × consumer impact × fixture cost**  
4. Finalize graceful-degradation + simplification synthesis docs  
5. Executive summary for owner  
6. Optional second-model review of synthesis only  

---

## Per-wave operating loop

```text
1. Orchestrator freezes track prompts (paths + hunt + lenses + prior-art exclude list)
2. Finders (≤3/track) → candidates
3. Verifiers → confirmed / refuted / inconclusive
4. One reporter writes tracks/<report>.md
5. Orchestrator updates file ledger + merges P0/P1 into running backlog draft
6. Owner gate (optional): sign-off or auto-continue unless P0 integrity crisis
7. Next wave
```

---

## Running backlog seed themes (not findings yet)

These are *themes to hunt*, not confirmed defects:

- Emit-then-compile-break vs skip-at-admission  
- Co-gater / stripper as primary vs defense-in-depth  
- Dual async emitters (intentional vs accidental divergence)  
- Projected-key / vtable-slot / emitted-signature triple consistency  
- Test attributes that hide our bugs  
- Mega-file AI edit risk  
- CSM fail-closed wrapper vs silent under-emit  
- SDK fingerprint / NativeReference drop → DllNotFound (integrity)  
- Consumer “new library” day-1 experience vs Objective-Sharpie  

---

## Progress log

| Date | Event |
|------|--------|
| 2026-07-15 | Program created; methodology + orchestration locked (multi-metric + L3 graceful degrade + L4 simplification) |
| 2026-07-15 | Wave 0 complete: M0-A/B/C/D/E/F → `00-codebase-map.md`, `00-prior-art-index.md`, ledger 1799 files / ~705k LOC |
| 2026-07-15 | Wave 1 complete: A1 risk 2/5, A2/A3 medium; 0 new emission-live P0; dual-oracle residual — see waves/W1-abi-marshalling/00-wave1-synthesis.md |
| 2026-07-15 | Wave 2 complete: A5a/b/c all risk 2/5; layout SSOT sound; AF05 legacy CT fixed (docs stale); F8 refuted — see waves/W2-protocols/00-wave2-synthesis.md |
| 2026-07-15 | Wave 3 complete: A6+M3 risk 2/5; CSM crash-class closed; SameType sugar residual; F15 held — waves/W3-generics/00-wave3-synthesis.md |
| 2026-07-15 | Wave 4+G1 complete: A4/A7 risk 2/5; **G1 day-1 risk 3/5** — wrapper-required + mixed ObjC abort + produce-throw; synthesis/graceful-degradation-map.md |
| 2026-07-16 | Wave 5/7/T complete: A8 risk 3 (nonisolated visibility); M2 risk 3 (051 policy); T risk 3 (baselines theater). Mid-exec: synthesis/00-mid-audit-executive-summary.md |
| 2026-07-16 | Wave 6+9+10 complete: runtime ~2/5; W9 capability-limited; W10 40 L4 + constraints drift |
| 2026-07-16 | **PROGRAM COMPLETE** — synthesis/executive-summary.md + work-items-backlog.md + open-questions.md |
| 2026-07-16 | **Data-pack wave COMPLETE** — quantitative evidence under data-pack/ (00–16 + 99 wall); soft wall on further in-tree research |
