// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Session 6c Route C — single source of truth for "is this method a per-Value-type
/// keypath-sort shape?". Used by two call sites that must agree exactly:
/// <list type="bullet">
///   <item><description>The Route C sibling emitter
///   (<see cref="KeyPathBagValueSpecializationEmitter"/>) — decides whether to
///   emit per-V overloads at all.</description></item>
///   <item><description><see cref="MemberValidationPipeline"/> — when the predicate
///   accepts the method, the pipeline returns <c>RoutedElsewhere</c> so the open-V
///   parent-body emission is suppressed and the C# surface holds only the closed
///   overloads.</description></item>
/// </list>
///
/// <para>
/// CSM's eligibility predicates
/// (<see cref="ConcreteProtocolSpecializationEmitter.IsCsmSyncEligibleForGenericParent"/>
/// and its async sibling) do NOT consult this predicate: Route C and CSM are
/// mutually exclusive by construction. CSM requires every method-own generic param
/// to be specializable (i.e. conformer-pairable, which requires at least one
/// protocol conformance), while Route C requires its method-own V to be
/// unconstrained (condition 3 below). A method matching one shape cannot match
/// the other, so the pipeline's CSM-suppression and Route-C-suppression checks
/// stand alone — there is no third "CSM consults Route C" hand-off.
/// </para>
///
/// <para>
/// A method qualifies for per-V specialization iff <b>all</b> of:
/// </para>
/// <list type="number">
///   <item><description>Has a parent generic constrained to a PAT (so CSM's
///   conformer enumeration applies).</description></item>
///   <item><description>Has <b>exactly one</b> method-own generic parameter V.</description></item>
///   <item><description>V has <b>zero</b> protocol constraints (unconstrained — Route C
///   handles this <i>by design</i>; constraint-bearing V would need Route B).</description></item>
///   <item><description>V appears <b>exclusively</b> in the Value slot of <b>exactly one</b>
///   KeyPath-family parameter. Not in the return type, not in any other parameter,
///   not transitively inside a nested generic.</description></item>
///   <item><description>The KeyPath-family parameter's Root type spec references the parent
///   generic's associated-type bag (e.g. <c>Parent.LibrarySortProperties</c>).</description></item>
///   <item><description>The method is <b>not async</b>, <b>not throws-typed-error</b>,
///   <b>not actor-isolated</b> (these add witness-table complications that are out of
///   scope; the simple synchronous mutating shape is what MusicKit's <c>sort(by:)</c> is).</description></item>
/// </list>
/// </summary>
public static class RouteCSortShapeEligibility
{
    /// <summary>
    /// Resolved shape of a Route-C-eligible keypath-sort method: enough state for
    /// the emitter to render per-(conformer × distinct projectable V) overloads
    /// without re-deriving anything from the predicate.
    /// </summary>
    public sealed record RouteCSortShape(
        string ParentGenericParamName,
        SwiftTypeName ProtocolName,
        string AssocBagName,
        int KeyPathParameterIndex,
        string MethodOwnValueParamName);

