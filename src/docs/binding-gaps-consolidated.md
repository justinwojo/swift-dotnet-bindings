# Consolidated Binding Gaps Analysis

This document consolidates binding issues discovered across multiple real-world Swift libraries (Lottie, BlinkID) and cross-references them with the Nuke binding roadmap to create a prioritized fix list.

**Date**: February 2026
**Last Updated**: February 2026 (Phase 31 - Generic DllImport, Protocol Proxy & AnyType Fixes)
**Libraries Analyzed**: Lottie, BlinkID, Nuke
**Source Documents**:
- `BindingTesting/Lottie/BINDING_GAPS.md`
- `BindingTesting/BlinkId/BINDING_GAPS.md`
- `src/docs/nuke-binding-roadmap.md`

---

## Executive Summary

After 31 phases of development, the binding generator handles most common Swift patterns. **Five critical issues have been fixed in Phase 30 and Phase 31:**

| Issue | Status | Errors/Warnings Fixed |
|-------|--------|----------------------|
| Operators on generic types (CS0563, CS0305) | ✅ **FIXED** (Phase 30) | ~48 in BlinkID |
| Member name collisions (CS0542) | ✅ **FIXED** (Phase 30) | Lottie edge cases |
| Generic types with DllImport (CS7042) | ✅ **FIXED** (Phase 31) | ~18 warnings in BlinkID |
| Protocol proxy generation issues | ✅ **FIXED** (Phase 31) | ~8 protocols in Lottie |
| AnyType in generic arguments | ✅ **FIXED** (Phase 31) | ~20 properties in BlinkID |

**Current Compilation Status:**
- BlinkID: **0 errors, minimal warnings**
- Lottie: **0 errors** (protocol proxies now generate correctly)
- Nuke: **0 errors** (maintained)

**Phase 31 Fixes:**
1. **Generic DllImport (P3)**: P/Invoke declarations now emitted to non-generic helper classes (`{TypeName}_PInvoke`)
2. **Protocol Proxy (P4)**: Fixed empty return types, unresolved generics (τ_0_0), metatype syntax, and framework imports
3. **AnyType in Generics (P5)**: Generic type parameters now correctly distinguished from existential types

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

## Prioritized Implementation Roadmap

| Priority | Issue | Status | Errors Fixed | Effort |
|----------|-------|--------|--------------|--------|
| ~~**P1**~~ | ~~Operator Generation for Generic Types~~ | ✅ **DONE** (Phase 30) | ~48 | 2-4 hours |
| ~~**P2**~~ | ~~Member Name Collision~~ | ✅ **DONE** (Phase 30) | ~1 | 1-2 hours |
| ~~**P3**~~ | ~~Generic Type DllImport~~ | ✅ **DONE** (Phase 31) | ~18 warnings | 1-2 days |
| ~~**P4**~~ | ~~Protocol Proxy Improvements~~ | ✅ **DONE** (Phase 31) | ~8 protocols | 3-5 days |
| ~~**P5**~~ | ~~AnyType in Generics~~ | ✅ **DONE** (Phase 31) | ~20 properties | 2-3 days |

### Completed (Phase 30)

1. ✅ **P1: Operator Generic Types** - Fixed in OperatorHandler, EqualityMethodsWriter, ClassEqualityMethodsWriter
2. ✅ **P2: Member Name Collision** - Fixed in NameProvider.GetPropertyName with containingTypeName parameter

### Completed (Phase 31)

3. ✅ **P3: Generic DllImport** - Implemented PInvokeHelperContext for factoring P/Invoke to non-generic helper classes
4. ✅ **P4: Protocol Proxy** - Fixed EveryProtocolEmitter (empty returns, generics, metatypes) and ModuleHandler (framework imports)
5. ✅ **P5: AnyType in Generics** - Added TypeSpecHelpers.IsGenericTypeParameter() and updated TypeDatabaseExtensions

### Completed (Phase 32)

6. ✅ **P6: Optional-wrapped existentials** - Fixed TypeSpecParser to not propagate `IsAny` flag to Optional wrappers. Properties like `(any DataCaching)?` now correctly generate accessor methods with `ExistentialContainer?` type.

### Remaining Known Issues

- **Async properties** - Properties with async getters/setters not yet supported
- **Actors** - Swift actors not yet supported

---

## What's Already Working (from Nuke + Phase 30 + Phase 31)

After 31 phases of development, the following features work correctly:

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

---

## Test Validation Commands

After implementing fixes, validate with:

```bash
# Run all generator tests
./run-tests.sh

# Regenerate Lottie bindings
cd BindingTesting/Lottie
./regenerate-bindings.sh

# Check compilation errors
dotnet build output-ios/Swift.Lottie.csproj 2>&1 | grep -c "error CS"

# Regenerate BlinkID bindings
cd BindingTesting/BlinkId
./regenerate-bindings.sh

# Check compilation errors
dotnet build output-ios/Swift.BlinkId.csproj 2>&1 | grep -c "error CS"
```

---

## References

- `/north-star.md` - Project vision and roadmap
- `/src/docs/emitter-redesign-proposal.md` - Emitter architecture
- `/src/docs/nuke-binding-roadmap.md` - Detailed phase history
- `/CLAUDE.md` - Project overview and structure
