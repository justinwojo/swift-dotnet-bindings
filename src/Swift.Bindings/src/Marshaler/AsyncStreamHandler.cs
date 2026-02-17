// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Helper class for handling AsyncStream types in Swift bindings.
/// AsyncStream is Swift's primary type for async sequences and is mapped to IAsyncEnumerable in C#.
/// </summary>
public class AsyncStreamHandler
{
    private readonly ITypeDatabase _typeDatabase;

    /// <summary>
    /// The Swift module and type name for AsyncStream.
    /// </summary>
    public const string AsyncStreamTypeName = "_Concurrency.AsyncStream";

    /// <summary>
    /// The Swift module and type name for AsyncThrowingStream.
    /// </summary>
    public const string AsyncThrowingStreamTypeName = "_Concurrency.AsyncThrowingStream";

    public AsyncStreamHandler(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
    }

    /// <summary>
    /// Determines whether the specified type is an AsyncStream or AsyncThrowingStream.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the type is an AsyncStream; otherwise, <c>false</c>.</returns>
    public bool IsAsyncStream(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        return namedType.Name == AsyncStreamTypeName ||
               namedType.Name == AsyncThrowingStreamTypeName;
    }

    /// <summary>
    /// Determines whether the specified property has an AsyncStream type.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns><c>true</c> if the property type is an AsyncStream; otherwise, <c>false</c>.</returns>
    public bool IsAsyncStream(PropertyDecl propertyDecl)
    {
        return IsAsyncStream(propertyDecl.SwiftTypeSpec);
    }

    /// <summary>
    /// Gets the element type from an AsyncStream type.
    /// AsyncStream&lt;T&gt; -> T
    /// </summary>
    /// <param name="typeSpec">The AsyncStream type specification.</param>
    /// <returns>The element type, or null if not an AsyncStream or has no generic parameter.</returns>
    public TypeSpec? GetElementType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        if (!IsAsyncStream(typeSpec))
            return null;

        // AsyncStream<T> has one generic parameter
        if (namedType.GenericParameters.Count == 0)
            return null;

        return namedType.GenericParameters[0];
    }

    /// <summary>
    /// Determines whether the AsyncStream is supported for binding.
    /// The element type must be known in the type database.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if the AsyncStream is supported; otherwise, <c>false</c>.</returns>
    public bool IsSupportedAsyncStream(TypeSpec typeSpec)
    {
        if (!IsAsyncStream(typeSpec))
            return false;

        var elementType = GetElementType(typeSpec);
        if (elementType == null)
            return false;

        // Check if element type is known
        return _typeDatabase.TryGetTypeRecord(elementType, out _);
    }

    /// <summary>
    /// Gets the C# IAsyncEnumerable type for an AsyncStream.
    /// AsyncStream&lt;T&gt; -> IAsyncEnumerable&lt;CSharpT&gt;
    /// </summary>
    /// <param name="typeSpec">The AsyncStream type specification.</param>
    /// <returns>The C# type string.</returns>
    public string GetCSharpAsyncEnumerableType(TypeSpec typeSpec)
    {
        var elementType = GetElementType(typeSpec);
        if (elementType == null)
            return "IAsyncEnumerable<object>";

        var csharpElementType = TranslateElementTypeToCSharp(elementType);
        return $"IAsyncEnumerable<{csharpElementType}>";
    }

    /// <summary>
    /// Translates the element type to its C# equivalent, preserving generic parameters.
    /// </summary>
    private string TranslateElementTypeToCSharp(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Use the TypeSpec overload which handles pointer types, existentials, etc.
            if (_typeDatabase.TryGetTypeRecord(namedType, out var typeRecord))
            {
                var baseName = typeRecord.CSharpTypeName.FullyQualifiedName;

                // Pointer types (UnsafeMutablePointer<T> etc.) map to non-generic IntPtr —
                // don't append generic parameters to a non-generic C# type.
                if (typeRecord == TypeDatabaseExtensions.IntPtrType)
                    return baseName;

                // Preserve generic parameters (e.g., Swift.Array<Element> → SwiftArray<Element>)
                if (namedType.GenericParameters.Count > 0)
                {
                    var translatedParams = namedType.GenericParameters
                        .Select(p => TranslateElementTypeToCSharp(p))
                        .ToList();
                    return $"{baseName}<{string.Join(", ", translatedParams)}>";
                }

                return baseName;
            }
        }

        return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Determines if the AsyncStream is a throwing stream.
    /// AsyncThrowingStream can throw errors during iteration.
    /// </summary>
    /// <param name="typeSpec">The type specification.</param>
    /// <returns><c>true</c> if this is an AsyncThrowingStream; otherwise, <c>false</c>.</returns>
    public bool IsThrowingStream(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return false;

        return namedType.Name == AsyncThrowingStreamTypeName;
    }

    /// <summary>
    /// Gets the Swift wrapper function name for an AsyncStream property.
    /// </summary>
    /// <param name="propertyDecl">The property declaration.</param>
    /// <returns>The Swift wrapper function name.</returns>
    public string GetSwiftWrapperFunctionName(PropertyDecl propertyDecl)
    {
        var parentName = propertyDecl.ParentDecl is TypeDecl typeDecl ? typeDecl.Name : "Module";
        return $"{parentName}_{propertyDecl.Name}_AsyncStream";
    }

    /// <summary>
    /// Gets the C# element type name for an AsyncStream.
    /// </summary>
    /// <param name="typeSpec">The AsyncStream type specification.</param>
    /// <returns>The C# element type name.</returns>
    public string GetCSharpElementType(TypeSpec typeSpec)
    {
        var elementType = GetElementType(typeSpec);
        if (elementType == null)
            return "object";

        return TranslateElementTypeToCSharp(elementType);
    }
}
