# Marshaler Component — Test & Code Review

**Date**: 2026-02-16
**Completed**: 2026-02-16
**Scope**: `src/Swift.Bindings/src/Marshaler/` (13 files, 5,880 LOC)
**Tests**: `src/Swift.Bindings/tests/UnitTests/MarshalerTests/` (15 files, 8,714 LOC)
**Test Ratio**: 1.48x (tests exceed source LOC — good baseline)
**Branch**: `marshaler-tests`

---

## Executive Summary

The Marshaler is the most bug-sensitive component in the pipeline — it decides how Swift types map to C# types, handles closures, tuples, existentials, bound generics, and naming. Many of the known active bugs in the project trace back to marshaling decisions.

**Overall assessment**: Well-architected with good test coverage on happy paths. However, several critical edge cases lack tests, there are silent error-swallowing patterns, and one potential bug in nested type rename collision detection.

| Severity | Count | Description |
|----------|-------|-------------|
| Potential Bug | 2 | Nested type rename collision, silent error swallowing (×2 dedup paths) |
| Missing Coverage (High) | 4 | `MethodRequiresIndirectResult`, B7 closure constraint, two silent catches |
| Missing Coverage (Medium) | 6 | Edge cases and integration points |
| Dead/Suspicious Code | 1 | Duplicate verb |

---

## File-by-File Review

### 1. IFactory.cs (23 LOC)

**Purpose**: Generic factory interface for handler construction (`Handles()`, `Construct()`).

**Coverage**: No dedicated tests — N/A (trivial interface, 2 methods).
**Issues**: None. Minimal interface, tested implicitly through all handler tests.

---

### 2. IEnvironment.cs (213 LOC)

**Purpose**: Context objects carrying state during marshaling — `ModuleEnvironment`, `TypeEnvironment`, `MethodEnvironment`, `PropertyEnvironment`.

**Coverage**: Tested implicitly through every handler test that creates environments.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| E1 | Minor | Line ~154 | `EmittedProjectedSignatures` is nullable but critical for dedup — must be set externally before use. No validation. Works fine in practice because the emitter always sets it, but a new caller could miss it. |
| E2 | Minor | Line ~64, ~97 | `GenericTypeMapping` recomputed fresh each construction — O(N) per environment. Not a bug, but could be cached if performance matters. |

**Verdict**: Adequate — no tests needed for data-holder classes, but E1 could use a guard.

---

### 3. IHandler.cs / BaseHandler (330 LOC)

**Purpose**: Core handler interface and `BaseHandler` implementation with `HandleBaseDecl()` (the main dispatch method), `GetProjectedCSharpMethodKey()` (dedup key), `GetMethodSignatureKey()`.

**Coverage**: Tested implicitly via ConductorTests (handler selection) and MemberEmissionValidator tests.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| H1 | **Silent Error** | Lines 283–291 | `GetProjectedCSharpMethodKey()` has a bare `catch` that silently falls back to `typeSpecForKey?.ToString() ?? "unknown"` when `GetTypeRecordOrAnyType()` fails. No logging. This can silently create bad dedup keys, causing methods to either collide or not collide when they should. |
| H1b | **Silent Error** | Lines 314–324 | `GetMethodSignatureKey()` has an **identical** bare `catch` pattern — same silent fallback to `arg.SwiftTypeSpec?.ToString() ?? "unknown"`. Both dedup key generation paths can independently mask type resolution failures. |
| H2 | Missing Coverage | Lines 119–123 | Struct handler not found (warning path) — never tested. |
| H3 | Missing Coverage | Lines 201–214 | `MemberEmissionValidator.ShouldSkipMethodEmission()` returning non-null (skip path) — tested in MemberEmissionValidator tests but not through `HandleBaseDecl`. |

**Priority**: H1/H1b are high — **two** silent error masking paths in dedup logic can cause subtle duplicate/missing method bugs. Both should log warnings.

---

### 4. NameProvider.cs (849 LOC) — CRITICAL

**Purpose**: C# name generation, collision detection, generic mapping, parameter dedup. 22+ public methods.

