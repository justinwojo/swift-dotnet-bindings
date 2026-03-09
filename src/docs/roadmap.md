# Roadmap

**Updated**: March 5, 2026
**Goal**: Ship a confident public release of the Swift/.NET binding generator

---

## Where We Are

| Metric | Value |
|--------|-------|
| Unit tests | 6,507+ passing |
| Integration tests | 700 passing (11 skipped, pre-existing) |
| Runtime library tests | 262 passing (0 failures, 1 skipped) |
| Analyzer tests | 12 passing |
| TestFramework must-pass | 94/94, golden files: 1 |
| Library validation | **88/88 passing** (53 Swift, 34 ObjC, 1 mixed) |
| SwiftUI bridged views | 20 |

**Completed roadmaps** (all archived to `Completed/`):
- Foundation Roadmap (A1-D1) — 4 pillars, 10 sessions
- Feature Roadmap (F1-F7) — nint overloads, noise reduction, param gate lifts, collection dispatch, safety, runtime metadata
- Usability Roadmap (Sessions 1-10, EP1-3, S4-6) — protocol extensions, existential bypass, closure bridges
- Binding Experience Roadmap (BX1-BX4) — projection completeness, simple enums, .NET idiom polish, ObjC type hierarchy
- Workflow Assessment v2 (Sessions 1-4) — 8/9 target libraries usable

---

## What's Left to Ship

### 1. Release Readiness

These are the gates between current state and a public release.

| Phase | What | Effort | Status |
|-------|------|--------|--------|
| **Cold-start walkthrough** | Findings doc: `Completed/cold-start-walkthrough-findings.md`. 21 friction points found, 16 resolved (template version bug, namespace default, prerequisites, debugging docs, version consistency). | Done | Done |
| **Release packaging** | `Swift.Runtime` + `Swift.Bindings.Sdk` NuGet packages ready to publish. `dotnet new swift-binding` installs cleanly. Consumer smoke test: template -> build -> pack -> consume -> call -> works. | 1 session | Not started |
| **Pre-launch cleanup** | ABI/module database versioning notes (item J). License check. No secrets in committed files. Update SB diagnostic `UrlFormat` attributes to public repo URL. GitHub release tagging + changelog workflow. Version sweep to `1.0.0` (see F1 in walkthrough findings). | 0.5 session | Not started |
| **Upstream bug reports** | File 3 Mono JIT issues + NativeAOT simulator request on dotnet/runtime. Drafts ready: `Future/upstream-bug-reports-draft.md`, `Future/upstream-nativeaot-simulator-issue.md`. | Trivial | Blocked on repo going public |
| **Contributor onboarding** | `CONTRIBUTING.md`, architecture overview, issue/PR templates. Currently excellent AI docs but nothing for human contributors. | 0.5 session | Not started |

**Total**: ~2 sessions to ship-ready.

---

## Post-Ship Improvements

Items that add value but don't block a public release.

### Generator Gaps (remaining known limitations)

| Item | Impact | Effort | Notes |
|------|--------|--------|-------|
| Optional<Primitive/Enum> in closures | Various closure-accepting APIs | Medium | Different ABI from pointer-based Optional |
| Complex enums in closures | Various | Medium | Structural emitter change |
| AnyError -> Exception error handling | Ergonomics | Medium | `SwiftException : Exception` wrapping `AnyError` |
| Cross-module protocol conformances | Polymorphic use through cross-module interfaces | Medium | Thread conformance declarations across module boundaries |
| ~~ExistentialContainer cleanup from public APIs~~ | ~~Noise reduction~~ | ~~Medium~~ | **Done** — all proxy classes already `[EditorBrowsable(Never)]`; containers only in P/Invoke (private) and method bodies |
| Architectural generic closures (~45 methods) | RxSwift subscribe/flatMap, Alamofire interceptors | Large | Deferred from foundation roadmap P3.5 |
| String enum raw values | GRDB ResultCode, CryptoSwift error codes | Blocked | No data source in compiled xcframeworks |
| ~~`Array<ObjCClass>` properties~~ | ~~StripeIdentity testing APIs~~ | ~~Small~~ | **Done** (CQ-5) — `TryProjectObjCElement` fallback for UIKit/Foundation elements |
| `ConfigurationValue` property name collision | Nuke readability | Small | Alternative disambiguation strategy |
| SwiftUI type public construction | Consumer ergonomics | Small | `SwiftUI.Color(red, green, blue)` like `SwiftColor`; current stubs are opaque pass-through handles |

