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

        // 3b. Pattern 2 emission-time gate: signature reaches a @usableFromInline
        // internal (or otherwise-suppressed) type that the Swift wrapper cannot
        // legally expose. Replaces the dominant Pattern 2 cleanup pass — the
        // wrapper post-processor stays in place as a safety net for body-reference
        // shapes the signature walk can't predict.
        if (TryCheckInternalTypeReach(methodDecl, out var methodSkip))
            return methodSkip!;

        // 4. Variadic methods — Swift variadic params (T...) appear as Array<T> in ABI JSON.
        // At the ABI level, variadic T... IS Array<T>, so CallConvSwift can dispatch correctly
        // by passing SwiftArray<T> as a single pointer. @_cdecl wrappers cannot call variadic
        // functions (compiler rejects [T] where T... expected), so these methods fall through
        // to CallConvSwift P/Invoke with the original mangled symbol.
        // No gate needed — variadic Array<T> is handled by existing ArrayProjection.

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
            var closureParamCount = methodDecl.CSSignature.Skip(1).Count(closureHandler.IsClosure);
            bool hasThunkClosure = methodDecl.CSSignature.Skip(1)
                .Where(arg => closureHandler.IsClosure(arg))
                .Any(arg => closureHandler.RequiresThunk(closureHandler.GetClosureTypeSpec(arg)!, methodDecl.MangledName, closureParamCount));

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
                    // Async methods in generic types: completion callbacks are hoisted
                    // to the PInvokeHelper class by EmitAsyncWrapper. Allow through
                    // unless they ALSO have thunk closures that can't be bridged,
                    // OR the return type references parent generic type parameters
                    // (callbacks can't use generic params since [UnmanagedCallersOnly]
                    // methods and non-generic helper classes can't be parameterized).
                    if (isAsync && !hasThunkClosure)
                    {
                        // Check if return type involves parent generic type parameters
                        if (methodDecl.ParentDecl is TypeDecl { IsGeneric: true } parentTd)
                        {
                            var parentParamNames = new HashSet<string>(
                                parentTd.GenericParameters.Select(p => p.TypeName));
                            var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
                            if (TypeSpecHelpers.ContainsAnyTypeName(returnTypeSpec, parentParamNames))
                            {
                                return ValidationResult.Skip(SkipReason.GenericTypeCallback,
                                    "Async callback references parent generic type parameters in return type.");
                            }
                        }
                        // Pure async with non-generic return — callbacks hoisted by EmitAsyncWrapper
                    }
                    // Allow MethodClosureBridge/NestedClosureBridge-eligible methods through —
                    // they hoist callbacks to the helper class like ProtocolExtensionClosureBridge.
                    else if (!MethodClosureBridge.IsEligible(methodDecl, closureHandler, _typeDatabase) &&
                        !NestedClosureBridge.IsEligible(methodDecl, closureHandler, _typeDatabase))
                    {
                        return ValidationResult.Skip(SkipReason.GenericTypeCallback,
                            "Member requires [UnmanagedCallersOnly] callback inside generic type.");
                    }
                }
            }
        }

        // ── Phase 3b: Method-own generic async callback gate ──
        // Async methods emit a [UnmanagedCallersOnly] completion callback whose body
        // references the return-type generics via MarshalFromSwift<T>. The callback
        // lands at class scope (non-generic parent) or in a non-generic PInvokeHelper
        // class (generic parent) — neither scope can see method-local generic params.
        // If the return type references any method-own generic, the callback won't
        // compile (CS0246). Skip these methods.
        if (methodDecl.IsAsync && methodDecl.IsGeneric)
        {
            var parentTypeParamNames = methodDecl.ParentDecl is TypeDecl parentForAsync && parentForAsync.IsGeneric
                ? new HashSet<string>(parentForAsync.GenericParameters.Select(p => p.TypeName))
                : new HashSet<string>();
            var methodOwnParamNames = new HashSet<string>(
                methodDecl.GenericParameters
                    .Where(p => !parentTypeParamNames.Contains(p.TypeName))
                    .Select(p => p.TypeName));
            if (methodOwnParamNames.Count > 0)
            {
                var returnTypeSpec = methodDecl.CSSignature[0].SwiftTypeSpec;
                if (TypeSpecHelpers.ContainsAnyTypeName(returnTypeSpec, methodOwnParamNames))
                {
                    return ValidationResult.Skip(SkipReason.GenericTypeCallback,
                        "Async callback references method-own generic type parameters.");
                }
            }
        }

        // ── Phase 4: Protocol constraint gate (non-constructor only) ──
        // Constructors don't check this — C# generic constructors are caught in Phase 6.

        // Phase 4a: CSM intercept. When a method is eligible for CSM-async specialization,
        // skip the unspecialized generic emission so only the concrete overloads appear.
        // Fires for async methods whose constraint protocol has hint conformers — these
        // wouldn't be caught by HasUnsupportedProtocolConstraints when the protocol is
        // not registered in TypeDatabase (e.g., Swift.Collection).
        if (!methodDecl.IsConstructor &&
            methodDecl.ParentDecl is TypeDecl parentTypeForCsm &&
            context?.EmissionContext.SpecializationEngine is { } specEngineForCsm &&
            ConcreteProtocolSpecializationEmitter.IsCsmAsyncEligible(
                methodDecl, parentTypeForCsm, _typeDatabase, specEngineForCsm,
                context.EmissionContext))
        {
            // The open-generic form is intentionally suppressed; per-conformer concrete
            // specializations are emitted by ConcreteProtocolSpecializationEmitter and
            // expose the supported public surface. This is NOT an unsupported outcome —
            // do not emit `// Unsupported:` or record as skipped.
            return ValidationResult.RoutedElsewhere(
                "Routed to concrete CSM-async specialization.");
        }

        // Phase 4a (sync, generic parent): CSM emits concrete overloads as extension
        // methods on a {Type}{ParentConformer}CsmExtensions class. The open-generic
        // instance method on the parent class would shadow those extensions during C#
        // overload resolution (instance methods win over extensions), routing callers
        // into the broken open-generic path. Suppress the open-generic emission so the
        // CSM extension binds cleanly via instance-call syntax.
        //
        // Scoped tight: fires only for generic-parent cases. Non-generic-parent sync
        // CSM emits concrete overloads as instance methods (no shadow) and goes through
        // the normal emission path.
        if (!methodDecl.IsConstructor &&
            methodDecl.ParentDecl is TypeDecl parentTypeForSyncCsm &&
            context?.EmissionContext.SpecializationEngine is { } specEngineForSyncCsm &&
            ConcreteProtocolSpecializationEmitter.IsCsmSyncEligibleForGenericParent(
                methodDecl, parentTypeForSyncCsm, _typeDatabase, specEngineForSyncCsm))
        {
            // The open-generic form is intentionally suppressed; per-conformer concrete
            // overloads are emitted as extension methods on a {Type}{ParentConformer}CsmExtensions
            // class and shadow-resolve via static dispatch. This is NOT an unsupported outcome —
            // do not emit `// Unsupported:` or record as skipped.
            return ValidationResult.RoutedElsewhere(
                "Routed to concrete CSM-sync specialization (generic parent extension).");
        }

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
            // CSSignature[0] is the return slot; the rest are parameters. Parameter-direction
            // generics have stricter gates (e.g., Swift.Result is unsupported outbound).
            for (int i = 0; i < methodDecl.CSSignature.Count; i++)
            {
                var argument = methodDecl.CSSignature[i];
                bool isParameterPosition = i != 0;

                // Bare generic usage (generic declaration used without type arguments)
                if (boundGenericsHandler.HasBareGenericUsage(argument.SwiftTypeSpec, methodDecl.ModuleDecl))
                {
                    return ValidationResult.Skip(SkipReason.UnsupportedSignature,
                        $"Type '{argument.SwiftTypeSpec}' contains generic declaration used without type arguments.");
                }

                if (!boundGenericsHandler.IsBoundGeneric(argument))
                    continue;

                // Non-ISwiftObject bound generic type argument
                if (boundGenericsHandler.HasNonSwiftObjectGenericArg(argument.SwiftTypeSpec, isParameterPosition))
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

        // ── Phase 5b: Tuple parameters with P/Invoke-vs-C# element type mismatch ──
        // PInvokeEmitter emits tuple params as ValueTuple<P/Invoke types>, while the
        // public-facing C# signature uses ValueTuple<idiomatic types>. When element
        // P/Invoke type is IntPtr but C# type is a class/struct (e.g., a non-frozen
        // Swift struct projected as a class with .Payload), no per-element conversion
        // is generated — the call site passes the raw class tuple and CS1503s.
        //
        // The CdeclTuple buffer path (PInvokeEmitter ~L517) already gates this via
        // IsCdeclSafeTuple (primitives only). The standard ValueTuple path has no such
        // gate — it just emits broken code. This Phase mirrors the closure-side check
        // (TupleHandler.HasClosureUnsafeTupleElements) at the method/ctor level.
        var tupleHandler = new TupleHandler(_typeDatabase);
        foreach (var argument in methodDecl.CSSignature.Skip(1))
        {
            if (!tupleHandler.IsTuple(argument))
                continue;
            var tupleSpec = tupleHandler.GetTupleTypeSpec(argument)!;
            if (tupleHandler.HasClosureUnsafeTupleElements(tupleSpec))
            {
                return ValidationResult.Skip(SkipReason.UnsupportedSignature,
                    $"Tuple parameter '{argument.Name}' has elements whose P/Invoke type (IntPtr) differs from the C# type — per-element marshalling is not yet implemented for tuple-of-class/struct.");
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
        // Pattern 2 emission-time gate (mirrors ValidateMethodEmission). Property
        // accessors would be emitted with a @_cdecl wrapper that exposes the
        // property's declared type — if that type is internal, the wrapper won't
        // compile.
        if (TryCheckInternalTypeReach(propertyDecl, out var propertySkip))
            return propertySkip!;

        // Constrained-extension multi-specialization conflict — see
        // MemberEmissionValidator.CanEmitProperty for the full rationale. PropertyHandler.Emit
        // routes through this pipeline (not CanEmitProperty), so the gate must be repeated here
        // to keep the two paths in sync.
        if (propertyDecl.ParentDecl is TypeDecl constrainedExtensionParent && constrainedExtensionParent.IsGeneric)
        {
            int siblingCount = 0;
            foreach (var sibling in constrainedExtensionParent.Properties)
            {
                if (sibling.Name == propertyDecl.Name && sibling.IsStatic == propertyDecl.IsStatic)
                {
                    siblingCount++;
                    if (siblingCount > 1)
                        break;
                }
            }
            if (siblingCount > 1)
            {
                return ValidationResult.Skip(SkipReason.UnsupportedType,
                    $"Multiple constrained-extension specializations of '{propertyDecl.Name}' on generic type '{constrainedExtensionParent.Name}' cannot be dispatched via C# generics.");
            }
        }

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

        // P8 (concrete-side mirror): Optional existential whose inner protocol is not in
        // the TypeDatabase. ExistentialHandler.GetPublicExistentialType falls back to
        // "object", which means we can't faithfully marshal
        // SwiftOptional<ExistentialContainer> into a meaningful C# nullable type. The
        // protocol-side equivalent lives in MemberGateEvaluator.EvaluateProperty (also
        // tagged P8) — both paths must agree, otherwise the conforming class skips the
        // property while the protocol interface still declares it as `object?` and we
        // get CS0535.
        //
        // PropertyHandler.cs:227-238 has an inline copy of this same check that catches
        // it during emission; centralizing it here means the inline guard is now a
        // belt-and-braces backstop rather than the sole authoritative gate. New callers
        // of ValidatePropertyEmission inherit the protection automatically.
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        if (existentialHandler.IsOptionalExistential(propertyDecl.SwiftTypeSpec))
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(propertyDecl.SwiftTypeSpec);
            if (innerProtocolList != null &&
                existentialHandler.GetPublicExistentialType(innerProtocolList) == "object")
            {
                return ValidationResult.Skip(SkipReason.AnyTypeFallback,
                    "Optional existential inner protocol not in TypeDatabase — falls back to object.");
            }
        }

        return ValidationResult.Emit;
    }

    /// <summary>
    /// Validates whether a subscript should be emitted. Today this is the Pattern 2
    /// emission-time gate; other subscript validation (AnyType, complex index params,
    /// dedup) lives in <c>SubscriptHandler.EmitSubscripts</c>.
    /// </summary>
    public ValidationResult ValidateSubscriptEmission(SubscriptDecl subscriptDecl, ValidationContext? context)
    {
        if (TryCheckInternalTypeReach(subscriptDecl, out var subscriptSkip))
            return subscriptSkip!;

        return ValidationResult.Emit;
    }

    /// <summary>
    /// Shared Pattern 2 emission-time predicate — signature reaches a name in
    /// <see cref="ModuleDecl.InternalTypeNames"/>. No-ops when the module hasn't
    /// populated the set (e.g. unit tests that construct ModuleDecls directly), so
    /// existing tests stay green.
    /// </summary>
    private static bool TryCheckInternalTypeReach(MethodDecl methodDecl, out ValidationResult? skip)
    {
        skip = null;
        var internalTypeNames = methodDecl.ModuleDecl?.InternalTypeNames;
        var moduleName = methodDecl.ModuleDecl?.Name;
        if (internalTypeNames is null || internalTypeNames.Count == 0 || string.IsNullOrEmpty(moduleName))
            return false;
        var effectiveNames = ExcludeParentTypeNamesForWrapperFreeMethod(internalTypeNames, methodDecl);
        if (effectiveNames.Count == 0)
            return false;
        if (!InternalTypeReferenceWalker.SignatureReachesInternalType(methodDecl, effectiveNames, moduleName))
            return false;
        skip = ValidationResult.Skip(SkipReason.Pattern2InternalTypeReach,
            "Signature reaches a @usableFromInline internal (or otherwise-suppressed) type; Swift wrapper cannot expose it.");
        return true;
    }

    private static bool TryCheckInternalTypeReach(PropertyDecl propertyDecl, out ValidationResult? skip)
    {
        skip = null;
        var internalTypeNames = propertyDecl.ModuleDecl?.InternalTypeNames;
        var moduleName = propertyDecl.ModuleDecl?.Name;
        if (internalTypeNames is null || internalTypeNames.Count == 0 || string.IsNullOrEmpty(moduleName))
            return false;
        if (!InternalTypeReferenceWalker.SignatureReachesInternalType(propertyDecl, internalTypeNames, moduleName))
            return false;
        skip = ValidationResult.Skip(SkipReason.Pattern2InternalTypeReach,
            "Property type reaches a @usableFromInline internal (or otherwise-suppressed) type; Swift wrapper cannot expose it.");
        return true;
    }

    private static bool TryCheckInternalTypeReach(SubscriptDecl subscriptDecl, out ValidationResult? skip)
    {
        skip = null;
        var internalTypeNames = subscriptDecl.ModuleDecl?.InternalTypeNames;
        var moduleName = subscriptDecl.ModuleDecl?.Name;
        if (internalTypeNames is null || internalTypeNames.Count == 0 || string.IsNullOrEmpty(moduleName))
            return false;
        if (!InternalTypeReferenceWalker.SignatureReachesInternalType(subscriptDecl, internalTypeNames, moduleName))
            return false;
        skip = ValidationResult.Skip(SkipReason.Pattern2InternalTypeReach,
            "Subscript signature reaches a @usableFromInline internal (or otherwise-suppressed) type; Swift wrapper cannot expose it.");
        return true;
    }

    /// <summary>
    /// Removes the parent type's short and module-qualified names from the effective
    /// internal-type set ONLY when the method is a failable initializer on a non-frozen
    /// struct — the one shape that <see cref="ConstructorWrapperEmitter"/> skips and
    /// routes through direct CallConvSwift to the dylib's mangled symbol (see
    /// <c>ConstructorWrapperEmitter.cs</c> lines 39-46). For that wrapper-free path a
    /// signature mentioning only Self does not require any Swift wrapper to reference
    /// the internal parent type. All other member kinds (non-failable inits,
    /// non-constructor methods, properties, subscripts) emit a <c>@_cdecl</c> wrapper
    /// whose body reconstructs <c>self</c> using the parent's module-qualified Swift
    /// name (<c>assumingMemoryBound(to: T.self)</c> / <c>Unmanaged&lt;T&gt;</c>), so the
    /// gate must still fire for them to avoid producing wrappers that fail to compile.
    /// </summary>
    internal static IReadOnlySet<string> ExcludeParentTypeNamesForWrapperFreeMethod(
        IReadOnlySet<string> internalTypeNames, MethodDecl methodDecl)
    {
        if (!methodDecl.IsConstructor || !methodDecl.IsFailable)
            return internalTypeNames;
        if (methodDecl.ParentDecl is not StructDecl structDecl || structDecl.IsFrozen)
            return internalTypeNames;

        var shortName = structDecl.Name;
        var qualifiedName = structDecl.SwiftTypeName?.ToString();
        bool excludesShort = !string.IsNullOrEmpty(shortName) && internalTypeNames.Contains(shortName);
        bool excludesQualified = !string.IsNullOrEmpty(qualifiedName) && internalTypeNames.Contains(qualifiedName);
        if (!excludesShort && !excludesQualified)
            return internalTypeNames;

        var copy = new HashSet<string>(internalTypeNames);
        if (excludesShort)
            copy.Remove(shortName);
        if (excludesQualified)
            copy.Remove(qualifiedName!);
        return copy;
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
            // Constructors still reject all async closures — the async-closure bridge
            // (Session A) is wired only through the async method wrapper path.
            if (env.MethodDecl.CSSignature.Skip(1)
                    .Where(env.ClosureHandler.IsClosure)
                    .Any(arg =>
                    {
                        var spec = env.ClosureHandler.GetClosureTypeSpec(arg);
                        return spec != null && env.ClosureHandler.IsAsyncClosure(spec);
                    }))
                return "async_closure_params";
        }
        if (env.MethodDecl.IsAsync) return "async_constructor";
        if (WrapperValidation.IsNonCopyableStructParent(env.ParentDecl)) return "non_copyable_struct_parent";
        if (env.MethodDecl.CSSignature.Skip(1)
                .Any(a => WrapperValidation.IsMetatypeTypeIncludingOptional(a.SwiftTypeSpec)))
            return "metatype_param";
        if (ConstructorWrapperEmitter.HasNonCopyableStructParameter(env)) return "non_copyable_struct_parameter";
        if (ConstructorWrapperEmitter.HasNestedFrozenStructParameter(env)) return "nested_frozen_struct_parameter";
        if (ConstructorWrapperEmitter.HasUnsupportedBufferPointerParameter(env)) return "unsupported_buffer_pointer_parameter";
        if (WrapperValidation.HasRawGenericTypeParams(env.MethodDecl)) return "raw_generic_type_params";
        if (env.MethodDecl.HasVariadicParameter) return "variadic_parameter";
        if (ConstructorWrapperEmitter.HasVariadicExpansionPattern(env)) return "variadic_expansion_pattern";
        return "unknown";
    }

    #endregion
}
