// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Handles automatic type conversions between Swift wrapper types (SwiftString, SwiftArray, SwiftOptional)
/// and idiomatic .NET types (string, IEnumerable&lt;T&gt;, T?).
/// This makes Swift bindings feel as natural as Obj-C bindings.
/// </summary>
public class TypeConversionHandler
{
    private readonly ITypeDatabase _typeDatabase;

    private static readonly SwiftTypeName SwiftStringTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String");
    private static readonly SwiftTypeName SwiftArrayTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array");
    private static readonly SwiftTypeName SwiftOptionalTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");

    public TypeConversionHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
    }

    /// <summary>
    /// Determines whether the specified type spec represents a type that can be
    /// automatically converted to/from an idiomatic .NET type.
    /// </summary>
    /// <param name="typeSpec">The type specification to check.</param>
    /// <returns>True if the type can be converted; otherwise, false.</returns>
    public bool IsConvertibleType(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        return IsSwiftString(namedTypeSpec) ||
               IsSwiftArray(namedTypeSpec) ||
               IsSwiftOptional(namedTypeSpec);
    }

    /// <summary>
    /// Determines whether the specified type spec represents Swift.String.
    /// </summary>
    public bool IsSwiftString(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return typeName.Equals(SwiftStringTypeName);
    }

    /// <summary>
    /// Determines whether the specified type spec represents Swift.Array.
    /// </summary>
    public bool IsSwiftArray(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return typeName.Equals(SwiftArrayTypeName);
    }

    /// <summary>
    /// Determines whether the specified type spec represents Swift.Optional.
    /// </summary>
    public bool IsSwiftOptional(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return typeName.Equals(SwiftOptionalTypeName);
    }

    /// <summary>
    /// Gets the idiomatic C# type name for the specified Swift type.
    /// </summary>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="isParameter">True if this is a parameter type (uses IEnumerable for arrays);
    /// false if this is a return type (uses IReadOnlyList for arrays).</param>
    /// <param name="typeTranslator">Optional function to translate inner type specs to C# type names.</param>
    /// <returns>The idiomatic C# type name, or null if the type is not convertible.</returns>
    public string? GetIdiomaticCSharpType(TypeSpec? typeSpec, bool isParameter, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return null;

        if (IsSwiftString(namedTypeSpec))
        {
            return "string";
        }

        if (IsSwiftArray(namedTypeSpec))
        {
            var elementType = GetElementType(namedTypeSpec, typeTranslator);
            if (elementType == null)
                return null;

            // Parameters use IEnumerable<T> for flexibility
            // Returns use IReadOnlyList<T> for index access
            return isParameter
                ? $"IEnumerable<{elementType}>"
                : $"IReadOnlyList<{elementType}>";
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            var innerType = GetElementType(namedTypeSpec, typeTranslator);
            if (innerType == null)
                return null;

            // Use nullable annotation: T?
            return $"{innerType}?";
        }

        return null;
    }

    /// <summary>
    /// Gets the element type name from a generic Swift type (Array&lt;T&gt; or Optional&lt;T&gt;).
    /// </summary>
    /// <param name="genericType">The generic type specification.</param>
    /// <param name="typeTranslator">Optional function to translate the element type spec to a C# type name.</param>
    /// <returns>The C# element type name, or null if not available.</returns>
    public string? GetElementType(NamedTypeSpec genericType, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (genericType.GenericParameters.Count == 0)
            return null;

        var elementTypeSpec = genericType.GenericParameters[0];

        // If we have a translator, use it
        if (typeTranslator != null)
        {
            return typeTranslator(elementTypeSpec);
        }

        // Fall back to simple type lookup
        if (elementTypeSpec is NamedTypeSpec elementNamedType)
        {
            var typeRecord = _typeDatabase.GetTypeRecordOrAnyType(elementNamedType);
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        return null;
    }

    /// <summary>
    /// Gets the conversion expression for converting an idiomatic .NET parameter to a Swift type.
    /// </summary>
    /// <param name="paramName">The parameter name.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="typeTranslator">Optional function to translate inner type specs to C# type names.</param>
    /// <returns>The conversion expression, or null if no conversion is needed.</returns>
    public string? GetParameterConversion(string paramName, TypeSpec? typeSpec, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return null;

        if (IsSwiftString(namedTypeSpec))
        {
            // string -> SwiftString
            return $"new SwiftString({paramName})";
        }

        if (IsSwiftArray(namedTypeSpec))
        {
            var elementType = GetElementType(namedTypeSpec, typeTranslator);
            if (elementType == null)
                return null;

            // IEnumerable<T> -> SwiftArray<T>
            return $"SwiftArray<{elementType}>.FromEnumerable({paramName})";
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            var innerType = GetElementType(namedTypeSpec, typeTranslator);
            if (innerType == null)
                return null;

            // T? -> SwiftOptional<T>
            return $"SwiftOptional<{innerType}>.FromNullable({paramName})";
        }

        return null;
    }

    /// <summary>
    /// Gets the conversion expression for converting a Swift return value to an idiomatic .NET type.
    /// </summary>
    /// <param name="resultVar">The variable containing the Swift result.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="typeTranslator">Optional function to translate inner type specs to C# type names.</param>
    /// <returns>The conversion expression, or null if no conversion is needed.</returns>
    public string? GetReturnConversion(string resultVar, TypeSpec? typeSpec, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return null;

        if (IsSwiftString(namedTypeSpec))
        {
            // SwiftString -> string (via implicit operator)
            return $"{resultVar}.ToString()";
        }

        if (IsSwiftArray(namedTypeSpec))
        {
            // SwiftArray<T> implements IReadOnlyList<T>, so cast is safe
            return resultVar;
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            // SwiftOptional<T> -> T? (via implicit operator)
            return $"(({GetIdiomaticCSharpType(namedTypeSpec, false, typeTranslator)}){resultVar})";
        }

        return null;
    }

    /// <summary>
    /// Gets the Swift wrapper type name for use in P/Invoke declarations.
    /// </summary>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="typeTranslator">Optional function to translate inner type specs to C# type names.</param>
    /// <returns>The Swift wrapper type name.</returns>
    public string? GetSwiftWrapperType(TypeSpec? typeSpec, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return null;

        if (IsSwiftString(namedTypeSpec))
        {
            return "SwiftString";
        }

        if (IsSwiftArray(namedTypeSpec))
        {
            var elementType = GetElementType(namedTypeSpec, typeTranslator);
            if (elementType == null)
                return null;

            return $"SwiftArray<{elementType}>";
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            var innerType = GetElementType(namedTypeSpec, typeTranslator);
            if (innerType == null)
                return null;

            return $"SwiftOptional<{innerType}>";
        }

        return null;
    }
}
