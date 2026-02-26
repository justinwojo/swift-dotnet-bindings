# Architectural Review 2 — February 25, 2026

## Executive Summary

1. ~~**The three extension emitters (ProtocolExtension, ForeignType, CrossModule) share ~60% structural overlap but no common base.**~~ **Resolved (A3).** `ExtensionMarshallingHelper` shares classify + marshalling. `ModuleEmissionContext` provides typed dedup API for wrapper accumulation. ~~Severity: High.~~

2. **ClosureHandler (1,620 LOC) is a parallel type system that bypasses TypeProjectionFactory entirely.** It contains its own `TranslateTypeSpecToCSharp`, `TranslateBoundGenericToCSharp`, and `TranslateTypeSpecToPInvokeType` — reimplementing resolution logic the factory was designed to own. The factory explicitly returns `null` for user-defined bound generics (line 204), so callers must still fall back to BoundGenericsHandler directly. The rework unified the *happy path* but left the hard cases fragmented. **Severity: High.**

3. ~~**Static mutable state is scattered across 7+ emitters, all using the same `_structEmitted` / `_swiftWrapperLines` / `_emittedSymbols` pattern with manual `ResetForModule()` calls.**~~ **Resolved (A3).** `ModuleEmissionContext` replaces all static state with per-module instances and typed dedup API. Zero `ResetForModule()` calls remain. ~~Severity: High.~~

4. ~~**19 separate files emit P/Invoke declarations independently**~~ **Resolved (A1.7).** `PInvokeEmitHelper` now centralizes P/Invoke declaration emission. All 38 explicit `[UnmanagedCallConv]` sites across 19 files migrated. 5 bare `[LibraryImport]`-only sites intentionally remain (no calling convention attribute needed). ~~Severity: Medium.~~

5. ~~**The gate/skip system uses 40+ independent `if` chains across 9+ files with no centralized policy.**~~ **Partially Resolved (A2).** `MemberGateEvaluator` centralizes shared type-resolution gates. PH + PCV fully unified (C1 resolved). `CanEmitMethod` delegates via evaluator early-out. MethodHandler, `CanEmitProperty`, and `ShouldSkipMethodEmission` keep inline checks to preserve gate ordering and constructor semantics (C2 reduced). ~~Severity: Critical.~~

---

## Prior Review Audit

| # | Original Finding | Status | Notes |
|---|-----------------|--------|-------|
| 1 | Four divergent type conversion pipelines | **Partially Resolved** | `TypeProjectionFactory` with 16 `ITypeProjection` implementations now handles the *happy path* (simple types, stdlib containers, existentials, closures, tuples). `GetIdiomaticCSharpType` is only referenced in 2 files (ClosureHandler, TypeProjectionFactory itself). However, the factory returns `null` for user-defined bound generics (line 204), so `BoundGenericsHandler.TranslateBoundGenericTypeToCSharp` remains a parallel path called from 8+ sites. ClosureHandler maintains 3 independent translation methods. The *number* of paths decreased from 4 to ~3, but the fundamental problem — "can you call one function to get the C# type for a Swift type?" — still has answer "no" for generics and closures. |
| 2 | Type information encoded in strings | **Resolved** | `MarshalledType` discriminated union (23 variants) replaced all string-encoded markers. `Parameter.Type` is now `MarshalledType` with pattern matching. No more `StartsWith("Existential:")` string parsing. This was a clean, complete fix. |
| 3 | Scattered bool marshalling | **Resolved** | `MarshalledType.BoolType` carries the `[MarshalAs(UnmanagedType.U1)]` intrinsically. `MarshallingHelpers.IsBoolType()` centralizes the check (both `string` and `TypeSpec` overloads). All call sites use the centralized helper — the last direct `== "bool"` in ProtocolExtensionClosureBridge.cs was fixed in A1.4. |
| 4 | Cross-cutting state through Conductor | **Partially Resolved** | The `Conductor` class itself is now a clean factory/dispatcher (137 lines). The ThreadStatic `s_activeCompositionCollector` pattern still exists — but now it's injected via `TypeHandlerContext.CompositionCollector` and set on `ExistentialHandler` via `SetCompositionCollector()`. The injection is still temporal (set during `Emit()`, not at construction), but the flow is explicit and traced. `NestedTypeRenames` still flows through mutable context. `PInvokeHelperContext` still flows through mutable property. The *worst* ThreadStatic coupling is gone, but the architecture still depends on "set state before calling, clear after." |
| 5 | Test architecture gap (no cross-path consistency tests) | **Moved** | The gap still exists. No cross-path consistency tests were added. Instead, the mitigation was making the factory the dominant path and adding golden-file tests for one library (SwiftBindingsTestLib). The 32/32 library validation serves as the de facto cross-path test, but it only catches compilation failures (CS0535/CS0738), not silent type disagreements that compile but produce wrong runtime behavior. |

