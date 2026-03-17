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
///   Phase 3 — Generic type callback gate (thunk closure in PInvokeHelperContext):
///     8. Constructor: closure requiring thunk in generic type
///     9. Method: closure thunk or async in generic type (with bridge eligibility exceptions)
///   Phase 4 — Protocol constraint gate (non-constructor only):
///     10. Constraints on protocols with associated types or self requirements
///   Phase 5 — Bound generic gates (non-accessor only):
///     11. Bare generic usage in signature
///     12. Non-ISwiftObject bound generic type argument
///     13. Unsatisfied generic constraint
///   Phase 6 — Generic constructor own params (constructor only):
///     14. C# does not support generic constructors with method-own type parameters
///
/// Existential type argument checks remain in handlers because they are interleaved
/// with fallback emission logic (existential bypass/bridge).
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

        // ── Phase 3: Generic type callback gate (thunk closure in PInvokeHelperContext) ──
        // Methods/constructors in generic types that need [UnmanagedCallersOnly] callbacks
        // can't be emitted because callbacks can't be hoisted to the generic helper class.
        if (context?.PInvokeHelperContext != null)
        {
            var closureHandler = new ClosureHandler(_typeDatabase);
            bool hasThunkClosure = methodDecl.CSSignature.Skip(1)
                .Where(arg => closureHandler.IsClosure(arg))
                .Any(arg => closureHandler.RequiresThunk(closureHandler.GetClosureTypeSpec(arg)!));

            if (methodDecl.IsConstructor)
            {
                // Constructor: simple thunk check, no bridge exceptions
                if (hasThunkClosure)
                    return ValidationResult.Skip(SkipReason.GenericTypeCallback,
                        "Constructor requires [UnmanagedCallersOnly] callback inside generic type.");
            }
            else if (!methodDecl.IsProtocolExtensionMethod)
            {
                // Method: thunk or async, with bridge eligibility exceptions
                // Protocol extension methods are always let through (they emit callbacks
                // in a non-generic helper class via PInvokeHelperContext.RawCodeBlocks).
                bool isAsync = methodDecl.IsAsync;
                if (hasThunkClosure || isAsync)
                {
                    // Allow MethodClosureBridge/NestedClosureBridge-eligible methods through —
                    // they hoist callbacks to the helper class like ProtocolExtensionClosureBridge.
                    if (!MethodClosureBridge.IsEligible(methodDecl, closureHandler, _typeDatabase) &&
                        !NestedClosureBridge.IsEligible(methodDecl, closureHandler, _typeDatabase))
                    {
                        return ValidationResult.Skip(SkipReason.GenericTypeCallback,
                            "Member requires [UnmanagedCallersOnly] callback inside generic type.");
                    }
                }
            }
        }

        // ── Phase 4: Protocol constraint gate (non-constructor only) ──
        // Constructors don't check this — C# generic constructors are caught in Phase 6.
        if (!methodDecl.IsConstructor &&
            MethodValidationGates.HasUnsupportedProtocolConstraints(methodDecl, _typeDatabase))
        {
            return ValidationResult.Skip(SkipReason.GenericProtocolConstraint,
                "Method has constraints on protocols with associated types or self requirements.");
        }

        // ── Phase 5: Bound generic gates (non-accessor only) ──
        // Accessors skip these checks — MethodHandler wraps the accessor check in `if (!isAccessor)`.
        // Only checks that are pure skip gates; existential type arg checks stay in handlers
        // because they accumulate state for bypass/bridge fallback logic.
        if (!methodDecl.IsAccessor)
        {
            var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
            foreach (var argument in methodDecl.CSSignature)
            {
                // Bare generic usage (generic declaration used without type arguments)
                if (boundGenericsHandler.HasBareGenericUsage(argument.SwiftTypeSpec, methodDecl.ModuleDecl))
                {
                    return ValidationResult.Skip(SkipReason.UnsupportedSignature,
                        $"Type '{argument.SwiftTypeSpec}' contains generic declaration used without type arguments.");
                }

                if (!boundGenericsHandler.IsBoundGeneric(argument))
                    continue;

                // Non-ISwiftObject bound generic type argument
                if (boundGenericsHandler.HasNonSwiftObjectGenericArg(argument.SwiftTypeSpec))
                {
                    return ValidationResult.Skip(SkipReason.UnsatisfiedGenericConstraint,
                        "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");
                }

                // Unsatisfied generic constraint
                if (boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(argument.SwiftTypeSpec, methodDecl, out var constraintDetails))
                {
                    return ValidationResult.Skip(SkipReason.UnsatisfiedGenericConstraint, constraintDetails);
                }
            }
        }

        // ── Phase 6: Generic constructor own params (constructor only) ──
        // C# does not support generic constructors. If the constructor has method-own
        // generic parameters (not inherited from the parent type), skip it.
        if (methodDecl.IsConstructor && methodDecl.IsGeneric)
        {
            var typeParamNames = methodDecl.ParentDecl is TypeDecl td && td.IsGeneric
                ? new HashSet<string>(td.GenericParameters.Select(p => p.TypeName))
                : new HashSet<string>();
            bool hasMethodOwnGenericParams = methodDecl.GenericParameters
                .Any(p => !typeParamNames.Contains(p.TypeName));
            if (hasMethodOwnGenericParams)
            {
                return ValidationResult.Skip(SkipReason.UnsupportedSignature,
                    "C# does not support generic constructors with method-own type parameters.");
            }
        }

        return ValidationResult.Emit;
    }

    /// <summary>
    /// Validates whether a property should be emitted. Consolidates property-level bound generic
    /// gates that were previously inline in PropertyHandler.Emit.
    /// </summary>
    public ValidationResult ValidatePropertyEmission(PropertyDecl propertyDecl, ValidationContext? context)
    {
        // Bare generic usage (generic declaration used without type arguments)
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        if (boundGenericsHandler.HasBareGenericUsage(propertyDecl.SwiftTypeSpec, propertyDecl.ModuleDecl))
        {
            return ValidationResult.Skip(SkipReason.UnsupportedSignature,
                $"Type '{propertyDecl.SwiftTypeSpec}' contains generic declaration used without type arguments.");
        }

        // Bound generic property type gates
        if (boundGenericsHandler.IsBoundGeneric(propertyDecl))
        {
            // Non-ISwiftObject bound generic type argument
            if (boundGenericsHandler.HasNonSwiftObjectGenericArg(propertyDecl.SwiftTypeSpec))
            {
                return ValidationResult.Skip(SkipReason.UnsatisfiedGenericConstraint,
                    "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.");
            }

            // Unsatisfied generic constraint
            if (boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(propertyDecl.SwiftTypeSpec, propertyDecl, out var constraintDetails))
            {
                return ValidationResult.Skip(SkipReason.UnsatisfiedGenericConstraint, constraintDetails);
            }
        }

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
        if (env.MethodDecl.HasVariadicParameter) return "variadic_parameter";
        if (ConstructorWrapperEmitter.HasVariadicExpansionPattern(env)) return "variadic_expansion_pattern";
        return "unknown";
    }

    #endregion
}
