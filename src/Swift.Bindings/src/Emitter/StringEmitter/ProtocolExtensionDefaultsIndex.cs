// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Index of unconstrained protocol extension default implementations.
/// Used by ProtocolConformanceValidator to allow conformance when concrete types rely
/// on protocol extension defaults (e.g., Lottie's Interpolatable providing _interpolate default
/// that satisfies AnyInterpolatable's requirement).
///
/// Only unconstrained extensions (WhereConstraints.Count == 0) are indexed — constrained
/// extensions may not apply to all conforming types.
/// </summary>
public class ProtocolExtensionDefaultsIndex
{
    // Key: qualified protocol name (e.g., "Lottie.Interpolatable")
    // Value: set of method keys in PrintedName format (e.g., "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")
    private readonly Dictionary<string, HashSet<string>> _methodDefaults = new();

    // Key: qualified protocol name, Value: set of property names that have getter defaults
    private readonly Dictionary<string, HashSet<string>> _propertyDefaults = new();

    // Key: qualified protocol name, Value: set of property names that ALSO have setter defaults
    private readonly Dictionary<string, HashSet<string>> _propertySetterDefaults = new();

    // Key: qualified protocol name, Value: set of qualified parent protocol names (transitive)
    private readonly Dictionary<string, HashSet<string>> _inheritsFrom = new();