---

## What's Good (Do Not Change)

1. **`MarshalledType` discriminated union.** Clean design, 23 variants, proper use of sealed records with pattern matching. `PublicTypeName` computed property is elegant. This was the previous review's #1 recommendation and it was done right.

2. **`ITypeProjection` / `TypeProjectionFactory` architecture.** The 16 projection implementations (BlittableProjection, StringProjection, ArrayProjection, OptionalProjection, etc.) are genuinely composable. `OptionalProjection(ArrayProjection(StringProjection()))` works correctly. The `MarshalPlan` / `GetParameterPlan` / `GetReturnPlan` API is well-designed — it separates "what type" from "how to marshal" cleanly.

3. **`ProtocolConformanceValidator`.** Despite my criticism of gate duplication, this class does a genuinely hard job well: validating that a concrete type can fully implement a protocol interface, with ancestor walking, accessor contract validation, TSelf substitution, and method-name collision detection. The logic is correct. The problem is that it's a *copy* of ProtocolHandler's logic rather than shared code.

4. **The validation infrastructure.** 32 real-world libraries, `validate-libraries.sh`, `.validation-baseline.json`, `scripts/fetch-libraries.sh` — this is a strong compile-gate testing system. Most binding generators don't have anything comparable.

5. **The reporting system** (`ReportCollector`, `ReportEmitter`). Every skipped type and member is recorded with a `SkipReason` enum and human-readable detail. This makes debugging "why didn't my method appear?" tractable.

6. **`PInvokeEmitter.ComputeEntryPoint`.** Single method that computes entry point symbol + wrapper-lib flag. Used by both direct emission and cross-module extensions. This is exactly the kind of shared primitive the codebase needs more of.

7. **MEMORY.md as living documentation.** The constraints list is unusually detailed and accurate. Every entry I checked against the code was correct. This is a genuine asset for onboarding.

---

## Critical Findings

### C1. Gate Logic Duplication Between ProtocolHandler and ProtocolConformanceValidator

- **Confidence:** Confirmed
- **Status:** **Resolved (A2).** `MemberGateEvaluator` created with `EvaluateProperty`, `EvaluateMethod`, `EvaluateSubscript` for protocol-context evaluation. Both `ProtocolHandler.Emit()` and `ProtocolConformanceValidator.IsPropertySkippedFromInterface()`/`IsMethodSkippedFromInterface()` now delegate to the evaluator. 10 private helper methods (7 from PH, 3 from PCV) removed. 31 unit tests added. Adding a new gate now requires editing one file.
- ~~**Location:**~~ `MemberGateEvaluator.cs` (new, ~360 lines)
- ~~**Impact:**~~ Eliminated. Single source of truth for skip decisions.

### C2. MemberEmissionValidator / ProtocolHandler / MethodHandler Gate Triplication

- **Confidence:** Confirmed
- **Status:** **Reduced (A2).** PH + PCV fully delegate to `MemberGateEvaluator` (C1 resolved). `MemberEmissionValidator.CanEmitMethod` delegates via `EvaluateHardGates()` early-out (safe — original had all three gates at top before special handling). MethodHandler, `CanEmitProperty`, and `ShouldSkipMethodEmission` keep inline checks to preserve semantics: MethodHandler never had unsupported-module gate (adding it changes constructor behavior); `CanEmitProperty`'s non-ISwiftObject must run after special handlers (AsyncStream/existential/closure could claim the type first); `ShouldSkipMethodEmission` only shares B19 (unsupported module).
- **Remaining:** MethodHandler's bare-generic + non-ISwiftObject checks are inline (could be unified if `EvaluateHardGates` supported an opt-out for the unsupported-module gate). MH-specific routing gates also remain inline: existential accumulate+bypass (routing to `ExistentialBypassEmitter`), unsatisfied generic constraints, protocol constraints, closure bridge routing (5+ specialized emitters). These have *routing* behavior (choosing between emitters), not just skip/emit — unifying them requires a different design (pluggable emitter strategies). Future session A2b/A4.
- ~~**Effort:**~~ Reduced to M (1 week) for remaining MH gates
- ~~**Migration Risk:**~~ Medium for remaining MH gates (touches the most complex emission paths)

---

## High-Priority Findings

### H1. Extension Emitter Triplication — RESOLVED (Session A3)

