# Roadmap

**Updated**: March 18, 2026

---

## Coverage Baseline (March 16, 2026)

Measured across 90 validation targets (56 libraries). This is the starting point — each session below projects its impact against these numbers.

| Metric | Count | % |
|--------|------:|--:|
| Total Swift types | 2,584 | |
| Types emitted | 2,174 | 84% |
| Total Swift members | 14,109 | |
| Members emitted (usable) | 9,521 | 67% |
| Members skipped | 4,588 | 33% |

Of the 9,521 emitted members:
- **6,320 (66%)** use safe `@_cdecl` wrappers (works on all runtimes)
- **2,691 (28%)** use `CallConvSwift` (SB0001 — NativeAOT only, Mono JIT crashes)
- **549 (6%)** are `SB0003` stubs (protocol members that throw `NotSupportedException` at runtime)

**Projected cumulative coverage after sessions 1–4: 67% → ~80%** (~1,900 members recovered)
**After Session 1**: 152 compile errors eliminated across 21 libraries (90/90 validation). Precise member count delta pending re-measurement.

Session 2 recovered ~852 `@MainActor` skips across 21 libraries. Actual emitted member recovery depends on overlap with other skip reasons (unsupported types, closures, etc.). Swift wrapper compilation: 40/56 (net -2 from 42/56 due to 2 pre-existing bugs exposed by the gate lift; see Session 2 notes).

---

## Prioritized Sessions

### ~~Session 0: BindingTests Generator Bug Fixes~~ (Complete)

Completed March 17, 2026. All 4 generator bugs fixed, Swift source restored, C# tests written. 90/90 validation, 480 runtime tests pass (477→480).

---

### ~~Session 1: Struct & Closure Boundary Expansion~~ (Complete)

Completed March 17, 2026. 152 compile errors eliminated across 21 libraries. 90/90 validation, 4 new runtime tests passing on simulator.

**What shipped:**
- **Non-primitive frozen struct params** — Custom frozen structs pass as `UnsafeRawPointer` in `@_cdecl` wrappers, reconstructed via `.load(as: T.self)`. System framework types (CoreGraphics, Foundation) remain by-value. C# side: blittable structs use `stackalloc + MarshalToSwift`; memory-managed use `Payload.DangerousGetHandle()`. Skip gates removed from 6 files.
- **Closures with frozen struct params** — `IsCdeclCompatibleType` now accepts frozen structs. Swift adapter uses heap allocation (`initializeMemory`) with defer cleanup. C# callback receives via `MarshalFromSwift`.
- **Complex enums in closures** — Pure gate lift in `IsCdeclCompatibleType`. C# callback and Swift adapter heap allocation already existed.
- **Foundation.Data** — Investigation confirmed implementation already complete (`DataProjection.cs`). No action needed.

**Deferred to future session:**
- **Optional\<Primitive/Enum\> in closures** — Risky ABI change (tag-byte layout vs pointer-based Optional). Needs runtime verification before enabling.
- **Async frozen struct params** — `stackalloc` not safe after `await`. Gate retained in async @_cdecl eligibility.

**Libraries improved:** DeviceKit, Swinject, PhoneNumberKit (59→0 errors), Valet, SwiftyBeaver (20→0), NVActivityIndicatorView, ObjectMapper, BonMot, Parchment, KeychainSwift, SwipeCellKit (13→0), Quick, AMPopTip, XMLCoder (12→0), SVGView (12→0), plus 6 Stripe modules.

---

### ~~Session 2: Actor Isolation — @MainActor Sync Gate Lift~~ (Complete)

Completed March 17, 2026. Lifted the `@MainActor` skip gate so `@MainActor`-isolated members are emitted as synchronous C# APIs, following the Xamarin.iOS precedent (consumer manages thread affinity via `MainThread.BeginInvokeOnMainThread()`). Custom actors (`actor Counter`) remain blocked.

