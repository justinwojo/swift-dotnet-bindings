// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Raises the availability of every member whose signature names a parameterized protocol
/// existential (<c>any Requestable&lt;Value, Failure&gt;</c>) to the OS releases that ship
/// runtime support for those types.
///
/// <para>A library can traffic in such a type at an older deployment target because static uses
/// need no metadata. The binding cannot: its wrapper moves the value through raw memory
/// (<c>assumingMemoryBound(to:)</c>, <c>initializeMemory(as:)</c>, <c>load(as:)</c>), and every one
/// of those instantiates the existential's metadata, which Swift rejects below iOS 16 / macOS 13 /
/// tvOS 16 / watchOS 9. Recording the floor on the member itself lets every consumer of member
/// availability agree on it: the <c>@_cdecl</c> wrapper's <c>@available</c>, the C#
/// <c>[SupportedOSPlatform]</c> a consumer sees, and the runtime guard for a caller who suppresses
/// CA1416. The entries are flagged <see cref="AvailabilityAnnotation.IsRuntimeSupportFloor"/> so a
/// protocol witness, which Swift requires to be as available as its requirement, guards its body
/// instead of declaring the floor.</para>
/// </summary>
public static class ParameterizedExistentialFloor
{
    internal static readonly IReadOnlyList<(string Platform, string Version)> RuntimeSupportFloors =
    [
        ("iOS", "16.0"),
        ("macOS", "13.0"),
        ("tvOS", "16.0"),
        ("watchOS", "9.0"),
    ];

    /// <summary>
    /// Applies the floor across the module's types, top-level members, and the dependency
    /// protocols it conforms to. Returns the number of members that were raised.
    /// </summary>
    public static int Apply(ModuleDecl module, ITypeDatabase typeDatabase)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(typeDatabase);

        var visited = new HashSet<BaseDecl>(ReferenceEqualityComparer.Instance);
        int raised = 0;

        foreach (var method in module.Methods)
            raised += ApplyToMethod(method, typeDatabase, visited);
        foreach (var property in module.Properties)
            raised += ApplyToProperty(property, typeDatabase, visited);
        foreach (var type in module.Types)
            raised += ApplyToType(type, typeDatabase, visited);
        foreach (var protocol in module.Protocols)
            raised += ApplyToType(protocol, typeDatabase, visited);
        foreach (var protocols in module.DependencyProtocols.Values)
            foreach (var protocol in protocols)
                raised += ApplyToType(protocol, typeDatabase, visited);

