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
    /// Checks if a property can be emitted. Returns null if valid, SkipReason if not.
    /// </summary>
    public static SkipReason? CanEmitProperty(
        PropertyDecl property,
        ITypeDatabase typeDatabase,
        out string? skipDetails,
        out string? projectedTypeName)
    {
        skipDetails = null;
        projectedTypeName = null;

        var asyncStreamHandler = new AsyncStreamHandler(typeDatabase);
        var existentialHandler = new ExistentialHandler(typeDatabase);
        var closureHandler = new ClosureHandler(typeDatabase);
        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        // B19: Skip properties referencing SwiftUI/Combine types
        if (ReferencesUnsupportedModule(property.SwiftTypeSpec))
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
            if (!closureHandler.CanInvokeFromCSharp(closureTypeSpec))
            {
                skipDetails = "Closure parameters are not invokable from C#.";
                return SkipReason.UnsupportedClosure;
            }
            // C12: Closure return types that map to void* in function pointers can't be marshalled
            // back to managed types by the invoker. Only primitive types (and void) are safe.
            if (!closureTypeSpec.ReturnType.IsEmptyTuple)
            {
                var returnPInvokeType = closureHandler.TranslateTypeSpecToPInvokeType(closureTypeSpec.ReturnType);
                if (returnPInvokeType == "void*")
                {
                    skipDetails = "Closure return type requires marshalling not supported by the closure invoker.";
                    return SkipReason.UnsupportedClosure;
                }
            }
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
                    skipDetails = $"Bound generic contains existential type argument '{existentialType}'.";
                    return SkipReason.UnsupportedExistential;
                }

                var boundGenericContext = property.ParentDecl is TypeDecl boundParentType && boundParentType.IsGeneric
                    ? GenericContext.FromType(boundParentType)
                    : GenericContext.Empty;
                projectedTypeName = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property, boundGenericContext);
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
                projectedTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;
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

        // Check for non-simple enum return types that require memory management
        // Non-simple enums don't emit a .Buffer nested struct, so PInvokeEmitter's
        // RequiresMemoryManagement path would emit invalid `.Buffer` suffix (B18)
        // C8: Also check inside Optional<Enum> — unwrap Swift.Optional to inspect inner type
        if (IsNonSimpleEnumWithMemoryManagement(typeRecord, property.SwiftTypeSpec, typeDatabase))
        {
            skipDetails = "Non-simple enum with memory management has no .Buffer type for marshalling.";
            return SkipReason.UnsupportedSignature;
        }

        // Check for async properties
        if (property.Accessors.Any(a => a.Method.IsAsync))
        {
            skipDetails = "Property has async getter/setter.";
            return SkipReason.AsyncProperty;
        }

        // Accessor preflight: Check each accessor method can be emitted
        // This mirrors PropertyHandler.Emit lines 261-323
        foreach (var accessor in property.Accessors)
        {
            var accessorMethod = accessor.Method;

            // Mark as accessor before preflight - mirrors PropertyHandler.cs:269
            // This affects type conversion behavior (e.g., native remapping is skipped for accessors)
            accessorMethod.IsAccessor = true;

            // Check generic protocol constraints on accessor
            if (accessorMethod.IsGeneric)
            {
                foreach (var param in accessorMethod.GenericParameters)
                {
                    foreach (var conformance in param.GenericConformances)
                    {
                        if (conformance.Kind == ConformanceKind.Protocol &&
                            typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record) &&
                            record.Kind == TypeRecordKind.Protocol &&
                            record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                        {
                            skipDetails = $"Accessor '{accessorMethod.Name}' has constraints on protocols with associated types.";
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

        // Skip constructors for this check
        if (method.IsConstructor)
            return null;

        // B19: Skip methods whose return type or parameters reference SwiftUI/Combine types
        foreach (var arg in method.CSSignature)
        {
            if (ReferencesUnsupportedModule(arg.SwiftTypeSpec))
            {
                skipDetails = $"Method signature references unsupported module (SwiftUI/Combine) in '{arg.SwiftTypeSpec}'.";
                return SkipReason.SwiftUIConstraint;
            }
        }

        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);

        // Check for protocol constraints with associated types
        if (method.IsGeneric)
        {
            foreach (var param in method.GenericParameters)
            {
                foreach (var conformance in param.GenericConformances)
                {
                    if (conformance.Kind == ConformanceKind.Protocol)
                    {
                        if (typeDatabase.TryGetTypeRecord(conformance.ConformanceTarget, out var record))
                        {
                            if (record.Kind == TypeRecordKind.Protocol &&
                                record.Flags.HasFlag(TypeRecordFlags.HasAssociatedTypes))
                            {
                                skipDetails = "Method has constraints on protocols with associated types.";
                                return SkipReason.GenericProtocolConstraint;
                            }
                        }
                    }
                }
            }
        }

        // Check bound generic arguments for issues
        foreach (var argument in method.CSSignature)
        {
            if (boundGenericsHandler.HasBareGenericUsage(argument.SwiftTypeSpec, method.ModuleDecl))
            {
                skipDetails = $"Type '{argument.SwiftTypeSpec}' contains generic declaration used without type arguments.";
                return SkipReason.UnsupportedSignature;
            }

            if (!boundGenericsHandler.IsBoundGeneric(argument))
                continue;

            if (boundGenericsHandler.HasNonSwiftObjectGenericArg(argument.SwiftTypeSpec))
            {
                skipDetails = "Bound generic contains type argument that cannot satisfy C# ISwiftObject constraint.";
                return SkipReason.UnsatisfiedGenericConstraint;
            }

            if (boundGenericsHandler.TryGetFirstUnsatisfiedConstraint(argument.SwiftTypeSpec, method, out var constraintDetails))
            {
                skipDetails = constraintDetails;
                return SkipReason.UnsatisfiedGenericConstraint;
            }

            // B6: Catch existentials in non-Array bound generics (Dictionary, Set, etc.).
            // Allow through ONLY if the outermost bound generic is Array AND the element
            // type is itself an existential (NamedTypeSpec.IsAny or ProtocolListTypeSpec).
            // WrapperEmitter.Marshalling has dedicated existential handling for direct
            // Array<any Protocol> elements. All other shapes (nested existentials in
            // Dictionary, closures/tuples containing existentials, etc.) break.
            // This matches the same check in MethodHandler.Emit() to keep validator and emitter consistent.
            if (boundGenericsHandler.TryGetFirstExistentialTypeArgument(argument.SwiftTypeSpec, out var existentialType))
            {
                var outerNamedType = argument.SwiftTypeSpec as NamedTypeSpec;
                var arrayChecker = new TypeConversionHandler(typeDatabase);
                var existentialChecker = new ExistentialHandler(typeDatabase);
                bool isArrayWithDirectExistentialElement = outerNamedType != null &&
                    arrayChecker.IsSwiftArray(outerNamedType) &&
                    outerNamedType.GenericParameters.Count > 0 &&
                    existentialChecker.IsExistential(outerNamedType.GenericParameters[0]);
                if (!isArrayWithDirectExistentialElement)
                {
                    skipDetails = $"Bound generic contains existential type argument '{existentialType}'.";
                    return SkipReason.UnsupportedExistential;
                }
            }
        }

        // Check for non-simple enum return types that require memory management (B18)
        // C8: Also check inside Optional<Enum> — unwrap Swift.Optional to inspect inner type
        // Only applies to synchronous methods — async methods use callback-based return, not .Buffer
        if (!method.IsAsync && method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec { IsEmptyTuple: true })
            {
                // Check the type directly and also unwrap Optional
                var typeSpecToCheck = returnArg.SwiftTypeSpec;
                if (typeSpecToCheck is NamedTypeSpec optionalType &&
                    optionalType.Name == "Swift.Optional" &&
                    optionalType.GenericParameters.Count == 1)
                {
                    typeSpecToCheck = optionalType.GenericParameters[0];
                }

                if (typeSpecToCheck is NamedTypeSpec returnNamedType &&
                    returnNamedType.HasModule() && !returnNamedType.ContainsGenericParameters)
                {
                    var returnSwiftName = SwiftTypeName.FromModuleQualifiedName(returnNamedType.Name);
                    if (typeDatabase.TryGetTypeRecord(returnSwiftName, out var returnTypeRecord) &&
                        returnTypeRecord.Kind == TypeRecordKind.Enum &&
                        !returnTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum) &&
                        MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord))
                    {
                        skipDetails = "Non-simple enum return with memory management has no .Buffer type for marshalling.";
                        return SkipReason.UnsupportedSignature;
                    }
                }
            }
        }

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
        var typeConversionHandler = new TypeConversionHandler(typeDatabase);
        var existentialHandler = new ExistentialHandler(typeDatabase);

        if (method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
            {
                // Check idiomatic type conversions first (SwiftString -> string, etc.)
                var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(returnArg.SwiftTypeSpec, isParameter: false);
                if (idiomaticType != null)
                {
                    projectedReturnTypeName = idiomaticType;
                }
                // Handle existential return types (any Protocol) - mirrors MethodSignature:238-252
                else if (existentialHandler.IsExistential(returnArg.SwiftTypeSpec))
                {
                    var protocolList = existentialHandler.ToProtocolListTypeSpec(returnArg.SwiftTypeSpec);
                    if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                    {
                        projectedReturnTypeName = existentialHandler.GetPublicExistentialType(protocolList);
                    }
                    else
                    {
                        projectedReturnTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                    }
                }
                // Handle Optional-wrapped existential return types - mirrors MethodSignature:254-268
                else if (existentialHandler.IsOptionalExistential(returnArg.SwiftTypeSpec))
                {
                    var innerProtocolList = existentialHandler.UnwrapOptionalExistential(returnArg.SwiftTypeSpec);
                    if (innerProtocolList != null && existentialHandler.IsSupportedExistential(innerProtocolList))
                    {
                        projectedReturnTypeName = existentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
                    }
                    else
                    {
                        projectedReturnTypeName = TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
                    }
                }
                // Apply native type remapping (Foundation.URL -> NSUrl, Foundation.Data -> NSData)
                // This mirrors MethodSignature.HandleReturnType lines 270-280
                else if (typeConversionHandler.HasNativeTypeRemapping(returnArg.SwiftTypeSpec))
                {
                    projectedReturnTypeName = typeConversionHandler.GetNativeTypeName(returnArg.SwiftTypeSpec)
                        ?? typeDatabase.GetTypeRecordOrAnyType(returnArg.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
                }
                else
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(returnArg.SwiftTypeSpec);
                    // Protocol types (interfaces) project to AnyType - mirrors MethodSignature:283-288
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
            if (subscript.ReturnTypeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
            {
                // Bound generic subscript return - create temp property to check
                var tempProperty = new PropertyDecl
                {
                    Name = "_temp",
                    SwiftTypeSpec = subscript.ReturnTypeSpec,
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                };
                projectedReturnTypeName = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
            }
            else
            {
                var typeRecord = typeDatabase.GetTypeRecordOrAnyType(subscript.ReturnTypeSpec);
                projectedReturnTypeName = typeRecord.CSharpTypeName.FullyQualifiedName;
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
    /// Returns true if the TypeSpec references a type from an unsupported module (SwiftUI, Combine).
    /// Recursively checks generic parameters, tuple elements, and closure args/return.
    /// </summary>
    private static bool ReferencesUnsupportedModule(TypeSpec? typeSpec)
    {
        if (typeSpec == null)
            return false;

        switch (typeSpec)
        {
            case NamedTypeSpec namedType:
                if (namedType.HasModule() && GenericTypeEmitter.IsUnsupportedModule(namedType.Module))
                    return true;
                foreach (var genericParam in namedType.GenericParameters)
                {
                    if (ReferencesUnsupportedModule(genericParam))
                        return true;
                }
                return false;

            case TupleTypeSpec tupleType:
                foreach (var element in tupleType.Elements)
                {
                    if (ReferencesUnsupportedModule(element))
                        return true;
                }
                return false;

            case ClosureTypeSpec closureType:
                if (ReferencesUnsupportedModule(closureType.Arguments))
                    return true;
                if (ReferencesUnsupportedModule(closureType.ReturnType))
                    return true;
                return false;

            case ProtocolListTypeSpec protocolList:
                foreach (var protocol in protocolList.Protocols.Keys)
                {
                    if (ReferencesUnsupportedModule(protocol))
                        return true;
                }
                return false;

            default:
                return false;
        }
    }

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

        // Skip constructors (always allowed through)
        if (method.IsConstructor)
            return null;

        // B19: Skip methods whose return type or parameters reference SwiftUI/Combine types
        foreach (var arg in method.CSSignature)
        {
            if (ReferencesUnsupportedModule(arg.SwiftTypeSpec))
            {
                skipDetails = $"Method signature references unsupported module (SwiftUI/Combine) in '{arg.SwiftTypeSpec}'.";
                return SkipReason.SwiftUIConstraint;
            }
        }

        // B18: Non-simple enum return with .Buffer suffix (sync only — async uses callbacks)
        // C8: Also check inside Optional<Enum>
        if (!method.IsAsync && method.CSSignature.Count > 0)
        {
            var returnArg = method.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec { IsEmptyTuple: true })
            {
                var typeSpecToCheck = returnArg.SwiftTypeSpec;
                if (typeSpecToCheck is NamedTypeSpec optionalType &&
                    optionalType.Name == "Swift.Optional" &&
                    optionalType.GenericParameters.Count == 1)
                {
                    typeSpecToCheck = optionalType.GenericParameters[0];
                }

                if (typeSpecToCheck is NamedTypeSpec returnNamedType &&
                    returnNamedType.HasModule() && !returnNamedType.ContainsGenericParameters)
                {
                    var returnSwiftName = SwiftTypeName.FromModuleQualifiedName(returnNamedType.Name);
                    if (typeDatabase.TryGetTypeRecord(returnSwiftName, out var returnTypeRecord) &&
                        returnTypeRecord.Kind == TypeRecordKind.Enum &&
                        !returnTypeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum) &&
                        MarshallingHelpers.RequiresMemoryManagement(returnTypeRecord))
                    {
                        skipDetails = "Non-simple enum return with memory management has no .Buffer type for marshalling.";
                        return SkipReason.UnsupportedSignature;
                    }
                }
            }
        }

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
    /// Checks if a type (or the inner type if Optional-wrapped) is a non-simple enum
    /// with memory management. Such enums don't emit .Buffer and cause B18 errors. (C8)
    /// </summary>
    private static bool IsNonSimpleEnumWithMemoryManagement(
        TypeRecord? typeRecord, TypeSpec typeSpec, ITypeDatabase typeDatabase)
    {
        // Direct check
        if (typeRecord != null && typeRecord.Kind == TypeRecordKind.Enum &&
            !typeRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum) &&
            MarshallingHelpers.RequiresMemoryManagement(typeRecord))
        {
            return true;
        }

        // C8: Unwrap Swift.Optional and check inner type
        if (typeSpec is NamedTypeSpec optionalType &&
            optionalType.Name == "Swift.Optional" &&
            optionalType.GenericParameters.Count == 1 &&
            optionalType.GenericParameters[0] is NamedTypeSpec innerType &&
            innerType.HasModule() && !innerType.ContainsGenericParameters)
        {
            var innerSwiftName = SwiftTypeName.FromModuleQualifiedName(innerType.Name);
            if (typeDatabase.TryGetTypeRecord(innerSwiftName, out var innerRecord) &&
                innerRecord.Kind == TypeRecordKind.Enum &&
                !innerRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum) &&
                MarshallingHelpers.RequiresMemoryManagement(innerRecord))
            {
                return true;
            }
        }

        return false;
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
}
