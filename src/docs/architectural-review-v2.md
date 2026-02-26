# Architectural Review 2 — February 25, 2026

## Executive Summary

1. **The three extension emitters (ProtocolExtension, ForeignType, CrossModule) share ~60% structural overlap but no common base.** Each independently reimplements type classification (`ClassifyReturnType`/`ClassifyParameterType`), P/Invoke emission, Swift wrapper accumulation, and return-value marshalling. This is the largest source of unnecessary duplication in the codebase and will cause bugs every time a new type category is added. **Severity: High.**

2. **ClosureHandler (1,620 LOC) is a parallel type system that bypasses TypeProjectionFactory entirely.** It contains its own `TranslateTypeSpecToCSharp`, `TranslateBoundGenericToCSharp`, and `TranslateTypeSpecToPInvokeType` — reimplementing resolution logic the factory was designed to own. The factory explicitly returns `null` for user-defined bound generics (line 204), so callers must still fall back to BoundGenericsHandler directly. The rework unified the *happy path* but left the hard cases fragmented. **Severity: High.**

3. **Static mutable state is scattered across 7+ emitters, all using the same `_structEmitted` / `_swiftWrapperLines` / `_emittedSymbols` pattern with manual `ResetForModule()` calls.** Missing a reset produces silent cross-module contamination. This is a class of bug waiting to happen — in fact, `ProtocolExtensionEmitter.ResetForModule()` was specifically called out in MEMORY.md for needing careful placement. **Severity: High.**

4. ~~**19 separate files emit P/Invoke declarations independently**~~ **Resolved (A1.7).** `PInvokeEmitHelper` now centralizes P/Invoke declaration emission. All 38 explicit `[UnmanagedCallConv]` sites across 19 files migrated. 5 bare `[LibraryImport]`-only sites intentionally remain (no calling convention attribute needed). ~~Severity: Medium.~~

5. **The gate/skip system uses 40+ independent `if` chains across 9+ files with no centralized policy.** `ProtocolConformanceValidator.IsMethodSkippedFromInterface` must mirror `ProtocolHandler`'s skip logic exactly — and the code literally says "mirrors ProtocolHandler" in comments. When they diverge (which they will), CS0535 errors appear in real libraries with no unit test to catch it. **Severity: Critical.**

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
- **Location:** `ProtocolHandler.cs:100-162` (property gates), `ProtocolConformanceValidator.cs:521-581` (`IsPropertySkippedFromInterface`, `IsMethodSkippedFromInterface`)
- **Problem:** `ProtocolConformanceValidator` contains comments like "Mirrors the skipping logic in ProtocolHandler.Emit for properties" (line 519) and "Mirrors the skipping logic in ProtocolHandler.Emit for methods" (line 553). This is not an abstraction — it's a synchronized copy. When `ProtocolHandler` adds a new gate (and it has, repeatedly, across sessions), `ProtocolConformanceValidator` must be updated in lockstep. If they diverge:
  - Validator says "yes, can implement" → type declares conformance → CS0535 at compile time
  - Validator says "no, skip" → type omits conformance it could have implemented → reduced binding quality