    /// <summary>
    /// Returns true if the method matches the Route C per-V specialization shape.
    /// On success, <paramref name="shape"/> carries the resolved descriptor; on
    /// failure, it is null.
    /// </summary>
    public static bool IsRouteCSortShapeEligible(
        MethodDecl method,
        TypeDecl parentTypeDecl,
        out RouteCSortShape? shape)
    {
        shape = null;
        if (!parentTypeDecl.IsGeneric) return false;
        // Route C closes the parent receiver as `Parent<Conformer>` in the Swift
        // trampoline. Multi-generic-parent types like `MusicLibrarySectionedRequest<Section, Item>`
        // would need the trampoline to close BOTH params simultaneously — a cartesian
        // (Section conformer × Item conformer) blow-up that the current emitter has
        // no recipe for. Single-generic-parent only.
        if (parentTypeDecl.GenericParameters.Count != 1) return false;
        // Synchronous, non-throws-typed, non-actor-isolated shape only (condition 6).
        if (method.IsAsync) return false;
        if (method.HasTypedThrows) return false;
        if (method.IsAccessor || method.IsConstructor) return false;
        if (method.IsActorIsolated) return false;

        // Condition 1: identify the parent's PAT-constrained generic parameter and the
        // protocol it conforms to. The Route C emitter calls engine.GetConformers on
        // that protocol; without a PAT constraint we have no conformer set to walk.
        // Index parent params by both canonical (τ_0_0) and sugared (Item) so the
        // KeyPath Root lookup matches whichever spelling the parser produced.
        var parentParamNames = new HashSet<string>(StringComparer.Ordinal);
        SwiftTypeName? patProtocol = null;
        string? patParamCanonicalName = null;
        foreach (var gp in parentTypeDecl.GenericParameters)
        {
            if (!string.IsNullOrEmpty(gp.TypeName)) parentParamNames.Add(gp.TypeName);
            if (!string.IsNullOrEmpty(gp.SugaredTypeName)) parentParamNames.Add(gp.SugaredTypeName);
            if (patProtocol is not null) continue;
            foreach (var conf in gp.GenericConformances)
            {
                if (conf.Kind != ConformanceKind.Protocol) continue;
                patProtocol = conf.ConformanceTarget;
                patParamCanonicalName = gp.TypeName;
                break;
            }
        }
        if (patProtocol is null || patParamCanonicalName is null) return false;

        // Conditions 2 + 3: exactly one method-own generic V with zero constraints.
        var ownParams = method.GenericParameters
            .Where(p => !parentParamNames.Contains(p.TypeName))
            .ToList();
        if (ownParams.Count != 1) return false;
        var vParam = ownParams[0];
        // Any protocol-kind conformance is a constraint. Concrete-type bindings
        // (same-type constraints) on V also fall under "not Route C".
        if (vParam.GenericConformances.Any(c => c.Kind == ConformanceKind.Protocol)) return false;
        if (vParam.GenericConformances.Any(c => c.Kind == ConformanceKind.ConcreteType)) return false;
        if (vParam.AssosiatedTypeConformances.Count > 0) return false;
        var vName = vParam.TypeName;
        var vSugared = vParam.SugaredTypeName;

        // Conditions 4 + 5: scan every signature position. Count V occurrences
        // outside the qualifying KeyPath, count qualifying KeyPath sites, and
        // capture the qualifying KeyPath's parameter index + Root assoc-bag name.
        int vOccurrencesOutside = 0;
        int qualifyingKeyPathCount = 0;
        int qualifyingKpParamIndex = -1;
        string? qualifyingAssocBagName = null;

        // CSSignature[0] is the return type. Walk return + each parameter
        // (CSSignature.Skip(1)) so we count V occurrences across the whole shape.
        for (int idx = 0; idx < method.CSSignature.Count; idx++)
        {
            var arg = method.CSSignature[idx];
            var spec = arg.SwiftTypeSpec;
            int kpParamIndex = idx - 1; // -1 means "this is the return type"

            // Detect a top-level qualifying KeyPath in a parameter position. The
            // qualifying shape is a KeyPath-family NamedTypeSpec at the parameter's
            // root whose Root references the parent's PAT-constrained generic.
            if (kpParamIndex >= 0
                && TryMatchQualifyingKeyPath(spec, parentParamNames, vName, vSugared,
                    out var rootAssocBagName))
            {
                qualifyingKeyPathCount++;
                qualifyingKpParamIndex = kpParamIndex;
                qualifyingAssocBagName = rootAssocBagName;
                // Count V occurrences in this argument's spec EXCLUDING the one
                // legitimate Value-slot occurrence of the qualifying KeyPath.
                vOccurrencesOutside += CountReferencesExcludingTopKeyPathValueSlot(spec, vName, vSugared);
            }
            else
            {
                vOccurrencesOutside += CountReferences(spec, vName, vSugared);
            }
        }

        if (qualifyingKeyPathCount != 1) return false;
        if (vOccurrencesOutside != 0) return false;
        if (qualifyingAssocBagName is null) return false;

        shape = new RouteCSortShape(
            ParentGenericParamName: patParamCanonicalName,
            ProtocolName: patProtocol,
            AssocBagName: qualifyingAssocBagName,
            KeyPathParameterIndex: qualifyingKpParamIndex,
            MethodOwnValueParamName: vName);
        return true;
    }

