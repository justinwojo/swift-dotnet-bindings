// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Context information needed by the factory to produce a type projection.
/// </summary>
public record ProjectionContext
{
    /// <summary>The type database for type resolution.</summary>
    public required ITypeDatabase TypeDatabase { get; init; }

    /// <summary>Whether this type is being projected as a parameter (true) or return value (false).</summary>
    public bool IsParameter { get; init; }
}

/// <summary>
/// Single entry point for producing type projections.
/// Given a TypeSpec and context, returns the appropriate ITypeProjection
/// that knows how to marshal the type between C# and Swift.
///
/// In Session 1, only simple projections are supported. Returns null for
/// unsupported types (tuples, closures, async, etc.) — these will be added
/// in Session 2.
/// </summary>
public class TypeProjectionFactory
{
    /// <summary>
    /// Produces a type projection for the given TypeSpec, or null if the type
    /// is not yet supported by the factory.
    /// </summary>
    /// <param name="typeSpec">The Swift type to project.</param>
    /// <param name="context">Context for the projection.</param>
    /// <returns>A type projection, or null if unsupported.</returns>
    public ITypeProjection? Project(TypeSpec typeSpec, ProjectionContext context)
    {
        // Tuples — Session 2
        if (typeSpec is TupleTypeSpec)
            return null;

        // Closures — Session 2
        if (typeSpec is ClosureTypeSpec)
            return null;

        // Protocol lists (existentials) — Session 2
        if (typeSpec is ProtocolListTypeSpec)
            return null;

        if (typeSpec is NamedTypeSpec namedType)
        {
            return ProjectNamedType(namedType, context);
        }

        return null;
    }

    private ITypeProjection? ProjectNamedType(NamedTypeSpec namedType, ProjectionContext context)
    {
        // Check for well-known simple types first
        var name = namedType.Name;

        // Bool
        if (name == "Swift.Bool")
            return new BoolProjection();

        // String
        if (name == "Swift.String")
            return new StringProjection();

        // Try to resolve from the type database
        if (!context.TypeDatabase.TryGetTypeRecord(
                SwiftTypeName.FromModuleQualifiedName(name), out var typeRecord))
            return null;

        // ObjC bridged types
        if (MarshallingHelpers.IsObjCBridged(typeRecord))
            return new ObjCBridgedProjection(typeRecord.CSharpTypeName.FullyQualifiedName);

        // Simple enums
        if (typeRecord.Kind == TypeRecordKind.Enum && typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
        {
            var underlyingType = EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
            return new SimpleEnumProjection(typeRecord.CSharpTypeName.FullyQualifiedName, underlyingType);
        }

        // Native remapped types (URL → NSUrl, Data → NSData)
        // Must check before non-frozen/blittable since native-remapped types need
        // specific wrapper conversion, not generic SafeHandle/blittable handling.
        if (typeRecord.NativeTypeName != null)
        {
            var isFrozen = MarshallingHelpers.IsTypeFrozen(typeRecord);
            return new NativeRemappedProjection(
                typeRecord.NativeTypeName.FullyQualifiedName,
                typeRecord.CSharpTypeName.FullyQualifiedName,
                isFrozen);
        }

        // Non-frozen structs/classes
        if (!MarshallingHelpers.IsTypeFrozen(typeRecord))
            return new NonFrozenStructProjection(typeRecord.CSharpTypeName.FullyQualifiedName);

        // Blittable frozen types
        if (!MarshallingHelpers.RequiresMemoryManagement(typeRecord))
            return new BlittableProjection(typeRecord.CSharpTypeName.FullyQualifiedName);

        // Frozen with memory management — not a simple projection
        return null;
    }
}