**Tests**: NameProviderMethodNamingTests (188 LOC), NameProviderParameterTests (322 LOC), NameProviderRenameTests (580 LOC) — 1,090 LOC total.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| N1 | **Potential Bug** | Line 543 | `ComputeNestedTypeRenames()` appends `"Info"` suffix without checking if `"{TypeName}Info"` already exists as another nested type. If type `Cache` and `CacheInfo` both exist and `Cache` collides with a property, the rename creates a second `CacheInfo`, producing a compile error. This is still untested for the collision case. |
| N6 | Missing Coverage | Line 172 | `GetPInvokeName()` — uses hash for overload distinction. No test for hash collision handling. Theoretical risk only. |
| N7 | Suspicious | Lines 775–776 | `"Relay"` listed twice in `_verbPrefixes`. Not a bug, just redundant. |

**What IS already tested** (verified via Codex cross-review):
- ~~N2~~ Swift `"_"` unnamed parameter path: `NameProviderParameterTests.cs:84`
- ~~N3~~ `arg1+` suffix disambiguation: `NameProviderParameterTests.cs:233`
- ~~N4~~ All-caps type derivation (`URL→url`): `NameProviderParameterTests.cs:223`
- ~~N5~~ Deeply nested rename propagation: `NameProviderRenameTests.cs:182`

**Priority**: N1 is high — can produce compile errors in generated code. N6 is theoretical only.

---

### 5. MarshallingHelpers.cs (108 LOC)

**Purpose**: Simple flag/property checks — `MethodRequiresIndirectResult()`, `MethodRequiresSwiftSelf()`, `IsTypeFrozen()`, etc.

**Tests**: MarshallingHelpersTests (152 LOC) — covers `MethodIsSetter` and `IsObjCBridged` only.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| M1 | **Missing Coverage** | Lines 9–60 | `MethodRequiresIndirectResult()` — the most complex method in the file (async, failable constructors, frozen structs, closures, existentials, tuples, bound generics). **No tests at all.** End-to-end coverage exists through integration tests, but no unit tests isolate this logic. |
| M2 | Missing Coverage | Lines 62–69 | `MethodRequiresSwiftSelf()` — no direct test. |
| M3 | Missing Coverage | Lines 71–84 | `IsTypeFrozen()`, `RequiresMemoryManagement()`, `IsFrozenStructProjectedAsClass()` — no direct tests (simple flag checks, low risk). |

**Verdict**: The test file is **narrower than it appears** — only `MethodIsSetter` (7 tests) and `IsObjCBridged` (5 tests) are covered. `MethodRequiresIndirectResult` has zero unit tests despite being the most complex method.

---

### 6. Conductor.cs (195 LOC)

**Purpose**: Handler factory management, dispatch, composition interface collection. ThreadLocal state management.

**Tests**: ConductorTests (485 LOC) — covers handler selection well.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| ~~C1~~ | ~~Missing Coverage~~ | — | ~~`CurrentPInvokeHelperContext` mutation — never directly tested.~~ **Corrected**: Tested at `ConductorTests.cs:448` (`CurrentPInvokeHelperContext_DefaultsToNull`, `CanBeSetAndCleared`). |
| C2 | Missing Coverage | — | `NestedTypeRenames` mutation and `DeferredPInvokeHelperContexts` — not directly tested. |
| C3 | Minor | Line ~72 | `CompositionInterfaces.Clear()` called without synchronization on ThreadLocal reference. Safe in practice (single-threaded per module), but undocumented threading model. |
| C4 | Minor | Line ~90 | `CollectCompositionInterface()` uses `?.TryAdd()` which silently no-ops if no collector set. Intentional but implicit. |

**Verdict**: Handler selection well-tested. State management untested but low-risk (simple mutable fields used in well-defined flow).

---

### 7. MonoJitRiskDetector.cs (279 LOC)

**Purpose**: Detect Mono JIT crash patterns (CallConvSwift risks). `AnalyzeMethod()`, `NeedsClosureCdeclWrapper()`.

