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
    /// AsyncStream&lt;T&gt; -> IAsyncEnumerable&lt;CSharpT&gt; where CSharpT is the
    /// public-API element type (e.g., Swift `[X]` projects to `IReadOnlyList&lt;X&gt;`,
    /// not `SwiftArray&lt;X&gt;`). See <see cref="TranslatePublicElementTypeToCSharp"/>.
    /// </summary>
    /// <param name="typeSpec">The AsyncStream type specification.</param>
    /// <returns>The C# type string.</returns>
    public string GetCSharpAsyncEnumerableType(TypeSpec typeSpec)
    {
        var elementType = GetElementType(typeSpec);
        if (elementType == null)
            return "IAsyncEnumerable<object>";

        var csharpElementType = TranslatePublicElementTypeToCSharp(elementType);
        return $"IAsyncEnumerable<{csharpElementType}>";
    }

    /// <summary>
    /// Translates the element type to its C# equivalent for the public API surface,
    /// substituting boundary projections for Swift collection containers so consumers
    /// see standard .NET abstractions instead of the runtime helper types:
    /// <list type="bullet">
    ///   <item><c>Swift.Array&lt;T&gt;</c> → <c>IReadOnlyList&lt;T&gt;</c></item>
    ///   <item><c>Swift.Set&lt;T&gt;</c> → <c>IReadOnlySet&lt;T&gt;</c></item>
    ///   <item><c>Swift.Dictionary&lt;K, V&gt;</c> → <c>IReadOnlyDictionary&lt;K, V&gt;</c></item>
    /// </list>
    /// The runtime channel type (<c>SwiftAsyncStream&lt;SwiftArray&lt;X&gt;&gt;</c>) is
    /// returnable as <c>IAsyncEnumerable&lt;IReadOnlyList&lt;X&gt;&gt;</c> via
    /// <c>IAsyncEnumerable&lt;out T&gt;</c> covariance and the inheritance
    /// <c>SwiftArray&lt;T&gt; : IReadOnlyList&lt;T&gt;</c>. See
    /// Swift collection containers are substituted for standard .NET read-only abstractions
    /// at the public API boundary (SwiftArray → IReadOnlyList, etc.).
    /// </summary>
    private string TranslatePublicElementTypeToCSharp(TypeSpec typeSpec)
    {
        if (typeSpec is NamedTypeSpec namedType)
        {
            // Boundary projection substitution: Swift collection containers surface as
            // standard .NET read-only abstractions at the public API. Channel storage
            // still uses SwiftArray/SwiftSet/SwiftDictionary; covariance handles the
            // implicit conversion at the property getter return.
            if (namedType.Name == "Swift.Array" && namedType.GenericParameters.Count == 1)
            {
                var inner = TranslatePublicElementTypeToCSharp(namedType.GenericParameters[0]);
                return $"IReadOnlyList<{inner}>";
            }
            if (namedType.Name == "Swift.Set" && namedType.GenericParameters.Count == 1)
            {
                var inner = TranslatePublicElementTypeToCSharp(namedType.GenericParameters[0]);
                return $"IReadOnlySet<{inner}>";
            }
            if (namedType.Name == "Swift.Dictionary" && namedType.GenericParameters.Count == 2)
            {
                var k = TranslatePublicElementTypeToCSharp(namedType.GenericParameters[0]);
                var v = TranslatePublicElementTypeToCSharp(namedType.GenericParameters[1]);
                return $"IReadOnlyDictionary<{k}, {v}>";
            }
        }
        return TranslateInternalChannelElementTypeToCSharp(typeSpec);
    }

    /// <summary>
    /// Translates the element type to its C# equivalent for the internal
    /// <c>SwiftAsyncStream&lt;T&gt;</c> channel storage. Preserves the runtime helper
    /// types (<c>SwiftArray&lt;T&gt;</c>, <c>SwiftSet&lt;T&gt;</c>,
    /// <c>SwiftDictionary&lt;K, V&gt;</c>) because <see cref="SwiftAsyncStream{TElement}"/>'s
    /// element callback uses <c>SwiftMarshal.MarshalFromSwift&lt;TElement&gt;</c>, which
    /// requires the runtime container shape to deserialize the Swift payload.
    /// </summary>
    private string TranslateInternalChannelElementTypeToCSharp(TypeSpec typeSpec)
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
                        .Select(p => TranslateInternalChannelElementTypeToCSharp(p))
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
    /// Gets the C# element type name for the public API surface of an AsyncStream
    /// (i.e., the <c>T</c> in the property's <c>IAsyncEnumerable&lt;T&gt;</c> return).
    /// Substitutes boundary projections for Swift collection containers — see
    /// <see cref="TranslatePublicElementTypeToCSharp"/>.
    /// </summary>
    /// <param name="typeSpec">The AsyncStream type specification.</param>
    /// <returns>The public-API C# element type name.</returns>
    public string GetCSharpElementType(TypeSpec typeSpec)
    {
        var elementType = GetElementType(typeSpec);
        if (elementType == null)
            return "object";

        return TranslatePublicElementTypeToCSharp(elementType);
    }

    /// <summary>
    /// Gets the C# element type name for the internal <c>SwiftAsyncStream&lt;T&gt;</c>
    /// channel storage. Preserves runtime helper containers
    /// (<c>SwiftArray&lt;T&gt;</c> etc.) because the channel's element callback uses
    /// <c>SwiftMarshal.MarshalFromSwift&lt;TElement&gt;</c>, which needs the runtime
    /// container shape to deserialize the Swift payload. See
    /// <see cref="TranslateInternalChannelElementTypeToCSharp"/>.
    /// </summary>
    public string GetCSharpInternalChannelElementType(TypeSpec typeSpec)
    {
        var elementType = GetElementType(typeSpec);
        if (elementType == null)
            return "object";

        return TranslateInternalChannelElementTypeToCSharp(elementType);
    }
}
