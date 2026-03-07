# ObjC Binding Subsystem: Master Improvement Plan

> Consolidated from three independent analyses (Claude, Grok, Codex) with cross-verification against the actual codebase. Items are verified as accurate unless noted with caveats.
>
> **Last updated**: 2026-03-07. Items 1-10 are complete. Item 7 Phases 1-2 (protocol qualifications + nested nullability) are complete; the full recursive type tree remains as future work. Three post-implementation bug fixes from Codex review are also complete.

## Guiding Principle

This subsystem replaces Objective Sharpie. Every improvement targets one of:
- **Correctness**: Emit bindings that compile and behave correctly for real-world frameworks
- **Fidelity**: Preserve more semantic information from ObjC headers than Sharpie did
- **Transparency**: Make it obvious what was bound, what was skipped, and why

---

## P0: Critical (Correctness Bugs)

### 1. Enum backing types are parsed then discarded

**Status**: COMPLETE (commit `694bde1`)

`StructsAndEnumsEmitter.EmitEnum()` now drives the emitted base type from `ObjCEnumDecl.UnderlyingType`:
- **Native-width signed** (`NSInteger`, `CFIndex`, `long`) -> `long` with `[Native]`
- **Native-width unsigned** (`NSUInteger`, `unsigned long`) -> `ulong` with `[Native]`
- **Fixed-width primitives** (`uint8_t` -> `byte`, `int32_t` -> `int`, `uint32_t` -> `uint`, etc.)
- **Typedef-aliased backing types** resolved through the typedef map (e.g., `MyEnumBase` -> `uint32_t` -> `uint`) — added in Codex bug fix commit `012c999`
- **`[Flags]`** remains orthogonal to backing type choice
- **Unknown/missing underlying type** -> falls back to `long`/`ulong` with `[Native]`

**Tests**: Round-trip tests for `NSInteger`, `NSUInteger`, `uint8_t`, `int32_t`, `unsigned int`, `CFIndex`, `[Flags]` + fixed-width combination, typedef-aliased backing types.

---

## P1: High (Fidelity & Transparency)

### 2. Diagnostic report for skipped symbols

**Status**: COMPLETE (commit `be68573`)

`ObjCBindingDiagnostics` collector accumulates skipped symbols with structured reasons. Categories: `UnresolvableType`, `UnavailableApi`, `UnsupportedConstruct`, `AccessibilityConflict`, `DuplicateSignature`. Both emitters record skips. Summary available via `SkippedSymbols` list with kind, name, reason, and detail.

### 3. Availability emission completion

**Status**: COMPLETE (commit `88a6bc8`)

- `[Obsoleted]` emitted when `ObsoletedVersion` is set
- Symbols skipped entirely when `IsUnavailable` is true (with diagnostic recording)
- `message` parameter included on `[Deprecated]`/`[Obsoleted]` when `Message` is set
- `ObjCAvailabilityEmitter` extracted as shared static helper used by both emitters
- Availability emission added to `StructsAndEnumsEmitter` for enums
- `ObjCConstantDecl` and `ObjCFunctionDecl` carry `Availability` metadata populated from clang AST

**Codex bug fix** (commit `012c999`): Availability checks now run BEFORE doc comment emission in all 5 emitter locations (protocol, class, method, property, enum). This prevents orphaned XML doc comments from attaching to the next declaration when a symbol is unavailable.

### 4. NS_SWIFT_NAME attribute capture

**Status**: COMPLETE (commit `2e8633c`)

`ClangAstParser` extracts `SwiftNameAttr` values from clang AST nodes. `SwiftName` field added to `ObjCMethodDecl`, `ObjCPropertyDecl`, `ObjCClassDecl`, and `ObjCEnumDecl`. Surfaced in diagnostics output. NOT auto-applied to C# naming (capture-only, per design rationale).

### 5. NS_REFINED_FOR_SWIFT metadata capture

**Status**: COMPLETE (commit `7455b66`)

`SwiftPrivateAttr` detected in clang AST. `IsRefinedForSwift` flag added to `ObjCMethodDecl` and `ObjCPropertyDecl`. NOT auto-emitted as `[EditorBrowsable(Never)]` (capture-only, per design rationale).

### 6. Doc comment preservation

**Status**: COMPLETE (commit `d8a29d8`)

`DocComment` field added to `ObjCClassDecl`, `ObjCProtocolDecl`, `ObjCMethodDecl`, `ObjCPropertyDecl`, `ObjCEnumDecl`. `ClangAstParser` extracts `FullComment` nodes including `ParagraphComment`, `TextComment`, and `ParamCommandComment`. `ObjCDocCommentEmitter` emits `/// <summary>` XML comments. For `@param` tags, emits `/// <param name="...">` on methods. `DocParams` list (via `ObjCDocParam` record) added to `ObjCMethodDecl`.

