// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Shared signature key generation for protocol member matching.
/// Used by both ProtocolHandler (interface emission) and ProtocolConformanceValidator.
/// </summary>
internal static class ProtocolSignatureHelper
{
    /// <summary>
    /// Creates a unique signature key for a method based on name and parameter types.
    /// </summary>
    public static string GetMethodSignatureKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
    {
        var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
        var paramTypes = new List<string>();
        // Skip first element (return type) in CSSignature
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            try
            {
                // Handle associated type references for protocols
                if (arg.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                {
                    paramTypes.Add(MapAssociatedTypeToGenericParam(assocRef, protocolContext));
                }
                else
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(arg.SwiftTypeSpec);
                    paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
                }
            }
            catch
            {
                // For generic type parameters or other unsupported types,
                // use the string representation of the type spec
                paramTypes.Add(arg.SwiftTypeSpec?.ToString() ?? "unknown");
            }
        }
        return $"{methodDecl.Name}({string.Join(",", paramTypes)})";
    }

    /// <summary>
    /// Creates a unique signature key for a subscript based on index parameter types.
    /// </summary>
    public static string GetSubscriptSignatureKey(SubscriptDecl subscriptDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
    {
        var paramTypes = new List<string>();
        foreach (var param in subscriptDecl.IndexParameters)
        {
            try
            {
                // Handle associated type references for protocols
                if (param.SwiftTypeSpec is AssociatedTypeReferenceSpec assocRef)
                {
                    paramTypes.Add(MapAssociatedTypeToGenericParam(assocRef, protocolContext));
                }
                else if (param.SwiftTypeSpec != null)
                {
                    var typeRecord = typeDatabase.GetTypeRecordOrAnyType(param.SwiftTypeSpec);
                    paramTypes.Add(typeRecord.CSharpTypeName.FullyQualifiedName);
                }
                else
                {
                    paramTypes.Add("unknown");
                }
            }
            catch
            {
                // For generic type parameters or other unsupported types,
                // use the string representation of the type spec
                paramTypes.Add(param.SwiftTypeSpec?.ToString() ?? "unknown");
            }
        }
        return $"subscript[{string.Join(",", paramTypes)}]";
    }

    /// <summary>
    /// Creates a projected C# method signature key for dedup purposes.
    /// Two methods that would produce the same C# interface signature get the same key.
    /// Key format: "MethodName(paramType1,paramType2,...)" — no return type (C# overload identity).
    /// </summary>
    public static string GetProjectedCSharpMethodKey(MethodDecl methodDecl, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null)
    {
        // Compute the public method name the same way EmitInterfaceMethod does
        var returnTypeSpec = methodDecl.CSSignature.FirstOrDefault()?.SwiftTypeSpec;
        bool hasReturnValue = returnTypeSpec != null && !returnTypeSpec.IsEmptyTuple;
        // Capture hasReturnValue BEFORE async conversion turns void→Task
        var methodName = NameProvider.GetPublicMethodName(methodDecl.Name, methodDecl.IsAsync, hasReturnValue: hasReturnValue);

        var paramTypes = new List<string>();
        for (int i = 1; i < methodDecl.CSSignature.Count; i++)
        {
            var arg = methodDecl.CSSignature[i];
            var projected = ProjectTypeToCSharp(arg.SwiftTypeSpec, typeDatabase, protocolContext, isParameter: true);

            // Normalize nullable reference types for C# overload identity.
            // In C#, nullability annotations don't affect overload resolution for reference types —
            // only value types produce distinct Nullable<T> overloads.
            // If the Swift type is Optional<T> where T is a reference type (class/protocol),
            // strip the trailing '?' so T and T? produce the same overload key.
            if (arg.SwiftTypeSpec is NamedTypeSpec optNamed && optNamed.Name == "Swift.Optional"
                && optNamed.GenericParameters.Count == 1)
            {
                var innerRecord = typeDatabase.GetTypeRecordOrAnyType(optNamed.GenericParameters[0]);
                if (innerRecord.Kind != TypeRecordKind.Struct && innerRecord.Kind != TypeRecordKind.Enum)
                    projected = projected.TrimEnd('?');
            }

            paramTypes.Add(projected);
        }
        return $"{methodName}({string.Join(",", paramTypes)})";
    }

    /// <summary>
    /// Projects a Swift TypeSpec to the C# type name that would appear in a protocol interface.
    /// Mirrors ProtocolHandler.GetCSharpTypeName() resolution chain.
    /// </summary>
    /// <param name="typeSpec">The Swift type specification.</param>
    /// <param name="typeDatabase">Type database for lookups.</param>
    /// <param name="protocolContext">Protocol context for associated type resolution.</param>
    /// <param name="isParameter">True for parameter types (arrays → IEnumerable), false for return types (arrays → IReadOnlyList).</param>
    public static string ProjectTypeToCSharp(TypeSpec typeSpec, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext = null, bool isParameter = false)
    {
        // Associated type references → generic param
        if (typeSpec is AssociatedTypeReferenceSpec assocRef)
            return MapAssociatedTypeToGenericParam(assocRef, protocolContext);

        var existentialHandler = new ExistentialHandler(typeDatabase);

        // Existential types (any Protocol)
        if (existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null && existentialHandler.IsSupportedExistential(protocolList))
                return existentialHandler.GetPublicExistentialType(protocolList);
        }

        // Optional-wrapped existential
        if (existentialHandler.IsOptionalExistential(typeSpec))
        {
            var innerProtocolList = existentialHandler.UnwrapOptionalExistential(typeSpec);
            if (innerProtocolList != null && existentialHandler.IsSupportedExistential(innerProtocolList))
            {
                var publicInnerType = existentialHandler.GetPublicExistentialType(innerProtocolList);
                if (publicInnerType != "object")
                    return existentialHandler.GetPublicOptionalExistentialType(innerProtocolList);
            }
        }

        // Closures → Action/Func
        if (typeSpec is ClosureTypeSpec closureTypeSpec)
            return ProjectClosureToCSharp(closureTypeSpec, typeDatabase, protocolContext, isParameter);

        // Tuples → ValueTuple
        if (typeSpec is TupleTypeSpec tupleTypeSpec && !tupleTypeSpec.IsEmptyTuple)
            return ProjectTupleToCSharp(tupleTypeSpec, typeDatabase, protocolContext, isParameter);

        // Bound generics (Optional<T>, Array<T>, etc.)
        if (typeSpec is NamedTypeSpec namedTypeSpec && namedTypeSpec.ContainsGenericParameters)
        {
            // Try idiomatic conversion first (SwiftString→string, SwiftArray→IEnumerable/IReadOnlyList, etc.)
            var typeConversion = new TypeConversionHandler(typeDatabase);
            var idiomaticType = typeConversion.GetIdiomaticCSharpType(typeSpec, isParameter: isParameter);
            if (idiomaticType != null)
                return idiomaticType;

            // Try BoundGenericsHandler for recognized bound generics (Optional<T>, Array<T>, etc.)
            // Falls back to raw type lookup for unrecognized generics (SwiftDictionary<K,V>, etc.)
            var boundGenericsHandler = new BoundGenericsHandler(typeDatabase);
            try
            {
                var tempProperty = new PropertyDecl
                {
                    Name = "_temp",
                    SwiftTypeSpec = typeSpec,
                    IsStatic = false,
                    HasStorage = false,
                    Accessors = new List<AccessorDecl>(),
                    ParentDecl = null,
                    ModuleDecl = null
                };
                return boundGenericsHandler.TranslateBoundGenericTypeToCSharp(tempProperty);
            }
            catch (NotSupportedException)
            {
                // Unrecognized bound generic (e.g., SwiftDictionary<K,V>) — return AnyType
                // to avoid bare type name without generic args (CS0305)
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            }
        }

        // Standard named type lookup with idiomatic + native remapping
        {
            var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            var rawType = record.CSharpTypeName.FullyQualifiedName;
            var typeConversion = new TypeConversionHandler(typeDatabase);
            var idiomaticType = typeConversion.GetIdiomaticCSharpType(typeSpec, isParameter: isParameter);
            if (idiomaticType != null)
                return idiomaticType;
            // Native type remapping (Foundation.URL → NSUrl, Foundation.Data → NSData)
            if (typeConversion.HasNativeTypeRemapping(typeSpec))
            {
                var nativeType = typeConversion.GetNativeTypeName(typeSpec);
                if (nativeType != null)
                    return nativeType;
            }
            return rawType;
        }
    }

    private static string ProjectClosureToCSharp(ClosureTypeSpec closureTypeSpec, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext, bool isParameter = false)
    {
        var paramTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
            paramTypes.Add(ProjectTypeToCSharp(arg, typeDatabase, protocolContext, isParameter));

        var returnType = closureTypeSpec.ReturnType;
        bool hasReturn = !returnType.IsEmptyTuple;

        if (!hasReturn)
        {
            if (paramTypes.Count == 0) return "Action";
            return $"Action<{string.Join(", ", paramTypes)}>";
        }
        else
        {
            var returnTypeName = ProjectTypeToCSharp(returnType, typeDatabase, protocolContext, isParameter);
            if (paramTypes.Count == 0) return $"Func<{returnTypeName}>";
            return $"Func<{string.Join(", ", paramTypes)}, {returnTypeName}>";
        }
    }

    private static string ProjectTupleToCSharp(TupleTypeSpec tupleTypeSpec, ITypeDatabase typeDatabase, ProtocolDecl? protocolContext, bool isParameter = false)
    {
        var elements = new List<string>();
        foreach (var element in tupleTypeSpec.Elements)
        {
            var typeName = ProjectTypeToCSharp(element, typeDatabase, protocolContext, isParameter);
            if (!string.IsNullOrEmpty(element.TypeLabel))
                elements.Add($"{typeName} {element.TypeLabel}");
            else
                elements.Add(typeName);
        }
        return $"({string.Join(", ", elements)})";
    }

    /// <summary>
    /// Maps an associated type reference to a C# generic parameter name.
    /// For example, "Self.Element" in a protocol with associated type "Element" becomes "TElement".
    /// </summary>
    internal static string MapAssociatedTypeToGenericParam(AssociatedTypeReferenceSpec assocRef, ProtocolDecl? protocolDecl)
    {
        // Handle Self reference
        if (assocRef.BaseType == "Self" && string.IsNullOrEmpty(assocRef.AssociatedTypeName))
        {
            return "TSelf";
        }

        // Handle associated type reference like "Self.Element"
        if (!string.IsNullOrEmpty(assocRef.AssociatedTypeName))
        {
            // Map "Element" -> "TElement"
            return $"T{assocRef.AssociatedTypeName}";
        }

        // Fallback for generic parameter like τ_0_0
        if (assocRef.BaseType.StartsWith("τ_") || assocRef.BaseType.StartsWith("T"))
        {
            // Already a generic param reference
            return assocRef.BaseType;
        }

        return "object";
    }
}
