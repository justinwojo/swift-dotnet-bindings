// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves Swift's <c>Self</c> dynamic-self return type. The runtime type of
/// the receiver is unknown at binding-generation time, so the binding surface
/// degrades to <see cref="TypeDatabaseExtensions.AnyType"/>. This is an
/// intentional resolution, not a fallback — callers should not surface it as
/// a missing-type diagnostic.
/// </summary>
internal sealed class DynamicSelfStrategy : IResolutionStrategy
{
    public string Name => "DynamicSelf";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && named.IsDynamicSelf)
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
