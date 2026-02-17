# Roadmap

**Updated**: February 2026
**Status**: Active

For completed work, see `Completed/`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 2,916 passing |
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

## Tasks

### 1. Library Validation Expansion

**Priority**: P2 | **Effort**: Medium per library | **Risk**: Low

Runtime test apps for additional libraries beyond the current 6 (Nuke, BlinkID, Lottie, CryptoSwift, BridgeTest, Alamofire). Each library is self-contained.

| Target | Notes |
|--------|-------|
| Stripe end-to-end | Multi-module with `--framework-dependency` chain |
| Additional 2-3 | Based on ecosystem demand |

**Verification**: Per-library `build-all.sh` + `validate-sim.sh`

---

### 2. CI Integration

**Priority**: P3 | **Effort**: Large | **Risk**: Medium

GitHub Actions workflow. The tiered test system was designed for CI but not yet wired up.

| Item | Description |
|------|-------------|
| **GitHub Actions CI** | macOS runner. Tier 1 on every PR, Tier 2 before merge, Tier 3 nightly. Real-world library validation on merge. |

**Key files**: New `.github/workflows/`, existing test scripts
**Verification**: PR opens, CI runs, tests pass

---

### 3. Performance Benchmarks

**Priority**: P3 | **Effort**: Medium | **Risk**: Low

New standalone project. No overlap with generator or runtime code. Measures interop overhead.

| Item | Description |
|------|-------------|
| **Interop performance benchmarks** | 5 CI perf smoke scenarios (<60s, ratio-based thresholds). Standalone BenchmarkDotNet harness for deep investigation. Baseline variance. |

**Key files**: New `perf/` directory, Swift test functions in TestFramework
**Verification**: Benchmark suite runs, produces ratio metrics
**Design**: `Future/interop-performance-validation-plan.md`

---

### 4. SwiftUI Bridge Corpus

**Priority**: P3 | **Effort**: Medium | **Risk**: Low

Extends the SwiftUI bridge with coverage tracking across a real library corpus. Standalone — builds on existing bridge infrastructure.

| Item | Description |
|------|-------------|
| **Bridge corpus + 3-tier metrics** | Track bridge coverage (generated / typechecked / runtime-validated) across 10+ libraries. Reproducible corpus with pinned versions + hashes. Regression detection. |

**Key files**: `BindingReport.cs`, `ReportCollector.cs`, new `bridge-corpus/` directory
**Verification**: `generate-bridge-coverage.sh` produces per-library and aggregate metrics
**Design**: `Future/swiftui-bridge-v2-plan.md` (Phase 4)

---

### 5. Actor-Aware Wrapper Emission

**Priority**: P2 | **Effort**: Medium | **Risk**: Low

Swift 6 enforces actor isolation as hard type-system errors. Generated `@_silgen_name` wrapper functions access @MainActor and actor-isolated properties from nonisolated context, which is rejected regardless of `-strict-concurrency=minimal`, `-swift-version 5`, or `@preconcurrency import`. The `-strict-concurrency=minimal` flag only affects Sendable checking, not actor isolation.

The fix is to parse `@MainActor` annotations from `.swiftinterface` files (which have explicit `@_Concurrency.MainActor` annotations) and emit matching actor isolation on generated wrapper functions. For custom actors, the wrapper functions need the property access wrapped in the actor's execution context.

| Item | Description |
|------|-------------|
| **Parse @MainActor from swiftinterface** | `SwiftInterfaceAccessParser` already extracts other annotations. Add extraction of `@MainActor` / `@_Concurrency.MainActor` per-member and per-type. |
| **Emit actor isolation on wrapper functions** | When a protocol/class is `@MainActor`, emit `@MainActor` on generated witness dispatch and async stream wrapper functions. |
| **Handle custom actors** | Types like `BlinkIDEventStream` (custom actor) need their wrapper functions to use `await` or the actor's execution context. |
| **Remove -strict-concurrency=minimal** | Once actor-aware emission covers known cases, remove the temporary flag from `SwiftWrapperCompiler.cs`. |

**Key files**: `SwiftInterfaceAccessParser.cs`, `EveryProtocolEmitter.cs`, `WitnessDispatchEmitter.cs`, `PInvokeHelperEmitter.cs`, `SwiftWrapperCompiler.cs`
**Verification**: BlinkIDUX wrapper compiles with 0 errors
**Affected**: BlinkIDUX (6 actor isolation errors: 4 on CameraModel @MainActor protocol, 1 on BlinkIDEventStream custom actor, 1 on Camera @MainActor class)