**Tests**: MonoJitRiskDetectorTests (855 LOC) — **excellent** coverage, 3x ratio.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| J1 | Known Limitation | Line 263 | `HasConventionCInMangledName()` uses `Contains("XC")` substring check — may false-positive on identifiers containing "XC" (e.g., `processXCData`). Documented in comments as intentionally conservative (safe direction: over-suppresses wrapper rather than under-suppresses). |
| J2 | Missing Coverage | Line ~247 | `NeedsClosureCdeclWrapper()` integration with `ClosureEmitter.IsClosureCdeclCompatible` — tested at the ClosureEmitter level but not through MonoJitRiskDetector. |

**Verdict**: Excellent test quality. J1 is a known/documented design choice, not a bug.

---

### 8. ExistentialHandler.cs (440 LOC)

**Purpose**: Protocol existential type (`any Protocol`) handling — detection, size calculation, C# type mapping.

**Tests**: ExistentialHandlerTests (606 LOC) — good coverage.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| X1 | Known Limitation | Lines 122–126 | Comment: "For now, we allow any protocol" — no PAT (protocol with associated types) checking. This is a known design gap, not a test gap. |
| X2 | Missing Coverage | Lines 161–175 | `GetExistentialContainerSize*()` size calculations — used internally, never directly unit tested. These compute `(4 + N) * sizeof(nint)` so they're simple arithmetic, but a test would document the expected sizes. |

**Verdict**: Good coverage. X1 is a known design limitation. X2 is low risk.

---

### 9. TupleHandler.cs (490 LOC)

**Purpose**: Tuple type handling — support checks, C# type generation, element translation.

**Tests**: TupleHandlerTests (577 LOC) — good coverage.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| T1 | Missing Coverage | Lines 198–200 | `IsSupportedTupleElementType()` — bound generic tuple element (`IsSupportedGenericTupleElement`) call. Not directly tested. |
| T2 | Missing Coverage | Lines 219–244 | `IsSupportedGenericTupleElement()` — existential generic param branch (lines 230–236). |
| T3 | Missing Coverage | Lines 318–332 | `HasClosureUnsafeTupleElements()` — string comparison `!= "IntPtr" && != "System.IntPtr"` is fragile. Works but type-based check would be more robust. |
| T4 | Missing Coverage | Lines 367–403 | `TranslateBoundGenericToCSharp()` — IntPtr pointer type case (lines 377–380). |

**What IS already tested** (verified via Codex cross-review):
- ~~T5~~ 7 vs 8 element boundary: `TupleHandlerTests.cs:177` (8→false), `:190` (7→true)

**Verdict**: Good coverage of common paths. Gaps are in uncommon combinations (generic tuple elements, closure tuples).

---

### 10. TypeConversionHandler.cs (653 LOC)

**Purpose**: Automatic type conversions — SwiftString↔string, SwiftArray↔IEnumerable, SwiftOptional↔nullable, Foundation URL/Data remapping.

**Tests**: TypeConversionHandlerTests (738 LOC) — good coverage.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| TC1 | Missing Coverage | Lines 126–139 | `GetIdiomaticCSharpType()` — Optional\<Closure> and Optional\<Existential> guard paths. These return `null` to signal "not convertible" but aren't tested for that null path. |
| TC5 | Missing Coverage | Lines 447–652 | Native type remapping (`URL → NSUrl`, `Data → NSData`). `GetSwiftWrapperTypeForNative()` is not directly tested. |

**What IS already tested** (verified via Codex cross-review):
- ~~TC2~~ Nested array conversion: `TypeConversionHandlerTests.cs:572`
- ~~TC3~~ Optional\<Array\<String>> conversion: `TypeConversionHandlerTests.cs:552`
- ~~TC4~~ Optional\<Array> return conversion: `TypeConversionHandlerTests.cs:292`, `:307`

**Priority**: TC1 and TC5 are medium.

---

### 11. BoundGenericsHandler.cs (863 LOC) — VERY COMPLEX

**Purpose**: Generic type translation and validation — constraint checking, bare generic detection, ObjC-bridged type detection.

