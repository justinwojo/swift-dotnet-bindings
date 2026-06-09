// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Guards against known stdlib generic identities (<c>Swift.Dictionary</c>,
/// <c>Swift.Array</c>, <c>Swift.Set</c>, <c>Swift.Optional</c>,
/// <c>Swift.Result</c>) reaching the resolver without their generic
/// arguments populated. Without this short-circuit, a downstream emitter
/// would emit a bare <c>SwiftDictionary</c> / <c>SwiftArray</c> reference
/// (CS0305) — degrading to <see cref="TypeDatabaseExtensions.AnyType"/>
/// triggers the standard skip path.
/// </summary>
/// <remarks>
/// The legacy <c>GetTypeRecordOrAnyType</c> branch previously carried
/// this guard while the sibling <c>TryGetTypeRecord</c> /
/// <c>GetTypeRecordOrThrow</c> overloads silently returned the bare
/// <see cref="TypeRecord"/> registered in the database. Folding the guard
/// into the unified resolver chain applies it on every path. Bare references
/// always produce broken downstream emission so the divergence was a latent
/// bug, not an intentional carve-out.
/// </remarks>
internal sealed class BareGenericGuardStrategy : IResolutionStrategy
{
    public string Name => "BareGenericGuard";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named
            && !named.ContainsGenericParameters
            && TypeDatabaseExtensions.IsKnownGenericType(named.Name))
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
