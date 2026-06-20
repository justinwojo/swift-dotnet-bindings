// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Arm 6 of the raw-name resolution cascade as a resolver strategy: maps the
/// well-known stdlib protocol <c>Swift.Error</c> to <c>AnyError</c>, the
/// hand-rolled projection in <c>SwiftBindings.Apple</c>. A faithful mirror of
/// the corresponding arm in
/// <see cref="TypeDatabase.TryGetTypeRecordWithoutSupplement(SwiftTypeName, out TypeRecord?)"/>.
/// </summary>
/// <remarks>
/// <para>F10 Stage 17: added shadowed (registered after
/// <see cref="DatabaseLookupStrategy"/>, which still black-boxes arms 2–6), so
/// it is dead in <see cref="TypeResolver.Default"/> until Stage 18.</para>
/// <para>Records the <c>Foundation.AnyError</c> supplement reference under the
/// <em>exact</em> hint string the inline arm used
/// (<c>"TypeDatabase.TryGetTypeRecord:SwiftError"</c>) so the aggregated
/// <see cref="AppleSupplementReferences"/> snapshot — consumed by binding
/// reports and the consumer-csproj PackageReference emitter — is byte-identical
/// after the Stage 18 collapse moves this side effect off the inline path.</para>
/// <para>Gated on the database being a concrete <see cref="TypeDatabase"/>: the
/// inline arm only ran inside the real database's cascade, never on a mock
/// (whose <c>TryGetTypeRecordWithoutSupplement</c> defaults to its own record
/// dictionary, with no <c>Swift.Error</c> special case). Deferring on a mock
/// preserves that.</para>
/// </remarks>
internal sealed class SwiftErrorStrategy : IResolutionStrategy
{
    public string Name => "SwiftError";

    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        if (typeSpec is NamedTypeSpec named && context.Database is TypeDatabase)
        {
            var typeName = SwiftTypeName.FromTypeSpec(named);
            if (typeName.ModuleQualifiedName == "Swift.Error")
            {
                AppleSupplementReferences.Record("Foundation.AnyError", "TypeDatabase.TryGetTypeRecord:SwiftError");
                result = new TypeResolutionResult(
                    Record: TypeDatabaseExtensions.SwiftErrorType,
                    Provenance: new ResolutionProvenance($"strategy:{Name}"));
                return true;
            }
        }

        result = null;
        return false;
    }
}
