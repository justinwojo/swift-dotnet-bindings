# Roadmap

**Updated**: March 22, 2026

Previous sessions archived in `Completed/`:
- Sessions 0–14, architecture audit, post-audit fixes → `roadmap-march-2026-sessions.md`
- Stability Sessions 1–2, Post-Stability Sessions A–F, error code audit, CONTRIBUTING.md → `post-stability-sessions-a-f.md`

---

## Current State (March 22, 2026)

| Metric | Value |
|--------|-------|
| Runtime tests (sim) | 846 pass, 62 skip |
| Runtime tests (device) | ~comparable |
| Unit tests | 9,060 |
| Validation compile gate | 90/90 pass |
| Swift wrapper compilation | 52/56 ok |
| Member emission | 995/1109 (89.7%) |
| Type emission | 257/277 (92.8%) |
| @_cdecl wrapper coverage | 725/918 (78.9%) |

### Validation failures (0 C# compile, 4 Swift wrapper)

**C# compile failures:** None.

**Swift wrapper failures:**

| Library | Root Cause |
|---------|------------|
| GRDB | Protocol extension associated type context (EC-17) — architecturally blocked |
| Quick | XCTest dependency not found during wrapper compilation (not a generator bug) |
| SkeletonView | Internal type member gate — ~266 errors when lifted |
| TinyConstraints | x86_64-only xcframework, no arm64 simulator slice (stale build artifact) |

---

## Stability Session 3 (Pending)

| Session | Focus | Impact | Status |
|---------|-------|--------|--------|
| **3** | SwiftOptional Mono marshalling investigation | ~22 tests behind crash barriers (DeviceKit, PhoneNumberKit) | Pending |

Full details, root cause analysis, and BindingTests plans in `sdk-0.3.0-validation-findings.md`.

---

## Session G: Generated Code Size Reduction

Generated bindings are verbose — 738K total LOC across 90 libraries. Analysis shows 25-35% reduction is achievable by extracting shared helpers, reducing ~221K lines of generated code. Improves build times, binary size, and debuggability.

**Extract runtime marshalling helpers:**
- Try/finally indirect result pattern (3,907 instances) → `SwiftMarshal.WithIndirectResult<T>()`
- Error handling block (50+ identical 16-line blocks) → `SwiftMarshal.HandleSwiftError()`
- String return decoding (50+ identical 9-line blocks) → `SwiftMarshal.ReadUtf8String()`
- Stackalloc + MarshalToSwift pattern (305 instances) → `SwiftMarshal.MarshalParameter<T>()`

**P/Invoke deduplication:**
- 590 duplicate P/Invoke stubs in test library alone. Deduplicate identical signatures across methods.

**Reduce metadata noise:**
- Review auto-generated XML doc comments on internal utility methods — remove non-value-add summaries.

**Validation**: `run-tests.sh` + `validate-libraries.sh` + `build-and-test.sh` (emitter changes affect all generated code).

---

## Remaining Fixes

Small items that can be tackled opportunistically or folded into any session.

| Item | Affects | Effort |
|------|---------|--------|
| Optional\<Bool\>/Optional\<SimpleEnum\> in closures | ~5-10 skips (RxSwift, Alamofire) | Medium — requires extra inhabitant encoding support in `MarshalOptionalFromSwift<T>` |
| SwiftUI type public construction | Consumer ergonomics | Small |
| ObjC-bridged optional setter @_cdecl wrapper | Final class `UIViewController?`/`NSString?` setters still CallConvSwift — `ShouldEmitWrapper` rejects due to IntPtr reconstruction incompatibility | Medium |
| Optional-closure property setter @_cdecl wrapper | Final class `((…) -> Void)?` setters still CallConvSwift — `ShouldEmitWrapper` rejects closure properties | Medium |

---

## Deferred from Previous Sessions

