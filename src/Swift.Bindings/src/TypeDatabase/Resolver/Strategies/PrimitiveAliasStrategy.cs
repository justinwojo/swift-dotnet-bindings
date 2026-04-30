// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves Foundation typealiases that target Swift stdlib primitives
/// (e.g., <c>Foundation.TimeInterval</c> → <c>Swift.Double</c>). Mapping
/// table lives in <see cref="MarshallingHelpers.TypeAliasToCSPrimitive"/>;
/// this strategy looks up the underlying primitive's
/// <see cref="TypeRecord"/> via the database so downstream code sees a real
/// struct record instead of the synthetic ObjC-bridged class record that
/// Apple framework heuristics would otherwise hand back.
/// </summary>
internal sealed class PrimitiveAliasStrategy : IResolutionStrategy
{
    public string Name => "PrimitiveAlias";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        result = null;

        if (typeSpec is not NamedTypeSpec named)
            return false;

        if (!MarshallingHelpers.TypeAliasToCSPrimitive.TryGetValue(named.Name, out var primitive))
            return false;

        var underlying = primitive switch
        {
            "double" => SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            _ => null,
        };
        if (underlying is null)
            return false;

        if (!context.Database.TryGetTypeRecord(underlying, out var record))
            return false;

        result = new TypeResolutionResult(
            Record: record,
            Provenance: new ResolutionProvenance($"strategy:{Name}"));
        return true;
    }
}
