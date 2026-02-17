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

### 8. ObjC Binding Integration (`objc-binding-integration.md`)

**Priority**: P4 | **Effort**: Large (2-3 weeks) | **Blocked on**: Nothing

Replace the abandoned Objective Sharpie by adding ObjC binding generation to the same CLI/SDK. Uses `clang -ast-dump=json` (no native dependencies, always version-matched with Xcode). ~1,500-2,000 lines of new code.

**Why here (and not higher)**: The design is thorough and the shared infrastructure is compelling — xcframework resolution, dependency handling, NuGet packaging, and the MSBuild SDK all reuse directly. However:
- ObjC is declining; most new libraries are Swift-only
- The ObjC registrar runtime already exists in .NET MAUI — this adds a front-end, not a stack
- Edge cases (categories, class extensions, `__attribute__`, lightweight generics) could balloon scope
- It's a new pipeline with fundamentally different output (binding definitions vs direct P/Invoke)

**When to prioritize higher**: If users specifically request ObjC support, or if mixed ObjC/Swift frameworks become a common pain point. The Stripe family (some modules are ObjC-only) is one concrete motivator.

### 9. Mono JIT Remaining Work (`mono-jit-future-work.md`)

**Priority**: Mixed (P2-P4) | **Effort**: Varies | **Mostly blocked on**: Upstream

Five remaining items:
| Item | Priority | Notes |
|------|----------|-------|
| VWT Destroy via CallConvSwift | Medium | Blocks Dispose() on types with non-trivial fields. Mitigated by Tier 3 demotion. |
| VWT InitializeWithCopy | Low | No known test failures yet. |
| Non-primitive closure Cdecl | Low | Requires Swift-side marshal adapters. High effort. |
| N-protocol existential metadata | Low | Only zero-protocol case handled. Has kill criteria. |
| NativeAOT migration | Opportunistic | Eliminates all JIT issues. Depends on .NET 10 iOS tooling. |

**Why here**: Most items are either blocked on upstream Mono fixes or have diminishing returns given the workarounds already in place. The NativeAOT migration path is the real answer — if NativeAOT simulator support lands (Tier 1, item #2), most of these become irrelevant. Investing heavily in Mono JIT workarounds when NativeAOT is the strategic direction doesn't make sense unless NativeAOT simulator gets delayed significantly.

**Action**: Monitor upstream. If NativeAOT simulator lands, deprioritize VWT and closure Cdecl work. If delayed past .NET 10, consider VWT Destroy as a targeted fix.

### 10. Unsupported Existential Analysis (`unsupported-existential-analysis.md`)

**Priority**: P3 | **Effort**: Hard | **Blocked on**: ExistentialContainer runtime support from C#

26 members across Nuke and Lottie are skipped — existential type arguments in bound generics where the parameter has no default value. Fixing requires:
- Constructor/method signatures accepting `ExistentialContainer{N}` as bound generic type args
- C# callers boxing protocol-conforming objects into containers
- Runtime support for existential container construction from C#

**Why here**: Narrow impact (26 members across 2 libraries), significant implementation complexity, and most are library-specific provider/delegate protocols that consumers rarely call directly. The existential bypass emitter (Phase 51) already handles the default-arg case, which covers the common patterns.

---

## Tier 4: Long-Term Vision

These are architectural investments that pay off over years, not sessions.

### 11. Emitter Architecture Redesign (`emitter-redesign-proposal.md`)

**Priority**: P4 | **Effort**: Very Large (5+ sessions) | **Blocked on**: Nothing (but risky)

Three-phase architecture: type pre-processing (graph traversal + marshalling label assignment), type processing (handler-based member representation), emission from structured representations.

**Why last**: The current emitter works. It generates correct code for 25+ libraries with 0 errors. The proposal is well-designed and would make future feature development faster, but:
- It touches everything — high regression risk
- The ROI only materializes if there's a sustained stream of new feature work requiring emitter changes
- The handler-based design already exists in pieces (MethodHandler decomposition from Phase 50)
- An incremental approach (migrate one handler pattern at a time) is safer than a rewrite

**When to revisit**: If adding a new feature (e.g., class inheritance hierarchies) becomes prohibitively difficult due to emitter architecture constraints. The proposal is the right north star — the question is whether to migrate incrementally or rebuild.

### 12. Roslyn Analyzer (`roslyn-analyzer-plan.md`)

**Priority**: P3 | **Effort**: Small-Medium | **Blocked on**: Nothing

Warn at compile time when Swift objects implementing `IDisposable` are created without `using` or explicit `Dispose()`.

**Why last**: Nice DX polish, but the impact is narrow. Swift objects already implement `IDisposable` — any .NET developer familiar with the pattern will use `using`. The analyzer catches mistakes, but it's unlikely to be the difference between someone adopting the bindings or not. Better to invest in capabilities (more platforms, more libraries) first.

**When to build**: When packaging `Swift.Runtime` for broader distribution. Ship it as part of the NuGet analyzer package alongside the runtime.

---

## Cross-Cutting Dependencies

```
Repo goes public
  |
  +---> File upstream bug reports (#1, #2)
  |       |
  |       +---> Mono JIT fixes (upstream)
  |       +---> NativeAOT simulator (upstream)
  |               |
  |               +---> Mono JIT remaining work becomes mostly irrelevant (#9)
  |
  +---> ExistentialContainer cleanup (#3)
  |       |
  |       +---> Golden scenarios (binding-api-future-work.md)
  |       +---> Unsupported existential deep support (#10)
  |
  +---> Multi-platform (#4)
  |       |
  |       +---> Package naming decision
  |       +---> Multi-framework pack-all.sh
  |
  +---> ObjC integration (#8)
          |
          +---> Class inheritance hierarchy (roadmap P4)
          +---> Mixed ObjC/Swift framework support
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
| 8 | ObjC binding integration | `objc-binding-integration.md` | P4 | Large | No |
| 9 | Mono JIT remaining work | `mono-jit-future-work.md` | P2-P4 | Varies | Mostly upstream |
| 10 | Unsupported existentials | `unsupported-existential-analysis.md` | P3 | Hard | Runtime support |
| 11 | Emitter redesign | `emitter-redesign-proposal.md` | P4 | Very Large | No (but risky) |
| 12 | Roslyn analyzer | `roslyn-analyzer-plan.md` | P3 | Small | No |

---

## Strategic Notes

**The single highest-leverage action is filing the upstream issues (#1, #2).** Everything else on this list is work *we* do. The upstream issues are work the *runtime team* does — and they eliminate entire categories of complexity (Swift wrappers, Cdecl expansion, risk detection, dual-runtime testing). Filing costs an hour. Not filing costs ongoing workaround maintenance indefinitely.

**NativeAOT is the exit strategy for Mono JIT issues.** Rather than investing heavily in incremental Mono workarounds (#9), the strategic bet is that NativeAOT simulator support will land and render most of that work unnecessary. The upstream issue (#2) accelerates this.

**Multi-platform (#4) and ObjC integration (#8) are the two capability expansions that change the project's scope.** Multi-platform turns it from "iOS bindings" to "Apple platform bindings." ObjC integration turns it from "Swift bindings" to "Apple framework bindings." Both are significant, but multi-platform has a clearer ROI (same pipeline, more platforms) while ObjC is a new pipeline for a declining ecosystem.

**The emitter redesign (#11) is the right long-term direction but wrong near-term investment.** The current architecture works for 25+ libraries. The redesign pays off when sustained feature development outpaces what the current architecture can support cleanly. Migrate incrementally as features demand it, rather than rewriting speculatively.
