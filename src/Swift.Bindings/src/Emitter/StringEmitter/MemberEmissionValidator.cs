// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared validation logic for determining if a member can be emitted.
/// Used by both handlers (for emission) and conformance checking (for interface selection).
/// </summary>
public static class MemberEmissionValidator
{
    /// <summary>
    /// Checks whether a property's type is unsupported for naming collision purposes.
    /// Returns true for properties that will be skipped by the emitter — these should not
    /// trigger nested type renames. Checks both unsupported modules (SwiftUI/Combine) and
    /// unresolvable named types (AnyType fallbacks).
    /// For non-NamedTypeSpec types (closures, tuples, generics), conservatively returns false
    /// (includes them in collision set — false positive renames are safer than missing collisions).
    /// </summary>
    public static bool HasUnsupportedPropertyType(PropertyDecl property, ITypeDatabase typeDatabase)
    {
        if (ReferencesUnsupportedModule(property.SwiftTypeSpec, typeDatabase))
            return true;

        // For named types, check if the type can be resolved in the database.
        // Unresolvable types fall through to AnyType and the emitter skips them.
        if (property.SwiftTypeSpec is NamedTypeSpec namedType)
        {
            // Generic type parameters are always handled by the emitter
            if (TypeSpecHelpers.IsGenericTypeParameter(namedType.Name))
                return false;

            // Well-known Swift standard library types are always resolvable at runtime
            // even if not registered in a unit test's type database
            var moduleName = namedType.Name.Contains('.')
                ? namedType.Name.Substring(0, namedType.Name.IndexOf('.'))
                : null;
            if (moduleName is "Swift" or "Foundation" or "CoreFoundation" or "CoreGraphics" or "Darwin")
                return false;

            // Check type database — if resolvable to a concrete type, property will be emitted.
            // TryGetTypeRecord returns true with AnyType for existentials and generics that
            // can't be concretely resolved — those are NOT truly supported and the emitter
            // will skip them, so we must treat them as unsupported here too.
            if (typeDatabase.TryGetTypeRecord(property.SwiftTypeSpec, out var record)
                && record != TypeDatabaseExtensions.AnyType)
            {
                // Types that are present in the database but marked Unemittable (e.g.,
                // single-case no-payload enums) are stripped by the emitter. Treat
                // references to them as unsupported.
                if (record.Flags.HasFlag(TypeRecordFlags.Unemittable))
                    return true;
                return false;
            }

            // Unresolvable type from non-standard module → will be AnyType → skipped
            return true;
        }

        // Non-named types (closures, tuples, existentials) — conservatively include
        return false;
    }

    /// <summary>
    /// Checks if a property can be emitted. Returns null if valid, SkipReason if not.
    /// </summary>
    public static SkipReason? CanEmitProperty(
        PropertyDecl property,
        ITypeDatabase typeDatabase,
        out string? skipDetails,
        out string? projectedTypeName,
        ConcreteSpecializationEngine? specializationEngine = null)
    {
        skipDetails = null;
        projectedTypeName = null;

        var asyncStreamHandler = new AsyncStreamHandler(typeDatabase);
        var existentialHandler = new ExistentialHandler(typeDatabase)
            { SpecializationEngine = specializationEngine };
        var closureHandler = new ClosureHandler(typeDatabase);
        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        // Skip internal properties — not accessible from the wrapper module
        if (property.IsModuleInternal)
        {
            skipDetails = "Internal property suppressed from bindings.";
            return SkipReason.ModuleInternal;
        }

        // Skip @_spi properties — only visible to SPI consumers
        if (property.IsSpiProtected)
        {
            skipDetails = "@_spi property suppressed from bindings.";
            return SkipReason.ModuleInternal;
        }

        // Constrained-extension multi-specialization conflict.
        // Multiple `extension Wrapper where T == ConcreteN` blocks each define a
        // property with the same Swift name. The ABI dump emits one Var node per
        // specialization (e.g., StoreKit's three `extension VerificationResult
        // where SignedType == ...` blocks each contribute a copy of
        // `jwsRepresentation`), and each carries its own specialization-specific
        // mangled accessor symbol. C# generics have only one specialization at
        // runtime, so the merged C# class cannot dispatch among them: emitting
        // one would silently call its symbol for ALL closed generic instantiations
        // (returning the wrong specialization's data — undefined behavior).
        // Skip ALL conflicting copies; users who need a specific specialization
        // must call the mangled symbol via direct P/Invoke. The PropertyWrapperEmitter
        // already defers these in `CanEmitGenericClassPropertyWrapper`, so no Swift
        // wrapper is generated either. Regression coverage lives in
        // BindingTests/.../Generics/ConstrainedExtensionDedup.swift.
        if (property.ParentDecl is TypeDecl constrainedExtensionParent && constrainedExtensionParent.IsGeneric)
        {
            int siblingCount = 0;
            foreach (var sibling in constrainedExtensionParent.Properties)
            {
                if (sibling.Name == property.Name && sibling.IsStatic == property.IsStatic)
                {
                    siblingCount++;
                    if (siblingCount > 1)
                        break;
                }
            }
            if (siblingCount > 1)
            {
                skipDetails = $"Multiple constrained-extension specializations of '{property.Name}' on generic type '{constrainedExtensionParent.Name}' cannot be dispatched via C# generics.";
                return SkipReason.UnsupportedType;
            }
        }

        // B19: Skip properties referencing SwiftUI/Combine types (unless registered in type database)
        if (MemberEmissionValidator.ReferencesUnsupportedModule(property.SwiftTypeSpec, typeDatabase))
        {
            skipDetails = "Property type references unsupported module (SwiftUI/Combine).";
            return SkipReason.SwiftUIConstraint;
        }

        // Check AsyncStream properties
        if (asyncStreamHandler.IsAsyncStream(property.SwiftTypeSpec))
        {
            if (!asyncStreamHandler.IsSupportedAsyncStream(property.SwiftTypeSpec))
            {
                skipDetails = "AsyncStream element type is not supported.";
                return SkipReason.UnsupportedAsyncStream;
            }
            // Custom actor AsyncStream properties are emitted as `Task { for await e in await __self.prop { ... } }`.
            // The `await` on the property access hops to the actor's serial executor; the stream itself
            // is Sendable and iterates without further isolation. Parameterized-protocol element types
            // are still blocked: the @_cdecl top-level function can't spell iOS 16+ parameterized
            // protocol types at an earlier deployment target.
            //
            // Two-pass isolation policy: this validator is pass 1 — it decides WHETHER the
            // property can be emitted based on element type + actor/parameterized-protocol shape,
            // and reads `property.IsNonisolated` only through that lens. AsyncStreamEmitter is
            // pass 2 — given the property passes here, it decides HOW the Swift wrapper body
            // hops into actor isolation (`await __self.prop` vs direct access) by re-reading
            // `IsNonisolated` to suppress the `await` on nonisolated actor members. The two
            // reads serve orthogonal purposes; changing one without the other would either emit
            // uncompilable Swift (missing/extra `await`) or silently skip emittable members.
            // See AsyncStreamEmitter.EmitSwiftWrapperFunction (needsActorAwait).
            if (!property.IsStatic && property.ParentDecl is ClassDecl { IsActor: true } &&
                WrapperValidation.ContainsParameterizedProtocol(property.SwiftTypeSpec, typeDatabase))
            {
                skipDetails = "Actor AsyncStream property has parameterized-protocol element type (@_cdecl wrapper cannot spell iOS 16+ parameterized protocol).";
                return SkipReason.ActorIsolatedAsyncStream;
            }
            // AsyncStream is handled specially - it's emittable
            projectedTypeName = asyncStreamHandler.GetCSharpElementType(property.SwiftTypeSpec);
            return null;
        }

        // Check existential types (any Protocol)
        bool isExistential = existentialHandler.IsExistential(property.SwiftTypeSpec);
        if (isExistential)
        {
            var protocolList = existentialHandler.ToProtocolListTypeSpec(property.SwiftTypeSpec);
            if (protocolList == null || !existentialHandler.IsSupportedExistential(protocolList))
            {
                skipDetails = "Existential contains unsupported protocol count.";
                return SkipReason.UnsupportedExistential;
            }
            projectedTypeName = existentialHandler.GetPublicExistentialType(protocolList);
        }

        // Check Optional-wrapped existential types like (any DataCaching)?
        bool isOptionalExistential = existentialHandler.IsOptionalExistential(property.SwiftTypeSpec);
        if (isOptionalExistential)
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(property.SwiftTypeSpec);
            if (innerProtocolList == null || !existentialHandler.IsSupportedExistential(innerProtocolList))
            {
                skipDetails = "Optional existential contains unsupported protocol count.";
                return SkipReason.UnsupportedExistential;
            }
            projectedTypeName = existentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
        }

