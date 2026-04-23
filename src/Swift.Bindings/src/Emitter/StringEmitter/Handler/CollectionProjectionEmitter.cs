// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Projects generic Swift structs that conform to Collection/RandomAccessCollection as
/// C# <c>IReadOnlyList&lt;TElement&gt;</c>. The projection is narrow on purpose: it fires
/// only when the struct has (a) a Collection-family protocol conformance and (b) a
/// single public property whose type is <c>Swift.Array&lt;T&gt;</c> where <c>T</c> is the
/// struct's own generic parameter. This covers the "Collection-with-metadata" shape
/// used by WeatherKit's <c>Forecast&lt;Element&gt;</c> (backing <c>forecast: [Element]</c>
/// plus a <c>metadata</c> property) without trying to dispatch through the protocol
/// witness table for arbitrary collection types.
///
/// Emitted surface: <c>Count</c>, <c>this[int index]</c>, <c>GetEnumerator()</c>, and
/// the non-generic <c>IEnumerable.GetEnumerator()</c>. All delegate to the existing
/// backing-array property, which already round-trips through the Swift runtime as
/// a <c>IReadOnlyList&lt;TElement&gt;</c>.
/// </summary>
internal static class CollectionProjectionEmitter
{
    private static readonly HashSet<string> s_collectionProtocols = new()
    {
        "Swift.Collection",
        "Swift.RandomAccessCollection",
        "Swift.BidirectionalCollection",
        "Swift.Sequence",
    };

    /// <summary>
    /// Decides whether the projection will fire on this struct. Returns the
    /// C# interface name to add to the class's interface list (e.g.
    /// <c>IReadOnlyList&lt;TElement&gt;</c>) or <c>null</c> when the projection
    /// does not apply. Must be called before the class header is written so the
    /// interface can be inserted.
    /// </summary>
    public static string? TryPlanInterface(StructDecl structDecl, ITypeDatabase typeDatabase)
    {
        var backing = TryFindBacking(structDecl, typeDatabase);
        if (backing is null)
            return null;
        var (_, elementCsName) = backing.Value;
        return $"global::System.Collections.Generic.IReadOnlyList<{elementCsName}>";
    }

    /// <summary>
    /// Emits the projection member bodies. Call only after <see cref="TryPlanInterface"/>
    /// has returned non-null for this struct. Uses <paramref name="propertyRenames"/> so
    /// the delegated property name matches what <see cref="NonFrozenStructHandler"/>
    /// actually emitted (C# identifier collisions between a property and a nested type
    /// trigger a rename; see <see cref="NameProvider.ComputePropertyRenames"/>).
    /// </summary>
    public static void EmitMembers(
        CSharpWriter csWriter,
        StructDecl structDecl,
        ITypeDatabase typeDatabase,
        IReadOnlyDictionary<string, string>? propertyRenames,
        ILogger logger)
    {
        var backing = TryFindBacking(structDecl, typeDatabase);
        if (backing is null)
            return;

        var (prop, elementCsName) = backing.Value;
        var backingCsName = NameProvider.GetFinalMemberName(
            NameProvider.GetPropertyName(prop.Name, structDecl.Name), propertyRenames);

        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Number of elements — projection of Swift <c>Collection.count</c>.</summary>");
        csWriter.WriteLine($"public int Count => {backingCsName}.Count;");
        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Element access — projection of Swift <c>subscript(_:)</c>.</summary>");
        csWriter.WriteLine($"public {elementCsName} this[int index] => {backingCsName}[index];");
        csWriter.WriteLine();
        csWriter.WriteLine("/// <summary>Iterates the collection in index order.</summary>");
        csWriter.WriteLine($"public global::System.Collections.Generic.IEnumerator<{elementCsName}> GetEnumerator() => {backingCsName}.GetEnumerator();");
        csWriter.WriteLine("global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();");
        csWriter.WriteLine();

        logger.LogInformation(
            "Emitted Collection projection on '{TypeName}' backed by property '{Backing}'.",
            structDecl.Name, prop.Name);
    }

    private static (PropertyDecl prop, string elementCsName)? TryFindBacking(StructDecl structDecl, ITypeDatabase typeDatabase)
    {
        if (structDecl.GenericParameters.Count != 1)
            return null;
        if (!HasCollectionConformance(structDecl))
            return null;

        var param = structDecl.GenericParameters[0];
        var unsugaredName = param.TypeName;
        var sugaredName = param.SugaredTypeName;
        if (string.IsNullOrEmpty(unsugaredName) || string.IsNullOrEmpty(sugaredName))
            return null;

        // The C# generic parameter name (e.g. "TElement" / "T0") may differ from the
        // Swift source name — use the canonical projection so the emitted C# compiles.
        var csParamName = NameProvider.GetCSharpGenericParameterName(param, 0);

        PropertyDecl? match = null;
        foreach (var p in structDecl.Properties)
        {
            // Keep backing-property filters in sync with MemberEmissionValidator.CanEmitProperty:
            // if the property won't be emitted, the projection body has nothing to delegate to.
            if (p.IsStatic || p.IsModuleInternal || p.IsSpiProtected)
                continue;
            var skipReason = MemberEmissionValidator.CanEmitProperty(p, typeDatabase, out _, out _);
            if (skipReason != null)
                continue;
            if (p.SwiftTypeSpec is not NamedTypeSpec named)
                continue;
            if (named.Name != "Swift.Array" || named.GenericParameters.Count != 1)
                continue;
            if (named.GenericParameters[0] is not NamedTypeSpec elem)
                continue;
            // Swift emits generic params as τ_0_N in the unsugared TypeSpec; some ABI paths
            // also surface the sugared form ("Element"). Accept either.
            if (elem.Name != unsugaredName && elem.Name != sugaredName)
                continue;
            if (match is not null)
                return null;
            match = p;
        }
        return match is null ? null : (match, csParamName);
    }

    /// <summary>
    /// Returns true when the struct conforms to Swift.Collection, Sequence,
    /// BidirectionalCollection, or RandomAccessCollection. Shared with
    /// <see cref="GenericDispatchEmitter.CanEmitStaticDispatch"/> and
    /// <c>PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper</c> to relax the
    /// generic-parent-param gates for Collection-family conformers.
    /// </summary>
    internal static bool HasCollectionConformance(StructDecl structDecl)
    {
        foreach (var c in structDecl.Conformances)
        {
            if (s_collectionProtocols.Contains(c.Protocol.ModuleQualifiedName))
                return true;
            if (c.Protocol.Name is "Collection" or "RandomAccessCollection"
                or "BidirectionalCollection" or "Sequence")
                return true;
        }
        return false;
    }
}
