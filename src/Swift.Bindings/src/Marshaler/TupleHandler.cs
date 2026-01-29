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

    /// <summary>
    /// Maximum number of tuple elements supported in Phase 1.
    /// C# ValueTuple supports up to 7 elements natively; beyond that requires nesting.
    /// </summary>
    public const int MaxSupportedTupleElements = 7;

    public TupleHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
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
    /// Checks if a type is supported as a tuple element.
    /// </summary>
    private bool IsSupportedTupleElementType(TypeSpec typeSpec)
    {
        // Nested tuples are not supported yet
        if (typeSpec is TupleTypeSpec)
            return false;

        // Closures within tuples are not supported yet
        if (typeSpec is ClosureTypeSpec)
            return false;

        // Named types should be resolvable in the type database and frozen
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Generic parameters in tuples not supported yet
            if (namedType.ContainsGenericParameters)
                return false;

            // Try to get the type record
            if (!_typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
                return false;

            // Only frozen types are supported in Phase 1
            if ((typeRecord.Flags & TypeRecordFlags.Frozen) == 0)
                return false;

            return true;
        }

        // Other type specs (ProtocolList, etc.) not supported
        return false;
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
    /// Translates a TypeSpec element to its C# equivalent type.
    /// </summary>
    private string TranslateElementTypeToCSharp(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Fallback for unsupported types
        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Translates a TypeSpec element to its P/Invoke equivalent type.
    /// </summary>
    private string TranslateElementTypeToPInvoke(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(namedType);
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Fallback for unsupported types
        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }
}
