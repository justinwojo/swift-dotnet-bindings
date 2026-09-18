// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// The single home for the bound-generic <see cref="NamedTypeSpec"/> → C# type-name mapping that the
/// closure and tuple element translators previously re-implemented line for line, plus the SIMD
/// bound-generic alias-collapse short-circuit shared by every C# translator.
///
/// <para>
/// Only the part of the mapping that was genuinely identical across callers lives here. The leaf
/// resolution is deliberately split into two reusable pieces:
/// <list type="number">
///   <item><see cref="TryResolveSimdAliasCSharp"/> — the alias-collapse: a one-parameter bound
///   generic whose argument matches a SIMD typealias (e.g. <c>Swift.SIMD3&lt;Swift.Float&gt;</c>)
///   resolves to the non-generic managed alias record (<c>simd.simd_float3</c>) and must NOT have
///   the bound-generic's argument re-appended. Every C# translator runs this first.</item>
///   <item><see cref="TranslateBoundGenericToCSharp"/> — the closure/tuple bound-generic body:
///   alias-collapse, base-record lookup (unknown base → AnyType), pointer short-circuit, per-argument
///   translation (existential argument → well-known/public existential type; everything else through
///   the caller-supplied recursion delegate), then the <c>Name&lt;args&gt;</c> wrap.</item>
/// </list>
/// </para>
///
/// <para>
/// What stays OUT of this service, in each handler, is the structural shaping that legitimately
/// differs per emission context and is NOT alias-collapse: the bound-generic handler's nested-owner
/// qualification, generic-context resolution and stdlib-container argument rules; the closure
/// handler's <c>Optional&lt;T&gt;</c>→<c>T?</c>, <c>Swift.String</c>→<c>string</c>,
/// <c>Foundation.Data</c>→<c>byte[]</c> and native-type remaps; the top-level existential policy
/// (container vs. interface vs. <c>object</c>); the async projection paths; and the P/Invoke
/// translators. Those produce different bytes by design, so a single two-mode "translate anything"
/// entry point cannot reproduce them — only the bound-generic body above is common.
/// </para>
///
/// <para>
/// The recursion is the caller's, supplied as a <see cref="System.Func{TypeSpec, String}"/>, mirroring
/// the existing <c>TupleHandler.GetCSharpTupleType(TupleTypeSpec, Func&lt;TypeSpec, string&gt;)</c>
/// shape, so each handler keeps its own element-translation rules for nested arguments. The handler's
/// own (per-instance) <see cref="ExistentialHandler"/> is threaded in for the same reason — callers
/// construct their own, and swapping it could change existential bytes.
/// </para>
/// </summary>
internal static class BoundGenericTranslation
{
    /// <summary>
    /// Short-circuits a bound-generic SIMD alias (e.g. <c>Swift.SIMD3&lt;Swift.Float&gt;</c> →
    /// <c>System.Numerics.Vector3</c>) to the resolved non-generic alias record's C# name. Returns
    /// <c>false</c> when <paramref name="namedType"/> is not such an alias, leaving the caller to its
    /// normal bound-generic handling. Appending the bound-generic's type argument to the resolved
    /// typealias would produce invalid syntax (a Swift typealias is not a C# generic), so the alias
    /// record IS the final type.
    /// </summary>
    internal static bool TryResolveSimdAliasCSharp(
        ITypeDatabase typeDatabase, NamedTypeSpec namedType, [NotNullWhen(true)] out string? csharp)
    {
        if (TypeDatabaseExtensions.TryResolveBoundGenericAlias(typeDatabase, namedType, out var aliasRecord))
        {
            csharp = aliasRecord.CSharpTypeName.FullyQualifiedName;
            return true;
        }

        csharp = null;
        return false;
    }

    /// <summary>
    /// Spells a type nested inside a bound generic (<c>Outer&lt;Int32&gt;.Leaf</c>) from the type
    /// database alone. The ABI parser encodes that reference as the outer <see cref="NamedTypeSpec"/>
    /// carrying the generic arguments with an <see cref="NamedTypeSpec.InnerType"/> chain naming the
    /// nested segments, so each link's arguments belong on that link's own segment of the leaf's
    /// dotted C# name (<c>Outer&lt;int&gt;.Leaf</c>), never appended after the leaf. Each link's
    /// segment is located by resolving the link's own Swift prefix (<c>Module.Outer</c>,
    /// <c>Module.Outer.Leaf</c>) and requiring its C# name to be a dotted prefix of the leaf's.
    /// Returns <c>false</c> when the spec has no inner chain, a prefix is unknown, or the C# names
    /// don't nest that way, leaving the caller to its own spelling.
    /// </summary>
    /// <param name="typeDatabase">The type database used to resolve each prefix record.</param>
    /// <param name="namedType">The outer spec whose <see cref="NamedTypeSpec.InnerType"/> chain
    /// names the nested leaf.</param>
    /// <param name="translateGenericArgument">The caller's element translator, applied to every
    /// generic argument on the chain.</param>
    /// <param name="outerArguments">Arguments for the outer link already translated by the caller
    /// under its own rules; when <c>null</c>, the outer link's arguments go through
    /// <paramref name="translateGenericArgument"/> like the inner links'.</param>
    internal static bool TryTranslateNestedInBoundGeneric(
        ITypeDatabase typeDatabase,
        NamedTypeSpec namedType,
        Func<TypeSpec, string> translateGenericArgument,
        [NotNullWhen(true)] out string? csharp,
        IReadOnlyList<string>? outerArguments = null)
    {
        csharp = null;
        if (namedType.InnerType == null || !namedType.HasModule())
            return false;

        if (!typeDatabase.TryGetTypeRecord(SwiftTypeName.FromTypeSpec(namedType), out var leafRecord))
            return false;
        var leafName = leafRecord.CSharpTypeName.FullyQualifiedName;
        if (leafName.Contains('<'))
            return false;

        var builder = new System.Text.StringBuilder();
        var swiftPrefix = string.Empty;
        var consumed = 0;
        for (NamedTypeSpec? link = namedType; link != null; link = link.InnerType)
        {
            swiftPrefix = swiftPrefix.Length == 0 ? link.Name : $"{swiftPrefix}.{link.Name}";
            if (!typeDatabase.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(swiftPrefix), out var ownerRecord))
                return false;

            var ownerName = ownerRecord.CSharpTypeName.FullyQualifiedName;
            if (ownerName.Length <= consumed ||
                !leafName.StartsWith(ownerName, StringComparison.Ordinal) ||
                (ownerName.Length < leafName.Length && leafName[ownerName.Length] != '.'))
                return false;

            builder.Append(leafName, consumed, ownerName.Length - consumed);
            consumed = ownerName.Length;

            if (link.GenericParameters.Count > 0)
            {
                var args = ReferenceEquals(link, namedType) && outerArguments != null
                    ? outerArguments
                    : link.GenericParameters.Select(translateGenericArgument).ToList();
                if (args.Count != link.GenericParameters.Count)
                    return false;
                builder.Append('<').Append(string.Join(", ", args)).Append('>');
            }
        }

