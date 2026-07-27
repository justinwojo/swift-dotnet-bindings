// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace BindingsGeneration;

/// <summary>
/// Central seam for Swift-to-C# type resolution. Replaces the ad-hoc 9-stage
/// branching in <see cref="TypeDatabaseExtensions.TryGetTypeRecord(ITypeDatabase, NamedTypeSpec, out TypeRecord)"/>
/// (and its three sibling overloads) with an ordered list of
/// <see cref="IResolutionStrategy"/> plug-ins.
/// </summary>
/// <remarks>
/// <para>Every legacy resolution stage lives behind an
/// <see cref="IResolutionStrategy"/> plug-in, and the four duplicated
/// <see cref="TypeDatabaseExtensions"/> overloads are reduced to thin
/// projections over a single resolver call.</para>
/// <para>Dispatch order mirrors the legacy stage order so observable
/// resolution does not shift. Three intentional consolidations close drift
/// the legacy paths carried:</para>
/// <list type="number">
/// <item><see cref="BareGenericGuardStrategy"/> applies the bare-generic
/// short-circuit on every entry point (previously only in
/// <c>GetTypeRecordOrAnyType</c>).</item>
/// <item><see cref="SwiftAnyAnyObjectStrategy"/> classifies <c>Swift.Any</c> /
/// <c>Swift.AnyObject</c> as intentional resolutions on every entry point
/// (previously they reached <see cref="TypeDatabaseExtensions.AnyType"/> via
/// different code paths and showed up as "missing-from-database" fallback in
/// <c>TryGetAnyTypeFallbackInfo</c>).</item>
/// <item><c>IsTypeProcessed(NamedTypeSpec)</c> now projects from the same
/// resolver call as <c>TryGetTypeRecord(NamedTypeSpec)</c>. The legacy
/// implementation called <c>ITypeDatabase.IsTypeProcessed(SwiftTypeName)</c>
/// directly, which only walked module DB / module-alias / Apple-umbrella
/// paths and disagreed with <c>TryGetTypeRecord</c> on supplement-owned
/// identities (e.g., <c>Foundation.Locale.Language</c>),
/// <see cref="MetatypeStrategy">metatypes</see>, bare generics, and
/// <c>Swift.Any</c> / <c>Swift.AnyObject</c>. Under the single-path policy
/// the four entry-point overloads are projections of one resolver decision,
/// so a type the resolver claims is a type the rest of the generator can
/// marshal — and is therefore "processed".</item>
/// </list>
/// </remarks>
public sealed class TypeResolver
{
    private readonly ImmutableArray<IResolutionStrategy> _strategies;

