# Roadmap

**Updated**: March 22, 2026

Previous sessions (0–14, architecture audit, post-audit fixes) archived in `Completed/roadmap-march-2026-sessions.md`.

---

## Current State (March 22, 2026)

| Metric | Value |
|--------|-------|
| Runtime tests (sim) | 757 pass, 32 skip |
| Runtime tests (device) | ~comparable |
| Unit tests | 8440 |
| Validation compile gate | 90/90 pass |
| Swift wrapper compilation | 51/56 ok |

### Validation failures (0 C# compile, 5 Swift wrapper)

**C# compile failures:** None (Session 1 fixed Kingfisher + Reachability; BlinkID/SVGView/StripePaymentSheet resolved post-baseline).

**Swift wrapper failures:**

| Library | Root Cause |
|---------|------------|
| GRDB | Protocol extension associated type context (EC-17) — architecturally blocked |
| Quick | XCTest dependency not found during wrapper compilation (not a generator bug) |
| SkeletonView | Internal type member gate — ~266 errors when lifted |
| StripeCryptoOnramp | Cross-module re-export (see Session C below) |
| TinyConstraints | x86_64-only xcframework, no arm64 simulator slice (stale build artifact) |

---

## Stability (Active — `sdk-0.3.0-validation-findings.md`)

Three sessions planned from the 0.3.0 validation review. These are the highest-priority work items.

| Session | Focus | Impact | Status |
|---------|-------|--------|--------|
| **1** | Generator bug fixes (Kingfisher + Reachability) + infrastructure | 2 BUILD_FAILED → PASS | **Done** |
| **2** | @_cdecl wrapper gap closure (class constructors, frozen struct SwiftIndirectResult, final class properties) | ~85 tests behind crash barriers unlocked | **Done** |
| **3** | SwiftOptional Mono marshalling investigation | ~22 tests behind crash barriers (DeviceKit, PhoneNumberKit) | Pending |

Full details, root cause analysis, and BindingTests plans in `sdk-0.3.0-validation-findings.md`.

---

## Post-Stability Sessions

### Session A: Runtime Safety + Validation Cleanup

**Runtime dispose safety:**
- `SwiftString`, `SwiftArray`, `SwiftDictionary`, and other `IDisposable` runtime types must throw `ObjectDisposedException` on post-dispose access. Currently, accessing a disposed handle reads invalid memory silently — violates .NET conventions and causes hard-to-debug crashes for consumers.
- Audit all public members on disposable runtime types. Add disposed-state checks.

**Remaining compile failures:**
- Investigate and fix BlinkID (5 errors), SVGView (5 errors), StripePaymentSheet (3 errors). These may already be fixed post-baseline (`9ab694c5` is behind HEAD) — re-run validation first.

**Coverage re-measurement:**
- The baseline coverage numbers (67% member emission, 66% @_cdecl) are from March 16. After 14+ sessions of work, these are stale. Re-measure to establish the actual current state and update the roadmap header.

**Validation**: `run-tests.sh` + `validate-libraries.sh`. `run-runtime-tests.sh --skip-regen` if dispose changes touch runtime.

---

### Session B: BindingTests Expansion

Two goals: add multi-module testing infrastructure, and re-enable ~165 disabled tests that are now viable after March work.

**Add `SwiftBindingsTestLibDependency` module:**
- New xcframework built alongside the main test library
- Contains types that the main `SwiftBindingsTestLib` imports and uses
- Tests cross-module type references (type from module A used as parameter/return in module B)
- Tests cross-module protocol conformances
- Tests namespace resolution when module name matches a type name

**Collision pattern coverage:**
- Emoji/non-ASCII identifiers (caught in Valet, BonMot validation)
- Case-insensitive enum case collisions (caught in SVGView — 21 errors)
- Module name = type name collisions (caught in Reachability)
- Nested type flattening with ObjC types (caught in SwipeCellKit)

