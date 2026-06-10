// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Stores TypeWitness mappings from ABI JSON conformance entries.
/// Maps (conformingType, protocol, associatedTypeName) → resolved TypeSpec.
/// Used to resolve associated type references (e.g., Self.Element → Module.ConcreteType)
/// during protocol extension emission and bound generic translation.
/// </summary>
public class ConformanceGraph
{
    private readonly Dictionary<(string ConformingType, string Protocol, string AssociatedTypeName), TypeSpec> _witnesses = new();

    /// <summary>
    /// Records a TypeWitness mapping: conformingType conforming to protocol has
    /// associatedTypeName resolved to resolvedType.
    /// </summary>
    /// <param name="conformingType">Module-qualified conforming type name (e.g., "Module.ConcreteType").</param>
    /// <param name="protocol">Module-qualified protocol name (e.g., "Module.Protocol").</param>
    /// <param name="associatedTypeName">The associated type name (e.g., "Element").</param>
    /// <param name="resolvedType">The resolved TypeSpec for this associated type.</param>
    public void AddWitness(string conformingType, string protocol,
                           string associatedTypeName, TypeSpec resolvedType)
    {
        _witnesses[(conformingType, protocol, associatedTypeName)] = resolvedType;
    }

    /// <summary>
    /// Attempts to resolve an associated type for a specific conformance.
    /// </summary>
    /// <param name="conformingType">Module-qualified conforming type name.</param>
    /// <param name="protocol">Module-qualified protocol name.</param>
    /// <param name="associatedTypeName">The associated type name to resolve.</param>
    /// <param name="resolvedType">The resolved TypeSpec, if found.</param>
    /// <returns>True if a witness mapping exists for this combination.</returns>
    public bool TryResolve(string conformingType, string protocol,
                           string associatedTypeName, out TypeSpec? resolvedType)
    {
        if (_witnesses.TryGetValue((conformingType, protocol, associatedTypeName), out var result))
        {
            resolvedType = result;
            return true;
        }
        resolvedType = null;
        return false;
    }

    /// <summary>
    /// Enumerates every associated-type witness recorded for a specific conformance
    /// <c>(conformingType, protocol)</c>. Lets a consumer recover associated types that
    /// swiftc elided as redundant typealiases (e.g. a struct whose <c>Element</c> is
    /// inferred from a stored property emits no <c>TypeAlias</c> node, but the conformance
    /// still carries the <c>TypeWitness</c>).
    /// </summary>
    public IEnumerable<(string AssociatedTypeName, TypeSpec ResolvedType)> WitnessesFor(
        string conformingType, string protocol)
    {
        foreach (var kv in _witnesses)
        {
            if (string.Equals(kv.Key.ConformingType, conformingType, StringComparison.Ordinal) &&
                string.Equals(kv.Key.Protocol, protocol, StringComparison.Ordinal))
            {
                yield return (kv.Key.AssociatedTypeName, kv.Value);
            }
        }
    }

    /// <summary>
    /// The number of TypeWitness entries in the graph.
    /// </summary>
    public int Count => _witnesses.Count;
}