    public TypeResolver(IEnumerable<IResolutionStrategy> strategies)
    {
        _strategies = strategies?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(strategies));
    }

    /// <summary>
    /// The raw-name database cascade as an ordered strategy list — arms 2+3
    /// (<see cref="DatabaseLookupStrategy"/>), arm 4
    /// (<see cref="OutOfModuleLookupStrategy"/>), arm 5
    /// (<see cref="CrossModuleAliasStrategy"/>), arm 6
    /// (<see cref="SwiftErrorStrategy"/>) — in the exact order the retired inline
    /// cascade ran them.
    /// </summary>
    /// <remarks>
    /// F10 Stage 18: this is the single source of truth for the database arms.
    /// It is spliced into <see cref="Default"/> (so <see cref="NamedTypeSpec"/>
    /// entry points get arms 2–6 after strategies 1–11) AND run standalone by
    /// <see cref="TypeDatabase.TryGetTypeRecordWithoutSupplement(SwiftTypeName, out TypeRecord?)"/>
    /// (so the ~85 raw-<see cref="SwiftTypeName"/> callers get arms 2–6 and
    /// NOTHING else — never strategies 1–11). One list, two surfaces, identical
    /// resolution. The strategies are stateless, so sharing the instances across
    /// both surfaces is safe. Declared before <see cref="Default"/> so it is
    /// initialized when Default's static initializer splices it in.
    /// </remarks>
    public static ImmutableArray<IResolutionStrategy> DatabaseCascade { get; } =
        ImmutableArray.Create<IResolutionStrategy>(
            new DatabaseLookupStrategy(),
            new OutOfModuleLookupStrategy(),
            new CrossModuleAliasStrategy(),
            new SwiftErrorStrategy());

    /// <summary>
    /// Default resolver wiring used by the <see cref="TypeDatabaseExtensions"/>
    /// entry points. Strategies dispatch in the listed order; the first match
    /// wins, mirroring the short-circuit of the retired legacy stage chain.
    /// </summary>
    public static TypeResolver Default { get; } = new(new IResolutionStrategy[]
    {
        new DynamicSelfStrategy(),
        new GenericParameterStrategy(),
        new PrimitiveAliasStrategy(),
        // Metatype must precede Existential. A metatype expressed in nested
        // NamedTypeSpec form (outer "Foundation" + InnerType chain ending in
        // "Type") leaves the outer name without a dot, which the legacy
        // existential heuristic would otherwise classify as a missing-binding
        // fallback. The legacy TryGetAnyTypeFallbackInfo path used the
        // metatype-first ordering for this reason; the resolver adopts it
        // uniformly so the classification is consistent across every entry
        // point.
        new MetatypeStrategy(),
        new ExistentialStrategy(),
        new SwiftAnyAnyObjectStrategy(),
        new PointerStrategy(),
        new UnsupportedAppleModuleStrategy(),
        new BareGenericGuardStrategy(),
        new BoundGenericSimdAliasStrategy(),
        new AppleSupplementStrategy(),
    }
        // F10 Stage 18: the database cascade (arms 2–6) splices in here, between
        // the Apple supplement and the ObjC bridge fallback — the same slot the
        // single black-box DatabaseLookupStrategy occupied before the split.
        .Concat(DatabaseCascade)
        .Append(new ObjCBridgingStrategy()));

    /// <summary>
    /// Strategies registered with this resolver, in dispatch order. Exposed
    /// for tests that exercise the dispatch contract.
    /// </summary>
    public ImmutableArray<IResolutionStrategy> Strategies => _strategies;

    /// <summary>
    /// Walk the registered strategies and return the first match. Returns
    /// false when no strategy claims the type — the legacy fall-through
    /// path remains responsible for those types until subsequent sessions
    /// migrate them.
    /// </summary>
    public bool TryResolve(
        TypeSpec typeSpec,
        ResolutionContext context,
        [NotNullWhen(true)] out TypeResolutionResult? result)
    {
        ArgumentNullException.ThrowIfNull(typeSpec);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var strategy in _strategies)
        {
            if (strategy.TryResolve(typeSpec, context, out result))
            {
                // One of the two places a resolved record surfaces. Recording here (rather than
                // in each strategy) is what keeps the emitted csproj's reference set derived
                // from the projections the emitted C# actually names — a strategy added later
                // cannot reintroduce a resolution path that resolves a type without also
                // declaring the package that supplies it.
                ResolvedReferenceRecorder.Record(result.Record, $"strategy:{strategy.Name}");
                return true;
            }
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Resolve or throw. Mirrors the legacy <c>GetTypeRecordOrThrow</c>
    /// shape so callers that previously hard-required a record can route
    /// through the resolver without restating the failure mode. A claimed
    /// result whose <see cref="TypeResolutionResult.Record"/> is null
    /// (i.e., a skip-style outcome) is also rejected — those callers want
    /// a real record, not a skip envelope.
    /// </summary>
    public TypeResolutionResult Resolve(TypeSpec typeSpec, ResolutionContext context)
    {
        if (TryResolve(typeSpec, context, out var result) && result.IsResolved)
            return result;

        throw new InvalidOperationException(
            $"No resolution strategy produced a TypeRecord for TypeSpec '{typeSpec}'.");
    }
}
