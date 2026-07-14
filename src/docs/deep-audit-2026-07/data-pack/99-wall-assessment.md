# Data Pack — Wall Assessment

**Date**: 2026-07-16  
**Question**: Have we hit diminishing returns for *finding* vs *implementing*?

---

## Verdict: **Yes — soft wall on in-tree research. Hard wall not claimed for external corpus.**

### What is saturated (stop re-doing)

| Domain | Why saturated |
|--------|----------------|
| ABI / CallConv / VWT / ARC / reverse-dispatch layout | Waves 1–2 + risk ~2/5 + 0 new live P0 |
| CSM main path / TypeDB parity | Wave 3; roadmap rows re-tagged fixed |
| Closures Layer1/2, async ownership | Waves 4; matrices documented (14) |
| SkipReason enum + disposition | Full catalog (00) |
| SWIFTBIND/SB ID surface | 98 codes (01, 12) |
| Emit-then-break loci | Classified inventory (02) |
| CI vs theater gates | Exhaustive (03) |
| Top skip buckets (Signature, Closure, Dup, NetUnavailable, MissingWrapper) | Taxonomies 09, 10, 13, 14 |
| Visibility nonisolated mechanism | Host comments prove exclusion (05) |
| PartialSuccessKitchen design | Ready for implementers (08) |
| Unit test theater quantification | Metrics (15) |
| CLI + ambient collectors | 64 options, 7 sinks (07) |
| Dead code / TODO | Thin; second metadata model noted (16) |
| Dual-oracle / simplification | 40 rows in synthesis pack |
| Prior art / roadmap latents | Indexed; re-chase banned |

**Another “deep read of EveryProtocolEmitter” will not yield proportional new worker fuel.**

---

## What is *not* saturated (only if you want more data later)

| Gap | Why not done this pass | ROI if done |
|-----|------------------------|-------------|
| **Fresh `nuke validate` on current HEAD** | Slow (~5 min+); used committed baseline snapshot | Medium — refresh heatmap SHA |
| **Per-library skip Details for 1420 UnsupportedSignature** | No Details in baseline; needs re-validate with report dumps | High for capacity planning |
| **swift-dotnet-packages / internal-binding-testing crawl** | External repos | High for day-1 UX claims |
| **Apple SDK `public nonisolated` hit count** | Needs grepping Xcode swiftinterfaces | Confirms A8-002 blast radius |
| **Device/NativeAOT skip differential** | Needs device run | Medium for platform honesty |
| **Full line-complete ledger → reviewed on all 1799 files** | Completeness theater | Low for workers |
| **Performance / API snapshot tooling** | Explicit non-goals | Low pre-0.18 |
| **Live re-run of binding-tests compile-only for metrics** | Artifacts already present | Low if reports current |

---

## Recommended stop rule (this program)

**Stop discovering** when:

1. Every Tier-0 backlog item has a data-pack with counts + file:line + sub-buckets ✅  
2. Top 5 validate skip reasons have taxonomies ✅  
3. Day-1 kill path has diagnostic map (050/051/108) ✅  
4. Further work is “run expensive gates for fresher numbers” not “new classes of findings” ✅  

**Start implementing** (owner-gated) using:

`../synthesis/work-items-backlog.md` + this `data-pack/` folder.

---

## Residual curiosity list (parked, not blocking)

1. ListenerProxy Review “no decision recorded” — single BindingTests honesty bug  
2. ConstrainedExtensionEmitter skips not always in ReportCollector — undercount  
3. SWIFTBIND104 dual use (buffer skip vs archive nm)  
4. Parity forward-missing MCB / mixed-generic props — fixture debt  
5. Roadmap UnsupportedClosure “~188” vs validate **600** — docs stale  
6. EnsureGeneratorBuilt fingerprint docs vs code (T claimed fix)  

These are **work items or doc drift**, not reasons to re-open a full audit.

---

## Program completeness scorecard

| Layer | Qualitative waves | Quantitative data-pack | Enough for workers? |
|-------|-------------------|------------------------|---------------------|
| Correctness cores | ✅ | Dual-oracle + refuted | ✅ |
| Graceful degradation | ✅ G1 | 00,01,02,08 | ✅ |
| Skip capacity | partial | 04,09,10,13,14 | ✅ |
| Gates/tests | ✅ T | 03,15 | ✅ |
| Visibility | ✅ A8 | 05 | ✅ (probe left for implement) |
| Packaging/SDK | ✅ M2 | 01,07 | ✅ |
| External real-user libs | BindingAudit prior | validate baseline only | ⚠️ optional refresh |
