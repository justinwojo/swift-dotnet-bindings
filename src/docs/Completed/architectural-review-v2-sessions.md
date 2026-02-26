# Architectural Review 2 — Completed Sessions

**Completed**: February 26, 2026
**Sessions**: A1, A2, A3

---

## Executive Summary

Three sessions addressed 13 of 20 action items from the architectural review. Two critical findings, three high-priority findings, five medium-priority findings, and four low-priority findings were resolved. One critical finding (C2) was reduced. The remaining items are deferred to `Future/architectural-review-v2.md`.

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

## Resolved Findings

### C1. Gate Logic Duplication Between ProtocolHandler and ProtocolConformanceValidator — RESOLVED (A2)

- **Confidence:** Confirmed
- **Resolution:** `MemberGateEvaluator` created with `EvaluateProperty`, `EvaluateMethod`, `EvaluateSubscript` for protocol-context evaluation. Both `ProtocolHandler.Emit()` and `ProtocolConformanceValidator.IsPropertySkippedFromInterface()`/`IsMethodSkippedFromInterface()` now delegate to the evaluator. 10 private helper methods (7 from PH, 3 from PCV) removed. 31 unit tests added. Adding a new gate now requires editing one file.
- **Location:** `MemberGateEvaluator.cs` (new, ~360 lines)

### H1. Extension Emitter Triplication — RESOLVED (A3)

- **Confidence:** Confirmed
- **Resolution:** Shared `ExtensionMarshallingHelper` extracts `ReturnKind`/`ParamKind` enums, `ClassifyReturnType`/`ClassifyParameterType`, and `EmitReturnValueMarshalling` — eliminating duplicate enum definitions and marshalling switch blocks from `ForeignTypeExtensionEmitter` and `CrossModuleExtensionEmitter`. Swift wrapper accumulation uses `ModuleEmissionContext` typed dedup API (`ctx.TryAdd*Symbol` / `ctx.Add*WrapperLine`) directly in each emitter, providing dedup consistency without an intermediate abstraction layer.
- **Evidence:** Zero `private enum ReturnKind` / `private enum ParamKind` in source. Adding a new return type requires ONE edit in `ExtensionMarshallingHelper`.

### H3. Static Mutable State With Manual Reset — RESOLVED (A3)

- **Confidence:** Confirmed
- **Resolution:** All static mutable state replaced with `ModuleEmissionContext` — a per-module instance created in `Program.cs` and threaded through `EmitModule` → `TypeHandlerContext.EmissionContext` → all handler/emitter call sites. Each emitter's methods accept optional `ModuleEmissionContext? ctx = null` with `Default` fallback for backward compatibility. Typed dedup API (`HasEmitted*/TryAdd*` methods) replaces raw collection access. Zero `ResetForModule()` calls remain. Zero timing-sensitive reset comments remain.
- **Evidence:** `grep -r "ResetForModule" src/Swift.Bindings/src/` returns only a comment explaining the replacement. All 7+ emitters migrated: `ProtocolExtensionEmitter`, `ForeignTypeExtensionEmitter`, `Utf8SliceEmitter`, `CancellationTaskEmitter`, `ErrorDescriptionEmitter`, `GenericClosureBridgeEmitter`, `EnumHandler.RawRepresentable`.

### M1. 19 Independent P/Invoke Emission Points — RESOLVED (A1.7)

- **Resolution:** `PInvokeEmitHelper` created with `PInvokeEmissionInfo` record, `EmitDeclaration(CSharpWriter)`, and `FormatDeclarationLines()`. All 38 explicit `[UnmanagedCallConv]` emission sites across 19 files migrated. 5 bare `[LibraryImport]`-only sites (no calling convention attribute) intentionally left as-is — they use `@_cdecl` wrappers or parameterless case constructors where no `[UnmanagedCallConv]` is needed. Existing `PInvokeDeclaration.Emit()` in `PInvokeHelperEmitter.cs` refactored to delegate to `PInvokeEmitHelper.EmitDeclaration()`.
- **Location:** `PInvokeEmitHelper.cs` (new), 18 migrated files, `PInvokeHelperEmitter.cs` (refactored)

### M2. Duplicate `ReturnKind` / `ParamKind` Enums — RESOLVED (A3)

- **Resolution:** Enums moved to shared `ExtensionMarshallingHelper`. Zero `private enum ReturnKind` / `private enum ParamKind` remain in source.

### M3. `ProtocolConformanceValidator` Creates New `BoundGenericsHandler` Per Method — RESOLVED (A1.5)

