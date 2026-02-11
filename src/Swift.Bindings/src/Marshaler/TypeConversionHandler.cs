// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Handles automatic type conversions between Swift wrapper types (SwiftString, SwiftArray, SwiftOptional)
/// and idiomatic .NET types (string, IEnumerable&lt;T&gt;, T?).
/// Also handles native type remapping for Swift types that have .NET iOS equivalents (URL → NSUrl, Data → NSData).
/// This makes Swift bindings feel as natural as Obj-C bindings.
/// </summary>
public class TypeConversionHandler
{
    private readonly ITypeDatabase _typeDatabase;

    private static readonly SwiftTypeName SwiftStringTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String");
    private static readonly SwiftTypeName SwiftArrayTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Array");
    private static readonly SwiftTypeName SwiftOptionalTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional");
    private static readonly SwiftTypeName FoundationURLTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.URL");
    private static readonly SwiftTypeName FoundationDataTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data");

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

        // Generic type parameters (e.g., τ_0_0, T) don't have a module qualifier
        if (!namedTypeSpec.HasModule())
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

        // Generic type parameters (e.g., τ_0_0, T) don't have a module qualifier
        if (!namedTypeSpec.HasModule())
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

        // Generic type parameters (e.g., τ_0_0, T) don't have a module qualifier
        if (!namedTypeSpec.HasModule())
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
            // Don't handle Optional<Closure> here - let ClosureHandler deal with it
            if (namedTypeSpec.GenericParameters.Count > 0 &&
                namedTypeSpec.GenericParameters[0] is ClosureTypeSpec)
            {
                return null;
            }

            var innerType = GetElementType(namedTypeSpec, typeTranslator);
            if (innerType == null)
                return null;

