# Roadmap

**Updated**: February 2026
**Status**: Active
**Target**: Raise binding quality from 6.5/10 to 8.5+/10 for .NET developer experience

For completed work, see `Completed/`.
External review: `/Users/wojo/Dev/swift-dotnet-packages/binding-analysis-v2.md` (not in this repo).

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 3,356 passing |
| Integration tests | 700 passing (11 skipped, pre-existing) |
| Runtime library tests | 181 passing |
| Runtime tests | 188 passing at Tier 2 (28 pre-existing failures, allowlist-based crash tolerance) |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 25 clean (0 generator errors) + 5 environmental-only |
| External review score | 6.5/10 (up from 5.5) |

---

## Priority Key

- **P0**: Blocks adoption — APIs are unusable or methods are silently missing
- **P1**: Major DX friction — consumers hit these within first hour of use
- **P2**: Quality gaps — noticeable but workable, erodes confidence over time
- **P3**: Polish — professional quality, long-term sustainability
- **P4**: Future vision — architectural improvements, new capabilities

---

## Acceptance KPIs

Hard, measurable gates per priority tier. Grep/compile checks, not subjective scores.

| KPI | Current | After P0 | After P1 |
|-----|---------|----------|----------|
| `ExistentialContainer` in public signatures | 0 (was ~60+) | 0 | 0 |
| Stripe 11-module cross-compile (non-AnyType members) | 100% (3-module validated, SDK integration complete) | 95%+ | 98%+ |
| `SwiftDictionary` in public signatures | 144 | 144 | 0 |
| `SwiftOptional<` in public signatures | ~20 | ~20 | 0 |
| Empty protocol interfaces (0 members) | 11 | <5 | 0 |
| Enums emitted as native C# `enum` | ~5% | ~5% | ~40% |
| Sync throw messages with actual error text | 0% | 0% | 100% |
| Runtime type leakage (SwiftArray/SwiftOptional/ExistentialContainer in public API) | ~170 (was ~230; EC eliminated) | ~170 | <10 |

---

## P0 — Adoption Blockers

### 1. Cross-Module Type Resolution

**Priority**: P0 | **Effort**: Large (3-4 sessions) | **Risk**: Medium

When generating bindings for StripePaymentSheet, types from StripePayments (e.g., `STPPaymentMethod`) resolve to `AnyType` and members are skipped. This blocks all real-world Stripe payment flows that span modules. The external review called this "the single biggest blocker for real-world adoption."

**Architecture today**: Generator processes ONE module at a time. TypeDatabase only contains the current module + built-in databases (Foundation, UIKit, Swift, CoreGraphics, Dispatch). Cross-module types → `GetTypeRecordOrAnyType()` → `AnyType` → member skipped.

**Architecture needed**: Load dependent module type databases before emitting the current module.

