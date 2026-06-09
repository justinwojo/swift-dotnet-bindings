// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves the special protocol identities <c>Swift.Any</c> and
/// <c>Swift.AnyObject</c>. They are module-qualified (so
/// <see cref="ExistentialStrategy"/> deliberately skips them) and have no
/// concrete C# equivalent — both degrade to
/// <see cref="TypeDatabaseExtensions.AnyType"/> as an intentional resolution.
/// </summary>
/// <remarks>
/// The legacy <c>TryGetTypeRecord</c> branch previously handled these
/// inline while <c>GetTypeRecordOrAnyType</c> reached the same answer through
/// the catch-all final fallback. The unified strategy collapses that drift
/// into one explicit, intentional resolution surface so
/// <see cref="TypeDatabaseExtensions.TryGetAnyTypeFallbackInfo"/> stops
/// labelling these as "missing from the type database".
/// </remarks>
internal sealed class SwiftAnyAnyObjectStrategy : IResolutionStrategy
{
    public string Name => "SwiftAnyAnyObject";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && (named.Name == "Swift.Any" || named.Name == "Swift.AnyObject"))
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
