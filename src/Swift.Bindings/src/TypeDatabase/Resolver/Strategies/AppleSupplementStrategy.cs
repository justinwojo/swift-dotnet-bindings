// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Resolves identities owned by <c>SwiftBindings.Apple</c> to the synthetic
/// <see cref="TypeRecord"/> that points at the supplement's managed
/// projection. Runs before <see cref="DatabaseLookupStrategy"/> so a
/// supplement-owned identity (e.g. <c>Foundation.Locale.Language</c>,
/// <c>CryptoKit.P256.Signing.ECDSASignature</c>) wins over a parallel local
/// emission inside an Apple framework binding package.
/// </summary>
/// <remarks>
/// Records the identity via <see cref="AppleSupplementReferences.Record"/> so
/// the consumer's csproj emitter only adds the supplement
/// <c>PackageReference</c> when something is actually referenced. The strategy
/// also propagates the identity through
/// <see cref="TypeResolutionResult.SupplementReference"/> in case downstream
/// code wants to consume the signal without re-querying the static collector.
/// </remarks>
internal sealed class AppleSupplementStrategy : IResolutionStrategy
{
    public string Name => "AppleSupplement";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            var typeName = SwiftTypeName.FromTypeSpec(named);
            if (AppleSupplementResolver.TryResolve(typeName, context.CurrentlyGeneratingModule, out var record))
            {
                AppleSupplementReferences.Record(typeName.ModuleQualifiedName);
                result = new TypeResolutionResult(
                    Record: record,
                    SupplementReference: typeName.ModuleQualifiedName,
                    Provenance: new ResolutionProvenance($"strategy:{Name}"));
                return true;
            }
        }

        result = null;
        return false;
    }
}