---

## P2: Medium (Model Richness & Extensibility)

### 7. ObjC type IR: nested nullability and protocol qualifications

**Status**: Phases 1-2 COMPLETE. Full recursive type tree remains as FUTURE WORK.

#### Phase 1: Protocol qualifications on concrete types (COMPLETE)

Commit `1c4047f` added `ProtocolQualifications` (as `List<string>`) to `ObjCTypeRef`, parsed from both `id<Proto>` (via `TryParseIdProtocol`) and concrete types like `NSObject<NSCopying>` (via heuristic in `TryParseGeneric`).

**Codex bug fix** (commits `012c999`, `9685f93`, `6cf59d1`): The initial heuristic misparsed custom ObjC lightweight generics (e.g., `RLMResults<RLMObjectType>`) as protocol qualifications. Fixed with a context-aware approach:
- `ClangAstParser` pre-scans for classes with `ObjCTypeParamDecl` children and registers them as known generic containers via `ObjCTypeRefParser.SetAdditionalGenericContainers`
- Static `KnownGenericContainers` set covers Foundation types (`NSArray`, `NSDictionary`, etc.)
- AST-derived set covers library-specific generic containers (`RLMResults`, `RLMArray`, etc.)
- `[ThreadStatic]` field ensures thread safety for parallel test execution
- `try/finally` ensures cleanup on exceptions

#### Phase 2: Nested nullability for blocks and generic args (COMPLETE)

`StripNullability` was a flat `string.Replace` that stripped ALL nullability annotations before structural parsing. This caused wrong nullability on blocks (picked inner annotation instead of outer) and lost nullability on generic args (same annotation on inner+outer got both stripped).

**Fix** (7a — block nullability): For block types (detected by `(^`), `Parse()` skips `StripNullability` entirely. `TryParseBlock` extracts the block's own nullability structurally from the caret group `(^ _Nullable)`, while return type and parameter nullabilities are handled by recursive `Parse` calls on raw substrings.

**Fix** (7b — generic arg nullability): `StripNullability` replaced with a depth-aware version using `FindAtDepthZero` / `FindLastAtDepthZero`. Only strips annotations at bracket depth 0, preserving inner annotations (inside `<>` or `()`) for recursive `Parse` calls. The outermost annotation is selected by rightmost position, not token-kind order, so double pointers like `NSError * _Nonnull * _Nullable` correctly pick the outer `_Nullable`.

**Emitter**: `FormatGenericTypeHint` enhanced to include nullability suffix — e.g., `"Element type: string (nullable)"`.

**No model changes** — `ObjCTypeRef` already had per-element nullability on `BlockParams`, `BlockReturnType`, `GenericArgs`.

**Tests**: 7 block nullability tests, 4 generic arg nullability tests, 1 mixed double-pointer test, 3 end-to-end emission tests. Validation: 88/88, +2 improvements (FirebaseFirestoreInternal and GTMSessionFetcher).

**Design doc**: `src/docs/Completed/item7-nested-nullability-plan.md`

#### Remaining phases (FUTURE WORK)

The current `ObjCTypeRef` is still flat. A recursive type tree would enable:
- **Full pointer-edge qualifier model** — qualifiers attach to pointer/reference edges in a tree structure
- **Double-pointer inner nullability emission** — `NSError * _Nullable *` inner annotation is stripped for structural matching; separate fix if ever needed

The current model covers the practical cases. The flat model with recursive fields (`BlockParams`, `GenericArgs`) handles 95%+ of real-world types.

**Refs**: `ObjCTypeRef.cs`, `ObjCTypeRefParser.cs`

### 8. Generic collection type mapping

**Status**: COMPLETE (commit `ec0dd52`)

Generic type information preserved as `//` comments in emitted output rather than changing C# types (bgen has limited generic collection support). `ObjCTypeMapper.FormatGenericTypeHint` produces descriptive hints:
- `NSArray<NSString *>` -> `// Element type: string`
- `NSDictionary<NSString *, NSNumber *>` -> `// Key type: string, Value type: NSNumber`
- `NSArray<NSString * _Nullable>` -> `// Element type: string (nullable)` (nullability suffix added in item 7 Phase 2)
- Custom containers -> `// Generic args: ...`

Hints emitted for method return types, parameters, and properties. Generic type parameters preserved by name (not mapped to `NSObject`).

### 9. Struct layout safety