- **Confidence:** Confirmed
- **Resolution:** Shared `ExtensionMarshallingHelper` extracts `ReturnKind`/`ParamKind` enums, `ClassifyReturnType`/`ClassifyParameterType`, and `EmitReturnValueMarshalling` — eliminating duplicate enum definitions and marshalling switch blocks from `ForeignTypeExtensionEmitter` and `CrossModuleExtensionEmitter`. Swift wrapper accumulation uses `ModuleEmissionContext` typed dedup API (`ctx.TryAdd*Symbol` / `ctx.Add*WrapperLine`) directly in each emitter, providing dedup consistency without an intermediate abstraction layer.
- **Evidence:** Zero `private enum ReturnKind` / `private enum ParamKind` in source. Adding a new return type requires ONE edit in `ExtensionMarshallingHelper`.

### H2. ClosureHandler as Parallel Type System

- **Confidence:** Confirmed
- **Location:** `ClosureHandler.cs` (1,620 lines), specifically:
  - `TranslateTypeSpecToCSharp()` (line ~963)
  - `TranslateBoundGenericToCSharp()` (line ~1087)
  - `TranslateTypeSpecToPInvokeType()` (line ~1149)
  - `IsFrozenStruct()` / `IsNonFrozenStruct()` / `IsClassType()` / `IsSimpleEnum()` (lines ~1268-1382)
- **Problem:** ClosureHandler translates types for closure argument/return positions using its own resolution logic, not `TypeProjectionFactory`. The factory handles closures via `ClosureProjection`, but `ClosureProjection` internally delegates back to the factory for argument types. The *callback signature* generation (what the `[UnmanagedCallersOnly]` method looks like) requires raw P/Invoke types, and `ClosureHandler.TranslateTypeSpecToPInvokeType` produces those independently of the factory's `ITypeProjection.PInvokeType`.
- **Evidence:** `GetIdiomaticCSharpType` is referenced in only 2 files — ClosureHandler.cs and TypeProjectionFactory.cs. This means ClosureHandler is the *only* consumer of the legacy path that the factory was supposed to replace.
- **Impact:** Adding a new type category (e.g., frozen value struct support in closures) requires updating both `ClosureHandler.TranslateTypeSpecToCSharp` AND `TypeProjectionFactory`, with no guarantee they agree.
- **Effort:** L (2+ weeks to fully migrate ClosureHandler to use factory projections)
- **Migration Risk:** Medium (closure marshalling is the most complex code path)
- **Fix:** Incrementally replace `ClosureHandler.TranslateTypeSpecToCSharp` calls with `TypeProjectionFactory.Project()` calls, using `projection.PublicType` for delegate types and `projection.PInvokeType` for callback signatures. The factory already handles all the types ClosureHandler manually resolves. This should reduce ClosureHandler by ~400 lines.

### H3. Static Mutable State With Manual Reset — RESOLVED (Session A3)

- **Confidence:** Confirmed
- **Resolution:** All static mutable state replaced with `ModuleEmissionContext` — a per-module instance created in `Program.cs` and threaded through `EmitModule` → `TypeHandlerContext.EmissionContext` → all handler/emitter call sites. Each emitter's methods accept optional `ModuleEmissionContext? ctx = null` with `Default` fallback for backward compatibility. Typed dedup API (`HasEmitted*/TryAdd*` methods) replaces raw collection access. Zero `ResetForModule()` calls remain. Zero timing-sensitive reset comments remain.
- **Evidence:** `grep -r "ResetForModule" src/Swift.Bindings/src/` returns only a comment explaining the replacement. All 7+ emitters migrated: `ProtocolExtensionEmitter`, `ForeignTypeExtensionEmitter`, `Utf8SliceEmitter`, `CancellationTaskEmitter`, `ErrorDescriptionEmitter`, `GenericClosureBridgeEmitter`, `EnumHandler.RawRepresentable`.

### H4. TypeProjectionFactory Gaps Force Fallback to Legacy Paths

- **Confidence:** Confirmed
- **Location:** `TypeProjectionFactory.cs:204` — `if (namedType.GenericParameters.Count > 0) return null;`
- **Problem:** The factory explicitly bails on user-defined bound generics. The comment says "Deferred to 5B when proper public-vs-raw type distinction is implemented." This means callers must check: `factory.Project(typeSpec, ctx) ?? boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec)`. This two-phase lookup exists in:
  - `ProtocolConformanceValidator.GetInterfacePropertyType` (lines 272-289)
  - `ProtocolConformanceValidator.GetInterfaceMethodReturnType` (lines 305-335)
  - `ProtocolConformanceValidator.GetInterfaceSubscriptReturnType` (lines 392-411)
  - `WrapperEmitter.Marshalling` (via MethodMarshalPlanBuilder)
  - `MethodSignature.cs` (WrapperSignatureBuilder)
  - `ProtocolHandler.cs` (interface emission)
