# Roadmap

**Created**: February 2026
**Updated**: February 2026
**Status**: Active — consumer API quality focused
**Source**: Consolidated from binding analysis review, existing roadmap, Future/ design docs, and testing gaps.

For completed work, see `Completed/` (notably `roadmap-sessions-1-14.md`, `roadmap-completed-feb2026.md`, `phases-a-through-g.md`, `phases-h-through-wu.md`, and `developer-experience.md`).

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 2,782 passing |
| Integration tests | 699 passing (11 skipped, pre-existing) |
| Runtime library tests | 156 passing |
| Runtime tests | 188 passing at Tier 2 (28 pre-existing failures, allowlist-based crash tolerance) |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 25 clean (0 generator errors) + 5 environmental-only |

---

## Priority Key

- **P0**: Blocks production use — silent crashes, data loss, unusable core APIs
- **P1**: Major DX friction — consumers hit these immediately and may abandon
- **P2**: Quality gaps — noticeable but workable, erodes confidence over time
- **P3**: Polish — professional quality, long-term sustainability
- **P4**: Future vision — architectural improvements, new capabilities

---

## Work Sessions

Items grouped by shared code paths, shared context, and realistic single-session scope. Sessions are ordered by priority — highest-impact work first.

### ~~Session 15: Type System Quality~~ (Done)

**Priority**: P1/P2 | **Type**: Done | **Risk**: Medium

| Item | Status | Summary |
|------|--------|---------|
| **`GetHashCode()` via Swift Hashable** | Done | `SwiftHashable.cs` runtime helper P/Invokes `$sSH9hashValueSivgTj` (dispatch thunk). Emitter conditionally generates `SwiftHashable.GetHashCode(this)` for Hashable-conforming types, `return 0` fallback for Equatable-only. `ISwiftHashable` conformance descriptors emitted for PWT lookup. |
| **Enum case parameter naming** | Done | `SwiftInterfaceAccessParser.GetEnumCaseLabels()` parses `.swiftinterface` for case labels (including `indirect case`). Labels threaded through `SwiftABIParser` → `TypeSpec.TypeLabel`. Fully-qualified keys (`Parent.Nested.caseName`) prevent collision between same-named nested enums. |
| **No-payload String enums as C# `enum`** | Done | `EnumDecl.IsStringRawValueSimpleEnum` predicate (frozen, no associated values, no methods/properties). Emits C# `enum` with tag-based values + `ToRawValue()`/`FromRawValue()` extension methods. Enums with methods keep class-based emission. |
| **Finalizer safety net** | Done | `SwiftDispose.FinalizerCleanup<T>()` — NativeAOT calls `Dispose()` (VWT Destroy), Mono no-ops (avoids jit-info.c crash). Destructors + `GC.SuppressFinalize(this)` emitted in all class/complex-enum/non-frozen-struct handlers. |
| **Info suffix collision strategy** | Done | `HasUnsupportedPropertyType()` filters SwiftUI/Combine references + unresolvable AnyType-fallback types from collision set. `TryGetTypeRecord` result checked against `AnyType` sentinel to catch existential/generic false positives. Well-known modules (`Swift`, `Foundation`, etc.) bypass DB lookup. |

**Key files**: new `SwiftHashable.cs`, new `SwiftDispose.cs`, `TypeHandlerHelpers.cs`, `ClassHandler.cs`, `EnumHandler.cs`, `EnumHandler.SimpleEnum.cs`, `EnumDecl.cs`, `SwiftInterfaceAccessParser.cs`, `SwiftABIParser.cs`, `MemberEmissionValidator.cs`, `NameProvider.cs`

---

### Session 16: Roslyn Analyzer

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

Entirely new project — a Roslyn analyzer that warns when `ISwiftObject` types aren't disposed. No overlap with any generator code. Requires Roslyn analyzer development experience but is self-contained.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **Analyzer for undisposed Swift objects** | P3 | Medium | Warn on `ISwiftObject` / `SwiftSafeHandle<T>` locals without `using`, fields without dispose in containing type. Package in `Swift.Runtime` NuGet. |

**Key files**: New analyzer project, `Swift.Runtime` NuGet packaging
**Verification**: Analyzer unit tests + manual validation in a test consumer project
**Design**: `Future/roslyn-analyzer-plan.md`

---

### Session 17: Golden API Snapshots

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

New tooling to detect API surface drift. Standalone scripting — no generator changes. Should be done before CI integration (Session 18) since the snapshot tool feeds into the CI pipeline.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **API snapshot tooling** | P3 | Medium | Script extracts public API surface from generated `.cs` files. Baseline snapshot checked into repo. Clear diff output for added/removed/changed members. |