**Approach**: Synchronous gate lift, NOT async trampolines. `@_cdecl` wrappers get `@MainActor` annotation for Swift 6 compilation, but this is compile-time only — no runtime dispatch overhead. The C# consumer is responsible for calling from the main thread, same as all .NET iOS UIKit APIs.

**What was done:**
- Refactored `IsActorIsolatedMember()` to only block custom actor types, not `@MainActor`
- Added `IsMainActorIsolated` flag to `MethodDecl`/`PropertyDecl` to distinguish `@MainActor` from custom actors like `@ProcessingActor`
- Added `NeedsMainActorAnnotation()` with `nonisolated` guard — wrappers get `@MainActor` only when needed
- Updated all 16 wrapper emitter sites (Method, Property, Subscript, Constructor, Closure, OptionalPointer, Marshalling, Async, MarkerProtocolOverload, WitnessDispatch, ProtocolExtension)
- Extended `SwiftInterfaceAccessParser` to detect `@MainActor` free functions (single-line + multiline) and output separate `mainActorMembers` set
- Narrowed `ActorIsolatedAsyncStream` skip to custom actors only
- Added `-strict-concurrency=minimal` to BindingTests wrapper build scripts
- Fixed strip scripts to absorb `@MainActor` predecessor lines during error recovery
- Fixed `UIControl.State`/`UIControl.Event` XML entries (OptionSet → `kind="struct"`)
- Fixed ObjC-bridged struct `Unmanaged` reconstruction (use `Unmanaged<AnyObject> as! T`)
- 8 runtime tests (MainActorTests.cs), 7756 unit tests passing

**Validation**: 90/90 compile gate. Swift wrapper: 40/56 (was 42/56 pre-session — net -2).

**Remaining wrapper regressions (2 libraries):**

| Library | Root Cause | Fix Area |
|---------|-----------|----------|
| ~~**Parchment**~~ | ~~Fixed: `Unmanaged.passRetained(indexPath as AnyObject)` pattern already implemented in commit `6f5162ca`.~~ | ~~Complete~~ |
| ~~**BlinkIDUX**~~ | ~~Fixed post-session: AsyncStream @MainActor annotation + async method parameter naming bug (`at: at` → `at: point`).~~ | ~~`AsyncStreamEmitter.cs`, `WrapperEmitter.Async.cs`~~ |

**Custom actors stay deferred**: `actor Counter`-style types require async dispatch through the actor's serial executor — fundamentally different from `@MainActor`. These remain blocked via `ClassDecl { IsActor: true }` and per-member custom actor detection (`IsActorIsolated && !IsMainActorIsolated`).

---

### ~~Session 3: Existential Types & Error Ergonomics~~ (Complete)

Completed March 17, 2026. Scoped to Sub-tasks 1 (`any Sendable`) and 4 (SwiftException). Sub-tasks 2 (`[String: Any]` dictionary) and 3 (ExistentialContainer API cleanup) deferred post-release.