- **Evidence:** The pattern `factory.Project() ?? fallback` appears in every file that needs to handle bound generic user types. This is the factory failing to be a single entry point.
- **Impact:** Every new consumer of the factory must know about the BoundGenericsHandler fallback. The factory's promise of "one function to get the C# type" is broken for the most common real-world type patterns (any generic type).
- **Effort:** L (2+ weeks — requires solving the public-vs-raw type problem)
- **Migration Risk:** Medium
- **Fix:** Extend `ITypeProjection` with a `RawType` property (in addition to `PublicType`). `ArrayProjection.RawType` = `SwiftArray<T>`, `ArrayProjection.PublicType` = `IReadOnlyList<T>`. For user-defined generics, create a `BoundGenericProjection` that delegates to `BoundGenericsHandler` but wraps the result in the projection interface. This eliminates the null-return gap.

---

## Medium-Priority Findings

### M1. 19 Independent P/Invoke Emission Points

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.7).** `PInvokeEmitHelper` created with `PInvokeEmissionInfo` record, `EmitDeclaration(CSharpWriter)`, and `FormatDeclarationLines()`. All 38 explicit `[UnmanagedCallConv]` emission sites across 19 files migrated. 5 bare `[LibraryImport]`-only sites (no calling convention attribute) intentionally left as-is — they use `@_cdecl` wrappers or parameterless case constructors where no `[UnmanagedCallConv]` is needed. Existing `PInvokeDeclaration.Emit()` in `PInvokeHelperEmitter.cs` refactored to delegate to `PInvokeEmitHelper.EmitDeclaration()`.
- **Location:** `PInvokeEmitHelper.cs` (new), 18 migrated files, `PInvokeHelperEmitter.cs` (refactored)
- **Impact:** Format changes to P/Invoke attributes now require editing one file.

### M2. Duplicate `ReturnKind` / `ParamKind` Enums

- **Confidence:** Confirmed
- **Status:** **Resolved (A3).** Enums moved to shared `ExtensionMarshallingHelper`. Zero `private enum ReturnKind` / `private enum ParamKind` remain in source.

### M3. `ProtocolConformanceValidator` Creates New `BoundGenericsHandler` Per Method

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.5).** Existing instance at line 97 now passed as parameter to `GetInterfacePropertyType`, `GetInterfaceMethodReturnType`, `GetInterfaceSubscriptReturnType`, and `HasBareGenericInMethodSignature`. 6 redundant `new BoundGenericsHandler(_typeDatabase)` allocations removed.

### M4. ClosureEmitter Has Its Own `IsBoolType(TypeSpec)` Method

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.3).** `IsBoolType(TypeSpec)` overload added to `MarshallingHelpers`. Local version removed from `ClosureEmitter.cs`. 12 call sites updated across `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`.

### M5. HashSet<string> Dedup Proliferation

- **Confidence:** Confirmed
- **Status:** **Resolved (A3).** `ModuleEmissionContext` centralizes dedup sets with typed API (`HasEmitted*/TryAdd*` methods). Static HashSet fields removed from 7+ emitters. Method-scoped sets for per-type/per-method dedup remain (appropriate — they don't need module-level lifetime).

### M6. Program.cs Orchestration Complexity

- **Confidence:** Confirmed
- **Location:** `Program.cs` (1,368 lines)
- **Problem:** `Program.cs` handles: CLI parsing (lines 1-150), input resolution (150-400), type database construction (400-650), swiftinterface parsing (650-750), dependency resolution (750-850), protocol extension injection (850-950), module emission orchestration (950-1200), wrapper compilation (1200-1368). This is too much for one file. In particular, the protocol extension injection timing (`ProtocolExtensionEmitter.InjectExtensionMethods` must happen AFTER `typeDatabase.AddModuleDatabase()` and BEFORE `stringEmitter.EmitModule()`) is encoded as ordering constraints between function calls, not as pipeline stages.
- **Impact:** Difficult to test individual pipeline stages. Difficult to add new pre-emission or post-emission stages.
- **Effort:** M (1 week)
- **Migration Risk:** Low
- **Fix:** Extract into pipeline stages: `InputResolver` → `TypeDatabaseBuilder` → `PreEmissionTransforms` (swiftinterface, protocol extensions, foreign extensions) → `Emitter` → `PostEmissionSteps` (wrapper compilation, project emission). Each stage is a class with clear inputs/outputs. `Program.cs` becomes a 50-line pipeline orchestrator.

---

## Low-Priority Findings

### L1. Single Direct `== "bool"` in ProtocolExtensionClosureBridge

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.4).** Replaced with `MarshallingHelpers.IsBoolType(csharpType)`.

### L2. ClosureHandler Creates ExistentialHandler Internally

- **Confidence:** Confirmed
- **Location:** `ClosureHandler.cs` — creates `ExistentialHandler` for existential-in-closure type checks.
- **Problem:** Should accept ExistentialHandler as a dependency rather than creating it. This means ClosureHandler existential resolution may use a different composition collector than the main pipeline's ExistentialHandler.
- **Effort:** S (1 day)
- **Migration Risk:** Low
- **Fix:** Pass ExistentialHandler as a constructor parameter.

