// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Emits per-property @_cdecl Swift wrappers that route property getter/setter P/Invokes
/// through C calling convention, eliminating CallConvSwift ABI mismatches on NativeAOT/ARM64.
///
/// For each property accessor, generates a @_cdecl free function in the wrapper library that:
/// - Receives C-compatible parameters (self as pointer, newValue for setters)
/// - Calls the actual Swift property getter/setter
/// - Returns the result via C ABI (appropriate type mapping per category)
///
/// String properties use the proven SBW_Utf8Slice pattern (UTF-8 bytes + length).
/// Follows the ConstructorWrapperEmitter pattern. State tracked on <see cref="ModuleEmissionContext"/>.
/// </summary>
public static class PropertyWrapperEmitter
{
    /// <summary>
    /// Pure query: determines whether a property should use @_cdecl wrappers for its accessors.
    /// Guards: xcframework mode, generic parents (allowed for non-final classes with concrete types),
    /// no closures, no async, no non-copyable structs, no nested types,
    /// no unsupported generic containers.
    /// </summary>
    public static bool ShouldEmitWrapper(PropertyDecl propertyDecl, MethodEnvironment accessorEnv)
        => EvaluateWrapperEligibility(propertyDecl, accessorEnv).IsWrappable;

    /// <summary>
    /// Single eligibility traversal for property @_cdecl wrappers: returns whether the property's
    /// accessors will be wrapped and, if not, the first guard that rejected them.
    /// <see cref="ShouldEmitWrapper"/> is its boolean shim, so the predicate and the rejection
    /// diagnostic can never drift (Finding 12).
    /// </summary>
    public static WrapperEligibility EvaluateWrapperEligibility(PropertyDecl propertyDecl, MethodEnvironment accessorEnv)
    {
        // Shared guards: xcframework, internal, SPI, non-copyable, actor, inherited generic context
        var memberReason = WrapperValidation.GetMemberRejectionReason(accessorEnv, MemberKind.Property,
            isModuleInternal: propertyDecl.IsModuleInternal,
            isSpiProtected: propertyDecl.IsSpiProtected,
            isActorIsolated: propertyDecl.IsActorIsolated,
            isMainActorIsolated: propertyDecl.IsMainActorIsolated,
            isNonisolated: propertyDecl.IsNonisolated);
        if (memberReason != null)
            return WrapperEligibility.Reject(memberReason);

        // 2. Generic parent type — allow non-final class instance properties with concrete types
        // (inherited generic context is already checked by CanEmitMember)
        if (accessorEnv.ParentDecl is TypeDecl td && td.IsGeneric)
        {
            if (!CanEmitGenericClassPropertyWrapper(propertyDecl, td))
                return WrapperEligibility.Reject("generic_parent_type");

            // Fail-closed wrapper-helper gates apply ONLY when this property would actually
            // route through EmitGenericStaticGetterWrapper / EmitGenericStaticSetterWrapper —
            // those are the paths that call MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded.
            // Concrete properties on generic class parents use SelfReconstructionEmitter.EmitProtocolCast
            // (PropertyWrapperEmitter.cs:342) and never touch _sbw_meta_*, so the gates must NOT
            // reject them. NeedsStaticDispatchForProperty = true iff the property is on a generic
            // struct OR its type references the parent's generic params.
            // Closed static factories also skip the metadata-helper path entirely (the wrapper
            // takes only resultPtr — no parent metadata or PWT threading), so the gates must
            // not reject them either.
            // Dynamic PWT resolution and buffer-mode ABI are not yet implemented.
            if (GenericDispatchEmitter.NeedsStaticDispatchForProperty(accessorEnv, td, propertyDecl)
                && !ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(propertyDecl))
            {
                if (MetatypeHelperEmitter.HasUnresolvableTypeConformances(td, accessorEnv.TypeDatabase))
                    return WrapperEligibility.Reject("generic_parent_unresolved_pwt_constraint");
                if (MetatypeHelperEmitter.WouldExceedRegisterArgumentThreshold(td, accessorEnv.TypeDatabase))
                    return WrapperEligibility.Reject("generic_parent_metadata_buffer_mode");
            }
        }

        // 2d. Skip metatype properties (Any.Type, T.Type) — not C-representable. Also skips
        //     Optional<Metatype> (e.g. AnyClass.Type?) which would otherwise slip past as a
        //     protocol-existential candidate at gate 9a and emit a Swift wrapper rendering
        //     "(any AnyObject.Type).self" — invalid Swift that fails wrapper compile.
        if (WrapperValidation.IsMetatypeTypeIncludingOptional(propertyDecl.SwiftTypeSpec))
            return WrapperEligibility.Reject("metatype_property");

        // 2e. Skip the Swift built-in `self` property — `obj.self` returns the receiver type,
        //     not the declared return type, so the wrapper would emit an invalid cast (e.g.
        //     a nested struct's `self` getter declared as the outer type cannot be reconstructed
        //     from `obj.self` of the nested type). The C# side already exposes the receiver.
        if (propertyDecl.Name == "self")
            return WrapperEligibility.Reject("self_property");

        // 3. Direct closure properties: getters allowed — routed through IndirectResult (resultPtr buffer)
        // with invoke thunk for closure invocation (same pattern as MethodWrapperEmitter).
        // Optional<closure> getter also allowed — routes through IndirectResult buffer with null check.

        // 3a. Direct closure setter: not supported — CdeclParamMapper has no closure handling,
        //     so the setter would fall through to UnsafeRawPointer reconstruction which is invalid
        //     for closures (they need funcPtr + context marshalling). Read-only closure properties are fine.
        if (propertyDecl.SwiftTypeSpec is ClosureTypeSpec &&
            propertyDecl.Accessors.OfType<SetAccessorDecl>().Any())
            return WrapperEligibility.Reject("direct_closure_setter");

        // 3b. Optional<closure> setter: the closure's params/return must be cdecl-compatible
        //     (Layer 2 gate — same as method closures). Without this, the Swift adapter
        //     passes non-primitive args directly to @convention(c) which expects raw pointers.
        if (propertyDecl.SwiftTypeSpec is NamedTypeSpec optClosure &&
            optClosure.Name == "Swift.Optional" && optClosure.GenericParameters.Count == 1 &&
            optClosure.GenericParameters[0] is ClosureTypeSpec closureInner &&
            propertyDecl.Accessors.OfType<SetAccessorDecl>().Any() &&
            !ClosureEmitter.IsClosureCdeclCompatible(closureInner, accessorEnv.ClosureHandler))
            return WrapperEligibility.Reject("optional_closure_not_cdecl_compatible");

        // 4. Async properties use the async method wrapper path (@_silgen_name), not @_cdecl
        if (propertyDecl.Accessors.Any(a => a.Method.IsAsync))
            return WrapperEligibility.Reject("async_property");

        // 4b. Throwing property getters — the @_cdecl wrapper doesn't emit try/catch for property access,
        // so it declines them until try/catch + error-callback support is added on the wrapper path.
        //
        // Declining is NOT dropping. The accessor falls through to the direct CallConvSwift P/Invoke,
        // and for a SYNCHRONOUS throwing getter that path is ABI-correct on its own terms: swiftcc
        // returns a thrown error in the dedicated error register, so the emitted P/Invoke declares a
        // `ref SwiftError` out-parameter, and the generated property body raises when it comes back
        // non-null. That mechanism is what makes this gate safe to leave as a wrapper-strategy decline
        // rather than a member skip.
        //
        // It is safe ONLY because the getter is synchronous. An ASYNC throwing getter must never reach
        // here — gate 4 above rejects `async_property` first, and PropertyHandler routes async
        // accessors to the async-method path before eligibility is even consulted. If accessor
        // async-ness were mis-detected as false, this gate would fire instead and the fall-through
        // would aim a synchronous `ref SwiftError` P/Invoke at an async entry point: it compiles, it
        // ships, and it mismatches the ABI on the first read. That is why accessor async-ness is
        // decided from two independent oracles (the TBD's `Tu`/`TjTu` sibling symbol and the
        // .swiftinterface's literal `get async`) rather than one.
        //
        // Carry a stable SWIFTBIND diagnostic code so the decline is OBSERVABLE in the emission-report
        // skip-reason histogram, not an anonymous bucket. Reason is diagnostic-only and never reaches
        // generated code.
        if (propertyDecl.Accessors.OfType<GetAccessorDecl>().Any(a => a.Method.Throws))
            return WrapperEligibility.Reject(
                "SWIFTBIND107: throwing property getter takes the direct CallConvSwift path — the @_cdecl property wrapper does not emit try/catch for throwing accessors");

        // 8. Nested types — ALLOWED. @_cdecl wrapper signatures use C-compatible types
        //    (Int32 raw value for simple enums, UnsafeRawPointer for complex types, void+resultPtr
        //    for indirect results). The nested type name only appears in the function BODY
        //    (e.g., initializeMemory(as: Codec.Format.self)), which is valid Swift.

        // 9a. Optional<protocol existential>: needs @_cdecl wrapper because
        //     Optional<ExistentialContainer> is too large (40+ bytes on arm64) for register return
        //     via CallConvSwift. Uses decomposed (resultPtr + hasValuePtr) getter pattern.
        if (CdeclParamMapper.IsProtocolExistentialType(propertyDecl.SwiftTypeSpec, accessorEnv.TypeDatabase) &&
            WrapperValidation.IsOptionalType(propertyDecl.SwiftTypeSpec))
            return WrapperEligibility.Wrappable;

        // 9. Skip unsupported generic container properties (Result<T,E>).
        //    Optional<value-type> allowed (IndirectResult). Array/Dictionary/Set allowed (UnsafeRawPointer transport).
        if (WrapperValidation.IsUnsupportedGenericContainer(propertyDecl.SwiftTypeSpec, accessorEnv.TypeDatabase))
            return WrapperEligibility.Reject("unsupported_generic_container");

        // 9b. ObjC-bridged Optional setter — resolved: IsOptionalWithReferenceInner now returns
        //     true for ObjC-bridged structs (not NSString typedefs), enabling nullable pointer ABI
        //     for their setters via Unmanaged<AnyObject> + cast. No guard needed.

        // 10. Skip properties with raw ABI generic type params (τ_0_0) in the property type,
        // UNLESS the parent is a generic type that supports static dispatch for T-typed properties.
        // Raw generic params from parent type generics are handled by the static dispatch protocol.
        if (propertyDecl.SwiftTypeSpec != null && WrapperValidation.ContainsRawGenericTypeParam(propertyDecl.SwiftTypeSpec))
        {
            // Allow if parent is generic and we passed the CanEmitGenericClassPropertyWrapper check above
            // (which already validated that the T-typed property can use static dispatch)
            if (!(accessorEnv.ParentDecl is TypeDecl genericTd && genericTd.IsGeneric))
                return WrapperEligibility.Reject("raw_generic_type_params");
        }

        return WrapperEligibility.Wrappable;
    }

