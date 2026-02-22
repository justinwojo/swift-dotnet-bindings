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
    private static readonly SwiftTypeName SwiftDictionaryTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Dictionary");
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
               IsSwiftDictionary(namedTypeSpec) ||
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
    /// Determines whether the specified type spec represents Swift.Dictionary.
    /// </summary>
    public bool IsSwiftDictionary(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        if (!namedTypeSpec.HasModule())
            return false;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return typeName.Equals(SwiftDictionaryTypeName);
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

        if (IsSwiftDictionary(namedTypeSpec))
        {
            var keyType = GetDictionaryKeyType(namedTypeSpec, typeTranslator);
            var valueType = GetDictionaryValueType(namedTypeSpec, typeTranslator);
            if (keyType == null || valueType == null)
                return null;

            // Parameters use IDictionary<K,V> for flexibility
            // Returns use IReadOnlyDictionary<K,V> for keyed access
            return isParameter
                ? $"IDictionary<{keyType}, {valueType}>"
                : $"IReadOnlyDictionary<{keyType}, {valueType}>";
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            // Don't handle Optional<Closure> here - let ClosureHandler deal with it
            if (namedTypeSpec.GenericParameters.Count > 0 &&
                namedTypeSpec.GenericParameters[0] is ClosureTypeSpec)
            {
                return null;
            }

            // Don't handle Optional<Existential> here - let ExistentialHandler deal with it
            if (namedTypeSpec.GenericParameters.Count > 0)
            {
                var existentialHandler = new ExistentialHandler(_typeDatabase);
                if (existentialHandler.IsExistential(namedTypeSpec.GenericParameters[0]))
                {
                    return null;
                }
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
    /// Gets the idiomatic (converted) key type from a SwiftDictionary type spec.
    /// Applies conversion (e.g., SwiftString → string).
    /// </summary>
    public string? GetDictionaryKeyType(NamedTypeSpec dictType, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (dictType.GenericParameters.Count < 2)
            return null;
        return GetConvertedGenericParam(dictType.GenericParameters[0], typeTranslator);
    }

    /// <summary>
    /// Gets the idiomatic (converted) value type from a SwiftDictionary type spec.
    /// Applies conversion (e.g., SwiftString → string).
    /// </summary>
    public string? GetDictionaryValueType(NamedTypeSpec dictType, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (dictType.GenericParameters.Count < 2)
            return null;
        return GetConvertedGenericParam(dictType.GenericParameters[1], typeTranslator);
    }

    /// <summary>
    /// Checks whether the key type of a dictionary was converted to an idiomatic type.
    /// </summary>
    public bool IsDictionaryKeyTypeConverted(NamedTypeSpec dictType, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (dictType.GenericParameters.Count < 2)
            return false;
        return GetIdiomaticCSharpType(dictType.GenericParameters[0], isParameter: false, typeTranslator) != null;
    }

    /// <summary>
    /// Checks whether the value type of a dictionary was converted to an idiomatic type.
    /// </summary>
    public bool IsDictionaryValueTypeConverted(NamedTypeSpec dictType, Func<TypeSpec, string>? typeTranslator = null)
    {
        if (dictType.GenericParameters.Count < 2)
            return false;
        return GetIdiomaticCSharpType(dictType.GenericParameters[1], isParameter: false, typeTranslator) != null;
    }

    /// <summary>
    /// Gets a converted (idiomatic) type for a generic parameter, falling through to raw lookup.
    /// </summary>
    private string? GetConvertedGenericParam(TypeSpec typeSpec, Func<TypeSpec, string>? typeTranslator)
    {
        var idiomatic = GetIdiomaticCSharpType(typeSpec, isParameter: false, typeTranslator);
        if (idiomatic != null)
            return idiomatic;
        if (typeTranslator != null)
            return typeTranslator(typeSpec);
        if (typeSpec is NamedTypeSpec named)
            return _typeDatabase.GetTypeRecordOrAnyType(named).CSharpTypeName.FullyQualifiedName;
        return null;
    }

    /// <summary>
    /// Builds the value projection lambda for a dictionary return conversion's .AsProjected() call.
    /// Returns the lambda string (e.g., "v => v.ToString()", "v => v.AsProjected(e => e.ToString())"),
    /// or null if no value conversion is needed.
    /// </summary>
    private string? GetDictValueReturnProjection(NamedTypeSpec dictSpec, bool valueConverted, Func<TypeSpec, string>? typeTranslator)
    {
        if (!valueConverted)
            return null;

        var valueSpec = dictSpec.GenericParameters[1];
        if (IsSwiftString(valueSpec))
            return "v => v.ToString()";

        if (valueSpec is NamedTypeSpec valArraySpec && IsSwiftArray(valArraySpec))
        {
            if (IsElementTypeConverted(valArraySpec, typeTranslator) && IsSwiftString(valArraySpec.GenericParameters.FirstOrDefault()))
                return "v => v.AsProjected(e => e.ToString())";
            // Array with non-converted elements: SwiftArray implements IReadOnlyList, cast is implicit
            return "v => (IReadOnlyList<" + GetElementType(valArraySpec, typeTranslator) + ">)v";
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
            // If element type was converted (e.g., SwiftString → string), project with .AsProjected()
            if (IsElementTypeConverted(namedTypeSpec, typeTranslator))
            {
                if (IsSwiftString(namedTypeSpec.GenericParameters.FirstOrDefault()))
                {
                    return $"{resultVar}.AsProjected(e => e.ToString())";
                }
                // Future: other element conversions can add their projections here
            }
            // SwiftArray<T> implements IReadOnlyList<T>, so cast is safe
            return resultVar;
        }

        if (IsSwiftDictionary(namedTypeSpec))
        {
            bool keyConverted = IsDictionaryKeyTypeConverted(namedTypeSpec, typeTranslator);
            bool valueConverted = IsDictionaryValueTypeConverted(namedTypeSpec, typeTranslator);

            if (keyConverted || valueConverted)
            {
                bool keyIsString = keyConverted && IsSwiftString(namedTypeSpec.GenericParameters[0]);
                var valueProjection = GetDictValueReturnProjection(namedTypeSpec, valueConverted, typeTranslator);

                if (keyIsString && valueProjection != null)
                {
                    return $"{resultVar}.AsProjected(k => k.ToString(), k => new SwiftString(k), {valueProjection})";
                }
                else if (keyIsString)
                {
                    return $"{resultVar}.AsProjected(k => k.ToString(), k => new SwiftString(k), v => v)";
                }
                else if (valueProjection != null)
                {
                    return $"{resultVar}.AsProjected({valueProjection})";
                }
            }
            // SwiftDictionary<K,V> implements IReadOnlyDictionary<K,V>, so direct return is safe
            return resultVar;
        }

        if (IsSwiftOptional(namedTypeSpec))
        {
            // SwiftOptional<T> -> T? (via implicit operator)
            // GetIdiomaticCSharpType returns null for types handled elsewhere (e.g., Optional<Closure>)
            var idiomaticType = GetIdiomaticCSharpType(namedTypeSpec, false, typeTranslator);
            if (idiomaticType == null)
                return null;

            // If the element type is itself convertible (e.g., SwiftString → string),
            // we need a two-step conversion: SwiftOptional<SwiftString> → SwiftString? → string?
            // A direct cast to string? from SwiftOptional<SwiftString> is invalid.
            var elementTypeSpec = namedTypeSpec.GenericParameters.FirstOrDefault();
            if (elementTypeSpec != null && IsSwiftString(elementTypeSpec))
            {
                return $"((SwiftString?){resultVar})?.ToString()";
            }

            // Handle Optional<Array<T>> — can't cast SwiftOptional<SwiftArray<T>> to IReadOnlyList<T>?
            // Must unwrap via .Case/.Some and apply inner array conversion (element projection if needed)
            if (elementTypeSpec is NamedTypeSpec elementNamed && IsSwiftArray(elementNamed))
            {
                var arrayReturnConversion = GetReturnConversion($"{resultVar}.Some", elementTypeSpec, typeTranslator);
                return $"({resultVar}.Case == Swift.SwiftOptionalCases.None ? ({idiomaticType})null : {arrayReturnConversion})";
            }

            // Handle Optional<Dictionary<K,V>> — unwrap and apply inner dict conversion
            if (elementTypeSpec is NamedTypeSpec elementDictNamed && IsSwiftDictionary(elementDictNamed))
            {
                var dictReturnConversion = GetReturnConversion($"{resultVar}.Some", elementTypeSpec, typeTranslator);
                return $"({resultVar}.Case == Swift.SwiftOptionalCases.None ? ({idiomaticType})null : {dictReturnConversion})";
            }

            return $"(({idiomaticType}){resultVar})";
        }

        return null;
    }

    #region Disposal Helpers

    /// <summary>
    /// Determines whether a property getter's return value needs disposal via <c>using</c>.
    /// True when the getter returns an IDisposable wrapper that is converted to a value type
    /// (e.g., SwiftString→string copies the data, so the SwiftString can be disposed).
    /// False for SwiftArray (the returned IReadOnlyList IS the array — disposing invalidates it)
    /// and Data (struct, not IDisposable).
    /// </summary>
    public bool RequiresGetterDisposal(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        // SwiftString: .ToString() copies data, original is disposable
        if (IsSwiftString(namedTypeSpec))
            return true;

        // SwiftOptional: wrapper needs disposal after cast/unwrap
        if (IsSwiftOptional(namedTypeSpec))
            return true;

        // URL: .ToNSUrl() copies, original URL is IDisposable
        if (IsFoundationURL(namedTypeSpec))
            return true;

        // SwiftArray: returned IReadOnlyList IS the array — do NOT dispose
        // SwiftDictionary: returned IReadOnlyDictionary IS the dict — do NOT dispose
        // Data: struct, not IDisposable
        return false;
    }

    /// <summary>
    /// Determines whether a property setter's converted value needs disposal via <c>using</c>.
    /// True when the setter creates an IDisposable wrapper from the idiomatic value
    /// (e.g., <c>new SwiftString(value)</c>). False for Data (struct, not IDisposable).
    /// </summary>
    public bool RequiresSetterDisposal(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        // SwiftString: new SwiftString(value) creates disposable
        if (IsSwiftString(namedTypeSpec))
            return true;

        // SwiftArray: FromEnumerable() creates disposable
        if (IsSwiftArray(namedTypeSpec))
            return true;

        // SwiftDictionary: FromDictionary() creates disposable
        if (IsSwiftDictionary(namedTypeSpec))
            return true;

        // SwiftOptional: NewSome()/NewNone() creates disposable
        if (IsSwiftOptional(namedTypeSpec))
            return true;

        // URL: conversion creates disposable URL
        if (IsFoundationURL(namedTypeSpec))
            return true;

        // Data: struct, not IDisposable
        return false;
    }

    #endregion

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
