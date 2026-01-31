# Phase 21: Generic Type Parameter & Swift Wrapper Fixes

**Status**: COMPLETED (2026-01-31)

Bug fixes discovered during routine validation testing. These issues were causing generator crashes and Swift wrapper compilation failures.

## Summary

- 21.1 TypeConversionHandler Generic Parameter Crash → COMPLETED
- 21.2 EveryProtocolEmitter Closure Type Rendering → COMPLETED
- 21.3 EveryProtocolEmitter Existential Metatype Syntax → COMPLETED

---

## 21.1 TypeConversionHandler Generic Parameter Crash

**Priority**: High (blocking integration tests)
**Status**: COMPLETED

### Problem

The generator crashed with `ArgumentException: Invalid module-qualified name: τ_0_0` when processing async methods with generic type parameters.

**Stack trace**:
```
System.ArgumentException: Invalid module-qualified name: τ_0_0
   at BindingsGeneration.SwiftTypeName.FromModuleQualifiedName(String moduleQualifiedName)
   at BindingsGeneration.SwiftTypeName.FromTypeSpec(NamedTypeSpec namedTypeSpec)
   at BindingsGeneration.TypeConversionHandler.IsSwiftString(TypeSpec typeSpec)
   at BindingsGeneration.TypeConversionHandler.GetIdiomaticCSharpType(...)
   at BindingsGeneration.WrapperSignatureBuilder.HandleArguments()
```

### Root Cause

`TypeConversionHandler.IsSwiftString()`, `IsSwiftArray()`, and `IsSwiftOptional()` called `SwiftTypeName.FromTypeSpec()` on all `NamedTypeSpec` instances. However, generic type parameters like `τ_0_0` (Swift's internal representation) or `T` don't have module qualifiers, causing `FromModuleQualifiedName()` to throw because it requires at least two dot-separated components.

### Solution

Added early return checks for types without module qualifiers:

```csharp
public bool IsSwiftString(TypeSpec? typeSpec)
{
    if (typeSpec is not NamedTypeSpec namedTypeSpec)
        return false;

    // Generic type parameters (e.g., τ_0_0, T) don't have a module qualifier
    if (!namedTypeSpec.HasModule())
        return false;

    var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
    return typeName.Equals(SwiftStringTypeName);
}
```

Same fix applied to `IsSwiftArray()` and `IsSwiftOptional()`.

### Files Modified

- `src/Swift.Bindings/src/Marshaler/TypeConversionHandler.cs` - Added `HasModule()` checks to all three methods

---

## 21.2 EveryProtocolEmitter Closure Type Rendering

**Priority**: High (blocking Swift wrapper compilation)
**Status**: COMPLETED

### Problem

Swift wrapper compilation failed with syntax errors for protocols containing closure parameters:

```
Swift.Nuke.swift:155:132: error: single argument function types require parentheses
Swift.Nuke.swift:155:137: error: optional 'any' type must be written 'Optional<any Error>'
```

Generated code:
```swift
completion: any (any Swift.Optional<any Swift.Error>) -> ()
```

### Root Cause

`EveryProtocolEmitter.GetSwiftTypeName()` didn't have a case for `ClosureTypeSpec`, so it fell through to `typeSpec.ToString()`. The `ToString()` method includes the `IsAny` flag in its output, producing malformed Swift like `any (...)` for closure types.

### Solution

Added explicit handling for `ClosureTypeSpec` in `GetSwiftTypeName()`:

```csharp
if (typeSpec is ClosureTypeSpec closureType)
{
    var argsString = GetSwiftTypeName(closureType.Arguments);
    if (closureType.Arguments is not TupleTypeSpec)
    {
        argsString = $"({argsString})";
    }
    var returnString = GetSwiftTypeName(closureType.ReturnType);
    if (closureType.ReturnType.IsEmptyTuple)
    {
        returnString = "Void";
    }

    var throwsKeyword = closureType.Throws ? " throws" : "";
    var asyncKeyword = closureType.IsAsync ? " async" : "";
    var attributes = closureType.IsEscaping ? "@escaping " : "";

    return $"{attributes}{argsString}{asyncKeyword}{throwsKeyword} -> {returnString}";
}
```

Also fixed `NamedTypeSpec` handling to properly include `any` prefix and wrap optionals correctly:

```csharp
if (typeSpec is NamedTypeSpec namedType)
{
    var anyPrefix = namedType.IsAny ? "any " : "";

    if (namedType.Name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
    {
        var innerType = GetSwiftTypeName(namedType.GenericParameters[0]);
        return $"({innerType})?";  // Wrap in parens for proper optional syntax
    }
    // ...
}
```

### Files Modified

- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs` - Rewrote `GetSwiftTypeName()` with closure and existential handling

---

## 21.3 EveryProtocolEmitter Existential Metatype Syntax

**Priority**: High (blocking Swift wrapper compilation)
**Status**: COMPLETED

### Problem

Swift wrapper compilation failed when protocols returned existential types:

```
Swift.Nuke.swift:162:75: error: 'self' is not a member type of protocol 'Nuke.Cancellable'
```

Generated code:
```swift
return resultPtr.assumingMemoryBound(to: any Nuke.Cancellable.self).pointee
```

### Root Cause

Swift requires parentheses around existential types when using `.self` metatype access:
- Correct: `(any Protocol).self`
- Incorrect: `any Protocol.self`

### Solution

Added helper method to wrap existential types for metatype access:

```csharp
private string GetSwiftTypeNameForMetatype(TypeSpec? typeSpec)
{
    var typeName = GetSwiftTypeName(typeSpec);
    // If the type starts with "any ", wrap in parentheses for .self access
    if (typeName.StartsWith("any ") || typeName.StartsWith("(any "))
    {
        if (!typeName.StartsWith("("))
            return $"({typeName})";
    }
    return typeName;
}
```

Updated all `.self` usages in property getters, subscript getters, and method returns:

```swift
// Before
return resultPtr.assumingMemoryBound(to: any Nuke.Cancellable.self).pointee

// After
return resultPtr.assumingMemoryBound(to: (any Nuke.Cancellable).self).pointee
```

### Files Modified

- `src/Swift.Bindings/src/Emitter/StringEmitter/EveryProtocolEmitter.cs`:
  - Added `GetSwiftTypeNameForMetatype()` helper method
  - Updated `EmitPropertyImplementation()` to use metatype-safe type names
  - Updated `EmitSubscriptImplementation()` to use metatype-safe type names
  - Updated `EmitMethodImplementation()` to use metatype-safe type names

---

## Verification

- All 619 unit tests pass
- NukeTestApp validation: 30 passed, 0 failed, 2 warnings
- Swift wrapper compiles without errors
- Async image loading works end-to-end

---

## Pre-Existing Issues (Not Fixed in This Phase)

The following issues were discovered but are pre-existing and not addressed:

### Integration Test Generic Protocol Compilation Errors

The `GenericTests` module has compilation errors:
```
error CS0305: Using the generic type 'ISwiftContainer<TElement>' requires 1 type arguments
error CS8895: Methods attributed with 'UnmanagedCallersOnly' cannot have generic type parameters
error CS7042: The DllImport attribute cannot be applied to a method that is generic or contained in a generic type
```

**Root cause**: Generic proxy classes (`ContainerProxy<TElement>`) contain `[UnmanagedCallersOnly]` callback methods, which C# doesn't allow in generic types. This is a fundamental limitation of the current protocol proxy architecture.

**Status**: Known limitation requiring architectural work to address.
