// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

public partial class ProtocolProxyEmitter
{
    /// <summary>
    /// Resolves a Swift type to its C# name. Uses factory-based projection for NamedTypeSpec
    /// with fallbacks for closures, existentials, tuples, and idiomatic conversion.
    /// When <paramref name="forAbiMarshalling"/> is true, returns the P/Invoke type
    /// (e.g., Swift.SwiftString, ExistentialContainer0) suitable for MarshalFromSwift&lt;T&gt;.
    /// When false (default), returns the idiomatic C# type (e.g., string, bool?)
    /// used in interface/implementation signatures.
    /// </summary>
    private string GetCSharpTypeName(TypeSpec? typeSpec, bool forAbiMarshalling = false, bool isParameter = true)
    {
        if (typeSpec == null) return "object";

        // Handle associated type references (e.g., Self.Element -> TElement)
        if (typeSpec is AssociatedTypeReferenceSpec associatedTypeRef)
            return $"T{associatedTypeRef.AssociatedTypeName}";

        // Closures: always use GetClosureCSharpType (factory can fail if inner types unknown)
        if (typeSpec is ClosureTypeSpec closureTypeSpec)
            return GetClosureCSharpType(closureTypeSpec);

        // Tuples: use GetTupleCSharpType (preserves element labels)
        if (typeSpec is TupleTypeSpec tupleTypeSpec)
        {
            if (tupleTypeSpec.IsEmptyTuple)
                return "void";
            return GetTupleCSharpType(tupleTypeSpec);
        }

        // Existentials: keep explicit handling (must match ProtocolHandler interface emission until 3d)
        var existentialHandler = new ExistentialHandler(_typeDatabase);
        if (existentialHandler.IsExistential(typeSpec))
        {
            var protocolList = existentialHandler.ToProtocolListTypeSpec(typeSpec);
            if (protocolList != null)
            {
                if (existentialHandler.TryGetWellKnownProtocolType(protocolList, out var wellKnownType))
                    return wellKnownType;

                if (existentialHandler.IsSupportedExistential(protocolList))
                {
                    return forAbiMarshalling
                        ? existentialHandler.GetCSharpExistentialType(protocolList)
                        : existentialHandler.GetPublicExistentialType(protocolList);
                }
            }
            // Unsupported existentials fall through to type database fallback
        }

        // Idiomatic type conversion (String → string, Array → IReadOnlyList, Dict → IReadOnlyDictionary,
        // Optional → T?, etc.). Must match what ProtocolHandler.cs emits for interface signatures.
        // P0 fix: skip for ABI marshalling — MarshalFromSwift<T> needs Swift wrapper types.
        if (!forAbiMarshalling && typeSpec is NamedTypeSpec)
        {
            var typeConversionHandler = new TypeConversionHandler(_typeDatabase);
            var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(typeSpec, isParameter: isParameter,
                ts => GetCSharpTypeName(ts));
            if (idiomaticType != null)
                return idiomaticType;
        }

        // Optional<existential> for well-known protocols (e.g., Optional<any Error> → AnyError?)
        if (!forAbiMarshalling && typeSpec is NamedTypeSpec optionalExistentialType &&
            optionalExistentialType.Name == "Swift.Optional" && optionalExistentialType.GenericParameters.Count == 1)
        {
            var innerTypeSpec = optionalExistentialType.GenericParameters[0];
            if (existentialHandler.IsExistential(innerTypeSpec))
            {
                var innerProtocolList = existentialHandler.ToProtocolListTypeSpec(innerTypeSpec);
                if (innerProtocolList != null && existentialHandler.TryGetWellKnownProtocolType(innerProtocolList, out var wkt))
                    return $"{wkt}?";
            }
        }

        // Bound generics and type record fallback
        try
        {
            if (typeSpec is NamedTypeSpec namedType && namedType.GenericParameters.Count > 0)
            {
                var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
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

            var record = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
            return record.CSharpTypeName.FullyQualifiedName;
        }
        catch
        {
            if (typeSpec is NamedTypeSpec { ContainsGenericParameters: true })
                return TypeDatabaseExtensions.AnyType.CSharpTypeName.FullyQualifiedName;
            if (typeSpec is NamedTypeSpec namedType2)
                return namedType2.NameWithoutModule;
            return "object";
        }
    }

    /// <summary>
    /// Resolves property types to match the emitted interface signatures from ProtocolHandler.
    /// Uses the same resolution rules so proxy signatures always match interface declarations.
    /// </summary>
    private string GetInterfaceCompatiblePropertyTypeName(PropertyDecl property)
    {
        var boundGenericsHandler = new BoundGenericsHandler(_typeDatabase);
        var rawType = boundGenericsHandler.IsBoundGeneric(property)
            ? boundGenericsHandler.TranslateBoundGenericTypeToCSharp(property)
            : _typeDatabase.GetTypeRecordOrAnyType(property.SwiftTypeSpec).CSharpTypeName.FullyQualifiedName;

        // Apply idiomatic type conversion to match interface declaration (SwiftString → string, etc.)
        var typeConversionHandler = new TypeConversionHandler(_typeDatabase);
        var idiomaticType = typeConversionHandler.GetIdiomaticCSharpType(
            property.SwiftTypeSpec,
            isParameter: false,
            typeSpec =>
            {
                var rec = _typeDatabase.GetTypeRecordOrAnyType(typeSpec);
                return rec.CSharpTypeName.FullyQualifiedName;
            });
        if (idiomaticType != null)
            return idiomaticType;
        if (typeConversionHandler.HasNativeTypeRemapping(property.SwiftTypeSpec))
        {
            var nativeType = typeConversionHandler.GetNativeTypeName(property.SwiftTypeSpec);
            if (nativeType != null)
                return nativeType;
        }
        return rawType;
    }

    /// <summary>
    /// Translates a Swift closure type to a C# delegate type (Action or Func).
    /// </summary>
    private string GetClosureCSharpType(ClosureTypeSpec closureTypeSpec)
    {
        // Build parameter types
        var paramTypes = new List<string>();
        foreach (var arg in closureTypeSpec.EachArgument())
        {
            paramTypes.Add(GetCSharpTypeName(arg));
        }

        // Get return type
        var returnType = closureTypeSpec.ReturnType;
        bool hasReturn = !returnType.IsEmptyTuple;

        if (!hasReturn)
        {
            // Action delegate
            if (paramTypes.Count == 0)
                return "Action";
            return $"Action<{string.Join(", ", paramTypes)}>";
        }
        else
        {
            // Func delegate
            var returnTypeName = GetCSharpTypeName(returnType);
            if (paramTypes.Count == 0)
                return $"Func<{returnTypeName}>";
            return $"Func<{string.Join(", ", paramTypes)}, {returnTypeName}>";
        }
    }

    /// <summary>
    /// Translates a Swift tuple type to a C# ValueTuple type.
    /// </summary>
    private string GetTupleCSharpType(TupleTypeSpec tupleTypeSpec)
    {
        var elements = new List<string>();

        foreach (var element in tupleTypeSpec.Elements)
        {
            var typeName = GetCSharpTypeName(element);

            // Include label if present
            if (!string.IsNullOrEmpty(element.TypeLabel))
            {
                elements.Add($"{typeName} {element.TypeLabel}");
            }
            else
            {
                elements.Add(typeName);
            }
        }

        return $"({string.Join(", ", elements)})";
    }

    /// <summary>
    /// Checks if a projected C# type name represents SwiftString.
    /// This validates that the TypeDatabase properly resolved Swift.String
    /// rather than falling back to Swift.AnyType.
    /// </summary>
    private static bool IsSwiftStringProjectedType(string csharpTypeName)
    {
        // Swift.String projects to Swift.SwiftString or idiomatic string via TypeConversionHandler
        return csharpTypeName == "Swift.SwiftString"
            || csharpTypeName == "SwiftString"
            || csharpTypeName == "Swift.Runtime.SwiftString"
            || csharpTypeName == "string";
    }

    /// <summary>
    /// Checks if a projected C# type name represents an idiomatic string type
    /// (used for method params/returns where TypeConversionHandler applies).
    /// </summary>
    private static bool IsIdiomaticStringType(string csharpTypeName)
    {
        return csharpTypeName == "string" || csharpTypeName == "System.String";
    }

    private static string GetProxyClassName(ProtocolDecl protocolDecl)
    {
        return $"{protocolDecl.Name}Proxy";
    }

    /// <summary>
    /// Gets the proxy class name with generic type parameters for protocols with associated types.
    /// </summary>
    private static string GetProxyClassNameWithGenerics(ProtocolDecl protocolDecl)
    {
        var baseName = GetProxyClassName(protocolDecl);

        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            var typeParams = protocolDecl.AssociatedTypes.Select(at => $"T{at.Name}");
            return $"{baseName}<{string.Join(", ", typeParams)}>";
        }

        return baseName;
    }

