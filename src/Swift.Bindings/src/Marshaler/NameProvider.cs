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
/// Provides a merged generic context combining type-level and method-level generic parameters.
/// This avoids C# name collisions when a method inside a generic type also has its own generic params.
/// Swift uses depth-indexed names (τ_0_0 = type-level, τ_1_0 = method-level) so dictionary keys don't collide,
/// but without an offset the C# output names would both be T0.
/// </summary>
public sealed class GenericContext
{
    public Dictionary<string, GenericParameterCSName> Mapping { get; }

    public GenericContext(Dictionary<string, GenericParameterCSName> mapping)
    {
        Mapping = mapping;
    }

    public static GenericContext Empty { get; } = new(new());

    /// <summary>
    /// Build from a method (uses method's own GenericParameters).
    /// </summary>
    public static GenericContext FromMethod(MethodDecl methodDecl) =>
        new(NameProvider.GetGenericTypeMapping(methodDecl));

    /// <summary>
    /// Build from a type (uses type's GenericParameters).
    /// </summary>
    public static GenericContext FromType(TypeDecl typeDecl) =>
        new(NameProvider.GetGenericTypeMappingForType(typeDecl));

    /// <summary>
    /// Build merged context: type params + method params with offset C# names.
    /// Type params get T0, T1, ...; method-only params continue at T{N}, T{N+1}, ...
    /// Method params that duplicate type params (e.g., when parser copies type params to accessor methods)
    /// are skipped — the type-level mapping takes precedence.
    /// </summary>
    public static GenericContext FromMethodInType(MethodDecl methodDecl, TypeDecl? typeDecl)
    {
        var merged = new Dictionary<string, GenericParameterCSName>();
        int offset = 0;
        if (typeDecl?.IsGeneric == true)
        {
            foreach (var kvp in NameProvider.GetGenericTypeMappingForType(typeDecl))
                merged[kvp.Key] = kvp.Value;
            offset = typeDecl.GenericParameters.Count;
        }
        // Only add method params that are genuinely method-level (not duplicates of type params).
        // The parser copies type generic params to accessor methods, so we must skip those
        // to avoid overwriting τ_0_0 → T0 with τ_0_0 → T1.
        int methodOnlyIndex = 0;
        foreach (var param in methodDecl.GenericParameters)
        {
            if (!merged.ContainsKey(param.TypeName))
            {
                merged[param.TypeName] = new GenericParameterCSName(TypeParameter: $"T{offset + methodOnlyIndex}");
                methodOnlyIndex++;
            }
        }
        return new(merged);
    }

    /// <summary>
    /// Tries to resolve a Swift generic parameter name to its C# type name.
    /// </summary>
    public bool TryResolve(string swiftParamName, out string csTypeName)
    {
        if (Mapping.TryGetValue(swiftParamName, out var csName))
        {
            csTypeName = csName.TypeParameter;
            return true;
        }
        csTypeName = "";
        return false;
    }

    public bool IsEmpty => Mapping.Count == 0;
}

/// <summary>
/// Provides methods for generating names.
/// <summary>
///
public static class NameProvider
{
    // Dictionary of Swift property names that need special renaming in C#.
    // These mappings predate the general collision detection (lines 145-150) which uses "Value" suffix.
    // Keep for backward compatibility with StoreKit bindings that use "Property" suffix.
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