    public ProtocolExtensionDefaultsIndex(
        Dictionary<string, List<ProtocolExtensionMethodDecl>> extensionMethods,
        List<ProtocolDecl> protocols)
    {
        // Build inheritance graph from protocol declarations
        BuildInheritanceGraph(protocols);

        // Index unconstrained extension defaults
        foreach (var (qualifiedProtoName, methods) in extensionMethods)
        {
            foreach (var method in methods)
            {
                // Only index unconstrained extensions — constrained ones may not apply to all types
                if (method.WhereConstraints.Count > 0)
                    continue;

                if (method.IsProperty)
                {
                    if (!_propertyDefaults.TryGetValue(qualifiedProtoName, out var propSet))
                    {
                        propSet = new HashSet<string>();
                        _propertyDefaults[qualifiedProtoName] = propSet;
                    }
                    propSet.Add(method.MethodName);

                    if (method.HasSetter)
                    {
                        if (!_propertySetterDefaults.TryGetValue(qualifiedProtoName, out var setterSet))
                        {
                            setterSet = new HashSet<string>();
                            _propertySetterDefaults[qualifiedProtoName] = setterSet;
                        }
                        setterSet.Add(method.MethodName);
                    }
                }
                else
                {
                    if (!_methodDefaults.TryGetValue(qualifiedProtoName, out var methodSet))
                    {
                        methodSet = new HashSet<string>();
                        _methodDefaults[qualifiedProtoName] = methodSet;
                    }
                    methodSet.Add(method.PrintedName);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a method requirement has an extension default, either directly on the protocol
    /// or from a sub-protocol that inherits from it.
    /// </summary>
    /// <param name="qualifiedProtocolName">Module-qualified protocol name (e.g., "Lottie.AnyInterpolatable")</param>
    /// <param name="methodKey">Method key in PrintedName format (e.g., "_interpolate(to:amount:spatialOutTangent:spatialInTangent:)")</param>
    public bool HasMethodDefault(string qualifiedProtocolName, string methodKey)
    {
        // Direct: protocol itself has the default
        if (_methodDefaults.TryGetValue(qualifiedProtocolName, out var directSet) &&
            directSet.Contains(methodKey))
            return true;

        // Sub-protocol: a child protocol provides the default for a parent's requirement
        foreach (var (providerProto, methodSet) in _methodDefaults)
        {
            if (providerProto == qualifiedProtocolName)
                continue;
            if (methodSet.Contains(methodKey) && InheritsFrom(providerProto, qualifiedProtocolName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a method requirement has an extension default, considering the concrete type's
    /// actual conformances. The sub-protocol providing the default must be one that the concrete
    /// type actually conforms to.
    /// </summary>
    public bool HasMethodDefault(string qualifiedProtocolName, string methodKey, HashSet<string> concreteConformances)
    {
        // Direct: protocol itself has the default
        if (_methodDefaults.TryGetValue(qualifiedProtocolName, out var directSet) &&
            directSet.Contains(methodKey))
            return true;

        // Sub-protocol: provider must be in concreteConformances
        foreach (var (providerProto, methodSet) in _methodDefaults)
        {
            if (providerProto == qualifiedProtocolName)
                continue;
            if (methodSet.Contains(methodKey) &&
                concreteConformances.Contains(providerProto) &&
                InheritsFrom(providerProto, qualifiedProtocolName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a property requirement has an extension default, considering the concrete type's
    /// actual conformances and accessor contract (getter/setter).
    /// </summary>
    /// <param name="requiresSetter">Whether the protocol requirement includes a setter.</param>
    public bool HasPropertyDefault(string qualifiedProtocolName, string propertyName,
        HashSet<string> concreteConformances, bool requiresSetter = false)
    {
        // Direct: protocol itself has the default
        if (_propertyDefaults.TryGetValue(qualifiedProtocolName, out var directSet) &&
            directSet.Contains(propertyName))
        {
            if (requiresSetter && !HasSetterDefault(qualifiedProtocolName, propertyName))
                return false;
            return true;
        }

        // Sub-protocol: provider must be in concreteConformances
        foreach (var (providerProto, propSet) in _propertyDefaults)
        {
            if (providerProto == qualifiedProtocolName)
                continue;
            if (propSet.Contains(propertyName) &&
                concreteConformances.Contains(providerProto) &&
                InheritsFrom(providerProto, qualifiedProtocolName))
            {
                if (requiresSetter && !HasSetterDefault(providerProto, propertyName))
                    continue;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a property extension default includes a setter.
    /// </summary>
    private bool HasSetterDefault(string qualifiedProtocolName, string propertyName)
    {
        return _propertySetterDefaults.TryGetValue(qualifiedProtocolName, out var setterSet) &&
               setterSet.Contains(propertyName);
    }

    /// <summary>
    /// Checks if a method requirement has a DIRECT extension default on this protocol only.
    /// Does NOT check sub-protocols. Used by ProtocolHandler for DIM emission — a sub-protocol
    /// default should not turn a parent protocol's requirement into a DIM for all implementers.
    /// </summary>
    public bool HasDirectMethodDefault(string qualifiedProtocolName, string methodKey)
    {
        return _methodDefaults.TryGetValue(qualifiedProtocolName, out var directSet) &&
               directSet.Contains(methodKey);
    }

    /// <summary>
    /// Checks if a property requirement has an extension default on this protocol or a sub-protocol.
    /// Used by ProtocolHandler for DIM emission — both direct and inherited defaults become DIMs.
    /// </summary>
    /// <param name="requiresSetter">When true, the default must include a setter to match.</param>
    public bool HasPropertyDefault(string qualifiedProtocolName, string propertyName,
        bool requiresSetter = false)
    {
        // Direct: protocol itself has the default
        if (_propertyDefaults.TryGetValue(qualifiedProtocolName, out var directSet) &&
            directSet.Contains(propertyName))
        {
            if (requiresSetter && !HasSetterDefault(qualifiedProtocolName, propertyName))
                return false;
            return true;
        }

        // Sub-protocol: a child protocol provides the default for a parent's requirement
        foreach (var (providerProto, propSet) in _propertyDefaults)
        {
            if (providerProto == qualifiedProtocolName)
                continue;
            if (propSet.Contains(propertyName) && InheritsFrom(providerProto, qualifiedProtocolName))
            {
                if (requiresSetter && !HasSetterDefault(providerProto, propertyName))
                    continue;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a property requirement has a DIRECT extension default on this protocol only.
    /// Does NOT check sub-protocols.
    /// </summary>
    /// <param name="requiresSetter">When true, the default must include a setter to match.</param>
    public bool HasDirectPropertyDefault(string qualifiedProtocolName, string propertyName,
        bool requiresSetter = false)
    {
        if (!_propertyDefaults.TryGetValue(qualifiedProtocolName, out var directSet) ||
            !directSet.Contains(propertyName))
            return false;

        if (requiresSetter && !HasSetterDefault(qualifiedProtocolName, propertyName))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if protocol Q inherits from protocol P (transitively).
    /// </summary>
    private bool InheritsFrom(string childProto, string parentProto)
    {
        return _inheritsFrom.TryGetValue(childProto, out var ancestors) &&
               ancestors.Contains(parentProto);
    }

    /// <summary>
    /// Builds the transitive inheritance graph from protocol declarations.
    /// </summary>
    private void BuildInheritanceGraph(List<ProtocolDecl> protocols)
    {
        // Build direct parent map first
        var directParents = new Dictionary<string, HashSet<string>>();
        foreach (var protocol in protocols)
        {
            var qualifiedName = protocol.SwiftTypeName?.ModuleQualifiedName
                              ?? $"{protocol.ModuleDecl?.Name ?? "Unknown"}.{protocol.Name}";
            var parents = new HashSet<string>();
            foreach (var inherited in protocol.InheritedProtocols)
            {
                parents.Add(inherited.Name); // NamedTypeSpec.Name is already module-qualified
            }
            if (parents.Count > 0)
                directParents[qualifiedName] = parents;
        }

        // Compute transitive closure
        foreach (var (proto, parents) in directParents)
        {
            var allAncestors = new HashSet<string>();
            var queue = new Queue<string>(parents);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!allAncestors.Add(current))
                    continue;
                if (directParents.TryGetValue(current, out var grandparents))
                {
                    foreach (var gp in grandparents)
                        queue.Enqueue(gp);
                }
            }
            _inheritsFrom[proto] = allAncestors;
        }
    }
}