**Status**: COMPLETE (commit `dee5996`)

`ClangAstParser.ParseStructFieldsWithLayout` detects bitfields (via `isBitfield` property) and anonymous unions/structs (unnamed inner `RecordDecl` nodes). Unsafe layout metadata carried through `HasUnsafeLayout` and `UnsafeLayoutReason` on `ObjCStructDecl`. `StructsAndEnumsEmitter` skips structs with unsafe layouts and records a diagnostic. Typedef promotion path carries unsafe layout metadata.

### 10. Test infrastructure

**Status**: COMPLETE (commit `cef5df1`)

- `ObjCTestHelpers` — static class with shared `Logger`, `SimpleType()`, `EmitApiDefinition()`, `EmitStructsAndEnums()`, `EmitStructsAndEnumsBoth()`, `WrapInTranslationUnit()`, `MakeLoc()`, `DefaultHeadersPath`
- `ObjCModuleBuilder` — fluent builder for constructing `ObjCModule` instances with sub-builders for Class, Protocol, Enum, Category
- `ObjCModuleBuilderTests` — 7 tests verifying builder produces correct modules and emitted output
- All 10 existing ObjC test files refactored to use `using static ObjCTestHelpers`
- Net: -85 lines of duplicated boilerplate, +200 lines of reusable infrastructure

---

## P3: Low (Future / Deferred)

### 11. ObjC generic variance annotations

`__covariant` / `__contravariant` on ObjC lightweight generics are not handled. These don't affect the C# binding shape (C# generics are invariant), so this is informational only. Defer until a real framework demands it.

### 12. ClangAstParser extensibility

At 1,300+ lines with an 8-case switch, the parser is manageable today. If we add 5+ more node types, consider a handler registry. Not worth refactoring preemptively.

### 13. ObjCTypeMapper extensibility

Static dictionaries (133 entries across 3 maps) work well. An injectable registry would only matter for per-framework overrides, which we don't need. Revisit if custom SDK remaps become a requirement.

---

## Explicitly Not Doing

These were recommended by one or more reviewers and rejected after cross-analysis:

| Recommendation | Rejected Because |
|---|---|
| Roslyn SyntaxFactory for emission | Over-engineering. Attribute-heavy binding interfaces are well-served by StringBuilder. No evidence of malformed-output bugs. Sharpie used string emission. |
| Visitor pattern for ClangAstParser | 8-case switch is readable and maintainable. Visitor adds indirection without solving a real problem at current scale. |
| Full Strategy/Registry for ObjCTypeMapper | No current need for injectable overrides or per-framework customization. |
| Chain of Responsibility for nullability | `StripObjCMacros()` handles this fine as simple string replacements. |
| Centralized AvailabilityEmitter class | Availability logic is not duplicated between emitters (Grok's original claim was false). A small shared static helper for P1 item 5 is sufficient. |
| Parallel framework processing | Belongs at orchestration/build layer, not in the ObjC subsystem. |
| Auto-apply NS_SWIFT_NAME to C# names | Conflates Swift overlay intent with .NET binding design. Risks churn and divergence from MAUI/Xamarin conventions. Capture first, apply selectively after validation. |
| Auto-hide NS_REFINED_FOR_SWIFT APIs | No C# replacement exists in most libraries. Hiding valid APIs from consumers for a Swift-specific reason is wrong. Capture as metadata only. |

---

## Implementation History

All work completed across 13 commits:

| # | Item | Commit | Notes |
|---|------|--------|-------|
| 1 | Enum backing types | `694bde1` | |
| 2 | Diagnostic report | `be68573` | |
| 3 | Availability completion | `88a6bc8` | |
| 4 | NS_SWIFT_NAME capture | `2e8633c` | |
| 5 | NS_REFINED_FOR_SWIFT capture | `7455b66` | |
| 6 | Doc comment preservation | `d8a29d8` | |
| 7 | Test infrastructure | `cef5df1` | |
| 8 | Type IR Phase 1 | `1c4047f` | |
| 9 | Generic collection type hints | `ec0dd52` | |
| 10 | Struct layout safety | `dee5996` | |
| 11 | Codex bug fixes (3) | `012c999` | Generic misparsing, orphaned doc comments, enum typedef resolution |
| 12 | Context-aware generic parsing | `9685f93` | AST pre-scan for custom generic containers |
| 13 | Exception-safe cleanup | `6cf59d1` | try/finally for thread-static context |
| 14 | Nested nullability (7a+7b) | — | Depth-aware block/generic nullability, +2 validation improvements |

Final test counts: 556 ObjC tests, 6,328 unit tests project-wide, 88/88 validation targets passing.
