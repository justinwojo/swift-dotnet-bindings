// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Specifies which kind of generic member dispatch is being evaluated or emitted.
/// </summary>
internal enum GenericDispatchKind
{
    Method,
    PropertyGetter,
    PropertySetter,
    Constructor
}

/// <summary>
/// Unified eligibility rules for generic dispatch patterns (protocol-based type erasure).
/// Consolidates guard logic previously spread across MethodWrapperEmitter, ConstructorWrapperEmitter,
/// PropertyWrapperEmitter, and WrapperValidation into a single discoverable location.
///
/// Three-level eligibility:
/// 1. <see cref="CanEmitGenericDispatch"/> — can ANY generic wrapper be emitted?
/// 2. <see cref="NeedsStaticDispatch"/> — does this member need the STATIC protocol pattern?
/// 3. <see cref="CanEmitStaticDispatch"/> — can the static pattern handle this specific member?
///
/// Emission remains in the individual emitters (PropertyWrapperEmitter, MethodWrapperEmitter,
/// ConstructorWrapperEmitter) because each kind has structurally different output: different
/// metadata ordering, self handling, return handling, and error paths.
/// </summary>
internal static class GenericDispatchEmitter
{
    // ═══════════════════════════════════════════════════════════════════════
    // Level 1: Can ANY generic wrapper be emitted?
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a generic member can use ANY wrapper dispatch (instance or static).
    /// Replaces MethodWrapperEmitter.CanEmitGenericWrapper,
    ///          ConstructorWrapperEmitter.CanEmitGenericConstructorWrapper,
    ///          and the property eligibility path in PropertyWrapperEmitter.
    /// Called from WrapperValidation.ShouldEmitWrapper (generic_parent guard).
    /// </summary>
    internal static bool CanEmitGenericDispatch(
        MethodEnvironment env, TypeDecl parentTypeDecl, GenericDispatchKind kind)
    {
        // The two wrapper-helper gates (HasUnresolvableTypeConformances and
        // WouldExceedRegisterArgumentThreshold) only apply to dispatch paths that
        // actually route through MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded.
        // They are pushed into the per-path branches below so that "safe" paths —
        // concrete-signature generic class instance methods, concrete properties on
        // generic classes — are NOT rejected just because the parent type has e.g.
        // an associated-type conformance or >3 register args. Those safe paths use
        // SelfReconstructionEmitter.EmitProtocolCast and never touch _sbw_meta_*.
        // See src/docs/Completed/constrained-generic-metadata-witness-tables.md for
        // the 0.8.0 buffer-mode follow-up plan.

        switch (kind)
        {
            case GenericDispatchKind.Method:
            {
                // Path 1: Generic class with concrete (non-T-referencing) signature —
                // instance dispatch via SelfReconstructionEmitter.EmitProtocolCast at
                // MethodWrapperEmitter.cs:495. Does NOT call EmitMetadataAccessorHelperIfNeeded,
                // so the wrapper-helper gates DO NOT apply here.
                if (parentTypeDecl is ClassDecl && env.MethodDecl.MethodType != MethodType.Static)
                {
                    if (!HasGenericTypeParamInSignature(env, parentTypeDecl))
                        return true;
                }

                // Path 2: Static protocol dispatch routes through EmitGenericStaticDispatchMethod,
                // which calls EmitMetadataAccessorHelperIfNeeded. Apply the wrapper-helper gates
                // before delegating to CanEmitStaticDispatch.
                if (HasWrapperHelperGateBlocker(parentTypeDecl, env.TypeDatabase))
                    return false;
                return CanEmitStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Method);
            }

            case GenericDispatchKind.Constructor:
            {
                // BOTH constructor paths route through EmitMetadataAccessorHelperIfNeeded:
                //  • Path 1 (concrete params, final class) — ConstructorWrapperEmitter.cs:532
                //  • Path 2 (static factory)               — ConstructorWrapperEmitter.cs:1050
                // The wrapper-helper gates apply to both, so check them up front.
                if (HasWrapperHelperGateBlocker(parentTypeDecl, env.TypeDatabase))
                    return false;

                // Path 1: Generic class with concrete (non-T-referencing) params — metatype dispatch
                // via _SBW_CI_ protocol with init() requirement. Only works for final classes
                // because non-final classes can't satisfy protocol init() without `required`.
                if (parentTypeDecl is ClassDecl classDecl)
                {
                    var genericParamNames = parentTypeDecl.GenericParameters
                        .Select(p => p.TypeName)
                        .ToHashSet();
                    bool hasGenericParams = env.MethodDecl.CSSignature.Skip(1)
                        .Any(arg => WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
                    if (!hasGenericParams)
                        return classDecl.IsFinal; // Non-final can't use _SBW_CI_ protocol
                }
                // Path 2: Static factory dispatch
                return CanEmitStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Constructor);
            }

            case GenericDispatchKind.PropertyGetter:
            case GenericDispatchKind.PropertySetter:
                // Properties always have a dispatch path for generic types
                // (concrete-signature generic classes use instance dispatch,
                //  T-referencing or struct types use static dispatch).
                // The wrapper-helper gates are scoped per-property in
                // PropertyWrapperEmitter.ShouldEmitWrapper because we need the
                // PropertyDecl to know whether the property type references T
                // (only T-typed properties go through the helper-using path).
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Returns true if the parent type would trip either wrapper-helper gate:
    ///  - <see cref="MetatypeHelperEmitter.HasUnresolvableTypeConformances"/>: parent has a
    ///    Self-requirement / associated-type protocol that <c>GetResolvablePwtParameterCount</c>
    ///    silently undercounts. Calling the dlsym'd Ma symbol with too few PWT slots shifts
    ///    caller-saved registers and PAC-traps on arm64e.
    ///  - <see cref="MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold"/>: total
    ///    (num_metadata + num_pwts) > 3 forces Swift's metadata accessor into the indirect
    ///    buffer ABI. Our wrapper helper always declares the symbol as a thin function with
    ///    explicit register args, so the call would shift registers and PAC-trap.
    /// Both are tracked as 0.8.0 follow-ups; for now, refuse to emit any wrapper that would
    /// route through <see cref="MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded"/>.
    /// </summary>
    internal static bool HasWrapperHelperGateBlocker(TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        if (MetatypeHelperEmitter.HasUnresolvableTypeConformances(parentTypeDecl, typeDatabase))
            return true;
        if (MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(parentTypeDecl, typeDatabase))
            return true;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Level 2: Does this member NEED the static protocol dispatch pattern?
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a generic member needs the STATIC protocol dispatch pattern
    /// (vs instance protocol dispatch for generic classes with concrete signatures).
    /// Replaces MethodWrapperEmitter.NeedsGenericStaticDispatch,
    ///          ConstructorWrapperEmitter.NeedsGenericStaticFactory,
    ///          and WrapperValidation.NeedsGenericDispatch's per-kind switch.
    /// </summary>
    internal static bool NeedsStaticDispatch(
        MethodEnvironment env, TypeDecl parentTypeDecl, GenericDispatchKind kind)
    {
        if (!parentTypeDecl.IsGeneric) return false;

        switch (kind)
        {
            case GenericDispatchKind.Method:
            {
                // Generic class with concrete signature uses existing instance dispatch
                if (parentTypeDecl is ClassDecl && env.MethodDecl.MethodType != MethodType.Static)
                {
                    if (!HasGenericTypeParamInSignature(env, parentTypeDecl))
                        return false;
                }
                // All generic struct methods need static dispatch;
                // class methods with T in signature need it too
                return true;
            }

            case GenericDispatchKind.Constructor:
            {
                // Generic class with no T in params uses existing metatype dispatch
                // via _SBW_CI_ protocol with init() requirement — BUT only for final
                // classes. Non-final classes can't satisfy protocol init() without
                // `required`, so they must NOT use the _SBW_CI_ path. This must stay
                // in sync with CanEmitGenericDispatch which rejects non-final classes.
                if (parentTypeDecl is ClassDecl classDecl)
                {
                    if (!classDecl.IsFinal)
                        return true; // Force static factory — _SBW_CI_ won't compile

                    var genericParamNames = parentTypeDecl.GenericParameters
                        .Select(p => p.TypeName)
                        .ToHashSet();
                    bool hasGenericParams = env.MethodDecl.CSSignature.Skip(1)
                        .Any(arg => WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
                    if (!hasGenericParams) return false;
                }
                // All generic struct constructors need static factory approach
                return true;
            }

            case GenericDispatchKind.PropertyGetter:
            case GenericDispatchKind.PropertySetter:
            {
                // Generic struct (not class) always needs static dispatch
                if (parentTypeDecl is not ClassDecl)
                    return true;
                // Generic class: needs static dispatch only if property type references T
                // (caller must pass the propertyDecl to check — handled by the overload below)
                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Property-specific overload that checks whether the property type references T.
    /// Generic class properties only need static dispatch when their type involves T.
    /// </summary>
    internal static bool NeedsStaticDispatchForProperty(
        MethodEnvironment env, TypeDecl parentTypeDecl, PropertyDecl propertyDecl)
    {
        if (!parentTypeDecl.IsGeneric) return false;

        // Generic struct always needs static dispatch
        if (parentTypeDecl is not ClassDecl)
            return true;

        // Generic class: check if property type references T
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();
        return WrapperValidation.TypeSpecReferencesGenericParam(propertyDecl.SwiftTypeSpec, genericParamNames);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Level 3: Can the static dispatch pattern handle this specific member?
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when the static dispatch pattern can handle this specific member.
    /// Replaces MethodWrapperEmitter.CanEmitGenericStaticMethodWrapper,
    ///          ConstructorWrapperEmitter.CanEmitGenericStaticFactoryWrapper.
    /// Unified logic: checks instance-only (methods), T-param simplicity,
    /// T-closure rejection (constructors), failable rejection (constructors).
    /// </summary>
    internal static bool CanEmitStaticDispatch(
        MethodEnvironment env, TypeDecl parentTypeDecl, GenericDispatchKind kind)
    {
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        switch (kind)
        {
            case GenericDispatchKind.Method:
            {
                // Instance methods only — static methods lack self pointer for dispatch
                if (env.MethodDecl.MethodType == MethodType.Static)
                    return false;

                // For non-class parents (structs), only allow methods that reference T in their
                // signature. Methods with concrete-only signatures may come from constrained extensions.
                if (parentTypeDecl is not ClassDecl)
                {
                    bool signatureReferencesT = env.MethodDecl.CSSignature
                        .Any(arg => WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
                    if (!signatureReferencesT)
                        return false;
                }

                // Check params: T-typed must be simple direct generic params
                foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
                {
                    if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                        continue;
                    if (arg.SwiftTypeSpec.IsEmptyTuple)
                        continue;

                    if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                    {
                        if (arg.SwiftTypeSpec is NamedTypeSpec named && genericParamNames.Contains(named.Name))
                            continue;
                        return false;
                    }
                }

                // Check return: T-typed returns are OK (routed through resultPtr)
                var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
                if (WrapperValidation.TypeSpecReferencesGenericParam(returnSpec, genericParamNames))
                {
                    if (returnSpec is not NamedTypeSpec named || !genericParamNames.Contains(named.Name))
                        return false;
                }

                return true;
            }

            case GenericDispatchKind.Constructor:
            {
                // Constrained constructors (e.g., init where Value == Data?) can't use the
                // protocol factory pattern because the constraint can't be expressed in the protocol.
                // Detect same-type requirements on parent generic params (τ_0_X == ConcreteType).
                if (HasSameTypeConstraintOnParentGenericParam(env.MethodDecl))
                    return false;

                foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
                {
                    if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                        continue;
                    if (arg.SwiftTypeSpec.IsEmptyTuple)
                        continue;

                    // Closure parameters that reference T are not supported
                    if (env.ClosureHandler.IsClosure(arg))
                    {
                        var closureSpec = env.ClosureHandler.GetClosureTypeSpec(arg);
                        if (closureSpec != null && WrapperValidation.TypeSpecReferencesGenericParam(closureSpec, genericParamNames))
                            return false;
                    }

                    // T-typed params must be simple (direct generic param)
                    if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                    {
                        if (arg.SwiftTypeSpec is NamedTypeSpec named && genericParamNames.Contains(named.Name))
                            continue;
                        return false;
                    }
                }

                return true;
            }

            case GenericDispatchKind.PropertyGetter:
            case GenericDispatchKind.PropertySetter:
                // Properties always support static dispatch when needed
                return true;

            default:
                return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Shared helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if the method's generic signature contains a same-type constraint
    /// on a parent generic param (e.g., "τ_0_0 == Foundation.Data?"). Such constructors
    /// only exist for specific specializations and can't be dispatched through a protocol
    /// factory that covers ALL specializations.
    /// </summary>
    private static bool HasSameTypeConstraintOnParentGenericParam(MethodDecl methodDecl)
    {
        var sig = methodDecl.RawGenericSig;
        if (string.IsNullOrEmpty(sig))
            return false;

        // Match patterns like "τ_0_0 ==" which constrain parent-level generic params
        // (depth 0) to specific types. Method-level params are depth 1+ (τ_1_0).
        return System.Text.RegularExpressions.Regex.IsMatch(sig, @"τ_0_\d+\s*==");
    }

    /// <summary>
    /// Checks whether any parameter or the return type references the parent type's generic
    /// type parameters (e.g., τ_0_0, τ_0_1).
    /// </summary>
    internal static bool HasGenericTypeParamInSignature(MethodEnvironment env, TypeDecl parentTypeDecl)
    {
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        foreach (var arg in env.MethodDecl.CSSignature)
        {
            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                return true;
        }
        return false;
    }
}
