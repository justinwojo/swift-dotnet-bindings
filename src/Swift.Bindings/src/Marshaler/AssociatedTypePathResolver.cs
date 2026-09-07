// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Resolves a complete associated-member path from conformance facts. Each hop changes
/// the concrete owner; an unrelated leaf on the original owner is never a substitute.
/// </summary>
internal sealed class AssociatedTypePathResolver
{
    internal enum ResolutionKind { Resolved, Unknown, Ambiguous }
    internal readonly record struct Resolution(ResolutionKind Kind, string? TypeName = null);

    private readonly Dictionary<string, List<IReadOnlyDictionary<string, string>>> _facts = new(StringComparer.Ordinal);

    internal void AddFacts(string typeName, IReadOnlyDictionary<string, string> facts)
    {
        var key = ConcreteSpecializationEngine.NormalizeTypeForComparison(typeName);
        if (!_facts.TryGetValue(key, out var maps))
            _facts[key] = maps = new();
        maps.Add(facts);
    }

    internal static Resolution Resolve(ConcreteSpecializationEngine.ConcreteConformer conformer, string memberPath)
    {
        var segments = memberPath.Split('.');
        var maps = conformer.AssociatedTypes is { } root
            ? new[] { root }
            : Array.Empty<IReadOnlyDictionary<string, string>>();
        return ResolveMaps(maps, segments, 0, conformer.AssociatedTypeScope);
    }

    private static Resolution ResolveMaps(IEnumerable<IReadOnlyDictionary<string, string>> maps,
        string[] segments, int offset, AssociatedTypePathResolver? scope)
    {
        var remaining = string.Join(".", segments.Skip(offset));
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        bool ambiguous = false;
        foreach (var map in maps)
        {
            // Explicit full-path facts (including stdlib hint same-type proofs) retain
            // the entire path. They never authorize another path sharing its leaf.
            if (map.TryGetValue(remaining, out var exact) && !string.IsNullOrEmpty(exact))
                candidates.Add(ConcreteSpecializationEngine.NormalizeTypeForComparison(exact));
            if (offset + 1 < segments.Length && map.TryGetValue(segments[offset], out var child) &&
                scope is not null && scope._facts.TryGetValue(
                    ConcreteSpecializationEngine.NormalizeTypeForComparison(child), out var childMaps))
            {
                var result = ResolveMaps(childMaps, segments, offset + 1, scope);
                if (result.Kind == ResolutionKind.Resolved)
                    candidates.Add(result.TypeName!);
                else if (result.Kind == ResolutionKind.Ambiguous)
                    ambiguous = true;
            }
        }
        if (ambiguous || candidates.Count > 1)
            return new(ResolutionKind.Ambiguous);
        return candidates.Count == 1
            ? new(ResolutionKind.Resolved, candidates.Single())
            : new(ResolutionKind.Unknown);
    }

    // Missing single-hop facts keep the existing admission behavior. Newly resolved
    // chain uncertainty is compiler-catchable and belongs to verification/recovery.
    internal static bool DeferToCompiler(Resolution result, string memberPath) =>
        result.Kind != ResolutionKind.Resolved && memberPath.Contains('.');
}
