// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves bound-generic Swift stdlib SIMD types
/// (<c>Swift.SIMD2&lt;Swift.Float&gt;</c>, <c>Swift.SIMD3&lt;Swift.Float&gt;</c>,
/// <c>Swift.SIMD4&lt;Swift.Float&gt;</c>) to the corresponding non-generic
/// <c>simd.simd_floatN</c> aliases. Must run before
/// <see cref="DatabaseLookupStrategy"/> — the bare <c>Swift.SIMD3</c>
/// identity is a registered stdlib generic that would otherwise win the
/// lookup, producing a generic record without the bound element type.
/// </summary>
internal sealed class BoundGenericSimdAliasStrategy : IResolutionStrategy
{
    public string Name => "BoundGenericSimdAlias";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named
            && TypeDatabaseExtensions.TryResolveBoundGenericAlias(context.Database, named, out var record))
        {
            result = new TypeResolutionResult(
                Record: record,
                Provenance: new ResolutionProvenance($"strategy:{Name}"));
            return true;
        }

        result = null;
        return false;
    }
}
