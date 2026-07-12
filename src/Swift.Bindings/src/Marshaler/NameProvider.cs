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
/// The complete set of collision-shaping inputs to <see cref="NameProvider.GetPublicMethodName(in PublicMethodNameContext)"/>.
/// Bundling them into one value means a call site cannot silently drop one (e.g. <see cref="ParentTypeName"/>) —
/// the P1-21 root cause where a fresh name recomputation omitted an axis the authoritative emitted name folds in.
/// Build it from a <see cref="MethodDecl"/> via <see cref="ForMethod"/> so the derivation lives in one place.
/// </summary>
/// <param name="MethodName">The original Swift method name.</param>
/// <param name="IsAsync">Whether the method is async.</param>
/// <param name="HasReturnValue">Whether the method has a non-void return value (drives the noun→"Get" prefix).</param>
/// <param name="PropertyNames">Sibling property names in PascalCase (drives the Foo→FooMethod/WithFoo rename).</param>
/// <param name="IsSelfReturning">Whether the method returns Self (builder/fluent pattern).</param>
/// <param name="ParentTypeName">The enclosing type name (drives the CS0542 parent-name collision rename).</param>
/// <param name="ParameterCount">The public-signature parameter count (drives the "Get" prefix gate).</param>
/// <param name="IsMutating">Whether the method is <c>mutating</c> — a mutating method advances/changes
/// state and is therefore not a getter, so it is excluded from the noun→"Get" prefix (e.g. an
/// <c>AsyncIteratorProtocol.next()</c> stays <c>NextAsync</c>, not <c>GetNextAsync</c>).</param>
public readonly record struct PublicMethodNameContext(
    string MethodName,
    bool IsAsync,
    bool HasReturnValue,
    IReadOnlySet<string>? PropertyNames,
    bool IsSelfReturning,
    string? ParentTypeName,
    int ParameterCount,
    bool IsMutating = false)
{
    /// <summary>
    /// Builds the context from a <see cref="MethodDecl"/> the same way the authoritative emitted name
    /// (<see cref="MethodEnvironment.CSharpMethodName"/>) derives its arguments, so every method-derived
    /// call site shapes the name identically instead of re-deriving the seven args inline (where one can
    /// be dropped). Callers that must diverge from a field do so explicitly via <c>with</c> (e.g. the
    /// protocol-interface key omits <see cref="ParentTypeName"/>).
    /// </summary>
    public static PublicMethodNameContext ForMethod(MethodDecl decl, IReadOnlySet<string>? siblingPropertyNames) => new(
        MethodName: decl.Name,
        IsAsync: decl.IsAsync,
        HasReturnValue: !decl.IsAccessor && decl.CSSignature.Count > 0 && !decl.CSSignature.First().SwiftTypeSpec.IsEmptyTuple,
        PropertyNames: siblingPropertyNames,
        IsSelfReturning: MethodEnvironment.IsSelfReturningMethod(decl),
        ParentTypeName: (decl.ParentDecl as TypeDecl)?.Name,
        ParameterCount: decl.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple),
        IsMutating: decl.IsMutating);
}

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
    // Currently empty — historical StoreKit-specific overrides were removed once
    // ApplyNestedTypeRenames' kind-aware suffix cascade covered the same collisions.
    // Left in place as an extension point for any future unavoidable one-off renames.
    private static readonly Dictionary<string, string> PropertyNameMappings = new();

    /// <summary>
    /// Converts a camelCase or SCREAMING_CASE string to PascalCase.
    /// </summary>
    /// <param name="camelCase">The string to convert.</param>
    /// <returns>The PascalCase string.</returns>
    public static string ToPascalCase(string camelCase)
    {
        if (string.IsNullOrEmpty(camelCase))
            return camelCase;

        // Strip any remaining backticks (belt+suspenders — parser also strips them)
        camelCase = camelCase.Replace("`", "");
        if (string.IsNullOrEmpty(camelCase))
            return "_";

        // Sanitize characters that are invalid in C# identifiers (emoji, symbols, etc.)
        camelCase = SanitizeIdentifierChars(camelCase);
        if (string.IsNullOrEmpty(camelCase))
            return "_";

        // SCREAMING_CASE → PascalCase (e.g., "CAMERA_DIRECTION" → "CameraDirection")
        if (IsScreamingCase(camelCase))
            return ScreamingCaseToPascalCase(camelCase);

        // Already PascalCase
        if (char.IsUpper(camelCase[0]))
            return camelCase;

        return char.ToUpperInvariant(camelCase[0]) + camelCase.Substring(1);
    }

    /// <summary>
    /// Replaces characters that are invalid in C# identifiers with underscores.
    /// Valid C# identifier characters: letters, digits, and underscores.
    /// Unicode letters (e.g., CJK characters) are allowed by C#, but emoji and
    /// other non-letter/non-digit Unicode characters are not.
    /// </summary>
    public static string SanitizeIdentifierChars(string name)
    {
        bool needsSanitization = false;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                needsSanitization = true;
                break;
            }
        }

        if (!needsSanitization)
            return name;

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }

        var result = sb.ToString();

        // Ensure starts with letter or underscore
        if (result.Length > 0 && char.IsDigit(result[0]))
            result = "_" + result;

        return result;
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
    /// <para>
    /// Pure-letter word segments are title-cased and their underscore joins collapse
    /// (<c>CAMERA_DIRECTION</c> → <c>CameraDirection</c>). A segment carrying a digit is treated as an
    /// acronym/designator and preserved verbatim rather than lower-cased into an unreadable form
    /// (<c>SHA3</c> stays <c>SHA3</c>, not <c>Sha3</c>; <c>MP3</c> stays <c>MP3</c>, not <c>Mp3</c>).
    /// The underscore boundary between two adjacent digit-bearing designators is preserved so the
    /// emitted identifier matches the original Swift name (<c>SHA3_256</c> → <c>SHA3_256</c>,
    /// <c>X9_63</c> → <c>X9_63</c>); an underscore adjacent to a pure-letter word still collapses
    /// (<c>MAX_SIZE_2</c> → <c>MaxSize2</c>).
    /// </para>
    /// </summary>
    private static string ScreamingCaseToPascalCase(string s)
    {
        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            bool partHasDigit = HasDigit(part);
            // Keep the separating underscore only between two digit-bearing designator segments
            // so digit-boundary names (SHA3_256) survive; drop it for ordinary word joins.
            if (i > 0 && partHasDigit && HasDigit(parts[i - 1]))
                sb.Append('_');
            if (partHasDigit)
            {
                // Digit-bearing acronym/designator — preserve verbatim (SHA3, 256, MP3).
                sb.Append(part);
            }
            else
            {
                sb.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                    sb.Append(part.Substring(1).ToLowerInvariant());
            }
        }
        return sb.ToString();
    }

    /// <summary>Returns true if the string contains at least one decimal digit.</summary>
    private static bool HasDigit(string s)
    {
        foreach (var c in s)
            if (char.IsDigit(c)) return true;
        return false;
    }

    /// <summary>
    /// Converts a type name segment to PascalCase for C# output.
    /// Only converts true SCREAMING_CASE names (with underscores and multi-letter segments,
    /// e.g. THING_KEY → ThingKey) and camelCase names (starts lowercase, e.g. pixelFormat → PixelFormat).
    /// Leaves abbreviations and short patterns unchanged (e.g. URL, F9S1, F0_S1).
    /// </summary>
    public static string ToPascalCaseForTypeName(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return segment;
        // Has underscores → convert only if it's true SCREAMING_CASE (not abbreviation patterns like F0_S1)
        if (segment.Contains('_') && HasMultiLetterUpperSegment(segment))
            return ToPascalCase(segment);
        // Starts lowercase → camelCase, capitalize first letter
        if (char.IsLower(segment[0]))
            return ToPascalCase(segment);
        // Already PascalCase, abbreviation (URL, F9S1), or short pattern (F0_S1) → leave unchanged
        return segment;
    }

    /// <summary>
    /// Returns the emitted C# leaf name of a nested type for member-collision detection — the
    /// renamed name when the nested-type-rename pre-pass disambiguated it (e.g. a sibling `entry`
    /// property forced <c>Entry</c> → <c>EntryInfo</c>, or <c>Format</c> → <c>FormatKind</c>),
    /// otherwise the natural PascalCase leaf. Method/subscript collision sets must reserve THIS
    /// name, not the raw Swift-name PascalCase: after a rename the C# type is <c>EntryInfo</c>, so a
    /// sibling method projecting to <c>EntryInfo</c> (from <c>entryInfo()</c>) — not one projecting
    /// to <c>Entry</c> — is the CS0102 duplicate that must force the method to <c>EntryInfoMethod</c>.
    /// Behavior-preserving for non-renamed types: the override fires only when the registered C#
    /// leaf actually differs from the natural type-name PascalCase, so a non-renamed type's
    /// collision name stays exactly the <see cref="ToPascalCase(string)"/> it has always been.
    /// </summary>
    internal static string GetEmittedNestedTypeLeafName(TypeDecl nestedType, ITypeDatabase? typeDatabase)
    {
        if (typeDatabase != null
            && typeDatabase.TryGetTypeRecord(nestedType.SwiftTypeName, out var record))
        {
            var csName = record.CSharpTypeName.Name;
            var lastDot = csName.LastIndexOf('.');
            var leaf = lastDot >= 0 ? csName.Substring(lastDot + 1) : csName;
            if (leaf != ToPascalCaseForTypeName(nestedType.Name))
                return leaf;
        }
        return ToPascalCase(nestedType.Name);
    }

    /// <summary>
    /// Returns true if any underscore-separated segment has 2+ consecutive uppercase letters.
    /// This distinguishes true SCREAMING_CASE (THING_KEY → segments with multi-letter words)
    /// from abbreviation patterns (F0_S1 → segments with single letters + digits).
    /// </summary>
    private static bool HasMultiLetterUpperSegment(string s)
    {
        foreach (var part in s.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            int consecutiveUpperLetters = 0;
            foreach (var c in part)
            {
                if (char.IsUpper(c))
                {
                    consecutiveUpperLetters++;
                    if (consecutiveUpperLetters >= 2) return true;
                }
                else
                    consecutiveUpperLetters = 0;
            }
        }
        return false;
    }

    /// <summary>
    /// Provides the name of the PInvoke method.
    /// Uses a hash of the mangled name to ensure uniqueness for method overloads
    /// that have different Swift parameter types but marshal to the same C# types.
    /// </summary>
    /// <param name="methodDecl">The method declaration.</param>
    /// <returns>The name of the PInvoke method.</returns>
    public static string GetPInvokeName(MethodDecl methodDecl)
        => GetPInvokeName(methodDecl.MangledName, methodDecl);

    /// <summary>
    /// AF13 (Finding 13): hashes an explicit <paramref name="baseSymbol"/> (the promoted emission
    /// symbol) instead of the decl's <see cref="MethodDecl.MangledName"/>, so the C# extern method
    /// name stays keyed on the wrapper symbol that disambiguates overloads while the decl keeps its
    /// immutable silgen symbol. Callers with a <c>MethodEnvironment</c> pass <c>env.EmissionSymbol</c>.
    /// </summary>
    public static string GetPInvokeName(string baseSymbol, MethodDecl methodDecl)
    {
        // Use last 8 chars of mangled name hash to disambiguate overloads
        // whose parameters marshal to the same C# types (e.g., URL and ImageRequest both become SafeHandle)
        var mangledHash = EmitterUtility.DeterministicHash8(baseSymbol);
        // Sanitize the method name: strip backticks (Swift keyword escaping) and
        // invalid C# identifier chars (emoji like 🚫 used in ObjC compatibility shims)
        var sanitizedName = SanitizeIdentifierChars(methodDecl.Name.Replace("`", ""));
        if (string.IsNullOrEmpty(sanitizedName))
            sanitizedName = "unnamed";
        return $"PInvoke_{sanitizedName}_{mangledHash}";
    }


    /// <summary>
    /// Provides the mangled name of the PInvoke method.
    /// </summary>
    /// <param name="methodDecl">The method declaration.</param>
    /// <returns>The mangled name of the PInvoke method.</returns>
    public static string GetMangledName(MethodDecl methodDecl)
        => GetMangledName(methodDecl.MangledName, methodDecl);

    /// <summary>
    /// AF13 (Finding 13): the suffix-ladder reconstruction over an explicit <paramref name="baseSymbol"/>
    /// (the promoted emission symbol) rather than the decl's <see cref="MethodDecl.MangledName"/>, so
    /// callers can source the base from <c>MethodEnvironment.EmissionSymbol</c> while the decl keeps its
    /// immutable silgen symbol. The wrapper-kind flags that select which suffix applies stay on the decl.
    /// <see cref="GetMangledName(MethodDecl)"/> is the (silgen-base) special case.
    /// </summary>
    public static string GetMangledName(string baseSymbol, MethodDecl methodDecl)
    {
        if (methodDecl.IsAsync)
            return $"{baseSymbol}_async";

        // Opaque return types (some Protocol) need a @_silgen_name wrapper with a unique symbol
        // to avoid self-recursion when the wrapper calls the original function.
        // When a @_cdecl wrapper handles the return (boxing `some Protocol` → `any Protocol`),
        // the base symbol already points to the @_cdecl symbol — no suffix needed.
        if (methodDecl.CSSignature.Count > 0 &&
            methodDecl.CSSignature.First().SwiftTypeSpec is ProtocolListTypeSpec { IsOpaque: true } &&
            !methodDecl.UsesCdeclMethodWrapper && !methodDecl.UsesCdeclPropertyWrapper)
            return $"{baseSymbol}_opaque";

        // Optional pointer wrappers need a unique symbol because the wrapper function takes
        // UnsafeRawPointer where the original takes Optional<String> by value.
        if (methodDecl.HasOptionalPointerWrapper)
            return $"{baseSymbol}_optbuf";

        // Closure Cdecl wrappers need a unique symbol because the wrapper function has
        // different parameter types (UnsafeMutableRawPointer pairs) than the original
        // Swift function (native closure types). @_silgen_name with the original symbol
        // would cause a function type mismatch error.
        if (methodDecl.HasClosureCdeclWrapper)
            return $"{baseSymbol}_cdecl";

        // Generic closure bridge wrappers specialize T=UnsafeMutableRawPointer and use
        // cdecl callback pairs, requiring a unique symbol distinct from the original.
        if (methodDecl.HasGenericClosureBridge)
            return $"{baseSymbol}_XC";

        return baseSymbol;
    }

    /// <summary>
    /// Provides the name of the interface based on a protocol name.
    /// </summary>
    /// <param name="protocolName">The protocol name.</param>
    /// <param name="typeName">The C# type name including generic parameters (used by Equatable).</param>
    /// <param name="moduleName">The protocol's source module.</param>
    /// <param name="currentModuleName">
    /// The consumer module emitting the reference. When supplied and different from
    /// <paramref name="moduleName"/>, the result is qualified with the protocol's namespace
    /// so cross-module protocol references (e.g. RealityKit referring to RealityFoundation.IEvent)
    /// resolve through the C# namespace path that matches how cross-module classes are emitted.
    /// </param>
    /// <returns>The name of the interface.</returns>
    public static string GetInterfaceName(string protocolName, string typeName = "", string moduleName = "", string currentModuleName = "")
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

        // Apply the I prefix to the LEAF component only. For a nested protocol like
        // `Parent.Nested` (the parent path lands here as `Parent.Nested`), the C# nested
        // layout is `Parent.INested` —
        // the parent type names stay un-prefixed and the I attaches to the protocol's
        // leaf name. Without this, `I + Parent.Nested` produces the nonexistent type `IParent.Nested`.
        var lastDot = protocolName.LastIndexOf('.');
        var interfaceName = lastDot >= 0
            ? protocolName.Substring(0, lastDot + 1) + "I" + protocolName.Substring(lastDot + 1)
            : $"I{protocolName}";

        // Cross-module qualification: when a binding references a protocol declared in another
        // module (umbrella re-export, dep DB), the bare `IFoo` is unresolvable in C#. Mirror
        // the cross-module namespace path that classes/structs already follow.
        // Swift stdlib is brought in via `using Swift;` so it never needs explicit qualification.
        if (!string.IsNullOrEmpty(moduleName) &&
            !string.IsNullOrEmpty(currentModuleName) &&
            moduleName != currentModuleName &&
            moduleName != "Swift")
        {
            return $"{moduleName}.{interfaceName}";
        }

        return interfaceName;
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
    /// Uses @ verbatim identifiers for C# keywords (e.g., @event, @string).
    /// This is safe in string interpolation: $"{name}Handle" produces "@eventHandle"
    /// which is a valid C# identifier (redundant @ is harmless).
    /// </summary>
    /// <param name="arg">The argument declaration.</param>
    /// <returns>A valid C# parameter name.</returns>
    public static string GetCSharpParameterName(ArgumentDecl arg)
    {
        // If a deduplicated name has been precomputed, use it
        if (arg.CSharpName != null)
            return arg.CSharpName;

        // 1. If PrivateName is populated, prefer it (internal Swift name from swiftinterface).
        //    Treat a literal "_" PrivateName as not-set: Swift's `_` is the
        //    "no internal name" marker, not a usable identifier. Falling
        //    through lets the external label or role-derived name win and
        //    avoids emitting the discard-pattern symbol as a C# parameter.
        if (!string.IsNullOrEmpty(arg.PrivateName) && arg.PrivateName != "_")
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

        // 3. Handle keyword-escaped names from the parser (_object, _event, _string)
        if (arg.Name.Length > 1 && arg.Name[0] == '_' && IsCSharpKeyword(arg.Name.Substring(1)))
        {
            // "value" is a contextual keyword — safe as parameter name
            if (arg.Name == "_value")
                return "value";

            // Use @ verbatim identifier for C# keywords.
            // @event, @string, @object are valid C# parameter names and
            // work in all reference positions. For derived variable names
            // (e.g., __{name}Swift), use StripVerbatimPrefix to get the bare name.
            return $"@{arg.Name.Substring(1)}";
        }

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
    internal static string? DeriveParameterNameFromType(TypeSpec typeSpec)
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

        // Generic placeholders (τ_0_0, τ_0_1, etc.) → "value" to avoid leaking ABI names
        if (typeName.StartsWith("τ_") || typeName.StartsWith("\u03C4_"))
            return "value";

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
    /// Includes Apple numeric typealiases (CGFloat, TimeInterval, NSInteger, …) that
    /// are semantically primitive doubles/ints. Without this, Swift `_: CGFloat`
    /// parameters would camelcase to nonsense names like `cGFloat`.
    /// </summary>
    private static readonly HashSet<string> _primitiveTypeNames = new()
    {
        "Int", "Int8", "Int16", "Int32", "Int64",
        "UInt", "UInt8", "UInt16", "UInt32", "UInt64",
        "Float", "Float16", "Float32", "Float64", "Double",
        "String", "Character",
        // Apple numeric primitive aliases (CoreGraphics / Foundation / ObjC bridging)
        "CGFloat", "TimeInterval", "NSTimeInterval",
        "NSInteger", "NSUInteger",
        "CFTimeInterval", "CFAbsoluteTime", "CFIndex",
    };

    /// <summary>
    /// Sanitizes a Swift internal parameter name for use as a C# identifier.
    /// Uses @ verbatim prefix for C# keywords. This is safe in string interpolation:
    /// $"{name}Handle" produces "@eventHandle" which is a valid C# identifier
    /// (redundant @ is harmless).
    /// </summary>
    private static string SanitizeForCSharp(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // CX-2: Strip characters illegal in C# identifiers (e.g., '<', '>' from existential annotations).
        // Some libraries produce params like "retryStrategy>" from "any RetryStrategy" type annotations.
        name = StripIllegalIdentifierChars(name);

        if (string.IsNullOrEmpty(name))
            return "_param";

        // "value" is a contextual keyword — valid as a parameter name in all positions
        // we generate (method params, not property setters).
        if (name == "value")
            return name;

        // C# keywords — use @ verbatim prefix
        if (IsCSharpKeyword(name))
            return $"@{name}";

        // Handle names starting with digits (rare but possible)
        if (char.IsDigit(name[0]))
            return $"_{name}";

        return name;
    }

    /// <summary>
    /// Strips characters that are illegal in C# identifiers.
    /// Keeps only letters, digits, and underscores.
    /// </summary>
    private static string StripIllegalIdentifierChars(string name)
    {
        if (name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '@'))
            return name;

        var cleaned = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '@').ToArray());
        return cleaned;
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
        "finally", "throw", "new", "this", "base", "null", "true", "false",
        "class", "struct", "enum", "interface", "public", "private", "protected",
        "internal", "static", "readonly", "const", "override", "virtual",
        "abstract", "sealed", "async", "await", "var", "object", "string",
        "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
        "float", "double", "decimal", "bool", "char", "void", "ref", "out",
        "params", "event", "delegate", "operator", "implicit", "explicit",
        "where", "get", "set", "value", "partial", "using", "namespace",
        "typeof", "sizeof", "checked", "unchecked", "foreach", "goto",
        "lock", "fixed", "stackalloc", "volatile", "extern", "unsafe"
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
    /// Applies the C# verbatim identifier prefix (@) if the name is a C# keyword.
    /// GetCSharpParameterName already returns @-prefixed names, so this is mainly
    /// useful for names from other sources (e.g., type-derived names).
    /// </summary>
    public static string EscapeForCSharpSignature(string name)
        => IsCSharpKeyword(name) && name != "value" ? $"@{name}" : name;

    /// <summary>
    /// Strips the C# verbatim identifier prefix (@) from a name.
    /// Use when building compound variable names (e.g., __{name}Swift) where
    /// the @ prefix would appear mid-identifier and be invalid.
    /// Also use when passing C# parameter names into Swift code generation.
    /// </summary>
    public static string StripVerbatimPrefix(string name)
        => name.StartsWith("@") ? name.Substring(1) : name;

    /// <summary>
    /// Escapes a name with backticks if it is a Swift keyword.
    /// Use for names NOT from the ABI parser (e.g., swiftinterface method names,
    /// parameter names, enum case names). For parser-escaped names, use
    /// <see cref="ParserNameToSwift"/> instead.
    /// </summary>
    public static string EscapeSwiftKeyword(string name)
        => IsSwiftKeyword(name) ? $"`{name}`" : name;

    /// <summary>
    /// Returns the external Swift argument label to emit for a subscript index parameter,
    /// keyword-escaped if necessary. Returns <c>_</c> when the source had no external label
    /// (i.e. <c>subscript(name: T)</c> / <c>subscript(_ name: T)</c>), driven by
    /// <see cref="ArgumentDecl.IsUnlabeledSubscriptIndex"/> — never by a string pattern, since
    /// real user labels can spell <c>index0</c>, <c>default</c>, etc. and would collide with
    /// either the synthetic sentinel or a Swift keyword.
    /// Recovers the raw Swift label via <see cref="ParserNameToSwift"/>, so keywords mangled to
    /// C#-safe form (<c>default</c> → <c>_default</c>) are restored before backtick escaping.
    /// </summary>
    public static string GetSubscriptExternalLabel(ArgumentDecl param)
    {
        if (param.IsUnlabeledSubscriptIndex || string.IsNullOrEmpty(param.Name))
            return "_";
        return ParserNameToSwift(param);
    }

    /// <summary>
    /// Gets the correct Swift identifier for a declaration, with backtick escaping
    /// if it is a Swift keyword. Uses <see cref="BaseDecl.OriginalSwiftName"/> when
    /// available (set by the parser when the name was modified for C# safety),
    /// eliminating ambiguity between parser-escaped names and genuine leading-underscore
    /// identifiers.
    /// </summary>
    public static string ParserNameToSwift(BaseDecl decl)
    {
        var swiftName = decl.GetSwiftName();
        return EscapeSwiftKeyword(swiftName);
    }

    /// <summary>
    /// Overload for cases where only the string name is available (e.g., derived names
    /// from accessor stripping). Falls back to <see cref="StripCSharpKeywordPrefix"/>
    /// heuristic which cannot distinguish parser-escaped names from genuine underscore-prefixed
    /// identifiers. Prefer <see cref="ParserNameToSwift(BaseDecl)"/> (provenance-aware) or
    /// <see cref="EscapeSwiftKeyword"/> (for raw Swift names) instead.
    /// </summary>
    [Obsolete("Ambiguous without provenance. Use ParserNameToSwift(BaseDecl) or EscapeSwiftKeyword(string) instead.")]
    public static string ParserNameToSwift(string name)
    {
        var stripped = StripCSharpKeywordPrefix(name);
        return IsSwiftKeyword(stripped) ? $"`{stripped}`" : stripped;
    }

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
        "Disposable",     // IDisposable → ISwiftDisposable (avoids collision with System.IDisposable)
    };

    /// <summary>
    /// Method names that collide with well-known .NET base class methods inherited by
    /// generated C# classes (e.g., IDisposable.Dispose() from SafeHandle, plus the
    /// System.Object virtuals every class inherits). Swift methods with these
    /// PascalCase names get a "Swift" suffix to avoid CS0111 / CS0114, and to keep
    /// the inherited System.Object semantics intact for callers that rely on
    /// patterns like `instance.GetType().Name` inside generated code.
    /// </summary>
    private static readonly HashSet<string> _inheritedMethodCollisions = new()
    {
        "Dispose",        // IDisposable.Dispose() from SafeHandle
        "Finalize",       // Object.Finalize() (C# destructor) — Swift type has a finalize() member
        "GetType",        // Object.GetType() — Firestore Expression.type() shadows it and breaks GetType().Name
        "ToString",       // Object.ToString()
        "Equals",         // Object.Equals(object)
        "GetHashCode",    // Object.GetHashCode()
    };

    /// <summary>
    /// C#-surfaced NSObject instance <b>property</b> names inherited by every binding rooted in
    /// Microsoft.iOS's NSObject. A Swift method projected to one of these names (e.g. a Swift
    /// <c>handle(_:)</c> → C# <c>Handle(...)</c>) shadows the inherited property (CS0108) and makes
    /// later reads of that property resolve the method group instead (CS0428 — the reported FBAEMKit
    /// crash). These are fed into the existing sibling-property rename axis — but only for ObjC-rooted
    /// classes, since a non-rooted Swift class does not inherit them.
    ///
    /// <para>This is the full set of public instance properties on <c>Foundation.NSObject</c> in the
    /// Microsoft.iOS ref assembly, plus the <c>protected internal</c> <c>IsDirectBinding</c> (a public
    /// Swift method projecting to it would still hide it). The reflection-driven drift test
    /// (<c>ObjCRootedInheritedPropertyDriftTests</c>) reads the installed ref assembly and fails if the
    /// SDK adds a property not covered here, so this hand-maintained set stays in sync without any
    /// installed-workload dependency leaking into generation. <c>Hash</c> is intentionally absent:
    /// .NET surfaces it as the method <c>GetNativeHash</c>, not a property, so seeding it would
    /// spuriously rename a legitimate Swift <c>hash()</c>.</para>
    /// </summary>
    private static readonly HashSet<string> _objCRootedInheritedPropertyNames = new()
    {
        // Public instance properties of Foundation.NSObject (Microsoft.iOS).
        "Handle",                                  // NativeHandle — the reported FBAEMKit collision
        "SuperHandle",                             // NativeHandle
        "ClassHandle",                             // NativeHandle
        "Class",                                   // Class
        "Description",                             // string
        "DebugDescription",                        // string
        "Zone",                                    // NSZone
        "Self",                                    // NSObject
        "Superclass",                              // Class
        "RetainCount",                             // nuint
        "IsProxy",                                 // bool
        "AccessibilityAttributedUserInputLabels",  // NSAttributedString[]
        "AccessibilityRespondsToUserInteraction",  // bool
        "AccessibilityTextualContext",             // string
        "AccessibilityUserInputLabels",            // string[]
        // protected internal (not public, but a public projected method still hides it).
        "IsDirectBinding",
    };

    /// <summary>
    /// Curated entries that are <b>not</b> public instance properties of NSObject (so the drift test's
    /// "no stale entries" check must allow them). Currently just the <c>protected internal</c>
    /// <c>IsDirectBinding</c>.
    /// </summary>
    public static IReadOnlyCollection<string> ObjCRootedInheritedNonPublicPropertyNames { get; } =
        new[] { "IsDirectBinding" };

    /// <summary>
    /// The curated NSObject instance-property names whose C# accessors collide with a same-named
    /// projected method on an ObjC-rooted binding. Exposed for the seed site in ClassHandler and for
    /// the reflection drift test; <see cref="_objCRootedInheritedPropertyNames"/> stays private.
    /// </summary>
    public static IReadOnlyCollection<string> ObjCRootedInheritedPropertyNames => _objCRootedInheritedPropertyNames;

    public static string GetMetadataName(string typeName) => $"{typeName}Metadata";
    public static string GetPayloadName(string argumentName) => $"{argumentName}Payload";
    public static string GetProtocolWitnessTableName(string typeName, string protocolName) => $"{typeName}{protocolName}PWT";

    /// <summary>
    /// Finding 48: maps the synthesized-accessor bit to a C# access modifier keyword. A
    /// synthesized accessor (a stored-property/subscript getter or setter) emits as a
    /// <c>private</c> helper behind the public property/indexer; every other method emits as
    /// <c>public</c>. This is the only distinction the old <c>Visibility</c> enum ever drew —
    /// its <c>Internal</c> arm was never assigned (module-internal-ness lives on
    /// <c>IsModuleInternal</c>), so it is gone.
    /// </summary>
    public static string GetAccessModifier(bool isSynthesizedAccessor) =>
        isSynthesizedAccessor ? "private" : "public";

    /// <summary>
    /// Gets the C# property name for a given Swift property name, converting to PascalCase
    /// and handling reserved keywords and CS0542 (property vs. containing-type) collisions.
    /// Sibling nested-type collisions (CS0102) are resolved via the
    /// <see cref="ApplyNestedTypeRenames"/> pre-pass (which renames the nested type with a
    /// kind-aware semantic suffix — enum→"Kind", struct/class→"Info") plus
    /// <see cref="ComputePropertyRenamesForNestedTypeCollisions"/> +
    /// <see cref="GetFinalMemberName"/> for cases where the nested type isn't renamed.
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
        // Example: class Foo { var Foo: Foo? } -> property Foo collides with class name (CS0542)
        if (!string.IsNullOrEmpty(containingTypeName) && pascalName == containingTypeName)
            return $"{pascalName}Value";

        return pascalName;
    }

    /// <summary>
    /// Applies a property rename if one exists, otherwise returns the original name.
    /// Use this at every site that emits or tracks a member name that could be renamed.
    /// </summary>
    public static string GetFinalMemberName(string name, IReadOnlyDictionary<string, string>? renames)
        => renames?.TryGetValue(name, out var renamed) == true ? renamed : name;

    /// <summary>
    /// Computes a case-insensitive-collision-safe mapping from Swift enum case names to unique
    /// C# PascalCase identifiers. Swift is case-sensitive (M vs m are distinct), but C# is not —
    /// both become "M" via ToPascalCase. This method detects such collisions and appends numeric
    /// suffixes to later occurrences (e.g., M → "M", m → "M2").
    /// Returns null if no collisions exist (caller can use ToPascalCase directly).
    /// </summary>
    public static Dictionary<string, string>? ComputeCaseNameMap(IReadOnlyList<EnumCaseDecl> cases)
    {
        // First pass: detect if any collisions exist (fast path — most enums have none)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasCollision = false;
        foreach (var caseDecl in cases)
        {
            var pascalName = ToPascalCase(caseDecl.Name);
            if (!seen.Add(pascalName))
            {
                hasCollision = true;
                break;
            }
        }

        if (!hasCollision)
            return null;

        // Second pass: build collision-free mapping
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, string>();
        foreach (var caseDecl in cases)
        {
            var pascalName = ToPascalCase(caseDecl.Name);
            if (usedNames.Add(pascalName))
            {
                map[caseDecl.Name] = pascalName;
            }
            else
            {
                // Collision: append numeric suffix
                int suffix = 2;
                string candidate;
                do
                {
                    candidate = $"{pascalName}{suffix}";
                    suffix++;
                } while (!usedNames.Add(candidate));
                map[caseDecl.Name] = candidate;
            }
        }
        return map;
    }

    /// <summary>
    /// Gets the PascalCase name for an enum case, using the collision map if available.
    /// </summary>
    public static string GetCaseName(string swiftCaseName, Dictionary<string, string>? caseNameMap)
        => caseNameMap?.TryGetValue(swiftCaseName, out var mapped) == true ? mapped : ToPascalCase(swiftCaseName);

    /// <summary>
    /// Computes property/member renames needed to avoid property/nested-type name collisions.
    /// When a member name collides with a nested type name, the member is renamed with a "Value" suffix.
    /// Exception: when a property's return type IS the colliding nested type, the nested type is renamed
    /// instead (with a kind-aware semantic suffix — enum→"Kind", struct/class→"Info"), keeping the
    /// property name clean for better consumer ergonomics.
    /// </summary>
    /// <param name="memberNames">PascalCase member names (properties, enum cases).</param>
    /// <param name="nestedTypeNames">Nested type names.</param>
    /// <param name="typeRenameNames">Member names where the nested type should be renamed instead of the property.</param>
    /// <returns>A dictionary mapping original member name → renamed name.</returns>
    public static Dictionary<string, string> ComputePropertyRenamesForNestedTypeCollisions(
        IEnumerable<string> memberNames, IEnumerable<string> nestedTypeNames,
        ISet<string>? typeRenameNames = null)
    {
        var typeNameSet = new HashSet<string>(nestedTypeNames);
        var memberNameSet = new HashSet<string>(memberNames);
        var renames = new Dictionary<string, string>();
        foreach (var memberName in memberNames)
        {
            if (typeNameSet.Contains(memberName))
            {
                // When the property's return type IS the colliding nested type, the nested type
                // is renamed instead (handled by the caller via TypeDatabase update), so skip
                // the property rename here.
                if (typeRenameNames?.Contains(memberName) == true)
                    continue;

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
    /// Returns a dictionary mapping original member name → renamed name.
    /// When a property's return type IS the colliding nested type, the nested type is renamed instead
    /// (with a kind-aware semantic suffix — enum→"Kind", struct/class→"Info") via TypeDatabase
    /// CSharpTypeName update, keeping the property name clean.
    /// </summary>
    /// <param name="typeDecl">The type declaration containing properties and nested types.</param>
    /// <param name="typeDatabase">The type database for type resolution and nested type renames.</param>
    /// <returns>A dictionary mapping original member name → renamed name.</returns>
    public static Dictionary<string, string> ComputePropertyRenames(TypeDecl typeDecl, ITypeDatabase typeDatabase)
    {
        // Only include properties whose types can be resolved — properties with unsupported types
        // (AnyType, SwiftUI references, etc.) are skipped by the emitter and should not trigger
        // property renames. Without this filter, a skipped property named "priority" would cause
        // a nested type "Priority" to unnecessarily rename the property to "PriorityValue".
        // We use a lightweight type-resolution check rather than the full CanEmitProperty() which
        // also rejects properties for structural reasons (no accessors, etc.) that don't affect naming.
        var emittableProperties = typeDecl.Properties
            .Where(p => !MemberEmissionValidator.HasUnsupportedPropertyType(p, typeDatabase))
            .ToList();
        var memberNames = emittableProperties
            .Select(p => GetPropertyName(p.Name, typeDecl.Name));

        // Also include AsyncStream properties that will be emitted as IAsyncEnumerable<T>.
        // HasUnsupportedPropertyType excludes these (AsyncStream is from _Concurrency module),
        // but the emitter does emit them as properties, so they must participate in collision detection.
        var asyncStreamHandler = new AsyncStreamHandler(typeDatabase);
        var asyncStreamPropertyNames = typeDecl.Properties
            .Where(p => asyncStreamHandler.IsAsyncStream(p.SwiftTypeSpec) && asyncStreamHandler.IsSupportedAsyncStream(p.SwiftTypeSpec))
            .Select(p => GetPropertyName(p.Name, typeDecl.Name));
        memberNames = memberNames.Concat(asyncStreamPropertyNames);

        // For enums, include collision-safe case names in the collision set.
        // Enum cases produce factory methods (e.g., case "pong" → static method "Pong()"),
        // which collide with nested types of the same PascalCase name (CS0102).
        if (typeDecl is EnumDecl enumDecl)
        {
            var caseNameMap = ComputeCaseNameMap(enumDecl.Cases);
            var caseNames = enumDecl.Cases.Select(c => GetCaseName(c.Name, caseNameMap));
            memberNames = memberNames.Concat(caseNames);
        }

        var nestedTypeNames = typeDecl.Types.Select(t => t.Name);

        // Check which nested types were already renamed by the PrecomputeNestedTypeRenames pre-pass.
        // These don't need property renames — the type rename resolves the collision.
        var typeRenameNames = new HashSet<string>();
        foreach (var nestedType in typeDecl.Types)
        {
            if (typeDatabase.TryGetTypeRecord(nestedType.SwiftTypeName, out var nestedRecord))
            {
                var csLeaf = nestedRecord.CSharpTypeName.Name;
                var lastDot = csLeaf.LastIndexOf('.');
                var leafName = lastDot >= 0 ? csLeaf.Substring(lastDot + 1) : csLeaf;
                if (leafName != ToPascalCaseForTypeName(nestedType.Name))
                    typeRenameNames.Add(nestedType.Name);
            }
        }

        return ComputePropertyRenamesForNestedTypeCollisions(memberNames, nestedTypeNames,
            typeRenameNames.Count > 0 ? typeRenameNames : null);
    }

    /// <summary>
    /// Pre-pass that walks all types in a module and applies CSharpTypeName renames for
    /// nested type/property name collisions. Must be called BEFORE any emission so that
    /// protocol interfaces and other early-emitted code use the correct renamed types.
    /// </summary>
    public static void PrecomputeNestedTypeRenames(ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
    {
        foreach (var typeDecl in moduleDecl.Types)
            PrecomputeNestedTypeRenamesRecursive(typeDecl, typeDatabase);
        foreach (var protocolDecl in moduleDecl.Protocols)
            PrecomputeNestedTypeRenamesRecursive(protocolDecl, typeDatabase);
    }

    private static void PrecomputeNestedTypeRenamesRecursive(TypeDecl typeDecl, ITypeDatabase typeDatabase)
    {
        // Apply renames for this type's nested type collisions
        ApplyNestedTypeRenames(typeDecl, typeDatabase);

        // Recurse into nested types
        foreach (var nestedType in typeDecl.Types)
            PrecomputeNestedTypeRenamesRecursive(nestedType, typeDatabase);
    }

    /// <summary>
    /// Detects and applies CSharpTypeName renames for property/nested-type name collisions
    /// where the property's return type IS the colliding nested type. In these cases, the
    /// nested TYPE is renamed (with a kind-aware semantic suffix — enum→"Kind", struct/class→"Info")
    /// instead of the property.
    /// </summary>
    private static void ApplyNestedTypeRenames(TypeDecl typeDecl, ITypeDatabase typeDatabase)
    {
        if (typeDecl.Types.Count == 0 || typeDecl.Properties.Count == 0)
            return;

        var emittableProperties = typeDecl.Properties
            .Where(p => !MemberEmissionValidator.HasUnsupportedPropertyType(p, typeDatabase))
            .ToList();

        var nestedTypeNameSet = new HashSet<string>(typeDecl.Types.Select(t => t.Name));
        var parentFullName = typeDecl.SwiftTypeName.ToString();

        // Build the set of names that are already taken in this container, so we can avoid
        // creating a new collision when we append the kind-aware suffix. Includes:
        //   - Emitted C# nested type leaf names — PascalCased to match emission. A Swift type
        //     identifier may be lowercase (`struct entryInfo {}`), which emits as `EntryInfo`;
        //     seeding the raw Swift name would let a rename target (`Entry` → `EntryInfo`) land
        //     on that sibling's emitted leaf without the numeric-fallback guard firing → CS0102.
        //   - Pascal-cased property/member names
        // Updated as we apply renames so a later rename can detect collisions with an
        // earlier rename's chosen name.
        var takenNames = new HashSet<string>(typeDecl.Types.Select(t => ToPascalCaseForTypeName(t.Name)));
        foreach (var prop in emittableProperties)
            takenNames.Add(GetPropertyName(prop.Name, typeDecl.Name));

        foreach (var p in emittableProperties)
        {
            var csPropertyName = GetPropertyName(p.Name, typeDecl.Name);
            if (!nestedTypeNameSet.Contains(csPropertyName))
                continue;

            var typeSpecToCheck = UnwrapOptionalTypeSpec(p.SwiftTypeSpec);
            if (typeSpecToCheck == null)
                continue;

            // Compare the full qualified name: property type must be "Parent.NestedType" exactly
            var expectedFullName = $"{parentFullName}.{csPropertyName}";
            var propTypeFullName = typeSpecToCheck.ToString();
            if (propTypeFullName == expectedFullName)
            {
                // Rename the nested type instead of the property.
                var nestedType = typeDecl.Types.FirstOrDefault(t => t.Name == csPropertyName);
                if (nestedType != null && typeDatabase.TryGetTypeRecord(nestedType.SwiftTypeName, out var nestedRecord))
                {
                    // Pick a new leaf name by appending the kind-aware suffix chosen below until it
                    // does not collide with any existing member name, original nested type name, or
                    // earlier rename in this loop. Without the takenNames guard, two sibling
                    // properties that each force a rename onto the same base leaf (e.g. a struct
                    // `Transaction.Offer` → OfferInfo and a later sibling that would also land on
                    // OfferInfo) would emit duplicate C# type names — yielding CS0102 / CS0542.
                    // Also reject collision with the renamed type's OWN child names — a child
                    // sharing the new leaf name trips CS0542 ("member names cannot be the
                    // same as their enclosing type"), e.g. Swift `Card.Wallet` renamed to
                    // `WalletInfo` while it already contains a nested type `WalletInfo` (or a
                    // lowercase `walletInfo`, which emits as `WalletInfo`). PascalCase the child
                    // names so the guard compares against the emitted C# leaf, not the raw Swift name.
                    var ownChildNames = new HashSet<string>(nestedType.Types.Select(t => ToPascalCaseForTypeName(t.Name)));
                    // Disambiguate the nested type from the colliding property with a kind-aware
                    // semantic suffix: an enum is a closed case-set → "Kind" (idiomatic .NET:
                    // SyntaxKind, DateTimeKind); a struct/class is a data aggregate → "Info"
                    // (FileInfo, ProcessStartInfo). The emitted name still contains the full Swift
                    // leaf (OfferType → OfferTypeKind), so a consumer grepping the Swift name still
                    // finds it, and the multi-collision cascade resolves without noise: a sibling
                    // `Offer` struct → OfferInfo and `OfferType` enum → OfferTypeKind are obviously
                    // distinct, where the old numeric scheme produced the misleading, family-looking
                    // OfferType2/OfferType3.
                    var suffix = nestedType switch
                    {
                        EnumDecl => "Kind",
                        _ => "Info",
                    };
                    // Anti-stutter: if the Swift leaf already ends in the chosen suffix (an enum
                    // `TokenKind`, a struct `PayloadInfo`), don't double it into KindKind/InfoInfo —
                    // use the leaf as-is and let the numeric fallback below disambiguate. This is the
                    // b6d1ba50 anti-stutter guard generalized from the single "Type" suffix to the two
                    // kind-based suffixes.
                    var baseLeafName = csPropertyName.EndsWith(suffix, StringComparison.Ordinal)
                        ? csPropertyName
                        : csPropertyName + suffix;
                    // Numeric fallback — now fires only when the semantic name is itself already taken
                    // by a sibling, an earlier rename in this loop, or the renamed type's own child
                    // (the CS0542 ownChildNames guard). Matches the generator's other dedup paths.
                    var newLeafName = baseLeafName;
                    for (int dedupSuffix = 2;
                         newLeafName == csPropertyName
                             || takenNames.Contains(newLeafName)
                             || ownChildNames.Contains(newLeafName);
                         dedupSuffix++)
                    {
                        newLeafName = $"{baseLeafName}{dedupSuffix}";
                    }

                    var oldCSharpName = nestedRecord.CSharpTypeName.Name;
                    var @namespace = nestedRecord.CSharpTypeName.Namespace;
                    // Replace only the trailing segment (leaf name), not all occurrences.
                    // e.g., "Parent.Configuration" → "Parent.ConfigurationInfo",
                    // NOT "Configuration.Configuration" → "ConfigurationInfo.ConfigurationInfo"
                    var lastDot = oldCSharpName.LastIndexOf('.');
                    var newCSharpName = lastDot >= 0
                        ? oldCSharpName.Substring(0, lastDot + 1) + newLeafName
                        : newLeafName;
                    // Finding 47: route the emission-time rename through the sanctioned
                    // emission-mutation API rather than mutating the stored record in place
                    // (the registry is frozen by the time the main module is emitted).
                    typeDatabase.ApplyEmissionResult(nestedType.SwiftTypeName, new TypeEmissionResult
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, newCSharpName),
                    });

                    // Reserve the chosen name so a subsequent rename in this loop sees it.
                    takenNames.Add(newLeafName);

                    // Cascade rename to all descendant types in the TypeDatabase.
                    CascadeTypeRename(nestedType, oldCSharpName, newCSharpName,
                        @namespace, typeDatabase);
                }
            }
        }
    }

    /// <summary>
    /// Gets the leaf (innermost) type name from a TypeSpec, for "Color Color" pattern detection.
    /// For "Module.Parent.Nested", returns "Nested". For "Optional&lt;Module.Parent.Nested&gt;",
    /// unwraps the Optional and returns "Nested".
    /// </summary>
    internal static string? GetTypeSpecLeafName(TypeSpec typeSpec)
    {
        var unwrapped = UnwrapOptionalTypeSpec(typeSpec);
        if (unwrapped is not NamedTypeSpec namedType)
            return null;

        // Traverse InnerType chain to get the leaf
        var current = namedType;
        while (current.InnerType != null)
            current = current.InnerType;

        // Return the last component of the name (after the last '.')
        var name = current.Name;
        var lastDotIndex = name.LastIndexOf('.');
        return lastDotIndex >= 0 ? name.Substring(lastDotIndex + 1) : name;
    }

    /// <summary>
    /// Unwraps Optional&lt;T&gt; to return the inner TypeSpec. Returns the input if not Optional.
    /// Returns null if the input is not a NamedTypeSpec.
    /// </summary>
    private static TypeSpec? UnwrapOptionalTypeSpec(TypeSpec typeSpec)
    {
        if (typeSpec is not NamedTypeSpec namedType)
            return null;

        var nameWithoutModule = namedType.Name.Contains('.')
            ? namedType.Name.Substring(namedType.Name.LastIndexOf('.') + 1)
            : namedType.Name;
        if (nameWithoutModule == "Optional" && namedType.GenericParameters.Count == 1)
            return UnwrapOptionalTypeSpec(namedType.GenericParameters[0]);

        return namedType;
    }

    /// <summary>
    /// Cascades a CSharpTypeName rename to all descendant types in the TypeDatabase.
    /// When a parent type is renamed (e.g., "Module.Cache" → "Module.CacheInfo"),
    /// all nested types must also be updated (e.g., "Module.Cache.Caches" → "Module.CacheInfo.Caches").
    /// </summary>
    private static void CascadeTypeRename(TypeDecl parentType, string oldPrefix, string newPrefix,
        string @namespace, ITypeDatabase typeDatabase)
    {
        foreach (var childType in parentType.Types)
        {
            if (typeDatabase.TryGetTypeRecord(childType.SwiftTypeName, out var childRecord))
            {
                var childOldName = childRecord.CSharpTypeName.Name;
                if (childOldName.StartsWith(oldPrefix))
                {
                    var childNewName = newPrefix + childOldName.Substring(oldPrefix.Length);
                    // Finding 47: sanctioned emission-mutation path (see PrecomputeNestedTypeRenames).
                    typeDatabase.ApplyEmissionResult(childType.SwiftTypeName, new TypeEmissionResult
                    {
                        CSharpTypeName = CSharpTypeName.FromNamespaceAndName(@namespace, childNewName),
                    });
                    // Recurse into grandchildren
                    CascadeTypeRename(childType, childOldName, childNewName, @namespace, typeDatabase);
                }
            }
        }
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
    public static string GetBoundGenericBufferName(string typeName) => $"{StripVerbatimPrefix(typeName)}Buffer";

    /// <summary>
    /// Gets the name of the async callback delegate field for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncCallbackFieldName(MethodDecl methodDecl)
        => GetAsyncCallbackFieldName(methodDecl.MangledName, methodDecl);

    /// <summary>
    /// AF13: emission-scoped overload — hashes the supplied <paramref name="baseSymbol"/>
    /// (the caller's <c>env.EmissionSymbol</c>) instead of <c>methodDecl.MangledName</c>, so the
    /// callback name tracks the promoted cdecl/wrapper symbol once the parsed model stops mutating.
    /// </summary>
    public static string GetAsyncCallbackFieldName(string baseSymbol, MethodDecl methodDecl)
    {
        var mangledHash = EmitterUtility.DeterministicHash8(baseSymbol);
        return $"s_{methodDecl.Name}Callback_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async callback method for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncCallbackMethodName(MethodDecl methodDecl)
        => GetAsyncCallbackMethodName(methodDecl.MangledName, methodDecl);

    /// <summary>
    /// AF13: emission-scoped overload — see <see cref="GetAsyncCallbackFieldName(string, MethodDecl)"/>.
    /// </summary>
    public static string GetAsyncCallbackMethodName(string baseSymbol, MethodDecl methodDecl)
    {
        var mangledHash = EmitterUtility.DeterministicHash8(baseSymbol);
        return $"{methodDecl.Name}OnComplete_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async error callback delegate field for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncErrorCallbackFieldName(MethodDecl methodDecl)
        => GetAsyncErrorCallbackFieldName(methodDecl.MangledName, methodDecl);

    /// <summary>
    /// AF13: emission-scoped overload — see <see cref="GetAsyncCallbackFieldName(string, MethodDecl)"/>.
    /// </summary>
    public static string GetAsyncErrorCallbackFieldName(string baseSymbol, MethodDecl methodDecl)
    {
        var mangledHash = EmitterUtility.DeterministicHash8(baseSymbol);
        return $"s_{methodDecl.Name}ErrorCallback_{mangledHash}";
    }

    /// <summary>
    /// Gets the name of the async error callback method for a method.
    /// Uses a hash to ensure uniqueness for method overloads.
    /// </summary>
    public static string GetAsyncErrorCallbackMethodName(MethodDecl methodDecl)
        => GetAsyncErrorCallbackMethodName(methodDecl.MangledName, methodDecl);

    /// <summary>
    /// AF13: emission-scoped overload — see <see cref="GetAsyncCallbackFieldName(string, MethodDecl)"/>.
    /// </summary>
    public static string GetAsyncErrorCallbackMethodName(string baseSymbol, MethodDecl methodDecl)
    {
        var mangledHash = EmitterUtility.DeterministicHash8(baseSymbol);
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
    public static string GetPublicMethodName(string methodName, bool isAsync, bool hasReturnValue = false, IReadOnlySet<string>? propertyNames = null, bool isSelfReturning = false, string? parentTypeName = null, int parameterCount = 0, bool isMutating = false)
        => GetPublicMethodName(new PublicMethodNameContext(methodName, isAsync, hasReturnValue, propertyNames, isSelfReturning, parentTypeName, parameterCount, isMutating));

    /// <summary>
    /// Context-object overload of <see cref="GetPublicMethodName(string, bool, bool, IReadOnlySet{string}, bool, string, int)"/>.
    /// Holds the actual name-shaping logic; the positional overload is a thin shim. Building the context once
    /// (via <see cref="PublicMethodNameContext.ForMethod"/>) makes it impossible for a method-derived call site
    /// to silently drop a collision-shaping arg.
    /// </summary>
    public static string GetPublicMethodName(in PublicMethodNameContext ctx)
    {
        // 1. Strip leading async/Async prefix (Swift convention → .NET suffix convention)
        //    Only strip for actual async methods — a sync property named "asyncInstance"
        //    should keep its prefix to avoid getter name collisions (e.g., Instance_Get).
        var strippedName = ctx.IsAsync ? StripAsyncPrefix(ctx.MethodName) : ctx.MethodName;

        // 2. PascalCase
        var name = ToPascalCase(strippedName);

        // 3. Add "Get" prefix for noun-only names with a return value — for sync AND async, so a
        //    zero-arg async getter reads `GetWeatherAsync` rather than `WeatherAsync`, matching the
        //    sync `count() -> Int` → GetCount rule. Doing this BEFORE the property-collision check
        //    also resolves the async-getter-vs-property case cleanly (`status() async` colliding
        //    with a `status` property becomes GetStatusAsync, not StatusMethodAsync).
        //    Skip for self-returning methods (fluent/builder pattern: EqualTo(), Accessibility(), etc.)
        //    and for any method that takes arguments (the Get prefix reads as a getter, not a call).
        //    Skip mutating methods too: they advance/change state and are not getters, so a mutating
        //    `next() async -> Element?` (AsyncIteratorProtocol) stays NextAsync, not GetNextAsync —
        //    the async-sequence bridge dispatches the iterator's advance through that fixed name.
        if (ctx.HasReturnValue && !StartsWithVerb(name) && !ctx.IsSelfReturning && ctx.ParameterCount == 0 && !ctx.IsMutating)
            name = $"Get{name}";

        // 4. Property collision resolution (only if still colliding after verb prefix)
        if (ctx.PropertyNames != null && ctx.PropertyNames.Contains(name))
        {
            if (ctx.IsSelfReturning)
                name = $"With{name}";  // Builder pattern: WithAccessibility()
            else
                name = $"{name}Method";
        }

        // 4b. Inherited method collision: generated C# classes inherit Dispose() from
        // IDisposable/SafeHandle. A Swift method named "dispose" PascalCase's to "Dispose",
        // colliding with the inherited method (CS0111). Suffix with "Swift" to disambiguate.
        if (_inheritedMethodCollisions.Contains(name))
            name = $"{name}Swift";

        // 4c. Type name collision: C# forbids member names identical to the enclosing type (CS0542).
        // This can happen when a Swift type has a method whose PascalCase name matches the type name
        // (e.g., `DatabaseRegion.databaseRegion(_:)` → `DatabaseRegion.DatabaseRegion(Database)`).
        if (ctx.ParentTypeName != null && name == ctx.ParentTypeName)
            name = $"Get{name}";

        // 5. Append "Async" suffix for async methods (per .NET convention)
        if (ctx.IsAsync && !name.EndsWith("Async"))
            name = $"{name}Async";

        return name;
    }

    /// <summary>
    /// Returns a synthetic emitter-local name that does not collide with any reserved in-scope
    /// identifier.
    /// <para>
    /// Emitters hardcode local names in their generated method bodies — <c>tag</c>, <c>result</c>,
    /// <c>resultPtr</c>, <c>handle</c>, <c>session</c>, <c>userData</c>, and similar. When a user's
    /// projected parameter or member name happens to spell the same identifier, the emitted C#
    /// fails to compile: <c>CS0136</c> (a local may not shadow an enclosing local/parameter of the
    /// same name) or <c>CS0100</c> (duplicate parameter name). This guard resolves the collision by
    /// escaping the SYNTHETIC name — never the user-facing name, which the consumer sees and must be
    /// preserved — with a <c>__</c> prefix (the convention already used for derived emitter locals
    /// such as <c>__{name}Swift</c>), escalating with a numeric suffix if the prefixed form is also
    /// taken (a user identifier can legitimately spell <c>__result</c>).
    /// </para>
    /// <para>
    /// Comparison is done on the C# verbatim-stripped form (<see cref="StripVerbatimPrefix"/>), so a
    /// reserved <c>@event</c> and a desired <c>event</c> are treated as the same identifier. The
    /// returned name is never <c>@</c>-prefixed.
    /// </para>
    /// </summary>
    /// <param name="desiredName">The synthetic local name the emitter wants to use.</param>
    /// <param name="reservedNames">In-scope identifiers (projected user parameter/member names, and
    /// any synthetic locals already allocated) that the result must not collide with.</param>
    /// <returns>The verbatim-stripped <paramref name="desiredName"/> if free, otherwise a
    /// <c>__</c>-prefixed (and, if needed, numeric-suffixed) variant guaranteed absent from
    /// <paramref name="reservedNames"/>. The result is never <c>@</c>-prefixed.</returns>
    public static string MakeNonCollidingSyntheticName(string desiredName, IReadOnlySet<string> reservedNames)
    {
        if (string.IsNullOrEmpty(desiredName))
            throw new ArgumentException("Synthetic name must be non-empty.", nameof(desiredName));

        // Normalize to the verbatim-stripped form up front and return THAT, never the raw
        // desiredName. The contract is that the result is never "@"-prefixed: synthetic emitter
        // locals are bare identifiers, and the consumer re-adds "@" only for user-facing keyword
        // names. Returning the raw desiredName on the free/empty paths would leak a "@" prefix that
        // the collision path already strips, so every return point uses the bare form.
        var bare = StripVerbatimPrefix(desiredName);
        if (string.IsNullOrEmpty(bare))
            throw new ArgumentException(
                "Synthetic name must contain an identifier after the verbatim prefix.", nameof(desiredName));
        if (reservedNames == null || reservedNames.Count == 0)
            return bare;

        // Normalize the reserved set to verbatim-stripped form once. User names may carry the C#
        // "@" verbatim prefix (e.g. "@event"); synthetic names never do, so all comparisons happen
        // on the bare identifier.
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in reservedNames)
        {
            if (!string.IsNullOrEmpty(r))
                reserved.Add(StripVerbatimPrefix(r));
        }

        if (!reserved.Contains(bare))
            return bare;

        var prefixed = "__" + bare;
        if (!reserved.Contains(prefixed))
            return prefixed;

        int suffix = 2;
        while (reserved.Contains($"{prefixed}{suffix}"))
            suffix++;
        return $"{prefixed}{suffix}";
    }

    /// <summary>
    /// The synthetic Swift parameter binding names that wrapper emitters inject into a generated
    /// <c>@_cdecl</c>/<c>@_silgen_name</c> function's flat parameter list — alongside the
    /// user-derived parameter bindings. These are the names against which a colliding user binding
    /// must be escaped (see <see cref="EscapeReservedSwiftWrapperLabel"/>).
    /// <para>
    /// The set is the union of every synthetic an emitter can add to the same signature as a user
    /// param: the indirect-result buffer pointer (<c>resultPtr</c>, <c>__resultPtr</c>), the throwing
    /// error out-param (<c>errorOut</c>, <c>errorPtr</c>), the instance-self pointer in its several
    /// spellings (<c>self_</c>, <c>_self</c>, <c>__self</c>, <c>selfObj</c>), the large-Optional /
    /// failable result buffer (<c>_resultBuf</c>), the decomposed-Optional flag pointer
    /// (<c>hasValuePtr</c>, <c>hasValue</c>), the enum discriminator (<c>tag</c>), the collection
    /// parent metadata (<c>parentMetaPtr</c>), the setter value (<c>newValue</c>), the key-path
    /// applicator (<c>_by</c>), the closure-bridge locals (<c>cdecl</c>, <c>innerError</c>), and the
    /// async-trampoline completion pair (<c>completionFn</c>, <c>completionCtx</c>).
    /// </para>
    /// <para>
    /// Over-reserving is output-safe: <see cref="EscapeReservedSwiftWrapperLabel"/> only renames a
    /// user binding that spells one of these EXACTLY, and the rename is source-local (a wrapper that
    /// does not inject the synthetic still compiles and forwards identically). Add a name here when a
    /// new emitter introduces a synthetic Swift wrapper binding that shares a signature with a user
    /// param.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedSwiftWrapperParamNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "resultPtr", "__resultPtr",
            "errorOut", "errorPtr",
            "self_", "_self", "__self", "selfObj",
            "_resultBuf",
            "hasValuePtr", "hasValue",
            "tag",
            "parentMetaPtr",
            "newValue",
            "_by",
            "cdecl", "innerError",
            "completionFn", "completionCtx",
        };

    /// <summary>
    /// Escapes a USER-derived Swift wrapper parameter's INTERNAL binding name when it would collide
    /// with a synthetic binding the emitter injects into the same <c>@_cdecl</c>/<c>@_silgen_name</c>
    /// function signature. Swift requires unique internal parameter names within one function; a
    /// duplicate makes <c>swiftc</c> reject the wrapper, which is then silently dropped from the
    /// compiled dylib — the binding compiles (generator exits 0) but crashes at runtime when the
    /// missing entry point is called (the wrapper-param-name collision class).
    /// <para>
    /// Unlike the user-FACING name (which the consumer sees and must be preserved), a wrapper's
    /// internal binding name is SOURCE-LOCAL: it is not part of the <c>@_cdecl</c> symbol (a
    /// positional C ABI), and the forwarded Swift call's EXTERNAL argument label is computed
    /// separately from <c>arg.Name</c>/<c>OriginalSwiftName</c> via <c>BuildSwiftCallArgLabel</c> —
    /// never from this binding. Renaming the internal binding is therefore output-safe, the same
    /// rationale as the Swift-keyword rename in <c>CdeclParamMapper.Map</c> (<c>{label}</c> →
    /// <c>{label}Param</c>). The bare name is returned unchanged when there is no collision, so
    /// generated Swift is byte-identical in the common (non-colliding) case.
    /// </para>
    /// </summary>
    /// <param name="label">The user-derived internal binding name the emitter is about to emit.</param>
    /// <returns><paramref name="label"/> unchanged when free, otherwise a <c>__</c>-prefixed (and, if
    /// needed, numeric-suffixed) variant guaranteed absent from
    /// <see cref="ReservedSwiftWrapperParamNames"/>.</returns>
    public static string EscapeReservedSwiftWrapperLabel(string label)
        => EscapeReservedSwiftWrapperLabel(label, reservedSiblings: null);

    /// <summary>
    /// Sibling-aware overload of <see cref="EscapeReservedSwiftWrapperLabel(string)"/>: escapes
    /// <paramref name="label"/> against the union of <see cref="ReservedSwiftWrapperParamNames"/> AND
    /// <paramref name="reservedSiblings"/> — the OTHER internal binding names emitted into the same
    /// <c>@_cdecl</c> wrapper signature (the user params' post-keyword/sanitize forms, plus any
    /// hand-emitted generic-pointer binding).
    /// <para>
    /// The global set alone closed only user-vs-synthetic collisions. It missed
    /// user-vs-SIBLING: a user param <c>tag</c> escapes to <c>__tag</c> against the global set, but a
    /// SIBLING user param literally named <c>__tag</c> is not in that set, so the two bindings still
    /// duplicate — <c>swiftc</c> rejects the wrapper and it is silently stripped from the dylib
    /// (runtime-missing entry point). Reserving the siblings here makes the escape pick <c>__tag2</c>.
    /// </para>
    /// <para>
    /// The CALLER is responsible for ensuring <paramref name="reservedSiblings"/> does not contain
    /// <paramref name="label"/>'s own binding (a param must never be escaped against itself). The
    /// per-param <c>Map</c>/<c>MapInout</c> chokepoint strips the current label before calling; a
    /// hand-emit site whose emitted binding (e.g. <c>_{label}</c>) is not itself in the sibling set
    /// passes the set unchanged. Over-reserving a name that does not equal the escape target is
    /// harmless; output stays byte-identical when there is no collision.
    /// </para>
    /// </summary>
    public static string EscapeReservedSwiftWrapperLabel(string label, IReadOnlySet<string>? reservedSiblings)
    {
        if (string.IsNullOrEmpty(label))
            return label;
        if (reservedSiblings == null || reservedSiblings.Count == 0)
            return MakeNonCollidingSyntheticName(label, ReservedSwiftWrapperParamNames);

        var combined = new HashSet<string>(ReservedSwiftWrapperParamNames, StringComparer.Ordinal);
        foreach (var sibling in reservedSiblings)
        {
            if (!string.IsNullOrEmpty(sibling))
                combined.Add(StripVerbatimPrefix(sibling));
        }
        return MakeNonCollidingSyntheticName(label, combined);
    }
}

/// <summary>
/// Allocates emitter-local "synthetic" names (e.g. <c>tag</c>, <c>result</c>, <c>resultPtr</c>,
/// <c>handle</c>, <c>session</c>, <c>userData</c>) that are guaranteed not to collide with any
/// in-scope user identifier OR with a previously allocated synthetic name in the same scope.
/// <para>
/// Emitters hardcode these local names in their generated method bodies. When a user's projected
/// parameter or member name spells the same identifier, the emitted C# fails to compile (CS0136 /
/// CS0100). This scope resolves the collision by escaping the SYNTHETIC name — never the
/// user-facing name — via <see cref="NameProvider.MakeNonCollidingSyntheticName"/>.
/// </para>
/// <para>
/// Seed the scope with the in-scope user identifiers, then call <see cref="Reserve"/> for each
/// synthetic local. Each reservation is recorded so subsequent reservations also avoid it:
/// <code>
///   var names     = new SyntheticNameScope(
///       method.CSSignature.Skip(1).Select(NameProvider.GetCSharpParameterName));
///   var result    = names.Reserve("result");     // "result"    if free, else "__result"
///   var resultPtr = names.Reserve("resultPtr");   // distinct from result AND from user params
/// </code>
/// </para>
/// </summary>
public sealed class SyntheticNameScope
{
    // Stored in verbatim-stripped form so "@event" and "event" collide as one identifier.
    private readonly HashSet<string> _reserved;

    /// <summary>
    /// Creates a scope seeded with the in-scope user identifiers the synthetic names must avoid.
    /// Null or empty entries are ignored; names are normalized via
    /// <see cref="NameProvider.StripVerbatimPrefix"/>.
    /// </summary>
    public SyntheticNameScope(IEnumerable<string>? reservedUserNames = null)
    {
        _reserved = new HashSet<string>(StringComparer.Ordinal);
        if (reservedUserNames != null)
        {
            foreach (var name in reservedUserNames)
            {
                if (!string.IsNullOrEmpty(name))
                    _reserved.Add(NameProvider.StripVerbatimPrefix(name));
            }
        }
    }

    /// <summary>
    /// Returns a non-colliding name for the requested synthetic local and reserves it so later
    /// calls in this scope avoid it too.
    /// </summary>
    public string Reserve(string desiredName)
    {
        var chosen = NameProvider.MakeNonCollidingSyntheticName(desiredName, _reserved);
        _reserved.Add(NameProvider.StripVerbatimPrefix(chosen));
        return chosen;
    }

    /// <summary>
    /// True if the (verbatim-normalized) name is already reserved in this scope — either seeded as
    /// a user identifier or previously returned by <see cref="Reserve"/>.
    /// </summary>
    public bool IsReserved(string name)
        => !string.IsNullOrEmpty(name) && _reserved.Contains(NameProvider.StripVerbatimPrefix(name));
}

/// <summary>
/// Resolved, collision-safe names for the synthetic locals that the sync-wrapper emission path
/// hardcodes into the generated C# wrapper body and P/Invoke call: the indirect-result buffer
/// pointer (<c>resultPtr</c>), the decomposed-optional flag pointer (<c>hasValuePtr</c>), the
/// non-cdecl indirect-result register (<c>swiftIndirectResult</c>), the constructor buffer
/// pointer (<c>bufferPtr</c>), and the return/inner type-metadata temporaries
/// (<c>returnMetadata</c>, <c>innerMetadata</c>).
/// <para>
/// These names are referenced by string convention across three phases that must agree:
/// the synthetic P/Invoke parameter added by <c>PInvokeSignatureBuilder</c> (which drives the
/// positional call argument through <c>CallArgumentsString</c>), the allocation snippets built by
/// <c>MethodMarshalPlanBuilder</c>, and the return-value marshalling in <c>WrapperEmitter.Return</c>.
/// A user parameter that projects to the same C# identifier would shadow the body local (CS0136).
/// Resolving the names once here — seeded from the same projected parameter names that
/// <c>ResolveReturnLocalName</c> uses — and sharing them via <see cref="MethodEnvironment"/> keeps
/// all three phases consistent while escaping the SYNTHETIC name (never the user-facing one).
/// </para>
/// <para>
/// Each name is resolved eagerly in a fixed order so the result is independent of which phase
/// reads it first. <see cref="SyntheticNameScope.Reserve"/> returns the bare spelling unless a user
/// parameter collides, so generated output is byte-identical for the overwhelmingly common
/// non-colliding case. The <c>_</c>-prefixed internal temporaries (<c>_cdeclBuf</c>, <c>_bufSize</c>,
/// <c>_innerSize</c>, <c>_cdeclResult</c>) and the indexed <c>tupleResult{i}Ptr</c> family are NOT
/// resolved here — a public Swift API parameter spelling one of those is not a realistic collision,
/// and they remain string literals in their emitters.
/// </para>
/// </summary>
public sealed class SyntheticLocalNames
{
    /// <summary>Indirect-result / @_cdecl payload buffer pointer (most common synthetic local).</summary>
    public string ResultPtr { get; }

    /// <summary>Decomposed-Optional has-value flag pointer.</summary>
    public string HasValuePtr { get; }

    /// <summary>Non-cdecl indirect-result register struct.</summary>
    public string SwiftIndirectResult { get; }

    /// <summary>Constructor frozen-with-ref-field buffer pointer (aliased to <see cref="ResultPtr"/>).</summary>
    public string BufferPtr { get; }

    /// <summary>Return-type metadata temporary used to size the result buffer.</summary>
    public string ReturnMetadata { get; }

    /// <summary>Inner-type metadata temporary for decomposed Optional returns.</summary>
    public string InnerMetadata { get; }

    private SyntheticLocalNames(SyntheticNameScope scope)
    {
        // Fixed resolution order → access-order independent. Reserve records each chosen name so
        // the (vanishingly unlikely) case where two synthetics escape into the same identifier is
        // still kept distinct.
        ResultPtr = scope.Reserve("resultPtr");
        HasValuePtr = scope.Reserve("hasValuePtr");
        SwiftIndirectResult = scope.Reserve("swiftIndirectResult");
        BufferPtr = scope.Reserve("bufferPtr");
        ReturnMetadata = scope.Reserve("returnMetadata");
        InnerMetadata = scope.Reserve("innerMetadata");
    }

    /// <summary>
    /// Resolves the synthetic-local bundle for a method, seeded from the projected C# parameter
    /// names (skipping the return slot at index 0), mirroring <c>ResolveReturnLocalName</c>.
    /// </summary>
    public static SyntheticLocalNames Resolve(MethodDecl method)
        => new SyntheticLocalNames(new SyntheticNameScope(
            method.CSSignature.Skip(1).Select(NameProvider.GetCSharpParameterName)));
}