        return raised;
    }

    /// <summary>
    /// True when <paramref name="typeSpec"/> names a parameterized protocol existential anywhere
    /// in its structure — directly, as a generic argument, a tuple element, or a closure
    /// parameter or result.
    /// </summary>
    public static bool ContainsParameterizedExistential(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        switch (typeSpec)
        {
            case NamedTypeSpec named:
                if (named.GenericParameters.Count > 0 && NamesProtocol(named, typeDatabase))
                    return true;
                // A member-type chain (`Outer<A>.Inner<B>`) carries each segment's own arguments;
                // the segments themselves name the enclosing types, not a protocol.
                for (var segment = named; segment is not null; segment = segment.InnerType)
                {
                    foreach (var argument in segment.GenericParameters)
                        if (ContainsParameterizedExistential(argument, typeDatabase))
                            return true;
                }
                return false;
            case TupleTypeSpec tuple:
                foreach (var element in tuple.Elements)
                    if (ContainsParameterizedExistential(element, typeDatabase))
                        return true;
                return false;
            case ClosureTypeSpec closure:
                return ContainsParameterizedExistential(closure.Arguments, typeDatabase)
                    || ContainsParameterizedExistential(closure.ReturnType, typeDatabase);
            case ProtocolListTypeSpec protocolList:
                // An opaque `some P<A>` result is never spelled as a value type by the wrapper.
                if (protocolList.IsOpaque)
                    return false;
                foreach (var protocol in protocolList.Protocols.Keys)
                    if (ContainsParameterizedExistential(protocol, typeDatabase))
                        return true;
                return false;
            default:
                return false;
        }
    }

    private static bool NamesProtocol(NamedTypeSpec named, ITypeDatabase typeDatabase)
    {
        // `any X<…>` is only well-formed for a protocol with primary associated types.
        if (named.IsAny && named.InnerType is null)
            return true;
        if (TypeSpecHelpers.IsGenericTypeParameter(named.Name))
            return false;
        try
        {
            return typeDatabase.TryGetTypeRecord(SwiftTypeName.FromTypeSpec(named), out var record)
                && record.Kind == TypeRecordKind.Protocol;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int ApplyToType(TypeDecl type, ITypeDatabase typeDatabase, HashSet<BaseDecl> visited)
    {
        if (!visited.Add(type))
            return 0;

        int raised = 0;
        foreach (var method in type.Methods)
            raised += ApplyToMethod(method, typeDatabase, visited);
        foreach (var property in type.Properties)
            raised += ApplyToProperty(property, typeDatabase, visited);
        foreach (var subscript in type.Subscripts)
            raised += ApplyToSubscript(subscript, typeDatabase, visited);
        foreach (var nested in type.Types)
            raised += ApplyToType(nested, typeDatabase, visited);
        return raised;
    }

    private static int ApplyToMethod(MethodDecl method, ITypeDatabase typeDatabase, HashSet<BaseDecl> visited)
    {
        if (!visited.Add(method))
            return 0;
        foreach (var argument in method.CSSignature)
        {
            if (ContainsParameterizedExistential(argument.SwiftTypeSpec, typeDatabase))
                return Raise(method) ? 1 : 0;
        }
        return 0;
    }

    private static int ApplyToProperty(PropertyDecl property, ITypeDatabase typeDatabase, HashSet<BaseDecl> visited)
    {
        if (!visited.Add(property) || !ContainsParameterizedExistential(property.SwiftTypeSpec, typeDatabase))
            return 0;

        bool raised = Raise(property);
        // A setter introduced after its property carries its own list, which replaces the
        // property's rather than adding to it, so it needs the floor too.
        if (property.SetterAvailabilityAnnotations is { Count: > 0 } setter)
        {
            var raisedSetter = WithFloor(setter, property.ParentDecl);
            if (raisedSetter is not null)
                property.SetterAvailabilityAnnotations = raisedSetter;
        }
        foreach (var accessor in property.Accessors)
            Raise(accessor.Method);
        return raised ? 1 : 0;
    }

    private static int ApplyToSubscript(SubscriptDecl subscript, ITypeDatabase typeDatabase, HashSet<BaseDecl> visited)
    {
        if (!visited.Add(subscript))
            return 0;
        bool names = ContainsParameterizedExistential(subscript.ReturnTypeSpec, typeDatabase)
            || subscript.IndexParameters.Any(p => ContainsParameterizedExistential(p.SwiftTypeSpec, typeDatabase));
        if (!names)
            return 0;

        bool raised = Raise(subscript);
        foreach (var accessor in subscript.Accessors)
            Raise(accessor.Method);
        return raised ? 1 : 0;
    }

    private static bool Raise(BaseDecl decl)
    {
        var raised = WithFloor(decl.AvailabilityAnnotations, decl.ParentDecl);
        if (raised is null)
            return false;
        decl.AvailabilityAnnotations = raised;
        return true;
    }

    /// <summary>
    /// <paramref name="own"/> plus a runtime-support entry for each platform whose effective
    /// floor — the member's own merged with its enclosing types' — sits below the runtime's.
    /// Null when nothing needs adding. A platform the member is unavailable on is left alone:
    /// an introduced version there would contradict the declaration.
    /// </summary>
    private static List<AvailabilityAnnotation>? WithFloor(
        IReadOnlyList<AvailabilityAnnotation>? own, BaseDecl? parent)
    {
        var effective = AvailabilityHelpers.MergeAvailabilityFromAncestors(own, parent);
        List<AvailabilityAnnotation>? result = null;

        foreach (var (platform, version) in RuntimeSupportFloors)
        {
            bool covered = false;
            if (effective is not null)
            {
                foreach (var annotation in effective)
                {
                    if (!string.Equals(annotation.Platform, platform, StringComparison.Ordinal))
                        continue;
                    if (annotation.IsUnconditionallyUnavailable
                        || (annotation.IntroducedVersion is { } introduced
                            && AvailabilityHelpers.CompareOsVersions(introduced, version) >= 0))
                    {
                        covered = true;
                        break;
                    }
                }
            }
            if (covered)
                continue;

            result ??= own is null ? new List<AvailabilityAnnotation>() : new List<AvailabilityAnnotation>(own);
            result.Add(new AvailabilityAnnotation(platform, version, null, null, false, false, null, null)
            {
                IsRuntimeSupportFloor = true,
            });
        }

        return result;
    }
}