    /// <summary>
    /// Gets the interface name with generic type parameters for protocols with associated types.
    /// </summary>
    private static string GetInterfaceNameWithGenerics(ProtocolDecl protocolDecl)
    {
        var baseName = NameProvider.GetInterfaceName(protocolDecl.Name, moduleName: protocolDecl.ModuleDecl?.Name ?? "");

        if (protocolDecl.AssociatedTypes.Count > 0)
        {
            var typeParams = protocolDecl.AssociatedTypes.Select(at => $"T{at.Name}");
            return $"{baseName}<{string.Join(", ", typeParams)}>";
        }

        return baseName;
    }

    /// <summary>
    /// Gets the generic constraints for proxy classes with associated types.
    /// Each associated type parameter is constrained to ISwiftObject.
    /// </summary>
    private static string GetProxyClassConstraints(ProtocolDecl protocolDecl)
    {
        if (protocolDecl.AssociatedTypes.Count == 0)
            return "";

        var constraints = protocolDecl.AssociatedTypes
            .Select(at => $"\n    where T{at.Name} : ISwiftObject");
        return string.Join("", constraints);
    }

    private static string GetSwiftVtableStructName(ProtocolDecl protocolDecl)
    {
        return $"{protocolDecl.Name}SwiftVTable";
    }

    private static string GetLocalVtableStructName(ProtocolDecl protocolDecl)
    {
        return $"{protocolDecl.Name}LocalVTable";
    }

    private static string GetSetVtablePInvokeName(ProtocolDecl protocolDecl)
    {
        return $"Set{protocolDecl.Name}_vtable";
    }

    private static string GetWitnessTableSymbol(ProtocolDecl protocolDecl)
    {
        // This would be the mangled symbol for the witness table
        // The format is: $s<module><type>AA<protocol>WT
        return $"EveryProtocol_{protocolDecl.Name}_WT";
    }

    internal static string GetMethodKey(MethodDecl method)
    {
        return method.Name + "(" + string.Join(",", method.CSSignature.Skip(1).Select(p => p.SwiftTypeSpec?.ToString() ?? "")) + ")";
    }
}
