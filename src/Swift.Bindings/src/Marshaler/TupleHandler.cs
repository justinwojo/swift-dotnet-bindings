// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling tuple types in Swift bindings.
/// It provides methods to detect tuple arguments and translate them to appropriate
/// C# ValueTuple types.
/// </summary>
public class TupleHandler
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ExistentialHandler _existentialHandler;

    /// <summary>
    /// Maximum number of tuple elements supported in Phase 1.
    /// C# ValueTuple supports up to 7 elements natively; beyond that requires nesting.
    /// </summary>
    public const int MaxSupportedTupleElements = 7;

    public TupleHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _existentialHandler = new ExistentialHandler(typeDatabase);
    }

    /// <summary>
    /// Determines whether the specified argument declaration represents a tuple type.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type is a non-empty tuple; otherwise, <c>false</c>.</returns>
    public bool IsTuple(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple;

    /// <summary>
    /// Determines whether the specified type spec represents a tuple type.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type spec is a non-empty tuple; otherwise, <c>false</c>.</returns>
    public bool IsTuple(TypeSpec typeSpec) =>
        typeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple;

    /// <summary>
    /// Gets the TupleTypeSpec from an argument declaration.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The TupleTypeSpec if the argument is a non-empty tuple; otherwise, null.</returns>
    public TupleTypeSpec? GetTupleTypeSpec(ArgumentDecl argumentDecl) =>
        argumentDecl.SwiftTypeSpec is TupleTypeSpec tuple && !tuple.IsEmptyTuple ? tuple : null;

    /// <summary>
    /// Determines whether the tuple is a supported type for Phase 1.
    /// Phase 1 supports:
    /// - Maximum 7 tuple elements
    /// - Only frozen/primitive element types
    /// - No nested tuples
    /// - No closures as tuple elements
    /// - No generic type parameters as elements
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <returns><c>true</c> if the tuple is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedTuple(TupleTypeSpec tupleTypeSpec)
    {
        // Empty tuples are void, not tuples
        if (tupleTypeSpec.IsEmptyTuple)
            return false;

        // Maximum 7 elements in Phase 1
        if (tupleTypeSpec.Elements.Count > MaxSupportedTupleElements)
            return false;

        // Check that all element types are supported
        foreach (var element in tupleTypeSpec.Elements)
        {
            if (!IsSupportedTupleElementType(element))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether any element of the tuple contains a generic type parameter,
    /// either directly (e.g., τ_0_0) or nested inside a bound generic (e.g., Optional&lt;τ_0_0&gt;).
    /// Tuples with generic elements use indirect result (SwiftIndirectResult) in P/Invoke
    /// and per-element extraction via SwiftMarshal.MarshalFromSwift at runtime.
    /// </summary>
    public bool HasGenericTypeParameterElements(TupleTypeSpec tupleTypeSpec) =>
        tupleTypeSpec.Elements.Any(ContainsGenericTypeParameter);

    /// <summary>
    /// Checks whether every top-level element of the tuple is a bare generic type parameter
    /// (e.g., T, U, τ_0_0). Swift returns bare generic parameters via @out indirect result,
    /// so a tuple where every element is a bare generic uniformly uses N @out registers.
    ///
    /// Bound generics like Array&lt;T&gt;, UnsafePointer&lt;T&gt;, and similar are returned direct
    /// (refcounted reference, pointer) even though they contain a generic parameter; mixing
    /// them with bare T would produce a mixed indirect/direct ABI that this branch does not
    /// model. Optional&lt;T&gt; is also address-only when T is generic but is excluded here for
    /// safety — the legacy path handles it.
    /// </summary>
    public bool AllElementsAreBareGenericTypeParameter(TupleTypeSpec tupleTypeSpec) =>
        tupleTypeSpec.Elements.Count > 0
        && tupleTypeSpec.Elements.All(static e => e is NamedTypeSpec named && TypeSpecHelpers.IsGenericTypeParameter(named.Name));

    /// <summary>
    /// Recursively checks whether a TypeSpec contains a generic type parameter,
    /// either directly or nested inside bound generic arguments.
    /// </summary>
    private static bool ContainsGenericTypeParameter(TypeSpec typeSpec)
    {
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec))
            return true;

        if (typeSpec is NamedTypeSpec namedType && namedType.ContainsGenericParameters)
            return namedType.GenericParameters.Any(ContainsGenericTypeParameter);

        if (typeSpec is TupleTypeSpec tupleType)
            return tupleType.Elements.Any(ContainsGenericTypeParameter);

        return false;
    }

    /// <summary>
    /// Determines whether the tuple is supported when a generic context is available.
    /// Generic type parameter elements are allowed when a context can resolve them.
    /// </summary>
    public bool IsSupportedTuple(TupleTypeSpec tupleTypeSpec, GenericContext genericContext)
    {
        if (tupleTypeSpec.IsEmptyTuple)
            return false;
        if (tupleTypeSpec.Elements.Count > MaxSupportedTupleElements)
            return false;
        foreach (var element in tupleTypeSpec.Elements)
        {
            if (!IsSupportedTupleElementType(element, genericContext))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Translates a Swift tuple type to a C# tuple type string, resolving generic type parameters
    /// via the provided generic context.
    /// </summary>
    public string GetCSharpTupleType(TupleTypeSpec tupleTypeSpec, GenericContext genericContext)
    {
        return GetCSharpTupleType(tupleTypeSpec, typeSpec =>
        {
            if (typeSpec is NamedTypeSpec namedType &&
                TypeSpecHelpers.IsGenericTypeParameter(namedType.Name) &&
                genericContext.TryResolve(namedType.Name, out var csName))
            {
                return csName;
            }
            return TranslateElementTypeToCSharp(typeSpec);
        });
    }

    /// <summary>
    /// Gets the P/Invoke tuple type, resolving generic type parameters to IntPtr.
    /// </summary>
    public string GetPInvokeTupleType(TupleTypeSpec tupleTypeSpec, GenericContext genericContext)
    {
        return GetPInvokeTupleType(tupleTypeSpec, typeSpec =>
        {
            if (typeSpec is NamedTypeSpec namedType &&
                TypeSpecHelpers.IsGenericTypeParameter(namedType.Name) &&
                genericContext.TryResolve(namedType.Name, out _))
            {
                return "IntPtr";
            }
            return TranslateElementTypeToPInvoke(typeSpec);
        });
    }

    /// <summary>
    /// Checks if a type is supported as a tuple element.
    /// </summary>
    private bool IsSupportedTupleElementType(TypeSpec typeSpec) =>
        IsSupportedTupleElementType(typeSpec, GenericContext.Empty);

    /// <summary>
    /// Checks if a type is supported as a tuple element, with optional generic context.
    /// </summary>
    private bool IsSupportedTupleElementType(TypeSpec typeSpec, GenericContext genericContext)
    {
        // Generic type parameters are valid tuple elements when a mapping is available
        if (TypeSpecHelpers.IsGenericTypeParameter(typeSpec) && !genericContext.IsEmpty)
            return true;

        // Nested tuples are not supported yet
        if (typeSpec is TupleTypeSpec)
            return false;

        // Closures within tuples are not supported yet
        if (typeSpec is ClosureTypeSpec)
            return false;

        // Existential types are supported
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            return protocolList != null && _existentialHandler.IsSupportedExistential(protocolList);
        }

        // Named types should be resolvable in the type database
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle bound generic types (e.g., Optional<T>, Array<T>)
            if (namedType.ContainsGenericParameters)
            {
                return IsSupportedGenericTupleElement(namedType);
            }

            // Try to get the type record
            if (!_typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
                return false;

            // Frozen types and ObjC-bridged types are supported
            // Non-frozen, non-ObjC types are also allowed since they can be wrapped
            return true;
        }

        // Other type specs (ProtocolList, etc.) not supported
        return false;
    }

    /// <summary>
    /// Checks if a generic type is supported as a tuple element.
    /// Supports bound generic types (Optional, Array, etc.) where the base type is in the database.
    /// </summary>
    private bool IsSupportedGenericTupleElement(NamedTypeSpec namedType)
    {
        // Check if base type is in type database
        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out _))
            return false;

        // Recursively check all generic parameters are supported
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Handle existential generic parameters (e.g., Optional<any Protocol>)
            if (_existentialHandler.IsExistential(genericParam))
            {
                var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
                if (protocolList == null || !_existentialHandler.IsSupportedExistential(protocolList))
                    return false;
                continue;
            }

            // Recursively check element type
            if (!IsSupportedTupleElementType(genericParam))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Translates a Swift tuple type to a C# tuple type string for wrapper methods.
    /// Uses C# tuple syntax with named elements if labels are present.
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <returns>The C# tuple type string (e.g., "(int, string)" or "(int x, string y)").</returns>
    public string GetCSharpTupleType(TupleTypeSpec tupleTypeSpec)
    {
        return GetCSharpTupleType(tupleTypeSpec, TranslateElementTypeToCSharp);
    }

    /// <summary>
    /// Translates a Swift tuple type to a C# tuple type string for wrapper methods,
    /// using a custom type translator for element types.
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <param name="typeTranslator">A function that translates element TypeSpecs to C# type names.</param>
    /// <returns>The C# tuple type string (e.g., "(int, string)" or "(int x, string y)").</returns>
    public string GetCSharpTupleType(TupleTypeSpec tupleTypeSpec, Func<TypeSpec, string> typeTranslator)
    {
        var elementTypes = new List<string>();
        foreach (var element in tupleTypeSpec.Elements)
        {
            var typeString = typeTranslator(element);

            // Include label if present
            if (!string.IsNullOrEmpty(element.TypeLabel))
            {
                typeString = $"{typeString} {element.TypeLabel}";
            }

            elementTypes.Add(typeString);
        }

        return $"({string.Join(", ", elementTypes)})";
    }

    /// <summary>
    /// Gets the P/Invoke tuple type for a tuple.
    /// Uses ValueTuple<> generic type for P/Invoke compatibility.
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <returns>The ValueTuple type string (e.g., "ValueTuple<int, string>").</returns>
    public string GetPInvokeTupleType(TupleTypeSpec tupleTypeSpec)
    {
        return GetPInvokeTupleType(tupleTypeSpec, TranslateElementTypeToPInvoke);
    }

    /// <summary>
    /// Gets the P/Invoke tuple type for a tuple, using a custom type translator for element types.
    /// Uses ValueTuple<> generic type for P/Invoke compatibility.
    /// </summary>
    /// <param name="tupleTypeSpec">The tuple type specification.</param>
    /// <param name="typeTranslator">A function that translates element TypeSpecs to P/Invoke type names.</param>
    /// <returns>The ValueTuple type string (e.g., "ValueTuple<int, IntPtr>").</returns>
    public string GetPInvokeTupleType(TupleTypeSpec tupleTypeSpec, Func<TypeSpec, string> typeTranslator)
    {
        var elementTypes = new List<string>();
        foreach (var element in tupleTypeSpec.Elements)
        {
            elementTypes.Add(typeTranslator(element));
        }

        return $"ValueTuple<{string.Join(", ", elementTypes)}>";
    }

    /// <summary>
    /// Checks if any element has a P/Invoke-to-C# type mismatch that would break closure callbacks.
    /// Returns true when a tuple element becomes IntPtr in P/Invoke but has a different C# type,
    /// meaning the callback would receive ValueTuple&lt;IntPtr,...&gt; while the delegate expects the C# type.
    /// Pointer types (UnsafeMutablePointer&lt;T&gt; etc.) are IntPtr in BOTH contexts, so they're safe.
    /// </summary>
    public bool HasClosureUnsafeTupleElements(TupleTypeSpec tupleTypeSpec)
    {
        foreach (var element in tupleTypeSpec.Elements)
        {
            var pinvokeType = TranslateElementTypeToPInvoke(element);
            var csharpType = TranslateElementTypeToCSharp(element);
            // IntPtr mismatch: P/Invoke uses IntPtr but C# uses a different type
            if (pinvokeType == "IntPtr" &&
                csharpType != "IntPtr" && csharpType != "System.IntPtr")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Translates a TypeSpec element to its C# equivalent type.
    /// </summary>
    internal string TranslateElementTypeToCSharp(TypeSpec typeSpec)
    {
        // Handle existential types — use public type (interface/object), not ExistentialContainer
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                return _existentialHandler.GetPublicExistentialType(protocolList);
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Handle bound generic types (e.g., Optional<T>, Array<T>)
            if (namedType.ContainsGenericParameters)
            {
                return TranslateBoundGenericToCSharp(namedType);
            }

            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Fallback for unsupported types
        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a bound generic NamedTypeSpec to its full C# type name with generic parameters.
    /// </summary>
    private string TranslateBoundGenericToCSharp(NamedTypeSpec namedType)
    {
        // Bound-generic SIMD aliases collapse to a non-generic managed type
        // (e.g. Swift.SIMD3<Swift.Float> → System.Numerics.Vector3). The resolved alias record
        // IS the final C# type — appending the bound-generic's type arguments to a typealias
        // produces invalid syntax like `simd.simd_float3<float>` (a Swift typealias is not a
        // C# generic). Short-circuit here so the <...> wrap below is never reached.
        if (TypeDatabaseExtensions.TryResolveBoundGenericAlias(_typeDatabase, namedType, out var aliasRecord))
        {
            return aliasRecord.CSharpTypeName.FullyQualifiedName;
        }

        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!_typeDatabase.TryGetTypeRecord(baseTypeName, out var typeRecord))
        {
            // Fallback if base type not in database
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
        if (typeRecord == TypeDatabaseExtensions.IntPtrType)
        {
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Recursively translate all generic parameters
        var translatedParams = new List<string>();
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Handle existential generic parameters (e.g., Optional<any Protocol>)
            if (_existentialHandler.IsExistential(genericParam))
            {
                var protocolList = _existentialHandler.ToProtocolListTypeSpec(genericParam);
                if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                {
                    if (_existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wk))
                        translatedParams.Add(wk);
                    else
                        translatedParams.Add(_existentialHandler.GetPublicExistentialType(protocolList));
                    continue;
                }
            }
            translatedParams.Add(TranslateElementTypeToCSharp(genericParam));
        }

        // Build full type name with generics
        return translatedParams.Count > 0
            ? $"{typeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>"
            : typeRecord.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a TypeSpec element to its P/Invoke equivalent type.
    /// </summary>
    private string TranslateElementTypeToPInvoke(TypeSpec typeSpec)
    {
        // Handle existential types
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                return _existentialHandler.GetPInvokeExistentialType(protocolList);
            return "IntPtr";
        }

        if (typeSpec is NamedTypeSpec namedType)
        {
            // Bound generic types with optional containing ObjC types → IntPtr
            if (namedType.ContainsGenericParameters)
            {
                var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
                if (_typeDatabase.TryGetTypeRecord(baseTypeName, out var baseRecord) &&
                    baseRecord.CSharpTypeName.Name == "SwiftOptional" &&
                    namedType.GenericParameters.Count > 0)
                {
                    var innerType = namedType.GenericParameters[0];
                    if (innerType is NamedTypeSpec innerNamed &&
                        _typeDatabase.TryGetTypeRecord(innerNamed, out var innerRecord) &&
                        MarshallingHelpers.IsObjCBridged(innerRecord))
                    {
                        // Optional ObjC type → IntPtr (null represented as IntPtr.Zero)
                        return "IntPtr";
                    }
                }
                // Other bound generics → IntPtr (opaque pointer, safe for C# generic type arguments)
                return "IntPtr";
            }

            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);

            // ObjC bridged types use IntPtr in P/Invoke
            if (MarshallingHelpers.IsObjCBridged(typeRecord))
            {
                return "IntPtr";
            }

            // Enums: simple enums use underlying integer type, complex enums use IntPtr
            if (typeRecord.Kind == TypeRecordKind.Enum)
            {
                if (typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                    return EnumHandler.GetCSharpEnumUnderlyingType(typeRecord.RawValueTypeName);
                return "IntPtr";
            }

            // Swift classes are non-blittable C# classes — must use IntPtr (no .Buffer)
            if (typeRecord.Kind == TypeRecordKind.Class)
            {
                return "IntPtr";
            }

            // Non-frozen structs (ClassWithOpaquePayload) are non-blittable C# classes — must use IntPtr (no .Buffer)
            if (typeRecord.Kind == TypeRecordKind.Struct && !MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return "IntPtr";
            }

            // Frozen types with memory management use Buffer type
            if ((typeRecord.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 &&
                (typeRecord.Flags & TypeRecordFlags.Frozen) != 0)
            {
                return $"{typeRecord.CSharpTypeName.FullyQualifiedName}.Buffer";
            }

            // Frozen blittable structs — use type name directly
            if (typeRecord.Kind == TypeRecordKind.Struct && MarshallingHelpers.IsTypeFrozen(typeRecord))
            {
                return typeRecord.CSharpTypeName.FullyQualifiedName;
            }

            // Fallback — IntPtr is safe for any unknown type (was class name, which breaks LibraryImport)
            return "IntPtr";
        }

        // Fallback for unsupported types
        return "IntPtr";
    }
}
