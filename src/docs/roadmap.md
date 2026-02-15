# Roadmap

**Created**: February 2026
**Status**: Active — production readiness focused
**Source**: Consolidated from binding research analysis, existing roadmap, Future/ design docs, and testing gaps.

For completed work, see `Completed/` (notably `roadmap-completed-feb2026.md`, `phases-a-through-g.md`, `phases-h-through-wu.md`, and `developer-experience.md`).

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 2,593 passing |
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

### Session 1: Consumer Safety Attributes — **Done** (2026-02-14, 2519 unit tests)

**Priority**: P0/P2 | **Type**: Implementation | **Risk**: Low

All three items are about surfacing hidden information to consumers at compile time — turning silent runtime crashes into visible warnings. They share code paths (`PInvokeEmitter`, TBD symbol parsing, attribute emission) and need the same context (how methods are flagged, how symbols are resolved).

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **Compile-time warnings for unbindable methods** | P0 | Small | Done — `[Obsolete("...", true)]` on unmitigated JIT-risky methods. 7 unit tests. |
| **P/Invoke symbol cross-referencing** | P2 | Medium | Done — `ComputeEntryPoint()` extracted, `CheckExportedSymbol()` cross-refs TBD. 7 unit tests. |
| **`[OriginalSwiftType]` attribute** | P2 | Small | Done — New runtime attribute + param/return emission for AnyType fallbacks. 8 unit tests. |

**Key changes**: `PInvokeEmitter.ComputeEntryPoint()` (extracted), `MethodHandler.CheckExportedSymbol()`, `WrapperEmitter.EmitSafetyObsolete()` + `BuildOriginalSwiftTypeAttributes()` + `EmitReturnTypeOriginalSwiftType()`, `MethodSignature.ParametersString()` overload, `OriginalSwiftTypeAttribute.cs` (new), `UnsupportedSwiftTypeSupport.EscapeStringLiteral()` (now internal). Property accessors deferred (see plan).
**Note**: `[OriginalSwiftType]` requires `Swift.Runtime` NuGet re-publish to compile in consumer projects.

---

### Session 2: SwiftOptional Extra Inhabitants Fix — **Done** (2026-02-14, 2527 unit tests)

**Priority**: P0 | **Type**: Bug fix | **Risk**: Low

Root cause: `SwiftOptional<T>.NewSome()` assumed all Optional types have a discriminator byte (`metadata.Size - 1`). For extra-inhabitant types (String, Array, classes) where `Optional<T>.Size == T.Size`, this created an undersized span — crashing with "Span size does not match type size." This is the same crash as Stripe's `StripeAPI.DefaultPublishableKey = "pk_test_xxx"`.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **SwiftOptional.NewSome() span fix** | P0 | Small | Done — use inner type's metadata size instead of `metadata.Size - 1`. 7 unit tests for `ComputePayloadSpanSize`. |
| **DllImportResolver conflict fix** | P0 | Small | Done — wrapped RuntimeTestsApp resolver in try-catch. Generated `[ModuleInitializer]` and app's `Main()` both call `SetDllImportResolver`. |
| **[Obsolete] test build compatibility** | P0 | Small | Done — `run-runtime-tests.sh` Step 1.7 sed-downgrades `[Obsolete("...", true)]` to warning for test builds. Consumer bindings retain `error: true`. |
| **Runtime tests** | P0 | Small | Done — 5 new Tier 3 Optional<String> tests (Mono JIT + P/Invoke truncation block Tier 2). |
| **StripePayments runtime verification** | P0 | Small | Deferred — requires external Stripe test app. |

**Key changes**: `SwiftOptional.cs` (`NewSome()` + `ComputePayloadSpanSize()`), `SwiftOptionalSpanSizeTests.cs` (7 tests), `run-runtime-tests.sh` (Step 1.7), `Program.cs` (DllImportResolver try-catch), `SdkPropsTargetsTests.cs` (removed brittle version-string assertions).
**Discovered**: Pre-existing `Optional<String>` P/Invoke truncation bug — `PayloadBuffer<IntPtr>` only captures 8 of 16 bytes. Tracked in Known Generator Bugs below.

