// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Walks a method, property accessor, or subscript signature and decides whether any
/// reachable <see cref="TypeSpec"/> resolves to a name in the module's
/// <c>InternalTypeNames</c> set. Used by <see cref="MemberValidationPipeline"/> to
/// suppress emission for members whose Swift wrapper would have to mention an
/// <c>@usableFromInline internal</c> (or otherwise-suppressed) type — Swift refuses to
/// compile such wrappers, and the post-processor would strip them anyway.
///
/// Matching semantics:
///   • If a <see cref="NamedTypeSpec"/> is module-qualified (e.g. <c>Foo.Bar</c>),
///     the qualified form is checked first against <c>InternalTypeNames</c>. Only that
///     exact qualified form counts as a hit. Cross-module short-name collisions are
///     never matched — a public type in module B whose short name happens to equal an
///     internal name in module A must not be flagged when walking module A's methods.
///   • If unqualified, or qualified to the current module, the short name is checked
///     against <c>InternalTypeNames</c> as a fallback.
///   • Generic args, optional/array/tuple element types, closure params/returns, and
///     generic where-clause constraints are all walked recursively. Nested
///     <c>InnerType</c> chains on <see cref="NamedTypeSpec"/> are checked link by
///     link.
/// </summary>
internal static class InternalTypeReferenceWalker
{
    /// <summary>
    /// Returns true if any TypeSpec reachable from the method's return type, parameter
    /// list, or generic where-clause resolves to a name in <paramref name="internalTypeNames"/>.
    /// </summary>
    /// <param name="method">The method (including property/subscript accessor MethodDecls) whose signature should be walked.</param>
    /// <param name="internalTypeNames">The module's collected internal type names — short and module-qualified forms.</param>
    /// <param name="currentModuleName">The module being emitted. Cross-module qualified types are never matched against short names from this set.</param>
    public static bool SignatureReachesInternalType(
        MethodDecl method,
        IReadOnlySet<string> internalTypeNames,
        string currentModuleName)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(internalTypeNames);
        if (internalTypeNames.Count == 0)
            return false;

        foreach (var arg in method.CSSignature)
        {
            if (Reaches(arg.SwiftTypeSpec, internalTypeNames, currentModuleName))
                return true;
        }