### L3. `CrossModuleExtensionEmitter.TypeAliasToCSPrimitive` Delegates to ForeignTypeExtensionEmitter

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.2).** Dictionary moved to `MarshallingHelpers.TypeAliasToCSPrimitive`. Alias field removed from `CrossModuleExtensionEmitter`. Original field removed from `ForeignTypeExtensionEmitter`.

### L4. `IsSwiftPrimitive` in ProtocolExtensionEmitter Is Used by Other Emitters

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.1).** Method moved to `MarshallingHelpers.IsSwiftPrimitive()`. Original removed from `ProtocolExtensionEmitter`. 25+ call sites updated across `ProtocolExtensionEmitter`, `ForeignTypeExtensionEmitter`, `CrossModuleExtensionEmitter`, `ProtocolExtensionClosureBridge`.

### L5. `ProtocolHandler` Creates `ClosureHandler` Per Property

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.6).** Hoisted `ClosureHandler` creation before loops. Passed as parameter to `EmitInterfaceProperty`, `EmitInterfaceSubscript`, `EmitInterfaceMethod`. 5 redundant allocations removed.

---

## Redesign Proposal

If I were redesigning the internal architecture from scratch with the same inputs/outputs:

### Pipeline Stages

```
1. Parse           → ModuleDecl tree (ABI JSON + swiftinterface)
2. TypeDatabase    → Type records, cross-module resolution
3. Plan            → MemberPlan for every emittable member
4. Emit C#         → MemberPlan → string (pure function)
5. Emit Swift      → MemberPlan → string (pure function)
6. Post-Process    → Compilation, project emission, reporting
```

### Stage 3 (Plan) Is The Key

The current architecture interleaves decision-making with string emission. The single most impactful change would be to separate them. `MemberPlan` would capture:

```csharp
record MemberPlan
{
    MemberKind Kind;               // Method, Property, Constructor, Operator, ...
    string PublicSignature;         // Full C# signature for the public API
    string PInvokeSignature;       // Full P/Invoke declaration
    List<MarshalStep> ParameterMarshal;  // Per-param: setup → expression → cleanup
    MarshalStep ReturnMarshal;     // Return: receive → convert → cleanup
    string? SwiftWrapperCode;      // Complete Swift wrapper if needed
    GateResult EmissionDecision;   // Emit / Skip(reason) / EmitInInterfaceOnly
}
```

This makes gates testable (assert the decision without emitting), makes emission testable (assert the string from a known plan), and makes consistency checkable (assert that ProtocolHandler's plan and MethodHandler's plan for the same member agree).

### Extension Emitter Problem

A single `ExtensionMethodEmitter` with a strategy pattern:

```csharp
interface IExtensionStrategy
{
    IEnumerable<MemberDecl> GetMembers(TypeDecl type, ModuleDecl module);
    string GetExtensionClassName(TypeDecl type, ModuleDecl module);
    SwiftWrapperInfo? GetSwiftWrapper(MemberDecl member);
    string GetSelfExpression(string selfParam);
}
```

Three implementations: `ProtocolExtensionStrategy`, `ForeignTypeStrategy`, `CrossModuleStrategy`. The base emitter handles classification, marshalling, P/Invoke emission, and dedup.

### Gate/Skip Problem

A single `MemberGateEvaluator`:

```csharp
enum GateResult { Emit, Skip, EmitInInterfaceOnly, EmitWithStub }

GateResult Evaluate(MemberDecl member, TypeDecl parentType, EmissionContext context);
```

Called once per member. Result is stored on the `MemberPlan`. No more scattered `if` chains.

### String Emission vs Roslyn