---

### Session 3: SDK & NuGet DX — **Done** (2026-02-14, 2542 unit tests)

**Priority**: P1 | **Type**: Implementation | **Risk**: Low

Both items are MSBuild SDK and NuGet packaging improvements. They share `Sdk.targets` and the `ConsumerTargetsEmitter`. Both affect the `dotnet build` -> `dotnet pack` -> consumer experience chain.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **Two-pass build fix (SWIFTBIND050)** | P1 | Small | Done — `EffectiveOutcome()` downgrades Fatal→Warning in SDK mode. `HandleWrapperCompilationOutcome()` extracted. 10 unit tests. |
| **NativeReference Exists() guard** | P1 | Small | Done — Source xcframework NativeReference gets `Exists()` condition matching wrapper pattern. 5 unit tests. |
| **Sdk.targets case-insensitive doc comments** | P1 | Small | Done — `SwiftGenerateDocComments` comparison uses `System.String.Equals(..., OrdinalIgnoreCase)`. |

**Key changes**: `SwiftWrapperCompiler.EffectiveOutcome()`, `BindingsGenerator.HandleWrapperCompilationOutcome()`, `Program.cs` (wired outcome handling), `ConsumerTargetsEmitter.cs` (Exists guard), `Sdk.targets` (case-insensitive), `dx-msbuild-sdk-design.md` (SWIFTBIND050 error code).

---

### Session 4: Typed Swift Exceptions — **Done** (2026-02-14, 2593 unit tests)

**Priority**: P1 | **Type**: Implementation | **Risk**: Medium

Standalone feature touching the async error pipeline. Requires a new type in `Swift.Runtime` and changes to how error callbacks marshal exception information. Self-contained — doesn't share code paths with other sessions.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **`SwiftException<TError>` runtime type** | P1 | Small | Done — generic exception class with nullable `Error` property. |
| **Typed throws detection** | P1 | Medium | Done — `GetTypedThrowsErrors()` parses `.swiftinterface` for `throws(ErrorType)`, threaded to `MethodDecl.ThrownErrorType` via `SwiftABIParser`. |
| **Async typed error callbacks** | P1 | Medium | Done — 4-param error callback (errorPtr + size + msg + task) with `MarshalFromSwift<TError>` + `SBW_Free`. `BuildErrorCallbackBlock()` helper deduplicates 5 emission sites. |
| **Sync typed exceptions** | P1 | Small | Done — `SwiftException<TError>(message)` with `Error = null` (existential extraction deferred). |
| **Free-function async guard** | P1 | Small | Done — D5 guard: `HasTypedThrows && IsAsync && parentTypeName == null` falls back to untyped (avoids known `_payload`/`this` bug). |

**Scope**: Async method wrappers (full error value transport) + sync method exception typing (message-only). Throwing closures (`ClosureEmitter.Throwing.cs`) explicitly out of scope — closures use `SwiftResult<TSuccess, SwiftError>`.
**Key changes**: `SwiftException.cs` (generic subclass), `SwiftInterfaceAccessParser.cs` (`GetTypedThrowsErrors()`), `MethodDecl.cs` (`ThrownErrorType`, `HasTypedThrows`), `SwiftABIParser.cs` (typed throws dictionary), `Program.cs` (wiring), `WrapperEmitter.cs` (sync `SwiftException<T>`), `WrapperEmitter.Async.cs` (`BuildErrorCallbackBlock()`, typed catch blocks, `SBW_Free` P/Invoke in error callback), `TypedThrows.swift` (async instance method).
**Tests**: 10 parser unit tests (`SwiftInterfaceTypedThrowsTests`), 10 emitter unit tests (`TypedThrowsEmitterTests`), 1 new Tier 1 runtime test (`TestValidateRangeTypedCatchNullError`), 2 Tier 3 async runtime tests, 5 existing runtime tests updated from `SwiftRuntimeException` to `SwiftException<T>`.
**Note**: `SwiftException<TError>` requires `Swift.Runtime` NuGet re-publish to compile in consumer projects.

---

### Session 5: SwiftArray Collection Interface — **Done** (2026-02-14, 2593 unit tests + 156 runtime library tests)

