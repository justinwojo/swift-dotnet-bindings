// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Index of protocol extension default implementations (constrained and unconstrained).
/// Used by ProtocolConformanceValidator to allow conformance when concrete types rely
/// on protocol extension defaults (e.g., Lottie's Interpolatable providing _interpolate default
/// that satisfies AnyInterpolatable's requirement, or GRDB's `extension DatabaseValueConvertible
/// where Self: RawRepresentable, Self.RawValue: DatabaseValueConvertible` providing
/// databaseValue/fromDatabaseValue for inline-conforming RawRepresentable wrappers).
///
/// Constrained extensions are indexed without evaluating the where-clause: the index is
/// consulted only for types Swift has already deemed conformers (the witness-table
/// dictionary entry exists), so the constraints are guaranteed satisfied. DIM bodies emitted
/// for any matched default just throw NotSupportedException pointing users to the concrete
/// type, so an over-broad index match is a safe fallback — not a false claim of dispatch.
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

        // Index BOTH unconstrained and constrained extension defaults.
        // Rationale: this index feeds CanFullyImplementProtocol, which decides whether a
        // concrete C# type can claim a Swift protocol conformance. When the conformance is
        // satisfied through a constrained extension (e.g.
        //   extension P where Self: Q, Self.RawValue: R { default impl }
        // ), the Swift runtime supplies the witness for any concrete type that meets the
        // where-clause — so the corresponding C# requirement must be marked as covered by
        // a default. The generated C# emits the requirement as a DIM that throws
        // NotSupportedException at the interface level, with the concrete type relying on
        // the Swift-side default. Skipping constrained extensions here makes the validator
        // reject otherwise-valid conformances (e.g. GRDB IndexInfo.Origin via
        // DatabaseValueConvertible's RawRepresentable extension).
        foreach (var (qualifiedProtoName, methods) in extensionMethods)
        {
            foreach (var method in methods)
            {
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
    /// Detects "phantom defaults" — required protocol members that no conforming type in the module
    /// can implement in C#. A member is a phantom default when either:
    /// (a) No conforming type has the member at all (satisfied by an invisible PAT extension), or
    /// (b) All conforming types have the member but none can emit it (e.g., AnyType fallback).
    ///
    /// For example, Lottie's AnyValueProvider requires typeErasedStorage, but FloatValueProvider
    /// doesn't have it in its ABI JSON — it's provided by a constrained extension on the
    /// ValueProvider PAT. Since PAT extensions are filtered during index construction, these
    /// defaults are invisible.
    ///
    /// By scanning all conforming types, we infer that a member with no usable implementations
    /// must have an invisible default. We add these as "phantom defaults" so the ProtocolHandler
    /// emits them as DIMs and the ProtocolConformanceValidator accepts the conformance.
    /// </summary>
    /// <param name="moduleDecl">The module declaration containing all types and protocols.</param>
    /// <param name="typeDatabase">Optional type database for checking property emittability.
    /// When null, only checks for missing members (not unemittable ones).</param>
    public void DetectPhantomDefaults(ModuleDecl moduleDecl, ITypeDatabase? typeDatabase = null)
    {
        foreach (var protocolDecl in moduleDecl.Protocols)
        {
            var protoQualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                                   ?? $"{moduleDecl.Name}.{protocolDecl.Name}";

            // Get all types in the module that conform to this protocol
            var conformingTypes = new List<TypeDecl>();
            foreach (var type in moduleDecl.Types)
            {
                IEnumerable<TypeConformance> conformances = type switch
                {
                    ClassDecl cd => cd.Conformances,
                    StructDecl sd => sd.Conformances,
                    EnumDecl ed => ed.Conformances,
                    _ => Enumerable.Empty<TypeConformance>()
                };
                if (conformances.Any(c => c.Protocol.Name == protocolDecl.Name ||
                    c.Protocol.ModuleQualifiedName == protoQualifiedName))
                {
                    conformingTypes.Add(type);
                }
            }

            // No conforming types → nothing to infer
            if (conformingTypes.Count == 0)
                continue;

            // Check each required non-static property
            foreach (var property in protocolDecl.Properties)
            {
                if (property.IsStatic) continue;

                // Skip if already known as an extension default
                if (_propertyDefaults.TryGetValue(protoQualifiedName, out var existingProps) &&
                    existingProps.Contains(property.Name))
                    continue;

                // Check if ANY conforming type has this property AND can emit it
                bool anyTypeCanEmitProperty = false;
                foreach (var type in conformingTypes)
                {
                    var properties = type switch
                    {
                        ClassDecl cd => cd.Properties,
                        StructDecl sd => sd.Properties,
                        EnumDecl ed => ed.Properties,
                        _ => Enumerable.Empty<PropertyDecl>()
                    };
                    var matchingProp = properties.FirstOrDefault(p => p.Name == property.Name && !p.IsStatic);
                    if (matchingProp == null) continue;

                    // If we have a type database, also check emittability
                    if (typeDatabase != null)
                    {
                        var skipReason = MemberEmissionValidator.CanEmitProperty(
                            matchingProp, typeDatabase, out _, out _);
                        if (skipReason != null) continue; // Property exists but can't be emitted
                    }

                    anyTypeCanEmitProperty = true;
                    break;
                }

                if (!anyTypeCanEmitProperty)
                {
                    // Phantom default: no conforming type can provide this property in C#
                    if (!_propertyDefaults.TryGetValue(protoQualifiedName, out var propSet))
                    {
                        propSet = new HashSet<string>();
                        _propertyDefaults[protoQualifiedName] = propSet;
                    }
                    propSet.Add(property.Name);

                    // If the protocol property has a setter, also add to setter defaults
                    bool hasSetter = property.Accessors.OfType<SetAccessorDecl>().Any();
                    if (hasSetter)
                    {
                        if (!_propertySetterDefaults.TryGetValue(protoQualifiedName, out var setterSet))
                        {
                            setterSet = new HashSet<string>();
                            _propertySetterDefaults[protoQualifiedName] = setterSet;
                        }
                        setterSet.Add(property.Name);
                    }
                }
            }

            // Check each required non-static, non-constructor method
            foreach (var method in protocolDecl.Methods)
            {
                if (method.IsConstructor || method.MethodType == MethodType.Static)
                    continue;

                var methodKey = BuildMethodKey(method);

                // Skip if already known as an extension default
                if (_methodDefaults.TryGetValue(protoQualifiedName, out var existingMethods) &&
                    existingMethods.Contains(methodKey))
                    continue;

                // Check if ANY conforming type has this method
                bool anyTypeHasMethod = false;
                foreach (var type in conformingTypes)
                {
                    var methods = type switch
                    {
                        ClassDecl cd => cd.Methods,
                        StructDecl sd => sd.Methods,
                        EnumDecl ed => ed.Methods,
                        _ => Enumerable.Empty<MethodDecl>()
                    };
                    if (methods.Any(m => m.Name == method.Name && !m.IsConstructor &&
                        m.MethodType != MethodType.Static))
                    {
                        anyTypeHasMethod = true;
                        break;
                    }
                }

                if (!anyTypeHasMethod)
                {
                    // Phantom default: no conforming type has this method
                    if (!_methodDefaults.TryGetValue(protoQualifiedName, out var methodSet))
                    {
                        methodSet = new HashSet<string>();
                        _methodDefaults[protoQualifiedName] = methodSet;
                    }
                    methodSet.Add(methodKey);
                }
            }
        }
    }

    /// <summary>
    /// Builds a method key from a MethodDecl for phantom default detection.
    /// Matches the format used by ProtocolExtensionEmitter.BuildMethodKey().
    /// </summary>
    private static string BuildMethodKey(MethodDecl method)
    {
        if (method.CSSignature.Count <= 1)
            return $"{method.Name}()";

        var labels = string.Join("", method.CSSignature.Skip(1).Select(arg =>
        {
            var label = string.IsNullOrEmpty(arg.Name) || arg.Name == "_" ? "_" : arg.Name;
            return $"{label}:";
        }));
        return $"{method.Name}({labels})";
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
            var protoModule = protocol.ModuleDecl?.Name;
            foreach (var inherited in protocol.InheritedProtocols)
            {
                // Must match ProtocolHandler.GetInheritedInterfaceList filters:

                // Skip AnyObject — class-bound constraint, not a real interface
                if (inherited.Name is "Swift.AnyObject" or "AnyObject")
                    continue;

                // Skip marker protocols that have no C# representation
                if (inherited.NameWithoutModule is "Sendable" or "Escapable" or "Copyable" or "SendableMetatype")
                    continue;

                // Skip cross-module protocols
                var inheritedModule = inherited.Module;
                if (!string.IsNullOrEmpty(inheritedModule) && !string.IsNullOrEmpty(protoModule) &&
                    inheritedModule != protoModule)
                    continue;

                // Use module-qualified name when available for consistent graph lookups
                var inheritedQualifiedName = inherited.HasModule()
                    ? inherited.Name
                    : $"{protocol.ModuleDecl?.Name ?? "Unknown"}.{inherited.Name}";
                parents.Add(inheritedQualifiedName);
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
