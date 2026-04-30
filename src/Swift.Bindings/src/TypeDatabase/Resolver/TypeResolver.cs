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
/// M4 Session 1 introduces the resolver alongside the legacy paths and
/// migrates the three simplest strategies (dynamic self, generic parameters,
/// primitive aliases). Subsequent M4 sessions migrate the remaining strategies
/// and delete the duplicated extension overloads.
/// </remarks>
public sealed class TypeResolver
{
    private readonly ImmutableArray<IResolutionStrategy> _strategies;

    public TypeResolver(IEnumerable<IResolutionStrategy> strategies)
    {
        _strategies = strategies?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(strategies));
    }

    /// <summary>
    /// Default resolver wiring used by the legacy entry points. Carries the
    /// three Session-1 strategies in dispatch order; later sessions extend
    /// the registration with the remaining strategies.
    /// </summary>
    public static TypeResolver Default { get; } = new(new IResolutionStrategy[]
    {
        new DynamicSelfStrategy(),
        new GenericParameterStrategy(),
        new PrimitiveAliasStrategy(),
    });

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
                return true;
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
            $"No resolution strategy produced a TypeRecord for TypeSpec '{typeSpec}'. The legacy " +
            "fall-through path should still cover this case until M4 Session 2 completes the migration.");
    }
}
