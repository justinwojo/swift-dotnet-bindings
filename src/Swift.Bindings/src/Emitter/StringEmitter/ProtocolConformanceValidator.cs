// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Validates that a concrete type can fully implement a protocol interface.
/// Used to prevent CS0535 (missing interface members) and CS0738 (return type mismatch) errors.
/// </summary>
public class ProtocolConformanceValidator
{
    private readonly ModuleDecl _moduleDecl;
    private readonly ITypeDatabase _typeDatabase;

    public ProtocolConformanceValidator(ModuleDecl moduleDecl, ITypeDatabase typeDatabase)
    {
        _moduleDecl = moduleDecl;
        _typeDatabase = typeDatabase;
    }

    /// <summary>
    /// Looks up a protocol by name in the module.
    /// Supports both simple names ("ImageDecoding") and module-qualified names ("Nuke.ImageDecoding").
    /// Prefers module-qualified matches to avoid ambiguity with same-name protocols.
    /// Returns null for cross-module protocols (e.g., Swift.Equatable).
    /// </summary>
    public ProtocolDecl? FindProtocol(string protocolName)
    {
        // If input contains a dot, it's likely module-qualified - try that first for precision
        if (protocolName.Contains('.'))
        {
            // Try exact module-qualified match first (most precise)
            var qualifiedResult = _moduleDecl.Protocols.FirstOrDefault(p =>
                p.SwiftTypeName?.ModuleQualifiedName == protocolName);
            if (qualifiedResult != null)
                return qualifiedResult;

            // Extract simple name and try that (fallback for cross-module references)
            var lastDot = protocolName.LastIndexOf('.');
            var simpleName = protocolName.Substring(lastDot + 1);

            // If multiple protocols have same simple name, prefer one from our module
            var candidates = _moduleDecl.Protocols.Where(p => p.Name == simpleName).ToList();
            if (candidates.Count == 1)
                return candidates[0];
            if (candidates.Count > 1)
            {
                // Prefer protocol whose module matches the prefix
                var modulePrefix = protocolName.Substring(0, lastDot);
                var moduleMatch = candidates.FirstOrDefault(p =>
                    p.SwiftTypeName?.Module == modulePrefix);
                return moduleMatch ?? candidates[0]; // Fall back to first if no module match
            }

            return null;
        }

        // Simple name lookup - only one protocol should match
        var result = _moduleDecl.Protocols.FirstOrDefault(p => p.Name == protocolName);
        return result;
    }

