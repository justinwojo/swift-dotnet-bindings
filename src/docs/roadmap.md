# Roadmap

**Updated**: February 2026 (post-Session 4 — Sessions 1-4 complete)
**Status**: Active — pushing toward 4.0+ across all libraries
**Target**: Every validation library scores above 4.0/5.0 in binding quality
**Scoring reference**: `binding-review-v2.md` — 18-library quality review with 10-category scorecards

---

## Completed Work

All completed phases are archived in `Completed/`:

| Phase | Sessions | What | Score Impact |
|-------|----------|------|-------------|
| Infrastructure | A-G, 1-9 | Core pipeline, CryptoSwift validation, architecture overhaul | Foundation |
| Class Inheritance | I1-I6 | 60 derived classes, virtual/override dispatch, protocol conformance inheritance | +0.20 avg |
| Binding Quality | Q1-Q4 | Naming fixes, type database, closure relaxation, Self-returning protocols | +0.07 avg |
| Binding Review v2 | P3 | Full 18-library re-review against v1 baseline. Result: 3.38 → 3.45 | Measurement |
| Usability Session 1 | U1 | Protocol conformance validation, Self-concrete methods, SwiftSet projection, bound-generic Optional params | Fixes |
| Usability Session 2 | U2 | Swiftinterface parsing (access levels, @MainActor, marker protocols), actor isolation on all wrapper emitters, marker protocol typed overloads, internal type filtering | Correctness + Polish |
| Usability Session 3 | U3 | Existential bypass generalization (method accumulate pattern), protocol interface recovery (45 methods across 13 libraries), NotSupportedException proxy stubs | +45 interface methods |
| Usability Session 4 | U4 | Generic throwing closure bridge (Pattern A monomorphized wrappers), cdecl callback pairs, GCHandle context passing, error propagation via SBW_CreateError | GRDB unlock |
| Codex Review Fixes | — | SetProjection `GetContainerCreationPlan`, validator property-skip + method-name parity, SwiftSet double-enum, Alamofire type database gap | 31/32 → 32/32 |

Key completed-work references:
- `Completed/roadmap-completed-feb2026.md` — full session details for all phases
- `Completed/class-inheritance-implementation.md` — Phase 1 design doc
- `Completed/binding-review-feb-23.md` — original v1 scores
- `binding-review-v2.md` — post-Phase 2 scores and gap analysis

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 4,161 passing (1 skipped) |
| Integration tests | 700 passing (11 skipped, pre-existing) |
| Runtime library tests | 221 passing (1 skipped) |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 32/32 passing, 0 compile errors (Alamofire fixed — `URLCredential.Persistence` type database entry) |
| Binding quality avg | 3.45/5 (range: 2.40 RxSwift — 4.44 SmartCardIO) |

---

## Active Work

### Usability Roadmap (10 sessions) — `usability-roadmap.md`

10 sessions optimized for **workflow completion first** (scores follow). Derived from v2 binding review, corrected after cross-review with Codex. Tracks both average score and a per-library critical workflow pass matrix.

```
Session 1: Foundation + Quick Wins                    ✅ COMPLETE
 │         (conformance, Self-concrete, SwiftSet,
 │          bound-generic optional)
 │
 ├─► Session 2: Swiftinterface + Actor + Markers     ✅ COMPLETE
 ├─► Session 3: Existential Bypass + Protocol Recovery ✅ COMPLETE
 ├─► Session 4: Generic Throwing Closures            ✅ COMPLETE
 │
 └─► Session 5: Protocol Extensions — Owned Types    ← Benefits from Session 2 (1-2 sessions)
      └─► Session 6: Protocol Extensions — Foreign    ← Depends on 5 (1 session)
           └─► Session 7: Protocol Extensions — RxSwift  ← Depends on 5-6, high risk (1-2 sessions)

Session 8: Naming + Polish + Cross-Module             ← Defer until after 1-7 (1 session)
Session 9: Safety & Hardening                         ← After workflows unlocked (0.5-1 session)
Session 10: Library-Specific Patches                  ← Endgame (1 session)
```

**Projected outcome**: 3.45 avg → ~3.81 avg (realistic range 3.70–3.90).

See `usability-roadmap.md` for full session plans, sub-tasks, acceptance gates, per-library projections, and critical workflow matrix.

---

### Finalizer Safety & Consumer Diagnostics

Carried from original Phase 3. Not in usability roadmap because it doesn't move binding quality scores, but critical for production use.

| Item | Description | Effort |
|------|-------------|--------|
| **Finalizer leak mitigation** | `SwiftSafeHandle<T>` finalizer skips VWT Destroy. Document loudly + `[DebuggerDisplay]` on leaked handles. Explore release-queue pattern. | 0.5 session |
| **Roslyn analyzer** | Warn at compile time when `ISwiftObject` locals lack `using`/`Dispose()`. Package in `Swift.Runtime` NuGet. Plan: `Future/roslyn-analyzer-plan.md`. | 0.5 session |
| **Proxy Dispose() no-op** | SkeletonView's 11 proxy classes leak GCHandle/EveryProtocol. Fix Dispose to clean up properly. | Small (part of safety session) |

