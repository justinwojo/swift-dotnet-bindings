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

    /// <summary>Whether the method is async. When true and IsParameter is false, wraps the return projection in AsyncProjection.</summary>
    public bool IsAsync { get; init; }

    /// <summary>Whether the method throws. Used by async projection for error callback generation.</summary>
    public bool Throws { get; init; }

    /// <summary>Unique prefix for callback names. Used by closures and async projections for callback method naming.</summary>
    public string? CallbackNamePrefix { get; init; }
}

/// <summary>
/// Single entry point for producing type projections.
/// Given a TypeSpec and context, returns the appropriate ITypeProjection
/// that knows how to marshal the type between C# and Swift.
///
/// Supports all Swift type categories:
/// - Simple types (bool, string, enums, ObjC bridged, blittable, non-frozen)
/// - Generic containers (Array, Dictionary, Optional)
/// - Tuples (per-element composition)
/// - Closures (Action/Func with callback declarations)
/// - Protocol existentials (3-tier: well-known, proxy, object)
/// - Async (Task/Task&lt;T&gt; with Swift wrapper and callbacks)
/// </summary>
public class TypeProjectionFactory
{
    /// <summary>
    /// Produces a type projection for the given TypeSpec, or null if the type
    /// is not supported by the factory.
    /// </summary>
    /// <param name="typeSpec">The Swift type to project.</param>
    /// <param name="context">Context for the projection.</param>
    /// <returns>A type projection, or null if unsupported.</returns>
    public ITypeProjection? Project(TypeSpec typeSpec, ProjectionContext context)
    {
        // Async wrapping — must be before all TypeSpec dispatch.
        // When IsAsync && !IsParameter, wrap the inner return projection in AsyncProjection.
        // Strip IsAsync before recursing to prevent double-wrap.
        if (context.IsAsync && !context.IsParameter)
        {
            // Void async methods have empty tuple return → Task (no inner projection)
            if (typeSpec.IsEmptyTuple)
                return new AsyncProjection(null, context.Throws, context.CallbackNamePrefix);

            var innerProjection = Project(typeSpec, context with { IsAsync = false });
            if (innerProjection == null)
                return null;
            return new AsyncProjection(innerProjection, context.Throws, context.CallbackNamePrefix);
        }

        // TypeSpec dispatch (only reached when !IsAsync or IsParameter)
        if (typeSpec is TupleTypeSpec tupleType)
            return ProjectTuple(tupleType, context);

        if (typeSpec is ClosureTypeSpec closureType)
            return ProjectClosure(closureType, context);

        if (typeSpec is ProtocolListTypeSpec protocolList)
            return ProjectExistential(protocolList, context);

        if (typeSpec is NamedTypeSpec namedType)
            return ProjectNamedType(namedType, context);

        return null;
    }