- **Evidence:** The property gates in ProtocolHandler check: (1) static, (2) bare generic, (3) AnyType generic arg, (4) unsupported module, (5) non-ISwiftObject bound generic, (6) closure (fall-through). `IsPropertySkippedFromInterface` checks the same 5 conditions (skipping static since it's pre-filtered). The method gates check: (1) non-ISwiftObject bound generic, (2) bare generic, (3) AnyType generic arg, (4) unsupported module. `IsMethodSkippedFromInterface` checks the same 4. But the *order* differs, the *exact logic* differs slightly (validator creates a new `BoundGenericsHandler` per call; handler uses one from the environment), and there's no test that enforces they agree.
- **Impact:** Will cause CS0535 errors in real libraries when a new gate is added to one but not the other. This has likely already happened during development and been caught only by full validation.
- **Effort:** M (2-3 days)
- **Migration Risk:** Low
- **Fix:** Extract a `MemberGateEvaluator` class that both ProtocolHandler and ProtocolConformanceValidator call. The evaluator takes a member + type database + protocol context and returns a `GateResult` (Emit / Skip / SkipButInInterface). ProtocolHandler uses it to decide emission; ProtocolConformanceValidator uses it to decide conformance. One source of truth, one set of tests.

### C2. MemberEmissionValidator / ProtocolHandler / MethodHandler Gate Triplication

- **Confidence:** Confirmed
- **Location:** `MemberEmissionValidator.cs` (1,056 lines), `ProtocolHandler.cs:200-500` (method gates), `MethodHandler.cs:200-500` (method gates)
- **Problem:** Three systems independently decide whether a method can be emitted:
  1. `MemberEmissionValidator.CanEmitMethod()` — used by `ProtocolConformanceValidator` and `DefaultParameterOverloadEmitter`
  2. `ProtocolHandler.Emit()` — inline gates during interface emission
  3. `MethodHandler.Emit()` — inline gates during method emission

  Each has its own combination of: existential detection, closure detection, bound-generic validation, unsupported-module filtering, bare-generic checking, AnyType fallback detection. When `MemberEmissionValidator` says a method can be emitted but `MethodHandler` actually skips it, the conformance validator produces a false positive → CS0535.
- **Evidence:** `MemberEmissionValidator.CanEmitMethod` has 20 gates (B1-B20) enumerated in comments. `MethodHandler.Emit` has its own gate chain with a different structure (accumulate-then-decide for existentials, early-return for others). The gate labeled B20 in `MemberEmissionValidator` has a special carve-out for `IsProtocolExtensionMethod && IsClosureBridgeable` — this kind of conditional logic is exactly what diverges.
- **Impact:** Every new gate added to any of the three systems must be mirrored in the other two. This is the definition of fragile architecture.
- **Effort:** L (1-2 weeks to unify)
- **Migration Risk:** Medium
- **Fix:** Same as C1 — a single `MemberGateEvaluator` that returns structured decisions. All three consumers call it. This is the single highest-impact refactoring in the codebase.

---

## High-Priority Findings

### H1. Extension Emitter Triplication

- **Confidence:** Confirmed
- **Location:** `ProtocolExtensionEmitter.cs` (1,773 lines), `ForeignTypeExtensionEmitter.cs` (1,532 lines), `CrossModuleExtensionEmitter.cs` (792 lines)
- **Problem:** All three emitters independently implement:
  - **Type classification:** `ClassifyReturnType` / `ClassifyParameterType` with local `ReturnKind` / `ParamKind` enums — `ForeignTypeExtensionEmitter` and `CrossModuleExtensionEmitter` define *identical* `ReturnKind` enums (Void/Primitive/ObjCClass/SwiftClass/NonFrozenStruct). Cross-emitter dependencies on `IsSwiftPrimitive()` and `TypeAliasToCSPrimitive` were resolved in A1 (moved to `MarshallingHelpers`), but the duplicate enum definitions remain.
  - **Return value marshalling:** The `switch (returnCategory)` blocks for Void/Primitive/ObjCClass/SwiftClass/NonFrozenStruct are copy-pasted with minor variations across all three files.
  - **P/Invoke emission:** Each builds `[UnmanagedCallConv]` + `[LibraryImport]` strings independently, with separate bool return-type checks.
  - **Swift wrapper accumulation:** `ProtocolExtensionEmitter` and `ForeignTypeExtensionEmitter` each maintain static `_swiftWrapperLines` lists with identical accumulate/flush patterns.
  - **Static state management:** `_emittedSymbols` HashSet, `_emittedCount` counter, `ResetForModule()` method — all three follow the same pattern.
- **Evidence:** `CrossModuleExtensionEmitter.ClassifyReturnType` (line 595) is structurally identical to `ForeignTypeExtensionEmitter.ClassifyReturnType` (line 926). The `EmitMethodBody` switch blocks in `CrossModuleExtensionEmitter` (lines 260-315) match the same patterns in `ForeignTypeExtensionEmitter` (lines 647-720).
- **Impact:** Adding support for a new return type (e.g., frozen value structs, which all three currently skip) requires changes in 3 files × 3 locations each = 9 coordinated edits.
- **Effort:** L (1-2 weeks)
- **Migration Risk:** Low (extract base, delegate, run validation)
- **Fix:** Create `ExtensionEmitterBase` with:
  - Shared `ReturnKind` / `ParamKind` enums
  - Shared `ClassifyReturnType` / `ClassifyParameterType` methods
  - Shared `EmitReturnMarshalling(CSharpWriter, ReturnKind, string nativeCall, string csharpType)` method
  - Shared `EmitPInvokeDeclaration(CSharpWriter, string libPath, string entryPoint, string returnType, List<string> params, bool returnIsBool)` method
  - Shared `SwiftWrapperAccumulator` class (replaces static `_swiftWrapperLines` / `_emittedSymbols` / `ResetForModule()`)

  Each concrete emitter overrides only what's unique: how members are discovered, how self is marshalled, whether Swift wrappers are needed.

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

### H3. Static Mutable State With Manual Reset

- **Confidence:** Confirmed
- **Location:** 7+ emitters:
  - `ProtocolExtensionEmitter`: `_swiftWrapperLines`, `_emittedSymbols`, `_injectedCount`
  - `ForeignTypeExtensionEmitter`: `_swiftWrapperLines`, `_emittedSymbols`, `_emittedCount`, `_extensionClasses`, `_neededImports`
  - `Utf8SliceEmitter`: `_structEmitted`, `_freeEmitted`, `_csharpTypesWithFreePInvoke`
  - `CancellationTaskEmitter`: `_infrastructureEmitted`, `_csharpTypesWithCancelPInvoke`
  - `ErrorDescriptionEmitter`: `_infrastructureEmitted`, `_csharpTypesWithErrorPInvoke`, `_typedErrorExtractorsEmitted`
  - `GenericClosureBridgeEmitter`: `_createErrorEmitted`, `_createErrorPInvokeEmittedTypes`
- **Problem:** All use the pattern: static field → accumulate during emission → manual `ResetForModule()` call from `ModuleHandler` or `Program.cs`. If a new emitter is added and its reset is forgotten, or if module processing order changes, state leaks between modules.
- **Evidence:** `ProtocolExtensionEmitter.ResetForModule()` has a comment: "Called from Program.cs before the conditional inject block — NOT from ModuleHandler.Emit() (which would wipe state populated by InjectExtensionMethods before EmitSwiftWrappers reads it)." This is a timing constraint encoded in a comment, not in the type system.
- **Impact:** Subtle cross-module contamination bugs that only appear with specific library combinations.
- **Effort:** M (1 week)
- **Migration Risk:** Low
- **Fix:** Replace static state with a `ModuleEmissionContext` instance created per module and passed through the emission pipeline. Each emitter receives its scratch space from this context. When the module is done, the context is dropped. No manual reset needed.

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
- **Location:** `CrossModuleExtensionEmitter.cs:578-593`, `ForeignTypeExtensionEmitter.cs:75` (approximate)
- **Problem:** Two identical `ReturnKind` enums and separate `ParamKind` enums. Neither is shared.
- **Impact:** Low on its own, but this is a symptom of H1 (extension emitter triplication).
- **Effort:** S (1 day, part of H1 fix)
- **Migration Risk:** Low
- **Fix:** Move to shared `ExtensionEmitterTypes.cs` or into the base class proposed in H1.

### M3. `ProtocolConformanceValidator` Creates New `BoundGenericsHandler` Per Method

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.5).** Existing instance at line 97 now passed as parameter to `GetInterfacePropertyType`, `GetInterfaceMethodReturnType`, `GetInterfaceSubscriptReturnType`, and `HasBareGenericInMethodSignature`. 6 redundant `new BoundGenericsHandler(_typeDatabase)` allocations removed.

