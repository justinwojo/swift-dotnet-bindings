# Phase 22: Generic Protocol Proxy Compilation Fix

**Status**: COMPLETED (2026-01-31)

Fixed compilation errors in integration tests caused by protocols with associated types generating invalid C# code.

## Problem

Protocols with associated types (PATs) like `Container` with `associatedtype Element` generate:
- Generic interfaces: `ISwiftContainer<TElement>`
- Generic proxy classes: `ContainerProxy<TElement>`

The proxy classes contain `[UnmanagedCallersOnly]` callback methods for Swift-to-C# callbacks, but C# has fundamental restrictions:

```
error CS8895: Methods attributed with 'UnmanagedCallersOnly' cannot have generic type parameters and cannot be declared in a generic type.
error CS7042: The DllImport attribute cannot be applied to a method that is generic or contained in a generic method or type.
error CS0305: Using the generic type 'ISwiftContainer<TElement>' requires 1 type arguments
```

## Solution

Implemented graceful degradation - skip code generation for protocols with associated types with appropriate warnings, since C# fundamentally doesn't support the required attributes in generic types.

### Changes

#### 1. TypeRecordFlags Enhancement

Added `HasAssociatedTypes` flag to mark protocols with associated types at registration time.

**File**: `src/Swift.Bindings/src/TypeDatabase/TypeRecord.cs`

```csharp
[Flags]
public enum TypeRecordFlags
{
    None = 0,
    // ... existing flags ...

    /// <summary>
    /// This flag indicates a protocol has associated types.
    /// Such protocols generate generic C# interfaces (e.g., ISwiftContainer<TElement>) and
    /// cannot be used directly as generic constraints without type arguments.
    /// </summary>
    HasAssociatedTypes = 1 << 3,
}
```

#### 2. ModuleProcessor Registration

Set the flag during protocol registration.

**File**: `src/Swift.Bindings/src/Parser/ModuleProcessor.cs`

```csharp
private TypeRecord RegisterProtocolType(ProtocolDecl protocolDecl)
{
    // Protocols with associated types generate generic C# interfaces
    // Mark them so we can skip them in generic constraints
    var flags = protocolDecl.AssociatedTypes.Count > 0
        ? TypeRecordFlags.HasAssociatedTypes
        : TypeRecordFlags.None;

    return _typeDatabase.RegisterType(..., flags);
}
```

#### 3. ProtocolProxyEmitter Skip Logic

Skip proxy generation for protocols with associated types.

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/ProtocolProxyEmitter.cs`

```csharp
public void EmitProxyClass(ProtocolDecl protocolDecl, IndentedTextWriter writer)
{
    // Skip protocols with associated types (would create generic proxy classes)
    // C# doesn't allow [UnmanagedCallersOnly] or [DllImport] in generic types
    if (protocolDecl.AssociatedTypes.Count > 0)
    {
        _logger.LogWarning($"Skipping proxy class for {protocolDecl.Name}: protocols with associated types are not yet supported for proxy generation");
        return;
    }
    // ...
}
```

#### 4. MethodHandler Generic Constraint Filtering

Skip protocols with associated types when building generic where clauses.

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/MethodHandler.cs`

- Added `HasUnsupportedProtocolConstraints()` to detect methods with problematic constraints
- Modified `IsProtocolAvailableForConstraint()` to check the `HasAssociatedTypes` flag
- Updated `BuildWhereClause()` and `EmitProtocolWitnessTables()` to skip affected protocols

#### 5. GenericTypeEmitter Constraint Filtering

Skip protocols with associated types in type-level where clauses.

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/GenericTypeEmitter.cs`

```csharp
public static string GetWhereClause(TypeDecl typeDecl, ITypeDatabase? typeDatabase = null)
{
    // ...
    foreach (var conformance in param.GenericConformances)
    {
        // Skip protocols with associated types (they generate generic interfaces
        // which can't be used as constraints without type arguments)
        if (typeDatabase != null && HasAssociatedTypes(typeDatabase, conformance.ConformanceTarget))
            continue;
        // ...
    }
}
```

#### 6. TypeHandler Protocol Conformance Dictionary

Skip protocols with associated types in `GetProtocolConformanceDictionary()`.

**File**: `src/Swift.Bindings/src/Emitter/StringEmitter/Handler/TypeHandler.cs`

```csharp
// Skip protocols with associated types (they generate generic interfaces that can't be used with typeof)
if (_typeDatabase.TryGetTypeRecord(conformance.Protocol, out var record) &&
    record.Kind == TypeRecordKind.Protocol &&
    record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
{
    continue;
}
```

### Unit Test Updates

Updated `ProtocolProxyEmitterTests.cs` to verify protocols with associated types are skipped:

```csharp
[Fact]
public void EmitProxyClass_SkipsProtocolsWithAssociatedTypes()
{
    var protocolDecl = CreateProtocolWithProperty("TestProtocol", "value", hasGetter: true, hasSetter: false);
    protocolDecl.AssociatedTypes.Add(new AssociatedTypeDecl { Name = "Element" });
    var output = EmitProxyClass(protocolDecl);
    Assert.DoesNotContain("public unsafe class TestProtocolProxy", output);
}
```

## Verification

**Unit tests**: 617 passing (all existing tests continue to pass)

**Integration tests**: The specific errors (CS8895, CS7042, CS0305 for generic protocol types) are resolved.

**Grep verification**:
```bash
./run-tests.sh 2>&1 | grep -E "CS8895|CS7042|error.*ISwiftContainer|UnmanagedCallersOnly.*generic|DllImport.*generic"
# No output = errors are fixed
```

## Remaining Issues

The integration tests still have pre-existing compilation errors unrelated to this fix:
- Missing property setters (various types)
- Boxing conversion errors for primitives in generics

These are tracked separately and were present before this fix.

## Future Work

A more sophisticated solution could potentially support protocols with associated types through:
1. Runtime code generation (Reflection.Emit) for proxy classes
2. Non-generic base class pattern with runtime type checks
3. Source generators that create specialized proxy classes per type argument

However, the graceful degradation approach is appropriate for now - protocols with associated types are relatively rare in Swift APIs, and the warnings clearly indicate the limitation.
