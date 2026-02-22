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
            projected = NormalizeParamTypeForOverloadIdentity(projected, arg.SwiftTypeSpec, typeDatabase);
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
        // Associated type references → generic param (factory doesn't handle these)
        if (typeSpec is AssociatedTypeReferenceSpec assocRef)
            return MapAssociatedTypeToGenericParam(assocRef, protocolContext);

        // Factory-first: handles existentials, closures, tuples, containers (Array, Dict, Optional),
        // string, bool, ObjC bridged, simple enum, native remapped, class, non-frozen, blittable
        var factory = new TypeProjectionFactory();
        var projection = factory.Project(typeSpec, new ProjectionContext
        {
            TypeDatabase = typeDatabase,
            IsParameter = isParameter,
            GenericContext = GenericContext.Empty
        });
        if (projection != null)
            return projection.PublicType;

        // Closure fallback when factory can't fully resolve (e.g., inner types not in TypeDatabase)
        if (typeSpec is ClosureTypeSpec closureType)
        {
            var args = closureType.EachArgument()
                .Select(a => ProjectTypeToCSharp(a, typeDatabase, protocolContext, isParameter: true))
                .ToList();
            bool hasReturn = !closureType.ReturnType.IsEmptyTuple;

            if (!hasReturn)
            {
                return args.Count == 0 ? "Action" : $"Action<{string.Join(", ", args)}>";
            }
            else
            {
                var retName = ProjectTypeToCSharp(closureType.ReturnType, typeDatabase, protocolContext, isParameter: false);
                return args.Count == 0 ? $"Func<{retName}>" : $"Func<{string.Join(", ", args)}, {retName}>";
            }
        }

        // Tuple fallback
        if (typeSpec is TupleTypeSpec tupleType)
        {
            if (tupleType.IsEmptyTuple) return "void";
            var elements = tupleType.Elements
                .Select(e => ProjectTypeToCSharp(e, typeDatabase, protocolContext, isParameter))
                .ToList();
            return $"({string.Join(", ", elements)})";
        }

        // Bound generic fallback: produce full type name with generic args
        // (e.g., BatchedCollection<Swift.AnyType> for unknown inner types).
        if (typeSpec is NamedTypeSpec boundGeneric && boundGeneric.ContainsGenericParameters)
        {
            var bgh = new BoundGenericsHandler(typeDatabase);
            return bgh.TranslateBoundGenericTypeToCSharp(typeSpec, GenericContext.Empty);
        }

        // Final fallback: raw type record lookup
        var record = typeDatabase.GetTypeRecordOrAnyType(typeSpec);
        return record.CSharpTypeName.FullyQualifiedName;
    }

    /// <summary>
    /// Normalizes a projected C# parameter type for overload identity comparison.
    /// In C#, nullability annotations don't affect overload resolution for reference types —
    /// Optional&lt;Class&gt; and Class resolve to the same overload. This strips the trailing '?'
    /// for reference-like types so that emission dedup correctly detects collisions.
    /// </summary>
    public static string NormalizeParamTypeForOverloadIdentity(string projectedType, TypeSpec swiftTypeSpec, ITypeDatabase typeDatabase)
    {
        if (swiftTypeSpec is NamedTypeSpec optNamed && optNamed.Name == "Swift.Optional" &&
            optNamed.GenericParameters.Count == 1)
        {
            var innerRecord = typeDatabase.GetTypeRecordOrAnyType(optNamed.GenericParameters[0]);
            if (innerRecord.Kind == TypeRecordKind.Class ||
                innerRecord.Kind == TypeRecordKind.Protocol ||
                innerRecord.Kind == TypeRecordKind.Existential ||
                (innerRecord.Kind == TypeRecordKind.Enum && !innerRecord.Flags.HasFlag(TypeRecordFlags.SimpleEnum)))
                return projectedType.TrimEnd('?');

            // Swift value types that project to C# reference types (e.g., Swift.String → string).
            // In C#, Optional<String> and String both map to 'string' / 'string?' which are the
            // same CLR type (nullability is annotation-only for reference types).
            if (projectedType.EndsWith("?") && IsCSharpReferenceTypeProjection(projectedType.TrimEnd('?')))
                return projectedType.TrimEnd('?');
        }

        return projectedType;
    }

    /// <summary>
    /// Checks if a projected C# type name is a reference type in the CLR,
    /// where nullability is annotation-only and doesn't affect overload resolution.
    /// </summary>
    private static bool IsCSharpReferenceTypeProjection(string projectedType) =>
        projectedType is "string" or "object";

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