### SwiftUI Bridge (4 remaining sessions)

Active roadmap: `swiftui-roadmap.md`

| Session | Focus | Priority |
|---------|-------|----------|
| **1B** | Closure non-primitive returns (String, class) | Medium |
| **4B** | Constrained generics (`<T: Identifiable>`, `<T: Hashable>`) | Medium |
| **5** | Lifecycle (`onAppear`/`onDisappear`), presentation helpers | Medium-low |
| **6** | Observable binding (C# -> Swift reactivity), corpus tracking | Low |

Sessions 1A-3 + 4A + 4C already cover the vast majority of real-world SwiftUI views.

### Runtime

See `swift-runtime-improvements.md` for details.

| Item | Effort | Notes |
|------|--------|-------|
| Bulk retain/release helpers | Low-medium | Perf win for large collections |
| ~~SuppressGCTransition on ARC P/Invokes~~ | ~~Low~~ | **Done** — 5 safe leaf P/Invokes (retain/read ops); release excluded due to deinit callbacks |

---

## Future Vision

Detailed plans in `Future/`. Consolidated priority in `Future/future-roadmap.md`.

| Item | Effort | Design Doc |
|------|--------|------------|
| ~~**Multi-platform support** (Mac Catalyst, macOS, tvOS)~~ | ~~Large (3+ sessions)~~ | **Done** — 3 sessions complete. `Completed/multi-platform-roadmap.md` |
| **ObjC binding integration** (replace Objective Sharpie) | Large (2-3 weeks) | `Future/objc-binding-integration.md` |
| **SPM package support** (source -> xcframework -> bind) | Large | `Future/sdk-future-work.md` |
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |
| **Emitter architecture redesign** | Very large | `Completed/emitter-redesign-proposal.md` |
| ~~**Witness dispatch emission dedup**~~ | ~~Small~~ | ~~`Future/witness-dispatch-emission-dedup.md`~~ — **Done** (items 1-4 complete) |

---

## Doc Organization

| Location | Contents |
|----------|----------|
| `src/docs/roadmap.md` | This file — the single source of truth for remaining work |
| `src/docs/swiftui-roadmap.md` | Active SwiftUI bridge sessions (referenced above) |
| `src/docs/swift-runtime-improvements.md` | Runtime improvement plan |
| `src/docs/known-issues-workarounds.md` | Reference: runtime issues, Mono JIT workarounds, revert plan |
| `src/docs/preview14-fixes.md` | Active: current validation fix session |
| `src/docs/Future/` | Design docs for post-ship features |
| `src/docs/Completed/` | Archived completed roadmaps, reviews, session notes |

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 53 validation libraries |
| Ownership semantics (`consume`/`borrow`) | Swift 6 feature with unclear ABI impact |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |

---

## Upstream .NET Runtime Notes

NativeAOT resolves most Mono JIT issues. Device builds are unaffected. See `known-issues-workarounds.md` for full details.

| Issue | Affects | Tracking |
|-------|---------|----------|
| JIT assertion crash (CallConvSwift) | Simulator (Mono) | Draft ready, file when public |
| Non-blittable types with CallConvSwift | Simulator (Mono) | Draft ready |
| SafeHandle in async P/Invoke | All runtimes | Draft ready |
| NativeAOT on iossimulator-arm64 | Simulator | Draft ready |
