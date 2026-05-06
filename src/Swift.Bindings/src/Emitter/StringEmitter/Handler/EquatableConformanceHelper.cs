// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// Decides whether a generic Swift type's <c>Equatable</c> / <c>Hashable</c> conformance can
/// safely be projected to C# as a typed <c>IEquatable&lt;T&gt;</c> interface plus
/// <c>Equals(T?)</c> / <c>GetHashCode</c> / <c>operator ==</c> emission.
///
/// <para>The Swift ABI JSON does not preserve the conditional <c>where T : Equatable</c> /
/// <c>where T : Hashable</c> clause from <c>extension Foo : Equatable where T : Equatable</c>.
/// All conformances arrive as flat protocol entries. For non-generic types that's fine — every
/// conformance is unconditional. For generic types, however, Swift's stdlib and Apple frameworks
/// universally express Equatable/Hashable on collection-shaped generics conditionally
/// (<c>MusicKit.MusicItemCollection&lt;T&gt;</c>, <c>WeatherKit.Forecast&lt;T&gt;</c>, etc.), so
/// emitting <c>IEquatable&lt;Foo&lt;T&gt;&gt;</c> + the bound-witness P/Invoke unconditionally is a
/// runtime trap: Swift's protocol-witness lookup expects <c>T</c>'s Equatable witness table at
/// the call site and segfaults / traps when the consumer instantiates with a non-Equatable
/// <c>T</c>.</para>
///
/// <para>Rule: a generic type's Equatable/Hashable conformance is treated as <em>unconditional
/// in C# terms</em> only when every generic parameter has at least one declared protocol
/// constraint that transitively conforms to the same protocol — i.e. the C# type system
/// already prevents the consumer from instantiating with a witness-less <c>T</c>. We walk
/// each generic parameter's <see cref="GenericParameterConformance"/> entries and follow
/// <see cref="TypeRecord.ProtocolConformances"/> edges through the <see cref="ITypeDatabase"/>
/// to find direct or refining matches. Any generic parameter without such a constraint
/// flips the conformance to <em>conditional</em>, and the emitter drops the typed
/// <c>IEquatable</c> / <c>Equals(T?)</c> / <c>GetHashCode</c> / <c>operator ==</c> surface so
/// reference equality is inherited from <c>object</c> instead.</para>
///
/// <para>This is intentionally conservative: when the parser cannot prove the conformance is
/// safe, we drop typed equality. Consumers that genuinely need value equality on a generic
/// type whose witnesses are conditional are pushed to <c>Equals(object?)</c>, which still
/// boxes-and-compares but never traps.</para>
/// </summary>
internal static class EquatableConformanceHelper
{
    public const string SwiftEquatableModuleQualifiedName = "Swift.Equatable";
    public const string SwiftHashableModuleQualifiedName = "Swift.Hashable";

    /// <summary>
    /// Returns <c>true</c> when the type's conformance to <paramref name="protocolModuleQualifiedName"/>
    /// can be safely projected to C# typed equality / hashing (i.e. is unconditional, or every
    /// generic parameter is C#-constrained to a protocol that transitively refines the target).
    ///
    /// Returns <c>false</c> when the type is generic and at least one generic parameter has no
    /// constraint that demonstrably guarantees the witness table — the conformance is treated
    /// as conditional and the typed surface is dropped.
    /// </summary>
    /// <param name="typeDecl">The conforming type.</param>
    /// <param name="typeDatabase">Type database used to walk protocol-refinement edges. May be null in tests; null is treated as "cannot prove refinement" and falls back to direct-match only.</param>
    /// <param name="protocolModuleQualifiedName">Module-qualified protocol name (e.g. <c>"Swift.Equatable"</c>).</param>
    public static bool IsConformanceUnconditionalForCSharp(
        TypeDecl typeDecl,
        ITypeDatabase? typeDatabase,
        string protocolModuleQualifiedName)
    {
        var genericParameters = typeDecl.GenericParameters;
        if (genericParameters == null || genericParameters.Count == 0)
        {
            // Non-generic types cannot have conditional conformances — the Swift ABI carries
            // them only on extensions of generic types.
            return true;
        }

        foreach (var param in genericParameters)
        {
            if (!ParameterGuaranteesProtocol(param, typeDatabase, protocolModuleQualifiedName))
                return false;
        }

        return true;
    }

    private static bool ParameterGuaranteesProtocol(
        GenericArgumentDecl param,
        ITypeDatabase? typeDatabase,
        string protocolModuleQualifiedName)
    {
        if (param.GenericConformances == null) return false;

        var visited = new HashSet<string>();
        foreach (var conformance in param.GenericConformances)
        {
            if (conformance.Kind != ConformanceKind.Protocol) continue;
            if (TransitivelyConformsTo(conformance.ConformanceTarget, protocolModuleQualifiedName, typeDatabase, visited))
                return true;
        }

        return false;
    }

    private static bool TransitivelyConformsTo(
        SwiftTypeName candidate,
        string targetModuleQualifiedName,
        ITypeDatabase? typeDatabase,
        HashSet<string> visited)
    {
        if (candidate.ModuleQualifiedName == targetModuleQualifiedName) return true;

        // Hashable refines Equatable in Swift's stdlib. Recognize the well-known refinement
        // even when a TypeRecord isn't loaded for Swift.Hashable (small frameworks may not
        // pull the full stdlib type-record graph).
        if (targetModuleQualifiedName == SwiftEquatableModuleQualifiedName
            && candidate.ModuleQualifiedName == SwiftHashableModuleQualifiedName)
            return true;

        if (typeDatabase == null) return false;
        if (!visited.Add(candidate.ModuleQualifiedName)) return false;
        if (!typeDatabase.TryGetTypeRecord(candidate, out var record)) return false;
        if (record.ProtocolConformances == null) return false;

        foreach (var refined in record.ProtocolConformances)
        {
            if (TransitivelyConformsTo(refined, targetModuleQualifiedName, typeDatabase, visited))
                return true;
        }

        return false;
    }
}