**Re-enable disabled test domains (~165 tests):**
- `Lifetime/` — non-async ownership tests (~40 tests, C# test file exists). Skip async functions with reason.
- `MemoryManagement/` — LeakDetection + LibraryEvolution (~60 tests). Skip RetainCycles (weak/unowned not supported).
- `Initializers/` — BasicInit + Failable + Throwing (~15 tests). All patterns now supported post-Phase 44.
- `ObjCInterop/` — NSObjectSubclass + ObjCAttributes (~25 tests). Skip Selectors (partially supported).
- `EdgeCases/` — Deprecation + Keywords + Visibility (~15 tests). Skip Unicode (needs emitter verification).
- `Parameters/` — Inout (~10 tests). Skip Defaults (not P/Invoke-expressible) and complex Variadic.
- Keep `PropertyWrappers/` disabled (genuinely unsupported — no generator support).

**Validation**: `run-tests.sh` + `build-and-test.sh` (new module + re-enabled tests need full rebuild). `validate-libraries.sh` to confirm no regressions.

---

### Session C: Consumer API Quality

Group all consumer-facing quality improvements into one session.

**ExistentialContainer API cleanup** (deferred from Session 3):
- `ExistentialContainer0-8` leak into public method signatures (closure params, protocol proxy constructors). Consumers see internal marshalling types in IntelliSense — the #1 "feels unfinished" signal.
- Map containers to protocol interfaces where possible. Gate on `AllProtocolsHaveTypeRecords()` for unregistered protocols like `Swift.Error`.

**nint return type convenience:**
- Properties like `Count`, `Index`, `HashValue` return `nint` where C# consumers expect `int`. Method parameters already get `int` overloads, but return types don't. Add `int`-returning convenience properties/methods where safe (values guaranteed to fit in Int32).

**AnyType fallback improvement:**
- Consumers get `AnyType` for unsupported types with no guidance. Add XML doc comments explaining what `AnyType` means, why it appears, and what (if anything) the consumer can do.

**StripeCryptoOnramp cross-module re-export:**
- Generator emits `StripeCryptoOnramp.STPAPIClient` but the type's canonical module is `StripePayments`. Swift wrapper fails because the re-exported type requires module-qualified access. Fix: detect cross-module re-exports and use canonical module in wrappers.

**SWIFTBIND error documentation:**
- 13 error codes exist (SWIFTBIND010–094) with no consumer-facing reference. Add actionable descriptions and resolution steps to the wiki Troubleshooting page. Improve in-code messages to suggest next steps (e.g., SWIFTBIND060 should explain `<SwiftFrameworkDependency>`).

**Validation**: `run-tests.sh` + `validate-libraries.sh` (StripeCryptoOnramp should go from wrapper fail to pass).

---

### Session D: Feature Expansion + Gate Relaxation

Feature gaps and overly conservative skip gates that recover meaningful member counts.

**Optional\<Primitive/Enum\> in closures** (deferred from Session 1):
- Currently gated due to "risky ABI change (tag-byte layout vs pointer-based Optional)". But `SwiftOptional<T>` already works in BindingTests (14/14 pass) for non-closure contexts. Verify the ABI matches for closure params with focused BindingTests: `Optional<Int32>`, `Optional<Bool>`, `Optional<CustomEnum>` in closure parameters.
- If ABI verifies, lift the gate in `IsCdeclCompatibleType`. ~5-10 skips recovered (RxSwift, Alamofire closure patterns).

**String enum raw values via .swiftinterface parsing:**
- Currently "blocked — no data source in compiled xcframeworks." But `.swiftinterface` files (which the generator already reads for access-level parsing) contain `case foo = "bar"` literals.
- Extend `SwiftInterfaceAccessParser` to extract string raw values. Emit as static string properties on the enum. ~20-30 skips recovered (GRDB, CryptoSwift).

**Overly conservative skip gate relaxation:**
- `Optional<Closure>` in bound generics: `BoundGenericsHandler.HasNonSwiftObjectGenericArg()` rejects this pattern, but BindingTests shows 14/14 Optional closure tests pass. Whitelist `Optional<Closure>` as valid. ~5-10 members per library.
- Associated type references: gate skips entire method when ANY parameter has an associated type ref. Could skip only the problematic parameter and emit partial signatures. ~10-20 members per library.

**Validation**: `run-tests.sh` + `validate-libraries.sh` + `build-and-test.sh` for new BindingTests.

---

### Session E: Generator Code Health

Reduce duplication and regression risk in the generator without changing behavior.

**Validation rule consolidation:**
- 8 separate validator classes with overlapping gate checks. `IsUnsupportedModule` has two divergent implementations across 23 call sites. `HasUnsupportedProtocolConstraints` is checked in both `MethodValidationGates` and `MemberValidationPipeline`. Fixing a validation bug currently requires updating 3+ places.
- Create a unified `ValidationRuleSet` with canonical gate predicates. All validators query it instead of reimplementing checks. Eliminates divergence risk.

**Program.cs extraction:**
- 2,114-line monolithic CLI entry point mixing option definitions, handler logic, and business logic. Extract `CliOptions` class and `BindingsGeneratorCommand` handler. Improves testability and readability.

**TODO/dead code cleanup:**
- 27 TODO/HACK/FIXME comments found. At least 5 guard unreachable code (future-proofing that adds noise). Remove dead paths, document deferred features in roadmap instead of comment-gated code.

**Validation**: `run-tests.sh` only (no behavioral changes).

---

### Session F: Generator Test Coverage Gaps

Coverlet coverage analysis (March 21) found the generator at 74.4% line coverage overall, but with 6 emitter files at 0% and 3 more under 20%. These are the highest regression risk areas — bugs here would go undetected by the test suite.

**Zero-coverage emitter files (2,702 lines):**

| File | Lines | What it does |
|------|-------|-------------|
| `ProtocolExtensionClosureBridge.cs` | 956 | Closure bridging in protocol extensions |
| `CrossModuleExtensionEmitter.cs` | 604 | Cross-module extension type dispatch |
| `ClosureEmitter.StructParams.cs` | 542 | Struct parameters in closure adapters |
| `MarkerProtocolOverloadEmitter.cs` | 336 | Sendable/Copyable overload generation |
| `ClosureEmitter.Async.cs` | 246 | Async closure emission |

**Low-coverage emitter files (<20%, 2,680 lines):**

| File | Coverage | Lines |
|------|----------|-------|
| `ForeignTypeExtensionEmitter.cs` | 1% | 1,458 |
| `GenericClosureBridgeEmitter.cs` | 3% | 1,222 |

**Projection unit tests:**
- All 26 Marshaler/Projection files (ArrayProjection, BoolProjection, ExistentialProjection, DataProjection, etc.) have zero dedicated unit tests. Currently tested only indirectly through TypeProjectionFactory composition. Add at minimum one test per `Visit()` override per projection type.

**Validation**: `run-tests.sh` only (no behavioral changes). Re-run Coverlet after to verify improvement.

---

### Session G: Generated Code Size Reduction

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
| SwiftUI type public construction | Consumer ergonomics | Small |
| SDK property documentation (`SwiftPlatformTarget`, `SwiftGeneratorVerbosity`, etc.) | Consumer onboarding | Small |
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