**Tests**: BoundGenericsHandlerTests (776 LOC) — good ratio, but complex logic means many paths.

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| BG1 | TODO | Line 66 | "Should also check that return type is not the type's own generic parameter" — acknowledged incomplete logic. |
| BG2 | TODO | Line 438 | "Consider throwing an exception instead" of `GetTypeRecordOrAnyType` fallback — silent fallback to AnyType. |
| BG3 | Missing Coverage | Lines 503–504 | `TranslateTypeSpecToCSharp()` — `NotSupportedException` for unknown TypeSpec subclass. Should be tested even if unlikely, to document the contract. |
| BG4 | Missing Coverage | Lines 378–391 | `HasNonSwiftObjectGenericArg()` — Optional tuple with existential element (B5 skip gate). Not tested. |
| BG5 | Missing Coverage | Line 179 | `TryGetFirstExistentialTypeArgument()` — ClosureTypeSpec.Arguments path in recursive existential search. |
| BG6 | Missing Coverage | Lines 508–544 | `QualifyNestedGenericOwners()` — complex nested generic qualification not explicitly tested. |

**Priority**: BG1 and BG2 are known TODOs. BG4 is medium — existentials inside optional tuples inside generic args is a real pattern in libraries like Combine.

---

### 12. ClosureHandler.cs (1,273 LOC) — LARGEST, MOST COMPLEX

**Purpose**: Closure (function pointer) handling — support checks, delegate type generation, P/Invoke function pointer types, async/throwing support.

**Tests**: ClosureHandlerTests (1,973 LOC) + ClosureExistentialTests (413 LOC) — **excellent** total coverage (2,386 LOC, 1.87x).

**Findings**:

| # | Type | Location | Description |
|---|------|----------|-------------|
| CL1 | Missing Coverage (B7) | Lines 340–350 | `IsSupportedClosureReturnType()` — generic param requiring memory management unsupported in closure returns. Documented constraint, but no explicit test verifies the rejection. |
| CL3 | Missing Coverage (B16) | Lines 430–438 | `IsSupportedClosureParameterType()` — C# enums are non-blittable in callbacks. The enum rejection path through `typeDatabase.TryGetTypeRecord` isn't explicitly tested. |
| CL4 | Missing Coverage | Lines 331–335 | `IsSupportedClosureReturnType()` — existential generic params in Optional explicitly unsupported but no test. |
| CL6 | Missing Coverage | Line 289 | `CanUseDirectCallbackReturn()` — `GetBlittablePrimitiveType()` call path not directly tested. |

**What IS already tested** (verified via Codex cross-review):
- ~~CL2~~ B13 async+throwing: `ClosureHandlerTests.cs:494` (no params → true), `:512` (with params → false)
- ~~CL5~~ Complex closure return types: `ClosureHandlerTests.cs:315` (tuple returns), `:380` (return validation flow)

**Priority**: CL1/CL3 are medium-high — these are documented constraints (B7, B16) that should have explicit tests proving the guard works. A regression here would silently emit non-compiling code.

---

### 13. AsyncStreamHandler.cs (164 LOC)

**Purpose**: AsyncStream/AsyncThrowingStream type handling.

**Tests**: AsyncStreamHandlerTests (287 LOC) — **excellent** coverage, 1.75x.

**Findings**: None. All public methods tested. Good edge case coverage.

**Verdict**: No action needed.

---

## Summary: Coverage Gaps by Priority

### HIGH — Should add tests before next major change

| ID | File | Gap | Risk |
|----|------|-----|------|
| H1+H1b | IHandler.cs:283, :319 | **Two** silent `catch` blocks in both dedup key methods — no logging, fall back to string repr | Dedup keys could silently be wrong |
| N1 | NameProvider.cs:543 | `ComputeNestedTypeRenames()` doesn't check if `{Name}Info` already exists | Generated code compile error |
| M1 | MarshallingHelpers.cs:9 | `MethodRequiresIndirectResult()` has **zero** unit tests despite being the most complex method | Indirect result logic untested |
| CL1 | ClosureHandler.cs:340 | B7 constraint (memory-mgmt generic in closure return) has no test | Regression could emit bad code |

