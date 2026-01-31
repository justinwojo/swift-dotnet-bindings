// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;


/// <summary>
/// Represents a generic parameter name mapping.
/// </summary>
/// <param name="TypeParameter">The name of the generic type parameter e.g. T0.</param>
public record struct GenericParameterCSName(string TypeParameter);

/// <summary>
/// Provides methods for generating names.
/// <summary>
///
public static class NameProvider
{
    // Dictionary of Swift property names that need to be renamed in C#
    // Temporary workaround for https://github.com/dotnet/runtimelab/issues/2997 to keep StoreKit tests passing
    private static readonly Dictionary<string, string> PropertyNameMappings = new()
    {
        { "isEligibleForIntroOffer", "IsEligibleForIntroOfferProperty" },
        { "status", "StatusProperty"}
    };

    /// <summary>
    /// Converts a camelCase string to PascalCase by capitalizing the first letter.
    /// </summary>
    /// <param name="camelCase">The camelCase string to convert.</param>
    /// <returns>The PascalCase string.</returns>
    public static string ToPascalCase(string camelCase)
    {
        if (string.IsNullOrEmpty(camelCase))
            return camelCase;

        // Already PascalCase or all caps
        if (char.IsUpper(camelCase[0]))
            return camelCase;

        return char.ToUpperInvariant(camelCase[0]) + camelCase.Substring(1);
    }

    /// <summary>
    /// Provides the name of the PInvoke method.
    /// Uses a hash of the mangled name to ensure uniqueness for method overloads
    /// that have different Swift parameter types but marshal to the same C# types.
    /// </summary>
    /// <param name="methodDecl">The method declaration.</param>
    /// <returns>The name of the PInvoke method.</returns>
    public static string GetPInvokeName(MethodDecl methodDecl)
    {
        // Use last 8 chars of mangled name hash to disambiguate overloads
        // whose parameters marshal to the same C# types (e.g., URL and ImageRequest both become SafeHandle)
        var mangledHash = Math.Abs(methodDecl.MangledName.GetHashCode()).ToString("X8");
        return $"PInvoke_{methodDecl.Name}_{mangledHash}";
    }


    /// <summary>
    /// Provides the mangled name of the PInvoke method.
    /// </summary>
    /// <param name="methodDecl">The method declaration.</param>
    /// <returns>The mangled name of the PInvoke method.</returns>
    public static string GetMangledName(MethodDecl methodDecl)
    {
        if (methodDecl.IsAsync)
            return $"{methodDecl.MangledName}_async";

        return methodDecl.MangledName;
    }

    /// <summary>
    /// Provides the name of the interface based on a protocol name.
    /// </summary>
    /// <param name="protocolName">The protocol name.</param>
    /// <returns>The name of the interface.</returns>
    public static string GetInterfaceName(string protocolName, string typeName = "")
    {
        var specialCases = new Dictionary<string, string>
        {
            { "Equatable", $"IEquatable<{typeName}>" },
        };

        if (specialCases.TryGetValue(protocolName, out var specialCase))
            return specialCase;

        return $"ISwift{protocolName}";
    }

    /// <summary>
    /// Provides the mapping of generic type parameters.
    /// </summary>
    /// <param name="methodDecl">The method declaration.</param>
    /// <returns>The mapping of generic type parameters.</returns>
    public static Dictionary<string, GenericParameterCSName> GetGenericTypeMapping(MethodDecl methodDecl) =>
        methodDecl.GenericParameters
            .Select((param, i) => (param, i))
            .ToDictionary(x => x.param.TypeName, x => new GenericParameterCSName(
                TypeParameter: $"T{x.i}"
            ));

    /// <summary>
    /// Provides the mapping of generic type parameters for a type declaration.
    /// </summary>
    /// <param name="typeDecl">The type declaration.</param>
    /// <returns>The mapping of generic type parameters.</returns>
    public static Dictionary<string, GenericParameterCSName> GetGenericTypeMappingForType(TypeDecl typeDecl) =>
        typeDecl.GenericParameters
            .Select((param, i) => (param, i))
            .ToDictionary(x => x.param.TypeName, x => new GenericParameterCSName(
                TypeParameter: $"T{x.i}"
            ));

