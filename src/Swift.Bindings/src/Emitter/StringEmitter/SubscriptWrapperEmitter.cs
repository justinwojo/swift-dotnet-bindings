// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-subscript @_cdecl Swift wrappers that route subscript accessor P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each subscript accessor, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (self as pointer, index params via GetCdeclParamMapping, newValue for setters)
/// - Reconstructs self and index params, accesses subscript via bracket syntax
/// - Returns the result via C ABI (appropriate type mapping per GetCdeclReturnMapping)
///
/// Modeled after PropertyWrapperEmitter but with index parameter support.
/// State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class SubscriptWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a subscript accessor should use a @_cdecl wrapper.
    /// Checked per-accessor (getter/setter signatures differ due to newValue). Boolean shim
    /// over <see cref="EvaluateWrapperEligibility"/> — single source of truth (Finding 12).
    /// </summary>
    public static bool ShouldEmitSubscriptWrapper(SubscriptDecl subscriptDecl, AccessorDecl accessor, MethodEnvironment env)
        => EvaluateWrapperEligibility(subscriptDecl, accessor, env).IsWrappable;

    /// <summary>
    /// Single eligibility traversal for subscript @_cdecl wrappers: returns whether the accessor
    /// will be wrapped and, if not, the first guard that rejected it. <see cref="ShouldEmitSubscriptWrapper"/>
    /// is its boolean shim and <see cref="GetRejectionReason"/> its diagnostic shim, so the predicate
    /// and the rejection reason can never drift (Finding 12).
    /// </summary>
    public static WrapperEligibility EvaluateWrapperEligibility(SubscriptDecl subscriptDecl, AccessorDecl accessor, MethodEnvironment env)
    {
        // Shared guards: xcframework, non-copyable, actor
        // Note: Subscript passes actor info from the accessor, not the subscript itself
        var memberReason = WrapperValidation.GetMemberRejectionReason(env, MemberKind.Subscript,
            isActorIsolated: accessor.Method.IsActorIsolated,
            isMainActorIsolated: accessor.Method.IsMainActorIsolated,
            isNonisolated: accessor.Method.IsNonisolated);
        if (memberReason != null)
            return WrapperEligibility.Reject(memberReason);

        // 2. Generic parent type — allow non-final class instance subscripts with concrete signatures
        if (env.ParentDecl is TypeDecl td && td.IsGeneric)
        {
            if (!CanEmitGenericClassSubscriptWrapper(subscriptDecl, td))
                return WrapperEligibility.Reject("generic_parent_type");
        }

        // 3. Not static (static subscripts aren't C# indexers)
        if (subscriptDecl.IsStatic)
            return WrapperEligibility.Reject("static_subscript");

        // 4. No closure index params
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (env.ClosureHandler.IsClosure(param))
                return WrapperEligibility.Reject("closure_index_param");
        }

        // 5. No async accessors
        if (accessor.Method.IsAsync)
            return WrapperEligibility.Reject("async_accessor");

        // 6b. No metatype return type or index parameters (including Optional<Metatype>).
        // Setter newValue uses ReturnTypeSpec too, so the same gate covers both accessors.
        // Same boundary as the method/property/constructor wrapper gates.
        if (WrapperValidation.IsMetatypeTypeIncludingOptional(subscriptDecl.ReturnTypeSpec))
            return WrapperEligibility.Reject("metatype_return");
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (WrapperValidation.IsMetatypeTypeIncludingOptional(param.SwiftTypeSpec))
                return WrapperEligibility.Reject("metatype_index_param");
        }

        // 7. No opaque return type (some Protocol)
        if (subscriptDecl.ReturnTypeSpec is ProtocolListTypeSpec { IsOpaque: true })
            return WrapperEligibility.Reject("opaque_return_type");

        // 7a. Optional<protocol existential> element, GETTER only — the same carve-out the property
        //     wrapper gate takes before its own container check. Optional<ExistentialContainer> is
        //     40+ bytes on arm64, too large to come back in registers under CallConvSwift, so the
        //     wrapper is the correct route rather than a declined one; it returns through the
        //     indirect result buffer.
        //
        //     The SETTER stays with the container check below: writing an Optional<any P> IN needs
        //     the existential re-boxed from the managed proxy, which nothing on the wrapper path
        //     emits yet — for properties either. That is a missing inbound mechanism, not a gate
        //     to widen, so the setter keeps the direct route until it exists.
        //
        //     This exempts the element from the container check ONLY. It is not a verdict on the
        //     rest of the signature: an early `Wrappable` here would carry the index parameters
        //     past every gate below it, so a nested by-value index or a raw ABI generic would
        //     reach the wrapper unexamined purely because the ELEMENT happened to take this arm.
        var isOptionalExistentialGetter =
            accessor is GetAccessorDecl &&
            CdeclParamMapper.IsProtocolExistentialType(subscriptDecl.ReturnTypeSpec, env.TypeDatabase) &&
            WrapperValidation.IsOptionalType(subscriptDecl.ReturnTypeSpec);

        // 8. No unsupported generic container params/returns (Result<T,E>, Optional<existential>).
        //    Optional<value-type> allowed (IndirectResult). Array/Dictionary/Set allowed (UnsafeRawPointer transport).
        if (!isOptionalExistentialGetter &&
            WrapperValidation.IsUnsupportedGenericContainer(subscriptDecl.ReturnTypeSpec, env.TypeDatabase))
            return WrapperEligibility.Reject("unsupported_generic_container");

        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (WrapperValidation.IsUnsupportedGenericContainer(param.SwiftTypeSpec, env.TypeDatabase))
                return WrapperEligibility.Reject("unsupported_generic_container_param");
        }

        // 9. No closure return types
        if (subscriptDecl.ReturnTypeSpec is ClosureTypeSpec)
            return WrapperEligibility.Reject("closure_return_type");

        // 10. Tuple return types: allowed — routed through IndirectResult (resultPtr buffer).

        // 11. Nested type returns — ALLOWED, on the same terms the method and property wrapper
        //     gates already grant. Only the @_cdecl SIGNATURE has to be C-representable, and it
        //     never names the nested type: an indirect result travels through a resultPtr buffer
        //     and a class pointer through UnsafeMutableRawPointer. `Outer.Inner` appears only in
        //     the wrapper BODY (`resultPtr.initializeMemory(as: Outer.Inner.self, ...)`), which is
        //     ordinary Swift. The setter direction is symmetric — newValue arrives as an
        //     UnsafeRawPointer and is read back with `load(as: Outer.Inner.self)`.

        // 12. Nested/non-primitive frozen struct index parameters — ALLOWED wherever the nested
        //     name stays in the wrapper body. Index parameters are lowered by the same
        //     CdeclParamMapper the method wrapper uses, which transports a custom frozen struct as
        //     UnsafeRawPointer and reconstructs it in the body. The older refusal described passing
        //     such a struct BY VALUE, which is the one arm that still applies: the mapper keeps it
        //     for system/Apple frozen structs on a module-based test a nested type also satisfies.
        if (subscriptDecl.IndexParameters.Any(p => WrapperValidation.IsByValueNestedStructParam(p, env)))
            return WrapperEligibility.Reject("nested_frozen_struct_index_param");

        // 14. Skip subscripts with raw ABI generic type params (τ_0_0) in return type or index params.
        // These leak from parent type generics and cause Swift compilation failures.
        if (subscriptDecl.ReturnTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(subscriptDecl.ReturnTypeSpec))
            return WrapperEligibility.Reject("raw_generic_type_params");
        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (param.SwiftTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(param.SwiftTypeSpec))
                return WrapperEligibility.Reject("raw_generic_type_params");
        }

        return WrapperEligibility.Wrappable;
    }

    /// <summary>
    /// Returns a human-readable skip reason if the subscript wrapper would be rejected, or null if it
    /// passes all gates. Diagnostic shim over <see cref="EvaluateWrapperEligibility"/> (Finding 12).
    /// </summary>
    public static string? GetRejectionReason(SubscriptDecl subscriptDecl, AccessorDecl accessor, MethodEnvironment env)
        => EvaluateWrapperEligibility(subscriptDecl, accessor, env).Reason;

    /// <summary>
    /// Gets the @_cdecl symbol name for a subscript accessor wrapper.
    /// </summary>
    public static string GetSubscriptAccessorSymbolName(string moduleName, string typeName, string mangledName, bool isGetter)
    {
        var hash = EmitterUtility.DeterministicHash8(mangledName);
        var safeTypeName = typeName.Replace(".", "_");
        var prefix = isGetter ? "SubGet" : "SubSet";
        return $"SBW_{prefix}_{moduleName}_{safeTypeName}_{hash}";
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper for a subscript getter.
    /// Params: [resultPtr if needed] → [index params via GetCdeclParamMapping] → self_
    /// Body: reconstruct self, reconstruct index params, let result = obj[key1, key2, ...]
    /// </summary>
    public static void EmitSwiftSubscriptGetterWrapper(
        SwiftWriter swiftWriter,
        SubscriptDecl subscriptDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();

        // subscript getters share the property bucket by ABI convention,
        // but Swift's mangling shapes subscript accessors with a distinct `_subscript`
        // discriminator that is structurally disjoint from any property/method/constructor
        // mangling. The per-kind dedup gate is collision-safe.
        // Attribute on the accessor axis so the getter and setter wrappers of one subscript
        // do not collapse onto a single ArtifactId. Subscripts take the SubscriptGetter/
        // SubscriptSetter kinds, not the property Getter/Setter ones: every report and skip
        // row for a subscript accessor is built with those, and the accessor kind is a field
        // of the canonical id, so the property kinds would produce an owner id that can never
        // join against the reporting identity for the same accessor.
        if (!ctx.TryAddPropertyWrapperSymbol(symbolName, DeclIdFactory.ForSubscript(subscriptDecl, AccessorKind.SubscriptGetter)))
            return; // Already emitted

        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl?.SwiftTypeName == null) return;
        var moduleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        bool isClass = env.ParentDecl is ClassDecl;
        bool isString = WitnessDispatchEmitter.IsStringType(subscriptDecl.ReturnTypeSpec);
        bool isNonCopyableParent = !isClass && WrapperValidation.IsNonCopyableStructParent(env.ParentDecl);

        // Determine return mapping
        var (returnMapping, needsResultPtr) = isString
            ? (new CdeclReturnMapping("SBW_Utf8Slice", CdeclReturnKind.String), true)
            : CdeclReturnMapping.Classify(subscriptDecl.ReturnTypeSpec, env.TypeDatabase);

        if (isString)
            needsResultPtr = true;

        // Ensure SBW_Utf8Slice infrastructure is emitted for string returns
        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Build Swift parameter list — phase ordering from CdeclSignatureContract.
        // ResultPtr is handled outside the loop using the emitter's own needsResultPtr logic.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var callArgs = new List<string>();
        bool isGenericParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        if (needsResultPtr)
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");

        var order = CdeclSignatureContract.DetermineParameterOrder(env, overrideNeedsResultPtr: needsResultPtr);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    break; // Already handled above

                case CdeclPhase.Arguments:
                    // Sibling bindings so a reserved-name escape also dodges a sibling index param.
                    var indexSiblings = CdeclParamMapper.CollectSiblingBindingNames(subscriptDecl.IndexParameters);
                    foreach (var param in subscriptDecl.IndexParameters)
                    {
                        if (param.SwiftTypeSpec.IsEmptyTuple)
                            continue;

                        var label = !string.IsNullOrEmpty(param.PrivateName) ? param.PrivateName : param.Name;
                        var (cdeclParam, reconstruction, callArg) = CdeclParamMapper.Map(
                            param, label, env, omitLabels: false, useUtf8Strings: true, reservedSiblings: indexSiblings);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                            reconstructionLines.Add(reconstruction);
                        callArgs.Add(FixSubscriptCallArg(callArg, param));
                    }
                    break;

                case CdeclPhase.Metadata:
                    if (isGenericParent && parentTypeDecl != null)
                    {
                        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
                        {
                            swiftParams.Add($"_ _metadata{i}: UnsafeRawPointer");
                        }
                        // C# side (HandleProtocolConformance) emits PWT pointers for resolvable
                        // protocol constraints on the parent's generic parameters. The wrapper
                        // must absorb them here even when the body doesn't use them, otherwise the
                        // PWT pointer slides into the self_ slot.
                        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, env.TypeDatabase);
                        for (int i = 0; i < pwtCount; i++)
                        {
                            swiftParams.Add($"_ _pwt{i}: UnsafeRawPointer");
                        }
                    }
                    break;

                case CdeclPhase.Self:
                    if (isClass)
                        swiftParams.Add("_ self_: UnsafeMutableRawPointer");
                    else
                        swiftParams.Add("_ self_: UnsafeRawPointer");
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Return clause
        string returnClause = needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";

        var swiftFuncName = $"_sbw_subget_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Build bracket access expression
        // For noncopyable types, use inline pointer borrow instead of obj
        var selfExpr = isNonCopyableParent
            ? $"self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee"
            : "obj";
        var subscriptAccess = BuildSubscriptAccessExpr(selfExpr, callArgs);

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericParent)
        {
            protocolName = EmitGetterProtocolAndConformance(
                swiftWriter, subscriptDecl, symbolName, moduleQualifiedName, parentTypeDecl!, env.TypeDatabase);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Subscript getter @_cdecl wrapper for {{moduleQualifiedName}}.subscript.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // Emit availability annotations from the member and ancestor chain.
        // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
        var availability = WrapperEmitterHelpers.MergeAvailability(subscriptDecl.AvailabilityAnnotations, env.ParentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, availability);

        // Add @MainActor when wrapping @MainActor-isolated subscripts.
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            env.ParentDecl, env.MethodDecl.IsMainActorIsolated, env.MethodDecl.IsNonisolated);
        if (needsMainActor)
        {
            swiftWriter.WriteLines($$"""
                @MainActor
                @_cdecl("{{symbolName}}")
                """);
        }
        else
        {
            swiftWriter.WriteLines($$"""
                @_cdecl("{{symbolName}}")
                """);
        }
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Emit parameter reconstruction lines
        foreach (var line in reconstructionLines)
            swiftWriter.WriteLine(line);

        // Reconstruct self (skip for noncopyable — inline pointer borrow used in subscriptAccess)
        if (isGenericParent && protocolName != null)
        {
            SelfReconstructionEmitter.EmitProtocolCast(swiftWriter, protocolName, isMutable: false);
        }
        else if (!isNonCopyableParent)
        {
            EmitSelfReconstruction(swiftWriter, isClass, moduleQualifiedName);
        }

        // Emit return based on type category
        if (isString)
        {
            EmitStringGetterBody(swiftWriter, subscriptAccess);
        }
        else if (needsResultPtr)
        {
            // Ask the shared renderer rather than the bare one: a protocol element has to reach
            // the metatype as `any P`, and inside a generic argument it has to be parenthesized
            // (`Optional<(any P)>`). The bare renderer drops the keyword entirely, which the
            // compiler still accepts in the Swift 5 language mode and rejects outright in Swift 6
            // — and a wrapper that stops compiling is withdrawn along with its accessor group,
            // so the member would disappear rather than fail loudly.
            var swiftType = CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(
                subscriptDecl.ReturnTypeSpec, env.TypeDatabase);
            var metatype = swiftType.StartsWith("any ") ? $"({swiftType}).self" : $"{swiftType}.self";
            swiftWriter.WriteLine($"let result = {subscriptAccess}");
            swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else
        {
            EmitDirectReturn(swiftWriter, subscriptAccess, subscriptDecl.ReturnTypeSpec, env.TypeDatabase, returnMapping);
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper for a subscript setter.
    /// Params: [newValue params via GetCdeclParamMapping] → [index params] → self_ (mutable)
    /// Body: reconstruct self/params, obj[key1, key2, ...] = newValueVal
    /// </summary>
    public static void EmitSwiftSubscriptSetterWrapper(
        SwiftWriter swiftWriter,
        SubscriptDecl subscriptDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();

        // subscript setters carry Swift's `_subscript`-discriminated
        // setter mangling — disjoint from getters, properties, methods, and constructors.
        // The per-kind dedup gate is collision-safe.
        // Attributed on the accessor axis, with the subscript-specific kind, for the same
        // reasons the getter is.
        if (!ctx.TryAddPropertyWrapperSymbol(symbolName, DeclIdFactory.ForSubscript(subscriptDecl, AccessorKind.SubscriptSetter)))
            return; // Already emitted

        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl?.SwiftTypeName == null) return;
        var moduleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        bool isClass = env.ParentDecl is ClassDecl;
        bool isString = WitnessDispatchEmitter.IsStringType(subscriptDecl.ReturnTypeSpec);

        // Build parameter list — phase ordering from CdeclSignatureContract.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        var callArgs = new List<string>();
        string? newValueCallArgExpr = null;
        bool isGenericParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        var order = CdeclSignatureContract.DetermineParameterOrder(env,
            overrideNeedsResultPtr: false, overrideHasArguments: true);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.Arguments:
                    // NewValue parameter(s)
                    if (isString)
                    {
                        swiftParams.Add("_ utf8Ptr: UnsafePointer<UInt8>");
                        swiftParams.Add("_ utf8Len: Int");
                        reconstructionLines.Add("let newValue = String(bytes: UnsafeBufferPointer(start: utf8Ptr, count: utf8Len), encoding: .utf8)!");
                    }
                    else
                    {
                        var newValueArg = new ArgumentDecl
                        {
                            SwiftTypeSpec = subscriptDecl.ReturnTypeSpec,
                            Name = "newValue",
                            PrivateName = "newValue",
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = null
                        };
                        // omitLabels: false — setters always need .load(as:) reconstruction for large Optionals.
                        // escapeReservedCollision: false — `newValue` IS the injected setter-value synthetic
                        // (referenced by bare name in the body), not a colliding user binding. The user *index*
                        // params below keep the default escape so a `subscript(newValue:)` index renames instead.
                        var (cdeclParam, reconstruction, callArgExpr) = CdeclParamMapper.Map(
                            newValueArg, "newValue", env, omitLabels: false, escapeReservedCollision: false);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                        {
                            reconstructionLines.Add(reconstruction);
                            // Capture the value expression (e.g., "newValueOpt") for the setter body
                            var colonIdx = callArgExpr.IndexOf(':');
                            newValueCallArgExpr = colonIdx >= 0 ? callArgExpr[(colonIdx + 2)..] : callArgExpr;
                        }
                    }

                    // Index parameters. Siblings dodge sibling index bindings; the injected
                    // `newValue` synthetic is already covered by the global reserved set.
                    var setterIndexSiblings = CdeclParamMapper.CollectSiblingBindingNames(subscriptDecl.IndexParameters);
                    foreach (var param in subscriptDecl.IndexParameters)
                    {
                        if (param.SwiftTypeSpec.IsEmptyTuple)
                            continue;

                        var label = !string.IsNullOrEmpty(param.PrivateName) ? param.PrivateName : param.Name;
                        var (cdeclParam, reconstruction, callArg) = CdeclParamMapper.Map(
                            param, label, env, omitLabels: false, useUtf8Strings: true, reservedSiblings: setterIndexSiblings);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                            reconstructionLines.Add(reconstruction);
                        callArgs.Add(FixSubscriptCallArg(callArg, param));
                    }
                    break;

                case CdeclPhase.Metadata:
                    if (isGenericParent && parentTypeDecl != null)
                    {
                        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
                        {
                            swiftParams.Add($"_ _metadata{i}: UnsafeRawPointer");
                        }
                        // C# side (HandleProtocolConformance) emits PWT pointers for resolvable
                        // protocol constraints on the parent's generic parameters. Mirror the
                        // getter so self_ stays aligned with the C# P/Invoke layout.
                        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, env.TypeDatabase);
                        for (int i = 0; i < pwtCount; i++)
                        {
                            swiftParams.Add($"_ _pwt{i}: UnsafeRawPointer");
                        }
                    }
                    break;

                case CdeclPhase.Self:
                    // Always mutable for setters
                    swiftParams.Add("_ self_: UnsafeMutableRawPointer");
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);
        var swiftFuncName = $"_sbw_subset_{EmitterUtility.DeterministicHash8(symbolName)}";

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericParent)
        {
            protocolName = EmitSetterProtocolAndConformance(
                swiftWriter, subscriptDecl, symbolName, moduleQualifiedName, parentTypeDecl!, env.TypeDatabase);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Subscript setter @_cdecl wrapper for {{moduleQualifiedName}}.subscript.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // Emit availability annotations from the member and ancestor chain.
        // @_cdecl wrappers are top-level functions and don't inherit enclosing type availability.
        var setterAvailability = WrapperEmitterHelpers.MergeAvailability(subscriptDecl.AvailabilityAnnotations, env.ParentDecl);
        WrapperEmitterHelpers.EmitSwiftAvailability(swiftWriter, setterAvailability);

        // Add @MainActor when wrapping @MainActor-isolated subscripts.
        bool needsMainActorSetter = WrapperValidation.NeedsMainActorAnnotation(
            env.ParentDecl, env.MethodDecl.IsMainActorIsolated, env.MethodDecl.IsNonisolated);
        if (needsMainActorSetter)
        {
            swiftWriter.WriteLines($$"""
                @MainActor
                @_cdecl("{{symbolName}}")
                """);
        }
        else
        {
            swiftWriter.WriteLines($$"""
                @_cdecl("{{symbolName}}")
                """);
        }
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}) {{");
        swiftWriter.Indent++;

        // Emit reconstruction lines
        foreach (var line in reconstructionLines)
            swiftWriter.WriteLine(line);

        // Get the value expression (may use suffix from GetCdeclParamMapping, e.g., "newValueOpt")
        string valueExpr = isString ? "newValue" :
            (newValueCallArgExpr ?? (reconstructionLines.Any(l => l.Contains("newValueVal")) ? "newValueVal" : "newValue"));

        // Build bracket access and emit assignment
        var subscriptAccess = BuildSubscriptAccessExpr(
            isClass ? "obj" : $"self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee",
            callArgs);

        var setterIndexArgs = string.Join(", ", callArgs);
        if (isGenericParent && protocolName != null)
        {
            SelfReconstructionEmitter.EmitProtocolCast(swiftWriter, protocolName, isMutable: true);
            swiftWriter.WriteLine($"obj[{setterIndexArgs}] = {valueExpr}");
        }
        else if (isClass)
        {
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
            swiftWriter.WriteLine($"obj[{setterIndexArgs}] = {valueExpr}");
        }
        else
        {
            swiftWriter.WriteLine($"self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee[{setterIndexArgs}] = {valueExpr}");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Builds a subscript access expression: obj[label1: arg1, label2: arg2, ...]
    /// Swift subscript bracket syntax requires argument labels when the subscript
    /// declaration uses labeled parameters (e.g., subscript(bitAt index: Int)).
    /// Unlabeled params (argLabel is empty) pass through without a label.
    /// </summary>
    private static string BuildSubscriptAccessExpr(string selfExpr, List<string> callArgs)
    {
        var indexArgs = string.Join(", ", callArgs);
        return $"{selfExpr}[{indexArgs}]";
    }

    /// <summary>
    /// Builds the index-parameter list for an existential-bypass subscript protocol declaration
    /// in the explicit `<external> <internal>:` form. Subscripts default to NO external label
    /// when written as `subscript(name: Type)`, so emitting just the label would suppress the
    /// real subscript's argument label and break witness matching on the bypass conformance.
    /// </summary>
    private static List<string> BuildBypassSubscriptIndexParams(SubscriptDecl subscriptDecl)
    {
        var indexParams = new List<string>();
        for (int i = 0; i < subscriptDecl.IndexParameters.Count; i++)
        {
            var param = subscriptDecl.IndexParameters[i];
            var externalLabel = NameProvider.GetSubscriptExternalLabel(param);
            var internalName = $"arg{i}";
            var paramType = ExistentialBypassEmitter.RenderSwiftTypeSpec(param.SwiftTypeSpec);
            indexParams.Add($"{externalLabel} {internalName}: {paramType}");
        }
        return indexParams;
    }

    /// <summary>
    /// Fixes the call argument from GetCdeclParamMapping for subscript bracket syntax.
    /// GetCdeclParamMapping generates "label: value" using arg.Name, but for subscripts
    /// declared as <c>subscript(name: T)</c> / <c>subscript(_ name: T)</c> the bracket
    /// expression must have NO label. Driven by <see cref="ArgumentDecl.IsUnlabeledSubscriptIndex"/>
    /// rather than a name pattern, so a real label literally named <c>index0</c> is preserved.
    /// </summary>
    private static string FixSubscriptCallArg(string callArg, ArgumentDecl param)
    {
        if (!param.IsUnlabeledSubscriptIndex)
            return callArg;

        // Strip the leading "label: " — bracket syntax for unlabeled subscripts is positional.
        var colonIdx = callArg.IndexOf(':');
        if (colonIdx >= 0)
            return callArg.Substring(colonIdx + 1).Trim();
        return callArg;
    }

    /// <summary>
    /// Emits self reconstruction for subscript getter/setter.
    /// Delegates to <see cref="SelfReconstructionEmitter.Emit"/>.
    /// </summary>
    private static void EmitSelfReconstruction(SwiftWriter swiftWriter, bool isClass, string moduleQualifiedName)
    {
        SelfReconstructionEmitter.Emit(swiftWriter, isClass, isMutating: false, moduleQualifiedName);
    }

    /// <summary>
    /// Emits the string getter body using SBW_Utf8Slice pattern.
    /// Delegates to <see cref="StringReturnEmitter.EmitGetterBody"/>.
    /// </summary>
    private static void EmitStringGetterBody(SwiftWriter swiftWriter, string propAccess)
    {
        StringReturnEmitter.EmitGetterBody(swiftWriter, propAccess);
    }

    private static void EmitDirectReturn(SwiftWriter swiftWriter, string expr,
        TypeSpec typeSpec, ITypeDatabase typeDatabase, CdeclReturnMapping mapping)
        => CdeclReturnRenderer.Write(swiftWriter, expr, typeSpec, typeDatabase, mapping, scalarParens: true);

    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent class support — protocol-based type erasure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a subscript on a generic parent type can be wrapped via @_cdecl
    /// using protocol-based type erasure.
    /// </summary>
    private static bool CanEmitGenericClassSubscriptWrapper(
        SubscriptDecl subscriptDecl, TypeDecl parentTypeDecl)
    {
        // Only class types — protocol dispatch via existential cast
        if (parentTypeDecl is not ClassDecl)
            return false;

        // Static subscripts don't use self-based erasure
        if (subscriptDecl.IsStatic)
            return false;

        // Return type and all index param types must not reference parent's generic type params
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();

        if (MethodWrapperEmitter.TypeSpecReferencesGenericParam(subscriptDecl.ReturnTypeSpec, genericParamNames))
            return false;

        foreach (var param in subscriptDecl.IndexParameters)
        {
            if (MethodWrapperEmitter.TypeSpecReferencesGenericParam(param.SwiftTypeSpec, genericParamNames))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a subscript getter on a generic class type.
    /// </summary>
    private static string EmitGetterProtocolAndConformance(
        SwiftWriter swiftWriter, SubscriptDecl subscriptDecl, string symbolName,
        string moduleQualifiedName, TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        var protocolName = $"_SBW_SG_{EmitterUtility.DeterministicHash8(symbolName)}";
        // A protocol element has to be spelled `any P` in the bypass protocol's own signature
        // too, and parenthesized when it sits inside a generic argument (`Optional<(any P)>`).
        // The bare renderer drops the keyword, which the Swift 5 language mode accepts with a
        // warning and Swift 6 rejects outright — and a bypass protocol that does not compile is
        // withdrawn along with the accessor group, so the member disappears rather than failing
        // loudly. Only the existential shape is re-rendered; every other return type keeps the
        // spelling this declaration has always used.
        var returnSwiftType = CdeclParamMapper.IsProtocolExistentialType(subscriptDecl.ReturnTypeSpec, typeDatabase)
            ? CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(subscriptDecl.ReturnTypeSpec, typeDatabase)
            : ExistentialBypassEmitter.RenderSwiftTypeSpec(subscriptDecl.ReturnTypeSpec);

        // Build subscript signature for protocol. Swift subscripts default to NO external label
        // when only one name is written (`subscript(at: Int)` parses as external=_, internal=at),
        // so emit the explicit `<external> <internal>:` form to preserve the real subscript's
        // argument label when the bypass type's witness is type-checked.
        var indexParams = BuildBypassSubscriptIndexParams(subscriptDecl);
        var indexParamString = string.Join(", ", indexParams);

        // Conformance extensions are top-level decls and don't inherit the enclosing
        // type's availability. Merge member + parent-chain annotations so the extension
        // type-checks against the deployment target.
        var extensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            subscriptDecl.AvailabilityAnnotations, parentTypeDecl);
        var extensionAvailPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            extensionAvailability, string.Empty);

        // The dispatch protocol + conformance extension carry no @_cdecl symbol; the anchor pins
        // both symbol-less blocks to the subscript that owns them so a wrapper-compile failure inside
        // either attributes to it rather than the coarse module scope, and the post-processor strips
        // the anchor with the block it names.
        var originAnchor = OriginAnchorEmitter.LineForWrapper(subscriptDecl);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            private protocol {{protocolName}} {
                subscript({{indexParamString}}) -> {{returnSwiftType}} { get }
            }
            {{originAnchor}}
            {{extensionAvailPrefix}}extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a subscript setter on a generic class type.
    /// </summary>
    private static string EmitSetterProtocolAndConformance(
        SwiftWriter swiftWriter, SubscriptDecl subscriptDecl, string symbolName,
        string moduleQualifiedName, TypeDecl parentTypeDecl, ITypeDatabase typeDatabase)
    {
        var protocolName = $"_SBW_SS_{EmitterUtility.DeterministicHash8(symbolName)}";
        // A protocol element has to be spelled `any P` in the bypass protocol's own signature
        // too, and parenthesized when it sits inside a generic argument (`Optional<(any P)>`).
        // The bare renderer drops the keyword, which the Swift 5 language mode accepts with a
        // warning and Swift 6 rejects outright — and a bypass protocol that does not compile is
        // withdrawn along with the accessor group, so the member disappears rather than failing
        // loudly. Only the existential shape is re-rendered; every other return type keeps the
        // spelling this declaration has always used.
        var returnSwiftType = CdeclParamMapper.IsProtocolExistentialType(subscriptDecl.ReturnTypeSpec, typeDatabase)
            ? CdeclParamMapper.RenderModuleQualifiedSwiftTypeWithExistentialAny(subscriptDecl.ReturnTypeSpec, typeDatabase)
            : ExistentialBypassEmitter.RenderSwiftTypeSpec(subscriptDecl.ReturnTypeSpec);

        // Build subscript signature for protocol. See EmitGetterProtocolAndConformance for why
        // the explicit `<external> <internal>:` form is required for subscripts.
        var indexParams = BuildBypassSubscriptIndexParams(subscriptDecl);
        var indexParamString = string.Join(", ", indexParams);

        // Conformance extensions are top-level decls and don't inherit the enclosing
        // type's availability. Merge member + parent-chain annotations so the extension
        // type-checks against the deployment target.
        var extensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            subscriptDecl.AvailabilityAnnotations, parentTypeDecl);
        var extensionAvailPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            extensionAvailability, string.Empty);

        // The dispatch protocol + conformance extension carry no @_cdecl symbol; the anchor pins
        // both symbol-less blocks to the subscript that owns them so a wrapper-compile failure inside
        // either attributes to it rather than the coarse module scope, and the post-processor strips
        // the anchor with the block it names.
        var originAnchor = OriginAnchorEmitter.LineForWrapper(subscriptDecl);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            private protocol {{protocolName}} {
                subscript({{indexParamString}}) -> {{returnSwiftType}} { get set }
            }
            {{originAnchor}}
            {{extensionAvailPrefix}}extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
    }
}
