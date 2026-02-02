# Consolidated Binding Gaps Analysis

This document consolidates binding issues discovered across multiple real-world Swift libraries (Lottie, BlinkID) and cross-references them with the Nuke binding roadmap to create a prioritized fix list.

**Date**: February 2026
**Last Updated**: February 2026 (Phase 39 - Existential Constraint Relaxation)
**Libraries Analyzed**: Lottie, BlinkID, Nuke
**Source Documents**:
- `BindingTesting/Lottie/BINDING_GAPS.md`
- `BindingTesting/BlinkId/BINDING_GAPS.md`
- `src/docs/nuke-binding-roadmap.md`

---

## Executive Summary

After 39 phases of development, the binding generator handles most common Swift patterns. **Fourteen improvements have been made in Phases 30-39:**

| Issue | Status | Errors/Warnings Fixed |
|-------|--------|----------------------|
| Operators on generic types (CS0563, CS0305) | ✅ **FIXED** (Phase 30) | ~48 in BlinkID |
| Member name collisions (CS0542) | ✅ **FIXED** (Phase 30) | Lottie edge cases |
| Generic types with DllImport (CS7042) | ✅ **FIXED** (Phase 31) | ~18 warnings in BlinkID |
| Protocol proxy generation issues | ✅ **FIXED** (Phase 31) | ~8 protocols in Lottie |
| AnyType in generic arguments | ✅ **FIXED** (Phase 31) | ~20 properties in BlinkID |
| Optional-wrapped existentials | ✅ **FIXED** (Phase 32) | Properties with `(any Protocol)?` |
| Generic type internal references (CS0305) | ✅ **FIXED** (Phase 33) | ~6 in BlinkID |
| Paired operator synthesis (CS0216) | ✅ **FIXED** (Phase 34) | Lottie operators |
| Duplicate enum members (CS0102) | ✅ **FIXED** (Phase 34) | Lottie enums |
| Generic enum type parameters (CS0308) | ✅ **FIXED** (Phase 35) | 10 errors in Lottie |
| SwiftUI constraint handling (CS0246, CS0314) | ✅ **FIXED** (Phase 36) | 14 errors in Lottie |
| Binding completeness report | ✅ **ADDED** (Phase 37) | DX improvement |
| UnsupportedType placeholder attributes | ✅ **ADDED** (Phase 38) | DX improvement |
| Existential constraint relaxation (CS0315) | ✅ **FIXED** (Phase 39) | 1 error in Lottie |

**Current Compilation Status:**
- BlinkID: **0 errors** ✅
- Lottie: **11 errors** (down from 12)
- Nuke: **0 errors** (maintained)

**Phase 39 Improvements:**
1. **Existential Constraint Relaxation**: Members using bound generics with existential type arguments are now skipped with `UnsupportedExistential` reason instead of generating CS0315 errors. Existential args translate to `AnyType` for consistent interface/proxy signatures.

**Lottie Coverage (Phase 39)**:
- Types: 79 emitted, 1 skipped (84.9% coverage)
- Members: 372 emitted, 56 skipped, 268 synthesized (61.1% coverage)

**Remaining Lottie Errors (11 total):**
- CS0311 (10): Generic constraint not satisfied - types like `LottieVector3D` don't implement `ISwiftAnyInterpolatable`
- CS0738 (1): Protocol interface mismatch (`AnyValueProviderProxy`)

**All 7 Codex Tasks Completed.** Remaining errors require deeper architectural changes (protocol conformance emission, constraint relaxation).

---

## Issue Catalog

### Issue 1: Generic Type Classes with DllImport (CS7042) ✅ FIXED

**Status**: ✅ **FIXED in Phase 31** (February 2026)
**Severity**: CRITICAL
**Impact**: HIGH (blocks entire type hierarchies)
**Difficulty**: MEDIUM
**Libraries**: Lottie, BlinkID

#### Problem

C# doesn't allow `[DllImport]` attributes on methods inside generic types:

```csharp
// This is invalid C# - causes CS7042
public class Keyframe<T0> : ISwiftObject
{
    [DllImport("Lottie", EntryPoint = "...")]
    private static extern void PInvoke_GetValue(...);  // CS7042!
}
```

#### Solution Implemented

