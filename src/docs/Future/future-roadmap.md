# Future Roadmap

**Created**: February 2026
**Scope**: All items in `src/docs/Future/` — analysis, prioritization, and rationale

---

## How to Read This

Each Future doc represents a distinct initiative. This roadmap ranks them into four tiers based on **impact-to-effort ratio**, **dependency chains**, and **strategic value**. Items within a tier are ordered by recommended execution sequence.

**Priority tiers:**
- **Tier 1**: High impact, relatively low effort — do these first
- **Tier 2**: Meaningful capability expansion — the next wave of work
- **Tier 3**: Significant new capabilities or architectural bets
- **Tier 4**: Long-term vision — revisit when earlier tiers are done

---

## Tier 1: High Impact / Low Friction

These items either require minimal effort for outsized payoff, or fix quality gaps that real consumers will hit.

### 1. Upstream Bug Reports (`upstream-bug-reports-draft.md`)

**Priority**: P1 | **Effort**: 1-2 hours (filing) | **Blocked on**: Repo going public

Three Mono runtime issues are drafted and ready to file on `dotnet/runtime`:
1. **JIT assertion crash** (`!ji->async`) — process-fatal, no managed recovery
2. **Non-blittable CallConvSwift rejection** — blocks natural type signatures
3. **SafeHandle lifetime in async** — blocks async instance methods

**Why first**: Zero code effort. Filing these starts the clock on runtime team engagement. Every month of delay is a month the runtime team isn't aware of (or prioritizing) these blockers. All three have production workarounds today, but the workarounds add generator complexity that could be eliminated. The draft quality is high — repro code, stack traces, and context are ready.

**Action**: File as soon as repo is public. Re-review drafts against current repo state and re-search `dotnet/runtime` for duplicates before submitting.

### 2. NativeAOT Simulator Issue (`upstream-nativeaot-simulator-issue.md`)

**Priority**: P1 | **Effort**: 1 hour (filing) | **Blocked on**: Repo going public

`iossimulator-arm64` doesn't support NativeAOT. All three Mono JIT blockers are verified resolved under NativeAOT on device. Simulator NativeAOT would eliminate the dual-runtime constraint entirely.

**Why here**: Same logic as upstream bugs — filing is free, and this issue affects every developer using the bindings (everyone develops on simulator first). Unlike the Mono JIT fixes (which require deep runtime changes), NativeAOT simulator is architecturally straightforward (same ARM64, different target triple). There's a realistic chance this gets prioritized.

**Action**: File alongside the bug reports. Reference the three Mono JIT issues as motivation.

### 3. Binding API: ExistentialContainer Cleanup (`binding-api-future-work.md`, R6)

**Priority**: P2 | **Effort**: Medium | **Blocked on**: Nothing

`ExistentialContainer` still leaks into public API surfaces in closure parameters and some protocol proxy constructors. This is the kind of thing that makes consumers feel the bindings are "not ready" — they see internal marshalling types in their IntelliSense.

**Why here**: Directly improves the experience for anyone using Nuke, Lottie, or any library with protocol-typed closures. No upstream dependency. The path is clear (map containers to protocol interfaces), gated on `AllProtocolsHaveTypeRecords()` for unregistered protocols like `Swift.Error`.

**Not included from this doc**: Async naming edge cases (P3, narrow impact), exception mapping (future), CancellationToken (future), golden scenarios (blocked on ExistentialContainer + AnyType gaps).

---

## Tier 2: Capability Expansion

These extend the project's reach — more platforms, more libraries, better confidence.

### 4. Multi-Platform Support (`dx-multi-framework-auto-detection.md`)

**Priority**: P2 | **Effort**: Large (3+ sessions) | **Blocked on**: Nothing

Currently iOS-only. Mac Catalyst, macOS, and tvOS are under investigation. Multi-platform support is table stakes for any framework-level tool — a library author targeting macOS today can't use the bindings at all.

**Why here**: Every platform added multiplies the addressable library count. Mac Catalyst is likely the easiest win (closest to iOS). macOS has the largest independent developer base. The infrastructure (xcframework slicing, TFM handling) already exists — it's mostly validation and edge-case work per platform.

