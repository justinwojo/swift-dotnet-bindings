# Phase 13: Optional Closures & AsyncStream Fixes

## Overview

This phase addressed optional closure parameter handling and AsyncStream property naming collisions. The key improvement is that methods with optional closure parameters (like `loadImage(with:queue:progress:completion:)`) now generate correctly instead of being skipped.

## Changes Made

### 1. BoundGenericsHandler - Exclude Optional Closures

**File**: `src/Swift.Bindings/src/Marshaler/BoundGenericsHandler.cs`

Modified `IsBoundGeneric()` methods to return false for optional closures. This ensures optional closures are handled by `ClosureHandler` instead of `BoundGenericsHandler`.

```csharp
public bool IsBoundGeneric(ArgumentDecl argumentDecl) =>
    !argumentDecl.IsGeneric &&
    argumentDecl.SwiftTypeSpec is NamedTypeSpec namedTypeSpec &&
    namedTypeSpec.ContainsGenericParameters &&
    !_closureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec);  // NEW
```

**Rationale**: `Optional<Closure>` has generic parameters (`ContainsGenericParameters = true`), so it was incorrectly being handled as a bound generic type, producing `Swift.SwiftOptional<Action<...>>` instead of `Action<...>?`.

### 2. TypeConversionHandler - Defer Optional Closures

**File**: `src/Swift.Bindings/src/Marshaler/TypeConversionHandler.cs`

Modified `GetIdiomaticCSharpType()` to return null for `Optional<Closure>` types, allowing `ClosureHandler` to process them.

```csharp
if (IsSwiftOptional(namedTypeSpec))
{
    // Don't handle Optional<Closure> here - let ClosureHandler deal with it
    if (namedTypeSpec.GenericParameters.Count > 0 &&
        namedTypeSpec.GenericParameters[0] is ClosureTypeSpec)
    {
        return null;
    }
    // ... rest of Optional handling
}
```

### 3. ClosureHandler - Nullable Syntax for Primitives Only

**File**: `src/Swift.Bindings/src/Marshaler/ClosureHandler.cs`

Modified `TranslateTypeSpecToCSharp()` to use C# nullable syntax (`T?`) only for primitive types. Complex types continue using `Swift.SwiftOptional<T>` to avoid issues with closure invocation marshalling.

```csharp
if (namedType.Name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
{
    var innerTypeSpec = namedType.GenericParameters[0];
    var innerType = TranslateTypeSpecToCSharp(innerTypeSpec);

    // Use nullable syntax only for primitive/simple types
    if (IsPrimitiveType(innerTypeSpec) || innerTypeSpec.IsEmptyTuple || IsPointerType(...))
    {
        return $"{innerType}?";
    }
    // For complex types, use SwiftOptional wrapper
    return $"Swift.SwiftOptional<{innerType}>";
}
```

Added helper methods:
- `IsPrimitiveType(TypeSpec)` - Returns true for Swift.Bool, Swift.Int*, Swift.UInt*, Swift.Float*, Swift.Double
- Updated `IsPointerType(NamedTypeSpec?)` to accept nullable parameter

### 4. MethodHandler - Skip Optional Closures in Type Conversions

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

Modified `EmitTypeConversions()` to skip optional closures since they're handled by `EmitClosureSetup()`.

```csharp
else if (_env.TypeConversionHandler.IsSwiftOptional(argumentDecl.SwiftTypeSpec) &&
         !_env.ClosureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec))  // NEW
{
    // T? -> SwiftOptional<T> (but not for optional closures)
    // ...
}
```

### 5. AsyncStreamEmitter - Property Collision Detection

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/AsyncStreamEmitter.cs`

Modified `EmitPropertyGetter()` to use `NameProvider.GetPropertyName()` with nested type collision detection, and fixed the self argument access pattern.

```csharp
public static void EmitPropertyGetter(..., IReadOnlySet<string>? siblingNestedTypeNames = null)
{
    // ...
    var propertyName = NameProvider.GetPropertyName(propertyDecl.Name, siblingNestedTypeNames);
    var selfArg = isStatic ? "" : "(void*)_payload.DangerousGetHandle(), ";
    // ...
}
```

### 6. PropertyHandler - Pass Nested Types for Collision Detection

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/PropertyHandler.cs`

Modified `EmitAsyncStreamProperty()` to compute and pass nested type names for collision detection.

```csharp
IReadOnlySet<string>? nestedTypeNames = null;
if (propertyDecl.ParentDecl is TypeDecl parentTypeDecl)
{
    nestedTypeNames = new HashSet<string>(parentTypeDecl.Types.Select(t => t.Name));
}
// ...
AsyncStreamEmitter.EmitPropertyGetter(..., nestedTypeNames);
```

## Results

### Before
```csharp
// Only 2 LoadImage overloads
public unsafe ImageTask LoadImage(URL with, Action<SwiftResult<...>> completion)
public unsafe ImageTask LoadImage(ImageRequest with, Action<SwiftResult<...>> completion)

// AsyncStream property collided with nested type
public IAsyncEnumerable<ImageTask.Progress> Progress  // COLLISION with class Progress
```

### After
```csharp
// All 3 LoadImage overloads generated
public unsafe ImageTask LoadImage(URL with, Action<SwiftResult<...>> completion)
public unsafe ImageTask LoadImage(ImageRequest with, Action<SwiftResult<...>> completion)
public unsafe ImageTask LoadImage(
    ImageRequest with,
    DispatchQueue? queue,
    Action<SwiftOptional<ImageResponse>, long, long>? progress,  // NEW - nullable delegate
    Action<SwiftResult<...>> completion)

// AsyncStream property renamed to avoid collision
public IAsyncEnumerable<ImageTask.Progress> ProgressValue  // Renamed
```

## Test Impact

- **593 unit tests** (was 591) - Added 2 new tests for optional closure handling
- **691 integration tests** - All passing
- **72 runtime tests** - All passing
- **Total: 1,356 tests passing**

## Pre-existing Bugs Revealed

The fixes revealed 8 pre-existing compilation errors in `ClosureEmitter` for closures with:
- Non-frozen struct parameters (uses `ISwiftObject.Payload` which doesn't exist on the interface)
- Existential return types (incorrect return type conversion)

These errors were previously hidden because the build failed earlier on the Progress collision. They are tracked as future work item 14.6.