**Priority**: P2 | **Type**: Implementation | **Risk**: Low

Standalone runtime library change. `SwiftArray<T>` previously copied to `List<T>` via LINQ `.Select().ToList()` on every string array access. Now uses lazy `AsProjected()` — zero-copy indexed access.

| Item | Priority | Effort | Status |
|------|----------|--------|--------|
| **Constructors from T[] and IEnumerable\<T\>** | P2 | Small | Done — `new SwiftArray<T>(source)` + implicit operator from `T[]`. |
| **Indexer bounds checking** | P2 | Small | Done — `ArgumentOutOfRangeException` on OOB (Swift would crash). |
| **Lazy projection wrapper** | P2 | Medium | Done — `SwiftArrayProjection<TSource, TResult>` (internal, IReadOnlyList). Live view, no copying. |
| **Emitter integration** | P2 | Small | Done — `.Select(e => e.ToString()).ToList()` → `.AsProjected(e => e.ToString())` in `GetReturnConversion`. |

**Key changes**: `SwiftArray.cs` (constructors, implicit op, bounds check, `AsProjected<T>()`), `SwiftArrayProjection.cs` (new), `TypeConversionHandler.cs` (return conversion).
**Tests**: 15 new SwiftArray tests (constructors, conversions, bounds, IList, AsProjected) + 10 SwiftArrayProjection tests (lazy access, live view, enumeration, bounds, SwiftString) + 2 updated emitter tests.
**Note**: `SwiftArrayProjection` requires `Swift.Runtime` NuGet re-publish to compile in consumer projects.

---

### Session 6: Async Method Improvements ✅

**Priority**: P2/P3 | **Type**: Implementation | **Risk**: Medium | **Status**: Done

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **CancellationToken on async methods** | P2 | Medium | Done — `CancellationToken cancellationToken = default` on all `Task`-returning methods. Swift Task store (`_SBWTaskEntry` + `_sbwActiveTasks` dictionary + `NSLock`), `@_cdecl("SBW_CancelTask_{Module}")` cancel function, C# registration → Swift cancel + `TrySetCanceled`, pre-cancel check with `Task.FromCanceled`. `isCancellation: Int32` error callback parameter (type-safe, not string matching). |
| **Callback-to-Task overloads** | P3 | Medium | Done — `CompletionHandlerDetector` identifies completion handler closures (trailing, void-returning, recognized shapes: VoidResult, SingleResult, ErrorOnly, ResultWithError). Generates `Task<T>`-returning overloads with `TaskCompletionSource` + `RunContinuationsAsynchronously`. Bound generic type resolution with protocol guard. |

**Key files**: `CancellationTaskEmitter.cs` (new), `CompletionHandlerDetector.cs` (new), `WrapperEmitter.Async.cs`, `WrapperEmitter.cs`, `ModuleHandler.cs`, `MethodHandler.cs`
**Tests**: 52 new (30 CancellationTokenEmitterTests + 22 CompletionHandlerDetectorTests)

---

### Session 7: Emitter Quality Fixes

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

Two small-medium emitter improvements that don't touch risky code paths. Both are about producing cleaner C# output. Can be done together since they're quick and independent within the emitter.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **Property collision logic** | P3 | Small | `Value` suffix on property names when nested type exists may be unnecessary — C# compiler can disambiguate. Verify across all contexts (generic type args, `typeof()`, `nameof()`). |
| **Default parameter overloads** | P3 | Medium | Expand `DefaultParameterOverloadEmitter.cs` scope beyond wrapper-backed methods to cover all methods with default values. |

**Key files**: `PropertyHandler.cs`, `DefaultParameterOverloadEmitter.cs`
**Verification**: `./run-tests.sh` + spot-check generated output for Nuke/Lottie
**Design**: `Future/binding-api-future-work.md` (N6, Default Parameters)

---

### Session 8: ExistentialContainer Cleanup

**Priority**: P2 | **Type**: Implementation | **Risk**: High