### MEDIUM — Add tests during normal development

| ID | File | Gap |
|----|------|-----|
| CL3 | ClosureHandler.cs:430 | B16 constraint (enum in callback) has no test |
| CL4 | ClosureHandler.cs:331 | Existential generic params in Optional closure return |
| BG4 | BoundGenericsHandler.cs:378 | Optional tuple with existential element untested |
| T1 | TupleHandler.cs:198 | Bound generic tuple element untested |
| TC1 | TypeConversionHandler.cs:126 | Optional\<Closure>/Optional\<Existential> null guard path |
| BG6 | BoundGenericsHandler.cs:508 | Nested generic owner qualification |

### LOW — Nice to have

| ID | File | Gap |
|----|------|-----|
| E1 | IEnvironment.cs:154 | EmittedProjectedSignatures nullable without guard |
| X2 | ExistentialHandler.cs:161 | Size calculations untested (simple arithmetic) |
| C2 | Conductor.cs | NestedTypeRenames/DeferredPInvokeHelperContexts mutation untested |

---

## Test Quality Assessment

### Tests that are solid and test real behavior
- **ClosureHandlerTests** — 1,973 LOC, tests real Swift closure patterns, verifies both supported and unsupported cases
- **MonoJitRiskDetectorTests** — 855 LOC, tests all risk flags with realistic method declarations
- **TypeConversionHandlerTests** — 738 LOC, tests string/array/optional conversions with actual type specs
- **BoundGenericsHandlerTests** — 776 LOC, tests generic resolution with real type database entries
- **NameProviderRenameTests** — 580 LOC, tests property/type collision detection with real type declarations

### Tests that could be deeper
- **MarshallingHelpersTests** (152 LOC) — **only tests `MethodIsSetter` and `IsObjCBridged`**. `MethodRequiresIndirectResult()` (the most complex method, 50+ LOC with 8+ branches) has zero unit tests. `MethodRequiresSwiftSelf()`, `IsTypeFrozen()`, `RequiresMemoryManagement()`, `IsFrozenStructProjectedAsClass()` also untested.
- **ConductorTests** (485 LOC) — tests handler selection and PInvokeHelperContext set/clear, but doesn't test DeferredPInvokeHelperContexts or composition collection
- **NameProviderMethodNamingTests** (188 LOC) — covers verb detection and PascalCase well, but only 188 LOC for 22 methods suggests gaps

### No fake/trivially-passing tests detected
All reviewed test files use meaningful assertions on handler outputs, type mappings, and name generation results. The test infrastructure uses realistic `MethodDecl`, `TypeSpec`, and `TypeDatabase` builders that produce representative test data.

---

## Bugs / Suspicious Code Found

### Confirmed Issues

1. **N1 — Nested type rename collision** (`NameProvider.cs:543`):
   ```csharp
   renames[typeName] = $"{typeName}Info";
   ```
   No check for whether `{typeName}Info` already exists. If a Swift type has both `Cache` (colliding with a property) and `CacheInfo` as nested types, the rename produces duplicate `CacheInfo`. Fix: check for collision and use incrementing suffix (`CacheInfo2`, etc.).

2. **H1+H1b — Two silent errors in dedup keys** (`IHandler.cs:283–291` and `:314–324`):
   ```csharp
   catch
   {
       paramTypes.Add(typeSpecForKey?.ToString() ?? "unknown");
   }
   ```
   **Both** `GetProjectedCSharpMethodKey()` and `GetMethodSignatureKey()` have bare `catch` blocks that silently fall back to the TypeSpec string representation when `GetTypeRecordOrAnyType()` fails. This can produce inconsistent dedup keys between the two methods, leading to either duplicate methods or missing methods. Both should at minimum log a warning.

### Design Observations (not bugs)

- **Circular handler dependencies**: ClosureHandler creates TupleHandler, ExistentialHandler; TupleHandler creates ExistentialHandler; BoundGenericsHandler creates all three. Makes isolated unit testing harder but works in practice.
- **Thread-local composition collector** (Conductor): Threading model undocumented but implicitly single-threaded per module.
- **XC substring check** (MonoJitRiskDetector:263): Intentionally conservative — over-suppresses Cdecl wrappers on false positive (safe direction).

