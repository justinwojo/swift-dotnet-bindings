# Consolidated Binding Gaps Analysis

This document consolidates binding issues discovered across multiple real-world Swift libraries (Lottie, BlinkID) and cross-references them with the Nuke binding roadmap to create a prioritized fix list.

**Date**: February 2026
**Last Updated**: February 2026 (Phase 30 - Generic Operator & Member Collision Fixes)
**Libraries Analyzed**: Lottie, BlinkID, Nuke
**Source Documents**:
- `BindingTesting/Lottie/BINDING_GAPS.md`
- `BindingTesting/BlinkId/BINDING_GAPS.md`
- `src/docs/nuke-binding-roadmap.md`

---

## Executive Summary

After 30 phases of development (29 Nuke phases + Phase 30 cross-library fixes), the binding generator handles most common Swift patterns. **Two critical issues were fixed in Phase 30:**

| Issue | Status | Errors Fixed |
|-------|--------|--------------|
| Operators on generic types (CS0563, CS0305) | ✅ **FIXED** | ~48 in BlinkID |
| Member name collisions (CS0542) | ✅ **FIXED** | Lottie edge cases |

**Current Compilation Status:**
- BlinkID: **0 errors** (down from 66)
- Lottie: **0 errors** (down from 41)
- Nuke: **0 errors** (maintained)

The remaining gap blocking full generic type support is:

1. **Generic types with DllImport** (CS7042) - C# forbids `[DllImport]` in generic classes

This accounts for **18 warnings** (properties skipped) in BlinkID's generic types.

---

## Issue Catalog

### Issue 1: Generic Type Classes with DllImport (CS7042)

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

#### Affected Types

| Library | Types | Error Count |
|---------|-------|-------------|
| Lottie | `Keyframe<T0>` | Multiple |
| BlinkID | `VehicleClassInfo<T0>`, `DateResult<T0>`, `DriverLicenseDetailedInfo<T0>` | 18 |

#### Root Cause

`MethodHandler.cs` emits P/Invoke declarations directly inside the type class without checking if the containing type is generic.

#### Solution: Factor P/Invoke to Non-Generic Helper Class

**Before** (broken):
```csharp
public class Keyframe<T0> : ISwiftObject where T0 : ISwiftObject
{
    [DllImport("Lottie", EntryPoint = "$s6Lottie8KeyframeV5valuexvg")]
    private static extern void PInvoke_value_Get(SwiftIndirectResult result, IntPtr self);

    public T0 Value
    {
        get
        {
            // ...
            PInvoke_value_Get(indirectResult, _payload.DangerousGetHandle());
            // ...
        }
    }
}
```

**After** (fixed):
```csharp
// Non-generic helper class for P/Invoke
internal static class Keyframe_PInvoke
{
    [DllImport("Lottie", EntryPoint = "$s6Lottie8KeyframeV5valuexvg")]
    internal static extern void PInvoke_value_Get(
        SwiftIndirectResult result,
        IntPtr self,
        TypeMetadata genericT0Metadata);  // Pass type metadata as parameter
}

public class Keyframe<T0> : ISwiftObject where T0 : ISwiftObject
{
    public T0 Value
    {
        get
        {
            var t0Metadata = SwiftObjectHelper<T0>.GetTypeMetadata();
            // ...
            Keyframe_PInvoke.PInvoke_value_Get(
                indirectResult,
                _payload.DangerousGetHandle(),
                t0Metadata);
            // ...
        }
    }
}
```

#### Files to Modify

| File | Changes Required |
|------|------------------|
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs` | Detect generic containing type, emit to helper class |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/NonFrozenStructHandler.cs` | Create `{TypeName}_PInvoke` helper class for generic types |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/FrozenStructHandler.cs` | Same pattern |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/ClassHandler.cs` | Same pattern |
| `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs` | Route P/Invoke through helper for generic types |

#### Implementation Strategy

1. Add `IsContainingTypeGeneric` check to `MethodHandler`
2. When true, collect P/Invoke declarations into a separate buffer
3. In type handlers (`NonFrozenStructHandler`, etc.), emit the helper class before the main class
4. Update P/Invoke calls to go through the helper class with type metadata parameters

#### Risks

- Requires passing `TypeMetadata` parameters through P/Invoke for generic type params
- Swift calling convention may need adjustment for metadata parameters
- Need to handle nested generic types carefully

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