        // Check closure properties
        bool isClosure = closureHandler.IsClosure(property);
        if (isClosure)
        {
            var closureTypeSpec = closureHandler.GetClosureTypeSpec(property);
            if (closureTypeSpec == null || !closureHandler.IsSupportedClosure(closureTypeSpec))
            {
                skipDetails = "Closure type is not supported.";
                return SkipReason.UnsupportedClosure;
            }
            // CanInvokeFromCSharp and C12 return-marshalling checks moved to PropertyHandler.
            // When these fail, PropertyHandler emits setter-only properties instead of skipping.
            // This allows the callback (setter) path to work even when getter invocation is blocked.
            bool isOptionalClosure = closureHandler.IsOptionalClosure(property.SwiftTypeSpec);
            projectedTypeName = isOptionalClosure
                ? closureHandler.GetCSharpOptionalDelegateType(property.SwiftTypeSpec)
                : closureHandler.GetCSharpDelegateType(closureTypeSpec);
        }

        // C1: Check for tuples (including inside Optional/generic wrappers) with unsupported elements
        if (ContainsUnsupportedTupleElement(property.SwiftTypeSpec, typeDatabase))
        {
            skipDetails = "Type contains tuple with unsupported element (closure or AnyType).";
            return SkipReason.UnsupportedSignature;
        }

        // Type resolution
        bool processed = typeDatabase.TryGetTypeRecord(property.SwiftTypeSpec, out var typeRecord);
        bool isGenericTypeParam = TypeSpecHelpers.IsGenericTypeParameter(property.SwiftTypeSpec) &&
                                  property.ParentDecl is TypeDecl gtParent && gtParent.IsGeneric;
        bool isBoundGeneric = boundGenericsHandler.IsBoundGeneric(property);

        // Only skip if not a special type that's handled above
        if (!processed && !isExistential && !isOptionalExistential && !isClosure && !isGenericTypeParam && !isBoundGeneric)
        {
            skipDetails = $"Type resolution failed for property type '{property.SwiftTypeSpec}'.";
            return SkipReason.UnsupportedType;
        }

        // Check for no public accessors
        if (property.Accessors.Count == 0)
        {
            skipDetails = "Property has no public accessors to emit.";
            return SkipReason.UnsupportedType;
        }

        if (boundGenericsHandler.HasBareGenericUsage(property.SwiftTypeSpec, property.ModuleDecl))
        {
            skipDetails = $"Type '{property.SwiftTypeSpec}' contains generic declaration used without type arguments.";
            return SkipReason.UnsupportedSignature;
        }

