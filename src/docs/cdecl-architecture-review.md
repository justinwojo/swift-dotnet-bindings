# @_cdecl Wrapper Architecture Review

Post-migration review after 9+ sessions converting ~78.5% of P/Invokes from `CallConvSwift` to `@_cdecl` wrappers. Goal: identify structural improvements that reduce failure points, simplify maintenance, and make the system more robust before continuing to fix individual wrapper compilation failures.

**Current state**: 40 of 56 wrapper targets fail Swift compilation (~71% failure rate among libraries that should have wrappers). 16 compile successfully. 32 are ObjC/no-wrapper. See `swift-wrapper-errors.md` for the categorized error analysis.

**Relationship to error fixes**: Many of the 13 error categories in `swift-wrapper-errors.md` would be easier to fix (and less likely to regress) after the architectural improvements below. For example:
- **Same-name collision** (error #1, 11 libraries): Fix is straightforward, but must be applied in every emitter that generates qualified type references. Guard centralization (§1) and the signature contract (§3) ensure the fix lands everywhere.
- **Actor isolation** (error #5): Needs a guard in every `ShouldEmitWrapper()`. With guard centralization (§1), this is one predicate in `WrapperValidation`, not 4 separate implementations.
- **`Unmanaged<struct>`** (error #6): Requires type classification in the Swift emission path. With the SwiftBuilder type map (§6), struct vs. class dispatch is centralized.
- **Malformed parameter names** (error #2): Direct consequence of no parameter sanitization in Swift emission. SwiftBuilder (§6) includes `SanitizeIdentifier()`.

The architectural work makes the error fixes **faster, less risky, and regression-proof**.

---

## 1. Guard Fragmentation

**Problem**: ~48 guard conditions scattered across 4 wrapper emitters (`MethodWrapperEmitter`, `PropertyWrapperEmitter`, `ConstructorWrapperEmitter`, `SubscriptWrapperEmitter`). Each reimplements similar checks with subtle variations.

**Examples of duplication**:
- XCFramework mode check (`string.IsNullOrEmpty(env.TypeDatabase.AsyncLibraryName)`) — 4 occurrences
- Non-copyable struct detection (Copyable/Escapable conformance) — 3 occurrences with slightly different code
- Generic parent type handling — 4 separate implementations (`CanEmitGenericClassPropertyWrapper`, `CanEmitGenericClassWrapper`, `CanEmitGenericClassSubscriptWrapper`, `CanEmitGenericClassConstructorWrapper`)

**Impact**: Fixing a guard bug in one emitter but missing another causes inconsistent behavior — some libraries fail where others don't, for the same underlying pattern.

### Recommendation: `WrapperValidation` static class

Extract all shared guard predicates into a single validation class:

```csharp
public static class WrapperValidation
{
    public static bool IsXCFrameworkMode(ITypeDatabase db) => ...;
    public static bool IsNonCopyableStruct(BaseDecl? parent) => ...;
    public static bool IsUnsupportedGenericParent(BaseDecl? parent, MethodDecl method) => ...;
    public static bool HasUnsupportedMetatypeParam(MethodDecl method) => ...;
    public static bool HasUnsupportedActorIsolation(MethodDecl method) => ...;
    // ... etc.
}
```

Each `ShouldEmitWrapper()` becomes a thin composition of shared predicates + emitter-specific checks (e.g., constructor-only logic stays in `ConstructorWrapperEmitter`).

**Priority**: High — directly reduces wrapper compilation failures and makes future guard fixes single-point changes.

---

## 2. Wrapper Type Selection (Flags → Enum)

**Problem**: 5+ boolean flags on `MethodDecl` (`UsesCdeclConstructorWrapper`, `UsesCdeclMethodWrapper`, `UsesCdeclPropertyWrapper`, `UsesCdeclSubscriptWrapper`, `UsesFreeFunctionWrapper`) with mutual exclusivity enforced only by guard ordering in `MethodHandler` lines 801-804. No hard guarantee that only one flag is set.

**Silent fallback**: When no flag is set, `PInvokeEmitter` silently falls back to `CallConvSwift` (line 793-795). No logging, no warning. The only way to discover a method isn't wrapped is to inspect generated code.

### Recommendation: `WrapperStrategy` enum + explicit logging

```csharp
public enum WrapperStrategy
{
    None,              // Intentionally not wrapped (log reason)
    CdeclMethod,
    CdeclConstructor,
    CdeclProperty,
    CdeclSubscript,
    FreeFunction,
    ClosureBridge,
    LegacyCallConvSwift  // Fallback (log warning)
}
```

Replace the flag-setting phase in `MethodHandler` with a single `DetermineWrapperStrategy()` method that returns the enum. Add structured logging when falling back to `LegacyCallConvSwift` with the reason (which guard rejected it).

**Benefits**:
- Mutual exclusivity enforced by the type system
- Silent fallbacks become visible in build output
- Easier to audit which methods are wrapped vs. not

**Priority**: High — eliminates a class of subtle bugs and makes the system observable.

---

## 3. C#/Swift Signature Contract

**Problem**: The generator emits both C# P/Invoke declarations and Swift `@_cdecl` wrappers in **independent code paths** with no shared logic. Parameter ordering, type mapping, and naming are hand-coded separately:

- C# side: `PInvokeEmitter` + `PInvokeSignatureBuilder`
- Swift side: `MethodWrapperEmitter`, `ConstructorWrapperEmitter`, etc.

Parameter order conventions differ by wrapper type:
- Methods: `[resultPtr] [args...] [metadata] [self] [errorOut]`
- Constructors: `[args...] [errorOut]` (result via return)
- Properties: `[resultPtr] [metadata] [self]`

If one side reorders a parameter, you get **silent memory corruption at runtime**, not a compile error.

### Recommendation: `CdeclSignatureContract`

Create a shared abstraction that both emitters consume:

```csharp
public class CdeclSignatureContract
{
    // Builds the canonical parameter list for a given member
    public IReadOnlyList<CdeclParameter> BuildParameterList(
        MemberKind kind,        // Method, Constructor, Property, Subscript
        MethodDecl method,
        TypeRecord? returnType,
        bool hasIndirectResult,
        bool isThrowable);

    // Each emitter queries the contract for its side:
    public string ToCSharpPInvokeSignature(IReadOnlyList<CdeclParameter> params);
    public string ToSwiftCdeclSignature(IReadOnlyList<CdeclParameter> params);
}

public record CdeclParameter(
    string Name,
    CdeclParamRole Role,     // Self, Argument, ResultPtr, ErrorOut, Metadata
    TypeRecord? TypeInfo,
    int Position);
```

Single source of truth for parameter ordering. Both emitters derive their signatures from the same contract. Tests can verify the contract itself rather than cross-checking two outputs.

**Priority**: High — prevents the scariest class of bugs (silent ABI mismatches).

---

## 4. Projection Parity (Visitor Pattern)

**Problem**: Adding a new type projection requires updating **7+ switch statements** across:
- `ProtocolProxyEmitter.Receivers` (getter + setter switches)
- `PropertyHandler`
- `SubscriptHandler`
- `EnumHandler.Marshalling`
- `WrapperEmitter.Return`
- `ClosureHandler`

All switches use `_ => null` or `_ => ...` default cases, so missing a type silently returns null rather than causing a compilation error.

### Recommendation: Visitor pattern on `ITypeProjection`

```csharp
public interface ITypeProjection
{
    // ... existing members ...
    T Accept<T>(IProjectionVisitor<T> visitor);
}

public interface IProjectionVisitor<T>
{
    T Visit(StringProjection p);
    T Visit(ArrayProjection p);
    T Visit(ClassProjection p);
    // ... one method per projection type
}
```

Each projection implements `Accept()`. Each current switch site becomes a visitor implementation. Adding a new projection type without implementing it in all visitors causes a **compile error**.

**Priority**: Medium — preventive measure; most impactful when the next projection type is added.

---

## 5. Post-Processor → Emitter Push-Back

**Problem**: `SwiftWrapperPostProcessor` strips ~10 categories of broken Swift patterns **after** generation instead of preventing them at emission time. Current stripped patterns:

| Pattern | Emitter Source | Preventable? |
|---------|---------------|-------------|
| `EveryProtocol()` calls | WitnessDispatchEmitter | **Yes** — sync gates with EveryProtocolEmitter |
| `self.` without `_self:` param | Protocol emitters | **Yes** — check param list before emitting |
| `__self.init(` | ConstructorWrapperEmitter | **Yes** — C# name leaked into Swift |
| Mutating on `let` existential | WitnessDispatchEmitter | **Yes** — use `var` for setters |
| Non-escaping closure in Task | WrapperEmitter.Async | **Yes** — gate on escaping classification |
| Raw generic params (τ_0_0) | Multiple emitters | **Yes** — reject unresolved names, skip method |
| Internal type references | All emitters | **No** — requires Swift compiler feedback |

**Impact**: The "emit then strip" pattern masks bugs. When the post-processor strips a function, you get a wrapper xcframework that compiles but is missing methods. The C# bindings still reference those methods → `DllNotFoundException` at runtime.

### Recommendation: Phase out preventable patterns

For each preventable pattern:
1. Add the equivalent gate to the emitter (skip the method instead of generating broken code)
2. Add a unit test that verifies the gate
3. Remove the pattern from the post-processor (or keep as a defensive safety net with a warning log)

Keep the post-processor **only** for:
- Internal type references (genuinely requires compiler feedback)
- As a last-resort safety net (log a warning when it strips, so the underlying emitter bug gets fixed)

**Priority**: Medium-high — each pattern fixed reduces the gap between "wrapper compiles" and "wrapper actually works."

---

## 6. Swift Code Generation Infrastructure

**Problem**: 29 distinct code paths emit Swift source using raw string interpolation through a minimal `SwiftWriter` (empty wrapper around `IndentedTextWriter`). No language-specific helpers.

**Issues**:
- **Type mapping duplication**: `WitnessDispatchEmitter` maintains `SwiftToCSharpPrimitiveMap` and `CSharpToSwiftTypeMap` (~30 entries each). `ClosureEmitter` has `GetSwiftCdeclParamType()` with a parallel switch. `ExistentialBypassEmitter` has `RenderSwiftTypeSpec()`. No single source of truth.
- **Indentation bugs**: Manual `writer.Indent++`/`Indent--` scattered across 29 files. No scope-managed blocks. Early returns risk forgetting to decrement.
- **Symbol sanitization**: `ThemeBridgeEmitter` hardcodes 39 Swift keywords. `ErrorDescriptionEmitter` implements `MakeSafeSymbolSuffix()`. `MethodWrapperEmitter` builds symbols ad-hoc.
- **No validation**: Malformed Swift only surfaces when `swiftc` runs externally.

### Recommendation: `SwiftBuilder` utilities

Not a full templating engine — that would be over-engineering. Instead, targeted helpers:

```csharp
public static class SwiftBuilder
{
    // Scope-managed blocks (auto indent/dedent + braces)
    public static IDisposable FunctionBlock(SwiftWriter w, string signature, string? attribute = null);
    public static IDisposable ExtensionBlock(SwiftWriter w, string typeName);
    public static IDisposable IfBlock(SwiftWriter w, string condition);

    // Centralized type maps
    public static string CSharpToSwiftType(string csharpType);
    public static string SwiftToCSharpType(string swiftType);

    // Symbol safety
    public static string SanitizeIdentifier(string name);
    public static string EscapeIfKeyword(string name);
}
```

Also centralize the "emit once per module" pattern used by `Utf8SliceEmitter`, `ErrorDescriptionEmitter`, and `ProtocolExtensionEmitter` into a shared `OncePerModuleEmitter` base or helper.

**Priority**: Medium — reduces bugs in generated Swift, but current raw-string approach works for simple cases.

---

## 7. Test Gaps

### Gap 1: No cross-wrapper guard consistency tests

Each wrapper type has independent `ShouldEmit` tests. No test verifies that all wrapper types make the **same decision** for shared conditions (non-copyable structs, generic parents, actor isolation, etc.).

**Recommendation**: `WrapperConsistencyTests.cs` — create shared test fixtures and verify all 4 wrapper types agree.

### Gap 2: No parameter order validation (C# ↔ Swift)

Tests verify that generated code contains expected strings (`"self_"`, `"resultPtr"`) but never verify the **order** matches between C# and Swift sides.

**Recommendation**: `WrapperParameterOrderTests.cs` — generate both sides for test methods, parse parameter lists, verify position-by-position match. (Mostly eliminated by the Signature Contract in item 3, but still useful as regression tests.)

### Gap 3: No projection parity tests across wrapper boundaries

`TypeProjectionConsistencyTests` verifies types resolve correctly, but doesn't verify wrapper-specific marshalling matches across emitters.

**Recommendation**: `WrapperProjectionParityTests.cs` — emit full methods with enum/bool/closure/optional types, verify marshalling is consistent.

### Gap 4: No golden files for generated Swift

Golden file tests exist for C# output (`TestFramework/golden/`) but not for Swift wrapper output. Swift regressions are only caught by `swiftc` compilation (binary pass/fail) or runtime crashes.

**Recommendation**: Add Swift golden files for representative wrapper patterns (method, property, constructor, subscript × class, struct, enum).

### Gap 5: No dedup/collision tests for @_cdecl symbols

Overloaded methods must get unique `@_cdecl` symbols. No test verifies this.

**Recommendation**: `WrapperDedupTests.cs` — generate wrappers for overloaded methods, verify unique symbols.

**Priority**: High for gaps 1-2 (directly prevent regression bugs), medium for 3-5.

---

## 8. Additional Improvements

### 8a. Decompose `MethodRequiresIndirectResult()`

60+ line monolithic decision tree in `MarshallingHelpers.cs` with deep nesting. Split into domain-specific predicates:
- `IsCdeclIndirectResultRequired()` — wrapper-specific rules
- `IsConstructorIndirectResultRequired()` — constructor-specific
- `IsTypeInherentlyIndirect()` — type-level rules

Each becomes independently testable.

### 8b. Structured emission metadata

When the generator runs, emit a manifest alongside the wrapper Swift files:
```json
{
  "module": "Nuke",
  "methods_wrapped": 142,
  "methods_skipped": 23,
  "skip_reasons": {
    "method_level_generics": 8,
    "unsupported_closure": 5,
    "inout_params": 10
  },
  "post_processor_strips": 3
}
```

This makes the wrapper coverage observable without reading logs, and helps identify which guard categories cause the most skips.

### 8c. WitnessDispatchEmitter / EveryProtocolEmitter gate synchronization

These two emitters operate independently but must agree: if `EveryProtocolEmitter` skips a protocol conformance, `WitnessDispatchEmitter` must not emit dispatch calls to it. Currently this inconsistency is the #1 source of post-processor strips.

**Fix**: Share a `ProtocolConformanceDecisionCache` between both emitters, populated by `EveryProtocolEmitter` and read by `WitnessDispatchEmitter`.

---

## Implementation Plan — 3 Sessions

Consolidated from the original 5-phase plan. Each session is self-contained and leaves the codebase in a better state. Sessions should be executed in order (each builds on the previous). Use plan mode in a fresh context for each session.

---

### Session 1: Wrapper Decision Infrastructure

**Scope**: Refactor how the system decides which wrapper strategy to use for each member, and how guards are evaluated.

**Deliverables**:

1. **`WrapperValidation` static class** (§1)
   - Extract all shared guard predicates from `MethodWrapperEmitter.ShouldEmitWrapper()`, `PropertyWrapperEmitter.ShouldEmitWrapper()`, `ConstructorWrapperEmitter.ShouldEmitWrapper()`, `SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper()`
   - Shared predicates: XCFramework mode, non-copyable struct, generic parent type, actor isolation, metatype params, unsupported generic containers, nested types
   - Each `ShouldEmitWrapper()` becomes a thin composition of `WrapperValidation` calls + emitter-specific logic
   - Grep the full codebase for all guard patterns to ensure none are missed

2. **`WrapperStrategy` enum** (§2)
   - Replace `UsesCdeclConstructorWrapper`, `UsesCdeclMethodWrapper`, `UsesCdeclPropertyWrapper`, `UsesCdeclSubscriptWrapper`, `UsesFreeFunctionWrapper` boolean flags
   - Single `WrapperStrategy` property on `MethodDecl` (or equivalent)
   - Refactor `MethodHandler` flag-setting phase (lines ~726-913) into `DetermineWrapperStrategy()` method
   - Refactor `PInvokeEmitter` calling convention selection (line ~793) to switch on enum

3. **Fallback logging** (§2)
   - When `WrapperStrategy` resolves to `LegacyCallConvSwift`, log the reason (which guard rejected wrapper emission)
   - When `WrapperStrategy` resolves to `None` (method skipped entirely), log that too

4. **`WrapperConsistencyTests.cs`** (§7 gap 1)
   - Shared test fixtures for conditions that all wrapper types must agree on
   - Test: given a non-copyable struct method, all 4 `ShouldEmitWrapper()` return false
   - Test: given a concrete generic class method, all 4 return true
   - Test: given an actor-isolated method, all 4 return false
   - Validate `WrapperValidation` predicates independently

**Key files to modify**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/PropertyWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/SubscriptWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- `MethodDecl` or wherever the boolean flags live
- New: `WrapperValidation.cs`, `WrapperStrategy.cs`, `WrapperConsistencyTests.cs`

**Validation**: `run-tests.sh` must pass. Run `validate-libraries.sh` at end to confirm no regressions (same baseline).

---

### Session 2: Emission Layer

**Scope**: Refactor how both C# and Swift code is generated — shared signature contracts, centralized type maps, compile-time projection exhaustiveness.

**Deliverables**:

1. **`CdeclSignatureContract`** (§3)
   - Single class that builds the canonical parameter list for any member kind (method, constructor, property, subscript)
   - Defines parameter ordering rule: `[resultPtr] [args...] [metadata] [self] [errorOut]` (and constructor variant)
   - `CdeclParameter` record with `Name`, `Role` (enum: Self, Argument, ResultPtr, ErrorOut, Metadata), `TypeInfo`, `Position`
   - `PInvokeEmitter` derives C# signatures from the contract
   - Wrapper emitters derive Swift `@_cdecl` signatures from the contract
   - Both sides guaranteed to match by construction

2. **`SwiftBuilder` utilities** (§6)
   - Scope-managed blocks: `using (SwiftBuilder.FunctionBlock(writer, signature, attribute))` — auto indent/braces
   - Centralized type maps: consolidate `SwiftToCSharpPrimitiveMap` (WitnessDispatchEmitter), `CSharpToSwiftTypeMap` (WitnessDispatchEmitter), `GetSwiftCdeclParamType()` (ClosureEmitter), `RenderSwiftTypeSpec()` (ExistentialBypassEmitter) into one shared map
   - Identifier sanitization: `SanitizeIdentifier()` strips brackets, parens, type-syntax chars from names; `EscapeIfKeyword()` consolidates the 39-keyword list from ThemeBridgeEmitter
   - "Emit once per module" helper: shared pattern from `Utf8SliceEmitter`, `ErrorDescriptionEmitter`, `ProtocolExtensionEmitter`

3. **Visitor pattern for projections** (§4)
   - Add `T Accept<T>(IProjectionVisitor<T> visitor)` to `ITypeProjection`
   - Define `IProjectionVisitor<T>` with one `Visit()` method per projection type
   - Convert the 7+ switch sites to visitor implementations:
     - `ProtocolProxyEmitter.Receivers` getter/setter switches
     - `PropertyHandler` conversion dispatch
     - `SubscriptHandler` conversion dispatch
     - `EnumHandler.Marshalling`
     - `WrapperEmitter.Return`
     - `ClosureHandler`
   - Adding a new projection without implementing all visitors → compile error

4. **Decompose `MethodRequiresIndirectResult()`** (§8a)
   - Split the 60-line decision tree into: `IsCdeclIndirectResultRequired()`, `IsConstructorIndirectResultRequired()`, `IsTypeInherentlyIndirect()`
   - Add unit tests for each predicate

5. **Tests** (§7 gaps 2-3)
   - `WrapperParameterOrderTests.cs` — generate C# + Swift for test methods, verify parameter sequences match (validates the signature contract)
   - `WrapperProjectionParityTests.cs` — emit methods with enum/bool/closure/optional types, verify marshalling consistent across emitters

**Key files to modify**:
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PInvokeEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter*.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/MethodWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/PropertyWrapperEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/SubscriptWrapperEmitter.cs`
- `src/Swift.Bindings/src/Marshaler/Projection/*.cs` (all projections — add Accept)
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ProtocolProxyEmitter.Receivers.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/EnumHandler.Marshalling.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/TextWriter/` (SwiftWriter enhancement)
- WitnessDispatchEmitter, ClosureEmitter, ExistentialBypassEmitter, ThemeBridgeEmitter (type map consolidation)
- `src/Swift.Bindings/src/Marshaler/MarshallingHelpers.cs`
- New: `CdeclSignatureContract.cs`, `SwiftBuilder.cs`, `IProjectionVisitor.cs`, test files

**Validation**: `run-tests.sh` must pass. Run `validate-libraries.sh` at end to confirm no regressions.

---

### Session 3: Post-Processor Elimination & Observability

**Scope**: Move pattern-stripping logic from the post-processor back into the emitters (prevent bad code generation instead of cleaning it up after), and add observability.

**Depends on**: Sessions 1-2 (centralized guards and SwiftBuilder make it easier to add gates in the right places).

**Deliverables**:

1. **`ProtocolConformanceDecisionCache`** (§8c)
   - Shared cache between `EveryProtocolEmitter` and `WitnessDispatchEmitter`
   - `EveryProtocolEmitter` populates: "I skipped conformance for protocol X because [reason]"
   - `WitnessDispatchEmitter` reads: "Is conformance available for protocol X?" — if not, skip dispatch emission
   - This is the #1 source of post-processor strips (EveryProtocol() calls in generated Swift)

2. **Push preventable patterns into emitters** (§5)
   - For each pattern, add the equivalent gate and a unit test:

   | Pattern | Where to gate | Test |
   |---------|--------------|------|
   | `EveryProtocol()` calls | WitnessDispatchEmitter (check conformance cache) | Verify no dispatch emitted for skipped conformance |
   | `self.` without `_self:` | Protocol emitters (check param list) | Verify `_self.` used when `_self:` param exists |
   | `__self.init(` | ConstructorWrapperEmitter (Swift emission path) | Verify no C# identifiers in Swift output |
   | Mutating on `let` existential | WitnessDispatchEmitter (use `var` for setters/mutating) | Verify `var` emitted for mutating contexts |
   | Non-escaping closure in Task | WrapperEmitter.Async (check escaping classification) | Verify async skipped for non-escaping closures |
   | Raw generic params (τ_0_0) | SwiftTypeNameHelper (reject unresolved, skip method) | Verify method skipped when generic can't resolve |

3. **Slim down `SwiftWrapperPostProcessor`** (§5)
   - Remove patterns that are now prevented at emission time
   - Keep only: internal type reference stripping (requires compiler feedback) + safety-net fallback with warning log
   - Add warning log when the post-processor strips anything — indicates an emitter bug that should be fixed

4. **Emission metadata manifest** (§8b)
   - Generator emits `binding-emission-report.json` alongside wrapper files:
     ```json
     {
       "module": "Nuke",
       "wrapper_strategy_counts": {
         "CdeclMethod": 98, "CdeclProperty": 34, "CdeclConstructor": 10,
         "LegacyCallConvSwift": 23, "Skipped": 5
       },
       "skip_reasons": { "method_level_generics": 3, "inout_params": 2 },
       "post_processor_strips": 0
     }
     ```
   - `validate-libraries.sh` can optionally report this

5. **Remaining test gaps** (§7 gaps 4-5)
   - `WrapperDedupTests.cs` — overloaded methods get unique `@_cdecl` symbols
   - Swift golden files for representative wrapper patterns (method/property/constructor/subscript × class/struct)

**Key files to modify**:
- `src/Swift.Bindings/src/Configuration/SwiftWrapperPostProcessor.cs` (slim down)
- `src/Swift.Bindings/src/Configuration/SwiftWrapperCompiler.cs` (emit manifest)
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/WitnessDispatchEmitter.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/WrapperEmitter.Async.cs`
- `src/Swift.Bindings/src/Emitter/StringEmitter/ConstructorWrapperEmitter.cs`
- Protocol emitters (self/`_self` fix)
- SwiftTypeNameHelper (generic param rejection)
- New: `ProtocolConformanceDecisionCache.cs`, test files, golden files

**Validation**: `run-tests.sh` must pass. Run `validate-libraries.sh` at end — expect **improved** results (fewer post-processor strips, possibly some libraries newly passing if gates were the issue).

---

## Post-Architecture: Error Fixing

After all 3 sessions, proceed to fix the 13 error categories in `swift-wrapper-errors.md`. The architectural improvements make these fixes:
- **Single-point**: Guards go in `WrapperValidation`, type maps go in `SwiftBuilder`, signatures go through `CdeclSignatureContract`
- **Test-covered**: New test suites catch regressions across all wrapper types
- **Observable**: Emission metadata shows exactly which methods are wrapped, skipped, or stripped

Recommended error fix order (from `swift-wrapper-errors.md`):
1. Same-name module/type collision (#1) — 11 libraries, 4 fully fixed
2. Malformed parameter names (#2) — 4 libraries (use `SwiftBuilder.SanitizeIdentifier()`)
3. Pointer type mismatch (#4) — 3 Nuke variants fully fixed
4. Actor isolation gate (#5) — 2 libraries fully fixed (add to `WrapperValidation`)
5. Remaining categories in priority order per `swift-wrapper-errors.md`

---

## Non-Goals

Things this review explicitly does **not** recommend:

- **Replacing `@_cdecl` with `CallConvSwift`** — the wrapper architecture is sound and more portable
- **Full Swift AST/templating engine** — targeted helpers (§6) are sufficient
- **Rewriting the emitter from scratch** — incremental refactoring is lower-risk
- **Changing the projection interface** — `ITypeProjection` is well-designed; the issue is switch-site exhaustiveness, not the interface itself