    public static string GetMetadataName(string typeName) => $"{typeName}Metadata";
    public static string GetPayloadName(string argumentName) => $"{argumentName}Payload";
    public static string GetProtocolWitnessTableName(string typeName, string protocolName) => $"{typeName}{protocolName}PWT";

    /// <summary>
    /// Maps visibility to C# access modifier keyword.
    /// </summary>
    public static string GetAccessModifier(Visibility visibility) => visibility switch
    {
        Visibility.Public => "public",
        Visibility.Private => "private",
        _ => throw new ArgumentException($"Unknown visibility: {visibility}")
    };

    /// <summary>
    /// Gets the C# property name for a given Swift property name, converting to PascalCase
    /// and handling reserved keywords and special cases.
    /// </summary>
    /// <param name="swiftPropertyName">The original Swift property name.</param>
    /// <param name="siblingNestedTypeNames">Optional set of nested type names in the same parent type, used for collision detection.</param>
    /// <returns>The appropriate C# property name in PascalCase.</returns>
    public static string GetPropertyName(string swiftPropertyName, IReadOnlySet<string>? siblingNestedTypeNames = null)
    {
        // Check for explicit mappings first
        if (PropertyNameMappings.TryGetValue(swiftPropertyName, out var mappedName))
            return mappedName;

        var pascalName = ToPascalCase(swiftPropertyName);

        // Check for collision with nested types (e.g., property "cacheType" -> "CacheType" collides with nested type "CacheType")
        if (siblingNestedTypeNames != null && siblingNestedTypeNames.Contains(pascalName))
        {
            // Suffix with "Value" to avoid collision (e.g., "CacheType" -> "CacheTypeValue")
            return $"{pascalName}Value";
        }

        return pascalName;
    }

    /// <summary>
    /// Gets the C# variable name for the buffer of a bound generic type.
    /// </summary>
    public static string GetBoundGenericBufferName(string typeName) => $"{typeName}Buffer";

    /// <summary>
    /// Gets the name of the async callback delegate field for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncCallbackFieldName(MethodDecl methodDecl)
    {
        var mangledHash = Math.Abs(methodDecl.MangledName.GetHashCode()).ToString("X8");
        return $"s_{methodDecl.Name}Callback_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async callback method for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncCallbackMethodName(MethodDecl methodDecl)
    {
        var mangledHash = Math.Abs(methodDecl.MangledName.GetHashCode()).ToString("X8");
        return $"{methodDecl.Name}OnComplete_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async error callback delegate field for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncErrorCallbackFieldName(MethodDecl methodDecl)
    {
        var mangledHash = Math.Abs(methodDecl.MangledName.GetHashCode()).ToString("X8");
        return $"s_{methodDecl.Name}ErrorCallback_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async error callback method for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncErrorCallbackMethodName(MethodDecl methodDecl)
    {
        var mangledHash = Math.Abs(methodDecl.MangledName.GetHashCode()).ToString("X8");
        return $"{methodDecl.Name}OnError_{mangledHash}";
    }

    /// <summary>
    /// Gets the C# method name, converting to PascalCase and resolving any collisions with property names.
    /// In Swift, a type can have both a property and a method with the same name,
    /// but C# does not allow this. Methods that collide with properties are suffixed.
    /// </summary>
    /// <param name="methodName">The original method name.</param>
    /// <param name="propertyNames">Set of property names in the same type (already in PascalCase).</param>
    /// <returns>A method name in PascalCase that doesn't collide with any property names.</returns>
    public static string GetMethodName(string methodName, IReadOnlySet<string>? propertyNames)
    {
        var pascalName = ToPascalCase(methodName);
        if (propertyNames != null && propertyNames.Contains(pascalName))
        {
            // Append "Method" suffix to disambiguate from property
            return $"{pascalName}Method";
        }
        return pascalName;
    }
}