    private ITypeProjection? ProjectNamedType(NamedTypeSpec namedType, ProjectionContext context)
    {
        var name = namedType.Name;

        // Generic type parameters (τ_0_0, T, U, etc.) cannot be projected — they're
        // resolved by the caller via GenericTypeMapping.
        if (TypeSpecHelpers.IsGenericTypeParameter(name))
            return null;

        // Swift special type names that can't be projected:
        // - "Self": dynamic self-type (protocol extensions, class factory methods)
        // - "repeat": parameter packs (Swift 5.9+ variadic generics)
        if (name is "Self" or "repeat")
            return null;

        // Route NamedTypeSpec.IsAny to existential
        if (namedType.IsAny)
        {
            var handler = new ExistentialHandler(context.TypeDatabase);
            var protocolList = handler.ToProtocolListTypeSpec(namedType);
            if (protocolList != null)
                return ProjectExistential(protocolList, context);
            return null;
        }

        // Generic container types
        if (name == "Swift.Optional" && namedType.GenericParameters.Count == 1)
        {
            var inner = namedType.GenericParameters[0];
            var isExistentialInner = inner is ProtocolListTypeSpec ||
                (inner is NamedTypeSpec innerNamed && innerNamed.IsAny);

            // Optional inner types always use IsParameter=false (return-style projection).
            // This matches the legacy GetIdiomaticCSharpType behavior where Optional<Dictionary<K,V>>
            // always produces IReadOnlyDictionary (not IDictionary), regardless of outer context.
            var innerProjection = Project(inner, context with { IsParameter = false });
            if (innerProjection == null)
                return null;
            return new OptionalProjection(innerProjection, isExistentialInner);
        }

        if (name == "Swift.Array" && namedType.GenericParameters.Count == 1)
        {
            var elemProjection = Project(namedType.GenericParameters[0], context);
            if (elemProjection == null)
                return null;
            return new ArrayProjection(elemProjection, context.IsParameter);
        }

        if (name == "Swift.Dictionary" && namedType.GenericParameters.Count == 2)
        {
            var keyProjection = Project(namedType.GenericParameters[0], context);
            var valueProjection = Project(namedType.GenericParameters[1], context);
            if (keyProjection == null || valueProjection == null)
                return null;
            return new DictionaryProjection(keyProjection, valueProjection, context.IsParameter);
        }

        // Well-known simple types
        if (name == "Swift.Bool")
            return new BoolProjection();

        if (name == "Swift.String")
            return new StringProjection();

        // Pointer types are always mapped to System.IntPtr
        if (IsPointerType(name))
            return new BlittableProjection("System.IntPtr");

        // User-defined types with generic parameters require bound-generic translation
        // (e.g., Result<String, Error> → Result<string, AnyError>) that the factory
        // doesn't handle yet. Return null to let callers use the legacy path.
        if (namedType.GenericParameters.Count > 0)
            return null;

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

        // Classes (non-frozen, pointer-based)
        if (typeRecord.Kind == TypeRecordKind.Class)
            return new ClassProjection(typeRecord.CSharpTypeName.FullyQualifiedName);

        // Native remapped types (URL → NSUrl, Data → NSData)
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

        // Frozen with memory management — not supported
        return null;
    }

    private ITypeProjection? ProjectTuple(TupleTypeSpec tupleType, ProjectionContext context)
    {
        if (tupleType.Elements.Count == 0)
            return null;

        var elementProjections = new List<ITypeProjection>();
        foreach (var element in tupleType.Elements)
        {
            var proj = Project(element, context);
            if (proj == null)
                return null;
            elementProjections.Add(proj);
        }

        return new TupleProjection(elementProjections);
    }

    private ITypeProjection? ProjectClosure(ClosureTypeSpec closureType, ProjectionContext context)
    {
        var argProjections = new List<ITypeProjection>();
        foreach (var arg in closureType.EachArgument())
        {
            var proj = Project(arg, context with { IsParameter = true });
            if (proj == null)
                return null;
            argProjections.Add(proj);
        }

        ITypeProjection? returnProjection = null;
        if (closureType.HasReturn())
        {
            returnProjection = Project(closureType.ReturnType, context with { IsParameter = false });
            if (returnProjection == null)
                return null;
        }

        var callbackName = context.CallbackNamePrefix != null
            ? $"{context.CallbackNamePrefix}Callback"
            : "closureCallback";

        return new ClosureProjection(
            argProjections,
            returnProjection,
            closureType.IsEscaping,
            closureType.Throws,
            closureType.IsAsync,
            callbackName);
    }

    private ITypeProjection? ProjectExistential(ProtocolListTypeSpec protocolList, ProjectionContext context)
    {
        var handler = new ExistentialHandler(context.TypeDatabase);
        var containerType = handler.GetCSharpExistentialType(protocolList);
        var publicType = handler.GetPublicExistentialType(protocolList);

        // Determine proxy class name:
        // - well-known protocols (e.g. Swift.Error → AnyError): no proxy
        // - "object" fallback: no proxy
        // - known protocols with interface: has proxy
        string? proxyClassName = null;
        if (!handler.TryGetWellKnownProtocolType(protocolList, out _) && publicType != "object")
        {
            proxyClassName = handler.GetProxyClassName(protocolList);
        }

        return new ExistentialProjection(containerType, publicType, proxyClassName);
    }

    /// <summary>
    /// Determines whether a Swift type name represents a pointer type that should be mapped to System.IntPtr.
    /// </summary>
    private static bool IsPointerType(string name) =>
        name is "Swift.OpaquePointer" or "Swift.UnsafePointer"
            or "Swift.UnsafeMutablePointer" or "Swift.UnsafeRawPointer"
            or "Swift.UnsafeMutableRawPointer" or "Builtin.RawPointer";
}
