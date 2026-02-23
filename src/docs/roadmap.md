# Roadmap

**Updated**: February 2026
**Status**: Active — binding quality phase
**Target**: Raise binding quality from 6.5/10 to 8.5+/10 for .NET developer experience

For completed work (cross-module resolution, ExistentialContainer elimination, native C# enums, Optional truncation fix, SwiftDictionary projection, architecture overhaul), see `Completed/roadmap-completed-feb2026.md`.

---

## Current Baselines

| Metric | Value |
|--------|-------|
| Unit tests | 4,001 passing |
| Integration tests | 700 passing (11 skipped, pre-existing) |
| Runtime library tests | 221 passing |
| TestFramework must-pass | 94/94 passing, 0 degraded |
| Libraries validated | 32/32 passing (all at 0 compile errors) |

---

## Acceptance KPIs

Measured across all 32 validated libraries. Grep/compile checks, not subjective scores.

| KPI | Current | Target | Session |
|-----|---------|--------|---------|
| `value0` parameters | ~~831~~ **0** (labeled) | <50 | ~~B~~ Done |
| `GetXxxAsync` method names | ~~124~~ **0** | 0 | ~~A~~ Done |
| `SwiftOptional<` in public signatures | **97** | 0 | A' |
| Empty protocol interfaces (0 members) | **67** | <10 | E |
| `SwiftArray<` in public signatures | **40** | 0 | A' |
| Sync throw messages with actual error text | ~~0%~~ **100%** | 100% | ~~C~~ C1 Done |
| Types with spurious `Info` suffix | ~50+ | <5 | D |

---

## Session Plan

7 sessions (A' added for deferred idiomatic projection work, C1 complete), grouped by shared code paths.

---

### Session A: Public API Type Projection (Partial — A2+A4 Complete)

**Priority**: P1 | **Status**: Partial | **Impact**: 124 async naming issues fixed + QuartzCore bridging

| Step | Description | Impact | Status |
|------|-------------|--------|--------|
| **A1. SwiftOptional projection** | `SwiftOptional<T>` → `T?` in tuple elements, bound-generic fallbacks, enum factory params, async stream elements. | 97 occurrences | **Deferred → A'** |
| **A2. Async method names** | Skip `Get` prefix for async methods: `GetPresentAsync` → `PresentAsync`. Added `&& !isAsync` guard + 9 new verbs to `_verbPrefixes`. Also fixed completion handler dedup key to use `NormalizeParamTypeForOverloadIdentity`. | 124 → 0 | **Done** |
| **A3. SwiftArray projection** | `SwiftArray<T>` → `IReadOnlyList<T>` in async stream elements, tuple elements, enum factory params, bound-generic fallbacks. | 40 occurrences | **Deferred → A'** |
| **A4. QuartzCore auto-bridging** | Added QuartzCore to `AppleObjCFrameworkModules` with `ModuleToCSharpNamespaceOverrides` (QuartzCore → CoreAnimation). 12 value types + 4 NSString typedefs (as ObjC class remaps to Foundation.NSString). | Lottie improved | **Done** |

**Key files changed**: `NameProvider.cs`, `TypeDatabaseExtensions.cs`, `MethodHandler.cs`, `MethodSignature.cs` (comments only)

### Session B: Parameter Naming (Complete)

**Priority**: P1 | **Effort**: Low (1 session) | **Impact**: 831 issues fixed | **Status**: Complete

| Step | Description | Impact | Status |
|------|-------------|--------|--------|
| **B1. Type-based derivation** | `DeriveParameterNameFromType` handles Optional→inner, Array→"items", Dictionary→"dictionary", `*Error`→"error". | ~30 occurrences | **Done** |
| **B2. Tuple labels for enum associated values** | Parsed from Tuple node's `printedName` via `TypeSpecParser.Parse()` in `SwiftABIParser.CreateEnumCaseDecl()`. Swiftinterface overlay fills gaps. | 0 `value0` in labeled enums | **Done** |
| **B3. Sugared generic param renaming** | `NameProvider.GetCSharpGenericParameterName` maps `SugaredTypeName` to C# (`"T"→"T"`, `"U"→"TU"`, `"Key"→"TKey"`, τ fallback→`"T{index}"`). | 0 `T0` across 32 libraries | **Done** |

**Key files**: `NameProvider.cs`, `EnumHandler.cs`, `TypeSpecParser.cs`, `SwiftABIParser.cs`

---

### Session A': Idiomatic Type Projection in Fallback Paths

**Priority**: P1 | **Effort**: Medium (1 session) | **Impact**: 137 issues (97 SwiftOptional + 40 SwiftArray)

Completes the deferred A1/A3 work. The root cause is a **signature-body mismatch**: changing public signatures to idiomatic types (`T?`, `IReadOnlyList<T>`) breaks the marshalling body in WrapperEmitter, which emits `.Payload` property access requiring the raw `SwiftOptional<T>`/`SwiftArray<T>` types. The `TypeProjectionFactory` already handles cases where both signature and body agree (properties, regular methods). The remaining 137 issues are in fallback paths where the factory returns null.

**Approach**: Extend `TypeProjectionFactory` coverage to the remaining contexts, so both signature and body use the same projection. Each leak point needs coordinated signature + body changes:

| Leak point | Files | Types affected |
|------------|-------|---------------|
| Tuple element types | `MethodSignature.cs`, `WrapperEmitter.Return.cs` | Optional, Array |
| Bound-generic fallback (return) | `MethodSignature.cs` | Optional, Array |
| Bound-generic fallback (params) | `MethodSignature.cs` | Optional, Array |
| Enum case factory params | `EnumHandler.CaseConstruction.cs` | Optional, Array |
| AsyncStream element types | `AsyncStreamHandler.cs` | Array (requires `ISwiftObject` constraint workaround) |

**Key constraint**: `SwiftAsyncStream<TElement>` requires `TElement : ISwiftObject`, so `IReadOnlyList<T>` and `string` cannot be used directly as element types. AsyncStream array projection may need a wrapper approach.

**Acceptance gate**: `grep -r "SwiftOptional<\|SwiftArray<" --include="*.cs"` across validation output → 0.

---

### Session C: Sync Error Detail Extraction (Partial — C1 Complete)

**Priority**: P1 | **Effort**: Medium (1 session) | **Impact**: Correctness

Sync throwing methods previously threw `SwiftRuntimeException("Call to Swift method {name} failed.")` — losing all error detail. Async already extracts actual error messages via callbacks.

| Step | Description | Effort | Status |
|------|-------------|--------|--------|
| **C1. Extract error message** | `ErrorDescriptionEmitter` emits `SBW_GetErrorDescription` (Swift `String(describing:)` via `Unmanaged<AnyObject>.fromOpaque`) and `SBW_ReleaseError` per module. Generated C# extracts message, frees C string via `SBW_Free`, releases error reference, throws with real message. | Medium | **Done** |
| **C2. Extract typed error value** | For typed throws, `MarshalFromSwift<TError>()` on the error existential to populate `SwiftException<TError>.Error` (same pattern as async). Currently `default`. | Medium | **Deferred** |

**Key files changed (C1)**: `ErrorDescriptionEmitter.cs` (new), `ModuleHandler.cs`, `WrapperEmitter.cs`, `WrapperEmitter.Marshalling.cs`, `WrapperEmitter.FailableFactory.cs`, `MethodMarshalPlanBuilder.cs`
**Acceptance gate**: All sync `throw new SwiftRuntimeException(...)` include actual Swift error message. ✅ (C1)
**Remaining (C2)**: `SwiftException<TError>.Error` is non-null for sync typed throws (currently only async populates it).

---

### Session D: Info Suffix Removal

**Priority**: P1 | **Effort**: Medium (1 session) | **Risk**: Medium | **Impact**: ~50+ types

`PaymentSheet.ConfigurationInfo` instead of `PaymentSheet.Configuration`. The suffix avoids CS0542 (property/type name collision) by renaming the type — should rename the *property* instead.

| Step | Description | Effort |
|------|-------------|--------|
| **D1. Reverse rename priority** | In `NameProvider.ComputeNestedTypeRenames()`, rename the property (not the type). Property gets a suffix (`Value`, `Instance`, or context-derived). | Medium |
| **D2. Verify descendant propagation** | Property renames need TypeDatabase propagation like type renames currently do. | Medium |

**Key files**: `NameProvider.cs`, `PropertyHandler.cs`
**Acceptance gate**: `Info`-suffixed nested types that don't genuinely end in "Info" in Swift drop to <5.

---

### Session E: Protocol Quality

**Priority**: P2 | **Effort**: Medium (1 session) | **Impact**: 67 empty interfaces + 320 unmarked throwing members

Both items touch protocol handler/proxy emitter code.

| Step | Description | Impact | Effort |
|------|-------------|--------|--------|
| **E1. Audit empty interface root causes** | Regenerate all libraries, categorize: genuinely empty (marker protocols) vs. skipped members. | 67 interfaces triaged | Low |
| **E2. Emit diagnostic on empty interfaces** | `[Obsolete("...", DiagnosticId = "SB0004")]` with skip reasons. Suppress genuinely empty protocols. | Discoverability | Low |
| **E3. Reduce member skip rate** | Evaluate whether closure marshalling in protocol proxy receivers can recover skipped members. | Fewer empties | Medium |
| **E4. Mark NotSupportedException proxy members** | `[Obsolete("...", DiagnosticId = "SB0003")]` on proxy members that throw, explaining the limitation. | 320 members marked | Low |

**Key files**: `ProtocolHandler.cs`, `ProtocolProxyEmitter.cs`, `ProtocolProxyEmitter.InterfaceImpl.cs`, `MemberEmissionValidator.cs`
**Acceptance gate**: `SB0003` count matches `NotSupportedException` count. Empty interfaces with skipped-member root cause drop to 0.

---

### Session F: Swiftinterface Parsing

**Priority**: P2 | **Effort**: Medium (1 session)

Both items extend `SwiftInterfaceAccessParser.cs` — same parsing pattern, different annotations.

| Step | Description | Effort |
|------|-------------|--------|
| **F1. Access-level filtering** | Types absent from `.swiftinterface` → `[EditorBrowsable(Never)]` or suppressed entirely. Heuristic fallback when swiftinterface unavailable: `*Pinglet*`, `*Telemetry*`, `_*`. | Medium |
| **F2. Parse @MainActor** | `SwiftInterfaceAccessParser` already extracts other annotations. Add `@MainActor` / `@_Concurrency.MainActor`. | Low |
| **F3. Emit actor isolation on wrappers** | When protocol/class is `@MainActor`, emit on generated wrapper functions. Handle custom actors. | Medium |
| **F4. Remove -strict-concurrency=minimal** | Once actor-aware emission covers known cases. | Low |

**Key files**: `SwiftInterfaceAccessParser.cs`, `MemberEmissionValidator.cs`, `EveryProtocolEmitter.cs`, `SwiftWrapperCompiler.cs`
**Acceptance gate**: Internal types suppressed for BlinkID/StripePayments. BlinkIDUX wrapper compiles with 0 actor isolation errors.

---

## P3 — Polish & Infrastructure

### 12. Lightweight Regression Gate

Fast local pre-push check: unit tests + regenerate Stripe multi-module + compile all libraries + diff API surface. Target: <5 min. Not a CI replacement.

### 13. CI Integration

GitHub Actions: Tier 1 on every PR, Tier 2 before merge, Tier 3 nightly, library validation on merge.

### 14. Library Validation Expansion

Runtime test apps for additional libraries. Stripe end-to-end (multi-module) is the key target.

### 15. Performance Benchmarks

BenchmarkDotNet harness measuring interop overhead. Design: `Future/interop-performance-validation-plan.md`.

### 16. SwiftUI Bridge Corpus

Coverage tracking across 10+ libraries. Design: `Future/swiftui-bridge-v2-plan.md` (Phase 4).

---

## P4 — Future Vision

### Class Inheritance Hierarchy

**Effort**: Very Large (5+ sessions). Emit C# class hierarchies mirroring Swift type graph. Prerequisite: ObjC binding integration.

### ObjC Binding Integration

**Effort**: Large (3-5 sessions). Replace Objective Sharpie via `clang -ast-dump=json`. Design: `Future/objc-binding-integration.md`.

### Multi-Platform Support

**Effort**: Large (3+ sessions). Extend beyond iOS to Mac Catalyst, macOS, tvOS. Design: `Future/dx-multi-framework-auto-detection.md`.

### Emitter Architecture Redesign

**Effort**: Very Large (5+ sessions). Three-phase: pre-processing, processing, emission. Design: `Future/emitter-redesign-proposal.md`. Current emitter works for 32 libraries; migrate incrementally.

---

## Blocked on Upstream (.NET Runtime)

Workarounds in place. Draft bug reports: `Future/upstream-bug-reports-draft.md`.

| Issue | Current Mitigation | Unblocked When |
|-------|--------------------|----------------|
| SafeHandle finalizer crashes on Mono | `Dispose()` required; Tier 3 tests | Mono JIT CallConvSwift fix |
| Non-blittable types with CallConvSwift | Wrapper methods + `MonoJitRiskDetector` | dotnet/runtime managed type marshalling |
| Async runtime (32 tests, Tier 3) | Tests written, tagged Tier 3 | Same as above |
| Non-primitive closure Cdecl | Fall back to CallConvSwift | Mono JIT fix OR Swift adapters |
| SafeHandle in async P/Invoke | Singleton + IntPtr conversion | dotnet/runtime SwiftSelf async support |

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
