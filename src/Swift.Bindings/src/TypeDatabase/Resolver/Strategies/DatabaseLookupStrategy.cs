// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Direct <see cref="ITypeDatabase.TryGetTypeRecordWithoutSupplement(SwiftTypeName, out TypeRecord?)"/>
/// lookup keyed on the module-qualified name derived from the
/// <see cref="NamedTypeSpec"/>. The inner database method handles its own
/// chain of fallbacks (module aliases, <c>compileImportModule</c> umbrellas,
/// <c>Ref</c> suffix variants, out-of-module cache, cross-module type aliases,
/// <c>Swift.Error</c>) — those remain centralized there so non-resolver
/// callers that key on raw <see cref="SwiftTypeName"/> still benefit. This
/// strategy only ferries the lookup result into a
/// <see cref="TypeResolutionResult"/>.
/// </summary>
/// <remarks>
/// Finding 10: this calls the WITHOUT-supplement variant deliberately. The Apple supplement is
/// already consulted by <see cref="AppleSupplementStrategy"/>, which the default resolver wiring
/// orders ahead of this strategy; routing through the full <c>TryGetTypeRecord</c> here would
/// consult the supplement a second time at a lower precedence (the retired double-consult). Any
/// supplement-owned identity is claimed by the earlier strategy and never reaches this one, so
/// omitting the supplement arm is behavior-preserving.
/// </remarks>
internal sealed class DatabaseLookupStrategy : IResolutionStrategy
{
    public string Name => "DatabaseLookup";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named)
        {
            var typeName = SwiftTypeName.FromTypeSpec(named);
            if (context.Database.TryGetTypeRecordWithoutSupplement(typeName, out var record))
            {
                result = new TypeResolutionResult(
                    Record: record,
                    Provenance: new ResolutionProvenance($"strategy:{Name}"));
                return true;
            }
        }

        result = null;
        return false;
    }
}
