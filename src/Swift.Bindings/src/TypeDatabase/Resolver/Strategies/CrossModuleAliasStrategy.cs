// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Arm 5 of the raw-name resolution cascade as a resolver strategy: resolves a
/// cross-module Swift <c>typealias</c> (e.g. <c>FamilyControls.ApplicationToken</c>
/// → <c>ManagedSettings.Token&lt;ManagedSettings.Application&gt;</c>) to the
/// canonical target's <see cref="TypeRecord"/>. A faithful mirror of the
/// corresponding arm in
/// <see cref="TypeDatabase.TryGetTypeRecordWithoutSupplement(SwiftTypeName, out TypeRecord?)"/>:
/// look the alias up by its module-qualified name, strip the generic arguments
/// from the canonical value (the <see cref="TypeRecord"/> is keyed on the base
/// type), then probe the arm-2 primitive on the canonical base.
/// </summary>
/// <remarks>
/// <para>F10 Stage 17: added shadowed (registered after
/// <see cref="DatabaseLookupStrategy"/>, which still black-boxes arms 2–6), so
/// it is dead in <see cref="TypeResolver.Default"/> until Stage 18.</para>
/// <para>Casts to the concrete <see cref="TypeDatabase"/> rather than widening
/// <see cref="ITypeDatabase"/>: both the alias map (<c>TryResolveTypeAlias</c>)
/// and the canonical-base probe (<c>TryGetTypeRecordInternal</c>) are concrete
/// implementation details. The generic strip uses the canonical VALUE only — the
/// input name is never re-parsed with generics, so <see cref="SwiftTypeName"/>
/// never sees a <c>&lt;</c> and cannot throw.</para>
/// </remarks>
internal sealed class CrossModuleAliasStrategy : IResolutionStrategy
{
    public string Name => "CrossModuleAlias";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && context.Database is TypeDatabase db)
        {
            var typeName = SwiftTypeName.FromTypeSpec(named);
            var canonicalName = db.TryResolveTypeAlias(typeName);
            if (canonicalName != null)
            {
                var baseName = canonicalName.IndexOf('<') is var idx and >= 0
                    ? canonicalName[..idx]
                    : canonicalName;
                var canonicalTypeName = SwiftTypeName.FromModuleQualifiedName(baseName);
                if (db.TryGetTypeRecordInternal(canonicalTypeName, out var record))
                {
                    result = new TypeResolutionResult(
                        Record: record,
                        Provenance: new ResolutionProvenance($"strategy:{Name}"));
                    return true;
                }
            }
        }

        result = null;
        return false;
    }
}