        foreach (var generic in method.GenericParameters)
        {
            if (GenericConstraintsReachInternal(generic, internalTypeNames, currentModuleName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the property's declared type reaches any internal type name. Walks
    /// the property's <see cref="PropertyDecl.SwiftTypeSpec"/> directly so callers don't
    /// have to construct accessor MethodDecls just to ask the question.
    /// </summary>
    public static bool SignatureReachesInternalType(
        PropertyDecl property,
        IReadOnlySet<string> internalTypeNames,
        string currentModuleName)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(internalTypeNames);
        if (internalTypeNames.Count == 0)
            return false;
        return Reaches(property.SwiftTypeSpec, internalTypeNames, currentModuleName);
    }

    /// <summary>
    /// Returns true if any of the subscript's index parameters or its return type reaches
    /// an internal type name. Includes accessor MethodDecls so generic where-clauses on
    /// the subscript itself are also covered.
    /// </summary>
    public static bool SignatureReachesInternalType(
        SubscriptDecl subscript,
        IReadOnlySet<string> internalTypeNames,
        string currentModuleName)
    {
        ArgumentNullException.ThrowIfNull(subscript);
        ArgumentNullException.ThrowIfNull(internalTypeNames);
        if (internalTypeNames.Count == 0)
            return false;

        if (Reaches(subscript.ReturnTypeSpec, internalTypeNames, currentModuleName))
            return true;

        foreach (var param in subscript.IndexParameters)
        {
            if (Reaches(param.SwiftTypeSpec, internalTypeNames, currentModuleName))
                return true;
        }

        foreach (var accessor in subscript.Accessors)
        {
            if (SignatureReachesInternalType(accessor.Method, internalTypeNames, currentModuleName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Recursive TypeSpec walk. Public for unit tests so the matching rules — especially
    /// the cross-module short-name guard — can be exercised directly.
    /// </summary>
    internal static bool Reaches(
        TypeSpec? typeSpec,
        IReadOnlySet<string> internalTypeNames,
        string currentModuleName)
    {
        if (typeSpec is null)
            return false;

        switch (typeSpec)
        {
            case NamedTypeSpec named:
                return NamedTypeReaches(named, internalTypeNames, currentModuleName);

            case TupleTypeSpec tuple:
                foreach (var element in tuple.Elements)
                {
                    if (Reaches(element, internalTypeNames, currentModuleName))
                        return true;
                }
                return false;

            case ClosureTypeSpec closure:
                if (Reaches(closure.Arguments, internalTypeNames, currentModuleName))
                    return true;
                if (Reaches(closure.ReturnType, internalTypeNames, currentModuleName))
                    return true;
                return false;

            case ProtocolListTypeSpec protocolList:
                foreach (var protocol in protocolList.Protocols.Keys)
                {
                    if (Reaches(protocol, internalTypeNames, currentModuleName))
                        return true;
                }
                return false;

            case AssociatedTypeReferenceSpec:
                // Protocol-associated-type references (e.g. Self.Element, T.Element)
                // are projection paths over generic params, not nominal types — neither
                // `BaseType` nor `AssociatedTypeName` will appear in InternalTypeNames.
                // Listed here explicitly so the walker is exhaustive across TypeSpec
                // subclasses rather than relying on the default fall-through.
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Walks a NamedTypeSpec including its InnerType chain. The chain represents a
    /// nested type path (e.g. <c>Foo.Outer.Inner</c>). To honor the cross-module
    /// short-name guard at every link, we propagate the effective parent module and
    /// build a cumulative qualified path; only links whose chain belongs to the
    /// current module ever fall back to short-name matching.
    /// </summary>
    private static bool NamedTypeReaches(
        NamedTypeSpec named,
        IReadOnlySet<string> internalTypeNames,
        string currentModuleName)
    {
        if (named.HasModule())
        {
            if (internalTypeNames.Contains(named.Name))
                return true;
            if (named.Module == currentModuleName &&
                internalTypeNames.Contains(named.NameWithoutModule))
                return true;
        }
        else
        {
            // Unqualified: fall back to short-name match. Unqualified references are
            // implicitly current-module (or stdlib, but stdlib shorts won't appear in
            // the internal set).
            if (internalTypeNames.Contains(named.Name))
                return true;
        }

        foreach (var generic in named.GenericParameters)
        {
            if (Reaches(generic, internalTypeNames, currentModuleName))
                return true;
        }

        // Walk the InnerType chain. The qualified outer path is `Module.Outer`; each
        // inner link contributes its local name to the cumulative path. Cross-module
        // qualified outer types (e.g. OtherModule.Outer.Inner) must NEVER short-name
        // match against the current module's internal set, even if the inner link's
        // own NamedTypeSpec carries no module prefix.
        var effectiveModule = named.HasModule() ? named.Module : currentModuleName;
        var qualifiedSoFar = named.HasModule()
            ? named.Name
            : currentModuleName + "." + named.Name;

        var inner = named.InnerType;
        while (inner is not null)
        {
            qualifiedSoFar = qualifiedSoFar + "." + inner.Name;

            if (internalTypeNames.Contains(qualifiedSoFar))
                return true;

            // Short-name fallback only when the chain is rooted in the current module.
            if (effectiveModule == currentModuleName &&
                internalTypeNames.Contains(inner.Name))
                return true;

            foreach (var generic in inner.GenericParameters)
            {
                if (Reaches(generic, internalTypeNames, currentModuleName))
                    return true;
            }

            inner = inner.InnerType;
        }

        return false;
    }

    private static bool GenericConstraintsReachInternal(
        GenericArgumentDecl generic,
        IReadOnlySet<string> internalTypeNames,
        string currentModuleName)
    {
        foreach (var conformance in generic.GenericConformances)
        {
            if (ConformanceReachesInternal(conformance, internalTypeNames, currentModuleName))
                return true;
        }
        foreach (var conformance in generic.AssosiatedTypeConformances)
        {
            if (ConformanceReachesInternal(conformance, internalTypeNames, currentModuleName))
                return true;
        }
        return false;
    }

    private static bool ConformanceReachesInternal(
        GenericParameterConformance conformance,
        IReadOnlySet<string> internalTypeNames,
        string currentModuleName)
    {
        var target = conformance.ConformanceTarget;
        if (target is null)
            return false;

        // Module-qualified target: prefer the module-qualified key, fall back to the
        // short name only when the target is in the current module. Mirrors the
        // NamedTypeSpec rule above so generic constraints obey the same matching
        // semantics as parameter/return types.
        var qualified = target.ToString();
        if (!string.IsNullOrEmpty(qualified) && internalTypeNames.Contains(qualified))
            return true;

        if (string.Equals(target.Module, currentModuleName, StringComparison.Ordinal) &&
            internalTypeNames.Contains(target.Name))
            return true;

        return false;
    }
}
