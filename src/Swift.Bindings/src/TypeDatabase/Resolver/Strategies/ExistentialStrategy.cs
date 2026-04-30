// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves existential <c>any X</c> identities (e.g. <c>any Swift.Encoder</c>),
/// bare <c>any</c> placeholders, and unqualified type names that reach the
/// resolver as parsing artifacts. Existentials degrade to
/// <see cref="TypeDatabaseExtensions.AnyType"/>; the resolver also flags this as
/// a synthetic fallback so <c>TryGetAnyTypeFallbackInfo</c> can surface the
/// degradation as a missing-binding diagnostic. <c>Swift.Any</c> and
/// <c>Swift.AnyObject</c> are deliberately excluded — those are handled by
/// <see cref="SwiftAnyAnyObjectStrategy"/>.
/// </summary>
internal sealed class ExistentialStrategy : IResolutionStrategy
{
    public string Name => "Existential";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && TypeDatabaseExtensions.IsExistentialTypeName(named))
        {
            result = new TypeResolutionResult(
                Record: TypeDatabaseExtensions.AnyType,
                SyntheticFallback: new TypeDatabaseExtensions.AnyTypeFallbackInfo(
                    "Existential type fallback",
                    typeSpec.ToString()),
                Provenance: new ResolutionProvenance($"strategy:{Name}"));
            return true;
        }

        result = null;
        return false;
    }
}
