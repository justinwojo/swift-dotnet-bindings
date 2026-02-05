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
