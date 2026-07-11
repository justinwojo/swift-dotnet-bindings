// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Stateless walk over a type-projection tree that collects the distinct suppressed-proxy names at its
/// existential leaves. A collection-element consume site (a <c>[any P]</c> array/set/dict or an
/// <c>(any P)?</c> optional parameter/setter) drops its per-element <c>static __v =&gt; new {Proxy}(__v)</c>
/// wrap fallback inside the leaf <see cref="ExistentialProjection"/>, which has no handle on the owning
/// member decl. The handler that DOES own the decl (method/property/subscript/receiver) calls this helper
/// right after building the projection, then records one classified consume-degrade row per distinct
/// suppressed proxy — so the same per-member decline the scalar sites already record is no longer silent
/// for the collection surface. A bare suppressed-existential <b>root</b> is collected too (not only nested
/// leaves): the reverse-dispatch receiver getter hands a scalar <c>any P</c> projection here and relies on
/// the root being reported.
///
/// The walk is keyed strictly on the <b>suppressed-proxy</b> predicate (<see cref="ExistentialProjection.ConsumeProxyIsSuppressed"/>),
/// never on "is an existential": a live proxy, a well-known / <c>object</c> leaf, and an existential-union
/// leaf all carry a null <see cref="ExistentialProjection.SuppressedProxyName"/> and contribute nothing, so
/// this does not re-enter the ExistentialUnion inert-engine path. It is purely additive — it emits no C#
/// and does not touch the projection — so it cannot change generated output.
/// </summary>
internal static class SuppressedProxyProjectionWalk
{
    /// <summary>
    /// Returns the distinct suppressed-proxy names (order preserved) at the projection root and at every
    /// existential leaf reachable through the <see cref="ArrayProjection"/> / <see cref="SetProjection"/>
    /// / <see cref="DictionaryProjection"/> / <see cref="OptionalProjection"/> / <see cref="TupleProjection"/>
    /// sub-projection accessors, including nesting. Empty when the tree carries no suppressed-proxy leaf
    /// (a live proxy, an <c>object</c> / well-known leaf, a union, or a non-existential container); a bare
    /// suppressed-existential root yields that one name.
    /// </summary>
    public static IReadOnlyList<string> CollectSuppressedProxyNames(ITypeProjection? projection)
    {
        var names = new List<string>();
        Collect(projection, names);
        return names;
    }

    private static void Collect(ITypeProjection? projection, List<string> names)
    {
        switch (projection)
        {
            case null:
                return;
            case ExistentialProjection existential:
                if (existential.SuppressedProxyName is { } name && !names.Contains(name))
                    names.Add(name);
                return;
            case ArrayProjection array:
                Collect(array.ElementProjection, names);
                return;
            case SetProjection set:
                Collect(set.ElementProjection, names);
                return;
            case OptionalProjection optional:
                Collect(optional.InnerProjection, names);
                return;
            case DictionaryProjection dictionary:
                Collect(dictionary.KeyProjection, names);
                Collect(dictionary.ValueProjection, names);
                return;
            case TupleProjection tuple:
                foreach (var element in tuple.ElementProjections)
                    Collect(element, names);
                return;
            default:
                return;
        }
    }
}
