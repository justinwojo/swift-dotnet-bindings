# Roadmap

**Updated**: March 30, 2026

**Current baseline**: 89/90 CS compile, 55/56 Swift compile (SkeletonView + GRDB: known non-binding failures).
**Downstream validation**: 630/630 sim tests passing across 20 libraries (swift-dotnet-packages + sim-validation). Zero regressions on 0.5.0-dev packages.

> **Every skipped test is guilty until proven innocent.** 102/102 tests previously blamed on Mono JIT were proven to be generator/runtime bugs in our code. There are exactly 5 confirmed upstream .NET runtime bugs (see `Blocked` section below + memory `feedback_mono_jit_blame.md`). If a crash doesn't match one of these, it's our bug. Investigate generated C#/Swift wrapper signatures before ever labeling a failure as upstream.

**Completed work**: Runtime test sessions 1-10 and validation coverage sessions 11-13 are archived in `Completed/roadmap-runtime-validation-sessions.md`.

---

## Pre-Release Sessions (0.5.0)

Data-driven from binding-report.json across 15 sim-validation libraries: 2,850 bound members, 806 skipped (22% skip rate). These sessions target the highest-impact skip reasons to maximize API coverage before the 0.5.0 release.

### Session 1: Foundation TypeDatabase Expansion

**Target**: UnsupportedType — 52 skips across 15 libraries (6.4% of all skips)

The generator skips members when it can't resolve a type. Many are common Foundation/ObjC types with existing .NET counterparts that just need TypeDatabase entries.

| Type | Skips | .NET Equivalent | Action |
|------|------:|----------------|--------|
| `Foundation.NSNotification.Name` | 8 | `Foundation.NSString` (ObjC typedef) | Add to FoundationDatabase.xml |
| `Foundation.JSONEncoder` | 1 | `Foundation.NSObject` subclass | Add to FoundationDatabase.xml |
| `Foundation.CharacterSet` | 1 | `Foundation.NSCharacterSet` (ObjC bridge) | Add to FoundationDatabase.xml |
| `Foundation.Calendar` | 1 | `Foundation.NSCalendar` (ObjC bridge) | Add to FoundationDatabase.xml |
| `Foundation.Decimal` | 1 | `Foundation.NSDecimalNumber` or C# `decimal` | Add to FoundationDatabase.xml |
| `CoreMedia.CMTime` | 1 | Struct (8+4+4+4 bytes) | Add to CoreMediaDatabase.xml |
| `CoreGraphics.CGBlendMode` | 1 | `nint` enum | Add to CoreGraphicsDatabase.xml |
| `Security.SecTrustResultType` | 1 | `uint` enum | Add to SecurityDatabase.xml |

**Scope**: Add XML type database entries for resolvable Foundation/system types. Each entry makes the type available for property emission and method parameter/return handling. Does not require runtime changes.

**Validation**: `nuke validate` to confirm no regressions, re-run `binding-report.json` to measure skip reduction. Spot-check that newly-emitted members compile and have correct P/Invoke signatures.

**Estimated impact**: ~15-20 UnsupportedType skips eliminated. Some types like NSNotification.Name appear in multiple libraries (Alamofire has 8 notification properties alone).

---

### Session 2: Closure Handler Gap Analysis & Targeted Fixes — COMPLETE

**Target**: UnsupportedClosure — 110 skips across 11 libraries (12.7% of all skips)

**Investigation results** (110 closure-related skips classified from ABI JSON):

| Root Cause | Count | Fixable? | Status |
|-----------|------:|---------|--------|
| Existential/Result params (`any Error`, `Result<T, any Error>`) | ~27 | Blocked | Documented in Hard/Deferred |
| Generic type parameter closures (`τ_0_0 → τ_1_0`) | ~26 | Partial | GenericClosureBridge handles narrow pattern; broader needs monomorphization |
| Class/struct return types | ~10 | **Fixed** | C12 gate + fallback lambda + invoke thunk expansion |
| ArraySlice<T> with non-primitive T | ~9 | Blocked | No slice conversion in closure context |
| Enum params not invokable | ~26 | **Fixed** | IsInvocableParameter expanded for simple+complex enums |
| Existential/protocol return types | ~4 | Blocked | Abstract return type, no ABI |