    /// <summary>
    /// Checks if a CONCRETE TYPE can fully implement a protocol interface.
    /// Validates the TYPE'S MEMBERS (not protocol requirements) against interface.
    /// </summary>
    /// <param name="concreteType">The actual type (e.g., ImageDecoders.Empty)</param>
    /// <param name="protocolDecl">The protocol it claims to implement</param>
    /// <param name="visited">Cycle protection - tracks visited protocols</param>
    /// <returns>True if the type can fully implement the protocol</returns>
    public bool CanFullyImplementProtocol(
        TypeDecl concreteType,
        ProtocolDecl protocolDecl,
        HashSet<string>? visited = null)
    {
        // Cycle protection with module-qualified name
        visited ??= new HashSet<string>();
        var qualifiedName = protocolDecl.SwiftTypeName?.ModuleQualifiedName
                         ?? $"{protocolDecl.ModuleDecl?.Name ?? "Unknown"}.{protocolDecl.Name}";
        if (!visited.Add(qualifiedName))
            return true;

        // Resolve the concrete type's C# name for TSelf-aware type matching
        string? conformingTypeName = null;
        if (protocolDecl.HasSelfRequirement && concreteType.SwiftTypeName != null &&
            _typeDatabase.TryGetTypeRecord(concreteType.SwiftTypeName, out var concreteRecord))
        {
            conformingTypeName = concreteRecord.CSharpTypeName.FullyQualifiedName;
        }

        // Track interface requirements (mirrors ProtocolHandler dedup)
        var requiredProperties = new HashSet<string>();
        var requiredSubscripts = new HashSet<string>();
        var requiredMethods = new HashSet<string>();

        // For each INTERFACE PROPERTY requirement:
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        foreach (var protoProperty in protocolDecl.Properties)
        {
            if (protoProperty.IsStatic) continue;
            var propertyKey = protoProperty.Name;
            if (!requiredProperties.Add(propertyKey)) continue;

            // Skip properties that won't appear in the interface (mirrors ProtocolHandler gates)
            if (IsPropertySkippedFromInterface(protoProperty, boundGenericsHandler, protocolDecl))
                continue;

            // Find matching property in CONCRETE TYPE
            var concreteProperty = FindMatchingProperty(concreteType, protoProperty);
            if (concreteProperty == null)
                return false;  // CS0535: member not found

            // Validate accessor contract: protocol { get set } requires concrete { get set }
            var protoHasGetter = protoProperty.Accessors.OfType<GetAccessorDecl>().Any();
            var protoHasSetter = protoProperty.Accessors.OfType<SetAccessorDecl>().Any();
            var concreteHasGetter = concreteProperty.Accessors.OfType<GetAccessorDecl>().Any();
            var concreteHasSetter = concreteProperty.Accessors.OfType<SetAccessorDecl>().Any();
            if ((protoHasGetter && !concreteHasGetter) || (protoHasSetter && !concreteHasSetter))
                return false;  // CS0535: missing accessor

            // Validate CONCRETE property can be emitted
            var skipReason = MemberEmissionValidator.CanEmitProperty(
                concreteProperty, _typeDatabase, out _, out var concreteTypeProjected);
            if (skipReason != null)
                return false;  // CS0535: member will be skipped

            // Check type compatibility (CS0738)
            var interfaceType = GetInterfacePropertyType(protoProperty, protocolDecl, boundGenericsHandler);
            if (!AreTypesCompatible(interfaceType, concreteTypeProjected, conformingTypeName))
                return false;  // CS0738: types don't match
        }

        // For each INTERFACE SUBSCRIPT requirement:
        foreach (var protoSubscript in protocolDecl.Subscripts)
        {
            if (protoSubscript.IsStatic) continue;
            var subscriptKey = ProtocolSignatureHelper.GetSubscriptSignatureKey(protoSubscript, _typeDatabase, protocolDecl);
            if (!requiredSubscripts.Add(subscriptKey)) continue;

            // Find matching subscript in CONCRETE TYPE
            var concreteSubscript = FindMatchingSubscript(concreteType, protoSubscript, protocolDecl);
            if (concreteSubscript == null)
                return false;

            // Validate accessor contract for subscript
            var protoHasGetter = protoSubscript.HasGetter;
            var protoHasSetter = protoSubscript.HasSetter;
            var concreteHasGetter = concreteSubscript.HasGetter;
            var concreteHasSetter = concreteSubscript.HasSetter;
            if ((protoHasGetter && !concreteHasGetter) || (protoHasSetter && !concreteHasSetter))
                return false;

            var skipReason = MemberEmissionValidator.CanEmitSubscript(
                concreteSubscript, _typeDatabase, out _, out var concreteReturnType);
            if (skipReason != null)
                return false;

            // Check return type compatibility (CS0738)
            var interfaceReturnType = GetInterfaceSubscriptReturnType(protoSubscript, protocolDecl, boundGenericsHandler);
            if (!AreTypesCompatible(interfaceReturnType, concreteReturnType, conformingTypeName))
                return false;
        }

        // For each INTERFACE METHOD requirement:
        var emittedCSharpKeys = new HashSet<string>();
        var emittedResolvedSignatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var protoMethod in protocolDecl.Methods)
        {
            if (protoMethod.IsConstructor || protoMethod.MethodType == MethodType.Static) continue;
            var methodKey = ProtocolSignatureHelper.GetMethodSignatureKey(protoMethod, _typeDatabase, protocolDecl);
            if (!requiredMethods.Add(methodKey)) continue;

            // Skip methods that won't appear in the interface (mirrors ProtocolHandler gates)
            if (IsMethodSkippedFromInterface(protoMethod, boundGenericsHandler, protocolDecl))
                continue;

            var projectedKey = ProtocolSignatureHelper.GetProjectedCSharpMethodKey(protoMethod, _typeDatabase, protocolDecl);
            if (!emittedCSharpKeys.Add(projectedKey))
                continue;

            var resolvedSignature = BuildInterfaceMethodSignature(protoMethod, protocolDecl);
            if (!emittedResolvedSignatures.Add(resolvedSignature))
                continue;

            // Find matching method in CONCRETE TYPE
            var concreteMethod = FindMatchingMethod(concreteType, protoMethod, protocolDecl);
            if (concreteMethod == null)
                return false;

            var skipReason = MemberEmissionValidator.CanEmitMethod(
                concreteMethod, _typeDatabase, out _, out var concreteReturnType);
            if (skipReason != null)
                return false;

            // Check C# name parity: the concrete type's method is emitted via GetPublicMethodName
            // with the concrete type's property names. If a property collision causes a "Method"
            // suffix, the emitted name won't match the interface member name → CS0535.
            // GetPublicMethodName accounts for Get prefix (noun-only + return value), Async suffix,
            // and property collision — so we must use it, not just ToPascalCase.
            var concreteProperties = concreteType switch
            {
                ClassDecl cd => cd.Properties,
                StructDecl sd => sd.Properties,
                EnumDecl ed => ed.Properties,
                _ => Enumerable.Empty<PropertyDecl>()
            };
            var concretePropertyNames = new HashSet<string>(
                concreteProperties.Select(p => NameProvider.GetPropertyName(p.Name)));
            var concreteReturnTypeSpec = concreteMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool concreteHasReturn = concreteReturnTypeSpec != null && !concreteReturnTypeSpec.IsEmptyTuple;
            var concreteIsSelfReturning = MethodEnvironment.IsSelfReturningMethod(concreteMethod);
            var concreteParentTypeName = NameProvider.ToPascalCase(concreteType.Name);
            var concreteEmittedName = NameProvider.GetPublicMethodName(
                concreteMethod.Name, concreteMethod.IsAsync,
                hasReturnValue: concreteHasReturn,
                propertyNames: concretePropertyNames,
                isSelfReturning: concreteIsSelfReturning,
                parentTypeName: concreteParentTypeName,
                parameterCount: concreteMethod.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

            // Compare with the interface method name (computed without property collision context)
            var protoReturnTypeSpec = protoMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
            bool protoHasReturn = protoReturnTypeSpec != null && !protoReturnTypeSpec.IsEmptyTuple;
            var protoIsSelfReturning = MethodEnvironment.IsSelfReturningMethod(protoMethod);
            var interfaceMethodName = NameProvider.GetPublicMethodName(
                protoMethod.Name, protoMethod.IsAsync,
                hasReturnValue: protoHasReturn,
                isSelfReturning: protoIsSelfReturning,
                parameterCount: protoMethod.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

            if (concreteEmittedName != interfaceMethodName)
                return false;  // CS0535: method names diverge due to collision resolution

            // Check return type compatibility (CS0738)
            var interfaceReturnType = GetInterfaceMethodReturnType(protoMethod, protocolDecl, boundGenericsHandler);
            if (!AreTypesCompatible(interfaceReturnType, concreteReturnType, conformingTypeName))
                return false;
        }

        // Recursively check inherited protocols
        foreach (var inheritedProto in protocolDecl.InheritedProtocols)
        {
            if (inheritedProto.Name == "AnyObject") continue;

            var inheritedDecl = FindProtocol(inheritedProto.NameWithoutModule);
            if (inheritedDecl != null)
            {
                if (!CanFullyImplementProtocol(concreteType, inheritedDecl, visited))
                    return false;
            }
            // Note: Cross-module protocols (e.g., Swift.Equatable) have no local ProtocolDecl
            // and are handled separately by ShouldEmitConformance
        }

        return true;
    }

    /// <summary>
    /// Gets interface property type using SAME projection as ProtocolHandler.EmitInterfaceProperty.
    /// </summary>
    private string GetInterfacePropertyType(PropertyDecl protoProperty, ProtocolDecl protocolContext, BoundGenericsHandler boundGenericsHandler)
    {
        if (protoProperty.SwiftTypeSpec is AssociatedTypeReferenceSpec)
            return "?";  // PAT - should have been filtered earlier

        // Use factory with GenericContext for all types including bound generics
        // For Self-requirement protocols, map τ_0_0 → TSelf
        var genericContext = protocolContext.HasSelfRequirement
            ? GenericContext.ForProtocolSelf()
            : GenericContext.Empty;

        var factory = new TypeProjectionFactory();
        var projection = factory.Project(protoProperty.SwiftTypeSpec, new ProjectionContext
        {
            TypeDatabase = _typeDatabase,
            IsParameter = false,
            GenericContext = genericContext
        });
        if (projection != null)
            return projection.PublicType;

        // Bound generic fallback: produce full type name with generic args
        if (protoProperty.SwiftTypeSpec is NamedTypeSpec propBoundGeneric && propBoundGeneric.ContainsGenericParameters)
        {
            return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(protoProperty.SwiftTypeSpec, genericContext);
        }

        return _typeDatabase.GetTypeRecordOrAnyType(protoProperty.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Gets interface method return type using SAME projection as ProtocolHandler.EmitInterfaceMethod.
    /// </summary>
    private string GetInterfaceMethodReturnType(MethodDecl protoMethod, ProtocolDecl protocolContext, BoundGenericsHandler boundGenericsHandler)
    {
        var returnType = "void";

        if (protoMethod.CSSignature.Count > 0)
        {
            var returnArg = protoMethod.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
            {
                // Try factory-based projection with GenericContext
                // For Self-requirement protocols, map τ_0_0 → TSelf
                var genericContext = protocolContext.HasSelfRequirement
                    ? GenericContext.ForProtocolSelf()
                    : GenericContext.Empty;

                var methodFactory = new TypeProjectionFactory();
                var methodProjection = methodFactory.Project(returnArg.SwiftTypeSpec, new ProjectionContext
                {
                    TypeDatabase = _typeDatabase,
                    IsParameter = false,
                    GenericContext = genericContext
                });
                if (methodProjection != null)
                {
                    returnType = methodProjection.PublicType;
                }
                else if (returnArg.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                {
                    returnType = ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
                }
                else if (returnArg.SwiftTypeSpec is NamedTypeSpec retBoundGeneric && retBoundGeneric.ContainsGenericParameters)
                {
                    returnType = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(returnArg.SwiftTypeSpec, genericContext);
                }
                else
                {
                    returnType = _typeDatabase.GetTypeRecordOrAnyType(returnArg.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
                }
            }
        }

        // Handle async methods
        if (protoMethod.IsAsync)
        {
            if (returnType == "void")
                returnType = "Task";
            else
                returnType = $"Task<{returnType}>";
        }

        return returnType;
    }

    private string BuildInterfaceMethodSignature(MethodDecl protoMethod, ProtocolDecl protocolContext)
    {
        var returnTypeSpec = protoMethod.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
        var isSelfReturning = MethodEnvironment.IsSelfReturningMethod(protoMethod);
        var methodName = NameProvider.GetPublicMethodName(protoMethod.Name, protoMethod.IsAsync, hasReturnValue: hasReturnValue, isSelfReturning: isSelfReturning,
            parameterCount: protoMethod.CSSignature.Skip(1).Count(a => !DefaultParameterOverloadEmitter.IsDebugParameter(a) && !a.SwiftTypeSpec.IsEmptyTuple));

        var parameterTypes = new List<string>();
        for (int i = 1; i < protoMethod.CSSignature.Count; i++)
        {
            var arg = protoMethod.CSSignature[i];
            // Skip debug params and empty tuple () params (zero-sized Void) — must match ProtocolHandler emission
            if (DefaultParameterOverloadEmitter.IsDebugParameter(arg))
                continue;
            if (arg.SwiftTypeSpec.IsEmptyTuple)
                continue;
            var projected = ResolveInterfaceMethodTypeName(arg.SwiftTypeSpec, isParameter: true, protocolContext);
            parameterTypes.Add(ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(projected, arg.SwiftTypeSpec, _typeDatabase));
        }

        // Add CancellationToken to async method signatures (matches ProtocolHandler interface emission)
        if (protoMethod.IsAsync)
        {
            parameterTypes.Add("System.Threading.CancellationToken");
        }

        return $"{methodName}({string.Join(",", parameterTypes)})";
    }

    private string ResolveInterfaceMethodTypeName(TypeSpec swiftTypeSpec, bool isParameter, ProtocolDecl protocolContext)
    {
        return ProtocolSignatureHelper.ProjectTypeToCSharp(swiftTypeSpec, _typeDatabase, protocolContext, isParameter);
    }

    /// <summary>
    /// Gets interface subscript return type using SAME projection as ProtocolHandler.EmitInterfaceSubscript.
    /// </summary>
    private string GetInterfaceSubscriptReturnType(SubscriptDecl protoSubscript, ProtocolDecl protocolContext, BoundGenericsHandler boundGenericsHandler)
    {
        // Factory-based projection with GenericContext
        // For Self-requirement protocols, map τ_0_0 → TSelf
        var genericContext = protocolContext.HasSelfRequirement
            ? GenericContext.ForProtocolSelf()
            : GenericContext.Empty;

        var subscriptFactory = new TypeProjectionFactory();
        var subscriptProjection = subscriptFactory.Project(protoSubscript.ReturnTypeSpec, new ProjectionContext
        {
            TypeDatabase = _typeDatabase,
            IsParameter = false,
            GenericContext = genericContext
        });
        if (subscriptProjection != null)
            return subscriptProjection.PublicType;

        if (protoSubscript.ReturnTypeSpec is AssociatedTypeReferenceSpec assocRef)
            return ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);

        // Bound generic fallback
        if (protoSubscript.ReturnTypeSpec is NamedTypeSpec subBoundGeneric && subBoundGeneric.ContainsGenericParameters)
        {
            return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(protoSubscript.ReturnTypeSpec, genericContext);
        }

        return _typeDatabase.GetTypeRecordOrAnyType(protoSubscript.ReturnTypeSpec).CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Yields the type itself, then walks the ResolvedSuperclass chain for class types.
    /// Stops at the first non-emittable ancestor (one with unsupported generic constraints),
    /// because flat emission means there is no valid C# inheritance chain beyond that point.
    /// For non-class types (structs, enums), yields only the type itself.
    /// </summary>
    internal static IEnumerable<TypeDecl> GetEmittableAncestors(TypeDecl type)
    {
        yield return type;

        if (type is not ClassDecl classDecl)
            yield break;

        var current = classDecl;
        while (current.HasResolvedSuperclass)
        {
            var ancestor = current.ResolvedSuperclass!;
            if (GenericTypeEmitter.TryGetUnsupportedConstraint(ancestor, out _))
                yield break; // Stop — can't see past a non-emittable ancestor
            yield return ancestor;
            current = ancestor;
        }
    }

    /// <summary>
    /// Finds matching property in concrete type or its emittable ancestors by name.
    /// </summary>
    private static PropertyDecl? FindMatchingProperty(TypeDecl type, PropertyDecl protoProperty)
    {
        foreach (var ancestor in GetEmittableAncestors(type))
        {
            var match = ancestor.Properties.FirstOrDefault(p => p.Name == protoProperty.Name && !p.IsStatic);
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Finds matching subscript in concrete type or its emittable ancestors by signature.
    /// </summary>
    private SubscriptDecl? FindMatchingSubscript(TypeDecl type, SubscriptDecl protoSubscript, ProtocolDecl protocolContext)
    {
        var protoKey = ProtocolSignatureHelper.GetSubscriptSignatureKey(protoSubscript, _typeDatabase, protocolContext);

        foreach (var ancestor in GetEmittableAncestors(type))
        {
            var match = ancestor.Subscripts.FirstOrDefault(s =>
                !s.IsStatic &&
                ProtocolSignatureHelper.GetSubscriptSignatureKey(s, _typeDatabase, null) == protoKey);
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Finds matching method in concrete type or its emittable ancestors by signature.
    /// </summary>
    private MethodDecl? FindMatchingMethod(TypeDecl type, MethodDecl protoMethod, ProtocolDecl protocolContext)
    {
        var protoKey = ProtocolSignatureHelper.GetMethodSignatureKey(protoMethod, _typeDatabase, protocolContext);

        foreach (var ancestor in GetEmittableAncestors(type))
        {
            var match = ancestor.Methods.FirstOrDefault(m =>
                !m.IsConstructor && m.MethodType != MethodType.Static &&
                ProtocolSignatureHelper.GetMethodSignatureKey(m, _typeDatabase, null) == protoKey);
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Checks if interface and implementation types are compatible.
    /// </summary>
    private static bool AreTypesCompatible(string? interfaceType, string? implType)
    {
        if (interfaceType == null || implType == null) return false;
        // String comparison with normalization for now
        return NormalizeTypeName(interfaceType) == NormalizeTypeName(implType);
    }

    /// <summary>
    /// Checks if interface and implementation types are compatible, with TSelf awareness.
    /// TSelf anywhere in the interface type is substituted with the conforming type's C# name
    /// before comparison. This handles plain TSelf, TSelf?, Task&lt;TSelf&gt;,
    /// IReadOnlyList&lt;TSelf&gt;, Func&lt;TSelf, int&gt;, etc.
    /// </summary>
    private static bool AreTypesCompatible(string? interfaceType, string? implType, string? conformingTypeName)
    {
        if (interfaceType == null || implType == null) return false;
        var ni = NormalizeTypeName(interfaceType);
        var np = NormalizeTypeName(implType);
        // Substitute TSelf with the conforming type's projected name
        if (conformingTypeName != null && ni.Contains("TSelf"))
            ni = ni.Replace("TSelf", NormalizeTypeName(conformingTypeName));
        return ni == np;
    }

    private static string NormalizeTypeName(string typeName)
        => typeName.Replace(" ", "").Trim();

    /// <summary>
    /// Checks if a protocol property would be skipped from the interface.
    /// Delegates to MemberGateEvaluator for unified gate logic.
    /// </summary>
    private bool IsPropertySkippedFromInterface(PropertyDecl property, BoundGenericsHandler boundGenericsHandler, ProtocolDecl protocolDecl)
    {
        var evaluator = new MemberGateEvaluator(_typeDatabase);
        var result = evaluator.EvaluateProperty(property, _moduleDecl, protocolDecl);
        // InterfaceOnly (closure properties) → NOT skipped from interface (they ARE in the interface)
        return result.IsSkipped;
    }

    /// <summary>
    /// Checks if a protocol method would be skipped from the interface.
    /// Delegates to MemberGateEvaluator for unified gate logic.
    /// Does NOT skip closure methods (they are emitted in the interface with stubs).
    /// Does NOT skip existential methods (they are emitted in the interface with stubs).
    /// </summary>
    private bool IsMethodSkippedFromInterface(MethodDecl method, BoundGenericsHandler boundGenericsHandler, ProtocolDecl protocolDecl)
    {
        var evaluator = new MemberGateEvaluator(_typeDatabase);
        var result = evaluator.EvaluateMethod(method, _moduleDecl, protocolDecl);
        // InterfaceOnly (closure/existential methods) → NOT skipped from interface (they ARE in the interface)
        return result.IsSkipped;
    }
}
