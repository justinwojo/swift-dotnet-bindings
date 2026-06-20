// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Arms 2 + 3 of the raw-name resolution cascade as a resolver strategy: the
/// direct module / module-alias / <c>compileImportModule</c>-umbrella lookup
/// (<c>TryGetTypeRecordInternal</c>) followed by the <c>Foo</c>↔<c>FooRef</c>
/// C-interop suffix variant. The remaining cascade arms (4 out-of-module,
/// 5 cross-module alias, 6 Swift.Error) are owned by their own strategies,
/// registered immediately after this one.
/// </summary>
/// <remarks>
/// <para>F10 Stage 18: this strategy was split down from black-boxing the whole
/// arms-2–6 cascade (via <c>TryGetTypeRecordWithoutSupplement</c>) to arms 2+3
/// only, so the cascade has a single per-arm source of truth shared between the
/// <see cref="TypeResolver"/> chain and the raw-name
/// <see cref="TypeDatabase.TryGetTypeRecordWithoutSupplement(SwiftTypeName, out TypeRecord?)"/>
/// adapter. Calling the <c>TryGetTypeRecordInternal</c> primitive (never the
/// adapter) is what keeps that adapter recursion-free.</para>
/// <para>Finding 10: this resolves WITHOUT the Apple supplement. The supplement
/// is consulted by <see cref="AppleSupplementStrategy"/>, which the default
/// wiring orders ahead of this strategy, so any supplement-owned identity is
/// already claimed and never reaches here.</para>
/// <para>On a non-<see cref="TypeDatabase"/> <see cref="ITypeDatabase"/> (test
/// mocks) the concrete arm primitives are unavailable, so the strategy falls
/// back to the historical <c>TryGetTypeRecordWithoutSupplement</c> black box —
/// the exact call this strategy made for every database before the split. Mock
/// behavior is therefore unchanged, and the four cascade strategies that follow
/// all defer on a mock (their casts fail), so the mock sees one black-box lookup
/// just as it did pre-split.</para>
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

            if (context.Database is TypeDatabase db)
            {
                // Arm 2 — direct module / module-alias / umbrella lookup.
                if (db.TryGetTypeRecordInternal(typeName, out var record))
                {
                    result = Resolved(record);
                    return true;
                }

                // Arm 3 — C-interop aliases often use either Foo or FooRef across
                // sources. Try a suffix variant to avoid missing typedef-backed types.
                var refVariant = TypeDatabase.GetRefAliasVariant(typeName);
                if (refVariant != null && db.TryGetTypeRecordInternal(refVariant, out record))
                {
                    result = Resolved(record);
                    return true;
                }
            }
            else if (context.Database.TryGetTypeRecordWithoutSupplement(typeName, out var record))
            {
                // Mock / non-TypeDatabase ITypeDatabase: preserve the historical
                // black-box seam (default impl delegates to TryGetTypeRecord).
                result = Resolved(record);
                return true;
            }
        }

        result = null;
        return false;
    }

    private TypeResolutionResult Resolved(TypeRecord record)
        => new(Record: record, Provenance: new ResolutionProvenance($"strategy:{Name}"));
}