**Key files**: New scripts in `TestFramework/`
**Verification**: Run against TestFramework generated bindings, verify baseline matches current output
**Spec**: `testing-gaps.md` Gap 9

---

### Session 18: CI Integration

**Priority**: P3 | **Type**: Implementation | **Risk**: Medium
**Depends on**: Session 17 (API snapshots)

GitHub Actions workflow. Large but well-scoped — the tiered test system was designed for CI but not yet wired up.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **GitHub Actions CI** | P3 | Large | macOS runner. Tier 1 on every PR, Tier 2 before merge, Tier 3 nightly. Real-world library validation on merge. API snapshot comparison. |

**Key files**: New `.github/workflows/`, existing test scripts
**Verification**: PR opens, CI runs, tests pass
**Spec**: `testing-gaps.md` Gap 10

---

### Session 19: Performance Benchmarks

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

New standalone project. No overlap with generator or runtime code. Measures interop overhead — valuable for confidence but doesn't change behavior.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **Interop performance benchmarks** | P3 | Medium | 5 CI perf smoke scenarios (<60s, ratio-based thresholds). Standalone BenchmarkDotNet harness for deep investigation. Baseline variance. |

**Key files**: New `perf/` directory, Swift test functions in TestFramework
**Verification**: Benchmark suite runs, produces ratio metrics
**Design**: `Future/interop-performance-validation-plan.md`

---

### Session 20: SwiftUI Bridge Corpus (Phase 4)

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

Extends the SwiftUI bridge with coverage tracking across a real library corpus. Standalone — builds on existing bridge infrastructure.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **Bridge corpus + 3-tier metrics** | P3 | Medium | Track bridge coverage (generated / typechecked / runtime-validated) across 10+ libraries. Reproducible corpus with pinned versions + hashes. Regression detection. |

**Key files**: `BindingReport.cs`, `ReportCollector.cs`, new `bridge-corpus/` directory
**Verification**: `generate-bridge-coverage.sh` produces per-library and aggregate metrics
**Design**: `Future/swiftui-bridge-v2-plan.md` (Phase 4)

---

### Session 21+: Library Validation Expansion

**Priority**: P2 | **Type**: Implementation | **Risk**: Low
**Multiple sessions** — one per library set

Runtime test apps for additional libraries beyond the current 5 (Nuke, BlinkID, Lottie, CryptoSwift, BridgeTest). Each library is a self-contained session.

| Session | Libraries | Notes |
|---------|-----------|-------|
| 21a | Alamofire | Popular networking, good API coverage test |
| 21b | Stripe end-to-end | Multi-module with `--framework-dependency` chain |
| 21c | Additional 2-3 | Based on ecosystem demand |

**Verification**: Per-library `build-all.sh` + `validate-sim.sh`

---

## Multi-Session Efforts (P4)

These are too large for single sessions. Each needs a **planning session first** to scope implementation, then multiple implementation sessions.

### P4-1: Class Inheritance Hierarchy

**Effort**: Very Large (5+ sessions estimated)
**Research ref**: Bad #1 (everything is ISwiftObject)
**Prerequisite**: P4-2 (ObjC binding integration design informs NSObject bridging)

Emit C# class hierarchies mirroring Swift type graph. Requires: cross-module inheritance chain resolution from ABI JSON `inheritsFrom`, diamond inheritance handling with protocols, `base()` constructor chains, bridging into `Foundation.NSObject`.

### P4-2: ObjC Binding Integration

**Effort**: Large (3-5 sessions estimated, ~2-3 weeks)
**Design**: `Future/objc-binding-integration.md`

Replace Objective Sharpie. Uses `clang -ast-dump=json`. Same CLI/SDK for Swift and ObjC. ~1,500-2,000 lines new code. Suggested session breakdown:
1. Detection + routing
2. Clang AST parser
3. ApiDefinition emitter
4. Binding project emission + SDK integration
5. Validation against known ObjC frameworks

### P4-3: Emitter Architecture Redesign

**Effort**: Very Large (5+ sessions estimated)
**Design**: `Future/emitter-redesign-proposal.md`

Three-phase architecture: type pre-processing (graph traversal + label assignment), type processing (handler-based member population), emission from representations. High risk — touches everything.

### P4-5: Multi-Platform Support

**Effort**: Large (3+ sessions estimated)
**Design**: `Future/dx-multi-framework-auto-detection.md` (Platform Coverage)

