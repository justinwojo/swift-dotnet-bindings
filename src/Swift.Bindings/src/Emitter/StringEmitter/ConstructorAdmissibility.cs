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
        if (!parentTypeDecl.IsGeneric)
            return false;

        // A stdlib @_marker the open erased form cannot honour (BitwiseCopyable) is dropped from
        // GenericConformances as an unrepresentable nominal conformance, so the conformance-list
        // walks below never see it; it survives only in the lossless ParsedGenericSignature. Check
        // it up front, independently of method.GenericParameters — a parameter whose ONLY
        // constraint was the dropped marker need not carry an entry in that list.
        if (HasUnerasableParentMarkerConstraint(method, parentTypeDecl))
            return true;

        if (method.GenericParameters.Count == 0)
            return false;

        // A dropped concrete same-type pin (`where RowDecoder == ()`) survives only on the
        // GenericArgumentDecl side-channel and is invisible to the conformance-list walks below;
        // check it up front. The extension-added variant is the one that matters here, for the same
        // reason the walk below subtracts parent-declared representable constraints.
        if (HasExtensionAddedUnrepresentableConcretePin(method, parentTypeDecl))
            return true;

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
    /// True when an initializer pins a PARENT-level generic parameter to a concrete,
    /// unrepresentable same-type target (<c>where RowDecoder == ()</c>), REGARDLESS of whether the
    /// parent type declares that pin itself. Such a pin is dropped by
    /// <see cref="GenericSignatureParser"/> as unrepresentable, so it survives only on
    /// <see cref="GenericArgumentDecl.UnrepresentableConcreteSameTypePins"/> — invisible to the
    /// conformance-list walks that both the open-ctor gate
    /// (<see cref="HasUnsatisfiableParentGenericExtensionConstraint"/>) and CSM's per-conformer
    /// constraint evaluation otherwise rely on.
    ///
    /// This is CSM's gate. CSM does not SUBTRACT a parent-declared constraint — it EVALUATES every
    /// constraint against each candidate conformer and emits only the closed forms that satisfy
    /// them. A dropped pin cannot be evaluated at all, so a conformer violating it would be
    /// enumerated anyway; and the unrepresentable target (e.g. <c>()</c>) is itself never a
    /// conformer CSM enumerates, so no closed form can pin the parameter either. Origin is
    /// therefore irrelevant to CSM: any parent-param pin it cannot see must be refused.
    ///
    /// The open-erasure paths take <see cref="HasExtensionAddedUnrepresentableConcretePin"/>
    /// instead, which subtracts the parent's own pins — see the remarks there for why the two gates
    /// legitimately differ. This mirrors the module-qualified <c>== Swift.Int</c> form (which IS in
    /// GenericConformances and is caught by the conformance walks); the unrepresentable form needs
    /// the side-channel because the parser could not represent its target.
    /// </summary>
    public static bool HasUnrepresentableConcreteParentPin(MethodDecl method, TypeDecl parentTypeDecl)
    {
        if (!parentTypeDecl.IsGeneric || method.GenericParameters.Count == 0)
            return false;

        var parentParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet(System.StringComparer.Ordinal);

        return method.GenericParameters.Any(gp =>
            parentParamNames.Contains(gp.TypeName) && gp.HasUnrepresentableConcreteSameTypePin);
    }

    /// <summary>
    /// The open-erasure narrowing of <see cref="HasUnrepresentableConcreteParentPin"/>: true only
    /// when the initializer carries a dropped concrete pin on a parent-level parameter that the
    /// PARENT TYPE does not declare itself.
    ///
    /// An init inherits its enclosing type's whole generic signature, so
    /// <c>final class Box&lt;T: Seq&gt; where T.Element == Pair&lt;Int&gt;</c> puts that pin on
    /// EVERY init — including a plain in-body one. That shape is legal Swift and its open erased
    /// form compiles: <c>extension Box: _SBW_CI_{hash} {}</c> carries the type's own requirements,
    /// and the type is never usable unpinned, so nothing is left unsatisfied. Only a pin the init
    /// carries and the parent does not is extension-origin, and only that one makes the open form
    /// fail ("does not conform" / "requires the types … be equivalent"). This is exactly the
    /// subtraction <see cref="HasUnsatisfiableParentGenericExtensionConstraint"/> already performs
    /// for representable parent-declared constraints, extended to the dropped ones.
    ///
    /// Subtraction is per CLAUSE, not per parameter, so an extension that adds a SECOND pin rooted
    /// at an already-pinned parameter is still refused.
    /// </summary>
    public static bool HasExtensionAddedUnrepresentableConcretePin(
        MethodDecl method, TypeDecl parentTypeDecl)
    {
        if (!parentTypeDecl.IsGeneric || method.GenericParameters.Count == 0)
            return false;

        var parentParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet(System.StringComparer.Ordinal);

        // The clause text is rooted at the parameter it constrains, so one flat set over the whole
        // parent signature identifies each declared pin unambiguously.
        var parentDeclaredPins = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
        foreach (var pp in parentTypeDecl.GenericParameters)
            if (pp.UnrepresentableConcreteSameTypePins is { } declared)
                parentDeclaredPins.UnionWith(declared);

        foreach (var gp in method.GenericParameters)
        {
            if (!parentParamNames.Contains(gp.TypeName))
                continue;
            if (gp.UnrepresentableConcreteSameTypePins is not { Count: > 0 } pins)
                continue;
            if (pins.Any(p => !parentDeclaredPins.Contains(p)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when an initializer requires a PARENT-level generic parameter — or one of its
    /// associated-type members (<c>Value.Item</c>) — to conform to the stdlib <c>@_marker</c> layout
    /// protocol <c>Swift.BitwiseCopyable</c>, a constraint no open type-erasure form (GSF static
    /// factory / <c>_SBW_CI_</c>) can legally honour. Both the direct form
    /// (<c>where Value: BitwiseCopyable</c>) and the member form
    /// (<c>where Value.Item: BitwiseCopyable</c>) make the unconditional erased body fail to compile,
    /// so both must be refused — see <see cref="GenericSignatureModel.ConformanceTargetsRootedAt"/>.
    ///
    /// Markers are dropped from <see cref="GenericArgumentDecl.GenericConformances"/> as
    /// unrepresentable nominal conformances, so the conformance-list walk in
    /// <see cref="HasUnsatisfiableParentGenericExtensionConstraint"/> never sees them; the
    /// constraint survives verbatim only in <see cref="MethodDecl.ParsedGenericSignature"/>. Most
    /// stdlib markers are harmless to the open erased form: the unconditional
    /// <c>extension Box: _SBW_GSF { … Self(init:) … }</c> body type-checks against
    /// <c>Sendable</c>/<c>SendableMetatype</c> (advisory under <c>-strict-concurrency=minimal</c>)
    /// and against <c>Copyable</c>/<c>Escapable</c> (implicit defaults a generic parameter already
    /// carries absent a <c>~</c> opt-out, which the pipeline rejects far upstream).
    /// <c>BitwiseCopyable</c> is the exception — a real layout requirement: the unconditional
    /// factory body fails to compile (<c>"requires that 'Value' conform to 'BitwiseCopyable'"</c>),
    /// and the marker cannot be re-stated as a conditional conformance (a non-marker protocol's
    /// conditional conformance may not depend on a marker), so there is NO legal open erased form.
    /// The wrapper would be stripped and the emitted C# constructor would dangle; the init must
    /// fail closed.
    ///
    /// This refuses the whole shape, including the rarer parent-type-declared form
    /// (<c>struct Box&lt;Value: BitwiseCopyable&gt;</c>): that body type-checks on the Swift side,
    /// but the C# surface erases the bound away (C# has no <c>BitwiseCopyable</c> constraint), so an
    /// open erased ctor would let a consumer instantiate <c>Box&lt;NonBitwiseCopyable&gt;</c> and
    /// trap in Swift's metadata accessor — itself fail-open-unsafe. CSM never enumerates a marker
    /// conformer (a marker carries no witness table and no conformer list), so no closed form is
    /// lost by refusing here.
    /// </summary>
    public static bool HasUnerasableParentMarkerConstraint(MethodDecl method, TypeDecl parentTypeDecl)
    {
        if (!parentTypeDecl.IsGeneric)
            return false;

        var parentParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToArray();
        if (parentParamNames.Length == 0)
            return false;

        // BitwiseCopyable survives only in the lossless parsed signature; match each requirement's
        // RAW subject root (τ_0_X) against the parent's raw param tokens. This walks DIRECT
        // (`τ_0_0 : Swift.BitwiseCopyable`) AND associated-type member (`τ_0_0.Item :
        // Swift.BitwiseCopyable`) clauses alike — both are requirements the init body inherits, so an
        // unconditional open erased form fails to compile for either (verified: a member-clause GSF
        // body errors "requires that 'Value.Item' conform to 'BitwiseCopyable'").
        foreach (var target in method.ParsedGenericSignature.ConformanceTargetsRootedAt(parentParamNames))
            if (IsUnerasableOpenFormMarker(target))
                return true;
        return false;
    }

    /// <summary>
    /// True if <paramref name="target"/> names <c>Swift.BitwiseCopyable</c> (module-qualified or
    /// bare) — the one stdlib marker an unconditional open erased form cannot satisfy. The other
    /// markers (<c>Sendable</c>/<c>SendableMetatype</c>/<c>Copyable</c>/<c>Escapable</c>) are
    /// erasure-safe and are merely dropped from the emitted <c>where</c> clause by
    /// <c>WrapperEmitterHelpers.IsStdlibMarkerProtocol</c> instead.
    /// </summary>
    private static bool IsUnerasableOpenFormMarker(string target)
    {
        var lt = target.IndexOf('<');
        var head = lt >= 0 ? target[..lt] : target;
        var dot = head.LastIndexOf('.');
        var module = dot >= 0 ? head[..dot] : null;
        var simpleName = dot >= 0 ? head[(dot + 1)..] : head;
        return (module is null or "Swift") && simpleName == "BitwiseCopyable";
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
