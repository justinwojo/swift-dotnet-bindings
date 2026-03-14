// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Unified validation pipeline for member emission and wrapper eligibility decisions.
/// Consolidates validation gates scattered across handlers, validators, and wrapper emitters
/// into a single ordered pipeline with two entry points:
///
/// - <see cref="ValidateMethodEmission"/>: pre-Marshal, decides "should this member be emitted at all?"
/// - <see cref="ValidateMethodWrapperEligibility"/>: post-Marshal, decides "should a @_cdecl wrapper be generated?"
///
/// Delegates to existing evaluators (MemberGateEvaluator, MemberEmissionValidator,
/// MethodValidationGates, WrapperValidation) — the pipeline is an orchestration layer,
/// not a reimplementation.
///
/// Gate ordering in ValidateMethodEmission:
///   Phase 1 — Suppression gates (cheapest, no type resolution):
///     1. @_spi protection
///     2. Implicit+overriding constructor
///     3. Synthesized protocol member
///   Phase 2 — Closure + module gates (via ShouldSkipMethodEmission):
///     4. Synthesized Codable (Encoder/Decoder)
///     5. Unsupported closure parameters (B20)
///     6. SwiftUI/Combine references (B19)
///     7. Async tuple with non-simple enum (C6)
///
/// Handler-inline gates (bare generic, existential, constraints) remain in handlers
/// because they are interleaved with fallback emission logic (existential bypass/bridge).
/// These will be consolidated in a future phase once the pipeline supports post-Marshal
/// validation with emission fallbacks.
/// </summary>
public class MemberValidationPipeline
{
    private readonly MemberGateEvaluator _gateEvaluator;
    private readonly ITypeDatabase _typeDatabase;

    public MemberValidationPipeline(ITypeDatabase typeDatabase)
    {
        _typeDatabase = typeDatabase;
        _gateEvaluator = new MemberGateEvaluator(typeDatabase);
    }

    #region Emission Validation (pre-Marshal)

    /// <summary>
    /// Validates whether a method should be emitted. Called from HandleBaseDecl before handler selection.
    /// Consolidates: SPI protection, implicit+overriding constructor, synthesized protocol method,
    /// and ShouldSkipMethodEmission.
    /// </summary>
    public ValidationResult ValidateMethodEmission(MethodDecl methodDecl, ValidationContext? context)
    {
        // ── Phase 1: Suppression gates (cheapest, no type resolution) ──

        // 1. @_spi protection
        if (methodDecl.IsSpiProtected)
            return ValidationResult.Skip(SkipReason.ModuleInternal, "@_spi method suppressed from bindings.");

        // 2a. Module-level internal functions (free functions that are not public)
        if (methodDecl.IsModuleInternal && methodDecl.ParentDecl is ModuleDecl)
            return ValidationResult.Skip(SkipReason.ModuleInternal, "Internal module-level function suppressed from bindings.");

        // 2b. Implicit+overriding constructor (doesn't exist at runtime)
        if (methodDecl.IsModuleInternal && methodDecl.IsImplicit && methodDecl.IsOverride && methodDecl.IsConstructor)
            return ValidationResult.Skip(SkipReason.ModuleInternal, "Implicit+overriding constructor is module-internal.");

        // 3. Synthesized protocol member (e.g., hash(into:) for Hashable)
        if (methodDecl.ParentDecl is TypeDecl parentType &&
            MemberEmissionValidator.IsSynthesizedProtocolMethod(methodDecl, parentType))
            return ValidationResult.Synthesized("Synthesized protocol method suppressed.");

        // ── Phase 2: Closure + module gates (via ShouldSkipMethodEmission) ──
        // Catches: synthesized Codable, unsupported closures (B20), SwiftUI/Combine refs (B19),
        // C6 async tuple with non-simple enum
        var methodSkipReason = MemberEmissionValidator.ShouldSkipMethodEmission(methodDecl, _typeDatabase, out var methodSkipDetails);
        if (methodSkipReason != null)
            return ValidationResult.Skip(methodSkipReason.Value, methodSkipDetails ?? "");

        return ValidationResult.Emit;
    }

    /// <summary>
    /// Validates whether a property should be emitted.
    /// Currently all property validation remains in PropertyHandler.Emit.
    /// </summary>
    public ValidationResult ValidatePropertyEmission(PropertyDecl propertyDecl, ValidationContext? context)
    {
        return ValidationResult.Emit;
    }

    /// <summary>
    /// Validates whether a subscript should be emitted.
    /// Currently all subscript validation remains in SubscriptHandler.EmitSubscripts.
    /// </summary>
    public ValidationResult ValidateSubscriptEmission(SubscriptDecl subscriptDecl, ValidationContext? context)
    {
        return ValidationResult.Emit;
    }

