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

        // A7: If the protocol interface has unemittable members (AnyType fallback),
        // concrete types can't implement it — skip conformance entirely.
        if (HasUnemittableInterfaceMembers(protocolDecl))
            return false;

        // Track interface requirements (mirrors ProtocolHandler dedup)
        var requiredProperties = new HashSet<string>();
        var requiredSubscripts = new HashSet<string>();
        var requiredMethods = new HashSet<string>();

        // For each INTERFACE PROPERTY requirement:
        foreach (var protoProperty in protocolDecl.Properties)
        {
            if (protoProperty.IsStatic) continue;
            var propertyKey = protoProperty.Name;
            if (!requiredProperties.Add(propertyKey)) continue;

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
            var interfaceType = GetInterfacePropertyType(protoProperty, protocolDecl);
            if (!AreTypesCompatible(interfaceType, concreteTypeProjected))
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
            var interfaceReturnType = GetInterfaceSubscriptReturnType(protoSubscript, protocolDecl);
            if (!AreTypesCompatible(interfaceReturnType, concreteReturnType))
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

            // Check return type compatibility (CS0738)
            var interfaceReturnType = GetInterfaceMethodReturnType(protoMethod, protocolDecl);
            if (!AreTypesCompatible(interfaceReturnType, concreteReturnType))
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
    private string GetInterfacePropertyType(PropertyDecl protoProperty, ProtocolDecl protocolContext)
    {
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        var typeConversionHandler = new TypeConversionHandler(_typeDatabase);

        string rawType;
        if (protoProperty.SwiftTypeSpec is AssociatedTypeReferenceSpec)
            return "?";  // PAT - should have been filtered earlier
        else if (boundGenericsHandler.IsBoundGeneric(protoProperty))
            rawType = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(protoProperty);
        else
            rawType = _typeDatabase.GetTypeRecordOrAnyType(protoProperty.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;

        // Apply idiomatic type conversion to match PropertyHandler behavior
        var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(
            protoProperty.SwiftTypeSpec,
            isParameter: false,
            typeSpec =>
            {
                var rec = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
                return rec.CSharpTypeName.FullyQualifiedName;
            });
        if (idiomaticType != null)
            return idiomaticType;
        if (typeConversionHandler.HasNativeTypeRemapping(protoProperty.SwiftTypeSpec))
        {
            var nativeType = typeConversionHandler.GetNativeTypeName(protoProperty.SwiftTypeSpec);
            if (nativeType != null)
                return nativeType;
        }
        return rawType;
    }

    /// <summary>
    /// Gets interface method return type using SAME projection as ProtocolHandler.EmitInterfaceMethod.
    /// </summary>
    private string GetInterfaceMethodReturnType(MethodDecl protoMethod, ProtocolDecl protocolContext)
    {
        var typeConversionHandler = new TypeConversionHandler(_typeDatabase);
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        var returnType = "void";

        if (protoMethod.CSSignature.Count > 0)
        {
            var returnArg = protoMethod.CSSignature[0];
            if (returnArg.SwiftTypeSpec is not TupleTypeSpec tuple || !tuple.IsEmptyTuple)
            {
                var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(returnArg.SwiftTypeSpec, isParameter: false);
                if (idiomaticType != null)
                {
                    returnType = idiomaticType;
                }
                else if (typeConversionHandler.HasNativeTypeRemapping(returnArg.SwiftTypeSpec))
                {
                    // Apply native type remapping (Foundation.URL -> NSUrl, Foundation.Data -> NSData)
                    returnType = typeConversionHandler.GetNativeTypeName(returnArg.SwiftTypeSpec)
                        ?? _typeDatabase.GetTypeRecordOrAnyType(returnArg.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;
                }
                else if (returnArg.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                {
                    returnType = ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
                }
                else if (boundGenericsHandler.IsBoundGeneric(returnArg))
                {
                    var tempProperty = new PropertyDecl
                    {
                        Name = "_temp",
                        SwiftTypeSpec = returnArg.SwiftTypeSpec,
                        IsStatic = false,
                        HasStorage = false,
                        Accessors = new List<AccessorDecl>(),
                        ParentDecl = null,
                        ModuleDecl = null
                    };
                    returnType = boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
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
        var methodName = NameProvider.GetPublicMethodName(protoMethod.Name, protoMethod.IsAsync, hasReturnValue: hasReturnValue);

        var parameterTypes = new List<string>();
        for (int i = 1; i < protoMethod.CSSignature.Count; i++)
        {
            var arg = protoMethod.CSSignature[i];
            var projected = ResolveInterfaceMethodTypeName(arg.SwiftTypeSpec, isParameter: true, protocolContext);
            parameterTypes.Add(ProtocolSignatureHelper.NormalizeParamTypeForOverloadIdentity(projected, arg.SwiftTypeSpec, _typeDatabase));
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
    private string GetInterfaceSubscriptReturnType(SubscriptDecl protoSubscript, ProtocolDecl protocolContext)
    {
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);

        if (protoSubscript.ReturnTypeSpec is AssociatedTypeReferenceSpec assocRef)
        {
            return ProtocolSignatureHelper.MapAssociatedTypeToGenericParam(assocRef, protocolContext);
        }
        else if (protoSubscript.ReturnTypeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
        {
            var tempProperty = new PropertyDecl
            {
                Name = "_temp",
                SwiftTypeSpec = protoSubscript.ReturnTypeSpec,
                IsStatic = false,
                HasStorage = false,
                Accessors = new List<AccessorDecl>(),
                ParentDecl = null,
                ModuleDecl = null
            };
            return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
        }
        else
        {
            return _typeDatabase.GetTypeRecordOrAnyType(protoSubscript.ReturnTypeSpec).CSharpTypeName.FullyQualifiedName;
        }
    }

    /// <summary>
    /// Finds matching property in concrete type by name.
    /// </summary>
    private static PropertyDecl? FindMatchingProperty(TypeDecl type, PropertyDecl protoProperty)
    {
        return type.Properties.FirstOrDefault(p => p.Name == protoProperty.Name && !p.IsStatic);
    }

    /// <summary>
    /// Finds matching subscript in concrete type by signature.
    /// </summary>
    private SubscriptDecl? FindMatchingSubscript(TypeDecl type, SubscriptDecl protoSubscript, ProtocolDecl protocolContext)
    {
        var protoKey = ProtocolSignatureHelper.GetSubscriptSignatureKey(protoSubscript, _typeDatabase, protocolContext);

        return type.Subscripts.FirstOrDefault(s =>
            !s.IsStatic &&
            ProtocolSignatureHelper.GetSubscriptSignatureKey(s, _typeDatabase, null) == protoKey);
    }

    /// <summary>
    /// Finds matching method in concrete type by signature.
    /// </summary>
    private MethodDecl? FindMatchingMethod(TypeDecl type, MethodDecl protoMethod, ProtocolDecl protocolContext)
    {
        var protoKey = ProtocolSignatureHelper.GetMethodSignatureKey(protoMethod, _typeDatabase, protocolContext);

        return type.Methods.FirstOrDefault(m =>
            !m.IsConstructor && m.MethodType != MethodType.Static &&
            ProtocolSignatureHelper.GetMethodSignatureKey(m, _typeDatabase, null) == protoKey);
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

    private static string NormalizeTypeName(string typeName)
        => typeName.Replace(" ", "").Trim();

    /// <summary>
    /// Checks if a protocol has interface members that would project to AnyType,
    /// making it impossible for concrete types to implement the interface.
    /// </summary>
    private bool HasUnemittableInterfaceMembers(ProtocolDecl protocolDecl)
    {
        var closureHandler = new ClosureHandler(_typeDatabase);

        foreach (var method in protocolDecl.Methods)
        {
            if (method.IsConstructor || method.MethodType == MethodType.Static) continue;

            foreach (var arg in method.CSSignature)
            {
                // Check via TryFindFallbackInfo (catches complex fallbacks)
                if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(_typeDatabase, closureHandler, arg.SwiftTypeSpec, out _))
                    return true;

                // Check via projection (catches generic params → AnyType, including nested:
                // Action<AnyType>, Func<AnyType, ...>, tuples with AnyType, etc.)
                var projected = ProtocolSignatureHelper.ProjectTypeToCSharp(arg.SwiftTypeSpec, _typeDatabase, protocolDecl);
                if (ContainsAnyTypeFallback(projected))
                    return true;
            }
        }

        foreach (var property in protocolDecl.Properties)
        {
            if (property.IsStatic) continue;

            if (UnsupportedSwiftTypeSupport.TryFindFallbackInfo(_typeDatabase, closureHandler, property.SwiftTypeSpec, out _))
                return true;

            var projected = ProtocolSignatureHelper.ProjectTypeToCSharp(property.SwiftTypeSpec, _typeDatabase, protocolDecl);
            if (ContainsAnyTypeFallback(projected))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a projected C# type name contains an AnyType fallback, either at the top level
    /// or nested inside generic wrappers (Action&lt;AnyType&gt;, Func&lt;AnyType, ...&gt;, tuples, etc.).
    /// Uses word-boundary matching to avoid false positives on user types containing "AnyType" as a substring.
    /// </summary>
    private static bool ContainsAnyTypeFallback(string projected)
    {
        // Fast path: exact match (most common case)
        if (projected is "AnyType" or "Swift.AnyType")
            return true;

        // Check for AnyType nested inside generic types (Action<AnyType>, Func<AnyType, int>, etc.)
        // Word boundary: AnyType must be preceded/followed by non-word chars or string boundaries
        if (projected.Contains("AnyType"))
        {
            return System.Text.RegularExpressions.Regex.IsMatch(projected, @"\bAnyType\b");
        }

        return false;
    }
}
