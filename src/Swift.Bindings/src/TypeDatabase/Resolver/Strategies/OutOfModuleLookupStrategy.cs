// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Arm 4 of the raw-name resolution cascade as a resolver strategy: probes the
/// out-of-module type cache (<c>_outOfModuleTypes</c>) for types registered
/// without a loaded module database. A faithful mirror of the corresponding
/// arm in <see cref="TypeDatabase.TryGetTypeRecordWithoutSupplement(SwiftTypeName, out TypeRecord?)"/>.
/// </summary>
/// <remarks>
/// <para>F10 Stage 17: added shadowed (registered after
/// <see cref="DatabaseLookupStrategy"/>, which still black-boxes arms 2–6),
/// so it is dead in <see cref="TypeResolver.Default"/> until Stage 18 splits
/// the database arm. It becomes the live source of arm 4 then.</para>
/// <para>The strategy keys on the concrete <see cref="TypeDatabase"/> via a cast
/// rather than a widened <see cref="ITypeDatabase"/> surface — the out-of-module
/// cache is an implementation detail with no place on the mock-facing interface.
/// On a non-<see cref="TypeDatabase"/> database (test mocks) the cast fails and
/// the strategy defers, exactly as the inline arm never ran for those.</para>
/// </remarks>
internal sealed class OutOfModuleLookupStrategy : IResolutionStrategy
{
    public string Name => "OutOfModuleLookup";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && context.Database is TypeDatabase db)
        {
            var typeName = SwiftTypeName.FromTypeSpec(named);
            if (db.TryGetOutOfModuleType(typeName, out var record))
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
