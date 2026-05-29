// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves Apple-framework typealiases that target Swift stdlib primitives
/// (e.g., <c>Foundation.TimeInterval</c> → <c>Swift.Double</c>,
/// <c>Darwin.OSStatus</c> → <c>Swift.Int32</c>). Mapping table lives in
/// <see cref="MarshallingHelpers.TypeAliasToCSPrimitive"/>; this strategy
/// looks up the underlying primitive's <see cref="TypeRecord"/> via the
/// database so downstream code sees a real struct record instead of the
/// synthetic ObjC-bridged class record that Apple framework heuristics
/// would otherwise hand back — and so the closure parameter/return gate
/// in <c>ClosureHandler</c> accepts the typealias as a known type.
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

        // Cases mirror the C# keyword values in MarshallingHelpers.TypeAliasToCSPrimitive
        // (see the comment there). Adding a new alias requires extending BOTH this switch
        // and the dictionary in lockstep — the dictionary value is also emitted verbatim
        // as the C# type name by TypeProjectionFactory, so the keyword form is required.
        var underlying = primitive switch
        {
            "double" => SwiftTypeName.FromModuleQualifiedName("Swift.Double"),
            "int" => SwiftTypeName.FromModuleQualifiedName("Swift.Int32"),
            "long" => SwiftTypeName.FromModuleQualifiedName("Swift.Int64"),
            "uint" => SwiftTypeName.FromModuleQualifiedName("Swift.UInt32"),
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