    /// <summary>
    /// True if <paramref name="spec"/> is a top-level KeyPath-family NamedTypeSpec
    /// whose Root resolves to <c>{parentGeneric}.{AssocBag}</c> and whose Value
    /// slot is exactly the method-own V (no nested wrapping). The two encodings
    /// mirror <c>KeyPathSingletonEmitter.ScanTypeSpec</c>.
    /// </summary>
    private static bool TryMatchQualifyingKeyPath(
        TypeSpec? spec,
        HashSet<string> parentParamNames,
        string vName,
        string vSugared,
        out string? rootAssocBagName)
    {
        rootAssocBagName = null;
        if (spec is not NamedTypeSpec named) return false;
        if (!TypeProjectionFactory.IsKeyPathFamily(named.Name)) return false;
        if (named.GenericParameters.Count < 2) return false;

        var rootSpec = named.GenericParameters[0];
        string? rootBase = null;
        string? rootAssoc = null;
        if (rootSpec is AssociatedTypeReferenceSpec atRef
            && !string.IsNullOrEmpty(atRef.AssociatedTypeName))
        {
            rootBase = atRef.BaseType;
            rootAssoc = atRef.AssociatedTypeName;
        }
        else if (rootSpec is NamedTypeSpec rootNamed && rootSpec.GenericParameters.Count == 0)
        {
            var dotIdx = rootNamed.Name.IndexOf('.');
            if (dotIdx > 0 && dotIdx < rootNamed.Name.Length - 1)
            {
                rootBase = rootNamed.Name.Substring(0, dotIdx);
                rootAssoc = rootNamed.Name.Substring(dotIdx + 1);
            }
        }
        if (rootBase is null || rootAssoc is null) return false;
        if (!parentParamNames.Contains(rootBase)) return false;

        // Value slot must be the method-own V *as a bare name* — no Optional<V>,
        // no Array<V>, no nested generic. Route C's `unsafeDowncast(_, to: KP<R, V>.self)`
        // wrapper requires the Value type to substitute identically.
        var valueSpec = named.GenericParameters[1];
        if (valueSpec is not NamedTypeSpec valueNamed) return false;
        if (valueNamed.GenericParameters.Count != 0) return false;
        if (valueNamed.Name != vName && valueNamed.Name != vSugared) return false;

        rootAssocBagName = rootAssoc;
        return true;
    }

    /// <summary>
    /// Count occurrences of V (by either canonical or sugared name) anywhere inside
    /// the spec tree. Used for the "V appears only in the one qualifying KeyPath
    /// Value slot" guard (condition 4).
    /// </summary>
    private static int CountReferences(TypeSpec? spec, string vName, string vSugared)
    {
        if (spec is null) return 0;
        int count = 0;
        if (spec is NamedTypeSpec named)
        {
            if (named.Name == vName || named.Name == vSugared) count++;
            foreach (var arg in named.GenericParameters)
                count += CountReferences(arg, vName, vSugared);
        }
        else if (spec is TupleTypeSpec tuple)
        {
            foreach (var elt in tuple.Elements)
                count += CountReferences(elt, vName, vSugared);
        }
        else if (spec is ClosureTypeSpec closure)
        {
            count += CountReferences(closure.Arguments, vName, vSugared);
            count += CountReferences(closure.ReturnType, vName, vSugared);
        }
        else if (spec is AssociatedTypeReferenceSpec atRef)
        {
            if (atRef.BaseType == vName || atRef.BaseType == vSugared) count++;
        }
        return count;
    }

    /// <summary>
    /// Count V references in a qualifying KeyPath's argument spec, EXCLUDING the
    /// one legitimate occurrence in its top-level Value slot. Used to detect cases
    /// where V appears both as the KP Value and somewhere else in the same arg
    /// (e.g. <c>KeyPath&lt;Root, V&gt;</c> passed inside a tuple alongside <c>V</c>).
    /// </summary>
    private static int CountReferencesExcludingTopKeyPathValueSlot(
        TypeSpec spec, string vName, string vSugared)
    {
        if (spec is not NamedTypeSpec named) return CountReferences(spec, vName, vSugared);
        if (!TypeProjectionFactory.IsKeyPathFamily(named.Name) || named.GenericParameters.Count < 2)
            return CountReferences(spec, vName, vSugared);

        int count = 0;
        // Root: count any V occurrences (V must not be in Root).
        count += CountReferences(named.GenericParameters[0], vName, vSugared);
        // Value slot (index 1): exclude the one top-level bare reference; count
        // any nested occurrences (e.g. `Array<V>` would carry an inner V).
        var valueSpec = named.GenericParameters[1];
        if (valueSpec is NamedTypeSpec valueNamed && valueNamed.GenericParameters.Count == 0
            && (valueNamed.Name == vName || valueNamed.Name == vSugared))
        {
            // The legit Value-slot V — don't count.
        }
        else
        {
            count += CountReferences(valueSpec, vName, vSugared);
        }
        // Any further KeyPath generic args (the family doesn't have arity > 2 right
        // now, but be defensive).
        for (int i = 2; i < named.GenericParameters.Count; i++)
            count += CountReferences(named.GenericParameters[i], vName, vSugared);
        return count;
    }
}
