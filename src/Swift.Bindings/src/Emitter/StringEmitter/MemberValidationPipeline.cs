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
///   Gate 1 — Suppression gates (cheapest, no type resolution):
///     1. @_spi protection
///     2. Implicit+overriding constructor
///     3. Synthesized protocol member
///   Gate 2 — Closure + module gates (via ShouldSkipMethodEmission):
///     4. Synthesized Codable (Encoder/Decoder)
///     5. Unsupported closure parameters (B20)
///     6. SwiftUI/Combine references (B19)
///     7. Async tuple with non-simple enum (C6)
///   Gate 3 — Generic type callback gate (thunk closure in PInvokeHelperContext):
///     8. Constructor: closure requiring thunk in generic type
///     9. Method: closure thunk or async in generic type (with bridge eligibility exceptions)
///   Gate 4 — Protocol constraint gate (non-constructor only):
///     10. Constraints on protocols with associated types or self requirements
///   Gate 5 — Bound generic gates (non-accessor only):
///     11. Bare generic usage in signature
///     12. Non-ISwiftObject bound generic type argument
///     13. Unsatisfied generic constraint
///   Gate 6 — Generic constructor own params (constructor only):
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
        // ── Gate 1: Suppression gates (cheapest, no type resolution) ──

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

        // 3c. Parent type is @usableFromInline internal AND the member shape has no
        // clean direct-CallConvSwift fallback (async / closure-bearing). A public
        // member on an internal parent compiles in Swift, but the only way to call
        // it across the binding is through a wrapper whose body names the internal
        // parent as `self` — and the separate wrapper-compilation module cannot
        // reference an internal type. Sync method/ctor/property/subscript members
        // survive by rejecting just the wrapper and binding a direct CallConvSwift
        // P/Invoke to the exported silgen/Tj symbol (WrapperValidation arm 2b keeps
        // them). Two shapes have no such fallback and must be DROPPED here, at
        // emission, before any wrapper or handler routing is chosen:
        //   * async — always needs a Swift bridge wrapper (the async entry/callback
        //     machinery), which still names the internal parent under @_silgen_name;
        //   * closure-bearing — a closure in EITHER a parameter or the return type
        //     forces the closure-@_cdecl carrier (a closure parameter degrades to a
        //     legacy CallConvSwift path that faults at runtime; a closure RETURN routed
        //     through a direct CallConvSwift P/Invoke crashes Mono and NativeAOT — see
        //     WrapperValidation.IsReturnTypeCdeclRequired), and that wrapper too names
        //     the parent. A closure return is the trap case: it slips a sync member past
        //     this gate into the arm-2b "keep via direct CallConvSwift" path, which then
        //     binds a crashing carrier — so the whole CSSignature (return at index 0 +
        //     parameters) must be scanned, not just the parameters.
        // Dropping is public-API-identical to today's emit-then-strip + C# reconcile,
        // but decided at the emission layer alongside the sync arm-2b decision. Placed
        // before the CSM/generic routing gates because a module-internal parent is a
        // hard blocker no downstream specialization can rescue (a CSM specialization
        // would re-emit code naming the internal parent). Operators take an analogous
        // drop in OperatorHandler.EmitOperator (they never reach this method).
        if (methodDecl.ParentDecl is TypeDecl { IsModuleInternal: true })
        {
            if (methodDecl.IsAsync)
                return ValidationResult.Skip(SkipReason.ParentModuleInternalNoFallback,
                    "Async member on a @usableFromInline internal parent type: its bridge wrapper must name the internal parent and has no direct CallConvSwift fallback.");

            // Scan the whole signature — CSSignature[0] is the return type, Skip(1) the
            // parameters — so a closure RETURN is caught, not only closure parameters.
            var internalParentClosureHandler = new ClosureHandler(_typeDatabase);
            if (methodDecl.CSSignature.Any(internalParentClosureHandler.IsClosure))
                return ValidationResult.Skip(SkipReason.ParentModuleInternalNoFallback,
                    "Closure-bearing member (closure parameter or return) on a @usableFromInline internal parent type: its closure wrapper must name the internal parent and has no direct CallConvSwift fallback.");
        }

        // 3d. The member ITSELF is module-internal (absent from the public swiftinterface)
        // on a PUBLIC parent type — the mirror of the internal-parent gate above. A sync
        // member of this shape survives via a direct CallConvSwift P/Invoke bound to the
        // exported symbol (no wrapper, so internal-ness does not block linkage). But an
        // async or closure-bearing member can only be reached through a Swift bridge
        // wrapper whose body NAMES the member, and that wrapper is compiled as a separate
        // client module (a plain `import`) that cannot reference a member which is not
        // public/SPI-visible. Without this gate the closure-bridge emitters (MCB and the
        // other bridge adapters), which run inside the handler with no visibility check of
        // their own, emit a wrapper that fails to compile. SPI members are already dropped
        // by the @_spi gate above; this gate covers the @usableFromInline / absent-from-
        // interface internal case. Drop before any bridge/handler routing, symmetric to 3c.
        if (methodDecl.IsModuleInternal && methodDecl.ParentDecl is TypeDecl { IsModuleInternal: false })
        {
            if (methodDecl.IsAsync)
                return ValidationResult.Skip(SkipReason.ModuleInternal,
                    "Async member that is itself module-internal: its bridge wrapper, compiled as a separate client module, must name a member it cannot resolve, and there is no direct CallConvSwift fallback for async.");

            var internalMemberClosureHandler = new ClosureHandler(_typeDatabase);
            if (methodDecl.CSSignature.Any(internalMemberClosureHandler.IsClosure))
                return ValidationResult.Skip(SkipReason.ModuleInternal,
                    "Closure-bearing member (closure parameter or return) that is itself module-internal: its closure bridge wrapper, compiled as a separate client module, must name a member it cannot resolve.");
        }

        // 4. Variadic methods — Swift variadic params (T...) appear as Array<T> in ABI JSON.
        // At the ABI level, variadic T... IS Array<T>, so CallConvSwift can dispatch correctly
        // by passing SwiftArray<T> as a single pointer. @_cdecl wrappers cannot call variadic
        // functions (compiler rejects [T] where T... expected), so these methods fall through
        // to CallConvSwift P/Invoke with the original mangled symbol.
        // No gate needed — variadic Array<T> is handled by existing ArrayProjection.

        // 4b. Variadic generic parameter packs (`each R` / `repeat each R`) — distinct from
        // (4) which is value-level variadics. Method-level pack parameters appear in
        // genericSig as type-parameter names like `each R`, which the C# generic-parameter
        // renderer would otherwise turn into invalid identifiers like `Teach R`. C# has no
        // parameter-pack equivalent, so the method is unbindable at signature level.
        if (methodDecl.GenericParameters.Any(GenericTypeEmitter.IsVariadicGenericParameter))
            return ValidationResult.Skip(SkipReason.UnsupportedSignature,
                "Method declares a variadic generic parameter pack (`each ...` / `repeat each ...`) which has no C# equivalent.");

        // ── Gate 2: Closure + module gates (via ShouldSkipMethodEmission) ──
        // Catches: synthesized Codable, unsupported closures (B20), SwiftUI/Combine refs (B19),
        // C6 async tuple with non-simple enum
        var methodSkipReason = MemberEmissionValidator.ShouldSkipMethodEmission(methodDecl, _typeDatabase, out var methodSkipDetails);
        if (methodSkipReason != null)
            return ValidationResult.Skip(methodSkipReason.Value, methodSkipDetails ?? "");

        // ── Gate 3: Generic type callback gate (thunk closure in PInvokeHelperContext) ──
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
                                // CSM-async-generic-parent specialization substitutes the parent
                                // generic with each concrete conformer, so the callback no longer
                                // references an unbound generic param and the rejection rationale
                                // disappears. Route to CSM before falling through to the skip.
                                if (context?.EmissionContext.SpecializationEngine is { } csmEngineForReturnGate &&
                                    ConcreteProtocolSpecializationEmitter.IsCsmAsyncEligibleForGenericParent(
                                        methodDecl, parentTd, _typeDatabase, csmEngineForReturnGate))
                                {
                                    return ValidationResult.RoutedElsewhere(
                                        "Routed to concrete CSM-async specialization (parent-only generic parent extension).");
                                }
                                return ValidationResult.Skip(SkipReason.GenericTypeCallback,
                                    "Async callback references parent generic type parameters in return type.");
                            }
                        }
                        // Pure async with non-generic return — callbacks hoisted by EmitAsyncWrapper
                    }
                    // A closure-bearing member of an inheritance-constrained extension on a
                    // generic class has a natural closed receiver (e.g. `where Base: PixelHost`
                    // → `HostWrapper<PixelHost>`). ClosedConstrainedClosureEmitter surfaces it as
                    // a concrete static extension method + non-generic @_cdecl wrapper at namespace
                    // scope (no CS7042). Route it out of the skip before the bridge check.
                    else if (ClosedConstrainedClosureEmitter.IsEligible(methodDecl, _typeDatabase))
                    {
                        return ValidationResult.RoutedElsewhere(
                            "Routed to closed-instantiation constrained-extension closure specialization.");
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

        // ── Gate 3b: Method-own generic async callback gate ──
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

        // ── Gate 4: Protocol constraint gate (non-constructor only) ──
        // Constructors don't check this — C# generic constructors are caught in Gate 6.

        // Gate 4a: CSM intercept. When a method is eligible for CSM-async specialization,
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

        // Gate 4a (async, generic parent — parent-only): mirrors the sync-generic-parent
        // intercept below, but for async methods with zero method-own generic parameters
        // whose return type substitutes through the parent's associated-type table
        // (e.g. `func respond() async -> Item.Response` on `struct Bag<Item: AsyncBagItem>`).
        // ConcreteProtocolSpecializationEmitter.EmitConcreteSpecializationsForGenericParent
        // emits per-conformer async extensions on the *CsmExtensions class; the open-generic
        // async method on the parent class body would shadow those extensions and route
        // callers into the broken open-generic path. Suppress the open-generic emission.
        //
        // Scoped tight: fires only for parent-only async on generic parents. Closed-conformer
        // async (no parent generics) goes through Gate 4a's first slot above; async methods
        // with method-own generics on generic parents fall through and emit their normal
        // open-generic surface — not handled by this path.
        if (!methodDecl.IsConstructor &&
            methodDecl.ParentDecl is TypeDecl parentTypeForAsyncGenericParent &&
            context?.EmissionContext.SpecializationEngine is { } specEngineForAsyncGenericParent &&
            ConcreteProtocolSpecializationEmitter.IsCsmAsyncEligibleForGenericParent(
                methodDecl, parentTypeForAsyncGenericParent, _typeDatabase, specEngineForAsyncGenericParent))
        {
            return ValidationResult.RoutedElsewhere(
                "Routed to concrete CSM-async specialization (parent-only generic parent extension).");
        }

        // Gate 4a: async method on a generic parent that did NOT route to a
        // CSM specialization above (unconstrained / non-specializable parent generic,
        // e.g. AsyncGenericContainer<T> with an unbounded T). The open-generic async
        // surface emits a @_silgen_name wrapper that is itself a generic instance method:
        // Swift hands `self` and the parent's type metadata through the implicit
        // self / generic-metadata registers, but the fixed C# CallConvSwift P/Invoke can
        // only supply them as trailing IntPtr arguments (TMetadata, _selfClass). Those
        // registers therefore hold garbage and the call SIGSEGVs at runtime — confirmed
        // for AsyncGenericContainer<T>.processAsync / fetchOrThrow. Returning a plain
        // (non-generic) Int32 from such a method does NOT make it safe: the return shape
        // was never the problem, the receiver/metadata ABI is. Only the return-references-T
        // case (Gate 3 above) was previously caught, so these slipped through as live
        // wrong-ABI methods. Suppress so no crashing method ships. Method-own generics are
        // a distinct (also-unbridged) shape left on their existing path. The correct
        // long-term fix is a generic-static-dispatch @_cdecl async bridge that forwards
        // TMetadata + self explicitly (the async analog of the storedValue property
        // getter's _SBW_GSPG machinery).
        if (!methodDecl.IsConstructor &&
            methodDecl.IsAsync &&
            methodDecl.ParentDecl is TypeDecl { IsGeneric: true } &&
            !WrapperValidation.HasMethodOwnGenericParameters(methodDecl))
        {
            return ValidationResult.Skip(SkipReason.GenericTypeCallback,
                "Async method on a generic parent: the wrapper needs the parent's type metadata and self in Swift's implicit registers, which a direct CallConvSwift P/Invoke cannot supply (ABI mismatch -> crash).");
        }

        // Gate 4a (sync, generic parent): CSM emits concrete overloads as extension
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

        // Per-V keypath-sort suppression. A method with a method-own unconstrained V
        // that appears only in a KeyPath Value slot rooted at the parent's PAT
        // associated-type bag would otherwise fall into the GenericProtocolConstraint
        // rejection below. KeyPathBagValueSpecializationEmitter handles this shape by
        // emitting one closed-V Sort overload per (conformer x distinct projectable V)
        // onto a sibling extension class. Suppress the open-V parent-body emission so the
        // C# surface holds only the closed overloads.
        if (!methodDecl.IsConstructor &&
            methodDecl.ParentDecl is TypeDecl parentTypeForRouteC &&
            RouteCSortShapeEligibility.IsRouteCSortShapeEligible(methodDecl, parentTypeForRouteC, out _))
        {
            return ValidationResult.RoutedElsewhere(
                "Routed to Route C per-V KeyPath sort specialization.");
        }

        if (!methodDecl.IsConstructor &&
            MethodValidationGates.HasUnsupportedProtocolConstraints(methodDecl, _typeDatabase))
        {
            return ValidationResult.Skip(SkipReason.GenericProtocolConstraint,
                "Method has constraints on protocols with associated types or self requirements.");
        }

        // Constrained-extension method on a generic parent — same routing as the
        // property-side gate at ValidatePropertyEmission: the open-generic class
        // can't dispatch among per-concrete specializations of the same overload,
        // and the closed-generic mangled symbol is bound to a single instantiation,
        // so we suppress the open-generic emission and let ConstrainedExtensionEmitter
        // re-surface each specialization as a closed-generic extension method.
        // Mirrors the property gate at lines 345-362 below.
        //
        // The emitter only handles a subset (zero-arg sync non-throwing — see
        // IsEmittableConstrainedExtensionMethod). Methods outside that subset
        // still need to be suppressed at the open-generic level, but they should
        // surface as a proper skip with reason rather than RoutedElsewhere —
        // otherwise an unsupported variant disappears from the diagnostic surface
        // entirely.
        if (!methodDecl.IsConstructor &&
            !methodDecl.IsAccessor &&
            !methodDecl.IsSubscriptAccessor &&
            methodDecl.ParentDecl is TypeDecl methodConstrainedParent &&
            methodConstrainedParent.IsGeneric &&
            ConstrainedExtensionEmitter.ExtractSameTypeConstraintForMethod(methodDecl) != null)
        {
            if (ConstrainedExtensionEmitter.IsEmittableConstrainedExtensionMethod(methodDecl))
            {
                return ValidationResult.RoutedElsewhere(
                    $"Constrained-extension method '{methodDecl.Name}' on generic type '{methodConstrainedParent.Name}' is suppressed at the open-generic class level; emitted as a closed-generic extension method via ConstrainedExtensionEmitter.");
            }

            return ValidationResult.Skip(
                SkipReason.UnsupportedSignature,
                $"Constrained-extension method '{methodDecl.Name}' on generic type '{methodConstrainedParent.Name}' is out of scope for ConstrainedExtensionEmitter (initial scope: zero-argument sync non-throwing public methods). Method has parameters, async/throws, or non-public visibility.");
        }

        // Constrained-extension WRAPPER skip on a generic parent — the planning-time
        // mirror of the two emit-time bails in MethodWrapperEmitter.EmitSwiftMethodWrapper.
        // A method whose unconstrained conformance wrapper cannot be emitted must be skipped
        // HERE, before MethodHandler Phase-1 promotes an SBW_ @_cdecl symbol the wrapper-emit
        // never registers — otherwise the C# emit rolls back transactionally and the member
        // surfaces as a mis-classified MissingWrapperSymbol row with a false "stripped during
        // wrapper compilation" workaround. Two arms, distinguished by Details:
        //   (a) an unconstrained extension method collides with a same-name overload on the
        //       parent (the wrapper cannot disambiguate); or
        //   (b) the method carries generic constraints narrower than its parent declares (the
        //       wrapper conformance extension is emitted without a where-clause, so the
        //       constrained method is invisible at the call site — the ObjectMapper
        //       `Mapper where N: ImmutableMappable` shape, which the same-type gate above
        //       misses because ExtractSameTypeConstraintForMethod only sees `where N == …`).
        // The emit-time bails stay as defense-in-depth. Both predicates read only
        // env.MethodDecl + the parent TypeDecl (no marshaled/promoted state), so a throwaway
        // pre-Marshal MethodEnvironment yields the same decision the emitter would.
        if (!methodDecl.IsConstructor &&
            !methodDecl.IsAccessor &&
            !methodDecl.IsSubscriptAccessor &&
            methodDecl.ParentDecl is TypeDecl constrainedWrapperParent &&
            constrainedWrapperParent.IsGeneric)
        {
            var wrapperProbeEnv = new MethodEnvironment(methodDecl, _typeDatabase);
            if (MethodWrapperEmitter.WouldGenericStaticDispatchSkipForExtensionCollision(
                    wrapperProbeEnv, constrainedWrapperParent, out var collisionSwiftName))
            {
                return ValidationResult.Skip(SkipReason.ConstrainedExtensionWrapper,
                    $"Extension method '{collisionSwiftName}' on generic type '{constrainedWrapperParent.Name}' collides with a same-name overload on the parent; the unconstrained conformance wrapper cannot disambiguate (conditional-conformance wrapper not yet supported).");
            }

            if (MethodWrapperEmitter.WouldGenericStaticDispatchSkipForNarrowerConstraint(
                    wrapperProbeEnv, constrainedWrapperParent, out var narrowerSwiftName))
            {
                return ValidationResult.Skip(SkipReason.ConstrainedExtensionWrapper,
                    $"Method '{narrowerSwiftName}' on generic type '{constrainedWrapperParent.Name}' declares generic constraints narrower than its parent; the wrapper conformance extension is unconstrained, so the method is invisible at the call site (conditional-conformance wrapper not yet supported).");
            }
        }

        // ── Gate 5: Bound generic gates (non-accessor only) ──
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

        // ── Gate 5b: Tuple parameters whose elements need per-element marshalling ──
        // PInvokeEmitter emits tuple params as ValueTuple<P/Invoke types>, while the
        // public-facing C# signature uses ValueTuple<idiomatic types>. When ANY element's
        // P/Invoke type differs from its C# type — a class/non-frozen struct (IntPtr vs the
        // class), an existential (ExistentialContainerN vs the I{Composition} interface), a
        // simple enum (underlying int vs the enum), a frozen-mem-mgmt struct (.Buffer vs the
        // struct) — no per-element conversion is threaded to the call site, so the standard
        // ValueTuple path passes the raw public-typed tuple and CS1503s (fail-open today).
        //
        // The CdeclTuple buffer path (PInvokeEmitter ~L557) handles tuples whose every element has a
        // fixed-size, ABI-faithful slot representation — IsCdeclBufferMarshallableTuple: all-primitive
        // (written by value) and pure-Swift-class elements (written as their object handle into the
        // pointer-width slot). Frozen-blittable/pointer tuples also pass raw with no conversion
        // (P/Invoke type == C# type, so HasUnmarshalledTupleElements is already false for them).
        // The remainder — existential / simple-enum / non-frozen-or-frozen-mem struct elements, whose
        // per-element conversion + lifetime is not yet implemented — is flagged by
        // HasUnmarshalledTupleElements AND NOT buffer-marshallable, and fails closed here. Broader than
        // the closure-side HasClosureUnsafeTupleElements (IntPtr subset only); at the method/ctor level.
        var tupleHandler = new TupleHandler(_typeDatabase);
        foreach (var argument in methodDecl.CSSignature.Skip(1))
        {
            if (!tupleHandler.IsTuple(argument))
                continue;
            var tupleSpec = tupleHandler.GetTupleTypeSpec(argument)!;
            if (tupleHandler.HasUnmarshalledTupleElements(tupleSpec) &&
                !tupleHandler.IsCdeclBufferMarshallableTuple(tupleSpec))
            {
                return ValidationResult.Skip(SkipReason.UnsupportedSignature,
                    $"Tuple parameter '{argument.Name}' has elements whose P/Invoke type differs from the C# type — per-element marshalling for convertible-element tuple parameters is not yet implemented.");
            }
        }

        // ── Gate 6: Generic constructor own params (constructor only) ──
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

            // Dependent-member same-type constraint mirror of MemberEmissionValidator.CanEmitProperty:
            // a property declared on `extension Parent where T.Assoc == Concrete` is only
            // satisfiable for one specific parent instantiation, but the constraint targets
            // an associated type rather than the parent generic argument itself. The
            // closed-extension path cannot re-surface it (no single concrete parent type arg
            // satisfies the constraint), and emitting at the open-generic level produces an
            // unsatisfiable `_SBW_PG_*` conformance extension that fails the Swift wrapper
            // build. Drop it from emission entirely.
            if (ConstrainedExtensionEmitter.HasParentExtensionSameTypeConstraint(propertyDecl))
            {
                return ValidationResult.Skip(SkipReason.UnsupportedType,
                    $"Constrained-extension property '{propertyDecl.Name}' on generic type '{constrainedExtensionParent.Name}' requires a dependent-member same-type constraint on a parent associated type (e.g. `where Value.ValueType == Concrete`); not re-surfaceable as a closed-generic extension method and would emit an unsatisfiable protocol-group conformance at the open-generic level.");
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

        // @objc protocol existential nested in a container/tuple/closure — unsupported ABI.
        // Bare `any P` / `Optional<any P>` property types marshal correctly (single ObjC object
        // pointer) and are NOT caught here; only nested positions route through the
        // ExistentialContainer1 carrier that fails for @objc. Concrete-path mirror of the
        // CanEmitProperty gate. Fail closed.
        if (ExistentialHandler.HasUnsupportedObjCProtocolExistentialPosition(propertyDecl.SwiftTypeSpec, _typeDatabase))
        {
            return ValidationResult.Skip(SkipReason.UnsupportedExistential,
                "Property has an @objc protocol existential in an unsupported nested position (container/tuple/closure); only bare `any P` / `Optional<any P>` are supported.");
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
        // Member-level visibility gate (mirrors the method/property paths). A
        // @usableFromInline internal or @_spi subscript with an all-public signature would
        // otherwise emit a C# indexer whose Swift-side @_cdecl wrapper references a symbol
        // the module doesn't export, which fails wrapper compilation.
        if (subscriptDecl.IsSpiProtected)
            return ValidationResult.Skip(SkipReason.ModuleInternal, "@_spi subscript suppressed from bindings.");

        if (subscriptDecl.IsModuleInternal)
            return ValidationResult.Skip(SkipReason.ModuleInternal, "Internal subscript suppressed from bindings.");

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
    /// Routes through the single eligibility traversal on the method/constructor wrapper emitter
    /// (Finding 12), so the predict decision and its rejection reason share one source of truth.
    /// </summary>
    public WrapperValidationResult ValidateMethodWrapperEligibility(MethodEnvironment env)
    {
        var eligibility = env.MethodDecl.IsConstructor
            ? ConstructorWrapperEmitter.EvaluateWrapperEligibility(env)
            : MethodWrapperEmitter.EvaluateWrapperEligibility(env);
        return eligibility.IsWrappable
            ? WrapperValidationResult.Wrap
            : WrapperValidationResult.Reject(eligibility.Reason!);
    }

    /// <summary>
    /// Validates whether a property accessor should have a @_cdecl wrapper generated.
    /// Routes through <see cref="PropertyWrapperEmitter.EvaluateWrapperEligibility"/> (Finding 12).
    /// </summary>
    public WrapperValidationResult ValidatePropertyWrapperEligibility(PropertyDecl propertyDecl, MethodEnvironment accessorEnv)
    {
        var eligibility = PropertyWrapperEmitter.EvaluateWrapperEligibility(propertyDecl, accessorEnv);
        return eligibility.IsWrappable
            ? WrapperValidationResult.Wrap
            : WrapperValidationResult.Reject(eligibility.Reason!);
    }

    /// <summary>
    /// Validates whether a subscript accessor should have a @_cdecl wrapper generated.
    /// Routes through <see cref="SubscriptWrapperEmitter.EvaluateWrapperEligibility"/> (Finding 12).
    /// </summary>
    public WrapperValidationResult ValidateSubscriptWrapperEligibility(SubscriptDecl subscriptDecl, AccessorDecl accessor, MethodEnvironment env)
    {
        var eligibility = SubscriptWrapperEmitter.EvaluateWrapperEligibility(subscriptDecl, accessor, env);
        return eligibility.IsWrappable
            ? WrapperValidationResult.Wrap
            : WrapperValidationResult.Reject(eligibility.Reason!);
    }

    #endregion
}