        // Calculate projected type name if not already set
        if (projectedTypeName == null)
        {
            if (isBoundGeneric)
            {
                if (boundGenericsHandler.HasNonSwiftObjectGenericArg(property.SwiftTypeSpec))
                {
                    skipDetails = "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.";
                    return SkipReason.UnsatisfiedGenericConstraint;
                }

                if (boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(property.SwiftTypeSpec, property, out var constraintDetails))
                {
                    skipDetails = constraintDetails;
                    return SkipReason.UnsatisfiedGenericConstraint;
                }

                if (boundGenericsHandler.TryGetFirstExistentialTypeArgument(property.SwiftTypeSpec, out var existentialType))
                {
                    if (!boundGenericsHandler.IsContainerWithSupportedDirectExistential(property.SwiftTypeSpec))
                    {
                        skipDetails = $"Bound generic contains existential type argument '{existentialType}'.";
                        return SkipReason.UnsupportedExistential;
                    }
                }

                var boundGenericContext = property.ParentDecl is TypeDecl boundParentType && boundParentType.IsGeneric
                    ? GenericContext.FromType(boundParentType)
                    : GenericContext.Empty;
                var factory = new TypeProjectionFactory();
                var projection = factory.Project(property.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = typeDatabase,
                    IsParameter = false,
                    GenericContext = boundGenericContext,
                    ParentTypeDecl = property.ParentDecl as TypeDecl
                });
                if (projection != null)
                {
                    projectedTypeName = projection.PublicType;
                }
                else if (property.SwiftTypeSpec is NamedTypeSpec propBoundGeneric && propBoundGeneric.ContainsGenericParameters)
                {
                    var bgh = new BoundGenericsHandler(typeDatabase);
                    projectedTypeName = bgh.TranslateBoundGenericTypeToCSharp(property.SwiftTypeSpec, boundGenericContext);
                }
                else
                {
                    projectedTypeName = typeDatabase.GetTypeRecordOrAnyType(property.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
                }
            }
            else if (isGenericTypeParam && property.ParentDecl is TypeDecl genericParentType && genericParentType.IsGeneric)
            {
                var context = GenericContext.FromType(genericParentType);
                var typeName = (property.SwiftTypeSpec as NamedTypeSpec)?.Name;
                if (typeName != null && context.TryResolve(typeName, out var resolved))
                    projectedTypeName = resolved;
                else if (typeRecord != null)
                    projectedTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;
                else
                {
                    skipDetails = $"Generic type parameter not resolvable for property {property.Name}.";
                    return SkipReason.AnyTypeFallback;
                }
            }
            else if (typeRecord != null)
            {
                // Try factory-based projection first for parity with interface type projection
                // (mirrors CanEmitMethod's return type projection via TypeProjectionFactory).
                // Without this, String properties project to "Swift.SwiftString" here but
                // GetInterfacePropertyType projects to "string", causing AreTypesCompatible
                // to reject valid protocol conformances.
                var propFactory = new TypeProjectionFactory();
                var propProjection = propFactory.Project(property.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = typeDatabase,
                    IsParameter = false,
                    GenericContext = GenericContext.Empty,
                    ParentTypeDecl = property.ParentDecl as TypeDecl
                });
                projectedTypeName = propProjection?.PublicType
                    ?? typeRecord.CSharpTypeName.FullyQualifiedName;
            }
            else
            {
                skipDetails = $"Property type not resolved for {property.Name}.";
                return SkipReason.UnsupportedType;
            }
        }

        // Check for AnyType fallback
        if (projectedTypeName != null && projectedTypeName.Contains("AnyType"))
        {
            skipDetails = $"Property type resolved to AnyType ({projectedTypeName}).";
            return SkipReason.AnyTypeFallback;
        }

        if (projectedTypeName != null && TypeDatabaseExtensions.IsBareGenericTypeName(projectedTypeName))
        {
            skipDetails = $"Property type resolved to bare generic type ({projectedTypeName}).";
            return SkipReason.UnsupportedSignature;
        }

        // NOTE: Non-simple enum property types are now supported (B18 gate removed).
        // PInvokeEmitter handles them via SwiftIndirectResult (non-frozen) or IntPtr (frozen).
        // The .Buffer suffix at PInvokeEmitter:173 is never reached for enums.

        // NOTE: Async properties are now supported — emitted as Task-returning methods
        // (e.g., GetPropertyNameAsync()) via PropertyHandler.EmitAsyncPropertyAsMethods().

        // Accessor preflight: Check each accessor method can be emitted
        // This mirrors PropertyHandler.Emit lines 261-323
        foreach (var accessor in property.Accessors)
        {
            var accessorMethod = accessor.Method;

            // Mark as accessor before preflight - mirrors PropertyHandler.cs:269
            // This affects type conversion behavior (e.g., native remapping is skipped for accessors)
            accessorMethod.IsAccessor = true;

            // Check generic protocol constraints on accessor — parent-baseline constraints
            // on SUPPORTED protocols are skipped (handled by type-level where clause).
            // Unsupported protocols (PAT or Self) always block, even if parent-declared.
            // Extra constraints (conditional extension) on supported protocols pass through.
            if (accessorMethod.IsGeneric)
            {
                var parentTypeGenericParams = accessorMethod.ParentDecl is TypeDecl accessorParentType
                    ? accessorParentType.GenericParameters
                    : null;

                foreach (var param in accessorMethod.GenericParameters)
                {
                    foreach (var conformance in param.GenericConformances)
                    {
                        if (conformance.Kind != ConformanceKind.Protocol)
                            continue;

                        // Skip parent-baseline constraints only when the protocol is supported
                        if (!MethodValidationGates.IsConditionalExtensionConstraint(param, conformance, parentTypeGenericParams) &&
                            !MethodValidationGates.IsUnsupportedProtocolConstraint(conformance.ConformanceTarget, typeDatabase))
                            continue;

                        // Block if unsupported (associated types or Self)
                        if (MethodValidationGates.IsUnsupportedProtocolConstraint(conformance.ConformanceTarget, typeDatabase))
                        {
                            skipDetails = $"Accessor '{accessorMethod.Name}' has constraints on protocols with associated types or self requirements.";
                            return SkipReason.GenericProtocolConstraint;
                        }
                    }
                }
            }

            // Check bound generic constraints on accessor return type
            if (accessorMethod.CSSignature.Count > 0)
            {
                var returnArg = accessorMethod.CSSignature[0];
                if (boundGenericsHandler.IsBoundGeneric(returnArg) &&
                    boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(returnArg.SwiftTypeSpec, accessorMethod, out var returnConstraintDetails))
                {
                    skipDetails = $"Accessor '{accessorMethod.Name}' return type: {returnConstraintDetails}";
                    return SkipReason.UnsatisfiedGenericConstraint;
                }
            }

            // Check bound generic constraints on accessor parameters
            foreach (var argument in accessorMethod.CSSignature.Skip(1))
            {
                if (boundGenericsHandler.IsBoundGeneric(argument) &&
                    boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(argument.SwiftTypeSpec, accessorMethod, out var paramConstraintDetails))
                {
                    skipDetails = $"Accessor '{accessorMethod.Name}' parameter: {paramConstraintDetails}";
                    return SkipReason.UnsatisfiedGenericConstraint;
                }
            }

            // Check accessor signature for placeholders
            var accessorEnv = new MethodEnvironment(accessorMethod, typeDatabase);
            var signatureHandler = new SignatureHandler(accessorEnv);
            if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
            {
                skipDetails = $"Accessor '{accessorMethod.Name}' has unsupported signature.";
                return SkipReason.UnsupportedSignature;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if a closure property would be emitted as setter-only by PropertyHandler.
    /// This happens when CanInvokeFromCSharp fails or C12 return-marshalling is unsupported —
    /// the getter is stripped but the setter (callback path) still works.
    /// Used by ProtocolConformanceValidator to detect accessor contract mismatches.
    /// </summary>
    public static bool IsSetterOnlyClosureProperty(PropertyDecl property, ITypeDatabase typeDatabase)
    {
        var closureHandler = new ClosureHandler(typeDatabase);
        if (!closureHandler.IsClosure(property))
            return false;
        var closureTypeSpec = closureHandler.GetClosureTypeSpec(property);
        if (closureTypeSpec == null || !closureHandler.IsSupportedClosure(closureTypeSpec))
            return false; // Would be skipped entirely, not setter-only
        if (!closureHandler.CanInvokeFromCSharp(closureTypeSpec))
            return true;
        if (!closureTypeSpec.ReturnType.IsEmptyTuple)
        {
            var returnPInvokeType = closureHandler.TranslateTypeSpecToPInvokeType(closureTypeSpec.ReturnType);
            if (returnPInvokeType == "void*" &&
                !ClosureEmitter.IsInvokeThunkCompatibleReturn(closureTypeSpec.ReturnType, closureHandler))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if a method can be emitted. Returns null if valid, SkipReason if not.
    /// </summary>
    public static SkipReason? CanEmitMethod(
        MethodDecl method,
        ITypeDatabase typeDatabase,
        out string? skipDetails,
        out string? projectedReturnTypeName)
    {
        skipDetails = null;
        projectedReturnTypeName = null;

        // Early-out: shared hard gates via MemberGateEvaluator
        // Checks bare generic, non-ISwiftObject bound generic, unsupported module (SwiftUI/Combine).
        // Must run BEFORE the constructor early-return so constructors with unsupported params are also caught.
        var gateEvaluator = new MemberGateEvaluator(typeDatabase);
        var hardGateResult = gateEvaluator.EvaluateHardGates(method, method.ModuleDecl);
        if (hardGateResult.IsSkipped)
        {
            skipDetails = hardGateResult.Details;
            return hardGateResult.Reason!.Value;
        }

        // Skip constructors for remaining checks
        if (method.IsConstructor)
            return null;

        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        // Check for protocol constraints with associated types — parent-baseline constraints
        // on SUPPORTED protocols are skipped (handled by type-level where clause).
        // Unsupported protocols (PAT or Self) always block, even if parent-declared.
        // Extra constraints (conditional extension) on supported protocols pass through.
        if (method.IsGeneric)
        {
            var parentTypeGenericParams = method.ParentDecl is TypeDecl methodParentType
                ? methodParentType.GenericParameters
                : null;

            foreach (var param in method.GenericParameters)
            {
                foreach (var conformance in param.GenericConformances)
                {
                    if (conformance.Kind != ConformanceKind.Protocol)
                        continue;

                    // Skip parent-baseline constraints only when the protocol is supported
                    if (!MethodValidationGates.IsConditionalExtensionConstraint(param, conformance, parentTypeGenericParams) &&
                        !MethodValidationGates.IsUnsupportedProtocolConstraint(conformance.ConformanceTarget, typeDatabase))
                        continue;

                    // Block if unsupported (associated types or Self)
                    if (MethodValidationGates.IsUnsupportedProtocolConstraint(conformance.ConformanceTarget, typeDatabase))
                    {
                        skipDetails = "Method has constraints on protocols with associated types or self requirements.";
                        return SkipReason.GenericProtocolConstraint;
                    }
                }
            }
        }

        // Check bound generic arguments for emission-specific issues (unsatisfied constraints, existentials)
        // Note: bare generic and non-ISwiftObject checks are handled by EvaluateHardGates above
        foreach (var argument in method.CSSignature)
        {
            if (!boundGenericsHandler.IsBoundGeneric(argument))
                continue;

            if (boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(argument.SwiftTypeSpec, method, out var constraintDetails))
            {
                skipDetails = constraintDetails;
                return SkipReason.UnsatisfiedGenericConstraint;
            }

            // Array<any P.Type> with known hint conformers is handled by MetatypeArrayBridgeEmitter.
            // Only exempt when the method shape is actually bridge-eligible (free functions in the
            // MVP). Instance methods/constructors that slip through the exemption would fall into
            // an incompatible fallback and produce broken wrappers — let the existential skip apply.
            if (BoundGenericsHandler.IsArrayOfExistentialMetatypes(
                    argument.SwiftTypeSpec,
                    method.ModuleDecl?.Name,
                    out _) &&
                MetatypeArrayBridgeEmitter.IsEligible(method))
                continue;

            // B6: Catch existentials in non-container bound generics.
            // Allow through containers with supported direct existential elements:
            // Array<any P>, Dictionary<K, any P>, Optional<any P>, and Optional-wrapped containers.
            // This matches the same check in MethodHandler.Emit() to keep validator and emitter consistent.
            if (boundGenericsHandler.TryGetFirstExistentialTypeArgument(argument.SwiftTypeSpec, out var existentialType))
            {
                if (!boundGenericsHandler.IsContainerWithSupportedDirectExistential(argument.SwiftTypeSpec))
                {
                    skipDetails = $"Bound generic contains existential type argument '{existentialType}'.";
                    return SkipReason.UnsupportedExistential;
                }
            }
        }

        // Reject methods whose return type or any parameter is a DIRECT existential
        // that IsSupportedExistential rejects — currently class-bounded compositions
        // like `any ClassA & ProtoP`. WrapperEmitter.EmitExistentialContainerMarshalling
        // would silently skip the container allocation for those args, leaving the body
        // to pass the raw managed reference into a P/Invoke that expects a container.
        // Skip the method instead of emitting an unsound cast / signature mismatch.
        var existentialHandler = new ExistentialHandler(typeDatabase);
        foreach (var argument in method.CSSignature)
        {
            if (existentialHandler.IsExistential(argument.SwiftTypeSpec))
            {
                var protocolList = existentialHandler.ToProtocolListTypeSpec(argument.SwiftTypeSpec);
                if (protocolList != null && !existentialHandler.IsSupportedExistential(protocolList))
                {
                    skipDetails = "Method has a direct existential parameter or return type that is unsupported (class-bounded composition or unsupported protocol count).";
                    return SkipReason.UnsupportedExistential;
                }
            }
            if (existentialHandler.IsOptionalExistential(argument.SwiftTypeSpec))
            {
                var innerProtocolList = existentialHandler.UnwrapOptionalExistential(argument.SwiftTypeSpec);
                if (innerProtocolList != null && !existentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    skipDetails = "Method has an Optional existential parameter or return type that is unsupported (class-bounded composition or unsupported protocol count).";
                    return SkipReason.UnsupportedExistential;
                }
            }
        }

        // B20: Check for unsupported closures in method parameters.
        // Mirrors property closure check in CanEmitProperty (lines 121-150).
        var closureHandler = new ClosureHandler(typeDatabase);
        foreach (var argument in method.CSSignature.Skip(1))
        {
            if (closureHandler.IsClosure(argument))
            {
                var closureTypeSpec = closureHandler.GetClosureTypeSpec(argument);
                if (closureTypeSpec == null || !closureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    skipDetails = $"Parameter '{argument.Name}' has unsupported closure type.";
                    return SkipReason.UnsupportedClosure;
                }
            }
        }

        // B21: Check for unsupported closure return types.
        // Methods returning closures where IsSupportedClosure is false will crash in
        // EmitReturnMethod (fallthrough to GetTypeRecordOrThrow on ClosureTypeSpec).
        if (method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (closureHandler.IsClosure(returnArg))
            {
                var closureTypeSpec = closureHandler.GetClosureTypeSpec(returnArg);
                if (closureTypeSpec == null || !closureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    skipDetails = "Return type is an unsupported closure type.";
                    return SkipReason.UnsupportedClosure;
                }
            }
        }

        // NOTE: Non-simple enum method return types are now supported (B18 gate removed).
        // PInvokeEmitter handles them via SwiftIndirectResult (non-frozen) or IntPtr (frozen).

        // C6: For async methods with tuple returns, the tuple elements are flattened into
        // [UnmanagedCallersOnly] callback parameters. Non-simple enums (managed classes) are
        // non-blittable and cause CS8894 in callback signatures. Check tuple elements.
        if (method.IsAsync && method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is TupleTypeSpec tupleReturn && !tupleReturn.IsEmptyTuple)
            {
                foreach (var element in tupleReturn.Elements)
                {
                    // Unwrap Optional<T> to check inner type
                    var elementToCheck = element;
                    if (element is NamedTypeSpec optionalElement &&
                        optionalElement.Name == "Swift.Optional" &&
                        optionalElement.GenericParameters.Count == 1)
                    {
                        elementToCheck = optionalElement.GenericParameters[0];
                    }

                    if (elementToCheck is NamedTypeSpec namedElement &&
                        namedElement.HasModule() && !namedElement.ContainsGenericParameters)
                    {
                        var elementSwiftName = SwiftTypeName.FromModuleQualifiedName(namedElement.Name);
                        if (typeDatabase.TryGetTypeRecord(elementSwiftName, out var elementRecord) &&
                            elementRecord.Kind == TypeRecordKind.Enum &&
                            !elementRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                        {
                            skipDetails = $"Async tuple return contains non-simple enum '{namedElement.Name}' which is non-blittable in callback.";
                            return SkipReason.UnsupportedSignature;
                        }
                    }
                }
            }
        }

        // Check signature for placeholders using SignatureHandler pattern
        // Create a temporary environment just to check the signature
        var tempEnv = new MethodEnvironment(method, typeDatabase);
        var signatureHandler = new SignatureHandler(tempEnv);
        if (signatureHandler.GetWrapperSignature().ContainsPlaceholder)
        {
            skipDetails = "Method signature contains unsupported placeholder type.";
            return SkipReason.UnsupportedSignature;
        }

        // Get projected return type - must mirror MethodSignature.HandleReturnType exactly
        if (method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
            {
                // Try factory-based projection (mirrors WrapperSignatureBuilder.HandleReturnType)
                var factory = new TypeProjectionFactory();
                var projection = factory.Project(returnArg.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = typeDatabase,
                    IsParameter = false,
                    ParentTypeDecl = method.ParentDecl as TypeDecl
                });
                if (projection != null)
                {
                    projectedReturnTypeName = projection.PublicType;
                }
                else if (returnArg.SwiftTypeSpec is NamedTypeSpec retBoundGeneric && retBoundGeneric.ContainsGenericParameters)
                {
                    // Bound generic fallback: produce raw ABI type name (mirrors MethodSignature.HandleReturnType)
                    var bgh = new BoundGenericsHandler(typeDatabase);
                    projectedReturnTypeName = bgh.TranslateBoundGenericTypeToCSharp(returnArg.SwiftTypeSpec, GenericContext.Empty);
                }
                else
                {
                    // Fallback: TypeRecord lookup (protocol → AnyType, others → CSharpTypeName)
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(returnArg.SwiftTypeSpec);
                    if (typeRecord.Kind == TypeRecordKind.Protocol)
                    {
                        projectedReturnTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                    }
                    else
                    {
                        projectedReturnTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;
                    }
                }
            }
            else
            {
                projectedReturnTypeName = "void";
            }
        }
        else
        {
            projectedReturnTypeName = "void";
        }

        // Handle async methods
        if (method.IsAsync)
        {
            if (projectedReturnTypeName == "void")
                projectedReturnTypeName = "Task";
            else
                projectedReturnTypeName = $"Task<{projectedReturnTypeName}>";
        }

        if (projectedReturnTypeName != null && TypeDatabaseExtensions.IsBareGenericTypeName(projectedReturnTypeName))
        {
            skipDetails = $"Method return type resolved to bare generic type ({projectedReturnTypeName}).";
            return SkipReason.UnsupportedSignature;
        }

        return null;
    }

    /// <summary>
    /// Checks if a subscript can be emitted. Returns null if valid, SkipReason if not.
    /// NOTE: Subscripts on concrete types (structs/classes/enums) are not yet emitted.
    /// This validator is used by ProtocolConformanceValidator to reject interfaces
    /// that require subscripts until we implement SubscriptHandler for concrete types.
    /// </summary>
    public static SkipReason? CanEmitSubscript(
        SubscriptDecl subscript,
        ITypeDatabase typeDatabase,
        out string? skipDetails,
        out string? projectedReturnTypeName)
    {
        skipDetails = null;
        projectedReturnTypeName = null;

        // IMPORTANT: Subscripts on concrete types are not yet emitted.
        // We have SubscriptDecl in the model, but no SubscriptHandler to emit them.
        // Until we implement that, subscripts cannot satisfy interface requirements.
        // Note: Protocol interface subscripts ARE emitted (in ProtocolHandler and ProtocolProxyEmitter),
        // but concrete type subscripts are NOT emitted yet.
        // This causes CS0535 errors when a concrete type declares IProtocol but has no subscript impl.
        // For now, reject subscripts as "not supported" so the validator rejects the interface.
        skipDetails = "Subscripts on concrete types are not yet supported.";
        return SkipReason.UnsupportedType;

        // The code below would validate if a subscript COULD be emitted, for future use:
        #pragma warning disable CS0162 // Unreachable code - keeping for future implementation
        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        // Check return type
        if (subscript.ReturnTypeSpec != null)
        {
            var subscriptGenericContext = subscript.ParentDecl is TypeDecl subscriptParentType && subscriptParentType.IsGeneric
                ? GenericContext.FromType(subscriptParentType)
                : GenericContext.Empty;
            var subscriptFactory = new TypeProjectionFactory();
            var subscriptProjection = subscriptFactory.Project(subscript.ReturnTypeSpec, new ProjectionContext
            {
                TypeDatabase = typeDatabase,
                IsParameter = false,
                GenericContext = subscriptGenericContext
            });
            if (subscriptProjection != null)
            {
                projectedReturnTypeName = subscriptProjection.PublicType;
            }
            else if (subscript.ReturnTypeSpec is NamedTypeSpec subBoundGeneric && subBoundGeneric.ContainsGenericParameters)
            {
                var bgh = new BoundGenericsHandler(typeDatabase);
                projectedReturnTypeName = bgh.TranslateBoundGenericTypeToCSharp(subscript.ReturnTypeSpec, subscriptGenericContext);
            }
            else
            {
                projectedReturnTypeName = typeDatabase.GetTypeRecordOrAnyType(subscript.ReturnTypeSpec).CSharpTypeName.FullyQualifiedName;
            }
        }
        else
        {
            skipDetails = "Subscript has no return type.";
            return SkipReason.UnsupportedType;
        }

        // Check for AnyType fallback
        if (projectedReturnTypeName != null && projectedReturnTypeName.Contains("AnyType"))
        {
            skipDetails = $"Subscript return type resolved to AnyType ({projectedReturnTypeName}).";
            return SkipReason.AnyTypeFallback;
        }

        // Check index parameters
        foreach (var param in subscript.IndexParameters)
        {
            if (param.SwiftTypeSpec != null)
            {
                var paramTypeRecord = typeDatabase.GetTypeRecordOrAnyType(param.SwiftTypeSpec);
                if (paramTypeRecord.CSharpTypeName.FullyQualifiedName.Contains("AnyType"))
                {
                    skipDetails = $"Subscript index parameter resolved to AnyType.";
                    return SkipReason.AnyTypeFallback;
                }
            }
        }

        return null;
        #pragma warning restore CS0162
    }

    /// <summary>
    /// Returns true if the TypeSpec references a type from an unsupported module (SwiftUI, Combine, etc.)
    /// that is NOT registered in the type database.
    /// Delegates to <see cref="ValidationRuleSet.ReferencesUnsupportedModule"/> as the canonical implementation.
    /// </summary>
    internal static bool ReferencesUnsupportedModule(TypeSpec? typeSpec, ITypeDatabase? typeDatabase = null)
        => ValidationRuleSet.ReferencesUnsupportedModule(typeSpec, typeDatabase);

    /// <summary>
    /// Returns true if the TypeSpec contains an associated type reference (e.g., Self.Element, τ_0_0.ID).
    /// Delegates to <see cref="ValidationRuleSet.ContainsAssociatedTypeReference"/> as the canonical implementation.
    /// </summary>
    internal static bool ContainsAssociatedTypeReference(TypeSpec? typeSpec)
        => ValidationRuleSet.ContainsAssociatedTypeReference(typeSpec);

    /// <summary>
    /// Lightweight method emission check for the main HandleBaseDecl path.
    /// Only checks conditions that cause compilation errors and aren't handled by the
    /// downstream method handler's UnsupportedSwiftType fallback mechanism.
    /// For full validation (e.g., conformance checking), use CanEmitMethod instead.
    /// </summary>
    public static SkipReason? ShouldSkipMethodEmission(
        MethodDecl method,
        ITypeDatabase typeDatabase,
        out string? skipDetails)
    {
        skipDetails = null;

        // Prune synthesized Codable members — encode(to: Encoder) and init(from: Decoder)
        // are always unusable because Encoder/Decoder are unresolvable existential protocols.
        if (IsSynthesizedCodableMember(method))
        {
            skipDetails = "Synthesized Codable member (Encoder/Decoder are unresolvable existential protocols).";
            return SkipReason.SynthesizedCodable;
        }

        // B20: Skip methods/constructors with unsupported closure parameters.
        // P/Invoke emits AnyType for unsupported closures, but TypeProjectionFactory
        // projects them to Action<>/Func<> — wrapper body gets CS1503 type mismatch.
        // Must run BEFORE the constructor early-return so constructors are also covered.
        // Exception: generic closures eligible for the monomorphized bridge pattern are
        // allowed through — GenericClosureBridgeEmitter.TryEmit handles them in MethodHandler.
        var closureHandler = new ClosureHandler(typeDatabase);
        foreach (var arg in method.CSSignature.Skip(1)) // Skip return type (element 0)
        {
            if (closureHandler.IsClosure(arg))
            {
                var closureTypeSpec = closureHandler.GetClosureTypeSpec(arg);
                if (closureTypeSpec != null && !closureHandler.IsSupportedClosure(closureTypeSpec))
                {
                    // Check if this is a generic closure eligible for the bridge pattern.
                    // Also verify all non-closure params are IntPtr-compatible (classes, primitives)
                    // to avoid emitting methods the bridge can't fully handle.
                    if (ClosureHandler.HasGenericTypeParameters(closureTypeSpec) &&
                        closureHandler.IsMethodGenericClosureEligible(closureTypeSpec, method) &&
                        GenericClosureBridgeEmitter.AreNonClosureParamsCompatible(method, arg, typeDatabase))
                    {
                        // Allow through — GenericClosureBridgeEmitter will handle this
                        continue;
                    }

                    // Allow protocol extension methods with bridgeable closures through —
                    // ProtocolExtensionClosureBridge handles them in MethodHandler.
                    if (method.IsProtocolExtensionMethod &&
                        ProtocolExtensionEmitter.IsClosureBridgeable(closureTypeSpec, typeDatabase))
                    {
                        continue;
                    }

                    // Allow methods with bound-generic closure args through —
                    // MethodClosureBridge handles them in MethodHandler.
                    if (MethodClosureBridge.IsEligible(method, closureHandler, typeDatabase))
                    {
                        continue;
                    }

                    // Allow methods with nested closures eligible for NestedClosureBridge through
                    if (NestedClosureBridge.IsEligible(method, closureHandler, typeDatabase))
                    {
                        continue;
                    }

                    // Allow Optional<Closure> params with default values through —
                    // ExistentialBypassEmitter omits these, letting Swift fill nil.
                    if (closureHandler.IsOptionalClosure(arg.SwiftTypeSpec) && arg.HasDefaultArg)
                    {
                        continue;
                    }

                    skipDetails = $"Parameter '{arg.Name}' has unsupported closure type that cannot be marshalled.";
                    return SkipReason.UnsupportedClosure;
                }
            }
        }

        // B19: Skip methods whose return type or parameters reference SwiftUI/Combine types (unless registered in type database).
        // Must run BEFORE the constructor early-return so constructors with unsupported params are also skipped.
        foreach (var arg in method.CSSignature)
        {
            if (ReferencesUnsupportedModule(arg.SwiftTypeSpec, typeDatabase))
            {
                skipDetails = $"Method signature references unsupported module (SwiftUI/Combine) in '{arg.SwiftTypeSpec}'.";
                return SkipReason.SwiftUIConstraint;
            }
        }

        // Swift.UnsafeRawBufferPointer / UnsafeMutableRawBufferPointer parameters in synchronous,
        // nonescaping positions are supported by splitting into (ptr, len) at the @_cdecl boundary
        // and bridging to ReadOnlySpan<byte> / Span<byte> on the C# side. See CdeclParamMapper.Map
        // and src/docs/Design/unsafe-mutable-raw-buffer-pointer.md.
        //
        // Out-of-scope shapes for v1:
        //   - Return-position buffers (no fixed-block scope to pin under).
        //   - Async method parameters (the fixed block scopes only the synchronous P/Invoke start;
        //     the await would cross out of the pin, leaving Swift with a dangling pointer if it
        //     retained the address).
        //   - Escaping closure parameters are already filtered out earlier by
        //     MethodClosureBridge.IsSwiftPointerType, which excludes both buffer pointer variants
        //     from closure bridging.
        // Fail closed with SWIFTBIND104 so consumers see the warning at generate time rather than
        // a runtime crash.
        if (method.CSSignature.Count > 0
            && MarshallingHelpers.IsAnyUnsafeRawBufferPointer(method.CSSignature[0].SwiftTypeSpec))
        {
            var returnTypeName = ((NamedTypeSpec)method.CSSignature[0].SwiftTypeSpec!).Name;
            skipDetails = $"SWIFTBIND104: '{returnTypeName}' is not supported as a return type. " +
                          "v1 supports synchronous, nonescaping parameters only. " +
                          "See src/docs/Design/unsafe-mutable-raw-buffer-pointer.md.";
            return SkipReason.UnsupportedSignature;
        }
        if (method.IsAsync
            && method.CSSignature.Skip(1).FirstOrDefault(arg =>
                MarshallingHelpers.IsAnyUnsafeRawBufferPointer(arg.SwiftTypeSpec)) is { } asyncBufArg)
        {
            var bufTypeName = ((NamedTypeSpec)asyncBufArg.SwiftTypeSpec!).Name;
            skipDetails = $"SWIFTBIND104: '{bufTypeName}' is not supported as a parameter on async methods. " +
                          "v1 supports synchronous, nonescaping parameters only. " +
                          "See src/docs/Design/unsafe-mutable-raw-buffer-pointer.md.";
            return SkipReason.UnsupportedSignature;
        }

        // Reject methods whose return type or any parameter is a DIRECT existential
        // that IsSupportedExistential rejects — currently class-bounded compositions
        // like `any ClassA & ProtoP`. WrapperEmitter.EmitExistentialContainerMarshalling
        // would silently skip the container allocation for those args, leaving the body
        // to pass the raw managed reference into a P/Invoke that expects an ExistentialContainerN.
        // Skip the method instead of emitting an unsound signature mismatch.
        var directExistentialHandler = new ExistentialHandler(typeDatabase);
        foreach (var arg in method.CSSignature)
        {
            if (directExistentialHandler.IsExistential(arg.SwiftTypeSpec))
            {
                var protocolList = directExistentialHandler.ToProtocolListTypeSpec(arg.SwiftTypeSpec);
                if (protocolList != null && !directExistentialHandler.IsSupportedExistential(protocolList))
                {
                    skipDetails = $"Method has an unsupported direct existential on '{arg.Name}' (class-bounded composition or unsupported protocol count).";
                    return SkipReason.UnsupportedExistential;
                }
            }
            if (directExistentialHandler.IsOptionalExistential(arg.SwiftTypeSpec))
            {
                var innerProtocolList = directExistentialHandler.UnwrapOptionalExistential(arg.SwiftTypeSpec);
                if (innerProtocolList != null && !directExistentialHandler.IsSupportedExistential(innerProtocolList))
                {
                    skipDetails = $"Method has an unsupported Optional existential on '{arg.Name}' (class-bounded composition or unsupported protocol count).";
                    return SkipReason.UnsupportedExistential;
                }
            }
        }

        // Skip constructors (always allowed through for remaining checks)
        if (method.IsConstructor)
            return null;

        // NOTE: Non-simple enum method return types are now supported (B18 gate removed).

        // C6: Async methods with tuple returns containing non-simple enums
        // (flattened into [UnmanagedCallersOnly] callback — non-blittable CS8894)
        if (method.IsAsync && method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is TupleTypeSpec tupleReturn && !tupleReturn.IsEmptyTuple)
            {
                foreach (var element in tupleReturn.Elements)
                {
                    var elementToCheck = element;
                    if (element is NamedTypeSpec optionalElement &&
                        optionalElement.Name == "Swift.Optional" &&
                        optionalElement.GenericParameters.Count == 1)
                    {
                        elementToCheck = optionalElement.GenericParameters[0];
                    }

                    if (elementToCheck is NamedTypeSpec namedElement &&
                        namedElement.HasModule() && !namedElement.ContainsGenericParameters)
                    {
                        var elementSwiftName = SwiftTypeName.FromModuleQualifiedName(namedElement.Name);
                        if (typeDatabase.TryGetTypeRecord(elementSwiftName, out var elementRecord) &&
                            elementRecord.Kind == TypeRecordKind.Enum &&
                            !elementRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum))
                        {
                            skipDetails = $"Async tuple return contains non-simple enum '{namedElement.Name}' which is non-blittable in callback.";
                            return SkipReason.UnsupportedSignature;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the projected C# type for a property using the same resolution as PropertyHandler.
    /// </summary>
    public static string? GetProjectedPropertyType(
        PropertyDecl property,
        ITypeDatabase typeDatabase)
    {
        var skipReason = CanEmitProperty(property, typeDatabase, out _, out var projectedTypeName);
        if (skipReason != null)
            return null;
        return projectedTypeName;
    }

    /// <summary>
    /// Gets the projected C# return type for a method using the same resolution as MethodHandler.
    /// </summary>
    public static string? GetProjectedMethodReturnType(
        MethodDecl method,
        ITypeDatabase typeDatabase)
    {
        var skipReason = CanEmitMethod(method, typeDatabase, out _, out var projectedReturnTypeName);
        if (skipReason != null)
            return null;
        return projectedReturnTypeName;
    }

    /// <summary>
    /// Gets the projected C# return type for a subscript.
    /// </summary>
    public static string? GetProjectedSubscriptReturnType(
        SubscriptDecl subscript,
        ITypeDatabase typeDatabase)
    {
        var skipReason = CanEmitSubscript(subscript, typeDatabase, out _, out var projectedReturnTypeName);
        if (skipReason != null)
            return null;
        return projectedReturnTypeName;
    }

    /// <summary>
    /// Recursively checks whether a TypeSpec contains a tuple with unsupported elements
    /// (closures or types that resolve to AnyType). Also checks inside Optional/generic wrappers. (C1)
    /// </summary>
    private static bool ContainsUnsupportedTupleElement(TypeSpec? typeSpec, ITypeDatabase typeDatabase)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case TupleTypeSpec tuple when !tuple.IsEmptyTuple:
                foreach (var element in tuple.Elements)
                {
                    if (element is ClosureTypeSpec)
                        return true;
                    if (element is TupleTypeSpec)
                        return true;
                    if (element is NamedTypeSpec namedElement && namedElement.HasModule())
                    {
                        var record = typeDatabase.GetTypeRecordOrAnyType(namedElement);
                        if (record.CSharpTypeName.FullyQualifiedName.Contains("AnyType"))
                            return true;
                    }
                    if (ContainsUnsupportedTupleElement(element, typeDatabase))
                        return true;
                }
                return false;

            case NamedTypeSpec named when named.GenericParameters.Count > 0:
                // Check inside Optional<T> and other generic wrappers
                foreach (var genericParam in named.GenericParameters)
                {
                    if (ContainsUnsupportedTupleElement(genericParam, typeDatabase))
                        return true;
                }
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// Detects synthesized Codable members: encode(to: any Encoder) and init(from: any Decoder).
    /// These are always unusable because Encoder/Decoder are unresolvable existential protocols.
    /// </summary>
    private static bool IsSynthesizedCodableMember(MethodDecl method)
    {
        // encode(to: any Encoder) — always exactly 2 elements in CSSignature (return + encoder param)
        if (!method.IsConstructor && method.Name == "encode" &&
            method.CSSignature.Count == 2 &&
            HasEncoderDecoderParam(method.CSSignature[1], "Encoder"))
            return true;

        // init(from: any Decoder) — constructor with exactly 2 elements (return + decoder param)
        if (method.IsConstructor &&
            method.CSSignature.Count == 2 &&
            HasEncoderDecoderParam(method.CSSignature[1], "Decoder"))
            return true;

        return false;
    }

    /// <summary>
    /// Checks if an argument's Swift type spec references the given Encoder/Decoder protocol.
    /// </summary>
    private static bool HasEncoderDecoderParam(ArgumentDecl arg, string protocolName)
    {
        var typeStr = arg.SwiftTypeSpec?.ToString() ?? "";
        return typeStr.Contains($"Swift.{protocolName}");
    }

    // ==================== Synthesized Protocol Member Detection ====================

    /// <summary>
    /// Returns true for properties that are synthesized by Swift protocol conformance and
    /// should be suppressed in favor of .NET equivalents (e.g., hashValue → GetHashCode()).
    /// Only matches instance members on types conforming to the relevant protocol.
    /// </summary>
    public static bool IsSynthesizedProtocolProperty(PropertyDecl property, TypeDecl typeDecl)
    {
        if (property.IsStatic)
            return false;

        if (property.Name == "hashValue"
            && GetConformances(typeDecl).Any(c => c.Protocol.Name == "Hashable"))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true for methods that are synthesized by Swift protocol conformance and
    /// should be suppressed in favor of .NET equivalents (e.g., hash(into:) → GetHashCode()).
    /// Only matches instance methods on types conforming to the relevant protocol.
    /// </summary>
    public static bool IsSynthesizedProtocolMethod(MethodDecl method, TypeDecl typeDecl)
    {
        if (method.IsConstructor)
            return false;

        if (method.MethodType == MethodType.Static)
            return false;

        if (method.Name == "hash"
            && method.CSSignature.Skip(1).Any(a => a.Name == "into")
            && GetConformances(typeDecl).Any(c => c.Protocol.Name == "Hashable"))
            return true;

        return false;
    }

    /// <summary>
    /// Gets the conformances for a type declaration. Conformances are declared individually
    /// on ClassDecl, StructDecl, and EnumDecl (not on the base TypeDecl).
    /// </summary>
    private static IEnumerable<TypeConformance> GetConformances(TypeDecl typeDecl)
    {
        return typeDecl switch
        {
            ClassDecl c => c.Conformances,
            StructDecl s => s.Conformances,
            EnumDecl e => e.Conformances,
            _ => Enumerable.Empty<TypeConformance>()
        };
    }

    // ==================== Emittable Member Count (Opaque Type Pre-Scan) ====================

    /// <summary>
    /// Pre-scans a type's members to count how many are emittable vs skipped.
    /// Uses the same gates as actual emission to ensure accurate counts.
    /// Synthesized members (e.g., hashValue for Hashable) count as available (not skipped).
    /// </summary>
    public static (int emittable, int skipped) CountEmittableMembers(TypeDecl typeDecl, ITypeDatabase typeDatabase)
    {
        int emittable = 0;
        int skipped = 0;

        // Count properties
        foreach (var property in typeDecl.Properties)
        {
            if (IsSynthesizedProtocolProperty(property, typeDecl))
            {
                // Synthesized members count as available — functionality exists via .NET equivalent
                emittable++;
                continue;
            }

            var skipReason = CanEmitProperty(property, typeDatabase, out _, out _);
            if (skipReason == null)
                emittable++;
            else
                skipped++;
        }

        // Count methods (Methods is on the base TypeDecl, no switch needed)
        foreach (var method in typeDecl.Methods)
        {
            // Accessors are property getter/setter implementations, not standalone public methods.
            // Module-internal methods cannot be called from external consumers.
            // Both are excluded from public API shape, matching the emit paths in IHandler.cs
            // and EnumHandler.SimpleEnum.cs.
            if (method.IsAccessor || method.IsModuleInternal)
                continue;

            if (IsSynthesizedProtocolMethod(method, typeDecl))
            {
                emittable++;
                continue;
            }

            if (method.IsConstructor)
            {
                emittable++;
                continue;
            }

            var methodSkipReason = ShouldSkipMethodEmission(method, typeDatabase, out _);
            if (methodSkipReason == null)
                emittable++;
            else
                skipped++;
        }

        return (emittable, skipped);
    }

    /// <summary>
    /// Apple framework types that are static classes in .NET iOS.
    /// In Swift these are typically RawRepresentable structs (NSString-backed),
    /// but .NET iOS maps them to static classes with string constants.
    /// Static classes cannot be used as variables, parameters, return types,
    /// or generic type arguments (CS0718/CS0723).
    /// Delegates to <see cref="ValidationRuleSet.IsNetStaticClassType"/> as the canonical implementation.
    /// </summary>
    internal static bool IsNetStaticClassType(string moduleQualifiedName)
        => ValidationRuleSet.IsNetStaticClassType(moduleQualifiedName);
}