| Item | Origin | Notes |
|------|--------|-------|
| Async frozen struct params | Session 1 | `stackalloc` not safe after `await`. Needs heap allocation path. |
| `[String: Any]` dictionary projection | Session 3 | Alamofire, Mixpanel JSON-like config patterns. Requires runtime boxing infrastructure. |
| Cross-module protocol conformances | Session 4 | Medium-high complexity. Multi-module libraries (Stripe) benefit. |
| Static protocol constructors (`init`) | Session 4 | Factory method synthesis on conforming types. |

---

## SwiftUI Bridge (4 remaining sessions)

Active roadmap: `swiftui-roadmap.md`. Sessions 1A–3 + 4A + 4C already cover the vast majority of real-world SwiftUI views. These remaining sessions are diminishing returns — schedule as needed, not as a block.

| Session | Focus | Priority |
|---------|-------|----------|
| **1B** | Closure non-primitive returns (String, class) | Medium |
| **4B** | Constrained generics (`<T: Identifiable>`, `<T: Hashable>`) | Medium |
| **5** | Lifecycle (`onAppear`/`onDisappear`), presentation helpers | Medium-low |
| **6** | Observable binding (C# → Swift reactivity), corpus tracking | Low |

---

## Hard / Deferred

High skip counts but architecturally difficult. Not scheduled unless a specific consumer need drives them.

| Item | Skips | Libraries | Why Deferred |
|------|------:|----------:|-------------|
| **Unsupported signatures** (associated type refs, placeholder types) | 353 | 37 | Requires associated type resolution through conformance graph |
| **Generic type contexts** (generic parent leaks into wrapper) | 349 | 14 | Needs type-erased dispatch for non-final generic class members |
| **Method-level generics** (`func foo<T>(...)`) | 179 | 13 | Requires specialization or type-erased wrappers |
| **Protocol extension associated type context** | — | GRDB | 666 errors contained by gate. Needs full generic constraint context. See EC-17. |
| **Architectural generic closures** | ~45 methods | RxSwift, Alamofire | RxSwift `subscribe`/`flatMap`, Alamofire interceptors. |
| **ObjC-bridged optional setters** | 90 | 11 | Setter paths for optional ObjC-bridged types |
| **Unsupported generic containers** | 71 | 20 | `Result<T,E>`, `Optional<existential>` |
| **Custom actor types** (`actor Counter`) | — | 5+ | Requires async dispatch through actor's serial executor |
| **Async methods** | 28 | 5 | Methods with `async` keyword |
| **Async properties** | 14 | 5 | Properties with `async get` |
| **inout parameters** | 14 | 2 | `inout` write-back semantics |
| **Noncopyable types** (`~Copyable`) | 8 tests | 0 validation | `@_cdecl` wrappers need `consuming`/`borrowing` + move semantics |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | 795 | Correct behavior — private API should not be bound |
| Synthesized Codable | 155 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| SwiftUI/Combine dependencies | 60 | Framework boundary — consumers use SwiftUI bridge instead |
| Generic protocol constraints / PATs | 68 | Architecturally blocked by associated type erasure |
| Unsatisfied ISwiftObject | 104 | Fundamental type system constraint — generic args must be projectable |

---

## Future Vision

Detailed plans in `Future/`. Consolidated priority in `Future/future-roadmap.md`.

| Item | Effort | Design Doc |
|------|--------|------------|
| **Upstream bug reports** (4 issues) | Trivial (filing) | `Future/upstream-bug-reports-draft.md`, `Future/upstream-nativeaot-simulator-issue.md` — blocked on repo going public |
| **Multi-platform support** (macOS, Mac Catalyst, tvOS) | Large (3+ sessions) | `Future/dx-multi-framework-auto-detection.md` |
| **SPM package support** (source → xcframework → bind) | Large | `Future/sdk-future-work.md` |
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |
| **Emitter architecture redesign** | Very Large | `Future/emitter-redesign-proposal.md` — right long-term direction, wrong near-term investment |

---

## Runtime

| Item | Effort | Notes |
|------|--------|-------|
| Bulk retain/release helpers | Low-medium | Perf win for large collections. Deferred — do when relevant. |

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