### M4. ClosureEmitter Has Its Own `IsBoolType(TypeSpec)` Method

- **Confidence:** Confirmed
- **Status:** **Resolved (A1.3).** `IsBoolType(TypeSpec)` overload added to `MarshallingHelpers`. Local version removed from `ClosureEmitter.cs`. 12 call sites updated across `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`.

### M5. HashSet<string> Dedup Proliferation

- **Confidence:** Confirmed
- **Location:** 35+ static HashSet fields, 14+ method-scoped HashSet variables across the codebase.
- **Problem:** Deduplication is implemented ad-hoc at each emission point. Some track Swift mangled names, some track C# method signatures, some track projected types. There's no unified "have I emitted this?" mechanism.
- **Impact:** Each new emission point must create its own dedup set, with its own key format, and clear it at the right time. Easy to get wrong.
- **Effort:** M (absorbed into H3 fix — ModuleEmissionContext would own these sets)
- **Migration Risk:** Low
- **Fix:** Part of H3 — centralize dedup sets into `ModuleEmissionContext`. Provide typed methods like `context.HasEmittedPInvoke(symbol)`, `context.HasEmittedSwiftWrapper(symbol)` instead of raw HashSet access.

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
| 1 | Extract `MemberGateEvaluator` from ProtocolHandler + ProtocolConformanceValidator + MemberEmissionValidator | 4-6 files | M | Low | — | Eliminates C1+C2. Single source of truth for skip decisions. |
| 2 | Create `ExtensionEmitterBase` with shared classification, marshalling, P/Invoke | 3 emitter files + 1 new base | L | Low | — | Eliminates H1. ~1,500 lines of dedup removed. |
| 3 | Create `PInvokeEmitHelper` for shared P/Invoke declaration emission | 19 files (mechanical) | S | Low | — | ~~Eliminates M1.~~ **Done (A1.7).** |
| 4 | Move `IsSwiftPrimitive` + `TypeAliasToCSPrimitive` to shared utility | 4 files | S | Low | — | ~~Eliminates L3+L4.~~ **Done (A1.1, A1.2).** |
| 5 | Replace static emitter state with `ModuleEmissionContext` | 7 emitter files + ModuleHandler | M | Low | — | Eliminates H3. No manual ResetForModule. |
| 6 | Migrate ClosureHandler type resolution to use TypeProjectionFactory | ClosureHandler.cs | L | Med | — | Reduces H2. ~400 lines removed from ClosureHandler. |
| 7 | Add `BoundGenericProjection` to TypeProjectionFactory | TypeProjectionFactory + BoundGenericsHandler | L | Med | — | Eliminates H4. Factory becomes true single entry point. |
| 8 | Extract Program.cs pipeline stages | Program.cs → 5 new classes | M | Low | — | Eliminates M6. Testable pipeline. |
| 9 | Add cross-path consistency tests | New test file | M | Low | #1, #7 | Catches gate/type disagreements before they reach libraries. |
| 10 | Add golden-file tests for all 32 validation libraries | New test infrastructure | M | Low | — | Catches any output change across generator modifications. |
| 11 | Replace `ProtocolExtensionClosureBridge == "bool"` check | 1 line | S | None | — | ~~Consistency fix.~~ **Done (A1.4).** |
| 12 | Fix ClosureEmitter local IsBoolType | ClosureEmitter.cs | S | None | — | ~~Eliminates M4.~~ **Done (A1.3).** |
| 13 | Hoist BoundGenericsHandler in ProtocolConformanceValidator | 1 file | S | None | — | ~~Eliminates M3.~~ **Done (A1.5).** |