### Issue 4: Swift Protocol Proxy Code Generation

**Severity**: MEDIUM
**Impact**: MEDIUM (8+ protocols in Lottie)
**Difficulty**: HIGH
**Libraries**: Lottie

#### Problem

The generated Swift code (`Swift.Lottie.swift`) for EveryProtocol conformances has multiple issues:

1. **Missing imports**: `CoreGraphics`, `CoreText`, `QuartzCore` not imported
2. **Invalid syntax**: `(any Any.Type).self` is not valid Swift
3. **Empty return types**: `public func value(frame: CoreGraphics.CGFloat) ->  {`
4. **Unresolved generics**: `τ_0_0` appears in method signatures

#### Affected Protocols (Lottie)

- `AnimationFontProvider`
- `AnimationTextProvider`
- `AnimationImageProvider`
- `TextContentsScaleProvider`
- `AnyValueProvider`
- `Interpolatable`
- `SpatialInterpolatable`
- `AnyInterpolatable`

#### Status

Already fixed for Nuke (8 protocols working). Lottie protocols have more complex scenarios:
- Protocols returning existential types
- Protocols with generic parameters in methods
- Protocols using framework types from CoreGraphics/CoreText

#### Files to Modify

| File | Changes |
|------|---------|
| `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` | Fix import detection, metatype syntax, return type handling |
| `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs` | Handle complex protocol signatures |

#### Implementation Notes

This is a larger effort that requires:
1. Analyzing the specific failing protocols
2. Adding module import detection for external frameworks
3. Fixing existential metatype emission (`(any Protocol).self` not `(any Any.Type).self`)
4. Handling generic type parameters in protocol method signatures

---

### Issue 5: AnyType in Generics

**Severity**: MEDIUM
**Impact**: MEDIUM (~20 properties skipped)
**Difficulty**: MEDIUM
**Libraries**: BlinkID

#### Problem

Properties using generic types with existential type arguments map to `AnyType`:

```swift
// Swift
var effectiveDate: DateResult<any SomeProtocol>?
var vehicleClassesInfo: [VehicleClassInfo<any SomeProtocol>]
```

```csharp
// Generated (skipped with warning)
// Property 'effectiveDate' skipped: type DateResult<Swift.AnyType> not supported
```

#### Root Cause

When `BoundGenericsHandler` encounters an existential type as a generic argument, it falls back to `AnyType` instead of using `ExistentialContainer{N}`.

#### Status

Partially addressed in Nuke phases. Existential types at the top level work, but not when nested inside generic type arguments.

#### Files to Modify

| File | Changes |
|------|---------|
| `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs` | Handle existentials as generic arguments |
| `src/Swift.Bindings/src/Marshaler/TypeConversionHandler.cs` | Support nested existentials |

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
| ~~**P1**~~ | ~~Operator Generation for Generic Types~~ | ✅ **DONE** | ~48 | 2-4 hours |
| ~~**P2**~~ | ~~Member Name Collision~~ | ✅ **DONE** | ~1 | 1-2 hours |
| **P3** | Generic Type DllImport | 🔲 Pending | ~18 warnings | 1-2 days |
| **P4** | Protocol Proxy Improvements | 🔲 Pending | ~8 protocols | 3-5 days |
| **P5** | AnyType in Generics | 🔲 Pending | ~20 warnings | 2-3 days |

### Completed (Phase 30)

1. ✅ **P1: Operator Generic Types** - Fixed in OperatorHandler, EqualityMethodsWriter, ClassEqualityMethodsWriter
2. ✅ **P2: Member Name Collision** - Fixed in NameProvider.GetPropertyName with containingTypeName parameter

### Remaining Roadmap

1. **P3: Generic DllImport** - Architectural change, but unblocks generic types
2. **P4: Protocol Proxy** - Complex, Lottie-specific benefit
3. **P5: AnyType in Generics** - Nice to have (depends on P3)

---

## What's Already Working (from Nuke + Phase 30)

After 30 phases of development, the following features work correctly:

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
| Protocol interfaces | ✅ | Including subscripts |
| Protocol proxies (non-generic) | ✅ | 8 protocols in Nuke |
| Native type remapping | ✅ | URL→NSUrl, Data→NSData |
| ObjC type bridging | ✅ | UIImage, URLResponse, etc. |
| Member name collision detection | ✅ | Auto-rename with `Value` suffix **[Phase 30]** |

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