Created a new `PInvokeHelperContext` system that factors P/Invoke declarations into non-generic helper classes:

**Files Modified:**
- **NEW** `src/Swift.Bindings/src/Emitter/StringEmitter/PInvokeHelperEmitter.cs` - `PInvokeHelperContext` and `PInvokeDeclaration` classes
- `src/Swift.Bindings/src/Marshaler/IEnvironment.cs` - Added `PInvokeHelperContext` to `MethodEnvironment`
- `src/Swift.Bindings/src/Marshaler/Conductor.cs` - Added `CurrentPInvokeHelperContext` property
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - P/Invoke now collected to context for generic types
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` - Creates helper context, emits helper class
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` - Same pattern
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` - Same pattern
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` - Updated to accept context
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/OperatorHandler.cs` - Same pattern for operators

**Result:**
```csharp
// Non-generic helper class for P/Invoke
internal static class Keyframe_PInvoke
{
    [DllImport("Lottie", EntryPoint = "$s6Lottie8KeyframeV5valuexvg")]
    internal static extern void PInvoke_value_Get(SwiftIndirectResult result, IntPtr self);
}

public class Keyframe<T0> : ISwiftObject where T0 : ISwiftObject
{
    public T0 Value
    {
        get { Keyframe_PInvoke.PInvoke_value_Get(...); }
    }
}
```

**BlinkID: 18 CS7042 warnings eliminated**

---

### Issue 2: Operator Generation for Generic Types (CS0563, CS0305) ✅ FIXED

**Status**: ✅ **FIXED in Phase 30** (February 2026)
**Severity**: HIGH
**Impact**: HIGH (48+ errors in BlinkID alone)
**Difficulty**: LOW
**Libraries**: Lottie, BlinkID

#### Problem

Operators on generic types referenced the non-generic type name:

```csharp
// Generated (broken - BEFORE fix):
public class DateResult<T0> : ISwiftObject
{
    // CS0305: Using generic type 'DateResult<T0>' requires 1 type arguments
    public static bool operator ==(DateResult left, DateResult right)  // Missing <T0>!
}
```

#### Error Counts (Before Fix)

| Library | CS0563 | CS0305 | Total |
|---------|--------|--------|-------|
| BlinkID | 12 | 36 | 48 |
| Lottie | - | Multiple | Multiple |

#### Solution Implemented

The fix involved updating multiple files to use `GenericTypeEmitter.GetTypeNameWithGenerics()`:

1. **`OperatorHandler.cs`**: Updated `EmitOperator()` and `EmitOperatorWrapper()` to compute and use `typeNameWithGenerics`. Added `FixGenericTypeName()` helper to replace base type names with generic versions.

2. **`TypeHandlerHelpers.cs`** (`EqualityMethodsWriter`): Added `_typeNameWithGenerics` field and updated all operator emissions to use it.

3. **`ClassHandler.cs`** (`ClassEqualityMethodsWriter`): Same pattern for class types.

4. **Type handlers** (`NonFrozenStructHandler.cs`, `FrozenStructHandler.cs`, `ClassHandler.cs`): Updated `ValidateAndEmitPairs()` calls to pass `typeNameWithGenerics`.

#### Result

```csharp
// Generated (AFTER fix):
public class DateResult<T0> : ISwiftObject where T0 : ISwiftObject
{
    public static bool operator ==(DateResult<T0> left, DateResult<T0> right)  // ✅ Correct!
    public static bool operator !=(DateResult<T0> left, DateResult<T0> right)  // ✅ Correct!
}
```

**BlinkID: 0 compilation errors** | **Lottie: 0 compilation errors**

---

### Issue 3: Member Names Matching Enclosing Type (CS0542) ✅ FIXED

**Status**: ✅ **FIXED in Phase 30** (February 2026)
**Severity**: MEDIUM
**Impact**: LOW (rare occurrence)
**Difficulty**: LOW
**Libraries**: Lottie

#### Problem

Swift allows a property to have the same name as the containing type or a nested type:

```swift
// Swift (valid)
class DotLottieFile {
    struct Animation { ... }
    var Animation: Animation? { get }  // Property named same as nested type
}
```

C# does not allow this (CS0542).

#### Solution Implemented

Updated `NameProvider.GetPropertyName()` to accept a `containingTypeName` parameter and check for collisions:

```csharp
public static string GetPropertyName(string swiftPropertyName,
    IReadOnlySet<string>? siblingNestedTypeNames = null,
    string? containingTypeName = null)
{
    var pascalName = ToPascalCase(swiftPropertyName);

    // Check collision with containing type name (CS0542)
    if (!string.IsNullOrEmpty(containingTypeName) && pascalName == containingTypeName)
        return $"{pascalName}Value";

    // Check collision with nested types
    if (siblingNestedTypeNames != null && siblingNestedTypeNames.Contains(pascalName))
        return $"{pascalName}Value";

    return pascalName;
}
```

Updated callers in `PropertyHandler.cs`, `AsyncStreamEmitter.cs`, and all type handlers to pass the containing type name.

#### Result

Properties that would collide with their containing type or sibling nested types are now automatically renamed with a `Value` suffix.

---

### Issue 4: Swift Protocol Proxy Code Generation ✅ FIXED

**Status**: ✅ **FIXED in Phase 31** (February 2026)
**Severity**: MEDIUM
**Impact**: MEDIUM (8+ protocols in Lottie)
**Difficulty**: HIGH
**Libraries**: Lottie

#### Problem

The generated Swift code (`Swift.Lottie.swift`) for EveryProtocol conformances had multiple issues:

1. **Missing imports**: `CoreGraphics`, `CoreText`, `QuartzCore` not imported
2. **Invalid syntax**: `(any Any.Type).self` is not valid Swift
3. **Empty return types**: `public func value(frame: CoreGraphics.CGFloat) ->  {`
4. **Unresolved generics**: `τ_0_0` appears in method signatures

#### Solution Implemented

**Files Modified:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs`:
  - Added `ProtocolListTypeSpec` handling (empty → "Any", single/multiple protocols)
  - Added generic type parameter detection using `TypeSpecHelpers.IsGenericTypeParameter()` (τ_0_0, T, Element → "Any")
  - Fixed metatype syntax: `any Any.Type` → `Any.Type`

- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ModuleHandler.cs`:
  - Expanded `AppleFrameworks` set to include CoreGraphics, CoreText, QuartzCore, CoreFoundation, and 40+ other frameworks
  - Added `ScanProtocolsForFrameworkImports()` to scan protocol members
  - Added `ScanTypeSpecForImports()` for recursive type scanning

**Result:**
```swift
// BEFORE (broken):
public func value(frame: CoreGraphics.CGFloat) ->  {  // Empty return type!
public var valueType: any Any.Type { ... }
public func interpolate(to: τ_0_0, amount: ...) -> τ_0_0  // Unresolved generic

// AFTER (fixed):
import CoreGraphics
import CoreText
import QuartzCore
public func value(frame: CoreGraphics.CGFloat) -> Any {
public var valueType: Any.Type { ... }
public func interpolate(to: Any, amount: ...) -> Any
```

**Lottie: All protocol proxies now generate valid Swift code**

---

### Issue 5: AnyType in Generics ✅ FIXED

**Status**: ✅ **FIXED in Phase 31** (February 2026)
**Severity**: MEDIUM
**Impact**: MEDIUM (~20 properties skipped)
**Difficulty**: MEDIUM
**Libraries**: BlinkID

#### Problem

Generic type parameters (τ_0_0, T, Element, etc.) were incorrectly classified as existential types, causing them to fall back to `AnyType` and getting skipped.

#### Root Cause

`TypeDatabaseExtensions.IsExistentialTypeName()` returned `true` for ANY type without a module qualifier:
```csharp
// Old code (broken):
if (!typeSpec.HasModule() && typeSpec.Name != "Swift.Any" && ...)
    return true;  // Catches generic params like "StringType"!
```

#### Solution Implemented

**Files Created/Modified:**
- **NEW** `src/Swift.Bindings/src/Model/TypeSpec/TypeSpecHelpers.cs` - Shared `IsGenericTypeParameter()` utility
- `src/Swift.Bindings/src/TypeDatabase/TypeDatabaseExtensions.cs`:
  - Added generic type parameter check in `IsExistentialTypeName()` to not misclassify them
  - Added checks in `GetTypeRecordOrAnyType()`, `TryGetTypeRecord()`, `GetTypeRecordOrThrow()` to return `AnyType` for generic params
- `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs` - Now delegates to shared helper
- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` - Now delegates to shared helper
- **NEW** `src/Swift.Bindings/tests/UnitTests/TypeSpecTests/TypeSpecHelpersTests.cs` - 32 tests for the new helper

**Result:**
```csharp
// Generic type parameters now correctly identified and handled:
// τ_0_0, τ_0_1, T, U, V, Element, Key, Value, Index, Result, etc.
// → Return AnyType instead of crashing with "Invalid module-qualified name"
```

**BlinkID: Properties with generic type parameters now handled gracefully**

---

### Issue 6: Async Return Types (Non-ObjC Representable)

**Severity**: MEDIUM
**Impact**: MEDIUM (some async methods)
**Difficulty**: MEDIUM
**Libraries**: Lottie

#### Problem

Async callbacks require `@convention(c)` which needs ObjC-representable types:

```swift
// Error: '(DotLottieFile, Int64) -> Void' is not representable in Objective-C
```

#### Status

Workaround exists from Phase 29: Use Swift wrapper functions that handle the non-ObjC types internally.

---

### Issue 7: C++ Interop Symbols (Cxx Module)

**Severity**: LOW
**Impact**: LOW
**Difficulty**: N/A (Out of Scope)
**Libraries**: BlinkID

#### Status

Already handled gracefully - demangler catches exceptions and skips with warning.

---

### Issue 8: Concurrency Types (UnownedSerialExecutor)

**Severity**: LOW
**Impact**: LOW
**Difficulty**: HIGH
**Libraries**: BlinkID

#### Status

Actors not supported. Properties using `_Concurrency.UnownedSerialExecutor` are skipped with warning.

---

### Issue 9: Generic Type Internal References (CS0305) ✅ FIXED

**Status**: ✅ **FIXED in Phase 33** (February 2026)
**Severity**: HIGH
**Impact**: HIGH (blocks generic type compilation)
**Difficulty**: MEDIUM
**Libraries**: BlinkID

#### Problem

Inside generic types like `DateResult<T0>`, internal type references used the bare type name instead of the generic version:

```csharp
// Generated (broken - BEFORE fix):
public class DateResult<T0> : ISwiftObject where T0 : ISwiftObject
{
    // CS0305: Using generic type 'DateResult<T0>' requires 1 type arguments
    static nuint _payloadSize = SwiftObjectHelper<DateResult>.GetTypeMetadata().Size;  // Missing <T0>!
    SwiftSafeHandle<DateResult> _payload = SwiftSafeHandle<DateResult>.Zero;  // Missing <T0>!
}
```

#### Solution Implemented

Updated multiple emitter files to pass and use `typeNameWithGenerics`:

**Files Modified:**
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` - `WritePrivateFields()` and `WritePayload()` now accept `typeNameWithGenerics`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` - Same pattern for classes, plus `ClassISwiftObjectMethodWriter` updated
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` - Reordered code to compute `typeNameWithGenerics` before use
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandlerHelpers.cs` - `ISwiftObjectMethodWriter` now accepts and uses `_typeNameWithGenerics`
- `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` - `EmitPInvokeCall()` now uses helper class prefix and metadata parameters

#### Result

```csharp
// Generated (AFTER fix):
public class DateResult<T0> : ISwiftObject where T0 : ISwiftObject
{
    static nuint _payloadSize = SwiftObjectHelper<DateResult<T0>>.GetTypeMetadata().Size;  // ✅ Correct!
    SwiftSafeHandle<DateResult<T0>> _payload = SwiftSafeHandle<DateResult<T0>>.Zero;  // ✅ Correct!

    // P/Invoke calls now use helper class:
    var result = DateResult_PInvoke.PInvoke_day_Get(self, SwiftObjectHelper<T0>.GetTypeMetadata());  // ✅
}
```

**BlinkID: 0 compilation errors** (down from 6 CS0305 errors)

---

### Issue 10: Lottie-Specific Issues (Remaining)

**Status**: ⚠️ **NOT FIXED** (different category of issues)
**Severity**: MEDIUM
**Impact**: MEDIUM (21 errors in Lottie)
**Libraries**: Lottie only

#### Problems

These issues are specific to Lottie's use of SwiftUI and complex type patterns:

1. **CS0246: ISwiftView not found** - SwiftUI `View` protocol not defined in bindings
2. **CS0102: Duplicate member** - `LottiePlaybackMode.Paused` defined twice (enum case collision)
3. **CS0308: Non-generic type with generics** - `ValueProviderStorage` used as `ValueProviderStorage<T>` but emitted as non-generic
4. **CS0216: Missing paired operator** - `Keyframe<T0>.!=` without `==` (the `==` signature was unsupported)
5. **CS0314/CS0315: Type parameter constraints** - Generic constraints don't match (e.g., `ExistentialContainer0` vs `ISwiftObject`)

#### Status

These require deeper investigation into:
- SwiftUI protocol support
- Enum case deduplication
- Generic type detection for bound generics
- Operator signature validation before synthesis

#### Investigation Notes + Potential Fixes

Below are concrete findings from the generated Lottie bindings (`BindingTesting/Lottie/output-ios/Swift.Lottie.cs`) and the likely changes needed to address the remaining 21 errors.

1) **SwiftUI constraint (`ISwiftView`)**
   - **Observed**: `LottieView<T0>` uses `where T0 : ISwiftObject, ISwiftView`, but `ISwiftView` is not generated.
   - **Likely root**: SwiftUI protocols (e.g., `SwiftUI.View`) are not emitted as C# interfaces; many are PATs / opaque result types.
   - **Potential fixes**:
     - **Short-term**: Skip type emission when a generic constraint references SwiftUI protocols (or any protocol marked unsupported). Emit a warning and prune the type to keep compilation green.
     - **Medium-term**: Emit *stub* interfaces for SwiftUI protocols (e.g., `ISwiftView`) in a dedicated SwiftUI namespace so constraints compile. Keep members skipped to avoid false capability.
     - **Long-term**: Add SwiftUI support (opaque result types + PAT strategy). This is a larger design effort.
   - **Likely code touch points**: `GenericTypeEmitter.GetWhereClause`, `ModuleProcessor` pruning rules.

2) **Duplicate enum case symbol (`Paused`)**
   - **Observed**: `LottiePlaybackMode` has both `public static LottiePlaybackMode Paused(...)` (case ctor) and `public static LottiePlaybackMode Paused { get; }` (static property), causing CS0102.
   - **Likely root**: EnumHandler emits both case constructors (associated values) and static case properties when a getter symbol exists, without deduping names.
   - **Potential fixes**:
     - Suppress static property emission when a case constructor with the same name already exists.
     - Or rename the property (e.g., `PausedCase` / `PausedValue`) via `NameProvider` collision logic.
   - **Likely code touch points**: `EnumHandler.cs` (case emission + static property emission).

3) **`ValueProviderStorage` generic type missing**
   - **Observed**: `ValueProviderStorage` is emitted non-generic, but is referenced as `ValueProviderStorage<T>` in multiple places (CS0308).
   - **Likely root**: Generic parameters on the enum type are not being propagated to the type declaration. The ABI signatures contain generic params (`x`) but the type is emitted non-generic.
   - **Potential fixes**:
     - Propagate generic parameters from the ABI type declaration into `TypeDecl.GenericParameters` for enums (and/or infer generics from member signatures when missing).
     - As a fallback, when a bound generic usage is detected for a non-generic type, force the type declaration to be generic in the emitter.
     - Optional: map `ValueProviderStorage<T>` to a type-erased `AnyValueProviderStorage` when `T` is existential (more correct when used with protocol values).
   - **Likely code touch points**: `SwiftABIParser.cs` (generic param parsing), `ModuleProcessor` or `TypeDecl` construction, `EnumHandler`/`GenericTypeEmitter`.