| Step | Description | Effort | Status |
|------|-------------|--------|--------|
| **1a. Generate module database XML** | After processing a module, emit a `{Module}Database.xml` alongside the binding. Contains all TypeRecord entries needed by dependents (type name, C# name, kind, flags, metadata accessor). | Medium | **COMPLETE** |
| **1b. Accept `--module-database` CLI option** | Repeatable option that loads dependent module XML files into TypeDatabase before emission. Validation: SWIFTBIND070 (missing file), SWIFTBIND071 (self-reference), SWIFTBIND072 (invalid XML). | Low | **COMPLETE** |
| **1c. SDK integration** | `_CollectSwiftModuleDatabases` target gathers databases from NuGet packages (`SwiftModuleDatabase` items from consumer `.targets`) and local `ModuleDatabasePath` metadata. Deduplicates, warns on missing paths (SWIFTBIND073), passes `--module-database` args to generator. `ConsumerTargetsEmitter` emits `SwiftModuleDatabase` item in consumer `.targets`. Pack layout bundles `{Module}Database.xml` in `buildTransitive/net10.0-ios/`. Fingerprint includes database content hashes. Build order NOT enforced by SDK (inherent MSBuild limitation — NuGet packages are pre-built; local builds use explicit `ProjectReference`). | Medium | **COMPLETE** |
| **1d. Expand cross-module protocol conformance** | Remove the `CrossModuleSupportedProtocols` whitelist (currently only `Equatable`/`Hashable`). With full type databases loaded, cross-module conformances are emitted for empty/marker interfaces (`EmittedMemberCount == 0`). Non-empty interfaces are gated to prevent CS0535. `ProtocolHandler.FixupProtocolInheritedRequirements` post-pass propagates inherited requirements (transitive, nested) to a fixed point. | Low | **COMPLETE** |

**Key insight**: We don't need to re-parse dependent ABI JSON. We just need the TypeRecord metadata (C# type name, mangled name, kind, flags). A small XML file per module (~10KB for StripePayments) is sufficient.

**Steps 1a+1b validated**: StripePaymentSheet with both StripeCore + StripePayments dependency databases resolves 46 StripeCore types + 87 StripePayments types (0 AnyType). `STPPaymentMethod` appears as proper type. New files: `ModuleDatabaseEmitter.cs`, `ProgramModuleDatabaseTests.cs`, `ModuleDatabaseEmitterTests.cs`. Extended: `TypeDatabase.cs` (protocol/existential kind, new flags), `ModuleDatabase.cs` (GetAllTypeRecords), `Program.cs` (CLI option + loading).

**Step 1c validated**: SDK integration adds `_CollectSwiftModuleDatabases` target to `Sdk.targets`, `SwiftModuleDatabase` item emission in `ConsumerTargetsEmitter.cs`, pack layout bundling, and fingerprint hashing. 4 behavioral MSBuild execution tests (stub generator approach) + 5 emitter content tests + 8 Sdk.targets content tests. NuGet flow: consumer `.targets` registers `SwiftModuleDatabase` → `_CollectSwiftModuleDatabases` collects → `_GenerateSwiftBindings` passes `--module-database` → cross-module types resolve.

**Acceptance gate**: Generate all 11 Stripe modules with dependency chain. `STPPaymentMethod` appears as proper type in StripePaymentSheet bindings (not `AnyType`). AnyType fallback count in Stripe drops >80%.

---

### 2. Eliminate ExistentialContainer from Public API — COMPLETE

**Priority**: P0 | **Effort**: Medium (2 sessions) | **Risk**: Low-Medium | **Status**: Complete

`ExistentialContainer{N}` no longer appears in any public closure/delegate signature, tuple element type, or generic type argument across all 25 validated libraries. Known protocols project to their interface type (e.g., `IImageProcessing`), well-known protocols to their runtime type (e.g., `AnyError`), and unknown protocols to `object`. The P/Invoke layer still uses `ExistentialContainer` internally — only the public C# API changed.

| Step | Description | Status |
|------|-------------|--------|
| **2a. Project unknown protocols to `object`** | Unknown existentials in closure params/returns → `object`. P/Invoke stays `ExistentialContainer`. Callback: `(object)arg` boxing. Invoker: `(ExistentialContainer1)_arg` unboxing. Added `IsExistentialParam()` and `GetPInvokeExistentialType()` helpers. | **COMPLETE** |
| **2b. Project closure return existentials** | Removed `isReturnType` guard in `TranslateTypeSpecToCSharp`. Known-protocol returns → interface with `ISwiftExistentialConvertible` extraction in callbacks and proxy wrapping in invokers. Unknown returns → `object` with boxing/unboxing. Applied to all emitter paths: `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`. | **COMPLETE** |
| **2c. Project SwiftResult error type** | Already handled — `SwiftResult<T, SwiftError>` uses `SwiftError` (well-known protocol mapping), not `ExistentialContainer1`. No work needed. | **COMPLETE** (pre-existing) |
| **2d. Array/collection of existentials** | `TranslateBoundGenericToCSharp` in both `ClosureHandler` and `TupleHandler` now uses `GetPublicExistentialType` (interface/object) instead of `GetCSharpExistentialType` (container). `TupleHandler.TranslateElementTypeToCSharp` also updated. `HasClosureUnsafeTupleElements` expanded to detect existential type mismatches (blocks closures with tuple-existential elements in both params and returns). | **COMPLETE** |

**Acceptance gate**: Passed. `grep 'Func<.*ExistentialContainer\|Action<.*ExistentialContainer'` across all 25 library outputs returns 0 matches. All libraries compile with 0 errors. `SwiftOptional<ExistentialContainer>` also resolved to 0 occurrences (deferred item had lower impact than estimated).

**Residual risk — runtime cast for unknown protocols**: When a closure parameter or return uses `object` (unknown protocol path), the callback boxes `ExistentialContainer` → `object` and the invoker unboxes `object` → `ExistentialContainer`. This round-trip works correctly when the object originated from Swift (the box contains the original container). However, if a .NET consumer provides an arbitrary `object` that is *not* a boxed `ExistentialContainer`, the unbox cast will throw `InvalidCastException` at runtime. This is expected fail-fast behavior — `object` is strictly better UX than `ExistentialContainer` (discoverable, documentable), and the cast failure is immediate and descriptive. A future improvement could add a runtime adapter that constructs an `ExistentialContainer` from user-provided objects, but this requires protocol witness table synthesis which is out of scope.

**Key files changed**: `ClosureHandler.cs`, `TupleHandler.cs`, `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`, `ClosureExistentialTests.cs`.

---

## P1 — Major DX Friction

### 3. Native C# Enums for Simple Swift Enums

**Priority**: P1 | **Effort**: Medium (2-3 sessions) | **Risk**: Low

Swift enums are modeled as heap-allocated classes with `CaseTag`, `Dispose()`, and native memory allocation per access. This is jarring for .NET developers — enums should be value types with zero allocation.

The generator already emits native `enum` for frozen, non-generic enums with integral raw values (`IsSimpleEnum` path in `EnumHandler.SimpleEnum.cs`). The gap is in expanding coverage.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **3a. String-raw-value enums with methods** | Gate at `EnumDecl.IsStringRawValueSimpleEnum` rejects enums with methods. Relax to emit C# enum + extension methods (infrastructure already exists in `SimpleEnum.cs`). | Low (~2h) | `EnumDecl.cs:105-112`, `EnumHandler.SimpleEnum.cs` |
| **3b. Non-frozen simple enums** | Emit C# enum for non-frozen enums that have no associated values, are non-generic, and have integral/no raw value. Emit a `_Unknown` sentinel case for forward compatibility. | Medium | `EnumDecl.cs:75-79`, `EnumHandler.SimpleEnum.cs` |
| **3c. Enum case caching for class-based enums** | For enums that must remain classes (associated values, generic), add singleton/flyweight caching for no-payload case accessors. `Country.Albania` should return the same object every access. | Medium | `EnumHandler.RawRepresentable.cs`, `EnumHandler.CaseConstruction.cs` |

**Acceptance gate**: Count of native `enum` declarations in generated output increases from ~5% to ~40% of total enum types. Class-based enum case accessors with no payload return cached instances (verify via object reference equality in tests).

---

### 4. Optional<T> P/Invoke Truncation (Correctness)

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Medium

`Optional<T>` for `T.Size > 8` silently truncates data through P/Invoke. The `_optbuf` wrapper fixes standalone methods, frozen struct constructors, property setters, and mutating methods. **Still broken for**: async methods, wrapper-owned methods, and Optional return values.

This is a data corruption bug, not a cosmetic issue. Elevated from "tracked bugs" because silent truncation is worse than a crash.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **4a. Audit remaining truncation paths** | Enumerate every P/Invoke signature emitting `PayloadBuffer<IntPtr>` for Optional types where inner size > 8. Categorize by fix strategy. | Low | `PInvokeEmitter.cs`, `WrapperEmitter.cs` |
| **4b. Extend _optbuf to wrapper-owned methods** | Apply the same buffer-widening pattern used in standalone methods. | Medium | `PInvokeEmitter.cs`, `WrapperEmitter.Marshalling.cs` |
| **4c. Fix Optional return values** | Optional returns via `SwiftIndirectResult` may also truncate. Verify and fix. | Medium | `WrapperEmitter.Return.cs` |

**Acceptance gate**: Zero `PayloadBuffer<IntPtr>` emissions where inner type size exceeds 8 bytes. Add unit tests for each path with `Optional<String>` (16 bytes) and `Optional<LargeStruct>`.

---

### 5. SwiftDictionary Projection

**Priority**: P1 | **Effort**: Medium (2 sessions) | **Risk**: Low

144 occurrences of `SwiftDictionary<SwiftString, SwiftString>` across 5 libraries. Arrays are properly projected (`IReadOnlyList<T>`) but dictionaries are not.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **5a. Runtime: Add IReadOnlyDictionary interface** | `SwiftDictionary<TKey, TValue>` implements `IReadOnlyDictionary<TKey, TValue>`. Add `Keys`, `Values`, `ContainsKey`, `TryGetValue`, `GetEnumerator`. Add `AsProjected()` and `FromDictionary()`. | Medium | `SwiftDictionary.cs` |
| **5b. Generator: Add dictionary type conversion** | Add `IsSwiftDictionary()` check to `TypeConversionHandler`. Returns use `IReadOnlyDictionary<K,V>`, parameters use `IDictionary<K,V>`. Element types converted independently (SwiftString→string). | Medium | `TypeConversionHandler.cs` |

**Pattern to follow**: Mirrors `SwiftArray` → `IReadOnlyList<T>`. `AsProjected(keySelector, valueSelector)` for lazy element-type conversion.

**Acceptance gate**: `grep -c 'SwiftDictionary' Swift.*.cs` on public signatures returns 0. `GetPropertyNamesToFormFieldNamesMapping()` returns `IReadOnlyDictionary<string, string>`.

---

### 6. Public API Projection Completeness

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Low

Unified item covering all remaining runtime type leakage in public signatures. Individual items (dictionary, optional) handle the biggest categories; this covers the long tail.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **6a. Consistent SwiftOptional projection** | Apply `GetIdiomaticCSharpType()` to async return types (`WrapperEmitter.Async.cs:133-175`) and tuple elements (`WrapperEmitter.Return.cs:593-595`). Currently only properties and regular methods project `SwiftOptional<T>` to `T?`. | Low | `WrapperEmitter.Async.cs`, `WrapperEmitter.Return.cs`, `MethodSignature.cs` |
| **6b. AsyncStream inner type projection** | `IAsyncEnumerable<SwiftArray<UIEvent>>` should be `IAsyncEnumerable<IReadOnlyList<UIEvent>>`. Apply array projection inside async stream generic parameter. | Low | `TypeConversionHandler.cs`, async emitter |
| **6c. Remaining AnyType cleanup** | After cross-module resolution (Task 1), audit remaining `AnyType` occurrences. For types still unresolvable, emit `[UnsupportedSwiftType]` with the original Swift type name instead of silently using `object`. | Medium | `TypeDatabaseExtensions.cs`, `MemberEmissionValidator.cs` |
| **6d. SwiftArray in non-projected contexts** | `SwiftArray<T>` appears unprojected in ~24 locations (async stream elements, enum factory params). Apply projection consistently. | Low | `TypeConversionHandler.cs` |
| **6e. Runtime existential marshalling for Optional projection** | Extend `SwiftMarshal.MarshalFromSwift<T>()` to support interfaces and `object` as `T`. When `T` is an interface, read the `ExistentialContainer` from the Swift payload and construct the corresponding proxy class. When `T` is `object`, box the container. This unblocks `SwiftOptional<IProtocol>` and `SwiftOptional<object>` — currently deferred because `.Some` throws `NotSupportedException` for non-concrete types. The reverse path (`MarshalToSwift` constructing `ExistentialContainer` from .NET-created objects) requires protocol witness table synthesis and remains deferred. Currently 0 occurrences across 25 libraries, but blocks full existential cleanup and will grow as libraries add Optional protocol APIs. | Medium | `SwiftMarshal.cs`, `SwiftOptional.cs` |

**Acceptance gate**: Combined grep for `SwiftOptional<|SwiftArray<|SwiftDictionary<|ExistentialContainer` in public signatures across all 25 libraries < 10 total occurrences (down from ~230). After 6e, `SwiftOptional<ExistentialContainer` drops to 0 (generator can emit `SwiftOptional<IProtocol>` once runtime supports it).

---

### 7. Empty Protocol Interface Completeness

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Low

11 protocol interfaces across StripeConnect (7 delegate protocols) and StripeIssuing (3 key provider protocols) are generated with zero members, making them unimplementable. Some empties may resolve after cross-module resolution (Task 1) — members could be skipped due to AnyType fallback on cross-module parameter types.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **7a. Post-cross-module audit** | After Task 1, regenerate Stripe modules and count remaining empty interfaces. Identify root cause per interface (AnyType fallback vs genuinely memberless protocol). | Low | Generated output analysis |
| **7b. Emit diagnostic on empty interfaces** | For protocols that genuinely have members in ABI JSON but all were skipped, emit `[Obsolete("...", DiagnosticId = "SB0004")]` with the skip reasons. For protocols with zero ABI members, consider suppression. | Low | `ProtocolHandler.cs`, `ProtocolProxyEmitter.cs` |
| **7c. Reduce member skip rate** | For members skipped due to non-blittable existential params or unsupported signatures, evaluate whether projection improvements (Task 2, 6) resolve the skip. | Medium | `MemberEmissionValidator.cs` |

**Acceptance gate**: Empty protocol interfaces with skipped-member root cause drop to 0. Genuinely empty protocols (no ABI members) get explicit diagnostic.

---

### 8. Remove `Info` Suffix on Nested Types

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Medium

50+ types across libraries have `Info` appended: `PaymentSheet.ConfigurationInfo` instead of `PaymentSheet.Configuration`. The suffix avoids CS0542 (property/type name collision) by renaming the nested type.

**Proposed change**: Rename the colliding *property* instead, keeping the type name clean. Types appear in `new`, `typeof`, variable declarations, generic constraints, and documentation. Properties appear only at call sites.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **8a. Reverse the rename priority** | In `NameProvider.ComputeNestedTypeRenames()`, instead of renaming the type, rename the property. Property gets a suffix (`Value`, `Instance`, or context-derived). | Medium | `NameProvider.cs:552-649` |
| **8b. Verify descendant propagation** | Property renames need TypeDatabase propagation like type renames currently do. | Medium | `NameProvider.cs`, `PropertyHandler.cs` |

**Acceptance gate**: Grep for `Info` suffix on nested types in generated output — only types that genuinely end in "Info" in Swift should have the suffix. Count should drop from ~50+ to <5.

---

### 9. Synchronous Error Detail Extraction

**Priority**: P1 | **Effort**: Medium (1-2 sessions) | **Risk**: Low

Synchronous throwing methods throw `SwiftRuntimeException("Call to Swift method {name} failed.")` — losing all error detail. Async methods already extract the actual error message and typed error value via callback parameters.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **9a. Extract error message in sync path** | After checking `error.Value != null`, marshal the error existential to extract `localizedDescription` via a Swift wrapper function (`SBW_Error_GetDescription`). | Medium | `WrapperEmitter.cs:546-589`, new Swift wrapper |
| **9b. Extract typed error in sync path** | For typed throws, use `SwiftMarshal.MarshalFromSwift<TError>()` on the error existential (same pattern as async at `WrapperEmitter.Async.cs:1490`). | Medium | `WrapperEmitter.cs:550-589` |

**Acceptance gate**: All sync `throw new SwiftRuntimeException(...)` include actual Swift error message. Update expectations in `ThrowingMethodTests.cs`.

---

## P2 — Quality Gaps

### 10. Mark NotSupportedException Proxy Members

**Priority**: P2 | **Effort**: Low (1 session) | **Risk**: Low

~320 protocol proxy members throw `NotSupportedException` when called on Swift-backed existential containers, with no compile-time indication.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **10a. Add `[Obsolete]` with SB0003 diagnostic** | Emit on proxy members that throw. Message explains the limitation and reason (non-blittable type, async, throwing, subscript). | Low | `ProtocolProxyEmitter.InterfaceImpl.cs` |

**Acceptance gate**: `SB0003` count matches `NotSupportedException` count in all generated proxy classes.

---

### 11. Suppress Internal/Telemetry Types

**Priority**: P2 | **Effort**: Low-Medium (1 session) | **Risk**: Low

Types like `CameraHardwareInfoPinglet` appear in public API when they should be internal.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **11a. swiftinterface-based access filtering** | Types not present in `.swiftinterface` are internal (even if in ABI JSON). Mark with `[EditorBrowsable(Never)]` or suppress entirely. This is the source of truth. | Medium | `SwiftInterfaceAccessParser.cs`, `MemberEmissionValidator.cs` |
| **11b. Heuristic fallback** | Only when swiftinterface is unavailable: types matching patterns (`*Pinglet*`, `*Telemetry*`, `_*`) get `[EditorBrowsable(Never)]`. | Low | `ClassHandler.cs` or new filter |

**Acceptance gate**: Public type count in generated output decreases for BlinkID, StripePayments. Swiftinterface-based filtering covers >95% of cases.

---

### 12. Normalize Async Method Names

**Priority**: P2 | **Effort**: Low (1 session) | **Risk**: Low

Swift async methods get `Get` prefix from the Swift wrapper: `GetPresentAsync`, `GetConfirmAsync`. .NET convention: `PresentAsync`, `ConfirmAsync`.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **12a. Strip `Get` prefix from async wrappers** | When an async method name starts with `Get` and the original Swift method doesn't, strip it. Expand the partial implementation from WU phase. | Low | `WrapperEmitter.Async.cs`, `MethodHandler.cs` |

**Acceptance gate**: `GetPresentAsync`, `GetConfirmAsync` no longer appear in generated output.

---

### 13. Improve Parameter Naming

**Priority**: P2 | **Effort**: Low-Medium (1 session) | **Risk**: Low

Swift unnamed parameters become `_` and `_2`. Factory method parameters become `value0`. Generic type parameters become `T0`.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **13a. Use Swift argument labels** | ABI JSON contains both parameter names and argument labels. Use argument label when parameter name is `_`. | Low | `MethodSignature.cs`, parser |
| **13b. Rename generic type params** | `T0` → `T` (single param), `T0`/`T1` → `TKey`/`TValue` or `TInput`/`TOutput` based on constraint names or position heuristics. | Low | Emitter generic handling |

**Acceptance gate**: `_2`, `value0`, `T0` counts in generated output drop >80%.

---

### 14. Lightweight Regression Gate

**Priority**: P2 | **Effort**: Low (1 session) | **Risk**: Low

Protect upcoming refactors with a minimal smoke test before full CI. Not a replacement for CI — a fast local pre-push check.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **14a. `check-regressions.sh` script** | Runs: unit tests, regenerates Stripe multi-module bindings, compiles all 25 library outputs, diffs public API surface against baseline. Fails on new AnyType fallbacks or compile errors. Target: <5 min. | Medium | New `check-regressions.sh` |
| **14b. API surface baseline** | Generate `api-baseline.txt` (public type/member signatures) from current output. Diff against baseline on each run to catch unintended API changes. | Low | New `generate-api-baseline.sh` |

**Acceptance gate**: Script runs in <5 min and catches regressions introduced by P0/P1 work.

---

### 15. Actor-Aware Wrapper Emission

**Priority**: P2 | **Effort**: Medium | **Risk**: Low

(Carried forward from previous roadmap.)

Swift 6 enforces actor isolation as hard type-system errors. Parse `@MainActor` annotations from `.swiftinterface` files and emit matching actor isolation on generated wrapper functions.

| Step | Description | Effort | Files |
|------|-------------|--------|-------|
| **Parse @MainActor from swiftinterface** | `SwiftInterfaceAccessParser` already extracts other annotations. Add `@MainActor` / `@_Concurrency.MainActor`. | Low | `SwiftInterfaceAccessParser.cs` |
| **Emit actor isolation on wrappers** | When a protocol/class is `@MainActor`, emit `@MainActor` on generated wrapper functions. | Medium | `EveryProtocolEmitter.cs`, `WitnessDispatchEmitter.cs` |
| **Handle custom actors** | Types like `BlinkIDEventStream` need actor execution context. | Medium | Same |
| **Remove -strict-concurrency=minimal** | Once actor-aware emission covers known cases. | Low | `SwiftWrapperCompiler.cs` |

**Acceptance gate**: BlinkIDUX wrapper compiles with 0 actor isolation errors.

---

## P3 — Polish & Infrastructure

### 16. CI Integration

**Priority**: P3 | **Effort**: Large | **Risk**: Medium

GitHub Actions workflow. macOS runner. Tier 1 on every PR, Tier 2 before merge, Tier 3 nightly. Real-world library validation on merge. Builds on the lightweight regression gate (Task 14).

**Key files**: New `.github/workflows/`, existing test scripts

---

### 17. Library Validation Expansion

**Priority**: P3 | **Effort**: Medium per library | **Risk**: Low

Runtime test apps for additional libraries. Stripe end-to-end (multi-module with dependency chain) is the key target after cross-module resolution (Task 1) ships.

**Verification**: Per-library `build-all.sh` + `validate-sim.sh`

---

### 18. Performance Benchmarks

**Priority**: P3 | **Effort**: Medium | **Risk**: Low

BenchmarkDotNet harness measuring interop overhead. 5 CI perf smoke scenarios.

**Design**: `Future/interop-performance-validation-plan.md`

---

### 19. SwiftUI Bridge Corpus

**Priority**: P3 | **Effort**: Medium | **Risk**: Low

Track bridge coverage across 10+ libraries with 3-tier metrics (generated / typechecked / runtime-validated).

**Design**: `Future/swiftui-bridge-v2-plan.md` (Phase 4)

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

Replace Objective Sharpie. Uses `clang -ast-dump=json`. Same CLI/SDK for Swift and ObjC. ~1,500-2,000 lines new code.

### Emitter Architecture Redesign

**Effort**: Very Large (5+ sessions)
**Design**: `Future/emitter-redesign-proposal.md`

Three-phase architecture: type pre-processing, type processing, emission from representations.

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
| ExistentialContainer0 in tuple element | Lottie edge case | Blocked by `HasClosureUnsafeTupleElements` safety gate (params and returns) |
