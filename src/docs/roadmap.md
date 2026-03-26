# Roadmap

**Updated**: March 25, 2026

---

## Pending Work

### SwiftUI Session 6

Observable binding (C# → Swift reactivity) + corpus tracking. Low priority — advanced reactivity. Details in `swiftui-roadmap.md`.

### Small Fixes

| Item | Notes |
|------|-------|
| Async frozen struct params | `stackalloc` not safe after `await`. Needs heap allocation path. |
| `[String: Any]` dictionary projection | Alamofire, Mixpanel JSON-like config. Requires runtime boxing. |
| Static protocol constructors (`init`) | Factory method synthesis on conforming types. |
| Bulk retain/release helpers | Perf win for large collections. Low-medium effort. |
| Cross-framework `using` directives | Auto-emit `using` for dependency namespaces (e.g., NukeUI referencing Nuke types). Currently hardcoded. |
| `pack-all.sh` orchestration | Multi-package build+pack in dependency order. Topological sort + manifest already exist. |
| Binding report as MSBuild warnings | Surface skip counts from `binding-report.json` as build warnings. Report infrastructure exists. |
| SwiftUI bridge SDK integration | Compile bridge `.swift` and package bridge framework in NuGet. Groundwork done, shell scripts work for now. |

---

## Hard / Deferred

High skip counts but architecturally difficult. Not scheduled unless a specific consumer need drives them.

| Item | Skips | Libraries | Why Deferred |
|------|------:|----------:|-------------|
| **Unsupported signatures** (associated type refs, placeholder types) | 353 | 37 | Requires associated type resolution through conformance graph |
| **Generic type contexts** (generic parent leaks into wrapper) | 349 | 14 | Needs type-erased dispatch for non-final generic class members |
| **Method-level generics** (`func foo<T>(...)`) | 179 | 13 | Requires specialization or type-erased wrappers |
| **Protocol extension associated type context** | — | GRDB | 666 errors contained by gate. Needs full generic constraint context. EC-17. |
| **Architectural generic closures** | ~45 | RxSwift, Alamofire | `subscribe`/`flatMap`, interceptors |
| **ObjC-bridged optional setters** | 90 | 11 | Setter paths for optional ObjC-bridged types |
| **Unsupported generic containers** | 71 | 20 | `Result<T,E>`, `Optional<existential>` |
| **Custom actor types** (`actor Counter`) | — | 5+ | Requires async dispatch through actor's serial executor |
| **Async methods** | 28 | 5 | Methods with `async` keyword |
| **Async properties** | 14 | 5 | Properties with `async get` |
| **inout parameters** | 14 | 2 | `inout` write-back semantics |
| **Noncopyable types** (`~Copyable`) | 8 tests | 0 validation | `@_cdecl` wrappers need `consuming`/`borrowing` + move semantics |

---

## Future Vision

| Item | Effort | Notes |
|------|--------|-------|
| **Upstream bug reports** (7 issues) | Trivial (filing) | `Future/upstream-bug-reports-draft.md` — blocked on repo going public |
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |

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
