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
    /// Build a context for Self-requirement protocols: τ_0_0 → TSelf.
    /// </summary>
    public static GenericContext ForProtocolSelf() =>
        new(new Dictionary<string, GenericParameterCSName>
        {
            ["τ_0_0"] = new GenericParameterCSName("TSelf")
        });

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
                merged[param.TypeName] = new GenericParameterCSName(TypeParameter: NameProvider.GetCSharpGenericParameterName(param, offset + methodOnlyIndex));
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
    /// Converts a camelCase or SCREAMING_CASE string to PascalCase.
    /// </summary>
    /// <param name="camelCase">The string to convert.</param>
    /// <returns>The PascalCase string.</returns>
    public static string ToPascalCase(string camelCase)
    {
        if (string.IsNullOrEmpty(camelCase))
            return camelCase;

        // SCREAMING_CASE → PascalCase (e.g., "CAMERA_DIRECTION" → "CameraDirection")
        if (IsScreamingCase(camelCase))
            return ScreamingCaseToPascalCase(camelCase);

        // Already PascalCase
        if (char.IsUpper(camelCase[0]))
            return camelCase;

        return char.ToUpperInvariant(camelCase[0]) + camelCase.Substring(1);
    }

    /// <summary>
    /// Detects SCREAMING_CASE: 2+ chars, all uppercase letters/digits/underscores, at least one letter.
    /// </summary>
    private static bool IsScreamingCase(string s)
    {
        if (s.Length < 2) return false;
        bool hasLetter = false;
        foreach (var c in s)
        {
            if (char.IsLetter(c))
            {
                if (!char.IsUpper(c)) return false;
                hasLetter = true;
            }
            else if (c != '_' && !char.IsDigit(c))
            {
                return false;
            }
        }
        return hasLetter;
    }

    /// <summary>
    /// Converts SCREAMING_CASE to PascalCase by splitting on underscores and title-casing each segment.
    /// </summary>
    private static string ScreamingCaseToPascalCase(string s)
    {
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
                sb.Append(part.Substring(1).ToLowerInvariant());
        }
        return sb.ToString();
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
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
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

        // Optional pointer wrappers need a unique symbol because the wrapper function takes
        // UnsafeRawPointer where the original takes Optional<String> by value.
        if (methodDecl.HasOptionalPointerWrapper)
            return $"{methodDecl.MangledName}_optbuf";

        // Closure Cdecl wrappers need a unique symbol because the wrapper function has
        // different parameter types (UnsafeMutableRawPointer pairs) than the original
        // Swift function (native closure types). @_silgen_name with the original symbol
        // would cause a function type mismatch error.
        if (methodDecl.HasClosureCdeclWrapper)
            return $"{methodDecl.MangledName}_cdecl";

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

        // Avoid collision with well-known System namespace interfaces (e.g., System.IDisposable).
        // Unlike _runtimeProtocols these are generator-emitted, not runtime-defined,
        // so no module gate — the collision applies regardless of source module.
        if (_systemCollisionNames.Contains(protocolName))
            return $"ISwift{protocolName}";

        return $"I{protocolName}";
    }

    /// <summary>
    /// Converts a Swift sugared generic parameter name to idiomatic C#.
    /// "T" → "T", "U" → "TU", "Key" → "TKey", "Element" → "TElement", etc.
    /// Falls back to "T{index}" for τ_N_M names (no sugared sig available).
    /// </summary>
    public static string GetCSharpGenericParameterName(GenericArgumentDecl param, int index)
    {
        var sugared = param.SugaredTypeName;
        if (string.IsNullOrEmpty(sugared) || sugared.StartsWith("τ_"))
            return $"T{index}";
        // Single uppercase letter: use as-is (T, U, V, W — standard C# convention)
        if (sugared.Length == 1 && char.IsUpper(sugared[0]))
            return sugared;
        // Multi-character: prefix with T (Key → TKey, Element → TElement)
        return $"T{sugared}";
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
                TypeParameter: GetCSharpGenericParameterName(x.param, x.i)
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
                TypeParameter: GetCSharpGenericParameterName(x.param, x.i)
            ));

    /// <summary>
    /// Gets the C# parameter name for an argument declaration.
    /// Prefers the internal Swift name (PrivateName) from swiftinterface data.
    /// For generated names (arg0, arg1), derives a meaningful name from the type.
    ///
    /// The returned name is safe for use in string interpolation for derived
    /// variable names (e.g., {name}Handle, {name}Swift). It does NOT use @
    /// verbatim identifiers because those break concatenation patterns.
    /// </summary>
    /// <param name="arg">The argument declaration.</param>
    /// <returns>A valid C# parameter name.</returns>
    public static string GetCSharpParameterName(ArgumentDecl arg)
    {
        // If a deduplicated name has been precomputed, use it
        if (arg.CSharpName != null)
            return arg.CSharpName;

        // 1. If PrivateName is populated, prefer it (internal Swift name from swiftinterface)
        if (!string.IsNullOrEmpty(arg.PrivateName))
            return SanitizeForCSharp(arg.PrivateName);

        // Swift unnamed parameter labels can arrive as literal "_" and should be
        // converted to an identifier that can be deduplicated.
        if (arg.Name == "_")
        {
            var derived = DeriveParameterNameFromType(arg.SwiftTypeSpec);
            return derived ?? "param";
        }

        // 2. If Name is a generated name (arg0, arg1), derive from the type
        if (IsGeneratedArgName(arg.Name))
        {
            var derived = DeriveParameterNameFromType(arg.SwiftTypeSpec);
            if (derived != null)
            {
                // Append index suffix for arg1+ to reduce collision risk
                var argIndex = arg.Name.Substring(3);
                return argIndex == "0" ? derived : $"{derived}{argIndex}";
            }
            return arg.Name;
        }

        // 3. Otherwise use Name as-is (including _keyword forms like _for, _using)
        // We keep the _ prefix because derived names ({name}Handle, {name}Swift)
        // must be valid identifiers without @ escaping.
        return arg.Name;
    }

    /// <summary>
    /// Pre-computes deduplicated C# parameter names for all arguments in a method.
    /// When two parameters normalize to the same name, appends numeric suffixes (value, value2, value3).
    /// Stores results in ArgumentDecl.CSharpName so all consumers see consistent names.
    /// </summary>
    /// <param name="arguments">The method's CSSignature (first element is return type, skipped).</param>
    public static void DeduplicateParameterNames(IList<ArgumentDecl> arguments)
    {
        DeduplicateParameterNamesCore(arguments.Skip(1));
    }

    /// <summary>
    /// Pre-computes deduplicated C# parameter names for parameter-only lists (no return slot).
    /// </summary>
    public static void DeduplicateParameterNamesForParameterList(IEnumerable<ArgumentDecl> parameters)
    {
        DeduplicateParameterNamesCore(parameters);
    }

    private static void DeduplicateParameterNamesCore(IEnumerable<ArgumentDecl> parameters)
    {
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var arg in parameters)
        {
            // Compute the base name using existing logic (bypass CSharpName to get raw name)
            arg.CSharpName = null;
            var baseName = GetCSharpParameterName(arg);

            if (usedNames.Add(baseName))
            {
                arg.CSharpName = baseName;
            }
            else
            {
                int suffix = 2;
                while (!usedNames.Add($"{baseName}{suffix}"))
                    suffix++;
                arg.CSharpName = $"{baseName}{suffix}";
            }
        }
    }

    /// <summary>
    /// Derives a meaningful parameter name from a Swift type specification.
    /// Strips module prefixes and common type prefixes (UI, NS) to produce
    /// short, idiomatic camelCase names.
    /// </summary>
    private static string? DeriveParameterNameFromType(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return "value";

        var typeName = namedType.NameWithoutModule;

        // For nested types (e.g., "ImageRequest.ThumbnailOptions"), use only the
        // leaf type name to avoid dots in the derived parameter name.
        var lastDot = typeName.LastIndexOf('.');
        if (lastDot >= 0)
            typeName = typeName.Substring(lastDot + 1);

        // Optional<T> → derive from inner type
        if (typeName == "Optional" && namedType.GenericParameters.Count == 1)
            return DeriveParameterNameFromType(namedType.GenericParameters[0]);

        // Array<T> → "items"
        if (typeName == "Array" && namedType.GenericParameters.Count >= 1)
            return "items";

        // Dictionary<K,V> → "dictionary"
        if (typeName == "Dictionary" && namedType.GenericParameters.Count >= 2)
            return "dictionary";

        // Primitives → generic "value"
        if (_primitiveTypeNames.Contains(typeName))
            return "value";

        // Bool → "flag"
        if (typeName == "Bool")
            return "flag";

        // Types ending in Error → "error"
        if (typeName.EndsWith("Error") && typeName.Length > 5)
            return "error";

        // Strip common Apple prefixes
        if (typeName.Length > 2 && typeName.StartsWith("UI") && char.IsUpper(typeName[2]))
            typeName = typeName.Substring(2);
        else if (typeName.Length > 2 && typeName.StartsWith("NS") && char.IsUpper(typeName[2]))
            typeName = typeName.Substring(2);

        // camelCase — handle all-caps words like URL, ID
        string result;
        if (typeName.All(char.IsUpper))
            result = typeName.ToLowerInvariant();
        else
            result = char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
        return SanitizeForCSharp(result);
    }

    /// <summary>
    /// Primitive Swift type names that should produce generic "value" parameter names.
    /// </summary>
    private static readonly HashSet<string> _primitiveTypeNames = new()
    {
        "Int", "Int8", "Int16", "Int32", "Int64",
        "UInt", "UInt8", "UInt16", "UInt32", "UInt64",
        "Float", "Float16", "Float32", "Float64", "Double",
        "String", "Character",
    };

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
    /// Swift keywords that require backtick escaping when used as identifiers in generated Swift code.
    /// </summary>
    private static readonly HashSet<string> _swiftKeywords = new()
    {
        "as", "break", "case", "catch", "class", "continue", "default", "defer",
        "do", "else", "enum", "extension", "fallthrough", "false", "for", "func",
        "guard", "if", "import", "in", "init", "inout", "internal", "is", "let",
        "nil", "operator", "private", "protocol", "public", "repeat", "rethrows",
        "return", "self", "Self", "static", "struct", "subscript", "super",
        "switch", "throw", "throws", "true", "try", "typealias", "var", "where", "while"
    };

    /// <summary>
    /// Checks if a name is a Swift keyword.
    /// </summary>
    public static bool IsSwiftKeyword(string name) => _swiftKeywords.Contains(name);

    /// <summary>
    /// Escapes a name with backticks if it is a Swift keyword.
    /// Use this when emitting Swift code that uses C# parameter names as Swift identifiers.
    /// </summary>
    public static string EscapeSwiftKeyword(string name)
        => IsSwiftKeyword(name) ? $"`{name}`" : name;

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
        "Encoder",        // ISwiftEncoder (ISwiftEncoder.cs)
        "Decoder",        // ISwiftDecoder (ISwiftEncoder.cs)
    };

    /// <summary>
    /// Protocol names that collide with well-known System namespace interfaces
    /// when the standard I{Name} prefix is applied. For example, a Swift protocol
    /// named "Disposable" would produce "IDisposable", colliding with System.IDisposable.
    /// These get the ISwift{Name} prefix instead (same as runtime protocols, but
    /// applied regardless of source module since the collision is with the System namespace).
    /// </summary>
    private static readonly HashSet<string> _systemCollisionNames = new()
    {
        "Disposable",     // IDisposable → ISwiftDisposable (RxSwift)
    };

    /// <summary>
    /// Method names that collide with well-known .NET base class methods inherited by
    /// generated C# classes (e.g., IDisposable.Dispose() from SafeHandle). Swift methods
    /// with these PascalCase names get a "Swift" suffix to avoid CS0111.
    /// </summary>
    private static readonly HashSet<string> _inheritedMethodCollisions = new()
    {
        "Dispose",        // IDisposable.Dispose() from SafeHandle (RxSwift dispose())
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
    /// Applies a property rename if one exists, otherwise returns the original name.
    /// Use this at every site that emits or tracks a member name that could be renamed.
    /// </summary>
    public static string GetFinalMemberName(string name, IReadOnlyDictionary<string, string>? renames)
        => renames?.TryGetValue(name, out var renamed) == true ? renamed : name;

    /// <summary>
    /// Computes property/member renames needed to avoid property/nested-type name collisions.
    /// When a member name collides with a nested type name, the member is renamed with a "Value" suffix.
    /// </summary>
    /// <param name="memberNames">PascalCase member names (properties, enum cases).</param>
    /// <param name="nestedTypeNames">Nested type names.</param>
    /// <returns>A dictionary mapping original member name → renamed name.</returns>
    public static Dictionary<string, string> ComputePropertyRenamesForNestedTypeCollisions(
        IEnumerable<string> memberNames, IEnumerable<string> nestedTypeNames)
    {
        var typeNameSet = new HashSet<string>(nestedTypeNames);
        var memberNameSet = new HashSet<string>(memberNames);
        var renames = new Dictionary<string, string>();
        foreach (var memberName in memberNames)
        {
            if (typeNameSet.Contains(memberName))
            {
                var candidate = $"{memberName}Value";
                var suffix = 2;
                while (memberNameSet.Contains(candidate) || typeNameSet.Contains(candidate) || renames.ContainsValue(candidate))
                {
                    candidate = $"{memberName}Value{suffix}";
                    suffix++;
                }
                renames[memberName] = candidate;
            }
        }
        return renames;
    }

    /// <summary>
    /// Computes property renames for a type declaration to resolve property/nested-type name collisions.
    /// Returns a dictionary mapping original member name → renamed name. Does NOT modify TypeDatabase.
    /// </summary>
    /// <param name="typeDecl">The type declaration containing properties and nested types.</param>
    /// <param name="typeDatabase">The type database for type resolution checks.</param>
    /// <returns>A dictionary mapping original member name → renamed name.</returns>
    public static Dictionary<string, string> ComputePropertyRenames(TypeDecl typeDecl, ITypeDatabase typeDatabase)
    {
        // Only include properties whose types can be resolved — properties with unsupported types
        // (AnyType, SwiftUI references, etc.) are skipped by the emitter and should not trigger
        // property renames. Without this filter, a skipped property named "priority" would cause
        // a nested type "Priority" to unnecessarily rename the property to "PriorityValue".
        // We use a lightweight type-resolution check rather than the full CanEmitProperty() which
        // also rejects properties for structural reasons (no accessors, etc.) that don't affect naming.
        var memberNames = typeDecl.Properties
            .Where(p => !MemberEmissionValidator.HasUnsupportedPropertyType(p, typeDatabase))
            .Select(p => GetPropertyName(p.Name, typeDecl.Name));

        // Also include AsyncStream properties that will be emitted as IAsyncEnumerable<T>.
        // HasUnsupportedPropertyType excludes these (AsyncStream is from _Concurrency module),
        // but the emitter does emit them as properties, so they must participate in collision detection.
        var asyncStreamHandler = new AsyncStreamHandler(typeDatabase);
        var asyncStreamPropertyNames = typeDecl.Properties
            .Where(p => asyncStreamHandler.IsAsyncStream(p.SwiftTypeSpec) && asyncStreamHandler.IsSupportedAsyncStream(p.SwiftTypeSpec))
            .Select(p => GetPropertyName(p.Name, typeDecl.Name));
        memberNames = memberNames.Concat(asyncStreamPropertyNames);

        // For enums, include PascalCase'd case names in the collision set.
        // Enum cases produce factory methods (e.g., case "pong" → static method "Pong()"),
        // which collide with nested types of the same PascalCase name (CS0102).
        if (typeDecl is EnumDecl enumDecl)
        {
            var caseNames = enumDecl.Cases.Select(c => ToPascalCase(c.Name));
            memberNames = memberNames.Concat(caseNames);
        }

        var nestedTypeNames = typeDecl.Types.Select(t => t.Name);
        return ComputePropertyRenamesForNestedTypeCollisions(memberNames, nestedTypeNames);
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
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        return $"s_{methodDecl.Name}Callback_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async callback method for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncCallbackMethodName(MethodDecl methodDecl)
    {
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        return $"{methodDecl.Name}OnComplete_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async error callback delegate field for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncErrorCallbackFieldName(MethodDecl methodDecl)
    {
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
        return $"s_{methodDecl.Name}ErrorCallback_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async error callback method for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncErrorCallbackMethodName(MethodDecl methodDecl)
    {
        var mangledHash = EmitterUtility.DeterministicHash8(methodDecl.MangledName);
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
    /// Common English verbs used as method name prefixes in .NET APIs.
    /// Used to detect whether a PascalCase method name already starts with a verb,
    /// so we can avoid adding a redundant "Get" prefix.
    /// </summary>
    private static readonly HashSet<string> _verbPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Get", "Set", "Create", "Make", "Build", "Load", "Fetch", "Find",
        "Remove", "Delete", "Clear", "Reset", "Start", "Stop", "Open", "Close",
        "Read", "Write", "Send", "Process", "Validate", "Check", "Is", "Has",
        "Can", "Add", "Update", "Refresh", "Store", "Save", "Encode", "Decode",
        "Register", "Sort", "Filter", "Format", "Render", "Configure",
        "Initialize", "Dispose", "Cancel", "Resume", "Invalidate", "Prefetch",
        "Cache", "Purge", "Run", "Execute", "Perform", "Apply", "Try",
        "Flush", "Notify", "Log", "Parse", "Merge", "Split", "Map",
        "Reduce", "Transform", "Convert", "Extract", "Insert", "Append",
        "Prepend", "Push", "Pop", "Enqueue", "Dequeue", "Show", "Hide",
        "Enable", "Disable", "Connect", "Disconnect", "Subscribe", "Unsubscribe",
        "Publish", "Emit", "Trigger", "Handle", "Observe", "Wait", "Resolve",
        "Reject", "Throw", "Catch", "Retry", "Abort", "Suspend", "Yield",
        "Allocate", "Deallocate", "Release", "Retain", "Copy", "Clone", "Move",
        "Swap", "Compare", "Contains", "Equals", "Hash", "Print", "Dump",
        "To", "From", "With", "Decompose", "Compose", "Compute", "Calculate",
        "Flatten", "Normalize", "Serialize", "Deserialize", "Marshal", "Unmarshal",
        "Dispatch", "Invoke", "Call", "Request", "Respond", "Receive",
        "Destroy", "Finalize", "Verify", "Assert", "Ensure", "Require",
        "Supply", "Provide", "Produce", "Consume", "Generate", "Derive",
        "Wrap", "Unwrap", "Pack", "Unpack", "Zip", "Unzip",
        "Attach", "Detach", "Bind", "Unbind", "Link", "Unlink",
        "Lock", "Unlock", "Acquire", "Relinquish",
        "Traverse", "Visit", "Iterate", "Enumerate", "Scan", "Seek",
        "Interpolate", "Animate", "Layout", "Measure", "Draw", "Paint",
        "Scroll", "Navigate", "Route", "Redirect",
        "Authorize", "Authenticate", "Revoke", "Grant", "Deny",
        "Increment", "Decrement", "Negate", "Invert", "Reverse", "Rotate",
        "Compress", "Decompress", "Encrypt", "Decrypt", "Sign",
        "Queue", "Schedule", "Postpone", "Defer", "Delay",
        "Broadcast", "Multicast", "Relay", "Forward",
        "Put", "Patch", "Post",
        "Accept", "Accepts", "Pass", "Passes", "Sum", "Sums",
        "Confirm", "Present", "Dismiss", "Select", "Deselect",
        "Submit", "Complete", "Finish", "Collect",
    };

    /// <summary>
    /// Checks whether a PascalCase method name starts with a recognized verb.
    /// The verb must be followed by an uppercase letter or end of string to avoid
    /// false positives (e.g., "Caching" should not match "Cache").
    /// </summary>
    private static bool StartsWithVerb(string pascalName)
    {
        foreach (var verb in _verbPrefixes)
        {
            if (pascalName.StartsWith(verb, StringComparison.Ordinal) &&
                (pascalName.Length == verb.Length || char.IsUpper(pascalName[verb.Length])))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Strips a leading "async" or "Async" prefix from a Swift method name.
    /// Swift methods like "asyncGetString" or "AsyncData" have the async semantics
    /// expressed in the suffix ("Async") in .NET conventions, not as a prefix.
    /// </summary>
    private static string StripAsyncPrefix(string methodName)
    {
        if (methodName.Length > 5 && methodName.StartsWith("async", StringComparison.OrdinalIgnoreCase))
        {
            var rest = methodName.Substring(5);
            // "asyncGetString" → "getString", "AsyncData" → "Data"
            // Only strip if the next char is uppercase (avoids stripping from words like "asyncify")
            if (char.IsUpper(rest[0]))
                return rest;
            // "asyncGetString" (camelCase) — next char is uppercase after conversion
            // Actually for camelCase "asyncGetString", rest = "GetString" which starts uppercase. Good.
        }
        return methodName;
    }

    /// <summary>
    /// Gets the public C# method name with PascalCase, property collision resolution,
    /// verb prefix for noun-only names, async prefix stripping, and Async suffix
    /// for async methods (per .NET naming conventions).
    /// </summary>
    /// <param name="methodName">The original Swift method name.</param>
    /// <param name="isAsync">Whether the method is async.</param>
    /// <param name="hasReturnValue">Whether the method has a non-void return value.</param>
    /// <param name="propertyNames">Set of property names in the same type (already in PascalCase).</param>
    /// <returns>The public-facing method name.</returns>
    public static string GetPublicMethodName(string methodName, bool isAsync, bool hasReturnValue = false, IReadOnlySet<string>? propertyNames = null, bool isSelfReturning = false, string? parentTypeName = null)
    {
        // 1. Strip leading async/Async prefix (Swift convention → .NET suffix convention)
        //    Only strip for actual async methods — a sync property named "asyncInstance"
        //    should keep its prefix to avoid getter name collisions (e.g., Instance_Get).
        var strippedName = isAsync ? StripAsyncPrefix(methodName) : methodName;

        // 2. PascalCase
        var name = ToPascalCase(strippedName);

        // 3. Add "Get" prefix for noun-only names with a return value
        //    Do this BEFORE property collision check so "Data" → "GetData" no longer collides
        //    Skip for self-returning methods (fluent/builder pattern: EqualTo(), Accessibility(), etc.)
        if (hasReturnValue && !StartsWithVerb(name) && !isAsync && !isSelfReturning)
            name = $"Get{name}";

        // 4. Property collision resolution (only if still colliding after verb prefix)
        if (propertyNames != null && propertyNames.Contains(name))
            name = $"{name}Method";

        // 4b. Inherited method collision: generated C# classes inherit Dispose() from
        // IDisposable/SafeHandle. A Swift method named "dispose" PascalCase's to "Dispose",
        // colliding with the inherited method (CS0111). Suffix with "Swift" to disambiguate.
        if (_inheritedMethodCollisions.Contains(name))
            name = $"{name}Swift";

        // 4c. Type name collision: C# forbids member names identical to the enclosing type (CS0542).
        // This can happen when a Swift type has a method whose PascalCase name matches the type name
        // (e.g., `DatabaseRegion.databaseRegion(_:)` → `DatabaseRegion.DatabaseRegion(Database)`).
        if (parentTypeName != null && name == parentTypeName)
            name = $"Get{name}";

        // 5. Append "Async" suffix for async methods (per .NET convention)
        if (isAsync && !name.EndsWith("Async"))
            name = $"{name}Async";

        return name;
    }
}
