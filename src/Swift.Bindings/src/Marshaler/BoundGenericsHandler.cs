// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling bound generic types in Swift bindings.
/// It translates Swift generic types into their C# representations and provides
/// type information for marshalling.
/// </summary>
public class BoundGenericsHandler
{
    private readonly ITypeDatabase _typeDatabase;
    private readonly ClosureHandler _closureHandler;
    private readonly TupleHandler _tupleHandler;
    private readonly ExistentialHandler _existentialHandler;

    public BoundGenericsHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _closureHandler = new ClosureHandler(typeDatabase);
        _tupleHandler = new TupleHandler(typeDatabase);
        _existentialHandler = new ExistentialHandler(typeDatabase);
    }

    // Almost all generics will be projected into C# as classes.
    // This collection contains types that must be marshalled as structs.
    // We might introduce a new field in the TypeRecord to indicate this
    private static readonly HashSet<SwiftTypeName> s_structGenerics = new()
        {
            SwiftTypeName.FromModuleQualifiedName("Swift.UnsafeMutableBufferPointer"),
            SwiftTypeName.FromModuleQualifiedName("Swift.UnsafeMutablePointer"),
        };

    // TODO: Add more types as needed.
    // Mapping of Swift generic types to their corresponding buffer types.
    private static readonly Dictionary<SwiftTypeName, string> s_bufferTypeMap = new()
        {
            { SwiftTypeName.FromModuleQualifiedName("Swift.Array"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Set"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Optional"), "IntPtr" },
            { SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary"), "IntPtr" }
        };

    /// <summary>
    /// Determines whether the specified property declaration represents a bound generic type.
    /// Optional closures (Optional&lt;Closure&gt;) are NOT considered bound generics - they should
    /// be handled by ClosureHandler instead.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property's Swift type contains generic parameters; otherwise, <c>false</c>.</returns>
    public bool IsBoundGeneric(PropertyDecl propertyDecl) =>
        propertyDecl.SwiftTypeSpec is NamedTypeSpec namedTypeSpec &&
        namedTypeSpec.ContainsGenericParameters &&
        !_closureHandler.IsOptionalClosure(propertyDecl.SwiftTypeSpec); // TODO: Should also check that return type is not the type's own generic parameter (e.g., T in class Foo<T>)

    /// <summary>
    /// Determines whether the specified argument declaration represents a bound generic type.
    /// Optional closures (Optional&lt;Closure&gt;) are NOT considered bound generics - they should
    /// be handled by ClosureHandler instead.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns><c>true</c> if the argument's Swift type contains generic parameters; otherwise, <c>false</c>.</returns>
    public bool IsBoundGeneric(ArgumentDecl argumentDecl) =>
        !argumentDecl.IsGeneric &&
        argumentDecl.SwiftTypeSpec is NamedTypeSpec namedTypeSpec &&
        namedTypeSpec.ContainsGenericParameters &&
        !_closureHandler.IsOptionalClosure(argumentDecl.SwiftTypeSpec);

    /// <summary>
    /// Determines whether the bound generic type requires special marshalling.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration to check.</param>
    /// <returns><c>true</c> if the type requires bound generic marshalling; otherwise, <c>false</c>.</returns>
    public bool RequiresBoundGenericMarshalling(ArgumentDecl argumentDecl)
    {
        if (!IsBoundGeneric(argumentDecl))
            return false;

        var namedTypeSpec = (NamedTypeSpec)argumentDecl.SwiftTypeSpec;
        var swiftTypeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return !s_structGenerics.Contains(swiftTypeName);
    }

    /// <summary>
    /// Translates the Swift generic type of the given property declaration into a C# type name.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns>The C# type name with generic parameters.</returns>
    /// <exception cref="NotSupportedException">Thrown when the property is not bound generic.</exception>
    public string TranslateBoundGenericTypeToCSharp(PropertyDecl propertyDecl)
    {
        if (!IsBoundGeneric(propertyDecl))
            throw new NotSupportedException(
                $"Attempted to translate to C# name for a non-bound generic property {propertyDecl.Name}");
        var namedTypeSpec = (NamedTypeSpec)propertyDecl.SwiftTypeSpec;
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec);
    }

    /// <summary>
    /// Translates the Swift generic type of the given argument declaration into a C# type name.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The C# type name with generic parameters.</returns>
    /// <exception cref="NotSupportedException">Thrown when the argument is not bound generic.</exception>
    public string TranslateBoundGenericTypeToCSharp(ArgumentDecl argumentDecl)
    {
        if (!IsBoundGeneric(argumentDecl))
            throw new NotSupportedException(
                $"Attempted to translate to C# name for a non-bound generic argument {argumentDecl.Name}");
        var namedTypeSpec = (NamedTypeSpec)argumentDecl.SwiftTypeSpec;
        return TranslateBoundGenericTypeToCSharp(namedTypeSpec);
    }

    /// <summary>
    /// Helper method to convert a Swift <see cref="NamedTypeSpec"/> into its corresponding C# type name.
    /// </summary>
    /// <param name="namedTypeSpec">The named type specification.</param>
    /// <returns>The C# type name string.</returns>
    private string TranslateBoundGenericTypeToCSharp(NamedTypeSpec namedTypeSpec)
    {
        var typeReference = _typeDatabase.GetTypeRecordOrAnyType(namedTypeSpec); // TODO: consider throwing an exception instead

        // If the type falls back to AnyType, don't append generic parameters
        // since AnyType is not a generic type and adding <T1, T2> would be invalid C#
        if (typeReference == TypeDatabaseExtensions.AnyType)
        {
            return typeReference.CSharpTypeName.FullyQualifiedName;
        }

        List<string> translatedGenericParameters = new();
        foreach (var genericParameter in namedTypeSpec.GenericParameters)
        {
            translatedGenericParameters.Add(TranslateTypeSpecToCSharp(genericParameter));
        }

        return typeReference.CSharpTypeName.FullyQualifiedName +
               (translatedGenericParameters.Count > 0
                    ? $"<{string.Join(", ", translatedGenericParameters)}>"
                    : "");
    }

    /// <summary>
    /// Translates any TypeSpec to its C# equivalent.
    /// Handles NamedTypeSpec, ClosureTypeSpec, TupleTypeSpec, and ProtocolListTypeSpec (existentials).
    /// </summary>
    /// <param name="typeSpec">The type specification to translate.</param>
    /// <returns>The C# type name string.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the type specification is not supported.
    /// </exception>
    private string TranslateTypeSpecToCSharp(TypeSpec typeSpec)
    {
        // Handle existential types (including bare 'Any' with 0 protocols and 'any Protocol' syntax)
        if (_existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = _existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && _existentialHandler.IsSupportedExistential(protocolList))
                return _existentialHandler.GetCSharpExistentialType(protocolList);
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        return typeSpec switch
        {
            NamedTypeSpec namedTypeSpec => TranslateBoundGenericTypeToCSharp(namedTypeSpec),
            ClosureTypeSpec closureTypeSpec => TranslateClosureTypeToCSharp(closureTypeSpec),
            TupleTypeSpec tupleTypeSpec => _tupleHandler.GetCSharpTupleType(tupleTypeSpec, TranslateTypeSpecToCSharp),
            _ => throw new NotSupportedException(
                $"Type spec {typeSpec.GetType().Name} ({typeSpec}) is not supported as a generic parameter")
        };
    }

    /// <summary>
    /// Translates a closure type spec to its C# delegate type.
    /// Falls back to object for unsupported closures.
    /// </summary>
    /// <param name="closureTypeSpec">The closure type specification.</param>
    /// <returns>The C# delegate type name.</returns>
    private string TranslateClosureTypeToCSharp(ClosureTypeSpec closureTypeSpec)
    {
        // Check if the closure is supported
        if (!_closureHandler.IsSupportedClosure(closureTypeSpec))
        {
            // For unsupported closures (async, throwing, etc.), fall back to object
            // This allows the binding to compile, though the closure won't be directly usable
            return "object";
        }

        return _closureHandler.GetCSharpDelegateType(closureTypeSpec);
    }

    /// <summary>
    /// Gets the buffer type name used for marshalling the specified bound generic argument.
    /// </summary>
    /// <param name="argumentDecl">The argument declaration.</param>
    /// <returns>The buffer type name.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown if the argument is not a bound generic type.
    /// </exception>
    public string GetBufferType(ArgumentDecl argumentDecl)
    {
        if (!IsBoundGeneric(argumentDecl))
            throw new NotSupportedException(
                $"Attempted to get buffer type for a non-bound generic argument {argumentDecl.Name}");
        var namedTypeSpec = (NamedTypeSpec)argumentDecl.SwiftTypeSpec;

        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(namedTypeSpec.Name);
        if (s_bufferTypeMap.TryGetValue(swiftTypeName, out var bufferType))
            return bufferType;

        // Fallback when no mapping is available.
        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName; // TODO: Consider throwing an exception instead
    }
}