- **Resolution:** Existing instance at line 97 now passed as parameter to `GetInterfacePropertyType`, `GetInterfaceMethodReturnType`, `GetInterfaceSubscriptReturnType`, and `HasBareGenericInMethodSignature`. 6 redundant `new BoundGenericsHandler(_typeDatabase)` allocations removed.

### M4. ClosureEmitter Has Its Own `IsBoolType(TypeSpec)` Method — RESOLVED (A1.3)

- **Resolution:** `IsBoolType(TypeSpec)` overload added to `MarshallingHelpers`. Local version removed from `ClosureEmitter.cs`. 12 call sites updated across `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`.

### M5. HashSet<string> Dedup Proliferation — RESOLVED (A3)

- **Resolution:** `ModuleEmissionContext` centralizes dedup sets with typed API (`HasEmitted*/TryAdd*` methods). Static HashSet fields removed from 7+ emitters. Method-scoped sets for per-type/per-method dedup remain (appropriate — they don't need module-level lifetime).

### L1. Single Direct `== "bool"` in ProtocolExtensionClosureBridge — RESOLVED (A1.4)

- **Resolution:** Replaced with `MarshallingHelpers.IsBoolType(csharpType)`.

### L3. `CrossModuleExtensionEmitter.TypeAliasToCSPrimitive` Delegates to ForeignTypeExtensionEmitter — RESOLVED (A1.2)

- **Resolution:** Dictionary moved to `MarshallingHelpers.TypeAliasToCSPrimitive`. Alias field removed from `CrossModuleExtensionEmitter`. Original field removed from `ForeignTypeExtensionEmitter`.

### L4. `IsSwiftPrimitive` in ProtocolExtensionEmitter Is Used by Other Emitters — RESOLVED (A1.1)

- **Resolution:** Method moved to `MarshallingHelpers.IsSwiftPrimitive()`. Original removed from `ProtocolExtensionEmitter`. 25+ call sites updated across `ProtocolExtensionEmitter`, `ForeignTypeExtensionEmitter`, `CrossModuleExtensionEmitter`, `ProtocolExtensionClosureBridge`.

### L5. `ProtocolHandler` Creates `ClosureHandler` Per Property — RESOLVED (A1.6)

- **Resolution:** Hoisted `ClosureHandler` creation before loops. Passed as parameter to `EmitInterfaceProperty`, `EmitInterfaceSubscript`, `EmitInterfaceMethod`. 5 redundant allocations removed.

---

## Session Details

### Session A1: Quick Wins + PInvokeEmitHelper
- **Status:** Complete (February 26, 2026)
- **Effort:** S (1 day)
- **Findings addressed:** M1, M3, M4, L1, L3, L4, L5

**Tasks:**
- [x] A1.1: Move `IsSwiftPrimitive()` from `ProtocolExtensionEmitter` to `MarshallingHelpers`. Updated 25+ call sites across `ProtocolExtensionEmitter`, `ForeignTypeExtensionEmitter`, `CrossModuleExtensionEmitter`, `ProtocolExtensionClosureBridge`. (L4)
- [x] A1.2: Move `TypeAliasToCSPrimitive` dictionary from `ForeignTypeExtensionEmitter` to `MarshallingHelpers`. Removed alias field from `CrossModuleExtensionEmitter`. (L3)
- [x] A1.3: Added `IsBoolType(TypeSpec)` overload to `MarshallingHelpers`. Removed local version from `ClosureEmitter.cs`. Updated 12 call sites across `ClosureEmitter.cs`, `ClosureEmitter.Throwing.cs`, `ClosureEmitter.StructParams.cs`. (M4)
- [x] A1.4: Replaced `csharpType == "bool"` with `MarshallingHelpers.IsBoolType(csharpType)` in `ProtocolExtensionClosureBridge.cs`. (L1)
- [x] A1.5: Hoisted `BoundGenericsHandler` in `ProtocolConformanceValidator` — created once at top, passed as parameter to 4 helper methods. Removed 6 redundant allocations. (M3)
- [x] A1.6: Hoisted `ClosureHandler` in `ProtocolHandler` — created once before loops, passed as parameter to 3 `EmitInterface*` methods. Removed 5 redundant allocations. (L5)
- [x] A1.7: Created `PInvokeEmitHelper` with `PInvokeEmissionInfo` record, `EmitDeclaration(CSharpWriter)`, and `FormatDeclarationLines()`. All 38 planned P/Invoke emission sites (the explicit `[UnmanagedCallConv]` sites) were migrated across 19 files. 5 bare `[LibraryImport]`-only sites remain and were intentionally out of scope (no calling convention attribute needed). (M1)
- [x] A1.8: Validation — 4303 unit tests (0 fail), 700 integration tests (0 fail), 32/32 library validation, golden files pass.

### Session A2: MemberGateEvaluator
- **Status:** Complete (February 26, 2026)
- **Effort:** M (1 day)
- **Findings addressed:** C1 (resolved), C2 (reduced)

**Context:** Four systems independently decided whether a method/property should be emitted: `ProtocolHandler.Emit()` (inline gates P1-P7, M1-M11, S1-S5), `ProtocolConformanceValidator.IsMethodSkippedFromInterface()` / `IsPropertySkippedFromInterface()` (mirrored copies with 9 private helpers), `MethodHandler.Emit()` (bare generic, non-ISwiftObject inline checks), and `MemberEmissionValidator` (B19 unsupported module, bare generic, non-ISwiftObject). When they diverged, CS0535 or silent binding quality loss resulted.

**Result:** Created `MemberGateEvaluator` with `GateResult` (Emit/InterfaceOnly/Skip) and two evaluation modes: full protocol-context evaluation (soft gates for closures/existentials → InterfaceOnly) and hard-gate-only evaluation (concrete context → Skip or Emit only). ProtocolHandler and ProtocolConformanceValidator fully delegate to the evaluator (C1 resolved). `MemberEmissionValidator.CanEmitMethod` delegates via `EvaluateHardGates` early-out. MethodHandler, `CanEmitProperty`, and `ShouldSkipMethodEmission` keep their original inline checks to preserve gate ordering and constructor semantics.

**Tasks:**
- [x] A2.1: Created `MemberGateEvaluator.cs` with `GateDisposition` enum, `SoftGateFlags` flags, `GateResult` class, and evaluator with `EvaluateProperty`, `EvaluateMethod`, `EvaluateSubscript` (protocol context), `EvaluateHardGates`, `EvaluatePropertyHardGates` (concrete context). Static utility `ContainsAnyTypeGenericArg`. 31 unit tests in `MemberGateEvaluatorTests.cs`.
- [x] A2.2: Migrated `ProtocolConformanceValidator` — `IsPropertySkippedFromInterface` and `IsMethodSkippedFromInterface` delegate to evaluator. Removed 3 duplicated private helpers.
- [x] A2.3: Migrated `ProtocolHandler.Emit()` — replaced inline gates P3-P7, M5-M10, S3-S5 with evaluator calls. InterfaceOnly populates tracking sets (`closureSkippedMethodKeys`, `existentialSkippedMethodKeys`) via `SoftGateFlags`. Removed 7 private helpers.
- [x] A2.4: MethodHandler — bare-generic and non-ISwiftObject checks remain inline (not delegated to evaluator) because `EvaluateHardGates` includes an unsupported-module gate that MethodHandler never had, and adding it would change constructor semantics (`ShouldSkipMethodEmission` skips B19 for constructors). MethodHandler's gates are MH-specific by nature.
- [x] A2.5: Wired `MemberEmissionValidator.CanEmitMethod()` — added early-out `EvaluateHardGates()` (safe because original code had B19 + bare-generic + non-ISwiftObject all at top before special handling). `CanEmitProperty` and `ShouldSkipMethodEmission` keep their original gate ordering to avoid changing semantics (non-ISwiftObject must run after special handlers in properties; B19 is the only shared gate in `ShouldSkipMethodEmission`). Kept emission-specific gates (B18, B20 with carve-outs, AsyncStream, tuple, etc.) in MEV.
- [x] A2.6: Validation — 4334 unit tests (0 fail, 31 new), 700 integration tests (0 fail), 32/32 library validation, golden files pass.

### Session A3: ExtensionMarshallingHelper + ModuleEmissionContext
- **Status:** Complete
- **Effort:** L (3-5 days)
- **Findings addressed:** H1 (resolved), H3 (resolved)

**Summary:** Extracted shared marshalling logic into `ExtensionMarshallingHelper` (shared `ReturnKind`/`ParamKind` enums, classify methods, return marshalling). Created `ModuleEmissionContext` — a per-module instance with typed dedup API — replacing all static mutable state and `ResetForModule()` calls across 7+ emitters. Context is threaded from `Program.cs` → `EmitModule` → `TypeHandlerContext` → all handler/emitter call sites. Swift wrapper accumulation uses `ModuleEmissionContext` typed methods directly (no intermediate abstraction needed).

**Tasks:**
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
