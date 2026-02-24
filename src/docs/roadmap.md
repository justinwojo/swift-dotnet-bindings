# Roadmap

**Updated**: February 2026 (post-Q2 quality session)
**Status**: Active — path to production-grade
**Target**: Production-ready binding libraries that feel like native C# to consumers
**Scoring reference**: `binding-review-feb-23.md` — 18-library quality review with 10-category scorecards

For completed work (binding quality sessions A-D, architecture sessions 1-9, cross-module resolution, ExistentialContainer elimination, native C# enums, Optional truncation fix, SwiftDictionary projection, class inheritance I1-I6, quality sessions Q1-Q2), see `Completed/roadmap-completed-feb2026.md`.

For future vision items (ObjC integration, multi-platform, emitter redesign, SwiftUI bridge corpus, performance benchmarks, etc.), see `Future/future-roadmap.md`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 4,130 passing (1 skipped) |
| Integration tests | 700 passing (11 skipped, pre-existing) |
| Runtime library tests | 221 passing (1 skipped) |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 32/32 passing (all at 0 compile errors) |
| Binding quality avg | 3.37/5 (range: 2.30 RxSwift — 4.44 SmartCardIO) |

---

## Definition of Done: Production-Grade

A binding library is production-grade when a C# developer can:

1. **Discover** — NuGet package with XML doc comments, no internal types in IntelliSense
2. **Construct** — Create Swift objects naturally, with proper constructors and factory methods
3. **Use** — Call methods, access properties, chain builders, iterate collections, await async
4. **Trust** — No memory leaks from normal usage patterns (GC handles cleanup), meaningful exceptions on errors
5. **Extend** — Implement Swift protocols from C#, pass callbacks, subscribe to events
6. **Compose** — Base class methods available on derived types, polymorphic assignment works, protocol inheritance correct

We are at 5/6 today (1-2, 4-6 are solid). Core workflow usability (#3) is the remaining gap — the generator projects data models well but misses the core "verbs" (closure-based transactions, fluent builders, response handlers) that define how each library is actually used.

---

## Roadmap Phases

### Phase 1: Class Inheritance ✅ COMPLETE

Sessions I1-I6 complete. See `class-inheritance-implementation.md`.

**Results**: 60 derived classes across 12 libraries now have proper C# inheritance. 6 Alamofire request classes hierarchically linked. SnapKit fluent builder chain structurally works. Lottie AnimatedControl hierarchy correct. Virtual/override dispatch, shared payload/disposal, protocol conformance inheritance all working. 32/32 validation maintained.

**Post-inheritance score assessment** (from regenerated binding analysis):
- Alamofire: 2.90 → ~3.15 (+0.25). Hierarchy correct, but core request/response workflow still blocked by closure param gate.
- SnapKit: 3.20 → ~3.50 (+0.30). Fluent chain works internally, but `Get` prefix naming and no `view.snp` entry point limit real usability.
- Lottie: 3.85 → ~4.05 (+0.20). Clean improvement — 4 hierarchies, 116 virtual members, `IAnimationImageProvider` no longer empty.

The projections were slightly optimistic for the two lowest-scoring libraries because inheritance is foundational but doesn't fix the core workflow gaps (closures, naming, type database). Those are addressed in Phase 2.

---

### Phase 2: Binding Quality (Next — 4 sessions)

**Ordering principle**: Most impactful items that improve the most libraries first. Derived from the 10 prioritized action items in `binding-review-feb-23.md`, cross-referenced with per-library score impact.

#### Session Q1: Generator Bug Fixes — Naming, Tuples, Polish ✅ COMPLETE

**Results**: All 6 sub-tasks implemented. Self-returning methods no longer get `Get` prefix. `#file`/`#line` debug params stripped (SnapKit overloads reduced by ~60%). `ToString()` synthesized on types with `Description` property. C# indexers emitted for Swift subscripts on concrete types (KeychainAccess, GRDB, Nuke). Enum case tuple element types projected idiomatically. Subscript indexer param types aligned with protocol interfaces. Constructor debug-param wrappers emit correct static factory pattern.

| Sub-task | Status | Notes |
|----------|--------|-------|
| **Q1a. Fix `Get` prefix** | ✅ | Self-returning detection via `IsDynamicSelf` + concrete type name match. Applied to both concrete types and protocol interfaces. CS0542 guard added (method name = enclosing type). |
| **Q1b. Strip debug params** | ✅ | `IsDebugParameter()` heuristic: `StaticString`+file/function, `UInt`+line/column. Filtered in `WrapperSignatureBuilder` and overload counting. Swift `@_silgen_name` wrapper fills defaults. |
| **Q1c. `Description` → `ToString()`** | ✅ | `TypeHandlerHelpers.TryGetDescriptionPropertyName()`. Emitted on classes, structs, and class-backed enums. |
| **Q1d. C# indexers for subscripts** | ✅ | `SubscriptHandler.cs` emits `this[...]` with P/Invoke-backed get/set. Skip gates for wrapper-triggering accessors, complex index params (dict/existential/array/optional). Idiomatic param types with conversion in indexer body. |
| **Q1e. Tuple element projection** | ✅ (enum cases) | Enum case tuples fully projected. Method param tuples intentionally deferred (documented in `MethodSignature.cs:475`). |
| **Q1f. Tuple label verification** | ✅ | Labels from ABI JSON propagate correctly — confirmed by verification. |

**Key files modified**: `NameProvider.cs`, `IEnvironment.cs`, `IHandler.cs`, `DefaultParameterOverloadEmitter.cs`, `MethodHandler.cs`, `MethodSignature.cs`, `EnumHandler.CaseConstruction.cs`, `SubscriptHandler.cs` (new), `ClassHandler.cs`, `FrozenStructHandler.cs`, `NonFrozenStructHandler.cs`, `EnumHandler.cs`, `TypeHandlerHelpers.cs`, `ProtocolHandler.cs`, `ProtocolConformanceValidator.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`, `ProtocolProxyEmitter.Receivers.cs`, `ProtocolSignatureHelper.cs`

#### Session Q2: Apple SDK Type Database + Protocol Audit ✅ COMPLETE

**Results**: Type database expanded with SecurityDatabase.xml (SecTrust, SecCertificate, SecKey, SecIdentity → IntPtr) and IndexPath → NSIndexPath ObjC-bridged entry in FoundationDatabase.xml. JSONDecoder added to AppleFrameworkValueTypes exclusion list. Protocol diagnostics: SB0004 emitted on empty interfaces where all declared members were skipped (excludes genuine marker protocols and derived protocols with inherited members). SB0003 emitted on non-dispatchable proxy members (properties with non-dispatchable accessors, all subscripts, non-dispatchable methods). Proxy class suppresses self-generated SB0003/SB0004 warnings via `#pragma warning disable`. SDK `Sdk.props` NoWarn updated. 32/32 validation maintained.

| Sub-task | Status | Notes |
|----------|--------|-------|
| **Q2a. SecurityDatabase.xml** | ✅ | New `SecurityDatabase.xml` with SecTrust, SecCertificate, SecKey, SecIdentity → `System.IntPtr`. Registered in `Program.cs` builtInDatabases. |
| **Q2b. IndexPath → NSIndexPath** | ✅ | ObjC-bridged class entry in `FoundationDatabase.xml`. DB lookup takes priority over AppleFrameworkValueTypes guard. |
| **Q2c. JSONDecoder exclusion** | ✅ | Added to `AppleFrameworkValueTypes` list — prevents incorrect ObjC auto-bridging, correctly falls to AnyType. |
| **Q2d. SB0004 on empty interfaces** | ✅ | Interface body buffered via `CSharpWriter(StringWriter)`, diagnostic emitted when `emittedMemberCount == 0 && totalDeclaredMembers > 0 && inheritedInterfaces.Count == 0`. |
| **Q2e. SB0003 on proxy members** | ✅ | Non-dispatchable properties (with correct accessor presence check), subscripts (always), and methods annotated. |
| **Q2f. Warning suppression** | ✅ | Proxy class wraps in `#pragma warning disable SB0003, SB0004`. Sdk.props adds SB0003;SB0004 to `<NoWarn>`. |

**Key files modified**: `SecurityDatabase.xml` (new), `FoundationDatabase.xml`, `TypeDatabaseExtensions.cs`, `Program.cs`, `ProtocolHandler.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`, `ProtocolProxyEmitter.cs`, `Sdk.props`

#### Session Q3: Closure Parameter Relaxation

**Effort**: 1 session (large) | **Libraries**: Alamofire, GRDB, Stripe, RxSwift, SkeletonView

This is the single highest-impact change possible. Methods with closures containing non-primitive parameter or return types are either skipped entirely or degraded. This blocks the **core workflows** of the libraries that need it most — GRDB's `read`/`write`, Alamofire's `responseData`/`responseString`, Stripe's `confirmPayment`.

The closure parameter gate (`ClosureHandler` safety constraints) currently requires all closure parameter types to be primitive. This needs to be relaxed for known-safe types: enums, classes, frozen structs, and ObjC-bridged types.

| Step | Description | Impact |
|------|-------------|--------|
| **Q3a. Audit closure skip reasons** | Regenerate all 32 libraries with verbose logging. Categorize every skipped-due-to-closure method. | Understand the full scope |
| **Q3b. Relax gate for class/enum params** | Allow closure parameters that are classes (passed as pointer) or enums (passed as int/tagged union). These are the most common non-primitive types in closures. | GRDB `(Database) throws -> T`, Stripe `(STPPaymentHandlerActionStatus, STPPaymentIntent?, NSError?) -> Void` |
| **Q3c. Relax gate for ObjC-bridged params** | Allow `NSError`, `NSData`, `NSUrl`, etc. in closure parameters — these already have working projections. | Alamofire response handlers, many Stripe callbacks |
| **Q3d. Wrapper function generation** | Generate Swift `@_cdecl` wrapper functions that bridge between the relaxed closure signatures and the actual Swift API. | Required for the C-ABI boundary |

**Key files**: `ClosureHandler.cs`, `ClosureEmitter.cs`, `MemberEmissionValidator.cs`, `SwiftWrapperEmitter.cs`
**Acceptance gate**: GRDB `read`/`write` methods emitted. Alamofire `responseData`/`responseString` emitted. Stripe `confirmPayment` emitted. 32/32 validation.

#### Session Q4: Self-Returning Methods + Protocol Member Recovery

**Effort**: 1 session (large) | **Libraries**: Kingfisher, SnapKit, KeychainAccess, RxSwift

Self-returning methods are the #1 issue from the binding review. This session also re-evaluates remaining skipped protocol members now that Q3 has relaxed the closure gate.

| Step | Description | Libraries | Effort |
|------|-------------|-----------|--------|
| **Q4a. Self-returning protocol methods** | When a Swift protocol method returns `Self`, emit using concrete type substitution on conforming types (not `AnyType`). For the protocol interface itself, use `TSelf` generic constraint or return the interface type. | Kingfisher (30+ builder methods), SnapKit, KeychainAccess, RxSwift | Large |
| **Q4b. Reduce member skip rate** | With Q3 closure relaxation in place, re-evaluate remaining skipped protocol members. Some will now be emittable. | All | Medium |

**Key files**: `ProtocolHandler.cs`, `ProtocolProxyEmitter.cs`, `TypeConversionHandler.cs`, `MethodHandler.cs`
**Acceptance gate**: `IKFOptionSetter` methods return concrete types, not `AnyType`. Empty interfaces with skipped-member root cause drop to 0. 32/32 validation.

**Phase 2 Acceptance KPIs**:

| KPI | Current | Target | Session |
|-----|---------|--------|---------|
| `Get` prefix on fluent methods | ~40 | 0 | Q1 ✅ |
| `SwiftOptional`/`SwiftString` in enum tuple params | ~20 | 0 | Q1 ✅ |
| `AnyType` from missing Apple SDK types | ~30 | 0 | Q2 ✅ (Security + IndexPath resolved; remaining are intentional AnyType) |
| Empty protocol interfaces with diagnostics | 67 | <10 | Q2 ✅ (SB0004 on skip-caused empties) + Q4 |
| Core workflow methods skipped (closure gate) | ~200 | <50 | Q3 |
| `Self` → `AnyType` in protocol returns | ~50 | 0 | Q4 |
| Binding quality avg | 3.37 | >3.80 | All |

---

### Phase 3: Binding Polish & Safety (3 sessions, after Phase 2)

Items that improve correctness and developer experience but don't directly move library usability scores.

#### Session P1: Swiftinterface Parsing & Actor Isolation

**Priority**: P2 | **Effort**: Medium (1 session)

| Step | Description | Effort |
|------|-------------|--------|
| **P1a. Access-level filtering** | Types absent from `.swiftinterface` → `[EditorBrowsable(Never)]` or suppressed entirely. Heuristic fallback when swiftinterface unavailable: `*Pinglet*`, `*Telemetry*`, `_*`. | Medium |
| **P1b. Parse @MainActor** | `SwiftInterfaceAccessParser` already extracts other annotations. Add `@MainActor` / `@_Concurrency.MainActor`. | Low |
| **P1c. Emit actor isolation on wrappers** | When protocol/class is `@MainActor`, emit on generated wrapper functions. Handle custom actors. | Medium |
| **P1d. Remove -strict-concurrency=minimal** | Once actor-aware emission covers known cases. | Low |

**Key files**: `SwiftInterfaceAccessParser.cs`, `MemberEmissionValidator.cs`, `EveryProtocolEmitter.cs`, `SwiftWrapperCompiler.cs`
**Acceptance gate**: Internal types suppressed for BlinkID/StripePayments. BlinkIDUX wrapper compiles with 0 actor isolation errors.

#### Session P2: Finalizer Safety & Consumer Diagnostics

**Priority**: P2 | **Effort**: Medium (1 session)

**P2a. Finalizer memory leak documentation + mitigation.** `SwiftSafeHandle<T>` finalizer deliberately skips `ValueWitnessTable.Destroy()` (unsafe during .NET shutdown ordering). Result: any `ISwiftObject` not explicitly disposed leaks Swift-side memory permanently. Mitigation options (pick one or combine):
- Document loudly in README, XML doc comments, and generated code comments
- Add `[DebuggerDisplay]` showing "LEAKED — call Dispose()" on finalized handles
- Explore release-queue pattern: release on next P/Invoke call from same thread (deferred — complex)

**P2b. Roslyn analyzer for undisposed Swift objects.** Existing plan: `Future/roslyn-analyzer-plan.md`. Warn at compile time when `ISwiftObject` locals are created without `using` or explicit `Dispose()`. Package as part of `Swift.Runtime` NuGet.

**Key files**: `SwiftSafeHandle.cs`, new `Swift.Runtime.Analyzers/` project
**Acceptance gate**: Analyzer warns on undisposed locals in test code. Zero false positives on properly disposed objects.

#### Session P3: Binding Quality Re-Review

**Priority**: P2 | **Effort**: Small (0.5 session)

Re-run the full binding quality review (`binding-review-feb-23.md` methodology) against all 18 libraries with Phase 2 improvements applied. Update scores, identify any new top-priority items that emerged. This closes the loop and ensures the Phase 2 work actually moved the needle.

**Acceptance gate**: Average binding quality score > 3.80 (up from 3.37). Bottom-3 libraries all above 3.0.

---

### Phase 4: Production Readiness (4 items, ~2 sessions total)

Infrastructure and documentation items needed before the repo goes public and NuGet packages are promoted beyond preview.

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
| **Module database migration** | Current format is XML version "1.0" with no migration path. Superclass data was the first schema change (Phase 1). Add version bump + clear SWIFTBIND error code for version mismatches. Backward-compatible reads where possible. |

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

### Phase 5: Future Vision

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
DONE                       Phase 1: Class Inheritance (I1-I6) ✅
                           |
NOW                        Phase 2: Binding Quality (Q1-Q4, 4 sessions)
                           |  Q1: Bug fixes — naming, tuples, polish ✅
                           |  Q2: Type database + protocol audit ✅
                           |  Q3: Closure parameter relaxation (Alamofire, GRDB, Stripe, RxSwift)
                           |  Q4: Self returns + protocol recovery (Kingfisher, SnapKit, RxSwift)
                           |
AFTER QUALITY              Phase 3: Binding Polish & Safety (P1-P3)
                           |  P1: Swiftinterface/actor isolation
                           |  P2: Finalizer safety + Roslyn analyzer
                           |  P3: Binding quality re-review (score verification)
                           |
BEFORE PUBLIC LAUNCH       Phase 4: Production Readiness (H, J, K, L)
                           |  H (contributor docs) gates external contributions
                           |  J (versioning) gates module database schema changes
                           |  K (consumer smoke test) gates NuGet promotion
                           |  L (upstream bugs) gates going public
                           |
POST-LAUNCH                Phase 5: Future Vision
                           |  ObjC integration, multi-platform, benchmarks
```

**Estimated total**: ~10 sessions from current state to production-grade (Phases 2-4). Phase 5 is ongoing capability expansion with no fixed timeline.

---

## Explicitly Out of Scope

Items evaluated during the February 2026 architectural audit and deliberately excluded:

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered. Inheritance topological sort + existing ModuleProcessor + cross-module database cover 90% of needs. Build more when a specific ordering bug is found. |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system (`where Element.SubSequence == Slice<Self>`). Better protocol proxy emission (Session Q5) is the practical fix. |
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