**Total estimated effort for items #1-5:** ~4 weeks. These alone would eliminate the two critical findings and three high-priority findings.

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
- **Status:** Not Started
- **Effort:** M (2-3 days)
- **Findings addressed:** C1, C2
- **Risk:** Low migration risk, high impact — eliminates both Critical findings

**Context:** Three systems independently decide whether a method/property should be emitted: `ProtocolHandler.Emit()` (inline gates), `MemberEmissionValidator.CanEmitMethod()` (20 gates B1-B20), and `ProtocolConformanceValidator.IsMethodSkippedFromInterface()` / `IsPropertySkippedFromInterface()` (mirrored copies). When they diverge, CS0535 or silent binding quality loss results.

**Tasks:**
- [ ] A2.1: Audit all gates in `ProtocolHandler.Emit()` (property gates lines ~100-162, method gates lines ~200-500). Document each gate with its condition and skip reason.
- [ ] A2.2: Audit all gates in `MemberEmissionValidator.CanEmitMethod()` (B1-B20). Map each to the corresponding ProtocolHandler gate.
- [ ] A2.3: Audit `ProtocolConformanceValidator.IsPropertySkippedFromInterface()` and `IsMethodSkippedFromInterface()`. Identify any divergences from ProtocolHandler.
- [ ] A2.4: Design `MemberGateEvaluator` class with `GateResult Evaluate(MemberDecl, TypeDecl, context)` returning `Emit / Skip(reason) / EmitInInterfaceOnly / EmitWithStub`.
- [ ] A2.5: Implement `MemberGateEvaluator` with all gates from the audit. Add unit tests for each gate.
- [ ] A2.6: Migrate `ProtocolHandler.Emit()` to call `MemberGateEvaluator` instead of inline gates.
- [ ] A2.7: Migrate `ProtocolConformanceValidator` to call `MemberGateEvaluator` instead of `IsMethodSkippedFromInterface` / `IsPropertySkippedFromInterface`.
- [ ] A2.8: Migrate `MemberEmissionValidator.CanEmitMethod()` to delegate to `MemberGateEvaluator` (or replace entirely if redundant).
- [ ] A2.9: Run `./run-tests.sh`, `./validate-libraries.sh` — all must pass. Pay special attention to CS0535 errors which indicate gate divergence.

