# Roadmap

**Updated**: February 2026
**Status**: Active — path to production-grade
**Target**: Production-ready binding libraries that feel like native C# to consumers

For completed work (binding quality sessions A-D, architecture sessions 1-9, cross-module resolution, ExistentialContainer elimination, native C# enums, Optional truncation fix, SwiftDictionary projection), see `Completed/roadmap-completed-feb2026.md`.

For future vision items (ObjC integration, multi-platform, emitter redesign, SwiftUI bridge corpus, performance benchmarks, etc.), see `Future/future-roadmap.md`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 4,005 passing |
| Integration tests | 700 passing (11 skipped, pre-existing) |
| Runtime library tests | 221 passing |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 32/32 passing (all at 0 compile errors) |

---

## Definition of Done: Production-Grade

A binding library is production-grade when a C# developer can:

1. **Discover** — NuGet package with XML doc comments, no internal types in IntelliSense
2. **Construct** — Create Swift objects naturally, with proper constructors and factory methods
3. **Use** — Call methods, access properties, chain builders, iterate collections, await async
4. **Trust** — No memory leaks from normal usage patterns (GC handles cleanup), meaningful exceptions on errors
5. **Extend** — Implement Swift protocols from C#, pass callbacks, subscribe to events
6. **Compose** — Base class methods available on derived types, polymorphic assignment works, protocol inheritance correct

We are at 4/6 today (1-2, 4-5 are solid). Inheritance (#6) and memory safety (#3) are the remaining gaps.

---

## Roadmap Phases

### Phase 1: Class Inheritance (Next — 6 sessions)

**Complete this first. Everything else in this roadmap comes after.**

Full implementation plan, session details, acceptance gates, and validation checkpoints are in **`class-inheritance-implementation.md`**. That document is self-contained — use it as the working reference for the next 6 sessions (I1-I6). Update it as sessions complete, then return here for Phase 2.

**Summary**: 1,184 inherited members missing across 60 derived classes in 12 libraries. Includes SafeHandle ownership fix (use-after-free + finalizer memory leak) integrated into Session I3.

---

### Phase 2: Binding Quality (3 sessions, after Phase 1 is complete)

Sessions E and F were already planned. Session G is new from the architectural audit.

#### Session E: Protocol Quality

**Priority**: P2 | **Effort**: Medium (1 session) | **Impact**: 67 empty interfaces + 320 unmarked throwing members

| Step | Description | Impact | Effort |
|------|-------------|--------|--------|
| **E1. Audit empty interface root causes** | Regenerate all libraries, categorize: genuinely empty (marker protocols) vs. skipped members. | 67 interfaces triaged | Low |
| **E2. Emit diagnostic on empty interfaces** | `[Obsolete("...", DiagnosticId = "SB0004")]` with skip reasons. Suppress genuinely empty protocols. | Discoverability | Low |
| **E3. Reduce member skip rate** | Evaluate whether closure marshalling in protocol proxy receivers can recover skipped members. | Fewer empties | Medium |
| **E4. Mark NotSupportedException proxy members** | `[Obsolete("...", DiagnosticId = "SB0003")]` on proxy members that throw, explaining the limitation. | 320 members marked | Low |

**Key files**: `ProtocolHandler.cs`, `ProtocolProxyEmitter.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`, `MemberEmissionValidator.cs`
**Acceptance gate**: `SB0003` count matches `NotSupportedException` count. Empty interfaces with skipped-member root cause drop to 0.
**Note**: Phase 1 Session I5 (protocol conformance inheritance) should be completed first — it will change the empty interface count.

#### Session F: Swiftinterface Parsing & Actor Isolation

**Priority**: P2 | **Effort**: Medium (1 session)

| Step | Description | Effort |
|------|-------------|--------|
| **F1. Access-level filtering** | Types absent from `.swiftinterface` → `[EditorBrowsable(Never)]` or suppressed entirely. Heuristic fallback when swiftinterface unavailable: `*Pinglet*`, `*Telemetry*`, `_*`. | Medium |
| **F2. Parse @MainActor** | `SwiftInterfaceAccessParser` already extracts other annotations. Add `@MainActor` / `@_Concurrency.MainActor`. | Low |
| **F3. Emit actor isolation on wrappers** | When protocol/class is `@MainActor`, emit on generated wrapper functions. Handle custom actors. | Medium |
| **F4. Remove -strict-concurrency=minimal** | Once actor-aware emission covers known cases. | Low |

**Key files**: `SwiftInterfaceAccessParser.cs`, `MemberEmissionValidator.cs`, `EveryProtocolEmitter.cs`, `SwiftWrapperCompiler.cs`
**Acceptance gate**: Internal types suppressed for BlinkID/StripePayments. BlinkIDUX wrapper compiles with 0 actor isolation errors.

#### Session G: Finalizer Safety & Consumer Diagnostics

**Priority**: P2 | **Effort**: Medium (1 session)

Identified in the architectural audit (February 2026). Two related issues:

**G1. Finalizer memory leak documentation + mitigation.** `SwiftSafeHandle<T>` finalizer deliberately skips `ValueWitnessTable.Destroy()` (unsafe during .NET shutdown ordering). Result: any `ISwiftObject` not explicitly disposed leaks Swift-side memory permanently. C# developers expect GC to clean up. Mitigation options (pick one or combine):
- Document loudly in README, XML doc comments, and generated code comments
- Add `[DebuggerDisplay]` showing "LEAKED — call Dispose()" on finalized handles
- Explore release-queue pattern: release on next P/Invoke call from same thread (deferred — complex)

**G2. Roslyn analyzer for undisposed Swift objects.** Existing plan: `Future/roslyn-analyzer-plan.md`. Warn at compile time when `ISwiftObject` locals are created without `using` or explicit `Dispose()`. Package as part of `Swift.Runtime` NuGet.

**Key files**: `SwiftSafeHandle.cs`, new `Swift.Runtime.Analyzers/` project
**Acceptance gate**: Analyzer warns on undisposed locals in test code. Zero false positives on properly disposed objects.

**Acceptance KPIs (Phase 2)**:

| KPI | Current | Target | Session |
|-----|---------|--------|---------|
| Empty protocol interfaces (0 members) | 67 | <10 | E |
| Internal types visible in IntelliSense | ~50 | 0 | F |
| Roslyn analyzer ships with Swift.Runtime | No | Yes | G |

---

### Phase 3: Production Readiness (4 items, ~2 sessions total)

These are infrastructure and documentation items needed before the repo goes public and NuGet packages are promoted beyond preview.

#### H. Contributor Onboarding

**Effort**: 0.5 session

| Item | Description |
|------|-------------|
| `CONTRIBUTING.md` | How to build, test, submit PRs. Code style. Commit conventions. |
| Architecture overview | How the pipeline works: Parser → TypeDatabase → Marshaler → Emitter. Why `TypeProjectionFactory` exists. How to add a new type projection. |
| Issue + PR templates | Structured templates for bug reports, feature requests, and PRs. |

Currently the project has excellent documentation for AI agents (CLAUDE.md, memory files) but nothing for human contributors. The architecture session docs (1-9, A-D) exist only in Claude's memory — the key decisions need to be in the repo.

#### J. ABI & Module Database Versioning

**Effort**: 0.5 session

| Item | Description |
|------|-------------|
| **ABI JSON robustness** | Document supported Swift version range. Graceful degradation on unknown node kinds (currently `NotImplementedException` logged, node silently skipped). Add `--swift-version` auto-detection from ABI metadata. |
| **Module database migration** | Current format is XML version "1.0" with no migration path. Adding superclass data (Phase 1) will be the first schema change. Add version bump + clear SWIFTBIND error code for version mismatches. Backward-compatible reads where possible. |

#### K. End-to-End Consumer Smoke Test

**Effort**: 0.5 session

CI lane that tests the full NuGet consumer workflow:
1. `dotnet new swift-binding -n TestLib`
2. Copy in a small xcframework
3. `dotnet build`
4. `dotnet pack`
5. Reference the NuGet from a consuming app project
6. Call a generated binding method

The current CI `package-smoke` job packs but doesn't consume. Breaking changes to `Sdk.targets`, `Sdk.props`, or the template could ship undetected.

#### L. Upstream Bug Reports

**Effort**: Trivial (filing only) | **Blocked on**: Repo going public

Three Mono runtime issues are drafted and ready to file (`Future/upstream-bug-reports-draft.md`):
1. JIT assertion crash (`!ji->async`) — process-fatal
2. Non-blittable CallConvSwift rejection
3. SafeHandle lifetime in async

Plus the NativeAOT simulator request (`Future/upstream-nativeaot-simulator-issue.md`).

**Action**: File as soon as repo is public. Zero code effort, starts the clock on runtime team engagement.

---

### Phase 4: Future Vision

Substantial capability expansions. See `Future/future-roadmap.md` for detailed analysis and prioritization.

| Item | Effort | Notes |
|------|--------|-------|
| **ObjC Binding Integration** | Large (3-5 sessions) | Replace Objective Sharpie. Prerequisite for NSObject hierarchy in generated bindings. Design: `Future/objc-binding-integration.md`. |
| **Multi-Platform Support** | Large (3+ sessions) | Mac Catalyst, macOS, tvOS. Design: `Future/dx-multi-framework-auto-detection.md`. |
| **Performance Benchmarks** | Medium | BenchmarkDotNet harness. Design: `Future/interop-performance-validation-plan.md`. |
| **API Snapshot Tooling** | Medium | Detect accidental API surface changes. Design: `Future/api-snapshot-tooling.md`. |
| **SwiftUI Bridge Corpus** | Medium | Coverage tracking across 10+ libraries. Design: `Future/swiftui-bridge-v2-plan.md`. |

---

## Sequencing Summary

```
NOW                        Phase 1: Class Inheritance (I1-I6)
                           |  Memory ownership fix built into I3
                           |
AFTER INHERITANCE          Phase 2: Binding Quality (E, F, G)
                           |  E after I5 (protocol conformance changes count)
                           |  F and G can run in parallel with E
                           |
BEFORE PUBLIC LAUNCH       Phase 3: Production Readiness (H, J, K, L)
                           |  H (contributor docs) gates external contributions
                           |  J (versioning) gates module database schema changes
                           |  K (consumer smoke test) gates NuGet promotion
                           |  L (upstream bugs) gates going public
                           |
POST-LAUNCH                Phase 4: Future Vision
                           |  ObjC integration, multi-platform, benchmarks
```

**Estimated total**: ~12 sessions from current state to production-grade (Phases 1-3). Phase 4 is ongoing capability expansion with no fixed timeline.

---

## Explicitly Out of Scope

Items evaluated during the February 2026 architectural audit and deliberately excluded:

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered. Inheritance Session I2 topological sort + existing ModuleProcessor + cross-module database cover 90% of needs. Build more when a specific ordering bug is found. |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system (`where Element.SubSequence == Slice<Self>`). Better protocol proxy emission (Session E) is the practical fix. |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature with no ABI JSON representation. Builder methods appear as regular methods, which is correct. |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 32 validation libraries. Nice-to-have, not foundational. |
| Centralized `ISwiftRuntime` abstraction layer | Current separation of concerns (Arc.cs, TypeMetadata, ErrorDescriptionEmitter, etc.) is appropriate. Adding a facade would be abstraction without a concrete problem. |
| Incremental regeneration | Full regen is fast. Premature optimization. |
| Ownership semantics (`consume`/`borrow`) | Swift 6 feature with unclear ABI impact. Wait for stabilization. |

---

## Upstream .NET Runtime Notes

NativeAOT resolves most Mono JIT issues (SafeHandle crashes, non-blittable CallConvSwift, closure Cdecl limitations). The Mono JIT bugs only affect the iOS Simulator; device builds using NativeAOT are unaffected. Workarounds remain in place for simulator testing. Draft bug reports: `Future/upstream-bug-reports-draft.md`.

| Issue | Affects | Notes |
|-------|---------|-------|
| SafeHandle finalizer crashes | Simulator (Mono) | `Dispose()` required; works on NativeAOT |
| Non-blittable types with CallConvSwift | Simulator (Mono) | Wrapper methods + `MonoJitRiskDetector`; works on NativeAOT |
| Async runtime (32 tests, Tier 3) | Simulator (Mono) | Tests written, tagged Tier 3; works on NativeAOT |
| Non-primitive closure Cdecl | Simulator (Mono) | Fall back to CallConvSwift; works on NativeAOT |
| SafeHandle in async P/Invoke | All runtimes | Singleton + IntPtr conversion; needs dotnet/runtime SwiftSelf async support |
| Typed throws ABI mismatch | All runtimes | `throws(E)` may pack error in return (not swifterror register); needs dotnet/runtime typed throws ABI support |

**Tracking**: [#93631](https://github.com/dotnet/runtime/issues/93631), [#108662](https://github.com/dotnet/runtime/issues/108662), [#64215](https://github.com/dotnet/runtime/issues/64215), [#80905](https://github.com/dotnet/runtime/issues/80905)

---

## Known Generator Bugs (Tracked, Not Prioritized)

| Bug | Impact | Workaround |
|-----|--------|------------|
| String enum raw values use case names | Cosmetic | ABI JSON lacks individual case raw values |
| `UnsafePointer<T>` → AnyType | No concrete projection | Use `UnsafeMutablePointer<T>` |
| Throwing closure thunks | `SwiftString` return as `void*` | Exclude throwing closures |
| `async throws(ErrorType)` free functions | `_payload`/`this` in static context | Guarded — no runtime impact |
| ExistentialContainer0 in tuple element | Lottie edge case | `HasClosureUnsafeTupleElements` safety gate |
| Bare `Any` in generic positions → AnyType | CS0311 with `ISwiftObject` constraint | AnyType fallback correct; needs `SwiftAny` wrapper |