---

## Multi-Session Efforts (P4)

Too large for single sessions. Each needs a **planning session first** to scope implementation.

### Class Inheritance Hierarchy

**Effort**: Very Large (5+ sessions)
**Prerequisite**: ObjC binding integration (informs NSObject bridging)

Emit C# class hierarchies mirroring Swift type graph. Requires: cross-module inheritance chain resolution from ABI JSON `inheritsFrom`, diamond inheritance handling with protocols, `base()` constructor chains, bridging into `Foundation.NSObject`.

### ObjC Binding Integration

**Effort**: Large (3-5 sessions)
**Design**: `Future/objc-binding-integration.md`

Replace Objective Sharpie. Uses `clang -ast-dump=json`. Same CLI/SDK for Swift and ObjC. ~1,500-2,000 lines new code. Session breakdown:
1. Detection + routing
2. Clang AST parser
3. ApiDefinition emitter
4. Binding project emission + SDK integration
5. Validation against known ObjC frameworks

### Emitter Architecture Redesign

**Effort**: Very Large (5+ sessions)
**Design**: `Future/emitter-redesign-proposal.md`

Three-phase architecture: type pre-processing (graph traversal + label assignment), type processing (handler-based member population), emission from representations. High risk — touches everything.

### Multi-Platform Support

**Effort**: Large (3+ sessions)
**Design**: `Future/dx-multi-framework-auto-detection.md` (Platform Coverage)

Extend beyond iOS to Mac Catalyst, macOS, tvOS.

---

## Blocked on Upstream (.NET Runtime)

Workarounds are in place. Draft bug reports ready in `Future/upstream-bug-reports-draft.md`.

| Issue | Root Cause | Current Mitigation | Unblocked When |
|-------|-----------|-------------------|----------------|
| **SafeHandle finalizer crashes on Mono** | `VWT->Destroy()` via indirect CallConvSwift → JIT assertion | MutableProps tests at Tier 3; consumers must call `Dispose()` | Mono JIT CallConvSwift fix |
| **Non-blittable types with CallConvSwift** | .NET requires all CallConvSwift P/Invoke params be blittable | Wrapper methods for known patterns; `MonoJitRiskDetector` flags | dotnet/runtime adds managed type marshalling for CallConvSwift |
| **Async runtime tests (32 tests, all Tier 3)** | Mono JIT assertion on CallConvSwift in async P/Invoke | Tests written and ready; tagged Tier 3 | Same as above |
| **Non-primitive closure Cdecl** | Strategy B only covers primitive-arg closures | Non-primitive closures fall back to CallConvSwift | Mono JIT fix OR Swift-side marshal adapters |
| **SafeHandle in async P/Invoke** | .NET runtime doesn't preserve SafeHandle through async continuation | Singleton pattern detection + IntPtr conversion | dotnet/runtime adds SwiftSelf register support with async Task capture |
| **VWT InitializeWithCopy** | Indirect CallConvSwift function pointer in `MarshalToSwift` | No known test failures yet | Same as VWT Destroy |

**Tracking issues**: [#93631](https://github.com/dotnet/runtime/issues/93631) (.NET 9), [#108662](https://github.com/dotnet/runtime/issues/108662) (.NET 10), [#64215](https://github.com/dotnet/runtime/issues/64215) (CallConvSwift), [#80905](https://github.com/dotnet/runtime/issues/80905) (NativeAOT iOS).

---

## Known Generator Bugs (Tracked, not prioritized)

Workarounds exist for all. Not blocking any library validation.

| Bug | Impact | Workaround |
|-----|--------|------------|
| String enum raw values use case names | ABI JSON lacks individual case raw values | Case names used; cosmetic only |
| `UnsafePointer<T>` → AnyType | No concrete projection for immutable pointers | Use `UnsafeMutablePointer<T>` |
| Throwing closure thunks | `SwiftString` return emitted as `void*` | Exclude throwing closures |
| `async throws(ErrorType)` free functions | Emit `_payload`/`this` in static context | Guarded — no runtime impact |
| ExistentialContainer0 in tuple element | Lottie edge case | Not reached by current guards |
| `Optional<T>` P/Invoke truncation for T.Size > 8 | `PayloadBuffer<IntPtr>` passes 8 bytes; `Optional<String>` is 16 bytes | **Partially fixed** — `_optbuf` covers standalone methods, frozen struct constructors, property setters, and mutating methods for String/Int/UInt/Int64/UInt64/Double. Still truncated for: async, wrapper-owned, and Optional return values. |