4) **Paired operator synthesis when primary operator is skipped**
   - **Observed**: `Keyframe<T0>.operator !=` emitted even though `==` is not (CS0216).
   - **Likely root**: Pair synthesis runs even if the source operator didn’t emit due to unsupported signature.
   - **Potential fixes**:
     - Track per-operator emission success; only synthesize pairs if the primary operator was successfully emitted.
   - **Likely code touch points**: `OperatorHandler.cs`, `TypeHandlerHelpers.cs`.

5) **Generic constraint mismatch with existentials (`ExistentialContainer0`)**
   - **Observed**: `Keyframe<ExistentialContainer0>` fails because `Keyframe<T0>` is constrained to `ISwiftObject`, and existential containers don’t implement it (CS0315).
   - **Likely root**: `GenericTypeEmitter` unconditionally adds `ISwiftObject` constraint to all generic params, even when Swift constraints are protocol-only and are represented as existentials on the C# side.
   - **Potential fixes**:
     - **Structural fix**: Add a metadata path for existentials so generic types can accept `IExistentialContainer` (or similar) instead of `ISwiftObject`, and adjust emitted metadata lookups accordingly.
     - **Pragmatic fix**: When a generic argument resolves to an existential container, route to a type-erased companion (e.g., `AnyValueProviderStorage`) rather than instantiating `Keyframe<ExistentialContainer0>`.
     - **Fallback**: Relax the `ISwiftObject` constraint for specific generic parameters that are only constrained to unsupported protocols, but only if the generated code path does not require `SwiftObjectHelper<T0>`.
   - **Likely code touch points**: `GenericTypeEmitter.GetWhereClause`, `TypeMetadata` helper usage in generic emission paths, `BoundGenericsHandler`.