---

## Session Plan

**1 session.** The marshaler is already at 1.48x test ratio — the work is targeted edge-case tests and 2 small bug fixes, not building coverage from scratch. All test files have existing infrastructure and patterns to follow.

### Single Session — Bug fixes, logging, and coverage gap tests

| Work Item | Files Touched | Est. Effort |
|-----------|---------------|-------------|
| **Fix N1**: Add collision check to `ComputeNestedTypeRenames()` — verify `{Name}Info` doesn't already exist before renaming. Add incrementing suffix fallback. | `NameProvider.cs`, `NameProviderRenameTests.cs` | Light |
| **Fix H1+H1b**: Add warning logging to both bare `catch` blocks in `GetProjectedCSharpMethodKey()` and `GetMethodSignatureKey()`. | `IHandler.cs` | Trivial |
| **Cleanup N7**: Remove duplicate `"Relay"` in `_verbPrefixes`. | `NameProvider.cs` | Trivial |
| **Test N1**: Nested type rename collision — type `Cache` collides with property while `CacheInfo` already exists as a nested type. | `NameProviderRenameTests.cs` | Light |
| **Test H1+H1b**: Force `GetTypeRecordOrAnyType()` failure in both dedup key methods, verify fallback behavior and logging. | New or existing handler test file | Light |
| **Test M1**: `MethodRequiresIndirectResult()` — 8+ branches: async, failable constructors, frozen structs, closures, existentials, tuples, bound generics, normal methods. | `MarshallingHelpersTests.cs` | Medium |
| **Test CL1**: B7 constraint — memory-management generic in closure return type rejected. | `ClosureHandlerTests.cs` | Light |
| **Test CL3**: B16 constraint — C# enum rejected in callback parameter. | `ClosureHandlerTests.cs` | Light |
| **Test CL4**: Existential generic param in Optional closure return unsupported. | `ClosureHandlerTests.cs` | Light |
| **Test BG4**: Optional tuple with existential element (B5 skip gate). | `BoundGenericsHandlerTests.cs` | Light |
| **Test T1**: Bound generic tuple element support check. | `TupleHandlerTests.cs` | Light |
| **Test TC1**: Optional\<Closure>/Optional\<Existential> null guard paths in `GetIdiomaticCSharpType()`. | `TypeConversionHandlerTests.cs` | Light |
| **Test BG6**: Nested generic owner qualification in `QualifyNestedGenericOwners()`. | `BoundGenericsHandlerTests.cs` | Light |

**Why this fits in one session**: Unlike the demangler (which has zero-coverage components and needs mangled symbol research), every marshaler test file already has builder helpers, realistic test data patterns, and passing tests to model after. The bug fixes are 2-5 line changes. The biggest item (M1) is ~8 test methods for a ~50 LOC method with clear branching logic. No research or test data sourcing required.

**Verification**: Run `./run-tests.sh | tail -20` at the end. Expect baseline + new tests passing, zero regressions.

---

## Review Corrections (from Codex cross-review)

The initial review was cross-reviewed by Codex. Corrections applied:

**Round 1 — False positives removed** (these paths ARE already tested):
- ~~N2~~ `"_"` unnamed parameter: `NameProviderParameterTests.cs:84`
- ~~N3~~ `arg1+` suffix: `NameProviderParameterTests.cs:233`
- ~~N4~~ All-caps type derivation: `NameProviderParameterTests.cs:223`
- ~~N5~~ Deep nested rename propagation: `NameProviderRenameTests.cs:182`
- ~~CL2~~ B13 async+throwing: `ClosureHandlerTests.cs:494`, `:512`
- ~~T5~~ Tuple 7 vs 8 boundary: `TupleHandlerTests.cs:177`, `:190`
- ~~TC2~~ Nested array conversion: `TypeConversionHandlerTests.cs:572`
- ~~TC3~~ Optional\<Array\<String>>: `TypeConversionHandlerTests.cs:552`
- ~~C1~~ PInvokeHelperContext: `ConductorTests.cs:448`