---

### Production Readiness (before public launch)

| Item | Description | Effort |
|------|-------------|--------|
| **H. Contributor Onboarding** | `CONTRIBUTING.md`, architecture overview, issue/PR templates. Currently excellent AI docs but nothing for human contributors. | 0.5 session |
| **J. ABI & Module Database Versioning** | Document supported Swift versions. Add schema version + migration path for module databases. | 0.5 session |
| **K. Consumer Smoke Test** | CI lane: `dotnet new swift-binding` → build → pack → consume from app → call binding. Current CI packs but doesn't consume. | 0.5 session |
| **L. Upstream Bug Reports** | File 3 Mono JIT issues + NativeAOT simulator request. Drafts ready: `Future/upstream-bug-reports-draft.md`. Blocked on repo going public. | Trivial |

---

### Future Vision — `Future/future-roadmap.md`

| Item | Effort | Notes |
|------|--------|-------|
| **ObjC Binding Integration** | Large (3-5 sessions) | Replace Objective Sharpie. Design: `Future/objc-binding-integration.md` |
| **Multi-Platform Support** | Large (3+ sessions) | Mac Catalyst, macOS, tvOS. Design: `Future/dx-multi-framework-auto-detection.md` |
| **Performance Benchmarks** | Medium | BenchmarkDotNet harness. Design: `Future/interop-performance-validation-plan.md` |
| **API Snapshot Tooling** | Medium | Detect accidental API surface changes. Design: `Future/api-snapshot-tooling.md` |
| **SwiftUI Bridge Corpus** | Medium | Coverage tracking across 10+ libraries. Design: `Future/swiftui-bridge-v2-plan.md` |

---

## Sequencing Summary

```
DONE                       Phase 1: Class Inheritance (I1-I6) ✅
                           Phase 2: Binding Quality (Q1-Q4) ✅
                           Binding Review v2 ✅
                           |
NOW ─────────────────────► Usability Roadmap (10 sessions)
                           |  1-4: Quick wins + workflow unlocks ✅ COMPLETE
                           |  5-7: Protocol extensions (Kingfisher/SnapKit/RxSwift) → avg ~3.80
                           |  8-10: Polish + safety + patches → avg ~3.81
                           |
PARALLEL ────────────────► Finalizer Safety (bundled into Session 9)
                           |
BEFORE PUBLIC LAUNCH ────► Production Readiness (H, J, K, L, ~2 sessions)
                           |
POST-LAUNCH ─────────────► Future Vision (ObjC, multi-platform, benchmarks)
```

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered. Build more when a specific ordering bug is found. |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system. Protocol proxy emission is the practical fix. |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature with no ABI JSON representation. Builder methods appear as regular methods. |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 32 validation libraries. |
| Incremental regeneration | Full regen is fast. Premature optimization. |
| Ownership semantics (`consume`/`borrow`) | Swift 6 feature with unclear ABI impact. Wait for stabilization. |

---

## Upstream .NET Runtime Notes

NativeAOT resolves most Mono JIT issues. The bugs only affect iOS Simulator; device builds using NativeAOT are unaffected. Workarounds in place. See `known-issues-workarounds.md` for details and revert plan.

| Issue | Affects | Notes |
|-------|---------|-------|
| SafeHandle finalizer crashes | Simulator (Mono) | `Dispose()` required; works on NativeAOT |
| Non-blittable types with CallConvSwift | Simulator (Mono) | Wrapper methods + `MonoJitRiskDetector` |
| Non-primitive closure Cdecl | Simulator (Mono) | Fall back to CallConvSwift |
| SafeHandle in async P/Invoke | All runtimes | Singleton + IntPtr; needs dotnet/runtime support |
| Typed throws ABI mismatch | All runtimes | `throws(E)` packs error in return; needs runtime support |

**Tracking**: [#93631](https://github.com/dotnet/runtime/issues/93631), [#108662](https://github.com/dotnet/runtime/issues/108662), [#64215](https://github.com/dotnet/runtime/issues/64215), [#80905](https://github.com/dotnet/runtime/issues/80905)

---

## Known Generator Bugs (Tracked, Not Prioritized)

| Bug | Impact | Addressed In |
|-----|--------|-------------|
| String enum raw values use case names | Cosmetic | ABI JSON lacks raw values — no fix possible |
| `UnsafePointer<T>` → AnyType | No concrete projection | Future work |
| `async throws(ErrorType)` free functions | `_payload`/`this` in static context | Guarded — no runtime impact |
| ExistentialContainer0 in tuple element | Lottie edge case | U8 (existential projection) |
| Bare `Any` in generic positions → AnyType | CS0311 with `ISwiftObject` constraint | U8 (existential projection) |
