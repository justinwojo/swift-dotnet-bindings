// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-method @_cdecl Swift wrappers that route instance/static method P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each method, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (primitives pass through, structs/classes as pointers)
/// - Reconstructs self for instance methods (class: Unmanaged, struct: load/pointer)
/// - Calls the actual Swift method
/// - Returns the result via C ABI (class → retained pointer, struct → writes to result buffer)
///
/// Handles throwing methods, mutating struct methods, string returns (SBW_Utf8Slice),
/// and static methods. Follows the ConstructorWrapperEmitter/PropertyWrapperEmitter pattern.
/// State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class MethodWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a method should use a @_cdecl wrapper.
    /// Guards: xcframework mode, non-generic parent, non-constructor, non-accessor,
    /// no closures, no protocol existentials, no async, etc.
    /// </summary>
    public static bool ShouldEmitWrapper(MethodEnvironment env)
        => EvaluateWrapperEligibility(env).IsWrappable;

    /// <summary>
    /// Single eligibility traversal for method @_cdecl wrappers: returns whether the method
    /// will be wrapped and, if not, the first guard that rejected it. <see cref="ShouldEmitWrapper"/>
    /// is its boolean shim, so the predicate and the rejection diagnostic can never drift (Finding 12).
    /// </summary>
    public static WrapperEligibility EvaluateWrapperEligibility(MethodEnvironment env)
    {
        // 1. Must NOT be a constructor (constructors handled by ConstructorWrapperEmitter)
        if (env.MethodDecl.IsConstructor)
            return WrapperEligibility.Reject("constructor");

        // 2. Must NOT be an accessor (property accessors handled by PropertyWrapperEmitter; subscripts deferred)
        if (env.MethodDecl.IsAccessor)
            return WrapperEligibility.Reject("accessor");

        // 3. Must NOT already have a cdecl property wrapper
        if (env.MethodDecl.UsesCdeclPropertyWrapper)
            return WrapperEligibility.Reject("cdecl_property_wrapper");

        // Shared guards: xcframework, internal, SPI, non-copyable, async, actor, inherited generic context
        var memberReason = WrapperValidation.GetMemberRejectionReason(env, MemberKind.Method,
            isModuleInternal: env.MethodDecl.IsModuleInternal,
            isSpiProtected: env.MethodDecl.IsSpiProtected,
            isAsync: env.MethodDecl.IsAsync,
            isActorIsolated: env.MethodDecl.IsActorIsolated,
            isMainActorIsolated: env.MethodDecl.IsMainActorIsolated,
            isNonisolated: env.MethodDecl.IsNonisolated);
        if (memberReason != null)
            return WrapperEligibility.Reject(memberReason);

        // 5. Must be on a type or module (free function)
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl == null && env.ParentDecl is not ModuleDecl)
            return WrapperEligibility.Reject("no_parent");

        // 5b. Generic parent type — allow methods using protocol-based type erasure.
        // (inherited generic context is already checked by CanEmitMember)
        if (parentTypeDecl?.IsGeneric == true)
        {
            if (!CanEmitGenericWrapper(env, parentTypeDecl))
                return WrapperEligibility.Reject("generic_parent");
            // Inout on generic parent: the protocol static-dispatch path handles concrete
            // (non-T-referencing) inout params by threading UnsafeMutableRawPointer through
            // the protocol boundary and doing the load/call/write-back inside the extension
            // body. Inout params whose type references a parent generic parameter can't
            // bind a concrete typed pointer, so those fall back to CallConvSwift.
            // HasInoutWithAbiMismatch (step 11b below) still rejects String/class/non-frozen
            // inout types that don't map cleanly onto UnsafeMutableRawPointer.
            var parentGenericParamNames = parentTypeDecl.GenericParameters
                .Select(p => p.TypeName)
                .ToHashSet();
            if (env.MethodDecl.CSSignature.Skip(1).Any(a => a.IsInOut &&
                WrapperValidation.TypeSpecReferencesGenericParam(a.SwiftTypeSpec, parentGenericParamNames)))
                return WrapperEligibility.Reject("generic_parent_inout");
        }

        // 6. No method-level generics (e.g., func pair<T,U>(...)).
        // MethodDecl.IsGeneric is true for ALL methods on generic types because the ABI JSON
        // includes the parent's generic signature in each method's GenericSig. Only block methods
        // that have their OWN generic parameters (not inherited from the parent type).
        if (WrapperValidation.HasMethodOwnGenericParameters(env.MethodDecl))
            return WrapperEligibility.Reject("method_level_generics");

        // 8. Closure parameters: allowed only when NeedsClosureCdeclWrapper validates them
        // AND no unsupported async closures. Baseline-shape async-throwing closures
        // (`() async throws -> T` with T a blittable primitive) are bridged via
        // the async wrapper's withCheckedThrowingContinuation harness, so they fall
        // outside the "unsupported" bucket.
        if (env.MethodDecl.CSSignature.Skip(1).Any(env.ClosureHandler.IsClosure))
        {
            if (!ClosureEmitter.NeedsClosureCdeclWrapper(env.MethodDecl, env.ClosureHandler))
                return WrapperEligibility.Reject("closure_params");
            // Distinct from the guard above: the closure shape itself is bridgeable, but it is
            // `async` in a position the wrapper harness cannot start. Sharing one token here
            // would collapse two different consumer-facing causes into one histogram bucket
            // and one [Obsolete] sentence.
            if (HasUnsupportedAsyncClosure(env))
                return WrapperEligibility.Reject("async_closure");
        }

        // 11b. Inout params with types that have C# ABI mismatch (String → 2 words, class → Unmanaged, etc.)
        if (WrapperValidation.HasInoutWithAbiMismatch(env))
            return WrapperEligibility.Reject("inout_abi_mismatch");

        // 11c. Variadic parameters are supported via the unsafeBitCast bridge when the shape is
        // simple (static on non-generic parent, no throws, no closures, no inout, no method-own
        // generics). The wrapper assigns the variadic Swift method to a function reference of
        // type `(T...) -> R`, then bitCasts to `([T]) -> R` and calls with the runtime array.
        // Variadic-pack and traditional variadic share ABI (both lower to Array<T>); the type
        // system tracks the variadic-ness but the call lowering is the same. Covers the
        // AppShortcutsBuilder.buildBlock sites. Unsupported variadic shapes still fall back
        // to CallConvSwift P/Invoke.
        if (env.MethodDecl.HasVariadicParameter && !IsSupportedVariadicShape(env))
            return WrapperEligibility.Reject("variadic_params");

        // 11d. Parameters with Swift's `_const` modifier require a compile-time-constant
        // literal at the call site. The @_cdecl wrapper would forward a runtime value;
        // Swift rejects the call with "expect a compile-time constant literal". ABI JSON
        // strips this annotation — the flag is sourced from the swiftinterface.
        if (env.MethodDecl.CSSignature.Skip(1).Any(a => a.IsConstLiteral))
            return WrapperEligibility.Reject("const_literal");

        // 12. No nested frozen struct parameters
        if (HasNestedFrozenStructParameter(env))
            return WrapperEligibility.Reject("nested_frozen_struct_param");

        // 12b. Non-primitive frozen struct parameters are now handled via UnsafeRawPointer
        // in @_cdecl wrappers — no longer a skip reason.

        // 13. Not already using wrapper library (DebugParam, ArraySlice, etc. own the wrapper)
        if (env.MethodDecl.UsesWrapperLibrary)
            return WrapperEligibility.Reject("uses_wrapper_library");

        // 14-15d. Type-signature checks (metatype, opaque, DynamicSelf, unsupported generics).
        var typeSigReason = GetUnsupportedTypeSignatureReason(env);
        if (typeSigReason != null)
            return WrapperEligibility.Reject(typeSigReason);

        // 17. Nested type returns — ALLOWED. @_cdecl wrapper return types use C-compatible types
        //     (Int32 for simple enums, void+resultPtr for indirect results, UnsafeMutableRawPointer
        //     for class pointers). The nested type only appears in the function BODY.

        return WrapperEligibility.Wrappable;
    }

    /// <summary>
    /// Returns true if the method has any unsupported type signature for @_cdecl wrapping.
    /// Covers: unsupported generic containers (Result, Optional&lt;existential&gt;), metatype
    /// params/return, and DynamicSelf on non-class parents.
    /// </summary>
    internal static bool HasUnsupportedTypeSignature(MethodEnvironment env)
        => GetUnsupportedTypeSignatureReason(env) != null;

    /// <summary>
    /// Reason-returning twin of <see cref="HasUnsupportedTypeSignature"/>: names the first
    /// unsupported type-signature condition, or null when the signature is supported.
    /// <see cref="HasUnsupportedTypeSignature"/> is its boolean shim (Finding 12).
    /// </summary>
    internal static string? GetUnsupportedTypeSignatureReason(MethodEnvironment env)
    {
        // 14. No unsupported generic container params/returns (Array, Dictionary, Set, Optional<existential>).
        //     Optional<value-type> allowed (IndirectResult). Optional<existential> blocked (needs proxy).
        if (HasUnsupportedGenericContainerParamsOrReturn(env))
            return "unsupported_generic_container";

        // 14b. No metatype parameters (Any.Type, T.Type) — not C-representable, renders as bare "Type".
        //      Includes Optional<Metatype> (e.g. AnyClass.Type?) which would otherwise be
        //      misclassified by IsProtocolExistentialType and emitted as "any AnyClass.Type".
        if (env.MethodDecl.CSSignature.Skip(1).Any(a => WrapperValidation.IsMetatypeTypeIncludingOptional(a.SwiftTypeSpec)))
            return "metatype_param";

        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;

        // 14c. No metatype return types (including Optional<Metatype>)
        if (WrapperValidation.IsMetatypeTypeIncludingOptional(returnSpec))
            return "metatype_return";

        // 15. Opaque return types (some Protocol): ALLOWED — routed through IndirectResult.
        // The @_cdecl wrapper boxes `some Protocol` into `any Protocol` via
        // initializeMemory(as: (any Protocol).self). ExistentialBypassEmitter renders
        // ProtocolListTypeSpec{IsOpaque:true} as "any Protocol" automatically.

        // 15b. Closure returns: allowed — routed through IndirectResult (resultPtr buffer).
        // @_cdecl wrapper writes closure to resultPtr via initializeMemory; C# reads SwiftClosureData.

        // 15c. Tuple returns: allowed — routed through IndirectResult (resultPtr buffer).
        // @_cdecl wrapper writes tuple to resultPtr via initializeMemory(as: (T1, T2).self).

        // 15d. DynamicSelf returns: allowed for class parents — Self resolves to parent class type.
        // @_cdecl wrapper returns Unmanaged.passRetained(result).toOpaque() (class pointer).
        // Structs/enums with DynamicSelf blocked — Unmanaged requires class type.
        if (returnSpec.IsDynamicSelf && env.ParentDecl is not ClassDecl)
            return "dynamic_self_non_class";

        // 15e. Optional<Self> returns: allowed for class parents (same reason as 15d).
        // The IsOptionalSupportedForCdecl gate lets Optional<Self> through the unsupported-generic
        // container check, but we must reject it on struct/enum parents because Unmanaged.passRetained
        // requires a class type.
        if (returnSpec is NamedTypeSpec optSelfReturn && optSelfReturn.Name == "Swift.Optional"
            && optSelfReturn.GenericParameters.Count == 1
            && optSelfReturn.GenericParameters[0].IsDynamicSelf
            && env.ParentDecl is not ClassDecl)
            return "optional_self_non_class";

        return null;
    }

    /// <summary>
    /// Gets the @_cdecl symbol name for a method wrapper.
    /// Pure function — no side effects, safe to call before emission.
    /// </summary>
    public static string GetMethodSymbolName(string moduleName, string typeName, string methodName, string originalMangledName)
    {
        var hash = EmitterUtility.DeterministicHash8(originalMangledName);
        var safeTypeName = typeName.Replace(".", "_");
        return $"SBW_{moduleName}_{safeTypeName}_{methodName}_{hash}";
    }

    /// <summary>
    /// Emits a Swift @_cdecl wrapper function for a method.
    /// The wrapper receives C-compatible parameters, reconstructs self for instance methods,
    /// calls the Swift method, and returns the result via C ABI.
    /// </summary>
    /// <param name="swiftWriter">The Swift writer for the wrapper .swift file.</param>
    /// <param name="env">The method environment with method info.</param>
    /// <param name="ctx">The per-module emission context for dedup tracking.</param>
    /// <param name="silgenTarget">Optional @_silgen_name symbol to call instead of direct method (for default param overloads).</param>
    /// <param name="silgenHasResultBuffer">When true, the silgen target has a _resultBuf parameter for large optional returns.</param>
    public static void EmitSwiftMethodWrapper(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null,
        string? silgenTarget = null,
        bool silgenHasResultBuffer = false)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();

        var methodDecl = env.MethodDecl;
        var parentTypeDecl = env.ParentDecl as TypeDecl;
        var parentModuleDecl = env.ParentDecl as ModuleDecl;
        if (parentTypeDecl == null && parentModuleDecl == null) return;

        var symbolName = env.EmissionSymbol; // AF13: the cdecl symbol promoted by the caller

        // Detect skip-paths BEFORE registering the symbol, so the wrapper-symbol contract
        // never reports a symbol as registered when the @_cdecl wasn't actually written.
        // Currently the only post-setup skip is the generic-static-dispatch extension-method
        // collision case below, which redirects to EmitGenericStaticDispatchMethod and bails
        // without producing Swift output.
        if (parentTypeDecl != null
            && WouldGenericStaticDispatchSkipForExtensionCollision(env, parentTypeDecl, out var skippedSwiftName))
        {
            swiftWriter.WriteLine();
            swiftWriter.WriteLines($$"""
                // Generic static dispatch wrapper skipped for '{{skippedSwiftName}}':
                // extension method has same-name overload on parent type — unconstrained
                // extension cannot disambiguate (constraint propagation not yet supported).
                """);
            return;
        }

        // Constrained-extension methods on generic parents: the wrapper's conformance
        // extension is emitted WITHOUT a where-clause, so any extra constraints (e.g.
        // `extension Mapper where N : ImmutableMappable` or `where N.Element : P`)
        // drop on the floor. Two failure modes follow, depending on dispatch path:
        // (1) GSM path — the constrained method becomes invisible at the wrapper call
        //     site and Swift's overload resolution bridges via implicit conversions
        //     (Any → Any?, etc.), producing wrappers that fail to compile
        //     (e.g., `map(JSONObject:Any) throws -> N` mis-resolves to `map(JSONObject:Any?) -> N?`).
        // (2) Instance-class-dispatch path (EmitGenericClassProtocolAndConformance)
        //     — swiftc rejects `extension Box: _SBW_P_<hash> {}` outright because the
        //     protocol requirement is only available under the extension's where-clause.
        // Skip in both cases until conditional-conformance wrapper extensions are supported.
        if (parentTypeDecl != null
            && WouldGenericStaticDispatchSkipForNarrowerConstraint(env, parentTypeDecl, out var narrowedSwiftName))
        {
            swiftWriter.WriteLine();
            swiftWriter.WriteLines($$"""
                // Generic static dispatch wrapper skipped for '{{narrowedSwiftName}}':
                // method has narrower generic constraints than its parent type —
                // the wrapper extension is unconstrained, so the constrained method
                // is invisible at the call site (conditional-conformance wrapper not yet supported).
                """);
            return;
        }

        // Route through the cross-emitter structural-identity registry so a prior
        // claim from ProtocolExtensionEmitter on the same Swift method (carried
        // forward on the synthetic MethodDecl as StructuralIdentityKey) is honored
        // even when the two emitters would render different @_cdecl symbol strings.
        // For ordinary methods StructuralIdentityKey is null and the symbol string
        // itself is the structural identity — equivalent to the prior
        // TryAddMethodWrapperSymbol.
        var sourceKey = methodDecl.StructuralIdentityKey ?? symbolName;
        var sourceTypeName = parentTypeDecl?.SwiftTypeName.ModuleQualifiedName
            ?? parentModuleDecl?.Name
            ?? string.Empty;
        if (!ctx.TryClaimWrapperSymbol(sourceTypeName, methodDecl.Name, sourceKey, symbolName, DeclIdFactory.ForMethod(methodDecl)))
            return; // Already emitted

        var moduleName = parentTypeDecl?.SwiftTypeName.Module ?? parentModuleDecl!.Name;
        var moduleQualifiedSwiftName = parentTypeDecl?.SwiftTypeName.ModuleQualifiedName ?? "";

        bool isClass = env.ParentDecl is ClassDecl;
        bool isStatic = methodDecl.MethodType == MethodType.Static || parentTypeDecl == null;
        bool isMutating = methodDecl.IsMutating;
        bool throws = methodDecl.Throws;
        bool isNonCopyableParent = !isClass && !isStatic && WrapperValidation.IsNonCopyableStructParent(env.ParentDecl);
        // Consuming self on a ~Copyable parent: the method takes ownership of self, so the wrapper
        // must move() the value out of the caller-owned buffer (a borrow via .pointee cannot be
        // consumed) — see selfRef below. That requires a MUTABLE raw pointer for self_. The paired
        // C# handle is marked consumed after the P/Invoke (WrapperEmitter) so the value-witness
        // Destroy doesn't run a second time. Copyable parents keep the borrow path (implicit copy).
        bool consumesSelf = isNonCopyableParent && methodDecl.IsConsuming;

        // Determine return mapping
        var returnTypeSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        bool isVoidReturn = returnTypeSpec.IsEmptyTuple;
        // LocalizedStringResource projects to a C# string and rides the SBW_Utf8Slice String return
        // path; broaden locally rather than touching WitnessDispatchEmitter.IsStringType so the
        // resilient type stays out of witness/protocol dispatch (where it remains gate-dropped).
        bool isLsrReturn = !isVoidReturn && MarshallingHelpers.IsLocalizedStringResource(returnTypeSpec);
        bool isString = !isVoidReturn &&
            (WitnessDispatchEmitter.IsStringType(returnTypeSpec) || isLsrReturn);

        var (returnMapping, needsResultPtr) = isVoidReturn
            ? (new CdeclReturnMapping("Void", CdeclReturnKind.Direct), false)
            : CdeclReturnMapping.Classify(returnTypeSpec, env.TypeDatabase);

        // String returns always need result ptr (SBW_Utf8Slice)
        if (isString)
            needsResultPtr = true;

        // Ensure SBW_Utf8Slice infrastructure is emitted for string returns
        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Build Swift parameter list for the @_cdecl wrapper.
        // Phase ordering is determined by CdeclSignatureContract.
        // ResultPtr is handled outside the loop using the emitter's own needsResultPtr logic
        // (GetCdeclReturnMapping), matching the PInvoke pattern where HandleReturnType handles it.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var closureAdapterLines = new List<string>();
        var writeBackLines = new List<string>();
        var callArgs = new List<string>();
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        // Result buffer parameter (first, for indirect results and string returns)
        if (needsResultPtr)
        {
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        }

        // When calling a silgen target, all parameters use _ (no external labels).
        bool omitLabels = silgenTarget != null;

        bool isGenericParent = WrapperValidation.IsGenericParent(env.ParentDecl);
        bool needsStaticDispatch = WrapperValidation.NeedsGenericDispatch(env, MemberKind.Method);

        // For generic static dispatch methods, delegate to the specialized emitter.
        if (needsStaticDispatch && !isStatic)
        {
            EmitGenericStaticDispatchMethod(swiftWriter, env, ctx, symbolName,
                parentTypeDecl!, moduleQualifiedSwiftName,
                isClass, isMutating, throws, returnTypeSpec, isVoidReturn, isString,
                needsResultPtr, returnMapping);
            return;
        }

        bool isGenericClassParent = IsGenericClassParent(env.ParentDecl);

        var order = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: needsResultPtr);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    break; // Already handled above

                case CdeclPhase.ErrorOut:
                    swiftParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
                    break;

                case CdeclPhase.Self:
                    // consuming self needs a mutable pointer so the wrapper can move() the value
                    // out of the buffer; mutating self needs it to write mutations back.
                    if (isClass || isMutating || consumesSelf)
                        swiftParams.Add("_ self_: UnsafeMutableRawPointer");
                    else
                        swiftParams.Add("_ self_: UnsafeRawPointer");
                    break;

                case CdeclPhase.Arguments:
                    var closureParamCount = keptArgs.Count(env.ClosureHandler.IsClosure);
                    // Sibling bindings so each user param's reserved-collision escape also dodges its
                    // siblings.
                    var siblings = CdeclParamMapper.CollectSiblingBindingNames(keptArgs);
                    for (int i = 0; i < keptArgs.Count; i++)
                    {
                        var arg = keptArgs[i];
                        if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                            continue;
                        // A Void (empty-tuple) parameter carries no bytes: it contributes no
                        // @_cdecl ABI parameter and no reconstruction. But Swift still requires
                        // the argument at the call site, so forward the unique Void value `()`
                        // with its label — e.g. `buildPartialBlock(first: ())` — instead of
                        // silently dropping it to `buildPartialBlock()`, which fails to compile.
                        if (arg.SwiftTypeSpec.IsEmptyTuple)
                        {
                            var voidLabel = omitLabels ? "" : ClosureEmitter.GetSwiftArgLabelForCdecl(arg);
                            callArgs.Add($"{voidLabel}()");
                            continue;
                        }

                        // Closure parameters: two @_cdecl params (funcPtr + context) + adapter code
                        var closureTypeSpec = env.ClosureHandler.GetClosureTypeSpec(arg);
                        if (closureTypeSpec != null &&
                            env.ClosureHandler.IsSupportedClosure(closureTypeSpec) &&
                            env.ClosureHandler.RequiresThunk(closureTypeSpec, env.EmissionSymbol, closureParamCount) &&
                            !env.ClosureHandler.IsAsyncClosure(closureTypeSpec))
                        {
                            var csName = NameProvider.StripVerbatimPrefix(
                                NameProvider.GetCSharpParameterName(arg));
                            swiftParams.Add($"_ {csName}FuncPtr: UnsafeMutableRawPointer?");
                            swiftParams.Add($"_ {csName}Context: UnsafeMutableRawPointer?");

                            bool isOptional = env.ClosureHandler.IsOptionalClosure(arg.SwiftTypeSpec);
                            bool isEscaping = WrapperValidation.IsEffectivelyEscaping(
                                closureTypeSpec, arg.SwiftTypeSpec, env.ClosureHandler);
                            if (isEscaping)
                                ClosureContextHelperEmitter.EmitIfNeeded(swiftWriter, ctx);
                            closureAdapterLines.AddRange(
                                ClosureEmitter.GetSwiftClosureAdapterCode(
                                    csName, closureTypeSpec, env.ClosureHandler, isOptional, isEscaping,
                                    swiftWriter, ctx, methodDecl.ModuleDecl?.Name ?? "SwiftBindings"));

                            var adapterName = $"_adapted_{csName}";
                            var argLabel = omitLabels ? "" : ClosureEmitter.GetSwiftArgLabelForCdecl(arg);
                            // @autoclosure parameters: forward the value by invoking the adapted
                            // closure with () — but ONLY when calling the real Swift API directly.
                            // When routing through a _dbw_ default-param shim (silgenTarget != null),
                            // the shim has already redeclared the param as an explicit
                            // `@escaping () -> T` and invokes it inside its own body, so the outer
                            // wrapper must pass the closure itself. Invoking it here would pass a `T`
                            // where the shim expects `() -> T` (swiftc "cannot convert value of type
                            // 'T' to expected argument type '() -> T'").
                            var autoClosureSuffix = closureTypeSpec.IsAutoClosure && silgenTarget == null ? "()" : "";
                            callArgs.Add($"{argLabel}{adapterName}{autoClosureSuffix}");
                            continue;
                        }

                        var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
                        if (label == "_")
                            label = $"arg{i}";

                        // Inout parameters: use UnsafeMutableRawPointer with write-back semantics.
                        // The wrapper creates a var binding, passes &ref, and writes back after the call.
                        if (arg.IsInOut)
                        {
                            var (cdeclParam, reconstruction, callArg, writeBack) =
                                CdeclParamMapper.MapInout(arg, label, env, omitLabels, reservedSiblings: siblings);
                            swiftParams.Add(cdeclParam);
                            reconstructionLines.Add(reconstruction);
                            callArgs.Add(callArg);
                            writeBackLines.Add(writeBack);
                        }
                        else
                        {
                            var (cdeclParam, reconstruction, callArg) = CdeclParamMapper.Map(arg, label, env, omitLabels, reservedSiblings: siblings);
                            swiftParams.Add(cdeclParam);
                            if (reconstruction != null)
                                reconstructionLines.Add(reconstruction);
                            callArgs.Add(callArg);
                        }
                    }
                    break;

                case CdeclPhase.Metadata:
                    if (isGenericClassParent && parentTypeDecl != null)
                    {
                        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
                        {
                            swiftParams.Add($"_ _metadata{i}: UnsafeRawPointer");
                        }
                        // Add PWT parameters for constrained generic types.
                        // Only include PWT for resolvable conformances (no associated types
                        // or Self requirements) to match what C# P/Invoke passes. Members on
                        // parents with unresolvable conformances are gated upstream by
                        // GenericDispatchEmitter.CanEmitGenericDispatch via
                        // MetatypeHelperEmitter.HasUnresolvableTypeConformances, so we never
                        // reach this line for those types today.
                        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, env.TypeDatabase);
                        for (int pi = 0; pi < pwtCount; pi++)
                        {
                            swiftParams.Add($"_ _pwt{pi}: UnsafeRawPointer");
                        }
                    }
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Build return clause
        string returnClause;
        if (isVoidReturn || needsResultPtr)
            returnClause = "";
        else
            returnClause = $" -> {returnMapping.CdeclReturnType}";

        // Build the Swift function name
        var swiftFuncName = $"_sbw_method_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build call arguments string
        var callArgString = string.Join(", ", callArgs);

        // Build the call expression
        // For mutating methods, use through-pointer access so mutations write back.
        // For noncopyable types, use inline borrow to avoid copy (let obj = ...pointee copies).
        string selfRef;
        if (isStatic && parentTypeDecl != null)
            selfRef = moduleQualifiedSwiftName;
        else if (isStatic)
            selfRef = "";  // Free function: no type prefix
        else if (consumesSelf)
            // Consuming self on ~Copyable: move() takes ownership out of the buffer (deinitializing
            // it) and yields an owned value the consuming method can take. A .pointee borrow here is
            // rejected by swiftc ("is borrowed and cannot be consumed"). The C# handle is marked
            // consumed after the call so the now-empty buffer is freed without a second Destroy.
            selfRef = $"self_.assumingMemoryBound(to: {moduleQualifiedSwiftName}.self).move()";
        else if ((isMutating || isNonCopyableParent) && !isClass)
            selfRef = $"self_.assumingMemoryBound(to: {moduleQualifiedSwiftName}.self).pointee";
        else
            selfRef = "obj";

        // When calling a @_silgen_name target that has its own _resultBuf parameter
        // (for large optional returns), forward resultPtr and skip result handling in this wrapper.
        bool silgenHandlesResult = silgenTarget != null && silgenHasResultBuffer && needsResultPtr;
        if (silgenHandlesResult)
        {
            // Append resultPtr to the call — the silgen function writes to _resultBuf directly
            callArgString = string.IsNullOrEmpty(callArgString)
                ? "resultPtr"
                : $"{callArgString}, resultPtr";
        }

        string callExpr;
        if (silgenTarget != null)
        {
            callExpr = string.IsNullOrEmpty(selfRef)
                ? $"{silgenTarget}({callArgString})"
                : $"{selfRef}.{silgenTarget}({callArgString})";
        }
        else if (methodDecl.HasVariadicParameter)
        {
            // Variadic call site: Swift has no runtime splat operator, so we can't write
            // `foo(myArray)` where foo takes `T...`. The (T...) -> R and ([T]) -> R function
            // types share ABI (variadic lowers to Array<T> at SIL), so we can unsafeBitCast
            // the function reference. The variadic-form `as` cast disambiguates overloads.
            // Strip argument labels — function values are called positionally, and Swift
            // rejects `(f as (Int) -> Int)(x: 1)` with "extraneous argument label".
            var swiftMethodName = NameProvider.ParserNameToSwift(methodDecl);
            var prefix = string.IsNullOrEmpty(selfRef) ? "" : $"{selfRef}.";
            var variadicSig = BuildVariadicCastSignature(methodDecl, useArrayForm: false);
            var arraySig = BuildVariadicCastSignature(methodDecl, useArrayForm: true);
            var positionalCallArgs = string.Join(", ", callArgs.Select(StripArgLabel));
            callExpr = $"unsafeBitCast({prefix}{swiftMethodName} as {variadicSig}, to: ({arraySig}).self)({positionalCallArgs})";
        }
        else
        {
            var swiftMethodName = NameProvider.ParserNameToSwift(methodDecl);
            // When the parent type has a same-name same-param sibling with a different
            // return type (e.g. AppShortcutsBuilder.buildExpression returning AppShortcut
            // vs [AppShortcut]), Swift's overload resolution at the call site is
            // ambiguous. Force selection of this specific overload via a
            // function-reference `as` cast pinned to this method's signature.
            if (HasReturnTypeOnlyOverloadSibling(methodDecl, parentTypeDecl))
            {
                // Strip argument labels — function values are called positionally.
                var castSig = BuildOverloadDisambiguationSignature(methodDecl);
                var prefix = string.IsNullOrEmpty(selfRef) ? "" : $"{selfRef}.";
                var positionalCallArgs = string.Join(", ", callArgs.Select(StripArgLabel));
                callExpr = $"({prefix}{swiftMethodName} as {castSig})({positionalCallArgs})";
            }
            else
            {
                callExpr = string.IsNullOrEmpty(selfRef)
                    ? $"{swiftMethodName}({callArgString})"
                    : $"{selfRef}.{swiftMethodName}({callArgString})";
            }
        }

        // LocalizedStringResource return: convert the resource to a String before it rides the
        // SBW_Utf8Slice String return path. String(localized:) resolves the resource against the
        // current locale (iOS 16+). Wrapping the fully-built call expression covers every branch
        // above, plus the throwing path (the `try` prefix is added downstream around this expr).
        if (isLsrReturn)
            callExpr = $"String(localized: {callExpr})";

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericClassParent && !string.IsNullOrEmpty(moduleQualifiedSwiftName))
        {
            protocolName = GenericProtocolEmitter.GetProtocolName("P", symbolName);
            EmitGenericClassProtocolAndConformance(
                swiftWriter, methodDecl, env, symbolName, moduleQualifiedSwiftName);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        var wrapperTarget = string.IsNullOrEmpty(moduleQualifiedSwiftName)
            ? $"free function {methodDecl.Name}"
            : $"{moduleQualifiedSwiftName}.{methodDecl.Name}";
        swiftWriter.WriteLines($$"""
            // Method @_cdecl wrapper for {{wrapperTarget}}.
            // Routes method through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // Add @MainActor when wrapping @MainActor-isolated members.
        // Swift 6 requires the caller to share the isolation context, even at
        // -strict-concurrency=minimal. @MainActor on @_cdecl is a compile-time
        // constraint only (no ABI change). The C# consumer manages thread affinity.
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentTypeDecl ?? (BaseDecl?)parentModuleDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, parentTypeDecl));

        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction lines
        foreach (var line in reconstructionLines)
        {
            swiftWriter.WriteLine(line);
        }

        // Emit closure adapter lines (reconstruct native Swift closures from Cdecl func ptrs)
        foreach (var line in closureAdapterLines)
        {
            swiftWriter.WriteLine(line);
        }

        // Emit inout write-back via defer. Must run after call completes (or throws)
        // but before function returns. defer ensures correct ordering even with early returns.
        if (writeBackLines.Count > 0)
        {
            if (writeBackLines.Count == 1)
            {
                swiftWriter.WriteLine($"defer {{ {writeBackLines[0]} }}");
            }
            else
            {
                swiftWriter.WriteLine("defer {");
                swiftWriter.Indent++;
                foreach (var writeBack in writeBackLines)
                {
                    swiftWriter.WriteLine(writeBack);
                }
                swiftWriter.Indent--;
                swiftWriter.WriteLine("}");
            }
        }

        // Reconstruct self for instance methods
        if (!isStatic)
        {
            if (isGenericClassParent && protocolName != null)
            {
                // Generic parent class: use AnyObject + protocol cast for type erasure
                SelfReconstructionEmitter.EmitProtocolCast(swiftWriter, protocolName, isMutable: false);
            }
            else
            {
                EmitSelfReconstruction(swiftWriter, isClass, isMutating, moduleQualifiedSwiftName, isNonCopyableParent);
            }
        }

        // Emit the body based on method characteristics
        if (throws)
        {
            // When the silgen target handles the result buffer, the call is effectively void
            // from this wrapper's perspective — the silgen function writes to _resultBuf directly.
            EmitThrowingMethodBody(swiftWriter, callExpr, returnTypeSpec, returnMapping,
                silgenHandlesResult ? false : needsResultPtr,
                silgenHandlesResult ? true : isVoidReturn,
                silgenHandlesResult ? false : isString,
                env.TypeDatabase);
        }
        else if (silgenHandlesResult)
        {
            // The @_silgen_name function writes to _resultBuf directly — just call it.
            swiftWriter.WriteLine(callExpr);
        }
        else if (isVoidReturn)
        {
            swiftWriter.WriteLine(callExpr);
        }
        else if (isString)
        {
            EmitStringReturnBody(swiftWriter, callExpr);
        }
        else if (needsResultPtr && returnTypeSpec is ClosureTypeSpec)
        {
            // Closure returns: strip @escaping/@Sendable (parameter attributes, not valid
            // in metatype position) and wrap in parens for correct .self binding.
            var closureType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec)
                .Replace("@escaping ", "").Replace("@Sendable ", "");
            swiftWriter.WriteLine($"let result = {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: ({closureType}).self, repeating: result, count: 1)");
        }
        else if (needsResultPtr)
        {
            // Non-frozen struct, complex enum, Optional<value-type>: write to result buffer
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);

            // Noncopyable return types: initializeMemory(as:repeating:count:) requires Copyable.
            // Use assumingMemoryBound(to:).initialize(to:) which takes consuming T instead.
            bool isNonCopyableReturn = returnTypeSpec is NamedTypeSpec returnNamed &&
                env.TypeDatabase.TryGetTypeRecord(returnNamed, out var returnRecord) &&
                returnRecord.Flags.HasFlag(TypeRecordFlags.NonCopyable);

            if (isNonCopyableReturn)
            {
                swiftWriter.WriteLine($"let result = {callExpr}");
                swiftWriter.WriteLine($"resultPtr.assumingMemoryBound(to: {swiftType}.self).initialize(to: result)");
            }
            else
            {
                // Protocol existentials (any Protocol1 & Protocol2) need parentheses before .self
                // to prevent .self from binding to only the last protocol in the composition.
                var metatype = swiftType.StartsWith("any ") ? $"({swiftType}).self" : $"{swiftType}.self";
                swiftWriter.WriteLine($"let result = {callExpr}");
                swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
            }
        }
        else
        {
            EmitDirectReturn(swiftWriter, callExpr, returnTypeSpec, env.TypeDatabase, returnMapping);
        }

        // For mutating struct methods, write back the mutated value
        if (!isStatic && isMutating && !isClass)
        {
            // The struct was loaded from self_ pointer; mutations happened on obj.
            // Write back the mutated value.
            // (Handled inline by using through-pointer access for mutating methods)
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");

        // Emit invoke thunk for closure returns — a separate @_cdecl function that C# calls
        // via CallConvCdecl to invoke the returned closure, avoiding delegate* unmanaged[Swift].
        // Handles both direct closure returns and Optional<Closure> returns.
        ClosureTypeSpec? closureReturnSpec = returnTypeSpec as ClosureTypeSpec;
        if (closureReturnSpec == null && env.ClosureHandler != null && env.ClosureHandler.IsOptionalClosure(returnTypeSpec))
        {
            // Extract the inner ClosureTypeSpec from Optional<Closure>
            if (returnTypeSpec is NamedTypeSpec optNts && optNts.GenericParameters.Count == 1)
                closureReturnSpec = optNts.GenericParameters[0] as ClosureTypeSpec;
        }
        if (needsResultPtr && closureReturnSpec != null
            && env.ClosureHandler != null
            && env.ClosureHandler.IsSupportedClosure(closureReturnSpec)
            && ClosureEmitter.CanUseInvokeThunk(closureReturnSpec, env.ClosureHandler))
        {
            var thunkEntryPoint = ClosureEmitter.GetInvokeThunkEntryPoint(symbolName);
            var thunkFuncName = $"_sbw_inv_closure_{EmitterUtility.DeterministicHash8(thunkEntryPoint)}";
            // Use ctx, not env.EmissionContext — env.EmissionContext is set later by
            // MethodHandler and is null at this point in the emission pipeline.
            ClosureEmitter.EmitSwiftInvokeThunk(swiftWriter, closureReturnSpec, env.ClosureHandler,
                thunkEntryPoint, thunkFuncName, ctx);
        }
    }

    /// <summary>
    /// Returns true when <see cref="EmitGenericStaticDispatchMethod"/> would refuse to
    /// emit a Swift @_cdecl wrapper because the method is an extension method on a
    /// generic parent and another non-accessor method on that parent shares the same
    /// dispatch identity (base name + per-slot (label, type, inout) tuple) — the
    /// unconstrained conformance extension can't disambiguate between such overloads
    /// when they differ only by where-clause constraints (Swift sees them as ambiguous
    /// from the unconstrained extension's vantage point).
    ///
    /// Overloads that differ in argument label, parameter type, or inout-ness (e.g.
    /// <c>index(before:)</c> vs <c>index(after:)</c>; <c>lookup(by:Int)</c> vs
    /// <c>lookup(by:String)</c>; <c>foo(_:Int)</c> vs <c>foo(_:inout Int)</c>)
    /// resolve unambiguously at the wrapper call site, so they must NOT trigger this
    /// skip.
    ///
    /// Mirrors the gate that EmitGenericStaticDispatchMethod itself used to apply
    /// internally; hoisted out so the wrapper-symbol contract can detect the skip
    /// before <see cref="ModuleEmissionContext.TryAddMethodWrapperSymbol"/> records the
    /// symbol as registered.
    /// </summary>
    internal static bool WouldGenericStaticDispatchSkipForExtensionCollision(
        MethodEnvironment env, TypeDecl parentTypeDecl, out string swiftMethodName)
    {
        swiftMethodName = NameProvider.ParserNameToSwift(env.MethodDecl);
        var methodDecl = env.MethodDecl;
        bool isStatic = methodDecl.MethodType == MethodType.Static;
        if (isStatic) return false;
        if (!WrapperValidation.NeedsGenericDispatch(env, MemberKind.Method)) return false;
        if (!methodDecl.IsExtensionMethod) return false;

        var baseName = methodDecl.Name;
        var thisSelector = BuildSwiftSelectorSignature(methodDecl);
        return parentTypeDecl.Methods.Any(m =>
            m != methodDecl
            && m.Name == baseName
            && !m.IsAccessor
            && BuildSwiftSelectorSignature(m) == thisSelector);
    }

    /// <summary>
    /// Returns true when the method's generic parameters carry conformance constraints
    /// on the PARENT's generic parameters that the parent type itself does not declare
    /// (a constrained extension like <c>extension Mapper where N : ImmutableMappable</c>,
    /// <c>extension Mapper where N.Element : ImmutableMappable</c>, or the same-type
    /// shape <c>extension Mapper where N == Foo</c>).
    /// <para>
    /// The generated wrapper's conformance extension is emitted unconditionally (no
    /// where-clause), so any extra constraint on a parent-declared generic parameter
    /// becomes invisible at the call site. This affects BOTH dispatch paths:
    /// (1) the generic-static-dispatch (GSM) path, where the constrained method
    /// becomes invisible and Swift's overload resolution silently bridges to a sibling;
    /// (2) the instance-class-dispatch path (<c>EmitGenericClassProtocolAndConformance</c>
    /// → <c>extension Box: _SBW_P_&lt;hash&gt; {}</c>), where swiftc rejects the
    /// unconditional conformance because the protocol requirement is only available
    /// under the extension's where-clause.
    /// </para>
    /// <para>
    /// Method-local generic parameters (depth &gt; 0, i.e. introduced by the method
    /// signature itself rather than inherited from the parent) are filtered out: their
    /// constraints are scoped to the method and don't require propagation onto the
    /// conformance extension. Static methods are also excluded — they don't reach the
    /// GSM/instance-class-dispatch emission paths in the same way (statics flow through
    /// metatype-derived dispatch with a different emission shape), and no real-world
    /// constrained-static-extension regression has been observed; the
    /// <c>GenericStaticDispatch_StaticConstrainedExtension_DoesNotMisfire</c> unit
    /// test pins this behavior.
    /// </para>
    /// </summary>
    internal static bool WouldGenericStaticDispatchSkipForNarrowerConstraint(
        MethodEnvironment env, TypeDecl parentTypeDecl, out string swiftMethodName)
    {
        swiftMethodName = NameProvider.ParserNameToSwift(env.MethodDecl);
        var methodDecl = env.MethodDecl;
        // Statics flow through metatype-derived dispatch with a different emission shape
        // (see GenericStaticDispatch_StaticConstrainedExtension_DoesNotMisfire); no
        // real-world constrained-static-extension regression has been observed, so the
        // narrowing gate is scoped to instance methods here. The shared predicate itself
        // is dispatch-neutral — the property path applies it to statics too.
        if (methodDecl.MethodType == MethodType.Static) return false;
        return WrapperValidation.GenericParamsNarrowParentConstraints(
            methodDecl.GenericParameters, parentTypeDecl);
    }

    /// <summary>
    /// Builds the dispatch-identity signature for a method: base name plus the tuple of
    /// (external argument label, parameter type, inout-ness) per slot. Two methods with
    /// the same signature are indistinguishable to Swift's overload resolution from
    /// inside the unconstrained conformance extension — that is the genuine ambiguity
    /// the wrapper-extension skip protects against. Methods that differ in arg labels,
    /// parameter types, or inout convention resolve unambiguously (Swift selects by
    /// label first, then by argument type and convention at the call site), so they
    /// must not be collapsed.
    /// </summary>
    private static string BuildSwiftSelectorSignature(MethodDecl methodDecl)
    {
        var slots = methodDecl.CSSignature
            .Skip(1) // First entry is the return type
            .Select(arg =>
            {
                var n = arg.Name;
                var label = (string.IsNullOrEmpty(n) || n == "_" || SwiftBuilder.IsAutoGeneratedArgName(n))
                    ? "_"
                    : n;
                // ArgumentDecl.IsInOut is set by the parser independently of
                // SwiftTypeSpec.IsInOut, so include it explicitly to keep
                // `foo(_ x: Int)` and `foo(_ x: inout Int)` distinct.
                var inoutMarker = arg.IsInOut ? "inout " : string.Empty;
                return $"{label}:{inoutMarker}{arg.SwiftTypeSpec.ToString(useFullNames: true)}";
            });
        return $"{methodDecl.Name}({string.Join(",", slots)})";
    }

    /// <summary>
    /// Emits a @_cdecl method wrapper for generic types where T appears in the method
    /// parameters or return type. Uses protocol-based type erasure with a static dispatch method:
    /// 1. Defines a protocol with a static method (UnsafeRawPointer for T positions)
    /// 2. Extends the generic type to unconditionally conform
    /// 3. The @_cdecl wrapper receives metadata, casts to protocol type, calls static method
    /// </summary>
    private static void EmitGenericStaticDispatchMethod(
        SwiftWriter swiftWriter,
        MethodEnvironment env,
        ModuleEmissionContext? ctx,
        string symbolName,
        TypeDecl parentTypeDecl,
        string moduleQualifiedSwiftName,
        bool isClass,
        bool isMutating,
        bool throws,
        TypeSpec returnTypeSpec,
        bool isVoidReturn,
        bool isString,
        bool needsResultPtr,
        CdeclReturnMapping returnMapping)
    {
        // Normalized once so every dedup registry consulted below is the same one. Coalescing at
        // each use site would mint an independent fallback per use, and two of those disagree
        // about what has already been emitted.
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();
        var methodDecl = env.MethodDecl;
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();
        var abiToSugaredName = WrapperValidation.GetAbiToSugaredNameMap(parentTypeDecl);

        var methodHash = EmitterUtility.DeterministicHash8(symbolName);
        var protocolName = $"_SBW_GSM_{methodHash}";
        var dispatchMethodName = $"_sbw_dispatch_{methodHash}";
        var swiftMethodName = NameProvider.ParserNameToSwift(methodDecl);

        // Extension-method/overload-collision skip is detected in EmitSwiftMethodWrapper
        // before symbol registration (see WouldGenericStaticDispatchSkipForExtensionCollision).
        // Reaching this point means the wrapper is safe to emit.

        // For string returns in generic static dispatch, we need Utf8Slice infrastructure
        if (isString)
        {
            var moduleName = parentTypeDecl.SwiftTypeName.Module;
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Determine if return type references T
        bool returnReferencesT = WrapperValidation.TypeSpecReferencesGenericParam(returnTypeSpec, genericParamNames);

        // Build protocol method and @_cdecl signatures
        var protocolParams = new List<string>();
        var extensionBodyLines = new List<string>();
        var methodCallArgs = new List<string>();
        var cdeclParams = new List<string>();
        var cdeclCallArgs = new List<string>();

        // Result ptr for indirect results (including T returns)
        bool cdeclNeedsResultPtr = needsResultPtr || (returnReferencesT && !isVoidReturn);

        // Sibling bindings so a reserved-name escape also dodges a sibling user param.
        var siblings = CdeclParamMapper.CollectSiblingBindingNames(keptArgs);
        int argIndex = 0;

        // PWT parameters for constrained generic types. Computed up front because the
        // metadata accessor helper and the metatype dispatch below reuse the count.
        int methodPwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, env.TypeDatabase);

        // Assemble the @_cdecl ABI parameter list from the shared parameter-order contract —
        // the same phase sequence the normal method path drives — so the ordering has a
        // single source. Self is always present (instance dispatch) and the error-out follows
        // the contract's throws decision. Protocol params and cdeclCallArgs use labeled
        // arguments (order-independent) and are appended alongside their @_cdecl counterparts;
        // the Metadata phase contributes only to the @_cdecl signature, since generic metadata
        // and PWTs are resolved through the metatype accessor, not passed to the protocol method.
        var cdeclOrder = CdeclSignatureContract.DetermineParameterOrder(
            env, overrideNeedsResultPtr: cdeclNeedsResultPtr, overrideNeedsSelf: true);
        foreach (var phase in cdeclOrder.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    cdeclParams.Add("_ resultPtr: UnsafeMutableRawPointer");
                    protocolParams.Add("resultPtr: UnsafeMutableRawPointer");
                    cdeclCallArgs.Add("resultPtr: resultPtr");
                    break;

                case CdeclPhase.Arguments:
                    for (int i = 0; i < keptArgs.Count; i++)
                    {
                        var arg = keptArgs[i];
                        if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                            continue;
                        // Void (empty-tuple) param: no ABI parameter, no protocol param, no
                        // reconstruction — but the underlying generic method call still needs the
                        // labeled `()` argument so the call type-checks (mirrors the non-generic path).
                        if (arg.SwiftTypeSpec.IsEmptyTuple)
                        {
                            // Provenance-aware call label (canonical builder) — preserves labels that
                            // genuinely begin with '_' (e.g. _self) and backtick-escapes keywords.
                            var voidMethodArgLabel = CdeclParamMapper.BuildSwiftCallArgLabel(arg);
                            methodCallArgs.Add($"{voidMethodArgLabel}()");
                            continue;
                        }

                        var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
                        if (label == "_") label = $"arg{argIndex}";
                        // Keyword rename + sanitize + reserved/sibling escape (canonical helper). The
                        // external label is argLabel below, so this rename is source-local and safe.
                        label = CdeclParamMapper.BuildSwiftBindingName(label, siblings);

                        // Bare external label for the wrapper's own signature ("_" for unlabeled). Provenance-
                        // aware: prefer the parser-captured OriginalSwiftName so a label that genuinely begins
                        // with '_' (e.g. _self) is not corrupted by the legacy underscore strip.
                        var argLabel = arg.Name switch
                        {
                            var n when SwiftBuilder.IsAutoGeneratedArgName(n) => "_",
                            "_" => "_",
                            var n when string.IsNullOrEmpty(n) => "_",
                            var n => arg.OriginalSwiftName ?? (n.StartsWith("_") ? n.Substring(1) : n)
                        };

                        // When argLabel == label, Swift syntax is just "label:" (no redundant duplicate)
                        var paramPrefix = (argLabel == label) ? label : $"{argLabel} {label}";
                        var protocolArgLabel = argLabel == "_" ? "" : argLabel + ": ";
                        // Provenance-aware call label (canonical builder) for the underlying generic-method call.
                        var methodArgLabel = CdeclParamMapper.BuildSwiftCallArgLabel(arg);

                        if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames))
                        {
                            protocolParams.Add($"{paramPrefix}: UnsafeRawPointer");
                            cdeclParams.Add($"_ {label}: UnsafeRawPointer");
                            cdeclCallArgs.Add($"{protocolArgLabel}{label}");

                            var swiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(arg.SwiftTypeSpec, abiToSugaredName);
                            extensionBodyLines.Add($"let {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee");
                            methodCallArgs.Add($"{methodArgLabel}{label}Val");
                        }
                        else if (arg.IsInOut)
                        {
                            // Concrete-typed inout on a generic parent: thread the raw pointer through
                            // the protocol boundary, then load/call/write-back inside the extension.
                            // T-referencing inout is rejected earlier in ShouldEmitWrapper, so we know
                            // the type is concrete and can bind a typed pointee.
                            //
                            // Writeback is emitted as `defer` so it runs on ALL scope exits — throws,
                            // early returns (e.g. the empty-string return branch for String returns),
                            // and normal completion. Late-appending the writeback would skip it on
                            // the throwing path (the `try obj.method()` unwinds past it) and on
                            // string early-returns (the `return` in the empty-utf8 branch is not the
                            // last line of the body).
                            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg.SwiftTypeSpec);
                            protocolParams.Add($"{paramPrefix}: UnsafeMutableRawPointer");
                            cdeclParams.Add($"_ {label}: UnsafeMutableRawPointer");
                            cdeclCallArgs.Add($"{protocolArgLabel}{label}");

                            extensionBodyLines.Add($"var {label}Val = {label}.assumingMemoryBound(to: {swiftType}.self).pointee");
                            extensionBodyLines.Add($"defer {{ {label}.assumingMemoryBound(to: {swiftType}.self).pointee = {label}Val }}");
                            methodCallArgs.Add($"{methodArgLabel}&{label}Val");
                        }
                        else
                        {
                            // label is already sibling-escaped above; passing siblings keeps Map's internal
                            // re-escape sibling-aware (idempotent here).
                            var (cdeclParam, reconstruction, callExpr) = CdeclParamMapper.Map(arg, label, env, false, reservedSiblings: siblings);
                            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);
                            protocolParams.Add($"{paramPrefix}: {swiftType}");
                            cdeclParams.Add(cdeclParam);

                            // Use CdeclParamMapper's actual call expression — the local-variable suffix
                            // varies (Val vs Opt for Optional<BlittablePrimitive>), and hardcoding "Val"
                            // here drifts when the mapper uses a different name.
                            if (reconstruction != null)
                                cdeclCallArgs.Add(callExpr);
                            else
                                cdeclCallArgs.Add($"{protocolArgLabel}{label}");

                            methodCallArgs.Add($"{methodArgLabel}{label}");
                        }
                        argIndex++;
                    }
                    break;

                case CdeclPhase.Metadata:
                    for (int mi = 0; mi < parentTypeDecl.GenericParameters.Count; mi++)
                        cdeclParams.Add($"_ _metadata{mi}: UnsafeRawPointer");
                    // The metadata accessor requires a PWT pointer per resolvable protocol
                    // conformance — must match what the C# P/Invoke side passes.
                    for (int pi = 0; pi < methodPwtCount; pi++)
                        cdeclParams.Add($"_ _pwt{pi}: UnsafeRawPointer");
                    break;

                case CdeclPhase.Self:
                    if (isClass)
                        cdeclParams.Add("_ self_: UnsafeMutableRawPointer");
                    else if (isMutating)
                        cdeclParams.Add("_ self_: UnsafeMutableRawPointer");
                    else
                        cdeclParams.Add("_ self_: UnsafeRawPointer");
                    protocolParams.Add(isMutating ? "selfPtr: UnsafeMutableRawPointer" : "selfPtr: UnsafeRawPointer");
                    cdeclCallArgs.Add("selfPtr: self_");
                    break;

                case CdeclPhase.ErrorOut:
                    cdeclParams.Add("_ errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
                    protocolParams.Add("errorOut: UnsafeMutablePointer<UnsafeMutableRawPointer?>");
                    cdeclCallArgs.Add("errorOut: errorOut");
                    break;
            }
        }

        // Build protocol method declaration
        string protocolReturnType;
        if (isVoidReturn || cdeclNeedsResultPtr)
            protocolReturnType = "";
        else
            protocolReturnType = $" -> {returnMapping.CdeclReturnType}";

        var throwsClause = throws ? " throws" : "";

        // A main-actor-isolated member can only be called from a matching isolation context.
        // The @_cdecl entry point below carries @MainActor, but the call itself happens in the
        // type-erasure dispatch shim — so the requirement and its witness need the annotation
        // too, or the shim is nonisolated and the call is rejected.
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            parentTypeDecl, methodDecl.IsMainActorIsolated, methodDecl.IsNonisolated);
        var mainActorPrefix = needsMainActor ? "@MainActor " : "";

        var protocolMethodSig = $"{mainActorPrefix}static func {dispatchMethodName}({string.Join(", ", protocolParams)}){throwsClause}{protocolReturnType}";

        // Build extension body
        var methodCallArgString = string.Join(", ", methodCallArgs);

        // Reconstruct self inside the extension body
        if (isClass)
        {
            extensionBodyLines.Insert(0, "let obj = Unmanaged<AnyObject>.fromOpaque(selfPtr).takeUnretainedValue() as! Self");
        }
        else if (isMutating)
        {
            extensionBodyLines.Insert(0, $"var obj = selfPtr.assumingMemoryBound(to: Self.self).pointee");
        }
        else
        {
            extensionBodyLines.Insert(0, $"let obj = selfPtr.assumingMemoryBound(to: Self.self).pointee");
        }

        // Build the call and result handling
        string tryPrefix = throws ? "try " : "";
        if (isVoidReturn)
        {
            extensionBodyLines.Add($"{tryPrefix}obj.{swiftMethodName}({methodCallArgString})");
        }
        else if (isString)
        {
            // String returns: write SBW_Utf8Slice to resultPtr
            extensionBodyLines.Add($"let result: String = {tryPrefix}obj.{swiftMethodName}({methodCallArgString})");
            // For mutating methods, write back BEFORE any early return (empty string branch)
            if (isMutating && !isClass)
            {
                extensionBodyLines.Add("selfPtr.assumingMemoryBound(to: Self.self).pointee = obj");
            }
            extensionBodyLines.Add("let utf8 = Array(result.utf8)");
            extensionBodyLines.Add("if utf8.isEmpty {");
            extensionBodyLines.Add("    resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)");
            extensionBodyLines.Add("    return");
            extensionBodyLines.Add("}");
            extensionBodyLines.Add("let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)");
            extensionBodyLines.Add("ptr.initialize(from: utf8, count: utf8.count)");
            extensionBodyLines.Add("resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)");
        }
        else if (returnReferencesT)
        {
            // T return: write to resultPtr using the concrete type from the extension
            // Use sugared names (T, Element) instead of ABI names (τ_0_0)
            var returnSwiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(returnTypeSpec, abiToSugaredName);
            // Explicit type annotation forces Swift to resolve the correct overload
            // when multiple methods share the same base name but differ in return type
            // (e.g., map(JSONObject:) -> N throws vs map(JSONObject:) -> N?)
            extensionBodyLines.Add($"let result: {returnSwiftType} = {tryPrefix}obj.{swiftMethodName}({methodCallArgString})");
            extensionBodyLines.Add($"resultPtr.initializeMemory(as: {returnSwiftType}.self, repeating: result, count: 1)");
        }
        else if (cdeclNeedsResultPtr)
        {
            var returnSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);
            var metatype = returnSwiftType.StartsWith("any ") ? $"({returnSwiftType}).self" : $"{returnSwiftType}.self";
            extensionBodyLines.Add($"let result = {tryPrefix}obj.{swiftMethodName}({methodCallArgString})");
            extensionBodyLines.Add($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else
        {
            // Direct return — must apply the same conversions as EmitDirectGetterReturn:
            // Bool → Int8 (ternary), SimpleEnum → rawValue/tag, ClassPointer → Unmanaged
            var callExpr = $"{tryPrefix}obj.{swiftMethodName}({methodCallArgString})";
            extensionBodyLines.AddRange(CdeclReturnRenderer.LinesBindingResult(
                callExpr, returnTypeSpec, env.TypeDatabase, returnMapping));
        }

        // Inout writebacks for concrete-typed inout params on generic parents are emitted
        // inline as `defer` statements in the param loop above, which runs on ALL scope
        // exits (throws, early-return in string branch, normal completion).

        // For mutating struct methods, write back BEFORE any return statement.
        // Skip if already handled in the string branch (which inserts write-back before early return).
        if (isMutating && !isClass && !isString)
        {
            // Insert the write-back before the last line (which contains `return`)
            // to ensure mutating state changes are persisted
            var lastIdx = extensionBodyLines.Count - 1;
            if (lastIdx >= 0 && extensionBodyLines[lastIdx].TrimStart().StartsWith("return"))
            {
                extensionBodyLines.Insert(lastIdx, "selfPtr.assumingMemoryBound(to: Self.self).pointee = obj");
            }
            else
            {
                extensionBodyLines.Add("selfPtr.assumingMemoryBound(to: Self.self).pointee = obj");
            }
        }

        // The dispatch protocol + conformance extension carry no @_cdecl symbol; the anchor pins
        // both symbol-less blocks to the method that owns them so a wrapper-compile failure inside
        // either attributes to it rather than the coarse module scope, and the post-processor strips
        // the anchor with the block it names.
        var originAnchor = OriginAnchorEmitter.LineForWrapper(methodDecl);

        // Emit protocol
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            private protocol {{protocolName}} {
                {{protocolMethodSig}}
            }
            """);

        // Emit extension
        var extensionBody = string.Join("\n        ", extensionBodyLines);
        // Conformance extensions need the same `@available` floor as the wrapped API:
        // Swift checks the extension declaration against the deployment target, not the
        // @_cdecl below it. Without this the wrapper compile fails with
        // "X is only available in iOS Y or newer".
        var extensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            methodDecl.AvailabilityAnnotations, parentTypeDecl);
        var extensionAvailPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            extensionAvailability, "");
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            {{extensionAvailPrefix}}extension {{moduleQualifiedSwiftName}}: {{protocolName}} {
                {{mainActorPrefix}}static func {{dispatchMethodName}}({{string.Join(", ", protocolParams)}}){{throwsClause}}{{protocolReturnType}} {
                    {{extensionBody}}
                }
            }
            """);

        // Emit metadata accessor helper at module scope (before @_cdecl).
        // Use resolvable PWT count — constrained generic types need PWT for their metadata accessor.
        var methodHelperName = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, parentTypeDecl, ctx!, pwtCount: methodPwtCount);

        // Emit @_cdecl wrapper
        var cdeclParamString = string.Join(", ", cdeclParams);
        var swiftFuncName = $"_sbw_method_{EmitterUtility.DeterministicHash8(symbolName)}";

        string cdeclReturnClause;
        if (isVoidReturn || cdeclNeedsResultPtr)
            cdeclReturnClause = "";
        else
            cdeclReturnClause = $" -> {returnMapping.CdeclReturnType}";

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Method @_cdecl wrapper for {{moduleQualifiedSwiftName}}.{{methodDecl.Name}} (generic static dispatch).
            // Routes through protocol-based type erasure to avoid CallConvSwift crash.
            """);

        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            WrapperEmitterHelpers.MergeAvailability(methodDecl.AvailabilityAnnotations, parentTypeDecl));

        swiftWriter.WriteLine($"public func {swiftFuncName}({cdeclParamString}){cdeclReturnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction for concrete params
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;
            if (WrapperValidation.TypeSpecReferencesGenericParam(arg.SwiftTypeSpec, genericParamNames)) continue;

            var label = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : arg.Name;
            if (label == "_") label = $"arg{i}";
            if (NameProvider.IsSwiftKeyword(label)) label = $"{label}Param";
            label = SwiftBuilder.SanitizeIdentifier(label);

            // Reuse the param-declaration loop's siblings so this body reconstruction's binding
            // names match the @_cdecl param names exactly (see the ctor-path note at the analogous site).
            var (_, reconstruction, _) = CdeclParamMapper.Map(arg, label, env, false, reservedSiblings: siblings);
            if (reconstruction != null)
                swiftWriter.WriteLine(reconstruction);
        }

        // Metatype dispatch — convert T.self → ParentType<T>.self via metadata accessor
        var methodMetaArgsList = Enumerable.Range(0, parentTypeDecl.GenericParameters.Count).Select(i => $"_metadata{i}");
        var methodPwtArgsList = Enumerable.Range(0, methodPwtCount).Select(i => $"_pwt{i}");
        var methodMetaArgs = string.Join(", ", methodMetaArgsList.Concat(methodPwtArgsList));
        swiftWriter.WriteLine($"let parentMeta = {methodHelperName}({methodMetaArgs})");
        swiftWriter.WriteLine($"let metatype = unsafeBitCast(parentMeta, to: Any.Type.self) as! any {protocolName}.Type");

        // Call the protocol static method
        var cdeclCallArgString = string.Join(", ", cdeclCallArgs);

        if (throws)
        {
            swiftWriter.WriteLine("do {");
            swiftWriter.Indent++;
            if (isVoidReturn || cdeclNeedsResultPtr)
                swiftWriter.WriteLine($"try metatype.{dispatchMethodName}({cdeclCallArgString})");
            else
                swiftWriter.WriteLine($"return try metatype.{dispatchMethodName}({cdeclCallArgString})");
            swiftWriter.Indent--;
            swiftWriter.WriteLines("""
                } catch {
                    errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
                """);
            if (!isVoidReturn && !cdeclNeedsResultPtr)
            {
                swiftWriter.WriteLine("    return 0"); // sentinel
            }
            swiftWriter.WriteLine("}");
        }
        else if (isVoidReturn || cdeclNeedsResultPtr)
        {
            swiftWriter.WriteLine($"metatype.{dispatchMethodName}({cdeclCallArgString})");
        }
        else
        {
            swiftWriter.WriteLine($"return metatype.{dispatchMethodName}({cdeclCallArgString})");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits self reconstruction for instance methods.
    /// Delegates to <see cref="SelfReconstructionEmitter.Emit"/>.
    /// </summary>
    private static void EmitSelfReconstruction(SwiftWriter swiftWriter, bool isClass, bool isMutating, string moduleQualifiedSwiftName, bool isNonCopyable = false)
    {
        SelfReconstructionEmitter.Emit(swiftWriter, isClass, isMutating, moduleQualifiedSwiftName, isNonCopyable);
    }

    /// <summary>
    /// Emits the body of a throwing method wrapper.
    /// </summary>
    private static void EmitThrowingMethodBody(
        SwiftWriter swiftWriter,
        string callExpr,
        TypeSpec returnTypeSpec,
        CdeclReturnMapping returnMapping,
        bool needsResultPtr,
        bool isVoidReturn,
        bool isString,
        ITypeDatabase typeDatabase)
    {
        swiftWriter.WriteLine("do {");
        swiftWriter.Indent++;

        if (isVoidReturn)
        {
            swiftWriter.WriteLine($"try {callExpr}");
        }
        else if (isString)
        {
            EmitStringReturnBody(swiftWriter, $"try {callExpr}");
        }
        else if (needsResultPtr && returnTypeSpec is ClosureTypeSpec)
        {
            var closureType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec)
                .Replace("@escaping ", "").Replace("@Sendable ", "");
            swiftWriter.WriteLine($"let result = try {callExpr}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: ({closureType}).self, repeating: result, count: 1)");
        }
        else if (needsResultPtr)
        {
            var swiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(returnTypeSpec);

            // Noncopyable return types: initializeMemory(as:repeating:count:) requires Copyable.
            // Use assumingMemoryBound(to:).initialize(to:) which takes consuming T instead.
            bool isNonCopyableReturn = returnTypeSpec is NamedTypeSpec returnNamed &&
                typeDatabase.TryGetTypeRecord(returnNamed, out var returnRecord) &&
                returnRecord.Flags.HasFlag(TypeRecordFlags.NonCopyable);

            if (isNonCopyableReturn)
            {
                swiftWriter.WriteLine($"let result = try {callExpr}");
                swiftWriter.WriteLine($"resultPtr.assumingMemoryBound(to: {swiftType}.self).initialize(to: result)");
            }
            else
            {
                var metatype = swiftType.StartsWith("any ") ? $"({swiftType}).self" : $"{swiftType}.self";
                swiftWriter.WriteLine($"let result = try {callExpr}");
                swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
            }
        }
        else
        {
            EmitDirectReturn(swiftWriter, $"try {callExpr}", returnTypeSpec, typeDatabase, returnMapping);
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLines("""
            } catch {
                errorOut.pointee = Unmanaged.passRetained(error as AnyObject).toOpaque()
            """);

        // For non-void direct returns (not via resultPtr), we need a dummy return value.
        // For resultPtr and void, Swift is happy with just the error assignment.
        if (!isVoidReturn && !needsResultPtr)
        {
            // Return a sentinel value matching the return type
            CdeclReturnRenderer.WriteErrorSentinel(swiftWriter, returnMapping);
        }

        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits the string return body using SBW_Utf8Slice pattern.
    /// Delegates to <see cref="StringReturnEmitter.EmitReturnBody"/>.
    /// </summary>
    private static void EmitStringReturnBody(SwiftWriter swiftWriter, string callExpr)
    {
        StringReturnEmitter.EmitReturnBody(swiftWriter, callExpr);
    }

    /// <summary>
    /// Emits a direct return statement for non-string, non-indirect-result returns.
    /// </summary>
    private static void EmitDirectReturn(SwiftWriter swiftWriter, string callExpr,
        TypeSpec typeSpec, ITypeDatabase typeDatabase, CdeclReturnMapping mapping)
        => CdeclReturnRenderer.Write(swiftWriter, callExpr, typeSpec, typeDatabase, mapping, scalarParens: true);

    /// <summary>
    /// Checks whether any closure parameter is an async closure the emitter can't
    /// bridge (baseline `() async throws -> primitive` is supported — see
    /// <see cref="ClosureHandler.IsBaselineAsyncThrowingClosure"/>).
    /// </summary>
    private static bool HasUnsupportedAsyncClosure(MethodEnvironment env)
        => WrapperValidation.HasUnsupportedAsyncClosure(env);

    /// <summary>
    /// Checks whether any method parameter is a protocol existential type.
    /// </summary>
    private static bool HasProtocolExistentialParameter(MethodEnvironment env)
    {
        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (env.ExistentialHandler.IsExistential(arg.SwiftTypeSpec))
                return true;

            if (arg.SwiftTypeSpec is NamedTypeSpec namedSpec &&
                env.TypeDatabase.TryGetTypeRecord(namedSpec, out var typeRecord) &&
                (typeRecord.Kind == TypeRecordKind.Protocol || typeRecord.Kind == TypeRecordKind.Existential))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Shared function-level eligibility check for @_cdecl wrappers.
    /// Contains guards that apply to ALL wrapper paths (method, closure, optional-pointer, async, arrayslice).
    /// Per-param checks are NOT included — each wrapper path has its own param-level gates
    /// because different wrappers transform different param types before checking.
    /// </summary>
    internal static bool HasCdeclCompatibleFunctionShape(MethodEnvironment env)
        => WrapperValidation.HasCdeclCompatibleFunctionShape(env);

    /// <summary>
    /// Per-param check: is this argument a nested frozen struct?
    /// Extracted from HasNestedFrozenStructParameter for reuse by wrapper-owned paths.
    /// </summary>
    internal static bool IsNestedFrozenStructParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
        => WrapperValidation.IsNestedFrozenStructParam(arg, typeDatabase);

    /// <summary>
    /// Per-param check: is this argument a non-primitive frozen struct?
    /// Extracted from HasNonPrimitiveFrozenStructParameter for reuse by wrapper-owned paths.
    /// </summary>
    internal static bool IsNonPrimitiveFrozenStructParam(ArgumentDecl arg, ITypeDatabase typeDatabase)
        => WrapperValidation.IsNonPrimitiveFrozenStructParam(arg, typeDatabase);

    /// <summary>
    /// Checks if a parent decl is a non-copyable struct.
    /// </summary>
    private static bool IsNonCopyableStructParent(BaseDecl? parentDecl)
        => WrapperValidation.IsNonCopyableStructParent(parentDecl);

    /// <summary>
    /// Checks whether any parameter is a nested frozen struct type.
    /// </summary>
    private static bool HasNestedFrozenStructParameter(MethodEnvironment env)
        => env.MethodDecl.CSSignature.Skip(1).Any(arg => WrapperValidation.IsNestedFrozenStructParam(arg, env.TypeDatabase));

    /// <summary>
    /// Checks whether any parameter is a non-primitive frozen struct type.
    /// </summary>
    private static bool HasNonPrimitiveFrozenStructParameter(MethodEnvironment env)
        => env.MethodDecl.CSSignature.Skip(1).Any(arg => WrapperValidation.IsNonPrimitiveFrozenStructParam(arg, env.TypeDatabase));

    /// <summary>
    /// Checks whether any parameter or the return type is a generic container type
    /// that can't be handled by @_cdecl wrappers.
    /// Allows: Optional&lt;reference&gt; (nullable pointer ABI), Optional&lt;value-type&gt; (IndirectResult),
    /// Array, Dictionary, Set (UnsafeRawPointer transport).
    /// Blocks: Result&lt;T,E&gt;, Optional&lt;protocol existential&gt; (needs proxy conversion).
    /// </summary>
    private static bool HasUnsupportedGenericContainerParamsOrReturn(MethodEnvironment env)
    {
        var returnSpec = env.MethodDecl.CSSignature.First().SwiftTypeSpec;
        // Optional<class-bound existential> RETURNS are wrappable even though the shared
        // generic-container gate rejects Optional<existential>: the @_cdecl wrapper captures the
        // register-returned 2-word cell and writes it to the result buffer, sidestepping the raw
        // dispatch thunk's broken x8/sret assumption. Non-@objc parameters stay rejected (the loop
        // below) — only the return position gets that 2-word carve-out.
        //
        // @objc protocol existentials (Optional<any @objcP>) are wrappable in BOTH positions: an
        // @objc protocol's existential is a single 8-byte object pointer (AnyObject ABI, no witness
        // table), so the wrapper marshals it as a nullable pointer in/out — no proxy conversion, no
        // indirect container. Carve it out symmetrically, ahead of the per-position rejections.
        if (IsUnsupportedGenericContainer(returnSpec, env.TypeDatabase)
            && !WrapperValidation.IsOptionalClassBoundExistentialReturn(returnSpec, env.TypeDatabase)
            && !ExistentialHandler.IsObjCProtocolExistentialSpec(returnSpec, env.TypeDatabase))
            return true;

        foreach (var arg in env.MethodDecl.CSSignature.Skip(1))
        {
            if (IsUnsupportedGenericContainer(arg.SwiftTypeSpec, env.TypeDatabase)
                && !ExistentialHandler.IsObjCProtocolExistentialSpec(arg.SwiftTypeSpec, env.TypeDatabase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if a type is a generic container that can't be handled by @_cdecl wrappers.
    /// Allows: Optional&lt;value-type&gt; (IndirectResult), Optional&lt;reference&gt; (nullable pointer),
    /// Array, Dictionary, Set (UnsafeRawPointer transport).
    /// Blocks: Result&lt;T,E&gt;, Optional&lt;protocol existential&gt; (needs proxy conversion).
    /// </summary>
    internal static bool IsUnsupportedGenericContainer(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => WrapperValidation.IsUnsupportedGenericContainer(typeSpec, typeDatabase);

    /// <summary>
    /// Returns true for collection container types that can be transported through @_cdecl
    /// wrappers via UnsafeRawPointer + .load(as:) / resultPtr.initializeMemory(as:).
    /// </summary>
    internal static bool IsSupportedCollectionType(TypeSpec typeSpec)
        => WrapperValidation.IsSupportedCollectionType(typeSpec);

    /// <summary>
    /// Returns true for Swift.Optional&lt;T&gt; type specs (any generic parameter count > 0).
    /// </summary>
    internal static bool IsOptionalType(TypeSpec typeSpec)
        => WrapperValidation.IsOptionalType(typeSpec);

    /// <summary>
    /// Returns true for metatype types (Any.Type, T.Type, etc.) which are not
    /// C-representable in @_cdecl wrappers. The generator renders them as bare "Type"
    /// which doesn't exist in Swift, causing compilation errors.
    /// </summary>
    internal static bool IsMetatypeType(TypeSpec typeSpec)
        => WrapperValidation.IsMetatypeType(typeSpec);

    /// <summary>
    /// Returns true for Optional types that can be handled by @_cdecl wrappers:
    /// - Optional&lt;reference&gt;: nullable pointer ABI (UnsafeMutableRawPointer?)
    /// - Optional&lt;value-type&gt;: IndirectResult via resultPtr
    /// Returns false for Optional&lt;protocol existential&gt; which needs proxy conversion
    /// that the @_cdecl IndirectResult path doesn't handle.
    /// </summary>
    internal static bool IsOptionalSupportedForCdecl(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => WrapperValidation.IsOptionalSupportedForCdecl(typeSpec, typeDatabase);

    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent class support — protocol-based type erasure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a method on a generic parent type can be wrapped via @_cdecl.
    /// Delegates to <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/>.
    /// </summary>
    internal static bool CanEmitGenericWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
        => GenericDispatchEmitter.CanEmitGenericDispatch(env, parentTypeDecl, GenericDispatchKind.Method);

    /// <summary>
    /// Backward-compatible alias. Delegates to <see cref="GenericDispatchEmitter.CanEmitGenericDispatch"/>.
    /// </summary>
    internal static bool CanEmitGenericClassWrapper(MethodEnvironment env, TypeDecl parentTypeDecl)
        => GenericDispatchEmitter.CanEmitGenericDispatch(env, parentTypeDecl, GenericDispatchKind.Method);

    /// <summary>
    /// Returns true when a method needs the generic static protocol dispatch approach.
    /// Delegates to <see cref="GenericDispatchEmitter.NeedsStaticDispatch"/>.
    /// </summary>
    internal static bool NeedsGenericStaticDispatch(MethodEnvironment env, TypeDecl parentTypeDecl)
        => GenericDispatchEmitter.NeedsStaticDispatch(env, parentTypeDecl, GenericDispatchKind.Method);

    /// <summary>
    /// Checks whether any parameter or return type references generic type parameters.
    /// Delegates to <see cref="GenericDispatchEmitter.HasGenericTypeParamInSignature"/>.
    /// </summary>
    internal static bool HasGenericTypeParamInSignature(MethodEnvironment env, TypeDecl parentTypeDecl)
        => GenericDispatchEmitter.HasGenericTypeParamInSignature(env, parentTypeDecl);

    /// <summary>
    /// Recursively checks whether a TypeSpec references any of the given generic type parameter names.
    /// </summary>
    internal static bool TypeSpecReferencesGenericParam(TypeSpec spec, HashSet<string> genericParamNames)
        => WrapperValidation.TypeSpecReferencesGenericParam(spec, genericParamNames);

    /// <summary>
    /// Builds the Swift protocol method declaration string for protocol-based type erasure.
    /// The protocol declaration must exactly match the original method's signature
    /// (labels, types, throws) for the conformance to be valid.
    /// </summary>
    internal static string BuildProtocolMethodDeclaration(MethodDecl methodDecl, MethodEnvironment env)
    {
        var baseName = NameProvider.ParserNameToSwift(methodDecl);
        var keptArgs = methodDecl.CSSignature.Skip(1).ToList();

        var protocolParams = new List<string>();
        for (int i = 0; i < keptArgs.Count; i++)
        {
            var arg = keptArgs[i];
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg)) continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple) continue;

            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(arg.SwiftTypeSpec);

            // Determine external label. Provenance-aware: prefer the parser-captured
            // OriginalSwiftName so a label that genuinely begins with '_' (e.g. _self) is not
            // corrupted by the legacy underscore strip.
            string externalLabel;
            if (string.IsNullOrEmpty(arg.Name) || arg.Name == "_" || SwiftBuilder.IsAutoGeneratedArgName(arg.Name))
                externalLabel = "_";
            else
                externalLabel = arg.OriginalSwiftName
                    ?? (arg.Name.StartsWith("_") ? arg.Name.Substring(1) : arg.Name);

            // Determine internal name
            string internalName = !string.IsNullOrEmpty(arg.PrivateName) ? arg.PrivateName : externalLabel;
            if (internalName == "_") internalName = $"p{i}";

            // Format parameter declaration
            if (externalLabel == internalName)
                protocolParams.Add($"{externalLabel}: {swiftType}");
            else
                protocolParams.Add($"{externalLabel} {internalName}: {swiftType}");
        }

        var paramString = string.Join(", ", protocolParams);
        var throwsClause = methodDecl.Throws ? " throws" : "";

        // Return type
        var returnSpec = methodDecl.CSSignature.First().SwiftTypeSpec;
        string returnClause = returnSpec.IsEmptyTuple
            ? ""
            : $" -> {ExistentialBypassEmitter.RenderSwiftTypeSpecForReturnType(returnSpec)}";

        return $"func {baseName}({paramString}){throwsClause}{returnClause}";
    }

    /// <summary>
    /// Emits the protocol declaration, conformance extension, and modified self reconstruction
    /// for a method on a generic class type.
    /// Delegates to <see cref="GenericProtocolEmitter"/> for the shared protocol+conformance pattern.
    /// </summary>
    internal static void EmitGenericClassProtocolAndConformance(
        SwiftWriter swiftWriter, MethodDecl methodDecl, MethodEnvironment env,
        string symbolName, string moduleQualifiedSwiftName)
    {
        var methodSig = BuildProtocolMethodDeclaration(methodDecl, env);
        var extensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            methodDecl.AvailabilityAnnotations, env.ParentDecl);
        GenericProtocolEmitter.EmitProtocolAndConformance(
            swiftWriter, "P", symbolName, methodSig, moduleQualifiedSwiftName,
            originAnchor: FragmentOwners.ForDeclWrapper(methodDecl).Artifact,
            extensionAvailability: extensionAvailability);
    }

    /// <summary>
    /// Returns true if the given parent type declaration is a generic class type.
    /// Used to determine whether protocol-based type erasure is needed in emission.
    /// </summary>
    internal static bool IsGenericClassParent(BaseDecl? parentDecl)
        => WrapperValidation.IsGenericClassParent(parentDecl);

    /// <summary>
    /// Returns true when a method with Swift variadic parameters has a shape supported
    /// by the unsafeBitCast variadic bridge. Restricts the relaxed gate to static methods
    /// on non-generic parents with no closures, no inout, no throws, no method-own
    /// generics. These restrictions match what BindingTests covers; broader shapes can
    /// be unlocked incrementally with additional fixtures.
    /// </summary>
    internal static bool IsSupportedVariadicShape(MethodEnvironment env)
    {
        var methodDecl = env.MethodDecl;

        if (methodDecl.MethodType != MethodType.Static)
            return false;

        if (env.ParentDecl is TypeDecl td && td.IsGeneric)
            return false;

        if (methodDecl.GenericParameters.Count > 0)
            return false;

        if (methodDecl.Throws || methodDecl.IsAsync)
            return false;

        int variadicCandidates = 0;
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            if (arg.IsInOut)
                return false;
            if (env.ClosureHandler != null && env.ClosureHandler.IsClosure(arg))
                return false;

            if (arg.SwiftTypeSpec is NamedTypeSpec ns
                && (ns.Name == "Swift.Array" || ns.Name == "Array")
                && ns.GenericParameters.Count == 1)
            {
                variadicCandidates++;
            }
        }

        // Swift permits at most one variadic param per function. If a method has
        // HasVariadicParameter=true and the demangler didn't propagate IsVariadic on the
        // inner element (concrete-element case), IsVariadicArrayParam falls back to "the
        // single Array<T> param is the variadic". For mixed-Array methods (e.g. `(_ a: [Int],
        // _ b: Int...)`) that fallback is ambiguous — reject and fall through to the
        // [Obsolete(SB0001)] direct-CallConvSwift path so we don't generate a wrong wrapper.
        if (variadicCandidates != 1)
            return false;

        return true;
    }

    /// <summary>
    /// Builds the function-type signature for the variadic bitCast trick. With
    /// <paramref name="useArrayForm"/>=false, variadic Array params render as
    /// <c>Element...</c> (their source-level type, which picks the variadic overload
    /// via Swift's `as` cast). With <paramref name="useArrayForm"/>=true, they render
    /// as <c>[Element]</c> (the ABI-equivalent type the wrapper's local Array variable
    /// satisfies). Non-variadic params render to their Swift type in both forms.
    /// </summary>
    internal static string BuildVariadicCastSignature(MethodDecl methodDecl, bool useArrayForm)
    {
        var parts = new List<string>();
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;

            if (IsVariadicArrayParam(methodDecl, arg))
            {
                var elementSpec = ((NamedTypeSpec)arg.SwiftTypeSpec).GenericParameters[0];
                var elementType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(elementSpec);
                parts.Add(useArrayForm ? $"[{elementType}]" : $"{elementType}...");
            }
            else
            {
                var typeStr = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg.SwiftTypeSpec);
                parts.Add(typeStr);
            }
        }
        var paramList = string.Join(", ", parts);
        var returnType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(
            methodDecl.CSSignature.First().SwiftTypeSpec);
        return $"({paramList}) -> {returnType}";
    }

    /// <summary>
    /// True when <paramref name="arg"/> is the variadic parameter of <paramref name="methodDecl"/>.
    /// The demangler propagates <c>IsVariadic</c> on the inner element spec for generic-element
    /// variadics (<c>T...</c>), but NOT for concrete-element variadics (<c>VariadicSection...</c>) —
    /// see <c>SwiftABIParser</c> lines ~2828–2846. For the concrete-element case the only signal
    /// is method-level <c>HasVariadicParameter</c> (set from the swiftinterface). Swift permits at
    /// most one variadic param per function (and the result-builder shapes doc 14 targets always
    /// have exactly one <c>Array&lt;T&gt;</c>-shaped param), so when method-level variadic is set
    /// and the method has a single <c>Array&lt;T&gt;</c>-shaped param, that param IS the variadic.
    /// Ambiguous multi-<c>Array</c> methods are rejected in <c>IsSupportedVariadicShape</c>.
    /// </summary>
    /// <summary>
    /// True when <paramref name="methodDecl"/> has a sibling on the same parent type
    /// with the same <c>Name</c>, the same parameter type signature, and a different
    /// return type. This is the result-builder shape (<c>AppShortcutsBuilder.buildExpression</c>:
    /// <c>(AppShortcut) -&gt; AppShortcut</c> vs <c>(AppShortcut) -&gt; [AppShortcut]</c>)
    /// where Swift's overload resolution at the wrapper call site can pick the wrong
    /// overload. The caller forces selection via a function-reference <c>as</c> cast.
    /// </summary>
    internal static bool HasReturnTypeOnlyOverloadSibling(MethodDecl methodDecl, TypeDecl? parentTypeDecl)
    {
        if (parentTypeDecl == null) return false;
        // The disambiguation cast lives only in the direct (non-static-dispatch) branch
        // of EmitSwiftMethodWrapper. Generic-host methods route through
        // EmitGenericStaticDispatchMethod, which has no cast-call path — if this
        // predicate fires there, the wrapper would emit ambiguous bare call syntax
        // and fail to compile. Symmetric to IsSupportedVariadicShape's generic-parent
        // guard. Drop into ambiguity-tolerant emission rather than guess.
        if (parentTypeDecl.IsGeneric) return false;
        // BuildOverloadDisambiguationSignature emits `(P) -> R` with no `throws` /
        // `async`. Casting a throwing or async function to a non-effectful function
        // type is a Swift compile error, and the throwing-wrapper path still wraps
        // the call in `try`, which would then operate on a non-throwing cast result.
        // Drop into ambiguity-tolerant emission for effectful methods.
        if (methodDecl.Throws || methodDecl.IsAsync) return false;
        var myParams = methodDecl.CSSignature.Skip(1).ToList();
        var myReturn = methodDecl.CSSignature.First().SwiftTypeSpec;
        foreach (var other in parentTypeDecl.Methods)
        {
            if (ReferenceEquals(other, methodDecl)) continue;
            if (other.Name != methodDecl.Name) continue;
            if (other.IsConstructor != methodDecl.IsConstructor) continue;
            if (other.IsAccessor != methodDecl.IsAccessor) continue;
            if (other.MethodType != methodDecl.MethodType) continue;
            var otherParams = other.CSSignature.Skip(1).ToList();
            if (otherParams.Count != myParams.Count) continue;
            bool paramsMatch = true;
            for (int i = 0; i < myParams.Count; i++)
            {
                if (!Equals(myParams[i].SwiftTypeSpec, otherParams[i].SwiftTypeSpec))
                {
                    paramsMatch = false;
                    break;
                }
            }
            if (!paramsMatch) continue;
            // Argument labels must also match. The disambiguation `as` cast pins by TYPE
            // only and is called positionally (labels stripped), so it can only ever select
            // a genuine return-type overload — siblings that share base name, parameter
            // types, AND argument labels, differing solely by return type (e.g. a result
            // builder's buildExpression(_:) -> X vs -> [X]). When labels differ, the two
            // overloads are distinguishable by an ordinary labeled call
            // (obj.tableView(_:viewForHeaderInSection:) vs (_:numberOfRowsInSection:)), and
            // the label-erasing cast is both unnecessary and unsafe: pinning by type can
            // match an inherited sibling of the same type but a different label
            // (UITableViewDelegate's viewForHeaderInSection vs viewForFooterInSection),
            // producing "ambiguous use of 'tableView'". Only treat this as a return-type
            // overload — and emit the cast — when the labels are identical.
            bool labelsMatch = true;
            for (int i = 0; i < myParams.Count; i++)
            {
                if (EffectiveArgLabel(myParams[i]) != EffectiveArgLabel(otherParams[i]))
                {
                    labelsMatch = false;
                    break;
                }
            }
            if (!labelsMatch) continue;
            var otherReturn = other.CSSignature.First().SwiftTypeSpec;
            if (!Equals(myReturn, otherReturn)) return true;
        }
        return false;
    }

    /// <summary>
    /// The external Swift argument label for a parameter, normalized so that an unlabeled
    /// slot (<c>_</c>, empty, or an auto-generated <c>argN</c> name) compares equal across
    /// methods. Mirrors the label notion used by CdeclParamMapper's call-site emission, so
    /// two overloads compare "same labels" iff they are indistinguishable by a labeled call.
    /// </summary>
    private static string EffectiveArgLabel(ArgumentDecl arg)
    {
        var name = arg.Name;
        if (string.IsNullOrEmpty(name) || name == "_" || SwiftBuilder.IsAutoGeneratedArgName(name))
            return "_";
        // Mirror CdeclParamMapper.BuildSwiftCallArgLabel's recovery: prefer the parser-captured
        // original (so `_self` and `self` compare distinct, as they are in Swift), and only fall
        // back to the legacy underscore strip when no original was captured.
        return arg.OriginalSwiftName ?? (name.StartsWith("_") ? name.Substring(1) : name);
    }

    /// <summary>
    /// Builds a Swift function-type signature for <paramref name="methodDecl"/> in the
    /// form <c>(P1, P2, ...) -&gt; R</c>, used as the right-hand side of an <c>as</c>
    /// cast to disambiguate overloads at the call site.
    /// </summary>
    internal static string BuildOverloadDisambiguationSignature(MethodDecl methodDecl)
    {
        var parts = new List<string>();
        foreach (var arg in methodDecl.CSSignature.Skip(1))
        {
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            parts.Add(ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(arg.SwiftTypeSpec));
        }
        var paramList = string.Join(", ", parts);
        var returnType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(
            methodDecl.CSSignature.First().SwiftTypeSpec);
        return $"({paramList}) -> {returnType}";
    }

    // Strips a leading `label: ` from a CdeclParamMapper-generated call arg. Swift
    // function values (variadic bitcast, overload disambiguation `as` cast) are
    // called positionally and reject `(f as (Int) -> Int)(x: 1)`.
    internal static string StripArgLabel(string callArg)
    {
        if (string.IsNullOrEmpty(callArg)) return callArg;
        int colon = callArg.IndexOf(':');
        if (colon <= 0) return callArg;
        for (int i = 0; i < colon; i++)
        {
            char c = callArg[i];
            bool ok = char.IsLetterOrDigit(c) || c == '_';
            if (i == 0) ok = char.IsLetter(c) || c == '_';
            if (!ok) return callArg;
        }
        int valStart = colon + 1;
        while (valStart < callArg.Length && callArg[valStart] == ' ') valStart++;
        return callArg.Substring(valStart);
    }

    internal static bool IsVariadicArrayParam(MethodDecl methodDecl, ArgumentDecl arg)
    {
        if (arg.SwiftTypeSpec is not NamedTypeSpec ns) return false;
        if (ns.Name != "Swift.Array" && ns.Name != "Array") return false;
        if (ns.GenericParameters.Count != 1) return false;
        if (ns.GenericParameters[0].IsVariadic) return true;
        if (!methodDecl.HasVariadicParameter) return false;
        int arrayParamCount = 0;
        foreach (var other in methodDecl.CSSignature.Skip(1))
        {
            if (other.SwiftTypeSpec is NamedTypeSpec on
                && (on.Name == "Swift.Array" || on.Name == "Array")
                && on.GenericParameters.Count == 1)
            {
                arrayParamCount++;
            }
        }
        return arrayParamCount == 1;
    }
}
