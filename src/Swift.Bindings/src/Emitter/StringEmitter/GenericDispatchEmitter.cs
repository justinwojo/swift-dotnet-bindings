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
        switch (kind)
        {
            case GenericDispatchKind.Method:
            {
                // Path 1: Generic class with concrete (non-T-referencing) signature — instance dispatch
                if (parentTypeDecl is ClassDecl && env.MethodDecl.MethodType != MethodType.Static)
                {
                    if (!HasGenericTypeParamInSignature(env, parentTypeDecl))
                        return true;
                }
                // Path 2: Static protocol dispatch
                return CanEmitStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Method);
            }

            case GenericDispatchKind.Constructor:
            {
                // Path 1: Generic class with concrete (non-T-referencing) params — metatype dispatch
                if (parentTypeDecl is ClassDecl)
                {
                    var genericParamNames = parentTypeDecl.GenericParameters
                        .Select(p => p.TypeName)
                        .ToHashSet();
                    bool hasGenericParams = env.MethodDecl.CSSignature.Skip(1)
                        .Any(arg => WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames));
                    if (!hasGenericParams)
                        return true;
                }
                // Path 2: Static factory dispatch
                return CanEmitStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Constructor);
            }

            case GenericDispatchKind.PropertyGetter:
            case GenericDispatchKind.PropertySetter:
                // Properties always have a dispatch path for generic types
                // (concrete-signature generic classes use instance dispatch,
                //  T-referencing or struct types use static dispatch)
                return true;

            default:
                return false;
        }
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
                if (parentTypeDecl is ClassDecl)
                {
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
