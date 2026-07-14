# Deep Audit 2026-07 — Methodology

**Status**: Active  
**Mode**: Read-only analysis and documentation. No production code changes in this program unless the owner later promotes a work item.  
**Primary product of the audit**: evidence-backed reports + ranked backlog under `src/docs/deep-audit-2026-07/`.

---

## Success metrics (multi-primary)

There is no single KPI. A wave or track is successful only if it advances **all** of these where relevant:

| Metric | What “good” looks like |
|--------|-------------------------|
| **Correctness** | Bindings that compile and marshal correctly; no silent wrong-ABI; crash-class paths identified and classified |
| **Meaningful gates** | Tests assert behavior that would catch real regressions, not implementation trivia or pass-only theater |
| **Usable partial bindings** | New / hard libraries degrade by *skipping* unbindable surface rather than emitting compile-broken C#/Swift or hard-failing the whole package |
| **Simplification without capability loss** | Duplication, dual oracles, mega-file hazards, and accidental complexity catalogued with safe consolidation shapes |
| **Completeness of the map** | Every in-scope source file reaches a non-`unreviewed` ledger status |

“Unlimited budget” does **not** mean “fix everything now.” It means **do not skip a valuable analysis lens** for cost reasons. Implementation remains owner-gated after synthesis.

---

## Relationship to prior work

| Artifact | Role in this audit |
|----------|--------------------|
| `src/docs/BindingAudit/` | Consumer surface baseline — **do not rewrite**; only delta when a generator finding forces it |
| `src/docs/binding-surface-audit/` | Packaging / C# quality / delta — reference only |
| `src/docs/roadmap.md` + latents | **Already-known** catalog; re-tag, don’t re-discover unless new reachability is shown |
| `SB-Backup-Docs/architecture-review-2026-06/` | Refuted + verified-clean log — consult before chasing R1–R6 leads |
| `.claude/rules/constraints.md` | Load-bearing trap list — every entry must be verified against current code in Wave 10 |
| `.claude/workflows/codebase-audit.js` | Track skeleton (A1–A8, C*, M*, L*) — adapted into this program’s waves |

**Product decisions we do not re-litigate**: SwiftUI View → bridge; ModuleInternal/`@_spi` prune; TN2435 framework (not loose dylib); AppIntents not shipping as full authoring surface; confirmed upstream Mono issues (exactly four).

**Roadmap “input-poor” thesis**: Accepted for *expecting many new P0s from re-scanning the same corpus*. Rejected as a reason to skip **map completeness**, **test honesty**, **graceful degradation**, or **simplification inventory**.

---

## Five audit lenses

Every deep track must score findings against these lenses (a finding can hit more than one).

### L1 — Correctness / ABI / lifetime

Silent wrong results, crashes, double-free, wrong CallConv, vtable slot shift, ownership leaks.

### L2 — Gate / test honesty

Tests that pass without proving the contract; skips masking our bugs; baselines that fail open; missing fixtures for known dangerous paths.

### L3 — Graceful degradation (partial-success binding)

**Product goal:** Dropping an arbitrary xcframework on the tool should yield a **usable binding package** even when some members/types are unsupported — honest skips, clean compile, clear report — rather than a pile of CS*/swiftc errors or a total pipeline failure that forces a bug report before the consumer can try anything.

Hunt for:

| Pattern | Desired direction |
|---------|-------------------|
| Emit then fail compile (C# or wrapper Swift) | Prefer **skip at emission** with `SkipReason` + report row |
| Public surface that always throws / SB000x poison as “API” | Prefer omit or mark non-public / `EditorBrowsable` with report |
| Hard pipeline exit on one bad member | Prefer isolate to member/type; continue module |
| Dual paths where one is fail-closed and sibling is fail-open inconsistently | Align on **skip-and-continue** for unsupported *shapes*, fail-closed only for **integrity** (symbol plan vs emit disagreement, corrupt packaging) |
| Co-gater / post-strip as sole safety net | Prefer emission-time admission so post-process is defense-in-depth |
| Missing wrapper symbol after emit | Integrity fail-closed is OK; count growth must be gated |
| ObjC/Swift mixed path partials | Same partial-success contract as pure Swift |
| Consumer experience vs Objective-Sharpie | We can’t offer freehand ApiDefinitions edits as the main escape hatch; **partial compile-clean output + editable skip report** is our analogue |

**Integrity vs usability split** (do not confuse):

- **Usability degrade (good default for unknown libraries):** skip unsupported signatures, PAT-heavy protocols, unprojectable types → binding still builds.
- **Integrity fail-closed (must stay hard):** plan/emit symbol mismatch, false “has wrapper” metadata, TN2435 packaging lies, runtime contract epoch fraud.

### L4 — Simplification without capability loss

Not “delete features.” Hunt for:

- Duplicated decision logic (two functions that must agree; copy-pasted emitters)
- Parallel async / marshalling paths that diverge by accident (document intentional vs accidental)
- Dead code, unused branches, second metadata models
- Mega-files that force whole-file context for every edit
- Naming / key builders that could share one core (already partly done — verify completeness)
- Safe mechanical consolidations (byte-identical or behavior-preserving)
- Refactors that **reduce** dual-oracle risk

Every simplification finding needs: **current capability preserved**, **risk class** (byte-identical / behavior-preserving / needs fixture), **suggested shape**, **do not do if…**

### L5 — Maintainability / AI hazard

Where a locally plausible change breaks a global invariant; undocumented ordering; switch fallthroughs; hardcoded generated locals; constraints.md drift.

---

## Severity & status

### Severity

| Tag | Meaning |
|-----|---------|
| **P0** | Correctness: wrong at runtime, crash, or silent critical path dead; or integrity hole that ships a lie |
| **P1** | Major footgun, broken core feature, compile-broken emit for common shapes, tests that greenwash P0/P1 |
| **P2** | Inconsistency, secondary gap, dual-path drift, simplification high-value |
| **P3** | Polish, docs, low-risk cleanup |

### Status (every finding)

| Status | Meaning |
|--------|---------|
| `confirmed` | Probe and/or multi-reader evidence; defect holds |
| `candidate` | Plausible; not yet verified |
| `refuted` | Checked; code is correct or already guarded |
| `already-known` | Matches roadmap / prior audit; linked; no re-chase |
| `simplification` | Capability-preserving complexity reduction opportunity |
| `degrade-opportunity` | Graceful-skip / partial-success improvement |

### Confidence

`high` / `medium` / `low` — independent of severity.

### Reachability

| Tag | Meaning |
|-----|---------|
| `emission-live` | Hits current validation or BindingTests corpus |
| `fixture-reachable` | No live lib, but a small fixture would hit it |
| `latent` | Mechanism real; no known emission site |
| `integrity-gate` | About pipeline honesty, not a single ABI shape |

---

## Evidence rules

1. Every claim cites `path:line` (or generated artifact path with regen context).
2. **P0/P1 confirmed** requires either (a) compile/SIL/nm/probe in `/tmp`, or (b) two independent model families agreeing after reading the same code, or (c) an existing BindingTests failure that maps cleanly.
3. Prefer **under-claim** over over-claim. Default status `candidate` until verified.
4. Do not invent product strategy (e.g. “bind AppIntents fully”) as findings.

---

## Non-goals (this program)

- Implementing fixes or landing PRs (unless owner later promotes items)
- Full `nuke validate` / RegressionValidate as the audit itself (may *consult* artifacts)
- Rewriting BindingAudit per-library docs from scratch
- Performance benchmarks as a primary workstream (optional L3 notes only)
- Re-opening settled product non-goals above

---

## Finding record schema

```markdown
### DA-<WAVE>-<TRACK>-<NNN>: <title>

- **Severity**: P0|P1|P2|P3
- **Status**: confirmed|candidate|refuted|already-known|simplification|degrade-opportunity
- **Confidence**: high|medium|low
- **Lenses**: L1|L2|L3|L4|L5 (one or more)
- **Reachability**: emission-live|fixture-reachable|latent|integrity-gate
- **Claim**: …
- **Evidence**: `file:line` + excerpt/reasoning
- **Probe**: what was run / what would refute
- **Suggested fixture** (if any): Swift shape for BindingTests
- **Suggested simplification** (if L4): shape + risk class
- **Prior art**: link or “none”
```

---

## File coverage ledger

Every in-scope source file gets one status:

| Status | Meaning |
|--------|---------|
| `unreviewed` | Not yet assigned |
| `inventory` | Purpose + deps noted (Wave 0) |
| `reviewed` | Full read; no load-bearing concern found |
| `reviewed-deep` | Branch-level + cross-check to tests/oracles |
| `hazard` | Dual-path / invariant / complexity documented |
| `deferred-known` | Matches roadmap latent; linked |
| `out-of-scope` | Generated output, bin/obj, third-party |

**Program exit criterion:** zero `unreviewed` under in-scope roots; synthesis + backlog exist.

---

## In-scope roots

```
src/Swift.Bindings/src/
src/Swift.Bindings/tests/
src/Swift.Runtime/src/
src/Swift.Runtime/tests/
src/Swift.Runtime/swift/
src/Swift.Bindings.Sdk/
src/Swift.Bindings.Apple/
src/Swift.Analyzers/
src/Swift.Analyzers.Tests/
src/SwiftBindings.TestDiscovery/
src/Swift.Bindings.Templates/
build/                    # Nuke + scripts (not bin/obj)
BindingTests/Sources/
BindingTests/RuntimeTestsApp/   # tests only; exclude bin/obj
tools/SwiftInterfaceParser/Sources/
.claude/rules/
src/docs/                 # drift check only, not line-complete
```

**Out of scope:** `.libraries/`, `artifacts/`, `BindingTests/output/`, bin/obj, external package repos unless a finding requires a one-shot reference read.