Hard difficulty — deep in the marshaler's existential handling. `ExistentialContainer` still appears in closure parameters and protocol proxy constructors (26 skipped members across Nuke/Lottie). Requires its own session due to complexity and the amount of marshaler context needed.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **ExistentialContainer in public API** | P2 | Hard | Map existential containers to protocol interfaces in closure and constructor contexts. Gated on `AllProtocolsHaveTypeRecords()`. |

**Key files**: Marshaler existential handling, `ClosureEmitter.cs`, `ProtocolProxyEmitter.cs`
**Verification**: `./run-tests.sh` + regenerate Nuke/Lottie, verify reduction in `UnsupportedExistential` skips
**Design**: `Future/binding-api-future-work.md` (R6), `Future/unsupported-existential-analysis.md`

---

### Session 9: Roslyn Analyzer

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

Entirely new project — a Roslyn analyzer that warns when `ISwiftObject` types aren't disposed. No overlap with any generator code. Requires Roslyn analyzer development experience but is self-contained.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **Analyzer for undisposed Swift objects** | P3 | Medium | Warn on `ISwiftObject` / `SwiftSafeHandle<T>` locals without `using`, fields without dispose in containing type. Package in `Swift.Runtime` NuGet. |

**Key files**: New analyzer project, `Swift.Runtime` NuGet packaging
**Verification**: Analyzer unit tests + manual validation in a test consumer project
**Design**: `Future/roslyn-analyzer-plan.md`

---

### Session 10: Golden API Snapshots

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

New tooling to detect API surface drift. Standalone scripting — no generator changes. Should be done before CI integration (Session 11) since the snapshot tool feeds into the CI pipeline.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **API snapshot tooling** | P3 | Medium | Script extracts public API surface from generated `.cs` files. Baseline snapshot checked into repo. Clear diff output for added/removed/changed members. |

**Key files**: New scripts in `TestFramework/`
**Verification**: Run against TestFramework generated bindings, verify baseline matches current output
**Spec**: `testing-gaps.md` Gap 9

---

### Session 11: CI Integration

**Priority**: P3 | **Type**: Implementation | **Risk**: Medium
**Depends on**: Session 10 (API snapshots)

GitHub Actions workflow. Large but well-scoped — the tiered test system was designed for CI but not yet wired up.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **GitHub Actions CI** | P3 | Large | macOS runner. Tier 1 on every PR, Tier 2 before merge, Tier 3 nightly. Real-world library validation on merge. API snapshot comparison. |

**Key files**: New `.github/workflows/`, existing test scripts
**Verification**: PR opens, CI runs, tests pass
**Spec**: `testing-gaps.md` Gap 10

---

### Session 12: Performance Benchmarks

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

New standalone project. No overlap with generator or runtime code. Measures interop overhead — valuable for confidence but doesn't change behavior.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **Interop performance benchmarks** | P3 | Medium | 5 CI perf smoke scenarios (<60s, ratio-based thresholds). Standalone BenchmarkDotNet harness for deep investigation. Baseline variance. |

**Key files**: New `perf/` directory, Swift test functions in TestFramework
**Verification**: Benchmark suite runs, produces ratio metrics
**Design**: `Future/interop-performance-validation-plan.md`

---

### Session 13: Multi-Framework Auto-Detection

**Priority**: P2 | **Type**: Implementation | **Risk**: Medium

Builds on existing `--framework-dependency` / `<SwiftFrameworkDependency>` support. Adds automatic detection so users don't need to specify dependencies manually.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **Auto-detection via binary linkage** | P2 | Medium | `otool -L` analysis for automatic dependency detection. `dependency-manifest.json` generation. Topological sort for multi-package build ordering. |

**Key files**: `Program.cs` (dependency resolution), new `DependencyAnalyzer.cs`
**Verification**: `./run-tests.sh` + Stripe multi-module build with auto-detected deps
**Design**: `Future/dx-multi-framework-auto-detection.md`

---

### Session 14: SwiftUI Bridge Corpus (Phase 4)

**Priority**: P3 | **Type**: Implementation | **Risk**: Low

Extends the SwiftUI bridge with coverage tracking across a real library corpus. Standalone — builds on existing bridge infrastructure.

