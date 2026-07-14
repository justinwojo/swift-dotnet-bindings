# Deep Audit — 2026-07

Read-only, multi-wave audit of the SwiftBindings tool (generator, runtime, SDK, build, tests).

**Status: PROGRAM COMPLETE** (qualitative waves + quantitative data-pack), **independently verified 2026-07-16**.  
**⚠️ Start here instead:** [`00-AUDIT-VERIFICATION.md`](00-AUDIT-VERIFICATION.md) — the verification ("audit of the audit") is the **single source of truth for the fix program**; its §3 worklist supersedes `synthesis/work-items-backlog.md` (two of that queue's top-six items are refuted). Consume any unverified audit content only through the verification's §9 discount rules.  
**Audit's own entry points:** [`synthesis/executive-summary.md`](synthesis/executive-summary.md) · [`data-pack/README.md`](data-pack/README.md) (skip heatmaps, diagnostics, taxonomies, kitchen design)

---

## Key documents

| Doc | Purpose |
|-----|---------|
| [synthesis/executive-summary.md](synthesis/executive-summary.md) | **Final one-pager** |
| [synthesis/work-items-backlog.md](synthesis/work-items-backlog.md) | Ranked implement queue |
| [synthesis/graceful-degradation-map.md](synthesis/graceful-degradation-map.md) | Day-1 partial-success map |
| [synthesis/simplification-opportunities.md](simplification-opportunities.md) | L4 catalog (40 rows) |
| [synthesis/open-questions.md](synthesis/open-questions.md) | Owner policy decisions |
| [synthesis/refuted-claims.md](synthesis/refuted-claims.md) | Don’t re-chase |
| [00-ORCHESTRATION.md](00-ORCHESTRATION.md) | Waves & concurrency |
| [00-methodology.md](00-methodology.md) | Lenses L1–L5 |
| [00-codebase-map.md](00-codebase-map.md) | Architecture map |
| [00-prior-art-index.md](00-prior-art-index.md) | Prior audits index |
| [00-file-coverage-ledger.md](00-file-coverage-ledger.md) | File inventory |
| [tracks/](tracks/) | Per-track deep reports |
| [waves/](waves/) | Per-wave synthesis |

---

## Bottom line (one paragraph)

ABI/ownership cores are **mature (~risk 2/5, no new emission-live P0s)**. Highest leverage is **day-1 graceful degradation** (wrapper-required package kill, mixed ObjC abort, compile-but-dead reverse APIs), **visibility honesty** (`nonisolated` / protocol reqs), **test/gate honesty** (baselines theater, mega unit string blobs), and **dual-oracle simplification** — not another CallConv audit.

**Mode:** document only. Implementation of findings is owner-gated via the backlog.