        if (consumed != leafName.Length)
            return false;

        csharp = builder.ToString();
        return true;
    }

    /// <summary>
    /// Translates a bound-generic <see cref="NamedTypeSpec"/> to its full C# type name with generic
    /// arguments — the tuple element translator's body. (The closure lane no longer calls this: a
    /// bound generic inside a closure signature is spelled by <see cref="BoundGenericsHandler"/>,
    /// the same translator the method-closure-bridge lane uses, so an interface and its conformer
    /// agree on the string.)
    /// </summary>
    /// <param name="typeDatabase">The type database used to resolve the base record.</param>
    /// <param name="existentialHandler">The caller's existential handler, used to translate
    /// existential generic arguments to their public/well-known type.</param>
    /// <param name="namedType">The bound-generic type to translate.</param>
    /// <param name="translateGenericArgument">The caller's element-translation delegate, applied to
    /// each non-existential generic argument so nested arguments pick up the caller's own rules.</param>
    /// <param name="mapEmptyTupleArgumentToSwiftVoid">When <c>true</c>, an empty-tuple generic
    /// argument maps to <c>Swift.SwiftVoid</c>; when <c>false</c>, it flows through
    /// <paramref name="translateGenericArgument"/> like any other argument (the tuple path).</param>
    /// <param name="bareGenericSafetyNet">When <c>true</c>, a base type that resolved to a bare
    /// generic C# name with no translated arguments falls back to AnyType rather than emitting an
    /// argument-less generic name (CS0305); the tuple path omits it.</param>
    internal static string TranslateBoundGenericToCSharp(
        ITypeDatabase typeDatabase,
        ExistentialHandler existentialHandler,
        NamedTypeSpec namedType,
        Func<TypeSpec, string> translateGenericArgument,
        bool mapEmptyTupleArgumentToSwiftVoid,
        bool bareGenericSafetyNet)
    {
        if (TryResolveSimdAliasCSharp(typeDatabase, namedType, out var aliasCSharp))
        {
            return aliasCSharp;
        }

        var baseTypeName = SwiftTypeName.FromModuleQualifiedName(namedType.Name);
        if (!typeDatabase.TryGetTypeRecord(baseTypeName, out var typeRecord))
        {
            // Fallback if base type not in database
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        // Pointer types like UnsafeMutablePointer<T> resolve to IntPtr which doesn't support generics
        if (typeRecord == TypeDatabaseExtensions.IntPtrType)
        {
            return typeRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Recursively translate all generic parameters
        var translatedParams = new List<string>();
        foreach (var genericParam in namedType.GenericParameters)
        {
            // Map Swift.Void (empty tuple) to SwiftVoid for generic type arguments (closure path only)
            if (mapEmptyTupleArgumentToSwiftVoid && genericParam.IsEmptyTuple)
            {
                translatedParams.Add("Swift.SwiftVoid");
                continue;
            }

            // Handle existential generic parameters (e.g., Array<any Protocol>)
            if (existentialHandler.IsExistential(genericParam))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(genericParam);
                if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                {
                    if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wk))
                        translatedParams.Add(wk);
                    else
                        translatedParams.Add(existentialHandler.GetPublicExistentialType(protocolList));
                    continue;
                }
            }

            translatedParams.Add(translateGenericArgument(genericParam));
        }

        // A type nested in this bound generic: the arguments belong on the outer segment of the
        // leaf's name, and the leaf is the type being named, not the outer.
        if (TryTranslateNestedInBoundGeneric(typeDatabase, namedType, translateGenericArgument,
                out var nestedCSharp, translatedParams))
        {
            return nestedCSharp;
        }

        // Safety net: if no generic params were translated but the base type requires them,
        // return AnyType to prevent bare generic type names like "SwiftDictionary" (CS0305)
        if (bareGenericSafetyNet &&
            translatedParams.Count == 0 &&
            TypeDatabaseExtensions.IsBareGenericTypeName(typeRecord.CSharpTypeName.FullyQualifiedName))
        {
            return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
        }

        // Build full type name with generics
        return translatedParams.Count > 0
            ? $"{typeRecord.CSharpTypeName.FullyQualifiedName}<{string.Join(", ", translatedParams)}>"
            : typeRecord.CSharpTypeName.FullyQualifiedName;
    }
}
