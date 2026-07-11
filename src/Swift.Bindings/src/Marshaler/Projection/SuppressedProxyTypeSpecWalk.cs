// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Stateless walk over a raw <see cref="TypeSpec"/> tree that collects the distinct suppressed-proxy
/// names at its existential leaves — the TypeSpec twin of <see cref="SuppressedProxyProjectionWalk"/>.
///
/// <para>Some CONSUME-degrade report sites do NOT have a projection already built by emission: enum-case
/// construction (whose scalar <c>Foundation.Data</c> payload arm marshals directly, never projecting)
/// and the closure consume sites (delegate RETURN of a closure PARAMETER / invoker ARGUMENTS of a
/// returned closure). Building a fresh <see cref="TypeProjectionFactory"/> projection purely to report
/// there is NOT diagnostic-only: projecting <c>Foundation.Data</c> records an Apple-supplement dependency
/// and the ObjC-prefix fallback records report state, so a payload that emission's own arm never projects
/// would gain a supplement reference and change the generated project/manifest. This walk re-queries the
/// suppression predicate at the TypeSpec level instead — it constructs no projection, records nothing on
/// the type database, and touches no collector — so it cannot alter emitted output.</para>
///
/// <para>Keyed STRICTLY on the suppressed-proxy predicate at an <see cref="ExistentialContainer1"/> leaf,
/// never on "is an existential": a live proxy, a well-known / <c>object</c> leaf, an existential-union
/// leaf, and an EC2+/composition leaf (which marshals via <c>GetExistentialContainer()</c> and has NO
/// per-element wrap fallback to drop) each contribute nothing — matching the EC1 gate on
/// <see cref="ExistentialProjection.SuppressedProxyName"/> so the two walks report the same set. The
/// recursion mirrors the projection walk's container coverage (Array/ArraySlice/Set/Optional element,
/// Dictionary key+value, Tuple elements, nested) so the two produce byte-identical rows for the same
/// member.</para>
/// </summary>
internal static class SuppressedProxyTypeSpecWalk
{
    /// <summary>
    /// Returns the distinct suppressed-proxy names (order preserved) at <paramref name="typeSpec"/> and
    /// at every existential leaf reachable through its container generic arguments. Uses
    /// <paramref name="handler"/>'s <c>CurrentModuleName</c> for cross-module proxy qualification and
    /// suppression matching, and <paramref name="emissionContext"/> for the per-module suppressed-name
    /// set. Empty when the tree carries no suppressed <c>ExistentialContainer1</c> leaf. Pass a handler
    /// built with a <b>null</b> composition collector so the walk stays side-effect-free.
    /// </summary>
    public static IReadOnlyList<string> CollectSuppressedProxyNames(
        TypeSpec? typeSpec, ExistentialHandler handler, ModuleEmissionContext? emissionContext)
    {
        var names = new List<string>();
        Collect(typeSpec, handler, emissionContext, names);
        return names;
    }

    private static void Collect(
        TypeSpec? typeSpec, ExistentialHandler handler, ModuleEmissionContext? ctx, List<string> names)
    {
        if (typeSpec == null)
            return;

        // Existential leaf: record only a suppressed single-protocol EC1 proxy that actually projects to a
        // real proxy interface — the exact shape whose per-element `static __v => new {Proxy}(__v)` wrap
        // fallback the CONSUME arms drop. EC2+/composition marshals via GetExistentialContainer() with no
        // fallback, so it is not a consume-degrade. The ProjectsToProxyInterface gate mirrors the projection
        // factory's `proxyClassName != null` half: a suppressed protocol-with-associated-types / Self
        // (PAT) existential collapses to `object` (no wrap fallback to drop), so IsProxyReferenceSuppressed
        // alone — which matches the suppressed NAME including PAT conformances — would over-report a false
        // degrade row the projection path never emits. AND-ing the projection gate keeps the two walks in
        // lockstep, side-effect-free (it reads TypeRecords WITHOUT the Apple-supplement recording arm).
        if (handler.IsExistential(typeSpec))
        {
            var protocolList = handler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null
                && handler.GetCSharpExistentialType(protocolList) == "Swift.Runtime.ExistentialContainer1"
                && handler.ProjectsToProxyInterface(protocolList)
                && handler.IsProxyReferenceSuppressed(protocolList, ctx))
            {
                var name = handler.GetQualifiedProxyClassName(protocolList);
                if (!names.Contains(name))
                    names.Add(name);
            }
            return;
        }

        switch (typeSpec)
        {
            case TupleTypeSpec tuple:
                foreach (var element in tuple.Elements)
                    Collect(element, handler, ctx, names);
                return;
            case NamedTypeSpec named
                when named.Name is "Swift.Array" or "Swift.ArraySlice" or "Swift.Set" or "Swift.Optional"
                     && named.GenericParameters.Count == 1:
                Collect(named.GenericParameters[0], handler, ctx, names);
                return;
            case NamedTypeSpec named
                when named.Name == "Swift.Dictionary" && named.GenericParameters.Count == 2:
                Collect(named.GenericParameters[0], handler, ctx, names);
                Collect(named.GenericParameters[1], handler, ctx, names);
                return;
            default:
                return;
        }
    }
}