**What shipped:**
- **`any Sendable` marker protocol visibility** — Marker protocols (Sendable, Escapable, Copyable, SendableMetatype) excluded from witness table count and proxy/interface naming via `GetNonMarkerProtocols()` (ABI) and `GetEffectiveProtocols()` (naming). `any Sendable` → EC0 / `object`. `any Sendable & Codable` → EC1 / `ICodable`. Members with marker protocol existentials stop being skipped, appear in generated bindings, and compile. 6 Stripe dependency-gate improvements (StripePayments, StripeIssuing, StripePaymentSheet, StripePaymentsUI, Stripe, StripeCryptoOnramp).
- **SwiftException for sync untyped throws** — Unified exception hierarchy: sync untyped `throws` now emits `SwiftException` (was `SwiftRuntimeException`). Matches async path. `SwiftRuntimeException` retained for infrastructure errors (conformance lookup, protocol dispatch, closure bridge).
- **Duplicate `[Obsolete]` attribute fix** — `AvailabilityAttributeEmitter` now deduplicates `[Obsolete]` (C# allows only one per declaration). Exposed by marker changes enabling more deprecated members.

**What this does NOT deliver:** Generally callable `any Sendable` APIs from C#. Parameters require `ISwiftExistentialConvertible<ExistentialContainer0>`. Return values are visible but the returned EC0 cannot be re-passed to `any Sendable` parameter APIs. Full callability requires a future projection rework.

**Deferred post-release:**
- `[String: Any]` dictionary projection (Alamofire, Mixpanel JSON-like config patterns)
- ExistentialContainer API cleanup (remove internal marshalling types from public API surfaces)

**Validation**: 90/90 compile gate, 7818 unit tests, 48/56 swift wrapper. No regressions.

---

### ~~Session 4: Protocol Emission Improvements~~ (Complete — Sub-task 1)

Completed March 18, 2026. Sub-task 1 (static protocol members) shipped. Sub-task 2 (cross-module protocol conformances) deferred.

**Design change from roadmap**: The roadmap said "companion static class" but investigation showed a companion class would be empty/uncallable — protocol static members are requirements, not implementations. `static virtual` interface members (C# 11+) are the correct mapping. Uses `static virtual` with `NotSupportedException` throw body (not `static abstract`) to avoid CS8920 when the interface is used as a generic type argument in `RegisterConformanceFactory<T, IProtocol>()`.

**What shipped:**
- **Static properties** on protocols emitted as `static virtual` in the C# interface with throw body default. Conforming types that implement the property override the default.
- **Static methods** on protocols emitted as `static virtual` in the C# interface with throw body default. Same override semantics.
- **Proxy stubs** — Protocol proxy classes emit `static NotSupportedException` stubs for static virtual members (proxy dispatch doesn't support statics).
- **Conformance validation** — Lenient: if concrete type HAS a matching static member, full validation (accessor parity, type compatibility, name parity, parameter compatibility). If missing, the `static virtual` default satisfies the C# interface contract. This avoids false conformance drops when extension default index has coverage gaps.
- **`HasEmittableInterfaceMembers`** — Now includes static properties/methods. A protocol with only static members is not treated as an empty marker protocol.
- **`StaticProtocolMember` skips reduced to 1** (only `init` constructors remain). Static subscripts (no C# mapping) and operators (signature parity concerns) remain deferred.

**Remaining scope (deferred):**
- **Constructors (`init`)** — Would need factory method synthesis on conforming types.
- **Static subscripts** — C# has no static indexers.
- **Operators** — C# `static abstract operator` exists but has signature parity concerns.
- **Cross-module protocol conformances** (Sub-task 2) — Medium-high complexity. Needs dependency `ProtocolDecl` preservation in TypeDatabase, `ITypeDatabase` threading through `ShouldEmitConformance`, cross-module `FindProtocol` extension. Multi-module libraries (Stripe) would benefit.

**Validation**: 90/90 compile gate, 7840 unit tests, 76/76 standalone. No regressions.

---

### Sessions 5–7: NativeAOT & CallConvSwift Migration — Sessions 1–5 COMPLETE, Session 6 remaining

**Detailed plan**: `nativeaot-callconvswift-sessions.md`

NativeAOT investigation revealed 4 of 6 original @_cdecl architecture motivations were our bugs. Sessions 1–5 complete (infrastructure cleanup, generator fixes, CallConvSwift migration, regression fixes, verification). Session 7 (further @_cdecl reduction) absorbed into Session 6 scope.

Mono simulator crash reproduction (`MONO-SIMULATOR-FINDINGS.md`, standalone repro at `/Users/wojo/Dev/swift-interop-repro/`) proved 4 of 5 "Mono bug" categories are actually our generator/runtime bugs. Only returned thick closures is confirmed upstream (Mono CallConvSwift 16-byte struct return ABI).

**Session 6: Complete Runtime Test Cleanup** — fixes ALL 71 remaining fixable `[Skip]`/`[SkipOnSimulator]` annotations. Zero deferrals.

| Sub-session | Focus | Skip Recovery | Key Impact |
|-------------|-------|--------------|------------|
| **6A** | Investigation bugs (generic CallConvSwift, finalizer lifecycle, typed throws) | 38 | Debug against standalone repro's working patterns |
| **6B** | Generator emission bugs (operators, async module, Optional\<Int32\>, arrays, exports) | 26 | Clear fixes — generator emits wrong code |
| **6C** | New emission patterns (nested type wrappers, AOT callbacks, nested enum values) + cleanup | 7 | New generator logic needed |

After Session 6: **24 skips remain** (5 confirmed Mono upstream, 8 string enum blocked, 8 noncopyable future, 2 non-blittable upstream, 1 ValueTuple upstream). Zero false skips.

### Remaining Misc Fixes (not in NativeAOT sessions)

| Sub-task | Affects | Effort | Notes |
|----------|---------|--------|-------|
| SwiftUI type public construction | Consumer ergonomics | Small | `SwiftUI.Color(red, green, blue)` like `SwiftColor`; current stubs are opaque. |
| StripeCryptoOnramp cross-module re-export | StripeCryptoOnramp validation | Medium | Not a config fix — generator emits `StripeCryptoOnramp.STPAPIClient` but the type's canonical module is `StripePayments`. Swift wrapper fails because the re-exported type requires module-qualified access (`StripePayments.STPAPIClient`). Generator needs to detect cross-module type re-exports and use the canonical module in Swift wrappers. |
| Protocol proxy co-gating | Correctness (runtime) | Medium | When EveryProtocol conformance is skipped (class-bound, genericSig constraint, static methods, etc.), the C# proxy still emits `NativeMethods` referencing non-existent Swift symbols (`SetVtable`, `GetWitnessTable`). Causes runtime P/Invoke crash on first proxy use. Fix requires co-gating proxy emission AND all method bodies that reference the proxy (existential return unwrappers, optional property getters). Pre-existing issue exposed during validation wrapper fix work. See `ProtocolHandler.cs` TODO comment. |
| Protocol type-resolution consolidation | Maintainability | Medium | Three near-identical type-resolution paths exist: `ProtocolProxyEmitter.Helpers.GetCSharpTypeName()`, `ProtocolSignatureHelper.ProjectTypeToCSharp()`, and `GetInterfaceCompatiblePropertyTypeName()`. Each has subtle differences (ExistentialHandler only in the first, NativeInt narrowing only in the third, Self-requirement generic context only in the second). Drift between these produces proxy/interface signature mismatches. Consolidate into a single `ProjectTypeToCSharp` entry point with mode flags. |

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
| **Protocol extension associated type context** | — | GRDB | 666 errors contained by gate. Needs full generic constraint context from protocol definition into wrapper signatures. See EC-17. |
| **Architectural generic closures** | ~45 methods | RxSwift, Alamofire | RxSwift `subscribe`/`flatMap`, Alamofire interceptors. Deferred from P3.5. |
| **String enum raw values** | — | GRDB, CryptoSwift | **Blocked** — no data source in compiled xcframeworks |
| **ObjC-bridged optional setters** | 90 | 11 | Setter paths for optional ObjC-bridged types |
| **Unsupported generic containers** | 71 | 20 | `Result<T,E>`, `Optional<existential>` |
| **Custom actor types** (`actor Counter`) | — | 5+ | Requires async dispatch through actor's serial executor. Blocked by `ClassDecl { IsActor: true }`. Fundamentally different from `@MainActor` (Session 2). |
| **Async methods** | 28 | 5 | Methods with `async` keyword |
| **Async properties** | 14 | 5 | Properties with `async get` |
| **inout parameters** | 14 | 2 | `inout` write-back semantics |
| **Noncopyable types** (`~Copyable`) | 8 tests | 0 validation | `@_cdecl` wrappers need `consuming`/`borrowing` annotations + move semantics. Swift compiler strips wrappers that copy noncopyable values. Maps to `IDisposable` with deterministic deinit. No validation libraries use noncopyable types today. |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | 795 | Correct behavior — private API should not be bound |
| Synthesized Codable | 155 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| SwiftUI/Combine dependencies | 60 | Framework boundary — consumers use SwiftUI bridge instead |
| Generic protocol constraints / PATs | 68 | Architecturally blocked by associated type erasure — fundamental limitation |
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

## Contributor Onboarding

| Item | Effort | Notes |
|------|--------|-------|
| `CONTRIBUTING.md` | 0.5 session | Architecture overview, issue/PR templates. Currently good AI docs but nothing for human contributors. |

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

## Recently Completed

| Item | Completed | Notes |
|------|-----------|-------|
| Session 4: Static protocol members | Mar 18 | Static properties/methods emitted as `static virtual` in C# interfaces. `StaticProtocolMember` skips reduced from ~254 to 1 (only constructors). Proxy stubs, lenient conformance validation, `HasEmittableInterfaceMembers` includes statics. 90/90 validation, 7840 unit tests. Deferred: cross-module conformances, constructors, static subscripts, operators. |
| Session 3: Existential types & error ergonomics | Mar 17 | `any Sendable` marker protocol visibility (members stop being skipped, compile as `object`/EC0). SwiftException for sync untyped throws. Duplicate `[Obsolete]` dedup. 6 Stripe improvements. 90/90 validation, 7818 unit tests. Deferred: `[String: Any]` dictionary, ExistentialContainer API cleanup. |
| Session 2: @MainActor sync gate lift | Mar 17 | ~852 @MainActor skips lifted across 21 libraries. Synchronous C# APIs following Xamarin.iOS precedent. Also fixed OptionSet XML misclassification (UIControl.State/Event) and ObjC-bridged struct Unmanaged reconstruction. Parchment return-side Unmanaged fixed (commit `6f5162ca`). 40/56 swift wrapper, 90/90 compile gate, 481 runtime tests. |
| Session 1: Struct & closure boundary expansion | Mar 17 | Frozen struct params via `UnsafeRawPointer`, frozen struct + complex enum closure params via heap allocation. 152 compile errors eliminated across 21 libraries. 90/90 validation. Deferred: Optional\<Primitive\> closures (ABI risk), async frozen struct params. |
| Session 0: BindingTests generator bug fixes | Mar 17 | SBW_Free generic routing (CS7042), Payload `new` modifier (CS0108), failable init `default!` (CS8625), SwiftAsyncStream constraint relaxed (CS0315). 4 Swift types restored, 9 runtime tests + 3 unit tests (477→480 passing on simulator). |
| Apple framework XML database expansion | `ac39a4f7` (Mar 16) | ~473 skips resolved (nested + unresolvable Apple types). 90/90 validation. |
| NativeAOT device stability target | Mar 15 | 373 pass, 0 fail, 14/15 libraries. See `Completed/nativeaot-stability-sessions.md`. |
| C# keyword escaping in enum case labels | `51efaeec` (Mar 14) | FilterScope.swift fixture enabled. |
| ObjC binding integration | Mar 2026 | 34 ObjC framework targets validated. See `Completed/objc-binding-comparison.md`. |
| ObjC static method wrapper generation | `eb303ab4` (Mar 11) | Universal `@_cdecl` wrappers for static + instance methods. |
| Roslyn analyzer (SB1001) | Mar 2026 | Undisposed `ISwiftObject` warning + code fix in `SwiftBindings.Runtime` NuGet. |
| Ownership automation | Feb 2026 | See `Completed/ownership-automation-design.md`. |