Keep string emission. The current `CSharpWriter` / `IndentedTextWriter` pattern works well enough, and Roslyn SyntaxTree construction is ~3x more verbose with no runtime benefit (we're generating files, not compiling). The real problem isn't "we emit strings" — it's "we make decisions while emitting strings." Fix the decision layer, keep the emission layer.

### Test Structure

1. **Plan tests:** For a corpus of real-world method declarations, assert the `MemberPlan` is correct.
2. **Emission tests:** For known `MemberPlan` values, assert the emitted string matches golden output.
3. **Consistency tests:** For every protocol-conforming type, assert that the interface plan and implementation plan agree on types and signatures.
4. **Golden-file tests:** Full output for all 32 validation libraries, diffed on every change.

---

## Recommended Action Plan

Ordered by impact/effort ratio. Each is independently valuable.

| # | What | Files | Effort | Risk | Depends On | Expected Benefit |
|---|------|-------|--------|------|------------|-----------------|
| 1 | Extract `MemberGateEvaluator` from ProtocolHandler + ProtocolConformanceValidator + MemberEmissionValidator | 4-6 files | M | Low | — | ~~Eliminates C1+C2.~~ **Done (A2).** C1 resolved, C2 reduced. |
| 2 | Create `ExtensionMarshallingHelper` with shared classification + marshalling | 3 emitter files + 1 new helper | L | Low | — | ~~Eliminates H1.~~ **Done (A3).** Shared enums + classify + marshalling. |
| 3 | Create `PInvokeEmitHelper` for shared P/Invoke declaration emission | 19 files (mechanical) | S | Low | — | ~~Eliminates M1.~~ **Done (A1.7).** |
| 4 | Move `IsSwiftPrimitive` + `TypeAliasToCSPrimitive` to shared utility | 4 files | S | Low | — | ~~Eliminates L3+L4.~~ **Done (A1.1, A1.2).** |
| 5 | Replace static emitter state with `ModuleEmissionContext` | 7 emitter files + ModuleHandler | M | Low | — | ~~Eliminates H3.~~ **Done (A3).** Per-module context with typed dedup API. |
| 6 | Migrate ClosureHandler type resolution to use TypeProjectionFactory | ClosureHandler.cs | L | Med | — | Reduces H2. ~400 lines removed from ClosureHandler. |
| 7 | Add `BoundGenericProjection` to TypeProjectionFactory | TypeProjectionFactory + BoundGenericsHandler | L | Med | — | Eliminates H4. Factory becomes true single entry point. |
| 8 | Extract Program.cs pipeline stages | Program.cs → 5 new classes | M | Low | — | Eliminates M6. Testable pipeline. |
| 9 | Add cross-path consistency tests | New test file | M | Low | #1, #7 | Catches gate/type disagreements before they reach libraries. |
| 10 | Add golden-file tests for all 32 validation libraries | New test infrastructure | M | Low | — | Catches any output change across generator modifications. |
| 11 | Replace `ProtocolExtensionClosureBridge == "bool"` check | 1 line | S | None | — | ~~Consistency fix.~~ **Done (A1.4).** |
| 12 | Fix ClosureEmitter local IsBoolType | ClosureEmitter.cs | S | None | — | ~~Eliminates M4.~~ **Done (A1.3).** |
| 13 | Hoist BoundGenericsHandler in ProtocolConformanceValidator | 1 file | S | None | — | ~~Eliminates M3.~~ **Done (A1.5).** |

**Total estimated effort for items #1-5:** ~4 weeks. ~~These alone would eliminate the two critical findings and three high-priority findings.~~ **All done (A1, A2, A3).** C1 resolved, C2 reduced, H1 resolved, H3 resolved, M1-M5 resolved, L1-L5 resolved.

### Deferred Findings

The following findings are real architectural debt but carry Medium migration risk relative to their benefit. Revisit after the foundation is cleaner.

- **H2 (ClosureHandler parallel type system):** L effort, Medium risk. The 1,620-line ClosureHandler works correctly — touching closure marshalling internals is where subtle runtime bugs hide.
- **H4 (TypeProjectionFactory bound generic gap):** L effort, Medium risk. The `?? fallback` pattern is ugly but stable across all 32 libraries.
- **M6 (Program.cs extraction):** M effort. Nice-to-have for testability but won't prevent any bugs.
- **#9, #10 (consistency tests, full golden files):** Infrastructure that pays off long-term but doesn't fix anything today.

---

## Session Plan

Three sessions, ordered by risk (low-risk mechanical work first, critical refactoring second, large structural extraction third). Each session ends with full 32-library validation.

### Session A1: Quick Wins + PInvokeEmitHelper
- **Status:** Complete (February 26, 2026)
- **Effort:** S (1 day)
- **Findings addressed:** #3, #4, #11, #12, #13, L3, L4, L5
- **Risk:** Low — mechanical moves, no behavioral changes

**Tasks:**
- [x] A1.1: Move `IsSwiftPrimitive()` from `ProtocolExtensionEmitter` to `MarshallingHelpers`. Updated 25+ call sites across `ProtocolExtensionEmitter`, `ForeignTypeExtensionEmitter`, `CrossModuleExtensionEmitter`, `ProtocolExtensionClosureBridge`. (L4)
- [x] A1.2: Move `TypeAliasToCSPrimitive` dictionary from `ForeignTypeExtensionEmitter` to `MarshallingHelpers`. Removed alias field from `CrossModuleExtensionEmitter`. (L3)
- [x] A1.3: Added `IsBoolType(TypeSpec)` overload to `MarshallingHelpers`. Removed local version from `ClosureEmitter.cs`. Updated 12 call sites across `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`. (#12 / M4)
- [x] A1.4: Replaced `csharpType == "bool"` with `MarshallingHelpers.IsBoolType(csharpType)` in `ProtocolExtensionClosureBridge.cs`. (#11 / L1)
- [x] A1.5: Hoisted `BoundGenericsHandler` in `ProtocolConformanceValidator` — created once at top, passed as parameter to 4 helper methods. Removed 6 redundant allocations. (#13 / M3)
- [x] A1.6: Hoisted `ClosureHandler` in `ProtocolHandler` — created once before loops, passed as parameter to 3 `EmitInterface*` methods. Removed 5 redundant allocations. (L5)
- [x] A1.7: Created `PInvokeEmitHelper` with `PInvokeEmissionInfo` record, `EmitDeclaration(CSharpWriter)`, and `FormatDeclarationLines()`. All 38 planned P/Invoke emission sites (the explicit `[UnmanagedCallConv]` sites) were migrated across 19 files. 5 bare `[LibraryImport]`-only sites remain and were intentionally out of scope (no calling convention attribute needed). (#3 / M1)
- [x] A1.8: Validation — 4303 unit tests (0 fail), 700 integration tests (0 fail), 32/32 library validation, golden files pass.

### Session A2: MemberGateEvaluator
- **Status:** Complete (February 26, 2026)
- **Effort:** M (1 day)
- **Findings addressed:** C1 (resolved), C2 (reduced)
- **Risk:** Low migration risk, high impact

**Context:** Four systems independently decided whether a method/property should be emitted: `ProtocolHandler.Emit()` (inline gates P1-P7, M1-M11, S1-S5), `ProtocolConformanceValidator.IsMethodSkippedFromInterface()` / `IsPropertySkippedFromInterface()` (mirrored copies with 9 private helpers), `MethodHandler.Emit()` (bare generic, non-ISwiftObject inline checks), and `MemberEmissionValidator` (B19 unsupported module, bare generic, non-ISwiftObject). When they diverged, CS0535 or silent binding quality loss resulted.

**Result:** Created `MemberGateEvaluator` with `GateResult` (Emit/InterfaceOnly/Skip) and two evaluation modes: full protocol-context evaluation (soft gates for closures/existentials → InterfaceOnly) and hard-gate-only evaluation (concrete context → Skip or Emit only). ProtocolHandler and ProtocolConformanceValidator fully delegate to the evaluator (C1 resolved). `MemberEmissionValidator.CanEmitMethod` delegates via `EvaluateHardGates` early-out. MethodHandler, `CanEmitProperty`, and `ShouldSkipMethodEmission` keep their original inline checks to preserve gate ordering and constructor semantics.

**Tasks:**
- [x] A2.1: Created `MemberGateEvaluator.cs` with `GateDisposition` enum, `SoftGateFlags` flags, `GateResult` class, and evaluator with `EvaluateProperty`, `EvaluateMethod`, `EvaluateSubscript` (protocol context), `EvaluateHardGates`, `EvaluatePropertyHardGates` (concrete context). Static utility `ContainsAnyTypeGenericArg`. 31 unit tests in `MemberGateEvaluatorTests.cs`.
- [x] A2.2: Migrated `ProtocolConformanceValidator` — `IsPropertySkippedFromInterface` and `IsMethodSkippedFromInterface` delegate to evaluator. Removed 3 duplicated private helpers.
- [x] A2.3: Migrated `ProtocolHandler.Emit()` — replaced inline gates P3-P7, M5-M10, S3-S5 with evaluator calls. InterfaceOnly populates tracking sets (`closureSkippedMethodKeys`, `existentialSkippedMethodKeys`) via `SoftGateFlags`. Removed 7 private helpers.
- [x] A2.4: MethodHandler — bare-generic and non-ISwiftObject checks remain inline (not delegated to evaluator) because `EvaluateHardGates` includes an unsupported-module gate that MethodHandler never had, and adding it would change constructor semantics (`ShouldSkipMethodEmission` skips B19 for constructors). MethodHandler's gates are MH-specific by nature.
- [x] A2.5: Wired `MemberEmissionValidator.CanEmitMethod()` — added early-out `EvaluateHardGates()` (safe because original code had B19 + bare-generic + non-ISwiftObject all at top before special handling). `CanEmitProperty` and `ShouldSkipMethodEmission` keep their original gate ordering to avoid changing semantics (non-ISwiftObject must run after special handlers in properties; B19 is the only shared gate in `ShouldSkipMethodEmission`). Kept emission-specific gates (B18, B20 with carve-outs, AsyncStream, tuple, etc.) in MEV.
- [x] A2.6: Validation — 4334 unit tests (0 fail, 31 new), 700 integration tests (0 fail), 32/32 library validation, golden files pass.

**C1 status: Resolved.** ProtocolHandler and ProtocolConformanceValidator now use the single evaluator. No more mirrored copies with "Mirrors the skipping logic" comments.

**C2 status: Reduced.** PH + PCV fully unified through the evaluator. `CanEmitMethod` delegates three shared hard gates (bare generic, non-ISwiftObject, unsupported module) via `EvaluateHardGates` early-out. MethodHandler, `CanEmitProperty`, and `ShouldSkipMethodEmission` keep inline checks — MethodHandler because `EvaluateHardGates` includes an unsupported-module gate it never had (constructor semantics would change); `CanEmitProperty` because non-ISwiftObject must run after special handlers (AsyncStream/existential/closure); `ShouldSkipMethodEmission` because it only shares B19 (unsupported module). MethodHandler's MH-specific gates (existential accumulate+bypass, unsatisfied constraints, protocol constraints, closure bridge routing) remain inline — these have routing behavior (choosing between 5+ specialized emitters). Full MH unification is a different-risk refactor for a future session.

### Session A3: ExtensionEmitterBase + ModuleEmissionContext
- **Status:** Complete
- **Effort:** L (3-5 days)
- **Findings addressed:** H1 (resolved), H3 (resolved)
- **Risk:** Low migration risk — extract base, delegate, validate

**Summary:** Extracted shared marshalling logic into `ExtensionMarshallingHelper` (shared `ReturnKind`/`ParamKind` enums, classify methods, return marshalling). Created `ModuleEmissionContext` — a per-module instance with typed dedup API — replacing all static mutable state and `ResetForModule()` calls across 7+ emitters. Context is threaded from `Program.cs` → `EmitModule` → `TypeHandlerContext` → all handler/emitter call sites. Swift wrapper accumulation uses `ModuleEmissionContext` typed methods directly (no intermediate abstraction needed).

**Tasks completed:**
- [x] A3.1: Extract shared `ReturnKind`/`ParamKind` enums and `ExtensionMarshallingHelper` (classify + marshalling)
- [x] A3.2: Swift wrapper dedup via `ModuleEmissionContext` typed API (`ctx.TryAdd*Symbol` / `ctx.Add*WrapperLine`)
- [x] A3.3: Create `ModuleEmissionContext` with typed dedup API for all emitter categories
- [x] A3.4: Thread context through `Program.cs` → `IEmitter.EmitModule` → `TypeHandlerContext` → extension emitters
- [x] A3.5: Migrate infrastructure emitters (`Utf8SliceEmitter`, `CancellationTaskEmitter`, `ErrorDescriptionEmitter`, `GenericClosureBridgeEmitter`, `EnumHandler.RawRepresentable`) to `ModuleEmissionContext`. Thread context through `WrapperEmitter`, `WitnessDispatchEmitter`, `DefaultParameterOverloadEmitter`, `ArraySliceNormalizationEmitter`.
- [x] A3.6: Verify zero `ResetForModule` calls, zero duplicate enums. All tests + validation pass.

---

## Not Reviewed

The following subsystems were not examined deeply enough to make findings:

1. **SwiftUI bridge system** (`SwiftUIBridgeEmitter.cs`, `SwiftUIBridgeEmitter.AsyncPattern.cs`, `SwiftUIBridgeEmitter.InitAnalyzer.cs`, `ThemeBridgeEmitter.cs`, `BridgeHints.cs`) — 3,200+ lines total. Not reviewed because it's a specialized subsystem for SwiftUI view bridging and is outside the core binding pipeline.

2. **Demangler** (`Swift5Demangler.cs` at 3,195 lines, `Swift5Reducer.cs` at 1,018 lines) — Ported from Swift's own demangler. Not reviewed as it's essentially third-party code.

3. **Parser layer** (`SwiftABIParser.cs` at 1,683 lines, `SwiftInterfaceAccessParser.cs` at 2,030 lines) — Briefly examined structure but not deeply reviewed for correctness or duplication.

4. **Test files** — Sampled ~10 test files to understand patterns but did not audit coverage comprehensively. The test-to-code ratio appears healthy (~34K LOC tests / ~72K LOC generator = 0.47). The golden-file infrastructure exists for 1 library but not the full 32.

5. **MSBuild SDK** (`Sdk.props`, `Sdk.targets`, `build-sdk.sh`) — Build system infrastructure, not code generation.

6. **Runtime library thread safety** — The `ExistentialContainer` family creates containers that may be passed across threads. `TypeMetadataCache` uses a simple dictionary without concurrent access protection (but appears to be populated once at startup). Did not deeply audit thread safety.

7. **OperatorHandler.cs** — Skimmed but did not audit the operator pairing logic or P/Invoke emission in detail.

8. **SubscriptHandler** — Not reviewed at all; assumed to follow PropertyHandler patterns.