### Session A3: ExtensionEmitterBase + ModuleEmissionContext
- **Status:** Not Started
- **Effort:** L (3-5 days)
- **Findings addressed:** H1, H3, M2, M5
- **Risk:** Low migration risk — extract base, delegate, validate

**Context:** The three extension emitters (`ProtocolExtensionEmitter` 1,773 LOC, `ForeignTypeExtensionEmitter` 1,532 LOC, `CrossModuleExtensionEmitter` 792 LOC) share ~60% structural overlap. All use static mutable state with manual `ResetForModule()` calls, as do 4+ other emitters.

**Tasks:**
- [ ] A3.1: Create `ModuleEmissionContext` — a per-module instance holding scratch state (emitted symbols, swift wrapper lines, struct-emitted flags, needed imports). Provide typed methods like `HasEmittedPInvoke(symbol)`, `HasEmittedSwiftWrapper(symbol)`.
- [ ] A3.2: Migrate `ProtocolExtensionEmitter` static state (`_swiftWrapperLines`, `_emittedSymbols`, `_injectedCount`) to `ModuleEmissionContext`. Remove `ResetForModule()`.
- [ ] A3.3: Migrate `ForeignTypeExtensionEmitter` static state (`_swiftWrapperLines`, `_emittedSymbols`, `_emittedCount`, `_extensionClasses`, `_neededImports`) to `ModuleEmissionContext`. Remove `ResetForModule()`.
- [ ] A3.4: Migrate remaining emitters: `Utf8SliceEmitter`, `CancellationTaskEmitter`, `ErrorDescriptionEmitter`, `GenericClosureBridgeEmitter`. Remove their `ResetForModule()` calls.
- [ ] A3.5: Create shared `ReturnKind` / `ParamKind` enums in a shared location (e.g., `ExtensionEmitterTypes.cs`). Remove duplicate definitions from `ForeignTypeExtensionEmitter` and `CrossModuleExtensionEmitter`. (M2)
- [ ] A3.6: Create `ExtensionEmitterBase` with shared methods: `ClassifyReturnType`, `ClassifyParameterType`, `EmitReturnMarshalling`, `EmitPInvokeDeclaration` (or delegate to `PInvokeEmitHelper` from A1.7). Accept `ModuleEmissionContext`.
- [ ] A3.7: Refactor `ProtocolExtensionEmitter` to extend `ExtensionEmitterBase`, keeping only protocol-specific logic (member discovery, `@_silgen_name` wrappers, self marshalling).
- [ ] A3.8: Refactor `ForeignTypeExtensionEmitter` to extend `ExtensionEmitterBase`, keeping only foreign-type-specific logic (ObjC class detection, default parameter reduction).
- [ ] A3.9: Refactor `CrossModuleExtensionEmitter` to extend `ExtensionEmitterBase`, keeping only cross-module-specific logic (module filtering, existing mangled name reuse).
- [ ] A3.10: Run `./run-tests.sh`, `./validate-libraries.sh` — all must pass with no regressions.

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