**Changes made**:
1. **C12 gate expanded** (`MemberEmissionValidator.cs`): Allows closure properties with class/ObjC return types through when `IsInvokeThunkCompatibleReturn` confirms thunk support
2. **Fallback lambda class returns** (`ClosureEmitter.cs`): `EmitClosureReturnMarshalling` now wraps `void*` in `new ClassName(new SwiftHandle((IntPtr)...))` for class/ObjC returns
3. **Invoke thunk expansion** (`ClosureEmitter.InvokeThunk.cs`): `CanUseInvokeThunk` supports class/ObjC returns (Swift: `Unmanaged.passRetained().toOpaque()`, C#: SwiftHandle wrapping). Complex enum args via `assumingMemoryBound`
4. **IsInvocableParameter expansion** (`ClosureHandler.cs`): Added simple enum + complex enum support
5. **SwiftHandle constructor accessibility** (`ClassHandler.cs`): Made `internal` for cross-class closure return construction

**Validation**: PhoneNumberKit (4 closure properties with class returns) now passes. Zero regressions.

---

### Session 3: Foundation.Data Type Projection

**Target**: Data type across all libraries — currently blocks 2 Starscream tests + unknown number of skipped members across validation libraries where `Foundation.Data` appears in API signatures.

`Foundation.Data` is one of the most common types in Swift (networking, serialization, crypto, file I/O). In `@_cdecl` wrappers, `Data` gets ObjC-bridged to `NSData`, which the generator can handle as an ObjC class pointer. But the current projection pipeline doesn't have a `DataProjection` to convert between `byte[]`/`NSData`/`Swift.Data`.

**Approach**:
1. Add `Foundation.Data` → `Foundation.NSData` mapping to FoundationDatabase.xml (ObjC bridge)
2. Implement `DataProjection` following the pattern of `DateProjection` (Data ↔ NSData ↔ byte[])
3. Update closure handler to support Data params in closures (if applicable)
4. Add BindingTests coverage: Swift functions taking/returning Data, Data in struct properties, optional Data

**Key files**: `TypeProjectionFactory.cs`, `DateProjection.cs` (reference pattern), `FoundationDatabase.xml`, BindingTests Swift source.

**Validation**: `nuke test`, `nuke validate`, unskip Starscream WebSocketEvent.Binary/Ping tests, re-run sim-validation.

**Estimated impact**: Hard to quantify without grepping all ABI JSON for Data usage, but Data appears in nearly every networking/serialization library. Even if it only unlocks a handful of methods per library, the consumer value is high — Data is the Swift equivalent of `byte[]`.

---

### Session 4: Skip Metrics Tooling & Release Baseline

**Target**: Build tooling to measure binding coverage across all validation libraries, then establish the 0.5.0 release baseline.

**Approach**:
1. **Skip metrics script**: Aggregate `binding-report.json` across all `nuke validate` targets (not just sim-validation). Produce a summary: total bound/skipped by reason, per-library coverage percentages, comparison against previous baseline.
2. **Release baseline**: Run full `nuke validate`, full sim-validation, full swift-dotnet-packages tests. Document final numbers as the 0.5.0 baseline.
3. **Changelog**: Summarize what improved since 0.4.0 — runtime test fixes, validation coverage, new projections, skip reductions.

**Deliverables**: `build/scripts/skip-metrics.py`, `.validation-baseline.json` updated, release notes draft.

---

## Hard / Deferred

High skip counts but architecturally difficult. Not scheduled unless a specific consumer need drives them.

| Item | Skips | Libraries | Why Deferred |
|------|------:|----------:|-------------|
| **Unsupported signatures** (associated type refs, placeholder types) | ~353 | ~37 | Requires associated type resolution through conformance graph |
| **Generic type contexts** (generic parent leaks into wrapper) | ~349 | ~14 | Needs type-erased dispatch for non-final generic class members |
| **Method-level generics** (`func foo<T>(...)`) | ~179 | ~13 | Requires specialization or type-erased wrappers; C# has no generic constructors |
| **Protocol extension associated type context** | — | GRDB | 666 errors contained by gate. Needs full generic constraint context. EC-17 |
| **Architectural generic closures** | ~26 | RxSwift, Alamofire | Generic type parameters in closures (`(τ_0_0) -> τ_1_0`). GenericClosureBridge handles narrow pattern (sync, method-generic, noescape, identity-forwarding). Broader coverage needs monomorphization. |
| **Existential params in closures** | ~27 | Alamofire, RxSwift | `Result<T, any Error>`, union types with existential args. Can't fit existential container in C function pointer ABI. Would need delegate-based wrapper. |
| **ArraySlice<T> closures** | ~9 | CryptoSwift | `ArraySlice<UInt8>` cipher operations. No ArraySlice-to-Array conversion in closure context. |
| **Existential/protocol return closures** | ~4 | Various | Abstract return type, no ABI for constructing existential from void*. |
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
| **Skip metrics aggregation** | Small | Covered by Session 17 pre-release |

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