Extend beyond iOS to Mac Catalyst, macOS, tvOS.

---

## Blocked on Upstream (.NET Runtime)

These require changes in `dotnet/runtime`. Workarounds are in place. Draft bug reports ready in `Future/upstream-bug-reports-draft.md`.

| Issue | Root Cause | Current Mitigation | Unblocked When |
|-------|-----------|-------------------|----------------|
| **SafeHandle finalizer crashes on Mono** | `VWT->Destroy()` via indirect CallConvSwift -> JIT assertion | MutableProps tests at Tier 3; consumers must call `Dispose()` | Mono JIT CallConvSwift fix |
| **Non-blittable types with CallConvSwift** | .NET requires all CallConvSwift P/Invoke params be blittable | Wrapper methods for known patterns; `MonoJitRiskDetector` flags | dotnet/runtime adds managed type marshalling for CallConvSwift |
| **Async runtime tests (32 tests, all Tier 3)** | Mono JIT assertion on CallConvSwift in async P/Invoke | Tests written and ready; tagged Tier 3 | Same as above |
| **Non-primitive closure Cdecl** | Strategy B only covers primitive-arg closures | Non-primitive closures fall back to CallConvSwift | Mono JIT fix OR Swift-side marshal adapters (high difficulty) |
| **SafeHandle in async P/Invoke** | .NET runtime doesn't preserve SafeHandle through async continuation | Singleton pattern detection + IntPtr conversion | dotnet/runtime adds SwiftSelf register support with async Task capture |
| **VWT InitializeWithCopy** | Indirect CallConvSwift function pointer in `MarshalToSwift` | No known test failures yet | Same as VWT Destroy |

**Tracking issues**: [#93631](https://github.com/dotnet/runtime/issues/93631) (.NET 9), [#108662](https://github.com/dotnet/runtime/issues/108662) (.NET 10), [#64215](https://github.com/dotnet/runtime/issues/64215) (CallConvSwift), [#80905](https://github.com/dotnet/runtime/issues/80905) (NativeAOT iOS).

---

## Known Generator Bugs (Tracked, not prioritized)

Workarounds exist for all. Not blocking any library validation.

| Bug | Impact | Workaround |
|-----|--------|------------|
| String enum raw values use case names | ABI JSON lacks individual case raw values | Case names used; cosmetic only |
| `UnsafePointer<T>` -> AnyType | No concrete projection for immutable pointers | Use `UnsafeMutablePointer<T>` |
| Named tuples with String elements | `(SwiftString.Buffer, ...)` -> `(SwiftString, ...)` CS0029 | Avoid String in named tuples |
| Throwing closure thunks | `SwiftString` return emitted as `void*` | Exclude throwing closures |
| `async throws(ErrorType)` free functions | Emit `_payload`/`this` in static context | Guarded — no runtime impact |
| ExistentialContainer0 in tuple element | Lottie edge case | Not reached by current guards |
| `Optional<T>` P/Invoke truncation for T.Size > 8 | `PayloadBuffer<IntPtr>` passes 8 bytes; `Optional<String>` is 16 bytes | **Partially fixed** — `_optbuf` Swift wrapper covers standalone methods, frozen struct constructors, property setters, and mutating methods for String/Int/UInt/Int64/UInt64/Double. Still truncated for: async, wrapper-owned (closure Cdecl, opaque return), and Optional return values. |

---

## Session Summary

| Session | Priority | Type | Effort | Theme |
|---------|----------|------|--------|-------|
| ~~14. Consumer API Polish~~ | P1 | Done | Medium | `IDisposable`, `[EditorBrowsable]`, shared helpers, SwiftString in factories, module stutter |
| ~~15. Type System Quality~~ | P1/P2 | Done | Medium-Hard | `GetHashCode`, enum param naming, String enums as C# enum, finalizer, Info suffix |
| **16. Roslyn Analyzer** | P3 | Implement | Medium | Undisposed ISwiftObject warnings |
| **17. API Snapshots** | P3 | Implement | Medium | API surface drift detection |
| **18. CI Integration** | P3 | Implement | Large | GitHub Actions tiered pipeline |
| **19. Perf Benchmarks** | P3 | Implement | Medium | Interop overhead measurement |
| **20. SwiftUI Corpus** | P3 | Implement | Medium | Bridge coverage tracking |
| **21a-c. Library Validation** | P2 | Implement | Medium each | Runtime test apps for additional libraries |
| **P4-1 through P4-5** | P4 | Multi-session | Large-Very Large | Architecture: inheritance, ObjC, emitter redesign, multi-platform |
