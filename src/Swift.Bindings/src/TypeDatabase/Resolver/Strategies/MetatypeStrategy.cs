// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves Swift metatypes (<c>Foundation.Decimal.Type</c>, <c>Any.Type</c>,
/// nested <c>Module.Type.Type</c> chains) to
/// <see cref="TypeDatabaseExtensions.AnyType"/>. C# has no native metatype
/// equivalent — without this short-circuit, the nested-name flattening in
/// <see cref="TypeDatabaseExtensions.CreateObjCBridgedTypeRecord"/> would emit
/// invalid identifiers like <c>Foundation.DecimalType</c> (CS0234).
/// </summary>
internal sealed class MetatypeStrategy : IResolutionStrategy
{
    public string Name => "Metatype";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (WrapperValidation.IsMetatypeType(typeSpec))
        {
            result = new TypeResolutionResult(
                Record: TypeDatabaseExtensions.AnyType,
                Provenance: new ResolutionProvenance($"strategy:{Name}"));
            return true;
        }

        result = null;
        return false;
    }
}