| Item | Priority | Effort | Description |
|------|----------|--------|-------------|
| **Bridge corpus + 3-tier metrics** | P3 | Medium | Track bridge coverage (generated / typechecked / runtime-validated) across 10+ libraries. Reproducible corpus with pinned versions + hashes. Regression detection. |

**Key files**: `BindingReport.cs`, `ReportCollector.cs`, new `bridge-corpus/` directory
**Verification**: `generate-bridge-coverage.sh` produces per-library and aggregate metrics
**Design**: `Future/swiftui-bridge-v2-plan.md` (Phase 4)

---

### Session 15+: Library Validation Expansion

**Priority**: P2 | **Type**: Implementation | **Risk**: Low
**Multiple sessions** — one per library set

Runtime test apps for additional libraries beyond the current 5 (Nuke, BlinkID, Lottie, CryptoSwift, BridgeTest). Each library is a self-contained session.

| Session | Libraries | Notes |
|---------|-----------|-------|
| 15a | Alamofire | Popular networking, good API coverage test |
| 15b | Stripe end-to-end | Multi-module with `--framework-dependency` chain |
| 15c | Additional 2-3 | Based on ecosystem demand |

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

### P4-4: NativeAOT Migration

**Effort**: Large (3+ sessions estimated)
**Design**: `Future/nativeaot-investigation.md`

Bypasses Mono JIT crash entirely. Suggested session breakdown:
1. Minimal NativeAOT iOS test app reproducing three known failures
2. `[LibraryImport]` + `CustomMarshaller` experiments for `SwiftOptional<T>`
3. SafeHandle async validation + production migration path

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
| `Optional<T>` P/Invoke truncation for T.Size > 8 | `PayloadBuffer<IntPtr>` passes 8 bytes; `Optional<String>` is 16 bytes | Runtime tests at Tier 3; value types and class refs (<=8 bytes) work. **Fixable** — route through Swift `@_cdecl` wrapper that accepts `UnsafeRawPointer` to optional buffer (same pattern as async methods). Generator-only change, no upstream fix needed. |

---

## Session Summary

| Session | Priority | Type | Effort | Theme |
|---------|----------|------|--------|-------|
| **1. Consumer Safety Attributes** | P0/P2 | Implement | Small-Medium | `[Obsolete]` on crashy methods, symbol cross-ref, `[OriginalSwiftType]` |
| **2. SwiftOptional Fix** | P0 | Bug fix | Small | `NewSome()` extra inhabitants + DllImportResolver + [Obsolete] compat |
| **3. SDK & NuGet DX** | P1 | Implement | Small | Done — SWIFTBIND050 two-pass build + NativeRef Exists() guard + case-insensitive doc comments |
| **4. Typed Swift Exceptions** | P1 | Implement | Medium | `SwiftException<TError>` with error details |
| **5. SwiftArray Collection** | P2 | Implement | Medium | `IReadOnlyList<T>` on SwiftArray, no LINQ copying |
| **6. Async Improvements** | P2/P3 | Done | Medium | CancellationToken on async + callback-to-Task overloads |
| **7. Emitter Quality** | P3 | Implement | Small-Medium | Property collision + default param overloads |
| **8. Existential Cleanup** | P2 | Implement | Hard | ExistentialContainer -> protocol interfaces |
| **9. Roslyn Analyzer** | P3 | Implement | Medium | Undisposed ISwiftObject warnings |
| **10. API Snapshots** | P3 | Implement | Medium | API surface drift detection |
| **11. CI Integration** | P3 | Implement | Large | GitHub Actions tiered pipeline |
| **12. Perf Benchmarks** | P3 | Implement | Medium | Interop overhead measurement |
| **13. Auto-Detection** | P2 | Implement | Medium | `otool -L` dependency discovery |
| **14. SwiftUI Corpus** | P3 | Implement | Medium | Bridge coverage tracking |
| **15a-c. Library Validation** | P2 | Implement | Medium each | Runtime test apps for additional libraries |
| **P4-1 through P4-5** | P4 | Multi-session | Large-Very Large | Architecture: inheritance, ObjC, emitter redesign, NativeAOT, multi-platform |