6) **Associated-value case treated as no-arg property (related to #3)**
   - **Observed**: `ValueProviderStorage.Closure` is emitted as a no-arg property even though the Swift case expects an associated closure.
   - **Likely root**: Generic parameter / associated value typing was lost, so the emitter treated the case as payload-less.
   - **Potential fixes**:
     - Same fix as #3 (correctly carrying generic parameters into the enum declaration) should allow the associated-value case to emit the proper signature.
   - **Likely code touch points**: `EnumHandler.cs` (case signature generation), `SwiftABIParser.cs`.

---

## Prioritized Implementation Roadmap

| Priority | Issue | Status | Errors Fixed | Effort |
|----------|-------|--------|--------------|--------|
| ~~**P1**~~ | ~~Operator Generation for Generic Types~~ | ✅ **DONE** (Phase 30) | ~48 | 2-4 hours |
| ~~**P2**~~ | ~~Member Name Collision~~ | ✅ **DONE** (Phase 30) | ~1 | 1-2 hours |
| ~~**P3**~~ | ~~Generic Type DllImport~~ | ✅ **DONE** (Phase 31) | ~18 warnings | 1-2 days |
| ~~**P4**~~ | ~~Protocol Proxy Improvements~~ | ✅ **DONE** (Phase 31) | ~8 protocols | 3-5 days |
| ~~**P5**~~ | ~~AnyType in Generics~~ | ✅ **DONE** (Phase 31) | ~20 properties | 2-3 days |
| ~~**P6**~~ | ~~Optional-wrapped existentials~~ | ✅ **DONE** (Phase 32) | ~5 properties | 1 hour |
| ~~**P7**~~ | ~~Generic Type Internal References~~ | ✅ **DONE** (Phase 33) | ~6 in BlinkID | 2-3 hours |

### Completed (Phase 30)

1. ✅ **P1: Operator Generic Types** - Fixed in OperatorHandler, EqualityMethodsWriter, ClassEqualityMethodsWriter
2. ✅ **P2: Member Name Collision** - Fixed in NameProvider.GetPropertyName with containingTypeName parameter

### Completed (Phase 31)

3. ✅ **P3: Generic DllImport** - Implemented PInvokeHelperContext for factoring P/Invoke to non-generic helper classes
4. ✅ **P4: Protocol Proxy** - Fixed EveryProtocolEmitter (empty returns, generics, metatypes) and ModuleHandler (framework imports)
5. ✅ **P5: AnyType in Generics** - Added TypeSpecHelpers.IsGenericTypeParameter() and updated TypeDatabaseExtensions

### Completed (Phase 32)

6. ✅ **P6: Optional-wrapped existentials** - Fixed TypeSpecParser to not propagate `IsAny` flag to Optional wrappers. Properties like `(any DataCaching)?` now correctly generate accessor methods with `ExistentialContainer?` type.

### Completed (Phase 33)

7. ✅ **P7: Generic Type Internal References** - Fixed internal type references (`SwiftObjectHelper<>`, `SwiftSafeHandle<>`, `_payloadSize`, `_payload`) to use `typeNameWithGenerics`. Fixed P/Invoke call sites to use helper class prefix and metadata parameters.

### Completed (Phase 34)

8. ✅ **P8: Paired Operator Synthesis Validation (CS0216)** - `EmitOperator()` now returns success/failure, `ValidateAndEmitPairs()` only synthesizes pairs from actually emitted operators. Prevents CS0216 errors when primary operator has unsupported signature.

9. ✅ **P9: Duplicate Enum Member Deduplication (CS0102)** - `EnumHandler` now tracks emitted case constructor names and skips static properties that would collide.

### Remaining Known Issues

- **SwiftUI types** - `View` protocol and related types not yet supported (CS0246, CS0314)
- **Generic enum types** - `ValueProviderStorage<T>` emitted non-generic (CS0308)
- **Existential in generic constraints** - `ExistentialContainer0` vs `ISwiftObject` (CS0315)
- **Async properties** - Properties with async getters/setters not yet supported
- **Actors** - Swift actors not yet supported
- **Lottie-specific issues** - See Issue 10 for details (19 errors remaining, down from 21)

---

## What's Already Working (from Nuke + Phases 30-33)

After 33 phases of development, the following features work correctly:

| Feature | Status | Notes |
|---------|--------|-------|
| Classes, structs (frozen/non-frozen) | ✅ | Full support |
| Instance/static methods | ✅ | Full support |
| Property getters and setters | ✅ | Full support |
| Async methods | ✅ | Via Swift wrapper pattern |
| Closures (escaping, convention(c)) | ✅ | Including bound generics |
| Throwing closures | ✅ | Maps to `SwiftResult<T, SwiftError>` |
| Async+throwing closures | ✅ | Via continuation wrapper |
| Tuples (1-7 elements) | ✅ | With runtime marshalling |
| Operators (all types including generic) | ✅ | With pair synthesis, generic type params **[Phase 30]** |
| Enums (RawRepresentable) | ✅ | Frozen and non-frozen |
| Enum associated values | ✅ | `TryGet` extraction |
| Existential types (`any Protocol`) | ✅ | `ExistentialContainer{N}` |
| Generic methods | ✅ | With where clauses |
| Generic types with P/Invoke | ✅ | Via helper classes **[Phase 31]** |
| Protocol interfaces | ✅ | Including subscripts |
| Protocol proxies (all types) | ✅ | 8 protocols in Nuke, 8+ in Lottie **[Phase 31]** |
| Native type remapping | ✅ | URL→NSUrl, Data→NSData |
| ObjC type bridging | ✅ | UIImage, URLResponse, etc. |
| Member name collision detection | ✅ | Auto-rename with `Value` suffix **[Phase 30]** |
| Framework import detection | ✅ | CoreGraphics, CoreText, 40+ frameworks **[Phase 31]** |
| Generic type parameter handling | ✅ | τ_0_0, T, Element → AnyType **[Phase 31]** |
| Generic type internal references | ✅ | `SwiftObjectHelper<T>`, `SwiftSafeHandle<T>` **[Phase 33]** |
| Generic P/Invoke call sites | ✅ | Helper class prefix + metadata params **[Phase 33]** |

---

## Test Validation Commands

After implementing fixes, validate with:

```bash
# Run all generator tests
./run-tests.sh

# Regenerate and test Lottie bindings
cd BindingTesting/Lottie
./regenerate-bindings.sh
dotnet build LottieTestApp/LottieTestApp.csproj 2>&1 | grep -c "error CS"

# Regenerate and test BlinkID bindings
cd BindingTesting/BlinkId
./regenerate-bindings.sh
dotnet build BlinkIdTestApp/BlinkIdTestApp.csproj 2>&1 | grep -c "error CS"
```

---

## References

- `/north-star.md` - Project vision and roadmap
- `/src/docs/emitter-redesign-proposal.md` - Emitter architecture
- `/src/docs/nuke-binding-roadmap.md` - Detailed phase history
- `/CLAUDE.md` - Project overview and structure
