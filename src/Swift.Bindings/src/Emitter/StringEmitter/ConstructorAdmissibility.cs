// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// Single source of truth for "can a constructor be erased through a runtime wrapper?".
///
/// Three init-erasure paths historically each re-implemented part of this contract:
///   • the normal `@_cdecl` ctor wrapper (<see cref="ConstructorWrapperEmitter.ShouldEmitWrapper"/>),
///   • the GSF static factory and `_SBW_CI_` open-type-erasure paths
///     (gated by <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/>), and
///   • CSM closed-conformer specializations
///     (<see cref="ConcreteProtocolSpecializationEmitter.CanEmitConcreteOverloadForPairing"/>).
///
/// CSM and `_SBW_CI_` skipped the cheap filters the normal path applied (`_const`, internal)
/// and never checked whether an init's constrained-extension `where` clause is satisfiable by
/// the type the wrapper closes over. Centralising the contract here keeps every consumer in
/// lockstep.
/// </summary>
internal static class ConstructorAdmissibility
{
    /// <summary>
    /// True when any non-self parameter is `_const` (a compile-time-constant literal, e.g.
    /// AppIntents `EntityProperty(identifier: _const String)`). A runtime wrapper passes a
    /// runtime value, which Swift rejects with "expect a compile-time constant literal".
    /// The annotation is stripped from ABI JSON; the flag is sourced from the swiftinterface.
    /// </summary>
    public static bool HasConstLiteralParameter(MethodDecl method)
        => method.CSSignature.Skip(1).Any(a => a.IsConstLiteral);

    /// <summary>
    /// Cheap, receiver-independent constructor filters shared by CSM closed forms and the
    /// normal/open ctor paths. Returns false (with a reason) for inits no erasure path can
    /// legally emit a runtime wrapper for, regardless of conformer:
    ///   • <see cref="MethodDecl.IsModuleInternal"/> — internal/unavailable inits aren't callable.
    ///     NOTE: unconditional `@available(*, unavailable)` members are stripped from a
    ///     from-source ABI JSON entirely, so this branch is defense-in-depth / parity; it bites
    ///     only when an init reaches the emitter still flagged internal (`_`-prefixed,
    ///     negative-space, or a cross-assembly dependency whose availability facts weren't loaded).
    ///   • a `_const` parameter (see <see cref="HasConstLiteralParameter"/>).
    /// </summary>
    public static bool PassesConstructorCheapFilters(MethodDecl method, out string? rejectReason)
    {
        if (method.IsModuleInternal)
        {
            rejectReason = "internal/unavailable initializer";
            return false;
        }
        if (HasConstLiteralParameter(method))
        {
            rejectReason = "constructor has a `_const` (compile-time-constant) parameter";
            return false;
        }
        rejectReason = null;
        return true;
    }

    /// <summary>
    /// True when <paramref name="method"/> is an initializer declared in a constrained
    /// extension that adds a requirement on the PARENT type's generic parameter the
    /// unconstrained parent type does not declare — e.g.
    /// <c>extension Box where Value.Element == Int { init(…) }</c> or
    /// <c>extension Box where Value.Element : Collection { init(…) }</c>.
    ///
    /// The open-type-erasure paths (`_SBW_CI_` Path 1, GSF static factory Path 2) emit an
    /// open form against the UNCONSTRAINED type — <c>extension Box: _SBW_CI_{hash} {}</c> or a
    /// <c>static func _sbw_create</c> on a protocol extension — which Swift rejects because the
    /// bare type does not satisfy the extension's `where` clause ("does not conform" /
    /// "requires the types … be equivalent"). CSM emits the satisfying closed forms per
    /// conformer instead, so open dispatch must refuse these.
    ///
    /// Detection works on the already-parsed <see cref="GenericArgumentDecl"/> constraints
    /// (no second sig parser): a constraint on a parent-level generic param present on the
    /// init but NOT declared on the parent type is extension-origin. Parent-declared recursive
    /// constraints (e.g. <c>class Box&lt;T&gt; where T : Seq, T.Element : Eq</c>) appear on
    /// every init and are subtracted, so a plain in-body init on such a type is not rejected.
    /// </summary>
    public static bool HasUnsatisfiableParentGenericExtensionConstraint(
        MethodDecl method, TypeDecl parentTypeDecl)
    {
        if (!parentTypeDecl.IsGeneric || method.GenericParameters.Count == 0)
            return false;

        var parentParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet(System.StringComparer.Ordinal);
        if (parentParamNames.Count == 0)
            return false;

        var parentDeclared = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (var gp in parentTypeDecl.GenericParameters)
        {
            foreach (var c in gp.GenericConformances) parentDeclared.Add(ConstraintKey(c));
            foreach (var c in gp.AssosiatedTypeConformances) parentDeclared.Add(ConstraintKey(c));
        }

        foreach (var gp in method.GenericParameters)
        {
            // Only constraints rooted at a parent-level generic param (τ_0_X) matter — a
            // method-own param (τ_1_X) closing over a conformer is the CSM/GSF method-generic
            // dimension, not a parent-type-erasure concern.
            if (!parentParamNames.Contains(gp.TypeName))
                continue;

            foreach (var c in gp.GenericConformances)
                if (!parentDeclared.Contains(ConstraintKey(c)))
                    return true;
            foreach (var c in gp.AssosiatedTypeConformances)
                if (!parentDeclared.Contains(ConstraintKey(c)))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// Stable string key for a parsed constraint: <c>member.path:Target</c> (conformance) or
    /// <c>member.path==Target</c> (same-type). Both the parent's declared constraints and the
    /// init's are keyed the same way (same parser, same representation), so set membership is
    /// an exact, formatting-independent comparison.
    /// </summary>
    private static string ConstraintKey(GenericParameterConformance c)
    {
        var op = c.Kind == ConformanceKind.Protocol ? ":" : "==";
        return $"{string.Join('.', c.Path)}{op}{c.ConformanceTarget}";
    }
}
