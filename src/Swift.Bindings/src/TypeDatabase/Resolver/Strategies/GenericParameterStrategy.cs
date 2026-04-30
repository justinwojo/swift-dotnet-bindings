// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves Swift generic-parameter references — the canonical ABI notation
/// (<c>τ_0_0</c>, <c>τ_1_0</c>, …) plus the short conventional names
/// (<c>T</c>, <c>U</c>, <c>V</c>, <c>W</c>, <c>E</c>, <c>K</c>, <c>R</c>,
/// <c>S</c>, and <c>T0</c>/<c>T1</c>/… patterns) accepted by
/// <see cref="TypeSpecHelpers.IsGenericTypeParameter(string)"/>. Longer
/// conventional names like <c>Element</c> or <c>Value</c> appear as their
/// canonical Swift form (<c>τ_0_0</c>) by the time they reach the
/// resolver, so they are matched there rather than by the short-name list.
/// Generic parameters have no concrete C# equivalent at binding time and
/// degrade to <see cref="TypeDatabaseExtensions.AnyType"/>; this is
/// intentional.
/// </summary>
internal sealed class GenericParameterStrategy : IResolutionStrategy
{
    public string Name => "GenericParameter";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && TypeSpecHelpers.IsGenericTypeParameter(named.Name))
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