    /// <summary>
    /// Returns a human-readable skip reason if the property wrapper would be rejected, or null if it
    /// passes all gates. Diagnostic shim over <see cref="EvaluateWrapperEligibility"/> — single source
    /// of truth for the predict/emit decision (Finding 12).
    /// </summary>
    public static string? GetRejectionReason(PropertyDecl propertyDecl, MethodEnvironment accessorEnv)
        => EvaluateWrapperEligibility(propertyDecl, accessorEnv).Reason;

    /// <summary>
    /// Gets the @_cdecl symbol name for a property accessor wrapper.
    /// </summary>
    public static string GetAccessorSymbolName(string moduleName, string typeName, string propertyName, bool isGetter)
    {
        var safeTypeName = typeName.Replace(".", "_");
        var prefix = isGetter ? "Get" : "Set";
        return $"SBW_{prefix}_{moduleName}_{safeTypeName}_{propertyName}";
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper for a property getter.
    /// </summary>
    public static void EmitSwiftGetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();

        // property getters live in the property bucket; no
        // method/constructor/subscript emitter ever registers a getter mangled name.
        // Swift mangles property accessors with a distinct accessor-kind discriminator,
        // so the symbol is unique per property and the per-kind dedup gate is
        // collision-safe without routing through the structural-identity registry.
        // Attribute on the accessor axis: the getter and setter are two separate wrapper
        // artifacts of one property, so an accessor-less id would give both the same
        // ArtifactId and lose which half a symbol-level failure actually belongs to.
        if (!ctx.TryAddPropertyWrapperSymbol(symbolName, DeclIdFactory.ForProperty(propertyDecl, AccessorKind.Getter)))
            return; // Already emitted

        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl?.SwiftTypeName == null) return;
        var moduleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        var propertySwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyDecl.SwiftTypeSpec);
        bool isClass = env.ParentDecl is ClassDecl;
        bool isStatic = propertyDecl.IsStatic;
        // LocalizedStringResource projects to a C# string and rides the SBW_Utf8Slice property
        // path; broaden locally rather than touching WitnessDispatchEmitter.IsStringType so the
        // resilient type stays out of witness/protocol dispatch (where it remains gate-dropped).
        bool isLsr = MarshallingHelpers.IsLocalizedStringResource(propertyDecl.SwiftTypeSpec);
        bool isString = WitnessDispatchEmitter.IsStringType(propertyDecl.SwiftTypeSpec) || isLsr;
        bool isNonCopyableParent = !isClass && !isStatic && WrapperValidation.IsNonCopyableStructParent(env.ParentDecl);

        // Ensure SBW_Utf8Slice infrastructure is emitted for string properties
        if (isString)
        {
            Utf8SliceEmitter.EmitIfNeeded(swiftWriter, ctx);
            Utf8SliceEmitter.EmitFreeIfNeeded(swiftWriter, moduleName, ctx);
        }

        // Check if return needs indirect result (non-frozen struct/complex enum)
        var (returnMapping, needsResultPtr) = GetCdeclReturnMapping(propertyDecl.SwiftTypeSpec, env.TypeDatabase);

        // Build parameter list — phase ordering from CdeclSignatureContract.
        // ResultPtr is handled outside the loop using the emitter's own needsResultPtr logic.
        var swiftParams = new List<string>();
        bool isGenericParent = WrapperValidation.IsGenericParent(env.ParentDecl);
        bool isGenericClassParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        // Check if property type references a generic type parameter
        bool propertyReferencesT = false;
        if (isGenericParent && parentTypeDecl != null)
        {
            var genericParamNames = parentTypeDecl.GenericParameters
                .Select(p => p.TypeName)
                .ToHashSet();
            propertyReferencesT = WrapperValidation.TypeSpecReferencesGenericParam(propertyDecl.SwiftTypeSpec, genericParamNames);
        }

        // When property type is T, the return needs a resultPtr (to write the T value)
        if (propertyReferencesT && !needsResultPtr)
        {
            needsResultPtr = true;
        }

        // Track whether this is a decomposed Optional getter (separate resultPtr + hasValuePtr)
        bool isDecomposedOptionalGetter = OptionalMarshalClassifier.IsDecomposed(propertyDecl.SwiftTypeSpec, env.TypeDatabase);

        if (needsResultPtr)
        {
            swiftParams.Add("_ resultPtr: UnsafeMutableRawPointer");
        }

        // Decomposed Optional getter: add hasValuePtr after resultPtr
        if (isDecomposedOptionalGetter)
        {
            swiftParams.Add("_ hasValuePtr: UnsafeMutableRawPointer");
        }

        var order = CdeclSignatureContract.DetermineParameterOrder(env,
            overrideNeedsResultPtr: needsResultPtr, overrideNeedsSelf: !isStatic);
        foreach (var phase in order.Phases)
        {
            switch (phase)
            {
                case CdeclPhase.ResultPtr:
                    break; // Already handled above
                case CdeclPhase.Self:
                    if (isClass)
                        swiftParams.Add($"_ self_: UnsafeMutableRawPointer");
                    else
                        swiftParams.Add($"_ self_: UnsafeRawPointer");
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
                        // PWT pointer slides into the self_ slot and the as!-cast crashes
                        // walking garbage in the Swift runtime's conformance hash table.
                        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, env.TypeDatabase);
                        for (int i = 0; i < pwtCount; i++)
                        {
                            swiftParams.Add($"_ _pwt{i}: UnsafeRawPointer");
                        }
                    }
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);

        // Return clause
        string returnClause = needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";

        var swiftFuncName = $"_sbw_get_{propertyDecl.Name}_{EmitterUtility.DeterministicHash8(symbolName)}";

        // Closed static factory shape — a static property on a generic type whose return
        // type is a fully closed bound generic of the parent (e.g.
        // `PatBoundedStatsQuery<T>.presetA : PatBoundedStatsQuery<StatPayloadA>`). The
        // accessor doesn't depend on the receiver's open T, so we emit a parameter-free
        // @_cdecl wrapper that source-calls the closed instantiation directly. The Swift
        // compiler resolves all metadata + PWTs at the hard-coded T'.
        if (isStatic && parentTypeDecl != null &&
            ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(propertyDecl))
        {
            EmitClosedStaticFactoryGetterWrapper(swiftWriter, propertyDecl, symbolName, env, moduleQualifiedName);
            return;
        }

        // Determine which generic dispatch pattern to use for the getter.
        // needsStaticGetterDispatch: generic struct OR generic class with T-typed property
        // (T-typed properties can't use the instance protocol pattern because the property type
        // is a generic param that's only available inside the conforming extension body)
        bool needsStaticGetterDispatch = WrapperValidation.NeedsGenericDispatch(
            env, MemberKind.Property, propertyDecl);

        // For generic types needing static dispatch, delegate to specialized emitter
        if (needsStaticGetterDispatch)
        {
            EmitGenericStaticGetterWrapper(swiftWriter, propertyDecl, symbolName, env, ctx,
                parentTypeDecl!, moduleQualifiedName, isClass, isStatic, isString, needsResultPtr,
                propertyReferencesT, returnMapping);
            return;
        }

        // For generic parent class types with concrete property, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericClassParent)
        {
            protocolName = EmitGetterProtocolAndConformance(
                swiftWriter, propertyDecl, symbolName, moduleQualifiedName, parentTypeDecl!);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Property getter @_cdecl wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}}.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // Add @MainActor when wrapping @MainActor-isolated properties.
        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            env.ParentDecl, propertyDecl.IsMainActorIsolated, propertyDecl.IsNonisolated);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, env.ParentDecl));
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}){returnClause} {{");
        swiftWriter.Indent++;

        // Reconstruct self
        // A struct getter declared as `mutating get { ... }` cannot be invoked on a `let`-bound
        // copy. Bind `obj` as `var` for that case so the call site `obj.{property}` compiles.
        // Class getters and noncopyable structs handle reconstruction differently and are unaffected.
        // The ABI digester drops the `mutating` attribute on accessors, so for struct properties
        // with both get and set we conservatively bind as `var` — non-frozen Swift structs use
        // `_modify`-flavored accessors that require mutable self even on the read path
        // (e.g. RealityKit ARView.Environment.sceneUnderstanding declares `mutating get`).
        // The generator's IsMutating signal isn't always populated, so we widen the trigger
        // to "settable struct property" — false positives become harmless `var was never
        // mutated` warnings, never compile errors.
        bool hasGetMutating = !isClass
            && propertyDecl.Accessors
                .OfType<GetAccessorDecl>()
                .FirstOrDefault()?.Method.IsMutating == true;
        bool hasSetterOnStruct = !isClass
            && propertyDecl.Accessors.OfType<SetAccessorDecl>().Any();
        bool isMutatingGetter = hasGetMutating || hasSetterOnStruct;
        if (!isStatic)
        {
            if (isGenericClassParent && protocolName != null)
            {
                SelfReconstructionEmitter.EmitProtocolCast(swiftWriter, protocolName, isMutable: isMutatingGetter);
            }
            else
            {
                EmitSelfReconstruction(swiftWriter, isClass, moduleQualifiedName, isMutable: isMutatingGetter, isNonCopyableParent);
            }
        }

        // Get property value
        // For noncopyable types, use inline pointer borrow instead of obj (no let binding = no copy)
        string propAccess;
        if (isStatic)
            propAccess = $"{moduleQualifiedName}.{propertyDecl.Name}";
        else if (isNonCopyableParent)
            propAccess = $"self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee.{propertyDecl.Name}";
        else
            propAccess = $"obj.{propertyDecl.Name}";

        // Emit return based on type category
        if (isString)
        {
            // LocalizedStringResource getter: resolve the resource to a String (iOS 16+) before
            // it rides the SBW_Utf8Slice path.
            EmitStringGetterBody(swiftWriter, isLsr ? $"String(localized: {propAccess})" : propAccess);
        }
        else if (isDecomposedOptionalGetter)
        {
            // Decomposed Optional getter: write inner payload to resultPtr, hasValue flag to hasValuePtr.
            // Avoids initializeMemory(as: Optional<T>.self) which uses VWT InitializeWithCopy — crashes Mono
            // for complex enum / non-frozen struct payloads.
            var innerSpec = ((NamedTypeSpec)propertyDecl.SwiftTypeSpec).GenericParameters[0];
            var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
            // Protocol existential metatypes need "any" prefix and parentheses: (any Protocol).self.
            // Inner may be ProtocolListTypeSpec (rendered with "any" by bypass emitter) or
            // NamedTypeSpec for a protocol (needs "any" added manually).
            bool isExistentialInner = innerSwiftType.StartsWith("any ") ||
                innerSpec is ProtocolListTypeSpec ||
                (env.TypeDatabase.TryGetTypeRecord(innerSpec, out var innerTR) &&
                 (innerTR.Kind == TypeRecordKind.Protocol || innerTR.Kind == TypeRecordKind.Existential));
            var innerMetatype = isExistentialInner
                ? (innerSwiftType.StartsWith("any ") ? $"({innerSwiftType}).self" : $"(any {innerSwiftType}).self")
                : $"{innerSwiftType}.self";
            swiftWriter.WriteLine($"let result = {propAccess}");
            swiftWriter.WriteLines($$"""
                if let value = result {
                    resultPtr.initializeMemory(as: {{innerMetatype}}, repeating: value, count: 1)
                    {{OptionalMarshalClassifier.SwiftWriteHasValue("hasValuePtr", true)}}
                } else {
                    {{OptionalMarshalClassifier.SwiftWriteHasValue("hasValuePtr", false)}}
                }
                """);
        }
        else if (needsResultPtr)
        {
            // Optional<BlittablePrimitive>: write value and tag byte separately to avoid
            // initializeMemory(as: Optional<Int32>.self) which produces incorrect tag bytes
            // on some runtimes (the tag byte for None reads as 0 instead of 1).
            if (WrapperValidation.IsOptionalType(propertyDecl.SwiftTypeSpec) &&
                propertyDecl.SwiftTypeSpec is NamedTypeSpec optNts && optNts.GenericParameters.Count == 1 &&
                optNts.GenericParameters[0] is NamedTypeSpec innerNts &&
                CdeclParamMapper.IsBlittablePrimitiveSwiftType(innerNts.Name))
            {
                var rawType = CdeclParamMapper.GetSwiftRawValueType(innerNts.Name);
                var tagOffset = OptionalMarshalClassifier.GetSwiftTagByteOffsetString(innerNts.Name) ?? "8";
                swiftWriter.WriteLine($"let result = {propAccess}");
                swiftWriter.WriteLines($$"""
                    let tagPtr = resultPtr.assumingMemoryBound(to: UInt8.self).advanced(by: {{tagOffset}})
                    if let val = result {
                        resultPtr.assumingMemoryBound(to: {{rawType}}.self).pointee = val
                        tagPtr.pointee = 0
                    } else {
                        tagPtr.pointee = 1
                    }
                    """);
            }
            else
            {
                // Non-frozen struct, complex enum, or Optional<closure>: write to result buffer
                var qualifiedPropertyType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(propertyDecl.SwiftTypeSpec);
                // Strip @escaping/@Sendable — parameter attributes, not valid in metatype position.
                // No-op for non-closure types.
                qualifiedPropertyType = qualifiedPropertyType.Replace("@escaping ", "").Replace("@Sendable ", "");
                // Closure types and existential types need parentheses before .self:
                // () -> FinalCounter.self is parsed as () -> (FinalCounter.self) — invalid.
                // (any Protocol).self prevents .self from binding to only the last protocol.
                bool needsParens = qualifiedPropertyType.StartsWith("any ") ||
                                   propertyDecl.SwiftTypeSpec is ClosureTypeSpec;
                var metatype = needsParens ? $"({qualifiedPropertyType}).self" : $"{qualifiedPropertyType}.self";
                swiftWriter.WriteLine($"let result = {propAccess}");
                swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
            }
        }
        else
        {
            EmitDirectGetterReturn(swiftWriter, propAccess, propertyDecl.SwiftTypeSpec, env.TypeDatabase, returnMapping);
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");

        // NOTE: Invoke thunk for closure property returns is emitted by PropertyHandler.EmitPropertyClosureInvokeThunkIfNeeded()
        // after calling this method. Do NOT emit it here to avoid duplicate @_cdecl symbols.
    }

    /// <summary>
    /// Emits a @_cdecl Swift wrapper for a property setter.
    /// Parameter order: [resultPtr if needed], newValue params, self (matches C# P/Invoke layout).
    /// </summary>
    public static void EmitSwiftSetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext? ctx = null)
    {
        ctx ??= ModuleEmissionContext.CreateImplicitFallback();

        // property setters share the property bucket with getters but
        // Swift's accessor mangling distinguishes setter from getter at the symbol level, so
        // the symbol is unique per (property, accessor-kind). No method/constructor/subscript
        // emitter ever registers symbols here; the per-kind dedup gate is collision-safe.
        // Attributed on the accessor axis for the same reason the getter is — the two
        // accessors are distinct wrapper artifacts of one property.
        if (!ctx.TryAddPropertyWrapperSymbol(symbolName, DeclIdFactory.ForProperty(propertyDecl, AccessorKind.Setter)))
            return; // Already emitted

        var parentTypeDecl = env.ParentDecl as TypeDecl;
        if (parentTypeDecl?.SwiftTypeName == null) return;
        var moduleQualifiedName = parentTypeDecl.SwiftTypeName.ModuleQualifiedName;
        var moduleName = parentTypeDecl.SwiftTypeName.Module;
        bool isClass = env.ParentDecl is ClassDecl;
        bool isStatic = propertyDecl.IsStatic;
        // LocalizedStringResource projects to a C# string and rides the SBW_Utf8Slice setter path;
        // broaden locally (see getter) so the resilient type stays out of witness/protocol dispatch.
        bool isLsr = MarshallingHelpers.IsLocalizedStringResource(propertyDecl.SwiftTypeSpec);
        bool isString = WitnessDispatchEmitter.IsStringType(propertyDecl.SwiftTypeSpec) || isLsr;

        // Build parameter list — phase ordering from CdeclSignatureContract.
        var swiftParams = new List<string>();
        var reconstructionLines = new List<string>();
        bool isGenericParent = WrapperValidation.IsGenericParent(env.ParentDecl);
        bool isGenericClassParent = MethodWrapperEmitter.IsGenericClassParent(env.ParentDecl);

        // Check if property type references T
        bool propertyReferencesT = false;
        if (isGenericParent && parentTypeDecl != null)
        {
            var genericParamNames = parentTypeDecl.GenericParameters
                .Select(p => p.TypeName)
                .ToHashSet();
            propertyReferencesT = WrapperValidation.TypeSpecReferencesGenericParam(propertyDecl.SwiftTypeSpec, genericParamNames);
        }

        bool needsStaticSetterDispatch = WrapperValidation.NeedsGenericDispatch(
            env, MemberKind.Property, propertyDecl);

        // For generic static dispatch setters, delegate to specialized emitter
        if (needsStaticSetterDispatch)
        {
            EmitGenericStaticSetterWrapper(swiftWriter, propertyDecl, symbolName, env, ctx,
                parentTypeDecl!, moduleQualifiedName, isClass, isStatic, isString, propertyReferencesT);
            return;
        }

        string? cdeclCallArgValueExpr = null;
        var order = CdeclSignatureContract.DetermineParameterOrder(env,
            overrideNeedsResultPtr: false, overrideHasArguments: true, overrideNeedsSelf: !isStatic);
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
                    else if (OptionalMarshalClassifier.IsDecomposed(propertyDecl.SwiftTypeSpec, env.TypeDatabase))
                    {
                        // Decomposed Optional setter: pass raw inner payload + hasValue flag separately.
                        // Swift reconstructs Optional<T> from these, avoiding C#-side VWT operations.
                        var innerSpec = ((NamedTypeSpec)propertyDecl.SwiftTypeSpec).GenericParameters[0];
                        var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
                        swiftParams.Add("_ newValue: UnsafeRawPointer");
                        swiftParams.Add($"_ {OptionalMarshalClassifier.SwiftHasValueParam}: {OptionalMarshalClassifier.SwiftHasValueType}");
                        reconstructionLines.Add(OptionalMarshalClassifier.SwiftReconstructOptional(
                            OptionalMarshalClassifier.SwiftHasValueParam, "newValue", innerSwiftType, "newValueVal"));
                    }
                    else if (propertyDecl.SwiftTypeSpec is NamedTypeSpec optClosureNts &&
                             optClosureNts.Name == "Swift.Optional" && optClosureNts.GenericParameters.Count == 1 &&
                             optClosureNts.GenericParameters[0] is ClosureTypeSpec closureSpec)
                    {
                        // Optional<closure> setter: accept funcPtr + context, adapt to Swift closure.
                        // Same pattern as method closure parameters in MethodWrapperEmitter.
                        // Property setters always store the closure beyond the call, so the
                        // adapter must wrap the GCHandle context in an _SBClosureCtx box —
                        // closes the property-setter handler subscription leak.
                        swiftParams.Add("_ newValueFuncPtr: UnsafeMutableRawPointer?");
                        swiftParams.Add("_ newValueContext: UnsafeMutableRawPointer?");

                        ClosureContextHelperEmitter.EmitIfNeeded(swiftWriter, ctx);
                        var adapterLines = ClosureEmitter.GetSwiftClosureAdapterCode(
                            "newValue", closureSpec, env.ClosureHandler, isOptional: true, isEscaping: true,
                            swiftWriter: swiftWriter, ctx: ctx,
                            moduleName: propertyDecl.ModuleDecl?.Name ?? "SwiftBindings");
                        reconstructionLines.AddRange(adapterLines);
                        cdeclCallArgValueExpr = "_adapted_newValue";
                    }
                    else
                    {
                        var newValueArg = new ArgumentDecl
                        {
                            SwiftTypeSpec = propertyDecl.SwiftTypeSpec,
                            Name = "newValue",
                            PrivateName = "newValue",
                            IsInOut = false,
                            IsGeneric = false,
                            ParentDecl = null,
                            ModuleDecl = null
                        };
                        // omitLabels: false — property setters always need .load(as:) reconstruction
                        // for large Optionals. omitLabels: true would trigger ShouldWidenParam bypass.
                        // escapeReservedCollision: false — `newValue` IS the injected setter synthetic
                        // (referenced by bare name in the body below), not a colliding user binding.
                        var (cdeclParam, reconstruction, callArgExpr) = CdeclParamMapper.Map(
                            newValueArg, "newValue", env, omitLabels: false, escapeReservedCollision: false);
                        swiftParams.Add(cdeclParam);
                        if (reconstruction != null)
                        {
                            reconstructionLines.Add(reconstruction);
                            // Strip any leading "argLabel: " from callArgExpr — property setters
                            // just need the value expression (e.g., "newValueOpt"), not "label: newValueOpt".
                            var colonIdx = callArgExpr.IndexOf(':');
                            cdeclCallArgValueExpr = colonIdx >= 0 ? callArgExpr[(colonIdx + 2)..] : callArgExpr;
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
                        // C# side (HandleProtocolConformance) emits PWT pointers for resolvable
                        // protocol constraints on the parent's generic parameters. The wrapper
                        // must absorb them here even when the body doesn't use them, otherwise the
                        // PWT pointer slides into the self_ slot and the wrapper writes through
                        // a garbage pointer.
                        int pwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, env.TypeDatabase);
                        for (int i = 0; i < pwtCount; i++)
                        {
                            swiftParams.Add($"_ _pwt{i}: UnsafeRawPointer");
                        }
                    }
                    break;

                case CdeclPhase.Self:
                    // Both class and struct setters use mutable self
                    swiftParams.Add($"_ self_: UnsafeMutableRawPointer");
                    break;
            }
        }

        var swiftParamString = string.Join(", ", swiftParams);
        var swiftFuncName = $"_sbw_set_{propertyDecl.Name}_{EmitterUtility.DeterministicHash8(symbolName)}";

        // For generic parent class types, emit protocol + conformance for type erasure
        string? protocolName = null;
        if (isGenericClassParent)
        {
            protocolName = EmitSetterProtocolAndConformance(
                swiftWriter, propertyDecl, symbolName, moduleQualifiedName, parentTypeDecl!);
        }

        // Emit the @_cdecl function
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Property setter @_cdecl wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}}.
            // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
            """);

        // Add @MainActor when wrapping @MainActor-isolated properties.
        bool needsMainActorSetter = WrapperValidation.NeedsMainActorAnnotation(
            env.ParentDecl, propertyDecl.IsMainActorIsolated, propertyDecl.IsNonisolated);
        // Prefer the setter-specific availability list when the ABI JSON tightens the
        // setter above the property level (e.g. WorkoutKit.PowerThresholdAlert.metric
        // setter is iOS 17.4 while the getter is iOS 17.0). Falls back to the property
        // availability when the setter has no extra restrictions.
        var setterAvailForWrapper = propertyDecl.SetterAvailabilityAnnotations is { Count: > 0 }
            ? propertyDecl.SetterAvailabilityAnnotations
            : propertyDecl.AvailabilityAnnotations;
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActorSetter,
            WrapperEmitterHelpers.MergeAvailability(setterAvailForWrapper, env.ParentDecl));
        swiftWriter.WriteLine($"public func {swiftFuncName}({swiftParamString}) {{");
        swiftWriter.Indent++;

        // Emit reconstruction lines
        foreach (var line in reconstructionLines)
        {
            swiftWriter.WriteLine(line);
        }

        // Get the value expression (may be reconstructed with suffix "Val" or "Opt" depending on path)
        // LocalizedStringResource setter: wrap the reconstructed String back into a resource (iOS 16+).
        string valueExpr = isLsr ? "Foundation.LocalizedStringResource(stringLiteral: newValue)" :
            isString ? "newValue" :
            (cdeclCallArgValueExpr ?? (reconstructionLines.Count > 0 ? "newValueVal" : "newValue"));

        // Emit assignment
        if (isStatic)
        {
            swiftWriter.WriteLine($"{moduleQualifiedName}.{propertyDecl.Name} = {valueExpr}");
        }
        else if (isGenericClassParent && protocolName != null)
        {
            // Generic class: use protocol-based type erasure
            SelfReconstructionEmitter.EmitProtocolCast(swiftWriter, protocolName, isMutable: true);
            swiftWriter.WriteLine($"obj.{propertyDecl.Name} = {valueExpr}");
        }
        else if (isClass)
        {
            // Class: reconstruct from Unmanaged, assign property
            swiftWriter.WriteLine($"let obj = Unmanaged<{moduleQualifiedName}>.fromOpaque(self_).takeUnretainedValue()");
            swiftWriter.WriteLine($"obj.{propertyDecl.Name} = {valueExpr}");
        }
        else
        {
            // Struct: mutate through pointer
            swiftWriter.WriteLine($"self_.assumingMemoryBound(to: {moduleQualifiedName}.self).pointee.{propertyDecl.Name} = {valueExpr}");
        }

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Maps a property type to its @_cdecl-compatible return type and whether it needs a result pointer.
    /// Delegates to <see cref="CdeclReturnMapping.Classify"/>.
    /// </summary>
    internal static (CdeclReturnMapping mapping, bool needsResultPtr) GetCdeclReturnMapping(
        TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => CdeclReturnMapping.Classify(typeSpec, typeDatabase);

    /// <summary>
    /// Emits the self reconstruction line for the getter/setter body.
    /// Delegates to <see cref="SelfReconstructionEmitter.Emit"/>.
    /// </summary>
    private static void EmitSelfReconstruction(SwiftWriter swiftWriter, bool isClass, string moduleQualifiedName, bool isMutable, bool isNonCopyable = false)
    {
        SelfReconstructionEmitter.Emit(swiftWriter, isClass, isMutating: false, moduleQualifiedName, isNonCopyable, bindAsVar: isMutable);
    }

    /// <summary>
    /// Emits the string getter body using SBW_Utf8Slice pattern.
    /// Delegates to <see cref="StringReturnEmitter.EmitGetterBody"/>.
    /// </summary>
    private static void EmitStringGetterBody(SwiftWriter swiftWriter, string propAccess)
    {
        StringReturnEmitter.EmitGetterBody(swiftWriter, propAccess);
    }

    /// <summary>
    /// Emits the direct return for non-string, non-indirect-result getter returns.
    /// </summary>
    private static void EmitDirectGetterReturn(SwiftWriter swiftWriter, string propAccess,
        TypeSpec typeSpec, ITypeDatabase typeDatabase, CdeclReturnMapping mapping)
        => CdeclReturnRenderer.Write(swiftWriter, propAccess, typeSpec, typeDatabase, mapping, scalarParens: false);

    /// <summary>
    /// Checks if a parent decl is a non-copyable struct.
    /// </summary>
    private static bool IsNonCopyableStruct(BaseDecl? parentDecl)
        => WrapperValidation.IsNonCopyableStructParent(parentDecl);

    // ═══════════════════════════════════════════════════════════════════════
    // Generic parent class support — protocol-based type erasure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true when a property on a generic parent type can be wrapped via @_cdecl
    /// using protocol-based type erasure.
    /// </summary>
    private static bool CanEmitGenericClassPropertyWrapper(
        PropertyDecl propertyDecl, TypeDecl parentTypeDecl)
    {
        // Static properties don't need self-based erasure, but static dispatch
        // uses wrong metadata for generic types — skip for now.
        // EXCEPTION — closed static factory: a static property whose return type is a
        // fully closed bound generic of the parent (e.g.
        // `PatBoundedStatsQuery<T>.presetA : PatBoundedStatsQuery<StatPayloadA>`) emits
        // a parameter-free @_cdecl wrapper that source-calls the closed instantiation.
        // No parent metadata or PWT threading required — the Swift compiler resolves
        // both at the hard-coded `T'`. See ClosedStaticFactoryGate.
        if (propertyDecl.IsStatic)
            return ClosedStaticFactoryGate.IsClosedStaticFactoryAccessor(propertyDecl);

        // Check if property type references the parent's generic type parameters
        var genericParamNames = parentTypeDecl.GenericParameters
            .Select(p => p.TypeName)
            .ToHashSet();
        bool referencesT = MethodWrapperEmitter.TypeSpecReferencesGenericParam(propertyDecl.SwiftTypeSpec, genericParamNames);

        // Path 1: Generic class with concrete property type — existing protocol dispatch
        if (parentTypeDecl is ClassDecl && !referencesT)
            return true;

        // Path 2: Generic struct/class with T-typed property — static protocol dispatch
        // Only allow if the property type is a direct generic param (not complex composition).
        // Concrete properties on generic structs are deferred — they may come from constrained
        // extensions (e.g., `extension Wrapper where T: UIImage`) and unconditional protocol
        // conformances can't access conditionally-available members.
        if (referencesT)
        {
            if (propertyDecl.SwiftTypeSpec is NamedTypeSpec named && genericParamNames.Contains(named.Name))
                return true; // Direct T property, can use static dispatch

            // Simply-parameterized shapes route through the @_cdecl static-dispatch wrapper.
            // Same shape the method-side gate accepts — both render the result via
            // initializeMemory(as: Type<T>.self) and avoid Mono Issue 1 (`!ji->async` with
            // 2+ type-metadata args) plus NativeAOT's multi-type-parameter generic SIGSEGV.
            // Narrowed to the two shapes with end-to-end runtime evidence:
            //   * Optional<T> — OptionalGenericHolder<Value>.stored (sim + device)
            //   * Array<T>    — MusicItemBag<Item>.items / IndexedSeries<Element>.items
            // Other shapes (Dictionary<K,T>, Pair<T,T>, custom generic structs) render
            // identically but lack end-to-end coverage, so they stay behind the gate until
            // a BindingTest proves the static-dispatch round-trip.
            if (parentTypeDecl is StructDecl
                && (GenericDispatchEmitter.IsOptionalOfParentGeneric(propertyDecl.SwiftTypeSpec, genericParamNames)
                    || GenericDispatchEmitter.IsArrayOfParentGeneric(propertyDecl.SwiftTypeSpec, genericParamNames)))
                return true;

            return false; // Complex generic composition, deferred
        }

        // Generic struct with concrete property type — normally deferred because the property
        // may come from a constrained extension (unconditional protocol conformance can't
        // access conditionally-available members). Fall back to CallConvSwift.
        //
        // EXCEPTION — Collection-family conformers. When the generic struct conforms to
        // Swift.Collection / Sequence / BidirectionalCollection / RandomAccessCollection,
        // the stored/computed properties of the Collection protocol witnesses (startIndex,
        // endIndex, items, etc.) are declared directly on the type — not inside a
        // constrained extension. Falling through to direct CallConvSwift leaves these
        // getters unreachable on Mono JIT (Issue 1 — jit-info.c:918 `!ji->async` assertion
        // trips when the Swift runtime's metadata / value-witness calls flow through a
        // direct CallConvSwift P/Invoke with 2+ type-metadata args). Routing them through
        // the @_cdecl static-dispatch wrapper avoids the Mono pathology and mirrors the
        // relaxation applied to Collection-family methods in
        // GenericDispatchEmitter.CanEmitStaticDispatch. Matches the MusicKit
        // MusicItemCollection<TMusicItemType> shape.
        if (parentTypeDecl is not ClassDecl)
        {
            if (parentTypeDecl is StructDecl structDecl
                && CollectionProjectionEmitter.HasCollectionConformance(structDecl))
                return true;
            return false;
        }

        // Generic class with concrete property type — use existing instance dispatch
        return true;
    }

    /// <summary>
    /// Emits a parameter-free @_cdecl getter wrapper for the closed-static-factory shape
    /// (e.g. <c>PatBoundedStatsQuery&lt;T&gt;.presetA : PatBoundedStatsQuery&lt;StatPayloadA&gt;</c>).
    /// The Swift body source-calls the closed instantiation directly so the Swift compiler
    /// resolves all parent metadata + PWTs at compile time. Wrapper signature is just
    /// <c>resultPtr: UnsafeMutableRawPointer</c>; the C# P/Invoke side passes only that.
    /// </summary>
    private static void EmitClosedStaticFactoryGetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        MethodEnvironment env,
        string moduleQualifiedName)
    {
        var hash = EmitterUtility.DeterministicHash8(symbolName);
        var swiftFuncName = $"_sbw_get_{propertyDecl.Name}_{hash}";

        // Closed return type rendered with module-qualified names so the generated Swift
        // disambiguates collisions with other imported modules (e.g.
        // "SwiftBindingsTestLib.PatBoundedStatsQuery<SwiftBindingsTestLib.StatPayloadA>").
        var closedReturnType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(propertyDecl.SwiftTypeSpec);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Property getter @_cdecl wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}}
            // (closed static factory — return type is fully closed; no metadata/PWT threading).
            """);

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            env.ParentDecl, propertyDecl.IsMainActorIsolated, propertyDecl.IsNonisolated);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, env.ParentDecl));
        swiftWriter.WriteLine($"public func {swiftFuncName}(_ resultPtr: UnsafeMutableRawPointer) {{");
        swiftWriter.Indent++;
        // Source-level call to the closed instantiation. Swift compiler resolves the open T
        // metadata and any PWTs at compile time, so the wrapper takes no extra parameters.
        swiftWriter.WriteLine($"let result = {closedReturnType}.{propertyDecl.Name}");
        swiftWriter.WriteLine($"resultPtr.initializeMemory(as: {closedReturnType}.self, repeating: result, count: 1)");
        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits a @_cdecl property getter wrapper using generic static dispatch.
    /// Used for generic struct parents and T-typed properties on generic class parents.
    /// </summary>
    private static void EmitGenericStaticGetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext ctx,
        TypeDecl parentTypeDecl,
        string moduleQualifiedName,
        bool isClass,
        bool isStatic,
        bool isString,
        bool needsResultPtr,
        bool propertyReferencesT,
        CdeclReturnMapping returnMapping)
    {
        var hash = EmitterUtility.DeterministicHash8(symbolName);
        var protocolName = $"_SBW_GSPG_{hash}";
        var getMethodName = $"_sbw_get_{hash}";
        var abiToSugaredName = WrapperValidation.GetAbiToSugaredNameMap(parentTypeDecl);
        var propertySwiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(propertyDecl.SwiftTypeSpec, abiToSugaredName);

        // Build protocol static method params
        var protocolParams = new List<string>();
        var cdeclParams = new List<string>();
        var cdeclCallArgs = new List<string>();

        bool isDecomposedOptionalGetter = OptionalMarshalClassifier.IsDecomposed(propertyDecl.SwiftTypeSpec, env.TypeDatabase);

        if (needsResultPtr)
        {
            cdeclParams.Add("_ resultPtr: UnsafeMutableRawPointer");
            protocolParams.Add("resultPtr: UnsafeMutableRawPointer");
            cdeclCallArgs.Add("resultPtr: resultPtr");

            // Decomposed Optional getter: add hasValuePtr after resultPtr
            if (isDecomposedOptionalGetter)
            {
                cdeclParams.Add("_ hasValuePtr: UnsafeMutableRawPointer");
                protocolParams.Add("hasValuePtr: UnsafeMutableRawPointer");
                cdeclCallArgs.Add("hasValuePtr: hasValuePtr");
            }
        }

        // Metadata params come BEFORE self to match C# PInvokeSignatureBuilder ordering for @_cdecl property accessors
        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
        {
            cdeclParams.Add($"_ _metadata{i}: UnsafeRawPointer");
        }

        // PWT params: one per resolvable protocol conformance per generic parameter.
        // Only include PWT for protocols that the C# side can resolve (no associated types
        // or Self requirements). This matches PInvokeEmitter.HandleProtocolConformance.
        int getterPwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, env.TypeDatabase);
        for (int i = 0; i < getterPwtCount; i++)
        {
            cdeclParams.Add($"_ _pwt{i}: UnsafeRawPointer");
        }

        if (isClass)
            cdeclParams.Add("_ self_: UnsafeMutableRawPointer");
        else
            cdeclParams.Add("_ self_: UnsafeRawPointer");
        protocolParams.Add(isClass ? "selfPtr: UnsafeMutableRawPointer" : "selfPtr: UnsafeRawPointer");
        cdeclCallArgs.Add("selfPtr: self_");

        string protocolReturnType = needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";

        // A struct getter declared as `mutating get { ... }` cannot be invoked on a `let`-bound
        // copy — same accommodation as the non-generic getter path (EmitSelfReconstruction above).
        // Binding `var` here is a local copy, not a through-pointer write-back: the wrapper's
        // C#-visible surface is read-only, so any mutation the getter performs is deliberately
        // discarded once the call returns.
        bool isMutatingGetter = !isClass
            && (propertyDecl.Accessors.OfType<GetAccessorDecl>().FirstOrDefault()?.Method.IsMutating == true
                || propertyDecl.Accessors.OfType<SetAccessorDecl>().Any());

        // Build extension body lines
        var bodyLines = new List<string>();
        if (isClass)
            bodyLines.Add("let obj = Unmanaged<AnyObject>.fromOpaque(selfPtr).takeUnretainedValue() as! Self");
        else
            bodyLines.Add($"{(isMutatingGetter ? "var" : "let")} obj = selfPtr.assumingMemoryBound(to: Self.self).pointee");

        var propAccess = $"obj.{propertyDecl.Name}";

        if (isString)
        {
            bodyLines.Add($"let result: String = {propAccess}");
            bodyLines.Add("let utf8 = Array(result.utf8)");
            bodyLines.Add("if utf8.isEmpty {");
            bodyLines.Add("    resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: &_sbw_emptyBuffer, len: 0), as: SBW_Utf8Slice.self)");
            bodyLines.Add("    return");
            bodyLines.Add("}");
            bodyLines.Add("let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: utf8.count)");
            bodyLines.Add("ptr.initialize(from: utf8, count: utf8.count)");
            bodyLines.Add("resultPtr.storeBytes(of: SBW_Utf8Slice(ptr: ptr, len: utf8.count), as: SBW_Utf8Slice.self)");
        }
        else if (needsResultPtr && isDecomposedOptionalGetter)
        {
            // Decomposed Optional: write inner payload and hasValue flag separately
            var innerSpec = ((NamedTypeSpec)propertyDecl.SwiftTypeSpec).GenericParameters[0];
            var innerSwiftType = propertyReferencesT
                ? WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(innerSpec, abiToSugaredName)
                : ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
            // Optional<any P> inner payload needs `(any P).self`, not `any P.self`.
            var innerMetatype = innerSwiftType.StartsWith("any ") ? $"({innerSwiftType}).self" : $"{innerSwiftType}.self";
            bodyLines.Add($"let result = {propAccess}");
            bodyLines.Add("if let value = result {");
            bodyLines.Add($"    resultPtr.initializeMemory(as: {innerMetatype}, repeating: value, count: 1)");
            bodyLines.Add($"    {OptionalMarshalClassifier.SwiftWriteHasValue("hasValuePtr", true)}");
            bodyLines.Add("} else {");
            bodyLines.Add($"    {OptionalMarshalClassifier.SwiftWriteHasValue("hasValuePtr", false)}");
            bodyLines.Add("}");
        }
        else if (needsResultPtr && propertyReferencesT)
        {
            bodyLines.Add($"let result = {propAccess}");
            bodyLines.Add($"resultPtr.initializeMemory(as: {propertySwiftType}.self, repeating: result, count: 1)");
        }
        else if (needsResultPtr)
        {
            var qualifiedType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(propertyDecl.SwiftTypeSpec)
                .Replace("@escaping ", "").Replace("@Sendable ", "");
            var metatype = qualifiedType.StartsWith("any ") ? $"({qualifiedType}).self" : $"{qualifiedType}.self";
            bodyLines.Add($"let result = {propAccess}");
            bodyLines.Add($"resultPtr.initializeMemory(as: {metatype}, repeating: result, count: 1)");
        }
        else
        {
            // Direct return — mirror EmitDirectGetterReturn's per-kind conversions so the
            // protocol-method body returns a value compatible with the declared
            // CdeclReturnType. Bool is the critical case: Swift's `Bool` is not
            // interchangeable with `Int8`, so a naked `return obj.hasNextBatch` fails
            // `swiftc` as "cannot convert Bool to Int8". SimpleEnum / ClassPointer
            // mirror the non-generic path for completeness so any Collection-family
            // conformer's enum- or class-returning nint-only property compiles cleanly.
            // Tag-only enum / Bool / ClassPointer mirror the non-generic getter path so that any
            // Collection-family conformer's enum- or class-returning nint-only property compiles
            // cleanly; both paths must agree or they produce mismatched ABI or invalid Swift.
            bodyLines.AddRange(CdeclReturnRenderer.Lines(
                propAccess, propertyDecl.SwiftTypeSpec, env.TypeDatabase, returnMapping, scalarParens: false));
        }

        // The dispatch protocol + conformance extension carry no @_cdecl symbol; the anchor pins
        // both symbol-less blocks to the property that owns them so a wrapper-compile failure inside
        // either attributes to it rather than the coarse module scope, and the post-processor strips
        // the anchor with the block it names.
        var originAnchor = OriginAnchorEmitter.LineForWrapper(propertyDecl);

        // Emit protocol
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            private protocol {{protocolName}} {
                static func {{getMethodName}}({{string.Join(", ", protocolParams)}}){{protocolReturnType}}
            }
            """);

        var extensionBody = string.Join("\n        ", bodyLines);
        // Conformance extensions need the same `@available` floor as the wrapped property:
        // Swift type-checks the extension against the deployment target, not the @_cdecl below.
        var getExtensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            propertyDecl.AvailabilityAnnotations, parentTypeDecl);
        var getExtensionAvailPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            getExtensionAvailability, "");
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            {{getExtensionAvailPrefix}}extension {{moduleQualifiedName}}: {{protocolName}} {
                static func {{getMethodName}}({{string.Join(", ", protocolParams)}}){{protocolReturnType}} {
                    {{extensionBody}}
                }
            }
            """);

        // Emit metadata accessor helper at module scope (before @_cdecl).
        // Use resolvable PWT count to match what the C# P/Invoke side passes.
        var getHelperName = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, parentTypeDecl, ctx, getterPwtCount);

        // Emit @_cdecl
        var swiftFuncName = $"_sbw_get_{propertyDecl.Name}_{hash}";
        string cdeclReturnClause = needsResultPtr ? "" : $" -> {returnMapping.CdeclReturnType}";
        var cdeclParamString = string.Join(", ", cdeclParams);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Property getter @_cdecl wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}} (generic static dispatch).
            """);

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            env.ParentDecl, propertyDecl.IsMainActorIsolated, propertyDecl.IsNonisolated);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, env.ParentDecl));
        swiftWriter.WriteLine($"public func {swiftFuncName}({cdeclParamString}){cdeclReturnClause} {{");
        swiftWriter.Indent++;
        var getMetaArgsList = Enumerable.Range(0, parentTypeDecl.GenericParameters.Count).Select(i => $"_metadata{i}");
        var getPwtArgsList = Enumerable.Range(0, getterPwtCount).Select(i => $"_pwt{i}");
        var getMetaArgs = string.Join(", ", getMetaArgsList.Concat(getPwtArgsList));
        swiftWriter.WriteLine($"let parentMeta = {getHelperName}({getMetaArgs})");
        swiftWriter.WriteLine($"let metatype = unsafeBitCast(parentMeta, to: Any.Type.self) as! any {protocolName}.Type");

        if (needsResultPtr || protocolReturnType == "")
            swiftWriter.WriteLine($"metatype.{getMethodName}({string.Join(", ", cdeclCallArgs)})");
        else
            swiftWriter.WriteLine($"return metatype.{getMethodName}({string.Join(", ", cdeclCallArgs)})");

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a property getter on a generic class type.
    /// Delegates to <see cref="GenericProtocolEmitter"/> for the shared protocol+conformance pattern.
    /// </summary>
    private static string EmitGetterProtocolAndConformance(
        SwiftWriter swiftWriter, PropertyDecl propertyDecl, string symbolName, string moduleQualifiedName,
        TypeDecl parentTypeDecl)
    {
        var memberDecl = GenericProtocolEmitter.BuildPropertyGetterMemberDeclaration(
            propertyDecl.Name, propertyDecl.SwiftTypeSpec);
        var extensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            propertyDecl.AvailabilityAnnotations, parentTypeDecl);
        return GenericProtocolEmitter.EmitProtocolAndConformance(
            swiftWriter, "PG", symbolName, memberDecl, moduleQualifiedName,
            originAnchor: FragmentOwners.ForDeclWrapper(propertyDecl).Artifact,
            extensionAvailability: extensionAvailability);
    }

    /// <summary>
    /// Emits protocol declaration and conformance for a property setter on a generic class type.
    /// </summary>
    /// <summary>
    /// Emits a @_cdecl property setter wrapper using generic static dispatch.
    /// </summary>
    private static void EmitGenericStaticSetterWrapper(
        SwiftWriter swiftWriter,
        PropertyDecl propertyDecl,
        string symbolName,
        MethodEnvironment env,
        ModuleEmissionContext ctx,
        TypeDecl parentTypeDecl,
        string moduleQualifiedName,
        bool isClass,
        bool isStatic,
        bool isString,
        bool propertyReferencesT)
    {
        var setHash = EmitterUtility.DeterministicHash8(symbolName);
        var protocolName = $"_SBW_GSPS_{setHash}";
        var setMethodName = $"_sbw_set_{setHash}";
        var abiToSugaredName = WrapperValidation.GetAbiToSugaredNameMap(parentTypeDecl);
        var propertySwiftType = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(propertyDecl.SwiftTypeSpec, abiToSugaredName);

        var protocolParams = new List<string>();
        var cdeclParams = new List<string>();
        var cdeclCallArgs = new List<string>();

        bool isDecomposedOptionalSetter = OptionalMarshalClassifier.IsDecomposed(propertyDecl.SwiftTypeSpec, env.TypeDatabase);
        // Generic-parameter Optional<T>: Swift's Optional<T> ABI varies by T's runtime layout, so we
        // route through the decomposed (payload, hasValue) pattern. The inner T is the parent's
        // generic param (only nameable inside the protocol-extension body where Self is bound),
        // so reconstruction must happen inside the extension method, not at the @_cdecl boundary.
        bool propertyReferencesTOptional = propertyReferencesT && isDecomposedOptionalSetter;

        // NewValue param
        if (propertyReferencesTOptional)
        {
            cdeclParams.Add("_ newValue: UnsafeRawPointer");
            cdeclParams.Add($"_ {OptionalMarshalClassifier.SwiftHasValueParam}: {OptionalMarshalClassifier.SwiftHasValueType}");
            protocolParams.Add("newValuePtr: UnsafeRawPointer");
            protocolParams.Add($"{OptionalMarshalClassifier.SwiftHasValueParam}: {OptionalMarshalClassifier.SwiftHasValueType}");
            cdeclCallArgs.Add("newValuePtr: newValue");
            cdeclCallArgs.Add($"{OptionalMarshalClassifier.SwiftHasValueParam}: {OptionalMarshalClassifier.SwiftHasValueParam}");
        }
        else if (propertyReferencesT)
        {
            cdeclParams.Add("_ newValue: UnsafeRawPointer");
            protocolParams.Add("newValuePtr: UnsafeRawPointer");
            cdeclCallArgs.Add("newValuePtr: newValue");
        }
        else if (isString)
        {
            cdeclParams.Add("_ utf8Ptr: UnsafePointer<UInt8>");
            cdeclParams.Add("_ utf8Len: Int");
            protocolParams.Add("utf8Ptr: UnsafePointer<UInt8>");
            protocolParams.Add("utf8Len: Int");
            cdeclCallArgs.Add("utf8Ptr: utf8Ptr");
            cdeclCallArgs.Add("utf8Len: utf8Len");
        }
        else if (isDecomposedOptionalSetter)
        {
            // Decomposed Optional setter: raw inner payload + hasValue flag
            var innerSpec = ((NamedTypeSpec)propertyDecl.SwiftTypeSpec).GenericParameters[0];
            var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
            cdeclParams.Add("_ newValue: UnsafeRawPointer");
            cdeclParams.Add($"_ {OptionalMarshalClassifier.SwiftHasValueParam}: {OptionalMarshalClassifier.SwiftHasValueType}");
            protocolParams.Add($"newValue: {innerSwiftType}?");
            cdeclCallArgs.Add("newValue: newValueVal");
        }
        else
        {
            var newValueArg = new ArgumentDecl
            {
                SwiftTypeSpec = propertyDecl.SwiftTypeSpec,
                Name = "newValue", PrivateName = "newValue",
                IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null
            };
            // escapeReservedCollision: false — `newValue` is the injected setter synthetic, not a user binding.
            var (cdeclParam, reconstruction1, callArgExpr1) = CdeclParamMapper.Map(newValueArg, "newValue", env, false, escapeReservedCollision: false);
            cdeclParams.Add(cdeclParam);
            var swiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyDecl.SwiftTypeSpec);
            protocolParams.Add($"newValue: {swiftType}");
            // Use the call arg expression from GetCdeclParamMapping (e.g., "newValueOpt" for Optional types)
            // Strip any leading "argLabel: " — we add our own "newValue:" prefix
            var valExpr1 = callArgExpr1;
            var colonIdx1 = callArgExpr1.IndexOf(':');
            if (colonIdx1 >= 0) valExpr1 = callArgExpr1[(colonIdx1 + 2)..];
            cdeclCallArgs.Add($"newValue: {(reconstruction1 != null ? valExpr1 : "newValueVal")}");
        }

        // Metadata params come BEFORE self to match C# PInvokeSignatureBuilder ordering for @_cdecl property accessors
        for (int i = 0; i < parentTypeDecl.GenericParameters.Count; i++)
            cdeclParams.Add($"_ _metadata{i}: UnsafeRawPointer");

        // PWT params: one per protocol conformance per generic parameter.
        // PWT params: only include resolvable conformances (matching C# P/Invoke side).
        int setterPwtCount = MetatypeHelperEmitter.GetResolvablePwtParameterCount(parentTypeDecl, env.TypeDatabase);
        for (int i = 0; i < setterPwtCount; i++)
            cdeclParams.Add($"_ _pwt{i}: UnsafeRawPointer");

        cdeclParams.Add("_ self_: UnsafeMutableRawPointer");
        protocolParams.Add("selfPtr: UnsafeMutableRawPointer");
        cdeclCallArgs.Add("selfPtr: self_");

        // Build extension body
        var bodyLines = new List<string>();
        if (isClass)
            bodyLines.Add("let obj = Unmanaged<AnyObject>.fromOpaque(selfPtr).takeUnretainedValue() as! Self");
        else
            bodyLines.Add("// Mutate through pointer for struct setter");

        string valueExpr;
        if (propertyReferencesTOptional)
        {
            // Reconstruct Optional<Value> inside the extension — Value is in scope here but not at @_cdecl scope.
            // newValuePtr points to a TValue.Size buffer holding just the inner Value (not the full Optional).
            // Read the inner with sugared inner type, wrap in Optional.some, or use nil for None.
            var innerSpec = ((NamedTypeSpec)propertyDecl.SwiftTypeSpec).GenericParameters[0];
            var innerSugared = WrapperValidation.RenderSwiftTypeSpecWithSugaredNames(innerSpec, abiToSugaredName);
            bodyLines.Add($"let val: {propertySwiftType} = {OptionalMarshalClassifier.SwiftHasValueParam} != 0 ? Optional.some(newValuePtr.assumingMemoryBound(to: {innerSugared}.self).pointee) : nil");
            valueExpr = "val";
        }
        else if (propertyReferencesT)
        {
            bodyLines.Add($"let val = newValuePtr.assumingMemoryBound(to: {propertySwiftType}.self).pointee");
            valueExpr = "val";
        }
        else if (isString)
        {
            bodyLines.Add("let val = String(bytes: UnsafeBufferPointer(start: utf8Ptr, count: utf8Len), encoding: .utf8)!");
            valueExpr = "val";
        }
        else
        {
            valueExpr = "newValue";
        }

        if (isClass)
            bodyLines.Add($"obj.{propertyDecl.Name} = {valueExpr}");
        else
            bodyLines.Add($"selfPtr.assumingMemoryBound(to: Self.self).pointee.{propertyDecl.Name} = {valueExpr}");

        // The dispatch protocol + conformance extension carry no @_cdecl symbol; the anchor pins
        // both symbol-less blocks to the property that owns them so a wrapper-compile failure inside
        // either attributes to it rather than the coarse module scope, and the post-processor strips
        // the anchor with the block it names.
        var originAnchor = OriginAnchorEmitter.LineForWrapper(propertyDecl);

        // Emit protocol
        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            private protocol {{protocolName}} {
                static func {{setMethodName}}({{string.Join(", ", protocolParams)}})
            }
            """);

        var extensionBody = string.Join("\n        ", bodyLines);
        var setExtensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            propertyDecl.AvailabilityAnnotations, parentTypeDecl);
        var setExtensionAvailPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            setExtensionAvailability, "");
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            {{setExtensionAvailPrefix}}extension {{moduleQualifiedName}}: {{protocolName}} {
                static func {{setMethodName}}({{string.Join(", ", protocolParams)}}) {
                    {{extensionBody}}
                }
            }
            """);

        // Emit metadata accessor helper at module scope (before @_cdecl)
        var setHelperName = MetatypeHelperEmitter.EmitMetadataAccessorHelperIfNeeded(swiftWriter, parentTypeDecl, ctx, setterPwtCount);

        // Emit @_cdecl
        var swiftFuncName = $"_sbw_set_{propertyDecl.Name}_{EmitterUtility.DeterministicHash8(symbolName)}";
        var cdeclParamString = string.Join(", ", cdeclParams);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            // Property setter @_cdecl wrapper for {{moduleQualifiedName}}.{{propertyDecl.Name}} (generic static dispatch).
            """);

        bool needsMainActor = WrapperValidation.NeedsMainActorAnnotation(
            env.ParentDecl, propertyDecl.IsMainActorIsolated, propertyDecl.IsNonisolated);
        WrapperEmitterHelpers.EmitCdeclAnnotation(swiftWriter, symbolName, needsMainActor,
            WrapperEmitterHelpers.MergeAvailability(propertyDecl.AvailabilityAnnotations, env.ParentDecl));
        swiftWriter.WriteLine($"public func {swiftFuncName}({cdeclParamString}) {{");
        swiftWriter.Indent++;

        // Reconstruct non-T concrete params for protocol dispatch
        if (!propertyReferencesT && !isString)
        {
            if (isDecomposedOptionalSetter)
            {
                // Decomposed Optional: reconstruct T? from (payload, hasValue) for protocol dispatch
                var innerSpec = ((NamedTypeSpec)propertyDecl.SwiftTypeSpec).GenericParameters[0];
                var innerSwiftType = ExistentialBypassEmitter.RenderModuleQualifiedSwiftTypeSpec(innerSpec);
                swiftWriter.WriteLine(OptionalMarshalClassifier.SwiftReconstructOptional(
                    OptionalMarshalClassifier.SwiftHasValueParam, "newValue", innerSwiftType, "newValueVal"));
            }
            else
            {
                var newValueArg = new ArgumentDecl
                {
                    SwiftTypeSpec = propertyDecl.SwiftTypeSpec,
                    Name = "newValue", PrivateName = "newValue",
                    IsInOut = false, IsGeneric = false, ParentDecl = null, ModuleDecl = null
                };
                // escapeReservedCollision: false — `newValue` is the injected setter synthetic, not a user binding.
                var (_, reconstruction, _) = CdeclParamMapper.Map(newValueArg, "newValue", env, false, escapeReservedCollision: false);
                if (reconstruction != null)
                    swiftWriter.WriteLine(reconstruction);
            }
        }

        var setMetaArgsList = Enumerable.Range(0, parentTypeDecl.GenericParameters.Count).Select(i => $"_metadata{i}");
        var setPwtArgsList = Enumerable.Range(0, setterPwtCount).Select(i => $"_pwt{i}");
        var setMetaArgs = string.Join(", ", setMetaArgsList.Concat(setPwtArgsList));
        swiftWriter.WriteLine($"let parentMeta = {setHelperName}({setMetaArgs})");
        swiftWriter.WriteLine($"let metatype = unsafeBitCast(parentMeta, to: Any.Type.self) as! any {protocolName}.Type");
        swiftWriter.WriteLine($"metatype.{setMethodName}({string.Join(", ", cdeclCallArgs)})");

        swiftWriter.Indent--;
        swiftWriter.WriteLine("}");
    }

    private static string EmitSetterProtocolAndConformance(
        SwiftWriter swiftWriter, PropertyDecl propertyDecl, string symbolName, string moduleQualifiedName,
        TypeDecl parentTypeDecl)
    {
        var protocolName = $"_SBW_PS_{EmitterUtility.DeterministicHash8(symbolName)}";
        var propertySwiftType = ExistentialBypassEmitter.RenderSwiftTypeSpec(propertyDecl.SwiftTypeSpec);

        // Conformance extensions are top-level decls and don't inherit the enclosing
        // type's availability. Merge member + parent-chain annotations so the extension
        // type-checks against the deployment target.
        var extensionAvailability = WrapperEmitterHelpers.MergeAvailability(
            propertyDecl.AvailabilityAnnotations, parentTypeDecl);
        var extensionAvailPrefix = WrapperEmitterHelpers.BuildAvailabilityHeredocPrefix(
            extensionAvailability, string.Empty);

        // The dispatch protocol + conformance extension carry no @_cdecl symbol; the anchor pins
        // both symbol-less blocks to the property that owns them so a wrapper-compile failure inside
        // either attributes to it rather than the coarse module scope, and the post-processor strips
        // the anchor with the block it names.
        var originAnchor = OriginAnchorEmitter.LineForWrapper(propertyDecl);

        swiftWriter.WriteLine();
        swiftWriter.WriteLines($$"""
            {{originAnchor}}
            private protocol {{protocolName}} {
                var {{propertyDecl.Name}}: {{propertySwiftType}} { get set }
            }
            {{originAnchor}}
            {{extensionAvailPrefix}}extension {{moduleQualifiedName}}: {{protocolName}} {}
            """);

        return protocolName;
    }

}