**Open question**: Package naming (`Library.Swift.iOS` vs `Library.Swift`). Resolve before starting.

### 5. SwiftUI Bridge Phase 4: Corpus + Metrics (`swiftui-bridge-v2-plan.md`)

**Priority**: P3 | **Effort**: Medium | **Blocked on**: Nothing

Phases 1-3 are complete. Phase 4 adds coverage tracking across a real library corpus (10+ libraries), three-tier metrics (generated / typechecked / runtime-validated), reproducible corpus with pinned versions, and regression detection.

**Why here**: The bridge works but there's no way to measure progress or catch regressions as the generator evolves. This is the difference between "it works on 3 libraries we tested" and "here's our coverage across a representative corpus." Essential for credibility when sharing the project.

**Risk**: Low. Mostly scripting and report infrastructure. No generator changes.

### 6. Performance Benchmarks (`interop-performance-validation-plan.md`)

**Priority**: P3 | **Effort**: Medium | **Blocked on**: Nothing

Five CI smoke scenarios (<60s, ratio-based) plus a standalone BenchmarkDotNet harness for deep investigation. Measures interop overhead vs native Swift.

**Why here**: The question "how much overhead does the interop add?" has no answer today. For production adoption, this is a must-answer. The plan is well-structured with a sensible phased approach (observe-only -> soft gate -> tighten). The CI smoke checks are cheap; the deep harness is opt-in.

**Risk**: Low. New standalone code. No generator changes. Main risk is CI flakiness from timing variance — mitigated by ratio metrics and coarse thresholds.

### 7. API Snapshot Tooling (`api-snapshot-tooling.md`)

**Priority**: P3 | **Effort**: Medium | **Blocked on**: Nothing

Script that extracts public API surface from generated `.cs`, compares against a baseline, and flags drift. Optional integration into `build-and-test.sh`.

