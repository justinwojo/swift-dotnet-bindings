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

        // A dropped concrete same-type pin (`where RowDecoder == ()`) survives only as a flag and
        // is invisible to the conformance-list walks below; check it up front via the shared
        // helper so the open-erasure gate and CSM refuse it in lockstep.
        if (HasUnrepresentableConcreteParentPin(method, parentTypeDecl))
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
    /// unrepresentable same-type target (<c>where RowDecoder == ()</c>). Such a pin is dropped by
    /// <see cref="GenericSignatureParser"/> as unrepresentable, so it survives ONLY as
    /// <see cref="GenericArgumentDecl.HasUnrepresentableConcreteSameTypePin"/> — invisible to the
    /// conformance-list walks that both the open-ctor gate
    /// (<see cref="HasUnsatisfiableParentGenericExtensionConstraint"/>) and CSM's per-conformer
    /// constraint evaluation otherwise rely on.
    ///
    /// No erasure path can satisfy it, so every path must refuse it in lockstep:
    ///   • the open <c>_SBW_CI_</c> / GSF form erases against the UNCONSTRAINED type, which Swift
    ///     rejects ("does not conform" / "requires the types … be equivalent");
    ///   • a CSM closed form closes over a DIFFERENT parameter, leaving the pinned parameter
    ///     generic — and the unrepresentable target (e.g. <c>()</c>) is itself never a conformer
    ///     CSM enumerates, so no closed form can pin it either.
    /// This mirrors the module-qualified <c>== Swift.Int</c> form (which IS in GenericConformances
    /// and is caught by the conformance walks); the unrepresentable form needs the flag because the
    /// parser could not represent its target.
    /// </summary>
    public static bool HasUnrepresentableConcreteParentPin(MethodDecl method, TypeDecl parentTypeDecl)
    {
        if (!parentTypeDecl.IsGeneric || method.GenericParameters.Count == 0)
            return false;

        var parentParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet(System.StringComparer.Ordinal);

        foreach (var gp in method.GenericParameters)
            if (gp.HasUnrepresentableConcreteSameTypePin && parentParamNames.Contains(gp.TypeName))
                return true;
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
