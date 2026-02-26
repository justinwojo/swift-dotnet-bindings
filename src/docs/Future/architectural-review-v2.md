# Architectural Review 2 — Remaining Items

**Original review**: February 25, 2026
**Completed sessions**: A1, A2, A3 — see `Completed/architectural-review-v2-sessions.md`
**Status**: All high-impact/low-risk items done. Remaining items are deferred (high effort or medium risk).

---

## Open Findings

### C2. MemberEmissionValidator / ProtocolHandler / MethodHandler Gate Triplication (Remainder)

- **Status:** Reduced (A2). PH + PCV fully delegate to `MemberGateEvaluator`. MethodHandler, `CanEmitProperty`, and `ShouldSkipMethodEmission` keep inline checks.
- **Why deferred:** MethodHandler never had the unsupported-module gate — adding it changes constructor semantics. `CanEmitProperty`'s non-ISwiftObject must run after special handlers (AsyncStream/existential/closure). MethodHandler's MH-specific gates (existential accumulate+bypass, unsatisfied constraints, protocol constraints, closure bridge routing) have *routing* behavior across 5+ specialized emitters — unifying them requires pluggable emitter strategies, not just gate extraction.
- **Effort:** M (1 week) | **Risk:** Medium

### H2. ClosureHandler as Parallel Type System

- **Location:** `ClosureHandler.cs` (1,620 lines) — `TranslateTypeSpecToCSharp()`, `TranslateBoundGenericToCSharp()`, `TranslateTypeSpecToPInvokeType()`, `IsFrozenStruct()` / `IsNonFrozenStruct()` / `IsClassType()` / `IsSimpleEnum()`
- **Problem:** ClosureHandler translates types for closure argument/return positions using its own resolution logic, not `TypeProjectionFactory`. The factory handles closures via `ClosureProjection`, but callback signature generation requires raw P/Invoke types independently of the factory's `ITypeProjection.PInvokeType`.
- **Why deferred:** Works correctly across all 32 libraries. Closure marshalling internals are where subtle runtime bugs hide. The fix (~400 lines removed) isn't worth the risk right now.
- **Effort:** L (2+ weeks) | **Risk:** Medium
- **Fix:** Incrementally replace `ClosureHandler.TranslateTypeSpecToCSharp` calls with `TypeProjectionFactory.Project()` calls, using `projection.PublicType` for delegate types and `projection.PInvokeType` for callback signatures.

### H4. TypeProjectionFactory Gaps Force Fallback to Legacy Paths

- **Location:** `TypeProjectionFactory.cs:204` — `if (namedType.GenericParameters.Count > 0) return null;`
- **Problem:** Factory explicitly bails on user-defined bound generics. Callers must use `factory.Project(typeSpec, ctx) ?? boundGenericsHandler.TranslateBoundGenericTypeToCSharp(typeSpec)`. This two-phase lookup exists in 6+ files.
- **Why deferred:** The `?? fallback` pattern is ugly but stable across all 32 libraries. Solving it requires the "public-vs-raw type" design problem.
- **Effort:** L (2+ weeks) | **Risk:** Medium
- **Fix:** Extend `ITypeProjection` with a `RawType` property. Create `BoundGenericProjection` wrapping `BoundGenericsHandler`. Eliminates the null-return gap.

### M6. Program.cs Orchestration Complexity

- **Location:** `Program.cs` (1,368 lines)
- **Problem:** Handles CLI parsing, input resolution, type database construction, swiftinterface parsing, dependency resolution, protocol extension injection, module emission orchestration, and wrapper compilation — all in one file. Pipeline stage ordering is encoded as function-call ordering, not as explicit stages.
- **Why deferred:** Nice-to-have for testability, won't prevent any bugs.
- **Effort:** M (1 week) | **Risk:** Low
- **Fix:** Extract into pipeline stages: `InputResolver` → `TypeDatabaseBuilder` → `PreEmissionTransforms` → `Emitter` → `PostEmissionSteps`. `Program.cs` becomes a 50-line pipeline orchestrator.

### L2. ClosureHandler Creates ExistentialHandler Internally

- **Location:** `ClosureHandler.cs`
- **Problem:** Should accept ExistentialHandler as a dependency rather than creating it. May use a different composition collector than the main pipeline's ExistentialHandler.
- **Effort:** S (1 day) | **Risk:** Low

---

## Open Action Items

| # | What | Effort | Risk | Notes |
|---|------|--------|------|-------|
| 6 | Migrate ClosureHandler type resolution to TypeProjectionFactory | L | Med | Reduces H2. ~400 lines removed. |
| 7 | Add `BoundGenericProjection` to TypeProjectionFactory | L | Med | Eliminates H4. Factory becomes true single entry point. |
| 8 | Extract Program.cs pipeline stages | M | Low | Eliminates M6. Testable pipeline. |
| 9 | Add cross-path consistency tests | M | Low | Catches gate/type disagreements before they reach libraries. |
| 10 | Add golden-file tests for all 32 validation libraries | M | Low | Catches any output change across generator modifications. |

---

## Redesign Proposal

If redesigning the internal architecture from scratch with the same inputs/outputs:

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

A single `MemberGateEvaluator` (partially done — A2 extracted this for protocol-context gates):

```csharp
enum GateResult { Emit, Skip, EmitInInterfaceOnly, EmitWithStub }

GateResult Evaluate(MemberDecl member, TypeDecl parentType, EmissionContext context);
```

Called once per member. Result is stored on the `MemberPlan`. No more scattered `if` chains.

### Test Structure

1. **Plan tests:** For a corpus of real-world method declarations, assert the `MemberPlan` is correct.
2. **Emission tests:** For known `MemberPlan` values, assert the emitted string matches golden output.
3. **Consistency tests:** For every protocol-conforming type, assert that the interface plan and implementation plan agree on types and signatures.
4. **Golden-file tests:** Full output for all 32 validation libraries, diffed on every change.