**Why here**: As the generator matures and gets external users, accidental API changes become a real risk. A method signature changing or disappearing silently could break downstream consumers. This is cheap insurance. The implementation is simple (essentially `grep` + `diff` on public member signatures), and it feeds naturally into CI (Tier 2, task #2 on the roadmap).

**Caveat**: Potentially noisy during active development. Best gated on opt-in or release builds until the API stabilizes.

---

## Tier 3: Bigger Bets

These are substantial new capabilities or cross-cutting changes. Each requires a planning session before starting.

### ~~8. ObjC Binding Integration~~ — COMPLETE

ObjC binding pipeline is fully implemented and validated against 34 ObjC framework targets (Realm, Stripe3DS2, BRLMPrinterKit, SDWebImage, CocoaLumberjack, MBProgressHUD, 28 Firebase/Google modules). Auto-detected from xcframework structure. Outperforms Objective Sharpie in compilation success, documentation, enum handling, protocol patterns, and project scaffolding. See `Completed/objc-binding-comparison.md` and `Completed/objc-improvement-plan.md` for details.

### ~~9. Mono JIT Remaining Work~~ — ARCHIVED

Moved to `Completed/mono-jit-future-work.md` (March 2026). All workarounds deployed; strategic direction is NativeAOT (#2 above). Not worth incremental investment.

### ~~10. Unsupported Existential Analysis~~ — ARCHIVED

Moved to `Completed/unsupported-existential-analysis.md` (March 2026). Usability Session 3's existential bypass covers common patterns. Remaining 26 cases are narrow and hard.

---

## Tier 4: Long-Term Vision

These are architectural investments that pay off over years, not sessions.

### 11. Emitter Architecture Redesign (`emitter-redesign-proposal.md`)

**Priority**: P4 | **Effort**: Very Large (5+ sessions) | **Blocked on**: Nothing (but risky)

Three-phase architecture: type pre-processing (graph traversal + marshalling label assignment), type processing (handler-based member representation), emission from structured representations.

**Why last**: The current emitter works. It generates correct code for 46 libraries (88 targets) with 0 errors. The proposal is well-designed and would make future feature development faster, but:
- It touches everything — high regression risk
- The ROI only materializes if there's a sustained stream of new feature work requiring emitter changes
- The handler-based design already exists in pieces (MethodHandler decomposition from Phase 50)
- An incremental approach (migrate one handler pattern at a time) is safer than a rewrite

**When to revisit**: If adding a new feature (e.g., class inheritance hierarchies) becomes prohibitively difficult due to emitter architecture constraints. The proposal is the right north star — the question is whether to migrate incrementally or rebuild.

### ~~12. Roslyn Analyzer~~ — COMPLETE (F6, March 2026)

SB1001 analyzer shipped in `Swift.Analyzers`, packaged into `SwiftBindings.Runtime` NuGet at `analyzers/dotnet/cs/`. Warns on undisposed `ISwiftObject` locals. Code fix adds `using` modifier.

---

## Cross-Cutting Dependencies

```
Repo goes public
  |
  +---> File upstream bug reports (#1, #2)
  |       |
  |       +---> Mono JIT fixes (upstream)
  |       +---> NativeAOT simulator (upstream)
  |
  +---> ExistentialContainer cleanup (#3)
  |       |
  |       +---> Golden scenarios (binding-api-future-work.md)
  |
  +---> Multi-platform (#4)
  |       |
  |       +---> Package naming decision
  |       +---> Multi-framework pack-all.sh
  |
  +---> ObjC integration (#8) — COMPLETE
```

---

## Summary Table

| Rank | Item | Doc | Priority | Effort | Blocked? |
|------|------|-----|----------|--------|----------|
| 1 | Upstream bug reports | `upstream-bug-reports-draft.md` | P1 | Trivial | Repo public |
| 2 | NativeAOT simulator issue | `upstream-nativeaot-simulator-issue.md` | P1 | Trivial | Repo public |
| 3 | ExistentialContainer cleanup | `binding-api-future-work.md` | P2 | Medium | No |
| 4 | Multi-platform support | `dx-multi-framework-auto-detection.md` | P2 | Large | No |
| 5 | SwiftUI Bridge corpus | `swiftui-bridge-v2-plan.md` | P3 | Medium | No |
| 6 | Performance benchmarks | `interop-performance-validation-plan.md` | P3 | Medium | No |
| 7 | API snapshot tooling | `api-snapshot-tooling.md` | P3 | Medium | No |
| ~~8~~ | ~~ObjC binding integration~~ | ~~`objc-binding-integration.md`~~ | — | — | Complete (March 2026) |
| ~~9~~ | ~~Mono JIT remaining work~~ | ~~`mono-jit-future-work.md`~~ | — | — | Archived (March 2026) |
| ~~10~~ | ~~Unsupported existentials~~ | ~~`unsupported-existential-analysis.md`~~ | — | — | Archived (March 2026) |
| 11 | Emitter redesign | `emitter-redesign-proposal.md` | P4 | Very Large | No (but risky) |
| ~~12~~ | ~~Roslyn analyzer~~ | ~~`roslyn-analyzer-plan.md`~~ | — | — | Complete (F6) |
| 13 | Witness dispatch emission dedup | `witness-dispatch-emission-dedup.md` | P4 | Small | No |

---

## Strategic Notes

**The single highest-leverage action is filing the upstream issues (#1, #2).** Everything else on this list is work *we* do. The upstream issues are work the *runtime team* does — and they eliminate entire categories of complexity (Swift wrappers, Cdecl expansion, risk detection, dual-runtime testing). Filing costs an hour. Not filing costs ongoing workaround maintenance indefinitely.

**NativeAOT is the exit strategy for Mono JIT issues.** Rather than investing heavily in incremental Mono workarounds (#9), the strategic bet is that NativeAOT simulator support will land and render most of that work unnecessary. The upstream issue (#2) accelerates this.

**Multi-platform (#4) is the remaining capability expansion that changes the project's scope.** It turns the project from "iOS bindings" to "Apple platform bindings." ObjC integration (#8) is now complete — the project already handles both Swift and ObjC frameworks, validated against 88 targets.

**The emitter redesign (#11) is the right long-term direction but wrong near-term investment.** The current architecture works for 46 libraries (88 targets). The redesign pays off when sustained feature development outpaces what the current architecture can support cleanly. Migrate incrementally as features demand it, rather than rewriting speculatively.