        // Opaque return types (some Protocol) need a wrapper with a unique symbol name
        // to avoid potential self-recursion when the wrapper calls the original function.
        if (methodDecl.CSSignature.Count > 0 &&
            methodDecl.CSSignature.First().SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true })
            return $"{methodDecl.MangledName}_opaque";

        return methodDecl.MangledName;
    }

    /// <summary>
    /// Provides the name of the interface based on a protocol name.
    /// </summary>
    /// <param name="protocolName">The protocol name.</param>
    /// <returns>The name of the interface.</returns>
    public static string GetInterfaceName(string protocolName, string typeName = "", string moduleName = "")
    {
        var specialCases = new Dictionary<string, string>
        {
            { "Equatable", $"IEquatable<{typeName}>" },
        };

        if (specialCases.TryGetValue(protocolName, out var specialCase))
            return specialCase;

        // Runtime-defined interfaces keep the ISwift prefix — these are hardcoded
        // in src/Swift.Runtime/ and not generated by the binding generator.
        // Only apply for Swift stdlib protocols; user-defined protocols with the same
        // name (e.g., "Collection" in a custom module) get the standard I prefix.
        if (_runtimeProtocols.Contains(protocolName) &&
            (string.IsNullOrEmpty(moduleName) || moduleName == "Swift"))
            return $"ISwift{protocolName}";

        return $"I{protocolName}";
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

    /// <summary>
    /// Gets the C# parameter name for an argument declaration.
    /// Prefers the internal Swift name (PrivateName) from swiftinterface data.
    /// Falls back to Name-based logic when swiftinterface data is unavailable.
    ///
    /// The returned name is safe for use in string interpolation for derived
    /// variable names (e.g., {name}Handle, {name}Swift). It does NOT use @
    /// verbatim identifiers because those break concatenation patterns.
    /// </summary>
    /// <param name="arg">The argument declaration.</param>
    /// <returns>A valid C# parameter name.</returns>
    public static string GetCSharpParameterName(ArgumentDecl arg)
    {
        // 1. If PrivateName is populated, prefer it (internal Swift name from swiftinterface)
        if (!string.IsNullOrEmpty(arg.PrivateName))
            return SanitizeForCSharp(arg.PrivateName);

        // 2. If Name is a generated name (arg0, arg1), keep it as fallback
        if (IsGeneratedArgName(arg.Name))
            return arg.Name;

        // 3. Otherwise use Name as-is (including _keyword forms like _for, _using)
        // We keep the _ prefix because derived names ({name}Handle, {name}Swift)
        // must be valid identifiers without @ escaping.
        return arg.Name;
    }

    /// <summary>
    /// Sanitizes a Swift internal parameter name for use as a C# identifier.
    /// Uses _ prefix (not @ verbatim) because derived names ({name}Handle, {name}Swift)
    /// must also be valid identifiers.
    /// </summary>
    private static string SanitizeForCSharp(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Handle C# keywords with _ prefix (not @ because derived names like {name}Handle break)
        if (IsCSharpKeyword(name))
            return $"_{name}";

        // Handle names starting with digits (rare but possible)
        if (char.IsDigit(name[0]))
            return $"_{name}";

        return name;
    }

    /// <summary>
    /// Checks if a parameter name was auto-generated (arg0, arg1, etc.)
    /// These are created by the parser when Swift has "_" (no external label).
    /// </summary>
    public static bool IsGeneratedArgName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith("arg"))
            return false;
        return name.Length > 3 && name.Substring(3).All(char.IsDigit);
    }

    /// <summary>
    /// Strips the underscore prefix added by the parser for C# keywords.
    /// e.g., "_for" -> "for", "_in" -> "in"
    /// </summary>
    public static string StripCSharpKeywordPrefix(string name)
    {
        if (name.Length > 1 && name[0] == '_' && IsCSharpKeyword(name.Substring(1)))
            return name.Substring(1);
        return name;
    }

    /// <summary>
    /// Checks if a name is a C# keyword.
    /// </summary>
    private static bool IsCSharpKeyword(string name)
    {
        return _csharpKeywords.Contains(name);
    }

    private static readonly HashSet<string> _csharpKeywords = new()
    {
        "for", "in", "is", "as", "if", "else", "do", "while", "return",
        "break", "continue", "switch", "case", "default", "try", "catch",
        "throw", "new", "this", "base", "null", "true", "false", "class",
        "struct", "enum", "interface", "public", "private", "protected",
        "internal", "static", "readonly", "const", "override", "virtual",
        "abstract", "sealed", "async", "await", "var", "object", "string",
        "int", "long", "float", "double", "bool", "void", "ref", "out",
        "params", "event", "delegate", "operator", "implicit", "explicit",
        "where", "get", "set", "value", "partial", "using", "namespace"
    };

    /// <summary>
    /// Protocol names whose C# interfaces are defined in the runtime (ISwift{Name})
    /// rather than generated by the binding generator (I{Name}).
    /// These correspond to hardcoded interfaces in src/Swift.Runtime/ and must not be renamed.
    /// </summary>
    private static readonly HashSet<string> _runtimeProtocols = new()
    {
        "Hashable",       // ISwiftHashable (SwiftSet.cs)
        "Collection",     // ISwiftCollection (SwiftArray.cs)
        "DataProtocol",   // ISwiftDataProtocol (Data.cs)
        "ContiguousBytes", // ISwiftContiguousBytes (Data.cs)
    };

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
        Visibility.Internal => "internal",
        _ => throw new ArgumentException($"Unknown visibility: {visibility}")
    };

    /// <summary>
    /// Gets the C# property name for a given Swift property name, converting to PascalCase
    /// and handling reserved keywords and special cases.
    /// </summary>
    /// <param name="swiftPropertyName">The original Swift property name.</param>
    /// <param name="containingTypeName">Optional name of the containing type, used for collision detection (CS0542).</param>
    /// <returns>The appropriate C# property name in PascalCase.</returns>
    public static string GetPropertyName(string swiftPropertyName, string? containingTypeName = null)
    {
        // Check for explicit mappings first
        if (PropertyNameMappings.TryGetValue(swiftPropertyName, out var mappedName))
            return mappedName;

        // Sanitize property wrapper projected values (e.g., $volume -> ProjectedVolume)
        var sanitizedName = SanitizePropertyWrapperName(swiftPropertyName);

        var pascalName = ToPascalCase(sanitizedName);

        // Check for collision with containing type name (CS0542: member names cannot be same as enclosing type)
        // Example: class DotLottieFile { var Animation: Animation? } -> property Animation collides with class name
        if (!string.IsNullOrEmpty(containingTypeName) && pascalName == containingTypeName)
        {
            // Suffix with "Value" to avoid collision
            return $"{pascalName}Value";
        }

        return pascalName;
    }

    /// <summary>
    /// Computes nested type renames needed to avoid property/nested-type name collisions.
    /// Instead of suffixing properties with "Value", we rename the colliding nested types with "Info".
    /// </summary>
    /// <param name="propertyNames">PascalCase property names.</param>
    /// <param name="nestedTypeNames">Nested type names.</param>
    /// <returns>A dictionary mapping original nested type name → renamed name.</returns>
    public static Dictionary<string, string> ComputeNestedTypeRenames(
        IEnumerable<string> propertyNames, IEnumerable<string> nestedTypeNames)
    {
        var propSet = new HashSet<string>(propertyNames);
        var renames = new Dictionary<string, string>();
        foreach (var typeName in nestedTypeNames)
        {
            if (propSet.Contains(typeName))
            {
                renames[typeName] = $"{typeName}Info";
            }
        }
        return renames;
    }

    /// <summary>
    /// Sanitizes property wrapper projected value names.
    /// Swift uses $ prefix for projected values (e.g., $volume), but $ is not valid in C# identifiers.
    /// This converts them to a valid C# name (e.g., $volume -> projectedVolume).
    /// </summary>
    /// <param name="swiftName">The original Swift property or method name.</param>
    /// <returns>A sanitized name with $ prefix converted to "projected" prefix.</returns>
    public static string SanitizePropertyWrapperName(string swiftName)
    {
        if (string.IsNullOrEmpty(swiftName))
            return swiftName;

        if (swiftName.StartsWith("$"))
            return "projected" + swiftName.Substring(1);

        return swiftName;
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

    /// <summary>
    /// Gets the public C# method name with PascalCase, property collision resolution,
    /// and Async suffix for async methods (per .NET naming conventions).
    /// </summary>
    /// <param name="methodName">The original Swift method name.</param>
    /// <param name="isAsync">Whether the method is async.</param>
    /// <param name="propertyNames">Set of property names in the same type (already in PascalCase).</param>
    /// <returns>The public-facing method name.</returns>
    public static string GetPublicMethodName(string methodName, bool isAsync, IReadOnlySet<string>? propertyNames = null)
    {
        var name = GetMethodName(methodName, propertyNames);
        if (isAsync && !name.EndsWith("Async"))
            name = $"{name}Async";
        return name;
    }
}