            // Use nullable annotation: T?
            return $"{innerType}?";
        }

        return null;
    }

    /// <summary>
    /// Gets the element type name from a generic Swift type (Array&lt;T&gt; or Optional&lt;T&gt;),
    /// applying idiomatic type conversion to the element type itself (e.g., SwiftString → string).
    /// </summary>
    /// <param name="genericType">The generic type specification.</param>
    /// <param name="typeTranslator">Optional function to translate the element type spec to a C# type name.</param>
    /// <returns>The C# element type name, or null if not available.</returns>
    public string? GetElementType(NamedTypeSpec genericType, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (genericType.GenericParameters.Count == 0)
            return null;

        var elementTypeSpec = genericType.GenericParameters[0];

        // Check if the element type itself is convertible (e.g., SwiftString → string)
        var idiomaticElement = GetIdiomaticCSharpType(elementTypeSpec, isParameter: false, typeTranslator);
        if (idiomaticElement != null)
            return idiomaticElement;

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
    /// Checks whether the element type of a generic container was converted to an idiomatic type.
    /// Used to determine if return conversion needs a .Select() projection.
    /// </summary>
    public bool IsElementTypeConverted(NamedTypeSpec genericType, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (genericType.GenericParameters.Count == 0)
            return false;

        var elementTypeSpec = genericType.GenericParameters[0];
        return GetIdiomaticCSharpType(elementTypeSpec, isParameter: false, typeTranslator) != null;
    }

    /// <summary>
    /// Gets the raw (unconverted) element type name from a generic Swift type.
    /// Unlike GetElementType(), this does NOT apply idiomatic conversion to the element.
    /// Used when constructing SwiftArray&lt;SwiftString&gt; in parameter conversion.
    /// </summary>
    private string? GetRawElementType(NamedTypeSpec genericType, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (genericType.GenericParameters.Count == 0)
            return null;

        var elementTypeSpec = genericType.GenericParameters[0];

        if (typeTranslator != null)
            return typeTranslator(elementTypeSpec);

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
            // For element type conversion, we need the raw (unconverted) element type for SwiftArray<T>
            var rawElementType = GetRawElementType(namedTypeSpec, typeTranslator);
            if (rawElementType == null)
                return null;

            // If element type was converted (e.g., string → SwiftString), wrap with .Select()
            if (IsElementTypeConverted(namedTypeSpec, typeTranslator))
            {
                if (IsSwiftString(namedTypeSpec.GenericParameters.FirstOrDefault()))
                {
                    return $"SwiftArray<{rawElementType}>.FromEnumerable({paramName}.Select(e => new SwiftString(e)))";
                }
            }

            // IEnumerable<T> -> SwiftArray<T>
            return $"SwiftArray<{rawElementType}>.FromEnumerable({paramName})";
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            // Don't handle Optional<Closure> here - let ClosureHandler deal with it
            if (namedTypeSpec.GenericParameters.Count > 0 &&
                namedTypeSpec.GenericParameters[0] is ClosureTypeSpec)
            {
                return null;
            }

            // Use raw (unconverted) element type — SwiftOptional<SwiftString>, not SwiftOptional<string>
            var innerType = GetRawElementType(namedTypeSpec, typeTranslator);
            if (innerType == null)
                return null;

            // T? -> SwiftOptional<T>
            // Use pattern matching to handle both value types (Nullable<T>) and reference types (T?).
            // SwiftOptional<T>.FromNullable(T?) with unconstrained T doesn't accept Nullable<T>
            // for value types, so we use `is {} val` to unwrap nullables universally.
            var patternVar = $"{paramName}Val";
            return $"({paramName} is {{}} {patternVar} ? SwiftOptional<{innerType}>.NewSome({patternVar}) : SwiftOptional<{innerType}>.NewNone())";
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
            // If element type was converted (e.g., SwiftString → string), project with .Select()
            if (IsElementTypeConverted(namedTypeSpec, typeTranslator))
            {
                if (IsSwiftString(namedTypeSpec.GenericParameters.FirstOrDefault()))
                {
                    return $"{resultVar}.Select(e => e.ToString()).ToList()";
                }
                // Future: other element conversions can add their projections here
            }
            // SwiftArray<T> implements IReadOnlyList<T>, so cast is safe
            return resultVar;
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            // SwiftOptional<T> -> T? (via implicit operator)
            // GetIdiomaticCSharpType returns null for types handled elsewhere (e.g., Optional<Closure>)
            var idiomaticType = GetIdiomaticCSharpType(namedTypeSpec, false, typeTranslator);
            if (idiomaticType == null)
                return null;
            return $"(({idiomaticType}){resultVar})";
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
            // Use raw (unconverted) element type — SwiftArray<SwiftString>, not SwiftArray<string>
            // GetElementType() would eagerly convert SwiftString→string, breaking marshalling
            var elementType = GetRawElementType(namedTypeSpec, typeTranslator);
            if (elementType == null)
                return null;

            return $"SwiftArray<{elementType}>";
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            // Use raw (unconverted) element type — SwiftOptional<SwiftString>, not SwiftOptional<string>
            var innerType = GetRawElementType(namedTypeSpec, typeTranslator);
            if (innerType == null)
                return null;

            return $"SwiftOptional<{innerType}>";
        }

        return null;
    }

    #region Native Type Remapping (URL → NSUrl, Data → NSData)

    /// <summary>
    /// Determines whether the specified type spec represents Foundation.URL.
    /// </summary>
    public bool IsFoundationURL(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        if (!namedTypeSpec.HasModule())
            return false;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return typeName.Equals(FoundationURLTypeName);
    }

    /// <summary>
    /// Determines whether the specified type spec represents Foundation.Data.
    /// </summary>
    public bool IsFoundationData(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        if (!namedTypeSpec.HasModule())
            return false;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return typeName.Equals(FoundationDataTypeName);
    }

    /// <summary>
    /// Determines whether the specified type has a native type remapping configured.
    /// When true, public method signatures should use the native .NET type (e.g., Foundation.NSUrl)
    /// instead of the Swift wrapper type (e.g., Swift.URL).
    /// </summary>
    public bool HasNativeTypeRemapping(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        if (!namedTypeSpec.HasModule())
            return false;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        if (_typeDatabase.TryGetTypeRecord(typeName, out var typeRecord))
        {
            return typeRecord.NativeTypeName != null;
        }

        return false;
    }

    /// <summary>
    /// Gets the native .NET type name for use in public method signatures.
    /// Returns null if no native remapping is configured.
    /// </summary>
    public string? GetNativeTypeName(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return null;

        if (!namedTypeSpec.HasModule())
            return null;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        if (_typeDatabase.TryGetTypeRecord(typeName, out var typeRecord))
        {
            return typeRecord.NativeTypeName?.FullyQualifiedName;
        }

        return null;
    }

    /// <summary>
    /// Gets the conversion expression for converting a native .NET parameter to a Swift type.
    /// For example: Foundation.NSUrl nsUrl → Swift.URL.FromNSUrl(nsUrl)
    /// </summary>
    /// <param name="paramName">The parameter name.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The conversion expression, or null if no conversion is needed.</returns>
    public string? GetNativeParameterConversion(string paramName, TypeSpec? typeSpec)
    {
        if (IsFoundationURL(typeSpec))
        {
            // Foundation.NSUrl -> Swift.URL
            return $"Swift.URL.FromNSUrl({paramName})";
        }

        if (IsFoundationData(typeSpec))
        {
            // Foundation.NSData -> Swift.Data
            return $"Swift.Data.FromNSData({paramName})";
        }

        return null;
    }

    /// <summary>
    /// Gets the conversion expression for converting a Swift return value to a native .NET type.
    /// For example: Swift.URL url → url.ToNSUrl()
    /// </summary>
    /// <param name="resultVar">The variable containing the Swift result.</param>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <returns>The conversion expression, or null if no conversion is needed.</returns>
    public string? GetNativeReturnConversion(string resultVar, TypeSpec? typeSpec)
    {
        if (IsFoundationURL(typeSpec))
        {
            // Swift.URL -> Foundation.NSUrl
            return $"{resultVar}.ToNSUrl()";
        }

        if (IsFoundationData(typeSpec))
        {
            // Swift.Data -> Foundation.NSData
            return $"{resultVar}.ToNSData()";
        }

        return null;
    }

    /// <summary>
    /// Gets the internal Swift wrapper type name for native-remapped types.
    /// This is used in P/Invoke declarations where the Swift ABI type is needed.
    /// </summary>
    public string? GetSwiftWrapperTypeForNative(TypeSpec? typeSpec)
    {
        if (IsFoundationURL(typeSpec))
        {
            return "Swift.URL";
        }

        if (IsFoundationData(typeSpec))
        {
            return "Swift.Data";
        }

        return null;
    }

    #endregion
}