**Round 1 — Findings added**:
- **H1b**: Second silent `catch` in `GetMethodSignatureKey()` at `IHandler.cs:319` (same pattern as H1)
- **M1 promoted to HIGH**: `MethodRequiresIndirectResult()` has zero unit tests — `MarshallingHelpersTests` only covers `MethodIsSetter` + `IsObjCBridged`

**Round 2 — Additional corrections**:
- ~~H4~~ Removed: `GetMethodSignatureKey` does not call `GetIdiomaticCSharpType` — finding was miswired to wrong method
- ~~TC4~~ Optional\<Array> return conversion: `TypeConversionHandlerTests.cs:292`, `:307`
- ~~CL5~~ Complex closure return types: `ClosureHandlerTests.cs:315`, `:380`

---

## Completion Notes

**All work items completed.** 3 bug fixes, 1 cleanup, 43 new tests across 7 test files + 1 new test file.

### Bug Fixes

| Item | Change |
|------|--------|
| **N1** | `NameProvider.cs:543` — `ComputeNestedTypeRenames()` now checks if `{TypeName}Info` already exists before renaming. Uses incrementing suffix (`Info2`, `Info3`, etc.) on collision. |
| **H1+H1b** | `IHandler.cs` — Added `ILogger? logger = null` parameter to both `GetProjectedCSharpMethodKey` and `GetMethodSignatureKey` (kept `private static`). Changed bare `catch` to `catch (Exception ex)` with `logger?.LogWarning(...)`. Updated 4 existing test files that use reflection to pass `null` logger. |
| **N7** | `NameProvider.cs` — Removed duplicate `"Relay"` in `_verbPrefixes`. |

### Tests Added

| Item | File | Tests Added | Description |
|------|------|-------------|-------------|
| **N1** | `NameProviderRenameTests.cs` | 3 | Collision scenarios: `InfoSuffixAlreadyExists`, `MultipleInfoSuffixCollisions`, `TwoCollisionsOneInfoExists` |
| **H1+H1b** | `BaseHandlerDedupTests.cs` (**new**) | 10 | Dedup key generation: known types use idiomatic names, unknown types fall back to AnyType, non-empty tuples resolve to AnyType, async includes CancellationToken, constructor prefixed with "ctor:". **ThrowingTypeDatabase** tests exercise actual catch blocks (Codex review fix). |
| **M1** | `MarshallingHelpersTests.cs` | 12 | `MethodRequiresIndirectResult()` — all 8+ branches: async, failable constructors, frozen/non-frozen structs, closures, existentials, tuples, bound generics, generic returns, classes |
| **CL1+CL3+CL4** | `ClosureHandlerTests.cs` | 4 | B7 (memory-mgmt generic in closure return), B16 (enum in callback param), CL4 (existential generic in Optional closure return) |
| **BG4+BG6** | `BoundGenericsHandlerTests.cs` | 5 | Optional tuple with existential element (B5 skip gate), nested generic owner qualification |
| **T1** | `TupleHandlerTests.cs` | 2 | Bound generic tuple element support check |
| **TC1** | `TypeConversionHandlerTests.cs` | 4 | Optional\<Closure>/Optional\<Existential> null guard paths |

### Codex Review Feedback (applied)

Initial H1/H1b catch-path tests used non-empty tuples, which hit the `_ => AnyType` default in `GetTypeRecordOrAnyType(TypeSpec)` without throwing — the catch blocks were never exercised. Fixed by adding a `ThrowingTypeDatabase` that throws `InvalidOperationException` from `TryGetTypeRecord`, properly triggering the catch-block string fallback path. Existing tuple tests renamed to accurately document the `_ => AnyType` path they actually test.

### Test Results

- **2914 passed**, 0 new failures
- 41 pre-existing failures in `SdkTargetsContentTests`/`SdkPropsContentTests`/`BuildScriptTests` (repo-root detection — unrelated to this work)