    #endregion

    #region Wrapper Eligibility (post-Marshal)

    /// <summary>
    /// Validates whether a method should have a @_cdecl wrapper generated.
    /// Consolidates MethodWrapperEmitter.ShouldEmitWrapper and ConstructorWrapperEmitter.ShouldEmitWrapper.
    /// </summary>
    public WrapperValidationResult ValidateMethodWrapperEligibility(MethodEnvironment env)
    {
        if (env.MethodDecl.IsConstructor)
        {
            bool shouldWrap = ConstructorWrapperEmitter.ShouldEmitWrapper(env);
            if (!shouldWrap)
            {
                var reason = GetConstructorWrapperRejectionReason(env);
                return WrapperValidationResult.Reject(reason ?? "unknown");
            }
            return WrapperValidationResult.Wrap;
        }
        else
        {
            bool shouldWrap = MethodWrapperEmitter.ShouldEmitWrapper(env);
            if (!shouldWrap)
            {
                var reason = WrapperValidation.GetRejectionReason(env);
                return WrapperValidationResult.Reject(reason ?? "unknown");
            }
            return WrapperValidationResult.Wrap;
        }
    }

    /// <summary>
    /// Validates whether a property accessor should have a @_cdecl wrapper generated.
    /// Consolidates PropertyWrapperEmitter.ShouldEmitWrapper.
    /// </summary>
    public WrapperValidationResult ValidatePropertyWrapperEligibility(PropertyDecl propertyDecl, MethodEnvironment accessorEnv)
    {
        bool shouldWrap = PropertyWrapperEmitter.ShouldEmitWrapper(propertyDecl, accessorEnv);
        if (!shouldWrap)
        {
            var reason = PropertyWrapperEmitter.GetRejectionReason(propertyDecl, accessorEnv);
            return WrapperValidationResult.Reject(reason ?? "unknown");
        }
        return WrapperValidationResult.Wrap;
    }

    /// <summary>
    /// Validates whether a subscript accessor should have a @_cdecl wrapper generated.
    /// Consolidates SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper.
    /// </summary>
    public WrapperValidationResult ValidateSubscriptWrapperEligibility(SubscriptDecl subscriptDecl, AccessorDecl accessor, MethodEnvironment env)
    {
        bool shouldWrap = SubscriptWrapperEmitter.ShouldEmitSubscriptWrapper(subscriptDecl, accessor, env);
        if (!shouldWrap)
        {
            var reason = SubscriptWrapperEmitter.GetRejectionReason(subscriptDecl, accessor, env);
            return WrapperValidationResult.Reject(reason ?? "unknown");
        }
        return WrapperValidationResult.Wrap;
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Returns rejection reason mirroring ConstructorWrapperEmitter.ShouldEmitWrapper guard order.
    /// Must stay in sync with all gates in ShouldEmitWrapper.
    /// </summary>
    private static string? GetConstructorWrapperRejectionReason(MethodEnvironment env)
    {
        if (!env.MethodDecl.IsConstructor) return "not_constructor";
        if (!WrapperValidation.IsXCFrameworkMode(env.TypeDatabase)) return null; // N/A
        if (env.MethodDecl.IsModuleInternal) return "internal_constructor";
        if (env.ParentDecl is TypeDecl td && td.IsGeneric &&
            !ConstructorWrapperEmitter.CanEmitGenericClassConstructorWrapper(env, td))
            return "generic_parent_type";
        if (env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
        {
            if (!ClosureEmitter.NeedsClosureCdeclWrapper(env.MethodDecl, env.ClosureHandler))
                return "unsupported_closure_params";
            if (WrapperValidation.HasAnyAsyncClosure(env))
                return "async_closure_params";
        }
        if (env.MethodDecl.IsAsync) return "async_constructor";
        if (WrapperValidation.IsNonCopyableStructParent(env.ParentDecl)) return "non_copyable_struct_parent";
        if (ConstructorWrapperEmitter.HasNonCopyableStructParameter(env)) return "non_copyable_struct_parameter";
        if (ConstructorWrapperEmitter.HasNestedFrozenStructParameter(env)) return "nested_frozen_struct_parameter";
        if (ConstructorWrapperEmitter.HasBufferPointerParameter(env)) return "buffer_pointer_parameter";
        if (WrapperValidation.HasRawGenericTypeParams(env.MethodDecl)) return "raw_generic_type_params";
        if (ConstructorWrapperEmitter.HasVariadicExpansionPattern(env)) return "variadic_expansion_pattern";
        return "unknown";
    }

    #endregion
}
