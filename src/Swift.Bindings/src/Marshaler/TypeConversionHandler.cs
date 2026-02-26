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
    private static readonly SwiftTypeName SwiftSetTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Set");
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
               IsSwiftSet(namedTypeSpec) ||
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
    /// Determines whether the specified type spec represents Swift.Set.
    /// </summary>
    public bool IsSwiftSet(TypeSpec? typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedTypeSpec)
            return false;

        if (!namedTypeSpec.HasModule())
            return false;

        var typeName = SwiftTypeName.FromTypeSpec(namedTypeSpec);
        return typeName.Equals(SwiftSetTypeName);
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

        // SwiftSet: FromEnumerable() creates disposable
        if (IsSwiftSet(namedTypeSpec))
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
            // byte[] -> Swift.Data
            return $"Swift.Data.FromByteArray({paramName})";
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
            // Swift.Data -> byte[]
            return $"{resultVar}.ToByteArray()";
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
