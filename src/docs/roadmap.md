# Roadmap

**Updated**: March 30, 2026 (Session 6)

**Current baseline**: 89/90 CS compile, 55/56 Swift compile (SkeletonView + GRDB: known non-binding failures). 43 `[Skip]` + 9 `[SkipOnDevice]` = 52 skipped runtime tests in BindingTests.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 5 confirmed upstream .NET runtime bugs (see `Blocked` section below + memory `feedback_mono_jit_blame.md`). If a crash doesn't match one of those 5, it's our bug. Investigate generated C#/Swift wrapper signatures before ever labeling a failure as upstream.

---

## Sessions

Work is organized into self-contained sessions. Each session targets a theme, lists the BindingTests skips it should resolve, and estimates scope. Sessions are ordered by impact and feasibility. **All skipped tests listed below are expected to be fixable** — investigate root causes, don't skip them.

### ~~Session 1: Closure & Callback Fixes~~ ✅ Complete

**Result**: 7/7 skipped tests now passing on simulator. Zero validation regressions.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Frozen struct/enum indirect return | 3 | `ClosureEdgeCaseTests`: TestClosureReturningFrozenPoint, TestClosureReturningEnum, TestClosureReturningFrozenPointWithParam — moved from direct `@convention(c)` return to indirect buffer-based return; replaced `unsafeBitCast` with safe `.rawValue`/pointer-load for enums |
| Throwing closure String return | 1 | `ClosureEdgeCaseTests`: TestThrowingWithParamSuccess — added SwiftString intermediary for callback String returns (System.String has no Swift metadata); made SwiftResult payload lazy to avoid metadata crash on type load |
| MCB complex enum ownership | 3 | `StructClosureBridgeTests`: TestDataTransformerProcess, TestDataTransformerProcessNegativeFactor, TestClassTransformerProcess — removed `defer` deallocation for heap-allocated complex enum args (C# takes ownership via NewFromPayload) |

Additional fixes from code review: ARC leak in buffer return paths (`load(as:)` → `move()`), string-backed enum closure ABI guard, SwiftResult C#-only interop guards.

### ~~Session 1.5: Validation Regression Fix~~ ✅ Complete

**Result**: 89/90 CS compile, 55/56 Swift compile restored. All 5 regressions fixed, zero new regressions.

| Fix | Libraries | What Changed |
|-----|----------|-------------|
| Async closure Data ABI type | Nuke (ios/macos/tvos) | `AsyncThrowingClosureState<T>` used projected `byte[]` instead of ABI `Swift.Data`; used `TypeProjectionFactory.PInvokeType` for state type + added `byte[]` → `Swift.Data` conversion wrapper |
| Closure enum `.rawValue` cast | StripePayments | Session 1 changed `unsafeBitCast` to `.rawValue` but `Int`-backed enums return `Swift.Int` not `Int64`; wrapped in explicit scalar cast `Int64(arg.rawValue)` in `ClosureEmitter.SwiftWrapper` + `NestedClosureBridge` |
| DynamicSelf guard depth | StripePayments | `hasDynamicSelfReturn` only checked top-level `IsDynamicSelf`, missing `Optional<Self>`; switched to `TypeSpec.HasDynamicSelf` which covers all nested shapes |
| @autoclosure invocation | SwiftyBeaver | `OptionalPointerWrapperEmitter` passed `@autoclosure () -> Any` closure directly where `Any` expected; added `()` invocation suffix matching `MethodWrapperEmitter` pattern |

### ~~Session 2: Optional & Metadata Fixes~~ ✅ Complete (d511fe4)

**Result**: 8/8 skipped tests now passing on simulator. Zero validation regressions. 1204 pass / 0 fail (up from 1201/3).

| Fix | Tests | What Changed |
|-----|------:|-------------|
| SwiftOptional\<T\> metadata for simple enums | 5 | Generator emits @_cdecl metadata wrappers for simple enums, registered via module initializer. Runtime fast paths handle C#↔Swift size mismatch (C# `enum : int` = 4 bytes, Swift enum = 1 byte) |
| Optional Bool extra-inhabitant encoding | 1 | Constructor memcpy fast path for Bool bypasses VWT InitializeWithCopy which corrupts extra-inhabitant encoding on Mono |
| Optional\<T\> return marshalling for value types | 2 | Fixed `ToNullable()` bug: in C# generics, `T?` with unconstrained T is `T` (not `Nullable<T>`). Generator now emits explicit `HasValue`/`Some` check with nullable cast |

### ~~Session 3: Protocol & Existential Wrapper Fixes~~ ✅ Complete (fe50a00)

**Result**: 7/9 skipped tests now passing on simulator. 2 remaining tests (RouteEvent/GetDelegateName) moved to `[SkipOnSimulator]` — string callback vtable issue, Session 7 territory. +2 Swift validation improvements (ObjectMapper, Parchment). 1211 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Protocol closure wrapper stripping | 5 | Added EventDelegate to SwiftSourceStripper PreservedProtocols so witness table isn't cascade-stripped |
| Existential `any` keyword in @_cdecl wrappers | 2 | CdeclParamMapper now emits `any` prefix for protocol existentials in Swift 6 output, with correct `Optional<any Protocol>` handling |
| String vtable callbacks (deferred) | 2 | RouteEvent/GetDelegateName hit Mono JIT async assertion via string callback through vtable — deferred to Session 7 |

### ~~Session 4: SwiftString.Buffer ABI Decomposition~~ ✅ Complete (c7670e2)

**Result**: 2/2 skipped tests now passing. Zero validation regressions despite high-risk ABI change. 1213 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| SwiftString.Buffer ABI (4+ string params) | 1 | Decomposed 16-byte Buffer struct into two `nint` P/Invoke parameters (_w0, _w1), matching Swift's two-Int-word layout. Fixes ARM64 AAPCS64 register overflow with 4+ string params |
| String enum raw values | 1 | CSSProperty enum cases now round-trip correctly via string raw value support |

### ~~Session 5: Variadic, ObjC & Cross-Module~~ ✅ Complete (af8518f)

**Result**: 8/8 skipped tests now passing. Zero validation regressions. 1221 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Variadic parameter support | 3 | Removed MemberValidationPipeline gate — `T...` is `Array<T>` at ABI level, CallConvSwift dispatches correctly via `SwiftArray<T>` |
| ObjC Selector type | 3 | Added Selector/ObjCBool to FoundationDatabase.xml (TypeSpecParser rewrites `ObjectiveC.*` → `Foundation.*`) |
| URL protocol bridge | 1 | EveryProtocolEmitter converts URL→AnyObject for vtable params/returns so C# receives NSURL pointer |
| Cross-module closure | 1 | Wrapper already generated correctly, removed stale `[Skip]` |

### ~~Session 6: Opaque Types & Protocol Conformance~~ ✅ Complete (pending commit)

**Result**: 2/2 skipped tests now passing on simulator. Zero validation regressions. 1223 pass / 0 fail.

| Fix | Tests | What Changed |
|-----|------:|-------------|
| Opaque return type marshalling | 1 | `WrapperStrippingTests`: TestMixedEmittabilityOpaqueReturn — the @_cdecl wrapper already correctly boxes `some CustomStringConvertible` to `any Protocol` existential container; C# reads `ExistentialContainer1` and returns as `object`. Skip was premature — removed |
| Protocol conformance via generic dispatch | 1 | `LifetimeTrackingTests`: TestOwnableProtocolConformance — `some Ownable` is ABI sugar for `<T: Ownable>`; generated C# generic with `CallConvSwift` + type metadata + protocol witness table dispatch works correctly. Filled in empty test body |

### Session 7: String Callback Fixes

**Target**: 4 skipped tests
**Focus**: Fix string callback marshalling. These are labeled "Mono JIT" in skip reasons but **are almost certainly generator bugs** — investigate the generated C#/Swift wrapper signatures first.

| Bug | Tests | Root Cause |
|-----|------:|-----------|
| String callback marshalling | 4 | `ClosureTests`: TestClosureWithOptionalStringReturn, TestClosureWithStringArrayReturn, TestLogRouterSetHandler/ClearHandler — currently blamed on Mono JIT async assertion + NativeAOT metadata resolution, but jit-info.c assertions are always secondary symptoms of our bugs. Investigate wrapper signatures. |

### Session 8: Noncopyable Type Wrapper Generation

**Target**: 11 skipped tests
**Focus**: Fix `@_cdecl` wrapper generation for `~Copyable` types. The wrappers are stripped during Swift compilation because they emit `.pointee` copy semantics that `~Copyable` types reject. This is a generator bug, not a Swift language limitation.

| Bug | Tests | Root Cause |
|-----|------:|-----------|
| UniqueResource wrapper stripping | 11 | `ClassMarshallingTests` (2), `OwnershipTests` (4), `OwnershipGCStressTests` (2), `DisposeScopeTests` (1), `NegativePathTests` (1) — wrapper emitter generates `.pointee` copy for `~Copyable` types; needs `consuming`/`borrowing` parameter semantics instead |

### Session 9: Async Device Crash Investigation

**Target**: 9 `[SkipOnDevice]` tests
**Focus**: Investigate async P/Invoke crashes on device. These are labeled "NativeAOT SIGBUS" but **must be investigated as our bugs first**. Only confirmed upstream issue #5 (Mono SafeHandle async lifetime) is even close, and that was never independently reproduced. Check generated signatures, calling conventions, and parameter counts before blaming the runtime.

| Bug | Tests | Root Cause |
|-----|------:|-----------|
| Async factory method device crashes | 9 | `AsyncFactoryMethodTests`: TestLoadAnimationFromFile/Data/Url/EmptyPath/InvalidUrl/NonHttpUrl, TestLoadBundleFromFile/EmptyPath, TestBundleAnimationByIndex — investigate wrapper signatures and SafeHandle marshalling; likely generator bugs in async return path |

### Session 10: Generic Edge Cases

**Target**: 3 skipped tests
**Focus**: Fix generator bugs in generic type handling. The "multi-type-param generic SIGSEGV" is a confirmed upstream NativeAOT issue (#4), but the "unbound type parameters" test is a generator bug.

| Bug | Tests | Root Cause |
|-----|------:|-----------|
| Unbound type parameter resolution | 1 | `BoundGenericEdgeCaseTests`: TestMakePairDescriptionSkipped — generator can't resolve unbound type parameters |
| Multi-type-arg bound generic struct | 1 | `BoundGenericEdgeCaseTests`: TestMakeRefPair — investigate before assuming upstream issue #4 |
| Method-level generic free function | 1 | `BasicGenericTests`: TestGetPairSameType — 2 type params; investigate wrapper generation before blaming NativeAOT |

---

## Coverage Expansion (Validation Libraries)

These sessions target skip reasons across the 90 validation libraries, not BindingTests. Run `nuke validate` to measure impact. Skip counts are estimates from last full analysis — re-measure when starting each session.

### Session 11: Async Properties

**Estimated skips**: ~14 across 5 libraries
**Approach**: Async methods already work via `AsyncProjection`. Extend to property getters by emitting Task-returning methods (C# properties can't be async).

### Session 12: ObjC-Bridged Optional Setters

**Estimated skips**: ~90 across 11 libraries
**Approach**: Setter paths for optional ObjC-bridged types. Likely needs nullable dispatch in property setter emission.

### Session 13: inout Parameters

**Estimated skips**: ~14 across 2 libraries
**Approach**: `inout` write-back semantics in @_cdecl wrappers. The wrapper needs to accept a pointer, call the method, then write back the modified value.

---

## Hard / Deferred

High skip counts but architecturally difficult. Not scheduled unless a specific consumer need drives them.

| Item | Skips | Libraries | Why Deferred |
|------|------:|----------:|-------------|
| **Unsupported signatures** (associated type refs, placeholder types) | ~353 | ~37 | Requires associated type resolution through conformance graph |
| **Generic type contexts** (generic parent leaks into wrapper) | ~349 | ~14 | Needs type-erased dispatch for non-final generic class members |
| **Method-level generics** (`func foo<T>(...)`) | ~179 | ~13 | Requires specialization or type-erased wrappers; C# has no generic constructors |
| **Protocol extension associated type context** | — | GRDB | 666 errors contained by gate. Needs full generic constraint context. EC-17 |
| **Architectural generic closures** | ~45 | RxSwift, Alamofire | `subscribe`/`flatMap`, interceptors |
| **Unsupported generic containers** | ~71 | ~20 | `Result<T,E>`, `Optional<existential>` |
| **Custom actor types** (`actor Counter`) | — | 5+ | Requires async dispatch through actor's serial executor |
| **Static protocol constructors** (`Create()` factory) | — | — | Init witness dispatch needs allocation infrastructure |
| **Weak/unowned references** | 4 tests | — | Requires ownership tracking infrastructure (LeakDetectionTests) |

---

## Blocked (Confirmed Upstream Only)

These are the **only** confirmed upstream issues. There are exactly 5 (reproduced in standalone repro at `/Users/wojo/Dev/swift-interop-repro/`). If a crash doesn't match one of these, it's our bug. See `feedback_mono_jit_blame.md` for the full investigation checklist.

| # | Issue | Blocked By |
|---|-------|-----------|
| 1 | **Non-blittable type rejection with CallConvSwift** | .NET runtime design limitation. Workaround: @_cdecl wrappers (already covers 78.5% of P/Invokes) |
| 2 | **NativeAOT: custom struct float/double in GPR instead of FPR** | NativeAOT JIT `SwiftPhysicalLowering.cs` register allocation bug. System types (CGRect) work; custom structs fail |
| 3 | **NativeAOT: custom integer struct >16B SIGSEGV** | Struct passed by pointer instead of in registers. NativeAOT (>24B), Mono (>32B) |
| 4 | **NativeAOT: multi-type-parameter generic SIGSEGV** | 1 type param works, 2+ type params SIGSEGV |
| 5 | **Mono: SafeHandle async lifetime** (inferred, not independently reproduced) | GC may collect SafeHandle during async suspension |

| Other | Status |
|-------|--------|
| **Non-Int32 enum raw values** | Blocked on Swift compiler: `.swiftinterface` strips integer raw values. No workaround. 1 skipped test. |
| **Upstream bug reports** (7 drafts) | Blocked on repo going public. Drafts: `Future/upstream-bug-reports-draft.md` |

---

## Future Vision

| Item | Effort | Notes |
|------|--------|-------|
| **Performance benchmarks** | Medium | `Future/interop-performance-validation-plan.md` |
| **API snapshot tooling** (detect API surface drift) | Medium | `Future/api-snapshot-tooling.md` |
| **Skip metrics aggregation** | Small | Script to aggregate skip reasons across all validation libraries post-`nuke validate` |

---

## Not Worth Addressing

| Skip Reason | Count | Why Not |
|-------------|------:|---------|
| @_spi / internal members | ~795 | Correct behavior — private API should not be bound |
| Synthesized Codable | ~155 | .NET consumers use own serialization (`System.Text.Json`, etc.) |
| SwiftUI/Combine dependencies | ~60 | Framework boundary — consumers use SwiftUI bridge instead |
| Generic protocol constraints / PATs | ~68 | Architecturally blocked by associated type erasure |
| Unsatisfied ISwiftObject | ~104 | Fundamental type system constraint — generic args must be projectable |

---

## Explicitly Out of Scope

| Item | Reason |
|------|--------|
| Full Swift type graph infrastructure | Over-engineered for current needs |
| Deep generic signature / associated type constraint emission | C# generics can't express Swift's full type system |
| Result builder (`@resultBuilder`) projection | Compile-time Swift feature, no ABI JSON representation |
| `@dynamicMemberLookup` / KeyPath projection | Affects <5 types across 53 validation libraries |
| Composing SwiftUI view trees from C# | Result builders are a compiler feature |
| Structs projected as C# value types | Only safe for frozen+blittable subset; marginal benefit |
